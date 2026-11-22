using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Cli.Update;

namespace MessageBus.Cli.Runner.Service;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceCommands
{
    internal static async Task<int> InstallAsync(CliHost host, string? account, string? server, bool print, CancellationToken ct)
    {
        var plan = WindowsServicePlan.Create(account);
        var paths = WindowsServicePaths.ForMachine();
        if (print)
        {
            await host.Out.WriteLineAsync($"Register and start the delayed automatic Windows service {WindowsServicePlan.Name} as {plan.Account}.");
            await host.Out.WriteLineAsync($"Grant its service SID read access to the signed NuGet dispatcher under {paths.HostTools}.");
            await host.Out.WriteLineAsync($"Store the worker, keys, credentials, and runs under {paths.State}.");
            await host.Out.WriteLineAsync($"Keep only {WindowsServicePlan.RequiredPrivilege} and restart after failure at 15 and 60 seconds.");
            return 0;
        }
        WindowsServicePaths.DemandAdministrator();
        DemandOutsideRun(host);
        var (selected, accountSid) = plan.Account == WindowsServicePlan.VirtualAccount
            ? (plan.Account, WindowsServicePaths.ServiceSid.Value) : ValidateManagedAccount(plan.Account);
        var version = DotnetToolInstall.Running?.Package?.Version;
        if (version is null || !DotnetToolUpdater.IsReleaseVersion(version))
            throw new CliException("Install the published mbus .NET tool before installing the Windows runner service.");
        var enrollment = WindowsServiceEnrollment.Validate(server ?? host.GetEnvironmentVariable(EnvVars.Server),
            ReadSecret(host, EnvVars.ApiKey));
        using var manager = new WindowsServiceScm(canCreate: true);
        using var existing = manager.Find();
        if (existing is not null || WindowsServicePaths.Exists(paths.Root) || WindowsServicePaths.Exists(paths.State))
            throw new CliException("An mbus-runner service or service directory already exists. Use mbus runner uninstall --service to remove an owned installation first.");
        paths.VerifyDotnet();
        paths.CreateRoot();
        var startAttempted = false;
        var stateCreated = false;
        try
        {
            var assembly = await WindowsServicePackage.StageHostAsync(paths, version, host, ct);
            var registration = new WindowsServiceRegistration(selected, accountSid, version, assembly, paths.HostCommandLine(assembly), Guid.NewGuid());
            paths.SaveRegistration(registration);
            WindowsServicePaths.OwnPayload(paths.Root);
            paths.CreateState();
            stateCreated = true;
            _ = paths.ReadRegistration();
            using var service = manager.Register(registration);
            startAttempted = true;
            await WindowsServiceScm.StartAsync(service, host.Time, ct);
            await WindowsServiceControl.SendAsync(registration.ControlPipe, service, new("enroll", enrollment), host.Time, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CliException or Win32Exception)
        {
            if (!startAttempted) RemoveEmptyInstallation(paths, stateCreated);
            else
                throw new CliException($"{ex.Message} The service may have started. Remove it with mbus runner uninstall --service before retrying.");
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (startAttempted)
                await host.Error.WriteLineAsync("Service startup was requested. Run mbus runner uninstall --service to remove the installation if needed.");
            else RemoveEmptyInstallation(paths, stateCreated);
            throw;
        }
        await host.Out.WriteLineAsync($"Started the Windows runner service as {selected}. Enrollment and worker startup continue in the service.");
        await host.Out.WriteLineAsync($"Set agent tokens with mbus runner env --save --service. Check progress with mbus doctor --service and logs under {Path.Combine(paths.State, "logs")}.");
        return 0;
    }

    internal static async Task<int> UninstallAsync(CliHost host, bool print, CancellationToken ct)
    {
        var paths = WindowsServicePaths.ForMachine();
        if (print)
        {
            await host.Out.WriteLineAsync($"Drain and delete the owned {WindowsServicePlan.Name} Windows service, its installation under {paths.Root}, and its state under {paths.State}.");
            return 0;
        }
        WindowsServicePaths.DemandAdministrator();
        DemandOutsideRun(host);
        using var manager = new WindowsServiceScm();
        if (!WindowsServicePaths.Exists(paths.Root))
        {
            using var existing = manager.Find();
            if (existing is not null) throw new CliException("The registered mbus-runner service has no matching protected installation.");
            await host.Out.WriteLineAsync("The Windows runner service is not installed.");
            return 0;
        }
        var registration = paths.ReadRegistration();
        using (var held = new FileStream(Path.Combine(paths.Root, "management.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var present = manager.Find();
            if (present is not null)
            {
                using var service = manager.OpenOwned(registration, manage: true);
                using (new FileStream(paths.MaintenancePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
                WindowsServiceScm.AllowMaintenanceStart(service);
                await WindowsServiceScm.StartAsync(service, host.Time, ct);
                await WindowsServiceControl.SendAsync(registration.ControlPipe, service, new("remove"), host.Time, ct);
                WindowsServiceScm.Disable(service);
                await WindowsServiceScm.StopAsync(service, host.Time, ct);
                WindowsServiceScm.Remove(service);
            }
            if (WindowsServicePaths.Exists(paths.State))
            {
                paths.VerifyState();
                Directory.Delete(paths.State); // Refuse a writable tree that the service did not empty.
            }
        }
        paths.VerifyRoot();
        Directory.Delete(paths.Root, recursive: true);
        await host.Out.WriteLineAsync("Removed the Windows runner service and its local keys, tokens, and state.");
        return 0;
    }

    private static void RemoveEmptyInstallation(WindowsServicePaths paths, bool stateCreated)
    {
        paths.VerifyRoot();
        if (stateCreated)
        {
            paths.VerifyState();
            Directory.Delete(paths.State);
        }
        Directory.Delete(paths.Root, recursive: true);
    }

    internal static async Task<int> EnvironmentAsync(CliHost host, bool save, bool clear, CancellationToken ct)
    {
        WindowsServicePaths.DemandAdministrator();
        DemandOutsideRun(host);
        var tokens = save ? AgentEnvironmentStore.Names.Select(name => (Name: name, Value: ReadSecret(host, name)))
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Name, pair => pair.Value!, StringComparer.Ordinal) : null;
        if (tokens is { Count: 0 }) throw new CliException("Set an agent token or an agent token's _FILE variable before running mbus runner env --save --service.");
        var (_, result) = await RequestAsync(host, new(save ? "save" : clear ? "clear" : "status", Tokens: tokens), ct);
        await host.Out.WriteLineAsync(result.TokenNames is { Length: > 0 } names
            ? "Windows runner service agent tokens: " + string.Join(", ", names)
            : "The Windows runner service has no stored agent tokens.");
        return 0;
    }

    internal static async Task<int> DoctorAsync(CliHost host, CancellationToken ct)
    {
        WindowsServicePaths.DemandAdministrator();
        DemandOutsideRun(host);
        var (registration, result) = await RequestAsync(host, new("status"), ct);
        await host.Out.WriteLineAsync($"Windows runner service is running as {registration.Account} in a separate Windows account.");
        await host.Out.WriteLineAsync(Runner.Sandbox.RunIsolation.WindowsServiceLimit);
        await host.Out.WriteLineAsync(result.Enrolled ? "Runner identity enrolled." : "Runner identity is awaiting enrollment.");
        var anyToken = false;
        foreach (var kind in new[] { Contracts.Machines.AgentKind.ClaudeCode, Contracts.Machines.AgentKind.Codex, Contracts.Machines.AgentKind.Copilot, Contracts.Machines.AgentKind.Antigravity })
        {
            var hasToken = ServiceAgentTokens.For(kind).Any((result.TokenNames ?? []).Contains);
            await host.Out.WriteLineAsync($"{Agents.AgentKinds.DisplayName(kind)}: {(hasToken ? "token configured" : "no token configured")}");
            anyToken |= hasToken;
        }
        return result.Enrolled && anyToken ? 0 : 1;
    }

    private static async Task<(WindowsServiceRegistration Registration, ServiceResponse Response)> RequestAsync(CliHost host, ServiceRequest request, CancellationToken ct)
    {
        var paths = WindowsServicePaths.ForMachine();
        if (!WindowsServicePaths.Exists(paths.Root)) throw new CliException("The Windows runner service is not installed.");
        var registration = paths.ReadRegistration();
        using var manager = new WindowsServiceScm();
        using var service = manager.OpenOwned(registration, manage: false);
        if (WindowsServiceScm.Status(service).State != System.ServiceProcess.ServiceControllerStatus.Running)
            throw new CliException("The Windows runner service is stopped. Start it in Windows Services to manage its tokens or check enrollment.");
        return (registration, await WindowsServiceControl.SendAsync(registration.ControlPipe, service, request, host.Time, ct));
    }

    private static (string Account, string Sid) ValidateManagedAccount(string account)
    {
        try
        {
            var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier));
            var canonical = sid.Translate(typeof(NTAccount)).Value;
            var split = canonical.IndexOf('\\');
            if (sid is SecurityIdentifier { AccountDomainSid: not null } && split > 0
                && !string.Equals(canonical[..split], Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                && NetIsServiceAccount(null, canonical[(split + 1)..], out var configured) == 0 && configured)
                return (WindowsServicePlan.Create(canonical).Account, sid.Value);
        }
        catch (IdentityNotMappedException) { }
        throw new CliException("Provision the gMSA on this domain-joined machine and grant it Log on as a service before installing mbus.");
    }

    private static string? ReadSecret(CliHost host, string name)
    {
        var value = host.GetEnvironmentVariable(name)?.Trim();
        if (!string.IsNullOrEmpty(value)) return value;
        if (host.GetEnvironmentVariable(name + "_FILE") is not { Length: > 0 } file) return null;
        try
        {
            using var reader = File.OpenText(Path.GetFullPath(file, host.CurrentDirectory));
            var buffer = new char[8193];
            try
            {
                var count = reader.ReadBlock(buffer);
                if (count > 8192) throw new CliException($"{name}_FILE is too large.");
                return new string(buffer, 0, count).Trim();
            }
            finally { Array.Clear(buffer); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new CliException($"Could not read the file named by {name}_FILE.");
        }
    }

    private static void DemandOutsideRun(CliHost host)
    {
        if (RunContext.IsInsideRun(host.GetEnvironmentVariable)) throw new CliException(RunContext.KeyRefused);
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetIsServiceAccount(string? server, string account, [MarshalAs(UnmanagedType.Bool)] out bool isService);
}

internal static class ServiceAgentTokens
{
    internal static string[] For(Contracts.Machines.AgentKind kind) => kind switch
    {
        Contracts.Machines.AgentKind.ClaudeCode => ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"],
        Contracts.Machines.AgentKind.Codex => ["OPENAI_API_KEY", "CODEX_API_KEY"],
        Contracts.Machines.AgentKind.Copilot => ["COPILOT_GITHUB_TOKEN"],
        Contracts.Machines.AgentKind.Antigravity => ["GEMINI_API_KEY"],
        _ => [],
    };
}

// Agent inventory is rescanned when tokens change, without interrupting in-flight tasks.
internal sealed class WindowsServiceTokenWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _watching;

    internal WindowsServiceTokenWatch(string tokenFile, Func<CancellationToken, Task> rescan, TimeProvider time, TextWriter log, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watching = WatchAsync(tokenFile, rescan, time, log, _stop.Token);
    }

    private static async Task WatchAsync(string tokenFile, Func<CancellationToken, Task> rescan, TimeProvider time, TextWriter log, CancellationToken ct)
    {
        var observed = File.GetLastWriteTimeUtc(tokenFile);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var current = File.GetLastWriteTimeUtc(tokenFile);
            if (current == observed) continue;
            try
            {
                await rescan(ct);
                observed = current;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                observed = current;
                await log.WriteLineAsync("Could not report the updated Windows service agent inventory. Rescan from the Runners page.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (_stop)
        {
            await _stop.CancelAsync();
            try { await _watching; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
    }
}

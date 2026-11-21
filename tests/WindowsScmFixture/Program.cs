using System.Reflection;
using System.Runtime.Versioning;
using System.ServiceProcess;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Releases;

return await WindowsScmFixture.RunAsync(args);

internal static class WindowsScmFixture
{
    private const string Initial = "0.0.1-scm-fixture.1";
    private const string Candidate = "0.0.1-scm-fixture.2";
    private const string AssemblyName = "mbus.ScmFixture.dll";
    private const string AgentFixtureValue = "fixture-agent-value";
    private const string AgentVariable = "OPENAI_API_KEY";
    private static readonly ServiceEnrollment Enrollment = new("https://scm-fixture.invalid", "fixture-enrollment-value");
    private static readonly TimeProvider Time = TimeProvider.System;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--version"])
        {
            // Deliberately different from both NuGet versions.
            Console.WriteLine("0.0.0-scm-fixture");
            return 0;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (args is [WindowsRunnerService.HostCommand] or [WindowsRunnerService.ApplyCommand])
            return await WindowsRunnerService.RunAsync(args[0], AssemblyName, RunOfflineUpdateAsync);
        if (args is [WindowsRunnerService.WorkerCommand]) return await RunWorkerAsync();
        if (args is ["--run-disposable-scm-fixture", var feed])
        {
            await RunAcceptanceAsync(Path.GetFullPath(feed));
            return 0;
        }
        Console.Error.WriteLine("This fixture requires --run-disposable-scm-fixture <local-feed> on a disposable Windows VM.");
        return 2;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAcceptanceAsync(string feed)
    {
        WindowsServicePaths.DemandAdministrator();
        var paths = WindowsServicePaths.ForMachine();
        paths.VerifyDotnet();
        using var manager = new WindowsServiceScm(canCreate: true);
        using var existing = manager.Find();
        Require(existing is null && !WindowsServicePaths.Exists(paths.Root) && !WindowsServicePaths.Exists(paths.State),
            "The fixed service name and both installation directories must be unused.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4), Time);
        var ct = deadline.Token;
        var stateCreated = false;
        var registered = false;
        paths.CreateRoot();
        try
        {
            // Only administrator-built files enter the dispatcher and seed directories.
            var build = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories))
            {
                var output = Path.Combine(paths.HostTools, Path.GetRelativePath(build, file));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output);
            }
            Directory.CreateDirectory(paths.HostPackageSource);
            foreach (var version in new[] { Initial, Candidate })
            {
                var name = DotnetToolPackage.ArchiveName(DotnetToolInstall.PackageId, version);
                File.Copy(Path.Combine(feed, name), Path.Combine(paths.HostPackageSource, name));
            }
            File.WriteAllText(OfflineConfig(paths), DotnetToolUpdater.NuGetConfig(paths.HostPackageSource));
            var assembly = Path.Combine(paths.HostTools, AssemblyName);
            var registration = new WindowsServiceRegistration(WindowsServicePlan.VirtualAccount,
                WindowsServicePaths.ServiceSid.Value, Initial, assembly, paths.HostCommandLine(assembly), Guid.NewGuid());
            paths.SaveRegistration(registration);
            WindowsServicePaths.OwnPayload(paths.Root);
            Require(paths.ReadRegistration() == registration, "The exact protected registration did not round-trip.");
            paths.CreateState();
            stateCreated = true;
            using (manager.Register(registration)) registered = true;
            using var service = manager.OpenOwned(registration, manage: true);
            await WindowsServiceScm.StartAsync(service, Time, ct);
            var supervisor = WindowsServiceScm.Status(service).ProcessId;
            await SendAsync(new("save", Tokens: new() { [AgentVariable] = AgentFixtureValue }));
            var status = await SendAsync(new("status"));
            Require(status.TokenNames is [AgentVariable], "The service could not read its DPAPI-protected agent environment.");
            await SendAsync(new("enroll", Enrollment));
            await WaitForAsync(Marker(paths, "initial-running"), ct);
            File.WriteAllText(Path.Combine(paths.Root, "request-update"), "");
            await WaitForAsync(Marker(paths, "draining"), ct);
            Require(DotnetToolPackage.InstalledVersion(paths.WorkerTools, DotnetToolInstall.PackageId) == Initial,
                "An update started while the dummy session was still draining.");
            File.WriteAllText(Path.Combine(paths.Root, "finish-session"), "");
            await WaitForAsync(Marker(paths, "candidate-running"), ct);
            var scm = WindowsServiceScm.Status(service);
            Require(scm.State == ServiceControllerStatus.Running && scm.ProcessId == supervisor,
                "The candidate was started by a different SCM host process.");

            await WindowsServiceScm.StopAsync(service, Time, ct);
            Require(File.Exists(Marker(paths, "candidate-stopped")), "The candidate did not handle SCM stop as EOF.");
            Console.WriteLine("Passed SCM boot, protected control requests and DPAPI, drain, unattended offline update, and SCM stop.");

            Task<ServiceResponse> SendAsync(ServiceRequest request) =>
                WindowsServiceControl.SendAsync(registration.ControlPipe, service, request, Time, ct);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2), Time);
            if (registered)
            {
                await WindowsServiceCommands.UninstallAsync(CliHost.CreateDefault(), print: false, cleanup.Token);
            }
            else
            {
                if (stateCreated)
                {
                    paths.VerifyState();
                    Directory.Delete(paths.State);
                }
                paths.VerifyRoot();
                Directory.Delete(paths.Root, recursive: true);
            }

            using var remaining = manager.Find();
            Require(remaining is null && !WindowsServicePaths.Exists(paths.Root) && !WindowsServicePaths.Exists(paths.State),
                "The fixture installation was not fully removed.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<CommandResult?> RunOfflineUpdateAsync(string command, IReadOnlyList<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment)
    {
        var paths = WindowsServicePaths.ForMachine();
        if (args is ["--version"])
            return DotnetToolUpdater.RunCommand(command, args, timeout, environment);

        Require(DotnetToolInstall.SamePath(command, paths.Dotnet)
            && args is ["tool", "update", "--tool-path", var tools, DotnetToolInstall.PackageId, "--version", Candidate, "--configfile", var config]
            && DotnetToolInstall.SamePath(tools, paths.WorkerTools)
            && DotnetToolInstall.SamePath(config, Path.Combine(paths.Settings, "update", "NuGet.Config"))
            && File.ReadAllText(config) == DotnetToolUpdater.NuGetConfig(DotnetToolUpdater.NuGetOrg, requireSignature: true),
            "The fixture only substitutes the forward command for its exact local dummy candidate.");
        var offline = OfflineConfig(paths);
        WindowsServicePaths.DemandTrustedPath(offline);
        return DotnetToolUpdater.RunCommand(command, [.. args.Take(8), offline], timeout, environment);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWorkerAsync()
    {
        var paths = WindowsServicePaths.ForMachine();
        var registration = paths.ReadRegistration();
        WindowsServicePaths.DemandServiceIdentity(registration.AccountSid);
        paths.VerifyState();
        var entry = Assembly.GetEntryAssembly()!.Location;
        var install = DotnetToolInstall.Running;
        Require(install is { Global: false, ToolsPath: not null, Package.Version: Initial or Candidate }
            && DotnetToolInstall.SamePath(install.ToolsPath, paths.WorkerTools), "Unexpected fixture worker package or location.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3), Time);
        var ct = deadline.Token;
        using var input = Console.OpenStandardInput();
        var signal = new byte[1];
        Require(await input.ReadAsync(signal, ct) == 1 && signal[0] == 1, "Expected the SCM job startup handshake.");
        Require(WindowsServiceEnrollment.Read(paths.Settings) == Enrollment, "The worker could not decrypt its enrollment.");
        Require(new ConfigStore(paths.Settings).AgentEnvironment.Load().GetValueOrDefault(AgentVariable) == AgentFixtureValue,
            "The worker could not decrypt its saved agent environment.");

        var updater = new DotnetToolUpdater(install!, paths.Settings, paths.Dotnet, Console.Error, RunnerUpdateRecord.In(paths.Settings),
            "0.0.0-scm-fixture", Time, entryAssembly: entry) { MinimumServiceVersion = registration.Version };
        if (updater.CurrentVersion == Candidate)
        {
            updater.Startup();
            Require(updater.LastResult?.Outcome == RunnerUpdateOutcome.Updated, "The candidate did not see a successful update.");
            updater.ConfirmConnected();
            var attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(paths.Settings, "update", "attempt.json"));
            Require(attempt is { From: Initial, To: Candidate, Confirmed: true, Terminal: true },
                "The service did not confirm the candidate using NuGet package identity.");
            File.WriteAllText(Marker(paths, "candidate-running"), "");
            Require(await input.ReadAsync(signal, ct) == 0, "Expected EOF on SCM stop.");
            File.WriteAllText(Marker(paths, "candidate-stopped"), "");
            return 0;
        }

        // The fixture's single synthetic session remains open until the acceptance process releases it.
        File.WriteAllText(Marker(paths, "initial-running"), "");
        await WaitForAsync(Path.Combine(paths.Root, "request-update"), ct);
        File.WriteAllText(Marker(paths, "draining"), "");
        await WaitForAsync(Path.Combine(paths.Root, "finish-session"), ct);
        Require(!File.Exists(Path.Combine(paths.Settings, "update", "pending.service")),
            "The update was prepared before the session finished.");
        Require(await updater.InstallAsync(new() { Version = Candidate, Via = RunnerInstallKind.DotnetTool }, ct),
            updater.LastResult?.Reason ?? "The worker could not prepare its update.");
        return RunnerCommand.UpdateExitCode;
    }

    private static async Task WaitForAsync(string marker, CancellationToken ct)
    {
        while (!File.Exists(marker)) await Task.Delay(TimeSpan.FromMilliseconds(100), Time, ct);
    }

    [SupportedOSPlatform("windows")]
    private static string Marker(WindowsServicePaths paths, string name) => Path.Combine(paths.State, "fixture-" + name);

    [SupportedOSPlatform("windows")]
    private static string OfflineConfig(WindowsServicePaths paths) => Path.Combine(paths.HostTools, "Fixture.NuGet.Config");

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}

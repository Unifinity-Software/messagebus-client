using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private const string FailedStart = "Fixture candidate deliberately failed its start check.";
    private static readonly TimeProvider Time = TimeProvider.System;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--version"])
        {
            if (OperatingSystem.IsWindows()
                && DotnetToolInstall.Running is { ToolsPath: { } tools, Package.Version: Candidate }
                && DotnetToolInstall.SamePath(tools, WindowsServicePaths.ForMachine().WorkerTools)
                && File.Exists(HostFile(WindowsServicePaths.ForMachine(), "rollback")))
            {
                Console.Error.WriteLine(FailedStart);
                return 1;
            }
            // Deliberately different from both NuGet versions.
            Console.WriteLine("0.0.0-scm-fixture");
            return 0;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (args is [WindowsRunnerService.HostCommand] or [WindowsRunnerService.ApplyCommand])
            return await WindowsRunnerService.RunAsync(args[0], AssemblyName, RunOfflineUpdateAsync);
        if (args is [WindowsRunnerService.WorkerCommand]) return await RunWorkerAsync();
        if (args is [var operation, var feed]
            && operation is "--run-disposable-scm-fixture" or "--run-disposable-scm-rollback" or "--prepare-disposable-scm-reboot")
        {
            await RunAcceptanceAsync(Path.GetFullPath(feed), rollback: operation == "--run-disposable-scm-rollback",
                retainForReboot: operation == "--prepare-disposable-scm-reboot");
            return 0;
        }
        if (args is ["--check-disposable-scm-reboot"] or ["--cleanup-disposable-scm-reboot"])
        {
            await RunRebootStageAsync(cleanup: args[0] == "--cleanup-disposable-scm-reboot");
            return 0;
        }
        Console.Error.WriteLine("See the fixture README for the opt-in commands and disposable Windows VM preconditions.");
        return 2;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAcceptanceAsync(string feed, bool rollback, bool retainForReboot)
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
        var retained = false;
        var probe = CreatePrivateProbe();
        var rootCreated = false;
        try
        {
            paths.CreateRoot();
            rootCreated = true;
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
            File.WriteAllText(HostFile(paths, "private-probe"), probe);
            if (rollback) File.WriteAllText(HostFile(paths, "rollback"), "");
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
            Require(File.ReadAllText(Marker(paths, "private-probe")) == "AccessDenied",
                "The virtual-account worker did not report AccessDenied for the synthetic profile file.");
            Console.WriteLine("Passed virtual-account access check (AccessDenied) for the synthetic profile file.");
            File.WriteAllText(Path.Combine(paths.Root, "request-update"), "");
            await WaitForAsync(Marker(paths, "draining"), ct);
            Require(DotnetToolPackage.InstalledVersion(paths.WorkerTools, DotnetToolInstall.PackageId) == Initial,
                "An update started while the dummy session was still draining.");
            File.WriteAllText(Path.Combine(paths.Root, "finish-session"), "");
            var worker = rollback ? "restored" : "candidate";
            await WaitForAsync(Marker(paths, worker + "-running"), ct);
            Require(DotnetToolPackage.InstalledVersion(paths.WorkerTools, DotnetToolInstall.PackageId)
                == (rollback ? Initial : Candidate), "The installed package did not match the running worker.");
            var scm = WindowsServiceScm.Status(service);
            Require(scm.State == ServiceControllerStatus.Running && scm.ProcessId == supervisor,
                "The worker was started by a different SCM host process.");

            await WindowsServiceScm.StopAsync(service, Time, ct);
            Require(File.Exists(Marker(paths, worker + "-stopped")), "The worker did not handle SCM stop as EOF.");
            if (retainForReboot)
            {
                File.Delete(Marker(paths, "candidate-running"));
                File.Delete(Marker(paths, "candidate-stopped"));
                File.Delete(probe);
                File.WriteAllText(HostFile(paths, "reboot"), "");
                retained = true;
                Console.WriteLine("Prepared reboot fixture. SCM service is stopped and the confirmed candidate is retained.");
            }
            Console.WriteLine(rollback
                ? $"Passed offline rollback (RolledBack). Candidate start failed, version {Initial} resumed under the same SCM host, and SCM stop reached EOF."
                : "Passed SCM boot, protected control requests and DPAPI, drain, unattended offline update, and SCM stop.");

            Task<ServiceResponse> SendAsync(ServiceRequest request) =>
                WindowsServiceControl.SendAsync(registration.ControlPipe, service, request, Time, ct);
        }
        finally
        {
            if (!retained)
            {
                try
                {
                    if (registered)
                    {
                        await UninstallAsync(manager, paths);
                    }
                    else if (rootCreated)
                    {
                        if (stateCreated)
                        {
                            paths.VerifyState();
                            Directory.Delete(paths.State);
                        }
                        paths.VerifyRoot();
                        Directory.Delete(paths.Root, recursive: true);
                    }
                }
                finally
                {
                    File.Delete(probe);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string CreatePrivateProbe()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Require(Path.IsPathFullyQualified(profile) && Directory.Exists(profile), "The installing user's Windows profile is missing.");
        var path = Path.Combine(profile, $"mbus-scm-private-{Guid.NewGuid():N}.txt");
        using var user = WindowsIdentity.GetCurrent();
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
            FileOptions.None, acl);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunRebootStageAsync(bool cleanup)
    {
        WindowsServicePaths.DemandAdministrator();
        var paths = WindowsServicePaths.ForMachine();
        var registration = paths.ReadRegistration();
        Require(registration.Account == WindowsServicePlan.VirtualAccount && registration.Version == Initial
            && registration.AccountSid == WindowsServicePaths.ServiceSid.Value
            && registration.Assembly == Path.Combine(paths.HostTools, AssemblyName)
            && File.Exists(HostFile(paths, "reboot")), "Expected the retained reboot fixture registration.");
        paths.VerifyState();
        using var manager = new WindowsServiceScm();
        using (var service = manager.OpenOwned(registration, manage: false))
        {
            if (!cleanup)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5), Time);
                await WaitForAsync(Marker(paths, "reboot-running"), deadline.Token);
                var status = WindowsServiceScm.Status(service);
                Require(status.State == ServiceControllerStatus.Running
                    && DotnetToolPackage.InstalledVersion(paths.WorkerTools, DotnetToolInstall.PackageId) == Candidate,
                    "Expected the confirmed fixture candidate running under SCM after reboot.");
                Console.WriteLine($"Passed post-reboot worker check. SCM PID {status.ProcessId}, candidate {Candidate}, DPAPI reads succeeded.");
            }
        }
        if (cleanup)
        {
            await UninstallAsync(manager, paths);
            Console.WriteLine("Removed the fixture SCM registration and both installation directories.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task UninstallAsync(WindowsServiceScm manager, WindowsServicePaths paths)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2), Time);
        await WindowsServiceCommands.UninstallAsync(CliHost.CreateDefault(), print: false, cleanup.Token);
        using var remaining = manager.Find();
        Require(remaining is null && !WindowsServicePaths.Exists(paths.Root) && !WindowsServicePaths.Exists(paths.State),
            "The fixture installation was not fully removed.");
    }

    [SupportedOSPlatform("windows")]
    private static Task<CommandResult?> RunOfflineUpdateAsync(string command, IReadOnlyList<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment)
    {
        var paths = WindowsServicePaths.ForMachine();
        if (args is ["--version"])
            return DotnetToolUpdater.RunCommand(command, args, timeout, environment);

        var update = Path.Combine(paths.Settings, "update");
        if (args is ["tool", "update", "--tool-path", var recoveryTools, var recoveryId,
                "--version", Initial, "--configfile", var recovery, "--allow-downgrade"])
        {
            Require(File.Exists(HostFile(paths, "rollback")) && DotnetToolInstall.SamePath(command, paths.Dotnet)
                && recoveryId.Equals(DotnetToolInstall.PackageId, StringComparison.OrdinalIgnoreCase)
                && DotnetToolInstall.SamePath(recoveryTools, paths.WorkerTools)
                && DotnetToolInstall.SamePath(recovery, Path.Combine(update, "Recovery.Config"))
                && File.ReadAllText(recovery) == DotnetToolUpdater.NuGetConfig(Path.Combine(update, "recovery")),
                "Expected the exact saved offline recovery command.");
            return DotnetToolUpdater.RunCommand(command, args, timeout, environment);
        }

        Require(DotnetToolInstall.SamePath(command, paths.Dotnet)
            && args is ["tool", "update", "--tool-path", var tools, var packageId, "--version", Candidate, "--configfile", var config]
            && packageId.Equals(DotnetToolInstall.PackageId, StringComparison.OrdinalIgnoreCase)
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
            var attemptPath = Path.Combine(paths.Settings, "update", "attempt.json");
            var attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(attemptPath);
            var reboot = File.Exists(HostFile(paths, "reboot")) && attempt is { Confirmed: true, Terminal: true };
            if (!reboot)
            {
                updater.Startup();
                Require(updater.LastResult?.Outcome == RunnerUpdateOutcome.Updated, "The candidate did not see a successful update.");
                updater.ConfirmConnected();
                attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(attemptPath);
            }
            Require(attempt is { From: Initial, To: Candidate, Confirmed: true, Terminal: true },
                "The service did not confirm the candidate using NuGet package identity.");
            File.WriteAllText(Marker(paths, reboot ? "reboot-running" : "candidate-running"), "");
            Require(await input.ReadAsync(signal) == 0, "Expected EOF on SCM stop.");
            File.WriteAllText(Marker(paths, "candidate-stopped"), "");
            return 0;
        }

        var updatePath = Path.Combine(paths.Settings, "update");
        if (File.Exists(HostFile(paths, "rollback"))
            && DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(updatePath, "attempt.json")) is
                { From: Initial, To: Candidate, Terminal: true, RecoveryInstalled: true, RecoveryTries: 1 })
        {
            updater.Startup();
            Require(updater.LastResult is { Outcome: RunnerUpdateOutcome.RolledBack, Reason: { } reason }
                && reason.Contains(FailedStart, StringComparison.Ordinal),
                "Expected an installed candidate start failure followed by offline rollback.");
            File.WriteAllText(Marker(paths, "restored-running"), "");
            Require(await input.ReadAsync(signal) == 0, "Expected EOF on SCM stop after rollback.");
            File.WriteAllText(Marker(paths, "restored-stopped"), "");
            return 0;
        }

        var probe = File.ReadAllText(HostFile(paths, "private-probe"));
        try
        {
            using var file = File.OpenRead(probe);
            throw new InvalidOperationException("The virtual-account worker opened the private synthetic profile file.");
        }
        catch (UnauthorizedAccessException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            File.WriteAllText(Marker(paths, "private-probe"), "AccessDenied");
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
    private static string HostFile(WindowsServicePaths paths, string name) => Path.Combine(paths.Root, "fixture-" + name);

    [SupportedOSPlatform("windows")]
    private static string OfflineConfig(WindowsServicePaths paths) => Path.Combine(paths.HostTools, "Fixture.NuGet.Config");

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}

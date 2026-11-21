using System.Reflection;
using System.Runtime.Versioning;
using System.ServiceProcess;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Update;

namespace MessageBus.Cli.Runner.Service;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRunnerService : ServiceBase
{
    internal const string HostCommand = "__runner-service-host";
    internal const string WorkerCommand = "__runner-service-worker";
    internal const string ApplyCommand = "__runner-service-apply";
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(22);
    private readonly WindowsServicePaths _paths;
    private readonly WindowsServiceRegistration _registration;
    private readonly string _workerAssemblyName;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _stopWorker = new();
    private readonly TaskCompletionSource _provisioned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time = TimeProvider.System;
    private Task _host = Task.CompletedTask;
    private Task _worker = Task.CompletedTask;
    private int _stopping;

    private WindowsRunnerService(WindowsServicePaths paths, WindowsServiceRegistration registration, string workerAssemblyName)
    {
        _paths = paths;
        _registration = registration;
        _workerAssemblyName = workerAssemblyName;
        ServiceName = WindowsServicePlan.Name;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    internal static async Task<int> RunAsync(string command, string workerAssemblyName = "mbus.dll", DotnetToolUpdater.ToolCommand? runUpdateCommand = null)
    {
        var paths = WindowsServicePaths.ForMachine();
        var registration = paths.ReadRegistration();
        WindowsServicePaths.DemandServiceIdentity(registration.AccountSid);
        paths.VerifyState();
        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (command == WorkerCommand)
        {
            if (assembly is null || !RunnerProcesses.IsInside(assembly, paths.WorkerTools))
                throw new CliException("The SCM worker must run from its own service installation.");
            return await RunWorkerAsync(paths, registration);
        }
        if (assembly is null || !DotnetToolInstall.SamePath(assembly, registration.Assembly))
            throw new CliException("The SCM dispatcher must run from the protected host installation.");
        if (command == ApplyCommand)
            return await DotnetToolUpdater.RunServiceApplierAsync(Path.Combine(paths.Settings, "update"), paths.WorkerTools, paths.Dotnet, run: runUpdateCommand);

        using var service = new WindowsRunnerService(paths, registration, workerAssemblyName);
        Run(service);
        return service.ExitCode;
    }

    protected override void OnStart(string[] args)
    {
        _host = Task.Run(HostAsync);
        _ = _host.ContinueWith(completed =>
        {
            if (Interlocked.CompareExchange(ref _stopping, 1, 0) != 0) return;
            ExitCode = 1;
            if (completed.Exception is { } error) Log($"Service failed ({error.GetBaseException().GetType().Name}).");
            Stop();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    protected override void OnStop()
    {
        RequestAdditionalTime(StopDeadline);
        StopHost(StopDeadline);
    }

    protected override void OnShutdown() => StopHost(TimeSpan.FromSeconds(18));

    private void StopHost(TimeSpan deadline)
    {
        Interlocked.Exchange(ref _stopping, 1);
        _stopWorker.Cancel();
        _stop.Cancel();
        try { _host.WaitAsync(deadline, _time).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (System.TimeoutException)
        {
            // Returning an orderly status with a worker still alive would orphan jobs on shutdown.
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            Log($"Service shutdown failed ({ex.GetType().Name}).");
        }
    }

    private async Task HostAsync()
    {
        if (!File.Exists(_paths.MaintenancePath)
            && (WindowsServiceEnrollment.IsPending(_paths.Settings) || IsEnrolled()))
            _provisioned.TrySetResult();
        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _stopWorker.Token);
        _worker = SuperviseAsync(workerStop.Token);
        var control = WindowsServiceControl.ServeAsync(_registration.ControlPipe, HandleAsync, Log, _time, _stop.Token);
        var finished = await Task.WhenAny(_worker, control);
        if (finished == _worker && _stopWorker.IsCancellationRequested)
        {
            try { await _worker; }
            catch (OperationCanceledException) when (workerStop.IsCancellationRequested) { }
            await control; // Keep the control pipe open until SCM stops the service.
        }
        await _stop.CancelAsync();
        await workerStop.CancelAsync();
        await Task.WhenAll(_worker, control);
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        await _provisioned.Task.WaitAsync(ct);
        var install = new DotnetToolInstall(_paths.WorkerTools, Global: false);
        var attempt = Path.Combine(_paths.Settings, "update", "attempt.json");
        if (install.Shim is null && !File.Exists(attempt))
            await WindowsServicePackage.InstallWorkerAsync(_paths, _registration.Version, _time, ct);
        await WindowsServiceWorkerLifecycle.RunAsync(
            async stopping =>
            {
                if (!File.Exists(attempt)) return;
                var apply = _paths.ProcessStart(_paths.Dotnet, _paths.Root, _paths.Home, worker: true);
                apply.ArgumentList.Add(_registration.Assembly);
                apply.ArgumentList.Add(ApplyCommand);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7), _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stopping);
                var code = await ServiceChild.RunAsync(apply, sendStop: false, _time, linked.Token);
                if (code != 0) throw new CliException("The service could not apply or recover its worker update.");
            },
            async stopping =>
            {
                var assembly = WindowsServicePackage.FindWorkerAssembly(_paths.WorkerTools, _workerAssemblyName);
                var start = _paths.ProcessStart(_paths.Dotnet, _paths.Root, _paths.Home, worker: true);
                start.ArgumentList.Add(assembly);
                start.ArgumentList.Add(WorkerCommand);
                return await ServiceChild.RunAsync(start, sendStop: true, _time, stopping);
            }, _time, ct);
    }

    private async Task<ServiceResponse> HandleAsync(ServiceRequest request, CancellationToken ct)
    {
        var store = new ConfigStore(_paths.Settings);
        switch (request.Operation)
        {
            case "enroll" when request.Enrollment is { } enrollment && !File.Exists(_paths.MaintenancePath) && !IsEnrolled():
                WindowsServiceEnrollment.Save(_paths.Settings, WindowsServiceEnrollment.Validate(enrollment.Server, enrollment.ApiKey));
                _provisioned.TrySetResult();
                return new(true);
            case "save" when request.Tokens is { Count: > 0 } tokens && !File.Exists(_paths.MaintenancePath)
                    && tokens.All(pair => AgentEnvironmentStore.Names.Contains(pair.Key) && pair.Value is { Length: > 0 and <= 8192 }):
                store.AgentEnvironment.Save(tokens);
                return new(true, [.. tokens.Keys.Order(StringComparer.Ordinal)]);
            case "clear" when !File.Exists(_paths.MaintenancePath):
                store.AgentEnvironment.Delete();
                return new(true, []);
            case "status":
                return new(true, [.. store.AgentEnvironment.Load().Keys.Order(StringComparer.Ordinal)], IsEnrolled());
            case "remove" when File.Exists(_paths.MaintenancePath):
                await _stopWorker.CancelAsync();
                try { await _worker.WaitAsync(ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception) when (_worker.IsFaulted && !ct.IsCancellationRequested) { }
                // Only the service identity handles its writable tree. The elevated uninstaller removes
                // the empty protected state directory and never traverses contents a task could replace.
                _paths.VerifyState();
                foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.State))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.Directory))
                        Directory.Delete(entry, recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
                    else File.Delete(entry);
                }
                return new(true);
            default:
                return new(false);
        }
    }

    private bool IsEnrolled()
    {
        // Avoid creating state just to answer status or wait for the initial provisioning command.
        if (!File.Exists(Path.Combine(_paths.Settings, CliConfig.FileName))) return false;
        var store = new ConfigStore(_paths.Settings);
        return store.Load().MachineId is not null && store.HasKey;
    }

    private void Log(string message)
    {
        try
        {
            using var file = new RunnerLogFile(Path.Combine(_paths.State, "logs", "supervisor.log"));
            file.WriteLine($"{_time.GetUtcNow():O} {message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<int> RunWorkerAsync(WindowsServicePaths paths, WindowsServiceRegistration registration)
    {
        using var input = Console.OpenStandardInput();
        if (input.ReadByte() != 1) return 1;
        using var stop = new CancellationTokenSource();
        _ = MonitorStopAsync(input, stop);
        var worker = new WindowsRunnerWorker(paths.Home, paths.Settings, paths.Dotnet, registration.Version, WindowsServiceEnrollment.Read(paths.Settings));
        var host = CliHost.CreateDefault(worker);
        return await CliApp.RunAsync(["runner", "--work-dir", paths.Runs, "--log-file", Path.Combine(paths.State, "logs", "runner.log")],
            host, stop.Token);
    }

    private static async Task MonitorStopAsync(Stream input, CancellationTokenSource stop)
    {
        try { _ = await input.ReadAsync(new byte[1]); }
        catch (IOException) { }
        try { await stop.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }
}

internal sealed class WindowsRunnerWorker(string home, string settings, string dotnet, string hostVersion, ServiceEnrollment? enrollment)
{
    internal string Home { get; } = home;
    internal string Settings { get; } = settings;
    internal string Dotnet { get; } = dotnet;
    internal string HostVersion { get; } = hostVersion;
    internal ServiceEnrollment? Enrollment { get; set; } = enrollment;

    internal string? GetEnvironmentVariable(string name) => name switch
    {
        EnvVars.Home => Settings,
        EnvVars.Server => Enrollment?.Server,
        EnvVars.ApiKey => Enrollment?.ApiKey,
        RunnerRestart.SupervisedEnvVar => "1",
        RunnerOptions.AgentToolsVariable => "refuse",
        _ => Environment.GetEnvironmentVariable(name),
    };
}

internal static class WindowsServiceWorkerLifecycle
{
    // Ordinary failure is handled by the SCM's finite recovery actions. Only an update may start
    // another worker inside one service process.
    internal static async Task RunAsync(Func<CancellationToken, Task> beforeStart, Func<CancellationToken, Task<int>> run,
        TimeProvider time, CancellationToken ct)
    {
        var updateRestarts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await beforeStart(ct);
            var started = time.GetTimestamp();
            var exit = await run(ct);
            ct.ThrowIfCancellationRequested();
            if (exit != RunnerCommand.UpdateExitCode) throw new CliException($"The Windows runner worker stopped (exit {exit}).");
            if (time.GetElapsedTime(started) >= TimeSpan.FromMinutes(5)) updateRestarts = 0;
            if (++updateRestarts > 3) throw new CliException("The Windows runner worker exceeded its update restart limit.");
        }
    }
}

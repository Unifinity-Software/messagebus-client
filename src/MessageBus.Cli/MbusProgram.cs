namespace MessageBus.Cli;

/// <summary>The mbus entry point, shared by the executable and in-process hosts.</summary>
public static class MbusProgram
{
    /// <summary>Runs mbus with the default host and returns its process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var imageManaged = Hosting.CliHost.IsRunnerImage;
        if (!imageManaged && OperatingSystem.IsWindows() && args is [var serviceCommand]
            && serviceCommand is Runner.Service.WindowsRunnerService.HostCommand or Runner.Service.WindowsRunnerService.WorkerCommand or Runner.Service.WindowsRunnerService.ApplyCommand)
        {
            // SCM must own the actual dispatcher process. Its protected entry point never hands off.
            try { return await Runner.Service.WindowsRunnerService.RunAsync(serviceCommand); }
            catch (CliException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                return ex.ExitCode;
            }
        }
        if (args is [Update.DotnetToolUpdater.PrivateCommand, var mode, var directory])
        {
            if (!imageManaged)
                return await Update.DotnetToolUpdater.RunApplierAsync(mode, directory);
            await Console.Error.WriteLineAsync(Update.ImageUpdatePolicy.Reason);
            return 1;
        }

        if (!imageManaged)
        {
            // Use the current version's own file where the standalone updater keeps versions side by side.
            if (Update.VersionedInstall.HandOff(args) is { } handedOff)
                return handedOff;
            Update.VersionedInstall.MarkRunning();
        }
        // UTF-8 before anything captures Console.Out, so tables draw on Windows consoles too.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // OpenTelemetry (the original issue): exports only when OTEL_EXPORTER_OTLP_ENDPOINT is set; continues an agent run's TRACEPARENT.
        // Started before the host takes Console.Error, which the runner's log export (the original issue) wraps.
        using var telemetry = MessageBus.Cli.Telemetry.CliTelemetry.Start(Environment.GetEnvironmentVariable, args);
        var host = MessageBus.Cli.Hosting.CliHost.CreateDefault();
        // the original issue: a runner started in a terminal that just updated itself starts the new version here, once this one has stopped.
        return await MessageBus.Cli.Runner.RunnerRestart.AfterExitAsync(await MessageBus.Cli.CliApp.RunAsync(args, host), args);
    }
}

namespace MessageBus.Cli;

/// <summary>The mbus entry point, shared by the executable and in-process hosts.</summary>
public static class MbusProgram
{
    /// <summary>Runs mbus with the default host and returns its process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is [Update.DotnetToolUpdater.PrivateCommand, var mode, var directory])
            return await Update.DotnetToolUpdater.RunApplierAsync(mode, directory);
        // mbus: the messagebus.ai command-line tool. The commands live in CliApp (and Runner/ for the original issue).
        // the original issue: on Windows, mbus mcp and mbus runner run from the current version's own file, which no update changes.
        if (MessageBus.Cli.Update.VersionedInstall.HandOff(args) is { } handedOff)
        {
            return handedOff;
        }
        // the original issue: on macOS and Linux, tells updates this version's file is in use, so they don't delete it.
        MessageBus.Cli.Update.VersionedInstall.MarkRunning();
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

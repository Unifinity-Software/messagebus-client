using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Update;

/// <summary>How <c>mbus runner</c> (the original issue) updates itself.</summary>
public static class RunnerUpdateSetup
{
    /// <summary>
    /// The runner's updater, or null (with a line in <paramref name="log"/>) when this mbus can't update
    /// itself: a development build without the release key, run from a build folder, or no feed.
    /// </summary>
    /// <param name="record">Where the result of an update is kept for the next start to report (the original issue).</param>
    public static IRunnerUpdater? TryCreate(CliHost host, CliSettings settings, TextWriter log, RunnerUpdateRecord? record = null) =>
        TryCreate(host, settings, log, record, out _);

    /// <summary>
    /// <see cref="TryCreate(CliHost, CliSettings, TextWriter, RunnerUpdateRecord?)"/>, also saying whether this mbus can
    /// update itself and why not (the original issue): the runner reports that, so the Runners page shows the reason instead of
    /// offering an update that would install nothing.
    /// </summary>
    /// <param name="ability">Whether an update request installs a release here, with the reason when it doesn't.</param>
    public static IRunnerUpdater? TryCreate(CliHost host, CliSettings settings, TextWriter log, RunnerUpdateRecord? record, out RunnerUpdateAbility ability)
    {
        static RunnerUpdateAbility Off(string reason) => new() { CanUpdate = false, Reason = reason };
        ability = new RunnerUpdateAbility { CanUpdate = true, Via = RunnerInstallKind.Standalone };
        if (DotnetToolInstall.Running is { } installedTool)
        {
            var manager = host.RunnerServices();
            string? definition = null;
            if (manager.Platform == ServicePlatform.Windows)
            {
                var query = new ServiceStep("schtasks.exe", ["/Query", "/TN", RunnerServiceDefinitions.TaskName, "/XML"], MayFail: true);
                var result = manager.RunAsync(query, CancellationToken.None).GetAwaiter().GetResult();
                if (result.ExitCode == 0) definition = result.Output;
            }
            else if (manager.Platform == ServicePlatform.MacOs)
            {
                var path = Path.Combine(host.HomeDirectory, "Library", "LaunchAgents", RunnerServiceDefinitions.LaunchdLabel + ".plist");
                try { if (File.Exists(path)) definition = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var reason = manager.Platform == ServicePlatform.Other ? "Dotnet-tool runner updates through a service are supported on Windows and macOS."
                : definition is null ? "This runner was started in a terminal; run mbus runner install so it updates itself."
                : !RunnerServiceCommand.HasUpdateStep(manager.Platform, definition, host.ConfigDirectory, installedTool.Shim)
                    ? "Run mbus runner install once on this machine so it can update itself." : null;
            var dotnet = MessageBus.Cli.Agents.ExecutableLocator.Find(host, "dotnet")?.Path;
            reason ??= installedTool.ToolsPath is null ? "Local manifest tools cannot update through the runner service."
                : installedTool.Package is null ? DotnetToolPackage.IdentityUnknown
                : dotnet is null ? "The dotnet SDK could not be found on this machine." : null;
            ability = new() { CanUpdate = reason is null, Via = RunnerInstallKind.DotnetTool, Reason = reason };
            if (reason is not null) { log.WriteLine("Updates: off. " + reason); return null; }
            var updater = new DotnetToolUpdater(installedTool, host.ConfigDirectory, dotnet!, log,
                record ?? RunnerUpdateRecord.In(host.ConfigDirectory), CliHost.Version, host.Time);
            log.WriteLine("Updates: through dotnet, applied by the registered runner service after open sessions close.");
            return updater;
        }
        SelfUpdater probe;
        try
        {
            probe = UpdateSetup.Create(host, settings, channel: null, log);
        }
        catch (CliException ex)
        {
            log.WriteLine($"Updates: off. {ex.Message}");
            ability = Off(ReleaseKey.Resolve(host.GetEnvironmentVariable) is null
                ? "This mbus is a build from source and can't update itself."
                : $"This mbus can't update itself. {ex.Message}");
            return null;
        }
        using (probe)
        {
            if (!probe.CanInstall)
            {
                log.WriteLine("Updates: off. This mbus runs from a build folder (dotnet run), so there is no mbus file to replace.");
                ability = Off("This mbus runs from a build folder and can't update itself.");
                return null;
            }
            if (probe.DotnetTool is { } tool)
            {
                // the original issue: update requests are answered "not installed" with this reason, which the Runners page shows.
                log.WriteLine($"Updates: through dotnet. This mbus was installed with dotnet tool; update it with: {tool.UpdateCommand()}");
                ability = Off($"This mbus was installed with dotnet tool. Update it on the machine with: {tool.UpdateCommand()}");
            }
        }
        return new RunnerSelfUpdater(request => UpdateSetup.Create(host, settings, request.Channel, log, request.Mirror,
            request.Version is { Length: > 0 } version && version != RunnerUpdates.Latest ? version : null), log, record, CliHost.Version, host.Time);
    }

    /// <summary>
    /// After the server closed the connection with 4426: asks the engine for an update (no new work)
    /// and waits until every running task has finished, so the runner then installs it and exits 75.
    /// </summary>
    public static async Task UpdateWhenIdleAsync(RunnerEngine engine, TextWriter log, CancellationToken cancellationToken)
    {
        await log.WriteLineAsync("Updating mbus once the running tasks finish.").ConfigureAwait(false);
        engine.RequestUpdate(new UpdateRequest { Version = RunnerUpdates.Latest }, offline: true);
        try
        {
            await engine.UpdateReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

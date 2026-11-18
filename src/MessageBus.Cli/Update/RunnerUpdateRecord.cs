using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Update;

/// <summary>
/// How the runner's last update ended (the original issue), kept in <see cref="FileName"/> in the mbus settings folder: written when an
/// update ends (before the runner exits to be restarted), read when the runner starts again and sent with its inventory,
/// so the Runners page says "updated to 0.3.1" or "rolled back, and why" once the runner is back.
/// </summary>
/// <param name="path">The file.</param>
public sealed class RunnerUpdateRecord(string path)
{
    /// <summary>The file's name in the settings folder.</summary>
    public const string FileName = "last-update.json";

    /// <summary>The record in <paramref name="settingsDirectory"/>.</summary>
    /// <param name="settingsDirectory">The mbus settings folder.</param>
    public static RunnerUpdateRecord In(string settingsDirectory) => new(Path.Combine(settingsDirectory, FileName));

    /// <summary>Keeps <paramref name="report"/>; a write that fails is logged, never thrown (the update itself is what matters).</summary>
    /// <param name="report">How the update ended.</param>
    /// <param name="log">Where a failed write is mentioned.</param>
    public void Save(RunnerUpdateReport report, TextWriter log) => Save(report, log, DotnetToolInstall.Running);

    internal void Save(RunnerUpdateReport report, TextWriter log, DotnetToolInstall? toolInstall)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            var stored = JsonNode.Parse(MessageBusJson.Serialize(report))!.AsObject();
            stored["install"] = InstallKind(toolInstall);
            File.WriteAllText(temp, stored.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Couldn't keep this update's result for the dashboard: {ex.Message}");
        }
    }

    /// <summary>
    /// The last update's result as the runner now running sees it: an update that put <c>0.3.1</c> in place, read by a runner
    /// that still runs an older version, or the from-version after a downgrade, reads as rolled back. A version newer
    /// than both recorded versions or another install kind clears the record. Null when there is none or it can't be read.
    /// Only the runner may call this: reading a stale record deletes its file.
    /// </summary>
    /// <param name="runningVersion">The version of the mbus reading it.</param>
    public RunnerUpdateReport? Load(string runningVersion) => Load(runningVersion, DotnetToolInstall.Running);

    internal RunnerUpdateReport? Load(string runningVersion, DotnetToolInstall? toolInstall)
    {
        RunnerUpdateReport? report;
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var stored = JsonDocument.Parse(json);
            // Older records came only from standalone mbus; its first update can migrate a plain file into versions.
            var install = stored.RootElement.TryGetProperty("install", out var kind) ? kind.GetString() : "standalone";
            if (install != InstallKind(toolInstall))
            {
                Clear();
                return null;
            }
            report = MessageBusJson.Deserialize<RunnerUpdateReport>(json);
            if (report is not null && ReleaseVersion.Compare(runningVersion, report.FromVersion) is > 0
                && ReleaseVersion.Compare(runningVersion, report.ToVersion) is > 0)
            {
                Clear();
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
        if (report is { Outcome: RunnerUpdateOutcome.Updated, ToVersion: { } installed }
            && (ReleaseVersion.Compare(installed, runningVersion) is > 0
                || (ReleaseVersion.Compare(runningVersion, report.FromVersion) is 0 && ReleaseVersion.Compare(installed, runningVersion) is < 0)))
        {
            return report with
            {
                Outcome = RunnerUpdateOutcome.RolledBack,
                Reason = $"mbus {installed} was installed, but the runner started again on mbus {runningVersion}.",
            };
        }
        return report;
    }

    private static string InstallKind(DotnetToolInstall? toolInstall) => toolInstall is null ? "standalone" : "dotnet-tool";

    /// <summary>
    /// Forgets an update from an install or server this runner no longer uses. A delete that fails is ignored: it must never
    /// stop a sign-in or a runner from starting.
    /// </summary>
    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

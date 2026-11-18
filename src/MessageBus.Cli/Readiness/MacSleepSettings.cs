using System.Text.RegularExpressions;
using MessageBus.Cli.Agents;

namespace MessageBus.Cli.Readiness;

/// <summary>Read-only macOS sleep diagnosis, kept separate from the runner's power assertion.</summary>
internal static class MacSleepSettings
{
    internal const string Advice = "To keep it available for queued work, use mbus runner --keep-awake always, or in System Settings > Energy prevent automatic sleeping when the display is off.";

    public static async Task<string> ReadAsync(CancellationToken ct)
    {
        var settings = await CommandRunner.RunAsync("/usr/bin/pmset", ["-g"], ct).ConfigureAwait(false);
        var assertions = await CommandRunner.RunAsync("/usr/bin/pmset", ["-g", "assertions"], ct).ConfigureAwait(false);
        return Describe(settings is { ExitCode: 0 } ? settings.Output : null, assertions is { ExitCode: 0 } ? assertions.Output : null);
    }

    internal static string Describe(string? settings, string? assertions)
    {
        var sleep = Regex.Match(settings ?? "", @"(?m)^\s*sleep\s+(\d+)\b");
        var held = Regex.IsMatch(assertions ?? "", @"(?m)^\s*PreventUserIdleSystemSleep\s+[1-9]\d*\b");
        if (held && Regex.IsMatch(assertions ?? "", @"(?m)^.*\bPreventUserIdleSystemSleep\b[^\r\n]*""messagebus\.ai runner"""))
            return "The runner is keeping this Mac awake; the display may still sleep and manual sleep still works.";
        if (sleep.Success && sleep.Groups[1].Value == "0")
            return "Automatic idle system sleep is disabled; the display may still sleep and manual sleep still works.";
        if (held)
            return "Idle system sleep is currently prevented by a power assertion; the Mac may sleep when it is released. " + Advice;
        if (sleep.Success)
            return $"This Mac can sleep after {sleep.Groups[1].Value} idle minute(s), so queued tasks may wait. " + Advice;
        return "Couldn't determine the Mac's idle sleep settings from pmset. " + Advice;
    }
}

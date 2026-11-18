namespace MessageBus.Cli.Runner;

internal static class RunnerGit
{
    // Runner-owned git commands need long paths and must not start inherited hooks or fsmonitor.
    internal static string[] Arguments(IEnumerable<string> args) =>
        ["-c", "core.longpaths=true", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "-c", "core.fsmonitor=false", .. args];

    // Local transport clears GIT_CONFIG_PARAMETERS before starting upload-pack. Pass our fixed settings
    // explicitly so the source can read long object paths too; these arguments contain no shell metacharacters.
    internal static string LocalUploadPack => "--upload-pack=git " + string.Join(' ', Arguments(["upload-pack"]));
}

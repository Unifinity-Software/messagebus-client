using MessageBus.Cli.Runner;

namespace MessageBus.Cli.Tests.Runner;

// Exercise checkout and mirror logic without a network server. Only this test wrapper allows
// the local transport, after production has validated the task's remote address.
internal static class TestRepositoryRemote
{
    internal static ProcessLaunch Map(ProcessLaunch launch, string remote, string local) => launch with
    {
        Arguments = ["-c", $"url.{local.Replace('\\', '/')}.insteadOf={remote}", .. launch.Arguments],
        Environment = new Dictionary<string, string?>(launch.Environment) { ["GIT_ALLOW_PROTOCOL"] = "file" },
    };
}

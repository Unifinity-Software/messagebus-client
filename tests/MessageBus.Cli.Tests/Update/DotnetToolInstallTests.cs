using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class DotnetToolInstallTests
{
    [Theory]
    [InlineData("0.1.1-preview.1", null, " --prerelease")]
    [InlineData("0.1.1-preview.1+build.2", null, " --prerelease")]
    [InlineData("0.1.1", null, "")]
    [InlineData("0.1.1+build-with-hyphens", null, "")]
    [InlineData("0.1.1-preview.1", "0.2.0-preview.1", " --version 0.2.0-preview.1")]
    [InlineData("0.1.1", "0.2.0", " --version 0.2.0")]
    public void Updates_follow_the_running_channel_unless_a_version_is_pinned(string running, string? pinned, string suffix)
    {
        Assert.Equal($"dotnet tool update -g MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall("tools", true).UpdateCommand(pinned, running));
        Assert.Equal($"dotnet tool update --tool-path \"my tools\" MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall("my tools", false).UpdateCommand(pinned, running));
        Assert.Equal($"dotnet tool update MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall(null, false).UpdateCommand(pinned, running));
    }
}

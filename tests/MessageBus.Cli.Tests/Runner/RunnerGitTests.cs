using MessageBus.Cli.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunnerGitTests
{
    [Theory]
    [InlineData("https://git.example.test/team/app.git", true)]
    [InlineData("HTTPS://GIT.EXAMPLE.TEST/team/app.git", true)]
    [InlineData("ssh://git@git.example.test/team/app.git", true)]
    [InlineData("git@git.example.test:team/app.git", true)]
    [InlineData("https://reader@git.example.test/team/app.git", true)]
    [InlineData("https://reader:example-password@git.example.test/team/app.git", false)]
    public void Repository_addresses_allow_remote_transports_without_embedded_passwords(string address, bool accepted) =>
        Assert.Equal(accepted, RunnerGit.RepositoryUri(address) is not null);
}

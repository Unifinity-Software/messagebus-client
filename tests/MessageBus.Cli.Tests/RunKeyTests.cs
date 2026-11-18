using MessageBus.Cli.Configuration;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>the original issue: inside a task's run mbus never loads a machine key, whatever settings folder it is pointed at.</summary>
public sealed class RunKeyTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-run-key-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => RunWorkspace.TryDelete(_folder);

    [Fact]
    public void Inside_a_run_no_key_is_loaded()
    {
        new ConfigStore(_folder, "file").SaveKeyPem("-----BEGIN PRIVATE KEY-----\ntest\n-----END PRIVATE KEY-----\n");
        Func<string, string?> run = name => name == RunToolsVariables.File ? Path.Combine(_folder, "mbus-tools.json") : null;

        var store = new ConfigStore(_folder, "file", run);

        Assert.Equal(RunContext.KeyRefused, Assert.Throws<CliException>(() => store.LoadKeyPem()).Message);
    }
}

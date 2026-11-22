using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class DotnetMcpLauncherTests
{
    [Fact]
    public void Updating_preserves_active_dependencies_and_reclaims_them_only_after_the_session_ends()
    {
        using var machine = new TestMachine();
        var tools = Path.Combine(machine.Home, "tools & ' (MCP)");
        var first = Install(tools, "1.0.0");
        var connection = DotnetMcpLauncher.ForConnection(DotnetToolInstall.Find(first)!, first, "dotnet");
        connection.Install();
        var original = DotnetMcpLauncher.Acquire(tools);
        try
        {
            // A real tool update removes this entire store entry, including dependencies not yet loaded by MCP.
            Directory.Delete(Path.Combine(tools, ".store", "messagebus.ai.cli", "1.0.0"), recursive: true);
            var second = Install(tools, "2.0.0");
            var reconnected = DotnetMcpLauncher.ForConnection(DotnetToolInstall.Find(second)!, second, "dotnet");
            reconnected.Install(); // Reconnecting never overwrites the immutable launcher an existing agent uses.
            Assert.Equal(connection.Launch.Command, reconnected.Launch.Command);
            Assert.Equal(connection.Launch.Args, reconnected.Launch.Args);
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(connection.Directory, "mbus.dll")));
            using var current = DotnetMcpLauncher.Acquire(tools);
            DotnetMcpLauncher.CleanUp(tools, current.Directory);
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(original.Directory, "late", "dependency.dll")));
            Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(current.Directory, "mbus.dll")));
            using (var child = DotnetMcpLauncher.LeaseCurrentRuntime(original.Directory))
            {
                Assert.NotNull(child);
                original.Dispose(); // A surviving MCP child protects its dependencies even after its bootstrap exits.
                DotnetMcpLauncher.CleanUp(tools, current.Directory);
                Assert.True(File.Exists(Path.Combine(original.Directory, "late", "dependency.dll")));
            }
            original.Dispose();
            DotnetMcpLauncher.CleanUp(tools, current.Directory);
            Assert.False(Directory.Exists(original.Directory));
            Assert.True(File.Exists(Path.Combine(current.Directory, "late", "dependency.dll")));
            Assert.True(File.Exists(Path.Combine(connection.Directory, "mbus.dll")));
        }
        finally { original.Dispose(); }
    }

    [Fact]
    public async Task Simultaneous_connections_publish_one_complete_runtime_without_overwriting_a_running_copy()
    {
        using var machine = new TestMachine();
        var tools = Path.Combine(machine.Home, "tools");
        Install(tools, "1.0.0");
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => DotnetMcpLauncher.Acquire(tools), TestContext.Current.CancellationToken)));
        try
        {
            Assert.Single(leases.Select(lease => lease.Directory).Distinct());
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(leases[0].Directory, "late", "dependency.dll")));
            Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(leases[0].Directory)!));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public void An_incomplete_install_never_publishes_a_launcher_or_leaves_staging_files()
    {
        using var machine = new TestMachine();
        var tools = Path.Combine(machine.Home, "tools");
        var entry = Install(tools, "1.0.0");
        File.Delete(Path.Combine(Path.GetDirectoryName(entry)!, "mbus.runtimeconfig.json"));
        var connection = DotnetMcpLauncher.ForConnection(DotnetToolInstall.Find(entry)!, entry, "dotnet");
        Assert.Throws<IOException>(connection.Install);
        Assert.False(Directory.Exists(connection.Directory));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(connection.Directory)!));
    }

    private static string Install(string tools, string version)
    {
        var package = Path.Combine(tools, ".store", "messagebus.ai.cli", version, "messagebus.ai.cli", version);
        var program = Path.Combine(package, "tools", "net10.0", "any");
        Directory.CreateDirectory(Path.Combine(program, "late"));
        File.WriteAllText(Path.Combine(package, $"messagebus.ai.cli.{version}.nupkg.sha512"), version);
        File.WriteAllText(Path.Combine(program, DotnetToolInstall.SettingsFile), "settings");
        File.WriteAllText(Path.Combine(program, "mbus.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(program, "late", "dependency.dll"), version);
        var entry = Path.Combine(program, "mbus.dll");
        File.WriteAllText(entry, version);
        return entry;
    }
}

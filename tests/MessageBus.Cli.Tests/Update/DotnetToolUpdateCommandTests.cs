using System.Xml.Linq;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class DotnetToolUpdateCommandTests
{
    [Fact]
    public void Registered_paths_are_compared_to_this_install()
    {
        var tools = Path.Combine(Path.GetTempPath(), "mbus-install");
        var tool = new DotnetToolInstall(tools, false);
        Assert.True(DotnetToolUpdateCommand.UsesInstall(tool, Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus")));
        Assert.True(DotnetToolUpdateCommand.UsesInstall(tool, Path.Combine(tools, ".store", "messagebus.ai.cli", "1", "mbus.dll")));
        Assert.False(DotnetToolUpdateCommand.UsesInstall(tool, Path.Combine(tools + "-other", "mbus")));
        Assert.False(DotnetToolUpdateCommand.UsesInstall(tool, Path.Combine(tools, ".store", "other", "mbus.dll")));
    }

    [Theory]
    [InlineData(true, "cli-wait-123")]
    [InlineData(false, "cli")]
    public void Applier_arguments_preserve_paths_and_wait_for_the_windows_parent(bool windows, string mode) =>
        Assert.Equal(["/private folder/mbus.dll", "__runner-update", mode, "/settings folder/update"],
            DotnetToolUpdateCommand.ApplierArguments(windows, 123, "/private folder/mbus.dll", "/settings folder/update"));

    [Theory]
    [InlineData("y", true, false, true)]
    [InlineData("YES", true, false, true)]
    [InlineData("n", true, false, false)]
    [InlineData("", true, false, false)]
    [InlineData("", false, false, false)]
    [InlineData("", false, true, true)]
    public async Task Answers_and_terminal_control_handoff(string answer, bool terminal, bool yes, bool expected)
    {
        using var machine = new TestMachine { Input = answer };
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Other, "user", 0, (_, _) => throw new Exception("No service changes"));
        // A runner lock for an unrelated install must not refuse this temporary install.
        using var unrelatedRunner = RunnerInstance.TryAcquire(machine.MbusFolder, out _);
        machine.InstallAgent("dotnet", "not run");
        var tools = Path.Combine(machine.Home, "tools & ' (test)");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus"), "shim");
        var tool = DotnetToolInstallTests.Install(tools);
        var entry = DotnetToolInstallTests.WriteRecoverableTool(tool, "private program");
        var source = Path.Combine(machine.Home, "feed");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "MessageBus.Ai.Cli.2.0.0.nupkg"), "new package");
        machine.Env["MBUS_UPDATE_NUGET_SOURCE"] = source;
        var output = new StringWriter();
        var called = false;
        var result = await DotnetToolUpdateCommand.RunAsync(machine.CreateHost(output, terminal: terminal), tool, null, null, false, yes,
            TestContext.Current.CancellationToken, (dotnet, assembly, root) =>
            {
                called = true;
                Assert.Equal(Path.Combine(machine.Bin, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet"), dotnet);
                Assert.Equal("private program", File.ReadAllText(assembly));
                var attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(root, "attempt.json"))!;
                Assert.Equal("2.0.0", attempt.To);
                Assert.False(attempt.Pinned);
                Assert.Equal(tools, attempt.Tools);
                var savedPackage = Path.Combine(root, "recovery", "MessageBus.Ai.Cli.1.0.0.nupkg");
                Assert.Equal(File.ReadAllBytes(Directory.GetFiles(tools, "*.nupkg", SearchOption.AllDirectories).Single()), File.ReadAllBytes(savedPackage));
                var sources = XDocument.Load(Path.Combine(root, "NuGet.Config")).Root!.Element("packageSources")!;
                Assert.Equal("clear", sources.Elements().First().Name.LocalName);
                Assert.Equal(source, sources.Element("add")!.Attribute("value")!.Value);
                return 0;
            }, "1.0.0", entry);
        Assert.Equal(0, result);
        Assert.Equal(expected, called);
        Assert.Contains("mbus 2.0.0 is available", output.ToString());
        if (terminal && !yes) Assert.Contains($"Update now with {tool.UpdateCommand("2.0.0")}? [y/N]", output.ToString());
        else Assert.DoesNotContain("[y/N]", output.ToString());
        if (!terminal && !yes) Assert.Contains("Run: " + tool.UpdateCommand("2.0.0"), output.ToString());
        if (!expected) Assert.False(Directory.Exists(Path.Combine(machine.MbusFolder, "manual-updates")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Holders_and_runner_refuse_even_with_yes(bool runner)
    {
        using var machine = new TestMachine();
        var tools = Path.Combine(machine.Home, "tools");
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        var records = runner ? ProcessTable.Empty : ProcessTable.Of([new(Environment.ProcessId, "self", 0, null, shim, 1, "user"), new(42, "mbus mcp", Environment.ProcessId, null, shim, 2, "user")]);
        var manager = new ServiceManager(ServicePlatform.MacOs, "user", 501, (_, _) => throw new Exception(), processes: records);
        machine.RunnerServices = () => manager;
        if (runner)
        {
            var plan = RunnerServiceDefinitions.Plan(manager, [shim], [], machine.Home, machine.MbusFolder, new Dictionary<string, string>());
            Directory.CreateDirectory(Path.GetDirectoryName(plan.DefinitionPath)!);
            File.WriteAllText(plan.DefinitionPath, plan.Definition);
        }
        using var held = runner ? RunnerInstance.TryAcquire(machine.MbusFolder, out _) : null;
        var ex = await Assert.ThrowsAsync<CliException>(() => DotnetToolUpdateCommand.RunAsync(machine.CreateHost(),
            DotnetToolInstallTests.Install(tools), "2.0.0", null, false, true, TestContext.Current.CancellationToken,
            (_, _, _) => throw new Exception("Must not hand off"), "1.0.0"));
        if (runner) Assert.Equal(DotnetToolUpdateCommand.RunnerMessage, ex.Message);
        else { Assert.Contains("1 processes", ex.Message); Assert.Contains("42 mbus mcp", ex.Message); }
        Assert.False(Directory.Exists(Path.Combine(machine.MbusFolder, "manual-updates")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registered_service_in_another_settings_folder_is_only_refused_when_running(bool running)
    {
        using var machine = new TestMachine();
        var other = Path.Combine(machine.Home, "other-settings");
        using var held = running ? RunnerInstance.TryAcquire(other, out _) : null;
        var manager = new ServiceManager(ServicePlatform.MacOs, "user", 501, (_, _) => throw new Exception("No service changes"));
        machine.RunnerServices = () => manager;
        var plan = RunnerServiceDefinitions.Plan(manager, [Path.Combine(machine.Bin, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus")], [], machine.Home, other, new Dictionary<string, string>());
        Directory.CreateDirectory(Path.GetDirectoryName(plan.DefinitionPath)!);
        File.WriteAllText(plan.DefinitionPath, plan.Definition);
        if (!running)
        {
            var output = new StringWriter();
            Assert.Equal(0, await DotnetToolUpdateCommand.RunAsync(machine.CreateHost(output, terminal: true),
                DotnetToolInstallTests.Install(machine.Bin), "2.0.0", null, false, false, TestContext.Current.CancellationToken,
                (_, _, _) => throw new Exception("Must not hand off"), "1.0.0"));
            Assert.Contains("[y/N]", output.ToString());
            return;
        }
        var ex = await Assert.ThrowsAsync<CliException>(() => DotnetToolUpdateCommand.RunAsync(machine.CreateHost(),
            DotnetToolInstallTests.Install(machine.Bin), "2.0.0", null, false, true, TestContext.Current.CancellationToken,
            (_, _, _) => throw new Exception("Must not hand off"), "1.0.0"));
        Assert.Equal(DotnetToolUpdateCommand.RunnerMessage, ex.Message);
    }

    [Theory]
    [InlineData("1.0.0", "up to date")]
    [InlineData("2.0.0", "is available")]
    public async Task Check_reads_local_feed_without_service_or_applier(string version, string message)
    {
        using var machine = new TestMachine();
        var source = Path.Combine(machine.Home, "feed");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, $"MessageBus.Ai.Cli.{version}.nupkg"), "package");
        machine.Env["MBUS_UPDATE_NUGET_SOURCE"] = source;
        var output = new StringWriter();
        Assert.Equal(0, await DotnetToolUpdateCommand.RunAsync(machine.CreateHost(output), DotnetToolInstallTests.Install(machine.Bin), null, null, true, true,
            TestContext.Current.CancellationToken, (_, _, _) => throw new Exception(), "6.0.0+dev.012abcd"));
        Assert.Contains(message, output.ToString());
    }
}

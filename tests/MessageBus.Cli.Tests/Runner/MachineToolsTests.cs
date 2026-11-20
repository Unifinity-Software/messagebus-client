using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class MachineToolsTests
{
    [Fact]
    public async Task Podman_and_Blender_report_versions_and_separate_capabilities()
    {
        using var machine = new TestMachine();
        machine.InstallFake("podman", new("Unexpected argument", 1),
            new Dictionary<string, FakeAnswer> { ["--version"] = new("podman version 5.2.2") });
        machine.InstallFake("blender", new("Unexpected argument", 1),
            new Dictionary<string, FakeAnswer> { ["--version"] = new("Blender 4.2.1\nbuild date: 2024.09.02") });

        var tools = await MachineTools.CheckAsync(machine.Env.GetValueOrDefault, [], TestContext.Current.CancellationToken);

        Assert.Equal("5.2.2", tools.Single(tool => tool.Name == "podman").Version);
        Assert.Equal("4.2.1", tools.Single(tool => tool.Name == "blender").Version);
        Assert.Equal(["tool:podman", "tool:blender"], MachineTools.Capabilities(tools));
    }

    [Fact]
    public async Task Missing_tools_and_Blender_build_metadata_do_not_report_capabilities()
    {
        using var machine = new TestMachine();
        var missing = await MachineTools.CheckAsync(machine.Env.GetValueOrDefault, [], TestContext.Current.CancellationToken);
        Assert.False(missing.Single(tool => tool.Name == "podman").Found);
        Assert.False(missing.Single(tool => tool.Name == "blender").Found);
        Assert.Empty(MachineTools.Capabilities(missing));

        machine.InstallFake("blender", new("Blender\nbuild date: 2024.09.02"));
        var tools = await MachineTools.CheckAsync(machine.Env.GetValueOrDefault, [], TestContext.Current.CancellationToken);
        Assert.True(tools.Single(tool => tool.Name == "blender").Found);
        Assert.Empty(MachineTools.Capabilities(tools));
    }

    [Fact]
    public void Windows_Blender_installs_are_found_off_PATH_in_version_order()
    {
        using var machine = new TestMachine();
        var programFiles = Path.Combine(machine.Home, "Program Files");
        machine.Env["ProgramFiles"] = programFiles;
        var older = Install("4.9");
        var newer = Install("4.10");
        Directory.CreateDirectory(Path.Combine(programFiles, "Blender Foundation", "Blender 5.0"));

        Assert.Equal([newer, older], ToolPaths.WindowsBlenderDirectories(machine.Env.GetValueOrDefault));
        if (OperatingSystem.IsWindows())
        {
            Func<string, string?> env = machine.Env.GetValueOrDefault;
            var path = ToolPaths.RunVariables(machine.Home, env)["PATH"];
            Assert.Equal(Path.Combine(newer, "blender.exe"),
                CommandLocator.Find("blender", name => name == "PATH" ? path : env(name), []));
        }

        string Install(string version)
        {
            var folder = Directory.CreateDirectory(Path.Combine(programFiles, "Blender Foundation", "Blender " + version)).FullName;
            File.WriteAllText(Path.Combine(folder, "blender.exe"), "");
            return folder;
        }
    }
}

using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class MacGitSetupTests
{
    [Fact]
    public async Task Clean_Mac_has_no_Git_path_for_startup_probes_identity_or_housekeeping()
    {
        var system = new FakeMacToolSetupSystem();
        Assert.Null(await MacGitSetup.InstalledGitPathAsync("/usr/bin/git", TestContext.Current.CancellationToken, system));
        Assert.Equal("/usr/bin/xcode-select", Assert.Single(system.ReadCalls).Executable);
        Assert.Empty(system.InstallCalls);
    }

    [Fact]
    public async Task Installed_Mac_uses_the_selected_developer_Git_instead_of_the_launcher()
    {
        var system = new FakeMacToolSetupSystem { Installed = true };
        Assert.Equal("/Library/Developer/CommandLineTools/usr/bin/git",
            await MacGitSetup.InstalledGitPathAsync("/usr/bin/git", TestContext.Current.CancellationToken, system));
        Assert.Empty(system.InstallCalls);
    }

    [Theory]
    [InlineData(true, "/opt/homebrew/bin/git")]
    [InlineData(false, "C:\\Program Files\\Git\\cmd\\git.exe")]
    public async Task Independent_Git_does_not_require_Apple_developer_tools(bool mac, string path)
    {
        var system = new FakeMacToolSetupSystem { IsMacOs = mac };
        Assert.Equal(path, await MacGitSetup.InstalledGitPathAsync(path, TestContext.Current.CancellationToken, system));
        Assert.Empty(system.ReadCalls);
        Assert.Empty(system.InstallCalls);
    }

    [Fact]
    public async Task Clean_Mac_readiness_does_not_execute_the_installer_launching_git_stub()
    {
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem();
        var setup = new MacGitSetup(machine.CreateHost(), system);
        Assert.Null(await setup.GitVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/usr/bin/xcode-select", Assert.Single(system.ReadCalls).Executable);
        Assert.Empty(system.InstallCalls);
    }

    [Theory]
    [InlineData(0, 501)]
    [InlineData(502, 501)]
    [InlineData(501, null)]
    public async Task Root_or_a_non_console_user_cannot_plan_or_launch_an_authorization_helper(int effective, int? console)
    {
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem { EffectiveUid = (uint)effective, ConsoleUid = console is { } id ? (uint)id : null };
        var setup = new MacGitSetup(machine.CreateHost(), system);
        Assert.NotNull(setup.DisabledReason);
        await Assert.ThrowsAsync<CliException>(() => setup.ResolvePlanAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CliException>(() => setup.InstallAsync(FakeMacToolSetupSystem.Plan, TestContext.Current.CancellationToken));
        Assert.Empty(system.ReadCalls);
        Assert.Empty(system.InstallCalls);
    }

    [Fact]
    public async Task A_mac_in_a_sandbox_cannot_plan_or_install()
    {
        // the original issue: the sandbox check asks the Mac being set up, so tests on a fake Mac run inside a run's sandbox too.
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem { InSandbox = true };
        var setup = new MacGitSetup(machine.CreateHost(), system);
        Assert.Equal("Tool setup cannot run inside an agent task or sandbox.", setup.DisabledReason);
        await Assert.ThrowsAsync<CliException>(() => setup.InstallAsync(FakeMacToolSetupSystem.Plan, TestContext.Current.CancellationToken));
        Assert.Empty(system.InstallCalls);
    }

    [Fact]
    public async Task Planning_uses_only_the_exact_stable_Apple_label_and_cleans_its_catalog_marker()
    {
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem();
        var plan = await new MacGitSetup(machine.CreateHost(), system).ResolvePlanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FakeMacToolSetupSystem.Plan, plan);
        Assert.False(system.MarkerOpen);
        Assert.Equal("/usr/sbin/softwareupdate", Assert.Single(system.ReadCalls).Executable);
        Assert.Equal(["--list"], system.ReadCalls[0].Arguments);
        Assert.Empty(system.InstallCalls);
    }

    [Fact]
    public async Task Mac_installer_refuses_Windows_or_modified_catalog_plans_before_any_native_authorization()
    {
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem();
        var setup = new MacGitSetup(machine.CreateHost(), system);
        await Assert.ThrowsAsync<CliException>(() => setup.InstallAsync(new() { Version = "2.51.0" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CliException>(() => setup.InstallAsync(FakeMacToolSetupSystem.Plan with { Version = "26.2" }, TestContext.Current.CancellationToken));
        Assert.Empty(system.InstallCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Install_keeps_the_CLT_catalog_available_until_native_execution_finishes(int outcome)
    {
        using var machine = Machine();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = new FakeMacToolSetupSystem { InstallCompletion = completion };
        var setup = new MacGitSetup(machine.CreateHost(), system);
        await setup.ResolvePlanAsync(TestContext.Current.CancellationToken);
        Assert.False(system.MarkerOpen);

        var installing = setup.InstallAsync(FakeMacToolSetupSystem.Plan, TestContext.Current.CancellationToken);
        Assert.False(installing.IsCompleted);
        Assert.True(system.MarkerOpen); // softwareupdate rescans the catalog after native authorization.
        if (outcome < 0)
        {
            completion.SetException(new IOException("Native installer failed."));
            await Assert.ThrowsAsync<IOException>(() => installing);
        }
        else
        {
            completion.SetResult(outcome);
            Assert.Equal(outcome, await installing);
        }
        Assert.False(system.MarkerOpen);
    }

    [Fact]
    public async Task A_working_git_does_not_verify_a_different_Apple_package_version()
    {
        using var machine = Machine();
        var system = new FakeMacToolSetupSystem { Installed = true, Receipt = "package-id: com.apple.pkg.CLTools_Executables\nversion: 26.2.0.0.1.1760670222\n" };
        var setup = new MacGitSetup(machine.CreateHost(), system);
        var git = await setup.GitVersionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2.50.1", git);
        Assert.False(await setup.VerifyInstalledAsync(FakeMacToolSetupSystem.Plan, git, TestContext.Current.CancellationToken));
        system.Receipt = "package-id: com.apple.pkg.CLTools_Executables\nversion: 26.1.0.0.1.1760670222\n";
        Assert.True(await setup.VerifyInstalledAsync(FakeMacToolSetupSystem.Plan, git, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Catalog_marker_cleanup_preserves_existing_files_and_removes_only_a_marker_it_created()
    {
        using var machine = Machine();
        var existing = machine.WriteHomeFile("existing-marker", "another process owns this marker");
        using (new NativeMacToolSetupSystem.CatalogMarker(existing)) { }
        Assert.Equal("another process owns this marker", File.ReadAllText(existing));
        var owned = Path.Combine(machine.Home, "owned-marker");
        using (new NativeMacToolSetupSystem.CatalogMarker(owned)) Assert.True(File.Exists(owned));
        Assert.False(File.Exists(owned));
    }

    internal static TestMachine Machine()
    {
        var machine = new TestMachine();
        machine.Env[WindowsGitSetup.OptInVariable] = "on";
        machine.Env[DeploymentTargets.OverrideVariable] = "pc";
        return machine;
    }
}

internal sealed class FakeMacToolSetupSystem : IMacToolSetupSystem
{
    public static ToolSetupPlan Plan { get; } = new() { Os = MachineOs.MacOs, PackageLabel = "Command Line Tools for Xcode-26.1", Version = "26.1" };
    public bool IsMacOs { get; set; } = true;
    public bool InSandbox { get; set; }
    public uint EffectiveUid { get; set; } = 501;
    public uint? ConsoleUid { get; set; } = 501;
    public bool Installed { get; set; }
    public bool MarkerOpen { get; private set; }
    public TaskCompletionSource<int>? InstallCompletion { get; init; }
    public string Receipt { get; set; } = "package-id: com.apple.pkg.CLTools_Executables\nversion: 26.1.0.0.1.1760670222\n";
    public List<(string Executable, string[] Arguments)> ReadCalls { get; } = [];
    public List<(string Executable, string[] Arguments)> InstallCalls { get; } = [];
    public bool FileExists(string path) => Installed && path == "/Library/Developer/CommandLineTools/usr/bin/git";
    public IDisposable CreateCatalogMarker() { MarkerOpen = true; return new DisposeAction(() => MarkerOpen = false); }
    public Task<CommandResult?> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        ReadCalls.Add((executable, arguments));
        var result = executable switch
        {
            "/usr/bin/xcode-select" => new CommandResult(Installed ? 0 : 2, Installed ? "/Library/Developer/CommandLineTools\n" : "", ""),
            "/usr/sbin/softwareupdate" => new(0, "Software Update found:\n * Label: Command Line Tools for Xcode-16.2\n * Label: macOS Tahoe-26.2\n * Label: Command Line Tools for Xcode-27.0 beta\n * Label: Command Line Tools for Xcode-26.1\n", ""),
            "/Library/Developer/CommandLineTools/usr/bin/git" => new(0, "git version 2.50.1 (Apple Git-155)\n", ""),
            "/usr/sbin/pkgutil" => new(0, Receipt, ""),
            _ => throw new InvalidOperationException("Unexpected or installer-launching command: " + executable),
        };
        return Task.FromResult<CommandResult?>(result);
    }
    public Task<int> InstallAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        InstallCalls.Add((executable, arguments));
        Installed = true;
        return InstallCompletion?.Task ?? Task.FromResult(0);
    }
    private sealed class DisposeAction(Action action) : IDisposable { public void Dispose() => action(); }
}

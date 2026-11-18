using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class ToolInstallHoldersTests
{
    [Theory]
    [InlineData("exec")]
    [InlineData("--roll-forward Major")]
    [InlineData("--roll-forward-on-no-candidate-fx 2")]
    [InlineData("--runtimeconfig config.json")]
    [InlineData("--depsfile deps.json")]
    [InlineData("--additionalprobingpath packages")]
    [InlineData("--fx-version 10.0.0")]
    [InlineData("--additional-deps deps.json")]
    [InlineData("exec --roll-forward Major --depsfile deps.json")]
    [InlineData("--roll-forward=Major")]
    public void Dotnet_host_options_do_not_hide_the_assembly(string options)
    {
        var tools = Path.Combine(Path.GetTempPath(), "host tools");
        var dll = Path.Combine(tools, ".store", "messagebus.ai.cli", "1", "mbus.dll");
        var dotnet = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var table = ProcessTable.Of([new(10, "runner", 0, null, null, 1, "u"),
            new(20, "dotnet", 0, $"dotnet {options} \"{dll}\" mcp", dotnet, 2, "u")]);
        Assert.Single(ToolInstallHolders.FinalCheck(new(tools, false), table, 10, new HashSet<int> { 10 }));
    }

    [Theory]
    [InlineData("shim", true, true)]
    [InlineData("store apphost", true, true)]
    [InlineData("dotnet dll", true, true)]
    [InlineData("another tools folder", false, false)]
    [InlineData("argument substring", false, false)]
    [InlineData("store prefix", false, false)]
    [InlineData("later argument", false, false)]
    [InlineData("relative assembly", false, false)]
    [InlineData("canonical path", true, true)]
    [InlineData("case", true, true)]
    [InlineData("other user", false, false)]
    [InlineData("descendant", false, true)]
    [InlineData("grandchild", false, true)]
    [InlineData("ancestor", true, true)]
    [InlineData("exiting child", false, false)]
    [InlineData("reused parent id", true, true)]
    [InlineData("runner", false, false)]
    public void Finds_only_this_installs_holders_with_the_requested_exclusions(string scenario, bool waiting, bool final)
    {
        var tools = Path.Combine(Path.GetTempPath(), "holder tools & ' (test)");
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        var store = Path.Combine(tools, ".store", "messagebus.ai.cli");
        var apphost = Path.Combine(store, "1.0", "tools", "mbus");
        var dll = Path.Combine(store, "1.0", "tools", "mbus.dll");
        var dotnet = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var candidate = new ProcessRecord(30, "holder", 0, null, shim, 30, "501");
        candidate = scenario switch
        {
            "store apphost" => candidate with { ExecutablePath = apphost },
            "dotnet dll" => candidate with { ExecutablePath = dotnet, CommandLine = $"\"{dotnet}\" --diagnostics \"{dll}\" mcp" },
            "another tools folder" => candidate with { ExecutablePath = Path.Combine(tools + "-other", "mbus") },
            "argument substring" => candidate with { ExecutablePath = dotnet, CommandLine = $"dotnet unrelated.dll --message=\"{dll}\"" },
            "store prefix" => candidate with { ExecutablePath = Path.Combine(store + "-other", "mbus") },
            "later argument" => candidate with { ExecutablePath = dotnet, CommandLine = $"dotnet unrelated.dll \"{dll}\"" },
            "relative assembly" => candidate with { ExecutablePath = dotnet, CommandLine = "dotnet .store/messagebus.ai.cli/mbus.dll" },
            "canonical path" => candidate with { ExecutablePath = Path.Combine(store, "..", "messagebus.ai.cli", "mbus") },
            "case" => candidate with { ExecutablePath = shim.ToUpperInvariant() },
            "other user" => candidate with { Owner = "502" },
            "descendant" or "exiting child" => candidate with { ParentPid = 10 },
            "grandchild" => candidate with { ParentPid = 20 },
            "ancestor" => candidate with { Pid = 5, StartTicks = 5 },
            "reused parent id" => candidate with { ParentPid = 10, StartTicks = 1 },
            "runner" => candidate with { Pid = 10, ParentPid = 5, StartTicks = 10 },
            _ => candidate,
        };
        if (scenario == "case" && OperatingSystem.IsLinux()) waiting = final = false;
        var records = new List<ProcessRecord>
        {
            new(10, "runner", 5, null, shim, 10, "501"),
            new(20, "agent", 10, null, null, 20, "501"),
        };
        if (scenario == "runner") records.RemoveAt(0);
        records.Add(candidate);
        var table = ProcessTable.Of(records);
        var install = new DotnetToolInstall(tools, false);
        var waitingHolders = ToolInstallHolders.WhileWaiting(install, table, 10);
        var finalHolders = ToolInstallHolders.FinalCheck(install, table, 10,
            scenario == "exiting child" ? new HashSet<int> { 10, 30 } : new HashSet<int> { 10 });
        Assert.Equal(waiting ? [new ToolInstallHolder(candidate.Pid, "holder")] : [], waitingHolders);
        Assert.Equal(final ? [new ToolInstallHolder(candidate.Pid, "holder")] : [], finalHolders);
    }
}

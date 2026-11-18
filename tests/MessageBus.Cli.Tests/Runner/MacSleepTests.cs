using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class MacSleepTests
{
    [Theory]
    [InlineData(null, null, RunnerKeepAwake.Always)]
    [InlineData("tasks", null, RunnerKeepAwake.Tasks)]
    [InlineData("off", "always", RunnerKeepAwake.Always)]
    [InlineData("always", "off", RunnerKeepAwake.Off)]
    public void Flag_overrides_environment(string? environment, string? flag, RunnerKeepAwake expected)
    {
        var options = RunnerOptions.FromEnvironment(key => key == RunnerEnvVars.KeepAwake ? environment : null, Path.GetTempPath(), keepAwake: flag);
        Assert.Equal(expected, options.KeepAwake);
    }

    [Fact]
    public void Invalid_policy_names_the_option_and_environment_variable()
    {
        var error = Assert.Throws<FormatException>(() => RunnerOptions.ParseKeepAwake("sometimes"));
        Assert.Contains("--keep-awake", error.Message);
        Assert.Contains(RunnerEnvVars.KeepAwake, error.Message);
    }

    [Theory]
    [InlineData(" sleep 0\n displaysleep 10", "", "disabled")]
    [InlineData(" sleep 5 (sleep prevented by mbus)\n", " PreventUserIdleSystemSleep 1\n", "currently prevented")]
    [InlineData(" sleep 5\n", " PreventUserIdleSystemSleep 0\n PreventUserIdleDisplaySleep 1\n", "after 5 idle minute")]
    [InlineData(null, null, "Couldn't determine")]
    [InlineData(" sleep 5\n", " PreventUserIdleSystemSleep 1\n pid 123(mbus): [0x123] PreventUserIdleSystemSleep named: \"messagebus.ai runner\"\n", "The runner is keeping this Mac awake")]
    public void Doctor_explains_pmset_output(string? settings, string? assertions, string expected)
    {
        var result = MacSleepSettings.Describe(settings, assertions);
        Assert.Contains(expected, result);
        if (!result.Contains("disabled") && !result.StartsWith("The runner")) Assert.Contains("mbus runner --keep-awake always", result);
        else Assert.DoesNotContain("mbus runner --keep-awake always", result);
    }

    [Theory]
    [InlineData(RunnerKeepAwake.Always, 1)]
    [InlineData(RunnerKeepAwake.Tasks, 0)]
    [InlineData(RunnerKeepAwake.Off, 0)]
    public void Assertion_follows_policy_and_is_released_once(RunnerKeepAwake mode, int initial)
    {
        var acquired = 0;
        var released = 0;
        var protection = new MacSleepProtection(mode, () => { acquired++; return new Release(() => released++); }, TextWriter.Null);
        Assert.Equal(initial, acquired);
        protection.SetWorking(true);
        protection.SetWorking(true); // another task starts, same assertion
        Assert.Equal(mode == RunnerKeepAwake.Off ? 0 : 1, acquired);
        protection.SetWorking(false);
        Assert.Equal(mode == RunnerKeepAwake.Tasks ? 1 : 0, released);
        protection.Dispose();
        protection.Dispose();
        protection.SetWorking(true); // late completion after shutdown cannot reacquire
        Assert.Equal(acquired, released);
    }

    private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
}

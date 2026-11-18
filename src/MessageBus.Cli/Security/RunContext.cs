namespace MessageBus.Cli.Security;

/// <summary>
/// Whether this mbus was started from inside a task's run (the original issue), where it must never sign in as the machine: the run
/// reaches the server only through the runner, with its own short-lived credential (the original issue).
/// </summary>
public static class RunContext
{
    /// <summary>What mbus says when asked for the machine key inside a run.</summary>
    public const string KeyRefused =
        "mbus is running inside a task's run or a sandbox, so it won't use this machine's key. A task's mbus tools reach the server through the runner; run other mbus commands outside the sandbox.";

    /// <summary>
    /// True when the run's tools are set for this process (<see cref="Runner.RunToolsVariables.File"/>: runs isolated
    /// from the machine get it), or when macOS has this process in a sandbox: mbus itself never runs sandboxed, so a
    /// sandboxed mbus was started by a run, and a run can't take its sandbox off the way it can clear a variable.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    public static bool IsInsideRun(Func<string, string?> getEnvironmentVariable) =>
        IsInsideRun(getEnvironmentVariable, OperatingSystem.IsMacOS() && MacSecurity.InSandbox());

    /// <summary><see cref="IsInsideRun(Func{string, string?})"/>, with whether this process is sandboxed already known (a fake machine in tests says no).</summary>
    public static bool IsInsideRun(Func<string, string?> getEnvironmentVariable, bool sandboxed) =>
        getEnvironmentVariable(Runner.RunToolsVariables.File) is { Length: > 0 } || sandboxed;
}

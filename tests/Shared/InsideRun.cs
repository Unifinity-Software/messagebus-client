using MessageBus.Cli.Security;

namespace MessageBus.Tests.Shared;

/// <summary>
/// the original issue: whether these tests themselves run inside a task's run (its tools are set, or macOS has them in a sandbox).
/// There mbus refuses this machine's key and every push is refused, by design: anything a test could do inside a run, a
/// task could too. So tests that start the real mbus commands or push skip there; they run outside a run and in CI.
/// </summary>
internal static class InsideRun
{
    public static bool Now { get; } = RunContext.IsInsideRun(Environment.GetEnvironmentVariable);

    public const string MbusReason = "mbus refuses keys inside a task's run (by design); runs outside a run and in CI.";

    public const string MachineKeyReason = "needs this computer's key, which runs can't use; runs outside a run and in CI.";

    public const string PushReason = "every push is refused inside a task's run (by design); runs outside a run and in CI.";
}

using MessageBus.Cli.Security;

namespace MessageBus.Cli.Runner;

/// <summary>Waits for the existing identity without enrolling or taking work.</summary>
internal static class RunnerKeyWait
{
    public static async Task WaitAsync(Func<string?> load, string store, TextWriter log, TimeProvider time, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var delay = TimeSpan.FromSeconds(15);
        var failed = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ = load(); // A missing key keeps the command's normal not-signed-in path.
                if (failed)
                    await log.WriteLineAsync($"The machine key is readable again after {(time.GetUtcNow() - started).TotalMinutes:0.##} minutes; the login Keychain was locked or interaction was not allowed.");
                return;
            }
            catch (MachineKeyUnavailableException)
            {
                failed = true;
                await log.WriteLineAsync($"Couldn't read the machine key ({store}): the login Keychain is locked or interaction is not allowed. Retrying in {delay.TotalSeconds:0} seconds ({time.GetUtcNow().Add(delay):O}).");
                await Task.Delay(delay, time, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }
}

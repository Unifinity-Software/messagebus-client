namespace MessageBus.Cli.Runner.Service;

/// <summary>Confirms that a service start produced a running instance.</summary>
public static class RunnerStartup
{
    /// <summary>How long an installed runner gets to take its instance lock.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for the instance lock, rather than trusting service registration or an old log entry.
    /// </summary>
    /// <param name="directory">The runner's settings folder.</param>
    /// <param name="time">The clock (fake in tests).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public static async Task<bool> WaitAsync(string directory, TimeProvider time, CancellationToken cancellationToken)
    {
        var start = time.GetTimestamp();
        // Service startup has no event to subscribe to, so probe for a bounded time.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunnerInstance.IsHeld(directory))
            {
                return true;
            }
            var remaining = StartWait - time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), time, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

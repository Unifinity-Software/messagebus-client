using System.Globalization;

namespace MessageBus.Cli.Runner.Service;

/// <summary>
/// One runner per settings folder, and a stop request that sticks (the original issue).
/// <list type="bullet">
/// <item>A runner holds <see cref="LockFile"/> open exclusively for as long as it runs. A second runner finds it held and
/// exits 0, which also ends a restart loop that started it (the Windows task's loop restarts only after a non-zero exit;
/// launchd's KeepAlive the same). Its process (id, start time and file), and on Windows the restart loop's, are in
/// <see cref="PidFile"/> (on macOS and Linux an exclusively locked file can't be read by another .NET process), so a
/// stop ends only those processes, and only while they are still the same ones.</item>
/// <item><see cref="StopFile"/> asks every runner here to stop: a running one notices within
/// <see cref="StopCheckInterval"/> and stops as on Ctrl+C (running tasks go back to the queue, exit 0), and one that a
/// service manager starts again exits 0 at once, so the loop ends. It stays until <c>mbus runner install</c> or a runner
/// started by hand removes it.</item>
/// </list>
/// </summary>
public sealed class RunnerInstance : IDisposable
{
    /// <summary>Held open by the running runner.</summary>
    public const string LockFile = "runner.lock";

    /// <summary>The running runner's process id and, on Windows, the restart loop's (<c>cmd</c>) after it.</summary>
    public const string PidFile = "runner.pid";

    /// <summary>The stop request.</summary>
    public const string StopFile = "runner.stop";

    /// <summary>How often a running runner looks for the stop request.</summary>
    public static readonly TimeSpan StopCheckInterval = TimeSpan.FromSeconds(2);

    private readonly FileStream _lock;
    private readonly string _directory;

    private RunnerInstance(FileStream held, string directory)
    {
        _lock = held;
        _directory = directory;
    }

    /// <summary>How often, and how many times in all, <see cref="AcquireAsync"/> tries the lock before it counts as held.</summary>
    public static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromMilliseconds(100);

    private const int AcquireAttempts = 3;

    /// <summary>
    /// Takes the lock for this process, trying again shortly (a stop command checking it holds it for a moment), or returns
    /// who holds it when another runner has it.
    /// </summary>
    /// <param name="directory">mbus's settings folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="processes">This machine's processes: what is written down to recognise this runner (and its restart loop) later.</param>
    /// <param name="installs">This mbus's file on PATH, for finding the restart loop past it.</param>
    /// <param name="supervised">Started by a service manager (<c>--log-file</c>): only then is a restart loop looked for.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public static async Task<(RunnerInstance? Instance, RunnerHolder? Holder)> AcquireAsync(string directory, TimeProvider time, ProcessTable processes,
        IReadOnlyCollection<string> installs, bool supervised, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (TryAcquire(directory, out var holder, processes, installs, supervised) is { } instance)
            {
                return (instance, null);
            }
            if (attempt >= AcquireAttempts)
            {
                return (null, holder);
            }
            await Task.Delay(AcquireRetryDelay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes the lock for this process once, or returns null (and who holds it) when another runner has it.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    /// <param name="holder">The runner that holds it, when known.</param>
    /// <param name="processes">This machine's processes; null writes nothing down (tests holding the lock).</param>
    /// <param name="installs">This mbus's file on PATH.</param>
    /// <param name="supervised">Started by a service manager.</param>
    public static RunnerInstance? TryAcquire(string directory, out RunnerHolder? holder, ProcessTable? processes = null, IReadOnlyCollection<string>? installs = null,
        bool supervised = false)
    {
        Directory.CreateDirectory(directory);
        if (Open(directory) is not { } held)
        {
            holder = ReadHolder(directory);
            return null;
        }
        holder = null;
        var self = processes?.Get(Environment.ProcessId)?.Identity;
        var loop = processes is not null && OperatingSystem.IsWindows() ? RunnerProcesses.RestartLoop(Environment.ProcessId, processes, installs ?? [], supervised)?.Identity : null;
        if (self is null || !TryWritePidFile(directory, new RunnerHolder(self, loop)))
        {
            // A stale file must not name a process that isn't this runner.
            DeletePidFile(directory);
        }
        return new RunnerInstance(held, directory);
    }

    /// <summary>Writes <paramref name="holder"/> to <see cref="PidFile"/>; false when it can't.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    /// <param name="holder">This runner and its restart loop.</param>
    public static bool TryWritePidFile(string directory, RunnerHolder holder)
    {
        var lines = new List<string> { Line("runner", holder.Runner) };
        if (holder.Loop is { } loop)
        {
            lines.Add(Line("loop", loop));
        }
        try
        {
            File.WriteAllText(Path.Combine(directory, PidFile), string.Join('\n', lines) + "\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static string Line(string kind, ProcessIdentity p) => string.Create(CultureInfo.InvariantCulture, $"{kind} {p.Pid} {p.StartTicks} {p.ExecutablePath}");
    }

    private static void DeletePidFile(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, PidFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether a runner holds the lock now.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    public static bool IsHeld(string directory)
    {
        if (!File.Exists(Path.Combine(directory, LockFile)))
        {
            return false;
        }
        // Open the lock exclusively for an instant, as in WaitForReleaseAsync.
        using var probe = Open(directory);
        return probe is null;
    }

    /// <summary>The runner holding the lock, from <see cref="PidFile"/>; null when it isn't held or the file is unreadable.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    public static RunnerHolder? ReadHolder(string directory)
    {
        try
        {
            ProcessIdentity? runner = null, loop = null;
            foreach (var line in File.ReadAllLines(Path.Combine(directory, PidFile)))
            {
                var parts = line.Split(' ', 4, StringSplitOptions.TrimEntries);
                if (parts.Length != 4 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var pid) || pid <= 0
                    || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var start) || parts[3].Length == 0)
                {
                    return null;
                }
                var identity = new ProcessIdentity(pid, start, parts[3]);
                switch (parts[0])
                {
                    case "runner": runner = identity; break;
                    case "loop": loop = identity; break;
                    default: return null;
                }
            }
            return runner is null ? null : new RunnerHolder(runner, loop);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Asks every runner here to stop, now and whenever one starts, until <see cref="ClearStop"/>.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    public static void RequestStop(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, StopFile), "Written by mbus runner stop, uninstall or install. A runner started by hand or mbus runner install removes it.\n");
    }

    /// <summary>Removes the stop request; true when there was one.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    public static bool ClearStop(string directory)
    {
        var path = Path.Combine(directory, StopFile);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>Whether a stop is requested.</summary>
    /// <param name="directory">mbus's settings folder.</param>
    public static bool StopRequested(string directory) => File.Exists(Path.Combine(directory, StopFile));

    /// <summary>
    /// A token cancelled when <paramref name="cancellationToken"/> is, or when the stop request appears (looked for every
    /// <see cref="StopCheckInterval"/>). Disposing the result stops looking.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">The runner's own stop (Ctrl+C).</param>
    public StopWatch WatchStop(TimeProvider time, CancellationToken cancellationToken)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var directory = _directory;
        var timer = time.CreateTimer(_ =>
        {
            if (StopRequested(directory))
            {
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, null, StopCheckInterval, StopCheckInterval);
        return new StopWatch(stop, timer);
    }

    /// <summary>
    /// Waits until no runner holds the lock, looking every half second, for at most <paramref name="timeout"/>.
    /// </summary>
    /// <returns>True when it is free.</returns>
    public static async Task<bool> WaitForReleaseAsync(string directory, TimeProvider time, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (IsHeld(directory))
        {
            if (time.GetUtcNow() >= deadline)
            {
                return false;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DeletePidFile(_directory);
        _lock.Dispose();
    }

    private static FileStream? Open(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The runner's stop token while it runs (<see cref="WatchStop"/>).</summary>
    public sealed class StopWatch(CancellationTokenSource stop, ITimer timer) : IDisposable
    {
        /// <summary>Cancelled by Ctrl+C or the stop request.</summary>
        public CancellationToken Token => stop.Token;

        /// <inheritdoc />
        public void Dispose()
        {
            timer.Dispose();
            stop.Dispose();
        }
    }
}

/// <summary>The runner holding the lock.</summary>
/// <param name="Runner">Its process.</param>
/// <param name="Loop">On Windows, the logon task's restart loop (<c>cmd</c>) that started it.</param>
public sealed record RunnerHolder(ProcessIdentity Runner, ProcessIdentity? Loop)
{
    /// <summary>The runner's process id.</summary>
    public int Pid => Runner.Pid;
}

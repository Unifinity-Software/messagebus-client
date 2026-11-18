using System.Globalization;

namespace MessageBus.Cli.Runner;

/// <summary>
/// The runner's housekeeping (the original issue, the original issue): when it starts and then once a day, it deletes what finished runs left in the
/// work folder (<see cref="RunWorkspace.Sweep"/>: a crash, a restart, a delete that failed, a held run past its day),
/// deletes a task's kept log once it is <see cref="LogDaysVariable"/> days old (14 by default, the original issue), removes repository
/// mirrors (<see cref="RepoMirrors"/>) no run has used for <see cref="MirrorDaysVariable"/> days (30 by default), drops the
/// per-task refs older than <see cref="RepoMirrors.CodeRefsKeptFor"/> from the copies of code sent with tasks and lets git
/// pack the rest when they need it. The runner's own log is kept small by <see cref="Service.RunnerLogFile"/>.
/// </summary>
public sealed class RunnerHousekeeping : IAsyncDisposable
{
    /// <summary>Days a repository mirror may go unused before the runner removes it; <c>0</c> keeps mirrors forever.</summary>
    public const string MirrorDaysVariable = "MBUS_RUNNER_MIRROR_DAYS";

    /// <summary>Days a finished task's kept log stays in the work folder (the original issue); <c>0</c> keeps logs forever.</summary>
    public const string LogDaysVariable = "MBUS_RUNNER_LOG_DAYS";

    /// <summary>How often housekeeping runs after the first time.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly string _workRoot;
    private readonly RepoMirrors _mirrors;
    private readonly string? _git;
    private readonly TimeSpan? _unusedFor;
    private readonly TimeSpan? _keepLogsFor;
    private readonly TextWriter _log;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task _current = Task.CompletedTask;

    private RunnerHousekeeping(string workRoot, string? git, TimeSpan? unusedFor, TimeSpan? keepLogsFor, TextWriter log, TimeProvider time)
    {
        _workRoot = workRoot;
        _mirrors = RepoMirrors.For(workRoot, time);
        _git = git;
        _unusedFor = unusedFor;
        _keepLogsFor = keepLogsFor;
        _log = log;
        _time = time;
        _timer = time.CreateTimer(_ => Tidy(), null, Interval, Interval);
        Tidy();
    }

    /// <summary>Starts housekeeping for the runs in <paramref name="workRoot"/>; the first pass begins now.</summary>
    /// <param name="workRoot">The runner's work folder.</param>
    /// <param name="git">git, or null (mirrors are then only removed, never packed).</param>
    /// <param name="env">The runner's environment (<see cref="MirrorDaysVariable"/>, <see cref="LogDaysVariable"/>).</param>
    /// <param name="log">Runner log.</param>
    /// <param name="time">Clock.</param>
    /// <exception cref="FormatException">A days variable isn't a whole number of days.</exception>
    public static RunnerHousekeeping Start(string workRoot, string? git, Func<string, string?> env, TextWriter log, TimeProvider time) =>
        new(workRoot, git, Days(env, MirrorDaysVariable, 30, "mirrors"), Days(env, LogDaysVariable, 14, "logs"), log, time);

    /// <summary>The days <paramref name="variable"/> sets, else <paramref name="byDefault"/>; null for 0 (keep forever).</summary>
    private static TimeSpan? Days(Func<string, string?> env, string variable, int byDefault, string what)
    {
        var days = RunnerEnvironment.NonEmpty(env(variable)) is { } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"{variable} is '{text}'; use a number of days, or 0 to keep {what}.")
            : byDefault;
        return days == 0 ? null : TimeSpan.FromDays(days);
    }

    /// <summary>The pass in progress, or the last one (tests wait for it).</summary>
    internal Task Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    private void Tidy()
    {
        lock (_gate)
        {
            if (!_current.IsCompleted || _stop.IsCancellationRequested)
            {
                return;
            }
            _current = Task.Run(TidyAsync);
        }
    }

    private async Task TidyAsync()
    {
        try
        {
            var swept = RunWorkspace.Sweep(_workRoot, _time.GetUtcNow(), _keepLogsFor);
            if (swept.Deleted > 0)
            {
                await _log.WriteLineAsync($"Deleted the files of {swept.Deleted} finished task(s) from {_workRoot}.").ConfigureAwait(false);
            }
            if (swept.Logs > 0)
            {
                await _log.WriteLineAsync($"Deleted {swept.Logs} task log(s) older than {_keepLogsFor!.Value.TotalDays:0} days from {_workRoot}.").ConfigureAwait(false);
            }
            if (swept.Failed > 0)
            {
                await _log.WriteLineAsync($"Couldn't delete the files of {swept.Failed} finished task(s) in {_workRoot} yet (a file is still open); the next tidy-up tries again.").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _log.WriteLineAsync($"Couldn't tidy the finished tasks' folders: {ex.Message}").ConfigureAwait(false);
        }
        try
        {
            var removed = await _mirrors.PruneAsync(_git, _unusedFor ?? TimeSpan.MaxValue, _stop.Token).ConfigureAwait(false);
            if (removed.Count > 0)
            {
                await _log.WriteLineAsync($"Removed {removed.Count} repository mirror(s) no task has used for {_unusedFor!.Value.TotalDays:0} days: {string.Join(", ", removed)}.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await _log.WriteLineAsync($"Couldn't tidy the repository mirrors: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Current.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}

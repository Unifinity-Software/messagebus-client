using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Resources;

/// <summary>Collects on its own timer so a slow platform read cannot delay lease heartbeats.</summary>
internal sealed class ResourceMonitor : IDisposable
{
    private ISystemResourceSampler? _sampler;
    private readonly TimeProvider _time;
    private readonly Func<(int[] Roots, bool HasUnknownRoots)> _snapshot;
    private readonly CancellationToken _stopping;
    private readonly ITimer _timer;
    private RunnerResourceSample? _latest;
    private readonly object _gate = new();
    private Task _collection;
    private long _lastCollectionStarted;
    private long _sequence;
    private bool _busy;
    private bool _disposed;

    public ResourceMonitor(ISystemResourceSampler? sampler, TimeProvider time,
        Func<(int[] Roots, bool HasUnknownRoots)> snapshot, CancellationToken stopping)
    {
        _sampler = sampler;
        _time = time;
        _snapshot = snapshot;
        _stopping = stopping;
        _lastCollectionStarted = time.GetTimestamp();
        _collection = Task.Run(CollectAsync);
        _timer = time.CreateTimer(_ => CollectOnTimer(), null, RunnerResourceProtocol.IdleInterval,
            RunnerResourceProtocol.IdleInterval);
    }

    public RunnerResourceSample? Latest => Volatile.Read(ref _latest);

    internal Task CurrentCollection
    {
        get { lock (_gate) return _collection; }
    }

    private TimeSpan Interval => _busy ? RunnerResourceProtocol.BusyInterval : RunnerResourceProtocol.IdleInterval;

    public void SetBusy(bool busy)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested || _busy == busy) return;
            _busy = busy;
            var remaining = Interval - _time.GetElapsedTime(_lastCollectionStarted);
            Schedule(remaining);
        }
    }

    private void Schedule(TimeSpan due) =>
        _timer.Change(TimeSpan.FromMilliseconds(Math.Max(0, Math.Ceiling(due.TotalMilliseconds))), Timeout.InfiniteTimeSpan);

    private void CollectOnTimer()
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested) return;
            var remaining = Interval - _time.GetElapsedTime(_lastCollectionStarted);
            if (remaining > TimeSpan.Zero)
            {
                Schedule(remaining);
                return;
            }
            if (_collection.IsCompleted)
            {
                _lastCollectionStarted = _time.GetTimestamp();
                _collection = Task.Run(CollectAsync);
            }
            Schedule(Interval);
        }
    }

    private async Task CollectAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        ResourceSamplingResult result;
        var unknownRoots = false;
        try
        {
            var snapshot = _snapshot();
            unknownRoots = snapshot.HasUnknownRoots;
            _sampler ??= ResourceSamplerFactory.Create();
            result = await _sampler.SampleAsync(new ResourceSamplingContext(
                _time, Environment.ProcessId, snapshot.Roots), _stopping).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = new ResourceSamplingResult(_time.GetUtcNow(), null, ResourceSampleStatus.Error, "collection-failed");
        }

        // Native reads may finish after shutdown.
        if (_stopping.IsCancellationRequested) return;
        if (unknownRoots && result.Status is ResourceSampleStatus.Ok or ResourceSampleStatus.Partial)
        {
            // The launcher is visible, but the protected task tree may be outside the runner's view.
            // Neither aggregate can be attributed reliably in that case.
            result = result with { Status = ResourceSampleStatus.Partial, Reason = "task-roots-unknown", Runner = null, Tasks = null };
        }
        Volatile.Write(ref _latest, new RunnerResourceSample
        {
            Sequence = ++_sequence,
            SampledAt = result.SampledAt,
            WindowMs = result.WindowMs,
            Status = result.Status,
            Reason = result.Reason,
            Cpu = result.Cpu,
            Memory = result.Memory,
            Runner = result.Runner,
            Tasks = result.Tasks,
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}

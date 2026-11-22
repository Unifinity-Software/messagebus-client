using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Resources;

/// <summary>Collects resources for a runner on the current platform.</summary>
/// <remarks>
/// The caller serializes calls and schedules them at the reporting cadence. A sampler can retain
/// counter baselines between calls. It uses the supplied clock for timestamps and elapsed time,
/// returns unknown metrics as null, and propagates cancellation. The first call can report
/// instantaneous metrics with a null measurement window and null CPU utilization.
/// </remarks>
internal interface ISystemResourceSampler
{
    /// <summary>Collects one observation. Expected platform read failures are reported through status and reason.</summary>
    ValueTask<ResourceSamplingResult> SampleAsync(ResourceSamplingContext context, CancellationToken cancellationToken);
}

/// <summary>Inputs supplied by the runner for each observation.</summary>
/// <param name="TimeProvider">Clock used for UTC time and monotonic counter deltas.</param>
/// <param name="RunnerProcessId">Root of the runner tree.</param>
/// <param name="OwnedRunRootProcessIds">Snapshot of the runner-owned task tree roots.</param>
internal sealed record ResourceSamplingContext(
    TimeProvider TimeProvider,
    int RunnerProcessId,
    IReadOnlyList<int> OwnedRunRootProcessIds);

/// <summary>Platform observation before the caller assigns a connection sequence.</summary>
/// <param name="SampledAt">UTC time at the end of collection.</param>
/// <param name="WindowMs">Elapsed monotonic milliseconds between counter readings, if available.</param>
/// <param name="Status">How much of the observation could be collected.</param>
/// <param name="Reason">Optional short code explaining the status.</param>
/// <param name="Cpu">Effective CPU capacity and utilization.</param>
/// <param name="Memory">Effective memory capacity and availability.</param>
/// <param name="Runner">Runner tree usage excluding the owned task trees.</param>
/// <param name="Tasks">Usage of the union of owned task trees.</param>
internal sealed record ResourceSamplingResult(
    DateTimeOffset SampledAt,
    long? WindowMs,
    ResourceSampleStatus Status,
    string? Reason = null,
    RunnerCpuSample? Cpu = null,
    RunnerMemorySample? Memory = null,
    RunnerProcessTreeSample? Runner = null,
    RunnerProcessTreeSample? Tasks = null);

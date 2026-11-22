using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Runner;

/// <summary>Schema and reporting cadence for negotiated runner resource samples.</summary>
public static class RunnerResourceProtocol
{
    /// <summary>The current resource sample schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The reporting interval while a runner has at least one busy slot.</summary>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(15);

    /// <summary>The reporting interval while a runner has no busy slots.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);
}

/// <summary>How much of a resource sample the runner was able to collect.</summary>
public enum ResourceSampleStatus
{
    /// <summary>Collection completed.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Collection returned only some of the metrics.</summary>
    [JsonStringEnumMemberName("partial")]
    Partial,

    /// <summary>The runner environment does not support collection.</summary>
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    /// <summary>Collection failed.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>The source of an effective CPU or memory limit.</summary>
public enum ResourceLimitSource
{
    /// <summary>The host capacity applies.</summary>
    [JsonStringEnumMemberName("host")]
    Host,

    /// <summary>A Linux cgroup v1 limit applies.</summary>
    [JsonStringEnumMemberName("cgroup-v1")]
    CgroupV1,

    /// <summary>A Linux cgroup v2 limit applies.</summary>
    [JsonStringEnumMemberName("cgroup-v2")]
    CgroupV2,

    /// <summary>A Windows job object limit applies.</summary>
    [JsonStringEnumMemberName("job-object")]
    JobObject,
}

/// <summary>CPU utilization and capacity as seen by the runner.</summary>
public sealed record RunnerCpuSample
{
    /// <summary>Utilization over the sample window, from 0 through 100 percent of effective CPU capacity.</summary>
    public double? UtilizationPercent { get; init; }

    /// <summary>Effective CPU capacity in logical CPUs, which may be fractional.</summary>
    public double? EffectiveCpus { get; init; }

    /// <summary>Number of logical CPUs on the host.</summary>
    public int? HostLogicalCpus { get; init; }

    /// <summary>Source of the effective CPU limit.</summary>
    public ResourceLimitSource? LimitSource { get; init; }
}

/// <summary>Memory capacity and availability as seen by the runner.</summary>
public sealed record RunnerMemorySample
{
    /// <summary>Effective memory capacity in bytes.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Available memory in bytes within the effective capacity.</summary>
    public long? AvailableBytes { get; init; }

    /// <summary>Total physical memory on the host in bytes.</summary>
    public long? HostTotalBytes { get; init; }

    /// <summary>Source of the effective memory limit.</summary>
    public ResourceLimitSource? LimitSource { get; init; }
}

/// <summary>Resource usage aggregated across a set of process trees.</summary>
public sealed record RunnerProcessTreeSample
{
    /// <summary>CPU utilization over the sample window, as a percentage of effective CPU capacity.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Sum of resident bytes for the observed processes.</summary>
    public long? ResidentBytes { get; init; }

    /// <summary>Number of observed processes.</summary>
    public int? ProcessCount { get; init; }

    /// <summary>Short code explaining partial or unavailable process data.</summary>
    public string? Reason { get; init; }
}

/// <summary>A resource observation from one runner connection session.</summary>
public sealed record RunnerResourceSample
{
    /// <summary>The schema version of this sample.</summary>
    public int SchemaVersion { get; set; } = RunnerResourceProtocol.SchemaVersion;

    /// <summary>A sequence that increases for each new sample within the connection session.</summary>
    public required long Sequence { get; init; }

    /// <summary>UTC time at the end of collection.</summary>
    public required DateTimeOffset SampledAt { get; init; }

    /// <summary>Duration of the measurement window in milliseconds, or null when no baseline is available.</summary>
    public long? WindowMs { get; init; }

    /// <summary>The runner's collection result.</summary>
    public required ResourceSampleStatus Status { get; init; }

    /// <summary>Short code explaining the collection result.</summary>
    public string? Reason { get; init; }

    /// <summary>Effective CPU capacity and usage, when available.</summary>
    public RunnerCpuSample? Cpu { get; init; }

    /// <summary>Effective memory capacity and availability, when available.</summary>
    public RunnerMemorySample? Memory { get; init; }

    /// <summary>Aggregate for the runner process tree, excluding the owned task trees.</summary>
    public RunnerProcessTreeSample? Runner { get; init; }

    /// <summary>Aggregate across all owned task process trees, counting each process once.</summary>
    public RunnerProcessTreeSample? Tasks { get; init; }
}

/// <summary>The service's resource placement classification for a machine.</summary>
public enum MachineResourceState
{
    /// <summary>There is insufficient information to classify the machine.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>The machine is within normal resource thresholds.</summary>
    [JsonStringEnumMemberName("healthy")]
    Healthy,

    /// <summary>The machine shows elevated resource pressure.</summary>
    [JsonStringEnumMemberName("elevated")]
    Elevated,

    /// <summary>The machine shows critical resource pressure.</summary>
    [JsonStringEnumMemberName("critical")]
    Critical,

    /// <summary>The latest resource observation is too old for placement.</summary>
    [JsonStringEnumMemberName("stale")]
    Stale,

    /// <summary>The runner does not support resource collection in its environment.</summary>
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    /// <summary>The runner is offline.</summary>
    [JsonStringEnumMemberName("offline")]
    Offline,
}

/// <summary>The service's resource view for a machine in discovery.</summary>
public sealed record MachineResourceView
{
    /// <summary>The latest runner sample, when available.</summary>
    public RunnerResourceSample? Sample { get; init; }

    /// <summary>UTC time when the service received the sample, when known.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Age of the sample in seconds as calculated by the service, when known.</summary>
    public long? AgeSeconds { get; init; }

    /// <summary>The service's placement classification, independent of the sample's collection status.</summary>
    public MachineResourceState State { get; init; }

    /// <summary>Short codes explaining the placement classification.</summary>
    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>Total runner slots, when known.</summary>
    public int? SlotsTotal { get; init; }

    /// <summary>Busy runner slots, when known.</summary>
    public int? SlotsBusy { get; init; }
}

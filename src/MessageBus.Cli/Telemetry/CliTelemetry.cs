using System.Diagnostics;
using MessageBus.Cli.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MessageBus.Cli.Telemetry;

/// <summary>
/// OpenTelemetry for mbus (the original issue). Off by default: spans and metrics are exported over OTLP only when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or <c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c>) is set, configured
/// by the standard <c>OTEL_*</c> variables (protocol, headers, service name). Whether or not anything is
/// exported, an mbus started inside an agent run continues that run's trace from <c>TRACEPARENT</c>, so
/// the messages the agent sends join the task's trace at the service. <c>mbus runner</c> also exports
/// its status lines as OTLP logs (the original issue) through <see cref="RunnerLogBridge"/>, which drops agent output
/// and credentials; <c>OTEL_LOGS_EXPORTER=none</c> turns that off.
/// </summary>
/// <remarks>Only ids, roles, machines, statuses, durations and token counts are recorded; never a body, prompt or secret.</remarks>
public static class CliTelemetry
{
    /// <summary>Name of mbus's activity source and meter.</summary>
    public const string Name = "MessageBus.Cli";

    /// <summary>mbus's spans.</summary>
    public static readonly ActivitySource Source = new(Name);

    /// <summary>The sources mbus records: its own, the MCP SDK's (method and tool names only) and outgoing HTTP calls.</summary>
    public static TracerProviderBuilder AddMbusSources(this TracerProviderBuilder builder) =>
        builder.AddSource(Name, "ModelContextProtocol", "Experimental.ModelContextProtocol", "System.Net.Http");

    /// <summary>Whether the environment asks for export.</summary>
    public static bool ExportEnabled(Func<string, string?> env) =>
        !string.Equals(env("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrWhiteSpace(env("OTEL_EXPORTER_OTLP_ENDPOINT")) || !string.IsNullOrWhiteSpace(env("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")));

    /// <summary>Whether the environment asks for the runner's logs to be exported.</summary>
    public static bool LogsExportEnabled(Func<string, string?> env) =>
        !string.Equals(env("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(env("OTEL_LOGS_EXPORTER"), "none", StringComparison.OrdinalIgnoreCase)
        && (!string.IsNullOrWhiteSpace(env("OTEL_EXPORTER_OTLP_ENDPOINT")) || !string.IsNullOrWhiteSpace(env("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")));

    /// <summary>
    /// Starts telemetry for one mbus command line; dispose it when the command ends (that flushes what
    /// is waiting to be exported). For <c>mbus runner</c> with logs export on, stderr is replaced with
    /// a <see cref="RunnerLogBridge"/> over it, so call this before anything captures <see cref="Console.Error"/>.
    /// </summary>
    public static IDisposable Start(Func<string, string?> env, string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "mbus";
        TracerProvider? tracing = null;
        MeterProvider? metrics = null;
        ILoggerFactory? logs = null;
        var resource = ResourceBuilder.CreateDefault()
            .AddService(env("OTEL_SERVICE_NAME") is { Length: > 0 } name ? name : "mbus", serviceVersion: CliHost.Version)
            .AddAttributes([new("mbus.command", command)]);
        if (ExportEnabled(env))
        {
            tracing = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddMbusSources().AddOtlpExporter().Build();
            metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(Name).AddOtlpExporter().Build();
        }
        if (command == "runner" && LogsExportEnabled(env))
        {
            logs = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(resource);
                o.AddOtlpExporter();
            }));
            Console.SetError(TextWriter.Synchronized(new RunnerLogBridge(Console.Error, logs.CreateLogger(RunnerLogBridge.Category))));
        }

        // Inside an agent run (the runner sets TRACEPARENT): the whole command is one span in the run's
        // trace, and every call it makes to the service carries it. The runner itself starts each run's
        // span from the delivery instead.
        Activity? span = null;
        if (command != "runner" && env("TRACEPARENT") is { Length: > 0 } traceParent)
            span = Continue($"mbus {command}", ActivityKind.Internal, traceParent, env("TRACESTATE"));
        return new Session(span, tracing, metrics, logs);
    }

    /// <summary>
    /// Starts a span under a remote parent. When nothing records mbus's spans (export is off) it is
    /// still started, unrecorded, so calls made under it carry the trace on to the service.
    /// </summary>
    internal static Activity? Continue(string name, ActivityKind kind, string? traceParent, string? traceState,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent);
        if (Source.StartActivity(name, kind, parent, tags) is { } recorded) return recorded;
        if (parent == default) return null;
        var unrecorded = new Activity(name).SetParentId(traceParent!);
        unrecorded.TraceStateString = traceState;
        return unrecorded.Start();
    }

    private sealed class Session(Activity? span, TracerProvider? tracing, MeterProvider? metrics, ILoggerFactory? logs) : IDisposable
    {
        public void Dispose()
        {
            span?.Dispose();
            tracing?.Dispose();
            metrics?.Dispose();
            logs?.Dispose();
        }
    }
}

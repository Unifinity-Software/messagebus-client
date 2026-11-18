using System.Diagnostics;
using System.Diagnostics.Metrics;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Telemetry;

/// <summary>
/// The runner's <c>mbus run</c> span around each delivery, continuing the delivery's trace (the
/// service's <c>mbus deliver</c> span), and its run metrics. The settle and progress calls are made
/// under it, so the service's <c>mbus settle</c> joins the same trace.
/// </summary>
internal static class RunnerTelemetry
{
    private static readonly Meter Meter = new(CliTelemetry.Name);
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("mbus.runner.runs", "{run}", "Runs finished, by role, agent and outcome.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("mbus.runner.run.duration", "s", "How long each run took, worktree to settle.");
    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>("gen_ai.client.token.usage", "{token}", "Tokens an agent used on a run.");

    /// <summary>Starts the run's span (null only when nothing records it and the delivery has no trace).</summary>
    public static Activity? StartRun(TaskDelivery delivery, string? machineId, string machineName, AgentKind agent)
    {
        var envelope = delivery.Envelope;
        return CliTelemetry.Continue("mbus run", ActivityKind.Consumer, envelope.Trace?.TraceParent, envelope.Trace?.TraceState,
        [
            new("messaging.system", "messagebus"),
            new("messaging.operation.name", "process"),
            new("messaging.message.id", envelope.Id),
            new("mbus.workspace.id", envelope.WorkspaceId),
            new("mbus.thread.id", envelope.ThreadId),
            new("mbus.project.id", envelope.ProjectId),
            new("mbus.delivery.id", delivery.DeliveryId),
            new("mbus.delivery.attempt", delivery.Attempt),
            new("mbus.role", delivery.Role),
            new("mbus.agent.id", delivery.AgentId),
            new("mbus.agent.kind", AgentKinds.Name(agent)),
            new("mbus.machine.id", machineId),
            new("mbus.machine.name", machineName),
        ]);
    }

    /// <summary>Records how the run ended: status, exit code and the agent's token usage (never its text).</summary>
    public static void Finish(Activity? span, TaskDelivery delivery, AgentKind agent, SettleResult result, TimeSpan elapsed)
    {
        var status = result.Status.ToString().ToLowerInvariant();
        if (span is { IsAllDataRequested: true })
        {
            span.SetTag("mbus.settle.status", status);
            span.SetTag("process.exit.code", result.ExitCode);
            if (result.Usage is { } usage)
            {
                span.SetTag("gen_ai.usage.input_tokens", usage.TokensIn);
                span.SetTag("gen_ai.usage.output_tokens", usage.TokensOut);
                span.SetTag("gen_ai.response.model", usage.Model);
                span.SetTag("gen_ai.provider.name", usage.Vendor);
                if (usage.EstimatedCostUsd is { } cost) span.SetTag("mbus.task.cost_usd", (double)cost);
                if (usage.ActiveSeconds > 0) span.SetTag("mbus.task.active_seconds", usage.ActiveSeconds);
            }
            if (result.Status is SettleStatus.Failed or SettleStatus.Abandoned)
            {
                span.SetStatus(ActivityStatusCode.Error);
                span.SetTag("error.type", status);
            }
        }

        var tags = new TagList { { "mbus.role", delivery.Role ?? "-" }, { "mbus.agent.kind", AgentKinds.Name(agent) }, { "mbus.settle.status", status } };
        Runs.Add(1, tags);
        Duration.Record(elapsed.TotalSeconds, tags);
        if (result.Usage is { } u)
        {
            var model = u.Model ?? "unknown";
            Tokens.Record(u.TokensIn, new TagList { { "mbus.role", delivery.Role ?? "-" }, { "gen_ai.response.model", model }, { "gen_ai.token.type", "input" } });
            Tokens.Record(u.TokensOut, new TagList { { "mbus.role", delivery.Role ?? "-" }, { "gen_ai.response.model", model }, { "gen_ai.token.type", "output" } });
        }
    }
}

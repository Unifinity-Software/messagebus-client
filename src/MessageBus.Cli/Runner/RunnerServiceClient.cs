using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>The calls the runner makes to the service (<see cref="ServiceRoutes"/>).</summary>
public interface IRunnerServiceClient
{
    /// <summary>Keeps leases alive; the answer says which runs to stop.</summary>
    Task<HeartbeatAck> HeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken);

    /// <summary>Progress on a delivery.</summary>
    Task ProgressAsync(RunnerProgress progress, CancellationToken cancellationToken);

    /// <summary>How a delivery ended.</summary>
    Task SettleAsync(RunnerSettle settle, CancellationToken cancellationToken);

    /// <summary>
    /// Agents and capabilities after a scan (<see cref="ApiRoutes.MachineInventory"/>). Returns the
    /// registry's agents for this machine (with their <c>agt_…</c> ids), as the service answers.
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> InventoryAsync(MachineInventory inventory, CancellationToken cancellationToken);

    /// <summary>This machine's work policy (the original issue); null when the service doesn't say (one older than the original issue).</summary>
    Task<RunnerWorkPolicy?> PolicyAsync(CancellationToken cancellationToken) => Task.FromResult<RunnerWorkPolicy?>(null);

    /// <summary>
    /// Asks the machine's owner to approve a run, or where that stands (the original issue); null when the service can't ask (one older
    /// than the original issue): the run is then handed back rather than started unapproved.
    /// </summary>
    Task<RunApprovalState?> RunApprovalAsync(RunApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult<RunApprovalState?>(null);

    /// <summary>Reports locally observed agent allowance windows.</summary>
    Task CapacityAsync(AgentCapacityReport report, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Where this runner's update stands, or how it ended without a restart (the original issue), for the Runners page. A service that
    /// doesn't take it (one older than the original issue) is not an error. No default: a wrapper that forgot to pass it on would
    /// silence it.
    /// </summary>
    Task UpdateProgressAsync(Contracts.Releases.RunnerUpdateProgress progress, CancellationToken cancellationToken);
}

/// <summary><see cref="IRunnerServiceClient"/> over the machine-authenticated HTTPS client.</summary>
/// <param name="http">From <see cref="IRunnerHostContext.CreateServiceClient"/>.</param>
public sealed class HttpRunnerServiceClient(HttpClient http) : IRunnerServiceClient
{
    /// <inheritdoc />
    public async Task<HeartbeatAck> HeartbeatAsync(RunnerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, ServiceRoutes.Heartbeat, heartbeat, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? new HeartbeatAck() : MessageBusJson.Deserialize<HeartbeatAck>(text) ?? new HeartbeatAck();
    }

    /// <inheritdoc />
    public async Task ProgressAsync(RunnerProgress progress, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Post, ServiceRoutes.Progress, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SettleAsync(RunnerSettle settle, CancellationToken cancellationToken)
    {
        if (settle.Attempt is not null && !await MessageBus.Mcp.TargetCapabilities.SupportsAsync(http, "settleAttemptV1", cancellationToken).ConfigureAwait(false))
            settle = settle with { Attempt = null };
        using var _ = await SendAsync(HttpMethod.Post, ServiceRoutes.Settle, settle, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentInfo>> InventoryAsync(MachineInventory inventory, CancellationToken cancellationToken)
    {
        if (MessageBus.Cli.Server.MachineInventoryCompatibility.HasAntigravity(inventory))
        {
            var supported = await MessageBus.Mcp.TargetCapabilities.SupportsAsync(http, MessageBus.Cli.Server.MachineInventoryCompatibility.CapabilityToken, cancellationToken).ConfigureAwait(false);
            inventory = MessageBus.Cli.Server.MachineInventoryCompatibility.ForServer(inventory, supported);
        }
        // The service takes inventory at the machines route (the original issue, the original issue) and answers with the registry's agents.
        using var response = await SendAsync(HttpMethod.Put, ApiRoutes.MachineInventory, inventory, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var info = (JsonTypeInfo<AgentInfo>)MessageBusJson.Options.GetTypeInfo(typeof(AgentInfo));
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Deserialize(info)).OfType<AgentInfo>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<RunnerWorkPolicy?> PolicyAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ServiceRoutes.Policy.TrimStart('/'), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            if (await MessageBus.Cli.Server.SuspensionRefusal.ReadAsync(response, cancellationToken).ConfigureAwait(false) is { } suspension)
                throw new MessageBus.Cli.Server.WorkspaceSuspendedException(suspension);
            throw new HttpRequestException($"GET {ServiceRoutes.Policy} answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
        return MessageBusJson.Deserialize<RunnerWorkPolicy>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<RunApprovalState?> RunApprovalAsync(RunApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, ServiceRoutes.RunApproval, request, cancellationToken).ConfigureAwait(false);
            return MessageBusJson.Deserialize<RunApprovalState>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // a service older than the original issue, or the task is no longer this machine's
        }
    }

    /// <inheritdoc />
    public async Task UpdateProgressAsync(Contracts.Releases.RunnerUpdateProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, ServiceRoutes.UpdateProgress, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            // A service older than the original issue: the page there shows the result when the runner next starts.
        }
    }

    /// <inheritdoc />
    public async Task CapacityAsync(AgentCapacityReport report, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ServiceRoutes.AgentCapacity.TrimStart('/'))
        {
            Content = new StringContent(JsonSerializer.Serialize(report, CapacityJsonContext.Default.AgentCapacityReport), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync<T>(HttpMethod method, string route, T body, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, (JsonTypeInfo<T>)MessageBusJson.Options.GetTypeInfo(typeof(T)));
        using var request = new HttpRequestMessage(method, route.TrimStart('/'))
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var suspension = await MessageBus.Cli.Server.SuspensionRefusal.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            response.Dispose();
            if (suspension is not null) throw new MessageBus.Cli.Server.WorkspaceSuspendedException(suspension);
            throw new HttpRequestException($"{method} {route} answered {status}.", null, response.StatusCode);
        }
        return response;
    }
}

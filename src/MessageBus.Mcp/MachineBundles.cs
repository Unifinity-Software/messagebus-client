using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MessageBus.Mcp;

/// <summary>
/// Gets a task's git bundle onto this machine, decrypted with the machine's keys (the runner's
/// <c>IBundleSource</c>, the original issue): the delivery's download link (the original issue), the role key from this machine's grants (the original issue).
/// </summary>
public sealed class MachineBundles : IDisposable
{
    private readonly HttpClient _mcp;
    private readonly AttachmentClient _attachments;
    private readonly MachineKeys _keys;
    private readonly TextWriter _log;
    private readonly TimeProvider _time;
    private readonly HttpClient _api;
    private readonly string _workspaceId;

    /// <param name="api">The service as this machine (signed; base address the server URL with a trailing slash).</param>
    /// <param name="links">A plain client for link URLs, with a long timeout.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="machineId">This machine.</param>
    /// <param name="configDirectory">mbus's settings folder.</param>
    /// <param name="log">Logs.</param>
    /// <param name="time">Clock.</param>
    /// <param name="machineKeyPem">Reads the device key (mbus's key store, the original issue); null reads the key file in <paramref name="configDirectory"/>.</param>
    /// <param name="mcp">The service's MCP tools as this machine (the original issue: asking the sender for earlier history); null uses <paramref name="api"/>.</param>
    public MachineBundles(HttpClient api, HttpClient links, string workspaceId, string? machineId, string configDirectory, TextWriter log, TimeProvider? time = null,
        Func<string?>? machineKeyPem = null, HttpClient? mcp = null)
    {
        _time = time ?? TimeProvider.System;
        _api = api;
        _workspaceId = workspaceId;
        _log = log;
        _mcp = mcp ?? api;
        _attachments = new AttachmentClient(api, links, workspaceId);
        _keys = new MachineKeys(api, workspaceId, machineId, configDirectory, log, _time, machineKeyPem);
    }

    /// <summary>Longest single inbox call while waiting for earlier history; the wait is made of several.</summary>
    public TimeSpan PollChunk { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Reads the latest result reply with this machine's keys, retaining sealed attachment metadata for download.</summary>
    public async Task<ReceivedMessage?> ReadStepResultAsync(TaskDelivery delivery, StepInput input, CancellationToken ct)
    {
        if (delivery.Envelope.WorkspaceId != _workspaceId)
            throw new InvalidOperationException("The step input belongs to another workspace.");
        if (!await TargetCapabilities.SupportsAsync(_api, WorkflowCapabilities.Workflows, ct))
            throw new InvalidOperationException("The service does not confirm support for workflow inputs.");
        var threadPath = $"api/w/{Uri.EscapeDataString(_workspaceId)}/threads/{Uri.EscapeDataString(input.ThreadId)}/messages";
        using var response = await _api.GetAsync(threadPath, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var envelope = document.RootElement.EnumerateArray()
            .Select(e => e.Deserialize<MessageEnvelope>(MessageBusJson.Options)!)
            .Where(e => e.ThreadId == input.ThreadId && e.WorkspaceId == _workspaceId && e.Type.EndsWith(".reply", StringComparison.Ordinal))
            .OrderBy(e => e.Time).ThenBy(e => e.Id, StringComparer.Ordinal).LastOrDefault();
        if (envelope is null) return null;
        byte[] bytes;
        if (envelope.Body.Data is { } data) bytes = Convert.FromBase64String(data);
        else
        {
            using var stored = await _api.GetAsync(threadPath + $"/{Uri.EscapeDataString(envelope.Id)}/body", ct);
            stored.EnsureSuccessStatusCode();
            bytes = await stored.Content.ReadAsByteArrayAsync(ct);
        }
        if (bytes.LongLength != envelope.Body.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != envelope.Body.Sha256)
            throw new CryptographicException("The result body does not match its size and SHA-256.");
        var body = envelope.Encryption is { } encryption
            ? await _keys.OpenAsync(bytes, encryption, ct)
            : MessageBusJson.Deserialize<MessageBody>(Encoding.UTF8.GetString(bytes));
        return body is null ? null : new ReceivedMessage { Envelope = envelope, Body = body, Decrypted = true };
    }

    /// <summary>Downloads an earlier result's attachment using this machine's keys and the existing attachment route.</summary>
    public async Task<string?> FetchStepAttachmentAsync(AttachmentRef attachment, string path, CancellationToken ct)
    {
        var link = await _attachments.DownloadLinkAsync(attachment.Id, ct);
        await _keys.WithRingAsync(async ring =>
        {
            await _attachments.DownloadToFileAsync(attachment, link, path, ring, ct);
            return true;
        }, ct);
        return path;
    }

    /// <summary>A decrypted copy of <paramref name="bundle"/> under <paramref name="runDirectory"/>, or null (logged) when it can't be had.</summary>
    public Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory, CancellationToken ct) =>
        FetchAsync(delivery.Envelope.ThreadId, delivery.DeliveryId, delivery.Links, bundle, Path.Combine(runDirectory, "task.bundle"), ct);

    private async Task<string?> FetchAsync(string threadId, string deliveryId, IReadOnlyList<DownloadLink> links, AttachmentRef bundle, string path, CancellationToken ct)
    {
        try
        {
            var link = links.FirstOrDefault(l => l.Id == bundle.Id && l.ExpiresAt > _time.GetUtcNow())
                       ?? (await _attachments.DeliveryLinksAsync(threadId, deliveryId, ct)).FirstOrDefault(l => l.Id == bundle.Id);
            if (link is null)
            {
                await _log.WriteLineAsync($"mbus: delivery {deliveryId} has no download link for {bundle.Id}.");
                return null;
            }
            await _keys.WithRingAsync(async ring =>
            {
                await _attachments.DownloadToFileAsync(bundle, link, path, ring, ct);
                return true;
            }, ct);
            return path;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _log.WriteLineAsync($"mbus: couldn't get the task's code ({bundle.Name}): {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// the original issue: the task's bundle starts after commits this machine doesn't have. Opens an inbox as the task's agent, asks the
    /// sender's mbus for the branch's whole history through <paramref name="ask"/> (the run's progress, naming that inbox),
    /// and waits up to <paramref name="wait"/> for it: a decrypted copy under <paramref name="runDirectory"/>, or null (logged)
    /// when it didn't come in time or couldn't be opened.
    /// </summary>
    public async Task<string?> RequestHistoryAsync(TaskDelivery delivery, AttachmentRef bundle, string runDirectory,
        Func<ProgressRequest, CancellationToken, Task> ask, TimeSpan wait, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + wait;
        try
        {
            await using var service = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_mcp.BaseAddress!, ApiRoutes.Mcp.TrimStart('/')),
                Name = "messagebus.ai",
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                AdditionalHeaders = new Dictionary<string, string> { [MbusHeaders.Agent] = delivery.AgentId },
            }, _mcp, ownsHttpClient: false), new McpClientOptions { ClientInfo = new Implementation { Name = "mbus-runner", Version = "1.0.0" } }, cancellationToken: ct);

            // The inbox closes a little after the wait, so an answer on its way still finds it.
            var inbox = await CallAsync<OpenInboxInput, OpenInboxResult>(service, McpToolNames.OpenInbox,
                new OpenInboxInput { LifetimeSeconds = (int)Math.Ceiling(wait.TotalSeconds) + 120 }, ct);
            await ask(new ProgressRequest { Kind = ProgressRequestKinds.GitHistory, AttachmentId = bundle.Id, ReplyTo = inbox.ReplyTo }, ct);

            while (true)
            {
                var left = deadline - _time.GetUtcNow();
                if (left <= TimeSpan.Zero) break;
                var chunk = left < PollChunk ? left : PollChunk;
                var received = await CallAsync<InboxInput, InboxResult>(service, McpToolNames.Inbox, new InboxInput
                {
                    InboxId = inbox.InboxId, Max = 4, TimeoutSeconds = Math.Max(1, (int)Math.Ceiling(chunk.TotalSeconds)),
                }, ct);
                foreach (var message in received.Messages)
                {
                    string? path = null;
                    if (message.Delivery is { } taken)
                    {
                        if (message.Envelope.Type == ProgressRequestKinds.GitHistoryMessageType && message.Envelope.ThreadId == delivery.Envelope.ThreadId
                            && message.Envelope.Attachments.FirstOrDefault(a => a.Format == AttachmentFormat.GitBundle) is { } history)
                            path = await FetchAsync(taken.ThreadId, taken.DeliveryId, message.Links, history, Path.Combine(runDirectory, "history.bundle"), ct);
                        if (taken.Status == DeliveryStatus.Leased)
                            await SettleQuietlyAsync(service, taken, path is not null, ct);
                    }
                    if (path is not null) return path;
                }
            }
            await _log.WriteLineAsync($"mbus: delivery {delivery.DeliveryId}: the sender didn't send the earlier history within {wait.TotalMinutes:0} minutes.");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _log.WriteLineAsync($"mbus: delivery {delivery.DeliveryId}: couldn't ask the sender for the earlier history: {e.Message}");
            return null;
        }
    }

    /// <summary>Ends the inbox delivery that carried (or didn't carry) the history; a failure only leaves it to expire.</summary>
    private async Task SettleQuietlyAsync(McpClient service, DeliveryInfo taken, bool used, CancellationToken ct)
    {
        try
        {
            await CallAsync<SettleInput, SettleOutput>(service, McpToolNames.Settle, new SettleInput
            {
                ThreadId = taken.ThreadId, DeliveryId = taken.DeliveryId,
                Result = new SettleResult { Status = SettleStatus.Succeeded, Summary = used ? "Used the earlier history." : "Not what the run waited for." },
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _log.WriteLineAsync($"mbus: couldn't settle {taken.DeliveryId}: {e.Message}");
        }
    }

    private static async Task<TOut> CallAsync<TIn, TOut>(McpClient service, string name, TIn input, CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(input, MessageBusJson.Options.GetTypeInfo(typeof(TIn)));
        var arguments = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return McpToolCatalog.ReadResult<TOut>(await service.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = arguments }, ct));
    }

    public void Dispose() => _keys.Dispose();
}

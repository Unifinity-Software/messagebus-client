using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;

namespace MessageBus.Mcp;

internal sealed partial class LocalTools
{
    private bool? _roleProposalsSupported;
    private static readonly JsonSerializerOptions ProposalJson = new(MessageBusJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static ProposeRoleInput ReadProposeRole(IDictionary<string, JsonElement>? arguments)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
                {
                    CheckDuplicates(value);
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return JsonSerializer.Deserialize(buffer.ToArray(), (JsonTypeInfo<ProposeRoleInput>)ProposalJson.GetTypeInfo(typeof(ProposeRoleInput)))
                ?? throw new JsonException("Give proposal arguments.");
        }
        catch (JsonException e)
        {
            throw ProposalError(McpErrorCodes.InvalidInput, "The proposal does not match its schema: " + e.Message, e.Path?.TrimStart('$', '.') ?? "definition");
        }

        static void CheckDuplicates(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate property: " + property.Name);
                    CheckDuplicates(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
        }
    }

    // These checks precede capability discovery, input parsing and every network call.
    private bool HasProposalAuthority => _o.ProposalAuthority switch
    {
        ProposalAuthority.Interactive => _o.Scope is null && _o.Delivery is null && _o.DeliveryFile is null
            && (_session is null || IsOriginalCoordinator(_session)),
        ProposalAuthority.ResumedCoordinator => _o.Scope?.ProposalAuthority == ProposalAuthority.ResumedCoordinator
            && _o.Delivery is { } delivery && _o.Session is { } session
            && delivery.Envelope.Target is InboxTarget inbox && inbox.InboxId == session.InboxId
            && session.OriginAuthority == ProposalAuthority.Interactive
            && session.AgentId == delivery.AgentId && session.MachineId == _o.MachineId
            && session.CoordinatorRecordId == _proposalCoordinatorRecordId
            && session.WorkspaceId == _ws && session.SessionId is { } id && session.AgentKind is { } kind
            && IsOriginalCoordinator(session)
            && AgentSessions.Exists(kind, session.Folder, id, ClaudeHome, CodexHome, _o.Time.GetUtcNow()),
        _ => false,
    };

    private bool IsOriginalCoordinator(SessionRecord session) =>
        session is { OriginAuthority: ProposalAuthority.Interactive, CoordinatorRecordId.Length: > 0, AgentId.Length: > 0 }
        && session.CoordinatorRecordId == _proposalCoordinatorRecordId
        && session.WorkspaceId == _ws && session.MachineId is not null && session.MachineId == _o.MachineId
        && _sessions.Find(session.InboxId) is { } saved
        && saved.CoordinatorRecordId == session.CoordinatorRecordId && saved.OriginAuthority == session.OriginAuthority
        && saved.WorkspaceId == session.WorkspaceId && saved.MachineId == session.MachineId && saved.AgentId == session.AgentId
        && saved.SessionId == session.SessionId && saved.AgentKind == session.AgentKind && saved.Folder == session.Folder;

    internal async Task<IList<ModelContextProtocol.Protocol.Tool>> ListToolsAsync(IReadOnlySet<string>? shown, CancellationToken ct)
    {
        var allowed = new HashSet<string>(shown ?? McpToolNames.All.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var supported = false;
        try { supported = await RoleProposalsSupportedAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        if (!supported) allowed.RemoveWhere(McpToolNames.IsRoleProposal);
        return McpToolCatalog.Tools(local: true, allowed);
    }

    internal async Task<bool> RoleProposalsSupportedAsync(CancellationToken ct)
    {
        if (!HasProposalAuthority) return false;
        if (_roleProposalsSupported is { } cached) return cached;
        using var response = await _o.Http.GetAsync("api/server", ct);
        if (!response.IsSuccessStatusCode) return (_roleProposalsSupported = false).Value;
        try
        {
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var capability = root?["capabilities"]?["roleProposals"];
            _roleProposalsSupported = capability is not null
                && capability["version"]?.GetValue<int>() == 1
                && capability["baselineVersion"]?.GetValue<string>() is { Length: > 0 }
                && capability["normalizerVersion"]?.GetValue<string>() is { Length: > 0 };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            _roleProposalsSupported = false;
        }
        return _roleProposalsSupported == true;
    }

    private static McpToolException ProposalError(string code, string message, string? field = null) =>
        new(new McpError { Code = code, Message = message, Field = field });

    private async Task<SessionRecord> ProposalCoordinatorAsync(bool open, CancellationToken ct)
    {
        if (!HasProposalAuthority)
            throw ProposalError(McpErrorCodes.Forbidden, "This host has no original coordinator authority for role proposals.");
        if (!await RoleProposalsSupportedAsync(ct))
            throw ProposalError(McpErrorCodes.Unsupported, "This server does not advertise role proposals version 1. No role changes were made.");

        if (_session is null && open)
            await OpenInboxAsync(new OpenInboxInput(), ct);
        if (_session is not { } session || !IsOriginalCoordinator(session))
            throw ProposalError(McpErrorCodes.Forbidden, "No matching original interactive coordinator record exists. Open a new interactive inbox to submit a proposal.");
        return session;
    }

    private async Task<RoleProposalSubmitted> ProposeRoleAsync(ProposeRoleInput input, CancellationToken ct)
    {
        if (!Enum.IsDefined(input.Operation)) throw ProposalError(McpErrorCodes.InvalidInput, "Operation must be create or update.", "operation");
        if (input.Definition is null) throw ProposalError(McpErrorCodes.InvalidInput, "Give the complete definition.", "definition");
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey)) throw ProposalError(McpErrorCodes.InvalidInput, "Give an idempotencyKey for safe retries.", "idempotencyKey");
        var session = await ProposalCoordinatorAsync(open: true, ct);
        var request = new SubmitRoleProposalRequest
        {
            Operation = input.Operation, Definition = input.Definition, Rationale = input.Rationale, IdempotencyKey = input.IdempotencyKey,
            AgentId = session.AgentId!, CoordinatorRecordId = session.CoordinatorRecordId!, InboxId = session.InboxId,
        };
        var json = MessageBusJson.Serialize(request);
        if (Encoding.UTF8.GetByteCount(json) > 256 * 1024)
            throw ProposalError(McpErrorCodes.TooLarge, "Role proposal requests are limited to 256 KiB.", "definition");
        // Persist before POST: the server may accept even when the response never reaches this host.
        session = (_sessions.Find(session.InboxId) ?? session) with
        {
            RoleProposalProvenance = true,
            RoleProposalRetryKeys = _o.RoleProposalRetryKey is { } retryKey
                ? [.. session.RoleProposalRetryKeys.Append(retryKey).Distinct(StringComparer.Ordinal)]
                : session.RoleProposalRetryKeys,
        };
        _sessions.Save(session);
        _session = session;
        using var message = new HttpRequestMessage(HttpMethod.Post, ProposalPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(MbusHeaders.Agent, session.AgentId);
        return await SendProposalAsync<RoleProposalSubmitted>(message, ct);
    }

    private Task<RoleProposalDetail> WaitRoleProposalAsync(WaitRoleProposalInput input, CancellationToken ct)
    {
        if (input.TimeoutSeconds is < 0 or > 30)
            throw ProposalError(McpErrorCodes.InvalidInput, "timeoutSeconds must be between 0 and 30.", "timeoutSeconds");
        return GetRoleProposalAsync(input.ProposalId, input.TimeoutSeconds, ct);
    }

    private async Task<RoleProposalDetail> GetRoleProposalAsync(string id, int? timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw ProposalError(McpErrorCodes.InvalidInput, "Give a valid proposal id.", "proposalId");
        var session = await ProposalCoordinatorAsync(open: false, ct);
        var path = ProposalPath + "/" + Uri.EscapeDataString(id) + (timeout is null ? "" : "/wait")
            + "?coordinatorRecordId=" + Uri.EscapeDataString(session.CoordinatorRecordId!)
            + "&inboxId=" + Uri.EscapeDataString(session.InboxId);
        if (timeout is { } seconds) path += "&timeoutSeconds=" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.TryAddWithoutValidation(MbusHeaders.Agent, session.AgentId);
        var result = await SendProposalAsync<RoleProposalDetail>(message, ct);
        if (result.ProposalId != id || result.CoordinatorRecordId != session.CoordinatorRecordId || result.InboxId != session.InboxId
            || result.AgentId != session.AgentId || result.MachineId != session.MachineId)
            throw ProposalError(McpErrorCodes.NotFound, "That proposal does not belong to this coordinator record.");
        return result;
    }

    private string ProposalPath => ApiRoutes.RoleProposals.Replace("{workspaceId}", Uri.EscapeDataString(_ws), StringComparison.Ordinal).TrimStart('/');

    private async Task<T> SendProposalAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _o.Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        McpError? error = null;
        if (!response.IsSuccessStatusCode)
        {
            try { error = MessageBusJson.Deserialize<McpError>(json); }
            catch (JsonException) { }
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // Recheck once when a server rolls back/disables the feature; never fall back to role CRUD.
            _roleProposalsSupported = null;
            var supported = await RoleProposalsSupportedAsync(ct);
            if (!supported || response.StatusCode == HttpStatusCode.MethodNotAllowed || request.Method == HttpMethod.Post || error is null)
                throw ProposalError(McpErrorCodes.Unsupported, "This server does not provide the advertised role proposal endpoint.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new McpToolException(error ?? new McpError
            {
                Code = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => McpErrorCodes.InvalidInput,
                    HttpStatusCode.Unauthorized => "unauthenticated",
                    HttpStatusCode.Forbidden => McpErrorCodes.Forbidden,
                    HttpStatusCode.NotFound => McpErrorCodes.NotFound,
                    HttpStatusCode.Conflict => McpErrorCodes.Conflict,
                    HttpStatusCode.RequestEntityTooLarge => McpErrorCodes.TooLarge,
                    HttpStatusCode.TooManyRequests => McpErrorCodes.LimitReached,
                    _ => McpErrorCodes.Unavailable,
                },
                Message = $"The role proposal endpoint returned HTTP {(int)response.StatusCode}.",
                Retryable = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests,
            });
        }
        return MessageBusJson.Deserialize<T>(json) ?? throw ProposalError(McpErrorCodes.Unavailable, "The role proposal endpoint returned an empty result.");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using ModelContextProtocol.Protocol;

namespace MessageBus.Mcp;

/// <summary>A call's outcome: the tool's result JSON, or its error.</summary>
/// <param name="Value">The result (or the error) as the tool returns it.</param>
/// <param name="Error">The error, when the call failed.</param>
public sealed record ScriptResult(JsonElement Value, McpError? Error)
{
    /// <summary>True when the call succeeded.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// The local MCP server's tools without MCP, for <c>mbus send</c>, <c>status</c>, <c>roles</c>, <c>threads</c> and
/// <c>attach</c> (the original issue). Every call goes through the same code as <c>mbus mcp</c>: bodies and attachments are checked
/// for secrets and sealed for their recipient here, and what comes back is opened with this machine's keys.
/// </summary>
public sealed class ScriptSession : IAsyncDisposable
{
    /// <summary>The client name the calls are made under (no agent kind: the machine sends as its owner, as a person's session does).</summary>
    public const string ClientName = "mbus-cli";

    /// <summary>The original inbox opened or resumed by this local tool session, if any.</summary>
    public string? CoordinatorInboxId => _tools.CoordinatorInboxId;

    private readonly LocalTools _tools;
    private readonly HttpClient? _ownLinks;

    /// <summary>A session over <paramref name="options"/>; <see cref="LocalServerOptions.Input"/> and <see cref="LocalServerOptions.Output"/> aren't used.</summary>
    public ScriptSession(LocalServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WorkspaceId is null) throw new ArgumentException("The machine isn't enrolled in a workspace.", nameof(options));
        _ownLinks = options.Links is null ? MessageBus.Attachments.Client.AttachmentTransfers.CreateHttpClient() : null;
        // the original issue: a script's send ends before a runner could ask it for earlier history, so its bundles carry the whole history.
        _tools = new LocalTools(options with { Links = options.Links ?? _ownLinks, IncrementalBundles = false });
    }

    /// <summary>Calls tool <paramref name="tool"/> (one of <see cref="McpToolNames"/>) with its JSON arguments.</summary>
    public async Task<ScriptResult> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(arguments.ToJsonString());
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return Result(await _tools.CallAsync(ClientName, tool, args, ct));
    }

    /// <summary>A thread's messages, oldest first, opened with this machine's keys: <c>{messages: [...]}</c>, as <c>inbox</c> returns them.</summary>
    public async Task<ScriptResult> ThreadAsync(string threadId, CancellationToken ct) =>
        Result(await _tools.ThreadAsync(threadId, ct));

    /// <summary>Packages and uploads an attachment sealed for <paramref name="to"/>; the result is its reference, for <c>send</c>'s <c>attachments</c>.</summary>
    public async Task<ScriptResult> AttachAsync(MessageTarget to, AttachSpec spec, string? threadId, CancellationToken ct) =>
        Result(await _tools.AttachAsync(to, spec, threadId, ct));

    private static ScriptResult Result(CallToolResult result)
    {
        var element = result.StructuredContent is { } structured
            ? structured.Clone()
            : JsonDocument.Parse(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}").RootElement.Clone();
        if (result.IsError != true) return new ScriptResult(element, null);
        McpError? error = null;
        try
        {
            error = element.Deserialize<McpError>(MessageBusJson.Options);
        }
        catch (JsonException)
        {
        }
        return new ScriptResult(element, error ?? new McpError { Code = McpErrorCodes.Internal, Message = element.GetRawText() });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _tools.DisposeAsync();
        _ownLinks?.Dispose();
    }
}

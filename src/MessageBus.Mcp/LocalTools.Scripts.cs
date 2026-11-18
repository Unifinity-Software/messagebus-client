using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using ModelContextProtocol.Protocol;

namespace MessageBus.Mcp;

/// <summary>
/// What <c>mbus</c>'s script commands (the original issue) need beyond the tools: a thread's messages opened with this
/// machine's keys, and an attachment packaged and sealed on its own. Both use the tools' own key, sealing
/// and packaging code, and fail with the tools' error codes.
/// </summary>
internal sealed partial class LocalTools
{
    /// <summary>A thread's messages, oldest first, each opened here when this machine holds its key.</summary>
    public Task<CallToolResult> ThreadAsync(string threadId, CancellationToken ct) =>
        GuardAsync("thread", async () =>
        {
            var envelopes = await ThreadMessagesAsync(threadId, ct);
            if (envelopes.Count == 0) throw McpToolException.NotFound($"Thread {threadId} wasn't found.");
            var opened = new List<ReceivedMessage>();
            foreach (var envelope in envelopes.OrderBy(e => e.Time).ThenBy(e => e.Id, StringComparer.Ordinal)) opened.Add(await OpenAsync(new ReceivedMessage { Envelope = envelope }, ct));
            return Ok(new InboxResult { Messages = opened });
        });

    /// <summary>Packages and uploads one attachment sealed for <paramref name="to"/>, as <c>send</c>'s <c>attach</c> does.</summary>
    public Task<CallToolResult> AttachAsync(MessageTarget to, AttachSpec spec, string? threadId, CancellationToken ct) =>
        GuardAsync("attach", async () =>
        {
            var key = await RecipientAsync(to, ct);
            return Ok(await PackageAsync(spec, to, key, threadId, "attach", ct));
        });
}

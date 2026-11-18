using System.Net;
using MessageBus.Attachments.Client;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

/// <summary>
/// An attachment a runner task's <c>settle</c> couldn't send (the original issue): a path outside the run folder, over the size limit,
/// a branch git can't package. The call fails, saying what to fix, so the agent fixes it and settles once with its answer
/// and code together. Meanwhile the attempted answer is kept in the run folder (<see cref="KeptAnswer.KeptFile"/>): if the agent ends
/// without settling, the runner sends that one, naming what wasn't attached, so an answer is never lost.
/// </summary>
/// <param name="Index">Its place in <c>attach</c>.</param>
/// <param name="Spec">What the agent asked to attach.</param>
/// <param name="Code">The error code the call fails with.</param>
/// <param name="Reason">Why it wasn't sent.</param>
internal sealed record RefusedAttachment(int Index, AttachSpec Spec, string Code, string Reason)
{
    /// <summary>
    /// The code and reason when the agent can fix this (the path, the size, the branch); null for what may pass next time
    /// (the network, the service), which fails the call as it is so the agent retries it.
    /// </summary>
    public static (string Code, string Reason)? Why(Exception e) => e switch
    {
        McpToolException { Error.Code: McpErrorCodes.Forbidden or McpErrorCodes.TooLarge or McpErrorCodes.InvalidInput } t => (t.Error.Code, t.Error.Message),
        AttachmentTransferException { Code: McpErrorCodes.TooLarge } or AttachmentTransferException { Status: HttpStatusCode.RequestEntityTooLarge } =>
            (McpErrorCodes.TooLarge, "it is over the size limit: " + LocalTools.Reason(e)),
        GitException => (McpErrorCodes.InvalidInput, "git couldn't package it: " + LocalTools.Reason(e)),
        _ => null,
    };

    /// <summary>Which attachment, as the agent gave it: <c>attach[0] (patch /tmp/x)</c>.</summary>
    public string Describe() =>
        $"attach[{Index}] ({Spec.Format switch { AttachmentFormat.GitBundle => "git bundle", AttachmentFormat.Patch => "patch", _ => "file" }}"
        + (Spec.Path is { Length: > 0 } path ? " " + path : "") + ")";

    private static string List(IReadOnlyList<RefusedAttachment> refused) => string.Join("; ", refused.Select(r => $"{r.Describe()}: {r.Reason}"));

    /// <summary>
    /// The answer to keep: <paramref name="input"/> (with what did package) naming what wasn't sent in its result's refs and at
    /// the end of its reply's details. Its status stays the agent's; with nothing attached at all, its summary says so too.
    /// </summary>
    public static SettleInput Kept(SettleInput input, IReadOnlyList<RefusedAttachment> refused)
    {
        var notSent = $"Not attached: {List(refused)}.";
        var nothingSent = input.Attachments is not { Count: > 0 };
        return input with
        {
            Result = input.Result with
            {
                Summary = nothingSent ? $"{input.Result.Summary} (Nothing it tried to attach was sent; see the reply.)".TrimStart() : input.Result.Summary,
                Refs = [.. input.Result.Refs, .. refused.Select(r => new Ref { Kind = RefKinds.AttachmentRefused, Path = r.Spec.Path, Title = "Not attached: " + r.Reason })],
            },
            Reply = input.Reply is { } reply ? reply with { Details = reply.Details is { Length: > 0 } d ? d + "\n\n" + notSent : notSent } : null,
        };
    }

    /// <summary>The settle call's error: what to fix, and (a runner's task) where its files must be.</summary>
    /// <param name="refused">What wasn't sent.</param>
    /// <param name="runFolder">The run's folder, for a runner's task; null otherwise.</param>
    public static McpToolException Refusal(IReadOnlyList<RefusedAttachment> refused, string? runFolder)
    {
        var fix = runFolder is not null && refused.Any(r => r.Code == McpErrorCodes.Forbidden)
            ? $" Put it inside the run folder: {runFolder}."
            : " Fix it.";
        return new McpToolException(new McpError
        {
            Code = refused[0].Code,
            Field = $"attach[{refused[0].Index}]",
            Message = $"Not settled: {List(refused)}.{fix} Then call settle again with your answer and every attachment; no answer was sent yet."
                + (runFolder is not null ? " (If this run ends without a settle, your answer is sent with what did attach, saying what didn't.)" : ""),
        });
    }
}

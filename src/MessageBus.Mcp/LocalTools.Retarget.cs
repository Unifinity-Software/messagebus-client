using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Crypto;
using ModelContextProtocol;

namespace MessageBus.Mcp;

internal sealed partial class LocalTools
{
    private async Task<RetargetResult> RetargetAsync(RetargetInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId)) throw McpToolException.Invalid("threadId", "Give the queued task's thread id.");
        if (input.To is not (RoleTarget or AgentTarget or CapabilityTarget))
            throw McpToolException.Invalid("to", "Retarget to a role, agent or capabilities target.");
        if (input.Reason?.Length > 500) throw McpToolException.Invalid("reason", "The reason must be at most 500 characters.");
        if (!input.DryRun)
        {
            if (string.IsNullOrWhiteSpace(input.OperationId)) throw McpToolException.Invalid("operationId", "Give a unique operation id and reuse it for retries.");
            if (string.IsNullOrWhiteSpace(input.ExpectedTaskMessageId))
                throw McpToolException.Invalid("expectedTaskMessageId", "Preview with dryRun, then name its previousMessageId.");
        }

        // Handler negotiation is a real dry-run call. An advertised tools/list entry does not prove
        // the deployed server implements it. Never read/download/reseal/upload before this succeeds.
        RetargetResult preview;
        try
        {
            preview = await ForwardAsync<RetargetInput, RetargetResult>(McpToolNames.Retarget,
                input with { DryRun = true, SealedBody = null, Attachments = null }, ct);
        }
        catch (McpToolException e) when (e.Error.Code == McpErrorCodes.NotFound)
        {
            throw McpToolException.NotFound("The service could not preview retargeting (the task may be missing or the handler is unavailable). No content was uploaded. " + e.Error.Message);
        }
        catch (McpProtocolException e) when (e.ErrorCode == McpErrorCode.MethodNotFound
            || e.ErrorCode == McpErrorCode.InvalidParams && (e.Message.Contains("unknown tool", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            throw McpToolException.NotFound("This service does not support retargeting yet. No content was uploaded; update the service and try again.");
        }
        if (input.DryRun || preview.AlreadyApplied) return preview;
        if (preview.ThreadId != input.ThreadId || preview.PreviousMessageId != input.ExpectedTaskMessageId)
            throw new McpToolException(new McpError { Code = McpErrorCodes.Conflict, Field = "expectedTaskMessageId", Message = "The queued task changed. Preview it again before retargeting." });

        var prepared = input with { SealedBody = null, Attachments = null };
        if (preview.NeedsReseal)
        {
            var messages = await ThreadMessagesAsync(input.ThreadId, ct);
            var envelope = messages.SingleOrDefault(m => m.Id == input.ExpectedTaskMessageId && m.ThreadId == input.ThreadId && m.WorkspaceId == _ws)
                ?? throw McpToolException.NotFound("The previewed task message could not be read. Preview the task again.");
            try
            {
                var (body, files) = await TaskReseal.ResealAsync(_keys, _o.Http, _attachments, envelope, input.To, ct,
                    preview.Recipient?.KeyId, _o.TempDirectory);
                prepared = prepared with { SealedBody = body, Attachments = files };
            }
            catch (MissingKeyException e)
            {
                throw McpToolException.Forbidden($"This machine does not hold the old key {e.KeyId}. Run mbus retarget on a machine holding all original role/workspace keys. No replacements were uploaded.");
            }
        }
        return await ForwardAsync<RetargetInput, RetargetResult>(McpToolNames.Retarget, prepared, ct);
    }
}

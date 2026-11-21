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
        if (input.ResumeOnly && (input.DryRun || input.SealedBody is not null || input.Attachments is not null))
            throw McpToolException.Invalid("resumeOnly", "Resume using the original request without preview or replacement content.");
        if (!input.DryRun)
        {
            if (string.IsNullOrWhiteSpace(input.OperationId)) throw McpToolException.Invalid("operationId", "Give a unique operation id and reuse it for retries.");
            if (string.IsNullOrWhiteSpace(input.ExpectedTaskMessageId))
                throw McpToolException.Invalid("expectedTaskMessageId", "Preview with dryRun, then name its previousMessageId.");
        }

        RetargetResult preview;
        try
        {
            if (!input.DryRun)
            {
                // A prior commit may have replaced the task and made the original preview stale.
                // Resolve that history before examining the current task or touching its content.
                try
                {
                    var resumed = await ForwardAsync<RetargetInput, RetargetResult>(McpToolNames.Retarget,
                        input with { ResumeOnly = true, SealedBody = null, Attachments = null }, ct);
                    if (resumed.DryRun || resumed.ThreadId != input.ThreadId || resumed.PreviousMessageId != input.ExpectedTaskMessageId)
                        throw new McpToolException(new McpError { Code = McpErrorCodes.Conflict, Message = "The service returned a result for a different retarget operation." });
                    return resumed;
                }
                catch (McpToolException e) when (!input.ResumeOnly && e.Error.Code == McpErrorCodes.RetargetOperationAbsent)
                {
                    // Only the supported handler's explicit absence permits new preparation.
                }
            }
            preview = await ForwardAsync<RetargetInput, RetargetResult>(McpToolNames.Retarget,
                input with { DryRun = true, ResumeOnly = false, SealedBody = null, Attachments = null }, ct);
        }
        catch (McpToolException e) when (e.Error.Code == McpErrorCodes.NotFound)
        {
            throw McpToolException.NotFound("The service could not resolve the retarget request. No content was uploaded. " + e.Error.Message);
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

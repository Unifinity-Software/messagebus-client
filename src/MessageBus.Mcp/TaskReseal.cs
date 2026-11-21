using System.Security.Cryptography;
using MessageBus.Attachments.Client;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;

namespace MessageBus.Mcp;

/// <summary>Shared replay/retarget resealing. Plain inputs and inputs already using the destination key are retained.</summary>
internal static class TaskReseal
{
    // Replay predates independent inputs. Discover support before any body/file transfer,
    // using the operation's authenticated target; reuse this key when preparing replacements.
    internal static async Task<PublicKeyInfo?> PrepareReplayAsync(
        MachineKeys keys, HttpClient http, MessageEnvelope envelope, RoleTarget to, CancellationToken ct)
    {
        if (envelope.Encryption is null && envelope.Attachments.All(a => a.Encryption is null)) return null;
        var key = await keys.RecipientAsync(to, ct);
        var bodyChanges = envelope.Encryption is { } body && body.KeyId != key.KeyId;
        var filesChange = envelope.Attachments.Any(a => a.Encryption is { } file && file.KeyId != key.KeyId);
        var legacyBodyMove = envelope.Encryption is not null && envelope.Target is RoleTarget from && from.Role != to.Role;
        var independent = filesChange && !(legacyBodyMove && bodyChanges)
            || bodyChanges && !legacyBodyMove || legacyBodyMove && !bodyChanges;
        if (independent && !await TargetCapabilities.SupportsAsync(http, "replayIndependentInputsV1", ct))
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.Conflict,
                Message = "This service doesn't confirm support for replaying body and attachment keys independently. Upgrade the service before replaying this task. No replacement inputs were uploaded.",
            });
        return key;
    }

    internal static async Task<(SealedBody? Body, IReadOnlyList<AttachmentRef>? Attachments)> ResealAsync(
        MachineKeys keys, HttpClient http, AttachmentClient attachments, MessageEnvelope envelope, MessageTarget to, CancellationToken ct,
        string? expectedRecipientKeyId = null, string? tempDirectory = null, PublicKeyInfo? recipientKey = null)
    {
        if (envelope.Encryption is null && envelope.Attachments.All(a => a.Encryption is null)) return (null, null);
        var key = recipientKey ?? await keys.RecipientAsync(to, ct);
        if (expectedRecipientKeyId is not null && key.KeyId != expectedRecipientKeyId)
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.Conflict,
                Message = "The destination key changed after the preview. Preview again before retargeting. No replacements were uploaded.",
            });
        var bodyChanges = envelope.Encryption is { } bodyEncryption && bodyEncryption.KeyId != key.KeyId;
        var changedFiles = envelope.Attachments.Where(a => a.Encryption is { } encryption && encryption.KeyId != key.KeyId).ToArray();
        if (!bodyChanges && changedFiles.Length == 0) return (null, null);

        // Check every old key before downloading or uploading anything. In particular a missing key on
        // a later file must not leave earlier replacements uploaded. WithRing retries only this read.
        await keys.WithRingAsync(ring =>
        {
            var needed = changedFiles.Select(a => a.Encryption!.KeyId);
            if (bodyChanges) needed = needed.Prepend(envelope.Encryption!.KeyId);
            foreach (var id in needed.Distinct(StringComparer.Ordinal))
                if (ring.Find(id) is null) throw new MissingKeyException(id);
            return Task.FromResult(true);
        }, ct);

        SealedBody? body = null;
        if (bodyChanges)
        {
            var ciphertext = envelope.Body.Data is { } data ? Convert.FromBase64String(data)
                : await StoredBodyAsync(http, keys.WorkspaceId, envelope, ct);
            var plaintext = await keys.OpenAsync(ciphertext, envelope.Encryption!, ct);
            var sealedBytes = BodyCrypto.Seal(plaintext, key);
            body = new SealedBody
            {
                Body = new BodyRef
                {
                    SizeBytes = sealedBytes.Ciphertext.Length, Sha256 = sealedBytes.Sha256,
                    ContentType = MessageLimits.BodyContentType, Data = Convert.ToBase64String(sealedBytes.Ciphertext),
                },
                Encryption = sealedBytes.Encryption,
            };
        }

        if (changedFiles.Length == 0) return (body, null);
        var moved = new List<AttachmentRef>();
        foreach (var attachment in envelope.Attachments)
        {
            if (attachment.Encryption is null || attachment.Encryption.KeyId == key.KeyId)
            {
                moved.Add(attachment);
                continue;
            }
            var file = Path.Combine(tempDirectory ?? Path.GetTempPath(), "mbus-reseal-" + Guid.NewGuid().ToString("N"));
            try
            {
                var link = await attachments.DownloadLinkAsync(attachment.Id, ct);
                await keys.WithRingAsync(async ring =>
                {
                    await attachments.DownloadToFileAsync(attachment, link, file, ring, ct);
                    return true;
                }, ct);
                await using var plaintext = File.OpenRead(file);
                moved.Add(await attachments.UploadAsync(plaintext,
                    new AttachmentUpload(to, attachment.Name, attachment.Format, envelope.ThreadId,
                        attachment.ContentType, attachment.Branch, attachment.BaseCommit), key, ct));
            }
            finally
            {
                File.Delete(file);
            }
        }
        return (body, moved);
    }

    private static async Task<byte[]> StoredBodyAsync(HttpClient http, string workspaceId, MessageEnvelope envelope, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"api/w/{Uri.EscapeDataString(workspaceId)}/threads/{Uri.EscapeDataString(envelope.ThreadId)}/messages/{Uri.EscapeDataString(envelope.Id)}/body", ct);
        if (!response.IsSuccessStatusCode)
            throw McpToolException.Unavailable($"The stored task body couldn't be read ({(int)response.StatusCode}).");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length != envelope.Body.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != envelope.Body.Sha256)
            throw McpToolException.Invalid("sealedBody", "The stored body doesn't match the message's size and SHA-256.");
        return bytes;
    }
}

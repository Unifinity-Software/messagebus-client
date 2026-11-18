using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>
/// Seals and opens <see cref="MessageBody"/> values, for the local <c>mbus mcp</c> server: it swaps an
/// agent's plaintext <c>body</c> for a <see cref="SealedBody"/> before calling the service, and opens
/// bodies on the way back.
/// </summary>
public static class BodyCrypto
{
    /// <summary>Serializes the body (<see cref="MessageBusJson"/>) and seals it to <paramref name="recipient"/>.</summary>
    /// <param name="body">The plaintext body.</param>
    /// <param name="recipient">The recipient key (see <see cref="KeyDirectory.RecipientFor"/>).</param>
    /// <exception cref="ArgumentException">The serialized body is over <see cref="MessageLimits.MaxBodyBytes"/>.</exception>
    public static SealedBytes Seal(MessageBody body, PublicKeyInfo recipient)
    {
        var plaintext = Encoding.UTF8.GetBytes(MessageBusJson.Serialize(body));
        try
        {
            if (plaintext.Length > MessageLimits.MaxBodyBytes)
                throw new ArgumentException($"The body is {plaintext.Length} bytes; the limit is {MessageLimits.MaxBodyBytes}. Attach large content instead.", nameof(body));
            return MessageCrypto.Seal(plaintext, recipient);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The <see cref="SealedBody"/> to send: the ciphertext inline when it fits
    /// (<see cref="MessageLimits.InlineBodyBytes"/>), otherwise a reference to <paramref name="blobName"/>,
    /// where the caller has uploaded <see cref="SealedBytes.Ciphertext"/>.
    /// </summary>
    /// <param name="sealedBytes">The sealed body.</param>
    /// <param name="blobName">The uploaded blob's name, for a body too large to send inline.</param>
    public static SealedBody ToSealedBody(this SealedBytes sealedBytes, string? blobName = null)
    {
        var inline = blobName is null;
        if (inline && sealedBytes.Ciphertext.Length > MessageLimits.InlineBodyBytes)
            throw new ArgumentException($"A sealed body over {MessageLimits.InlineBodyBytes} bytes must be uploaded; pass its blob name.", nameof(blobName));
        return new SealedBody
        {
            Body = new BodyRef
            {
                SizeBytes = sealedBytes.Ciphertext.Length,
                Sha256 = sealedBytes.Sha256,
                ContentType = MessageLimits.BodyContentType,
                Data = inline ? Convert.ToBase64String(sealedBytes.Ciphertext) : null,
                BlobName = blobName,
            },
            Encryption = sealedBytes.Encryption,
        };
    }

    /// <summary>Opens an inline <see cref="SealedBody"/>. For one stored as a blob, download it and use <see cref="Open(ReadOnlySpan{byte}, EncryptionInfo, IKeyRing)"/>.</summary>
    /// <param name="sealedBody">The sealed body.</param>
    /// <param name="keys">This machine's private keys.</param>
    public static MessageBody Open(SealedBody sealedBody, IKeyRing keys)
    {
        if (sealedBody.Body.Data is null)
            throw new InvalidOperationException("The body is stored as a blob; download it and open the bytes.");
        return Open(Convert.FromBase64String(sealedBody.Body.Data), sealedBody.Encryption, keys);
    }

    /// <summary>Opens a sealed body's ciphertext.</summary>
    /// <param name="ciphertext">The ciphertext (inline data or the downloaded blob).</param>
    /// <param name="encryption">How it was sealed.</param>
    /// <param name="keys">This machine's private keys.</param>
    /// <exception cref="MissingKeyException">This machine doesn't hold the key it was sealed to.</exception>
    /// <exception cref="CryptographicException">It was changed or cut off.</exception>
    public static MessageBody Open(ReadOnlySpan<byte> ciphertext, EncryptionInfo encryption, IKeyRing keys)
    {
        var plaintext = MessageCrypto.Open(ciphertext, encryption, keys);
        try
        {
            return MessageBusJson.Deserialize<MessageBody>(Encoding.UTF8.GetString(plaintext))
                   ?? throw new CryptographicException("The decrypted body is empty.");
        }
        catch (JsonException e)
        {
            throw new CryptographicException("The decrypted body isn't a message body.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

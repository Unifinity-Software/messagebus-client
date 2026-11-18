using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>Bytes sealed to one recipient key, with what the receiver needs to open them.</summary>
/// <param name="Ciphertext">The ciphertext with the GCM tag appended. This is what the service stores.</param>
/// <param name="Encryption">How it was sealed (goes on the envelope or attachment).</param>
public sealed record SealedBytes(byte[] Ciphertext, EncryptionInfo Encryption)
{
    /// <summary>Lowercase hex SHA-256 of <see cref="Ciphertext"/> (<see cref="BodyRef.Sha256"/>).</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Ciphertext));
}

/// <summary>The outcome of sealing a stream.</summary>
/// <param name="Encryption">How it was sealed (goes on the <see cref="AttachmentRef"/>).</param>
/// <param name="CiphertextLength">Bytes written (<see cref="AttachmentRef.SizeBytes"/>).</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the bytes written (<see cref="AttachmentRef.Sha256"/>).</param>
public sealed record SealedStreamResult(EncryptionInfo Encryption, long CiphertextLength, string Sha256);

/// <summary>
/// End-to-end encryption of message bodies and attachments. Content is AES-256-GCM under a fresh
/// random key per message or file; that key is wrapped to the recipient's public key with
/// ECDH-ES+A256KW (P-256, RFC 7518) or RSA-OAEP-256. Only a holder of the recipient's private key
/// (a machine with a key grant for the role) can open it.
/// </summary>
/// <remarks>
/// Byte arrays (<see cref="Seal(ReadOnlySpan{byte}, PublicKeyInfo)"/>) suit bodies. Streams
/// (<see cref="SealAsync(Stream, Stream, PublicKeyInfo, CancellationToken)"/>) suit attachments of any
/// size: content is cut into 64 KiB chunks, each sealed with its index and a final-chunk flag, so
/// reordering, truncation and appended bytes are detected.
/// </remarks>
public static class MessageCrypto
{
    /// <summary>Plaintext bytes per chunk in a sealed stream.</summary>
    public const int StreamChunkSize = 64 * 1024;

    private const int StreamHeaderSize = 8;
    private static ReadOnlySpan<byte> StreamMagic => "MBS1"u8;

    /// <summary>Seals bytes to the key a <see cref="PublicKeyInfo"/> record describes (fingerprint checked).</summary>
    /// <param name="plaintext">The plaintext.</param>
    /// <param name="recipient">The recipient key: a role's current key, the workspace key or a machine key.</param>
    public static SealedBytes Seal(ReadOnlySpan<byte> plaintext, PublicKeyInfo recipient) =>
        Seal(plaintext, recipient.KeyId, PublicKey.FromInfo(recipient));

    /// <summary>Seals bytes to <paramref name="recipient"/>, recorded as key <paramref name="keyId"/>.</summary>
    /// <param name="plaintext">The plaintext.</param>
    /// <param name="keyId">The recipient key's id (<c>ek_…</c>); the receiver looks its private key up by this id.</param>
    /// <param name="recipient">The recipient public key.</param>
    public static SealedBytes Seal(ReadOnlySpan<byte> plaintext, string keyId, PublicKey recipient)
    {
        var cek = RandomNumberGenerator.GetBytes(GcmContent.KeySize);
        var nonce = RandomNumberGenerator.GetBytes(GcmContent.NonceSize);
        try
        {
            var info = NewInfo(keyId, recipient, cek, nonce);
            var ciphertext = GcmContent.Encrypt(cek, nonce, plaintext, AssociatedData(info, Mode.Bytes));
            return new SealedBytes(ciphertext, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    /// <summary>Opens bytes sealed by <see cref="Seal(ReadOnlySpan{byte}, PublicKeyInfo)"/>.</summary>
    /// <param name="ciphertext">The ciphertext with its tag.</param>
    /// <param name="encryption">How it was sealed.</param>
    /// <param name="keys">This machine's private keys.</param>
    /// <exception cref="MissingKeyException">This machine doesn't hold the key it was sealed to.</exception>
    /// <exception cref="CryptographicException">It was changed, cut off, or sealed to a different key with the same id.</exception>
    public static byte[] Open(ReadOnlySpan<byte> ciphertext, EncryptionInfo encryption, IKeyRing keys)
    {
        var view = EncryptionInfoView.Parse(encryption);
        var cek = UnwrapContentKey(view, keys);
        try
        {
            if (ciphertext.Length < GcmContent.TagSize) throw new CryptographicException("The ciphertext is too short: it was cut off.");
            var plaintext = new byte[ciphertext.Length - GcmContent.TagSize];
            GcmContent.Decrypt(cek, view.Nonce, ciphertext, AssociatedData(encryption, Mode.Bytes), plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
        }
    }

    /// <summary>Seals a stream (an attachment) to the key a <see cref="PublicKeyInfo"/> record describes, writing to <paramref name="destination"/>.</summary>
    /// <param name="plaintext">The plaintext, read to its end.</param>
    /// <param name="destination">Where the ciphertext goes (a file or an upload stream).</param>
    /// <param name="recipient">The recipient key.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    public static Task<SealedStreamResult> SealAsync(Stream plaintext, Stream destination, PublicKeyInfo recipient,
        CancellationToken cancellationToken = default) =>
        SealAsync(plaintext, destination, recipient.KeyId, PublicKey.FromInfo(recipient), cancellationToken);

    /// <summary>Seals a stream to <paramref name="recipient"/>, recorded as key <paramref name="keyId"/>.</summary>
    /// <param name="plaintext">The plaintext, read to its end.</param>
    /// <param name="destination">Where the ciphertext goes.</param>
    /// <param name="keyId">The recipient key's id.</param>
    /// <param name="recipient">The recipient public key.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    public static async Task<SealedStreamResult> SealAsync(Stream plaintext, Stream destination, string keyId, PublicKey recipient,
        CancellationToken cancellationToken = default)
    {
        var cek = RandomNumberGenerator.GetBytes(GcmContent.KeySize);
        var baseNonce = RandomNumberGenerator.GetBytes(GcmContent.NonceSize);
        var input = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        var output = ArrayPool<byte>.Shared.Rent(StreamChunkSize + GcmContent.TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var info = NewInfo(keyId, recipient, cek, baseNonce);
            var header = StreamHeader();
            var aad = Concat(AssociatedData(info, Mode.Stream), header);
            await destination.WriteAsync(header, cancellationToken);
            hash.AppendData(header);
            long written = header.Length;

            for (uint index = 0; ; index++)
            {
                var read = await plaintext.ReadAtLeastAsync(input.AsMemory(0, StreamChunkSize), StreamChunkSize,
                    throwOnEndOfStream: false, cancellationToken);
                // A full chunk is never the last one; the last chunk is short (possibly empty), so a
                // reader can tell where the stream ends and notices if it was cut at a chunk boundary.
                var final = read < StreamChunkSize;
                if (index == uint.MaxValue && !final) throw new CryptographicException("The stream is too long to seal.");
                var nonce = ChunkNonce(baseNonce, index, final);
                GcmContent.Encrypt(cek, nonce, input.AsSpan(0, read), aad, output);
                var length = read + GcmContent.TagSize;
                await destination.WriteAsync(output.AsMemory(0, length), cancellationToken);
                hash.AppendData(output, 0, length);
                written += length;
                if (final) break;
            }

            return new SealedStreamResult(info, written, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
            CryptographicOperations.ZeroMemory(input);
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>
    /// Opens a stream sealed by <see cref="SealAsync(Stream, Stream, PublicKeyInfo, CancellationToken)"/>, writing
    /// plaintext to <paramref name="destination"/> chunk by chunk as each chunk checks out.
    /// </summary>
    /// <remarks>
    /// If this throws, what was already written is incomplete and must be discarded: write to a
    /// temporary file and move it into place only when this returns.
    /// </remarks>
    /// <param name="source">The ciphertext, read to its end.</param>
    /// <param name="destination">Where the plaintext goes.</param>
    /// <param name="encryption">How it was sealed.</param>
    /// <param name="keys">This machine's private keys.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <exception cref="MissingKeyException">This machine doesn't hold the key it was sealed to.</exception>
    /// <exception cref="CryptographicException">It was changed, reordered, cut off or extended.</exception>
    public static async Task OpenAsync(Stream source, Stream destination, EncryptionInfo encryption, IKeyRing keys,
        CancellationToken cancellationToken = default)
    {
        var view = EncryptionInfoView.Parse(encryption);
        var cek = UnwrapContentKey(view, keys);
        var input = ArrayPool<byte>.Shared.Rent(StreamChunkSize + GcmContent.TagSize);
        var output = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        try
        {
            var header = new byte[StreamHeaderSize];
            var headerRead = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            if (headerRead < header.Length || !header.AsSpan().SequenceEqual(StreamHeader()))
                throw new CryptographicException("Not a sealed attachment stream (bad header), or it was cut off.");
            var aad = Concat(AssociatedData(encryption, Mode.Stream), header);

            var full = StreamChunkSize + GcmContent.TagSize;
            for (uint index = 0; ; index++)
            {
                var read = await source.ReadAtLeastAsync(input.AsMemory(0, full), full, throwOnEndOfStream: false, cancellationToken);
                var final = read < full;
                var nonce = ChunkNonce(view.Nonce, index, final);
                GcmContent.Decrypt(cek, nonce, input.AsSpan(0, read), aad, output);
                await destination.WriteAsync(output.AsMemory(0, read - GcmContent.TagSize), cancellationToken);
                if (final) break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
            CryptographicOperations.ZeroMemory(output);
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    /// <summary>The size of a sealed stream for a plaintext of <paramref name="plaintextLength"/> bytes (for an upload's Content-Length).</summary>
    /// <param name="plaintextLength">Plaintext bytes.</param>
    public static long SealedLength(long plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        var chunks = plaintextLength / StreamChunkSize + 1; // the last chunk is short or empty
        return StreamHeaderSize + plaintextLength + chunks * GcmContent.TagSize;
    }

    private static EncryptionInfo NewInfo(string keyId, PublicKey recipient, byte[] cek, byte[] nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        var (wrapped, ephemeral) = recipient.WrapContentKey(cek);
        return new EncryptionInfo
        {
            Algorithm = EncryptionAlgorithms.Aes256Gcm,
            KeyWrapAlgorithm = recipient.KeyWrapAlgorithm,
            KeyId = keyId,
            WrappedKey = Convert.ToBase64String(wrapped),
            Nonce = Convert.ToBase64String(nonce),
            EphemeralPublicKey = ephemeral is null ? null : Convert.ToBase64String(ephemeral),
        };
    }

    private static byte[] UnwrapContentKey(EncryptionInfoView view, IKeyRing keys)
    {
        var key = keys.Find(view.KeyId) ?? throw new MissingKeyException(view.KeyId);
        var cek = key.UnwrapContentKey(view);
        if (cek.Length != GcmContent.KeySize)
        {
            CryptographicOperations.ZeroMemory(cek);
            throw new CryptographicException("The content key has the wrong length.");
        }
        return cek;
    }

    private enum Mode { Bytes, Stream }

    /// <summary>
    /// Binds the algorithms, the key id and the kind of payload to the ciphertext, so none of them can be
    /// swapped on the stored record without the integrity check failing.
    /// </summary>
    private static byte[] AssociatedData(EncryptionInfo info, Mode mode) =>
        Encoding.UTF8.GetBytes($"mbus-e2e-v1\n{info.Algorithm}\n{info.KeyWrapAlgorithm}\n{info.KeyId}\n{(mode == Mode.Bytes ? "bytes" : "stream")}");

    private static byte[] StreamHeader()
    {
        var header = new byte[StreamHeaderSize];
        StreamMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), StreamChunkSize);
        return header;
    }

    /// <summary>The chunk nonce: the random base nonce with its last five bytes XORed with the chunk index and the final flag.</summary>
    private static byte[] ChunkNonce(ReadOnlySpan<byte> baseNonce, uint index, bool final)
    {
        var nonce = baseNonce.ToArray();
        Span<byte> counter = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(counter, index);
        counter[4] = final ? (byte)1 : (byte)0;
        for (var i = 0; i < 5; i++) nonce[7 + i] ^= counter[i];
        return nonce;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

/// <summary>An <see cref="EncryptionInfo"/> with its base64 fields decoded and checked.</summary>
internal sealed record EncryptionInfoView(string KeyId, string KeyWrapAlgorithm, byte[] WrappedKey, byte[] Nonce, byte[]? EphemeralPublicKey)
{
    public static EncryptionInfoView Parse(EncryptionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Algorithm != EncryptionAlgorithms.Aes256Gcm)
            throw new CryptographicException($"Unsupported content algorithm {info.Algorithm}.");
        if (info.KeyWrapAlgorithm is not (EncryptionAlgorithms.EcdhEsA256Kw or EncryptionAlgorithms.RsaOaep256))
            throw new CryptographicException($"Unsupported key-wrap algorithm {info.KeyWrapAlgorithm}.");
        try
        {
            var nonce = Convert.FromBase64String(info.Nonce);
            if (nonce.Length != GcmContent.NonceSize) throw new CryptographicException("The nonce must be 12 bytes.");
            return new EncryptionInfoView(info.KeyId, info.KeyWrapAlgorithm, Convert.FromBase64String(info.WrappedKey), nonce,
                info.EphemeralPublicKey is null ? null : Convert.FromBase64String(info.EphemeralPublicKey));
        }
        catch (FormatException e)
        {
            throw new CryptographicException("The encryption details aren't valid base64.", e);
        }
    }
}

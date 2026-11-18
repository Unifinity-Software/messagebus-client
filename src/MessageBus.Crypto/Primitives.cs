using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MessageBus.Crypto;

/// <summary>AES key wrap (RFC 3394), the <c>A256KW</c> step of <c>ECDH-ES+A256KW</c>. Built on AES-ECB from .NET.</summary>
internal static class AesKeyWrap
{
    private static readonly byte[] DefaultIv = [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    public static byte[] Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> keyData)
    {
        if (keyData.Length < 16 || keyData.Length % 8 != 0)
            throw new ArgumentException("Key data must be a multiple of 8 bytes, at least 16.", nameof(keyData));

        using var aes = Aes.Create();
        aes.Key = kek.ToArray();
        var n = keyData.Length / 8;
        var output = new byte[keyData.Length + 8];
        Span<byte> a = output.AsSpan(0, 8);
        DefaultIv.CopyTo(a);
        keyData.CopyTo(output.AsSpan(8));
        Span<byte> block = stackalloc byte[16];

        for (var j = 0; j <= 5; j++)
        {
            for (var i = 1; i <= n; i++)
            {
                var r = output.AsSpan(i * 8, 8);
                a.CopyTo(block);
                r.CopyTo(block[8..]);
                aes.EncryptEcb(block, block, PaddingMode.None);
                block[..8].CopyTo(a);
                XorCounter(a, (ulong)(n * j + i));
                block[8..].CopyTo(r);
            }
        }
        return output;
    }

    public static byte[] Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
            throw new CryptographicException("The wrapped key has the wrong length.");

        using var aes = Aes.Create();
        aes.Key = kek.ToArray();
        var n = wrapped.Length / 8 - 1;
        Span<byte> a = stackalloc byte[8];
        wrapped[..8].CopyTo(a);
        var r = wrapped[8..].ToArray();
        Span<byte> block = stackalloc byte[16];

        for (var j = 5; j >= 0; j--)
        {
            for (var i = n; i >= 1; i--)
            {
                XorCounter(a, (ulong)(n * j + i));
                a.CopyTo(block);
                r.AsSpan((i - 1) * 8, 8).CopyTo(block[8..]);
                aes.DecryptEcb(block, block, PaddingMode.None);
                block[..8].CopyTo(a);
                block[8..].CopyTo(r.AsSpan((i - 1) * 8, 8));
            }
        }

        if (!CryptographicOperations.FixedTimeEquals(a, DefaultIv))
        {
            CryptographicOperations.ZeroMemory(r);
            throw new CryptographicException("The content key could not be unwrapped: wrong key, or the message was changed.");
        }
        return r;
    }

    private static void XorCounter(Span<byte> a, ulong t)
    {
        Span<byte> tb = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tb, t);
        for (var k = 0; k < 8; k++) a[k] ^= tb[k];
    }
}

/// <summary>
/// The Concat KDF of NIST SP 800-56A as JOSE uses it for ECDH-ES (RFC 7518 §4.6.2): one SHA-256
/// round over counter 1, the shared secret and OtherInfo (algorithm id, empty party infos, key length).
/// </summary>
internal static class ConcatKdf
{
    public static byte[] DeriveKey(ECDiffieHellman privateKey, ECDiffieHellmanPublicKey otherPublicKey,
        string algorithmId, ReadOnlySpan<byte> partyUInfo, ReadOnlySpan<byte> partyVInfo, int keyBits)
    {
        if (keyBits is <= 0 or > 256 || keyBits % 8 != 0) throw new ArgumentOutOfRangeException(nameof(keyBits));

        var alg = Encoding.ASCII.GetBytes(algorithmId);
        var otherInfo = new byte[4 + alg.Length + 4 + partyUInfo.Length + 4 + partyVInfo.Length + 4];
        var span = otherInfo.AsSpan();
        WriteWithLength(ref span, alg);
        WriteWithLength(ref span, partyUInfo);
        WriteWithLength(ref span, partyVInfo);
        BinaryPrimitives.WriteInt32BigEndian(span, keyBits);

        var derived = privateKey.DeriveKeyFromHash(otherPublicKey, HashAlgorithmName.SHA256,
            secretPrepend: [0, 0, 0, 1], secretAppend: otherInfo);
        if (derived.Length == keyBits / 8) return derived;
        var key = derived[..(keyBits / 8)];
        CryptographicOperations.ZeroMemory(derived);
        return key;
    }

    private static void WriteWithLength(ref Span<byte> span, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(span, value.Length);
        value.CopyTo(span[4..]);
        span = span[(4 + value.Length)..];
    }
}

/// <summary>AES-256-GCM with the 16-byte tag appended to the ciphertext (the layout <see cref="Contracts.Messaging.EncryptionInfo"/> documents).</summary>
internal static class GcmContent
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData, Span<byte> destination)
    {
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, destination[..plaintext.Length], destination.Slice(plaintext.Length, TagSize), associatedData);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[plaintext.Length + TagSize];
        Encrypt(key, nonce, plaintext, associatedData, output);
        return output;
    }

    /// <summary>Decrypts <paramref name="ciphertextAndTag"/> into <paramref name="destination"/> (its length minus the tag).</summary>
    public static void Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> associatedData, Span<byte> destination)
    {
        if (ciphertextAndTag.Length < TagSize)
            throw new CryptographicException("The ciphertext is too short: it was cut off.");
        var length = ciphertextAndTag.Length - TagSize;
        using var gcm = new AesGcm(key, TagSize);
        try
        {
            gcm.Decrypt(nonce, ciphertextAndTag[..length], ciphertextAndTag[length..], destination[..length], associatedData);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new CryptographicException("The content failed its integrity check: it was changed, cut off or encrypted to another key.", e);
        }
    }
}

using System.Security.Cryptography;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>Attachments (git bundles, files) sealed as a stream of chunks: they round-trip at any size and every kind of damage is caught.</summary>
public sealed class StreamTests : IDisposable
{
    private const int Chunk = MessageCrypto.StreamChunkSize;
    private const int SealedChunk = Chunk + 16;
    private const int Header = 8;

    private readonly KeyPair _roleKey = KeyPair.GenerateP256();
    private readonly PublicKeyInfo _info;
    private readonly KeyRing _ring;

    public StreamTests()
    {
        _info = TestKeys.Info(_roleKey, KeyScope.Role, "developer");
        _ring = TestKeys.Ring(_info, _roleKey);
    }

    public void Dispose() => _ring.Dispose();

    public static TheoryData<int> Sizes => [0, 1, Chunk - 1, Chunk, Chunk + 1, 3 * Chunk + 5];

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task A_stream_round_trips_and_reports_the_size_and_hash_of_what_was_written(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var sealedStream = new MemoryStream();

        var result = await MessageCrypto.SealAsync(new MemoryStream(plaintext), sealedStream, _info, TestContext.Current.CancellationToken);

        var stored = sealedStream.ToArray();
        Assert.Equal(stored.Length, result.CiphertextLength);
        Assert.Equal(MessageCrypto.SealedLength(size), result.CiphertextLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), result.Sha256);

        var opened = new MemoryStream();
        await MessageCrypto.OpenAsync(new MemoryStream(stored), opened, result.Encryption, _ring, TestContext.Current.CancellationToken);
        Assert.Equal(plaintext, opened.ToArray());
    }

    public static TheoryData<string> Damage =>
        ["cut-at-chunk-boundary", "cut-mid-chunk", "last-chunk-dropped", "flipped-byte", "chunks-swapped", "bytes-appended", "header-changed"];

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task A_damaged_stream_fails_to_open(string damage)
    {
        // 2 full chunks + a short final chunk.
        var plaintext = RandomNumberGenerator.GetBytes(2 * Chunk + 100);
        var sealedStream = new MemoryStream();
        var result = await MessageCrypto.SealAsync(new MemoryStream(plaintext), sealedStream, _info, TestContext.Current.CancellationToken);
        var stored = sealedStream.ToArray();

        var damaged = damage switch
        {
            "cut-at-chunk-boundary" => stored[..(Header + 2 * SealedChunk)],
            "cut-mid-chunk" => stored[..(Header + SealedChunk + 500)],
            "last-chunk-dropped" => stored[..(Header + SealedChunk)],
            "flipped-byte" => Flip(stored, Header + SealedChunk + 10),
            "chunks-swapped" => [.. stored[..Header], .. stored[(Header + SealedChunk)..(Header + 2 * SealedChunk)],
                .. stored[Header..(Header + SealedChunk)], .. stored[(Header + 2 * SealedChunk)..]],
            "bytes-appended" => [.. stored, 0, 1, 2],
            "header-changed" => Flip(stored, 7),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            MessageCrypto.OpenAsync(new MemoryStream(damaged), new MemoryStream(), result.Encryption, _ring, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_sealed_body_cant_be_passed_off_as_a_stream_or_the_other_way_round()
    {
        var body = MessageCrypto.Seal(new byte[100], _info);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            MessageCrypto.OpenAsync(new MemoryStream(body.Ciphertext), new MemoryStream(), body.Encryption, _ring, TestContext.Current.CancellationToken));

        var sealedStream = new MemoryStream();
        var result = await MessageCrypto.SealAsync(new MemoryStream(new byte[100]), sealedStream, _info, TestContext.Current.CancellationToken);
        // Strip the header so the bytes have a body's shape: still refused, because the mode is bound in.
        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(sealedStream.ToArray()[Header..], result.Encryption, _ring));
    }

    private static byte[] Flip(byte[] data, int index)
    {
        var copy = data.ToArray();
        copy[index] ^= 0x01;
        return copy;
    }
}

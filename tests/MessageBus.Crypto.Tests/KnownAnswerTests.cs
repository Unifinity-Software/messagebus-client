using System.Buffers.Text;
using System.Security.Cryptography;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>The primitives against published test vectors, so the wire format matches the standards it names.</summary>
public class KnownAnswerTests
{
    [Fact]
    public void Aes_key_wrap_matches_RFC_3394_section_4_6()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var keyData = Convert.FromHexString("00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F");
        var expected = Convert.FromHexString("28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21");

        Assert.Equal(expected, AesKeyWrap.Wrap(kek, keyData));
        Assert.Equal(keyData, AesKeyWrap.Unwrap(kek, expected));

        expected[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => AesKeyWrap.Unwrap(kek, expected));
    }

    [Fact]
    public void Ecdh_es_concat_kdf_matches_RFC_7518_appendix_C()
    {
        using var alice = Key("gI0GAILBdu7T53akrFmMyGcsF3n5dO7MmwNBHKW5SV0", "SLW_xSffzlPWrHEVI30DHM_4egVwt3NQqeUD7nMFpps",
            "0_NxaRPUMQoAJt50Gz8YiTr8gRTwyEaCumd-MToTmIo");
        using var bob = Key("weNJy2HscCSM6AEDTDg04biOvhFhyyWvOHQfeF_PxMQ", "e8lnCO-AlStT-NJVX-crhB7QRYhiix03illJOVAOyck",
            "VEmDZpDXXK8p8N0Cndsxs924q6nS1RXFASRl6BfUqdw");

        var fromAlice = ConcatKdf.DeriveKey(alice, bob.PublicKey, "A128GCM", "Alice"u8, "Bob"u8, 128);
        var fromBob = ConcatKdf.DeriveKey(bob, alice.PublicKey, "A128GCM", "Alice"u8, "Bob"u8, 128);

        Assert.Equal("VqqN6vgjbSBcIijNcacQGg", Base64Url.EncodeToString(fromAlice));
        Assert.Equal(fromAlice, fromBob);
    }

    [Fact]
    public void Aes_256_gcm_content_is_ciphertext_then_tag_per_the_GCM_spec_test_case_14()
    {
        var key = new byte[32];
        var nonce = new byte[12];
        var plaintext = new byte[16];

        var sealedBytes = GcmContent.Encrypt(key, nonce, plaintext, []);

        Assert.Equal("cea7403d4d606b6e074ec5d3baf39d18" + "d0d1c8a799996bf0265b98b5d48ab919", Convert.ToHexStringLower(sealedBytes));
    }

    private static ECDiffieHellman Key(string x, string y, string d) => ECDiffieHellman.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = Base64Url.DecodeFromChars(x), Y = Base64Url.DecodeFromChars(y) },
        D = Base64Url.DecodeFromChars(d),
    });
}

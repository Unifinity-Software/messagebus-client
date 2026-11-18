using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>Bodies sealed to a role key open only with that key, and any change to what is stored is caught.</summary>
public class SealingTests
{
    public static TheoryData<string> KeyTypes => [KeyAlgorithms.P256, KeyAlgorithms.Rsa];

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public void A_body_opens_with_the_recipient_key_and_not_with_another_key_under_the_same_id(string algorithm)
    {
        using var roleKey = New(algorithm);
        using var impostor = New(algorithm);
        var info = TestKeys.Info(roleKey, KeyScope.Role, "developer");
        var body = new MessageBody
        {
            Summary = "Add due dates to the todo API",
            Details = "Use DateOnly; keep the existing tests passing.",
            Data = JsonDocument.Parse("""{"workItem":123}""").RootElement,
        };

        var sealedBytes = BodyCrypto.Seal(body, info);

        Assert.Equal(info.KeyId, sealedBytes.Encryption.KeyId);
        Assert.Equal(algorithm == KeyAlgorithms.P256 ? EncryptionAlgorithms.EcdhEsA256Kw : EncryptionAlgorithms.RsaOaep256,
            sealedBytes.Encryption.KeyWrapAlgorithm);
        Assert.DoesNotContain("due dates", Encoding.UTF8.GetString(sealedBytes.Ciphertext));

        using var holder = TestKeys.Ring(info, roleKey);
        var opened = BodyCrypto.Open(sealedBytes.ToSealedBody(), holder);
        Assert.Equal(body.Summary, opened.Summary);
        Assert.Equal(body.Details, opened.Details);
        Assert.Equal(123, opened.Data!.Value.GetProperty("workItem").GetInt32());

        using var wrongHolder = TestKeys.Ring(info, impostor);
        Assert.ThrowsAny<CryptographicException>(() => BodyCrypto.Open(sealedBytes.ToSealedBody(), wrongHolder));
    }

    [Fact]
    public void A_machine_without_the_key_is_told_which_key_it_needs()
    {
        using var roleKey = KeyPair.GenerateP256();
        var info = TestKeys.Info(roleKey, KeyScope.Role, "developer");
        var sealedBytes = MessageCrypto.Seal("hello"u8, info);

        using var empty = new KeyRing();
        var e = Assert.Throws<MissingKeyException>(() => MessageCrypto.Open(sealedBytes.Ciphertext, sealedBytes.Encryption, empty));
        Assert.Equal(info.KeyId, e.KeyId);
    }

    public static TheoryData<string> Tampers =>
        ["ciphertext", "tag", "truncated", "wrapped-key", "ephemeral-key", "nonce", "key-id", "algorithm-swap"];

    [Theory]
    [MemberData(nameof(Tampers))]
    public void Any_change_to_the_stored_ciphertext_or_its_encryption_details_fails(string tamper)
    {
        using var roleKey = KeyPair.GenerateP256();
        var info = TestKeys.Info(roleKey, KeyScope.Role, "developer");
        var sealedBytes = MessageCrypto.Seal(Encoding.UTF8.GetBytes("ship the due-dates change"), info);
        var ciphertext = sealedBytes.Ciphertext.ToArray();
        var encryption = sealedBytes.Encryption;
        var ring = new KeyRing().Add(info.KeyId, roleKey);

        switch (tamper)
        {
            case "ciphertext": ciphertext[0] ^= 0x01; break;
            case "tag": ciphertext[^1] ^= 0x80; break;
            case "truncated": ciphertext = ciphertext[..^1]; break;
            case "wrapped-key": encryption = encryption with { WrappedKey = FlipByte(encryption.WrappedKey, 3) }; break;
            case "ephemeral-key":
                using (var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                    encryption = encryption with { EphemeralPublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()) };
                break;
            case "nonce": encryption = encryption with { Nonce = FlipByte(encryption.Nonce, 0) }; break;
            case "key-id":
                // Same private key filed under a second id: the content is bound to the id it was sealed for.
                var otherId = "ek_01jcz7m2v1g3x4y5z6a7b8c9zz";
                ring.Add(otherId, KeyPair.FromPkcs8(roleKey.ExportPkcs8()));
                encryption = encryption with { KeyId = otherId };
                break;
            case "algorithm-swap": encryption = encryption with { KeyWrapAlgorithm = EncryptionAlgorithms.RsaOaep256 }; break;
        }

        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(ciphertext, encryption, ring));
    }

    [Fact]
    public void Sealing_the_same_body_twice_gives_unrelated_ciphertexts()
    {
        using var roleKey = KeyPair.GenerateP256();
        var info = TestKeys.Info(roleKey, KeyScope.Role, "developer");

        var a = MessageCrypto.Seal("same body"u8, info);
        var b = MessageCrypto.Seal("same body"u8, info);

        Assert.NotEqual(a.Encryption.Nonce, b.Encryption.Nonce);
        Assert.NotEqual(a.Encryption.EphemeralPublicKey, b.Encryption.EphemeralPublicKey);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void A_key_record_whose_fingerprint_doesnt_match_its_key_is_refused_before_sealing()
    {
        using var roleKey = KeyPair.GenerateP256();
        using var other = KeyPair.GenerateP256();
        var info = TestKeys.Info(roleKey, KeyScope.Role, "developer") with { Fingerprint = other.PublicKey.Fingerprint };

        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Seal("x"u8, info));
    }

    private static KeyPair New(string algorithm) =>
        algorithm == KeyAlgorithms.P256 ? KeyPair.GenerateP256() : KeyPair.GenerateRsa(2048);

    private static string FlipByte(string base64, int index)
    {
        var bytes = Convert.FromBase64String(base64);
        bytes[index] ^= 0x01;
        return Convert.ToBase64String(bytes);
    }
}

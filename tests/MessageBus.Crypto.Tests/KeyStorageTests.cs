using System.Security.Cryptography;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>Keys saved on a machine (PEM) and in a recovery kit come back as the same keys, and only with the right passphrase.</summary>
public class KeyStorageTests
{
    [Theory]
    [InlineData(KeyAlgorithms.P256, false)]
    [InlineData(KeyAlgorithms.P256, true)]
    [InlineData(KeyAlgorithms.Rsa, false)]
    public void A_key_saved_as_PEM_still_opens_what_was_sealed_to_it(string algorithm, bool encrypted)
    {
        using var original = algorithm == KeyAlgorithms.P256 ? KeyPair.GenerateP256() : KeyPair.GenerateRsa(2048);
        var info = TestKeys.Info(original, KeyScope.Machine, "mch_1");
        var sealedBytes = MessageCrypto.Seal("for this machine"u8, info);

        var pem = encrypted ? original.ExportEncryptedPem("correct horse battery", iterations: 1000) : original.ExportPem();
        using var restored = KeyPair.FromPem(pem, encrypted ? "correct horse battery" : null);
        using var ring = new KeyRing().Add(info.KeyId, restored);

        Assert.Equal(original.PublicKey.Fingerprint, restored.PublicKey.Fingerprint);
        Assert.Equal("for this machine"u8.ToArray(), MessageCrypto.Open(sealedBytes.Ciphertext, sealedBytes.Encryption, ring));
        if (encrypted)
        {
            Assert.ThrowsAny<CryptographicException>(() => KeyPair.FromPem(pem, "wrong passphrase"));
            Assert.ThrowsAny<CryptographicException>(() => KeyPair.FromPem(pem));
        }
    }

    [Fact]
    public void A_private_key_is_never_accepted_where_a_public_key_belongs()
    {
        using var key = KeyPair.GenerateP256();

        Assert.ThrowsAny<CryptographicException>(() => PublicKey.FromSpki(key.ExportPkcs8()));
        Assert.ThrowsAny<CryptographicException>(() => PublicKey.FromBase64(Convert.ToBase64String(key.ExportPkcs8())));
    }

    [Fact]
    public void A_recovery_kit_restores_every_key_with_its_passphrase_and_nothing_without_it()
    {
        using var workspaceKey = KeyPair.GenerateP256();
        using var developerKey = KeyPair.GenerateP256();
        var developerInfo = TestKeys.Info(developerKey, KeyScope.Role, "developer", version: 3);
        var message = MessageCrypto.Seal("still readable after recovery"u8, developerInfo);

        var kit = RecoveryKit.Create("w101",
        [
            new RecoveryKitEntry("ek_ws", KeyScope.Workspace, null, 1, workspaceKey),
            new RecoveryKitEntry(developerInfo.KeyId, KeyScope.Role, "developer", 3, developerKey),
        ], "a long recovery passphrase", TestKeys.Now, iterations: 1000);

        Assert.DoesNotContain(developerKey.ExportPem().Split('\n')[1], kit);
        Assert.ThrowsAny<CryptographicException>(() => RecoveryKit.Open(kit, "a wrong recovery passphrase"));
        Assert.ThrowsAny<CryptographicException>(() => RecoveryKit.Open(kit.Replace("Workspace: w101", "Workspace: w102"), "a long recovery passphrase"));

        var restored = RecoveryKit.Open(kit, "a long recovery passphrase");
        using var ring = new KeyRing();
        foreach (var entry in restored) ring.Add(entry.KeyId, entry.PrivateKey);

        var developer = Assert.Single(restored, e => e.Owner == "developer");
        Assert.Equal((KeyScope.Role, 3), (developer.Scope, developer.Version));
        Assert.Equal(workspaceKey.PublicKey.Fingerprint, Assert.Single(restored, e => e.Scope == KeyScope.Workspace).PrivateKey.PublicKey.Fingerprint);
        Assert.Equal("still readable after recovery"u8.ToArray(), MessageCrypto.Open(message.Ciphertext, message.Encryption, ring));
    }
}

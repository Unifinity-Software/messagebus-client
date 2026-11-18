using MessageBus.Contracts;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto.Tests;

/// <summary>Key records shaped like the ones the service lists.</summary>
internal static class TestKeys
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public static PublicKeyInfo Info(KeyPair key, KeyScope scope, string? owner, int version = 1) => new()
    {
        KeyId = ContractIds.New(ContractIds.EncryptionKey, Now),
        Scope = scope,
        Owner = owner,
        Version = version,
        Algorithm = key.Algorithm,
        PublicKey = key.PublicKey.ToBase64(),
        Fingerprint = key.PublicKey.Fingerprint,
        CreatedAt = Now,
    };

    public static KeyRing Ring(PublicKeyInfo info, KeyPair key) => new KeyRing().Add(info.KeyId, key);
}

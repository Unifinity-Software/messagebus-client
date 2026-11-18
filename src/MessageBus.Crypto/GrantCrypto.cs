using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>
/// Key grants: a role's (or the workspace's) private key sealed to one machine's device key and signed
/// by the granting admin's device key. The admin's machine creates them; the service checks the
/// signature and stores them; the receiving machine checks the signature again and opens them.
/// </summary>
/// <remarks>
/// <see cref="KeyGrant.SealedPrivateKey"/> is two base64url parts joined by a dot: the
/// <see cref="EncryptionInfo"/> JSON (whose <c>keyId</c> is the machine key it was sealed to) and the
/// sealed PKCS#8 private key. The signature (ES256) covers <see cref="CanonicalString"/>.
/// </remarks>
public static class GrantCrypto
{
    /// <summary>Creates a signed grant of <paramref name="grantedKey"/> to the machine that owns <paramref name="machineKey"/>.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="grantedKey">The role or workspace key version being granted.</param>
    /// <param name="grantedPrivateKey">Its private key (held by the granting admin's machine).</param>
    /// <param name="machineKey">The receiving machine's device key (scope machine).</param>
    /// <param name="signerKey">The granting admin's machine key record (its id becomes <see cref="KeyGrant.SigningKeyId"/>).</param>
    /// <param name="signer">The granting admin's device private key.</param>
    /// <param name="grantedBy">The admin's subject id.</param>
    /// <param name="now">The time, for the grant id and <see cref="KeyGrant.CreatedAt"/> (the service sets its own).</param>
    public static KeyGrant Create(string workspaceId, PublicKeyInfo grantedKey, KeyPair grantedPrivateKey,
        PublicKeyInfo machineKey, PublicKeyInfo signerKey, KeyPair signer, string grantedBy, DateTimeOffset now)
    {
        if (machineKey.Scope != KeyScope.Machine || machineKey.Owner is null)
            throw new ArgumentException("Grants are sealed to a machine key.", nameof(machineKey));
        if (grantedPrivateKey.PublicKey.Fingerprint != grantedKey.Fingerprint)
            throw new ArgumentException("The private key doesn't belong to the key being granted.", nameof(grantedPrivateKey));
        if (signerKey.Scope != KeyScope.Machine || signer.PublicKey.Fingerprint != signerKey.Fingerprint)
            throw new ArgumentException("The signer must be the admin's own machine key.", nameof(signer));

        var pkcs8 = grantedPrivateKey.ExportPkcs8();
        SealedBytes sealedKey;
        try { sealedKey = MessageCrypto.Seal(pkcs8, machineKey); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }

        var sealedText = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(MessageBusJson.Serialize(sealedKey.Encryption)))
                         + "." + Base64Url.EncodeToString(sealedKey.Ciphertext);
        var unsigned = new KeyGrant
        {
            GrantId = ContractIds.New(ContractIds.KeyGrant, now),
            Role = OwnerOf(grantedKey),
            KeyVersion = grantedKey.Version,
            MachineId = machineKey.Owner,
            SealedPrivateKey = sealedText,
            GrantedBy = grantedBy,
            SigningKeyId = signerKey.KeyId,
            Signature = "",
            CreatedAt = now,
        };
        return unsigned with { Signature = signer.Sign(CanonicalString(workspaceId, unsigned, grantedKey.Fingerprint)) };
    }

    /// <summary>
    /// True when <paramref name="grant"/> is for <paramref name="grantedKey"/> and was signed by
    /// <paramref name="signerKey"/>. The service runs this before storing a grant; machines run it
    /// (inside <see cref="Open"/>) before using one.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="grantedKey">The role or workspace key version it claims to grant.</param>
    /// <param name="signerKey">The machine key named by <see cref="KeyGrant.SigningKeyId"/>, from a source the verifier trusts.</param>
    public static bool Verify(KeyGrant grant, string workspaceId, PublicKeyInfo grantedKey, PublicKeyInfo signerKey)
    {
        if (grant.Role != OwnerOf(grantedKey) || grant.KeyVersion != grantedKey.Version) return false;
        if (signerKey.Scope != KeyScope.Machine || grant.SigningKeyId != signerKey.KeyId) return false;
        PublicKey signer;
        try { signer = PublicKey.FromInfo(signerKey); }
        catch (CryptographicException) { return false; }
        return signer.Verify(CanonicalString(workspaceId, grant, grantedKey.Fingerprint), grant.Signature);
    }

    /// <summary>The id of the machine key a grant's private key was sealed to.</summary>
    /// <param name="grant">The grant.</param>
    /// <exception cref="CryptographicException">The sealed key isn't in the expected form.</exception>
    public static string SealedToKeyId(KeyGrant grant) => ParseSealed(grant.SealedPrivateKey).Encryption.KeyId;

    /// <summary>
    /// Checks a grant (<see cref="Verify"/>) and opens it with this machine's device key, returning
    /// the role or workspace private key. Add it to the machine's <see cref="KeyRing"/> under
    /// <paramref name="grantedKey"/>'s id.
    /// </summary>
    /// <param name="grant">The grant (from <c>GET /api/w/{workspaceId}/keys/grants</c>).</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="grantedKey">The role or workspace key version (<see cref="KeyDirectory.For"/>).</param>
    /// <param name="signerKey">The signer's machine key, which the caller trusts to belong to a workspace admin.</param>
    /// <param name="machineKeys">This machine's keys (its device key at least).</param>
    /// <exception cref="CryptographicException">The signature, the sealed key or the key itself doesn't check out.</exception>
    public static KeyPair Open(KeyGrant grant, string workspaceId, PublicKeyInfo grantedKey, PublicKeyInfo signerKey, IKeyRing machineKeys)
    {
        if (!Verify(grant, workspaceId, grantedKey, signerKey))
            throw new CryptographicException($"Key grant {grant.GrantId} has a bad signature or doesn't match key {grantedKey.KeyId}.");

        var (encryption, ciphertext) = ParseSealed(grant.SealedPrivateKey);
        var pkcs8 = MessageCrypto.Open(ciphertext, encryption, machineKeys);
        try
        {
            var key = KeyPair.FromPkcs8(pkcs8);
            if (key.PublicKey.Fingerprint == grantedKey.Fingerprint) return key;
            key.Dispose();
            throw new CryptographicException($"Key grant {grant.GrantId} holds a different key than {grantedKey.KeyId}.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// The exact text a grant's signature covers, one field per line: a version tag, the workspace id,
    /// the role (<c>-</c> for the workspace key), the key version, the granted key's fingerprint, the
    /// machine id, the SHA-256 of <see cref="KeyGrant.SealedPrivateKey"/>, the granting subject and the
    /// signing key id. The grant id and creation time are the service's and aren't signed.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="grant">The grant.</param>
    /// <param name="grantedKeyFingerprint">The granted key's fingerprint.</param>
    public static string CanonicalString(string workspaceId, KeyGrant grant, string grantedKeyFingerprint) =>
        string.Join('\n',
            "mbus-key-grant-v1",
            workspaceId,
            grant.Role,
            grant.KeyVersion.ToString(CultureInfo.InvariantCulture),
            grantedKeyFingerprint,
            grant.MachineId,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant.SealedPrivateKey))),
            grant.GrantedBy,
            grant.SigningKeyId);

    /// <summary>The grant's role value for a key: the role name, or <see cref="KeyDirectory.WorkspaceOwner"/> for the workspace key.</summary>
    /// <param name="key">A role or workspace key.</param>
    public static string OwnerOf(PublicKeyInfo key) => key.Scope switch
    {
        KeyScope.Workspace => KeyDirectory.WorkspaceOwner,
        KeyScope.Role when key.Owner is not null => key.Owner,
        _ => throw new ArgumentException("Only role and workspace keys are granted.", nameof(key)),
    };

    private static (EncryptionInfo Encryption, byte[] Ciphertext) ParseSealed(string sealedText)
    {
        var dot = sealedText.IndexOf('.');
        if (dot <= 0 || dot == sealedText.Length - 1)
            throw new CryptographicException("The sealed private key isn't in the expected form.");
        try
        {
            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(sealedText.AsSpan(0, dot)));
            var encryption = MessageBusJson.Deserialize<EncryptionInfo>(json)
                             ?? throw new CryptographicException("The sealed private key has no encryption details.");
            return (encryption, Base64Url.DecodeFromChars(sealedText.AsSpan(dot + 1)));
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            throw new CryptographicException("The sealed private key isn't in the expected form.", e);
        }
    }
}

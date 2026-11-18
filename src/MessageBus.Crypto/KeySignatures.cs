using System.Security.Cryptography;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>
/// An admin's computer signs each role or workspace key it makes (the original issue), so another computer can tell a
/// rotation by an admin it already trusts from a key the service put there itself. The signature (ES256,
/// by the admin's device key) covers <see cref="CanonicalString"/>: the workspace, the scope, the owner and
/// the key's fingerprint. The version isn't signed (the service numbers versions); computers refuse a
/// fingerprint they have already seen for an older version instead.
/// </summary>
public static class KeySignatures
{
    /// <summary>The text a key's signature covers, one field per line.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="scope">Workspace or role.</param>
    /// <param name="owner">The role name; ignored for the workspace key.</param>
    /// <param name="fingerprint">The key's fingerprint.</param>
    public static string CanonicalString(string workspaceId, KeyScope scope, string? owner, string fingerprint) =>
        string.Join('\n',
            "mbus-key-v1",
            workspaceId,
            scope == KeyScope.Workspace ? "workspace" : "role",
            scope == KeyScope.Workspace ? KeyDirectory.WorkspaceOwner : owner ?? throw new ArgumentException("A role key needs its role.", nameof(owner)),
            fingerprint);

    /// <summary>Signs a new key with the admin's device key.</summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="scope">Workspace or role.</param>
    /// <param name="owner">The role name; null for the workspace key.</param>
    /// <param name="key">The new key's public half.</param>
    /// <param name="signer">The admin's device private key.</param>
    public static string Sign(string workspaceId, KeyScope scope, string? owner, PublicKey key, KeyPair signer) =>
        signer.Sign(CanonicalString(workspaceId, scope, owner, key.Fingerprint));

    /// <summary>True when <paramref name="key"/> carries a valid signature by <paramref name="signerKey"/>.</summary>
    /// <param name="key">A role or workspace key as the service lists it.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="signerKey">The machine key named by <see cref="PublicKeyInfo.SigningKeyId"/>.</param>
    public static bool Verify(PublicKeyInfo key, string workspaceId, PublicKeyInfo signerKey) =>
        Verify(workspaceId, key.Scope, key.Owner, key.Fingerprint, key.SigningKeyId, key.Signature, signerKey);

    /// <summary>True when <paramref name="signature"/> by <paramref name="signerKey"/> covers this key.</summary>
    public static bool Verify(string workspaceId, KeyScope scope, string? owner, string fingerprint, string? signingKeyId, string? signature,
        PublicKeyInfo signerKey)
    {
        if (scope == KeyScope.Machine || signature is null || signingKeyId != signerKey.KeyId || signerKey.Scope != KeyScope.Machine) return false;
        if (scope == KeyScope.Role && owner is null) return false;
        try
        {
            return PublicKey.FromInfo(signerKey).Verify(CanonicalString(workspaceId, scope, owner, fingerprint), signature);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

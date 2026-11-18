using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>
/// The public keys the service lists (<c>GET /api/w/{workspaceId}/keys</c>), with lookups by id and
/// by owner. Senders use <see cref="RecipientFor"/> to choose the key to seal to.
/// </summary>
/// <param name="keys">Every version the service returned, retired ones included.</param>
public sealed class KeyDirectory(IEnumerable<PublicKeyInfo> keys)
{
    /// <summary>The owner value used for the workspace key in grants and storage keys.</summary>
    public const string WorkspaceOwner = "-";

    private readonly IReadOnlyList<PublicKeyInfo> _keys = [.. keys];

    /// <summary>Every key version.</summary>
    public IReadOnlyList<PublicKeyInfo> All => _keys;

    /// <summary>The key with id <paramref name="keyId"/>, if listed.</summary>
    /// <param name="keyId">A key id (<c>ek_…</c>).</param>
    public PublicKeyInfo? Find(string keyId) => _keys.FirstOrDefault(k => k.KeyId == keyId);

    /// <summary>The current (highest, not retired) version of a key.</summary>
    /// <param name="scope">Workspace, role or machine.</param>
    /// <param name="owner">The role name or machine id; ignored for the workspace key.</param>
    public PublicKeyInfo? Current(KeyScope scope, string? owner = null) =>
        Versions(scope, owner).FirstOrDefault(k => k.RetiredAt is null);

    /// <summary>A specific version of a key.</summary>
    /// <param name="scope">Workspace, role or machine.</param>
    /// <param name="owner">The role name or machine id; ignored for the workspace key.</param>
    /// <param name="version">The version.</param>
    public PublicKeyInfo? Version(KeyScope scope, string? owner, int version) =>
        Versions(scope, owner).FirstOrDefault(k => k.Version == version);

    /// <summary>The key a grant for <paramref name="grant"/>'s role (or the workspace, <see cref="WorkspaceOwner"/>) and version refers to.</summary>
    /// <param name="grant">The grant.</param>
    public PublicKeyInfo? For(KeyGrant grant) =>
        grant.Role == WorkspaceOwner
            ? Version(KeyScope.Workspace, null, grant.KeyVersion)
            : Version(KeyScope.Role, grant.Role, grant.KeyVersion);

    /// <summary>
    /// The key to seal a message to: the role's current key for a role target, otherwise the workspace
    /// key (agent, capability and inbox targets can be served by any machine in the workspace).
    /// </summary>
    /// <param name="target">The message target.</param>
    /// <exception cref="InvalidOperationException">The workspace (or role) has no key yet.</exception>
    public PublicKeyInfo RecipientFor(MessageTarget target) => target switch
    {
        RoleTarget role => Current(KeyScope.Role, role.Role)
            ?? throw new InvalidOperationException($"Role '{role.Role}' has no encryption key yet. A workspace admin creates it with mbus."),
        _ => Current(KeyScope.Workspace)
            ?? throw new InvalidOperationException("The workspace has no encryption key yet. A workspace admin creates it with mbus."),
    };

    private IEnumerable<PublicKeyInfo> Versions(KeyScope scope, string? owner) =>
        _keys.Where(k => k.Scope == scope && (scope == KeyScope.Workspace || k.Owner == owner))
            .OrderByDescending(k => k.Version);
}

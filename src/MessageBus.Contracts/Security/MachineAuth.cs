using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using MessageBus.Contracts.Machines;

namespace MessageBus.Contracts.Security;

/// <summary>
/// <c>Authorization</c> header schemes for callers that aren't a browser (the original issue). The same handler
/// serves the REST API, the <c>/mcp</c> endpoint and the runner connection.
/// </summary>
public static class AuthSchemes
{
    /// <summary>
    /// An enrolled machine proving possession of its device key:
    /// <c>Authorization: MbusMachine &lt;machineId&gt;</c> plus the <see cref="Http.MbusHeaders.Timestamp"/>,
    /// <see cref="Http.MbusHeaders.Nonce"/> and <see cref="Http.MbusHeaders.Signature"/> headers
    /// (see <see cref="MachineRequestSigning"/>).
    /// </summary>
    public const string Machine = "MbusMachine";

    /// <summary>An API key: <c>Authorization: MbusKey &lt;key&gt;</c>.</summary>
    public const string ApiKey = "MbusKey";

    /// <summary>A one-time enrollment token, only on the enroll request: <c>Authorization: MbusEnroll &lt;token&gt;</c>.</summary>
    public const string EnrollmentToken = "MbusEnroll";

    /// <summary>
    /// The person's command-line sign-in, issued by the service after they approved it in the
    /// dashboard (<c>mbus login</c>, the original issue): <c>Authorization: MbusCli &lt;access token&gt;</c>. Accepted only
    /// for enrolling a machine and listing the workspaces it can enroll in.
    /// </summary>
    public const string Cli = "MbusCli";

    /// <summary>
    /// A cloud agent calling back about one task it was sent (the original issue): <c>Authorization: MbusDelivery &lt;token&gt;</c>,
    /// with the token that came in that task's POST. Accepted only by the heartbeat, progress and settle routes,
    /// and only for that delivery while it is still leased to the agent.
    /// </summary>
    public const string Delivery = "MbusDelivery";
}

/// <summary>
/// How an enrolled machine signs a request with its device key. The private key never leaves the
/// machine (OS keychain); the service holds the public key from enrollment. The signature is
/// ECDSA P-256 with SHA-256 (<see cref="Algorithm"/>) over <see cref="CanonicalString"/>, in IEEE P1363
/// form, base64url without padding.
/// </summary>
public static class MachineRequestSigning
{
    /// <summary>JOSE name of the signature algorithm.</summary>
    public const string Algorithm = "ES256";

    /// <summary>Requests whose timestamp is further than this from the service's clock are refused.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The exact string that is signed: method (uppercase), path and query as sent, the timestamp
    /// header value, the nonce header value and the lowercase hex SHA-256 of the body (of the empty
    /// string when there is none), joined by <c>\n</c>.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="pathAndQuery">Path and query string exactly as on the request line, for example <c>/api/runner/settle</c>.</param>
    /// <param name="timestamp">The <see cref="Http.MbusHeaders.Timestamp"/> value (Unix seconds).</param>
    /// <param name="nonce">The <see cref="Http.MbusHeaders.Nonce"/> value (random, single use within <see cref="MaxClockSkew"/>).</param>
    /// <param name="bodySha256Hex">Lowercase hex SHA-256 of the request body.</param>
    public static string CanonicalString(string method, string pathAndQuery, long timestamp, string nonce, string bodySha256Hex) =>
        string.Join('\n',
            method.ToUpperInvariant(),
            pathAndQuery,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            bodySha256Hex.ToLowerInvariant());
}

/// <summary>
/// Enroll this machine (<see cref="Http.ApiRoutes.MachineEnroll"/>). Authorized either with the
/// person's CLI session (<see cref="AuthSchemes.Cli"/>, a personal machine) or a one-time
/// enrollment token (<see cref="AuthSchemes.EnrollmentToken"/>, a shared runner).
/// </summary>
public sealed record MachineEnrollRequest
{
    /// <summary>The workspace to enroll in; for a person in more than one, chosen in <c>mbus login</c>.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The device public key (P-256), SubjectPublicKeyInfo DER, base64.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Display name (defaults to the host name).</summary>
    public required string Name { get; init; }

    /// <summary>Location label from <c>--location</c>.</summary>
    public string? Location { get; init; }

    /// <summary>The first inventory scan.</summary>
    public required MachineInventory Inventory { get; init; }

    /// <summary>Whether Entra reports the device as managed and compliant, when known. The client's own word: the service doesn't rely on it.</summary>
    public bool? DeviceCompliant { get; init; }

    /// <summary>
    /// The machine's Entra device id (<c>DeviceId</c> in <c>dsregcmd /status</c>), when it is joined or registered.
    /// The service looks it up in Microsoft Graph: a managed, compliant device registered to the person skips
    /// the unmanaged-runner approval.
    /// </summary>
    public string? EntraDeviceId { get; init; }

    /// <summary>
    /// Proof that this machine holds the private key of the Entra device certificate for <see cref="EntraDeviceId"/>
    /// (the original issue). Without it, anyone could name their own compliant device's id from another machine.
    /// </summary>
    public DeviceProof? DeviceProof { get; init; }
}

/// <summary>
/// A signature made with the key of the device certificate Entra issued when the device joined or registered
/// (issuer <c>MS-Organization-Access</c>, subject <c>CN=&lt;device id&gt;</c>; the key is usually in the TPM and
/// can't be exported). The service checks the signature and that Entra lists this certificate for the device.
/// </summary>
public sealed record DeviceProof
{
    /// <summary>The device certificate, DER, base64.</summary>
    public required string Certificate { get; init; }

    /// <summary>When it was signed, Unix seconds. The service accepts it for a few minutes.</summary>
    public required long SignedAt { get; init; }

    /// <summary>The signature over <see cref="DeviceProofs.Text"/>, base64: RSA PKCS#1 v1.5 or ECDSA (IEEE P1363), SHA-256.</summary>
    public required string Signature { get; init; }
}

/// <summary>What a <see cref="DeviceProof"/> signs, so a proof made for one enrollment can't be reused for another.</summary>
public static class DeviceProofs
{
    /// <summary>
    /// <c>mbus-device-proof/v1|{device id}|{machine public key}|{signed at}</c>: the proof names the new machine key,
    /// so a copied proof is no use to anyone who doesn't also hold that machine's private key.
    /// </summary>
    public static string Text(string entraDeviceId, string machinePublicKey, long signedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"mbus-device-proof/v1|{entraDeviceId.Trim().ToLowerInvariant()}|{machinePublicKey}|{signedAt}");

    /// <summary>
    /// Signs the proof with the device certificate's private key (RSA PKCS#1 v1.5 or ECDSA, SHA-256). Throws
    /// <see cref="CryptographicException"/> when this process can't use the key (an Entra-joined computer's key
    /// can belong to the computer rather than the person).
    /// </summary>
    public static DeviceProof Create(X509Certificate2 certificate, string entraDeviceId, string machinePublicKey, DateTimeOffset now)
    {
        var signedAt = now.ToUnixTimeSeconds();
        var data = Encoding.UTF8.GetBytes(Text(entraDeviceId, machinePublicKey, signedAt));
        byte[] signature;
        using (var rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is not null)
                signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            else
            {
                using var ecdsa = certificate.GetECDsaPrivateKey() ?? throw new CryptographicException("The device certificate has no usable private key.");
                signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);
            }
        }
        return new DeviceProof { Certificate = Convert.ToBase64String(certificate.RawData), SignedAt = signedAt, Signature = Convert.ToBase64String(signature) };
    }

    /// <summary>Whether <paramref name="proof"/>'s signature is good for the text it should sign, with its certificate's public key.</summary>
    public static bool SignatureMatches(DeviceProof proof, X509Certificate2 certificate, string entraDeviceId, string machinePublicKey)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(Text(entraDeviceId, machinePublicKey, proof.SignedAt));
            var signature = Convert.FromBase64String(proof.Signature);
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null) return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ecdsa = certificate.GetECDsaPublicKey();
            return ecdsa is not null && ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>The answer to <see cref="MachineEnrollRequest"/>.</summary>
public sealed record MachineEnrollResponse
{
    /// <summary>The new (or re-enrolled) machine.</summary>
    public required MachineInfo Machine { get; init; }

    /// <summary>The workspace's display name, for the CLI to confirm.</summary>
    public required string WorkspaceName { get; init; }

    /// <summary>The oldest <c>mbus</c> version the service accepts.</summary>
    public required string MinCliVersion { get; init; }
}

/// <summary>A workspace the signed-in person can enroll a machine in (<see cref="Http.ApiRoutes.MachineWorkspaces"/>).</summary>
public sealed record EnrollableWorkspace
{
    /// <summary>Workspace id, for <see cref="MachineEnrollRequest.WorkspaceId"/>.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Its display name.</summary>
    public required string Name { get; init; }

    /// <summary>The person's role in it (<c>member</c>, <c>admin</c>, <c>owner</c>).</summary>
    public required string Role { get; init; }
}

/// <summary>What an API key may do. A key holds one or more.</summary>
public static class ApiKeyScopes
{
    /// <summary>Read threads, projects, roles and the directory.</summary>
    public const string Read = "read";
    /// <summary>Send messages and use the MCP tools that send.</summary>
    public const string Send = "send";
    /// <summary>Take messages from inboxes, report progress and settle (cloud agents).</summary>
    public const string Receive = "receive";
    /// <summary>Create and complete projects.</summary>
    public const string Projects = "projects";
    /// <summary>
    /// Sign in runners (the original issue): a reusable enrollment credential for runner containers. Each container makes its own
    /// machine key and enrolls with the key as a shared runner (<c>POST /api/machines/enroll</c>), then signs as that
    /// machine. The key can do nothing else, so it is always the key's only scope; only someone who may enroll runners
    /// can create one.
    /// </summary>
    public const string Runner = "runner";

    /// <summary>Every scope.</summary>
    public static IReadOnlyList<string> All { get; } = [Read, Send, Receive, Projects, Runner];
}

/// <summary>Algorithm names (JOSE style) used in <see cref="Messaging.EncryptionInfo"/> and key records.</summary>
public static class EncryptionAlgorithms
{
    /// <summary>AES-256-GCM content encryption.</summary>
    public const string Aes256Gcm = "A256GCM";
    /// <summary>ECDH-ES on P-256 with AES-256 key wrap.</summary>
    public const string EcdhEsA256Kw = "ECDH-ES+A256KW";
    /// <summary>RSA-OAEP with SHA-256.</summary>
    public const string RsaOaep256 = "RSA-OAEP-256";
}

/// <summary>Whose key pair it is.</summary>
public enum KeyScope
{
    /// <summary>The workspace key pair.</summary>
    [JsonStringEnumMemberName("workspace")] Workspace,
    /// <summary>A role's key pair: message keys are wrapped to it.</summary>
    [JsonStringEnumMemberName("role")] Role,
    /// <summary>A machine's device key pair.</summary>
    [JsonStringEnumMemberName("machine")] Machine,
}

/// <summary>A public key the service holds (it never holds private keys).</summary>
public sealed record PublicKeyInfo
{
    /// <summary>Key id (<c>ek_…</c>), unique per version.</summary>
    public required string KeyId { get; init; }

    /// <summary>Whose key.</summary>
    public required KeyScope Scope { get; init; }

    /// <summary>The role name, for role keys; the machine id, for machine keys.</summary>
    public string? Owner { get; init; }

    /// <summary>Version, 1 for the first; rotation adds one.</summary>
    public required int Version { get; init; }

    /// <summary>Key algorithm, for example <c>P-256</c>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>SubjectPublicKeyInfo DER, base64.</summary>
    public required string PublicKey { get; init; }

    /// <summary>SHA-256 of the DER, lowercase hex.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was replaced by a newer version (still decrypts until <see cref="ExpiresAt"/>).</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>When it can no longer be used at all.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// For a role or workspace key: the machine key (<c>ek_…</c>) of the admin's computer that made it and signed it, so
    /// other computers can tell a rotation by an admin they trust from a key the service swapped (the original issue).
    /// </summary>
    public string? SigningKeyId { get; init; }

    /// <summary>ES256 signature by <see cref="SigningKeyId"/> over the workspace, scope, owner and fingerprint, base64url.</summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Publish a public key (POST <see cref="Http.ApiRoutes.PublicKeys"/>, the original issue): a machine's device key, or
/// the workspace or a role key. Publishing for an owner that already has a key adds the next version
/// and retires the previous one (rotation); retired versions still decrypt until they expire.
/// </summary>
public sealed record PublishKeyRequest
{
    /// <summary>Whose key.</summary>
    public required KeyScope Scope { get; init; }

    /// <summary>The role name, for role keys; the machine id, for machine keys; null for the workspace key.</summary>
    public string? Owner { get; init; }

    /// <summary>SubjectPublicKeyInfo DER, base64. Never a private key.</summary>
    public required string PublicKey { get; init; }

    /// <summary>For a role or workspace key: the publishing admin's own machine key (<c>ek_…</c>) that signed it (the original issue).</summary>
    public string? SigningKeyId { get; init; }

    /// <summary>The signature (see <see cref="PublicKeyInfo.Signature"/>).</summary>
    public string? Signature { get; init; }
}

/// <summary>Why a computer must lose a role or workspace key (the original issue).</summary>
public enum KeyAccessLoss
{
    /// <summary>None of its agents holds the role any more (and its owner doesn't manage keys).</summary>
    [JsonStringEnumMemberName("left-role")] LeftRole,
    /// <summary>The computer was removed from the workspace.</summary>
    [JsonStringEnumMemberName("machine-removed")] MachineRemoved,
}

/// <summary>A computer that was given the current version of a key and must not read anything sealed to the next one.</summary>
public sealed record KeyAccessLossInfo
{
    /// <summary>The machine (<c>mch_…</c>).</summary>
    public required string MachineId { get; init; }

    /// <summary>Its name, when it is still enrolled.</summary>
    public string? Name { get; init; }

    /// <summary>Why it loses access.</summary>
    public required KeyAccessLoss Reason { get; init; }
}

/// <summary>
/// Whether the current version of the workspace key or a role key must be replaced (GET
/// <see cref="Http.ApiRoutes.KeyRotation"/>, the original issue). Private keys exist only on members' computers, so the service only
/// marks the key: an admin's <c>mbus</c> replaces it (on its next send to the role, or <c>mbus keys rotate</c>), revokes
/// the grants of <see cref="MustLoseAccess"/> and grants the new version to <see cref="Holders"/>.
/// </summary>
public sealed record KeyRotationState
{
    /// <summary>Workspace or role.</summary>
    public required KeyScope Scope { get; init; }

    /// <summary>The role name; null for the workspace key.</summary>
    public string? Owner { get; init; }

    /// <summary>The current version; null when the role (or workspace) has no key yet.</summary>
    public PublicKeyInfo? Key { get; init; }

    /// <summary>True when a computer in <see cref="MustLoseAccess"/> was given the current version.</summary>
    public bool RotationNeeded { get; set; }

    /// <summary>Computers given the current version that may no longer read it.</summary>
    public IReadOnlyList<KeyAccessLossInfo> MustLoseAccess { get; set; } = [];

    /// <summary>The computers that should hold the key: those whose agents hold the role (every enrolled computer, for the workspace key).</summary>
    public IReadOnlyList<string> Holders { get; set; } = [];
}

/// <summary>
/// A role's private key sealed to one machine's device key, signed by a role owner's or admin's
/// device key. Runners verify the signature before using it.
/// </summary>
public sealed record KeyGrant
{
    /// <summary>Grant id (<c>grt_…</c>).</summary>
    public required string GrantId { get; init; }

    /// <summary>The role.</summary>
    public required string Role { get; init; }

    /// <summary>The role key version granted.</summary>
    public required int KeyVersion { get; init; }

    /// <summary>The machine it is sealed to.</summary>
    public required string MachineId { get; init; }

    /// <summary>The role's private key, encrypted to the machine's public key, base64.</summary>
    public required string SealedPrivateKey { get; init; }

    /// <summary>Who signed the grant (subject id).</summary>
    public required string GrantedBy { get; init; }

    /// <summary>The signing device key's id.</summary>
    public required string SigningKeyId { get; init; }

    /// <summary>Signature over the grant, base64url.</summary>
    public required string Signature { get; init; }

    /// <summary>When it was granted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was revoked, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}

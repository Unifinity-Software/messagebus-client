using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Security;

/// <summary>
/// The machine's device key: an ECDSA P-256 key pair made on this machine by <c>mbus login</c>. The
/// service gets only the public half at enrollment; the private half stays in
/// <c>~/.mbus/machine-key.pem</c> and signs every request the machine makes
/// (<see cref="MachineRequestSigning"/>, ES256).
/// </summary>
public sealed class MachineKey : IDisposable
{
    private readonly ECDsa _key;

    private MachineKey(ECDsa key) => _key = key;

    /// <summary>A new key pair.</summary>
    public static MachineKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Loads a key saved with <see cref="ExportPrivateKeyPem"/>.</summary>
    public static MachineKey FromPem(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new CliException("This machine's key file can't be read. Run mbus login again.", 1, ex);
        }
        return new MachineKey(key);
    }

    /// <summary>The private key, PKCS#8 PEM.</summary>
    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>The public key as SubjectPublicKeyInfo DER, base64 (what enrollment sends).</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>SHA-256 of the SubjectPublicKeyInfo DER, lowercase hex (what the dashboard shows).</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()));

    /// <summary>ES256 signature of <paramref name="data"/>, IEEE P1363 form, base64url without padding.</summary>
    public string Sign(ReadOnlySpan<byte> data) =>
        Base64Url.EncodeToString(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Makes the headers that authenticate one machine request (<see cref="MachineRequestSigning"/>).
/// The HTTP client from <see cref="Server.ServerConnection"/> adds them to every request; the runner
/// (the original issue) also uses this for its WebSocket connect request, where it sets headers itself.
/// </summary>
public sealed class MachineRequestSigner(MachineKey key, string machineId, TimeProvider time)
{
    /// <summary>The enrolled machine's id.</summary>
    public string MachineId { get; } = machineId;

    /// <summary>
    /// Headers for a request: <c>Authorization: MbusMachine &lt;id&gt;</c>, timestamp, nonce and signature.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="pathAndQuery">Path and query exactly as sent, for example <c>/api/runner/connect</c>.</param>
    /// <param name="body">The request body (empty when there is none).</param>
    public IReadOnlyList<KeyValuePair<string, string>> CreateHeaders(string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var canonical = MachineRequestSigning.CanonicalString(method, pathAndQuery, timestamp, nonce, bodyHash);
        var signature = key.Sign(Encoding.UTF8.GetBytes(canonical));
        return
        [
            new("Authorization", $"{AuthSchemes.Machine} {MachineId}"),
            new(MbusHeaders.Timestamp, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(MbusHeaders.Nonce, nonce),
            new(MbusHeaders.Signature, signature),
        ];
    }
}

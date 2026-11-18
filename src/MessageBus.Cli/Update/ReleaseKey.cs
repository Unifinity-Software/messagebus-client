using System.Buffers.Text;
using System.Reflection;
using System.Security.Cryptography;

namespace MessageBus.Cli.Update;

/// <summary>
/// The public key releases are signed with (ECDSA P-256, SubjectPublicKeyInfo, base64). A release
/// build has it built in: <c>dotnet publish -p:MbusReleasePublicKey=&lt;base64&gt;</c> writes it into
/// the assembly's metadata. A development build has none; then, and only then,
/// <c>MBUS_RELEASE_PUBLIC_KEY</c> supplies one, so a built-in key can never be swapped out by an
/// environment variable.
/// </summary>
public static class ReleaseKey
{
    /// <summary>The assembly metadata key the build writes.</summary>
    public const string MetadataKey = "MbusReleasePublicKey";

    /// <summary>The environment variable development builds read.</summary>
    public const string EnvVar = "MBUS_RELEASE_PUBLIC_KEY";

    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>The key built into this mbus, if any.</summary>
    public static string? BuiltIn { get; } = typeof(ReleaseKey).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == MetadataKey)?.Value is { Length: > 0 } key ? key : null;

    /// <summary>The key to trust: the built-in one, else (development builds) the environment's.</summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    public static string? Resolve(Func<string, string?> getEnvironmentVariable) =>
        BuiltIn ?? (getEnvironmentVariable(EnvVar) is { Length: > 0 } dev ? dev.Trim() : null);

    /// <summary>
    /// Whether <paramref name="signature"/> is the release key's signature of <paramref name="data"/>:
    /// ES256 over SHA-256, the signature as base64url or base64 text, in IEEE P1363 form (what
    /// <c>KeyPair.Sign</c> makes) or DER (what <c>openssl dgst -sha256 -sign</c> makes).
    /// </summary>
    /// <param name="publicKey">SubjectPublicKeyInfo, base64.</param>
    /// <param name="data">The signed bytes.</param>
    /// <param name="signature">The signature text.</param>
    public static bool Verify(string publicKey, ReadOnlySpan<byte> data, string signature)
    {
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKey);
        }
        catch (FormatException)
        {
            throw new CryptographicException("The release public key isn't valid base64.");
        }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        if (!IsP256(key.ExportParameters(false).Curve))
        {
            throw new CryptographicException("The release public key must be an ECDSA P-256 key.");
        }
        if (Decode(signature.Trim()) is not { } bytes)
        {
            return false;
        }
        var format = bytes.Length == 64 ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence;
        try
        {
            return key.VerifyData(data, bytes, HashAlgorithmName.SHA256, format);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // Windows reports a named curve by its friendly name, other platforms by its OID.
    private static bool IsP256(ECCurve curve) =>
        curve.IsNamed && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1");

    private static byte[]? Decode(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }
        if (Base64Url.IsValid(text))
        {
            return Base64Url.DecodeFromChars(text);
        }
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

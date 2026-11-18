using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>Key algorithm names stored in <see cref="PublicKeyInfo.Algorithm"/>.</summary>
public static class KeyAlgorithms
{
    /// <summary>NIST P-256: ECDSA (ES256) signing and ECDH-ES key agreement. Every key messagebus.ai generates.</summary>
    public const string P256 = "P-256";

    /// <summary>RSA, 2048 bits or more: RSA-OAEP-256 key wrapping only (for keys held in an external KMS or HSM).</summary>
    public const string Rsa = "RSA";
}

/// <summary>
/// A private key with its public half: a P-256 key (machine, role and workspace keys) or an RSA key.
/// Import and export are PKCS#8 (DER or PEM, optionally passphrase-encrypted), the same on Windows and
/// macOS. Where the PEM lives and how it is protected on disk is the caller's choice (the <c>mbus</c> CLI).
/// </summary>
/// <remarks>
/// A machine has one P-256 device key. It signs requests (<see cref="MachineRequestSigning"/>) and key
/// grants (ES256), and role keys are sealed to it (ECDH-ES). Role and workspace keys are P-256 too.
/// </remarks>
public sealed class KeyPair : IDisposable
{
    private readonly ECDiffieHellman? _ecdh;
    private readonly ECDsa? _ecdsa;
    private readonly RSA? _rsa;

    private KeyPair(ECDiffieHellman ecdh)
    {
        if (ecdh.KeySize != 256)
        {
            ecdh.Dispose();
            throw new CryptographicException("Only P-256 elliptic-curve keys are supported.");
        }
        var parameters = ecdh.ExportParameters(includePrivateParameters: true);
        try
        {
            _ecdh = ecdh;
            _ecdsa = ECDsa.Create(parameters);
        }
        finally
        {
            if (parameters.D is not null) CryptographicOperations.ZeroMemory(parameters.D);
        }
        PublicKey = PublicKey.FromSpki(ecdh.ExportSubjectPublicKeyInfo());
    }

    private KeyPair(RSA rsa)
    {
        if (rsa.KeySize < 2048)
        {
            rsa.Dispose();
            throw new CryptographicException("RSA keys must be at least 2048 bits.");
        }
        _rsa = rsa;
        PublicKey = PublicKey.FromSpki(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The public half (SubjectPublicKeyInfo, fingerprint, algorithm).</summary>
    public PublicKey PublicKey { get; }

    /// <summary><see cref="KeyAlgorithms.P256"/> or <see cref="KeyAlgorithms.Rsa"/>.</summary>
    public string Algorithm => PublicKey.Algorithm;

    /// <summary>A new random P-256 key: for a machine's device key, a role key or the workspace key.</summary>
    public static KeyPair GenerateP256() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>A new random RSA key (wrapping only; it can't sign grants).</summary>
    /// <param name="bits">Key size, at least 2048.</param>
    public static KeyPair GenerateRsa(int bits = 3072) => new(RSA.Create(bits));

    /// <summary>Imports an unencrypted PKCS#8 private key (DER).</summary>
    /// <param name="pkcs8">The DER bytes.</param>
    public static KeyPair FromPkcs8(ReadOnlySpan<byte> pkcs8) => Import(pkcs8, password: null);

    /// <summary>Imports a PEM <c>PRIVATE KEY</c> block, or an <c>ENCRYPTED PRIVATE KEY</c> block with its passphrase.</summary>
    /// <param name="pem">PEM text containing one private key block.</param>
    /// <param name="passphrase">The passphrase for an encrypted block; null for a plain one.</param>
    public static KeyPair FromPem(ReadOnlySpan<char> pem, string? passphrase = null)
    {
        if (!PemEncoding.TryFind(pem, out var fields))
            throw new CryptographicException("No PEM block found.");
        var label = pem[fields.Label];
        var der = Convert.FromBase64String(pem[fields.Base64Data].ToString());
        try
        {
            if (label.SequenceEqual("PRIVATE KEY")) return Import(der, password: null);
            if (label.SequenceEqual("ENCRYPTED PRIVATE KEY"))
                return Import(der, passphrase ?? throw new CryptographicException("This key is encrypted: a passphrase is needed."));
            throw new CryptographicException($"Expected a PRIVATE KEY or ENCRYPTED PRIVATE KEY block, found {label}.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    /// <summary>The private key as unencrypted PKCS#8 DER. Zero it when done.</summary>
    public byte[] ExportPkcs8() => _ecdh?.ExportPkcs8PrivateKey() ?? _rsa!.ExportPkcs8PrivateKey();

    /// <summary>The private key as a PEM <c>PRIVATE KEY</c> block (unencrypted: protect the file).</summary>
    public string ExportPem() => _ecdh?.ExportPkcs8PrivateKeyPem() ?? _rsa!.ExportPkcs8PrivateKeyPem();

    /// <summary>The private key as a PEM <c>ENCRYPTED PRIVATE KEY</c> block (PBES2, AES-256-CBC, PBKDF2-SHA256).</summary>
    /// <param name="passphrase">The passphrase.</param>
    /// <param name="iterations">PBKDF2 iterations.</param>
    public string ExportEncryptedPem(string passphrase, int iterations = 600_000)
    {
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, iterations);
        return _ecdh?.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe) ?? _rsa!.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe);
    }

    /// <summary>Signs <paramref name="data"/> with ES256 (ECDSA P-256, SHA-256), IEEE P1363 form, base64url without padding.</summary>
    /// <param name="data">The bytes to sign.</param>
    public string Sign(ReadOnlySpan<byte> data)
    {
        if (_ecdsa is null) throw new CryptographicException("Only P-256 keys can sign.");
        return Base64Url.EncodeToString(_ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>Signs UTF-8 text with ES256 (for example <see cref="MachineRequestSigning.CanonicalString"/>).</summary>
    /// <param name="text">The text to sign.</param>
    public string Sign(string text) => Sign(Encoding.UTF8.GetBytes(text));

    internal byte[] UnwrapContentKey(EncryptionInfoView info)
    {
        switch (info.KeyWrapAlgorithm)
        {
            case EncryptionAlgorithms.EcdhEsA256Kw when _ecdh is not null:
            {
                if (info.EphemeralPublicKey is null) throw new CryptographicException("The sender's ephemeral key is missing.");
                using var ephemeral = ECDiffieHellman.Create();
                ephemeral.ImportSubjectPublicKeyInfo(info.EphemeralPublicKey, out _);
                if (ephemeral.KeySize != 256) throw new CryptographicException("The sender's ephemeral key isn't P-256.");
                var kek = ConcatKdf.DeriveKey(_ecdh, ephemeral.PublicKey, EncryptionAlgorithms.EcdhEsA256Kw, [], [], 256);
                try { return AesKeyWrap.Unwrap(kek, info.WrappedKey); }
                finally { CryptographicOperations.ZeroMemory(kek); }
            }
            case EncryptionAlgorithms.RsaOaep256 when _rsa is not null:
                try { return _rsa.Decrypt(info.WrappedKey, RSAEncryptionPadding.OaepSHA256); }
                catch (CryptographicException e)
                {
                    throw new CryptographicException("The content key could not be unwrapped: wrong key, or the message was changed.", e);
                }
            default:
                throw new CryptographicException($"A {Algorithm} key can't unwrap {info.KeyWrapAlgorithm}.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ecdh?.Dispose();
        _ecdsa?.Dispose();
        _rsa?.Dispose();
    }

    private static KeyPair Import(ReadOnlySpan<byte> der, string? password)
    {
        var ecdh = ECDiffieHellman.Create();
        try
        {
            if (password is null) ecdh.ImportPkcs8PrivateKey(der, out _);
            else ecdh.ImportEncryptedPkcs8PrivateKey(password, der, out _);
            return new KeyPair(ecdh);
        }
        catch (CryptographicException)
        {
            ecdh.Dispose(); // not an EC key (or not P-256); try RSA
        }

        var rsa = RSA.Create();
        try
        {
            if (password is null) rsa.ImportPkcs8PrivateKey(der, out _);
            else rsa.ImportEncryptedPkcs8PrivateKey(password, der, out _);
            return new KeyPair(rsa);
        }
        catch (CryptographicException e)
        {
            rsa.Dispose();
            throw new CryptographicException("Not a P-256 or RSA private key, or the passphrase is wrong.", e);
        }
    }
}

/// <summary>A public key: SubjectPublicKeyInfo DER, its fingerprint (SHA-256, lowercase hex) and its algorithm.</summary>
public sealed class PublicKey
{
    private readonly byte[] _spki;

    private PublicKey(byte[] spki, string algorithm)
    {
        _spki = spki;
        Algorithm = algorithm;
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(spki));
    }

    /// <summary><see cref="KeyAlgorithms.P256"/> or <see cref="KeyAlgorithms.Rsa"/>.</summary>
    public string Algorithm { get; }

    /// <summary>SHA-256 of the SubjectPublicKeyInfo DER, lowercase hex (<see cref="PublicKeyInfo.Fingerprint"/>).</summary>
    public string Fingerprint { get; }

    /// <summary>The SubjectPublicKeyInfo DER.</summary>
    public ReadOnlySpan<byte> Spki => _spki;

    /// <summary>The SubjectPublicKeyInfo DER, base64 (<see cref="PublicKeyInfo.PublicKey"/>).</summary>
    public string ToBase64() => Convert.ToBase64String(_spki);

    /// <summary>The key-wrap algorithm used when sealing to this key.</summary>
    public string KeyWrapAlgorithm =>
        Algorithm == KeyAlgorithms.P256 ? EncryptionAlgorithms.EcdhEsA256Kw : EncryptionAlgorithms.RsaOaep256;

    /// <summary>Parses a SubjectPublicKeyInfo. Accepts P-256 and RSA (2048 bits or more); anything else, including a private key, is refused.</summary>
    /// <param name="spki">The DER bytes.</param>
    public static PublicKey FromSpki(ReadOnlySpan<byte> spki)
    {
        try
        {
            using var ec = ECDiffieHellman.Create();
            ec.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read == spki.Length && ec.KeySize == 256) return new PublicKey(spki.ToArray(), KeyAlgorithms.P256);
        }
        catch (CryptographicException)
        {
            // not an EC key; try RSA
        }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read == spki.Length && rsa.KeySize >= 2048) return new PublicKey(spki.ToArray(), KeyAlgorithms.Rsa);
        }
        catch (CryptographicException)
        {
            // fall through
        }
        throw new CryptographicException("Not a P-256 or RSA (2048 bits or more) public key in SubjectPublicKeyInfo form.");
    }

    /// <summary>Parses a base64 SubjectPublicKeyInfo (<see cref="PublicKeyInfo.PublicKey"/>, <see cref="MachineEnrollRequest.PublicKey"/>).</summary>
    /// <param name="spkiBase64">The base64 text.</param>
    public static PublicKey FromBase64(string spkiBase64)
    {
        byte[] der;
        try { der = Convert.FromBase64String(spkiBase64); }
        catch (FormatException e) { throw new CryptographicException("The public key isn't valid base64.", e); }
        return FromSpki(der);
    }

    /// <summary>The key a <see cref="PublicKeyInfo"/> record describes, after checking its fingerprint and algorithm match.</summary>
    /// <param name="info">The record from the service.</param>
    public static PublicKey FromInfo(PublicKeyInfo info)
    {
        var key = FromBase64(info.PublicKey);
        if (!string.Equals(key.Fingerprint, info.Fingerprint, StringComparison.Ordinal))
            throw new CryptographicException($"Key {info.KeyId}: the fingerprint doesn't match the key.");
        if (!string.Equals(key.Algorithm, info.Algorithm, StringComparison.Ordinal))
            throw new CryptographicException($"Key {info.KeyId}: the algorithm doesn't match the key.");
        return key;
    }

    /// <summary>Checks an ES256 signature (IEEE P1363, base64url) made by <see cref="KeyPair.Sign(ReadOnlySpan{byte})"/>.</summary>
    /// <param name="data">The signed bytes.</param>
    /// <param name="signature">The signature, base64url.</param>
    public bool Verify(ReadOnlySpan<byte> data, string signature)
    {
        if (Algorithm != KeyAlgorithms.P256) return false;
        byte[] raw;
        try { raw = Base64Url.DecodeFromChars(signature); }
        catch (FormatException) { return false; }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_spki, out _);
        return ecdsa.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Checks an ES256 signature over UTF-8 text.</summary>
    /// <param name="text">The signed text.</param>
    /// <param name="signature">The signature, base64url.</param>
    public bool Verify(string text, string signature) => Verify(Encoding.UTF8.GetBytes(text), signature);

    internal (byte[] WrappedKey, byte[]? EphemeralPublicKey) WrapContentKey(ReadOnlySpan<byte> contentKey)
    {
        if (Algorithm == KeyAlgorithms.Rsa)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(_spki, out _);
            return (rsa.Encrypt(contentKey.ToArray(), RSAEncryptionPadding.OaepSHA256), null);
        }

        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(_spki, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var kek = ConcatKdf.DeriveKey(ephemeral, recipient.PublicKey, EncryptionAlgorithms.EcdhEsA256Kw, [], [], 256);
        try
        {
            return (AesKeyWrap.Wrap(kek, contentKey), ephemeral.ExportSubjectPublicKeyInfo());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }
}

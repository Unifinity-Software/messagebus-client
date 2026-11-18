using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Contracts.Security;

namespace MessageBus.Crypto;

/// <summary>One key in a recovery kit.</summary>
/// <param name="KeyId">The key id (<c>ek_…</c>).</param>
/// <param name="Scope">Workspace or role.</param>
/// <param name="Owner">The role name; null for the workspace key.</param>
/// <param name="Version">The key version.</param>
/// <param name="PrivateKey">The private key.</param>
public sealed record RecoveryKitEntry(string KeyId, KeyScope Scope, string? Owner, int Version, KeyPair PrivateKey);

/// <summary>
/// A recovery kit: the workspace and role private keys an admin's machine holds, in one text file
/// encrypted with a passphrase (PBKDF2-SHA256, then AES-256-GCM). The service can't recover keys; if
/// every machine holding a role key is lost, this file and its passphrase are the only way back. It is
/// created and opened on the admin's machine; the service only records that one was made.
/// </summary>
public static class RecoveryKit
{
    private const string Label = "MBUS RECOVERY KIT";
    private const byte FormatVersion = 1;
    private const int SaltSize = 16;

    /// <summary>The shortest passphrase accepted.</summary>
    public const int MinPassphraseLength = 12;

    /// <summary>Builds the kit text.</summary>
    /// <param name="workspaceId">The workspace (shown in the file and bound to the contents).</param>
    /// <param name="keys">The keys to include.</param>
    /// <param name="passphrase">The passphrase, at least <see cref="MinPassphraseLength"/> characters.</param>
    /// <param name="now">The creation time shown in the file.</param>
    /// <param name="iterations">PBKDF2 iterations.</param>
    public static string Create(string workspaceId, IEnumerable<RecoveryKitEntry> keys, string passphrase, DateTimeOffset now, int iterations = 600_000)
    {
        if (passphrase.Length < MinPassphraseLength)
            throw new ArgumentException($"Use a passphrase of at least {MinPassphraseLength} characters.", nameof(passphrase));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        var list = keys.ToList();
        var plaintext = Serialize(list);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(GcmContent.NonceSize);
        var header = Header(iterations, salt, nonce);
        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            var sealedBytes = GcmContent.Encrypt(key, nonce, plaintext, AssociatedData(header, workspaceId));
            var payload = new byte[header.Length + sealedBytes.Length];
            header.CopyTo(payload, 0);
            sealedBytes.CopyTo(payload, header.Length);

            var text = new StringBuilder()
                .AppendLine("messagebus.ai recovery kit")
                .AppendLine(CultureInfo.InvariantCulture, $"Workspace: {workspaceId}")
                .AppendLine(CultureInfo.InvariantCulture, $"Created: {now.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}")
                .AppendLine(CultureInfo.InvariantCulture, $"Keys: {string.Join(", ", list.Select(Describe))}")
                .AppendLine("Keep this file and its passphrase in separate safe places. messagebus.ai can't recover either.")
                .AppendLine()
                .Append(PemEncoding.Write(Label, payload))
                .AppendLine();
            return text.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Opens a kit made by <see cref="Create"/>. The caller disposes the returned keys.</summary>
    /// <param name="kit">The kit text.</param>
    /// <param name="passphrase">Its passphrase.</param>
    /// <exception cref="CryptographicException">Wrong passphrase, or the file was changed.</exception>
    public static IReadOnlyList<RecoveryKitEntry> Open(string kit, string passphrase)
    {
        var workspaceLine = kit.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Workspace: ", StringComparison.Ordinal));
        if (workspaceLine is null || !PemEncoding.TryFind(kit, out var fields) || kit.AsSpan()[fields.Label].ToString() != Label)
            throw new CryptographicException("Not a messagebus.ai recovery kit.");
        var workspaceId = workspaceLine["Workspace: ".Length..];
        var payload = Convert.FromBase64String(kit[fields.Base64Data]);

        var headerLength = 1 + 4 + SaltSize + GcmContent.NonceSize;
        if (payload.Length < headerLength + GcmContent.TagSize || payload[0] != FormatVersion)
            throw new CryptographicException("Not a recovery kit this version of mbus can read.");
        var iterations = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1));
        var salt = payload.AsSpan(5, SaltSize);
        var nonce = payload.AsSpan(5 + SaltSize, GcmContent.NonceSize);
        if (iterations < 1) throw new CryptographicException("The recovery kit is damaged.");

        var key = DeriveKey(passphrase, salt.ToArray(), iterations);
        var ciphertext = payload.AsSpan(headerLength);
        var plaintext = new byte[ciphertext.Length - GcmContent.TagSize];
        try
        {
            try
            {
                GcmContent.Decrypt(key, nonce, ciphertext, AssociatedData(payload[..headerLength], workspaceId), plaintext);
            }
            catch (CryptographicException e)
            {
                throw new CryptographicException("Wrong passphrase, or the recovery kit was changed.", e);
            }
            return Deserialize(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string Describe(RecoveryKitEntry e) =>
        e.Scope == KeyScope.Workspace ? $"workspace v{e.Version}" : $"{e.Owner} v{e.Version}";

    private static byte[] Header(int iterations, byte[] salt, byte[] nonce)
    {
        var header = new byte[1 + 4 + SaltSize + GcmContent.NonceSize];
        header[0] = FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), iterations);
        salt.CopyTo(header, 5);
        nonce.CopyTo(header, 5 + SaltSize);
        return header;
    }

    private static byte[] AssociatedData(ReadOnlySpan<byte> header, string workspaceId) =>
        [.. header, .. Encoding.UTF8.GetBytes(workspaceId)];

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, GcmContent.KeySize);

    private static byte[] Serialize(IReadOnlyList<RecoveryKitEntry> keys)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var entry in keys)
            {
                var pkcs8 = entry.PrivateKey.ExportPkcs8();
                json.WriteStartObject();
                json.WriteString("keyId", entry.KeyId);
                json.WriteString("scope", entry.Scope == KeyScope.Workspace ? "workspace" : "role");
                if (entry.Owner is not null) json.WriteString("owner", entry.Owner);
                json.WriteNumber("version", entry.Version);
                json.WriteBase64String("privateKey", pkcs8);
                json.WriteEndObject();
                CryptographicOperations.ZeroMemory(pkcs8);
            }
            json.WriteEndArray();
        }
        return buffer.ToArray();
    }

    private static List<RecoveryKitEntry> Deserialize(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var result = new List<RecoveryKitEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var pkcs8 = item.GetProperty("privateKey").GetBytesFromBase64();
            try
            {
                result.Add(new RecoveryKitEntry(
                    item.GetProperty("keyId").GetString()!,
                    item.GetProperty("scope").GetString() == "workspace" ? KeyScope.Workspace : KeyScope.Role,
                    item.TryGetProperty("owner", out var owner) ? owner.GetString() : null,
                    item.GetProperty("version").GetInt32(),
                    KeyPair.FromPkcs8(pkcs8)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
        return result;
    }
}

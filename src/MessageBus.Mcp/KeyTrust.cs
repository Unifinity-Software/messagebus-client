using System.Text.Json;
using System.Text.Json.Serialization;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;

namespace MessageBus.Mcp;

/// <summary>A role or workspace key version this computer has accepted.</summary>
public sealed record TrustedKey(int Version, string KeyId, string Fingerprint);

/// <summary>An admin computer's device key this computer accepts signatures from (keys and grants).</summary>
public sealed record TrustedSigner(string KeyId, string Fingerprint);

/// <summary>What this computer has accepted in one workspace.</summary>
public sealed class TrustedWorkspace
{
    /// <summary>Accepted versions per key: the role name, or <c>-</c> for the workspace key.</summary>
    public Dictionary<string, List<TrustedKey>> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Accepted admin device keys, by machine id.</summary>
    public Dictionary<string, TrustedSigner> Signers { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>How a key the service shows compares with what this computer accepted before.</summary>
public enum TrustStatus
{
    /// <summary>The version this computer already uses.</summary>
    Trusted,
    /// <summary>Never seen: accepted on first use.</summary>
    New,
    /// <summary>A newer version, signed by an admin computer this computer already trusts: accepted.</summary>
    Rotated,
    /// <summary>Different from what this computer accepted, and nothing vouches for it: refused until a person confirms it.</summary>
    Changed,
}

/// <summary>The outcome of <see cref="KeyTrust.Check"/>; <see cref="Problem"/> says what changed when refused.</summary>
public sealed record TrustCheck(TrustStatus Status, string? Problem = null, TrustedKey? Previous = null);

/// <summary>
/// Trust on first use for role and workspace keys and for the admin computers that sign them (the original issue). The
/// service lists public keys, so a dishonest service could swap one and read what is sealed to it. This
/// computer remembers, per server and workspace, every key version it accepted and each signing admin computer's
/// device key (<c>trusted-keys.v2.json</c> in mbus's settings folder). A version it already accepted must keep
/// its fingerprint; a new version is accepted without asking only when an admin computer it already trusts
/// signed it (a normal rotation); anything else stops sending to (and opening from) that key until a
/// person checks the fingerprint and runs <c>mbus keys trust</c>.
/// </summary>
public static class KeyTrust
{
    /// <summary>The file in mbus's settings folder.</summary>
    public const string FileName = "trusted-keys.v2.json";
    private const string LegacyFileName = "trusted-keys.json";

    /// <summary>The name a person types for a key: the role, or <c>workspace</c>.</summary>
    public static string NameOf(PublicKeyInfo key) => key.Scope == KeyScope.Workspace ? "workspace" : key.Owner ?? "";

    /// <summary>
    /// Compares the key the service shows as current with what this computer accepted. Pure: the caller
    /// records the key (<see cref="Accept"/>) when it goes ahead.
    /// </summary>
    /// <param name="pins">This computer's accepted keys.</param>
    /// <param name="key">The key the service lists.</param>
    /// <param name="directory">The service's public keys (for the signer's key).</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="ownKeyId">This computer's own device key id: its own signature is always trusted.</param>
    public static TrustCheck Check(TrustedWorkspace pins, PublicKeyInfo key, KeyDirectory directory, string workspaceId, string? ownKeyId)
    {
        var what = Describe(key);
        var seen = pins.Keys.GetValueOrDefault(GrantCrypto.OwnerOf(key)) ?? [];
        var latest = seen.MaxBy(k => k.Version);
        if (seen.FirstOrDefault(k => k.Version == key.Version) is { } same)
        {
            return same.Fingerprint == key.Fingerprint && same.KeyId == key.KeyId
                ? new TrustCheck(TrustStatus.Trusted, Previous: same)
                : new TrustCheck(TrustStatus.Changed,
                    $"messagebus.ai now shows a different {what} for version {key.Version}: it was {Short(same.Fingerprint)}, it is now {Short(key.Fingerprint)}.", same);
        }
        if (seen.Any(k => k.Fingerprint == key.Fingerprint))
            return new TrustCheck(TrustStatus.Changed, $"messagebus.ai shows an old {what} again as version {key.Version} ({Short(key.Fingerprint)}).", latest);
        if (latest is not null && key.Version < latest.Version)
            return new TrustCheck(TrustStatus.Changed,
                $"messagebus.ai shows version {key.Version} of the {what}, older than version {latest.Version} this computer already uses.", latest);

        var signer = SignerOf(key, directory, workspaceId);
        var signerTrust = signer is null ? (TrustStatus?)null : CheckSigner(pins, signer, ownKeyId).Status;
        if (signerTrust == TrustStatus.Changed)
            return new TrustCheck(TrustStatus.Changed,
                $"The {what} is signed by admin computer {signer!.Owner}, whose key changed since this computer last saw it.", latest);
        if (latest is null) return new TrustCheck(TrustStatus.New);
        return signerTrust == TrustStatus.Trusted
            ? new TrustCheck(TrustStatus.Rotated, Previous: latest)
            : new TrustCheck(TrustStatus.Changed,
                $"The {what} was replaced (version {latest.Version} → {key.Version}, now {Short(key.Fingerprint)}), but not by an admin computer this computer already trusts.",
                latest);
    }

    /// <summary>Compares an admin computer's device key with the one this computer accepted for that machine.</summary>
    public static TrustCheck CheckSigner(TrustedWorkspace pins, PublicKeyInfo signer, string? ownKeyId)
    {
        if (signer.KeyId == ownKeyId) return new TrustCheck(TrustStatus.Trusted);
        if (signer.Owner is null || !pins.Signers.TryGetValue(signer.Owner, out var pinned)) return new TrustCheck(TrustStatus.New);
        return pinned.Fingerprint == signer.Fingerprint
            ? new TrustCheck(TrustStatus.Trusted)
            : new TrustCheck(TrustStatus.Changed,
                $"Admin computer {signer.Owner} now shows a different key: it was {Short(pinned.Fingerprint)}, it is now {Short(signer.Fingerprint)}.");
    }

    /// <summary>The machine key that validly signed <paramref name="key"/>, or null when it isn't signed (or the signature doesn't check out).</summary>
    public static PublicKeyInfo? SignerOf(PublicKeyInfo key, KeyDirectory directory, string workspaceId) =>
        key.SigningKeyId is { } id && directory.Find(id) is { Scope: KeyScope.Machine } signer && KeySignatures.Verify(key, workspaceId, signer)
            ? signer
            : null;

    /// <summary>Records <paramref name="key"/> (replacing a different key recorded for its version) and its signer.</summary>
    public static void Accept(TrustedWorkspace pins, PublicKeyInfo key, PublicKeyInfo? signer)
    {
        var name = GrantCrypto.OwnerOf(key);
        if (!pins.Keys.TryGetValue(name, out var list)) pins.Keys[name] = list = [];
        list.RemoveAll(k => k.Version == key.Version || k.Fingerprint == key.Fingerprint);
        list.Add(new TrustedKey(key.Version, key.KeyId, key.Fingerprint));
        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        if (signer is not null) AcceptSigner(pins, signer);
    }

    /// <summary>Records an admin computer's device key.</summary>
    public static void AcceptSigner(TrustedWorkspace pins, PublicKeyInfo signer)
    {
        if (signer.Owner is not null) pins.Signers[signer.Owner] = new TrustedSigner(signer.KeyId, signer.Fingerprint);
    }

    /// <summary>"the developer role's key" / "workspace key".</summary>
    public static string Describe(PublicKeyInfo key) => key.Scope == KeyScope.Workspace ? "workspace key" : $"{key.Owner} role's key";

    /// <summary>The first 16 hex digits, in groups of four, as people compare them.</summary>
    public static string Short(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, Math.Min(4, fingerprint.Length / 4)).Select(i => fingerprint.Substring(i * 4, 4)));

    // ---- file ----

    /// <summary>Reads trust for this server and workspace, independently of other servers.</summary>
    public static TrustedWorkspace Load(string configDirectory, string workspaceId, string server) =>
        ReadFile(configDirectory, server).Servers.GetValueOrDefault(NormalizeServer(server))?.GetValueOrDefault(workspaceId) ?? new TrustedWorkspace();

    /// <summary>The server's origin: scheme, host and port, without a trailing slash.</summary>
    public static string NormalizeServer(string server)
    {
        var text = server.Trim();
        if (text.Length == 0) return "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        // Trust belongs to an origin; URL paths deliberately do not distinguish servers.
        return new Uri(text).GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/');
    }

    /// <summary>Binds old workspace-only trust before the saved server changes, even after logout.</summary>
    public static void BindLegacy(string configDirectory, string previousServer) => ReadFile(configDirectory, previousServer, previousServer);

    /// <summary>Saves trust for this server and workspace.</summary>
    public static void Save(string configDirectory, string workspaceId, TrustedWorkspace pins, string server)
    {
        var file = ReadFile(configDirectory, server);
        var origin = NormalizeServer(server);
        if (!file.Servers.TryGetValue(origin, out var workspaces)) file.Servers[origin] = workspaces = new(StringComparer.Ordinal);
        workspaces[workspaceId] = pins;
        WriteFile(configDirectory, file);
    }

    private static void WriteFile(string configDirectory, TrustFile file)
    {
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, TrustJson.Default.TrustFile));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    private static TrustFile ReadFile(string configDirectory, string server, string? legacyServer = null)
    {
        var path = Path.Combine(configDirectory, FileName);
        var importing = !File.Exists(path);
        if (importing) path = Path.Combine(configDirectory, LegacyFileName);
        if (!File.Exists(path)) return new TrustFile();
        TrustFile file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), TrustJson.Default.TrustFile) ?? new TrustFile();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is damaged ({e.Message}). Delete it to start trusting keys afresh.", e);
        }
        if (file.Workspaces is { Count: > 0 } legacy)
        {
            // Diagnose the settings separately, so a damaged config is never blamed on the trust file.
            var origin = NormalizeServer(legacyServer ?? CurrentServer(configDirectory) ?? server);
            if (!file.Servers.TryGetValue(origin, out var workspaces)) file.Servers[origin] = workspaces = new(StringComparer.Ordinal);
            foreach (var (workspace, pins) in legacy) workspaces.TryAdd(workspace, pins);
        }
        file.Workspaces = null;
        // Import only once. Old clients may keep writing their own file, never this one.
        if (importing) WriteFile(configDirectory, file);
        return file;
    }

    private static string? CurrentServer(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "config.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(path));
            return config.RootElement.TryGetProperty("server", out var server) && server.GetString() is { Length: > 0 } url ? NormalizeServer(url) : null;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is damaged ({e.Message}). Repair the saved server before importing trusted keys.", e);
        }
    }
}

/// <summary>The <c>trusted-keys.v2.json</c> file: workspaces grouped by server origin.</summary>
public sealed class TrustFile
{
    public Dictionary<string, Dictionary<string, TrustedWorkspace>> Servers { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TrustedWorkspace>? Workspaces { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TrustFile))]
internal sealed partial class TrustJson : JsonSerializerContext;

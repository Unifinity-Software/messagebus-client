using System.Text.Json;
using MessageBus.Cli.Configuration;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Server;

/// <summary>A successfully parsed anonymous discovery observation, not a reusable authorization context.</summary>
public sealed class ServerDiscovery
{
    internal ServerDiscovery(ServerInfoResponse info, ServerBinding? binding, string[] aliases)
    { Info = info; Binding = binding; Aliases = Array.AsReadOnly(aliases); }
    /// <summary>The descriptive response. Never use this DTO alone for security decisions.</summary>
    public ServerInfoResponse Info { get; }
    /// <summary>The complete validated isolated bundle, or null only when both security descriptors were absent.</summary>
    public ServerBinding? Binding { get; }
    /// <summary>Normalized server-configured alias origins; not local permission to move credentials.</summary>
    public IReadOnlyList<string> Aliases { get; }
}

/// <summary>Strict presence-aware security parsing with generated JSON metadata, independent of HTTP and profile writes.</summary>
public static class ServerDiscoveryParser
{
    /// <summary>Parses a successful anonymous response. A transport error or redirect must never be supplied as legacy evidence.</summary>
    public static ServerDiscovery Parse(string json, bool allowLoopbackHttp = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CheckObject(root, "product", "version", "url", "stamp", "signIn", "capabilities");
            var authorityPresent = false;
            var signaturePresent = false;
            if (root.TryGetProperty("signIn", out var signIn) && signIn.ValueKind != JsonValueKind.Null)
            {
                CheckObject(signIn, "microsoft", "cliApprovalUrl", "authority", "machineSignature");
                authorityPresent = signIn.TryGetProperty("authority", out var authority);
                signaturePresent = signIn.TryGetProperty("machineSignature", out var signature);
                if (authorityPresent) CheckObject(authority, "version", "stampId", "issuer", "audience");
                if (signaturePresent) CheckObject(signature, "version", "required");
            }
            if (root.TryGetProperty("stamp", out var stamp) && stamp.ValueKind != JsonValueKind.Null)
                CheckObject(stamp, "stampId", "geography", "azureRegion", "url", "aliases");

            var info = JsonSerializer.Deserialize(root, CliJsonContext.Default.ServerInfoResponse)
                ?? throw new ServerBindingException("Discovery is empty.");
            if (info.Product != ServerInfoResponse.ProductName) throw new ServerBindingException("Unexpected discovery product.");
            // Stamp is descriptive even on legacy servers. Presence is measured on JSON, never nullable DTO members.
            if (!authorityPresent && !signaturePresent) return new(info, null, []);
            if (!authorityPresent || !signaturePresent || info.Stamp is not { } s
                || info.SignIn?.Authority is not { Version: 1 } a
                || info.SignIn.MachineSignature is not { Version: 2, Required: true }
                || s.StampId is null || a.StampId != s.StampId || a.Issuer is null || a.Audience is null
                || s.Url is null || s.Geography is null || s.AzureRegion is null || s.Aliases is null)
                throw new ServerBindingException("Incomplete or unsupported isolated discovery bundle.");
            var binding = ServerBinding.Create(s.StampId, a.Issuer, a.Audience, allowLoopbackHttp);
            if (ProtocolOrigin.NormalizeIsolated(s.Url, allowLoopbackHttp) != binding.Issuer)
                throw new ServerBindingException("Stamp URL and canonical authority disagree.");
            var aliases = s.Aliases.Select(x => ProtocolOrigin.NormalizeIsolated(x, allowLoopbackHttp)).ToArray();
            if (aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length)
                throw new ServerBindingException("Duplicate isolated alias origins.");
            return new(info, binding, aliases);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException && ex is not ServerBindingException)
        { throw new ServerBindingException("Malformed server discovery; credential use refused.", ex); }
    }

    internal static ServerBindingExpectation ParseExpectation(string json, bool allowLoopbackHttp)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            CheckObject(root, "version", "transportOrigin", "stampId", "issuer", "audience", "authorityVersion",
                "minimumMachineSignatureVersion", "machineSignatureRequired");
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("authorityVersion").GetInt32() != 1
                || root.GetProperty("minimumMachineSignatureVersion").GetInt32() != 2
                || root.GetProperty("machineSignatureRequired").ValueKind != JsonValueKind.True)
                throw new ServerBindingException("Unsupported expected isolated binding.");
            var binding = ServerBinding.Create(RequiredString(root, "stampId"), RequiredString(root, "issuer"),
                RequiredString(root, "audience"), allowLoopbackHttp);
            return ServerBindingExpectation.Create(RequiredString(root, "transportOrigin"), binding, allowLoopbackHttp);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or KeyNotFoundException or FormatException
            || ex is InvalidOperationException && ex is not ServerBindingException)
        { throw new ServerBindingException("Malformed MBUS_SERVER_BINDING; credential use refused.", ex); }
    }

    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString()
        ?? throw new ServerBindingException("Missing expected binding string.");

    private static void CheckObject(JsonElement element, params string[] known)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ServerBindingException("Security descriptor must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            var name = known.FirstOrDefault(x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase));
            if (name is not null && (name != property.Name || !seen.Add(name)))
                throw new ServerBindingException("Duplicate or ambiguously cased discovery security field.");
        }
    }
}

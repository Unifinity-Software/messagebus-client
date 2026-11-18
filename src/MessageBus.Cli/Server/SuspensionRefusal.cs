using System.Net;
using System.Text.Json;
using MessageBus.Contracts.Http;

namespace MessageBus.Cli.Server;

internal sealed class WorkspaceSuspendedException(string message) : HttpRequestException(message, null, HttpStatusCode.Forbidden);

internal static class SuspensionRefusal
{
    public static string Format(string? message, string? reference, string? supportEmail) =>
        $"{(message?.StartsWith("This workspace is suspended", StringComparison.OrdinalIgnoreCase) == true ? message : "This workspace is suspended: " + (message ?? "Contact support"))} ({reference ?? "reference unavailable"}). Contact {supportEmail ?? "support@messagebus.ai"}.";

    public static async Task<string?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var header = response.Headers.TryGetValues(MbusHeaders.Error, out var codes) && codes.Contains("workspace-suspended");
            if (!header && Field("code") != "workspace-suspended") return null;
            return Format(Field("message") ?? Field("error"), Field("reference"), Field("supportEmail"));
        }
        catch (JsonException) { return null; }
    }
}

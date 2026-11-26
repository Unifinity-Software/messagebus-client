using System.Net.Http.Headers;
using System.Text.Json;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

/// <summary>Fresh protocol discovery through the same authenticated HTTP client used for the operation.</summary>
public static class TargetCapabilities
{
    /// <summary>Whether the configured service runs workflows; nothing workflow-specific is sent until it does.</summary>
    public static Task<bool> SupportsWorkflowsAsync(HttpClient http, CancellationToken ct) =>
        SupportsAsync(http, WorkflowCapabilities.Workflows, ct);

    /// <summary>Reads the actual configured target with no positive cache. Failed or unrecognized discovery gives no support; caller cancellation is preserved.</summary>
    public static async Task<bool> SupportsAsync(HttpClient http, string capability, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/server");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var info = MessageBusJson.Deserialize<ServerCapabilitiesResponse>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return info is { Product: "messagebus.ai", Capabilities: { } capabilities }
                && capabilities.All(value => value is not null)
                && capabilities.Contains(capability, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

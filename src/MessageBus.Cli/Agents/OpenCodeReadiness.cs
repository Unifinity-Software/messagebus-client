using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MessageBus.Cli.Agents;

/// <summary>Read-only LM Studio checks. No inference, automatic load, downloads, redirects or proxy credentials.</summary>
internal static class OpenCodeReadiness
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    internal static HttpMessageHandler Handler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };

    internal static async Task<string?> CheckAsync(OpenCodeSelection selection, CancellationToken ct,
        Func<HttpMessageHandler>? handler = null, TimeProvider? time = null)
    {
        if (selection.Endpoint is not { } endpoint) return null;
        using var timeout = new CancellationTokenSource(Budget, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var client = new HttpClient((handler ?? Handler)()) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var baseUrl = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
            var nativeUrl = baseUrl[..^3] + "/api/v1/models";
            var native = await GetAsync(nativeUrl).ConfigureAwait(false);
            if (native["models"] is not JsonArray models) return Problem("loaded state is unknown (unsupported native model API)", "use an LM Studio version exposing /api/v1/models and eagerly load the selected model");
            var llms = models.OfType<JsonObject>().Where(m => OpenCodeSettings.Str(m["type"]) == "llm").ToArray();
            if (llms.Length == 0) return Problem("has no language models (an embedding model cannot run tasks)", "download and eagerly load a tool-capable coding model in LM Studio");
            var selected = llms.Where(m => OpenCodeSettings.Str(m["key"]) == selection.ApiId
                || m["loaded_instances"] is JsonArray instances && instances.OfType<JsonObject>().Any(i => OpenCodeSettings.Str(i["id"]) == selection.ApiId)).ToArray();
            if (selected.Length == 0) return Problem("does not list the selected model or API alias", "check OpenCode's modelID/id alias against LM Studio's model key or loaded instance ID");
            if (selected.Any(m => m["loaded_instances"] is not JsonArray)) return Problem("selected model loaded state is unknown", "use an LM Studio version exposing loaded_instances and eagerly load the selected model");
            if (!selected.Any(m => m["loaded_instances"] is JsonArray instances && instances.OfType<JsonObject>().Any(i => OpenCodeSettings.Str(i["id"]) is not null)))
                return Problem("selected model is downloaded but not loaded", "eagerly load it in LM Studio; discovery does not load models and this baseline does not assume JIT loading");
            var compatible = await GetAsync(baseUrl + "/models").ConfigureAwait(false);
            if (compatible["data"] is not JsonArray data) return Problem("returned an invalid OpenAI-compatible model list", "check the configured baseURL and LM Studio version");
            if (!data.OfType<JsonObject>().Any(m => OpenCodeSettings.Str(m["id"]) == selection.ApiId)) return Problem("loaded model's API ID does not match the selected model", "set modelID/id to the ID served by /v1/models");
            return null;
        }
        catch (LocalStatusException e)
        {
            return e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? Problem("refused authentication", "configure the runner's OpenCode apiKey for this server, or disable authentication in the operator-controlled local server")
                : Problem("model information is unavailable", "check the endpoint and LM Studio native /api/v1/models support; redirects are refused");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Problem("did not answer within five seconds", "start the local server and eagerly load the selected model, then rescan");
        }
        catch (HttpRequestException)
        {
            return Problem("server is not reachable from the runner", "start LM Studio's local server under the runner's account and check the configured loopback address/port, then rescan");
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Problem("returned invalid or oversized model information", "check the configured endpoint and LM Studio version (model responses must be at most 1 MiB)");
        }

        async Task<JsonObject> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (selection.ApiKey is { Length: > 0 } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LocalStatusException(response.StatusCode);
            if (response.Content.Headers.ContentLength > OpenCodeSettings.MaxBytes) throw new JsonException();
            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > OpenCodeSettings.MaxBytes) throw new JsonException();
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            return await JsonNode.ParseAsync(bytes, cancellationToken: linked.Token).ConfigureAwait(false) as JsonObject ?? throw new JsonException();
        }
    }

    internal static async Task<SignInCheck> ScanAsync(OpenCodeSettings settings, bool cloudSignedIn, CancellationToken ct,
        Func<HttpMessageHandler>? handler = null, TimeProvider? time = null)
    {
        if (settings.Problem is { } problem) return new(cloudSignedIn, cloudSignedIn ? null : problem, cloudSignedIn ? problem : null);
        var local = settings.Select(null);
        if (local.Endpoint is null)
            local = settings.Inventory.Models.Select(settings.Select).FirstOrDefault(m => m.Endpoint is not null) ?? local;
        if (local.Endpoint is null) return new(cloudSignedIn);
        var error = await CheckAsync(local, ct, handler, time).ConfigureAwait(false);
        // Never suppress authenticated cloud overrides because a local default is down.
        return error is null ? new(true, Warning: "LM Studio local provider ready (no vendor sign-in required); selected models are checked again before each run.")
            : new(cloudSignedIn, cloudSignedIn ? null : error, cloudSignedIn ? error : null);
    }

    private static string Problem(string reason, string remedy) => $"LM Studio local provider: {reason}. Fix: {remedy}.";
    private sealed class LocalStatusException(HttpStatusCode status) : Exception
    {
        internal HttpStatusCode Status => status;
    }
}

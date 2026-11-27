using System.Net;
using MessageBus.Cli.Runner.Sandbox;

namespace MessageBus.Cli.Agents;

internal static class AntigravityReadiness
{
    internal static HttpMessageHandler Handler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    internal static async Task<string?> CheckAsync(Func<HttpMessageHandler> handler, TimeProvider time, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var client = new HttpClient(handler()) { Timeout = Timeout.InfiniteTimeSpan };
        var unavailable = await Task.WhenAll(RunNetwork.AntigravityProviderHosts.Select(async host =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://" + host + "/");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                // Google may return 401, 404 or 405 without credentials or an API path.
                return response.StatusCode is HttpStatusCode.ProxyAuthenticationRequired or >= HttpStatusCode.InternalServerError ? host : null;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return host;
            }
        })).ConfigureAwait(false);
        var failed = unavailable.OfType<string>().ToArray();
        return failed.Length == 0 ? null
            : $"Antigravity CLI cannot reach {string.Join(", ", failed)}. Restore access to these Google hosts, then restart mbus runner or run mbus connect --rescan.";
    }
}

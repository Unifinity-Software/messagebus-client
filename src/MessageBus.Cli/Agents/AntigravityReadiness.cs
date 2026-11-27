using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Agents;

internal static class AntigravityReadiness
{
    internal static HttpMessageHandler Handler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    internal static async Task<DetectedAgent> CheckAsync(DetectedAgent agent, Func<HttpMessageHandler> handler,
        TimeProvider time, CancellationToken ct)
    {
        using var client = new HttpClient(handler()) { Timeout = Timeout.InfiniteTimeSpan };
        var unavailable = await Task.WhenAll(RunNetwork.AntigravityProviderHosts.Select(async host =>
            await ReachesAsync(host).ConfigureAwait(false) || await ReachesAsync(host).ConfigureAwait(false) ? null : host))
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var failed = unavailable.OfType<string>().ToArray();
        return failed.Length == 0 ? agent : agent with
        {
            NotReady = $"Antigravity CLI cannot reach {string.Join(", ", failed)}. Restore access to these Google hosts, then restart mbus runner or run mbus connect --rescan.",
        };

        async Task<bool> ReachesAsync(string host)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://" + host + "/");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (HttpRequestException ex)
            {
                return ex.StatusCode is not null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}

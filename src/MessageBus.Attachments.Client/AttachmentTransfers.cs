using System.Runtime.CompilerServices;

namespace MessageBus.Attachments.Client;

/// <summary>Authorizes one resolved transfer and awaits its actual credential-free transport send.</summary>
/// <param name="destination">The actual link destination, including its signed query. Never log it.</param>
/// <param name="send">The actual send. Await it inside the caller's current binding reservation.</param>
/// <param name="ct">Cancels discovery, authorization and transmission.</param>
public delegate Task<HttpResponseMessage> AttachmentTransferSend(Uri destination,
    Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct);

/// <summary>Shared transfer transport and optional caller-owned binding authorization, independent of CLI configuration.</summary>
public static class AttachmentTransfers
{
    private sealed record Policy(AttachmentTransferSend Send);
    private static readonly ConditionalWeakTable<HttpClient, Policy> Policies = new();
    private static readonly ConditionalWeakTable<HttpClient, object> TransferClients = new();

    /// <summary>
    /// Binds all attachment clients using this API client to a caller-owned current-binding send gate.
    /// Register once, before sharing the API client. Existing attachment constructors and MCP options inherit it.
    /// The callback must validate the actual destination and await send under its authorization reservation.
    /// </summary>
    public static void Bind(HttpClient api, AttachmentTransferSend send)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(send);
        Policies.Add(api, new Policy(send));
    }

    /// <summary>Creates a separate long-timeout transport with no redirects, cookies or ambient credentials.</summary>
    public static HttpClient CreateHttpClient() => CreateHttpClient(() => HttpClient.DefaultProxy);

    // Per-instance default-proxy seam keeps real transport fixtures independent of process globals.
    internal static HttpClient CreateHttpClient(Func<System.Net.IWebProxy> defaultProxy) => Register(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        PreAuthenticate = false, DefaultProxyCredentials = null,
        Proxy = MessageBus.Http.CredentialFreeProxy.Wrap(null, defaultProxy),
    }) { Timeout = TimeSpan.FromHours(1) });

    private static HttpClient Register(HttpClient client)
    {
        TransferClients.Add(client, new object());
        return client;
    }

    // Only the friend test assembly can substitute an in-memory terminal recorder. No public
    // handler/registration overload can turn an unknown authentication pipeline into a safe transport.
    internal static HttpClient CreateTestHttpClient(HttpMessageHandler handler, bool disposeHandler = true)
    {
        if (handler is DelegatingHandler or HttpClientHandler or SocketsHttpHandler)
            throw new ArgumentException("Test transfers require an in-memory terminal handler.", nameof(handler));
        return Register(new HttpClient(handler, disposeHandler));
    }

    internal static Uri Resolve(Uri? server, string url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
            || !Uri.TryCreate(server, url, out var uri) || !uri.IsAbsoluteUri
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme == "http" && (server?.Scheme == "https" || !uri.IsLoopback && server?.Scheme != "http"))
            throw new AttachmentTransferException("Unsafe attachment transfer destination; transfer refused.");
        return uri;
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient api, HttpClient links,
        HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        // Defaults are merged by HttpClient at send time. Refuse rather than mutating a caller's shared client.
        // In particular, using the authenticated API client here would attach credentials in its handlers.
        if (ReferenceEquals(api, links) || !TransferClients.TryGetValue(links, out _) || links.DefaultRequestHeaders.Any())
            throw new AttachmentTransferException("Attachment transfers require a separate factory-created client without default headers.");
        var response = Policies.TryGetValue(api, out var policy)
            ? await policy.Send(request.RequestUri!, Send, ct).ConfigureAwait(false)
            : await Send(ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new AttachmentTransferException("Attachment transfer redirects are refused.", status);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            // Transfer response bodies can echo signed URLs. Never surface them as error text.
            throw new AttachmentTransferException($"Attachment transfer failed ({(int)status}).", status);
        }
        return response;

        async Task<HttpResponseMessage> Send(CancellationToken token)
        {
            // Recheck after asynchronous discovery, immediately before HttpClient merges defaults.
            if (links.DefaultRequestHeaders.Any())
                throw new AttachmentTransferException("Attachment transfers require a client without default headers.");
            try { return await links.SendAsync(request, completion, token).ConfigureAwait(false); }
            catch (HttpRequestException)
            {
                // Handler/proxy diagnostics can contain the signed query. Do not expose them to tool logs.
                throw new AttachmentTransferException("Attachment transfer transport failed.");
            }
        }
    }
}

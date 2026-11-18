using System.Net.Http.Headers;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Security;

/// <summary>
/// Adds this machine's credentials and the mbus client headers to every outgoing request: a device-key
/// signature (<see cref="MachineRequestSigner"/>) for an enrolled machine, or <c>MbusKey</c> when
/// <c>MBUS_API_KEY</c> is set.
/// </summary>
internal sealed class AuthenticatingHandler : DelegatingHandler
{
    private readonly MachineRequestSigner? _signer;
    private readonly string? _apiKey;
    private readonly string? _workspaceId;
    private readonly string _component;
    private readonly string _version;

    private AuthenticatingHandler(HttpMessageHandler inner, MachineRequestSigner? signer, string? apiKey, string? workspaceId, string component, string version)
        : base(inner)
    {
        _signer = signer;
        _apiKey = apiKey;
        _workspaceId = workspaceId;
        _component = component;
        _version = version;
    }

    public static AuthenticatingHandler WithMachineKey(HttpMessageHandler inner, MachineRequestSigner signer, string? workspaceId, string component, string version) =>
        new(inner, signer, null, workspaceId, component, version);

    public static AuthenticatingHandler WithApiKey(HttpMessageHandler inner, string apiKey, string? workspaceId, string component, string version) =>
        new(inner, null, apiKey, workspaceId, component, version);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientVersion, _version);
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientComponent, _component);
        if (_workspaceId is not null && !request.Headers.Contains(MbusHeaders.Workspace))
        {
            request.Headers.TryAddWithoutValidation(MbusHeaders.Workspace, _workspaceId);
        }

        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.ApiKey, _apiKey);
        }
        else if (_signer is not null)
        {
            // The signature covers the exact body bytes, so read them once and send those same bytes.
            byte[] body = [];
            if (request.Content is { } content)
            {
                body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var copy = new ByteArrayContent(body);
                foreach (var header in content.Headers)
                {
                    copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                request.Content = copy;
            }
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no address.");
            foreach (var (name, value) in _signer.CreateHeaders(request.Method.Method, uri.PathAndQuery, body))
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

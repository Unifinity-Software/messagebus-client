using System.Net.Http.Headers;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Security;
using MessageBus.Cli.Server;

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
    private readonly ServerSendGate _gate;

    private AuthenticatingHandler(HttpMessageHandler inner, MachineRequestSigner? signer, string? apiKey, string? workspaceId, string component, string version, ServerSendGate gate)
        : base(inner)
    {
        _signer = signer;
        _apiKey = apiKey;
        _workspaceId = workspaceId;
        _component = component;
        _version = version;
        _gate = gate;
    }

    public static AuthenticatingHandler WithMachineKey(HttpMessageHandler inner, MachineRequestSigner signer, string? workspaceId, string component, string version, ServerSendGate gate) =>
        new(inner, signer, null, workspaceId, component, version, gate);

    public static AuthenticatingHandler WithApiKey(HttpMessageHandler inner, string apiKey, string? workspaceId, string component, string version, ServerSendGate gate) =>
        new(inner, null, apiKey, workspaceId, component, version, gate);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new ServerBindingException("The request has no destination.");
        _gate.DemandDestination(uri);
        var prepared = await _gate.PrepareSendAsync(uri, cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientVersion, _version);
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientComponent, _component);
        if (_workspaceId is not null && !request.Headers.Contains(MbusHeaders.Workspace))
        {
            request.Headers.TryAddWithoutValidation(MbusHeaders.Workspace, _workspaceId);
        }

        // Buffer before taking the consumption lock, for keys as well as machine signatures. No
        // asynchronous content work may leave a legacy authorization queued after the final floor read.
        byte[] body = [];
        if (request.Content is { } content)
        {
            body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var copy = new ByteArrayContent(body);
            foreach (var header in content.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = copy;
        }
        return await prepared.ConsumeAsync(request.RequestUri!, async authorization =>
        {
            if (_apiKey is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.ApiKey, _apiKey);
            }
            else if (_signer is not null)
            {
                // Version comes only from current discovery, never the caller's version header.
                request.Headers.Remove(MbusHeaders.SignatureVersion);
                foreach (var (name, value) in _signer.CreateHeaders(authorization, request, body))
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                response.Dispose();
                throw new ServerBindingException("Authenticated redirects are refused; no credentials were replayed.");
            }
            return response;
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }
}

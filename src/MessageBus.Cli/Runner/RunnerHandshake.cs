using System.Net.Http.Headers;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Security;
using MessageBus.Runner.Transport;

namespace MessageBus.Cli.Runner;

// The authorization and its explicit send reservation live until the actual upgrade finishes.
internal static class RunnerHandshake
{
    internal static bool IsTerminal(Exception exception) => exception is ServerBindingException
        && exception.InnerException is not (HttpRequestException or OperationCanceledException);

    internal static async Task ConnectAsync(ServerSendGate gate, ConnectRequest request,
        MachineRequestSigner? signer, string? apiKey, string? workspace, Func<CancellationToken, Task> connect,
        CancellationToken ct)
    {
        if (request.Uri.Scheme is not ("ws" or "wss"))
            throw new ServerBindingException("Invalid WebSocket transport scheme.");
        // Only translate transport scheme. Preserve the selected host, port and escaped target;
        // the canonical signing authority comes exclusively from the prepared binding.
        var text = request.Uri.AbsoluteUri;
        var target = new Uri((request.Uri.Scheme == "wss" ? "https" : "http") + text[text.IndexOf(':')..]);
        var prepared = await gate.PrepareSendAsync(target, ct).ConfigureAwait(false);
        await prepared.ConsumeAsync(target, async authorization =>
        {
            using var signed = new HttpRequestMessage(HttpMethod.Get, target);
            var actualWorkspace = request.GetHeader(MbusHeaders.Workspace) ?? workspace;
            if (actualWorkspace is not null) signed.Headers.Add(MbusHeaders.Workspace, actualWorkspace);
            if (apiKey is not null)
                signed.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.ApiKey, apiKey);
            else if (signer is not null)
                foreach (var (name, value) in signer.CreateHeaders(authorization, signed, []))
                    signed.Headers.TryAddWithoutValidation(name, value);
            else
                throw new ServerBindingException("The runner has no credentials for the selected endpoint.");
            signed.Headers.TryAddWithoutValidation(MbusHeaders.ClientComponent, "runner");
            signed.Headers.TryAddWithoutValidation(MbusHeaders.ClientVersion, RunnerEnvironment.Version);
            foreach (var header in signed.Headers)
                request.SetHeader(header.Key, string.Join(", ", header.Value));
            await connect(ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }
}

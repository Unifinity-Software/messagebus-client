using MessageBus.Attachments.Client;

namespace MessageBus.Cli.Server;

/// <summary>Connects every attachment factory using a built-in API client to its current durable binding gate.</summary>
internal static class ServerAttachmentTransfers
{
    internal static HttpClient Bind(HttpClient api, Func<ServerSendGate> createGate)
    {
        AttachmentTransfers.Bind(api, async (destination, send, ct) =>
        {
            try
            {
                using var gate = createGate();
                // Legacy SAS may be off-origin. Discovery always goes to the explicitly selected API,
                // never to the link host; ConsumeAsync rereads the same durable floor before sending.
                var selected = new Uri(gate.Endpoint.Value);
                var prepared = await gate.PrepareSendAsync(selected, ct).ConfigureAwait(false);
                if (prepared.Binding is not null && !gate.Endpoint.Contains(destination))
                    throw new ServerBindingException("Isolated attachment transfers require the selected server origin and API base path.");
                return await prepared.ConsumeAsync(selected, _ => send(ct), ct).ConfigureAwait(false);
            }
            catch (ServerBindingException)
            {
                // MCP/dead-letter callers understand this library exception and report a terminal refusal.
                // Do not retain inner transport errors which can contain URI diagnostics.
                throw new AttachmentTransferException("Current server binding refused the attachment transfer.",
                    System.Net.HttpStatusCode.Forbidden, MessageBus.Contracts.Mcp.McpErrorCodes.Forbidden);
            }
        });
        return api;
    }
}

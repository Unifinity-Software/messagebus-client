namespace MessageBus.Cli.Mcp;

/// <summary>
/// Everything the local MCP server needs from mbus, prepared by <c>mbus mcp</c>. Only BCL types, so
/// the server (MessageBus.Mcp, the original issue) needs no reference to this project.
/// </summary>
public sealed record McpHostContext
{
    /// <summary>The messagebus.ai server (no trailing slash).</summary>
    public required Uri ServerUrl { get; init; }

    /// <summary>
    /// Client for the server: <see cref="HttpClient.BaseAddress"/> is the server URL with a trailing
    /// slash, and every request is authenticated (device-key signature, or <c>MbusKey</c> when
    /// <c>MBUS_API_KEY</c> is set) and carries <c>X-Mbus-Client: mcp</c>, the version and the workspace.
    /// Use relative paths without a leading slash (<c>"mcp"</c>, <c>"api/w/…"</c>).
    /// </summary>
    public required HttpClient Http { get; init; }

    /// <summary>The workspace this machine is enrolled in (null with an API key and no login).</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The enrolled machine (null with an API key and no login).</summary>
    public string? MachineId { get; init; }

    /// <summary>mbus's settings folder (<c>~/.mbus</c>); owner-only. Keys the server needs (the original issue) can live here.</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>Reads the machine's private key (PEM) from mbus's key store (the original issue): DPAPI, the Keychain or the file.</summary>
    public Func<string?>? MachineKeyPem { get; init; }

    /// <summary>stdin: MCP messages from the agent.</summary>
    public required Stream Input { get; init; }

    /// <summary>stdout: MCP messages to the agent. Nothing else may be written to it.</summary>
    public required Stream Output { get; init; }

    /// <summary>stderr, for logs.</summary>
    public required TextWriter Log { get; init; }

    /// <summary>The mbus version.</summary>
    public required string ClientVersion { get; init; }
}

/// <summary>The stdio MCP server <c>mbus mcp</c> runs. Issue the original issue provides the real one.</summary>
public interface ILocalMcpServer
{
    /// <summary>Serves MCP over <see cref="McpHostContext.Input"/>/<see cref="McpHostContext.Output"/> until stdin closes or <paramref name="cancellationToken"/> fires.</summary>
    /// <returns>The process exit code.</returns>
    Task<int> RunAsync(McpHostContext context, CancellationToken cancellationToken);
}

/// <summary>Adapts a method (for example a static entry point in MessageBus.Mcp) to <see cref="ILocalMcpServer"/>.</summary>
public sealed class DelegateLocalMcpServer(Func<McpHostContext, CancellationToken, Task<int>> run) : ILocalMcpServer
{
    /// <inheritdoc />
    public Task<int> RunAsync(McpHostContext context, CancellationToken cancellationToken) => run(context, cancellationToken);
}

/// <summary>Stands in until the original issue's server is plugged in (<see cref="LocalMcpServerRegistration"/>).</summary>
internal sealed class NotAvailableLocalMcpServer : ILocalMcpServer
{
    public async Task<int> RunAsync(McpHostContext context, CancellationToken cancellationToken)
    {
        await context.Log.WriteLineAsync("The local MCP server isn't available in this build of mbus yet. Update mbus and try again.").ConfigureAwait(false);
        return 1;
    }
}

/// <summary>Which local MCP server <c>mbus mcp</c> runs.</summary>
internal static class LocalMcpServerRegistration
{
    // the original issue's encrypting proxy (src/MessageBus.Mcp). It also reads the runner's per-run MBUS_DELIVERY_FILE / MBUS_SETTLE_FILE.
    public static ILocalMcpServer Create() => new DelegateLocalMcpServer((ctx, ct) => MessageBus.Mcp.LocalServer.RunAsync(
        MessageBus.Mcp.LocalServer.Options(ctx.Http, ctx.WorkspaceId, ctx.MachineId, ctx.ConfigDirectory, ctx.Input, ctx.Output, ctx.Log, ctx.ClientVersion)
            with { MachineKeyPem = ctx.MachineKeyPem }, ct));
}

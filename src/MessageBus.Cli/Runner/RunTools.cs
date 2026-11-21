using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>What the runner needs to serve one run's mbus tools.</summary>
/// <param name="Workspace">The run's folders.</param>
/// <param name="Delivery">The task.</param>
/// <param name="Profile">What the run may do.</param>
/// <param name="Sandboxed">Wraps a process the tools start (git) in the run's sandbox and environment.</param>
public sealed record RunToolsRequest(RunWorkspace Workspace, TaskDelivery Delivery, PermissionProfile Profile, Func<ProcessLaunch, ProcessLaunch> Sandboxed)
{
    /// <summary>The session the run resumes for a reply to its inbox (the original issue), or null.</summary>
    public MessageBus.Mcp.SessionRecord? Session { get; init; }

    /// <summary>Origin verified by the runner, never inferred from the role's permission profile.</summary>
    public MessageBus.Mcp.ProposalAuthority ProposalAuthority { get; init; } = MessageBus.Mcp.ProposalAuthority.Worker;

    /// <summary>Reports the last non-retryable mbus refusal without trusting files the agent can rewrite.</summary>
    public Action<string, MessageBus.Contracts.Mcp.McpError>? Refused { get; init; }

    internal Action<MessageBus.Contracts.Mcp.SettleInput>? RecordVerifiedTaskSettle { get; init; }
}

/// <summary>
/// Serves each run's mbus tools from the runner itself (the original issue), so the agent never holds this machine's key or
/// API key: the agent's <c>mbus mcp</c> connects to the runner with a credential made for that run alone, and the
/// runner answers its calls as this machine, within the run's task (<see cref="Mcp.RunToolScope"/>).
/// </summary>
public interface IRunTools
{
    /// <summary>Starts serving the run's tools; disposing the session ends them and the credential with them.</summary>
    Task<RunToolsSession> StartAsync(RunToolsRequest request, CancellationToken cancellationToken);
}

/// <summary>One run's tools while it lasts.</summary>
public sealed class RunToolsSession(IReadOnlyDictionary<string, string> variables, Func<ValueTask> end) : IAsyncDisposable
{
    /// <summary>Variables for the run (and the MCP server its agent starts): <see cref="RunToolsVariables.File"/>.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; } = variables;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => end();
}

/// <summary>How a run finds its tools.</summary>
public static class RunToolsVariables
{
    /// <summary>
    /// The file (in the run's folder) naming the runner's local endpoint for this run and the run's credential for it.
    /// <c>mbus mcp</c> started with it set relays to the runner instead of signing in itself.
    /// </summary>
    public const string File = "MBUS_RUN_TOOLS";

    /// <summary>The file's name in the run's folder.</summary>
    public const string FileName = "mbus-tools.json";
}

/// <summary>
/// <see cref="IRunTools"/> over a local pipe (a named pipe on Windows, a Unix socket elsewhere): never a network
/// port, and only this user's processes may connect (<see cref="PipeOptions.CurrentUserOnly"/>). Each run gets
/// its own endpoint and a random credential, which the connecting side must send first; both end with the run.
/// </summary>
/// <param name="serve">Serves MCP on an accepted, authenticated connection until it closes.</param>
/// <param name="log">Runner log.</param>
public sealed class PipeRunTools(Func<RunToolsRequest, Stream, CancellationToken, Task> serve, TextWriter log) : IRunTools
{
    /// <summary>Calls the runner's authenticated tools pipe from a CLI command inside a run, without machine credentials.</summary>
    public static async Task<MessageBus.Mcp.ScriptResult> CallAsync(string file, string tool, JsonObject arguments, CancellationToken ct)
    {
        var json = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        var pipe = (string?)json?["pipe"] ?? throw new JsonException("The run's tools file has no pipe.");
        var token = (string?)json?["token"] ?? throw new JsonException("The run's tools file has no credential.");
        await using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await stream.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{Greeting} {token}\n"), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        var answer = await ReadLineAsync(stream, 64, ct).ConfigureAwait(false);
        if (answer is null || !answer.AsSpan().SequenceEqual("ok"u8))
            throw new UnauthorizedAccessException("The runner refused this run's tools credential.");
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
            new ModelContextProtocol.Protocol.StreamClientTransport(stream, stream),
            new ModelContextProtocol.Client.McpClientOptions
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = MessageBus.Mcp.ScriptSession.ClientName, Version = Cli.Hosting.CliHost.Version },
            }, cancellationToken: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(arguments.ToJsonString());
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var result = await client.CallToolAsync(new ModelContextProtocol.Protocol.CallToolRequestParams { Name = tool, Arguments = args }, ct).ConfigureAwait(false);
        var value = result.StructuredContent?.Clone()
            ?? JsonDocument.Parse(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text ?? "{}").RootElement.Clone();
        return new MessageBus.Mcp.ScriptResult(value, result.IsError == true
            ? MessageBus.Contracts.Json.MessageBusJson.Deserialize<MessageBus.Contracts.Mcp.McpError>(value.GetRawText())
                ?? new MessageBus.Contracts.Mcp.McpError { Code = "internal", Message = "The runner returned an invalid error." }
            : null);
    }

    /// <summary>What the connecting side sends first: this, a space, the credential and a newline.</summary>
    public const string Greeting = "MBUS-RUN";

    /// <inheritdoc />
    public Task<RunToolsSession> StartAsync(RunToolsRequest request, CancellationToken cancellationToken)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        // A Unix socket's path must stay under ~104 bytes, so it goes in /tmp (which runs may reach) rather than the run folder.
        var pipe = OperatingSystem.IsWindows() ? "mbus-run-" + id : "/tmp/mbus-run-" + id + ".sock";
        var file = Path.Combine(request.Workspace.Root, RunToolsVariables.FileName);
        System.IO.File.WriteAllText(file, new JsonObject { ["pipe"] = pipe, ["token"] = token }.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var stop = new CancellationTokenSource();
        var expected = Encoding.ASCII.GetBytes($"{Greeting} {token}");
        var connections = new List<Task>();
        var accepting = Task.Run(() => AcceptAsync(request, pipe, expected, connections, stop.Token), CancellationToken.None);

        async ValueTask End()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await accepting.ConfigureAwait(false);
                Task[] open;
                lock (connections)
                {
                    open = [.. connections];
                }
                await Task.WhenAll(open).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
            }
            stop.Dispose();
            TryDelete(file);
            if (!OperatingSystem.IsWindows())
            {
                TryDelete(pipe);
            }
        }

        return Task.FromResult(new RunToolsSession(new Dictionary<string, string> { [RunToolsVariables.File] = file }, End));
    }

    private async Task AcceptAsync(RunToolsRequest request, string pipe, byte[] expected, List<Task> connections, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                await log.WriteLineAsync($"{request.Delivery.DeliveryId}: the run's mbus tools stopped taking connections: {ex.Message}").ConfigureAwait(false);
                return;
            }
            var connection = Task.Run(() => HandleAsync(request, server, expected, ct), CancellationToken.None);
            lock (connections)
            {
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(connection);
            }
        }
    }

    private async Task HandleAsync(RunToolsRequest request, NamedPipeServerStream stream, byte[] expected, CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var greeting = await ReadLineAsync(stream, 256, ct).ConfigureAwait(false);
                if (greeting is null || !CryptographicOperations.FixedTimeEquals(greeting, expected))
                {
                    await log.WriteLineAsync($"{request.Delivery.DeliveryId}: refused a connection to the run's mbus tools without its credential.").ConfigureAwait(false);
                    await stream.WriteAsync("refused\n"u8.ToArray(), ct).ConfigureAwait(false);
                    return;
                }
                await stream.WriteAsync("ok\n"u8.ToArray(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                await serve(request, stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                await log.WriteLineAsync($"{request.Delivery.DeliveryId}: the run's mbus tools failed: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>Bytes up to a newline (not included), read one at a time so nothing after it is taken; null when the line is too long or the stream ends.</summary>
    internal static async Task<byte[]?> ReadLineAsync(Stream stream, int max, CancellationToken ct)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (line.Count <= max)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }
            if (one[0] == (byte)'\n')
            {
                return [.. line];
            }
            line.Add(one[0]);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The relay <c>mbus mcp</c> runs inside a run (<see cref="RunToolsVariables.File"/> set): connects to the runner
    /// with the run's credential and passes MCP through both ways until either side closes. Returns the exit code.
    /// </summary>
    public static async Task<int> RelayAsync(string file, Stream input, Stream output, TextWriter log, CancellationToken ct)
    {
        string pipe, token;
        try
        {
            var json = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            pipe = (string?)json?["pipe"] ?? throw new JsonException("no pipe");
            token = (string?)json?["token"] ?? throw new JsonException("no token");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            await log.WriteLineAsync($"mbus mcp: can't read this run's tools file {file} ({ex.Message}).").ConfigureAwait(false);
            return 1;
        }

        // On Unix an absolute pipe name is the socket's path.
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await client.WriteAsync(Encoding.ASCII.GetBytes($"{Greeting} {token}\n"), ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
            var answer = await ReadLineAsync(client, 64, ct).ConfigureAwait(false);
            if (answer is null || !answer.AsSpan().SequenceEqual("ok"u8))
            {
                await log.WriteLineAsync("mbus mcp: the runner refused this run's credential (the task has ended?).").ConfigureAwait(false);
                return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            await log.WriteLineAsync($"mbus mcp: can't reach the runner for this run's tools ({ex.Message}); the task may have ended.").ConfigureAwait(false);
            return 1;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Pump(input, client, stop.Token);
        var down = Pump(client, output, stop.Token);
        // Either the agent closed stdin (it is done with its tools) or the runner ended the run's tools.
        try
        {
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        return 0;

        static async Task Pump(Stream from, Stream to, CancellationToken token)
        {
            var buffer = new byte[16384];
            int read;
            while ((read = await from.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                await to.FlushAsync(token).ConfigureAwait(false);
            }
        }
    }
}

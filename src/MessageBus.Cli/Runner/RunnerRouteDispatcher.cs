using System.Text.Json;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>An answer to one service→runner request.</summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="Json">The body (application/json), or null for none.</param>
public sealed record RunnerRouteResponse(int StatusCode, string? Json);

/// <summary>
/// The runner's side of <see cref="RunnerRoutes"/>, independent of how requests arrive. The runner
/// connection (the original issue) hosts it: for each HTTP/2 request the service sends over the socket it calls
/// <see cref="HandleAsync"/> with the method, path and body, and writes the answer back. Bodies are
/// the contract types, serialized with <see cref="MessageBusJson"/>.
/// </summary>
/// <param name="engine">The runner.</param>
/// <param name="log">Where background failures (a rescan) are written.</param>
public sealed class RunnerRouteDispatcher(RunnerEngine engine, TextWriter log)
{
    /// <summary>The runner behind the routes (the transport reads its free slots when it connects).</summary>
    public RunnerEngine Engine => engine;

    /// <summary>Handles one request.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path (query ignored).</param>
    /// <param name="body">Request body, or null.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    public Task<RunnerRouteResponse> HandleAsync(string method, string path, string? body, CancellationToken cancellationToken)
    {
        var route = path.Split('?', 2)[0].TrimEnd('/');
        if (route == RunnerRoutes.Connect)
            return ConnectAsync(method, body, cancellationToken);
        if (route == RunnerRoutes.ToolSetup)
            return ToolSetupAsync(method, cancellationToken);
        try
        {
            return Task.FromResult((method.ToUpperInvariant(), route) switch
            {
                ("POST", RunnerRoutes.Deliver) => Ok(engine.Deliver(Read<TaskDelivery>(body))),
                // Idempotent: a run that already ended (or never ran here) needs nothing more.
                ("POST", RunnerRoutes.Cancel) => Do(() => engine.Cancel(Read<CancelDelivery>(body))),
                ("POST", RunnerRoutes.Drain) => Do(() => engine.Drain(Read<DrainRequest>(body))),
                ("POST", RunnerRoutes.Update) => Do(() => engine.RequestUpdate(Read<UpdateRequest>(body))),
                ("POST", RunnerRoutes.Rescan) => Do(() => _ = RescanInBackground()),
                ("GET", RunnerRoutes.Status) => Ok(engine.GetStatus()),
                ("POST", RunnerRoutes.Slots) => Ok(engine.SetSlots(Read<SlotsRequest>(body))),
                ("POST", RunnerRoutes.Policy) => Do(() => engine.ApplyPolicy(Read<RunnerWorkPolicy>(body))),
                (_, RunnerRoutes.Deliver or RunnerRoutes.Cancel or RunnerRoutes.Drain or RunnerRoutes.Update or RunnerRoutes.Rescan or RunnerRoutes.Status
                    or RunnerRoutes.Slots or RunnerRoutes.Policy)
                    => new RunnerRouteResponse(405, null),
                _ => new RunnerRouteResponse(404, null),
            });
        }
        catch (JsonException)
        {
            return Task.FromResult(new RunnerRouteResponse(400, null));
        }
    }

    private async Task<RunnerRouteResponse> ConnectAsync(string method, string? body, CancellationToken cancellationToken)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return new RunnerRouteResponse(405, null);
        try
        {
            return Ok(await engine.ConnectAgentAsync(Read<ConnectAgentRequest>(body), cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return new RunnerRouteResponse(400, null);
        }
    }

    // Checking whether Git works runs a process, so these two don't answer synchronously.
    private async Task<RunnerRouteResponse> ToolSetupAsync(string method, CancellationToken cancellationToken) => method.ToUpperInvariant() switch
    {
        "GET" => Ok(await engine.ToolSetupCurrentAsync(cancellationToken).ConfigureAwait(false)),
        "POST" => Ok(await engine.StartToolSetupAsync(cancellationToken).ConfigureAwait(false)),
        _ => new RunnerRouteResponse(405, null),
    };

    private async Task RescanInBackground()
    {
        try
        {
            await engine.RescanAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await log.WriteLineAsync($"Rescan failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static T Read<T>(string? body) =>
        MessageBusJson.Deserialize<T>(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? throw new JsonException("Empty body.");

    private static RunnerRouteResponse Ok<T>(T value) => new(200, MessageBusJson.Serialize(value));

    private static RunnerRouteResponse Accepted() => new(202, null);

    private static RunnerRouteResponse Do(Action action)
    {
        action();
        return Accepted();
    }
}

/// <summary>
/// The connection the service's requests arrive on. Integration point for the original issue: its connector dials
/// <see cref="RunnerProtocol.ConnectPath"/> with subprotocol <see cref="RunnerProtocol.SubProtocol"/>,
/// serves HTTP/2 over the socket by passing each request to the dispatcher, and reconnects with
/// Groundhog's backoff until <paramref name="cancellationToken"/> fires (or a terminal close code).
/// </summary>
public interface IRunnerTransport
{
    /// <summary>Serves <paramref name="dispatcher"/> until stopped.</summary>
    Task RunAsync(RunnerRouteDispatcher dispatcher, CancellationToken cancellationToken);
}

/// <summary>Stands in until the original issue's connector is plugged in: the runner sends inventory and heartbeats but receives nothing.</summary>
public sealed class NoRunnerTransport(TextWriter log) : IRunnerTransport
{
    /// <inheritdoc />
    public async Task RunAsync(RunnerRouteDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await log.WriteLineAsync("This build of mbus can't receive tasks yet: the runner connection isn't included. Update mbus.").ConfigureAwait(false);
        var stopped = new TaskCompletionSource();
        using (cancellationToken.Register(() => stopped.TrySetResult()))
        {
            await stopped.Task.ConfigureAwait(false);
        }
    }
}

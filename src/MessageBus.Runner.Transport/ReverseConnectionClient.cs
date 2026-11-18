using System.Globalization;
using System.Net.WebSockets;
using Groundhog.Protocol;
using MessageBus.Runner.Transport.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageBus.Runner.Transport;

/// <summary>
/// The client's half (the runner): dials out to the server over a WebSocket, serves
/// <see cref="ReverseConnectionClientOptions.MapRoutes"/> over HTTP/2 on that socket, and keeps it open,
/// reconnecting with backoff, until cancelled or told to stop for good. Nothing on the machine listens
/// on the network.
/// </summary>
/// <example>
/// <code>
/// var client = new ReverseConnectionClient(RunnerTransport.ClientOptions(serverAddress, app =>
/// {
///     app.MapPost(RunnerRoutes.Deliver, (TaskDelivery d) => new DeliveryDecision { Accepted = true });
/// }) with { PrepareRequest = SignAsync });
/// var stop = await client.RunAsync(stoppingToken);
/// </code>
/// </example>
public sealed class ReverseConnectionClient(ReverseConnectionClientOptions options)
{
    /// <summary>How long requests in flight get to finish when the client stops.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    // How long a connection may sit without requests before Kestrel closes it: practically never. The server
    // starts HTTP/2 with its first request, which may be long after connecting; pings detect dead sockets.
    // Not Timeout.InfiniteTimeSpan: with it, Kestrel closes a connection still waiting for its first request.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(365);

    // Honour a server's Retry-After, but never wait longer than this.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger("MessageBus.Runner.Transport");

    private SocketConnection? _current;
    private string? _dropReason;

    /// <summary>
    /// Drops the open connection and reconnects (with the usual backoff), for when the client learns by other
    /// means that the server no longer has it: through a proxy or relay the socket can stay open after the
    /// server behind it has gone, and pings may be answered on the server's behalf. False when not connected.
    /// </summary>
    /// <param name="reason">Why, for the reconnect status and the log.</param>
    public bool DropConnection(string reason) => Drop(Volatile.Read(ref _current), reason);

    private bool Drop(SocketConnection? connection, string reason)
    {
        if (connection is null || !ReferenceEquals(connection, Volatile.Read(ref _current)))
            return false;
        Volatile.Write(ref _dropReason, reason);
        connection.Abort(new ConnectionAbortedException(reason));
        return true;
    }

    /// <summary>
    /// Connects and stays connected until <paramref name="cancellationToken"/> is cancelled (then closes
    /// normally, letting requests in flight finish for up to <see cref="ShutdownTimeout"/>), the server
    /// refuses for good, or it closes with a terminal code.
    /// </summary>
    /// <param name="cancellationToken">Stops the client.</param>
    /// <returns>Why it stopped.</returns>
    public async Task<ReverseConnectionStop> RunAsync(CancellationToken cancellationToken)
    {
        options.KeepAlive.Validate();
        var listener = new SocketListener();
        var app = BuildServer(listener);
        ReverseConnectionStop stop;
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            Report(new ReverseConnectionStatus(ReverseConnectionState.Connecting));
            stop = await KeepConnectedAsync(listener, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stop = new ReverseConnectionStop(ReverseConnectionStopReason.Cancelled, "Stopped.");
        }
        finally
        {
            // Sends GOAWAY, lets requests in flight finish, then closes the socket normally.
            try
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }

        Report(new ReverseConnectionStatus(ReverseConnectionState.Stopped, stop.Message, CloseCode: stop.CloseCode, Stop: stop));
        return stop;
    }

    private async Task<ReverseConnectionStop> KeepConnectedAsync(SocketListener listener, CancellationToken cancellationToken)
    {
        var time = options.TimeProvider;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            string reason;
            TimeSpan? minimumDelay = null;
            int? closeCode = null;
            switch (outcome)
            {
                case ConnectOutcome.Refused refused:
                    return new ReverseConnectionStop(ReverseConnectionStopReason.Refused, refused.Reason, StatusCode: refused.StatusCode);

                case ConnectOutcome.Retry retry:
                    reason = retry.Reason;
                    minimumDelay = retry.RetryAfter;
                    break;

                case ConnectOutcome.Connected connected:
                    var connection = new SocketConnection(connected.WebSocket, time);
                    if (!listener.TryAdd(connection))
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        throw new OperationCanceledException(cancellationToken);
                    }

                    var openedAt = time.GetTimestamp();
                    Volatile.Write(ref _dropReason, null);
                    Volatile.Write(ref _current, connection);
                    _logger.LogInformation("Connected to {Uri}.", options.ConnectUri);
                    Report(new ReverseConnectionStatus(ReverseConnectionState.Connected));
                    var watch = connected.ServerPings is { } pings
                        ? new SilenceWatch(this, connection, options.KeepAlive.SilenceWindow(pings), time)
                        : null;

                    // Cancelling leaves the connection to the local server's shutdown, which closes it normally
                    // (at once if the server never used it: SocketConnection.CloseIfUnusedAsync).
                    ConnectionEnd end;
                    try
                    {
                        end = await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await connection.CloseIfUnusedAsync().ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        watch?.Dispose();
                        Volatile.Write(ref _current, null);
                    }

                    if (Volatile.Read(ref _dropReason) is { } dropped)
                    {
                        reason = dropped;
                        if (time.GetElapsedTime(openedAt) >= ReconnectBackoff.MaxDelay)
                            attempt = 0;
                        break;
                    }

                    closeCode = end.CloseCode;
                    if (end.CloseCode is int code && options.IsTerminalClose(code))
                    {
                        return new ReverseConnectionStop(
                            ReverseConnectionStopReason.TerminalClose,
                            $"The server closed the connection for good (close code {code}{Describe(end.CloseDescription)}).",
                            CloseCode: code);
                    }

                    reason = end.CloseCode is int closed
                        ? $"The server closed the connection (close code {closed}{Describe(end.CloseDescription)})."
                        : "Lost the connection to the server.";
                    if (end.CloseCode is int c && options.MinimumDelayAfterClose?.Invoke(c) is { } longer)
                        minimumDelay = longer;

                    // A connection that stayed up resets the schedule; one the server keeps closing right away doesn't.
                    if (time.GetElapsedTime(openedAt) >= ReconnectBackoff.MaxDelay)
                        attempt = 0;
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected connect outcome {outcome.GetType().Name}.");
            }

            var delay = options.ReconnectDelay(attempt);
            if (minimumDelay > delay)
                delay = minimumDelay.Value;
            attempt++;

            // Start the wait before reporting it, so whoever sees the report (a test advancing a fake clock, say)
            // can rely on the wait already counting.
            var wait = Task.Delay(delay, time, cancellationToken);
            _logger.LogInformation("{Reason} Retrying in {Delay}.", reason, delay);
            Report(new ReverseConnectionStatus(ReverseConnectionState.Reconnecting, reason, delay, closeCode));
            await wait.ConfigureAwait(false);
        }
    }

    private async Task<ConnectOutcome> ConnectAsync(CancellationToken cancellationToken)
    {
        var uri = options.ConnectUri;
        var webSocket = new ClientWebSocket();
        var settings = webSocket.Options;
        settings.AddSubProtocol(options.SubProtocol);
        settings.CollectHttpResponseDetails = true;
        settings.KeepAliveInterval = options.KeepAlive.PingInterval;
        settings.KeepAliveTimeout = options.KeepAlive.PongTimeout;

        using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (options.PrepareRequest is { } prepare)
                await prepare(new ConnectRequest(uri, settings), linked.Token).ConfigureAwait(false);
            await webSocket.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            webSocket.Dispose();
            return new ConnectOutcome.Retry("The server didn't answer in time.", null);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            var outcome = Classify(webSocket);
            webSocket.Dispose();
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely preparing the request failed (a credential store was unavailable): worth retrying.
            webSocket.Dispose();
            _logger.LogWarning(ex, "Connection attempt failed before reaching the server.");
            return new ConnectOutcome.Retry($"Couldn't connect: {ex.Message}", null);
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

        var serverPings = int.TryParse(Header(webSocket, KeepAlive.PingHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var every)
            && every > 0
                ? TimeSpan.FromSeconds(every)
                : (TimeSpan?)null;
        webSocket.HttpResponseHeaders = null;
        return new ConnectOutcome.Connected(webSocket, serverPings);
    }

    private static ConnectOutcome Classify(ClientWebSocket webSocket)
    {
        var status = (int)webSocket.HttpStatusCode;
        if (status < 400)
            return new ConnectOutcome.Retry("Couldn't reach the server.", null); // DNS, TCP or TLS failed.

        if (ConnectRefusal.IsPermanent(status))
        {
            var detail = status == ConnectRefusal.Unauthorized && Header(webSocket, "WWW-Authenticate") is { } accepted
                ? $" It accepts: {accepted}."
                : "";
            return new ConnectOutcome.Refused(status, $"The server refused the connection (HTTP {status}).{detail}");
        }

        return new ConnectOutcome.Retry($"The server answered HTTP {status}.",
            status == ConnectRefusal.TooManyAttempts || status == 503 ? RetryAfter(webSocket) : null);
    }

    private static string? Header(ClientWebSocket webSocket, string name) =>
        webSocket.HttpResponseHeaders is { } headers && headers.TryGetValue(name, out var values)
            ? string.Join(", ", values)
            : null;

    private static TimeSpan? RetryAfter(ClientWebSocket webSocket) =>
        int.TryParse(Header(webSocket, "Retry-After"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfter.TotalSeconds))
            : null;

    private static string Describe(string? description) =>
        string.IsNullOrWhiteSpace(description) ? "" : $": {description}";

    private WebApplication BuildServer(SocketListener listener)
    {
        // The empty builder reads no configuration or environment variables, so settings such as
        // ASPNETCORE_URLS in the shell can't make the client listen on a port; Kestrel core has no
        // network transport, only the outbound socket.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.KeepAliveTimeout = IdleTimeout;
            kestrel.Limits.Http2.MaxStreamsPerConnection = Math.Max(100, ConnectionPool.MinConcurrentStreams);
            kestrel.Listen(listener.EndPoint, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<IConnectionListenerFactory>(listener);
        builder.Services.AddSingleton(options.LoggerFactory ?? NullLoggerFactory.Instance);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        options.MapRoutes(app);
        return app;
    }

    private void Report(ReverseConnectionStatus status)
    {
        try
        {
            options.OnStatus?.Invoke(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A status callback failed.");
        }
    }

    /// <summary>
    /// Drops a connection on which nothing has arrived from the server for <c>window</c>. Only for servers that ping
    /// over HTTP/2 (<see cref="KeepAlive.PingHeader"/>): from those something arrives at least every ping interval,
    /// through any proxy or relay, so silence means the server is gone even while the socket looks open and
    /// something in between answers its WebSocket pings.
    /// </summary>
    private sealed class SilenceWatch : IDisposable
    {
        private readonly ReverseConnectionClient _client;
        private readonly SocketConnection _connection;
        private readonly TimeSpan _window;
        private readonly ITimer _timer;

        public SilenceWatch(ReverseConnectionClient client, SocketConnection connection, TimeSpan window, TimeProvider time)
        {
            _client = client;
            _connection = connection;
            _window = window;
            _timer = time.CreateTimer(_ => Check(), null, window, Timeout.InfiniteTimeSpan);
        }

        private void Check()
        {
            var quiet = _connection.Quiet;
            if (quiet < _window)
            {
                // Heard from it since: look again when the window after that runs out.
                _timer.Change(_window - quiet, Timeout.InfiniteTimeSpan);
                return;
            }
            var seconds = (int)_window.TotalSeconds;
            _client._logger.LogWarning("Nothing arrived from the server for {Seconds} s; dropping the connection.", seconds);
            _client.Drop(_connection,
                $"Nothing arrived from the server for {seconds} s (something in between may be holding a dead connection open).");
        }

        public void Dispose() => _timer.Dispose();
    }

    private abstract record ConnectOutcome
    {
        /// <param name="WebSocket">The open socket.</param>
        /// <param name="ServerPings">How often the server pings over HTTP/2 when the client is quiet, if it does.</param>
        public sealed record Connected(WebSocket WebSocket, TimeSpan? ServerPings) : ConnectOutcome;

        public sealed record Retry(string Reason, TimeSpan? RetryAfter) : ConnectOutcome;

        public sealed record Refused(int StatusCode, string Reason) : ConnectOutcome;
    }
}

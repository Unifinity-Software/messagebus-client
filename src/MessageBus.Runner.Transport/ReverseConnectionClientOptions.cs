using System.Net.WebSockets;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageBus.Runner.Transport;

/// <summary>Settings for <see cref="ReverseConnectionClient"/>. <see cref="RunnerTransport.ClientOptions"/> fills in the <c>mbus.v1</c> values.</summary>
public sealed record ReverseConnectionClientOptions
{
    /// <summary>The server's address, for example <c>https://messagebus.example.com</c>. http becomes ws, https becomes wss.</summary>
    public required Uri ServerAddress { get; init; }

    /// <summary>The connect path on the server, for example <c>/api/runner/connect</c>.</summary>
    public required string ConnectPath { get; init; }

    /// <summary>The WebSocket subprotocol to offer, for example <c>mbus.v1</c>.</summary>
    public required string SubProtocol { get; init; }

    /// <summary>
    /// Maps the routes the server may call, for example <c>app.MapPost("/deliveries", …)</c>. Runs once per
    /// <see cref="ReverseConnectionClient.RunAsync"/>, on a Kestrel server that only serves this connection.
    /// </summary>
    public required Action<WebApplication> MapRoutes { get; init; }

    /// <summary>Registers services those routes need (JSON options, handlers). Routing is already added.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Called before every connect attempt to add headers: credentials (a fresh signature each time),
    /// session id, version. The <see cref="ConnectRequest.Uri"/> says exactly what is being requested.
    /// </summary>
    public Func<ConnectRequest, CancellationToken, ValueTask>? PrepareRequest { get; init; }

    /// <summary>
    /// Runs after PrepareRequest and wraps authorization and the actual handshake. The callback must await
    /// <c>connect</c> before returning; credentials must never escape for a later handshake.
    /// </summary>
    public Func<ConnectRequest, Func<CancellationToken, Task>, CancellationToken, Task>? ExecuteConnect { get; init; }

    /// <summary>Identifies local authorization failures that must stop reconnecting rather than retry.</summary>
    public Func<Exception, bool>? IsTerminalPreparationException { get; init; }

    /// <summary>Ping and silence timing (Groundhog's 15 s / 45 s by default).</summary>
    public KeepAlive KeepAlive { get; init; } = KeepAlive.Default;

    /// <summary>Close codes after which the client stops instead of reconnecting (Groundhog's terminal codes by default).</summary>
    public Func<int, bool> IsTerminalClose { get; init; } = GroundhogCloseCodes.IsTerminal;

    /// <summary>A longer wait than the backoff after particular close codes (for example "held for approval"), or null.</summary>
    public Func<int, TimeSpan?>? MinimumDelayAfterClose { get; init; }

    /// <summary>The wait before reconnect attempt <c>n</c> (zero-based; reset after a connection that stayed up). Groundhog's 1–30 s schedule by default.</summary>
    public Func<int, TimeSpan> ReconnectDelay { get; init; } = static attempt => ReconnectBackoff.GetDelay(attempt, Random.Shared);

    /// <summary>The longest one connect attempt may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Clock for backoff waits and timeouts.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Logging for the client and its local server; none by default.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Told about every state change (connecting, connected, reconnecting, stopped).</summary>
    public Action<ReverseConnectionStatus>? OnStatus { get; init; }

    internal Uri ConnectUri
    {
        get
        {
            var scheme = ServerAddress.Scheme is "https" or "wss" ? "wss" : "ws";
            var builder = new UriBuilder(ServerAddress) { Scheme = scheme, Path = ConnectPath, Query = "" };
            builder.Port = ServerAddress.IsDefaultPort ? -1 : ServerAddress.Port;
            return builder.Uri;
        }
    }
}

/// <summary>One connect attempt, for adding headers.</summary>
public sealed class ConnectRequest
{
    private readonly ClientWebSocketOptions _options;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    internal ConnectRequest(Uri uri, ClientWebSocketOptions options)
    {
        Uri = uri;
        _options = options;
    }

    /// <summary>The ws/wss URI being requested.</summary>
    public Uri Uri { get; }

    /// <summary>The method on the wire (a WebSocket upgrade is a GET), for request signing.</summary>
    public string Method => "GET";

    /// <summary>Path and query exactly as on the request line, for request signing.</summary>
    public string PathAndQuery => Uri.PathAndQuery;

    /// <summary>Adds or replaces a request header.</summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    public void SetHeader(string name, string value)
    {
        _options.SetRequestHeader(name, value);
        _headers[name] = value;
    }

    /// <summary>The final value set for a header on this attempt, or null when absent.</summary>
    public string? GetHeader(string name) => _headers.GetValueOrDefault(name);
}

/// <summary>Where the client is.</summary>
public enum ReverseConnectionState
{
    /// <summary>Opening the first connection.</summary>
    Connecting,
    /// <summary>Connected; the server can call the routes.</summary>
    Connected,
    /// <summary>Waiting <see cref="ReverseConnectionStatus.RetryDelay"/> before trying again.</summary>
    Reconnecting,
    /// <summary>Stopped for good; see <see cref="ReverseConnectionStatus.Stop"/>.</summary>
    Stopped,
}

/// <summary>A state change.</summary>
/// <param name="State">The new state.</param>
/// <param name="Reason">What happened, for logs and the console.</param>
/// <param name="RetryDelay">The wait before the next attempt, when reconnecting.</param>
/// <param name="CloseCode">The server's close code, when it closed the connection.</param>
/// <param name="Stop">Why the client stopped, when stopped.</param>
public sealed record ReverseConnectionStatus(
    ReverseConnectionState State,
    string? Reason = null,
    TimeSpan? RetryDelay = null,
    int? CloseCode = null,
    ReverseConnectionStop? Stop = null);

/// <summary>Why <see cref="ReverseConnectionClient.RunAsync"/> returned.</summary>
public enum ReverseConnectionStopReason
{
    /// <summary>The caller cancelled.</summary>
    Cancelled,
    /// <summary>The server refused the connect request with a permanent HTTP status (401, 403, 404, 426).</summary>
    Refused,
    /// <summary>The server closed with a code <see cref="ReverseConnectionClientOptions.IsTerminalClose"/> says is final.</summary>
    TerminalClose,
}

/// <summary>How the client ended.</summary>
/// <param name="Reason">Why.</param>
/// <param name="Message">A sentence for the console.</param>
/// <param name="StatusCode">The HTTP status, when refused.</param>
/// <param name="CloseCode">The close code, when closed for good.</param>
public sealed record ReverseConnectionStop(
    ReverseConnectionStopReason Reason,
    string Message,
    int? StatusCode = null,
    int? CloseCode = null);

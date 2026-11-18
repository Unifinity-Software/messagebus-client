using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Http;

namespace MessageBus.Runner.Transport;

/// <summary>
/// The service's half: accepts a client's WebSocket and turns it into an HTTP/2 connection on which the
/// service is the client (<see cref="AcceptedConnection"/>). Authenticate the request before calling
/// <see cref="AcceptAsync"/>; refusals are plain HTTP answers sent instead of the upgrade.
/// </summary>
public static class ConnectionAcceptor
{
    /// <summary>Whether the request is a WebSocket upgrade that offers <paramref name="subProtocol"/>.</summary>
    /// <param name="context">The connect request.</param>
    /// <param name="subProtocol">The subprotocol the server speaks, for example <c>mbus.v1</c>.</param>
    public static bool OffersSubProtocol(HttpContext context, string subProtocol) =>
        context.WebSockets.IsWebSocketRequest
        && context.WebSockets.WebSocketRequestedProtocols.Contains(subProtocol, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upgrades the request to a WebSocket with <paramref name="subProtocol"/> and starts reading it, so pings,
    /// pongs and the client's close are handled before the first request is sent. Starts HTTP/2 at once and pings
    /// over it, telling the client so (<see cref="KeepAlive.PingHeader"/>). Keep the request delegate running until
    /// <see cref="AcceptedConnection.Completion"/>: the socket belongs to the request.
    /// </summary>
    /// <param name="context">The authenticated connect request.</param>
    /// <param name="subProtocol">The subprotocol to answer with.</param>
    /// <param name="keepAlive">Ping and silence timing.</param>
    /// <param name="time">Clock for close grace periods.</param>
    public static async Task<AcceptedConnection> AcceptAsync(
        HttpContext context, string subProtocol, KeepAlive keepAlive, TimeProvider time)
    {
        keepAlive.Validate();
        context.Response.Headers[KeepAlive.PingHeader] =
            ((int)keepAlive.Http2PingDelay.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var webSocket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = subProtocol,
            KeepAliveInterval = keepAlive.PingInterval,
            KeepAliveTimeout = keepAlive.PongTimeout,
        }).ConfigureAwait(false);

        var connection = new AcceptedConnection(webSocket, keepAlive, time);
        connection.Start();
        return connection;
    }
}

/// <summary>
/// One accepted WebSocket carrying one HTTP/2 connection on which the service is the client and the
/// other end (the runner) is the server.
/// </summary>
/// <remarks>
/// A background loop reads the WebSocket from the moment it's accepted and buffers what arrives (up to a
/// limit, then it waits) for the HTTP/2 connection, which an opening request starts at once. The invoker's
/// connect callback hands out the socket's stream once, speaking HTTP/2 without TLS (prior knowledge),
/// and fails if asked again, so requests can only ever go over this socket.
/// <para>
/// HTTP/2 PINGs go out whenever the client has been quiet for the ping interval, whether or not a request is in
/// flight: the client hears from the server regularly through anything in between, and the service notices a
/// client it can no longer reach even when something in between answers its WebSocket pings. When the HTTP/2
/// connection gives up, the socket is closed, and dropped if the close isn't answered.
/// </para>
/// </remarks>
public sealed class AcceptedConnection : IAsyncDisposable
{
    /// <summary>The authority used in request URIs. Nothing resolves it; the socket is the only route.</summary>
    public static readonly Uri BaseAddress = new("http://runner.invalid/");

    /// <summary>How long the client gets to answer the close sent when the HTTP/2 connection ends.</summary>
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(10);

    private const int ReadSize = 16 * 1024;

    private static readonly PipeOptions IncomingOptions = new(
        pauseWriterThreshold: 1024 * 1024,
        resumeWriterThreshold: 512 * 1024,
        useSynchronizationContext: false);

    private readonly WebSocket _webSocket;
    private readonly WebSocketDuplexStream _stream;
    private readonly TimeProvider _time;
    private readonly Pipe _incoming = new(IncomingOptions);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpMessageInvoker _invoker;
    private readonly Lock _gate = new();
    private Task? _closeTask;
    private int _transportHandedOut;

    internal AcceptedConnection(WebSocket webSocket, KeepAlive keepAlive, TimeProvider time)
    {
        _webSocket = webSocket;
        _time = time;
        _stream = new WebSocketDuplexStream(webSocket, ownsWebSocket: false);
        _invoker = new HttpMessageInvoker(CreateHandler(keepAlive), disposeHandler: true);
    }

    /// <summary>Completes when the WebSocket has closed or failed (including a silence timeout). Never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>The close code the client sent, if it closed the WebSocket.</summary>
    public int? CloseCode => (int?)_webSocket.CloseStatus;

    /// <summary>
    /// Sends <paramref name="request"/> to the client over this connection. A relative URI is resolved
    /// against <see cref="BaseAddress"/>; HTTP/2 is forced.
    /// </summary>
    /// <param name="request">The request; its URI is usually just a path such as <c>/deliveries</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HttpRequestException">The connection is closed or broke.</exception>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
            request.RequestUri = new Uri(BaseAddress, request.RequestUri?.OriginalString ?? "/");
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return _invoker.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends a close with <paramref name="closeCode"/> once (later calls return the first close).</summary>
    /// <param name="closeCode">The WebSocket close code.</param>
    /// <param name="description">A short reason.</param>
    public Task CloseAsync(int closeCode, string? description)
    {
        lock (_gate)
            return _closeTask ??= SendCloseAsync(closeCode, description);
    }

    /// <summary>
    /// Sends a close, then aborts the socket if the client hasn't finished closing within <paramref name="grace"/>.
    /// </summary>
    /// <param name="closeCode">The WebSocket close code.</param>
    /// <param name="description">A short reason.</param>
    /// <param name="grace">How long the client gets to answer the close.</param>
    public async Task CloseAsync(int closeCode, string? description, TimeSpan grace)
    {
        await CloseAsync(closeCode, description).ConfigureAwait(false);
        try
        {
            await Completion.WaitAsync(grace, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _webSocket.Abort();
        }
    }

    /// <summary>Drops the socket at once, without a close handshake.</summary>
    public void Abort() => _webSocket.Abort();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _invoker.Dispose();
        await CloseAsync(GroundhogCloseCodes.Normal, null).ConfigureAwait(false);
        _webSocket.Dispose();
        await Completion.ConfigureAwait(false);
    }

    internal void Start()
    {
        _ = PumpAsync();
        _ = OpenAsync();
    }

    /// <summary>
    /// Starts HTTP/2 now rather than with the first real request, so its PINGs flow from the start. Any answer
    /// will do (a client without the route says 404); a failure means the connection is already ending.
    /// </summary>
    private async Task OpenAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "/");
            using var response = await SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private SocketsHttpHandler CreateHandler(KeepAlive keepAlive) => new()
    {
        ConnectCallback = ConnectAsync,
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        EnableMultipleHttp2Connections = false,

        // An idle connection keeps its HTTP/2 session; pings tell when it's dead.
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        KeepAlivePingDelay = keepAlive.Http2PingDelay,
        KeepAlivePingTimeout = keepAlive.Http2PingTimeout,
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,

        // The caller decides which trace headers to send.
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    };

    private ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _transportHandedOut, 1) != 0 || Completion.IsCompleted)
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The connection has closed.");

        return ValueTask.FromResult<Stream>(new TransportStream(this));
    }

    private async Task PumpAsync()
    {
        var writer = _incoming.Writer;
        Exception? failure = null;
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(writer.GetMemory(ReadSize)).ConfigureAwait(false);
                if (read == 0)
                    break;

                writer.Advance(read);
                var flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    // The HTTP/2 connection is gone; keep reading so the client's close is still seen.
                    await DrainAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Includes the silence timeout: the WebSocket aborts itself when a ping goes unanswered.
            failure = ex;
        }
        finally
        {
            await writer.CompleteAsync(failure is null ? null : new IOException("The connection failed.", failure))
                .ConfigureAwait(false);
            _completion.TrySetResult();
        }
    }

    private async Task DrainAsync()
    {
        var buffer = new byte[ReadSize];
        while (await _stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
        }
    }

    private async Task SendCloseAsync(int closeCode, string? description)
    {
        if (_webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            await _webSocket.CloseOutputAsync((WebSocketCloseStatus)closeCode, description, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
            _webSocket.Abort();
        }
    }

    /// <summary>The stream the HTTP/2 client reads and writes: buffered WebSocket input, direct WebSocket output.</summary>
    private sealed class TransportStream(AcceptedConnection owner) : Stream
    {
        private readonly Stream _input = owner._incoming.Reader.AsStream();
        private int _disposed;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _input.ReadAsync(buffer, offset, count, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            owner._stream.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            owner._stream.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => owner._stream.Write(buffer, offset, count);

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // The HTTP/2 client gave up on this connection (the client stopped, or stopped answering its pings);
                // end the WebSocket too so the client reconnects, and drop it if nothing answers the close.
                _input.Dispose();
                _ = owner.CloseAsync(GroundhogCloseCodes.Normal, null, CloseGrace);
            }

            base.Dispose(disposing);
        }
    }
}

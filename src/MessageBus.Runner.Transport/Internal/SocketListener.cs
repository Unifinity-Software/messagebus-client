using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Groundhog.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace MessageBus.Runner.Transport.Internal;

/// <summary>How a connection ended: the server's close code, or none when the connection was simply lost.</summary>
internal readonly record struct ConnectionEnd(int? CloseCode, string? CloseDescription);

/// <summary>The endpoint Kestrel "listens" on: the client's open connection to the server.</summary>
internal sealed class OutboundEndPoint : EndPoint
{
    public override string ToString() => "outbound-websocket";
}

/// <summary>
/// A Kestrel transport whose connections are the WebSockets the client opened to the server, instead of
/// sockets accepted from the network. Nothing on the machine listens for incoming connections.
/// </summary>
internal sealed class SocketListener : IConnectionListenerFactory, IConnectionListenerFactorySelector, IConnectionListener
{
    private readonly Channel<SocketConnection> _pending = Channel.CreateUnbounded<SocketConnection>(
        new UnboundedChannelOptions { SingleReader = true });

    public EndPoint EndPoint { get; } = new OutboundEndPoint();

    /// <summary>Hands a newly opened connection to Kestrel. Returns false once the server is shutting down.</summary>
    public bool TryAdd(SocketConnection connection) => _pending.Writer.TryWrite(connection);

    public bool CanBind(EndPoint endpoint) => endpoint is OutboundEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        endpoint is OutboundEndPoint
            ? ValueTask.FromResult<IConnectionListener>(this)
            : throw new NotSupportedException($"Only {nameof(OutboundEndPoint)} is supported.");

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            return null; // Tells Kestrel the listener is done.
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.Writer.TryComplete())
        {
            // Connections opened but never accepted are closed here; their owners see them end.
            while (_pending.Reader.TryRead(out var connection))
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => UnbindAsync();
}

/// <summary>
/// One WebSocket to the server, presented to Kestrel as a transport connection. Kestrel speaks HTTP/2 over
/// it and disposes it when the HTTP/2 connection is over, which completes <see cref="Completion"/>.
/// </summary>
internal sealed class SocketConnection : ConnectionContext
{
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(2);
    private static long s_nextId;

    private readonly WebSocketDuplexStream _stream;
    private readonly UncancelledReads _reads;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<ConnectionEnd> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public SocketConnection(WebSocket webSocket, TimeProvider time)
    {
        _stream = new WebSocketDuplexStream(webSocket);
        // Kestrel cancels its pending read when it shuts down, and cancelling a WebSocket receive aborts the
        // socket, which would drop the connection without a close. Reads therefore ignore cancellation; the
        // close handshake in DisposeAsync ends the last one. (So Kestrel's own HTTP/2 keep-alive, which runs when
        // its heartbeat cancels the pending read, can't work here; the server pings instead, KeepAlive.PingHeader.)
        _reads = new UncancelledReads(_stream, time);
        Transport = new DuplexPipe(
            PipeReader.Create(_reads, new StreamPipeReaderOptions(leaveOpen: true)),
            PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true)));
        ConnectionId = $"outbound-{Interlocked.Increment(ref s_nextId)}";
        ConnectionClosed = _closed.Token;
    }

    /// <summary>Completes when the connection is over, with the server's close code if it sent one.</summary>
    public Task<ConnectionEnd> Completion => _completion.Task;

    /// <summary>
    /// How long since data last arrived from the server (or since connecting). WebSocket pings and pongs don't
    /// count: something between the client and the server can answer those.
    /// </summary>
    public TimeSpan Quiet => _reads.Quiet;

    public override string ConnectionId { get; set; }

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    public override IDuplexPipe Transport { get; set; }

    /// <summary>
    /// When the client stops: closes the connection normally if the server never sent anything on it. The server
    /// starts HTTP/2 with its first request, so until then no request can be in flight, and Kestrel's graceful
    /// shutdown would only wait out <see cref="ReverseConnectionClient.ShutdownTimeout"/> for an answer to a
    /// GOAWAY that nobody reads, then drop the socket (which the server sees as a lost runner, not a stopped one).
    /// The server answers the close, Kestrel's pending read ends, and Kestrel disposes the connection as usual.
    /// Returns once that has happened (or after <see cref="CloseHandshakeTimeout"/>), so the shutdown that follows
    /// can't drop the socket before the server has read the close.
    /// </summary>
    public async Task CloseIfUnusedAsync()
    {
        var webSocket = _stream.WebSocket;
        if (_reads.Received || webSocket.State != WebSocketState.Open)
            return;
        using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
        await CompleteQuietlyAsync(_ => new ValueTask(
            webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stopping", timeout.Token))).ConfigureAwait(false);
        await CompleteQuietlyAsync(_ => new ValueTask(Completion.WaitAsync(timeout.Token))).ConfigureAwait(false);
    }

    // Ends any pending read or write; Kestrel then completes the pipes and disposes the connection.
    public override void Abort(ConnectionAbortedException abortReason) => _stream.WebSocket.Abort();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var webSocket = _stream.WebSocket;
        ConnectionEnd? end = null;
        try
        {
            // Completing the output flushes Kestrel's last buffered write (its GOAWAY, say), which fails when the
            // server is already gone. That is simply the connection ending; it must never keep the owner from
            // seeing the end, or the client would wait forever instead of reconnecting.
            await CompleteQuietlyAsync(Transport.Input.CompleteAsync).ConfigureAwait(false);
            await CompleteQuietlyAsync(Transport.Output.CompleteAsync).ConfigureAwait(false);

            // The server's close code, read before the client's own close reply.
            end = new ConnectionEnd((int?)webSocket.CloseStatus, webSocket.CloseStatusDescription);

            // The client is ending the connection (it is stopping): finish the close handshake, so the server
            // sees a normal close rather than a dropped socket (closing the socket at once can reset it and lose
            // the close frame).
            if (webSocket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
                await CompleteQuietlyAsync(_ => new ValueTask(
                    webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopping", timeout.Token))).ConfigureAwait(false);
            }

            // Answers the server's close, if it closed first.
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // Already broken.
        }
        finally
        {
            _completion.TrySetResult(end ?? new ConnectionEnd(null, null));
            try
            {
                await _closed.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                _closed.Dispose();
            }
        }
    }

    private static async ValueTask CompleteQuietlyAsync(Func<Exception?, ValueTask> complete)
    {
        try
        {
            await complete(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // The WebSocket is already broken; there's nothing left to flush to.
        }
    }

    private sealed class UncancelledReads(Stream inner, TimeProvider time) : Stream
    {
        private volatile bool _received;
        private long _lastReceived = time.GetTimestamp();

        /// <summary>True once a read has returned data: the server has started HTTP/2 on the connection.</summary>
        public bool Received => _received;

        /// <summary>How long since a read last returned data, or since the connection opened.</summary>
        public TimeSpan Quiet => time.GetElapsedTime(Interlocked.Read(ref _lastReceived));

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        private int Count(int read)
        {
            if (read > 0)
            {
                Interlocked.Exchange(ref _lastReceived, time.GetTimestamp());
                _received = true;
            }
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}

using Groundhog.Protocol;

namespace MessageBus.Runner.Transport;

/// <summary>
/// How each side notices a dead connection: it pings every <see cref="PingInterval"/>, and a ping
/// not answered in time means <see cref="SilenceTimeout"/> without hearing from the peer, which aborts
/// the socket. That is also how a connection killed by sleep or a network change is noticed.
/// <para>
/// WebSocket pings only prove that the next hop is there: a proxy or relay in between can answer them itself
/// after the server behind it has gone. So the server also pings over HTTP/2, inside the socket's data where
/// nothing in between can answer (<see cref="PingHeader"/>), and the client drops a connection on which
/// nothing at all has arrived for its silence timeout.
/// </para>
/// </summary>
/// <param name="PingInterval">How often to ping (Groundhog: 15 s).</param>
/// <param name="SilenceTimeout">How long without hearing from the peer before the socket is dropped (Groundhog: 45 s).</param>
public sealed record KeepAlive(TimeSpan PingInterval, TimeSpan SilenceTimeout)
{
    /// <summary>
    /// Sent by the server with its upgrade answer: it starts HTTP/2 at once and sends an HTTP/2 PING whenever it has
    /// heard nothing from the client for this many seconds, so the client always has something arriving. Clients
    /// that don't know the header ignore it (the PINGs are answered by any HTTP/2 server); servers that don't send
    /// it leave the client to its WebSocket pings.
    /// </summary>
    public const string PingHeader = "X-Mbus-Http2-Ping";

    // SocketsHttpHandler's shortest ping delay and timeout.
    private static readonly TimeSpan MinHttp2Ping = TimeSpan.FromSeconds(1);

    /// <summary>Groundhog's values (<see cref="ConnectionPool.PingInterval"/>, <see cref="ConnectionPool.SilenceTimeout"/>).</summary>
    public static KeepAlive Default { get; } = new(ConnectionPool.PingInterval, ConnectionPool.SilenceTimeout);

    /// <summary>
    /// How long to wait for a pong. A ping goes out at most <see cref="PingInterval"/> after the last
    /// traffic, so waiting the rest of <see cref="SilenceTimeout"/> drops the socket after that much silence.
    /// </summary>
    internal TimeSpan PongTimeout => SilenceTimeout - PingInterval;

    /// <summary>The server's HTTP/2 ping delay: <see cref="PingInterval"/> in whole seconds, at least one.</summary>
    internal TimeSpan Http2PingDelay => TimeSpan.FromSeconds(Math.Ceiling(Max(PingInterval, MinHttp2Ping).TotalSeconds));

    /// <summary>How long the server waits for the answer to an HTTP/2 ping before giving up on the connection.</summary>
    internal TimeSpan Http2PingTimeout => Max(PongTimeout, MinHttp2Ping);

    /// <summary>
    /// How long a client promised HTTP/2 pings every <paramref name="serverPings"/> waits, hearing nothing, before it
    /// drops the connection: its <see cref="SilenceTimeout"/>, or three missed pings if that is longer.
    /// </summary>
    internal TimeSpan SilenceWindow(TimeSpan serverPings) => Max(SilenceTimeout, serverPings * 3);

    internal void Validate()
    {
        if (PingInterval <= TimeSpan.Zero || SilenceTimeout <= PingInterval)
            throw new InvalidOperationException(
                $"The ping interval ({PingInterval}) must be positive and shorter than the silence timeout ({SilenceTimeout}).");
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

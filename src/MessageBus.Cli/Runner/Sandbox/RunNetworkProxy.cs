using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// The run-network proxy (the original issue): an HTTP proxy on <c>127.0.0.1</c> that applies a run's <see cref="RunNetwork"/>. Either a
/// run's own (<see cref="RunNetwork.Start"/>), or the runner's one proxy on a port kept across restarts (the original issue,
/// <see cref="StartStable"/>), where each run's proxy address carries a credential of its own (<c>Proxy-Authorization</c>,
/// Basic) that says which run's allow-list applies. A request without a known credential gets 407 and nothing is forwarded.
/// </summary>
/// <remarks>
/// One proxy for every run, rather than one per run, so the runner listens on one loopback port that stays the same
/// across restarts.
/// </remarks>
public sealed class RunNetworkProxy : IDisposable
{
    /// <summary>Where the runner keeps its proxy's port, in the mbus settings folder.</summary>
    public const string FileName = "run-network.json";

    /// <summary>The first port the runner tries for its one proxy; it takes the first free one of <see cref="PortRangeSize"/>.</summary>
    public const int PortRangeStart = 47300;

    /// <summary>How many ports from <see cref="PortRangeStart"/> are tried before any free port is taken.</summary>
    public const int PortRangeSize = 100;

    private readonly TcpListener _listener;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _connect;
    private readonly TextWriter _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, RunNetwork> _runs = new(StringComparer.Ordinal);
    // Each open client connection, with the run it turned out to be for once its request was read (the original issue: withdrawn with it).
    private readonly ConcurrentDictionary<TcpClient, RunNetwork?> _open = new();
    private readonly UpstreamProxy? _upstream;
    private readonly Func<CancellationToken, ValueTask<TcpClient>> _accept;

    private RunNetworkProxy(TcpListener listener, TextWriter log, Func<string, int, CancellationToken, Task<Stream>>? connect, UpstreamProxy? upstream,
        Func<TcpListener, CancellationToken, ValueTask<TcpClient>>? accept = null)
    {
        _listener = listener;
        _log = log;
        _upstream = upstream;
        _connect = connect ?? ConnectAsync;
        _accept = accept is null ? listener.AcceptTcpClientAsync : ct => accept(listener, ct);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>The proxy's port on 127.0.0.1.</summary>
    public int Port { get; }

    /// <summary>Whether a runner with <paramref name="options"/> gives its runs one proxy on a kept port (Windows with the network limited, the original issue).</summary>
    public static bool UsedFor(RunIsolationOptions options) =>
        OperatingSystem.IsWindows() && options.LimitNetwork && options.Requested != IsolationLevel.Off;

    /// <summary>How many runs hold a credential on this proxy now.</summary>
    internal int Registered => _runs.Count;

    /// <summary>A run's own proxy: the one run every request is for, credential or not.</summary>
    internal RunNetwork? Only { get; set; }

    /// <summary>Starts a proxy on <paramref name="port"/> (0: any free port).</summary>
    /// <exception cref="SocketException">The port is taken.</exception>
    public static RunNetworkProxy Start(int port, TextWriter log, Func<string, int, CancellationToken, Task<Stream>>? connect = null, UpstreamProxy? upstream = null) =>
        new(Listen(port), log, connect, upstream);

    /// <summary>A proxy on any free port whose listener takes connections through <paramref name="accept"/> (tests: a failing accept).</summary>
    internal static RunNetworkProxy StartWithAccept(TextWriter log, Func<TcpListener, CancellationToken, ValueTask<TcpClient>> accept) =>
        new(Listen(0), log, connect: null, upstream: null, accept);

    /// <summary>
    /// The runner's one proxy, on the port kept in <see cref="FileName"/> in <paramref name="settingsDirectory"/>. With none kept
    /// yet, the first free port from <paramref name="rangeStart"/> is taken and kept. When the kept port is taken, another
    /// is taken, kept and logged, or, with <paramref name="moveIfTaken"/>
    /// false, null is returned.
    /// </summary>
    public static RunNetworkProxy? StartStable(string settingsDirectory, TextWriter log, UpstreamProxy? upstream = null, bool moveIfTaken = true,
        int rangeStart = PortRangeStart, Func<string, int, CancellationToken, Task<Stream>>? connect = null)
    {
        var kept = StoredPort(settingsDirectory);
        if (kept is { } port && TryListen(port) is { } listener)
        {
            return new RunNetworkProxy(listener, log, connect, upstream);
        }
        if (kept is not null && !moveIfTaken)
        {
            return null;
        }
        listener = null;
        for (var candidate = rangeStart; candidate < rangeStart + PortRangeSize && candidate <= IPEndPoint.MaxPort && listener is null; candidate++)
        {
            listener = TryListen(candidate);
        }
        listener ??= Listen(0);
        var proxy = new RunNetworkProxy(listener, log, connect, upstream);
        Store(settingsDirectory, proxy.Port, log);
        if (kept is { } old)
        {
            log.WriteLine($"Network: port {old} for runs' network proxy is taken, so runs now use port {proxy.Port}.");
        }
        return proxy;
    }

    /// <summary>The port kept in <paramref name="settingsDirectory"/>, or null.</summary>
    public static int? StoredPort(string settingsDirectory)
    {
        try
        {
            var path = Path.Combine(settingsDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("port", out var value)
                && value.TryGetInt32(out var port) && port is > 0 and <= IPEndPoint.MaxPort ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Store(string settingsDirectory, int port, TextWriter log)
    {
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            var path = Path.Combine(settingsDirectory, FileName);
            File.WriteAllText(path + ".tmp", $"{{\"port\":{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}\n");
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Network: couldn't keep the port of runs' network proxy ({ex.Message}); the next start may pick another.");
        }
    }

    private static TcpListener? TryListen(int port)
    {
        try
        {
            return Listen(port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows())
        {
            // Nothing else may listen on the same port alongside it.
            listener.ExclusiveAddressUse = true;
        }
        listener.Start();
        return listener;
    }

    /// <summary>A place for one run on this proxy, with a credential of its own; disposing it withdraws the credential.</summary>
    /// <param name="allow">What the run may reach; null: any host.</param>
    /// <param name="runName">The run (also the credential's user name).</param>
    public RunNetwork Register(HostAllowList? allow, string runName)
    {
        var network = new RunNetwork(this, ownsProxy: false, allow, runName, (runName, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))));
        _runs[runName] = network;
        return network;
    }

    /// <summary>
    /// Withdraws a run's credential and closes the run's open connections: a process the run started outside its job
    /// mustn't keep a tunnel past the run.
    /// </summary>
    internal void Withdraw(RunNetwork network)
    {
        if (network.Credential is { } c)
        {
            _runs.TryRemove(new KeyValuePair<string, RunNetwork>(c.User, network));
        }
        foreach (var (client, run) in _open)
        {
            if (ReferenceEquals(run, network))
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Whether <paramref name="run"/> may still use this proxy: its own, or its credential not yet withdrawn.</summary>
    private bool Current(RunNetwork run) =>
        ReferenceEquals(Only, run) || (run.Credential is { } c && _runs.TryGetValue(c.User, out var registered) && ReferenceEquals(registered, run));

    /// <summary>The run a request is for: its own proxy's, else the one whose credential it carries; null when none.</summary>
    private RunNetwork? RunFor(IEnumerable<string> headers)
    {
        if (Only is { } only)
        {
            return only;
        }
        foreach (var line in headers)
        {
            if (!line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = line["Proxy-Authorization:".Length..].Trim();
            if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
            }
            catch (FormatException)
            {
                return null;
            }
            var colon = decoded.IndexOf(':');
            if (colon <= 0 || !_runs.TryGetValue(decoded[..colon], out var run) || run.Credential is not { } c)
            {
                return null;
            }
            var given = Encoding.UTF8.GetBytes(decoded[(colon + 1)..]);
            return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(c.Token)) ? run : null;
        }
        return null;
    }

    /// <summary>
    /// Takes connections until the proxy is disposed. An error taking one (Windows reports a connection its client reset
    /// before it was taken, or a brief lack of buffers, as a failed accept) never ends the loop: the listener would keep
    /// its port while nothing took its connections, so every run's requests would be refused until the runner restarted.
    /// </summary>
    private async Task AcceptAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _accept(_stop.Token).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }
                if (failures++ == 0)
                {
                    _log.WriteLine($"Network: runs' network proxy couldn't take a connection ({ex.SocketErrorCode}: {ex.Message}); it keeps taking connections.");
                    continue;
                }
                // Failing again straight away: wait a little longer each time (up to 2 s) rather than spin.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100 * failures, 2000)), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            _open[client] = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(client, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
                finally
                {
                    _open.TryRemove(client, out _);
                    client.Dispose();
                }
            });
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var (head, rest) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        if (head is null)
        {
            await RespondAsync(stream, 400, "Bad Request", "mbus: that isn't an HTTP proxy request.", ct).ConfigureAwait(false);
            return;
        }
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3)
        {
            await RespondAsync(stream, 400, "Bad Request", "mbus: that isn't an HTTP proxy request.", ct).ConfigureAwait(false);
            return;
        }
        if (RunFor(lines.Skip(1)) is not { } run)
        {
            // The challenge lets clients that send their credential only when asked (.NET) send it.
            await RespondAsync(stream, 407, "Proxy Authentication Required",
                "mbus: this proxy is for runs on this computer; use the proxy address the run was given.", ct, "Proxy-Authenticate: Basic realm=\"mbus\"\r\n").ConfigureAwait(false);
            return;
        }
        _open[client] = run;
        if (!Current(run))
        {
            return; // withdrawn meanwhile
        }
        var (method, target) = (parts[0], parts[1]);
        var tunnel = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        string host;
        int port;
        string forward = "";
        Uri? via = null;
        BodyFraming body = default;
        if (tunnel)
        {
            if (!TrySplitHostPort(target, out host, out port))
            {
                await RespondAsync(stream, 400, "Bad Request", "mbus: CONNECT needs host:port.", ct).ConfigureAwait(false);
                return;
            }
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
        {
            (host, port) = (uri.Host, uri.Port);
            if (BodyFraming.Of(lines.Skip(1)) is not { } framing)
            {
                await RespondAsync(stream, 400, "Bad Request", "mbus: the request's length isn't clear (Content-Length and Transfer-Encoding).", ct).ConfigureAwait(false);
                return;
            }
            body = framing;
            via = _upstream?.For(host, tunnel: false);
            // To the server: the path alone (to an upstream proxy: the whole URL and its credential), without the
            // run's proxy headers (its credential included), one request per connection: only this request and its
            // body go on (anything the client sent after it, another request's credential included, never does).
            var headers = lines.Skip(1).Where(l => l.Length > 0 && !IsHopByHop(l));
            forward = $"{method} {(via is null ? uri.PathAndQuery : uri.AbsoluteUri)} {parts[2]}\r\n" + string.Join("\r\n", headers)
                + "\r\n" + (via is null ? "" : UpstreamProxy.AuthorizationLine(via)) + "Connection: close\r\n\r\n";
        }
        else
        {
            await RespondAsync(stream, 400, "Bad Request", "mbus: this proxy takes CONNECT and http:// requests only.", ct).ConfigureAwait(false);
            return;
        }

        if (run.Allowed is { } allow && !allow.Allows(host))
        {
            if (run.Refuse(host))
            {
                await _log.WriteLineAsync($"{run.RunName}: refused a connection to {host}: it isn't on the run's network allow-list.").ConfigureAwait(false);
            }
            await RespondAsync(stream, 403, "Forbidden",
                $"mbus: {host} isn't on this task's network allow-list. The role's network setting, or MBUS_RUNNER_ALLOW_HOSTS on the runner, can add it.", ct).ConfigureAwait(false);
            return;
        }

        if (tunnel)
        {
            via = _upstream?.For(host, tunnel: true);
        }
        Stream upstream;
        try
        {
            upstream = via is null
                ? await _connect(host, port, ct).ConfigureAwait(false)
                : await _connect(via.Host, via.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            var through = via is null ? "" : $" through the runner's proxy {via.Host}:{via.Port}";
            await RespondAsync(stream, 502, "Bad Gateway", $"mbus: couldn't connect to {host}:{port}{through} ({ex.Message}).", ct).ConfigureAwait(false);
            return;
        }
        if (via is not null && tunnel)
        {
            try
            {
                await OpenTunnelAsync(upstream, via, host, port, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await upstream.DisposeAsync().ConfigureAwait(false);
                await RespondAsync(stream, 502, "Bad Gateway", $"mbus: couldn't reach {host}:{port} through the runner's proxy {via.Host}:{via.Port} ({ex.Message}).", ct).ConfigureAwait(false);
                return;
            }
        }
        await using (upstream.ConfigureAwait(false))
        {
            using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!tunnel)
            {
                // One request and its response, then the connection closes.
                var request = ForwardOneAsync(new FramedReader(rest, stream), upstream, forward, body, done.Token);
                var response = ForwardResponseAsync(new FramedReader([], upstream), stream, done.Token);
                var first = await Task.WhenAny(request, response).ConfigureAwait(false);
                if (first == response || request.IsCompletedSuccessfully)
                {
                    await response.ConfigureAwait(false);
                }
                await done.CancelAsync().ConfigureAwait(false);
                await first.ConfigureAwait(false);
                return;
            }
            // A tunnel: the bytes both ways, as they come.
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            if (rest.Length > 0)
            {
                await upstream.WriteAsync(rest, ct).ConfigureAwait(false);
            }
            var up = stream.CopyToAsync(upstream, done.Token);
            var down = upstream.CopyToAsync(stream, done.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            await done.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Headers for this connection alone (the run's proxy credential among them), which never go on.</summary>
    private static bool IsHopByHop(string line) =>
        line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends the request's head, then exactly its body as framed; nothing the client sends after that is read.</summary>
    private static async Task ForwardOneAsync(FramedReader client, Stream upstream, string head, BodyFraming body, CancellationToken ct)
    {
        await upstream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        if (body.Chunked)
        {
            while (true)
            {
                var line = await client.ReadLineAsync(ct).ConfigureAwait(false);
                var size = line.Split(';', 2)[0].Trim();
                if (!long.TryParse(size, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
                {
                    throw new IOException("a malformed chunk");
                }
                await upstream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct).ConfigureAwait(false);
                if (length == 0)
                {
                    // Trailers, up to the blank line.
                    string trailer;
                    do
                    {
                        trailer = await client.ReadLineAsync(ct).ConfigureAwait(false);
                        await upstream.WriteAsync(Encoding.ASCII.GetBytes(trailer + "\r\n"), ct).ConfigureAwait(false);
                    }
                    while (trailer.Length > 0);
                    break;
                }
                await client.CopyAsync(upstream, length, ct).ConfigureAwait(false);
                if ((await client.ReadLineAsync(ct).ConfigureAwait(false)).Length != 0)
                {
                    throw new IOException("a malformed chunk");
                }
                await upstream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            }
        }
        else if (body.Length > 0)
        {
            await client.CopyAsync(upstream, body.Length, ct).ConfigureAwait(false);
        }
        await upstream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The server's response, its interim (1xx) heads as they are and its final head saying the connection closes; then the rest as it comes.</summary>
    private static async Task ForwardResponseAsync(FramedReader upstream, Stream client, CancellationToken ct)
    {
        while (true)
        {
            var head = new List<string>();
            for (var size = 0; ; )
            {
                var line = await upstream.ReadLineAsync(ct).ConfigureAwait(false);
                if (line.Length == 0)
                {
                    break;
                }
                size += line.Length;
                if (size > 32 * 1024)
                {
                    throw new IOException("the response head is too long");
                }
                head.Add(line);
            }
            if (head.Count == 0)
            {
                throw new IOException("no response");
            }
            var status = head[0].Split(' ');
            var interim = status.Length > 1 && status[1].Length == 3 && status[1][0] == '1' && status[1] != "101";
            var lines = interim ? head : [.. head.Where(l => !IsHopByHop(l)), "Connection: close"];
            await client.WriteAsync(Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n"), ct).ConfigureAwait(false);
            if (!interim)
            {
                break;
            }
        }
        await upstream.CopyToEndAsync(client, ct).ConfigureAwait(false);
    }

    /// <summary>How a request's body is framed: by <c>Content-Length</c>, chunked, or none.</summary>
    private readonly record struct BodyFraming(long Length, bool Chunked)
    {
        /// <summary>The framing the headers give; null when it isn't clear (both, conflicting lengths, or another coding last).</summary>
        public static BodyFraming? Of(IEnumerable<string> headers)
        {
            string? Value(string line, string name) =>
                line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? line[(name.Length + 1)..].Trim() : null;
            var lengths = new List<string>();
            var codings = new List<string>();
            foreach (var line in headers)
            {
                if (Value(line, "Content-Length") is { } length)
                {
                    lengths.AddRange(length.Split(',', StringSplitOptions.TrimEntries));
                }
                else if (Value(line, "Transfer-Encoding") is { } coding)
                {
                    codings.AddRange(coding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                }
            }
            if (codings.Count > 0)
            {
                return lengths.Count == 0 && codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase) ? new BodyFraming(0, true) : null;
            }
            if (lengths.Count == 0)
            {
                return new BodyFraming(0, false);
            }
            return lengths.Distinct(StringComparer.Ordinal).Count() == 1
                && long.TryParse(lengths[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? new BodyFraming(n, false) : null;
        }
    }

    /// <summary>Reads a connection: bytes already read first, then the stream; by line or by count.</summary>
    private sealed class FramedReader(byte[] first, Stream stream)
    {
        private readonly byte[] _buffer = new byte[Math.Max(16 * 1024, first.Length)];
        private int _start;
        private int _end;
        private byte[]? _first = first.Length > 0 ? first : null;

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (_start < _end)
            {
                return true;
            }
            if (_first is { } bytes)
            {
                _first = null;
                bytes.CopyTo(_buffer, 0);
                (_start, _end) = (0, bytes.Length);
                return true;
            }
            (_start, _end) = (0, await stream.ReadAsync(_buffer, ct).ConfigureAwait(false));
            return _end > 0;
        }

        /// <summary>One line, without its CRLF.</summary>
        /// <exception cref="IOException">The connection ended first, or the line is longer than 8 KB.</exception>
        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new IOException("the connection closed");
                }
                var at = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                var stop = at < 0 ? _end : at;
                line.Append(Encoding.ASCII.GetString(_buffer, _start, stop - _start));
                _start = at < 0 ? _end : at + 1;
                if (line.Length > 8 * 1024)
                {
                    throw new IOException("a line is too long");
                }
                if (at >= 0)
                {
                    return line.ToString().TrimEnd('\r');
                }
            }
        }

        /// <summary>Exactly <paramref name="count"/> bytes to <paramref name="to"/>.</summary>
        public async Task CopyAsync(Stream to, long count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new IOException("the connection closed");
                }
                var take = (int)Math.Min(count, _end - _start);
                await to.WriteAsync(_buffer.AsMemory(_start, take), ct).ConfigureAwait(false);
                _start += take;
                count -= take;
            }
        }

        /// <summary>Everything to the end of the connection.</summary>
        public async Task CopyToEndAsync(Stream to, CancellationToken ct)
        {
            while (await FillAsync(ct).ConfigureAwait(false))
            {
                await to.WriteAsync(_buffer.AsMemory(_start, _end - _start), ct).ConfigureAwait(false);
                _start = _end;
            }
        }
    }

    /// <summary>
    /// Asks the upstream proxy for a tunnel to <paramref name="host"/> (the original issue). Reads its answer a byte at a time, so
    /// nothing the far end sends first (an SSH server's greeting) is taken from the tunnel.
    /// </summary>
    /// <exception cref="IOException">The proxy refused or closed the connection.</exception>
    private static async Task OpenTunnelAsync(Stream proxy, Uri via, string host, int port, CancellationToken ct)
    {
        var target = (host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await proxy.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{UpstreamProxy.AuthorizationLine(via)}\r\n"), ct).ConfigureAwait(false);
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (head.Length > 16 * 1024 || await proxy.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                throw new IOException("the proxy closed the connection");
            }
            head.Append((char)one[0]);
        }
        var status = head.ToString().Split("\r\n")[0];
        if (status.Split(' ') is not [_, ['2', ..], ..])
        {
            throw new IOException($"the proxy answered {status}");
        }
    }

    /// <summary>The request head (up to the blank line) and any bytes read after it; null head when there is no complete one within 32 KB.</summary>
    private static async Task<(string? Head, byte[] After)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return (null, []);
            }
            filled += read;
            var end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..filled]);
            }
        }
        return (null, []);
    }

    private static bool TrySplitHostPort(string target, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }
        host = target[..colon].Trim('[', ']');
        return host.Length > 0;
    }

    private static async Task RespondAsync(Stream stream, int status, string reason, string message, CancellationToken ct, string extraHeaders = "")
    {
        var body = Encoding.UTF8.GetBytes(message + "\n");
        var head = $"HTTP/1.1 {status} {reason}\r\n{extraHeaders}Content-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new OwningStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _open.Keys)
        {
            client.Dispose();
        }
        _runs.Clear();
        _stop.Dispose();
    }

    /// <summary>A TCP connection's stream that closes the connection with it.</summary>
    private sealed class OwningStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _inner = tcp.GetStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                tcp.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

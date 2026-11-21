using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace MessageBus.Cli.Tests.Infrastructure;

// A private CA and real TLS endpoint. No certificate-store, environment, hosts, or proxy changes.
// Every connection is owned, cancellation-aware, and joined at disposal, including upgraded sockets.
internal sealed class IsolatedHttpsServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string RawTarget, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _leaf;
    private readonly Task _serving;
    private readonly ConcurrentQueue<Task> _connections = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _sockets = new();
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal Func<Request, SslStream, CancellationToken, Task>? Respond { get; set; }
    internal string Origin { get; }
    internal string Selected => Origin + "/gateway%20scope";
    private int _stripped;
    internal bool Stripped { get => Volatile.Read(ref _stripped) != 0; set => Volatile.Write(ref _stripped, value ? 1 : 0); }
    internal bool ExpectTlsRejection { get; set; }
    internal string Discovery => Stripped ? """{"product":"messagebus.ai","signIn":{"microsoft":true}}"""
        : $$$$"""{"product":"messagebus.ai","stamp":{"stampId":"fixture","geography":"eu","azureRegion":"westeurope","url":"{{{{Origin}}}}","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"fixture","issuer":"{{{{Origin}}}}","audience":"{{{{Origin}}}}"},"machineSignature":{"version":2,"required":true}}}""";

    internal IsolatedHttpsServer(CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Wall-clock timeout is a deadlock guard only; tests advance a fake clock for backoff.
        _stop.CancelAfter(TimeSpan.FromSeconds(20));
        using var caKey = RSA.Create(2048);
        var ca = new CertificateRequest("CN=Fixture private root", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var now = DateTimeOffset.UtcNow;
        _root = ca.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leaf = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); // Deliberately excludes 127.0.0.1 for the hostname-rejection control.
        leaf.CertificateExtensions.Add(san.Build());
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var issued = leaf.Create(_root, now.AddHours(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(leafKey);
        try
        {
            _leaf = ServerCertificate(withKey);
            _listener.Start();
            Origin = $"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serving = AcceptAsync();
        }
        catch
        {
            _listener.Stop();
            _leaf?.Dispose();
            _root.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    private static X509Certificate2 ServerCertificate(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return new X509Certificate2(certificate);
        // Schannel cannot acquire an ephemeral CopyWithPrivateKey key. Import a temporary
        // user key container instead: no certificate-store installation or PersistKeySet.
        // .NET owns deleting that container when _leaf is disposed after all TLS sockets.
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal X509ChainPolicy ChainPolicy(bool trustRoot = true)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.NoFlag,
            DisableCertificateDownloads = true,
        };
        if (trustRoot) policy.CustomTrustStore.Add(_root);
        return policy;
    }

    internal SocketsHttpHandler Handler() => new()
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
        SslOptions = new() { CertificateChainPolicy = ChainPolicy() },
    };

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sockets.TryAdd(socket, 0);
                _connections.Enqueue(ServeAsync(socket));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient socket)
    {
        using (socket)
        await using (var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false))
        {
            var receivedHttp = false;
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _leaf,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, _stop.Token);
                var request = await ReadAsync(stream, _stop.Token);
                if (request is null) return; // A client rejecting trust/name may close immediately after TLS.
                receivedHttp = true;
                Assert.Equal(new Uri(Origin).Authority, request.Header("Host"));
                Requests.Enqueue(request);
                if (request.RawTarget == "/gateway%20scope/api/server")
                {
                    AssertCredentialFree(request);
                    await ReplyAsync(stream, Encoding.UTF8.GetBytes(Discovery), _stop.Token);
                }
                else
                    await (Respond ?? throw new InvalidOperationException("Unexpected fixture request."))(request, stream, _stop.Token);
            }
            catch (System.Security.Authentication.AuthenticationException) when (ExpectTlsRejection && !receivedHttp) { }
            catch (IOException) when (ExpectTlsRejection && !receivedHttp) { }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            finally { _sockets.TryRemove(socket, out _); }
        }
    }

    private static async Task<Request?> ReadAsync(Stream stream, CancellationToken ct)
    {
        // Do not buffer beyond the headers: the remaining bytes may belong to a WebSocket.
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                Assert.Empty(bytes);
                return null;
            }
            bytes.Add(one[0]);
            Assert.True(bytes.Count <= 32 * 1024, "Fixture request headers exceeded the bound.");
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        Assert.Equal("HTTP/1.1", first[2]);
        var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        Assert.False(headers.ContainsKey("Transfer-Encoding"));
        var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text, CultureInfo.InvariantCulture) : 0;
        Assert.InRange(length, 0, 1024 * 1024);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return new(first[0], first[1], headers, body);
    }

    internal static void AssertCredentialFree(Request request) => Assert.DoesNotContain(request.Headers.Keys,
        h => h.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase)
            || h.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || h.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase));

    internal static async Task ReplyAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
    }

    internal static async Task UpgradeAsync(Request request, Stream stream, CancellationToken ct)
    {
        Assert.Equal("GET", request.Method);
        Assert.Equal("websocket", request.Header("Upgrade"), ignoreCase: true);
        Assert.Equal("13", request.Header("Sec-WebSocket-Version"));
        Assert.Equal("mbus.v1", request.Header("Sec-WebSocket-Protocol"));
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Header("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: mbus.v1\r\n\r\n"), ct);
        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: "mbus.v1", keepAliveInterval: Timeout.InfiniteTimeSpan);
        try
        {
            var buffer = new byte[4096];
            while ((await socket.ReceiveAsync(buffer.AsMemory(), ct)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (WebSocketException) { /* The journey deliberately drops both upgraded connections. */ }
        catch (IOException) { /* The peer aborts its TLS connection. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _serving;
            foreach (var socket in _sockets.Keys) socket.Dispose();
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _leaf.Dispose(); _root.Dispose(); _stop.Dispose(); }
    }
}

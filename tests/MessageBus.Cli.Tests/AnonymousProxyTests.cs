using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class AnonymousProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> LoginChallenges()
    {
        foreach (var dedicated in new[] { false, true })
        foreach (var clientHandler in new[] { false, true })
        foreach (var scheme in new[] { "http", "https" })
        foreach (var challenge in new[] { "Basic realm=\"fixture\"", "Negotiate", "NTLM" })
            yield return [dedicated, clientHandler, scheme, challenge];
    }

    [Theory]
    [MemberData(nameof(LoginChallenges))]
    public async Task Initial_login_discovery_cannot_answer_proxy_challenges(bool dedicated, bool clientHandler,
        string scheme, string challenge)
    {
        await using var proxy = new ProxyServer(challenge);
        var basic = challenge.StartsWith("Basic", StringComparison.Ordinal);
        var address = basic ? new UriBuilder(proxy.Address) { UserName = "fixture-user", Password = "fixture-password" }.Uri : proxy.Address;
        var routing = new RoutingProxy(address, basic
            ? new NetworkCredential("fixture-user", "fixture-password") : CredentialCache.DefaultNetworkCredentials);
        HttpMessageHandler Handler() => clientHandler
            ? new HttpClientHandler { Proxy = routing, UseDefaultCredentials = true, DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials }
            : new SocketsHttpHandler { Proxy = routing, Credentials = CredentialCache.DefaultNetworkCredentials,
                DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials };
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var apiFactories = 0;
        machine.ApiHttpHandler = () => { apiFactories++; return Handler(); };
        machine.DiscoveryHttpHandler = dedicated ? Handler : null;
        var result = await machine.RunAsync("login", "--server", $"{scheme}://anonymous.example.test/gateway", "--token", "fixture-enroll");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(dedicated ? 0 : 1, apiFactories);
        var request = Assert.Single(proxy.Requests);
        Assert.StartsWith(scheme == "https" ? "CONNECT anonymous.example.test:443 "
            : "GET http://anonymous.example.test/gateway/api/server ", request[0]);
        Assert.DoesNotContain(request, line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, routing.CredentialReads);
        Assert.Equal(0, routing.CredentialWrites);
        Assert.True(routing.RouteReads > 0);
        Assert.Empty(machine.Server.Requests);
        Assert.False(new ConfigStore(machine.MbusFolder, "file").HasKey);
    }

    public static IEnumerable<object[]> Challenges()
    {
        foreach (var factory in new[] { "release", "attachment", "discovery-sockets-default", "discovery-sockets-explicit",
                     "discovery-client-default", "discovery-client-explicit" })
        foreach (var scheme in new[] { "http", "https" })
        foreach (var challenge in new[] { "Basic realm=\"fixture\"", "Negotiate", "NTLM" })
            yield return [factory, scheme, challenge];
    }

    [Theory]
    [MemberData(nameof(Challenges))]
    public async Task Proxy_407_cannot_acquire_inherited_or_integrated_credentials(string factory, string scheme, string challenge)
    {
        await using var server = new ProxyServer(challenge);
        var proxyAddress = challenge.StartsWith("Basic", StringComparison.Ordinal)
            ? new UriBuilder(server.Address) { UserName = "fixture-user", Password = "fixture-password" }.Uri
            : server.Address;
        var routing = new RoutingProxy(proxyAddress, challenge.StartsWith("Basic", StringComparison.Ordinal)
            ? new NetworkCredential("fixture-user", "fixture-password") : CredentialCache.DefaultNetworkCredentials);
        using var client = CreateClient(factory, routing);
        var destination = new Uri($"{scheme}://anonymous.example.test/artifact");
        if (scheme == "https")
        {
            // A rejected CONNECT never starts TLS or contacts the destination.
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(destination, Ct));
            Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, error.StatusCode);
        }
        else
        {
            using var response = await client.GetAsync(destination, Ct);
            Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        }

        var request = Assert.Single(server.Requests);
        Assert.StartsWith(scheme == "https" ? "CONNECT anonymous.example.test:443 " : "GET http://anonymous.example.test/artifact ", request[0]);
        Assert.DoesNotContain(request, line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, routing.CredentialReads);
        Assert.Equal(0, routing.CredentialWrites);
        Assert.True(routing.RouteReads > 0);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("attachment")]
    [InlineData("discovery-sockets-default")]
    [InlineData("discovery-sockets-explicit")]
    [InlineData("discovery-client-default")]
    [InlineData("discovery-client-explicit")]
    public async Task Credential_free_proxy_route_still_delivers_the_response(string factory)
    {
        await using var server = new ProxyServer(null);
        var routing = new RoutingProxy(server.Address, new NetworkCredential("fixture-user", "fixture-password"));
        using var client = CreateClient(factory, routing);
        Assert.Equal(ProxyServer.Body, await client.GetStringAsync("http://anonymous.example.test/artifact", Ct));
        Assert.Single(server.Requests);
        Assert.Equal(0, routing.CredentialReads);
        Assert.Equal(0, routing.CredentialWrites);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_send_gate_applies_anonymous_proxy_policy(bool clientHandler, bool challenge)
    {
        await using var server = new ProxyServer(challenge ? "Basic realm=\"fixture\"" : null);
        var routing = new RoutingProxy(server.Address, new NetworkCredential("fixture-user", "fixture-password"));
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder);
        using var gate = new ServerSendGate(store, new Uri("http://anonymous.example.test/"),
            () => clientHandler ? new HttpClientHandler { Proxy = routing, UseDefaultCredentials = true }
                : new SocketsHttpHandler { Proxy = routing, Credentials = CredentialCache.DefaultNetworkCredentials }, _ => null);
        if (challenge)
            await Assert.ThrowsAsync<ServerBindingException>(() => gate.PrepareSendAsync(new Uri("http://anonymous.example.test/api/messages"), Ct));
        else
            await gate.PrepareSendAsync(new Uri("http://anonymous.example.test/api/messages"), Ct);
        Assert.Single(server.Requests);
        Assert.Equal(0, routing.CredentialReads);
        Assert.Equal(0, routing.CredentialWrites);
    }

    [Fact]
    public void Authenticated_API_retains_its_explicit_proxy_and_disabled_proxy_is_respected()
    {
        var routing = new RoutingProxy(new Uri("http://127.0.0.1:1"), CredentialCache.DefaultNetworkCredentials);
        using var api = new SocketsHttpHandler { Proxy = routing };
        ServerHttpTransport.Restrict(api);
        Assert.Same(routing, api.Proxy);
        using var anonymous = new HttpClientHandler { UseProxy = false, Proxy = routing };
        ServerHttpTransport.Restrict(anonymous, anonymous: true, defaultProxy: () => throw new InvalidOperationException());
        Assert.False(anonymous.UseProxy);
        Assert.Same(routing, anonymous.Proxy);
    }

    [Fact]
    public async Task Anonymous_proxy_preserves_bypass_without_querying_credentials()
    {
        // The destination itself is the local server: IsBypassed must prevent GetProxy from being used.
        await using var destination = new ProxyServer(null);
        var routing = new RoutingProxy(new Uri("http://127.0.0.1:1"), CredentialCache.DefaultNetworkCredentials, bypass: true);
        using var client = HttpReleaseSource.CreateHttpClient(() => routing);
        Assert.Equal(ProxyServer.Body, await client.GetStringAsync(destination.Address, Ct));
        Assert.StartsWith("GET / ", Assert.Single(destination.Requests)[0]);
        Assert.Equal(0, routing.RouteReads);
        Assert.Equal(0, routing.CredentialReads);
    }

    private static HttpClient CreateClient(string factory, IWebProxy routing)
    {
        if (factory == "release") return HttpReleaseSource.CreateHttpClient(() => routing);
        if (factory == "attachment") return AttachmentTransfers.CreateHttpClient(() => routing);
        var explicitProxy = factory.EndsWith("explicit", StringComparison.Ordinal);
        HttpMessageHandler handler = factory.StartsWith("discovery-sockets", StringComparison.Ordinal)
            ? new SocketsHttpHandler { Proxy = explicitProxy ? routing : null, DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials }
            : new HttpClientHandler { Proxy = explicitProxy ? routing : null, DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials };
        return new HttpClient(ServerHttpTransport.Restrict(handler, anonymous: true,
            defaultProxy: () => explicitProxy ? throw new InvalidOperationException("Explicit route must win.") : routing));
    }

    private sealed class RoutingProxy(Uri address, ICredentials credentials, bool bypass = false) : IWebProxy
    {
        public int CredentialReads { get; private set; }
        public int CredentialWrites { get; private set; }
        public int RouteReads { get; private set; }
        public ICredentials? Credentials
        {
            get { CredentialReads++; return credentials; }
            set { CredentialWrites++; throw new InvalidOperationException("Shared proxy must never be mutated."); }
        }
        public Uri GetProxy(Uri destination) { RouteReads++; return address; }
        public bool IsBypassed(Uri host) => bypass;
    }

    // No global proxy/environment changes, TLS trust changes, real-time sleeps or polling.
    // The proxy accepts every connection until the client's completed request establishes no retry remains.
    private sealed class ProxyServer : IAsyncDisposable
    {
        internal const string Body = """{"product":"messagebus.ai","signIn":{"microsoft":true}}""";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Task _serving;
        private readonly string? _challenge;
        internal Uri Address { get; }
        internal ConcurrentQueue<string[]> Requests { get; } = new();

        internal ProxyServer(string? challenge)
        {
            _challenge = challenge;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var lines = new List<string>();
                for (var line = await reader.ReadLineAsync(_stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(_stop.Token))
                    lines.Add(line);
                if (lines.Count == 0) throw new IOException("Missing proxy request.");
                Requests.Enqueue(lines.ToArray());
                var body = _challenge is null ? Body : "";
                var headers = _challenge is null ? "HTTP/1.1 200 OK\r\n" : $"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: {_challenge}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body), _stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serving; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }
}

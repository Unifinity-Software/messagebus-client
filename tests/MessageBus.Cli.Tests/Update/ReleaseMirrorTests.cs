using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Releases;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class ReleaseMirrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("environment", "/environment/stable/")]
    [InlineData("workspace", "/workspace/stable/")]
    [InlineData("default", "/gateway/api/releases/stable/")]
    public async Task Real_factory_selects_feed_precedence_and_verifies_signed_bytes_without_profile_credentials(string selection, string prefix)
    {
        using var feed = new TestReleaseFeed();
        var release = feed.Publish("99.0.0");
        feed.InstallCurrent();
        await using var server = new PublicServer(path => (200, null, File.ReadAllBytes(Path.Combine(feed.ChannelFolder, Path.GetFileName(path)))));
        using var machine = new TestMachine();
        machine.ReleaseHttpClient = HttpReleaseSource.CreateHttpClient;
        machine.Env[ReleaseKey.EnvVar] = feed.PublicKey;
        machine.Env[UpdateEnvVars.Target] = feed.Target;
        machine.Env[EnvVars.ApiKey] = "fixture-api-key";
        if (selection == "environment") machine.Env[UpdateEnvVars.Feed] = server.Origin + "/environment";
        var settings = new CliSettings
        {
            Server = new Uri(server.Origin + "/gateway"), MachineName = "fixture",
            ApiKey = "fixture-api-key", Stored = new CliConfig
            { Server = "https://saved.example.test", MachineId = "mch_saved", WorkspaceId = "ws_saved" },
        };
        // In mirror cases the selected API is a different origin, and must never be probed.
        if (selection != "default") settings = settings with { Server = new Uri("https://selected.example.test/gateway") };
        using var updater = UpdateSetup.Create(machine.CreateHost(), settings, null, TextWriter.Null,
            mirror: selection == "default" ? null : server.Origin + "/workspace");
        Assert.Equal(UpdateResult.Installed, await updater.InstallAsync(allowDowngrade: false, Ct));
        Assert.Equal(release, File.ReadAllText(feed.Target));
        Assert.Equal(prefix + ReleaseFeed.ManifestFile, server.Paths[0]);
        Assert.Equal(prefix + ReleaseFeed.SignatureFile, server.Paths[1]);
        Assert.Equal(3, server.Paths.Count);
        Assert.StartsWith(prefix + "mbus-99.0.0-", server.Paths[2]);
        Assert.Empty(machine.Server.Requests); // No API/discovery pipeline, even with saved foreign credentials.
        Assert.All(server.Headers, headers => Assert.DoesNotContain(headers, h =>
            h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("payload")]
    [InlineData("size")]
    [InlineData("pinned-version")]
    public async Task Actual_http_mirror_cannot_install_tampered_or_wrong_version_release(string tampering)
    {
        using var feed = new TestReleaseFeed();
        feed.Publish("99.0.0");
        if (tampering == "signature") feed.EditManifest(s => s.Replace("99.0.0", "98.0.0", StringComparison.Ordinal));
        if (tampering == "payload") feed.EditBinary("99.0.0");
        if (tampering == "pinned-version") feed.CopyVersionManifest("99.0.0", "98.0.0");
        await using var server = new PublicServer(path =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(feed.ChannelFolder, Path.GetFileName(path)));
            if (tampering == "size" && !path.Contains("manifest", StringComparison.Ordinal)) bytes = [.. bytes, 0];
            return (200, null, bytes);
        });
        using var machine = new TestMachine();
        machine.ReleaseHttpClient = HttpReleaseSource.CreateHttpClient;
        machine.Env[UpdateEnvVars.Feed] = server.Origin;
        machine.Env[UpdateEnvVars.Target] = feed.Target;
        machine.Env[ReleaseKey.EnvVar] = feed.PublicKey;
        var installed = feed.InstallCurrent();
        var before = File.ReadAllBytes(installed);
        var result = tampering == "pinned-version"
            ? await machine.RunAsync("update", "--version", "98.0.0")
            : await machine.RunAsync("update");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(installed));
        Assert.Empty(feed.Versions());
        Assert.Empty(feed.Leftovers());
        Assert.True(server.Paths.Count >= 2); // The real signed metadata path was reached.
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Real_public_factory_never_follows_mirror_redirects(int status)
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var foreign = $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        await using var server = new PublicServer(_ => (status, foreign, []));
        using var source = new HttpReleaseSource(HttpReleaseSource.CreateHttpClient(), new Uri(server.Origin + "/stable/"));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.OpenAsync(ReleaseFeed.ManifestFile, Ct));
        Assert.Single(server.Paths);
        Assert.False(target.Pending());
    }

    [Fact]
    public async Task Verified_manifest_does_not_authorize_a_payload_redirect()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var foreign = $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        using var feed = new TestReleaseFeed();
        feed.Publish("99.0.0");
        feed.InstallCurrent();
        var before = File.ReadAllBytes(feed.Target);
        await using var server = new PublicServer(path => path.Contains("manifest", StringComparison.Ordinal)
            ? (200, null, File.ReadAllBytes(Path.Combine(feed.ChannelFolder, Path.GetFileName(path))))
            : (307, foreign, []));
        using var machine = new TestMachine();
        machine.ReleaseHttpClient = HttpReleaseSource.CreateHttpClient;
        machine.Env[UpdateEnvVars.Feed] = server.Origin;
        machine.Env[UpdateEnvVars.Target] = feed.Target;
        machine.Env[ReleaseKey.EnvVar] = feed.PublicKey;
        var result = await machine.RunAsync("update");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(3, server.Paths.Count); // Both signature files were accepted before the payload request.
        Assert.False(target.Pending());
        Assert.Equal(before, File.ReadAllBytes(feed.Target));
        Assert.Empty(feed.Versions());
        Assert.Empty(feed.Leftovers());
    }

    [Theory]
    [InlineData("https://name:password@mirror.example.test/")]
    [InlineData("https://mirror.example.test/?token=fixture")]
    [InlineData("https://mirror.example.test/#fragment")]
    public async Task Unsafe_mirror_urls_refuse_before_any_network_contact(string url)
    {
        var contacted = false;
        using var client = HttpReleaseSource.CreateTestHttpClient(new Terminal(_ =>
        { contacted = true; return new(HttpStatusCode.OK); }));
        using var source = new HttpReleaseSource(client, new Uri(url));
        await Assert.ThrowsAsync<CliException>(() => source.OpenAsync(ReleaseFeed.ManifestFile, Ct));
        Assert.False(contacted);
    }

    [Fact]
    public async Task Unknown_clients_and_mutated_defaults_cannot_be_used_as_public_feed_transports()
    {
        var contacted = false;
        using var unknown = new HttpClient(new Terminal(_ => { contacted = true; return new(HttpStatusCode.OK); }));
        using var source = new HttpReleaseSource(unknown, new Uri("https://mirror.example.test/stable/"));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => source.OpenAsync(ReleaseFeed.ManifestFile, Ct));
        using var safe = HttpReleaseSource.CreateTestHttpClient(new Terminal(_ => { contacted = true; return new(HttpStatusCode.OK); }));
        safe.DefaultRequestHeaders.Add("X-Mbus-Workspace", "ws_fixture");
        using var altered = new HttpReleaseSource(safe, new Uri("https://mirror.example.test/stable/"));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => altered.OpenAsync(ReleaseFeed.ManifestFile, Ct));
        Assert.False(contacted);
    }

    private sealed class Terminal(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class PublicServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly Task _serving;
        private readonly Func<string, (int Status, string? Location, byte[] Body)> _respond;
        public string Origin { get; }
        public List<string> Paths { get; } = [];
        public List<List<string>> Headers { get; } = [];
        public PublicServer(Func<string, (int Status, string? Location, byte[] Body)> respond)
        {
            _respond = respond;
            _listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serving = ServeAsync();
        }
        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing request line.");
                var path = first.Split(' ')[1];
                var headers = new List<string>();
                for (var line = await reader.ReadLineAsync(_stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(_stop.Token)) headers.Add(line);
                Paths.Add(path);
                Headers.Add(headers);
                var (status, location, body) = _respond(path);
                var head = $"HTTP/1.1 {status} Result\r\nContent-Length: {body.Length}\r\nConnection: close\r\n"
                    + (location is null ? "" : $"Location: {location}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
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

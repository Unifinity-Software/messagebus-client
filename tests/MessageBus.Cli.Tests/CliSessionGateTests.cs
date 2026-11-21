using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Cli.SignIn;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class CliSessionGateTests
{
    private const string Origin = "https://eu.stamps.example.test";
    private const string Endpoint = Origin + "/gateway";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"cliApprovalUrl":"https://eu.stamps.example.test/cli-login"}}""";
    private const string Isolated = """{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":["https://alias.stamps.example.test","https://other.stamps.example.test"]},"signIn":{"cliApprovalUrl":"https://eu.stamps.example.test/cli-login","authority":{"version":1,"stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test"},"machineSignature":{"version":2,"required":true}}}""";
    private const string Approved = """{"state":"approved","accessToken":"fixture-approved-access","refreshToken":"fixture-approved-refresh","expiresIn":3600}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_cached_access_requires_current_binding_including_after_restart()
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        Assert.Equal("fixture-access", await fixture.Session.AccessTokenAsync(null, Ct));
        var snapshot = fixture.Store.LoadSession()!.ServerBinding;
        Assert.Equal(Endpoint + "/", snapshot!.SelectedEndpoint);
        fixture.Discovery = Legacy;
        var restarted = new CliSession(fixture.Host, new ConfigStore(fixture.Store.Directory, "file"), new Uri(Endpoint));
        await Assert.ThrowsAsync<ServerBindingException>(() => restarted.AccessTokenAsync(null, Ct));
        await Assert.ThrowsAsync<ServerBindingException>(() => restarted.SendAsync(ServerConnection.WorkspacesRequest, Ct));
        Assert.Empty(fixture.CredentialRequests);
        Assert.Equal(snapshot, fixture.Store.LoadSession()!.ServerBinding);
        fixture.AssertAnonymousDiscoveries();
    }

    [Fact]
    public async Task Actual_send_rediscovers_after_the_fresh_token_check_and_leaves_no_auth_header_on_refusal()
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        var discoveries = 0;
        fixture.Machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => Text(++discoveries == 1 ? Isolated : Legacy));
        using var request = ServerConnection.WorkspacesRequest();
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SendAsync(() => request, Ct));
        Assert.Null(request.Headers.Authorization);
        Assert.Empty(fixture.CredentialRequests);
        Assert.Equal(2, discoveries);
    }

    [Theory]
    [InlineData("https://foreign.example.test/api/items")]
    [InlineData(Origin + "/gateway-other/api/items")]
    [InlineData(Origin + "/api/items")]
    public async Task Off_endpoint_build_refuses_before_refresh_or_discovery(string destination)
    {
        using var fixture = new Fixture();
        fixture.SaveSession(expired: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination) { Content = new StringContent("fixture-body") };
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SendAsync(() => request, Ct));
        Assert.Empty(fixture.Machine.Server.Requests);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Unauthorized_retry_rotates_tokens_and_logout_keeps_the_floor()
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        var calls = 0;
        fixture.Machine.Server
            .On(HttpMethod.Get, "/gateway/api/machines/workspaces", request =>
            {
                Assert.Equal(++calls == 1 ? "MbusCli fixture-access" : "MbusCli fixture-new-access", request.Header("Authorization"));
                Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
                return new(calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
            })
            .On(HttpMethod.Post, "/gateway/api/cli/auth/refresh", request =>
            {
                Assert.Null(request.Header("Authorization"));
                Assert.Contains("fixture-refresh", request.BodyText);
                return Text("""{"accessToken":"fixture-new-access","refreshToken":"fixture-new-refresh","expiresIn":3600}""");
            })
            .On(HttpMethod.Post, "/gateway/api/cli/auth/logout", request =>
            {
                Assert.Equal("MbusCli fixture-new-access", request.Header("Authorization"));
                Assert.Contains("fixture-new-refresh", request.BodyText);
                return new(HttpStatusCode.NoContent);
            });
        var built = new List<HttpRequestMessage>();
        using var response = await fixture.Session.SendAsync(() =>
        {
            var request = ServerConnection.WorkspacesRequest();
            built.Add(request);
            return request;
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.All(built, request => Assert.Null(request.Headers.Authorization));
        var rotated = fixture.Store.LoadSession()!;
        Assert.Equal("fixture-new-refresh", rotated.RefreshToken);
        Assert.NotNull(rotated.ServerBinding);
        Assert.True((await fixture.Session.LogoutAsync(Ct)).Ended);
        Assert.Null(fixture.Store.LoadSession());
        Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        Assert.Equal(2, calls);
        fixture.AssertAnonymousDiscoveries();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_discovery_refusal_or_outage_only_forgets_locally_and_retains_pin(bool outage)
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        await fixture.Session.AccessTokenAsync(null, Ct);
        fixture.Discovery = Legacy;
        if (outage) fixture.Machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => new(HttpStatusCode.ServiceUnavailable));
        var result = await fixture.Session.LogoutAsync(Ct);
        Assert.False(result.Ended);
        Assert.NotNull(result.Problem);
        Assert.Null(fixture.Store.LoadSession());
        Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        Assert.Empty(fixture.CredentialRequests);
    }

    [Fact]
    public async Task Revoked_refresh_deletes_session_without_erasing_pin()
    {
        using var fixture = new Fixture();
        fixture.SaveSession(expired: true);
        fixture.Machine.Server.On(HttpMethod.Post, "/gateway/api/cli/auth/refresh", _ => new(HttpStatusCode.Unauthorized));
        Assert.Null(await fixture.Session.AccessTokenAsync(null, Ct));
        Assert.Null(fixture.Store.LoadSession());
        Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        fixture.SaveSession();
        fixture.Discovery = Legacy;
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.AccessTokenAsync(null, Ct));
        Assert.Single(fixture.CredentialRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_poll_refuses_binding_change_without_transmitting_the_device_code(bool legacyUpgrade)
    {
        using var fixture = new Fixture();
        if (legacyUpgrade) fixture.Discovery = Legacy;
        var started = await fixture.StartAsync();
        fixture.Discovery = legacyUpgrade ? Isolated : Legacy;
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.PollAsync(started.DeviceCode, Ct));
        Assert.Single(fixture.CredentialRequests);
        Assert.DoesNotContain(fixture.Machine.Server.Requests, r => r.BodyText.Contains("fixture-device", StringComparison.Ordinal));
        fixture.AssertAnonymousDiscoveries();
    }

    [Fact]
    public async Task Held_legacy_poll_refuses_overlapping_start_and_never_sends_its_code_under_isolated_discovery()
    {
        using var fixture = new Fixture { Discovery = Legacy };
        var legacy = await fixture.StartAsync();
        using var barrier = new RequestBarrier("/gateway/api/server");
        fixture.Barrier = barrier;
        var polling = fixture.Session.PollAsync(legacy.DeviceCode, Ct);
        await barrier.Entered.WaitAsync(Ct);
        fixture.Discovery = Isolated;
        await RefuseOverlapAsync(fixture.StartAsync());
        barrier.Release();
        await Assert.ThrowsAsync<ServerBindingException>(() => polling);
        Assert.Single(fixture.CredentialRequests); // Only the legacy start; no code-bearing POST.

        fixture.Machine.Server.On(HttpMethod.Post, "/gateway/api/cli/auth/start", _ =>
            Text("""{"deviceCode":"fixture-new-device","userCode":"IJKL-MNOP","expiresIn":600,"interval":5}"""));
        var isolated = await fixture.StartAsync();
        var approved = await fixture.Session.PollAsync(isolated.DeviceCode, Ct);
        var saved = await fixture.Session.SaveApprovedAsync(approved, Ct);
        Assert.NotNull(saved.ServerBinding);
        var token = Assert.Single(fixture.CredentialRequests, r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal));
        Assert.Contains("fixture-new-device", token.BodyText);
        Assert.DoesNotContain(fixture.CredentialRequests, r => r.BodyText.Contains(legacy.DeviceCode, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("token")]
    public async Task Held_device_response_refuses_overlapping_operations_and_obsolete_approval(string operation)
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        var first = await fixture.StartAsync();
        var obsolete = await fixture.Session.PollAsync(first.DeviceCode, Ct);
        using var barrier = new RequestBarrier("/gateway/api/cli/auth/" + operation, afterResponse: true);
        fixture.Barrier = barrier;
        var pending = operation == "start"
            ? (Task)fixture.StartAsync()
            : fixture.Session.PollAsync(first.DeviceCode, Ct);
        await barrier.Entered.WaitAsync(Ct);
        await RefuseOverlapAsync(fixture.StartAsync());
        await RefuseOverlapAsync(fixture.Session.PollAsync(first.DeviceCode, Ct));
        await RefuseOverlapAsync(fixture.Session.SaveApprovedAsync(obsolete, Ct));
        barrier.Release();
        await pending;
        Assert.Equal("fixture-access", fixture.Store.LoadSession()!.AccessToken);
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SaveApprovedAsync(obsolete, Ct));
        var current = await fixture.Session.PollAsync(first.DeviceCode, Ct);
        var saved = await fixture.Session.SaveApprovedAsync(current, Ct);
        Assert.Equal("fixture-approved-access", saved.AccessToken);
        Assert.Equal(saved, fixture.Store.LoadSession());
    }

    [Fact]
    public async Task Held_approval_save_keeps_its_issuing_record_and_rejects_overlap_before_the_profile_lock()
    {
        using var fixture = new Fixture();
        var first = await fixture.StartAsync();
        var approved = await fixture.Session.PollAsync(first.DeviceCode, Ct);
        using var barrier = new RequestBarrier("/gateway/api/server");
        fixture.Barrier = barrier;
        var saving = fixture.Session.SaveApprovedAsync(approved, Ct);
        await barrier.Entered.WaitAsync(Ct);
        await RefuseOverlapAsync(fixture.StartAsync());
        await RefuseOverlapAsync(fixture.Session.PollAsync(first.DeviceCode, Ct));
        await RefuseOverlapAsync(fixture.Session.SaveApprovedAsync(approved, Ct));
        barrier.Release();
        var saved = await saving;
        Assert.Equal(Origin, saved.ServerBinding!.Issuer);
        Assert.Equal(saved, fixture.Store.LoadSession());
        await fixture.StartAsync();
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SaveApprovedAsync(approved, Ct));
        Assert.Equal(saved, fixture.Store.LoadSession());
    }

    [Fact]
    public async Task Independent_instances_do_not_share_device_or_approval_provenance()
    {
        using var fixture = new Fixture { Discovery = Legacy };
        var legacy = await fixture.StartAsync();
        var oldApproval = await fixture.Session.PollAsync(legacy.DeviceCode, Ct);
        var independent = new CliSession(fixture.Host, fixture.Store, new Uri(Endpoint));
        fixture.Discovery = Isolated;
        var isolated = await independent.StartAsync(new CliStartBody("fixture", "fixture", null, null), Ct);
        var approval = await independent.PollAsync(isolated.DeviceCode, Ct);
        await Assert.ThrowsAsync<ServerBindingException>(() => independent.SaveApprovedAsync(oldApproval, Ct));
        var saved = await independent.SaveApprovedAsync(approval, Ct);
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SaveApprovedAsync(oldApproval, Ct));
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.PollAsync(legacy.DeviceCode, Ct));
        Assert.Equal(saved, fixture.Store.LoadSession());
        Assert.NotNull(saved.ServerBinding);
        Assert.Equal(2, fixture.CredentialRequests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Logout_of_another_selected_server_does_not_claim_remote_revocation()
    {
        using var fixture = new Fixture();
        fixture.SaveSession();
        await fixture.Session.AccessTokenAsync(null, Ct);
        var different = new CliSession(fixture.Host, fixture.Store, new Uri("https://foreign.example.test"));
        var result = await different.LogoutAsync(Ct);
        Assert.False(result.Ended);
        Assert.Null(fixture.Store.LoadSession());
        Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        Assert.Empty(fixture.CredentialRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_tokens_cannot_be_relabelled_when_discovery_changes_before_save(bool legacyUpgrade)
    {
        using var fixture = new Fixture();
        if (legacyUpgrade) fixture.Discovery = Legacy;
        var started = await fixture.StartAsync();
        var approved = await fixture.Session.PollAsync(started.DeviceCode, Ct);
        fixture.Discovery = legacyUpgrade ? Isolated : Legacy;
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.Session.SaveApprovedAsync(approved, Ct));
        Assert.Null(fixture.Store.LoadSession());
        Assert.True(File.Exists(fixture.Store.ServerBindingsPath));
        Assert.Equal(2, fixture.CredentialRequests.Count());
    }

    [Theory]
    [InlineData("https://foreign.example.test/approve")]
    [InlineData("https://other.stamps.example.test/approve")]
    [InlineData("http://eu.stamps.example.test/approve")]
    [InlineData("https://user@eu.stamps.example.test/approve")]
    [InlineData("https://eu.stamps.example.test/approve#fragment")]
    [InlineData("https://eu.stamps.example.test\\approve")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://eu.stamps.example.test/approve?%63ode=foreign")]
    public async Task Unsafe_or_unselected_approval_refuses_before_start_including_no_browser(string approval)
    {
        using var fixture = new Fixture();
        fixture.Discovery = Isolated.Replace(Origin + "/cli-login", approval.Replace("\\", "\\\\"), StringComparison.Ordinal);
        var info = new ServerInfoResponse { Product = "messagebus.ai", SignIn = new ServerSignIn { CliApprovalUrl = approval } };
        await Assert.ThrowsAsync<ServerBindingException>(() => BrowserSignIn.SignInAsync(fixture.Context(), fixture.Session, info,
            "fixture-machine", null, openBrowser: false, Ct));
        Assert.Empty(fixture.CredentialRequests);
        Assert.Empty(fixture.Machine.Browser.Opened);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5173/cli-login?source=terminal")]
    [InlineData("https://explicit-tunnel.example.test/cli-login")]
    public async Task Legacy_split_approval_and_no_browser_preserve_query_and_save_tokens(string approval)
    {
        using var fixture = new Fixture();
        fixture.Discovery = Legacy.Replace(Origin + "/cli-login", approval, StringComparison.Ordinal);
        var info = ServerDiscoveryParser.Parse(fixture.Discovery).Info;
        var output = new StringWriter();
        var context = fixture.Context(fixture.Machine.CreateHost(output));
        var saved = await BrowserSignIn.SignInAsync(context, fixture.Session, info, "fixture-machine", null, openBrowser: false, Ct);
        Assert.Equal("fixture-approved-access", saved.AccessToken);
        Assert.Null(saved.ServerBinding);
        Assert.Empty(fixture.Machine.Browser.Opened);
        Assert.Contains(approval + (approval.Contains('?') ? "&" : "?") + "code=ABCD-EFGH", output.ToString());
        Assert.All(fixture.CredentialRequests, r => Assert.Equal(new Uri(Endpoint).Authority, r.Uri.Authority));
    }

    [Fact]
    public async Task Isolated_approved_tokens_retain_the_issuing_endpoint_and_canonical_binding()
    {
        using var fixture = new Fixture();
        var started = await fixture.StartAsync();
        var approved = await fixture.Session.PollAsync(started.DeviceCode, Ct);
        var saved = await fixture.Session.SaveApprovedAsync(approved, Ct);
        Assert.Equal(Endpoint + "/", saved.ServerBinding!.SelectedEndpoint);
        Assert.Equal(Origin, saved.ServerBinding.Issuer);
        Assert.Equal(saved, fixture.Store.LoadSession());
        fixture.AssertAnonymousDiscoveries();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preconfigured_selected_alias_can_approve_at_itself_or_canonical_only(bool canonicalApproval)
    {
        const string alias = "https://alias.stamps.example.test";
        using var fixture = new Fixture(alias + "/gateway");
        fixture.Machine.Env["MBUS_HOME"] = fixture.Store.Directory;
        fixture.Machine.Env["MBUS_SERVER_BINDING"] = """{"version":1,"transportOrigin":"https://alias.stamps.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
        if (!canonicalApproval) fixture.Discovery = Isolated.Replace(Origin + "/cli-login", alias + "/cli-login", StringComparison.Ordinal);
        var started = await fixture.StartAsync();
        var approved = await fixture.Session.PollAsync(started.DeviceCode, Ct);
        var saved = await fixture.Session.SaveApprovedAsync(approved, Ct);
        Assert.Equal(alias + "/gateway/", saved.ServerBinding!.SelectedEndpoint);
        Assert.Equal(Origin, saved.ServerBinding.Issuer);
        Assert.All(fixture.Machine.Server.Requests, r => Assert.Equal("alias.stamps.example.test", r.Uri.Host));
    }

    [Fact]
    public async Task Advertised_alias_without_local_mapping_cannot_start_a_device_flow()
    {
        using var fixture = new Fixture("https://alias.stamps.example.test/gateway");
        await Assert.ThrowsAsync<ServerBindingException>(() => fixture.StartAsync());
        Assert.Empty(fixture.CredentialRequests);
    }

    public static IEnumerable<object[]> RedirectCases =>
        from status in new[] { 301, 302, 303, 307, 308 }
        from operation in new[] { "start", "token", "refresh", "logout", "enroll" }
        from sameOrigin in new[] { false, true }
        select new object[] { status, operation, sameOrigin };

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task Real_session_post_redirects_never_contact_the_target(int status, string operation, bool sameOrigin)
    {
        using var machine = new TestMachine();
        var source = new TcpListener(IPAddress.Loopback, 0);
        var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        var server = $"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}";
        var destination = sameOrigin ? server + "/stolen" : $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var paths = new List<string>();
        var serving = ServeAsync();
        try
        {
            var basis = machine.CreateHost();
            var host = new CliHost
            {
                Out = basis.Out, Error = basis.Error, In = basis.In,
                OpenStandardInput = basis.OpenStandardInput, OpenStandardOutput = basis.OpenStandardOutput,
                GetEnvironmentVariable = basis.GetEnvironmentVariable, HomeDirectory = basis.HomeDirectory,
                MachineName = basis.MachineName, MbusCommand = basis.MbusCommand, Browser = basis.Browser,
                Skills = basis.Skills, McpServer = basis.McpServer, Time = basis.Time,
                CreateHttpHandler = () => new SocketsHttpHandler(),
                CreateDiscoveryHttpHandler = () => new SocketsHttpHandler(),
            };
            var store = new ConfigStore(machine.MbusFolder, "file");
            var session = new CliSession(host, store, new Uri(server));
            if (operation is "refresh" or "logout" or "enroll")
                store.SaveSession(new CliSessionFile
                {
                    Server = server, AccessToken = "fixture-access", RefreshToken = "fixture-refresh",
                    AccessExpiresAt = machine.Time.GetUtcNow().AddHours(operation == "refresh" ? -1 : 1),
                });
            if (operation == "logout")
            {
                Assert.False((await session.LogoutAsync(stop.Token)).Ended);
                Assert.Null(store.LoadSession());
            }
            else
            {
                await Assert.ThrowsAsync<ServerBindingException>(async () =>
                {
                    if (operation == "start") await session.StartAsync(new CliStartBody("fixture", "fixture", null, null), stop.Token);
                    else if (operation == "token")
                    {
                        var started = await session.StartAsync(new CliStartBody("fixture", "fixture", null, null), stop.Token);
                        await session.PollAsync(started.DeviceCode, stop.Token);
                    }
                    else if (operation == "refresh") await session.AccessTokenAsync(null, stop.Token);
                    else using (await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/machines/enroll")
                    { Content = new StringContent("fixture-enrollment") }, stop.Token)) { }
                });
            }
            Assert.False(target.Pending());
            Assert.DoesNotContain(paths, path => path.Contains("/stolen", StringComparison.Ordinal));
            Assert.Contains(paths, path => path.StartsWith("POST ", StringComparison.Ordinal));
        }
        finally
        {
            stop.Cancel();
            source.Stop();
            target.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await source.AcceptTcpClientAsync(stop.Token); }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { return; }
                catch (SocketException) when (stop.IsCancellationRequested) { return; }
                using var connection = accepted;
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token) ?? "";
                paths.Add(first);
                var headers = new List<string>();
                for (var line = await reader.ReadLineAsync(stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(stop.Token)) headers.Add(line);
                var contentLength = headers.FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var body = new char[contentLength is null ? 0 : int.Parse(contentLength.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)];
                if (body.Length > 0)
                    Assert.Equal(body.Length, await reader.ReadBlockAsync(body.AsMemory(), stop.Token));
                var discovery = first.StartsWith("GET /api/server ", StringComparison.Ordinal);
                if (discovery) Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase));
                var bootstrap = operation == "token" && first.StartsWith("POST /api/cli/auth/start ", StringComparison.Ordinal);
                var text = discovery ? Legacy.Replace(Origin, server, StringComparison.Ordinal)
                    : """{"deviceCode":"fixture-device","userCode":"ABCD-EFGH","expiresIn":600,"interval":5}""";
                var reply = discovery || bootstrap
                    ? $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(text)}\r\nConnection: close\r\n\r\n{text}"
                    : $"HTTP/1.1 {status} Redirect\r\nLocation: {destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), stop.Token);
            }
        }
    }

    private static HttpResponseMessage Text(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static async Task RefuseOverlapAsync(Task operation)
    {
        Assert.True(operation.IsCompleted, "An overlapping device operation must refuse without waiting for discovery or the profile lock.");
        await Assert.ThrowsAsync<ServerBindingException>(() => operation);
    }

    private sealed class RequestBarrier(string path, bool afterResponse = false) : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public async Task HoldAsync(HttpRequestMessage request, bool responseReceived, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != path || responseReceived != afterResponse
                || Interlocked.CompareExchange(ref _held, 1, 0) != 0) return;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
        public void Dispose() => Release();
    }

    private sealed class BarrierHandler(Fixture fixture) : DelegatingHandler(fixture.Machine.Server)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (fixture.Barrier is { } before) await before.HoldAsync(request, false, ct);
            var response = await base.SendAsync(request, ct);
            try
            {
                if (fixture.Barrier is { } after) await after.HoldAsync(request, true, ct);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        protected override void Dispose(bool disposing) { } // The fixture owns the shared fake server.
    }

    private sealed class Fixture : IDisposable
    {
        public TestMachine Machine { get; } = new();
        public ConfigStore Store { get; }
        public CliHost Host { get; }
        public CliSession Session { get; }
        public RequestBarrier? Barrier { get; set; }
        public string Discovery { get; set; } = Isolated;
        public IEnumerable<RecordedRequest> CredentialRequests => Machine.Server.Requests.Where(r => r.Uri.AbsolutePath != "/gateway/api/server");
        private readonly string _endpoint;
        public Fixture(string endpoint = Endpoint)
        {
            _endpoint = endpoint;
            var basis = Machine.CreateHost();
            Host = new CliHost
            {
                Out = basis.Out, Error = basis.Error, In = basis.In,
                OpenStandardInput = basis.OpenStandardInput, OpenStandardOutput = basis.OpenStandardOutput,
                GetEnvironmentVariable = basis.GetEnvironmentVariable, HomeDirectory = basis.HomeDirectory,
                MachineName = basis.MachineName, MbusCommand = basis.MbusCommand, Browser = basis.Browser,
                Skills = basis.Skills, McpServer = basis.McpServer, Time = basis.Time,
                CreateHttpHandler = () => new BarrierHandler(this),
                CreateDiscoveryHttpHandler = () => new BarrierHandler(this),
            };
            Store = new ConfigStore(Machine.MbusFolder, "file");
            Session = new CliSession(Host, Store, new Uri(endpoint));
            Machine.Server
                .On(HttpMethod.Get, "/gateway/api/server", _ => Text(Discovery))
                .On(HttpMethod.Post, "/gateway/api/cli/auth/start", _ =>
                {
                    if (Discovery == Isolated) Assert.True(File.Exists(Store.ServerBindingsPath));
                    return Text("""{"deviceCode":"fixture-device","userCode":"ABCD-EFGH","expiresIn":600,"interval":5}""");
                })
                .On(HttpMethod.Post, "/gateway/api/cli/auth/token", _ => Text(Approved));
        }
        public void SaveSession(bool expired = false) => Store.SaveSession(new CliSessionFile
        {
            Server = _endpoint, AccessToken = "fixture-access", RefreshToken = "fixture-refresh",
            AccessExpiresAt = Machine.Time.GetUtcNow().AddHours(expired ? -1 : 1),
        });
        public Task<CliStartResponse> StartAsync() => Session.StartAsync(new CliStartBody("fixture-cli", "fixture-machine", null, null), Ct);
        public CommandContext Context(CliHost? host = null)
        {
            var options = new CommonOptions();
            var root = new RootCommand();
            root.Options.Add(options.Server);
            return CommandContext.Load(host ?? Host, root.Parse(["--server", _endpoint]), options);
        }
        public void AssertAnonymousDiscoveries()
        {
            var discoveries = Machine.Server.Requests.Where(r => r.Uri.AbsolutePath == "/gateway/api/server").ToArray();
            Assert.NotEmpty(discoveries);
            Assert.All(discoveries, r => { Assert.Empty(r.Headers); Assert.Empty(r.Body); });
        }
        public void Dispose() => Machine.Dispose();
    }
}

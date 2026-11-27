using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// the original issue: a run's network allow-list. The proxy itself (everything on 127.0.0.1; "allowed" hosts are answered by a
/// local stand-in, never the internet), and a real run whose agent asks for a host outside its list.
/// </summary>
public sealed class RunNetworkTests
{
    [Theory]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "::1")]
    public async Task Configured_local_model_exception_is_exact_authenticated_and_never_points_at_the_proxy(string configuredHost, string socketHost)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
        var dialled = new List<string>();
        using var proxy = RunNetworkProxy.Start(0, TextWriter.Null, async (host, port, ct) =>
        {
            lock (dialled) dialled.Add($"{host}:{port}");
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, serverPort, ct);
            return tcp.GetStream();
        }, UpstreamProxy.For(new Uri("http://proxy.example:9876")));
        using var local = proxy.Register(new HostAllowList(["github.com"]), "local-run");
        local.LocalModelEndpoint = new Uri($"http://{configuredHost}:{serverPort}/v1");
        using var other = proxy.Register(new HostAllowList(["github.com"]), "other-run");
        var target = $"{configuredHost}:{serverPort}";
        Assert.StartsWith("HTTP/1.1 407", await ConnectThroughAsync(proxy.Port, target, null));
        Assert.StartsWith("HTTP/1.1 407", await ConnectThroughAsync(proxy.Port, target, "Basic d3Jvbmc6d3Jvbmc="));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, target, Basic(other)));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, $"{configuredHost}:{serverPort + 1}", Basic(local)));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, $"localhost.evil:{serverPort}", Basic(local)));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, $"192.0.2.1:{serverPort}", Basic(local)));
        var echo = EchoOnceAsync(server);
        using (var client = await ConnectAsync(proxy.Port))
        {
            var stream = client.GetStream();
            Assert.StartsWith("HTTP/1.1 200", await RequestAsync(stream, $"CONNECT {target} HTTP/1.1\r\nProxy-Authorization: {Basic(local)}\r\n\r\n"));
            await stream.WriteAsync("ping"u8.ToArray());
            var answer = new byte[4];
            await stream.ReadExactlyAsync(answer);
            Assert.Equal("ping", Encoding.ASCII.GetString(answer));
        }
        await echo;
        var http = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream);
            Assert.StartsWith("GET /v1/models HTTP/1.1", head);
            Assert.DoesNotContain("Proxy-Authorization", head);
            await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        });
        using (var client = await ConnectAsync(proxy.Port))
            Assert.StartsWith("HTTP/1.1 204", await RequestAsync(client.GetStream(), $"GET http://{target}/v1/models HTTP/1.1\r\nHost: {target}\r\nProxy-Authorization: {Basic(local)}\r\n\r\n"));
        await http;
        Assert.Equal([$"{socketHost}:{serverPort}", $"{socketHost}:{serverPort}"], dialled);
        // Even a trusted configuration must not turn the mbus proxy into its own upstream.
        foreach (var alias in new[] { "localhost", "127.0.0.1", "127.0.0.2", "[::1]" })
        {
            local.LocalModelEndpoint = new Uri($"http://{alias}:{proxy.Port}/v1");
            Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, $"{alias}:{proxy.Port}", Basic(local)));
        }
        Assert.Equal(2, dialled.Count);
    }

    [Fact]
    public void GitHub_repository_allows_only_its_attachment_buckets()
    {
        var hosts = new HostAllowList(RunNetwork.RepositoryHosts(new Uri("https://github.com/example/repo")));
        var roundTripped = new HostAllowList(hosts.Entries);
        const string bucket = "github-production-user-asset-6210df.s3.amazonaws.com";
        Assert.True(roundTripped.Allows(bucket));
        Assert.False(roundTripped.Allows("github-production-user-asset-wrong.s3.amazonaws.com"));
        Assert.False(roundTripped.Allows("evil.s3.amazonaws.com"));
        Assert.False(roundTripped.Allows("http-intake.logs.us5.datadoghq.com"));
        Assert.False(new HostAllowList(RunNetwork.RepositoryHosts(new Uri("https://git.example.org/repo"))).Allows(bucket));
    }

    [Fact]
    public async Task The_proxy_tunnels_to_hosts_on_the_list_and_refuses_the_rest_without_connecting()
    {
        // The stand-in for every host the proxy may open: an echo server on this computer.
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoing = EchoOnceAsync(echo);
        var dialled = new List<string>();
        using var network = RunNetwork.Start(new HostAllowList(["github.com", "*.githubusercontent.com", "https://api.anthropic.com/v1"]), TextWriter.Null, "dlv_net",
            async (host, port, ct) =>
            {
                lock (dialled) dialled.Add($"{host}:{port}");
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)echo.LocalEndpoint).Port, ct);
                return tcp.GetStream();
            });

        // On the list: the tunnel opens and carries bytes both ways.
        using (var client = await ConnectAsync(network.Port))
        {
            var stream = client.GetStream();
            var answer = await RequestAsync(stream, "CONNECT objects.githubusercontent.com:443 HTTP/1.1\r\nHost: objects.githubusercontent.com:443\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200", answer);
            await stream.WriteAsync("ping"u8.ToArray());
            var buffer = new byte[4];
            await stream.ReadExactlyAsync(buffer);
            Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
        }
        await echoing;

        // Not on the list: a suffix that only looks alike, a sub-domain of an exact entry, an IP address, plain http.
        foreach (var request in new[]
        {
            "CONNECT evilgithubusercontent.com:443 HTTP/1.1\r\n\r\n",
            "CONNECT gist.github.com:443 HTTP/1.1\r\n\r\n",
            "CONNECT 203.0.113.7:443 HTTP/1.1\r\n\r\n",
            "GET http://example.com/upload?data=secret HTTP/1.1\r\nHost: example.com\r\n\r\n",
        })
        {
            using var client = await ConnectAsync(network.Port);
            var answer = await RequestAsync(client.GetStream(), request);
            Assert.StartsWith("HTTP/1.1 403", answer);
        }
        Assert.Equal(["objects.githubusercontent.com:443"], dialled);
        Assert.Equal(["203.0.113.7", "evilgithubusercontent.com", "example.com", "gist.github.com"], network.Refused.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Allowed_connections_go_through_the_runners_own_proxy_with_its_credential_except_its_no_proxy_hosts()
    {
        // the original issue. A stand-in for the runner's proxy: records each request, tunnels (the far end speaking first, as SSH does) or answers.
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var asked = new List<string>();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await proxy.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                lock (asked) asked.Add(head);
                if (head.StartsWith("CONNECT ", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\nSSH-2.0-far\r\n"u8.ToArray());
                    var buffer = new byte[4];
                    await stream.ReadExactlyAsync(buffer);
                    await stream.WriteAsync(buffer);
                }
                else
                {
                    await stream.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
                }
            }
        });
        var dialled = new List<string>();
        using var network = RunNetwork.Start(new HostAllowList(["github.com", "docs.example.org", "git.corp.example"]), TextWriter.Null, "dlv_up",
            async (host, port, ct) =>
            {
                lock (dialled) dialled.Add($"{host}:{port}");
                if (port != proxyPort)
                {
                    throw new SocketException((int)SocketError.ConnectionRefused); // a direct connection: nothing answers here
                }
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
                return tcp.GetStream();
            },
            UpstreamProxy.For(new Uri($"http://ci%40corp:p%3Ass@127.0.0.1:{proxyPort}"), ".corp.example"));
        var credential = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ci@corp:p:ss"));

        using (var client = await ConnectAsync(network.Port))
        {
            var stream = client.GetStream();
            Assert.StartsWith("HTTP/1.1 200", await RequestAsync(stream, "CONNECT github.com:22 HTTP/1.1\r\nHost: github.com:22\r\n\r\n"));
            var greeting = new byte[13];
            await stream.ReadExactlyAsync(greeting);
            Assert.Equal("SSH-2.0-far\r\n", Encoding.ASCII.GetString(greeting));
            await stream.WriteAsync("ping"u8.ToArray());
            var echo = new byte[4];
            await stream.ReadExactlyAsync(echo);
            Assert.Equal("ping", Encoding.ASCII.GetString(echo));
        }
        using (var client = await ConnectAsync(network.Port))
        {
            Assert.StartsWith("HTTP/1.1 204", await RequestAsync(client.GetStream(), "GET http://docs.example.org/a?b=1 HTTP/1.1\r\nHost: docs.example.org\r\nProxy-Authorization: Basic cnVuOnJ1bg==\r\n\r\n"));
        }
        using (var client = await ConnectAsync(network.Port))
        {
            Assert.StartsWith("HTTP/1.1 502", await RequestAsync(client.GetStream(), "CONNECT git.corp.example:443 HTTP/1.1\r\n\r\n"));
        }
        await serving;

        Assert.StartsWith("CONNECT github.com:22 HTTP/1.1\r\n", asked[0]);
        Assert.Contains(credential, asked[0]);
        // A plain request goes on in full to the runner's proxy, with the runner's credential instead of the run's.
        Assert.StartsWith("GET http://docs.example.org/a?b=1 HTTP/1.1\r\n", asked[1]);
        Assert.Contains(credential, asked[1]);
        Assert.DoesNotContain("cnVuOnJ1bg==", asked[1]);
        // NO_PROXY: connected directly.
        Assert.Equal([$"127.0.0.1:{proxyPort}", $"127.0.0.1:{proxyPort}", "git.corp.example:443"], dialled);
    }

    [Fact]
    public async Task The_proxy_keeps_taking_connections_after_an_error_taking_one()
    {
        // A failed accept (on Windows: a connection its client reset before it was taken) once ended the loop for good:
        // the port stayed taken, every later connection was refused, and each agent run failed to reach its model.
        var log = new StringWriter();
        var calls = 0;
        using var proxy = RunNetworkProxy.StartWithAccept(log, (listener, ct) =>
            Interlocked.Increment(ref calls) == 1 ? throw new SocketException((int)SocketError.ConnectionReset) : listener.AcceptSocketAsync(ct));

        Assert.StartsWith("HTTP/1.1 407", await ConnectThroughAsync(proxy.Port, "github.com:443", authorization: null));
        Assert.Contains("couldn't take a connection (ConnectionReset", log.ToString());
    }

    [Fact]
    public async Task On_the_runners_one_proxy_each_run_gets_its_own_list_by_its_credential_and_no_credential_gets_407()
    {
        // the original issue: one port for every run; the credential in each run's proxy address says whose list applies.
        var dialled = new List<string>();
        var log = new StringWriter();
        using var proxy = RunNetworkProxy.Start(0, log, (host, port, ct) =>
        {
            lock (dialled) dialled.Add($"{host}:{port}");
            return Task.FromResult<Stream>(new MemoryStream());
        });
        var first = proxy.Register(new HostAllowList(["github.com"]), "dlv_one");
        var second = proxy.Register(new HostAllowList(["dev.azure.com"]), "dlv_two");
        var open = proxy.Register(allow: null, "dlv_open");
        Assert.Equal(proxy.Port, first.Port);
        Assert.Equal(proxy.Port, second.Port);
        Assert.True(first.NeedsCredential);

        Assert.StartsWith("HTTP/1.1 200", await ConnectThroughAsync(proxy.Port, "github.com:443", Basic(first)));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, "dev.azure.com:443", Basic(first)));
        Assert.StartsWith("HTTP/1.1 200", await ConnectThroughAsync(proxy.Port, "dev.azure.com:443", Basic(second)));
        Assert.StartsWith("HTTP/1.1 403", await ConnectThroughAsync(proxy.Port, "github.com:443", Basic(second)));
        Assert.StartsWith("HTTP/1.1 200", await ConnectThroughAsync(proxy.Port, "example.com:443", Basic(open)));
        Assert.Equal(["dev.azure.com"], first.Refused);
        Assert.Equal(["github.com"], second.Refused);
        Assert.Contains("dlv_one: refused a connection to dev.azure.com", log.ToString());

        // No credential, another run's user with the wrong token, not Basic, and a run that has ended: 407, nothing dialled.
        var wrong = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"dlv_one:{second.Credential!.Value.Token}"));
        second.Dispose();
        foreach (var authorization in new[] { null, wrong, "Bearer x", Basic(second), "Basic !!!" })
        {
            var answer = await ConnectThroughAsync(proxy.Port, "github.com:443", authorization);
            Assert.StartsWith("HTTP/1.1 407", answer);
            Assert.Contains("Proxy-Authenticate: Basic", answer);
        }
        Assert.Equal(["github.com:443", "dev.azure.com:443", "example.com:443"], dialled);
    }

    [Fact]
    public async Task A_plain_request_goes_on_alone_with_its_body_and_nothing_sent_after_it_reaches_the_server()
    {
        // the original issue: a second request pipelined behind the first (its own proxy credential in it) is never forwarded.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var received = new MemoryStream();
            var buffer = new byte[4096];
            // The head and the 5-byte body, then the answer; then whatever else comes until the proxy closes.
            while (!Encoding.ASCII.GetString(received.ToArray()).EndsWith("\r\n\r\nhello", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                received.Write(buffer, 0, read);
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\nKeep-Alive: timeout=5\r\n\r\nok"u8.ToArray());
            accepted.Client.Shutdown(SocketShutdown.Send);
            int more;
            while ((more = await stream.ReadAsync(buffer)) > 0)
            {
                received.Write(buffer, 0, more);
            }
            return Encoding.ASCII.GetString(received.ToArray());
        });
        using var proxy = RunNetworkProxy.Start(0, TextWriter.Null, async (_, _, ct) =>
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, serverPort, ct);
            return tcp.GetStream();
        });
        var run = proxy.Register(new HostAllowList(["docs.example.org"]), "dlv_pipe");

        string answer;
        using (var client = await ConnectAsync(proxy.Port))
        {
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"POST http://docs.example.org/a HTTP/1.1\r\nHost: docs.example.org\r\nProxy-Authorization: {Basic(run)}\r\nContent-Length: 5\r\n\r\nhello"
                + $"GET http://docs.example.org/b HTTP/1.1\r\nHost: docs.example.org\r\nProxy-Authorization: {Basic(run)}\r\n\r\n"));
            answer = await new StreamReader(stream).ReadToEndAsync(); // the proxy closes after the one response
        }

        Assert.Equal("POST /a HTTP/1.1\r\nHost: docs.example.org\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello", await serving);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", answer);
    }

    [Fact]
    public async Task Withdrawing_a_runs_credential_closes_its_open_tunnels()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
        var held = server.AcceptTcpClientAsync();
        using var proxy = RunNetworkProxy.Start(0, TextWriter.Null, async (_, _, ct) =>
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, serverPort, ct);
            return tcp.GetStream();
        });
        var run = proxy.Register(new HostAllowList(["github.com"]), "dlv_tunnel");
        var other = proxy.Register(new HostAllowList(["github.com"]), "dlv_other");
        using var client = await ConnectAsync(proxy.Port);
        var stream = client.GetStream();
        Assert.StartsWith("HTTP/1.1 200", await RequestAsync(stream, $"CONNECT github.com:443 HTTP/1.1\r\nProxy-Authorization: {Basic(run)}\r\n\r\n"));
        using var far = await held;

        other.Dispose(); // another run's end leaves this tunnel open
        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[4];
        await far.GetStream().ReadExactlyAsync(echo, TestContext.Current.CancellationToken);
        Assert.Equal("ping", Encoding.ASCII.GetString(echo));

        run.Dispose();
        var one = new byte[1];
        var read = await Record.ExceptionAsync(async () => Assert.Equal(0, await stream.ReadAsync(one, TestContext.Current.CancellationToken)));
        Assert.True(read is null or IOException, read?.ToString());
    }

    [Fact]
    public async Task A_run_whose_preparation_fails_gives_its_place_on_the_runners_proxy_back()
    {
        using var shared = RunNetworkProxy.Start(0, TextWriter.Null);
        var root = Directory.CreateTempSubdirectory("mbus-net-").FullName;
        try
        {
            var sandbox = RunIsolation.Create(new RunIsolationOptions
            {
                WorkRoot = root, HomeDirectory = root, MachineName = "test-machine", LimitNetwork = true, SharedNetwork = shared,
                Requested = IsolationLevel.Basic,
            }, TextWriter.Null);
            using var workspace = MessageBus.Cli.Runner.RunWorkspace.Create(root, "dlv_broken", 1);
            File.WriteAllText(Path.Combine(workspace.Root, "tmp"), "a file where the run's temp folder goes");
            await using var h = new RunnerHarness();

            await Assert.ThrowsAnyAsync<IOException>(() => sandbox.PrepareAsync(workspace, h.Task("dlv_broken"), new PermissionProfile { Name = "worker" }, CancellationToken.None));

            Assert.Equal(0, shared.Registered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_run_cannot_reach_a_host_outside_its_allow_list()
    {
        // Exercise the shared Windows-style proxy on every host, independently of a host OS sandbox.
        // The fake agent's HttpClient must reach that proxy and receive its actual allow-list refusal.
        using var shared = RunNetworkProxy.Start(0, TextWriter.Null);
        await using var h = new RunnerHarness(sandbox: root => RunIsolation.Create(new RunIsolationOptions
        {
            Requested = IsolationLevel.Basic,
            WorkRoot = root,
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            MachineName = "test-machine",
            LimitNetwork = true,
            NetworkHosts = ["api.anthropic.com"],
            SharedNetwork = shared,
        }, TextWriter.Null));
        var task = h.Task("dlv_fetch", directives: d =>
        {
            d["fetch"] = "http://not-on-the-list.example/upload";
            d["env"] = "HTTP_PROXY,https_proxy,NO_PROXY";
        });
        task = task with
        {
            RunEnvironment = task.RunEnvironment with { PermissionProfile = new PermissionProfile { Name = "worker", AllowedHosts = ["docs.example.org"] } },
            Envelope = task.Envelope with { Refs = [new Ref { Kind = RefKinds.GitHubPullRequest, Repo = "acme/app", Number = 7 }] },
        };

        Assert.True((await h.DeliverAsync(task)).Accepted);
        var settle = await h.Service.SettledAsync("dlv_fetch");

        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        var fetched = File.ReadAllText(Path.Combine(h.WorkTreeOf("dlv_fetch"), "fetch.txt"));
        Assert.StartsWith("403 mbus: not-on-the-list.example isn't on this task's network allow-list", fetched);
        var env = File.ReadAllLines(Path.Combine(h.WorkTreeOf("dlv_fetch"), "env.txt")).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.StartsWith("http://dlv_fetch:", env["HTTP_PROXY"]);
        Assert.EndsWith($"@127.0.0.1:{shared.Port}", env["HTTP_PROXY"]);
        Assert.Equal(env["HTTP_PROXY"], env["https_proxy"]);
        Assert.Contains("127.0.0.1", env["NO_PROXY"]);
    }

    private static string Basic(RunNetwork run) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{run.Credential!.Value.User}:{run.Credential.Value.Token}"));

    /// <summary>A CONNECT through the proxy, with a <c>Proxy-Authorization</c> value or none; the answer's head.</summary>
    private static async Task<string> ConnectThroughAsync(int port, string target, string? authorization)
    {
        using var client = await ConnectAsync(port);
        return await RequestAsync(client.GetStream(),
            $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n{(authorization is null ? "" : $"Proxy-Authorization: {authorization}\r\n")}\r\n");
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    /// <summary>Sends <paramref name="request"/> and reads the response head (up to the blank line).</summary>
    private static async Task<string> RequestAsync(NetworkStream stream, string request)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        return await ReadHeadAsync(stream);
    }

    /// <summary>Reads a request or response head (up to the blank line), a byte at a time.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
        {
            head.Append((char)one[0]);
        }
        return head.ToString();
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer);
        await stream.WriteAsync(buffer);
    }
}


// On macOS, a concurrent process launch can hold the listener's descriptor between fork and exec.
// Run the immediate rebind check without concurrent process launches from other tests.
[CollectionDefinition(nameof(StableRunNetworkPortTests), DisableParallelization = true)]
public sealed class StableRunNetworkPortCollection;

[Collection(nameof(StableRunNetworkPortTests))]
public sealed class StableRunNetworkPortTests
{
    [Fact]
    public void The_runners_proxy_keeps_its_port_across_restarts_and_moves_only_when_it_is_taken()
    {
        var settings = Directory.CreateTempSubdirectory("mbus-net-").FullName;
        try
        {
            // Keep both scan ranges below the default ephemeral ranges so parallel tests' port-zero
            // listeners cannot take the stored port while the proxy is stopped.
            var range = 20000 + Random.Shared.Next(0, 50) * 2 * RunNetworkProxy.PortRangeSize;
            int port;
            using (var proxy = RunNetworkProxy.StartStable(settings, TextWriter.Null, rangeStart: range)!)
            {
                port = proxy.Port;
            }
            Assert.Equal(port, RunNetworkProxy.StoredPort(settings));
            using (var again = RunNetworkProxy.StartStable(settings, TextWriter.Null, rangeStart: range + RunNetworkProxy.PortRangeSize)!)
            {
                Assert.Equal(port, again.Port);
            }

            using var squatter = new TcpListener(IPAddress.Loopback, port);
            squatter.Start();
            Assert.Null(RunNetworkProxy.StartStable(settings, TextWriter.Null, moveIfTaken: false, rangeStart: range)); // mbus doctor --smoke
            var log = new StringWriter();
            using var moved = RunNetworkProxy.StartStable(settings, log, rangeStart: range)!;
            Assert.NotEqual(port, moved.Port);
            Assert.Equal(moved.Port, RunNetworkProxy.StoredPort(settings));
            Assert.Contains($"port {port} for runs' network proxy is taken", log.ToString());
        }
        finally
        {
            Directory.Delete(settings, recursive: true);
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MessageBus.Cli.Tests.Infrastructure;

// One owned CONNECT tunnel to one fixed fixture destination; it cannot forward to arbitrary hosts.
internal sealed class FixtureConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Uri _target;
    private readonly Task _serving;
    private TcpClient? _incoming;
    private TcpClient? _outgoing;
    internal Uri Address { get; }
    internal int Connects { get; private set; }

    internal FixtureConnectProxy(Uri target, CancellationToken ct)
    {
        _target = target;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop.CancelAfter(TimeSpan.FromSeconds(20));
        _listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
            _incoming = incoming;
            using var input = incoming.GetStream();
            using var reader = new StreamReader(input, Encoding.ASCII, leaveOpen: true);
            Assert.Equal($"CONNECT {_target.Authority} HTTP/1.1", await reader.ReadLineAsync(_stop.Token));
            var headers = new List<string>();
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header) headers.Add(header);
            Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
                || h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
                || h.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase) || h.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
            using var outgoing = new TcpClient();
            _outgoing = outgoing;
            await outgoing.ConnectAsync(IPAddress.Loopback, _target.Port, _stop.Token);
            using var output = outgoing.GetStream();
            Connects++;
            await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _stop.Token);
            // TLS is opaque here: the actual endpoint and client perform their normal handshake.
            var toServer = input.CopyToAsync(output, _stop.Token);
            var toClient = output.CopyToAsync(input, _stop.Token);
            await Task.WhenAny(toServer, toClient);
            incoming.Dispose();
            outgoing.Dispose();
            await ObserveRelayAsync(toServer);
            await ObserveRelayAsync(toClient);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
    }

    private static async Task ObserveRelayAsync(Task relay)
    {
        try { await relay; }
        catch (IOException) { /* The other half closed after forwarding the final bytes. */ }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _incoming?.Dispose();
        _outgoing?.Dispose();
        try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { _stop.Dispose(); }
    }
}

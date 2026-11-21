using System.Net;
using System.Net.Sockets;

namespace MessageBus.Cli.Runner.Sandbox;

// The ACI proxy never delegates DNS or connections to an upstream proxy. Check
// every resolved IPv4 address and connect to that same address after checking it.
internal sealed class AciEgress(string identityHost)
{
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (port is not (443 or 22) || IsIdentityHost(host))
            throw new IOException("ACI tasks can only proxy HTTPS or SSH to approved public destinations.");

        var addresses = await Dns.GetHostAddressesAsync(host.TrimEnd('.'), AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new IOException("The destination has no approved public IPv4 address.");

        SocketException? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new IOException("The approved public destination could not be reached.", failure);
    }

    internal bool IsIdentityHost(string host)
    {
        var name = host.TrimEnd('.');
        return name.Equals(identityHost, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".privatelink.blob.core.windows.net", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 or >= 224 => false,
            100 when octets[1] is >= 64 and <= 127 => false,
            // Azure's virtual platform endpoint is not in the IPv4 link-local range.
            168 when octets[1] == 63 && octets[2] == 129 && octets[3] == 16 => false,
            169 when octets[1] == 254 => false,
            172 when octets[1] is >= 16 and <= 31 => false,
            192 when octets[1] == 0 && octets[2] is 0 or 2 => false,
            192 when octets[1] == 88 && octets[2] == 99 => false,
            192 when octets[1] == 168 => false,
            198 when octets[1] is 18 or 19 || octets[1] == 51 && octets[2] == 100 => false,
            203 when octets[1] == 0 && octets[2] == 113 => false,
            _ => true,
        };
    }
}

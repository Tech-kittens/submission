using System.Net;
using System.Net.Sockets;

namespace Quacktion.Data.Agents;

public static class McpHttpSecurity
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x)))
                throw new HttpRequestException("MCP target resolved to a non-public address.");
            // Connect to the validated IP, avoiding a second DNS lookup and rebinding.
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168) && !(b[0] == 198 && b[1] is 18 or 19);
        return b.Length == 16 && (b[0] & 0xe0) == 0x20; // Global unicast only.
    }
}

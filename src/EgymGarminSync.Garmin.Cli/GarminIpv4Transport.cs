using System.Net;
using System.Net.Sockets;

namespace EgymGarminSync.Garmin.Cli;

/// <summary>Diagnostic-only IPv4 transport; never changes system DNS or IPv6 settings.</summary>
internal static class GarminIpv4Transport
{
    public static SocketsHttpHandler Create() => new()
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
            if (addresses.Length == 0)
                throw new HttpRequestException("No IPv4 address available for diagnostic.");
            Exception? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    lastError = ex;
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }
            throw new HttpRequestException("IPv4 connection failed.", lastError);
        }
    };
}

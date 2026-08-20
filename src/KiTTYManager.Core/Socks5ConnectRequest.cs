using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KiTTYManager.Core;

public static class Socks5ConnectRequest
{
    public static byte[] Build(Uri destination)
    {
        var port = destination.IsDefaultPort
            ? destination.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : destination.Port;
        var request = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(destination.Host, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        { request.Add(0x01); request.AddRange(address.GetAddressBytes()); }
        else
        {
            var host = Encoding.ASCII.GetBytes(destination.IdnHost);
            if (host.Length is 0 or > 255) throw new ArgumentException("Некорректное имя SOCKS5.", nameof(destination));
            request.Add(0x03); request.Add((byte)host.Length); request.AddRange(host);
        }
        request.Add((byte)(port >> 8)); request.Add((byte)port);
        return request.ToArray();
    }
}

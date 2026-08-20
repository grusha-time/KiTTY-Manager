using System.Net;
using System.Net.Sockets;

namespace KiTTYManager.Core;

public static class JumphostPortSelector
{
    public static int Select(BaseProxy proxy)
    {
        if (!proxy.UseAutomaticPort) return proxy.Port;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            proxy.Host = "127.0.0.1";
            proxy.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return proxy.Port;
        }
        finally { listener.Stop(); }
    }
}

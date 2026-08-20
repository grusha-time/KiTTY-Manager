namespace KiTTYManager.Core;

public static class ProxyEndpointComparer
{
    public static bool HostsEquivalent(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase) ||
        IsLoopback(left) && IsLoopback(right);

    private static bool IsLoopback(string host) =>
        host.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Trim().Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Trim().Equals("::1", StringComparison.OrdinalIgnoreCase);
}

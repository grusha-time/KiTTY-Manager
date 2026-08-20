using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using KiTTYManager.Core;

namespace KiTTYManager.App;

internal sealed record ManagedJumphostProcess(
    Guid ProxyId, JumphostConsoleKind Kind, int ProcessId, DateTime ProcessStartTimeUtc,
    string Host, int Port, string Title);

internal enum JumphostProcessKind
{
    NoListener,
    ManagedKitty,
    UnknownKitty,
    NonKitty
}

internal sealed record JumphostProcessMatch(
    JumphostProcessKind Kind, int? ProcessId = null, DateTime? ProcessStartTimeUtc = null,
    string ProcessName = "", string WindowTitle = "", bool TitleMatches = false);

/// <summary>
/// Associates a SOCKS listener with the exact KiTTY process that owns it and
/// remembers every console the manager opened or adopted (entry SOCKS console
/// and isolated access-script console). The registry is persisted to a small
/// file in Data so a manager restart recognises its own consoles instead of
/// spawning duplicates. A matching title is diagnostic only and never upgrades
/// an unknown process to a manager-owned one.
/// </summary>
internal sealed class JumphostProcessRegistry
{
    private readonly record struct Key(Guid ProxyId, JumphostConsoleKind Kind);
    private readonly ConcurrentDictionary<Key, ManagedJumphostProcess> managed = new();

    public void Remember(BaseProxy proxy, JumphostConsoleKind kind, Process process, string title) =>
        Store(proxy.Id, kind, process, title, proxy.Host, proxy.Port);

    public bool Adopt(BaseProxy proxy, JumphostConsoleKind kind, int processId, string title)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return false;
            }
            Store(proxy.Id, kind, process, title, proxy.Host, proxy.Port);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public bool Restore(JumphostConsoleRecord record)
    {
        try
        {
            var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                return false;
            }
            if (!IsKittyProcessName(process.ProcessName))
            {
                process.Dispose();
                return false;
            }
            if (process.StartTime.ToUniversalTime() != record.StartTimeUtc)
            {
                process.Dispose();
                return false;
            }
            Store(record.ProxyId, record.Kind, process, record.Title, record.Host, record.Port);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Store(
        Guid proxyId, JumphostConsoleKind kind, Process process, string title,
        string host, int port)
    {
        process.Refresh();
        var identity = new ManagedJumphostProcess(
            proxyId, kind, process.Id, process.StartTime.ToUniversalTime(), host, port, title);
        var key = new Key(proxyId, kind);
        managed[key] = identity;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (managed.TryGetValue(key, out var current) && current == identity)
                managed.TryRemove(key, out _);
            process.Dispose();
        };
    }

    public void Forget(Guid proxyId)
    {
        managed.TryRemove(new Key(proxyId, JumphostConsoleKind.Entry), out _);
        managed.TryRemove(new Key(proxyId, JumphostConsoleKind.Access), out _);
    }

    public bool TryGetAliveManaged(BaseProxy proxy, out ManagedJumphostProcess identity) =>
        TryGetAlive(proxy, JumphostConsoleKind.Entry, out identity);

    public bool TryGetAlive(BaseProxy proxy, JumphostConsoleKind kind, out ManagedJumphostProcess identity)
    {
        var key = new Key(proxy.Id, kind);
        if (!managed.TryGetValue(key, out identity!)) return false;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime() == identity.ProcessStartTimeUtc)
                return true;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        managed.TryRemove(key, out _);
        identity = null!;
        return false;
    }

    public IReadOnlyList<JumphostConsoleRecord> Snapshot()
    {
        var result = new List<JumphostConsoleRecord>();
        foreach (var identity in managed.Values)
        {
            if (!IsAlive(identity))
            {
                managed.TryRemove(new Key(identity.ProxyId, identity.Kind), out _);
                continue;
            }
            result.Add(new JumphostConsoleRecord(
                identity.ProxyId, identity.Kind, identity.ProcessId,
                identity.ProcessStartTimeUtc, identity.Title, identity.Host, identity.Port));
        }
        return result
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Clear() => managed.Clear();

    private static bool IsAlive(ManagedJumphostProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited &&
                   process.StartTime.ToUniversalTime() == identity.ProcessStartTimeUtc;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsKittyProcessName(string name) =>
        name.Equals("kitty", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("kitty_portable", StringComparison.OrdinalIgnoreCase);

    public JumphostProcessMatch Classify(BaseProxy proxy, string expectedTitlePart)
    {
        var pid = TcpListenerOwner.Find(proxy.Host, proxy.Port);
        if (pid is null) return new(JumphostProcessKind.NoListener);
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            process.Refresh();
            var started = process.StartTime.ToUniversalTime();
            var name = process.ProcessName;
            var title = process.MainWindowTitle;
            if (!IsKittyProcessName(name))
                return new(JumphostProcessKind.NonKitty, pid, started, name, title);
            var titleMatches = !string.IsNullOrWhiteSpace(expectedTitlePart) &&
                title.Contains(expectedTitlePart, StringComparison.OrdinalIgnoreCase);
            var known = managed.TryGetValue(new Key(proxy.Id, JumphostConsoleKind.Entry), out var identity) &&
                        identity.ProcessId == pid && identity.ProcessStartTimeUtc == started &&
                        identity.Port == proxy.Port &&
                        HostsEquivalent(identity.Host, proxy.Host);
            return new(known ? JumphostProcessKind.ManagedKitty : JumphostProcessKind.UnknownKitty,
                pid, started, name, title, titleMatches);
        }
        catch (ArgumentException) { return new(JumphostProcessKind.NoListener); }
        catch (InvalidOperationException) { return new(JumphostProcessKind.NoListener); }
    }

    private static bool HostsEquivalent(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(left, out var leftAddress) &&
        IPAddress.TryParse(right, out var rightAddress) && leftAddress.Equals(rightAddress);
}

internal static class TcpListenerOwner
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int OwnerPidListener = 3;

    public static int? Find(string host, int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535) return null;
        IPAddress? expected = null;
        if (IPAddress.TryParse(host, out var parsed)) expected = parsed;
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            expected = IPAddress.Loopback;
        return FindV4(expected, port) ?? FindV6(expected, port);
    }

    private static int? FindV4(IPAddress? expected, int port)
    {
        foreach (var row in ReadTable(AfInet, 24))
        {
            var localAddress = new IPAddress((long)(uint)Marshal.ReadInt32(row, 4));
            if (ReadPort(row, 8) == port && AddressMatches(expected, localAddress))
                return Marshal.ReadInt32(row, 20);
        }
        return null;
    }

    private static int? FindV6(IPAddress? expected, int port)
    {
        foreach (var row in ReadTable(AfInet6, 56))
        {
            var bytes = new byte[16];
            Marshal.Copy(row, bytes, 0, bytes.Length);
            var localAddress = new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 16));
            if (ReadPort(row, 20) == port && AddressMatches(expected, localAddress))
                return Marshal.ReadInt32(row, 52);
        }
        return null;
    }

    private static IEnumerable<nint> ReadTable(int addressFamily, int rowSize)
    {
        const uint insufficientBuffer = 122;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var sizeResult = GetExtendedTcpTable(0, ref size, false, addressFamily, OwnerPidListener, 0);
            if (size == 0 || sizeResult is not (0 or insufficientBuffer)) yield break;
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            uint result;
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, OwnerPidListener, 0);
                if (result == 0)
                {
                    var count = Marshal.ReadInt32(buffer);
                    var row = buffer + sizeof(int);
                    for (var index = 0; index < count; index++) yield return row + index * rowSize;
                    yield break;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (result != insufficientBuffer) yield break;
        }
    }

    private static int ReadPort(nint row, int offset)
    {
        var raw = Marshal.ReadInt32(row, offset);
        return (ushort)IPAddress.NetworkToHostOrder((short)(raw & 0xffff));
    }

    private static bool AddressMatches(IPAddress? expected, IPAddress actual) =>
        expected is null || expected.Equals(actual) ||
        IPAddress.IsLoopback(expected) && IPAddress.IsLoopback(actual) ||
        expected.Equals(IPAddress.Any) || expected.Equals(IPAddress.IPv6Any) ||
        actual.Equals(IPAddress.Any) || actual.Equals(IPAddress.IPv6Any);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint tcpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}

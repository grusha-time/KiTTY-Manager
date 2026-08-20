using System.Text;

namespace KiTTYManager.Core;

public sealed class KittyRoutedSession : IDisposable
{
    public string Name { get; }
    public string Path { get; }

    private KittyRoutedSession(string name, string path) => (Name, Path) = (name, path);

    public static KittyRoutedSession Create(
        string sourceSessionPath, int localSshPort, string importedCommand = "", bool ignoreImportedCommand = false,
        int? dynamicPort = null)
    {
        if (!File.Exists(sourceSessionPath)) throw new FileNotFoundException("Сессия KiTTY не найдена.", sourceSessionPath);
        var name = "KiTTYManager-route-" + Guid.NewGuid().ToString("N");
        var runtimeDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KiTTYManager", "RoutedSessions");
        Directory.CreateDirectory(runtimeDirectory);
        var path = System.IO.Path.Combine(runtimeDirectory, name);
        var lines = File.ReadAllLines(sourceSessionPath).ToList();
        Set(lines, "HostName", "127.0.0.1");
        Set(lines, "PortNumber", localSshPort.ToString());
        Set(lines, "ProxyMethod", "0");
        Set(lines, "ProxyLocalhost", "0");
        Set(lines, "Nopty", "0");
        Set(lines, "PortForwardings", dynamicPort is > 0 ? $"D{dynamicPort}=" : "");
        // Password is encrypted with HostName as part of the salt in the
        // portable mode used by these sessions. It becomes invalid when this
        // temporary copy points at 127.0.0.1; -pass supplies a fresh value.
        Set(lines, "Password", "");
        // The manager supplies a temporary, prompt-aware privilege script.
        Set(lines, "Scriptfile", "");
        Set(lines, "ScriptfileContent", "");
        Set(lines, "RemoteCommand", "");
        var isPrivilegeCommand = KittyCredentialDecoder.NormalizeRootCommand(importedCommand) is not null;
        Set(lines, "Autocommand", ignoreImportedCommand || isPrivilegeCommand ? "" : importedCommand);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return new KittyRoutedSession(name, path);
    }

    public static KittyRoutedSession CreateDirect(
        string sourceSessionPath, string host, int port, string importedCommand = "",
        bool ignoreImportedCommand = false)
    {
        if (!File.Exists(sourceSessionPath))
            throw new FileNotFoundException("Сессия KiTTY не найдена.", sourceSessionPath);
        var name = "KiTTYManager-direct-" + Guid.NewGuid().ToString("N");
        var runtimeDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "KiTTYManager", "RoutedSessions");
        Directory.CreateDirectory(runtimeDirectory);
        var path = System.IO.Path.Combine(runtimeDirectory, name);
        var lines = File.ReadAllLines(sourceSessionPath).ToList();
        Set(lines, "HostName", host);
        Set(lines, "PortNumber", port.ToString());
        Set(lines, "ProxyMethod", "0");
        Set(lines, "ProxyLocalhost", "0");
        Set(lines, "Nopty", "0");
        Set(lines, "Password", "");
        Set(lines, "Scriptfile", "");
        Set(lines, "ScriptfileContent", "");
        Set(lines, "RemoteCommand", "");
        var isPrivilegeCommand =
            KittyCredentialDecoder.NormalizeRootCommand(importedCommand) is not null;
        Set(lines, "Autocommand",
            ignoreImportedCommand || isPrivilegeCommand ? "" : importedCommand);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return new KittyRoutedSession(name, path);
    }

    /// <summary>
    /// Creates a minimal temporary session file from scratch when no saved KiTTY
    /// session exists. Used for web tunnels on manager-only sessions (duplicates).
    /// </summary>
    public static KittyRoutedSession CreateMinimal(int localSshPort, int dynamicPort)
    {
        var name = "KiTTYManager-route-" + Guid.NewGuid().ToString("N");
        var runtimeDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KiTTYManager", "RoutedSessions");
        Directory.CreateDirectory(runtimeDirectory);
        var path = System.IO.Path.Combine(runtimeDirectory, name);
        var lines = new List<string>
        {
            "Present\\1\\",
            "Protocol\\ssh\\",
            "HostName\\127.0.0.1\\",
            $"PortNumber\\{localSshPort}\\",
            "TerminalType\\xterm\\",
            "Nopty\\0\\",
            "ProxyMethod\\0\\",
            "ProxyLocalhost\\0\\",
            $"PortForwardings\\D{dynamicPort}=\\",
            "Password\\\\",
            "Scriptfile\\\\",
            "ScriptfileContent\\\\",
            "Autocommand\\\\",
            "RemoteCommand\\\\"
        };
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return new KittyRoutedSession(name, path);
    }

    private static void Set(List<string> lines, string key, string value)
    {
        var prefix = key + "\\";
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var replacement = $"{key}\\{value}\\";
        if (index < 0) lines.Add(replacement);
        else lines[index] = replacement;
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { }
    }

    public static void CleanupStaleFiles(string? legacySessionsDirectory = null)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KiTTYManager", "RoutedSessions");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "KiTTYManager-route-*"))
                try { File.Delete(path); } catch { }
        if (!string.IsNullOrWhiteSpace(legacySessionsDirectory) && Directory.Exists(legacySessionsDirectory))
            foreach (var path in Directory.EnumerateFiles(legacySessionsDirectory, "KiTTYManager-route-*", SearchOption.TopDirectoryOnly))
                try { File.Delete(path); } catch { }
    }
}

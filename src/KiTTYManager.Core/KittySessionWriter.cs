using System.Globalization;
using System.Text;

namespace KiTTYManager.Core;

public static class KittySessionWriter
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        [nameof(ManagedServer.Host)] = "HostName",
        [nameof(ManagedServer.Port)] = "PortNumber",
        [nameof(ManagedServer.Username)] = "UserName",
        [nameof(ManagedServer.Password)] = "Password",
        [nameof(ManagedServer.PrivateKeyPath)] = "PublicKeyFile",
        [nameof(ManagedServer.UseKeyboardInteractive)] = "AuthKI",
        [nameof(ManagedServer.ImportedCommand)] = "Autocommand"
    };

    public static IReadOnlySet<string> WritableProperties { get; } =
        new HashSet<string>(Keys.Keys.Append(nameof(ManagedServer.Name)).Append(nameof(ManagedServer.RootPassword)), StringComparer.Ordinal);

    public static void Write(string bundledSessionsDirectory, ManagedServer server, IEnumerable<string> properties,
        BaseProxy? confirmedDirectProxy = null)
    {
        var root = CanonicalRoot(bundledSessionsDirectory);
        Directory.CreateDirectory(root);
        var selected = properties.Where(WritableProperties.Contains).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0 && confirmedDirectProxy is null) return;

        var source = ResolveSource(root, server.SourceSessionPath);
        var creating = source is null;
        source ??= Path.Combine(root, EncodeValue(server.Name));
        EnsureInside(root, source);
        if (creating && File.Exists(source))
            throw new IOException("Сессия KiTTY с таким именем уже существует. Выберите другое название.");
        var originalBytes = creating ? null : File.ReadAllBytes(source);
        var lines = creating ? new List<string>() : File.ReadAllLines(source).ToList();
        var values = creating ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : KittySessionImporter.ParseFile(source);
        var rewriteRootPassword = !creating && selected.Contains(nameof(ManagedServer.RootPassword)) &&
                                  server.KittyBaseline?.RootPassword != server.RootPassword;
        var createRootScript = selected.Contains(nameof(ManagedServer.RootPassword)) &&
                               KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin) is not null &&
                               server.RootPassword.Length > 0 &&
                               string.IsNullOrEmpty(values.GetValueOrDefault("ScriptfileContent"));

        if (creating)
        {
            selected.UnionWith(WritableProperties);
            Set(lines, "Present", "1");
            Set(lines, "Protocol", "ssh");
            Set(lines, "TerminalType", "xterm");
        }

        foreach (var property in selected)
        {
            if (property is nameof(ManagedServer.Name) or nameof(ManagedServer.RootPassword)) continue;
            var key = Keys[property];
            var value = property switch
            {
                nameof(ManagedServer.Host) => server.Host,
                nameof(ManagedServer.Port) => server.Port.ToString(CultureInfo.InvariantCulture),
                nameof(ManagedServer.Username) => server.Username,
                nameof(ManagedServer.PrivateKeyPath) => PortableKeyPath(root, server.PrivateKeyPath),
                nameof(ManagedServer.UseKeyboardInteractive) => server.UseKeyboardInteractive ? "1" : "0",
                nameof(ManagedServer.ImportedCommand) => server.ImportedCommand,
                nameof(ManagedServer.Password) => EncodePassword(root, server, values),
                _ => ""
            };
            Set(lines, key, value);
        }
        if (createRootScript)
        {
            // Include the shell prompt guard so the root password is not
            // accidentally sent to the initial SSH password prompt. The script
            // waits for the shell prompt, sends su/sudo, then answers the
            // password prompt (matching KittyLoginScript.Create logic).
            // Autocommand is NOT set: the script handles the entire flow.
            // Setting both would send su/sudo twice.
            // ScriptfileContent is encrypted with KiTTY's "9bis" key because
            // KiTTY 0.76.1.13 only executes encrypted embedded scripts.
            var shellPrompt = string.IsNullOrWhiteSpace(server.ShellPrompt) ? "$" : server.ShellPrompt;
            var command = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin)!;
            var script = $"{shellPrompt}\n{command}\nassword:\n{server.RootPassword}";
            Set(lines, "Autocommand", "");
            Set(lines, "ScriptfileContent", KittyCredentialDecoder.EncryptScriptContent(script));
        }
        else if (rewriteRootPassword)
        {
            var oldRootPassword = server.KittyBaseline?.RootPassword ?? "";
            if (!KittyCredentialDecoder.TryRewriteRootPassword(
                    values.GetValueOrDefault("ScriptfileContent") ?? "", server.RootLogin,
                    oldRootPassword, server.RootPassword, out var script))
                throw new InvalidOperationException(
                    "Root-пароль не записан: во встроенном login script не найден единственный безопасный ответ после su/sudo.");
            Set(lines, "ScriptfileContent", script);
        }
        // Если ImportedCommand — привилегированная команда (su -, sudo su),
        // а встроенный скрипт уже существует, очищаем Autocommand — иначе
        // KiTTY выполнит su/sudo дважды (из скрипта и из Autocommand).
        // Если ImportedCommand — другой скрипт (./protei_access.sh), не трогаем:
        // он выполнится после login script и может быть полезен.
        if (KittyCredentialDecoder.NormalizeRootCommand(server.ImportedCommand) is not null &&
            KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin) is not null &&
            server.RootPassword.Length > 0 &&
            !string.IsNullOrEmpty(values.GetValueOrDefault("ScriptfileContent")))
        {
            Set(lines, "Autocommand", "");
        }
        if (selected.Contains(nameof(ManagedServer.Password)) && !selected.Contains(nameof(ManagedServer.Host)))
            Set(lines, "HostName", server.Host);
        if (selected.Contains(nameof(ManagedServer.Host)) && !selected.Contains(nameof(ManagedServer.Password)))
            Set(lines, "Password", EncodePassword(root, server, values));
        var proxyToWrite = confirmedDirectProxy ?? (server.ImportedProxy is { Method: 2, Port: > 0 } imported
            ? new BaseProxy { Host = imported.Host, Port = imported.Port }
            : null);
        if (proxyToWrite is not null)
        {
            Set(lines, "ProxyMethod", "2");
            var existingProxyHost = values.GetValueOrDefault("ProxyHost") ?? "";
            Set(lines, "ProxyHost", ProxyEndpointComparer.HostsEquivalent(existingProxyHost, proxyToWrite.Host)
                ? existingProxyHost
                : proxyToWrite.Host);
            Set(lines, "ProxyPort", proxyToWrite.Port.ToString(CultureInfo.InvariantCulture));
        }

        var destination = selected.Contains(nameof(ManagedServer.Name))
            ? Path.Combine(root, EncodeValue(server.Name))
            : source;
        EnsureInside(root, destination);
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
            throw new IOException("Сессия KiTTY с таким именем уже существует.");

        var temp = Path.Combine(root, ".kitty-manager-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush(); stream.Flush(true);
            }
            _ = KittySessionImporter.ParseFile(temp);
            if (originalBytes is not null && (!File.Exists(source) || !File.ReadAllBytes(source).SequenceEqual(originalBytes)))
                throw new IOException("Сессия KiTTY изменилась во время записи. Обновите список изменений и повторите попытку.");
            if (File.Exists(destination))
            {
                var backupDirectory = Path.Combine(Directory.GetParent(root)!.FullName, "ManagerBackups", "Sessions");
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, Path.GetFileName(destination) + ".backup");
                File.Replace(temp, destination, backup, true);
            }
            else File.Move(temp, destination);
            if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            {
                var backupDirectory = Path.Combine(Directory.GetParent(root)!.FullName, "ManagerBackups", "Sessions");
                Directory.CreateDirectory(backupDirectory);
                File.Move(source, Path.Combine(backupDirectory, Path.GetFileName(source) + ".renamed-backup"), true);
            }
            server.SourceSessionPath = destination;
            VerifyWrittenSession(destination, server, selected, proxyToWrite,
                rewriteRootPassword || createRootScript);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static void VerifyWrittenSession(
        string path, ManagedServer server, IReadOnlySet<string> selected, BaseProxy? proxy, bool verifyRootPassword)
    {
        var values = KittySessionImporter.ParseFile(path);
        if (selected.Contains(nameof(ManagedServer.Password)))
        {
            var host = values.GetValueOrDefault("HostName") ?? "";
            var terminalType = values.GetValueOrDefault("TerminalType") ?? values.GetValueOrDefault("TermType") ?? "xterm";
            var decoded = KittyCredentialDecoder.DecodePassword(values.GetValueOrDefault("Password") ?? "", host,
                terminalType, KittySessionImporter.ReadCryptSaltMode(Path.GetDirectoryName(path)!));
            if (decoded != server.Password)
                throw new IOException("KiTTY не подтвердила записанный SSH-пароль. Изменение оставлено неприменённым.");
        }
        if (verifyRootPassword)
        {
            var imported = KittySessionImporter.ImportDirectory(Path.GetDirectoryName(path)!)
                .Single(item => Path.GetFullPath(item.SourceSessionPath!).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (imported.RootPassword != server.RootPassword)
                throw new IOException("KiTTY не подтвердила записанный root-пароль login script. Изменение оставлено неприменённым.");
        }
        if (proxy is null) return;
        _ = int.TryParse(values.GetValueOrDefault("ProxyMethod"), out var method);
        _ = int.TryParse(values.GetValueOrDefault("ProxyPort"), out var port);
        var hostValue = values.GetValueOrDefault("ProxyHost") ?? "";
        if (method != 2 || port != proxy.Port || !ProxyEndpointComparer.HostsEquivalent(hostValue, proxy.Host))
            throw new IOException("KiTTY не подтвердила записанные настройки SOCKS5. Изменение оставлено неприменённым.");
    }

    public static void RelocateLegacyBackups(string bundledSessionsDirectory)
    {
        var root = CanonicalRoot(bundledSessionsDirectory);
        if (!Directory.Exists(root)) return;
        var backupDirectory = Path.Combine(Directory.GetParent(root)!.FullName, "ManagerBackups", "Sessions");
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetFileName(path).Contains(".kitty-manager-backup", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileName(path).Contains(".kitty-manager-renamed-backup", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(backupDirectory);
            var destination = Path.Combine(backupDirectory, Path.GetFileName(source));
            File.Move(source, destination, true);
        }
    }

    private static string EncodePassword(string root, ManagedServer server, IReadOnlyDictionary<string, string> oldValues)
    {
        var terminalType = oldValues.GetValueOrDefault("TerminalType") ?? oldValues.GetValueOrDefault("TermType") ?? "xterm";
        return KittyCredentialDecoder.EncodePassword(server.Password, server.Host, terminalType,
            KittySessionImporter.ReadCryptSaltMode(root));
    }

    private static void Set(List<string> lines, string key, string value)
    {
        var prefix = key + "\\";
        var replacement = $"{key}\\{EncodeValue(value)}\\";
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index < 0) lines.Add(replacement); else lines[index] = replacement;
    }

    public static string EncodeValue(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes;
        try { bytes = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(value); }
        catch (EncoderFallbackException) { bytes = Encoding.UTF8.GetBytes(value); }
        var result = new StringBuilder(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            var valueByte = bytes[index];
            var character = (char)valueByte;
            var escape = valueByte < 32 || valueByte >= 127 || character is ' ' or '\\' or '*' or '?' or ':' or '/' or '"' or '<' or '>' or '|' or '%' || index == 0 && character == '.';
            if (escape) result.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            else result.Append(character);
        }
        return result.ToString();
    }

    private static string PortableKeyPath(string root, string path)
    {
        if (path.Length == 0) return "";
        var kittyRoot = Directory.GetParent(root)?.FullName;
        if (kittyRoot is not null && Path.IsPathRooted(path) && Path.GetFullPath(path).StartsWith(kittyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(kittyRoot, path).Replace(Path.DirectorySeparatorChar, '\\');
        return path;
    }

    private static string? ResolveSource(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var full = Path.GetFullPath(path); EnsureInside(root, full); return full;
    }

    private static string CanonicalRoot(string path)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        RejectReparsePoints(root, root);
        return root;
    }
    private static void EnsureInside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запись разрешена только во встроенную папку KiTTY\\Sessions.");
        RejectReparsePoints(root, full);
    }

    private static void RejectReparsePoints(string root, string path)
    {
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Путь к KiTTY\\Sessions не должен проходить через ссылку или junction.");
        var relative = Path.GetRelativePath(root, path);
        var candidate = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".") continue;
            candidate = Path.Combine(candidate, part);
            if ((Directory.Exists(candidate) || File.Exists(candidate)) &&
                File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Запись через ссылку или junction запрещена.");
        }
    }
}

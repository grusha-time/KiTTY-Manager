using System.Text;

namespace KiTTYManager.Core;

public static class KittySessionImporter
{
    public static IReadOnlyList<ManagedServer> ImportDirectory(string sessionsDirectory)
    {
        if (!Directory.Exists(sessionsDirectory))
            throw new DirectoryNotFoundException(sessionsDirectory);

        var result = new List<ManagedServer>();
        var cryptSaltMode = ReadCryptSaltMode(sessionsDirectory);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(sessionsDirectory, "*", options)
                     .OrderBy(path => Path.GetRelativePath(sessionsDirectory, path), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("KiTTYManager-route-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(".kitty-manager-", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".kitty-manager-backup", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".kitty-manager-renamed-backup", StringComparison.OrdinalIgnoreCase)) continue;
            var values = ParseFile(path);
            if (!values.TryGetValue("HostName", out var rawHost) || string.IsNullOrWhiteSpace(rawHost)) continue;
            var host = rawHost.Trim();
            _ = int.TryParse(values.GetValueOrDefault("PortNumber"), out var port);
            _ = int.TryParse(values.GetValueOrDefault("ProxyPort"), out var proxyPort);
            _ = int.TryParse(values.GetValueOrDefault("ProxyMethod"), out var proxyMethod);
            var username = values.GetValueOrDefault("UserName") ?? values.GetValueOrDefault("Username") ?? "";
            var storedPassword = values.GetValueOrDefault("Password") ?? "";
            var password = KittyCredentialDecoder.DecodePassword(
                storedPassword, rawHost,
                values.GetValueOrDefault("TerminalType") ?? values.GetValueOrDefault("TermType") ?? "xterm",
                cryptSaltMode) ?? "";
            var scriptCredentials = ReadScriptCredentials(values, path, cryptSaltMode);
            var rootLogin = scriptCredentials.RootLogin ?? "";
            var rootPassword = scriptCredentials.RootPassword ?? "";
            if (storedPassword.Length > 0 && scriptCredentials.Username is null &&
                scriptCredentials.Password is { Length: > 0 } scriptPassword &&
                rootPassword.Length == 0)
            {
                // A password-only KiTTY script is commonly used after typing `su`.
                // A separate saved Password already covers SSH authentication, so
                // this response belongs to the privilege-elevation prompt.
                rootLogin = "su";
                rootPassword = scriptPassword;
            }

            result.Add(new ManagedServer
            {
                Name = DecodeSessionName(fileName),
                Host = host,
                Port = port is > 0 and <= 65535 ? port : 22,
                Username = string.IsNullOrWhiteSpace(username) ? scriptCredentials.Username ?? "" : username,
                // A present but undecodable KiTTY Password must not be replaced by
                // the login-script response: for su scripts that response is the
                // root password and repeated SSH attempts may trigger a lockout.
                Password = string.IsNullOrEmpty(storedPassword) && string.IsNullOrEmpty(password)
                    ? scriptCredentials.Password ?? ""
                    : password,
                PasswordImportState = storedPassword.Length == 0 ? ImportedCredentialState.Empty :
                    password is null ? ImportedCredentialState.PresentButUndecodable : ImportedCredentialState.Decoded,
                PrivateKeyPath = ResolvePrivateKeyPath(path, values.GetValueOrDefault("PublicKeyFile")) ?? "",
                UseKeyboardInteractive = ReadBoolean(values.GetValueOrDefault("AuthKI"), true),
                RootLogin = rootLogin,
                RootPassword = rootPassword,
                ImportedCommand = values.GetValueOrDefault("Autocommand") ?? "",
                SourceSessionPath = Path.GetFullPath(path),
                SourceScriptPath = scriptCredentials.SourcePath,
                SourceScriptContent = scriptCredentials.Content,
                ImportedProxy = proxyPort > 0 ? new ImportedProxy
                {
                    Host = values.GetValueOrDefault("ProxyHost") ?? "127.0.0.1",
                    Port = proxyPort,
                    Method = proxyMethod
                } : null
            });
        }
        return result;
    }

    private static ImportedScriptCredentials ReadScriptCredentials(
        IReadOnlyDictionary<string, string> values, string sessionPath, int cryptSaltMode)
    {
        KittyScriptData? embedded = null;
        var content = values.GetValueOrDefault("ScriptfileContent");
        if (!string.IsNullOrWhiteSpace(content))
            embedded = KittyCredentialDecoder.DecodeLoginScript(content, cryptSaltMode);

        var scriptPath = ResolveScriptPath(sessionPath, values.GetValueOrDefault("Scriptfile"), embedded?.Content);
        var fileData = scriptPath is null ? null : KittyCredentialDecoder.ReadLoginScriptFile(scriptPath);
        if (embedded is null && fileData is null) return ImportedScriptCredentials.Empty;

        var credentials = new ImportedScriptCredentials(
            embedded?.Username ?? fileData?.Username,
            embedded?.Password ?? fileData?.Password,
            embedded?.RootLogin ?? fileData?.RootLogin,
            embedded?.RootPassword ?? fileData?.RootPassword,
            scriptPath is null ? null : Path.GetFullPath(scriptPath),
            embedded?.Content ?? fileData?.Content ?? "");

        var automaticRootCommand = KittyCredentialDecoder.NormalizeRootCommand(
            values.GetValueOrDefault("Autocommand"));
        if (automaticRootCommand is null) return credentials;
        if (!string.IsNullOrEmpty(credentials.RootPassword))
            return credentials with { RootLogin = credentials.RootLogin ?? automaticRootCommand };
        if (string.IsNullOrEmpty(credentials.Password)) return credentials;

        // A password-only login script paired with an automatic su/sudo command
        // answers that command's prompt; it is not the SSH authentication secret.
        return credentials with
        {
            Password = null,
            RootLogin = automaticRootCommand,
            RootPassword = credentials.Password
        };
    }

    private static string? ResolveScriptPath(string sessionPath, string? storedPath, string? decodedContent)
    {
        var explicitCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(storedPath))
        {
            explicitCandidates.Add(storedPath);
            if (!Path.IsPathRooted(storedPath))
                explicitCandidates.Add(Path.Combine(Path.GetDirectoryName(sessionPath)!, storedPath));
        }

        var sessionsRoot = FindSessionsRoot(Path.GetDirectoryName(sessionPath)!);
        var scriptDirectory = sessionsRoot?.Parent is null ? null : FindChildDirectory(sessionsRoot.Parent, "Script");
        if (scriptDirectory is not null)
        {
            var storedFileName = GetPortableFileName(storedPath);
            if (!string.IsNullOrEmpty(storedFileName))
                explicitCandidates.Add(Path.Combine(scriptDirectory.FullName, storedFileName));
        }

        var explicitMatch = explicitCandidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
        if (explicitMatch is not null) return Path.GetFullPath(explicitMatch);
        if (scriptDirectory is null || decodedContent is null) return null;

        string? uniqueMatch = null;
        foreach (var candidate in Directory.EnumerateFiles(scriptDirectory.FullName, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     IgnoreInaccessible = true
                 }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fileData = KittyCredentialDecoder.ReadLoginScriptFile(candidate);
            if (fileData is not null && NormalizeScript(fileData.Content) == NormalizeScript(decodedContent))
            {
                if (uniqueMatch is not null) return null;
                uniqueMatch = Path.GetFullPath(candidate);
            }
        }
        return uniqueMatch;
    }

    private static string? ResolvePrivateKeyPath(string sessionPath, string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;

        var sessionDirectory = Path.GetDirectoryName(sessionPath)!;
        var portablePath = storedPath.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var explicitCandidates = new List<string> { storedPath };
        if (!Path.IsPathRooted(portablePath))
            explicitCandidates.Add(Path.Combine(sessionDirectory, portablePath));

        var sessionsRoot = FindSessionsRoot(sessionDirectory);
        var kittyRoot = sessionsRoot?.Parent;
        if (kittyRoot is not null && !Path.IsPathRooted(portablePath))
            explicitCandidates.Add(Path.Combine(kittyRoot.FullName, portablePath));

        var explicitMatch = explicitCandidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        if (explicitMatch is not null) return Path.GetFullPath(explicitMatch);
        if (kittyRoot is null) return null;

        var fileName = GetPortableFileName(storedPath);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var matches = Directory.EnumerateFiles(kittyRoot.FullName, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            })
            .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .Select(Path.GetFullPath)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static DirectoryInfo? FindSessionsRoot(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Name.Equals("Sessions", StringComparison.OrdinalIgnoreCase)) return current;
        return null;
    }

    private static DirectoryInfo? FindChildDirectory(DirectoryInfo parent, string name) =>
        parent.EnumerateDirectories().FirstOrDefault(directory => directory.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? GetPortableFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static string NormalizeScript(string content) => string.Join('\n',
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0));

    private sealed record ImportedScriptCredentials(
        string? Username,
        string? Password,
        string? RootLogin,
        string? RootPassword,
        string? SourcePath,
        string Content)
    {
        public static ImportedScriptCredentials Empty { get; } = new(null, null, null, null, null, "");
    }

    public static int ReadCryptSaltMode(string sessionsDirectory)
    {
        var parent = Directory.GetParent(Path.GetFullPath(sessionsDirectory));
        if (parent is null) return 0;

        var iniPath = Directory.EnumerateFiles(parent.FullName, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("kitty.ini", StringComparison.OrdinalIgnoreCase));
        if (iniPath is null) return 0;

        foreach (var rawLine in File.ReadLines(iniPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || !line[..separator].Trim().Equals("cryptsalt", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line[(separator + 1)..].Trim(), out var mode) ? mode : 0;
        }
        return 0;
    }

    private static bool ReadBoolean(string? value, bool defaultValue) => value?.Trim() switch
    {
        "0" => false,
        "1" => true,
        _ when bool.TryParse(value, out var parsed) => parsed,
        _ => defaultValue
    };

    public static Dictionary<string, string> ParseFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var first = raw.IndexOf('\\');
            var last = raw.LastIndexOf('\\');
            if (first <= 0 || last <= first) continue;
            result[raw[..first]] = DecodeKiTTYValue(raw[(first + 1)..last]);
        }
        return result;
    }

    public static string DecodeSessionName(string fileName) => DecodeKiTTYValue(fileName);

    // KiTTY's mungestr uses percent escapes but, unlike form URL encoding,
    // leaves '+' unchanged. WebUtility.UrlDecode would corrupt such values.
    private static string DecodeKiTTYValue(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (!TryReadEscapedByte(value, index, out _))
            {
                result.Append(value[index++]);
                continue;
            }

            var bytes = new List<byte>();
            while (TryReadEscapedByte(value, index, out var escaped))
            {
                bytes.Add(escaped);
                index += 3;
            }
            result.Append(DecodeBytes(bytes.ToArray()));
        }
        return result.ToString();
    }

    private static string DecodeBytes(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static bool TryReadEscapedByte(string value, int index, out byte result)
    {
        result = 0;
        return index + 2 < value.Length && value[index] == '%' &&
               byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}

namespace KiTTYManager.Core;

public static class WinScpLaunchPlan
{
    public static IReadOnlyList<string> BuildArguments(ManagedServer server, int localPort,
        string? password = null, string? passphrase = null)
    {
        if (localPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        var user = Uri.EscapeDataString(server.EffectiveUsername);
        var result = new List<string>
        {
            $"sftp://{(user.Length == 0 ? "" : user + "@")}127.0.0.1:{localPort}/"
        };
        var hostKey = FormatHostKey(server);
        if (hostKey.Length > 0) result.Add($"/hostkey={hostKey}");
        if (!string.IsNullOrWhiteSpace(server.PrivateKeyPath))
        {
            var keyPath = ManagerPathResolver.ResolveOptionalExistingFile(server.PrivateKeyPath, "SSH-ключ");
            if (keyPath is not null) result.Add($"/privatekey={keyPath}");
        }
        // Пароль передаётся напрямую, как это делает KiTTY при открытии WinSCP из сессии.
        if (!string.IsNullOrEmpty(password)) result.Add($"/password={password}");
        if (!string.IsNullOrEmpty(passphrase)) result.Add($"/passphrase={passphrase}");
        return result;
    }

    internal static string FormatHostKey(ManagedServer server)
    {
        var fingerprint = server.HostKeyFingerprint.Trim();
        if (fingerprint.Length == 0) return "";
        // Один старый fingerprint без типа и длины ключа не является полным
        // каноническим host key WinSCP. Не передаём двусмысленное значение.
        if (server.HostKeyAlgorithm.Length == 0 || server.HostKeyBits <= 0) return "";
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            // WinSCP сверяет base64 посимвольно: без паддинга «=» ключ «не совпадает»,
            // хотя отпечаток тот же.
            var base64 = fingerprint[7..];
            var padding = base64.Length % 4;
            if (padding > 0) base64 += new string('=', 4 - padding);
            // WinSCP требует полный формат «алгоритм биты SHA256:base64».
            // Без алгоритма/бит он не может сопоставить ключ — лучше не передавать /hostkey.
            // ed25519: некоторые инструменты хранят 256 бит, WinSCP ожидает 255.
            var bits = NormalizeBits(server.HostKeyAlgorithm, server.HostKeyBits);
            // В полном формате WinSCP ожидает base64 без маркера SHA256:.
            // Маркер допустим у отдельного fingerprint, но внутри
            // "algorithm bits fingerprint" становится частью ожидаемого значения
            // и приводит к ложному «хост-ключ не соответствует».
            return $"{server.HostKeyAlgorithm.Trim()} {bits} {base64}";
        }
        return $"{server.HostKeyAlgorithm.Trim()} {NormalizeBits(server.HostKeyAlgorithm, server.HostKeyBits)} {fingerprint}";
    }

    // ed25519: PuTTY/KiTTY хранят 256, WinSCP ожидает 255. Нормализуем.
    internal static int NormalizeBits(string algorithm, int bits) =>
        algorithm.Trim().Equals("ssh-ed25519", StringComparison.OrdinalIgnoreCase) && bits == 256 ? 255 : bits;

    public static string? FindExecutable(string configuredPath, string appDirectory,
        string programFiles, string programFilesX86)
    {
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(appDirectory, "WinSCP.exe"),
            string.IsNullOrWhiteSpace(programFiles) ? "" : Path.Combine(programFiles, "WinSCP", "WinSCP.exe"),
            string.IsNullOrWhiteSpace(programFilesX86) ? "" : Path.Combine(programFilesX86, "WinSCP", "WinSCP.exe")
        };
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path) ? path : Path.GetFullPath(path, appDirectory))
            .FirstOrDefault(File.Exists);
    }
}

public sealed class WinScpCredentialFiles : IDisposable
{
    public string? PasswordPath { get; }
    public string? PassphrasePath { get; }

    private WinScpCredentialFiles(string? passwordPath, string? passphrasePath) =>
        (PasswordPath, PassphrasePath) = (passwordPath, passphrasePath);

    public static WinScpCredentialFiles Create(ManagedServer server, string directory)
    {
        Directory.CreateDirectory(directory);
        return new(WriteSecret(directory, server.Password),
            WriteSecret(directory, server.PrivateKeyPassphrase));
    }

    private static string? WriteSecret(string directory, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        var path = Path.Combine(directory, "winscp-secret-" + Guid.NewGuid().ToString("N") + ".tmp");
        // BOM нужен, чтобы WinSCP однозначно читал файл как UTF-8, а не как ANSI.
        File.WriteAllText(path, secret, new System.Text.UTF8Encoding(true));
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
        return path;
    }

    public static void CleanupStale(string directory, DateTime utcNow)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "winscp-secret-*.tmp"))
            try
            {
                if (File.GetLastWriteTimeUtc(path) < utcNow.AddHours(-1)) File.Delete(path);
            }
            catch { }
    }

    public void Dispose()
    {
        foreach (var path in new[] { PasswordPath, PassphrasePath })
            if (path is not null) try { File.Delete(path); } catch { }
    }
}

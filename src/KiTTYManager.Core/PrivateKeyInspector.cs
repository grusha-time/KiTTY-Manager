using System.Text;

namespace KiTTYManager.Core;

public sealed record PrivateKeyMetadata(
    bool Present,
    bool Resolved,
    string Format,
    bool? Encrypted);

public static class PrivateKeyInspector
{
    public static PrivateKeyMetadata Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(false, false, "none", null);
        if (!File.Exists(path))
            return new(true, false, "missing", null);

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
            var buffer = new char[16 * 1024];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, count);
            return InspectContent(content);
        }
        catch (IOException)
        {
            return new(true, true, "unreadable", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(true, true, "unreadable", null);
        }
    }

    private static PrivateKeyMetadata InspectContent(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        var first = lines.FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? "";

        const string puttyPrefix = "PuTTY-User-Key-File-";
        if (first.StartsWith(puttyPrefix, StringComparison.Ordinal))
        {
            var separator = first.IndexOf(':', puttyPrefix.Length);
            var version = separator > puttyPrefix.Length
                ? first[puttyPrefix.Length..separator]
                : "unknown";
            var encryption = lines.FirstOrDefault(line =>
                line.StartsWith("Encryption:", StringComparison.OrdinalIgnoreCase));
            var value = encryption?[(encryption.IndexOf(':') + 1)..].Trim();
            return new(true, true, $"putty-ppk-v{version}", value is null
                ? null
                : !value.Equals("none", StringComparison.OrdinalIgnoreCase));
        }

        if (first.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "pem-pkcs8", true);
        if (first.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "openssh", null);
        if (first.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "pem-pkcs8", false);
        if (first.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "pem-rsa", IsTraditionalPemEncrypted(lines));
        if (first.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "pem-ec", IsTraditionalPemEncrypted(lines));
        if (first.Contains("BEGIN DSA PRIVATE KEY", StringComparison.Ordinal))
            return new(true, true, "pem-dsa", IsTraditionalPemEncrypted(lines));
        return new(true, true, "unknown", null);
    }

    private static bool IsTraditionalPemEncrypted(IEnumerable<string> lines) =>
        lines.Any(line => line.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase));
}

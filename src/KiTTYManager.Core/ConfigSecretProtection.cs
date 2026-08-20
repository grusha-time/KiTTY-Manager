using System.Security.Cryptography;
using System.Text;

namespace KiTTYManager.Core;

internal interface IConfigSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

internal sealed class DpapiConfigSecretProtector : IConfigSecretProtector
{
    internal const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("KiTTYManager.ConfigSecrets.v1");

    public string Protect(string plaintext)
    {
        if (plaintext.Length == 0) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Шифрование config.json поддерживается только в Windows.");
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0 ||
            !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Расшифровка config.json поддерживается только в Windows.");
        try
        {
            var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            throw new InvalidDataException(
                "Не удалось расшифровать секрет из config.json. " +
                "Файл должен открываться той же учётной записью Windows, которая его сохранила.",
                exception);
        }
    }

    internal static bool IsProtected(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);
}

internal static class ConfigSecrets
{
    public static void Protect(ManagerConfig config, IConfigSecretProtector protector) =>
        Transform(config, protector.Protect);

    public static void Unprotect(ManagerConfig config, IConfigSecretProtector protector) =>
        Transform(config, protector.Unprotect);

    public static bool ContainsPlaintext(ManagerConfig config)
    {
        var found = false;
        Visit(config, value =>
        {
            if (value.Length > 0 && !DpapiConfigSecretProtector.IsProtected(value))
                found = true;
            return value;
        });
        return found;
    }

    private static void Transform(ManagerConfig config, Func<string, string> transform) =>
        Visit(config, transform);

    private static void Visit(ManagerConfig config, Func<string, string> transform)
    {
        foreach (var proxy in config.BaseProxies)
        {
            proxy.TotpSecret = transform(proxy.TotpSecret ?? "");
            proxy.PostLoginCommand = transform(proxy.PostLoginCommand ?? "");
        }

        foreach (var server in config.AllServers())
        {
            server.Password = transform(server.Password ?? "");
            server.PrivateKeyPassphrase = transform(server.PrivateKeyPassphrase ?? "");
            server.RootLogin = transform(server.RootLogin ?? "");
            server.RootPassword = transform(server.RootPassword ?? "");
            server.ImportedCommand = transform(server.ImportedCommand ?? "");
            server.SourceScriptContent = transform(server.SourceScriptContent ?? "");

            if (server.KittyBaseline is not null)
            {
                server.KittyBaseline.Password = transform(server.KittyBaseline.Password ?? "");
                server.KittyBaseline.RootLogin = transform(server.KittyBaseline.RootLogin ?? "");
                server.KittyBaseline.RootPassword = transform(server.KittyBaseline.RootPassword ?? "");
                server.KittyBaseline.ImportedCommand =
                    transform(server.KittyBaseline.ImportedCommand ?? "");
            }

            foreach (var web in server.WebInterfaces)
                web.Password = transform(web.Password ?? "");
        }
    }
}

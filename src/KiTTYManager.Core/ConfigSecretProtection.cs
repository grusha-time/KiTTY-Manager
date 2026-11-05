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
    internal const string V1Prefix = "dpapi:v1:";
    internal const string V2Prefix = "dpapi:v2:";
    internal const string Prefix = V2Prefix;

    private static readonly byte[] V1Entropy =
        Encoding.UTF8.GetBytes("KiTTYManager.ConfigSecrets.v1");
    private const string V2EntropyPrefix = "KiTTYManager.ConfigSecrets.v2:";

    private readonly Func<byte[], byte[], DataProtectionScope, byte[]> _protectData;
    private readonly Func<byte[], byte[], DataProtectionScope, byte[]> _unprotectData;
    private readonly Func<string> _getUserSid;

    public DpapiConfigSecretProtector()
        : this(
            (userData, optionalEntropy, scope) =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException(
                        "Шифрование config.json поддерживается только в Windows.");
                return ProtectedData.Protect(userData, optionalEntropy, scope);
            },
            (encryptedData, optionalEntropy, scope) =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException(
                        "Расшифровка config.json поддерживается только в Windows.");
                return ProtectedData.Unprotect(encryptedData, optionalEntropy, scope);
            },
            GetCurrentUserSid)
    {
    }

    internal DpapiConfigSecretProtector(
        Func<byte[], byte[], DataProtectionScope, byte[]> protectData,
        Func<byte[], byte[], DataProtectionScope, byte[]> unprotectData,
        Func<string> getUserSid)
    {
        _protectData = protectData ?? throw new ArgumentNullException(nameof(protectData));
        _unprotectData = unprotectData ?? throw new ArgumentNullException(nameof(unprotectData));
        _getUserSid = getUserSid ?? throw new ArgumentNullException(nameof(getUserSid));
    }

    private static string GetCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Получение SID пользователя поддерживается только в Windows.");

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity?.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException(
                "Не удалось определить SID текущего пользователя Windows для защиты секретов.");
        return sid;
    }

    private byte[] GetV2Entropy()
    {
        var sid = _getUserSid();
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException(
                "Не удалось определить SID текущего пользователя Windows для защиты секретов.");
        return Encoding.UTF8.GetBytes(V2EntropyPrefix + sid);
    }

    public string Protect(string plaintext)
    {
        if (plaintext.Length == 0) return "";
        var bytes = _protectData(
            Encoding.UTF8.GetBytes(plaintext),
            GetV2Entropy(),
            DataProtectionScope.LocalMachine);
        return V2Prefix + Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return protectedValue;

        if (protectedValue.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            return UnprotectPayload(
                protectedValue[V2Prefix.Length..],
                GetV2Entropy(),
                DataProtectionScope.LocalMachine);
        }

        if (protectedValue.StartsWith(V1Prefix, StringComparison.Ordinal))
        {
            return UnprotectPayload(
                protectedValue[V1Prefix.Length..],
                V1Entropy,
                DataProtectionScope.CurrentUser);
        }

        return protectedValue;
    }

    private string UnprotectPayload(string base64Payload, byte[] entropy, DataProtectionScope scope)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Payload);
            return Encoding.UTF8.GetString(_unprotectData(bytes, entropy, scope));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Не удалось прочитать защищённые данные из config.json: повреждён формат Base64.",
                exception);
        }
        catch (CryptographicException exception)
        {
            var hex = $"0x{exception.HResult:X8}";
            throw new InvalidDataException(
                $"Не удалось расшифровать секрет из config.json через Windows DPAPI ({hex}). " +
                "Файл должен открываться на том же компьютере и под той же учётной записью Windows, в которой он был сохранён.",
                exception);
        }
    }

    internal static bool IsProtected(string value) =>
        value.StartsWith(V2Prefix, StringComparison.Ordinal) ||
        value.StartsWith(V1Prefix, StringComparison.Ordinal);
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

    public static bool NeedsMigration(ManagerConfig config)
    {
        var found = false;
        Visit(config, value =>
        {
            if (value.Length > 0 && (!DpapiConfigSecretProtector.IsProtected(value) || value.StartsWith(DpapiConfigSecretProtector.V1Prefix, StringComparison.Ordinal)))
                found = true;
            return value;
        });
        return found;
    }

    public static bool TryGetCryptographicFailure(Exception exception, out string? hresultHex)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is CryptographicException cryptoEx)
            {
                hresultHex = $"0x{cryptoEx.HResult:X8}";
                return true;
            }
        }
        hresultHex = null;
        return false;
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
            server.PamPassword = transform(server.PamPassword ?? "");
            server.PamTotpSecret = transform(server.PamTotpSecret ?? "");
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


using System.Security.Cryptography;
using System.Text;

namespace KiTTYManager.Core;

public static class KittyChangeIgnore
{
    public static string Fingerprint(string propertyName, string managerValue, string kittyValue)
    {
        static string Part(string value) => $"{value.Length}:{value}";
        var value = $"M{Part(propertyName)}{Part(managerValue)}K{Part(kittyValue)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static bool Matches(ManagedServer server, string propertyName, string managerValue, string kittyValue)
    {
        var fingerprint = Fingerprint(propertyName, managerValue, kittyValue);
        return server.IgnoredKittyChanges.Any(change =>
            change.PropertyName.Equals(propertyName, StringComparison.Ordinal) &&
            change.Fingerprint.Equals(fingerprint, StringComparison.Ordinal));
    }

    public static void Remember(ManagedServer server, string propertyName, string managerValue, string kittyValue)
    {
        server.IgnoredKittyChanges.RemoveAll(change =>
            change.PropertyName.Equals(propertyName, StringComparison.Ordinal));
        server.IgnoredKittyChanges.Add(new IgnoredKittyChange
        {
            PropertyName = propertyName,
            Fingerprint = Fingerprint(propertyName, managerValue, kittyValue)
        });
    }
}

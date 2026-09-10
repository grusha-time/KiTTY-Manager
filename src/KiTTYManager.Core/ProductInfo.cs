namespace KiTTYManager.Core;

public static class ProductInfo
{
    public const string Version = "2.0.0";
}

public static class JumphostStartupOfferPolicy
{
    public static IReadOnlyList<BaseProxy> MissingConfigured(
        ManagerConfig config, IReadOnlySet<Guid> readyProxyIds) =>
        config.BaseProxies
            .Where(proxy => proxy.Enabled && proxy.StartupServerId is not null &&
                            !readyProxyIds.Contains(proxy.Id))
            .ToArray();

    public static string DisplayName(ManagerConfig config, BaseProxy proxy) =>
        proxy.StartupServerId is Guid serverId && config.FindServer(serverId) is { } server &&
        !string.IsNullOrWhiteSpace(server.Name)
            ? server.Name
            : proxy.Name;
}

public static class ManagerConfigMigration
{
    public static bool UpgradeToVersion8(ManagerConfig config)
    {
        if (config.SchemaVersion >= 8) return false;
        config.SchemaVersion = 8;
        return true;
    }

    public static bool UpgradeToVersion9(ManagerConfig config)
    {
        if (config.SchemaVersion >= 9) return false;
        foreach (var server in config.AllServers())
            foreach (var endpoint in server.BackupEndpoints ?? []) endpoint.InternalOnly = false;
        config.SchemaVersion = 9;
        return true;
    }
}

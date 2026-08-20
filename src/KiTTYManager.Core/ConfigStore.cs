using System.Text.Json;
using System.Collections.Concurrent;

namespace KiTTYManager.Core;

public static class ConfigStore
{
    private static readonly ConcurrentDictionary<string, object> SaveLocks =
        new(StringComparer.OrdinalIgnoreCase);
    internal static IConfigSecretProtector SecretProtector { get; set; } =
        new DpapiConfigSecretProtector();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Base directory for resolving relative paths (the manager's exe folder).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static ManagerConfig Load(string path, bool migratePlaintextSecrets = false)
    {
        if (!File.Exists(path)) return new ManagerConfig();
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ManagerConfig>(json, Options)
            ?? throw new InvalidDataException("Пустой или повреждённый файл конфигурации.");
        using (var document = JsonDocument.Parse(json))
            if (!document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals(nameof(ManagerConfig.SchemaVersion), StringComparison.OrdinalIgnoreCase)))
                config.SchemaVersion = 0;
        Normalize(config);
        var needsMigration = ConfigSecrets.ContainsPlaintext(config);
        ConfigSecrets.Unprotect(config, SecretProtector);
        ResolvePaths(config);
        if (migratePlaintextSecrets && needsMigration)
            Save(path, config);
        return config;
    }

    public static void Save(string path, ManagerConfig config)
    {
        var fullPath = Path.GetFullPath(path);
        lock (SaveLocks.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Clone and make paths portable so the config is relocatable.
            var portable = CloneForSave(config);
            MakePathsPortable(portable);
            ConfigSecrets.Protect(portable, SecretProtector);
            WriteAtomically(fullPath, JsonSerializer.Serialize(portable, Options));
        }
    }

    public static ManagerConfig Import(string path) => Load(path);
    public static void Export(string path, ManagerConfig config)
    {
        var fullPath = Path.GetFullPath(path);
        lock (SaveLocks.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var portable = CloneForSave(config);
            MakePathsPortable(portable);
            WriteAtomically(fullPath, JsonSerializer.Serialize(portable, Options));
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Converts absolute paths inside <paramref name="baseDir"/> to relative paths
    /// so the config can be moved to another folder without breaking.
    /// </summary>
    public static void MakePathsPortable(ManagerConfig config)
    {
        foreach (var server in config.AllServers())
        {
            server.SourceSessionPath = ToRelative(server.SourceSessionPath);
            server.SourceScriptPath = ToRelative(server.SourceScriptPath);
            server.PrivateKeyPath = ToRelative(server.PrivateKeyPath) ?? "";
        }
    }

    /// <summary>
    /// Resolves relative paths to absolute using <see cref="BaseDirectory"/>.
    /// Also handles migration of stale absolute paths from moved installations.
    /// </summary>
    public static void ResolvePaths(ManagerConfig config)
    {
        foreach (var server in config.AllServers())
        {
            server.SourceSessionPath = ToAbsolute(server.SourceSessionPath);
            server.SourceScriptPath = ToAbsolute(server.SourceScriptPath);
            server.PrivateKeyPath = ToAbsolute(server.PrivateKeyPath) ?? "";
        }
    }

    private static string? ToRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!Path.IsPathRooted(path)) return path; // already relative
        try
        {
            var full = Path.GetFullPath(path);
            var baseFull = Path.GetFullPath(BaseDirectory);
            if (full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(baseFull, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(baseFull, full).Replace(Path.DirectorySeparatorChar, '/');

            // Path is outside current BaseDirectory (e.g. from a backup folder).
            // If it contains a known KiTTY marker, extract the tail and store
            // it as relative so ResolvePaths can find it in the new location.
            var markers = new[] { "KiTTY\\Sessions\\", "KiTTY\\Script\\", "KiTTY\\" };
            foreach (var marker in markers)
            {
                var idx = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var tail = full[idx..];
                // Verify the file exists in the current BaseDirectory.
                var candidate = Path.GetFullPath(Path.Combine(baseFull, tail));
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return tail.Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        catch { }
        return path;
    }

    private static string? ToAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (Path.IsPathRooted(path))
        {
            // Absolute path from config.  If it exists, keep it.
            if (File.Exists(path) || Directory.Exists(path)) return Path.GetFullPath(path);
            // Stale absolute path (folder was moved).  Try to relocate by
            // extracting the relative part after "KiTTY/" or "Data/" etc.
            var relocated = TryRelocateStalePath(path);
            if (relocated is not null) return relocated;
            // Cannot relocate — keep original path as-is (might be valid on
            // another machine or the file may appear later).
            return path;
        }
        // Keep portable values portable in the in-memory configuration. Callers
        // resolve them against BaseDirectory immediately before using them.
        // This avoids making the stored value depend on whether the target happened
        // to exist while the configuration was loaded.
        return path;
    }

    /// <summary>
    /// When the manager folder is moved, absolute paths in old configs point to
    /// the old location.  This method tries to find the file in the new location
    /// by matching the tail of the path (e.g. "KiTTY\Sessions\filename").
    /// </summary>
    private static string? TryRelocateStalePath(string absolutePath)
    {
        // Look for known subdirectory markers in the path.
        var markers = new[] { "KiTTY\\Sessions", "KiTTY\\Script", "KiTTY\\", "Data\\" };
        foreach (var marker in markers)
        {
            var idx = absolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var tail = absolutePath[idx..];
            var candidate = Path.GetFullPath(Path.Combine(BaseDirectory, tail));
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        // Last resort: try just the filename inside KiTTY\Sessions or KiTTY\Script.
        var fileName = Path.GetFileName(absolutePath);
        if (fileName.Length == 0) return null;
        foreach (var sub in new[] { "KiTTY/Sessions", "KiTTY/Script" })
        {
            var candidate = Path.GetFullPath(Path.Combine(BaseDirectory, sub, fileName));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static ManagerConfig CloneForSave(ManagerConfig config)
    {
        var json = JsonSerializer.Serialize(config, Options);
        return JsonSerializer.Deserialize<ManagerConfig>(json, Options)!;
    }

    private static void Normalize(ManagerConfig config)
    {
        // System.Text.Json can assign null to non-nullable collection properties
        // when a hand-edited/imported JSON explicitly contains null.
        config.Groups ??= [];
        config.UngroupedServers ??= [];
        config.Links ??= [];
        config.BaseProxies ??= [];
        foreach (var proxy in config.BaseProxies)
        {
            proxy.TotpSecret ??= "";
            proxy.TotpAlgorithm = proxy.TotpAlgorithm is "SHA1" or "SHA256" or "SHA512" ? proxy.TotpAlgorithm : "SHA1";
            proxy.TotpDigits = Math.Clamp(proxy.TotpDigits, 6, 8);
            proxy.TotpPeriodSeconds = Math.Clamp(proxy.TotpPeriodSeconds, 1, 300);
            proxy.TotpPrompt ??= "TOTP:";
            if (proxy.TotpSecret.Length > 0 && proxy.TotpPrompt == "Verification code:")
                proxy.TotpPrompt = "TOTP:";
            proxy.PostLoginCommand ??= "";
            if (string.IsNullOrWhiteSpace(proxy.PostLoginPasswordPrompt))
                proxy.PostLoginPasswordPrompt = "assword";
            proxy.PostCommandReadyDelaySeconds = proxy.PostCommandReadyDelaySeconds <= 0
                ? 180
                : Math.Clamp(proxy.PostCommandReadyDelaySeconds, 1, 600);
            proxy.AccessProbeServerLimit = proxy.AccessProbeServerLimit <= 0
                ? 5
                : Math.Clamp(proxy.AccessProbeServerLimit, 1, 20);
            proxy.AccessProbeServerIds ??= [];
        }
        config.ConnectionTimeoutSeconds = Math.Clamp(config.ConnectionTimeoutSeconds, 10, 600);
        config.EndpointProbeTimeoutSeconds = Math.Clamp(
            config.EndpointProbeTimeoutSeconds <= 0 ? 4 : config.EndpointProbeTimeoutSeconds, 1, 30);
        foreach (var server in config.UngroupedServers) Normalize(server);
        foreach (var group in config.Groups) Normalize(group);
        var validServerIds = config.AllServers().Select(server => server.Id).ToHashSet();
        var validProxyIds = config.BaseProxies.Select(proxy => proxy.Id).ToHashSet();
        foreach (var server in config.AllServers())
        {
            if (server.RequiredPreviousServerId is Guid requiredPreviousServerId &&
                (!validServerIds.Contains(requiredPreviousServerId) || requiredPreviousServerId == server.Id))
                server.RequiredPreviousServerId = null;
            if (server.PreferredProxyId is Guid preferredProxyId &&
                !validProxyIds.Contains(preferredProxyId))
                server.PreferredProxyId = null;
            server.EndpointPreferences = server.EndpointPreferences
                .Where(item =>
                    (item.ProxyId is not null) != (item.PreviousServerId is not null) &&
                    (item.ProxyId is null || item.ProxyId == Guid.Empty ||
                     validProxyIds.Contains(item.ProxyId.Value)) &&
                    (item.PreviousServerId is null || validServerIds.Contains(item.PreviousServerId.Value)))
                .ToList();
        }
        foreach (var link in config.Links)
        {
            link.ProxyStatistics ??= [];
            if (link.LastSuccessfulProxyId is Guid legacyProxyId &&
                validProxyIds.Contains(legacyProxyId) &&
                link.ProxyStatistics.All(item => item.ProxyId != legacyProxyId))
                link.ProxyStatistics.Add(new LinkProxyStatistic
                {
                    ProxyId = legacyProxyId,
                    LastSuccessUtc = link.LastSuccessUtc ?? DateTimeOffset.MinValue,
                    LatencyMs = link.LastLatencyMs,
                    Strategy = link.LastStrategy
                });
            link.ProxyStatistics = link.ProxyStatistics
                .Where(item => item is not null && validProxyIds.Contains(item.ProxyId))
                .GroupBy(item => item.ProxyId)
                .Select(group => group.OrderByDescending(item => item.LastSuccessUtc).First())
                .ToList();
            if (link.LastSuccessfulProxyId is Guid invalidLegacyProxyId &&
                !validProxyIds.Contains(invalidLegacyProxyId))
            {
                link.LastSuccessfulProxyId = null;
                link.LastLatencyMs = null;
            }
        }
        foreach (var proxy in config.BaseProxies)
            proxy.AccessProbeServerIds = proxy.AccessProbeServerIds
                .Where(id => id != Guid.Empty && validServerIds.Contains(id) &&
                             AccessGrantPolicy.IsEligibleControl(config.FindServer(id), proxy))
                .Distinct()
                .Take(proxy.AccessProbeServerLimit)
                .ToList();
    }

    private static void Normalize(ServerGroup group)
    {
        group.Groups ??= [];
        group.Servers ??= [];
        foreach (var server in group.Servers) Normalize(server);
        foreach (var child in group.Groups) Normalize(child);
    }

    private static void Normalize(ManagedServer server)
    {
        server.Host = server.Host.Trim();
        if (server.KittyBaseline is not null) server.KittyBaseline.Host = server.KittyBaseline.Host.Trim();
        server.ShellPrompt = string.IsNullOrWhiteSpace(server.ShellPrompt) ? "$" : server.ShellPrompt;
        server.ImportedCommand ??= "";
        server.WebInterfaces ??= [];
        foreach (var web in server.WebInterfaces)
            web.ResolverAddress = (web.ResolverAddress ?? "").Trim();
        server.BackupEndpoints ??= [];
        server.EndpointPreferences ??= [];
        server.BackupEndpoints = server.BackupEndpoints
            .Where(endpoint => endpoint is not null && endpoint.Port > 0)
            .Select(endpoint => new ServerEndpoint((endpoint.Host ?? "").Trim(), endpoint.Port))
            .ToList();
        if (server.PreferredEndpoint is not null && server.PreferredEndpoint.Port <= 0)
            server.PreferredEndpoint = null;
        if (server.PreferredEndpoint is not null)
        {
            server.PreferredEndpoint.Host = (server.PreferredEndpoint.Host ?? "").Trim();
            // Запомненный адрес должен входить в {основной}∪{резервные}; иначе
            // сбрасываем, чтобы правки адресов применялись после перезапуска.
            var resolvedPreferred = new ServerEndpoint(
                string.IsNullOrWhiteSpace(server.PreferredEndpoint.Host) ? server.CleanHost : server.PreferredEndpoint.Host,
                server.PreferredEndpoint.Port);
            var main = new ServerEndpoint(server.CleanHost, server.Port);
            var inSet = resolvedPreferred.Equals(main) || server.BackupEndpoints.Any(backup =>
                new ServerEndpoint(
                    string.IsNullOrWhiteSpace(backup.Host) ? server.CleanHost : backup.Host,
                    backup.Port).Equals(resolvedPreferred));
            if (!inSet) server.PreferredEndpoint = null;
        }
        var allowedEndpoints = ServerEndpointPolicy.Ordered(server).ToHashSet();
        server.EndpointPreferences = server.EndpointPreferences
            .Where(item => item is not null && item.Endpoint is not null && item.Endpoint.Port > 0)
            .Select(item => new EndpointPreference
            {
                ProxyId = item.ProxyId,
                PreviousServerId = item.PreviousServerId,
                Endpoint = new ServerEndpoint((item.Endpoint.Host ?? "").Trim(), item.Endpoint.Port),
                LastSuccessUtc = item.LastSuccessUtc
            })
            .Where(item => allowedEndpoints.Contains(new ServerEndpoint(
                string.IsNullOrWhiteSpace(item.Endpoint.Host) ? server.CleanHost : item.Endpoint.Host,
                item.Endpoint.Port)))
            .DistinctBy(item => (item.ProxyId, item.PreviousServerId))
            .ToList();
        server.ManagerOverrides ??= [];
        server.IgnoredKittyProperties ??= [];
        server.IgnoredKittyChanges ??= [];
        server.ManagerOverrides = server.ManagerOverrides
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        server.IgnoredKittyProperties = server.IgnoredKittyProperties
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToList();
        server.IgnoredKittyChanges = server.IgnoredKittyChanges
            .Where(value => !string.IsNullOrWhiteSpace(value.PropertyName) && !string.IsNullOrWhiteSpace(value.Fingerprint))
            .GroupBy(value => value.PropertyName, StringComparer.Ordinal)
            .Select(group => group.Last()).ToList();
        if (server.RequiredPreviousServerId == server.Id)
            server.RequiredPreviousServerId = null;
        if (server.PreferredRoute is not null)
        {
            server.PreferredRoute.ServerIds ??= [];
            if (server.PreferredRoute.ProxyId == Guid.Empty &&
                (!server.TryDirectWithoutJumphost ||
                 server.PreferredRoute.ServerIds.Count != 1) ||
                server.PreferredRoute.ServerIds.Count == 0 ||
                server.PreferredRoute.ServerIds[^1] != server.Id ||
                server.PreferredRoute.ProxyId != Guid.Empty &&
                server.RequiredPreviousServerId is Guid required &&
                (server.PreferredRoute.ServerIds.Count < 2 ||
                 server.PreferredRoute.ServerIds[^2] != required))
                server.PreferredRoute = null;
        }
    }
}

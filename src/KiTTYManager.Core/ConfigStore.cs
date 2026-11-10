using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KiTTYManager.Core;

public static class ConfigStore
{
    private static readonly ConcurrentDictionary<string, object> SaveLocks =
        new(StringComparer.OrdinalIgnoreCase);
    internal static IConfigSecretProtector SecretProtector { get; set; } =
        new DpapiConfigSecretProtector();

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Base directory for resolving relative paths (the manager's exe folder).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static bool TryGetCryptographicFailure(Exception exception, out string? hresultHex) =>
        ConfigSecrets.TryGetCryptographicFailure(exception, out hresultHex);

    public static ManagerConfig Load(string path, bool migratePlaintextSecrets = false, bool requireExisting = false)
    {
        if (!requireExisting && !File.Exists(path)) return new ManagerConfig();
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException) when (!requireExisting)
        {
            return new ManagerConfig();
        }
        var config = JsonSerializer.Deserialize<ManagerConfig>(json, Options)
            ?? throw new InvalidDataException("Пустой или повреждённый файл конфигурации.");
        using (var document = JsonDocument.Parse(json))
            if (!document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals(nameof(ManagerConfig.SchemaVersion), StringComparison.OrdinalIgnoreCase)))
                config.SchemaVersion = 0;
        Normalize(config);
        var needsMigration = ConfigSecrets.NeedsMigration(config);
        ConfigSecrets.Unprotect(config, SecretProtector);
        ResolvePaths(config);
        if (migratePlaintextSecrets && needsMigration)
            Save(path, config);
        return config;
    }

    public static void Save(string path, ManagerConfig config)
    {
        var snapshot = CloneForSave(config);
        SaveSnapshot(path, snapshot);
    }

    public static void SaveSnapshot(string path, ManagerConfig snapshot)
    {
        var fullPath = Path.GetFullPath(path);
        lock (SaveLocks.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Snapshot is already cloned before passing here.
            foreach (var server in snapshot.AllServers()) NormalizePamDefaults(server);
            MakePathsPortable(snapshot);
            ConfigSecrets.Protect(snapshot, SecretProtector);
            WriteAtomically(fullPath, stream => JsonSerializer.Serialize(stream, snapshot, Options), secureAclRequired: true);
        }
    }

    public static ManagerConfig Import(string path)
    {
        if (!File.Exists(path)) return new ManagerConfig();

        var config = Load(path);

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(document.RootElement, "UngroupedServers", out var ungroupedEl))
                {
                    MatchServers(config.UngroupedServers, ungroupedEl);
                }

                if (TryGetPropertyIgnoreCase(document.RootElement, "Groups", out var groupsEl))
                {
                    MatchGroups(config.Groups, groupsEl);
                }
            }
        }
        catch
        {
            // If structural matching fails, config remains as loaded
        }

        return config;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void MatchServers(IList<ManagedServer> servers, JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array) return;
        var count = Math.Min(servers.Count, arrayElement.GetArrayLength());
        var index = 0;
        foreach (var serverElement in arrayElement.EnumerateArray())
        {
            if (index >= count) break;
            ApplyOmittedPamDefaults(servers[index], serverElement);
            index++;
        }
    }

    private static void MatchGroups(IList<ServerGroup> groups, JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array) return;
        var count = Math.Min(groups.Count, arrayElement.GetArrayLength());
        var index = 0;
        foreach (var groupElement in arrayElement.EnumerateArray())
        {
            if (index >= count) break;
            var group = groups[index];
            if (groupElement.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(groupElement, "Servers", out var serversEl))
                {
                    MatchServers(group.Servers, serversEl);
                }

                if (TryGetPropertyIgnoreCase(groupElement, "Groups", out var childGroupsEl))
                {
                    MatchGroups(group.Groups, childGroupsEl);
                }
            }
            index++;
        }
    }

    private static void ApplyOmittedPamDefaults(ManagedServer server, JsonElement serverElement)
    {
        if (serverElement.ValueKind != JsonValueKind.Object) return;

        var propUsePam = false;
        var propAutoConfirm = false;
        var propSshCommand = false;
        var propPassword = false;
        var propSecret = false;
        var propAlgorithm = false;
        var propDigits = false;
        var propPeriod = false;
        var propPrompt = false;
        var propConfirm = false;

        var hasNonEmptyAlgorithm = false;
        var hasNonZeroDigits = false;
        var hasNonZeroPeriod = false;
        var hasNonEmptyPrompt = false;
        var hasNonEmptyConfirm = false;

        foreach (var prop in serverElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "UsePam", StringComparison.OrdinalIgnoreCase))
                propUsePam = true;
            else if (string.Equals(prop.Name, "PamAutoConfirm", StringComparison.OrdinalIgnoreCase))
                propAutoConfirm = true;
            else if (string.Equals(prop.Name, "PamSshCommand", StringComparison.OrdinalIgnoreCase))
                propSshCommand = true;
            else if (string.Equals(prop.Name, "PamPassword", StringComparison.OrdinalIgnoreCase))
                propPassword = true;
            else if (string.Equals(prop.Name, "PamTotpSecret", StringComparison.OrdinalIgnoreCase))
                propSecret = true;
            else if (string.Equals(prop.Name, "PamTotpAlgorithm", StringComparison.OrdinalIgnoreCase))
            {
                propAlgorithm = true;
                hasNonEmptyAlgorithm = prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString());
            }
            else if (string.Equals(prop.Name, "PamTotpDigits", StringComparison.OrdinalIgnoreCase))
            {
                propDigits = true;
                hasNonZeroDigits = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var d) && d > 0;
            }
            else if (string.Equals(prop.Name, "PamTotpPeriodSeconds", StringComparison.OrdinalIgnoreCase))
            {
                propPeriod = true;
                hasNonZeroPeriod = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var p) && p > 0;
            }
            else if (string.Equals(prop.Name, "PamTotpPrompt", StringComparison.OrdinalIgnoreCase))
            {
                propPrompt = true;
                hasNonEmptyPrompt = prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString());
            }
            else if (string.Equals(prop.Name, "PamConfirmPrompt", StringComparison.OrdinalIgnoreCase))
            {
                propConfirm = true;
                hasNonEmptyConfirm = prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString());
            }
        }

        var allTenPamPropertiesPresent = propUsePam && propAutoConfirm && propSshCommand && propPassword && propSecret &&
                                         propAlgorithm && propDigits && propPeriod && propPrompt && propConfirm;
        server.HasAllPamPropertiesPresentInImport = allTenPamPropertiesPresent;

        if (allTenPamPropertiesPresent && server.IsFullUnconfiguredPamExport())
        {
            server.HasExplicitUsePam = false;
            server.HasExplicitPamAutoConfirm = false;
            server.PamTotpAlgorithm = "";
            server.PamTotpDigits = 0;
            server.PamTotpPeriodSeconds = 0;
            server.PamTotpPrompt = "";
            server.PamConfirmPrompt = "";
            return;
        }

        server.HasExplicitUsePam = propUsePam;
        server.HasExplicitPamAutoConfirm = propAutoConfirm;
        if (!hasNonEmptyAlgorithm) server.PamTotpAlgorithm = "";
        if (!hasNonZeroDigits) server.PamTotpDigits = 0;
        if (!hasNonZeroPeriod) server.PamTotpPeriodSeconds = 0;
        if (!hasNonEmptyPrompt) server.PamTotpPrompt = "";
        if (!hasNonEmptyConfirm) server.PamConfirmPrompt = "";
    }

    public static void Export(string path, ManagerConfig config)
    {
        var fullPath = Path.GetFullPath(path);
        lock (SaveLocks.GetOrAdd(fullPath, _ => new object()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var portable = CloneForSave(config);
            foreach (var server in portable.AllServers()) NormalizePamDefaults(server);
            MakePathsPortable(portable);
            WriteAtomically(fullPath, stream => JsonSerializer.Serialize(stream, portable, Options), secureAclRequired: false);
        }
    }

    public static void CreateSecuredBackup(string sourcePath, string backupPath)
    {
        var content = File.ReadAllText(sourcePath);
        WriteAtomically(backupPath, content, secureAclRequired: true);
    }

    internal static Action<FileInfo>? PreWriteAclHook { get; set; }
    internal static bool ForceWindowsAclForTesting { get; set; }

    private static void WriteAtomically(string path, Action<Stream> writeAction, bool secureAclRequired = true)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (secureAclRequired && (OperatingSystem.IsWindows() || ForceWindowsAclForTesting))
            {
                var fileInfo = new FileInfo(temp);
                FileStream stream;
                if (OperatingSystem.IsWindows())
                {
                    var fileSecurity = CreateRestrictedFileSecurity();
                    stream = FileSystemAclExtensions.Create(
                        fileInfo,
                        FileMode.CreateNew,
                        FileSystemRights.FullControl,
                        FileShare.None,
                        4096,
                        FileOptions.None,
                        fileSecurity);
                }
                else
                {
                    stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                }

                using (stream)
                {
                    // Enforce and verify ACLs on the open temporary file before writing any secret content
                    EnsureFileSecurityEnforced(fileInfo);
                    writeAction(stream);
                    stream.Flush();
                }

                // All security operations completed before replacement; same-volume rename preserves file security
                File.Move(temp, path, true);
            }
            else
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    writeAction(stream);
                    stream.Flush();
                }
                File.Move(temp, path, true);
            }
        }
        catch (Exception ex) when (secureAclRequired &&
                                  (OperatingSystem.IsWindows() || ForceWindowsAclForTesting) &&
                                  (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException))
        {
            throw new InvalidOperationException($"Cannot securely save configuration: restrictive file permissions could not be established ({ex.Message}). KiTTY Manager requires an NTFS volume with access control to protect encrypted secrets.", ex);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    private static void WriteAtomically(string path, string content, bool secureAclRequired = true) =>
        WriteAtomically(path, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
            writer.Write(content);
            writer.Flush();
        }, secureAclRequired);

    private static void EnsureFileSecurityEnforced(FileInfo fileInfo)
    {
        PreWriteAclHook?.Invoke(fileInfo);
        if (OperatingSystem.IsWindows())
        {
            var fileSecurity = CreateRestrictedFileSecurity();
            fileInfo.SetAccessControl(fileSecurity);
            var actual = fileInfo.GetAccessControl(AccessControlSections.Access);
            if (actual is null || !actual.AreAccessRulesProtected)
                throw new PlatformNotSupportedException("The destination volume does not enforce NTFS access control lists.");
        }
    }

    private static FileSecurity CreateRestrictedFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity?.User is null)
            throw new InvalidOperationException("Cannot determine current user SID for access control.");

        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            identity.User,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            adminSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        return fileSecurity;
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

    public static string? ResolveExistingSessionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path))
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        else
        {
            try
            {
                var candidate = Path.Combine(BaseDirectory, path);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        try
        {
            var relocated = TryRelocateStalePath(path);
            if (relocated is not null && File.Exists(relocated)) return relocated;
        }
        catch { }
        return null;
    }

    public static ManagerConfig CloneForSave(ManagerConfig config) => config.CloneForSave();

    public static void Normalize(ManagerConfig config)
    {
        // System.Text.Json can assign null to non-nullable collection properties
        // when a hand-edited/imported JSON explicitly contains null.
        config.Groups ??= [];
        config.UngroupedServers ??= [];
        config.Links ??= [];
        config.BaseProxies ??= [];
        config.PendingFirefoxContainerCleanups = (config.PendingFirefoxContainerCleanups ?? [])
            .Where(t => t is not null && t.ServerId != Guid.Empty)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Id) ? $"{t.ServerId:N}_{t.WebId}" : t.Id)
            .Select(g => g.First())
            .ToList();
        foreach (var task in config.PendingFirefoxContainerCleanups)
            if (string.IsNullOrWhiteSpace(task.Id)) task.Id = Guid.NewGuid().ToString("N");
        ManagerConfigMigration.UpgradeToVersion9(config);
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
        config.ConnectionTimeoutSeconds = Math.Clamp(config.ConnectionTimeoutSeconds, 3, 600);
        config.MaxRouteAttempts = RouteAttemptBudget.NormalizeLimit(config.MaxRouteAttempts);
        config.MaxGroupServersInRouteAttempts = Math.Clamp(config.MaxGroupServersInRouteAttempts, 0, 100);
        config.TaskConnectionRecoveryMinutes = Math.Clamp(config.TaskConnectionRecoveryMinutes, 0, 99999);
        if (config.AutoDiscoverFirefoxProfile) config.FirefoxTemplateProfile = "";
        config.EndpointProbeTimeoutSeconds = Math.Clamp(
            config.EndpointProbeTimeoutSeconds <= 0 ? 4 : config.EndpointProbeTimeoutSeconds, 1, 30);
        foreach (var server in config.UngroupedServers) Normalize(server);
        foreach (var group in config.Groups) Normalize(group);
        var validServerIds = config.AllServers().Select(server => server.Id).ToHashSet();
        var validProxyIds = config.BaseProxies.Select(proxy => proxy.Id).ToHashSet();
        var serverMap = config.AllServers().ToDictionary(s => s.Id);
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
            var route = server.PreferredRoute;
            if (route is null) continue;
            if (route.ProxyId != Guid.Empty && !validProxyIds.Contains(route.ProxyId) ||
                route.ProxyId == Guid.Empty &&
                (route.ServerIds.Count == 0 ||
                 !serverMap.TryGetValue(route.ServerIds[0], out var entry) ||
                 !entry.TryDirectWithoutJumphost) ||
                route.ServerIds.Count == 0 ||
                route.ServerIds[^1] != server.Id ||
                route.ServerIds.Any(id => !validServerIds.Contains(id)) ||
                route.ProxyId != Guid.Empty &&
                server.RequiredPreviousServerId is Guid req &&
                (route.ServerIds.Count < 2 || route.ServerIds[^2] != req))
            {
                server.PreferredRoute = null;
            }
        }
        foreach (var link in config.Links)
        {
            link.ProxyStatistics ??= [];
            if (link.LastSuccessfulProxyId is Guid legacyProxyId &&
                (legacyProxyId == Guid.Empty || validProxyIds.Contains(legacyProxyId)) &&
                link.ProxyStatistics.All(item => item.ProxyId != legacyProxyId))
                link.ProxyStatistics.Add(new LinkProxyStatistic
                {
                    ProxyId = legacyProxyId,
                    LastSuccessUtc = link.LastSuccessUtc ?? DateTimeOffset.MinValue,
                    LatencyMs = link.LastLatencyMs,
                    Strategy = link.LastStrategy
                });
            link.ProxyStatistics = link.ProxyStatistics
                .Where(item => item is not null && (item.ProxyId == Guid.Empty || validProxyIds.Contains(item.ProxyId)))
                .GroupBy(item => item.ProxyId)
                .Select(group => group.OrderByDescending(item => item.LastSuccessUtc).First())
                .ToList();
            if (link.LastSuccessfulProxyId is Guid invalidLegacyProxyId &&
                invalidLegacyProxyId != Guid.Empty &&
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

    public static void NormalizePamDefaults(ManagedServer server)
    {
        server.PamTotpAlgorithm = server.PamTotpAlgorithm is "SHA1" or "SHA256" or "SHA512" ? server.PamTotpAlgorithm : "SHA1";
        server.PamTotpDigits = server.PamTotpDigits is >= 6 and <= 8 ? server.PamTotpDigits : 6;
        server.PamTotpPeriodSeconds = server.PamTotpPeriodSeconds > 0 ? server.PamTotpPeriodSeconds : 30;
        server.PamTotpPrompt = string.IsNullOrWhiteSpace(server.PamTotpPrompt) ? "OTP: " : server.PamTotpPrompt;
        server.PamConfirmPrompt = string.IsNullOrWhiteSpace(server.PamConfirmPrompt) ? "[Y/n]" : server.PamConfirmPrompt;
    }

    public static void Normalize(ManagedServer server)
    {
        server.Host = server.Host.Trim();
        server.Note = SessionNotePolicy.Normalize(server.Note);
        if (server.KittyBaseline is not null) server.KittyBaseline.Host = server.KittyBaseline.Host.Trim();
        server.ShellPrompt = string.IsNullOrWhiteSpace(server.ShellPrompt) ? "$" : server.ShellPrompt;
        server.ImportedCommand ??= "";
        server.PamSshCommand = (server.PamSshCommand ?? "").Trim();
        server.PamPassword ??= "";
        server.PamTotpSecret = (server.PamTotpSecret ?? "").Trim();
        server.PamTotpAlgorithm = server.PamTotpAlgorithm is "SHA1" or "SHA256" or "SHA512" ? server.PamTotpAlgorithm : "SHA1";
        server.PamTotpDigits = server.PamTotpDigits is >= 6 and <= 8 ? server.PamTotpDigits : 6;
        server.PamTotpPeriodSeconds = server.PamTotpPeriodSeconds > 0 ? server.PamTotpPeriodSeconds : 30;
        server.PamTotpPrompt = string.IsNullOrWhiteSpace(server.PamTotpPrompt) ? "OTP: " : server.PamTotpPrompt;
        server.PamConfirmPrompt = string.IsNullOrWhiteSpace(server.PamConfirmPrompt) ? "[Y/n]" : server.PamConfirmPrompt;
        MigrateKeepaliveFromSourceSession(server);
        if (server.KeepaliveIntervalSeconds < 0) server.KeepaliveIntervalSeconds = 15;
        server.WebInterfaces ??= [];
        foreach (var web in server.WebInterfaces)
            web.ResolverAddress = (web.ResolverAddress ?? "").Trim();
        server.BackupEndpoints ??= [];
        server.EndpointPreferences ??= [];
        server.BackupEndpoints = server.BackupEndpoints
            .Where(endpoint => endpoint is not null && endpoint.Port > 0)
            .Select(endpoint => new ServerEndpoint((endpoint.Host ?? "").Trim(), endpoint.Port, endpoint.InternalOnly))
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
                    backup.Port, backup.InternalOnly).Equals(resolvedPreferred));
            if (!inSet) server.PreferredEndpoint = null;
        }
        server.EndpointPreferences = server.EndpointPreferences
            .Where(item => item is not null && item.Endpoint is not null && item.Endpoint.Port > 0)
            .Select(item => new EndpointPreference
            {
                ProxyId = item.ProxyId,
                PreviousServerId = item.PreviousServerId,
                Endpoint = new ServerEndpoint((item.Endpoint.Host ?? "").Trim(), item.Endpoint.Port),
                LastSuccessUtc = item.LastSuccessUtc
            })
            .Where(item => ServerEndpointPolicy.Ordered(server,
                    new EndpointContext(item.ProxyId, item.PreviousServerId))
                .Contains(new ServerEndpoint(
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
            if (server.PreferredRoute.ServerIds.Count == 0 ||
                server.PreferredRoute.ServerIds[^1] != server.Id ||
                server.PreferredRoute.ProxyId == Guid.Empty && server.PreferredRoute.ServerIds.Count == 1 && !server.TryDirectWithoutJumphost ||
                server.PreferredRoute.ProxyId != Guid.Empty &&
                server.RequiredPreviousServerId is Guid required &&
                (server.PreferredRoute.ServerIds.Count < 2 ||
                 server.PreferredRoute.ServerIds[^2] != required))
                server.PreferredRoute = null;
        }
    }

    private static void MigrateKeepaliveFromSourceSession(ManagedServer server)
    {
        if (server.HasAllPersistedKeepaliveSettings) return;
        if (server.KittyBaseline is not null && server.KittyBaseline.KeepaliveIntervalSeconds >= 0) return;
        if (string.IsNullOrWhiteSpace(server.SourceSessionPath)) return;

        var sessionPath = ResolveExistingSessionPath(server.SourceSessionPath);
        if (sessionPath is not null)
        {
            try
            {
                var values = KittySessionImporter.ParseFile(sessionPath);
                var (ka, tcp, failRec, wakeRec) = KittySessionImporter.ReadKeepaliveSettings(values);
                if (!server.HasPersistedKeepaliveIntervalSeconds && !server.ManagerOverrides.Contains(nameof(server.KeepaliveIntervalSeconds)))
                    server.KeepaliveIntervalSeconds = ka;
                if (!server.HasPersistedEnableTcpKeepalives && !server.ManagerOverrides.Contains(nameof(server.EnableTcpKeepalives)))
                    server.EnableTcpKeepalives = tcp;
                if (!server.HasPersistedReconnectOnConnectionFailure && !server.ManagerOverrides.Contains(nameof(server.ReconnectOnConnectionFailure)))
                    server.ReconnectOnConnectionFailure = failRec;
                if (!server.HasPersistedReconnectOnSystemWakeup && !server.ManagerOverrides.Contains(nameof(server.ReconnectOnSystemWakeup)))
                    server.ReconnectOnSystemWakeup = wakeRec;

                if (server.KittyBaseline is not null)
                {
                    server.KittyBaseline.KeepaliveIntervalSeconds = ka;
                    server.KittyBaseline.EnableTcpKeepalives = tcp;
                    server.KittyBaseline.ReconnectOnConnectionFailure = failRec;
                    server.KittyBaseline.ReconnectOnSystemWakeup = wakeRec;
                }
                server.HasPersistedKeepaliveIntervalSeconds = true;
                server.HasPersistedEnableTcpKeepalives = true;
                server.HasPersistedReconnectOnConnectionFailure = true;
                server.HasPersistedReconnectOnSystemWakeup = true;
                return;
            }
            catch { }
        }

        if (server.KittyBaseline is not null)
        {
            server.KittyBaseline.KeepaliveIntervalSeconds = server.KeepaliveIntervalSeconds;
            server.KittyBaseline.EnableTcpKeepalives = server.EnableTcpKeepalives;
            server.KittyBaseline.ReconnectOnConnectionFailure = server.ReconnectOnConnectionFailure;
            server.KittyBaseline.ReconnectOnSystemWakeup = server.ReconnectOnSystemWakeup;
        }
        server.HasPersistedKeepaliveIntervalSeconds = true;
        server.HasPersistedEnableTcpKeepalives = true;
        server.HasPersistedReconnectOnConnectionFailure = true;
        server.HasPersistedReconnectOnSystemWakeup = true;
    }
}

using System.Text.Json;

namespace KiTTYManager.Core;

public enum TransferConflictKind
{
    Server,
    EntryPoint
}

public sealed record TransferConflict(
    TransferConflictKind Kind,
    Guid IncomingId,
    string Name,
    string CurrentSummary,
    string IncomingSummary);

public static class ConfigTransfer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ManagerConfig CreateExport(
        ManagerConfig source,
        IEnumerable<Guid> selectedServerIds,
        bool includeEntryPoints)
    {
        var selected = selectedServerIds.ToHashSet();
        var result = Clone(source);
        result.Groups = PruneGroups(result.Groups, selected);
        result.UngroupedServers.RemoveAll(server => !selected.Contains(server.Id));
        result.Links.RemoveAll(link =>
            !selected.Contains(link.FromServerId) || !selected.Contains(link.ToServerId));

        if (!includeEntryPoints)
        {
            result.BaseProxies.Clear();
            result.PreferredProxyId = null;
        }
        else
        {
            foreach (var proxy in result.BaseProxies)
                proxy.AccessProbeServerIds.RemoveAll(id => !selected.Contains(id));
            foreach (var proxy in result.BaseProxies.Where(proxy =>
                         proxy.StartupServerId is not null && !selected.Contains(proxy.StartupServerId.Value)))
            {
                proxy.StartupServerId = null;
                proxy.AutoStartWhenUnavailable = false;
            }
        }

        var exportedProxyIds = result.BaseProxies.Select(proxy => proxy.Id).ToHashSet();
        foreach (var server in result.AllServers())
        {
            if (server.RequiredPreviousServerId is Guid requiredPreviousServerId &&
                !selected.Contains(requiredPreviousServerId))
                server.RequiredPreviousServerId = null;
            if (server.PreferredProxyId is Guid preferredProxyId &&
                !exportedProxyIds.Contains(preferredProxyId))
                server.PreferredProxyId = null;
            server.EndpointPreferences.RemoveAll(item =>
                (item.ProxyId is not null && item.ProxyId != Guid.Empty &&
                 !exportedProxyIds.Contains(item.ProxyId.Value)) ||
                (item.PreviousServerId is not null && !selected.Contains(item.PreviousServerId.Value)));
        }
        foreach (var link in result.Links)
        {
            link.ProxyStatistics.RemoveAll(item => !exportedProxyIds.Contains(item.ProxyId));
            if (link.LastSuccessfulProxyId is Guid preferredLinkProxyId &&
                !exportedProxyIds.Contains(preferredLinkProxyId))
            {
                link.LastSuccessfulProxyId = null;
                link.LastLatencyMs = null;
            }
        }

        SanitizePreferredRoutes(result);

        ResetAppSettingsForExport(result);
        SanitizeProxiesForExport(result);
        DetachKittyPaths(result);

        return result;
    }

    /// <summary>
    /// Clears local KiTTY file paths so the export is portable.
    /// The recipient can re-apply changes via «Изменения KiTTY…» if needed.
    /// </summary>
    private static void DetachKittyPaths(ManagerConfig config)
    {
        foreach (var server in config.AllServers())
        {
            server.SourceSessionPath = null;
            server.SourceScriptPath = null;
            server.SourceScriptContent = "";
            server.KittyBaseline = null;
            server.ManagerOverrides.Clear();
            server.IgnoredKittyChanges.Clear();
        }
    }

    private static void ResetAppSettingsForExport(ManagerConfig config)
    {
        config.KittyPath = "KiTTY\\kitty.exe";
        config.FirefoxPath = "firefox.exe";
        config.FirefoxProfile = "kitty-manager";
        config.ClosePreferenceConfigured = false;
        config.CloseToTray = false;
        config.EnableLogging = false;
        config.WriteChangesImmediatelyToKitty = false;
        config.CloseWebTunnelWithFirefox = false;
        config.TemporaryFirefoxProfiles = true;
        config.ShareFirefoxProfileByGroup = false;
        config.UseInternalWebResolver = false;
        config.FirefoxTemplateProfile = "";
        config.ConnectionTimeoutSeconds = 10;
        config.EndpointProbeTimeoutSeconds = 4;
        config.RaceBestEntryPoints = false;
        config.SkipExistingLinksInMapCheck = true;
    }

    private static void SanitizeProxiesForExport(ManagerConfig config)
    {
        var jumphostServerIds = config.BaseProxies
            .Where(p => p.StartupServerId is not null)
            .Select(p => p.StartupServerId!.Value)
            .ToHashSet();

        foreach (var proxy in config.BaseProxies)
        {
            proxy.TotpSecret = "";
            proxy.LastAccessScriptAttemptUtc = null;
            proxy.LastAccessScriptSuccessUtc = null;
            proxy.LastAccessConfirmedUtc = null;
            proxy.AccessScheduleBaselineUtc = null;
            proxy.LastAccessScriptResult = "";
            proxy.LastSuccessUtc = null;
            proxy.LastConnectLatencyMs = null;
            proxy.LastStartupLatencyMs = null;
        }

        // Очищаем логины/пароли серверов, используемых как jumphost —
        // получатель введёт свои учётные данные самостоятельно.
        foreach (var server in config.AllServers().Where(s => jumphostServerIds.Contains(s.Id)))
        {
            server.Username = "";
            server.Password = "";
            server.PrivateKeyPassphrase = "";
            server.RootPassword = "";
        }
    }

    public static IReadOnlyList<string> FindProxiesMissingTotp(ManagerConfig incoming)
    {
        return incoming.BaseProxies
            .Where(proxy => proxy.StartupServerId is not null &&
                            proxy.TotpSecret.Length == 0 &&
                            proxy.PostLoginCommand.Length > 0)
            .Select(proxy => proxy.Name)
            .ToList();
    }

    public static IReadOnlyList<TransferConflict> FindConflicts(
        ManagerConfig current,
        ManagerConfig incoming)
    {
        var conflicts = new List<TransferConflict>();
        foreach (var server in incoming.AllServers())
        {
            var existing = FindMatchingServer(current, server);
            if (existing is null) continue;
            conflicts.Add(new TransferConflict(
                TransferConflictKind.Server,
                server.Id,
                server.Name,
                $"{existing.Name} — {existing.Endpoint}",
                $"{server.Name} — {server.Endpoint}"));
        }

        foreach (var proxy in incoming.BaseProxies)
        {
            var existing = FindMatchingProxy(current, proxy);
            if (existing is null) continue;
            conflicts.Add(new TransferConflict(
                TransferConflictKind.EntryPoint,
                proxy.Id,
                proxy.Name,
                $"{existing.Name} — {existing.Host}:{existing.Port}",
                $"{proxy.Name} — {proxy.Host}:{proxy.Port}"));
        }

        return conflicts;
    }

    public static ManagerConfig Merge(
        ManagerConfig current,
        ManagerConfig incoming,
        IReadOnlyDictionary<(TransferConflictKind Kind, Guid IncomingId), bool> useIncoming)
    {
        var result = Clone(current);
        var importedServerIds = new HashSet<Guid>();
        var serverIdMap = new Dictionary<Guid, Guid>();

        foreach (var incomingServer in incoming.AllServers())
        {
            var existing = FindMatchingServer(result, incomingServer);
            if (existing is not null)
            {
                if (!UseIncoming(useIncoming, TransferConflictKind.Server, incomingServer.Id))
                {
                    serverIdMap[incomingServer.Id] = existing.Id;
                    continue;
                }
                result.RemoveServer(existing.Id);
            }

            importedServerIds.Add(incomingServer.Id);
            serverIdMap[incomingServer.Id] = incomingServer.Id;
        }

        MergeGroups(result.Groups, incoming.Groups, importedServerIds);
        foreach (var server in incoming.UngroupedServers.Where(server => importedServerIds.Contains(server.Id)))
            result.UngroupedServers.Add(Clone(server));

        var proxyIdMap = new Dictionary<Guid, Guid>();
        foreach (var incomingProxy in incoming.BaseProxies)
        {
            var existing = FindMatchingProxy(result, incomingProxy);
            if (existing is not null)
            {
                if (!UseIncoming(useIncoming, TransferConflictKind.EntryPoint, incomingProxy.Id))
                {
                    proxyIdMap[incomingProxy.Id] = existing.Id;
                    continue;
                }
                result.BaseProxies.Remove(existing);
            }
            var importedProxy = Clone(incomingProxy);
            if (importedProxy.StartupServerId is Guid startupId && serverIdMap.TryGetValue(startupId, out var mappedId))
                importedProxy.StartupServerId = mappedId;
            importedProxy.AccessProbeServerIds = importedProxy.AccessProbeServerIds
                .Select(id => serverIdMap.TryGetValue(id, out var mappedProbeId) ? mappedProbeId : id)
                .Where(id => result.FindServer(id) is not null)
                .Distinct()
                .Take(importedProxy.AccessProbeServerLimit)
                .ToList();
            result.BaseProxies.Add(importedProxy);
            proxyIdMap[incomingProxy.Id] = importedProxy.Id;
        }

        var validServerIds = result.AllServers().Select(server => server.Id).ToHashSet();
        foreach (var incomingLink in incoming.Links)
        {
            var link = Clone(incomingLink);
            if (serverIdMap.TryGetValue(link.FromServerId, out var fromId)) link.FromServerId = fromId;
            if (serverIdMap.TryGetValue(link.ToServerId, out var toId)) link.ToServerId = toId;
            if (link.LastSuccessfulProxyId is Guid linkProxyId)
                link.LastSuccessfulProxyId = proxyIdMap.TryGetValue(linkProxyId, out var mappedLinkProxyId)
                    ? mappedLinkProxyId
                    : result.BaseProxies.Any(proxy => proxy.Id == linkProxyId) ? linkProxyId : null;
            link.ProxyStatistics = link.ProxyStatistics
                .Select(item =>
                {
                    var copy = Clone(item);
                    copy.ProxyId = proxyIdMap.TryGetValue(item.ProxyId, out var mappedStatisticProxyId)
                        ? mappedStatisticProxyId
                        : item.ProxyId;
                    return copy;
                })
                .Where(item => result.BaseProxies.Any(proxy => proxy.Id == item.ProxyId))
                .ToList();
            if (!validServerIds.Contains(link.FromServerId) || !validServerIds.Contains(link.ToServerId)) continue;
            result.Links.RemoveAll(existing =>
                existing.FromServerId == link.FromServerId && existing.ToServerId == link.ToServerId);
            result.Links.Add(link);
        }

        if (incoming.PreferredProxyId is Guid preferred &&
            proxyIdMap.TryGetValue(preferred, out var mappedPreferred))
            result.PreferredProxyId = mappedPreferred;

        foreach (var server in result.AllServers().Where(server => importedServerIds.Contains(server.Id)))
        {
            if (server.RequiredPreviousServerId is Guid requiredPreviousServerId)
                server.RequiredPreviousServerId = serverIdMap.TryGetValue(
                    requiredPreviousServerId, out var mappedRequiredPreviousServerId)
                    ? mappedRequiredPreviousServerId
                    : requiredPreviousServerId;
            if (server.PreferredProxyId is Guid serverPreferredProxyId)
                server.PreferredProxyId = proxyIdMap.TryGetValue(
                    serverPreferredProxyId, out var mappedServerPreferredProxyId)
                    ? mappedServerPreferredProxyId
                    : result.BaseProxies.Any(proxy => proxy.Id == serverPreferredProxyId)
                        ? serverPreferredProxyId
                        : null;
            foreach (var endpointPreference in server.EndpointPreferences)
            {
                if (endpointPreference.ProxyId is Guid endpointProxyId)
                    endpointPreference.ProxyId = proxyIdMap.TryGetValue(
                        endpointProxyId, out var mappedEndpointProxyId)
                        ? mappedEndpointProxyId
                        : endpointProxyId;
                if (endpointPreference.PreviousServerId is Guid previousServerId &&
                    serverIdMap.TryGetValue(previousServerId, out var mappedPreviousServerId))
                    endpointPreference.PreviousServerId = mappedPreviousServerId;
            }
            var route = server.PreferredRoute;
            if (route is null) continue;
            route.ProxyId = proxyIdMap.TryGetValue(route.ProxyId, out var mappedProxyId)
                ? mappedProxyId
                : route.ProxyId;
            route.ServerIds = route.ServerIds
                .Select(id => serverIdMap.TryGetValue(id, out var mappedServerId) ? mappedServerId : id)
                .ToList();
        }
        SanitizePreferredRoutes(result);

        return result;
    }

    private static bool UseIncoming(
        IReadOnlyDictionary<(TransferConflictKind Kind, Guid IncomingId), bool> decisions,
        TransferConflictKind kind,
        Guid incomingId) =>
        !decisions.TryGetValue((kind, incomingId), out var choice) || choice;

    private static ManagedServer? FindMatchingServer(ManagerConfig config, ManagedServer incoming)
    {
        var byId = config.FindServer(incoming.Id);
        if (byId is not null) return byId;
        if (!string.IsNullOrWhiteSpace(incoming.SourceSessionPath))
        {
            var bySource = config.AllServers().FirstOrDefault(server =>
                string.Equals(server.SourceSessionPath, incoming.SourceSessionPath,
                    StringComparison.OrdinalIgnoreCase));
            if (bySource is not null) return bySource;
        }
        return config.AllServers().FirstOrDefault(server =>
            string.Equals(server.Name, incoming.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(server.Host, incoming.Host, StringComparison.OrdinalIgnoreCase) &&
            server.Port == incoming.Port);
    }

    private static BaseProxy? FindMatchingProxy(ManagerConfig config, BaseProxy incoming) =>
        config.BaseProxies.FirstOrDefault(proxy => proxy.Id == incoming.Id) ??
        config.BaseProxies.FirstOrDefault(proxy =>
            string.Equals(proxy.Host, incoming.Host, StringComparison.OrdinalIgnoreCase) &&
            proxy.Port == incoming.Port);

    private static List<ServerGroup> PruneGroups(IEnumerable<ServerGroup> groups, HashSet<Guid> selected)
    {
        var result = new List<ServerGroup>();
        foreach (var group in groups)
        {
            group.Servers.RemoveAll(server => !selected.Contains(server.Id));
            group.Groups = PruneGroups(group.Groups, selected);
            if (group.Servers.Count > 0 || group.Groups.Count > 0) result.Add(group);
        }
        return result;
    }

    private static void MergeGroups(
        List<ServerGroup> target,
        IEnumerable<ServerGroup> incoming,
        HashSet<Guid> importedServerIds)
    {
        foreach (var incomingGroup in incoming)
        {
            if (!ContainsImportedServer(incomingGroup, importedServerIds)) continue;
            var targetGroup = target.FirstOrDefault(group => group.Id == incomingGroup.Id) ??
                              target.FirstOrDefault(group => string.Equals(
                                  group.Name, incomingGroup.Name, StringComparison.OrdinalIgnoreCase));
            if (targetGroup is null)
            {
                targetGroup = new ServerGroup { Id = incomingGroup.Id, Name = incomingGroup.Name };
                target.Add(targetGroup);
            }

            foreach (var server in incomingGroup.Servers.Where(server => importedServerIds.Contains(server.Id)))
                targetGroup.Servers.Add(Clone(server));
            MergeGroups(targetGroup.Groups, incomingGroup.Groups, importedServerIds);
        }
    }

    private static bool ContainsImportedServer(ServerGroup group, HashSet<Guid> importedServerIds) =>
        group.Servers.Any(server => importedServerIds.Contains(server.Id)) ||
        group.Groups.Any(child => ContainsImportedServer(child, importedServerIds));

    private static void SanitizePreferredRoutes(ManagerConfig config)
    {
        var serverIds = config.AllServers().Select(server => server.Id).ToHashSet();
        var proxyIds = config.BaseProxies.Select(proxy => proxy.Id).ToHashSet();
        foreach (var server in config.AllServers())
        {
            if (server.RequiredPreviousServerId is Guid requiredPreviousServerId &&
                (!serverIds.Contains(requiredPreviousServerId) || requiredPreviousServerId == server.Id))
                server.RequiredPreviousServerId = null;
            var route = server.PreferredRoute;
            if (route is null) continue;
            if (route.ProxyId != Guid.Empty && !proxyIds.Contains(route.ProxyId) ||
                route.ProxyId == Guid.Empty &&
                (!server.TryDirectWithoutJumphost || route.ServerIds.Count != 1) ||
                route.ServerIds.Count == 0 ||
                route.ServerIds[^1] != server.Id || route.ServerIds.Any(id => !serverIds.Contains(id)) ||
                route.ProxyId != Guid.Empty &&
                server.RequiredPreviousServerId is Guid required &&
                (route.ServerIds.Count < 2 || route.ServerIds[^2] != required))
                server.PreferredRoute = null;
        }
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}

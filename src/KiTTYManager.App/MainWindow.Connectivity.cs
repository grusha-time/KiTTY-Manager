using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class MainWindow
{
    internal static bool ApplyDiscoveredRoutesAndPreferences(
        ManagerConfig liveConfig,
        ManagerConfig snapshot,
        IReadOnlyDictionary<Guid, (string Host, int Port, string Fingerprint)> initialFingerprints,
        IReadOnlyDictionary<Guid, CachedRoute?>? initialRoutes,
        Guid sourceId,
        IReadOnlyList<Guid> targetIds,
        Func<bool>? checkAccess = null)
    {
        if (checkAccess != null && !checkAccess())
            throw new InvalidOperationException("ApplyDiscoveredRoutesAndPreferences must be called on the UI thread.");

        bool changed = false;
        var liveSource = liveConfig.FindServer(sourceId);
        var snapSource = snapshot.FindServer(sourceId);
        if (liveSource is not null && snapSource is not null)
        {
            var isSourceUpdated = initialRoutes is null ||
                (initialRoutes.TryGetValue(sourceId, out var initialSourceRoute)
                    ? snapSource.PreferredRoute != initialSourceRoute
                    : snapSource.PreferredRoute is not null);

            // If the snapshot source route was updated during the check, it reflects the proven working route
            if (snapSource.PreferredRoute is not null &&
                isSourceUpdated &&
                (liveSource.PreferredRoute is null ||
                 snapSource.PreferredRoute.LastSuccessUtc >= liveSource.PreferredRoute.LastSuccessUtc))
            {
                liveSource.PreferredRoute = snapSource.PreferredRoute;
                liveSource.PreferredProxyId = snapSource.PreferredProxyId;
                changed = true;
            }
        }

        foreach (var targetId in targetIds.Distinct())
        {
            var liveTarget = liveConfig.FindServer(targetId);
            var snapTarget = snapshot.FindServer(targetId);
            if (liveTarget is null || snapTarget is null) continue;

            var isTargetUpdated = initialRoutes is null ||
                (initialRoutes.TryGetValue(targetId, out var initialTargetRoute)
                    ? snapTarget.PreferredRoute != initialTargetRoute
                    : snapTarget.PreferredRoute is not null);

            if (snapTarget.PreferredRoute is not null &&
                isTargetUpdated &&
                (liveTarget.PreferredRoute is null ||
                 snapTarget.PreferredRoute.LastSuccessUtc >= liveTarget.PreferredRoute.LastSuccessUtc) &&
                RoutePreferencePolicy.ShouldReplacePreferred(liveTarget.PreferredRoute, snapTarget.PreferredRoute.ServerIds))
            {
                liveTarget.PreferredRoute = snapTarget.PreferredRoute;
                liveTarget.PreferredProxyId = snapTarget.PreferredProxyId;
                changed = true;
            }
        }

        foreach (var hop in snapshot.AllServers())
        {
            var liveHop = liveConfig.FindServer(hop.Id);
            if (liveHop is null) continue;

            if (!initialFingerprints.TryGetValue(hop.Id, out var initial))
                continue;
            if (!string.Equals(initial.Host, hop.Host, StringComparison.OrdinalIgnoreCase) || initial.Port != hop.Port)
                continue;
            if (!string.Equals(initial.Host, liveHop.Host, StringComparison.OrdinalIgnoreCase) || initial.Port != liveHop.Port)
                continue;

            if (!string.IsNullOrEmpty(hop.HostKeyFingerprint) &&
                !string.Equals(hop.HostKeyFingerprint, initial.Fingerprint, StringComparison.Ordinal) &&
                string.Equals(liveHop.HostKeyFingerprint ?? "", initial.Fingerprint, StringComparison.Ordinal))
            {
                liveHop.HostKeyFingerprint = hop.HostKeyFingerprint;
                liveHop.HostKeyAlgorithm = hop.HostKeyAlgorithm;
                liveHop.HostKeyBits = hop.HostKeyBits;
                changed = true;
            }

            if (SyncEndpointPreferences(liveHop, hop))
            {
                changed = true;
            }
        }

        foreach (var snapProxy in snapshot.BaseProxies)
        {
            if (snapProxy.LastSuccessUtc is null) continue;
            var liveProxy = liveConfig.BaseProxies.FirstOrDefault(p => p.Id == snapProxy.Id);
            if (liveProxy is null) continue;

            if (liveProxy.LastSuccessUtc is null || snapProxy.LastSuccessUtc >= liveProxy.LastSuccessUtc)
            {
                if (liveProxy.LastSuccessUtc != snapProxy.LastSuccessUtc ||
                    liveProxy.LastConnectLatencyMs != snapProxy.LastConnectLatencyMs)
                {
                    liveProxy.LastSuccessUtc = snapProxy.LastSuccessUtc;
                    liveProxy.LastConnectLatencyMs = snapProxy.LastConnectLatencyMs;
                    changed = true;
                }
            }
        }

        return changed;
    }

    internal static bool ApplyDiscoveredRoutesAndPreferences(
        ManagerConfig liveConfig,
        ManagerConfig snapshot,
        IReadOnlyDictionary<Guid, (string Host, int Port, string Fingerprint)> initialFingerprints,
        Guid sourceId,
        IReadOnlyList<Guid> targetIds,
        Func<bool>? checkAccess = null)
    {
        return ApplyDiscoveredRoutesAndPreferences(
            liveConfig,
            snapshot,
            initialFingerprints,
            initialRoutes: null,
            sourceId,
            targetIds,
            checkAccess);
    }

    private static bool SyncEndpointPreferences(ManagedServer liveServer, ManagedServer snapServer)
    {
        bool changed = false;
        if (snapServer.PreferredEndpoint is not null)
        {
            if (liveServer.PreferredEndpoint?.Host != snapServer.PreferredEndpoint.Host ||
                liveServer.PreferredEndpoint?.Port != snapServer.PreferredEndpoint.Port)
            {
                liveServer.PreferredEndpoint = new ServerEndpoint(snapServer.PreferredEndpoint.Host, snapServer.PreferredEndpoint.Port);
                changed = true;
            }
        }

        foreach (var snapshotPref in snapServer.EndpointPreferences)
        {
            var livePref = liveServer.EndpointPreferences.FirstOrDefault(item =>
                item.ProxyId == snapshotPref.ProxyId &&
                item.PreviousServerId == snapshotPref.PreviousServerId);
            if (livePref is null || livePref.LastSuccessUtc <= snapshotPref.LastSuccessUtc)
            {
                var hopContext = snapshotPref.ProxyId is not null
                    ? EndpointContext.Direct(snapshotPref.ProxyId.Value)
                    : (snapshotPref.PreviousServerId is not null ? EndpointContext.Via(snapshotPref.PreviousServerId.Value) : (EndpointContext?)null);
                ServerEndpointPolicy.Remember(liveServer, snapshotPref.Endpoint, hopContext);
                changed = true;
                if (snapshotPref.LastSuccessUtc != default)
                {
                    var updatedPref = liveServer.EndpointPreferences.FirstOrDefault(item =>
                        item.ProxyId == snapshotPref.ProxyId &&
                        item.PreviousServerId == snapshotPref.PreviousServerId);
                    if (updatedPref is not null && updatedPref.LastSuccessUtc != snapshotPref.LastSuccessUtc)
                    {
                        updatedPref.LastSuccessUtc = snapshotPref.LastSuccessUtc;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }
}

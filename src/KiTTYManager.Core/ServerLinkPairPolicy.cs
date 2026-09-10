namespace KiTTYManager.Core;

public static class ServerLinkPairPolicy
{
    public static IReadOnlyList<IReadOnlyList<ServerLink>> GroupByDirection(IEnumerable<ServerLink> links) =>
        links.GroupBy(link => (link.FromServerId, link.ToServerId))
            .Select(group => (IReadOnlyList<ServerLink>)group.ToArray())
            .ToArray();

    public static IReadOnlyList<ServerLink> GroupAsRelationship(
        Guid selectedServerId,
        Guid counterpartServerId,
        IEnumerable<ServerLink> links) =>
        links.Where(link =>
                (link.FromServerId == selectedServerId && link.ToServerId == counterpartServerId) ||
                (link.FromServerId == counterpartServerId && link.ToServerId == selectedServerId))
            .ToArray();

    public static void RememberSuccess(
        ManagerConfig config, ConnectivityResult result, DateTimeOffset now)
    {
        RememberDirection(config, result.SourceId, result.TargetId, result, now, synthetic: false);
        RememberDirection(config, result.TargetId, result.SourceId, result, now, synthetic: true);
    }

    public static void RememberDirectedSuccess(
        ManagerConfig config, ConnectivityResult result, DateTimeOffset now) =>
        RememberDirection(config, result.SourceId, result.TargetId, result, now, synthetic: false);

    public static bool IsKnownAsymmetric(ManagerConfig config, Guid serverAId, Guid serverBId)
    {
        var fwd = config.Links.FirstOrDefault(l => l.FromServerId == serverAId && l.ToServerId == serverBId);
        var rev = config.Links.FirstOrDefault(l => l.FromServerId == serverBId && l.ToServerId == serverAId);
        var fwdConfirmed = fwd?.LastSuccessUtc is not null;
        var revConfirmed = rev?.LastSuccessUtc is not null;
        return fwdConfirmed ^ revConfirmed;
    }

    public static bool RememberAdaptiveSuccess(
        ManagerConfig config, ConnectivityResult result, DateTimeOffset now)
    {
        var wasAsymmetric = IsKnownAsymmetric(config, result.SourceId, result.TargetId);
        if (wasAsymmetric)
        {
            RememberDirectedSuccess(config, result, now);
            return true;
        }

        var rev = config.Links.FirstOrDefault(l => l.FromServerId == result.TargetId && l.ToServerId == result.SourceId);
        var revConfirmed = rev?.LastSuccessUtc is not null;
        if (revConfirmed)
        {
            RememberDirectedSuccess(config, result, now);
            return false;
        }

        RememberSuccess(config, result, now);
        return false;
    }

    public static void Invalidate(ManagerConfig config, Guid serverAId, Guid serverBId)
    {
        foreach (var link in config.Links.Where(link =>
                     (link.FromServerId == serverAId && link.ToServerId == serverBId) ||
                     (link.FromServerId == serverBId && link.ToServerId == serverAId)))
        {
            link.LastSuccessUtc = null;
            link.LastStrategy = "";
            LinkStatisticsPolicy.Clear(link);
        }

        foreach (var server in config.AllServers())
        {
            var route = server.PreferredRoute;
            if (route is null) continue;
            for (var index = 0; index < route.ServerIds.Count - 1; index++)
            {
                var from = route.ServerIds[index];
                var to = route.ServerIds[index + 1];
                if (!((from == serverAId && to == serverBId) ||
                      (from == serverBId && to == serverAId))) continue;
                server.PreferredRoute = null;
                break;
            }
        }
    }

    public static void InvalidateDirection(ManagerConfig config, Guid sourceId, Guid targetId)
    {
        foreach (var link in config.Links.Where(link =>
                     link.FromServerId == sourceId && link.ToServerId == targetId))
        {
            link.LastSuccessUtc = null;
            link.LastStrategy = "";
            LinkStatisticsPolicy.Clear(link);
        }
        foreach (var server in config.AllServers())
        {
            var route = server.PreferredRoute;
            if (route is null) continue;
            for (var index = 0; index < route.ServerIds.Count - 1; index++)
                if (route.ServerIds[index] == sourceId && route.ServerIds[index + 1] == targetId)
                { server.PreferredRoute = null; break; }
        }
    }

    private static void RememberDirection(
        ManagerConfig config, Guid fromId, Guid toId, ConnectivityResult result,
        DateTimeOffset now, bool synthetic)
    {
        var link = config.Links.FirstOrDefault(item =>
            item.FromServerId == fromId && item.ToServerId == toId);
        if (link is null)
        {
            link = new ServerLink
            {
                FromServerId = fromId,
                ToServerId = toId,
                Discovered = true
            };
            config.Links.Add(link);
        }

        link.LastSuccessUtc = now;
        link.LastStrategy = synthetic ? "" : result.Strategy;
        if (synthetic || result.ProxyId is null)
            LinkStatisticsPolicy.Clear(link);
        else
            LinkStatisticsPolicy.Remember(
                link, result.ProxyId.Value, now, result.Duration.TotalMilliseconds, result.Strategy);
    }
}

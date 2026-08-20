namespace KiTTYManager.Core;

public static class ServerLinkPairPolicy
{
    public static void RememberSuccess(
        ManagerConfig config, ConnectivityResult result, DateTimeOffset now)
    {
        RememberDirection(config, result.SourceId, result.TargetId, result, now, synthetic: false);
        RememberDirection(config, result.TargetId, result.SourceId, result, now, synthetic: true);
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

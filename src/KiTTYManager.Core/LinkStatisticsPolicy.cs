namespace KiTTYManager.Core;

public static class LinkStatisticsPolicy
{
    public static LinkProxyStatistic? ForProxy(ServerLink link, Guid proxyId) =>
        link.ProxyStatistics.FirstOrDefault(item => item.ProxyId == proxyId) ??
        (link.LastSuccessfulProxyId == proxyId
            ? new LinkProxyStatistic
            {
                ProxyId = proxyId,
                LastSuccessUtc = link.LastSuccessUtc ?? DateTimeOffset.MinValue,
                LatencyMs = link.LastLatencyMs,
                Strategy = link.LastStrategy
            }
            : null);

    public static bool HasProxyEvidence(ServerLink link) =>
        link.ProxyStatistics.Count > 0 || link.LastSuccessfulProxyId is not null;

    public static void Remember(
        ServerLink link, Guid proxyId, DateTimeOffset now, double? latencyMs, string strategy)
    {
        var statistic = ForProxy(link, proxyId);
        if (statistic is null)
        {
            statistic = new LinkProxyStatistic { ProxyId = proxyId };
            link.ProxyStatistics.Add(statistic);
        }
        statistic.LastSuccessUtc = now;
        statistic.LatencyMs = latencyMs;
        statistic.Strategy = strategy ?? "";

        // Поля оставлены для чтения старыми версиями и миграции старых config.json.
        link.LastSuccessfulProxyId = proxyId;
        link.LastLatencyMs = latencyMs;
    }

    public static void Clear(ServerLink link)
    {
        link.ProxyStatistics.Clear();
        link.LastSuccessfulProxyId = null;
        link.LastLatencyMs = null;
    }
}

using System.Net;

namespace KiTTYManager.Core;

public static class WebResolverMappingPlan
{
    public static IReadOnlyList<KeyValuePair<string, string>> Build(ManagedServer server)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var web in server.WebInterfaces)
        {
            if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var uri) ||
                IPAddress.TryParse(uri.Host, out _) || string.IsNullOrWhiteSpace(web.ResolverAddress)) continue;
            var address = web.ResolverAddress.Trim();
            if (!IPAddress.TryParse(address, out _))
                throw new InvalidOperationException(
                    $"Для веб-интерфейса «{web.Name}» укажите IP в поле «Адрес DNS».");
            result.Add(new(uri.DnsSafeHost, address));
        }
        return result;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        ManagerConfig config,
        ManagedServer targetServer,
        IEnumerable<Guid>? routeHopServerIds = null,
        Guid? proxyId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(targetServer);

        foreach (var web in targetServer.WebInterfaces)
        {
            if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var uri) ||
                IPAddress.TryParse(uri.Host, out _) || string.IsNullOrWhiteSpace(web.ResolverAddress)) continue;
            var address = web.ResolverAddress.Trim();
            if (!IPAddress.TryParse(address, out _))
                throw new InvalidOperationException(
                    $"Для веб-интерфейса «{web.Name}» укажите IP в поле «Адрес DNS».");
        }

        var activeHops = routeHopServerIds != null ? new HashSet<Guid>(routeHopServerIds) : [];
        var effectiveTargetProxyId = proxyId.HasValue
            ? (proxyId.Value != Guid.Empty ? proxyId.Value : (Guid?)null)
            : (targetServer.PreferredProxyId is { } pId && pId != Guid.Empty
                ? pId
                : (targetServer.PreferredRoute?.ProxyId is { } rId && rId != Guid.Empty ? rId : (Guid?)null));

        if (effectiveTargetProxyId.HasValue)
        {
            var activeProxy = config.BaseProxies.FirstOrDefault(p => p.Id == effectiveTargetProxyId.Value);
            if (activeProxy?.StartupServerId is { } startupSid && startupSid != Guid.Empty)
                activeHops.Add(startupSid);
        }

        bool targetUsesProxy = effectiveTargetProxyId.HasValue;
        var targetReq = targetServer.RequiredPreviousServerId;

        var targetGroup = config.FindServerGroup(targetServer.Id);
        var targetAncestryIds = targetGroup is not null
            ? config.GroupAncestry(targetGroup.Id).Select(g => g.Id).ToHashSet()
            : null;

        bool IsLinkCompatibleWithTargetProxy(ServerLink link)
        {
            if (link.LastSuccessUtc is null) return false;
            var targetContextProxyId = effectiveTargetProxyId ?? Guid.Empty;
            return LinkStatisticsPolicy.ForProxy(link, targetContextProxyId) is not null ||
                   !LinkStatisticsPolicy.HasProxyEvidence(link);
        }

        var allServers = config.AllServers()
            .Where(s => s.Id != targetServer.Id)
            .Concat([targetServer])
            .ToList();

        var candidatesByDomain = new Dictionary<string, List<(int Priority, IPAddress IP, ManagedServer Server)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in allServers)
        {
            bool isTarget = server.Id == targetServer.Id;
            int? priority = null;

            if (isTarget)
            {
                priority = 1;
            }
            else
            {
                bool isHop = activeHops.Contains(server.Id);
                var candReq = server.RequiredPreviousServerId;

                if (!isHop)
                {
                    if (targetReq.HasValue)
                    {
                        bool hasDirectedLink = config.Links.Any(l => l.FromServerId == targetServer.Id && l.ToServerId == server.Id && IsLinkCompatibleWithTargetProxy(l));

                        if (candReq.HasValue && candReq.Value != targetReq.Value && !hasDirectedLink)
                            continue;

                        bool gatewayCompatible =
                            hasDirectedLink ||
                            (candReq.HasValue && candReq.Value == targetReq.Value) ||
                            server.Id == targetReq.Value ||
                            config.Links.Any(l => ((l.FromServerId == targetReq.Value && l.ToServerId == server.Id) ||
                                                   (l.FromServerId == targetServer.Id && l.ToServerId == server.Id)) &&
                                                  IsLinkCompatibleWithTargetProxy(l));

                        if (!gatewayCompatible)
                            continue;
                    }
                    else if (candReq.HasValue)
                    {
                        bool gatewayCompatible =
                            candReq.Value == targetServer.Id ||
                            activeHops.Contains(candReq.Value) ||
                            config.Links.Any(l => l.FromServerId == targetServer.Id && l.ToServerId == server.Id && IsLinkCompatibleWithTargetProxy(l));

                        if (!gatewayCompatible)
                            continue;
                    }
                }

                var candProxyId = (server.PreferredProxyId is { } cpId && cpId != Guid.Empty)
                    ? cpId
                    : (server.PreferredRoute?.ProxyId is { } crId && crId != Guid.Empty ? crId : (Guid?)null);
                bool candUsesProxy = candProxyId.HasValue;

                bool hasLinkOrRoute =
                    isHop ||
                    (targetServer.PreferredRoute?.ServerIds.Contains(server.Id) == true) ||
                    config.Links.Any(l => ((l.FromServerId == targetServer.Id && l.ToServerId == server.Id) ||
                                           (targetReq.HasValue && l.FromServerId == targetReq.Value && l.ToServerId == server.Id)) &&
                                          IsLinkCompatibleWithTargetProxy(l));

                if (targetUsesProxy && candUsesProxy)
                {
                    if (effectiveTargetProxyId != candProxyId && !hasLinkOrRoute)
                        continue;
                }
                else if (targetUsesProxy && !candUsesProxy)
                {
                    bool hasCompatibleContext =
                        hasLinkOrRoute ||
                        (targetReq.HasValue && candReq.HasValue && targetReq.Value == candReq.Value) ||
                        (targetReq.HasValue && server.Id == targetReq.Value) ||
                        (candReq.HasValue && targetServer.Id == candReq.Value) ||
                        (targetAncestryIds is not null && config.FindServerGroup(server.Id) is { } cg &&
                         config.GroupAncestry(cg.Id).Any(a => targetAncestryIds.Contains(a.Id)));

                    if (!hasCompatibleContext)
                        continue;
                }
                else if (!targetUsesProxy && candUsesProxy)
                {
                    if (!hasLinkOrRoute)
                        continue;
                }

                if (isHop || (targetServer.PreferredRoute?.ServerIds.Contains(server.Id) == true))
                {
                    priority = 2;
                }
                else if (
                    (targetReq.HasValue && candReq.HasValue && targetReq.Value == candReq.Value) ||
                    (targetReq.HasValue && server.Id == targetReq.Value) ||
                    (candReq.HasValue && targetServer.Id == candReq.Value) ||
                    config.Links.Any(l => ((l.FromServerId == targetServer.Id && l.ToServerId == server.Id) ||
                                           (targetReq.HasValue && l.FromServerId == targetReq.Value && l.ToServerId == server.Id)) &&
                                          IsLinkCompatibleWithTargetProxy(l)))
                {
                    priority = 3;
                }
                else if (
                    (targetAncestryIds is not null && config.FindServerGroup(server.Id) is { } cg &&
                     config.GroupAncestry(cg.Id).Any(a => targetAncestryIds.Contains(a.Id))) ||
                    (targetUsesProxy && candUsesProxy && effectiveTargetProxyId == candProxyId))
                {
                    priority = 4;
                }
                else if (!targetReq.HasValue && !candReq.HasValue && !targetUsesProxy && !candUsesProxy)
                {
                    priority = 5;
                }
                else
                {
                    continue;
                }
            }

            if (!priority.HasValue) continue;

            foreach (var web in server.WebInterfaces)
            {
                if (!Uri.TryCreate(web.Url, UriKind.Absolute, out var uri) ||
                    IPAddress.TryParse(uri.Host, out _) ||
                    string.IsNullOrWhiteSpace(web.ResolverAddress))
                    continue;

                var address = web.ResolverAddress.Trim();
                if (!IPAddress.TryParse(address, out var parsedIp))
                {
                    if (isTarget)
                        throw new InvalidOperationException(
                            $"Для веб-интерфейса «{web.Name}» укажите IP в поле «Адрес DNS».");
                    continue;
                }

                string domain;
                try
                {
                    domain = ResolvingSocks5Relay.NormalizeDomain(uri.DnsSafeHost);
                }
                catch
                {
                    if (isTarget) throw;
                    continue;
                }

                if (!candidatesByDomain.TryGetValue(domain, out var list))
                {
                    list = [];
                    candidatesByDomain[domain] = list;
                }

                list.Add((priority.Value, parsedIp, server));
            }
        }

        var result = new List<KeyValuePair<string, string>>();

        foreach (var kv in candidatesByDomain.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var domain = kv.Key;
            var candidates = kv.Value;
            var bestPriority = candidates.Min(c => c.Priority);
            var bestCandidates = candidates.Where(c => c.Priority == bestPriority).ToList();

            var firstIp = bestCandidates[0].IP;
            if (bestCandidates.All(c => c.IP.Equals(firstIp)))
            {
                result.Add(new(domain, firstIp.ToString()));
            }
        }

        return result;
    }
}

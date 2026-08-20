namespace KiTTYManager.Core;

public static class RoutePlanner
{
    private const int MaxSavedPaths = 32;
    private const int MaxPathsPerEntry = 2;
    private const int MaxServersPerPath = 16;
    public static BaseProxy DirectConnectionProxy { get; } = new()
    {
        Id = Guid.Empty,
        Name = "Прямое подключение",
        Host = "",
        Port = 0,
        Enabled = true
    };

    public static IReadOnlyList<IReadOnlyList<ManagedServer>> FindPaths(ManagerConfig config, Guid targetId)
    {
        var servers = config.AllServers().ToDictionary(s => s.Id);
        if (!servers.ContainsKey(targetId)) return [];
        var target = servers[targetId];
        if (servers.Count == 1) return [[servers[targetId]]];

        var adjacency = config.Links
            .Where(l => servers.ContainsKey(l.FromServerId) && servers.ContainsKey(l.ToServerId))
            .GroupBy(l => l.FromServerId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ToServerId).Distinct()
                .OrderBy(id => servers[id].Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(id => id).ToArray());
        var pathsByEntry = new List<IReadOnlyList<IReadOnlyList<ManagedServer>>>();
        foreach (var start in servers.Values
                     .Where(server => server.Id != targetId)
                     .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(server => server.Id))
        {
            var first = ShortestPath(start.Id, targetId);
            if (first is null) continue;
            var alternatives = new List<List<Guid>> { first };
            if (MaxPathsPerEntry > 1)
            {
                var second = Enumerable.Range(0, first.Count - 1)
                    .Select(index => ShortestPath(
                        start.Id, targetId, (first[index], first[index + 1])))
                    .Where(path => path is not null && !path.SequenceEqual(first))
                    .Select(path => path!)
                    .OrderBy(path => path.Count)
                    .ThenBy(PathSignature, StringComparer.CurrentCultureIgnoreCase)
                    .FirstOrDefault();
                if (second is not null) alternatives.Add(second);
            }
            pathsByEntry.Add(alternatives
                .Select(path => (IReadOnlyList<ManagedServer>)path.Select(id => servers[id]).ToArray())
                .ToArray());
        }

        // Сначала по одному лучшему пути от разных входных серверов, затем вторые
        // варианты. Это не позволяет плотной ветке вытеснить все остальные входы.
        return Enumerable.Range(0, MaxPathsPerEntry)
            .SelectMany(round => pathsByEntry
                .Where(paths => paths.Count > round)
                .Select(paths => paths[round])
                .OrderBy(path => target.RequiredPreviousServerId is Guid required &&
                                 (path.Count < 2 || path[^2].Id != required) ? 1 : 0)
                .ThenBy(path => path.Count)
                .ThenBy(path => string.Join('/', path.Select(server => server.Name)),
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => string.Join('/', path.Select(server => server.Id))))
            .Take(MaxSavedPaths)
            .ToArray();

        List<Guid>? ShortestPath(
            Guid startId, Guid destinationId, (Guid From, Guid To)? bannedEdge = null)
        {
            var queue = new Queue<Guid>();
            var previous = new Dictionary<Guid, Guid?>();
            queue.Enqueue(startId);
            previous[startId] = null;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var depth = 1;
                for (var node = current; previous[node] is Guid parent; node = parent) depth++;
                if (current == destinationId)
                {
                    var path = new List<Guid>();
                    for (Guid? node = current; node is not null; node = previous[node.Value])
                        path.Add(node.Value);
                    path.Reverse();
                    return path;
                }
                if (depth >= MaxServersPerPath || !adjacency.TryGetValue(current, out var next)) continue;
                foreach (var id in next)
                {
                    if (bannedEdge == (current, id) || previous.ContainsKey(id)) continue;
                    previous[id] = current;
                    queue.Enqueue(id);
                }
            }
            return null;
        }

        string PathSignature(IEnumerable<Guid> path) => string.Join('/',
            path.Select(id => $"{servers[id].Name}\0{id:N}"));
    }

    public static IReadOnlyList<RouteCandidate> Candidates(ManagerConfig config, Guid targetId)
    {
        var target = config.FindServer(targetId);
        if (target is null) return [];

        var savedPaths = FindPaths(config, targetId)
            .Where(path => path.Count > 1)
            .ToArray();
        var direct = target.TryDirectWithoutJumphost
            ? new[] { new RouteCandidate(DirectConnectionProxy, [target], true) }
            : [];
        var raw = direct.Concat(OrderedProxies(config, target)
            .SelectMany(proxy => CandidatesForProxy(config, target, proxy, savedPaths)));
        return Rank(config, target, raw
            .Where(candidate => SatisfiesRouteConstraints(config, candidate))
            .DistinctBy(CandidateSignature));
    }

    /// <summary>
    /// Единое ранжирование кандидатов. Подтверждённый прямой доступ и проверенные
    /// маршруты идут перед непроверенными; далее — по сложности точки входа,
    /// задержке, числу переходов и свежести успеха. Одно и то же ранжирование
    /// применяется и к исходному списку, и после подстановки сохранённого
    /// маршрута, чтобы тот не мог «перепрыгнуть» подтверждённый прямой путь.
    /// </summary>
    private static RouteCandidate[] Rank(
        ManagerConfig config, ManagedServer target, IEnumerable<RouteCandidate> candidates,
        CachedRoute? preferred = null) =>
        candidates
            .OrderBy(candidate => CandidateClass(config, candidate))
            .ThenBy(candidate => StartupComplexity(candidate.Proxy))
            .ThenBy(candidate => candidate.Servers.Count)
            // Среди одинаково коротких цепочек сначала используем входной сервер,
            // который уже успешно открывался напрямую через эту же JH.
            .ThenBy(candidate => EntryRoutePriority(candidate))
            // Память и задержка выбирают лучший вариант только среди путей одной
            // длины. Иначе старая длинная цепочка могла вытеснить короткую.
            .ThenBy(candidate => preferred is not null && RoutePreferencePolicy.Matches(preferred, candidate) ? 0 : 1)
            .ThenBy(candidate => PathLatency(config, candidate))
            .ThenBy(candidate => ProxyHintPriority(candidate.Proxy, candidate.Servers[0]))
            .ThenBy(candidate => LinkProxyPriority(config, candidate))
            .ThenByDescending(candidate => PathLastSuccess(config, candidate))
            .ThenBy(candidate => ProxyPriority(config, candidate.Proxy, target))
            .ThenBy(candidate => candidate.Proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static int EntryRoutePriority(RouteCandidate candidate)
    {
        if (candidate.WithoutProxy) return 0;
        var route = candidate.Servers[0].PreferredRoute;
        return route is not null &&
               route.ProxyId == candidate.Proxy.Id &&
               route.ServerIds.Count == 1 &&
               route.ServerIds[0] == candidate.Servers[0].Id
            ? 0
            : 1;
    }

    /// <summary>
    /// Reconstructs a candidate from a saved preferred route.  Returns null when
    /// the proxy is disabled, a server was removed, or — for multi-hop routes —
    /// any hop no longer has a saved link (the route is stale, e.g. links were
    /// deleted), so a removed route must not be resurrected from cache.
    /// </summary>
    public static RouteCandidate? CandidateFromCached(ManagerConfig config, CachedRoute cached)
    {
        var proxy = cached.ProxyId == Guid.Empty
            ? DirectConnectionProxy
            : config.BaseProxies.FirstOrDefault(p => p.Id == cached.ProxyId && p.Enabled);
        if (proxy is null) return null;
        var servers = new List<ManagedServer>(cached.ServerIds.Count);
        foreach (var id in cached.ServerIds)
        {
            var server = config.FindServer(id);
            if (server is null) return null;
            servers.Add(server);
        }
        if (servers.Count == 0) return null;
        for (var index = 0; index < servers.Count - 1; index++)
        {
            var from = servers[index].Id;
            var to = servers[index + 1].Id;
            if (!config.Links.Any(link => link.FromServerId == from &&
                                         link.ToServerId == to &&
                                         link.LastSuccessUtc is not null &&
                                         (LinkStatisticsPolicy.ForProxy(link, cached.ProxyId) is not null ||
                                          !LinkStatisticsPolicy.HasProxyEvidence(link))))
                return null;
        }
        if (cached.ProxyId == Guid.Empty &&
            (servers.Count != 1 || !servers[0].TryDirectWithoutJumphost))
            return null;
        var candidate = new RouteCandidate(proxy, servers, cached.ProxyId == Guid.Empty);
        return SatisfiesRouteConstraints(config, candidate) ? candidate : null;
    }

    /// <summary>
    /// Гарантирует, что сохранённый маршрут участвует в ранжировании (даже если его
    /// отбросило ограничение на число путей), и ставит его первым в пределах своего
    /// класса качества — поэтому запомненная точка входа выигрывает у равных
    /// кандидатов, но подтверждённый прямой путь всё равно остаётся впереди длинной
    /// сохранённой цепочки. Устаревший маршрут (без связей) не добавляется.
    /// </summary>
    public static IReadOnlyList<RouteCandidate> OrderPreferred(
        ManagerConfig config, IReadOnlyList<RouteCandidate> ranked, CachedRoute? preferred)
    {
        if (preferred is null) return ranked;
        var list = ranked.ToList();
        if (!list.Any(candidate => RoutePreferencePolicy.Matches(preferred, candidate)))
        {
            var reconstructed = CandidateFromCached(config, preferred);
            if (reconstructed is not null) list.Add(reconstructed);
        }
        var target = config.FindServer(preferred.ServerIds[^1]);
        if (target is null) return list.ToArray();
        var rankedWithPreferred = Rank(config, target, list, preferred);
        // Явно включённый режим «сначала без JH» остаётся сильнее памяти
        // резервного маршрута: это отдельное решение пользователя.
        if (target.TryDirectWithoutJumphost) return rankedWithPreferred;
        var exact = rankedWithPreferred.FirstOrDefault(candidate =>
            RoutePreferencePolicy.Matches(preferred, candidate));
        if (exact is null) return rankedWithPreferred;

        // Последний действительно успешный и всё ещё валидный маршрут всегда
        // является первой попыткой. Более короткие варианты доказываются в фоне,
        // а не задерживают пользовательское подключение.
        return
        [
            exact,
            .. rankedWithPreferred.Where(candidate =>
                !ReferenceEquals(candidate, exact) &&
                candidate.Servers.Count > 1 &&
                IsVerifiedPath(config, candidate.Servers, candidate.Proxy.Id)),
            .. rankedWithPreferred.Where(candidate =>
                !ReferenceEquals(candidate, exact) &&
                !(candidate.Servers.Count > 1 &&
                  IsVerifiedPath(config, candidate.Servers, candidate.Proxy.Id)))
        ];
    }

    private static IEnumerable<RouteCandidate> CandidatesForProxy(
        ManagerConfig config,
        ManagedServer target,
        BaseProxy proxy,
        IEnumerable<IReadOnlyList<ManagedServer>> paths)
    {
        if (target.RequiredPreviousServerId is null)
            yield return new RouteCandidate(proxy, [target]);

        foreach (var path in paths)
        {
            foreach (var expanded in ExpandEntries(config, proxy, path))
                yield return new RouteCandidate(proxy, expanded);
        }
    }

    /// <summary>
    /// A graph path says that entry can reach the target; it does not prove that
    /// the proxy can reach entry. Keep a direct entry only after a real one-hop
    /// success, otherwise prepend the entry's still-valid cached route.
    /// </summary>
    private static IEnumerable<IReadOnlyList<ManagedServer>> ExpandEntries(
        ManagerConfig config, BaseProxy proxy, IReadOnlyList<ManagedServer> path)
    {
        if (path.Count == 0) yield break;
        var entry = path[0];
        var preferred = entry.PreferredRoute;
        if (preferred is not null &&
            preferred.ProxyId == proxy.Id &&
            preferred.ServerIds.Count == 1 &&
            preferred.ServerIds[0] == entry.Id)
        {
            yield return path;
            yield break;
        }

        if (preferred is not null && preferred.ProxyId == proxy.Id)
        {
            var prefix = CandidateFromCached(config, preferred);
            if (prefix is not null && prefix.Servers[^1].Id == entry.Id)
            {
                var combined = prefix.Servers.Concat(path.Skip(1)).ToArray();
                if (combined.Select(server => server.Id).Distinct().Count() == combined.Length)
                    yield return combined;
            }
        }

        // Оставляем теоретический вариант как последний резерв для первичного
        // обнаружения. CandidateClass не даст ему обогнать подтверждённый или
        // составленный маршрут.
        yield return path;
    }

    public static string CandidateReason(
        ManagerConfig config, Guid targetId, RouteCandidate candidate, CachedRoute? preferred)
    {
        if (RoutePreferencePolicy.Matches(preferred, candidate))
            return "последний успешный";
        if (IsComposedFromCachedEntry(candidate))
            return "составлен через сохранённый вход";
        if (candidate.Servers.Count > 1 &&
            HasProvenEntry(config, candidate) &&
            IsVerifiedPath(config, candidate.Servers, candidate.Proxy.Id))
            return "подтверждённый";
        if (candidate.Servers.Count == 1 &&
            candidate.Servers[0].PreferredRoute is { } direct &&
            direct.ProxyId == candidate.Proxy.Id &&
            direct.ServerIds.Count == 1)
            return "подтверждённый";
        return "непроверенный";
    }

    private static bool IsComposedFromCachedEntry(RouteCandidate candidate)
    {
        for (var index = 1; index < candidate.Servers.Count - 1; index++)
        {
            var preferred = candidate.Servers[index].PreferredRoute;
            if (preferred is not null &&
                preferred.ProxyId == candidate.Proxy.Id &&
                preferred.ServerIds.Count > 1 &&
                preferred.ServerIds.SequenceEqual(
                    candidate.Servers.Take(index + 1).Select(server => server.Id)))
                return true;
        }
        return false;
    }

    private static string CandidateSignature(RouteCandidate candidate) =>
        $"{candidate.Proxy.Id:N}:{candidate.WithoutProxy}:{string.Join('/', candidate.Servers.Select(server => server.Id))}";

    /// <summary>
    /// Проверяет ограничения каждого перехода. Если сервер требует определённую
    /// предыдущую сессию, она должна находиться непосредственно перед ним.
    /// </summary>
    public static bool SatisfiesRouteConstraints(RouteCandidate candidate)
    {
        for (var index = 0; index < candidate.Servers.Count; index++)
        {
            var required = candidate.Servers[index].RequiredPreviousServerId;
            if (required is not null &&
                !(candidate.WithoutProxy && candidate.Servers.Count == 1) &&
                (index == 0 || candidate.Servers[index - 1].Id != required.Value))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Сохраняет защиту от прямого входа в совпадающий private IP, но разрешает
    /// другой последний переход, если эта конкретная связь уже была успешно
    /// проверена менеджером.
    /// </summary>
    public static bool SatisfiesRouteConstraints(ManagerConfig config, RouteCandidate candidate)
    {
        for (var index = 0; index < candidate.Servers.Count; index++)
        {
            var server = candidate.Servers[index];
            var required = server.RequiredPreviousServerId;
            if (required is null ||
                (candidate.WithoutProxy && candidate.Servers.Count == 1))
                continue;
            if (index == 0) return false;
            var previousId = candidate.Servers[index - 1].Id;
            if (previousId == required.Value) continue;
            if (!config.Links.Any(link =>
                    link.FromServerId == previousId &&
                    link.ToServerId == server.Id &&
                    link.LastSuccessUtc is not null))
                return false;
        }
        return true;
    }

    private static bool IsVerifiedPath(
        ManagerConfig config, IReadOnlyList<ManagedServer> path, Guid? proxyId = null)
    {
        for (var index = 0; index < path.Count - 1; index++)
        {
            var from = path[index].Id;
            var to = path[index + 1].Id;
            if (!config.Links.Any(link => link.FromServerId == from &&
                                         link.ToServerId == to &&
                                         link.LastSuccessUtc is not null &&
                                         (proxyId is null ||
                                          LinkStatisticsPolicy.ForProxy(link, proxyId.Value) is not null ||
                                          !LinkStatisticsPolicy.HasProxyEvidence(link))))
                return false;
        }
        return true;
    }

    /// <summary>Connects a known entry server directly through a global SOCKS endpoint.</summary>
    public static IReadOnlyList<RouteCandidate> DirectCandidates(ManagerConfig config, Guid serverId)
    {
        var server = config.FindServer(serverId);
        if (server is null) return [];
        var local = server.TryDirectWithoutJumphost
            ? new[] { new RouteCandidate(DirectConnectionProxy, [server], true) }
            : [];
        return local.Concat(OrderedProxies(config, server)
            .Select(proxy => new RouteCandidate(proxy, [server]))
            .Where(candidate => SatisfiesRouteConstraints(config, candidate)))
            .ToArray();
    }

    /// <summary>
    /// Builds the explicit SOCKS -> source -> target route used while discovering
    /// a link. It intentionally does not require that link to exist beforehand.
    /// </summary>
    public static IReadOnlyList<RouteCandidate> ViaCandidates(
        ManagerConfig config, Guid sourceId, Guid targetId)
    {
        var source = config.FindServer(sourceId);
        var target = config.FindServer(targetId);
        if (source is null || target is null || source.Id == target.Id) return [];
        return OrderedProxies(config, source)
            .Select(proxy => new RouteCandidate(proxy, [source, target]))
            .Where(candidate => SatisfiesRouteConstraints(config, candidate))
            .ToArray();
    }

    /// <summary>
    /// Builds normal routes to <paramref name="viaServerId"/> and appends the
    /// selected saved link as the mandatory final hop to
    /// <paramref name="targetId"/>. No candidate that omits that final hop is
    /// returned.
    /// </summary>
    public static IReadOnlyList<RouteCandidate> ForcedFinalHopCandidates(
        ManagerConfig config, Guid viaServerId, Guid targetId)
    {
        var via = config.FindServer(viaServerId);
        var target = config.FindServer(targetId);
        if (via is null || target is null || via.Id == target.Id) return [];
        if (!config.Links.Any(link =>
                link.FromServerId == via.Id &&
                link.ToServerId == target.Id &&
                link.LastSuccessUtc is not null))
            return [];

        var candidates = Candidates(config, via.Id)
            .Where(candidate => candidate.Servers.All(server => server.Id != target.Id))
            .Select(candidate => new RouteCandidate(
                candidate.Proxy,
                candidate.Servers.Append(target).ToArray(),
                candidate.WithoutProxy))
            .Where(candidate => SatisfiesRouteConstraints(config, candidate))
            .DistinctBy(CandidateSignature);
        return Rank(config, target, candidates);
    }

    public static IReadOnlyList<BaseProxy> OrderedProxies(
        ManagerConfig config, ManagedServer? importedHint = null) =>
        config.BaseProxies
            .Where(proxy => proxy.Enabled)
            .OrderBy(proxy => ProxyPriority(config, proxy, importedHint))
            .ThenBy(proxy => proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>
    /// Orders stopped managed entry points. A plain password jumphost is cheaper to
    /// start than one requiring TOTP or a post-login access script; an imported
    /// SOCKS hint only breaks ties between equally complex entry points.
    /// </summary>
    public static IReadOnlyList<BaseProxy> OrderedProxiesForStartup(
        ManagerConfig config, ManagedServer? importedHint = null) =>
        config.BaseProxies
            .Where(proxy => proxy.Enabled)
            .OrderBy(StartupComplexity)
            .ThenBy(proxy => ProxyHintPriority(proxy, importedHint))
            .ThenBy(proxy => proxy.LastStartupLatencyMs ?? proxy.LastConnectLatencyMs ?? double.MaxValue)
            .ThenByDescending(proxy => proxy.LastSuccessUtc)
            .ThenBy(proxy => proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static int StartupComplexity(BaseProxy proxy) =>
        string.IsNullOrWhiteSpace(proxy.TotpSecret) && string.IsNullOrWhiteSpace(proxy.PostLoginCommand) ? 0 :
        string.IsNullOrWhiteSpace(proxy.TotpSecret) || string.IsNullOrWhiteSpace(proxy.PostLoginCommand) ? 1 : 2;

    private static int ProxyPriority(ManagerConfig config, BaseProxy proxy, ManagedServer? importedHint)
    {
        if (importedHint?.ImportedProxy is { Method: 2 } imported &&
            imported.Port == proxy.Port && SameProxyHost(imported.Host, proxy.Host))
            return 0;
        var preferredProxyId = importedHint?.PreferredProxyId ??
                               importedHint?.PreferredRoute?.ProxyId ??
                               config.PreferredProxyId;
        return preferredProxyId == proxy.Id ? 1 : 2;
    }

    private static int ProxyHintPriority(BaseProxy proxy, ManagedServer? server)
    {
        if (server?.ImportedProxy is { Method: 2 } imported &&
            imported.Port == proxy.Port && SameProxyHost(imported.Host, proxy.Host))
            return 0;
        return 1;
    }

    private static int CandidateClass(ManagerConfig config, RouteCandidate candidate)
    {
        if (candidate.WithoutProxy) return 0;
        if (candidate.Servers.Count == 1)
        {
            // Direct route: prefer it only if the server was ever reached
            // directly (PreferredRoute with 1 hop).  Otherwise deprioritize
            // so that proven multi-hop paths are tried first — a verified
            // incoming link means the server is reachable via that link,
            // NOT that it is reachable directly from the proxy.
            var target = candidate.Servers[0];
            var hasDirectSuccess = target.PreferredRoute is not null &&
                target.PreferredRoute.ServerIds.Count == 1 &&
                target.PreferredRoute.ServerIds[0] == target.Id;
            return hasDirectSuccess ? 0 : 2;
        }
        return HasProvenEntry(config, candidate) &&
               IsVerifiedPath(config, candidate.Servers, candidate.Proxy.Id)
            ? 1
            : 2;
    }

    private static bool HasProvenEntry(ManagerConfig config, RouteCandidate candidate)
    {
        if (candidate.WithoutProxy) return true;
        var entry = candidate.Servers[0];
        var preferred = entry.PreferredRoute;
        return preferred is not null &&
               preferred.ProxyId == candidate.Proxy.Id &&
               preferred.ServerIds.Count == 1 &&
               preferred.ServerIds[0] == entry.Id;
    }

    private static int LinkProxyPriority(ManagerConfig config, RouteCandidate candidate)
    {
        var links = PathLinks(config, candidate.Servers).ToArray();
        if (links.Length == 0) return 1;
        if (links.All(link => LinkStatisticsPolicy.ForProxy(link, candidate.Proxy.Id) is not null)) return 0;
        if (links.Any(link => LinkStatisticsPolicy.HasProxyEvidence(link) &&
                              LinkStatisticsPolicy.ForProxy(link, candidate.Proxy.Id) is null)) return 2;
        return 1;
    }

    private static double PathLatency(ManagerConfig config, RouteCandidate candidate)
    {
        var known = PathLinks(config, candidate.Servers)
            .Select(link => LinkStatisticsPolicy.ForProxy(link, candidate.Proxy.Id)?.LatencyMs)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return known.Length == 0 ? candidate.Proxy.LastConnectLatencyMs ?? double.MaxValue : known.Sum();
    }

    private static DateTimeOffset PathLastSuccess(ManagerConfig config, RouteCandidate candidate) =>
        PathLinks(config, candidate.Servers)
            .Select(link => LinkStatisticsPolicy.ForProxy(link, candidate.Proxy.Id)?.LastSuccessUtc ??
                            link.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(candidate.Proxy.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .Min();

    private static IEnumerable<ServerLink> PathLinks(
        ManagerConfig config, IReadOnlyList<ManagedServer> servers)
    {
        for (var index = 0; index < servers.Count - 1; index++)
        {
            var from = servers[index].Id;
            var to = servers[index + 1].Id;
            var link = config.Links.FirstOrDefault(item => item.FromServerId == from && item.ToServerId == to);
            if (link is not null) yield return link;
        }
    }

    public static BaseProxy? PreferredProxyForTarget(ManagerConfig config, Guid targetId)
    {
        return Candidates(config, targetId).FirstOrDefault()?.Proxy;
    }

    private static bool SameProxyHost(string left, string right) =>
        ProxyEndpointComparer.HostsEquivalent(left, right);
}

namespace KiTTYManager.Core;

public sealed record ConnectivityBatch(ManagedServer Source, IReadOnlyList<Guid> TargetIds);

public static class ConnectivityBatchPlanner
{
    public static IReadOnlyList<ManagedServer> SelectedServers(
        ManagerConfig config, IEnumerable<Guid> serverIds)
    {
        return serverIds
            .Distinct()
            .Select(config.FindServer)
            .Where(server => server is not null)
            .Cast<ManagedServer>()
            .ToArray();
    }

    /// <summary>
    /// Search source for the link-map picker. Unlike the map model, this returns
    /// every configured session, including sessions without saved links.
    /// </summary>
    public static IReadOnlyList<ManagedServer> SearchServers(
        ManagerConfig config, string? query)
    {
        var text = query?.Trim() ?? "";
        return config.AllServers()
            .Where(server => text.Length == 0 || SearchText(config, server)
                .Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(server => server.Id)
            .ToArray();
    }

    public static void UpdateSelection(
        ISet<Guid> selection, IEnumerable<Guid> added, IEnumerable<Guid> removed)
    {
        foreach (var id in removed) selection.Remove(id);
        foreach (var id in added) selection.Add(id);
    }

    private static string SearchText(ManagerConfig config, ManagedServer server)
    {
        var group = config.FindServerGroup(server.Id);
        var groupPath = group is null ? "Без группы" : config.GroupPath(group.Id);
        return string.Join('\n', server.Name, server.Host, server.Port.ToString(),
            server.Username, server.Endpoint, groupPath);
    }

    public static IReadOnlyList<(ManagedServer A, ManagedServer B)> NewPairs(
        ManagerConfig config, IEnumerable<ManagedServer> servers)
    {
        var existing = config.Links
            .Select(link => OrderedPair(link.FromServerId, link.ToServerId))
            .ToHashSet();
        return Pairs(servers)
            .Where(pair => !existing.Contains(OrderedPair(pair.A.Id, pair.B.Id)))
            .ToArray();
    }

    public static IReadOnlyList<ConnectivityBatch> OneDirectionPerPair(IEnumerable<ManagedServer> servers)
    {
        var distinct = servers.DistinctBy(server => server.Id).ToArray();
        return distinct
            .Select((source, index) => new ConnectivityBatch(source,
                distinct.Skip(index + 1).Select(target => target.Id).ToArray()))
            .Where(batch => batch.TargetIds.Count > 0)
            .ToArray();
    }

    public static IReadOnlyList<ConnectivityBatch> AllDirected(IEnumerable<ManagedServer> servers)
    {
        var distinct = servers.DistinctBy(server => server.Id).ToArray();
        return distinct
            .Select(source => new ConnectivityBatch(source,
                distinct.Where(target => target.Id != source.Id).Select(target => target.Id).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Returns unordered pairs (each pair once) for bidirectional checking.
    /// </summary>
    public static IReadOnlyList<(ManagedServer A, ManagedServer B)> Pairs(IEnumerable<ManagedServer> servers)
    {
        var distinct = servers.DistinctBy(server => server.Id).ToArray();
        var pairs = new List<(ManagedServer, ManagedServer)>();
        for (var i = 0; i < distinct.Length; i++)
            for (var j = i + 1; j < distinct.Length; j++)
                pairs.Add((distinct[i], distinct[j]));
        return pairs;
    }

    /// <summary>
    /// Проверяет каждую пару ровно один раз, но сначала завершает пары с уже
    /// подтверждённым входным сервером. Найденные в этом этапе связи позволяют
    /// зависимым парам использовать новый маршрут в той же операции.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(ManagedServer A, ManagedServer B)>> DependencyStages(
        ManagerConfig config,
        IEnumerable<ManagedServer> servers,
        IEnumerable<(ManagedServer A, ManagedServer B)> pairs)
    {
        var pairList = pairs.ToArray();
        if (pairList.Length == 0) return [];
        var directIds = DirectlyReachable(config, servers.Select(server => server.Id)).ToHashSet();
        var first = pairList.Where(pair =>
            directIds.Contains(pair.A.Id) || directIds.Contains(pair.B.Id)).ToArray();
        var dependent = pairList.Where(pair =>
            !directIds.Contains(pair.A.Id) && !directIds.Contains(pair.B.Id)).ToArray();
        if (first.Length == 0 || dependent.Length == 0) return [pairList];
        return [first, dependent];
    }

    /// <summary>
    /// Возвращает только пары, для которых текущая операция ещё не получила
    /// ни одного успешного направления. Используется для одной точечной
    /// допроверки после того, как соседние пары расширили известную топологию.
    /// </summary>
    public static IReadOnlyList<(ManagedServer A, ManagedServer B)> UnsuccessfulPairs(
        IEnumerable<(ManagedServer A, ManagedServer B)> pairs,
        IEnumerable<ConnectivityResult> results)
    {
        var successful = results
            .Where(result => result.Success)
            .Select(result => OrderedPair(result.SourceId, result.TargetId))
            .ToHashSet();
        return pairs
            .Where(pair => !successful.Contains(OrderedPair(pair.A.Id, pair.B.Id)))
            .ToArray();
    }

    private static (Guid A, Guid B) OrderedPair(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? (left, right) : (right, left);

    /// <summary>
    /// Filters out servers that are only reachable through other servers in the
    /// same group.  A server is "directly reachable" when it has a proven direct
    /// route (PreferredRoute with 1 hop) or a verified incoming link from a
    /// server outside the group.  Dependent servers are skipped because if the
    /// gateway server is unreachable, they are too.
    /// </summary>
    public static IReadOnlyList<Guid> DirectlyReachable(
        ManagerConfig config, IEnumerable<Guid> groupServerIds)
    {
        var groupIds = groupServerIds.ToHashSet();
        var result = new List<Guid>();
        foreach (var id in groupIds)
        {
            var server = config.FindServer(id);
            if (server is null) continue;

            // Proven direct success: PreferredRoute with exactly 1 hop.
            var hasProvenDirect = server.PreferredRoute is not null &&
                server.PreferredRoute.ServerIds.Count == 1 &&
                server.PreferredRoute.ServerIds[0] == id;
            if (hasProvenDirect) { result.Add(id); continue; }

            // Verified incoming link from outside the group.
            var hasExternalVerifiedLink = config.Links.Any(link =>
                link.ToServerId == id && link.LastSuccessUtc is not null &&
                !groupIds.Contains(link.FromServerId));
            if (hasExternalVerifiedLink) { result.Add(id); continue; }

            // An arbitrary multi-hop route containing an external server does not
            // make the target an entry server: it may still depend on another
            // server in this group. Such targets belong to the deferred phase.
        }
        return result;
    }

    /// <summary>
    /// Selects a group server that already has a valid route through servers
    /// outside the group. One connection to this anchor can discover all of its
    /// group neighbours without reopening the same long chain for every pair.
    /// </summary>
    public static ManagedServer? RemoteAnchor(
        ManagerConfig config, IEnumerable<ManagedServer> servers)
    {
        var group = servers.DistinctBy(server => server.Id).ToArray();
        var groupIds = group.Select(server => server.Id).ToHashSet();
        return group
            .Select(server => (Server: server,
                Route: server.PreferredRoute is null
                    ? null
                    : RoutePlanner.CandidateFromCached(config, server.PreferredRoute)))
            .Where(item => item.Route is not null &&
                           item.Route.Servers.Take(item.Route.Servers.Count - 1)
                               .Any(server => !groupIds.Contains(server.Id)))
            .OrderBy(item => item.Route!.Servers.Count)
            .ThenBy(item => item.Server.PreferredRoute!.LatencyMs ?? double.MaxValue)
            .ThenBy(item => item.Server.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Server)
            .FirstOrDefault();
    }
}

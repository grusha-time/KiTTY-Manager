namespace KiTTYManager.Core;

public sealed record ConnectivityPairChoice(
    Guid SourceId,
    Guid TargetId,
    string SourceName,
    string TargetName,
    bool Selected = true,
    bool ForwardVerified = false,
    bool ReverseVerified = false)
{
    public string Display => (ForwardVerified, ReverseVerified) switch
    {
        (true, true) => $"{SourceName} → {TargetName} (уже подтверждено)",
        (true, false) => $"{SourceName} → {TargetName} (исходящее уже подтверждено)",
        (false, true) => $"{SourceName} → {TargetName} (обратное подтверждено)",
        _ => $"{SourceName} → {TargetName}"
    };
}

public static class ConnectivityPairSelectionPolicy
{
    public static IReadOnlyList<ConnectivityPairChoice> Build(
        ManagerConfig config,
        IEnumerable<ManagedServer> servers,
        bool skipExisting,
        bool skipConfirmedOnly = false)
    {
        var items = servers.DistinctBy(x => x.Id).ToArray();
        var result = new List<ConnectivityPairChoice>();
        foreach (var source in items)
        foreach (var target in items)
        {
            if (source.Id == target.Id) continue;
            var fwd = config.Links.FirstOrDefault(x => x.FromServerId == source.Id && x.ToServerId == target.Id);
            var rev = config.Links.FirstOrDefault(x => x.FromServerId == target.Id && x.ToServerId == source.Id);

            if (skipExisting)
            {
                if (skipConfirmedOnly)
                {
                    if (fwd?.LastSuccessUtc is not null) continue;
                }
                else
                {
                    if (fwd is not null) continue;
                }
            }

            var fwdVerified = fwd?.LastSuccessUtc is not null;
            var revVerified = rev?.LastSuccessUtc is not null;
            result.Add(new(
                source.Id, target.Id, source.Name, target.Name,
                Selected: true,
                ForwardVerified: fwdVerified,
                ReverseVerified: revVerified));
        }
        return result;
    }

    public static IReadOnlyList<ConnectivityPairChoice> ExcludeServers(
        IEnumerable<ConnectivityPairChoice> pairs, IEnumerable<Guid> excludedServerIds)
    {
        var excluded = excludedServerIds.ToHashSet();
        return pairs.Where(x => !excluded.Contains(x.SourceId) && !excluded.Contains(x.TargetId)).ToArray();
    }

    public static IReadOnlyList<ConnectivityPairChoice> BuildFromSource(
        ManagerConfig config,
        ManagedServer source,
        IEnumerable<ManagedServer> targets,
        bool skipExisting,
        bool skipConfirmedOnly = false)
    {
        return targets.DistinctBy(x => x.Id)
            .Where(target => target.Id != source.Id)
            .Where(target =>
            {
                if (!skipExisting) return true;
                var fwd = config.Links.FirstOrDefault(link =>
                    link.FromServerId == source.Id && link.ToServerId == target.Id);
                if (skipConfirmedOnly)
                    return fwd?.LastSuccessUtc is null;
                return fwd is null;
            })
            .Select(target =>
            {
                var fwd = config.Links.FirstOrDefault(link =>
                    link.FromServerId == source.Id && link.ToServerId == target.Id);
                var rev = config.Links.FirstOrDefault(link =>
                    link.FromServerId == target.Id && link.ToServerId == source.Id);
                return new ConnectivityPairChoice(
                    source.Id, target.Id, source.Name, target.Name,
                    Selected: true,
                    ForwardVerified: fwd?.LastSuccessUtc is not null,
                    ReverseVerified: rev?.LastSuccessUtc is not null);
            })
            .ToArray();
    }

    public static IReadOnlyList<ConnectivityPairChoice> Selected(
        IEnumerable<ConnectivityPairChoice> pairs) => pairs
        .Where(pair => pair.Selected && pair.SourceId != pair.TargetId)
        .DistinctBy(pair => (pair.SourceId, pair.TargetId))
        .ToArray();
}

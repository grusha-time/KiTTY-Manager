namespace KiTTYManager.Core;

public sealed record ConnectivitySelectionPlan(
    IReadOnlyList<Guid> PrimaryTargetIds,
    IReadOnlyList<Guid> DeferredTargetIds);

public static class ConnectivitySelectionPlanner
{
    public static ConnectivitySelectionPlan Build(
        ManagerConfig config,
        Guid sourceId,
        IEnumerable<IReadOnlyList<Guid>> selectedGroups,
        IEnumerable<Guid> selectedServers)
    {
        var primary = selectedServers.Where(id => id != sourceId).ToHashSet();
        var all = primary.ToHashSet();

        foreach (var ids in selectedGroups)
        {
            var members = ids.Where(id => id != sourceId).Distinct().ToArray();
            all.UnionWith(members);
            if (ids.Contains(sourceId))
            {
                primary.UnionWith(members);
                continue;
            }

            primary.UnionWith(ConnectivityBatchPlanner.DirectlyReachable(config, ids)
                .Where(id => id != sourceId));
        }

        primary.IntersectWith(all);
        var deferred = all.Where(id => !primary.Contains(id)).ToArray();
        return new(primary.ToArray(), deferred);
    }
}

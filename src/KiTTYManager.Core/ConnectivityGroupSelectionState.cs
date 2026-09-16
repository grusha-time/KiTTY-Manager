namespace KiTTYManager.Core;

public sealed class ConnectivityGroupSelectionState
{
    private readonly ManagerConfig config;
    private readonly Guid? fixedSourceId;
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> selectedGroups = [];
    private readonly HashSet<Guid> explicitlyIncludedServerIds = [];
    private readonly HashSet<Guid> explicitlyExcludedServerIds = [];
    private bool filterIndependentGroupServers = true;

    public bool FilterIndependentGroupServers
    {
        get => filterIndependentGroupServers;
        set
        {
            if (filterIndependentGroupServers == value) return;
            filterIndependentGroupServers = value;
            Recalculate();
        }
    }

    public IReadOnlySet<Guid> SelectedIds { get; private set; } = new HashSet<Guid>();

    public ConnectivityGroupSelectionState(ManagerConfig config, IEnumerable<Guid> initialServerIds, Guid? fixedSourceId = null, bool filterIndependentGroupServers = true)
    {
        this.config = config;
        this.fixedSourceId = fixedSourceId;
        this.filterIndependentGroupServers = filterIndependentGroupServers;
        foreach (var id in initialServerIds)
        {
            if (id != fixedSourceId)
                explicitlyIncludedServerIds.Add(id);
        }
        Recalculate();
    }

    public void AddServers(IEnumerable<Guid> serverIds)
    {
        foreach (var id in serverIds)
        {
            if (id != fixedSourceId)
            {
                explicitlyExcludedServerIds.Remove(id);
                explicitlyIncludedServerIds.Add(id);
            }
        }
        Recalculate();
    }

    public void ToggleGroup(Guid groupId, IReadOnlyList<Guid> groupRowServerIds)
    {
        var targetIds = groupRowServerIds.Where(id => id != fixedSourceId).ToArray();
        if (targetIds.Length == 0) return;

        var fullGroupComposition = GetFullGroupComposition(groupId);
        var effectiveGroupTargets = GetEffectiveTargets(fullGroupComposition, targetIds);
        // Снимаем, если все эффективные цели выбраны (при их наличии) ИЛИ если все цели строки уже выбраны (например, когда поиск скрыл независимые, а зависимые выбраны вручную)
        var shouldDeselect = (effectiveGroupTargets.Count > 0 && effectiveGroupTargets.All(SelectedIds.Contains))
            || (targetIds.Length > 0 && targetIds.All(SelectedIds.Contains));

        if (shouldDeselect)
        {
            if (selectedGroups.TryGetValue(groupId, out var existingTargets))
            {
                var remaining = existingTargets.Where(id => !targetIds.Contains(id)).ToArray();
                if (remaining.Length > 0)
                    selectedGroups[groupId] = remaining;
                else
                    selectedGroups.Remove(groupId);
            }
            else
            {
                selectedGroups.Remove(groupId);
            }

            var coveredByAnotherGroup = selectedGroups.Keys.Any(otherId => IsDescendantGroup(groupId, otherId));
            foreach (var id in targetIds)
            {
                explicitlyIncludedServerIds.Remove(id);
                if (coveredByAnotherGroup)
                    explicitlyExcludedServerIds.Add(id);
                else
                    explicitlyExcludedServerIds.Remove(id);
            }

            var childGroupIds = selectedGroups.Keys.Where(otherId => IsDescendantGroup(otherId, groupId)).ToArray();
            foreach (var childId in childGroupIds)
            {
                var remainingChildTargets = selectedGroups[childId].Where(id => !targetIds.Contains(id)).ToArray();
                if (remainingChildTargets.Length > 0)
                    selectedGroups[childId] = remainingChildTargets;
                else
                    selectedGroups.Remove(childId);
            }
        }
        else
        {
            if (selectedGroups.TryGetValue(groupId, out var existingTargets))
            {
                selectedGroups[groupId] = existingTargets.Concat(targetIds).Distinct().ToArray();
            }
            else
            {
                selectedGroups[groupId] = targetIds;
            }

            foreach (var id in targetIds)
            {
                explicitlyExcludedServerIds.Remove(id);
            }
        }
        Recalculate();
    }

    private IReadOnlyList<Guid> GetFullGroupComposition(Guid groupId)
    {
        if (groupId == Guid.Empty)
        {
            return config.UngroupedServers.Select(s => s.Id).ToArray();
        }

        var group = config.FindGroup(groupId);
        if (group is not null)
        {
            return AllServerIds(group).ToArray();
        }

        return [];
    }

    private IReadOnlyList<Guid> GetEffectiveTargets(IReadOnlyList<Guid> fullComposition, IReadOnlyList<Guid> targetIds)
    {
        if (!filterIndependentGroupServers) return targetIds;

        var allIndependent = ConnectivityBatchPlanner.DirectlyReachable(config, fullComposition)
            .Where(id => id != fixedSourceId)
            .ToHashSet();

        // Если в полном составе группы есть независимые серверы — выбираем только те из них, которые входят в targetIds строки (без fallback на зависимые!)
        if (allIndependent.Count > 0)
        {
            return targetIds.Where(allIndependent.Contains).ToArray();
        }

        // Если в группе вообще нет ни одного независимого сервера — fallback на все серверы строки
        return targetIds;
    }

    private bool IsDescendantGroup(Guid potentialChildId, Guid potentialParentId)
    {
        if (potentialChildId == potentialParentId || potentialParentId == Guid.Empty || potentialChildId == Guid.Empty)
            return false;
        var parentGroup = config.FindGroup(potentialParentId);
        if (parentGroup is null) return false;
        return ConfigIndex.AllGroups(new ManagerConfig { Groups = parentGroup.Groups })
            .Any(g => g.Id == potentialChildId);
    }

    public void ToggleServer(Guid serverId)
    {
        if (serverId == fixedSourceId) return;

        if (SelectedIds.Contains(serverId))
        {
            explicitlyIncludedServerIds.Remove(serverId);
            explicitlyExcludedServerIds.Add(serverId);
        }
        else
        {
            explicitlyExcludedServerIds.Remove(serverId);
            explicitlyIncludedServerIds.Add(serverId);
        }
        Recalculate();
    }

    public void SetExplicitSelection(IEnumerable<Guid> serverIds)
    {
        selectedGroups.Clear();
        explicitlyIncludedServerIds.Clear();
        explicitlyExcludedServerIds.Clear();
        foreach (var id in serverIds)
        {
            if (id != fixedSourceId)
                explicitlyIncludedServerIds.Add(id);
        }
        Recalculate();
    }

    public void Clear()
    {
        selectedGroups.Clear();
        explicitlyIncludedServerIds.Clear();
        explicitlyExcludedServerIds.Clear();
        Recalculate();
    }

    public void Recalculate()
    {
        var result = new HashSet<Guid>();

        foreach (var (groupId, groupTargetIds) in selectedGroups)
        {
            var targets = groupTargetIds.Where(id => id != fixedSourceId).ToArray();
            if (targets.Length == 0) continue;

            var fullComposition = GetFullGroupComposition(groupId);
            var effective = GetEffectiveTargets(fullComposition, targets);
            foreach (var id in effective)
                result.Add(id);
        }

        // Применяем ручные включения и исключения
        foreach (var id in explicitlyIncludedServerIds)
            result.Add(id);

        foreach (var id in explicitlyExcludedServerIds)
            result.Remove(id);

        if (fixedSourceId.HasValue)
            result.Remove(fixedSourceId.Value);

        SelectedIds = result;
    }

    private static IEnumerable<Guid> AllServerIds(ServerGroup group) =>
        group.Servers.Select(s => s.Id).Concat(group.Groups.SelectMany(AllServerIds));
}

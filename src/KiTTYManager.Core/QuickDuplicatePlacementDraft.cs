namespace KiTTYManager.Core;

public sealed record NewGroupDraft(Guid Id, string Name, Guid? ParentId);

public sealed class QuickDuplicatePlacementDraft
{
    private readonly List<NewGroupDraft> newGroups = [];

    public IReadOnlyList<NewGroupDraft> NewGroups => newGroups;
    public Guid? SelectedGroupId { get; set; }

    public QuickDuplicatePlacementDraft(ManagerConfig config, ManagedServer source)
    {
        SelectedGroupId = config.FindServerGroup(source.Id)?.Id;
    }

    public QuickDuplicatePlacementDraft(Guid? initialGroupId = null)
    {
        SelectedGroupId = initialGroupId;
    }

    public NewGroupDraft CreateGroup(string name, Guid? parentId = null)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Название группы не может быть пустым.", nameof(name));

        var draft = new NewGroupDraft(Guid.NewGuid(), trimmed, parentId);
        newGroups.Add(draft);
        SelectedGroupId = draft.Id;
        return draft;
    }

    public (Guid? TargetGroupId, bool FallbackOccurred) ApplyTo(ManagerConfig config)
    {
        var failedDraftIds = new HashSet<Guid>();

        // Add new groups in order of creation
        foreach (var draft in newGroups)
        {
            // If group already exists in config, don't duplicate
            if (config.FindGroup(draft.Id) is not null) continue;

            if (draft.ParentId.HasValue)
            {
                var parent = config.FindGroup(draft.ParentId.Value);
                if (parent is not null && !failedDraftIds.Contains(draft.ParentId.Value))
                {
                    parent.Groups.Add(new ServerGroup
                    {
                        Id = draft.Id,
                        Name = draft.Name
                    });
                }
                else
                {
                    failedDraftIds.Add(draft.Id);
                }
            }
            else
            {
                config.Groups.Add(new ServerGroup
                {
                    Id = draft.Id,
                    Name = draft.Name
                });
            }
        }

        if (!SelectedGroupId.HasValue)
            return (null, false);

        if (failedDraftIds.Contains(SelectedGroupId.Value))
            return (null, true);

        var target = config.FindGroup(SelectedGroupId.Value);
        if (target is not null)
            return (target.Id, false);

        // If target group was deleted or missing, fall back to ungrouped (null)
        return (null, true);
    }

    public string GetDisplayPath(ManagerConfig config)
    {
        if (!SelectedGroupId.HasValue)
            return "Без группы (верхний уровень)";

        var id = SelectedGroupId.Value;

        // Check in config first
        var path = config.GroupPath(id);
        if (!string.IsNullOrEmpty(path))
            return path;

        // Check in draft new groups
        var draft = newGroups.FirstOrDefault(item => item.Id == id);
        if (draft is null)
            return "Без группы (верхний уровень)";

        var parts = new List<string> { draft.Name };
        var parentId = draft.ParentId;
        while (parentId.HasValue)
        {
            var pDraft = newGroups.FirstOrDefault(item => item.Id == parentId.Value);
            if (pDraft is not null)
            {
                parts.Insert(0, pDraft.Name);
                parentId = pDraft.ParentId;
            }
            else
            {
                var pPath = config.GroupPath(parentId.Value);
                if (!string.IsNullOrEmpty(pPath))
                    parts.Insert(0, pPath);
                break;
            }
        }
        return string.Join(" / ", parts);
    }
}

namespace KiTTYManager.Core;

public sealed record GroupSelectionRow(
    Guid GroupId,
    string Name,
    string Path,
    int Depth,
    int ServerCount,
    bool IsNew = false);

public static class GroupSelectionPolicy
{
    public static IReadOnlyList<GroupSelectionRow> Build(
        ManagerConfig config,
        IEnumerable<NewGroupDraft>? newGroups = null,
        string? searchText = null)
    {
        var roots = BuildHierarchy(config, newGroups);
        var search = searchText?.Trim() ?? "";
        var rows = new List<GroupSelectionRow>();

        foreach (var root in roots)
            AddNode(rows, root, "", 0, search);

        return rows;
    }

    private static List<HierarchyNode> BuildHierarchy(
        ManagerConfig config,
        IEnumerable<NewGroupDraft>? newGroups)
    {
        var allNodes = new Dictionary<Guid, HierarchyNode>();
        var roots = new List<HierarchyNode>();

        HierarchyNode ConvertGroup(ServerGroup group)
        {
            var node = new HierarchyNode
            {
                Id = group.Id,
                Name = group.Name,
                ServerCount = group.Servers.Count,
                IsNew = false
            };
            allNodes[group.Id] = node;
            foreach (var child in group.Groups)
            {
                var childNode = ConvertGroup(child);
                node.Children.Add(childNode);
            }
            return node;
        }

        foreach (var rootGroup in config.Groups)
            roots.Add(ConvertGroup(rootGroup));

        if (newGroups is not null)
        {
            foreach (var draft in newGroups)
            {
                var node = new HierarchyNode
                {
                    Id = draft.Id,
                    Name = draft.Name,
                    ServerCount = 0,
                    IsNew = true
                };
                allNodes[draft.Id] = node;

                if (draft.ParentId.HasValue && allNodes.TryGetValue(draft.ParentId.Value, out var parentNode))
                    parentNode.Children.Add(node);
                else
                    roots.Add(node);
            }
        }

        return roots;
    }

    private static bool AddNode(
        List<GroupSelectionRow> rows,
        HierarchyNode node,
        string parentPath,
        int depth,
        string search)
    {
        var path = string.IsNullOrEmpty(parentPath) ? node.Name : $"{parentPath} / {node.Name}";
        var selfMatches = string.IsNullOrEmpty(search) ||
                          Contains(node.Name, search) ||
                          Contains(path, search);

        var childRows = new List<GroupSelectionRow>();
        foreach (var child in node.Children)
            AddNode(childRows, child, path, depth + 1, search);

        var anyChildMatches = childRows.Count > 0;
        if (!selfMatches && !anyChildMatches)
            return false;

        var totalServerCount = CalculateTotalServerCount(node);
        rows.Add(new GroupSelectionRow(node.Id, node.Name, path, depth, totalServerCount, node.IsNew));
        rows.AddRange(childRows);
        return true;
    }

    private static int CalculateTotalServerCount(HierarchyNode node) =>
        node.ServerCount + node.Children.Sum(CalculateTotalServerCount);

    private static bool Contains(string? value, string search) =>
        search.Length == 0 || (value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private sealed class HierarchyNode
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int ServerCount { get; set; }
        public bool IsNew { get; set; }
        public List<HierarchyNode> Children { get; set; } = [];
    }
}

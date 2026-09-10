namespace KiTTYManager.Core;

public sealed record ServerSelectionRow(
    Guid? ServerId,
    IReadOnlyList<Guid> ServerIds,
    string Name,
    string Details,
    int Depth,
    bool IsGroup,
    bool? IsChecked);

public static class ServerSelectionPolicy
{
    public static IReadOnlyList<ServerSelectionRow> Build(
        ManagerConfig config,
        IReadOnlySet<Guid> selectedIds,
        string? searchText = null,
        Guid? excludedServerId = null)
    {
        var rows = new List<ServerSelectionRow>();
        var search = searchText?.Trim() ?? "";
        foreach (var group in config.Groups)
            AddGroup(rows, group, "", 0, search, selectedIds, excludedServerId);

        var ungrouped = config.UngroupedServers
            .Where(server => server.Id != excludedServerId && Matches(server, "Без группы", search))
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (ungrouped.Length > 0)
        {
            AddGroupRow(rows, "Без группы", 0, ungrouped.Select(server => server.Id).ToArray(), selectedIds);
            foreach (var server in ungrouped)
                AddServerRow(rows, server, "Без группы", 1, selectedIds);
        }
        return rows;
    }

    private static bool AddGroup(List<ServerSelectionRow> rows, ServerGroup group, string parentPath,
        int depth, string search, IReadOnlySet<Guid> selectedIds, Guid? excludedServerId)
    {
        var path = string.IsNullOrEmpty(parentPath) ? group.Name : $"{parentPath} / {group.Name}";
        var groupMatches = Contains(path, search);
        var visibleServers = group.Servers
            .Where(server => server.Id != excludedServerId && (groupMatches || Matches(server, path, search)))
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var childRows = new List<ServerSelectionRow>();
        foreach (var child in group.Groups.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            AddGroup(childRows, child, path, depth + 1, groupMatches ? "" : search, selectedIds, excludedServerId);
        if (!groupMatches && visibleServers.Length == 0 && childRows.Count == 0) return false;

        var allIds = AllServerIds(group).Where(id => id != excludedServerId).ToArray();
        if (allIds.Length == 0) return false;
        AddGroupRow(rows, group.Name, depth, allIds, selectedIds);
        foreach (var server in visibleServers) AddServerRow(rows, server, path, depth + 1, selectedIds);
        rows.AddRange(childRows);
        return true;
    }

    private static void AddGroupRow(List<ServerSelectionRow> rows, string name, int depth,
        Guid[] serverIds, IReadOnlySet<Guid> selectedIds)
    {
        var selected = serverIds.Count(selectedIds.Contains);
        rows.Add(new(null, serverIds, name, $"Сессий: {serverIds.Length}", depth, true,
            selected == 0 ? false : selected == serverIds.Length ? true : null));
    }

    private static void AddServerRow(List<ServerSelectionRow> rows, ManagedServer server,
        string groupPath, int depth, IReadOnlySet<Guid> selectedIds) =>
        rows.Add(new(server.Id, [server.Id], server.Name, $"{server.Endpoint}  ·  {groupPath}",
            depth, false, selectedIds.Contains(server.Id)));

    private static IEnumerable<Guid> AllServerIds(ServerGroup group) =>
        group.Servers.Select(server => server.Id).Concat(group.Groups.SelectMany(AllServerIds));

    private static bool Matches(ManagedServer server, string groupPath, string search) =>
        Contains(server.Name, search) || Contains(server.Endpoint, search) ||
        Contains(server.Username, search) || Contains(groupPath, search);

    private static bool Contains(string? value, string search) =>
        search.Length == 0 || (value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}

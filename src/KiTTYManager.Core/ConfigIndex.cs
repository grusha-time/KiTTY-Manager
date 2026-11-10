namespace KiTTYManager.Core;

public static class ConfigIndex
{
    public static IEnumerable<ServerGroup> AllGroups(this ManagerConfig config) =>
        config.Groups.SelectMany(Flatten);

    public static IEnumerable<ManagedServer> AllServers(this ManagerConfig config) =>
        config.UngroupedServers.Concat(config.AllGroups().SelectMany(g => g.Servers));

    public static ManagedServer? FindServer(this ManagerConfig config, Guid id) =>
        config.AllServers().FirstOrDefault(s => s.Id == id);

    public static ServerGroup? FindGroup(this ManagerConfig config, Guid id) =>
        config.AllGroups().FirstOrDefault(g => g.Id == id);

    public static ServerGroup? FindServerGroup(this ManagerConfig config, Guid serverId) =>
        config.AllGroups().FirstOrDefault(group => group.Servers.Any(server => server.Id == serverId));

    public static bool MoveServerToGroup(this ManagerConfig config, Guid serverId, Guid? groupId)
    {
        var server = config.FindServer(serverId);
        var target = groupId.HasValue ? config.FindGroup(groupId.Value) : null;
        if (server is null || groupId.HasValue && target is null) return false;

        config.UngroupedServers.RemoveAll(s => s.Id == serverId);
        foreach (var group in config.AllGroups()) group.Servers.RemoveAll(s => s.Id == serverId);
        (target?.Servers ?? config.UngroupedServers).Add(server);
        return true;
    }

    public static bool RemoveServer(this ManagerConfig config, Guid serverId)
    {
        var removed = config.UngroupedServers.RemoveAll(s => s.Id == serverId) > 0;
        foreach (var group in config.AllGroups())
            removed |= group.Servers.RemoveAll(s => s.Id == serverId) > 0;
        config.Links.RemoveAll(link => link.FromServerId == serverId || link.ToServerId == serverId);
        foreach (var server in config.AllServers())
        {
            if (server.RequiredPreviousServerId == serverId)
                server.RequiredPreviousServerId = null;
            if (server.PreferredRoute?.ServerIds.Contains(serverId) == true)
                server.PreferredRoute = null;
        }
        return removed;
    }

    public static string GroupPath(this ManagerConfig config, Guid groupId)
    {
        foreach (var group in config.Groups)
        {
            var path = FindPath(group, groupId, []);
            if (path is not null) return string.Join(" / ", path);
        }
        return "";
    }

    public static IReadOnlyList<ServerGroup> GroupAncestry(this ManagerConfig config, Guid groupId)
    {
        foreach (var group in config.Groups)
        {
            var ancestry = FindAncestry(group, groupId, []);
            if (ancestry is not null) return ancestry;
        }
        return [];
    }

    private static IEnumerable<ServerGroup> Flatten(ServerGroup group)
    {
        yield return group;
        foreach (var child in group.Groups.SelectMany(Flatten)) yield return child;
    }

    private static IReadOnlyList<string>? FindPath(ServerGroup group, Guid id, List<string> parents)
    {
        var path = new List<string>(parents) { group.Name };
        if (group.Id == id) return path;
        foreach (var child in group.Groups)
        {
            var result = FindPath(child, id, path);
            if (result is not null) return result;
        }
        return null;
    }

    private static List<ServerGroup>? FindAncestry(ServerGroup group, Guid id, List<ServerGroup> parents)
    {
        var path = new List<ServerGroup>(parents) { group };
        if (group.Id == id) return path;
        foreach (var child in group.Groups)
        {
            var result = FindAncestry(child, id, path);
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>
    /// Builds a dictionary mapping each group ID to its formatted path (e.g. "Root / Subgroup") in a single tree walk.
    /// </summary>
    public static Dictionary<Guid, string> BuildGroupPathMap(this ManagerConfig config)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var group in config.Groups)
        {
            WalkGroup(group, group.Name, map);
        }
        return map;

        static void WalkGroup(ServerGroup group, string currentPath, Dictionary<Guid, string> map)
        {
            map[group.Id] = currentPath;
            foreach (var child in group.Groups)
            {
                WalkGroup(child, $"{currentPath} / {child.Name}", map);
            }
        }
    }

    /// <summary>
    /// Builds both group path map and server-to-group-path map in a single tree pass.
    /// Ungrouped servers are mapped to "Без группы".
    /// </summary>
    public static (Dictionary<Guid, string> GroupPaths, Dictionary<Guid, string> ServerGroupPaths) BuildGroupAndServerPathMaps(this ManagerConfig config)
    {
        var groupPaths = new Dictionary<Guid, string>();
        var serverGroupPaths = new Dictionary<Guid, string>();
        foreach (var group in config.Groups)
        {
            WalkGroup(group, group.Name, groupPaths, serverGroupPaths);
        }
        foreach (var server in config.UngroupedServers)
        {
            serverGroupPaths[server.Id] = "Без группы";
        }
        return (groupPaths, serverGroupPaths);

        static void WalkGroup(
            ServerGroup group,
            string currentPath,
            Dictionary<Guid, string> groupPaths,
            Dictionary<Guid, string> serverGroupPaths)
        {
            groupPaths[group.Id] = currentPath;
            foreach (var server in group.Servers)
            {
                serverGroupPaths[server.Id] = currentPath;
            }
            foreach (var child in group.Groups)
            {
                WalkGroup(child, $"{currentPath} / {child.Name}", groupPaths, serverGroupPaths);
            }
        }
    }

    /// <summary>
    /// Builds a dictionary mapping server ID to its group path in a single tree pass.
    /// Ungrouped servers map to "Без группы".
    /// </summary>
    public static Dictionary<Guid, string> BuildServerGroupPathMap(this ManagerConfig config) =>
        config.BuildGroupAndServerPathMaps().ServerGroupPaths;

    /// <summary>
    /// Builds a fast server-to-group mapping in a single tree walk.
    /// Ungrouped servers map to null.
    /// </summary>
    public static Dictionary<Guid, ServerGroup?> BuildServerToGroupMap(this ManagerConfig config)
    {
        var map = new Dictionary<Guid, ServerGroup?>();
        foreach (var server in config.UngroupedServers)
        {
            map[server.Id] = null;
        }
        foreach (var group in config.Groups)
        {
            Walk(group, map);
        }
        return map;

        static void Walk(ServerGroup group, Dictionary<Guid, ServerGroup?> map)
        {
            foreach (var server in group.Servers)
            {
                map[server.Id] = group;
            }
            foreach (var child in group.Groups)
            {
                Walk(child, map);
            }
        }
    }

    /// <summary>
    /// Builds an indexed dictionary mapping server ID to ManagedServer for fast O(1) lookups.
    /// </summary>
    public static Dictionary<Guid, ManagedServer> BuildServerLookup(this ManagerConfig config) =>
        config.AllServers().ToDictionary(s => s.Id);
}

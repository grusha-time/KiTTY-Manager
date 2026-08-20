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
}

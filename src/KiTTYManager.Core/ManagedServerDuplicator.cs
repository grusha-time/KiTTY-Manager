namespace KiTTYManager.Core;

public static class ManagedServerDuplicator
{
    public static ManagedServer Duplicate(ManagerConfig config, ManagedServer source)
    {
        var copy = Create(config, source, resetRuntime: false);
        AddToSourceGroup(config, source, copy);
        return copy;
    }

    public static ManagedServer CreateQuickDuplicate(ManagerConfig config, ManagedServer source) =>
        Create(config, source, resetRuntime: true);

    public static void RefreshQuickDuplicateName(ManagerConfig config, ManagedServer source,
        ManagedServer copy, string initiallyGeneratedName)
    {
        if (!copy.Name.Equals(initiallyGeneratedName, StringComparison.Ordinal) ||
            config.AllServers().All(server => !server.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase))) return;
        copy.Name = UniqueName(config, source.Name);
    }

    public static void AddToSourceGroup(ManagerConfig config, ManagedServer source, ManagedServer copy)
    {
        var group = config.AllGroups().FirstOrDefault(candidate => candidate.Servers.Any(server => server.Id == source.Id));
        if (group is null) config.UngroupedServers.Add(copy);
        else group.Servers.Add(copy);
    }

    private static ManagedServer Create(ManagerConfig config, ManagedServer source, bool resetRuntime)
    {
        return new ManagedServer
        {
            Name = UniqueName(config, source.Name),
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            UseKeyboardInteractive = source.UseKeyboardInteractive,
            RootLogin = source.RootLogin,
            RootPassword = source.RootPassword,
            ShellPrompt = source.ShellPrompt,
            ImportedCommand = source.ImportedCommand,
            IgnoreImportedCommand = source.IgnoreImportedCommand,
            HostKeyFingerprint = resetRuntime ? "" : source.HostKeyFingerprint,
            HostKeyAlgorithm = resetRuntime ? "" : source.HostKeyAlgorithm,
            HostKeyBits = resetRuntime ? 0 : source.HostKeyBits,
            // A duplicate is manager-owned: loading the original KiTTY session would ignore its edited host/port.
            SourceSessionPath = null,
            SourceScriptPath = source.SourceScriptPath,
            SourceScriptContent = source.SourceScriptContent,
            ImportedProxy = source.ImportedProxy is null ? null : new ImportedProxy
            {
                Host = source.ImportedProxy.Host,
                Port = source.ImportedProxy.Port,
                Method = source.ImportedProxy.Method
            },
            IgnoredKittyProperties = source.IgnoredKittyProperties.ToList(),
            IgnoredKittyChanges = source.IgnoredKittyChanges.Select(change => new IgnoredKittyChange
            {
                PropertyName = change.PropertyName,
                Fingerprint = change.Fingerprint
            }).ToList(),
            RequiredPreviousServerId = source.RequiredPreviousServerId,
            TryDirectWithoutJumphost = source.TryDirectWithoutJumphost,
            WebInterfaces = resetRuntime ? [] : source.WebInterfaces.Select(web => new WebInterface
            {
                Name = web.Name,
                Url = web.Url,
                Username = web.Username,
                Password = web.Password,
                ResolverAddress = web.ResolverAddress
            }).ToList(),
            BackupEndpoints = resetRuntime ? [] : source.BackupEndpoints.Select(endpoint => new ServerEndpoint(endpoint.Host, endpoint.Port, endpoint.InternalOnly)).ToList(),
            PreferredProxyId = resetRuntime ? null : source.PreferredProxyId,
            // Ordinary duplication historically did not copy a cached route.
            PreferredRoute = null,
            PreferredEndpoint = resetRuntime || source.PreferredEndpoint is null ? null : new ServerEndpoint(source.PreferredEndpoint.Host, source.PreferredEndpoint.Port),
            EndpointPreferences = resetRuntime ? [] : source.EndpointPreferences.Select(item => new EndpointPreference
            {
                ProxyId = item.ProxyId,
                PreviousServerId = item.PreviousServerId,
                Endpoint = new ServerEndpoint(item.Endpoint.Host, item.Endpoint.Port),
                LastSuccessUtc = item.LastSuccessUtc
            }).ToList()
        };
    }

    private static string UniqueName(ManagerConfig config, string sourceName)
    {
        var names = config.AllServers().Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = $"{sourceName} — копия";
        if (!names.Contains(name)) return name;
        for (var number = 2; ; number++)
        {
            name = $"{sourceName} — копия {number}";
            if (!names.Contains(name)) return name;
        }
    }
}

public static class QuickDuplicatePolicy
{
    public static IReadOnlyList<Guid> DefaultLinkSourceIds(ManagerConfig config, ManagedServer source)
    {
        var group = config.FindServerGroup(source.Id);
        if (group is null)
            return ConnectivityBatchPlanner.DirectlyReachable(config, [source.Id]);
        return ConnectivityBatchPlanner.DirectlyReachable(config, NestedServers(group).Select(server => server.Id))
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<ManagedServer> NestedServers(ServerGroup group) =>
        group.Servers.Concat(group.Groups.SelectMany(NestedServers));
}

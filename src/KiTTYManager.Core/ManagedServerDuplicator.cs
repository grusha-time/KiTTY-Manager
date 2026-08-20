namespace KiTTYManager.Core;

public static class ManagedServerDuplicator
{
    public static ManagedServer Duplicate(ManagerConfig config, ManagedServer source)
    {
        var copy = new ManagedServer
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
            HostKeyFingerprint = source.HostKeyFingerprint,
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
            WebInterfaces = source.WebInterfaces.Select(web => new WebInterface
            {
                Name = web.Name,
                Url = web.Url,
                Username = web.Username,
                Password = web.Password,
                ResolverAddress = web.ResolverAddress
            }).ToList(),
            BackupEndpoints = source.BackupEndpoints.Select(endpoint => new ServerEndpoint(endpoint.Host, endpoint.Port)).ToList(),
            PreferredProxyId = source.PreferredProxyId,
            PreferredEndpoint = source.PreferredEndpoint is null ? null : new ServerEndpoint(source.PreferredEndpoint.Host, source.PreferredEndpoint.Port),
            EndpointPreferences = source.EndpointPreferences.Select(item => new EndpointPreference
            {
                ProxyId = item.ProxyId,
                PreviousServerId = item.PreviousServerId,
                Endpoint = new ServerEndpoint(item.Endpoint.Host, item.Endpoint.Port),
                LastSuccessUtc = item.LastSuccessUtc
            }).ToList()
        };

        var group = config.AllGroups().FirstOrDefault(candidate => candidate.Servers.Any(server => server.Id == source.Id));
        if (group is null) config.UngroupedServers.Add(copy);
        else group.Servers.Add(copy);
        return copy;
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

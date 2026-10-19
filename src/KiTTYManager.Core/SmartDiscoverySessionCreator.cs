namespace KiTTYManager.Core;

public static class SmartDiscoverySessionCreator
{
    public static List<ManagedServer> CreateSessions(
        ManagerConfig config,
        IReadOnlyList<DiscoveredServerCandidate> candidates,
        SmartDiscoveryOptions? options = null,
        string? configPath = null)
    {
        var selected = candidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return [];

        // Validate dependencies: if a candidate has RequiredPreviousServerId,
        // ensure that gateway is either already in config or among the selected candidates being created.
        var allServerIds = config.AllServers().Select(s => s.Id).ToHashSet();
        foreach (var c in selected)
        {
            if (c.RequiredPreviousServerId.HasValue)
            {
                var reqId = c.RequiredPreviousServerId.Value;
                if (!allServerIds.Contains(reqId) && !selected.Any(s => s.Id == reqId))
                {
                    throw new InvalidOperationException(
                        $"Промежуточный шлюз для сервера {c.Host} ({c.ProposedName}) не выбран для создания и отсутствует в конфигурации.");
                }
            }
        }

        var existingNames = config.AllServers().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<ManagedServer>();

        foreach (var c in selected)
        {
            if (config.FindServer(c.Id) is not null || allServerIds.Contains(c.Id))
            {
                // Server with this ID was already added (e.g. from previous attempt or concurrent create)
                continue;
            }

            // Resolve unique name against live config and previously created in this batch
            var name = c.ProposedName;
            if (string.IsNullOrWhiteSpace(name)) name = c.Host;
            if (existingNames.Contains(name))
            {
                var counter = 2;
                while (existingNames.Contains($"{name} {counter}")) counter++;
                name = $"{name} {counter}";
            }
            existingNames.Add(name);

            // Determine target group (synchronize edited GroupName over stale GroupId)
            ServerGroup? targetGroup = null;
            string? effectiveSubgroupName = c.SubgroupName;

            if (!string.IsNullOrWhiteSpace(c.GroupName))
            {
                var trimmedGroup = c.GroupName.Trim();
                if (trimmedGroup.Contains('/'))
                {
                    var parts = trimmedGroup.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var parentName = parts[0];
                    if (parts.Length > 1 && string.IsNullOrWhiteSpace(effectiveSubgroupName))
                    {
                        effectiveSubgroupName = parts[1];
                    }
                    targetGroup = config.Groups.FirstOrDefault(g => string.Equals(g.Name, parentName, StringComparison.OrdinalIgnoreCase));
                    if (targetGroup is null)
                    {
                        targetGroup = new ServerGroup { Name = parentName };
                        config.Groups.Add(targetGroup);
                    }
                }
                else
                {
                    // If c.GroupId points to a group with the exact same name, keep it
                    if (c.GroupId.HasValue)
                    {
                        var groupById = config.FindGroup(c.GroupId.Value);
                        if (groupById != null && string.Equals(groupById.Name, trimmedGroup, StringComparison.OrdinalIgnoreCase))
                        {
                            targetGroup = groupById;
                        }
                    }

                    if (targetGroup is null)
                    {
                        targetGroup = config.Groups.FirstOrDefault(g => string.Equals(g.Name, trimmedGroup, StringComparison.OrdinalIgnoreCase));
                        if (targetGroup is null)
                        {
                            targetGroup = new ServerGroup { Name = trimmedGroup };
                            config.Groups.Add(targetGroup);
                        }
                    }
                }
            }
            else if (c.GroupName != null)
            {
                // Explicit ungroup edit: user cleared the group name
                targetGroup = null;
                effectiveSubgroupName = null;
            }
            else if (c.GroupId.HasValue)
            {
                targetGroup = config.FindGroup(c.GroupId.Value);
            }
            else if (options?.TargetGroupId.HasValue == true)
            {
                targetGroup = config.FindGroup(options.TargetGroupId.Value);
            }

            // Handle subgroup if specified
            var subgroupName = !string.IsNullOrWhiteSpace(effectiveSubgroupName) ? effectiveSubgroupName : options?.TargetSubgroupName;
            if (targetGroup is not null && !string.IsNullOrWhiteSpace(subgroupName))
            {
                var sub = targetGroup.Groups.FirstOrDefault(g => string.Equals(g.Name, subgroupName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (sub is null)
                {
                    sub = new ServerGroup { Name = subgroupName.Trim() };
                    targetGroup.Groups.Add(sub);
                }
                targetGroup = sub;
            }

            var server = new ManagedServer
            {
                Id = c.Id,
                Name = name,
                Host = c.Host,
                Port = c.Port > 0 ? c.Port : 22,
                Username = c.Username,
                Password = c.Password,
                RootLogin = c.PrivilegeEscalation.Success ? c.PrivilegeEscalation.RootCommand : "",
                RootPassword = c.PrivilegeEscalation.Success ? c.PrivilegeEscalation.RootPassword : "",
                RequiredPreviousServerId = c.RequiredPreviousServerId,
                PreferredProxyId = c.VerifiedProxyId,
                HostKeyFingerprint = c.HostKeyFingerprint,
                HostKeyAlgorithm = c.HostKeyAlgorithm,
                HostKeyBits = c.HostKeyBits,
                KeepaliveIntervalSeconds = 15,
                EnableTcpKeepalives = true,
                ReconnectOnConnectionFailure = true,
                ReconnectOnSystemWakeup = true,
                ShellPrompt = "$",
                UseKeyboardInteractive = true
            };

            if (targetGroup is not null)
                targetGroup.Servers.Add(server);
            else
                config.UngroupedServers.Add(server);

            // Record discovered links
            var now = DateTimeOffset.UtcNow;
            foreach (var fromId in c.DiscoveredLinksFrom)
            {
                if (!config.Links.Any(l => l.FromServerId == fromId && l.ToServerId == c.Id))
                {
                    var linkResult = new ConnectivityResult(
                        TargetId: c.Id,
                        Success: true,
                        Message: "Smart Discovery",
                        Duration: TimeSpan.Zero,
                        Strategy: "DirectTcpIp",
                        ProxyId: c.VerifiedProxyId,
                        SourceId: fromId);
                    ServerLinkPairPolicy.RememberDirectedSuccess(config, linkResult, now);
                }
            }

            created.Add(server);
            allServerIds.Add(server.Id);
        }

        if (!string.IsNullOrEmpty(configPath))
        {
            ConfigStore.Save(configPath, config);
        }

        return created;
    }
}

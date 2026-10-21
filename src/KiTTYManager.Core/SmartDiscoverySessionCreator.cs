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

        var candidateById = candidates.ToDictionary(c => c.Id);

        Guid ResolveDependencyId(Guid dependencyId)
        {
            if (!candidateById.TryGetValue(dependencyId, out var dependency))
                return dependencyId;
            if (dependency.IsSelected)
                return dependency.Id;
            if (dependency.ExistingServerId is Guid savedId && config.FindServer(savedId) is not null)
                return savedId;
            return dependency.Id;
        }

        var reconciled = new HashSet<Guid>();
        void ReconcileVerifiedDependency(Guid dependencyId)
        {
            if (!candidateById.TryGetValue(dependencyId, out var dependency) || dependency.IsSelected)
                return;
            if (dependency.ExistingServerId is not Guid savedId || config.FindServer(savedId) is not { } saved)
                return;
            if (!reconciled.Add(dependency.Id))
                return;

            saved.UseKeyboardInteractive = true;
            saved.Username = dependency.Username;
            saved.Password = dependency.Password;
            saved.HostKeyFingerprint = dependency.HostKeyFingerprint;
            saved.HostKeyAlgorithm = dependency.HostKeyAlgorithm;
            saved.HostKeyBits = dependency.HostKeyBits;
            if (dependency.VerifiedProxyId.HasValue)
            {
                saved.PreferredProxyId = dependency.VerifiedProxyId;
            }
            else if (!dependency.IsDuplicate)
            {
                saved.PreferredProxyId = null;
            }

            if (dependency.RequiredPreviousServerId.HasValue)
            {
                saved.RequiredPreviousServerId = ResolveDependencyId(dependency.RequiredPreviousServerId.Value);
            }
            else if (!dependency.IsDuplicate)
            {
                saved.RequiredPreviousServerId = null;
            }

            saved.TryDirectWithoutJumphost = saved.PreferredProxyId is null && saved.RequiredPreviousServerId is null;
            if (dependency.PrivilegeEscalation.Success)
            {
                saved.RootLogin = dependency.PrivilegeEscalation.RootCommand;
                saved.RootPassword = dependency.PrivilegeEscalation.RootPassword;
            }

            foreach (var fromId in dependency.DiscoveredLinksFrom)
            {
                if (fromId == dependency.Id) continue;
                var resolvedFromId = ResolveDependencyId(fromId);
                if (resolvedFromId == saved.Id) continue;
                if (config.Links.Any(l => l.FromServerId == resolvedFromId && l.ToServerId == saved.Id))
                    continue;

                var linkResult = new ConnectivityResult(
                    TargetId: saved.Id,
                    Success: true,
                    Message: "Smart Discovery",
                    Duration: TimeSpan.Zero,
                    Strategy: "DirectTcpIp",
                    ProxyId: dependency.VerifiedProxyId,
                    SourceId: resolvedFromId);
                ServerLinkPairPolicy.RememberDirectedSuccess(config, linkResult, DateTimeOffset.UtcNow);
            }

            if (dependency.RequiredPreviousServerId.HasValue)
                ReconcileVerifiedDependency(dependency.RequiredPreviousServerId.Value);
        }

        var allServerIds = config.AllServers().Select(s => s.Id).ToHashSet();
        var validatedDependencies = new HashSet<Guid>();
        void ValidateDependencyChain(Guid dependencyId)
        {
            if (!candidateById.TryGetValue(dependencyId, out var dependency))
            {
                if (!allServerIds.Contains(dependencyId))
                {
                    throw new InvalidOperationException(
                        $"Промежуточный шлюз {dependencyId} не выбран для создания и отсутствует в конфигурации.");
                }
                return;
            }

            if (dependency.IsSelected || !validatedDependencies.Add(dependency.Id))
                return;

            if (dependency.ExistingServerId is not Guid savedId || config.FindServer(savedId) is null)
            {
                throw new InvalidOperationException(
                    $"Промежуточный шлюз для сервера {dependency.Host} ({dependency.ProposedName}) не выбран для создания и отсутствует в конфигурации.");
            }

            if (dependency.RequiredPreviousServerId.HasValue)
                ValidateDependencyChain(dependency.RequiredPreviousServerId.Value);
            foreach (var fromId in dependency.DiscoveredLinksFrom)
            {
                if (fromId != dependency.Id)
                    ValidateDependencyChain(fromId);
            }
        }

        foreach (var c in selected)
        {
            if (c.RequiredPreviousServerId.HasValue)
                ValidateDependencyChain(c.RequiredPreviousServerId.Value);
            foreach (var fromId in c.DiscoveredLinksFrom)
            {
                if (fromId != c.Id)
                    ValidateDependencyChain(fromId);
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
            List<ServerGroup>? idPathRemainder = null;

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
                    if (c.GroupId.HasValue)
                    {
                        var ancestry = config.GroupAncestry(c.GroupId.Value);
                        if (ancestry.Count > 0 && string.Equals(ancestry[0].Name, trimmedGroup, StringComparison.OrdinalIgnoreCase))
                        {
                            targetGroup = ancestry[0];
                            idPathRemainder = ancestry.Skip(1).ToList();
                        }
                    }

                    if (targetGroup is null)
                    {
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
                var idPathValid = idPathRemainder is not null;
                var sectionIndex = 0;
                foreach (var part in subgroupName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    ServerGroup? sub;
                    if (idPathValid && sectionIndex < idPathRemainder!.Count &&
                        string.Equals(idPathRemainder[sectionIndex].Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        sub = idPathRemainder[sectionIndex];
                    }
                    else
                    {
                        idPathValid = false;
                        sub = targetGroup.Groups.FirstOrDefault(g => string.Equals(g.Name, part, StringComparison.OrdinalIgnoreCase));
                        if (sub is null)
                        {
                            sub = new ServerGroup { Name = part };
                            targetGroup.Groups.Add(sub);
                        }
                    }
                    targetGroup = sub;
                    sectionIndex++;
                }
            }

            if (c.RequiredPreviousServerId.HasValue)
                ReconcileVerifiedDependency(c.RequiredPreviousServerId.Value);
            foreach (var fromId in c.DiscoveredLinksFrom)
            {
                if (fromId != c.Id)
                    ReconcileVerifiedDependency(fromId);
            }

            var server = CreateDraftServer(c);
            if (c.RequiredPreviousServerId.HasValue)
                server.RequiredPreviousServerId = ResolveDependencyId(c.RequiredPreviousServerId.Value);
            server.Name = name;

            if (targetGroup is not null)
                targetGroup.Servers.Add(server);
            else
                config.UngroupedServers.Add(server);

            // Record discovered links
            var now = DateTimeOffset.UtcNow;
            foreach (var fromId in c.DiscoveredLinksFrom)
            {
                if (fromId == c.Id) continue;
                var resolvedFromId = ResolveDependencyId(fromId);
                if (config.Links.Any(l => l.FromServerId == resolvedFromId && l.ToServerId == c.Id))
                    continue;

                var linkResult = new ConnectivityResult(
                    TargetId: c.Id,
                    Success: true,
                    Message: "Smart Discovery",
                    Duration: TimeSpan.Zero,
                    Strategy: "DirectTcpIp",
                    ProxyId: c.VerifiedProxyId,
                    SourceId: resolvedFromId);
                ServerLinkPairPolicy.RememberDirectedSuccess(config, linkResult, now);
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

    public static IReadOnlyList<ConnectivityPairChoice> PlanLinksToExistingGroupServers(
        ManagerConfig config,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> existingIndependentByGroup,
        IEnumerable<Guid> createdServerIds)
    {
        var createdIds = createdServerIds.ToHashSet();
        var result = new List<ConnectivityPairChoice>();
        var seen = new HashSet<(Guid SourceId, Guid TargetId)>();

        foreach (var createdId in createdIds)
        {
            var created = config.FindServer(createdId);
            if (created is null) continue;

            var group = config.FindServerGroup(createdId);
            var scopeIds = new List<Guid>();
            if (group is not null)
            {
                var ancestry = config.GroupAncestry(group.Id);
                if (ancestry.Count > 0)
                    scopeIds.AddRange(ancestry.Select(g => g.Id));
                else
                    scopeIds.Add(group.Id);
            }
            else
            {
                scopeIds.Add(Guid.Empty);
            }

            foreach (var scopeId in scopeIds)
            {
                if (!existingIndependentByGroup.TryGetValue(scopeId, out var sources)) continue;

                foreach (var sourceId in sources.Distinct())
                {
                    if (createdIds.Contains(sourceId)) continue;
                    var source = config.FindServer(sourceId);
                    if (source is null || sourceId == createdId) continue;
                    if (!seen.Add((sourceId, createdId))) continue;
                    result.Add(new ConnectivityPairChoice(source.Id, created.Id, source.Name, created.Name));
                }
            }
        }

        return result;
    }

    public static (string? RootGroupName, string? SubgroupPath) ResolveGroupAssignment(
        ManagerConfig config, Guid? targetGroupId, string? typedSubgroupName)
    {
        var typed = string.IsNullOrWhiteSpace(typedSubgroupName) ? null : typedSubgroupName.Trim();
        if (!targetGroupId.HasValue)
            return (null, typed);

        var ancestry = config.GroupAncestry(targetGroupId.Value);
        if (ancestry.Count == 0)
            return (null, typed);

        var sections = ancestry.Skip(1).Select(group => group.Name).ToList();
        if (typed is not null) sections.Add(typed);
        return (ancestry[0].Name, sections.Count > 0 ? string.Join(" / ", sections) : null);
    }

    public static ManagedServer CreateDraftServer(DiscoveredServerCandidate c) =>
        new()
        {
            Id = c.Id,
            Name = string.IsNullOrWhiteSpace(c.ProposedName) ? c.Host : c.ProposedName,
            Host = c.Host,
            Port = c.Port > 0 ? c.Port : 22,
            Username = c.Username,
            Password = c.Password,
            RootLogin = c.PrivilegeEscalation.Success ? c.PrivilegeEscalation.RootCommand : "",
            RootPassword = c.PrivilegeEscalation.Success ? c.PrivilegeEscalation.RootPassword : "",
            RequiredPreviousServerId = c.RequiredPreviousServerId,
            PreferredProxyId = c.VerifiedProxyId,
            TryDirectWithoutJumphost = c.VerifiedProxyId is null && c.RequiredPreviousServerId is null,
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

    public static IReadOnlyList<RouteCandidate> CreatePreviewCandidates(
        ManagerConfig previewConfig, DiscoveredServerCandidate target)
    {
        if (target.VerifiedProxyId is not null || target.RequiredPreviousServerId is null)
            return [];

        var chain = new List<ManagedServer>();
        Guid? currentId = target.Id;
        while (currentId.HasValue && chain.Count < 16)
        {
            var server = previewConfig.FindServer(currentId.Value);
            if (server is null) break;
            chain.Insert(0, server);
            currentId = server.RequiredPreviousServerId;
        }

        if (chain.Count <= 1) return [];
        var candidate = new RouteCandidate(RoutePlanner.DirectConnectionProxy, chain, true);
        return RoutePlanner.SatisfiesRouteConstraints(previewConfig, candidate) ? [candidate] : [];
    }

    public static ManagerConfig CreatePreviewConfig(
        ManagerConfig source, IReadOnlyList<DiscoveredServerCandidate> candidates)
    {
        var snapshot = ConfigTransfer.CloneSnapshot(source);
        var knownIds = snapshot.AllServers().Select(s => s.Id).ToHashSet();
        foreach (var c in candidates)
        {
            var targetId = c.ExistingServerId ?? c.Id;
            if (snapshot.FindServer(targetId) is { } existing)
            {
                if (!c.IsDuplicate)
                {
                    existing.RequiredPreviousServerId = c.RequiredPreviousServerId;
                    existing.PreferredProxyId = c.VerifiedProxyId;
                    existing.TryDirectWithoutJumphost = c.VerifiedProxyId is null && c.RequiredPreviousServerId is null;
                }
                if (!string.IsNullOrWhiteSpace(c.Password)) existing.Password = c.Password;
            }
            else if (knownIds.Add(c.Id))
            {
                snapshot.UngroupedServers.Add(CreateDraftServer(c));
            }
        }

        foreach (var c in candidates)
        {
            foreach (var fromId in c.DiscoveredLinksFrom)
            {
                if (fromId == c.Id || !knownIds.Contains(fromId)) continue;
                if (snapshot.Links.Any(l => l.FromServerId == fromId && l.ToServerId == c.Id)) continue;

                snapshot.Links.Add(new ServerLink
                {
                    FromServerId = fromId,
                    ToServerId = c.Id,
                    Discovered = true,
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                    LastStrategy = "DirectTcpIp",
                    LastSuccessfulProxyId = c.VerifiedProxyId
                });
            }
        }

        return snapshot;
    }
}

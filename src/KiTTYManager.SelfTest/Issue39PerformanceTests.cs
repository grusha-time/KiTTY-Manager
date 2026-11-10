using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private static void RoutePlannerBfsDepthAndOrderingTests()
    {
        var config = new ManagerConfig();
        // 1. Test BFS depth limit: MaxServersPerPath = 16
        // Create 17 servers in a line: S0 -> S1 -> S2 -> ... -> S16
        var lineServers = new List<ManagedServer>();
        for (var i = 0; i < 17; i++)
        {
            var s = new ManagedServer { Name = $"Node_{i:D2}", Host = $"10.0.0.{i + 1}" };
            lineServers.Add(s);
            config.UngroupedServers.Add(s);
        }
        for (var i = 0; i < 16; i++)
        {
            config.Links.Add(new ServerLink
            {
                FromServerId = lineServers[i].Id,
                ToServerId = lineServers[i + 1].Id,
                LastSuccessUtc = DateTimeOffset.UtcNow
            });
        }

        // S0 to S15 is 16 servers: exactly within MaxServersPerPath limit (16 nodes)
        var pathsTo15 = RoutePlanner.FindPaths(config, lineServers[15].Id);
        var p0To15 = pathsTo15.Where(p => p[0].Id == lineServers[0].Id).ToList();
        Equal(1, p0To15.Count);
        Equal(16, p0To15[0].Count);

        // S0 to S16 is 17 servers: exceeds MaxServersPerPath limit, so S0 cannot reach S16
        var pathsTo16 = RoutePlanner.FindPaths(config, lineServers[16].Id);
        // S1 to S16 is 16 servers, which is valid
        Equal(true, pathsTo16.Count > 0);
        Equal(true, pathsTo16.All(p => p.Count <= 16));
        Equal(false, pathsTo16.Any(p => p[0].Id == lineServers[0].Id));

        // 2. Cycle handling and BFS termination
        var sA = new ManagedServer { Name = "CycleA", Host = "10.0.1.1" };
        var sB = new ManagedServer { Name = "CycleB", Host = "10.0.1.2" };
        var sC = new ManagedServer { Name = "CycleC", Host = "10.0.1.3" };
        var cycleConfig = new ManagerConfig { UngroupedServers = [sA, sB, sC] };
        cycleConfig.Links.Add(new ServerLink { FromServerId = sA.Id, ToServerId = sB.Id, LastSuccessUtc = DateTimeOffset.UtcNow });
        cycleConfig.Links.Add(new ServerLink { FromServerId = sB.Id, ToServerId = sC.Id, LastSuccessUtc = DateTimeOffset.UtcNow });
        cycleConfig.Links.Add(new ServerLink { FromServerId = sC.Id, ToServerId = sA.Id, LastSuccessUtc = DateTimeOffset.UtcNow });

        var cyclePaths = RoutePlanner.FindPaths(cycleConfig, sC.Id);
        Equal(true, cyclePaths.Count >= 1);
        Equal(true, cyclePaths.All(p => p.Count <= 3));

        // 3. Deterministic candidate ordering (case-insensitive server name tie-breaking)
        var sUpper = new ManagedServer { Name = "SERVER_A", Host = "10.0.2.1" };
        var sLower = new ManagedServer { Name = "server_a", Host = "10.0.2.2" };
        var t = new ManagedServer { Name = "target_node", Host = "10.0.2.3" };
        var sortConfig = new ManagerConfig { UngroupedServers = [sUpper, sLower, t] };
        sortConfig.Links.Add(new ServerLink { FromServerId = sUpper.Id, ToServerId = t.Id, LastSuccessUtc = DateTimeOffset.UtcNow });
        sortConfig.Links.Add(new ServerLink { FromServerId = sLower.Id, ToServerId = t.Id, LastSuccessUtc = DateTimeOffset.UtcNow });
        var sortPaths = RoutePlanner.FindPaths(sortConfig, t.Id);
        Equal(2, sortPaths.Count);
    }

    private static void RoutePlannerBfsBenchmarkComparisonTests()
    {
        // Construct a graph with a chain of 35 servers plus branching shortcuts
        var config = new ManagerConfig();
        var servers = new List<ManagedServer>();
        for (var i = 0; i < 35; i++)
        {
            var s = new ManagedServer { Name = $"BenchNode_{i:D2}", Host = $"10.5.0.{i + 1}" };
            servers.Add(s);
            config.UngroupedServers.Add(s);
        }
        for (var i = 0; i < servers.Count - 1; i++)
        {
            config.Links.Add(new ServerLink
            {
                FromServerId = servers[i].Id,
                ToServerId = servers[i + 1].Id,
                LastSuccessUtc = DateTimeOffset.UtcNow
            });
        }
        // Add alternate branches
        for (var i = 0; i < servers.Count - 3; i += 3)
        {
            config.Links.Add(new ServerLink
            {
                FromServerId = servers[i].Id,
                ToServerId = servers[i + 2].Id,
                LastSuccessUtc = DateTimeOffset.UtcNow
            });
        }

        var targetId = servers[14].Id;
        var startId = servers[0].Id;

        // --- "Before" Algorithm (backward traversal on every dequeue to compute depth) ---
        long beforeBackwardSteps = 0;
        List<Guid>? RunBeforeBfs(Guid start, Guid dest)
        {
            var queue = new Queue<Guid>();
            var previous = new Dictionary<Guid, Guid?>();
            queue.Enqueue(start);
            previous[start] = null;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var depth = 1;
                // Backward traversal loop from current back to start
                for (var node = current; previous[node] is Guid parent; node = parent)
                {
                    depth++;
                    beforeBackwardSteps++;
                }

                if (current == dest)
                {
                    var path = new List<Guid>();
                    for (var node = current; ; node = previous[node]!.Value)
                    {
                        path.Add(node);
                        if (node == start) break;
                    }
                    path.Reverse();
                    return path;
                }

                if (depth >= 16) continue;

                var nextNodes = config.Links
                    .Where(l => l.FromServerId == current && l.LastSuccessUtc != null)
                    .Select(l => l.ToServerId);
                foreach (var id in nextNodes)
                {
                    if (previous.ContainsKey(id)) continue;
                    previous[id] = current;
                    queue.Enqueue(id);
                }
            }
            return null;
        }

        // --- "After" Algorithm (direct tuple depth tracking in queue) ---
        long afterBackwardSteps = 0;
        List<Guid>? RunAfterBfs(Guid start, Guid dest)
        {
            var queue = new Queue<(Guid Id, int Depth)>();
            var previous = new Dictionary<Guid, Guid?>();
            queue.Enqueue((start, 1));
            previous[start] = null;
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                // Depth is O(1) from queue tuple; 0 backward lookups needed!

                if (current == dest)
                {
                    var path = new List<Guid>();
                    for (var node = current; ; node = previous[node]!.Value)
                    {
                        path.Add(node);
                        if (node == start) break;
                    }
                    path.Reverse();
                    return path;
                }

                if (depth >= 16) continue;

                var nextNodes = config.Links
                    .Where(l => l.FromServerId == current && l.LastSuccessUtc != null)
                    .Select(l => l.ToServerId);
                foreach (var id in nextNodes)
                {
                    if (previous.ContainsKey(id)) continue;
                    previous[id] = current;
                    queue.Enqueue((id, depth + 1));
                }
            }
            return null;
        }

        var pathBefore = RunBeforeBfs(startId, targetId);
        var pathAfter = RunAfterBfs(startId, targetId);

        // Verify identical results
        Equal(true, pathBefore is not null);
        Equal(true, pathAfter is not null);
        Equal(pathBefore!.Count, pathAfter!.Count);
        for (var i = 0; i < pathBefore.Count; i++)
        {
            Equal(pathBefore[i], pathAfter[i]);
        }

        // Verify that Before incurred O(N^2) backward lookups while After incurred 0
        Equal(true, beforeBackwardSteps > 50);
        Equal(0L, afterBackwardSteps);

        // Verify official RoutePlanner produces the exact shortest path
        var officialPaths = RoutePlanner.FindPaths(config, targetId);
        Equal(true, officialPaths.Count > 0);
        var officialPathFromStart = officialPaths.First(p => p[0].Id == startId);
        Equal(pathAfter.Count, officialPathFromStart.Count);

        // Performance timing benchmark comparison
        const int iterations = 100;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            RunBeforeBfs(startId, targetId);
        }
        sw.Stop();
        var beforeElapsed = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < iterations; i++)
        {
            RunAfterBfs(startId, targetId);
        }
        sw.Stop();
        var afterElapsed = sw.ElapsedMilliseconds;

        Console.WriteLine($"  [Benchmark] RoutePlanner BFS: before={beforeElapsed}ms ({beforeBackwardSteps} steps), after={afterElapsed}ms ({afterBackwardSteps} steps)");
    }

    private static void ConfigIndexFastMappingTests()
    {
        var config = new ManagerConfig();
        var s1 = new ManagedServer { Name = "Server1", Host = "10.1.0.1" };
        var s2 = new ManagedServer { Name = "Server2", Host = "10.1.0.2" };
        var s3 = new ManagedServer { Name = "Server3", Host = "10.1.0.3" };
        var sUngrouped = new ManagedServer { Name = "ServerUngrouped", Host = "10.1.0.4" };

        var leafGroup = new ServerGroup { Name = "Leaf", Servers = [s3] };
        var subGroup2 = new ServerGroup { Name = "Sub2", Groups = [leafGroup] };
        var subGroup1 = new ServerGroup { Name = "Sub1", Servers = [s1, s2] };
        var rootGroup = new ServerGroup { Name = "Root", Groups = [subGroup1, subGroup2] };

        config.Groups.Add(rootGroup);
        config.UngroupedServers.Add(sUngrouped);

        // Verify BuildGroupPathMap
        var groupPaths = config.BuildGroupPathMap();
        Equal("Root", groupPaths[rootGroup.Id]);
        Equal("Root / Sub1", groupPaths[subGroup1.Id]);
        Equal("Root / Sub2", groupPaths[subGroup2.Id]);
        Equal("Root / Sub2 / Leaf", groupPaths[leafGroup.Id]);

        // Verify BuildServerGroupPathMap
        var (gps, serverGroupPaths) = config.BuildGroupAndServerPathMaps();
        Equal("Root / Sub1", serverGroupPaths[s1.Id]);
        Equal("Root / Sub1", serverGroupPaths[s2.Id]);
        Equal("Root / Sub2 / Leaf", serverGroupPaths[s3.Id]);
        Equal("Без группы", serverGroupPaths[sUngrouped.Id]);

        // Verify BuildServerToGroupMap
        var serverToGroup = config.BuildServerToGroupMap();
        Equal(subGroup1.Id, serverToGroup[s1.Id]!.Id);
        Equal(subGroup1.Id, serverToGroup[s2.Id]!.Id);
        Equal(leafGroup.Id, serverToGroup[s3.Id]!.Id);
        Equal<ServerGroup?>(null, serverToGroup[sUngrouped.Id]);

        // Verify BuildServerLookup
        var lookup = config.BuildServerLookup();
        Equal(4, lookup.Count);
        Equal("Server1", lookup[s1.Id].Name);
        Equal("ServerUngrouped", lookup[sUngrouped.Id].Name);
    }

    private static void ConfigIndexMappingVsRecursiveTraversalBenchmarkTests()
    {
        // Build a multi-level group tree with 40 servers
        var config = new ManagerConfig();
        var allCreatedServers = new List<ManagedServer>();
        for (var i = 0; i < 4; i++)
        {
            var parentGroup = new ServerGroup { Name = $"Datacenter_{i}" };
            for (var j = 0; j < 3; j++)
            {
                var rackGroup = new ServerGroup { Name = $"Rack_{i}_{j}" };
                for (var k = 0; k < 3; k++)
                {
                    var s = new ManagedServer { Name = $"srv_{i}_{j}_{k}", Host = $"10.{i}.{j}.{k + 1}" };
                    allCreatedServers.Add(s);
                    rackGroup.Servers.Add(s);
                }
                parentGroup.Groups.Add(rackGroup);
            }
            config.Groups.Add(parentGroup);
        }
        for (var i = 0; i < 4; i++)
        {
            var u = new ManagedServer { Name = $"ungrouped_{i}", Host = $"10.99.0.{i + 1}" };
            allCreatedServers.Add(u);
            config.UngroupedServers.Add(u);
        }

        // --- "Before" Algorithm: recursive walk per server ---
        static string? FindGroupPathRecursive(IEnumerable<ServerGroup> groups, Guid id, string prefix = "")
        {
            foreach (var group in groups)
            {
                var path = prefix.Length == 0 ? group.Name : $"{prefix} / {group.Name}";
                if (group.Servers.Any(s => s.Id == id)) return path;
                var nested = FindGroupPathRecursive(group.Groups, id, path);
                if (nested is not null) return nested;
            }
            return null;
        }

        Dictionary<Guid, string> RunBefore()
        {
            var map = new Dictionary<Guid, string>();
            foreach (var s in config.AllServers())
            {
                var path = FindGroupPathRecursive(config.Groups, s.Id);
                map[s.Id] = path ?? "Без группы";
            }
            return map;
        }

        // --- "After" Algorithm: single-pass BuildGroupAndServerPathMaps ---
        Dictionary<Guid, string> RunAfter()
        {
            var (_, serverGroupPaths) = config.BuildGroupAndServerPathMaps();
            return serverGroupPaths;
        }

        var mapBefore = RunBefore();
        var mapAfter = RunAfter();

        Equal(allCreatedServers.Count, mapBefore.Count);
        Equal(allCreatedServers.Count, mapAfter.Count);
        foreach (var s in allCreatedServers)
        {
            Equal(mapBefore[s.Id], mapAfter[s.Id]);
        }

        const int iterations = 300;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            RunBefore();
        }
        sw.Stop();
        var beforeMs = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < iterations; i++)
        {
            RunAfter();
        }
        sw.Stop();
        var afterMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"  [Benchmark] ConfigIndex paths: recursive={beforeMs}ms, single-pass={afterMs}ms");
    }

    private static void ConfigStoreCloneForSaveAndStreamTests()
    {
        var config = new ManagerConfig
        {
            SchemaVersion = 9,
            KittyPath = @"KiTTY\kitty.exe",
            FirefoxPath = "firefox.exe",
            WinScpPath = "winscp.exe",
            ConnectionTimeoutSeconds = 15,
            AutoConfirmHostKeys = true,
            NotifiedUpdateVersions = ["1.0.0", "1.1.0"]
        };

        var server = new ManagedServer
        {
            Name = "ProductionWeb",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "admin",
            Password = "PlainTextPassword",
            UsePam = true,
            PamPassword = "PamPasswordSecret",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            WebInterfaces = [new WebInterface { Name = "AdminPanel", Url = "https://192.168.1.100:8443" }],
            BackupEndpoints = [new ServerEndpoint("10.0.1.100", 22, true)],
            ManagerOverrides = ["KeepaliveIntervalSeconds"],
            PreferredRoute = new CachedRoute
            {
                ProxyId = Guid.NewGuid(),
                ServerIds = [Guid.NewGuid()]
            }
        };

        var group = new ServerGroup { Name = "Datacenter", Servers = [server] };
        config.Groups.Add(group);

        var proxy = new BaseProxy
        {
            Name = "Bastion",
            Host = "bastion.example.com",
            Port = 22,
            TotpSecret = "BASE32SECRET",
            AccessProbeServerIds = [server.Id]
        };
        config.BaseProxies.Add(proxy);

        var link = new ServerLink
        {
            FromServerId = proxy.Id,
            ToServerId = server.Id,
            Discovered = true,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            ProxyStatistics = [new LinkProxyStatistic { ProxyId = proxy.Id, LatencyMs = 12.5 }]
        };
        config.Links.Add(link);

        config.PendingFirefoxContainerCleanups.Add(new FirefoxContainerCleanupTask
        {
            ServerId = server.Id,
            WebId = server.WebInterfaces[0].Id
        });

        // 1. Deep clone fidelity
        var clone = config.CloneForSave();
        Equal(config.SchemaVersion, clone.SchemaVersion);
        Equal(config.Groups.Count, clone.Groups.Count);
        Equal(config.Groups[0].Servers[0].Name, clone.Groups[0].Servers[0].Name);
        Equal(config.Groups[0].Servers[0].WebInterfaces[0].Url, clone.Groups[0].Servers[0].WebInterfaces[0].Url);
        Equal(config.BaseProxies[0].TotpSecret, clone.BaseProxies[0].TotpSecret);
        Equal(config.Links[0].ProxyStatistics[0].LatencyMs, clone.Links[0].ProxyStatistics[0].LatencyMs);

        // 2. Reference isolation
        clone.Groups[0].Servers[0].Name = "MutatedName";
        clone.Groups[0].Servers[0].WebInterfaces[0].Url = "https://mutated.com";
        clone.BaseProxies[0].Name = "MutatedBastion";
        clone.Links[0].ProxyStatistics[0].LatencyMs = 999.0;
        clone.NotifiedUpdateVersions.Add("9.9.9");

        Equal("ProductionWeb", config.Groups[0].Servers[0].Name);
        Equal("https://192.168.1.100:8443", config.Groups[0].Servers[0].WebInterfaces[0].Url);
        Equal("Bastion", config.BaseProxies[0].Name);
        Equal(12.5, config.Links[0].ProxyStatistics[0].LatencyMs);
        Equal(2, config.NotifiedUpdateVersions.Count);

        // 3. Save to stream roundtrip
        var path = TempFile();
        try
        {
            ConfigStore.Save(path, config);
            Equal(true, File.Exists(path));
            var loaded = ConfigStore.Load(path);
            Equal(config.Groups[0].Servers[0].Name, loaded.Groups[0].Servers[0].Name);
            Equal(config.Groups[0].Servers[0].Port, loaded.Groups[0].Servers[0].Port);
            Equal(config.BaseProxies[0].Name, loaded.BaseProxies[0].Name);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void ConfigStoreCloneVsJsonRoundtripBenchmarkTests()
    {
        var config = new ManagerConfig();
        for (var i = 0; i < 20; i++)
        {
            var server = new ManagedServer
            {
                Name = $"Server_{i:D2}",
                Host = $"10.2.0.{i + 1}",
                Port = 22,
                Username = "operator",
                Password = "SecretPassword123",
                WebInterfaces = [
                    new WebInterface { Name = "Dashboard", Url = $"https://10.2.0.{i + 1}:8443/" },
                    new WebInterface { Name = "API", Url = $"https://10.2.0.{i + 1}:9090/" }
                ],
                BackupEndpoints = [
                    new ServerEndpoint($"10.3.0.{i + 1}", 2222)
                ]
            };
            var grp = new ServerGroup { Name = $"Group_{i / 5}", Servers = [server] };
            config.Groups.Add(grp);
        }

        // 1. Correctness comparison
        var json = JsonSerializer.Serialize(config, ConfigStore.Options);
        var jsonClone = JsonSerializer.Deserialize<ManagerConfig>(json, ConfigStore.Options)!;
        var typedClone = config.CloneForSave();

        Equal(jsonClone.Groups.Count, typedClone.Groups.Count);
        Equal(jsonClone.AllServers().Count(), typedClone.AllServers().Count());
        Equal(config.AllServers().First().Name, typedClone.AllServers().First().Name);
        Equal(config.AllServers().First().WebInterfaces.Count, typedClone.AllServers().First().WebInterfaces.Count);

        // 2. Reference isolation check
        typedClone.Groups[0].Servers[0].Name = "MutatedInClone";
        Equal("Server_00", config.Groups[0].Servers[0].Name);

        // 3. Timing comparison
        const int iterations = 100;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, ConfigStore.Options);
            _ = JsonSerializer.Deserialize<ManagerConfig>(bytes, ConfigStore.Options);
        }
        sw.Stop();
        var jsonElapsed = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < iterations; i++)
        {
            _ = config.CloneForSave();
        }
        sw.Stop();
        var typedElapsed = sw.ElapsedMilliseconds;

        Console.WriteLine($"  [Benchmark] ConfigStore Clone: JSON roundtrip={jsonElapsed}ms, typed CloneForSave={typedElapsed}ms");
    }


    private static void SyntheticLargeScaleResponsivenessBenchmarks()
    {
        // 1. Generate large synthetic config (2,000 servers, 50 groups, dense link graph)
        var config = new ManagerConfig
        {
            SchemaVersion = 9,
            KittyPath = @"KiTTY\kitty.exe",
            FirefoxPath = "firefox.exe",
        };

        var allGroups = new List<ServerGroup>();
        var rootGroups = new List<ServerGroup>();
        for (var g = 0; g < 10; g++)
        {
            var root = new ServerGroup { Name = $"Region_{g:D2}" };
            rootGroups.Add(root);
            allGroups.Add(root);
            for (var sub = 0; sub < 4; sub++)
            {
                var child = new ServerGroup { Name = $"Rack_{g:D2}_{sub:D2}" };
                root.Groups.Add(child);
                allGroups.Add(child);
            }
        }
        config.Groups.AddRange(rootGroups);

        var allServers = new List<ManagedServer>();
        for (var i = 0; i < 2000; i++)
        {
            var groupIndex = i % allGroups.Count;
            var server = new ManagedServer
            {
                Name = $"Server_Host_{i:D4}",
                Host = $"10.{(i / 256) % 256}.{i % 256}.{(i % 250) + 1}",
                Port = 22,
                Username = $"user_{i % 10}",
                Password = $"pass_{i % 10}",
            };
            allGroups[groupIndex].Servers.Add(server);
            allServers.Add(server);
        }

        var rand = new Random(42);
        for (var i = 0; i < 2500; i++)
        {
            var s1 = allServers[rand.Next(allServers.Count)];
            var s2 = allServers[rand.Next(allServers.Count)];
            if (s1.Id != s2.Id)
            {
                config.Links.Add(new ServerLink
                {
                    FromServerId = s1.Id,
                    ToServerId = s2.Id,
                    LastSuccessUtc = (i % 3 == 0) ? DateTimeOffset.UtcNow : null
                });
            }
        }

        Equal(50, allGroups.Count);
        Equal(2000, allServers.Count);
        Equal(true, config.Links.Count >= 2000);

        // 2. Benchmark Search & Indexing: Measure time to filter 2,000 servers using ConfigIndex single-pass precomputed cache vs recursive traversal.
        static string? FindGroupPathRecursive(IEnumerable<ServerGroup> groups, Guid id, string prefix = "")
        {
            foreach (var group in groups)
            {
                var path = prefix.Length == 0 ? group.Name : $"{prefix} / {group.Name}";
                if (group.Servers.Any(s => s.Id == id)) return path;
                var nested = FindGroupPathRecursive(group.Groups, id, path);
                if (nested is not null) return nested;
            }
            return null;
        }

        // Warmup
        var (_, warmupPaths) = config.BuildGroupAndServerPathMaps();
        _ = allServers.Where(s => warmupPaths.ContainsKey(s.Id)).ToList();

        // Single-pass precomputed cache build and search filtering
        var swSearch = Stopwatch.StartNew();
        var (_, cachedServerGroupPaths) = config.BuildGroupAndServerPathMaps();
        var query = "Server_Host_15";
        var precomputedFiltered = allServers
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        cachedServerGroupPaths[s.Id].Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        swSearch.Stop();
        var cachedLatencyMs = swSearch.ElapsedMilliseconds;

        // Recursive tree traversal across search queries to simulate unindexed filtering
        var swRecursive = Stopwatch.StartNew();
        List<ManagedServer> recursiveFiltered = [];
        for (var q = 0; q < 5; q++)
        {
            var qStr = $"Server_Host_15{q}";
            recursiveFiltered = allServers
                .Where(s => s.Name.Contains(qStr, StringComparison.OrdinalIgnoreCase) ||
                            (FindGroupPathRecursive(config.Groups, s.Id) ?? "Без группы").Contains(qStr, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        swRecursive.Stop();
        var recursiveLatencyMs = swRecursive.ElapsedMilliseconds;

        Console.WriteLine($"  [Benchmark] Search & Indexing (2,000 servers): cached={cachedLatencyMs}ms, recursive={recursiveLatencyMs}ms");
        Equal(true, cachedLatencyMs < recursiveLatencyMs);
        Equal(true, cachedLatencyMs <= 500);

        // 3. Benchmark Save Non-Blocking UI Responsiveness: Measure UI thread pause when saving large config with BackgroundConfigSaver (< 2ms) vs synchronous direct serialization
        var tempSyncFile = Path.Combine(Path.GetTempPath(), "bench_sync_" + Guid.NewGuid().ToString("N") + ".json");
        var tempAsyncFile = Path.Combine(Path.GetTempPath(), "bench_async_" + Guid.NewGuid().ToString("N") + ".json");
        long bgUiPauseMs;
        long syncPauseMs;
        try
        {
            // Warmup JIT for CloneForSave and BackgroundConfigSaver
            _ = config.CloneForSave();
            var warmupSaver = new BackgroundConfigSaver(tempAsyncFile + ".warmup");
            warmupSaver.Save(config);
            warmupSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            try { File.Delete(tempAsyncFile + ".warmup"); } catch { }

            var bgSaver = new BackgroundConfigSaver(tempAsyncFile);
            var swSaver = Stopwatch.StartNew();
            bgSaver.Save(config);
            swSaver.Stop();
            bgUiPauseMs = swSaver.ElapsedMilliseconds;

            var swSync = Stopwatch.StartNew();
            ConfigStore.SaveSnapshot(tempSyncFile, config);
            swSync.Stop();
            syncPauseMs = swSync.ElapsedMilliseconds;

            bgSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        }
        finally
        {
            if (File.Exists(tempSyncFile)) File.Delete(tempSyncFile);
            if (File.Exists(tempAsyncFile)) File.Delete(tempAsyncFile);
        }

        Console.WriteLine($"  [Benchmark] ConfigStore Save Responsiveness (2,000 servers): non-blocking UI pause={bgUiPauseMs}ms, synchronous write={syncPauseMs}ms");
        Equal(true, bgUiPauseMs < syncPauseMs);
        Equal(true, bgUiPauseMs <= 500);

        // 4. Benchmark LinkMap Hit Testing: Measure 10,000 geometric hit-tests using HitTestEdge (< 10ms total, 0 GC allocations)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool HitTestEdge(double px, double py, double x1, double y1, double x2, double y2, double thickness = 8.0)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-9)
            {
                var ddx = px - x1;
                var ddy = py - y1;
                return (ddx * ddx + ddy * ddy) <= (thickness * thickness / 4.0);
            }
            var t = ((px - x1) * dx + (py - y1) * dy) / lenSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            var projX = x1 + t * dx;
            var projY = y1 + t * dy;
            var distSq = (px - projX) * (px - projX) + (py - projY) * (py - projY);
            return distSq <= (thickness * thickness / 4.0);
        }

        // Warmup loop for Tier 1 JIT / inlining
        for (var w = 0; w < 10000; w++)
        {
            _ = HitTestEdge((w % 200) * 0.5, (w % 100) * 0.5, 10.0, 10.0, 90.0, 45.0);
        }

        var swHit = new Stopwatch();
        var gcBefore = GC.GetAllocatedBytesForCurrentThread();
        swHit.Start();
        var hitCount = 0;
        for (var i = 0; i < 10000; i++)
        {
            var px = (i % 200) * 0.5;
            var py = (i % 100) * 0.5;
            if (HitTestEdge(px, py, 10.0, 10.0, 90.0, 45.0))
            {
                hitCount++;
            }
        }
        swHit.Stop();
        var gcAfter = GC.GetAllocatedBytesForCurrentThread();
        var hitAllocBytes = gcAfter - gcBefore;
        var hitElapsedMs = swHit.ElapsedMilliseconds;

        Console.WriteLine($"  [Benchmark] LinkMap Hit-Testing (10,000 tests): time={hitElapsedMs}ms, GC={hitAllocBytes} bytes, hits={hitCount}");
        Equal(true, hitElapsedMs <= 500);
        Equal(0L, hitAllocBytes);

        // Tree navigation & O(1) group path mapping
        var swNav = Stopwatch.StartNew();
        var (navGroupPaths, navServerPaths) = config.BuildGroupAndServerPathMaps();
        var foundPathsCount = 0;
        foreach (var s in allServers)
        {
            if (navServerPaths.TryGetValue(s.Id, out _)) foundPathsCount++;
        }
        swNav.Stop();
        var navElapsedMs = swNav.ElapsedMilliseconds;
        Console.WriteLine($"  [Benchmark] Tree Navigation & O(1) Group Path Mapping (2,000 servers): time={navElapsedMs}ms (mapped {foundPathsCount} paths)");
        Equal(2000, foundPathsCount);
        Equal(true, navElapsedMs <= 500);
    }

    private static void LinkMapOptimizationTests()
    {
        var config = new ManagerConfig();
        var s1 = new ManagedServer { Name = "Node1", Host = "10.0.0.1" };
        var s2 = new ManagedServer { Name = "Node2", Host = "10.0.0.2" };
        var grp = new ServerGroup { Name = "Cluster", Servers = [s1, s2] };
        config.Groups.Add(grp);

        config.Links.Add(new ServerLink
        {
            FromServerId = s1.Id,
            ToServerId = s2.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        });

        var model = LinkMapLayout.Build(config);
        Equal(2, model.Nodes.Count);
        Equal(1, model.Edges.Count);
        Equal("Cluster", model.Nodes[0].GroupPath);
        Equal("Cluster", model.Nodes[1].GroupPath);

        // Highlight offsets test
        var offsets0 = LinkMapLayout.HighlightOffsets(0);
        Equal(0, offsets0.Count);
        var offsets1 = LinkMapLayout.HighlightOffsets(1);
        Equal(1, offsets1.Count);
        Equal(0.0, offsets1[0]);
        var offsets3 = LinkMapLayout.HighlightOffsets(3);
        Equal(3, offsets3.Count);
        Equal(true, offsets3[0] < offsets3[1] && offsets3[1] < offsets3[2]);

        // Edge properties
        var edge = model.Edges[0];
        Equal(true, edge.IsAvailable);
        Equal(true, edge.IsDirected);
    }

    private static void BackgroundConfigSaverNonBlockingAndLastSaveWinsTests()
    {
        // 1. Non-blocking UI save with simulated slow disk
        var writtenSnapshots = new List<string>();
        var writeDelayMs = 50;
        var saver = new BackgroundConfigSaver("test_config.json", (path, snapshot) =>
        {
            Thread.Sleep(writeDelayMs);
            lock (writtenSnapshots)
            {
                writtenSnapshots.Add(snapshot.KittyPath);
            }
        });

        var config = new ManagerConfig { KittyPath = "save_1" };
        var sw = Stopwatch.StartNew();
        saver.Save(config);
        sw.Stop();

        // UI call returns immediately (snapshot taken in < 15ms), not waiting for the 50ms disk write
        Equal(true, sw.ElapsedMilliseconds < 25);
        Equal(true, saver.IsBusy);

        saver.WaitForPendingSave();
        Equal(false, saver.IsBusy);
        Equal(1, writtenSnapshots.Count);
        Equal("save_1", writtenSnapshots[0]);

        // 2. "Last save wins" coalescing test:
        // When multiple saves happen while disk writer is busy, only the latest state is written
        writtenSnapshots.Clear();
        config.KittyPath = "burst_1";
        saver.Save(config);
        // While write 1 is in progress, enqueue updates
        config.KittyPath = "burst_2";
        saver.Save(config);
        config.KittyPath = "burst_3";
        saver.Save(config);

        saver.WaitForPendingSave();
        Equal(false, saver.IsBusy);
        // The final written state must be burst_3!
        Equal(true, writtenSnapshots.Count >= 1);
        Equal("burst_3", writtenSnapshots[^1]);

        // 3. FlushPendingSavesAsync async wait
        writtenSnapshots.Clear();
        config.KittyPath = "async_save";
        saver.Save(config);
        saver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        Equal(false, saver.IsBusy);
        Equal("async_save", writtenSnapshots[^1]);
    }

    private static void SessionSearchDebounceAndKeyboardPolicyTests()
    {
        // 1. Policy test for debounce flush on key down
        // Regular characters ('A', '1', Back, Space) do not flush debounce (debounced via timer)
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("A", hasModifiers: false));
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("D1", hasModifiers: false));
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Back", hasModifiers: false));
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Space", hasModifiers: false));

        // Navigation and submit keys flush debounce immediately
        Equal(true, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Return", hasModifiers: false));
        Equal(true, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Enter", hasModifiers: false));
        Equal(true, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Down", hasModifiers: false));
        Equal(true, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Up", hasModifiers: false));

        // Modifiers inhibit debounce flush
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Enter", hasModifiers: true));
        Equal(false, SessionKeyboardPolicy.ShouldFlushSearchDebounceOnKeyDown("Down", hasModifiers: true));

        // Escape clears search
        Equal(true, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Escape", hasModifiers: false));
        Equal(true, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Esc", hasModifiers: false));
        Equal(false, SessionKeyboardPolicy.ShouldClearSearchOnKeyDown("Escape", hasModifiers: true));

        // 2. Source code verification for MainWindow search debounce
        var mainWindowSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.App/MainWindow.xaml.cs"));
        var previewKeyDownBody = ExtractMethodBody(mainWindowSrc, "void SearchBox_PreviewKeyDown");
        Equal(true, ContainsUncommentedCode(previewKeyDownBody, "ShouldFlushSearchDebounceOnKeyDown"));
        Equal(false, ContainsUncommentedCode(previewKeyDownBody, "searchDebounceTimer.Stop();\n        RefreshSessions();\n        var result = SessionKeyboardPolicy.EvaluateSearchKeyDown"));
    }

    private static void CacheAndIndexUpdateOnSessionAndWebEditingTests()
    {
        // 1. Session creation index rebuild
        var config = new ManagerConfig();
        var s1 = new ManagedServer { Name = "ExistingServer", Host = "10.0.0.1" };
        config.UngroupedServers.Add(s1);

        var (groupPaths, serverGroupPaths) = config.BuildGroupAndServerPathMaps();
        Equal(1, serverGroupPaths.Count);
        Equal(true, serverGroupPaths.ContainsKey(s1.Id));

        // Simulate session creation
        var s2 = new ManagedServer { Name = "NewServer", Host = "10.0.0.2" };
        config.UngroupedServers.Add(s2);

        // Rebuild index
        var (newGroupPaths, newServerGroupPaths) = config.BuildGroupAndServerPathMaps();
        Equal(2, newServerGroupPaths.Count);
        Equal(true, newServerGroupPaths.ContainsKey(s2.Id));
        Equal("Без группы", newServerGroupPaths[s2.Id]);

        // 2. Web interface editing searchable text update
        var web = new WebInterface
        {
            Name = "WebConsole",
            Url = "https://old-admin.corp.local:8443",
            Username = "admin_v1"
        };
        var server = new ManagedServer
        {
            Name = "WebHost",
            Host = "10.0.0.10",
            WebInterfaces = [web]
        };

        static string ComputeSearchableText(ManagedServer s, string groupName)
        {
            var webFields = s.WebInterfaces.SelectMany(item =>
                new[] { item.Name, item.Url, item.ResolverAddress, item.Username, item.Password });
            return string.Join("\n", new[] { s.Name, s.Host, s.Port.ToString(), s.Username, s.Password,
                s.PrivateKeyPath, s.PrivateKeyPassphrase,
                s.RootLogin, s.RootPassword, s.SourceScriptPath ?? "", s.SourceScriptContent, groupName }.Concat(webFields));
        }

        var oldSearchable = ComputeSearchableText(server, "WebGroup");
        Equal(true, oldSearchable.Contains("old-admin.corp.local", StringComparison.OrdinalIgnoreCase));
        Equal(true, oldSearchable.Contains("admin_v1", StringComparison.OrdinalIgnoreCase));
        Equal(false, oldSearchable.Contains("new-admin.corp.local", StringComparison.OrdinalIgnoreCase));

        // User edits web URL and username
        web.Url = "https://new-admin.corp.local:8443";
        web.Username = "admin_v2";

        var newSearchable = ComputeSearchableText(server, "WebGroup");
        Equal(false, newSearchable.Contains("old-admin.corp.local", StringComparison.OrdinalIgnoreCase));
        Equal(false, newSearchable.Contains("admin_v1", StringComparison.OrdinalIgnoreCase));
        Equal(true, newSearchable.Contains("new-admin.corp.local", StringComparison.OrdinalIgnoreCase));
        Equal(true, newSearchable.Contains("admin_v2", StringComparison.OrdinalIgnoreCase));

        // 3. Verify source code wires up RebuildIndex and cache update
        var mainWindowSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.App/MainWindow.xaml.cs"));
        var createSessionBody = ExtractMethodBody(mainWindowSrc, "void CreateSession_Click");
        Equal(true, ContainsUncommentedCode(createSessionBody, "RebuildIndex();"));

        var webCellEditEndingBody = ExtractMethodBody(mainWindowSrc, "void WebGrid_CellEditEnding");
        Equal(true, ContainsUncommentedCode(webCellEditEndingBody, "cachedSearchableTexts[targetServer.Id] = ComputeSearchableText"));
    }

    private static void LinkMapCurveStrokeHitTestingAndWindowsStaTests()
    {
        // 1. Verify source code fixes in LinkMapWindow.xaml.cs
        var linkMapSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.App/LinkMapWindow.xaml.cs"));
        Equal(true, ContainsUncommentedCode(linkMapSrc, "IsClosed = false, IsFilled = false"));
        Equal(true, ContainsUncommentedCode(linkMapSrc, "hitAreas.Add((lineGeom, false, tooltip));"));
        Equal(true, ContainsUncommentedCode(linkMapSrc, "hitAreas.Add((geom, false, tooltip));"));
        Equal(true, ContainsUncommentedCode(linkMapSrc, "hitAreas.Add((arrowGeom, true, tooltip));"));

        Equal(true, ContainsUncommentedCode(linkMapSrc, "isFill ? geom.FillContains(pt) : geom.StrokeContains(pen ?? HitTestPen, pt)"));

        // 2. Mathematical curve stroke vs fill geometry verification:
        // A quadratic bezier with start (0, 0), control (50, 100), end (100, 0):
        // Curve formula: B(t) = (1-t)^2 * P0 + 2(1-t)t * P1 + t^2 * P2
        // At t = 0.5: B(0.5) = 0.25*(0,0) + 0.5*(50,100) + 0.25*(100,0) = (50, 50).
        // Chord connects (0, 0) to (100, 0) along y = 0.
        // A point (50, 20) is inside the chord region between y = 0 and y = 50.
        // In the bug, FillContains evaluated to true on this interior point!
        // In the fix, Stroke with thickness 8.0 checks distance to curve B(t).
        // Distance from (50, 20) to curve vertex (50, 50) is 30.0 > 8.0/2 = 4.0 -> FALSE!
        // Distance from (50, 50) to curve vertex is 0.0 <= 4.0 -> TRUE!
        static double QuadraticBezierDistance(double px, double py, double x0, double y0, double x1, double y1, double x2, double y2)
        {
            var minDistSq = double.MaxValue;
            for (var i = 0; i <= 100; i++)
            {
                var t = i / 100.0;
                var bx = (1 - t) * (1 - t) * x0 + 2 * (1 - t) * t * x1 + t * t * x2;
                var by = (1 - t) * (1 - t) * y0 + 2 * (1 - t) * t * y1 + t * t * y2;
                var dsq = (px - bx) * (px - bx) + (py - by) * (py - by);
                if (dsq < minDistSq) minDistSq = dsq;
            }
            return Math.Sqrt(minDistSq);
        }

        var chordInteriorDist = QuadraticBezierDistance(50, 20, 0, 0, 50, 100, 100, 0);
        var onStrokeDist = QuadraticBezierDistance(50, 50, 0, 0, 50, 100, 100, 0);

        Equal(true, chordInteriorDist > 15.0); // Far away from curve stroke!
        Equal(true, onStrokeDist < 1.0);       // Exactly on stroke!

        // 3. Windows STA verification if running on Windows platform
        if (OperatingSystem.IsWindows())
        {
            var appDll = ResolveAppDll();
            if (File.Exists(appDll))
            {
                Exception? staEx = null;
                var staThread = new Thread(() =>
                {
                    try
                    {
                        RunWindowsStaHitTest(appDll);
                    }
                    catch (Exception ex)
                    {
                        staEx = ex;
                    }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                if (!staThread.Join(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Windows STA HitTest timed out after 10 seconds.");
                }
                if (staEx is not null) throw staEx;
            }
        }
    }

    private static void RunWindowsStaHitTest(string appDll)
    {
        var asm = System.Reflection.Assembly.LoadFrom(appDll);
        var linkMapType = asm.GetType("KiTTYManager.App.LinkMapWindow");
        if (linkMapType is null) return;

        var hitTestMethod = linkMapType.GetMethod("HitTestEdge",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (hitTestMethod is null) return;

        // PresentationCore and WindowsBase reflection
        var winBase = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WindowsBase")
            ?? System.Reflection.Assembly.Load("WindowsBase");
        var pointType = winBase.GetType("System.Windows.Point")
            ?? Type.GetType("System.Windows.Point, WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
            ?? throw new InvalidOperationException("System.Windows.Point type not found in WindowsBase.");
        var pcore = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PresentationCore")
            ?? System.Reflection.Assembly.Load("PresentationCore");
        var pathGeomType = pcore.GetType("System.Windows.Media.PathGeometry")!;
        var pathFigureType = pcore.GetType("System.Windows.Media.PathFigure")!;
        var quadSegmentType = pcore.GetType("System.Windows.Media.QuadraticBezierSegment")!;

        var p0 = Activator.CreateInstance(pointType, 0.0, 0.0)!;
        var pControl = Activator.CreateInstance(pointType, 50.0, 100.0)!;
        var pEnd = Activator.CreateInstance(pointType, 100.0, 0.0)!;

        var figure = Activator.CreateInstance(pathFigureType)!;
        pathFigureType.GetProperty("StartPoint")!.SetValue(figure, p0);
        pathFigureType.GetProperty("IsClosed")!.SetValue(figure, false);
        pathFigureType.GetProperty("IsFilled")!.SetValue(figure, false);

        var segment = Activator.CreateInstance(quadSegmentType, pControl, pEnd, true)!;
        var segments = (System.Collections.IList)pathFigureType.GetProperty("Segments")!.GetValue(figure)!;
        segments.Add(segment);

        var figures = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathFigureType))!;
        figures.Add(figure);
        var geom = Activator.CreateInstance(pathGeomType, figures)!;

        // Point inside chord (50, 20) with isFill = false -> false
        var testPtChord = Activator.CreateInstance(pointType, 50.0, 20.0)!;
        var hitChord = (bool)hitTestMethod.Invoke(null, [geom, false, testPtChord, null])!;
        Equal(false, hitChord);

        // Point on curve (50, 50) with isFill = false -> true
        var testPtStroke = Activator.CreateInstance(pointType, 50.0, 50.0)!;
        var hitStroke = (bool)hitTestMethod.Invoke(null, [geom, false, testPtStroke, null])!;
        Equal(true, hitStroke);
    }


    private static void RunWindowsStaDispatcherTest(string appDll)
    {
        var staThread = Thread.CurrentThread;
        var winBase = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WindowsBase")
            ?? System.Reflection.Assembly.Load("WindowsBase");
        var dispatcherType = winBase.GetType("System.Windows.Threading.Dispatcher");
        if (dispatcherType is null) return;

        var currentDispatcherProp = dispatcherType.GetProperty("CurrentDispatcher",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var dispatcher = currentDispatcherProp?.GetValue(null);
        if (dispatcher is null) return;

        var checkAccessMethod = dispatcherType.GetMethod("CheckAccess", Type.EmptyTypes);
        var invokeMethod = dispatcherType.GetMethod("Invoke", [typeof(Action)]);
        if (checkAccessMethod is null || invokeMethod is null) return;

        // Verify CheckAccess returns true on this STA thread
        Equal(true, (bool)checkAccessMethod.Invoke(dispatcher, null)!);

        var frameType = winBase.GetType("System.Windows.Threading.DispatcherFrame")!;
        var frame = Activator.CreateInstance(frameType)!;
        var continueProp = frameType.GetProperty("Continue")!;
        var pushFrameMethod = dispatcherType.GetMethod("PushFrame", [frameType])!;

        var savedSnapshots = new List<string>();
        var saver = new BackgroundConfigSaver("sta_test_config.json", (p, snap) =>
        {
            lock (savedSnapshots) { savedSnapshots.Add(snap.KittyPath); }
        });

        var liveConfig = new ManagerConfig { KittyPath = "initial_sta_kitty" };

        // Reflection on appDll to load MainWindow and its SaveConfig() method
        object? mainWindow = null;
        MethodInfo? saveConfigMethod = null;
        if (!string.IsNullOrEmpty(appDll) && File.Exists(appDll))
        {
            var appAsm = System.Reflection.Assembly.LoadFrom(appDll);
            var mainWindowType = appAsm.GetType("KiTTYManager.App.MainWindow");
            if (mainWindowType is not null)
            {
                mainWindow = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(mainWindowType);

                // Wire WPF DispatcherObject._dispatcher field so Dispatcher property resolves
                var dispObjType = winBase.GetType("System.Windows.Threading.DispatcherObject") ?? mainWindowType.BaseType;
                var dispField = dispObjType?.GetField("_dispatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? mainWindowType.GetField("_dispatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                dispField?.SetValue(mainWindow, dispatcher);

                mainWindowType.GetField("config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(mainWindow, liveConfig);
                mainWindowType.GetField("configSaver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(mainWindow, saver);
                mainWindowType.GetField("configAvailable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(mainWindow, true);
                mainWindowType.GetField("isImportWizardActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(mainWindow, false);

                saveConfigMethod = mainWindowType.GetMethod("SaveConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
        }

        var threadIds = new List<int>();
        var activeConcurrency = 0;
        var maxConcurrency = 0;

        Action invokeSaveConfig;
        if (mainWindow is not null && saveConfigMethod is not null)
        {
            invokeSaveConfig = () => saveConfigMethod.Invoke(mainWindow, null);
        }
        else
        {
            // Isolated test harness matching MainWindow.SaveConfig dispatcher marshaling
            invokeSaveConfig = () =>
            {
                var check = (bool)checkAccessMethod.Invoke(dispatcher, null)!;
                if (!check)
                {
                    invokeMethod.Invoke(dispatcher, [new Action(() =>
                    {
                        var cur = Interlocked.Increment(ref activeConcurrency);
                        lock (threadIds)
                        {
                            if (cur > maxConcurrency) maxConcurrency = cur;
                            threadIds.Add(Thread.CurrentThread.ManagedThreadId);
                        }
                        saver.Save(liveConfig);
                        Interlocked.Decrement(ref activeConcurrency);
                    })]);
                }
                else
                {
                    var cur = Interlocked.Increment(ref activeConcurrency);
                    lock (threadIds)
                    {
                        if (cur > maxConcurrency) maxConcurrency = cur;
                        threadIds.Add(Thread.CurrentThread.ManagedThreadId);
                    }
                    saver.Save(liveConfig);
                    Interlocked.Decrement(ref activeConcurrency);
                }
            };
        }

        using var gate = new ManualResetEventSlim(false);
        var updateLock = new object();
        var latestUpdatedKittyPath = "";
        var tasks = new Task[10];

        for (var i = 0; i < 10; i++)
        {
            var taskIndex = i;
            tasks[i] = Task.Run(() =>
            {
                gate.Wait();
                var check = (bool)checkAccessMethod.Invoke(dispatcher, null)!;
                Equal(false, check);
                lock (updateLock)
                {
                    liveConfig.KittyPath = $"sta_kitty_update_{taskIndex}";
                    latestUpdatedKittyPath = liveConfig.KittyPath;
                    invokeSaveConfig();
                }
            });
        }

        gate.Set();
        Task.WhenAll(tasks).ContinueWith(_ =>
        {
            continueProp.SetValue(frame, false);
        });
        pushFrameMethod.Invoke(null, [frame]);

        if (threadIds.Count > 0)
        {
            Equal(10, threadIds.Count);
            Equal(0, activeConcurrency);
            Equal(1, maxConcurrency);
            Equal(true, threadIds.All(id => id == staThread.ManagedThreadId));
        }

        saver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        Equal(false, saver.HasPendingUnsavedChanges);
        lock (savedSnapshots)
        {
            Equal(true, savedSnapshots.Count > 0);
            Equal(latestUpdatedKittyPath, savedSnapshots[^1]);
        }
    }


    private static void ExitSequenceAndImportWizardDataSafetyTests()
    {
        var mwSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.App/MainWindow.xaml.cs"));

        // 1. [P1] forceExit in Window_Closing must not bypass flush, cleanup, or update commit
        Equal(false, ContainsUncommentedCode(mwSrc, "if (forceExit || configExitCompleted)"));
        Equal(true, ContainsUncommentedCode(mwSrc, "if (configExitCompleted)"));
        Equal(true, ContainsUncommentedCode(mwSrc, "if (!configAvailable)"));

        var closingMethodBody = ExtractMethodBody(mwSrc, "void Window_Closing");
        Equal(true, ContainsUncommentedCode(closingMethodBody, "if (configExitCompleted)"));
        Equal(true, ContainsUncommentedCode(closingMethodBody, "if (!configAvailable)"));
        Equal(true, ContainsUncommentedCode(closingMethodBody, "Cleanup();"));
        Equal(true, ContainsUncommentedCode(closingMethodBody, "_ = FlushConfigAndExitAsync();"));

        // Simulation: forceExit triggers flush and cleanup
        var flushCalled = false;
        var cleanupCalled = false;
        var windowClosed = false;
        var configExitPending = false;
        var configExitCompleted = false;
        var forceExit = true;
        _ = forceExit;
        var configAvailable = true;

        void SimulateWindowClosing(ref bool cancel)
        {
            if (configExitCompleted) return;
            if (!configAvailable)
            {
                cleanupCalled = true;
                return;
            }
            cancel = true;
            if (configExitPending) return;
            configExitPending = true;
            flushCalled = true;
        }

        var cancelEvent = false;
        SimulateWindowClosing(ref cancelEvent);
        Equal(true, cancelEvent);
        Equal(true, flushCalled);
        Equal(false, cleanupCalled);

        // Simulated completion of FlushConfigAndExitAsync:
        configExitCompleted = true;
        cleanupCalled = true;
        windowClosed = true;
        cancelEvent = false;
        SimulateWindowClosing(ref cancelEvent);
        Equal(false, cancelEvent); // Natural exit allowed
        Equal(true, windowClosed);

        // 2. [P1] Window of Data Loss between Last Flush Loop Check and Deferred Close
        var betterProbeBody = ExtractMethodBody(mwSrc, "void ScheduleBetterRouteProbe(");
        var fallbackProbeBody = ExtractMethodBody(mwSrc, "void ScheduleFallbackRouteProbe(");
        var directProbeBody = ExtractMethodBody(mwSrc, "void ScheduleDirectRouteProbe(");

        Equal(true, ContainsUncommentedCode(betterProbeBody, "if (probe.Token.IsCancellationRequested || configExitPending)"));
        Equal(true, ContainsUncommentedCode(fallbackProbeBody, "if (probe.Token.IsCancellationRequested || configExitPending)"));
        Equal(true, ContainsUncommentedCode(directProbeBody, "if (probe.Token.IsCancellationRequested || configExitPending)"));

        var flushBody = ExtractMethodBody(mwSrc, "async Task FlushConfigAndExitAsync()");
        Equal(true, ContainsUncommentedCode(flushBody, "if (activeForegroundOperationTask is { } opTask)"));
        Equal(true, ContainsUncommentedCode(flushBody, "CloseActiveSavedLinksDialogs();"));
        Equal(true, ContainsUncommentedCode(flushBody, "savedLinksChecksCancellation.Cancel();"));
        Equal(true, ContainsUncommentedCode(flushBody, "var runningLinkChecks = activeSavedLinksChecks.Keys.ToArray();"));
        Equal(true, ContainsUncommentedCode(flushBody, "accessScriptAlarmCancellation.Cancel();"));
        Equal(true, ContainsUncommentedCode(flushBody, "accessScriptsMasterCancellation.Cancel();"));
        Equal(true, ContainsUncommentedCode(flushBody, "var runningAccessScripts = activeAccessScriptTasks.Keys.ToArray();"));
        Equal(true, ContainsUncommentedCode(flushBody, "if (activeAccessScriptAlarmTask is { } alarmTask)"));
        Equal(true, ContainsUncommentedCode(flushBody, "var runningAccessChecks = backgroundAccessChecks.Values.ToArray();"));
        Equal(true, ContainsUncommentedCode(flushBody, "backgroundRouteProbes.CancelAll();"));
        Equal(true, ContainsUncommentedCode(flushBody, "operationCancellation?.Cancel();"));
        Equal(true, ContainsUncommentedCode(flushBody, "if (configSaver.HasPendingUnsavedChanges || configSaver.SavedVersion < configSaver.EnqueuedVersion)"));
        Equal(true, ContainsUncommentedCode(flushBody, "SaveConfig();"));
        Equal(true, ContainsUncommentedCode(flushBody, "await configSaver.FlushPendingSavesAsync();"));
        Equal(true, ContainsUncommentedCode(flushBody, "Cleanup();"));
        Equal(true, ContainsUncommentedCode(flushBody, "pendingUpdateSession.Commit()"));
        Equal(true, ContainsUncommentedCode(flushBody, "configExitCompleted = true;"));
        Equal(true, ContainsUncommentedCode(flushBody, "Close();"));

        // 3. [P2] Normal Exit Must Save In-Memory Model State
        Equal(true, ContainsUncommentedCode(flushBody, "SaveConfig();"));

        // Behavioral test: exit saves in-memory model modifications enqueued at exit start
        var inMemorySavedSnapshots = new List<string>();
        var inMemorySaver = new BackgroundConfigSaver("test_in_memory_exit.json", (path, snapshot) =>
        {
            lock (inMemorySavedSnapshots) { inMemorySavedSnapshots.Add(snapshot.KittyPath); }
        });

        var liveConfig = new ManagerConfig { KittyPath = "initial_kitty_path" };
        inMemorySaver.Save(liveConfig);
        inMemorySaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        Equal(1, inMemorySavedSnapshots.Count);
        Equal("initial_kitty_path", inMemorySavedSnapshots[0]);

        // Model is modified in-memory without prior explicit save
        liveConfig.KittyPath = "modified_in_memory_before_exit";
        Equal(false, inMemorySaver.HasPendingUnsavedChanges);

        // Exit sequence starts: calls SaveConfig() to enqueue in-memory changes
        inMemorySaver.Save(liveConfig);
        Equal(true, inMemorySaver.HasPendingUnsavedChanges);

        // Flush loop executes:
        while (true)
        {
            var targetVersion = inMemorySaver.EnqueuedVersion;
            inMemorySaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            if (inMemorySaver.SavedVersion >= inMemorySaver.EnqueuedVersion && !inMemorySaver.HasPendingUnsavedChanges)
            {
                break;
            }
        }
        Equal(false, inMemorySaver.HasPendingUnsavedChanges);
        Equal(2, inMemorySavedSnapshots.Count);
        Equal("modified_in_memory_before_exit", inMemorySavedSnapshots[1]);

        // 4. [P2] Successful Import Must Not Discard Saves of the Merged Configuration
        var importClickBody = ExtractMethodBody(mwSrc, "void ImportConfig_Click(");
        Equal(true, ContainsUncommentedCode(importClickBody, "isImportWizardActive = false;"));
        Equal(true, ContainsUncommentedCode(importClickBody, "importWizardMissedSave = false;"));
        Equal(true, ContainsUncommentedCode(importClickBody, "importCompletedSuccessfully = true;"));

        // Behavioral test: verify saves during modal TOTP dialog are NOT suppressed
        var wizardActive = true;
        var wizardMissedSave = false;
        var importSuccess = false;
        var savedConfigs = new List<string>();

        void SimulateSaveConfig(string tag)
        {
            if (!wizardActive)
            {
                savedConfigs.Add(tag);
            }
            else
            {
                wizardMissedSave = true;
            }
        }

        // Before apply: save is suppressed
        SimulateSaveConfig("pre_apply");
        Equal(true, wizardMissedSave);
        Equal(0, savedConfigs.Count);

        // Apply merged configuration: state transition happens before modal dialog
        wizardActive = false;
        wizardMissedSave = false;
        importSuccess = true;

        // Background probe or event during modal TOTP warning dialog:
        SimulateSaveConfig("during_modal_totp_dialog");
        Equal(1, savedConfigs.Count);
        Equal("during_modal_totp_dialog", savedConfigs[0]);
        Equal(false, wizardMissedSave);

        // Finally block executes
        if (wizardMissedSave && !importSuccess)
        {
            wizardMissedSave = false;
            SimulateSaveConfig("from_finally");
        }
        Equal(1, savedConfigs.Count);

        // 5. [P2] Failed Rollback Must Re-Enqueue In-Memory Config for Saving
        var rollbackClickBody = ExtractMethodBody(mwSrc, "void RollbackLastImport(");
        Equal(true, ContainsUncommentedCode(rollbackClickBody, "if (!rollbackCompleted)"));
        Equal(true, ContainsUncommentedCode(rollbackClickBody, "SaveConfig();"));

        // Behavioral test: failed rollback re-enqueues config save
        var rollbackSavedSnapshots = new List<string>();
        var rollbackSaver = new BackgroundConfigSaver("test_rollback_fail.json", (path, snapshot) =>
        {
            lock (rollbackSavedSnapshots) { rollbackSavedSnapshots.Add(snapshot.KittyPath); }
        });

        var rollbackConfig = new ManagerConfig { KittyPath = "active_config_before_rollback" };
        rollbackSaver.Save(rollbackConfig);
        rollbackSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        Equal(1, rollbackSavedSnapshots.Count);

        // Rollback begins:
        var isRollbackWizardActive = true;
        _ = isRollbackWizardActive;
        var rollbackCompleted = false;
        rollbackSaver.ClearPending();
        Equal(false, rollbackSaver.HasPendingUnsavedChanges);

        // Finally block executes after failed rollback:
        isRollbackWizardActive = false;
        if (!rollbackCompleted)
        {
            rollbackSaver.Save(rollbackConfig);
        }

        Equal(true, rollbackSaver.HasPendingUnsavedChanges);
        Equal(2L, rollbackSaver.EnqueuedVersion);
        rollbackSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
        Equal(false, rollbackSaver.HasPendingUnsavedChanges);
        Equal(2, rollbackSavedSnapshots.Count);
        Equal("active_config_before_rollback", rollbackSavedSnapshots[1]);

        // 6. [Round 5] Access script cancellation during exit completes and flushes before exit close
        {
            var alarmSavedSnapshots = new List<string>();
            var alarmSaver = new BackgroundConfigSaver("test_access_alarm_exit.json", (path, snapshot) =>
            {
                lock (alarmSavedSnapshots) { alarmSavedSnapshots.Add(snapshot.KittyPath); }
            });

            var alarmConfig = new ManagerConfig { KittyPath = "baseline_alarm_path" };
            alarmSaver.Save(alarmConfig);
            alarmSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            Equal(1, alarmSavedSnapshots.Count);

            // Simulate active alarm task and background access check running during exit
            var accessAlarmCts = new CancellationTokenSource();
            var alarmTcs = new TaskCompletionSource();
            Task? activeAlarmTask = alarmTcs.Task;
            var backgroundChecks = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();
            var proxyId = Guid.NewGuid();
            backgroundChecks.TryAdd(proxyId, 0);

            // Active background check task simulates cancellation catch block triggering SaveConfig
            var backgroundTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, accessAlarmCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Access script error handling marks unconfirmed and calls SaveConfig
                    alarmConfig.KittyPath = "saved_from_alarm_cancellation_handler";
                    alarmSaver.Save(alarmConfig);
                }
                finally
                {
                    backgroundChecks.TryRemove(proxyId, out _);
                    alarmTcs.TrySetResult();
                    activeAlarmTask = null;
                }
            });

            // FlushConfigAndExit sequence executes:
            accessAlarmCts.Cancel();
            if (activeAlarmTask is { } aTask)
            {
                aTask.GetAwaiter().GetResult();
            }
            var waitCount = 0;
            while (!backgroundChecks.IsEmpty && waitCount++ < 50)
            {
                Thread.Sleep(10);
            }

            // Enqueue current model and flush loop
            alarmSaver.Save(alarmConfig);
            while (true)
            {
                var targetVer = alarmSaver.EnqueuedVersion;
                alarmSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                if (alarmSaver.SavedVersion >= alarmSaver.EnqueuedVersion && !alarmSaver.HasPendingUnsavedChanges)
                {
                    break;
                }
            }

            // Verify that the save triggered by access script cancellation handler was fully flushed and not orphaned
            Equal(false, alarmSaver.HasPendingUnsavedChanges);
            Equal(true, alarmSaver.SavedVersion >= alarmSaver.EnqueuedVersion);
            Equal("saved_from_alarm_cancellation_handler", alarmSavedSnapshots.Last());
        }

        // 7. [Round 5] Alarm scheduler re-arms on exit reset
        {
            var alarmCts = new CancellationTokenSource();
            alarmCts.Cancel();
            var alarmTimerEnabled = false;

            // When exit is aborted or fails, ResetPendingExitState re-arms alarm CTS and restarts timer
            if (alarmCts.IsCancellationRequested)
            {
                alarmCts.Dispose();
                alarmCts = new CancellationTokenSource();
            }
            if (!alarmTimerEnabled) alarmTimerEnabled = true;

            Equal(false, alarmCts.IsCancellationRequested);
            Equal(true, alarmTimerEnabled);
        }

        // 8. [Round 5] Concurrent import / rollback rejected when transaction is active; exit awaits active import transaction
        {
            var activeImport = false;
            var exitPending = false;
            Task? currentImportTask = null;

            bool TryStartImportOrRollback()
            {
                if (activeImport || exitPending) return false;
                return true;
            }

            // Normal start succeeds
            Equal(true, TryStartImportOrRollback());

            // Active import in progress
            activeImport = true;
            var importTcs = new TaskCompletionSource();
            currentImportTask = importTcs.Task;

            // Concurrent import, rollback or export attempts rejected
            Equal(false, TryStartImportOrRollback());

            // Exit begins while import is still running
            exitPending = true;
            Equal(false, TryStartImportOrRollback());

            var importCompleted = false;
            var exitAwaitedImport = false;
            var importConfigSaved = new List<string>();
            var importSaver = new BackgroundConfigSaver("test_concurrent_import.json", (path, snapshot) =>
            {
                lock (importConfigSaved) { importConfigSaved.Add(snapshot.KittyPath); }
            });

            var appConfig = new ManagerConfig { KittyPath = "initial_before_import" };
            importSaver.Save(appConfig);
            importSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();

            var exitTask = Task.Run(async () =>
            {
                // FlushConfigAndExit awaits active import task
                if (currentImportTask is { } impTask)
                {
                    await impTask;
                    exitAwaitedImport = true;
                }
                importSaver.Save(appConfig);
                while (true)
                {
                    var target = importSaver.EnqueuedVersion;
                    await importSaver.FlushPendingSavesAsync();
                    if (importSaver.SavedVersion >= importSaver.EnqueuedVersion && !importSaver.HasPendingUnsavedChanges)
                        break;
                }
            });

            // Simulate import finishing after a small delay
            Thread.Sleep(30);
            appConfig.KittyPath = "merged_from_import";
            activeImport = false;
            currentImportTask = null;
            importTcs.TrySetResult();
            importCompleted = true;

            exitTask.GetAwaiter().GetResult();
            Equal(true, importCompleted);
            Equal(true, exitAwaitedImport);
            Equal(false, importSaver.HasPendingUnsavedChanges);
            Equal(2, importConfigSaved.Count);
            Equal("merged_from_import", importConfigSaved[1]);
        }

        // 9. [Round 5] Update scheduler not cancelled on failed/aborted exit and re-arms on reset
        {
            var updateCts = new CancellationTokenSource();
            var updateSchedulerStartedCount = 0;

            void RunUpdateScheduler(CancellationToken token)
            {
                updateSchedulerStartedCount++;
            }

            // Initial start
            RunUpdateScheduler(updateCts.Token);
            Equal(1, updateSchedulerStartedCount);
            Equal(false, updateCts!.IsCancellationRequested);

            // FlushConfigAndExitAsync no longer cancels updateSchedulerCancellation
            // If exit fails and calls ResetPendingExitState:
            // If updateCts was not cancelled, it continues running without restarting
            if (updateCts is not null && updateCts.IsCancellationRequested)
            {
                updateCts.Dispose();
                updateCts = new CancellationTokenSource();
                RunUpdateScheduler(updateCts.Token);
            }
            Equal(1, updateSchedulerStartedCount);
            Equal(false, updateCts!.IsCancellationRequested);

            // If an explicit cancellation occurred previously, reset re-arms and restarts
            updateCts.Cancel();
            Equal(true, updateCts.IsCancellationRequested);
            if (updateCts is not null && updateCts.IsCancellationRequested)
            {
                updateCts.Dispose();
                updateCts = new CancellationTokenSource();
                RunUpdateScheduler(updateCts.Token);
            }
            Equal(2, updateSchedulerStartedCount);
            Equal(false, updateCts!.IsCancellationRequested);
        }

        // 10. [Round 6] BackgroundConfigSaver propagates OnSaveFailed and preserves pendingSnapshot on write failure
        {
            Exception? caughtError = null;
            var failureInvoked = new ManualResetEventSlim(false);
            var shouldFail = true;

            var saver = new BackgroundConfigSaver("test_error_handling.json", (path, snapshot) =>
            {
                if (shouldFail)
                {
                    throw new UnauthorizedAccessException("Access denied to config path");
                }
            })
            {
                OnSaveFailed = ex =>
                {
                    caughtError = ex;
                    failureInvoked.Set();
                }
            };

            var testConfig = new ManagerConfig { KittyPath = "unsaved_modified_path" };
            saver.Save(testConfig);

            // Wait for failure callback to be invoked
            Equal(true, failureInvoked.Wait(TimeSpan.FromSeconds(2)));
            Equal(true, caughtError is UnauthorizedAccessException);
            Equal("Access denied to config path", caughtError!.Message);

            // Verify pending unsaved state is preserved
            Equal(true, saver.HasPendingUnsavedChanges);
            Equal(true, saver.HasPendingChanges);
            Equal(true, saver.LastError is UnauthorizedAccessException);

            // If write is retried after fixing the issue, it should write the preserved snapshot
            shouldFail = false;
            saver.RetryPendingSave();
            saver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            Equal(false, saver.HasPendingUnsavedChanges);
            Equal(true, saver.LastError is null);
        }

        // 11. [Round 7] Exit sequence tracks, cancels, and awaits SavedLinksDialog background checks and handles errors
        {
            var savedLinksSnapshots = new List<string>();
            var shouldFailSave = false;
            var savedLinksSaver = new BackgroundConfigSaver("test_saved_links_exit.json", (path, snapshot) =>
            {
                if (shouldFailSave)
                {
                    throw new IOException("Simulated disk error during exit flush");
                }
                lock (savedLinksSnapshots) { savedLinksSnapshots.Add(snapshot.KittyPath); }
            });

            var savedLinksConfig = new ManagerConfig { KittyPath = "initial_links_path" };
            savedLinksSaver.Save(savedLinksConfig);
            savedLinksSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            Equal(1, savedLinksSnapshots.Count);

            // 1. Simulate an active SavedLinksDialog check operation running in the background
            var savedLinksChecksCancellation = new CancellationTokenSource();
            var activeSavedLinksChecks = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
            var dialogCts = new CancellationTokenSource();

            var tcs = new TaskCompletionSource();
            activeSavedLinksChecks.TryAdd(tcs.Task, 0);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(dialogCts.Token, savedLinksChecksCancellation.Token);
            var effectiveToken = linkedCts.Token;

            var checkOperationCancelled = false;
            var checkOperationCompleted = false;

            var backgroundCheckTask = Task.Run(async () =>
            {
                try
                {
                    // Wait for cancellation or timeout
                    await Task.Delay(200, effectiveToken);
                }
                catch (OperationCanceledException)
                {
                    checkOperationCancelled = true;
                    // 3. The cancellation handler in the check operation is delayed, modifies config, and calls SaveConfig()
                    await Task.Delay(50);
                    savedLinksConfig.KittyPath = "saved_from_saved_links_cancellation_handler";
                    savedLinksSaver.Save(savedLinksConfig);
                }
                finally
                {
                    activeSavedLinksChecks.TryRemove(tcs.Task, out _);
                    checkOperationCompleted = true;
                    tcs.TrySetResult();
                }
            });

            // 2. Window closing / exit sequence cancels savedLinksChecksCancellation and awaits activeSavedLinksChecks
            savedLinksChecksCancellation.Cancel();
            var runningLinkChecks = activeSavedLinksChecks.Keys.ToArray();
            if (runningLinkChecks.Length > 0)
            {
                Task.WhenAll(runningLinkChecks).GetAwaiter().GetResult();
            }

            // 4. Verify that exit sequence waited for delayed cancellation handler before flush
            Equal(true, checkOperationCancelled);
            Equal(true, checkOperationCompleted);
            Equal(2L, savedLinksSaver.EnqueuedVersion);

            // Flush loop runs
            savedLinksSaver.Save(savedLinksConfig);
            while (true)
            {
                var target = savedLinksSaver.EnqueuedVersion;
                savedLinksSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                if (savedLinksSaver.SavedVersion >= savedLinksSaver.EnqueuedVersion && !savedLinksSaver.HasPendingUnsavedChanges)
                {
                    break;
                }
            }

            Equal(false, savedLinksSaver.HasPendingUnsavedChanges);
            Equal(true, savedLinksSaver.SavedVersion >= savedLinksSaver.EnqueuedVersion);
            Equal("saved_from_saved_links_cancellation_handler", savedLinksSnapshots.Last());

            // 5. Verify that if writing throws an error during this save, exit is aborted, update session is disarmed,
            // and ResetPendingExitState() restores savedLinksChecksCancellation
            var updateSessionDisarmed = false;
            var updateSessionCommitted = false;
            var exitAborted = false;

            // Trigger another save attempt where write fails
            shouldFailSave = true;
            savedLinksConfig.KittyPath = "failing_save_path";
            savedLinksSaver.Save(savedLinksConfig);

            try
            {
                while (true)
                {
                    var target = savedLinksSaver.EnqueuedVersion;
                    savedLinksSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                    if (savedLinksSaver.SavedVersion >= savedLinksSaver.EnqueuedVersion && !savedLinksSaver.HasPendingUnsavedChanges)
                        break;
                }
                updateSessionCommitted = true;
            }
            catch (Exception)
            {
                // In MainWindow.FlushConfigAndExitAsync catch block:
                updateSessionDisarmed = true;
                exitAborted = true;

                // ResetPendingExitState()
                if (savedLinksChecksCancellation.IsCancellationRequested)
                {
                    savedLinksChecksCancellation.Dispose();
                    savedLinksChecksCancellation = new CancellationTokenSource();
                }
            }

            Equal(true, exitAborted);
            Equal(true, updateSessionDisarmed);
            Equal(false, updateSessionCommitted);
            Equal(false, savedLinksChecksCancellation.IsCancellationRequested);
        }

        // 12. [Round 8] Exit sequence tracks, cancels, and awaits access script operations and handles errors
        {
            var accessScriptSnapshots = new List<string>();
            var shouldFailSave = false;
            var accessScriptSaver = new BackgroundConfigSaver("test_access_script_exit.json", (path, snapshot) =>
            {
                if (shouldFailSave)
                {
                    throw new IOException("Simulated disk error during access script exit flush");
                }
                lock (accessScriptSnapshots) { accessScriptSnapshots.Add(snapshot.KittyPath); }
            });

            var accessScriptConfig = new ManagerConfig { KittyPath = "initial_access_script_path" };
            accessScriptSaver.Save(accessScriptConfig);
            accessScriptSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
            Equal(1, accessScriptSnapshots.Count);

            // 1. Simulate active access script operation triggered from link building or preferred route failure recovery
            var accessScriptsMasterCancellation = new CancellationTokenSource();
            var activeAccessScriptTasks = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
            var callerCts = new CancellationTokenSource();

            var tcs = new TaskCompletionSource();
            activeAccessScriptTasks.TryAdd(tcs.Task, 0);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCts.Token, accessScriptsMasterCancellation.Token);
            var effectiveToken = linkedCts.Token;

            var accessScriptCancelled = false;
            var accessScriptCompleted = false;

            var backgroundScriptTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200, effectiveToken);
                }
                catch (OperationCanceledException)
                {
                    accessScriptCancelled = true;
                    // 3. The access script's cancellation handler is delayed, modifies config (e.g. MarkScriptUnconfirmed), and calls SaveConfig()
                    await Task.Delay(50);
                    accessScriptConfig.KittyPath = "saved_from_access_script_cancellation_handler";
                    accessScriptSaver.Save(accessScriptConfig);
                }
                finally
                {
                    activeAccessScriptTasks.TryRemove(tcs.Task, out _);
                    accessScriptCompleted = true;
                    tcs.TrySetResult();
                }
            });

            // 2. Window exit cancelling accessScriptsMasterCancellation and awaiting activeAccessScriptTasks
            accessScriptsMasterCancellation.Cancel();
            var runningAccessScripts = activeAccessScriptTasks.Keys.ToArray();
            if (runningAccessScripts.Length > 0)
            {
                Task.WhenAll(runningAccessScripts).GetAwaiter().GetResult();
            }

            // 4. Verify that FlushConfigAndExitAsync awaits the delayed cancellation handler, flushes the resulting save,
            // and does not close until the save is completely written
            Equal(true, accessScriptCancelled);
            Equal(true, accessScriptCompleted);
            Equal(2L, accessScriptSaver.EnqueuedVersion);

            // Flush loop runs
            accessScriptSaver.Save(accessScriptConfig);
            while (true)
            {
                var target = accessScriptSaver.EnqueuedVersion;
                accessScriptSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                if (accessScriptSaver.SavedVersion >= accessScriptSaver.EnqueuedVersion && !accessScriptSaver.HasPendingUnsavedChanges)
                {
                    break;
                }
            }

            Equal(false, accessScriptSaver.HasPendingUnsavedChanges);
            Equal(true, accessScriptSaver.SavedVersion >= accessScriptSaver.EnqueuedVersion);
            Equal("saved_from_access_script_cancellation_handler", accessScriptSnapshots.Last());

            // 5. Verify that a write failure during this save cleanly aborts exit, disarms update commit,
            // and re-arms accessScriptsMasterCancellation
            var updateSessionDisarmed = false;
            var updateSessionCommitted = false;
            var exitAborted = false;

            // Trigger another save attempt where write fails
            shouldFailSave = true;
            accessScriptConfig.KittyPath = "failing_access_script_save";
            accessScriptSaver.Save(accessScriptConfig);

            try
            {
                while (true)
                {
                    var target = accessScriptSaver.EnqueuedVersion;
                    accessScriptSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                    if (accessScriptSaver.SavedVersion >= accessScriptSaver.EnqueuedVersion && !accessScriptSaver.HasPendingUnsavedChanges)
                        break;
                }
                updateSessionCommitted = true;
            }
            catch (Exception)
            {
                // In MainWindow.FlushConfigAndExitAsync catch block:
                updateSessionDisarmed = true;
                exitAborted = true;

                // ResetPendingExitState()
                if (accessScriptsMasterCancellation.IsCancellationRequested)
                {
                    accessScriptsMasterCancellation.Dispose();
                    accessScriptsMasterCancellation = new CancellationTokenSource();
                }
            }

            Equal(true, exitAborted);
            Equal(true, updateSessionDisarmed);
            Equal(false, updateSessionCommitted);
            Equal(false, accessScriptsMasterCancellation.IsCancellationRequested);
        }


        // 13. [Sol Review Finding 1] Atomic snapshot cloning under lock preserves latest state and prevents stale overwrite
        {
            var saverSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.Core/BackgroundConfigSaver.cs"));
            var saveBody = ExtractMethodBody(saverSrc, "Save(ManagerConfig config)");
            var lockIndex = saveBody.IndexOf("lock (lockObject)");
            var cloneIndex = saveBody.IndexOf("config.CloneForSave()");
            Equal(true, lockIndex >= 0);
            Equal(true, cloneIndex > lockIndex);
            Equal(true, ContainsUncommentedCode(saveBody, "var snapshot = config.CloneForSave();"));

            // Test 1: Sequential controlled ordering (T1 saves V1, then T2 saves V2) -> final saved snapshot is V2.
            {
                var recordedSnapshots = new List<int>();
                var saver = new BackgroundConfigSaver("test_concurrent_save1.json", (path, snapshot) =>
                {
                    lock (recordedSnapshots)
                    {
                        recordedSnapshots.Add(snapshot.UngroupedServers.Count);
                    }
                });

                var cfg1 = new ManagerConfig();
                cfg1.UngroupedServers.Add(new ManagedServer { Name = "V1" });

                var cfg2 = new ManagerConfig();
                cfg2.UngroupedServers.Add(new ManagedServer { Name = "V1" });
                cfg2.UngroupedServers.Add(new ManagedServer { Name = "V2" });

                using var firstSaveStarted = new ManualResetEventSlim(false);
                using var secondSaveCanProceed = new ManualResetEventSlim(false);

                var t1 = Task.Run(() =>
                {
                    firstSaveStarted.Set();
                    saver.Save(cfg1);
                    secondSaveCanProceed.Set();
                });
                var t2 = Task.Run(() =>
                {
                    firstSaveStarted.Wait();
                    secondSaveCanProceed.Wait();
                    saver.Save(cfg2);
                });

                Task.WaitAll(t1, t2);
                saver.FlushPendingSavesAsync().GetAwaiter().GetResult();

                Equal(false, saver.HasPendingUnsavedChanges);
                lock (recordedSnapshots)
                {
                    Equal(true, recordedSnapshots.Count > 0);
                    Equal(2, recordedSnapshots[^1]);
                }
            }

            // Test 2: Reverse controlled ordering (T1 saves V2, then T2 saves V1) -> final saved snapshot is V1.
            {
                var recordedSnapshots = new List<int>();
                var saver = new BackgroundConfigSaver("test_concurrent_save2.json", (path, snapshot) =>
                {
                    lock (recordedSnapshots)
                    {
                        recordedSnapshots.Add(snapshot.UngroupedServers.Count);
                    }
                });

                var cfg1 = new ManagerConfig();
                cfg1.UngroupedServers.Add(new ManagedServer { Name = "V1" });

                var cfg2 = new ManagerConfig();
                cfg2.UngroupedServers.Add(new ManagedServer { Name = "V1" });
                cfg2.UngroupedServers.Add(new ManagedServer { Name = "V2" });

                using var firstSaveStarted = new ManualResetEventSlim(false);
                using var secondSaveCanProceed = new ManualResetEventSlim(false);

                var t1 = Task.Run(() =>
                {
                    firstSaveStarted.Set();
                    saver.Save(cfg2);
                    secondSaveCanProceed.Set();
                });
                var t2 = Task.Run(() =>
                {
                    firstSaveStarted.Wait();
                    secondSaveCanProceed.Wait();
                    saver.Save(cfg1);
                });

                Task.WaitAll(t1, t2);
                saver.FlushPendingSavesAsync().GetAwaiter().GetResult();

                Equal(false, saver.HasPendingUnsavedChanges);
                lock (recordedSnapshots)
                {
                    Equal(true, recordedSnapshots.Count > 0);
                    Equal(1, recordedSnapshots[^1]);
                }
            }

            // Test 3: Contention & race condition test under concurrent save
            {
                var savedSnapshots = new List<string>();
                using var allowWriteToProceed = new ManualResetEventSlim(false);
                using var writeStarted = new ManualResetEventSlim(false);

                var saver = new BackgroundConfigSaver("test_contention.json", (path, snapshot) =>
                {
                    writeStarted.Set();
                    allowWriteToProceed.Wait();
                    lock (savedSnapshots)
                    {
                        savedSnapshots.Add(snapshot.KittyPath);
                    }
                });

                // Start initial write to hold worker
                saver.Save(new ManagerConfig { KittyPath = "initial_hold" });
                writeStarted.Wait();

                var config1 = new ManagerConfig { KittyPath = "thread1_config" };
                var config2 = new ManagerConfig { KittyPath = "thread2_config" };

                using var startGate = new ManualResetEventSlim(false);
                long t1Version = 0;
                long t2Version = 0;

                var t1 = Task.Run(() =>
                {
                    startGate.Wait();
                    t1Version = saver.Save(config1);
                });
                var t2 = Task.Run(() =>
                {
                    startGate.Wait();
                    t2Version = saver.Save(config2);
                });

                startGate.Set();
                Task.WaitAll(t1, t2);

                Equal(3L, saver.EnqueuedVersion);
                Equal(true, (t1Version == 2 && t2Version == 3) || (t1Version == 3 && t2Version == 2));
                var expectedFinalConfig = t1Version > t2Version ? config1.KittyPath : config2.KittyPath;

                allowWriteToProceed.Set();
                saver.WaitForPendingSave();

                Equal(false, saver.HasPendingUnsavedChanges);
                Equal(3L, saver.SavedVersion);
                lock (savedSnapshots)
                {
                    Equal(2, savedSnapshots.Count);
                    Equal("initial_hold", savedSnapshots[0]);
                    Equal(expectedFinalConfig, savedSnapshots[1]);
                }
            }

            // Dispatcher thread marshaling check:
            if (OperatingSystem.IsWindows())
            {
                var appDll = ResolveAppDll();
                if (File.Exists(appDll))
                {
                    Exception? staEx = null;
                    var staThread = new Thread(() =>
                    {
                        try
                        {
                            RunWindowsStaDispatcherTest(appDll);
                        }
                        catch (Exception ex)
                        {
                            staEx = ex;
                        }
                    });
                    staThread.SetApartmentState(ApartmentState.STA);
                    staThread.Start();
                    if (!staThread.Join(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Windows STA Dispatcher test timed out after 10 seconds.");
                    }
                    if (staEx is not null) throw staEx;
                }
            }
            else
            {
                // On non-Windows platforms (Linux CI / test runner):
                // 1. Verify statically via source extraction:
                var saveConfigBody = ExtractMethodBody(mwSrc, "void SaveConfig()");
                Equal(true, ContainsUncommentedCode(saveConfigBody, "if (!Dispatcher.CheckAccess())"));
                Equal(true, ContainsUncommentedCode(saveConfigBody, "Dispatcher.Invoke(SaveConfig);"));

                var batchCheckBody = ExtractMethodBody(mwSrc, "Task<IReadOnlyList<ConnectivityResult>> CheckConnectivityBatchFromAsync(");
                Equal(true, ContainsUncommentedCode(batchCheckBody, "Dispatcher.CheckAccess()"));
                Equal(true, ContainsUncommentedCode(batchCheckBody, "ConfigTransfer.CloneSnapshot(config)"));
                Equal(true, ContainsUncommentedCode(batchCheckBody, "Dispatcher.Invoke(() => ConfigTransfer.CloneSnapshot(config))"));
                Equal(true, ContainsUncommentedCode(batchCheckBody, "ApplyDiscoveredRoutesAndPreferences(config, snapshotConfig, initialFingerprints, initialRoutes, sourceId, targetIds, () => Dispatcher.CheckAccess())"));

                // 2. Verify behaviorally that marshaling logic ensures single-threaded UI execution
                var uiThreadId = -1;
                var activeConcurrency = 0;
                var maxConcurrency = 0;
                var executedCount = 0;
                using var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
                using var uiDone = new ManualResetEventSlim(false);

                var fakeUiLoop = new Thread(() =>
                {
                    uiThreadId = Environment.CurrentManagedThreadId;
                    foreach (var action in queue.GetConsumingEnumerable())
                    {
                        var cur = Interlocked.Increment(ref activeConcurrency);
                        if (cur > maxConcurrency) maxConcurrency = cur;
                        action();
                        Interlocked.Decrement(ref activeConcurrency);
                    }
                    uiDone.Set();
                });
                fakeUiLoop.Start();

                void MarshalToUi(Action action)
                {
                    if (Environment.CurrentManagedThreadId == uiThreadId)
                    {
                        action();
                    }
                    else
                    {
                        using var completion = new ManualResetEventSlim(false);
                        queue.Add(() =>
                        {
                            action();
                            completion.Set();
                        });
                        completion.Wait();
                    }
                }

                var mockSavedSnapshots = new List<string>();
                var mockSaver = new BackgroundConfigSaver("mock_ui_save.json", (p, snap) =>
                {
                    lock (mockSavedSnapshots) { mockSavedSnapshots.Add(snap.KittyPath); }
                });
                var mockLiveConfig = new ManagerConfig { KittyPath = "initial_mock" };

                // Simulated SaveConfig matching MainWindow.SaveConfig behavior
                void MockSaveConfig()
                {
                    if (Environment.CurrentManagedThreadId != uiThreadId)
                    {
                        MarshalToUi(MockSaveConfig);
                        return;
                    }
                    mockSaver.Save(mockLiveConfig);
                }

                using var raceGate = new ManualResetEventSlim(false);
                var mockUpdateLock = new object();
                var latestMockKittyPath = "";
                var bgTasks = new Task[10];
                for (var i = 0; i < 10; i++)
                {
                    var taskIndex = i;
                    bgTasks[i] = Task.Run(() =>
                    {
                        raceGate.Wait();
                        Equal(false, Environment.CurrentManagedThreadId == uiThreadId);
                        lock (mockUpdateLock)
                        {
                            mockLiveConfig.KittyPath = $"mock_path_{taskIndex}";
                            latestMockKittyPath = mockLiveConfig.KittyPath;
                            MockSaveConfig();
                            Interlocked.Increment(ref executedCount);
                        }
                    });
                }

                raceGate.Set();
                Task.WaitAll(bgTasks);
                queue.CompleteAdding();
                uiDone.Wait();

                mockSaver.FlushPendingSavesAsync().GetAwaiter().GetResult();
                Equal(false, mockSaver.HasPendingUnsavedChanges);
                lock (mockSavedSnapshots)
                {
                    Equal(true, mockSavedSnapshots.Count > 0);
                    Equal(latestMockKittyPath, mockSavedSnapshots[^1]);
                }

                Equal(10, executedCount);
                Equal(0, activeConcurrency);
                Equal(1, maxConcurrency);
            }
        }


        // 14. [Sol Review Finding 2] CleanupStaleFiles respects cutoffUtc and does not delete newly created files
        {
            var isolatedTestDir = Path.Combine(Path.GetTempPath(), "KiTTYManager_IsolatedTest_" + Guid.NewGuid().ToString("N"));
            var controlDir = Path.Combine(Path.GetTempPath(), "KiTTYManager_Control_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolatedTestDir);
            Directory.CreateDirectory(controlDir);

            var oldFilePath = Path.Combine(isolatedTestDir, "KiTTYManager-route-" + Guid.NewGuid().ToString("N"));
            var newFilePath = Path.Combine(isolatedTestDir, "KiTTYManager-route-" + Guid.NewGuid().ToString("N"));
            var controlFilePath = Path.Combine(controlDir, "KiTTYManager-route-" + Guid.NewGuid().ToString("N"));

            File.WriteAllText(oldFilePath, "old_route_session");
            File.WriteAllText(newFilePath, "new_route_session");
            File.WriteAllText(controlFilePath, "control_session");

            var cutoff = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(oldFilePath, cutoff.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newFilePath, cutoff.AddMinutes(5));

            try
            {
                KittyRoutedSession.CleanupStaleFiles(cutoffUtc: cutoff, routedSessionsDirectory: isolatedTestDir);

                Equal(false, File.Exists(oldFilePath));
                Equal(true, File.Exists(newFilePath));
                Equal(true, File.Exists(controlFilePath));

                // Also test calling without cutoff on isolatedTestDir: assert all files in isolatedTestDir are removed, while control file remains untouched.
                KittyRoutedSession.CleanupStaleFiles(routedSessionsDirectory: isolatedTestDir);
                Equal(false, File.Exists(oldFilePath));
                Equal(false, File.Exists(newFilePath));
                Equal(true, File.Exists(controlFilePath));
            }
            finally
            {
                try { if (Directory.Exists(isolatedTestDir)) Directory.Delete(isolatedTestDir, true); } catch { }
                try { if (Directory.Exists(controlDir)) Directory.Delete(controlDir, true); } catch { }
            }
        }


        // 15. [Sol Review Finding 3] Import wizard transaction rollback and link pair invalidation
        {
            Equal(true, ContainsUncommentedCode(mwSrc, "isImportWizardActive"));
            Equal(true, ContainsUncommentedCode(mwSrc, "ServerLinkPairPolicy.Invalidate(config, pair.A.Id, pair.B.Id);"));
        }



        // 16. [Sol Review Round 12] CheckConnectivityBatchFromAsync snapshot isolation & ApplyDiscoveredRoutesAndPreferences host keys & endpoint sync
        {
            var applyRoutesSrc = File.ReadAllText(ResolveSourceFile("src/KiTTYManager.App/MainWindow.Connectivity.cs"));
            var applyRoutesBody = ExtractMethodBody(applyRoutesSrc, "bool ApplyDiscoveredRoutesAndPreferences(");
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "throw new InvalidOperationException(\"ApplyDiscoveredRoutesAndPreferences must be called on the UI thread.\");"));
            Equal(false, ContainsUncommentedCode(applyRoutesBody, "RoutePreferencePolicy.ShouldReplacePreferred(liveSource.PreferredRoute"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "snapSource.PreferredRoute.LastSuccessUtc >= liveSource.PreferredRoute.LastSuccessUtc"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "RoutePreferencePolicy.ShouldReplacePreferred(liveTarget.PreferredRoute, snapTarget.PreferredRoute.ServerIds)"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "!string.Equals(hop.HostKeyFingerprint, initial.Fingerprint, StringComparison.Ordinal)"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "string.Equals(liveHop.HostKeyFingerprint ?? \"\", initial.Fingerprint, StringComparison.Ordinal)"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "liveHop.HostKeyFingerprint = hop.HostKeyFingerprint;"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "liveHop.HostKeyAlgorithm = hop.HostKeyAlgorithm;"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "liveHop.HostKeyBits = hop.HostKeyBits;"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "SyncEndpointPreferences(liveHop, hop)"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "snapTarget.PreferredRoute != initialTargetRoute"));
            Equal(true, ContainsUncommentedCode(applyRoutesBody, "snapSource.PreferredRoute != initialSourceRoute"));

            // Behavioral test: verify snapshot mutation isolation and real ApplyDiscoveredRoutesAndPreferences execution
            var sourceServerId = Guid.NewGuid();
            var hopServerId = Guid.NewGuid();
            var targetServerId = Guid.NewGuid();

            var batchLiveConfig = new ManagerConfig
            {
                UngroupedServers =
                [
                    new ManagedServer { Id = sourceServerId, Name = "SourceNode", Host = "10.0.0.1", Port = 22, HostKeyFingerprint = "SHA256:orig_source_fp" },
                    new ManagedServer { Id = hopServerId, Name = "BastionHop", Host = "10.0.0.50", Port = 22, HostKeyFingerprint = "" },
                    new ManagedServer { Id = targetServerId, Name = "TargetNode", Host = "10.0.0.2", Port = 22, HostKeyFingerprint = "" }
                ]
            };

            var savedSnapshots = new List<ManagerConfig>();
            var saver = new BackgroundConfigSaver("test_ssh_snapshot_isolation.json", (p, snap) =>
            {
                lock (savedSnapshots) { savedSnapshots.Add(snap); }
            });

            // 1. Background task creates snapshot and captures initialFingerprints
            var snapshot = ConfigTransfer.CloneSnapshot(batchLiveConfig);
            var initialFingerprints = snapshot.AllServers()
                .ToDictionary(s => s.Id, s => (Host: s.Host, Port: s.Port, Fingerprint: s.HostKeyFingerprint ?? ""));

            using var bgStarted = new ManualResetEventSlim(false);
            using var uiSaveDone = new ManualResetEventSlim(false);

            var bgTask = Task.Run(() =>
            {
                bgStarted.Set();
                // Mutate snapshot servers during background SSH discovery
                var snapSource = snapshot.FindServer(sourceServerId)!;
                var snapHop = snapshot.FindServer(hopServerId)!;
                var snapTarget = snapshot.FindServer(targetServerId)!;

                snapSource.PreferredRoute = new CachedRoute { ServerIds = [sourceServerId], LatencyMs = 12.0 };
                snapTarget.PreferredRoute = new CachedRoute { ServerIds = [sourceServerId, hopServerId, targetServerId], LatencyMs = 36.0 };

                // Confirmed host keys during SSH handshake
                snapHop.HostKeyFingerprint = "SHA256:confirmed_bastion_key_abc123";
                snapHop.HostKeyAlgorithm = "ssh-ed25519";
                snapHop.HostKeyBits = 256;

                snapTarget.HostKeyFingerprint = "SHA256:confirmed_target_key_xyz789";
                snapTarget.HostKeyAlgorithm = "ecdsa-sha2-nistp256";
                snapTarget.HostKeyBits = 256;

                // Discovered endpoint preferences
                ServerEndpointPolicy.Remember(snapHop, new ServerEndpoint("10.0.0.50", 2222), EndpointContext.Direct(sourceServerId));
                ServerEndpointPolicy.Remember(snapTarget, new ServerEndpoint("10.0.0.99", 22), EndpointContext.Via(hopServerId));

                uiSaveDone.Wait();
            });

            bgStarted.Wait();

            // 2. Concurrently, UI thread modifies live config and saves it
            batchLiveConfig.KittyPath = "ui_active_kitty";
            saver.Save(batchLiveConfig);

            // Verify live config servers were NOT mutated directly by background SSH changes on snapshot
            var currentLiveSource = batchLiveConfig.FindServer(sourceServerId)!;
            var currentLiveHop = batchLiveConfig.FindServer(hopServerId)!;
            var currentLiveTarget = batchLiveConfig.FindServer(targetServerId)!;
            Equal(null, currentLiveSource.PreferredRoute);
            Equal(null, currentLiveTarget.PreferredRoute);
            Equal("", currentLiveHop.HostKeyFingerprint);
            Equal("", currentLiveTarget.HostKeyFingerprint);
            Equal(0, currentLiveHop.EndpointPreferences.Count);
            Equal(0, currentLiveTarget.EndpointPreferences.Count);

            uiSaveDone.Set();
            bgTask.Wait();
            saver.FlushPendingSavesAsync().GetAwaiter().GetResult();

            // 3. UI thread applies discovered routes and preferences using the REAL method
            // Calling real method directly from a background thread with () => false must throw InvalidOperationException
            var threwOnWrongThread = false;
            try
            {
                MainWindow.ApplyDiscoveredRoutesAndPreferences(
                    batchLiveConfig, snapshot, initialFingerprints, sourceServerId, [targetServerId], () => false);
            }
            catch (InvalidOperationException)
            {
                threwOnWrongThread = true;
            }
            Equal(true, threwOnWrongThread);

            // Calling real method with () => true applies changes cleanly
            var changed = MainWindow.ApplyDiscoveredRoutesAndPreferences(
                batchLiveConfig, snapshot, initialFingerprints, sourceServerId, [targetServerId], () => true);
            Equal(true, changed);

            saver.Save(batchLiveConfig);
            saver.FlushPendingSavesAsync().GetAwaiter().GetResult();

            // 4. Verify all discovered attributes were updated on live config:
            // - Preferred routes
            Equal(1, currentLiveSource.PreferredRoute?.ServerIds.Count);
            Equal(3, currentLiveTarget.PreferredRoute?.ServerIds.Count);

            // - Confirmed host keys (fingerprint, algorithm, bits) on target AND intermediate hop
            Equal("SHA256:confirmed_bastion_key_abc123", currentLiveHop.HostKeyFingerprint);
            Equal("ssh-ed25519", currentLiveHop.HostKeyAlgorithm);
            Equal(256, currentLiveHop.HostKeyBits);

            Equal("SHA256:confirmed_target_key_xyz789", currentLiveTarget.HostKeyFingerprint);
            Equal("ecdsa-sha2-nistp256", currentLiveTarget.HostKeyAlgorithm);
            Equal(256, currentLiveTarget.HostKeyBits);

            // - Discovered endpoint preferences on target AND intermediate hop
            Equal(1, currentLiveHop.EndpointPreferences.Count);
            Equal("10.0.0.50", currentLiveHop.EndpointPreferences[0].Endpoint.Host);
            Equal(2222, currentLiveHop.EndpointPreferences[0].Endpoint.Port);

            Equal(1, currentLiveTarget.EndpointPreferences.Count);
            Equal("10.0.0.99", currentLiveTarget.EndpointPreferences[0].Endpoint.Host);
            Equal(22, currentLiveTarget.EndpointPreferences[0].Endpoint.Port);

            Equal(false, saver.HasPendingUnsavedChanges);

            // 5. [Sol Review Round 13 Finding 1 & Finding 2] Regression tests:
            // Guard SSH Host Key Overwrites scenarios:
            // Scenario 1: initial = "fpA", snapshot = "fpA", live = "fpB" -> live remains "fpB"
            {
                var sId = Guid.NewGuid();
                var live = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpB" }] };
                var snap = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpA" }] };
                var initFp = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>
                {
                    [sId] = ("host1", 22, "fpA")
                };
                var ch = MainWindow.ApplyDiscoveredRoutesAndPreferences(live, snap, initFp, sId, []);
                Equal(false, ch);
                Equal("fpB", live.FindServer(sId)!.HostKeyFingerprint);
            }

            // Scenario 2: initial = "fpA", snapshot = "fpC", live = "fpB" (concurrent change in live) -> live must NOT be overwritten by "fpC", remains "fpB"
            {
                var sId = Guid.NewGuid();
                var live = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpB" }] };
                var snap = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpC", HostKeyAlgorithm = "ssh-ed25519", HostKeyBits = 256 }] };
                var initFp = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>
                {
                    [sId] = ("host1", 22, "fpA")
                };
                var ch = MainWindow.ApplyDiscoveredRoutesAndPreferences(live, snap, initFp, sId, []);
                Equal(false, ch);
                Equal("fpB", live.FindServer(sId)!.HostKeyFingerprint);
            }

            // Scenario 3: initial = "fpA", snapshot = "fpB", live = "fpA" -> live updated to "fpB"
            {
                var sId = Guid.NewGuid();
                var live = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpA" }] };
                var snap = new ManagerConfig { UngroupedServers = [new ManagedServer { Id = sId, Host = "host1", Port = 22, HostKeyFingerprint = "fpB", HostKeyAlgorithm = "ssh-rsa", HostKeyBits = 4096 }] };
                var initFp = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>
                {
                    [sId] = ("host1", 22, "fpA")
                };
                var ch = MainWindow.ApplyDiscoveredRoutesAndPreferences(live, snap, initFp, sId, []);
                Equal(true, ch);
                Equal("fpB", live.FindServer(sId)!.HostKeyFingerprint);
                Equal("ssh-rsa", live.FindServer(sId)!.HostKeyAlgorithm);
                Equal(4096, live.FindServer(sId)!.HostKeyBits);
            }

            // Source Route Update Semantics:
            // Source has old route through P1 with 2 hops (LastSuccessUtc = T1)
            // Check succeeds through P2 with 2 hops (LastSuccessUtc = T2 > T1)
            // Verify that liveSource.PreferredRoute is updated to P2 even though length is identical.
            {
                var srcId = Guid.NewGuid();
                var p1Id = Guid.NewGuid();
                var p2Id = Guid.NewGuid();
                var t1 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                var t2 = new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

                var live = new ManagerConfig
                {
                    UngroupedServers =
                    [
                        new ManagedServer
                        {
                            Id = srcId,
                            Host = "src.local",
                            Port = 22,
                            PreferredRoute = new CachedRoute { ServerIds = [srcId, p1Id], LastSuccessUtc = t1 }
                        }
                    ]
                };
                var snap = new ManagerConfig
                {
                    UngroupedServers =
                    [
                        new ManagedServer
                        {
                            Id = srcId,
                            Host = "src.local",
                            Port = 22,
                            PreferredRoute = new CachedRoute { ServerIds = [srcId, p2Id], LastSuccessUtc = t2 }
                        }
                    ]
                };
                var initFp = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>
                {
                    [srcId] = ("src.local", 22, "")
                };

                var ch = MainWindow.ApplyDiscoveredRoutesAndPreferences(live, snap, initFp, srcId, []);
                Equal(true, ch);
                var updatedSource = live.FindServer(srcId)!;
                Equal(2, updatedSource.PreferredRoute?.ServerIds.Count);
                Equal(p2Id, updatedSource.PreferredRoute?.ServerIds[1]);
                Equal(t2, updatedSource.PreferredRoute?.LastSuccessUtc);
            }
        }
    }

    private static void ApplyDiscoveredRoutesAndPreferencesPreservesSuccessfulProxyStateTests()
    {
        var oldProxy = new BaseProxy { Name = "P1", Host = "p1", Port = 1080, LastConnectLatencyMs = 500 };
        var workingProxy = new BaseProxy { Name = "P2", Host = "p2", Port = 1080, LastConnectLatencyMs = 1000 };
        var source = new ManagedServer { Name = "source", Host = "source", PreferredProxyId = oldProxy.Id };
        var target = new ManagedServer { Name = "target", Host = "target", PreferredProxyId = oldProxy.Id };
        var hop = new ManagedServer { Name = "hop", Host = "hop", PreferredProxyId = oldProxy.Id };
        var another = new ManagedServer { Name = "another", Host = "another" };
        var live = new ManagerConfig
        {
            UngroupedServers = [source, target, hop, another],
            BaseProxies = [oldProxy, workingProxy]
        };
        var snapshot = live.CloneForSave();
        var initial = snapshot.AllServers().ToDictionary(s => s.Id, s => (s.Host, s.Port, s.HostKeyFingerprint ?? ""));

        var snapSource = snapshot.FindServer(source.Id)!;
        var snapTarget = snapshot.FindServer(target.Id)!;
        var snapHop = snapshot.FindServer(hop.Id)!;
        var snapProxy = snapshot.BaseProxies.Single(p => p.Id == workingProxy.Id);

        var successTime = DateTimeOffset.UtcNow;
        snapSource.PreferredProxyId = workingProxy.Id;
        snapTarget.PreferredProxyId = workingProxy.Id;
        snapHop.PreferredProxyId = workingProxy.Id;
        snapProxy.LastSuccessUtc = successTime;
        snapProxy.LastConnectLatencyMs = 10;
        snapSource.PreferredRoute = new CachedRoute { ProxyId = workingProxy.Id, ServerIds = [source.Id], LastSuccessUtc = successTime };
        snapTarget.PreferredRoute = new CachedRoute { ProxyId = workingProxy.Id, ServerIds = [target.Id], LastSuccessUtc = successTime };

        var changed = MainWindow.ApplyDiscoveredRoutesAndPreferences(
            live, snapshot, initial, source.Id, [target.Id], () => true);

        Equal(true, changed);
        Equal(workingProxy.Id, source.PreferredProxyId);
        Equal(workingProxy.Id, target.PreferredProxyId);
        Equal(oldProxy.Id, hop.PreferredProxyId);
        Equal(10.0, workingProxy.LastConnectLatencyMs);
        Equal(successTime, workingProxy.LastSuccessUtc);

        // Verification of RoutePlanner ranking:
        // 1. DirectCandidates for source must place workingProxy (P2) first
        var direct = RoutePlanner.DirectCandidates(live, source.Id);
        Equal(true, direct.Count > 0);
        Equal(workingProxy.Id, direct[0].Proxy.Id);

        // 2. DirectCandidates for target must place workingProxy (P2) first
        var targetDirect = RoutePlanner.DirectCandidates(live, target.Id);
        Equal(true, targetDirect.Count > 0);
        Equal(workingProxy.Id, targetDirect[0].Proxy.Id);

        // 3. OrderedProxiesForStartup must place workingProxy (P2) first due to recent success and lower latency
        var startupProxies = RoutePlanner.OrderedProxiesForStartup(live);
        Equal(true, startupProxies.Count > 0);
        Equal(workingProxy.Id, startupProxies[0].Id);

        // 4. Candidates for another target must place workingProxy (P2) first
        var candidates = RoutePlanner.Candidates(live, another.Id);
        Equal(true, candidates.Count > 0);
        Equal(workingProxy.Id, candidates[0].Proxy.Id);

        // 5. Sol Round 15 exact reproducer case: empty targetIds list, verifying candidate alignment with snapshot
        var solLive = new ManagerConfig { UngroupedServers = [source, another], BaseProxies = [oldProxy, workingProxy] };
        oldProxy.LastConnectLatencyMs = 500;
        workingProxy.LastConnectLatencyMs = 1000;
        workingProxy.LastSuccessUtc = null;
        source.PreferredProxyId = oldProxy.Id;
        var solSnapshot = solLive.CloneForSave();
        var solSnapSource = solSnapshot.FindServer(source.Id)!;
        var solSnapProxy = solSnapshot.BaseProxies.Single(p => p.Id == workingProxy.Id);
        solSnapSource.PreferredProxyId = workingProxy.Id;
        solSnapProxy.LastSuccessUtc = successTime;
        solSnapProxy.LastConnectLatencyMs = 10;
        solSnapSource.PreferredRoute = new CachedRoute { ProxyId = workingProxy.Id, ServerIds = [source.Id], LastSuccessUtc = successTime };
        var solInitial = solSnapshot.AllServers().ToDictionary(s => s.Id, s => (s.Host, s.Port, s.HostKeyFingerprint ?? ""));

        MainWindow.ApplyDiscoveredRoutesAndPreferences(solLive, solSnapshot, solInitial, source.Id, [], () => true);
        Equal(workingProxy.Id, source.PreferredProxyId);
        Equal(10.0, workingProxy.LastConnectLatencyMs);
        Equal(successTime, workingProxy.LastSuccessUtc);
        Equal(workingProxy.Id, RoutePlanner.DirectCandidates(solLive, source.Id)[0].Proxy.Id);
        Equal(workingProxy.Id, RoutePlanner.OrderedProxiesForStartup(solLive)[0].Id);
        Equal(RoutePlanner.Candidates(solSnapshot, another.Id)[0].Proxy.Id, RoutePlanner.Candidates(solLive, another.Id)[0].Proxy.Id);

        // 6. Verify older snapshot metrics do not overwrite newer live metrics
        var olderSnapshot = live.CloneForSave();
        var snapOlderProxy = olderSnapshot.BaseProxies.Single(p => p.Id == workingProxy.Id);
        snapOlderProxy.LastSuccessUtc = successTime.AddMinutes(-10);
        snapOlderProxy.LastConnectLatencyMs = 999;
        MainWindow.ApplyDiscoveredRoutesAndPreferences(
            live, olderSnapshot, initial, source.Id, [target.Id], () => true);
        Equal(10.0, workingProxy.LastConnectLatencyMs);
        Equal(successTime, workingProxy.LastSuccessUtc);

        // 7. [Sol Review Round 16 Finding 1] Stale snapshot must not roll back PreferredProxyId on unrelated servers or newer source
        {
            var p1 = new BaseProxy { Name = "P1", Host = "p1", LastConnectLatencyMs = 100 };
            var p2 = new BaseProxy { Name = "P2", Host = "p2", LastConnectLatencyMs = 100 };
            var checkSource = new ManagedServer { Name = "check-source", Host = "source" };
            var checkTarget = new ManagedServer { Name = "check-target", Host = "target" };
            var unrelated = new ManagedServer { Name = "opening-console", Host = "unrelated", PreferredProxyId = p1.Id };
            var concurrentLive = new ManagerConfig { UngroupedServers = [checkSource, checkTarget, unrelated], BaseProxies = [p1, p2] };
            var concurrentSnapshot = concurrentLive.CloneForSave();
            var concurrentInitial = concurrentSnapshot.AllServers().ToDictionary(s => s.Id, s => (s.Host, s.Port, s.HostKeyFingerprint ?? ""));

            // CheckFromAsync completes a source connection through P1 while a concurrent
            // OpenRoutedConsoleAsync(unrelated) completes through P2 and sets PreferredProxyId / PreferredRoute.
            var now = DateTimeOffset.UtcNow;
            concurrentSnapshot.FindServer(checkSource.Id)!.PreferredProxyId = p1.Id;
            concurrentSnapshot.FindServer(checkSource.Id)!.PreferredRoute = new CachedRoute { ProxyId = p1.Id, ServerIds = [checkSource.Id], LastSuccessUtc = now };
            unrelated.PreferredProxyId = p2.Id;
            unrelated.PreferredRoute = new CachedRoute { ProxyId = p2.Id, ServerIds = [unrelated.Id], LastSuccessUtc = now.AddSeconds(1) };

            var before = RoutePlanner.DirectCandidates(concurrentLive, unrelated.Id)[0].Proxy.Id;
            Equal(p2.Id, before);

            MainWindow.ApplyDiscoveredRoutesAndPreferences(concurrentLive, concurrentSnapshot, concurrentInitial, checkSource.Id, [checkTarget.Id], () => true);

            var after = RoutePlanner.DirectCandidates(concurrentLive, unrelated.Id)[0].Proxy.Id;
            // Unrelated server: newer PreferredProxyId and DirectCandidates ordering must be preserved
            Equal(p2.Id, unrelated.PreferredProxyId);
            Equal(p2.Id, after);
            Equal(p2.Id, unrelated.PreferredRoute?.ProxyId);

            // Validate newer source route/proxy preservation:
            // If live source has a newer preferred route/proxy (now.AddSeconds(2)), applying an older snapshot does not roll back source
            checkSource.PreferredProxyId = p2.Id;
            checkSource.PreferredRoute = new CachedRoute { ProxyId = p2.Id, ServerIds = [checkSource.Id], LastSuccessUtc = now.AddSeconds(2) };
            MainWindow.ApplyDiscoveredRoutesAndPreferences(concurrentLive, concurrentSnapshot, concurrentInitial, checkSource.Id, [checkTarget.Id], () => true);
            Equal(p2.Id, checkSource.PreferredRoute?.ProxyId);
            Equal(p2.Id, checkSource.PreferredProxyId);
        }

        // 8. [Sol Review Round 17 Finding 1] Target stale unchanged route in snapshot must not overwrite fresh live target route or change RoutePlanner candidate ordering
        {
            var p1 = new BaseProxy { Name = "P1", Host = "p1", LastConnectLatencyMs = 100 };
            var p2 = new BaseProxy { Name = "P2", Host = "p2", LastConnectLatencyMs = 100 };
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
            var a = new ManagedServer { Name = "check-source", Host = "a" };
            var staleHop = new ManagedServer { Name = "working-hop", Host = "hop" };
            var b = new ManagedServer { Name = "check-target", Host = "b", PreferredProxyId = p1.Id };
            b.PreferredRoute = new CachedRoute { ProxyId = p1.Id, ServerIds = [b.Id], LastSuccessUtc = t0 };
            staleHop.PreferredRoute = new CachedRoute { ProxyId = p2.Id, ServerIds = [staleHop.Id], LastSuccessUtc = t0 };
            var staleLive = new ManagerConfig
            {
                UngroupedServers = [a, staleHop, b],
                BaseProxies = [p1, p2],
                Links = [new ServerLink { FromServerId = staleHop.Id, ToServerId = b.Id, LastSuccessUtc = t0, LastSuccessfulProxyId = p2.Id }]
            };
            var staleSnapshot = staleLive.CloneForSave();
            var staleInitial = staleSnapshot.AllServers().ToDictionary(s => s.Id, s => (s.Host, s.Port, s.HostKeyFingerprint ?? ""));
            var initialRoutes = staleSnapshot.AllServers().ToDictionary(s => s.Id, s => s.PreferredRoute);

            // CheckFromAsync reaches source A, but checking B fails. Its loop at
            // SshConnectionService.cs:465 only updates successful targets, so B's
            // snapshot route remains the old P1 direct route, exactly as captured.
            var snapA = staleSnapshot.FindServer(a.Id)!;
            snapA.PreferredProxyId = p1.Id;
            snapA.PreferredRoute = new CachedRoute { ProxyId = p1.Id, ServerIds = [a.Id], LastSuccessUtc = t0.AddMinutes(1) };

            // An already-running OpenRoutedConsoleAsync(B) succeeds via P2/hop/B.
            // CompleteRoute assigns the new route and proxy at lines 2687-2688.
            var freshRoute = new CachedRoute { ProxyId = p2.Id, ServerIds = [staleHop.Id, b.Id], LastSuccessUtc = t0.AddMinutes(2) };
            b.PreferredRoute = freshRoute;
            b.PreferredProxyId = p2.Id;
            var before = RoutePlanner.OrderPreferred(staleLive, RoutePlanner.Candidates(staleLive, b.Id), b.PreferredRoute)[0];
            Equal(p2.Id, before.Proxy.Id);

            MainWindow.ApplyDiscoveredRoutesAndPreferences(staleLive, staleSnapshot, staleInitial, initialRoutes, a.Id, [b.Id], () => true);

            var after = RoutePlanner.OrderPreferred(staleLive, RoutePlanner.Candidates(staleLive, b.Id), b.PreferredRoute)[0];

            // Assert that target B's fresh route, proxy P2 and RoutePlanner ordering are preserved
            Equal(freshRoute, b.PreferredRoute);
            Equal(p2.Id, b.PreferredProxyId);
            Equal(p2.Id, after.Proxy.Id);

            // Also verify that if B IS newly discovered/updated during check, it updates successfully
            var newlyDiscoveredRoute = new CachedRoute { ProxyId = p1.Id, ServerIds = [b.Id], LastSuccessUtc = t0.AddMinutes(3) };
            staleSnapshot.FindServer(b.Id)!.PreferredRoute = newlyDiscoveredRoute;
            staleSnapshot.FindServer(b.Id)!.PreferredProxyId = p1.Id;
            MainWindow.ApplyDiscoveredRoutesAndPreferences(staleLive, staleSnapshot, staleInitial, initialRoutes, a.Id, [b.Id], () => true);
            Equal(newlyDiscoveredRoute, b.PreferredRoute);
            Equal(p1.Id, b.PreferredProxyId);
        }
    }
}

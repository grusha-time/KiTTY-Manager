using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private static void RouteAttemptLimitDefaultsAndRoundTrip()
    {
        Equal(10, new ManagerConfig().MaxRouteAttempts);
        Equal(1, new ManagerConfig().MaxGroupServersInRouteAttempts);

        // NormalizeLimit checks
        Equal(10, RouteAttemptBudget.NormalizeLimit(0));
        Equal(10, RouteAttemptBudget.NormalizeLimit(-5));
        Equal(100, RouteAttemptBudget.NormalizeLimit(101));
        Equal(1, RouteAttemptBudget.NormalizeLimit(1));
        Equal(100, RouteAttemptBudget.NormalizeLimit(100));
        Equal(50, RouteAttemptBudget.NormalizeLimit(50));

        var path = TempFile();
        try
        {
            ConfigStore.Save(path, new ManagerConfig
            {
                MaxRouteAttempts = 25,
                MaxGroupServersInRouteAttempts = 3
            });
            var loaded = ConfigStore.Load(path);
            Equal(25, loaded.MaxRouteAttempts);
            Equal(3, loaded.MaxGroupServersInRouteAttempts);

            // Invalid / boundary values loaded through ConfigStore are normalized
            ConfigStore.Save(path, new ManagerConfig
            {
                MaxRouteAttempts = 0,
                MaxGroupServersInRouteAttempts = 0
            });
            loaded = ConfigStore.Load(path);
            Equal(10, loaded.MaxRouteAttempts);
            Equal(0, loaded.MaxGroupServersInRouteAttempts);

            ConfigStore.Save(path, new ManagerConfig
            {
                MaxRouteAttempts = 150,
                MaxGroupServersInRouteAttempts = 150
            });
            loaded = ConfigStore.Load(path);
            Equal(100, loaded.MaxRouteAttempts);
            Equal(100, loaded.MaxGroupServersInRouteAttempts);

            // Negative MaxGroupServersInRouteAttempts clamps to 0
            ConfigStore.Save(path, new ManagerConfig
            {
                MaxGroupServersInRouteAttempts = -5
            });
            Equal(0, ConfigStore.Load(path).MaxGroupServersInRouteAttempts);

            // Missing JSON property preserves default (1)
            File.WriteAllText(path, "{}");
            Equal(1, ConfigStore.Load(path).MaxGroupServersInRouteAttempts);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void RouteAttemptBudgetEnforcementAndException()
    {
        var budget = new RouteAttemptBudget(3);
        Equal(3, budget.Limit);
        Equal(3, budget.Remaining);
        Equal(0, budget.AttemptCount);
        Equal(true, budget.HasCapacity);

        Equal(true, budget.TryAcquire());
        Equal(2, budget.Remaining);
        Equal(1, budget.AttemptCount);

        Equal(true, budget.TryAcquire());
        Equal(1, budget.Remaining);
        Equal(2, budget.AttemptCount);

        Equal(true, budget.TryAcquire());
        Equal(0, budget.Remaining);
        Equal(3, budget.AttemptCount);
        Equal(false, budget.HasCapacity);

        Equal(false, budget.TryAcquire());
        Equal(0, budget.Remaining);
        Equal(3, budget.AttemptCount);

        var ex = new RouteAttemptLimitException(budget.Limit, budget.AttemptCount);
        Equal(3, ex.AttemptLimit);
        Equal(3, ex.AttemptCount);
        Equal(RouteAttemptLimitException.FormatMessage(3), ex.Message);
        var expectedMessage = "Перебрали 3 вариантов маршрутов, ни один не сработал. Если нужно увеличить количество возможных вариантов, это можно сделать в настройках (пункт меню «Настройки» → блок «Подключение и сеть»).";
        Equal(expectedMessage, ex.Message);
    }

    private static void RouteAttemptLimitNonRetryableInBatchTasks()
    {
        var ex = new RouteAttemptLimitException(10, 10);
        // IsConnectivityFailure must return false for RouteAttemptLimitException
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(ex));
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(new AggregateException(ex)));
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(new Exception("outer", ex)));
    }

    private static void ConnectFirstSuccessfulSharesBudgetWhenOmitted()
    {
        var config = new ManagerConfig { MaxRouteAttempts = 1 };
        var proxy = new BaseProxy { Name = "test-proxy", Host = "127.0.0.1", Port = 1, Enabled = true };
        var s1 = new ManagedServer { Name = "s1", Host = "127.0.0.1", Port = 22 };
        var s2 = new ManagedServer { Name = "s2", Host = "127.0.0.1", Port = 22 };
        var candidate1 = new RouteCandidate(proxy, [s1]);
        var candidate2 = new RouteCandidate(proxy, [s2]);

        var events = new List<SshTraceEvent>();
        var service = new SshConnectionService
        {
            Timeout = TimeSpan.FromMilliseconds(300),
            EndpointProbeTimeout = TimeSpan.FromMilliseconds(100),
            Trace = events.Add
        };

        try
        {
            service.ConnectFirstSuccessfulAsync(config, [candidate1, candidate2])
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Ожидался сбой подключения");
        }
        catch (Exception ex)
        {
            // One candidate takes the single budget attempt, the other exhausts the budget
            var starts = events.Count(e => e.Stage == SshTraceStage.RouteCandidate && e.Status == "START");
            Equal(1, starts);
            Equal(true, ex is RouteAttemptLimitException or InvalidOperationException);
        }
    }

    private static void RouteAttemptLimitWithMultipleUnavailableManagedJumphosts()
    {
        var target = new ManagedServer { Id = Guid.NewGuid(), Name = "target", Host = "10.0.0.2", Port = 22 };
        var jh1Server = new ManagedServer { Id = Guid.NewGuid(), Name = "jh1-server", Host = "127.0.0.1", Port = 22 };
        var jh2Server = new ManagedServer { Id = Guid.NewGuid(), Name = "jh2-server", Host = "127.0.0.1", Port = 22 };
        var jh3Server = new ManagedServer { Id = Guid.NewGuid(), Name = "jh3-server", Host = "127.0.0.1", Port = 22 };
        var jh1Proxy = new BaseProxy { Id = Guid.NewGuid(), Name = "jh1", Host = "127.0.0.1", Port = 5551, StartupServerId = jh1Server.Id, Enabled = true };
        var jh2Proxy = new BaseProxy { Id = Guid.NewGuid(), Name = "jh2", Host = "127.0.0.1", Port = 5552, StartupServerId = jh2Server.Id, Enabled = true };
        var jh3Proxy = new BaseProxy { Id = Guid.NewGuid(), Name = "jh3", Host = "127.0.0.1", Port = 5553, StartupServerId = jh3Server.Id, Enabled = true };

        var ssh = new SshConnectionService();

        // 1. MaxRouteAttempts = 1: only first unavailable jumphost is launched; jh2 and jh3 are never launched
        {
            var config = new ManagerConfig { MaxRouteAttempts = 1, RaceBestEntryPoints = false };
            var candidates = new List<RouteCandidate>
            {
                new(jh1Proxy, [target]),
                new(jh2Proxy, [target]),
                new(jh3Proxy, [target])
            };
            var attemptedJumphosts = new List<Guid>();
            try
            {
                RouteCandidateConnector.ConnectCandidatesAsync(
                    config, target, candidates, ssh, CancellationToken.None,
                    consoleOnly: true,
                    ensureManagedJumphost: (proxy, ct) =>
                    {
                        attemptedJumphosts.Add(proxy.Id);
                        return Task.FromResult(false);
                    },
                    isSocks5Ready: (proxy, timeout, ct) => Task.FromResult(false)
                ).GetAwaiter().GetResult();
                throw new InvalidOperationException("Ожидался сбой лимита попыток");
            }
            catch (RouteAttemptLimitException ex)
            {
                Equal(1, ex.AttemptLimit);
                Equal(1, ex.AttemptCount);
                Equal(1, attemptedJumphosts.Count);
                Equal(jh1Proxy.Id, attemptedJumphosts[0]);
            }
        }

        // 2. MaxRouteAttempts = 2: exactly two unavailable jumphosts are launched; jh3 is skipped
        {
            var config = new ManagerConfig { MaxRouteAttempts = 2, RaceBestEntryPoints = false };
            var candidates = new List<RouteCandidate>
            {
                new(jh1Proxy, [target]),
                new(jh2Proxy, [target]),
                new(jh3Proxy, [target])
            };
            var attemptedJumphosts = new List<Guid>();
            try
            {
                RouteCandidateConnector.ConnectCandidatesAsync(
                    config, target, candidates, ssh, CancellationToken.None,
                    consoleOnly: true,
                    ensureManagedJumphost: (proxy, ct) =>
                    {
                        attemptedJumphosts.Add(proxy.Id);
                        return Task.FromResult(false);
                    },
                    isSocks5Ready: (proxy, timeout, ct) => Task.FromResult(false)
                ).GetAwaiter().GetResult();
                throw new InvalidOperationException("Ожидался сбой лимита попыток");
            }
            catch (RouteAttemptLimitException ex)
            {
                Equal(2, ex.AttemptLimit);
                Equal(2, ex.AttemptCount);
                Equal(2, attemptedJumphosts.Count);
                Equal(jh1Proxy.Id, attemptedJumphosts[0]);
                Equal(jh2Proxy.Id, attemptedJumphosts[1]);
            }
        }

        // 3. Cached proxy skips do not deduct budget:
        // Candidate 1: jhA -> serverX -> target (jhA probe fails, consumes attempt 1)
        // Candidate 2: jhA -> serverY -> target (jhA cached as unavailable, skips WITHOUT deducting budget)
        // Candidate 3: jhB -> target (jhB succeeds, connects SSH with attempt 2)
        {
            using var socks = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            socks.Start();
            var socksPort = ((System.Net.IPEndPoint)socks.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var client = await socks.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                var header = new byte[4];
                await stream.ReadExactlyAsync(header);
                var addressLength = header[3] switch
                {
                    0x01 => 4,
                    0x03 => stream.ReadByte(),
                    0x04 => 16,
                    _ => throw new InvalidOperationException()
                };
                var addressAndPort = new byte[addressLength + 2];
                await stream.ReadExactlyAsync(addressAndPort);
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 22 });
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("SSH-2.0-test\r\n"));
                await stream.ReadAsync(new byte[1]);
            });

            var jhAServer = new ManagedServer { Id = Guid.NewGuid(), Name = "jhA-server", Host = "127.0.0.1", Port = 22 };
            var jhBServer = new ManagedServer { Id = Guid.NewGuid(), Name = "jhB-server", Host = "127.0.0.1", Port = 22 };
            var jhAProxy = new BaseProxy { Id = Guid.NewGuid(), Name = "jhA", Host = "127.0.0.1", Port = 5554, StartupServerId = jhAServer.Id, Enabled = true };
            var jhBProxy = new BaseProxy { Id = Guid.NewGuid(), Name = "jhB", Host = "127.0.0.1", Port = socksPort, StartupServerId = jhBServer.Id, Enabled = true };

            var serverX = new ManagedServer { Id = Guid.NewGuid(), Name = "serverX", Host = "10.0.0.3", Port = 22 };
            var serverY = new ManagedServer { Id = Guid.NewGuid(), Name = "serverY", Host = "10.0.0.4", Port = 22 };

            var config = new ManagerConfig { MaxRouteAttempts = 2, RaceBestEntryPoints = false };
            var budget = new RouteAttemptBudget(config.MaxRouteAttempts);
            var candidates = new List<RouteCandidate>
            {
                new(jhAProxy, [serverX, target]),
                new(jhAProxy, [serverY, target]),
                new(jhBProxy, [target])
            };
            var attemptedJumphosts = new List<Guid>();

            var activeRoute = RouteCandidateConnector.ConnectCandidatesAsync(
                config, target, candidates, ssh, CancellationToken.None,
                consoleOnly: true,
                budget: budget,
                ensureManagedJumphost: (proxy, ct) =>
                {
                    attemptedJumphosts.Add(proxy.Id);
                    return Task.FromResult(proxy.Id == jhBProxy.Id);
                },
                isSocks5Ready: (proxy, timeout, ct) => Task.FromResult(proxy.Id == jhBProxy.Id)
            ).GetAwaiter().GetResult();

            activeRoute.Dispose();
            serverTask.GetAwaiter().GetResult();

            // jhA was launched once (for Candidate 1)
            Equal(1, attemptedJumphosts.Count);
            Equal(jhAProxy.Id, attemptedJumphosts[0]);

            // Total budget consumed is exactly 2:
            // Attempt 1: jhA startup failure (Candidate 1)
            // Attempt 2: jhB SSH connect (Candidate 3)
            // (Candidate 2 was skipped via cached proxyReady=false without consuming budget)
            Equal(2, budget.AttemptCount);
            Equal(0, budget.Remaining);
            Equal(false, budget.HasCapacity);
        }
    }

    private static void MaxGroupServersInRouteAttemptsCandidateFiltering()
    {
        var proxy = new BaseProxy { Id = Guid.NewGuid(), Name = "SOCKS1", Enabled = true, Host = "127.0.0.1", Port = 1080 };
        var proxy2 = new BaseProxy { Id = Guid.NewGuid(), Name = "SOCKS2", Enabled = true, Host = "127.0.0.1", Port = 1081 };
        var target = new ManagedServer { Id = Guid.NewGuid(), Name = "Target", TryDirectWithoutJumphost = true };
        var groupA = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupA", Servers = [target] };

        // Group B has 15 servers
        var groupBServers = Enumerable.Range(1, 15)
            .Select(i => new ManagedServer { Id = Guid.NewGuid(), Name = $"B{i:D2}" })
            .ToList();
        var groupB = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupB", Servers = groupBServers };

        // Group C has 1 server
        var serverC1 = new ManagedServer { Id = Guid.NewGuid(), Name = "C01" };
        var groupC = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupC", Servers = [serverC1] };

        // Ungrouped server
        var ungrouped = new ManagedServer { Id = Guid.NewGuid(), Name = "U01" };

        var config = new ManagerConfig
        {
            BaseProxies = [proxy, proxy2],
            Groups = [groupA, groupB, groupC],
            UngroupedServers = [ungrouped],
            MaxGroupServersInRouteAttempts = 1
        };

        // 1. Direct candidate to target (1 hop, no intermediate server)
        var directCandidate = new RouteCandidate(RoutePlanner.DirectConnectionProxy, [target], true);

        // 2. Candidates through Group B (B1..B15)
        var candidatesB = groupBServers.Select(b => new RouteCandidate(proxy, [b, target])).ToList();

        // 3. Candidate through Group C
        var candidateC = new RouteCandidate(proxy, [serverC1, target]);

        // 4. Candidate through Ungrouped
        var candidateU = new RouteCandidate(proxy, [ungrouped, target]);

        // 5. Duplicate entry server B1 through alternative proxy
        var candidateB1Alt = new RouteCandidate(proxy2, [groupBServers[0], target]);

        // Combine: direct, B1..B15, B1Alt, C1, U1
        var allCandidates = new List<RouteCandidate> { directCandidate }
            .Concat(candidatesB)
            .Append(candidateB1Alt)
            .Append(candidateC)
            .Append(candidateU)
            .ToList();

        // Test with limit = 1:
        config.MaxGroupServersInRouteAttempts = 1;
        var filtered1 = RoutePlanner.LimitGroupEntryServers(config, allCandidates);
        // Direct route kept
        Equal(true, filtered1.Contains(directCandidate));
        // Ungrouped kept
        Equal(true, filtered1.Contains(candidateU));
        // Group C kept
        Equal(true, filtered1.Contains(candidateC));
        // Group B: B1 kept, B1Alt kept (same entry server), B2..B15 excluded
        Equal(true, filtered1.Contains(candidatesB[0]));
        Equal(true, filtered1.Contains(candidateB1Alt));
        for (var i = 1; i < 15; i++)
        {
            Equal(false, filtered1.Contains(candidatesB[i]));
        }
        // Total count: direct (1) + B1 (1) + B1Alt (1) + C1 (1) + U1 (1) = 5
        Equal(5, filtered1.Count);

        // Test with limit = 0 (disabled):
        config.MaxGroupServersInRouteAttempts = 0;
        var filtered0 = RoutePlanner.LimitGroupEntryServers(config, allCandidates);
        Equal(allCandidates.Count, filtered0.Count);

        // Test with limit = 2:
        config.MaxGroupServersInRouteAttempts = 2;
        var filtered2 = RoutePlanner.LimitGroupEntryServers(config, allCandidates);
        Equal(true, filtered2.Contains(candidatesB[0]));
        Equal(true, filtered2.Contains(candidateB1Alt));
        Equal(true, filtered2.Contains(candidatesB[1]));
        for (var i = 2; i < 15; i++)
        {
            Equal(false, filtered2.Contains(candidatesB[i]));
        }
        Equal(6, filtered2.Count);
    }

    private static void OrderPreferredHonorsGroupServerLimit()
    {
        var proxy = new BaseProxy { Id = Guid.NewGuid(), Name = "SOCKS1", Enabled = true, Host = "127.0.0.1", Port = 1080 };
        var target = new ManagedServer { Id = Guid.NewGuid(), Name = "Target", TryDirectWithoutJumphost = false };
        var groupA = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupA", Servers = [target] };

        var groupBServers = Enumerable.Range(1, 15)
            .Select(i => new ManagedServer { Id = Guid.NewGuid(), Name = $"B{i:D2}" })
            .ToList();
        var groupB = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupB", Servers = groupBServers };

        var serverC1 = new ManagedServer { Id = Guid.NewGuid(), Name = "C01" };
        var groupC = new ServerGroup { Id = Guid.NewGuid(), Name = "GroupC", Servers = [serverC1] };

        // Links from all B and C to target
        var links = groupBServers.Select(b => new ServerLink
        {
            FromServerId = b.Id,
            ToServerId = target.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        }).Append(new ServerLink
        {
            FromServerId = serverC1.Id,
            ToServerId = target.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        }).ToList();

        var config = new ManagerConfig
        {
            BaseProxies = [proxy],
            Groups = [groupA, groupB, groupC],
            Links = links,
            MaxGroupServersInRouteAttempts = 1
        };

        // Target has preferred route through B5
        var preferredB5 = groupBServers[4];
        var preferredRoute = new CachedRoute
        {
            ProxyId = proxy.Id,
            ServerIds = [preferredB5.Id, target.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };

        var ranked = RoutePlanner.Candidates(config, target.Id);
        // Ranked contains 1 direct candidate + 15 B servers + 1 C server = 17
        Equal(17, ranked.Count);

        // OrderPreferred with limit = 1:
        // preferred route B5 is put first, so B5 takes the single slot for Group B!
        // Direct candidate (1 hop) is kept without consuming group quota.
        // All other B servers are excluded. C1 is retained!
        var ordered = RoutePlanner.OrderPreferred(config, ranked, preferredRoute);
        Equal(3, ordered.Count);
        Equal(preferredB5.Id, ordered[0].Servers[0].Id);
        Equal(1, ordered.Count(c => c.Servers.Count > 1 && config.FindServerGroup(c.Servers[0].Id)?.Id == groupB.Id));
        Equal(1, ordered.Count(c => c.Servers.Count > 1 && config.FindServerGroup(c.Servers[0].Id)?.Id == groupC.Id));
        Equal(1, ordered.Count(c => c.Servers.Count == 1));

        // Without preferred route: 1 direct candidate + 1 from Group B + 1 from Group C = 3
        var orderedNoPref = RoutePlanner.OrderPreferred(config, ranked, null);
        Equal(3, orderedNoPref.Count);
        Equal(1, orderedNoPref.Count(c => c.Servers.Count > 1 && config.FindServerGroup(c.Servers[0].Id)?.Id == groupB.Id));
        Equal(1, orderedNoPref.Count(c => c.Servers.Count > 1 && config.FindServerGroup(c.Servers[0].Id)?.Id == groupC.Id));
        Equal(1, orderedNoPref.Count(c => c.Servers.Count == 1));

        // With limit = 0 (disabled), all 17 candidates are retained
        config.MaxGroupServersInRouteAttempts = 0;
        var orderedUnlimited = RoutePlanner.OrderPreferred(config, ranked, preferredRoute);
        Equal(17, orderedUnlimited.Count);
    }
}

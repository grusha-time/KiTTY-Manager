using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private static void RouteAttemptLimitDefaultsAndRoundTrip()
    {
        Equal(10, new ManagerConfig().MaxRouteAttempts);

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
            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 25 });
            Equal(25, ConfigStore.Load(path).MaxRouteAttempts);

            // Invalid / boundary values loaded through ConfigStore are normalized
            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 0 });
            Equal(10, ConfigStore.Load(path).MaxRouteAttempts);

            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 150 });
            Equal(100, ConfigStore.Load(path).MaxRouteAttempts);
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
}

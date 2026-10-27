using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using KiTTYManager.App;
using KiTTYManager.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

internal sealed partial class SelfTestRunner
{
    private static void TunnelDefinitionAndPolicyValidation()
    {
        var serverId = Guid.NewGuid();

        // 1. Missing server ID
        var emptyServer = new TunnelDefinition
        {
            ServerId = Guid.Empty,
            BindPort = 8080,
            DestinationHost = "10.0.0.1",
            DestinationPort = 80
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(emptyServer));

        // 2. Empty bind host
        var emptyBindHost = new TunnelDefinition
        {
            ServerId = serverId,
            BindHost = "   ",
            BindPort = 8080,
            DestinationHost = "10.0.0.1",
            DestinationPort = 80
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(emptyBindHost));

        // 3. Invalid bind port
        var zeroBindPort = new TunnelDefinition
        {
            ServerId = serverId,
            BindHost = "127.0.0.1",
            BindPort = 0,
            DestinationHost = "10.0.0.1",
            DestinationPort = 80
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(zeroBindPort));

        var outOfRangeBindPort = new TunnelDefinition
        {
            ServerId = serverId,
            BindHost = "127.0.0.1",
            BindPort = 70000,
            DestinationHost = "10.0.0.1",
            DestinationPort = 80
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(outOfRangeBindPort));

        // 4. Local tunnel requires destination host and port
        var localNoDest = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Local,
            BindHost = "127.0.0.1",
            BindPort = 8080,
            DestinationHost = "",
            DestinationPort = 80
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(localNoDest));

        var localBadDestPort = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Local,
            BindHost = "127.0.0.1",
            BindPort = 8080,
            DestinationHost = "10.0.0.1",
            DestinationPort = 0
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(localBadDestPort));

        // 5. Remote tunnel requires destination host and port
        var remoteNoDest = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Remote,
            BindHost = "127.0.0.1",
            BindPort = 9000,
            DestinationHost = "   ",
            DestinationPort = 90
        };
        AssertThrows<InvalidDataException>(() => TunnelPolicy.Validate(remoteNoDest));

        // 6. Dynamic tunnel does NOT require destination host or port
        var dynamicValid = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1080,
            DestinationHost = "",
            DestinationPort = 0
        };
        TunnelPolicy.Validate(dynamicValid); // Should not throw

        Equal("Dynamic (-D / SOCKS)", dynamicValid.KindDisplay);
        Equal("127.0.0.1:1080 (SOCKS)", dynamicValid.Summary);

        // 7. Valid Local and Remote display
        var localValid = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Local,
            BindHost = "127.0.0.1",
            BindPort = 8080,
            DestinationHost = "internal.host",
            DestinationPort = 80
        };
        TunnelPolicy.Validate(localValid);
        Equal("Local (-L)", localValid.KindDisplay);
        Equal("127.0.0.1:8080 → internal.host:80", localValid.Summary);

        var remoteValid = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Remote,
            BindHost = "0.0.0.0",
            BindPort = 9090,
            DestinationHost = "127.0.0.1",
            DestinationPort = 3000
        };
        TunnelPolicy.Validate(remoteValid);
        Equal("Remote (-R)", remoteValid.KindDisplay);
        Equal("0.0.0.0:9090 → 127.0.0.1:3000", remoteValid.Summary);
    }

    private static void TunnelPortFactoryCreatesCorrectPortTypes()
    {
        var serverId = Guid.NewGuid();

        // Local
        var localDef = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Local,
            BindHost = "127.0.0.1",
            BindPort = 8443,
            DestinationHost = "10.0.1.5",
            DestinationPort = 443
        };
        var localPort = TunnelPortFactory.Create(localDef);
        Equal(true, localPort is ForwardedPortLocal);
        var fpl = (ForwardedPortLocal)localPort;
        Equal("127.0.0.1", fpl.BoundHost);
        Equal(8443u, fpl.BoundPort);
        Equal("10.0.1.5", fpl.Host);
        Equal(443u, fpl.Port);

        // Remote
        var remoteDef = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Remote,
            BindHost = "127.0.0.1",
            BindPort = 9000,
            DestinationHost = "192.168.1.50",
            DestinationPort = 22
        };
        var remotePort = TunnelPortFactory.Create(remoteDef);
        Equal(true, remotePort is ForwardedPortRemote);
        var fpr = (ForwardedPortRemote)remoteDef.CreateForwardedPort();
        Equal("127.0.0.1", fpr.BoundHost);
        Equal(9000u, fpr.BoundPort);
        Equal("192.168.1.50", fpr.Host);
        Equal(22u, fpr.Port);

        // Dynamic
        var dynamicDef = new TunnelDefinition
        {
            ServerId = serverId,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1085
        };
        var dynamicPort = TunnelPortFactory.Create(dynamicDef);
        Equal(true, dynamicPort is ForwardedPortDynamic);
        var fpd = (ForwardedPortDynamic)dynamicPort;
        Equal("127.0.0.1", fpd.BoundHost);
        Equal(1085u, fpd.BoundPort);
    }

    private static void TunnelServiceStateTrackingAndErrorHandling()
    {
        var releasedRoutes = new List<ActiveRoute>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-Tunnel" };

        // Test route with null client throws expected exception
        var routeWithNullClient = new ActiveRoute(server, 2222, 0, "direct", null, [], []);

        var service = new TunnelService(
            openRoute: (_, _) => Task.FromResult(routeWithNullClient),
            releaseRoute: r => releasedRoutes.Add(r));

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1099
        };

        var thrown = false;
        try
        {
            service.StartTunnelAsync(def).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        Equal(true, thrown);
        Equal(1, releasedRoutes.Count);
        Equal(true, ReferenceEquals(routeWithNullClient, releasedRoutes[0]));

        // Check tunnel list after failed start
        var tunnels = service.GetTunnels();
        Equal(1, tunnels.Count);
        Equal(false, tunnels[0].IsRunning);
        Equal(true, tunnels[0].Status.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase));
        Equal(false, service.HasActiveTunnels());

        // Remove from list
        service.RemoveTunnelFromList(tunnels[0].Id);
        Equal(0, service.GetTunnels().Count);

        service.Dispose();
    }

    private static void TunnelHelpContentSearchVerification()
    {
        var entries = HelpContent.Search("туннель");
        Equal(true, entries.Any(e => e.Section == "Сессия" && e.Title.Contains("Прокинуть туннель")));

        var socksEntries = HelpContent.Search("SOCKS");
        Equal(true, socksEntries.Any(e => e.Section == "Сессия" && e.Title.Contains("Прокинуть туннель")));

        var localEntries = HelpContent.Search("Local (-L)");
        Equal(true, localEntries.Any(e => e.Section == "Сессия"));
    }

    private static void TunnelServiceStopDuringStartupCleansUpProperly()
    {
        var releasedRoutes = new List<ActiveRoute>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-StopStartup" };
        var routeTcs = new TaskCompletionSource<ActiveRoute>();
        var startupEnteredTcs = new TaskCompletionSource<bool>();
        var logMessages = new List<string>();

        var service = new TunnelService(
            openRoute: async (_, ct) =>
            {
                startupEnteredTcs.TrySetResult(true);
                using (ct.Register(() => routeTcs.TrySetCanceled()))
                {
                    return await routeTcs.Task;
                }
            },
            releaseRoute: r => releasedRoutes.Add(r),
            log: msg => logMessages.Add(msg));

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1098
        };

        var startTask = Task.Run(async () =>
        {
            try
            {
                return await service.StartTunnelAsync(def);
            }
            catch
            {
                return null;
            }
        });

        startupEnteredTcs.Task.GetAwaiter().GetResult();
        Equal(true, service.HasActiveTunnels());

        service.StopTunnelAsync(def.Id).GetAwaiter().GetResult();
        startTask.GetAwaiter().GetResult();

        Equal(false, service.HasActiveTunnels());
        Equal(0, service.GetTunnels().Count);
        Equal(true, logMessages.Count > 0);

        service.Dispose();
    }

    private static void TunnelServiceErrorStatusRetainsActiveResourceProtection()
    {
        var releasedRoutes = new List<ActiveRoute>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-ErrorResource" };
        var route = new ActiveRoute(server, 2222, 0, "direct", null, [], []);

        var service = new TunnelService(
            openRoute: (_, _) => Task.FromResult(route),
            releaseRoute: r => releasedRoutes.Add(r));

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1097
        };

        var item = new ActiveTunnelItem(def)
        {
            Route = route,
            Status = "Ошибка туннеля: Соединение сброшено"
        };
        service.AddActiveTunnelItem(item);

        // Even though Status is error and IsRunning is false,
        // HasActiveResources is true because Route is held
        Equal(false, item.IsRunning);
        Equal(true, item.HasActiveResources);
        Equal(true, service.HasActiveTunnels());

        // StopAllAsync must clean up despite error status
        service.StopAllAsync().GetAwaiter().GetResult();
        Equal(false, item.HasActiveResources);
        Equal(false, service.HasActiveTunnels());
        Equal(0, service.GetTunnels().Count);
        Equal(1, releasedRoutes.Count);
        Equal(true, ReferenceEquals(route, releasedRoutes[0]));

        service.Dispose();
    }

    private static void TunnelServiceSynchronousStopAllReleasesResourcesWithoutBlocking()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        var releaseThreadIds = new List<int>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-SyncStop" };
        var route1 = new ActiveRoute(server, 2222, 0, "direct", null, [], []);
        var route2 = new ActiveRoute(server, 2223, 0, "direct", null, [], []);
        var activeRoutesSet = new HashSet<ActiveRoute> { route1, route2 };
        var testLock = new object();

        var service = new TunnelService(
            openRoute: (_, _) => Task.FromResult(route1),
            releaseRoute: r =>
            {
                lock (testLock)
                {
                    releaseThreadIds.Add(Environment.CurrentManagedThreadId);
                    activeRoutesSet.Remove(r);
                }
                r.Dispose();
            });

        var def1 = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1095
        };

        var def2 = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1096
        };

        var item1 = new ActiveTunnelItem(def1) { Route = route1, Status = "Работает" };
        var item2 = new ActiveTunnelItem(def2) { Route = route2, Status = "Работает" };

        service.AddActiveTunnelItem(item1);
        service.AddActiveTunnelItem(item2);

        Equal(2, service.GetTunnels().Count);
        Equal(true, service.HasActiveTunnels());

        var stopTask = service.StopAllAsync();
        stopTask.GetAwaiter().GetResult();

        Equal(0, service.GetTunnels().Count);
        Equal(false, service.HasActiveTunnels());
        Equal(0, activeRoutesSet.Count);
        Equal(2, releaseThreadIds.Count);
        Equal(true, releaseThreadIds.All(id => id != callingThreadId));
        Equal("Остановлен", item1.Status);
        Equal("Остановлен", item2.Status);

        service.Dispose();
    }

    private static void TunnelServiceAutoReconnectLifecycle()
    {
        var releasedRoutes = new List<ActiveRoute>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-AutoReconnect" };
        using var dummyClient = new SshClient("127.0.0.1", 2222, "user", "pass");

        int openRouteCount = 0;
        bool shouldFailRoute = false;
        var openRouteTcs = new TaskCompletionSource<bool>();
        var reconnectAttemptTcs = new TaskCompletionSource<bool>();

        var service = new TunnelService(
            openRoute: (_, _) =>
            {
                var count = Interlocked.Increment(ref openRouteCount);
                if (count == 1)
                {
                    openRouteTcs.TrySetResult(true);
                }
                else
                {
                    reconnectAttemptTcs.TrySetResult(true);
                }

                if (shouldFailRoute)
                {
                    throw new SocketException((int)SocketError.ConnectionRefused);
                }

                var r = new ActiveRoute(server, 2222, 0, "direct", dummyClient, [], []);
                return Task.FromResult(r);
            },
            releaseRoute: r =>
            {
                lock (releasedRoutes)
                {
                    releasedRoutes.Add(r);
                }
            })
        {
            ReconnectRetryDelay = TimeSpan.FromMilliseconds(30),
            HealthCheckInterval = TimeSpan.FromMilliseconds(10),
            RoutePortAttacher = (_, _) => { },
            PortStarter = _ => { }
        };

        bool isClientHealthy = true;
        service.HealthCheckProbe = _ => isClientHealthy;

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1093
        };

        // 1. Start tunnel with externalToken
        using var externalCts = new CancellationTokenSource();
        var item = service.StartTunnelAsync(def, externalCts.Token).GetAwaiter().GetResult();
        Equal("Работает", item.Status);
        Equal(true, item.IsRunning);
        Equal(true, item.HasActiveResources);
        Equal(true, service.HasActiveTunnels());
        Equal(1, openRouteCount);

        // Cancel externalToken after start - supervisor must NOT terminate
        externalCts.Cancel();

        // 2. Configure next route attempt to fail, so we observe "Ошибка: ..."
        shouldFailRoute = true;
        reconnectAttemptTcs = new TaskCompletionSource<bool>();
        service.SimulateTunnelFault(item.Id, "Соединение разорвано");

        // Verify reconnect attempt TCS returned true
        Equal(true, reconnectAttemptTcs.Task.Wait(TimeSpan.FromSeconds(3)));
        Equal(true, openRouteCount >= 2);

        // Wait for failure handling to update status to "Ошибка: ..."
        var sw = Stopwatch.StartNew();
        while (!item.Status.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase) && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }
        Equal(true, item.Status.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase));

        // 3. Verify HasActiveTunnels() remains true during recovery
        Equal(true, item.HasActiveResources);
        Equal(true, service.HasActiveTunnels());
        Equal(false, item.IsRunning);

        // 4. Now let route succeed on next retry
        shouldFailRoute = false;
        var prevCount = openRouteCount;

        sw.Restart();
        while (item.Status != "Работает" && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }

        Equal("Работает", item.Status);
        Equal(true, item.IsRunning);
        Equal(true, item.HasActiveResources);
        Equal(true, service.HasActiveTunnels());
        Equal(true, openRouteCount > prevCount);

        // 5. Test health check probe trigger
        reconnectAttemptTcs = new TaskCompletionSource<bool>();
        prevCount = openRouteCount;
        isClientHealthy = false;

        Equal(true, reconnectAttemptTcs.Task.Wait(TimeSpan.FromSeconds(3)));
        Equal(true, openRouteCount > prevCount);
        isClientHealthy = true;

        sw.Restart();
        while (item.Status != "Работает" && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }
        Equal("Работает", item.Status);

        // 6. StopTunnelAsync cleanly stops recovery and releases resources
        service.StopTunnelAsync(item.Id).GetAwaiter().GetResult();
        Equal("Остановлен", item.Status);
        Equal(false, item.IsRunning);
        Equal(false, item.HasActiveResources);
        Equal(false, service.HasActiveTunnels());
        Equal(0, service.GetTunnels().Count);

        service.Dispose();
    }

    private static void TunnelServiceTransientConnectionErrorsDoNotFaultTunnel()
    {
        var logs = new List<string>();
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-TransientTest" };
        using var dummyClient = new SshClient("127.0.0.1", 2222, "user", "pass");

        int openRouteCount = 0;
        var releasedRoutes = new List<ActiveRoute>();
        bool isHealthy = true;
        TaskCompletionSource<bool>? reconnectAttemptTcs = null;

        var service = new TunnelService(
            openRoute: (_, _) =>
            {
                Interlocked.Increment(ref openRouteCount);
                reconnectAttemptTcs?.TrySetResult(true);
                return Task.FromResult(new ActiveRoute(server, 2222, 0, "direct", dummyClient, [], []));
            },
            releaseRoute: r =>
            {
                lock (releasedRoutes)
                {
                    releasedRoutes.Add(r);
                }
            },
            log: msg =>
            {
                lock (logs)
                {
                    logs.Add(msg);
                }
            })
        {
            ReconnectRetryDelay = TimeSpan.FromMilliseconds(30),
            HealthCheckInterval = TimeSpan.FromSeconds(5),
            RoutePortAttacher = (_, _) => { },
            PortStarter = _ => { }
        };

        service.HealthCheckProbe = _ => isHealthy;

        // Verify direct IsTunnelHealthy behavior
        var mockItem = new ActiveTunnelItem(new TunnelDefinition { ServerId = server.Id, BindPort = 1234 });
        Equal(true, service.IsTunnelHealthy(mockItem));
        isHealthy = false;
        Equal(false, service.IsTunnelHealthy(mockItem));
        service.HealthCheckProbe = null;
        Equal(false, service.IsTunnelHealthy(mockItem));
        service.HealthCheckProbe = _ => isHealthy;
        isHealthy = true;

        // a) Healthy tunnel: Dynamic, Local, Remote
        var kinds = new[] { TunnelKind.Dynamic, TunnelKind.Local, TunnelKind.Remote };
        int bindPort = 2000;

        foreach (var kind in kinds)
        {
            var def = new TunnelDefinition
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Kind = kind,
                BindHost = "127.0.0.1",
                BindPort = bindPort++,
                DestinationHost = kind == TunnelKind.Dynamic ? "" : "192.168.1.100",
                DestinationPort = kind == TunnelKind.Dynamic ? 0 : 8080
            };

            var item = service.StartTunnelAsync(def).GetAwaiter().GetResult();
            Equal("Работает", item.Status);
            Equal(true, item.IsRunning);
            Equal(true, service.GetTunnels().Any(t => t.Id == item.Id && t.IsRunning));

            var faultTcs = item.CurrentFaultTcs;
            Equal(true, faultTcs != null);
            Equal(false, faultTcs!.Task.IsCompleted);

            int currentRouteCount = openRouteCount;

            // Simulate SshOperationTimeoutException
            var timeoutEx = new SshOperationTimeoutException("Session operation has timed out");
            service.HandlePortException(item, faultTcs, timeoutEx);

            Equal("Работает", item.Status);
            Equal(true, item.IsRunning);
            Equal(false, faultTcs.Task.IsCompleted);
            Equal(currentRouteCount, openRouteCount);
            lock (logs)
            {
                Equal(true, logs.Any(l => l.Contains("временная ошибка соединения — Session operation has timed out") && l.Contains(item.Summary)));
            }

            // Simulate SocketException
            var socketEx = new SocketException((int)SocketError.TimedOut);
            service.HandlePortException(item, faultTcs, socketEx);

            Equal("Работает", item.Status);
            Equal(true, item.IsRunning);
            Equal(false, faultTcs.Task.IsCompleted);
            Equal(currentRouteCount, openRouteCount);
            lock (logs)
            {
                Equal(true, logs.Any(l => l.Contains("временная ошибка соединения") && l.Contains(socketEx.Message) && l.Contains(item.Summary)));
            }

            // Clean stop before next kind
            service.StopTunnelAsync(item.Id).GetAwaiter().GetResult();
            service.RemoveTunnelFromList(item.Id);
            Equal(false, item.IsRunning);
        }

        // b) Unhealthy tunnel: when IsTunnelHealthy returns false, HandlePortException triggers faultTcs
        var unhealthyDef = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = bindPort++,
            DestinationHost = "",
            DestinationPort = 0
        };

        var unhealthyItem = service.StartTunnelAsync(unhealthyDef).GetAwaiter().GetResult();
        Equal("Работает", unhealthyItem.Status);
        Equal(true, unhealthyItem.IsRunning);

        var preFaultTcs = unhealthyItem.CurrentFaultTcs;
        Equal(true, preFaultTcs != null);
        Equal(false, preFaultTcs!.Task.IsCompleted);

        var oldPortCallback = service.LastPortCallback;
        Equal(true, oldPortCallback != null);

        // Make tunnel unhealthy
        isHealthy = false;
        int routeCountBeforeFault = openRouteCount;
        reconnectAttemptTcs = new TaskCompletionSource<bool>();

        var faultEx = new SshOperationTimeoutException("Session operation has timed out on unhealthy connection");
        service.HandlePortException(unhealthyItem, preFaultTcs, faultEx);

        // faultTcs must be triggered immediately
        Equal(true, preFaultTcs.Task.IsCompleted);
        lock (logs)
        {
            Equal(true, logs.Any(l => l.Contains("Ошибка туннеля: Session operation has timed out on unhealthy connection")));
        }

        // Wait until recovery reconnect attempt begins
        Equal(true, reconnectAttemptTcs.Task.Wait(TimeSpan.FromSeconds(3)));
        Equal(true, openRouteCount > routeCountBeforeFault);

        // Allow recovery to complete by making probe healthy again
        isHealthy = true;

        var sw = Stopwatch.StartNew();
        while (unhealthyItem.Status != "Работает" && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }

        Equal("Работает", unhealthyItem.Status);
        Equal(true, unhealthyItem.IsRunning);

        // c) Isolation: stale callback with old faultTcs after recovery does not fault new connection
        var recoveredFaultTcs = unhealthyItem.CurrentFaultTcs;
        Equal(true, recoveredFaultTcs != null);
        Equal(false, ReferenceEquals(preFaultTcs, recoveredFaultTcs));
        Equal(false, recoveredFaultTcs!.Task.IsCompleted);

        int routeCountAfterRecovery = openRouteCount;

        isHealthy = false;
        oldPortCallback!(new Exception("Поздняя ошибка старого порта"));
        isHealthy = true;

        // New connection TCS must NOT be faulted
        Equal(false, recoveredFaultTcs.Task.IsCompleted);
        Equal("Работает", unhealthyItem.Status);
        Equal(true, unhealthyItem.IsRunning);
        Equal(routeCountAfterRecovery, openRouteCount);

        service.StopTunnelAsync(unhealthyItem.Id).GetAwaiter().GetResult();
        service.Dispose();
    }

    private static void TunnelServiceRemoveTunnelStopsRecoveryCleanly()
    {
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-RemoveSync" };
        using var dummyClient = new SshClient("127.0.0.1", 2222, "user", "pass");

        var reconnectAttemptTcs = new TaskCompletionSource<bool>();
        bool shouldFail = false;

        var service = new TunnelService(
            openRoute: (_, _) =>
            {
                if (shouldFail)
                {
                    reconnectAttemptTcs.TrySetResult(true);
                    throw new SocketException((int)SocketError.ConnectionRefused);
                }
                return Task.FromResult(new ActiveRoute(server, 2222, 0, "direct", dummyClient, [], []));
            },
            releaseRoute: _ => { })
        {
            ReconnectRetryDelay = TimeSpan.FromMilliseconds(50),
            HealthCheckInterval = TimeSpan.FromMilliseconds(10),
            RoutePortAttacher = (_, _) => { },
            PortStarter = _ => { }
        };

        var def1 = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1091
        };

        var def2 = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1092
        };

        // Test RemoveTunnelFromList during recovery
        var item1 = service.StartTunnelAsync(def1).GetAwaiter().GetResult();
        shouldFail = true;
        service.SimulateTunnelFault(item1.Id, "Обрыв");
        Equal(true, reconnectAttemptTcs.Task.Wait(TimeSpan.FromSeconds(3)));

        service.RemoveTunnelFromList(item1.Id);
        var sw = Stopwatch.StartNew();
        while ((service.GetTunnels().Count > 0 || item1.Status != "Остановлен") && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }
        Equal(0, service.GetTunnels().Count);
        Equal(false, service.HasActiveTunnels());
        Equal("Остановлен", item1.Status);

        // Test RemoveTunnelAsync on active tunnel
        shouldFail = false;
        var item2 = service.StartTunnelAsync(def2).GetAwaiter().GetResult();
        Equal("Работает", item2.Status);
        service.RemoveTunnelAsync(item2.Id).GetAwaiter().GetResult();
        Equal(0, service.GetTunnels().Count);
        Equal(false, service.HasActiveTunnels());
        Equal("Остановлен", item2.Status);

        service.Dispose();
    }

    private static void TunnelServiceRemoveTunnelDoesNotDeadlockWithSyncContext()
    {
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-SyncContext" };
        using var dummyClient = new SshClient("127.0.0.1", 2222, "user", "pass");

        using var syncContext = new SingleThreadSynchronizationContext();

        bool releaseRouteExecuted = false;
        var releaseEvent = new ManualResetEventSlim(false);

        var service = new TunnelService(
            openRoute: (_, _) => Task.FromResult(new ActiveRoute(server, 2222, 0, "direct", dummyClient, [], [])),
            releaseRoute: _ =>
            {
                // Simulate UI dispatch (e.g. Application.Current.Dispatcher.Invoke or syncContext.Send)
                syncContext.Send(_ =>
                {
                    releaseRouteExecuted = true;
                }, null);
                releaseEvent.Set();
            })
        {
            ReconnectRetryDelay = TimeSpan.FromMilliseconds(50),
            HealthCheckInterval = TimeSpan.FromMilliseconds(10),
            RoutePortAttacher = (_, _) => { },
            PortStarter = _ => { }
        };

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1093
        };

        var item = service.StartTunnelAsync(def).GetAwaiter().GetResult();
        Equal("Работает", item.Status);

        // Call RemoveTunnelFromList from simulated UI thread
        syncContext.Send(_ =>
        {
            service.RemoveTunnelFromList(item.Id);
        }, null);

        Equal(true, releaseEvent.Wait(TimeSpan.FromSeconds(3)));
        Equal(true, releaseRouteExecuted);

        var sw = Stopwatch.StartNew();
        while (service.GetTunnels().Count > 0 && sw.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(10);
        }
        Equal(0, service.GetTunnels().Count);
        Equal(false, service.HasActiveTunnels());

        service.Dispose();
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new();
        private readonly Thread thread;

        public SingleThreadSynchronizationContext()
        {
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            try
            {
                foreach (var action in queue.GetConsumingEnumerable())
                {
                    action();
                }
            }
            catch (ObjectDisposedException) { }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.Add(() => d(state));
            }
            catch (InvalidOperationException) { }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == thread)
            {
                d(state);
                return;
            }

            using var mres = new ManualResetEventSlim(false);
            Exception? error = null;
            try
            {
                queue.Add(() =>
                {
                    try { d(state); }
                    catch (Exception ex) { error = ex; }
                    finally { mres.Set(); }
                });
            }
            catch (InvalidOperationException)
            {
                return;
            }

            mres.Wait();
            if (error != null)
                throw new InvalidOperationException("SyncContext invocation error", error);
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join(TimeSpan.FromSeconds(2));
            queue.Dispose();
        }
    }

    private static void TunnelServiceStopDuringRecoveryPreservesLifecycleLock()
    {
        var server = new ManagedServer { Id = Guid.NewGuid(), Name = "Server-StopRecoveryLock" };
        using var dummyClient = new SshClient("127.0.0.1", 2222, "user", "pass");

        var portStarterEntered = new ManualResetEventSlim(false);
        var stopTriggered = new ManualResetEventSlim(false);
        bool inRecovery = false;
        ForwardedPort? observedPortDuringHook = null;
        bool portWasNullDuringHook = false;
        int releaseRouteCount = 0;

        ActiveTunnelItem? item = null;

        var service = new TunnelService(
            openRoute: (_, _) => Task.FromResult(new ActiveRoute(server, 2222, 0, "direct", dummyClient, [], [])),
            releaseRoute: _ => Interlocked.Increment(ref releaseRouteCount))
        {
            ReconnectRetryDelay = TimeSpan.FromMilliseconds(50),
            HealthCheckInterval = TimeSpan.FromMilliseconds(10),
            RoutePortAttacher = (_, _) => { },
            PortStarter = port =>
            {
                if (!inRecovery)
                {
                    return;
                }

                observedPortDuringHook = port;
                portStarterEntered.Set();
                // Wait until stop has been initiated from the outside
                stopTriggered.Wait(TimeSpan.FromSeconds(3));
                // Give background tasks a moment to see if premature cleanup runs
                Thread.Sleep(50);
                if (item != null && item.Port == null)
                {
                    portWasNullDuringHook = true;
                }
            }
        };

        var def = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = TunnelKind.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = 1094
        };

        item = service.StartTunnelAsync(def).GetAwaiter().GetResult();
        Equal("Работает", item.Status);

        inRecovery = true;
        service.SimulateTunnelFault(item.Id, "Сбой для проверки блокировки");

        // Wait until recovery reaches PortStarter under LifecycleLock
        Equal(true, portStarterEntered.Wait(TimeSpan.FromSeconds(3)));

        // Initiate StopTunnelAsync while PortStarter is still executing under LifecycleLock
        var stopTask = service.StopTunnelAsync(item.Id);

        // Signal PortStarter to proceed after stop was requested
        stopTriggered.Set();

        // Wait for StopTunnelAsync to complete
        stopTask.GetAwaiter().GetResult();

        // Port must NOT have been nulled out or cleaned up while PortStarter was inside LifecycleLock
        Equal(false, portWasNullDuringHook);
        Equal(true, observedPortDuringHook != null);

        // After StopTunnelAsync completes, resources must be stopped and cleared
        Equal(0, service.GetTunnels().Count);
        Equal(false, service.HasActiveTunnels());
        Equal("Остановлен", item.Status);
        Equal(null, item.Port);
        Equal(null, item.Route);
        Equal(true, releaseRouteCount > 0);

        service.Dispose();
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected exception of type {typeof(TException).Name} was not thrown.");
        }
        catch (TException)
        {
            // Expected
        }
    }
}

internal static class TunnelTestExtensions
{
    public static ForwardedPort CreateForwardedPort(this TunnelDefinition def) =>
        TunnelPortFactory.Create(def);
}

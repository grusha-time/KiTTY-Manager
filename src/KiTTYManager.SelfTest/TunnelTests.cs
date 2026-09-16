using KiTTYManager.App;
using KiTTYManager.Core;
using Renci.SshNet;

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

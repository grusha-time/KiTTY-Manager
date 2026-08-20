using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KiTTYManager.Core;

public enum SshTraceStage
{
    RouteCandidate,
    ProxyConnect,
    SourceAuthentication,
    ChannelForward,
    RemoteCommandFallback,
    PrivilegedCommandFallback,
    TargetAuthentication,
    HostKey,
    RouteReady
}

public sealed record SshTraceEvent(
    SshTraceStage Stage, string Subject, string Status, Exception? Error = null);

public static class SshRouteStrategy
{
    public const string DirectTcpIp = "direct-tcpip";
    public const string RemoteCommand = "remote-command";
    public const string SuRemoteCommand = "su-remote-command";

    public static IReadOnlyList<string> Order(bool hasPrivilegeFallback) => hasPrivilegeFallback
        ? [DirectTcpIp, RemoteCommand, SuRemoteCommand]
        : [DirectTcpIp, RemoteCommand];
}

public sealed class ActiveRoute : IDisposable
{
    private readonly List<IDisposable> resources;
    public ManagedServer Target { get; }
    public int LocalSshPort { get; }
    public int LocalSocksPort { get; }
    public string Strategy { get; }
    public IReadOnlyList<RouteHop> Hops { get; }
    public bool CanDetachDirectConsole { get; }
    internal SshClient? Client { get; }
    internal ActiveRoute(ManagedServer target, int sshPort, int socksPort, string strategy,
        SshClient? client, List<IDisposable> resources, IReadOnlyList<RouteHop> hops,
        bool canDetachDirectConsole = false) =>
        (Target, LocalSshPort, LocalSocksPort, Strategy, Client, this.resources, Hops,
            CanDetachDirectConsole) =
            (target, sshPort, socksPort, strategy, client, resources, hops,
                canDetachDirectConsole);

    public void Dispose()
    {
        for (var i = resources.Count - 1; i >= 0; i--) try { resources[i].Dispose(); } catch { }
    }
}

public sealed class SshConnectionService
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan EndpointProbeTimeout { get; set; } = TimeSpan.FromSeconds(4);
    public Func<ManagedServer, string, bool>? HostKeyVerifier { get; set; }
    public Func<ManagedServer, string, string, bool>? HostKeyMismatchVerifier { get; set; }
    public Action<SshTraceEvent>? Trace { get; set; }

    // Неудача конкретного прямого маршрута не должна блокировать обходной
    // маршрут через тот же SOCKS: таймаут цели не означает таймаут jumphost.
    private readonly RouteFailureCache failureCache = new();
    private readonly EndpointFailureCache endpointFailureCache = new();
    // Предел зонда достижимости одного адреса при выборе ip:port. Короче полного
    // таймаута попытки, чтобы «молчащий» основной порт не съедал весь бюджет и
    // резервный адрес успевал провериться (иначе при таймауте основного резервный
    // на первом хопе/в цепочке подключения никогда не пробовался). При ложном
    // срабатывании зонда выбор всё равно возвращается к основному адресу, поэтому
    // рабочий, но медленный основной порт не теряется.
    public void ClearFailureCache()
    {
        failureCache.Clear();
        endpointFailureCache.Clear();
    }

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectBestAsync(
        ManagerConfig config, Guid targetId, CancellationToken cancellationToken = default, bool consoleOnly = false)
    {
        var ranked = RoutePlanner.Candidates(config, targetId);
        var target = config.FindServer(targetId);
        var ordered = RoutePlanner.OrderPreferred(config, ranked, target?.PreferredRoute);
        return await ConnectCandidatesAsync(config, ordered,
            "Не найден рабочий маршрут. Запустите ручную проверку связности.", cancellationToken, consoleOnly);
    }

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectBestUsingAsync(
        ManagerConfig config, Guid targetId, IEnumerable<Guid> proxyIds,
        CancellationToken cancellationToken = default, bool consoleOnly = false)
    {
        var allowed = proxyIds.ToHashSet();
        return await ConnectCandidatesAsync(
            config, RoutePlanner.Candidates(config, targetId).Where(candidate => allowed.Contains(candidate.Proxy.Id)),
            "Ни одна из готовых точек входа не дала рабочий маршрут.", cancellationToken, consoleOnly);
    }

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectBestViaAsync(
        ManagerConfig config, Guid sourceId, Guid targetId, CancellationToken cancellationToken = default)
        => await ConnectCandidatesAsync(
            config, RoutePlanner.ViaCandidates(config, sourceId, targetId),
            "Не удалось подключиться к целевому серверу через выбранный исходный сервер.", cancellationToken, false);

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectUsingFinalHopAsync(
        ManagerConfig config, Guid viaServerId, Guid targetId,
        CancellationToken cancellationToken = default, bool consoleOnly = false)
        => await ConnectCandidatesAsync(
            config, RoutePlanner.ForcedFinalHopCandidates(config, viaServerId, targetId),
            "Не удалось подключиться через выбранную сохранённую связь.",
            cancellationToken, consoleOnly, rememberTargetPreference: false);

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectViaAsync(
        ManagerConfig config, Guid sourceId, Guid targetId, BaseProxy proxy,
        CancellationToken cancellationToken = default)
    {
        var candidate = RoutePlanner.ViaCandidates(config, sourceId, targetId)
            .FirstOrDefault(item => item.Proxy.Id == proxy.Id)
            ?? throw new InvalidOperationException("Для указанного SOCKS отсутствует маршрут через выбранную пару.");
        return await ConnectCandidatesAsync(config, [candidate],
            "Не удалось подключиться через указанный SOCKS.", cancellationToken, false);
    }

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectCandidateAsync(
        ManagerConfig config, RouteCandidate candidate,
        CancellationToken cancellationToken = default, bool consoleOnly = false,
        bool rememberTargetPreference = true) =>
        await ConnectCandidatesAsync(config, [candidate],
            "Указанный маршрут недоступен.", cancellationToken, consoleOnly,
            rememberTargetPreference);

    public async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectFirstSuccessfulAsync(
        ManagerConfig config, IReadOnlyList<RouteCandidate> candidates,
        CancellationToken cancellationToken = default, bool consoleOnly = false)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не указаны маршруты для параллельной проверки.");

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = candidates.Select(candidate => ConnectCandidateAsync(
            config, candidate, raceCancellation.Token, consoleOnly)).ToList();
        var failures = new List<Exception>();
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completed);
            try
            {
                var result = await completed.ConfigureAwait(false);
                raceCancellation.Cancel();
                foreach (var pending in tasks)
                    _ = DisposeLateRouteAsync(pending);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(ex); }
        }

        throw new InvalidOperationException(
            "Ни один из параллельно проверенных маршрутов не сработал.",
            new AggregateException(failures));

        static async Task DisposeLateRouteAsync(
            Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> pending)
        {
            try { (await pending.ConfigureAwait(false)).Route.Dispose(); }
            catch { }
        }
    }

    private async Task<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)> ConnectCandidatesAsync(
        ManagerConfig config, IEnumerable<RouteCandidate> candidates, string failureMessage,
        CancellationToken cancellationToken, bool consoleOnly,
        bool rememberTargetPreference = true)
    {
        var failures = new List<Exception>();
        var candidateList = candidates
            .Where(candidate => RoutePlanner.SatisfiesRouteConstraints(config, candidate))
            .ToArray();
        if (candidateList.Length == 0)
            Emit(SshTraceStage.RouteCandidate, "route", "FAIL",
                new InvalidOperationException("No enabled global SOCKS route candidates."));
        foreach (var candidate in candidateList)
        {
            // Кэш: пропускаем недавно провалившиеся пары (proxy, server)
            // только для прямых маршрутов — multi-hop неудачи не кэшируем,
            // т.к. причина может быть в промежуточном сервере, а не в proxy.
            if (failureCache.ShouldSkip(candidate, DateTimeOffset.UtcNow))
            {
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "SKIP_CACHED_FAILURE");
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CreateAttemptCancellation(cancellationToken);
            var sw = Stopwatch.StartNew();
            var originalHostKeys = candidate.Servers
                .Select(server => (Server: server, Fingerprint: server.HostKeyFingerprint))
                .ToArray();
            Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "START");
            try
            {
                var route = await Task.Run(() => Connect(candidate, true, attempt.Token, consoleOnly), attempt.Token)
                    .ConfigureAwait(false);
                RememberSuccessfulProxy(
                    candidate, route.Target, sw.Elapsed, rememberTargetPreference);
                RememberSuccessfulPath(config, candidate, sw.Elapsed, route.Strategy);
                failureCache.ClearSuccess(candidate);
                // Save per-server preferred route so each server remembers its
                // own best proxy independently of the global PreferredProxyId.
                if (rememberTargetPreference)
                    route.Target.PreferredRoute = new CachedRoute
                    {
                        ProxyId = candidate.Proxy.Id,
                        ServerIds = candidate.Servers.Select(s => s.Id).ToList(),
                        LatencyMs = sw.Elapsed.TotalMilliseconds,
                        LastSuccessUtc = DateTimeOffset.UtcNow
                    };
                Emit(SshTraceStage.RouteReady, route.Target.Name, "PASS");
                return (route, candidate, sw.Elapsed);
            }
            catch (SshAuthenticationException ex)
            {
                foreach (var item in originalHostKeys)
                    item.Server.HostKeyFingerprint = item.Fingerprint;
                failures.Add(new InvalidOperationException(
                    $"{ProxyLabel(candidate.Proxy)}: {SafeMessage(ex)}", ex));
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "FAIL", ex);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "CANCELLED", ex);
                throw;
            }
            catch (OperationCanceledException ex) when (attempt.IsCancellationRequested)
            {
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                foreach (var item in originalHostKeys)
                    item.Server.HostKeyFingerprint = item.Fingerprint;
                failures.Add(timeout);
                failureCache.RememberDirectFailure(candidate, DateTimeOffset.UtcNow);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (SshOperationTimeoutException ex)
            {
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                foreach (var item in originalHostKeys)
                    item.Server.HostKeyFingerprint = item.Fingerprint;
                failures.Add(timeout);
                failureCache.RememberDirectFailure(candidate, DateTimeOffset.UtcNow);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt.IsCancellationRequested)
            {
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                foreach (var item in originalHostKeys)
                    item.Server.HostKeyFingerprint = item.Fingerprint;
                failures.Add(timeout);
                failureCache.RememberDirectFailure(candidate, DateTimeOffset.UtcNow);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (Exception ex)
            {
                foreach (var item in originalHostKeys)
                    item.Server.HostKeyFingerprint = item.Fingerprint;
                failures.Add(new InvalidOperationException(
                    $"{ProxyLabel(candidate.Proxy)}: {SafeMessage(ex)}", ex));
                failureCache.RememberDirectFailure(candidate, DateTimeOffset.UtcNow);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "FAIL", ex);
            }
        }
        throw new InvalidOperationException(AddTimeoutHint(failureMessage, failures), CombineFailures(failures));
    }

    public async Task<IReadOnlyList<ConnectivityResult>> CheckFromAsync(
        ManagerConfig config, Guid sourceId, IEnumerable<Guid> targetIds,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        // Use full candidates (including multi-hop routes) so that servers
        // reachable only via intermediate hosts can be used as source.
        var source = config.FindServer(sourceId);
        var ranked = RoutePlanner.Candidates(config, sourceId);
        var candidates = RoutePlanner.OrderPreferred(config, ranked, source?.PreferredRoute);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CreateAttemptCancellation(cancellationToken);
            var routeTarget = candidate.Servers[^1];
            var originalHostKey = routeTarget.HostKeyFingerprint;
            var sw = Stopwatch.StartNew();
            try
            {
                using var sourceRoute = await Task.Run(
                        () => Connect(candidate, false, attempt.Token), attempt.Token)
                    .ConfigureAwait(false);
                RememberSuccessfulProxy(candidate.Proxy, routeTarget, sw.Elapsed);
                // Remember the route used to reach the source so subsequent
                // opens reuse it instead of re-discovering.
                routeTarget.PreferredRoute = RoutePreferencePolicy.FromSuccess(
                    candidate, sw.Elapsed, DateTimeOffset.UtcNow);
                var results = await Task.Run(
                        () => CheckTargets(config, sourceId, targetIds,
                            sourceRoute.Client ?? throw new InvalidOperationException("Исходная SSH-сессия не создана."),
                            candidate.Proxy.Id, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                // Remember the route to each successfully checked target so
                // the first connection goes straight through the known path —
                // but never downgrade a shorter/proven route (e.g. a confirmed
                // direct path) to the longer discovery chain we happened to use.
                foreach (var result in results.Where(r => r.Success))
                {
                    var target = config.FindServer(result.TargetId);
                    if (target is null) continue;
                    var fullChain = candidate.Servers.Append(target).Select(s => s.Id).ToList();
                    if (!RoutePreferencePolicy.ShouldReplacePreferred(target.PreferredRoute, fullChain))
                        continue;
                    target.PreferredRoute = new CachedRoute
                    {
                        ProxyId = candidate.Proxy.Id,
                        ServerIds = fullChain,
                        LatencyMs = sw.Elapsed.TotalMilliseconds,
                        LastSuccessUtc = DateTimeOffset.UtcNow
                    };
                }
                return results;
            }
            catch (SshAuthenticationException ex)
            {
                routeTarget.HostKeyFingerprint = originalHostKey;
                failures.Add(new InvalidOperationException(
                    $"{ProxyLabel(candidate.Proxy)}: {SafeMessage(ex)}", ex));
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "CANCELLED", ex);
                throw;
            }
            catch (OperationCanceledException ex) when (attempt.IsCancellationRequested)
            {
                routeTarget.HostKeyFingerprint = originalHostKey;
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                failures.Add(timeout);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (SshOperationTimeoutException ex)
            {
                routeTarget.HostKeyFingerprint = originalHostKey;
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                failures.Add(timeout);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt.IsCancellationRequested)
            {
                routeTarget.HostKeyFingerprint = originalHostKey;
                var timeout = AttemptTimeout(ProxyLabel(candidate.Proxy), ex);
                failures.Add(timeout);
                Emit(SshTraceStage.RouteCandidate, ProxyLabel(candidate.Proxy), "TIMEOUT", timeout);
            }
            catch (Exception ex)
            {
                routeTarget.HostKeyFingerprint = originalHostKey;
                failures.Add(new InvalidOperationException(
                    $"{ProxyLabel(candidate.Proxy)}: {SafeMessage(ex)}", ex));
            }
        }

        throw new InvalidOperationException(
            AddTimeoutHint("Не найден рабочий маршрут к исходному серверу.", failures),
            CombineFailures(failures));
    }

    public async Task<IReadOnlyList<ConnectivityResult>> CheckFromAsync(
        ManagerConfig config, Guid sourceId, IEnumerable<Guid> targetIds, BaseProxy proxy,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConnectivityResult>();
        var source = config.FindServer(sourceId)
            ?? throw new InvalidOperationException("Исходный сервер не найден.");
        if (!RoutePlanner.SatisfiesRouteConstraints(new RouteCandidate(proxy, [source])))
            throw new InvalidOperationException(
                "Исходная сессия требует обязательный предыдущий переход и не может быть открыта напрямую.");
        var originalHostKey = source.HostKeyFingerprint;
        var sw = Stopwatch.StartNew();
        using var attempt = CreateAttemptCancellation(cancellationToken);
        try
        {
            using var sourceRoute = await Task.Run(
                    () => Connect(new RouteCandidate(proxy, [source]), false, attempt.Token), attempt.Token)
                .ConfigureAwait(false);
            RememberSuccessfulProxy(proxy, source, sw.Elapsed);
            return await Task.Run(
                    () => CheckTargets(config, sourceId, targetIds,
                        sourceRoute.Client ?? throw new InvalidOperationException("Исходная SSH-сессия не создана."),
                        proxy.Id, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt.IsCancellationRequested)
        {
            source.HostKeyFingerprint = originalHostKey;
            var timeout = AttemptTimeout(ProxyLabel(proxy));
            Emit(SshTraceStage.RouteCandidate, ProxyLabel(proxy), "TIMEOUT", timeout);
            throw timeout;
        }
        catch (SshOperationTimeoutException ex)
        {
            source.HostKeyFingerprint = originalHostKey;
            var timeout = AttemptTimeout(ProxyLabel(proxy), ex);
            Emit(SshTraceStage.RouteCandidate, ProxyLabel(proxy), "TIMEOUT", timeout);
            throw timeout;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt.IsCancellationRequested)
        {
            source.HostKeyFingerprint = originalHostKey;
            var timeout = AttemptTimeout(ProxyLabel(proxy), ex);
            Emit(SshTraceStage.RouteCandidate, ProxyLabel(proxy), "TIMEOUT", timeout);
            throw timeout;
        }
        catch
        {
            source.HostKeyFingerprint = originalHostKey;
            throw;
        }
    }

    private IReadOnlyList<ConnectivityResult> CheckTargets(
        ManagerConfig config, Guid sourceId, IEnumerable<Guid> targetIds, SshClient sourceClient,
        Guid proxyId, CancellationToken cancellationToken)
    {
        var results = new List<ConnectivityResult>();
        var source = config.FindServer(sourceId);
        foreach (var id in targetIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A dropped source route cannot produce a meaningful result for this
            // or later targets. Return the completed prefix so the batch runner
            // can reconnect and continue only with targets that have no result.
            if (!sourceClient.IsConnected) break;
            var target = config.FindServer(id);
            if (target is null || target.Id == sourceId) continue;
            var originalHostKey = target.HostKeyFingerprint;
            // Пробуем основной адрес и все резервные настоящей попыткой; у каждого
            // свой таймаут, поэтому «молчащий» основной адрес не лишает времени
            // резервный. Неуспех по сети → следующий адрес; по учётке → стоп.
            ConnectivityResult? successResult = null;
            string? lastFailureMessage = null;
            TimeSpan lastFailureDuration = default;
            var endpointContext = EndpointContext.Via(sourceId);
            var orderedEndpoints = ServerEndpointPolicy.Ordered(target, endpointContext);
            var availableEndpoints = orderedEndpoints
                .Where(endpoint => !endpointFailureCache.ShouldSkip(
                    target.Id, endpointContext, endpoint, DateTimeOffset.UtcNow))
                .ToArray();
            if (availableEndpoints.Length == 0) availableEndpoints = orderedEndpoints.ToArray();
            foreach (var endpoint in availableEndpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var attempt = CreateAttemptCancellation(cancellationToken);
                var sw = Stopwatch.StartNew();
                var strategy = SshRouteStrategy.DirectTcpIp;
                try
                {
                    using var forward = new ForwardedPortLocal("127.0.0.1", 0, endpoint.Host, (uint)endpoint.Port);
                    Exception? forwardFailure = null;
                    forward.Exception += (_, args) =>
                    {
                        forwardFailure = args.Exception;
                        Emit(SshTraceStage.ChannelForward, target.Name, "FAIL", args.Exception);
                    };
                    Emit(SshTraceStage.ChannelForward, target.Name, "START");
                    sourceClient.AddForwardedPort(forward);
                    forward.Start();
                    try
                    {
                        using var targetClient = CreateDirect(target, "127.0.0.1", checked((int)forward.BoundPort));
                        Emit(SshTraceStage.TargetAuthentication, target.Name, AuthenticationStart(target));
                        ConnectClient(targetClient, attempt.Token);
                        Emit(SshTraceStage.ChannelForward, target.Name, "PASS");
                        Emit(SshTraceStage.TargetAuthentication, target.Name, "PASS");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (SshAuthenticationException authenticationError)
                    {
                        Emit(SshTraceStage.TargetAuthentication, target.Name, "FAIL", authenticationError);
                        throw;
                    }
                    catch (Exception directError) when (directError is not SshAuthenticationException)
                    {
                        try { forward.Stop(); } catch { }
                        sourceClient.RemoveForwardedPort(forward);
                        Emit(SshTraceStage.TargetAuthentication, target.Name, "DEFERRED", directError);
                        Emit(SshTraceStage.RemoteCommandFallback, target.Name, "START", forwardFailure);
                        using var fallbackAttempt = CreateAttemptCancellation(cancellationToken);
                        try
                        {
                            using var bridge = new RemoteCommandBridge(
                                sourceClient, endpoint.Host, endpoint.Port, cancellationToken: fallbackAttempt.Token,
                                privilegeTimeout: Timeout);
                            using var targetClient = CreateDirect(target, "127.0.0.1", bridge.LocalPort);
                            ConnectClient(targetClient, fallbackAttempt.Token);
                            Emit(SshTraceStage.RemoteCommandFallback, target.Name, "PASS");
                            Emit(SshTraceStage.TargetAuthentication, target.Name, "PASS");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (SshAuthenticationException authenticationError)
                        {
                            Emit(SshTraceStage.RemoteCommandFallback, target.Name, "PASS");
                            Emit(SshTraceStage.TargetAuthentication, target.Name, "FAIL", authenticationError);
                            throw;
                        }
                        catch (Exception fallbackError)
                        {
                            Emit(SshTraceStage.RemoteCommandFallback, target.Name, "FAIL", fallbackError);
                            if (!CanUsePrivilegeFallback(source))
                                throw new InvalidOperationException(
                                    "direct-tcpip and remote command fallback both failed.",
                                    new AggregateException(directError, forwardFailure ?? directError, fallbackError));

                            Emit(SshTraceStage.PrivilegedCommandFallback, target.Name, "START");
                            using var privilegedAttempt = CreateAttemptCancellation(cancellationToken);
                            try
                            {
                                using var rootBridge = new RemoteCommandBridge(
                                    sourceClient, endpoint.Host, endpoint.Port, source!.RootPassword, source.RootLogin,
                                    privilegedAttempt.Token, Timeout);
                                using var rootTargetClient = CreateDirect(target, "127.0.0.1", rootBridge.LocalPort);
                                ConnectClient(rootTargetClient, privilegedAttempt.Token);
                                strategy = SshRouteStrategy.SuRemoteCommand;
                                Emit(SshTraceStage.PrivilegedCommandFallback, target.Name, "PASS");
                                Emit(SshTraceStage.TargetAuthentication, target.Name, "PASS");
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception privilegedError)
                            {
                                Emit(SshTraceStage.PrivilegedCommandFallback, target.Name, "FAIL", privilegedError);
                                throw new InvalidOperationException(
                                    "direct-tcpip, remote command and su command strategies failed.",
                                    new AggregateException(directError, forwardFailure ?? directError,
                                        fallbackError, privilegedError));
                            }
                        }
                        if (strategy != SshRouteStrategy.SuRemoteCommand)
                            strategy = SshRouteStrategy.RemoteCommand;
                    }
                    ServerEndpointPolicy.Remember(
                        target, endpoint, endpointContext);
                    endpointFailureCache.ClearSuccess(target.Id, endpointContext, endpoint);
                    successResult = new ConnectivityResult(
                        target.Id, true, "SSH-аутентификация успешна", sw.Elapsed, strategy, proxyId, sourceId);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    target.HostKeyFingerprint = originalHostKey;
                    throw;
                }
                catch (SshAuthenticationException ex)
                {
                    // Учётные данные одинаковы для всех адресов — остальные не пробуем.
                    target.HostKeyFingerprint = originalHostKey;
                    lastFailureMessage = SafeMessage(ex);
                    lastFailureDuration = sw.Elapsed;
                    break;
                }
                catch (OperationCanceledException ex) when (attempt.IsCancellationRequested)
                {
                    endpointFailureCache.RememberFailure(
                        target.Id, endpointContext, endpoint, DateTimeOffset.UtcNow);
                    target.HostKeyFingerprint = originalHostKey;
                    var timeout = AttemptTimeout(target.Name, ex);
                    Emit(SshTraceStage.TargetAuthentication, target.Name, "TIMEOUT", timeout);
                    lastFailureMessage = timeout.Message;
                    lastFailureDuration = sw.Elapsed;
                }
                catch (SshOperationTimeoutException ex)
                {
                    endpointFailureCache.RememberFailure(
                        target.Id, endpointContext, endpoint, DateTimeOffset.UtcNow);
                    target.HostKeyFingerprint = originalHostKey;
                    var timeout = AttemptTimeout(target.Name, ex);
                    Emit(SshTraceStage.TargetAuthentication, target.Name, "TIMEOUT", timeout);
                    lastFailureMessage = timeout.Message;
                    lastFailureDuration = sw.Elapsed;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt.IsCancellationRequested)
                {
                    endpointFailureCache.RememberFailure(
                        target.Id, endpointContext, endpoint, DateTimeOffset.UtcNow);
                    target.HostKeyFingerprint = originalHostKey;
                    var timeout = AttemptTimeout(target.Name, ex);
                    Emit(SshTraceStage.TargetAuthentication, target.Name, "TIMEOUT", timeout);
                    lastFailureMessage = timeout.Message;
                    lastFailureDuration = sw.Elapsed;
                }
                catch (Exception ex)
                {
                    endpointFailureCache.RememberFailure(
                        target.Id, endpointContext, endpoint, DateTimeOffset.UtcNow);
                    target.HostKeyFingerprint = originalHostKey;
                    lastFailureMessage = SafeMessage(ex);
                    lastFailureDuration = sw.Elapsed;
                    if (!sourceClient.IsConnected) break;
                }
            }

            if (!sourceClient.IsConnected && successResult is null) break;

            if (successResult is not null)
                results.Add(successResult);
            else
                results.Add(new ConnectivityResult(target.Id, false,
                    lastFailureMessage ?? "Не удалось подключиться ни по одному адресу.",
                    lastFailureDuration, SourceId: sourceId));
        }
        return results;
    }

    private static void RememberSuccessfulProxy(
        RouteCandidate candidate, ManagedServer target, TimeSpan duration,
        bool rememberTargetPreference = true)
    {
        if (rememberTargetPreference)
            target.PreferredProxyId = candidate.WithoutProxy ? null : candidate.Proxy.Id;
        if (candidate.WithoutProxy) return;
        RememberSuccessfulProxy(candidate.Proxy, target, duration, rememberTargetPreference);
    }

    private static void RememberSuccessfulProxy(
        BaseProxy proxy, ManagedServer target, TimeSpan duration,
        bool rememberTargetPreference = true)
    {
        if (rememberTargetPreference) target.PreferredProxyId = proxy.Id;
        proxy.LastSuccessUtc = DateTimeOffset.UtcNow;
        proxy.LastConnectLatencyMs = duration.TotalMilliseconds;
    }

    private static void RememberSuccessfulPath(
        ManagerConfig config, RouteCandidate candidate, TimeSpan duration, string strategy)
    {
        if (candidate.Servers.Count < 2) return;
        var perLink = duration.TotalMilliseconds / (candidate.Servers.Count - 1);
        for (var index = 0; index < candidate.Servers.Count - 1; index++)
        {
            var from = candidate.Servers[index].Id;
            var to = candidate.Servers[index + 1].Id;
            var link = config.Links.FirstOrDefault(item =>
                item.FromServerId == from && item.ToServerId == to);
            if (link is null) continue;
            var now = DateTimeOffset.UtcNow;
            link.LastSuccessUtc = now;
            LinkStatisticsPolicy.Remember(
                link, candidate.Proxy.Id, now, perLink, strategy);
        }
    }

    private CancellationTokenSource CreateAttemptCancellation(CancellationToken cancellationToken)
    {
        if (Timeout <= TimeSpan.Zero || Timeout == System.Threading.Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException("Тайм-аут подключения должен быть больше нуля.");
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(Timeout);
        return attempt;
    }

    private TimeoutException AttemptTimeout(string subject, Exception? inner = null)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(Timeout.TotalSeconds));
        return new TimeoutException(
            $"Операция «{subject}» не завершилась за {seconds} сек. Увеличьте тайм-аут подключения в настройках.",
            inner);
    }

    private static string AddTimeoutHint(string message, IEnumerable<Exception> failures) =>
        failures.Any(ContainsTimeout)
            ? message + " Подключиться не удалось за отведённое время; вы можете увеличить таймаут в настройках."
            : message;

    private static bool ContainsTimeout(Exception exception)
    {
        if (exception is TimeoutException or SshOperationTimeoutException) return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(ContainsTimeout);
        return exception.InnerException is not null && ContainsTimeout(exception.InnerException);
    }

    private static bool CanUsePrivilegeFallback(ManagedServer? server)
    {
        if (server is null) return false;
        var command = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin);
        if (command?.StartsWith("sudo", StringComparison.OrdinalIgnoreCase) == true) return true;
        return !string.IsNullOrEmpty(server.RootPassword);
    }

    private ActiveRoute Connect(
        RouteCandidate candidate, bool exposePorts = true,
        CancellationToken cancellationToken = default, bool consoleOnly = false)
    {
        var resources = new List<IDisposable>();
        var hops = new List<RouteHop>();
        SshClient? previous = null;
        ManagedServer? previousServer = null;
        int? finalIngressPort = null;
        SshClient? finalIngressOwner = null;
        var strategy = SshRouteStrategy.DirectTcpIp;
        void RememberHop(ManagedServer server, ServerEndpoint endpoint, EndpointContext context)
        {
            ServerEndpointPolicy.Remember(server, endpoint, context);
            hops.Add(new RouteHop(server.Id, server.Name, endpoint.Host, endpoint.Port));
        }
        try
        {
            if (consoleOnly && candidate.Servers.Count == 1)
            {
                var consoleTarget = candidate.Servers[0];
                var context = EndpointContext.Direct(candidate.Proxy.Id);
                var consoleEndpoint = (candidate.WithoutProxy
                        ? SelectEndpointDirectAsync(consoleTarget, context, cancellationToken)
                        : SelectEndpointViaProxyAsync(
                            candidate.Proxy, consoleTarget, context, cancellationToken))
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                Emit(SshTraceStage.ProxyConnect,
                    candidate.WithoutProxy ? "direct" : ProxyLabel(candidate.Proxy), "START");
                Emit(SshTraceStage.ProxyConnect, $"{consoleEndpoint.Host}:{consoleEndpoint.Port}",
                    candidate.WithoutProxy ? "DIRECT_CONNECT_TARGET" : "SOCKS5_CONNECT_TARGET");
                IDisposable bridge = candidate.WithoutProxy
                    ? new DirectConsoleBridge(
                        consoleEndpoint.Host, consoleEndpoint.Port, Timeout, cancellationToken)
                    : new Socks5ConsoleBridge(
                        candidate.Proxy, consoleEndpoint.Host, consoleEndpoint.Port, Timeout, cancellationToken);
                resources.Add(bridge);
                RememberHop(consoleTarget, consoleEndpoint, context);
                Emit(SshTraceStage.ProxyConnect,
                    candidate.WithoutProxy ? "direct" : ProxyLabel(candidate.Proxy), "PASS");
                Emit(SshTraceStage.TargetAuthentication, consoleTarget.Name, "DEFERRED_TO_KITTY");
                Emit(SshTraceStage.ChannelForward, consoleTarget.Name, "CONSOLE_TRANSPORT_READY");
                var localPort = bridge is DirectConsoleBridge direct
                    ? direct.LocalPort
                    : ((Socks5ConsoleBridge)bridge).LocalPort;
                return new ActiveRoute(consoleTarget, localPort, 0, strategy, null, resources, hops,
                    candidate.WithoutProxy);
            }

            foreach (var server in candidate.Servers)
            {
                SshClient client;
                ServerEndpoint hopEndpoint;
                var hopContext = previous is null
                    ? EndpointContext.Direct(candidate.Proxy.Id)
                    : EndpointContext.Via(previousServer!.Id);
                if (previous is null)
                {
                    hopEndpoint = (candidate.WithoutProxy
                            ? SelectEndpointDirectAsync(server, hopContext, cancellationToken)
                            : SelectEndpointViaProxyAsync(
                                candidate.Proxy, server, hopContext, cancellationToken))
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    Emit(SshTraceStage.ProxyConnect,
                        candidate.WithoutProxy ? "direct" : ProxyLabel(candidate.Proxy), "START");
                    Emit(SshTraceStage.SourceAuthentication, server.Name, AuthenticationStart(server));
                    client = candidate.WithoutProxy
                        ? CreateDirect(server, hopEndpoint.Host, hopEndpoint.Port)
                        : CreateViaProxy(server, candidate.Proxy, hopEndpoint.Host, hopEndpoint.Port);
                }
                else
                {
                    hopEndpoint = SelectEndpointViaClientAsync(
                            previous, server, hopContext, cancellationToken)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    var ingressOwner = previous;
                    Emit(SshTraceStage.ChannelForward, server.Name, "START");
                    var forward = new ForwardedPortLocal("127.0.0.1", 0, hopEndpoint.Host, (uint)hopEndpoint.Port);
                    Exception? forwardFailure = null;
                    forward.Exception += (_, args) =>
                    {
                        forwardFailure = args.Exception;
                        Emit(SshTraceStage.ChannelForward, server.Name, "FAIL", args.Exception);
                    };
                    previous.AddForwardedPort(forward);
                    forward.Start();
                    resources.Add(forward);
                    Emit(SshTraceStage.TargetAuthentication, server.Name, AuthenticationStart(server));
                    client = CreateDirect(server, "127.0.0.1", checked((int)forward.BoundPort));
                    try
                    {
                        ConnectClient(client, cancellationToken);
                        Emit(SshTraceStage.ChannelForward, server.Name, "PASS");
                        Emit(SshTraceStage.TargetAuthentication, server.Name, "PASS");
                        resources.Add(client);
                        finalIngressPort = checked((int)forward.BoundPort);
                        finalIngressOwner = ingressOwner;
                        previous = client;
                        previousServer = server;
                        RememberHop(server, hopEndpoint, hopContext);
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        client.Dispose();
                        throw;
                    }
                    catch (SshAuthenticationException authenticationError)
                    {
                        client.Dispose();
                        Emit(SshTraceStage.TargetAuthentication, server.Name, "FAIL", authenticationError);
                        throw;
                    }
                    catch (Exception directError) when (directError is not SshAuthenticationException)
                    {
                        client.Dispose();
                        try { forward.Stop(); } catch { }
                        previous.RemoveForwardedPort(forward);
                        forward.Dispose();
                        resources.Remove(forward);
                        Emit(SshTraceStage.TargetAuthentication, server.Name, "DEFERRED", directError);
                        Emit(SshTraceStage.RemoteCommandFallback, server.Name, "START", forwardFailure);
                        try
                        {
                            var bridge = new RemoteCommandBridge(
                                previous, hopEndpoint.Host, hopEndpoint.Port, cancellationToken: cancellationToken,
                                privilegeTimeout: Timeout);
                            resources.Add(bridge);
                            client = CreateDirect(server, "127.0.0.1", bridge.LocalPort);
                            ConnectClient(client, cancellationToken);
                            resources.Add(client);
                            finalIngressPort = bridge.LocalPort;
                            finalIngressOwner = ingressOwner;
                            previous = client;
                            previousServer = server;
                            RememberHop(server, hopEndpoint, hopContext);
                            strategy = SshRouteStrategy.RemoteCommand;
                            Emit(SshTraceStage.RemoteCommandFallback, server.Name, "PASS");
                            Emit(SshTraceStage.TargetAuthentication, server.Name, "PASS");
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            client?.Dispose();
                            throw;
                        }
                        catch (SshAuthenticationException authenticationError)
                        {
                            client?.Dispose();
                            Emit(SshTraceStage.RemoteCommandFallback, server.Name, "PASS");
                            Emit(SshTraceStage.TargetAuthentication, server.Name, "FAIL", authenticationError);
                            throw;
                        }
                        catch (Exception fallbackError)
                        {
                            client?.Dispose();
                            Emit(SshTraceStage.RemoteCommandFallback, server.Name, "FAIL", fallbackError);
                            if (!CanUsePrivilegeFallback(previousServer))
                            {
                                Emit(SshTraceStage.TargetAuthentication, server.Name, "FAIL", fallbackError);
                                throw new InvalidOperationException(
                                    "direct-tcpip and remote command fallback both failed.",
                                    new AggregateException(directError, forwardFailure ?? directError, fallbackError));
                            }

                            Emit(SshTraceStage.PrivilegedCommandFallback, server.Name, "START");
                            try
                            {
                                var rootBridge = new RemoteCommandBridge(
                                    previous, hopEndpoint.Host, hopEndpoint.Port,
                                    previousServer!.RootPassword, previousServer.RootLogin,
                                    cancellationToken, Timeout);
                                resources.Add(rootBridge);
                                client = CreateDirect(server, "127.0.0.1", rootBridge.LocalPort);
                                ConnectClient(client, cancellationToken);
                                resources.Add(client);
                                finalIngressPort = rootBridge.LocalPort;
                                finalIngressOwner = ingressOwner;
                                previous = client;
                                previousServer = server;
                                RememberHop(server, hopEndpoint, hopContext);
                                strategy = SshRouteStrategy.SuRemoteCommand;
                                Emit(SshTraceStage.PrivilegedCommandFallback, server.Name, "PASS");
                                Emit(SshTraceStage.TargetAuthentication, server.Name, "PASS");
                                continue;
                            }
                            catch (OperationCanceledException)
                            {
                                client?.Dispose();
                                throw;
                            }
                            catch (Exception privilegedError)
                            {
                                client?.Dispose();
                                Emit(SshTraceStage.PrivilegedCommandFallback, server.Name, "FAIL", privilegedError);
                                Emit(SshTraceStage.TargetAuthentication, server.Name, "FAIL", privilegedError);
                                throw new InvalidOperationException(
                                    "direct-tcpip, remote command and su command strategies failed.",
                                    new AggregateException(directError, forwardFailure ?? directError,
                                        fallbackError, privilegedError));
                            }
                        }
                    }
                }
                try
                {
                    ConnectClient(client, cancellationToken);
                    if (previous is null)
                    {
                        Emit(SshTraceStage.ProxyConnect, ProxyLabel(candidate.Proxy), "PASS");
                        Emit(SshTraceStage.SourceAuthentication, server.Name, "PASS");
                    }
                    else
                    {
                        Emit(SshTraceStage.ChannelForward, server.Name, "PASS");
                        Emit(SshTraceStage.TargetAuthentication, server.Name, "PASS");
                    }
                    resources.Add(client);
                    previous = client;
                    previousServer = server;
                    RememberHop(server, hopEndpoint, hopContext);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    if (previous is null)
                    {
                        Emit(SshTraceStage.ProxyConnect, ProxyLabel(candidate.Proxy),
                            ex is SshAuthenticationException ? "PASS" : FailureStatus(ex), ex);
                        Emit(SshTraceStage.SourceAuthentication, server.Name, FailureStatus(ex), ex);
                    }
                    else
                    {
                        Emit(SshTraceStage.TargetAuthentication, server.Name, FailureStatus(ex), ex);
                    }
                    throw;
                }
            }
            if (previous is null) throw new InvalidOperationException("Маршрут не содержит серверов.");
            if (!exposePorts)
                return new ActiveRoute(candidate.Servers[^1], 0, 0, strategy, previous, resources, hops);

            var target = candidate.Servers[^1];
            // sshd на цели слушает тот же порт, по которому мы к ней подключились
            // (основной или сработавший резервный), поэтому loopback‑ingress для
            // консоли берёт именно его, а не сырой порт сессии.
            var targetSshPort = hops.Count > 0 ? hops[^1].Port : target.Port;
            if (consoleOnly && finalIngressPort is not null && finalIngressOwner is not null)
            {
                // The authentication above verifies the final hop. Some servers
                // reject a second concurrent SSH login for the same account, so
                // release the probe connection before KiTTY takes its place.
                resources.Remove(previous);
                previous.Dispose();
                previous = finalIngressOwner;
                Emit(SshTraceStage.ChannelForward, target.Name, "CONSOLE_INGRESS_READY");
                return new ActiveRoute(target, finalIngressPort.Value, 0, strategy, previous, resources, hops);
            }
            // This forward runs on the target itself. Connecting back through its
            // externally configured address may fail when that address is not
            // locally routable (NAT, split DNS); sshd's loopback endpoint is the
            // appropriate destination for the second KiTTY connection.
            int localSshPort;
            if (strategy != SshRouteStrategy.DirectTcpIp)
            {
                // Keep console launch independent of direct-tcpip as well. A
                // second nc session is intentionally used because the first
                // bridge is occupied by the manager's target SshClient.
                var consoleBridge = new RemoteCommandBridge(
                    previous, "127.0.0.1", targetSshPort, cancellationToken: cancellationToken,
                    privilegeTimeout: Timeout);
                resources.Add(consoleBridge);
                localSshPort = consoleBridge.LocalPort;
                Emit(SshTraceStage.RemoteCommandFallback, target.Name, "CONSOLE_READY");
            }
            else if (finalIngressPort is not null)
            {
                // Reuse the already authenticated and verified ingress into the
                // final server. Some sshd configurations do not listen on their
                // own loopback address, so target -> 127.0.0.1:ssh can close the
                // KiTTY connection even though source -> target works.
                localSshPort = finalIngressPort.Value;
            }
            else
            {
                var ssh = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)targetSshPort);
                previous.AddForwardedPort(ssh); ssh.Start(); resources.Add(ssh);
                localSshPort = checked((int)ssh.BoundPort);
            }
            var socks = new ForwardedPortDynamic("127.0.0.1", 0);
            socks.Exception += (_, args) => Emit(SshTraceStage.ChannelForward,
                $"{target.Name} web SOCKS", "FAIL", args.Exception);
            previous.AddForwardedPort(socks); socks.Start(); resources.Add(socks);
            if (strategy != SshRouteStrategy.DirectTcpIp)
                Emit(SshTraceStage.ChannelForward, target.Name, "DYNAMIC_SOCKS_UNVERIFIED");
            return new ActiveRoute(target, localSshPort, checked((int)socks.BoundPort), strategy, previous, resources, hops);
        }
        catch
        {
            for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
            throw;
        }
    }

    private SshClient CreateViaProxy(ManagedServer s, BaseProxy p, string host, int port)
    {
        var info = new ConnectionInfo(host, port, s.EffectiveUsername, ProxyTypes.Socks5, p.Host, p.Port, null, null,
            CreateAuthenticationMethods(s)) { Timeout = Timeout };
        return CreateClient(s, info);
    }

    private SshClient CreateDirect(ManagedServer s, string host, int port)
    {
        var info = new ConnectionInfo(host, port, s.EffectiveUsername,
            CreateAuthenticationMethods(s)) { Timeout = Timeout };
        return CreateClient(s, info);
    }

    private async Task<ServerEndpoint> SelectEndpointDirectAsync(
        ManagedServer server, EndpointContext context,
        CancellationToken cancellationToken)
    {
        var endpoints = ServerEndpointPolicy.Ordered(server, context);
        if (endpoints.Count == 1) return endpoints[0];
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCancellation.CancelAfter(EndpointProbeTimeout);
                using var client = new TcpClient();
                await client.ConnectAsync(
                    endpoint.Host, endpoint.Port, probeCancellation.Token).ConfigureAwait(false);
                var banner = new byte[1];
                if (await client.GetStream().ReadAsync(
                        banner, probeCancellation.Token).ConfigureAwait(false) > 0)
                    return endpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }
        return endpoints[0];
    }

    /// <summary>
    /// Выбирает достижимый адрес сервера при подключении напрямую через точку
    /// входа (SOCKS5). Когда резервных адресов нет — возвращает основной без
    /// зондирования, чтобы не менять поведение и не добавлять задержек.
    /// </summary>
    private async Task<ServerEndpoint> SelectEndpointViaProxyAsync(
        BaseProxy proxy, ManagedServer server, EndpointContext context,
        CancellationToken cancellationToken)
    {
        var endpoints = ServerEndpointPolicy.Ordered(server, context);
        if (endpoints.Count == 1) return endpoints[0];
        var now = DateTimeOffset.UtcNow;
        var candidates = endpoints
            .Where(endpoint => !endpointFailureCache.ShouldSkip(server.Id, context, endpoint, now))
            .ToArray();
        if (candidates.Length == 0) candidates = endpoints.ToArray();
        foreach (var endpoint in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(EndpointProbeTimeout);
                using var client = await Socks5TcpProbe.ConnectAsync(
                    proxy, endpoint.Host, endpoint.Port, probeTimeout.Token).ConfigureAwait(false);
                // SOCKS CONNECT подтверждает лишь TCP‑открытие удалённого порта.
                // Читаем байт баннера sshd, чтобы отличить живой сервис от «порт
                // открыт, но закрыт до приветствия» и корректно перебрать резервные.
                var banner = new byte[1];
                var read = await client.GetStream().ReadAsync(banner, 0, 1, probeTimeout.Token)
                    .ConfigureAwait(false);
                if (read > 0)
                {
                    endpointFailureCache.ClearSuccess(server.Id, context, endpoint);
                    return endpoint;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // адрес недоступен через эту точку входа — пробуем следующий
                endpointFailureCache.RememberFailure(
                    server.Id, context, endpoint, DateTimeOffset.UtcNow);
            }
        }
        return candidates[0];
    }

    /// <summary>
    /// Выбирает достижимый адрес сервера при подключении через уже открытый
    /// SSH‑клиент (предыдущий хоп цепочки или источник проверки связи):
    /// поднимает временный direct‑tcpip forward и делает короткий TCP‑зонд.
    /// Без резервных адресов — основной адрес без зондирования.
    /// </summary>
    private async Task<ServerEndpoint> SelectEndpointViaClientAsync(
        SshClient previous, ManagedServer server, EndpointContext context,
        CancellationToken cancellationToken)
    {
        var endpoints = ServerEndpointPolicy.Ordered(server, context);
        if (endpoints.Count == 1) return endpoints[0];
        var now = DateTimeOffset.UtcNow;
        var candidates = endpoints
            .Where(endpoint => !endpointFailureCache.ShouldSkip(server.Id, context, endpoint, now))
            .ToArray();
        if (candidates.Length == 0) candidates = endpoints.ToArray();
        foreach (var endpoint in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardedPortLocal? probe = null;
            try
            {
                probe = new ForwardedPortLocal("127.0.0.1", 0, endpoint.Host, (uint)endpoint.Port);
                previous.AddForwardedPort(probe);
                probe.Start();
                using var tcp = new System.Net.Sockets.TcpClient();
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(EndpointProbeTimeout);
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, checked((int)probe.BoundPort), probeTimeout.Token)
                    .ConfigureAwait(false);
                // Локальное подключение к forward успешно всегда, поэтому оно не
                // говорит о достижимости цели. Реальный сигнал — баннер sshd с
                // удалённого конца: если цель недоступна, forward закрывает сокет
                // и чтение вернёт 0 (или выбросит исключение) — тогда этот адрес
                // пропускаем и пробуем следующий (резервный).
                var banner = new byte[1];
                var read = await tcp.GetStream().ReadAsync(banner, 0, 1, probeTimeout.Token)
                    .ConfigureAwait(false);
                if (read > 0)
                {
                    endpointFailureCache.ClearSuccess(server.Id, context, endpoint);
                    return endpoint;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Адрес недоступен из этого хопа — пробуем следующий.
                endpointFailureCache.RememberFailure(
                    server.Id, context, endpoint, DateTimeOffset.UtcNow);
            }
            finally
            {
                if (probe is not null)
                {
                    try { probe.Stop(); } catch { }
                    try { previous.RemoveForwardedPort(probe); } catch { }
                    probe.Dispose();
                }
            }
        }
        return candidates[0];
    }

    private static void ConnectClient(SshClient client, CancellationToken cancellationToken) =>
        client.ConnectAsync(cancellationToken).GetAwaiter().GetResult();

    internal static AuthenticationMethod[] CreateAuthenticationMethods(ManagedServer server)
    {
        var methods = new List<AuthenticationMethod>();
        if (CanUsePrivateKey(server))
        {
            var keyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ")!;
            var keyFile = server.PrivateKeyPassphrase.Length == 0
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, server.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(server.EffectiveUsername, keyFile));
        }

        var password = new PasswordAuthenticationMethod(server.EffectiveUsername, server.Password);
        methods.Add(password);
        if (!server.UseKeyboardInteractive) return [.. methods];

        var keyboard = new KeyboardInteractiveAuthenticationMethod(server.EffectiveUsername);
        keyboard.AuthenticationPrompt += (_, args) =>
        {
            foreach (var prompt in args.Prompts)
                if (!prompt.IsEchoed || prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    prompt.Request.Contains("пароль", StringComparison.OrdinalIgnoreCase))
                    prompt.Response = server.Password;
        };
        methods.Add(keyboard);
        return [.. methods];
    }

    private static bool CanUsePrivateKey(ManagedServer server)
    {
        var keyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ");
        if (keyPath is null) return false;
        var metadata = PrivateKeyInspector.Inspect(keyPath);
        return metadata.Resolved &&
               (metadata.Encrypted != true || server.PrivateKeyPassphrase.Length > 0);
    }

    private static string AuthenticationStart(ManagedServer server)
    {
        var methods = new List<string>();
        if (CanUsePrivateKey(server)) methods.Add("private-key");
        methods.Add("password");
        if (server.UseKeyboardInteractive) methods.Add("keyboard-interactive");
        return "START; methods=" + string.Join(',', methods);
    }

    private SshClient CreateClient(ManagedServer server, ConnectionInfo info)
    {
        var client = new SshClient(info);
        client.HostKeyReceived += (_, args) =>
        {
            var fingerprint = "SHA256:" + args.FingerPrintSHA256;
            var saved = NormalizeFingerprint(server.HostKeyFingerprint);
            if (saved.Length > 0)
            {
                if (string.Equals(saved, fingerprint, StringComparison.Ordinal))
                {
                    args.CanTrust = true;
                    Emit(SshTraceStage.HostKey, server.Name, "MATCH");
                    return;
                }

                args.CanTrust = HostKeyMismatchVerifier?.Invoke(server, saved, fingerprint) == true;
                if (args.CanTrust) server.HostKeyFingerprint = fingerprint;
                Emit(SshTraceStage.HostKey, server.Name,
                    args.CanTrust ? "REPLACED_CONFIRMED" : "REJECTED_CHANGED");
                return;
            }

            args.CanTrust = HostKeyVerifier?.Invoke(server, fingerprint) == true;
            if (args.CanTrust) server.HostKeyFingerprint = fingerprint;
            Emit(SshTraceStage.HostKey, server.Name, args.CanTrust ? "ACCEPTED_NEW" : "REJECTED_NEW");
        };
        return client;
    }

    private void Emit(SshTraceStage stage, string subject, string status, Exception? error = null) =>
        Trace?.Invoke(new SshTraceEvent(stage, subject, status, error));

    private static string FailureStatus(Exception exception) =>
        exception is OperationCanceledException ? "CANCELLED" : "FAIL";

    private static string ProxyLabel(BaseProxy proxy) => $"{proxy.Name}; port={proxy.Port}";

    private static string NormalizeFingerprint(string? fingerprint)
    {
        var value = fingerprint?.Trim() ?? "";
        return value.Length == 0 || value.StartsWith("SHA256:", StringComparison.Ordinal)
            ? value
            : "SHA256:" + value;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        SshAuthenticationException => "Ошибка SSH-аутентификации",
        SshConnectionException => "SSH-соединение отклонено",
        _ => ex.Message.Replace('\r', ' ').Replace('\n', ' ')
    };

    private static Exception? CombineFailures(IReadOnlyList<Exception> failures) => failures.Count switch
    {
        0 => null,
        1 => failures[0],
        _ => new AggregateException(failures)
    };
}

/// <summary>
/// Bridges a local socket to `nc target port` running as an ordinary remote
/// command on the already authenticated source. This uses a session channel,
/// so it can work when sshd rejects direct-tcpip/AllowTcpForwarding.
/// </summary>
internal sealed class RemoteCommandBridge : IDisposable
{
    private const string PrivilegedReadyMarker = "__KITTY_MANAGER_PRIVILEGED_READY__";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation;
    private readonly TimeSpan privilegeTimeout;
    private readonly SshCommand command;
    private readonly Stream input;
    private readonly Task commandTask;
    private readonly Task bridgeTask;
    private TcpClient? client;

    public int LocalPort { get; }

    public RemoteCommandBridge(
        SshClient source, string targetHost, int targetPort,
        string? rootPassword = null, string? rootCommand = null,
        CancellationToken cancellationToken = default,
        TimeSpan? privilegeTimeout = null)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.privilegeTimeout = privilegeTimeout ?? TimeSpan.FromSeconds(60);
        listener.Start(1);
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connector = BuildRemoteCommand(targetHost, targetPort);
        var normalizedRootCommand = KittyCredentialDecoder.NormalizeRootCommand(rootCommand);
        var privileged = normalizedRootCommand is not null || !string.IsNullOrEmpty(rootPassword);
        normalizedRootCommand ??= privileged ? "su" : null;
        if (privileged && normalizedRootCommand?.StartsWith("sudo", StringComparison.OrdinalIgnoreCase) != true &&
            string.IsNullOrEmpty(rootPassword))
            throw new InvalidOperationException("Для команды su требуется пароль.");
        var commandText = privileged
            ? BuildPrivilegedCommand(
                $"printf '%s\\n' {ShellQuote(PrivilegedReadyMarker)} >&2; {connector}",
                normalizedRootCommand!, !string.IsNullOrEmpty(rootPassword))
            : connector;
        command = source.CreateCommand(commandText);
        commandTask = command.ExecuteAsync(cancellation.Token);
        try
        {
            input = command.CreateInputStream();
        }
        catch
        {
            cancellation.Cancel();
            listener.Stop();
            command.Dispose();
            cancellation.Dispose();
            _ = commandTask.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
        if (privileged)
        {
            if (!string.IsNullOrEmpty(rootPassword))
            {
                var passwordBytes = System.Text.Encoding.UTF8.GetBytes(rootPassword + "\n");
                try
                {
                    input.Write(passwordBytes, 0, passwordBytes.Length);
                    input.Flush();
                }
                finally
                {
                    Array.Clear(passwordBytes);
                }
            }
            try
            {
                WaitForPrivilegedReadyAsync().GetAwaiter().GetResult();
            }
            catch
            {
                cancellation.Cancel();
                listener.Stop();
                input.Dispose();
                command.Dispose();
                cancellation.Dispose();
                throw;
            }
        }
        // Start accepting the target client only after su has received its
        // password, so SSH protocol bytes cannot be consumed by the prompt.
        bridgeTask = BridgeAsync();
        _ = commandTask.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _ = bridgeTask.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task BridgeAsync()
    {
        client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
        using var network = client.GetStream();
        var toRemote = network.CopyToAsync(input, cancellation.Token);
        var fromRemote = command.OutputStream.CopyToAsync(network, cancellation.Token);
        await Task.WhenAny(toRemote, fromRemote, commandTask).ConfigureAwait(false);
        client.Close();
    }

    private async Task WaitForPrivilegedReadyAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(privilegeTimeout);
        var buffer = new byte[256];
        var output = new System.Text.StringBuilder();
        try
        {
            while (true)
            {
                var read = await command.ExtendedOutputStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidOperationException(
                        "Команда повышения прав завершилась до запуска TCP-моста. Возможно, пароль неверен или su/sudo требует терминал.");
                output.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                if (output.ToString().Contains(PrivilegedReadyMarker, StringComparison.Ordinal)) return;
                if (output.Length > 4096) output.Remove(0, output.Length - PrivilegedReadyMarker.Length * 2);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(privilegeTimeout.TotalSeconds));
            throw new TimeoutException(
                $"Команда повышения прав не запустила TCP-мост за {seconds} сек. " +
                "Возможно, su/sudo требует терминал или другой пароль. Увеличьте тайм-аут подключения в настройках.");
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();
        client?.Dispose();
        input.Dispose();
        command.Dispose();
        cancellation.Dispose();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static string BuildPrivilegedCommand(
        string connector, string configuredCommand, bool hasPassword)
    {
        var quoted = ShellQuote(connector);
        return (configuredCommand.ToLowerInvariant(), hasPassword) switch
        {
            ("sudo -i", true) => $"sudo -S -p '' -i sh -c {quoted}",
            ("sudo -i", false) => $"sudo -n -i sh -c {quoted}",
            ("sudo su" or "sudo su -", true) => $"sudo -S -p '' su - -c {quoted}",
            ("sudo su" or "sudo su -", false) => $"sudo -n su - -c {quoted}",
            ("su -", _) => $"su - -c {quoted}",
            _ => $"su -c {quoted}"
        };
    }

    private static string BuildRemoteCommand(string host, int port) =>
        $"host={ShellQuote(host)}; port={port}; " +
        "if command -v nc >/dev/null 2>&1; then exec nc \"$host\" \"$port\"; " +
        "elif command -v ncat >/dev/null 2>&1; then exec ncat \"$host\" \"$port\"; " +
        "elif command -v socat >/dev/null 2>&1; then exec socat - \"TCP:$host:$port\"; " +
        "elif command -v bash >/dev/null 2>&1; then " +
        "exec bash -c 'exec 3<>\"/dev/tcp/$1/$2\"; cat <&3 & cat >&3; wait' _ \"$host\" \"$port\"; " +
        "else exit 127; fi";
}

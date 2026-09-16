using System.Diagnostics;

namespace KiTTYManager.Core;

public static class RouteCandidateConnector
{
    public static async Task<ActiveRoute> ConnectCandidatesAsync(
        ManagerConfig config,
        ManagedServer server,
        IReadOnlyList<RouteCandidate> candidates,
        SshConnectionService ssh,
        CancellationToken cancellationToken,
        bool consoleOnly = false,
        Guid? forcedViaServerId = null,
        Action<SshConnectionProgress>? progress = null,
        RouteAttemptBudget? budget = null,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureManagedJumphost = null,
        Func<BaseProxy, TimeSpan, CancellationToken, Task<bool>>? isSocks5Ready = null,
        Func<IReadOnlyList<RouteCandidate>, CancellationToken, Task<bool>>? tryRestoreAccess = null,
        Action<string>? status = null,
        Action<string>? routeLog = null,
        Action<Guid, Guid>? onHopFailed = null,
        Action<(ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration)>? onRouteSucceeded = null,
        Func<RouteCandidate, CancellationToken, Task>? onPreferredRouteFailed = null)
    {
        budget ??= new RouteAttemptBudget(config.MaxRouteAttempts);
        var errors = new List<Exception>();
        var proxyReady = new Dictionary<Guid, bool>();
        var proxyErrors = new Dictionary<Guid, Exception>();
        var racedCandidates = new HashSet<RouteCandidate>();
        var raceAttempted = false;

        var sequentialPriority = candidates.Take(1)
            .Concat(candidates.Skip(1).TakeWhile(candidate => candidate.Servers.Count > 1))
            .ToHashSet();

        for (var candidateIdx = 0; candidateIdx < candidates.Count; candidateIdx++)
        {
            var candidate = candidates[candidateIdx];
            var attemptIndex = candidateIdx + 1;
            var attemptCount = candidates.Count;
            if (racedCandidates.Contains(candidate)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.HasCapacity) break;
            var candidateSw = Stopwatch.StartNew();

            if (!proxyReady.TryGetValue(candidate.Proxy.Id, out var ready))
            {
                if (!candidate.WithoutProxy && candidate.Proxy.StartupServerId is not null)
                {
                    progress?.Invoke(new SshConnectionProgress(
                        SshConnectionProgressKind.AttemptStarting,
                        attemptIndex, attemptCount, RouteAttemptFormatter.FormatCandidate(candidate),
                        TimeoutSeconds: (int)Math.Ceiling(ssh.Timeout.TotalSeconds)));
                    progress?.Invoke(new SshConnectionProgress(
                        SshConnectionProgressKind.EndpointProbing,
                        attemptIndex, attemptCount, RouteAttemptFormatter.FormatCandidate(candidate),
                        EndpointDisplay: $"{candidate.Proxy.Name} [{candidate.Proxy.Host}:{candidate.Proxy.Port}]"));
                }

                ready = candidate.WithoutProxy || (isSocks5Ready is not null && await isSocks5Ready(
                    candidate.Proxy, TimeSpan.FromSeconds(1), cancellationToken));

                if (!ready && candidate.Proxy.StartupServerId is not null && ensureManagedJumphost is not null)
                {
                    status?.Invoke($"Для маршрута к «{server.Name}» запускаю точку входа «{candidate.Proxy.Name}»…");
                    try
                    {
                        ready = await ensureManagedJumphost(candidate.Proxy, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                        proxyErrors[candidate.Proxy.Id] = ex;
                        routeLog?.Invoke($"Jumphost startup probe failed: proxy={candidate.Proxy.Name}; error={ex.GetType().Name}: {ex.Message}");
                        progress?.Invoke(new SshConnectionProgress(
                            SshConnectionProgressKind.AttemptFailed,
                            attemptIndex, attemptCount, RouteAttemptFormatter.FormatCandidate(candidate),
                            Duration: candidateSw.Elapsed,
                            ErrorMessage: $"сбой запуска точки входа {candidate.Proxy.Name}: {ex.Message}"));
                        ready = false;
                    }
                }

                proxyReady[candidate.Proxy.Id] = ready;
                if (!ready)
                {
                    budget.TryAcquire();
                }
            }

            if (!ready)
            {
                if (!proxyErrors.ContainsKey(candidate.Proxy.Id))
                {
                    var notReadyEx = new InvalidOperationException(
                        $"Точка входа «{candidate.Proxy.Name}» ({candidate.Proxy.Host}:{candidate.Proxy.Port}) недоступна.");
                    errors.Add(notReadyEx);
                    progress?.Invoke(new SshConnectionProgress(
                        SshConnectionProgressKind.AttemptFailed,
                        attemptIndex, attemptCount, RouteAttemptFormatter.FormatCandidate(candidate),
                        Duration: candidateSw.Elapsed,
                        ErrorMessage: $"точка входа «{candidate.Proxy.Name}» ({candidate.Proxy.Host}:{candidate.Proxy.Port}) недоступна"));
                }
                continue;
            }

            // Shorter candidate race
            if (candidate.Servers.Count > 1 && !racedCandidates.Contains(candidate))
            {
                var shorter = candidates
                    .Where(item => !racedCandidates.Contains(item) &&
                                   !ReferenceEquals(item, candidate) &&
                                   item.Servers.Count < candidate.Servers.Count)
                    .OrderBy(item => item.Servers.Count)
                    .FirstOrDefault();

                if (shorter is not null)
                {
                    var shorterReady = shorter.WithoutProxy ||
                        (proxyReady.TryGetValue(shorter.Proxy.Id, out var pr)
                            ? pr
                            : (isSocks5Ready is not null && await isSocks5Ready(shorter.Proxy, TimeSpan.FromSeconds(1), cancellationToken)));

                    if (shorterReady)
                    {
                        if (budget.Remaining >= 2)
                        {
                            racedCandidates.Add(candidate);
                            racedCandidates.Add(shorter);
                            try
                            {
                                status?.Invoke($"Параллельно проверяю короткий путь «{RouteLabel(shorter)}» и текущий «{RouteLabel(candidate)}» для «{server.Name}»…");
                                routeLog?.Invoke($"Shorter route race started: target={server.Name}; shorter={RouteLabel(shorter)}; candidate={RouteLabel(candidate)}");
                                var shorterIndex = candidates.TakeWhile(c => !ReferenceEquals(c, shorter)).Count() + 1;
                                if (shorterIndex > attemptCount) shorterIndex = attemptIndex;
                                var raced = await ssh.ConnectFirstSuccessfulAsync(
                                    config, [shorter, candidate], cancellationToken, consoleOnly,
                                    progress: progress,
                                    candidateIndices: [shorterIndex, attemptIndex],
                                    totalAttempts: attemptCount,
                                    budget: budget);
                                if (ReferenceEquals(raced.Candidate, shorter))
                                    routeLog?.Invoke($"Shorter route race WON: target={server.Name}; winner={RouteLabel(shorter)}");
                                else
                                    routeLog?.Invoke($"Primary candidate finished first: target={server.Name}; winner={RouteLabel(candidate)}");
                                onRouteSucceeded?.Invoke(raced);
                                return raced.Route;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception raceError)
                            {
                                errors.Add(raceError);
                                var failedHops = ExceptionChain(raceError).OfType<SshHopException>()
                                    .DistinctBy(h => (h.SourceId, h.TargetId)).ToArray();
                                if (failedHops.Length > 0 && onHopFailed is not null)
                                {
                                    foreach (var hop in failedHops)
                                        onHopFailed(hop.SourceId, hop.TargetId);
                                }
                                routeLog?.Invoke($"Shorter route race failed: target={server.Name}; shorter={RouteLabel(shorter)}; candidate={RouteLabel(candidate)}; error={raceError.GetType().Name}: {raceError.Message}");
                                continue;
                            }
                        }
                    }
                }
            }

            // Entry point race
            if (config.RaceBestEntryPoints && !candidate.WithoutProxy && !raceAttempted &&
                !sequentialPriority.Contains(candidate) && candidate.Servers.Count == 1)
            {
                var second = candidates.FirstOrDefault(item =>
                    !ReferenceEquals(item, candidate) &&
                    !sequentialPriority.Contains(item) &&
                    item.Servers.Count == 1 &&
                    item.Proxy.Id != candidate.Proxy.Id);
                if (second is not null && isSocks5Ready is not null &&
                    await isSocks5Ready(second.Proxy, TimeSpan.FromSeconds(1), cancellationToken))
                {
                    if (budget.Remaining >= 2)
                    {
                        raceAttempted = true;
                        racedCandidates.Add(candidate);
                        racedCandidates.Add(second);
                        try
                        {
                            status?.Invoke($"Параллельно проверяю две точки входа для «{server.Name}»…");
                            var secondIndex = candidates.TakeWhile(c => !ReferenceEquals(c, second)).Count() + 1;
                            if (secondIndex > attemptCount) secondIndex = attemptIndex;
                            var raced = await ssh.ConnectFirstSuccessfulAsync(
                                config, [candidate, second], cancellationToken, consoleOnly,
                                progress: progress,
                                candidateIndices: [attemptIndex, secondIndex],
                                totalAttempts: attemptCount,
                                budget: budget);
                            onRouteSucceeded?.Invoke(raced);
                            return raced.Route;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error)
                        {
                            errors.Add(error);
                            if (ExceptionChain(error).OfType<SshHopException>().FirstOrDefault() is { } hop && onHopFailed is not null)
                            {
                                onHopFailed(hop.SourceId, hop.TargetId);
                            }
                            routeLog?.Invoke($"Entry point race failed: target={server.Name}; routes={RouteLabel(candidate)} | {RouteLabel(second)}; error={error.GetType().Name}: {error.Message}");
                            continue;
                        }
                    }
                }
            }

            try
            {
                status?.Invoke($"Проверяю маршрут: {RouteLabel(candidate)}…");
                var connected = await ssh.ConnectCandidateAsync(
                    config, candidate, cancellationToken, consoleOnly,
                    rememberTargetPreference: forcedViaServerId is null,
                    progress: progress,
                    attemptIndex: attemptIndex,
                    attemptCount: attemptCount,
                    budget: budget);
                onRouteSucceeded?.Invoke(connected);
                return connected.Route;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                errors.Add(error);
                if (ExceptionChain(error).OfType<SshHopException>().FirstOrDefault() is { } hop && onHopFailed is not null)
                {
                    onHopFailed(hop.SourceId, hop.TargetId);
                }
                var detail = string.Join(" --> ", ExceptionChain(error).Select(e => $"{e.GetType().Name}: {e.Message}"));
                routeLog?.Invoke($"Route failed: target={server.Name}; route={RouteLabel(candidate)}; error={detail}");
                if (forcedViaServerId is null && server.PreferredRoute?.ProxyId == candidate.Proxy.Id && onPreferredRouteFailed is not null)
                {
                    _ = onPreferredRouteFailed(candidate, cancellationToken);
                }
            }
        }

        // Mechanism B: all routes failed. Check if access script expired.
        if (budget.HasCapacity && tryRestoreAccess is not null && await tryRestoreAccess(candidates, cancellationToken))
        {
            var retryProxyReady = new Dictionary<Guid, bool>();
            for (var retryIdx = 0; retryIdx < candidates.Count; retryIdx++)
            {
                var candidate = candidates[retryIdx];
                cancellationToken.ThrowIfCancellationRequested();
                if (!budget.HasCapacity) break;
                if (!candidate.WithoutProxy)
                {
                    if (!retryProxyReady.TryGetValue(candidate.Proxy.Id, out var rReady))
                    {
                        rReady = isSocks5Ready is not null && await isSocks5Ready(candidate.Proxy, TimeSpan.FromSeconds(1), cancellationToken);
                        retryProxyReady[candidate.Proxy.Id] = rReady;
                        if (!rReady)
                        {
                            budget.TryAcquire();
                        }
                    }
                    if (!rReady) continue;
                }
                try
                {
                    status?.Invoke($"Повторяю маршрут: {RouteLabel(candidate)}…");
                    var connected = await ssh.ConnectCandidateAsync(
                        config, candidate, cancellationToken, consoleOnly,
                        rememberTargetPreference: forcedViaServerId is null,
                        progress: progress,
                        attemptIndex: retryIdx + 1,
                        attemptCount: candidates.Count,
                        budget: budget);
                    onRouteSucceeded?.Invoke(connected);
                    return connected.Route;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    errors.Add(error);
                    if (ExceptionChain(error).OfType<SshHopException>().FirstOrDefault() is { } hop && onHopFailed is not null)
                    {
                        onHopFailed(hop.SourceId, hop.TargetId);
                    }
                    routeLog?.Invoke($"Route retry failed: target={server.Name}; route={RouteLabel(candidate)}; error={error.GetType().Name}: {error.Message}");
                }
            }
        }

        if (!budget.HasCapacity)
        {
            var limitMsg = RouteAttemptLimitException.FormatMessage(budget.Limit);
            routeLog?.Invoke(limitMsg);
            throw new RouteAttemptLimitException(budget.Limit, budget.AttemptCount,
                errors.Count == 0 ? null : new AggregateException(errors));
        }

        throw new InvalidOperationException(
            forcedViaServerId is Guid failedVia
                ? $"Не удалось подключиться через выбранный сервер «{config.FindServer(failedVia)?.Name ?? failedVia.ToString()}». Обычный маршрут не использован."
                : "Не найден рабочий маршрут. Запустите ручную проверку связности.",
            errors.Count == 0 ? null : new AggregateException(errors));
    }

    private static string RouteLabel(RouteCandidate candidate) =>
        (candidate.WithoutProxy
            ? "Прямо без JH → "
            : $"{candidate.Proxy.Name}:{candidate.Proxy.Port} → ") +
        string.Join(" → ", candidate.Servers.Select(item => item.Name));

    private static IEnumerable<Exception> ExceptionChain(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    foreach (var chain in ExceptionChain(inner))
                        yield return chain;
            }
        }
    }
}

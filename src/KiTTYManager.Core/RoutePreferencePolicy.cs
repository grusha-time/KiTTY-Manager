namespace KiTTYManager.Core;

public static class RoutePreferencePolicy
{
    public static IReadOnlyList<RouteCandidate> Order(
        IReadOnlyList<RouteCandidate> rankedCandidates, CachedRoute? preferred)
    {
        if (preferred is null) return rankedCandidates;
        var index = IndexOf(rankedCandidates, preferred);
        if (index < 0) return rankedCandidates;
        return [rankedCandidates[index], .. rankedCandidates.Skip(index + 1), .. rankedCandidates.Take(index)];
    }

    public static IReadOnlyList<RouteCandidate> BetterCandidates(
        IReadOnlyList<RouteCandidate> rankedCandidates, RouteCandidate current)
    {
        var index = IndexOf(rankedCandidates, current);
        return index <= 0 ? [] : rankedCandidates.Take(index).ToArray();
    }

    public static bool Matches(CachedRoute? cached, RouteCandidate candidate) =>
        cached is not null && cached.ProxyId == candidate.Proxy.Id &&
        cached.ServerIds.SequenceEqual(candidate.Servers.Select(server => server.Id));

    /// <summary>
    /// SSH-сервис сам запоминает успешно проверенный кандидат до возврата
    /// вызывающему коду. Фоновая задача может сохранить результат, если маршрут
    /// всё ещё исходный либо уже заменён именно её собственным кандидатом.
    /// </summary>
    public static bool CanCommitBackgroundResult(
        CachedRoute? observed, RouteCandidate original, RouteCandidate candidate) =>
        Matches(observed, original) || Matches(observed, candidate);

    public static CachedRoute FromSuccess(
        RouteCandidate candidate, TimeSpan duration, DateTimeOffset now) => new()
    {
        ProxyId = candidate.Proxy.Id,
        ServerIds = candidate.Servers.Select(server => server.Id).ToList(),
        LatencyMs = duration.TotalMilliseconds,
        LastSuccessUtc = now
    };

    /// <summary>
    /// Решает, можно ли перезаписать сохранённый маршрут новым. Проверка связей не
    /// должна затирать подтверждённый короткий/прямой маршрут длинной цепочкой, по
    /// которой цель просто оказалась проверена: заменяем только если новый маршрут
    /// короче (или сохранённого ещё нет).
    /// </summary>
    public static bool ShouldReplacePreferred(CachedRoute? existing, IReadOnlyList<Guid> newServerIds) =>
        existing is null || newServerIds.Count < existing.ServerIds.Count;

    private static int IndexOf(IReadOnlyList<RouteCandidate> candidates, CachedRoute preferred)
    {
        for (var index = 0; index < candidates.Count; index++)
            if (Matches(preferred, candidates[index])) return index;
        return -1;
    }

    private static int IndexOf(IReadOnlyList<RouteCandidate> candidates, RouteCandidate selected)
    {
        for (var index = 0; index < candidates.Count; index++)
            if (candidates[index].Proxy.Id == selected.Proxy.Id &&
                candidates[index].Servers.Select(server => server.Id)
                    .SequenceEqual(selected.Servers.Select(server => server.Id))) return index;
        return -1;
    }
}

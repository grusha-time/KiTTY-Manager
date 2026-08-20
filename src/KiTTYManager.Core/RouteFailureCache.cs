namespace KiTTYManager.Core;

public sealed class RouteFailureCache
{
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(90);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Guid ProxyId, Guid ServerId), DateTimeOffset> failures = new();

    public bool ShouldSkip(RouteCandidate candidate, DateTimeOffset now)
    {
        if (candidate.Servers.Count != 1) return false;
        return failures.TryGetValue((candidate.Proxy.Id, candidate.Servers[0].Id), out var failedAt) &&
               now - failedAt < FailureTtl;
    }

    public void RememberDirectFailure(RouteCandidate candidate, DateTimeOffset now)
    {
        if (candidate.Servers.Count == 1)
            failures[(candidate.Proxy.Id, candidate.Servers[0].Id)] = now;
    }

    public void ClearSuccess(RouteCandidate candidate)
    {
        if (candidate.Servers.Count == 1)
            failures.TryRemove((candidate.Proxy.Id, candidate.Servers[0].Id), out _);
    }

    public void Clear() => failures.Clear();
}

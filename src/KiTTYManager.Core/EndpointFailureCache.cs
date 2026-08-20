namespace KiTTYManager.Core;

/// <summary>
/// Коротко запоминает недоступность конкретного ip:port только в том контексте,
/// где зонд отказал: JH→сервер либо предыдущий сервер→следующий сервер.
/// </summary>
public sealed class EndpointFailureCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);
    private readonly TimeSpan ttl;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Key, DateTimeOffset> failures = new();

    public EndpointFailureCache(TimeSpan? ttl = null) => this.ttl = ttl ?? DefaultTtl;

    public bool ShouldSkip(
        Guid serverId, EndpointContext context, ServerEndpoint endpoint, DateTimeOffset now) =>
        failures.TryGetValue(CreateKey(serverId, context, endpoint), out var failedAt) &&
        now - failedAt < ttl;

    public void RememberFailure(
        Guid serverId, EndpointContext context, ServerEndpoint endpoint, DateTimeOffset now) =>
        failures[CreateKey(serverId, context, endpoint)] = now;

    public void ClearSuccess(Guid serverId, EndpointContext context, ServerEndpoint endpoint) =>
        failures.TryRemove(CreateKey(serverId, context, endpoint), out _);

    public void Clear() => failures.Clear();

    private static Key CreateKey(
        Guid serverId, EndpointContext context, ServerEndpoint endpoint) =>
        new(serverId, context.ProxyId, context.PreviousServerId,
            endpoint.Host.Trim().ToUpperInvariant(), endpoint.Port);

    private readonly record struct Key(
        Guid ServerId, Guid? ProxyId, Guid? PreviousServerId, string Host, int Port);
}

namespace KiTTYManager.Core;

/// <summary>
/// Формирует порядок адресов подключения к серверу. Последний рабочий адрес
/// запоминается отдельно для JH→сервер и предыдущий сервер→следующий сервер,
/// затем идут основной Host:Port и резервные. Пустой Host в резервной записи
/// подставляется из основного хоста сервера.
/// </summary>
public static class ServerEndpointPolicy
{
    /// <summary>Основной адрес сервера (хост без префикса «user@» + порт сессии).</summary>
    public static ServerEndpoint Main(ManagedServer server) => new(server.CleanHost, server.Port);

    /// <summary>
    /// Упорядоченный список адресов для заданного контекста. Дубликаты
    /// (резервный адрес, совпавший с основным) отбрасываются, чтобы не стучаться
    /// в одну точку дважды.
    /// </summary>
    public static IReadOnlyList<ServerEndpoint> Ordered(
        ManagedServer server, EndpointContext? context = null)
    {
        var ordered = new List<ServerEndpoint> { Main(server) };
        foreach (var backup in server.BackupEndpoints)
        {
            if (backup.Port <= 0) continue;
            var resolved = Resolve(server, backup);
            if (!ordered.Contains(resolved)) ordered.Add(resolved);
        }

        // Запомненный адрес может лишь переупорядочить текущий набор
        // {основной}∪{резервные}. Если его там больше нет (пользователь поменял
        // основной порт или удалил/изменил резервный), он игнорируется — иначе
        // правка адреса не применялась бы и менеджер ходил по устаревшему порту.
        var contextual = context is null
            ? null
            : server.EndpointPreferences.FirstOrDefault(item =>
                item.ProxyId == context.Value.ProxyId &&
                item.PreviousServerId == context.Value.PreviousServerId)?.Endpoint;
        var preferred = contextual ??
            (server.EndpointPreferences.Count == 0 ? server.PreferredEndpoint : null);
        if (preferred is { Port: > 0 } pref)
        {
            var resolvedPreferred = Resolve(server, pref);
            var index = ordered.IndexOf(resolvedPreferred);
            if (index > 0)
            {
                ordered.RemoveAt(index);
                ordered.Insert(0, resolvedPreferred);
            }
        }
        return ordered;
    }

    /// <summary>Запоминает сработавший адрес для заданного контекста.</summary>
    public static void Remember(
        ManagedServer server, ServerEndpoint effective, EndpointContext? context = null)
    {
        server.PreferredEndpoint = new ServerEndpoint(effective.Host, effective.Port);
        if (context is null) return;
        var preference = server.EndpointPreferences.FirstOrDefault(item =>
            item.ProxyId == context.Value.ProxyId &&
            item.PreviousServerId == context.Value.PreviousServerId);
        if (preference is null)
        {
            preference = new EndpointPreference
            {
                ProxyId = context.Value.ProxyId,
                PreviousServerId = context.Value.PreviousServerId
            };
            server.EndpointPreferences.Add(preference);
        }
        preference.Endpoint = new ServerEndpoint(effective.Host, effective.Port);
        preference.LastSuccessUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Нужно ли перепроверить основной адрес в фоне: да, если запомнен рабочий
    /// адрес, отличающийся от основного (значит, мы «сидим» на резервном и стоит
    /// узнать, не ожил ли основной).
    /// </summary>
    public static bool ShouldReprobeMain(ManagedServer server) =>
        server.PreferredEndpoint is { Port: > 0 } pref && !pref.Equals(Main(server));

    private static ServerEndpoint Resolve(ManagedServer server, ServerEndpoint endpoint) =>
        new(string.IsNullOrWhiteSpace(endpoint.Host) ? server.CleanHost : endpoint.Host.Trim(),
            endpoint.Port);
}

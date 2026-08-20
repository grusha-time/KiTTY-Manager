namespace KiTTYManager.Core;

/// <summary>
/// Единственный источник заголовков консолей KiTTY: по ним же менеджер
/// ищет уже открытые консоли, поэтому заголовки детерминированы и не
/// содержат случайных суффиксов.
/// </summary>
public static class JumphostConsoleTitles
{
    public static string EntryTitle(string serverName, string proxyName) =>
        $"{serverName} — точка входа {proxyName}";

    public static string AccessTitle(string serverName, string proxyName) =>
        $"{serverName} — скрипт доступа {proxyName}";
}

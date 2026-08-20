namespace KiTTYManager.Core;

public sealed record ConsoleCandidate(int ProcessId, DateTime StartTimeUtc);

/// <summary>
/// Уборка дублей: из живых консолей с одинаковым заголовком оставляем самую
/// свежую, все остальные считаются лишними.
/// </summary>
public static class JumphostConsoleCleanupPolicy
{
    public static IReadOnlyList<ConsoleCandidate> ChooseExtras(IReadOnlyList<ConsoleCandidate> alive) =>
        alive.Count <= 1
            ? []
            : alive.OrderByDescending(item => item.StartTimeUtc)
                   .ThenBy(item => item.ProcessId)
                   .Skip(1)
                   .ToList();
}

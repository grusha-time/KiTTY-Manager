namespace KiTTYManager.Core;

public sealed class RouteAttemptLimitException : InvalidOperationException
{
    public int AttemptLimit { get; }
    public int AttemptCount { get; }

    public RouteAttemptLimitException(int attemptLimit, int attemptCount, Exception? innerException = null)
        : base(FormatMessage(attemptLimit), innerException)
    {
        AttemptLimit = attemptLimit;
        AttemptCount = attemptCount;
    }

    public static string FormatMessage(int attemptLimit) =>
        $"Перебрали {attemptLimit} вариантов маршрутов, ни один не сработал. Если нужно увеличить количество возможных вариантов, это можно сделать в настройках (пункт меню «Настройки» → блок «Подключение и сеть»).";
}

public sealed class RouteAttemptBudget
{
    private int remaining;
    public int Limit { get; }
    public int AttemptCount => Limit - Math.Max(0, Volatile.Read(ref remaining));

    public RouteAttemptBudget(int limit)
    {
        Limit = NormalizeLimit(limit);
        remaining = Limit;
    }

    public static int NormalizeLimit(int value) => Math.Clamp(value <= 0 ? 20 : value, 1, 100);

    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref remaining);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
                return true;
        }
    }

    public int Remaining => Math.Max(0, Volatile.Read(ref remaining));

    public bool HasCapacity => Volatile.Read(ref remaining) > 0;
}

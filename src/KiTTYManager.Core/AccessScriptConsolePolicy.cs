namespace KiTTYManager.Core;

public enum AccessScriptConsoleOwner
{
    NoListener,
    UnknownKitty,
    NonKitty
}

public enum AccessScriptConsoleTarget
{
    ManagedMain,
    ExistingAccess,
    PromptUnknown,
    Isolated
}

public static class AccessScriptConsolePolicy
{
    public static bool ShouldAutoAdoptAfterSuccessfulPreflight(
        AccessScriptConsoleOwner owner, bool titleMatches, int? processId,
        bool processAlive) =>
        owner == AccessScriptConsoleOwner.UnknownKitty &&
        titleMatches && processId is not null && processAlive;

    /// <summary>
    /// Скрипт доступа никогда не открывает дубль: любая своя открытая консоль
    /// (основная управляемая или ранее открытая служебная) принимает команду.
    /// Отдельная новая консоль открывается только когда своей нет вовсе,
    /// а вопрос задаётся лишь про действительно неизвестную KiTTY на SOCKS-порту.
    /// </summary>
    public static AccessScriptConsoleTarget DecideConsole(
        bool managedMainAlive, bool accessConsoleAlive, AccessScriptConsoleOwner socksOwner)
    {
        if (managedMainAlive) return AccessScriptConsoleTarget.ManagedMain;
        if (accessConsoleAlive) return AccessScriptConsoleTarget.ExistingAccess;
        return socksOwner == AccessScriptConsoleOwner.UnknownKitty
            ? AccessScriptConsoleTarget.PromptUnknown
            : AccessScriptConsoleTarget.Isolated;
    }

    public static DateTimeOffset PromptEligibleUtc(
        DateTimeOffset cancelledAt, int scheduledRestartMinutes) =>
        cancelledAt + TimeSpan.FromMinutes(Math.Max(1, scheduledRestartMinutes));

    public static bool IsPromptSnoozed(
        DateTimeOffset? cancelledAt, int scheduledRestartMinutes, DateTimeOffset now) =>
        cancelledAt is { } cancelled &&
        PromptEligibleUtc(cancelled, scheduledRestartMinutes) > now;
}

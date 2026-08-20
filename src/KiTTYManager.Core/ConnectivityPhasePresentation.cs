namespace KiTTYManager.Core;

public static class ConnectivityPhasePresentation
{
    public static string Progress(int phase, int phaseCount, int phaseCompleted, int phaseTotal,
        int phaseSuccessful, int overallCompleted, int overallTotal) =>
        $"Этап {phase} из {phaseCount}: {phaseCompleted} из {phaseTotal}; " +
        $"доступно {phaseSuccessful}. Всего проверено {overallCompleted} из {overallTotal}.";

    public static string DeferredPrompt(int primaryChecked, int primarySuccessful,
        IReadOnlyList<string> unavailable, IReadOnlyList<string> dependentNames)
    {
        var unavailableText = unavailable.Count == 0
            ? "Недоступных серверов нет."
            : $"Недоступно: {unavailable.Count}\n" +
              string.Join(Environment.NewLine, unavailable.Select(value => $"• {value}"));
        var names = string.Join(Environment.NewLine, dependentNames.Select(name => $"• {name}"));
        return $"Первый этап завершён. Проверено: {primaryChecked}; доступно: {primarySuccessful}.\n\n" +
               $"{unavailableText}\n\n" +
               $"Зависимые серверы: {dependentNames.Count}\n\n{names}\n\n" +
               "Можно завершить сейчас: уже найденные связи сохранены и будут работать, но карта останется неполной.\n" +
               "Или можно продолжить и проверить зависимые серверы для полной карты.";
    }
}

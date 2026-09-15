using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private static void HelpSearchFindsFieldsAcrossSections()
    {
        Equal(HelpContent.Entries.Count, HelpContent.Search(null).Count);
        Equal(HelpContent.Entries.Count, HelpContent.Search(" \t ", HelpContent.AllSections).Count);
        var session = HelpContent.Search("", "Сессия");
        Equal(true, session.Count > 0);
        Equal(true, session.All(entry => entry.Section == "Сессия"));
        // A query is global even when the sidebar is on a different section.
        var whpx = HelpContent.Search("whpx", "Сессия");
        Equal(true, whpx.Any(entry => entry.Section == "Ansible"));
        Equal(true, whpx.SequenceEqual(HelpContent.Search("WHPX")));
        var acrossFields = HelpContent.Search("  ключ\tSSH\nпуть ", "Веб-интерфейсы");
        Equal(true, acrossFields.Any(entry => entry.Section == "Сессия" && entry.Title == "Ключ SSH"));
        Equal(0, HelpContent.Search("WHPX nonexistent-field-unique").Count);
        Equal(0, HelpContent.Search("nonexistent-field-unique").Count);
        Equal(true, HelpContent.Search("localhost").Any(entry => entry.Title.Contains("delegate_to")));
        Equal(true, HelpContent.Search("InternalOnly").Any(entry => entry.Title.Contains("Внутренняя подсеть")));
        Equal(true, HelpContent.Search(".kmtask").Any(entry => entry.Section == "Массовые задачи"));
        Equal(true, HelpContent.Entries.All(entry => !string.IsNullOrWhiteSpace(entry.Title) &&
            !string.IsNullOrWhiteSpace(entry.Description) && !string.IsNullOrWhiteSpace(entry.Section)));
        Equal(HelpContent.Entries.Count, HelpContent.Entries.Select(entry => (entry.Section, entry.Title)).Distinct().Count());

        // Settings sections are properly structured into 4 groups
        var extPrograms = HelpContent.Search("", "Настройки — Внешние программы");
        Equal(true, extPrograms.Any(e => e.Title.Contains("KiTTY")));
        var firefoxGroup = HelpContent.Search("", "Настройки — Firefox и веб-панели");
        Equal(true, firefoxGroup.Any(e => e.Title.Contains("Firefox")));
        Equal(true, firefoxGroup.Any(e => e.Title.Contains("веб-туннеля")));
        Equal(true, firefoxGroup.Any(e => e.Title.Contains("Оптимизация")));
        var connGroup = HelpContent.Search("", "Настройки — Подключение и сеть");
        Equal(true, connGroup.Any(e => e.Title.Contains("Таймаут")));
        Equal(true, connGroup.Any(e => e.Title.Contains("точки входа")));
        var appGroup = HelpContent.Search("", "Настройки — Поведение приложения и KiTTY");
        Equal(true, appGroup.Any(e => e.Title.Contains("трей")));
        Equal(true, appGroup.Any(e => e.Title.Contains("журнал")));
        Equal(true, appGroup.Any(e => e.Title.Contains("развёрнутыми")));
    }
}

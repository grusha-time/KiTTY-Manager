using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ImportWizardDialog : Window
{
    private readonly ManagerConfig current;
    private readonly ManagerConfig incoming;
    private Dictionary<Guid, (Guid? CurrentId, ImportDecision Decision)> sessionState;
    public ImportWizardPlan Plan { get; }
    public ManagerConfig? ResultConfig { get; private set; }

    public ImportWizardDialog(ManagerConfig current, ManagerConfig incoming)
    {
        this.current = current;
        this.incoming = incoming;
        Plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        sessionState = CaptureSessionState();
        InitializeComponent();
        DataContext = Plan;
        UpdateSummary();
    }

    private void SelectNew_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Plan.Sessions)
            row.Decision = row.CurrentId is null ? ImportDecision.Add : ImportDecision.KeepCurrent;
        foreach (var row in Plan.Proxies)
            row.Decision = row.CurrentId is null ? ImportDecision.Add : ImportDecision.KeepCurrent;
        foreach (var row in Plan.Links)
            row.Decision = row.ExistingConflict ? ImportDecision.KeepCurrent : ImportDecision.Add;
        Refresh();
    }

    private void SkipAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Plan.Sessions) row.Decision = ImportDecision.Skip;
        foreach (var row in Plan.Proxies) row.Decision = ImportDecision.Skip;
        foreach (var row in Plan.Links) row.Decision = ImportDecision.Skip;
        Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SessionsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SessionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        FieldsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        FieldsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ProxyGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ProxyGrid.CommitEdit(DataGridEditingUnit.Row, true);
        LinksGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinksGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PlanGrid_Changed(null, EventArgs.Empty);
        try { ImportWizardEngine.ValidateGroupChoices(Plan); }
        catch (InvalidDataException ex)
        {
            ThemedMessageDialog.Show(this, ex.Message, "Конфликт имён групп",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Plan.Links.Any(x => x.ExistingConflict && x.Decision == ImportDecision.Add))
        {
            ThemedMessageDialog.Show(this,
                "Существующую связь нельзя добавить второй раз. Выберите «Оставить текущую», «Взять данные из файла» или «Не импортировать».",
                "Конфликт связи", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var row in Plan.Links.Where(x => x.Decision is ImportDecision.Add or ImportDecision.UseIncoming))
        {
            var link = incoming.Links[row.IncomingIndex];
            if (Plan.Sessions.Any(x => (x.IncomingId == link.FromServerId || x.IncomingId == link.ToServerId) &&
                                       x.Decision == ImportDecision.Skip))
            {
                ThemedMessageDialog.Show(this,
                    $"Связь «{row.FromName} → {row.ToName}» выбрана, но одна из её сессий не импортируется.",
                    "Неполное сопоставление", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        UpdateSummary();
        var confirmation = $"Будет добавлено сессий: {Plan.Sessions.Count(x => x.Decision == ImportDecision.Add)}; " +
            $"обновлено сессий целиком: {Plan.Sessions.Count(x => x.Decision == ImportDecision.UseIncoming)}; " +
            $"выбрано отдельных полей: {Plan.Fields.Count(x => x.UseIncoming)}; " +
            $"групп добавлено в существующие: {Plan.Groups.Count(x => x.Decision != ImportGroupDecision.CreateNew)}; " +
            $"добавлено или обновлено JH: {Plan.Proxies.Count(x => x.Decision is ImportDecision.Add or ImportDecision.UseIncoming)}; " +
            $"добавлено или обновлено связей: {Plan.Links.Count(x => x.Decision is ImportDecision.Add or ImportDecision.UseIncoming)}.\n\n" +
            "Локальные записи не удаляются. Применить изменения?";
        if (ThemedMessageDialog.Show(this, confirmation, "Итог импорта",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ResultConfig = ConfigTransfer.MergeSmartImport(current, incoming, Plan);
        DialogResult = true;
    }

    private void Refresh()
    {
        SessionsGrid.Items.Refresh();
        PlanGrid_Changed(null, EventArgs.Empty);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void ImportTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ImportTabs)) return;
        // Без явного коммита неотправленные правки (галочки, выпадающие списки)
        // теряются при скрытии вкладки — DataGrid отменяет незавершённое редактирование.
        CommitAllGrids();
        ApplySearch();
    }

    private void CommitAllGrids()
    {
        foreach (var grid in new[] { SessionsGrid, FieldsGrid, GroupsGrid, ProxyGrid, LinksGrid })
        {
            if (grid is null) continue;
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private void ApplySearch()
    {
        if (SearchBox is null || ImportTabs is null) return;
        var grid = ImportTabs.SelectedIndex switch
        {
            0 => SessionsGrid,
            1 => FieldsGrid,
            2 => GroupsGrid,
            3 => ProxyGrid,
            4 => LinksGrid,
            _ => null
        };
        if (grid is null) return;
        var query = SearchBox.Text;
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is null) return;
        view.Filter = item => ImportSearchPolicy.Matches(item, query);
        view.Refresh();
    }

    private void PlanGrid_Changed(object? sender, EventArgs e)
    {
        // «Не сопоставлять» хранится как Guid.Empty в комбобоксе — приводим к null.
        foreach (var row in Plan.Sessions)
            if (row.CurrentId == ImportWizardEngine.NoMatchCandidateId) row.CurrentId = null;
        var state = CaptureSessionState();
        if (!state.OrderBy(x => x.Key).SequenceEqual(sessionState.OrderBy(x => x.Key)))
        {
            var becameFull = state
                .Where(kv => kv.Value.Decision == ImportDecision.UseIncoming &&
                             sessionState.TryGetValue(kv.Key, out var old) &&
                             old.Decision != ImportDecision.UseIncoming)
                .Select(kv => kv.Key).ToArray();
            // Ручной выбор полей (и снятые галочки) переживает пересборку зависимостей.
            var previousFields = Plan.Fields
                .ToDictionary(x => (x.IncomingSessionId, x.PropertyName), x => x.UseIncoming);
            ImportWizardEngine.RefreshSessionDependencies(current, incoming, Plan);
            foreach (var field in Plan.Fields)
                if (previousFields.TryGetValue((field.IncomingSessionId, field.PropertyName), out var was))
                    field.UseIncoming = was;
            if (becameFull.Length > 0)
            {
                ImportWizardEngine.ApplyFullImportCascade(Plan, becameFull);
                FieldsGrid.Items.Refresh();
            }
            sessionState = state;
            FieldsGrid.Items.Refresh();
            GroupsGrid.Items.Refresh();
            LinksGrid.Items.Refresh();
        }
        UpdateSummary();
    }

    private Dictionary<Guid, (Guid? CurrentId, ImportDecision Decision)> CaptureSessionState() =>
        Plan.Sessions.ToDictionary(x => x.IncomingId, x => (x.CurrentId, x.Decision));

    private void UpdateSummary()
    {
        foreach (var row in Plan.Sessions)
            row.CheckedFields = Plan.Fields.Count(x => x.IncomingSessionId == row.IncomingId && x.UseIncoming);
        SessionsGrid?.Items.Refresh();
        if (SummaryText is null) return;
        SummaryText.Text = $"Сессии: новых {Plan.NewSessions}, точных {Plan.ExactSessions}, вероятных {Plan.ProbableSessions}; " +
            $"выбрано полей {Plan.Fields.Count(x => x.UseIncoming)}; групп в существующие {Plan.Groups.Count(x => x.Decision != ImportGroupDecision.CreateNew)}; " +
            $"JH к добавлению/обновлению {Plan.Proxies.Count(x => x.Decision is ImportDecision.Add or ImportDecision.UseIncoming)}; " +
            $"связей к добавлению/обновлению {Plan.Links.Count(x => x.Decision is ImportDecision.Add or ImportDecision.UseIncoming)}.";
    }
}

public sealed record ImportDecisionOption(ImportDecision Value, string Name)
{
    public override string ToString() => Name;
}

public sealed record ImportGroupDecisionOption(ImportGroupDecision Value, string Name)
{
    public override string ToString() => Name;
}

public static class ImportChoices
{
    public static IReadOnlyList<ImportDecisionOption> Decisions { get; } = new[]
    {
        new ImportDecisionOption(ImportDecision.Skip, "Не импортировать"),
        new ImportDecisionOption(ImportDecision.Add, "Добавить как новую"),
        new ImportDecisionOption(ImportDecision.KeepCurrent, "Оставить текущую"),
        new ImportDecisionOption(ImportDecision.UseIncoming, "Взять данные из файла")
    };

    public static IReadOnlyList<ImportGroupDecisionOption> Groups { get; } = new[]
    {
        new ImportGroupDecisionOption(ImportGroupDecision.CreateNew, "Создать отдельную группу"),
        new ImportGroupDecisionOption(ImportGroupDecision.MergeKeepCurrentName, "Оставить текущую, добавить в неё"),
        new ImportGroupDecisionOption(ImportGroupDecision.MergeUseIncomingName, "Оставить текущую, взять имя из файла")
    };
}

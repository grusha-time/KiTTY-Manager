using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ImportConflictsDialog : Window
{
    public List<ImportConflictRow> Rows { get; }

    public IReadOnlyDictionary<(TransferConflictKind Kind, Guid IncomingId), bool> Decisions =>
        Rows.ToDictionary(row => (row.Kind, row.IncomingId), row => row.UseIncoming);

    public ImportConflictsDialog(IEnumerable<TransferConflict> conflicts)
    {
        Rows = conflicts.Select(conflict => new ImportConflictRow
        {
            Kind = conflict.Kind,
            IncomingId = conflict.IncomingId,
            Name = conflict.Name,
            CurrentSummary = conflict.CurrentSummary,
            IncomingSummary = conflict.IncomingSummary,
            UseIncoming = true
        }).ToList();
        InitializeComponent();
        DataContext = this;
    }

    private void AllIncoming_Click(object sender, RoutedEventArgs e) => SetAll(true);
    private void AllCurrent_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool useIncoming)
    {
        foreach (var row in Rows) row.UseIncoming = useIncoming;
        ConflictsGrid.Items.Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        ConflictsGrid.CommitEdit();
        DialogResult = true;
    }
}

public sealed class ImportConflictRow
{
    public TransferConflictKind Kind { get; init; }
    public Guid IncomingId { get; init; }
    public string KindText => Kind == TransferConflictKind.Server ? "Сессия" : "Точка входа";
    public string Name { get; init; } = "";
    public string CurrentSummary { get; init; } = "";
    public string IncomingSummary { get; init; } = "";
    public bool UseIncoming { get; set; }
}

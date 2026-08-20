using System.Windows;

namespace KiTTYManager.App;

public enum KittyChangesAction { None, Apply, Reject, Ignore }

public sealed class KittyChangeRow
{
    public bool Selected { get; set; }
    public bool CanSelect { get; init; } = true;
    public required Guid ServerId { get; init; }
    public required string PropertyName { get; init; }
    public required string SessionName { get; init; }
    public required string FieldName { get; init; }
    public required string Status { get; init; }
    public required string ManagerDisplay { get; init; }
    public required string KittyDisplay { get; init; }
    public required string ManagerValue { get; init; }
    public required string KittyValue { get; init; }
}

public partial class KittyChangesDialog : Window
{
    private readonly List<KittyChangeRow> rows;
    public KittyChangesAction Action { get; private set; }
    public IReadOnlyList<KittyChangeRow> SelectedRows => rows.Where(row => row.Selected).ToList();

    public KittyChangesDialog(IEnumerable<KittyChangeRow> changes)
    {
        InitializeComponent();
        rows = changes.ToList();
        ChangesGrid.ItemsSource = rows;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { rows.ForEach(row => row.Selected = row.CanSelect); ChangesGrid.Items.Refresh(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { rows.ForEach(row => row.Selected = false); ChangesGrid.Items.Refresh(); }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (!EnsureSelected()) return; Action = KittyChangesAction.Apply; DialogResult = true; }
    private void Reject_Click(object sender, RoutedEventArgs e) { if (!EnsureSelected()) return; Action = KittyChangesAction.Reject; DialogResult = true; }
    private void Ignore_Click(object sender, RoutedEventArgs e) { if (!EnsureSelected()) return; Action = KittyChangesAction.Ignore; DialogResult = true; }
    private bool EnsureSelected()
    {
        ChangesGrid.CommitEdit();
        if (rows.Any(row => row.Selected)) return true;
        ThemedMessageDialog.Show(this, "Выберите хотя бы одно изменение.", "Изменения KiTTY", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
}

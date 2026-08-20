using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ExportSelectionDialog : Window
{
    public List<ExportServerRow> Rows { get; }
    public ICollectionView RowsView { get; }
    public IReadOnlyList<Guid> SelectedServerIds => Rows.Where(row => row.IsSelected).Select(row => row.Id).ToList();
    public bool IncludeEntryPoints => EntryPointsCheckBox.IsChecked == true;

    public ExportSelectionDialog(ManagerConfig config)
    {
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        Rows = config.AllServers()
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(server => new ExportServerRow
            {
                Id = server.Id,
                Name = server.Name,
                Endpoint = server.Endpoint,
                GroupPath = FindGroupPath(config, server.Id),
                IsJumphost = config.BaseProxies.Any(proxy => proxy.StartupServerId == server.Id),
                IsSelected = true
            })
            .ToList();
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private static string FindGroupPath(ManagerConfig config, Guid serverId)
    {
        var group = config.AllGroups().FirstOrDefault(item => item.Servers.Any(server => server.Id == serverId));
        return group is null ? "Без группы" : config.GroupPath(group.Id);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.IsSelected = true;
        ServersGrid.Items.Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.IsSelected = false;
        ServersGrid.Items.Refresh();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        RowsView.Filter = value => value is ExportServerRow row && row.Matches(query);
        RowsView.Refresh();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        ServersGrid.CommitEdit();
        if (Rows.All(row => !row.IsSelected))
        {
            ThemedMessageDialog.Show(this, "Выберите хотя бы одну сессию.", "Экспорт",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}

public sealed class ExportServerRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string GroupPath { get; init; } = "";
    public bool IsJumphost { get; init; }
    public bool IsSelected { get; set; }

    public bool Matches(string query)
    {
        if (query.Length == 0) return true;
        var searchable = $"{Name}\n{Endpoint}\n{GroupPath}\n{(IsJumphost ? "jumphost точка входа" : "")}";
        return searchable.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KiTTYManager.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace KiTTYManager.App;

public partial class ServerSelectionList : UserControl
{
    private ManagerConfig? config;
    private Guid? excludedServerId;
    private ConnectivityGroupSelectionState? groupSelectionState;
    private readonly HashSet<Guid> selectedIds = [];
    private bool refreshing;

    public event EventHandler? SelectionChanged;
    public IReadOnlySet<Guid> SelectedIds => selectedIds;

    public ServerSelectionList() => InitializeComponent();

    public void Configure(ManagerConfig value, IEnumerable<Guid> selected, Guid? excluded = null, bool supportGroupState = false, bool filterIndependentGroupServers = true)
    {
        config = value;
        excludedServerId = excluded;
        if (supportGroupState)
        {
            groupSelectionState = new ConnectivityGroupSelectionState(value, selected, excluded, filterIndependentGroupServers);
            selectedIds.Clear();
            selectedIds.UnionWith(groupSelectionState.SelectedIds);
        }
        else
        {
            groupSelectionState = null;
            selectedIds.Clear();
            selectedIds.UnionWith(selected.Where(id => id != excluded));
        }
        RefreshRows();
    }

    public void SetFilterIndependentGroupServers(bool filter)
    {
        if (groupSelectionState is null) return;
        groupSelectionState.FilterIndependentGroupServers = filter;
        selectedIds.Clear();
        selectedIds.UnionWith(groupSelectionState.SelectedIds);
        RefreshRows();
        RaiseSelectionChanged();
    }

    public void SetSelected(IEnumerable<Guid> selected)
    {
        if (groupSelectionState is not null)
        {
            groupSelectionState.SetExplicitSelection(selected);
            selectedIds.Clear();
            selectedIds.UnionWith(groupSelectionState.SelectedIds);
        }
        else
        {
            selectedIds.Clear();
            selectedIds.UnionWith(selected.Where(id => id != excludedServerId));
        }
        RefreshRows();
        RaiseSelectionChanged();
    }

    public void RefreshRows()
    {
        if (config is null || ChoicesList is null) return;
        var rows = ServerSelectionPolicy.Build(config, selectedIds, SearchBox?.Text, excludedServerId)
            .Select(row => new DisplayRow(row)).ToArray();
        refreshing = true;
        ChoicesList.ItemsSource = rows;
        refreshing = false;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = selectedIds.Count == 0 ? "Не выбрано" : $"Выбрано: {selectedIds.Count}";
        SelectionText.ToolTip = selectedIds.Count == 0 ? null : string.Join(Environment.NewLine,
            config.AllServers().Where(server => selectedIds.Contains(server.Id))
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(server => server.Name));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshRows();

    private void ChoicesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (refreshing || ItemsControl.ContainerFromElement(ChoicesList, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: DisplayRow row }) return;

        if (groupSelectionState is not null)
        {
            if (row.IsGroup && row.GroupId.HasValue)
                groupSelectionState.ToggleGroup(row.GroupId.Value, row.ServerIds);
            else if (!row.IsGroup && row.ServerId.HasValue)
                groupSelectionState.ToggleServer(row.ServerId.Value);
            else
                Toggle(row.ServerIds);

            selectedIds.Clear();
            selectedIds.UnionWith(groupSelectionState.SelectedIds);
            RefreshRows();
            RaiseSelectionChanged();
        }
        else
        {
            Toggle(row.ServerIds);
        }
        e.Handled = true;
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        var visibleIds = ChoicesList.Items.OfType<DisplayRow>().Where(row => !row.IsGroup)
            .SelectMany(row => row.ServerIds).ToArray();
        if (groupSelectionState is not null)
        {
            groupSelectionState.AddServers(visibleIds);
            selectedIds.Clear();
            selectedIds.UnionWith(groupSelectionState.SelectedIds);
        }
        else
        {
            selectedIds.UnionWith(visibleIds);
        }
        RefreshRows();
        RaiseSelectionChanged();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (groupSelectionState is null && selectedIds.Count == 0) return;
        groupSelectionState?.Clear();
        selectedIds.Clear();
        RefreshRows();
        RaiseSelectionChanged();
    }

    private void Toggle(IReadOnlyList<Guid> ids)
    {
        var select = SelectionTogglePolicy.ShouldSelectAll(ids, selectedIds);
        foreach (var id in ids)
            if (select) selectedIds.Add(id); else selectedIds.Remove(id);
        RefreshRows();
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private sealed class DisplayRow
    {
        public Guid? ServerId { get; }
        public Guid? GroupId { get; }
        public IReadOnlyList<Guid> ServerIds { get; }
        public string Name { get; }
        public string Details { get; }
        public bool IsGroup { get; }
        public bool? IsChecked { get; }
        public Thickness Indent { get; }
        public FontWeight FontWeight { get; }
        public Brush Foreground { get; }

        public DisplayRow(ServerSelectionRow row)
        {
            ServerId = row.ServerId;
            GroupId = row.GroupId;
            ServerIds = row.ServerIds;
            Name = row.Name;
            Details = row.Details;
            IsGroup = row.IsGroup;
            IsChecked = row.IsChecked;
            Indent = new Thickness(row.Depth * 18, 0, 0, 0);
            FontWeight = row.IsGroup ? FontWeights.SemiBold : FontWeights.Normal;
            Foreground = row.IsGroup
                ? new SolidColorBrush(Color.FromRgb(190, 210, 245))
                : Brushes.White;
        }
    }
}

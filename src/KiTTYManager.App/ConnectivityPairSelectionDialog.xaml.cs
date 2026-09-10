using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ConnectivityPairSelectionDialog : Window
{
    private readonly ManagerConfig config;
    private readonly ManagedServer? fixedSource;
    private readonly bool skipExisting;
    private readonly HashSet<Guid> selectedServerIds;
    private readonly ObservableCollection<PairRow> pairs = [];
    public IReadOnlyList<ConnectivityPairChoice> SelectedPairs => pairs
        .Where(row => row.Selected)
        .Select(row => row.Pair with { Selected = true }).ToArray();

    public ConnectivityPairSelectionDialog(ManagerConfig config, IEnumerable<Guid> initialServerIds,
        bool skipExisting, ManagedServer? fixedSource = null)
    {
        this.config = config;
        this.fixedSource = fixedSource;
        this.skipExisting = skipExisting;
        selectedServerIds = initialServerIds.Where(id => id != fixedSource?.Id).ToHashSet();
        InitializeComponent();
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        TitleText.Text = fixedSource is null
            ? (skipExisting ? "Только недостающие направленные связи" : "Все направленные связи")
            : $"Цели для «{fixedSource.Name}»";
        PairsGrid.ItemsSource = pairs;
        ServerSelector.Configure(config, selectedServerIds, fixedSource?.Id);
        ServerSelector.SelectionChanged += (_, _) =>
        {
            selectedServerIds.Clear();
            selectedServerIds.UnionWith(ServerSelector.SelectedIds);
            RebuildPairs();
        };
        RebuildPairs();
    }

    private void RebuildPairs()
    {
        if (PairsGrid is null) return;
        var old = pairs.Where(row => !row.Selected).Select(row => (row.Pair.SourceId, row.Pair.TargetId)).ToHashSet();
        var servers = config.AllServers().Where(server => selectedServerIds.Contains(server.Id)).ToArray();
        var plan = fixedSource is null
            ? ConnectivityPairSelectionPolicy.Build(config, servers, skipExisting, skipConfirmedOnly: skipExisting)
            : ConnectivityPairSelectionPolicy.BuildFromSource(config, fixedSource, servers, skipExisting, skipConfirmedOnly: skipExisting);
        pairs.Clear();
        foreach (var pair in plan)
            pairs.Add(new PairRow(pair, !old.Contains((pair.SourceId, pair.TargetId)), UpdateCount));
        UpdateCount();
    }

    private void UpdateCount() => CountText.Text = $"Выбрано: {pairs.Count(row => row.Selected)} из {pairs.Count}";
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var pair in pairs) pair.Selected = true; UpdateCount(); }
    private void ClearAll_Click(object sender, RoutedEventArgs e) { foreach (var pair in pairs) pair.Selected = false; UpdateCount(); }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!pairs.Any(pair => pair.Selected))
        {
            ThemedMessageDialog.Show(this, "Не выбрано ни одного направления.", "Построение связей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private sealed class PairRow : INotifyPropertyChanged
    {
        private bool selected;
        private readonly Action changed;
        public ConnectivityPairChoice Pair { get; }
        public string Display => Pair.Display;
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
        public PairRow(ConnectivityPairChoice pair, bool selected, Action changed) => (Pair, this.selected, this.changed) = (pair, selected, changed);
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

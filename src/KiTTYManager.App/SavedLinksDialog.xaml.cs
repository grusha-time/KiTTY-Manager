using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using KiTTYManager.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace KiTTYManager.App;

public delegate Task<IReadOnlyList<ConnectivityResult>> SavedLinkCheckHandler(
    Guid sourceServerId,
    IReadOnlyList<Guid> targetServerIds,
    CancellationToken cancellationToken);

public delegate Task SavedLinkConnectHandler(
    Guid viaServerId,
    CancellationToken cancellationToken);

public partial class SavedLinksDialog : Window, INotifyPropertyChanged
{
    private readonly ManagerConfig config;
    private readonly ManagedServer selectedServer;
    private readonly SavedLinkCheckHandler? checkLink;
    private readonly Action? saveConfig;
    private readonly SavedLinkConnectHandler? connectLink;
    private readonly ObservableCollection<SavedLinkTreeItem> roots = [];
    private LinkMapModel focusedMap = new([], [], 800, 500);
    private readonly Dictionary<Guid, Button> mapButtons = [];
    private readonly List<(LinkMapEdge Edge, Line Line, Polygon? Arrow)> mapEdges = [];
    private double mapScale = 1;
    private Vector mapTranslation;
    private Point? panStart;
    private Vector panStartTranslation;
    private CancellationTokenSource? checkCancellation;
    private CancellationTokenSource? connectCancellation;
    private readonly HashSet<Guid> markedCounterpartIds = [];
    private readonly Dictionary<Guid, string> counterpartLabels = [];

    public double MarkWidth { get; private set; } = 36;
    public double StatusWidth { get; private set; } = 52;
    public double DirectionWidth { get; private set; } = 70;
    public double ServerWidth { get; private set; } = 120;
    public double GroupWidth { get; private set; } = 120;
    public double StrategyWidth { get; private set; } = 90;
    public double EntryWidth { get; private set; } = 100;
    public double LastSuccessWidth { get; private set; } = 120;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SavedLinksDialog(
        ManagerConfig config,
        ManagedServer selectedServer,
        SavedLinkCheckHandler? checkLink = null,
        Action? saveConfig = null,
        SavedLinkConnectHandler? connectLink = null)
    {
        this.config = config;
        this.selectedServer = selectedServer;
        this.checkLink = checkLink;
        this.saveConfig = saveConfig;
        this.connectLink = connectLink;

        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();

        Title = $"Сохранённые связи — {selectedServer.Name}";
        LinksTree.ItemsSource = roots;
        BuildTree();
        Loaded += (_, _) => { SyncTreeColumnWidths(); FitToViewport(); };
        SizeChanged += (_, _) => ApplyMapTransform();
    }

    private void LinksHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) => SyncTreeColumnWidths();
    private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SyncTreeColumnWidths();

    private void SyncTreeColumnWidths()
    {
        SetColumnWidth(MarkWidth, MarkColumn.ActualWidth, value => MarkWidth = value, nameof(MarkWidth));
        SetColumnWidth(StatusWidth, StatusColumn.ActualWidth, value => StatusWidth = value, nameof(StatusWidth));
        SetColumnWidth(DirectionWidth, DirectionColumn.ActualWidth, value => DirectionWidth = value, nameof(DirectionWidth));
        SetColumnWidth(ServerWidth, ServerColumn.ActualWidth, value => ServerWidth = value, nameof(ServerWidth));
        SetColumnWidth(GroupWidth, GroupColumn.ActualWidth, value => GroupWidth = value, nameof(GroupWidth));
        SetColumnWidth(StrategyWidth, StrategyColumn.ActualWidth, value => StrategyWidth = value, nameof(StrategyWidth));
        SetColumnWidth(EntryWidth, EntryColumn.ActualWidth, value => EntryWidth = value, nameof(EntryWidth));
        SetColumnWidth(LastSuccessWidth, LastSuccessColumn.ActualWidth, value => LastSuccessWidth = value, nameof(LastSuccessWidth));
    }

    private void SetColumnWidth(double current, double value, Action<double> assign, string propertyName)
    {
        if (value <= 0 || Math.Abs(current - value) < 0.5) return;
        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void BuildTree()
    {
        foreach (var item in LeafItems())
        {
            if (item.IsMarked == true) markedCounterpartIds.Add(item.CounterpartServerId);
            else markedCounterpartIds.Remove(item.CounterpartServerId);
        }
        var query = LinksSearchBox?.Text.Trim() ?? "";
        roots.Clear();

        var links = config.Links
            .Where(link => link.FromServerId == selectedServer.Id || link.ToServerId == selectedServer.Id)
            .Select(link => (Link: link, CounterpartId: link.FromServerId == selectedServer.Id
                ? link.ToServerId
                : link.FromServerId))
            .Where(item => item.CounterpartId != selectedServer.Id && config.FindServer(item.CounterpartId) is not null)
            .GroupBy(item => item.CounterpartId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Link).ToList());
        BuildCounterpartLabels(links.Keys);

        if (links.Count > 0)
        {
            foreach (var group in config.Groups)
            {
                var groupNode = BuildGroupNode(group, links, group.Name, query);
                if (groupNode is not null) roots.Add(groupNode);
            }

            var ungrouped = config.UngroupedServers
                .Where(server => links.ContainsKey(server.Id))
                .Where(server => MatchesSearch(server, "Без группы", query))
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (ungrouped.Count > 0)
            {
                var groupNode = SavedLinkTreeItem.Branch("Без группы", true, "Без группы");
                foreach (var server in ungrouped)
                    AddServerRelation(groupNode, server, links[server.Id], "Без группы");
                roots.Add(groupNode);
            }
        }

        foreach (var leaf in LeafItems())
            if (markedCounterpartIds.Contains(leaf.CounterpartServerId)) leaf.IsMarked = true;

        RefreshBranchStatuses();
        var hasLinks = LeafItems().Any();
        LinksTree.Visibility = hasLinks ? Visibility.Visible : Visibility.Collapsed;
        BuildMap();
        if (hasLinks)
            UpdateMarkedStatus();
        else
        {
            CheckAllButton.IsEnabled = false;
            DeleteSelectedButton.IsEnabled = false;
            ConnectSelectedButton.IsEnabled = false;
            StatusText.Text = "Нет связей для выбранного сервера";
        }
    }

    private void BuildCounterpartLabels(IEnumerable<Guid> counterpartIds)
    {
        counterpartLabels.Clear();
        var servers = counterpartIds.Select(config.FindServer).Where(server => server is not null).Cast<ManagedServer>().ToArray();
        foreach (var sameName in servers.GroupBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (sameName.Count() == 1)
            {
                var server = sameName.Single();
                counterpartLabels[server.Id] = server.Name;
                continue;
            }

            foreach (var sameEndpoint in sameName.GroupBy(server => server.Endpoint, StringComparer.OrdinalIgnoreCase))
            foreach (var server in sameEndpoint)
                counterpartLabels[server.Id] = sameEndpoint.Count() == 1
                    ? $"{server.Name} [{server.Endpoint}]"
                    : $"{server.Name} [{server.Endpoint}; {server.Id.ToString("N")[..8]}]";
        }
    }

    private void BuildMap()
    {
        GraphCanvas.Children.Clear();
        mapButtons.Clear();
        mapEdges.Clear();
        var full = LinkMapLayout.Build(config);
        var ids = new HashSet<Guid>([selectedServer.Id]);
        foreach (var leaf in LeafItems()) ids.Add(leaf.CounterpartServerId);
        var nodes = full.Nodes.Where(node => ids.Contains(node.ServerId)).ToArray();
        var edges = full.Edges.Where(edge =>
            (edge.ServerAId == selectedServer.Id && ids.Contains(edge.ServerBId)) ||
            (edge.ServerBId == selectedServer.Id && ids.Contains(edge.ServerAId))).ToArray();
        if (nodes.Length == 0 || edges.Length == 0)
        {
            focusedMap = new([], [], 800, 500);
            MapEmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        var minX = nodes.Min(node => node.X);
        var minY = nodes.Min(node => node.Y);
        var normalized = nodes.Select(node => node with { X = node.X - minX + 90, Y = node.Y - minY + 90 }).ToArray();
        focusedMap = new(normalized, edges,
            Math.Max(800, normalized.Max(node => node.X) + LinkMapWindow.NodeWidth + 90),
            Math.Max(420, normalized.Max(node => node.Y) + LinkMapWindow.NodeHeight + 90));
        GraphCanvas.Width = focusedMap.Width; GraphCanvas.Height = focusedMap.Height;
        MapEmptyPanel.Visibility = Visibility.Collapsed;
        var byId = normalized.ToDictionary(node => node.ServerId);
        foreach (var edge in edges)
        {
            var from = byId[edge.ServerAId]; var to = byId[edge.ServerBId];
            var tooltip = LinkMapWindow.EdgeTooltip(edge, from.Name, to.Name);
            var x1 = from.X + LinkMapWindow.NodeWidth / 2;
            var y1 = from.Y + LinkMapWindow.NodeHeight / 2;
            var x2 = to.X + LinkMapWindow.NodeWidth / 2;
            var y2 = to.Y + LinkMapWindow.NodeHeight / 2;
            var line = new Line
            {
                X1 = x1, Y1 = y1,
                X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(edge.IsAvailable ? "#4F8CFF" : "#D68A2E")),
                StrokeThickness = edge.IsAvailable ? 2.4 : 1.8,
                Opacity = edge.IsAvailable ? 0.78 : 0.65,
                ToolTip = tooltip
            };
            if (!edge.IsAvailable) line.StrokeDashArray = [5, 4];
            GraphCanvas.Children.Add(line);

            var arrow = LinkMapWindow.CreateDirectedArrow(edge, x1, y1, x2, y2, tooltip);
            if (arrow is not null)
            {
                Panel.SetZIndex(arrow, 1);
                GraphCanvas.Children.Add(arrow);
            }
            mapEdges.Add((edge, line, arrow));
        }
        foreach (var node in normalized)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = node.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
            content.Children.Add(new TextBlock { Text = node.GroupPath, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 196)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            var button = new Button
            {
                Width = LinkMapWindow.NodeWidth, Height = LinkMapWindow.NodeHeight, Padding = new Thickness(12, 7, 12, 7),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Content = content, Tag = node.ServerId,
                ToolTip = $"{node.Name}\n{node.GroupPath}\nНажмите, чтобы выбрать направления в таблице",
                Background = new SolidColorBrush(node.ServerId == selectedServer.Id ? Color.FromRgb(38, 57, 92) : Color.FromRgb(31, 42, 59)),
                BorderBrush = new SolidColorBrush(node.ServerId == selectedServer.Id ? Color.FromRgb(79, 140, 255) : Color.FromRgb(66, 91, 128)),
                BorderThickness = new Thickness(node.ServerId == selectedServer.Id ? 2.2 : 1.4)
            };
            button.Click += MapNode_Click;
            mapButtons[node.ServerId] = button;
            Canvas.SetLeft(button, node.X); Canvas.SetTop(button, node.Y); Panel.SetZIndex(button, 2);
            GraphCanvas.Children.Add(button);
        }
        ApplyMapTransform();
        SyncMapSelection();
    }

    private void MapNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid serverId }) return;
        if (serverId == selectedServer.Id) ClearSelection_Click(sender, e);
        else
        {
            var leaves = LeafItems().Where(item => item.CounterpartServerId == serverId).ToArray();
            var select = leaves.Any(item => item.IsMarked != true);
            foreach (var leaf in leaves) leaf.IsMarked = select;
            UpdateMarkedStatus();
        }
        SyncMapSelection();
    }

    private void MapViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        panStart = e.GetPosition(MapViewport); panStartTranslation = mapTranslation;
        MapViewport.CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void MapViewport_MouseMove(object sender, MouseEventArgs e)
    { if (panStart is null || e.LeftButton != MouseButtonState.Pressed) return; mapTranslation = panStartTranslation + (e.GetPosition(MapViewport) - panStart.Value); ApplyMapTransform(); }
    private void MapViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();
    private void MapViewport_MouseLeave(object sender, MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed) EndPan(); }
    private void EndPan() { panStart = null; MapViewport.ReleaseMouseCapture(); Cursor = Cursors.Arrow; }
    private void MapViewport_MouseWheel(object sender, MouseWheelEventArgs e) { ZoomAt(e.GetPosition(MapViewport), e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true; }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(new(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(new(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1 / 1.2);
    private void ZoomAt(Point point, double factor)
    {
        if (focusedMap.Nodes.Count == 0) return;
        var newScale = Math.Clamp(mapScale * factor, .15, 3.5);
        var world = new Point((point.X - mapTranslation.X) / mapScale, (point.Y - mapTranslation.Y) / mapScale);
        mapTranslation = new(point.X - world.X * newScale, point.Y - world.Y * newScale); mapScale = newScale; ApplyMapTransform();
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToViewport();
    private void FitToViewport()
    {
        if (focusedMap.Nodes.Count == 0 || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0) return;
        mapScale = Math.Clamp(Math.Min((MapViewport.ActualWidth - 60) / focusedMap.Width, (MapViewport.ActualHeight - 60) / focusedMap.Height), .15, 1);
        mapTranslation = new((MapViewport.ActualWidth - focusedMap.Width * mapScale) / 2, (MapViewport.ActualHeight - focusedMap.Height * mapScale) / 2); ApplyMapTransform();
    }
    private void ApplyMapTransform()
    { GraphScale.ScaleX = mapScale; GraphScale.ScaleY = mapScale; GraphTranslate.X = mapTranslation.X; GraphTranslate.Y = mapTranslation.Y; ZoomText.Text = $"{mapScale * 100:0}%"; }
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    { while (child is not null) { if (child is T result) return result; child = VisualTreeHelper.GetParent(child); } return null; }

    private SavedLinkTreeItem? BuildGroupNode(
        ServerGroup group,
        IReadOnlyDictionary<Guid, List<ServerLink>> byServer,
        string path,
        string query)
    {
        var node = SavedLinkTreeItem.Branch(group.Name, true, path);
        var groupMatches = query.Length == 0 || path.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        foreach (var server in group.Servers
                     .Where(server => byServer.ContainsKey(server.Id))
                     .Where(server => groupMatches || MatchesSearch(server, path, query))
                     .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase))
            AddServerRelation(node, server, byServer[server.Id], path);

        foreach (var child in group.Groups)
        {
            var childNode = BuildGroupNode(child, byServer, $"{path} / {child.Name}", groupMatches ? "" : query);
            if (childNode is not null) node.AddChild(childNode);
        }

        return node.Children.Count == 0 ? null : node;
    }

    private static bool MatchesSearch(ManagedServer server, string groupPath, string query) =>
        query.Length == 0 || server.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        server.Endpoint.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        server.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        groupPath.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void LinksSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildTree();

    private void AddServerRelation(
        SavedLinkTreeItem parent,
        ManagedServer server,
        IReadOnlyList<ServerLink> links,
        string groupPath)
    {
        var relationship = ServerLinkPairPolicy.GroupAsRelationship(selectedServer.Id, server.Id, links);
        if (relationship.Count > 0)
            parent.AddChild(SavedLinkTreeItem.Leaf(config, selectedServer, server, relationship, groupPath,
                counterpartLabels.GetValueOrDefault(server.Id, server.Name)));
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        if (checkLink is null || checkCancellation is not null || connectCancellation is not null) return;

        var leaves = LeafItems().Where(item => item.IsMarked == true).ToList();
        if (leaves.Count == 0) return;
        var checks = leaves.SelectMany(item =>
            {
                var pairs = new List<(SavedLinkTreeItem Item, Guid From, Guid To)>();
                foreach (var group in item.RelatedLinks.GroupBy(link => (link.FromServerId, link.ToServerId)))
                    pairs.Add((item, group.Key.FromServerId, group.Key.ToServerId));

                if (item.SelectedServerId != Guid.Empty && item.CounterpartServerId != Guid.Empty)
                {
                    var fwd = (From: item.SelectedServerId, To: item.CounterpartServerId);
                    var rev = (From: item.CounterpartServerId, To: item.SelectedServerId);
                    if (!pairs.Any(p => p.From == fwd.From && p.To == fwd.To))
                        pairs.Add((item, fwd.From, fwd.To));
                    if (!pairs.Any(p => p.From == rev.From && p.To == rev.To))
                        pairs.Add((item, rev.From, rev.To));
                }
                return pairs;
            })
            .ToList();
        if (config.BaseProxies.All(proxy => !proxy.Enabled))
        {
            ThemedMessageDialog.Show(this,
                "Нет включённых точек входа. Сначала настройте работающий SOCKS5.",
                "Проверка сохранённых связей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ThemedMessageDialog.Show(
                this,
                $"Будет последовательно проверено направлений: {checks.Count}. Продолжить?",
                "Проверка сохранённых связей",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        using var cancellation = new CancellationTokenSource();
        checkCancellation = cancellation;
        CheckAllButton.IsEnabled = false;
        ConnectSelectedButton.IsEnabled = false;
        DeleteSelectedButton.IsEnabled = false;
        CancelCheckButton.IsEnabled = true;
        CancelCheckButton.Visibility = Visibility.Visible;

        var completed = 0;
        var successful = 0;
        try
        {
            var outcomes = leaves.ToDictionary(item => item, _ => new List<ConnectivityResult>());
            foreach (var sourceBatch in checks.GroupBy(item => item.From))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var batch = sourceBatch.ToList();
                foreach (var item in batch.Select(entry => entry.Item).Distinct())
                    item.SetStatus(LinkCheckState.Checking, "Проверяются направления…");
                RefreshBranchStatuses();
                var sourceName = config.FindServer(sourceBatch.Key)?.Name ?? "исходный сервер";
                StatusText.Text = $"Проверяю от «{sourceName}»: {batch.Count} связей";

                try
                {
                    var results = await checkLink(sourceBatch.Key,
                        batch.Select(item => item.To).Distinct().ToArray(), cancellation.Token);
                    foreach (var entry in batch)
                    {
                        var result = results.FirstOrDefault(value => value.TargetId == entry.To);
                        if (result is null)
                        {
                            outcomes[entry.Item].Add(new(entry.To, false,
                                "Направление не было проверено", TimeSpan.Zero, SourceId: entry.From));
                            continue;
                        }
                        outcomes[entry.Item].Add(result);
                        if (result.Success) successful++;
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    foreach (var item in batch.Select(entry => entry.Item).Distinct())
                        item.SetStatus(LinkCheckState.Unchecked, "Проверка отменена");
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var entry in batch)
                        outcomes[entry.Item].Add(new(entry.To, false,
                            ShortMessage(ex.Message), TimeSpan.Zero, SourceId: entry.From));
                }

                completed += batch.Count;
                foreach (var item in batch.Select(entry => entry.Item).Distinct())
                {
                    item.RefreshFromConfig(config);
                    item.ApplyRelationshipResults(outcomes[item]);
                }
                RefreshBranchStatuses();
                SyncMapSelection();
            }

            StatusText.Text = $"Проверено направлений: {completed}. Доступно: {successful}. Недоступно: {completed - successful}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = $"Проверка отменена. Завершено направлений: {completed} из {checks.Count}.";
            RefreshBranchStatuses();
        }
        finally
        {
            if (ReferenceEquals(checkCancellation, cancellation)) checkCancellation = null;
            CancelCheckButton.IsEnabled = true;
            CancelCheckButton.Visibility = Visibility.Collapsed;
            UpdateMarkedStatus();
            UpdateConnectButton();
            BuildMap();
        }
    }

    private void RefreshBranchStatuses()
    {
        foreach (var root in roots) root.RefreshAggregateStatus();
    }

    private IEnumerable<SavedLinkTreeItem> LeafItems() => roots.SelectMany(root => root.DescendantLeaves());

    private void MarkCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateMarkedStatus();
        SyncMapSelection();
    }

    private void LinksTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TreeSelectionPolicy.ShouldHandleRowClick(
                FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null)) return;
        if (FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)
            is not { DataContext: SavedLinkTreeItem item }) return;
        item.IsMarked = item.IsMarked != true;
        UpdateMarkedStatus();
        SyncMapSelection();
        e.Handled = true;
    }

    private void UpdateConnectButton()
    {
        var marked = LeafItems().Where(item => item.IsMarked == true).ToArray();
        ConnectSelectedButton.IsEnabled = connectLink is not null &&
            checkCancellation is null && connectCancellation is null &&
            marked is [{ CanConnectVia: true }];
    }

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        var marked = LeafItems().Where(candidate => candidate.IsMarked == true).ToArray();
        if (connectLink is null || connectCancellation is not null || checkCancellation is not null ||
            marked is not [{ CanConnectVia: true } item])
            return;

        using var cancellation = new CancellationTokenSource();
        connectCancellation = cancellation;
        ConnectSelectedButton.IsEnabled = false;
        CheckAllButton.IsEnabled = false;
        DeleteSelectedButton.IsEnabled = false;
        StatusText.Text = $"Подключаю через «{item.DisplayName}»…";
        try
        {
            await connectLink(item.CounterpartServerId, cancellation.Token);
            Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Подключение отменено.";
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, ShortMessage(ex.Message),
                "Подключение через сохранённую связь",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Не удалось подключиться через выбранную связь.";
        }
        finally
        {
            if (ReferenceEquals(connectCancellation, cancellation)) connectCancellation = null;
            UpdateMarkedStatus();
            UpdateConnectButton();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in LeafItems())
        {
            item.IsMarked = true;
            markedCounterpartIds.Add(item.CounterpartServerId);
        }
        UpdateMarkedStatus();
        SyncMapSelection();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        markedCounterpartIds.Clear();
        foreach (var item in LeafItems()) item.IsMarked = false;
        UpdateMarkedStatus();
        SyncMapSelection();
    }

    private void UpdateMarkedStatus()
    {
        var count = LeafItems().Count(item => item.IsMarked == true);
        DeleteSelectedButton.IsEnabled = count > 0 &&
            checkCancellation is null && connectCancellation is null;
        CheckAllButton.IsEnabled = count > 0 && checkLink is not null &&
            checkCancellation is null && connectCancellation is null;
        StatusText.Text = $"Выбрано связей: {count}";
        UpdateConnectButton();
    }

    private void SyncMapSelection()
    {
        var markedIds = LeafItems()
            .Where(item => item.IsMarked == true)
            .Select(item => item.CounterpartServerId)
            .ToHashSet();

        foreach (var (id, button) in mapButtons)
        {
            var marked = id != selectedServer.Id && markedIds.Contains(id);
            button.Background = new SolidColorBrush(marked ? Color.FromRgb(45, 83, 61) : id == selectedServer.Id ? Color.FromRgb(38, 57, 92) : Color.FromRgb(31, 42, 59));
            button.BorderBrush = new SolidColorBrush(marked ? Color.FromRgb(63, 190, 116) : id == selectedServer.Id ? Color.FromRgb(79, 140, 255) : Color.FromRgb(66, 91, 128));
            button.BorderThickness = new Thickness(marked || id == selectedServer.Id ? 2.2 : 1.4);
        }

        foreach (var (edge, line, arrow) in mapEdges)
        {
            var counterpartId = edge.ServerAId == selectedServer.Id ? edge.ServerBId : edge.ServerAId;
            var isMarked = markedIds.Contains(counterpartId);
            var anyMarked = markedIds.Count > 0;

            if (anyMarked)
            {
                if (isMarked)
                {
                    line.Stroke = new SolidColorBrush(Color.FromRgb(255, 205, 67));
                    line.StrokeThickness = 3.6;
                    line.Opacity = 1.0;
                    Panel.SetZIndex(line, 1);
                    if (arrow is not null)
                    {
                        arrow.Fill = new SolidColorBrush(Color.FromRgb(255, 205, 67));
                        arrow.Opacity = 1.0;
                        Panel.SetZIndex(arrow, 2);
                    }
                }
                else
                {
                    line.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(edge.IsAvailable ? "#4F8CFF" : "#D68A2E"));
                    line.StrokeThickness = edge.IsAvailable ? 2.4 : 1.8;
                    line.Opacity = 0.18;
                    Panel.SetZIndex(line, 0);
                    if (arrow is not null)
                    {
                        arrow.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F8CFF"));
                        arrow.Opacity = 0.18;
                        Panel.SetZIndex(arrow, 0);
                    }
                }
            }
            else
            {
                line.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(edge.IsAvailable ? "#4F8CFF" : "#D68A2E"));
                line.StrokeThickness = edge.IsAvailable ? 2.4 : 1.8;
                line.Opacity = edge.IsAvailable ? 0.78 : 0.65;
                Panel.SetZIndex(line, 0);
                if (arrow is not null)
                {
                    arrow.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F8CFF"));
                    arrow.Opacity = 0.88;
                    Panel.SetZIndex(arrow, 1);
                }
            }
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var marked = LeafItems().Where(item => item.IsMarked == true && item.Link is not null).ToList();
        if (marked.Count == 0) return;
        var preview = string.Join("\n", marked.Take(12).Select(item =>
            $"• {config.FindServer(item.FromServerId)?.Name} → {config.FindServer(item.ToServerId)?.Name}"));
        if (marked.Count > 12) preview += $"\n…и ещё {marked.Count - 12}";
        if (ThemedMessageDialog.Show(this,
                $"Удалить выбранные связи: {marked.Count}?\n\n{preview}",
                "Удаление связей", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var item in marked)
            foreach (var link in item.RelatedLinks)
                config.Links.Remove(link);
        saveConfig?.Invoke();
        BuildTree();
    }

    private void CancelCheck_Click(object sender, RoutedEventArgs e)
    {
        CancelCheckButton.IsEnabled = false;
        StatusText.Text = "Отменяю проверку…";
        checkCancellation?.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        checkCancellation?.Cancel();
        connectCancellation?.Cancel();
    }

    private static string ShortMessage(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 240 ? singleLine : singleLine[..237] + "…";
    }

}

internal enum LinkCheckState
{
    Unchecked,
    Checking,
    Succeeded,
    Failed
}

internal sealed class SavedLinkTreeItem : INotifyPropertyChanged
{
    private LinkCheckState state;
    private string statusDetail = "Не проверено в этом окне";
    private string directionDisplay = "";
    private string strategy = "";
    private string lastSuccessDisplay = "";

    private SavedLinkTreeItem(string displayName, bool isBranch)
    {
        DisplayName = displayName;
        IsBranch = isBranch;
    }

    public string DisplayName { get; }
    public bool IsBranch { get; }
    public bool IsExpanded { get; set; }
    private bool? isMarked = false;
    private SavedLinkTreeItem? parent;
    public bool? IsMarked
    {
        get => isMarked;
        set
        {
            if (IsBranch)
            {
                SetSubtree(TreeSelectionPolicy.NextBranchClick(isMarked));
                return;
            }
            if (isMarked == value) return;
            Set(ref isMarked, value);
            parent?.RefreshMarkedFromChildren();
        }
    }
    public string DirectionDisplay
    {
        get => directionDisplay;
        private set => Set(ref directionDisplay, value);
    }
    public string GroupPath { get; private init; } = "";
    public string ProxyName { get; private init; } = "—";
    public string Strategy
    {
        get => strategy;
        private set => Set(ref strategy, value);
    }
    public string LastSuccessDisplay
    {
        get => lastSuccessDisplay;
        private set => Set(ref lastSuccessDisplay, value);
    }
    public string StatusGlyph => state switch
    {
        LinkCheckState.Checking => "…",
        LinkCheckState.Succeeded => "✓",
        LinkCheckState.Failed => "✕",
        _ => "•"
    };
    public string StatusColor => state switch
    {
        LinkCheckState.Checking => "#60A5FA",
        LinkCheckState.Succeeded => "#22C55E",
        LinkCheckState.Failed => "#EF4444",
        _ => "#6B7280"
    };
    public string StatusDetail
    {
        get => statusDetail;
        private set => Set(ref statusDetail, value);
    }
    public ObservableCollection<SavedLinkTreeItem> Children { get; } = [];
    public ServerLink? Link { get; private set; }
    public IReadOnlyList<ServerLink> RelatedLinks { get; private set; } = [];
    public Guid FromServerId => Link?.FromServerId ?? Guid.Empty;
    public Guid ToServerId => Link?.ToServerId ?? Guid.Empty;
    public Guid CounterpartServerId { get; private init; }
    public Guid SelectedServerId { get; private init; }
    public bool CanConnectVia { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddChild(SavedLinkTreeItem child)
    {
        child.parent = this;
        Children.Add(child);
        RefreshMarkedFromChildren();
    }

    private void SetSubtree(bool value)
    {
        foreach (var child in Children)
        {
            if (child.IsBranch) child.SetSubtree(value);
            else child.SetMarkedWithoutParent(value);
        }
        SetMarkedWithoutParent(value);
        parent?.RefreshMarkedFromChildren();
    }

    private void SetMarkedWithoutParent(bool? value) => Set(ref isMarked, value, nameof(IsMarked));

    private void RefreshMarkedFromChildren()
    {
        if (!IsBranch || Children.Count == 0) return;
        bool? value = TreeSelectionPolicy.Aggregate(Children.Select(child => child.IsMarked));
        SetMarkedWithoutParent(value);
        parent?.RefreshMarkedFromChildren();
    }

    public static SavedLinkTreeItem Branch(string name, bool expanded, string groupPath = "") => new(name, true)
    {
        IsExpanded = expanded,
        GroupPath = groupPath
    };

    public static SavedLinkTreeItem Leaf(
        ManagerConfig config,
        ManagedServer selectedServer,
        ManagedServer server,
        IReadOnlyList<ServerLink> links,
        string groupPath,
        string? displayName = null)
    {
        var outgoingLinks = links.Where(candidate => candidate.FromServerId == selectedServer.Id).ToArray();
        var incomingLinks = links.Where(candidate => candidate.ToServerId == selectedServer.Id).ToArray();
        var outgoingSuccess = outgoingLinks.Any(candidate => candidate.LastSuccessUtc is not null);
        var incomingSuccess = incomingLinks.Any(candidate => candidate.LastSuccessUtc is not null);

        var link = links
            .OrderByDescending(candidate => candidate.LastSuccessUtc.HasValue)
            .ThenByDescending(candidate => candidate.FromServerId == selectedServer.Id)
            .ThenByDescending(candidate => candidate.LastSuccessUtc)
            .First();

        var (direction, statusDetail) = SavedLinkDirectionPolicy.Evaluate(outgoingSuccess, incomingSuccess);

        var item = new SavedLinkTreeItem(displayName ?? server.Name, false)
        {
            DirectionDisplay = direction,
            GroupPath = groupPath,
            ProxyName = link.LastSuccessfulProxyId is Guid proxyId
                ? config.BaseProxies.FirstOrDefault(proxy => proxy.Id == proxyId)?.Name ?? "Удалена"
                : "—",
            Strategy = string.IsNullOrWhiteSpace(link.LastStrategy) ? "—" : link.LastStrategy,
            LastSuccessDisplay = FormatDate(link.LastSuccessUtc),
            Link = link,
            RelatedLinks = links,
            SelectedServerId = selectedServer.Id,
            CounterpartServerId = server.Id,
            CanConnectVia = incomingSuccess
        };
        if (outgoingSuccess || incomingSuccess)
        {
            item.state = LinkCheckState.Succeeded;
        }
        item.statusDetail = statusDetail;
        return item;
    }

    public void RefreshFromLinks()
    {
        if (SelectedServerId == Guid.Empty || RelatedLinks.Count == 0) return;
        var outgoingLinks = RelatedLinks.Where(candidate => candidate.FromServerId == SelectedServerId).ToArray();
        var incomingLinks = RelatedLinks.Where(candidate => candidate.ToServerId == SelectedServerId).ToArray();
        var outgoingSuccess = outgoingLinks.Any(candidate => candidate.LastSuccessUtc is not null);
        var incomingSuccess = incomingLinks.Any(candidate => candidate.LastSuccessUtc is not null);

        var link = RelatedLinks
            .OrderByDescending(candidate => candidate.LastSuccessUtc.HasValue)
            .ThenByDescending(candidate => candidate.FromServerId == SelectedServerId)
            .ThenByDescending(candidate => candidate.LastSuccessUtc)
            .FirstOrDefault();

        if (link is not null)
        {
            Link = link;
            LastSuccessDisplay = FormatDate(link.LastSuccessUtc);
            if (!string.IsNullOrWhiteSpace(link.LastStrategy))
                Strategy = link.LastStrategy;
        }

        var (direction, _) = SavedLinkDirectionPolicy.Evaluate(outgoingSuccess, incomingSuccess);
        DirectionDisplay = direction;

        CanConnectVia = incomingSuccess;
    }

    public IEnumerable<SavedLinkTreeItem> DescendantLeaves()
    {
        if (!IsBranch)
        {
            yield return this;
            yield break;
        }

        foreach (var leaf in Children.SelectMany(child => child.DescendantLeaves()))
            yield return leaf;
    }

    public void ApplyResult(ConnectivityResult result)
    {
        SetStatus(result.Success ? LinkCheckState.Succeeded : LinkCheckState.Failed, result.Message);
        if (!result.Success) return;

        if (!string.IsNullOrWhiteSpace(result.Strategy)) Strategy = result.Strategy;
        LastSuccessDisplay = FormatDate(Link?.LastSuccessUtc ?? DateTimeOffset.Now);
    }

    public void RefreshFromConfig(ManagerConfig config)
    {
        if (SelectedServerId == Guid.Empty || CounterpartServerId == Guid.Empty) return;
        var currentLinks = config.Links
            .Where(l => (l.FromServerId == SelectedServerId && l.ToServerId == CounterpartServerId) ||
                        (l.FromServerId == CounterpartServerId && l.ToServerId == SelectedServerId))
            .ToList();
        if (currentLinks.Count > 0)
            RelatedLinks = currentLinks;
        RefreshFromLinks();
    }

    public void ApplyRelationshipResults(IReadOnlyList<ConnectivityResult> results)
    {
        if (results.Count == 0) return;
        RefreshFromLinks();
        var outgoingSuccess = RelatedLinks.Any(l => l.FromServerId == SelectedServerId && l.LastSuccessUtc is not null);
        var incomingSuccess = RelatedLinks.Any(l => l.ToServerId == SelectedServerId && l.LastSuccessUtc is not null);
        var failed = results.Where(result => !result.Success).ToArray();

        if (outgoingSuccess || incomingSuccess)
        {
            state = LinkCheckState.Succeeded;
            var (direction, status) = SavedLinkDirectionPolicy.Evaluate(outgoingSuccess, incomingSuccess);
            DirectionDisplay = direction;
            StatusDetail = status;
            var successfulResult = results.LastOrDefault(result => result.Success);
            if (successfulResult is not null && !string.IsNullOrWhiteSpace(successfulResult.Strategy))
                Strategy = successfulResult.Strategy;
            LastSuccessDisplay = FormatDate(Link?.LastSuccessUtc ?? DateTimeOffset.Now);
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(DirectionDisplay));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(Strategy));
            OnPropertyChanged(nameof(LastSuccessDisplay));
            return;
        }

        if (failed.Length > 0)
        {
            SetStatus(LinkCheckState.Failed, string.Join("; ", failed.Select(result => result.Message).Distinct()));
            return;
        }

        var latest = results[^1];
        ApplyResult(latest);
    }

    public void SetStatus(LinkCheckState newState, string detail)
    {
        state = newState;
        StatusDetail = detail;
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusColor));
    }

    public void RefreshAggregateStatus()
    {
        if (!IsBranch) return;
        foreach (var child in Children) child.RefreshAggregateStatus();

        var leaves = DescendantLeaves().ToList();
        if (leaves.Any(leaf => leaf.state == LinkCheckState.Checking))
            SetStatus(LinkCheckState.Checking, "Проверка выполняется");
        else if (leaves.Count > 0 && leaves.All(leaf => leaf.state == LinkCheckState.Succeeded))
            SetStatus(LinkCheckState.Succeeded, "Все связи доступны");
        else if (leaves.Any(leaf => leaf.state == LinkCheckState.Failed))
            SetStatus(LinkCheckState.Failed, "Одна или несколько связей недоступны");
        else
            SetStatus(LinkCheckState.Unchecked, "Не проверено в этом окне");
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "—";

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KiTTYManager.Core;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace KiTTYManager.App;

public partial class LinkMapWindow : Window
{
    internal const double NodeWidth = 190;
    internal const double NodeHeight = 58;
    private LinkMapModel map;
    private readonly ManagerConfig config;
    private readonly Action<Guid> selectServer;
    private readonly Func<IReadOnlyList<Guid>, Task> checkLinks;
    private readonly Dictionary<Guid, Button> nodeButtons = [];
    private readonly List<(LinkMapEdge Edge, Line Line, Polygon? Arrow)> edgeVisuals = [];
    private readonly List<UIElement> highlightedVisuals = [];
    private readonly HashSet<Guid> checkedServerIds = [];
    private readonly HashSet<Guid> highlightedNeighborIds = [];
    private bool checkMode;
    private bool refreshingSessionChoices;
    private double scale = 1;
    private Vector translation;
    private Point? panStart;
    private Vector panStartTranslation;
    private Button? selectedNode;

    public LinkMapWindow(
        ManagerConfig config,
        Action<Guid> selectServer,
        Func<IReadOnlyList<Guid>, Task> checkLinks)
    {
        this.config = config;
        this.selectServer = selectServer;
        this.checkLinks = checkLinks;
        map = LinkMapLayout.Build(config);
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();
        BuildGraph();
        Loaded += (_, _) => FitToViewport();
        SizeChanged += (_, _) =>
        {
            if (map.Nodes.Count == 0) return;
            ApplyTransform();
        };
    }

    private void BuildGraph()
    {
        GraphCanvas.Children.Clear();
        nodeButtons.Clear();
        edgeVisuals.Clear();
        highlightedVisuals.Clear();
        highlightedNeighborIds.Clear();
        selectedNode = null;
        SummaryText.Text = map.Nodes.Count == 0
            ? "Нет сессий со связями"
            : $"Сессий: {map.Nodes.Count} · Связей: {map.Edges.Count}";
        EmptyPanel.Visibility = map.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GraphCanvas.Width = map.Width;
        GraphCanvas.Height = map.Height;
        var nodes = map.Nodes.ToDictionary(node => node.ServerId);

        foreach (var edge in map.Edges)
        {
            var from = nodes[edge.ServerAId];
            var to = nodes[edge.ServerBId];
            var tooltip = EdgeTooltip(edge, from.Name, to.Name);
            var x1 = from.X + NodeWidth / 2;
            var y1 = from.Y + NodeHeight / 2;
            var x2 = to.X + NodeWidth / 2;
            var y2 = to.Y + NodeHeight / 2;
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    edge.IsAvailable ? "#4F8CFF" : "#D68A2E")),
                StrokeThickness = edge.IsAvailable ? 2.4 : 1.8,
                Opacity = edge.IsAvailable ? 0.78 : 0.65,
                ToolTip = tooltip
            };
            if (!edge.IsAvailable) line.StrokeDashArray = [5, 4];
            GraphCanvas.Children.Add(line);

            var arrow = CreateDirectedArrow(edge, x1, y1, x2, y2, tooltip);
            if (arrow is not null)
            {
                Panel.SetZIndex(arrow, 1);
                GraphCanvas.Children.Add(arrow);
            }
            edgeVisuals.Add((edge, line, arrow));
        }

        foreach (var node in map.Nodes)
        {
            var title = new TextBlock
            {
                Text = node.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.White
            };
            var group = new TextBlock
            {
                Text = node.GroupPath,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 196)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };
            var content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(group);
            var button = new Button
            {
                Width = NodeWidth,
                Height = NodeHeight,
                Padding = new Thickness(12, 7, 12, 7),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content,
                Tag = node.ServerId,
                ToolTip = $"{node.Name}\n{node.GroupPath}\nСвязей: {node.LinkCount}",
                Background = new SolidColorBrush(Color.FromRgb(31, 42, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(66, 91, 128)),
                BorderThickness = new Thickness(1.4)
            };
            button.Click += Node_Click;
            nodeButtons[node.ServerId] = button;
            Canvas.SetLeft(button, node.X);
            Canvas.SetTop(button, node.Y);
            Panel.SetZIndex(button, 2);
            GraphCanvas.Children.Add(button);
        }
        ApplyTransform();
    }

    internal static string EdgeTooltip(LinkMapEdge edge, string? nameA = null, string? nameB = null) =>
        LinkMapLayout.FormatTooltip(edge, nameA, nameB);

    internal static Polygon? CreateDirectedArrow(
        LinkMapEdge edge,
        double x1, double y1, double x2, double y2,
        string tooltip,
        Point? curveMidpoint = null,
        Color? fillColor = null)
    {
        if (!edge.IsDirected) return null;
        var dx = edge.AtoBAvailable ? (x2 - x1) : (x1 - x2);
        var dy = edge.AtoBAvailable ? (y2 - y1) : (y1 - y2);
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return null;
        var ux = dx / len;
        var uy = dy / len;
        var nx = -uy;
        var ny = ux;
        var mx = curveMidpoint?.X ?? ((x1 + x2) / 2);
        var my = curveMidpoint?.Y ?? ((y1 + y2) / 2);

        var isHighlight = fillColor.HasValue;
        var tipOffset = isHighlight ? 9.5 : 8.0;
        var baseOffset = isHighlight ? 8.5 : 7.0;
        var baseWidth = isHighlight ? 6.5 : 5.5;

        var color = fillColor ?? (Color)ColorConverter.ConvertFromString("#4F8CFF");

        return new Polygon
        {
            Points = [
                new Point(mx + ux * tipOffset, my + uy * tipOffset),
                new Point(mx - ux * baseOffset + nx * baseWidth, my - uy * baseOffset + ny * baseWidth),
                new Point(mx - ux * baseOffset - nx * baseWidth, my - uy * baseOffset - ny * baseWidth)
            ],
            Fill = new SolidColorBrush(color),
            ToolTip = tooltip
        };
    }

    private void Node_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Guid serverId) return;
        if (checkMode)
        {
            if (!checkedServerIds.Add(serverId)) checkedServerIds.Remove(serverId);
            UpdateNodeAppearance(button, serverId);
            UpdateCheckSelection();
            return;
        }
        var previousNode = selectedNode;
        selectedNode = null;
        if (previousNode is not null)
            UpdateNodeAppearance(previousNode, (Guid)previousNode.Tag);
        selectedNode = button;
        UpdateNodeAppearance(button, serverId);
        UpdateEdgeAppearance(serverId);
        selectServer(serverId);
    }

    private void CheckMode_Click(object sender, RoutedEventArgs e)
    {
        checkMode = !checkMode;
        AddSessionButton.Visibility = checkMode ? Visibility.Visible : Visibility.Collapsed;
        CheckSelectedButton.Visibility = checkMode ? Visibility.Visible : Visibility.Collapsed;
        CheckModeButton.Background = new SolidColorBrush(checkMode
            ? Color.FromRgb(38, 57, 92)
            : Color.FromRgb(31, 31, 31));
        CheckModeButton.BorderBrush = new SolidColorBrush(checkMode
            ? Color.FromRgb(79, 140, 255)
            : Color.FromRgb(58, 58, 58));
        if (!checkMode)
        {
            SessionPickerPopup.IsOpen = false;
            checkedServerIds.Clear();
        }
        foreach (var (serverId, button) in nodeButtons)
            UpdateNodeAppearance(button, serverId);
        UpdateEdgeAppearance(!checkMode && selectedNode?.Tag is Guid selectedId ? selectedId : null);
        UpdateCheckSelection();
    }

    private async void CheckSelected_Click(object sender, RoutedEventArgs e)
    {
        if (checkedServerIds.Count < 2) return;
        CheckSelectedButton.IsEnabled = false;
        try
        {
            await checkLinks(checkedServerIds.ToArray());
            map = LinkMapLayout.Build(config);
            checkedServerIds.Clear();
            BuildGraph();
        }
        finally { UpdateCheckSelection(); }
    }

    private void UpdateCheckSelection()
    {
        var count = checkedServerIds.Count;
        CheckSelectedButton.Content = count == 0
            ? "Проверить выбранные"
            : $"Проверить выбранные ({count})";
        CheckSelectedButton.IsEnabled = checkMode && count >= 2;
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        if (!checkMode) return;
        SessionSearchBox.Text = "";
        RefreshSessionChoices();
        SessionPickerPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => SessionSearchBox.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SessionSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshSessionChoices();

    private void RefreshSessionChoices()
    {
        if (SessionChoicesList is null) return;
        var choices = BuildSessionChoices(SessionSearchBox?.Text);
        refreshingSessionChoices = true;
        try
        {
            SessionChoicesList.ItemsSource = choices;
        }
        finally { refreshingSessionChoices = false; }
        NoSessionChoicesText.Visibility = choices.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private PickerChoice[] BuildSessionChoices(string? searchText)
        => ServerSelectionPolicy.Build(config, checkedServerIds, searchText)
            .Select(row => new PickerChoice(row.ServerId, row.ServerIds.ToArray(), row.Name,
                row.Details, row.Depth, row.IsGroup, row.IsChecked))
            .ToArray();

    private void SessionChoicesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (refreshingSessionChoices ||
            ItemsControl.ContainerFromElement(SessionChoicesList, e.OriginalSource as DependencyObject)
                is not ListBoxItem { DataContext: PickerChoice choice })
            return;
        var select = SelectionTogglePolicy.ShouldSelectAll(choice.ServerIds, checkedServerIds);
        foreach (var id in choice.ServerIds)
        {
            if (select) checkedServerIds.Add(id);
            else checkedServerIds.Remove(id);
            if (nodeButtons.TryGetValue(id, out var button)) UpdateNodeAppearance(button, id);
        }
        RefreshSessionChoices();
        UpdateCheckSelection();
        e.Handled = true;
    }

    private void SessionSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SessionPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && SessionChoicesList.Items.Count > 0)
            e.Handled = true;
    }

    private void UpdateNodeAppearance(Button button, Guid serverId)
    {
        var isChecked = checkMode && checkedServerIds.Contains(serverId);
        var isCurrent = !checkMode && ReferenceEquals(button, selectedNode);
        var isLinked = !checkMode && highlightedNeighborIds.Contains(serverId);
        button.Background = new SolidColorBrush(isChecked
            ? Color.FromRgb(45, 83, 61)
            : isCurrent ? Color.FromRgb(38, 57, 92) : Color.FromRgb(31, 42, 59));
        button.BorderBrush = new SolidColorBrush(isChecked
            ? Color.FromRgb(63, 190, 116)
            : isCurrent ? Color.FromRgb(79, 140, 255)
            : isLinked ? Color.FromRgb(255, 205, 67) : Color.FromRgb(66, 91, 128));
        button.BorderThickness = new Thickness(isChecked || isLinked ? 2.2 : 1.4);
    }

    private void UpdateEdgeAppearance(Guid? selectedServerId)
    {
        foreach (var visual in highlightedVisuals)
            GraphCanvas.Children.Remove(visual);
        highlightedVisuals.Clear();
        highlightedNeighborIds.Clear();

        foreach (var (edge, line, arrow) in edgeVisuals)
        {
            var isIncident = selectedServerId is not null &&
                (edge.ServerAId == selectedServerId || edge.ServerBId == selectedServerId);

            line.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                edge.IsAvailable ? "#4F8CFF" : "#D68A2E"));
            line.StrokeThickness = edge.IsAvailable ? 2.4 : 1.8;
            line.Opacity = selectedServerId is null ? (edge.IsAvailable ? 0.78 : 0.65) : 0.14;
            Panel.SetZIndex(line, 0);

            if (arrow is not null)
            {
                arrow.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F8CFF"));
                // When an incident edge is highlighted with a gold curve, hide the chord arrow
                // so it does not float in empty space away from the gold curve.
                arrow.Opacity = selectedServerId is null ? 0.88 : (isIncident ? 0.0 : 0.14);
                Panel.SetZIndex(arrow, 1);
            }
        }

        if (selectedServerId is not null)
        {
            var nodes = map.Nodes.ToDictionary(node => node.ServerId);
            var incident = map.Edges
                .Where(edge => edge.ServerAId == selectedServerId || edge.ServerBId == selectedServerId)
                .OrderBy(edge => edge.ServerAId == selectedServerId ? edge.ServerBId : edge.ServerAId)
                .ToArray();
            var offsets = LinkMapLayout.HighlightOffsets(incident.Length);
            for (var index = 0; index < incident.Length; index++)
            {
                var edge = incident[index];
                var neighborId = edge.ServerAId == selectedServerId ? edge.ServerBId : edge.ServerAId;
                highlightedNeighborIds.Add(neighborId);
                var from = nodes[selectedServerId.Value];
                var to = nodes[neighborId];
                var start = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);
                var end = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var offset = offsets[index];
                var control = new Point(
                    (start.X + end.X) / 2 - dy / length * offset,
                    (start.Y + end.Y) / 2 + dx / length * offset);
                var figure = new PathFigure { StartPoint = start, IsClosed = false };
                figure.Segments.Add(new QuadraticBezierSegment(control, end, true));

                var nodeA = nodes[edge.ServerAId];
                var nodeB = nodes[edge.ServerBId];
                var tooltip = EdgeTooltip(edge, nodeA.Name, nodeB.Name);

                var path = new System.Windows.Shapes.Path
                {
                    Data = new PathGeometry([figure]),
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 205, 67)),
                    StrokeThickness = 4.2,
                    Opacity = 1,
                    ToolTip = tooltip,
                    IsHitTestVisible = true
                };
                Panel.SetZIndex(path, 1);
                highlightedVisuals.Add(path);
                GraphCanvas.Children.Add(path);

                if (edge.IsDirected)
                {
                    var curveMidpoint = new Point(
                        0.25 * start.X + 0.5 * control.X + 0.25 * end.X,
                        0.25 * start.Y + 0.5 * control.Y + 0.25 * end.Y);

                    var ax = nodeA.X + NodeWidth / 2;
                    var ay = nodeA.Y + NodeHeight / 2;
                    var bx = nodeB.X + NodeWidth / 2;
                    var by = nodeB.Y + NodeHeight / 2;

                    var goldArrow = CreateDirectedArrow(
                        edge, ax, ay, bx, by, tooltip,
                        curveMidpoint: curveMidpoint,
                        fillColor: Color.FromRgb(255, 205, 67));

                    if (goldArrow is not null)
                    {
                        Panel.SetZIndex(goldArrow, 2);
                        highlightedVisuals.Add(goldArrow);
                        GraphCanvas.Children.Add(goldArrow);
                    }
                }
            }
        }
        foreach (var (serverId, button) in nodeButtons)
            UpdateNodeAppearance(button, serverId);
    }

    private void MapViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        panStart = e.GetPosition(MapViewport);
        panStartTranslation = translation;
        MapViewport.CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void MapViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (panStart is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(MapViewport);
        translation = panStartTranslation + (current - panStart.Value);
        ApplyTransform();
    }

    private void MapViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();
    private void MapViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) EndPan();
    }

    private void EndPan()
    {
        panStart = null;
        MapViewport.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void MapViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(MapViewport), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1.2);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Point(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2), 1 / 1.2);

    private void ZoomAt(Point screenPoint, double factor)
    {
        if (map.Nodes.Count == 0) return;
        var newScale = Math.Clamp(scale * factor, 0.15, 3.5);
        var world = new Point(
            (screenPoint.X - translation.X) / scale,
            (screenPoint.Y - translation.Y) / scale);
        translation = new Vector(
            screenPoint.X - world.X * newScale,
            screenPoint.Y - world.Y * newScale);
        scale = newScale;
        ApplyTransform();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToViewport();

    private void FitToViewport()
    {
        if (map.Nodes.Count == 0 || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
            return;
        scale = Math.Clamp(Math.Min(
            (MapViewport.ActualWidth - 80) / map.Width,
            (MapViewport.ActualHeight - 80) / map.Height), 0.15, 1);
        translation = new Vector(
            (MapViewport.ActualWidth - map.Width * scale) / 2,
            (MapViewport.ActualHeight - map.Height * scale) / 2);
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        GraphScale.ScaleX = scale;
        GraphScale.ScaleY = scale;
        GraphTranslate.X = translation.X;
        GraphTranslate.Y = translation.Y;
        ZoomText.Text = $"{scale * 100:0}%";
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T result) return result;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record PickerChoice(Guid? ServerId, Guid[] ServerIds, string Name,
        string Details, int Depth, bool IsGroup, bool? IsChecked)
    {
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public FontWeight FontWeight => IsGroup ? FontWeights.SemiBold : FontWeights.Normal;
        public Brush Foreground => IsGroup
            ? new SolidColorBrush(Color.FromRgb(190, 210, 245))
            : Brushes.White;
    }
}

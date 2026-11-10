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
using Pen = System.Windows.Media.Pen;

namespace KiTTYManager.App;

public partial class LinkMapWindow : Window
{
    internal const double NodeWidth = 190;
    internal const double NodeHeight = 58;
    private LinkMapModel map;
    private readonly ManagerConfig config;
    private readonly Action<Guid> selectServer;
    private readonly Func<IReadOnlyList<Guid>, Task> checkLinks;
    private static readonly SolidColorBrush AvailableBrush = CreateFrozenBrush("#4F8CFF", 0.78);
    private static readonly SolidColorBrush AvailableDimBrush = CreateFrozenBrush("#4F8CFF", 0.14);
    private static readonly SolidColorBrush UnavailableBrush = CreateFrozenBrush("#D68A2E", 0.65);
    private static readonly SolidColorBrush UnavailableDimBrush = CreateFrozenBrush("#D68A2E", 0.14);

    private static readonly Pen AvailablePen = CreateFrozenPen(AvailableBrush, 2.4);
    private static readonly Pen AvailableDimPen = CreateFrozenPen(AvailableDimBrush, 2.4);
    private static readonly Pen UnavailablePen = CreateFrozenPen(UnavailableBrush, 1.8, [5, 4]);
    private static readonly Pen UnavailableDimPen = CreateFrozenPen(UnavailableDimBrush, 1.8, [5, 4]);

    private static readonly SolidColorBrush ArrowBrush = CreateFrozenBrush("#4F8CFF", 0.88);
    private static readonly SolidColorBrush ArrowDimBrush = CreateFrozenBrush("#4F8CFF", 0.14);

    private static readonly SolidColorBrush GoldHighlightBrush = CreateFrozenBrush(Color.FromRgb(255, 205, 67));
    private static readonly Pen GoldHighlightPen = CreateFrozenPen(GoldHighlightBrush, 4.2);

    private static readonly SolidColorBrush NodeDefaultBgBrush = CreateFrozenBrush(Color.FromRgb(31, 42, 59));
    private static readonly SolidColorBrush NodeCurrentBgBrush = CreateFrozenBrush(Color.FromRgb(38, 57, 92));
    private static readonly SolidColorBrush NodeCheckedBgBrush = CreateFrozenBrush(Color.FromRgb(45, 83, 61));

    private static readonly SolidColorBrush NodeDefaultBorderBrush = CreateFrozenBrush(Color.FromRgb(66, 91, 128));
    private static readonly SolidColorBrush NodeCurrentBorderBrush = CreateFrozenBrush(Color.FromRgb(79, 140, 255));
    private static readonly SolidColorBrush NodeCheckedBorderBrush = CreateFrozenBrush(Color.FromRgb(63, 190, 116));
    private static readonly SolidColorBrush NodeLinkedBorderBrush = CreateFrozenBrush(Color.FromRgb(255, 205, 67));

    private static readonly SolidColorBrush NodeGroupTextBrush = CreateFrozenBrush(Color.FromRgb(170, 180, 196));

    private static SolidColorBrush CreateFrozenBrush(string hex, double opacity = 1.0)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness, DoubleCollection? dashes = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashes is not null)
        {
            var dashStyle = new DashStyle(dashes, 0);
            dashStyle.Freeze();
            pen.DashStyle = dashStyle;
        }
        pen.Freeze();
        return pen;
    }

    internal sealed class EdgeVisualHost : FrameworkElement
    {
        private readonly VisualCollection children;
        public DrawingVisual BaseVisual { get; } = new();
        public DrawingVisual HighlightVisual { get; } = new();
        private readonly List<(Geometry HitArea, bool IsFill, string Tooltip)> hitAreas = [];
        internal static readonly Pen HitTestPen;

        static EdgeVisualHost()
        {
            HitTestPen = new Pen(Brushes.Black, 8.0);
            HitTestPen.Freeze();
        }

        public EdgeVisualHost()
        {
            children = new VisualCollection(this) { BaseVisual, HighlightVisual };
            IsHitTestVisible = true;
        }

        protected override int VisualChildrenCount => children.Count;
        protected override Visual GetVisualChild(int index) => children[index];

        public void SetHitAreas(IEnumerable<(Geometry HitArea, bool IsFill, string Tooltip)> areas)
        {
            hitAreas.Clear();
            hitAreas.AddRange(areas);
        }

        internal static bool HitTest(Geometry geom, bool isFill, Point pt, Pen? pen = null) =>
            isFill ? geom.FillContains(pt) : geom.StrokeContains(pen ?? HitTestPen, pt);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pt = e.GetPosition(this);
            for (var i = hitAreas.Count - 1; i >= 0; i--)
            {
                var (geom, isFill, tip) = hitAreas[i];
                if (HitTest(geom, isFill, pt))
                {
                    ToolTip = tip;
                    return;
                }
            }
            ToolTip = null;
        }
    }

    internal static bool HitTestEdge(Geometry geom, bool isFill, Point pt, Pen? pen = null) =>
        EdgeVisualHost.HitTest(geom, isFill, pt, pen);

    private EdgeVisualHost? edgeHost;
    private readonly Dictionary<Guid, Button> nodeButtons = [];
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
        highlightedNeighborIds.Clear();
        selectedNode = null;
        SummaryText.Text = map.Nodes.Count == 0
            ? "Нет сессий со связями"
            : $"Сессий: {map.Nodes.Count} · Связей: {map.Edges.Count}";
        EmptyPanel.Visibility = map.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GraphCanvas.Width = map.Width;
        GraphCanvas.Height = map.Height;

        edgeHost = new EdgeVisualHost
        {
            Width = map.Width,
            Height = map.Height
        };
        Panel.SetZIndex(edgeHost, 0);
        GraphCanvas.Children.Add(edgeHost);
        RenderEdges(null);

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
                Foreground = NodeGroupTextBrush,
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
                Background = NodeDefaultBgBrush,
                BorderBrush = NodeDefaultBorderBrush,
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

    private static PathGeometry CreateArrowGeometry(
        LinkMapEdge edge,
        double x1, double y1, double x2, double y2,
        Point? curveMidpoint = null,
        bool isHighlight = false)
    {
        var dx = edge.AtoBAvailable ? (x2 - x1) : (x1 - x2);
        var dy = edge.AtoBAvailable ? (y2 - y1) : (y1 - y2);
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0)
        {
            var empty = new PathGeometry();
            empty.Freeze();
            return empty;
        }
        var ux = dx / len;
        var uy = dy / len;
        var nx = -uy;
        var ny = ux;
        var mx = curveMidpoint?.X ?? ((x1 + x2) / 2);
        var my = curveMidpoint?.Y ?? ((y1 + y2) / 2);

        var tipOffset = isHighlight ? 9.5 : 8.0;
        var baseOffset = isHighlight ? 8.5 : 7.0;
        var baseWidth = isHighlight ? 6.5 : 5.5;

        var figure = new PathFigure
        {
            StartPoint = new Point(mx + ux * tipOffset, my + uy * tipOffset),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment(new Point(mx - ux * baseOffset + nx * baseWidth, my - uy * baseOffset + ny * baseWidth), true));
        figure.Segments.Add(new LineSegment(new Point(mx - ux * baseOffset - nx * baseWidth, my - uy * baseOffset - ny * baseWidth), true));
        var geom = new PathGeometry([figure]);
        geom.Freeze();
        return geom;
    }

    private void RenderEdges(Guid? selectedServerId)
    {
        if (edgeHost is null) return;
        var nodes = map.Nodes.ToDictionary(node => node.ServerId);
        var hitAreas = new List<(Geometry HitArea, bool IsFill, string Tooltip)>();

        using (var dc = edgeHost.BaseVisual.RenderOpen())
        {
            foreach (var edge in map.Edges)
            {
                if (!nodes.TryGetValue(edge.ServerAId, out var from) ||
                    !nodes.TryGetValue(edge.ServerBId, out var to))
                    continue;

                var tooltip = EdgeTooltip(edge, from.Name, to.Name);
                var x1 = from.X + NodeWidth / 2;
                var y1 = from.Y + NodeHeight / 2;
                var x2 = to.X + NodeWidth / 2;
                var y2 = to.Y + NodeHeight / 2;

                var isIncident = selectedServerId is not null &&
                    (edge.ServerAId == selectedServerId || edge.ServerBId == selectedServerId);

                var pen = selectedServerId is null
                    ? (edge.IsAvailable ? AvailablePen : UnavailablePen)
                    : (edge.IsAvailable ? AvailableDimPen : UnavailableDimPen);

                var p1 = new Point(x1, y1);
                var p2 = new Point(x2, y2);
                dc.DrawLine(pen, p1, p2);

                var lineGeom = new LineGeometry(p1, p2);
                lineGeom.Freeze();
                hitAreas.Add((lineGeom, false, tooltip));

                if (edge.IsDirected)
                {
                    if (selectedServerId is null || !isIncident)
                    {
                        var arrowBrush = selectedServerId is null ? ArrowBrush : ArrowDimBrush;
                        var arrowGeom = CreateArrowGeometry(edge, x1, y1, x2, y2, isHighlight: false);
                        dc.DrawGeometry(arrowBrush, null, arrowGeom);
                        hitAreas.Add((arrowGeom, true, tooltip));
                    }
                }
            }
        }

        using (var dc = edgeHost.HighlightVisual.RenderOpen())
        {
            if (selectedServerId is not null)
            {
                var incident = map.Edges
                    .Where(edge => edge.ServerAId == selectedServerId || edge.ServerBId == selectedServerId)
                    .OrderBy(edge => edge.ServerAId == selectedServerId ? edge.ServerBId : edge.ServerAId)
                    .ToArray();
                var offsets = LinkMapLayout.HighlightOffsets(incident.Length);
                for (var index = 0; index < incident.Length; index++)
                {
                    var edge = incident[index];
                    var neighborId = edge.ServerAId == selectedServerId ? edge.ServerBId : edge.ServerAId;
                    if (!nodes.TryGetValue(selectedServerId.Value, out var from) ||
                        !nodes.TryGetValue(neighborId, out var to))
                        continue;

                    var start = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);
                    var end = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);
                    var dx = end.X - start.X;
                    var dy = end.Y - start.Y;
                    var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                    var offset = offsets[index];
                    var control = new Point(
                        (start.X + end.X) / 2 - dy / length * offset,
                        (start.Y + end.Y) / 2 + dx / length * offset);

                    var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
                    figure.Segments.Add(new QuadraticBezierSegment(control, end, true));
                    var geom = new PathGeometry([figure]);
                    geom.Freeze();

                    var nodeA = nodes[edge.ServerAId];
                    var nodeB = nodes[edge.ServerBId];
                    var tooltip = EdgeTooltip(edge, nodeA.Name, nodeB.Name);

                    dc.DrawGeometry(null, GoldHighlightPen, geom);
                    hitAreas.Add((geom, false, tooltip));

                    if (edge.IsDirected)
                    {
                        var curveMidpoint = new Point(
                            0.25 * start.X + 0.5 * control.X + 0.25 * end.X,
                            0.25 * start.Y + 0.5 * control.Y + 0.25 * end.Y);

                        var ax = nodeA.X + NodeWidth / 2;
                        var ay = nodeA.Y + NodeHeight / 2;
                        var bx = nodeB.X + NodeWidth / 2;
                        var by = nodeB.Y + NodeHeight / 2;

                        var goldArrowGeom = CreateArrowGeometry(
                            edge, ax, ay, bx, by, curveMidpoint: curveMidpoint, isHighlight: true);
                        dc.DrawGeometry(GoldHighlightBrush, null, goldArrowGeom);
                        hitAreas.Add((goldArrowGeom, true, tooltip));
                    }
                }
            }
        }

        edgeHost.SetHitAreas(hitAreas);
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
        button.Background = isChecked
            ? NodeCheckedBgBrush
            : isCurrent ? NodeCurrentBgBrush : NodeDefaultBgBrush;
        button.BorderBrush = isChecked
            ? NodeCheckedBorderBrush
            : isCurrent ? NodeCurrentBorderBrush
            : isLinked ? NodeLinkedBorderBrush : NodeDefaultBorderBrush;
        button.BorderThickness = new Thickness(isChecked || isLinked ? 2.2 : 1.4);
    }

    private void UpdateEdgeAppearance(Guid? selectedServerId)
    {
        highlightedNeighborIds.Clear();
        if (selectedServerId is not null)
        {
            foreach (var edge in map.Edges)
            {
                if (edge.ServerAId == selectedServerId)
                    highlightedNeighborIds.Add(edge.ServerBId);
                else if (edge.ServerBId == selectedServerId)
                    highlightedNeighborIds.Add(edge.ServerAId);
            }
        }

        RenderEdges(selectedServerId);

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

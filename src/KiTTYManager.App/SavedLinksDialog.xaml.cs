using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public delegate Task<IReadOnlyList<ConnectivityResult>> SavedLinkCheckHandler(
    Guid sourceServerId,
    IReadOnlyList<Guid> targetServerIds,
    CancellationToken cancellationToken);

public delegate Task SavedLinkConnectHandler(
    Guid viaServerId,
    CancellationToken cancellationToken);

public partial class SavedLinksDialog : Window
{
    private readonly ManagerConfig config;
    private readonly ManagedServer selectedServer;
    private readonly SavedLinkCheckHandler? checkLink;
    private readonly Action? saveConfig;
    private readonly SavedLinkConnectHandler? connectLink;
    private readonly ObservableCollection<SavedLinkTreeItem> roots = [];
    private CancellationTokenSource? checkCancellation;
    private CancellationTokenSource? connectCancellation;

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
        DescriptionText.Text = $"Связи для «{selectedServer.Name}». Стрелка показывает доступное направление SSH. " +
                               "Раскройте группы, чтобы увидеть серверы внутри.";
        LinksTree.ItemsSource = roots;
        BuildTree();
    }

    private void BuildTree()
    {
        roots.Clear();

        var links = config.Links
            .Where(link => link.FromServerId == selectedServer.Id || link.ToServerId == selectedServer.Id)
            .Select(link => (Link: link, CounterpartId: link.FromServerId == selectedServer.Id
                ? link.ToServerId
                : link.FromServerId))
            .Where(item => item.CounterpartId != selectedServer.Id && config.FindServer(item.CounterpartId) is not null)
            .GroupBy(item => item.CounterpartId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Link).ToList());

        if (links.Count > 0)
        {
            foreach (var group in config.Groups)
            {
                var groupNode = BuildGroupNode(group, links, group.Name);
                if (groupNode is not null) roots.Add(groupNode);
            }

            var ungrouped = config.UngroupedServers
                .Where(server => links.ContainsKey(server.Id))
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

        RefreshBranchStatuses();
        var hasLinks = LeafItems().Any();
        LinksTree.Visibility = hasLinks ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasLinks ? Visibility.Collapsed : Visibility.Visible;
        CheckAllButton.IsEnabled = hasLinks && checkLink is not null;
        DeleteSelectedButton.IsEnabled = false;
        ConnectSelectedButton.IsEnabled = false;
        StatusText.Text = hasLinks
            ? $"Сохранено связей: {LeafItems().Count()}"
            : "Нет связей для выбранного сервера";
    }

    private SavedLinkTreeItem? BuildGroupNode(
        ServerGroup group,
        IReadOnlyDictionary<Guid, List<ServerLink>> byServer,
        string path)
    {
        var node = SavedLinkTreeItem.Branch(group.Name, true, path);

        foreach (var server in group.Servers
                     .Where(server => byServer.ContainsKey(server.Id))
                     .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase))
            AddServerRelation(node, server, byServer[server.Id], path);

        foreach (var child in group.Groups)
        {
            var childNode = BuildGroupNode(child, byServer, $"{path} / {child.Name}");
            if (childNode is not null) node.AddChild(childNode);
        }

        return node.Children.Count == 0 ? null : node;
    }

    private void AddServerRelation(
        SavedLinkTreeItem parent,
        ManagedServer server,
        IReadOnlyList<ServerLink> links,
        string groupPath)
    {
        parent.AddChild(SavedLinkTreeItem.Leaf(selectedServer, server, links, groupPath));
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        if (checkLink is null || checkCancellation is not null || connectCancellation is not null) return;

        var leaves = LeafItems().ToList();
        if (config.BaseProxies.All(proxy => !proxy.Enabled))
        {
            ThemedMessageDialog.Show(this,
                "Нет включённых точек входа. Сначала настройте работающий SOCKS5.",
                "Проверка сохранённых связей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ThemedMessageDialog.Show(
                this,
                $"Будет последовательно проверено {leaves.Count} настоящих SSH-связей. Продолжить?",
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
            foreach (var sourceBatch in leaves.GroupBy(item => item.FromServerId))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var batch = sourceBatch.ToList();
                foreach (var item in batch)
                    item.SetStatus(LinkCheckState.Checking, "Проверяется…");
                RefreshBranchStatuses();
                var sourceName = config.FindServer(sourceBatch.Key)?.Name ?? "исходный сервер";
                StatusText.Text = $"Проверяю от «{sourceName}»: {batch.Count} связей";

                try
                {
                    var results = await checkLink(sourceBatch.Key,
                        batch.Select(item => item.ToServerId).Distinct().ToArray(), cancellation.Token);
                    foreach (var item in batch)
                    {
                        var result = results.FirstOrDefault(value => value.TargetId == item.ToServerId);
                        if (result is null)
                        {
                            item.SetStatus(LinkCheckState.Failed, "Сервер не был проверен");
                            continue;
                        }
                        item.ApplyResult(result);
                        if (result.Success) successful++;
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    foreach (var item in batch)
                        item.SetStatus(LinkCheckState.Unchecked, "Проверка отменена");
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var item in batch)
                        item.SetStatus(LinkCheckState.Failed, ShortMessage(ex.Message));
                }

                completed += batch.Count;
                RefreshBranchStatuses();
            }

            StatusText.Text = $"Проверено: {completed}. Доступно: {successful}. Недоступно: {completed - successful}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = $"Проверка отменена. Завершено: {completed} из {leaves.Count}.";
            RefreshBranchStatuses();
        }
        finally
        {
            if (ReferenceEquals(checkCancellation, cancellation)) checkCancellation = null;
            CheckAllButton.IsEnabled = true;
            CancelCheckButton.IsEnabled = true;
            CancelCheckButton.Visibility = Visibility.Collapsed;
            UpdateMarkedStatus();
            UpdateConnectButton();
        }
    }

    private void RefreshBranchStatuses()
    {
        foreach (var root in roots) root.RefreshAggregateStatus();
    }

    private IEnumerable<SavedLinkTreeItem> LeafItems() => roots.SelectMany(root => root.DescendantLeaves());

    private void MarkCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateMarkedStatus();

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
            CheckAllButton.IsEnabled = checkLink is not null && LeafItems().Any();
            UpdateMarkedStatus();
            UpdateConnectButton();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in LeafItems()) item.IsMarked = true;
        UpdateMarkedStatus();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in LeafItems()) item.IsMarked = false;
        UpdateMarkedStatus();
    }

    private void UpdateMarkedStatus()
    {
        var count = LeafItems().Count(item => item.IsMarked == true);
        DeleteSelectedButton.IsEnabled = count > 0 &&
            checkCancellation is null && connectCancellation is null;
        if (count > 0) StatusText.Text = $"Выбрано связей: {count}";
        UpdateConnectButton();
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
    public string DirectionDisplay { get; private init; } = "";
    public string GroupPath { get; private init; } = "";
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
    public ServerLink? Link { get; private init; }
    public IReadOnlyList<ServerLink> RelatedLinks { get; private init; } = [];
    public Guid FromServerId => Link?.FromServerId ?? Guid.Empty;
    public Guid ToServerId => Link?.ToServerId ?? Guid.Empty;
    public Guid CounterpartServerId { get; private init; }
    public bool CanConnectVia { get; private init; }

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
        ManagedServer selectedServer,
        ManagedServer server,
        IReadOnlyList<ServerLink> links,
        string groupPath)
    {
        var outgoing = links.FirstOrDefault(link => link.FromServerId == selectedServer.Id);
        var incoming = links.FirstOrDefault(link => link.ToServerId == selectedServer.Id);
        var link = outgoing ?? incoming ?? throw new InvalidOperationException("Связь не найдена.");
        var direction = outgoing is not null && incoming is not null ? "↔" : outgoing is not null ? "→" : "←";
        var item = new SavedLinkTreeItem(server.Name, false)
        {
            DirectionDisplay = direction,
            GroupPath = groupPath,
            Strategy = string.IsNullOrWhiteSpace(link.LastStrategy) ? "—" : link.LastStrategy,
            LastSuccessDisplay = FormatDate(link.LastSuccessUtc),
            Link = link,
            RelatedLinks = links,
            CounterpartServerId = server.Id,
            CanConnectVia = links.Any(candidate =>
                candidate.FromServerId == server.Id &&
                candidate.ToServerId == selectedServer.Id &&
                candidate.LastSuccessUtc is not null)
        };
        if (link.LastSuccessUtc is not null)
        {
            item.state = LinkCheckState.Succeeded;
            item.statusDetail = "Последняя проверка была успешной";
        }
        return item;
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

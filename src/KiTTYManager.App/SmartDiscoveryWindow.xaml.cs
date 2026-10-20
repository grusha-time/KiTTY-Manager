using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KiTTYManager.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace KiTTYManager.App;

public partial class SmartDiscoveryWindow : Window
{
    private readonly Func<ManagerConfig> getConfig;
    private readonly Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost;
    private readonly Action onSessionsCreated;
    private readonly Func<ManagedServer, string, bool>? hostKeyVerifier;
    private readonly Action<string>? log;
    private readonly Func<DiscoveredServerCandidate, IReadOnlyList<DiscoveredServerCandidate>, Task>? previewCandidate;
    private readonly Func<IReadOnlyList<ConnectivityPairChoice>, Task<bool>>? buildLinksToExistingGroupServers;

    private ManagerConfig LiveConfig => getConfig();

    private readonly ObservableCollection<DiscoveredServerCandidate> discoveredCandidates = [];
    private readonly ObservableCollection<ProblematicServerCandidate> problematicCandidates = [];

    private CancellationTokenSource? scanCts;
    private bool isScanning;
    private bool isCreatingSessions;
    private bool passwordsMasked;
    private bool groupPickerCloseOnMouseUp;
    private string rawPasswords = "";
    private Guid? targetGroupId;
    private IReadOnlyList<string> currentPasswords = [];

    public SmartDiscoveryWindow(
        Func<ManagerConfig> getConfig,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost = null,
        Action? onSessionsCreated = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null,
        Action<string>? log = null,
        Func<DiscoveredServerCandidate, IReadOnlyList<DiscoveredServerCandidate>, Task>? previewCandidate = null,
        Func<IReadOnlyList<ConnectivityPairChoice>, Task<bool>>? buildLinksToExistingGroupServers = null)
    {
        this.getConfig = getConfig;
        this.ensureJumphost = ensureJumphost;
        this.onSessionsCreated = onSessionsCreated ?? (() => { });
        this.hostKeyVerifier = hostKeyVerifier;
        this.log = log;
        this.previewCandidate = previewCandidate;
        this.buildLinksToExistingGroupServers = buildLinksToExistingGroupServers;

        Closing += (_, _) =>
        {
            try { scanCts?.Cancel(); } catch { }
        };

        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();

        var candidatesView = new System.Windows.Data.ListCollectionView(discoveredCandidates);
        candidatesView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(DiscoveredServerCandidate.IsDuplicate)));
        CandidatesGrid.ItemsSource = candidatesView;
        ProblemsGrid.ItemsSource = problematicCandidates;

        PopulateJumpHosts();
        SetTargetGroup(null);
        UpdateCounts();
    }

    public SmartDiscoveryWindow(
        ManagerConfig liveConfig,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost = null,
        Action? onSessionsCreated = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null,
        Action<string>? log = null,
        Func<DiscoveredServerCandidate, IReadOnlyList<DiscoveredServerCandidate>, Task>? previewCandidate = null,
        Func<IReadOnlyList<ConnectivityPairChoice>, Task<bool>>? buildLinksToExistingGroupServers = null)
        : this(() => liveConfig, ensureJumphost, onSessionsCreated, hostKeyVerifier, log, previewCandidate,
            buildLinksToExistingGroupServers)
    {
    }

    private void PopulateJumpHosts()
    {
        JumpHostComboBox.Items.Clear();
        var autoItem = new ComboBoxItem { Content = "Автоматически (подобрать рабочую точку входа)", Tag = "auto" };
        JumpHostComboBox.Items.Add(autoItem);
        JumpHostComboBox.SelectedItem = autoItem;

        var directItem = new ComboBoxItem { Content = "[Без JumpHost — прямое подключение]", Tag = "direct" };
        JumpHostComboBox.Items.Add(directItem);

        foreach (var proxy in LiveConfig.BaseProxies.Where(p => p.Enabled))
        {
            var item = new ComboBoxItem
            {
                Content = $"{proxy.Name} ({proxy.Host}:{proxy.Port})",
                Tag = proxy
            };
            JumpHostComboBox.Items.Add(item);
        }
    }

    private (BaseProxy? Proxy, bool Auto) GetEntryPointSelection() =>
        (JumpHostComboBox.SelectedItem as ComboBoxItem)?.Tag switch
        {
            BaseProxy proxy => (proxy, false),
            "auto" => (null, true),
            _ => (null, false)
        };

    private void SetTargetGroup(Guid? id)
    {
        targetGroupId = id;
        if (!id.HasValue)
        {
            TargetGroupBox.Text = "[Без группы — верхний уровень]";
            return;
        }
        var path = LiveConfig.GroupPath(id.Value);
        TargetGroupBox.Text = string.IsNullOrEmpty(path)
            ? LiveConfig.FindGroup(id.Value)?.Name ?? ""
            : path;
    }

    private void OpenGroupPicker()
    {
        GroupSearchBox.Text = "";
        RefreshGroupChoices();
        GroupPickerPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => GroupSearchBox.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TargetGroupBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GroupPickerPopup.IsOpen)
        {
            GroupPickerPopup.IsOpen = false;
            groupPickerCloseOnMouseUp = true;
        }
        e.Handled = true;
    }

    private void TargetGroupBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (groupPickerCloseOnMouseUp)
        {
            groupPickerCloseOnMouseUp = false;
            e.Handled = true;
            return;
        }
        OpenGroupPicker();
        e.Handled = true;
    }

    private void TargetGroupBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space or Key.Down)
        {
            OpenGroupPicker();
            e.Handled = true;
        }
    }

    private void GroupSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshGroupChoices();

    private void GroupSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            GroupPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            PickGroup(GroupChoicesList.SelectedItem as GroupChoice
                      ?? GroupChoicesList.Items.OfType<GroupChoice>().FirstOrDefault(c => c.Id is not null));
            e.Handled = true;
        }
    }

    private void GroupChoicesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PickGroup(GroupChoicesList.SelectedItem as GroupChoice);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            GroupPickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void GroupChoicesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: GroupChoice choice })
        {
            PickGroup(choice);
            e.Handled = true;
        }
    }

    private void PickGroup(GroupChoice? choice)
    {
        if (choice is null) return;
        SetTargetGroup(choice.Id);
        GroupPickerPopup.IsOpen = false;
    }

    private void RefreshGroupChoices()
    {
        var search = GroupSearchBox?.Text;
        var rows = GroupSelectionPolicy.Build(LiveConfig, null, search);
        var choices = new List<GroupChoice>
        {
            new(null, "[Без группы — верхний уровень]", "Верхний уровень", new Thickness(0))
        };
        choices.AddRange(rows.Select(row => new GroupChoice(row.GroupId, row.Name,
            $"{row.Path} · сессий: {row.ServerCount}", new Thickness(row.Depth * 14, 0, 0, 0))));

        GroupChoicesList.ItemsSource = choices;
        var selectedId = GroupSelectionPolicy.SelectForSearch(rows, targetGroupId, search);
        var selectedChoice = string.IsNullOrWhiteSpace(search)
            ? choices.FirstOrDefault(c => c.Id == selectedId) ?? choices[0]
            : choices.FirstOrDefault(c => selectedId.HasValue && c.Id == selectedId);
        GroupChoicesList.SelectedItem = selectedChoice;
        if (selectedChoice is not null) GroupChoicesList.ScrollIntoView(selectedChoice);
        NoGroupChoicesText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed record GroupChoice(Guid? Id, string Name, string Details, Thickness Indent);

    private void TogglePasswordsMask_Click(object sender, RoutedEventArgs e)
    {
        if (!passwordsMasked)
        {
            rawPasswords = PasswordsInputBox.Text;
            var lines = rawPasswords.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var maskedLines = lines.Select(line => string.IsNullOrWhiteSpace(line) ? "" : new string('•', Math.Min(Math.Max(line.Length, 6), 14)));
            PasswordsInputBox.Text = string.Join(Environment.NewLine, maskedLines);
            PasswordsInputBox.IsReadOnly = true;
            passwordsMasked = true;
            TogglePasswordsMaskButton.Content = "👁 Показать";
        }
        else
        {
            PasswordsInputBox.IsReadOnly = false;
            PasswordsInputBox.Text = rawPasswords;
            passwordsMasked = false;
            TogglePasswordsMaskButton.Content = "👁 Скрыть";
        }
    }

    private string GetEffectivePasswordsText() =>
        passwordsMasked ? rawPasswords : PasswordsInputBox.Text;

    private void ReportLog(string message)
    {
        var sanitized = message;
        foreach (var password in currentPasswords)
        {
            if (password.Length >= 3)
                sanitized = sanitized.Replace(password, "***", StringComparison.Ordinal);
        }
        log?.Invoke(sanitized);
    }

    private void AddCandidateInOrder(DiscoveredServerCandidate candidate)
    {
        if (candidate.IsDuplicate)
        {
            discoveredCandidates.Add(candidate);
            return;
        }
        var firstDuplicate = discoveredCandidates.FirstOrDefault(c => c.IsDuplicate);
        if (firstDuplicate is null)
        {
            discoveredCandidates.Add(candidate);
            return;
        }
        discoveredCandidates.Insert(discoveredCandidates.IndexOf(firstDuplicate), candidate);
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (isScanning) return;

        var hosts = SmartDiscoveryInputParser.ParseHosts(HostsInputBox.Text);
        if (hosts.Count == 0)
        {
            ThemedMessageDialog.Show(this, "Укажите хотя бы один валидный IP-адрес или хост.",
                "Массовое добавление", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var logins = SmartDiscoveryInputParser.ParseList(LoginsInputBox.Text);
        var passwords = SmartDiscoveryInputParser.ParsePasswords(GetEffectivePasswordsText());
        currentPasswords = passwords;

        var selection = GetEntryPointSelection();
        var targetSubgroup = TargetSubgroupBox.Text.Trim();
        var parallelism = int.TryParse((ParallelismComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var p) ? p : 16;

        scanCts = new CancellationTokenSource();
        SetScanningState(true);
        discoveredCandidates.Clear();
        problematicCandidates.Clear();
        UpdateCounts();

        try
        {
            var options = new SmartDiscoveryOptions
            {
                TargetHosts = hosts,
                Usernames = logins,
                Passwords = passwords,
                JumpHost = selection.Proxy,
                AutoSelectJumpHost = selection.Auto,
                TargetGroupId = targetGroupId,
                TargetSubgroupName = targetSubgroup,
                MaxParallelConnections = parallelism,
                Timeout = TimeSpan.FromSeconds(LiveConfig.ConnectionTimeoutSeconds),
                EndpointProbeTimeout = TimeSpan.FromSeconds(LiveConfig.EndpointProbeTimeoutSeconds)
            };

            var progress = new Progress<SmartDiscoveryProgress>(UpdateProgressUi);
            var engine = new SmartDiscoveryEngine(
                LiveConfig, options, progress: progress, hostKeyVerifier: hostKeyVerifier,
                log: ReportLog, ensureEntryPoint: ensureJumphost);

            var result = await engine.DiscoverAsync(scanCts.Token).ConfigureAwait(true);

            discoveredCandidates.Clear();
            foreach (var cand in result.Discovered)
                AddCandidateInOrder(cand);

            problematicCandidates.Clear();
            foreach (var prob in result.Problematic)
                problematicCandidates.Add(prob);

            UpdateCounts();
            if (discoveredCandidates.Count > 0)
                ResultsTabControl.SelectedIndex = 0;
            else if (problematicCandidates.Count > 0)
                ResultsTabControl.SelectedIndex = 1;
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Сканирование прервано пользователем.";
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Ошибка сканирования: {ex.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        scanCts?.Cancel();
    }

    private async void RetrySelected_Click(object sender, RoutedEventArgs e)
    {
        if (isScanning) return;

        var selectedProblems = problematicCandidates.Where(p => p.IsSelected && p.CanRetry).ToList();
        if (selectedProblems.Count == 0)
        {
            ThemedMessageDialog.Show(this, "Выберите хотя бы один проблемный узел для повторной проверки.",
                "Повтор проверки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var logins = SmartDiscoveryInputParser.ParseList(LoginsInputBox.Text);
        var passwords = SmartDiscoveryInputParser.ParsePasswords(GetEffectivePasswordsText());
        currentPasswords = passwords;
        var selection = GetEntryPointSelection();
        var targetSubgroup = TargetSubgroupBox.Text.Trim();
        var parallelism = int.TryParse((ParallelismComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var p) ? p : 16;

        scanCts = new CancellationTokenSource();
        SetScanningState(true);

        try
        {
            var options = new SmartDiscoveryOptions
            {
                Usernames = logins,
                Passwords = passwords,
                JumpHost = selection.Proxy,
                AutoSelectJumpHost = selection.Auto,
                TargetGroupId = targetGroupId,
                TargetSubgroupName = targetSubgroup,
                MaxParallelConnections = parallelism,
                Timeout = TimeSpan.FromSeconds(LiveConfig.ConnectionTimeoutSeconds),
                EndpointProbeTimeout = TimeSpan.FromSeconds(LiveConfig.EndpointProbeTimeoutSeconds)
            };

            var progress = new Progress<SmartDiscoveryProgress>(UpdateProgressUi);
            var engine = new SmartDiscoveryEngine(
                LiveConfig, options, progress: progress, initialGateways: discoveredCandidates.ToList(),
                hostKeyVerifier: hostKeyVerifier, log: ReportLog, ensureEntryPoint: ensureJumphost);

            var retryResult = await engine.RetryProblematicAsync(
                selectedProblems, options, existingGateways: discoveredCandidates.ToList(), scanCts.Token).ConfigureAwait(true);

            foreach (var newlyFound in retryResult.Discovered)
            {
                AddCandidateInOrder(newlyFound);
                var existingProb = problematicCandidates.FirstOrDefault(p => string.Equals(p.Host, newlyFound.Host, StringComparison.OrdinalIgnoreCase));
                if (existingProb != null)
                    problematicCandidates.Remove(existingProb);
            }

            foreach (var stillProblem in retryResult.Problematic)
            {
                var existingProb = problematicCandidates.FirstOrDefault(p => string.Equals(p.Host, stillProblem.Host, StringComparison.OrdinalIgnoreCase));
                if (existingProb != null)
                {
                    var index = problematicCandidates.IndexOf(existingProb);
                    problematicCandidates[index] = stillProblem;
                }
                else
                {
                    problematicCandidates.Add(stillProblem);
                }
            }

            UpdateCounts();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Повтор проверки остановлен пользователем.";
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Ошибка повторной проверки: {ex.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void UpdateProgressUi(SmartDiscoveryProgress p)
    {
        ScanProgressBar.Value = p.ProgressPercentage;
        StatusTextBlock.Text = string.IsNullOrEmpty(p.Host) ? p.Message : $"[{p.Host}] {p.Message}";
        UpdateCounts();
    }

    private void SetScanningState(bool scanning)
    {
        isScanning = scanning;
        StartScanButton.IsEnabled = !scanning;
        StopScanButton.IsEnabled = scanning;
        RetrySelectedButton.IsEnabled = !scanning && problematicCandidates.Any(p => p.IsSelected);
        CreateSessionsButton.IsEnabled = !scanning && discoveredCandidates.Count > 0;
        HostsInputBox.IsEnabled = !scanning;
        LoginsInputBox.IsEnabled = !scanning;
        JumpHostComboBox.IsEnabled = !scanning;
        TargetGroupBox.IsEnabled = !scanning;
    }

    private void UpdateCounts()
    {
        var duplicateCount = discoveredCandidates.Count(c => c.IsDuplicate);
        var newCount = discoveredCandidates.Count(c => !c.IsDuplicate);
        SummaryCountsTextBlock.Text = $"Новых: {newCount} | Дублей: {duplicateCount} | Проблемных: {problematicCandidates.Count}";
        RetrySelectedButton.IsEnabled = !isScanning && problematicCandidates.Any(p => p.IsSelected);
        CreateSessionsButton.IsEnabled = !isScanning && discoveredCandidates.Count > 0;
    }

    private void SelectAllCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in discoveredCandidates) c.IsSelected = !c.IsDuplicate;
    }

    private void DeselectAllCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in discoveredCandidates) c.IsSelected = false;
    }

    private void SelectAllProblems_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in problematicCandidates) p.IsSelected = true;
        UpdateCounts();
    }

    private void DeselectAllProblems_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in problematicCandidates) p.IsSelected = false;
        UpdateCounts();
    }

    private void BulkChangeGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = discoveredCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageDialog.Show(this, "Выберите хотя бы одну сессию в таблице.",
                "Массовое изменение группы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (rootGroupName, subgroupPath) =
            SmartDiscoverySessionCreator.ResolveGroupAssignment(LiveConfig, targetGroupId, TargetSubgroupBox.Text);
        foreach (var c in selected)
        {
            c.GroupId = targetGroupId;
            c.GroupName = rootGroupName;
            c.SubgroupName = subgroupPath;
        }

        ThemedMessageDialog.Show(this, $"Группа/подгруппа обновлена для {selected.Count} сессий.",
            "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoveredServerCandidate candidate)
        {
            candidate.ShowPassword = !candidate.ShowPassword;
        }
    }

    private void CopyCandidatePassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoveredServerCandidate candidate)
        {
            try
            {
                System.Windows.Clipboard.SetText(candidate.Password ?? "");
            }
            catch { }
        }
    }

    private void ToggleRootPasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoveredServerCandidate candidate)
            candidate.ShowRootPassword = !candidate.ShowRootPassword;
    }

    private void CopyCandidateRootPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoveredServerCandidate candidate)
        {
            try { System.Windows.Clipboard.SetText(candidate.PrivilegeEscalation.RootPassword ?? ""); }
            catch { }
        }
    }

    private void CandidateLinks_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiscoveredServerCandidate candidate) return;

        var draftUngrouped = new List<ManagedServer>(LiveConfig.UngroupedServers);
        foreach (var c in discoveredCandidates)
        {
            if (!draftUngrouped.Any(s => s.Id == c.Id))
            {
                draftUngrouped.Add(new ManagedServer
                {
                    Id = c.Id,
                    Name = c.ProposedName,
                    Host = c.Host,
                    Port = c.Port
                });
            }
        }

        var draftConfig = new ManagerConfig
        {
            Groups = LiveConfig.Groups,
            UngroupedServers = draftUngrouped,
            Links = new List<ServerLink>(LiveConfig.Links)
        };

        var draftServer = draftUngrouped.First(s => s.Id == candidate.Id);

        foreach (var fromId in candidate.DiscoveredLinksFrom)
        {
            if (!draftConfig.Links.Any(l => l.FromServerId == fromId && l.ToServerId == candidate.Id))
            {
                draftConfig.Links.Add(new ServerLink
                {
                    FromServerId = fromId,
                    ToServerId = candidate.Id,
                    Discovered = true,
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                    LastStrategy = "DirectTcpIp"
                });
            }
        }

        var dialog = new SavedLinksDialog(draftConfig, draftServer) { Owner = this };
        dialog.ShowDialog();

        // Sync back remaining links
        candidate.DiscoveredLinksFrom = draftConfig.Links
            .Where(l => l.ToServerId == candidate.Id && l.LastSuccessUtc != null)
            .Select(l => l.FromServerId)
            .Distinct()
            .ToList();
    }

    private async void CandidatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (isScanning || previewCandidate is null) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(source) is not null || FindAncestor<TextBoxBase>(source) is not null) return;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not DiscoveredServerCandidate candidate) return;

        CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        try
        {
            await previewCandidate(candidate, discoveredCandidates.ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Не удалось открыть сессию без сохранения: {ex.Message}",
                "Предпросмотр", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> SnapshotIndependentServerIds()
    {
        var config = LiveConfig;
        var snapshot = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [Guid.Empty] = ConnectivityBatchPlanner.DirectlyReachable(
                config, config.UngroupedServers.Select(server => server.Id))
        };
        foreach (var group in config.AllGroups())
            snapshot[group.Id] = ConnectivityBatchPlanner.DirectlyReachable(config, NestedServerIds(group));
        return snapshot;

        static IEnumerable<Guid> NestedServerIds(ServerGroup group) =>
            group.Servers.Select(server => server.Id)
                .Concat(group.Groups.SelectMany(NestedServerIds));
    }

    private async void CreateSessions_Click(object sender, RoutedEventArgs e)
    {
        if (isCreatingSessions) return;
        CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = discoveredCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageDialog.Show(this, "Не выбрано ни одной сессии для создания.",
                "Создание сессий", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        isCreatingSessions = true;
        CreateSessionsButton.IsEnabled = false;
        try
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? existingIndependentByGroup = null;
            if (LinkExistingIndependentServersCheckBox.IsChecked == true)
                existingIndependentByGroup = SnapshotIndependentServerIds();

            var selection = GetEntryPointSelection();
            var options = new SmartDiscoveryOptions
            {
                JumpHost = selection.Proxy,
                AutoSelectJumpHost = selection.Auto
            };

            var created = SmartDiscoverySessionCreator.CreateSessions(LiveConfig, discoveredCandidates, options);

            ReportLog($"Создание сессий: выбрано {selected.Count}, создано {created.Count}.");

            onSessionsCreated();

            if (created.Count > 0 && existingIndependentByGroup is not null &&
                buildLinksToExistingGroupServers is not null)
            {
                var pairs = SmartDiscoverySessionCreator.PlanLinksToExistingGroupServers(
                    LiveConfig, existingIndependentByGroup, created.Select(server => server.Id));
                if (pairs.Count > 0)
                {
                    try
                    {
                        if (!await buildLinksToExistingGroupServers(pairs))
                        {
                            ReportLog("Построение связей с существующими сессиями не выполнено.");
                            ThemedMessageDialog.Show(this,
                                "Сессии созданы, но связи с существующими серверами группы не построены: операция занята или отменена.",
                                "Создание связей", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        ReportLog("Построение связей с существующими сессиями отменено.");
                        ThemedMessageDialog.Show(this,
                            "Сессии созданы, но связи с существующими серверами группы не построены: операция отменена.",
                            "Создание связей", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch (Exception ex)
                    {
                        ReportLog($"Не удалось построить связи с существующими сессиями: {ex.Message}");
                        ThemedMessageDialog.Show(this,
                            "Сессии созданы, но связи с существующими серверами группы не построены.",
                            "Создание связей", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            ThemedMessageDialog.Show(this, $"Успешно создано сессий: {created.Count}",
                "Создание сессий", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Не удалось создать сессии: {ex.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            isCreatingSessions = false;
            CreateSessionsButton.IsEnabled = !isScanning && discoveredCandidates.Count > 0;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (isScanning)
            scanCts?.Cancel();
        Close();
    }
}

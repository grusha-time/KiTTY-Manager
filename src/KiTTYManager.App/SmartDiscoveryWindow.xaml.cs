using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class SmartDiscoveryWindow : Window
{
    private readonly Func<ManagerConfig> getConfig;
    private readonly Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost;
    private readonly Action onSessionsCreated;
    private readonly Func<ManagedServer, string, bool>? hostKeyVerifier;

    private ManagerConfig LiveConfig => getConfig();

    private readonly ObservableCollection<DiscoveredServerCandidate> discoveredCandidates = [];
    private readonly ObservableCollection<ProblematicServerCandidate> problematicCandidates = [];

    private CancellationTokenSource? scanCts;
    private bool isScanning;
    private bool passwordsMasked;
    private string rawPasswords = "";

    public SmartDiscoveryWindow(
        Func<ManagerConfig> getConfig,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost = null,
        Action? onSessionsCreated = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null)
    {
        this.getConfig = getConfig;
        this.ensureJumphost = ensureJumphost;
        this.onSessionsCreated = onSessionsCreated ?? (() => { });
        this.hostKeyVerifier = hostKeyVerifier;

        Closing += (_, _) =>
        {
            try { scanCts?.Cancel(); } catch { }
        };

        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();

        CandidatesGrid.ItemsSource = discoveredCandidates;
        ProblemsGrid.ItemsSource = problematicCandidates;

        PopulateJumpHosts();
        PopulateGroups();
        UpdateCounts();
    }

    public SmartDiscoveryWindow(
        ManagerConfig liveConfig,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureJumphost = null,
        Action? onSessionsCreated = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null)
        : this(() => liveConfig, ensureJumphost, onSessionsCreated, hostKeyVerifier)
    {
    }

    private void PopulateJumpHosts()
    {
        JumpHostComboBox.Items.Clear();
        var directItem = new ComboBoxItem { Content = "[Без JumpHost — прямое подключение]", Tag = null };
        JumpHostComboBox.Items.Add(directItem);
        JumpHostComboBox.SelectedItem = directItem;

        foreach (var proxy in LiveConfig.BaseProxies.Where(p => p.Enabled))
        {
            var item = new ComboBoxItem
            {
                Content = $"{proxy.Name} ({proxy.Host}:{proxy.Port})",
                Tag = proxy
            };
            JumpHostComboBox.Items.Add(item);
            if (LiveConfig.PreferredProxyId.HasValue && proxy.Id == LiveConfig.PreferredProxyId.Value)
            {
                JumpHostComboBox.SelectedItem = item;
            }
        }
    }

    private void PopulateGroups()
    {
        TargetGroupComboBox.Items.Clear();
        var rootItem = new ComboBoxItem { Content = "[Без группы — верхний уровень]", Tag = null };
        TargetGroupComboBox.Items.Add(rootItem);
        TargetGroupComboBox.SelectedItem = rootItem;

        foreach (var group in LiveConfig.AllGroups())
        {
            var path = LiveConfig.GroupPath(group.Id);
            var item = new ComboBoxItem
            {
                Content = string.IsNullOrEmpty(path) ? group.Name : path,
                Tag = group.Id
            };
            TargetGroupComboBox.Items.Add(item);
        }
    }

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

        var selectedProxy = (JumpHostComboBox.SelectedItem as ComboBoxItem)?.Tag as BaseProxy;
        var selectedGroupId = (TargetGroupComboBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        var targetSubgroup = TargetSubgroupBox.Text.Trim();
        var parallelism = int.TryParse((ParallelismComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var p) ? p : 4;

        scanCts = new CancellationTokenSource();
        SetScanningState(true);
        discoveredCandidates.Clear();
        problematicCandidates.Clear();
        UpdateCounts();

        try
        {
            if (selectedProxy != null && ensureJumphost != null)
            {
                StatusTextBlock.Text = $"Запуск и проверка точки входа {selectedProxy.Name}...";
                var ready = await ensureJumphost(selectedProxy, scanCts.Token).ConfigureAwait(true);
                if (!ready)
                {
                    ThemedMessageDialog.Show(this, $"Не удалось запустить точку входа {selectedProxy.Name}.",
                        "Ошибка точки входа", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetScanningState(false);
                    return;
                }
            }

            var options = new SmartDiscoveryOptions
            {
                TargetHosts = hosts,
                Usernames = logins,
                Passwords = passwords,
                JumpHost = selectedProxy,
                TargetGroupId = selectedGroupId,
                TargetSubgroupName = targetSubgroup,
                MaxParallelConnections = parallelism,
                Timeout = TimeSpan.FromSeconds(LiveConfig.ConnectionTimeoutSeconds),
                EndpointProbeTimeout = TimeSpan.FromSeconds(LiveConfig.EndpointProbeTimeoutSeconds)
            };

            var progress = new Progress<SmartDiscoveryProgress>(UpdateProgressUi);
            var engine = new SmartDiscoveryEngine(LiveConfig, options, progress: progress, hostKeyVerifier: hostKeyVerifier);

            var result = await engine.DiscoverAsync(scanCts.Token).ConfigureAwait(true);

            discoveredCandidates.Clear();
            foreach (var cand in result.Discovered)
                discoveredCandidates.Add(cand);

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
        var selectedProxy = (JumpHostComboBox.SelectedItem as ComboBoxItem)?.Tag as BaseProxy;
        var selectedGroupId = (TargetGroupComboBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        var targetSubgroup = TargetSubgroupBox.Text.Trim();
        var parallelism = int.TryParse((ParallelismComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var p) ? p : 4;

        scanCts = new CancellationTokenSource();
        SetScanningState(true);

        try
        {
            var options = new SmartDiscoveryOptions
            {
                Usernames = logins,
                Passwords = passwords,
                JumpHost = selectedProxy,
                TargetGroupId = selectedGroupId,
                TargetSubgroupName = targetSubgroup,
                MaxParallelConnections = parallelism,
                Timeout = TimeSpan.FromSeconds(LiveConfig.ConnectionTimeoutSeconds),
                EndpointProbeTimeout = TimeSpan.FromSeconds(LiveConfig.EndpointProbeTimeoutSeconds)
            };

            var progress = new Progress<SmartDiscoveryProgress>(UpdateProgressUi);
            var engine = new SmartDiscoveryEngine(
                LiveConfig, options, progress: progress, initialGateways: discoveredCandidates.ToList(), hostKeyVerifier: hostKeyVerifier);

            var retryResult = await engine.RetryProblematicAsync(
                selectedProblems, options, existingGateways: discoveredCandidates.ToList(), scanCts.Token).ConfigureAwait(true);

            foreach (var newlyFound in retryResult.Discovered)
            {
                discoveredCandidates.Add(newlyFound);
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
    }

    private void UpdateCounts()
    {
        SummaryCountsTextBlock.Text = $"Найдено сессий: {discoveredCandidates.Count} | Проблемных: {problematicCandidates.Count}";
        RetrySelectedButton.IsEnabled = !isScanning && problematicCandidates.Any(p => p.IsSelected);
        CreateSessionsButton.IsEnabled = !isScanning && discoveredCandidates.Count > 0;
    }

    private void SelectAllCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in discoveredCandidates) c.IsSelected = true;
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

        var targetGroupId = (TargetGroupComboBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        var groupName = targetGroupId.HasValue ? LiveConfig.FindGroup(targetGroupId.Value)?.Name : "";
        var subgroupName = TargetSubgroupBox.Text.Trim();

        foreach (var c in selected)
        {
            c.GroupId = targetGroupId;
            c.GroupName = groupName;
            c.SubgroupName = subgroupName;
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

    private void CreateSessions_Click(object sender, RoutedEventArgs e)
    {
        CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = discoveredCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ThemedMessageDialog.Show(this, "Не выбрано ни одной сессии для создания.",
                "Создание сессий", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var selectedProxy = (JumpHostComboBox.SelectedItem as ComboBoxItem)?.Tag as BaseProxy;
            var options = new SmartDiscoveryOptions
            {
                JumpHost = selectedProxy
            };

            var created = SmartDiscoverySessionCreator.CreateSessions(LiveConfig, discoveredCandidates, options);

            onSessionsCreated();

            ThemedMessageDialog.Show(this, $"Успешно создано сессий: {created.Count}",
                "Создание сессий", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Не удалось создать сессии: {ex.Message}",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (isScanning)
            scanCts?.Cancel();
        Close();
    }
}

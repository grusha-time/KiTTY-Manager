using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KiTTYManager.Core;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace KiTTYManager.App;

public partial class MainWindow : Window
{
    private const string KittyProxyProperty = "__proxy";
    private readonly string dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private string CurrentRouteLogPath => RuntimeLogWriter.DailyPath(
        Path.Combine(AppContext.BaseDirectory, "Data", "Logs"), DateTimeOffset.Now);
    private string ConsoleStorePath => Path.Combine(dataDirectory, "jumphost-consoles.json");
    private readonly string configPath;
    private readonly SshConnectionService ssh = new();
    private readonly List<ActiveRoute> activeRoutes = [];
    private ManagerConfig config;
    private ManagedServer? selectedServer;
    private ServerGroup? selectedGroup;
    private Point dragStart;
    private bool dragStartedOnRow;
    private bool forceExit;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private CancellationTokenSource? operationCancellation;
    private readonly List<KittyFieldConflict> pendingKittyConflicts = [];
    private readonly object linkSaveLock = new();
    private readonly Dictionary<Guid, string> lastRouteDisplays = [];
    private readonly BackgroundProbeRegistry backgroundRouteProbes = new();
    private readonly JumphostProcessRegistry jumphostProcesses = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> jumphostStartupGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> accessScriptGates = new();
    private LinkMapWindow? linkMapWindow;
    private bool configAvailable = true;
    private RequiredRouteOption[] requiredRouteOptions = [];
    private RequiredRouteOption? selectedRequiredRouteOption;
    private bool updatingRequiredRoutePicker;
    private readonly DispatcherTimer accessScriptAlarm = new()
        { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource accessScriptAlarmCancellation = new();
    private bool accessScriptAlarmRunning;
    private bool accessScriptAlarmRerunRequested;
    private readonly HashSet<Guid> accessStartupPreflightCompleted = [];
    private readonly Dictionary<Guid, DateTimeOffset> accessPromptCancelledUtc = [];

    public MainWindow()
    {
        InitializeComponent();
        KittyRoutedSession.CleanupStaleFiles(Path.Combine(AppContext.BaseDirectory, "KiTTY", "Sessions"));
        KittySessionWriter.RelocateLegacyBackups(Path.Combine(AppContext.BaseDirectory, "KiTTY", "Sessions"));
        configPath = Path.Combine(dataDirectory, "config.json");
        try { config = ConfigStore.Load(configPath, migratePlaintextSecrets: true); }
        catch (Exception ex)
        {
            config = new();
            configAvailable = false;
            ThemedMessageDialog.Show(null,
                ex.Message + "\n\nМенеджер будет закрыт, исходный config.json не изменён.",
                "Ошибка конфигурации", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        CleanupOrphanedFirefoxProfiles();
        if (configAvailable) MigrateLegacyPrototypeConfig();
        RestoreRememberedConsoles();
        ssh.Timeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds);
        ssh.EndpointProbeTimeout = TimeSpan.FromSeconds(config.EndpointProbeTimeoutSeconds);
        ssh.HostKeyVerifier = ConfirmHostKey;
        ssh.HostKeyMismatchVerifier = ConfirmChangedHostKey;
        ssh.Trace = traceEvent =>
        {
            var logMessage = $"SSH stage={traceEvent.Stage}; subject={traceEvent.Subject}; status={traceEvent.Status}" +
                             (traceEvent.Error is null ? "" : "; exceptions=" + string.Join(" --> ", ExceptionChain(traceEvent.Error).Select(item => $"{item.GetType().Name}: {item.Message}")));
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                RouteLog(logMessage);
                Status(FormatSshStatus(traceEvent));
            });
        };
        RefreshAll();
        accessScriptAlarm.Tick += AccessScriptAlarm_Tick;
        Loaded += (_, _) =>
        {
            if (!configAvailable)
            {
                forceExit = true;
                Close();
                return;
            }
            ConfigureFirstCloseBehavior();
            PromptForFirefoxTemplateProfile();
            ShowPendingKittyConflicts();
            OfferDuplicateConsoleCleanup();
            accessScriptAlarm.Start();
            _ = RunAccessScriptAlarmAsync();
        };
    }

    private void MigrateLegacyPrototypeConfig()
    {
        var changed = false;
        foreach (var server in config.AllServers())
            if (server.Name.Contains('%') && !string.IsNullOrWhiteSpace(server.SourceSessionPath))
            {
                server.Name = KittySessionImporter.DecodeSessionName(Path.GetFileName(server.SourceSessionPath)); changed = true;
            }

        foreach (var legacy in config.Groups.Where(g => g.Name.StartsWith("Импорт KiTTY ", StringComparison.Ordinal)).ToList())
        {
            foreach (var server in AllNestedServers(legacy))
                if (config.UngroupedServers.All(s => s.Id != server.Id)) config.UngroupedServers.Add(server);
            config.Groups.Remove(legacy); changed = true;
        }
        changed |= config.Groups.RemoveAll(g => g.Name == "Пример региона" && !AllNestedServers(g).Any()) > 0;
        var removed = config.BaseProxies.RemoveAll(p => p.Name.StartsWith("Импорт ", StringComparison.Ordinal));
        changed |= removed > 0;
        changed |= RefreshMissingImportedCredentials();
        changed |= SyncBundledKittySessions();
        if (config.SchemaVersion < 3)
        {
            // Older builds persisted an unknown host key and preferred SOCKS
            // before the complete chain had authenticated successfully.
            foreach (var server in config.AllServers()) server.HostKeyFingerprint = "";
            config.PreferredProxyId = null;
            config.SchemaVersion = 3;
            changed = true;
        }
        if (config.SchemaVersion < 6)
        {
            // Schema 5 ignored a property forever. Its exact value pair is unknown,
            // so show those differences again instead of hiding future changes.
            foreach (var server in config.AllServers()) server.IgnoredKittyProperties.Clear();
            config.SchemaVersion = 6; changed = true;
        }
        if (config.SchemaVersion < 7)
        {
            config.SchemaVersion = 7;
            changed = true;
        }
        if (changed) SaveConfig();
    }

    private bool SyncBundledKittySessions()
    {
        var sessionsDirectory = Path.Combine(AppContext.BaseDirectory, "KiTTY", "Sessions");
        if (!Directory.Exists(sessionsDirectory)) return false;
        try
        {
            var result = ImportOrUpdateSessions(sessionsDirectory);
            RouteLog($"Автоимпорт KiTTY: добавлено={result.Added}; обновлено={result.Updated}; конфликтов={result.Conflicts}");
            return result.Changed;
        }
        catch (Exception ex) { RouteLog($"Автоимпорт KiTTY не выполнен: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private (int Added, int Updated, int Conflicts, bool Changed) ImportOrUpdateSessions(string sessionsDirectory)
    {
        var imported = KittySessionImporter.ImportDirectory(sessionsDirectory);
        var current = config.AllServers().ToList();
        var added = 0; var updated = 0;
        foreach (var fresh in imported)
        {
            var existing = current.FirstOrDefault(server =>
                SamePath(server.SourceSessionPath, fresh.SourceSessionPath) ||
                (server.Name.Equals(fresh.Name, StringComparison.CurrentCultureIgnoreCase) &&
                 server.Host.Equals(fresh.Host, StringComparison.OrdinalIgnoreCase) && server.Port == fresh.Port));
            if (existing is null)
            {
                fresh.KittyBaseline = ImportedSessionMerger.Snapshot(fresh);
                config.UngroupedServers.Add(fresh); current.Add(fresh); added++; continue;
            }

            pendingKittyConflicts.AddRange(ImportedSessionMerger.Apply(existing, fresh));
            updated++;
        }
        return (added, updated, pendingKittyConflicts.Count, added > 0 || updated > 0);
    }

    private void ShowPendingKittyConflicts()
    {
        if (config.SuppressKittyChangeNotifications) return;
        if (!pendingKittyConflicts.Any(conflict => !KittyChangeIgnore.Matches(conflict.Server,
                conflict.PropertyName, conflict.ManagerValue, conflict.KittyValue))) return;
        ThemedMessageDialog.Show(this,
            "Обнаружены изменения KiTTY. Полный список, включая настройки proxy, " +
            "доступен в меню ⋯ → «Изменения KiTTY…».",
            "Изменения KiTTY", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string DetailedValue(string value) => value.Length == 0
        ? "(пусто; длина 0)"
        : $"«{value.Replace(" ", "·", StringComparison.Ordinal).Replace("\t", "⇥", StringComparison.Ordinal)}» (длина {value.Length}; · = пробел)";

    private static string ProxyValue(string host, int port) =>
        $"{(ProxyEndpointComparer.HostsEquivalent(host, "127.0.0.1") ? "loopback" : host.Trim().ToLowerInvariant())}:{port}";

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private bool RefreshMissingImportedCredentials()
    {
        var upgradeScripts = config.SchemaVersion < 2;
        var pending = config.AllServers().Where(s => !string.IsNullOrEmpty(s.SourceSessionPath) && File.Exists(s.SourceSessionPath)
            && (upgradeScripts || s.Password.Length == 0 || s.Username.Length == 0)).ToList();
        if (pending.Count == 0) { if (!upgradeScripts) return false; config.SchemaVersion = 2; return true; }
        var changed = false;
        foreach (var rootGroup in pending.GroupBy(s => FindSessionsRoot(s.SourceSessionPath!)))
        {
            if (rootGroup.Key is null) continue;
            IReadOnlyList<ManagedServer> imported;
            try { imported = KittySessionImporter.ImportDirectory(rootGroup.Key); } catch { continue; }
            var byPath = imported.Where(s => s.SourceSessionPath is not null).ToDictionary(s => Path.GetFullPath(s.SourceSessionPath!), StringComparer.OrdinalIgnoreCase);
            foreach (var current in rootGroup)
            {
                if (!byPath.TryGetValue(Path.GetFullPath(current.SourceSessionPath!), out var fresh)) continue;
                if (current.Username.Length == 0 && fresh.Username.Length > 0 &&
                    !current.ManagerOverrides.Contains(nameof(ManagedServer.Username), StringComparer.Ordinal))
                { current.Username = fresh.Username; changed = true; }
                if (current.Password.Length == 0 && fresh.Password.Length > 0 &&
                    !current.ManagerOverrides.Contains(nameof(ManagedServer.Password), StringComparer.Ordinal))
                { current.Password = fresh.Password; changed = true; }
                if (upgradeScripts)
                {
                    current.UseKeyboardInteractive = fresh.UseKeyboardInteractive;
                    if (!current.ManagerOverrides.Contains(nameof(ManagedServer.RootLogin), StringComparer.Ordinal))
                        current.RootLogin = fresh.RootLogin;
                    if (!current.ManagerOverrides.Contains(nameof(ManagedServer.RootPassword), StringComparer.Ordinal))
                        current.RootPassword = fresh.RootPassword;
                    current.SourceScriptPath = fresh.SourceScriptPath; current.SourceScriptContent = fresh.SourceScriptContent;
                    changed = true;
                }
            }
        }
        if (upgradeScripts) { config.SchemaVersion = 2; changed = true; }
        return changed;
    }

    private static string? FindSessionsRoot(string sessionPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sessionPath)!);
        for (var depth = 0; depth < 5 && directory is not null; depth++, directory = directory.Parent)
        {
            if (directory.Name.Equals("Sessions", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
            if (directory.Parent is not null && (File.Exists(Path.Combine(directory.Parent.FullName, "kitty.ini")) || File.Exists(Path.Combine(directory.Parent.FullName, "kitty.exe")))) return directory.FullName;
        }
        return Path.GetDirectoryName(sessionPath);
    }

    private void ConfigureFirstCloseBehavior()
    {
        if (config.ClosePreferenceConfigured) return;
        config.CloseToTray = ThemedMessageDialog.Show(this,
            "Что делать при нажатии на крестик?\n\nДа — свернуть в системный трей и сохранить активные туннели.\nНет — полностью закрыть приложение и туннели.\n\nВыбор можно изменить в настройках.",
            "Поведение при закрытии", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        config.ClosePreferenceConfigured = true;
        SaveConfig();
    }

    private void PromptForFirefoxTemplateProfile()
    {
        if (config.FirefoxTemplateProfile.Length > 0) return;
        if (ThemedMessageDialog.Show(this,
                "Для корректной работы веб-интерфейсов укажите рабочий профиль Firefox (с отключённым чекбоксом «Отправлять DNS-запросы через прокси при использовании SOCKS 5»).\n\nКак найти: откройте Firefox, введите в адресную строку about:support и скопируйте путь из поля «Папка профиля».\n\nВыбрать папку профиля сейчас?",
                "Профиль Firefox для веб-интерфейсов", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку рабочего профиля Firefox (about:support → «Папка профиля»)",
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        config.FirefoxTemplateProfile = dialog.SelectedPath;
        SaveConfig();
        Status($"Шаблон профиля Firefox: {config.FirefoxTemplateProfile}");
    }

    private void RefreshAll()
    {
        RefreshGroups(); RefreshSessions(); ShowServer(selectedServer);
        Status($"Сессий: {config.AllServers().Count()} • Групп: {config.AllGroups().Count()} • Jumphost’ов: {config.BaseProxies.Count}");
    }

    private void RefreshGroups()
    {
        GroupTree.Items.Clear();
        var query = GroupSearchBox?.Text.Trim() ?? "";
        foreach (var group in config.Groups.OrderBy(g => g.Name))
            if (query.Length == 0 || GroupMatches(group, query))
                GroupTree.Items.Add(CreateGroupItem(group, query));
    }

    private static TreeViewItem CreateGroupItem(ServerGroup group, string query = "", bool ancestorMatched = false)
    {
        var count = AllNestedServers(group).Count();
        var item = new TreeViewItem { Header = $"{group.Name}   {count}", Tag = group, IsExpanded = true, Padding = new Thickness(5) };
        var matched = ancestorMatched || query.Length == 0 || group.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        foreach (var child in group.Groups.OrderBy(g => g.Name))
            if (matched || GroupMatches(child, query))
                item.Items.Add(CreateGroupItem(child, query, matched));
        return item;
    }

    private static bool GroupMatches(ServerGroup group, string query) =>
        group.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        group.Groups.Any(child => GroupMatches(child, query));

    private void RefreshSessions()
    {
        var query = SearchBox?.Text.Trim() ?? "";
        var rows = config.AllServers()
            .Where(s => selectedGroup is null || AllNestedServers(selectedGroup).Any(candidate => candidate.Id == s.Id))
            .Where(s => query.Length == 0 || SearchableText(s).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(s => new SessionRow(s, GroupNameFor(s)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SessionsGrid.ItemsSource = rows;
        SessionCountText.Text = query.Length == 0 ? $"{rows.Count} сессий" : $"Найдено: {rows.Count}";
        GroupFilterText.Text = selectedGroup is null ? "" : $"Группа: {FindGroupPath(config.Groups, selectedGroup.Id)}";
        ClearGroupFilterButton.Visibility = selectedGroup is null ? Visibility.Collapsed : Visibility.Visible;
        if (selectedServer is not null) SessionsGrid.SelectedItem = rows.FirstOrDefault(r => r.Server.Id == selectedServer.Id);
    }

    private string SearchableText(ManagedServer server)
    {
        var web = server.WebInterfaces.SelectMany(item =>
            new[] { item.Name, item.Url, item.ResolverAddress, item.Username, item.Password });
        return string.Join('\n', new[] { server.Name, server.Host, server.Port.ToString(), server.Username, server.Password,
            server.PrivateKeyPath, server.PrivateKeyPassphrase,
            server.RootLogin, server.RootPassword, server.SourceScriptPath ?? "", server.SourceScriptContent, GroupNameFor(server) }.Concat(web));
    }

    private string GroupNameFor(ManagedServer server)
    {
        foreach (var group in config.AllGroups())
            if (group.Servers.Any(s => s.Id == server.Id)) return FindGroupPath(config.Groups, group.Id) ?? group.Name;
        return "Без группы";
    }

    private static string? FindGroupPath(IEnumerable<ServerGroup> groups, Guid id, string prefix = "")
    {
        foreach (var group in groups)
        {
            var path = prefix.Length == 0 ? group.Name : $"{prefix} / {group.Name}";
            if (group.Id == id) return path;
            var nested = FindGroupPath(group.Groups, id, path); if (nested is not null) return nested;
        }
        return null;
    }

    private void ShowServer(ManagedServer? server)
    {
        selectedServer = server;
        DetailsPanel.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionHint.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
        if (server is null)
        {
            WebGrid.ItemsSource = null;
            BackupGrid.ItemsSource = null;
            LastRouteText.Visibility = Visibility.Collapsed;
            return;
        }
        PasswordBox.ResetToHidden();
        PrivateKeyPassphraseBox.ResetToHidden();
        RootPasswordBox.ResetToHidden();
        ServerNameBox.Text = server.Name; HostBox.Text = server.Host; PortBox.Text = server.Port.ToString();
        UsernameBox.Text = server.Username; PasswordBox.Value = server.Password;
        PrivateKeyPathBox.Text = server.PrivateKeyPath;
        PrivateKeyPassphraseBox.Value = server.PrivateKeyPassphrase;
        RootLoginBox.Text = server.RootLogin; RootPasswordBox.Value = server.RootPassword;
        ShellPromptBox.Text = server.ShellPrompt;
        ImportedCommandBox.Text = server.ImportedCommand;
        IgnoreImportedCommandBox.IsChecked = server.IgnoreImportedCommand;
        TryDirectWithoutJumphostBox.IsChecked = server.TryDirectWithoutJumphost;
        requiredRouteOptions = new[]
            {
                new RequiredRouteOption(null, "Автоматически (без ограничения)")
            }
            .Concat(config.AllServers()
                .Where(candidate => candidate.Id != server.Id)
                .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(candidate => new RequiredRouteOption(
                    candidate.Id,
                    $"{candidate.Name} — {GroupNameFor(candidate)}",
                    $"{candidate.Name} {candidate.Host} {candidate.Port} {GroupNameFor(candidate)}")))
            .ToArray();
        selectedRequiredRouteOption = requiredRouteOptions.First(option =>
            option.Id == server.RequiredPreviousServerId);
        ResetRequiredRoutePicker();
        StartJumphostButton.Visibility = config.BaseProxies.Any(proxy => proxy.Enabled && proxy.StartupServerId == server.Id)
            ? Visibility.Visible : Visibility.Collapsed;
        SaveToKittyButton.Visibility = string.IsNullOrWhiteSpace(server.SourceSessionPath)
            ? Visibility.Visible : Visibility.Collapsed;
        ScriptInfoText.Text = server.SourceScriptPath is not null
            ? server.SourceScriptPath
            : server.SourceScriptContent.Length > 0 ? "Встроенный login script из сессии KiTTY" : "Не указан";
        WebGrid.ItemsSource = null; WebGrid.ItemsSource = server.WebInterfaces;
        BackupGrid.ItemsSource = null; BackupGrid.ItemsSource = server.BackupEndpoints;
        if (lastRouteDisplays.TryGetValue(server.Id, out var routeDisplay))
        {
            LastRouteText.Text = routeDisplay;
            LastRouteText.Visibility = Visibility.Visible;
        }
        else LastRouteText.Visibility = Visibility.Collapsed;
    }

    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { selectedGroup = (e.NewValue as TreeViewItem)?.Tag as ServerGroup; RefreshSessions(); }
    private void ClearGroupFilter_Click(object sender, RoutedEventArgs e) { selectedGroup = null; ClearTreeSelection(GroupTree.Items); RefreshSessions(); }
    private static void ClearTreeSelection(ItemCollection items) { foreach (var value in items) if (value is TreeViewItem item) { item.IsSelected = false; ClearTreeSelection(item.Items); } }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (SessionsGrid is not null) RefreshSessions(); }
    private void GroupSearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (GroupTree is not null) RefreshGroups(); }

    private void ResetRequiredRoutePicker()
    {
        updatingRequiredRoutePicker = true;
        RequiredRouteSearchBox.Text = selectedRequiredRouteOption?.Name ?? "";
        RequiredRouteOptionsList.ItemsSource = requiredRouteOptions;
        RequiredRouteOptionsList.SelectedItem = selectedRequiredRouteOption;
        updatingRequiredRoutePicker = false;
    }

    private void RequiredRouteSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RequiredRouteOptionsList.ItemsSource = requiredRouteOptions;
        RequiredRouteOptionsList.SelectedItem = selectedRequiredRouteOption;
        RequiredRoutePopup.IsOpen = true;
        RequiredRouteSearchBox.SelectAll();
    }

    private void RequiredRouteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingRequiredRoutePicker || RequiredRouteOptionsList is null) return;
        var query = RequiredRouteSearchBox.Text.Trim();
        var filtered = requiredRouteOptions
            .Where(option => query.Length == 0 ||
                option.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                option.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        RequiredRouteOptionsList.ItemsSource = filtered;
        RequiredRouteOptionsList.SelectedIndex = filtered.Length > 0 ? 0 : -1;
        if (RequiredRouteSearchBox.IsKeyboardFocusWithin) RequiredRoutePopup.IsOpen = true;
    }

    private void RequiredRouteSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RequiredRouteOptionsList.SelectedItem is RequiredRouteOption option)
        {
            SelectRequiredRouteOption(option);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RequiredRoutePopup.IsOpen = false;
            ResetRequiredRoutePicker();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && RequiredRouteOptionsList.Items.Count > 0)
        {
            RequiredRouteOptionsList.SelectedIndex = Math.Min(
                RequiredRouteOptionsList.SelectedIndex + 1,
                RequiredRouteOptionsList.Items.Count - 1);
            RequiredRouteOptionsList.ScrollIntoView(RequiredRouteOptionsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && RequiredRouteOptionsList.Items.Count > 0)
        {
            RequiredRouteOptionsList.SelectedIndex = Math.Max(
                RequiredRouteOptionsList.SelectedIndex - 1, 0);
            RequiredRouteOptionsList.ScrollIntoView(RequiredRouteOptionsList.SelectedItem);
            e.Handled = true;
        }
    }

    private void RequiredRouteDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (RequiredRoutePopup.IsOpen)
        {
            RequiredRoutePopup.IsOpen = false;
            return;
        }
        ResetRequiredRoutePicker();
        RequiredRoutePopup.IsOpen = true;
        RequiredRouteSearchBox.Focus();
        RequiredRouteSearchBox.SelectAll();
    }

    private void RequiredRouteOptionsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RequiredRouteOptionsList.SelectedItem is RequiredRouteOption option)
            SelectRequiredRouteOption(option);
    }

    private void SelectRequiredRouteOption(RequiredRouteOption option)
    {
        selectedRequiredRouteOption = option;
        RequiredRoutePopup.IsOpen = false;
        ResetRequiredRoutePicker();
        Keyboard.ClearFocus();
    }

    private void RequiredRoutePopup_Closed(object sender, EventArgs e)
    {
        if (!updatingRequiredRoutePicker) ResetRequiredRoutePicker();
    }

    private async void SessionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on column headers (sorting)
        if (FindParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null) return;
        var server = SelectedRow()?.Server;
        if (server is null) return;
        if (config.BaseProxies.Any(proxy => proxy.Enabled && proxy.StartupServerId == server.Id))
        {
            await StartManagedJumphostAsync(server);
            return;
        }
        await OpenRoutedConsoleAsync(server);
    }
    private void SessionsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragStartedOnRow = FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is not null;
        if (dragStartedOnRow) SelectGridRowFromEvent(e);
    }
    private void SessionsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragStartedOnRow || e.LeftButton != MouseButtonState.Pressed || SelectedRow() is not SessionRow row) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(SessionsGrid, new DataObject(typeof(ManagedServer), row.Server), DragDropEffects.Move);
        dragStartedOnRow = false;
    }
    private void SelectGridRowFromEvent(MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject); if (row is not null) SessionsGrid.SelectedItem = row.Item;
        if (SelectedRow() is SessionRow selected) ShowServer(selected.Server);
    }

    private void GroupTree_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(typeof(ManagedServer)) && FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)?.Tag is ServerGroup ? DragDropEffects.Move : DragDropEffects.None;
    private void GroupTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ManagedServer)) is not ManagedServer server || FindParent<TreeViewItem>(e.OriginalSource as DependencyObject)?.Tag is not ServerGroup group) return;
        config.MoveServerToGroup(server.Id, group.Id); SaveAndRefresh(); Status($"{server.Name} перемещена в «{group.Name}»");
    }
    private void GroupTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null) { item.IsSelected = true; item.Focus(); selectedGroup = item.Tag as ServerGroup; }
        else { GroupTree.SelectedItemChanged -= GroupTree_SelectedItemChanged; selectedGroup = null; GroupTree.SelectedItemChanged += GroupTree_SelectedItemChanged; }
    }

    private void CreateRootGroup_Click(object sender, RoutedEventArgs e) { var name = Prompt("Новая группа", "Название группы"); if (name is null) return; config.Groups.Add(new() { Name = name }); SaveAndRefresh(); }
    private void CreateSubgroup_Click(object sender, RoutedEventArgs e) { if (selectedGroup is null) { Warn("Щёлкните правой кнопкой по родительской группе."); return; } var name = Prompt("Новая подгруппа", "Название подгруппы"); if (name is null) return; selectedGroup.Groups.Add(new() { Name = name }); SaveAndRefresh(); }
    private async void BuildGroupLinks_Click(object sender, RoutedEventArgs e)
    {
        if (selectedGroup is null) { Warn("Щёлкните правой кнопкой по нужной группе."); return; }
        var groupServers = AllNestedServers(selectedGroup).DistinctBy(server => server.Id).ToArray();
        await BuildLinksForServersAsync($"Группа «{selectedGroup.Name}»", groupServers, false);
    }

    private async void BuildMissingGroupLinks_Click(object sender, RoutedEventArgs e)
    {
        if (selectedGroup is null) { Warn("Щёлкните правой кнопкой по нужной группе."); return; }
        var groupServers = AllNestedServers(selectedGroup).DistinctBy(server => server.Id).ToArray();
        await BuildLinksForServersAsync(
            $"Недостающие связи группы «{selectedGroup.Name}»",
            groupServers,
            true);
    }

    private async Task BuildLinksForServersAsync(
        string scopeName,
        IReadOnlyList<ManagedServer> selectedServers,
        bool skipExistingLinks)
    {
        if (config.BaseProxies.All(proxy => !proxy.Enabled))
        {
            Warn("Нет включённых точек входа. Назначьте сессию jumphost либо добавьте уже запущенный внешний SOCKS5.");
            return;
        }
        var groupServers = selectedServers.DistinctBy(server => server.Id).ToArray();
        if (groupServers.Length < 2) { Warn("Для проверки связей выберите хотя бы две сессии."); return; }
        var allPairs = ConnectivityBatchPlanner.Pairs(groupServers);
        var pairs = (skipExistingLinks
            ? ConnectivityBatchPlanner.NewPairs(config, groupServers)
            : allPairs).ToList();
        var remoteAnchor = ConnectivityBatchPlanner.RemoteAnchor(config, groupServers);
        var skippedCount = allPairs.Count - pairs.Count;
        var attempts = pairs.Count;
        var serverNames = string.Join("\n", pairs.Take(20).Select(pair => $"• {pair.A.Name} ↔ {pair.B.Name}"));
        if (pairs.Count > 20) serverNames += $"\n…и ещё {pairs.Count - 20}";
        if (skippedCount > 0) serverNames += $"\n\nПропущено уже сохранённых пар: {skippedCount}";
        if (pairs.Count == 0)
        {
            Status(skipExistingLinks
                ? "Между выбранными сессиями уже сохранены все возможные связи."
                : "Нет пар для проверки.");
            return;
        }
        if (ThemedMessageDialog.Show(this,
                $"{scopeName}: {groupServers.Length} серверов.\nБудет проверено пар: {attempts}. При успехе связь сохраняется сразу в обоих направлениях; обратная проверка выполняется только после неудачи первой.\n\n{serverNames}\n\nНесколько неверных паролей могут вызвать блокировку. Начать?",
                "Построение карты связей", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (operationCancellation is not null) { Status("Дождитесь завершения текущей операции или отмените её."); return; }
        using var cts = new CancellationTokenSource();
        operationCancellation = cts;
        var progressWindow = new LinkBuildingProgress
        {
            Owner = this,
            CancelRequested = () =>
            {
                Status("Отменяю операцию…");
                cts.Cancel();
            }
        };
        progressWindow.Show();
        HeaderPanel.IsEnabled = false;
        WorkspacePanel.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;

        // await (а не fire-and-forget), иначе `using var cts` уничтожится сразу
        // после возврата обработчика и фоновая задача получит disposed CTS.
        await Task.Run(async () =>
        {
            try
            {
                var startup = await Dispatcher.InvokeAsync(() =>
                    EnsureManagedJumphostAsync(cts.Token, false, startAllAutoStart: true));
                await startup;
                var results = new System.Collections.Concurrent.ConcurrentBag<ConnectivityResult>();
                var completed = 0;
                var total = pairs.Count;
                // Если до одного сервера группы уже известен рабочий внешний
                // маршрут, открываем эту длинную цепочку один раз и из неё
                // проверяем сразу всех соседей группы.
                if (remoteAnchor is not null)
                {
                    var anchorTargets = pairs
                        .Where(pair => pair.A.Id == remoteAnchor.Id || pair.B.Id == remoteAnchor.Id)
                        .Select(pair => pair.A.Id == remoteAnchor.Id ? pair.B : pair.A)
                        .DistinctBy(server => server.Id)
                        .ToArray();
                    if (anchorTargets.Length > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                            progressWindow.UpdateStatus(
                                $"Открываю опорную сессию «{remoteAnchor.Name}» для {anchorTargets.Length} связей…",
                                completed, total));
                        try
                        {
                            var anchorPairs = anchorTargets
                                .Select(target => (A: remoteAnchor, B: target)).ToArray();
                            var anchorResults = await ConnectivityBatchExecutor.CheckPairsAsync(
                                anchorPairs, CheckConnectivityBatchFromAsync, cts.Token);
                            foreach (var result in anchorResults) results.Add(result);
                            RouteLog($"Group anchor: source={remoteAnchor.Name}; targets=" +
                                     string.Join(", ", anchorTargets.Select(server => server.Name)));
                            foreach (var pair in anchorPairs.Where(pair => !anchorResults.Any(result =>
                                         result.Success &&
                                         ((result.SourceId == pair.A.Id && result.TargetId == pair.B.Id) ||
                                          (result.SourceId == pair.B.Id && result.TargetId == pair.A.Id)))))
                                ServerLinkPairPolicy.Invalidate(config, pair.A.Id, pair.B.Id);
                            SaveLinkResults(anchorResults);
                        }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            RouteLog($"Group anchor failed: source={remoteAnchor.Name}; " +
                                     $"error={ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                var remainingPairs = ConnectivityBatchPlanner.UnsuccessfulPairs(pairs, results);
                completed = pairs.Count - remainingPairs.Count;
                // Опорные пары должны успеть сохранить найденные маршруты до
                // проверки зависимых. Уже найденные опорной сессией не повторяем.
                var stages = ConnectivityBatchPlanner.DependencyStages(
                    config, groupServers, remainingPairs);
                foreach (var stage in stages)
                {
                    var stageResults = await ConnectivityBatchExecutor.CheckPairsAsync(
                        stage, CheckConnectivityBatchFromAsync, cts.Token);
                    foreach (var result in stageResults) results.Add(result);
                    foreach (var pair in stage.Where(pair => !stageResults.Any(result =>
                                 result.Success &&
                                 ((result.SourceId == pair.A.Id && result.TargetId == pair.B.Id) ||
                                  (result.SourceId == pair.B.Id && result.TargetId == pair.A.Id)))))
                        ServerLinkPairPolicy.Invalidate(config, pair.A.Id, pair.B.Id);
                    SaveLinkResults(stageResults);
                    completed += stage.Count;
                    await Dispatcher.InvokeAsync(() => progressWindow.UpdateStatus(
                        $"Проверено {completed}/{total}: пакет из {stage.Count} связей",
                        completed, total));
                }

                // Если в начале ни один сервер ещё не был известен как прямой,
                // пары могли стартовать одновременно. Успех соседних пар уже
                // открыл новые маршруты; один раз допроверяем только оставшиеся
                // неуспешные пары, не повторяя найденные связи.
                var unresolved = ConnectivityBatchPlanner.UnsuccessfulPairs(pairs, results);
                if (unresolved.Count > 0 && unresolved.Count < pairs.Count)
                {
                    await Dispatcher.InvokeAsync(() =>
                        progressWindow.UpdateStatus(
                            $"Допроверяю {unresolved.Count} пар через только что найденные связи…",
                            completed, total));
                    foreach (var pair in unresolved)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var forwardTested = results.Any(result =>
                            result.SourceId == pair.A.Id && result.TargetId == pair.B.Id &&
                            result.Strategy != "SOURCE_UNREACHABLE");
                        var reverseTested = results.Any(result =>
                            result.SourceId == pair.B.Id && result.TargetId == pair.A.Id &&
                            result.Strategy != "SOURCE_UNREACHABLE");
                        if (forwardTested && reverseTested) continue;
                        var retrySource = !forwardTested ? pair.A : pair.B;
                        var retryTarget = !forwardTested ? pair.B : pair.A;
                        try
                        {
                            RouteLog($"Pair retry after new route: reach source={retrySource.Name}; direction={retrySource.Name}->{retryTarget.Name}");
                            var retry = await ssh.CheckFromAsync(config, retrySource.Id,
                                [retryTarget.Id], cts.Token);
                            foreach (var result in retry) results.Add(result);
                            SaveLinkResults(retry);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            results.Add(new ConnectivityResult(retryTarget.Id, false,
                                ex.Message, TimeSpan.Zero, "SOURCE_UNREACHABLE", SourceId: retrySource.Id));
                        }
                    }
                }

                var resultList = results.ToList();

                var pairResultsList = new List<(ManagedServer A, ManagedServer B, bool Found, string? Failure)>();
                foreach (var (serverA, serverB) in pairs)
                {
                    var forward = resultList.FirstOrDefault(r => r.SourceId == serverA.Id && r.TargetId == serverB.Id);
                    var reverse = resultList.FirstOrDefault(r => r.SourceId == serverB.Id && r.TargetId == serverA.Id);
                    // Успех считаем по любому прохождению направления (пару могли
                    // проверить несколько раз из-за повторных проходов).
                    var found = resultList.Any(r => r.SourceId == serverA.Id && r.TargetId == serverB.Id && r.Success) ||
                                resultList.Any(r => r.SourceId == serverB.Id && r.TargetId == serverA.Id && r.Success);
                    string? failure = null;
                    if (!found)
                    {
                        var forwardMsg = forward is not null ? $"{serverA.Name} → {serverB.Name}: {forward.Message}" : null;
                        var reverseMsg = reverse is not null ? $"{serverB.Name} → {serverA.Name}: {reverse.Message}" : null;
                        failure = string.Join(Environment.NewLine, new[] { forwardMsg, reverseMsg }.Where(m => m is not null));
                    }
                    pairResultsList.Add((serverA, serverB, found, failure));
                }

                var successful = pairResultsList.Count(r => r.Found);
                var failedPairs = pairResultsList.Where(r => !r.Found).Take(12).Select(r => $"✗ {r.A.Name} ↔ {r.B.Name}:{Environment.NewLine}{r.Failure}");
                var failureText = string.Join(Environment.NewLine, failedPairs);
                if (pairResultsList.Count - successful > 12) failureText += $"\n…и ещё {pairResultsList.Count - successful - 12}";

                await Dispatcher.InvokeAsync(() =>
                {
                    EndProgressOperation(cts, progressWindow);
                    ThemedMessageDialog.Show(this,
                        $"Пар проверено: {pairResultsList.Count}\nСвязи найдены: {successful}\nНедоступно: {pairResultsList.Count - successful}" +
                        (failureText.Length == 0 ? "" : $"\n\n{RedactSecrets(failureText)}"),
                        "Карта SSH-связей", MessageBoxButton.OK,
                        successful == pairResultsList.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
                });
            }
            catch (OperationCanceledException) { /* user cancelled */ }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    EndProgressOperation(cts, progressWindow);
                    Error(ex);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                    EndProgressOperation(cts, progressWindow));
            }
        });
    }

    private void DeleteGroupLinks_Click(object sender, RoutedEventArgs e)
    {
        if (selectedGroup is null) { Warn("Щёлкните правой кнопкой по нужной группе."); return; }
        var groupServerIds = AllNestedServers(selectedGroup).Select(s => s.Id).ToHashSet();
        var linksToRemove = config.Links
            .Where(link => groupServerIds.Contains(link.FromServerId) && groupServerIds.Contains(link.ToServerId))
            .ToList();
        if (linksToRemove.Count == 0) { Warn("В этой группе нет связей между её серверами."); return; }
        if (ThemedMessageDialog.Show(this,
                $"Удалить {linksToRemove.Count} связей между серверами группы «{selectedGroup.Name}»?\n\n" +
                "Связи с серверами вне группы останутся нетронутыми.",
                "Удаление связей", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var link in linksToRemove) config.Links.Remove(link);
        SaveConfig();
        Status($"Удалено связей: {linksToRemove.Count}");
    }
    private void RenameGroup_Click(object sender, RoutedEventArgs e) { if (selectedGroup is null) return; var name = Prompt("Переименовать", "Новое название", selectedGroup.Name); if (name is null) return; selectedGroup.Name = name; SaveAndRefresh(); }
    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (selectedGroup is null || ThemedMessageDialog.Show(this, "Удалить группу и подгруппы? Все их сессии останутся в разделе «Без группы».", "Удаление группы", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var servers = AllNestedServers(selectedGroup).ToList();
        config.Groups.RemoveAll(g => g.Id == selectedGroup.Id); foreach (var group in config.AllGroups()) group.Groups.RemoveAll(g => g.Id == selectedGroup.Id);
        foreach (var server in servers) if (config.AllServers().All(s => s.Id != server.Id)) config.UngroupedServers.Add(server);
        selectedGroup = null; SaveAndRefresh();
    }
    private static IEnumerable<ManagedServer> AllNestedServers(ServerGroup group) => group.Servers.Concat(group.Groups.SelectMany(AllNestedServers));

    private void CreateSession_Click(object sender, RoutedEventArgs e) { var server = new ManagedServer(); config.UngroupedServers.Add(server); SaveAndRefresh(); ShowServer(server); }
    private void DuplicateSession_Click(object sender, RoutedEventArgs e)
    {
        var source = selectedServer ?? SelectedRow()?.Server;
        if (source is null) { Warn("Сначала выберите сессию."); return; }
        var copy = ManagedServerDuplicator.Duplicate(config, source);
        ShowServer(copy);
        SaveAndRefresh();
        Status($"Создана сессия «{copy.Name}»");
    }
    private void MoveToUngrouped_Click(object sender, RoutedEventArgs e) { if (SelectedRow() is SessionRow row) { config.MoveServerToGroup(row.Server.Id, null); SaveAndRefresh(); } }
    private void SessionsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = SelectedRow();
        var inGroup = row is not null && config.FindServerGroup(row.Server.Id) is not null;
        MoveToUngroupedItem.Visibility = inGroup ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DeleteSession_Click(object sender, RoutedEventArgs e) { if (SelectedRow() is not SessionRow row || ThemedMessageDialog.Show(this, $"Удалить «{row.Name}» только из менеджера? Исходная сессия KiTTY останется.", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; config.RemoveServer(row.Server.Id); if (selectedServer?.Id == row.Server.Id) selectedServer = null; SaveAndRefresh(); }

    private void ApplyServer_Click(object sender, RoutedEventArgs e)
    {
        if (selectedServer is null) return;
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { Warn("SSH-порт должен быть от 1 до 65535."); return; }
        if (string.IsNullOrWhiteSpace(HostBox.Text)) { Warn("Укажите адрес сервера."); return; }
        var before = ImportedSessionMerger.Snapshot(selectedServer);
        var beforeKeyPassphrase = selectedServer.PrivateKeyPassphrase;
        var beforeShellPrompt = selectedServer.ShellPrompt;
        var beforeIgnoreCommand = selectedServer.IgnoreImportedCommand;
        var beforeRequiredPreviousServerId = selectedServer.RequiredPreviousServerId;
        var beforeTryDirectWithoutJumphost = selectedServer.TryDirectWithoutJumphost;
        var beforeOverrides = selectedServer.ManagerOverrides.ToList();
        SetManagerOverride(selectedServer, nameof(ManagedServer.Name), selectedServer.Name, ServerNameBox.Text.Trim(), value => selectedServer.Name = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.Host), selectedServer.Host, HostBox.Text.Trim(), value => selectedServer.Host = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.Port), selectedServer.Port, port, value => selectedServer.Port = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.Username), selectedServer.Username, UsernameBox.Text.Trim(), value => selectedServer.Username = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.Password), selectedServer.Password, PasswordBox.Value, value => selectedServer.Password = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.PrivateKeyPath), selectedServer.PrivateKeyPath, PrivateKeyPathBox.Text.Trim(), value => selectedServer.PrivateKeyPath = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.PrivateKeyPassphrase), selectedServer.PrivateKeyPassphrase, PrivateKeyPassphraseBox.Value, value => selectedServer.PrivateKeyPassphrase = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.RootLogin), selectedServer.RootLogin, RootLoginBox.Text.Trim(), value => selectedServer.RootLogin = value);
        SetManagerOverride(selectedServer, nameof(ManagedServer.RootPassword), selectedServer.RootPassword, RootPasswordBox.Value, value => selectedServer.RootPassword = value);
        selectedServer.ShellPrompt = string.IsNullOrWhiteSpace(ShellPromptBox.Text) ? "$" : ShellPromptBox.Text;
        SetManagerOverride(selectedServer, nameof(ManagedServer.ImportedCommand), selectedServer.ImportedCommand,
            ImportedCommandBox.Text, value => selectedServer.ImportedCommand = value);
        selectedServer.IgnoreImportedCommand = IgnoreImportedCommandBox.IsChecked == true;
        selectedServer.TryDirectWithoutJumphost = TryDirectWithoutJumphostBox.IsChecked == true;
        var requiredPreviousServerId = selectedRequiredRouteOption?.Id;
        try
        {
            if (config.WriteChangesImmediatelyToKitty && selectedServer.SourceSessionPath is not null)
            {
                var properties = selectedServer.ManagerOverrides.Where(KittySessionWriter.WritableProperties.Contains).ToHashSet();
                ApplyKittyProperties(selectedServer, properties);
            }
        }
        catch (Exception ex)
        {
            ImportedSessionMerger.Restore(selectedServer, before);
            selectedServer.PrivateKeyPassphrase = beforeKeyPassphrase;
            selectedServer.ShellPrompt = beforeShellPrompt;
            selectedServer.IgnoreImportedCommand = beforeIgnoreCommand;
            selectedServer.RequiredPreviousServerId = beforeRequiredPreviousServerId;
            selectedServer.TryDirectWithoutJumphost = beforeTryDirectWithoutJumphost;
            selectedServer.ManagerOverrides = beforeOverrides;
            ShowServer(selectedServer);
            Error(ex);
            return;
        }
        selectedServer.RequiredPreviousServerId = requiredPreviousServerId;
        if (selectedServer.RequiredPreviousServerId != beforeRequiredPreviousServerId ||
            selectedServer.TryDirectWithoutJumphost != beforeTryDirectWithoutJumphost)
        {
            backgroundRouteProbes.Cancel(selectedServer.Id);
            selectedServer.PreferredRoute = null;
            lastRouteDisplays.Remove(selectedServer.Id);
        }
        ssh.ClearFailureCache();
        SaveAndRefresh();
    }

    private static void SetManagerOverride<T>(
        ManagedServer server, string property, T current, T value, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        var baseline = server.KittyBaseline is null ? null : ImportedSessionMerger.BaselineValue(server, property);
        if (baseline is not null && string.Equals(Convert.ToString(value), baseline, StringComparison.Ordinal))
            server.ManagerOverrides.RemoveAll(item => item == property);
        else if (!server.ManagerOverrides.Contains(property, StringComparer.Ordinal)) server.ManagerOverrides.Add(property);
        assign(value);
    }

    private void AddWeb_Click(object sender, RoutedEventArgs e) { if (selectedServer is null) { Warn("Сначала выберите сессию."); return; } selectedServer.WebInterfaces.Add(new()); WebGrid.ItemsSource = null; WebGrid.ItemsSource = selectedServer.WebInterfaces; }
    private void AddBackupEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (selectedServer is null) { Warn("Сначала выберите сессию."); return; }
        selectedServer.BackupEndpoints.Add(new ServerEndpoint());
        BackupGrid.ItemsSource = null; BackupGrid.ItemsSource = selectedServer.BackupEndpoints;
    }
    private void RemoveBackupEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (selectedServer is null || BackupGrid.SelectedItem is not ServerEndpoint endpoint) return;
        selectedServer.BackupEndpoints.Remove(endpoint);
        if (endpoint.Equals(selectedServer.PreferredEndpoint)) selectedServer.PreferredEndpoint = null;
        BackupGrid.ItemsSource = null; BackupGrid.ItemsSource = selectedServer.BackupEndpoints;
        SaveConfig();
    }
    private void BackupGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Сохраняем после завершения правки ячейки (отложенно, чтобы привязка
        // успела записать значение в модель). Невалидные строки (порт <= 0)
        // отбрасываются при загрузке, как и прочие поля.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (selectedServer is not null) SaveConfig();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private void MoveWebUp_Click(object sender, RoutedEventArgs e) => MoveWeb(-1);
    private void MoveWebDown_Click(object sender, RoutedEventArgs e) => MoveWeb(1);
    private void MoveWeb(int direction)
    {
        if (selectedServer is null || WebGrid.SelectedItem is not WebInterface web) return;
        var index = selectedServer.WebInterfaces.IndexOf(web);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= selectedServer.WebInterfaces.Count) return;
        selectedServer.WebInterfaces.RemoveAt(index);
        selectedServer.WebInterfaces.Insert(target, web);
        WebGrid.ItemsSource = null; WebGrid.ItemsSource = selectedServer.WebInterfaces;
        WebGrid.SelectedItem = web;
        SaveConfig();
    }
    private void WebGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (selectedServer is null) return;
        e.Handled = true;
        var direction = e.Column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending : ListSortDirection.Descending;
        e.Column.SortDirection = direction;
        var property = e.Column.SortMemberPath;
        var sorted = direction == ListSortDirection.Ascending
            ? selectedServer.WebInterfaces.OrderBy(item => GetWebSortValue(item, property), StringComparer.CurrentCultureIgnoreCase).ToList()
            : selectedServer.WebInterfaces.OrderByDescending(item => GetWebSortValue(item, property), StringComparer.CurrentCultureIgnoreCase).ToList();
        selectedServer.WebInterfaces.Clear();
        selectedServer.WebInterfaces.AddRange(sorted);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(WebGrid.ItemsSource);
        view?.Refresh();
        SaveConfig();
    }
    private static string GetWebSortValue(WebInterface web, string property) => property switch
    {
        nameof(WebInterface.Name) => web.Name,
        nameof(WebInterface.Url) => web.Url,
        nameof(WebInterface.ResolverAddress) => web.ResolverAddress,
        nameof(WebInterface.Username) => web.Username,
        nameof(WebInterface.Password) => web.Password,
        _ => web.Name
    };
    private void SaveToKitty_Click(object sender, RoutedEventArgs e)
    {
        if (selectedServer is null) { Warn("Сначала выберите сессию."); return; }
        try
        {
            ApplyKittyProperties(selectedServer, KittySessionWriter.WritableProperties);
            SaveAndRefresh();
            Status($"Сессия «{selectedServer.Name}» сохранена в KiTTY");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void RemoveWeb_Click(object sender, RoutedEventArgs e) { if (selectedServer is not null && WebGrid.SelectedItem is WebInterface web) { selectedServer.WebInterfaces.Remove(web); SaveAndRefresh(); } }

    private async void WebGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!WebGrid.IsReadOnly) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not WebInterface web) return;
        WebGrid.SelectedItem = web;
        e.Handled = true;
        await OpenSelectedWebAsync();
    }

    private void WebGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null) return;
        WebGrid.SelectedItem = cell.DataContext;
        WebGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
        cell.Focus();
    }

    private void EditWebField_Click(object sender, RoutedEventArgs e)
    {
        if (!WebGrid.CurrentCell.IsValid || WebGrid.CurrentCell.Item is not WebInterface) return;
        WebGrid.IsReadOnly = false;
        WebGrid.BeginEdit();
    }

    private void WebGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            WebGrid.IsReadOnly = true;
            SaveConfig();
        });
    }

    private void OpenOriginalSession_Click(object sender, RoutedEventArgs e) => OpenOriginalSession();
    private void OpenOriginalSession(ManagedServer? requestedServer = null)
    {
        var server = requestedServer ?? SelectedRow()?.Server ?? selectedServer; if (server is null) return;
        if (string.IsNullOrWhiteSpace(server.SourceSessionPath) || !File.Exists(server.SourceSessionPath)) { Warn("Это не импортированная сессия KiTTY. Используйте «Подключиться» либо укажите сервер вручную."); return; }
        try
        {
            var kitty = FindKittyNearSession(server.SourceSessionPath) ?? ResolveProgram(config.KittyPath);
            KittyLoginScript? loginScript = null;
            try
            {
                loginScript = KittyLoginScript.Create(server);
                var startInfo = new ProcessStartInfo(kitty) { WorkingDirectory = Path.GetDirectoryName(kitty)! };
                foreach (var argument in KittyLaunchPlan.OriginalSessionArguments(server, loginScript?.Path))
                    startInfo.ArgumentList.Add(argument);
                Process.Start(startInfo);
                if (loginScript is not null)
                {
                    PreserveTemporaryFile(loginScript.Path);
                    loginScript = null;
                }
            }
            finally { loginScript?.Dispose(); }
            Log($"Открыта исходная сессия KiTTY: {server.Name}");
        }
        catch (Exception ex) { Error(ex); }
    }

    private static string? FindKittyNearSession(string sessionPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sessionPath)!);
        for (var i = 0; i < 4 && directory is not null; i++, directory = directory.Parent)
        {
            foreach (var name in new[] { "kitty.exe", "kitty_portable.exe" }) { var path = Path.Combine(directory.FullName, name); if (File.Exists(path)) return path; }
        }
        return null;
    }

    private async void OpenRoutedConsole_Click(object sender, RoutedEventArgs e)
    {
        var server = SelectedRow()?.Server ?? selectedServer; if (server is null) return;
        if (config.BaseProxies.Any(proxy => proxy.Enabled && proxy.StartupServerId == server.Id))
        {
            await StartManagedJumphostAsync(server);
            return;
        }
        await OpenRoutedConsoleAsync(server);
    }

    private bool BlockIfOperationRunning()
    {
        if (operationCancellation is null) return false;
        ThemedMessageDialog.Show(this,
            "Сейчас идёт проверка связей — подключение недоступно.\nЕсли нужно подключиться, отмените проверку кнопкой «Отмена».",
            "Идёт проверка связей", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private async Task OpenRoutedConsoleAsync(
        ManagedServer server, Guid? forcedViaServerId = null,
        CancellationToken externalCancellationToken = default)
    {
        if (BlockIfOperationRunning()) return;
        var keyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ");
        var key = PrivateKeyInspector.Inspect(keyPath ?? "");
        if (key.Encrypted == true && server.PrivateKeyPassphrase.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(server.SourceSessionPath) && File.Exists(server.SourceSessionPath) &&
                ThemedMessageDialog.Show(this,
                    "Для автоматической SSH-аутентификации нужен пароль зашифрованного ключа. Он не хранится в исходной сессии KiTTY.\n\nОткрыть сохранённую сессию напрямую и ввести пароль ключа в штатном окне KiTTY?",
                    "Зашифрованный SSH-ключ", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                OpenOriginalSession(server);
                return;
            }
            Warn("Введите пароль ключа в поле «Пароль ключа» и сохраните изменения либо откройте исходную сессию KiTTY напрямую.");
            return;
        }
        ssh.ClearFailureCache();
        await BusyAsync("Строю SSH-маршрут…", async cancellationToken =>
        {
            ActiveRoute route;
            try
            {
                route = await Connect(server, cancellationToken, true, forcedViaServerId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("Не найден рабочий маршрут.", StringComparison.Ordinal) &&
                forcedViaServerId is null &&
                !string.IsNullOrWhiteSpace(server.SourceSessionPath) &&
                File.Exists(server.SourceSessionPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RouteLog($"Managed routes exhausted: target={server.Name}; fallback=original-session; error={ex.Message}");
                Status($"Управляемые маршруты недоступны. Открываю исходную сессию KiTTY «{server.Name}»…");
                server.LastOriginalSessionFallbackUtc = DateTimeOffset.UtcNow;
                SaveConfig();
                OpenOriginalSession(server);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var loadSavedSession = !string.IsNullOrWhiteSpace(server.SourceSessionPath) &&
                                   File.Exists(server.SourceSessionPath);
            var kitty = loadSavedSession
                ? FindKittyNearSession(server.SourceSessionPath!) ?? ResolveProgram(config.KittyPath)
                : ResolveProgram(config.KittyPath);
            KittyRoutedSession? routedSession = null;
            KittyLoginScript? loginScript = null;
            try
            {
                var directEndpoint = route.CanDetachDirectConsole
                    ? route.Hops.Single()
                    : null;
                if (loadSavedSession)
                    routedSession = directEndpoint is null
                        ? KittyRoutedSession.Create(
                            server.SourceSessionPath!, route.LocalSshPort, server.ImportedCommand,
                            server.IgnoreImportedCommand)
                        : KittyRoutedSession.CreateDirect(
                            server.SourceSessionPath!, directEndpoint.Host, directEndpoint.Port,
                            server.ImportedCommand, server.IgnoreImportedCommand);
                loginScript = KittyLoginScript.Create(server);
                var startInfo = new ProcessStartInfo(kitty)
                {
                    WorkingDirectory = Path.GetDirectoryName(kitty)!
                };
                var arguments = directEndpoint is null
                    ? KittyLaunchPlan.RoutedConsoleArguments(
                        server, route.LocalSshPort, loadSavedSession, routedSession?.Path,
                        loginScript?.Path)
                    : KittyLaunchPlan.DirectConsoleArguments(
                        server, directEndpoint.Host, directEndpoint.Port, loadSavedSession,
                        routedSession?.Path, loginScript?.Path);
                foreach (var argument in arguments)
                    startInfo.ArgumentList.Add(argument);
                RouteLog($"KiTTY launch: target={server.Name}; local=127.0.0.1:{route.LocalSshPort}; " +
                         $"loghost={server.Host}:{server.Port}; hostkey=stable-cache; " +
                         $"saved-session={loadSavedSession}; login-script={(loginScript is not null ? "present" : "none")}");
                Process.Start(startInfo);
                if (directEndpoint is not null)
                {
                    ReleaseRoute(route);
                    RouteLog($"Direct KiTTY console detached from manager: target={server.Name}; " +
                             $"endpoint={directEndpoint.Host}:{directEndpoint.Port}");
                }
                if (routedSession is not null)
                {
                    var cleanup = routedSession;
                    routedSession = null;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        cleanup.Dispose();
                    });
                }
                if (loginScript is not null)
                {
                    PreserveTemporaryFile(loginScript.Path);
                    loginScript = null;
                }
            }
            finally
            {
                routedSession?.Dispose();
                loginScript?.Dispose();
            }
            Log(loadSavedSession
                ? $"Открыта сессия KiTTY «{server.Name}» через автоматический маршрут с исходными туннелями и login script"
                : $"Открыта сессия «{server.Name}» через автоматический маршрут");
        }, externalCancellationToken);
    }

    private static void PreserveTemporaryFile(string path)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try { File.Delete(path); } catch { }
        });
    }

    private async void OpenWeb_Click(object sender, RoutedEventArgs e)
        => await OpenSelectedWebAsync();

    private async Task OpenSelectedWebAsync()
    {
        if (BlockIfOperationRunning()) return;
        if (selectedServer is null || WebGrid.SelectedItem is not WebInterface web) { Warn("Выберите веб-интерфейс."); return; }
        if (!Uri.TryCreate(web.Url, UriKind.Absolute, out _)) { Warn("Некорректный URL."); return; }
        var server = selectedServer;
        ssh.ClearFailureCache();
        await BusyAsync("Строю маршрут к веб-интерфейсу…", async cancellationToken =>
        {
            ActiveRoute? route = null;
            Process? tunnel = null;
            KittyRoutedSession? routedSession = null;
            KittyLoginScript? loginScript = null;
            ResolvingHttpProxy? resolver = null;
            string? profile = null;
            var temporaryProfile = config.TemporaryFirefoxProfiles;
            try
            {
                var resolverMappings = config.UseInternalWebResolver
                    ? WebResolverMappingPlan.Build(server)
                    : [];
                route = await Connect(server, cancellationToken, true);
                var webPort = SelectFreeLoopbackPort();
                var loadSavedSession = !string.IsNullOrWhiteSpace(server.SourceSessionPath) &&
                                       File.Exists(server.SourceSessionPath);
                var kitty = loadSavedSession
                    ? FindKittyNearSession(server.SourceSessionPath!) ?? ResolveProgram(config.KittyPath)
                    : ResolveProgram(config.KittyPath);
                routedSession = loadSavedSession
                    ? KittyRoutedSession.Create(server.SourceSessionPath!, route.LocalSshPort,
                        server.ImportedCommand, true, webPort)
                    : KittyRoutedSession.CreateMinimal(route.LocalSshPort, webPort);
                // No login script for the tunnel: it connects to an already-
                // authenticated local SSH port. A login script waiting for a
                // shell prompt blocks the tunnel console until timeout.
                var tunnelStart = new ProcessStartInfo(kitty) { WorkingDirectory = Path.GetDirectoryName(kitty)! };
                foreach (var argument in KittyLaunchPlan.RoutedTunnelArguments(server, route.LocalSshPort,
                             true, routedSession.Path, null, skipPrivilegeCommand: true))
                    tunnelStart.ArgumentList.Add(argument);
                tunnel = Process.Start(tunnelStart) ?? throw new IOException("KiTTY не вернула процесс веб-туннеля.");
                RouteLog($"Web KiTTY tunnel starting: session={server.Name}; ingress=127.0.0.1:{route.LocalSshPort}; socks=127.0.0.1:{webPort}; exit=target");
                RouteLog($"Web tunnel args: {string.Join(" ", tunnelStart.ArgumentList)}");
                RouteLog($"Web tunnel routed-session: {routedSession.Path}");
                try
                {
                    var sessionContent = File.ReadAllText(routedSession.Path);
                    var relevantLines = sessionContent.Split('\n')
                        .Where(l => l.StartsWith("Nopty", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("TerminalType", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("Autocommand", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("Scriptfile", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("RemoteCommand", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("PortForwardings", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("HostName", StringComparison.OrdinalIgnoreCase) ||
                                    l.StartsWith("PortNumber", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.Trim());
                    RouteLog($"Web tunnel session key fields: {string.Join(" | ", relevantLines)}");
                }
                catch { }

                var deadline = DateTime.UtcNow.AddSeconds(config.ConnectionTimeoutSeconds);
                while (DateTime.UtcNow < deadline &&
                       !await IsSocks5ReadyAsync("127.0.0.1", webPort, TimeSpan.FromSeconds(1), cancellationToken))
                {
                    if (tunnel.HasExited) throw new IOException("KiTTY завершила веб-туннель до запуска SOCKS5.");
                    await Task.Delay(250, cancellationToken);
                }
                if (!await IsSocks5ReadyAsync("127.0.0.1", webPort, TimeSpan.FromSeconds(1), cancellationToken))
                    throw new TimeoutException("KiTTY не запустила динамический SOCKS5 за отведённое время.");
                routedSession?.Dispose(); routedSession = null;
                if (loginScript is not null)
                {
                    PreserveTemporaryFile(loginScript.Path);
                    loginScript = null;
                }

                var originalDestination = new Uri(web.Url);
                var destination = config.UseInternalWebResolver
                    ? originalDestination
                    : await ResolveWebDestinationAsync(originalDestination, cancellationToken);
                if (config.UseInternalWebResolver)
                    resolver = new ResolvingHttpProxy("127.0.0.1", webPort,
                        resolverMappings, message => RouteLog($"Web resolver: {message}"));
                var browserProxyPort = resolver?.Port ?? webPort;
                var probe = resolver is null
                    ? await ProbeThroughSocksAsync("127.0.0.1", browserProxyPort, destination,
                        TimeSpan.FromSeconds(8), cancellationToken)
                    : await ProbeThroughHttpProxyAsync("127.0.0.1", browserProxyPort, destination,
                        TimeSpan.FromSeconds(8), cancellationToken);
                RouteLog($"Web destination probe: session={server.Name}; engine=kitty; exit=target; socks=127.0.0.1:{browserProxyPort}; resolver={(resolver is null ? "system" : "internal")}; destination={destination.Host}:{destination.Port}; result={(probe.Success ? "PASS" : "FAIL")}; detail={probe.Detail}");
                if (!probe.Success)
                    throw new IOException("KiTTY-туннель конечной сессии не смог открыть указанный веб-адрес. Подробности записаны в журнал.");

                var firefox = ResolveProgram(config.FirefoxPath);
                if (config.UseInternalWebResolver)
                    FirefoxProfileWorkspace.RemoveLegacyAutoConfig(firefox);
                var templateProfile = config.FirefoxTemplateProfile.Length > 0 ? config.FirefoxTemplateProfile : null;
                while (true)
                {
                    try
                    {
                        profile = temporaryProfile
                            ? FirefoxProfileWorkspace.Create(FirefoxRuntimeRoot, server.Id, web.Id, templateProfile)
                            : FirefoxProfileWorkspace.Persistent(FirefoxPersistentRoot, FirefoxProfileKey(server));
                        break;
                    }
                    catch (FirefoxProfileLockedException lockEx)
                    {
                        var retry = await Dispatcher.InvokeAsync(() =>
                            ThemedMessageDialog.Show(this,
                                $"Firefox, из которого копируется шаблон профиля, сейчас запущен.\n\n" +
                                $"Путь профиля: {lockEx.TemplateProfilePath}\n\n" +
                                "Закройте этот Firefox и нажмите «Повторить».\n" +
                                "Или нажмите «Отмена», чтобы не открывать веб-интерфейс.",
                                "Шаблон профиля заблокирован",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK);
                        if (!retry) return;
                    }
                }
                FirefoxProfileWorkspace.ApplyPreferences(profile, browserProxyPort,
                    config.UseInternalWebResolver, resolverMappings.Select(mapping => mapping.Key));
                // Small delay ensures the profile files are fully committed to
                // disk before Firefox reads them (Windows file system caching).
                await Task.Delay(150, cancellationToken);
                RouteLog($"Web SOCKS destination ready: session={server.Name}; engine=kitty; port={browserProxyPort}; resolver={(resolver is null ? "system" : "internal")}; profile={profile}; url={web.Url}");
                var browserStart = new ProcessStartInfo(firefox)
                    { WorkingDirectory = Path.GetDirectoryName(firefox)! };
                foreach (var argument in FirefoxProfileWorkspace.LaunchArguments(profile, web.Url))
                    browserStart.ArgumentList.Add(argument);
                var browser = Process.Start(browserStart)
                    ?? throw new IOException("Firefox не вернул запущенный процесс.");
                browser.EnableRaisingEvents = true;
                RouteLog($"Web cleanup: registered for Firefox pid={browser.Id}, tunnel pid={tunnel.Id}");
                _ = CleanupWebSessionAfterExitAsync(browser, tunnel, route, resolver, profile, temporaryProfile);
                tunnel = null; route = null; resolver = null; profile = null;
            }
            finally
            {
                routedSession?.Dispose();
                loginScript?.Dispose();
                if (resolver is not null) await resolver.DisposeAsync();
                if (tunnel is not null) StopProcess(tunnel);
                if (route is not null) ReleaseRoute(route);
                if (profile is not null) TryDeleteDirectory(profile);
            }
        });
    }

    private async Task<ActiveRoute> Connect(
        ManagedServer server, CancellationToken cancellationToken, bool consoleOnly = false,
        Guid? forcedViaServerId = null)
    {
        // Пользовательское подключение всегда важнее устаревшей фоновой проверки.
        backgroundRouteProbes.Cancel(server.Id);
        if (!server.TryDirectWithoutJumphost && config.BaseProxies.All(p => !p.Enabled))
            throw new InvalidOperationException("Сначала настройте точку входа: назначьте сессию jumphost либо добавьте внешний SOCKS5.");
        if (forcedViaServerId is Guid requestedViaId)
            RouteLog($"Forced route request: target={server.Name}; via=" +
                     $"{config.FindServer(requestedViaId)?.Name ?? requestedViaId.ToString()}");
        var ranked = forcedViaServerId is Guid viaId
            ? RoutePlanner.ForcedFinalHopCandidates(config, viaId, server.Id)
            : RoutePlanner.Candidates(config, server.Id);
        if (ranked.Count == 0)
        {
            var required = server.RequiredPreviousServerId is Guid requiredId
                ? config.FindServer(requiredId)
                : null;
            throw new InvalidOperationException(forcedViaServerId is Guid forcedId
                ? $"Не найден маршрут через выбранный сервер «{config.FindServer(forcedId)?.Name ?? forcedId.ToString()}». Обычный маршрут не использован."
                : required is null
                ? "Не найден рабочий маршрут. Запустите ручную проверку связности."
                : $"Не найден маршрут через обязательную сессию «{required.Name}». " +
                  "Проверьте связь между ней и целевой сессией.");
        }
        // Forced candidates are already ranked and every one ends with the
        // selected hop. OrderPreferred may reconstruct an ordinary cached route,
        // so it must never be applied to an explicitly forced connection.
        var candidates = forcedViaServerId is null
            ? RoutePlanner.OrderPreferred(config, ranked, server.PreferredRoute)
            : ranked;
        var sequentialPriority = candidates.Take(1)
            .Concat(candidates.Skip(1).TakeWhile(candidate => candidate.Servers.Count > 1))
            .ToHashSet();
        var errors = new List<Exception>();
        var proxyReady = new Dictionary<Guid, bool>();
        var racedCandidates = new HashSet<RouteCandidate>();
        var raceAttempted = false;
        RouteLog($"Route order for {server.Name}: preferred=" +
                 (server.PreferredRoute is null ? "none" :
                     $"proxy={server.PreferredRoute.ProxyId}; servers={string.Join('>', server.PreferredRoute.ServerIds)}") +
                 "; candidates=" + string.Join(" | ", candidates.Select(candidate =>
                     $"[{RoutePlanner.CandidateReason(config, server.Id, candidate, server.PreferredRoute)}] " +
                     RouteLabel(candidate))));

        ActiveRoute CompleteRoute(
            (ActiveRoute Route, RouteCandidate Candidate, TimeSpan Duration) result)
        {
            if (!result.Candidate.WithoutProxy)
                AccessGrantPolicy.RememberSuccessfulRoute(config, result.Candidate.Proxy.Id,
                    result.Candidate.Servers.Select(item => item.Id), DateTimeOffset.UtcNow);
            if (forcedViaServerId is null)
                server.PreferredRoute = RoutePreferencePolicy.FromSuccess(
                    result.Candidate, result.Duration, DateTimeOffset.UtcNow);
            server.LastOriginalSessionFallbackUtc = null;
            activeRoutes.Add(result.Route);
            var routeDisplay = FormatRouteDisplay(
                result.Candidate.Proxy, result.Route.Hops, result.Candidate.WithoutProxy);
            lastRouteDisplays[server.Id] = routeDisplay;
            if (selectedServer?.Id == server.Id)
            {
                LastRouteText.Text = routeDisplay;
                LastRouteText.Visibility = Visibility.Visible;
            }
            SaveConfig();
            Log($"{result.Candidate.Proxy.Name} → {string.Join(" → ", result.Candidate.Servers.Select(s => s.Name))}");
            if (forcedViaServerId is Guid successfulViaId)
                RouteLog($"Forced route success: target={server.Name}; via=" +
                         $"{config.FindServer(successfulViaId)?.Name ?? successfulViaId.ToString()}; " +
                         $"route={RouteLabel(result.Candidate)}");
            if (forcedViaServerId is null && consoleOnly &&
                RoutePreferencePolicy.BetterCandidates(ranked, result.Candidate).Count > 0)
                ScheduleBetterRouteProbe(server, ranked, result.Candidate);
            else if (forcedViaServerId is null && consoleOnly && server.TryDirectWithoutJumphost &&
                     !result.Candidate.WithoutProxy)
                ScheduleDirectRouteProbe(server, result.Candidate);
            // Подключились обходным путём, а прямой доступ ещё не подтверждён —
            // пробуем доказать его в фоне, чтобы следующее подключение шло напрямую.
            else if (forcedViaServerId is null && consoleOnly &&
                     result.Candidate.Servers.Count > 1 && !IsProvenDirect(server))
                ScheduleDirectRouteProbe(server, result.Candidate);
            return result.Route;
        }

        static string FormatRouteDisplay(
            BaseProxy proxy, IReadOnlyList<RouteHop> hops, bool withoutProxy)
        {
            var parts = new List<string>();
            parts.Add(withoutProxy
                ? "Прямо с компьютера (без JH)"
                : $"{proxy.Name} [{proxy.Host}:{proxy.Port}]");
            parts.AddRange(hops.Select(hop =>
                $"{hop.ServerName} [{hop.Host}:{hop.Port}]"));
            return "Использован маршрут: " + string.Join(" → ", parts);
        }

        foreach (var candidate in candidates)
        {
            if (racedCandidates.Contains(candidate)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!proxyReady.TryGetValue(candidate.Proxy.Id, out var ready))
            {
                ready = candidate.WithoutProxy || await IsSocks5ReadyAsync(
                    candidate.Proxy, TimeSpan.FromSeconds(1), cancellationToken);
                if (!ready && candidate.Proxy.StartupServerId is not null)
                {
                    Status($"Для маршрута к «{server.Name}» запускаю точку входа «{candidate.Proxy.Name}»…");
                    try
                    {
                        ready = await EnsureManagedJumphostAsync(
                            cancellationToken, true, proxyId: candidate.Proxy.Id,
                            accessTargetServerId: server.Id);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        errors.Add(error);
                        RouteLog($"Jumphost startup failed: name={candidate.Proxy.Name}; endpoint={candidate.Proxy.Host}:{candidate.Proxy.Port}; error={error.GetType().Name}: {error.Message}");
                    }
                }
                proxyReady[candidate.Proxy.Id] = ready;
            }
            if (!ready)
            {
                RouteLog($"Proxy not ready, skipping: target={server.Name}; proxy={candidate.Proxy.Name}:{candidate.Proxy.Port}");
                continue;
            }

            if (config.RaceBestEntryPoints && !candidate.WithoutProxy && !raceAttempted &&
                !sequentialPriority.Contains(candidate) && candidate.Servers.Count == 1)
            {
                var second = candidates.FirstOrDefault(item =>
                    !ReferenceEquals(item, candidate) &&
                    !sequentialPriority.Contains(item) &&
                    item.Servers.Count == 1 &&
                    item.Proxy.Id != candidate.Proxy.Id);
                if (second is not null &&
                    await IsSocks5ReadyAsync(second.Proxy, TimeSpan.FromSeconds(1), cancellationToken))
                {
                    raceAttempted = true;
                    racedCandidates.Add(candidate);
                    racedCandidates.Add(second);
                    try
                    {
                        Status($"Параллельно проверяю две точки входа для «{server.Name}»…");
                        return CompleteRoute(await ssh.ConnectFirstSuccessfulAsync(
                            config, [candidate, second], cancellationToken, consoleOnly));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        errors.Add(error);
                        RouteLog($"Entry point race failed: target={server.Name}; routes={RouteLabel(candidate)} | {RouteLabel(second)}; error={error.GetType().Name}: {error.Message}");
                        continue;
                    }
                }
            }

            try
            {
                Status($"Проверяю маршрут: {RouteLabel(candidate)}…");
                return CompleteRoute(await ssh.ConnectCandidateAsync(
                    config, candidate, cancellationToken, consoleOnly,
                    rememberTargetPreference: forcedViaServerId is null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                errors.Add(error);
                var detail = string.Join(" --> ", ExceptionChain(error).Select(e => $"{e.GetType().Name}: {e.Message}"));
                RouteLog($"Route failed: target={server.Name}; route={RouteLabel(candidate)}; " +
                         $"error={detail}");
            }
        }

        // Mechanism B: all routes failed. Check if a jumphost's access script
        // expired (all control servers unreachable) and try to restore it.
        if (await TryRestoreAccessAsync(candidates, cancellationToken))
        {
            // Access restored — retry all candidates once.
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.WithoutProxy &&
                    !await IsSocks5ReadyAsync(candidate.Proxy, TimeSpan.FromSeconds(1), cancellationToken))
                    continue;
                try
                {
                    Status($"Повторяю маршрут: {RouteLabel(candidate)}…");
                    return CompleteRoute(await ssh.ConnectCandidateAsync(
                        config, candidate, cancellationToken, consoleOnly,
                        rememberTargetPreference: forcedViaServerId is null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    errors.Add(error);
                    RouteLog($"Route retry failed: target={server.Name}; route={RouteLabel(candidate)}; error={error.GetType().Name}: {error.Message}");
                }
            }
        }

        throw new InvalidOperationException(
            forcedViaServerId is Guid failedVia
                ? $"Не удалось подключиться через выбранный сервер «{config.FindServer(failedVia)?.Name ?? failedVia.ToString()}». Обычный маршрут не использован."
                : "Не найден рабочий маршрут. Запустите ручную проверку связности.",
            errors.Count == 0 ? null : new AggregateException(errors));
    }

    /// <summary>
    /// Mechanism B: when all routes fail, check if a jumphost's access script
    /// expired. If all control servers are unreachable, send the script command
    /// to the running jumphost console and re-check.
    /// </summary>
    private async Task<bool> TryRestoreAccessAsync(
        IReadOnlyList<RouteCandidate> candidates, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var proxiesToCheck = candidates
            .Select(c => c.Proxy)
            .DistinctBy(p => p.Id)
            .Where(p => AccessGrantPolicy.ShouldCheckControlsOnFailure(p, now))
            .ToArray();
        if (proxiesToCheck.Length == 0) return false;

        var restored = false;
        foreach (var proxy in proxiesToCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken)) continue;

            if (await ConfirmExistingAccessAsync(proxy, cancellationToken))
                continue; // Jumphost and granted access are still healthy

            RouteLog($"Access expired detected: proxy={proxy.Name}; all {proxy.AccessProbeServerIds.Count} controls unreachable; sending script");
            Status($"Доступ через «{proxy.Name}» истёк. Готовлю запуск скрипта…");
            var jumphostServer = config.FindServer(proxy.StartupServerId ?? Guid.Empty);
            if (jumphostServer is null) continue;
            try
            {
                restored |= await RunAccessScriptWithConsoleChoiceAsync(
                    proxy, jumphostServer, null, "failure-recovery", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                RouteLog($"Access restore error: proxy={proxy.Name}; error={ex.GetType().Name}: {ex.Message}");
            }
        }
        return restored;
    }

    private async Task<bool> RunAccessScriptInExistingConsoleAsync(
        BaseProxy proxy, ManagedServer server, int processId, Guid? targetServerId,
        CancellationToken cancellationToken)
    {
        var proxyId = proxy.Id;
        var requestedAfter = proxy.LastAccessScriptAttemptUtc;
        var gate = accessScriptGates.GetOrAdd(proxyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId)
                ?? throw new InvalidOperationException("Точка входа была удалена во время запуска скрипта.");
            if (proxy.LastAccessScriptAttemptUtc != requestedAfter)
                return proxy.LastAccessScriptResult == "Verified";
            AccessGrantPolicy.MarkScriptAttempt(proxy, DateTimeOffset.UtcNow);
            SaveConfig();
            try
            {
                using var existing = Process.GetProcessById(processId);
                await KittyWindowInput.SendLinesAsync(existing,
                    JumphostStartupPlan.BuildPostLogin(proxy, server, true)
                        .Select(step => step.Response).ToArray(), cancellationToken);
                if (proxy.PostCommandReadyDelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(proxy.PostCommandReadyDelaySeconds), cancellationToken);
                var controls = await VerifyAccessControlsAsync(proxy, targetServerId, cancellationToken);
                proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
                if (controls.Count == 0)
                {
                    AccessGrantPolicy.MarkScriptUnconfirmed(proxy, DateTimeOffset.UtcNow);
                    SaveConfig();
                    return false;
                }
                AccessGrantPolicy.MarkScriptSuccess(proxy, DateTimeOffset.UtcNow);
                AccessGrantPolicy.RememberReachableControls(
                    config, proxy.Id, controls, DateTimeOffset.UtcNow);
                SaveConfig();
                return true;
            }
            catch
            {
                proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
                AccessGrantPolicy.MarkScriptUnconfirmed(proxy, DateTimeOffset.UtcNow);
                SaveConfig();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RunAccessScriptWithConsoleChoiceAsync(
        BaseProxy proxy, ManagedServer server, Guid? targetServerId, string trigger,
        CancellationToken cancellationToken)
    {
        var entryTitle = JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name);
        var accessTitle = JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name);
        var managedMain = jumphostProcesses.TryGetAliveManaged(proxy, out var mainIdentity);
        int? accessPid = null;
        if (jumphostProcesses.TryGetAlive(proxy, JumphostConsoleKind.Access, out var accessIdentity))
        {
            accessPid = accessIdentity.ProcessId;
        }
        else
        {
            using var accessConsole = KittyWindowInput.FindByTitle(accessTitle);
            if (accessConsole is not null &&
                jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Access, accessConsole.Id, accessTitle))
            {
                accessPid = accessConsole.Id;
                PersistConsoles();
                RouteLog($"Access script console adopted by title: proxy={proxy.Name}; " +
                         $"trigger={trigger}; pid={accessPid}");
            }
        }
        var identity = jumphostProcesses.Classify(proxy, entryTitle);
        var owner = identity.Kind switch
        {
            JumphostProcessKind.UnknownKitty => AccessScriptConsoleOwner.UnknownKitty,
            JumphostProcessKind.NonKitty => AccessScriptConsoleOwner.NonKitty,
            _ => AccessScriptConsoleOwner.NoListener
        };
        var target = AccessScriptConsolePolicy.DecideConsole(
            managedMain, accessPid is not null, owner);
        if (target == AccessScriptConsoleTarget.ManagedMain && mainIdentity is not null)
        {
            RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                     $"owner={identity.Kind}; choice=existing-main; pid={mainIdentity.ProcessId}");
            return await RunAccessScriptInExistingConsoleAsync(
                proxy, server, mainIdentity.ProcessId, targetServerId, cancellationToken);
        }
        if (target == AccessScriptConsoleTarget.ExistingAccess && accessPid is int existingPid)
        {
            RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                     $"owner={identity.Kind}; choice=existing-access; pid={existingPid}");
            return await RunAccessScriptInExistingConsoleAsync(
                proxy, server, existingPid, targetServerId, cancellationToken);
        }
        if (target == AccessScriptConsoleTarget.PromptUnknown)
        {
            var choice = await Dispatcher.InvokeAsync(() => ThemedMessageDialog.Show(this,
                $"Порт SOCKS5 {proxy.Host}:{proxy.Port} принадлежит неизвестной KiTTY " +
                $"(PID {identity.ProcessId}, «{identity.WindowTitle}»).\n\n" +
                "«Да» — использовать эту консоль.\n" +
                "«Нет» — открыть отдельную служебную консоль.\n" +
                "«Отмена» — не запускать скрипт.",
                "Неизвестная консоль JH", MessageBoxButton.YesNoCancel, MessageBoxImage.Question));
            if (choice == MessageBoxResult.Cancel)
            {
                accessPromptCancelledUtc[proxy.Id] = DateTimeOffset.UtcNow;
                var promptEligible = AccessScriptConsolePolicy.PromptEligibleUtc(
                    accessPromptCancelledUtc[proxy.Id], proxy.ScheduledRestartMinutes);
                RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                         $"owner=unknown-kitty; choice=cancel; pid={identity.ProcessId}; " +
                         $"prompt-eligible={promptEligible:O}");
                Status($"Запуск скрипта через «{proxy.Name}» отменён пользователем");
                return false;
            }
            if (choice == MessageBoxResult.Yes && identity.ProcessId is int pid)
            {
                if (!jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Entry, pid,
                        identity.WindowTitle.Length > 0 ? identity.WindowTitle : entryTitle))
                    throw new InvalidOperationException(
                        "Выбранная консоль KiTTY завершилась до принятия её под управление.");
                PersistConsoles();
                accessPromptCancelledUtc.Remove(proxy.Id);
                RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                         $"owner=unknown-kitty; choice=existing-adopted; pid={pid}");
                return await RunAccessScriptInExistingConsoleAsync(
                    proxy, server, pid, targetServerId, cancellationToken);
            }
            RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                     $"owner=unknown-kitty; choice=isolated; pid={identity.ProcessId}");
        }
        else
        {
            RouteLog($"Access script console choice: proxy={proxy.Name}; trigger={trigger}; " +
                     $"owner={identity.Kind}; choice=isolated; pid={identity.ProcessId}");
        }
        return await RunAccessScriptAndConfirmAsync(proxy, server, targetServerId, cancellationToken);
    }

    private static string RouteLabel(RouteCandidate candidate) =>
        (candidate.WithoutProxy
            ? "Прямо без JH → "
            : $"{candidate.Proxy.Name}:{candidate.Proxy.Port} → ") +
        string.Join(" → ", candidate.Servers.Select(item => item.Name));

    /// <summary>
    /// Background probe: when the fallback (original session) is active,
    /// periodically try managed routes. If one succeeds, clear the fallback
    /// flag so next time the managed route is used directly.
    /// </summary>
    private void ScheduleFallbackRouteProbe(ManagedServer server, IReadOnlyList<RouteCandidate> ranked)
    {
        var lease = backgroundRouteProbes.TryStart(server.Id);
        if (lease is null)
        {
            RouteLog($"Fallback probe skipped: target={server.Name}; another background probe is running");
            return;
        }
        _ = ProbeAsync(lease);
        return;

        async Task ProbeAsync(BackgroundProbeRegistry.Lease probe)
        {
            using (probe)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), probe.Token);
                foreach (var candidate in ranked)
                {
                    probe.Token.ThrowIfCancellationRequested();
                    if (server.LastOriginalSessionFallbackUtc is null) return; // Already resolved
                    if (!candidate.WithoutProxy &&
                        !await IsSocks5ReadyAsync(candidate.Proxy, TimeSpan.FromSeconds(1), probe.Token))
                        continue;
                    RouteLog($"Fallback probe: target={server.Name}; trying={RouteLabel(candidate)}");
                    try
                    {
                        var result = await ssh.ConnectCandidateAsync(
                            config, candidate, probe.Token, true);
                        result.Route.Dispose();
                        // Success! Clear fallback and save the preferred route.
                        server.LastOriginalSessionFallbackUtc = null;
                        server.PreferredRoute = RoutePreferencePolicy.FromSuccess(
                            candidate, result.Duration, DateTimeOffset.UtcNow);
                        if (!candidate.WithoutProxy)
                            AccessGrantPolicy.RememberSuccessfulRoute(config, candidate.Proxy.Id,
                                candidate.Servers.Select(item => item.Id), DateTimeOffset.UtcNow);
                        SaveConfig();
                        RouteLog($"Fallback probe success: target={server.Name}; route={RouteLabel(candidate)}; will use managed route next time");
                        Status($"Для «{server.Name}» снова доступен управляемый маршрут");
                        return;
                    }
                    catch (Exception ex)
                    {
                        RouteLog($"Fallback probe failed: target={server.Name}; route={RouteLabel(candidate)}; error={ex.GetType().Name}: {ex.Message}");
                    }
                }
                RouteLog($"Fallback probe: target={server.Name}; no managed route available; keeping fallback");
            }
            catch (OperationCanceledException) when (probe.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                RouteLog($"Fallback probe stopped: target={server.Name}; error={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ScheduleBetterRouteProbe(
        ManagedServer server, IReadOnlyList<RouteCandidate> ranked, RouteCandidate current)
    {
        var lease = backgroundRouteProbes.TryStart(server.Id);
        if (lease is null)
        {
            RouteLog($"Background route recovery skipped: target={server.Name}; another probe is running");
            return;
        }
        _ = ProbeAsync(lease);
        return;

        async Task ProbeAsync(BackgroundProbeRegistry.Lease probe)
        {
            using (probe)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), probe.Token);
                foreach (var candidate in RoutePreferencePolicy.BetterCandidates(ranked, current))
                {
                    probe.Token.ThrowIfCancellationRequested();
                    if (!RoutePreferencePolicy.Matches(server.PreferredRoute, current) ||
                        (!candidate.WithoutProxy &&
                         !await IsSocks5ReadyAsync(candidate.Proxy, TimeSpan.FromSeconds(1), probe.Token)))
                        continue;
                    RouteLog($"Background route recovery: target={server.Name}; checking={RouteLabel(candidate)}");
                    try
                    {
                        var result = await ssh.ConnectCandidateAsync(
                            config, candidate, probe.Token, true);
                        result.Route.Dispose();
                        if (!RoutePreferencePolicy.CanCommitBackgroundResult(
                                server.PreferredRoute, current, candidate))
                            return;
                        server.PreferredRoute = RoutePreferencePolicy.FromSuccess(
                            candidate, result.Duration, DateTimeOffset.UtcNow);
                        SaveConfig();
                        RouteLog($"Background route recovery: target={server.Name}; recovered={RouteLabel(candidate)}");
                        Status($"Для «{server.Name}» снова доступен более быстрый маршрут");
                        return;
                    }
                    catch (Exception ex)
                    {
                        RouteLog($"Background route recovery failed: target={server.Name}; route={RouteLabel(candidate)}; error={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (probe.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                RouteLog($"Background route recovery stopped: target={server.Name}; error={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static bool IsProvenDirect(ManagedServer server) =>
        server.PreferredRoute is { } preferred &&
        preferred.ServerIds.Count == 1 && preferred.ServerIds[0] == server.Id;

    private static bool IsProvenDirectWithoutJumphost(ManagedServer server) =>
        server.PreferredRoute is { ProxyId: var proxyId } preferred &&
        proxyId == Guid.Empty &&
        preferred.ServerIds.Count == 1 && preferred.ServerIds[0] == server.Id;

    /// <summary>
    /// Фоновая допроверка неподтверждённого прямого доступа: если подключение
    /// прошло обходным маршрутом, пробуем достучаться до сервера напрямую через
    /// точку входа (с учётом резервных адресов). При успехе прямой путь
    /// запоминается и дальше используется первым.
    /// </summary>
    private void ScheduleDirectRouteProbe(ManagedServer server, RouteCandidate used)
    {
        var directCandidates = RoutePlanner.DirectCandidates(config, server.Id)
            .OrderByDescending(candidate => candidate.WithoutProxy)
            .ThenByDescending(candidate => candidate.Proxy.Id == used.Proxy.Id)
            .ToArray();
        if (directCandidates.Length == 0) return;
        var lease = backgroundRouteProbes.TryStart(server.Id);
        if (lease is null)
        {
            RouteLog($"Background direct probe skipped: target={server.Name}; another probe is running");
            return;
        }
        _ = ProbeAsync(lease);
        return;

        async Task ProbeAsync(BackgroundProbeRegistry.Lease probe)
        {
            using (probe)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), probe.Token);
                foreach (var candidate in directCandidates)
                {
                    probe.Token.ThrowIfCancellationRequested();
                    if (server.TryDirectWithoutJumphost &&
                        IsProvenDirectWithoutJumphost(server)) return;
                    if (!candidate.WithoutProxy &&
                        !await IsSocks5ReadyAsync(candidate.Proxy, TimeSpan.FromSeconds(1), probe.Token))
                        continue;
                    RouteLog($"Background direct probe: target={server.Name}; checking={RouteLabel(candidate)}");
                    try
                    {
                        var result = await ssh.ConnectCandidateAsync(
                            config, candidate, probe.Token, true);
                        result.Route.Dispose();
                        if (server.TryDirectWithoutJumphost &&
                            IsProvenDirectWithoutJumphost(server)) return;
                        server.PreferredRoute = RoutePreferencePolicy.FromSuccess(
                            candidate, result.Duration, DateTimeOffset.UtcNow);
                        SaveConfig();
                        RouteLog($"Background direct probe: target={server.Name}; direct confirmed={RouteLabel(candidate)}");
                        Status($"Для «{server.Name}» подтверждён прямой доступ — дальше будет использоваться он");
                        return;
                    }
                    catch (Exception ex)
                    {
                        RouteLog($"Background direct probe failed: target={server.Name}; route={RouteLabel(candidate)}; error={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (probe.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                RouteLog($"Background direct probe stopped: target={server.Name}; error={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void AccessScriptAlarm_Tick(object? sender, EventArgs e) =>
        _ = RunAccessScriptAlarmAsync();

    private async Task RunAccessScriptAlarmAsync()
    {
        if (accessScriptAlarmRunning)
        {
            accessScriptAlarmRerunRequested = true;
            return;
        }
        if (!configAvailable ||
            accessScriptAlarmCancellation.IsCancellationRequested) return;
        accessScriptAlarmRunning = true;
        var handledAny = false;
        try
        {
            foreach (var proxy in config.BaseProxies.Where(proxy =>
                         proxy.Enabled &&
                         !AccessScriptConsolePolicy.IsPromptSnoozed(
                             accessPromptCancelledUtc.GetValueOrDefault(proxy.Id),
                             proxy.ScheduledRestartMinutes, DateTimeOffset.UtcNow) &&
                         ((!accessStartupPreflightCompleted.Contains(proxy.Id) &&
                           AccessGrantPolicy.ShouldRunStartupPreflight(proxy)) ||
                          AccessGrantPolicy.ShouldInitializeAccessBaseline(proxy, DateTimeOffset.UtcNow) ||
                          AccessGrantPolicy.ShouldRunScheduledRestart(proxy, DateTimeOffset.UtcNow))))
            {
                handledAny = true;
                accessScriptAlarmCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    await RunScheduledAccessScriptAsync(
                        proxy, accessScriptAlarmCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (accessScriptAlarmCancellation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    RouteLog($"Access script alarm error: proxy={proxy.Name}; " +
                             $"error={ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
            when (accessScriptAlarmCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RouteLog($"Access script alarm error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            accessScriptAlarmRunning = false;
            if (handledAny) Status("");
            RefreshAccessScriptAlarmInterval();
            if (accessScriptAlarmRerunRequested && !accessScriptAlarmCancellation.IsCancellationRequested)
            {
                accessScriptAlarmRerunRequested = false;
                _ = Dispatcher.BeginInvoke(() => _ = RunAccessScriptAlarmAsync());
            }
        }
    }

    private async Task RunScheduledAccessScriptAsync(
        BaseProxy proxy, CancellationToken cancellationToken)
    {
        var server = config.FindServer(proxy.StartupServerId ?? Guid.Empty);
        if (server is null)
        {
            RouteLog($"Access script alarm skipped: proxy={proxy.Name}; managed JH session is not configured");
            return;
        }

        var initializeBaseline = AccessGrantPolicy.ShouldInitializeAccessBaseline(
            proxy, DateTimeOffset.UtcNow);
        if (!await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken))
        {
            if (!initializeBaseline &&
                !AccessGrantPolicy.ShouldRunScheduledRestart(proxy, DateTimeOffset.UtcNow)) return;
            Status($"По расписанию запускаю точку входа «{proxy.Name}»…");
            var startupPreflightPending = !accessStartupPreflightCompleted.Contains(proxy.Id);
            var ready = await EnsureManagedJumphostAsync(
                cancellationToken, true, proxyId: proxy.Id,
                forceAccessScriptProxyId: initializeBaseline || startupPreflightPending ? null : proxy.Id,
                deferAccessScriptUntilReady: startupPreflightPending);
            if (ready && startupPreflightPending)
                await RunScheduledAccessScriptAsync(proxy, cancellationToken);
            return;
        }

        var gate = jumphostStartupGates.GetOrAdd(proxy.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!accessStartupPreflightCompleted.Contains(proxy.Id))
            {
                var accessAlreadyValid = await ConfirmExistingAccessAsync(
                    proxy, cancellationToken, rebaseSchedule: true);
                accessStartupPreflightCompleted.Add(proxy.Id);
                if (accessAlreadyValid)
                {
                    var expectedTitle = JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name);
                    var identity = jumphostProcesses.Classify(proxy, expectedTitle);
                    var owner = identity.Kind switch
                    {
                        JumphostProcessKind.UnknownKitty => AccessScriptConsoleOwner.UnknownKitty,
                        JumphostProcessKind.NonKitty => AccessScriptConsoleOwner.NonKitty,
                        _ => AccessScriptConsoleOwner.NoListener
                    };
                    if (AccessScriptConsolePolicy.ShouldAutoAdoptAfterSuccessfulPreflight(
                            owner, identity.TitleMatches, identity.ProcessId, processAlive: true) &&
                        identity.ProcessId is int pid &&
                        jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Entry, pid, expectedTitle))
                    {
                        PersistConsoles();
                        RouteLog($"Access startup preflight adopted KiTTY: proxy={proxy.Name}; " +
                                 $"pid={pid}; title={identity.WindowTitle}");
                    }
                    RouteLog($"Access startup preflight rebased schedule: proxy={proxy.Name}");
                    return;
                }
            }
            if (AccessGrantPolicy.ShouldInitializeAccessBaseline(proxy, DateTimeOffset.UtcNow))
            {
                RouteLog($"Access baseline preflight failed: proxy={proxy.Name}; script=required");
                await RunAccessScriptWithConsoleChoiceAsync(
                    proxy, server, null, "cold-start", cancellationToken);
                return;
            }
            if (!AccessGrantPolicy.ShouldRunScheduledRestart(proxy, DateTimeOffset.UtcNow))
            {
                RouteLog($"Access script alarm reused completed operation: proxy={proxy.Name}");
                return;
            }
            RouteLog($"Access script alarm fired: proxy={proxy.Name}");
            Status($"По расписанию запускаю скрипт доступа через «{proxy.Name}»…");
            await RunAccessScriptWithConsoleChoiceAsync(
                proxy, server, null, "scheduled", cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RunAccessScriptAndConfirmAsync(
        BaseProxy proxy, ManagedServer server, Guid? targetServerId,
        CancellationToken cancellationToken)
    {
        var proxyId = proxy.Id;
        var requestedAfter = proxy.LastAccessScriptAttemptUtc;
        var gate = accessScriptGates.GetOrAdd(proxyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId)
                ?? throw new InvalidOperationException("Точка входа была удалена во время запуска скрипта.");
            if (proxy.LastAccessScriptAttemptUtc != requestedAfter)
            {
                RouteLog($"Access script reused completed operation: proxy={proxy.Name}");
                return proxy.LastAccessScriptResult == "Verified";
            }
            return await RunAccessScriptAndConfirmCoreAsync(
                proxy, server, targetServerId, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RunAccessScriptAndConfirmCoreAsync(
        BaseProxy proxy, ManagedServer server, Guid? targetServerId,
        CancellationToken cancellationToken)
    {
        var proxyId = proxy.Id;
        AccessGrantPolicy.MarkScriptAttempt(proxy, DateTimeOffset.UtcNow);
        SaveConfig();
        AccessScriptRunnerPlan? plan = null;
        Process? process = null;
        var cleanupAfterExit = false;
        try
        {
            var kitty = !string.IsNullOrWhiteSpace(server.SourceSessionPath) && File.Exists(server.SourceSessionPath)
                ? FindKittyNearSession(server.SourceSessionPath!) ?? ResolveProgram(config.KittyPath)
                : ResolveProgram(config.KittyPath);
            plan = AccessScriptRunnerPlan.Create(kitty, server, proxy, DateTimeOffset.UtcNow);
            process = Process.Start(plan.StartInfo)
                ?? throw new InvalidOperationException("KiTTY для скрипта доступа не удалось запустить.");
            // Remember the service console so every later attempt reuses it
            // instead of opening another duplicate window.
            jumphostProcesses.Remember(proxy, JumphostConsoleKind.Access, process,
                JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name));
            PersistConsoles();
            if (plan.TotpCodeForWindowInput is not null)
                await KittyWindowInput.SendLineAsync(process, plan.TotpCodeForWindowInput, cancellationToken);

            var deadline = DateTime.UtcNow + plan.CompletionTimeout;
            while (DateTime.UtcNow < deadline && !process.HasExited && !plan.HasCompletionMarker())
                await Task.Delay(500, cancellationToken);
            var markerSeen = plan.HasCompletionMarker();
            if (!markerSeen)
            {
                proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
                AccessGrantPolicy.MarkScriptUnconfirmed(proxy, DateTimeOffset.UtcNow);
                SaveConfig();
                var earlyExit = process.HasExited;
                RouteLog($"Access script unconfirmed: proxy={proxy.Name}; " +
                         (earlyExit ? "KiTTY exited before marker" : "marker timeout; process left running"));
                cleanupAfterExit = true;
                _ = plan.DisposeAfterExitAsync(process);
                Status(earlyExit
                    ? $"KiTTY скрипта через «{proxy.Name}» закрылась до подтверждения завершения"
                    : $"Запуск скрипта через «{proxy.Name}» не подтверждён за " +
                      $"{(int)plan.CompletionTimeout.TotalSeconds} сек.; консоль оставлена открытой");
                return false;
            }

            var controls = await VerifyAccessControlsAsync(proxy, targetServerId, cancellationToken);
            proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
            if (controls.Count == 0)
            {
                IReadOnlyList<Guid> candidates;
                try { candidates = await DiscoverAccessControlsAsync(proxy, targetServerId, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { candidates = []; }
                proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
                AccessGrantPolicy.RememberControlCandidates(config, proxy.Id, candidates);
                AccessGrantPolicy.MarkScriptUnconfirmed(proxy, DateTimeOffset.UtcNow);
                SaveConfig();
                RouteLog($"Access script unconfirmed: proxy={proxy.Name}; marker=seen; controls=0; " +
                         $"learned-candidates={candidates.Count}");
                Status("Команда завершилась, но доступ к целевому или контрольному серверу не подтверждён");
                return false;
            }
            IReadOnlyList<Guid> learned;
            try { learned = await DiscoverAccessControlsAsync(proxy, targetServerId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { learned = controls; }
            proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
            AccessGrantPolicy.MarkScriptSuccess(proxy, DateTimeOffset.UtcNow);
            AccessGrantPolicy.RememberReachableControls(config, proxy.Id,
                learned.Count > 0 ? learned : controls, DateTimeOffset.UtcNow);
            SaveConfig();
            RouteLog($"Access script verified: proxy={proxy.Name}; controls={controls.Count}; learned={learned.Count}");
            Status($"Скрипт доступа через «{proxy.Name}» подтверждён ({controls.Count} серверов)");
            return true;
        }
        catch
        {
            proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId) ?? proxy;
            AccessGrantPolicy.MarkScriptUnconfirmed(proxy, DateTimeOffset.UtcNow);
            SaveConfig();
            if (plan is not null && process is not null)
            {
                cleanupAfterExit = true;
                _ = plan.DisposeAfterExitAsync(process);
            }
            throw;
        }
        finally
        {
            if (!cleanupAfterExit)
            {
                plan?.Dispose();
                process?.Dispose();
            }
        }
    }

    private async Task<bool> EnsureManagedJumphostAsync(
        CancellationToken cancellationToken, bool startMissing, Guid? startupServerId = null,
        Guid? proxyId = null, Guid? accessTargetServerId = null, bool startAllAutoStart = false,
        Guid? forceAccessScriptProxyId = null, bool deferAccessScriptUntilReady = false)
    {
        var ordered = RoutePlanner.OrderedProxies(config)
            .Where(proxy => startupServerId is null || proxy.StartupServerId == startupServerId)
            .Where(proxy => proxyId is null || proxy.Id == proxyId)
            .ToArray();
        if (!startMissing && !startAllAutoStart)
            foreach (var proxy in ordered)
                if (await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken)) return true;

        var anyReady = false;
        foreach (var candidateProxy in ordered.Where(item => item.StartupServerId is not null &&
                                                             (startMissing || item.AutoStartWhenUnavailable)))
        {
            var proxy = candidateProxy;
            if (await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken))
            {
                anyReady = true;
                continue;
            }
            var gate = jumphostStartupGates.GetOrAdd(proxy.Id, _ => new SemaphoreSlim(1, 1));
            RouteLog($"Jumphost startup gate waiting: name={proxy.Name}; endpoint={proxy.Host}:{proxy.Port}");
            await gate.WaitAsync(cancellationToken);
            try
            {
                proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxy.Id) ?? proxy;
                if (await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken))
                {
                    RouteLog($"Jumphost startup reused: name={proxy.Name}; endpoint={proxy.Host}:{proxy.Port}");
                    anyReady = true;
                    if (!startAllAutoStart) return true;
                    continue;
                }
                if (jumphostProcesses.TryGetAliveManaged(proxy, out var existingProcess))
                {
                    RouteLog($"Jumphost startup blocked by existing process: name={proxy.Name}; " +
                             $"endpoint={proxy.Host}:{proxy.Port}; pid={existingProcess.ProcessId}; socks=not-ready");
                    Status($"KiTTY точки входа «{proxy.Name}» уже запущена (PID {existingProcess.ProcessId}), " +
                           "но SOCKS5 ещё недоступен. Проверьте открытую консоль.");
                    continue;
                }
            var server = config.FindServer(proxy.StartupServerId!.Value);
            if (server is null) continue;
            var entryTitle = JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name);
            var startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var forcedScheduledAccessScript = proxy.Id == forceAccessScriptProxyId;
            var runAccessScript = forcedScheduledAccessScript ||
                AccessGrantPolicy.ShouldRunAccessScript(proxy, DateTimeOffset.UtcNow);

            async Task<bool> WaitForReadyAsync()
            {
                var deadline = DateTime.UtcNow.AddSeconds(config.ConnectionTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken))
                    {
                        if (!deferAccessScriptUntilReady)
                        {
                            var accessAlreadyValid = !forcedScheduledAccessScript &&
                                await ConfirmExistingAccessAsync(proxy, cancellationToken);
                            var learnedAccessUnavailable = proxy.AccessProbeServerIds.Count > 0 && !accessAlreadyValid;
                            if (!accessAlreadyValid && (runAccessScript || learnedAccessUnavailable))
                            {
                                Status($"Контрольные серверы через «{proxy.Name}» недоступны. " +
                                       "Открываю отдельную служебную консоль скрипта доступа…");
                                await RunAccessScriptWithConsoleChoiceAsync(
                                    proxy, server, accessTargetServerId,
                                    forcedScheduledAccessScript ? "scheduled" : "cold-start",
                                    cancellationToken);
                            }
                        }
                        proxy.LastSuccessUtc = DateTimeOffset.UtcNow;
                        proxy.LastStartupLatencyMs = startupStopwatch.Elapsed.TotalMilliseconds;
                        SaveConfig();
                        Status($"Точка входа «{proxy.Name}» готова");
                        return true;
                    }
                    Status($"Жду SOCKS5 «{proxy.Name}» на {proxy.Host}:{proxy.Port}…");
                    await Task.Delay(1000, cancellationToken);
                }
                RouteLog($"Jumphost startup timeout: name={proxy.Name}; endpoint={proxy.Host}:{proxy.Port}");
                return false;
            }

            // A forgotten own console (manager restarted, notebook lost) must be
            // adopted by its title instead of spawning a duplicate.
            using (var titled = KittyWindowInput.FindByTitle(entryTitle))
            {
                if (titled is not null)
                {
                    jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Entry, titled.Id, entryTitle);
                    PersistConsoles();
                    RouteLog($"Jumphost startup adopted forgotten console: name={proxy.Name}; " +
                             $"endpoint={proxy.Host}:{proxy.Port}; pid={titled.Id}");
                    if (await WaitForReadyAsync())
                    {
                        if (deferAccessScriptUntilReady) return true;
                        anyReady = true;
                        if (!startAllAutoStart) return true;
                    }
                    else
                    {
                        Status($"KiTTY точки входа «{proxy.Name}» открыта (PID {titled.Id}), " +
                               "но SOCKS5 недоступен. Проверьте открытую консоль.");
                    }
                    continue;
                }
            }

            Status($"Запускаю точку входа «{proxy.Name}»…");
            if (!proxy.UseAutomaticPort &&
                await IsTcpPortOpenAsync(proxy.Host, proxy.Port, TimeSpan.FromSeconds(1), cancellationToken))
                throw new InvalidOperationException(
                    $"Порт {proxy.Host}:{proxy.Port} занят процессом, который не отвечает как SOCKS5. " +
                    "Освободите порт или включите автоматический выбор порта.");
            var port = JumphostPortSelector.Select(proxy);
            KittyLoginScript? loginScript = null;
            try
            {
                if (proxy.TotpSecret.Length > 0)
                {
                    var period = Math.Max(1, proxy.TotpPeriodSeconds);
                    var remaining = period - (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % period);
                    if (remaining < Math.Min(8, period))
                    {
                        Status($"Жду новое окно TOTP для «{proxy.Name}»…");
                        await Task.Delay(TimeSpan.FromSeconds(remaining + 1), cancellationToken);
                    }
                }
                // The long-lived SOCKS console only authenticates. Access commands
                // run in a separate visible console with a completion marker.
                var steps = JumphostStartupPlan.BuildPostLogin(proxy, server, false);
                loginScript = KittyLoginScript.Create(steps);
                var kitty = !string.IsNullOrWhiteSpace(server.SourceSessionPath) && File.Exists(server.SourceSessionPath)
                    ? FindKittyNearSession(server.SourceSessionPath!) ?? ResolveProgram(config.KittyPath)
                    : ResolveProgram(config.KittyPath);
                var startInfo = new ProcessStartInfo(kitty) { WorkingDirectory = Path.GetDirectoryName(kitty)! };
                if (!string.IsNullOrWhiteSpace(server.SourceSessionPath) && File.Exists(server.SourceSessionPath))
                {
                    startInfo.ArgumentList.Add("-load");
                    startInfo.ArgumentList.Add(server.Name);
                }
                if (string.IsNullOrWhiteSpace(server.SourceSessionPath) || !File.Exists(server.SourceSessionPath))
                {
                    startInfo.ArgumentList.Add("-ssh"); startInfo.ArgumentList.Add(server.Host);
                    startInfo.ArgumentList.Add("-P"); startInfo.ArgumentList.Add(server.Port.ToString());
                    if (server.Username.Length > 0) { startInfo.ArgumentList.Add("-l"); startInfo.ArgumentList.Add(server.Username); }
                }
                var preserveSavedAuthentication = !string.IsNullOrWhiteSpace(server.SourceSessionPath) &&
                    File.Exists(server.SourceSessionPath) && proxy.TotpSecret.Length == 0;
                foreach (var argument in JumphostStartupPlan.KittyAuthenticationArguments(
                             server, preserveSavedAuthentication))
                    startInfo.ArgumentList.Add(argument);
                var startupKeyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ");
                if (startupKeyPath is not null)
                { startInfo.ArgumentList.Add("-i"); startInfo.ArgumentList.Add(startupKeyPath); }
                startInfo.ArgumentList.Add("-D"); startInfo.ArgumentList.Add(port.ToString());
                if (loginScript is not null)
                { startInfo.ArgumentList.Add("-loginscript"); startInfo.ArgumentList.Add(loginScript.Path); }
                startInfo.ArgumentList.Add("-title"); startInfo.ArgumentList.Add(entryTitle);
                var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("KiTTY не удалось запустить.");
                proxy.Port = port;
                jumphostProcesses.Remember(proxy, JumphostConsoleKind.Entry, process, entryTitle);
                PersistConsoles();
                RouteLog($"Jumphost launched: name={proxy.Name}; endpoint={proxy.Host}:{proxy.Port}; pid={process.Id}");
                if (proxy.TotpSecret.Length > 0)
                {
                    var code = TotpGenerator.Generate(proxy.TotpSecret, DateTimeOffset.UtcNow,
                        proxy.TotpDigits, proxy.TotpPeriodSeconds, proxy.TotpAlgorithm);
                    await KittyWindowInput.SendLineAsync(process, code, cancellationToken);
                }

                if (await WaitForReadyAsync())
                {
                    if (deferAccessScriptUntilReady) return true;
                    anyReady = true;
                    if (!startAllAutoStart) return true;
                }
            }
            finally { loginScript?.Dispose(); }
            }
            finally { gate.Release(); }
        }
        return anyReady;
    }

    private void PersistConsoles()
    {
        try { JumphostConsoleStore.Save(ConsoleStorePath, jumphostProcesses.Snapshot()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Startup: restore the persisted console notebook and adopt own consoles
    /// whose titles match but which the notebook no longer remembers, so the
    /// manager never treats its own KiTTY windows as foreign duplicates.
    /// </summary>
    private void RestoreRememberedConsoles()
    {
        if (!configAvailable) return;
        foreach (var record in JumphostConsoleStore.Load(ConsoleStorePath))
            if (jumphostProcesses.Restore(record))
                RouteLog($"Console notebook restored: proxy={record.ProxyId}; kind={record.Kind}; " +
                         $"pid={record.ProcessId}; title={record.Title}");
        foreach (var proxy in config.BaseProxies)
        {
            if (proxy.StartupServerId is not Guid serverId) continue;
            var server = config.FindServer(serverId);
            if (server is null) continue;
            var entryTitle = JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name);
            if (!jumphostProcesses.TryGetAlive(proxy, JumphostConsoleKind.Entry, out _))
            {
                using var entry = KittyWindowInput.FindByTitle(entryTitle);
                if (entry is not null &&
                    jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Entry, entry.Id, entryTitle))
                    RouteLog($"Startup adoption by title: proxy={proxy.Name}; kind=entry; pid={entry.Id}");
            }
            var accessTitle = JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name);
            if (!jumphostProcesses.TryGetAlive(proxy, JumphostConsoleKind.Access, out _))
            {
                var alive = KittyWindowInput.FindAllByTitle(accessTitle);
                try
                {
                    var newest = alive.MaxBy(item => item.StartTime.ToUniversalTime());
                    if (newest is not null &&
                        jumphostProcesses.Adopt(proxy, JumphostConsoleKind.Access, newest.Id, accessTitle))
                        RouteLog($"Startup adoption by title: proxy={proxy.Name}; kind=access; pid={newest.Id}");
                }
                finally
                {
                    foreach (var item in alive) item.Dispose();
                }
            }
        }
        PersistConsoles();
    }

    /// <summary>
    /// Startup offer to close leftover duplicate consoles (older windows with
    /// the same title); the newest one stays under management.
    /// </summary>
    private void OfferDuplicateConsoleCleanup()
    {
        if (!configAvailable) return;
        var total = config.BaseProxies.Sum(CountExtraConsoles);
        if (total == 0) return;
        var choice = ThemedMessageDialog.Show(this,
            $"Найдено лишних дублирующих консолей KiTTY: {total}. " +
            "Менеджер оставит самые свежие и закроет остальные.\n\nЗакрыть лишние консоли?",
            "Лишние консоли", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        foreach (var proxy in config.BaseProxies.ToList())
            CloseExtraConsolesFor(proxy.Id);
        Status("Лишние консоли закрыты");
    }

    private int CountExtraConsoles(BaseProxy proxy) => CloseExtraConsoles(proxy, false);

    private int CloseExtraConsolesFor(Guid proxyId)
    {
        var proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
        return proxy is null ? 0 : CloseExtraConsoles(proxy, true);
    }

    private int CloseExtraConsoles(BaseProxy proxy, bool close)
    {
        if (proxy.StartupServerId is not Guid serverId) return 0;
        var server = config.FindServer(serverId);
        if (server is null) return 0;
        var closed = 0;
        foreach (var (kind, title) in new[]
                 {
                     (JumphostConsoleKind.Entry, JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name)),
                     (JumphostConsoleKind.Access, JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name))
                 })
        {
            var alive = KittyWindowInput.FindAllByTitle(title);
            try
            {
                var extras = JumphostConsoleCleanupPolicy.ChooseExtras(alive
                    .Select(item => new ConsoleCandidate(item.Id, item.StartTime.ToUniversalTime()))
                    .ToList());
                if (extras.Count == 0) continue;
                var keep = alive.FirstOrDefault(item => extras.All(extra => extra.ProcessId != item.Id));
                if (keep is not null)
                    jumphostProcesses.Adopt(proxy, kind, keep.Id, title);
                if (!close)
                {
                    closed += extras.Count;
                    continue;
                }
                foreach (var extra in extras)
                {
                    var duplicate = alive.First(item => item.Id == extra.ProcessId);
                    try
                    {
                        if (!duplicate.CloseMainWindow()) continue;
                        closed++;
                        RouteLog($"Duplicate console closed: proxy={proxy.Name}; kind={kind}; pid={extra.ProcessId}");
                    }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                foreach (var item in alive) item.Dispose();
            }
        }
        if (closed > 0) PersistConsoles();
        return closed;
    }

    private string DescribeConsoleState(Guid proxyId)
    {
        var proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
        if (proxy is null) return "—";
        if (proxy.StartupServerId is not Guid serverId) return "сессия jumphost не назначена";
        var server = config.FindServer(serverId);
        if (server is null) return "сессия jumphost не найдена";
        var parts = new List<string>();
        if (jumphostProcesses.TryGetAliveManaged(proxy, out var entry))
            parts.Add($"основная: своя, PID {entry.ProcessId}");
        else
        {
            using var titledEntry = KittyWindowInput.FindByTitle(
                JumphostConsoleTitles.EntryTitle(server.Name, proxy.Name));
            parts.Add(titledEntry is null
                ? "основная: не запущена"
                : $"основная: открыта вне менеджера, PID {titledEntry.Id}");
        }
        var accessConsoles = KittyWindowInput.FindAllByTitle(
            JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name));
        try
        {
            if (jumphostProcesses.TryGetAlive(proxy, JumphostConsoleKind.Access, out var access))
                parts.Add($"служебная: своя, PID {access.ProcessId}");
            else
                parts.Add(accessConsoles.Count > 0
                    ? $"служебная: открыта вне менеджера, PID {accessConsoles[0].Id}"
                    : "служебная: нет");
            if (accessConsoles.Count > 1)
                parts.Add($"лишних служебных: {accessConsoles.Count - 1}");
        }
        finally
        {
            foreach (var item in accessConsoles) item.Dispose();
        }
        return string.Join("; ", parts);
    }

    private async Task<IReadOnlyList<Guid>> DiscoverAccessControlsAsync(
        BaseProxy proxy, Guid? preferredServerId, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(proxy.AccessProbeServerLimit, 1, 20);
        var known = config.AllServers().ToDictionary(server => server.Id);
        var orderedIds = (preferredServerId is null ? [] : new[] { preferredServerId.Value })
            .Concat(proxy.AccessProbeServerIds)
            .Concat(config.Links
                .Where(link => link.LastSuccessUtc is not null &&
                               LinkStatisticsPolicy.ForProxy(link, proxy.Id) is not null)
                .OrderByDescending(link =>
                    LinkStatisticsPolicy.ForProxy(link, proxy.Id)!.LastSuccessUtc)
                .SelectMany(link => new[] { link.ToServerId, link.FromServerId }))
            .Concat(known.Values.OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(server => server.Id))
            .Where(id => known.TryGetValue(id, out var server) &&
                         AccessGrantPolicy.IsEligibleControl(server, proxy))
            .Distinct()
            .Take(limit * 5)
            .ToArray();
        var reachable = new List<Guid>();
        foreach (var batch in orderedIds.Chunk(2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checks = batch.Select(async id => (Id: id, Reachable: await Socks5TcpProbe.CanConnectAsync(
                proxy, known[id], TimeSpan.FromSeconds(2), cancellationToken))).ToArray();
            foreach (var result in await Task.WhenAll(checks))
                if (result.Reachable) reachable.Add(result.Id);
            if (reachable.Count >= limit) break;
        }
        return reachable.Take(limit).ToArray();
    }

    private async Task<IReadOnlyList<Guid>> VerifyAccessControlsAsync(
        BaseProxy proxy, Guid? targetServerId, CancellationToken cancellationToken)
    {
        var candidates = (targetServerId is null ? [] : new[] { targetServerId.Value })
            .Concat(proxy.AccessProbeServerIds.Take(proxy.AccessProbeServerLimit))
            .Distinct()
            .Select(config.FindServer)
            .Where(server => AccessGrantPolicy.IsEligibleControl(server, proxy))
            .Cast<ManagedServer>()
            .ToArray();
        var reachable = new List<Guid>();
        foreach (var server in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await Socks5TcpProbe.CanConnectAsync(
                    proxy, server, TimeSpan.FromSeconds(2), cancellationToken))
                reachable.Add(server.Id);
        }
        return reachable;
    }

    private async Task<bool> AnyAccessControlReachableAsync(
        BaseProxy proxy, CancellationToken cancellationToken)
    {
        foreach (var serverId in proxy.AccessProbeServerIds.Take(proxy.AccessProbeServerLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = config.FindServer(serverId);
            if (server is null) continue;
            Status($"Проверяю контрольный сервер «{server.Name}» через «{proxy.Name}»…");
            if (await Socks5TcpProbe.CanConnectAsync(
                    proxy, server, TimeSpan.FromSeconds(2), cancellationToken))
                return true;
        }
        return false;
    }

    private async Task<bool> ConfirmExistingAccessAsync(
        BaseProxy proxy, CancellationToken cancellationToken, bool rebaseSchedule = false)
    {
        var learnedCount = proxy.AccessProbeServerIds.Take(proxy.AccessProbeServerLimit).Count();
        if (learnedCount == 0) return false;
        Status($"Сначала проверяю сохранённые контрольные серверы через «{proxy.Name}»…");
        var anyReachable = await AnyAccessControlReachableAsync(proxy, cancellationToken);
        if (AccessGrantPolicy.ShouldRunAfterControlPreflight(learnedCount, anyReachable))
        {
            RouteLog($"Access preflight: proxy={proxy.Name}; controls={learnedCount}; reachable=false; script=required");
            return false;
        }
        proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxy.Id) ?? proxy;
        var now = DateTimeOffset.UtcNow;
        if (rebaseSchedule)
        {
            AccessGrantPolicy.RebaseAccessConfirmation(proxy, now);
            SaveConfig();
        }
        else if (AccessGrantPolicy.MarkAccessConfirmedWithoutScript(proxy, now)) SaveConfig();
        RouteLog($"Access script skipped: proxy={proxy.Name}; learned control reachable");
        Status($"Доступ через «{proxy.Name}» уже действует; отдельная консоль скрипта не нужна");
        return true;
    }

    private async void StartJumphost_Click(object sender, RoutedEventArgs e)
    {
        var server = selectedServer ?? SelectedRow()?.Server;
        if (server is null) return;
        await StartManagedJumphostAsync(server);
    }

    private async Task StartManagedJumphostAsync(ManagedServer server)
    {
        var points = config.BaseProxies.Where(proxy => proxy.Enabled && proxy.StartupServerId == server.Id).ToArray();
        if (points.Length == 0) { Warn("Эта сессия не назначена точкой входа."); return; }
        await BusyAsync($"Запускаю точку входа «{server.Name}»…", async cancellationToken =>
        {
            foreach (var proxy in points)
                if (await IsSocks5ReadyAsync(proxy, TimeSpan.FromSeconds(1), cancellationToken))
                { Status($"Точка входа «{server.Name}» уже работает; источник существующего SOCKS5 не подтверждён"); return; }
            if (!await EnsureManagedJumphostAsync(cancellationToken, true, server.Id))
                throw new InvalidOperationException("Не удалось запустить назначенную точку входа.");
        });
    }

    private static async Task<bool> IsTcpPortOpenAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port, linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private static async Task<bool> IsSocks5ReadyAsync(
        BaseProxy proxy, TimeSpan timeout, CancellationToken cancellationToken)
        => await IsSocks5ReadyAsync(proxy.Host, proxy.Port, timeout, cancellationToken);

    private static async Task<bool> IsSocks5ReadyAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port, linked.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, linked.Token);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, linked.Token);
            return response[0] == 0x05 && response[1] == 0x00;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private sealed record SocksProbeResult(bool Success, string Detail);

    private static async Task<SocksProbeResult> ProbeThroughSocksAsync(
        string proxyHost, int proxyPort, Uri destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(proxyHost, proxyPort, linked.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, linked.Token);
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, linked.Token);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                return new(false, $"greeting={Convert.ToHexString(greeting)}");
            await stream.WriteAsync(Socks5ConnectRequest.Build(destination), linked.Token);
            var response = new byte[4]; await stream.ReadExactlyAsync(response, linked.Token);
            var success = response[0] == 0x05 && response[1] == 0x00;
            return new(success, success ? "connected" : $"reply=0x{response[1]:X2}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "timeout"); }
        catch (Exception ex) { return new(false, ex.GetType().Name); }
    }

    private static async Task<SocksProbeResult> ProbeThroughHttpProxyAsync(
        string proxyHost, int proxyPort, Uri destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(proxyHost, proxyPort, linked.Token);
            await using var stream = client.GetStream();
            var authority = destination.Host.Contains(':', StringComparison.Ordinal)
                ? $"[{destination.Host}]:{destination.Port}"
                : $"{destination.Host}:{destination.Port}";
            var request = Encoding.ASCII.GetBytes(
                $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
            await stream.WriteAsync(request, linked.Token);
            var response = new byte[12];
            await stream.ReadExactlyAsync(response, linked.Token);
            var status = Encoding.ASCII.GetString(response);
            return new(status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), status.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "timeout"); }
        catch (Exception ex) { return new(false, ex.GetType().Name); }
    }

    private async void CheckConnectivity_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedRow()?.Server ?? selectedServer; if (source is null) return;
        if (config.BaseProxies.All(proxy => !proxy.Enabled))
        {
            Warn("Нет включённых точек входа. Назначьте сессию jumphost либо добавьте уже запущенный внешний SOCKS5.");
            return;
        }
        var choices = new List<object>();
        choices.AddRange(config.AllGroups().Select(g => new TargetChoice($"{FindGroupPath(config.Groups, g.Id)} — {AllNestedServers(g).Count()} сессий", AllNestedServers(g).Select(s => s.Id).ToArray())));
        choices.AddRange(config.AllServers().Where(s => s.Id != source.Id).Select(s => new TargetChoice($"{s.Name}   {s.Endpoint}", [s.Id])));
        var targets = ChooseMany("Поиск SSH-связей", "Выберите один или несколько серверов/групп", choices).OfType<TargetChoice>().ToList();
        if (targets.Count == 0) return;
        var groupTargets = targets.Where(t => t.Ids.Count > 1).Select(t => t.Ids).ToArray();
        var individualTargets = targets.Where(t => t.Ids.Count == 1).SelectMany(t => t.Ids).ToArray();
        var selectionPlan = ConnectivitySelectionPlanner.Build(
            config, source.Id, groupTargets, individualTargets);
        var targetIds = selectionPlan.PrimaryTargetIds.ToArray();
        var deferredTargetIds = selectionPlan.DeferredTargetIds.ToArray();
        var count = targetIds.Length;
        var targetNames = targetIds.Select(id => config.FindServer(id)?.Name ?? id.ToString()).ToArray();
        var preview = string.Join("\n", targetNames.Take(12).Select(name => $"• {name}"));
        if (targetNames.Length > 12) preview += $"\n…и ещё {targetNames.Length - 12}";
        var deferredNote = deferredTargetIds.Length == 0 ? "" :
            $"\n\nЕщё {deferredTargetIds.Length} серверов чужой группы зависят от указанных выше входных серверов. " +
            "Сначала проверяются только независимые; после этого менеджер предложит долгую дополнительную проверку.";
        if (deferredTargetIds.Length == 0)
            deferredNote = $"\n\nВсе {count} целей относятся к первому этапу; дополнительного вопроса не будет.";
        if (ThemedMessageDialog.Show(this, $"На первом этапе будет проверено связей: {count}. " +
                "Один успех сохраняет связь в обоих направлениях.\n\n" +
                (preview.Length == 0 ? "Независимых целей не найдено." : preview) + deferredNote + "\n\nНачать?",
                "Активная проверка", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RouteLog($"Connectivity phases: source={source.Name}; primary={targetIds.Length}; deferred={deferredTargetIds.Length}");
        if (operationCancellation is not null) { Status("Дождитесь завершения текущей операции или отмените её."); return; }
        using var cts = new CancellationTokenSource();
        operationCancellation = cts;
        var progressWindow = new LinkBuildingProgress
        {
            Owner = this,
            CancelRequested = () =>
            {
                Status("Отменяю операцию…");
                cts.Cancel();
            }
        };
        progressWindow.Show();
        HeaderPanel.IsEnabled = false;
        WorkspacePanel.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        try
        {
            // Запуск точек входа — на UI‑потоке (асинхронно, интерфейс не блокируется),
            // иначе проверка не увидит незапущенный jumphost и не построит связи.
            Status("Запускаю точки входа…");
            progressWindow.UpdateStatus("Запускаю точки входа…", 0, 1);
            await EnsureManagedJumphostAsync(cts.Token, false, startAllAutoStart: true);

            // Сама проверка — в фоне, без блокировки интерфейса (как построение карты связей).
            await Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    Status("Проверяю SSH-связи…");
                    progressWindow.UpdateStatus($"Проверяю связи «{source.Name}» ↔ выбранные…", 0, 1);
                });

                var all = new List<ConnectivityResult>();
                var checkedTargetIds = new List<Guid>();
                async Task<int> CheckTargetsAsync(IReadOnlyList<Guid> ids, int phase, int phaseCount)
                {
                    RouteLog($"Connectivity phase {phase}/{phaseCount} started: source={source.Name}; targets={ids.Count}");
                    var phasePairs = ids.Select(config.FindServer)
                        .Where(candidate => candidate is not null && candidate.Id != source.Id)
                        .Select(candidate => (A: source, B: candidate!)).ToArray();
                    var phaseResults = await ConnectivityBatchExecutor.CheckPairsAsync(
                        phasePairs, CheckConnectivityBatchFromAsync, cts.Token);
                    all.AddRange(phaseResults);
                    foreach (var pair in phasePairs.Where(pair => !phaseResults.Any(result =>
                                 result.Success &&
                                 ((result.SourceId == pair.A.Id && result.TargetId == pair.B.Id) ||
                                  (result.SourceId == pair.B.Id && result.TargetId == pair.A.Id)))))
                        ServerLinkPairPolicy.Invalidate(config, pair.A.Id, pair.B.Id);
                    SaveLinkResults(phaseResults);
                    checkedTargetIds.AddRange(phasePairs.Select(pair => pair.B.Id));
                    var phaseCompleted = phasePairs.Length;
                    var phaseSuccessful = phasePairs.Count(pair => phaseResults.Any(result =>
                        result.Success &&
                        ((result.SourceId == pair.A.Id && result.TargetId == pair.B.Id) ||
                         (result.SourceId == pair.B.Id && result.TargetId == pair.A.Id))));
                    await Dispatcher.InvokeAsync(() => progressWindow.UpdateStatus(
                        ConnectivityPhasePresentation.Progress(phase, phaseCount,
                            phaseCompleted, ids.Count, phaseSuccessful,
                            checkedTargetIds.Count, targetIds.Length + deferredTargetIds.Length),
                        checkedTargetIds.Count, targetIds.Length + deferredTargetIds.Length));
                    RouteLog($"Connectivity phase {phase}/{phaseCount} completed: source={source.Name}; checked={phaseCompleted}; successful={phaseSuccessful}");
                    return phaseSuccessful;
                }

                var phaseCount = deferredTargetIds.Length > 0 ? 2 : 1;
                var primarySuccessful = await CheckTargetsAsync(targetIds, 1, phaseCount);
                if (deferredTargetIds.Length > 0)
                {
                    var deferredNames = deferredTargetIds
                        .Select(id => config.FindServer(id)?.Name ?? id.ToString()).ToArray();
                    var unavailable = targetIds.Where(targetId => !all.Any(result => result.Success &&
                            ((result.SourceId == source.Id && result.TargetId == targetId) ||
                             (result.SourceId == targetId && result.TargetId == source.Id))))
                        .Select(targetId =>
                        {
                            var name = config.FindServer(targetId)?.Name ?? targetId.ToString();
                            var error = all.Where(result =>
                                    result.SourceId == targetId && result.TargetId == source.Id ||
                                    result.SourceId == source.Id && result.TargetId == targetId)
                                .Select(result => result.Message.Replace('\r', ' ').Replace('\n', ' ').Trim())
                                .FirstOrDefault(message => message.Length > 0) ?? "связь не найдена";
                            if (error.Length > 180) error = error[..177] + "…";
                            return $"{name}: {error}";
                        }).ToArray();
                    RouteLog($"Connectivity deferred prompt: source={source.Name}; targets={deferredTargetIds.Length}");
                    var choice = await Dispatcher.InvokeAsync(() => ThemedMessageDialog.ShowChoice(progressWindow,
                        RedactSecrets(ConnectivityPhasePresentation.DeferredPrompt(
                            targetIds.Length, primarySuccessful, unavailable, deferredNames)),
                        "Первый этап завершён",
                        "Завершить", "Проверить зависимые серверы",
                        MessageBoxImage.Question));
                    var proceed = choice == ThemedDialogChoice.Secondary;
                    RouteLog($"Connectivity deferred choice: source={source.Name}; choice={(proceed ? "check-dependent" : "finish")}");
                    if (!proceed)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            EndProgressOperation(cts, progressWindow);
                            Status($"Проверка завершена после первого этапа: доступно {primarySuccessful} из {targetIds.Length}");
                        });
                        return;
                    }
                    _ = await CheckTargetsAsync(deferredTargetIds, 2, phaseCount);
                }

                var successful = checkedTargetIds.Count(targetId =>
                    all.Any(result => result.Success &&
                                      ((result.SourceId == source.Id && result.TargetId == targetId) ||
                                       (result.SourceId == targetId && result.TargetId == source.Id))));
                var details = string.Join(Environment.NewLine, all.Select(result =>
                {
                    var from = config.FindServer(result.SourceId)?.Name ?? result.SourceId.ToString();
                    var to = config.FindServer(result.TargetId)?.Name ?? result.TargetId.ToString();
                    var strategy = result.Success && result.Strategy.Length > 0 ? $" ({result.Strategy})" : "";
                    return $"{(result.Success ? "✓" : "✗")} {from} → {to}: {result.Message}{strategy}";
                }));
                await Dispatcher.InvokeAsync(() =>
                {
                    EndProgressOperation(cts, progressWindow);
                    ThemedMessageDialog.Show(this,
                        $"Проверено связей: {checkedTargetIds.Count}\nДоступно: {successful}\nНедоступно: {checkedTargetIds.Count - successful}" +
                        $"\n\n{RedactSecrets(details)}\n\nУспешные связи сохранены в обоих направлениях и будут использоваться автоматическим маршрутом.",
                        "Результат поиска SSH-связей", MessageBoxButton.OK,
                        successful == checkedTargetIds.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
                });
            });
        }
        catch (OperationCanceledException) { /* пользователь отменил */ }
        catch (Exception ex)
        {
            EndProgressOperation(cts, progressWindow);
            Error(ex);
        }
        finally
        {
            EndProgressOperation(cts, progressWindow);
        }
    }

    private async Task<IReadOnlyList<ConnectivityResult>> CheckConnectivityBatchFromAsync(
        Guid sourceId, IReadOnlyList<Guid> targetIds, CancellationToken cancellationToken)
    {
        try
        {
            return await ssh.CheckFromAsync(config, sourceId, targetIds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var sourceName = config.FindServer(sourceId)?.Name ?? sourceId.ToString();
            return targetIds.Select(targetId => new ConnectivityResult(targetId, false,
                $"{sourceName}: исходный сервер недоступен: {ex.Message}",
                TimeSpan.Zero, "SOURCE_UNREACHABLE", SourceId: sourceId)).ToArray();
        }
    }

    /// <summary>Persists each successful result as one bidirectional link.</summary>
    private void SaveLinkResults(IReadOnlyList<ConnectivityResult> results)
    {
        lock (linkSaveLock)
        {
            foreach (var result in results)
            {
                if (!result.Success) continue;
                ServerLinkPairPolicy.RememberSuccess(config, result, DateTimeOffset.UtcNow);
            }
            SaveConfig();
        }
    }

    private void ShowSavedLinks_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedRow()?.Server ?? selectedServer;
        if (source is null) return;
        var dialog = new SavedLinksDialog(config, source, CheckSavedLinksAsync, SaveConfig,
            async (viaServerId, cancellationToken) =>
            {
                await OpenRoutedConsoleAsync(source, viaServerId, cancellationToken);
            }) { Owner = this };
        _ = dialog.ShowDialog();
    }

    private async Task<IReadOnlyList<ConnectivityResult>> CheckSavedLinksAsync(
        Guid sourceId, IReadOnlyList<Guid> targetIds, CancellationToken cancellationToken)
    {
        await EnsureManagedJumphostAsync(cancellationToken, false);
        var source = config.FindServer(sourceId)
            ?? throw new InvalidOperationException("Исходный сервер связи не найден.");
        var results = await ssh.CheckFromAsync(config, sourceId, targetIds, cancellationToken);
        foreach (var result in results)
        {
            var destination = config.FindServer(result.TargetId);
            Log($"{source.Name} → {destination?.Name}: {(result.Success ? "OK" : "FAIL")} — {result.Message}" +
                (result.Strategy.Length == 0 ? "" : $"; strategy={result.Strategy}"));
            if (result.Success)
                ServerLinkPairPolicy.RememberSuccess(config, result, DateTimeOffset.UtcNow);
            else
                ServerLinkPairPolicy.Invalidate(config, sourceId, result.TargetId);
        }
        SaveConfig();
        return results;
    }

    private void Jumphosts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProxySettingsDialog(
            config.BaseProxies, config.AllServers(), accessPromptCancelledUtc,
            id => config.BaseProxies.FirstOrDefault(proxy => proxy.Id == id),
            id => accessScriptGates.TryGetValue(id, out var gate) && gate.CurrentCount == 0,
            DescribeConsoleState,
            CloseExtraConsolesFor)
            { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var proxyId in dialog.ResetScheduleProxyIds)
            accessPromptCancelledUtc.Remove(proxyId);
        config.BaseProxies = dialog.Proxies; SaveAndRefresh();
        WakeAccessScriptAlarm();
    }

    private void WakeAccessScriptAlarm()
    {
        accessScriptAlarm.Stop();
        RefreshAccessScriptAlarmInterval();
        accessScriptAlarm.Start();
        _ = RunAccessScriptAlarmAsync();
    }

    private void RefreshAccessScriptAlarmInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var next = config.BaseProxies
            .Select(proxy => AccessGrantPolicy.NextEligibleScheduledActionUtc(
                proxy, accessPromptCancelledUtc.GetValueOrDefault(proxy.Id) is { } cancelled &&
                       cancelled != default ? cancelled : null))
            .Where(value => value is not null)
            .Min();
        accessScriptAlarm.Interval = AccessGrantPolicy.NextAlarmPollDelay(
            now, next, TimeSpan.FromSeconds(30));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var loggingWasEnabled = config.EnableLogging;
        var dialog = new TextSettingsDialog(config.KittyPath, config.FirefoxPath, config.FirefoxProfile,
            config.CloseToTray, config.EnableLogging, config.ConnectionTimeoutSeconds,
            config.EndpointProbeTimeoutSeconds,
            config.WriteChangesImmediatelyToKitty, config.CloseWebTunnelWithFirefox,
            config.TemporaryFirefoxProfiles, config.ShareFirefoxProfileByGroup,
            config.FirefoxTemplateProfile, config.AutoConfirmHostKeys,
            config.SuppressKittyChangeNotifications, config.RaceBestEntryPoints,
            config.SkipExistingLinksInMapCheck, config.UseInternalWebResolver) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        config.KittyPath = dialog.KittyPath; config.FirefoxPath = dialog.FirefoxPath;
        config.FirefoxProfile = dialog.Profile; config.CloseToTray = dialog.CloseToTray;
        if (loggingWasEnabled && !dialog.EnableLogging)
            RouteLog("Журналирование отключено пользователем; дальнейшие записи прекращены.");
        config.EnableLogging = dialog.EnableLogging; config.ConnectionTimeoutSeconds = dialog.ConnectionTimeoutSeconds;
        config.EndpointProbeTimeoutSeconds = dialog.EndpointProbeTimeoutSeconds;
        config.WriteChangesImmediatelyToKitty = dialog.WriteChangesImmediatelyToKitty;
        config.CloseWebTunnelWithFirefox = dialog.CloseWebTunnelWithFirefox;
        config.TemporaryFirefoxProfiles = dialog.TemporaryFirefoxProfiles;
        config.ShareFirefoxProfileByGroup = dialog.ShareFirefoxProfileByGroup;
        config.FirefoxTemplateProfile = dialog.TemplateProfile;
        config.AutoConfirmHostKeys = dialog.AutoConfirmHostKeys;
        config.SuppressKittyChangeNotifications = dialog.SuppressKittyChangeNotifications;
        config.RaceBestEntryPoints = dialog.RaceBestEntryPoints;
        config.SkipExistingLinksInMapCheck = dialog.SkipExistingLinksInMapCheck;
        config.UseInternalWebResolver = dialog.UseInternalWebResolver;
        ssh.Timeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds);
        ssh.EndpointProbeTimeout = TimeSpan.FromSeconds(config.EndpointProbeTimeoutSeconds);
        config.ClosePreferenceConfigured = true; SaveConfig(); CleanupUnusedFirefoxProfiles();
        if (!loggingWasEnabled && config.EnableLogging &&
            !TryRouteLog("Журналирование включено пользователем; изменения применены без перезапуска.", out var logError))
            Warn($"Не удалось создать журнал. Проверьте доступ к папке Data\\Logs.\n\n{logError}");
    }

    private string BundledSessionsDirectory => Path.Combine(AppContext.BaseDirectory, "KiTTY", "Sessions");

    private void ApplyKittyProperties(ManagedServer server, IEnumerable<string> properties)
    {
        var requested = properties.Distinct(StringComparer.Ordinal).ToArray();
        var selected = requested.Where(KittySessionWriter.WritableProperties.Contains).ToArray();
        var proxyRequested = requested.Contains(KittyProxyProperty, StringComparer.Ordinal);
        if (selected.Length == 0 && !proxyRequested) return;
        var directProxy = ConfirmedDirectProxy(server);
        if (proxyRequested && directProxy is null)
            throw new InvalidOperationException("Для этой сессии нет подтверждённого прямого маршрута; proxy KiTTY не изменён.");
        KittySessionWriter.Write(BundledSessionsDirectory, server, selected, directProxy);
        foreach (var property in selected) ImportedSessionMerger.MarkManagerApplied(server, property);
        if (directProxy is not null)
            server.ImportedProxy = new ImportedProxy { Method = 2, Host = directProxy.Host, Port = directProxy.Port };
        var proxyResult = directProxy is null ? "preserved (no confirmed direct route)" : $"SOCKS5 {directProxy.Host}:{directProxy.Port}";
        RouteLog($"KiTTY write: session={server.Name}; fields={string.Join(',', selected)}; proxy={proxyResult}");
        if (directProxy is null)
            Status($"{server.Name}: прокси KiTTY сохранён без изменений — подтверждённого прямого маршрута нет.");
    }

    private BaseProxy? ConfirmedDirectProxy(ManagedServer server)
    {
        var route = server.PreferredRoute;
        if (route is null || route.ServerIds.Count != 1 || route.ServerIds[0] != server.Id ||
            route.LastSuccessUtc == default) return null;
        return config.BaseProxies.FirstOrDefault(proxy => proxy.Enabled && proxy.Id == route.ProxyId);
    }

    private void ShowKittyChanges()
    {
        try
        {
            pendingKittyConflicts.Clear();
            if (Directory.Exists(BundledSessionsDirectory)) _ = ImportOrUpdateSessions(BundledSessionsDirectory);
            var conflicts = pendingKittyConflicts
                .Where(conflict => KittySessionWriter.WritableProperties.Contains(conflict.PropertyName))
                .ToDictionary(conflict => (conflict.Server.Id, conflict.PropertyName));
            var rows = new List<KittyChangeRow>();
            foreach (var server in config.AllServers())
            foreach (var property in KittySessionWriter.WritableProperties)
            {
                conflicts.TryGetValue((server.Id, property), out var conflict);
                var baseline = ImportedSessionMerger.BaselineValue(server, property);
                var manager = ImportedSessionMerger.CurrentValue(server, property);
                var managerPending = server.ManagerOverrides.Contains(property, StringComparer.Ordinal) && baseline != manager;
                if (conflict is null && !managerPending) continue;
                var kitty = conflict?.KittyValue ?? baseline ?? "";
                if (KittyChangeIgnore.Matches(server, property, manager, kitty)) continue;
                rows.Add(new KittyChangeRow
                {
                    ServerId = server.Id, PropertyName = property, SessionName = server.Name,
                    FieldName = ImportedSessionMerger.DisplayName(property),
                    Status = conflict is null ? "Изменено в менеджере" : managerPending ? "Конфликт" : "KiTTY изменена",
                    ManagerDisplay = DetailedValue(manager),
                    KittyDisplay = DetailedValue(kitty), ManagerValue = manager, KittyValue = kitty
                });
            }
            foreach (var server in config.AllServers())
            {
                var directProxy = ConfirmedDirectProxy(server);
                if (directProxy is not null &&
                    (server.ImportedProxy is not { Method: 2 } imported || imported.Port != directProxy.Port ||
                     !ProxyEndpointComparer.HostsEquivalent(imported.Host, directProxy.Host)))
                {
                    var managerValue = ProxyValue(directProxy.Host, directProxy.Port);
                    var kittyValue = server.ImportedProxy is null ? "" : ProxyValue(server.ImportedProxy.Host, server.ImportedProxy.Port);
                    if (!KittyChangeIgnore.Matches(server, KittyProxyProperty, managerValue, kittyValue)) rows.Add(new KittyChangeRow
                    {
                        ServerId = server.Id, PropertyName = KittyProxyProperty, SessionName = server.Name,
                        FieldName = "Прокси SOCKS5", Status = "Будет записан вместе с изменениями",
                        ManagerDisplay = $"{(server.ImportedProxy is { } current && ProxyEndpointComparer.HostsEquivalent(current.Host, directProxy.Host) ? current.Host : directProxy.Host)}:{directProxy.Port}",
                        KittyDisplay = server.ImportedProxy is null ? "(не задан)" : $"{server.ImportedProxy.Host}:{server.ImportedProxy.Port}",
                        ManagerValue = managerValue, KittyValue = kittyValue,
                        CanSelect = true
                    });
                }
            }
            if (rows.Count == 0)
            {
                ThemedMessageDialog.Show(this, "Неприменённых изменений нет.", "Изменения KiTTY", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new KittyChangesDialog(rows) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Action == KittyChangesAction.None) return;
            if (dialog.Action == KittyChangesAction.Ignore)
            {
                foreach (var row in dialog.SelectedRows)
                    if (config.FindServer(row.ServerId) is { } server)
                        KittyChangeIgnore.Remember(server, row.PropertyName, row.ManagerValue, row.KittyValue);
                SaveAndRefresh();
            }
            else if (dialog.Action == KittyChangesAction.Apply)
            {
                var applyErrors = new List<string>();
                foreach (var group in dialog.SelectedRows.GroupBy(row => row.ServerId))
                {
                    var server = config.FindServer(group.Key);
                    if (server is null) continue;
                    try
                    {
                        ApplyKittyProperties(server, group.Select(row => row.PropertyName));
                    }
                    catch (Exception ex)
                    {
                        applyErrors.Add($"{server.Name}: {ex.Message}");
                    }
                }
                SaveAndRefresh();
                if (applyErrors.Count > 0)
                {
                    var errorText = string.Join("\n", applyErrors.Take(10));
                    if (applyErrors.Count > 10) errorText += $"\n…и ещё {applyErrors.Count - 10}";
                    ThemedMessageDialog.Show(this,
                        $"Применено с ошибками ({applyErrors.Count}):\n\n{errorText}",
                        "Изменения KiTTY", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                foreach (var row in dialog.SelectedRows)
                {
                    if (row.PropertyName == KittyProxyProperty) continue;
                    if (conflicts.TryGetValue((row.ServerId, row.PropertyName), out var conflict))
                        ImportedSessionMerger.Resolve(conflict, KittyConflictChoice.Kitty);
                    else if (config.FindServer(row.ServerId) is { } server)
                        ImportedSessionMerger.RejectManagerChange(server, row.PropertyName);
                }
                SaveAndRefresh();
            }
            Status($"Обработано изменений KiTTY: {dialog.SelectedRows.Count}");
        }
        catch (Exception ex) { Error(ex); }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var export = new MenuItem { Header = "Экспорт JSON…" }; export.Click += Export_Click;
        var import = new MenuItem { Header = "Импорт JSON…" }; import.Click += ImportConfig_Click;
        var logs = new MenuItem { Header = "Открыть папку журналов…" }; logs.Click += (_, _) => OpenLogsDirectory();
        var changes = new MenuItem { Header = "Изменения KiTTY…" }; changes.Click += (_, _) => ShowKittyChanges();
        var resetIgnored = new MenuItem { Header = "Снова показывать игнорируемые изменения KiTTY" };
        resetIgnored.Click += (_, _) => ResetIgnoredKittyChanges();
        var linkMap = new MenuItem { Header = "Карта связей…" }; linkMap.Click += (_, _) => ShowLinkMap();
        var help = new MenuItem { Header = "Справка по полям…" }; help.Click += (_, _) => _ = new HelpDialog { Owner = this }.ShowDialog();
        var exit = new MenuItem { Header = "Выйти" }; exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(changes); menu.Items.Add(resetIgnored); menu.Items.Add(linkMap); menu.Items.Add(new Separator()); menu.Items.Add(export); menu.Items.Add(import);
        menu.Items.Add(logs); menu.Items.Add(help); menu.Items.Add(exit); menu.PlacementTarget = sender as UIElement; menu.IsOpen = true;
    }

    private void ShowLinkMap()
    {
        if (linkMapWindow is not null)
        {
            linkMapWindow.Activate();
            return;
        }
        linkMapWindow = new LinkMapWindow(config, SelectServerFromLinkMap, CheckLinksFromMapAsync) { Owner = this };
        linkMapWindow.Closed += (_, _) => linkMapWindow = null;
        linkMapWindow.Show();
    }

    private async Task CheckLinksFromMapAsync(IReadOnlyList<Guid> serverIds)
    {
        var servers = ConnectivityBatchPlanner.SelectedServers(config, serverIds);
        await BuildLinksForServersAsync(
            "Выбранные на карте сессии",
            servers,
            config.SkipExistingLinksInMapCheck);
    }

    private void SelectServerFromLinkMap(Guid serverId)
    {
        var server = config.FindServer(serverId);
        if (server is null) return;
        selectedGroup = null;
        ClearTreeSelection(GroupTree.Items);
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        selectedServer = server;
        RefreshSessions();
        var row = (SessionsGrid.ItemsSource as IEnumerable<SessionRow>)?
            .FirstOrDefault(item => item.Server.Id == serverId);
        if (row is not null)
        {
            SessionsGrid.SelectedItem = row;
            SessionsGrid.ScrollIntoView(row);
        }
        ShowServer(server);
        Activate();
        Focus();
        Status($"Выбрана сессия «{server.Name}»");
    }

    private void ResetIgnoredKittyChanges()
    {
        var count = config.AllServers().Sum(server => server.IgnoredKittyChanges.Count);
        if (count == 0) { Status("Игнорируемых изменений KiTTY нет"); return; }
        if (ThemedMessageDialog.Show(this, $"Снова показывать все игнорируемые изменения ({count})?",
                "Изменения KiTTY", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var server in config.AllServers()) server.IgnoredKittyChanges.Clear();
        SaveAndRefresh();
    }

    private void OpenLogsDirectory()
    {
        var directory = Path.GetDirectoryName(CurrentRouteLogPath)!; Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(directory)) { UseShellExecute = true });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var selection = new ExportSelectionDialog(config) { Owner = this };
        if (selection.ShowDialog() != true) return;
        if (ThemedMessageDialog.Show(this,
                "Экспорт содержит логины, пароли, ключи TOTP и другие секреты в открытом виде. Продолжить?",
                "Открытые секреты", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "kitty-manager-export.json" };
        if (dialog.ShowDialog() != true) return;
        var exportConfig = ConfigTransfer.CreateExport(config, selection.SelectedServerIds, selection.IncludeEntryPoints);
        ConfigStore.Export(dialog.FileName, exportConfig);
        Status($"Экспортировано сессий: {exportConfig.AllServers().Count()}");
    }
    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" }; if (dialog.ShowDialog() != true) return;
        try
        {
            var incoming = ConfigStore.Import(dialog.FileName);
            var conflicts = ConfigTransfer.FindConflicts(config, incoming);
            IReadOnlyDictionary<(TransferConflictKind Kind, Guid IncomingId), bool> decisions =
                new Dictionary<(TransferConflictKind, Guid), bool>();
            if (conflicts.Count > 0)
            {
                var conflictDialog = new ImportConflictsDialog(conflicts) { Owner = this };
                if (conflictDialog.ShowDialog() != true) return;
                decisions = conflictDialog.Decisions;
            }
            else if (ThemedMessageDialog.Show(this,
                         $"Добавить {incoming.AllServers().Count()} сессий и {incoming.BaseProxies.Count} точек входа?",
                         "Импорт", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            config = ConfigTransfer.Merge(config, incoming, decisions);
            ssh.Timeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds);
            ssh.EndpointProbeTimeout = TimeSpan.FromSeconds(config.EndpointProbeTimeoutSeconds);
            var selectedId = selectedServer?.Id;
            SaveAndRefresh();
            if (selectedId is Guid sid)
            {
                var refreshed = config.FindServer(sid);
                if (refreshed is not null) ShowServer(refreshed);
            }
            Status($"Импорт завершён. Сессий: {config.AllServers().Count()}, точек входа: {config.BaseProxies.Count}");

            var missingTotp = ConfigTransfer.FindProxiesMissingTotp(incoming);
            if (missingTotp.Count > 0)
            {
                var proxyList = string.Join("\n", missingTotp.Select(name => $"• {name}"));
                ThemedMessageDialog.Show(this,
                    $"Импортированы точки входа без TOTP-секрета:\n\n{proxyList}\n\n" +
                    "Для автоматической подстановки OTP добавьте свой TOTP-секрет в настройках каждой точки входа.",
                    "Импорт: требуется настройка OTP", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { Error(ex); }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!forceExit && config.CloseToTray)
        {
            e.Cancel = true; Hide(); EnsureTrayIcon(); trayIcon!.Visible = true; trayIcon.ShowBalloonTip(1500, "KiTTY Manager", "Приложение продолжает работать, активные туннели сохранены.", System.Windows.Forms.ToolTipIcon.Info); return;
        }
        Cleanup();
    }
    private void EnsureTrayIcon()
    {
        if (trayIcon is not null) return;
        var appIcon = Environment.ProcessPath is { } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
            : null;
        trayIcon = new System.Windows.Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "KiTTY Manager", Visible = true };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip(); menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray()); menu.Items.Add("Выйти", null, (_, _) => Dispatcher.Invoke(ExitApplication)); trayIcon.ContextMenuStrip = menu;
    }
    private void RestoreFromTray() { Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); if (trayIcon is not null) trayIcon.Visible = false; }); }
    private void ExitApplication() { forceExit = true; Close(); }
    private void Cleanup()
    {
        operationCancellation?.Cancel();
        backgroundRouteProbes.CancelAll();
        accessScriptAlarm.Stop();
        accessScriptAlarmCancellation.Cancel();
        accessScriptAlarmCancellation.Dispose();
        jumphostProcesses.Clear();
        jumphostStartupGates.Clear();
        accessScriptGates.Clear();
        try { SaveConfig(); } catch { }
        foreach (var route in activeRoutes) route.Dispose();
        if (trayIcon is not null) { trayIcon.Visible = false; trayIcon.Dispose(); }
    }

    private async Task BusyAsync(
        string text, Func<CancellationToken, Task> action,
        CancellationToken externalCancellationToken = default)
    {
        if (operationCancellation is not null) { Status("Дождитесь завершения текущей операции или отмените её."); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellationToken);
        operationCancellation = cancellation;
        HeaderPanel.IsEnabled = false;
        WorkspacePanel.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        Status(text);
        try
        {
            await action(cancellation.Token);
            Status("Готово");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Status("Операция отменена"); }
        catch (Exception ex) { Error(ex); }
        finally
        {
            if (ReferenceEquals(operationCancellation, cancellation)) operationCancellation = null;
            HeaderPanel.IsEnabled = true;
            WorkspacePanel.IsEnabled = true;
            CancelOperationButton.Visibility = Visibility.Collapsed;
        }
    }
    private void EndProgressOperation(CancellationTokenSource cancellation, LinkBuildingProgress progressWindow)
    {
        if (ReferenceEquals(operationCancellation, cancellation)) operationCancellation = null;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        HeaderPanel.IsEnabled = true;
        WorkspacePanel.IsEnabled = true;
        if (progressWindow.IsLoaded) progressWindow.CloseCompleted();
    }
    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (operationCancellation is null) return;
        CancelOperationButton.IsEnabled = false;
        Status("Отменяю операцию…");
        operationCancellation.Cancel();
    }
    private void SaveAndRefresh() { SaveConfig(); CleanupUnusedFirefoxProfiles(); var group = selectedGroup; RefreshAll(); selectedGroup = group; RefreshSessions(); }
    private void SaveConfig()
    {
        if (configAvailable) ConfigStore.Save(configPath, config);
    }
    private SessionRow? SelectedRow() => SessionsGrid.SelectedItem as SessionRow;
    private string? Prompt(string title, string text, string initial = "") { var dialog = new PromptDialog(title, text, initial) { Owner = this }; return dialog.ShowDialog() == true ? dialog.Value : null; }
    private IReadOnlyList<object> ChooseMany(string title, string prompt, IEnumerable<object> values) { var list = values.ToList(); if (list.Count == 0) { Warn("Нет доступных вариантов."); return []; } var dialog = new SearchChoiceDialog(title, prompt, list) { Owner = this }; return dialog.ShowDialog() == true ? dialog.Selections : []; }
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject { while (child is not null) { if (child is T result) return result; child = VisualTreeHelper.GetParent(child); } return null; }
    private string ResolveProgram(string configured) => TryResolveProgram(configured) ?? throw new FileNotFoundException($"Программа не найдена: {configured}. Укажите полный путь в настройках.");
    private static string? TryResolveProgram(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
        var local = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)); if (File.Exists(local)) return local;
        if (!configured.Contains(Path.DirectorySeparatorChar) && !configured.Contains(Path.AltDirectorySeparatorChar))
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) { var candidate = Path.Combine(directory.Trim('"'), configured); if (File.Exists(candidate)) return candidate; }
            if (configured.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)) foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }) { var candidate = Path.Combine(root, "Mozilla Firefox", "firefox.exe"); if (File.Exists(candidate)) return candidate; }
        }
        return null;
    }
    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';
    private static string SafeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private string FirefoxRuntimeRoot => Path.Combine(dataDirectory, "FirefoxProfiles",
        SafeFileName(config.FirefoxProfile), "runtime");
    private string FirefoxPersistentRoot => Path.Combine(dataDirectory, "FirefoxProfiles",
        SafeFileName(config.FirefoxProfile), "persistent");

    private string FirefoxProfileKey(ManagedServer server)
    {
        var group = config.ShareFirefoxProfileByGroup ? config.FindServerGroup(server.Id) : null;
        return group is null ? $"session-{server.Id:N}" : $"group-{group.Id:N}";
    }

    private void CleanupOrphanedFirefoxProfiles()
    {
        if (Directory.Exists(FirefoxRuntimeRoot))
            foreach (var path in Directory.EnumerateDirectories(FirefoxRuntimeRoot)) TryDeleteDirectory(path);
        CleanupUnusedFirefoxProfiles();
    }

    private void CleanupUnusedFirefoxProfiles()
    {
        if (!Directory.Exists(FirefoxPersistentRoot)) return;
        var valid = config.AllServers().Select(FirefoxProfileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateDirectories(FirefoxPersistentRoot))
            if (!valid.Contains(Path.GetFileName(path))) TryDeleteDirectory(path);
    }

    private async Task CleanupWebSessionAfterExitAsync(
        Process browser, Process tunnel, ActiveRoute route, ResolvingHttpProxy? resolver,
        string profile, bool deleteProfile)
    {
        var browserPid = browser.Id;
        RouteLog($"Web cleanup: polling Firefox pid={browserPid}; closeTunnel={config.CloseWebTunnelWithFirefox}");
        try { browser.Dispose(); } catch { }
        // WaitForExitAsync hangs on Windows with Firefox multi-process model.
        // Poll GetProcessById instead — throws ArgumentException when process is gone.
        while (true)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                using var probe = Process.GetProcessById(browserPid);
                if (probe.HasExited) break;
            }
            catch (ArgumentException) { break; }
            catch (InvalidOperationException) { break; }
        }

        RouteLog($"Web cleanup: Firefox pid={browserPid} exited; closeTunnel={config.CloseWebTunnelWithFirefox}");
        if (resolver is not null) await resolver.DisposeAsync().ConfigureAwait(false);
        if (config.CloseWebTunnelWithFirefox)
        {
            RouteLog($"Web cleanup: stopping tunnel pid={tunnel.Id}");
            StopProcess(tunnel);
            try { await Dispatcher.InvokeAsync(() => ReleaseRoute(route)); } catch { }
            RouteLog("Web cleanup: tunnel stopped and route released");
        }
        else
        {
            tunnel.Dispose();
            RouteLog("Web KiTTY tunnel left open after Firefox exit; close-after-firefox setting is disabled");
        }
        if (!deleteProfile) return;
        for (var attempt = 0; attempt < 40 && Directory.Exists(profile); attempt++)
        {
            if (TryDeleteDirectory(profile)) return;
            await Task.Delay(250);
        }
    }

    private void ReleaseRoute(ActiveRoute route)
    {
        activeRoutes.Remove(route);
        route.Dispose();
    }

    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch { }
        finally { process.Dispose(); }
    }

    private static int SelectFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener.Stop(); }
    }

    private static async Task<Uri> ResolveWebDestinationAsync(Uri destination, CancellationToken cancellationToken)
    {
        if (System.Net.IPAddress.TryParse(destination.Host, out _)) return destination;
        System.Net.IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(destination.DnsSafeHost, cancellationToken);
        }
        catch (System.Net.Sockets.SocketException ex) when (ActionableErrorFormatter.IsUnknownHost(ex))
        {
            throw new HostResolutionException(destination.DnsSafeHost, ex);
        }
        var address = addresses.FirstOrDefault(value => value.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault()
                      ?? throw new IOException($"Windows не смогла разрешить DNS-имя {destination.DnsSafeHost}.");
        var builder = new UriBuilder(destination) { Host = address.ToString() };
        return builder.Uri;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private bool ConfirmHostKey(ManagedServer server, string fingerprint)
    {
        if (config.AutoConfirmHostKeys) return true;
        return Dispatcher.Invoke(() => ThemedMessageDialog.Show(this, $"Первое подключение к {server.Name} ({server.Endpoint}).\n\nHost key SHA256:\n{fingerprint}\n\nСверьте отпечаток с администратором. Доверять?", "Неизвестный SSH host key", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    }
    private bool ConfirmChangedHostKey(ManagedServer server, string oldFingerprint, string newFingerprint)
    {
        if (config.AutoConfirmHostKeys) return true;
        return Dispatcher.Invoke(() => ThemedMessageDialog.Show(this,
            $"SSH-ключ сервера {server.Name} ({server.Endpoint}) изменился.\n\n" +
            $"Сохранённый:\n{oldFingerprint}\n\nНовый:\n{newFingerprint}\n\n" +
            "Это может означать переустановку сервера или подмену. Сверьте новый SHA256 с администратором. Заменить ключ после проверки?",
            "Изменился SSH host key", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    }
    private void Log(string text) { RouteLog(text); Status(text); }
    private void RouteLog(string text)
    {
        if (!config.EnableLogging) return;
        _ = TryRouteLog(text, out _);
    }
    private bool TryRouteLog(string text, out string? error) =>
        RuntimeLogWriter.TryAppend(CurrentRouteLogPath, RedactSecrets(text), out error);
    private void Status(string text) => StatusText.Text = text;
    private static string FormatSshStatus(SshTraceEvent traceEvent)
    {
        var stage = traceEvent.Stage switch
        {
            SshTraceStage.RouteCandidate => "Проверка маршрута",
            SshTraceStage.ProxyConnect => "Подключение через точку входа",
            SshTraceStage.SourceAuthentication => "Вход на исходный сервер",
            SshTraceStage.ChannelForward => "Проверка перехода к серверу",
            SshTraceStage.RemoteCommandFallback => "Резервный переход через команду",
            SshTraceStage.PrivilegedCommandFallback => "Переход с повышением прав",
            SshTraceStage.TargetAuthentication => "Вход на целевой сервер",
            SshTraceStage.HostKey => "Проверка ключа сервера",
            SshTraceStage.RouteReady => "Маршрут готов",
            _ => "SSH"
        };
        var state = traceEvent.Status switch
        {
            var value when value.StartsWith("START", StringComparison.Ordinal) => "начато",
            "PASS" => "успешно",
            "FAIL" => "ошибка",
            "DEFERRED" => "пробую другой способ",
            "MATCH" => "ключ подтверждён",
            "ACCEPTED_NEW" => "новый ключ принят",
            "REJECTED" or "REJECTED_NEW" => "ключ отклонён",
            "CONSOLE_READY" => "консоль готова",
            "DYNAMIC_SOCKS_UNVERIFIED" => "SOCKS-туннель создан",
            "TIMEOUT" => "превышен таймаут",
            "CANCELLED" => "отменено",
            _ => traceEvent.Status
        };
        return $"{stage}: {traceEvent.Subject} — {state}";
    }
    private void Warn(string text) => ThemedMessageDialog.Show(this, text, "KiTTY Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Error(Exception ex)
    {
        var details = RedactSecrets(string.Join(" --> ", ExceptionChain(ex)
            .Select(item => $"{item.GetType().Name}: {item.Message}")));
        var logPath = CurrentRouteLogPath;
        string? logError = null;
        var logged = config.EnableLogging &&
                     RuntimeLogWriter.TryAppend(logPath, RedactSecrets("ERROR " + details), out logError);
        var hint = logged
            ? $"Подробности: Data\\Logs\\{Path.GetFileName(logPath)}"
            : config.EnableLogging
                ? $"Причина: {details}\n\nНе удалось записать журнал: {logError}"
                : $"Причина: {details}\n\nЖурнал отключён в настройках.";
        ThemedMessageDialog.Show(this, $"{ActionableErrorFormatter.Format(ex)}\n\n{hint}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        Status("Ошибка");
    }

    private string RedactSecrets(string text)
    {
        var result = text;
        foreach (var secret in config.AllServers()
                     .SelectMany(server => new[] { server.Password, server.PrivateKeyPassphrase, server.RootPassword }
                         .Concat(server.WebInterfaces.Select(web => web.Password)))
                     .Where(secret => !string.IsNullOrEmpty(secret))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(secret => secret.Length))
            result = result.Replace(secret, "[скрыто]", StringComparison.Ordinal);
        return result;
    }
    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var nested in ExceptionChain(inner))
                    yield return nested;
        }
        else if (exception.InnerException is not null)
        {
            foreach (var nested in ExceptionChain(exception.InnerException))
                yield return nested;
        }
    }

    private sealed record SessionRow(ManagedServer Server, string Group) { public string Name => Server.Name; public string Endpoint => Server.Endpoint; }
    private sealed record RequiredRouteOption(Guid? Id, string Name, string SearchText = "")
    {
        public override string ToString() => Name;
    }
    private sealed record TargetChoice(string Label, IReadOnlyList<Guid> Ids) { public override string ToString() => Label; }
}

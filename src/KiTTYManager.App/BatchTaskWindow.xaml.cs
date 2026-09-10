using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Text.Json;
using System.Text;
using KiTTYManager.Core;
using Microsoft.Win32;
using Renci.SshNet;
using MessageBox = KiTTYManager.App.ThemedMessageDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace KiTTYManager.App;

public partial class BatchTaskWindow : Window
{
    private readonly ManagerConfig config;
    private readonly ObservableCollection<BatchServerChoice> servers = [];
    private readonly BatchTaskRunner runner;
    private readonly SshConnectionService connections;
    private readonly Func<Guid, CancellationToken, Task<ActiveRoute>>? routeFactory;
    private readonly Action<ActiveRoute>? routeRelease;
    private readonly ObservableCollection<BatchTaskStep> steps = [];
    private readonly ObservableCollection<BatchTunnelDefinition> tunnels = [];
    private readonly ObservableCollection<BatchTunnelDefinition> ansibleTunnels = [];
    private readonly List<BatchTaskLog> logs = [];
    private readonly List<AnsibleLogEntry> ansibleLogs = [];
    private readonly Dictionary<Guid, List<WpfTextBox>> detachedLogs = [];
    private string taskDirectory;
    private CancellationTokenSource? cancellation;
    private readonly Action? saveConfig;
    private bool runActive;
    private bool closeAfterStop;
    private string savedTaskState = "";
    private TaskCompletionSource<bool>? runCompletion;
    private readonly HashSet<string> ownedTaskDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? fileLog;
    private string? loadedTemplatePath;
    private int unlockedWizardStage;
    private BatchTaskStep? draggedStep;
    private BatchTaskStep? pendingClickStep;
    private bool isStepDragging;
    private int lastStepAnchorIndex = -1;
    private System.Windows.Point dragStart;
    private readonly ServerSelectionList constructorServerSelector;
    private int ansibleUnlockedStage;
    private bool syncingServerSelectors;
    private HashSet<Guid> constructorSelectedIds = [];
    private HashSet<Guid> ansibleSelectedIds = [];
    private readonly SemaphoreSlim connectionPromptGate = new(1, 1);

    public BatchTaskWindow(ManagerConfig config, IEnumerable<Guid> serverIds, SshConnectionService? connections = null,
        Func<Guid, CancellationToken, Task<ActiveRoute>>? routeFactory = null,
        Action<ActiveRoute>? routeRelease = null, Action? saveConfig = null, Action<string>? fileLog = null)
    {
        this.fileLog = fileLog;
        InitializeComponent();
        BatchTaskTemplateStore.MigrateLegacyDirectory(AppContext.BaseDirectory);
        this.config = config;
        var initiallySelected = serverIds.ToHashSet();
        constructorSelectedIds = initiallySelected.ToHashSet();
        ansibleSelectedIds = initiallySelected.ToHashSet();
        foreach (var server in config.AllServers().OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            servers.Add(new(server.Id, server.Name, $"{server.CleanHost}:{server.Port}", initiallySelected.Contains(server.Id)));
        this.saveConfig = saveConfig;
        this.connections = connections ?? new SshConnectionService();
        this.routeFactory = routeFactory; this.routeRelease = routeRelease;
        runner = new(this.connections, routeFactory, routeRelease);
        runner.RecoveryTimeout = PromptConnectionRecoveryAsync;
        runner.ConfirmAmbiguousRetry = PromptAmbiguousRetryAsync;
        taskDirectory = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        ownedTaskDirectories.Add(taskDirectory);
        TaskNameBox.Text = "Новая задача";
        StepsGrid.ItemsSource = steps;
        TunnelsGrid.ItemsSource = tunnels;
        AnsibleTunnelsGrid.ItemsSource = ansibleTunnels;
        ServersGrid.ItemsSource = servers;
        AnsibleDownloadFolderBox.Text = AnsibleRunPolicy.DefaultDownloadDirectory(AppContext.BaseDirectory);
        constructorServerSelector = new ServerSelectionList();
        ServerSelector.Content = constructorServerSelector;
        constructorServerSelector.Configure(config, initiallySelected);
        constructorServerSelector.SelectionChanged += (_, _) =>
        {
            if (syncingServerSelectors) return;
            constructorSelectedIds = constructorServerSelector.SelectedIds.ToHashSet();
            foreach (var server in servers) server.Selected = constructorServerSelector.SelectedIds.Contains(server.Id);
            RefreshSelectedServers();
        };
        AnsibleServerSelector.Configure(config, initiallySelected);
        AnsibleServerSelector.SelectionChanged += (_, _) =>
        {
            if (syncingServerSelectors) return;
            ansibleSelectedIds = AnsibleServerSelector.SelectedIds.ToHashSet();
            foreach (var server in servers) server.Selected = AnsibleServerSelector.SelectedIds.Contains(server.Id);
            RefreshSelectedServers();
            RefreshAnsibleSelectedServers();
            UpdateAnsibleNavigation();
        };
        ServerSearchBox.Parent.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        if (ServerSearchBox.Parent is Grid searchRow && searchRow.Parent is StackPanel header &&
            header.Children.OfType<WrapPanel>().FirstOrDefault() is { } oldButtons)
            oldButtons.Visibility = Visibility.Collapsed;
        var gridCheckBoxStyle = (Style)FindResource("DataGridCheckBoxStyle");
        foreach (var column in ServersGrid.Columns.OfType<DataGridCheckBoxColumn>())
        {
            column.ElementStyle = gridCheckBoxStyle;
            column.EditingElementStyle = gridCheckBoxStyle;
        }
        RefreshSelectedServers();
        RefreshAnsibleSelectedServers();
        UpdateLogConfigButtonText();
        UpdateAnsibleLogConfigButtonText();
        UpdateSelectAllStepsCheck();
        Closing += BatchTaskWindow_Closing;
        Closed += (_, _) => CleanupOwnedTaskDirectories();
        runner.Log += item => Dispatcher.Invoke(() =>
        {
            logs.Add(item);
            fileLog?.Invoke("BATCH " + BatchLogFormatter.Format(item).TrimEnd());
            var choice = servers.FirstOrDefault(x => x.Id == item.ServerId);
            if (choice is not null) choice.Status = StatusFrom(item);
            ServersGrid.Items.Refresh();
            UpdateStatusCounts();
            RenderLog();
            if (detachedLogs.TryGetValue(item.ServerId, out var boxes))
                foreach (var box in boxes.ToArray()) { box.AppendText(FormatLog(item)); box.ScrollToEnd(); }
        });
        runner.Progress += (done, total) => Dispatcher.Invoke(() => { Progress.Maximum = total; Progress.Value = done; });
        var actions = ((Grid)StepsGrid.Parent).Children.OfType<StackPanel>().Single()
            .Children.OfType<WrapPanel>().Single();
        var duplicate = new System.Windows.Controls.Button { Content = "Дублировать шаг", Margin = new Thickness(0, 0, 8, 0) };
        duplicate.Click += DuplicateStep_Click;
        actions.Children.Insert(2, duplicate);
        UpdateWizardNavigation();
        RememberSavedState();
    }

    private async Task<int?> PromptConnectionRecoveryAsync(ConnectionRecoveryPrompt prompt, CancellationToken token)
    {
        await connectionPromptGate.WaitAsync(token);
        try
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                var dialog = new ConnectionRecoveryDialog(prompt.ServerName, prompt.Operation, prompt.PreviousMinutes) { Owner = this };
                using var registration = token.Register(() => Dispatcher.BeginInvoke(() => dialog.Close()));
                var result = dialog.ShowDialog() == true ? dialog.AdditionalMinutes : null;
                token.ThrowIfCancellationRequested();
                return result;
            });
        }
        finally { connectionPromptGate.Release(); }
    }

    private async Task<bool> PromptAmbiguousRetryAsync(AmbiguousActionRetryPrompt prompt, CancellationToken token)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            return MessageBox.Show(this,
                $"Связь с «{prompt.ServerName}» восстановлена, но менеджер не может доказать, завершилось ли действие «{prompt.Operation}» на сервере.\n\n" +
                "Повторить действие? Для неидемпотентной команды повтор может выполнить изменения второй раз. «Нет» остановит выполнение на этом сервере.",
                "Повтор действия после обрыва", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        });
    }

    private BatchTaskDefinition ReadTask()
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TunnelsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TunnelsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return new()
        {
            Name = TaskNameBox.Text.Trim(), Steps = steps.Select(CloneStep).ToList(),
            Tunnels = tunnels.Select(CloneTunnel).ToList(),
            AutoReconnectTunnels = AutoReconnectBox.IsChecked == true,
            KeepTunnelsAfterSteps = KeepTunnelsBox.IsChecked == true,
            WorkingDirectory = WorkingDirectoryBox.Text.Trim(),
            PrivilegeMode = Enum.Parse<BatchPrivilegeMode>(((ComboBoxItem)PrivilegeModeBox.SelectedItem).Tag.ToString()!),
            ServerIds = constructorSelectedIds.ToList()
        };
    }

    private void LoadTask(string manifest)
    {
        var task = BatchTaskFile.Load(manifest);
        taskDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        BatchTaskFile.ResolveImportedLocalSources(task, taskDirectory);
        TaskNameBox.Text = task.Name;
        steps.Clear();
        foreach (var step in task.Steps) steps.Add(step);
        tunnels.Clear();
        foreach (var tunnel in task.Tunnels)
        {
            UpdateServerDisplay(tunnel);
            tunnels.Add(tunnel);
        }
        AutoReconnectBox.IsChecked = task.AutoReconnectTunnels;
        KeepTunnelsBox.IsChecked = task.KeepTunnelsAfterSteps;
        WorkingDirectoryBox.Text = task.WorkingDirectory;
        PrivilegeModeBox.SelectedIndex = task.PrivilegeMode == BatchPrivilegeMode.AlwaysBecome ? 1 : 0;
        // Загруженная задача уже готова: разрешаем свободно ходить по всем шагам
        // и восстанавливаем серверы, которые были выбраны при сохранении.
        var known = task.ServerIds.Where(id => config.FindServer(id) is not null).ToHashSet();
        constructorSelectedIds = known.ToHashSet();
        foreach (var server in servers) server.Selected = known.Contains(server.Id);
        constructorServerSelector.SetSelected(known);
        RefreshSelectedServers();
        unlockedWizardStage = 4;
        WizardTabs.SelectedIndex = 1;
        UpdateWizardNavigation();
        RememberSavedState();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnsibleMode)
        {
            if (AnsibleWizardTabs.SelectedIndex > 0) AnsibleWizardTabs.SelectedIndex--;
            UpdateAnsibleNavigation();
            return;
        }
        if (WizardTabs.SelectedIndex > 0) WizardTabs.SelectedIndex--;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnsibleMode)
        {
            if (AnsibleWizardTabs.SelectedIndex == 0 && !File.Exists(AnsiblePlaybookBox.Text.Trim()))
            { MessageBox.Show(this, "Выберите существующий playbook.", "Шаг 1", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (AnsibleWizardTabs.SelectedIndex == 1 && AnsibleServerSelector.SelectedIds.Count == 0)
            { MessageBox.Show(this, "Выберите хотя бы один сервер.", "Шаг 2", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (AnsibleWizardTabs.SelectedIndex == 2)
            {
                try
                {
                    BatchTunnelPolicy.ValidateRunSelection(new BatchTaskDefinition
                        { Name = "Ansible", Tunnels = ansibleTunnels.Select(CloneTunnel).ToList() },
                        AnsibleServerSelector.SelectedIds);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Шаг 3", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            }
            ansibleUnlockedStage = Math.Max(ansibleUnlockedStage, Math.Min(3, AnsibleWizardTabs.SelectedIndex + 1));
            AnsibleWizardTabs.SelectedIndex = Math.Min(3, AnsibleWizardTabs.SelectedIndex + 1);
            UpdateAnsibleNavigation();
            return;
        }
        if (WizardTabs.SelectedIndex == 1)
        {
            ServersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!servers.Any(x => x.Selected))
            {
                MessageBox.Show(this, "Выберите хотя бы один сервер.", "Шаг 1", MessageBoxButton.OK, MessageBoxImage.Information); return;
            }
        }
        if (WizardTabs.SelectedIndex == 3)
        {
            try { BatchTaskFile.Validate(ReadTask()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверьте задачу", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        unlockedWizardStage = Math.Max(unlockedWizardStage, Math.Min(4, WizardTabs.SelectedIndex + 1));
        WizardTabs.SelectedIndex = Math.Min(4, WizardTabs.SelectedIndex + 1);
        UpdateWizardNavigation();
    }

    private void WizardTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardTabs is null || PreviousButton is null) return;
        if (WizardTabs.SelectedIndex > unlockedWizardStage) WizardTabs.SelectedIndex = unlockedWizardStage;
        UpdateWizardNavigation();
    }

    private void UpdateWizardNavigation()
    {
        if (WizardTabs is null || PreviousButton is null || NextButton is null || RunButton is null) return;
        for (var i = 0; i < WizardTabs.Items.Count; i++)
            if (WizardTabs.Items[i] is TabItem tab) tab.IsEnabled = i <= unlockedWizardStage;
        PreviousButton.IsEnabled = WizardTabs.SelectedIndex > 0 && !runActive;
        NextButton.Visibility = WizardTabs.SelectedIndex is > 0 and < 4 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !runActive;
        RunButton.Visibility = WizardTabs.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (WizardTabs.SelectedIndex == 4)
        {
            var selected = servers.Count(x => x.Selected);
            ReviewSummary.Text = $"Серверов: {selected}. Действий: {steps.Count}. Туннелей: {tunnels.Count}. " +
                $"Рабочая папка: {(string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? "домашняя" : WorkingDirectoryBox.Text)}. " +
                $"Права: {(PrivilegeModeBox.SelectedIndex == 1 ? "повышенные" : "учётка сессии")}. После запуска появятся прогресс и журнал.";
        }
    }

    private void NewTask_Click(object sender, RoutedEventArgs e)
    {
        TaskNameBox.Text = "Новая задача"; steps.Clear(); tunnels.Clear(); WorkingDirectoryBox.Clear();
        PrivilegeModeBox.SelectedIndex = 0; unlockedWizardStage = 1; WizardTabs.SelectedIndex = 1;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Task YAML|task.yaml|YAML|*.yaml", FileName = "task.yaml" };
        if (dialog.ShowDialog(this) != true) return;
        BatchTaskFile.Save(dialog.FileName, ReadTask());
        taskDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName))!;
        RememberSavedState();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var task = ReadTask();
            var hasFiles = BatchTaskFile.HasFiles(task);
            var dialog = new SaveFileDialog { Filter = "KiTTY Manager task|*.kmtask", FileName = TaskNameBox.Text + ".kmtask" };
            if (dialog.ShowDialog(this) != true) return;
            BatchTaskFile.Save(Path.Combine(taskDirectory, "task.yaml"), task);

            var includeFiles = false;
            if (hasFiles)
            {
                var choice = AskIncludeFiles("Сохранение задачи как файл", this);
                if (choice is null) return;
                includeFiles = choice.Value;
            }

            if (includeFiles)
            {
                var missing = BatchTaskFile.GetMissingLocalSources(taskDirectory, task);
                if (missing.Count > 0)
                {
                    if (!ShowMissingFilesDialog(this, missing)) return;
                    includeFiles = false;
                }
            }

            if (includeFiles) BatchTaskFile.ExportPackageWithFiles(taskDirectory, task, dialog.FileName);
            else BatchTaskFile.ExportPackageWithoutFiles(taskDirectory, dialog.FileName);
            RememberSavedState();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Экспорт задачи", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Task files|*.kmtask;*.yaml|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (Path.GetExtension(dialog.FileName).Equals(".kmtask", StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks", Guid.NewGuid().ToString("N"));
            ownedTaskDirectories.Add(target);
            LoadTask(BatchTaskFile.ImportPackage(dialog.FileName, target));
        }
        else LoadTask(dialog.FileName);
    }

    private async void AddStep_Click(object sender, RoutedEventArgs e)
    {
        // В действии можно выбрать только серверы, отмеченные на шаге 1.
        var editor = new BatchStepEditorWindow(taskDirectory: taskDirectory,
            availableServers: servers.Where(x => x.Selected)) { Owner = this };
        await ShowStepEditorAsync(editor);
        if (editor.Accepted) steps.Add(editor.Result);
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveSelectedStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveSelectedStep(1);
    private void MoveSelectedStep(int offset)
    {
        if (StepsGrid.SelectedItem is not BatchTaskStep selected) return;
        var from = steps.IndexOf(selected); var to = from + offset;
        if (to < 0 || to >= steps.Count) return;
        steps.Move(from, to); StepsGrid.SelectedItem = selected; StepsGrid.ScrollIntoView(selected);
    }

    private void DuplicateStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItems.Count != 1 || StepsGrid.SelectedItem is not BatchTaskStep selected)
        {
            MessageBox.Show(this, "Выберите одно действие для дублирования.", "Массовая задача");
            return;
        }
        var copy = BatchStepPolicy.Duplicate(selected);
        var index = steps.IndexOf(selected) + 1;
        steps.Insert(index, copy);
        StepsGrid.SelectedItem = copy;
        StepsGrid.ScrollIntoView(copy);
    }

    private void StepsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        draggedStep = null;
        pendingClickStep = null;
        isStepDragging = false;
        if (runActive) return;

        var source = e.OriginalSource as DependencyObject;
        if (source is null) return;
        var row = ItemsControl.ContainerFromElement(StepsGrid, source) as DataGridRow;
        if (row?.Item is not BatchTaskStep step) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var targetIndex = steps.IndexOf(step);
            BatchStepInteractionPolicy.ApplyShiftSelection(steps, ref lastStepAnchorIndex, targetIndex);
            StepsGrid.Items.Refresh();
            UpdateSelectAllStepsCheck();
            return;
        }

        if (IsInsideCheckBox(source))
        {
            lastStepAnchorIndex = steps.IndexOf(step);
            Dispatcher.BeginInvoke(new Action(UpdateSelectAllStepsCheck));
            return;
        }

        if (e.ClickCount > 1)
        {
            BatchStepInteractionPolicy.ToggleStepSelection(step);
            StepsGrid.Items.Refresh();
            UpdateSelectAllStepsCheck();
            return;
        }

        draggedStep = step;
        pendingClickStep = step;
        dragStart = e.GetPosition(StepsGrid);
    }

    private void StepsGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (draggedStep is null || runActive || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(StepsGrid);
        if (Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;

        isStepDragging = true;
        pendingClickStep = null;

        if (ItemsControl.ContainerFromElement(StepsGrid, e.OriginalSource as DependencyObject) is not DataGridRow row ||
            row.Item is not BatchTaskStep target || ReferenceEquals(target, draggedStep)) return;
        var from = steps.IndexOf(draggedStep);
        var to = steps.IndexOf(target);
        if (from < 0 || to < 0) return;
        steps.Move(from, to);
        lastStepAnchorIndex = to;
        StepsGrid.SelectedItem = draggedStep;
        StepsGrid.ScrollIntoView(draggedStep);
        dragStart = position;
    }

    private void StepsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isStepDragging && pendingClickStep is not null && !runActive)
        {
            BatchStepInteractionPolicy.ToggleStepSelection(pendingClickStep);
            lastStepAnchorIndex = steps.IndexOf(pendingClickStep);
            StepsGrid.Items.Refresh();
            UpdateSelectAllStepsCheck();
        }
        draggedStep = null;
        pendingClickStep = null;
        isStepDragging = false;
    }

    private async void EditStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItems.Count != 1 || StepsGrid.SelectedItem is not BatchTaskStep selected)
        {
            MessageBox.Show(this, "Выберите одно действие для изменения.", "Массовая задача"); return;
        }
        var editor = new BatchStepEditorWindow(selected, taskDirectory, servers.Where(x => x.Selected)) { Owner = this };
        await ShowStepEditorAsync(editor);
        if (!editor.Accepted) return;
        var index = steps.IndexOf(selected);
        steps[index] = editor.Result;
    }

    private async Task ShowStepEditorAsync(BatchStepEditorWindow editor)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Closed += (_, _) => closed.TrySetResult();
        IsEnabled = false;
        try
        {
            editor.Show();
            await closed.Task;
        }
        finally
        {
            IsEnabled = true;
            Activate();
        }
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        var selected = steps.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) selected = StepsGrid.SelectedItems.OfType<BatchTaskStep>().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Отметьте действия галочками или выделите их в списке.", "Массовая задача"); return; }
        var label = selected.Count == 1 ? $"Удалить действие «{selected[0].Name}»?" : $"Удалить выбранные действия ({selected.Count})?";
        if (MessageBox.Show(this, label, "Массовая задача",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var step in selected) steps.Remove(step);
            UpdateSelectAllStepsCheck();
        }
    }

    private void UpdateSelectAllStepsCheck()
    {
        if (SelectAllStepsCheck is null) return;
        SelectAllStepsCheck.IsChecked = BatchStepInteractionPolicy.DetermineSelectAllState(steps);
    }

    private void SelectAllSteps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var step in steps) step.IsSelected = true;
        StepsGrid.Items.Refresh();
        UpdateSelectAllStepsCheck();
    }

    private void DeselectSteps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var step in steps) step.IsSelected = false;
        StepsGrid.Items.Refresh();
        UpdateSelectAllStepsCheck();
    }

    private void SelectAllStepsCheck_Click(object sender, RoutedEventArgs e)
    {
        var value = SelectAllStepsCheck.IsChecked == true;
        foreach (var step in steps) step.IsSelected = value;
        StepsGrid.Items.Refresh();
        SelectAllStepsCheck.IsChecked = value;
    }

    private void StepsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var insideCheckBox = e.OriginalSource is DependencyObject source && IsInsideCheckBox(source);
        if (!BatchStepInteractionPolicy.OpenEditorOnDoubleClick(insideCheckBox)) return;
        EditStep_Click(sender, e);
    }

    private static bool IsInsideCheckBox(DependencyObject source)
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.CheckBox) return true;
        return false;
    }

    private void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BatchTunnelEditorWindow(servers) { Owner = this };
        if (editor.ShowDialog() != true) return;
        UpdateServerDisplay(editor.Result); tunnels.Add(editor.Result);
    }

    private void EditTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelsGrid.SelectedItem is not BatchTunnelDefinition selected)
        {
            MessageBox.Show(this, "Сначала выберите туннель.", "Массовая задача", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        var editor = new BatchTunnelEditorWindow(servers, selected) { Owner = this };
        if (editor.ShowDialog() != true) return;
        UpdateServerDisplay(editor.Result);
        tunnels[tunnels.IndexOf(selected)] = editor.Result;
    }

    private void DeleteTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelsGrid.SelectedItem is not BatchTunnelDefinition selected) return;
        if (MessageBox.Show(this, $"Удалить туннель «{selected.Name}»?", "Массовая задача",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        tunnels.Remove(selected);
    }

    private void TunnelsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        EditTunnel_Click(sender, e);

    private void AddAnsibleTunnel_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BatchTunnelEditorWindow(servers.Where(x => AnsibleServerSelector.SelectedIds.Contains(x.Id))) { Owner = this };
        if (editor.ShowDialog() != true) return;
        UpdateServerDisplay(editor.Result); ansibleTunnels.Add(editor.Result);
    }

    private void EditAnsibleTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (AnsibleTunnelsGrid.SelectedItem is not BatchTunnelDefinition selected) return;
        var editor = new BatchTunnelEditorWindow(
            servers.Where(x => AnsibleServerSelector.SelectedIds.Contains(x.Id)), selected) { Owner = this };
        if (editor.ShowDialog() != true) return;
        UpdateServerDisplay(editor.Result);
        ansibleTunnels[ansibleTunnels.IndexOf(selected)] = editor.Result;
    }

    private void DeleteAnsibleTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (AnsibleTunnelsGrid.SelectedItem is BatchTunnelDefinition selected) ansibleTunnels.Remove(selected);
    }

    private void AnsibleTunnelsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        EditAnsibleTunnel_Click(sender, e);

    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        var templateFiles = BatchTaskTemplateStore.List(AppContext.BaseDirectory).ToList();
        var list = new System.Windows.Controls.ListBox
            { ItemsSource = templateFiles.Select(Path.GetFileNameWithoutExtension).ToList(), Margin = new Thickness(0, 0, 0, 10) };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var help = new TextBlock
        {
            Text = "Задачи лежат в папке Data/Tasks рядом с менеджером. Двойной клик или «Открыть/изменить» — загрузить для редактирования.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var search = new WpfTextBox { Margin = new Thickness(0, 0, 0, 10), ToolTip = "Поиск шаблона" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(list);
        void Button(string text, Action action)
        {
            var button = new System.Windows.Controls.Button { Content = text, Margin = new Thickness(4) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        var dialog = new Window
        {
            Owner = this, Title = "Шаблоны массовых задач", Width = 720, Height = 470,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        string? Selected() => list.SelectedItem is string name
            ? BatchTaskTemplateStore.Resolve(AppContext.BaseDirectory, name + ".kmtask")
            : null;
        list.MouseDoubleClick += (_, _) =>
        {
            var selected = Selected(); if (selected is null) return;
            var target = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks", Guid.NewGuid().ToString("N"));
            ownedTaskDirectories.Add(target); LoadTask(BatchTaskFile.ImportPackage(selected, target));
            loadedTemplatePath = selected; dialog.Close();
        };
        void Refresh()
        {
            templateFiles = BatchTaskTemplateStore.List(AppContext.BaseDirectory).ToList();
            var query = search.Text.Trim();
            list.ItemsSource = templateFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(x => string.IsNullOrEmpty(query) || x.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        search.TextChanged += (_, _) => Refresh();
        Button("Открыть/изменить", () =>
        {
            var selected = Selected(); if (selected is null) return;
            var target = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks", Guid.NewGuid().ToString("N"));
            ownedTaskDirectories.Add(target); LoadTask(BatchTaskFile.ImportPackage(selected, target));
            loadedTemplatePath = selected; dialog.Close();
        });
        Button("Переименовать", () =>
        {
            var selected = Selected(); if (selected is null) return;
            var prompt = new PromptDialog("Переименовать шаблон", "Новое название шаблона:",
                Path.GetFileNameWithoutExtension(selected)) { Owner = dialog };
            if (prompt.ShowDialog() != true) return;
            try
            {
                var renamed = BatchTaskTemplateStore.Rename(AppContext.BaseDirectory, selected, prompt.Value);
                if (loadedTemplatePath is not null &&
                    string.Equals(loadedTemplatePath, selected, StringComparison.OrdinalIgnoreCase))
                    loadedTemplatePath = renamed;
                Refresh();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Шаблоны", MessageBoxButton.OK, MessageBoxImage.Error); }
        });
        Button("Импортировать…", () =>
        {
            var open = new OpenFileDialog { Filter = "KiTTY Manager task|*.kmtask" };
            if (open.ShowDialog(dialog) != true) return;
            var target = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks", Guid.NewGuid().ToString("N"));
            try
            {
                ownedTaskDirectories.Add(target);
                var manifest = BatchTaskFile.ImportPackage(open.FileName, target);
                var imported = BatchTaskFile.Load(manifest);
                BatchTaskTemplateStore.Save(AppContext.BaseDirectory, target, imported);
                Refresh();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Импорт шаблона", MessageBoxButton.OK, MessageBoxImage.Error); }
        });
        Button("Экспортировать…", () =>
        {
            var selected = Selected(); if (selected is null) return;
            var save = new SaveFileDialog { Filter = "KiTTY Manager task|*.kmtask", FileName = Path.GetFileName(selected) };
            if (save.ShowDialog(dialog) != true) return;

            var staging = Path.Combine(Path.GetTempPath(), "KiTTYManager", "export-" + Guid.NewGuid().ToString("N"));
            try
            {
                var manifest = BatchTaskFile.ImportPackage(selected, staging);
                var task = BatchTaskFile.Load(manifest);
                BatchTaskFile.ResolveImportedLocalSources(task, staging);
                var hasFiles = BatchTaskFile.HasFiles(task);
                var includeFiles = false;
                if (hasFiles)
                {
                    var choice = AskIncludeFiles("Экспорт шаблона", dialog);
                    if (choice is null) return;
                    includeFiles = choice.Value;
                }

                if (includeFiles)
                {
                    var missing = BatchTaskFile.GetMissingLocalSources(staging, task);
                    if (missing.Count > 0)
                    {
                        if (!ShowMissingFilesDialog(dialog, missing)) return;
                        includeFiles = false;
                    }
                }

                if (includeFiles) BatchTaskFile.ExportPackageWithFiles(staging, task, save.FileName);
                else BatchTaskFile.ExportPackageWithoutFiles(staging, save.FileName);
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Экспорт шаблона", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
        });
        var deleteButton = new System.Windows.Controls.Button { Content = "Удалить", Margin = new Thickness(4) };
        deleteButton.SetResourceReference(StyleProperty, "DangerButton");
        deleteButton.Click += (_, _) =>
        {
            var selected = Selected(); if (selected is null) return;
            if (MessageBox.Show(dialog, $"Удалить шаблон «{Path.GetFileNameWithoutExtension(selected)}»?", "Шаблоны",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            BatchTaskTemplateStore.Delete(AppContext.BaseDirectory, selected); Refresh();
        };
        buttons.Children.Add(deleteButton);
        Button("Закрыть", dialog.Close);
        dialog.ShowDialog();
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var task = ReadTask();
            var targetName = BatchTaskTemplateStore.SafeName(task.Name);
            var existingPath = BatchTaskTemplateStore.Resolve(AppContext.BaseDirectory, targetName + ".kmtask");
            if (File.Exists(existingPath))
            {
                var choice = ShowThreeButtonDialog(
                    $"Шаблон «{task.Name}» уже существует.",
                    "Перезаписать существующий", "Сохранить как новый шаблон", "Отмена");
                if (choice == 2) return;
                if (choice == 1)
                {
                    var prompt = new PromptDialog("Новое название шаблона", "Введите название для нового шаблона:",
                        task.Name + " (копия)") { Owner = this };
                    if (prompt.ShowDialog() != true) return;
                    task.Name = prompt.Value.Trim();
                    TaskNameBox.Text = task.Name;
                }
            }

            var hasFiles = BatchTaskFile.HasFiles(task);
            var includeFiles = false;
            if (hasFiles)
            {
                var choice = AskIncludeFiles("Сохранение шаблона", this);
                if (choice is null) return;
                includeFiles = choice.Value;
            }

            if (includeFiles)
            {
                var missing = BatchTaskFile.GetMissingLocalSources(taskDirectory, task);
                if (missing.Count > 0)
                {
                    if (!ShowMissingFilesDialog(this, missing)) return;
                    includeFiles = false;
                }
            }

            loadedTemplatePath = BatchTaskTemplateStore.Save(AppContext.BaseDirectory, taskDirectory, task, includeFiles);
            RememberSavedState();
            MessageBox.Show(this, "Шаблон сохранён в папке Data/Tasks рядом с менеджером.", "Шаблон");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Шаблон", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool ShowMissingFilesDialog(Window owner, IReadOnlyList<string> missingFiles)
    {
        var result = false;
        var panel = new StackPanel { Margin = new Thickness(18), MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = "Следующие файлы задачи не найдены на диске:",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var textBox = new WpfTextBox
        {
            Text = string.Join(Environment.NewLine, missingFiles),
            IsReadOnly = true,
            AcceptsReturn = true,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        panel.Children.Add(textBox);

        panel.Children.Add(new TextBlock
        {
            Text = "Без этих файлов невозможно включить файлы в пакет. Вы можете сохранить задачу без файлов или отменить сохранение.",
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => { result = false; Window.GetWindow(cancelBtn)!.DialogResult = false; };
        var saveWithoutFilesBtn = new System.Windows.Controls.Button { Content = "Сохранить без файлов", MinWidth = 160, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        saveWithoutFilesBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        saveWithoutFilesBtn.Click += (_, _) => { result = true; Window.GetWindow(saveWithoutFilesBtn)!.DialogResult = true; };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveWithoutFilesBtn);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Owner = owner,
            Title = "Отсутствуют файлы задачи",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        dialog.ShowDialog();
        return result;
    }

    // null = отмена, true/false = выбор пользователя
    private bool? AskIncludeFiles(string title, Window? owner = null)
    {
        var result = (bool?)null;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var check = new System.Windows.Controls.CheckBox
        {
            Content = "Включить файлы в пакет",
            IsChecked = BatchTaskPolicy.IncludeFilesInPackageByDefault,
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Если включено — загруженные файлы (конфиги, скрипты) сохраняются внутри .kmtask и задача полностью самодостаточна. Если выключено — сохраняются только пути до файлов: пакет будет маленьким, но файлы должны быть на месте при запуске."
        };
        panel.Children.Add(check);
        panel.Children.Add(new TextBlock
        {
            Text = "Без файлов пакет содержит только описание задачи (task.yaml). Пути до файлов сохраняются как есть.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        });
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => { result = null; Window.GetWindow(cancelBtn)!.DialogResult = true; };
        var okBtn = new System.Windows.Controls.Button { Content = "Сохранить", MinWidth = 100, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        okBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        okBtn.Click += (_, _) => { result = check.IsChecked == true; Window.GetWindow(okBtn)!.DialogResult = true; };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = owner ?? this, Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        dialog.ShowDialog();
        return result;
    }

    // 0 = primary, 1 = secondary, 2 = cancel
    private int ShowThreeButtonDialog(string message, string primary, string secondary, string cancel)
    {
        var result = 2;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        void AddBtn(string text, int value, bool isDefault, bool isCancel)
        {
            var btn = new System.Windows.Controls.Button { Content = text, MinWidth = 140, Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            if (isDefault) btn.SetResourceReference(StyleProperty, "PrimaryButton");
            btn.Click += (_, _) => { result = value; Window.GetWindow(btn)!.DialogResult = true; };
            buttons.Children.Add(btn);
        }
        AddBtn(cancel, 2, false, true);
        AddBtn(secondary, 1, false, false);
        AddBtn(primary, 0, true, false);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this, Title = "Сохранение шаблона", SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("WindowBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        dialog.ShowDialog();
        return result;
    }

    private void ShowTemplatePreviewAndConfirm()
    {
        unlockedWizardStage = 4;
        WizardTabs.SelectedIndex = 4;
        UpdateWizardNavigation();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ThemedMessageDialog.Show(this, ReviewSummary.Text + "\n\nЗапустить эту задачу?", "Проверка шаблона",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Run_Click(this, new RoutedEventArgs());
        }));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnsibleMode) { await RunAnsibleAsync(); return; }
        ServersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var serverIds = servers.Where(item => item.Selected).Select(item => item.Id).ToArray();
        if (serverIds.Length == 0) { MessageBox.Show(this, "Не выбраны серверы.", "Массовая задача"); return; }
        var runStarted = false;
        try
        {
            var task = ReadTask();
            BatchTaskFile.Validate(task);
            CleanupStaleTaskDirectories();
            var missingTunnelServers = task.Tunnels.SelectMany(x => x.ServerIds).Distinct()
                .Where(id => config.FindServer(id) is null).ToArray();
            if (missingTunnelServers.Length > 0)
                throw new InvalidDataException("В туннелях остались серверы из другой конфигурации. Откройте «Серверы…» и выберите их заново.");
            cancellation = new();
            runActive = true;
            runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Owner is not null) Owner.IsEnabled = false;
            StepsGrid.IsEnabled = false; TunnelEditor.IsEnabled = false; ServerSelector.IsEnabled = false;
            TaskNameBox.IsEnabled = false; FailureModeBox.IsEnabled = false;
            for (var i = 0; i < 4; i++) if (WizardTabs.Items[i] is TabItem tab) tab.IsEnabled = false;
            logs.Clear(); RenderLog();
            SaveLogButton.IsEnabled = false; SaveSeparateLogsButton.IsEnabled = false;
            foreach (var server in servers) server.Status = serverIds.Contains(server.Id) ? "Ожидает" : "Не выбран";
            ServersGrid.Items.Refresh();
            UpdateStatusCounts();
            RunButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            Progress.Value = 0; Progress.Maximum = Math.Max(1, task.Steps.Count);
            var mode = Enum.Parse<BatchFailureMode>(((ComboBoxItem)FailureModeBox.SelectedItem).Tag.ToString()!);
            runStarted = true;
            fileLog?.Invoke($"BATCH Задача «{task.Name}» запущена: серверов {serverIds.Length}, действий {task.Steps.Count}, туннелей {task.Tunnels.Count}");
            var result = await runner.RunAsync(config, task, serverIds, taskDirectory, mode, cancellation.Token);
            fileLog?.Invoke($"BATCH Задача «{task.Name}» завершена: успешно {result.Servers.Count(x => x.Success)} из {serverIds.Length}");
            foreach (var item in result.Servers)
            {
                var choice = servers.FirstOrDefault(x => x.Id == item.ServerId);
                if (choice is not null) choice.Status = item.Cancelled ? "Остановлен" : item.Success ? "Готово" : "Ошибка";
            }
            ServersGrid.Items.Refresh();
            UpdateStatusCounts();
            AppendSummaryLogs(BatchRunSummary.Lines(result.Servers, serverIds.Length));
        }
        catch (OperationCanceledException)
        {
            AppendSummaryLogs([new BatchRunSummaryLine(Guid.Empty, "Задача", "Остановлено пользователем.", BatchLogLevel.Warning)]);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка задачи", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (runStarted) saveConfig?.Invoke();
            cancellation?.Dispose(); cancellation = null; RunButton.IsEnabled = true; StopButton.IsEnabled = false;
            StepsGrid.IsEnabled = true; TunnelEditor.IsEnabled = true; ServerSelector.IsEnabled = true;
            TaskNameBox.IsEnabled = true; FailureModeBox.IsEnabled = true;
            if (Owner is not null) Owner.IsEnabled = true;
            runActive = false; runCompletion?.TrySetResult(true);
            SaveLogButton.IsEnabled = logs.Count > 0; SaveSeparateLogsButton.IsEnabled = logs.Count > 0;
            UpdateWizardNavigation();
            if (closeAfterStop) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void CleanupStaleTaskDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "KiTTYManager", "tasks");
        foreach (var directory in BatchTaskWorkspace.CleanupStaleDirectories(root, taskDirectory))
            ownedTaskDirectories.Remove(directory);
    }

    private void SelectAllServers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var server in ServersGrid.Items.Cast<object>().OfType<BatchServerChoice>()) server.Selected = true;
        RefreshSelectedServers();
    }

    private void ClearServers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var server in servers) server.Selected = false;
        RefreshSelectedServers();
    }

    private void ServerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(ServersGrid.ItemsSource);
        var query = ServerSearchBox.Text.Trim();
        view.Filter = item => item is BatchServerChoice x && (string.IsNullOrEmpty(query) ||
            x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.Host.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void ServersGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Чекбокс обрабатывает клик сам через binding — не дублируем.
        if (e.OriginalSource is System.Windows.Controls.CheckBox or System.Windows.Shapes.Path) return;
        if (ItemsControl.ContainerFromElement(ServersGrid, e.OriginalSource as DependencyObject) is not DataGridRow row ||
            row.Item is not BatchServerChoice selected) return;
        selected.Selected = !selected.Selected; RefreshSelectedServers();
    }

    private void RefreshSelectedServers()
    {
        ServersGrid?.Items.Refresh();
        constructorServerSelector?.RefreshRows();
        if (SelectedServersText is not null)
        {
            var selected = servers.Where(x => x.Selected).ToArray();
            SelectedServersText.Text = selected.Length == 0
                ? "Не выбрано"
                : $"Выбрано: {selected.Length} — {string.Join(", ", selected.Take(2).Select(x => x.Name))}{(selected.Length > 2 ? "…" : "")}";
            SelectedServersText.ToolTip = selected.Length == 0
                ? null
                : string.Join(Environment.NewLine, selected.Select(x => x.Name));
        }
        if (LogServerFilter is not null)
        {
            var old = (LogServerFilter.SelectedItem as BatchLogFilter)?.ServerId;
            LogServerFilter.ItemsSource = new[] { new BatchLogFilter(null, "Все выбранные серверы") }
                .Concat(servers.Where(x => x.Selected).Select(x => new BatchLogFilter(x.Id, x.Name))).ToArray();
            LogServerFilter.SelectedItem = LogServerFilter.Items.Cast<BatchLogFilter>().FirstOrDefault(x => x.ServerId == old) ?? LogServerFilter.Items[0];
        }
    }

    private void RefreshAnsibleSelectedServers()
    {
        if (AnsibleSelectedServersText is null) return;
        var selected = servers.Where(item => item.Selected).ToArray();
        AnsibleSelectedServersText.Text = selected.Length == 0
            ? "Не выбрано"
            : $"Выбрано: {selected.Length} — {string.Join(", ", selected.Take(2).Select(item => item.Name))}{(selected.Length > 2 ? "…" : "")}";
        AnsibleSelectedServersText.ToolTip = selected.Length == 0
            ? null
            : string.Join(Environment.NewLine, selected.Select(item => item.Name));
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();

    private bool IsAnsibleMode => (TaskModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Ansible";

    private void TaskModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardTabs is null || AnsiblePanel is null) return;
        WizardTabs.Visibility = IsAnsibleMode ? Visibility.Collapsed : Visibility.Visible;
        AnsiblePanel.Visibility = IsAnsibleMode ? Visibility.Visible : Visibility.Collapsed;
        TaskNameLabel.Visibility = TaskNameBox.Visibility = IsAnsibleMode ? Visibility.Collapsed : Visibility.Visible;
        AnsibleReadinessButton.Visibility = IsAnsibleMode ? Visibility.Visible : Visibility.Collapsed;
        SaveSeparateLogsButton.Visibility = IsAnsibleMode ? Visibility.Collapsed : Visibility.Visible;
        if (IsAnsibleMode)
        {
            syncingServerSelectors = true;
            try
            {
                AnsibleServerSelector.SetSelected(ansibleSelectedIds);
                foreach (var server in servers) server.Selected = ansibleSelectedIds.Contains(server.Id);
            }
            finally { syncingServerSelectors = false; }
            RefreshAnsibleSelectedServers();
            UpdateAnsibleNavigation();
        }
        else
        {
            syncingServerSelectors = true;
            try
            {
                constructorServerSelector.SetSelected(constructorSelectedIds);
                foreach (var server in servers) server.Selected = constructorSelectedIds.Contains(server.Id);
            }
            finally { syncingServerSelectors = false; }
            UpdateWizardNavigation();
        }
    }

    private void BrowseAnsiblePlaybook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Ansible playbook|*.yml;*.yaml|Все файлы|*.*" };
        if (dialog.ShowDialog(this) == true) AnsiblePlaybookBox.Text = dialog.FileName;
    }

    private void CheckAnsibleReadiness_Click(object sender, RoutedEventArgs e)
    {
        if (runActive) return;
        var runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", "Ansible");
        var vmRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager", "AnsibleVm");
        var dialog = new AnsibleReadinessWindow(async token =>
        {
            return await AnsibleWindowsDiagnostics.CheckAsync(runtime, vmRoot, async innerToken =>
            {
                await using var vm = await AnsibleVmSession.StartAsync(runtime, vmRoot, innerToken,
                    message => fileLog?.Invoke("ANSIBLE readiness: " + message));
                if (!vm.AnsibleVersion.Contains("2.16", StringComparison.Ordinal))
                    throw new InvalidOperationException("В VM найден несовместимый " + vm.AnsibleVersion);
            }, token);
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private void AnsibleWizardTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnsibleWizardTabs is null || !IsAnsibleMode) return;
        if (AnsibleWizardTabs.SelectedIndex > ansibleUnlockedStage)
            AnsibleWizardTabs.SelectedIndex = ansibleUnlockedStage;
        UpdateAnsibleNavigation();
    }

    private void UpdateAnsibleNavigation()
    {
        if (AnsibleWizardTabs is null || PreviousButton is null) return;
        for (var i = 0; i < AnsibleWizardTabs.Items.Count; i++)
            if (AnsibleWizardTabs.Items[i] is TabItem tab) tab.IsEnabled = i <= ansibleUnlockedStage;
        PreviousButton.Visibility = Visibility.Visible;
        PreviousButton.IsEnabled = AnsibleWizardTabs.SelectedIndex > 0 && !runActive;
        NextButton.Visibility = AnsibleWizardTabs.SelectedIndex < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !runActive;
        RunButton.Visibility = AnsibleWizardTabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (AnsibleWizardTabs.SelectedIndex == 3)
        {
            AnsibleReviewText.Text = $"Playbook: {Path.GetFileName(AnsiblePlaybookBox.Text)}. " +
                $"Серверов: {AnsibleServerSelector.SelectedIds.Count}. " +
                $"Туннелей: {ansibleTunnels.Count}. " +
                "После запуска здесь появятся прогресс и журнал.";
        }
    }

    private void BrowseAnsibleDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для файлов, выгруженных с серверов",
            InitialDirectory = AnsibleDownloadFolderBox.Text };
        if (dialog.ShowDialog(this) == true) AnsibleDownloadFolderBox.Text = dialog.FolderName;
    }

    private void CopyAnsibleLog_Click(object sender, RoutedEventArgs e)
    {
        if (AnsibleLogBox.Text.Length > 0) System.Windows.Clipboard.SetText(AnsibleLogBox.Text);
    }

    internal string AnsibleDownloadDirectory => AnsibleDownloadFolderBox.Text.Trim();

    private int AnsibleVerbosity => int.TryParse(
        (AnsibleVerbosityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value)
        ? AnsibleRunPolicy.NormalizeVerbosity(value) : 0;

    private void AppendAnsibleLog(string text, IEnumerable<string>? secrets = null, bool isConsole = false)
    {
        var safe = AnsibleSecretRedactor.Redact(text, secrets ?? []);
        var isErrorOrWarning = safe.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
                               safe.Contains("FAILED!", StringComparison.OrdinalIgnoreCase) ||
                               safe.Contains("ERROR!", StringComparison.OrdinalIgnoreCase) ||
                               safe.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
                               safe.StartsWith("Ошибка (", StringComparison.Ordinal) ||
                               safe.Contains("Сбой: ", StringComparison.Ordinal) ||
                               safe.Contains("недоступен", StringComparison.Ordinal);
        var entry = new AnsibleLogEntry(DateTimeOffset.Now, safe, isConsole, isErrorOrWarning);
        void Append()
        {
            ansibleLogs.Add(entry);
            fileLog?.Invoke("ANSIBLE " + safe);
            RenderAnsibleLog();
        }
        if (Dispatcher.CheckAccess()) Append(); else Dispatcher.Invoke(Append);
    }

    private void RenderAnsibleLog()
    {
        if (AnsibleLogBox is null) return;
        var hideTime = AnsibleHideTimeMenu?.IsChecked == true;
        var consoleOnly = AnsibleConsoleOnlyMenu?.IsChecked == true;
        var errorsOnly = AnsibleLogWarnErrorMenu?.IsChecked == true;
        var filtered = AnsibleLogFormatter.Filter(ansibleLogs, consoleOnly, errorsOnly);
        AnsibleLogBox.Text = string.Concat(filtered.Select(x => AnsibleLogFormatter.Format(x, hideTime)));
        AnsibleLogBox.ScrollToEnd();
    }

    private void PreviewAnsible_Click(object sender, RoutedEventArgs e)
    {
        var path = AnsiblePlaybookBox.Text.Trim();
        if (!File.Exists(path)) { MessageBox.Show(this, "Выберите playbook.", "Ansible"); return; }
        var directory = Path.GetDirectoryName(path)!;
        var selected = servers.Where(x => x.Selected).Select(x => x.Name).ToArray();
        var roles = Directory.Exists(Path.Combine(directory, "roles")) ? Directory.EnumerateDirectories(Path.Combine(directory, "roles")).Select(Path.GetFileName) : [];
        var collections = Directory.Exists(Path.Combine(directory, "collections")) ? Directory.EnumerateDirectories(Path.Combine(directory, "collections")).Select(Path.GetFileName) : [];
        var adjacent = AnsibleTaskWorkspacePolicy.ProjectMaterials(path)
            .Select(file => Path.GetRelativePath(directory, file)).OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase).ToArray();
        IReadOnlyList<AnsibleLocalFile> windowsFiles;
        try { windowsFiles = AnsibleWindowsMaterialScanner.Scan(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Материалы Ansible"); return; }
        AnsibleCompositionText.Text = $"Папка: {directory}\nPlaybook: {Path.GetFileName(path)}\n" +
            $"Серверы: {string.Join(", ", selected)}\nРоли: {string.Join(", ", roles)}\n" +
            $"Коллекции: {string.Join(", ", collections)}\nПодробность: {(AnsibleVerbosity == 0 ? "обычная" : "-" + new string('v', AnsibleVerbosity))}\n" +
            $"Туннели ({ansibleTunnels.Count}):\n{string.Join(Environment.NewLine, ansibleTunnels.Select(x => x.Summary))}\n" +
            $"Материалы playbook ({adjacent.Length}):\n{string.Join(Environment.NewLine, adjacent)}\n" +
            $"Файлы Windows ({windowsFiles.Count}):\n{string.Join(Environment.NewLine, windowsFiles.Select(x => x.SourcePath))}\n" +
            $"Папка выгрузок: {AnsibleDownloadFolderBox.Text}\n\ndelegate_to: localhost выполняется внутри Linux VM.";
        AnsibleCompositionText.Visibility = Visibility.Visible;
    }

    private async Task RunAnsibleAsync()
    {
        var playbook = AnsiblePlaybookBox.Text.Trim();
        if (!File.Exists(playbook)) { MessageBox.Show(this, "Выберите существующий playbook.", "Ansible"); return; }
        var selected = servers.Where(x => x.Selected).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Не выбраны серверы.", "Ansible"); return; }
        var routes = new List<(BatchServerChoice Choice, ManagedServer Server, ActiveRoute Route)>();
        var activeTunnels = new List<(ActiveRoute Route, ForwardedPort Port)>();
        AnsibleTaskWorkspace? workspace = null;
        var runStarted = false;
        cancellation = new(); runActive = true; runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunButton.IsEnabled = false; StopButton.IsEnabled = true; TaskModeBox.IsEnabled = false;
        SaveLogButton.IsEnabled = false;
        AnsibleLogBox.Clear(); ansibleLogs.Clear(); AnsibleProgress.Maximum = selected.Length + 2; AnsibleProgress.Value = 0;
        var secrets = config.AllServers().SelectMany(x => new[] { x.Password, x.RootPassword, x.PrivateKeyPassphrase })
            .Append(AnsibleVaultPasswordBox.Value).Where(x => x.Length > 0).ToArray();
        try
        {
            BatchTunnelPolicy.ValidateRunSelection(new BatchTaskDefinition
                { Name = "Ansible", Tunnels = ansibleTunnels.Select(CloneTunnel).ToList() },
                selected.Select(item => item.Id).ToArray());
            runStarted = true;
            AppendAnsibleLog($"Запуск: playbook={Path.GetFileName(playbook)}; серверов={selected.Length}; verbosity={AnsibleVerbosity}.", secrets);
            var templates = BatchTaskTemplateStore.GetAnsibleDirectory(AppContext.BaseDirectory);
            AppendAnsibleLog("Подготавливаю playbook и соседние зависимости.", secrets);
            workspace = AnsibleTaskWorkspacePolicy.Prepare(templates, playbook);
            var windowsFiles = AnsibleWindowsMaterialScanner.Scan(playbook);
            var dependencies = AnsibleDependencyScanner.Scan(workspace.TaskDirectory);
            if (dependencies.Missing.Count > 0)
                throw new InvalidDataException("Не найдены локальные зависимости: " + string.Join(", ", dependencies.Missing) +
                    ". Runtime не скачивает Galaxy-коллекции.");
            var gate = new SemaphoreSlim(5);
            var routeTasks = selected.Select(async choice =>
            {
                await gate.WaitAsync(cancellation.Token);
                try
                {
                    var server = config.FindServer(choice.Id) ?? throw new InvalidOperationException("Сессия удалена: " + choice.Name);
                    choice.Status = "Строю маршрут";
                    AppendAnsibleLog($"Маршрут: начинаю подключение к «{choice.Name}».", secrets);
                    var route = await ConnectAnsibleRouteWithRecoveryAsync(server, choice, cancellation.Token);
                    lock (routes) routes.Add((choice, server, route));
                    AppendAnsibleLog($"Маршрут: «{choice.Name}» готов, локальный SSH-порт {route.LocalSshPort}.", secrets);
                    choice.Status = "Маршрут готов"; await Dispatcher.InvokeAsync(() => AnsibleProgress.Value++);
                }
                catch (Exception ex)
                {
                    AppendAnsibleLog($"Маршрут: «{choice.Name}» — {ex.GetType().Name}: {ex.Message}", secrets);
                    throw;
                }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(routeTasks);
            var usedHosts = new HashSet<string>(); var usedGroups = new HashSet<string>();
            var inventoryGroups = new Dictionary<string, string>(StringComparer.Ordinal);
            var ordered = routes.OrderBy(x => x.Choice.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var inventoryHosts = ordered.Select((item, index) =>
            {
                var display = FindServerGroupPath(item.Server.Id) ?? "Без группы";
                if (!inventoryGroups.TryGetValue(display, out var inventoryGroup))
                {
                    inventoryGroup = AnsibleInventoryGenerator.SafeUniqueName(display, GuidUtility(Guid.Empty, display), usedGroups);
                    inventoryGroups.Add(display, inventoryGroup);
                }
                return new AnsibleInventoryHost(item.Server, item.Route.LocalSshPort,
                    AnsibleInventoryGenerator.SafeUniqueName(item.Server.Name, item.Server.Id, usedHosts),
                    inventoryGroup, display, index,
                    AnsibleRunPolicy.ServerDownloadFolder(item.Server.Name, item.Server.CleanHost),
                    ansibleTunnels.Where(tunnel => BatchTunnelPolicy.AppliesTo(tunnel, item.Server.Id))
                        .Select(CloneTunnel).ToArray());
            }).ToArray();
            var inventory = AnsibleInventoryGenerator.Generate(inventoryHosts);
            File.WriteAllText(Path.Combine(workspace.ServiceDirectory, "inventory.yml"), inventory, new UTF8Encoding(false));
            foreach (var item in ordered)
            foreach (var definition in ansibleTunnels.Where(tunnel => BatchTunnelPolicy.AppliesTo(tunnel, item.Server.Id)))
            {
                var port = BatchTunnelPolicy.Create(definition);
                port.Exception += (_, args) => AppendAnsibleLog(
                    $"Туннель «{definition.Name}»: ошибка — {args.Exception.Message}", secrets);
                item.Route.AddForwardedPort(port);
                activeTunnels.Add((item.Route, port));
                port.Start();
                AppendAnsibleLog($"Туннель «{definition.Name}» работает для «{item.Server.Name}»: " +
                    $"{definition.BindHost}:{definition.BindPort} → {definition.DestinationHost}:{definition.DestinationPort}", secrets);
            }
            var hostSecrets = ordered.Select((item, index) => new AnsibleGuestHostSecret(
                inventoryHosts[index].InventoryName, item.Server.Password, item.Server.RootPassword,
                ReadPrivateKey(item.Server.PrivateKeyPath), item.Server.PrivateKeyPassphrase)).ToArray();
            var displayByInventory = ordered.Select((item, index) =>
                    new KeyValuePair<string, string>(inventoryHosts[index].InventoryName, item.Server.Name))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", "Ansible");
            var vmRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager", "AnsibleVm");
            AppendAnsibleLog("Маршруты готовы. Запускаю встроенную Linux VM.", secrets);
            await using var vm = await AnsibleVmSession.StartAsync(runtime, vmRoot, cancellation.Token,
                message => AppendAnsibleLog(message, secrets));
            AnsibleProgress.Value++;
            var effectivePlaybook = workspace.CreatedForRun ? Path.GetFileName(playbook) : Path.GetRelativePath(workspace.TaskDirectory, playbook);
            var code = await vm.RunAsync(new(workspace.TaskDirectory, effectivePlaybook, inventory, hostSecrets,
                AnsibleVaultPasswordBox.Value, windowsFiles, AnsibleDownloadDirectory, AnsibleVerbosity,
                config.TaskConnectionRecoveryMinutes), item =>
            {
                var raw = item.Type switch
                {
                    "connection_lost" => $"Связь с {displayByInventory.GetValueOrDefault(item.Message, item.Message)} потеряна; текущая операция удерживается, повтор каждые 10 секунд.",
                    "connection_recovered" => $"Связь с {displayByInventory.GetValueOrDefault(item.Message, item.Message)} восстановлена; выполнение продолжается.",
                    _ => AnsibleVmEventFormatter.Format(item,
                        host => displayByInventory.GetValueOrDefault(host, host))
                };
                raw = AnsibleRunPolicy.HumanizeVmRoute(raw);
                AppendAnsibleLog(raw, secrets, isConsole: true);
            }, cancellation.Token, async (prompt, token) => await PromptConnectionRecoveryAsync(
                new(displayByInventory.GetValueOrDefault(prompt.InventoryName, prompt.InventoryName),
                    "выполнение Ansible", prompt.PreviousMinutes), token));
            AnsibleProgress.Value++;
            if (code != 0) throw new InvalidOperationException($"ansible-playbook завершился с кодом {code}.");
            foreach (var item in ordered) item.Choice.Status = "Готово";
        }
        catch (OperationCanceledException) { AppendAnsibleLog("Остановлено пользователем.", secrets); foreach (var item in routes) item.Choice.Status = "Остановлен"; }
        catch (Exception ex) { var safe = AnsibleSecretRedactor.Redact(ex.Message, secrets); fileLog?.Invoke("ANSIBLE EXCEPTION " + AnsibleSecretRedactor.Redact(ex.ToString(), secrets)); AppendAnsibleLog($"Ошибка ({ex.GetType().Name}): {safe}", secrets); MessageBox.Show(this, safe, "Ansible", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            foreach (var active in activeTunnels)
            {
                try { if (active.Port.IsStarted) active.Port.Stop(); } catch { }
                try { active.Route.RemoveForwardedPort(active.Port); } catch { }
                try { active.Port.Dispose(); } catch { }
            }
            foreach (var item in routes) try { if (routeRelease is null) item.Route.Dispose(); else routeRelease(item.Route); } catch { }
            if (workspace is not null)
            {
                AnsibleTaskWorkspacePolicy.DeleteSecrets(workspace);
                if (workspace.CreatedForRun && MessageBox.Show(this, "Удалить новую папку этой Ansible-задачи?\n\n«Нет» — оставить для повторного запуска.", "Папка Ansible-задачи", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    try { AnsibleTaskWorkspacePolicy.DeleteCreatedTask(BatchTaskTemplateStore.GetAnsibleDirectory(AppContext.BaseDirectory), workspace); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Удаление папки"); }
            }
            if (runStarted) saveConfig?.Invoke();
            cancellation.Dispose(); cancellation = null; runActive = false; RunButton.IsEnabled = true; StopButton.IsEnabled = false; TaskModeBox.IsEnabled = true;
            SaveLogButton.IsEnabled = AnsibleLogBox.Text.Length > 0;
            runCompletion?.TrySetResult(true); if (closeAfterStop) Close();
        }
    }

    private async Task<ActiveRoute> ConnectAnsibleRouteWithRecoveryAsync(
        ManagedServer server, BatchServerChoice choice, CancellationToken token)
    {
        var minutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(config.TaskConnectionRecoveryMinutes);
        while (true)
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
            Exception? last = null;
            do
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return routeFactory is null
                        ? (await connections.ConnectBestAsync(config, server.Id, token)).Route
                        : await routeFactory(server.Id, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TaskConnectionRecoveryPolicy.IsConnectivityFailure(ex))
                {
                    last = ex;
                    AppendAnsibleLog($"Маршрут: «{choice.Name}» недоступен; повтор через 10 секунд.", []);
                }
                if (DateTimeOffset.UtcNow >= deadline) break;
                var delay = deadline - DateTimeOffset.UtcNow;
                await Task.Delay(delay < TaskConnectionRecoveryPolicy.RetryDelay
                    ? delay : TaskConnectionRecoveryPolicy.RetryDelay, token);
            } while (true);
            var additional = await PromptConnectionRecoveryAsync(
                new(choice.Name, "подключение Ansible", minutes), token);
            if (additional is null)
                throw new OperationCanceledException("Ожидание восстановления связи остановлено пользователем.", last, token);
            minutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(additional.Value);
        }
    }

    private string? FindServerGroupPath(Guid serverId)
    {
        string? Find(IEnumerable<ServerGroup> groups, string prefix)
        {
            foreach (var group in groups)
            {
                var path = prefix.Length == 0 ? group.Name : prefix + " / " + group.Name;
                if (group.Servers.Any(x => x.Id == serverId)) return path;
                var nested = Find(group.Groups, path); if (nested is not null) return nested;
            }
            return null;
        }
        return Find(config.Groups, "");
    }

    private static Guid GuidUtility(Guid id, string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N") + value));
        return new Guid(bytes[..16]);
    }

    private static string ReadPrivateKey(string configured)
    {
        var path = ManagerPathResolver.ResolveOptionalExistingFile(configured, "SSH-ключ");
        return path is null ? "" : Convert.ToBase64String(File.ReadAllBytes(path));
    }

    public bool IsRunning => runActive;

    public async Task StopAndCloseAsync()
    {
        closeAfterStop = true;
        cancellation?.Cancel();
        if (!runActive) { Close(); return; }
        if (runCompletion is not null) await runCompletion.Task;
    }

    private void BatchTaskWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (runActive)
        {
            e.Cancel = true;
            closeAfterStop = true;
            cancellation?.Cancel();
            return;
        }
        if (HasUnsavedChanges() && MessageBox.Show(this,
                "В задаче есть несохранённые изменения. Закрыть без сохранения?",
                "Массовая задача", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private string CurrentTaskState() => JsonSerializer.Serialize(ReadTask());
    private void RememberSavedState() => savedTaskState = CurrentTaskState();
    private bool HasUnsavedChanges() => !string.Equals(savedTaskState, CurrentTaskState(), StringComparison.Ordinal);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!runActive) { Close(); return; }
        if (MessageBox.Show(this, "Задача выполняется. Остановить её и закрыть окно?", "Массовая задача",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { closeAfterStop = true; cancellation?.Cancel(); }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text log|*.log;*.txt", FileName = IsAnsibleMode ? "ansible.log" : "batch-task.log" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName,
            IsAnsibleMode ? AnsibleLogBox.Text : LogBox.Text);
    }

    private void SaveSeparateLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text log|*.log", FileName = "batch-task.log" };
        if (dialog.ShowDialog(this) != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName)!;
        var stem = Path.GetFileNameWithoutExtension(dialog.FileName);
        foreach (var group in logs.Where(x => x.ServerId != Guid.Empty).GroupBy(x => new { x.ServerId, x.ServerName }))
        {
            var safeName = string.Concat(group.Key.ServerName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            File.WriteAllText(Path.Combine(directory, $"{stem}.{safeName}.{group.Key.ServerId:N}.log"), string.Concat(group.Select(FormatLog)));
        }
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderLog();

    // Summary lines live in the log model, not in the text box: level and
    // server filters, copy and save must keep seeing them after a re-render.
    private void AppendSummaryLogs(IReadOnlyList<BatchRunSummaryLine> lines)
    {
        foreach (var line in lines)
        {
            var entry = new BatchTaskLog(DateTimeOffset.Now, line.ServerId, line.ServerName, "", line.Message, line.Level, "Итог");
            logs.Add(entry);
            // Файловый журнал уже получает сводную строку «BATCH Задача … завершена»,
            // поэтому туда пишем только персональные вердикты серверов.
            if (line.ServerId != Guid.Empty) fileLog?.Invoke("BATCH " + BatchLogFormatter.Format(entry).TrimEnd());
            if (detachedLogs.TryGetValue(line.ServerId, out var boxes))
                foreach (var box in boxes.ToArray()) { box.AppendText(FormatLog(entry)); box.ScrollToEnd(); }
        }
        RenderLog();
    }

    private void RenderLog()
    {
        if (LogBox is null) return;
        var serverId = (LogServerFilter?.SelectedItem as BatchLogFilter)?.ServerId;
        BatchLogLevel? selectedLevel = null;
        var errorsAndWarningsOnly = false;
        if (LogLevelInfoMenu?.IsChecked == true) selectedLevel = BatchLogLevel.Info;
        else if (LogLevelWarnMenu?.IsChecked == true) errorsAndWarningsOnly = true;
        else if (LogLevelErrorMenu?.IsChecked == true) selectedLevel = BatchLogLevel.Error;

        var consoleOnly = ConsoleOnlyMenu?.IsChecked == true;
        var filtered = BatchLogFormatter.Filter(logs, serverId, selectedLevel, consoleOnly, errorsAndWarningsOnly);
        LogBox.Text = string.Concat(filtered.Select(FormatLog));
        LogBox.ScrollToEnd();
    }

    private void LogLevelOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item) return;
        if (LogLevelAllMenu is not null) LogLevelAllMenu.IsChecked = ReferenceEquals(item, LogLevelAllMenu);
        if (LogLevelInfoMenu is not null) LogLevelInfoMenu.IsChecked = ReferenceEquals(item, LogLevelInfoMenu);
        if (LogLevelWarnMenu is not null) LogLevelWarnMenu.IsChecked = ReferenceEquals(item, LogLevelWarnMenu);
        if (LogLevelErrorMenu is not null) LogLevelErrorMenu.IsChecked = ReferenceEquals(item, LogLevelErrorMenu);
        UpdateLogConfigButtonText();
        RenderLog();
    }

    private void UpdateLogConfigButtonText()
    {
        if (LogConfigButtonText is null) return;
        if (LogLevelErrorMenu?.IsChecked == true) LogConfigButtonText.Text = "Вид журнала: Ошибки";
        else if (LogLevelWarnMenu?.IsChecked == true) LogConfigButtonText.Text = "Вид журнала: Ошибки и предупреждения";
        else if (LogLevelInfoMenu?.IsChecked == true) LogConfigButtonText.Text = "Вид журнала: Info";
        else LogConfigButtonText.Text = "Вид журнала: Все уровни";
    }

    private void LogConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void AnsibleLogLevelOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item) return;
        if (AnsibleLogAllMenu is not null) AnsibleLogAllMenu.IsChecked = ReferenceEquals(item, AnsibleLogAllMenu);
        if (AnsibleLogWarnErrorMenu is not null) AnsibleLogWarnErrorMenu.IsChecked = ReferenceEquals(item, AnsibleLogWarnErrorMenu);
        UpdateAnsibleLogConfigButtonText();
        RenderAnsibleLog();
    }

    private void UpdateAnsibleLogConfigButtonText()
    {
        if (AnsibleLogConfigButtonText is null) return;
        AnsibleLogConfigButtonText.Text = AnsibleLogWarnErrorMenu?.IsChecked == true
            ? "Вид журнала: Ошибки и предупреждения"
            : "Вид журнала: Все сообщения";
    }

    private void AnsibleLogConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void LogViewOption_Click(object sender, RoutedEventArgs e) => RenderLog();

    private void AnsibleLogViewOption_Click(object sender, RoutedEventArgs e) => RenderAnsibleLog();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogBox.Text.Length == 0) return;
        System.Windows.Clipboard.SetText(LogBox.Text);
    }

    private void LogWrapBox_Click(object sender, RoutedEventArgs e)
    {
        if (LogBox is null) return;
        LogBox.TextWrapping = LogWrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private void AnsibleLogWrapBox_Click(object sender, RoutedEventArgs e)
    {
        if (AnsibleLogBox is null) return;
        AnsibleLogBox.TextWrapping = AnsibleLogWrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private void OpenServerLog_Click(object sender, RoutedEventArgs e)
    {
        var selected = (LogServerFilter.SelectedItem as BatchLogFilter)?.ServerId ?? (ServersGrid.SelectedItem as BatchServerChoice)?.Id;
        if (selected is not Guid serverId) { MessageBox.Show(this, "Выберите сервер в фильтре или таблице.", "Журнал"); return; }
        var server = servers.First(x => x.Id == serverId);
        var box = new WpfTextBox
        {
            IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = string.Concat(logs.Where(x => x.ServerId == serverId).Select(FormatLog))
        };
        var copyButton = new System.Windows.Controls.Button { Content = "Копировать", Margin = new Thickness(0, 0, 8, 6) };
        copyButton.Click += (_, _) => { if (box.Text.Length > 0) System.Windows.Clipboard.SetText(box.Text); };
        var wrapCheck = new System.Windows.Controls.CheckBox { Content = "Перенос строк", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        wrapCheck.Click += (_, _) => box.TextWrapping = wrapCheck.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var topBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        topBar.Children.Add(copyButton);
        topBar.Children.Add(wrapCheck);
        var panel = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(topBar, Dock.Top);
        panel.Children.Add(topBar);
        panel.Children.Add(box);
        var window = new Window { Owner = this, Title = "Журнал — " + server.Name, Content = panel, Width = 850, Height = 550 };
        if (!detachedLogs.TryGetValue(serverId, out var boxes)) detachedLogs[serverId] = boxes = [];
        boxes.Add(box); window.Closed += (_, _) => boxes.Remove(box); window.Show();
    }

    private void UpdateServerDisplay(BatchTunnelDefinition tunnel) => tunnel.ServerDisplay = tunnel.ServerIds.Count == 0
        ? "Все выбранные"
        : string.Join(", ", tunnel.ServerIds.Select(id => config.FindServer(id)?.Name ?? "Удалённая сессия"));
    private string FormatLog(BatchTaskLog item) =>
        BatchLogFormatter.Format(item, HideTimeMenu?.IsChecked == true, HidePrefixMenu?.IsChecked == true);
    private static string StatusFrom(BatchTaskLog item) => item.Level == BatchLogLevel.Error ? "Ошибка" :
        item.Source == "Туннель" && item.Message.StartsWith("Работает", StringComparison.Ordinal) ? "Туннель работает" :
        string.IsNullOrEmpty(item.Step) ? item.Message : item.Step + ": " + item.Message;

    private void UpdateStatusCounts()
    {
        if (StatusCounts is null) return;
        var selected = servers.Where(x => x.Selected).ToArray();
        var waiting = selected.Count(x => x.Status == "Ожидает");
        var errors = selected.Count(x => x.Status == "Ошибка");
        var stopped = selected.Count(x => x.Status == "Остановлен");
        var ready = selected.Count(x => x.Status is "Готово" or "Туннель работает");
        var working = selected.Length - waiting - errors - stopped - ready;
        StatusCounts.Text = $"Ожидает: {waiting}; работает: {Math.Max(0, working)}; готово: {ready}; ошибок: {errors}; остановлено: {stopped}";
    }

    private static BatchTaskStep CloneStep(BatchTaskStep item) => BatchStepPolicy.Duplicate(item);

    private static BatchTunnelDefinition CloneTunnel(BatchTunnelDefinition item) => new()
    {
        Name = item.Name, Kind = item.Kind, BindHost = item.BindHost, BindPort = item.BindPort,
        DestinationHost = item.DestinationHost, DestinationPort = item.DestinationPort,
        ServerIds = item.ServerIds.ToList(), ServerDisplay = item.ServerDisplay
    };

    private void CleanupOwnedTaskDirectories()
    {
        foreach (var directory in ownedTaskDirectories)
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        ownedTaskDirectories.Clear();
    }
}

public sealed class BatchServerChoice(Guid id, string name, string host, bool selected)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Host { get; } = host;
    public bool Selected { get; set; } = selected;
    public string Status { get; set; } = selected ? "Ожидает" : "Не выбран";
}

public sealed record BatchLogFilter(Guid? ServerId, string Name)
{
    public override string ToString() => Name;
}

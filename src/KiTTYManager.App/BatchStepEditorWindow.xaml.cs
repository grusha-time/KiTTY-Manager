using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KiTTYManager.Core;
using MessageBox = KiTTYManager.App.ThemedMessageDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace KiTTYManager.App;

public partial class BatchStepEditorWindow : Window
{
    public bool Accepted { get; private set; }
    private sealed record KindChoice(BatchTaskStepKind Kind, string Name, string Help)
    {
        public override string ToString() => Name;
    }

    private static readonly KindChoice[] Choices =
    [
        new(BatchTaskStepKind.Command, "Выполнить команду",
            "Выполняет команду на сервере, как в терминале. Пример: «systemctl restart nginx» или «rpm -Uvh package.rpm». " +
            "Несколько строк выполняются по очереди в одной сессии: cd в первой строке действует на остальные. " +
            "Весь вывод команды попадает в журнал."),
        new(BatchTaskStepKind.Upload, "Загрузить файлы или папки на сервер",
            "Копирует выбранные файлы с ПК на сервер. Каталоги также поддерживаются в импортированных описаниях задач. " +
            "Пример: выбрать config.ini и app.jar и положить в /opt/app/. " +
            "Если путь на сервере — существующий каталог, файлы кладутся внутрь него."),
        new(BatchTaskStepKind.Download, "Скачать файлы или папку с сервера",
            "Скачивает указанные файлы или папки с сервера на ваш ПК — в папку Data\\Downloads рядом с менеджером, " +
            "в подпапку «название сессии адрес сервера». Пример: выгрузить /var/log/app/app.log после обновления, чтобы прочитать логи."),
        new(BatchTaskStepKind.Delete, "Удалить файлы или папку на сервере",
            "Удаляет файлы или папки по точному пути или маске. Пример: /var/log/app/*.log удалит все логи приложения. " +
            "Удаление необратимо — backup не делается."),
        new(BatchTaskStepKind.MakeDirectory, "Создать каталог",
            "Создаёт папку на сервере вместе с недостающими родительскими. Пример: /opt/app/logs."),
        new(BatchTaskStepKind.ReplaceText, "Заменить текст в файле",
            "Находит точный текст в файле на сервере и заменяет его. Пример: в /etc/app/config.ini заменить «port=8080» на «port=9090». " +
            "Если текст не найден — действие завершается ошибкой. Для замены изменившегося файла включите backup: старая версия будет сохранена."),
        new(BatchTaskStepKind.Check, "Проверить условие командой",
            "Выполняет команду-проверку. Если она завершилась с ошибкой (или вывод не содержит ожидаемый текст) — " +
            "действие считается ошибочным. Продолжение определяется флажком некритичной ошибки и режимом реакции задачи. " +
            "Пример: «systemctl is-active nginx» должен вывести «active»."),
        new(BatchTaskStepKind.WaitForText, "Запустить команду, дождаться текста и выполнить другую",
            "Запускает программу в одной SSH-консоли, ждёт точный текст с учётом регистра и знаков, " +
            "отправляет ответ в stdin этой же программы и ждёт её завершения. " +
            "Пример: запустить скрипт, дождаться «Логины:» и отправить строку значений."),
        new(BatchTaskStepKind.InteractiveWaitAndSend, "Диалог с программой: ждать текст → ответить",
            "Открывает на сервере фоновую консоль (можно сразу запустить в ней программу) и ведёт диалог: " +
            "ждёт указанный текст и отправляет ответ. Блоков может быть несколько — например, дождаться «Password:» → отправить пароль, " +
            "потом дождаться «Continue? (y/n)» → отправить «y»."),
    ];

    private readonly ObservableCollection<StepServerChoice> serverChoices = [];
    private readonly ObservableCollection<DialogBlock> dialogBlocks = [];
    private readonly ObservableCollection<string> sourceFiles = [];
    public BatchTaskStep Result { get; private set; }

    public BatchStepEditorWindow(BatchTaskStep? source = null, string? taskDirectory = null,
        IEnumerable<BatchServerChoice>? availableServers = null)
    {
        InitializeComponent();
        Result = source is null ? new() { Kind = BatchTaskStepKind.Command, Name = "Новый шаг" } : Clone(source);
        var choices = Choices.ToList();
        // Устаревшие действия из старых задач можно открыть и отредактировать.
        if (Result.Kind is BatchTaskStepKind.Template or BatchTaskStepKind.InteractiveSend &&
            choices.All(x => x.Kind != Result.Kind))
            choices.Insert(0, Result.Kind == BatchTaskStepKind.Template
                ? new KindChoice(BatchTaskStepKind.Template, "Файл-шаблон с подстановкой (устаревшее)",
                    "Старое действие: подставляет данные сервера в файл-шаблон и загружает результат. Для новых задач используйте «Загрузить на сервер» и «Заменить текст».")
                : new KindChoice(BatchTaskStepKind.InteractiveSend, "Отправить текст программе (устаревшее)",
                    "Старое действие. Для новых задач используйте «Диалог с программой»: он умеет и запускать программу, и ждать ответ."));
        KindBox.ItemsSource = choices;
        KindBox.SelectedItem = choices.First(x => x.Kind == Result.Kind);
        NameBox.Text = Result.Name; CommandBox.Text = Result.Command; ExpectedBox.Text = Result.ExpectedText;
        ActionBox.Text = Result.ActionCommand; TimeoutBox.Text = Result.TimeoutSeconds.ToString();
        foreach (var path in BatchTaskFile.ParseLocalSources(Result.Source)) sourceFiles.Add(path);
        SourceFilesList.ItemsSource = sourceFiles;
        DestinationBox.Text = Result.Destination; DestOnlyBox.Text = Result.Destination;
        DeletePathBox.Text = Result.Destination; SearchBox.Text = Result.Search; ReplacementBox.Text = Result.Replacement;
        ConditionBox.Text = Result.ConditionCommand; StepWorkingDirectoryBox.Text = Result.WorkingDirectory;
        DialogWorkingDirectoryBox.Text = Result.WorkingDirectory;
        BackupBox.IsChecked = source is not null && Result.Backup;
        ContinueOnErrorBox.IsChecked = Result.ContinueOnError;
        WaitAfterBox.Text = Result.WaitAfterSeconds.ToString();
        RunAsUserBox.Text = Result.RunAsUser;
        RunAsPasswordBox.Password = Result.RunAsPassword;
        if (Result.Kind == BatchTaskStepKind.InteractiveWaitAndSend)
        {
            DialogCommandBox.Text = Result.Command;
            var waits = Result.ExpectedText.Replace("\r\n", "\n").Split('\n');
            var replies = Result.ActionCommand.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < Math.Max(waits.Length, replies.Length); i++)
                dialogBlocks.Add(new DialogBlock
                {
                    WaitText = i < waits.Length ? waits[i] : "",
                    ReplyText = i < replies.Length ? replies[i] : ""
                });
        }
        if (Result.Kind == BatchTaskStepKind.Download)
        {
            RemotePathsBox.Text = Result.Source;
            DownloadFolderBox.Text = Result.DownloadFolder;
            DownloadAsArchiveBox.IsChecked = Result.DownloadAsArchive;
        }
        if (dialogBlocks.Count == 0) dialogBlocks.Add(new DialogBlock());
        BlocksList.ItemsSource = dialogBlocks;
        foreach (var server in availableServers ?? [])
            serverChoices.Add(new StepServerChoice(server.Id, server.Name, Result.ServerIds.Contains(server.Id)));
        ServersList.ItemsSource = serverChoices;
        AllServersBox.IsChecked = Result.ServerIds.Count == 0;
        UpdateServerState();
        UpdateFields();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFields();

    private void UpdateFields()
    {
        if (KindBox?.SelectedItem is not KindChoice choice || KindHelp is null) return;
        KindHelp.Text = choice.Help;
        var kind = choice.Kind;
        CommandPanel.Visibility = kind is BatchTaskStepKind.Command or BatchTaskStepKind.Check or BatchTaskStepKind.WaitForText ? Visibility.Visible : Visibility.Collapsed;
        DialogPanel.Visibility = kind == BatchTaskStepKind.InteractiveWaitAndSend ? Visibility.Visible : Visibility.Collapsed;
        ExpectedPanel.Visibility = kind is BatchTaskStepKind.Check or BatchTaskStepKind.WaitForText ? Visibility.Visible : Visibility.Collapsed;
        ActionPanel.Visibility = kind == BatchTaskStepKind.WaitForText ? Visibility.Visible : Visibility.Collapsed;
        SourcePanel.Visibility = kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = kind == BatchTaskStepKind.Download ? Visibility.Visible : Visibility.Collapsed;
        DestOnlyPanel.Visibility = kind is BatchTaskStepKind.MakeDirectory or BatchTaskStepKind.ReplaceText ? Visibility.Visible : Visibility.Collapsed;
        DeletePanel.Visibility = kind == BatchTaskStepKind.Delete ? Visibility.Visible : Visibility.Collapsed;
        ReplacePanel.Visibility = kind == BatchTaskStepKind.ReplaceText ? Visibility.Visible : Visibility.Collapsed;
        SourceFilesList.IsEnabled = kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template;
        PickSourceButton.IsEnabled = SourceFilesList.IsEnabled;
        PickSourceButton.ToolTip = "Откроется стандартное окно выбора одного или нескольких файлов";
        ServersPanel.Visibility = kind == BatchTaskStepKind.InteractiveWaitAndSend ? Visibility.Collapsed : Visibility.Visible;
        BackupBox.Visibility = kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Download or BatchTaskStepKind.ReplaceText or BatchTaskStepKind.Template ? Visibility.Visible : Visibility.Collapsed;
        BackupBox.Content = kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Download
            ? "Если файл уже есть — всё равно передать его и сделать backup"
            : "Сделать backup изменяемого файла";
    }

    private void AddBlock_Click(object sender, RoutedEventArgs e) => dialogBlocks.Add(new DialogBlock());

    private void RemoveBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: DialogBlock block }) dialogBlocks.Remove(block);
        if (dialogBlocks.Count == 0) dialogBlocks.Add(new DialogBlock());
    }

    private void AllServersBox_Changed(object sender, RoutedEventArgs e) => UpdateServerState();
    private void UpdateServerState() { if (ServersList is not null) ServersList.IsEnabled = AllServersBox.IsChecked != true; }

    private void ServerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ServerSearchBox.Text.Trim();
        System.Windows.Data.CollectionViewSource.GetDefaultView(ServersList.ItemsSource).Filter = item => item is StepServerChoice x &&
            (string.IsNullOrEmpty(query) || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ServersList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (AllServersBox.IsChecked == true ||
            ItemsControl.ContainerFromElement(ServersList, e.OriginalSource as DependencyObject) is not ListBoxItem row ||
            row.Content is not StepServerChoice selected) return;
        selected.Selected = !selected.Selected; ServersList.Items.Refresh();
    }

    private void PickSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите файлы для загрузки", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames.Select(Path.GetFullPath))
            if (!sourceFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) sourceFiles.Add(path);
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFilesList.SelectedItem is string selected) sourceFiles.Remove(selected);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (KindBox.SelectedItem is not KindChoice choice) return;
        if (AllServersBox.IsChecked != true && !serverChoices.Any(x => x.Selected))
        {
            MessageBox.Show(this, "Отметьте хотя бы один сервер или включите «На всех серверах задачи».",
                "Проверьте шаг", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var kind = choice.Kind;
        string command = CommandBox.Text, expected = ExpectedBox.Text, action = ActionBox.Text,
            source = BatchTaskFile.SerializeLocalSources(sourceFiles);
        var destination = kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template
            ? DestinationBox.Text.Trim()
            : DestOnlyBox.Text.Trim();
        if (kind == BatchTaskStepKind.InteractiveWaitAndSend)
        {
            command = DialogCommandBox.Text;
            expected = string.Join("\n", dialogBlocks.Select(x => x.WaitText.TrimEnd()));
            action = string.Join("\n", dialogBlocks.Select(x => x.ReplyText));
            if (dialogBlocks.All(x => string.IsNullOrWhiteSpace(x.WaitText) && string.IsNullOrWhiteSpace(x.ReplyText)))
            {
                MessageBox.Show(this, "Добавьте хотя бы один блок «ждать текст → ответить».",
                    "Проверьте шаг", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (kind == BatchTaskStepKind.Download) source = RemotePathsBox.Text;
        var downloadFolder = kind == BatchTaskStepKind.Download ? DownloadFolderBox.Text.Trim() : "";
        if (kind == BatchTaskStepKind.Delete) destination = DeletePathBox.Text.Trim();
        var waitForExit = true;
        Result = new()
        {
            Kind = kind, Name = NameBox.Text.Trim(), Command = command, ExpectedText = expected,
            ActionCommand = action, TimeoutSeconds = int.TryParse(TimeoutBox.Text, out var timeout) ? timeout : 0,
            Source = source, Destination = destination, Search = SearchBox.Text,
            Replacement = ReplacementBox.Text, ConditionCommand = ConditionBox.Text,
            RunAsUser = RunAsUserBox.Text.Trim(), RunAsPassword = RunAsPasswordBox.Password,
            Backup = BackupBox.IsChecked == true, ContinueOnError = ContinueOnErrorBox.IsChecked == true,
            WaitAfterSeconds = int.TryParse(WaitAfterBox.Text, out var waitAfter) ? Math.Max(0, waitAfter) : 0,
            DownloadFolder = downloadFolder,
            DownloadAsArchive = kind == BatchTaskStepKind.Download && DownloadAsArchiveBox.IsChecked == true,
            WaitForExit = waitForExit,
            WorkingDirectory = (kind == BatchTaskStepKind.InteractiveWaitAndSend
                ? DialogWorkingDirectoryBox.Text : StepWorkingDirectoryBox.Text).Trim(),
            ServerIds = AllServersBox.IsChecked == true ? [] : serverChoices.Where(x => x.Selected).Select(x => x.Id).ToList()
        };
        try { BatchTaskFile.Validate(new() { Name = "Проверка", Steps = [Result] }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверьте шаг", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static BatchTaskStep Clone(BatchTaskStep x) => new()
    {
        Kind = x.Kind, Name = x.Name, Command = x.Command, Source = x.Source, Destination = x.Destination,
        Search = x.Search, Replacement = x.Replacement, ExpectedText = x.ExpectedText,
        ConditionCommand = x.ConditionCommand, ActionCommand = x.ActionCommand, TimeoutSeconds = x.TimeoutSeconds,
        Become = x.Become, RunAsUser = x.RunAsUser, RunAsPassword = x.RunAsPassword,
        Backup = x.Backup, ContinueOnError = x.ContinueOnError,
        WaitAfterSeconds = x.WaitAfterSeconds, DownloadFolder = x.DownloadFolder,
        DownloadAsArchive = x.DownloadAsArchive, WaitForExit = x.WaitForExit,
        ServerIds = x.ServerIds.ToList(), WorkingDirectory = x.WorkingDirectory
    };
}

public sealed class DialogBlock
{
    public string WaitText { get; set; } = "";
    public string ReplyText { get; set; } = "";
}

public sealed class StepServerChoice(Guid id, string name, bool selected)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool Selected { get; set; } = selected;
}

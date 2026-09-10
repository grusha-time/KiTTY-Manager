using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class QuickDuplicateDialog : Window
{
    private readonly ManagerConfig config;
    private readonly ManagedServer source;
    public ManagedServer Draft { get; }
    public string InitiallyGeneratedName { get; }
    public event EventHandler? Saved;
    public IReadOnlyList<Guid> SelectedServerIds => BuildLinksBox.IsChecked == true
        ? LinkServerSelector.SelectedIds.ToArray() : [];

    public QuickDuplicateDialog(ManagerConfig config, ManagedServer source)
    {
        this.config = config;
        this.source = source;
        Draft = ManagedServerDuplicator.CreateQuickDuplicate(config, source);
        InitiallyGeneratedName = Draft.Name;
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();
        LoadDraft();
        BuildLinksBox.IsChecked = true;
        var entryIds = QuickDuplicatePolicy.DefaultLinkSourceIds(config, source);
        LinkServerSelector.Configure(config, entryIds);
        UpdateTreeEnabled();
    }

    private void LoadDraft()
    {
        NameBox.Text = Draft.Name; HostBox.Text = Draft.Host; PortBox.Text = Draft.Port.ToString();
        UsernameBox.Text = Draft.Username; PasswordBox.Value = Draft.Password;
        RootLoginBox.Text = Draft.RootLogin; RootPasswordBox.Value = Draft.RootPassword;
        PrivateKeyBox.Text = Draft.PrivateKeyPath; PrivateKeyPassphraseBox.Value = Draft.PrivateKeyPassphrase;
        KeyboardInteractiveBox.IsChecked = Draft.UseKeyboardInteractive; TryDirectBox.IsChecked = Draft.TryDirectWithoutJumphost;
        IgnoreCommandBox.IsChecked = Draft.IgnoreImportedCommand; ShellPromptBox.Text = Draft.ShellPrompt;
        ImportedCommandBox.Text = Draft.ImportedCommand;
        RequiredPreviousBox.ItemsSource = new[] { new ServerChoice(null, "Не задан") }
            .Concat(config.AllServers().Where(server => server.Id != source.Id).Select(server => new ServerChoice(server.Id, server.Name))).ToArray();
        RequiredPreviousBox.SelectedValue = Draft.RequiredPreviousServerId;
        BackupGrid.ItemsSource = Draft.BackupEndpoints;
        WebGrid.ItemsSource = Draft.WebInterfaces;
    }

    private void BuildLinksBox_Changed(object sender, RoutedEventArgs e) => UpdateTreeEnabled();
    private void UpdateTreeEnabled() { if (LinkServerSelector is not null) LinkServerSelector.IsEnabled = BuildLinksBox.IsChecked == true; }

    private void AddBackup_Click(object sender, RoutedEventArgs e)
    {
        Draft.BackupEndpoints.Add(new ServerEndpoint("", 22));
        RefreshGrid(BackupGrid, Draft.BackupEndpoints[^1]);
    }

    private void RemoveBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupGrid.SelectedItem is not ServerEndpoint endpoint) return;
        Draft.BackupEndpoints.Remove(endpoint);
        RefreshGrid(BackupGrid);
    }

    private void AddWeb_Click(object sender, RoutedEventArgs e)
    {
        Draft.WebInterfaces.Add(new WebInterface());
        RefreshGrid(WebGrid, Draft.WebInterfaces[^1]);
    }

    private void RemoveWeb_Click(object sender, RoutedEventArgs e)
    {
        if (WebGrid.SelectedItem is not WebInterface web) return;
        Draft.WebInterfaces.Remove(web);
        RefreshGrid(WebGrid);
    }

    private static void RefreshGrid(System.Windows.Controls.DataGrid grid, object? selected = null)
    {
        var source = grid.ItemsSource;
        grid.ItemsSource = null;
        grid.ItemsSource = source;
        grid.SelectedItem = selected;
        if (selected is not null) grid.ScrollIntoView(selected);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ShowError("Укажите название."); return; }
        if (string.IsNullOrWhiteSpace(HostBox.Text)) { ShowError("Укажите IP или host."); return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { ShowError("SSH-порт должен быть от 1 до 65535."); return; }
        if (Draft.BackupEndpoints.Any(endpoint => endpoint.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(endpoint.Host)))
        { ShowError("У каждого резервного адреса должны быть host и порт от 1 до 65535."); return; }
        Draft.Name = NameBox.Text.Trim(); Draft.Host = HostBox.Text.Trim(); Draft.Port = port;
        Draft.Username = UsernameBox.Text.Trim(); Draft.Password = PasswordBox.Value;
        Draft.RootLogin = RootLoginBox.Text.Trim(); Draft.RootPassword = RootPasswordBox.Value;
        Draft.PrivateKeyPath = PrivateKeyBox.Text.Trim(); Draft.PrivateKeyPassphrase = PrivateKeyPassphraseBox.Value;
        Draft.UseKeyboardInteractive = KeyboardInteractiveBox.IsChecked == true; Draft.TryDirectWithoutJumphost = TryDirectBox.IsChecked == true;
        Draft.IgnoreImportedCommand = IgnoreCommandBox.IsChecked == true; Draft.ShellPrompt = ShellPromptBox.Text;
        Draft.ImportedCommand = ImportedCommandBox.Text; Draft.RequiredPreviousServerId = RequiredPreviousBox.SelectedValue as Guid?;
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void ShowError(string message) => ThemedMessageDialog.Show(this, message, "Быстрый дубль", MessageBoxButton.OK, MessageBoxImage.Warning);
    private sealed record ServerChoice(Guid? Id, string Name);
}

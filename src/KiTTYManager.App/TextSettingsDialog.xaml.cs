using System.Windows;

namespace KiTTYManager.App;

public partial class TextSettingsDialog : Window
{
    public string KittyPath => KittyBox.Text.Trim();
    public string FirefoxPath => FirefoxBox.Text.Trim();
    public string WinScpPath => WinScpBox.Text.Trim();
    public string TemplateProfile => TemplateProfileBox.Text.Trim();
    public bool AutoDiscoverFirefoxProfile => AutoDiscoverFirefoxProfileBox.IsChecked == true;
    public bool CloseToTray => CloseToTrayBox.IsChecked == true;
    public bool EnableLogging => EnableLoggingBox.IsChecked == true;
    public bool WriteChangesImmediatelyToKitty => WriteToKittyBox.IsChecked == true;
    public bool CloseWebTunnelWithFirefox => CloseWebTunnelBox.IsChecked == true;
    public bool AutoConfirmHostKeys => AutoConfirmHostKeysBox.IsChecked == true;
    public bool SuppressKittyChangeNotifications => SuppressKittyChangesBox.IsChecked == true;
    public bool RaceBestEntryPoints => RaceBestEntryPointsBox.IsChecked == true;
    public bool SkipExistingLinksInMapCheck => SkipExistingLinksInMapCheckBox.IsChecked == true;
    public bool OfferStartMissingJumphosts => OfferStartMissingJumphostsBox.IsChecked == true;
    public int ConnectionTimeoutSeconds { get; private set; } = 10;
    public int EndpointProbeTimeoutSeconds { get; private set; } = 4;
    public int TaskConnectionRecoveryMinutes { get; private set; } = 1;
    public TextSettingsDialog(string kittyPath, string firefoxPath, bool closeToTray,
        bool enableLogging = false, int connectionTimeoutSeconds = 10, int endpointProbeTimeoutSeconds = 4,
        bool writeChangesImmediatelyToKitty = false,
        bool closeWebTunnelWithFirefox = false, bool autoDiscoverFirefoxProfile = true,
        string templateProfile = "", bool autoConfirmHostKeys = true,
        bool suppressKittyChangeNotifications = true, bool raceBestEntryPoints = false,
        bool skipExistingLinksInMapCheck = true,
        string winScpPath = "", bool offerStartMissingJumphosts = true,
        int taskConnectionRecoveryMinutes = 1)
    {
        InitializeComponent();
        Width = Math.Min(760, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        Height = Math.Min(680, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        KittyBox.Text = kittyPath; FirefoxBox.Text = firefoxPath;
        WinScpBox.Text = winScpPath;
        TemplateProfileBox.Text = templateProfile;
        AutoDiscoverFirefoxProfileBox.IsChecked = autoDiscoverFirefoxProfile;
        UpdateTemplateControls();
        ConnectionTimeoutBox.Text = connectionTimeoutSeconds.ToString(); CloseToTrayBox.IsChecked = closeToTray;
        EndpointProbeTimeoutBox.Text = endpointProbeTimeoutSeconds.ToString();
        TaskConnectionRecoveryBox.Text = taskConnectionRecoveryMinutes.ToString();
        EnableLoggingBox.IsChecked = enableLogging;
        WriteToKittyBox.IsChecked = writeChangesImmediatelyToKitty;
        CloseWebTunnelBox.IsChecked = closeWebTunnelWithFirefox;
        AutoConfirmHostKeysBox.IsChecked = autoConfirmHostKeys;
        SuppressKittyChangesBox.IsChecked = suppressKittyChangeNotifications;
        RaceBestEntryPointsBox.IsChecked = raceBestEntryPoints;
        SkipExistingLinksInMapCheckBox.IsChecked = skipExistingLinksInMapCheck;
        OfferStartMissingJumphostsBox.IsChecked = offerStartMissingJumphosts;
    }
    private void AutoDiscoverFirefoxProfile_Changed(object sender, RoutedEventArgs e) => UpdateTemplateControls();
    private void UpdateTemplateControls()
    {
        if (TemplateProfileBox is null) return;
        var manual = AutoDiscoverFirefoxProfileBox.IsChecked != true;
        TemplateProfileBox.IsEnabled = manual;
        BrowseTemplateButton.IsEnabled = manual;
    }
    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку шаблона профиля Firefox",
            ShowNewFolderButton = false
        };
        if (TemplateProfileBox.Text.Trim().Length > 0 && System.IO.Directory.Exists(TemplateProfileBox.Text.Trim()))
            dialog.SelectedPath = TemplateProfileBox.Text.Trim();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TemplateProfileBox.Text = dialog.SelectedPath;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (KittyPath.Length == 0 || FirefoxPath.Length == 0) { ThemedMessageDialog.Show(this, "Заполните все поля.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(ConnectionTimeoutBox.Text.Trim(), out var timeout) || timeout is < 3 or > 600)
        {
            ThemedMessageDialog.Show(this, "Укажите таймаут от 3 до 600 секунд.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            ConnectionTimeoutBox.Focus();
            ConnectionTimeoutBox.SelectAll();
            return;
        }
        ConnectionTimeoutSeconds = timeout;
        if (!int.TryParse(EndpointProbeTimeoutBox.Text.Trim(), out var probeTimeout) ||
            probeTimeout is < 1 or > 30)
        {
            ThemedMessageDialog.Show(this, "Укажите лимит зонда от 1 до 30 секунд.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            EndpointProbeTimeoutBox.Focus();
            EndpointProbeTimeoutBox.SelectAll();
            return;
        }
        EndpointProbeTimeoutSeconds = probeTimeout;
        if (!int.TryParse(TaskConnectionRecoveryBox.Text.Trim(), out var recoveryMinutes) || recoveryMinutes is < 0 or > 99999)
        {
            ThemedMessageDialog.Show(this, "Укажите ожидание восстановления связи от 0 до 99999 минут.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            TaskConnectionRecoveryBox.Focus(); TaskConnectionRecoveryBox.SelectAll(); return;
        }
        TaskConnectionRecoveryMinutes = recoveryMinutes;
        DialogResult = true;
    }
}

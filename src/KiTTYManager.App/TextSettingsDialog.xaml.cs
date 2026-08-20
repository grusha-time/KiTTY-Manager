using System.Windows;

namespace KiTTYManager.App;

public partial class TextSettingsDialog : Window
{
    public string KittyPath => KittyBox.Text.Trim();
    public string FirefoxPath => FirefoxBox.Text.Trim();
    public string Profile => ProfileBox.Text.Trim();
    public string TemplateProfile => TemplateProfileBox.Text.Trim();
    public bool CloseToTray => CloseToTrayBox.IsChecked == true;
    public bool EnableLogging => EnableLoggingBox.IsChecked == true;
    public bool WriteChangesImmediatelyToKitty => WriteToKittyBox.IsChecked == true;
    public bool CloseWebTunnelWithFirefox => CloseWebTunnelBox.IsChecked == true;
    public bool TemporaryFirefoxProfiles => TemporaryFirefoxProfilesBox.IsChecked == true;
    public bool ShareFirefoxProfileByGroup => ShareFirefoxProfileByGroupBox.IsChecked == true;
    public bool UseInternalWebResolver => UseInternalWebResolverBox.IsChecked == true;
    public bool AutoConfirmHostKeys => AutoConfirmHostKeysBox.IsChecked == true;
    public bool SuppressKittyChangeNotifications => SuppressKittyChangesBox.IsChecked == true;
    public bool RaceBestEntryPoints => RaceBestEntryPointsBox.IsChecked == true;
    public bool SkipExistingLinksInMapCheck => SkipExistingLinksInMapCheckBox.IsChecked == true;
    public int ConnectionTimeoutSeconds { get; private set; } = 60;
    public int EndpointProbeTimeoutSeconds { get; private set; } = 4;
    public TextSettingsDialog(string kittyPath, string firefoxPath, string profileName, bool closeToTray,
        bool enableLogging = false, int connectionTimeoutSeconds = 60, int endpointProbeTimeoutSeconds = 4,
        bool writeChangesImmediatelyToKitty = false,
        bool closeWebTunnelWithFirefox = false, bool temporaryFirefoxProfiles = true,
        bool shareFirefoxProfileByGroup = false, string templateProfile = "", bool autoConfirmHostKeys = true,
        bool suppressKittyChangeNotifications = true, bool raceBestEntryPoints = false,
        bool skipExistingLinksInMapCheck = true, bool useInternalWebResolver = true)
    {
        InitializeComponent();
        Width = Math.Min(760, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        Height = Math.Min(680, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        KittyBox.Text = kittyPath; FirefoxBox.Text = firefoxPath; ProfileBox.Text = profileName;
        TemplateProfileBox.Text = templateProfile;
        ConnectionTimeoutBox.Text = connectionTimeoutSeconds.ToString(); CloseToTrayBox.IsChecked = closeToTray;
        EndpointProbeTimeoutBox.Text = endpointProbeTimeoutSeconds.ToString();
        EnableLoggingBox.IsChecked = enableLogging;
        WriteToKittyBox.IsChecked = writeChangesImmediatelyToKitty;
        CloseWebTunnelBox.IsChecked = closeWebTunnelWithFirefox;
        TemporaryFirefoxProfilesBox.IsChecked = temporaryFirefoxProfiles;
        ShareFirefoxProfileByGroupBox.IsChecked = shareFirefoxProfileByGroup;
        UseInternalWebResolverBox.IsChecked = useInternalWebResolver;
        AutoConfirmHostKeysBox.IsChecked = autoConfirmHostKeys;
        SuppressKittyChangesBox.IsChecked = suppressKittyChangeNotifications;
        RaceBestEntryPointsBox.IsChecked = raceBestEntryPoints;
        SkipExistingLinksInMapCheckBox.IsChecked = skipExistingLinksInMapCheck;
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
        if (KittyPath.Length == 0 || FirefoxPath.Length == 0 || Profile.Length == 0) { ThemedMessageDialog.Show(this, "Заполните все поля.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(ConnectionTimeoutBox.Text.Trim(), out var timeout) || timeout is < 10 or > 600)
        {
            ThemedMessageDialog.Show(this, "Укажите таймаут от 10 до 600 секунд.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        DialogResult = true;
    }
}

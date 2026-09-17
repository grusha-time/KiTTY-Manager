using System.IO;
using System.Reflection;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class UpdateDialog : Window
{
    private readonly AppUpdateService updateService;
    private readonly Func<AppReleaseInfo, string, Task<UpdateExecutionSession>>? installAction;
    private AppReleaseInfo? currentRelease;
    private CancellationTokenSource? activeCts;

    public UpdateExecutionSession? StartedSession { get; private set; }

    public UpdateDialog(
        AppReleaseInfo? initialRelease = null,
        AppUpdateService? customService = null,
        Func<AppReleaseInfo, string, Task<UpdateExecutionSession>>? customInstallAction = null)
    {
        InitializeComponent();
        updateService = customService ?? new AppUpdateService();
        installAction = customInstallAction;

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? ProductInfo.Version;
        CurrentVersionText.Text = currentVersion;

        if (initialRelease is not null)
        {
            DisplayRelease(initialRelease);
        }
        else
        {
            AvailableVersionText.Text = "Проверка ещё не выполнялась";
            ChangelogBox.Text = "Нажмите «Проверить наличие обновлений» для получения сведений о последнем релизе.";
            InstallButton.Visibility = Visibility.Collapsed;
        }

        Closed += (_, _) =>
        {
            activeCts?.Cancel();
            activeCts?.Dispose();
        };
    }

    private void DisplayRelease(AppReleaseInfo release)
    {
        currentRelease = release;
        var currentVersion = ProductInfo.Version;

        var releaseDisplay = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name;
        if (release.PublishedAt.HasValue)
        {
            releaseDisplay += $" (от {release.PublishedAt.Value.ToLocalTime():dd.MM.yyyy})";
        }
        AvailableVersionText.Text = releaseDisplay;
        ChangelogBox.Text = string.IsNullOrWhiteSpace(release.Body)
            ? "Описание изменений отсутствует в релизе."
            : release.Body;

        if (release.IsNewerThan(currentVersion))
        {
            InstallButton.Content = "Установить обновление";
            InstallButton.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"Доступна новая версия {release.TagName}. Нажмите «Установить обновление» для загрузки и установки.";
        }
        else if (release.IsSameVersionAs(currentVersion))
        {
            InstallButton.Content = "Переустановить обновление";
            InstallButton.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"Установлена актуальная версия ({currentVersion}). Вы можете переустановить приложение из текущего релиза.";
        }
        else
        {
            InstallButton.Visibility = Visibility.Collapsed;
            StatusTextBlock.Text = $"Установлена версия ({currentVersion}), которая новее последнего официального релиза ({release.TagName}).";
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        activeCts?.Cancel();
        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        var token = activeCts.Token;

        CheckButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "Проверка наличия обновлений на GitHub...";

        try
        {
            var release = await updateService.GetLatestReleaseAsync(token: token);
            DisplayRelease(release);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Проверка обновлений отменена.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Не удалось проверить обновления: {ActionableErrorFormatter.Format(ex)}";
            ThemedMessageDialog.Show(this,
                $"Не удалось получить сведения об обновлениях:\n\n{ActionableErrorFormatter.Format(ex)}",
                "Проверка обновлений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CheckButton.IsEnabled = true;
            InstallButton.IsEnabled = currentRelease is not null;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentRelease is null) return;

        activeCts?.Cancel();
        activeCts?.Dispose();
        activeCts = new CancellationTokenSource();
        var token = activeCts.Token;

        CheckButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;

        var assetName = currentRelease.Asset.Name;
        var tempFolder = Path.Combine(Path.GetTempPath(), "KiTTYManager-Updates");
        Directory.CreateDirectory(tempFolder);
        var zipPath = Path.Combine(tempFolder, assetName);

        try
        {
            StatusTextBlock.Text = $"Скачивание {assetName}...";

            await updateService.DownloadAssetAsync(
                currentRelease.Asset,
                zipPath,
                progress: (read, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (total.HasValue && total.Value > 0)
                        {
                            var pct = (double)read / total.Value * 100.0;
                            UpdateProgressBar.Value = Math.Min(100.0, Math.Max(0.0, pct));
                            var mbRead = read / (1024.0 * 1024.0);
                            var mbTotal = total.Value / (1024.0 * 1024.0);
                            StatusTextBlock.Text = $"Скачивание: {mbRead:F1} МБ из {mbTotal:F1} МБ ({pct:F0}%)";
                        }
                        else
                        {
                            UpdateProgressBar.IsIndeterminate = true;
                            var mbRead = read / (1024.0 * 1024.0);
                            StatusTextBlock.Text = $"Скачивание: {mbRead:F1} МБ...";
                        }
                    });
                },
                token: token);

            StatusTextBlock.Text = "Проверка целостности архива...";
            var validation = AppUpdatePackage.ValidateArchive(zipPath);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(validation.ErrorMessage ?? "Архив не прошёл проверку безопасности.");
            }

            StatusTextBlock.Text = "Подготовка процедуры безопасного обновления...";

            UpdateExecutionSession session;
            if (installAction is not null)
            {
                session = await installAction(currentRelease, zipPath);
            }
            else
            {
                session = AppUpdateInstaller.PrepareAndLaunchHelper(zipPath, AppContext.BaseDirectory);
            }

            StartedSession = session;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Обновление отменено.";
            CheckButton.IsEnabled = true;
            InstallButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка обновления: {ActionableErrorFormatter.Format(ex)}";
            ThemedMessageDialog.Show(this,
                $"Не удалось выполнить обновление:\n\n{ActionableErrorFormatter.Format(ex)}\n\nРаботающее приложение и пользовательские данные не затронуты.",
                "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);

            CheckButton.IsEnabled = true;
            InstallButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

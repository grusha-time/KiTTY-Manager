using System.IO;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class MainWindow
{
    private AppReleaseInfo? discoveredUpdateRelease;
    private readonly SemaphoreSlim updateCheckLock = new(1, 1);
    private CancellationTokenSource? updateSchedulerCancellation;
    private UpdateExecutionSession? pendingUpdateSession;

    private void InitializeUpdateScheduler()
    {
        _ = Task.Run(AppUpdateInstaller.CleanupOldUpdateArtifacts);
        updateSchedulerCancellation = new CancellationTokenSource();
        _ = RunUpdateSchedulerAsync(updateSchedulerCancellation.Token);
    }

    private void ShowUpdateDialog()
    {
        var dialog = new UpdateDialog(discoveredUpdateRelease) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.StartedSession is not null)
        {
            pendingUpdateSession = dialog.StartedSession;
            forceExit = true;
            Close();
        }
    }

    private async Task RunUpdateSchedulerAsync(CancellationToken token)
    {
        // Initial delay after startup so application initializes without network contention
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lastCheckFailed = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                lastCheckFailed = !await CheckForUpdatesSilentlyAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                lastCheckFailed = true;
            }

            var nextTime = AppUpdatePolicy.CalculateNextScheduledTime(DateTime.Now, lastCheckFailed);
            var delay = nextTime - DateTime.Now;
            if (delay < TimeSpan.FromSeconds(10))
                delay = TimeSpan.FromSeconds(10);

            try
            {
                // Sleep until next scheduled time, evaluating periodically for sleep/DST/clock changes
                var delayLeft = delay;
                while (delayLeft > TimeSpan.Zero && !token.IsCancellationRequested)
                {
                    var chunk = delayLeft > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delayLeft;
                    await Task.Delay(chunk, token);
                    if (AppUpdatePolicy.IsCheckDue(DateTime.Now, nextTime))
                        break;
                    delayLeft = nextTime - DateTime.Now;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<bool> CheckForUpdatesSilentlyAsync(CancellationToken token)
    {
        if (!await updateCheckLock.WaitAsync(0, token))
            return true; // Check already running

        try
        {
            using var service = new AppUpdateService();
            var release = await service.GetLatestReleaseAsync(token: token);
            discoveredUpdateRelease = release;

            if (release.IsNewerThan(ProductInfo.Version))
            {
                if (!config.HasNotifiedUpdateVersion(release.TagName))
                {
                    // Persist state before showing notification (at most once across restarts)
                    config.RecordNotifiedUpdateVersion(release.TagName);
                    try { SaveConfig(); } catch { }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (Dispatcher.HasShutdownStarted) return;
                        ThemedMessageDialog.Show(this,
                            $"Доступна новая версия KiTTY Manager ({release.TagName}).\n\nВы можете ознакомиться со списком изменений и установить её через меню «Обновление KiTTY Manager».",
                            "Обновление KiTTY Manager",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            RouteLog($"[UpdateCheck] Ошибка проверки обновлений: {ex.Message}");
            return false;
        }
        finally
        {
            updateCheckLock.Release();
        }
    }
}

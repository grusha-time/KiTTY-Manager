using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class MainWindow
{
    // Separate from the old disposable runtime tree. Never cleaned up automatically.
    private string FirefoxContainerRoot => Path.Combine(dataDirectory, "FirefoxContainers");
    private FirefoxContainerBrowser? containerBrowser;
    private readonly Dictionary<string, ContainerWebSession> containerSessions = [];
    private readonly DispatcherTimer containerWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim firefoxStartupLock = new(1, 1);
    private readonly HashSet<string> pendingWebOpenings = [];
    private CancellationTokenSource webCts = new();
    private bool webExitPending;
    private bool webExitCompleted;
    private bool tunnelsExitConfirmed;
    private sealed record ContainerWebSession(Process Tunnel, ActiveRoute Route, ResolvingSocks5Relay Relay, IReadOnlyList<string> Domains);

    private async Task EnsureFirefoxContainersAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        await firefoxStartupLock.WaitAsync(token);
        try
        {
            if (containerBrowser?.IsRunning == true)
            {
                if (!containerBrowser.Bridge.Connected)
                    throw new IOException("Расширение Firefox не отвечает. Закройте его окна и повторите.");
                return;
            }
            StopFirefoxContainers();
            containerBrowser = new FirefoxContainerBrowser();
            var current = containerBrowser;
            current.Bridge.Trace = RouteLog;
            current.Bridge.Closed += key => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(containerBrowser, current)) CloseContainerSession(key, false);
            });
            current.Bridge.PurgeAcknowledged += id => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(containerBrowser, current)) OnFirefoxContainerPurgeAcknowledged(id);
            });
            if (config.FirefoxCleanRemovedServerContainers)
            {
                foreach (var task in config.PendingFirefoxContainerCleanups.ToArray())
                {
                    if (config.FindServer(task.ServerId) is not null && !task.WebId.HasValue)
                    {
                        config.PendingFirefoxContainerCleanups.Remove(task);
                        continue;
                    }
                    var pattern = task.WebId.HasValue
                        ? $"{task.ServerId:N}-{task.WebId.Value:N}"
                        : $"{task.ServerId:N}-";
                    current.Bridge.EnqueuePurge(task.Id, pattern);
                }
            }
            containerWatch.Tick -= WatchFirefoxContainers;
            containerWatch.Tick += WatchFirefoxContainers;
            var firefoxExe = ResolveProgram(config.FirefoxPath);
            var root = FirefoxContainerRoot;
            var autoDiscover = config.AutoDiscoverFirefoxProfile;
            var templateProfile = config.FirefoxTemplateProfile;
            var appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Func<string> sourceResolver = () => autoDiscover
                ? FirefoxProfileWorkspace.DiscoverDefaultProfile(appDataDir)
                : templateProfile;
            var optRam = config.FirefoxOptimizeRamCache;
            var noSafeBrowsing = config.FirefoxDisableSafeBrowsing;
            var noHistory = config.FirefoxDisableHistoryAndIcons;
            var clearCache = config.FirefoxClearCacheOnShutdown;
            var acceptInsecure = config.FirefoxAcceptInsecureCerts;
            try
            {
                await Task.Run(async () =>
                {
                    await current.StartAsync(firefoxExe, root, sourceResolver, token,
                        headless: false,
                        optimizeRamCache: optRam,
                        disableSafeBrowsing: noSafeBrowsing,
                        disableHistoryAndIcons: noHistory,
                        clearCacheOnShutdown: clearCache,
                        acceptInsecureCerts: acceptInsecure);
                }, token);
                containerWatch.Start();
            }
            catch
            {
                current.Dispose();
                containerBrowser = null;
                throw;
            }
        }
        finally
        {
            firefoxStartupLock.Release();
        }
    }

    private void WatchFirefoxContainers(object? sender, EventArgs e)
    {
        Dispatcher.VerifyAccess();
        if (containerBrowser is null) return;
        if (!containerBrowser.IsRunning || !containerBrowser.Bridge.Connected)
        {
            foreach (var key in containerSessions.Keys.ToArray()) CloseContainerSession(key, true);
            return;
        }
        foreach (var pair in containerSessions.ToArray())
            if (pair.Value.Tunnel.HasExited) CloseContainerSession(pair.Key, true);
    }

    private void CloseContainerSession(string key, bool force)
    {
        Dispatcher.VerifyAccess();
        containerBrowser?.Bridge.Remove(key);
        if (!containerSessions.Remove(key, out var session)) return;
        session.Relay.Dispose();
        if (force || config.CloseWebTunnelWithFirefox)
        {
            StopProcess(session.Tunnel);
            ReleaseRoute(session.Route);
        }
        else session.Tunnel.Dispose();
        RouteLog($"Firefox container disconnected: key={key}; profile retained");
    }

    private void StopFirefoxContainers()
    {
        Dispatcher.VerifyAccess();
        containerWatch.Stop();
        foreach (var key in containerSessions.Keys.ToArray()) CloseContainerSession(key, true);
        containerBrowser?.Dispose();
        containerBrowser = null;
    }

    private void OnFirefoxContainerPurgeAcknowledged(string id)
    {
        Dispatcher.VerifyAccess();
        var removed = config.PendingFirefoxContainerCleanups.RemoveAll(t => t.Id == id);
        if (removed > 0)
        {
            SaveConfig();
            RouteLog($"Firefox container purge acknowledged: taskId={id}");
        }
    }

    private void RequestContainerCleanup(Guid serverId, Guid? webId = null)
    {
        Dispatcher.VerifyAccess();
        if (!config.FirefoxCleanRemovedServerContainers) return;
        var prefix = webId.HasValue ? $"{serverId:N}-{webId.Value:N}" : $"{serverId:N}-";
        foreach (var key in containerSessions.Keys.ToArray())
        {
            if (webId.HasValue ? key == prefix : key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                CloseContainerSession(key, true);
            }
        }
        var task = new FirefoxContainerCleanupTask
        {
            Id = Guid.NewGuid().ToString("N"),
            ServerId = serverId,
            WebId = webId
        };
        config.PendingFirefoxContainerCleanups.Add(task);
        SaveConfig();
        containerBrowser?.Bridge.EnqueuePurge(task.Id, prefix);
        RouteLog($"Firefox container cleanup queued: server={serverId:N}; web={webId}; taskId={task.Id}");
    }
}

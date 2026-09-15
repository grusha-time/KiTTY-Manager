using System.Diagnostics;
using System.IO;
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
    private sealed record ContainerWebSession(Process Tunnel, ActiveRoute Route, ResolvingSocks5Relay Relay);

    private async Task EnsureFirefoxContainersAsync(CancellationToken token)
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
        try
        {
            await current.StartAsync(ResolveProgram(config.FirefoxPath), FirefoxContainerRoot, FirefoxSourceProfile, token,
                headless: false,
                optimizeRamCache: config.FirefoxOptimizeRamCache,
                disableSafeBrowsing: config.FirefoxDisableSafeBrowsing,
                disableHistoryAndIcons: config.FirefoxDisableHistoryAndIcons,
                clearCacheOnShutdown: config.FirefoxClearCacheOnShutdown,
                acceptInsecureCerts: config.FirefoxAcceptInsecureCerts);
            containerWatch.Start();
        }
        catch
        {
            current.Dispose();
            containerBrowser = null;
            throw;
        }
    }

    private void WatchFirefoxContainers(object? sender, EventArgs e)
    {
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
        containerWatch.Stop();
        foreach (var key in containerSessions.Keys.ToArray()) CloseContainerSession(key, true);
        containerBrowser?.Dispose();
        containerBrowser = null;
    }

    private void OnFirefoxContainerPurgeAcknowledged(string id)
    {
        var removed = config.PendingFirefoxContainerCleanups.RemoveAll(t => t.Id == id);
        if (removed > 0)
        {
            SaveConfig();
            RouteLog($"Firefox container purge acknowledged: taskId={id}");
        }
    }

    private void RequestContainerCleanup(Guid serverId, Guid? webId = null)
    {
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

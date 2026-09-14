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
        containerWatch.Tick -= WatchFirefoxContainers;
        containerWatch.Tick += WatchFirefoxContainers;
        try
        {
            await current.StartAsync(ResolveProgram(config.FirefoxPath), FirefoxContainerRoot, FirefoxSourceProfile, token);
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
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KiTTYManager.Core;

public sealed class ActiveTunnelItem : INotifyPropertyChanged
{
    private string status = "Подключение…";
    internal bool isStopRequested;
    internal bool isRecoveryActive;
    internal Task? SupervisorTask { get; set; }
    internal TaskCompletionSource<string>? CurrentFaultTcs { get; set; }
    internal readonly SemaphoreSlim LifecycleLock = new(1, 1);

    public Guid Id => Definition.Id;
    public TunnelDefinition Definition { get; }
    public Guid ServerId => Definition.ServerId;
    public string ServerName => Definition.ServerName;
    public TunnelKind Kind => Definition.Kind;
    public string KindDisplay => Definition.KindDisplay;
    public string Summary => Definition.Summary;
    public string BindDisplay => $"{Definition.BindHost}:{Definition.BindPort}";
    public string DestinationDisplay => Definition.Kind == TunnelKind.Dynamic
        ? "—"
        : $"{Definition.DestinationHost}:{Definition.DestinationPort}";

    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public string CreatedTimeDisplay => CreatedUtc.ToLocalTime().ToString("HH:mm:ss");

    public string Status
    {
        get => status;
        set
        {
            if (status != value)
            {
                status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(HasActiveResources));
            }
        }
    }

    public bool IsRunning => Status == "Работает";

    public bool HasActiveResources =>
        Route is not null || Port is not null || (!isStopRequested && (status == "Подключение…" || isRecoveryActive));

    internal ActiveRoute? Route { get; set; }
    internal ForwardedPort? Port { get; set; }
    internal CancellationTokenSource? Cts { get; set; }

    public ActiveTunnelItem(TunnelDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TunnelService : IDisposable
{
    private readonly Func<Guid, CancellationToken, Task<ActiveRoute>> openRoute;
    private readonly Action<ActiveRoute> releaseRoute;
    private readonly Action<string>? log;
    private readonly List<ActiveTunnelItem> tunnels = [];
    private readonly object lockObj = new();
    private readonly CancellationTokenSource disposeCts = new();
    private bool isDisposed;

    public event Action? Changed;

    internal TimeSpan ReconnectRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal Func<ActiveTunnelItem, bool>? HealthCheckProbe { get; set; }
    internal Action<ActiveRoute, ForwardedPort>? RoutePortAttacher { get; set; }
    internal Action<ForwardedPort>? PortStarter { get; set; }

    internal void SimulateTunnelFault(Guid tunnelId, string faultMessage = "Сбой соединения")
    {
        ActiveTunnelItem? item;
        lock (lockObj)
        {
            item = tunnels.FirstOrDefault(t => t.Id == tunnelId);
        }
        item?.CurrentFaultTcs?.TrySetResult(faultMessage);
    }

    public TunnelService(
        Func<Guid, CancellationToken, Task<ActiveRoute>> openRoute,
        Action<ActiveRoute> releaseRoute,
        Action<string>? log = null)
    {
        this.openRoute = openRoute ?? throw new ArgumentNullException(nameof(openRoute));
        this.releaseRoute = releaseRoute ?? throw new ArgumentNullException(nameof(releaseRoute));
        this.log = log;
    }

    public IReadOnlyList<ActiveTunnelItem> GetTunnels()
    {
        lock (lockObj)
        {
            return tunnels.ToList();
        }
    }

    public bool HasActiveTunnels()
    {
        lock (lockObj)
        {
            return tunnels.Any(t => t.HasActiveResources);
        }
    }

    private async Task ConnectTunnelPortAsync(
        ActiveTunnelItem item,
        TaskCompletionSource<string> faultTcs,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.isStopRequested || isDisposed)
            throw new OperationCanceledException();

        item.CurrentFaultTcs = faultTcs;

        var route = await openRoute(item.Definition.ServerId, token).ConfigureAwait(false);
        item.Route = route;

        if (item.isStopRequested || isDisposed || token.IsCancellationRequested)
        {
            CleanupItemResources(item);
            throw new OperationCanceledException();
        }

        if (route.Client is null)
            throw new InvalidOperationException("Маршрут не предоставляет SSH-клиент для создания туннеля.");

        var port = TunnelPortFactory.Create(item.Definition);
        item.Port = port;

        port.Exception += (_, args) =>
        {
            var msg = "Ошибка туннеля: " + args.Exception.Message;
            log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}): {msg}");
            faultTcs.TrySetResult(msg);
        };

        if (route.Client is { } sshClient)
        {
            try
            {
                sshClient.KeepAliveInterval = TimeSpan.FromSeconds(10);
            }
            catch { }

            sshClient.ErrorOccurred += (_, args) =>
            {
                var msg = "Сбой SSH: " + args.Exception.Message;
                log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}): {msg}");
                faultTcs.TrySetResult(msg);
            };
        }

        if (RoutePortAttacher != null)
            RoutePortAttacher(route, port);
        else
            route.AddForwardedPort(port);

        if (item.isStopRequested || isDisposed || token.IsCancellationRequested)
        {
            CleanupItemResources(item);
            throw new OperationCanceledException();
        }

        if (PortStarter != null)
            PortStarter(port);
        else
            port.Start();

        if (item.isStopRequested || isDisposed || token.IsCancellationRequested)
        {
            CleanupItemResources(item);
            throw new OperationCanceledException();
        }

        var bound = port switch
        {
            ForwardedPortLocal localPort => (int)localPort.BoundPort,
            ForwardedPortDynamic dynamicPort => (int)dynamicPort.BoundPort,
            ForwardedPortRemote remotePort => (int)remotePort.Port,
            _ => 0
        };

        if (bound != 0 && item.Definition.BindPort == 0)
        {
            item.Definition.BindPort = bound;
        }
    }

    public async Task<ActiveTunnelItem> StartTunnelAsync(
        TunnelDefinition definition,
        CancellationToken externalToken = default)
    {
        TunnelPolicy.Validate(definition);

        if (isDisposed)
            throw new ObjectDisposedException(nameof(TunnelService));

        var item = new ActiveTunnelItem(definition)
        {
            Status = "Подключение…"
        };

        CancellationTokenSource linkedCts;
        lock (lockObj)
        {
            tunnels.Add(item);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, disposeCts.Token);
            item.Cts = linkedCts;
        }

        NotifyChanged();

        await item.LifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (item.isStopRequested || isDisposed)
            {
                item.Status = "Остановлен";
                NotifyChanged();
                return item;
            }

            log?.Invoke($"Запуск туннеля «{item.Summary}» для «{item.ServerName}»…");
            var faultTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await ConnectTunnelPortAsync(item, faultTcs, linkedCts.Token).ConfigureAwait(false);

            if (item.isStopRequested || isDisposed)
            {
                CleanupItemResources(item);
                item.Status = "Остановлен";
                NotifyChanged();
                return item;
            }

            item.Status = "Работает";
            log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}) успешно поднят.");
            NotifyChanged();

            CancellationToken supervisorToken;
            lock (lockObj)
            {
                item.Cts = CancellationTokenSource.CreateLinkedTokenSource(disposeCts.Token);
                supervisorToken = item.Cts.Token;
            }

            item.SupervisorTask = Task.Run(() => RunTunnelSupervisorAsync(item, faultTcs, supervisorToken));
            return item;
        }
        catch (OperationCanceledException)
        {
            CleanupItemResources(item);
            item.Status = "Отменён";
            log?.Invoke($"Запуск туннеля «{item.Summary}» ({item.ServerName}) отменён.");
            NotifyChanged();
            throw;
        }
        catch (Exception ex)
        {
            CleanupItemResources(item);
            var message = ex is AggregateException ag
                ? (ag.InnerExceptions.FirstOrDefault()?.Message ?? ag.Message)
                : ex.Message;
            item.Status = "Ошибка: " + message;
            log?.Invoke($"Не удалось поднять туннель «{item.Summary}» ({item.ServerName}): {message}");
            NotifyChanged();
            throw;
        }
        finally
        {
            item.LifecycleLock.Release();
        }
    }

    private async Task RunTunnelSupervisorAsync(
        ActiveTunnelItem item,
        TaskCompletionSource<string> initialFaultTcs,
        CancellationToken stopToken)
    {
        var faultTcs = initialFaultTcs;

        while (!item.isStopRequested && !stopToken.IsCancellationRequested && !isDisposed)
        {
            // 1. Healthy tunnel monitoring
            while (!item.isStopRequested && !stopToken.IsCancellationRequested && !isDisposed)
            {
                try
                {
                    var delayTask = Task.Delay(HealthCheckInterval, stopToken);
                    var completed = await Task.WhenAny(faultTcs.Task, delayTask).ConfigureAwait(false);

                    if (item.isStopRequested || stopToken.IsCancellationRequested || isDisposed)
                        return;

                    bool faulted = false;
                    string? faultMessage = null;

                    if (completed == faultTcs.Task)
                    {
                        faulted = true;
                        faultMessage = await faultTcs.Task.ConfigureAwait(false);
                    }
                    else
                    {
                        var client = item.Route?.Client;
                        var port = item.Port;
                        bool isHealthy = HealthCheckProbe != null
                            ? HealthCheckProbe(item)
                            : (client != null && client.IsConnected && port != null && port.IsStarted);

                        if (!isHealthy)
                        {
                            faulted = true;
                            faultMessage = "Потеряно соединение с SSH-сервером";
                        }
                    }

                    if (faulted)
                    {
                        log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}): сбой соединения ({faultMessage}). Восстановление…");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (item.isStopRequested || stopToken.IsCancellationRequested || isDisposed)
                return;

            // 2. Begin recovery
            item.isRecoveryActive = true;
            await item.LifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                CleanupItemResources(item);
            }
            finally
            {
                item.LifecycleLock.Release();
            }

            // 3. Retry connection indefinitely until restored or stopped
            while (!item.isStopRequested && !stopToken.IsCancellationRequested && !isDisposed)
            {
                item.Status = "Переподключение…";
                NotifyChanged();

                var nextFaultTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool connected = false;

                await item.LifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (item.isStopRequested || stopToken.IsCancellationRequested || isDisposed)
                    {
                        CleanupItemResources(item);
                        return;
                    }

                    await ConnectTunnelPortAsync(item, nextFaultTcs, stopToken).ConfigureAwait(false);
                    connected = true;
                }
                catch (OperationCanceledException) when (item.isStopRequested || stopToken.IsCancellationRequested || isDisposed)
                {
                    CleanupItemResources(item);
                    return;
                }
                catch (Exception ex)
                {
                    CleanupItemResources(item);
                    var msg = ex is AggregateException ag
                        ? (ag.InnerExceptions.FirstOrDefault()?.Message ?? ag.Message)
                        : ex.Message;
                    item.Status = "Ошибка: " + msg;
                    log?.Invoke($"Не удалось восстановить туннель «{item.Summary}» ({item.ServerName}): {msg}. Повтор через {ReconnectRetryDelay.TotalSeconds:0.#} с…");
                    NotifyChanged();
                }
                finally
                {
                    item.LifecycleLock.Release();
                }

                if (connected)
                {
                    faultTcs = nextFaultTcs;
                    item.isRecoveryActive = false;
                    item.Status = "Работает";
                    log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}) успешно восстановлен.");
                    NotifyChanged();
                    break;
                }

                try
                {
                    await Task.Delay(ReconnectRetryDelay, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async Task StopTunnelAsync(Guid tunnelId)
    {
        ActiveTunnelItem? item;
        lock (lockObj)
        {
            item = tunnels.FirstOrDefault(t => t.Id == tunnelId);
        }

        if (item is null) return;

        item.isStopRequested = true;
        item.isRecoveryActive = false;

        try
        {
            item.Cts?.Cancel();
        }
        catch { }

        item.Status = "Останавливается";
        NotifyChanged();

        if (item.SupervisorTask is { } supTask)
        {
            try
            {
                await supTask.ConfigureAwait(false);
            }
            catch { }
        }

        await item.LifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Run(() => CleanupItemResources(item)).ConfigureAwait(false);
            item.Status = "Остановлен";
        }
        finally
        {
            item.LifecycleLock.Release();
        }

        log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}) остановлен.");

        lock (lockObj)
        {
            tunnels.Remove(item);
        }

        NotifyChanged();
    }

    public async Task StopTunnelsAsync(IEnumerable<Guid> tunnelIds)
    {
        var tasks = tunnelIds.Select(StopTunnelAsync).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        List<Guid> ids;
        lock (lockObj)
        {
            ids = tunnels.Select(t => t.Id).ToList();
        }

        await StopTunnelsAsync(ids).ConfigureAwait(false);
    }

    public async Task RemoveTunnelAsync(Guid tunnelId) =>
        await StopTunnelAsync(tunnelId).ConfigureAwait(false);

    public void RemoveTunnelFromList(Guid tunnelId)
    {
        ActiveTunnelItem? item;
        lock (lockObj)
        {
            item = tunnels.FirstOrDefault(t => t.Id == tunnelId);
        }

        if (item is null) return;

        if (item.HasActiveResources || item.isRecoveryActive || item.SupervisorTask != null)
        {
            _ = StopTunnelAsync(tunnelId);
            return;
        }

        lock (lockObj)
        {
            tunnels.Remove(item);
        }

        CleanupItemResources(item);
        item.Status = "Остановлен";
        NotifyChanged();
    }

    internal void AddActiveTunnelItem(ActiveTunnelItem item)
    {
        lock (lockObj)
        {
            tunnels.Add(item);
        }
        NotifyChanged();
    }

    private void CleanupItemResources(ActiveTunnelItem item)
    {
        ForwardedPort? port;
        ActiveRoute? route;
        lock (item)
        {
            port = item.Port;
            item.Port = null;
            route = item.Route;
            item.Route = null;
            item.CurrentFaultTcs = null;
        }

        if (port != null)
        {
            try { if (port.IsStarted) port.Stop(); } catch { }
            try { route?.RemoveForwardedPort(port); } catch { }
            try { port.Dispose(); } catch { }
        }

        if (route != null)
        {
            try { releaseRoute(route); } catch { }
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        try
        {
            disposeCts.Cancel();
        }
        catch { }

        List<ActiveTunnelItem> remaining;
        lock (lockObj)
        {
            remaining = tunnels.ToList();
            tunnels.Clear();
        }

        foreach (var item in remaining)
        {
            item.isStopRequested = true;
            item.isRecoveryActive = false;
            try { item.Cts?.Cancel(); } catch { }
            CleanupItemResources(item);
            item.Status = "Остановлен";
        }

        disposeCts.Dispose();
        NotifyChanged();
    }
}

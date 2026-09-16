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
        Route is not null || Port is not null || (!isStopRequested && status == "Подключение…");

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
            var route = await openRoute(definition.ServerId, linkedCts.Token).ConfigureAwait(false);
            item.Route = route;

            if (item.isStopRequested || isDisposed)
            {
                CleanupItemResources(item);
                item.Status = "Остановлен";
                NotifyChanged();
                return item;
            }

            if (route.Client is null)
                throw new InvalidOperationException("Маршрут не предоставляет SSH-клиент для создания туннеля.");

            var port = TunnelPortFactory.Create(definition);
            item.Port = port;

            port.Exception += (_, args) =>
            {
                item.Status = "Ошибка туннеля: " + args.Exception.Message;
                log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}): ошибка — {args.Exception.Message}");
                NotifyChanged();
            };

            if (route.Client is { } sshClient)
            {
                sshClient.ErrorOccurred += (_, args) =>
                {
                    item.Status = "Сбой SSH: " + args.Exception.Message;
                    log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}): сбой SSH — {args.Exception.Message}");
                    NotifyChanged();
                };
            }

            route.AddForwardedPort(port);

            if (item.isStopRequested || isDisposed)
            {
                CleanupItemResources(item);
                item.Status = "Остановлен";
                NotifyChanged();
                return item;
            }

            port.Start();

            if (item.isStopRequested || isDisposed)
            {
                CleanupItemResources(item);
                item.Status = "Остановлен";
                NotifyChanged();
                return item;
            }

            var bound = port switch
            {
                ForwardedPortLocal localPort => (int)localPort.BoundPort,
                ForwardedPortDynamic dynamicPort => (int)dynamicPort.BoundPort,
                ForwardedPortRemote remotePort => (int)remotePort.Port,
                _ => 0
            };

            if (bound != 0 && definition.BindPort == 0)
            {
                definition.BindPort = bound;
            }

            item.Status = "Работает";
            log?.Invoke($"Туннель «{item.Summary}» ({item.ServerName}) успешно поднят.");
            NotifyChanged();
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

    public async Task StopTunnelAsync(Guid tunnelId)
    {
        ActiveTunnelItem? item;
        lock (lockObj)
        {
            item = tunnels.FirstOrDefault(t => t.Id == tunnelId);
        }

        if (item is null) return;

        item.isStopRequested = true;

        try
        {
            item.Cts?.Cancel();
        }
        catch { }

        item.Status = "Останавливается";
        NotifyChanged();

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

    public void RemoveTunnelFromList(Guid tunnelId)
    {
        lock (lockObj)
        {
            var item = tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (item != null && !item.HasActiveResources)
            {
                tunnels.Remove(item);
            }
        }
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
            try { item.Cts?.Cancel(); } catch { }
            CleanupItemResources(item);
            item.Status = "Остановлен";
        }

        disposeCts.Dispose();
        NotifyChanged();
    }
}

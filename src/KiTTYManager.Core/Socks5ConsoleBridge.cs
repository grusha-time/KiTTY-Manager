using System.Net;
using System.Net.Sockets;

namespace KiTTYManager.Core;

/// <summary>
/// Exposes a loopback TCP port that relays each accepted connection through a
/// dedicated SOCKS5 CONNECT tunnel to the target SSH server.  Supports
/// multiple simultaneous connections (e.g. KiTTY Duplicate Session).
/// </summary>
internal sealed class Socks5ConsoleBridge : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly BaseProxy proxy;
    private readonly string endpointHost;
    private readonly int endpointPort;
    private readonly TimeSpan timeout;
    private TcpListener listener = null!;
    private Task acceptLoop = null!;

    // Pre-established first tunnel (consumed by the first KiTTY connection).
    private TcpClient? firstRemote;
    private byte firstRemoteByte;

    public int LocalPort { get; private set; }

    public Socks5ConsoleBridge(
        BaseProxy proxy, ManagedServer target, TimeSpan timeout,
        CancellationToken cancellationToken)
        : this(proxy, target.CleanHost, target.Port, timeout, cancellationToken) { }

    public Socks5ConsoleBridge(
        BaseProxy proxy, string endpointHost, int endpointPort,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        this.proxy = proxy;
        this.endpointHost = endpointHost;
        this.endpointPort = endpointPort;
        this.timeout = timeout;

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(timeout);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(1000);
                connectCancellation.Token.ThrowIfCancellationRequested();
            }
            try
            {
                firstRemote = Socks5TcpProbe.ConnectAsync(proxy, endpointHost, endpointPort, connectCancellation.Token)
                    .GetAwaiter().GetResult();
                var prefix = new byte[1];
                var count = firstRemote.GetStream().ReadAsync(prefix, connectCancellation.Token)
                    .AsTask().GetAwaiter().GetResult();
                if (count == 0)
                    throw new IOException("Целевой SSH-сервер закрыл транспорт до отправки приветствия.");
                firstRemoteByte = prefix[0];

                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                acceptLoop = AcceptLoopAsync();
                return;
            }
            catch (IOException ex) when (attempt < 2 && !connectCancellation.IsCancellationRequested)
            {
                lastError = ex;
                try { firstRemote?.Dispose(); } catch { }
                firstRemote = null;
            }
        }
        throw lastError ?? new IOException("Целевой SSH-сервер закрыл транспорт до отправки приветствия.");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var local = await listener.AcceptTcpClientAsync(cancellation.Token)
                    .ConfigureAwait(false);
                _ = HandleConnectionAsync(local);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(TcpClient local)
    {
        TcpClient? remote = null;
        try
        {
            await using var localStream = local.GetStream();

            // First connection reuses the pre-established tunnel.
            if (Interlocked.Exchange(ref firstRemote, null) is { } preRemote)
            {
                remote = preRemote;
                await using var remoteStream = remote.GetStream();
                await localStream.WriteAsync(new[] { firstRemoteByte }, cancellation.Token)
                    .ConfigureAwait(false);
                await RelayAsync(localStream, remoteStream).ConfigureAwait(false);
                return;
            }

            // Subsequent connections create a fresh SOCKS5 tunnel.
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            connectCancellation.CancelAfter(timeout);
            remote = await Socks5TcpProbe.ConnectAsync(proxy, endpointHost, endpointPort, connectCancellation.Token)
                .ConfigureAwait(false);
            await using var newRemoteStream = remote.GetStream();
            await RelayAsync(localStream, newRemoteStream).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            try { local.Dispose(); } catch { }
            try { remote?.Dispose(); } catch { }
        }
    }

    private static async Task RelayAsync(NetworkStream localStream, NetworkStream remoteStream)
    {
        var toRemote = localStream.CopyToAsync(remoteStream);
        var toLocal = remoteStream.CopyToAsync(localStream);
        await Task.WhenAny(toRemote, toLocal).ConfigureAwait(false);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { listener.Stop(); } catch { }
        try { firstRemote?.Dispose(); } catch { }
        try { acceptLoop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        cancellation.Dispose();
    }
}

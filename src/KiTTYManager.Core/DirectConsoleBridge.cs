using System.Net;
using System.Net.Sockets;

namespace KiTTYManager.Core;

/// <summary>Loopback relay from KiTTY directly to an SSH server, without SOCKS/JH.</summary>
internal sealed class DirectConsoleBridge : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout;
    private readonly TcpListener listener;
    private readonly Task acceptLoop;
    private TcpClient? firstRemote;
    private byte firstRemoteByte;

    public int LocalPort { get; }

    public DirectConsoleBridge(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        this.host = host;
        this.port = port;
        this.timeout = timeout;
        using var initialCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        initialCancellation.CancelAfter(timeout);
        firstRemote = ConnectAsync(initialCancellation.Token).GetAwaiter().GetResult();
        var prefix = new byte[1];
        var read = firstRemote.GetStream().ReadAsync(prefix, initialCancellation.Token)
            .AsTask().GetAwaiter().GetResult();
        if (read == 0)
        {
            firstRemote.Dispose();
            throw new IOException("Целевой SSH-сервер закрыл прямое соединение до отправки приветствия.");
        }
        firstRemoteByte = prefix[0];
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptLoopAsync();
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        timeoutCancellation.CancelAfter(timeout);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeoutCancellation.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
                _ = HandleConnectionAsync(
                    await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false));
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
            if (Interlocked.Exchange(ref firstRemote, null) is { } prepared)
            {
                remote = prepared;
                await using var preparedStream = remote.GetStream();
                await localStream.WriteAsync(new[] { firstRemoteByte }, cancellation.Token)
                    .ConfigureAwait(false);
                await RelayAsync(localStream, preparedStream).ConfigureAwait(false);
                return;
            }
            remote = await ConnectAsync(cancellation.Token).ConfigureAwait(false);
            await using var remoteStream = remote.GetStream();
            await RelayAsync(localStream, remoteStream).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            local.Dispose();
            remote?.Dispose();
        }
    }

    private static async Task RelayAsync(NetworkStream local, NetworkStream remote)
    {
        await Task.WhenAny(local.CopyToAsync(remote), remote.CopyToAsync(local))
            .ConfigureAwait(false);
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

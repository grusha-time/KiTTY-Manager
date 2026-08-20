using System.Net;
using System.Net.Sockets;

namespace KiTTYManager.Core;

public static class Socks5TcpProbe
{
    public static async Task<bool> CanConnectAsync(
        BaseProxy proxy,
        ManagedServer target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await CanConnectAsync(proxy, target.Host, target.Port, timeout, cancellationToken)
            .ConfigureAwait(false);

    public static async Task<bool> CanConnectAsync(
        BaseProxy proxy,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var client = await ConnectAsync(proxy, host, port, timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static async Task<TcpClient> ConnectAsync(
        BaseProxy proxy,
        ManagedServer target,
        CancellationToken cancellationToken) =>
        await ConnectAsync(proxy, target.Host, target.Port, cancellationToken).ConfigureAwait(false);

    internal static async Task<TcpClient> ConnectAsync(
        BaseProxy proxy,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken)
                .ConfigureAwait(false);
            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                throw new IOException("SOCKS5 не поддерживает подключение без аутентификации.");

            await stream.WriteAsync(BuildConnectRequest(host, port), cancellationToken)
                .ConfigureAwait(false);
            var response = new byte[4];
            await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
            if (response[0] != 0x05 || response[1] != 0x00)
                throw new IOException($"SOCKS5 отклонил подключение к серверу, код {response[1]}.");

            var remainingAddressBytes = response[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
                _ => throw new IOException("SOCKS5 вернул неизвестный тип адреса.")
            };
            await ReadExactlyAsync(stream, new byte[remainingAddressBytes + 2], cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadDomainLengthAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }

    public static byte[] BuildConnectRequest(string host, int port)
    {
        host = host.Trim().Trim('[', ']');
        // Handle "user@host" format: strip the username part.
        var atIndex = host.LastIndexOf('@');
        if (atIndex >= 0 && atIndex < host.Length - 1)
            host = host[(atIndex + 1)..];
        byte type;
        byte[] address;
        if (IPAddress.TryParse(host, out var ip))
        {
            type = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
            address = ip.GetAddressBytes();
        }
        else
        {
            address = System.Text.Encoding.ASCII.GetBytes(host);
            if (address.Length is 0 or > 255)
                throw new ArgumentException("Некорректное имя SSH-сервера.", nameof(host));
            type = 0x03;
        }

        var request = new List<byte> { 0x05, 0x01, 0x00, type };
        if (type == 0x03) request.Add((byte)address.Length);
        request.AddRange(address);
        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        return request.ToArray();
    }

    private static async Task ReadExactlyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("SOCKS5 закрыл соединение до завершения ответа.");
            offset += count;
        }
    }
}

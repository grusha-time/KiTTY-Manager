using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KiTTYManager.Core;

/// <summary>
/// Per-web-session SOCKS5 relay. It replaces only explicitly mapped domain
/// destinations with their IP address and passes all other destinations to the
/// upstream SOCKS server unchanged, preserving HTTP Host and TLS SNI bytes.
/// </summary>
public sealed class ResolvingSocks5Relay : IDisposable, IAsyncDisposable
{
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly IReadOnlyDictionary<string, IPAddress> mappings;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly ConcurrentDictionary<Task, byte> handlers = new();
    private readonly Task acceptTask;

    public int Port { get; }

    public ResolvingSocks5Relay(string upstreamHost, int upstreamPort,
        IEnumerable<KeyValuePair<string, string>> mappings)
    {
        if (string.IsNullOrWhiteSpace(upstreamHost))
            throw new ArgumentException("Не указан upstream SOCKS5.", nameof(upstreamHost));
        if (upstreamPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(upstreamPort));
        this.upstreamHost = upstreamHost;
        this.upstreamPort = upstreamPort;
        this.mappings = BuildMappings(mappings);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync();
    }

    public static string NormalizeDomain(string domain)
    {
        var value = domain.Trim().TrimEnd('.');
        if (value.Length == 0) throw new ArgumentException("Пустое DNS-имя.", nameof(domain));
        return new IdnMapping().GetAscii(value).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, IPAddress> BuildMappings(
        IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var domain = NormalizeDomain(pair.Key);
            if (!IPAddress.TryParse(pair.Value.Trim(), out var address))
                throw new ArgumentException($"Для {pair.Key} указан некорректный IP-адрес.");
            if (result.TryGetValue(domain, out var existing) && !existing.Equals(address))
                throw new InvalidOperationException($"Для домена {domain} указаны разные IP-адреса.");
            result[domain] = address;
        }
        return result;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
                clients.TryAdd(client, 0);
                var handler = HandleAndRemoveAsync(client);
                handlers.TryAdd(handler, 0);
                _ = handler.ContinueWith(completed => handlers.TryRemove(completed, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task HandleAndRemoveAsync(TcpClient client)
    {
        try { await HandleAsync(client, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { }
        finally
        {
            clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private async Task HandleAsync(TcpClient browser, CancellationToken cancellationToken)
    {
        await using var browserStream = browser.GetStream();
        var greeting = await ReadExactAsync(browserStream, 2, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 0x05) return;
        var methods = await ReadExactAsync(browserStream, greeting[1], cancellationToken).ConfigureAwait(false);
        if (!methods.Contains((byte)0x00))
        {
            await browserStream.WriteAsync(new byte[] { 0x05, 0xFF }, cancellationToken).ConfigureAwait(false);
            return;
        }
        await browserStream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken).ConfigureAwait(false);

        var header = await ReadExactAsync(browserStream, 4, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0x05 || header[1] != 0x01 || header[2] != 0x00)
        {
            await WriteFailureAsync(browserStream, 0x07, cancellationToken).ConfigureAwait(false);
            return;
        }
        var destination = await ReadAddressAsync(browserStream, header[3], cancellationToken).ConfigureAwait(false);
        var portBytes = await ReadExactAsync(browserStream, 2, cancellationToken).ConfigureAwait(false);
        if (destination.Domain is not null &&
            mappings.TryGetValue(NormalizeDomain(destination.Domain), out var mapped))
            destination = Address.From(mapped);

        using var upstream = new TcpClient();
        clients.TryAdd(upstream, 0);
        try
        {
            await upstream.ConnectAsync(upstreamHost, upstreamPort, cancellationToken).ConfigureAwait(false);
            await using var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
            var upstreamGreeting = await ReadExactAsync(upstreamStream, 2, cancellationToken).ConfigureAwait(false);
            if (upstreamGreeting[0] != 0x05 || upstreamGreeting[1] != 0x00)
            {
                await WriteFailureAsync(browserStream, 0x01, cancellationToken).ConfigureAwait(false);
                return;
            }
            var request = new List<byte> { 0x05, 0x01, 0x00, destination.Type };
            request.AddRange(destination.Bytes);
            request.AddRange(portBytes);
            await upstreamStream.WriteAsync(request.ToArray(), cancellationToken).ConfigureAwait(false);
            var replyHeader = await ReadExactAsync(upstreamStream, 4, cancellationToken).ConfigureAwait(false);
            var replyAddress = await ReadRawAddressAsync(upstreamStream, replyHeader[3], cancellationToken).ConfigureAwait(false);
            var replyPort = await ReadExactAsync(upstreamStream, 2, cancellationToken).ConfigureAwait(false);
            await browserStream.WriteAsync(replyHeader, cancellationToken).ConfigureAwait(false);
            await browserStream.WriteAsync(replyAddress, cancellationToken).ConfigureAwait(false);
            await browserStream.WriteAsync(replyPort, cancellationToken).ConfigureAwait(false);
            if (replyHeader[1] != 0x00) return;

            var browserToUpstream = CopyAndHalfCloseAsync(
                browserStream, upstreamStream, upstream.Client, cancellationToken);
            var upstreamToBrowser = CopyAndHalfCloseAsync(
                upstreamStream, browserStream, browser.Client, cancellationToken);
            await Task.WhenAll(browserToUpstream, upstreamToBrowser).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            try { await WriteFailureAsync(browserStream, 0x01, cancellationToken).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            clients.TryRemove(upstream, out _);
        }
    }

    private static async Task<Address> ReadAddressAsync(Stream stream, byte type, CancellationToken token)
    {
        if (type == 0x01) return Address.From(new IPAddress(await ReadExactAsync(stream, 4, token)));
        if (type == 0x04) return Address.From(new IPAddress(await ReadExactAsync(stream, 16, token)));
        if (type == 0x03)
        {
            var length = (await ReadExactAsync(stream, 1, token))[0];
            if (length == 0) throw new IOException("Пустое SOCKS5 DNS-имя.");
            var bytes = await ReadExactAsync(stream, length, token);
            return new Address(0x03, [length, .. bytes], Encoding.ASCII.GetString(bytes));
        }
        throw new IOException("Неподдерживаемый SOCKS5 ATYP.");
    }

    private static async Task<byte[]> ReadRawAddressAsync(Stream stream, byte type, CancellationToken token) =>
        type switch
        {
            0x01 => await ReadExactAsync(stream, 4, token),
            0x04 => await ReadExactAsync(stream, 16, token),
            0x03 => await ReadDomainRawAsync(stream, token),
            _ => throw new IOException("Некорректный SOCKS5 reply ATYP.")
        };

    private static async Task<byte[]> ReadDomainRawAsync(Stream stream, CancellationToken token)
    {
        var length = (await ReadExactAsync(stream, 1, token))[0];
        return [length, .. await ReadExactAsync(stream, length, token)];
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer;
    }

    private static Task WriteFailureAsync(Stream stream, byte reply, CancellationToken token) =>
        stream.WriteAsync(new byte[] { 0x05, reply, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token).AsTask();

    private static async Task CopyAndHalfCloseAsync(
        Stream source, Stream destination, Socket destinationSocket, CancellationToken token)
    {
        try
        {
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
            try { destinationSocket.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        catch (IOException) when (!token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        try { await acceptTask.ConfigureAwait(false); } catch { }
        var activeHandlers = handlers.Keys.ToArray();
        if (activeHandlers.Length > 0)
            try { await Task.WhenAll(activeHandlers).ConfigureAwait(false); } catch { }
        cancellation.Dispose();
    }

    private sealed record Address(byte Type, byte[] Bytes, string? Domain)
    {
        public static Address From(IPAddress address) => address.AddressFamily switch
        {
            AddressFamily.InterNetwork => new(0x01, address.GetAddressBytes(), null),
            AddressFamily.InterNetworkV6 => new(0x04, address.GetAddressBytes(), null),
            _ => throw new ArgumentException("Неподдерживаемое семейство IP-адреса.", nameof(address))
        };
    }
}

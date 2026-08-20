using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KiTTYManager.Core;

/// <summary>
/// Per-Firefox HTTP/HTTPS proxy which resolves configured domains itself and
/// reaches them through an upstream SOCKS5 server. Firefox never resolves the
/// destination and its SOCKS DNS preferences are not used.
/// </summary>
public sealed class ResolvingHttpProxy : IDisposable, IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly IReadOnlyDictionary<string, IPAddress> mappings;
    private readonly Action<string>? log;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly ConcurrentDictionary<Task, byte> handlers = new();
    private readonly Task acceptTask;

    public int Port { get; }

    public ResolvingHttpProxy(string upstreamHost, int upstreamPort,
        IEnumerable<KeyValuePair<string, string>> mappings, Action<string>? log = null)
    {
        this.upstreamHost = upstreamHost;
        this.upstreamPort = upstreamPort;
        this.mappings = BuildMappings(mappings);
        this.log = log;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptAsync();
    }

    private static IReadOnlyDictionary<string, IPAddress> BuildMappings(
        IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, IPAddress>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var domain = ResolvingSocks5Relay.NormalizeDomain(pair.Key);
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

    private async Task HandleAndRemoveAsync(TcpClient browser)
    {
        try
        {
            log?.Invoke($"client accepted; remote={browser.Client.RemoteEndPoint}");
            await HandleAsync(browser, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { log?.Invoke($"client failed; error={ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            clients.TryRemove(browser, out _);
            browser.Dispose();
        }
    }

    private async Task HandleAsync(TcpClient browser, CancellationToken token)
    {
        await using var browserStream = browser.GetStream();
        var headerBytes = await ReadHeaderAsync(browserStream, token).ConfigureAwait(false);
        var headerText = Encoding.Latin1.GetString(headerBytes);
        var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd < 0) return;
        var parts = headerText[..firstLineEnd].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return;

        var isConnect = parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        var isUpgrade = headerText.Contains("\r\nUpgrade:", StringComparison.OrdinalIgnoreCase);
        string host;
        int port;
        if (isConnect)
        {
            if (!TryParseAuthority(parts[1], 443, out host, out port)) return;
        }
        else
        {
            if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
            host = uri.DnsSafeHost;
            port = uri.Port;
            headerBytes = RewritePlainHttpHeader(headerText, firstLineEnd,
                $"{parts[0]} {uri.PathAndQuery} {parts[2]}", isUpgrade);
        }

        var normalized = IPAddress.TryParse(host, out _) ? host : ResolvingSocks5Relay.NormalizeDomain(host);
        var target = mappings.TryGetValue(normalized, out var mappedAddress) ? mappedAddress.ToString() : normalized;
        log?.Invoke($"request; method={parts[0]}; host={normalized}; target={target}; port={port}");

        using var upstream = await ConnectThroughSocksAsync(host, port, token).ConfigureAwait(false);
        clients.TryAdd(upstream, 0);
        try
        {
            await using var upstreamStream = upstream.GetStream();
            if (isConnect)
            {
                await browserStream.WriteAsync(
                    "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
                log?.Invoke("CONNECT accepted; relay starting");
            }
            else
                await upstreamStream.WriteAsync(headerBytes, token).ConfigureAwait(false);

            var browserToUpstream = CopyAndHalfCloseAsync(
                browserStream, upstreamStream, upstream.Client, "browser-to-target", log, token,
                coalesceFirstTlsRecord: isConnect);
            var upstreamToBrowser = CopyAndHalfCloseAsync(
                upstreamStream, browserStream, browser.Client, "target-to-browser", log, token,
                coalesceFirstTlsRecord: false);
            if (isConnect || isUpgrade)
                await Task.WhenAll(browserToUpstream, upstreamToBrowser).ConfigureAwait(false);
            else
            {
                await upstreamToBrowser.ConfigureAwait(false);
                browser.Dispose();
                upstream.Dispose();
                try { await browserToUpstream.ConfigureAwait(false); } catch { }
            }
        }
        finally { clients.TryRemove(upstream, out _); }
    }

    private async Task<TcpClient> ConnectThroughSocksAsync(string host, int port, CancellationToken token)
    {
        var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(upstreamHost, upstreamPort, token).ConfigureAwait(false);
            var stream = upstream.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token).ConfigureAwait(false);
            var greeting = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                throw new IOException("Upstream SOCKS5 не поддерживает режим без авторизации.");

            byte[] address;
            var normalized = IPAddress.TryParse(host, out var literal)
                ? null
                : ResolvingSocks5Relay.NormalizeDomain(host);
            if (normalized is not null && mappings.TryGetValue(normalized, out var mapped)) literal = mapped;
            if (literal is not null)
                address = literal.AddressFamily == AddressFamily.InterNetwork
                    ? [0x01, .. literal.GetAddressBytes()]
                    : [0x04, .. literal.GetAddressBytes()];
            else
            {
                var domain = Encoding.ASCII.GetBytes(normalized!);
                if (domain.Length > 255) throw new IOException("Слишком длинное DNS-имя.");
                address = [0x03, (byte)domain.Length, .. domain];
            }
            byte[] request = [0x05, 0x01, 0x00, .. address, (byte)(port >> 8), (byte)port];
            await stream.WriteAsync(request, token)
                .ConfigureAwait(false);
            var reply = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
            await SkipAddressAsync(stream, reply[3], token).ConfigureAwait(false);
            await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
            if (reply[0] != 0x05 || reply[1] != 0x00)
                throw new IOException($"Upstream SOCKS5 отклонил соединение, код {reply[1]}.");
            log?.Invoke($"SOCKS connected; host={normalized ?? literal!.ToString()}; port={port}");
            return upstream;
        }
        catch { upstream.Dispose(); throw; }
    }

    private static bool TryParseAuthority(string value, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri)) return false;
        host = uri.DnsSafeHost;
        port = uri.IsDefaultPort ? defaultPort : uri.Port;
        return host.Length > 0 && port is > 0 and <= 65535;
    }

    private static byte[] RewritePlainHttpHeader(
        string header, int firstLineEnd, string originLine, bool isUpgrade)
    {
        var lines = header[(firstLineEnd + 2)..].Split("\r\n", StringSplitOptions.None)
            .Where(line => line.Length > 0 &&
                !line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) &&
                (isUpgrade || !line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (!isUpgrade) lines.Add("Connection: close");
        return Encoding.Latin1.GetBytes(originLine + "\r\n" + string.Join("\r\n", lines) + "\r\n\r\n");
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var state = 0;
        while (buffer.Length < MaxHeaderBytes)
        {
            var next = await ReadExactAsync(stream, 1, token).ConfigureAwait(false);
            buffer.WriteByte(next[0]);
            state = (state, next[0]) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };
            if (state == 4) return buffer.ToArray();
        }
        throw new IOException("HTTP-заголовок превышает допустимый размер.");
    }

    private static async Task SkipAddressAsync(Stream stream, byte type, CancellationToken token)
    {
        var length = type switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactAsync(stream, 1, token).ConfigureAwait(false))[0],
            _ => throw new IOException("Некорректный SOCKS5 reply ATYP.")
        };
        await ReadExactAsync(stream, length, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer;
    }

    private static async Task CopyAndHalfCloseAsync(
        Stream source, Stream destination, Socket destinationSocket, string direction,
        Action<string>? log, CancellationToken token, bool coalesceFirstTlsRecord)
    {
        long total = 0;
        try
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (coalesceFirstTlsRecord && total == 0 && read >= 5 && buffer[0] == 0x16)
                {
                    var recordLength = 5 + (buffer[3] << 8) + buffer[4];
                    if (recordLength <= buffer.Length)
                    {
                        while (read < recordLength)
                        {
                            var more = await source.ReadAsync(buffer.AsMemory(read, recordLength - read), token)
                                .ConfigureAwait(false);
                            if (more == 0) break;
                            read += more;
                        }
                        log?.Invoke($"TLS record coalesced; direction={direction}; bytes={read}; expected={recordLength}");
                    }
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
                if (total == read)
                    log?.Invoke($"relay data; direction={direction}; first={Convert.ToHexString(buffer, 0, Math.Min(read, 8))}");
            }
            try { destinationSocket.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        catch (IOException ex) when (!token.IsCancellationRequested)
        {
            log?.Invoke($"relay failed; direction={direction}; bytes={total}; error={ex.Message}");
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        finally { log?.Invoke($"relay ended; direction={direction}; bytes={total}"); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        try { await acceptTask.ConfigureAwait(false); } catch { }
        var active = handlers.Keys.ToArray();
        if (active.Length > 0)
            try { await Task.WhenAll(active).ConfigureAwait(false); } catch { }
        cancellation.Dispose();
    }
}

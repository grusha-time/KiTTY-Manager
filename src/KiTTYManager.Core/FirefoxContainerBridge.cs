using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KiTTYManager.Core;

// Loopback-only, per-run authenticated bridge. No URL/command API is exposed to websites.
public sealed class FirefoxContainerBridge : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly TcpListener blockedListener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, Route> routes = new();
    private readonly ConcurrentDictionary<string, Pending> commands = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private long lastPollTicks;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Url { get; }
    public int BlockedPort { get; }
    public bool Connected => DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastPollTicks)) < TimeSpan.FromSeconds(10);
    public event Action<string>? Closed;
    public Action<string>? Trace { get; set; }
    private sealed record Route(string Key, string Name, int Port) { public bool Opened { get; set; } }
    private sealed record Pending(string Key, string Url)
    {
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public FirefoxContainerBridge()
    {
        listener.Start();
        blockedListener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/poll";
        BlockedPort = ((IPEndPoint)blockedListener.LocalEndpoint).Port;
        _ = AcceptAsync(listener, false);
        _ = AcceptAsync(blockedListener, true);
    }

    public async Task OpenAsync(string key, string name, int port, string url, CancellationToken token)
    {
        if (port is < 1 or > 65535 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) throw new ArgumentException("Ожидается HTTP(S) URL и порт прокси.");
        routes.AddOrUpdate(key, new Route(key, name, port), (_, old) => old.Port == port
            ? old : throw new InvalidOperationException("Контейнер уже связан с другим туннелем."));
        var id = Guid.NewGuid().ToString("N");
        var command = new Pending(key, url);
        commands[id] = command;
        try { await command.Done.Task.WaitAsync(TimeSpan.FromSeconds(20), token); }
        catch { Remove(key); throw; }
        finally { commands.TryRemove(id, out _); }
    }

    public void Remove(string key)
    {
        routes.TryRemove(key, out _);
        foreach (var pair in commands.Where(p => p.Value.Key == key))
            if (commands.TryRemove(pair.Key, out var command))
                command.Done.TrySetException(new IOException("Подключение контейнера закрыто."));
    }

    private async Task AcceptAsync(TcpListener source, bool reject)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await source.AcceptTcpClientAsync(stop.Token);
                if (reject) { client.Dispose(); continue; }
                clients[client] = 0;
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream();
                // One bounded HTTP/1.1 request per connection; reject chunked bodies.
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    await stream.ReadExactlyAsync(one, deadline.Token);
                    header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] {13, 10, 13, 10})) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
                    .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                if (lines[0] != "POST /poll HTTP/1.1" ||
                    headers.GetValueOrDefault("Host") != new Uri(Url).Authority ||
                    headers.GetValueOrDefault("Authorization") != "Bearer " + Token ||
                    headers.ContainsKey("Transfer-Encoding") ||
                    !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) || length is < 2 or > 131072)
                {
                    Trace?.Invoke("Firefox bridge: request rejected (method/host/auth/body)");
                    await RespondAsync(stream, "403 Forbidden", "{}", deadline.Token);
                    return;
                }
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, deadline.Token);
                using var json = JsonDocument.Parse(body);
                var active = json.RootElement.GetProperty("activeKeys").EnumerateArray()
                    .Select(v => v.GetString()!).ToHashSet();
                // Closure detection precedes acknowledgement: the first open ack
                // was computed from tabs BEFORE that command was executed.
                foreach (var pair in routes)
                    if (pair.Value.Opened && !active.Contains(pair.Key) &&
                        !commands.Values.Any(c => c.Key == pair.Key))
                    {
                        if (routes.TryRemove(pair.Key, out _)) Closed?.Invoke(pair.Key);
                    }
                foreach (var ack in json.RootElement.GetProperty("acks").EnumerateArray())
                {
                    if (!commands.TryRemove(ack.GetProperty("id").GetString()!, out var pending)) continue;
                    var error = ack.GetProperty("error");
                    if (error.ValueKind != JsonValueKind.Null)
                        pending.Done.TrySetException(new IOException(error.GetString()));
                    else
                    {
                        if (routes.TryGetValue(pending.Key, out var route)) route.Opened = true;
                        pending.Done.TrySetResult(true);
                    }
                }
                Interlocked.Exchange(ref lastPollTicks, DateTime.UtcNow.Ticks);
                var response = JsonSerializer.Serialize(new
                {
                    routes = routes.Values.Select(r => new {key = r.Key, name = r.Name, port = r.Port}),
                    commands = commands.Select(p => new {id = p.Key, key = p.Value.Key, url = p.Value.Url})
                });
                await RespondAsync(stream, "200 OK", response, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or
                InvalidOperationException or KeyNotFoundException or ArgumentException or SocketException)
            { Trace?.Invoke("Firefox bridge: " + ex.Message); }
            finally { clients.TryRemove(client, out _); }
        }
    }

    private static async Task RespondAsync(Stream stream, string status, string json, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel(); listener.Stop(); blockedListener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        foreach (var key in routes.Keys) Remove(key);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void FirefoxContainerPersistenceAndBridge() => CheckContainersAsync().GetAwaiter().GetResult();
    private static async Task CheckContainersAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-containers-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "key4.db"), "key");
            File.WriteAllText(Path.Combine(source, "cert9.db"), "cert");
            File.WriteAllText(Path.Combine(source, "logins.json"), "initial");
            var profile = FirefoxProfileWorkspace.EnsureContainerProfile(Path.Combine(root, "managed"), () => source);
            File.WriteAllText(Path.Combine(profile, "logins.json"), "new data");
            Equal(profile, FirefoxProfileWorkspace.EnsureContainerProfile(Path.Combine(root, "managed"),
                () => throw new Exception("Must not access the original profile again")));
            Equal("new data", File.ReadAllText(Path.Combine(profile, "logins.json")));
            Equal("initial", File.ReadAllText(Path.Combine(source, "logins.json")));
            using var bridge = new FirefoxContainerBridge();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var denied = await http.PostAsync(bridge.Url, new StringContent("{}"));
            Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
            var opening = bridge.OpenAsync("a", "A", 12345, "https://same.test/", CancellationToken.None);
            var response = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
            using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var command = state.RootElement.GetProperty("commands")[0].GetProperty("id").GetString();
            response = await http.PostAsync(bridge.Url, new StringContent(JsonSerializer.Serialize(new
                {acks = new[] {new {id = command, error = (string?)null}}, activeKeys = new[] {"a"}})));
            response.EnsureSuccessStatusCode();
            await opening;
            var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.Closed += key => closed.TrySetResult(key);
            response = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
            response.EnsureSuccessStatusCode();
            Equal("a", await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Equal(true, File.Exists(Path.Combine(profile, "logins.json")));
        }
        finally { Directory.Delete(root, true); }
    }
}

internal static class FirefoxContainerSmoke
{
    public static async Task<int> RunAsync(string executable, string root)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        if (!File.Exists(Path.Combine(source, "key4.db")))
        {
            var seed = new System.Diagnostics.ProcessStartInfo(executable) {UseShellExecute = false};
            foreach (var arg in new[] {"-headless", "-no-remote", "-profile", source, "-screenshot",
                         Path.Combine(root, "seed.png"), "about:blank"}) seed.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(seed)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            if (process.ExitCode != 0) throw new Exception("Seed Firefox failed");
        }
        for (var pass = 0; pass < 2; pass++)
        {
            using var direct = new DirectTrap();
            using var a = new FakePanel("A", direct.Port);
            using var b = new FakePanel("B", direct.Port);
            using var browser = new FirefoxContainerBrowser();
            browser.Bridge.Trace = Console.WriteLine;
            try
            {
                await browser.StartAsync(executable, Path.Combine(root, "managed"),
                    () => pass == 0 ? source : throw new Exception("Restart must not read source"),
                    CancellationToken.None, headless: true);
                await Task.WhenAll(
                    browser.Bridge.OpenAsync("a", "Test A", a.Port, "http://same.test/", CancellationToken.None),
                    browser.Bridge.OpenAsync("b", "Test B", b.Port, "http://same.test/", CancellationToken.None));
                var results = await Task.WhenAll(a.Seen.Task, b.Seen.Task).WaitAsync(TimeSpan.FromSeconds(20));
                if (!results[0].Contains("after=flavor=A") || !results[1].Contains("after=flavor=B"))
                    throw new Exception("Cookie isolation failed: " + string.Join(" / ", results));
                if (pass == 1 && (!results[0].Contains("before=flavor=A") || !results[1].Contains("before=flavor=B")))
                    throw new Exception("Cookies were not preserved across Firefox restart");
                // A live page keeps fetching. After route removal it must stop
                // reaching this proxy, even though its tab is still open.
                await Task.Delay(1200);
                browser.Bridge.Remove("b");
                await Task.Delay(1500);
                var before = b.RequestCount;
                await Task.Delay(1500);
                if (b.RequestCount != before) throw new Exception("Revoked route still receives requests");
                browser.Bridge.Dispose();
                await Task.Delay(4000);
                if (direct.Connections != 0) throw new Exception("Browser bypassed the container proxy");
                Console.WriteLine($"PASS {pass + 1}: real Firefox, same domain, two SOCKS routes, isolated cookies, revoked route blocked, no direct loopback bypass after manager disconnect.");
            }
            finally
            {
                if (browser.IsRunning) await browser.CloseForSmokeAsync();
            }
        }
        Console.WriteLine("PASS: cookies and container identities survived Firefox restart; original profile not re-read.");
        return 0;
    }

    private sealed class DirectTrap : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public int Port { get; }
        public DirectTrap()
        {
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                }
            }
            catch (OperationCanceledException) { }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

    private sealed class FakePanel : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly string flavor;
        private readonly int directPort;
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public int Port { get; }
        public TaskCompletionSource<string> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakePanel(string flavor, int directPort)
        {
            this.directPort = directPort;
            this.flavor = flavor; listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!stop.IsCancellationRequested) _ = HandleAsync(await listener.AcceptTcpClientAsync(stop.Token)); }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                var hello = new byte[2]; await stream.ReadExactlyAsync(hello, stop.Token);
                var methods = new byte[hello[1]]; await stream.ReadExactlyAsync(methods, stop.Token);
                await stream.WriteAsync(new byte[] {5, 0}, stop.Token);
                var header = new byte[4]; await stream.ReadExactlyAsync(header, stop.Token);
                if (header[3] == 3)
                {
                    var len = new byte[1]; await stream.ReadExactlyAsync(len, stop.Token);
                    var domain = new byte[len[0]]; await stream.ReadExactlyAsync(domain, stop.Token);
                    if (Encoding.ASCII.GetString(domain) != "same.test") throw new Exception("Unexpected destination");
                }
                else if (header[3] == 1)
                {
                    var ip = new byte[4]; await stream.ReadExactlyAsync(ip, stop.Token);
                    if (!new IPAddress(ip).Equals(IPAddress.Loopback)) throw new Exception("Unexpected IP destination");
                }
                else throw new Exception("Unexpected address type");
                var port = new byte[2]; await stream.ReadExactlyAsync(port, stop.Token);
                await stream.WriteAsync(new byte[] {5, 0, 0, 1, 127, 0, 0, 1, 0, 80}, stop.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token) ?? "";
                Interlocked.Increment(ref requestCount);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                if (request.StartsWith("GET /seen?", StringComparison.Ordinal)) Seen.TrySetResult(Uri.UnescapeDataString(request));
                var html = $"<html><title>{flavor}</title><script>let old=document.cookie;if(!old)document.cookie='flavor={flavor}; Max-Age=86400; Path=/';fetch('/seen?before='+encodeURIComponent(old)+'&after='+encodeURIComponent(document.cookie));setInterval(()=>{{fetch('/heartbeat').catch(()=>{{}});fetch('http://127.0.0.1:{directPort}/direct').catch(()=>{{}});}},300);</script></html>";
                var data = Encoding.UTF8.GetBytes(html);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(data, stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            catch (Exception e) { Seen.TrySetException(e); }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }
}

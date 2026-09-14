using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void FirefoxContainerPersistenceAndBridge()
    {
        CheckContainersAsync().GetAwaiter().GetResult();
        CheckExtensionInterleaving();
    }
    private static void CheckExtensionInterleaving()
    {
        using var stream = typeof(FirefoxContainerBrowser).Assembly.GetManifestResourceStream("KiTTYManager.Core.FirefoxExtension.background.js");
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        var jsCode = reader.ReadToEnd();

        var nodeCandidates = new[] { "/usr/bin/node", "/usr/local/bin/node", "node" };
        string? nodeExe = null;
        foreach (var candidate in nodeCandidates)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(2000);
                    if (p.ExitCode == 0) { nodeExe = candidate; break; }
                }
            }
            catch { }
        }
        if (nodeExe == null) return;

        var script = $$"""
        const vm = require('vm'), assert = require('assert');
        const code = {{JsonSerializer.Serialize(jsCode)}}.split('browser.proxy.onRequest')[0];

        async function testScenario(scenario) {
          const h = {}, event = n => ({addListener: f => h[n] = f}), data = new Map(), calls = [];
          let selected = (scenario === 'race' || scenario === 'normal' || scenario === 'race_during_update') ? 3 : 2;
          let first = true;
          const original = {
            id: 3, windowId: 1, index: 2,
            active: (scenario === 'race' || scenario === 'normal' || scenario === 'race_during_update'),
            cookieStoreId: 'firefox-default', url: 'about:newtab'
          };
          const b = {
            id: 2, windowId: 1, index: 1,
            active: (scenario === 'background'),
            cookieStoreId: 'B', url: 'http://same.test'
          };
          data.set(3, original);
          data.set(2, b);

          const browser = {
            runtime: { getURL: () => '' },
            windows: { onRemoved: event('windowRemoved') },
            tabs: {
              onActivated: event('activated'),
              onCreated: event('created'),
              onUpdated: event('updated'),
              onRemoved: event('removed'),
              get: async id => {
                const snapshot = { ...data.get(id) };
                if (scenario === 'race' && id === 3 && first) {
                  first = false;
                  original.active = false;
                  b.active = true;
                  selected = 2;
                  await h.activated({ tabId: 2, windowId: 1, previousTabId: 3 });
                }
                return snapshot;
              },
              create: async options => {
                calls.push(options);
                const t = { id: 100 + calls.length, url: 'about:newtab', ...options };
                data.set(t.id, t);
                if (options.active) {
                  selected = t.id;
                  for (const x of data.values()) x.active = x.id === t.id;
                  await h.activated({ tabId: t.id, windowId: 1 });
                }
                return { ...t };
              },
              remove: async id => data.delete(id),
              update: async (id, opts) => {
                if (opts.active) {
                  selected = id;
                  for (const x of data.values()) x.active = x.id === id;
                  await h.activated({ tabId: id, windowId: 1 });
                  if (scenario === 'race_during_update') {
                    selected = 2;
                    for (const x of data.values()) x.active = x.id === 2;
                    await h.activated({ tabId: 2, windowId: 1, previousTabId: id });
                  }
                }
                if (opts.url && data.has(id)) data.get(id).url = opts.url;
              }
            }
          };

          const ctx = vm.createContext({ browser, KITTY: { startupUrl: 'about:blank#launcher' }, console, original, b });
          vm.runInContext(code, ctx);
          vm.runInContext(
            "routes.set('A',{}); routes.set('B',{}); inheritanceReady=true; " +
            "knownTabs.set(2,b); knownTabs.set(3,original); " +
            `activeTabs.set(1, ${original.active ? 3 : 2}); activeContainers.set(1, '${original.active ? 'A' : 'B'}');`,
            ctx
          );

          await vm.runInContext("inheritNewTab(original,'A')", ctx);

          assert.equal(calls[0].active, false, "Replacement must always be created inactive initially");

          if (scenario === 'race') {
            assert.equal(selected, 2, "Tab B must retain focus when user switched to B during tab resolution");
            assert.equal(vm.runInContext('activeTabs.get(1)', ctx), 2);
          } else if (scenario === 'background') {
            assert.equal(selected, 2, "Tab B must retain focus when original tab was in background");
            assert.equal(vm.runInContext('activeTabs.get(1)', ctx), 2);
          } else if (scenario === 'normal') {
            assert.equal(selected, 101, "Replacement tab must receive focus when original tab was active and not switched");
            assert.equal(vm.runInContext('activeTabs.get(1)', ctx), 101);
            assert.equal(vm.runInContext("activeContainers.get(1)", ctx), 'A');
          } else if (scenario === 'race_during_update') {
            assert.equal(selected, 2, "Selected B during update response, but tracked active tab must remain B");
            assert.equal(vm.runInContext('activeTabs.get(1)', ctx), 2);
            assert.equal(vm.runInContext("activeContainers.get(1)", ctx), 'B');
          }

          h.updated(6, { status: 'complete' }, { id: 6, active: false, cookieStoreId: 'firefox-default', url: 'about:newtab' });
          assert.equal(vm.runInContext('startupTabs.length', ctx), 0);
        }

        (async () => {
          await testScenario('race');
          await testScenario('background');
          await testScenario('normal');
          await testScenario('race_during_update');
        })().catch(e => { console.error(e); process.exit(1); });
        """;

        var runPsi = new System.Diagnostics.ProcessStartInfo(nodeExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(runPsi)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
            throw new Exception("Extension interleaving check failed: " + stderr);
    }
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
            // Upgrade an already-created TEST profile which explicitly disabled WebRTC.
            foreach (var name in new[] { "prefs.js", "user.js" })
                File.WriteAllText(Path.Combine(profile, name), "user_pref(\"media.peerconnection.enabled\", false);\n");
            FirefoxProfileWorkspace.ConfigureContainers(profile, 12340, 12341, 12342);
            foreach (var name in new[] { "prefs.js", "user.js" })
            {
                var preferences = File.ReadAllText(Path.Combine(profile, name));
                Equal(true, preferences.Contains("user_pref(\"media.peerconnection.enabled\", true);"));
                Equal(false, preferences.Contains("user_pref(\"media.peerconnection.enabled\", false);"));
            }
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
            File.WriteAllText(Path.Combine(source, "user.js"),
                "user_pref(\"browser.aboutwelcome.enabled\", false);\n" +
                "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n" +
                "user_pref(\"browser.startup.firstrunSkipsHomepage\", true);\n" +
                "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
                "user_pref(\"browser.startup.homepage_welcome_url\", \"\");\n" +
                "user_pref(\"browser.startup.homepage_welcome_url.additional\", \"\");\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                "user_pref(\"datareporting.policy.firstRunURL\", \"\");\n" +
                "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);\n" +
                "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);\n" +
                "user_pref(\"doh-rollout.doneFirstRun\", true);\n" +
                "user_pref(\"doh-rollout.enabled\", false);\n" +
                "user_pref(\"app.update.auto\", false);\n" +
                "user_pref(\"app.update.enabled\", false);\n" +
                "user_pref(\"app.update.doorhanger\", false);\n" +
                "user_pref(\"termsofuse.bypassNotification\", true);\n" +
                "user_pref(\"dom.security.https_first\", false);\n" +
                "user_pref(\"dom.security.https_only_mode\", false);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n");
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
            var failed = false;
            try
            {
                await browser.StartAsync(executable, Path.Combine(root, "managed"),
                    () => pass == 0 ? source : throw new Exception("Restart must not read source"),
                    CancellationToken.None, headless: true);
                await Task.WhenAll(
                    browser.Bridge.OpenAsync("a", "Test A", a.Port, "http://same.test/", CancellationToken.None),
                    browser.Bridge.OpenAsync("b", "Test B", b.Port, "http://same.test/", CancellationToken.None));
                var results = await Task.WhenAll(a.Seen.Task, b.Seen.Task).WaitAsync(TimeSpan.FromSeconds(20));
                if (await browser.TabCountForSmokeAsync() != 2)
                    throw new Exception("Unexpected tab left over after opening two interfaces");
                if (!results[0].Contains("after=flavor=A") || !results[1].Contains("after=flavor=B"))
                    throw new Exception("Cookie isolation failed: " + string.Join(" / ", results));
                if (pass == 1 && (!results[0].Contains("before=flavor=A") || !results[1].Contains("before=flavor=B")))
                    throw new Exception("Cookies were not preserved across Firefox restart");
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_SMOKE_XDOTOOL")))
                {
                    var windowId = (await XdoAsync("search", "--onlyvisible", "--class", "firefox")).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
                    foreach (var panel in new[] {a, b})
                    {
                        var ui = await browser.ChromeForSmokeAsync($$"""
                            const win = Services.wm.getMostRecentWindow("navigator:browser").wrappedJSObject;
                            const index = [...win.gBrowser.tabs].findIndex(t => t.label === "{{panel.Flavor}}");
                            if (index >= 0) {
                              win.gBrowser.selectedTab = win.gBrowser.tabs[index];
                              win.gBrowser.selectedBrowser.focus();
                            }
                            const button = [...win.document.querySelectorAll("#tabs-newtab-button, #new-tab-button")]
                              .find(b => b.getBoundingClientRect().width > 0);
                            const r = button.getBoundingClientRect();
                            return {index, x:Math.round(win.mozInnerScreenX+r.x+r.width/2), y:Math.round(win.mozInnerScreenY+r.y+r.height/2)};
                            """);
                        var index = ui.GetProperty("index").GetInt32();
                        if (index < 0) throw new Exception("Panel tab not found");
                        await XdoAsync("windowfocus", "--sync", windowId);
                        await Task.Delay(300);
                        if (panel == a)
                            await XdoAsync("mousemove", "--sync", ui.GetProperty("x").ToString(), ui.GetProperty("y").ToString(), "click", "1");
                        else await XdoAsync("key", "--clearmodifiers", "ctrl+t");
                        await Task.Delay(1000);
                        await XdoAsync("key", "--clearmodifiers", "ctrl+l");
                        await Task.Delay(200);
                        await XdoAsync("type", "--clearmodifiers", "--delay", "1", "http://same.test/inherited");
                        await XdoAsync("key", "--clearmodifiers", "Return");
                        await panel.Inherited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    if (await browser.TabCountForSmokeAsync() != 4)
                        throw new Exception("New-tab replacement left duplicate tabs");
                    Console.WriteLine("PASS: mouse new-tab button inherits A, Ctrl+T inherits B; requests reached their respective SOCKS servers.");
                }
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
            catch (Exception ex)
            {
                failed = true;
                Console.Error.WriteLine("SMOKE FAILURE: " + ex);
                try { await browser.SaveScreenshotForSmokeAsync(Path.Combine(root, "failure.png")); } catch { }
                throw;
            }
            finally
            {
                if (browser.IsRunning)
                    try { await browser.CloseForSmokeAsync(); }
                    catch (Exception ex) when (failed) { Console.Error.WriteLine("Smoke shutdown: " + ex.Message); }
            }
        }
        Console.WriteLine("PASS: cookies and container identities survived Firefox restart; original profile not re-read.");
        return 0;
    }

    private static async Task<string> XdoAsync(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("KITTY_SMOKE_XDOTOOL")!)
            {UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true};
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new Exception("xdotool: " + await error);
        return await output;
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
        public string Flavor => flavor;
        private readonly int directPort;
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public int Port { get; }
        public TaskCompletionSource<string> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Inherited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    var domainStr = Encoding.ASCII.GetString(domain);
                    if (domainStr != "same.test" && domainStr != "127.0.0.1" && domainStr != "localhost")
                        throw new Exception("Unexpected destination: " + domainStr);
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
                if (request.StartsWith("GET /inherited ", StringComparison.Ordinal)) Inherited.TrySetResult(true);
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

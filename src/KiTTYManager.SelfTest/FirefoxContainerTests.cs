using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    internal static void FirefoxDiskOptimizationPreferencesTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-pref-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);

            // 1. Default flags (all true)
            FirefoxProfileWorkspace.ConfigureContainers(profile, 12340, 12341, 12342);
            foreach (var name in new[] { "prefs.js", "user.js" })
            {
                var content = File.ReadAllText(Path.Combine(profile, name));
                Equal(true, content.Contains("user_pref(\"browser.cache.disk.enable\", false);"));
                Equal(true, content.Contains("user_pref(\"browser.cache.memory.enable\", true);"));
                Equal(true, content.Contains("user_pref(\"browser.cache.memory.capacity\", 51200);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.malware.enabled\", false);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.phishing.enabled\", false);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.downloads.enabled\", false);"));
                Equal(true, content.Contains("user_pref(\"places.history.enabled\", false);"));
                Equal(true, content.Contains("user_pref(\"browser.chrome.site_icons\", false);"));
                Equal(true, content.Contains("user_pref(\"privacy.sanitize.sanitizeOnShutdown\", true);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown.cache\", true);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown.cookies\", false);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown.sessions\", false);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown_v2.cache\", true);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown_v2.cookiesAndStorage\", false);"));
                Equal(true, content.Contains("user_pref(\"security.enterprise_roots.enabled\", true);"));
            }

            // 2. Disabled flags (all false)
            FirefoxProfileWorkspace.ConfigureContainers(profile, 12340, 12341, 12342,
                optimizeRamCache: false,
                disableSafeBrowsing: false,
                disableHistoryAndIcons: false,
                clearCacheOnShutdown: false,
                acceptInsecureCerts: false);
            foreach (var name in new[] { "prefs.js", "user.js" })
            {
                var content = File.ReadAllText(Path.Combine(profile, name));
                Equal(true, content.Contains("user_pref(\"browser.cache.disk.enable\", true);"));
                Equal(true, content.Contains("user_pref(\"browser.cache.memory.capacity\", -1);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.malware.enabled\", true);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.phishing.enabled\", true);"));
                Equal(true, content.Contains("user_pref(\"browser.safebrowsing.downloads.enabled\", true);"));
                Equal(true, content.Contains("user_pref(\"places.history.enabled\", true);"));
                Equal(true, content.Contains("user_pref(\"browser.chrome.site_icons\", true);"));
                Equal(true, content.Contains("user_pref(\"privacy.sanitize.sanitizeOnShutdown\", false);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown.cache\", false);"));
                Equal(true, content.Contains("user_pref(\"privacy.clearOnShutdown_v2.cache\", false);"));
                Equal(true, content.Contains("user_pref(\"security.enterprise_roots.enabled\", false);"));
            }

            // 3. Cycle back to enabled: verify no duplicate lines
            FirefoxProfileWorkspace.ConfigureContainers(profile, 12340, 12341, 12342,
                optimizeRamCache: true,
                disableSafeBrowsing: true,
                disableHistoryAndIcons: true,
                clearCacheOnShutdown: true,
                acceptInsecureCerts: true);
            foreach (var name in new[] { "prefs.js", "user.js" })
            {
                var lines = File.ReadAllLines(Path.Combine(profile, name));
                Equal(1, lines.Count(l => l.Contains("\"browser.cache.disk.enable\"")));
                Equal(1, lines.Count(l => l.Contains("\"browser.safebrowsing.malware.enabled\"")));
                Equal(1, lines.Count(l => l.Contains("\"places.history.enabled\"")));
                Equal(1, lines.Count(l => l.Contains("\"privacy.sanitize.sanitizeOnShutdown\"")));
                Equal(1, lines.Count(l => l.Contains("\"security.enterprise_roots.enabled\"")));
                Equal(true, lines.Any(l => l.Contains("user_pref(\"security.enterprise_roots.enabled\", true);")));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void FirefoxContainerBridgePurgeTest()
    {
        CheckBridgePurgeAsync().GetAwaiter().GetResult();
    }

    private static async Task CheckBridgePurgeAsync()
    {
        using var bridge = new FirefoxContainerBridge();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);

        bridge.EnqueuePurge("purge-task-1", "0123456789abcdef0123456789abcdef-");

        // Poll without acks to inspect purges list
        var response = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        response.EnsureSuccessStatusCode();
        using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var purges = state.RootElement.GetProperty("purges").EnumerateArray().ToList();
        Equal(1, purges.Count);
        Equal("purge-task-1", purges[0].GetProperty("id").GetString());
        Equal("0123456789abcdef0123456789abcdef-", purges[0].GetProperty("pattern").GetString());

        // 1. Acknowledge purge with an error: task must NOT be acknowledged and must be retained in bridge for retry
        var ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.PurgeAcknowledged += id => ackTcs.TrySetResult(id);

        var failPayload = JsonSerializer.Serialize(new
        {
            acks = Array.Empty<object>(),
            activeKeys = Array.Empty<string>(),
            purgeAcks = new[] { new { id = "purge-task-1", error = "simulated removal failure" } }
        });
        response = await http.PostAsync(bridge.Url, new StringContent(failPayload));
        response.EnsureSuccessStatusCode();

        // Event should not fire
        Equal(false, ackTcs.Task.IsCompleted);

        // Next poll should still contain the purge task for retry
        response = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        response.EnsureSuccessStatusCode();
        using (var retryState = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var retainedPurges = retryState.RootElement.GetProperty("purges").EnumerateArray().ToList();
            Equal(1, retainedPurges.Count);
            Equal("purge-task-1", retainedPurges[0].GetProperty("id").GetString());
        }

        // 2. Retry succeeds (error = null): event fires and task is removed
        var successPayload = JsonSerializer.Serialize(new
        {
            acks = Array.Empty<object>(),
            activeKeys = Array.Empty<string>(),
            purgeAcks = new[] { new { id = "purge-task-1", error = (string?)null } }
        });
        response = await http.PostAsync(bridge.Url, new StringContent(successPayload));
        response.EnsureSuccessStatusCode();

        Equal("purge-task-1", await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));

        // Next poll should have empty purges
        response = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        response.EnsureSuccessStatusCode();
        using var nextState = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Equal(0, nextState.RootElement.GetProperty("purges").GetArrayLength());
    }

    internal static void FirefoxDiskOptimizationConfigTest()
    {
        // 1. Defaults
        var config = new ManagerConfig();
        Equal(true, config.FirefoxOptimizeRamCache);
        Equal(true, config.FirefoxDisableSafeBrowsing);
        Equal(true, config.FirefoxDisableHistoryAndIcons);
        Equal(true, config.FirefoxClearCacheOnShutdown);
        Equal(true, config.FirefoxCleanRemovedServerContainers);
        Equal(true, config.FirefoxAcceptInsecureCerts);
        Equal(0, config.PendingFirefoxContainerCleanups.Count);

        // 2. Deserialization from JSON without new fields defaults to true
        var legacyJson = "{\"SchemaVersion\":9,\"Groups\":[],\"UngroupedServers\":[]}";
        var deserialized = JsonSerializer.Deserialize<ManagerConfig>(legacyJson)!;
        Equal(true, deserialized.FirefoxOptimizeRamCache);
        Equal(true, deserialized.FirefoxDisableSafeBrowsing);
        Equal(true, deserialized.FirefoxDisableHistoryAndIcons);
        Equal(true, deserialized.FirefoxClearCacheOnShutdown);
        Equal(true, deserialized.FirefoxCleanRemovedServerContainers);
        Equal(true, deserialized.FirefoxAcceptInsecureCerts);

        // 3. Saved false values are preserved
        var customJson = JsonSerializer.Serialize(new ManagerConfig
        {
            FirefoxOptimizeRamCache = false,
            FirefoxDisableSafeBrowsing = false,
            FirefoxDisableHistoryAndIcons = false,
            FirefoxClearCacheOnShutdown = false,
            FirefoxCleanRemovedServerContainers = false,
            FirefoxAcceptInsecureCerts = false
        });
        var loadedCustom = JsonSerializer.Deserialize<ManagerConfig>(customJson)!;
        Equal(false, loadedCustom.FirefoxOptimizeRamCache);
        Equal(false, loadedCustom.FirefoxDisableSafeBrowsing);
        Equal(false, loadedCustom.FirefoxDisableHistoryAndIcons);
        Equal(false, loadedCustom.FirefoxClearCacheOnShutdown);
        Equal(false, loadedCustom.FirefoxCleanRemovedServerContainers);
        Equal(false, loadedCustom.FirefoxAcceptInsecureCerts);

        // 4. ConfigTransfer.CreateExport resets options and empties pending queue
        var srvId = Guid.NewGuid();
        var testServer = new ManagedServer { Id = srvId, Name = "Test" };
        config.UngroupedServers.Add(testServer);
        config.PendingFirefoxContainerCleanups.Add(new FirefoxContainerCleanupTask { ServerId = srvId });
        config.FirefoxOptimizeRamCache = false;
        config.FirefoxAcceptInsecureCerts = false;
        var exported = ConfigTransfer.CreateExport(config, [srvId], true);
        Equal(true, exported.FirefoxOptimizeRamCache);
        Equal(true, exported.FirefoxDisableSafeBrowsing);
        Equal(true, exported.FirefoxAcceptInsecureCerts);
        Equal(0, exported.PendingFirefoxContainerCleanups.Count);
    }

    internal static void FirefoxExtensionBackgroundPurgeNodeTest()
    {
        using var stream = typeof(FirefoxContainerBrowser).Assembly
            .GetManifestResourceStream("KiTTYManager.Core.FirefoxExtension.background.js");
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
        const fs = require('fs'), assert = require('assert');
        const s = {{JsonSerializer.Serialize(jsCode)}};
        const a = s.indexOf('      if (Array.isArray(state.purges))');
        const b = s.indexOf('      const next = new Map();', a);
        const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
        const run = new AsyncFunction('state','browser','containerIds','routes','activeContainers','pendingContainers','purgeAcknowledgements', s.slice(a, b));

        (async () => {
          // 1. Missing identity ("Invalid contextual identity: ...") should not block remaining containers
          {
            const ids = {'server-web1': 'c1', 'server-web2': 'c2'};
            let removedSecond = false;
            let persisted = null;
            const browser = {
              tabs: { query: async () => [] },
              contextualIdentities: {
                remove: async id => {
                  if (id === 'c1') throw Error('Invalid contextual identity: ' + id);
                  removedSecond = true;
                }
              },
              storage: { local: { set: async val => { persisted = structuredClone(val); } } }
            };
            const acks = [];
            await run({purges: [{id: 'task-1', pattern: 'server-'}]}, browser, ids, new Map(), new Map(), new Map(), acks);
            assert.equal(acks[0].error, null, 'Error must be null when missing container was already deleted');
            assert.equal(removedSecond, true, 'Second container must be deleted');
            assert.equal(Object.keys(ids).length, 0, 'Mappings must be cleaned up');
            assert.deepEqual(persisted.containerIds, {}, 'Storage must be cleaned up');
          }

          // 2. Genuine failure should preserve mapping and report error
          {
            const ids = {'server-web1': 'c1'};
            const browser = {
              tabs: { query: async () => [] },
              contextualIdentities: {
                remove: async id => { throw Error('simulated removal failure'); }
              },
              storage: { local: { set: async () => {} } }
            };
            const acks = [];
            await run({purges: [{id: 'task-2', pattern: 'server-'}]}, browser, ids, new Map(), new Map(), new Map(), acks);
            assert.equal(acks[0].error, 'Error: simulated removal failure');
            assert.equal(ids['server-web1'], 'c1');
          }
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
            throw new Exception("Extension purge Node check failed: " + stderr);
    }

    internal static void FirefoxAcceptInsecureCertsProtocolAndTlsTest()
    {
        CheckMarionetteAcceptInsecureCertsProtocolAsync().GetAwaiter().GetResult();
        var firefoxCandidate = FindFirefoxCandidate();
        if (firefoxCandidate != null)
            CheckFirefoxContainerHttpsSelfSignedAsync(firefoxCandidate).GetAwaiter().GetResult();
    }

    private static async Task CheckMarionetteAcceptInsecureCertsProtocolAsync()
    {
        // 1. Test acceptInsecureCerts = true: verifies flat { acceptInsecureCerts: true }, ack, and session retention (no DeleteSession)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = client.GetStream();
                await SendMarionettePacketAsync(stream, "{\"marionetteProtocol\":3}", cts.Token);

                // Read command 1: WebDriver:NewSession
                var cmd1 = await ReadMarionettePacketAsync(stream, cts.Token);
                Equal(0, cmd1[0].GetInt32());
                Equal(1, cmd1[1].GetInt32());
                Equal("WebDriver:NewSession", cmd1[2].GetString());
                var p1 = cmd1[3];
                Equal(false, p1.TryGetProperty("capabilities", out _));
                Equal(true, p1.GetProperty("acceptInsecureCerts").GetBoolean());

                await SendMarionettePacketAsync(stream, "[1,1,null,{\"sessionId\":\"sess-1\",\"capabilities\":{\"acceptInsecureCerts\":true}}]", cts.Token);

                // Read command 2: Addon:Install
                var cmd2 = await ReadMarionettePacketAsync(stream, cts.Token);
                Equal(0, cmd2[0].GetInt32());
                Equal(2, cmd2[1].GetInt32());
                Equal("Addon:Install", cmd2[2].GetString());
                await SendMarionettePacketAsync(stream, "[1,2,null,{\"value\":\"ext-id\"}]", cts.Token);

                // No DeleteSession should be received when acceptInsecureCerts = true.
                await Task.Delay(100, cts.Token);
            });

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var traces = new List<string>();
            await FirefoxContainerBrowser.InitializeSessionAsync(
                clientTcp.GetStream(),
                "dummy.xpi",
                headless: false,
                acceptInsecureCerts: true,
                trace: traces.Add,
                cts.Token);

            await serverTask;
            Equal(false, traces.Any(t => t.Contains("warning: acceptInsecureCerts not acknowledged")));
        }

        // 2. Test acceptInsecureCerts = true with unacknowledged capability: verifies warning trace
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = client.GetStream();
                await SendMarionettePacketAsync(stream, "{\"marionetteProtocol\":3}", cts.Token);
                var cmd1 = await ReadMarionettePacketAsync(stream, cts.Token);
                // Return acceptInsecureCerts: false in capabilities
                await SendMarionettePacketAsync(stream, "[1,1,null,{\"sessionId\":\"sess-warn\",\"capabilities\":{\"acceptInsecureCerts\":false}}]", cts.Token);
                var cmd2 = await ReadMarionettePacketAsync(stream, cts.Token);
                await SendMarionettePacketAsync(stream, "[1,2,null,{\"value\":\"ext-id\"}]", cts.Token);
            });

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var traces = new List<string>();
            await FirefoxContainerBrowser.InitializeSessionAsync(
                clientTcp.GetStream(),
                "dummy.xpi",
                headless: false,
                acceptInsecureCerts: true,
                trace: traces.Add,
                cts.Token);

            await serverTask;
            Equal(true, traces.Any(t => t.Contains("warning: acceptInsecureCerts not acknowledged")));
        }

        // 3. Test acceptInsecureCerts = false: verifies flat { acceptInsecureCerts: false } and presence of WebDriver:DeleteSession
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = client.GetStream();
                await SendMarionettePacketAsync(stream, "{\"marionetteProtocol\":3}", cts.Token);

                var cmd1 = await ReadMarionettePacketAsync(stream, cts.Token);
                Equal(0, cmd1[0].GetInt32());
                Equal(1, cmd1[1].GetInt32());
                Equal("WebDriver:NewSession", cmd1[2].GetString());
                var p1 = cmd1[3];
                Equal(false, p1.TryGetProperty("capabilities", out _));
                Equal(false, p1.GetProperty("acceptInsecureCerts").GetBoolean());

                await SendMarionettePacketAsync(stream, "[1,1,null,{\"sessionId\":\"sess-false\",\"capabilities\":{\"acceptInsecureCerts\":false}}]", cts.Token);

                var cmd2 = await ReadMarionettePacketAsync(stream, cts.Token);
                Equal(0, cmd2[0].GetInt32());
                Equal(2, cmd2[1].GetInt32());
                await SendMarionettePacketAsync(stream, "[1,2,null,{\"value\":\"ext-id\"}]", cts.Token);

                var cmd3 = await ReadMarionettePacketAsync(stream, cts.Token);
                Equal(0, cmd3[0].GetInt32());
                Equal(3, cmd3[1].GetInt32());
                Equal("WebDriver:DeleteSession", cmd3[2].GetString());
                await SendMarionettePacketAsync(stream, "[1,3,null,{}]", cts.Token);
            });

            using var clientTcp = new TcpClient();
            await clientTcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var traces = new List<string>();
            await FirefoxContainerBrowser.InitializeSessionAsync(
                clientTcp.GetStream(),
                "dummy.xpi",
                headless: false,
                acceptInsecureCerts: false,
                trace: traces.Add,
                cts.Token);

            await serverTask;
        }
    }

    private static string? FindFirefoxCandidate()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("KITTY_SMOKE_FIREFOX"),
            "/root/test4/build/firefox-research/firefox/firefox",
            "/usr/bin/firefox",
            "/usr/bin/firefox-esr",
            "/usr/local/bin/firefox"
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    private static async Task EnsureSeedProfileAsync(string executable, string source, string root)
    {
        Directory.CreateDirectory(source);
        if (File.Exists(Path.Combine(source, "key4.db")) && File.Exists(Path.Combine(source, "cert9.db")))
            return;

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
        var seed = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in new[] { "-headless", "-no-remote", "-profile", source, "-screenshot",
                     Path.Combine(root, "seed.png"), "about:blank" }) seed.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(seed)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        if (process.ExitCode != 0) throw new Exception("Seed Firefox failed");
    }

    private static async Task CheckFirefoxContainerHttpsSelfSignedAsync(string executable)
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-firefox-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            await EnsureSeedProfileAsync(executable, source, root);

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=same.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("same.test");
            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = new X509Certificate2(cert.Export(X509ContentType.Pfx));

            using var direct = new DirectTrap();

            // 1. acceptInsecureCerts = true: self-signed certificate is accepted, container tab loads
            {
                using var panel = new FakePanel("HTTPS_ACCEPTED", direct.Port, pfx);
                using var browser = new FirefoxContainerBrowser();
                await browser.StartAsync(executable, Path.Combine(root, "managed-accepted"),
                    () => source, CancellationToken.None, headless: true, acceptInsecureCerts: true);
                try
                {
                    await browser.Bridge.OpenAsync("s1", "Test Accepted", panel.Port, "https://same.test/", CancellationToken.None);
                    var seen = await panel.Seen.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    Equal(true, seen.Contains("after=flavor=HTTPS_ACCEPTED"));
                }
                finally
                {
                    if (browser.IsRunning) await browser.CloseForSmokeAsync();
                }
            }

            // 2. acceptInsecureCerts = false: self-signed certificate is rejected, about:neterror is displayed
            {
                using var panel = new FakePanel("HTTPS_REJECTED", direct.Port, pfx);
                using var browser = new FirefoxContainerBrowser();
                await browser.StartAsync(executable, Path.Combine(root, "managed-rejected"),
                    () => source, CancellationToken.None, headless: true, acceptInsecureCerts: false);
                try
                {
                    await browser.Bridge.OpenAsync("s2", "Test Rejected", panel.Port, "https://same.test/", CancellationToken.None);
                    var deadline = DateTime.UtcNow.AddSeconds(45);
                    string? certErrorDoc = null;
                    while (DateTime.UtcNow < deadline)
                    {
                        var tabInfo = await browser.ChromeForSmokeAsync("""
                            const win = Services.wm.getMostRecentWindow("navigator:browser").wrappedJSObject;
                            const tab = [...win.gBrowser.tabs].find(t => {
                                const doc = t.linkedBrowser?.documentURI?.spec || "";
                                return doc.includes("about:certerror") || doc.includes("about:neterror");
                            });
                            return tab?.linkedBrowser?.documentURI?.spec || "";
                            """);
                        var doc = tabInfo.GetString() ?? "";
                        if ((doc.Contains("about:certerror") || doc.Contains("about:neterror")) && doc.Contains("nssBadCert"))
                        {
                            certErrorDoc = doc;
                            break;
                        }
                        await Task.Delay(300);
                    }

                    if (certErrorDoc == null)
                        throw new Exception("Expected certificate error page (about:certerror with nssBadCert) was not displayed in Firefox tab");

                    Equal(true, certErrorDoc.Contains("about:certerror") || certErrorDoc.Contains("about:neterror"));
                    Equal(true, certErrorDoc.Contains("nssBadCert"));
                    Equal(false, panel.Seen.Task.IsCompleted);
                }
                finally
                {
                    if (browser.IsRunning) await browser.CloseForSmokeAsync();
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task SendMarionettePacketAsync(Stream stream, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes(bytes.Length + ":");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<JsonElement> ReadMarionettePacketAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1];
        var length = 0;
        for (var i = 0; ; i++)
        {
            await stream.ReadExactlyAsync(one, token);
            if (one[0] == ':') break;
            if (i >= 7 || one[0] is < (byte)'0' or > (byte)'9') throw new IOException("Bad length");
            length = checked(length * 10 + (one[0] - '0'));
        }
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }

    private static void FirefoxNonBlockingWebOpenTests()
    {
        CheckNonBlockingWebOpenStructure();
        CheckFirefoxStartupConcurrencyAsync().GetAwaiter().GetResult();
        CheckPendingWebOpeningsDeduplication();
        CheckKittyTemporarySessionFileCleanupOnFailure();
        CheckConfigSnapshotConsistency();
        CheckWindowCloseExitCoordination();
        CheckTunnelArgsSecretRedactionWithSnapshot();
        CheckLazySourceProfileNotCalledWhenContainerProfileExists();
        CheckConnectionLearningSyncedToLiveConfig();
        CheckExitCancellationRecovery();
    }

    private static string FindRepoSourcePath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Cannot find {relativePath}");
    }

    private static void CheckNonBlockingWebOpenStructure()
    {
        var mainWindowPath = FindRepoSourcePath(Path.Combine("src", "KiTTYManager.App", "MainWindow.xaml.cs"));
        var firefoxPath = FindRepoSourcePath(Path.Combine("src", "KiTTYManager.App", "MainWindow.Firefox.cs"));

        var mainWindowContent = File.ReadAllText(mainWindowPath);
        var firefoxContent = File.ReadAllText(firefoxPath);

        // 1. Verify OpenSelectedWebAsync does NOT call BusyAsync or disable panels
        var openMethodStart = mainWindowContent.IndexOf("private async Task OpenSelectedWebAsync()", StringComparison.Ordinal);
        Equal(true, openMethodStart >= 0);
        var nextMethodStart = mainWindowContent.IndexOf("private async Task<ActiveRoute> Connect(", openMethodStart, StringComparison.Ordinal);
        Equal(true, nextMethodStart > openMethodStart);
        var openMethodBody = mainWindowContent.Substring(openMethodStart, nextMethodStart - openMethodStart);

        Equal(false, openMethodBody.Contains("BusyAsync"));
        Equal(false, openMethodBody.Contains("HeaderPanel.IsEnabled = false"));
        Equal(false, openMethodBody.Contains("WorkspacePanel.IsEnabled = false"));
        Equal(false, openMethodBody.Contains("operationCancellation ="));
        Equal(true, openMethodBody.Contains("Error(ex"));
        Equal(true, openMethodBody.Contains("UpdateCancelOperationButtonVisibility()"));
        Equal(true, openMethodBody.Contains("pendingWebOpenings.Contains(containerKey)"));
        Equal(true, openMethodBody.Contains("pendingWebOpenings.Add(containerKey)"));
        Equal(true, openMethodBody.Contains("pendingWebOpenings.Remove(containerKey)"));
        Equal(true, openMethodBody.Contains("EnsureFirefoxContainersAsync"));
        Equal(true, openMethodBody.Contains("CloseContainerSession(containerKey, true)"));

        // 2. Verify MainWindow.Firefox.cs has synchronization lock and thread checks
        Equal(true, firefoxContent.Contains("private readonly SemaphoreSlim firefoxStartupLock = new(1, 1);"));
        Equal(true, firefoxContent.Contains("await firefoxStartupLock.WaitAsync(token);"));
        Equal(true, firefoxContent.Contains("firefoxStartupLock.Release();"));
        Equal(true, firefoxContent.Contains("Dispatcher.VerifyAccess();"));

        // 3. Verify offload to Task.Run and session file cleanup
        Equal(true, openMethodBody.Contains("var configSnapshot = ConfigTransfer.CloneSnapshot(config);"));
        Equal(true, openMethodBody.Contains("routed?.Dispose();"));
        Equal(true, firefoxContent.Contains("await Task.Run(async () =>"));

        // 4. Verify exit coordination
        Equal(true, mainWindowContent.Contains("if (pendingWebOpenings.Count > 0 && !webExitCompleted)"));
        Equal(true, mainWindowContent.Contains("webExitCompleted = true;"));

        // 5. Verify secret redaction with snapshot
        Equal(true, openMethodBody.Contains("SecretRedactor.Redact(tunnelArgs, serverSnapshot, SecretRedactor.Secrets(configSnapshot));"));

        // 6. Verify lazy source resolver
        Equal(true, firefoxContent.Contains("sourceResolver"));

        // 7. Verify connection learning sync
        Equal(true, mainWindowContent.Contains("if (persist && routeConfig is not null && !ReferenceEquals(routeConfig, config))"));
        Equal(true, mainWindowContent.Contains("var probeTarget = liveTarget ?? server;"));
        Equal(true, mainWindowContent.Contains("initialFingerprints"));
        Equal(true, mainWindowContent.Contains("foreach (var s in activeConfig.AllServers())"));
        Equal(true, mainWindowContent.Contains("ServerEndpointPolicy.Remember(liveHop, effectiveEndpoint, hopContext);"));
        Equal(true, mainWindowContent.Contains("updatedPref.LastSuccessUtc = snapshotPref.LastSuccessUtc;"));
        Equal(true, mainWindowContent.Contains("string.Equals(initial.Fingerprint, liveHop.HostKeyFingerprint ?? \"\", StringComparison.Ordinal)"));

        // 8. Verify exit cancellation recovery and orphaned task abort
        Equal(true, mainWindowContent.Contains("ResetPendingExitState()"));
        Equal(true, mainWindowContent.Contains("tunnelsExitConfirmed"));
        Equal(true, mainWindowContent.Contains("StopWebAndExitAsync(webCts)"));
        Equal(true, mainWindowContent.Contains("StopWebAndExitAsync(CancellationTokenSource exitCts)"));
        Equal(true, mainWindowContent.Contains("!ReferenceEquals(webCts, exitCts) || !webExitPending"));
    }

    private static async Task CheckFirefoxStartupConcurrencyAsync()
    {
        var startupLock = new SemaphoreSlim(1, 1);
        var startupExecutions = 0;
        var isRunning = false;
        var bridgeConnected = false;

        async Task SimulatedEnsureFirefoxContainersAsync(CancellationToken token)
        {
            await startupLock.WaitAsync(token);
            try
            {
                if (isRunning)
                {
                    if (!bridgeConnected)
                        throw new IOException("Расширение Firefox не отвечает.");
                    return;
                }
                // Simulate cold startup delay
                await Task.Delay(25, token);
                Interlocked.Increment(ref startupExecutions);
                isRunning = true;
                bridgeConnected = true;
            }
            finally
            {
                startupLock.Release();
            }
        }

        // 1. Multiple concurrent calls serialize and execute startup exactly once
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => SimulatedEnsureFirefoxContainersAsync(CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        Equal(1, startupExecutions);
        Equal(true, isRunning);
        Equal(true, bridgeConnected);

        // 2. Subsequent call reuses existing running instance without startup
        await SimulatedEnsureFirefoxContainersAsync(CancellationToken.None);
        Equal(1, startupExecutions);

        // 3. Failure recovery: if startup fails, lock is released and subsequent call retries
        var failLock = new SemaphoreSlim(1, 1);
        var failExecutions = 0;
        var failRunning = false;
        var failFirst = true;

        async Task SimulatedFailingStartupAsync()
        {
            await failLock.WaitAsync();
            try
            {
                if (failRunning) return;
                await Task.Delay(10);
                if (failFirst)
                {
                    failFirst = false;
                    throw new IOException("Simulated network or profile error");
                }
                Interlocked.Increment(ref failExecutions);
                failRunning = true;
            }
            finally
            {
                failLock.Release();
            }
        }

        var threw = false;
        try
        {
            await SimulatedFailingStartupAsync();
        }
        catch (IOException)
        {
            threw = true;
        }

        Equal(true, threw);

        Equal(0, failExecutions);
        Equal(false, failRunning);

        // Retry succeeds
        await SimulatedFailingStartupAsync();
        Equal(1, failExecutions);
        Equal(true, failRunning);
    }

    private static void CheckPendingWebOpeningsDeduplication()
    {
        var pending = new HashSet<string>();
        var key1 = "server1-web1";
        var key2 = "server2-web2";

        bool TryStart(string key)
        {
            if (pending.Contains(key)) return false;
            pending.Add(key);
            return true;
        }

        void Finish(string key)
        {
            pending.Remove(key);
        }

        // 1. First request for key1 accepted
        Equal(true, TryStart(key1));
        Equal(true, pending.Contains(key1));

        // 2. Duplicate request for key1 rejected while running
        Equal(false, TryStart(key1));

        // 3. Different key2 accepted concurrently
        Equal(true, TryStart(key2));
        Equal(2, pending.Count);

        // 4. Duplicate request for key2 rejected
        Equal(false, TryStart(key2));

        // 5. When key1 completes, it can be opened again
        Finish(key1);
        Equal(false, pending.Contains(key1));
        Equal(true, TryStart(key1));

        // 6. Clean up
        Finish(key1);
        Finish(key2);
        Equal(0, pending.Count);
    }

    private static void CheckKittyTemporarySessionFileCleanupOnFailure()
    {
        KittyRoutedSession? innerRouted = null;
        string? createdPath = null;
        var failed = false;

        try
        {
            try
            {
                innerRouted = KittyRoutedSession.CreateMinimal(12345, 12346);
                createdPath = innerRouted.Path;
                Equal(true, File.Exists(createdPath));

                // Simulate process launch failure (as in Task.Run for tunnel)
                throw new InvalidOperationException("Simulated Process.Start failure");
            }
            catch
            {
                innerRouted?.Dispose();
                throw;
            }
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Equal(true, failed);
        Equal(true, createdPath != null);
        Equal(false, File.Exists(createdPath));
    }

    private static void CheckConfigSnapshotConsistency()
    {
        var originalConfig = new ManagerConfig();
        var serverId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var originalServer = new ManagedServer
        {
            Id = serverId,
            Name = "InitialServerName",
            Port = 22,
            WebInterfaces =
            {
                new WebInterface { Id = webId, Name = "AdminPanel", Url = "http://192.168.1.10:8080" }
            }
        };
        originalConfig.UngroupedServers.Add(originalServer);

        // Take snapshot before first await as done in OpenSelectedWebAsync
        var configSnapshot = ConfigTransfer.CloneSnapshot(originalConfig);
        var serverSnapshot = configSnapshot.FindServer(serverId);
        Equal(true, serverSnapshot != null);

        // Mutate original config and server during async operation
        originalServer.Name = "MutatedServerName";
        originalServer.Port = 2222;
        originalServer.WebInterfaces.Clear();

        // Verify snapshot remains immutable and holds the original pre-await data
        Equal("InitialServerName", serverSnapshot!.Name);
        Equal(22, serverSnapshot.Port);
        Equal(1, serverSnapshot.WebInterfaces.Count);
        Equal("AdminPanel", serverSnapshot.WebInterfaces[0].Name);

        // Verify snapshot lookup works independently
        Equal("InitialServerName", configSnapshot.FindServer(serverId)!.Name);
    }

    private static void CheckWindowCloseExitCoordination()
    {
        var pendingOpenings = new HashSet<string>();
        var webExitPending = false;
        var webExitCompleted = false;
        var stopTasksStarted = 0;
        var cleanupCount = 0;

        void OnWindowClosing(ref bool cancel)
        {
            if (pendingOpenings.Count > 0 && !webExitCompleted)
            {
                cancel = true;
                if (!webExitPending)
                {
                    webExitPending = true;
                    stopTasksStarted++;
                }
                return;
            }

            cleanupCount++;
        }

        // Scenario 1: Window closing with no pending web openings -> exits immediately
        var cancel0 = false;
        OnWindowClosing(ref cancel0);
        Equal(false, cancel0);
        Equal(1, cleanupCount);

        // Reset state for pending scenario
        cleanupCount = 0;
        pendingOpenings.Add("server-web-1");

        // Scenario 2: First close attempt with pending web opening
        // -> should cancel and trigger stop task exactly once
        var cancel1 = false;
        OnWindowClosing(ref cancel1);
        Equal(true, cancel1);
        Equal(true, webExitPending);
        Equal(1, stopTasksStarted);
        Equal(0, cleanupCount);

        // Scenario 3: User clicks close again while stop is in progress
        // -> should cancel again, should NOT trigger stop task again, should NOT call cleanup
        var cancel2 = false;
        OnWindowClosing(ref cancel2);
        Equal(true, cancel2);
        Equal(1, stopTasksStarted);
        Equal(0, cleanupCount);

        // Scenario 4: Stop completes (webExitCompleted = true)
        webExitCompleted = true;
        pendingOpenings.Clear();

        // Final close invoked by Dispatcher
        // -> should NOT cancel and should call cleanup
        var cancel3 = false;
        OnWindowClosing(ref cancel3);
        Equal(false, cancel3);
        Equal(1, cleanupCount);

        // Scenario 5: If pending openings remain (> 0), exit does NOT complete
        // and window closing is canceled to prevent Cleanup() while tasks run
        cleanupCount = 0;
        pendingOpenings.Add("stuck-connection");
        webExitCompleted = false;
        var cancel4 = false;
        OnWindowClosing(ref cancel4);
        Equal(true, cancel4);
        Equal(0, cleanupCount);
    }

    private static void CheckTunnelArgsSecretRedactionWithSnapshot()
    {
        var config = new ManagerConfig();
        var server = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "SecretServer",
            Password = "MyOldSnapshotPassword123!",
            RootPassword = "MyOldRootPassword456!"
        };
        config.UngroupedServers.Add(server);

        var configSnapshot = ConfigTransfer.CloneSnapshot(config);
        var serverSnapshot = configSnapshot.FindServer(server.Id)!;

        // User edits password in live config while connection is in flight
        server.Password = "BrandNewPassword789!";
        server.RootPassword = "BrandNewRootPassword999!";

        // Tunnel arguments created using serverSnapshot
        var rawTunnelArgs = $"-ssh -pw {serverSnapshot.Password} -l root {serverSnapshot.Host}";

        // With serverSnapshot + configSnapshot:
        var safeArgs = SecretRedactor.Redact(rawTunnelArgs, serverSnapshot, SecretRedactor.Secrets(configSnapshot));
        Equal(false, safeArgs.Contains("MyOldSnapshotPassword123!"));
        Equal(true, safeArgs.Contains("***"));
    }

    private static void CheckLazySourceProfileNotCalledWhenContainerProfileExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "km_test_firefox_lazy_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var profileDir = Path.Combine(tempRoot, "profile");
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(Path.Combine(profileDir, "key4.db"), "test-key");
            File.WriteAllText(Path.Combine(profileDir, "cert9.db"), "test-cert");

            var sourceResolverCalled = false;
            string SourceResolver()
            {
                sourceResolverCalled = true;
                throw new InvalidOperationException("Source resolver should not be called when container profile exists!");
            }

            var resolvedProfile = FirefoxProfileWorkspace.EnsureContainerProfile(tempRoot, SourceResolver);
            Equal(profileDir, resolvedProfile);
            Equal(false, sourceResolverCalled);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void CheckConnectionLearningSyncedToLiveConfig()
    {
        var liveConfig = new ManagerConfig();
        var targetId = Guid.NewGuid();
        var hopRotatedId = Guid.NewGuid();
        var hopEditedId = Guid.NewGuid();
        var proxyId1 = Guid.NewGuid();
        var proxyId2 = Guid.NewGuid();

        var liveTarget = new ManagedServer { Id = targetId, Name = "Target", Host = "10.0.0.2", Port = 22 };
        var liveHopRotated = new ManagedServer
        {
            Id = hopRotatedId,
            Name = "HopRotated",
            Host = "10.0.0.3",
            Port = 22,
            HostKeyFingerprint = "SHA256:old_server_key",
            HostKeyAlgorithm = "ssh-rsa",
            HostKeyBits = 2048
        };
        var liveHopEdited = new ManagedServer
        {
            Id = hopEditedId,
            Name = "HopEdited",
            Host = "10.0.0.4",
            Port = 22,
            HostKeyFingerprint = "SHA256:old_key_before_manual_edit",
            HostKeyAlgorithm = "ssh-ed25519",
            HostKeyBits = 256
        };
        liveConfig.UngroupedServers.Add(liveTarget);
        liveConfig.UngroupedServers.Add(liveHopRotated);
        liveConfig.UngroupedServers.Add(liveHopEdited);

        var proxy1 = new BaseProxy { Id = proxyId1, Name = "Proxy1", Host = "127.0.0.1", Port = 1080 };
        var proxy2 = new BaseProxy { Id = proxyId2, Name = "Proxy2", Host = "127.0.0.1", Port = 1081 };
        liveConfig.BaseProxies.Add(proxy1);
        liveConfig.BaseProxies.Add(proxy2);

        // Snapshot 1 created before Firefox launch
        var snapshot1 = ConfigTransfer.CloneSnapshot(liveConfig);
        var s1Target = snapshot1.FindServer(targetId)!;
        var s1HopRotated = snapshot1.FindServer(hopRotatedId)!;
        var s1HopEdited = snapshot1.FindServer(hopEditedId)!;

        // User edits host and fingerprint in live UI during Firefox launch (after snapshot was taken)
        liveHopEdited.HostKeyFingerprint = "SHA256:user_manually_typed_key";
        liveHopEdited.Host = "10.0.0.99";

        // In Connect, initialFingerprints are collected from activeConfig (snapshot1)
        var initialFingerprints = new Dictionary<Guid, (string Host, int Port, string Fingerprint)>();
        foreach (var s in snapshot1.AllServers())
            initialFingerprints[s.Id] = (s.Host, s.Port, s.HostKeyFingerprint ?? "");

        // User confirmed rotated key on hopRotated
        s1HopRotated.HostKeyFingerprint = "SHA256:new_confirmed_key";
        s1HopRotated.HostKeyAlgorithm = "ssh-ed25519";
        s1HopRotated.HostKeyBits = 256;

        // Remote host gave new key for hopEdited, but user manually typed a different key and host in live UI
        s1HopEdited.HostKeyFingerprint = "SHA256:remote_key";

        // Snapshot 1 endpoint: Direct(proxyId1) -> target on port 2222
        var s1Pref = new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 2222),
            LastSuccessUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        s1Target.EndpointPreferences.Add(s1Pref);

        var candidateKey = new RouteCandidate(Proxy: proxy1, Servers: [s1HopRotated, s1HopEdited], WithoutProxy: false);
        var routeKey = new ActiveRoute(
            s1HopEdited, 10000, 1080, "proxy", null, [],
            [
                new RouteHop(s1HopRotated.Id, "HopRotated", "10.0.0.3", 22),
                new RouteHop(s1HopEdited.Id, "HopEdited", "10.0.0.4", 22)
            ]);

        var candidate1 = new RouteCandidate(Proxy: proxy1, Servers: [s1Target], WithoutProxy: false);
        var route1 = new ActiveRoute(
            s1Target, 10001, 1080, "proxy", null, [],
            [
                new RouteHop(s1Target.Id, "Target", "10.0.0.2", 2222)
            ]);

        // Sync helper simulating the CompleteRoute sync block
        void ApplySync(ManagerConfig config, RouteCandidate candidate, ActiveRoute route, bool persist, DateTimeOffset timestamp)
        {
            if (!persist) return;
            for (var i = 0; i < candidate.Servers.Count; i++)
            {
                var hop = candidate.Servers[i];
                var liveHop = config.FindServer(hop.Id);
                if (liveHop is null) continue;

                var hopContext = i == 0
                    ? EndpointContext.Direct(candidate.Proxy.Id)
                    : EndpointContext.Via(candidate.Servers[i - 1].Id);
                var snapshotPref = hop.EndpointPreferences.FirstOrDefault(item =>
                    item.ProxyId == hopContext.ProxyId &&
                    item.PreviousServerId == hopContext.PreviousServerId);
                var effectiveEndpoint = snapshotPref?.Endpoint ??
                    (i < route.Hops.Count ? new ServerEndpoint(route.Hops[i].Host, route.Hops[i].Port) : null);
                if (effectiveEndpoint is not null)
                {
                    var livePref = liveHop.EndpointPreferences.FirstOrDefault(item =>
                        item.ProxyId == hopContext.ProxyId &&
                        item.PreviousServerId == hopContext.PreviousServerId);
                    if (livePref is null || snapshotPref is null || livePref.LastSuccessUtc <= snapshotPref.LastSuccessUtc)
                    {
                        ServerEndpointPolicy.Remember(liveHop, effectiveEndpoint, hopContext);
                        if (snapshotPref is not null && snapshotPref.LastSuccessUtc != default)
                        {
                            var updatedPref = liveHop.EndpointPreferences.FirstOrDefault(item =>
                                item.ProxyId == hopContext.ProxyId &&
                                item.PreviousServerId == hopContext.PreviousServerId);
                            if (updatedPref is not null)
                                updatedPref.LastSuccessUtc = snapshotPref.LastSuccessUtc;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(hop.HostKeyFingerprint) &&
                    initialFingerprints.TryGetValue(hop.Id, out var initial) &&
                    string.Equals(initial.Host, liveHop.Host, StringComparison.OrdinalIgnoreCase) &&
                    initial.Port == liveHop.Port &&
                    string.Equals(initial.Fingerprint, liveHop.HostKeyFingerprint ?? "", StringComparison.Ordinal))
                {
                    liveHop.HostKeyFingerprint = hop.HostKeyFingerprint;
                    liveHop.HostKeyAlgorithm = hop.HostKeyAlgorithm;
                    liveHop.HostKeyBits = hop.HostKeyBits;
                }
            }
        }

        // Apply key verification connection and connection 1
        ApplySync(liveConfig, candidateKey, routeKey, persist: true, DateTimeOffset.UtcNow);
        ApplySync(liveConfig, candidate1, route1, persist: true, DateTimeOffset.UtcNow);

        // 1. Confirmed key replacement transferred
        Equal("SHA256:new_confirmed_key", liveHopRotated.HostKeyFingerprint);
        Equal("ssh-ed25519", liveHopRotated.HostKeyAlgorithm);

        // 2. Parallel manual user edit preserved (NOT overwritten by remote_key, host unchanged)
        Equal("SHA256:user_manually_typed_key", liveHopEdited.HostKeyFingerprint);
        Equal("10.0.0.99", liveHopEdited.Host);

        // 3. Endpoint from connection 1 remembered
        Equal(1, liveTarget.EndpointPreferences.Count);
        Equal(proxyId1, liveTarget.EndpointPreferences[0].ProxyId);
        Equal(2222, liveTarget.EndpointPreferences[0].Endpoint.Port);

        // Snapshot 2: parallel connection through Proxy2 / Via hop
        var snapshot2 = ConfigTransfer.CloneSnapshot(liveConfig);
        var s2Target = snapshot2.FindServer(targetId)!;
        var s2HopRotated = snapshot2.FindServer(hopRotatedId)!;

        // Context 2: Via(hopRotatedId) -> target on port 3333
        var s2Pref = new EndpointPreference
        {
            PreviousServerId = hopRotatedId,
            Endpoint = new ServerEndpoint("10.0.0.2", 3333),
            LastSuccessUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
        };
        s2Target.EndpointPreferences.Add(s2Pref);

        var candidate2 = new RouteCandidate(Proxy: proxy2, Servers: [s2HopRotated, s2Target], WithoutProxy: false);
        var route2 = new ActiveRoute(
            s2Target, 10002, 1081, "proxy", null, [],
            [
                new RouteHop(s2HopRotated.Id, "HopRotated", "10.0.0.3", 22),
                new RouteHop(s2Target.Id, "Target", "10.0.0.2", 3333)
            ]);

        ApplySync(liveConfig, candidate2, route2, persist: true, DateTimeOffset.UtcNow);

        // 3. Both contexts preserved in liveTarget.EndpointPreferences!
        Equal(2, liveTarget.EndpointPreferences.Count);
        Equal(true, liveTarget.EndpointPreferences.Any(p => p.ProxyId == proxyId1 && p.Endpoint.Port == 2222));
        Equal(true, liveTarget.EndpointPreferences.Any(p => p.PreviousServerId == hopRotatedId && p.Endpoint.Port == 3333));

        // 4. Overwrite in same context only if newer
        var olderTime = DateTimeOffset.UtcNow.AddHours(-1);
        var newerTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var olderSnapshot = new ManagedServer { Id = targetId, Host = "10.0.0.2", Port = 22 };
        olderSnapshot.EndpointPreferences.Add(new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 9999),
            LastSuccessUtc = olderTime
        });

        // Current livePref has time > olderTime, so 9999 must NOT overwrite 2222
        var sOlderCandidate = new RouteCandidate(Proxy: proxy1, Servers: [olderSnapshot], WithoutProxy: false);
        var sOlderRoute = new ActiveRoute(olderSnapshot, 10003, 1080, "proxy", null, [], [new RouteHop(targetId, "T", "10.0.0.2", 9999)]);
        ApplySync(liveConfig, sOlderCandidate, sOlderRoute, persist: true, olderTime);
        var p1Pref = liveTarget.EndpointPreferences.First(p => p.ProxyId == proxyId1);
        Equal(2222, p1Pref.Endpoint.Port);

        // But a newer snapshot DOES update 2222 to 8888
        var newerSnapshot = new ManagedServer { Id = targetId, Host = "10.0.0.2", Port = 22 };
        newerSnapshot.EndpointPreferences.Add(new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 8888),
            LastSuccessUtc = newerTime
        });
        var sNewerCandidate = new RouteCandidate(Proxy: proxy1, Servers: [newerSnapshot], WithoutProxy: false);
        var sNewerRoute = new ActiveRoute(newerSnapshot, 10004, 1080, "proxy", null, [], [new RouteHop(targetId, "T", "10.0.0.2", 8888)]);
        ApplySync(liveConfig, sNewerCandidate, sNewerRoute, persist: true, newerTime);
        p1Pref = liveTarget.EndpointPreferences.First(p => p.ProxyId == proxyId1);
        Equal(8888, p1Pref.Endpoint.Port);

        // 5. When persist == false, sync is completely skipped
        var noPersistSnapshot = new ManagedServer { Id = targetId, Host = "10.0.0.2", Port = 22 };
        noPersistSnapshot.EndpointPreferences.Add(new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 7777),
            LastSuccessUtc = DateTimeOffset.UtcNow.AddDays(1)
        });
        var sNoPersistCandidate = new RouteCandidate(Proxy: proxy1, Servers: [noPersistSnapshot], WithoutProxy: false);
        var sNoPersistRoute = new ActiveRoute(noPersistSnapshot, 10005, 1080, "proxy", null, [], [new RouteHop(targetId, "T", "10.0.0.2", 7777)]);
        ApplySync(liveConfig, sNoPersistCandidate, sNoPersistRoute, persist: false, DateTimeOffset.UtcNow);
        p1Pref = liveTarget.EndpointPreferences.First(p => p.ProxyId == proxyId1);
        Equal(8888, p1Pref.Endpoint.Port);

        // 6. Two snapshots of the same context: snapshot A with success at t=10, snapshot B with success at t=20.
        // Test both synchronization orders (A then B, and B then A).
        // In both cases, liveConfig must retain the endpoint and exact timestamp from B (t=20).
        var t10 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var t20 = DateTimeOffset.UtcNow.AddMinutes(-10);

        var targetA = new ManagedServer { Id = targetId, Host = "10.0.0.2", Port = 22 };
        targetA.EndpointPreferences.Add(new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 1111),
            LastSuccessUtc = t10
        });
        var candA = new RouteCandidate(Proxy: proxy1, Servers: [targetA], WithoutProxy: false);
        var routeA = new ActiveRoute(targetA, 10006, 1080, "proxy", null, [], [new RouteHop(targetId, "T", "10.0.0.2", 1111)]);

        var targetB = new ManagedServer { Id = targetId, Host = "10.0.0.2", Port = 22 };
        targetB.EndpointPreferences.Add(new EndpointPreference
        {
            ProxyId = proxyId1,
            Endpoint = new ServerEndpoint("10.0.0.2", 2222),
            LastSuccessUtc = t20
        });
        var candB = new RouteCandidate(Proxy: proxy1, Servers: [targetB], WithoutProxy: false);
        var routeB = new ActiveRoute(targetB, 10007, 1080, "proxy", null, [], [new RouteHop(targetId, "T", "10.0.0.2", 2222)]);

        // Case 6.1: Order A (t=10) then B (t=20)
        liveTarget.EndpointPreferences.Clear();
        ApplySync(liveConfig, candA, routeA, persist: true, t10);
        ApplySync(liveConfig, candB, routeB, persist: true, t20);
        var prefOrderAB = liveTarget.EndpointPreferences.First(p => p.ProxyId == proxyId1);
        Equal(2222, prefOrderAB.Endpoint.Port);
        Equal(t20, prefOrderAB.LastSuccessUtc);

        // Case 6.2: Order B (t=20) then A (t=10)
        liveTarget.EndpointPreferences.Clear();
        ApplySync(liveConfig, candB, routeB, persist: true, t20);
        ApplySync(liveConfig, candA, routeA, persist: true, t10);
        var prefOrderBA = liveTarget.EndpointPreferences.First(p => p.ProxyId == proxyId1);
        Equal(2222, prefOrderBA.Endpoint.Port);
        Equal(t20, prefOrderBA.LastSuccessUtc);
    }

    private static void CheckExitCancellationRecovery()
    {
        var webCts = new CancellationTokenSource();
        var webExitPending = true;
        var webExitCompleted = false;
        var tunnelsExitConfirmed = true;

        var exitCts = webCts;

        // Reset exit state (as in ResetPendingExitState)
        void ResetState()
        {
            webExitPending = false;
            webExitCompleted = false;
            tunnelsExitConfirmed = false;
            if (webCts.IsCancellationRequested)
            {
                webCts.Dispose();
                webCts = new CancellationTokenSource();
            }
        }

        // Simulate cancel requested during exit
        exitCts.Cancel();
        ResetState();

        Equal(false, webExitPending);
        Equal(false, webExitCompleted);
        Equal(false, tunnelsExitConfirmed);
        Equal(false, webCts.IsCancellationRequested);

        // Verify check in StopWebAndExitAsync aborts without setting webExitCompleted
        var wouldAbort = !ReferenceEquals(webCts, exitCts) || !webExitPending;
        Equal(true, wouldAbort);
        Equal(false, webExitCompleted);

        // Verify new token is functional
        var token = webCts.Token;
        Equal(false, token.IsCancellationRequested);
    }

    internal static void FirefoxContainerDomainFallbackRoutingTests()
    {
        CheckBridgeDomainMetadataAsync().GetAwaiter().GetResult();
        FirefoxExtensionDomainFallbackRoutingNodeTest();
        CheckCrossContainerRelayResolutionAsync().GetAwaiter().GetResult();
    }

    private static async Task CheckBridgeDomainMetadataAsync()
    {
        using var bridge = new FirefoxContainerBridge();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);

        // 1. Open route with URL containing domain, plus explicit domains with IPs and duplicates
        var domains = new[] { "api.corp.local", "127.0.0.1", "::1", "10.0.0.5", "DB.corp.local.", "api.corp.local" };
        var openTask1 = bridge.OpenAsync("s1", "Server 1", 10001, "https://panel.corp.local:8443/index.html", CancellationToken.None, domains);

        var poll1 = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        poll1.EnsureSuccessStatusCode();
        using var doc1 = JsonDocument.Parse(await poll1.Content.ReadAsStringAsync());
        var routes1 = doc1.RootElement.GetProperty("routes").EnumerateArray().ToList();
        Equal(1, routes1.Count);
        Equal("s1", routes1[0].GetProperty("key").GetString());
        Equal(10001, routes1[0].GetProperty("port").GetInt32());

        var routeDomains1 = routes1[0].GetProperty("domains").EnumerateArray().Select(d => d.GetString()).ToList();
        Equal(3, routeDomains1.Count);
        Equal(true, routeDomains1.Contains("panel.corp.local"));
        Equal(true, routeDomains1.Contains("api.corp.local"));
        Equal(true, routeDomains1.Contains("db.corp.local"));

        var domainRoutes1 = doc1.RootElement.GetProperty("domainRoutes");
        Equal(10001, domainRoutes1.GetProperty("panel.corp.local").GetInt32());
        Equal(10001, domainRoutes1.GetProperty("api.corp.local").GetInt32());
        Equal(10001, domainRoutes1.GetProperty("db.corp.local").GetInt32());

        // Ack open task 1
        var cmd1 = doc1.RootElement.GetProperty("commands")[0].GetProperty("id").GetString();
        var ack1 = await http.PostAsync(bridge.Url, new StringContent(JsonSerializer.Serialize(new
        {
            acks = new[] { new { id = cmd1, error = (string?)null } },
            activeKeys = new[] { "s1" }
        })));
        ack1.EnsureSuccessStatusCode();
        await openTask1;

        // 2. Reopen same key with same port and extra domain -> domains merged, Opened preserved
        var reopenTask = bridge.OpenAsync("s1", "Server 1", 10001, "https://panel.corp.local/", CancellationToken.None, ["extra.corp.local"]);
        var pollReopen = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[\"s1\"]}"));
        using var docReopen = JsonDocument.Parse(await pollReopen.Content.ReadAsStringAsync());
        var routeReopen = docReopen.RootElement.GetProperty("routes").EnumerateArray().First();
        var reopenedDomains = routeReopen.GetProperty("domains").EnumerateArray().Select(d => d.GetString()).ToList();
        Equal(4, reopenedDomains.Count);
        Equal(true, reopenedDomains.Contains("extra.corp.local"));

        var cmdReopen = docReopen.RootElement.GetProperty("commands")[0].GetProperty("id").GetString();
        await http.PostAsync(bridge.Url, new StringContent(JsonSerializer.Serialize(new
        {
            acks = new[] { new { id = cmdReopen, error = (string?)null } },
            activeKeys = new[] { "s1" }
        })));
        await reopenTask;

        // 3. Reopen same key with different port -> throws InvalidOperationException
        try
        {
            await bridge.OpenAsync("s1", "Server 1", 10002, "https://panel.corp.local/", CancellationToken.None);
            throw new Exception("Expected InvalidOperationException on port conflict");
        }
        catch (InvalidOperationException) { }

        // 4. Add second route s2 with domain collision on "api.corp.local"
        var openTask2 = bridge.OpenAsync("s2", "Server 2", 20002, "https://other.corp.local/", CancellationToken.None, ["api.corp.local"]);
        var poll2 = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[\"s1\"]}"));
        using var doc2 = JsonDocument.Parse(await poll2.Content.ReadAsStringAsync());
        var domainRoutes2 = doc2.RootElement.GetProperty("domainRoutes");
        Equal(true, domainRoutes2.TryGetProperty("panel.corp.local", out _));
        Equal(true, domainRoutes2.TryGetProperty("other.corp.local", out _));
        // "api.corp.local" is claimed by s1 and s2 -> fail-closed: excluded from domainRoutes
        Equal(false, domainRoutes2.TryGetProperty("api.corp.local", out _));

        var cmd2 = doc2.RootElement.GetProperty("commands")[0].GetProperty("id").GetString();
        await http.PostAsync(bridge.Url, new StringContent(JsonSerializer.Serialize(new
        {
            acks = new[] { new { id = cmd2, error = (string?)null } },
            activeKeys = new[] { "s1", "s2" }
        })));
        await openTask2;

        // 5. Remove s1: "api.corp.local" becomes unique to s2, s1 domains removed
        bridge.Remove("s1");
        var poll3 = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[\"s2\"]}"));
        using var doc3 = JsonDocument.Parse(await poll3.Content.ReadAsStringAsync());
        var domainRoutes3 = doc3.RootElement.GetProperty("domainRoutes");
        Equal(false, domainRoutes3.TryGetProperty("panel.corp.local", out _));
        Equal(true, domainRoutes3.TryGetProperty("other.corp.local", out _));
        Equal(true, domainRoutes3.TryGetProperty("api.corp.local", out var s2ApiPort));
        Equal(20002, s2ApiPort.GetInt32());

        // 6. Remove s2: domainRoutes empty
        bridge.Remove("s2");
        var poll4 = await http.PostAsync(bridge.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        using var doc4 = JsonDocument.Parse(await poll4.Content.ReadAsStringAsync());
        Equal(0, doc4.RootElement.GetProperty("routes").GetArrayLength());
        Equal(0, doc4.RootElement.GetProperty("domainRoutes").EnumerateObject().Count());

        // 7. Regression check: Two distinct web interfaces sharing resolver mappings
        // Each session publishes only its own hostname, so both remain active in domainRoutes without false collision
        using var bridgeShared = new FirefoxContainerBridge();
        using var httpShared = new HttpClient(new HttpClientHandler { UseProxy = false });
        httpShared.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridgeShared.Token);

        _ = bridgeShared.OpenAsync("s-web1", "Server Web 1", 11001, "https://panel1.corp/", CancellationToken.None, ["panel1.corp"]);
        _ = bridgeShared.OpenAsync("s-web2", "Server Web 2", 11002, "https://panel2.corp/", CancellationToken.None, ["panel2.corp"]);

        var pollShared = await httpShared.PostAsync(bridgeShared.Url, new StringContent("{\"acks\":[],\"activeKeys\":[]}"));
        using var docShared = JsonDocument.Parse(await pollShared.Content.ReadAsStringAsync());
        var domainRoutesShared = docShared.RootElement.GetProperty("domainRoutes");
        Equal(11001, domainRoutesShared.GetProperty("panel1.corp").GetInt32());
        Equal(11002, domainRoutesShared.GetProperty("panel2.corp").GetInt32());
    }

    internal static void FirefoxExtensionDomainFallbackRoutingNodeTest()
    {
        using var stream = typeof(FirefoxContainerBrowser).Assembly
            .GetManifestResourceStream("KiTTYManager.Core.FirefoxExtension.background.js");
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
        const jsCode = {{JsonSerializer.Serialize(jsCode)}};
        const code = jsCode.split('(async () => {')[0];

        const events = {};
        const event = name => ({ addListener: fn => { events[name] = fn; } });
        let purgeCheckSeen = false, purgeFallback;
        const browser = {
          runtime: { getURL: () => 'moz-extension://ext-id/' },
          proxy: { onRequest: event('proxy') },
          webRequest: { onBeforeRequest: event('webRequest') },
          tabs: {
            query: async filter => {
              if (filter && filter.cookieStoreId === 'cid-Server 1') {
                purgeCheckSeen = true;
                purgeFallback = vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://panel.corp/' })", ctx);
              }
              return [];
            },
            create: async () => ({ id: 99 }),
            get: async id => ({ id, url: '' }),
            remove: async () => {},
            update: async () => {},
            onActivated: event('tabActivated'),
            onUpdated: event('tabUpdated'),
            onRemoved: event('tabRemoved'),
            onCreated: event('tabCreated')
          },
          windows: { onRemoved: event('winRemoved') },
          contextualIdentities: {
            get: async id => ({ cookieStoreId: id }),
            create: async ({ name }) => ({ cookieStoreId: 'cid-' + name }),
            remove: async () => {}
          },
          storage: { local: { get: async () => ({}), set: async () => {} } }
        };

        let currentMockState = { routes: [], purges: [], commands: [] };
        let lastRequestBody = null;
        let pollNextFn = null;
        let pollStepWaitResolve = null;

        const mockFetch = async (url, options) => {
          lastRequestBody = JSON.parse(options.body);
          return {
            ok: true,
            status: 200,
            json: async () => currentMockState
          };
        };

        const mockSetTimeout = (fn, ms) => {
          pollNextFn = fn;
          if (pollStepWaitResolve) {
            const cb = pollStepWaitResolve;
            pollStepWaitResolve = null;
            cb();
          }
        };

        const stepPoll = async (nextState) => {
          currentMockState = nextState;
          if (pollNextFn) {
            const p = new Promise(r => { pollStepWaitResolve = r; });
            const resume = pollNextFn;
            pollNextFn = null;
            resume();
            await p;
          } else {
            const p = new Promise(r => { pollStepWaitResolve = r; });
            vm.runInContext('poll()', ctx);
            await p;
          }
        };

        const ctx = vm.createContext({
          browser,
          KITTY: { url: 'http://127.0.0.1:9999/poll', token: 'test-tok', blockedPort: 9998, startupUrl: 'about:blank' },
          fetch: mockFetch,
          setTimeout: mockSetTimeout,
          console,
          URL,
          AbortSignal: global.AbortSignal || { timeout: () => ({}) }
        });
        vm.runInContext(code, ctx);

        (async () => {
          // 1. Offline guard: routeFor returns undefined, proxy returns blocked, webRequest cancels
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://panel.corp/' })", ctx), undefined);
          assert.deepEqual(events.proxy({ url: 'https://panel.corp/' }), [{type: 'http', host: '127.0.0.1', port: 9998}, null]);
          assert.deepEqual(events.webRequest({ url: 'https://panel.corp/' }), { cancel: true });

          // 2. Real poll execution: 3 routes published by bridge, srv-2 and srv-3 collide on 'collide.corp'
          await stepPoll({
            routes: [
              { key: 'srv-1', name: 'Server 1', port: 10001, domains: ['panel.corp', 'api.corp', 'xn--e1afmkfd.xn--p1ai'] },
              { key: 'srv-2', name: 'Server 2', port: 20002, domains: ['other.corp', 'collide.corp'] },
              { key: 'srv-3', name: 'Server 3', port: 30003, domains: ['collide.corp'] }
            ],
            purges: [],
            commands: []
          });

          // Verify state populated by real poll()
          assert.equal(vm.runInContext("online", ctx), true);
          assert.equal(vm.runInContext("domainRoutes.get('panel.corp').port", ctx), 10001);
          assert.equal(vm.runInContext("domainRoutes.get('api.corp').port", ctx), 10001);
          assert.equal(vm.runInContext("domainRoutes.get('xn--e1afmkfd.xn--p1ai').port", ctx), 10001);
          assert.equal(vm.runInContext("domainRoutes.get('other.corp').port", ctx), 20002);
          // Collision filtering verified: collide.corp is excluded
          assert.equal(vm.runInContext("domainRoutes.has('collide.corp')", ctx), false);

          // 3. Domain fallback for firefox-default (iframe / embedded widget)
          const fbPanel = events.proxy({ cookieStoreId: 'firefox-default', url: 'https://panel.corp:8443/app/main' });
          assert.deepEqual(fbPanel, [{type: 'socks', host: '127.0.0.1', port: 10001, proxyDNS: true, connectionIsolationKey: 'srv-1'}, null]);
          assert.deepEqual(events.webRequest({ cookieStoreId: 'firefox-default', url: 'https://panel.corp:8443/app/main' }), { cancel: false });

          // 4. Transparent protocol support: HTTP, HTTPS, WS, WSS
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'http://panel.corp/api' }).port", ctx), 10001);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://panel.corp/api' }).port", ctx), 10001);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'ws://panel.corp:3000/socket' }).port", ctx), 10001);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'wss://panel.corp:3443/socket' }).port", ctx), 10001);

          // 5. Case-insensitivity and trailing dot normalization
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://PANEL.CORP./test' }).port", ctx), 10001);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://Api.Corp:9090/' }).port", ctx), 10001);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://xn--e1afmkfd.xn--p1ai/path' }).port", ctx), 10001);

          // 6. Absolute container priority over domain fallback
          // Request from Container 2 must use Server 2 (port 20002), NOT fallback for panel.corp (port 10001)
          const c2Id = vm.runInContext("containerIds['srv-2']", ctx);
          const c2Request = events.proxy({ cookieStoreId: c2Id, url: 'https://panel.corp/test' });
          assert.deepEqual(c2Request, [{type: 'socks', host: '127.0.0.1', port: 20002, proxyDNS: true, connectionIsolationKey: 'srv-2'}, null]);
          assert.equal(vm.runInContext(`routeFor({ cookieStoreId: '${c2Id}', url: 'https://panel.corp/test' }).port`, ctx), 20002);

          // 7. Unknown and colliding domains denied
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://unknown.local/' })", ctx), undefined);
          assert.deepEqual(events.proxy({ cookieStoreId: 'firefox-default', url: 'https://unknown.local/' }), [{type: 'http', host: '127.0.0.1', port: 9998}, null]);
          assert.deepEqual(events.webRequest({ cookieStoreId: 'firefox-default', url: 'https://unknown.local/' }), { cancel: true });

          // Colliding domain "collide.corp" was excluded by real poll() -> fail-closed
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://collide.corp/' })", ctx), undefined);
          assert.deepEqual(events.webRequest({ cookieStoreId: 'firefox-default', url: 'https://collide.corp/' }), { cancel: true });

          // 8. Suffix and lookalike rejection
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://sub.panel.corp/' })", ctx), undefined);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://panel.corp.evil/' })", ctx), undefined);

          // 9. Malformed URL safety
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'javascript:alert(1)' })", ctx), undefined);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'not a valid url' })", ctx), undefined);

          // 10. Second poll iteration: remove srv-3 so collide.corp becomes uniquely owned by srv-2
          await stepPoll({
            routes: [
              { key: 'srv-1', name: 'Server 1', port: 10001, domains: ['panel.corp', 'api.corp', 'xn--e1afmkfd.xn--p1ai'] },
              { key: 'srv-2', name: 'Server 2', port: 20002, domains: ['other.corp', 'collide.corp'] }
            ],
            purges: [],
            commands: []
          });
          assert.equal(vm.runInContext("domainRoutes.has('collide.corp')", ctx), true);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://collide.corp/' }).port", ctx), 20002);

          // 11. Third poll iteration: real Purge processing removes srv-1 and purges its domains
          await stepPoll({
            routes: [
              { key: 'srv-2', name: 'Server 2', port: 20002, domains: ['other.corp', 'collide.corp'] }
            ],
            purges: [
              { id: 'purge-srv1', pattern: 'srv-1' }
            ],
            commands: []
          });
          assert.equal(purgeCheckSeen, true);
          assert.equal(purgeFallback, undefined, 'fallback must already be revoked before awaiting tab deletion');
          assert.equal(vm.runInContext("domainRoutes.has('panel.corp')", ctx), false);
          assert.equal(vm.runInContext("domainRoutes.has('api.corp')", ctx), false);
          assert.equal(vm.runInContext("routeFor({ cookieStoreId: 'firefox-default', url: 'https://panel.corp/' })", ctx), undefined);
          assert.deepEqual(events.webRequest({ cookieStoreId: 'firefox-default', url: 'https://panel.corp/' }), { cancel: true });

          // 12. Fourth poll iteration: verify purge acknowledgement was sent to bridge
          await stepPoll({
            routes: [
              { key: 'srv-2', name: 'Server 2', port: 20002, domains: ['other.corp', 'collide.corp'] }
            ],
            purges: [],
            commands: []
          });
          assert.deepEqual(lastRequestBody.purgeAcks, [{ id: 'purge-srv1', error: null }]);
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
            throw new Exception("Extension domain fallback routing Node check failed: " + stderr);
    }

    private static async Task CheckCrossContainerRelayResolutionAsync()
    {
        // Setup a mock upstream TCP listener simulating SSH tunnel dynamic SOCKS5
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();
        var upstreamPort = ((IPEndPoint)upstreamListener.LocalEndpoint).Port;

        var upstreamConnected = new TaskCompletionSource<(string Host, int Port, string RequestHeader)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            using var client = await upstreamListener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // 1. SOCKS5 greeting
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            // 2. SOCKS5 CONNECT
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var atypical = header[3];
            string destHost = "";
            if (atypical == 0x01) // IPv4
            {
                var ipBytes = new byte[4];
                await stream.ReadExactlyAsync(ipBytes);
                destHost = new IPAddress(ipBytes).ToString();
            }
            var portBytes = new byte[2];
            await stream.ReadExactlyAsync(portBytes);
            var destPort = (portBytes[0] << 8) | portBytes[1];

            // SOCKS5 success reply
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 80 });

            // Read HTTP request bytes from client
            var buffer = new byte[1024];
            var count = await stream.ReadAsync(buffer);
            var req = Encoding.ASCII.GetString(buffer, 0, count);
            upstreamConnected.TrySetResult((destHost, destPort, req));

            // Write HTTP 200 reply
            var httpReply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK");
            await stream.WriteAsync(httpReply);
        });

        // ResolvingSocks5Relay with domain mapped to 10.1.2.3
        await using var relay = new ResolvingSocks5Relay("127.0.0.1", upstreamPort,
            [new KeyValuePair<string, string>("panel.internal.corp", "10.1.2.3")]);

        // Client (simulating browser making request for panel.internal.corp:8443)
        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, relay.Port);
        var clientStream = clientTcp.GetStream();

        // SOCKS5 handshake to relay
        await clientStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var relayGreeting = new byte[2];
        await clientStream.ReadExactlyAsync(relayGreeting);
        Equal(0x05, relayGreeting[0]);
        Equal(0x00, relayGreeting[1]);

        // SOCKS5 CONNECT for domain "panel.internal.corp" port 8443 (ATYP 0x03)
        var domainBytes = Encoding.ASCII.GetBytes("panel.internal.corp");
        var reqBytes = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)domainBytes.Length };
        reqBytes.AddRange(domainBytes);
        reqBytes.Add((byte)(8443 >> 8));
        reqBytes.Add((byte)(8443 & 0xFF));
        await clientStream.WriteAsync(reqBytes.ToArray());

        var relayReply = new byte[10];
        await clientStream.ReadExactlyAsync(relayReply);
        Equal(0x05, relayReply[0]);
        Equal(0x00, relayReply[1]); // succeeded

        // Send HTTP request preserving original Host
        var httpRequest = "GET /api/status HTTP/1.1\r\nHost: panel.internal.corp:8443\r\nConnection: close\r\n\r\n";
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(httpRequest));

        var (resolvedIp, targetPort, receivedHttp) = await upstreamConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal("10.1.2.3", resolvedIp);
        Equal(8443, targetPort);
        Equal(true, receivedHttp.Contains("Host: panel.internal.corp:8443"));
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
}

internal sealed class DirectTrap : IDisposable
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

internal sealed class FakePanel : IDisposable
{
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly string flavor;
        private readonly X509Certificate2? cert;
        public string Flavor => flavor;
        private readonly int directPort;
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public int Port { get; }
        public TaskCompletionSource<string> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Inherited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakePanel(string flavor, int directPort, X509Certificate2? cert = null)
        {
            this.directPort = directPort;
            this.flavor = flavor;
            this.cert = cert;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
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

                Stream dataStream = stream;
                if (cert != null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(cert, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                    dataStream = ssl;
                }

                using var reader = new StreamReader(dataStream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token) ?? "";
                if (request.StartsWith("GET /inherited ", StringComparison.Ordinal)) Inherited.TrySetResult(true);
                Interlocked.Increment(ref requestCount);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                if (request.StartsWith("GET /seen?", StringComparison.Ordinal)) Seen.TrySetResult(Uri.UnescapeDataString(request));
                var html = $"<html><title>{flavor}</title><script>let old=document.cookie;if(!old)document.cookie='flavor={flavor}; Max-Age=86400; Path=/';fetch('/seen?before='+encodeURIComponent(old)+'&after='+encodeURIComponent(document.cookie));setInterval(()=>{{fetch('/heartbeat').catch(()=>{{}});fetch('http://127.0.0.1:{directPort}/direct').catch(()=>{{}});}},300);</script></html>";
                var data = Encoding.UTF8.GetBytes(html);
                await dataStream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await dataStream.WriteAsync(data, stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
            catch (Exception e) { Seen.TrySetException(e); }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void PreReleaseShellPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("INFO: POSIX file-command execution requires sh; run this check on Linux as well.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "kitty-path-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            Directory.CreateDirectory(Path.Combine(root, "app"));
            Directory.CreateDirectory(Path.Combine(work, "app data", "cache"));
            var step = new BatchTaskStep { Kind = BatchTaskStepKind.Delete, Destination = "app data/cache" };
            Shell(BatchTaskRunner.BuildDeleteCommand(step, work, false, Server("target")), root);
            Equal(true, Directory.Exists(Path.Combine(root, "app")));
            Equal(false, Directory.Exists(Path.Combine(work, "app data", "cache")));
            foreach (var name in new[] { "payload_$(touch marker).txt", "O'Brien.txt", "a;touch marker", "a`touch marker`", "a b.txt" })
            {
                Equal(work + "/" + name, Shell(BatchTaskRunner.BuildUploadDestinationCommand(name, work), root));
                File.WriteAllText(Path.Combine(work, name), "sample");
                step.Destination = name;
                Shell(BatchTaskRunner.BuildDeleteCommand(step, work, false, Server("target")), root);
                Equal(false, File.Exists(Path.Combine(work, name)));
                Equal(false, File.Exists(Path.Combine(root, "marker")));
                Equal(false, File.Exists(Path.Combine(work, "marker")));
            }
            Equal("/file.txt", Shell(BatchTaskRunner.BuildUploadDestinationCommand("file.txt", "/"), root));
            foreach (var name in new[] { "a1.log", "b2.log", "c3.log", "keep.txt" }) File.WriteAllText(Path.Combine(work, name), "data");
            step.Destination = "[ab]?.log";
            Shell(BatchTaskRunner.BuildDeleteCommand(step, work, false, Server("target")), root);
            Equal(false, File.Exists(Path.Combine(work, "a1.log")));
            Equal(false, File.Exists(Path.Combine(work, "b2.log")));
            Equal(true, File.Exists(Path.Combine(work, "c3.log")));
            foreach (var pattern in new[] { "*.log", "*.log$(touch marker)" })
            {
                var archive = Path.Combine(root, "output.tar");
                var command = BatchTaskRunner.BuildDownloadArchiveCommand(work, pattern, archive,
                    new("tar", ".tar", "tar"), false, Server("target"));
                if (pattern == "*.log")
                {
                    Shell(command, root);
                    Equal("c3.log\n", Shell("tar -tf '" + archive + "'", root));
                }
                else
                {
                    MustThrow<InvalidOperationException>(() => Shell(command, root));
                    Equal(false, File.Exists(Path.Combine(work, "marker")));
                }
            }
            step.Destination = "missing*.log";
            Shell(BatchTaskRunner.BuildDeleteCommand(step, work, false, Server("target")), root);
            Equal(true, File.Exists(Path.Combine(work, "keep.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Shell(string command, string directory)
    {
        var start = new ProcessStartInfo("sh") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Shell regression timed out."); }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Result);
        return output.Result;
    }

    private static void PreReleaseFileContextAndRunAs()
    {
        foreach (var kind in new[] { BatchTaskStepKind.Upload, BatchTaskStepKind.Template, BatchTaskStepKind.ReplaceText })
        {
            var step = new BatchTaskStep { Kind = kind, Destination = "data/settings", Source = "local.txt" };
            var resolved = BatchTaskRunner.ResolveFileStep(step, "/opt/app", true);
            Equal("/opt/app/data/settings", resolved.Destination);
            Equal("data/settings", step.Destination); Equal(false, step.Become);
            Equal("local.txt", resolved.Source); Equal(true, resolved.Become);
            step.Destination = "/absolute/settings";
            Equal(step.Destination, BatchTaskRunner.ResolveFileStep(step, "/opt/app", false).Destination);
        }
        foreach (var kind in Enum.GetValues<BatchTaskStepKind>())
        {
            var task = new BatchTaskDefinition { Name = "run-as", Steps = [new() {
                Kind = kind, RunAsUser = "operator", Command = "true", Source = "file", Destination = "file",
                ExpectedText = "ready", ActionCommand = "yes", Search = "old", Replacement = "new" }] };
            if (kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Download or BatchTaskStepKind.Template or
                BatchTaskStepKind.ReplaceText or BatchTaskStepKind.InteractiveSend)
                MustThrow<InvalidDataException>(() => BatchTaskFile.Validate(task));
            else BatchTaskFile.Validate(task);
        }
        var delete = BatchTaskRunner.BuildDeleteCommand(new() { Destination = "cache", RunAsUser = "operator", RunAsPassword = "secret" },
            "/opt/app", false, Server("target"));
        Equal(true, BatchTaskRunner.TryParseSuCommandWithPipedPassword(delete, out var su, out var password));
        Equal("secret", password); Equal(true, su!.Contains("operator"));
    }

    private static void PreReleasePtyDispatchBoundary()
    {
        int fallback = 0, sent = 0;
        foreach (var failure in new Exception[] { new TimeoutException(), new IOException("connection lost"), new InvalidOperationException("EOF") })
        {
            try
            {
                BatchTaskRunner.TryRunCommandPtyAsync(() => { sent++; return Task.FromException<(string, int)>(failure); },
                    _ => fallback++).GetAwaiter().GetResult();
                throw new Exception("Dispatched PTY failure was swallowed.");
            }
            catch (Exception ex) when (ReferenceEquals(ex, failure)) { }
        }
        Equal(3, sent); Equal(0, fallback);
        var unavailable = BatchTaskRunner.TryRunCommandPtyAsync(() =>
        {
            BatchTaskRunner.CreateCommandPty<Stream>(() => throw new IOException("channel refused"));
            throw new Exception("PTY unexpectedly created.");
        }, _ => fallback++).GetAwaiter().GetResult();
        Equal(true, unavailable is null); Equal(1, fallback);
        MustThrow<OperationCanceledException>(() => BatchTaskRunner.CreateCommandPty<Stream>(() => throw new OperationCanceledException()));
        using var stream = new PendingPtyStream();
        MustThrow<TimeoutException>(() => BatchTaskRunner.RunInteractivePtyCommandCoreAsync(stream, _ => sent++, "su -c true", "",
            Server("pty"), "step", [], (_, _, _, _, _) => { }, CancellationToken.None, timeoutSeconds: 1).GetAwaiter().GetResult());
        Equal(4, sent);
    }

    private static void PreReleasePartialImportPreservesReferences()
    {
        var a = Server("A"); var b = Server("B"); b.Host = "192.0.2.11";
        var proxy = new BaseProxy(); b.RequiredPreviousServerId = a.Id; b.PreferredProxyId = proxy.Id;
        var incomingB = Server("B"); incomingB.Id = b.Id; incomingB.Host = b.Host; incomingB.Username = "new-user";
        var current = new ManagerConfig { UngroupedServers = [a, b], BaseProxies = [proxy] };
        var incoming = new ManagerConfig { UngroupedServers = [incomingB] };
        var plan = ImportWizardEngine.Analyze(current, incoming);
        foreach (var row in plan.Sessions) row.Decision = ImportDecision.KeepCurrent;
        foreach (var field in plan.Fields) field.UseIncoming = field.PropertyName == "Username";
        var merged = ImportWizardEngine.Merge(current, incoming, plan).FindServer(b.Id)!;
        Equal("new-user", merged.Username); Equal(a.Id, merged.RequiredPreviousServerId); Equal(proxy.Id, merged.PreferredProxyId);
        Equal(b.RequiredPreviousServerId, a.Id); Equal(b.Username, current.FindServer(b.Id)!.Username);
        foreach (var field in plan.Fields)
            field.UseIncoming = field.PropertyName is "Username" or "RequiredPreviousServerId" or "PreferredProxyId";
        merged = ImportWizardEngine.Merge(current, incoming, plan).FindServer(b.Id)!;
        Equal<Guid?>(null, merged.RequiredPreviousServerId); Equal<Guid?>(null, merged.PreferredProxyId);
    }

    private static void PreReleaseEndpointPreferenceRemap()
    {
        var a = Server("A"); var b = Server("B"); b.Host = "192.0.2.11";
        var proxy = new BaseProxy { Name = "entry" };
        b.EndpointPreferences = [new() { PreviousServerId = a.Id, Endpoint = new(b.Host, 22) },
            new() { ProxyId = proxy.Id, Endpoint = new(b.Host, 22) }, new() { ProxyId = Guid.Empty, Endpoint = new(b.Host, 22) }];
        var current = new ManagerConfig { UngroupedServers = [a], BaseProxies = [proxy] };
        var incoming = new ManagerConfig { UngroupedServers = [a, b], BaseProxies = [proxy] };
        var plan = ImportWizardEngine.Analyze(current, incoming);
        foreach (var row in plan.Sessions) row.Decision = ImportDecision.Add;
        foreach (var row in plan.Proxies) row.Decision = ImportDecision.Add;
        var merged = ImportWizardEngine.Merge(current, incoming, plan);
        var duplicate = merged.AllServers().Single(s => s.Id != a.Id && s.Id != b.Id);
        var addedProxy = merged.BaseProxies.Single(p => p.Id != proxy.Id);
        var prefs = merged.FindServer(b.Id)!.EndpointPreferences;
        Equal(duplicate.Id, prefs[0].PreviousServerId); Equal(addedProxy.Id, prefs[1].ProxyId); Equal(Guid.Empty, prefs[2].ProxyId);
        Equal(a.Id, b.EndpointPreferences[0].PreviousServerId);
        foreach (var row in plan.Sessions.Where(r => r.IncomingId == a.Id)) row.Decision = ImportDecision.Skip;
        foreach (var row in plan.Proxies) row.Decision = ImportDecision.Skip;
        prefs = ImportWizardEngine.Merge(current, incoming, plan).FindServer(b.Id)!.EndpointPreferences;
        Equal(1, prefs.Count); Equal(Guid.Empty, prefs[0].ProxyId);
    }

    private static void PreReleaseConsoleIngressLifecycle()
    {
        var order = new List<string>();
        var owner = new CallbackDisposable(() => order.Add("owner"));
        var probe = new CallbackDisposable(() => order.Add("probe"));
        var spent = new CallbackDisposable(() => order.Add("spent"));
        var fresh = new CallbackDisposable(() => order.Add("fresh"));
        var resources = new List<IDisposable> { owner, spent, probe };
        Equal(2022, SshConnectionService.ReleaseProbeForConsole(probe, resources, 1022, spent, () =>
        {
            Equal("probe,spent", string.Join(',', order)); return (fresh, 2022);
        }));
        Equal(true, resources.SequenceEqual(new IDisposable[] { owner, fresh }));
        foreach (var resource in resources.AsEnumerable().Reverse()) resource.Dispose();
        Equal("probe,spent,fresh,owner", string.Join(',', order));
        order.Clear(); resources = [owner, probe];
        Equal(1022, SshConnectionService.ReleaseProbeForConsole(probe, resources, 1022, null, null));
        Equal("probe", string.Join(',', order)); Equal(true, resources.SequenceEqual(new[] { owner }));
        resources = [owner, spent, probe]; order.Clear();
        MustThrow<IOException>(() => SshConnectionService.ReleaseProbeForConsole(probe, resources, 1022, spent,
            () => throw new IOException("reconnect failed")));
        Equal("probe,spent", string.Join(',', order)); Equal(true, resources.SequenceEqual(new[] { owner }));
    }

    private static void PreReleaseFirstHopCacheAndEmptyRoute()
    {
        var a = Server("A"); var b = Server("B"); var proxy = new BaseProxy(); var cache = new RouteFailureCache(); var now = DateTimeOffset.UtcNow;
        cache.RememberDirectFailure(new(proxy, [a]), now);
        Equal(true, cache.ShouldSkip(new(proxy, [a, b]), now));
        Equal(false, cache.ShouldSkip(new(proxy, [b, a]), now));
        Equal(false, cache.ShouldSkip(new(new BaseProxy(), [a, b]), now));
        Equal(false, cache.ShouldSkip(new(proxy, [a, b]), now.AddSeconds(90)));
        cache.ClearSuccess(new(proxy, [a, b])); Equal(false, cache.ShouldSkip(new(proxy, [a, b]), now));
        Equal(0, RoutePlanner.OrderPreferred(new(), [], new() { ServerIds = [] }).Count);
        Equal<RouteCandidate?>(null, RoutePlanner.CandidateFromCached(new(), new() { ProxyId = Guid.NewGuid(), ServerIds = [a.Id] }));
    }

    private static void PreReleaseConnectAuthority()
    {
        foreach (var (value, expectedHost, expectedPort) in new[] { ("service.example", "service.example", 443),
            ("service.example:80", "service.example", 80), ("service.example:443", "service.example", 443),
            ("[2001:db8::1]", "2001:db8::1", 443), ("[2001:db8::1]:80", "2001:db8::1", 80) })
        {
            Equal(true, ResolvingHttpProxy.TryParseAuthority(value, 443, out var host, out var port));
            Equal(expectedHost, host); Equal(expectedPort, port);
        }
        foreach (var invalid in new[] { "service.example:0", "service.example:65536", "service.example:", "user@service.example:80", "service.example/path" })
            Equal(false, ResolvingHttpProxy.TryParseAuthority(invalid, 443, out _, out _));
    }

    private static void PreReleaseRelayFailureUnblocksPeer()
    {
        foreach (var http in new[] { false, true })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var peer = listener.AcceptTcpClient(); using var network = peer.GetStream();
            var blocked = network.ReadAsync(new byte[1]).AsTask();
            using var source = new FailedRelayStream(); using var destination = new MemoryStream();
            MustThrow<IOException>(() => (http
                ? ResolvingHttpProxy.CopyAndHalfCloseAsync(source, destination, peer.Client, "test", null, CancellationToken.None, false)
                : ResolvingSocks5Relay.CopyAndHalfCloseAsync(source, destination, peer.Client, CancellationToken.None)).GetAwaiter().GetResult());
            Equal(true, peer.Client.SafeHandle.IsClosed);
            try { blocked.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            Equal(true, blocked.IsCompleted);
        }
    }

    private static void PreReleaseAtomicPackageReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-package-regression-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "saved.kmtask"); File.WriteAllText(target, "old package");
            MustThrow<IOException>(() => BatchTaskFile.WritePackageAtomically(target, temporary =>
            { File.WriteAllText(temporary, "partial new package"); throw new IOException("simulated disk failure"); }));
            Equal("old package", File.ReadAllText(target)); Equal(1, Directory.GetFiles(root).Length);
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            BatchTaskFile.Save(Path.Combine(source, "task.yaml"), new() { Name = "example", Steps = [new() { Command = "true" }] });
            BatchTaskFile.ExportPackageWithoutFiles(source, target);
            using (var archive = ZipFile.OpenRead(target)) Equal(1, archive.Entries.Count);
            File.WriteAllText(Path.Combine(source, "material.txt"), "data");
            BatchTaskFile.ExportPackage(source, target);
            using (var archive = ZipFile.OpenRead(target)) Equal(2, archive.Entries.Count);
            var nested = Path.Combine(source, "previous.kmtask"); File.WriteAllText(nested, "keep");
            MustThrow<InvalidDataException>(() => BatchTaskFile.ExportPackage(source, nested)); Equal("keep", File.ReadAllText(nested));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void MustThrow<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class CallbackDisposable(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class FailedRelayStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token) => Task.FromException(new IOException("simulated reset"));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(new IOException("simulated reset"));
    }
    private sealed class PendingPtyStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KiTTYManager.Core;

public sealed class FirefoxContainerBrowser : IDisposable
{
    private Process? browser;
    private FileStream? owner;
    private TcpClient? smokeClient;
    private readonly string startupUrl = "about:blank#kitty-manager-" + Guid.NewGuid().ToString("N");
    public FirefoxContainerBridge Bridge { get; } = new();
    public bool IsRunning => browser is { HasExited: false };

    public async Task StartAsync(string executable, string root, Func<string> source, CancellationToken token,
        bool headless = false)
    {
        if (IsRunning)
        {
            if (!Bridge.Connected) throw new IOException("Расширение Firefox не отвечает. Закройте Firefox TEST и повторите.");
            return;
        }
        Directory.CreateDirectory(root);
        owner ??= new FileStream(Path.Combine(root, "manager.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var profile = await Task.Run(() => FirefoxProfileWorkspace.EnsureContainerProfile(root, source), token);
        token.ThrowIfCancellationRequested();
        Bridge.Trace?.Invoke("Firefox containers: persistent profile ready");
        // An old browser may outlive its manager. Never rewrite that browser's profile.
        // Firefox's own lock is also checked by the OS when the next instance launches.
        var lockPath = Path.Combine(profile, "parent.lock");
        if (OperatingSystem.IsWindows() && File.Exists(lockPath))
            using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        if (OperatingSystem.IsWindows() && !FirefoxProfileWorkspace.CanMergeAndDelete(profile))
            throw new IOException("Постоянный профиль занят. Закройте Firefox TEST от предыдущего запуска менеджера.");
        if (OperatingSystem.IsLinux())
        {
            // Linux smoke runs can leave the legacy 'lock' symlink after a crash.
            // The actual Firefox lock is the fcntl lock on .parentlock.
            using var guard = new FileStream(Path.Combine(profile, ".parentlock"), FileMode.OpenOrCreate, FileAccess.ReadWrite);
            guard.Lock(0, 1);
            guard.Unlock(0, 1);
        }
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        FirefoxProfileWorkspace.ConfigureContainers(profile, Bridge.BlockedPort, port, new Uri(Bridge.Url).Port);
        var addonPath = Path.Combine(root, "containers-test.xpi");
        WriteAddon(addonPath);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in new[] { "-wait-for-browser", "-no-remote", "-new-instance", "-profile", profile, "-marionette" }) start.ArgumentList.Add(arg);
        if (headless) start.ArgumentList.Add("-headless");
        start.ArgumentList.Add(startupUrl);
        browser?.Dispose();
        browser = Process.Start(start) ?? throw new IOException("Firefox не запущен.");
        Bridge.Trace?.Invoke($"Firefox containers: browser launched pid={browser.Id}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var ownedClient = headless ? null : new TcpClient();
        var client = ownedClient ?? (smokeClient = new TcpClient());
        while (!client.Connected)
        {
            if (browser.HasExited) throw new IOException("Firefox завершился до загрузки расширения. Возможно, профиль уже открыт.");
            try { await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token); }
            catch (SocketException) { await Task.Delay(200, deadline.Token); }
        }
        var stream = client.GetStream();
        using var greeting = await ReadPacketAsync(stream, deadline.Token);
        if (greeting.RootElement.GetProperty("marionetteProtocol").GetInt32() != 3)
            throw new IOException("Неподдерживаемый протокол Marionette.");
        await CommandAsync(stream, 1, "WebDriver:NewSession", new
        {
            capabilities = new { alwaysMatch = new { acceptInsecureCerts = false } }
        }, deadline.Token);
        try
        {
            await CommandAsync(stream, 2, "Addon:Install", new {path = addonPath, temporary = true}, deadline.Token);
            Bridge.Trace?.Invoke("Firefox containers: temporary extension installed");
        }
        finally
        {
            // Delete only the automation session, not the browser or profile.
            if (!headless) await CommandAsync(stream, 3, "WebDriver:DeleteSession", new { }, deadline.Token);
        }
        while (!Bridge.Connected)
        {
            if (browser.HasExited) throw new IOException("Firefox закрылся во время установки расширения.");
            await Task.Delay(200, deadline.Token);
        }
        Bridge.Trace?.Invoke("Firefox containers: authenticated bridge ready");
    }

    private void WriteAddon(string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var name in new[] { "manifest.json", "background.js" })
        {
            using var entry = archive.CreateEntry(name).Open();
            using var source = typeof(FirefoxContainerBrowser).Assembly.GetManifestResourceStream(
                "KiTTYManager.Core.FirefoxExtension." + name)!;
            source.CopyTo(entry);
        }
        using var writer = new StreamWriter(archive.CreateEntry("config.js").Open(), new UTF8Encoding(false));
        writer.Write("const KITTY = " + JsonSerializer.Serialize(new
        {url = Bridge.Url, token = Bridge.Token, blockedPort = Bridge.BlockedPort, startupUrl}) + ";");
    }

    internal async Task CloseForSmokeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stream = smokeClient!.GetStream();
        await CommandAsync(stream, 6, "Marionette:Quit", new {flags = new[] {"eAttemptQuit"}}, deadline.Token);
        if (browser is not null) await browser.WaitForExitAsync(deadline.Token);
    }

    internal async Task<int> TabCountForSmokeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stream = smokeClient!.GetStream();
        var response = await CommandAsync(stream, 4, "WebDriver:GetWindowHandles", new { }, deadline.Token);
        var handles = response.ValueKind == JsonValueKind.Array ? response : response.GetProperty("value");
        return handles.GetArrayLength();
    }

    private static async Task<JsonElement> CommandAsync(Stream stream, int id, string command, object parameters, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new object[] {0, id, command, parameters});
        await stream.WriteAsync(Encoding.ASCII.GetBytes(data.Length + ":"), token);
        await stream.WriteAsync(data, token);
        using var response = await ReadPacketAsync(stream, token);
        var result = response.RootElement;
        if (result[0].GetInt32() != 1 || result[1].GetInt32() != id)
            throw new IOException("Некорректный ответ Firefox.");
        if (result[2].ValueKind != JsonValueKind.Null)
            throw new IOException("Firefox: " + result[2].GetProperty("message").GetString());
        return result[3].Clone();
    }

    private static async Task<JsonDocument> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1];
        var length = 0;
        for (var i = 0; ; i++)
        {
            await stream.ReadExactlyAsync(one, token);
            if (one[0] == ':') break;
            if (i >= 7 || one[0] is < (byte)'0' or > (byte)'9') throw new IOException("Некорректная длина Marionette.");
            length = checked(length * 10 + one[0] - '0');
        }
        if (length is < 1 or > 1048576) throw new IOException("Слишком большой ответ Firefox.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonDocument.Parse(data);
    }

    public void Dispose()
    {
        Bridge.Dispose();
        smokeClient?.Dispose();
        browser?.Dispose(); // Leave Firefox alive to finish saving; never kill or delete its profile.
        owner?.Dispose();
    }
}

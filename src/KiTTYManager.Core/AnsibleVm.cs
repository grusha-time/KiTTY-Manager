using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

namespace KiTTYManager.Core;

public sealed record AnsibleGuestHostSecret(string InventoryName, string Password, string RootPassword,
    string PrivateKeyBase64, string PrivateKeyPassphrase);
public sealed record AnsibleLocalFile(string SourcePath, string ArchiveName);
public sealed record AnsibleRunRequest(string TaskDirectory, string PlaybookRelativePath, string Inventory,
    IReadOnlyList<AnsibleGuestHostSecret> Hosts, string VaultPassword = "",
    IReadOnlyList<AnsibleLocalFile>? LocalFiles = null, string DownloadDirectory = "", int Verbosity = 0,
    int ConnectionRecoveryMinutes = 1);
public sealed record AnsibleVmEvent(string Type, string Text = "", string Message = "", int? ExitCode = null,
    string AnsibleVersion = "", string RelativePath = "", string DataBase64 = "");
public static class AnsibleVmEventPolicy
{
    public static AnsibleVmEvent Normalize(AnsibleVmEvent item) => item with
    {
        Type = item.Type ?? "", Text = item.Text ?? "", Message = item.Message ?? "",
        AnsibleVersion = item.AnsibleVersion ?? "", RelativePath = item.RelativePath ?? "",
        DataBase64 = item.DataBase64 ?? ""
    };

    public static AnsibleVmEvent? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("type", out _))
        {
            if (document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() is
                    "upload_start" or "upload_chunk" or "upload_end" or "run" or "stop" or "shutdown")
                return null;
            throw new InvalidDataException("VM вернула JSON-событие без типа.");
        }
        return Normalize(JsonSerializer.Deserialize<AnsibleVmEvent>(line,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("VM вернула пустое JSON-событие."));
    }
}
public static class AnsibleVmEventFormatter
{
    public static string Format(AnsibleVmEvent item, Func<string, string>? displayHost = null)
    {
        displayHost ??= value => value;
        if (item.Type == "output") return item.Text;
        if (item.Type == "upload") return item.Text;
        if (item.Type == "started") return "Ansible-playbook запущен.";
        if (item.Type == "completed") return $"Ansible-playbook завершён, код {item.ExitCode ?? -1}.";
        if (item.Type != "callback") return !string.IsNullOrWhiteSpace(item.Message)
            ? item.Message : $"Событие Ansible: {item.Type}";
        try
        {
            using var document = JsonDocument.Parse(item.Text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "Событие Ansible: " + item.Text;
            var kind = Text(root, "kind");
            var name = Text(root, "name");
            var host = displayHost(Text(root, "host"));
            return kind switch
            {
                "playbook" => "Playbook: " + name,
                "play" => "Сценарий: " + name,
                "task" => "Задача: " + name + (Text(root, "action") is { Length: > 0 } action ? $" [{action}]" : ""),
                "ok" => $"{host}: " + (Bool(root, "changed") ? "изменено" : "выполнено"),
                "failed" => $"{host}: ошибка — {ResultMessage(root)}",
                "unreachable" => $"{host}: недоступен — {ResultMessage(root)}",
                "skipped" => $"{host}: пропущено",
                "stats" => FormatStats(root, displayHost),
                _ => "Событие Ansible: " + (kind.Length > 0 ? kind : item.Text)
            };
        }
        catch (JsonException) { return "Событие Ansible: " + item.Text; }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string ResultMessage(JsonElement item)
    {
        if (!item.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return "без подробностей";
        return Text(result, "msg") is { Length: > 0 } message ? message : result.GetRawText();
    }
    private static string FormatStats(JsonElement item, Func<string, string> displayHost)
    {
        if (!item.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object) return "Итог Ansible.";
        return "Итог: " + string.Join("; ", hosts.EnumerateObject().Select(host =>
            $"{displayHost(host.Name)}: ok={Number(host.Value, "ok")}, изменено={Number(host.Value, "changed")}, " +
            $"ошибок={Number(host.Value, "failures")}, недоступно={Number(host.Value, "unreachable")}, пропущено={Number(host.Value, "skipped")}"));
    }
    private static int Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
public sealed record AnsibleConnectionRecoveryPrompt(string InventoryName, int PreviousMinutes);
internal sealed record AnsibleUploadChunk(int BytesSent, string Json);

internal static class AnsibleUploadProtocol
{
    public const int ChunkSize = 2048;
    public static IEnumerable<AnsibleUploadChunk> Chunks(byte[] archive, string uploadId)
    {
        for (var offset = 0; offset < archive.Length; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, archive.Length - offset);
            yield return new(offset + count, JsonSerializer.Serialize(new { action = "upload_chunk", uploadId,
                dataBase64 = Convert.ToBase64String(archive, offset, count) }));
        }
    }
}

public static class AnsibleTaskArchive
{
    public static string CreateBase64(string taskDirectory, IReadOnlyList<AnsibleLocalFile>? localFiles = null)
        => Convert.ToBase64String(CreateBytes(taskDirectory, localFiles));

    public static byte[] CreateBytes(string taskDirectory, IReadOnlyList<AnsibleLocalFile>? localFiles = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var root = Path.GetFullPath(taskDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in SafeFiles(root))
            {
                if (file.StartsWith(Path.Combine(root, AnsibleTaskWorkspacePolicy.ServiceFolderName) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) continue;
                archive.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Fastest);
            }
            var uploadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var local in localFiles ?? [])
            {
                var source = Path.GetFullPath(local.SourcePath);
                if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Локальный файл Ansible не найден или является ссылкой: " + local.SourcePath);
                var name = Path.GetFileName(local.ArchiveName);
                if (name.Length == 0 || !name.Equals(local.ArchiveName, StringComparison.Ordinal))
                    throw new InvalidDataException("Небезопасное имя локального файла Ansible: " + local.ArchiveName);
                if (!uploadNames.Add(name))
                    throw new InvalidDataException("Повторяется имя локального файла Ansible: " + name);
                archive.CreateEntryFromFile(source, ".kitty-uploads/" + name, CompressionLevel.Fastest);
            }
        }
        return stream.ToArray();
    }

    public static async Task<string> CreateTemporaryFileAsync(string taskDirectory,
        IReadOnlyList<AnsibleLocalFile>? localFiles, CancellationToken token)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager", "AnsibleUploads");
        Directory.CreateDirectory(temporaryRoot);
        foreach (var old in Directory.EnumerateFiles(temporaryRoot, "task-*.zip"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) TryDeleteFile(old);
        var path = Path.Combine(temporaryRoot, "task-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var root = Path.GetFullPath(taskDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in SafeFiles(root))
            {
                token.ThrowIfCancellationRequested();
                if (file.StartsWith(Path.Combine(root, AnsibleTaskWorkspacePolicy.ServiceFolderName) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) continue;
                await AddFileAsync(archive, file, Path.GetRelativePath(root, file).Replace('\\', '/'), token);
            }
            var uploadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var local in localFiles ?? [])
            {
                token.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(local.SourcePath);
                if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Локальный файл Ansible не найден или является ссылкой: " + local.SourcePath);
                var name = Path.GetFileName(local.ArchiveName);
                if (name.Length == 0 || !name.Equals(local.ArchiveName, StringComparison.Ordinal))
                    throw new InvalidDataException("Небезопасное имя локального файла Ansible: " + local.ArchiveName);
                if (!uploadNames.Add(name))
                    throw new InvalidDataException("Повторяется имя локального файла Ansible: " + name);
                await AddFileAsync(archive, source, ".kitty-uploads/" + name, token);
            }
            return path;
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
    }

    private static async Task AddFileAsync(ZipArchive archive, string sourcePath, string entryName,
        CancellationToken token)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = entry.Open();
        await source.CopyToAsync(target, 65536, token);
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Папка Ansible-задачи содержит ссылку: " + file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Папка Ansible-задачи содержит ссылку: " + child);
            foreach (var file in SafeFiles(child)) yield return file;
        }
    }
}

public sealed class AnsibleVmSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly string overlayPath;
    private readonly string bootLogPath;
    private readonly ProcessLifetimeJob? lifetimeJob;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private static readonly HashSet<string> ActiveArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveArtifactsGate = new();
    private bool completed;
    public string AnsibleVersion { get; }

    private AnsibleVmSession(Process process, NamedPipeClientStream pipe, StreamReader reader,
        StreamWriter writer, string overlayPath, string bootLogPath, string version, ProcessLifetimeJob? lifetimeJob)
    {
        this.process = process; this.pipe = pipe; this.overlayPath = overlayPath; this.bootLogPath = bootLogPath;
        AnsibleVersion = version;
        this.reader = reader; this.writer = writer;
        this.lifetimeJob = lifetimeJob;
    }

    public static async Task<AnsibleVmSession> StartAsync(string runtimeRoot, string workingRoot,
        CancellationToken cancellationToken, Action<string>? onLog = null)
    {
        onLog?.Invoke("VM: проверяю встроенный runtime.");
        var verified = AnsibleRuntimeVerifier.Verify(runtimeRoot);
        if (!verified.Ready) throw new InvalidOperationException(string.Join(Environment.NewLine, verified.Errors));
        Directory.CreateDirectory(workingRoot);
        CleanupOrphanedArtifacts(workingRoot);
        var qemu = Path.Combine(runtimeRoot, "qemu", "qemu-system-x86_64.exe");
        var qemuImg = Path.Combine(runtimeRoot, "qemu", "qemu-img.exe");
        var image = Path.Combine(runtimeRoot, "images", "ansible-base.img");
        var overlay = Path.Combine(workingRoot, "overlay-" + Guid.NewGuid().ToString("N") + ".qcow2");
        var bootLog = Path.Combine(workingRoot, "boot-" + Guid.NewGuid().ToString("N") + ".log");
        lock (ActiveArtifactsGate) { ActiveArtifacts.Add(overlay); ActiveArtifacts.Add(bootLog); }
        onLog?.Invoke("VM: создаю временный диск.");
        try
        {
            await RunToolAsync(qemuImg, ["create", "-q", "-f", "qcow2", "-F", "raw", "-b", image, overlay], cancellationToken);
        }
        catch
        {
            ReleaseArtifacts(overlay, bootLog); throw;
        }
        var pipeName = "kitty-ansible-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(qemu)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = runtimeRoot, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in BuildQemuArguments(runtimeRoot, overlay, pipeName, bootLog)) info.ArgumentList.Add(argument);
        onLog?.Invoke("VM: запускаю QEMU/WHPX.");
        Process process;
        try { process = Process.Start(info) ?? throw new IOException("QEMU не запустился."); }
        catch
        {
            ReleaseArtifacts(overlay, bootLog); throw;
        }
        ProcessLifetimeJob? lifetimeJob = null;
        try { lifetimeJob = ProcessLifetimeJob.Attach(process); }
        catch
        {
            StopProcess(process); process.Dispose(); ReleaseArtifacts(overlay, bootLog); throw;
        }
        var qemuErrors = new List<string>();
        process.ErrorDataReceived += (_, eventArgs) => CaptureQemuLine(eventArgs.Data, qemuErrors, onLog);
        process.OutputDataReceived += (_, eventArgs) => CaptureQemuLine(eventArgs.Data, qemuErrors, onLog);
        process.BeginErrorReadLine(); process.BeginOutputReadLine();
        onLog?.Invoke($"VM: QEMU запущен, PID {process.Id}; жду serial control pipe.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            try { await WaitForPipeAsync(pipe, process, TimeSpan.FromSeconds(90), cancellationToken); }
            catch (TimeoutException ex)
            { throw StartupFailure(ex.Message, process, qemuErrors, bootLog); }
            onLog?.Invoke("VM: serial control pipe подключён; жду ready от Linux agent.");
            var initialReader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            var initialWriter = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
                { AutoFlush = true, NewLine = "\n" };
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(120));
            string? line;
            AnsibleVmEvent? ready;
            do
            {
                try { line = await initialReader.ReadLineAsync(readyTimeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw StartupFailure("Linux VM не прислала ready за 120 секунд", process, qemuErrors, bootLog); }
                if (line is null) throw StartupFailure("VM закрыла serial control channel до ready", process, qemuErrors, bootLog);
                ready = AnsibleVmEventPolicy.ParseLine(line);
            } while (ready is null);
            if (!string.Equals(ready.Type, "ready", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(ready.AnsibleVersion))
                throw new InvalidDataException("VM не подтвердила готовность: " + line);
            onLog?.Invoke("VM: Linux agent готов; " + ready.AnsibleVersion);
            return new(process, pipe, initialReader, initialWriter, overlay, bootLog, ready.AnsibleVersion, lifetimeJob);
        }
        catch (Exception ex)
        {
            onLog?.Invoke("VM: ошибка запуска: " + ex.Message);
            pipe.Dispose(); StopProcess(process); lifetimeJob?.Dispose(); ReleaseArtifacts(overlay, bootLog); throw;
        }
    }

    public async Task<int> RunAsync(AnsibleRunRequest request, Action<AnsibleVmEvent> onEvent,
        CancellationToken cancellationToken,
        Func<AnsibleConnectionRecoveryPrompt, CancellationToken, Task<int?>>? recoveryTimeout = null)
    {
        if (completed) throw new InvalidOperationException("VM уже завершила запуск.");
        var uploadId = Guid.NewGuid().ToString("N");
        var archivePath = await AnsibleTaskArchive.CreateTemporaryFileAsync(
            request.TaskDirectory, request.LocalFiles, cancellationToken);
        try
        {
            var length = new FileInfo(archivePath).Length;
            await WriteControlAsync(new { action = "upload_start", uploadId, length }, cancellationToken);
            await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                AnsibleUploadProtocol.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[AnsibleUploadProtocol.ChunkSize];
            long sent = 0;
            var previous = 0L;
            while (true)
            {
                var count = await archive.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                sent += count;
                var json = JsonSerializer.Serialize(new { action = "upload_chunk", uploadId,
                    dataBase64 = Convert.ToBase64String(buffer, 0, count) });
                await WriteRawControlAsync(json, cancellationToken);
                if (previous == 0 || sent == length || previous / (1024 * 1024) != sent / (1024 * 1024))
                    onEvent(new("upload", Text: $"Передано в VM {sent:N0} из {length:N0} байт."));
                previous = sent;
            }
            await WriteControlAsync(new { action = "upload_end", uploadId }, cancellationToken);
        }
        finally { TryDelete(archivePath); }
        var payload = new
        {
            action = "run", taskUploadId = uploadId,
            playbookRelativePath = request.PlaybookRelativePath, inventory = request.Inventory,
            hosts = request.Hosts.Select(x => new { inventoryName = x.InventoryName, password = x.Password,
                rootPassword = x.RootPassword, privateKey = x.PrivateKeyBase64,
                privateKeyPassphrase = x.PrivateKeyPassphrase }), vaultPassword = request.VaultPassword,
            verbosity = AnsibleRunPolicy.NormalizeVerbosity(request.Verbosity)
            , connectionRecoveryMinutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(request.ConnectionRecoveryMinutes)
        };
        await WriteControlAsync(payload, cancellationToken);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = runCancellation.Token;
        async Task TrySendStopAsync()
        {
            try { await WriteControlAsync(new { action = "stop" }, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        using var registration = cancellationToken.Register(() => _ = TrySendStopAsync());
        var downloads = new Dictionary<string, PendingDownload>(StringComparer.OrdinalIgnoreCase);
        var recoveryMonitors = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        Exception? recoveryFailure = null;

        async Task MonitorRecoveryAsync(string inventoryName, CancellationToken token)
        {
            var minutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(request.ConnectionRecoveryMinutes);
            try
            {
                while (true)
                {
                    if (minutes > 0) await Task.Delay(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false);
                    else token.ThrowIfCancellationRequested();
                    var additional = recoveryTimeout is null
                        ? null
                        : await recoveryTimeout(new(inventoryName, minutes), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (additional is not null)
                    {
                        minutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(additional.Value);
                        continue;
                    }
                    recoveryFailure = new OperationCanceledException(
                        $"Ожидание восстановления связи с {inventoryName} остановлено.");
                    try { await WriteControlAsync(new { action = "stop" }, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                    runCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        try
        {
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(runToken); }
                catch (OperationCanceledException) when (recoveryFailure is not null) { throw recoveryFailure; }
                if (line is null) throw new IOException("VM закрыла канал до результата Ansible.");
                var item = AnsibleVmEventPolicy.ParseLine(line);
                if (item is null) continue;
                if (item.Type.Equals("connection_lost", StringComparison.OrdinalIgnoreCase))
                {
                    onEvent(item);
                    if (!recoveryMonitors.ContainsKey(item.Message))
                    {
                        var monitor = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                        recoveryMonitors[item.Message] = monitor;
                        _ = MonitorRecoveryAsync(item.Message, monitor.Token);
                    }
                    continue;
                }
                if (item.Type.Equals("connection_recovered", StringComparison.OrdinalIgnoreCase))
                {
                    if (recoveryMonitors.Remove(item.Message, out var monitor))
                    { monitor.Cancel(); monitor.Dispose(); }
                    onEvent(item);
                    continue;
                }
                if (item.Type.StartsWith("download_", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleDownloadAsync(request.DownloadDirectory, item, downloads, runToken);
                    continue;
                }
                onEvent(item);
                if (item.Type == "completed") { completed = true; return item.ExitCode ?? -1; }
                if (item.Type == "error") { completed = true; throw new InvalidOperationException(item.Message); }
            }
        }
        finally
        {
            runCancellation.Cancel();
            foreach (var monitor in recoveryMonitors.Values) { monitor.Cancel(); monitor.Dispose(); }
            foreach (var download in downloads.Values)
            {
                await download.Stream.DisposeAsync();
                TryDelete(download.TemporaryPath);
            }
        }
    }

    private Task WriteControlAsync(object payload, CancellationToken token) =>
        WriteRawControlAsync(JsonSerializer.Serialize(payload), token);

    private async Task WriteRawControlAsync(string payload, CancellationToken token)
    {
        await writerGate.WaitAsync(token).ConfigureAwait(false);
        try { await writer.WriteLineAsync(payload.AsMemory(), token).ConfigureAwait(false); }
        finally { writerGate.Release(); }
    }

    private static async Task HandleDownloadAsync(string outputRoot, AnsibleVmEvent item,
        IDictionary<string, PendingDownload> streams, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new InvalidOperationException("Playbook подготовил файлы для выгрузки, но папка Downloads не задана.");
        var relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathFullyQualified(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
            throw new InvalidDataException("VM вернула небезопасный путь выгрузки: " + item.RelativePath);
        var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VM попыталась записать файл вне Downloads.");
        if (item.Type.Equals("download_start", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (streams.Remove(relative, out var old))
            {
                await old.Stream.DisposeAsync();
                TryDelete(old.TemporaryPath);
            }
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
            streams[relative] = new(temporary,
                new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true));
        }
        else if (item.Type.Equals("download_chunk", StringComparison.OrdinalIgnoreCase))
        {
            if (!streams.TryGetValue(relative, out var download)) throw new InvalidDataException("Получен фрагмент неизвестного файла.");
            await download.Stream.WriteAsync(Convert.FromBase64String(item.DataBase64), token);
        }
        else if (item.Type.Equals("download_end", StringComparison.OrdinalIgnoreCase))
        {
            if (!streams.TryGetValue(relative, out var download)) throw new InvalidDataException("Получено завершение неизвестного файла.");
            await download.Stream.FlushAsync(token);
            await download.Stream.DisposeAsync();
            File.Move(download.TemporaryPath, target, true);
            streams.Remove(relative);
        }
    }

    private sealed record PendingDownload(string TemporaryPath, FileStream Stream);

    public async ValueTask DisposeAsync()
    {
        try { if (pipe.IsConnected) await writer.WriteLineAsync("{\"action\":\"shutdown\"}"); } catch { }
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token); }
        catch { StopProcess(process); }
        writer.Dispose(); reader.Dispose(); pipe.Dispose(); process.Dispose(); lifetimeJob?.Dispose();
        writerGate.Dispose();
        ReleaseArtifacts(overlayPath, bootLogPath);
    }

    internal static IReadOnlyList<string> BuildQemuArguments(string runtimeRoot, string overlay, string pipeName,
        string? bootLogPath = null) =>
    ["-machine", "q35,accel=whpx", "-cpu", "max", "-smp", "2", "-m", "1024",
     "-display", "none", "-vga", "none", "-monitor", "none", "-no-reboot", "-netdev", "user,id=net0",
     "-device", "virtio-net-pci,netdev=net0,romfile=", "-drive", $"file={overlay},if=virtio,format=qcow2",
     "-L", Path.Combine(runtimeRoot, "qemu", "share"),
     "-kernel", Path.Combine(runtimeRoot, "linux", "vmlinuz-virt"),
     "-initrd", Path.Combine(runtimeRoot, "linux", "initramfs-virt"),
     "-append", "root=/dev/vda rootfstype=ext4 rw console=ttyS0 modules=virtio_pci,virtio_blk,virtio_net quiet",
     "-serial", bootLogPath is null ? "null" : "file:" + bootLogPath,
     "-chardev", $"pipe,id=km,path={pipeName}",
     "-device", "isa-serial,chardev=km"];

    private static async Task WaitForPipeAsync(NamedPipeClientStream pipe, Process process, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var connect = pipe.ConnectAsync(timeoutSource.Token);
            while (!connect.IsCompleted)
            {
                if (process.HasExited)
                {
                    timeoutSource.Cancel();
                    try { await connect; } catch (OperationCanceledException) { }
                    throw new TimeoutException($"QEMU завершился с кодом {process.ExitCode} до появления serial control pipe");
                }
                await Task.Delay(100, timeoutSource.Token);
            }
            await connect;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var state = process.HasExited ? $"QEMU завершился с кодом {process.ExitCode}" : "QEMU продолжает работать";
            throw new TimeoutException($"Serial control pipe не появился за {timeout.TotalSeconds:0} секунд; {state}.");
        }
    }

    private static void CaptureQemuLine(string? line, ICollection<string> captured, Action<string>? onLog)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (captured)
        {
            captured.Add(line);
            while (captured.Count > 30) captured.Remove(captured.First());
        }
        onLog?.Invoke("QEMU: " + line);
    }

    private static Exception StartupFailure(string reason, Process process, IEnumerable<string> qemuErrors,
        string bootLogPath)
    {
        var detail = new List<string> { reason + ".", process.HasExited
            ? $"QEMU завершился с кодом {process.ExitCode}." : "QEMU продолжает работать." };
        string[] qemuSnapshot;
        lock (qemuErrors) qemuSnapshot = qemuErrors.ToArray();
        detail.AddRange(qemuSnapshot.TakeLast(12).Select(line => "QEMU: " + line));
        detail.AddRange(ReadSharedTail(bootLogPath, 20).Select(line => "BOOT: " + line));
        return new TimeoutException(string.Join(Environment.NewLine, detail));
    }

    internal static IReadOnlyList<string> ReadSharedTail(string path, int count)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == count) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }
        catch (IOException ex) { return [$"boot log временно недоступен: {ex.Message}"]; }
    }

    private static async Task RunToolAsync(string file, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException($"Не удалось запустить {Path.GetFileName(file)}.");
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)}: {error.Trim()} (код {process.ExitCode}).");
    }
    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { }
    }
    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(100); }
            catch { return; }
        }
    }

    private static void CleanupOrphanedArtifacts(string root)
    {
        string[] active;
        lock (ActiveArtifactsGate) active = ActiveArtifacts.ToArray();
        foreach (var pattern in new[] { "overlay-*.qcow2", "boot-*.log" })
            foreach (var path in Directory.EnumerateFiles(root, pattern))
                if (!active.Contains(path, StringComparer.OrdinalIgnoreCase)) TryDelete(path);
    }

    private static void ReleaseArtifacts(string overlay, string bootLog)
    {
        lock (ActiveArtifactsGate) { ActiveArtifacts.Remove(overlay); ActiveArtifacts.Remove(bootLog); }
        TryDelete(overlay); TryDelete(bootLog);
    }
}

internal sealed class ProcessLifetimeJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private ProcessLifetimeJob(SafeFileHandle handle) => this.handle = handle;

    public static ProcessLifetimeJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)size) || !AssignProcessToJobObject(handle, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new(handle);
        }
        catch { handle.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(
        SafeFileHandle job, IntPtr process);
}

public static class AnsibleRunPolicy
{
    public static int NormalizeVerbosity(int value) => Math.Clamp(value, 0, 4);
    public static string? BecomeMethod(string rootLogin) => rootLogin.Contains("sudo", StringComparison.OrdinalIgnoreCase)
        ? "sudo" : rootLogin.TrimStart().StartsWith("su", StringComparison.OrdinalIgnoreCase) ? "su" : null;
    public static string DefaultDownloadDirectory(string applicationBaseDirectory) =>
        Path.Combine(Path.GetFullPath(applicationBaseDirectory), "Data", "Downloads");
    public static string HumanizeVmRoute(string text) => text.Replace(
        AnsibleInventoryGenerator.QemuHostAddress, "VM→локальный маршрут", StringComparison.Ordinal);
    public static string ServerDownloadFolder(string name, string host) => SafeFolderName($"{name} {host}");
    private static string SafeFolderName(string value)
    {
        const string invalid = "<>:\"/\\|?*";
        var result = string.Concat(value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)).Trim().TrimEnd('.');
        return result.Length == 0 ? "server" : result;
    }
}

public static class AnsibleWindowsDiagnostics
{
    // Machine-readable tokens keep readiness checks independent from the Windows display language.
    public const string HardwareVirtualizationScript =
        "$ErrorActionPreference='Stop'; try { $cs=Get-CimInstance Win32_ComputerSystem; $cpu=Get-CimInstance Win32_Processor | Select-Object -First 1; if ($cs.HypervisorPresent -or $cpu.VirtualizationFirmwareEnabled) {'READY'} else {'DISABLED'} } catch {'UNKNOWN'}";
    public const string WindowsHypervisorScript =
        "$ErrorActionPreference='Stop'; try { if ((Get-CimInstance Win32_ComputerSystem).HypervisorPresent) {'READY'} else {'DISABLED'} } catch {'UNKNOWN'}";
    public const string HypervisorLaunchScript =
        "$ErrorActionPreference='Stop'; try { $bcd=Get-CimInstance -Namespace root/WMI -ClassName BcdObject -Filter \"Id='{current}' and StoreFilePath=''\"; $r=Invoke-CimMethod -InputObject $bcd -MethodName GetElement -Arguments @{Type=[uint32]0x250000F0}; if ($r.Element.Integer -eq 0) {'DISABLED'} elseif ($r.Element.Integer -eq 1) {'READY'} else {'UNKNOWN'} } catch {'UNKNOWN'}";
    public const string HypervisorPlatformScript =
        "$ErrorActionPreference='Stop'; try { $state=(Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform).State; if ($state -eq [Microsoft.Dism.Commands.FeatureState]::Enabled) {'READY'} else {'DISABLED'} } catch {'UNKNOWN'}";

    public static async Task<AnsibleReadinessReport> CheckAsync(string runtimeRoot, string tempRoot,
        Func<CancellationToken, Task>? smokeTest, CancellationToken token)
    {
        var items = new List<AnsibleDiagnosticItem>();
        if (!OperatingSystem.IsWindows())
            return AnsibleReadinessPolicy.Summarize([new("Windows", false,
                "Проверка WHPX выполняется только в Windows.", Unknown: true)]);
        await AddProtocolCommand(items, "Аппаратная виртуализация", HardwareVirtualizationScript,
            "Виртуализация отключена в BIOS/UEFI.", null, token);
        await AddProtocolCommand(items, "Windows-гипервизор", WindowsHypervisorScript,
            "Гипервизор Windows не запущен.", null, token);
        await AddProtocolCommand(items, "Запуск гипервизора Windows", HypervisorLaunchScript,
            "Автозапуск гипервизора отключён.", "bcdedit /set hypervisorlaunchtype auto", token);
        items = ApplyEffectiveHypervisorState(items).ToList();
        await AddProtocolCommand(items, "Windows Hypervisor Platform", HypervisorPlatformScript,
            "Компонент Windows Hypervisor Platform отключён.",
            "Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All", token);
        var runtime = AnsibleRuntimeVerifier.Verify(runtimeRoot);
        items.Add(new("Runtime", runtime.Ready, runtime.Ready ? "QEMU и Linux runtime целы." : string.Join("; ", runtime.Errors)));
        try { Directory.CreateDirectory(tempRoot); var probe = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")); File.WriteAllText(probe, "probe"); File.Delete(probe);
            items.Add(new("Временный диск", true, "Доступен.")); }
        catch (Exception ex) { items.Add(new("Временный диск", false, ex.Message, Unknown: true)); }
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            items.Add(new("Локальный порт", true, $"Можно открыть loopback-порт {port}."));
        }
        catch (Exception ex) { items.Add(new("Локальный порт", false, ex.Message, Unknown: true)); }
        if (items.All(x => x.Success) && smokeTest is not null)
        {
            try { await smokeTest(token); items.Add(new("VM smoke test", true, "VM запущена, ansible-playbook доступен, VM остановлена.")); }
            catch (Exception ex) { items.Add(new("VM smoke test", false, ex.Message, Unknown: true)); }
        }
        return AnsibleReadinessPolicy.Summarize(items);
    }

    public static AnsibleDiagnosticItem InterpretProtocolResult(string stage, string output, int exitCode,
        string disabledMessage, string? enableCommand)
    {
        var token = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "UNKNOWN";
        if (exitCode == 0 && token.Equals("READY", StringComparison.Ordinal))
            return new(stage, true, "Готово.");
        if (exitCode == 0 && token.Equals("DISABLED", StringComparison.Ordinal))
            return new(stage, false, disabledMessage, enableCommand);
        return new(stage, false,
            "Не удалось определить состояние системной настройки.",
            ExitCode: exitCode == 0 ? null : exitCode, Unknown: true);
    }

    public static IReadOnlyList<AnsibleDiagnosticItem> ApplyEffectiveHypervisorState(
        IEnumerable<AnsibleDiagnosticItem> source)
    {
        var items = source.ToArray();
        var hypervisorRunning = items.FirstOrDefault(x => x.Stage == "Windows-гипервизор")?.Success == true;
        if (!hypervisorRunning) return items;

        return items.Select(item => item.Stage == "Запуск гипервизора Windows" && !item.Success
            ? item with
            {
                Success = true,
                Message = "Гипервизор уже запущен; чтение параметра автозапуска BCD не требуется.",
                EnableCommand = null,
                ExitCode = null,
                Unknown = false
            }
            : item).ToArray();
    }

    private static async Task AddProtocolCommand(List<AnsibleDiagnosticItem> items, string stage, string script,
        string knownFailure, string? command, CancellationToken token)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" + script));
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(encoded);
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("PowerShell не запустился.");
            var outputTask = process.StandardOutput.ReadToEndAsync(token); var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token); var output = (await outputTask).Trim(); var error = (await errorTask).Trim();
            items.Add(InterpretProtocolResult(stage, output.Length > 0 ? output : error, process.ExitCode,
                knownFailure, command));
        }
        catch (Exception) { items.Add(new(stage, false,
            "Не удалось запустить системную проверку PowerShell.", Unknown: true)); }
    }
}

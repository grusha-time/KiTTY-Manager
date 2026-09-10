using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net.Sockets;

namespace KiTTYManager.Core;

public enum BatchTaskStepKind { Command, Upload, MakeDirectory, ReplaceText, Template, Check, WaitForText, InteractiveSend, InteractiveWaitAndSend, Download, Delete }
public enum BatchFailureMode { Continue, StopStartingNewServers }
public enum BatchTunnelKind { Local, Remote }
public enum BatchLogLevel { Info, Warning, Error }
public enum BatchPrivilegeMode { SessionUser, AlwaysBecome }
public static class BatchStepInteractionPolicy
{
    public static bool OpenEditorOnDoubleClick(bool insideCheckBox) => !insideCheckBox;

    public static void ApplyShiftSelection(IList<BatchTaskStep> list, ref int anchorIndex, int targetIndex)
    {
        if (list.Count == 0) return;
        if (targetIndex < 0 || targetIndex >= list.Count) return;
        if (anchorIndex < 0 || anchorIndex >= list.Count)
            anchorIndex = 0;

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var i = start; i <= end; i++)
        {
            list[i].IsSelected = true;
        }
    }

    public static void ToggleStepSelection(BatchTaskStep step)
    {
        step.IsSelected = !step.IsSelected;
    }

    public static bool? DetermineSelectAllState(IEnumerable<BatchTaskStep> list)
    {
        var any = false;
        var all = true;
        var count = 0;
        foreach (var item in list)
        {
            count++;
            if (item.IsSelected) any = true;
            else all = false;
        }
        if (count == 0 || !any) return false;
        if (all) return true;
        return null;
    }
}
public sealed class BatchTaskDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Новая задача";
    public List<BatchTaskStep> Steps { get; set; } = [];
    public List<BatchTunnelDefinition> Tunnels { get; set; } = [];
    public bool AutoReconnectTunnels { get; set; } = true;
    // По умолчанию туннели закрываются после завершения всех шагов.
    public bool KeepTunnelsAfterSteps { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public BatchPrivilegeMode PrivilegeMode { get; set; } = BatchPrivilegeMode.SessionUser;
    // Серверы, на которых задача запускалась в последний раз. На чужой машине
    // неизвестные ID просто игнорируются — выбор остаётся за пользователем.
    public List<Guid> ServerIds { get; set; } = [];
}

public sealed class BatchTunnelDefinition
{
    public string Name { get; set; } = "Туннель";
    public BatchTunnelKind Kind { get; set; } = BatchTunnelKind.Remote;
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string DestinationHost { get; set; } = "";
    public int DestinationPort { get; set; }
    public List<Guid> ServerIds { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string ServerDisplay { get; set; } = "Все выбранные";
    [System.Text.Json.Serialization.JsonIgnore] public string KindDisplay => Kind == BatchTunnelKind.Local ? "Local (порт на ПК)" : "Remote (порт на сервере)";
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => $"{BindHost}:{BindPort} → {DestinationHost}:{DestinationPort}";
}

public static class BatchTunnelPolicy
{
    public static bool AppliesTo(BatchTunnelDefinition tunnel, Guid serverId) =>
        tunnel.ServerIds.Count == 0 || tunnel.ServerIds.Contains(serverId);

    public static ForwardedPort Create(BatchTunnelDefinition tunnel) => tunnel.Kind == BatchTunnelKind.Remote
        ? new ForwardedPortRemote(tunnel.BindHost, checked((uint)tunnel.BindPort),
            tunnel.DestinationHost, checked((uint)tunnel.DestinationPort))
        : new ForwardedPortLocal(tunnel.BindHost, checked((uint)tunnel.BindPort),
            tunnel.DestinationHost, checked((uint)tunnel.DestinationPort));

    public static void ValidateRunSelection(BatchTaskDefinition task, IReadOnlyCollection<Guid> selectedServerIds)
    {
        foreach (var tunnel in task.Tunnels)
            if (tunnel.ServerIds.Any(id => !selectedServerIds.Contains(id)))
                throw new InvalidDataException($"Туннель «{tunnel.Name}» ссылается на сервер, который не выбран для запуска.");
        foreach (var tunnel in task.Tunnels.Where(x => x.Kind == BatchTunnelKind.Local))
            if (selectedServerIds.Count(id => AppliesTo(tunnel, id)) != 1)
                throw new InvalidDataException($"Local-туннель «{tunnel.Name}» должен использовать ровно один выбранный сервер: один локальный порт нельзя одновременно открыть для нескольких SSH-маршрутов.");
    }
}

public static class BatchTaskPolicy
{
    public const bool IncludeFilesInPackageByDefault = false;

    public static bool NeedsSftp(IEnumerable<BatchTaskStep> steps) =>
        steps.Any(x => x.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Download or BatchTaskStepKind.ReplaceText or BatchTaskStepKind.Template);

    public static bool EffectiveBecome(BatchPrivilegeMode mode, BatchTaskStep step) =>
        step.Kind is not BatchTaskStepKind.InteractiveSend &&
        (mode == BatchPrivilegeMode.AlwaysBecome || step.Become);

    public static string EffectiveWorkingDirectory(BatchTaskDefinition task, BatchTaskStep step) =>
        string.IsNullOrWhiteSpace(step.WorkingDirectory) ? task.WorkingDirectory : step.WorkingDirectory.Trim();

}

public static class BatchTaskWorkspace
{
    public static IReadOnlyList<string> CleanupStaleDirectories(string root, string currentDirectory)
    {
        if (!Directory.Exists(root)) return [];
        var current = Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var deleted = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), current,
                    StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(directory, true); deleted.Add(directory); }
            catch { }
        }
        return deleted;
    }
}

public static class BatchStepPolicy
{
    public static bool AppliesTo(BatchTaskStep step, Guid serverId) =>
        step.ServerIds.Count == 0 || step.ServerIds.Contains(serverId);

    public static BatchTaskStep Duplicate(BatchTaskStep item) => new()
    {
        Kind = item.Kind, Name = item.Name, Command = item.Command, Source = item.Source,
        Destination = item.Destination, Search = item.Search, Replacement = item.Replacement,
        ExpectedText = item.ExpectedText, ConditionCommand = item.ConditionCommand,
        ActionCommand = item.ActionCommand, TimeoutSeconds = item.TimeoutSeconds,
        Become = item.Become, RunAsUser = item.RunAsUser, RunAsPassword = item.RunAsPassword,
        Backup = item.Backup, ContinueOnError = item.ContinueOnError,
        WaitAfterSeconds = item.WaitAfterSeconds, DownloadFolder = item.DownloadFolder,
        DownloadAsArchive = item.DownloadAsArchive, WaitForExit = item.WaitForExit,
        ServerIds = item.ServerIds.ToList(), WorkingDirectory = item.WorkingDirectory
    };
}

public static class SelectionTogglePolicy
{
    public static bool ShouldSelectAll(IEnumerable<Guid> itemIds, IReadOnlySet<Guid> selectedIds) =>
        !itemIds.All(selectedIds.Contains);
}

public static class BatchTemplateRenderer
{
    public static string Render(string template, ManagedServer server) => template
        .Replace("{{server.name}}", server.Name, StringComparison.Ordinal)
        .Replace("{{server.host}}", server.CleanHost, StringComparison.Ordinal)
        .Replace("{{server.port}}", server.Port.ToString(), StringComparison.Ordinal)
        .Replace("{{server.username}}", server.EffectiveUsername, StringComparison.Ordinal);
}

public sealed class BatchTaskStep
{
    public BatchTaskStepKind Kind { get; set; }
    public string Name { get; set; } = "Шаг";
    public string Command { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Search { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string ExpectedText { get; set; } = "";
    public string ConditionCommand { get; set; } = "";
    public string ActionCommand { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public bool Become { get; set; }
    // Выполнить действие под другой учётной записью (пусто = текущая).
    public string RunAsUser { get; set; } = "";
    public string RunAsPassword { get; set; } = "";
    public bool Backup { get; set; } = true;
    // Ошибка этого действия не останавливает сервер: следующие действия всё равно выполняются.
    public bool ContinueOnError { get; set; }
    // Пауза после успешного завершения действия (0 = без паузы).
    public int WaitAfterSeconds { get; set; }
    // Подпапка внутри папки сессии для скачанных файлов (пусто = без подпапки).
    public string DownloadFolder { get; set; } = "";
    // Создать сжатую архивную копию рядом с источником и скачать её, не распаковывая.
    public bool DownloadAsArchive { get; set; }
    // Дождаться завершения команды / интерактивной программы (по умолчанию включено).
    public bool WaitForExit { get; set; } = true;
    // Пустой список — действие выполняется на всех серверах задачи.
    public List<Guid> ServerIds { get; set; } = [];
    // Пустая строка — используется рабочая папка всей задачи.
    public string WorkingDirectory { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSelected { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string KindDisplay => Kind switch
    {
        BatchTaskStepKind.Command => "Выполнить команду",
        BatchTaskStepKind.WaitForText => "Дождаться текста и выполнить команду",
        BatchTaskStepKind.InteractiveSend => "Отправить текст программе",
        BatchTaskStepKind.InteractiveWaitAndSend => "Диалог: ждать текст → ответить",
        BatchTaskStepKind.Check => "Проверить условие командой",
        BatchTaskStepKind.Upload => "Загрузить файлы или папки на сервер",
        BatchTaskStepKind.Download => "Скачать с сервера",
        BatchTaskStepKind.Delete => "Удалить на сервере",
        BatchTaskStepKind.MakeDirectory => "Создать каталог",
        BatchTaskStepKind.ReplaceText => "Заменить текст",
        BatchTaskStepKind.Template => "Применить шаблон",
        _ => Kind.ToString()
    };
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => Kind switch
    {
        BatchTaskStepKind.Command or BatchTaskStepKind.Check => Command,
        BatchTaskStepKind.WaitForText => $"«{ExpectedText}» → {ActionCommand}",
        BatchTaskStepKind.InteractiveSend => Command,
        BatchTaskStepKind.InteractiveWaitAndSend => string.Join("; ", ExpectedText.Replace("\r\n", "\n").Split('\n')
            .Select((wait, i) => $"«{wait}» → «{ActionCommand.Replace("\r\n", "\n").Split('\n').ElementAtOrDefault(i) ?? ""}»")),
        BatchTaskStepKind.MakeDirectory => Destination,
        BatchTaskStepKind.Upload => Source.Replace("\r\n", "\n").Replace('\n', ';') + " → " + Destination,
        BatchTaskStepKind.Template => $"{Source} → {Destination}",
        BatchTaskStepKind.Download => Source.Replace("\r\n", "\n").Replace('\n', ';') + " → downloads/",
        BatchTaskStepKind.Delete => Destination,
        BatchTaskStepKind.ReplaceText => $"{Destination}: «{Search}» → «{Replacement}»",
        _ => ""
    };
}

public static class BatchTaskFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // JSON is a YAML 1.2 subset. Keeping one strict representation avoids a parser dependency.
    public static BatchTaskDefinition Load(string path)
    {
        var task = JsonSerializer.Deserialize<BatchTaskDefinition>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Пустое описание задачи.");
        task.WorkingDirectory ??= "";
        task.Steps ??= [];
        task.Tunnels ??= [];
        return task;
    }

    public static void Save(string path, BatchTaskDefinition task)
    {
        Validate(task);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(task, Options), new UTF8Encoding(false));
    }

    public static void ExportPackage(string taskDirectory, string packagePath)
    {
        var manifest = Path.Combine(taskDirectory, "task.yaml");
        if (!File.Exists(manifest)) throw new FileNotFoundException("В папке нет task.yaml.", manifest);
        _ = Load(manifest);
        var sourceRoot = Path.GetFullPath(taskDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(packagePath).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Пакет нельзя сохранять внутри архивируемой папки.");
        WritePackageAtomically(packagePath,
            temporary => ZipFile.CreateFromDirectory(taskDirectory, temporary, CompressionLevel.Optimal, false));
    }

    public static void ExportPackageWithFiles(
        string taskDirectory, BatchTaskDefinition task, string packagePath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "KiTTYManager", "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var portable = CloneTask(task);
            foreach (var step in portable.Steps.Where(step =>
                         step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template))
            {
                var packaged = new List<string>();
                foreach (var source in SplitSources(step.Source))
                {
                    var local = ResolveLocalSource(taskDirectory, source);
                    if (!File.Exists(local) && !Directory.Exists(local))
                        throw new FileNotFoundException("Не найден файл или каталог задачи.", local);
                    var name = UniqueName(staging, Path.GetFileName(local.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                    var target = Path.Combine(staging, name);
                    if (Directory.Exists(local)) CopyDirectory(local, target);
                    else File.Copy(local, target);
                    packaged.Add(name);
                }
                step.Source = string.Join('\n', packaged);
            }
            Save(Path.Combine(staging, "task.yaml"), portable);
            ExportPackage(staging, packagePath);
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    public static void ResolveImportedLocalSources(BatchTaskDefinition task, string taskDirectory)
    {
        foreach (var step in task.Steps.Where(step =>
                     step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template))
            step.Source = string.Join('\n', SplitSources(step.Source)
                .Select(source => Path.IsPathFullyQualified(source)
                    ? Path.GetFullPath(source)
                    : Path.GetFullPath(Path.Combine(taskDirectory, source))));
    }

    public static bool HasFiles(BatchTaskDefinition task) =>
        task.Steps.Any(step => step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template &&
                               ParseLocalSources(step.Source).Length > 0);

    public static IReadOnlyList<string> GetMissingLocalSources(string taskDirectory, BatchTaskDefinition task)
    {
        var missing = new List<string>();
        foreach (var step in task.Steps.Where(step =>
                     step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template))
        {
            foreach (var source in ParseLocalSources(step.Source))
            {
                var local = ResolveLocalSource(taskDirectory, source);
                if (!File.Exists(local) && !Directory.Exists(local))
                    missing.Add(local);
            }
        }
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static BatchTaskDefinition CloneTask(BatchTaskDefinition task) =>
        JsonSerializer.Deserialize<BatchTaskDefinition>(JsonSerializer.Serialize(task, Options), Options)!;

    public static string[] ParseLocalSources(string? source) => (source ?? "").Replace("\r\n", "\n")
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string SerializeLocalSources(IEnumerable<string> sources) =>
        string.Join('\n', sources.Where(source => !string.IsNullOrWhiteSpace(source)).Select(source => source.Trim()));

    private static string[] SplitSources(string source) => ParseLocalSources(source);

    private static string ResolveLocalSource(string taskDirectory, string source) =>
        Path.IsPathFullyQualified(source)
            ? Path.GetFullPath(source)
            : Path.GetFullPath(Path.Combine(taskDirectory, source));

    private static string UniqueName(string directory, string wanted)
    {
        if (wanted.Length == 0) throw new InvalidDataException("Не удалось определить имя файла или каталога.");
        if (!File.Exists(Path.Combine(directory, wanted)) && !Directory.Exists(Path.Combine(directory, wanted))) return wanted;
        var name = Path.GetFileNameWithoutExtension(wanted);
        var extension = Path.GetExtension(wanted);
        for (var index = 2; ; index++)
        {
            var candidate = $"{name} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate)) &&
                !Directory.Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    // Только task.yaml без загруженных файлов — для переноса описания задачи.
    public static void ExportPackageWithoutFiles(string taskDirectory, string packagePath)
    {
        var manifest = Path.Combine(taskDirectory, "task.yaml");
        if (!File.Exists(manifest)) throw new FileNotFoundException("В папке нет task.yaml.", manifest);
        _ = Load(manifest);
        WritePackageAtomically(packagePath, temporary =>
        {
            using var zip = ZipFile.Open(temporary, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(manifest, "task.yaml", CompressionLevel.Optimal);
        });
    }

    internal static void WritePackageAtomically(string packagePath, Action<string> write)
    {
        var temporary = Path.GetFullPath(packagePath) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            write(temporary);
            File.Move(temporary, packagePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string ImportPackage(string packagePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(packagePath);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Пакет содержит небезопасный путь.");
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true); }
        }
        var manifest = Path.Combine(destinationDirectory, "task.yaml");
        _ = Load(manifest);
        return manifest;
    }

    public static bool SupportsRunAs(BatchTaskStepKind kind) => kind is
        BatchTaskStepKind.Command or BatchTaskStepKind.Check or BatchTaskStepKind.MakeDirectory or
        BatchTaskStepKind.Delete or BatchTaskStepKind.WaitForText or BatchTaskStepKind.InteractiveWaitAndSend;

    public static void Validate(BatchTaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.Name)) throw new InvalidDataException("Не задано имя задачи.");
        if (task.Steps.Count == 0 && task.Tunnels.Count == 0)
            throw new InvalidDataException("Задача не содержит шагов или туннелей.");
        if (task.WorkingDirectory.Contains('\n') || task.WorkingDirectory.Contains('\r'))
            throw new InvalidDataException("Рабочая папка содержит недопустимый перенос строки.");
        if (!Enum.IsDefined(task.PrivilegeMode)) throw new InvalidDataException("Неизвестный режим повышения прав.");
        foreach (var step in task.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name)) throw new InvalidDataException("У шага нет имени.");
            if (!string.IsNullOrWhiteSpace(step.RunAsUser) && !SupportsRunAs(step.Kind))
                throw new InvalidDataException($"Шаг «{step.Name}»: выполнение под другой учётной записью не поддерживается для {step.Kind}. Используйте Command или Ansible.");
            if ((step.Kind is BatchTaskStepKind.Command or BatchTaskStepKind.Check) && string.IsNullOrWhiteSpace(step.Command))
                throw new InvalidDataException($"Шаг «{step.Name}»: не задана команда.");
            if (step.Kind is BatchTaskStepKind.WaitForText &&
                (string.IsNullOrWhiteSpace(step.Command) || string.IsNullOrEmpty(step.ExpectedText) || string.IsNullOrWhiteSpace(step.ActionCommand)))
                throw new InvalidDataException($"Шаг «{step.Name}»: задайте команду наблюдения, ожидаемый текст и команду действия.");
            if (step.Kind is BatchTaskStepKind.WaitForText && step.TimeoutSeconds is < 1 or > 3600)
                throw new InvalidDataException($"Шаг «{step.Name}»: ожидание должно быть от 1 до 3600 секунд.");
            if (step.Kind is BatchTaskStepKind.InteractiveSend && string.IsNullOrWhiteSpace(step.Command))
                throw new InvalidDataException($"Шаг «{step.Name}»: не задан текст для интерактивной консоли.");
            if (step.Kind is BatchTaskStepKind.InteractiveWaitAndSend &&
                (string.IsNullOrEmpty(step.ExpectedText) || string.IsNullOrEmpty(step.ActionCommand)))
                throw new InvalidDataException($"Шаг «{step.Name}»: задайте ожидаемый текст и ответ.");
            if (step.Kind is BatchTaskStepKind.InteractiveWaitAndSend && step.TimeoutSeconds is < 1 or > 3600)
                throw new InvalidDataException($"Шаг «{step.Name}»: ожидание должно быть от 1 до 3600 секунд.");
            if (step.Kind is BatchTaskStepKind.Upload && (string.IsNullOrWhiteSpace(step.Source) || string.IsNullOrWhiteSpace(step.Destination)))
                throw new InvalidDataException($"Шаг «{step.Name}»: не заданы пути.");
            if (step.Kind is BatchTaskStepKind.Download && string.IsNullOrWhiteSpace(step.Source))
                throw new InvalidDataException($"Шаг «{step.Name}»: не заданы пути на сервере.");
            if (step.Kind is BatchTaskStepKind.Delete && string.IsNullOrWhiteSpace(step.Destination))
                throw new InvalidDataException($"Шаг «{step.Name}»: не задан путь или маска для удаления.");
            if (step.Kind is BatchTaskStepKind.MakeDirectory && string.IsNullOrWhiteSpace(step.Destination))
                throw new InvalidDataException($"Шаг «{step.Name}»: не задан каталог.");
            if (step.Kind is BatchTaskStepKind.ReplaceText &&
                (string.IsNullOrWhiteSpace(step.Destination) || string.IsNullOrEmpty(step.Search)))
                throw new InvalidDataException($"Шаг «{step.Name}»: не заданы файл или искомый текст.");
            if (step.Kind is BatchTaskStepKind.Template &&
                (string.IsNullOrWhiteSpace(step.Source) || string.IsNullOrWhiteSpace(step.Destination)))
                throw new InvalidDataException($"Шаг «{step.Name}»: не заданы шаблон или файл назначения.");
        }
        foreach (var tunnel in task.Tunnels)
        {
            if (string.IsNullOrWhiteSpace(tunnel.Name)) throw new InvalidDataException("У туннеля нет имени.");
            if (string.IsNullOrWhiteSpace(tunnel.BindHost)) throw new InvalidDataException($"Туннель «{tunnel.Name}»: не задан адрес прослушивания.");
            if (tunnel.BindPort is < 1 or > 65535) throw new InvalidDataException($"Туннель «{tunnel.Name}»: порт прослушивания должен быть 1–65535.");
            if (string.IsNullOrWhiteSpace(tunnel.DestinationHost)) throw new InvalidDataException($"Туннель «{tunnel.Name}»: не задан целевой хост.");
            if (tunnel.DestinationPort is < 1 or > 65535) throw new InvalidDataException($"Туннель «{tunnel.Name}»: целевой порт должен быть 1–65535.");
        }
        for (var i = 0; i < task.Tunnels.Count; i++)
        for (var j = i + 1; j < task.Tunnels.Count; j++)
        {
            var first = task.Tunnels[i]; var second = task.Tunnels[j];
            var sameKindAndPort = first.Kind == second.Kind && first.BindPort == second.BindPort;
            var sameListener = sameKindAndPort && (first.Kind == BatchTunnelKind.Local ||
                BindHostsOverlap(first.BindHost, second.BindHost));
            var sameServers = first.Kind == BatchTunnelKind.Local || first.ServerIds.Count == 0 || second.ServerIds.Count == 0 ||
                first.ServerIds.Intersect(second.ServerIds).Any();
            if (sameListener && sameServers)
                throw new InvalidDataException($"Туннели «{first.Name}» и «{second.Name}» используют один адрес прослушивания.");
        }
    }

    private static bool BindHostsOverlap(string first, string second)
    {
        static bool Wildcard(string value) => value.Trim() is "" or "0.0.0.0" or "::" or "*";
        return Wildcard(first) || Wildcard(second) ||
               string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record BatchTaskLog(DateTimeOffset Time, Guid ServerId, string ServerName, string Step, string Message,
    BatchLogLevel Level = BatchLogLevel.Info, string Source = "Шаг");
public static class BatchLogFormatter
{
    public static IEnumerable<BatchTaskLog> Filter(IEnumerable<BatchTaskLog> logs, Guid? serverId,
        BatchLogLevel? level, bool consoleOnly = false, bool errorsAndWarningsOnly = false)
    {
        var result = logs.Where(x => serverId is null || x.ServerId == serverId)
            .Where(x => level is null || x.Level == level);
        if (consoleOnly)
            result = result.Where(x => x.Source == "Консоль" ||
                                       x.Message.StartsWith("Вывод команды: ", StringComparison.Ordinal) ||
                                       x.Message.StartsWith("Вывод ошибок: ", StringComparison.Ordinal));
        if (errorsAndWarningsOnly)
            result = result.Where(x => x.Level is BatchLogLevel.Warning or BatchLogLevel.Error);
        return result;
    }

    public static string Format(BatchTaskLog item, bool hideTimestamp = false, bool hidePrefix = false)
    {
        if (hidePrefix)
        {
            var message = item.Message;
            return hideTimestamp ? message + Environment.NewLine : $"[{item.Time:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
        }

        var prefix = hideTimestamp
            ? $"[{item.Level}] {item.ServerName} | {item.Source} | {item.Step} | "
            : $"[{item.Time:yyyy-MM-dd HH:mm:ss.fff}] [{item.Level}] {item.ServerName} | {item.Source} | {item.Step} | ";
        return prefix + item.Message + Environment.NewLine;
    }
}

public sealed record AnsibleLogEntry(DateTimeOffset Time, string Text, bool IsConsole, bool IsErrorOrWarning);

public static class AnsibleLogFormatter
{
    public static string Format(AnsibleLogEntry item, bool hideTimestamp = false) =>
        hideTimestamp ? item.Text + Environment.NewLine : $"[{item.Time:yyyy-MM-dd HH:mm:ss}] {item.Text}{Environment.NewLine}";

    public static IEnumerable<AnsibleLogEntry> Filter(IEnumerable<AnsibleLogEntry> entries,
        bool consoleOnly = false, bool errorsAndWarningsOnly = false)
    {
        var result = entries;
        if (consoleOnly) result = result.Where(x => x.IsConsole);
        if (errorsAndWarningsOnly) result = result.Where(x => x.IsErrorOrWarning);
        return result;
    }
}

public static partial class BatchTerminalOutputSanitizer
{
    [GeneratedRegex("\\x1B(?:\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\[[0-?]*[ -/]*[@-~]|[()][0-9A-Z]|[@-_])")]
    private static partial Regex AnsiSequence();
    [GeneratedRegex("(?:printf\\s+)?'(?:\\\\[0-7]{3}){8,}")]
    private static partial Regex EncodedMarkerCommand();

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = AnsiSequence().Replace(value, "").Replace("\r", "");
        clean = new string(clean.Where(ch => ch is '\n' or '\t' || ch >= ' ').ToArray());
        var lines = clean.Split('\n').Where(line =>
            !line.Contains("__KITTY_MANAGER_DONE_", StringComparison.Ordinal) &&
            !EncodedMarkerCommand().IsMatch(line));
        return string.Join(Environment.NewLine, lines).Trim('\r', '\n');
    }
}

public sealed class BatchTerminalOutputSanitizerSession
{
    private const string CompletionPrefix = "__KITTY_MANAGER_DONE_";
    private readonly StringBuilder line = new();
    private int escapeState;

    public string Feed(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var output = new StringBuilder();
        foreach (var ch in value)
        {
            if (ConsumeEscape(ch)) continue;
            if (ch == '\r' || ch != '\n' && ch < ' ') continue;
            if (ch == '\n')
            {
                var clean = BatchTerminalOutputSanitizer.Sanitize(line.ToString());
                if (!string.IsNullOrEmpty(clean)) output.Append(clean).AppendLine();
                line.Clear();
                continue;
            }
            line.Append(ch);
        }
        FlushSafeText(output);
        return output.ToString().TrimEnd('\r', '\n');
    }

    public string Complete()
    {
        var clean = BatchTerminalOutputSanitizer.Sanitize(line.ToString());
        line.Clear(); escapeState = 0;
        return clean;
    }

    private bool ConsumeEscape(char ch)
    {
        switch (escapeState)
        {
            case 0:
                if (ch != '\x1b') return false;
                escapeState = 1; return true;
            case 1:
                escapeState = ch == '[' ? 2 : ch == ']' ? 3 : ch is '(' or ')' ? 5 : 0; return true;
            case 2:
                if (ch is >= '@' and <= '~') escapeState = 0;
                return true;
            case 3:
                if (ch == '\a') escapeState = 0;
                else if (ch == '\x1b') escapeState = 4;
                return true;
            case 5:
                escapeState = 0; return true;
            default:
                escapeState = ch == '\\' ? 0 : 3; return true;
        }
    }

    private void FlushSafeText(StringBuilder output)
    {
        if (line.Length == 0) return;
        var value = line.ToString();
        if (value.Contains("printf", StringComparison.Ordinal) ||
            value.Contains("'\\", StringComparison.Ordinal) ||
            CompletionPrefix.StartsWith(value, StringComparison.Ordinal) ||
            value.Contains(CompletionPrefix, StringComparison.Ordinal)) return;
        var held = new[] { CompletionPrefix, "printf", "'\\" }.Max(token =>
            Enumerable.Range(1, Math.Min(value.Length, token.Length - 1))
                .Where(length => token.StartsWith(value[^length..], StringComparison.Ordinal))
                .DefaultIfEmpty(0).Max());
        var count = value.Length - held;
        if (count <= 0) return;
        output.Append(value.AsSpan(0, count));
        line.Remove(0, count);
    }
}
public sealed record BatchServerResult(Guid ServerId, string ServerName, bool Success, bool Cancelled, string Message,
    IReadOnlyList<string> Backups, string? StoppedAtStep = null, bool Failed = false);
public sealed record BatchRunResult(IReadOnlyList<BatchServerResult> Servers, bool Cancelled);

public sealed record BatchRunSummaryLine(Guid ServerId, string ServerName, string Message, BatchLogLevel Level);

/// <summary>
/// Builds the closing lines of a batch run: one verdict per server (including
/// the step a server stopped at) and the task-wide total. These lines are part
/// of the log model, so level and server filters keep showing them.
/// </summary>
public static class BatchRunSummary
{
    public const string ConnectStepLabel = "Подключение";

    public static IReadOnlyList<BatchRunSummaryLine> Lines(IReadOnlyList<BatchServerResult> servers, int totalServers)
    {
        var lines = new List<BatchRunSummaryLine>();
        foreach (var server in servers)
        {
            var step = string.IsNullOrWhiteSpace(server.StoppedAtStep) ? null : server.StoppedAtStep;
            if (server.Success && step is null)
                lines.Add(new(server.ServerId, server.ServerName, "успешно, все шаги выполнены", BatchLogLevel.Info));
            else if (server.Success)
                lines.Add(new(server.ServerId, server.ServerName,
                    $"выполнен не полностью, оставшиеся шаги пропущены начиная с «{step}»: задача остановлена после ошибки", BatchLogLevel.Warning));
            else if (server.Cancelled && !server.Failed)
                lines.Add(new(server.ServerId, server.ServerName,
                    step is null ? "остановлен после выполнения всех шагов" : $"остановлен на шаге «{step}»", BatchLogLevel.Warning));
            else
                lines.Add(new(server.ServerId, server.ServerName,
                    $"ошибка на шаге «{step ?? ConnectStepLabel}»: {server.Message}", BatchLogLevel.Error));
        }
        lines.Add(new(Guid.Empty, "Задача", $"Итог: успешно {servers.Count(x => x.Success)} из {totalServers}.", BatchLogLevel.Info));
        return lines;
    }
}

public sealed record ConnectionRecoveryPrompt(string ServerName, string Operation, int PreviousMinutes);
public sealed record AmbiguousActionRetryPrompt(string ServerName, string Operation);

public static class TaskConnectionRecoveryPolicy
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    public static int NormalizeMinutes(int value) => Math.Clamp(value, 0, 99999);
    public static bool CanRetryWithoutDuplicateEffect(BatchTaskStepKind kind) =>
        kind is BatchTaskStepKind.Check or BatchTaskStepKind.MakeDirectory or
            BatchTaskStepKind.Download or BatchTaskStepKind.Delete;
    public static bool IsConnectivityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SshConnectionException or SshOperationTimeoutException or SocketException or EndOfStreamException ||
                current is ObjectDisposedException && current.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                current is IOException && (current.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                                             current.Message.Contains("соединен", StringComparison.OrdinalIgnoreCase) ||
                                             current.Message.Contains("socket", StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }
}

public static class BatchInteractiveCompletionMarker
{
    public static string Create() => "__KITTY_MANAGER_DONE_" + Guid.NewGuid().ToString("N") + "__";

    public static string BuildCommand(string marker)
    {
        if (string.IsNullOrEmpty(marker) || marker.Any(ch => ch is < ' ' or > '~'))
            throw new ArgumentException("Маркер завершения должен содержать только печатные ASCII-символы.", nameof(marker));
        var octal = string.Concat(Encoding.ASCII.GetBytes(marker).Select(value => $"\\{Convert.ToString(value, 8).PadLeft(3, '0')}"));
        return $"printf '{octal}\\n'";
    }
}

public sealed class BatchTaskRunner
{
    internal static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(10);
    private readonly SshConnectionService connections;
    private readonly Func<Guid, CancellationToken, Task<ActiveRoute>>? routeFactory;
    private readonly Action<ActiveRoute>? routeRelease;
    private string[] activeSecrets = [];
    private int running;
    public int MaxParallelism { get; set; } = 5;
    public event Action<BatchTaskLog>? Log;
    public event Action<int, int>? Progress;
    public Func<ConnectionRecoveryPrompt, CancellationToken, Task<int?>>? RecoveryTimeout { get; set; }
    public Func<AmbiguousActionRetryPrompt, CancellationToken, Task<bool>>? ConfirmAmbiguousRetry { get; set; }

    public BatchTaskRunner(SshConnectionService connections,
        Func<Guid, CancellationToken, Task<ActiveRoute>>? routeFactory = null,
        Action<ActiveRoute>? routeRelease = null) =>
        (this.connections, this.routeFactory, this.routeRelease) =
        (connections, routeFactory, routeRelease);

    public async Task<BatchRunResult> RunAsync(ManagerConfig config, BatchTaskDefinition task,
        IEnumerable<Guid> serverIds, string taskDirectory, BatchFailureMode failureMode,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("Исполнитель уже занят другой массовой задачей.");
        activeSecrets = SecretRedactor.Secrets(config)
            .Concat(task.Steps.Select(step => step.RunAsPassword))
            .Where(value => !string.IsNullOrEmpty(value)).Distinct().ToArray();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = runCancellation.Token;
        var states = new Dictionary<Guid, ServerRunState>();
        try
        {
            BatchTaskFile.Validate(task);
            var ids = serverIds.Distinct().ToArray();
            BatchTunnelPolicy.ValidateRunSelection(task, ids);
            var unknownStepServers = task.Steps.SelectMany(x => x.ServerIds).Distinct()
                .Where(id => !ids.Contains(id)).ToArray();
            if (unknownStepServers.Length > 0)
                throw new InvalidDataException("В действиях остались серверы, которые не выбраны для запуска.");
            foreach (var id in ids)
                states[id] = new ServerRunState(config.FindServer(id) ?? throw new InvalidOperationException("Сессия не найдена."));

            // Этап 1: подключения (не более MaxParallelism одновременно).
            using (var gate = new SemaphoreSlim(Math.Clamp(MaxParallelism, 1, 5)))
            {
                var connectJobs = ids.Select(async id =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await ConnectWithRecoveryAsync(config, task, states[id], "первичное подключение", token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { states[id].StoppedAtStep = BatchRunSummary.ConnectStepLabel; }
                    catch (Exception ex) { states[id].Failed = true; states[id].ErrorMessage = SecretRedactor.Redact(ex.Message, states[id].Server, activeSecrets); states[id].StoppedAtStep = BatchRunSummary.ConnectStepLabel; Emit(states[id], "", "Ошибка подключения: " + states[id].ErrorMessage, BatchLogLevel.Error); }
                    finally { gate.Release(); }
                }).ToArray();
                await Task.WhenAll(connectJobs).ConfigureAwait(false);
            }

            var connectedIds = ids.Where(id => states[id].Connected).ToArray();

            // Этап 2: действия выполняются по порядку на всех подходящих серверах.
            var conditionCache = new ConcurrentDictionary<(Guid, string), bool>();
            var stopAll = 0;
            var stoppedEarly = 0;
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    for (var stepIndex = 0; stepIndex < task.Steps.Count; stepIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        var step = task.Steps[stepIndex];
                        var stepLabel = $"Шаг {stepIndex + 1}/{task.Steps.Count}: {step.Name}";
                        if (Volatile.Read(ref stopAll) != 0)
                        {
                            stoppedEarly = 1;
                            foreach (var id in connectedIds.Where(id => !states[id].Failed))
                            {
                                states[id].StoppedAtStep = stepLabel;
                                Emit(states[id], "", "Оставшиеся шаги пропущены: задача остановлена после ошибки", BatchLogLevel.Warning);
                            }
                            break;
                        }
                    var applicable = connectedIds
                        .Where(id => !states[id].Failed && BatchStepPolicy.AppliesTo(step, id)).ToArray();
                    foreach (var id in connectedIds.Where(id => !states[id].Failed && !BatchStepPolicy.AppliesTo(step, id)))
                        Emit(states[id], stepLabel, "Пропущен: сервер не выбран для этого действия");
                    if (applicable.Length == 0)
                    {
                        Progress?.Invoke(stepIndex + 1, task.Steps.Count);
                        continue;
                    }
                    var stepHadError = 0;
                    var stepJobs = applicable.Select(async id =>
                    {
                        var state = states[id];
                        state.StoppedAtStep = stepLabel;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            if (!string.IsNullOrWhiteSpace(step.ConditionCommand))
                            {
                                var key = (id, step.ConditionCommand);
                                if (!conditionCache.TryGetValue(key, out var conditionOk))
                                {
                                    conditionOk = await ShouldRunStepAsync(state, step, task, token).ConfigureAwait(false);
                                    conditionCache[key] = conditionOk;
                                }
                                if (!conditionOk)
                                {
                                    Emit(state, stepLabel, "Условие не выполнено — шаг пропущен");
                                    return;
                                }
                            }
                            var become = BatchTaskPolicy.EffectiveBecome(task.PrivilegeMode, step);
                            var cwd = BatchTaskPolicy.EffectiveWorkingDirectory(task, step);
                            Emit(state, stepLabel, "Запуск: " + DescribeStep(step, cwd, become));
                            await ExecuteWithRecoveryAsync(config, task, state, step, taskDirectory, cwd, become, stepLabel, token).ConfigureAwait(false);
                            Emit(state, stepLabel, "Готово");
                            if (step.WaitAfterSeconds > 0)
                            {
                                Emit(state, stepLabel, $"Пауза {step.WaitAfterSeconds} с после завершения");
                                await Task.Delay(TimeSpan.FromSeconds(step.WaitAfterSeconds), token).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            state.ErrorMessage = SecretRedactor.Redact(ex.Message, state.Server, activeSecrets);
                            if (step.ContinueOnError)
                            {
                                Emit(state, stepLabel, "Ошибка (действие помечено как некритичное, выполнение продолжается): " +
                                    state.ErrorMessage, BatchLogLevel.Warning);
                                return;
                            }
                            Interlocked.Exchange(ref stepHadError, 1);
                            state.Failed = true;
                            Emit(state, stepLabel, "Ошибка: " + state.ErrorMessage, BatchLogLevel.Error);
                        }
                    }).ToArray();
                    try { await Task.WhenAll(stepJobs).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    Progress?.Invoke(stepIndex + 1, task.Steps.Count);
                    if (stepHadError != 0 && failureMode == BatchFailureMode.StopStartingNewServers)
                        Volatile.Write(ref stopAll, 1);
                    }
                    if (stoppedEarly == 0)
                        foreach (var id in connectedIds.Where(id => !states[id].Failed))
                            states[id].StoppedAtStep = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Пользовательская остановка между шагами: доводим до
                    // результатов, чтобы каждый сервер получил вердикт со своим
                    // шагом остановки вместо исчезнувшего журнала.
                    stoppedEarly = 1;
                }
            }

            // Этап 3: туннели. По умолчанию закрываются после шагов; если включено
            // KeepTunnelsAfterSteps — держат соединения до остановки.
            // Снимок состояния делаем ДО очистки: CleanupConnection сбрасывает Connected.
            var snapshot = states.ToDictionary(kv => kv.Key, kv => (kv.Value.Connected, kv.Value.Failed, kv.Value.ErrorMessage, kv.Value.StoppedAtStep));
            var keepAlive = task.KeepTunnelsAfterSteps
                ? connectedIds.Where(id => states[id].Tunnels.Count > 0 && !states[id].Failed).ToArray()
                : [];
            var keepAliveJobs = keepAlive.Select(id => TunnelKeepAliveAsync(config, task, states[id], token)).ToArray();
            foreach (var id in connectedIds.Except(keepAlive))
                CleanupConnection(states[id]);
            if (keepAliveJobs.Length > 0 && !cancellationToken.IsCancellationRequested)
            {
                foreach (var id in keepAlive)
                    Emit(states[id], "Туннели", "Работают; соединение будет открыто до остановки", source: "Туннель");
                try { await Task.WhenAll(keepAliveJobs).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            foreach (var state in states.Values) CleanupConnection(state);

            var results = ids.Select(id =>
            {
                var state = states[id];
                var snap = snapshot[id];
                var success = snap.Connected && !snap.Failed && !cancellationToken.IsCancellationRequested;
                var message = cancellationToken.IsCancellationRequested && !snap.Failed ? "Остановлено"
                    : !snap.Connected ? snap.ErrorMessage ?? "Не подключено"
                    : snap.Failed ? snap.ErrorMessage ?? "Ошибка"
                    : "Готово";
                return new BatchServerResult(id, state.Server.Name, success, cancellationToken.IsCancellationRequested, message, state.Backups, snap.StoppedAtStep, snap.Failed);
            }).ToList();
            return new(results.OrderBy(x => x.ServerName).ToArray(), cancellationToken.IsCancellationRequested);
        }
        finally
        {
            runCancellation.Cancel();
            foreach (var state in states.Values) CleanupConnection(state);
            activeSecrets = [];
            Interlocked.Exchange(ref running, 0);
        }
    }

    private sealed class ServerRunState
    {
        public ServerRunState(ManagedServer server) => Server = server;
        public ManagedServer Server { get; }
        public ActiveRoute? Route { get; set; }
        public SftpClient? Sftp { get; set; }
        public ShellStream? Shell { get; set; }
        public BatchInteractiveOutputCoordinator? ShellOutput { get; set; }
        public BatchTerminalOutputSanitizerSession? ShellSanitizer { get; set; }
        public CancellationTokenSource? ShellReaderCancellation { get; set; }
        public Task? ShellReader { get; set; }
        public List<ForwardedPort> Tunnels { get; } = [];
        public TaskCompletionSource<Exception>? TunnelFault { get; set; }
        public List<string> Backups { get; } = [];
        public bool Connected { get; set; }
        public bool Failed { get; set; }
        public string? ErrorMessage { get; set; }
        public string? StoppedAtStep { get; set; }
    }

    private async Task ConnectServerAsync(ManagerConfig config, BatchTaskDefinition task,
        ServerRunState state, CancellationToken token)
    {
        var server = state.Server;
        Emit(state, "", $"Подключение к {server.CleanHost}:{server.Port}");
        state.Route = routeFactory is null
            ? (await connections.ConnectBestAsync(config, server.Id, token).ConfigureAwait(false)).Route
            : await routeFactory(server.Id, token).ConfigureAwait(false);
        var ssh = state.Route.Client ?? throw new InvalidOperationException("Маршрут не предоставляет SSH-канал.");
        Emit(state, "", $"Подключено: {state.Route.Strategy}; локальный SSH-порт {state.Route.LocalSshPort}");
        var applicableTunnels = task.Tunnels.Where(x => BatchTunnelPolicy.AppliesTo(x, server.Id)).ToArray();
        if (applicableTunnels.Length > 0)
        {
            state.TunnelFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartTunnels(ssh, server, applicableTunnels, state.Tunnels, state.TunnelFault);
        }
        if (BatchTaskPolicy.NeedsSftp(task.Steps))
        {
            state.Sftp = connections.CreateSftpClient(server, state.Route);
            await state.Sftp.ConnectAsync(token).ConfigureAwait(false);
        }
        if (task.Steps.Any(x => x.Kind is BatchTaskStepKind.WaitForText or BatchTaskStepKind.InteractiveSend or BatchTaskStepKind.InteractiveWaitAndSend))
        {
            state.Shell = ssh.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
            state.ShellOutput = new();
            state.ShellSanitizer = new();
            state.ShellOutput.Output += text =>
            {
                var clean = state.ShellSanitizer.Feed(text);
                if (!string.IsNullOrWhiteSpace(clean)) Emit(state, "Интерактивная консоль", clean, source: "Консоль");
            };
            state.ShellReaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            state.ShellReader = PumpShellAsync(state.Shell, state.ShellOutput, state.ShellReaderCancellation.Token);
        }
        var cwd = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? "домашняя папка учётной записи" : task.WorkingDirectory;
        Emit(state, "Подготовка", "Рабочая папка задачи: " + cwd + "; у отдельных действий может быть своя папка");
        Emit(state, "Подготовка", task.PrivilegeMode == BatchPrivilegeMode.AlwaysBecome
            ? "Права: все команды выполняются через настроенный sudo/su"
            : "Права: учётная запись сессии; повышение только у отмеченных действий");
        if (task.Steps.Count > 1)
            Emit(state, "Подготовка",
                "Действия выполняются по порядку: сначала шаг 1 на всех подходящих серверах, затем шаг 2 и так далее. " +
                "Несколько команд пишите построчно внутри одного действия.");
        state.Connected = true;
    }

    private async Task ConnectWithRecoveryAsync(ManagerConfig config, BatchTaskDefinition task,
        ServerRunState state, string operation, CancellationToken token)
    {
        var waitMinutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(config.TaskConnectionRecoveryMinutes);
        while (true)
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(waitMinutes);
            Exception? last = null;
            do
            {
                token.ThrowIfCancellationRequested();
                CleanupConnection(state);
                try { await ConnectServerAsync(config, task, state, token).ConfigureAwait(false); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TaskConnectionRecoveryPolicy.IsConnectivityFailure(ex))
                {
                    last = ex;
                    Emit(state, operation, "Связь недоступна: " + SecretRedactor.Redact(ex.Message, state.Server, activeSecrets) +
                        $". Следующая попытка через {TaskConnectionRecoveryPolicy.RetryDelay.TotalSeconds:0} секунд.", BatchLogLevel.Warning);
                }
                if (DateTimeOffset.UtcNow >= deadline) break;
                var remaining = deadline - DateTimeOffset.UtcNow;
                await Task.Delay(remaining < TaskConnectionRecoveryPolicy.RetryDelay ? remaining : TaskConnectionRecoveryPolicy.RetryDelay, token).ConfigureAwait(false);
            } while (true);
            var extra = RecoveryTimeout is null ? null : await RecoveryTimeout(
                new(state.Server.Name, operation, waitMinutes), token).ConfigureAwait(false);
            if (extra is null) throw new OperationCanceledException("Ожидание восстановления связи остановлено пользователем.", last, token);
            waitMinutes = TaskConnectionRecoveryPolicy.NormalizeMinutes(extra.Value);
        }
    }

    private async Task ExecuteWithRecoveryAsync(ManagerConfig config, BatchTaskDefinition task, ServerRunState state,
        BatchTaskStep step, string root, string workingDirectory, bool become, string operation, CancellationToken token)
    {
        while (true)
        {
            try { await ExecuteAsync(state, step, root, workingDirectory, become, token).ConfigureAwait(false); return; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (TaskConnectionRecoveryPolicy.IsConnectivityFailure(ex))
            {
                Emit(state, operation, "Соединение потеряно; действие удерживается до восстановления связи.", BatchLogLevel.Warning);
                await ConnectWithRecoveryAsync(config, task, state, operation, token).ConfigureAwait(false);
                if (!TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(step.Kind))
                {
                    var retry = ConfirmAmbiguousRetry is not null && await ConfirmAmbiguousRetry(
                        new(state.Server.Name, operation), token).ConfigureAwait(false);
                    if (!retry)
                        throw new InvalidOperationException("Выполнение на сервере остановлено: пользователь отказался повторять неоднозначно завершившееся действие.", ex);
                }
                Emit(state, operation, "Связь восстановлена; действие запускается повторно.");
            }
        }
    }

    private async Task TunnelKeepAliveAsync(ManagerConfig config, BatchTaskDefinition task, ServerRunState state, CancellationToken token)
    {
        while (true)
        {
            try
            {
                var ssh = state.Route?.Client ?? throw new IOException("Нет SSH-соединения.");
                while (ssh.IsConnected && state.Tunnels.All(x => x.IsStarted) &&
                       state.TunnelFault is not null && !state.TunnelFault.Task.IsCompleted)
                {
                    await Task.WhenAny(Task.Delay(1000, token), state.TunnelFault.Task).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                if (state.TunnelFault?.Task.IsCompletedSuccessfully == true)
                    throw new IOException("Ошибка работающего SSH-туннеля.", state.TunnelFault.Task.Result);
                if (!task.AutoReconnectTunnels)
                    throw new IOException("SSH-соединение туннеля разорвано.");
                Emit(state, "Туннели", "Связь потеряна; повтор через 5 секунд", BatchLogLevel.Warning, "Туннель");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Emit(state, "Туннели", "Сбой: " + SecretRedactor.Redact(ex.Message, state.Server, activeSecrets),
                    BatchLogLevel.Warning, "Туннель");
            }
            CleanupConnection(state);
            try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                Emit(state, "", "Переподключение");
                state.Route = routeFactory is null
                    ? (await connections.ConnectBestAsync(config, state.Server.Id, token).ConfigureAwait(false)).Route
                    : await routeFactory(state.Server.Id, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Emit(state, "", "Переподключение не удалось: " +
                    SecretRedactor.Redact(ex.Message, state.Server, activeSecrets), BatchLogLevel.Warning);
                continue;
            }
            var reconnected = state.Route.Client!;
            state.TunnelFault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var definitions = task.Tunnels.Where(x => BatchTunnelPolicy.AppliesTo(x, state.Server.Id));
            StartTunnels(reconnected, state.Server, definitions, state.Tunnels, state.TunnelFault);
            Emit(state, "Туннели", "Восстановлены", source: "Туннель");
        }
    }

    private void CleanupConnection(ServerRunState state)
    {
        if (state.Route?.Client is { } ssh)
            foreach (var tunnel in state.Tunnels)
            {
                try { if (tunnel.IsStarted) tunnel.Stop(); } catch { }
                try { ssh.RemoveForwardedPort(tunnel); } catch { }
            }
        foreach (var tunnel in state.Tunnels) try { tunnel.Dispose(); } catch { }
        state.Tunnels.Clear();
        state.ShellReaderCancellation?.Cancel();
        state.Shell?.Dispose();
        if (state.ShellReader is not null)
            try { state.ShellReader.Wait(TimeSpan.FromSeconds(2)); } catch { }
        state.ShellReaderCancellation?.Dispose();
        var finalOutput = state.ShellSanitizer?.Complete();
        if (!string.IsNullOrWhiteSpace(finalOutput))
            Emit(state, "Интерактивная консоль", finalOutput, source: "Консоль");
        state.ShellOutput?.Dispose();
        state.Shell = null; state.ShellOutput = null; state.ShellSanitizer = null;
        state.ShellReader = null; state.ShellReaderCancellation = null;
        state.Sftp?.Dispose(); state.Sftp = null;
        if (state.Route is not null)
        {
            if (routeRelease is null) state.Route.Dispose();
            else routeRelease(state.Route);
            state.Route = null;
        }
        state.TunnelFault = null;
        state.Connected = false;
    }

    private void StartTunnels(SshClient ssh, ManagedServer server, IEnumerable<BatchTunnelDefinition> definitions,
        List<ForwardedPort> active, TaskCompletionSource<Exception> fault)
    {
        foreach (var definition in definitions)
        {
            var tunnel = BatchTunnelPolicy.Create(definition);
            tunnel.Exception += (_, args) =>
            {
                Emit(server, definition.Name, "Ошибка: " + args.Exception.Message, BatchLogLevel.Error, "Туннель");
                fault.TrySetResult(args.Exception);
            };
            ssh.AddForwardedPort(tunnel);
            active.Add(tunnel);
            Emit(server, definition.Name, "Подключается", source: "Туннель");
            tunnel.Start();
            Emit(server, definition.Name,
                $"Работает: {definition.BindHost}:{definition.BindPort} → {definition.DestinationHost}:{definition.DestinationPort}",
                source: "Туннель");
        }
    }

    private async Task ExecuteAsync(ServerRunState state, BatchTaskStep step,
        string root, string workingDirectory, bool become, CancellationToken token)
    {
        var ssh = state.Route!.Client!;
        var sftp = state.Sftp;
        var shell = state.Shell;
        var shellOutput = state.ShellOutput;
        var server = state.Server;
        var backups = state.Backups;
        var effectiveStep = ResolveFileStep(step, workingDirectory, become);
        switch (step.Kind)
        {
            case BatchTaskStepKind.Command:
                await RunCommandAsync(ssh, BuildTaskCommand(step.Command, workingDirectory, become, server, step.RunAsUser, step.RunAsPassword), step.Name, server, token, failOnNonZero: false);
                break;
            case BatchTaskStepKind.Check:
                var output = await RunCommandAsync(ssh, BuildTaskCommand(step.Command, workingDirectory, become, server, step.RunAsUser, step.RunAsPassword), step.Name, server, token);
                if (!string.IsNullOrEmpty(step.ExpectedText) && !output.Contains(step.ExpectedText, StringComparison.Ordinal))
                    throw new InvalidOperationException("Проверка не содержит ожидаемый текст.");
                break;
            case BatchTaskStepKind.WaitForText:
            {
                shellOutput!.BeginInteraction();
                shell!.WriteLine(BuildTaskCommand(step.Command, workingDirectory, become, server,
                    step.RunAsUser, step.RunAsPassword));
                shell.Flush();
                await shellOutput.WaitForAsync(step.ExpectedText,
                    TimeSpan.FromSeconds(step.TimeoutSeconds), token).ConfigureAwait(false);
                Emit(server, step.Name, $"Точный текст «{step.ExpectedText}» найден; ответ отправляется в ту же консоль",
                    source: "Консоль");
                shellOutput.BeginInteraction();
                shell.WriteLine(step.ActionCommand);
                var marker = BatchInteractiveCompletionMarker.Create();
                shell.WriteLine(BatchInteractiveCompletionMarker.BuildCommand(marker));
                shell.Flush();
                var markerTimeout = TimeSpan.FromSeconds(Math.Max(step.TimeoutSeconds, step.WaitAfterSeconds > 0 ? step.WaitAfterSeconds : step.TimeoutSeconds));
                try
                {
                    await shellOutput.WaitForAsync(marker, markerTimeout, token).ConfigureAwait(false);
                    Emit(server, step.Name, "Интерактивная программа завершилась", source: "Консоль");
                }
                catch (TimeoutException)
                {
                    Emit(server, step.Name,
                        $"Интерактивная программа продолжает выполняться в фоне (маркер завершения не получен за {markerTimeout.TotalSeconds:0} с)",
                        BatchLogLevel.Warning, source: "Консоль");
                }
                break;
            }
            case BatchTaskStepKind.InteractiveSend:
                shellOutput!.BeginInteraction();
                shell!.WriteLine(step.Command); shell.Flush(); break;
            case BatchTaskStepKind.InteractiveWaitAndSend:
            {
                string? completionMarker = null;
                if (!string.IsNullOrWhiteSpace(step.Command))
                {
                    completionMarker = BatchInteractiveCompletionMarker.Create();
                    var launch = BuildInteractiveTaskCommand(step, workingDirectory, become, server, completionMarker);
                    shellOutput!.BeginInteraction();
                    shell!.WriteLine(launch.Command);
                    shell.Flush();
                    if (launch.Password.Length > 0)
                    {
                        if (launch.PasswordPrompt.Length > 0)
                            await shellOutput.WaitForAsync(launch.PasswordPrompt,
                                TimeSpan.FromSeconds(step.TimeoutSeconds), token).ConfigureAwait(false);
                        shellOutput.BeginInteraction();
                        shell.WriteLine(launch.Password);
                        shell.Flush();
                    }
                }
                var expected = step.ExpectedText.Replace("\r\n", "\n").Split('\n');
                var replies = step.ActionCommand.Replace("\r\n", "\n").Split('\n');
                var blocks = Math.Max(expected.Length, replies.Length);
                for (var block = 0; block < blocks; block++)
                {
                    var waitText = block < expected.Length ? expected[block].Trim() : "";
                    var reply = block < replies.Length ? replies[block] : "";
                    if (waitText.Length > 0)
                        await shellOutput!.WaitForAsync(waitText, TimeSpan.FromSeconds(step.TimeoutSeconds), token).ConfigureAwait(false);
                    shellOutput!.BeginInteraction();
                    if (reply.Length > 0) { shell!.WriteLine(reply); shell.Flush(); }
                    Emit(server, step.Name,
                        $"Блок {block + 1}/{blocks}: {(waitText.Length > 0 ? $"текст «{waitText}» найден; " : "")}ответ отправлен в ту же консоль",
                        source: "Консоль");
                }
                if (completionMarker is not null)
                {
                    var markerTimeout = TimeSpan.FromSeconds(Math.Max(step.TimeoutSeconds, step.WaitAfterSeconds > 0 ? step.WaitAfterSeconds : step.TimeoutSeconds));
                    try
                    {
                        await shellOutput!.WaitForAsync(completionMarker, markerTimeout, token).ConfigureAwait(false);
                        Emit(server, step.Name, "Интерактивная программа завершилась", source: "Консоль");
                    }
                    catch (TimeoutException)
                    {
                        Emit(server, step.Name,
                            $"Интерактивная программа продолжает выполняться в фоне (маркер завершения не получен за {markerTimeout.TotalSeconds:0} с)",
                            BatchLogLevel.Warning, source: "Консоль");
                    }
                }
                break;
            }
            case BatchTaskStepKind.MakeDirectory:
                await RunCommandAsync(ssh, BuildTaskCommand("mkdir -p -- " + Sh(step.Destination), workingDirectory, become, server, step.RunAsUser, step.RunAsPassword), step.Name, server, token); break;
            case BatchTaskStepKind.Upload:
                await UploadAsync(ssh, sftp!, server, effectiveStep, root, backups, token); break;
            case BatchTaskStepKind.Download:
                await DownloadAsync(ssh, sftp!, server, effectiveStep, workingDirectory, backups, token); break;
            case BatchTaskStepKind.Delete:
                await RunCommandAsync(ssh, BuildDeleteCommand(step, workingDirectory, become, server), step.Name, server, token);
                Emit(server, step.Name, $"Удалено: {step.Destination}");
                break;
            case BatchTaskStepKind.ReplaceText:
                await ReplaceAsync(ssh, sftp!, server, effectiveStep, backups, token); break;
            case BatchTaskStepKind.Template:
                await ApplyTemplateAsync(ssh, sftp!, server, effectiveStep, root, backups, token); break;
        }
    }

    private async Task DownloadAsync(SshClient ssh, SftpClient sftp, ManagedServer server, BatchTaskStep step,
        string workingDirectory, List<string> backups, CancellationToken token)
    {
        var downloadsRoot = Path.Combine(AppContext.BaseDirectory, "Data", "Downloads");
        var serverFolder = Path.Combine(downloadsRoot, SafeFolderName($"{server.Name} {server.CleanHost}"));
        if (!string.IsNullOrWhiteSpace(step.DownloadFolder))
            serverFolder = Path.Combine(serverFolder, SafeFolderName(step.DownloadFolder.Trim()));
        Directory.CreateDirectory(serverFolder);
        var paths = step.Source.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) throw new InvalidDataException("Не заданы пути для скачивания.");
        foreach (var requestedPath in paths)
        {
            var remotePath = ResolveRemoteDownloadPath(requestedPath, workingDirectory);
            var parent = RemoteDir(remotePath);
            var name = Path.GetFileName(remotePath.TrimEnd('/'));
            if (name.Length == 0) throw new InvalidDataException($"Некорректный путь для скачивания: {remotePath}");
            if (!step.DownloadAsArchive)
            {
                var transferred = await DownloadDirectAsync(sftp, remotePath, serverFolder, step.Backup, backups, token).ConfigureAwait(false);
                Emit(server, step.Name, transferred == 0
                    ? $"Пропущено: файлы «{remotePath}» уже существуют в {serverFolder}"
                    : $"Скачано «{remotePath}» → {serverFolder}");
                continue;
            }
            var toolOutput = await RunCommandAsync(ssh, BuildDownloadArchiveToolProbe(step.Become, server),
                step.Name, server, token, emitOutput: false).ConfigureAwait(false);
            var format = SelectDownloadArchiveFormat(toolOutput);
            var remoteBase = $"{parent.TrimEnd('/')}/.kitty-manager-download-{Guid.NewGuid():N}";
            var remoteTemp = remoteBase + format.Extension;
            try
            {
                await RunCommandAsync(ssh,
                    BuildDownloadArchiveCommand(parent, name, remoteTemp, format, step.Become, server),
                    step.Name, server, token, emitOutput: false).ConfigureAwait(false);
                var localName = SafeFolderName(HasGlob(name) ? "download" : name);
                var localArchive = Path.Combine(serverFolder, localName + format.Extension);
                if (File.Exists(localArchive) && !step.Backup)
                    throw new IOException($"Файл уже существует: {localArchive}. Включите передачу с backup для замены архива.");
                if (File.Exists(localArchive))
                {
                    var backup = LocalBackupPath(localArchive);
                    File.Copy(localArchive, backup);
                    backups.Add(backup);
                }
                var localTemporary = localArchive + ".part-" + Guid.NewGuid().ToString("N");
                try
                {
                    await using (var fileStream = File.Create(localTemporary))
                        await sftp.DownloadFileAsync(remoteTemp, fileStream, token).ConfigureAwait(false);
                    File.Move(localTemporary, localArchive, true);
                }
                finally { try { File.Delete(localTemporary); } catch { } }
                Emit(server, step.Name, $"Архив «{remotePath}» ({format.DisplayName}, максимальное сжатие) → {localArchive}");
            }
            finally
            {
                try
                {
                    using var cleanup = ssh.CreateCommand(BuildDownloadArchiveCleanupCommand(remoteBase, step.Become, server));
                    cleanup.CommandTimeout = TimeSpan.FromSeconds(5);
                    cleanup.Execute();
                    if (cleanup.ExitStatus != 0)
                        throw new IOException($"команда очистки завершилась с кодом {cleanup.ExitStatus}: {cleanup.Error}");
                }
                catch (Exception commandError)
                {
                    try
                    {
                        if (sftp.IsConnected && sftp.Exists(remoteTemp)) sftp.DeleteFile(remoteTemp);
                    }
                    catch (Exception sftpError)
                    {
                        Emit(server, step.Name,
                            $"Предупреждение: временный архив «{remoteTemp}» не удалён: {commandError.Message}; {sftpError.Message}");
                    }
                }
            }
        }
    }

    private static async Task<int> DownloadDirectAsync(SftpClient sftp, string remotePath,
        string destinationDirectory, bool overwriteAndBackup, List<string> backups, CancellationToken token)
    {
        var matches = ExpandRemotePath(sftp, remotePath).ToArray();
        if (matches.Length == 0) throw new FileNotFoundException($"На сервере не найден путь: {remotePath}");
        var transferred = 0;
        foreach (var match in matches)
            transferred += await DownloadRemoteEntryAsync(sftp, match, destinationDirectory, overwriteAndBackup, backups, token).ConfigureAwait(false);
        return transferred;
    }

    private static IEnumerable<Renci.SshNet.Sftp.ISftpFile> ExpandRemotePath(SftpClient sftp, string remotePath)
    {
        if (!HasGlob(remotePath))
        {
            yield return sftp.Get(remotePath.TrimEnd('/'));
            yield break;
        }
        var parent = RemoteDir(remotePath);
        var pattern = Path.GetFileName(remotePath.TrimEnd('/'));
        var regex = GlobRegex(pattern);
        foreach (var item in sftp.ListDirectory(parent))
            if (item.Name is not "." and not ".." && regex.IsMatch(item.Name)) yield return item;
    }

    private static async Task<int> DownloadRemoteEntryAsync(SftpClient sftp, Renci.SshNet.Sftp.ISftpFile entry,
        string destinationDirectory, bool overwriteAndBackup, List<string> backups, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entry.IsDirectory)
        {
            var childDirectory = Path.Combine(destinationDirectory, SafeFolderName(entry.Name));
            Directory.CreateDirectory(childDirectory);
            var transferred = 0;
            foreach (var child in sftp.ListDirectory(entry.FullName))
                if (child.Name is not "." and not "..")
                    transferred += await DownloadRemoteEntryAsync(sftp, child, childDirectory, overwriteAndBackup, backups, token).ConfigureAwait(false);
            return transferred;
        }
        if (!entry.IsRegularFile) return 0;
        var localPath = Path.Combine(destinationDirectory, SafeFolderName(entry.Name));
        if (File.Exists(localPath) && !overwriteAndBackup)
        {
            await using var local = File.OpenRead(localPath);
            await using var remote = sftp.OpenRead(entry.FullName);
            if (await StreamsEqualAsync(local, remote, token).ConfigureAwait(false)) return 0;
            throw new IOException($"Файл уже существует и отличается: {localPath}. Включите передачу с backup для замены.");
        }
        if (File.Exists(localPath))
        {
            var backup = LocalBackupPath(localPath);
            File.Copy(localPath, backup);
            backups.Add(backup);
        }
        var temporary = localPath + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = File.Create(temporary))
                await sftp.DownloadFileAsync(entry.FullName, output, token).ConfigureAwait(false);
            File.Move(temporary, localPath, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
        return 1;
    }

    internal static string LocalBackupPath(string path) =>
        path + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

    internal static async Task<bool> StreamsEqualAsync(Stream first, Stream second, CancellationToken token)
    {
        if (first.CanSeek && second.CanSeek && first.Length != second.Length) return false;
        var left = new byte[81920];
        var right = new byte[left.Length];
        while (true)
        {
            var leftCount = await FillBufferAsync(first, left, token).ConfigureAwait(false);
            var rightCount = await FillBufferAsync(second, right, token).ConfigureAwait(false);
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;
            if (!left.AsSpan(0, leftCount).SequenceEqual(right.AsSpan(0, rightCount))) return false;
        }
    }

    private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static Regex GlobRegex(string pattern)
    {
        var value = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        value = Regex.Replace(value, @"\\\[([^]]+)\\\]", "[$1]");
        return new Regex("^" + value + "$", RegexOptions.CultureInvariant);
    }

    internal static BatchTaskStep ResolveFileStep(BatchTaskStep step, string workingDirectory, bool become)
    {
        var result = WithBecome(step, become);
        if (step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template or BatchTaskStepKind.ReplaceText)
            result.Destination = ResolveRemoteDownloadPath(step.Destination, workingDirectory);
        return result;
    }

    internal static string BuildDeleteCommand(BatchTaskStep step, string workingDirectory, bool become, ManagedServer server) =>
        BuildTaskCommand("rm -rf -- " + ShellPathPattern(step.Destination), workingDirectory, become, server,
            step.RunAsUser, step.RunAsPassword);

    internal static string BuildUploadDestinationCommand(string source, string destination) =>
        $"DEST={Sh(destination.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/")}; " +
        $"if [ -d \"$DEST\" ]; then DEST=\"${{DEST%/}}/\"{Sh(Path.GetFileName(source))}; fi; printf '%s' \"$DEST\"";

    // Only glob operators are shell syntax; every other character is literal.
    // Bracket ranges deliberately support ASCII letters/digits, _, - and !/^ negation.
    internal static string ShellPathPattern(string value)
    {
        if (!HasGlob(value)) return Sh(value);
        var result = new StringBuilder();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            result.Append(Sh(literal.ToString())); literal.Clear();
        }
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch is '*' or '?') { FlushLiteral(); result.Append(ch); }
            else if (ch == '[' && value.IndexOf(']', index + 1) is var end && end > index &&
                     Regex.IsMatch(value[index..(end + 1)], @"^\[[!^]?[A-Za-z0-9_-]+\]$"))
            {
                FlushLiteral(); result.Append(value[index..(end + 1)]);
                index = end;
            }
            else literal.Append(ch);
        }
        FlushLiteral();
        return result.Length == 0 ? Sh("") : result.ToString();
    }

    private static bool HasGlob(string value) => value.Contains('*') || value.Contains('?') || value.Contains('[');

    internal sealed record DownloadArchiveFormat(string Id, string Extension, string DisplayName);

    internal static string BuildDownloadArchiveToolProbe(bool become, ManagedServer server) => WrapCommand(
        "if command -v tar >/dev/null 2>&1 && command -v gzip >/dev/null 2>&1; then printf 'tar-gzip'; " +
        "elif command -v zip >/dev/null 2>&1; then printf 'zip'; " +
        "elif command -v tar >/dev/null 2>&1; then printf 'tar'; else echo 'Не найден tar/gzip/zip' >&2; exit 127; fi",
        become, server);

    internal static string BuildDownloadArchiveCleanupCommand(string remoteBase, bool become, ManagedServer server)
    {
        var paths = string.Join(' ', new[] { ".tar.gz", ".tar", ".zip" }.Select(x => Sh(remoteBase + x)));
        return WrapCommand($"rm -f -- {paths}", become, server);
    }

    internal static DownloadArchiveFormat SelectDownloadArchiveFormat(string probeOutput)
    {
        var id = probeOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(x => x is "tar-gzip" or "zip" or "tar");
        return id switch
        {
            "tar-gzip" => new("tar-gzip", ".tar.gz", "tar + gzip"),
            "zip" => new("zip", ".zip", "zip"),
            "tar" => new("tar", ".tar", "tar без сжатия"),
            _ => throw new InvalidOperationException("Сервер вернул неизвестный формат архивации.")
        };
    }

    internal static string BuildDownloadArchiveCommand(
        string parent, string name, string remoteTemp, DownloadArchiveFormat format, bool become, ManagedServer server)
    {
        var nameArg = ShellPathPattern(name);
        var command = format.Id switch
        {
            "tar-gzip" => $"tar -cf {Sh(remoteTemp[..^3])} -- {nameArg} && gzip -9 -f {Sh(remoteTemp[..^3])}",
            "zip" => $"zip -9 -r {Sh(remoteTemp)} -- {nameArg}",
            "tar" => $"tar -cf {Sh(remoteTemp)} -- {nameArg}",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return WrapCommand($"cd -- {Sh(parent)} && {command}", become, server);
    }

    private static string UniqueLocalPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(path)) return path;
        }
    }

    internal static string ResolveRemoteDownloadPath(string path, string workingDirectory)
    {
        var value = path.Trim();
        if (value.StartsWith('/') || string.IsNullOrWhiteSpace(workingDirectory)) return value;
        return workingDirectory.TrimEnd('/') + "/" + value;
    }

    internal static void ExtractDownloadArchive(Stream gzipArchive, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var tar = new GZipStream(gzipArchive, CompressionMode.Decompress, leaveOpen: true);
        TarFile.ExtractToDirectory(tar, destinationDirectory, overwriteFiles: true);
    }

    private static string SafeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        return result.Length == 0 ? "server" : result;
    }

    private static async Task PumpShellAsync(Stream shell, BatchInteractiveOutputCoordinator output, CancellationToken token)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[2048];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        while (true)
        {
            var count = await shell.ReadAsync(bytes, token).ConfigureAwait(false);
            if (count == 0) return;
            var charCount = decoder.GetChars(bytes, 0, count, chars, 0, false);
            if (charCount > 0) output.Feed(new string(chars, 0, charCount));
        }
    }

    private async Task<bool> ShouldRunStepAsync(ServerRunState state, BatchTaskStep step,
        BatchTaskDefinition task, CancellationToken token)
    {
        var ssh = state.Route!.Client!;
        var server = state.Server;
        var become = BatchTaskPolicy.EffectiveBecome(task.PrivilegeMode, step);
        var cwd = BatchTaskPolicy.EffectiveWorkingDirectory(task, step);
        var cmd = BuildTaskCommand(step.ConditionCommand, cwd, become, server, step.RunAsUser, step.RunAsPassword);
        string output;
        int? exitStatus;
        var pty = TryParseSuCommandWithPipedPassword(cmd, out var suCommand, out var password)
            ? await TryRunCommandPtyAsync(
                () => RunInteractivePtyCommandAsync(ssh, suCommand, password, step.Name, server, token, emitOutput: false),
                ex => Emit(state, step.Name, ex.Message + " Проверка условия через exec.", BatchLogLevel.Warning)).ConfigureAwait(false)
            : null;
        if (pty is { } result)
        {
            output = SecretRedactor.Redact(result.Output.Trim(), server, activeSecrets);
            exitStatus = result.ExitCode;
        }
        else
        {
            using var condition = ssh.CreateCommand(cmd);
            await condition.ExecuteAsync(token).ConfigureAwait(false);
            output = SecretRedactor.Redact((condition.Result + condition.Error).Trim(), server, activeSecrets);
            exitStatus = condition.ExitStatus;
        }
        if (output.Length > 0) Emit(state, step.Name, "Условие: " + output);
        return exitStatus == 0;
    }

    private async Task ApplyTemplateAsync(SshClient ssh, SftpClient sftp, ManagedServer server,
        BatchTaskStep step, string root, List<string> backups, CancellationToken token)
    {
        var source = ResolveTaskPath(root, step.Source);
        var temp = Path.GetTempFileName();
        try
        {
            var template = await File.ReadAllTextAsync(source, token);
            await File.WriteAllTextAsync(temp, BatchTemplateRenderer.Render(template, server), token);
            await UploadFileAsync(ssh, sftp, server, Clone(step, temp, step.Destination), backups, token);
        }
        finally { File.Delete(temp); }
    }

    private async Task UploadAsync(SshClient ssh, SftpClient sftp, ManagedServer server, BatchTaskStep step,
        string root, List<string> backups, CancellationToken token)
    {
        var sources = step.Source.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (sources.Length == 0) throw new InvalidDataException("Не заданы пути для загрузки.");
        foreach (var source in sources)
        {
            var local = ResolveTaskPath(root, source);
            if (Directory.Exists(local))
            {
                foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(local, file).Replace('\\', '/');
                    var child = Clone(step, file, step.Destination.TrimEnd('/') + "/" + relative);
                    await UploadFileAsync(ssh, sftp, server, child, backups, token);
                }
            }
            else await UploadFileAsync(ssh, sftp, server, Clone(step, local, step.Destination), backups, token);
        }
    }

    private async Task UploadFileAsync(SshClient ssh, SftpClient sftp, ManagedServer server, BatchTaskStep step,
        List<string> backups, CancellationToken token)
    {
        // Если назначение — существующий каталог, файл кладётся внутрь него.
        var resolve = BuildUploadDestinationCommand(step.Source, step.Destination);
        var destination = (await RunCommandAsync(ssh, WrapCommand(resolve, step.Become, server),
            step.Name, server, token, emitOutput: false)).Trim();
        if (destination.Length == 0) destination = step.Destination;
        const string existsMarker = "KITTY_MANAGER_DESTINATION_EXISTS";
        var exists = (await RunCommandAsync(ssh, WrapCommand(
            $"if [ -e {Sh(destination)} ]; then printf {Sh(existsMarker)}; fi", step.Become, server),
            step.Name, server, token, emitOutput: false)).Contains(existsMarker, StringComparison.Ordinal);
        if (exists && !step.Backup)
        {
            try
            {
                var identical = step.Become
                    ? await PrivilegedRemoteFileEqualsAsync(ssh, sftp, server, step, destination, token).ConfigureAwait(false)
                    : await SftpFileEqualsAsync(sftp, step.Source, destination, token).ConfigureAwait(false);
                if (identical)
                {
                    Emit(server, step.Name, $"Пропущено: идентичный файл уже существует — {destination}");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Файл уже существует, но сравнить его не удалось: {destination}. Включите передачу с backup для замены.", ex);
            }
            throw new IOException($"Файл уже существует и отличается: {destination}. Включите передачу с backup для замены.");
        }
        var destinationTemp = destination + ".kitty-manager.tmp." + Guid.NewGuid().ToString("N");
        var uploadTemp = step.Become
            ? "/tmp/kitty-manager." + Guid.NewGuid().ToString("N")
            : destinationTemp;
        await RunCommandAsync(ssh, WrapCommand("mkdir -p -- " + Sh(RemoteDir(destination)), step.Become, server), step.Name, server, token, emitOutput: false);
        try
        {
            if (!step.Become)
                await RunCommandAsync(ssh,
                    $"if [ -e {Sh(destination)} ]; then cp -p -- {Sh(destination)} {Sh(destinationTemp)}; fi",
                    step.Name, server, token, emitOutput: false);
            await using (var input = File.OpenRead(step.Source))
                await sftp.UploadFileAsync(input, uploadTemp, token).ConfigureAwait(false);
            if (step.Backup)
            {
                var backup = destination + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                const string marker = "KITTY_MANAGER_BACKUP_CREATED";
                var output = await RunCommandAsync(ssh, WrapCommand(
                    $"if [ -e {Sh(destination)} ]; then cp -p -- {Sh(destination)} {Sh(backup)} && printf {Sh(marker)}; fi",
                    step.Become, server), step.Name, server, token, emitOutput: false);
                if (output.Contains(marker, StringComparison.Ordinal)) backups.Add(backup);
            }
            var replace = step.Become
                ? $"if [ -e {Sh(destination)} ]; then cp -p -- {Sh(destination)} {Sh(destinationTemp)}; else : > {Sh(destinationTemp)}; fi && " +
                  $"cat -- {Sh(uploadTemp)} > {Sh(destinationTemp)} && mv -f -- {Sh(destinationTemp)} {Sh(destination)} && rm -f -- {Sh(uploadTemp)}"
                : $"mv -f -- {Sh(destinationTemp)} {Sh(destination)}";
            await RunCommandAsync(ssh, WrapCommand(replace, step.Become, server), step.Name, server, token, emitOutput: false);
        }
        finally
        {
            try
            {
                using var cleanup = ssh.CreateCommand(WrapCommand(
                    $"rm -f -- {Sh(uploadTemp)} {Sh(destinationTemp)}", step.Become, server));
                cleanup.CommandTimeout = TimeSpan.FromSeconds(5);
                cleanup.Execute();
            }
            catch { }
        }
    }

    private static async Task<bool> SftpFileEqualsAsync(SftpClient sftp, string localPath,
        string remotePath, CancellationToken token)
    {
        await using var local = File.OpenRead(localPath);
        await using var remote = sftp.OpenRead(remotePath);
        return await StreamsEqualAsync(local, remote, token).ConfigureAwait(false);
    }

    private async Task<bool> PrivilegedRemoteFileEqualsAsync(SshClient ssh, SftpClient sftp,
        ManagedServer server, BatchTaskStep step, string destination, CancellationToken token)
    {
        var staged = "/tmp/kitty-manager-compare-" + Guid.NewGuid().ToString("N");
        const string same = "KITTY_MANAGER_FILES_IDENTICAL";
        const string different = "KITTY_MANAGER_FILES_DIFFERENT";
        try
        {
            await using (var input = File.OpenRead(step.Source))
                await sftp.UploadFileAsync(input, staged, token).ConfigureAwait(false);
            var script =
                $"f1={Sh(staged)}; f2={Sh(destination)}; " +
                $"if command -v cmp >/dev/null 2>&1; then cmp -s -- \"$f1\" \"$f2\"; c=$?; " +
                $"if [ $c -eq 0 ]; then printf {Sh(same)}; elif [ $c -eq 1 ]; then printf {Sh(different)}; else exit $c; fi; " +
                $"elif command -v sha256sum >/dev/null 2>&1; then " +
                $"h1=$(sha256sum -- \"$f1\" | cut -d' ' -f1); h2=$(sha256sum -- \"$f2\" | cut -d' ' -f1); " +
                $"if [ -n \"$h1\" ] && [ \"$h1\" = \"$h2\" ]; then printf {Sh(same)}; else printf {Sh(different)}; fi; " +
                $"elif command -v md5sum >/dev/null 2>&1; then " +
                $"h1=$(md5sum -- \"$f1\" | cut -d' ' -f1); h2=$(md5sum -- \"$f2\" | cut -d' ' -f1); " +
                $"if [ -n \"$h1\" ] && [ \"$h1\" = \"$h2\" ]; then printf {Sh(same)}; else printf {Sh(different)}; fi; " +
                $"elif command -v cksum >/dev/null 2>&1; then " +
                $"h1=$(cksum -- \"$f1\" | cut -d' ' -f1,2); h2=$(cksum -- \"$f2\" | cut -d' ' -f1,2); " +
                $"if [ -n \"$h1\" ] && [ \"$h1\" = \"$h2\" ]; then printf {Sh(same)}; else printf {Sh(different)}; fi; " +
                $"elif command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then " +
                $"py=$(command -v python3 || command -v python); " +
                "\"$py\" -c 'import sys; sys.exit(0 if open(sys.argv[1], \"rb\").read() == open(sys.argv[2], \"rb\").read() else 1)' \"$f1\" \"$f2\"; c=$?; " +
                $"if [ $c -eq 0 ]; then printf {Sh(same)}; else printf {Sh(different)}; fi; " +
                $"else exit 127; fi";
            var output = await RunCommandAsync(ssh, WrapCommand(script, true, server),
                step.Name, server, token, emitOutput: false).ConfigureAwait(false);
            if (output.Contains(same, StringComparison.Ordinal)) return true;
            if (output.Contains(different, StringComparison.Ordinal)) return false;
            throw new IOException("Сервер не вернул результат сравнения файлов.");
        }
        finally
        {
            try { if (sftp.IsConnected && sftp.Exists(staged)) sftp.DeleteFile(staged); } catch { }
        }
    }

    private async Task ReplaceAsync(SshClient ssh, SftpClient sftp, ManagedServer server, BatchTaskStep step,
        List<string> backups, CancellationToken token)
    {
        var tempLocal = Path.GetTempFileName();
        try
        {
            string content;
            if (step.Become)
                content = await RunCommandAsync(ssh, WrapCommand("cat -- " + Sh(step.Destination), true, server),
                    step.Name, server, token, emitOutput: false);
            else
            {
                await using (var output = File.Create(tempLocal))
                {
                    await sftp.DownloadFileAsync(step.Destination, output, token).ConfigureAwait(false);
                    await output.FlushAsync(token);
                }
                content = await File.ReadAllTextAsync(tempLocal, token);
            }
            if (!content.Contains(step.Search, StringComparison.Ordinal)) throw new InvalidOperationException("Искомый текст не найден.");
            await File.WriteAllTextAsync(tempLocal, content.Replace(step.Search, step.Replacement, StringComparison.Ordinal), token);
            await UploadFileAsync(ssh, sftp, server, Clone(step, tempLocal, step.Destination), backups, token);
        }
        finally { File.Delete(tempLocal); }
    }

    private static string ToOctalEscape(string ascii) =>
        string.Concat(Encoding.ASCII.GetBytes(ascii).Select(b => $"\\{Convert.ToString(b, 8).PadLeft(3, '0')}"));

    internal static bool TryParseSuCommandWithPipedPassword(string command,
        [NotNullWhen(true)] out string? suCommand,
        [NotNullWhen(true)] out string? password)
    {
        suCommand = null;
        password = null;

        const string prefix = "printf '%s\\n' '";
        if (!command.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var i = prefix.Length;
        while (i < command.Length)
        {
            if (command[i] == '\'')
            {
                if (i + 4 < command.Length && command.Substring(i, 5) == "'\"'\"'")
                {
                    i += 5;
                    continue;
                }
                break;
            }
            i++;
        }

        if (i >= command.Length)
            return false;

        var quotedPass = command[prefix.Length..i];
        var remainder = command[(i + 1)..].TrimStart();
        if (!remainder.StartsWith("| su", StringComparison.Ordinal))
            return false;

        var suPart = remainder[1..].TrimStart();
        if (!suPart.StartsWith("su ", StringComparison.Ordinal) &&
            !suPart.StartsWith("su-", StringComparison.Ordinal) &&
            suPart != "su")
            return false;

        password = quotedPass.Replace("'\"'\"'", "'");
        suCommand = suPart;
        return true;
    }

    internal static async Task<(string Output, int ExitCode)?> TryRunCommandPtyAsync(
        Func<Task<(string Output, int ExitCode)>> run, Action<Exception> unavailable)
    {
        try { return await run().ConfigureAwait(false); }
        catch (PtyUnavailableException ex) { unavailable(ex); return null; }
    }

    internal sealed class PtyUnavailableException(Exception inner)
        : Exception("Не удалось создать SSH PTY до отправки команды.", inner);

    internal static T CreateCommandPty<T>(Func<T> create)
    {
        try { return create(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new PtyUnavailableException(ex); }
    }

    private async Task<(string Output, int ExitCode)> RunInteractivePtyCommandAsync(
        SshClient ssh,
        string suCommand,
        string password,
        string step,
        ManagedServer server,
        CancellationToken token,
        bool emitOutput = true,
        int timeoutSeconds = 0)
    {
        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>
        {
            { Renci.SshNet.Common.TerminalModes.ECHO, 0 }
        };
        using var shell = CreateCommandPty(() => ssh.CreateShellStream("xterm", 80, 24, 800, 600, 4096, modes));
        return await RunInteractivePtyCommandCoreAsync(
            shell,
            line => { shell.WriteLine(line); shell.Flush(); },
            suCommand,
            password,
            server,
            step,
            activeSecrets,
            (srv, stp, msg, lvl, src) => Emit(server, stp, msg, lvl, src ?? "Шаг"),
            token,
            emitOutput,
            timeoutSeconds).ConfigureAwait(false);
    }

    internal static async Task<(string Output, int ExitCode)> RunInteractivePtyCommandCoreAsync(
        Stream shell,
        Action<string> sendLine,
        string suCommand,
        string password,
        ManagedServer server,
        string step,
        string[] secrets,
        Action<string, string, string, BatchLogLevel, string?> emit,
        CancellationToken token,
        bool emitOutput = true,
        int timeoutSeconds = 0)
    {
        var startMarker = "__KITTY_START_" + Guid.NewGuid().ToString("N") + "__";
        var doneMarkerPrefix = "__KITTY_DONE_" + Guid.NewGuid().ToString("N") + "_";

        var startOctal = ToOctalEscape(startMarker);
        var doneOctal = ToOctalEscape(doneMarkerPrefix);

        var shellScript = $"export LC_ALL=C LANG=C 2>/dev/null; stty -echo 2>/dev/null; printf '%b\\n' '{startOctal}'; {suCommand}; printf '\\n%b%d\\n' '{doneOctal}' \"$?\"";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeoutSeconds > 0)
            linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var cancellationToken = linkedCts.Token;

        sendLine(shellScript);

        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[2048];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var lineAccumulator = new StringBuilder();
        var rawAccumulator = new StringBuilder();
        var stdoutBuilder = new StringBuilder();
        var capturedLines = new List<string>();

        var started = false;
        var passwordSent = false;
        var completed = false;
        var exitCode = -1;
        var stdoutLinesCount = 0;

        while (!completed)
        {
            int count;
            try
            {
                count = await shell.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"Команда через PTY не завершилась за {timeoutSeconds} с.");
            }

            if (count == 0)
                break;

            var charCount = decoder.GetChars(buffer, 0, count, chars, 0, false);
            if (charCount == 0)
                continue;

            for (var i = 0; i < charCount; i++)
            {
                var ch = chars[i];

                if (!started)
                {
                    rawAccumulator.Append(ch);
                    if (rawAccumulator.Length >= startMarker.Length)
                    {
                        var len = rawAccumulator.Length;
                        var mLen = startMarker.Length;
                        var match = true;
                        for (var k = 0; k < mLen; k++)
                        {
                            if (rawAccumulator[len - mLen + k] != startMarker[k])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                        {
                            started = true;
                            rawAccumulator.Clear();
                            lineAccumulator.Clear();
                        }
                    }
                    continue;
                }

                if (ch == '\r')
                    continue;

                if (ch == '\n')
                {
                    var rawLine = lineAccumulator.ToString();
                    lineAccumulator.Clear();

                    if (rawLine.Contains(doneMarkerPrefix, StringComparison.Ordinal))
                    {
                        var prefixIdx = rawLine.IndexOf(doneMarkerPrefix, StringComparison.Ordinal);
                        var ecStr = rawLine[(prefixIdx + doneMarkerPrefix.Length)..].TrimEnd('_', '\r', '\n', ' ');
                        if (int.TryParse(ecStr, out var parsedEc))
                        {
                            if (prefixIdx > 0)
                            {
                                var before = rawLine[..prefixIdx];
                                var sanitizedBefore = BatchTerminalOutputSanitizer.Sanitize(before);
                                if (!string.IsNullOrWhiteSpace(sanitizedBefore) &&
                                    !sanitizedBefore.Contains("password:", StringComparison.OrdinalIgnoreCase) &&
                                    !sanitizedBefore.Contains("пароль:", StringComparison.OrdinalIgnoreCase))
                                {
                                    capturedLines.Add(sanitizedBefore);
                                    stdoutBuilder.AppendLine(sanitizedBefore);
                                    Interlocked.Increment(ref stdoutLinesCount);
                                    var redactedBefore = SecretRedactor.Redact(sanitizedBefore, server, secrets);
                                    if (emitOutput && !string.IsNullOrWhiteSpace(redactedBefore))
                                        emit(server.Name, step, "Вывод команды: " + redactedBefore, BatchLogLevel.Info, null);
                                }
                            }
                            exitCode = parsedEc;
                            completed = true;
                            break;
                        }
                    }

                    var sanitized = BatchTerminalOutputSanitizer.Sanitize(rawLine);
                    if (string.IsNullOrWhiteSpace(sanitized))
                        continue;

                    if (sanitized.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                        sanitized.Contains("пароль:", StringComparison.OrdinalIgnoreCase) ||
                        sanitized.Contains("password for", StringComparison.OrdinalIgnoreCase) ||
                        sanitized.Contains("пароль для", StringComparison.OrdinalIgnoreCase))
                        continue;

                    capturedLines.Add(sanitized);
                    stdoutBuilder.AppendLine(sanitized);
                    Interlocked.Increment(ref stdoutLinesCount);

                    var redacted = SecretRedactor.Redact(sanitized, server, secrets);
                    if (emitOutput && !string.IsNullOrWhiteSpace(redacted))
                        emit(server.Name, step, "Вывод команды: " + redacted, BatchLogLevel.Info, null);
                }
                else
                {
                    lineAccumulator.Append(ch);

                    if (!passwordSent && password.Length > 0)
                    {
                        var currentText = lineAccumulator.ToString();
                        if (currentText.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                            currentText.Contains("пароль:", StringComparison.OrdinalIgnoreCase) ||
                            currentText.Contains("password for", StringComparison.OrdinalIgnoreCase) ||
                            currentText.Contains("пароль для", StringComparison.OrdinalIgnoreCase))
                        {
                            passwordSent = true;
                            lineAccumulator.Clear();
                            sendLine(password);
                            continue;
                        }
                    }
                }
            }
        }

        if (!completed && lineAccumulator.Length > 0)
        {
            var remaining = lineAccumulator.ToString();
            if (remaining.Contains(doneMarkerPrefix, StringComparison.Ordinal))
            {
                var prefixIdx = remaining.IndexOf(doneMarkerPrefix, StringComparison.Ordinal);
                var ecStr = remaining[(prefixIdx + doneMarkerPrefix.Length)..].TrimEnd('_', '\r', '\n', ' ');
                if (int.TryParse(ecStr, out var parsedEc))
                {
                    exitCode = parsedEc;
                    completed = true;
                }
            }
        }

        if (!completed)
            throw new InvalidOperationException("Сессия SSH PTY завершилась до получения маркера завершения команды.");

        var rawStdout = stdoutBuilder.ToString();
        var stdout = SecretRedactor.Redact(rawStdout.Trim(), server, secrets);

        if (emitOutput && stdoutLinesCount == 0 && stdout.Length == 0)
            emit(server.Name, step, $"Команда завершилась без вывода (код {exitCode})", BatchLogLevel.Info, null);

        return (rawStdout, exitCode);
    }

    private async Task<string> RunCommandAsync(SshClient ssh, string command, string step, ManagedServer server,
        CancellationToken token, bool emitOutput = true, bool failOnNonZero = true)
    {
        if (TryParseSuCommandWithPipedPassword(command, out var suCommand, out var password))
        {
            var ptyResult = await TryRunCommandPtyAsync(
                () => RunInteractivePtyCommandAsync(ssh, suCommand, password, step, server, token, emitOutput),
                ex => Emit(server, step, ex.Message + " Выполнение через exec.", BatchLogLevel.Warning)).ConfigureAwait(false);

            if (ptyResult != null)
            {
                var (ptyRawStdout, ptyExitCode) = ptyResult.Value;
                var ptyStdout = SecretRedactor.Redact(ptyRawStdout.Trim(), server, activeSecrets);
                if (ptyExitCode != 0)
                {
                    if (IsCommandResultFatal(ptyExitCode, ptyStdout, stderr: "", failOnNonZero))
                    {
                        if (string.IsNullOrEmpty(ptyStdout))
                            throw new InvalidOperationException($"Команда завершилась с кодом {ptyExitCode} без вывода на сервере.");
                        throw new InvalidOperationException($"Команда завершилась с кодом {ptyExitCode}: {ClarifyCommandError(ptyStdout)}");
                    }
                    Emit(server, step, $"Команда завершилась с кодом {ptyExitCode} (не считается ошибкой)", BatchLogLevel.Warning);
                }
                return ptyRawStdout;
            }
        }

        using var result = ssh.CreateCommand(command);
        var execTask = result.ExecuteAsync(token);
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutLinesCount = 0;
        var stderrLinesCount = 0;

        void OnStdout(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var redacted = SecretRedactor.Redact(line, server, activeSecrets);
            if (string.IsNullOrWhiteSpace(redacted)) return;
            Interlocked.Increment(ref stdoutLinesCount);
            Emit(server, step, "Вывод команды: " + redacted);
        }

        void OnStderr(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var redacted = SecretRedactor.Redact(line, server, activeSecrets);
            if (string.IsNullOrWhiteSpace(redacted)) return;
            Interlocked.Increment(ref stderrLinesCount);
            Emit(server, step, "Вывод ошибок: " + redacted, BatchLogLevel.Warning);
        }

        var stdoutTask = ConsumeStreamLinesAsync(result.OutputStream, stdoutBuilder, emitOutput ? OnStdout : null, token);
        var stderrTask = ConsumeStreamLinesAsync(result.ExtendedOutputStream, stderrBuilder, emitOutput ? OnStderr : null, token);

        try
        {
            await execTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        var rawStdout = stdoutBuilder.ToString();
        var rawStderr = stderrBuilder.ToString();
        var stdout = SecretRedactor.Redact(rawStdout.Trim(), server, activeSecrets);
        var stderr = SecretRedactor.Redact(rawStderr.Trim(), server, activeSecrets);

        if (emitOutput && stdoutLinesCount == 0 && stderrLinesCount == 0)
        {
            if (stdout.Length == 0 && stderr.Length == 0)
                Emit(server, step, $"Команда завершилась без вывода (код {result.ExitStatus})");
        }

        if (result.ExitStatus != 0)
        {
            if (!failOnNonZero)
            {
                if (IsCommandResultFatal(result.ExitStatus, stdout, stderr, failOnNonZero: false))
                    throw new InvalidOperationException($"Команда завершилась с кодом {result.ExitStatus} без вывода на сервере.");
                Emit(server, step, $"Команда завершилась с кодом {result.ExitStatus} (не считается ошибкой)", BatchLogLevel.Warning);
                return rawStdout;
            }
            throw new InvalidOperationException($"Команда завершилась с кодом {result.ExitStatus}: {ClarifyCommandError((stdout + " " + stderr).Trim())}");
        }
        return rawStdout;
    }

    public static async Task ConsumeStreamLinesAsync(Stream stream, StringBuilder? rawBuffer,
        Action<string>? onLine, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 2048, leaveOpen: true);
        var chars = new char[2048];
        var lineBuffer = new StringBuilder();
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(chars.AsMemory(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }
            if (read <= 0) break;

            if (rawBuffer is not null)
            {
                lock (rawBuffer) rawBuffer.Append(chars, 0, read);
            }

            if (onLine is not null)
            {
                for (var i = 0; i < read; i++)
                {
                    var ch = chars[i];
                    if (ch == '\r') continue;
                    if (ch == '\n')
                    {
                        var line = lineBuffer.ToString();
                        lineBuffer.Clear();
                        onLine(line);
                    }
                    else
                    {
                        lineBuffer.Append(ch);
                        if (lineBuffer.Length >= 4000)
                        {
                            var line = lineBuffer.ToString();
                            lineBuffer.Clear();
                            onLine(line);
                        }
                    }
                }
            }
        }

        if (onLine is not null && lineBuffer.Length > 0)
        {
            var line = lineBuffer.ToString();
            lineBuffer.Clear();
            onLine(line);
        }
    }

    public static bool IsCommandResultFatal(int? exitStatus, string stdout, string stderr, bool failOnNonZero = false)
    {
        if (exitStatus is 0) return false;
        if (!failOnNonZero)
            return string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr);
        return true;
    }

    private static string WrapCommand(string command, bool become, ManagedServer server)
    {
        if (!become) return command;
        var configured = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin)
            ?? throw new InvalidOperationException("Для сессии не настроен sudo/su-переход.");
        var privileged = RemoteCommandBridge.BuildPrivilegedCommand(command, configured, server.RootPassword.Length > 0);
        return server.RootPassword.Length == 0
            ? privileged
            : $"printf '%s\\n' {Sh(server.RootPassword)} | {privileged}";
    }
    public static string BuildTaskCommand(string command, string workingDirectory, bool become, ManagedServer server,
        string runAsUser = "", string runAsPassword = "")
    {
        var effective = string.IsNullOrWhiteSpace(workingDirectory)
            ? command
            : BuildInWorkingDirectory(command, workingDirectory.Trim());
        if (!string.IsNullOrWhiteSpace(runAsUser))
            return WrapRunAs(effective, runAsUser.Trim(), runAsPassword);
        return WrapCommand(effective, become, server);
    }

    // Проверяем рабочую папку до cd: иначе ошибка выглядела бы как cryptic-вывод
    // оболочки и её легко принять за проблему с правами или su. [ -d ] ложен и
    // при отсутствии папки, и когда она недоступна эффективному пользователю.
    internal static string BuildInWorkingDirectory(string command, string directory) =>
        $"if [ -d {Sh(directory)} ]; then cd -- {Sh(directory)} && {command}; " +
        $"else printf '%s\\n' {Sh($"Рабочая папка отсутствует или недоступна на сервере: {directory}")} >&2; exit 126; fi";

    internal static string ClarifyCommandError(string detail) =>
        detail.Contains("must be run from a terminal", StringComparison.Ordinal)
            ? detail + " Подсказка: su на этом сервере требует терминал для ввода пароля; " +
              "настройте переход через sudo (например, «sudo -i») или беспарольный su."
            : detail;

    internal sealed record InteractiveLaunchPlan(string Command, string PasswordPrompt, string Password);

    internal static InteractiveLaunchPlan BuildInteractiveTaskCommand(BatchTaskStep step, string workingDirectory,
        bool become, ManagedServer server, string? completionMarker = null)
    {
        var baseCmd = string.IsNullOrWhiteSpace(workingDirectory)
            ? step.Command
            : $"cd -- {Sh(workingDirectory.Trim())} && {step.Command}";
        var command = string.IsNullOrWhiteSpace(completionMarker)
            ? baseCmd
            : baseCmd + "; " + BatchInteractiveCompletionMarker.BuildCommand(completionMarker);
        if (!string.IsNullOrWhiteSpace(step.RunAsUser))
            return step.RunAsPassword.Length > 0
                ? new($"su - {Sh(step.RunAsUser.Trim())} -c {Sh(command)}", "Password:", step.RunAsPassword)
                : new($"sudo -n -u {Sh(step.RunAsUser.Trim())} -- sh -c {Sh(command)}", "", "");
        if (!become) return new(command, "", "");
        var configured = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin)
            ?? throw new InvalidOperationException("Для сессии не настроен sudo/su-переход.");
        var quoted = Sh(command);
        if (configured.StartsWith("sudo", StringComparison.OrdinalIgnoreCase))
        {
            const string prompt = "__KITTY_MANAGER_SUDO_PASSWORD__";
            var sudo = configured.ToLowerInvariant() switch
            {
                "sudo -i" => $"sudo -k -S -p {Sh(prompt)} -i sh -c {quoted}",
                "sudo su" or "sudo su -" => $"sudo -k -S -p {Sh(prompt)} su - -c {quoted}",
                _ => $"sudo -k -S -p {Sh(prompt)} sh -c {quoted}"
            };
            return new(sudo, server.RootPassword.Length > 0 ? prompt : "", server.RootPassword);
        }
        return new(configured.Trim().Equals("su -", StringComparison.OrdinalIgnoreCase)
                ? $"su - -c {quoted}" : $"su -c {quoted}",
            server.RootPassword.Length > 0 ? "Password:" : "", server.RootPassword);
    }

    private static string WrapRunAs(string command, string user, string password)
    {
        var inner = $"su - {Sh(user)} -c {Sh(command)}";
        return password.Length > 0
            ? $"printf '%s\\n' {Sh(password)} | {inner}"
            : $"sudo -n -u {Sh(user)} -- sh -c {Sh(command)}";
    }
    private static string DescribeStep(BatchTaskStep step, string workingDirectory, bool become)
    {
        var command = step.Kind switch
        {
            BatchTaskStepKind.Command or BatchTaskStepKind.Check or BatchTaskStepKind.InteractiveSend => step.Command,
            BatchTaskStepKind.WaitForText => step.Command,
            BatchTaskStepKind.InteractiveWaitAndSend => "ожидание текста и отправка ответа",
            _ => step.Summary
        };
        var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? "домашняя папка" : workingDirectory;
        return $"{command} (папка: {cwd}; права: {(become ? "повышенные" : "обычные")})";
    }
    private static string Sh(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static string ResolveTaskPath(string root, string source)
    {
        if (Path.IsPathFullyQualified(source)) return Path.GetFullPath(source);
        var local = Path.GetFullPath(Path.Combine(root, source));
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var allowed = rootPath + Path.DirectorySeparatorChar;
        if (!string.Equals(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), rootPath,
                StringComparison.OrdinalIgnoreCase) && !local.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Локальный путь выходит за папку задачи.");
        return local;
    }
    private static string RemoteDir(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "." : i == 0 ? "/" : path[..i];
    }
    private static BatchTaskStep Clone(BatchTaskStep s, string source, string destination) => new()
    {
        Kind = s.Kind, Name = s.Name, Source = source, Destination = destination, Backup = s.Backup,
        Become = s.Become, ConditionCommand = s.ConditionCommand, ActionCommand = s.ActionCommand,
        TimeoutSeconds = s.TimeoutSeconds, ExpectedText = s.ExpectedText,
        DownloadFolder = s.DownloadFolder, DownloadAsArchive = s.DownloadAsArchive, WaitForExit = s.WaitForExit
    };
    private static BatchTaskStep WithBecome(BatchTaskStep s, bool become) => new()
    {
        Kind = s.Kind, Name = s.Name, Command = s.Command, Source = s.Source, Destination = s.Destination,
        Search = s.Search, Replacement = s.Replacement, ExpectedText = s.ExpectedText,
        ConditionCommand = s.ConditionCommand, ActionCommand = s.ActionCommand, TimeoutSeconds = s.TimeoutSeconds,
        Become = become, RunAsUser = s.RunAsUser, RunAsPassword = s.RunAsPassword,
        Backup = s.Backup, ContinueOnError = s.ContinueOnError,
        WaitAfterSeconds = s.WaitAfterSeconds, DownloadFolder = s.DownloadFolder,
        DownloadAsArchive = s.DownloadAsArchive, WaitForExit = s.WaitForExit,
        ServerIds = s.ServerIds, WorkingDirectory = s.WorkingDirectory
    };
    private void Emit(ManagedServer server, string step, string message, BatchLogLevel level = BatchLogLevel.Info,
        string source = "Шаг") => Log?.Invoke(new(DateTimeOffset.Now, server.Id, server.Name, step,
            SecretRedactor.Redact(message, server, activeSecrets), level, source));
    private void Emit(ServerRunState state, string step, string message, BatchLogLevel level = BatchLogLevel.Info,
        string source = "Шаг") => Emit(state.Server, step, message, level, source);
}

public static class BatchOutputTrigger
{
    public static bool Contains(string output, string expectedText) =>
        !string.IsNullOrEmpty(expectedText) && output.Contains(expectedText, StringComparison.Ordinal);
}

public sealed class BatchOutputMatcher
{
    private readonly string expected;
    private readonly bool isPasswordPrompt;
    private string tail = "";
    public BatchOutputMatcher(string expectedText)
    {
        expected = string.IsNullOrEmpty(expectedText) ? throw new ArgumentException("Ожидаемый текст пуст.", nameof(expectedText)) : expectedText;
        isPasswordPrompt = expected.Equals("Password:", StringComparison.OrdinalIgnoreCase) ||
                           expected.Equals("assword", StringComparison.OrdinalIgnoreCase);
    }

    public bool Append(string chunk)
    {
        lock (this)
        {
            var combined = tail + chunk;
            if (combined.Contains(expected, StringComparison.Ordinal)) return true;
            if (isPasswordPrompt)
            {
                if (combined.Contains("Password:", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("Пароль:", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("пароль", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            tail = combined.Length < expected.Length - 1 ? combined : combined[^Math.Max(0, expected.Length - 1)..];
            return false;
        }
    }
}

public sealed class BatchInteractiveOutputCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly List<(BatchOutputMatcher Matcher, TaskCompletionSource Completion)> waiters = [];
    private string buffered = "";
    private bool disposed;
    public event Action<string>? Output;

    public void Feed(string text)
    {
        List<TaskCompletionSource> matches = [];
        lock (sync)
        {
            if (disposed) return;
            buffered = (buffered + text);
            if (buffered.Length > 65536) buffered = buffered[^65536..];
            foreach (var waiter in waiters)
                if (waiter.Matcher.Append(text)) matches.Add(waiter.Completion);
            waiters.RemoveAll(x => matches.Contains(x.Completion));
        }
        Output?.Invoke(text);
        foreach (var match in matches) match.TrySetResult();
    }

    public void BeginInteraction()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            buffered = "";
        }
    }

    public async Task WaitForAsync(string expectedText, TimeSpan timeout, CancellationToken token, string? timeoutDescription = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var matcher = new BatchOutputMatcher(expectedText);
            if (matcher.Append(buffered)) completion.TrySetResult();
            else waiters.Add((matcher, completion));
        }
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);
            await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var detail = timeoutDescription ?? "ожидаемый текст не появился";
            throw new TimeoutException($"За {(int)timeout.TotalSeconds} с {detail}.");
        }
        finally
        {
            lock (sync) waiters.RemoveAll(x => ReferenceEquals(x.Completion, completion));
        }
    }

    public void Dispose()
    {
        TaskCompletionSource[] pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = waiters.Select(x => x.Completion).ToArray();
            waiters.Clear();
        }
        foreach (var item in pending) item.TrySetException(new ObjectDisposedException(nameof(BatchInteractiveOutputCoordinator)));
    }
}

public static class BatchTaskTemplateStore
{
    public static string GetDirectory(string applicationDirectory) =>
        Path.Combine(Path.GetFullPath(applicationDirectory), "Data", "Tasks");

    public static string GetAnsibleDirectory(string applicationDirectory) =>
        Path.Combine(GetDirectory(applicationDirectory), "Ansible");

    public static void MigrateLegacyDirectory(string applicationDirectory)
    {
        var data = Path.Combine(Path.GetFullPath(applicationDirectory), "Data");
        var legacy = Path.Combine(data, "Templates");
        var current = Path.Combine(data, "Tasks");
        if (!Directory.Exists(legacy)) return;
        if (!Directory.Exists(current)) { Directory.Move(legacy, current); return; }
        foreach (var source in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(current, Path.GetRelativePath(legacy, source));
            if (File.Exists(target)) target = LegacyConflictPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
        }
        if (!Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).Any())
            Directory.Delete(legacy, true);
    }

    private static string LegacyConflictPath(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} (legacy {index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static IReadOnlyList<string> List(string applicationDirectory)
    {
        MigrateLegacyDirectory(applicationDirectory);
        var directory = GetDirectory(applicationDirectory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.kmtask", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string Save(string applicationDirectory, string taskDirectory, BatchTaskDefinition task, bool includeFiles = true)
    {
        MigrateLegacyDirectory(applicationDirectory);
        var directory = GetDirectory(applicationDirectory);
        Directory.CreateDirectory(directory);
        var safeName = SanitizeName(task.Name);
        var manifest = Path.Combine(taskDirectory, "task.yaml");
        BatchTaskFile.Save(manifest, task);
        var package = Path.Combine(directory, safeName + ".kmtask");
        if (includeFiles) BatchTaskFile.ExportPackageWithFiles(taskDirectory, task, package);
        else BatchTaskFile.ExportPackageWithoutFiles(taskDirectory, package);
        return package;
    }

    public static void Delete(string applicationDirectory, string fileName)
    {
        var path = Resolve(applicationDirectory, fileName);
        if (File.Exists(path)) File.Delete(path);
    }

    public static string SafeName(string name) => SanitizeName(name);

    public static string Rename(string applicationDirectory, string currentPath, string newName)
    {
        var directory = GetDirectory(applicationDirectory);
        var target = Path.Combine(directory, SanitizeName(newName) + ".kmtask");
        if (string.Equals(Path.GetFullPath(currentPath), target, StringComparison.OrdinalIgnoreCase)) return target;
        if (File.Exists(target)) throw new InvalidDataException($"Шаблон «{newName.Trim()}» уже существует.");
        // Имя задачи внутри пакета обновляется вместе с именем файла.
        var temp = Path.Combine(Path.GetTempPath(), "KiTTYManager", "rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = BatchTaskFile.ImportPackage(currentPath, temp);
            var task = BatchTaskFile.Load(manifest);
            task.Name = newName.Trim();
            BatchTaskFile.Save(manifest, task);
            BatchTaskFile.ExportPackage(temp, target);
            File.Delete(currentPath);
            return target;
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    public static string Resolve(string applicationDirectory, string fileName)
    {
        var directory = GetDirectory(applicationDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, Path.GetFileName(fileName)));
        if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Недопустимое имя шаблона.");
        return path;
    }

    private static string SanitizeName(string name)
    {
        var value = string.Concat(name.Trim().Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(value) ? "task" : value;
    }
}

public static class SecretRedactor
{
    public static string[] Secrets(ManagerConfig config) => config.AllServers()
        .SelectMany(server => new[] { server.Password, server.PrivateKeyPassphrase, server.RootPassword }
            .Concat(server.WebInterfaces.Select(x => x.Password)))
        .Concat(config.BaseProxies.Select(x => x.TotpSecret))
        .Where(value => !string.IsNullOrEmpty(value)).Distinct().OrderByDescending(value => value.Length).ToArray();

    public static string Redact(string text, ManagedServer server, IEnumerable<string>? additionalSecrets = null)
    {
        foreach (var secret in new[] { server.Password, server.PrivateKeyPassphrase, server.RootPassword }
                     .Concat(server.WebInterfaces.Select(x => x.Password))
                     .Concat(additionalSecrets ?? [])
                     .Where(value => !string.IsNullOrEmpty(value)).Distinct().OrderByDescending(value => value.Length))
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}

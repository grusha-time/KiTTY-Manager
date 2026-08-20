using System.Text.Json;

namespace KiTTYManager.Core;

public enum JumphostConsoleKind
{
    Entry,
    Access
}

public sealed record JumphostConsoleRecord(
    Guid ProxyId, JumphostConsoleKind Kind, int ProcessId, DateTime StartTimeUtc,
    string Title, string Host, int Port);

/// <summary>
/// Небольшой файл-«блокнот» в папке Data: какие консоли KiTTY открыл менеджер.
/// Позволяет после перезапуска узнать свои консоли и не открывать дубли.
/// Повреждённый или отсутствующий файл означает пустой список.
/// </summary>
public static class JumphostConsoleStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static IReadOnlyList<JumphostConsoleRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var payload = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path));
            return payload?.Consoles?
                .Where(item => item is not null && item.ProcessId > 0)
                .Select(item => new JumphostConsoleRecord(
                    item!.ProxyId, item.Kind, item.ProcessId,
                    DateTime.SpecifyKind(item.StartTimeUtc, DateTimeKind.Utc),
                    item.Title ?? "", item.Host ?? "", item.Port))
                .ToList() ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static void Save(string path, IReadOnlyList<JumphostConsoleRecord> records)
    {
        var payload = new StoredFile
        {
            Consoles = records.Select(record => new StoredConsole
            {
                ProxyId = record.ProxyId,
                Kind = record.Kind,
                ProcessId = record.ProcessId,
                StartTimeUtc = record.StartTimeUtc,
                Title = record.Title,
                Host = record.Host,
                Port = record.Port
            }).ToList()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    private sealed class StoredFile
    {
        public List<StoredConsole> Consoles { get; set; } = [];
    }

    public sealed class StoredConsole
    {
        public Guid ProxyId { get; set; }
        public JumphostConsoleKind Kind { get; set; }
        public int ProcessId { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public string Title { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
    }
}

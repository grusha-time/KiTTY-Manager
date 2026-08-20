namespace KiTTYManager.Core;

public static class RuntimeLogWriter
{
    private static readonly object AppendLock = new();

    public static string DailyPath(string directory, DateTimeOffset timestamp) =>
        Path.Combine(directory, $"manager-{timestamp:yyyyMMdd}.log");

    public static bool TryAppend(string path, string text, out string? error)
    {
        try
        {
            lock (AppendLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}");
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

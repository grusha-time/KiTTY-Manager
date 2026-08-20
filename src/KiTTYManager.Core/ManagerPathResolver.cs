namespace KiTTYManager.Core;

/// <summary>Resolves portable manager paths without changing their configured value.</summary>
public static class ManagerPathResolver
{
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException($"Некорректный путь: {path}", ex);
        }
    }

    /// <summary>
    /// Resolves an optional file. An empty value is valid; a configured missing
    /// file fails with the exact path that was checked.
    /// </summary>
    public static string? ResolveOptionalFile(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var resolved = Resolve(path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                $"{description} не найден. Проверьте путь: {resolved}", resolved);
        return resolved;
    }
}

using System.IO.Compression;

namespace KiTTYManager.Core;

public sealed record PackageManifestItem(
    string RelativePath,
    long Size,
    bool PreserveIfTargetExists = false);

public static class AppUpdatePackage
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static UpdateValidationResult ValidateArchive(string zipFilePath)
    {
        if (!File.Exists(zipFilePath))
            return new UpdateValidationResult(false, $"Файл обновления не найден: {zipFilePath}");

        try
        {
            using var fileStream = File.OpenRead(zipFilePath);
            return ValidateArchive(fileStream);
        }
        catch (InvalidDataException)
        {
            return new UpdateValidationResult(false, "Архив обновления повреждён или имеет неверный формат.");
        }
        catch (Exception ex)
        {
            return new UpdateValidationResult(false, $"Ошибка проверки архива обновления: {ex.Message}");
        }
    }

    public static UpdateValidationResult ValidateArchive(Stream zipStream)
    {
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = new List<string>();
            var hasExecutable = false;

            foreach (var entry in archive.Entries)
            {
                var rawName = entry.FullName;
                if (string.IsNullOrWhiteSpace(rawName)) continue;

                // Ignore directory-only entries
                if (rawName.EndsWith('/') || rawName.EndsWith('\\')) continue;

                if (!TryNormalizeAndValidatePath(rawName, out var normalizedPath, out var error))
                    return new UpdateValidationResult(false, error);

                if (string.Equals(normalizedPath, "KiTTYManager.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length == 0)
                        return new UpdateValidationResult(false, "Файл KiTTYManager.exe в архиве имеет нулевой размер.");
                    hasExecutable = true;
                }

                manifest.Add(normalizedPath);
            }

            if (!hasExecutable)
                return new UpdateValidationResult(false, "Архив обновления не содержит исполняемый файл KiTTYManager.exe в корне.");

            if (manifest.Count == 0)
                return new UpdateValidationResult(false, "Архив обновления пуст.");

            return new UpdateValidationResult(true, null, manifest);
        }
        catch (InvalidDataException)
        {
            return new UpdateValidationResult(false, "Архив обновления повреждён или не является допустимым ZIP-файлом.");
        }
        catch (Exception ex)
        {
            return new UpdateValidationResult(false, $"Ошибка чтения архива обновления: {ex.Message}");
        }
    }

    public static bool TryNormalizeAndValidatePath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Путь записи архива пуст.";
            return false;
        }

        // Reject absolute paths, drive letters, UNC paths
        if (rawPath.StartsWith('/') || rawPath.StartsWith('\\'))
        {
            error = $"Абсолютные пути в архиве запрещены: {rawPath}";
            return false;
        }

        if (rawPath.Length >= 2 && rawPath[1] == ':')
        {
            error = $"Пути с указанием диска в архиве запрещены: {rawPath}";
            return false;
        }

        if (rawPath.Contains(':'))
        {
            error = $"Альтернативные потоки данных (ADS) в архиве запрещены: {rawPath}";
            return false;
        }

        var segments = rawPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = $"Недопустимый путь записи: {rawPath}";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                error = $"Попытка выхода за пределы каталога (path traversal) запрещена: {rawPath}";
                return false;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                error = $"Запрещены сегменты пути с замыкающими точками или пробелами: {rawPath}";
                return false;
            }

            var baseName = Path.GetFileNameWithoutExtension(segment);
            if (ReservedDeviceNames.Contains(baseName) || ReservedDeviceNames.Contains(segment))
            {
                error = $"Использование зарезервированных системных имён устройств запрещено: {rawPath}";
                return false;
            }
        }

        var normalized = string.Join('/', segments);

        // Security check: Data/ is strictly inviolable (check both raw and alias-stripped segments)
        var cleanSegments = segments.Select(s => s.TrimEnd('.', ' ')).ToArray();
        var cleanNormalized = string.Join('/', cleanSegments);

        if (cleanNormalized.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
            cleanNormalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Архив содержит запрещённую запись в каталог Data/: {rawPath}";
            return false;
        }

        // Security check: KiTTY/SshHostKeys/ and KiTTY/Sessions/ are inviolable
        if (cleanNormalized.Equals("KiTTY/SshHostKeys", StringComparison.OrdinalIgnoreCase) ||
            cleanNormalized.StartsWith("KiTTY/SshHostKeys/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Архив содержит запрещённую запись в каталог KiTTY/SshHostKeys/: {rawPath}";
            return false;
        }

        if (cleanNormalized.Equals("KiTTY/Sessions", StringComparison.OrdinalIgnoreCase) ||
            cleanNormalized.StartsWith("KiTTY/Sessions/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Архив содержит запрещённую запись в каталог KiTTY/Sessions/: {rawPath}";
            return false;
        }

        normalizedPath = normalized;
        return true;
    }

    public static IReadOnlyList<PackageManifestItem> BuildManifest(string stagingDirectory)
    {
        var items = new List<PackageManifestItem>();
        var directoryInfo = new DirectoryInfo(stagingDirectory);
        if (!directoryInfo.Exists) return items;

        foreach (var file in directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(stagingDirectory, file.FullName).Replace('\\', '/');
            if (string.Equals(relPath, "manifest.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryNormalizeAndValidatePath(relPath, out var validRel, out var error))
                throw new InvalidOperationException($"Файл в каталоге подготовки нарушает правила безопасности: {relPath} ({error})");

            var preserveIfTargetExists = string.Equals(validRel, "KiTTY/kitty.ini", StringComparison.OrdinalIgnoreCase);
            items.Add(new PackageManifestItem(validRel, file.Length, preserveIfTargetExists));
        }

        return items;
    }

    public static void ExtractArchiveSafely(string zipFilePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        using var fileStream = File.OpenRead(zipFilePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var rawName = entry.FullName;
            if (string.IsNullOrWhiteSpace(rawName)) continue;
            if (rawName.EndsWith('/') || rawName.EndsWith('\\')) continue;

            if (!TryNormalizeAndValidatePath(rawName, out var normalizedPath, out var error))
                throw new InvalidOperationException(error ?? $"Недопустимый путь в архиве: {rawName}");

            var targetFilePath = Path.Combine(destinationDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            entry.ExtractToFile(targetFilePath, overwrite: true);
        }
    }

    public static bool ApplyUpdateWithRollback(
        string stagingDirectory,
        string targetDirectory,
        string backupDirectory,
        IReadOnlyList<PackageManifestItem> manifest,
        out string? failureReason)
    {
        failureReason = null;
        Directory.CreateDirectory(backupDirectory);

        var backedUpFiles = new List<(string TargetPath, string BackupPath)>();
        var createdFiles = new List<string>();

        try
        {
            foreach (var item in manifest)
            {
                if (!TryNormalizeAndValidatePath(item.RelativePath, out var validRel, out var pathErr))
                    throw new InvalidOperationException($"Запрещённый путь в манифесте обновления: {item.RelativePath} ({pathErr})");

                var targetFile = Path.Combine(targetDirectory, validRel.Replace('/', Path.DirectorySeparatorChar));
                var stagedFile = Path.Combine(stagingDirectory, validRel.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(stagedFile))
                    throw new FileNotFoundException($"Файл обновления отсутствует в каталоге подготовки: {item.RelativePath}", stagedFile);

                // Preserve existing file if policy specifies (e.g. KiTTY/kitty.ini)
                if (item.PreserveIfTargetExists && File.Exists(targetFile))
                    continue;

                // Back up existing file before replacement
                if (File.Exists(targetFile))
                {
                    var backupFile = Path.Combine(backupDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(targetFile, backupFile, overwrite: true);
                    backedUpFiles.Add((targetFile, backupFile));
                }
                else
                {
                    createdFiles.Add(targetFile);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(stagedFile, targetFile, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Сбой при копировании файлов обновления: {ex.Message}";

            // Roll back
            try
            {
                // Delete newly created files
                foreach (var created in createdFiles)
                {
                    try { if (File.Exists(created)) File.Delete(created); }
                    catch { /* best effort */ }
                }

                // Restore backed up files
                foreach (var (target, backup) in backedUpFiles)
                {
                    try { File.Copy(backup, target, overwrite: true); }
                    catch { /* best effort */ }
                }
            }
            catch (Exception rollbackEx)
            {
                failureReason += $"; Ошибка отката: {rollbackEx.Message}";
            }

            return false;
        }
    }
}

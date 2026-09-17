using System.Diagnostics;
using System.IO;
using System.Text;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public sealed class UpdateExecutionSession
{
    private readonly Process? helperProcess;

    public string UpdateRoot { get; }
    public string SignalFile { get; }
    public string DisarmFile { get; }
    public string LogFile { get; }
    public bool Disarmed { get; private set; }

    public UpdateExecutionSession(string updateRoot, string signalFile, string disarmFile, string logFile, Process? helperProcess = null)
    {
        UpdateRoot = updateRoot;
        SignalFile = signalFile;
        DisarmFile = disarmFile;
        LogFile = logFile;
        this.helperProcess = helperProcess;
    }

    public bool IsHelperAlive()
    {
        if (helperProcess is null) return true;
        try
        {
            return !helperProcess.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public bool Commit()
    {
        if (Disarmed) return false;
        if (helperProcess is not null)
        {
            try
            {
                if (helperProcess.HasExited)
                    return false;
            }
            catch { }
        }

        try
        {
            if (!File.Exists(SignalFile))
                File.WriteAllText(SignalFile, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Disarm()
    {
        if (Disarmed) return;
        Disarmed = true;
        try
        {
            if (!File.Exists(DisarmFile))
                File.WriteAllText(DisarmFile, DateTime.UtcNow.ToString("O"));
        }
        catch { /* best effort */ }
    }
}

public static class AppUpdateInstaller
{
    public static UpdateExecutionSession PrepareAndLaunchHelper(string zipFilePath, string targetDirectory)
    {
        var targetDir = Path.GetFullPath(targetDirectory);
        var validation = AppUpdatePackage.ValidateArchive(zipFilePath);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.ErrorMessage ?? "Архив обновления не прошёл валидацию.");

        // Check target directory write permissions
        CheckWriteAccess(targetDir);

        // Check available disk space (require at least 2.5x zip size free)
        var zipInfo = new FileInfo(zipFilePath);
        CheckDiskSpace(targetDir, zipInfo.Length * 3);

        var updateRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager-Update-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(updateRoot, "staged");
        var backupDir = Path.Combine(updateRoot, "backup");
        var signalFile = Path.Combine(updateRoot, "commit.signal");
        var disarmFile = Path.Combine(updateRoot, "disarm.signal");
        var logFile = Path.Combine(updateRoot, "update.log");

        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(backupDir);

        // Extract files safely to staging directory
        AppUpdatePackage.ExtractArchiveSafely(zipFilePath, stagingDir);

        var manifest = AppUpdatePackage.BuildManifest(stagingDir);
        if (manifest.Count == 0)
            throw new InvalidOperationException("Каталог подготовки обновления пуст после распаковки.");

        // Write manifest.txt for updater script
        var manifestPath = Path.Combine(stagingDir, "manifest.txt");
        var sb = new StringBuilder();
        foreach (var item in manifest)
        {
            sb.AppendLine($"{item.RelativePath}|{(item.PreserveIfTargetExists ? "1" : "0")}");
        }
        File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(false));

        // Write update.ps1
        var psScriptPath = Path.Combine(updateRoot, "update.ps1");
        File.WriteAllText(psScriptPath, GeneratePowerShellScript(), new UTF8Encoding(false));

        // Write update.cmd wrapper
        var cmdPath = Path.Combine(updateRoot, "update.cmd");
        var cmdContent = $"@echo off\r\nchcp 65001 >nul\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psScriptPath}\" -TargetDir \"{targetDir}\" -StagingDir \"{stagingDir}\" -BackupDir \"{backupDir}\" -SignalFile \"{signalFile}\" -DisarmFile \"{disarmFile}\" -ParentPid {Environment.ProcessId} -LogFile \"{logFile}\"\r\n";
        File.WriteAllText(cmdPath, cmdContent, Encoding.ASCII);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-WindowStyle", "Hidden",
                "-File", psScriptPath,
                "-TargetDir", targetDir,
                "-StagingDir", stagingDir,
                "-BackupDir", backupDir,
                "-SignalFile", signalFile,
                "-DisarmFile", disarmFile,
                "-ParentPid", Environment.ProcessId.ToString(),
                "-LogFile", logFile
            },
            CreateNoWindow = true,
            UseShellExecute = false
        };

        Process? helperProcess = null;
        try
        {
            helperProcess = Process.Start(psi);
            if (helperProcess is null)
                throw new InvalidOperationException("Не удалось запустить системную службу обновления (PowerShell).");
        }
        catch (Exception ex)
        {
            // Try fallback via cmd wrapper
            try
            {
                var cmdPsi = new ProcessStartInfo(cmdPath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                helperProcess = Process.Start(cmdPsi);
            }
            catch
            {
                throw new InvalidOperationException($"Не удалось запустить процесс обновления: {ex.Message}", ex);
            }
        }

        return new UpdateExecutionSession(updateRoot, signalFile, disarmFile, logFile, helperProcess);
    }

    private static void CheckWriteAccess(string targetDirectory)
    {
        var testFile = Path.Combine(targetDirectory, ".km_write_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException($"Нет прав на запись в каталог установки программы:\n{targetDirectory}\n\nЗапустите приложение от имени администратора или проверьте права доступа.", ex);
        }
    }

    private static void CheckDiskSpace(string targetDirectory, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetDirectory));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                {
                    var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                    var reqMb = requiredBytes / (1024 * 1024);
                    throw new IOException($"Недостаточно свободного места на диске {root}. Требуется: ~{reqMb} МБ, доступно: {freeMb} МБ.");
                }
            }
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // Best effort on unsupported filesystems
        }
    }

    internal static string GeneratePowerShellScript()
    {
        return """
        param(
            [string]$TargetDir,
            [string]$StagingDir,
            [string]$BackupDir,
            [string]$SignalFile,
            [string]$DisarmFile,
            [int]$ParentPid,
            [string]$LogFile
        )

        $ErrorActionPreference = 'Stop'

        function Log([string]$msg) {
            $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
            Add-Content -LiteralPath $LogFile -Value "[$ts] $msg" -ErrorAction SilentlyContinue
        }

        Log "Служба обновления запущена. PID: $ParentPid, Target: $TargetDir"

        # 1. Ожидание сигнала подтверждения (commit.signal) или отмены (disarm.signal)
        # Цикл ожидания синхронизирован с активностью родительского процесса
        while (!(Test-Path -LiteralPath $SignalFile)) {
            if (Test-Path -LiteralPath $DisarmFile) {
                Log "Обновление отменено пользователем (disarm). Завершение работы помощника."
                exit 0
            }

            $parentProc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if (!$parentProc -or $parentProc.HasExited) {
                Start-Sleep -Seconds 2
                if (Test-Path -LiteralPath $SignalFile) {
                    break
                }
                Log "Родительский процесс $ParentPid завершился без подтверждения обновления (commit). Прерывание."
                exit 0
            }

            Start-Sleep -Milliseconds 500
        }

        Log "Сигнал подтверждения получен. Ожидание завершения процесса KiTTY Manager ($ParentPid)..."

        # 2. Ожидание выхода родительского процесса (до 45 секунд)
        try {
            $proc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if ($proc) {
                $exited = $proc.WaitForExit(45000)
                if (!$exited) {
                    Log "Родительский процесс не завершился вовремя. Прерывание обновления."
                    exit 1
                }
            }
        } catch {
            Log "Процесс $ParentPid уже завершён."
        }

        # Пауза для освобождения файловых дескрипторов ОС
        Start-Sleep -Seconds 2

        # 3. Чтение манифеста и копирование файлов с бэкапом
        $manifestFile = Join-Path $StagingDir "manifest.txt"
        if (!(Test-Path -LiteralPath $manifestFile)) {
            Log "Манифест manifest.txt отсутствует. Прерывание."
            exit 1
        }

        if (!(Test-Path -LiteralPath $BackupDir)) {
            [System.IO.Directory]::CreateDirectory($BackupDir) | Out-Null
        }

        $lines = Get-Content -LiteralPath $manifestFile -ErrorAction Stop
        $createdFiles = @()
        $backedUpFiles = @()
        $success = $true

        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $parts = $line.Split('|')
            $relPath = $parts[0]
            $preserve = ($parts[1] -eq "1")

            # Защита от попыток записи в защищённые каталоги и обхода путей
            $normalizedCheck = $relPath.Replace('\', '/').TrimStart('/')
            if ($normalizedCheck -match '^(?i)(Data(\.| )?(/|$)|KiTTY/SshHostKeys(\.| )?(/|$)|KiTTY/Sessions(\.| )?(/|$))') {
                Log "ОШИБКА БЕЗОПАСНОСТИ: Попытка записи в защищённый каталог: $relPath. Прерывание."
                $success = $false
                break
            }

            $targetFile = Join-Path $TargetDir $relPath
            $stagedFile = Join-Path $StagingDir $relPath

            if ($preserve -and (Test-Path -LiteralPath $targetFile)) {
                Log "Сохранён существующий файл: $relPath"
                continue
            }

            try {
                if (Test-Path -LiteralPath $targetFile) {
                    $backupFile = Join-Path $BackupDir $relPath
                    $backupParent = Split-Path -Parent $backupFile
                    if (!(Test-Path -LiteralPath $backupParent)) {
                        [System.IO.Directory]::CreateDirectory($backupParent) | Out-Null
                    }
                    Copy-Item -LiteralPath $targetFile -Destination $backupFile -Force -ErrorAction Stop
                    $backedUpFiles += @{ Target = $targetFile; Backup = $backupFile }
                } else {
                    $createdFiles += $targetFile
                }

                $targetParent = Split-Path -Parent $targetFile
                if (!(Test-Path -LiteralPath $targetParent)) {
                    [System.IO.Directory]::CreateDirectory($targetParent) | Out-Null
                }
                Copy-Item -LiteralPath $stagedFile -Destination $targetFile -Force -ErrorAction Stop
            } catch {
                Log "Ошибка копирования $relPath : $_"
                $success = $false
                break
            }
        }

        if (!$success) {
            Log "ВНИМАНИЕ: Ошибка применения обновления. Выполняется откат изменений к исходному состоянию..."
            foreach ($created in $createdFiles) {
                try {
                    if (Test-Path -LiteralPath $created) {
                        Remove-Item -LiteralPath $created -Force -Recurse -ErrorAction SilentlyContinue
                        Log "Откат: удалён добавленный файл: $created"
                    }
                } catch {
                    Log "Откат: не удалось удалить $created : $_"
                }
            }
            foreach ($b in $backedUpFiles) {
                try {
                    Copy-Item -LiteralPath $b.Backup -Destination $b.Target -Force -ErrorAction Stop
                    Log "Откат: восстановлен исходный файл: $($b.Target)"
                } catch {
                    Log "Откат: ошибка восстановления $($b.Target) : $_"
                }
            }
            Log "Откат завершён. Приложение не перезапущено."
            exit 1
        }

        Log "Все файлы обновления успешно применены."

        # 4. Перезапуск обновлённого приложения
        $targetExe = Join-Path $TargetDir "KiTTYManager.exe"
        if (Test-Path -LiteralPath $targetExe) {
            Log "Запуск обновлённого $targetExe"
            Start-Process -FilePath $targetExe -WorkingDirectory $TargetDir
        }

        exit 0
        """;
    }
}

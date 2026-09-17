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
        var targetDir = Path.GetFullPath(targetDirectory).TrimEnd('\\', '/');
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
        File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(true));

        // Write update.ps1 with UTF-8 BOM for Windows PowerShell 5.1 compatibility
        var psScriptPath = Path.Combine(updateRoot, "update.ps1");
        File.WriteAllText(psScriptPath, GeneratePowerShellScript(), new UTF8Encoding(true));

        // Write update.cmd wrapper
        var cmdPath = Path.Combine(updateRoot, "update.cmd");
        var cmdContent = $"@echo off\r\nchcp 65001 >nul\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psScriptPath}\" -TargetDir \"{targetDir}\" -StagingDir \"{stagingDir}\" -BackupDir \"{backupDir}\" -SignalFile \"{signalFile}\" -DisarmFile \"{disarmFile}\" -ParentPid {Environment.ProcessId} -LogFile \"{logFile}\" -ZipFile \"{zipFilePath}\"\r\n";
        File.WriteAllText(cmdPath, cmdContent, new UTF8Encoding(false));

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
                "-LogFile", logFile,
                "-ZipFile", zipFilePath
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
            [string]$LogFile,
            [string]$ZipFile = ""
        )

        $ErrorActionPreference = 'Stop'

        function Log([string]$msg) {
            $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
            Add-Content -LiteralPath $LogFile -Value "[$ts] $msg" -ErrorAction SilentlyContinue
        }

        Log "Update service started. PID: $ParentPid, Target: $TargetDir"

        # 1. Wait for commit.signal or disarm.signal
        while (!(Test-Path -LiteralPath $SignalFile)) {
            if (Test-Path -LiteralPath $DisarmFile) {
                Log "Update cancelled by user (disarm). Helper exiting."
                exit 0
            }

            $parentProc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if (!$parentProc -or $parentProc.HasExited) {
                Start-Sleep -Seconds 2
                if (Test-Path -LiteralPath $SignalFile) {
                    break
                }
                Log "Parent process $ParentPid exited without commit signal. Aborting."
                exit 0
            }

            Start-Sleep -Milliseconds 500
        }

        Log "Commit signal received. Waiting for KiTTY Manager process ($ParentPid) to exit..."

        # 2. Wait for parent process exit (up to 45 seconds)
        try {
            $proc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if ($proc) {
                $exited = $proc.WaitForExit(45000)
                if (!$exited) {
                    Log "Parent process did not exit within timeout. Aborting update."
                    exit 1
                }
            }
        } catch {
            Log "Parent process $ParentPid already terminated."
        }

        # Pause to let OS release file handles
        Start-Sleep -Seconds 2

        # 3. Read manifest and copy files with backup
        $manifestFile = Join-Path $StagingDir "manifest.txt"
        if (!(Test-Path -LiteralPath $manifestFile)) {
            Log "Manifest manifest.txt missing. Aborting."
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

            # Security check: protect Data/, KiTTY/SshHostKeys/, KiTTY/Sessions/
            $normalizedCheck = $relPath.Replace('\', '/').TrimStart('/')
            if ($normalizedCheck -match '^(?i)(Data(\.| )?(/|$)|KiTTY/SshHostKeys(\.| )?(/|$)|KiTTY/Sessions(\.| )?(/|$))') {
                Log "SECURITY ERROR: Attempted write to protected directory: $relPath. Aborting."
                $success = $false
                break
            }

            $targetFile = Join-Path $TargetDir $relPath
            $stagedFile = Join-Path $StagingDir $relPath

            if ($preserve -and (Test-Path -LiteralPath $targetFile)) {
                Log "Preserved existing file: $relPath"
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
                Log "Error copying $relPath : $_"
                $success = $false
                break
            }
        }

        if (!$success) {
            Log "WARNING: Update failed. Rolling back changes to original state..."
            foreach ($created in $createdFiles) {
                try {
                    if (Test-Path -LiteralPath $created) {
                        Remove-Item -LiteralPath $created -Force -Recurse -ErrorAction SilentlyContinue
                        Log "Rollback: removed added file: $created"
                    }
                } catch {
                    Log "Rollback: failed to remove $created : $_"
                }
            }
            foreach ($b in $backedUpFiles) {
                try {
                    Copy-Item -LiteralPath $b.Backup -Destination $b.Target -Force -ErrorAction Stop
                    Log "Rollback: restored original file: $($b.Target)"
                } catch {
                    Log "Rollback: error restoring $($b.Target) : $_"
                }
            }
            Log "Rollback complete. Application not restarted."
            exit 1
        }

        Log "All update files successfully applied."

        # 4. Restart updated application
        $targetExe = Join-Path $TargetDir "KiTTYManager.exe"
        if (Test-Path -LiteralPath $targetExe) {
            Log "Starting updated $targetExe"
            Start-Process -FilePath $targetExe -WorkingDirectory $TargetDir
        }

        # 5. Cleanup temporary staging, backup, and downloaded archive
        try {
            if (Test-Path -LiteralPath $StagingDir) {
                Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $BackupDir) {
                Remove-Item -LiteralPath $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (![string]::IsNullOrWhiteSpace($ZipFile) -and (Test-Path -LiteralPath $ZipFile)) {
                Remove-Item -LiteralPath $ZipFile -Force -ErrorAction SilentlyContinue
            }
        } catch { /* best effort */ }

        # 6. Schedule delayed removal of update directory after helper exits
        $updateRoot = Split-Path -Parent $StagingDir
        if (Test-Path -LiteralPath $updateRoot) {
            Start-Process -FilePath "cmd.exe" -ArgumentList "/c timeout /t 3 /nobreak >nul & rd /s /q `"$updateRoot`"" -WindowStyle Hidden
        }

        exit 0
        """;
    }

    public static void CleanupOldUpdateArtifacts()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var updateDirs = Directory.GetDirectories(tempPath, "KiTTYManager-Update-*");
            foreach (var dir in updateDirs)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (DateTime.UtcNow - dirInfo.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch { /* best effort */ }
            }

            var downloadDir = Path.Combine(tempPath, "KiTTYManager-Updates");
            if (Directory.Exists(downloadDir))
            {
                foreach (var file in Directory.GetFiles(downloadDir, "*.zip"))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                        {
                            File.Delete(file);
                        }
                    }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort */ }
    }
}

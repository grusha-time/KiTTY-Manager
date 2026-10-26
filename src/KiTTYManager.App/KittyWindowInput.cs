using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KiTTYManager.App;

internal static class KittyWindowInput
{
    private const uint WmChar = 0x0102;

    public static async Task SendLineAsync(
        Process process, string value, CancellationToken cancellationToken)
    {
        nint handle = 0;
        for (var attempt = 0; attempt < 50 && handle == 0; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("Окно KiTTY закрылось до ввода TOTP.");
            process.Refresh();
            handle = process.MainWindowHandle;
            if (handle == 0) await Task.Delay(100, cancellationToken);
        }
        if (handle == 0) throw new InvalidOperationException("Не найдено окно KiTTY для ввода TOTP.");

        // The first keyboard-interactive answer is supplied by -pass. Input
        // queued here is consumed by the following TOTP prompt.
        foreach (var character in value)
            if (!PostMessage(handle, WmChar, character, 0))
                throw new InvalidOperationException("KiTTY не принял ввод TOTP.");
        if (!PostMessage(handle, WmChar, '\r', 0))
            throw new InvalidOperationException("KiTTY не принял ввод TOTP.");
    }

    internal static Func<Process, string, CancellationToken, Task> SendLineAction = SendLineAsync;

    public static bool IsActivePrompt(string logContent, string prompt)
    {
        if (string.IsNullOrWhiteSpace(logContent) || string.IsNullOrWhiteSpace(prompt))
            return false;

        // Strip ANSI terminal escape sequences (OSC, DCS/APC/PM, CSI, 2-char escapes, BEL)
        var clean = System.Text.RegularExpressions.Regex.Replace(
            logContent,
            @"\x1B\][\s\S]*?(?:\x07|\x1B\\|\x9C|$)|" +
            @"\x1B[P^_][\s\S]*?(?:\x07|\x1B\\|\x9C|$)|" +
            @"\x1B\[[0-?]*[ -/]*[@-~]|" +
            @"\x1B[@-Z\\-_]|\x07",
            "");

        // An active terminal challenge prompt waiting for input is at the end of the buffer
        // and is not yet followed by a newline (\n or \r).
        if (clean.EndsWith('\n') || clean.EndsWith('\r'))
            return false;

        var lastNewline = Math.Max(clean.LastIndexOf('\n'), clean.LastIndexOf('\r'));
        var activeLine = lastNewline >= 0 ? clean[(lastNewline + 1)..] : clean;

        return activeLine.Contains(prompt.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsActiveShellPrompt(string logContent, string? customShellPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(logContent))
            return false;

        // Strip ANSI terminal escape sequences (OSC, DCS/APC/PM, CSI, 2-char escapes, BEL)
        var clean = System.Text.RegularExpressions.Regex.Replace(
            logContent,
            @"\x1B\][\s\S]*?(?:\x07|\x1B\\|\x9C|$)|" +
            @"\x1B[P^_][\s\S]*?(?:\x07|\x1B\\|\x9C|$)|" +
            @"\x1B\[[0-?]*[ -/]*[@-~]|" +
            @"\x1B[@-Z\\-_]|\x07",
            "");

        if (clean.EndsWith('\n') || clean.EndsWith('\r'))
            return false;

        var lastNewline = Math.Max(clean.LastIndexOf('\n'), clean.LastIndexOf('\r'));
        var activeLine = (lastNewline >= 0 ? clean[(lastNewline + 1)..] : clean).TrimEnd();
        if (activeLine.Length == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(customShellPrompt) &&
            activeLine.EndsWith(customShellPrompt.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (activeLine.EndsWith('$') || activeLine.EndsWith('#') || activeLine.EndsWith('>') || activeLine.EndsWith('%'))
            return true;

        return false;
    }

    public static Task<int> SendLineAfterPromptAsync(
        Process process, string logPath, string prompt, string value,
        TimeSpan timeout, CancellationToken cancellationToken,
        Func<Process, string, CancellationToken, Task>? sendLine = null,
        int minOffset = 0,
        Func<string, bool>? progressionCheck = null) =>
        SendLineAfterPromptAsync(process, logPath, prompt, () => value, timeout, cancellationToken, sendLine, minOffset, progressionCheck);

    public static async Task<int> SendLineAfterPromptAsync(
        Process process, string logPath, string prompt, Func<string> valueFactory,
        TimeSpan timeout, CancellationToken cancellationToken,
        Func<Process, string, CancellationToken, Task>? sendLine = null,
        int minOffset = 0,
        Func<string, bool>? progressionCheck = null)
    {
        var result = await SendLineAfterPromptAsync(
            process, logPath, prompt, valueFactory, timeout, cancellationToken,
            optional: false, sendLine: sendLine, minOffset: minOffset,
            progressionCheck: progressionCheck, completionCheck: null);
        return result.Offset;
    }

    public static Task<(int Offset, bool Sent)> SendLineAfterPromptAsync(
        Process process, string logPath, string prompt, string value,
        TimeSpan timeout, CancellationToken cancellationToken,
        bool optional,
        Func<Process, string, CancellationToken, Task>? sendLine = null,
        int minOffset = 0,
        Func<string, bool>? progressionCheck = null,
        Func<string, bool>? completionCheck = null) =>
        SendLineAfterPromptAsync(process, logPath, prompt, () => value, timeout, cancellationToken, optional, sendLine, minOffset, progressionCheck, completionCheck);

    public static async Task<(int Offset, bool Sent)> SendLineAfterPromptAsync(
        Process process, string logPath, string prompt, Func<string> valueFactory,
        TimeSpan timeout, CancellationToken cancellationToken,
        bool optional,
        Func<Process, string, CancellationToken, Task>? sendLine = null,
        int minOffset = 0,
        Func<string, bool>? progressionCheck = null,
        Func<string, bool>? completionCheck = null)
    {
        var send = sendLine ?? SendLineAction;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                if (optional) return (minOffset, false);
                throw new InvalidOperationException("Окно KiTTY закрылось до подтверждения запроса OTP.");
            }

            if (File.Exists(logPath))
            {
                try
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync(cancellationToken);
                    if (content.Length > minOffset)
                    {
                        var unconsumed = content[minOffset..];
                        if (progressionCheck == null || progressionCheck(unconsumed))
                        {
                            if (IsActivePrompt(unconsumed, prompt))
                            {
                                var value = valueFactory();
                                try
                                {
                                    await send(process, value, cancellationToken);
                                    return (content.Length, true);
                                }
                                catch (Exception) when (optional && process.HasExited)
                                {
                                    return (minOffset, false);
                                }
                            }
                            if (completionCheck != null && completionCheck(unconsumed))
                            {
                                return (content.Length, false);
                            }
                        }
                    }
                }
                catch (IOException) { }
            }
            await Task.Delay(50, cancellationToken);
        }
        if (optional) return (minOffset, false);
        throw new TimeoutException($"Таймаут ожидания запроса OTP «{prompt}» в сессии KiTTY.");
    }

    public static void CleanupStaleLogs(string? tempDirectory = null)
    {
        var dir = tempDirectory ?? Path.GetTempPath();
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "kitty-manager-jh-otp-*.log"))
            {
                try
                {
                    File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    public static void SpawnDetachedLogCleanupWatcher(Process process, string logPath)
    {
        try
        {
            if (process.HasExited)
            {
                try { File.Delete(logPath); } catch { }
                return;
            }

            var pid = process.Id;
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                var escapedPath = logPath.Replace("'", "''");
                var command = $"$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue; if ($p) {{ $p.WaitForExit() }}; for ($i = 0; $i -lt 30; $i++) {{ Remove-Item -Force '{escapedPath}' -ErrorAction SilentlyContinue; if (-not (Test-Path '{escapedPath}')) {{ break }}; Start-Sleep -Milliseconds 200 }}";
                psi = new ProcessStartInfo("powershell.exe")
                {
                    ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-Command", command },
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
            }
            else
            {
                var escapedPath = logPath.Replace("'", "'\\''");
                var script = $"tail --pid={pid} -f /dev/null 2>/dev/null; for i in $(seq 1 30); do rm -f '{escapedPath}'; [ ! -f '{escapedPath}' ] && break; sleep 0.2; done";
                psi = new ProcessStartInfo("sh")
                {
                    ArgumentList = { "-c", script },
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
            }
            Process.Start(psi);
        }
        catch { }
    }

    public static void CleanTemporaryLogOnExit(Process? process, string logPath) =>
        _ = CleanTemporaryLogOnExitAsync(process, logPath);

    public static async Task CleanTemporaryLogOnExitAsync(
        Process? process, string logPath, TimeSpan? maxWait = null,
        Action<string>? deleteAction = null)
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }

        var del = deleteAction ?? File.Delete;
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!File.Exists(logPath)) return;
                del(logPath);
                if (!File.Exists(logPath)) return;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
        try
        {
            if (File.Exists(logPath))
                del(logPath);
        }
        catch { }
        try { process?.Dispose(); } catch { }
    }

    /// <summary>
    /// Finds every running KiTTY process whose window title contains the given
    /// substring. The caller owns (and must dispose) the returned processes.
    /// </summary>
    public static List<Process> FindAllByTitle(string titlePart)
    {
        var result = new List<Process>();
        foreach (var name in new[] { "kitty", "kitty_portable" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                process.Refresh();
                if (process.MainWindowTitle.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch { }
            process.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Finds a running KiTTY process whose window title contains the given substring.
    /// </summary>
    public static Process? FindByTitle(string titlePart)
    {
        var all = FindAllByTitle(titlePart);
        for (var index = 1; index < all.Count; index++) all[index].Dispose();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// Sends multiple lines (command + password) to a running KiTTY window with delays between them.
    /// </summary>
    public static async Task SendLinesAsync(
        Process process, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            await SendLineAsync(process, line, cancellationToken);
            await Task.Delay(500, cancellationToken);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}

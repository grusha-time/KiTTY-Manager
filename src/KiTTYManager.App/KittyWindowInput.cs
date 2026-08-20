using System.Diagnostics;
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

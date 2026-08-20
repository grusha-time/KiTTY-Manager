using System.Diagnostics;

internal enum NativeKlinkProbeOutcome
{
    Passed,
    Failed,
    TimedOut,
    Skipped,
    Cancelled
}

internal enum NativeKlinkProbeReason
{
    RemoteMarkerObserved,
    UnsupportedPlatform,
    KlinkNotFound,
    InvalidInput,
    ProcessStartFailed,
    RemoteMarkerMissing,
    Timeout,
    Cancelled
}

internal sealed record NativeKlinkProbeResult(
    NativeKlinkProbeOutcome Outcome,
    NativeKlinkProbeReason Reason,
    int? ExitCode = null);

internal static class NativeKlinkProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static async Task<NativeKlinkProbeResult> RunAsync(
        string klinkExecutablePath,
        string sessionName,
        string? sessionFolder = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(NativeKlinkProbeOutcome.Skipped, NativeKlinkProbeReason.UnsupportedPlatform);
        if (string.IsNullOrWhiteSpace(klinkExecutablePath) || string.IsNullOrWhiteSpace(sessionName))
            return new(NativeKlinkProbeOutcome.Skipped, NativeKlinkProbeReason.InvalidInput);

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(klinkExecutablePath);
        }
        catch
        {
            return new(NativeKlinkProbeOutcome.Skipped, NativeKlinkProbeReason.InvalidInput);
        }

        if (!File.Exists(executablePath))
            return new(NativeKlinkProbeOutcome.Skipped, NativeKlinkProbeReason.KlinkNotFound);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var successMarker = "KITTY_MANAGER_AUTH_" + Guid.NewGuid().ToString("N");
        foreach (var argument in BuildArguments(sessionName, sessionFolder, successMarker))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new(NativeKlinkProbeOutcome.Failed, NativeKlinkProbeReason.ProcessStartFailed);
        }
        catch
        {
            return new(NativeKlinkProbeOutcome.Failed, NativeKlinkProbeReason.ProcessStartFailed);
        }

        var markerFound = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutPump = WatchForMarkerAsync(process.StandardOutput, successMarker, markerFound);
        var stderrPump = WatchForMarkerAsync(process.StandardError, successMarker, markerFound);
        var exited = process.WaitForExitAsync();
        var timedOut = Task.Delay(ProbeTimeout);
        var cancelled = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan);

        NativeKlinkProbeResult result;
        var completed = await Task.WhenAny(markerFound.Task, exited, timedOut, cancelled);
        if (completed == markerFound.Task)
        {
            result = new(NativeKlinkProbeOutcome.Passed, NativeKlinkProbeReason.RemoteMarkerObserved);
        }
        else if (completed == cancelled)
        {
            result = new(NativeKlinkProbeOutcome.Cancelled, NativeKlinkProbeReason.Cancelled);
        }
        else if (completed == timedOut)
        {
            result = new(NativeKlinkProbeOutcome.TimedOut, NativeKlinkProbeReason.Timeout);
        }
        else
        {
            await IgnoreReadErrorsAsync(stdoutPump, stderrPump);
            result = markerFound.Task.IsCompleted
                ? new(NativeKlinkProbeOutcome.Passed, NativeKlinkProbeReason.RemoteMarkerObserved, process.ExitCode)
                : new(NativeKlinkProbeOutcome.Failed,
                    NativeKlinkProbeReason.RemoteMarkerMissing, process.ExitCode);
        }

        StopOwnedProcess(process);
        await IgnoreProcessExitErrorsAsync(process);
        await IgnoreReadErrorsAsync(stdoutPump, stderrPump);
        return result;
    }

    private static async Task WatchForMarkerAsync(
        StreamReader reader, string successMarker, TaskCompletionSource markerFound)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                if (IsSuccessLine(line, successMarker))
                    markerFound.TrySetResult();
        }
        catch (IOException)
        {
            // The owned process was stopped after success or timeout.
        }
        catch (ObjectDisposedException)
        {
            // The owned process was stopped after success or timeout.
        }
    }

    internal static IReadOnlyList<string> BuildArguments(
        string sessionName, string? sessionFolder, string successMarker)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(sessionFolder))
        {
            arguments.Add("-folder");
            arguments.Add(sessionFolder);
        }
        arguments.Add("-load");
        arguments.Add(sessionName);
        arguments.Add("-batch");
        arguments.Add($"printf '{successMarker}\\n'");
        return arguments;
    }

    internal static bool IsSuccessLine(string line, string successMarker) =>
        line.Equals(successMarker, StringComparison.Ordinal);

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task IgnoreProcessExitErrorsAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task IgnoreReadErrorsAsync(params Task[] readers)
    {
        try
        {
            await Task.WhenAll(readers);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

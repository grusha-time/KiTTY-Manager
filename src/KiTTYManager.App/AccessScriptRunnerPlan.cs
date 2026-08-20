using System.Diagnostics;
using System.IO;
using KiTTYManager.Core;

namespace KiTTYManager.App;

/// <summary>
/// Owns the temporary login script and session log for one isolated access
/// command. The runner never opens a SOCKS listener and never reuses an
/// interactive jumphost console.
/// </summary>
internal sealed class AccessScriptRunnerPlan : IDisposable
{
    private readonly KittyLoginScript loginScript;
    private readonly KittyRoutedSession? isolatedSession;
    public ProcessStartInfo StartInfo { get; }
    public string CompletionMarker { get; }
    public string SessionLogPath { get; }
    public string? TotpCodeForWindowInput { get; }
    public TimeSpan CompletionTimeout { get; }

    private AccessScriptRunnerPlan(
        ProcessStartInfo startInfo, KittyLoginScript script, string marker,
        string logPath, string? totpCode, KittyRoutedSession? isolatedSession,
        TimeSpan completionTimeout)
    {
        StartInfo = startInfo;
        loginScript = script;
        CompletionMarker = marker;
        SessionLogPath = logPath;
        TotpCodeForWindowInput = totpCode;
        this.isolatedSession = isolatedSession;
        CompletionTimeout = completionTimeout;
    }

    public static AccessScriptRunnerPlan Create(
        string kittyExecutable, ManagedServer server, BaseProxy proxy, DateTimeOffset now)
    {
        if (proxy.StartupServerId != server.Id)
            throw new ArgumentException("Для точки входа выбрана другая сессия.", nameof(server));
        if (string.IsNullOrWhiteSpace(proxy.PostLoginCommand))
            throw new ArgumentException("Команда после входа не настроена.", nameof(proxy));
        if (proxy.RepeatAccountPasswordAfterCommand && server.Password.Length == 0)
            throw new InvalidOperationException(
                "Для повторного запроса Password заполните пароль учётной записи jumphost-сессии в менеджере.");

        var token = Guid.NewGuid().ToString("N");
        var marker = $"__KITTY_MANAGER_ACCESS_DONE_{token}__";
        var variable = $"__km_rc_{token}";
        var command = proxy.PostLoginCommand.Trim().TrimEnd(';');
        var wrapped = $"{command}; {variable}=$?; printf '\\n{marker}:%s\\n' \"${variable}\"";
        var shellPrompt = string.IsNullOrWhiteSpace(server.ShellPrompt) ? "$" : server.ShellPrompt;
        var steps = new List<JumphostPromptResponse>
        {
            new(shellPrompt, wrapped)
        };
        if (proxy.RepeatAccountPasswordAfterCommand && server.Password.Length > 0)
            steps.Add(new(proxy.PostLoginPasswordPrompt.Trim(), server.Password));
        var script = KittyLoginScript.Create(steps)
            ?? throw new InvalidOperationException("Не удалось создать login script KiTTY.");

        KittyRoutedSession? isolatedSession = null;
        try
        {
        var fullKitty = Path.GetFullPath(kittyExecutable);
        var info = new ProcessStartInfo(fullKitty)
        {
            WorkingDirectory = Path.GetDirectoryName(fullKitty)!
        };
        if (!string.IsNullOrWhiteSpace(server.SourceSessionPath) && File.Exists(server.SourceSessionPath))
        {
            // Do not inherit Autocommand or embedded ScriptfileContent from the
            // user's interactive session: either can consume the sudo Password
            // prompt before this runner's prompt-aware script sees it.
            isolatedSession = KittyRoutedSession.CreateDirect(
                server.SourceSessionPath!, server.Host, server.Port, ignoreImportedCommand: true);
            info.ArgumentList.Add("-loadfile");
            info.ArgumentList.Add(isolatedSession.Path);
            info.ArgumentList.Add("-ssh");
            info.ArgumentList.Add(server.Host);
            info.ArgumentList.Add("-P");
            info.ArgumentList.Add(server.Port.ToString());
            if (server.Username.Length > 0)
            {
                info.ArgumentList.Add("-l");
                info.ArgumentList.Add(server.Username);
            }
        }
        else
        {
            info.ArgumentList.Add("-ssh");
            info.ArgumentList.Add(server.Host);
            info.ArgumentList.Add("-P");
            info.ArgumentList.Add(server.Port.ToString());
            if (server.Username.Length > 0)
            {
                info.ArgumentList.Add("-l");
                info.ArgumentList.Add(server.Username);
            }
        }
        // The isolated session deliberately clears its encrypted Password, so
        // authentication secrets must always be supplied by explicit switches.
        const bool preserveSavedAuthentication = false;
        foreach (var argument in JumphostStartupPlan.KittyAuthenticationArguments(
                     server, preserveSavedAuthentication))
            info.ArgumentList.Add(argument);
        var keyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ");
        if (keyPath is not null)
        {
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(keyPath);
        }
        var logPath = Path.Combine(Path.GetTempPath(), $"kitty-manager-access-{token}.log");
        info.ArgumentList.Add("-sessionlog");
        info.ArgumentList.Add(logPath);
        info.ArgumentList.Add("-loginscript");
        info.ArgumentList.Add(script.Path);
        info.ArgumentList.Add("-title");
        // Stable title without the run token: the manager finds and reuses
        // this console by title on every subsequent script run.
        info.ArgumentList.Add(JumphostConsoleTitles.AccessTitle(server.Name, proxy.Name));

        var totp = proxy.TotpSecret.Length == 0 ? null : TotpGenerator.Generate(
            proxy.TotpSecret, now, proxy.TotpDigits, proxy.TotpPeriodSeconds, proxy.TotpAlgorithm);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(proxy.PostCommandReadyDelaySeconds, 1, 600));
        return new(info, script, marker, logPath, totp, isolatedSession, timeout);
        }
        catch
        {
            script.Dispose();
            isolatedSession?.Dispose();
            throw;
        }
    }

    public bool HasCompletionMarker()
    {
        try
        {
            return File.Exists(SessionLogPath) &&
                File.ReadAllText(SessionLogPath).Contains(CompletionMarker, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task DisposeAfterExitAsync(Process process)
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        finally { Dispose(); process.Dispose(); }
    }

    public void Dispose()
    {
        loginScript.Dispose();
        isolatedSession?.Dispose();
        try { File.Delete(SessionLogPath); } catch { }
    }
}

namespace KiTTYManager.Core;

public static class PamLoginPlan
{
    public static string WrapExecCommand(string rawCommand)
    {
        var trimmed = rawCommand.Trim();
        var inner = trimmed.StartsWith("exec ", StringComparison.Ordinal)
            ? trimmed
            : $"exec {trimmed}";
        var escaped = inner.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"exec sh -c '{escaped}'";
    }

    public static IReadOnlyList<JumphostPromptResponse> BuildPromptResponses(
        ManagedServer target, ManagedServer? jumpHost, DateTimeOffset? now = null, BaseProxy? jumpHostProxy = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.PamSshCommand))
            throw new InvalidOperationException("Для сессии с PAM-авторизацией не указана SSH-команда PAM.");

        var steps = new List<JumphostPromptResponse>();
        var jumpHostPrompt = string.IsNullOrWhiteSpace(jumpHost?.ShellPrompt) ? "$" : jumpHost.ShellPrompt;

        // JumpHost post-login command if configured for this jumpHost
        if (jumpHostProxy is not null && jumpHost is not null && jumpHostProxy.StartupServerId == jumpHost.Id)
        {
            if (jumpHostProxy.PostLoginCommand.Length > 0)
            {
                steps.Add(new JumphostPromptResponse("$", jumpHostProxy.PostLoginCommand.Trim()));
                if (jumpHostProxy.RepeatAccountPasswordAfterCommand && jumpHost.Password.Length > 0)
                {
                    var postLoginPassPrompt = string.IsNullOrWhiteSpace(jumpHostProxy.PostLoginPasswordPrompt)
                        ? "assword"
                        : jumpHostProxy.PostLoginPasswordPrompt.Trim();
                    steps.Add(new JumphostPromptResponse(postLoginPassPrompt, jumpHost.Password));
                }
            }
        }

        // PAM SSH command on JumpHost wrapped to guarantee process replacement
        // and immediate shell termination on syntax/parse errors or command failures.
        var sshCommand = WrapExecCommand(target.PamSshCommand);
        steps.Add(new JumphostPromptResponse(jumpHostPrompt, sshCommand));

        if (!string.IsNullOrEmpty(target.PamPassword))
        {
            steps.Add(new JumphostPromptResponse("assword", target.PamPassword));
        }

        // Note: Target PAM TOTP is generated dynamically upon actual challenge via
        // KittyWindowInput.SendLineAfterPromptAsync to guarantee codes never expire
        // during connection/auth delays. It is not statically placed in login script.

        if (target.PamAutoConfirm)
        {
            var confirmPrompt = string.IsNullOrWhiteSpace(target.PamConfirmPrompt)
                ? "[Y/n]"
                : target.PamConfirmPrompt.Trim();
            steps.Add(new JumphostPromptResponse(confirmPrompt, "Y"));
        }

        var rootLogin = target.RootLogin?.Trim() ?? "";
        if (rootLogin.Length > 0)
        {
            var targetShellPrompt = string.IsNullOrWhiteSpace(target.ShellPrompt) ? "$" : target.ShellPrompt;
            var rootCommand = KittyCredentialDecoder.NormalizeRootCommand(rootLogin) ?? rootLogin;
            steps.Add(new JumphostPromptResponse(targetShellPrompt, rootCommand));
            if (!string.IsNullOrEmpty(target.RootPassword))
            {
                steps.Add(new JumphostPromptResponse("assword", target.RootPassword));
            }
        }

        return steps;
    }

    public static int SafeMargin(int period) => period > 8 ? 8 : Math.Max(1, period / 2);
}

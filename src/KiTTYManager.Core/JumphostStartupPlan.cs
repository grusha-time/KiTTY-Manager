namespace KiTTYManager.Core;

public sealed class JumphostPromptResponse(string prompt, string response)
{
    public string Prompt { get; } = prompt;
    public string Response { get; } = response;
    public override string ToString() => $"Prompt={Prompt}; Response=<secret>";
}

public static class JumphostStartupPlan
{
    public static IReadOnlyList<string> KittyAuthenticationArguments(
        ManagedServer server, bool preserveSavedSessionAuthentication = false)
    {
        // A portable saved session already contains the authentication setup
        // that KiTTY successfully uses on a normal double click.  Do not
        // replace it unless the managed TOTP flow explicitly needs a password.
        if (preserveSavedSessionAuthentication) return [];

        // KiTTY login scripts only see terminal output after SSH authentication.
        // Authentication secrets must therefore use KiTTY's native switches.
        if (server.PrivateKeyPath.Length > 0 && server.PrivateKeyPassphrase.Length > 0)
            return ["-pw", server.PrivateKeyPassphrase];
        return server.Password.Length > 0 ? ["-pass", server.Password] : [];
    }

    public static IReadOnlyList<JumphostPromptResponse> Build(
        BaseProxy proxy, ManagedServer server, DateTimeOffset now, bool includeInitialPassword = true)
    {
        if (proxy.StartupServerId != server.Id)
            throw new ArgumentException("Для точки входа выбрана другая сессия.", nameof(server));

        var steps = new List<JumphostPromptResponse>();
        if (includeInitialPassword && server.Password.Length > 0)
            steps.Add(new JumphostPromptResponse("assword", server.Password));
        if (proxy.TotpSecret.Length > 0)
            steps.Add(new JumphostPromptResponse(proxy.TotpPrompt.Trim(), TotpGenerator.Generate(
                proxy.TotpSecret, now, proxy.TotpDigits, proxy.TotpPeriodSeconds, proxy.TotpAlgorithm)));
        if (proxy.PostLoginCommand.Length > 0)
        {
            steps.Add(new JumphostPromptResponse("$", proxy.PostLoginCommand.Trim()));
            if (proxy.RepeatAccountPasswordAfterCommand && server.Password.Length > 0)
                steps.Add(new JumphostPromptResponse(proxy.PostLoginPasswordPrompt.Trim(), server.Password));
        }
        return steps;
    }

    public static IReadOnlyList<JumphostPromptResponse> BuildPostLogin(
        BaseProxy proxy, ManagedServer server, bool includeAccessCommand = true)
    {
        if (proxy.StartupServerId != server.Id)
            throw new ArgumentException("Для точки входа выбрана другая сессия.", nameof(server));
        if (!includeAccessCommand || proxy.PostLoginCommand.Length == 0) return [];

        var steps = new List<JumphostPromptResponse>
        {
            new("$", proxy.PostLoginCommand.Trim())
        };
        if (proxy.RepeatAccountPasswordAfterCommand && server.Password.Length > 0)
            steps.Add(new JumphostPromptResponse(proxy.PostLoginPasswordPrompt.Trim(), server.Password));
        return steps;
    }
}

namespace KiTTYManager.Core;

public static class KittyLaunchPlan
{
    public static IReadOnlyList<string> DirectConsoleArguments(
        ManagedServer server, string host, int port, bool loadSavedSession,
        string? savedSessionPath = null, string? loginScriptPath = null)
    {
        var arguments = new List<string>();
        if (loadSavedSession)
            arguments.AddRange(["-loadfile",
                savedSessionPath ?? server.SourceSessionPath ?? server.Name]);
        arguments.AddRange(["-ssh", host, "-P", port.ToString()]);
        arguments.AddRange(["-loghost", $"{server.Host}:{server.Port}"]);
        if (server.EffectiveUsername.Length > 0)
            arguments.AddRange(["-l", server.EffectiveUsername]);
        AddAuthenticationSecret(arguments, server);
        AddPrivateKey(arguments, server);
        if (!string.IsNullOrWhiteSpace(loginScriptPath))
            arguments.AddRange(["-loginscript", loginScriptPath]);
        else
        {
            var privilegeCommand = BuildPrivilegeCommand(server);
            if (privilegeCommand is not null) arguments.AddRange(["-cmd", privilegeCommand]);
        }
        AppendImportedCommand(arguments, server, loadSavedSession);
        arguments.AddRange(["-title", $"{server.Name} — прямое подключение"]);
        return arguments;
    }

    public static IReadOnlyList<string> RoutedConsoleArguments(
        ManagedServer server, int localSshPort, bool loadSavedSession, string? savedSessionPath = null,
        string? loginScriptPath = null)
    {
        var arguments = new List<string>();
        if (loadSavedSession)
        {
            arguments.Add("-loadfile");
            arguments.Add(savedSessionPath ?? server.SourceSessionPath ?? server.Name);
        }

        arguments.AddRange(["-ssh", "127.0.0.1", "-P", localSshPort.ToString()]);
        arguments.AddRange(["-loghost", $"{server.Host}:{server.Port}"]);
        if (server.EffectiveUsername.Length > 0) arguments.AddRange(["-l", server.EffectiveUsername]);
        AddAuthenticationSecret(arguments, server);
        AddPrivateKey(arguments, server);

        if (!string.IsNullOrWhiteSpace(loginScriptPath))
            arguments.AddRange(["-loginscript", loginScriptPath]);
        else
        {
            var privilegeCommand = BuildPrivilegeCommand(server);
            if (privilegeCommand is not null) arguments.AddRange(["-cmd", privilegeCommand]);
        }

        // When there is no saved session file, ImportedCommand (Команда KiTTY)
        // is not carried by Autocommand in the session file.  Pass it via -cmd
        // so KiTTY executes it after login (and after the login script if any).
        AppendImportedCommand(arguments, server, loadSavedSession);

        arguments.AddRange(["-title", $"{server.Name} — автоматический маршрут"]);
        return arguments;
    }

    public static IReadOnlyList<string> OriginalSessionArguments(
        ManagedServer server, string? loginScriptPath = null)
    {
        var arguments = new List<string> { "-load", server.Name };
        AddAuthenticationSecret(arguments, server);
        if (!string.IsNullOrWhiteSpace(loginScriptPath))
            arguments.AddRange(["-loginscript", loginScriptPath]);
        else
        {
            var privilegeCommand = BuildPrivilegeCommand(server);
            if (privilegeCommand is not null) arguments.AddRange(["-cmd", privilegeCommand]);
        }
        return arguments;
    }

    public static IReadOnlyList<string> RoutedTunnelArguments(
        ManagedServer server, int localSshPort,
        bool loadSavedSession, string? savedSessionPath = null, string? loginScriptPath = null,
        bool skipPrivilegeCommand = false)
    {
        var arguments = new List<string>();
        if (loadSavedSession)
        {
            arguments.Add("-loadfile");
            arguments.Add(savedSessionPath ?? server.SourceSessionPath ?? server.Name);
        }
        arguments.AddRange(["-ssh", "127.0.0.1", "-P", localSshPort.ToString()]);
        arguments.AddRange(["-loghost", $"{server.Host}:{server.Port}"]);
        if (server.EffectiveUsername.Length > 0) arguments.AddRange(["-l", server.EffectiveUsername]);
        AddAuthenticationSecret(arguments, server);
        AddPrivateKey(arguments, server);
        if (!string.IsNullOrWhiteSpace(loginScriptPath))
            arguments.AddRange(["-loginscript", loginScriptPath]);
        else if (!skipPrivilegeCommand)
        {
            var privilegeCommand = BuildPrivilegeCommand(server);
            if (privilegeCommand is not null) arguments.AddRange(["-cmd", privilegeCommand]);
        }
        arguments.AddRange(["-title", $"{server.Name} — веб-туннель"]);
        return arguments;
    }

    /// <summary>
    /// When there is no saved KiTTY session file, the ImportedCommand is not
    /// stored as Autocommand in the session.  This method appends it via -cmd
    /// so KiTTY executes it after login (and after the login script if any).
    /// </summary>
    private static void AppendImportedCommand(List<string> arguments, ManagedServer server, bool loadSavedSession)
    {
        if (loadSavedSession || server.IgnoreImportedCommand || server.ImportedCommand.Length == 0)
            return;
        // Skip privilege commands — they are already handled by the login script.
        if (KittyCredentialDecoder.NormalizeRootCommand(server.ImportedCommand) is not null)
            return;

        var escaped = EscapeAutoCommand(server.ImportedCommand);
        var cmdIndex = arguments.IndexOf("-cmd");
        if (cmdIndex >= 0 && cmdIndex + 1 < arguments.Count)
        {
            // -cmd already set (privilege command).  Combine: run privilege
            // command first, then the imported command on the next line.
            arguments[cmdIndex + 1] = arguments[cmdIndex + 1] + "\\n" + escaped;
        }
        else
        {
            arguments.AddRange(["-cmd", escaped]);
        }
    }

    private static void AddPrivateKey(List<string> arguments, ManagedServer server)
    {
        var keyPath = ManagerPathResolver.ResolveOptionalFile(server.PrivateKeyPath, "SSH-ключ");
        if (keyPath is not null) arguments.AddRange(["-i", keyPath]);
    }

    private static void AddAuthenticationSecret(List<string> arguments, ManagedServer server)
    {
        // -pw answers the first non-echo prompt (key passphrase).
        // Pass it whenever a passphrase is set — the key may reside on the
        // jumphost even when PrivateKeyPath is empty.
        if (server.PrivateKeyPassphrase.Length > 0)
            arguments.AddRange(["-pw", server.PrivateKeyPassphrase]);

        // -pass stores the server password for the second prompt.
        // For routed sessions the session file's Password is cleared (encrypted
        // with the original HostName), so -pass is the only way to supply it.
        if (server.Password.Length > 0)
            arguments.AddRange(["-pass", server.Password]);
    }

    private static string? BuildPrivilegeCommand(ManagedServer server)
    {
        var command = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin);
        if (command is null) return null;
        return EscapeAutoCommand(command);
    }

    private static string EscapeAutoCommand(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

}

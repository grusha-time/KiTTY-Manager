namespace KiTTYManager.Core;

public sealed class ServerEditorDraft
{
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";
    public string PortText { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKeyPassphrase { get; set; } = "";
    public string RootLogin { get; set; } = "";
    public string RootPassword { get; set; } = "";
    public string ShellPrompt { get; set; } = "$";
    public string ImportedCommand { get; set; } = "";
    public bool IgnoreImportedCommand { get; set; }
    public bool TryDirectWithoutJumphost { get; set; }
    public string KeepaliveIntervalText { get; set; } = "15";
    public bool EnableTcpKeepalives { get; set; } = true;
    public bool ReconnectOnConnectionFailure { get; set; } = true;
    public bool ReconnectOnSystemWakeup { get; set; } = true;
    public Guid? RequiredPreviousServerId { get; set; }
}

public static class ServerEditorDirtyTracker
{
    public static bool HasUnsavedChanges(ManagedServer? server, ServerEditorDraft? draft)
    {
        if (server is null || draft is null) return false;

        if ((draft.ServerName ?? "").Trim() != server.Name) return true;
        if ((draft.Host ?? "").Trim() != server.Host) return true;
        if (!int.TryParse(draft.PortText, out var port) || port != server.Port) return true;
        if ((draft.Username ?? "").Trim() != server.Username) return true;
        if ((draft.Password ?? "") != server.Password) return true;
        if ((draft.PrivateKeyPath ?? "").Trim() != server.PrivateKeyPath) return true;
        if ((draft.PrivateKeyPassphrase ?? "") != server.PrivateKeyPassphrase) return true;
        if ((draft.RootLogin ?? "").Trim() != server.RootLogin) return true;
        if ((draft.RootPassword ?? "") != server.RootPassword) return true;

        var effectivePrompt = string.IsNullOrWhiteSpace(draft.ShellPrompt) ? "$" : draft.ShellPrompt;
        if (effectivePrompt != server.ShellPrompt) return true;

        if ((draft.ImportedCommand ?? "") != server.ImportedCommand) return true;
        if (draft.IgnoreImportedCommand != server.IgnoreImportedCommand) return true;
        if (draft.TryDirectWithoutJumphost != server.TryDirectWithoutJumphost) return true;

        if (!int.TryParse(draft.KeepaliveIntervalText, out var keepalive) ||
            keepalive != server.KeepaliveIntervalSeconds) return true;

        if (draft.EnableTcpKeepalives != server.EnableTcpKeepalives) return true;
        if (draft.ReconnectOnConnectionFailure != server.ReconnectOnConnectionFailure) return true;
        if (draft.ReconnectOnSystemWakeup != server.ReconnectOnSystemWakeup) return true;
        if (draft.RequiredPreviousServerId != server.RequiredPreviousServerId) return true;

        return false;
    }
}

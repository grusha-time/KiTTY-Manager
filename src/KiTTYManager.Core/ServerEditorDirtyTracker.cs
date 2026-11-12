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
    public string RootPasswordPrompt { get; set; } = "assword";
    public string ShellPrompt { get; set; } = "$";
    public string ImportedCommand { get; set; } = "";
    public bool IgnoreImportedCommand { get; set; }
    public bool TryDirectWithoutJumphost { get; set; }
    public string KeepaliveIntervalText { get; set; } = "15";
    public bool EnableTcpKeepalives { get; set; } = true;
    public bool ReconnectOnConnectionFailure { get; set; } = true;
    public bool ReconnectOnSystemWakeup { get; set; } = true;
    public Guid? RequiredPreviousServerId { get; set; }
    public bool UsePam { get; set; }
    public string PamSshCommand { get; set; } = "";
    public string PamPassword { get; set; } = "";
    public string PamTotpSecret { get; set; } = "";
    public string PamTotpAlgorithm { get; set; } = "SHA1";
    public string PamTotpDigitsText { get; set; } = "6";
    public string PamTotpPeriodText { get; set; } = "30";
    public string PamTotpPrompt { get; set; } = "OTP: ";
    public bool PamAutoConfirm { get; set; }
    public string PamConfirmPrompt { get; set; } = "[Y/n]";
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

        var effectiveRootPrompt = string.IsNullOrWhiteSpace(draft.RootPasswordPrompt) ? "assword" : draft.RootPasswordPrompt;
        var serverRootPrompt = string.IsNullOrWhiteSpace(server.RootPasswordPrompt) ? "assword" : server.RootPasswordPrompt;
        if (effectiveRootPrompt != serverRootPrompt) return true;

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

        if (draft.UsePam != server.UsePam) return true;
        if ((draft.PamSshCommand ?? "").Trim() != (server.PamSshCommand ?? "").Trim()) return true;
        if ((draft.PamPassword ?? "") != (server.PamPassword ?? "")) return true;
        if ((draft.PamTotpSecret ?? "").Trim() != (server.PamTotpSecret ?? "").Trim()) return true;

        var draftAlgo = string.IsNullOrWhiteSpace(draft.PamTotpAlgorithm) ? "SHA1" : draft.PamTotpAlgorithm.Trim().ToUpperInvariant();
        var serverAlgo = string.IsNullOrWhiteSpace(server.PamTotpAlgorithm) ? "SHA1" : server.PamTotpAlgorithm.Trim().ToUpperInvariant();
        if (draftAlgo != serverAlgo) return true;

        if (!int.TryParse(draft.PamTotpDigitsText, out var pamDigits) || pamDigits != server.PamTotpDigits) return true;
        if (!int.TryParse(draft.PamTotpPeriodText, out var pamPeriod) || pamPeriod != server.PamTotpPeriodSeconds) return true;

        var effectivePamTotpPrompt = string.IsNullOrWhiteSpace(draft.PamTotpPrompt) ? "OTP: " : draft.PamTotpPrompt;
        var serverPamTotpPrompt = string.IsNullOrWhiteSpace(server.PamTotpPrompt) ? "OTP: " : server.PamTotpPrompt;
        if (effectivePamTotpPrompt != serverPamTotpPrompt) return true;

        if (draft.PamAutoConfirm != server.PamAutoConfirm) return true;

        var effectivePamConfirmPrompt = string.IsNullOrWhiteSpace(draft.PamConfirmPrompt) ? "[Y/n]" : draft.PamConfirmPrompt;
        var serverPamConfirmPrompt = string.IsNullOrWhiteSpace(server.PamConfirmPrompt) ? "[Y/n]" : server.PamConfirmPrompt;
        if (effectivePamConfirmPrompt != serverPamConfirmPrompt) return true;

        return false;
    }
}

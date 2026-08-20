using System.Text;

namespace KiTTYManager.Core;

public sealed class KittyLoginScript : IDisposable
{
    public string Path { get; }

    private KittyLoginScript(string path) => Path = path;

    public static KittyLoginScript? Create(ManagedServer server)
    {
        var command = KittyCredentialDecoder.NormalizeRootCommand(server.RootLogin);
        if (command is null ||
            server.RootPassword.Length == 0)
            return null;

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "kitty-manager-login-" + Guid.NewGuid().ToString("N") + ".txt");

        // KiTTY login scripts are ordered prompt / reply pairs. Waiting for the
        // shell prompt first prevents the root password from matching and being
        // sent to the initial SSH "password:" prompt.
        var shellPrompt = string.IsNullOrWhiteSpace(server.ShellPrompt) ? "$" : server.ShellPrompt;
        File.WriteAllLines(path, [shellPrompt, command, "assword", server.RootPassword],
            new UTF8Encoding(false));
        return new KittyLoginScript(path);
    }

    public static KittyLoginScript? Create(IReadOnlyList<JumphostPromptResponse> steps)
    {
        if (steps.Count == 0) return null;
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "kitty-manager-jumphost-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(path, steps.SelectMany(step => new[] { step.Prompt, step.Response }),
            new UTF8Encoding(false));
        return new KittyLoginScript(path);
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { }
    }
}

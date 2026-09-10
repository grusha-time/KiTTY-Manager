using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KiTTYManager.Core;

public sealed class AnsibleRuntimeManifest
{
    public int Version { get; set; } = 1;
    public string AnsibleCoreVersion { get; set; } = "2.16";
    public List<AnsibleRuntimeComponent> Components { get; set; } = [];
}

public sealed class AnsibleRuntimeComponent
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string LicenseRelativePath { get; set; } = "";
}

public sealed record AnsibleRuntimeCheck(bool Ready, IReadOnlyList<string> Errors);

public static class AnsibleRuntimeVerifier
{
    public static AnsibleRuntimeCheck Verify(string runtimeRoot)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(runtimeRoot, "manifest.json");
        if (!File.Exists(manifestPath)) return new(false, ["Не найден runtime/ansible/manifest.json."]);
        AnsibleRuntimeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AnsibleRuntimeManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Пустой manifest.json.");
        }
        catch (Exception ex) { return new(false, [$"Повреждён manifest.json: {ex.Message}"]); }
        if (!manifest.AnsibleCoreVersion.StartsWith("2.16.", StringComparison.Ordinal) && manifest.AnsibleCoreVersion != "2.16")
            errors.Add("Runtime должен содержать ansible-core 2.16.*.");
        foreach (var item in manifest.Components)
        {
            if (!SafeRelative(item.RelativePath) || !SafeRelative(item.LicenseRelativePath))
            { errors.Add($"Компонент «{item.Name}» содержит небезопасный путь."); continue; }
            var path = Path.GetFullPath(Path.Combine(runtimeRoot, item.RelativePath));
            var license = Path.GetFullPath(Path.Combine(runtimeRoot, item.LicenseRelativePath));
            if (!File.Exists(path)) { errors.Add($"Не найден компонент «{item.Name}»: {item.RelativePath}"); continue; }
            if (!File.Exists(license)) errors.Add($"Не найдена лицензия «{item.Name}»: {item.LicenseRelativePath}");
            using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!actual.Equals(item.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                errors.Add($"Неверная SHA-256 компонента «{item.Name}».");
        }
        return new(errors.Count == 0, errors);
    }

    private static bool SafeRelative(string value) => !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathFullyQualified(value) && !value.Split('/', '\\').Contains("..");
}

public sealed record AnsibleTaskWorkspace(string TaskDirectory, string ServiceDirectory,
    string SecretDirectory, bool CreatedForRun);

public static class AnsibleTaskWorkspacePolicy
{
    public const string ServiceFolderName = ".kitty-manager";
    private static readonly string[] MaterialDirectories =
        ["roles", "collections", "templates", "files", "group_vars", "host_vars"];
    public static AnsibleTaskWorkspace Prepare(string templatesRoot, string playbookPath)
    {
        var root = FullWithSeparator(templatesRoot);
        var playbook = Path.GetFullPath(playbookPath);
        if (!File.Exists(playbook)) throw new FileNotFoundException("Playbook не найден.", playbook);
        Directory.CreateDirectory(root);
        var sourceDirectory = Path.GetDirectoryName(playbook)!;
        var existing = IsWithin(sourceDirectory, root);
        var taskDirectory = existing ? sourceDirectory : UniqueTaskDirectory(root, Path.GetFileNameWithoutExtension(playbook));
        if (!existing)
        {
            Directory.CreateDirectory(taskDirectory);
            CopyProjectMaterials(sourceDirectory, taskDirectory, playbook);
        }
        var service = Path.Combine(taskDirectory, ServiceFolderName);
        CleanupServiceFiles(taskDirectory);
        Directory.CreateDirectory(service);
        var secretRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager", "AnsibleSecrets");
        CleanupStaleDirectories(secretRoot, DateTime.UtcNow.AddHours(-1));
        var secret = Path.Combine(secretRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secret);
        return new(taskDirectory, service, secret, !existing);
    }

    public static void CleanupServiceFiles(string taskDirectory)
    {
        var service = Path.Combine(Path.GetFullPath(taskDirectory), ServiceFolderName);
        if (Directory.Exists(service)) Directory.Delete(service, true);
    }

    public static void DeleteCreatedTask(string templatesRoot, AnsibleTaskWorkspace workspace)
    {
        if (!workspace.CreatedForRun) throw new InvalidOperationException("Существующая папка задачи не удаляется целиком.");
        var root = FullWithSeparator(templatesRoot);
        var target = Path.GetFullPath(workspace.TaskDirectory);
        if (!IsWithin(target, root) || target.TrimEnd(Path.DirectorySeparatorChar).Equals(
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Удаление разрешено только для новой папки текущего запуска внутри Data/Tasks.");
        Directory.Delete(target, true);
    }

    public static void DeleteSecrets(AnsibleTaskWorkspace workspace)
    { if (Directory.Exists(workspace.SecretDirectory)) Directory.Delete(workspace.SecretDirectory, true); }

    private static string UniqueTaskDirectory(string root, string wanted)
    {
        var safe = Regex.Replace(wanted, "[^A-Za-z0-9А-Яа-я._-]+", "-").Trim('-', '.');
        if (safe.Length == 0) safe = "ansible-task";
        for (var i = 1; ; i++)
        {
            var path = Path.Combine(root, i == 1 ? safe : $"{safe}-{i}");
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
        }
    }
    private static string FullWithSeparator(string path) => Path.GetFullPath(path).TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    private static void CleanupStaleDirectories(string root, DateTime olderThanUtc)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
            try { if (Directory.GetLastWriteTimeUtc(directory) < olderThanUtc) Directory.Delete(directory, true); } catch { }
    }
    public static IReadOnlyList<string> ProjectMaterials(string playbookPath)
    {
        var playbook = Path.GetFullPath(playbookPath);
        var source = Path.GetDirectoryName(playbook)!;
        var files = Directory.EnumerateFiles(source, "*.yml").Concat(Directory.EnumerateFiles(source, "*.yaml"))
            .Append(playbook);
        foreach (var name in MaterialDirectories)
        {
            var directory = Path.Combine(source, name);
            if (Directory.Exists(directory)) files = files.Concat(SafeFiles(directory));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Select(EnsureSafeFile)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CopyProjectMaterials(string source, string target, string playbook)
    {
        foreach (var file in ProjectMaterials(playbook))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static IEnumerable<string> SafeFiles(string source)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка Ansible-задачи является ссылкой: " + source);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Файл Ansible-задачи является ссылкой: " + file);
            yield return file;
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory).Equals(ServiceFolderName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in SafeFiles(directory)) yield return file;
        }
    }

    private static string EnsureSafeFile(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Файл Ansible-задачи является ссылкой: " + file);
        return file;
    }
}

public sealed record AnsibleDependencyReport(IReadOnlyList<string> Roles, IReadOnlyList<string> Collections,
    IReadOnlyList<string> Missing);

public static class AnsibleWindowsMaterialScanner
{
    public const string VariableName = "kitty_manager_windows_files";

    public static IReadOnlyList<AnsibleLocalFile> Scan(string playbookPath)
    {
        var result = new List<AnsibleLocalFile>();
        var lines = File.ReadAllLines(playbookPath);
        var inList = false;
        var listIndent = -1;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!inList)
            {
                if (!trimmed.Equals(VariableName + ":", StringComparison.Ordinal)) continue;
                inList = true; listIndent = line.Length - line.TrimStart().Length; continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (indent <= listIndent || !trimmed.StartsWith("- ", StringComparison.Ordinal)) break;
            var configured = trimmed[2..].Trim().Trim('"', '\'');
            var source = ExpandWindowsVariables(configured);
            if (!File.Exists(source))
                throw new FileNotFoundException($"Локальный файл из {VariableName} не найден: {configured}", source);
            result.Add(new(source, Path.GetFileName(source)));
        }
        return result;
    }

    private static string ExpandWindowsVariables(string value) =>
        Regex.Replace(value, "%([^%]+)%", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
}

public static class AnsibleDependencyScanner
{
    private static readonly Regex CollectionModule = new(@"\b([a-zA-Z0-9_]+)\.([a-zA-Z0-9_]+)\.[a-zA-Z0-9_]+\s*:", RegexOptions.Compiled);
    private static readonly Regex RoleEntry = new("^\\s*-?\\s*role\\s*:\\s*['\\\"]?([a-zA-Z0-9_.-]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    public static AnsibleDependencyReport Scan(string taskDirectory)
    {
        var text = string.Join("\n", Directory.EnumerateFiles(taskDirectory, "*.y*ml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + AnsibleTaskWorkspacePolicy.ServiceFolderName + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        var roles = RoleEntry.Matches(text).Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var collections = CollectionModule.Matches(text).Select(x => x.Groups[1].Value + "." + x.Groups[2].Value)
            .Where(x => !x.Equals("ansible.builtin", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        var missing = new List<string>();
        foreach (var role in roles)
            if (!Directory.Exists(Path.Combine(taskDirectory, "roles", role))) missing.Add("role:" + role);
        foreach (var collection in collections)
        {
            var parts = collection.Split('.');
            if (!Directory.Exists(Path.Combine(taskDirectory, "collections", "ansible_collections", parts[0], parts[1])))
                missing.Add("collection:" + collection);
        }
        return new(roles, collections, missing);
    }
}

public sealed record AnsibleInventoryHost(ManagedServer Server, int LocalPort, string InventoryName,
    string GroupName, string GroupDisplay, int AuthIndex = 0, string DownloadFolder = "",
    IReadOnlyList<BatchTunnelDefinition>? Tunnels = null);

public static class AnsibleInventoryGenerator
{
    public const string QemuHostAddress = "10.0.2.2";
    public static string Generate(IEnumerable<AnsibleInventoryHost> hosts)
    {
        var rows = hosts.ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Не выбраны серверы Ansible.");
        var output = new StringBuilder("all:\n  vars:\n")
            .Append("    kitty_upload_dir: '__KITTY_UPLOAD_DIR__'\n")
            .Append("  children:\n");
        foreach (var group in rows.GroupBy(x => x.GroupName, StringComparer.Ordinal))
        {
            output.Append("    ").Append(YamlScalar(group.Key)).Append(":\n      vars:\n        kitty_group_display: ")
                .Append(YamlScalar(group.First().GroupDisplay)).Append("\n      hosts:\n");
            foreach (var row in group)
            {
                output.Append("        ").Append(YamlScalar(row.InventoryName)).Append(":\n")
                    .Append("          ansible_host: ").Append(QemuHostAddress).Append('\n')
                    .Append("          ansible_port: ").Append(row.LocalPort).Append('\n')
                    .Append("          ansible_user: ").Append(YamlScalar(row.Server.EffectiveUsername)).Append('\n')
                    .Append("          kitty_server_display: ").Append(YamlScalar(row.Server.Name)).Append('\n')
                    .Append("          kitty_download_dir: ").Append(YamlScalar(
                        "__KITTY_DOWNLOAD_DIR__/" + row.DownloadFolder)).Append('\n')
                    .Append("          ansible_ssh_executable: ").Append(YamlScalar($"__KITTY_SSH_{row.AuthIndex}__")).Append('\n')
                    .Append("          ansible_ssh_common_args: '-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 -o ServerAliveInterval=10 -o ServerAliveCountMax=1 -o TCPKeepAlive=yes'\n");
                var tunnels = row.Tunnels ?? [];
                output.Append(tunnels.Count == 0
                    ? "          kitty_manager_tunnels: []\n"
                    : "          kitty_manager_tunnels:\n");
                foreach (var tunnel in tunnels)
                    output.Append("            - name: ").Append(YamlScalar(tunnel.Name)).Append('\n')
                        .Append("              kind: ").Append(tunnel.Kind == BatchTunnelKind.Remote ? "remote" : "local").Append('\n')
                        .Append("              bind_host: ").Append(YamlScalar(tunnel.BindHost)).Append('\n')
                        .Append("              bind_port: ").Append(tunnel.BindPort).Append('\n')
                        .Append("              destination_host: ").Append(YamlScalar(tunnel.DestinationHost)).Append('\n')
                        .Append("              destination_port: ").Append(tunnel.DestinationPort).Append('\n');
                var becomeMethod = AnsibleRunPolicy.BecomeMethod(row.Server.RootLogin);
                if (becomeMethod is not null)
                    output.Append("          ansible_become_method: ").Append(YamlScalar(becomeMethod)).Append('\n');
            }
        }
        return output.ToString();
    }

    public static string SafeUniqueName(string value, Guid id, ISet<string> used)
    {
        var name = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        if (name.Length == 0 || char.IsDigit(name[0])) name = "item_" + name;
        var candidate = name;
        if (!used.Add(candidate)) { candidate = $"{name}_{id:N}"; used.Add(candidate); }
        return candidate;
    }

    private static string YamlScalar(string value) => "'" + (value ?? "").Replace("'", "''") + "'";
}

public static class AnsibleSecretRedactor
{
    public static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderByDescending(x => x.Length))
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}

public enum AnsibleReadinessState { Ready, RequiresConfiguration, UnknownError }
public sealed record AnsibleDiagnosticItem(string Stage, bool Success, string Message, string? EnableCommand = null,
    int? ExitCode = null, bool Unknown = false);
public sealed record AnsibleReadinessReport(AnsibleReadinessState State, IReadOnlyList<AnsibleDiagnosticItem> Items);

public static class AnsibleReadinessPolicy
{
    public static AnsibleReadinessReport Summarize(IEnumerable<AnsibleDiagnosticItem> items)
    {
        var all = items.ToArray();
        var state = all.Any(x => x.Unknown) ? AnsibleReadinessState.UnknownError :
            all.All(x => x.Success) ? AnsibleReadinessState.Ready : AnsibleReadinessState.RequiresConfiguration;
        return new(state, all);
    }
}

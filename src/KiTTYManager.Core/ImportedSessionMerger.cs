namespace KiTTYManager.Core;

public enum KittyConflictChoice { Manager, Kitty, Postpone }

public sealed record KittyFieldConflict(
    ManagedServer Server, ManagedServer Imported, string PropertyName,
    string DisplayName, string ManagerValue, string KittyValue, bool IsSecret);

public static class ImportedSessionMerger
{
    private sealed record Field(
        string PropertyName, string DisplayName, bool IsSecret,
        Func<ManagedServer, string> ReadServer, Action<ManagedServer, string> WriteServer,
        Func<KittySessionSnapshot, string> ReadBaseline, Action<KittySessionSnapshot, string> WriteBaseline);

    private static readonly Field[] Fields =
    [
        Text(nameof(ManagedServer.Name), "Название", s => s.Name, (s, v) => s.Name = v, b => b.Name, (b, v) => b.Name = v),
        Text(nameof(ManagedServer.Host), "Адрес", s => s.Host, (s, v) => s.Host = v, b => b.Host, (b, v) => b.Host = v),
        Text(nameof(ManagedServer.Port), "SSH-порт", s => s.Port.ToString(), (s, v) => s.Port = int.Parse(v), b => b.Port.ToString(), (b, v) => b.Port = int.Parse(v)),
        Text(nameof(ManagedServer.Username), "Логин", s => s.Username, (s, v) => s.Username = v, b => b.Username, (b, v) => b.Username = v),
        Text(nameof(ManagedServer.Password), "Пароль", s => s.Password, (s, v) => s.Password = v, b => b.Password, (b, v) => b.Password = v, true),
        Text(nameof(ManagedServer.PrivateKeyPath), "Ключ SSH", s => s.PrivateKeyPath, (s, v) => s.PrivateKeyPath = v, b => b.PrivateKeyPath, (b, v) => b.PrivateKeyPath = v),
        Text(nameof(ManagedServer.UseKeyboardInteractive), "Keyboard-interactive", s => s.UseKeyboardInteractive.ToString(), (s, v) => s.UseKeyboardInteractive = bool.Parse(v), b => b.UseKeyboardInteractive.ToString(), (b, v) => b.UseKeyboardInteractive = bool.Parse(v)),
        Text(nameof(ManagedServer.RootLogin), "Повышение прав", s => s.RootLogin, (s, v) => s.RootLogin = v, b => b.RootLogin, (b, v) => b.RootLogin = v),
        Text(nameof(ManagedServer.RootPassword), "Root-пароль", s => s.RootPassword, (s, v) => s.RootPassword = v, b => b.RootPassword, (b, v) => b.RootPassword = v, true),
        Text(nameof(ManagedServer.ImportedCommand), "Команда KiTTY", s => s.ImportedCommand, (s, v) => s.ImportedCommand = v, b => b.ImportedCommand, (b, v) => b.ImportedCommand = v)
    ];

    public static IReadOnlyList<string> TrackedProperties { get; } = Fields.Select(field => field.PropertyName).ToArray();

    public static string DisplayName(string propertyName) => Fields.Single(field => field.PropertyName == propertyName).DisplayName;
    public static bool IsSecret(string propertyName) => Fields.Single(field => field.PropertyName == propertyName).IsSecret;
    public static string CurrentValue(ManagedServer server, string propertyName) => Fields.Single(field => field.PropertyName == propertyName).ReadServer(server);

    public static IReadOnlyList<KittyFieldConflict> Apply(ManagedServer existing, ManagedServer imported)
    {
        existing.Host = existing.Host.Trim();
        imported.Host = imported.Host.Trim();
        if (existing.KittyBaseline is not null) existing.KittyBaseline.Host = existing.KittyBaseline.Host.Trim();
        var conflicts = new List<KittyFieldConflict>();
        if (existing.KittyBaseline is null)
        {
            existing.KittyBaseline = Snapshot(imported);
            foreach (var field in Fields)
            {
                if (SkipUndecodablePassword(field, imported)) continue;
                var managerValue = field.ReadServer(existing);
                var kittyValue = field.ReadServer(imported);
                if (managerValue == kittyValue) continue;
                // The old build did not retain a KiTTY baseline, so it is
                // impossible to infer which side changed. Preserve both and ask.
                field.WriteBaseline(existing.KittyBaseline, managerValue);
                conflicts.Add(new(existing, imported, field.PropertyName, field.DisplayName,
                    managerValue, kittyValue, field.IsSecret));
            }
        }
        else
        {
            foreach (var field in Fields)
            {
                if (SkipUndecodablePassword(field, imported)) continue;
                var baselineValue = field.ReadBaseline(existing.KittyBaseline);
                var managerValue = field.ReadServer(existing);
                var kittyValue = field.ReadServer(imported);
                if (kittyValue == baselineValue) continue;
                if (managerValue == kittyValue)
                {
                    field.WriteBaseline(existing.KittyBaseline, kittyValue);
                    existing.ManagerOverrides.RemoveAll(value => value == field.PropertyName);
                }
                else
                    conflicts.Add(new(existing, imported, field.PropertyName, field.DisplayName,
                        managerValue, kittyValue, field.IsSecret));
            }
        }

        RefreshMetadata(existing, imported);
        return conflicts;
    }

    private static bool SkipUndecodablePassword(Field field, ManagedServer imported) =>
        field.PropertyName == nameof(ManagedServer.Password) &&
        imported.PasswordImportState == ImportedCredentialState.PresentButUndecodable;

    public static void Resolve(KittyFieldConflict conflict, KittyConflictChoice choice)
    {
        if (choice == KittyConflictChoice.Postpone) return;
        var field = Fields.Single(item => item.PropertyName == conflict.PropertyName);
        var baseline = conflict.Server.KittyBaseline ??= Snapshot(conflict.Imported);
        if (choice == KittyConflictChoice.Kitty)
        {
            field.WriteServer(conflict.Server, conflict.KittyValue);
            conflict.Server.ManagerOverrides.RemoveAll(value => value == field.PropertyName);
        }
        else if (!conflict.Server.ManagerOverrides.Contains(field.PropertyName, StringComparer.Ordinal))
            conflict.Server.ManagerOverrides.Add(field.PropertyName);
        field.WriteBaseline(baseline, conflict.KittyValue);
    }

    public static string? BaselineValue(ManagedServer server, string propertyName)
    {
        var field = Fields.FirstOrDefault(item => item.PropertyName == propertyName);
        return server.KittyBaseline is null || field is null ? null : field.ReadBaseline(server.KittyBaseline);
    }

    public static void MarkManagerApplied(ManagedServer server, string propertyName)
    {
        var field = Fields.Single(item => item.PropertyName == propertyName);
        var baseline = server.KittyBaseline ??= Snapshot(server);
        field.WriteBaseline(baseline, field.ReadServer(server));
        server.ManagerOverrides.RemoveAll(value => value == propertyName);
    }

    public static void RejectManagerChange(ManagedServer server, string propertyName)
    {
        var field = Fields.Single(item => item.PropertyName == propertyName);
        if (server.KittyBaseline is null) return;
        field.WriteServer(server, field.ReadBaseline(server.KittyBaseline));
        server.ManagerOverrides.RemoveAll(value => value == propertyName);
    }

    private static void RefreshMetadata(ManagedServer existing, ManagedServer imported)
    {
        existing.SourceSessionPath = imported.SourceSessionPath;
        existing.SourceScriptPath = imported.SourceScriptPath;
        existing.SourceScriptContent = imported.SourceScriptContent;
        existing.ImportedProxy = imported.ImportedProxy;
    }

    public static KittySessionSnapshot Snapshot(ManagedServer server) => new()
    {
        Name = server.Name, Host = server.Host, Port = server.Port, Username = server.Username,
        Password = server.Password, PrivateKeyPath = server.PrivateKeyPath,
        UseKeyboardInteractive = server.UseKeyboardInteractive, RootLogin = server.RootLogin,
        RootPassword = server.RootPassword, ImportedCommand = server.ImportedCommand
    };

    public static void Restore(ManagedServer server, KittySessionSnapshot snapshot)
    {
        foreach (var field in Fields) field.WriteServer(server, field.ReadBaseline(snapshot));
    }

    private static Field Text(string propertyName, string displayName,
        Func<ManagedServer, string> readServer, Action<ManagedServer, string> writeServer,
        Func<KittySessionSnapshot, string> readBaseline, Action<KittySessionSnapshot, string> writeBaseline,
        bool isSecret = false) =>
        new(propertyName, displayName, isSecret, readServer, writeServer, readBaseline, writeBaseline);
}

using KiTTYManager.Core;
using KiTTYManager.App;
using System.Diagnostics;
using System.Text;

internal sealed partial class SelfTestRunner
{
    private static void Version2Metadata()
    {
        Equal("2.0.0", ProductInfo.Version);
        Equal(9, new ManagerConfig().SchemaVersion);
        Equal(1, new ManagerConfig().TaskConnectionRecoveryMinutes);
    }

    private static void Version2ConnectionTimeoutMinimum()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{\"SchemaVersion\":8,\"ConnectionTimeoutSeconds\":1}");
            Equal(3, ConfigStore.Load(path).ConnectionTimeoutSeconds);
            File.WriteAllText(path, "{\"SchemaVersion\":8,\"ConnectionTimeoutSeconds\":10}");
            Equal(10, ConfigStore.Load(path).ConnectionTimeoutSeconds);
            File.WriteAllText(path, "{\"SchemaVersion\":9,\"TaskConnectionRecoveryMinutes\":100000}");
            Equal(99999, ConfigStore.Load(path).TaskConnectionRecoveryMinutes);
        }
        finally { File.Delete(path); }
    }

    private static void Version2SchemaMigration()
    {
        var server = new ManagedServer { Name = "kept", Host = "host" };
        var config = new ManagerConfig { SchemaVersion = 7, UngroupedServers = [server], ConnectionTimeoutSeconds = 60 };
        Equal(true, ManagerConfigMigration.UpgradeToVersion8(config));
        Equal(8, config.SchemaVersion);
        Equal(server.Id, config.UngroupedServers.Single().Id);
        Equal(60, config.ConnectionTimeoutSeconds);
        Equal(false, ManagerConfigMigration.UpgradeToVersion8(config));
    }

    private static void WinScpRouteArguments()
    {
        var server = new ManagedServer
        {
            Username = "user name", Password = "top-secret",
            HostKeyFingerprint = "SHA256:YWJj",
            HostKeyAlgorithm = "ssh-ed25519",
            HostKeyBits = 255
        };
        var args = WinScpLaunchPlan.BuildArguments(server, 23456, server.Password, server.PrivateKeyPassphrase);
        Equal(true, args[0].Contains("user%20name@127.0.0.1:23456", StringComparison.Ordinal));
        Equal(true, args.Contains("/hostkey=ssh-ed25519 255 YWJj"));
        Equal(true, args.Contains("/password=top-secret"));
    }

    private static void WinScpLegacyHostKeyArgument()
    {
        // WinSCP требует полный формат «алгоритм биты SHA256:base64».
        // Без алгоритма/бит ключ не передаётся — WinSCP сам запросит подтверждение.
        var server = new ManagedServer { HostKeyFingerprint = "SHA256:abc" };
        Equal("", WinScpLaunchPlan.FormatHostKey(server));
        server.HostKeyFingerprint = "aa:bb:cc";
        Equal("", WinScpLaunchPlan.FormatHostKey(server));
        server.HostKeyFingerprint = "SHA256:abc";
        server.HostKeyAlgorithm = "ssh-ed25519";
        server.HostKeyBits = 255;
        Equal("ssh-ed25519 255 abc=", WinScpLaunchPlan.FormatHostKey(server));
        // ed25519: 256 бит нормализуется в 255 (WinSCP ожидает 255).
        server.HostKeyBits = 256;
        Equal("ssh-ed25519 255 abc=", WinScpLaunchPlan.FormatHostKey(server));
        var padded = new ManagedServer { HostKeyFingerprint = "SHA256:abcd" };
        Equal("", WinScpLaunchPlan.FormatHostKey(padded));
        padded.HostKeyAlgorithm = "ssh-rsa";
        padded.HostKeyBits = 2048;
        Equal("ssh-rsa 2048 abcd", WinScpLaunchPlan.FormatHostKey(padded));
        padded.HostKeyAlgorithm = "ecdsa-sha2-nistp256";
        padded.HostKeyBits = 256;
        Equal("ecdsa-sha2-nistp256 256 abcd", WinScpLaunchPlan.FormatHostKey(padded));
    }

    private static void MissingJumphostOffer()
    {
        var readyServer = new ManagedServer { Name = "Работающая сессия" };
        var missingServer = new ManagedServer { Name = "Точка ЦОД" };
        var ready = new BaseProxy { Enabled = true, Name = "Jumphost", StartupServerId = readyServer.Id };
        var missing = new BaseProxy { Enabled = true, Name = "Jumphost", StartupServerId = missingServer.Id };
        var external = new BaseProxy { Enabled = true, StartupServerId = null };
        var disabled = new BaseProxy { Enabled = false, StartupServerId = Guid.NewGuid() };
        var config = new ManagerConfig
        {
            UngroupedServers = [readyServer, missingServer],
            BaseProxies = [ready, missing, external, disabled]
        };
        var result = JumphostStartupOfferPolicy.MissingConfigured(config, new HashSet<Guid> { ready.Id });
        Equal(1, result.Count);
        Equal(missing.Id, result[0].Id);
        Equal("Точка ЦОД", JumphostStartupOfferPolicy.DisplayName(config, missing));
        Equal("Jumphost", JumphostStartupOfferPolicy.DisplayName(config,
            new BaseProxy { Name = "Jumphost", StartupServerId = Guid.NewGuid() }));
    }

    private static void SmartImportMatching()
    {
        var existing = new ManagedServer { Name = "Local", Host = "host", Port = 22, Username = "user", Password = "one" };
        var current = new ManagerConfig { UngroupedServers = [existing] };
        var sameId = new ManagedServer { Id = existing.Id, Name = "Renamed", Host = "other", Username = "x" };
        var sameEndpoint = new ManagedServer { Name = "Foreign", Host = "host", Port = 22, Username = "user", Password = "two" };
        var different = new ManagedServer { Name = "New", Host = "host", Port = 2200, Username = "user" };
        var incoming = new ManagerConfig { UngroupedServers = [sameId, sameEndpoint, different] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        Equal(ImportMatchConfidence.Exact, plan.Sessions[0].Confidence);
        Equal(ImportMatchConfidence.Probable, plan.Sessions[1].Confidence);
        Equal(ImportMatchConfidence.None, plan.Sessions[2].Confidence);
    }

    private static void SmartImportIsNonDestructive()
    {
        var a = new ManagedServer { Name = "A", Host = "a", Username = "u" };
        var b = new ManagedServer { Name = "B", Host = "b", Username = "u" };
        var localLink = new ServerLink { FromServerId = a.Id, ToServerId = b.Id, LastStrategy = "local" };
        var current = new ManagerConfig { UngroupedServers = [a, b], Links = [localLink] };
        var incomingA = new ManagedServer { Id = a.Id, Name = "A2", Host = "a", Username = "u" };
        var incomingB = new ManagedServer { Id = b.Id, Name = "B2", Host = "b", Username = "u" };
        var incoming = new ManagerConfig
        {
            UngroupedServers = [incomingA, incomingB],
            Links = [new() { FromServerId = a.Id, ToServerId = b.Id, LastStrategy = "incoming" }]
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        Equal(2, merged.AllServers().Count());
        Equal(1, merged.Links.Count);
        Equal("local", merged.Links[0].LastStrategy);
    }

    private static void SmartImportKeepsCurrentProxyReferences()
    {
        var localServer = new ManagedServer { Name = "Local", Host = "local", Username = "user" };
        var localProxy = new BaseProxy
        {
            Name = "Local JH", Host = "jump", Port = 22,
            AccessProbeServerIds = [localServer.Id], StartupServerId = localServer.Id
        };
        var incomingProxy = new BaseProxy { Name = "Incoming JH", Host = "jump", Port = 22 };
        var current = new ManagerConfig { UngroupedServers = [localServer], BaseProxies = [localProxy] };
        var incoming = new ManagerConfig { BaseProxies = [incomingProxy] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        Equal(ImportDecision.KeepCurrent, plan.Proxies.Single().Decision);
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        Equal(localServer.Id, merged.BaseProxies.Single().StartupServerId);
        Equal(localServer.Id, merged.BaseProxies.Single().AccessProbeServerIds.Single());
    }

    private static void SmartImportClearsSkippedReferences()
    {
        var skippedServer = new ManagedServer { Name = "Skipped", Host = "skip", Username = "u" };
        var skippedProxy = new BaseProxy { Name = "Skipped JH", Host = "skip-jh", Port = 1080 };
        var addedServer = new ManagedServer
        {
            Name = "Added", Host = "added", Username = "u",
            RequiredPreviousServerId = skippedServer.Id, PreferredProxyId = skippedProxy.Id
        };
        var addedProxy = new BaseProxy
        {
            Name = "Added JH", Host = "added-jh", Port = 1080,
            StartupServerId = skippedServer.Id, AccessProbeServerIds = [skippedServer.Id]
        };
        var incoming = new ManagerConfig
        {
            UngroupedServers = [skippedServer, addedServer], BaseProxies = [skippedProxy, addedProxy]
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(new ManagerConfig(), incoming);
        plan.Sessions.Single(x => x.IncomingId == skippedServer.Id).Decision = ImportDecision.Skip;
        plan.Proxies.Single(x => x.IncomingId == skippedProxy.Id).Decision = ImportDecision.Skip;
        var merged = ConfigTransfer.MergeSmartImport(new ManagerConfig(), incoming, plan);
        var server = merged.AllServers().Single();
        var proxy = merged.BaseProxies.Single();
        Equal<Guid?>(null, server.RequiredPreviousServerId);
        Equal<Guid?>(null, server.PreferredProxyId);
        Equal<Guid?>(null, proxy.StartupServerId);
        Equal(0, proxy.AccessProbeServerIds.Count);
    }

    private static void SmartImportRefreshesManualMapping()
    {
        var currentA = new ManagedServer { Name = "A", Host = "same", Username = "u", Password = "a" };
        var currentB = new ManagedServer { Name = "B", Host = "same", Username = "u", Password = "b" };
        var currentTarget = new ManagedServer { Name = "Target", Host = "target", Username = "u" };
        var incomingA = new ManagedServer { Name = "Imported", Host = "same", Username = "u", Password = "new" };
        var incomingTarget = new ManagedServer
            { Id = currentTarget.Id, Name = "Target", Host = "target", Username = "u" };
        var current = new ManagerConfig
        {
            UngroupedServers = [currentA, currentB, currentTarget],
            Links = [new() { FromServerId = currentB.Id, ToServerId = currentTarget.Id, LastStrategy = "local" }]
        };
        var incoming = new ManagerConfig
        {
            UngroupedServers = [incomingA, incomingTarget],
            Links = [new() { FromServerId = incomingA.Id, ToServerId = incomingTarget.Id, LastStrategy = "incoming" }]
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var row = plan.Sessions.Single(x => x.IncomingId == incomingA.Id);
        Equal<Guid?>(null, row.CurrentId);
        row.CurrentId = currentB.Id;
        row.Decision = ImportDecision.KeepCurrent;
        ImportWizardEngine.RefreshSessionDependencies(current, incoming, plan);
        Equal(true, plan.Fields.Any(x => x.IncomingSessionId == incomingA.Id && x.PropertyName == "Password"));
        var nameField = plan.Fields.Single(x => x.IncomingSessionId == incomingA.Id && x.PropertyName == "Name");
        Equal("B", nameField.CurrentValue);
        Equal("Imported", nameField.IncomingValue);
        var link = plan.Links.Single();
        Equal(true, link.ExistingConflict);
        Equal(ImportDecision.KeepCurrent, link.Decision);
        Equal(true, link.Reason.Contains("Конфликт связи", StringComparison.Ordinal));

        row.Decision = ImportDecision.Add;
        ImportWizardEngine.RefreshSessionDependencies(current, incoming, plan);
        Equal(false, plan.Links.Single().ExistingConflict);
        Equal(ImportDecision.Add, plan.Links.Single().Decision);
    }

    private static void SmartImportRejectsConflictingGroupRenames()
    {
        var currentId = Guid.NewGuid();
        var plan = new ImportWizardPlan
        {
            Groups =
            [
                new() { IncomingId = Guid.NewGuid(), IncomingPath = "Импорт A", CurrentId = currentId, Merge = true, UseIncomingName = true },
                new() { IncomingId = Guid.NewGuid(), IncomingPath = "Импорт B", CurrentId = currentId, Merge = true, UseIncomingName = true }
            ]
        };
        ExpectInvalidData(() => ImportWizardEngine.ValidateGroupChoices(plan));
        plan.Groups[1].UseIncomingName = false;
        ImportWizardEngine.ValidateGroupChoices(plan);
    }

    private static void SmartImportSelectedFieldsAndGroup()
    {
        var local = new ManagedServer { Name = "Local", Host = "old", Username = "user", Password = "keep" };
        var imported = new ManagedServer
        {
            Name = "Imported", Host = "new", Username = "user", Password = "replace", Id = local.Id,
            HostKeyFingerprint = "SHA256:a2V5", HostKeyAlgorithm = "ssh-ed25519", HostKeyBits = 255
        };
        var current = new ManagerConfig { UngroupedServers = [local] };
        var incoming = new ManagerConfig { Groups = [new() { Name = "Imported group", Servers = [imported] }] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        plan.Sessions.Single().UseIncomingGroup = true;
        // По умолчанию все поля отмечены; снимаем те, которые не хотим заменять.
        plan.Fields.Single(x => x.PropertyName == "Host").UseIncoming = false;
        plan.Fields.Single(x => x.PropertyName == "Password").UseIncoming = false;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        var result = merged.Groups.Single().Servers.Single();
        Equal("Imported", result.Name);
        Equal("old", result.Host);
        Equal("keep", result.Password);
        Equal("SHA256:a2V5", result.HostKeyFingerprint);
        Equal("ssh-ed25519", result.HostKeyAlgorithm);
        Equal(255, result.HostKeyBits);
        Equal(false, plan.Fields.Single(x => x.PropertyName == "Password").CurrentValue.Contains("keep"));
    }

    private static void SmartImportSimilarGroups()
    {
        var currentServer = new ManagedServer { Name = "Local", Host = "same", Port = 22, Username = "user" };
        var currentGroup = new ServerGroup { Name = "Текущее имя", Servers = [currentServer] };
        var incomingServer = new ManagedServer { Name = "Foreign", Host = "same", Port = 22, Username = "user" };
        var incomingGroup = new ServerGroup { Name = "Другое имя", Servers = [incomingServer] };
        var plan = ConfigTransfer.AnalyzeSmartImport(
            new ManagerConfig { Groups = [currentGroup] }, new ManagerConfig { Groups = [incomingGroup] });
        Equal(currentGroup.Id, plan.Groups.Single().CurrentId);
        Equal(1, plan.Groups.Single().MatchedSessions);
        Equal(true, plan.Groups.Single().Merge);
    }

    private static void SmartImportAmbiguousSession()
    {
        var first = new ManagedServer { Name = "A", Host = "same", Port = 22, Username = "user" };
        var second = new ManagedServer { Name = "B", Host = "same", Port = 22, Username = "user" };
        var incoming = new ManagedServer { Name = "C", Host = "same", Port = 22, Username = "user" };
        var plan = ConfigTransfer.AnalyzeSmartImport(
            new ManagerConfig { UngroupedServers = [first, second] },
            new ManagerConfig { UngroupedServers = [incoming] });
        var row = plan.Sessions.Single();
        Equal<Guid?>(null, row.CurrentId);
        Equal(ImportDecision.Skip, row.Decision);
        Equal(2, row.Candidates.Count);
    }

    private static void SmartImportBackupRollback()
    {
        var path = TempFile();
        try
        {
            ConfigStore.Save(path, new ManagerConfig { WinScpPath = "before" });
            var original = File.ReadAllBytes(path);
            var backup = ImportWizardTransaction.SaveWithBackup(path,
                new ManagerConfig { WinScpPath = "after" });
            Equal(true, File.Exists(backup));
            ImportWizardTransaction.Rollback(path, backup);
            Equal(true, original.SequenceEqual(File.ReadAllBytes(path)));
            File.Delete(backup);
        }
        finally { File.Delete(path); }
    }

    private static void BatchTaskRoundTripAndRedaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kitty-task-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.yaml");
        try
        {
            var task = new BatchTaskDefinition
            {
                Name = "test",
                Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "command", Command = "true" }]
            };
            BatchTaskFile.Save(path, task);
            Equal("test", BatchTaskFile.Load(path).Name);
            var server = new ManagedServer { Password = "secret", RootPassword = "root-secret" };
            Equal("*** and ***", SecretRedactor.Redact("secret and root-secret", server));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void BatchTaskExecutionContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kitty-task-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "task.yaml");
            File.WriteAllText(path, "{\"name\":\"old\",\"steps\":[{\"kind\":0,\"name\":\"run\",\"command\":\"./script.sh\"}]}");
            var old = BatchTaskFile.Load(path);
            Equal("", old.WorkingDirectory);
            Equal(BatchPrivilegeMode.SessionUser, old.PrivilegeMode);

            old.WorkingDirectory = "/opt/my app";
            old.PrivilegeMode = BatchPrivilegeMode.AlwaysBecome;
            BatchTaskFile.Save(path, old);
            var loaded = BatchTaskFile.Load(path);
            Equal("/opt/my app", loaded.WorkingDirectory);
            Equal(BatchPrivilegeMode.AlwaysBecome, loaded.PrivilegeMode);

            var server = new ManagedServer();
            Equal("if [ -d '/opt/my app' ]; then cd -- '/opt/my app' && ./script.sh; " +
                "else printf '%s\\n' 'Рабочая папка отсутствует или недоступна на сервере: /opt/my app' >&2; exit 126; fi",
                BatchTaskRunner.BuildTaskCommand("./script.sh", "/opt/my app", false, server));
            ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
            {
                Name = "bad", WorkingDirectory = "/tmp\nrm", Steps = [new() { Name = "x", Command = "true" }]
            }));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void BatchTaskValidation()
    {
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "bad", Steps = [new() { Kind = BatchTaskStepKind.ReplaceText, Name = "replace", Destination = "/x" }]
        }));
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "bad", Steps = [new() { Kind = BatchTaskStepKind.MakeDirectory, Name = "mkdir" }]
        }));
    }

    private static void BatchTunnelRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kitty-tunnel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.yaml");
        try
        {
            var task = new BatchTaskDefinition
            {
                Name = "tunnels", Steps = [], AutoReconnectTunnels = true,
                Tunnels =
                [
                    new() { Name = "remote", Kind = BatchTunnelKind.Remote, BindHost = "127.0.0.1", BindPort = 8443, DestinationHost = "example.com", DestinationPort = 443 },
                    new() { Name = "local", Kind = BatchTunnelKind.Local, BindHost = "127.0.0.1", BindPort = 9443, DestinationHost = "127.0.0.1", DestinationPort = 443 }
                ]
            };
            BatchTaskFile.Save(path, task);
            var loaded = BatchTaskFile.Load(path);
            Equal(2, loaded.Tunnels.Count);
            Equal(true, loaded.AutoReconnectTunnels);
            using var remote = BatchTunnelPolicy.Create(loaded.Tunnels[0]);
            using var local = BatchTunnelPolicy.Create(loaded.Tunnels[1]);
            Equal(true, remote is Renci.SshNet.ForwardedPortRemote);
            Equal(true, local is Renci.SshNet.ForwardedPortLocal);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void BatchTunnelServerSelection()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var all = new BatchTunnelDefinition();
        var selected = new BatchTunnelDefinition { ServerIds = [first] };
        Equal(true, BatchTunnelPolicy.AppliesTo(all, first));
        Equal(true, BatchTunnelPolicy.AppliesTo(all, second));
        Equal(true, BatchTunnelPolicy.AppliesTo(selected, first));
        Equal(false, BatchTunnelPolicy.AppliesTo(selected, second));
    }

    private static void BatchLocalTunnelSingleServer()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var task = new BatchTaskDefinition { Name = "local", Tunnels = [new()
        {
            Name = "local", Kind = BatchTunnelKind.Local, BindPort = 9000,
            DestinationHost = "localhost", DestinationPort = 80, ServerIds = [first]
        }] };
        BatchTunnelPolicy.ValidateRunSelection(task, [first, second]);
        ExpectInvalidData(() => BatchTunnelPolicy.ValidateRunSelection(task, [second]));
        task.Tunnels[0].ServerIds = [];
        ExpectInvalidData(() => BatchTunnelPolicy.ValidateRunSelection(task, [first, second]));
    }

    private static void BatchTunnelValidation()
    {
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "bad", Tunnels = [new() { BindPort = 0, DestinationHost = "example.com", DestinationPort = 443 }]
        }));
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "duplicate", Tunnels =
            [
                new() { Name = "one", BindPort = 8443, DestinationHost = "one", DestinationPort = 443 },
                new() { Name = "two", BindPort = 8443, DestinationHost = "two", DestinationPort = 443 }
            ]
        }));
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "separate", Tunnels =
            [
                new() { Name = "one", BindPort = 8443, DestinationHost = "one", DestinationPort = 443, ServerIds = [first] },
                new() { Name = "two", BindPort = 8443, DestinationHost = "two", DestinationPort = 443, ServerIds = [second] }
            ]
        });
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "local conflict", Tunnels =
            [
                new() { Name = "one", Kind = BatchTunnelKind.Local, BindPort = 8443, DestinationHost = "one", DestinationPort = 443, ServerIds = [first] },
                new() { Name = "two", Kind = BatchTunnelKind.Local, BindPort = 8443, DestinationHost = "two", DestinationPort = 443, ServerIds = [second] }
            ]
        }));
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "remote wildcard conflict", Tunnels =
            [
                new() { Name = "one", Kind = BatchTunnelKind.Remote, BindHost = "0.0.0.0", BindPort = 8443, DestinationHost = "one", DestinationPort = 443, ServerIds = [first] },
                new() { Name = "two", Kind = BatchTunnelKind.Remote, BindHost = "127.0.0.1", BindPort = 8443, DestinationHost = "two", DestinationPort = 443, ServerIds = [first] }
            ]
        }));
    }

    private static void BatchTunnelLogViews()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var logs = new[]
        {
            new BatchTaskLog(DateTimeOffset.UnixEpoch, first, "A", "tunnel", "Работает", BatchLogLevel.Info, "Туннель"),
            new BatchTaskLog(DateTimeOffset.UnixEpoch, second, "B", "step", "Ошибка", BatchLogLevel.Error, "Шаг")
        };
        Equal(1, BatchLogFormatter.Filter(logs, first, null).Count());
        Equal(1, BatchLogFormatter.Filter(logs, null, BatchLogLevel.Error).Count());
        var text = BatchLogFormatter.Format(logs[0]);
        Equal(true, text.Contains("A | Туннель | tunnel | Работает", StringComparison.Ordinal));
    }

    private static void BatchTaskSftpPolicy()
    {
        Equal(false, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.Command }]));
        Equal(false, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.MakeDirectory }]));
        Equal(true, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.Upload }]));
        Equal(true, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.Download }]));
        Equal(true, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.ReplaceText }]));
        Equal(true, BatchTaskPolicy.NeedsSftp([new() { Kind = BatchTaskStepKind.Template }]));
    }

    private static void BatchTaskTemplateRendering()
    {
        var server = new ManagedServer { Name = "node", Host = "host", Port = 2222, Username = "user" };
        Equal("node host 2222 user", BatchTemplateRenderer.Render(
            "{{server.name}} {{server.host}} {{server.port}} {{server.username}}", server));
    }

    private static void BatchTaskConfigSecretRedaction()
    {
        var server = new ManagedServer
        {
            Password = "ssh-secret", WebInterfaces = [new() { Password = "web-secret" }]
        };
        var config = new ManagerConfig
        {
            UngroupedServers = [server], BaseProxies = [new() { TotpSecret = "totp-secret" }]
        };
        Equal("*** *** ***", SecretRedactor.Redact("ssh-secret web-secret totp-secret", server,
            SecretRedactor.Secrets(config)));
    }

    private static void BatchTaskOutputMatcher()
    {
        var matcher = new BatchOutputMatcher("READY");
        Equal(false, matcher.Append("boot RE"));
        Equal(true, matcher.Append("ADY now"));
        Equal(false, new BatchOutputMatcher("READY").Append("ready"));
        Equal(true, BatchOutputTrigger.Contains("x READY y", "READY"));
    }

    private static void BatchInteractiveOutputLifecycle()
    {
        using var output = new BatchInteractiveOutputCoordinator();
        output.BeginInteraction();
        output.Feed("prompt RE");
        var buffered = output.WaitForAsync("READY", TimeSpan.FromSeconds(1), CancellationToken.None);
        output.Feed("ADY");
        buffered.GetAwaiter().GetResult();

        output.BeginInteraction();
        output.Feed("FAST");
        output.WaitForAsync("FAST", TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();

        output.BeginInteraction();
        var stale = output.WaitForAsync("FAST", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        try { stale.GetAwaiter().GetResult(); throw new Exception("Старый вывод не должен повторно срабатывать."); }
        catch (TimeoutException) { }

        using var cancelled = new CancellationTokenSource();
        var pending = output.WaitForAsync("NEVER", TimeSpan.FromSeconds(10), cancelled.Token);
        cancelled.Cancel();
        try { pending.GetAwaiter().GetResult(); throw new Exception("Ожидалась отмена."); }
        catch (OperationCanceledException) { }

        try { output.WaitForAsync("NEVER", TimeSpan.FromMilliseconds(10), CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Ожидался таймаут."); }
        catch (TimeoutException) { }
    }

    private static void BatchTerminalOutputIsReadable()
    {
        var raw = "\u001b]0;operator@host: /srv\a\u001b[92mГотово\u001b[0m\r\n" +
                  "printf '\\137\\137\\113\\111\\124\\124\\131\\137'\r\n" +
                  "__KITTY_MANAGER_DONE_deadbeef__\r\nСледующая строка";
        var clean = BatchTerminalOutputSanitizer.Sanitize(raw);
        Equal(true, clean.Contains("Готово", StringComparison.Ordinal));
        Equal(true, clean.Contains("Следующая строка", StringComparison.Ordinal));
        Equal(false, clean.Contains('\u001b'));
        Equal(false, clean.Contains("printf", StringComparison.Ordinal));
        Equal(false, clean.Contains("KITTY_MANAGER_DONE", StringComparison.Ordinal));

        for (var split = 1; split < raw.Length; split++)
        {
            var session = new BatchTerminalOutputSanitizerSession();
            var streamed = session.Feed(raw[..split]) + session.Feed(raw[split..]) + session.Complete();
            Equal(false, streamed.Contains('\u001b'));
            Equal(false, streamed.Contains("printf", StringComparison.Ordinal));
            Equal(false, streamed.Contains("KITTY_MANAGER_DONE", StringComparison.Ordinal));
            Equal(true, streamed.Contains("Готово", StringComparison.Ordinal));
            Equal(true, streamed.Contains("Следующая строка", StringComparison.Ordinal));
        }

        var promptSession = new BatchTerminalOutputSanitizerSession();
        Equal("Password:", promptSession.Feed("Password:"));
        Equal("", promptSession.Complete());

        const string charsetPrompt = "\u001b(BPassword:";
        for (var split = 1; split < charsetPrompt.Length; split++)
        {
            var session = new BatchTerminalOutputSanitizerSession();
            Equal("Password:", session.Feed(charsetPrompt[..split]) + session.Feed(charsetPrompt[split..]));
        }
    }

    private static void BatchInteractivePromptAndCompletionMarker()
    {
        var matcher = new BatchOutputMatcher("Логины:");
        Equal(false, matcher.Append("Подсказка про логины без нужного знака"));
        Equal(true, matcher.Append("\nЛогины:"));

        var marker = BatchInteractiveCompletionMarker.Create();
        var command = BatchInteractiveCompletionMarker.BuildCommand(marker);
        Equal(false, command.Contains(marker, StringComparison.Ordinal));
        Equal(true, command.StartsWith("printf '", StringComparison.Ordinal));
        var server = Server("interactive");
        var launch = BatchTaskRunner.BuildInteractiveTaskCommand(new()
            { Kind = BatchTaskStepKind.InteractiveWaitAndSend, Command = "./restart" },
            "/opt/app", false, server, marker);
        Equal(true, launch.Command.StartsWith("cd -- '/opt/app' && ./restart; printf '", StringComparison.Ordinal));
        server.RootLogin = "sudo -i"; server.RootPassword = "secret";
        var privileged = BatchTaskRunner.BuildInteractiveTaskCommand(new()
            { Kind = BatchTaskStepKind.InteractiveWaitAndSend, Command = "./restart" },
            "/opt/app", true, server, marker);
        Equal(true, privileged.Command.StartsWith("sudo -k -S -p '__KITTY_MANAGER_SUDO_PASSWORD__'", StringComparison.Ordinal));
        Equal("__KITTY_MANAGER_SUDO_PASSWORD__", privileged.PasswordPrompt);
        Equal("secret", privileged.Password);

        server.RootLogin = "su -"; server.RootPassword = "rootpassword";
        var suPrivileged = BatchTaskRunner.BuildInteractiveTaskCommand(new()
            { Kind = BatchTaskStepKind.InteractiveWaitAndSend, Command = "./restart" },
            "/opt/app", true, server, marker);
        Equal(true, suPrivileged.Command.StartsWith("su - -c '", StringComparison.Ordinal));
        Equal("Password:", suPrivileged.PasswordPrompt);
        Equal("rootpassword", suPrivileged.Password);

        var passMatcher = new BatchOutputMatcher("Password:");
        Equal(true, passMatcher.Append("Password: "));
        Equal(true, passMatcher.Append("password:"));
        Equal(true, passMatcher.Append("Пароль: "));

        using var coordinator = new BatchInteractiveOutputCoordinator();
        var timedOut = false;
        try
        {
            coordinator.WaitForAsync("marker", TimeSpan.FromMilliseconds(20), CancellationToken.None,
                "интерактивная программа не завершилась").GetAwaiter().GetResult();
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            Equal(true, ex.Message.Contains("интерактивная программа не завершилась", StringComparison.Ordinal));
        }
        Equal(true, timedOut);
    }

    private static void BatchInteractiveIgnoresGlobalBecome()
    {
        Equal(false, BatchTaskPolicy.EffectiveBecome(BatchPrivilegeMode.AlwaysBecome,
            new BatchTaskStep { Kind = BatchTaskStepKind.InteractiveSend, Become = true }));
        Equal(true, BatchTaskPolicy.EffectiveBecome(BatchPrivilegeMode.AlwaysBecome,
            new BatchTaskStep { Kind = BatchTaskStepKind.InteractiveWaitAndSend, Become = true }));
        Equal(true, BatchTaskPolicy.EffectiveBecome(BatchPrivilegeMode.AlwaysBecome,
            new BatchTaskStep { Kind = BatchTaskStepKind.Command }));
        Equal(true, BatchTaskPolicy.EffectiveBecome(BatchPrivilegeMode.SessionUser,
            new BatchTaskStep { Kind = BatchTaskStepKind.Command, Become = true }));
    }

    private static void BatchTaskWaitValidation()
    {
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "bad", Steps = [new() { Kind = BatchTaskStepKind.WaitForText, Name = "wait", Command = "watch", ExpectedText = "READY" }]
        }));
        ExpectInvalidData(() => BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "bad", Steps = [new() { Kind = BatchTaskStepKind.WaitForText, Name = "wait", Command = "watch", ExpectedText = "READY", ActionCommand = "go", TimeoutSeconds = 0 }]
        }));
        BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "ok", Steps = [new() { Kind = BatchTaskStepKind.WaitForText, Name = "wait", Command = "watch", ExpectedText = "READY", ActionCommand = "go", TimeoutSeconds = 30 }]
        });
        BatchTaskFile.Validate(new BatchTaskDefinition
        {
            Name = "interactive", Steps =
            [
                new() { Kind = BatchTaskStepKind.InteractiveSend, Name = "send", Command = "configure" },
                new() { Kind = BatchTaskStepKind.InteractiveWaitAndSend, Name = "reply", ExpectedText = "Password:", ActionCommand = "answer", TimeoutSeconds = 30 }
            ]
        });
    }

    private static void BatchTaskTemplateStoreRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-template-store-" + Guid.NewGuid().ToString("N"));
        var taskDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(taskDirectory);
        try
        {
            var legacy = Path.Combine(root, "Data", "Templates");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "legacy.kmtask"), "legacy");
            BatchTaskTemplateStore.MigrateLegacyDirectory(root);
            Equal(true, File.Exists(Path.Combine(root, "Data", "Tasks", "legacy.kmtask")));
            Equal(false, Directory.Exists(legacy));
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "legacy.kmtask"), "second");
            BatchTaskTemplateStore.MigrateLegacyDirectory(root);
            Equal("second", File.ReadAllText(Path.Combine(root, "Data", "Tasks", "legacy (legacy 1).kmtask")));
            File.Delete(Path.Combine(root, "Data", "Tasks", "legacy (legacy 1).kmtask"));
            File.Delete(Path.Combine(root, "Data", "Tasks", "legacy.kmtask"));
            var task = new BatchTaskDefinition
            {
                Name = "Safe template", Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "command", Command = "true" }]
            };
            var package = BatchTaskTemplateStore.Save(root, taskDirectory, task);
            Equal(true, File.Exists(package));
            Equal(1, BatchTaskTemplateStore.List(root).Count);
            var imported = Path.Combine(root, "imported");
            Equal("Safe template", BatchTaskFile.Load(BatchTaskFile.ImportPackage(package, imported)).Name);
            BatchTaskTemplateStore.Delete(root, package);
            Equal(0, BatchTaskTemplateStore.List(root).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchTaskTemplateRename()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-template-rename-" + Guid.NewGuid().ToString("N"));
        var taskDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(taskDirectory);
        try
        {
            var task = new BatchTaskDefinition
            {
                Name = "Old name", Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "command", Command = "true" }]
            };
            var package = BatchTaskTemplateStore.Save(root, taskDirectory, task);
            var renamed = BatchTaskTemplateStore.Rename(root, package, "New name");
            Equal(false, File.Exists(package));
            Equal(true, File.Exists(renamed));
            Equal("New name", Path.GetFileNameWithoutExtension(renamed));
            var imported = Path.Combine(root, "imported");
            Equal("New name", BatchTaskFile.Load(BatchTaskFile.ImportPackage(renamed, imported)).Name);
            // Повторное имя того же файла — no-op, чужое занятое имя — ошибка.
            Equal(renamed, BatchTaskTemplateStore.Rename(root, renamed, "New name"));
            BatchTaskTemplateStore.Save(root, taskDirectory, new BatchTaskDefinition
            {
                Name = "Other", Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "command", Command = "true" }]
            });
            ExpectInvalidData(() => BatchTaskTemplateStore.Rename(root, renamed, "Other"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchTaskServerIdsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kitty-task-servers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.yaml");
        try
        {
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            var task = new BatchTaskDefinition
            {
                Name = "servers", ServerIds = [first, second],
                Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "command", Command = "true" }]
            };
            BatchTaskFile.Save(path, task);
            var loaded = BatchTaskFile.Load(path);
            Equal(2, loaded.ServerIds.Count);
            Equal(first, loaded.ServerIds[0]);
            // Старые файлы без списка серверов загружаются с пустым выбором.
            File.WriteAllText(path, "{\"name\":\"legacy\",\"steps\":[{\"kind\":0,\"name\":\"run\",\"command\":\"true\"}]}");
            Equal(0, BatchTaskFile.Load(path).ServerIds.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SmartImportSelectedFieldsReplaceCommand()
    {
        var local = new ManagedServer
        {
            Name = "Old session", Host = "host", Username = "user",
            ImportedCommand = "ssh user@10.0.0.1"
        };
        var imported = new ManagedServer
        {
            Id = local.Id, Name = "New session", Host = "host", Username = "user",
            ImportedCommand = ""
        };
        var current = new ManagerConfig { UngroupedServers = [local] };
        var incoming = new ManagerConfig { UngroupedServers = [imported] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        Equal(ImportDecision.KeepCurrent, plan.Sessions.Single().Decision);
        plan.Fields.Single(x => x.PropertyName == "Name").UseIncoming = true;
        plan.Fields.Single(x => x.PropertyName == "ImportedCommand").UseIncoming = true;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        var result = merged.UngroupedServers.Single();
        Equal(local.Id, result.Id);
        Equal("New session", result.Name);
        Equal("", result.ImportedCommand);
    }

    private static void SmartImportSentinelAndCascade()
    {
        var local = new ManagedServer { Name = "Old", Host = "host", Username = "user", ImportedCommand = "ssh x" };
        var imported = new ManagedServer { Id = local.Id, Name = "New", Host = "host", Username = "user" };
        var current = new ManagerConfig { UngroupedServers = [local] };
        var incoming = new ManagerConfig { UngroupedServers = [imported] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var row = plan.Sessions.Single();
        Equal(local.Id, row.CurrentId);

        // «Взять данные из файла» каскадом отмечает группу и все поля.
        row.Decision = ImportDecision.UseIncoming;
        ImportWizardEngine.ApplyFullImportCascade(plan, [row.IncomingId]);
        Equal(true, row.UseIncomingGroup);
        Equal(true, plan.Fields.All(x => x.UseIncoming));

        // «Добавить как новую» с CurrentId=null создаёт отдельную сессию.
        row.CurrentId = null;
        row.Decision = ImportDecision.Add;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        Equal(2, merged.AllServers().Count());
    }

    private static void BatchStepServerSubsetAndDirectory()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var all = new BatchTaskStep();
        var subset = new BatchTaskStep { ServerIds = [first] };
        Equal(true, BatchStepPolicy.AppliesTo(all, first));
        Equal(true, BatchStepPolicy.AppliesTo(all, second));
        Equal(true, BatchStepPolicy.AppliesTo(subset, first));
        Equal(false, BatchStepPolicy.AppliesTo(subset, second));

        var task = new BatchTaskDefinition { WorkingDirectory = "/task" };
        var plain = new BatchTaskStep();
        var own = new BatchTaskStep { WorkingDirectory = "/step" };
        Equal("/task", BatchTaskPolicy.EffectiveWorkingDirectory(task, plain));
        Equal("/step", BatchTaskPolicy.EffectiveWorkingDirectory(task, own));
        var server = new ManagedServer();
        Equal("if [ -d '/step' ]; then cd -- '/step' && run; " +
            "else printf '%s\\n' 'Рабочая папка отсутствует или недоступна на сервере: /step' >&2; exit 126; fi",
            BatchTaskRunner.BuildTaskCommand("run", BatchTaskPolicy.EffectiveWorkingDirectory(task, own), false, server));

        // Новые поля шага переживают round-trip.
        var directory = Path.Combine(Path.GetTempPath(), "kitty-step-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.yaml");
        try
        {
            var saved = new BatchTaskDefinition
            {
                Name = "fields",
                Steps = [new() { Kind = BatchTaskStepKind.Command, Name = "s", Command = "true", ServerIds = [first], WorkingDirectory = "/step" }]
            };
            BatchTaskFile.Save(path, saved);
            var loaded = BatchTaskFile.Load(path).Steps.Single();
            Equal(first, loaded.ServerIds.Single());
            Equal("/step", loaded.WorkingDirectory);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SmartImportLinkDecision()
    {
        var first = new ManagedServer { Name = "A", Host = "a" };
        var second = new ManagedServer { Name = "B", Host = "b" };
        var current = new ManagerConfig
        {
            UngroupedServers = [first, second],
            Links = [new() { FromServerId = first.Id, ToServerId = second.Id, LastStrategy = "local" }]
        };
        var importedFirst = new ManagedServer { Id = first.Id, Name = "A2", Host = "a" };
        var importedSecond = new ManagedServer { Id = second.Id, Name = "B2", Host = "b" };
        var incoming = new ManagerConfig
        {
            UngroupedServers = [importedFirst, importedSecond],
            Links = [new() { FromServerId = first.Id, ToServerId = second.Id, LastStrategy = "incoming" }]
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        Equal(true, plan.Links.Single().ExistingConflict);
        Equal(ImportDecision.KeepCurrent, plan.Links.Single().Decision);
        plan.Links.Single().Decision = ImportDecision.UseIncoming;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        Equal("incoming", merged.Links.Single().LastStrategy);
    }

    private static void SmartImportMatchedAddCreatesCopy()
    {
        var local = new ManagedServer { Name = "Local", Host = "host", Username = "user" };
        var imported = new ManagedServer { Id = local.Id, Name = "Imported", Host = "host", Username = "user" };
        var currentProxy = new BaseProxy { Name = "Local JH", Host = "jump", Port = 22 };
        var importedProxy = new BaseProxy { Id = currentProxy.Id, Name = "Imported JH", Host = "jump", Port = 22 };
        var current = new ManagerConfig { UngroupedServers = [local], BaseProxies = [currentProxy] };
        var incoming = new ManagerConfig { UngroupedServers = [imported], BaseProxies = [importedProxy] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        plan.Sessions.Single().Decision = ImportDecision.Add;
        plan.Proxies.Single().Decision = ImportDecision.Add;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        Equal(2, merged.AllServers().Count());
        Equal(2, merged.BaseProxies.Count);
        Equal(2, merged.AllServers().Select(x => x.Id).Distinct().Count());
        Equal(2, merged.BaseProxies.Select(x => x.Id).Distinct().Count());
    }

    private static void SmartImportRestoresNewNestedGroups()
    {
        var first = new ManagedServer { Name = "First", Host = "first", Username = "user" };
        var second = new ManagedServer { Name = "Second", Host = "second", Username = "user" };
        var current = new ManagerConfig
        {
            UngroupedServers =
            [
                new() { Id = first.Id, Name = first.Name, Host = first.Host, Username = first.Username },
                new() { Id = second.Id, Name = second.Name, Host = second.Host, Username = second.Username }
            ],
            Groups = [new() { Name = "Unrelated", Groups = [new() { Name = "Common child" }] }]
        };
        var incoming = new ManagerConfig
        {
            Groups = [new()
            {
                Name = "Imported parent", Servers = [first],
                Groups = [new() { Name = "Common child", Servers = [second] }]
            }]
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        Equal(true, plan.Sessions.All(x => x.UseIncomingGroup));
        Equal(true, plan.Groups.All(x => x.Decision == ImportGroupDecision.CreateNew));
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        var parent = merged.Groups.Single(x => x.Name == "Imported parent");
        Equal(first.Id, parent.Servers.Single().Id);
        Equal(second.Id, parent.Groups.Single(x => x.Name == "Common child").Servers.Single().Id);
        Equal(0, merged.UngroupedServers.Count);
    }

    private static void SmartImportLogProjectionIsSafe()
    {
        var local = new ManagedServer { Name = "Local", Host = "host", Username = "user", Password = "local-secret" };
        var imported = new ManagedServer
        {
            Id = local.Id, Name = "Imported", Host = "host", Username = "user",
            Password = "incoming-secret", PrivateKeyPassphrase = "key-secret", RootPassword = "root-secret"
        };
        var plan = ConfigTransfer.AnalyzeSmartImport(
            new ManagerConfig { UngroupedServers = [local] },
            new ManagerConfig { UngroupedServers = [imported] });
        var log = string.Join('\n', ImportWizardEngine.DescribePlan(plan));
        Equal(false, log.Contains("local-secret", StringComparison.Ordinal));
        Equal(false, log.Contains("incoming-secret", StringComparison.Ordinal));
        Equal(false, log.Contains("key-secret", StringComparison.Ordinal));
        Equal(false, log.Contains("root-secret", StringComparison.Ordinal));
        Equal(true, log.Contains("Imported", StringComparison.Ordinal));
    }

    private static void SmartImportSearchPolicy()
    {
        var session = new ImportSessionDecision
        {
            IncomingName = "Imported node", IncomingGroup = "Parent / Child", Reason = "New session"
        };
        Equal(true, ImportSearchPolicy.Matches(session, "child"));
        Equal(true, ImportSearchPolicy.Matches(session, "IMPORTED"));
        Equal(false, ImportSearchPolicy.Matches(session, "missing"));
        Equal(true, ImportSearchPolicy.Matches(session, " "));
        Equal(true, ImportSearchPolicy.Matches(
            new ImportLinkDecision { FromName = "Source", ToName = "Target", Reason = "Conflict" }, "target"));
    }

    private static void ExpectInvalidData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException");
    }

    private static void BatchStepWaitAndTunnelPolicy()
    {
        // WaitAfterSeconds, KeepTunnelsAfterSteps и DownloadFolder переживают round-trip.
        var directory = Path.Combine(Path.GetTempPath(), "kitty-wait-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.yaml");
        try
        {
            var saved = new BatchTaskDefinition
            {
                Name = "wait-test",
                KeepTunnelsAfterSteps = true,
                Steps =
                [
                    new() { Kind = BatchTaskStepKind.Command, Name = "s", Command = "true", WaitAfterSeconds = 15 },
                    new() { Kind = BatchTaskStepKind.Download, Name = "dl", Source = "/var/log/*.log", DownloadFolder = "logs" }
                ]
            };
            BatchTaskFile.Save(path, saved);
            var loaded = BatchTaskFile.Load(path);
            Equal(true, loaded.KeepTunnelsAfterSteps);
            Equal(15, loaded.Steps[0].WaitAfterSeconds);
            Equal("logs", loaded.Steps[1].DownloadFolder);
            Equal(false, loaded.Steps[1].DownloadAsArchive);

            loaded.Steps[1].DownloadAsArchive = true;
            BatchTaskFile.Save(path, loaded);
            Equal(true, BatchTaskFile.Load(path).Steps[1].DownloadAsArchive);

            // По умолчанию туннели закрываются после шагов.
            var defaultTask = new BatchTaskDefinition();
            Equal(false, defaultTask.KeepTunnelsAfterSteps);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void BatchUploadMultiSource()
    {
        // Несколько источников разделяются переносом строки.
        var step = new BatchTaskStep
        {
            Kind = BatchTaskStepKind.Upload, Name = "up",
            Source = "config.ini\napp.jar\nscripts/",
            Destination = "/opt/app"
        };
        var sources = BatchTaskFile.ParseLocalSources(step.Source);
        Equal(3, sources.Length);
        Equal("config.ini", sources[0]);
        Equal("app.jar", sources[1]);
        Equal("scripts/", sources[2]);

        // Валидация пропускает множественный источник.
        BatchTaskFile.Validate(new() { Name = "t", Steps = [step] });

        // Пустой источник отклоняется.
        var empty = new BatchTaskStep { Kind = BatchTaskStepKind.Upload, Name = "e", Source = "  \n  ", Destination = "/opt" };
        var failed = false;
        try { BatchTaskFile.Validate(new() { Name = "t", Steps = [empty] }); }
        catch (InvalidDataException) { failed = true; }
        Equal(true, failed);
    }

    private static void BatchDownloadGlobDirectory()
    {
        var format = BatchTaskRunner.SelectDownloadArchiveFormat("tar-gzip\n");
        var command = BatchTaskRunner.BuildDownloadArchiveCommand(
            "/var/log/app", "*.log", "/var/log/app/.out.tar.gz", format, false, new ManagedServer());
        Equal("cd -- '/var/log/app' && tar -cf '/var/log/app/.out.tar' -- *'.log' && gzip -9 -f '/var/log/app/.out.tar'", command);
    }

    private static void BatchDownloadArchiveFormats()
    {
        var gzip = BatchTaskRunner.SelectDownloadArchiveFormat("shell banner\ntar-gzip");
        Equal(".tar.gz", gzip.Extension);
        Equal(true, BatchTaskRunner.BuildDownloadArchiveCommand("/srv", "data", "/srv/.copy.tar.gz",
            gzip, false, new ManagedServer()).Contains("gzip -9 -f", StringComparison.Ordinal));
        var zip = BatchTaskRunner.SelectDownloadArchiveFormat("zip\r\n");
        Equal(".zip", zip.Extension);
        Equal(true, BatchTaskRunner.BuildDownloadArchiveCommand("/srv", "*.log", "/srv/.copy.zip",
            zip, false, new ManagedServer()).Contains("zip -9 -r", StringComparison.Ordinal));
        Equal(".tar", BatchTaskRunner.SelectDownloadArchiveFormat("tar").Extension);
        Equal(true, BatchTaskRunner.BuildDownloadArchiveToolProbe(false, new ManagedServer())
            .Contains("command -v tar", StringComparison.Ordinal));
        var cleanup = BatchTaskRunner.BuildDownloadArchiveCleanupCommand("/srv/.kitty-id", false, new ManagedServer());
        Equal("rm -f -- '/srv/.kitty-id.tar.gz' '/srv/.kitty-id.tar' '/srv/.kitty-id.zip'", cleanup);
        try { BatchTaskRunner.SelectDownloadArchiveFormat("rar"); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unknown archive format accepted");
    }

    private static void BatchDownloadWorkingDirectory()
    {
        Equal("/srv/files/*.json", BatchTaskRunner.ResolveRemoteDownloadPath("*.json", "/srv/files"));
        Equal("/var/log/app.log", BatchTaskRunner.ResolveRemoteDownloadPath("/var/log/app.log", "/srv/files"));
        Equal("*.json", BatchTaskRunner.ResolveRemoteDownloadPath("*.json", ""));
        var rootFormat = BatchTaskRunner.SelectDownloadArchiveFormat("tar");
        Equal("cd -- '/' && tar -cf '/.copy.tar' -- 'app.log'", BatchTaskRunner.BuildDownloadArchiveCommand(
            "/", "app.log", "/.copy.tar", rootFormat, false, new ManagedServer()));
    }

    private static void SelectionRowToggle()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Equal(true, SelectionTogglePolicy.ShouldSelectAll([first, second], new HashSet<Guid>()));
        Equal(true, SelectionTogglePolicy.ShouldSelectAll([first, second], new HashSet<Guid> { first }));
        Equal(false, SelectionTogglePolicy.ShouldSelectAll([first, second], new HashSet<Guid> { first, second }));
    }

    private static void SharedServerSelectionPolicy()
    {
        var first = new ManagedServer { Name = "Alpha", Host = "10.0.0.1", Username = "operator" };
        var second = new ManagedServer { Name = "Beta", Host = "10.0.0.2" };
        var nested = new ServerGroup { Name = "Child", Servers = [second] };
        var config = new ManagerConfig
        {
            Groups = [new ServerGroup { Name = "Parent", Servers = [first], Groups = [nested] }]
        };

        var partlySelected = ServerSelectionPolicy.Build(config, new HashSet<Guid> { first.Id });
        Equal(null, partlySelected.Single(row => row.Name == "Parent").IsChecked);
        Equal(true, partlySelected.Single(row => row.ServerId == first.Id).IsChecked);
        Equal(false, partlySelected.Single(row => row.ServerId == second.Id).IsChecked);
        Equal("10.0.0.1:22  ·  Parent", partlySelected.Single(row => row.ServerId == first.Id).Details);

        var byLogin = ServerSelectionPolicy.Build(config, new HashSet<Guid>(), "operator");
        Equal(true, byLogin.Any(row => row.ServerId == first.Id));
        Equal(false, byLogin.Any(row => row.ServerId == second.Id));

        var excluded = ServerSelectionPolicy.Build(config, new HashSet<Guid>(), "", first.Id);
        Equal(false, excluded.Any(row => row.ServerId == first.Id));
        Equal(true, excluded.Any(row => row.ServerId == second.Id));
    }

    private static void BatchTaskExternalFilePackaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-package-" + Guid.NewGuid().ToString("N"));
        var taskDirectory = Path.Combine(root, "task");
        var externalDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(taskDirectory);
        Directory.CreateDirectory(externalDirectory);
        try
        {
            var external = Path.Combine(externalDirectory, "config.json");
            File.WriteAllText(external, "data");
            var task = new BatchTaskDefinition
            {
                Name = "task",
                Steps = [new() { Kind = BatchTaskStepKind.Upload, Name = "upload", Source = external, Destination = "/tmp" }]
            };
            var package = Path.Combine(root, "task.kmtask");
            BatchTaskFile.ExportPackageWithFiles(taskDirectory, task, package);
            var extracted = Path.Combine(root, "extracted");
            var manifest = BatchTaskFile.ImportPackage(package, extracted);
            var imported = BatchTaskFile.Load(manifest);
            Equal("config.json", imported.Steps[0].Source);
            Equal(true, File.Exists(Path.Combine(extracted, "config.json")));
            BatchTaskFile.ResolveImportedLocalSources(imported, extracted);
            Equal(Path.Combine(extracted, "config.json"), imported.Steps[0].Source);
            Equal(external, task.Steps[0].Source);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchTaskWorkspaceCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-workspace-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        var stale = Path.Combine(root, "stale");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "task.yaml"), "old");
        try
        {
            var deleted = BatchTaskWorkspace.CleanupStaleDirectories(root, current);
            Equal(1, deleted.Count);
            Equal(true, Directory.Exists(current));
            Equal(false, Directory.Exists(stale));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchStepDuplicate()
    {
        var source = new BatchTaskStep
        {
            Kind = BatchTaskStepKind.Download, Name = "download", Source = "/var/log/*.log",
            Destination = "/tmp", Search = "a", Replacement = "b", ExpectedText = "ready",
            ConditionCommand = "test -f x", ActionCommand = "yes", TimeoutSeconds = 37,
            Become = true, RunAsUser = "service", RunAsPassword = "secret", Backup = false,
            ContinueOnError = true, WaitAfterSeconds = 4, DownloadFolder = "logs",
            DownloadAsArchive = true,
            ServerIds = [Guid.NewGuid()], WorkingDirectory = "/opt/app"
        };
        var copy = BatchStepPolicy.Duplicate(source);
        Equal(System.Text.Json.JsonSerializer.Serialize(source), System.Text.Json.JsonSerializer.Serialize(copy));
        copy.ServerIds.Clear();
        Equal(1, source.ServerIds.Count);
    }

    private static void BatchConnectionRetryDelay() =>
        Equal(TimeSpan.FromSeconds(10), BatchTaskRunner.ConnectionRetryDelay);

    private static void BatchConnectionRetrySafety()
    {
        Equal(true, TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(BatchTaskStepKind.Check));
        Equal(true, TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(BatchTaskStepKind.MakeDirectory));
        Equal(false, TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(BatchTaskStepKind.Command));
        Equal(false, TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(BatchTaskStepKind.Upload));
        Equal(false, TaskConnectionRecoveryPolicy.CanRetryWithoutDuplicateEffect(BatchTaskStepKind.WaitForText));
    }

    private static void BatchRunAsAndExportWithoutFiles()
    {
        // RunAs генерирует su/sudo команду.
        var server = new ManagedServer();
        var withPassword = BatchTaskRunner.BuildTaskCommand("id", "", false, server, "appuser", "secret");
        Equal(true, withPassword.Contains("su - 'appuser'", StringComparison.Ordinal));
        Equal(true, withPassword.Contains("printf '%s\\n' 'secret'", StringComparison.Ordinal));
        var withoutPassword = BatchTaskRunner.BuildTaskCommand("id", "", false, server, "appuser", "");
        Equal(true, withoutPassword.Contains("sudo -n -u 'appuser'", StringComparison.Ordinal));
        // Без RunAs — обычная команда.
        var plain = BatchTaskRunner.BuildTaskCommand("id", "", false, server);
        Equal("id", plain);

        // ExportPackageWithoutFiles создаёт zip только с task.yaml.
        var directory = Path.Combine(Path.GetTempPath(), "kitty-nofiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "task.yaml"), "{\"Name\":\"t\",\"Steps\":[]}");
            File.WriteAllText(Path.Combine(directory, "big-file.bin"), new string('x', 100000));
            var package = Path.Combine(directory, "out.kmtask");
            BatchTaskFile.ExportPackageWithoutFiles(directory, package);
            using var zip = System.IO.Compression.ZipFile.OpenRead(package);
            Equal(1, zip.Entries.Count);
            Equal("task.yaml", zip.Entries[0].FullName);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void BatchPackageAndSourceListDefaults()
    {
        Equal(false, BatchTaskPolicy.IncludeFilesInPackageByDefault);
        var paths = new[] { @"C:\work\first file.txt", @"C:\work\second.bin" };
        var serialized = BatchTaskFile.SerializeLocalSources(paths);
        Equal(string.Join('\n', paths), serialized);
        Equal(true, paths.SequenceEqual(BatchTaskFile.ParseLocalSources(
            "  " + paths[0] + "\r\n\r\n" + paths[1] + "  "), StringComparer.Ordinal));
    }

    private static void SmartImportKeepsCurrentKeyPath()
    {
        var existing = new ManagedServer { Name = "S", Host = "h", PrivateKeyPath = "/correct/key" };
        var current = new ManagerConfig { UngroupedServers = [existing] };
        var imported = new ManagedServer { Id = existing.Id, Name = "S", Host = "h", PrivateKeyPath = "/wrong/key" };
        var incoming = new ManagerConfig { UngroupedServers = [imported] };
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var keyField = plan.Fields.Single(x => x.PropertyName == "PrivateKeyPath");
        Equal(false, keyField.UseIncoming);
        // Остальные поля по-прежнему берутся из файла.
        var nameField = plan.Fields.SingleOrDefault(x => x.PropertyName == "Name");
        // Name не отличается — не попадает в список полей.
        Equal(null, nameField);
    }

    private static void ManagedJumphostStop()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 >nul") { UseShellExecute = false }
            : new ProcessStartInfo("sleep", "30") { UseShellExecute = false };
        using var process = Process.Start(startInfo)!;
        var processId = process.Id;
        var proxy = new BaseProxy { Host = "127.0.0.1", Port = 5555 };
        var registry = new JumphostProcessRegistry();
        registry.Remember(proxy, JumphostConsoleKind.Entry, process, "test");
        Equal(true, registry.TryStopAliveManaged(proxy));
        try
        {
            using var remaining = Process.GetProcessById(processId);
            remaining.WaitForExit(5000);
            Equal(true, remaining.HasExited);
        }
        catch (ArgumentException) { }
    }

    private static void SafeVerificationTaskPackage()
    {
        var examples = Path.Combine(Directory.GetCurrentDirectory(), "examples", "batch-tasks");
        var source = Path.Combine(examples, "constructor");
        var packagePath = Path.Combine(examples, "task-safe-smoke.kmtask");
        Equal(true, File.Exists(packagePath));
        var tempExtract = Path.Combine(Path.GetTempPath(), "kitty-smoke-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = BatchTaskFile.ImportPackage(packagePath, tempExtract);
            var task = BatchTaskFile.Load(manifest);
            BatchTaskFile.Validate(task);
            Equal(0, task.Tunnels.Count);
            Equal(true, Enum.GetValues<BatchTaskStepKind>().All(kind => task.Steps.Any(step => step.Kind == kind)));
            var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            Equal(sourceFiles.Length, Directory.GetFiles(tempExtract, "*", SearchOption.AllDirectories).Length);
            foreach (var file in sourceFiles)
                Equal(true, File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(
                    Path.Combine(tempExtract, Path.GetRelativePath(source, file)))));
            foreach (var step in task.Steps.Where(step => step.Kind is BatchTaskStepKind.Upload or BatchTaskStepKind.Template))
                foreach (var relative in BatchTaskFile.ParseLocalSources(step.Source))
                {
                    var local = Path.Combine(tempExtract, relative);
                    Equal(true, File.Exists(local) || Directory.Exists(local));
                }

            var playbook = Path.Combine(examples, "ansible", "ansible-safe-smoke.yml");
            var workspace = AnsibleTaskWorkspacePolicy.Prepare(Path.Combine(tempExtract, "templates"), playbook);
            try
            {
                Equal(0, AnsibleDependencyScanner.Scan(workspace.TaskDirectory).Missing.Count);
                foreach (var file in AnsibleTaskWorkspacePolicy.ProjectMaterials(playbook))
                    Equal(true, File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(
                        Path.Combine(workspace.TaskDirectory, Path.GetRelativePath(Path.GetDirectoryName(playbook)!, file)))));
            }
            finally { AnsibleTaskWorkspacePolicy.DeleteSecrets(workspace); }
        }
        finally { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); }
    }

    private static void BatchStepWaitForExitPolicy()
    {
        var defaultStep = new BatchTaskStep();
        Equal(true, defaultStep.WaitForExit);

        var detached = new BatchTaskStep
        {
            Kind = BatchTaskStepKind.InteractiveWaitAndSend,
            Name = "restart",
            Command = "./restart",
            ExpectedText = "[y/N]:",
            ActionCommand = "y",
            WaitForExit = false
        };
        var duplicated = BatchStepPolicy.Duplicate(detached);
        Equal(false, duplicated.WaitForExit);

        var json = System.Text.Json.JsonSerializer.Serialize(detached);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<BatchTaskStep>(json)!;
        Equal(false, deserialized.WaitForExit);
    }

    private static void BatchCommandExitCodePolicy()
    {
        Equal(false, BatchTaskRunner.IsCommandResultFatal(0, "", ""));
        Equal(false, BatchTaskRunner.IsCommandResultFatal(0, "ok", ""));
        Equal(false, BatchTaskRunner.IsCommandResultFatal(124, "log trace output", "", failOnNonZero: false));
        Equal(false, BatchTaskRunner.IsCommandResultFatal(1, "", "some warning on stderr", failOnNonZero: false));
        Equal(true, BatchTaskRunner.IsCommandResultFatal(1, "", "", failOnNonZero: false));
        Equal(true, BatchTaskRunner.IsCommandResultFatal(127, "   ", "\t\n", failOnNonZero: false));
        Equal(true, BatchTaskRunner.IsCommandResultFatal(1, "output", "", failOnNonZero: true));
    }


    public static void BatchCommandStreamLineStreaming()
    {
        var input = "line 1\r\nline 2\npartial tail";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input));
        var raw = new System.Text.StringBuilder();
        var lines = new List<string>();
        BatchTaskRunner.ConsumeStreamLinesAsync(stream, raw, lines.Add, CancellationToken.None).GetAwaiter().GetResult();

        Equal(input, raw.ToString());
        Equal(3, lines.Count);
        Equal("line 1", lines[0]);
        Equal("line 2", lines[1]);
        Equal("partial tail", lines[2]);
    }

    public static void BatchRunSummaryListsServerVerdicts()
    {
        var done = new BatchServerResult(Guid.NewGuid(), "server-a", true, false, "Готово", []);
        var partial = new BatchServerResult(Guid.NewGuid(), "server-b", true, false, "Готово", [], "Шаг 2/3: Сертификат");
        var failed = new BatchServerResult(Guid.NewGuid(), "server-c", false, false, "su: must be run from a terminal", [], "Шаг 1/3: Проверка");
        var stopped = new BatchServerResult(Guid.NewGuid(), "server-d", false, true, "Остановлено", [], "Шаг 2/3: Сертификат");
        var offline = new BatchServerResult(Guid.NewGuid(), "server-e", false, false, "Не подключено", []);

        var summary = BatchRunSummary.Lines([done, partial, failed, stopped, offline], 5);

        Equal(6, summary.Count);
        Equal(done.ServerId, summary[0].ServerId);
        Equal("успешно, все шаги выполнены", summary[0].Message);
        Equal(BatchLogLevel.Info, summary[0].Level);
        Equal(true, summary[1].Message.Contains("оставшиеся шаги пропущены начиная с «Шаг 2/3: Сертификат»", StringComparison.Ordinal));
        Equal(BatchLogLevel.Warning, summary[1].Level);
        Equal("ошибка на шаге «Шаг 1/3: Проверка»: su: must be run from a terminal", summary[2].Message);
        Equal(BatchLogLevel.Error, summary[2].Level);
        Equal("остановлен на шаге «Шаг 2/3: Сертификат»", summary[3].Message);
        Equal(BatchLogLevel.Warning, summary[3].Level);
        Equal("ошибка на шаге «Подключение»: Не подключено", summary[4].Message);
        Equal(BatchLogLevel.Error, summary[4].Level);
        Equal("Итог: успешно 2 из 5.", summary[5].Message);
        Equal(BatchLogLevel.Info, summary[5].Level);
        Equal(Guid.Empty, summary[5].ServerId);
        Equal("Задача", summary[5].ServerName);

        // Остановка пользователем: без метки шага сервер прошёл всё, с меткой
        // подключения — не успел подключиться, упавший до остановки остаётся ошибкой.
        var finishedThenStopped = new BatchServerResult(Guid.NewGuid(), "server-f", false, true, "Остановлено", []);
        var stoppedOnConnect = new BatchServerResult(Guid.NewGuid(), "server-g", false, true, "Остановлено", [], BatchRunSummary.ConnectStepLabel);
        var failedThenStopped = new BatchServerResult(Guid.NewGuid(), "server-h", false, true, "Команда завершилась с кодом 1", [], "Шаг 1/2: Файлы", Failed: true);
        var cancelSummary = BatchRunSummary.Lines([finishedThenStopped, stoppedOnConnect, failedThenStopped], 3);
        Equal("остановлен после выполнения всех шагов", cancelSummary[0].Message);
        Equal(BatchLogLevel.Warning, cancelSummary[0].Level);
        Equal("остановлен на шаге «Подключение»", cancelSummary[1].Message);
        Equal("ошибка на шаге «Шаг 1/2: Файлы»: Команда завершилась с кодом 1", cancelSummary[2].Message);
        Equal(BatchLogLevel.Error, cancelSummary[2].Level);
        Equal("Итог: успешно 0 из 3.", cancelSummary[3].Message);

        // Пустой запуск и расхождение счётчиков не должны ломать итоговую строку.
        Equal("Итог: успешно 0 из 0.", BatchRunSummary.Lines([], 0)[0].Message);
        Equal("Итог: успешно 1 из 4.", BatchRunSummary.Lines([done], 4)[1].Message);

        // Итоговые строки — часть модели журнала: фильтры не должны их терять.
        var asLogs = summary.Select(x => new BatchTaskLog(DateTimeOffset.Now, x.ServerId, x.ServerName, "", x.Message, x.Level, "Итог")).ToList();
        Equal(6, BatchLogFormatter.Filter(asLogs, null, null).Count());
        Equal(2, BatchLogFormatter.Filter(asLogs, null, BatchLogLevel.Error).Count());
        Equal(1, BatchLogFormatter.Filter(asLogs, done.ServerId, null).Count());
    }

    public static void PrivilegedSuPasswordDirectPipeAndTaskCommandChecksDirectory()
    {
        // su с паролем и без пароля: чистая команда без pty script
        Equal("su - -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "su -", true));
        Equal("su - -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "su -", false));
        Equal("su -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "su", true));
        Equal("su -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "su", false));
        Equal("sudo -S -p '' -i sh -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "sudo -i", true));
        Equal("sudo -n su - -c 'id'", RemoteCommandBridge.BuildPrivilegedCommand("id", "sudo su -", false));

        // Команда шага с переходом su + паролем подаёт пароль напрямую через конвейер
        var suServer = new ManagedServer { RootLogin = "su -", RootPassword = "rootpassword" };
        var batchSuCmd = BatchTaskRunner.BuildTaskCommand("id", "", true, suServer);
        Equal("printf '%s\\n' 'rootpassword' | su - -c 'id'", batchSuCmd);

        // WrapRunAs с паролем также подаёт пароль напрямую через конвейер
        var runAsCmd = BatchTaskRunner.BuildTaskCommand("id", "", false, new ManagedServer(), "appuser", "runaspass");
        Equal("printf '%s\\n' 'runaspass' | su - 'appuser' -c 'id'", runAsCmd);

        // Маскирование пароля RunAsPassword
        Equal("***", SecretRedactor.Redact("runaspass", new ManagedServer(), ["runaspass"]));

        // Рабочая папка проверяется до cd с понятной ошибкой, а не cryptic-выводом оболочки.
        var inDir = BatchTaskRunner.BuildTaskCommand("ls -l", "/opt/app", false, new ManagedServer());
        Equal(true, inDir.Contains("[ -d '/opt/app' ]", StringComparison.Ordinal));
        Equal(true, inDir.Contains("Рабочая папка отсутствует или недоступна на сервере: /opt/app", StringComparison.Ordinal));
        Equal(true, inDir.Contains("exit 126", StringComparison.Ordinal));
        Equal("ls -l", BatchTaskRunner.BuildTaskCommand("ls -l", "  ", false, new ManagedServer()));

        // Ошибка su без терминала получает подсказку, прочие ошибки не меняются.
        Equal(true, BatchTaskRunner.ClarifyCommandError("Код 1: su: must be run from a terminal")
            .Contains("Подсказка", StringComparison.Ordinal));
        Equal("disk full", BatchTaskRunner.ClarifyCommandError("disk full"));
    }

    private sealed class MockTerminalStream : Stream
    {
        private readonly Queue<byte> input = new();
        public List<string> SentLines { get; } = [];

        public void Feed(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (input)
            {
                foreach (var b in bytes) input.Enqueue(b);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (input)
            {
                var read = 0;
                while (read < count && input.Count > 0)
                    buffer[offset + read++] = input.Dequeue();
                return read;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (input)
                {
                    if (input.Count > 0)
                    {
                        var read = 0;
                        while (read < buffer.Length && input.Count > 0)
                            buffer.Span[read++] = input.Dequeue();
                        return read;
                    }
                }
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }

        public bool Echo { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var text = Encoding.UTF8.GetString(buffer, offset, count);
            SentLines.Add(text);
            if (Echo)
                Feed(text);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string FromOctalEscape(string octal)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < octal.Length; i += 4)
        {
            if (octal[i] == '\\' && i + 3 < octal.Length)
            {
                var val = Convert.ToByte(octal.Substring(i + 1, 3), 8);
                bytes.Add(val);
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public static void SuPasswordViaPtyTerminalExecution()
    {
        // 1. Проверка разбора команд su с передачей пароля (включая спецсимволы и кавычки)
        var suCmd = BatchTaskRunner.BuildTaskCommand("ls -l", "", true, new ManagedServer { RootLogin = "su -", RootPassword = "mypassword" });
        Equal(true, BatchTaskRunner.TryParseSuCommandWithPipedPassword(suCmd, out var parsedSu, out var parsedPass));
        Equal("su - -c 'ls -l'", parsedSu);
        Equal("mypassword", parsedPass);

        var runAsCmd = BatchTaskRunner.BuildTaskCommand("whoami", "", false, new ManagedServer(), "appuser", "apppass'word");
        Equal(true, BatchTaskRunner.TryParseSuCommandWithPipedPassword(runAsCmd, out var parsedRunAs, out var parsedRunAsPass));
        Equal("su - 'appuser' -c 'whoami'", parsedRunAs);
        Equal("apppass'word", parsedRunAsPass);

        var complexCmd = BatchTaskRunner.BuildTaskCommand("id", "", true, new ManagedServer { RootLogin = "su -", RootPassword = "p@ss' | su 'word" });
        Equal(true, BatchTaskRunner.TryParseSuCommandWithPipedPassword(complexCmd, out var parsedComplexSu, out var parsedComplexPass));
        Equal("su - -c 'id'", parsedComplexSu);
        Equal("p@ss' | su 'word", parsedComplexPass);

        // Не-su команды не должны перехватываться
        Equal(false, BatchTaskRunner.TryParseSuCommandWithPipedPassword("sudo -S -p '' -i sh -c 'id'", out _, out _));
        Equal(false, BatchTaskRunner.TryParseSuCommandWithPipedPassword("ls -l", out _, out _));
        Equal(false, BatchTaskRunner.TryParseSuCommandWithPipedPassword("su - -c 'id'", out _, out _));

        // 2. Тестирование протокола терминала через mock-поток с включённым эхо терминала
        var server = Server("pty-test");
        server.RootPassword = "secretpassword";
        using var stream = new MockTerminalStream { Echo = true };

        string? startMarker = null;
        string? doneMarkerPrefix = null;
        var runnerTask = Task.Run(async () =>
        {
            return await BatchTaskRunner.RunInteractivePtyCommandCoreAsync(
                stream,
                line =>
                {
                    const string p1 = "printf '%b\\n' '";
                    const string p2 = "printf '\\n%b%d\\n' '";
                    if (line.Contains(p1))
                    {
                        var sIdx = line.IndexOf(p1) + p1.Length;
                        var sEnd = line.IndexOf('\'', sIdx);
                        startMarker = FromOctalEscape(line[sIdx..sEnd]);

                        var dIdx = line.IndexOf(p2) + p2.Length;
                        var dEnd = line.IndexOf('\'', dIdx);
                        doneMarkerPrefix = FromOctalEscape(line[dIdx..dEnd]);
                    }
                    else if (line == "secretpassword")
                    {
                        stream.Feed($"total 64\r\ndrwxr-xr-x 2 root root 4096 bin\r\n\r\n{doneMarkerPrefix}0\r\n");
                    }
                },
                "su - -c 'ls -l'",
                "secretpassword",
                server,
                "Шаг теста",
                ["secretpassword"],
                (s, step, msg, lvl, src) => { },
                CancellationToken.None,
                emitOutput: false,
                timeoutSeconds: 5);
        });

        // Терминал выдаёт заголовок, стартовый маркер и приглашение Password:
        var sw = Stopwatch.StartNew();
        while (startMarker == null && sw.ElapsedMilliseconds < 2000)
            Thread.Sleep(10);
        Equal(true, startMarker != null);

        stream.Feed($"Last login: Sun Sep 7 12:00\r\nuser@host:~$ \r\n{startMarker}\r\nPassword: ");

        var (output, exitCode) = runnerTask.GetAwaiter().GetResult();
        Equal(0, exitCode);
        Equal(true, output.Contains("total 64"));
        Equal(true, output.Contains("bin"));
        Equal(false, output.Contains("Password:"));
        Equal(false, output.Contains("Last login"));

        // 3. Тестирование неуспешной аутентификации su (код выхода 1) с эхо терминала
        using var failStream = new MockTerminalStream { Echo = true };
        string? fStartMarker = null;
        string? fDoneMarkerPrefix = null;
        var failTask = Task.Run(async () =>
        {
            return await BatchTaskRunner.RunInteractivePtyCommandCoreAsync(
                failStream,
                line =>
                {
                    const string fp1 = "printf '%b\\n' '";
                    const string fp2 = "printf '\\n%b%d\\n' '";
                    if (line.Contains(fp1))
                    {
                        var sIdx = line.IndexOf(fp1) + fp1.Length;
                        var sEnd = line.IndexOf('\'', sIdx);
                        fStartMarker = FromOctalEscape(line[sIdx..sEnd]);

                        var dIdx = line.IndexOf(fp2) + fp2.Length;
                        var dEnd = line.IndexOf('\'', dIdx);
                        fDoneMarkerPrefix = FromOctalEscape(line[dIdx..dEnd]);
                    }
                    else if (line == "wrong")
                    {
                        failStream.Feed($"su: Authentication failure\r\n\r\n{fDoneMarkerPrefix}1\r\n");
                    }
                },
                "su - -c 'id'",
                "wrong",
                server,
                "Шаг ошибки",
                ["wrong"],
                (s, step, msg, lvl, src) => { },
                CancellationToken.None,
                emitOutput: false,
                timeoutSeconds: 5);
        });

        var fSw = Stopwatch.StartNew();
        while (fStartMarker == null && fSw.ElapsedMilliseconds < 2000)
            Thread.Sleep(10);
        Equal(true, fStartMarker != null);
        failStream.Feed($"{fStartMarker}\r\nПароль для root: ");

        var (failOut, failCode) = failTask.GetAwaiter().GetResult();
        Equal(1, failCode);
        Equal(true, failOut.Contains("Authentication failure"));
        Equal(false, failOut.Contains("Пароль"));
    }

    public static void FirefoxProfileCopyKeepsPkcs11AndForcesMigration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kitty-firefox-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(tempDir, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "key4.db"), "key");
            File.WriteAllText(Path.Combine(source, "cert9.db"), "cert");
            File.WriteAllText(Path.Combine(source, "parent.lock"), "");
            File.WriteAllText(Path.Combine(source, "pkcs11.txt"),
                "library=\n" +
                "name=NSS Internal PKCS #11 Module\n" +
                "parameters=configdir='sql:C:\\\\Users\\\\OldUser\\\\AppData\\\\Roaming\\\\Mozilla\\\\Firefox\\\\Profiles\\\\old.profile' certPrefix='' keyPrefix='' secmod='secmod.db'\n" +
                "NSS=Flags=internal,critical\n");
            File.WriteAllText(Path.Combine(source, "prefs.js"),
                "user_pref(\"browser.migration.version\", 179);\n" +
                "user_pref(\"browser.startup.homepage_override.buildID\", \"20260903215306\");\n" +
                "user_pref(\"browser.startup.homepage_override.mstone\", \"155.0.1\");\n" +
                "user_pref(\"security.enterprise_roots.enabled\", true);\n");

            var runtime = FirefoxProfileWorkspace.Create(Path.Combine(tempDir, "runtime"), Guid.NewGuid(), Guid.NewGuid(), source);
            // pkcs11.txt must stay byte-identical: NSS resolves the certificate
            // and key databases through it, and a rewritten configdir pointed
            // runtime profiles at databases Firefox then failed to open.
            Equal(File.ReadAllText(Path.Combine(source, "pkcs11.txt")),
                File.ReadAllText(Path.Combine(runtime, "pkcs11.txt")));

            FirefoxProfileWorkspace.ApplyPreferences(runtime, 43210);
            var resultPrefs = File.ReadAllText(Path.Combine(runtime, "prefs.js"));
            Equal(true, resultPrefs.Contains("browser.migration.version\", 999", StringComparison.Ordinal));
            Equal(false, resultPrefs.Contains("browser.migration.version\", 179", StringComparison.Ordinal));
            Equal(true, resultPrefs.Contains("browser.startup.homepage_override.buildID\", \"20260101000000\"", StringComparison.Ordinal));
            Equal(true, resultPrefs.Contains("browser.startup.homepage_override.mstone\", \"ignore\"", StringComparison.Ordinal));
            Equal(false, resultPrefs.Contains("security.enterprise_roots.enabled", StringComparison.Ordinal));
            var resultUser = File.ReadAllText(Path.Combine(runtime, "user.js"));
            Equal(true, resultUser.Contains("browser.migration.version\", 999", StringComparison.Ordinal));
            Equal(false, resultUser.Contains("network.proxy.socks_port", StringComparison.Ordinal));

            // A running source Firefox holds parent.lock exclusively; copying
            // must refuse instead of snapshotting databases mid-write.
            using (var held = new FileStream(Path.Combine(source, "parent.lock"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var thrown = false;
                try { FirefoxProfileWorkspace.Create(Path.Combine(tempDir, "runtime"), Guid.NewGuid(), Guid.NewGuid(), source); }
                catch (FirefoxProfileLockedException) { thrown = true; }
                Equal(true, thrown);
            }
            // The refused copy must not leave a partial runtime profile behind.
            Equal(1, Directory.GetDirectories(Path.Combine(tempDir, "runtime")).Length);
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    public static void WebSessionCleanupPolicyGuardsHandedOffProfile()
    {
        // Once the browser process owns the profile directory, the startup
        // path deleting it leaves a running browser without prefs.js,
        // user.js, cert_override.txt and logins.json.
        Equal(false, WebSessionCleanupPolicy.ShouldDeletePreparedProfile(handedOffToCleanup: true, profileAttached: true, preparationSucceeded: true));
        Equal(false, WebSessionCleanupPolicy.ShouldDeletePreparedProfile(handedOffToCleanup: true, profileAttached: false, preparationSucceeded: true));
        // Failed startup must not leak the prepared directory.
        Equal(true, WebSessionCleanupPolicy.ShouldDeletePreparedProfile(handedOffToCleanup: false, profileAttached: true, preparationSucceeded: true));
        Equal(true, WebSessionCleanupPolicy.ShouldDeletePreparedProfile(handedOffToCleanup: false, profileAttached: false, preparationSucceeded: true));
        // A faulted preparation deletes its own directory; nothing to do here.
        Equal(false, WebSessionCleanupPolicy.ShouldDeletePreparedProfile(handedOffToCleanup: false, profileAttached: false, preparationSucceeded: false));
    }

    private static void SmartImportAppliesShorterPreferredRoutes()
    {
        var proxy = new BaseProxy { Name = "JH", Host = "jump", Port = 22 };
        var serverA = new ManagedServer { Name = "Server-A", Host = "srv-a", Username = "user" };
        var serverB = new ManagedServer { Name = "Server-B", Host = "srv-b", Username = "user" };
        var serverT = new ManagedServer
        {
            Name = "Server-T", Host = "srv-t", Username = "user",
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy.Id,
                ServerIds = [serverA.Id, serverB.Id, Guid.NewGuid()],
                LatencyMs = 120
            }
        };
        serverT.PreferredRoute.ServerIds[^1] = serverT.Id;

        var serverU = new ManagedServer
        {
            Name = "Server-U", Host = "srv-u", Username = "user",
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy.Id,
                ServerIds = [serverA.Id, Guid.NewGuid()],
                LatencyMs = 40
            }
        };
        serverU.PreferredRoute.ServerIds[^1] = serverU.Id;

        var serverV = new ManagedServer { Name = "Server-V", Host = "srv-v", Username = "user" };

        var current = new ManagerConfig
        {
            BaseProxies = [proxy],
            UngroupedServers = [serverA, serverB, serverT, serverU, serverV]
        };

        var incomingProxy = new BaseProxy { Id = proxy.Id, Name = "JH", Host = "jump", Port = 22 };
        var incomingA = new ManagedServer { Id = serverA.Id, Name = serverA.Name, Host = serverA.Host, Username = serverA.Username };
        var incomingB = new ManagedServer { Id = serverB.Id, Name = serverB.Name, Host = serverB.Host, Username = serverB.Username };
        var incomingT = new ManagedServer
        {
            Id = serverT.Id, Name = serverT.Name, Host = serverT.Host, Username = serverT.Username,
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy.Id,
                ServerIds = [serverT.Id],
                LatencyMs = 15
            }
        };
        var incomingU = new ManagedServer
        {
            Id = serverU.Id, Name = serverU.Name, Host = serverU.Host, Username = serverU.Username,
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy.Id,
                ServerIds = [serverA.Id, serverB.Id, serverU.Id],
                LatencyMs = 150
            }
        };
        var incomingV = new ManagedServer
        {
            Id = serverV.Id, Name = serverV.Name, Host = serverV.Host, Username = serverV.Username,
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy.Id,
                ServerIds = [serverA.Id, serverV.Id],
                LatencyMs = 60
            }
        };

        var incoming = new ManagerConfig
        {
            BaseProxies = [incomingProxy],
            UngroupedServers = [incomingA, incomingB, incomingT, incomingU, incomingV]
        };

        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);

        var mergedT = merged.AllServers().Single(s => s.Id == serverT.Id);
        var mergedU = merged.AllServers().Single(s => s.Id == serverU.Id);
        var mergedV = merged.AllServers().Single(s => s.Id == serverV.Id);

        Equal(1, mergedT.PreferredRoute?.ServerIds.Count);
        Equal(serverT.Id, mergedT.PreferredRoute!.ServerIds[0]);

        Equal(2, mergedU.PreferredRoute?.ServerIds.Count);
        Equal(serverA.Id, mergedU.PreferredRoute!.ServerIds[0]);
        Equal(serverU.Id, mergedU.PreferredRoute!.ServerIds[1]);

        Equal(2, mergedV.PreferredRoute?.ServerIds.Count);
        Equal(serverA.Id, mergedV.PreferredRoute!.ServerIds[0]);
        Equal(serverV.Id, mergedV.PreferredRoute!.ServerIds[1]);

        // Duplicate server: importing matching session with ImportDecision.Add
        // must NOT inherit current server's route targeting current server's ID
        var dupPlan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var uDecision = dupPlan.Sessions.Single(s => s.IncomingId == incomingU.Id);
        uDecision.Decision = ImportDecision.Add;
        var incomingDup = new ManagerConfig
        {
            BaseProxies = [incomingProxy],
            UngroupedServers = [new() { Id = incomingU.Id, Name = incomingU.Name, Host = incomingU.Host, Username = incomingU.Username }]
        };
        var mergedDup = ConfigTransfer.MergeSmartImport(current, incomingDup, dupPlan);
        var duplicateU = mergedDup.AllServers().Single(s => s.Id != serverU.Id && s.Name == serverU.Name);
        Equal(null, duplicateU.PreferredRoute);
    }

    private static void AsymmetricLinkVerificationAndDirectedInvalidation()
    {
        var serverA = new ManagedServer { Name = "Server-A", Host = "srv-a", Username = "user" };
        var serverB = new ManagedServer { Name = "Server-B", Host = "srv-b", Username = "user" };
        var serverC = new ManagedServer { Name = "Server-C", Host = "srv-c", Username = "user" };
        var linkAB = new ServerLink { FromServerId = serverA.Id, ToServerId = serverB.Id, Discovered = true };
        var linkBA = new ServerLink { FromServerId = serverB.Id, ToServerId = serverA.Id, Discovered = true };
        var linkBC = new ServerLink { FromServerId = serverB.Id, ToServerId = serverC.Id, Discovered = true };

        var proxy = new BaseProxy { Name = "JH", Host = "jump", Port = 22 };
        serverB.PreferredRoute = new CachedRoute
        {
            ProxyId = proxy.Id,
            ServerIds = [serverA.Id, serverB.Id],
            LatencyMs = 50
        };
        serverC.PreferredRoute = new CachedRoute
        {
            ProxyId = proxy.Id,
            ServerIds = [serverB.Id, serverC.Id],
            LatencyMs = 70
        };

        var config = new ManagerConfig
        {
            BaseProxies = [proxy],
            UngroupedServers = [serverA, serverB, serverC],
            Links = [linkAB, linkBA, linkBC]
        };

        var now = DateTimeOffset.UtcNow;
        var resultBA = new ConnectivityResult(serverA.Id, true, "OK", TimeSpan.FromMilliseconds(45), "DirectTcpIp", proxy.Id, serverB.Id);

        ServerLinkPairPolicy.RememberDirectedSuccess(config, resultBA, now);
        Equal(now, linkBA.LastSuccessUtc);
        Equal("DirectTcpIp", linkBA.LastStrategy);
        Equal(null, linkAB.LastSuccessUtc);
        Equal("", linkAB.LastStrategy);

        linkAB.LastSuccessUtc = now;
        linkAB.LastStrategy = "DirectTcpIp";
        ServerLinkPairPolicy.InvalidateDirection(config, serverA.Id, serverB.Id);

        Equal(null, linkAB.LastSuccessUtc);
        Equal("", linkAB.LastStrategy);
        Equal(now, linkBA.LastSuccessUtc);
        Equal("DirectTcpIp", linkBA.LastStrategy);

        Equal(null, serverB.PreferredRoute);
        Equal(2, serverC.PreferredRoute?.ServerIds.Count);

        var hopEx = new SshHopException(serverA.Id, serverB.Id, "Hop failed", new Exception("inner"));
        Equal(serverA.Id, hopEx.SourceId);
        Equal(serverB.Id, hopEx.TargetId);
        Equal("Hop failed", hopEx.Message);
    }

    private static void AsymmetricLinkMapVisuals()
    {
        var server1 = new ManagedServer { Name = "Node-1", Host = "node-1" };
        var server2 = new ManagedServer { Name = "Node-2", Host = "node-2" };
        var server3 = new ManagedServer { Name = "Node-3", Host = "node-3" };
        var now = DateTimeOffset.UtcNow;

        // 1. One-way: Node-1 -> Node-2 confirmed, Node-2 -> Node-1 not confirmed
        var link12 = new ServerLink { FromServerId = server1.Id, ToServerId = server2.Id, LastSuccessUtc = now };
        var link21 = new ServerLink { FromServerId = server2.Id, ToServerId = server1.Id, LastSuccessUtc = null };

        // 2. Bidirectional: Node-2 <-> Node-3 both confirmed
        var link23 = new ServerLink { FromServerId = server2.Id, ToServerId = server3.Id, LastSuccessUtc = now };
        var link32 = new ServerLink { FromServerId = server3.Id, ToServerId = server2.Id, LastSuccessUtc = now.AddMinutes(-5) };

        // 3. Unconfirmed: Node-1 <-> Node-3 both null
        var link13 = new ServerLink { FromServerId = server1.Id, ToServerId = server3.Id, LastSuccessUtc = null };
        var link31 = new ServerLink { FromServerId = server3.Id, ToServerId = server1.Id, LastSuccessUtc = null };

        var config = new ManagerConfig
        {
            UngroupedServers = [server1, server2, server3],
            Links = [link12, link21, link23, link32, link13, link31]
        };

        var map = LinkMapLayout.Build(config);
        Equal(3, map.Nodes.Count);
        Equal(3, map.Edges.Count);

        // Edge 1-2
        var edge12 = map.Edges.Single(e => (e.ServerAId == server1.Id && e.ServerBId == server2.Id) || (e.ServerAId == server2.Id && e.ServerBId == server1.Id));
        Equal(true, edge12.IsAvailable);
        Equal(true, edge12.IsDirected);
        Equal(false, edge12.IsBidirectional);
        if (edge12.ServerAId == server1.Id)
        {
            Equal(true, edge12.AtoBAvailable);
            Equal(false, edge12.BtoAAvailable);
        }
        else
        {
            Equal(false, edge12.AtoBAvailable);
            Equal(true, edge12.BtoAAvailable);
        }
        var tip12 = LinkMapLayout.FormatTooltip(edge12, "Node-1", "Node-2");
        Equal(true, tip12.Contains("односторонняя"));
        Equal(true, tip12.Contains("не подтверждена"));

        // Edge 2-3
        var edge23 = map.Edges.Single(e => (e.ServerAId == server2.Id && e.ServerBId == server3.Id) || (e.ServerAId == server3.Id && e.ServerBId == server2.Id));
        Equal(true, edge23.IsAvailable);
        Equal(false, edge23.IsDirected);
        Equal(true, edge23.IsBidirectional);
        var tip23 = LinkMapLayout.FormatTooltip(edge23, "Node-2", "Node-3");
        Equal(true, tip23.Contains("двусторонняя"));

        // Edge 1-3
        var edge13 = map.Edges.Single(e => (e.ServerAId == server1.Id && e.ServerBId == server3.Id) || (e.ServerAId == server3.Id && e.ServerBId == server1.Id));
        Equal(false, edge13.IsAvailable);
        Equal(false, edge13.IsDirected);
        Equal(false, edge13.IsBidirectional);
        var tip13 = LinkMapLayout.FormatTooltip(edge13, "Node-1", "Node-3");
        Equal(true, tip13.Contains("ни в одну сторону"));
    }

    private static void AsymmetricConnectivityPairSelection()
    {
        var server1 = new ManagedServer { Name = "Alpha", Host = "alpha" };
        var server2 = new ManagedServer { Name = "Beta", Host = "beta" };
        var server3 = new ManagedServer { Name = "Gamma", Host = "gamma" };
        var now = DateTimeOffset.UtcNow;

        // Alpha -> Beta verified; Beta -> Alpha unverified (null)
        var link12 = new ServerLink { FromServerId = server1.Id, ToServerId = server2.Id, LastSuccessUtc = now };
        var link21 = new ServerLink { FromServerId = server2.Id, ToServerId = server1.Id, LastSuccessUtc = null };

        var config = new ManagerConfig
        {
            UngroupedServers = [server1, server2, server3],
            Links = [link12, link21]
        };

        // When building missing links with skipExisting = true and skipConfirmedOnly = true:
        // Alpha -> Beta is confirmed -> skipped
        // Beta -> Alpha is unconfirmed (null) -> INCLUDED! And ReverseVerified is true (because Alpha -> Beta is confirmed)
        var missing = ConnectivityPairSelectionPolicy.Build(config, [server1, server2, server3], skipExisting: true, skipConfirmedOnly: true);
        var betaToAlpha = missing.Single(p => p.SourceId == server2.Id && p.TargetId == server1.Id);
        Equal(true, betaToAlpha.ReverseVerified);
        Equal(false, betaToAlpha.ForwardVerified);
        Equal(true, betaToAlpha.Display.Contains("(обратное подтверждено)"));

        // Alpha -> Beta must NOT be in missing because it is already confirmed
        Equal(false, missing.Any(p => p.SourceId == server1.Id && p.TargetId == server2.Id));

        // When building all pairs (skipExisting = false):
        var all = ConnectivityPairSelectionPolicy.Build(config, [server1, server2], skipExisting: false);
        var alphaToBeta = all.Single(p => p.SourceId == server1.Id && p.TargetId == server2.Id);
        Equal(true, alphaToBeta.ForwardVerified);
        Equal(false, alphaToBeta.ReverseVerified);
        Equal(true, alphaToBeta.Display.Contains("(исходящее уже подтверждено)"));
    }

    private static void AsymmetricSavedLinkTreeItemStatus()
    {
        // Case 1: Outgoing confirmed, incoming unconfirmed
        var (dir1, status1) = SavedLinkDirectionPolicy.Evaluate(true, false);
        Equal("→", dir1);
        Equal(true, status1.Contains("Исходящая связь подтверждена; обратная не подтверждена"));

        // Case 2: Outgoing unconfirmed, incoming confirmed
        var (dir2, status2) = SavedLinkDirectionPolicy.Evaluate(false, true);
        Equal("←", dir2);
        Equal(true, status2.Contains("Обратная связь подтверждена; исходящая не подтверждена"));

        // Case 3: Both confirmed
        var (dir3, status3) = SavedLinkDirectionPolicy.Evaluate(true, true);
        Equal("↔", dir3);
        Equal(true, status3.Contains("Двусторонняя"));

        // Case 4: Neither confirmed
        var (dir4, status4) = SavedLinkDirectionPolicy.Evaluate(false, false);
        Equal("•—•", dir4);
        Equal(true, status4.Contains("ни одно направление не подтверждено"));
    }

    private static void AsymmetricAdaptiveLinkDiscoveryPreservesKnownOneWay()
    {
        var s1 = new ManagedServer { Name = "Server-A", Host = "srv-a" };
        var s2 = new ManagedServer { Name = "Server-B", Host = "srv-b" };
        var config = new ManagerConfig { UngroupedServers = [s1, s2] };
        var now = DateTimeOffset.UtcNow;

        // 1. New link: no links exist in config -> optimistic bidirectional
        Equal(false, ServerLinkPairPolicy.IsKnownAsymmetric(config, s1.Id, s2.Id));
        var res1 = new ConnectivityResult(s2.Id, true, "OK", TimeSpan.FromMilliseconds(50), "direct-tcpip", SourceId: s1.Id);
        var reqReverse1 = ServerLinkPairPolicy.RememberAdaptiveSuccess(config, res1, now);
        Equal(false, reqReverse1);
        Equal(2, config.Links.Count);
        var linkAB = config.Links.Single(l => l.FromServerId == s1.Id && l.ToServerId == s2.Id);
        var linkBA = config.Links.Single(l => l.FromServerId == s2.Id && l.ToServerId == s1.Id);
        Equal(true, linkAB.LastSuccessUtc.HasValue);
        Equal(true, linkBA.LastSuccessUtc.HasValue);

        // 2. Simulate B->A failing and being invalidated -> link becomes known one-way A->B
        ServerLinkPairPolicy.InvalidateDirection(config, s2.Id, s1.Id);
        Equal(true, linkAB.LastSuccessUtc.HasValue);
        Equal(false, linkBA.LastSuccessUtc.HasValue);
        Equal(true, ServerLinkPairPolicy.IsKnownAsymmetric(config, s1.Id, s2.Id));

        // 3. Retesting A->B must NOT overwrite B->A as synthetic success, must signal reverse check
        var later = now.AddMinutes(10);
        var res2 = new ConnectivityResult(s2.Id, true, "OK", TimeSpan.FromMilliseconds(45), "direct-tcpip", SourceId: s1.Id);
        var reqReverse2 = ServerLinkPairPolicy.RememberAdaptiveSuccess(config, res2, later);
        Equal(true, reqReverse2);
        Equal(later, linkAB.LastSuccessUtc);
        Equal(false, linkBA.LastSuccessUtc.HasValue); // B->A is preserved as unconfirmed!

        // 4. Checking B->A actually fails -> remains one-way
        ServerLinkPairPolicy.InvalidateDirection(config, s2.Id, s1.Id);
        Equal(true, ServerLinkPairPolicy.IsKnownAsymmetric(config, s1.Id, s2.Id));
        Equal(false, linkBA.LastSuccessUtc.HasValue);

        // 5. Checking B->A succeeds -> legitimately becomes bidirectional
        var resBA = new ConnectivityResult(s1.Id, true, "OK", TimeSpan.FromMilliseconds(60), "direct-tcpip", SourceId: s2.Id);
        ServerLinkPairPolicy.RememberDirectedSuccess(config, resBA, later.AddMinutes(1));
        Equal(false, ServerLinkPairPolicy.IsKnownAsymmetric(config, s1.Id, s2.Id));
        Equal(true, linkAB.LastSuccessUtc.HasValue);
        Equal(true, linkBA.LastSuccessUtc.HasValue);
    }

    public static void BatchTaskHasFilesAndMissingSourcesDetection()
    {
        var taskNoFiles = new BatchTaskDefinition
        {
            Name = "NoFiles",
            Steps = [
                new() { Kind = BatchTaskStepKind.Command, Command = "echo hello" },
                new() { Kind = BatchTaskStepKind.Download, Source = "/var/log/syslog" }
            ]
        };
        Equal(false, BatchTaskFile.HasFiles(taskNoFiles));

        var tempDir = Path.Combine(Path.GetTempPath(), "batch-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var existingFile = Path.Combine(tempDir, "existing.txt");
            File.WriteAllText(existingFile, "content");

            var taskWithFiles = new BatchTaskDefinition
            {
                Name = "WithFiles",
                Steps = [
                    new() { Kind = BatchTaskStepKind.Upload, Source = existingFile + "\n" + Path.Combine(tempDir, "missing.txt") },
                    new() { Kind = BatchTaskStepKind.Template, Source = Path.Combine(tempDir, "nonexistent-template.j2") }
                ]
            };
            Equal(true, BatchTaskFile.HasFiles(taskWithFiles));

            var missing = BatchTaskFile.GetMissingLocalSources(tempDir, taskWithFiles);
            Equal(2, missing.Count);
            Equal(true, missing.Contains(Path.Combine(tempDir, "missing.txt")));
            Equal(true, missing.Contains(Path.Combine(tempDir, "nonexistent-template.j2")));
            Equal(false, missing.Contains(existingFile));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static void BatchAndAnsibleLogFilteringAndFormatting()
    {
        var serverId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var logs = new List<BatchTaskLog>
        {
            new(now, serverId, "srv1", "Подготовка", "Подключение к 192.168.1.10", BatchLogLevel.Info, "Шаг"),
            new(now.AddSeconds(1), serverId, "srv1", "Шаг 1", "Вывод команды: ok", BatchLogLevel.Info, "Консоль"),
            new(now.AddSeconds(2), serverId, "srv1", "Шаг 2", "Вывод ошибок: warn", BatchLogLevel.Warning, "Консоль"),
            new(now.AddSeconds(3), serverId, "srv1", "Шаг 3", "Ошибка выполнения", BatchLogLevel.Error, "Шаг")
        };

        var formattedFull = BatchLogFormatter.Format(logs[1], hideTimestamp: false, hidePrefix: false);
        Equal(true, formattedFull.Contains("[2026-09-08 12:00:01.000]"));
        Equal(true, formattedFull.Contains("[Info] srv1 | Консоль | Шаг 1 | Вывод команды: ok"));

        var formattedNoTime = BatchLogFormatter.Format(logs[1], hideTimestamp: true, hidePrefix: false);
        Equal(false, formattedNoTime.Contains("2026-09-08"));
        Equal(true, formattedNoTime.StartsWith("[Info] srv1 | Консоль | Шаг 1 | "));

        var formattedNoPrefix = BatchLogFormatter.Format(logs[1], hideTimestamp: false, hidePrefix: true);
        Equal(true, formattedNoPrefix.Contains("[2026-09-08 12:00:01.000] Вывод команды: ok"));
        Equal(false, formattedNoPrefix.Contains("srv1"));

        var formattedCleanMessageOnly = BatchLogFormatter.Format(logs[1], hideTimestamp: true, hidePrefix: true);
        Equal("Вывод команды: ok" + Environment.NewLine, formattedCleanMessageOnly);

        var consoleFiltered = BatchLogFormatter.Filter(logs, null, null, consoleOnly: true, errorsAndWarningsOnly: false).ToList();
        Equal(2, consoleFiltered.Count);
        Equal(true, consoleFiltered.All(x => x.Source == "Консоль"));

        var errorsFiltered = BatchLogFormatter.Filter(logs, null, null, consoleOnly: false, errorsAndWarningsOnly: true).ToList();
        Equal(2, errorsFiltered.Count);
        Equal(true, errorsFiltered.All(x => x.Level is BatchLogLevel.Warning or BatchLogLevel.Error));

        var infoOnlyFiltered = BatchLogFormatter.Filter(logs, null, BatchLogLevel.Info, consoleOnly: false, errorsAndWarningsOnly: false).ToList();
        Equal(2, infoOnlyFiltered.Count);
        Equal(true, infoOnlyFiltered.All(x => x.Level == BatchLogLevel.Info));

        var errorOnlyFiltered = BatchLogFormatter.Filter(logs, null, BatchLogLevel.Error, consoleOnly: false, errorsAndWarningsOnly: false).ToList();
        Equal(1, errorOnlyFiltered.Count);
        Equal(BatchLogLevel.Error, errorOnlyFiltered[0].Level);

        var ansibleEntries = new List<AnsibleLogEntry>
        {
            new(now, "Подготавливаю playbook", IsConsole: false, IsErrorOrWarning: false),
            new(now.AddSeconds(1), "ok: [srv1] => changed=false", IsConsole: true, IsErrorOrWarning: false),
            new(now.AddSeconds(2), "fatal: [srv1] => FAILED!", IsConsole: true, IsErrorOrWarning: true),
            new(now.AddSeconds(3), "Сбой: связь потеряна", IsConsole: false, IsErrorOrWarning: true)
        };

        var ansibleConsoleOnly = AnsibleLogFormatter.Filter(ansibleEntries, consoleOnly: true, errorsAndWarningsOnly: false).ToList();
        Equal(2, ansibleConsoleOnly.Count);
        Equal(true, ansibleConsoleOnly.All(x => x.IsConsole));

        var ansibleErrorsOnly = AnsibleLogFormatter.Filter(ansibleEntries, consoleOnly: false, errorsAndWarningsOnly: true).ToList();
        Equal(2, ansibleErrorsOnly.Count);
        Equal(true, ansibleErrorsOnly.All(x => x.IsErrorOrWarning));

        var ansibleFormattedWithTime = AnsibleLogFormatter.Format(ansibleEntries[0], hideTimestamp: false);
        Equal(true, ansibleFormattedWithTime.Contains("[2026-09-08 12:00:00] Подготавливаю playbook"));

        var ansibleFormattedNoTime = AnsibleLogFormatter.Format(ansibleEntries[0], hideTimestamp: true);
        Equal("Подготавливаю playbook" + Environment.NewLine, ansibleFormattedNoTime);
    }

    public static void BatchStepShiftAndMultiSelectionPolicy()
    {
        Equal(false, BatchStepInteractionPolicy.OpenEditorOnDoubleClick(true));
        Equal(true, BatchStepInteractionPolicy.OpenEditorOnDoubleClick(false));
        var steps = new List<BatchTaskStep>
        {
            new() { Kind = BatchTaskStepKind.Command, Name = "Step 0", Command = "cmd0", IsSelected = false },
            new() { Kind = BatchTaskStepKind.Command, Name = "Step 1", Command = "cmd1", IsSelected = false },
            new() { Kind = BatchTaskStepKind.Command, Name = "Step 2", Command = "cmd2", IsSelected = false },
            new() { Kind = BatchTaskStepKind.Command, Name = "Step 3", Command = "cmd3", IsSelected = false },
            new() { Kind = BatchTaskStepKind.Command, Name = "Step 4", Command = "cmd4", IsSelected = false }
        };

        Equal(false, BatchStepInteractionPolicy.DetermineSelectAllState(steps));

        BatchStepInteractionPolicy.ToggleStepSelection(steps[1]);
        Equal(true, steps[1].IsSelected);
        Equal(null, BatchStepInteractionPolicy.DetermineSelectAllState(steps));

        // Shift selection from 1 to 3 (top-down)
        var anchor = 1;
        BatchStepInteractionPolicy.ApplyShiftSelection(steps, ref anchor, 3);
        Equal(false, steps[0].IsSelected);
        Equal(true, steps[1].IsSelected);
        Equal(true, steps[2].IsSelected);
        Equal(true, steps[3].IsSelected);
        Equal(false, steps[4].IsSelected);
        Equal(1, anchor);

        // Shift selection with invalid anchor defaults to 0
        var invalidAnchor = -1;
        BatchStepInteractionPolicy.ApplyShiftSelection(steps, ref invalidAnchor, 1);
        Equal(true, steps[0].IsSelected);
        Equal(true, steps[1].IsSelected);

        // Out of bounds target is ignored
        BatchStepInteractionPolicy.ApplyShiftSelection(steps, ref anchor, 999);
        Equal(false, steps[4].IsSelected);

        // Select all
        foreach (var s in steps) s.IsSelected = true;
        Equal(true, BatchStepInteractionPolicy.DetermineSelectAllState(steps));

        // Empty list
        Equal(false, BatchStepInteractionPolicy.DetermineSelectAllState(new List<BatchTaskStep>()));
    }

    private sealed class ChunkedMemoryStream(byte[] buffer, int chunkSize) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) =>
            base.ReadAsync(destination[..Math.Min(destination.Length, chunkSize)], cancellationToken);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiTTYManager.Core;

partial class SelfTestRunner
{
    private static void AnsibleReadsSharedBootLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "ansible-boot-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            writer.Write(Encoding.UTF8.GetBytes("first\nsecond\nthird\n"));
            writer.Flush();
            Equal("second|third", string.Join('|', AnsibleVmSession.ReadSharedTail(path, 2)));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void AnsibleInventoryIsSafe()
    {
        var first = Server("Сервер Один"); first.Username = "operator"; first.Password = "do-not-write";
        var second = Server("Server One"); second.Username = "root"; second.RootPassword = "also-secret";
        var usedHosts = new HashSet<string>(); var usedGroups = new HashSet<string>();
        var group = AnsibleInventoryGenerator.SafeUniqueName("Группа 1", Guid.NewGuid(), usedGroups);
        var text = AnsibleInventoryGenerator.Generate([
            new(first, 41001, AnsibleInventoryGenerator.SafeUniqueName(first.Name, first.Id, usedHosts), group, "Группа 1", 0,
                "Server One 10.0.2.2", [new() { Name = "HTTPS", Kind = BatchTunnelKind.Remote,
                    BindHost = "127.0.0.1", BindPort = 8443, DestinationHost = "example.org", DestinationPort = 443 }]),
            new(second, 41002, AnsibleInventoryGenerator.SafeUniqueName(second.Name, second.Id, usedHosts), group, "Группа 1")]);
        Equal(true, text.Contains("ansible_host: 10.0.2.2", StringComparison.Ordinal));
        Equal(true, text.Contains("ansible_port: 41001", StringComparison.Ordinal));
        Equal(true, text.Contains("ConnectTimeout=10", StringComparison.Ordinal));
        Equal(true, text.Contains("ServerAliveInterval=10", StringComparison.Ordinal));
        Equal(true, text.Contains("ServerAliveCountMax=1", StringComparison.Ordinal));
        Equal(true, text.Contains("kitty_download_dir: '__KITTY_DOWNLOAD_DIR__/Server One 10.0.2.2'", StringComparison.Ordinal));
        Equal(true, text.Contains("kind: remote", StringComparison.Ordinal));
        Equal(true, text.Contains("bind_port: 8443", StringComparison.Ordinal));
        Equal(false, text.Contains(first.Password, StringComparison.Ordinal));
        Equal(false, text.Contains(second.RootPassword, StringComparison.Ordinal));
        Equal(2, text.Split("kitty_server_display:", StringSplitOptions.None).Length - 1);
    }

    private static void AnsibleTaskWorkspaceCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var task = Path.Combine(root, "Data", "Tasks", "Ansible", "kept"); Directory.CreateDirectory(task);
            var playbook = Path.Combine(task, "site.yml"); File.WriteAllText(playbook, "- hosts: all\n");
            File.WriteAllText(Path.Combine(task, "user.txt"), "keep");
            var service = Path.Combine(task, AnsibleTaskWorkspacePolicy.ServiceFolderName); Directory.CreateDirectory(service);
            File.WriteAllText(Path.Combine(service, "inventory.yml"), "old");
            var workspace = AnsibleTaskWorkspacePolicy.Prepare(Path.Combine(root, "Data", "Tasks", "Ansible"), playbook);
            Equal(false, workspace.CreatedForRun);
            Equal(true, File.Exists(Path.Combine(task, "user.txt")));
            Equal(false, File.Exists(Path.Combine(service, "inventory.yml")));
            AnsibleTaskWorkspacePolicy.DeleteSecrets(workspace);
            Equal(false, Directory.Exists(workspace.SecretDirectory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleWorkspaceCopiesAdjacentDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-adjacent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "source");
            var templates = Path.Combine(root, "templates");
            Directory.CreateDirectory(Path.Combine(source, "files", "nested"));
            var playbook = Path.Combine(source, "site.yml");
            File.WriteAllText(playbook, "- hosts: all\n  tasks:\n    - copy:\n        src: files/nested/payload.bin\n");
            File.WriteAllText(Path.Combine(source, "vars.yaml"), "value: safe\n");
            File.WriteAllText(Path.Combine(source, "files", "nested", "payload.bin"), "payload");
            File.WriteAllText(Path.Combine(source, "unrelated.exe"), "do-not-copy");

            var workspace = AnsibleTaskWorkspacePolicy.Prepare(templates, playbook);

            Equal(true, workspace.CreatedForRun);
            Equal(true, File.Exists(Path.Combine(workspace.TaskDirectory, "site.yml")));
            Equal(true, File.Exists(Path.Combine(workspace.TaskDirectory, "vars.yaml")));
            Equal("payload", File.ReadAllText(Path.Combine(workspace.TaskDirectory, "files", "nested", "payload.bin")));
            Equal(false, File.Exists(Path.Combine(workspace.TaskDirectory, "unrelated.exe")));
            Equal(false, Directory.Exists(Path.Combine(source, AnsibleTaskWorkspacePolicy.ServiceFolderName)));
            AnsibleTaskWorkspacePolicy.DeleteSecrets(workspace);
            AnsibleTaskWorkspacePolicy.DeleteCreatedTask(templates, workspace);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleTaskDeletionGuard()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var templates = Path.Combine(root, "Templates"); Directory.CreateDirectory(templates);
            var external = Path.Combine(root, "external.yml"); File.WriteAllText(external, "- hosts: all\n");
            var workspace = AnsibleTaskWorkspacePolicy.Prepare(templates, external);
            Equal(true, workspace.CreatedForRun);
            AnsibleTaskWorkspacePolicy.DeleteSecrets(workspace);
            AnsibleTaskWorkspacePolicy.DeleteCreatedTask(templates, workspace);
            Equal(false, Directory.Exists(workspace.TaskDirectory));
            var rejected = false;
            try { AnsibleTaskWorkspacePolicy.DeleteCreatedTask(templates,
                new(Path.Combine(root, "outside"), "", "", true)); }
            catch (InvalidOperationException) { rejected = true; }
            Equal(true, rejected);
            // All paths exist: an unrelated missing-directory exception must not mask a lost guard.
            var existing = Path.Combine(templates, "existing");
            var sibling = templates + "-outside";
            foreach (var directory in new[] { existing, sibling })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "keep.txt"), "user data");
            }
            foreach (var candidate in new[]
            {
                new AnsibleTaskWorkspace(existing, "", "", false),
                new AnsibleTaskWorkspace(sibling, "", "", true),
                new AnsibleTaskWorkspace(templates, "", "", true)
            })
            {
                var denied = false;
                try { AnsibleTaskWorkspacePolicy.DeleteCreatedTask(templates, candidate); }
                catch (InvalidOperationException) { denied = true; }
                Equal(true, denied);
                Equal("user data", File.ReadAllText(Path.Combine(existing, "keep.txt")));
                Equal("user data", File.ReadAllText(Path.Combine(sibling, "keep.txt")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleRuntimeManifestVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "licenses"));
            var component = Path.Combine(root, "qemu.exe"); File.WriteAllText(component, "qemu");
            File.WriteAllText(Path.Combine(root, "licenses", "qemu.txt"), "GPL");
            var manifest = new AnsibleRuntimeManifest { AnsibleCoreVersion = "2.16.14", Components = [new()
            {
                Name = "QEMU", RelativePath = "qemu.exe", LicenseRelativePath = "licenses/qemu.txt",
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(component))).ToLowerInvariant()
            }]};
            File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest));
            Equal(true, AnsibleRuntimeVerifier.Verify(root).Ready);
            File.AppendAllText(component, "corrupt");
            Equal(false, AnsibleRuntimeVerifier.Verify(root).Ready);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleReadinessStates()
    {
        Equal(AnsibleReadinessState.Ready, AnsibleReadinessPolicy.Summarize([
            new("runtime", true, "ok"), new("vm", true, "ok")]).State);
        Equal(AnsibleReadinessState.RequiresConfiguration, AnsibleReadinessPolicy.Summarize([
            new("WHPX", false, "Компонент отключён", "Enable-WindowsOptionalFeature ...")]).State);
        Equal(AnsibleReadinessState.UnknownError, AnsibleReadinessPolicy.Summarize([
            new("boot", false, "unexpected", ExitCode: 17, Unknown: true)]).State);
    }

    private static void AnsibleDiagnosticsAreLocaleIndependent()
    {
        Equal(false, AnsibleWindowsDiagnostics.HypervisorLaunchScript.Contains("Select-String", StringComparison.Ordinal));
        Equal(false, AnsibleWindowsDiagnostics.HypervisorLaunchScript.Contains("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase));
        Equal(true, AnsibleWindowsDiagnostics.HypervisorLaunchScript.Contains("0x250000F0", StringComparison.Ordinal));
        Equal(true, AnsibleWindowsDiagnostics.HypervisorPlatformScript.Contains("FeatureState]::Enabled", StringComparison.Ordinal));
        Equal(true, AnsibleWindowsDiagnostics.InterpretProtocolResult("x", "READY", 0, "off", "fix").Success);
        Equal("fix", AnsibleWindowsDiagnostics.InterpretProtocolResult("x", "DISABLED", 0, "off", "fix").EnableCommand);
        Equal(true, AnsibleWindowsDiagnostics.InterpretProtocolResult("x", "localized error", 1, "off", "fix").Unknown);
        var effective = AnsibleWindowsDiagnostics.ApplyEffectiveHypervisorState([
            new("Windows-гипервизор", true, "Готово."),
            new("Запуск гипервизора Windows", false, "Не удалось", Unknown: true)
        ]);
        Equal(true, effective[1].Success);
        Equal(false, effective[1].Unknown);
        Equal(null, effective[1].EnableCommand);

        var absent = AnsibleWindowsDiagnostics.ApplyEffectiveHypervisorState([
            new("Windows-гипервизор", false, "Не запущен."),
            new("Запуск гипервизора Windows", false, "Не удалось", Unknown: true)
        ]);
        Equal(false, absent[1].Success);
        Equal(true, absent[1].Unknown);
        Equal(false, AnsibleWindowsDiagnostics.InterpretProtocolResult(
            "x", "localized error", 1, "off", "fix").Message.Contains("администратор", StringComparison.OrdinalIgnoreCase));
    }

    private static void AnsibleWorkspaceIsLazyUntilPrepare()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-ansible-lazy-" + Guid.NewGuid().ToString("N"));
        var templates = Path.Combine(root, "Data", "Tasks", "Ansible");
        try
        {
            Equal(false, Directory.Exists(templates));
            var missing = Path.Combine(root, "missing.yml");
            try { AnsibleTaskWorkspacePolicy.Prepare(templates, missing); throw new Exception("Expected failure."); }
            catch (FileNotFoundException) { }
            Equal(false, Directory.Exists(templates));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleQemuArguments()
    {
        var args = AnsibleVmSession.BuildQemuArguments("C:\\Runtime\\Ansible", "C:\\Temp\\overlay.qcow2", "control",
            "C:\\Temp\\boot.log");
        var arguments = args.ToList();
        foreach (var (flag, value) in new[] { ("-machine", "q35,accel=whpx"),
                     ("-display", "none"), ("-vga", "none"), ("-monitor", "none"),
                     ("-netdev", "user,id=net0"), ("-chardev", "pipe,id=km,path=control") })
        {
            var index = arguments.IndexOf(flag);
            Equal(true, index >= 0 && index + 1 < arguments.Count);
            Equal(value, arguments[index + 1]);
        }
        Equal(true, args.Contains("virtio-net-pci,netdev=net0,romfile="));
        Equal(false, args.Contains("virtio-net-pci,netdev=net0"));
        Equal(true, args.Contains("none"));
        Equal(true, args.Contains("-vga"));
        Equal(true, args.Contains("isa-serial,chardev=km"));
        Equal(true, args.Contains("pipe,id=km,path=control"));
        Equal(false, args.Any(argument => argument.Contains("\\\\.\\pipe", StringComparison.Ordinal)));
        Equal(true, args.Any(x => x.Contains("overlay.qcow2", StringComparison.Ordinal)));
        Equal(true, args.Any(x => x.EndsWith("vmlinuz-virt", StringComparison.Ordinal)));
        Equal(true, args.Any(x => x.Contains("ttyS0", StringComparison.Ordinal)));
        Equal(true, args.Contains("file:C:\\Temp\\boot.log"));
        Equal(1, args.Count(argument => argument.EndsWith("initramfs-virt", StringComparison.Ordinal)));
    }

    private static void AnsibleRunDefaults()
    {
        Equal(0, AnsibleRunPolicy.NormalizeVerbosity(-1));
        Equal(3, AnsibleRunPolicy.NormalizeVerbosity(3));
        Equal(4, AnsibleRunPolicy.NormalizeVerbosity(99));
        Equal("sudo", AnsibleRunPolicy.BecomeMethod("sudo -s"));
        Equal("su", AnsibleRunPolicy.BecomeMethod("su -"));
        Equal(null, AnsibleRunPolicy.BecomeMethod(""));
        var root = Path.Combine(Path.GetTempPath(), "kitty-manager-app");
        Equal(Path.Combine(Path.GetFullPath(root), "Data", "Downloads"), AnsibleRunPolicy.DefaultDownloadDirectory(root));
        var folder = AnsibleRunPolicy.ServerDownloadFolder("Server A", "192.0.2.10:22");
        Equal("Server A 192.0.2.10_22", folder);
        Equal("SSH VM→локальный маршрут:22", AnsibleRunPolicy.HumanizeVmRoute("SSH 10.0.2.2:22"));
    }

    private static void AnsibleWindowsMaterialsComeFromYaml()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-material-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var material = Path.Combine(root, "payload.bin"); File.WriteAllText(material, "safe");
            var playbook = Path.Combine(root, "site.yml");
            File.WriteAllText(playbook, $"vars:\n  {AnsibleWindowsMaterialScanner.VariableName}:\n    - '{material}'\ntasks: []\n");
            var found = AnsibleWindowsMaterialScanner.Scan(playbook);
            Equal(1, found.Count); Equal(material, found[0].SourcePath); Equal("payload.bin", found[0].ArchiveName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleArchiveExcludesServiceFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-archive-" + Guid.NewGuid().ToString("N"));
        string? archivePath = null;
        try
        {
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, AnsibleTaskWorkspacePolicy.ServiceFolderName));
            File.WriteAllText(Path.Combine(root, "site.yml"), "- hosts: all\n");
            File.WriteAllText(Path.Combine(root, AnsibleTaskWorkspacePolicy.ServiceFolderName, "inventory.yml"), "secret-runtime");
            archivePath = AnsibleTaskArchive.CreateTemporaryFileAsync(root, [], CancellationToken.None).GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
            Equal(true, zip.Entries.Any(x => x.FullName == "site.yml"));
            Equal(false, zip.Entries.Any(x => x.FullName.Contains(AnsibleTaskWorkspacePolicy.ServiceFolderName, StringComparison.Ordinal)));
        }
        finally { if (archivePath is not null) File.Delete(archivePath); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AnsibleVmEventMissingFields()
    {
        var normalized = AnsibleVmEventPolicy.Normalize(new AnsibleVmEvent("upload", Text: "Передано")
        {
            Message = null!, RelativePath = null!, DataBase64 = null!
        });
        Equal("upload", normalized.Type);
        Equal("Передано", normalized.Text);
        Equal("", normalized.Message);
        Equal("", normalized.RelativePath);
        Equal("", normalized.DataBase64);
        Equal(null, AnsibleVmEventPolicy.ParseLine(""));
        Equal(null, AnsibleVmEventPolicy.ParseLine("  \r\n"));
        Equal(null, AnsibleVmEventPolicy.ParseLine("{\"action\":\"upload_end\",\"uploadId\":\"safe\"}"));
        var unknownRejected = false;
        try { AnsibleVmEventPolicy.ParseLine("{\"action\":\"unexpected\"}"); }
        catch (InvalidDataException) { unknownRejected = true; }
        Equal(true, unknownRejected);
        var parsed = AnsibleVmEventPolicy.ParseLine("{\"type\":\"upload\",\"text\":\"3907\"}");
        Equal("upload", parsed!.Type);
        Equal("3907", parsed.Text);
        Equal("", parsed.Message);
    }

    private static void AnsibleVmEventsAreReadable()
    {
        Equal("Ansible-playbook запущен.", AnsibleVmEventFormatter.Format(new("started")));
        Equal("Задача: Проверить доступность [ansible.builtin.ping]", AnsibleVmEventFormatter.Format(new("callback",
            Text: "{\"kind\":\"task\",\"name\":\"Проверить доступность\",\"action\":\"ansible.builtin.ping\"}")));
        Equal("Сервер: ошибка — отказ", AnsibleVmEventFormatter.Format(new("callback",
            Text: "{\"kind\":\"failed\",\"host\":\"node_1\",\"result\":{\"msg\":\"отказ\"}}"),
            host => host == "node_1" ? "Сервер" : host));
        var stats = AnsibleVmEventFormatter.Format(new("callback",
            Text: "{\"kind\":\"stats\",\"hosts\":{\"node_1\":{\"ok\":3,\"changed\":1,\"failures\":0,\"unreachable\":0,\"skipped\":2}}}"),
            _ => "Сервер");
        Equal(true, stats.Contains("ok=3", StringComparison.Ordinal));
        Equal(true, stats.Contains("изменено=1", StringComparison.Ordinal));
        Equal(false, stats.Contains("callback", StringComparison.OrdinalIgnoreCase));
        Equal("Событие Ansible: []", AnsibleVmEventFormatter.Format(new("callback", Text: "[]")));
    }

    private static void AnsibleArchiveTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-stream-" + Guid.NewGuid().ToString("N"));
        string? archivePath = null;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "site.yml"), "- hosts: all\n");
            archivePath = AnsibleTaskArchive.CreateTemporaryFileAsync(root, [], CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(true, File.Exists(archivePath));
            using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
            Equal(true, archive.GetEntry("site.yml") is not null);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var before = Directory.EnumerateFiles(Path.GetDirectoryName(archivePath)!, "task-*.zip").Count();
            var cancellationObserved = false;
            try
            {
                AnsibleTaskArchive.CreateTemporaryFileAsync(root, [], canceled.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Equal(true, cancellationObserved);
            Equal(before, Directory.EnumerateFiles(Path.GetDirectoryName(archivePath)!, "task-*.zip").Count());
        }
        finally
        {
            if (archivePath is not null && File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AnsibleArchiveStreamsAbsoluteFilesWithoutPersistentCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-upload-" + Guid.NewGuid().ToString("N"));
        var external = Path.Combine(Path.GetTempPath(), "ansible-external-" + Guid.NewGuid().ToString("N") + ".bin");
        string? archivePath = null;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "site.yml"), "- hosts: all\n");
            File.WriteAllText(external, "payload");
            archivePath = AnsibleTaskArchive.CreateTemporaryFileAsync(root,
                [new(external, "payload.bin")], CancellationToken.None).GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(".kitty-uploads/payload.bin");
            Equal(true, entry is not null);
            using var reader = new StreamReader(entry!.Open());
            Equal("payload", reader.ReadToEnd());
            Equal(false, File.Exists(Path.Combine(root, "payload.bin")));
            Equal(false, Directory.Exists(Path.Combine(root, ".kitty-uploads")));
        }
        finally
        {
            if (archivePath is not null) File.Delete(archivePath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(external)) File.Delete(external);
        }
    }

    private static void AnsibleDependencyPreflight()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansible-deps-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "site.yml"), "- hosts: all\n  roles:\n    - role: local_role\n  tasks:\n    - community.general.ufw:\n");
            var missing = AnsibleDependencyScanner.Scan(root).Missing;
            Equal(true, missing.Contains("role:local_role"));
            Equal(true, missing.Contains("collection:community.general"));
            Directory.CreateDirectory(Path.Combine(root, "roles", "local_role"));
            Directory.CreateDirectory(Path.Combine(root, "collections", "ansible_collections", "community", "general"));
            Equal(0, AnsibleDependencyScanner.Scan(root).Missing.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void ConnectivityPairExclusions()
    {
        var first = Server("first"); var second = Server("second"); var third = Server("third");
        var config = Config(first, second, third);
        config.Links.Add(new() { FromServerId = first.Id, ToServerId = second.Id });
        var missing = ConnectivityPairSelectionPolicy.Build(config, [first, second, third], true);
        Equal(5, missing.Count);
        Equal(false, missing.Any(x => x.SourceId == first.Id && x.TargetId == second.Id));
        var excluded = ConnectivityPairSelectionPolicy.ExcludeServers(missing, [third.Id]);
        Equal(1, excluded.Count);
        Equal(second.Id, excluded[0].SourceId);
        Equal(first.Id, excluded[0].TargetId);

        var fromFirst = ConnectivityPairSelectionPolicy.BuildFromSource(
            config, first, [first, second, third, third], true);
        Equal(1, fromFirst.Count);
        Equal(first.Id, fromFirst[0].SourceId);
        Equal(third.Id, fromFirst[0].TargetId);

        var selected = ConnectivityPairSelectionPolicy.Selected([
            new(first.Id, second.Id, "first", "second", false),
            new(second.Id, first.Id, "second", "first"),
            new(second.Id, first.Id, "second", "first")]);
        Equal(1, selected.Count);
        Equal(second.Id, selected[0].SourceId);
        Equal(first.Id, selected[0].TargetId);

        var calls = new List<(Guid Source, Guid Target)>();
        var directed = ConnectivityBatchExecutor.CheckDirectedAsync(selected,
            (source, targets, _) =>
            {
                calls.AddRange(targets.Select(target => (source, target)));
                return Task.FromResult<IReadOnlyList<ConnectivityResult>>(targets.Select(target =>
                    new ConnectivityResult(target, true, "ok", TimeSpan.Zero, SourceId: source)).ToArray());
            }).GetAwaiter().GetResult();
        Equal(1, calls.Count);
        Equal((second.Id, first.Id), calls[0]);
        Equal(false, calls.Contains((first.Id, second.Id)));
        Equal(1, directed.Count);
    }

    private static void DirectedConnectivityReusesEachSource()
    {
        var first = Server("first"); var second = Server("second");
        var third = Server("third"); var fourth = Server("fourth");
        var calls = new List<(Guid Source, Guid[] Targets)>();
        var observed = new List<(Guid Source, Guid Target)>();
        var pairs = new[]
        {
            new ConnectivityPairChoice(first.Id, second.Id, first.Name, second.Name),
            new ConnectivityPairChoice(third.Id, first.Id, third.Name, first.Name),
            new ConnectivityPairChoice(first.Id, fourth.Id, first.Name, fourth.Name),
            new ConnectivityPairChoice(third.Id, second.Id, third.Name, second.Name)
        };

        var results = ConnectivityBatchExecutor.CheckDirectedAsync(pairs,
            (source, targets, _) =>
            {
                calls.Add((source, targets.ToArray()));
                return Task.FromResult<IReadOnlyList<ConnectivityResult>>(targets.Select(target =>
                    new ConnectivityResult(target, true, "ok", TimeSpan.Zero, SourceId: source)).ToArray());
            }, resultObserved: result => observed.Add((result.SourceId, result.TargetId)))
            .GetAwaiter().GetResult();

        Equal(2, calls.Count);
        Equal(1, calls.Count(call => call.Source == first.Id));
        Equal(1, calls.Count(call => call.Source == third.Id));
        Equal(true, calls.Single(call => call.Source == first.Id).Targets.ToHashSet()
            .SetEquals([second.Id, fourth.Id]));
        Equal(true, calls.Single(call => call.Source == third.Id).Targets.ToHashSet()
            .SetEquals([first.Id, second.Id]));
        Equal(4, results.Count);
        Equal(4, observed.Count);
    }

    private static void DirectedConnectivityHasNoReverseFallback()
    {
        var source = Server("source"); var target = Server("target");
        var calls = new List<(Guid Source, Guid[] Targets)>();
        var results = ConnectivityBatchExecutor.CheckDirectedAsync(
            [new(source.Id, target.Id, source.Name, target.Name)],
            (sourceId, targets, _) =>
            {
                calls.Add((sourceId, targets.ToArray()));
                return Task.FromResult<IReadOnlyList<ConnectivityResult>>(
                    [new(targets[0], false, "fail", TimeSpan.Zero, SourceId: sourceId)]);
            }).GetAwaiter().GetResult();

        Equal(1, calls.Count);
        Equal(source.Id, calls[0].Source);
        Equal(target.Id, calls[0].Targets.Single());
        Equal(false, calls.Any(call => call.Source == target.Id && call.Targets.Contains(source.Id)));
        Equal(false, results.Single().Success);
    }

    private static void DirectedConnectivityCancellationKeepsObservedResults()
    {
        var first = Server("first"); var second = Server("second"); var third = Server("third");
        var observed = new List<(Guid Source, Guid Target)>();
        var cancelled = false;
        try
        {
            _ = ConnectivityBatchExecutor.CheckDirectedAsync(
                [
                    new(first.Id, second.Id, first.Name, second.Name),
                    new(third.Id, first.Id, third.Name, first.Name)
                ],
                (source, targets, _) => source == third.Id
                    ? Task.FromCanceled<IReadOnlyList<ConnectivityResult>>(new CancellationToken(true))
                    : Task.FromResult<IReadOnlyList<ConnectivityResult>>(
                        [new(targets[0], true, "ok", TimeSpan.Zero, SourceId: source)]),
                resultObserved: result => observed.Add((result.SourceId, result.TargetId)))
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelled = true; }

        Equal(true, cancelled);
        Equal(1, observed.Count);
        Equal((first.Id, second.Id), observed[0]);
    }
}

using System.IO.Compression;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void BatchFileComparisonWithFragmentedReads()
    {
        var content = Enumerable.Range(0, 170003).Select(index => (byte)(index % 251)).ToArray();
        bool Same(byte[] first, byte[] second)
        {
            using var left = new ChunkedMemoryStream(first, 997);
            using var right = new ChunkedMemoryStream(second, 4093);
            return BatchTaskRunner.StreamsEqualAsync(left, right, CancellationToken.None).GetAwaiter().GetResult();
        }
        Equal(true, Same(content, content));
        Equal(true, Same([], []));
        Equal(false, Same(content, content[..^1]));
        Equal(false, Same([], [1]));
        foreach (var index in new[] { 0, 81920, content.Length - 1 })
        {
            var changed = content.ToArray(); changed[index] ^= 0xff;
            Equal(false, Same(content, changed));
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var stream = new ChunkedMemoryStream(content, 3);
        try
        {
            BatchTaskRunner.StreamsEqualAsync(stream, stream, cancelled.Token).GetAwaiter().GetResult();
            throw new Exception("Отмена сравнения проигнорирована.");
        }
        catch (OperationCanceledException) { }
    }

    private static void BatchPackageRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-archive-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "keep");
            foreach (var entryName in new[] { "../outside.txt", "../destination-other/outside.txt" })
            {
                var package = Path.Combine(root, "bad.kmtask");
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open())) writer.Write("overwrite");
                ExpectInvalidData(() => BatchTaskFile.ImportPackage(package, Path.Combine(root, "destination")));
                Equal("keep", File.ReadAllText(outside));
                Equal(false, File.Exists(Path.Combine(root, "destination-other", "outside.txt")));
                File.Delete(package);
            }
            var valid = Path.Combine(root, "valid.kmtask");
            using (var zip = ZipFile.Open(valid, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("task.yaml").Open())) writer.Write("{\"Name\":\"valid\"}");
            Equal("valid", BatchTaskFile.Load(BatchTaskFile.ImportPackage(valid, Path.Combine(root, "destination"))).Name);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Schema9MigrationPreservesUserData()
    {
        var server = new ManagedServer { Host = "example.test", Password = "keep-password",
            BackupEndpoints = [new("192.0.2.1", 2200, true)] };
        var config = Config(server); config.SchemaVersion = 8;
        Equal(true, ManagerConfigMigration.UpgradeToVersion9(config));
        Equal(9, config.SchemaVersion);
        Equal(server.Id, config.AllServers().Single().Id);
        Equal("keep-password", config.AllServers().Single().Password);
        Equal(2200, server.BackupEndpoints.Single().Port);
        Equal(false, server.BackupEndpoints.Single().InternalOnly);
        server.BackupEndpoints.Single().InternalOnly = true;
        Equal(false, ManagerConfigMigration.UpgradeToVersion9(config));
        Equal(true, server.BackupEndpoints.Single().InternalOnly);
    }
    private static void FirefoxRejectsIncompleteSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-profile-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExpectInvalidData(() => FirefoxProfileWorkspace.ValidateSourceProfile(root));
            File.WriteAllText(Path.Combine(root, "key4.db"), "keys");
            ExpectInvalidData(() => FirefoxProfileWorkspace.ValidateSourceProfile(root));
            File.Delete(Path.Combine(root, "key4.db"));
            File.WriteAllText(Path.Combine(root, "cert9.db"), "certs");
            ExpectInvalidData(() => FirefoxProfileWorkspace.ValidateSourceProfile(root));
            File.WriteAllText(Path.Combine(root, "key4.db"), "keys");
            FirefoxProfileWorkspace.ValidateSourceProfile(root);
            Equal("keys", File.ReadAllText(Path.Combine(root, "key4.db")));
            Equal("certs", File.ReadAllText(Path.Combine(root, "cert9.db")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AnsibleArchiveRejectsUnsafeNamesAndCleansFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "kitty-upload-audit-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(Path.GetTempPath(), "KiTTYManager", "AnsibleUploads");
        Directory.CreateDirectory(root); Directory.CreateDirectory(archiveRoot);
        try
        {
            var source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "keep");
            foreach (var files in new AnsibleLocalFile[][]
            {
                [new(source, "../escape.txt")],
                [new(source, "nested/escape.txt")],
                [new(source, "same.txt"), new(source, "SAME.txt")],
                [new(Path.Combine(root, "missing.txt"), "missing.txt")]
            })
            {
                var before = Directory.GetFiles(archiveRoot, "task-*.zip").ToHashSet();
                ExpectInvalidData(() => AnsibleTaskArchive.CreateTemporaryFileAsync(root, files,
                    CancellationToken.None).GetAwaiter().GetResult());
                Equal(0, Directory.GetFiles(archiveRoot, "task-*.zip").Except(before).Count());
                Equal("keep", File.ReadAllText(source));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchRunnerRejectsSelectionBeforeConnecting()
    {
        var first = Server("first"); var second = Server("second");
        var config = Config(first, second);
        var connections = 0;
        var runner = new BatchTaskRunner(new SshConnectionService(), (_, _) =>
        {
            connections++;
            return Task.FromException<ActiveRoute>(new InvalidOperationException("Unexpected connection"));
        });
        runner.RecoveryTimeout = (_, _) => Task.FromResult<int?>(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var task = new BatchTaskDefinition { Name = "selection", Tunnels = [new()
        {
            Name = "local", Kind = BatchTunnelKind.Local, BindPort = 9000,
            DestinationHost = "localhost", DestinationPort = 80
        }] };
        ExpectInvalidData(() => runner.RunAsync(config, task, [first.Id, second.Id], "",
            BatchFailureMode.Continue, timeout.Token).GetAwaiter().GetResult());
        Equal(0, connections);
        task.Tunnels.Clear();
        task.Steps.Add(new() { Name = "check", Kind = BatchTaskStepKind.Check, Command = "true", ServerIds = [second.Id] });
        ExpectInvalidData(() => runner.RunAsync(config, task, [first.Id], "",
            BatchFailureMode.Continue, timeout.Token).GetAwaiter().GetResult());
        Equal(0, connections);
    }

}

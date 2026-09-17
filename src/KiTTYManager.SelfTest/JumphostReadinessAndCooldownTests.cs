using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void JumphostConnectionCredentialsValidation()
    {
        Equal(false, AccessGrantPolicy.HasConnectionCredentials(null));

        var noHost = new ManagedServer { Name = "S", Host = "  ", Username = "user", Password = "pwd" };
        Equal(false, AccessGrantPolicy.HasConnectionCredentials(noHost));

        var noUser = new ManagedServer { Name = "S", Host = "10.0.0.1", Username = "", Password = "pwd" };
        Equal(false, AccessGrantPolicy.HasConnectionCredentials(noUser));

        var noCreds = new ManagedServer { Name = "S", Host = "10.0.0.1", Username = "user", Password = "", PrivateKeyPath = "" };
        Equal(false, AccessGrantPolicy.HasConnectionCredentials(noCreds));

        var withPassword = new ManagedServer { Name = "S", Host = "10.0.0.1", Username = "user", Password = "secret-password" };
        Equal(true, AccessGrantPolicy.HasConnectionCredentials(withPassword));

        var withWhitespacePassword = new ManagedServer { Name = "S", Host = "10.0.0.1", Username = "user", Password = "  secret  " };
        Equal(true, AccessGrantPolicy.HasConnectionCredentials(withWhitespacePassword));

        var withKey = new ManagedServer { Name = "S", Host = "10.0.0.1", Username = "user", PrivateKeyPath = "keys/id_rsa.ppk" };
        Equal(true, AccessGrantPolicy.HasConnectionCredentials(withKey));

        var userAtHost = new ManagedServer { Name = "S", Host = "admin@10.0.0.1", Username = "", Password = "pwd" };
        Equal("admin", userAtHost.EffectiveUsername);
        Equal("10.0.0.1", userAtHost.CleanHost);
        Equal(true, AccessGrantPolicy.HasConnectionCredentials(userAtHost));

        // Undecodable saved password requires existing source session file
        var tempFile = Path.Combine(Path.GetTempPath(), "test-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllLines(tempFile, ["HostName\\10.0.0.1\\", "UserName\\user\\", "Password\\enc\\"]);
            var undecodableWithFile = new ManagedServer
            {
                Name = "S", Host = "10.0.0.1", Username = "user",
                SourceSessionPath = tempFile,
                PasswordImportState = ImportedCredentialState.PresentButUndecodable
            };
            Equal(true, AccessGrantPolicy.HasConnectionCredentials(undecodableWithFile));

            var undecodableMissingFile = new ManagedServer
            {
                Name = "S", Host = "10.0.0.1", Username = "user",
                SourceSessionPath = tempFile + "-nonexistent",
                PasswordImportState = ImportedCredentialState.PresentButUndecodable
            };
            Equal(false, AccessGrantPolicy.HasConnectionCredentials(undecodableMissingFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private static void KittySessionImporterPreservesPresentButUndecodablePassword()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kitty-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sessionFile = Path.Combine(tempDir, "TestSession");
            // Encrypted password that will not decode properly with standard salt mode
            File.WriteAllLines(sessionFile, ["HostName\\192.168.1.1\\", "UserName\\operator\\", "Password\\invalidencryptedpayload12345\\"]);

            var imported = KittySessionImporter.ImportDirectory(tempDir);
            Equal(1, imported.Count);
            Equal(ImportedCredentialState.PresentButUndecodable, imported[0].PasswordImportState);
            Equal("", imported[0].Password);

            // Test ImportedSessionMerger.Apply propagates PasswordImportState
            var existing = new ManagedServer { Name = "TestSession", Host = "192.168.1.1", Username = "operator" };
            ImportedSessionMerger.Apply(existing, imported[0]);
            Equal(ImportedCredentialState.PresentButUndecodable, existing.PasswordImportState);
            Equal(Path.GetFullPath(sessionFile), existing.SourceSessionPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static void ServerEditorDirtyTrackingTests()
    {
        var serverId = Guid.NewGuid();
        var prevId = Guid.NewGuid();
        var server = new ManagedServer
        {
            Id = serverId,
            Name = "Server1",
            Host = "10.0.0.1",
            Port = 22,
            Username = "user",
            Password = "pwd",
            PrivateKeyPath = "key.ppk",
            PrivateKeyPassphrase = "pass",
            RootLogin = "root",
            RootPassword = "rootpwd",
            ShellPrompt = "$",
            ImportedCommand = "echo 1",
            IgnoreImportedCommand = false,
            TryDirectWithoutJumphost = false,
            KeepaliveIntervalSeconds = 15,
            EnableTcpKeepalives = true,
            ReconnectOnConnectionFailure = true,
            ReconnectOnSystemWakeup = true,
            RequiredPreviousServerId = prevId
        };

        ServerEditorDraft BaseDraft() => new()
        {
            ServerName = "Server1",
            Host = "10.0.0.1",
            PortText = "22",
            Username = "user",
            Password = "pwd",
            PrivateKeyPath = "key.ppk",
            PrivateKeyPassphrase = "pass",
            RootLogin = "root",
            RootPassword = "rootpwd",
            ShellPrompt = "$",
            ImportedCommand = "echo 1",
            IgnoreImportedCommand = false,
            TryDirectWithoutJumphost = false,
            KeepaliveIntervalText = "15",
            EnableTcpKeepalives = true,
            ReconnectOnConnectionFailure = true,
            ReconnectOnSystemWakeup = true,
            RequiredPreviousServerId = prevId
        };

        // Clean
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, BaseDraft()));
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(null, BaseDraft()));
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, null));

        // Each individual field modified
        var d = BaseDraft(); d.ServerName = "Changed"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.Host = "10.0.0.2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.PortText = "2222"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.PortText = "invalid"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.Username = "user2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.Password = "pwd2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.PrivateKeyPath = "key2.ppk"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.PrivateKeyPassphrase = "pass2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.RootLogin = "toor"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.RootPassword = "root2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.ShellPrompt = "#"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.ImportedCommand = "echo 2"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.IgnoreImportedCommand = true; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.TryDirectWithoutJumphost = true; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.KeepaliveIntervalText = "30"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.KeepaliveIntervalText = "bad"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.EnableTcpKeepalives = false; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.ReconnectOnConnectionFailure = false; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.ReconnectOnSystemWakeup = false; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.RequiredPreviousServerId = Guid.NewGuid(); Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = BaseDraft(); d.RequiredPreviousServerId = null; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));

        // Revert back
        d = BaseDraft();
        d.Host = "10.0.0.99";
        Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d.Host = "10.0.0.1";
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));

        // Async simulation: editing begins during delay
        EditingBeginsDuringAsyncWaitSimulation(server);
    }

    private static void EditingBeginsDuringAsyncWaitSimulation(ManagedServer server)
    {
        var draft = new ServerEditorDraft
        {
            ServerName = server.Name,
            Host = server.Host,
            PortText = server.Port.ToString(),
            Username = server.Username,
            Password = server.Password,
            PrivateKeyPath = server.PrivateKeyPath,
            PrivateKeyPassphrase = server.PrivateKeyPassphrase,
            RootLogin = server.RootLogin,
            RootPassword = server.RootPassword,
            ShellPrompt = server.ShellPrompt,
            ImportedCommand = server.ImportedCommand,
            IgnoreImportedCommand = server.IgnoreImportedCommand,
            TryDirectWithoutJumphost = server.TryDirectWithoutJumphost,
            KeepaliveIntervalText = server.KeepaliveIntervalSeconds.ToString(),
            EnableTcpKeepalives = server.EnableTcpKeepalives,
            ReconnectOnConnectionFailure = server.ReconnectOnConnectionFailure,
            ReconnectOnSystemWakeup = server.ReconnectOnSystemWakeup,
            RequiredPreviousServerId = server.RequiredPreviousServerId
        };

        // Before async wait: draft is clean
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // Simulate user typing in the editor during an async wait (e.g. TOTP window or process wait)
        var task = Task.Run(async () =>
        {
            await Task.Delay(10);
            draft.Password = "new-unsaved-password";
        });
        task.GetAwaiter().GetResult();

        // Immediately before launch: draft is dirty, auto-launch must be aborted
        Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // When user reverts/cancels changes: clean again
        draft.Password = server.Password;
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));
    }

    private static void JumphostStartupCooldownAndSchedulingTests()
    {
        var now = DateTimeOffset.UtcNow;
        var proxy = new BaseProxy
        {
            Name = "JH",
            Enabled = true,
            EnableScheduledRestart = true,
            PostLoginCommand = "sudo access.sh",
            ScheduledRestartMinutes = 60
        };

        // Fresh proxy is not in cooldown
        Equal(false, AccessGrantPolicy.IsStartupInCooldown(proxy, now));
        Equal(true, AccessGrantPolicy.ShouldRunStartupPreflight(proxy, now));
        Equal(true, AccessGrantPolicy.ShouldInitializeAccessBaseline(proxy, now));

        // Proxy with recent failure is in cooldown
        proxy.LastStartupFailureUtc = now - TimeSpan.FromMinutes(3);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));
        Equal(false, AccessGrantPolicy.ShouldRunStartupPreflight(proxy, now));
        Equal(false, AccessGrantPolicy.ShouldInitializeAccessBaseline(proxy, now));
        Equal(false, AccessGrantPolicy.ShouldRunScheduledRestart(proxy, now));

        // Exactly at or after 10 minutes, cooldown expires
        var afterCooldown = now - TimeSpan.FromMinutes(3) + TimeSpan.FromMinutes(10);
        Equal(false, AccessGrantPolicy.IsStartupInCooldown(proxy, afterCooldown));
        Equal(true, AccessGrantPolicy.ShouldRunStartupPreflight(proxy, afterCooldown));
        Equal(true, AccessGrantPolicy.ShouldInitializeAccessBaseline(proxy, afterCooldown));

        // Cooldown deadline affects NextScheduledRunUtc
        var nextRun = AccessGrantPolicy.NextScheduledRunUtc(proxy);
        Equal(true, nextRun is not null);
        Equal(proxy.LastStartupFailureUtc.Value + AccessGrantPolicy.StartupFailureCooldown, nextRun!.Value);

        // Independent proxies
        var otherProxy = new BaseProxy { Name = "JH2", Enabled = true, EnableScheduledRestart = true, PostLoginCommand = "cmd" };
        Equal(false, AccessGrantPolicy.IsStartupInCooldown(otherProxy, now));

        // Export sanitation clears LastStartupFailureUtc
        var config = new ManagerConfig { BaseProxies = [proxy, otherProxy] };
        var exported = ConfigTransfer.CreateExport(config, [], includeEntryPoints: true);
        Equal(null, exported.BaseProxies[0].LastStartupFailureUtc);

        // Orchestration failure modes and active editing tests
        JumphostStartupOrchestrationAndFailureTests();
    }

    private static void JumphostStartupOrchestrationAndFailureTests()
    {
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Server1",
            Host = "10.0.0.1",
            Port = 22,
            Username = "operator",
            Password = "pwd"
        };
        var proxy = new BaseProxy
        {
            Name = "JH",
            Enabled = true,
            StartupServerId = server.Id,
            Host = "127.0.0.1",
            Port = 1080
        };

        // 1. Port conflict / selection error sets cooldown and prevents launch
        var portConflictCalled = false;
        var portConflictResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: false,
            isServerBeingEdited: _ => false,
            preLaunchDelayAsync: null,
            selectPortAndVerifyAsync: () => throw new InvalidOperationException("Port 1080 already in use"),
            launchProcessAndWaitReadyAsync: _ => { portConflictCalled = true; return Task.FromResult(true); },
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.FailedWithCooldown, portConflictResult.Status);
        Equal(false, portConflictCalled);
        Equal(true, proxy.LastStartupFailureUtc is not null);
        Equal(now, proxy.LastStartupFailureUtc!.Value);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));
        Equal(false, AccessGrantPolicy.ShouldRunStartupPreflight(proxy, now));

        // 2. Cooldown blocks next automated attempt
        var blockedByCooldownResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: false,
            isServerBeingEdited: _ => false,
            preLaunchDelayAsync: null,
            selectPortAndVerifyAsync: () => Task.FromResult(1080),
            launchProcessAndWaitReadyAsync: _ => Task.FromResult(true),
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.SkippedNotEligible, blockedByCooldownResult.Status);
        Equal(JumphostStartupEligibility.InFailureCooldown, blockedByCooldownResult.Eligibility);

        // 3. Manual launch clears cooldown and succeeds
        var manualProcessLaunched = false;
        var manualResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: true,
            isServerBeingEdited: _ => false,
            preLaunchDelayAsync: null,
            selectPortAndVerifyAsync: () => Task.FromResult(1080),
            launchProcessAndWaitReadyAsync: _ => { manualProcessLaunched = true; return Task.FromResult(true); },
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.Success, manualResult.Status);
        Equal(true, manualProcessLaunched);
        Equal(null, proxy.LastStartupFailureUtc);
        Equal(false, AccessGrantPolicy.IsStartupInCooldown(proxy, now));

        // 4. Adopted console SOCKS5 timeout records failure cooldown
        JumphostStartupPolicy.HandleAdoptedConsoleTimeout(proxy, now);
        Equal(now, proxy.LastStartupFailureUtc);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));

        // Reset
        proxy.LastStartupFailureUtc = null;

        // 5. Unhealthy process stop failure records failure cooldown
        JumphostStartupPolicy.HandleStopUnhealthyProcessFailure(proxy, now);
        Equal(now, proxy.LastStartupFailureUtc);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));

        // Reset
        proxy.LastStartupFailureUtc = null;

        // 6. Process launch error records failure cooldown
        var processLaunchErrorResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: false,
            isServerBeingEdited: _ => false,
            preLaunchDelayAsync: null,
            selectPortAndVerifyAsync: () => Task.FromResult(1080),
            launchProcessAndWaitReadyAsync: _ => throw new InvalidOperationException("Failed to spawn process"),
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.FailedWithCooldown, processLaunchErrorResult.Status);
        Equal(now, proxy.LastStartupFailureUtc);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));

        // Reset
        proxy.LastStartupFailureUtc = null;

        // 7. SOCKS5 timeout on launched process records failure cooldown
        var socksTimeoutResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: false,
            isServerBeingEdited: _ => false,
            preLaunchDelayAsync: null,
            selectPortAndVerifyAsync: () => Task.FromResult(1080),
            launchProcessAndWaitReadyAsync: _ => Task.FromResult(false),
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.FailedWithCooldown, socksTimeoutResult.Status);
        Equal(now, proxy.LastStartupFailureUtc);
        Equal(true, AccessGrantPolicy.IsStartupInCooldown(proxy, now));

        // Reset
        proxy.LastStartupFailureUtc = null;

        // 8. Active editing begins DURING async delay before launch:
        // Draft is clean at start, user begins typing in editor during delay,
        // launch MUST be aborted immediately without recording a failure cooldown!
        var draft = new ServerEditorDraft
        {
            ServerName = server.Name,
            Host = server.Host,
            PortText = server.Port.ToString(),
            Username = server.Username,
            Password = server.Password
        };
        var serverEdited = false;
        var processShouldNotLaunch = false;

        var abortedResult = JumphostStartupPolicy.ExecuteLaunchSequenceAsync(
            proxy,
            server,
            isManualLaunch: false,
            isServerBeingEdited: _ => serverEdited,
            preLaunchDelayAsync: async () =>
            {
                await Task.Delay(10);
                draft.Password = "new-unsaved-password";
                serverEdited = ServerEditorDirtyTracker.HasUnsavedChanges(server, draft);
            },
            selectPortAndVerifyAsync: () => Task.FromResult(1080),
            launchProcessAndWaitReadyAsync: _ =>
            {
                processShouldNotLaunch = true;
                return Task.FromResult(true);
            },
            getNow: () => now).GetAwaiter().GetResult();

        Equal(JumphostLaunchStatus.AbortedDueToEditing, abortedResult.Status);
        Equal(JumphostStartupEligibility.ServerBeingEdited, abortedResult.Eligibility);
        Equal(false, processShouldNotLaunch);
        Equal(null, proxy.LastStartupFailureUtc);
        Equal(false, AccessGrantPolicy.IsStartupInCooldown(proxy, now));
    }
}

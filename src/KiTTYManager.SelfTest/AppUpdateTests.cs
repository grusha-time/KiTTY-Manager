using System.IO.Compression;
using System.Text;
using KiTTYManager.App;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Ожидалось истинное значение.");
    }

    private static void False(bool condition)
    {
        if (condition) throw new InvalidOperationException("Ожидалось ложное значение.");
    }

    private static void NotNull(object? value)
    {
        if (value is null) throw new InvalidOperationException("Ожидалось не-null значение.");
    }

    private static void Null(object? value)
    {
        if (value is not null) throw new InvalidOperationException("Ожидалось null значение.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ожидалось исключение {typeof(T).Name}, но получено {ex.GetType().Name}: {ex.Message}");
        }
        throw new InvalidOperationException($"Ожидалось исключение {typeof(T).Name}, но исключение не возникло.");
    }

    private static void AppUpdateVersionParsingAndComparison()
    {
        True(AppUpdatePolicy.TryParseVersion("2.0.0", out var v1));
        Equal(new Version(2, 0, 0), v1);

        True(AppUpdatePolicy.TryParseVersion("v2.0.1", out var v2));
        Equal(new Version(2, 0, 1), v2);

        True(AppUpdatePolicy.TryParseVersion("V3.1.2.4", out var v3));
        Equal(new Version(3, 1, 2, 4), v3);

        True(AppUpdatePolicy.TryParseVersion("v2.0.0-rc1", out var v4));
        Equal(new Version(2, 0, 0), v4);

        False(AppUpdatePolicy.TryParseVersion("", out _));
        False(AppUpdatePolicy.TryParseVersion("invalid-version", out _));

        var asset = new AppReleaseAsset("KiTTYManager-2.0.1-windows-x64.zip", "https://example.test/download.zip", 12345, "application/zip");
        var release = new AppReleaseInfo("v2.0.1", "KiTTY Manager 2.0.1", "Bugfixes", DateTimeOffset.UtcNow, "https://example.test", asset, v2!);

        True(release.IsNewerThan("2.0.0"));
        False(release.IsNewerThan("2.0.1"));
        False(release.IsNewerThan("2.0.2"));

        True(release.IsSameVersionAs("2.0.1"));
        True(release.IsSameVersionAs("v2.0.1"));
        False(release.IsSameVersionAs("2.0.0"));

        True(release.IsOlderThan("2.1.0"));
        False(release.IsOlderThan("2.0.0"));
    }

    private static void AppUpdateScheduleCalculations()
    {
        // On network error: retry in 1 hour
        var now = new DateTime(2026, 9, 17, 8, 30, 0);
        var nextAfterFailure = AppUpdatePolicy.CalculateNextScheduledTime(now, lastCheckFailed: true);
        Equal(new DateTime(2026, 9, 17, 9, 30, 0), nextAfterFailure);

        // Before 10:00 local: schedule today at 10:00
        var nextBefore10 = AppUpdatePolicy.CalculateNextScheduledTime(now, lastCheckFailed: false);
        Equal(new DateTime(2026, 9, 17, 10, 0, 0), nextBefore10);

        // At or after 10:00 local: schedule tomorrow at 10:00
        var nowAfter10 = new DateTime(2026, 9, 17, 10, 15, 0);
        var nextAfter10 = AppUpdatePolicy.CalculateNextScheduledTime(nowAfter10, lastCheckFailed: false);
        Equal(new DateTime(2026, 9, 18, 10, 0, 0), nextAfter10);

        // Due check
        True(AppUpdatePolicy.IsCheckDue(nowAfter10, new DateTime(2026, 9, 17, 10, 0, 0)));
        False(AppUpdatePolicy.IsCheckDue(now, new DateTime(2026, 9, 17, 10, 0, 0)));

        // Large backward clock shift (>2 days)
        var shiftedNow = new DateTime(2026, 9, 10, 10, 0, 0);
        True(AppUpdatePolicy.IsCheckDue(shiftedNow, new DateTime(2026, 9, 17, 10, 0, 0)));
    }

    private static void AppUpdateNotificationDeduplication()
    {
        var config = new ManagerConfig();
        False(config.HasNotifiedUpdateVersion("v2.0.1"));
        False(config.HasNotifiedUpdateVersion("2.0.1"));

        config.RecordNotifiedUpdateVersion("v2.0.1");
        True(config.HasNotifiedUpdateVersion("v2.0.1"));
        True(config.HasNotifiedUpdateVersion("2.0.1"));
        Equal(1, config.NotifiedUpdateVersions.Count);

        // Recording again should not duplicate
        config.RecordNotifiedUpdateVersion("2.0.1");
        Equal(1, config.NotifiedUpdateVersions.Count);

        // Another version is not notified yet
        False(config.HasNotifiedUpdateVersion("2.0.2"));
        config.RecordNotifiedUpdateVersion("2.0.2");
        Equal(2, config.NotifiedUpdateVersions.Count);
        True(config.HasNotifiedUpdateVersion("v2.0.2"));
    }

    private static void AppUpdateAssetSelectionAndJsonParsing()
    {
        var json = """
        {
            "tag_name": "v2.0.1",
            "name": "KiTTY Manager 2.0.1",
            "body": "## Changes\n- Improved update support",
            "published_at": "2026-09-17T10:00:00Z",
            "html_url": "https://github.com/grusha-time/KiTTY-Manager/releases/tag/v2.0.1",
            "prerelease": false,
            "assets": [
                {
                    "name": "KiTTYManager-2.0.1-linux-x64.tar.gz",
                    "browser_download_url": "https://example.test/linux.tar.gz",
                    "size": 50000000,
                    "content_type": "application/gzip"
                },
                {
                    "name": "KiTTYManager-2.0.1-windows-x64.zip",
                    "browser_download_url": "https://example.test/windows.zip",
                    "size": 150000000,
                    "content_type": "application/zip"
                }
            ]
        }
        """;

        var release = AppUpdateService.ParseRelease(json);
        Equal("v2.0.1", release.TagName);
        Equal("KiTTYManager-2.0.1-windows-x64.zip", release.Asset.Name);
        Equal("https://example.test/windows.zip", release.Asset.DownloadUrl);
        Equal(150000000L, release.Asset.Size);
        Equal(new Version(2, 0, 1), release.NormalizedVersion);
        True(release.IsNewerThan("2.0.0"));

        // Reject if missing windows x64 zip
        var noWinAssetJson = """
        {
            "tag_name": "v2.0.2",
            "assets": [
                { "name": "source.zip", "browser_download_url": "https://example.test/src.zip" }
            ]
        }
        """;
        Throws<InvalidOperationException>(() => AppUpdateService.ParseRelease(noWinAssetJson));

        // Reject if ambiguous windows x64 zips
        var ambiguousJson = """
        {
            "tag_name": "v2.0.3",
            "assets": [
                { "name": "KiTTYManager-2.0.3-windows-x64.zip", "browser_download_url": "https://example.test/w1.zip" },
                { "name": "KiTTYManager-debug-windows-x64.zip", "browser_download_url": "https://example.test/w2.zip" }
            ]
        }
        """;
        Throws<InvalidOperationException>(() => AppUpdateService.ParseRelease(ambiguousJson));
    }

    private static void AppUpdateArchiveValidation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KM_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Valid archive
            var validZip = Path.Combine(tempDir, "valid.zip");
            CreateTestZip(validZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "MZ-BINARY-CONTENT",
                ["KiTTY/kitty.exe"] = "KITTY-BINARY",
                ["KiTTY/kitty.ini"] = "[KiTTY]\nportable=yes",
                ["Runtime/Ansible/agent.py"] = "print('ansible')"
            });

            var validRes = AppUpdatePackage.ValidateArchive(validZip);
            True(validRes.IsValid);
            NotNull(validRes.ManifestEntries);
            Equal(4, validRes.ManifestEntries!.Count);

            // 2. Corrupt archive
            var corruptZip = Path.Combine(tempDir, "corrupt.zip");
            File.WriteAllText(corruptZip, "not-a-zip-file-at-all");
            var corruptRes = AppUpdatePackage.ValidateArchive(corruptZip);
            False(corruptRes.IsValid);

            // 3. Missing KiTTYManager.exe
            var missingExeZip = Path.Combine(tempDir, "missing_exe.zip");
            CreateTestZip(missingExeZip, new Dictionary<string, string>
            {
                ["OtherFile.exe"] = "BINARY"
            });
            var missingRes = AppUpdatePackage.ValidateArchive(missingExeZip);
            False(missingRes.IsValid);
            True(missingRes.ErrorMessage!.Contains("KiTTYManager.exe"));

            // 4. Zero-length KiTTYManager.exe
            var zeroExeZip = Path.Combine(tempDir, "zero_exe.zip");
            CreateTestZip(zeroExeZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = ""
            });
            var zeroRes = AppUpdatePackage.ValidateArchive(zeroExeZip);
            False(zeroRes.IsValid);
            True(zeroRes.ErrorMessage!.Contains("нулевой размер"));

            // 5. Traversal attack with ..
            var traversalZip = Path.Combine(tempDir, "traversal.zip");
            CreateTestZip(traversalZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["../secret.txt"] = "hacked"
            });
            var traversalRes = AppUpdatePackage.ValidateArchive(traversalZip);
            False(traversalRes.IsValid);
            True(traversalRes.ErrorMessage!.Contains("path traversal") || traversalRes.ErrorMessage.Contains("запрещен"));

            // 6. Violation: targets Data/
            var dataZip = Path.Combine(tempDir, "data_exploit.zip");
            CreateTestZip(dataZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["Data/config.json"] = "{}"
            });
            var dataRes = AppUpdatePackage.ValidateArchive(dataZip);
            False(dataRes.IsValid);
            True(dataRes.ErrorMessage!.Contains("Data/"));

            // 7. Violation: targets KiTTY/SshHostKeys/
            var hostKeyZip = Path.Combine(tempDir, "hostkey_exploit.zip");
            CreateTestZip(hostKeyZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["KiTTY/SshHostKeys/key1"] = "compromised"
            });
            var hostKeyRes = AppUpdatePackage.ValidateArchive(hostKeyZip);
            False(hostKeyRes.IsValid);
            True(hostKeyRes.ErrorMessage!.Contains("KiTTY/SshHostKeys/"));

            // 8. Violation: targets KiTTY/Sessions/
            var sessionZip = Path.Combine(tempDir, "session_exploit.zip");
            CreateTestZip(sessionZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["KiTTY/Sessions/Session1"] = "compromised"
            });
            var sessionRes = AppUpdatePackage.ValidateArchive(sessionZip);
            False(sessionRes.IsValid);
            True(sessionRes.ErrorMessage!.Contains("KiTTY/Sessions/"));

            // 9. Reserved device names (CON, AUX, NUL)
            var conZip = Path.Combine(tempDir, "con_device.zip");
            CreateTestZip(conZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["CON.txt"] = "bad"
            });
            var conRes = AppUpdatePackage.ValidateArchive(conZip);
            False(conRes.IsValid);

            // 10. Alternate data streams
            var adsZip = Path.Combine(tempDir, "ads.zip");
            CreateTestZip(adsZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["file.txt:stream"] = "bad"
            });
            var adsRes = AppUpdatePackage.ValidateArchive(adsZip);
            False(adsRes.IsValid);

            // 11. Trailing dot bypass attempt (Data./config.json)
            var dotZip = Path.Combine(tempDir, "dot_exploit.zip");
            CreateTestZip(dotZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["Data./config.json"] = "{}"
            });
            var dotRes = AppUpdatePackage.ValidateArchive(dotZip);
            False(dotRes.IsValid);

            // 12. Trailing space bypass attempt (Data /config.json)
            var spaceZip = Path.Combine(tempDir, "space_exploit.zip");
            CreateTestZip(spaceZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["Data /config.json"] = "{}"
            });
            var spaceRes = AppUpdatePackage.ValidateArchive(spaceZip);
            False(spaceRes.IsValid);

            // 13. Sessions trailing dot bypass (KiTTY/Sessions./xyz)
            var sessDotZip = Path.Combine(tempDir, "sessdot_exploit.zip");
            CreateTestZip(sessDotZip, new Dictionary<string, string>
            {
                ["KiTTYManager.exe"] = "BINARY",
                ["KiTTY/Sessions./session"] = "bad"
            });
            var sessDotRes = AppUpdatePackage.ValidateArchive(sessDotZip);
            False(sessDotRes.IsValid);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private static void AppUpdatePowerShellScriptAndSessionTests()
    {
        var script = AppUpdateInstaller.GeneratePowerShellScript();
        True(script.Contains("$ErrorActionPreference = 'Stop'"));
        True(script.Contains("-ErrorAction Stop"));
        True(script.Contains("Copy-Item -LiteralPath $targetFile"));
        True(script.Contains("Copy-Item -LiteralPath $stagedFile"));
        True(script.Contains("Test-Path -LiteralPath $targetFile"));
        True(script.Contains("Get-Content -LiteralPath $manifestFile"));
        True(script.Contains("Add-Content -LiteralPath $LogFile"));
        True(script.Contains("[System.IO.Directory]::CreateDirectory"));
        True(script.Contains("Data(\\.| )?"));
        True(script.Contains("KiTTY/SshHostKeys(\\.| )?"));
        True(script.Contains("KiTTY/Sessions(\\.| )?"));
        True(script.Contains("Get-Process -Id $ParentPid"));
        True(script.Contains("Start-Process -FilePath $targetExe"));

        var tempDir = Path.Combine(Path.GetTempPath(), "KM_SessionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sig = Path.Combine(tempDir, "commit.signal");
            var dis = Path.Combine(tempDir, "disarm.signal");
            var log = Path.Combine(tempDir, "update.log");

            var session = new UpdateExecutionSession(tempDir, sig, dis, log);
            True(session.IsHelperAlive());
            False(session.Disarmed);

            // Commit writes signal file
            True(session.Commit());
            True(File.Exists(sig));

            // Disarm
            var session2 = new UpdateExecutionSession(tempDir, sig + "2", dis + "2", log);
            session2.Disarm();
            True(session2.Disarmed);
            True(File.Exists(dis + "2"));
            // Disarmed session cannot commit
            False(session2.Commit());
            False(File.Exists(sig + "2"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private static void AppUpdateSafeRollbackAndPreservation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KM_Rollback_" + Guid.NewGuid().ToString("N"));
        // Test directory path with brackets (e.g. KiTTY [portable]) to verify literal path handling
        var targetDir = Path.Combine(tempDir, "target [portable]");
        var stagingDir = Path.Combine(tempDir, "staging");
        var backupDir = Path.Combine(tempDir, "backup");

        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(stagingDir);

        try
        {
            // Set up existing target installation
            var targetExe = Path.Combine(targetDir, "KiTTYManager.exe");
            var targetKittyIni = Path.Combine(targetDir, "KiTTY", "kitty.ini");
            var targetDataConfig = Path.Combine(targetDir, "Data", "config.json");
            var targetHostKey = Path.Combine(targetDir, "KiTTY", "SshHostKeys", "server_key");

            Directory.CreateDirectory(Path.GetDirectoryName(targetKittyIni)!);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDataConfig)!);
            Directory.CreateDirectory(Path.GetDirectoryName(targetHostKey)!);

            File.WriteAllText(targetExe, "OLD_EXE_V200");
            File.WriteAllText(targetKittyIni, "CUSTOM_USER_KITTY_INI");
            File.WriteAllText(targetDataConfig, "SECRET_USER_CONFIG");
            File.WriteAllText(targetHostKey, "USER_SSH_HOST_KEY");

            // Set up staged update
            var stagedExe = Path.Combine(stagingDir, "KiTTYManager.exe");
            var stagedKittyIni = Path.Combine(stagingDir, "KiTTY", "kitty.ini");
            var stagedAnsible = Path.Combine(stagingDir, "Runtime", "Ansible", "agent.py");

            Directory.CreateDirectory(Path.GetDirectoryName(stagedKittyIni)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedAnsible)!);

            File.WriteAllText(stagedExe, "NEW_EXE_V201");
            File.WriteAllText(stagedKittyIni, "DEFAULT_PACKAGE_KITTY_INI");
            File.WriteAllText(stagedAnsible, "ANSIBLE_SCRIPT");

            var manifest = AppUpdatePackage.BuildManifest(stagingDir);
            Equal(3, manifest.Count);

            // Apply update successfully
            var success = AppUpdatePackage.ApplyUpdateWithRollback(stagingDir, targetDir, backupDir, manifest, out var failure);
            True(success);
            Null(failure);

            // Verification:
            // 1. KiTTYManager.exe was updated
            Equal("NEW_EXE_V201", File.ReadAllText(targetExe));
            // 2. KiTTY/kitty.ini was PRESERVED because it already existed!
            Equal("CUSTOM_USER_KITTY_INI", File.ReadAllText(targetKittyIni));
            // 3. New file was installed
            Equal("ANSIBLE_SCRIPT", File.ReadAllText(Path.Combine(targetDir, "Runtime", "Ansible", "agent.py")));
            // 4. Data/config.json was untouched
            Equal("SECRET_USER_CONFIG", File.ReadAllText(targetDataConfig));
            // 5. KiTTY/SshHostKeys/server_key was untouched
            Equal("USER_SSH_HOST_KEY", File.ReadAllText(targetHostKey));

            // Now test Rollback upon failure:
            // Delete one staged file to cause a mid-copy exception
            File.Delete(stagedExe);
            File.WriteAllText(targetExe, "OLD_EXE_BEFORE_FAILED_RUN");

            var failResult = AppUpdatePackage.ApplyUpdateWithRollback(stagingDir, targetDir, backupDir + "_2", manifest, out var failReason);
            False(failResult);
            NotNull(failReason);
            // Target exe should be rolled back to its state
            Equal("OLD_EXE_BEFORE_FAILED_RUN", File.ReadAllText(targetExe));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private static void AppUpdateDeferredShutdownPolicyTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "KM_DeferredShutdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sig = Path.Combine(tempDir, "commit.signal");
            var dis = Path.Combine(tempDir, "disarm.signal");
            var log = Path.Combine(tempDir, "update.log");

            // Case 1: Deferred shutdown outcome = CANCELLED / DECLINED (e.g. user chose "No" on unsaved changes)
            UpdateExecutionSession? session = new UpdateExecutionSession(tempDir, sig, dis, log);
            bool batchCloseAccepted = false;
            if (!batchCloseAccepted)
            {
                session.Disarm();
                session = null;
            }

            Null(session);
            True(File.Exists(dis));
            False(File.Exists(sig));

            // Case 2: Deferred shutdown outcome = ACCEPTED (e.g. task completed and user closed window)
            var sig2 = Path.Combine(tempDir, "commit2.signal");
            var dis2 = Path.Combine(tempDir, "disarm2.signal");
            UpdateExecutionSession? session2 = new UpdateExecutionSession(tempDir, sig2, dis2, log);
            bool batchCloseAccepted2 = true;
            if (!batchCloseAccepted2)
            {
                session2.Disarm();
                session2 = null;
            }
            else
            {
                True(session2.Commit());
            }

            NotNull(session2);
            False(session2!.Disarmed);
            True(File.Exists(sig2));
            False(File.Exists(dis2));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    private static void CreateTestZip(string zipPath, IDictionary<string, string> files)
    {
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (path, content) in files)
        {
            var entry = archive.CreateEntry(path);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using var entryStream = entry.Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
}

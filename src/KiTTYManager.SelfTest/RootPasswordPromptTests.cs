using System.Text;
using System.Text.Json;
using KiTTYManager.App;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private static void RootPasswordPromptLoginScriptAndPamTests()
    {
        // 1. Direct connection login script generation
        var serverDefault = new ManagedServer
        {
            Name = "ServerDefault",
            Host = "192.0.2.1",
            RootLogin = "su -",
            RootPassword = "root-password",
            ShellPrompt = "$"
        };
        using (var script = KittyLoginScript.Create(serverDefault))
        {
            Equal(true, script is not null);
            var lines = File.ReadAllLines(script!.Path);
            Equal(4, lines.Length);
            Equal("$", lines[0]);
            Equal("su -", lines[1]);
            Equal("assword", lines[2]);
            Equal("root-password", lines[3]);
        }

        var serverCustom = new ManagedServer
        {
            Name = "ServerCustom",
            Host = "192.0.2.2",
            RootLogin = "sudo -i",
            RootPassword = "pin-secret",
            RootPasswordPrompt = "Smart card PIN:",
            ShellPrompt = "user@host:"
        };
        using (var script = KittyLoginScript.Create(serverCustom))
        {
            Equal(true, script is not null);
            var lines = File.ReadAllLines(script!.Path);
            Equal(4, lines.Length);
            Equal("user@host:", lines[0]);
            Equal("sudo -i", lines[1]);
            Equal("Smart card PIN:", lines[2]);
            Equal("pin-secret", lines[3]);
        }

        var serverSanitized = new ManagedServer
        {
            Name = "ServerSanitized",
            Host = "192.0.2.3",
            RootLogin = "su -",
            RootPassword = "root-password",
            RootPasswordPrompt = "Smart card PIN:\r\n\0",
            ShellPrompt = "$"
        };
        using (var script = KittyLoginScript.Create(serverSanitized))
        {
            Equal(true, script is not null);
            var lines = File.ReadAllLines(script!.Path);
            Equal("Smart card PIN:", lines[2]);
        }

        // Empty or whitespace prompt falls back to default "assword"
        var serverEmpty = new ManagedServer
        {
            Name = "ServerEmpty",
            Host = "192.0.2.4",
            RootLogin = "su -",
            RootPassword = "root-password",
            RootPasswordPrompt = "   ",
            ShellPrompt = "$"
        };
        using (var script = KittyLoginScript.Create(serverEmpty))
        {
            Equal(true, script is not null);
            var lines = File.ReadAllLines(script!.Path);
            Equal("assword", lines[2]);
        }

        // 2. PAM login plan step generation
        var jumpHost = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "JH",
            Host = "192.0.2.100",
            ShellPrompt = "jh$ "
        };
        var pamTargetDefault = new ManagedServer
        {
            Name = "PamTargetDefault",
            Host = "192.0.2.200",
            UsePam = true,
            PamSshCommand = "ssh target@host",
            PamPassword = "pam-pass",
            RootLogin = "su -",
            RootPassword = "pam-root-pass",
            ShellPrompt = "target$ "
        };
        var stepsDefault = PamLoginPlan.BuildPromptResponses(pamTargetDefault, jumpHost, DateTimeOffset.UtcNow);
        var pamPassStep = stepsDefault.First(s => s.Response == "pam-pass");
        Equal("assword", pamPassStep.Prompt);
        var rootPassStepDefault = stepsDefault.First(s => s.Response == "pam-root-pass");
        Equal("assword", rootPassStepDefault.Prompt);

        var pamTargetCustom = new ManagedServer
        {
            Name = "PamTargetCustom",
            Host = "192.0.2.201",
            UsePam = true,
            PamSshCommand = "ssh target@host",
            PamPassword = "pam-pass",
            RootLogin = "su -",
            RootPassword = "pam-root-pin",
            RootPasswordPrompt = "Smart card PIN:",
            ShellPrompt = "target$ "
        };
        var stepsCustom = PamLoginPlan.BuildPromptResponses(pamTargetCustom, jumpHost, DateTimeOffset.UtcNow);
        // PAM password prompt remains unchanged
        var pamPassStepCustom = stepsCustom.First(s => s.Response == "pam-pass");
        Equal("assword", pamPassStepCustom.Prompt);
        // Root elevation step uses custom prompt
        var rootPassStepCustom = stepsCustom.First(s => s.Response == "pam-root-pin");
        Equal("Smart card PIN:", rootPassStepCustom.Prompt);

        // 3. KittyCredentialDecoder recognizes the configured PIN prompt in the root context
        var scriptWithPin = "$\nsu -\nEnter PIN:\n123456\n";
        var rewriteSuccess = KittyCredentialDecoder.TryRewriteRootPassword(
            scriptWithPin, "su -", "123456", "654321", out var rewrittenScript, "Enter PIN:");
        Equal(true, rewriteSuccess);
        Equal(true, rewrittenScript.Contains("654321", StringComparison.Ordinal));
        // Without the configured prompt the arbitrary text is not guessed globally
        Equal(false, KittyCredentialDecoder.TryRewriteRootPassword(
            scriptWithPin, "su -", "123456", "654321", out _));
    }

    private static void RootPasswordPromptImportAndMergeRulesTests()
    {
        // 1. IsDefaultRootPasswordPrompt tests
        Equal(true, ImportWizardEngine.IsDefaultRootPasswordPrompt(null));
        Equal(true, ImportWizardEngine.IsDefaultRootPasswordPrompt(""));
        Equal(true, ImportWizardEngine.IsDefaultRootPasswordPrompt("   "));
        Equal(true, ImportWizardEngine.IsDefaultRootPasswordPrompt("assword"));
        Equal(true, ImportWizardEngine.IsDefaultRootPasswordPrompt(" assword "));
        Equal(false, ImportWizardEngine.IsDefaultRootPasswordPrompt("Smart card PIN:"));
        Equal(false, ImportWizardEngine.IsDefaultRootPasswordPrompt("Password:"));

        // 2. ResolveImportedRootPasswordPrompt rule combinations
        // (default, custom) -> custom
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("assword", "Smart card PIN:"));
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("", "Smart card PIN:"));
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt(null, "Smart card PIN:"));

        // (custom, other custom) -> other custom
        Equal("PIN-2:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("PIN-1:", "PIN-2:"));

        // (custom, default) -> custom
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("Smart card PIN:", "assword"));
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("Smart card PIN:", ""));
        Equal("Smart card PIN:", ImportWizardEngine.ResolveImportedRootPasswordPrompt("Smart card PIN:", null));

        // (default, default) -> default
        Equal("assword", ImportWizardEngine.ResolveImportedRootPasswordPrompt("assword", "assword"));
        Equal("assword", ImportWizardEngine.ResolveImportedRootPasswordPrompt("", ""));
        Equal("assword", ImportWizardEngine.ResolveImportedRootPasswordPrompt(null, null));

        // 3. Smart Import Wizard Analysis and Decisions
        var serverId = Guid.NewGuid();
        var currentConfig = new ManagerConfig
        {
            UngroupedServers =
            [
                new ManagedServer
                {
                    Id = serverId,
                    Name = "Server-Local",
                    Host = "10.0.0.1",
                    Port = 22,
                    RootLogin = "su -",
                    RootPassword = "local-pass",
                    RootPasswordPrompt = "Smart card PIN:"
                }
            ]
        };

        var incomingDefault = new ManagerConfig
        {
            UngroupedServers =
            [
                new ManagedServer
                {
                    Id = serverId,
                    Name = "Server-Local",
                    Host = "10.0.0.1",
                    Port = 22,
                    RootLogin = "su -",
                    RootPassword = "incoming-pass",
                    RootPasswordPrompt = "assword"
                }
            ]
        };

        // Analysis: local is custom, incoming is default -> keepCurrent = true, UseIncoming = false
        var plan = ImportWizardEngine.Analyze(currentConfig, incomingDefault);
        var promptField = plan.Fields.FirstOrDefault(f => f.PropertyName == "RootPasswordPrompt");
        Equal(true, promptField is not null);
        Equal(false, promptField!.UseIncoming);

        // ApplyFullImportCascade: should NOT overwrite local custom with incoming default
        ImportWizardEngine.ApplyFullImportCascade(plan, [serverId]);
        promptField = plan.Fields.First(f => f.PropertyName == "RootPasswordPrompt");
        Equal(false, promptField.UseIncoming);

        // Merge with UseIncoming decision (full session replacement):
        // Target custom prompt must be preserved because incoming prompt is default
        plan.Sessions.First().Decision = ImportDecision.UseIncoming;
        var mergedCascade = ImportWizardEngine.Merge(currentConfig, incomingDefault, plan);
        var mergedServer = mergedCascade.AllServers().Single(s => s.Id == serverId);
        Equal("Smart card PIN:", mergedServer.RootPasswordPrompt);

        // Merge with Field-by-field: if user explicitly checks the field, Resolve applies
        var fieldTarget = new ManagedServer
        {
            Id = serverId,
            RootPasswordPrompt = "Smart card PIN:"
        };
        ImportWizardEngine.ApplySelectedFields(fieldTarget, new ManagedServer { RootPasswordPrompt = "assword" },
            [new ImportFieldDecision { PropertyName = "RootPasswordPrompt", UseIncoming = true }]);
        // Even if applied, ResolveImportedRootPasswordPrompt keeps local custom over default
        Equal("Smart card PIN:", fieldTarget.RootPasswordPrompt);

        // Field-by-field: incoming custom overwrites local default
        var fieldTarget2 = new ManagedServer
        {
            Id = serverId,
            RootPasswordPrompt = "assword"
        };
        ImportWizardEngine.ApplySelectedFields(fieldTarget2, new ManagedServer { RootPasswordPrompt = "Custom PIN:" },
            [new ImportFieldDecision { PropertyName = "RootPasswordPrompt", UseIncoming = true }]);
        Equal("Custom PIN:", fieldTarget2.RootPasswordPrompt);

        // 4. Legacy ConfigTransfer.Merge
        var legacyCurrent = new ManagerConfig
        {
            UngroupedServers =
            [
                new ManagedServer
                {
                    Id = serverId,
                    Name = "Server-Legacy",
                    Host = "10.0.0.1",
                    RootPasswordPrompt = "Smart card PIN:"
                }
            ]
        };
        var legacyIncoming = new ManagerConfig
        {
            UngroupedServers =
            [
                new ManagedServer
                {
                    Id = serverId,
                    Name = "Server-Legacy",
                    Host = "10.0.0.1",
                    RootPasswordPrompt = "assword"
                }
            ]
        };
        var conflicts = new Dictionary<(TransferConflictKind Kind, Guid IncomingId), bool>
        {
            { (TransferConflictKind.Server, serverId), true }
        };
        var legacyMerged = ConfigTransfer.Merge(legacyCurrent, legacyIncoming, conflicts);
        var legacyResult = legacyMerged.AllServers().Single(s => s.Id == serverId);
        Equal("Smart card PIN:", legacyResult.RootPasswordPrompt);

        // 5. Legacy JSON missing RootPasswordPrompt property
        var legacyJson = "{\n  \"UngroupedServers\": [\n    {\n      \"Id\": \"" + serverId.ToString() + "\",\n      \"Name\": \"Legacy-Server\",\n      \"Host\": \"10.0.0.5\",\n      \"Port\": 22\n    }\n  ]\n}";
        var tempFile = Path.Combine(Path.GetTempPath(), "legacy-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, legacyJson);
            var loadedLegacy = ConfigStore.Import(tempFile);
            var loadedServer = loadedLegacy.AllServers().Single();
            Equal("assword", loadedServer.RootPasswordPrompt);

            // Import legacy into custom local
            var legacyPlan = ImportWizardEngine.Analyze(currentConfig, loadedLegacy);
            var legacyPromptField = legacyPlan.Fields.FirstOrDefault(f => f.PropertyName == "RootPasswordPrompt");
            if (legacyPromptField is not null)
            {
                Equal(false, legacyPromptField.UseIncoming);
            }
            legacyPlan.Sessions.First().Decision = ImportDecision.UseIncoming;
            var mergedWithLegacy = ImportWizardEngine.Merge(currentConfig, loadedLegacy, legacyPlan);
            Equal("Smart card PIN:", mergedWithLegacy.AllServers().Single(s => s.Id == serverId).RootPasswordPrompt);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void RootPasswordPromptCloningDuplicationAndDirtyTrackerTests()
    {
        // 1. ManagedServer.Clone()
        var original = new ManagedServer
        {
            Name = "Server1",
            RootPasswordPrompt = "Custom Prompt:"
        };
        var clone = original.Clone();
        Equal("Custom Prompt:", clone.RootPasswordPrompt);

        // 2. ManagedServerDuplicator.Create()
        var config = new ManagerConfig { UngroupedServers = [original] };
        var duplicate = ManagedServerDuplicator.Duplicate(config, original);
        Equal("Custom Prompt:", duplicate.RootPasswordPrompt);

        // 3. ManagedServerDuplicator.CreateQuickDuplicate()
        var quickDuplicate = ManagedServerDuplicator.CreateQuickDuplicate(config, original);
        Equal("Custom Prompt:", quickDuplicate.RootPasswordPrompt);

        // 4. ServerEditorDirtyTracker
        var server = new ManagedServer
        {
            Name = "TestServer",
            Host = "10.0.0.1",
            Port = 22,
            RootPasswordPrompt = "assword"
        };
        var draft = new ServerEditorDraft
        {
            ServerName = "TestServer",
            Host = "10.0.0.1",
            PortText = "22",
            RootPasswordPrompt = "assword"
        };

        // Same default -> not dirty
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // Draft whitespace / empty is effective default -> not dirty
        draft.RootPasswordPrompt = "";
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // Draft custom -> dirty
        draft.RootPasswordPrompt = "Smart card PIN:";
        Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // Server updated to custom, draft matching -> not dirty
        server.RootPasswordPrompt = "Smart card PIN:";
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));

        // Server custom, draft default -> dirty
        draft.RootPasswordPrompt = "assword";
        Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, draft));
    }

    private static void RootPasswordPromptConfigRoundtripAndSessionWriterTests()
    {
        // 1. ConfigStore.Normalize
        var serverWithNewlines = new ManagedServer
        {
            RootPasswordPrompt = "Smart card PIN:\r\n\0"
        };
        ConfigStore.Normalize(serverWithNewlines);
        Equal("Smart card PIN:", serverWithNewlines.RootPasswordPrompt);

        var serverEmpty = new ManagedServer
        {
            RootPasswordPrompt = "  "
        };
        ConfigStore.Normalize(serverEmpty);
        Equal("assword", serverEmpty.RootPasswordPrompt);

        // 2. Config roundtrip save and load
        var tempConfigPath = Path.Combine(Path.GetTempPath(), "roundtrip-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cfg = new ManagerConfig
            {
                UngroupedServers =
                [
                    new ManagedServer
                    {
                        Id = Guid.NewGuid(),
                        Name = "SrvRoundtrip",
                        Host = "192.0.2.50",
                        RootLogin = "su -",
                        RootPassword = "secret-pw",
                        RootPasswordPrompt = "Smart card PIN:"
                    }
                ]
            };
            ConfigStore.SaveSnapshot(tempConfigPath, cfg);
            var loaded = ConfigStore.Load(tempConfigPath);
            var loadedSrv = loaded.AllServers().Single();
            Equal("Smart card PIN:", loadedSrv.RootPasswordPrompt);

            // CloneForSave retains prompt
            var clonedForSave = ConfigStore.CloneForSave(loaded);
            Equal("Smart card PIN:", clonedForSave.AllServers().Single().RootPasswordPrompt);

            // Export retains prompt
            var exported = ConfigTransfer.CreateExport(loaded, [loadedSrv.Id], includeEntryPoints: false);
            Equal("Smart card PIN:", exported.AllServers().Single().RootPasswordPrompt);
        }
        finally
        {
            try { File.Delete(tempConfigPath); } catch { }
        }

        // 3. KittySessionWriter writes custom prompt
        var rootDir = Path.Combine(Path.GetTempPath(), "session-writer-test-" + Guid.NewGuid().ToString("N"));
        var sessionsDir = Path.Combine(rootDir, "KiTTY", "Sessions");
        Directory.CreateDirectory(sessionsDir);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp1251 = Encoding.GetEncoding(1251);
            var decryptMethod = typeof(KittyCredentialDecoder).GetMethod("TryDecrypt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var encryptMethod = typeof(KittyCredentialDecoder).GetMethod("EncryptBytes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            byte[] RawScript(string encrypted)
            {
                object?[] arguments = [encrypted, Encoding.ASCII.GetBytes("9bis"), null];
                Equal(true, (bool)decryptMethod.Invoke(null, arguments)!);
                return (byte[])arguments[2]!;
            }
            string EncryptRaw(byte[] bytes) =>
                (string)encryptMethod.Invoke(null, [bytes, Encoding.ASCII.GetBytes("9bis"), "AZERT"])!;
            byte[][] ScriptSegments(byte[] raw)
            {
                var segments = new List<byte[]>();
                var start = 0;
                for (var index = 0; index < raw.Length; index++)
                {
                    if (raw[index] != 0) continue;
                    segments.Add(raw[start..index]);
                    start = index + 1;
                }
                segments.Add(raw[start..]);
                return segments.ToArray();
            }

            var srv = new ManagedServer
            {
                Name = "SessionWriterServer",
                Host = "192.0.2.60",
                Port = 22,
                Password = "ssh-pw",
                RootLogin = "su -",
                RootPassword = "root-pw",
                RootPasswordPrompt = "Smart card PIN:"
            };
            KittySessionWriter.Write(sessionsDir, srv, KittySessionWriter.WritableProperties);
            var writtenFile = Path.Combine(sessionsDir, "SessionWriterServer");
            Equal(true, File.Exists(writtenFile));
            var parsed = KittySessionImporter.ParseFile(writtenFile);
            var encryptedScript = parsed.GetValueOrDefault("ScriptfileContent") ?? "";
            Equal(true, encryptedScript.Length > 0);
            var decoded = KittyCredentialDecoder.DecodeLoginScript(encryptedScript, 0, "Smart card PIN:");
            Equal(true, decoded is not null);
            Equal(true, decoded!.Content.Contains("Smart card PIN:", StringComparison.Ordinal));
            Equal("root-pw", decoded.RootPassword);

            // Arbitrary prompt text without password/PIN keywords must also survive
            // writing and verification, because the user configures the expected text.
            var arbitraryServer = new ManagedServer
            {
                Name = "SessionWriterArbitrary",
                Host = "192.0.2.61",
                Port = 22,
                Password = "ssh-pw",
                RootLogin = "sudo -i",
                RootPassword = "root-pw-2",
                RootPasswordPrompt = "Введите код:"
            };
            var arbitraryFailure = "";
            try
            {
                KittySessionWriter.Write(sessionsDir, arbitraryServer, KittySessionWriter.WritableProperties);
            }
            catch (IOException ex)
            {
                arbitraryFailure = ex.Message;
            }
            Equal("", arbitraryFailure);
            var arbitraryParsed = KittySessionImporter.ParseFile(
                Path.Combine(sessionsDir, "SessionWriterArbitrary"));
            var arbitraryDecoded = KittyCredentialDecoder.DecodeLoginScript(
                arbitraryParsed.GetValueOrDefault("ScriptfileContent") ?? "", 0, "Введите код:");
            Equal("root-pw-2", arbitraryDecoded?.RootPassword);
            Equal(true, arbitraryDecoded!.Content.Contains("Введите код:", StringComparison.Ordinal));

            // Non-ASCII prompts must round-trip exactly, including sequences whose
            // UTF-8 and ANSI/1251 bytes overlap (e.g. "Р°").
            foreach (var prompt in new[] { "Введите код:", "Р°" })
            {
                var content = "$\nsu -\n" + prompt + "\nroot-secret";
                var roundTrip = KittyCredentialDecoder.DecodeLoginScript(
                    KittyCredentialDecoder.EncryptScriptContent(content), 0, prompt);
                Equal(content, roundTrip?.Content);
                Equal("root-secret", roundTrip?.RootPassword);
            }

            // Prompt lines are stored as UTF-8 for KiTTY's strstr() against UTF-8
            // server output; answer lines use strict ANSI/1251 for SendKeyboard.
            foreach (var wirePrompt in new[] { "Введите код:", "PIN 😀:" })
            {
                var wireName = "SessionWriterWire" + wirePrompt.Length;
                var wireServer = new ManagedServer
                {
                    Name = wireName,
                    Host = "192.0.2.63",
                    Port = 22,
                    Password = "ssh-pw",
                    RootLogin = "su -",
                    RootPassword = "wire-root-secret",
                    RootPasswordPrompt = wirePrompt
                };
                var wireDir = Path.Combine(rootDir, "KiTTYWire", "Sessions");
                Directory.CreateDirectory(wireDir);
                KittySessionWriter.Write(wireDir, wireServer, KittySessionWriter.WritableProperties);
                var wireValues = KittySessionImporter.ParseFile(Path.Combine(wireDir, wireName));
                Equal("1", wireValues.GetValueOrDefault(KittyCredentialDecoder.ScriptEncodingMarkerKey));
                var wireEncrypted = wireValues.GetValueOrDefault("ScriptfileContent") ?? "";
                var wireSegments = ScriptSegments(RawScript(wireEncrypted));
                Equal(8, wireSegments.Length);
                Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(wirePrompt)), Convert.ToHexString(wireSegments[2]));
                Equal(Convert.ToHexString(cp1251.GetBytes("wire-root-secret")), Convert.ToHexString(wireSegments[3]));
                Equal(0, wireSegments[4].Length);
                Equal(Convert.ToHexString(Encoding.ASCII.GetBytes(KittyCredentialDecoder.ScriptFormatMarker)),
                    Convert.ToHexString(wireSegments[5]));
                Equal(0, wireSegments[6].Length);
                Equal(0, wireSegments[7].Length);
                var wireDecoded = KittyCredentialDecoder.DecodeLoginScript(wireEncrypted, 0, wirePrompt, roleEncoded: true);
                Equal("$\nsu -\n" + wirePrompt + "\nwire-root-secret", wireDecoded!.Content);
                Equal(false, wireDecoded.Content.Contains(KittyCredentialDecoder.ScriptFormatMarker, StringComparison.Ordinal));
                Equal("wire-root-secret", wireDecoded.RootPassword);
            }

            // Rewriting must replace only the response bytes: an existing CP1251
            // prompt must stay byte-identical, the new answer stays CP1251.
            var legacyPromptBytes = cp1251.GetBytes("Пароль:");
            var legacyRaw = new List<byte>();
            legacyRaw.AddRange(cp1251.GetBytes("$\0su -\0"));
            legacyRaw.AddRange(legacyPromptBytes);
            legacyRaw.Add(0);
            legacyRaw.AddRange(cp1251.GetBytes("старый"));
            var legacyScript = EncryptRaw(legacyRaw.ToArray());
            Equal(true, KittyCredentialDecoder.TryRewriteRootPassword(
                legacyScript, "su -", "старый", "новый", out var legacyRewritten, "Пароль:"));
            var legacySegments = ScriptSegments(RawScript(legacyRewritten));
            Equal(Convert.ToHexString(legacyPromptBytes), Convert.ToHexString(legacySegments[2]));
            Equal(Convert.ToHexString(cp1251.GetBytes("новый")), Convert.ToHexString(legacySegments[3]));
            Equal(false, legacySegments[3].SequenceEqual(Encoding.UTF8.GetBytes("новый")));
            Equal("новый", KittyCredentialDecoder.DecodeLoginScript(legacyRewritten, 0, "Пароль:")?.RootPassword);

            // Legacy embedded scripts written as pure UTF-8 must stay readable.
            var legacyUtf8Script = EncryptRaw(Encoding.UTF8.GetBytes("$\0su -\0Пароль:\0root-secret"));
            Equal("root-secret", KittyCredentialDecoder.DecodeLoginScript(legacyUtf8Script, 0)?.RootPassword);
            var legacyUtf8Dir = Path.Combine(rootDir, "KiTTYLegacyUtf8", "Sessions");
            Directory.CreateDirectory(legacyUtf8Dir);
            File.WriteAllLines(Path.Combine(legacyUtf8Dir, "LegacyUtf8"),
            [
                "HostName\\192.0.2.65\\",
                "ScriptfileContent\\" + legacyUtf8Script + "\\"
            ]);
            Equal("root-secret", KittySessionImporter.ImportDirectory(legacyUtf8Dir).Single().RootPassword);

            // Marked scripts use role-based decoding: an answer such as CP1251
            // "Р°-secret" (bytes D0 B0 are also valid UTF-8 "а") must stay intact.
            var ambiguousServer = new ManagedServer
            {
                Name = "SessionWriterAmbiguous",
                Host = "192.0.2.66",
                Port = 22,
                Password = "ssh-pw",
                RootLogin = "su -",
                RootPassword = "Р°-secret",
                RootPasswordPrompt = "Пароль:"
            };
            var ambiguousDir = Path.Combine(rootDir, "KiTTYAmbiguous", "Sessions");
            Directory.CreateDirectory(ambiguousDir);
            KittySessionWriter.Write(ambiguousDir, ambiguousServer, KittySessionWriter.WritableProperties);
            var ambiguousValues = KittySessionImporter.ParseFile(Path.Combine(ambiguousDir, "SessionWriterAmbiguous"));
            Equal("1", ambiguousValues.GetValueOrDefault(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            var ambiguousScript = ambiguousValues.GetValueOrDefault("ScriptfileContent") ?? "";
            Equal("Р°-secret", KittyCredentialDecoder.DecodeLoginScript(
                ambiguousScript, 0, "Пароль:", roleEncoded: true)?.RootPassword);
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(ambiguousDir).Single().RootPassword);

            // Simulated native KiTTY Save: dropping the informational session key
            // must not lose the durable in-payload trailer.
            var ambiguousFile = Path.Combine(ambiguousDir, "SessionWriterAmbiguous");
            File.WriteAllLines(ambiguousFile, File.ReadAllLines(ambiguousFile)
                .Where(line => !line.StartsWith(KittyCredentialDecoder.ScriptEncodingMarkerKey + "\\", StringComparison.Ordinal)));
            Equal(false, KittySessionImporter.ParseFile(ambiguousFile)
                .ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(ambiguousDir).Single().RootPassword);

            // Legacy unmarked CP1251 scripts are decoded as a whole stream, so the
            // same ambiguous answer is not mistaken for UTF-8 "а".
            var legacyAmbiguousRaw = new List<byte>();
            legacyAmbiguousRaw.AddRange(cp1251.GetBytes("$\0su -\0Пароль:\0"));
            legacyAmbiguousRaw.AddRange(cp1251.GetBytes("Р°-secret"));
            var legacyAmbiguous = EncryptRaw(legacyAmbiguousRaw.ToArray());
            Equal("Р°-secret", KittyCredentialDecoder.DecodeLoginScript(legacyAmbiguous, 0)?.RootPassword);
            var legacyAmbiguousDir = Path.Combine(rootDir, "KiTTYLegacyAmbiguous", "Sessions");
            Directory.CreateDirectory(legacyAmbiguousDir);
            File.WriteAllLines(Path.Combine(legacyAmbiguousDir, "LegacyAmbiguous"),
            [
                "HostName\\192.0.2.67\\",
                "ScriptfileContent\\" + legacyAmbiguous + "\\"
            ]);
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(legacyAmbiguousDir).Single().RootPassword);
            Equal(true, KittyCredentialDecoder.TryRewriteRootPassword(
                legacyAmbiguous, "su -", "Р°-secret", "новый", out var legacyAmbiguousRewritten));
            var legacyAmbiguousSegments = ScriptSegments(RawScript(legacyAmbiguousRewritten));
            Equal(Convert.ToHexString(cp1251.GetBytes("новый")), Convert.ToHexString(legacyAmbiguousSegments[3]));
            Equal("новый", KittyCredentialDecoder.DecodeLoginScript(
                legacyAmbiguousRewritten, 0, "Пароль:")?.RootPassword);

            // Legacy UTF-8 multi-answer rewrite: only the root answer changes,
            // other answers keep their UTF-8 interpretation, and neither the
            // trailer nor the informational session key is added.
            var multiAnswerDir = Path.Combine(rootDir, "KiTTYMultiAnswer", "Sessions");
            Directory.CreateDirectory(multiAnswerDir);
            var multiAnswerScript = EncryptRaw(Encoding.UTF8.GetBytes(
                "login:\0operator\0password:\0ёж-secret\0$\0su -\0assword:\0root-old"));
            File.WriteAllLines(Path.Combine(multiAnswerDir, "MultiAnswer"),
            [
                "HostName\\192.0.2.68\\",
                "ScriptfileContent\\" + multiAnswerScript + "\\"
            ]);
            var multiAnswerServer = KittySessionImporter.ImportDirectory(multiAnswerDir).Single();
            Equal("operator", multiAnswerServer.Username);
            Equal("ёж-secret", multiAnswerServer.Password);
            Equal("root-old", multiAnswerServer.RootPassword);
            multiAnswerServer.KittyBaseline = ImportedSessionMerger.Snapshot(multiAnswerServer);
            multiAnswerServer.RootPassword = "root-new";
            KittySessionWriter.Write(multiAnswerDir, multiAnswerServer, [nameof(ManagedServer.RootPassword)]);
            var multiAnswerAfter = KittySessionImporter.ImportDirectory(multiAnswerDir).Single();
            Equal("operator", multiAnswerAfter.Username);
            Equal("ёж-secret", multiAnswerAfter.Password);
            Equal("root-new", multiAnswerAfter.RootPassword);
            var multiAnswerValues = KittySessionImporter.ParseFile(Path.Combine(multiAnswerDir, "MultiAnswer"));
            Equal(false, multiAnswerValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            var oldMultiRaw = RawScript(multiAnswerScript);
            var newMultiRaw = RawScript(multiAnswerValues.GetValueOrDefault("ScriptfileContent") ?? "");
            var lastMultiNull = Array.LastIndexOf(oldMultiRaw, (byte)0);
            Equal(true, oldMultiRaw.AsSpan(0, lastMultiNull + 1).SequenceEqual(newMultiRaw.AsSpan(0, lastMultiNull + 1)));
            Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("root-new")),
                Convert.ToHexString(newMultiRaw[(lastMultiNull + 1)..]));

            // Mixed UTF-8 multi-answer rewrite: an all-hex replacement must end
            // with the Enter sentinel, keep the untouched answer UTF-8, add no
            // metadata, and remain rewritable.
            var mixedEnterDir = Path.Combine(rootDir, "KiTTYMixedEnter", "Sessions");
            Directory.CreateDirectory(mixedEnterDir);
            var mixedEnterScript = EncryptRaw(Encoding.UTF8.GetBytes(
                "login:\0operator\0password:\0ёж-secret\0$\0su -\0assword:\0old-secret"));
            var mixedEnterFile = Path.Combine(mixedEnterDir, "MixedEnter");
            File.WriteAllLines(mixedEnterFile,
            [
                "HostName\\192.0.2.81\\",
                "ScriptfileContent\\" + mixedEnterScript + "\\"
            ]);
            var mixedEnterServer = KittySessionImporter.ImportDirectory(mixedEnterDir).Single();
            mixedEnterServer.KittyBaseline = ImportedSessionMerger.Snapshot(mixedEnterServer);
            mixedEnterServer.RootPassword = "новый";
            KittySessionWriter.Write(mixedEnterDir, mixedEnterServer, [nameof(ManagedServer.RootPassword)]);
            var mixedEnterValues = KittySessionImporter.ParseFile(mixedEnterFile);
            Equal(false, mixedEnterValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            Equal(false, KittyCredentialDecoder.HasScriptFormatTrailer(
                mixedEnterValues.GetValueOrDefault("ScriptfileContent")));
            var mixedEnterOldRaw = RawScript(mixedEnterScript);
            var mixedEnterNewRaw = RawScript(mixedEnterValues.GetValueOrDefault("ScriptfileContent") ?? "");
            var mixedEnterLastNull = Array.LastIndexOf(mixedEnterOldRaw, (byte)0);
            Equal(true, mixedEnterOldRaw.AsSpan(0, mixedEnterLastNull + 1)
                .SequenceEqual(mixedEnterNewRaw.AsSpan(0, mixedEnterLastNull + 1)));
            Equal("\\xD0\\xBD\\xD0\\xBE\\xD0\\xB2\\xD1\\x8B\\xD0\\xB9\\n",
                Encoding.ASCII.GetString(mixedEnterNewRaw[(mixedEnterLastNull + 1)..]));
            var mixedEnterAfter = KittySessionImporter.ImportDirectory(mixedEnterDir).Single();
            Equal("ёж-secret", mixedEnterAfter.Password);
            Equal("новый", mixedEnterAfter.RootPassword);
            mixedEnterServer.KittyBaseline = ImportedSessionMerger.Snapshot(mixedEnterServer);
            mixedEnterServer.RootPassword = "следующий";
            KittySessionWriter.Write(mixedEnterDir, mixedEnterServer, [nameof(ManagedServer.RootPassword)]);
            var mixedEnterSecond = KittySessionImporter.ImportDirectory(mixedEnterDir).Single();
            Equal("ёж-secret", mixedEnterSecond.Password);
            Equal("следующий", mixedEnterSecond.RootPassword);

            // Ambiguous legacy candidates must be refused before the session file
            // is replaced, so the writer leaves it byte-identical.
            var ambiguousLegacyDir = Path.Combine(rootDir, "KiTTYAmbiguousLegacy", "Sessions");
            Directory.CreateDirectory(ambiguousLegacyDir);
            var ambiguousLegacyScript = EncryptRaw(Encoding.UTF8.GetBytes(
                "$\0su -\0Пароль:\0same-secret\0Password:\0same-secret"));
            var ambiguousLegacyFile = Path.Combine(ambiguousLegacyDir, "AmbiguousLegacy");
            File.WriteAllLines(ambiguousLegacyFile,
            [
                "HostName\\192.0.2.69\\",
                "ScriptfileContent\\" + ambiguousLegacyScript + "\\"
            ]);
            Equal(false, KittyCredentialDecoder.TryRewriteRootPassword(
                ambiguousLegacyScript, "su -", "same-secret", "root-new", out _));
            var ambiguousLegacyServer = KittySessionImporter.ImportDirectory(ambiguousLegacyDir).Single();
            Equal("same-secret", ambiguousLegacyServer.RootPassword);
            ambiguousLegacyServer.KittyBaseline = ImportedSessionMerger.Snapshot(ambiguousLegacyServer);
            ambiguousLegacyServer.RootPassword = "root-new";
            var ambiguousLegacyBefore = File.ReadAllBytes(ambiguousLegacyFile);
            var ambiguousLegacyError = "";
            try { KittySessionWriter.Write(ambiguousLegacyDir, ambiguousLegacyServer, [nameof(ManagedServer.RootPassword)]); }
            catch (InvalidOperationException ex) { ambiguousLegacyError = ex.Message; }
            Equal(true, ambiguousLegacyError.Contains("login script", StringComparison.Ordinal));
            Equal(true, ambiguousLegacyBefore.SequenceEqual(File.ReadAllBytes(ambiguousLegacyFile)));

            // ASCII-only legacy script rewritten to a non-ASCII answer: KiTTY
            // sends responses through the ANSI path, so the replacement must be
            // strict CP1251, not UTF-8, while untouched bytes stay identical.
            var asciiLegacyDir = Path.Combine(rootDir, "KiTTYAsciiLegacy", "Sessions");
            Directory.CreateDirectory(asciiLegacyDir);
            var asciiLegacyScript = EncryptRaw(Encoding.ASCII.GetBytes("$\0su -\0assword:\0old-secret"));
            var asciiLegacyFile = Path.Combine(asciiLegacyDir, "AsciiLegacy");
            File.WriteAllLines(asciiLegacyFile,
            [
                "HostName\\192.0.2.71\\",
                "ScriptfileContent\\" + asciiLegacyScript + "\\"
            ]);
            Equal(true, KittyCredentialDecoder.TryRewriteRootPassword(
                asciiLegacyScript, "su -", "old-secret", "новый", out var asciiRewritten));
            var asciiSegments = ScriptSegments(RawScript(asciiRewritten));
            Equal(Convert.ToHexString(cp1251.GetBytes("новый")), Convert.ToHexString(asciiSegments[3]));
            Equal(false, asciiSegments[3].SequenceEqual(Encoding.UTF8.GetBytes("новый")));
            var asciiOldRaw = RawScript(asciiLegacyScript);
            var asciiNewRaw = RawScript(asciiRewritten);
            var asciiLastNull = Array.LastIndexOf(asciiOldRaw, (byte)0);
            Equal(true, asciiOldRaw.AsSpan(0, asciiLastNull + 1).SequenceEqual(asciiNewRaw.AsSpan(0, asciiLastNull + 1)));
            Equal("новый", KittyCredentialDecoder.DecodeLoginScript(asciiRewritten, 0)?.RootPassword);
            var asciiServer = KittySessionImporter.ImportDirectory(asciiLegacyDir).Single();
            asciiServer.KittyBaseline = ImportedSessionMerger.Snapshot(asciiServer);
            asciiServer.RootPassword = "новый";
            KittySessionWriter.Write(asciiLegacyDir, asciiServer, [nameof(ManagedServer.RootPassword)]);
            var asciiAfterValues = KittySessionImporter.ParseFile(asciiLegacyFile);
            Equal(false, asciiAfterValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            Equal(Convert.ToHexString(cp1251.GetBytes("новый")),
                Convert.ToHexString(ScriptSegments(RawScript(
                    asciiAfterValues.GetValueOrDefault("ScriptfileContent") ?? ""))[3]));
            Equal("новый", KittySessionImporter.ImportDirectory(asciiLegacyDir).Single().RootPassword);

            // Genuine CP1251 multi-answer script: a CP1251 prompt anchors the
            // whole stream, so an early answer whose bytes are also valid UTF-8
            // ("Р°-secret") keeps its CP1251 interpretation.
            var cp1251MultiDir = Path.Combine(rootDir, "KiTTYCP1251Multi", "Sessions");
            Directory.CreateDirectory(cp1251MultiDir);
            var cp1251MultiScript = EncryptRaw(cp1251.GetBytes(
                "login:\0operator\0password:\0Р°-secret\0$\0su -\0Пароль:\0root-old"));
            File.WriteAllLines(Path.Combine(cp1251MultiDir, "CP1251Multi"),
            [
                "HostName\\192.0.2.73\\",
                "ScriptfileContent\\" + cp1251MultiScript + "\\"
            ]);
            var cp1251MultiServer = KittySessionImporter.ImportDirectory(cp1251MultiDir).Single();
            Equal("operator", cp1251MultiServer.Username);
            Equal("Р°-secret", cp1251MultiServer.Password);
            Equal("root-old", cp1251MultiServer.RootPassword);
            cp1251MultiServer.KittyBaseline = ImportedSessionMerger.Snapshot(cp1251MultiServer);
            cp1251MultiServer.RootPassword = "новый";
            KittySessionWriter.Write(cp1251MultiDir, cp1251MultiServer, [nameof(ManagedServer.RootPassword)]);
            var cp1251MultiAfter = KittySessionImporter.ImportDirectory(cp1251MultiDir).Single();
            Equal("operator", cp1251MultiAfter.Username);
            Equal("Р°-secret", cp1251MultiAfter.Password);
            Equal("новый", cp1251MultiAfter.RootPassword);

            // prompt validity does not prove the answer encoding: an all-ASCII
            // prompt stream with any non-UTF-8 segment is genuine CP1251 and must
            // keep both ambiguous answers in their CP1251 interpretation.
            foreach (var fixture in new[]
            {
                (Text: "login:\0operator\0password:\0старый\0$\0su -\0assword:\0Р°-secret", Password: "старый", Root: "Р°-secret"),
                (Text: "login:\0operator\0password:\0Р°-secret\0$\0su -\0assword:\0старый", Password: "Р°-secret", Root: "старый")
            })
            {
                var asciiPromptScript = EncryptRaw(cp1251.GetBytes(fixture.Text));
                var asciiPromptDecoded = KittyCredentialDecoder.DecodeLoginScript(asciiPromptScript, 0);
                Equal(fixture.Password, asciiPromptDecoded?.Password);
                Equal(fixture.Root, asciiPromptDecoded?.RootPassword);
                var asciiPromptDir = Path.Combine(rootDir, "KiTTYCP1251AsciiPrompt" + Guid.NewGuid().ToString("N"), "Sessions");
                Directory.CreateDirectory(asciiPromptDir);
                File.WriteAllLines(Path.Combine(asciiPromptDir, "Legacy"),
                [
                    "HostName\\192.0.2.77\\",
                    "ScriptfileContent\\" + asciiPromptScript + "\\"
                ]);
                var asciiPromptImported = KittySessionImporter.ImportDirectory(asciiPromptDir).Single();
                Equal(fixture.Password, asciiPromptImported.Password);
                Equal(fixture.Root, asciiPromptImported.RootPassword);
            }

            // Upstream SendKeyboardPlus quotes a backslash: "\\x41" is literal
            // text "\x41", while a single "\x41" escape still means "A".
            var literalHexText = "$\0su -\0assword:\0\\\\x41-secret";
            var literalHexScript = EncryptRaw(Encoding.ASCII.GetBytes(literalHexText));
            Equal(@"\x41-secret", KittyCredentialDecoder.DecodeLoginScript(literalHexScript, 0)?.RootPassword);
            var literalHexDir = Path.Combine(rootDir, "KiTTYLiteralHex", "Sessions");
            Directory.CreateDirectory(literalHexDir);
            File.WriteAllLines(Path.Combine(literalHexDir, "LiteralHex"),
            [
                "HostName\\192.0.2.78\\",
                "ScriptfileContent\\" + literalHexScript + "\\"
            ]);
            Equal(@"\x41-secret", KittySessionImporter.ImportDirectory(literalHexDir).Single().RootPassword);
            Equal("A-secret", KittyCredentialDecoder.DecodeLoginScript(
                EncryptRaw(Encoding.ASCII.GetBytes("$\0su -\0assword:\0\\x41-secret")), 0)?.RootPassword);

            // An escaped answer without the trailing "\n" still round-trips in
            // the manager's reader (the sentinel only exists for KiTTY's Enter).
            var noSentinelAnswer = EncryptRaw(Encoding.ASCII.GetBytes(
                "$\0su -\0assword:\0\\xD0\\xA0\\xC2\\xB0-secret"));
            Equal("Р°-secret", KittyCredentialDecoder.DecodeLoginScript(noSentinelAnswer, 0)?.RootPassword);

            // A prompt whose CP1251 bytes happen to be valid UTF-8 does not
            // prove the stream encoding: an invalid answer still makes the
            // whole stream CP1251, so "Р°-secret" keeps its CP1251 meaning.
            var nonAsciiPromptScript = EncryptRaw(cp1251.GetBytes(
                "Р°>\0su -\0assword:\0Р°-secret\0done:\0старый"));
            Equal("Р°-secret", KittyCredentialDecoder.DecodeLoginScript(nonAsciiPromptScript, 0)?.RootPassword);
            var nonAsciiPromptDir = Path.Combine(rootDir, "KiTTYCP1251NonAsciiPrompt", "Sessions");
            Directory.CreateDirectory(nonAsciiPromptDir);
            File.WriteAllLines(Path.Combine(nonAsciiPromptDir, "Legacy"),
            [
                "HostName\\192.0.2.79\\",
                "ScriptfileContent\\" + nonAsciiPromptScript + "\\"
            ]);
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(nonAsciiPromptDir).Single().RootPassword);

            // A real UTF-8 prompt with an ambiguous replacement must be written
            // as escapes plus the Enter sentinel, not raw CP1251 that the reader
            // would reinterpret as UTF-8 "а-secret".
            var utfPromptDir = Path.Combine(rootDir, "KiTTYUtf8PromptRewrite", "Sessions");
            Directory.CreateDirectory(utfPromptDir);
            var utfPromptScript = EncryptRaw(Encoding.UTF8.GetBytes("$\0su -\0Пароль:\0root-old"));
            var utfPromptFile = Path.Combine(utfPromptDir, "Utf8Prompt");
            File.WriteAllLines(utfPromptFile,
            [
                "HostName\\192.0.2.80\\",
                "ScriptfileContent\\" + utfPromptScript + "\\"
            ]);
            var utfPromptServer = KittySessionImporter.ImportDirectory(utfPromptDir).Single();
            utfPromptServer.RootPasswordPrompt = "Пароль:";
            utfPromptServer.KittyBaseline = ImportedSessionMerger.Snapshot(utfPromptServer);
            utfPromptServer.RootPassword = "Р°-secret";
            KittySessionWriter.Write(utfPromptDir, utfPromptServer, [nameof(ManagedServer.RootPassword)]);
            var utfPromptValues = KittySessionImporter.ParseFile(utfPromptFile);
            Equal(false, utfPromptValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            Equal(false, KittyCredentialDecoder.HasScriptFormatTrailer(
                utfPromptValues.GetValueOrDefault("ScriptfileContent")));
            var utfPromptSegments = ScriptSegments(RawScript(
                utfPromptValues.GetValueOrDefault("ScriptfileContent") ?? ""));
            Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("Пароль:")), Convert.ToHexString(utfPromptSegments[2]));
            Equal("\\xD0\\xA0\\xC2\\xB0\\x2D\\x73\\x65\\x63\\x72\\x65\\x74\\n",
                Encoding.ASCII.GetString(utfPromptSegments[3]));
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(utfPromptDir).Single().RootPassword);

            // A manager-produced mixed legacy script must stay readable and
            // rewritable: UTF-8 prompt bytes are preserved, the answer stays
            // CP1251, and a second rewrite finds the same response segment.
            var repeatDir = Path.Combine(rootDir, "KiTTYRepeat", "Sessions");
            Directory.CreateDirectory(repeatDir);
            var repeatScript = EncryptRaw(Encoding.UTF8.GetBytes("$\0su -\0Пароль:\0root-old"));
            var repeatFile = Path.Combine(repeatDir, "Repeat");
            File.WriteAllLines(repeatFile,
            [
                "HostName\\192.0.2.74\\",
                "ScriptfileContent\\" + repeatScript + "\\"
            ]);
            var repeatServer = KittySessionImporter.ImportDirectory(repeatDir).Single();
            repeatServer.RootPasswordPrompt = "Пароль:";
            repeatServer.KittyBaseline = ImportedSessionMerger.Snapshot(repeatServer);
            repeatServer.RootPassword = "новый";
            KittySessionWriter.Write(repeatDir, repeatServer, [nameof(ManagedServer.RootPassword)]);
            var repeatValues = KittySessionImporter.ParseFile(repeatFile);
            Equal(false, repeatValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            var repeatContent = repeatValues.GetValueOrDefault("ScriptfileContent") ?? "";
            Equal(false, KittyCredentialDecoder.HasScriptFormatTrailer(repeatContent));
            var repeatSegments = ScriptSegments(RawScript(repeatContent));
            Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("Пароль:")), Convert.ToHexString(repeatSegments[2]));
            Equal("EDEEE2FBE9", Convert.ToHexString(repeatSegments[3]));
            Equal("новый", KittySessionImporter.ImportDirectory(repeatDir).Single().RootPassword);
            repeatServer.KittyBaseline = ImportedSessionMerger.Snapshot(repeatServer);
            repeatServer.RootPassword = "следующий";
            KittySessionWriter.Write(repeatDir, repeatServer, [nameof(ManagedServer.RootPassword)]);
            Equal("следующий", KittySessionImporter.ImportDirectory(repeatDir).Single().RootPassword);

            // An ANSI answer that would read back as different UTF-8 text is
            // stored as ASCII \xHH escapes of its UTF-8 bytes.
            var ambiguousAnswerDir = Path.Combine(rootDir, "KiTTYAmbiguousAnswer", "Sessions");
            Directory.CreateDirectory(ambiguousAnswerDir);
            var ambiguousAnswerScript = EncryptRaw(Encoding.ASCII.GetBytes("$\0su -\0assword:\0root-old"));
            var ambiguousAnswerFile = Path.Combine(ambiguousAnswerDir, "AmbiguousAnswer");
            File.WriteAllLines(ambiguousAnswerFile,
            [
                "HostName\\192.0.2.75\\",
                "ScriptfileContent\\" + ambiguousAnswerScript + "\\"
            ]);
            var ambiguousAnswerServer = KittySessionImporter.ImportDirectory(ambiguousAnswerDir).Single();
            ambiguousAnswerServer.KittyBaseline = ImportedSessionMerger.Snapshot(ambiguousAnswerServer);
            ambiguousAnswerServer.RootPassword = "Р°-secret";
            var ambiguousAnswerError = "";
            try { KittySessionWriter.Write(ambiguousAnswerDir, ambiguousAnswerServer, [nameof(ManagedServer.RootPassword)]); }
            catch (Exception ex) { ambiguousAnswerError = ex.Message; }
            Equal("", ambiguousAnswerError);
            var ambiguousAnswerValues = KittySessionImporter.ParseFile(ambiguousAnswerFile);
            Equal(false, ambiguousAnswerValues.ContainsKey(KittyCredentialDecoder.ScriptEncodingMarkerKey));
            Equal(false, KittyCredentialDecoder.HasScriptFormatTrailer(
                ambiguousAnswerValues.GetValueOrDefault("ScriptfileContent")));
            var ambiguousAnswerSegment = ScriptSegments(RawScript(
                ambiguousAnswerValues.GetValueOrDefault("ScriptfileContent") ?? ""))[3];
            Equal(true, ambiguousAnswerSegment.All(value => value < 128));
            Equal("\\xD0\\xA0\\xC2\\xB0\\x2D\\x73\\x65\\x63\\x72\\x65\\x74\\n",
                Encoding.ASCII.GetString(ambiguousAnswerSegment));
            Equal("Р°-secret", KittySessionImporter.ImportDirectory(ambiguousAnswerDir).Single().RootPassword);

            // A failed source-to-backup move must not restore an older stale
            // backup over the untouched source: only this operation's own move
            // may be undone, and the original exception instance is rethrown.
            var rollbackDir = Path.Combine(rootDir, "KiTTYRollback", "Sessions");
            Directory.CreateDirectory(rollbackDir);
            var rollbackServer = new ManagedServer { Name = "Original", Host = "192.0.2.76", Password = "ssh-secret" };
            KittySessionWriter.Write(rollbackDir, rollbackServer, KittySessionWriter.WritableProperties);
            var rollbackSource = rollbackServer.SourceSessionPath!;
            var rollbackOriginal = File.ReadAllBytes(rollbackSource);
            var rollbackBackups = Path.Combine(Directory.GetParent(rollbackDir)!.FullName, "ManagerBackups", "Sessions");
            Directory.CreateDirectory(rollbackBackups);
            var rollbackStale = Path.Combine(rollbackBackups, "Original.renamed-backup");
            var rollbackStaleBytes = Encoding.UTF8.GetBytes("HostName\\192.0.2.10\\\nUserName\\stale-user\\\n");
            File.WriteAllBytes(rollbackStale, rollbackStaleBytes);
            rollbackServer.Name = "Renamed";
            var injectedMoveFailure = new IOException("injected source-to-backup move failure");
            var previousMoveHook = KittySessionWriter.SourceMoveHook;
            KittySessionWriter.SourceMoveHook = (_, destination) =>
            {
                if (destination.EndsWith(".renamed-backup", StringComparison.Ordinal)) throw injectedMoveFailure;
            };
            Exception? rollbackError = null;
            try { KittySessionWriter.Write(rollbackDir, rollbackServer, [nameof(ManagedServer.Name)]); }
            catch (Exception ex) { rollbackError = ex; }
            finally { KittySessionWriter.SourceMoveHook = previousMoveHook; }
            Equal(true, ReferenceEquals(injectedMoveFailure, rollbackError));
            Equal(true, rollbackOriginal.SequenceEqual(File.ReadAllBytes(rollbackSource)));
            Equal(true, rollbackStaleBytes.SequenceEqual(File.ReadAllBytes(rollbackStale)));
            Equal(false, File.Exists(Path.Combine(rollbackDir, "Renamed")));
            Equal(rollbackSource, rollbackServer.SourceSessionPath);

            // A failure inside the rollback itself must not be swallowed: the
            // user-facing error says the file may remain changed and names the
            // backup, the original verification error stays as InnerException,
            // the rollback exception object and backup path are attached to
            // Data, and the created .backup remains intact as recovery evidence.
            var restoreDir = Path.Combine(rootDir, "KiTTYRestoreFailure", "Sessions");
            Directory.CreateDirectory(restoreDir);
            var restoreServer = new ManagedServer { Name = "Original", Host = "192.0.2.82", Password = "ssh-secret" };
            KittySessionWriter.Write(restoreDir, restoreServer, KittySessionWriter.WritableProperties);
            var restoreSource = restoreServer.SourceSessionPath!;
            var restoreOriginal = File.ReadAllBytes(restoreSource);
            restoreServer.Password = "ssh-😀";
            var injectedRestoreFailure = new IOException("injected session restore failure");
            var previousRestoreHook = KittySessionWriter.RestoreWriteHook;
            KittySessionWriter.RestoreWriteHook = _ => throw injectedRestoreFailure;
            Exception? restoreError = null;
            try { KittySessionWriter.Write(restoreDir, restoreServer, [nameof(ManagedServer.Password)]); }
            catch (Exception ex) { restoreError = ex; }
            finally { KittySessionWriter.RestoreWriteHook = previousRestoreHook; }
            var restoreBackup = Path.Combine(
                Directory.GetParent(restoreDir)!.FullName, "ManagerBackups", "Sessions", "Original.backup");
            Equal(true, restoreError is IOException);
            Equal(true, restoreError!.Message.StartsWith(
                "KiTTY не подтвердила записанный SSH-пароль", StringComparison.Ordinal));
            Equal(true, restoreError.Message.Contains(injectedRestoreFailure.Message, StringComparison.Ordinal));
            Equal(true, restoreError.Message.Contains(restoreBackup, StringComparison.Ordinal));
            Equal(false, restoreError.Message.Contains(
                "Изменение оставлено неприменённым", StringComparison.Ordinal));
            Equal(true, restoreError.InnerException is IOException);
            Equal(true, restoreError.InnerException!.Message.StartsWith(
                "KiTTY не подтвердила записанный SSH-пароль", StringComparison.Ordinal));
            Equal(false, ReferenceEquals(restoreError.InnerException, injectedRestoreFailure));
            Equal(true, ReferenceEquals(restoreError.Data["RollbackFailure"], injectedRestoreFailure));
            Equal(restoreBackup, restoreError.Data["RollbackBackup"] as string);
            Equal(true, File.Exists(restoreBackup));
            Equal(true, restoreOriginal.SequenceEqual(File.ReadAllBytes(restoreBackup)));
            Equal(restoreSource, restoreServer.SourceSessionPath);

            // A root password equal to the marker is ordinary user data: the
            // encoder must append a real structural trailer and the writer must
            // produce a valid session.
            var collisionPassword = KittyCredentialDecoder.ScriptFormatMarker;
            var collisionContent = "$\nsu -\nassword\n" + collisionPassword;
            var collisionDecoded = KittyCredentialDecoder.DecodeLoginScript(
                KittyCredentialDecoder.EncryptScriptContent(collisionContent), 0);
            Equal(collisionContent, collisionDecoded?.Content);
            Equal(collisionPassword, collisionDecoded?.RootPassword);
            var collisionDir = Path.Combine(rootDir, "KiTTYCollision", "Sessions");
            Directory.CreateDirectory(collisionDir);
            var collisionServer = new ManagedServer
            {
                Name = "SessionWriterCollision",
                Host = "192.0.2.72",
                Port = 22,
                Password = "ssh-pw",
                RootLogin = "su -",
                RootPassword = collisionPassword,
                RootPasswordPrompt = "assword"
            };
            var collisionError = "";
            try { KittySessionWriter.Write(collisionDir, collisionServer, KittySessionWriter.WritableProperties); }
            catch (Exception ex) { collisionError = ex.Message; }
            Equal("", collisionError);
            Equal(collisionPassword, KittySessionImporter.ImportDirectory(collisionDir).Single().RootPassword);

            // Emoji is allowed in a prompt but not in the answer sent through the
            // ANSI path: reject it with one clear ANSI/1251 error.
            var emojiRootServer = new ManagedServer
            {
                Name = "SessionWriterEmojiRoot",
                Host = "192.0.2.64",
                Port = 22,
                Password = "ssh-pw",
                RootLogin = "su -",
                RootPassword = "root-😀",
                RootPasswordPrompt = "PIN 😀:"
            };
            var emojiRootError = "";
            try
            {
                KittySessionWriter.Write(sessionsDir, emojiRootServer, KittySessionWriter.WritableProperties);
            }
            catch (InvalidOperationException ex)
            {
                emojiRootError = ex.Message;
            }
            Equal(true, emojiRootError.Contains("ANSI/1251", StringComparison.Ordinal));

            // A supported short password-only script with an automatic su/sudo
            // command must stay rewritable and verifiable.
            var shortDir = Path.Combine(rootDir, "KiTTYShort", "Sessions");
            Directory.CreateDirectory(shortDir);
            File.WriteAllLines(Path.Combine(shortDir, "Short"),
            [
                "HostName\\192.0.2.70\\",
                "Password\\" + KittyCredentialDecoder.EncodePassword("ssh-secret", "192.0.2.70", "xterm", 0) + "\\",
                "Autocommand\\su%20-\\",
                "ScriptfileContent\\" + KittyCredentialDecoder.EncryptScriptContent("assword:\nold-secret") + "\\"
            ]);
            var shortServer = KittySessionImporter.ImportDirectory(shortDir).Single();
            Equal("old-secret", shortServer.RootPassword);
            shortServer.KittyBaseline = ImportedSessionMerger.Snapshot(shortServer);
            shortServer.RootPassword = "new-secret";
            KittySessionWriter.Write(shortDir, shortServer, [nameof(ManagedServer.RootPassword)]);
            var shortAfter = KittySessionImporter.ImportDirectory(shortDir).Single();
            Equal("new-secret", shortAfter.RootPassword);
            Equal("ssh-secret", shortAfter.Password);
        }
        finally
        {
            try { Directory.Delete(rootDir, true); } catch { }
        }
    }
}

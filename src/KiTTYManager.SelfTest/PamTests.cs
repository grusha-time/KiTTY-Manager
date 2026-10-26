using System.Diagnostics;
using System.Text;
using KiTTYManager.App;
using KiTTYManager.Core;

internal sealed partial class SelfTestRunner
{
    private sealed class MockSecretProtector : IConfigSecretProtector
    {
        public string Protect(string plaintext) =>
            string.IsNullOrEmpty(plaintext) ? "" : DpapiConfigSecretProtector.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith(DpapiConfigSecretProtector.Prefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[DpapiConfigSecretProtector.Prefix.Length..]))
                : protectedValue;
    }

    private static void PamLoginPlanSequenceTests()
    {
        var fixedTime = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var target = new ManagedServer
        {
            Name = "TargetServer",
            Host = "10.1.2.3",
            Port = 22,
            Username = "target-user",
            Password = "target-main-pass",
            UsePam = true,
            PamSshCommand = "ssh -p 2222 app@internal.host",
            PamPassword = "pam-user-password",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            PamTotpAlgorithm = "SHA1",
            PamTotpDigits = 6,
            PamTotpPeriodSeconds = 30,
            PamTotpPrompt = "OTP: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "[Y/n]",
            RootLogin = "su -",
            RootPassword = "root-admin-password",
            ShellPrompt = "target$ "
        };
        var jumpHost = new ManagedServer
        {
            Name = "JumpHost",
            Host = "192.168.1.1",
            Port = 22,
            ShellPrompt = "jump$ "
        };

        var steps = PamLoginPlan.BuildPromptResponses(target, jumpHost, fixedTime);
        Equal(6, steps.Count);

        // Step 1: JumpHost prompt -> Shell isolation
        Equal("jump$ ", steps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", steps[0].Response);

        // Step 2: Isolated prompt -> PAM SSH command
        Equal("[pam-isolated] ", steps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh -p 2222 app@internal.host"), steps[1].Response);

        // Step 3: assword -> PAM password
        Equal("assword", steps[2].Prompt);
        Equal("pam-user-password", steps[2].Response);

        // Step 4: Confirm prompt -> "Y"
        Equal("[Y/n]", steps[3].Prompt);
        Equal("Y", steps[3].Response);

        // Step 5: Target shell prompt -> Root command
        Equal("target$ ", steps[4].Prompt);
        Equal("su -", steps[4].Response);

        // Step 6: assword -> Root password
        Equal("assword", steps[5].Prompt);
        Equal("root-admin-password", steps[5].Response);

        // None of the responses should ever be target.Password
        Equal(false, steps.Any(s => s.Response == target.Password));

        // Test minimal configuration with empty optional fields
        var minimalTarget = new ManagedServer
        {
            Name = "MinTarget",
            UsePam = true,
            PamSshCommand = "ssh min@internal"
        };
        var minimalJh = new ManagedServer { Name = "MinJH" };
        var minimalSteps = PamLoginPlan.BuildPromptResponses(minimalTarget, minimalJh, fixedTime);
        Equal(2, minimalSteps.Count);
        Equal("$", minimalSteps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", minimalSteps[0].Response);
        Equal("[pam-isolated] ", minimalSteps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh min@internal"), minimalSteps[1].Response);

        // Test custom command with quotes and auto-confirm disabled
        var customTarget = new ManagedServer
        {
            Name = "CustomTarget",
            UsePam = true,
            PamSshCommand = "ssh 'custom'@'internal'",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            PamTotpPrompt = "Enter 2FA Code: ",
            PamAutoConfirm = false
        };
        var customSteps = PamLoginPlan.BuildPromptResponses(customTarget, minimalJh, fixedTime);
        Equal(2, customSteps.Count);
        Equal("$", customSteps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", customSteps[0].Response);
        Equal("[pam-isolated] ", customSteps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh 'custom'@'internal'"), customSteps[1].Response);

        // Finding 1: Empty PamSshCommand throws InvalidOperationException
        var emptyCmdTarget = new ManagedServer { Name = "NoCmd", UsePam = true, PamSshCommand = "   " };
        var threwForEmpty = false;
        try
        {
            PamLoginPlan.BuildPromptResponses(emptyCmdTarget, minimalJh, fixedTime);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("не указана SSH-команда PAM"))
        {
            threwForEmpty = true;
        }
        Equal(true, threwForEmpty);

        // Finding 6: Elevation without root password (e.g. NOPASSWD sudo) still outputs elevation command
        var sudoTarget = new ManagedServer
        {
            Name = "SudoTarget",
            UsePam = true,
            PamSshCommand = "ssh sudo-user@target",
            RootLogin = "sudo -i",
            RootPassword = "",
            ShellPrompt = "target$ "
        };
        var sudoSteps = PamLoginPlan.BuildPromptResponses(sudoTarget, minimalJh, fixedTime);
        Equal(3, sudoSteps.Count);
        Equal("$", sudoSteps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", sudoSteps[0].Response);
        Equal("[pam-isolated] ", sudoSteps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh sudo-user@target"), sudoSteps[1].Response);
        Equal("target$ ", sudoSteps[2].Prompt);
        Equal("sudo -i", sudoSteps[2].Response);

        // Finding 2: JumpHost BaseProxy post-login command precedes PAM SSH command; JumpHost OTP is excluded
        var jhServer = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "JH",
            Password = "jh-ssh-password",
            ShellPrompt = "jh$ "
        };
        var jhProxy = new BaseProxy
        {
            StartupServerId = jhServer.Id,
            TotpSecret = "JBSWY3DPEHPK3PXP",
            TotpPrompt = "JH-OTP: ",
            PostLoginCommand = "source /etc/profile",
            RepeatAccountPasswordAfterCommand = true,
            PostLoginPasswordPrompt = "Password: "
        };
        var pamTarget = new ManagedServer
        {
            Name = "PAMTarget",
            UsePam = true,
            PamSshCommand = "ssh target@host",
            PamPassword = "target-pam-pass"
        };
        var jhSteps = PamLoginPlan.BuildPromptResponses(pamTarget, jhServer, fixedTime, jhProxy);
        Equal(5, jhSteps.Count);
        Equal(false, jhSteps.Any(s => s.Prompt.Contains("OTP")));
        Equal("$", jhSteps[0].Prompt);
        Equal("source /etc/profile", jhSteps[0].Response);
        Equal("Password:", jhSteps[1].Prompt);
        Equal("jh-ssh-password", jhSteps[1].Response);
        Equal("jh$ ", jhSteps[2].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", jhSteps[2].Response);
        Equal("[pam-isolated] ", jhSteps[3].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh target@host"), jhSteps[3].Response);
        Equal("assword", jhSteps[4].Prompt);
        Equal("target-pam-pass", jhSteps[4].Response);

        // Mismatched JumpHost proxy does NOT inject post-login command
        var mismatchedProxy = new BaseProxy
        {
            StartupServerId = Guid.NewGuid(),
            PostLoginCommand = "source /etc/wrong"
        };
        var mismatchedSteps = PamLoginPlan.BuildPromptResponses(pamTarget, jhServer, fixedTime, mismatchedProxy);
        Equal(3, mismatchedSteps.Count);
        Equal("jh$ ", mismatchedSteps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", mismatchedSteps[0].Response);
        Equal("[pam-isolated] ", mismatchedSteps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh target@host"), mismatchedSteps[1].Response);
        Equal("assword", mismatchedSteps[2].Prompt);
        Equal("target-pam-pass", mismatchedSteps[2].Response);

        // Finding 3: JumpHost root elevation is NOT injected into PAM sequence
        var jhWithRoot = new ManagedServer
        {
            Name = "JHWithRoot",
            RootLogin = "su -",
            RootPassword = "jh-root-pass",
            ShellPrompt = "jh$ "
        };
        var jhRootSteps = PamLoginPlan.BuildPromptResponses(pamTarget, jhWithRoot, fixedTime);
        Equal(3, jhRootSteps.Count);
        Equal("jh$ ", jhRootSteps[0].Prompt);
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", jhRootSteps[0].Response);
        Equal("[pam-isolated] ", jhRootSteps[1].Prompt);
        Equal(PamLoginPlan.FormatPamSshCommand("ssh target@host"), jhRootSteps[1].Response);
        Equal("assword", jhRootSteps[2].Prompt);
        Equal("target-pam-pass", jhRootSteps[2].Response);
    }

    private static void PamCredentialIndependenceTests()
    {
        var fixedTime = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var target = new ManagedServer
        {
            Name = "Target",
            Password = "target-main-pass-must-not-leak",
            PamPassword = "pam-ssh-specific-pass",
            RootPassword = "root-elevation-pass",
            RootLogin = "sudo -i",
            PamSshCommand = "ssh user@target",
            UsePam = true
        };
        var jumpHost = new ManagedServer
        {
            Name = "JumpHost",
            Host = "10.0.0.1",
            Port = 22,
            Username = "jh-user",
            Password = "jh-ssh-password"
        };

        var steps = PamLoginPlan.BuildPromptResponses(target, jumpHost, fixedTime);
        foreach (var step in steps)
        {
            Equal(false, step.Response == target.Password);
        }
        Equal(target.PamPassword, steps[2].Response);
        Equal(target.RootPassword, steps[4].Response);

        // Launch arguments test: KiTTY must connect to JumpHost, not sending target passwords
        var routedArgs = KittyLaunchPlan.PamRoutedConsoleArguments(target, jumpHost, 12345, false, null, "login.txt");
        Equal(true, routedArgs.Contains("-ssh"));
        Equal(true, routedArgs.Contains("127.0.0.1"));
        Equal(true, routedArgs.Contains("-P"));
        Equal(true, routedArgs.Contains("12345"));
        Equal(true, routedArgs.Contains("-pass"));
        Equal(true, routedArgs.Contains("jh-ssh-password"));
        Equal(false, routedArgs.Contains(target.Password));
        Equal(false, routedArgs.Contains(target.PamPassword));
        Equal(true, routedArgs.Contains("-title"));
        Equal(true, routedArgs.Contains($"{target.Name} — PAM-авторизация"));
        Equal(true, routedArgs.Contains("-loginscript"));
        Equal(true, routedArgs.Contains("login.txt"));

        var directArgs = KittyLaunchPlan.PamDirectConsoleArguments(target, jumpHost, "10.0.0.1", 22, false, null, "login.txt");
        Equal(true, directArgs.Contains("-ssh"));
        Equal(true, directArgs.Contains("10.0.0.1"));
        Equal(true, directArgs.Contains("-P"));
        Equal(true, directArgs.Contains("22"));
        Equal(true, directArgs.Contains("-pass"));
        Equal(true, directArgs.Contains("jh-ssh-password"));
        Equal(false, directArgs.Contains(target.Password));
        Equal(false, directArgs.Contains(target.PamPassword));
        Equal(true, directArgs.Contains("-title"));
        Equal(true, directArgs.Contains($"{target.Name} — PAM-авторизация"));
    }

    private static void PamSecretProtectionAndRedactionTests()
    {
        var protector = new MockSecretProtector();
        var server = new ManagedServer
        {
            Name = "PamSecureServer",
            PamPassword = "plain-pam-password-123",
            PamTotpSecret = "JBSWY3DPEHPK3PXP"
        };
        var config = new ManagerConfig
        {
            Groups = [new ServerGroup { Servers = [server] }]
        };

        // ContainsPlaintext finds unencrypted PAM secrets
        Equal(true, ConfigSecrets.ContainsPlaintext(config));

        // Protect encrypts both PamPassword and PamTotpSecret
        ConfigSecrets.Protect(config, protector);
        Equal(false, ConfigSecrets.ContainsPlaintext(config));
        Equal(true, server.PamPassword.StartsWith(DpapiConfigSecretProtector.Prefix, StringComparison.Ordinal));
        Equal(true, server.PamTotpSecret.StartsWith(DpapiConfigSecretProtector.Prefix, StringComparison.Ordinal));

        // Unprotect restores plaintext values
        ConfigSecrets.Unprotect(config, protector);
        Equal("plain-pam-password-123", server.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", server.PamTotpSecret);

        // SecretRedactor
        var secrets = SecretRedactor.Secrets(config);
        Equal(true, secrets.Contains("plain-pam-password-123"));
        Equal(true, secrets.Contains("JBSWY3DPEHPK3PXP"));

        var logText = "Connecting with secret plain-pam-password-123 and TOTP JBSWY3DPEHPK3PXP completed.";
        var redacted = SecretRedactor.Redact(logText, server);
        Equal(false, redacted.Contains("plain-pam-password-123"));
        Equal(false, redacted.Contains("JBSWY3DPEHPK3PXP"));
        Equal("Connecting with secret *** and TOTP *** completed.", redacted);
    }

    private static void PamExportSanitizationTests()
    {
        var jumpHost = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "JumpHostServer",
            PamPassword = "jh-pam-secret-to-clear",
            PamTotpSecret = "jh-totp-secret-to-clear"
        };
        var target = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "TargetServer",
            PamPassword = "target-pam-secret-preserved",
            PamTotpSecret = "target-totp-secret-preserved"
        };
        var proxy = new BaseProxy
        {
            Id = Guid.NewGuid(),
            Name = "EntryProxy",
            StartupServerId = jumpHost.Id,
            TotpSecret = "proxy-totp-secret-to-clear"
        };
        var config = new ManagerConfig
        {
            BaseProxies = [proxy],
            Groups = [new ServerGroup { Servers = [jumpHost, target] }]
        };

        // 1. Экспорт с точками входа
        var exported = ConfigTransfer.CreateExport(config, [jumpHost.Id, target.Id], includeEntryPoints: true);

        var exportedJh = exported.FindServer(jumpHost.Id);
        var exportedTarget = exported.FindServer(target.Id);

        Equal(true, exportedJh is not null);
        Equal(true, exportedTarget is not null);
        Equal("", exportedJh!.PamPassword);
        Equal("", exportedJh.PamTotpSecret);
        Equal("target-pam-secret-preserved", exportedTarget!.PamPassword);
        Equal("target-totp-secret-preserved", exportedTarget.PamTotpSecret);

        // 2. Экспорт без точек входа (секреты PAM сервера-JumpHost всё равно должны очищаться)
        var exportedWithoutEp = ConfigTransfer.CreateExport(config, [jumpHost.Id, target.Id], includeEntryPoints: false);

        var exportedJhWithoutEp = exportedWithoutEp.FindServer(jumpHost.Id);
        var exportedTargetWithoutEp = exportedWithoutEp.FindServer(target.Id);

        Equal(true, exportedJhWithoutEp is not null);
        Equal(true, exportedTargetWithoutEp is not null);
        Equal("", exportedJhWithoutEp!.PamPassword);
        Equal("", exportedJhWithoutEp.PamTotpSecret);
        Equal("target-pam-secret-preserved", exportedTargetWithoutEp!.PamPassword);
        Equal("target-totp-secret-preserved", exportedTargetWithoutEp.PamTotpSecret);
    }

    private static void PamServerEditorDirtyTrackerTests()
    {
        var server = new ManagedServer
        {
            Name = "Server1",
            Host = "10.0.0.1",
            Port = 22,
            UsePam = true,
            PamSshCommand = "ssh user@target",
            PamPassword = "pam-password",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            PamTotpAlgorithm = "SHA256",
            PamTotpDigits = 8,
            PamTotpPeriodSeconds = 60,
            PamTotpPrompt = "Enter OTP: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "Are you sure? [y/N]: "
        };

        ServerEditorDraft MakeDraft() => new()
        {
            ServerName = server.Name,
            Host = server.Host,
            PortText = server.Port.ToString(),
            UsePam = server.UsePam,
            PamSshCommand = server.PamSshCommand,
            PamPassword = server.PamPassword,
            PamTotpSecret = server.PamTotpSecret,
            PamTotpAlgorithm = server.PamTotpAlgorithm,
            PamTotpDigitsText = server.PamTotpDigits.ToString(),
            PamTotpPeriodText = server.PamTotpPeriodSeconds.ToString(),
            PamTotpPrompt = server.PamTotpPrompt,
            PamAutoConfirm = server.PamAutoConfirm,
            PamConfirmPrompt = server.PamConfirmPrompt
        };

        // Identical draft has no changes
        Equal(false, ServerEditorDirtyTracker.HasUnsavedChanges(server, MakeDraft()));

        // Modifying each field individually marks draft as dirty
        var d = MakeDraft(); d.UsePam = !d.UsePam; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamSshCommand += " modified"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamPassword += " modified"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamTotpSecret += " modified"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamTotpAlgorithm = "SHA512"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamTotpDigitsText = "6"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamTotpPeriodText = "30"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamTotpPrompt = "OTP: "; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamAutoConfirm = !d.PamAutoConfirm; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
        d = MakeDraft(); d.PamConfirmPrompt = "[Y/n]"; Equal(true, ServerEditorDirtyTracker.HasUnsavedChanges(server, d));
    }

    private static void PamHelpContentSearchTests()
    {
        var pamEntries = HelpContent.Search("PAM");
        Equal(true, pamEntries.Count >= 7);
        Equal(true, pamEntries.All(e => e.Section == "Сессия"));

        var usePam = HelpContent.Search("Использовать PAM");
        Equal(true, usePam.Any(e => e.Title == "Использовать PAM" && e.Section == "Сессия"));

        var sshCmd = HelpContent.Search("SSH-команда PAM");
        Equal(true, sshCmd.Any(e => e.Title == "SSH-команда PAM" && e.Section == "Сессия"));

        var pamPw = HelpContent.Search("Пароль PAM");
        Equal(true, pamPw.Any(e => e.Title == "Пароль PAM" && e.Section == "Сессия"));

        var totpSecret = HelpContent.Search("TOTP-секрет PAM");
        Equal(true, totpSecret.Any(e => e.Title == "TOTP-секрет PAM" && e.Section == "Сессия"));

        var paramsEntry = HelpContent.Search("Алгоритм, цифры и период PAM TOTP");
        Equal(true, paramsEntry.Any(e => e.Title == "Алгоритм, цифры и период PAM TOTP" && e.Section == "Сессия"));

        var otpPrompt = HelpContent.Search("Запрос OTP PAM");
        Equal(true, otpPrompt.Any(e => e.Title == "Запрос OTP PAM" && e.Section == "Сессия"));

        var confirmPrompt = HelpContent.Search("Автоподтверждение и запрос подтверждения PAM");
        Equal(true, confirmPrompt.Any(e => e.Title == "Автоподтверждение и запрос подтверждения PAM" && e.Section == "Сессия"));
    }

    private static void PamSafeMarginCalculationTests()
    {
        // For periods > 8, SafeMargin is 8
        Equal(8, PamLoginPlan.SafeMargin(30));
        Equal(8, PamLoginPlan.SafeMargin(9));

        // For short periods <= 8, SafeMargin is Math.Max(1, period / 2)
        Equal(4, PamLoginPlan.SafeMargin(8));
        Equal(1, PamLoginPlan.SafeMargin(3));
        Equal(1, PamLoginPlan.SafeMargin(2));
        Equal(1, PamLoginPlan.SafeMargin(1));

        // Verify that for short periods (2, 3, 8), the safe margin calculation terminates and does not loop indefinitely
        int[] shortPeriods = [2, 3, 8];
        foreach (var period in shortPeriods)
        {
            var margin = PamLoginPlan.SafeMargin(period);
            for (long second = 0; second < period * 5; second++)
            {
                var remaining = period - (second % period);
                if (remaining < margin)
                {
                    var delay = (int)remaining + 1;
                    var nextSecond = second + delay;
                    var nextRemaining = period - (nextSecond % period);
                    // Next remaining MUST be >= margin, guaranteeing the while loop terminates in at most 1 delay
                    Equal(true, nextRemaining >= margin);
                }
            }
        }
    }

    private static void KittyLoginScriptEscapeResponseTests()
    {
        // Null or empty returns empty
        Equal("", KittyLoginScript.EscapeResponse(""));

        // Single backslash is escaped to double backslash matching KiTTY SendKeyboardPlus rules
        Equal(@"domain\\user", KittyLoginScript.EscapeResponse(@"domain\user"));
        Equal(@"pass\\word", KittyLoginScript.EscapeResponse(@"pass\word"));
        Equal(@"\\leading", KittyLoginScript.EscapeResponse(@"\leading"));
        Equal(@"multiple\\\\slashes", KittyLoginScript.EscapeResponse(@"multiple\\slashes"));

        // Trailing backslash appends \n so KiTTY sends the literal \ and explicitly sends Enter
        Equal(@"secret\\\n", KittyLoginScript.EscapeResponse(@"secret\"));
        Equal(@"domain\\user\\\n", KittyLoginScript.EscapeResponse(@"domain\user\"));
        Equal(@"pass\\\\\n", KittyLoginScript.EscapeResponse(@"pass\\"));

        // Strings without backslash remain unchanged
        Equal("simplePassword123!", KittyLoginScript.EscapeResponse("simplePassword123!"));
        Equal("user@domain.com", KittyLoginScript.EscapeResponse("user@domain.com"));

        // Verify against decoded terminal output rules of KiTTY SendKeyboardPlus:
        // KiTTY parses \\ as literal \, \n as <ENTER> (\r), and if line ends with unescaped \, strips it & suppresses Enter.
        static (string text, bool enter) DecodeKiTTY(string line)
        {
            var sb = new StringBuilder();
            bool enter = true;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    char next = line[i + 1];
                    if (next == '\\') { sb.Append('\\'); i++; }
                    else if (next == 'n') { enter = true; i++; }
                    else if (next == 't') { sb.Append('\t'); i++; }
                    else { sb.Append(next); i++; }
                }
                else if (line[i] == '\\' && i + 1 == line.Length)
                {
                    enter = false;
                }
                else
                {
                    sb.Append(line[i]);
                }
            }
            return (sb.ToString(), enter);
        }

        var decodedSecret = DecodeKiTTY(KittyLoginScript.EscapeResponse(@"secret\"));
        Equal(@"secret\", decodedSecret.text);
        Equal(true, decodedSecret.enter);

        var decodedDomainUser = DecodeKiTTY(KittyLoginScript.EscapeResponse(@"domain\user\"));
        Equal(@"domain\user\", decodedDomainUser.text);
        Equal(true, decodedDomainUser.enter);

        var decodedPass = DecodeKiTTY(KittyLoginScript.EscapeResponse(@"pass\\"));
        Equal(@"pass\\", decodedPass.text);
        Equal(true, decodedPass.enter);

        // Verify that KittyLoginScript.Create(steps) writes escaped replies to file
        var steps = new List<JumphostPromptResponse>
        {
            new("assword", @"pass\word"),
            new("prompt$", @"domain\user")
        };
        using var script = KittyLoginScript.Create(steps);
        Equal(true, script is not null);
        var lines = File.ReadAllLines(script!.Path);
        Equal(4, lines.Length);
        Equal("assword", lines[0]);
        Equal(@"pass\\word", lines[1]);
        Equal("prompt$", lines[2]);
        Equal(@"domain\\user", lines[3]);

        // Verify root password escaping in KittyLoginScript.Create(server)
        var server = new ManagedServer
        {
            RootLogin = "su -",
            RootPassword = @"root\pass"
        };
        using var rootScript = KittyLoginScript.Create(server);
        Equal(true, rootScript is not null);
        var rootLines = File.ReadAllLines(rootScript!.Path);
        Equal(@"root\\pass", rootLines[3]);
    }

    private static void PamFailedLoginTranscriptTests()
    {
        var target = new ManagedServer
        {
            Name = "FailTarget",
            UsePam = true,
            PamSshCommand = "ssh target-user@target-host",
            PamPassword = "pam-pass",
            RootLogin = "sudo -i",
            RootPassword = "root-pass",
            ShellPrompt = "target$ "
        };
        var jumpHost = new ManagedServer
        {
            Name = "JumpHost",
            Host = "jump.internal",
            ShellPrompt = "jump$ "
        };

        var steps = PamLoginPlan.BuildPromptResponses(target, jumpHost, DateTimeOffset.UtcNow);

        // Step 1: on JumpHost prompt ("jump$ "), sends isolated shell command
        var isolateStep = steps.First(s => s.Prompt == "jump$ ");
        Equal("ENV= PS1='[pam-isolated] ' PROMPT='[pam-isolated] ' exec sh", isolateStep.Response);

        // Step 2: on isolated prompt ("[pam-isolated] "), sends target PAM SSH command directly
        var pamCommandStep = steps.First(s => s.Prompt == "[pam-isolated] ");
        Equal(PamLoginPlan.FormatPamSshCommand("ssh target-user@target-host"), pamCommandStep.Response);
        Equal("ssh target-user@target-host", pamCommandStep.Response);

        // Verify prompt detection: [pam-isolated] is not detected as an active shell prompt
        // even when JumpHost and Target both use '$'
        var isolatedPrompt = "[pam-isolated] ";
        Equal(false, KittyWindowInput.IsActiveShellPrompt(isolatedPrompt));
        Equal(false, KittyWindowInput.IsActiveShellPrompt(isolatedPrompt, "$"));
        Equal(false, KittyWindowInput.IsActiveShellPrompt(isolatedPrompt, "target$ "));
        Equal(false, KittyWindowInput.IsActiveShellPrompt($"user@jump:~$ {pamCommandStep.Response}\n{isolatedPrompt}", "$"));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("user@target:~$ ", "$"));

        // Live shell check: in isolated shell, any SSH command exit (exit 1, exit 0, syntax error)
        // returns control to [pam-isolated] while shell process remains active instead of executing target prompts.
        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sh",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi)!)
            {
                // 1. Enter isolated shell
                proc.StandardInput.WriteLine(isolateStep.Response);
                proc.StandardInput.Flush();
                Thread.Sleep(200);
                Equal(false, proc.HasExited);

                // 2. SSH exit 1 (authentication failure / network failure)
                proc.StandardInput.WriteLine("sh -c 'exit 1'");
                proc.StandardInput.Flush();
                Thread.Sleep(200);
                Equal(false, proc.HasExited);

                // 3. SSH exit 0 (remote session ended immediately / closed by peer)
                proc.StandardInput.WriteLine("sh -c 'exit 0'");
                proc.StandardInput.Flush();
                Thread.Sleep(200);
                Equal(false, proc.HasExited);

                // 4. Shell syntax error (bad parameters, unquoted parens, etc.)
                proc.StandardInput.WriteLine("sh -c 'if'");
                proc.StandardInput.Flush();
                Thread.Sleep(200);
                Equal(false, proc.HasExited);

                // 5. Verify isolated shell remained alive and can execute diagnostic commands
                proc.StandardInput.WriteLine("echo ISOLATED_SHELL_ALIVE; exit");
                proc.StandardInput.Flush();
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                Equal(true, output.Contains("ISOLATED_SHELL_ALIVE"));
            }
        }

        // Real shell check 1: Shell syntax error in command (e.g. unquoted parens: ssh user@(target))
        // exec sh -c replaces the outer shell before sh parses the syntax.
        // When sh encounters syntax error, it exits with non-zero code and the session terminates immediately.
        var syntaxErrorCmd = PamLoginPlan.WrapExecCommand("ssh user@(target)");
        var psiSyntax = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            psiSyntax.ArgumentList.Add("/c");
            psiSyntax.ArgumentList.Add(syntaxErrorCmd);
        }
        else
        {
            psiSyntax.ArgumentList.Add("-c");
            psiSyntax.ArgumentList.Add(syntaxErrorCmd);
        }
        using (var proc = Process.Start(psiSyntax)!)
        {
            proc.WaitForExit(5000);
            Equal(true, proc.HasExited);
            Equal(false, proc.ExitCode == 0); // Non-zero exit on syntax error
        }

        // Real shell check 2: Nonexistent command (/nonexistent/review-ssh user@target)
        var nonexistentCmd = PamLoginPlan.WrapExecCommand("/nonexistent/review-ssh user@target");
        var psiNonexistent = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            psiNonexistent.ArgumentList.Add("/c");
            psiNonexistent.ArgumentList.Add(nonexistentCmd);
        }
        else
        {
            psiNonexistent.ArgumentList.Add("-c");
            psiNonexistent.ArgumentList.Add(nonexistentCmd);
        }
        using (var proc = Process.Start(psiNonexistent)!)
        {
            proc.WaitForExit(5000);
            Equal(true, proc.HasExited);
            Equal(false, proc.ExitCode == 0); // Non-zero exit on command failure
        }
    }

    private static void InitialSshOtpVsNestedPamOtpTests()
    {
        var fixedTime = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        // 1. JumpHost Proxy initial SSH OTP
        var jhServer = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "JH-Bastion",
            Host = "192.168.1.1",
            Port = 22,
            Password = "jh-ssh-password",
            ShellPrompt = "jh$ "
        };
        var jhProxy = new BaseProxy
        {
            StartupServerId = jhServer.Id,
            TotpSecret = "JBSWY3DPEHPK3PXP", // Secret for JumpHost SSH auth
            TotpDigits = 6,
            TotpPeriodSeconds = 30,
            TotpAlgorithm = "SHA1",
            TotpPrompt = "JH-OTP: ",
            PostLoginCommand = "source /etc/profile"
        };

        // 2. Nested PAM Target OTP
        var pamTarget = new ManagedServer
        {
            Name = "NestedTarget",
            Host = "10.0.0.5",
            Port = 22,
            UsePam = true,
            PamSshCommand = "ssh -p 22 internal@target",
            PamPassword = "nested-pam-password",
            PamTotpSecret = "GEZDGNBVGY3TQOJQ", // Separate Secret for Target PAM auth
            PamTotpDigits = 8,
            PamTotpPeriodSeconds = 60,
            PamTotpAlgorithm = "SHA256",
            PamTotpPrompt = "Target-PAM-OTP: ",
            ShellPrompt = "target$ "
        };

        // JumpHost initial SSH OTP and Nested PAM OTP are generated with separate parameters
        var jhInitialOtp = TotpGenerator.Generate(
            jhProxy.TotpSecret, fixedTime, jhProxy.TotpDigits, jhProxy.TotpPeriodSeconds, jhProxy.TotpAlgorithm);
        Equal("324550", jhInitialOtp);

        var targetPamOtp = TotpGenerator.Generate(
            pamTarget.PamTotpSecret, fixedTime, pamTarget.PamTotpDigits, pamTarget.PamTotpPeriodSeconds, pamTarget.PamTotpAlgorithm);
        Equal(8, targetPamOtp.Length);
        Equal(false, jhInitialOtp == targetPamOtp);

        // Neither JumpHost initial SSH OTP nor nested PAM OTP is statically placed in login script.
        // Both are generated and delivered dynamically upon their actual challenge prompts.
        var steps = PamLoginPlan.BuildPromptResponses(pamTarget, jhServer, fixedTime, jhProxy);
        Equal(4, steps.Count);
        Equal(false, steps.Any(s => s.Prompt.Contains("OTP") || s.Response == jhInitialOtp || s.Response == targetPamOtp));

        // 3. Test IsActivePrompt handling: banners, completed lines, and ANSI sequences
        var bannerWithPassword = "Welcome! Two-factor OTP authentication is enabled.\r\nPassword: ";
        Equal(false, KittyWindowInput.IsActivePrompt(bannerWithPassword, "OTP"));
        Equal(true, KittyWindowInput.IsActivePrompt(bannerWithPassword, "Password:"));

        var challengeAppeared = "Welcome! Two-factor OTP authentication is enabled.\r\nPassword: pass\r\nOTP: ";
        Equal(true, KittyWindowInput.IsActivePrompt(challengeAppeared, "OTP"));

        var challengeCompleted = "Welcome! Two-factor OTP authentication is enabled.\r\nPassword: pass\r\nOTP: 123456\r\n";
        Equal(false, KittyWindowInput.IsActivePrompt(challengeCompleted, "OTP"));

        var ansiChallenge = "Login banner\r\n\x1B[0m\x1B[1mOTP: ";
        Equal(true, KittyWindowInput.IsActivePrompt(ansiChallenge, "OTP"));

        // P2 Fix: OSC sequence stripping with BEL (\x07), 7-bit ST (\x1b\\), and 8-bit ST (\x9c)
        var oscBel = "\x1B]0;OTP: login help\x07Password: ";
        Equal(false, KittyWindowInput.IsActivePrompt(oscBel, "OTP"));
        Equal(true, KittyWindowInput.IsActivePrompt(oscBel, "Password:"));

        var osc7BitSt = "\x1B]0;OTP: login help\x1B\\Password: ";
        Equal(false, KittyWindowInput.IsActivePrompt(osc7BitSt, "OTP"));
        Equal(true, KittyWindowInput.IsActivePrompt(osc7BitSt, "Password:"));

        var osc8BitSt = "\x1B]0;OTP: login help\x9CPassword: ";
        Equal(false, KittyWindowInput.IsActivePrompt(osc8BitSt, "OTP"));
        Equal(true, KittyWindowInput.IsActivePrompt(osc8BitSt, "Password:"));

        // 4. Verify prompt-verified delivery via SendLineAfterPromptAsync
        var tempLog = Path.Combine(Path.GetTempPath(), $"selftest-otp-log-{Guid.NewGuid():N}.log");
        try
        {
            string? deliveredOtp = null;
            Func<Process, string, CancellationToken, Task> mockSender = (_, val, _) =>
            {
                deliveredOtp = val;
                return Task.CompletedTask;
            };

            using var currentProcess = Process.GetCurrentProcess();

            // A. Banner-before-challenge: "OTP" in banner but prompt is "Password: "
            // SendLineAfterPromptAsync must NOT deliver OTP to the password prompt.
            File.WriteAllText(tempLog, "Connecting to jump.internal...\r\nWelcome! OTP auth enabled.\r\nPassword: ");
            bool timedOutOnBanner = false;
            try
            {
                KittyWindowInput.SendLineAfterPromptAsync(
                    currentProcess, tempLog, "OTP", jhInitialOtp, TimeSpan.FromMilliseconds(100), CancellationToken.None, mockSender)
                    .GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                timedOutOnBanner = true;
            }
            Equal(true, timedOutOnBanner);
            Equal(null, deliveredOtp);

            // B. Password is submitted, and active challenge prompt appears:
            File.AppendAllText(tempLog, "secret\r\nJH-OTP: ");
            KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "JH-OTP", jhInitialOtp, TimeSpan.FromSeconds(1), CancellationToken.None, mockSender)
                .GetAwaiter().GetResult();
            Equal(jhInitialOtp, deliveredOtp);

            // C. Clock crosses TOTP boundary during wait: OTP is generated fresh upon prompt detection
            var t0 = DateTimeOffset.FromUnixTimeSeconds(29999); // 29999 / 30 = 999
            var t1 = DateTimeOffset.FromUnixTimeSeconds(30001); // 30001 / 30 = 1000
            var otpAtStart = TotpGenerator.Generate(jhProxy.TotpSecret, t0, jhProxy.TotpDigits, jhProxy.TotpPeriodSeconds, jhProxy.TotpAlgorithm);
            var otpAtPrompt = TotpGenerator.Generate(jhProxy.TotpSecret, t1, jhProxy.TotpDigits, jhProxy.TotpPeriodSeconds, jhProxy.TotpAlgorithm);
            Equal(false, otpAtStart == otpAtPrompt);

            File.WriteAllText(tempLog, "Connecting to nested target...\r\nPassword: pass\r\n");
            deliveredOtp = null;
            var currentTime = t0;

            var waitTask = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "Target-PAM-OTP",
                () => TotpGenerator.Generate(jhProxy.TotpSecret, currentTime, jhProxy.TotpDigits, jhProxy.TotpPeriodSeconds, jhProxy.TotpAlgorithm),
                TimeSpan.FromSeconds(2), CancellationToken.None, mockSender);

            // Simulate clock crossing boundary during connection before prompt appears
            Task.Delay(100).GetAwaiter().GetResult();
            currentTime = t1;
            File.AppendAllText(tempLog, "Target-PAM-OTP: ");

            waitTask.GetAwaiter().GetResult();
            Equal(otpAtPrompt, deliveredOtp);
            Equal(false, deliveredOtp == otpAtStart);

            // D. P1 Sequential two-OTP regression test:
            // JumpHost OTP and PAM OTP both expect prompt "OTP" in the same shared session log.
            // After JumpHost OTP is consumed, PAM OTP must not fire on the old unprogressed JumpHost prompt.
            File.WriteAllText(tempLog, "Connecting to jump.internal...\r\nOTP: ");
            deliveredOtp = null;
            var jhOffset = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", "111111", TimeSpan.FromSeconds(1), CancellationToken.None, mockSender)
                .GetAwaiter().GetResult();
            Equal("111111", deliveredOtp);
            Equal(true, jhOffset > 0);

            // Call PAM OTP with UNCHANGED log, minOffset: jhOffset, and progressionCheck
            deliveredOtp = null;
            var pamTimedOutWithoutProgression = false;
            try
            {
                KittyWindowInput.SendLineAfterPromptAsync(
                    currentProcess, tempLog, "OTP", () => "222222", TimeSpan.FromMilliseconds(100), CancellationToken.None, mockSender,
                    minOffset: jhOffset,
                    progressionCheck: text => text.Contains("exec sh -c", StringComparison.OrdinalIgnoreCase) || text.Contains("ssh target", StringComparison.OrdinalIgnoreCase))
                    .GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                pamTimedOutWithoutProgression = true;
            }
            Equal(true, pamTimedOutWithoutProgression);
            Equal(null, deliveredOtp);

            // Append progression: JumpHost logs the entered OTP, then executes ssh target, and target issues its own OTP challenge
            File.AppendAllText(tempLog, "111111\r\njump$ exec sh -c 'exec ssh target'\r\nPassword: pass\r\nOTP: ");
            var pamOffset = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", () => "222222", TimeSpan.FromSeconds(1), CancellationToken.None, mockSender,
                minOffset: jhOffset,
                progressionCheck: text => text.Contains("exec sh -c", StringComparison.OrdinalIgnoreCase) || text.Contains("ssh target", StringComparison.OrdinalIgnoreCase))
                .GetAwaiter().GetResult();
            Equal("222222", deliveredOtp);
            Equal(true, pamOffset > jhOffset);

            // E. P1 Manual JumpHost OTP vs nested PAM OTP regression test:
            // JumpHost requires manual OTP (empty JumpHost secret, consumedOffset = 0).
            // JumpHost presents initial "TOTP: " prompt which matches PAM "OTP" substring.
            // PAM OTP waiter must NOT answer the manual JumpHost challenge before progression to nested session.
            File.WriteAllText(tempLog, "Connecting to jump.internal...\r\nTOTP: ");
            deliveredOtp = null;
            var manualJhPromptIntercepted = false;
            try
            {
                KittyWindowInput.SendLineAfterPromptAsync(
                    currentProcess, tempLog, "OTP", () => "99999999", TimeSpan.FromMilliseconds(100), CancellationToken.None, mockSender,
                    minOffset: 0,
                    progressionCheck: text => text.Contains("exec sh -c", StringComparison.OrdinalIgnoreCase) || text.Contains("ssh target", StringComparison.OrdinalIgnoreCase))
                    .GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                manualJhPromptIntercepted = true;
            }
            Equal(true, manualJhPromptIntercepted);
            Equal(null, deliveredOtp);

            // User completes manual JumpHost OTP, JumpHost enters shell, executes PAM command, and target PAM OTP prompt appears
            File.AppendAllText(tempLog, "manual-jh-code\r\njump$ exec sh -c 'exec ssh target'\r\nPassword: pass\r\nOTP: ");
            var targetPamOffset = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", () => "99999999", TimeSpan.FromSeconds(1), CancellationToken.None, mockSender,
                minOffset: 0,
                progressionCheck: text => text.Contains("exec sh -c", StringComparison.OrdinalIgnoreCase) || text.Contains("ssh target", StringComparison.OrdinalIgnoreCase))
                .GetAwaiter().GetResult();
            Equal("99999999", deliveredOtp);
            Equal(true, targetPamOffset > 0);
        }
        finally
        {
            try { File.Delete(tempLog); } catch { }
        }
    }

    private static void SessionLogCleanupTiedToProcessLifetimeTests()
    {
        // 1. Log file cleanup is tied to process exit: retained while process runs, deleted after exit
        var tempLog = Path.Combine(Path.GetTempPath(), $"selftest-log-proc-{Guid.NewGuid():N}.log");
        File.WriteAllText(tempLog, "temporary session log content\r\n");
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("ping 127.0.0.1 -n 2 > nul");
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sleep 0.2");
        }
        var process = Process.Start(psi)!;
        try
        {
            var cleanupTask = KittyWindowInput.CleanTemporaryLogOnExitAsync(process, tempLog, TimeSpan.FromSeconds(5));
            Equal(true, File.Exists(tempLog));
            cleanupTask.GetAwaiter().GetResult();
            Equal(false, File.Exists(tempLog));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { File.Delete(tempLog); } catch { }
        }

        // 2. Retry loop on file lock / sharing violation: retries until released, then verifies removal
        var retryLog = Path.Combine(Path.GetTempPath(), $"selftest-log-retry-{Guid.NewGuid():N}.log");
        File.WriteAllText(retryLog, "retry session log content\r\n");
        try
        {
            var failureCount = 3;
            var cleanupTask = KittyWindowInput.CleanTemporaryLogOnExitAsync(null, retryLog, TimeSpan.FromSeconds(3), path =>
            {
                if (failureCount-- > 0)
                    throw new IOException("The process cannot access the file because it is being used by another process.");
                File.Delete(path);
            });
            cleanupTask.GetAwaiter().GetResult();
            Equal(false, File.Exists(retryLog));
            Equal(-1, failureCount); // All retries were executed until lock released and deletion succeeded
        }
        finally
        {
            try { File.Delete(retryLog); } catch { }
        }

        // 3. CleanupStaleLogs removes abandoned kitty-manager-jh-otp-*.log files from previous sessions
        var staleDir = Path.Combine(Path.GetTempPath(), $"selftest-staledir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staleDir);
        try
        {
            var stale1 = Path.Combine(staleDir, $"kitty-manager-jh-otp-{Guid.NewGuid():N}.log");
            var stale2 = Path.Combine(staleDir, $"kitty-manager-jh-otp-{Guid.NewGuid():N}.log");
            var keepFile = Path.Combine(staleDir, "other-log.log");
            File.WriteAllText(stale1, "stale log 1");
            File.WriteAllText(stale2, "stale log 2");
            File.WriteAllText(keepFile, "keep");

            KittyWindowInput.CleanupStaleLogs(staleDir);

            Equal(false, File.Exists(stale1));
            Equal(false, File.Exists(stale2));
            Equal(true, File.Exists(keepFile));
        }
        finally
        {
            try { Directory.Delete(staleDir, true); } catch { }
        }
    }

    private static void PamSmartImportFieldAcceptRejectTests()
    {
        var local = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Server1",
            Host = "10.0.0.1",
            Port = 22,
            Username = "user",
            UsePam = false,
            PamSshCommand = "",
            PamPassword = "",
            PamTotpSecret = "",
            PamTotpAlgorithm = "SHA1",
            PamTotpDigits = 6,
            PamTotpPeriodSeconds = 30,
            PamTotpPrompt = "OTP: ",
            PamAutoConfirm = false,
            PamConfirmPrompt = "[Y/n]"
        };

        var incomingServer = new ManagedServer
        {
            Id = local.Id,
            Name = "Server1",
            Host = "10.0.0.1",
            Port = 22,
            Username = "user",
            UsePam = true,
            PamSshCommand = "ssh -p 2222 app@internal",
            PamPassword = "new-pam-secret-password",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            PamTotpAlgorithm = "SHA256",
            PamTotpDigits = 8,
            PamTotpPeriodSeconds = 60,
            PamTotpPrompt = "Verification Code: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "Continue? (y/n)"
        };

        var current = new ManagerConfig { UngroupedServers = [local] };
        var incoming = new ManagerConfig { UngroupedServers = [incomingServer] };

        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);

        // 1. All 10 PAM fields are detected as differing
        var pamFields = plan.Fields.Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam").ToList();
        Equal(10, pamFields.Count);

        // 2. Secret fields are redacted in the comparison view (CurrentValue/IncomingValue)
        var passField = pamFields.Single(f => f.PropertyName == "PamPassword");
        var secretField = pamFields.Single(f => f.PropertyName == "PamTotpSecret");
        Equal("не задан", passField.CurrentValue);
        Equal("задан", passField.IncomingValue);
        Equal("не задан", secretField.CurrentValue);
        Equal("задан", secretField.IncomingValue);
        Equal(false, passField.IncomingValue.Contains("new-pam-secret-password"));
        Equal(false, secretField.IncomingValue.Contains("JBSWY3DPEHPK3PXP"));

        // 3. Selective acceptance: accept UsePam, PamSshCommand, PamTotpSecret, reject PamPassword, PamAutoConfirm, PamConfirmPrompt
        foreach (var field in pamFields)
        {
            if (field.PropertyName is "PamPassword" or "PamAutoConfirm" or "PamConfirmPrompt")
                field.UseIncoming = false; // Reject, keep current
            else
                field.UseIncoming = true; // Accept from incoming
        }

        var merged = ConfigTransfer.MergeSmartImport(current, incoming, plan);
        var mergedServer = merged.UngroupedServers.Single();

        Equal(true, mergedServer.UsePam);
        Equal("ssh -p 2222 app@internal", mergedServer.PamSshCommand);
        Equal("", mergedServer.PamPassword); // Kept current (rejected incoming)
        Equal("JBSWY3DPEHPK3PXP", mergedServer.PamTotpSecret); // Accepted
        Equal("SHA256", mergedServer.PamTotpAlgorithm); // Accepted
        Equal(8, mergedServer.PamTotpDigits); // Accepted
        Equal(60, mergedServer.PamTotpPeriodSeconds); // Accepted
        Equal("Verification Code: ", mergedServer.PamTotpPrompt); // Accepted
        Equal(false, mergedServer.PamAutoConfirm); // Kept current (rejected incoming)
        Equal("[Y/n]", mergedServer.PamConfirmPrompt); // Kept current (rejected incoming)

        // 4. Test session-level Decision = UseIncoming (replaces all fields including all PAM fields)
        var fullIncomingPlan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        fullIncomingPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var fullMerged = ConfigTransfer.MergeSmartImport(current, incoming, fullIncomingPlan);
        var fullServer = fullMerged.UngroupedServers.Single();
        Equal(true, fullServer.UsePam);
        Equal("ssh -p 2222 app@internal", fullServer.PamSshCommand);
        Equal("new-pam-secret-password", fullServer.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", fullServer.PamTotpSecret);
        Equal("SHA256", fullServer.PamTotpAlgorithm);
        Equal(8, fullServer.PamTotpDigits);
        Equal(60, fullServer.PamTotpPeriodSeconds);
        Equal("Verification Code: ", fullServer.PamTotpPrompt);
        Equal(true, fullServer.PamAutoConfirm);
        Equal("Continue? (y/n)", fullServer.PamConfirmPrompt);

        // 5. Test session-level Decision = Skip
        var skipPlan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        skipPlan.Sessions.Single().Decision = ImportDecision.Skip;
        var skipMerged = ConfigTransfer.MergeSmartImport(current, incoming, skipPlan);
        var skipServer = skipMerged.UngroupedServers.Single();
        Equal(false, skipServer.UsePam);
        Equal("", skipServer.PamSshCommand);
        Equal("", skipServer.PamPassword);
    }

    private static void PamExportImportRoundTripTests()
    {
        var server = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "PamRoundTripServer",
            Host = "192.168.10.50",
            Port = 22,
            Username = "admin",
            UsePam = true,
            PamSshCommand = "ssh -p 2200 devops@bastion.internal",
            PamPassword = "secure-pam-password",
            PamTotpSecret = "MFRGGZDFMZTWQ2LK",
            PamTotpAlgorithm = "SHA512",
            PamTotpDigits = 7,
            PamTotpPeriodSeconds = 45,
            PamTotpPrompt = "Enter 2FA: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "Are you sure? "
        };

        var config = new ManagerConfig
        {
            UngroupedServers = [server]
        };

        var exportConfig = ConfigTransfer.CreateExport(config, [server.Id], includeEntryPoints: false);
        var tempFile = Path.Combine(Path.GetTempPath(), $"pam-export-{Guid.NewGuid():N}.json");
        try
        {
            ConfigStore.Export(tempFile, exportConfig);
            var reloaded = ConfigStore.Import(tempFile);

            var loadedServer = reloaded.UngroupedServers.Single();
            Equal(server.Id, loadedServer.Id);
            Equal(server.Name, loadedServer.Name);
            Equal(server.UsePam, loadedServer.UsePam);
            Equal(server.PamSshCommand, loadedServer.PamSshCommand);
            Equal(server.PamPassword, loadedServer.PamPassword);
            Equal(server.PamTotpSecret, loadedServer.PamTotpSecret);
            Equal(server.PamTotpAlgorithm, loadedServer.PamTotpAlgorithm);
            Equal(server.PamTotpDigits, loadedServer.PamTotpDigits);
            Equal(server.PamTotpPeriodSeconds, loadedServer.PamTotpPeriodSeconds);
            Equal(server.PamTotpPrompt, loadedServer.PamTotpPrompt);
            Equal(server.PamAutoConfirm, loadedServer.PamAutoConfirm);
            Equal(server.PamConfirmPrompt, loadedServer.PamConfirmPrompt);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void ActiveShellPromptDetectionTests()
    {
        // 1. Standard shell markers $, #, >, %
        Equal(true, KittyWindowInput.IsActiveShellPrompt("user@host:~$ "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("root@host:~# "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("switch> "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("csh% "));

        // Without trailing whitespace
        Equal(true, KittyWindowInput.IsActiveShellPrompt("user@host:~$"));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("root@host:~#"));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("switch>"));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("csh%"));

        // 2. Custom shell prompt
        Equal(true, KittyWindowInput.IsActiveShellPrompt("my-custom-prompt: ", "my-custom-prompt:"));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("my-custom-prompt: ", "my-custom-prompt: "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("banner\r\napp-server> ", "app-server>"));

        // 3. Newlines mean line is completed (not waiting at active prompt)
        Equal(false, KittyWindowInput.IsActiveShellPrompt("user@host:~$ \r\n"));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("user@host:~$ \n"));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("user@host:~$ \r"));

        // 4. ANSI terminal escape sequences stripped
        Equal(true, KittyWindowInput.IsActiveShellPrompt("\x1B[0;32muser@host\x1B[0m:\x1B[0;34m~\x1B[0m$ "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("\x1B]0;title\x07user@host:~$ "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("\x1B]0;title\x1B\\user@host:~$ "));
        Equal(true, KittyWindowInput.IsActiveShellPrompt("\x1B]0;title\x9Cuser@host:~$ "));

        // 5. Negative and incomplete cases
        Equal(false, KittyWindowInput.IsActiveShellPrompt(""));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("   \r\n   "));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("Password: "));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("OTP: "));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("Connecting to remote host..."));
        Equal(false, KittyWindowInput.IsActiveShellPrompt("Last login: Wed Oct 22 from 10.0.0.1"));
    }

    private static void OptionalOtpWaitLifecycleTests()
    {
        var tempLog = Path.Combine(Path.GetTempPath(), $"selftest-opt-otp-{Guid.NewGuid():N}.log");
        try
        {
            string? deliveredOtp = null;
            Func<Process, string, CancellationToken, Task> mockSender = (_, val, _) =>
            {
                deliveredOtp = val;
                return Task.CompletedTask;
            };

            using var currentProcess = Process.GetCurrentProcess();

            // A. Shell completion without OTP: shell prompt appears without any OTP challenge
            File.WriteAllText(tempLog, "Connecting to jump...\r\njump$ ssh target\r\nuser@target:~$ ");
            var shellResult = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", "123456", TimeSpan.FromSeconds(1), CancellationToken.None,
                optional: true,
                sendLine: mockSender,
                completionCheck: text => KittyWindowInput.IsActiveShellPrompt(text))
                .GetAwaiter().GetResult();
            Equal(true, shellResult.Offset > 0);
            Equal(false, shellResult.Sent);
            Equal(null, deliveredOtp); // No OTP was sent because shell completed without OTP

            // B. Process exited early with optional: true returns minOffset without exception
            var psiExit = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                psiExit.ArgumentList.Add("/c");
                psiExit.ArgumentList.Add("exit 0");
            }
            else
            {
                psiExit.ArgumentList.Add("-c");
                psiExit.ArgumentList.Add("exit 0");
            }
            using (var exitedProc = Process.Start(psiExit)!)
            {
                exitedProc.WaitForExit(5000);
                Equal(true, exitedProc.HasExited);

                var exitResult = KittyWindowInput.SendLineAfterPromptAsync(
                    exitedProc, tempLog, "OTP", "123456", TimeSpan.FromSeconds(1), CancellationToken.None,
                    optional: true,
                    sendLine: mockSender,
                    minOffset: 42)
                    .GetAwaiter().GetResult();
                Equal(42, exitResult.Offset); // Returned minOffset without throwing
                Equal(false, exitResult.Sent);

                // Process exited with optional: false throws InvalidOperationException
                bool threwOnExit = false;
                try
                {
                    KittyWindowInput.SendLineAfterPromptAsync(
                        exitedProc, tempLog, "OTP", "123456", TimeSpan.FromSeconds(1), CancellationToken.None,
                        optional: false,
                        sendLine: mockSender)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Окно KiTTY закрылось"))
                {
                    threwOnExit = true;
                }
                Equal(true, threwOnExit);
            }

            // C. Timeout with optional: true returns minOffset without throwing
            File.WriteAllText(tempLog, "Still connecting...\r\n");
            var timeoutResult = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", "123456", TimeSpan.FromMilliseconds(60), CancellationToken.None,
                optional: true,
                sendLine: mockSender,
                minOffset: 15)
                .GetAwaiter().GetResult();
            Equal(15, timeoutResult.Offset);
            Equal(false, timeoutResult.Sent);

            // Timeout with optional: false throws TimeoutException
            bool threwOnTimeout = false;
            try
            {
                KittyWindowInput.SendLineAfterPromptAsync(
                    currentProcess, tempLog, "OTP", "123456", TimeSpan.FromMilliseconds(60), CancellationToken.None,
                    optional: false,
                    sendLine: mockSender)
                    .GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                threwOnTimeout = true;
            }
            Equal(true, threwOnTimeout);

            // D. OTP prompt actually appears with optional: true -> delivers OTP
            File.WriteAllText(tempLog, "Connecting...\r\nOTP: ");
            deliveredOtp = null;
            var deliveredResult = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", "654321", TimeSpan.FromSeconds(1), CancellationToken.None,
                optional: true,
                sendLine: mockSender)
                .GetAwaiter().GetResult();
            Equal("654321", deliveredOtp);
            Equal(true, deliveredResult.Offset > 0);
            Equal(true, deliveredResult.Sent);

            // E. JumpHost prompt transition: jump$ -> ssh app@internal -> OTP:
            // ProgressionCheck prevents premature completion on jump$ shell prompt.
            File.WriteAllText(tempLog, "Connected to JumpHost...\r\njump$ ");
            deliveredOtp = null;
            var pamTask = KittyWindowInput.SendLineAfterPromptAsync(
                currentProcess, tempLog, "OTP", "777888", TimeSpan.FromSeconds(2), CancellationToken.None,
                optional: true,
                sendLine: mockSender,
                progressionCheck: text => text.Contains("ssh app@internal", StringComparison.OrdinalIgnoreCase),
                completionCheck: text => KittyWindowInput.IsActiveShellPrompt(text));

            // Ensure wait does NOT complete immediately on jump$ prompt
            Task.Delay(50).GetAwaiter().GetResult();
            Equal(false, pamTask.IsCompleted);

            // Now PAM command is sent and OTP challenge appears
            File.AppendAllText(tempLog, "ssh app@internal\r\nPassword: pass\r\nOTP: ");
            var pamSeqResult = pamTask.GetAwaiter().GetResult();
            Equal("777888", deliveredOtp);
            Equal(true, pamSeqResult.Sent);
            Equal(true, pamSeqResult.Offset > 0);

            // F. Process exit during send exception handling when optional: true
            var psiLive = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                psiLive.ArgumentList.Add("/c");
                psiLive.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
            }
            else
            {
                psiLive.ArgumentList.Add("30");
            }
            using (var liveSenderProc = Process.Start(psiLive)!)
            {
                try
                {
                    File.WriteAllText(tempLog, "Ready for OTP...\r\nOTP: ");
                    bool senderInvoked = false;
                    var raceResult = KittyWindowInput.SendLineAfterPromptAsync(
                        liveSenderProc, tempLog, "OTP", "999000", TimeSpan.FromSeconds(2), CancellationToken.None,
                        optional: true,
                        sendLine: (p, v, ct) =>
                        {
                            senderInvoked = true;
                            try { p.Kill(); p.WaitForExit(); } catch { }
                            throw new InvalidOperationException("Окно KiTTY закрылось до ввода TOTP.");
                        },
                        minOffset: 12)
                        .GetAwaiter().GetResult();
                    Equal(true, senderInvoked);
                    Equal(12, raceResult.Offset);
                    Equal(false, raceResult.Sent);
                }
                finally
                {
                    try { if (!liveSenderProc.HasExited) { liveSenderProc.Kill(); liveSenderProc.WaitForExit(); } } catch { }
                }
            }
        }
        finally
        {
            try { File.Delete(tempLog); } catch { }
        }
    }

    private static void PamImportProtectionTests()
    {
        // 1. Local server has PAM configured, incoming server has empty/unset PAM
        var local = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "ProdTarget",
            Host = "10.0.0.10",
            Port = 22,
            Username = "ops",
            UsePam = true,
            PamSshCommand = "ssh -p 2222 app@internal",
            PamPassword = "local-pam-password",
            PamTotpSecret = "JBSWY3DPEHPK3PXP",
            PamTotpAlgorithm = "SHA256",
            PamTotpDigits = 8,
            PamTotpPeriodSeconds = 60,
            PamTotpPrompt = "Enter 2FA: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "Confirm (y/n): "
        };

        var emptyIncoming = new ManagedServer
        {
            Id = local.Id,
            Name = "ProdTarget",
            Host = "10.0.0.10",
            Port = 22,
            Username = "ops",
            UsePam = false,
            HasExplicitUsePam = false,
            PamSshCommand = "",
            PamPassword = "",
            PamTotpSecret = "",
            PamTotpAlgorithm = "",
            PamTotpDigits = 0,
            PamTotpPeriodSeconds = 0,
            PamTotpPrompt = "",
            PamAutoConfirm = false,
            HasExplicitPamAutoConfirm = false,
            PamConfirmPrompt = ""
        };

        var current = new ManagerConfig { UngroupedServers = [local] };
        var incoming = new ManagerConfig { UngroupedServers = [emptyIncoming] };

        // Part A: AnalyzeSmartImport marks all PAM fields with keepCurrent: true (UseIncoming == false)
        var plan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        var pamFields = plan.Fields.Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam").ToList();
        Equal(true, pamFields.Count > 0);
        foreach (var field in pamFields)
        {
            Equal(false, field.UseIncoming); // Default decision is to KEEP current PAM
        }

        // Part B: ApplyFullImportCascade preserves PAM fields when target had PAM configured
        ImportWizardEngine.ApplyFullImportCascade(plan, [emptyIncoming.Id]);
        foreach (var field in pamFields)
        {
            Equal(false, field.UseIncoming); // Cascade does NOT mark empty PAM values as UseIncoming
        }

        // Part C: ApplySelectedFields does not overwrite populated PAM fields with empty values
        var targetToModify = new ManagedServer();
        targetToModify.CopyPamSettingsFrom(local);
        foreach (var field in pamFields)
        {
            field.UseIncoming = true; // Simulate user forcing all fields to incoming
        }
        var selectedFields = plan.Fields.Where(f => f.UseIncoming).ToArray();
        ImportWizardEngine.ApplySelectedFields(targetToModify, emptyIncoming, selectedFields);
        Equal(true, targetToModify.UsePam);
        Equal("ssh -p 2222 app@internal", targetToModify.PamSshCommand);
        Equal("local-pam-password", targetToModify.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", targetToModify.PamTotpSecret);
        Equal("SHA256", targetToModify.PamTotpAlgorithm);
        Equal(8, targetToModify.PamTotpDigits);
        Equal(60, targetToModify.PamTotpPeriodSeconds);
        Equal("Enter 2FA: ", targetToModify.PamTotpPrompt);
        Equal(true, targetToModify.PamAutoConfirm);
        Equal("Confirm (y/n): ", targetToModify.PamConfirmPrompt);

        // Part D: Merge with Decision = UseIncoming preserves PAM from existingServer
        var useIncomingPlan = ConfigTransfer.AnalyzeSmartImport(current, incoming);
        useIncomingPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var merged = ConfigTransfer.MergeSmartImport(current, incoming, useIncomingPlan);
        var mergedServer = merged.UngroupedServers.Single();
        Equal(true, mergedServer.UsePam);
        Equal("ssh -p 2222 app@internal", mergedServer.PamSshCommand);
        Equal("local-pam-password", mergedServer.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", mergedServer.PamTotpSecret);
        Equal("SHA256", mergedServer.PamTotpAlgorithm);
        Equal(8, mergedServer.PamTotpDigits);
        Equal(60, mergedServer.PamTotpPeriodSeconds);
        Equal("Enter 2FA: ", mergedServer.PamTotpPrompt);
        Equal(true, mergedServer.PamAutoConfirm);
        Equal("Confirm (y/n): ", mergedServer.PamConfirmPrompt);

        // Part E: Legacy ConfigTransfer.Merge preserves PAM settings
        var legacyConflicts = ConfigTransfer.FindConflicts(current, incoming);
        var decisions = legacyConflicts.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
        var legacyMerged = ConfigTransfer.Merge(current, incoming, decisions);
        var legacyServer = legacyMerged.UngroupedServers.Single();
        Equal(true, legacyServer.UsePam);
        Equal("ssh -p 2222 app@internal", legacyServer.PamSshCommand);
        Equal("local-pam-password", legacyServer.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", legacyServer.PamTotpSecret);

        // 2. Incoming server has NEW non-empty PAM data -> correctly replaces
        var newPamIncoming = new ManagedServer
        {
            Id = local.Id,
            Name = "ProdTarget",
            Host = "10.0.0.10",
            Port = 22,
            Username = "ops",
            UsePam = true,
            PamSshCommand = "ssh -p 3333 newapp@internal",
            PamPassword = "new-pam-password",
            PamTotpSecret = "GEZDGNBVGY3TQOJQ",
            PamTotpAlgorithm = "SHA512",
            PamTotpDigits = 6,
            PamTotpPeriodSeconds = 30,
            PamTotpPrompt = "New OTP: ",
            PamAutoConfirm = false,
            PamConfirmPrompt = "[Y/n]"
        };
        var newIncomingConfig = new ManagerConfig { UngroupedServers = [newPamIncoming] };
        var newPlan = ConfigTransfer.AnalyzeSmartImport(current, newIncomingConfig);
        newPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var newMerged = ConfigTransfer.MergeSmartImport(current, newIncomingConfig, newPlan);
        var newServer = newMerged.UngroupedServers.Single();
        Equal(true, newServer.UsePam);
        Equal("ssh -p 3333 newapp@internal", newServer.PamSshCommand);
        Equal("new-pam-password", newServer.PamPassword);
        Equal("GEZDGNBVGY3TQOJQ", newServer.PamTotpSecret);
        Equal("SHA512", newServer.PamTotpAlgorithm);

        // 3. Partial incoming PAM (new command, empty password and secret)
        // New command must apply, but existing password and secret must NOT be overwritten by empty.
        var partialIncoming = new ManagedServer
        {
            Id = local.Id,
            Name = "PartialTarget",
            Host = "10.0.0.10",
            Port = 22,
            Username = "ops",
            UsePam = true,
            PamSshCommand = "ssh -p 4444 partialapp@internal",
            PamPassword = "",
            PamTotpSecret = ""
        };
        var partialConfig = new ManagerConfig { UngroupedServers = [partialIncoming] };

        // Test Smart Import (ImportWizardEngine.Merge) with partial PAM
        var partialPlan = ConfigTransfer.AnalyzeSmartImport(current, partialConfig);
        partialPlan.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var partialSmartMerged = ConfigTransfer.MergeSmartImport(current, partialConfig, partialPlan);
        var partialSmartServer = partialSmartMerged.UngroupedServers.Single();
        Equal(true, partialSmartServer.UsePam);
        Equal("ssh -p 4444 partialapp@internal", partialSmartServer.PamSshCommand);
        Equal("local-pam-password", partialSmartServer.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", partialSmartServer.PamTotpSecret);
        Equal("SHA1", partialSmartServer.PamTotpAlgorithm);

        // Test Legacy Merge with partial PAM
        var partialConflicts = ConfigTransfer.FindConflicts(current, partialConfig);
        var partialDecisions = partialConflicts.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
        var partialLegacyMerged = ConfigTransfer.Merge(current, partialConfig, partialDecisions);
        var partialLegacyServer = partialLegacyMerged.UngroupedServers.Single();
        Equal(true, partialLegacyServer.UsePam);
        Equal("ssh -p 4444 partialapp@internal", partialLegacyServer.PamSshCommand);
        Equal("local-pam-password", partialLegacyServer.PamPassword);
        Equal("JBSWY3DPEHPK3PXP", partialLegacyServer.PamTotpSecret);

        // 4. Immutability of incoming argument in ConfigTransfer.Merge
        // Ensure that incoming.UngroupedServers[0] still has empty password and secret
        Equal("", partialIncoming.PamPassword);
        Equal("", partialIncoming.PamTotpSecret);
        Equal("ssh -p 4444 partialapp@internal", partialIncoming.PamSshCommand);

        // Reusing partialConfig with another config must not be corrupted
        var anotherCurrent = new ManagerConfig
        {
            UngroupedServers = [
                new ManagedServer
                {
                    Id = local.Id,
                    Name = "OtherTarget",
                    Host = "10.0.0.10",
                    UsePam = true,
                    PamSshCommand = "ssh -p 5555 other@internal",
                    PamPassword = "other-password",
                    PamTotpSecret = "OTHERTOTPSECRET"
                }
            ]
        };
        var secondMerged = ConfigTransfer.Merge(anotherCurrent, partialConfig, partialDecisions);
        var secondServer = secondMerged.UngroupedServers.Single();
        Equal("ssh -p 4444 partialapp@internal", secondServer.PamSshCommand);
        Equal("other-password", secondServer.PamPassword);
        Equal("OTHERTOTPSECRET", secondServer.PamTotpSecret);
        Equal("", partialIncoming.PamPassword);
        Equal("", partialIncoming.PamTotpSecret);

        // 5. Reviewer Feedback 1: Empty incoming PAM + local server with empty TOTP secret
        // All 10 fields (custom algorithm, period, digits, prompts, auto-confirm) must be preserved from existing
        var localWithEmptyTotpSecret = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "LocalTargetNoTotpSecret",
            Host = "10.0.0.11",
            Port = 22,
            Username = "admin",
            UsePam = true,
            PamSshCommand = "ssh -p 2200 admin@internal",
            PamPassword = "admin-pam-password",
            PamTotpSecret = "", // Empty secret locally!
            PamTotpAlgorithm = "SHA256",
            PamTotpDigits = 8,
            PamTotpPeriodSeconds = 45,
            PamTotpPrompt = "Local Custom Prompt: ",
            PamAutoConfirm = true,
            PamConfirmPrompt = "Local Custom Confirm: "
        };
        var currentNoSecret = new ManagerConfig { UngroupedServers = [localWithEmptyTotpSecret] };
        var incomingUnconfigured = new ManagerConfig
        {
            UngroupedServers = [
                new ManagedServer
                {
                    Id = localWithEmptyTotpSecret.Id,
                    Name = localWithEmptyTotpSecret.Name,
                    Host = localWithEmptyTotpSecret.Host,
                    UsePam = false,
                    HasExplicitUsePam = false,
                    PamSshCommand = "",
                    PamPassword = "",
                    PamTotpSecret = "",
                    PamTotpAlgorithm = "",
                    PamTotpDigits = 0,
                    PamTotpPeriodSeconds = 0,
                    PamTotpPrompt = "",
                    PamAutoConfirm = false,
                    HasExplicitPamAutoConfirm = false,
                    PamConfirmPrompt = ""
                }
            ]
        };
        var planNoSecret = ConfigTransfer.AnalyzeSmartImport(currentNoSecret, incomingUnconfigured);
        planNoSecret.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var mergedNoSecret = ConfigTransfer.MergeSmartImport(currentNoSecret, incomingUnconfigured, planNoSecret);
        var serverNoSecret = mergedNoSecret.UngroupedServers.Single();
        Equal(true, serverNoSecret.UsePam);
        Equal("ssh -p 2200 admin@internal", serverNoSecret.PamSshCommand);
        Equal("admin-pam-password", serverNoSecret.PamPassword);
        Equal("", serverNoSecret.PamTotpSecret);
        Equal("SHA256", serverNoSecret.PamTotpAlgorithm);
        Equal(8, serverNoSecret.PamTotpDigits);
        Equal(45, serverNoSecret.PamTotpPeriodSeconds);
        Equal("Local Custom Prompt: ", serverNoSecret.PamTotpPrompt);
        Equal(true, serverNoSecret.PamAutoConfirm);
        Equal("Local Custom Confirm: ", serverNoSecret.PamConfirmPrompt);

        // 6. Reviewer Feedback 2: Partial incoming PAM with explicit SHA512 / PamAutoConfirm=false / custom settings
        // Incoming explicit algorithm, digits, period, prompts, and auto-confirm must NOT be overwritten by local
        var partialCustomIncoming = new ManagedServer
        {
            Id = local.Id,
            Name = "PartialCustomTarget",
            Host = "10.0.0.10",
            UsePam = true,
            PamSshCommand = "ssh -p 9999 custom@internal",
            PamPassword = "", // Empty: should take from local
            PamTotpSecret = "", // Empty: should take from local
            PamTotpAlgorithm = "SHA512", // Explicit incoming algorithm
            PamTotpDigits = 7, // Explicit incoming digits
            PamTotpPeriodSeconds = 45, // Explicit incoming period
            PamTotpPrompt = "Verification code: ", // Explicit incoming prompt
            PamAutoConfirm = false, // Explicit incoming auto-confirm=false
            PamConfirmPrompt = "Proceed? " // Explicit incoming confirm prompt
        };
        var incomingCustomConfig = new ManagerConfig { UngroupedServers = [partialCustomIncoming] };
        var planCustom = ConfigTransfer.AnalyzeSmartImport(current, incomingCustomConfig);
        planCustom.Sessions.Single().Decision = ImportDecision.UseIncoming;
        var mergedCustom = ConfigTransfer.MergeSmartImport(current, incomingCustomConfig, planCustom);
        var serverCustom = mergedCustom.UngroupedServers.Single();
        Equal(true, serverCustom.UsePam);
        Equal("ssh -p 9999 custom@internal", serverCustom.PamSshCommand);
        Equal("local-pam-password", serverCustom.PamPassword); // Preserved from local
        Equal("JBSWY3DPEHPK3PXP", serverCustom.PamTotpSecret); // Preserved from local
        Equal("SHA512", serverCustom.PamTotpAlgorithm); // Kept from incoming!
        Equal(7, serverCustom.PamTotpDigits); // Kept from incoming!
        Equal(45, serverCustom.PamTotpPeriodSeconds); // Kept from incoming!
        Equal("Verification code: ", serverCustom.PamTotpPrompt); // Kept from incoming!
        Equal(false, serverCustom.PamAutoConfirm); // Kept from incoming (not overwritten by true)!
        Equal("Proceed? ", serverCustom.PamConfirmPrompt); // Kept from incoming!

        // 7. ConfigStore.Load and Import scenarios
        // 7A: ConfigStore.Load with PamTotpDigits: 0, PamTotpPeriodSeconds: 0 normalizes to 6 and 30
        var loadFile = Path.Combine(Path.GetTempPath(), $"test-pam-load-{Guid.NewGuid():N}.json");
        try
        {
            var loadJson = """
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "00000000-0000-0000-0000-000000000001",
                  "Name": "LoadServer",
                  "Host": "10.0.0.1",
                  "UsePam": true,
                  "PamTotpSecret": "JBSWY3DPEHPK3PXP",
                  "PamTotpDigits": 0,
                  "PamTotpPeriodSeconds": 0
                }
              ]
            }
            """;
            File.WriteAllText(loadFile, loadJson);
            var loadedConfig = ConfigStore.Load(loadFile);
            var loadedServer = loadedConfig.UngroupedServers.Single();
            Equal(6, loadedServer.PamTotpDigits);
            Equal(30, loadedServer.PamTotpPeriodSeconds);
            Equal("SHA1", loadedServer.PamTotpAlgorithm);
            Equal("OTP: ", loadedServer.PamTotpPrompt);
            var code = TotpGenerator.Generate(loadedServer.PamTotpSecret, DateTimeOffset.UtcNow, loadedServer.PamTotpDigits, loadedServer.PamTotpPeriodSeconds, loadedServer.PamTotpAlgorithm);
            Equal(6, code.Length);
        }
        finally
        {
            try { File.Delete(loadFile); } catch { }
        }

        // 7B: ConfigStore.Import with TOTP properties omitted from JSON (only new secret present)
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-pam-import-{Guid.NewGuid():N}.json");
        try
        {
            var serverId = Guid.NewGuid();
            var jsonContent = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{serverId}}",
                  "Name": "E2ETarget",
                  "Host": "10.0.0.12",
                  "Port": 22,
                  "Username": "admin",
                  "UsePam": true,
                  "PamSshCommand": "ssh -p 2200 admin@internal",
                  "PamPassword": "",
                  "PamTotpSecret": "NEWTOTPSECRET123"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, jsonContent);
            var importedViaFile = ConfigStore.Import(tempFile);

            var localE2E = new ManagedServer
            {
                Id = serverId,
                Name = "E2ETarget",
                Host = "10.0.0.12",
                Port = 22,
                Username = "admin",
                UsePam = true,
                PamSshCommand = "ssh -p 2200 admin@internal",
                PamPassword = "local-admin-password",
                PamTotpSecret = "OLDSECRET",
                PamTotpAlgorithm = "SHA256",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 60,
                PamTotpPrompt = "Verification code: ",
                PamAutoConfirm = true,
                PamConfirmPrompt = "Local confirm: "
            };
            var currentE2E = new ManagerConfig { UngroupedServers = [localE2E] };

            // KeepCurrent decision
            var planE2E = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedViaFile);
            var secretField = planE2E.Fields.FirstOrDefault(f => f.PropertyName == "PamTotpSecret");
            if (secretField is not null) secretField.UseIncoming = true;
            var mergedKeep = ConfigTransfer.MergeSmartImport(currentE2E, importedViaFile, planE2E);
            var serverKeep = mergedKeep.UngroupedServers.Single();
            Equal("NEWTOTPSECRET123", serverKeep.PamTotpSecret);
            Equal("SHA256", serverKeep.PamTotpAlgorithm);
            Equal(8, serverKeep.PamTotpDigits);
            Equal(60, serverKeep.PamTotpPeriodSeconds);
            Equal("Verification code: ", serverKeep.PamTotpPrompt);

            // UseIncoming decision
            planE2E.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedIncoming = ConfigTransfer.MergeSmartImport(currentE2E, importedViaFile, planE2E);
            var serverIncoming = mergedIncoming.UngroupedServers.Single();
            Equal("NEWTOTPSECRET123", serverIncoming.PamTotpSecret);
            Equal("SHA256", serverIncoming.PamTotpAlgorithm);
            Equal(8, serverIncoming.PamTotpDigits);
            Equal(60, serverIncoming.PamTotpPeriodSeconds);
            Equal("Verification code: ", serverIncoming.PamTotpPrompt);

            // Legacy ConfigTransfer.Merge
            var e2eConflicts = ConfigTransfer.FindConflicts(currentE2E, importedViaFile);
            var e2eDecisions = e2eConflicts.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var mergedLegacy = ConfigTransfer.Merge(currentE2E, importedViaFile, e2eDecisions);
            var serverLegacy = mergedLegacy.UngroupedServers.Single();
            Equal("NEWTOTPSECRET123", serverLegacy.PamTotpSecret);
            Equal("SHA256", serverLegacy.PamTotpAlgorithm);
            Equal(8, serverLegacy.PamTotpDigits);
            Equal(60, serverLegacy.PamTotpPeriodSeconds);
            Equal("Verification code: ", serverLegacy.PamTotpPrompt);

            // 7C: JSON explicitly contains standard properties ("SHA1", 6, 30, "OTP: ")
            var explicitDefaultsJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{serverId}}",
                  "Name": "E2ETarget",
                  "Host": "10.0.0.12",
                  "Port": 22,
                  "Username": "admin",
                  "UsePam": true,
                  "PamSshCommand": "ssh -p 2200 admin@internal",
                  "PamPassword": "",
                  "PamTotpSecret": "NEWTOTPSECRET123",
                  "PamTotpAlgorithm": "SHA1",
                  "PamTotpDigits": 6,
                  "PamTotpPeriodSeconds": 30,
                  "PamTotpPrompt": "OTP: ",
                  "PamConfirmPrompt": "[Y/n]"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, explicitDefaultsJson);
            var importedDefaults = ConfigStore.Import(tempFile);
            var planDefaults = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedDefaults);
            planDefaults.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedDefaults = ConfigTransfer.MergeSmartImport(currentE2E, importedDefaults, planDefaults);
            var serverDefaults = mergedDefaults.UngroupedServers.Single();
            Equal("NEWTOTPSECRET123", serverDefaults.PamTotpSecret);
            Equal("SHA1", serverDefaults.PamTotpAlgorithm);
            Equal(6, serverDefaults.PamTotpDigits);
            Equal(30, serverDefaults.PamTotpPeriodSeconds);
            Equal("OTP: ", serverDefaults.PamTotpPrompt);

            // 7D: JSON explicitly contains custom properties ("SHA512", 7, 45, "Proceed: ")
            var explicitCustomJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{serverId}}",
                  "Name": "E2ETarget",
                  "Host": "10.0.0.12",
                  "Port": 22,
                  "Username": "admin",
                  "UsePam": true,
                  "PamSshCommand": "ssh -p 2200 admin@internal",
                  "PamPassword": "",
                  "PamTotpSecret": "EXPLICITSECRET",
                  "PamTotpAlgorithm": "SHA512",
                  "PamTotpDigits": 7,
                  "PamTotpPeriodSeconds": 45,
                  "PamTotpPrompt": "Proceed: ",
                  "PamConfirmPrompt": "Confirm: "
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, explicitCustomJson);
            var importedExplicit = ConfigStore.Import(tempFile);
            var planExplicit = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedExplicit);
            planExplicit.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedExplicit = ConfigTransfer.MergeSmartImport(currentE2E, importedExplicit, planExplicit);
            var serverExplicit = mergedExplicit.UngroupedServers.Single();
            Equal("EXPLICITSECRET", serverExplicit.PamTotpSecret);
            Equal("SHA512", serverExplicit.PamTotpAlgorithm);
            Equal(7, serverExplicit.PamTotpDigits);
            Equal(45, serverExplicit.PamTotpPeriodSeconds);
            Equal("Proceed: ", serverExplicit.PamTotpPrompt);

            // 7E: Import new session without Id with explicit SHA512 / 7 / 45 / "Proceed: "
            var noIdJson = """
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Name": "NoIdServer",
                  "Host": "10.0.0.13",
                  "Port": 22,
                  "Username": "admin",
                  "UsePam": true,
                  "PamSshCommand": "ssh -p 2200 admin@internal",
                  "PamPassword": "",
                  "PamTotpSecret": "NOIDSECRET",
                  "PamTotpAlgorithm": "SHA512",
                  "PamTotpDigits": 7,
                  "PamTotpPeriodSeconds": 45,
                  "PamTotpPrompt": "Proceed: ",
                  "PamConfirmPrompt": "Confirm: "
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, noIdJson);
            var importedNoId = ConfigStore.Import(tempFile);
            var planNoId = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedNoId);
            planNoId.Sessions.Single().Decision = ImportDecision.Add;
            var mergedNoId = ConfigTransfer.MergeSmartImport(currentE2E, importedNoId, planNoId);
            var addedServer = mergedNoId.UngroupedServers.Single(s => s.Name == "NoIdServer");
            Equal("NOIDSECRET", addedServer.PamTotpSecret);
            Equal("SHA512", addedServer.PamTotpAlgorithm);
            Equal(7, addedServer.PamTotpDigits);
            Equal(45, addedServer.PamTotpPeriodSeconds);
            Equal("Proceed: ", addedServer.PamTotpPrompt);

            // 7F: Import session inside a group where Id of group matches Id of server
            var duplicateId = Guid.NewGuid();
            var matchingIdJson = $$"""
            {
              "SchemaVersion": 9,
              "Groups": [
                {
                  "Id": "{{duplicateId}}",
                  "Name": "MatchingGroup",
                  "Servers": [
                    {
                      "Id": "{{duplicateId}}",
                      "Name": "GroupServer",
                      "Host": "10.0.0.14",
                      "Port": 22,
                      "Username": "admin",
                      "UsePam": true,
                      "PamSshCommand": "ssh -p 2200 admin@internal",
                      "PamPassword": "",
                      "PamTotpSecret": "GROUPSECRET",
                      "PamTotpAlgorithm": "SHA512",
                      "PamTotpDigits": 7,
                      "PamTotpPeriodSeconds": 45,
                      "PamTotpPrompt": "Proceed: ",
                      "PamConfirmPrompt": "Confirm: "
                    }
                  ]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, matchingIdJson);
            var importedMatching = ConfigStore.Import(tempFile);
            var groupServer = importedMatching.Groups.Single().Servers.Single();
            Equal("GROUPSECRET", groupServer.PamTotpSecret);
            Equal("SHA512", groupServer.PamTotpAlgorithm);
            Equal(7, groupServer.PamTotpDigits);
            Equal(45, groupServer.PamTotpPeriodSeconds);
            Equal("Proceed: ", groupServer.PamTotpPrompt);

            // 7G: Omitted boolean fields in JSON (UsePam, PamAutoConfirm) preserve local true, while explicit false is accepted
            var omittedBooleansJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{serverId}}",
                  "Name": "E2ETarget",
                  "Host": "10.0.0.12",
                  "Port": 22,
                  "Username": "admin",
                  "PamSshCommand": "ssh -p 2200 admin@internal-updated"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, omittedBooleansJson);
            var importedOmitted = ConfigStore.Import(tempFile);
            Equal(false, importedOmitted.UngroupedServers.Single().HasExplicitUsePam);
            Equal(false, importedOmitted.UngroupedServers.Single().HasExplicitPamAutoConfirm);

            var planOmitted = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedOmitted);
            // Omitted boolean fields are not offered as discrepancies
            Equal(false, planOmitted.Fields.Any(f => f.PropertyName == "UsePam"));
            Equal(false, planOmitted.Fields.Any(f => f.PropertyName == "PamAutoConfirm"));

            planOmitted.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedOmitted = ConfigTransfer.MergeSmartImport(currentE2E, importedOmitted, planOmitted);
            var serverOmitted = mergedOmitted.UngroupedServers.Single();
            // Local true values are preserved when omitted in incoming JSON
            Equal(true, serverOmitted.UsePam);
            Equal(true, serverOmitted.PamAutoConfirm);
            Equal("ssh -p 2200 admin@internal-updated", serverOmitted.PamSshCommand);

            var explicitFalseJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{serverId}}",
                  "Name": "E2ETarget",
                  "Host": "10.0.0.12",
                  "Port": 22,
                  "Username": "admin",
                  "UsePam": false,
                  "PamAutoConfirm": false,
                  "PamSshCommand": "ssh -p 2200 admin@internal-disabled"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, explicitFalseJson);
            var importedExplicitFalse = ConfigStore.Import(tempFile);
            Equal(true, importedExplicitFalse.UngroupedServers.Single().HasExplicitUsePam);
            Equal(true, importedExplicitFalse.UngroupedServers.Single().HasExplicitPamAutoConfirm);

            var planExplicitFalse = ConfigTransfer.AnalyzeSmartImport(currentE2E, importedExplicitFalse);
            Equal(true, planExplicitFalse.Fields.Any(f => f.PropertyName == "UsePam"));
            Equal(true, planExplicitFalse.Fields.Any(f => f.PropertyName == "PamAutoConfirm"));

            planExplicitFalse.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedExplicitFalse = ConfigTransfer.MergeSmartImport(currentE2E, importedExplicitFalse, planExplicitFalse);
            var serverExplicitFalse = mergedExplicitFalse.UngroupedServers.Single();
            Equal(false, serverExplicitFalse.UsePam);
            Equal(false, serverExplicitFalse.PamAutoConfirm);

            // 7H: Non-empty TOTP parameters applied in ApplySelectedFields even when source has no PAM command or secret
            var source7H = new ManagedServer
            {
                Name = "Source7H",
                UsePam = false,
                PamSshCommand = "",
                PamTotpSecret = "",
                PamTotpAlgorithm = "SHA256",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 45,
                PamTotpPrompt = "Token: ",
                PamConfirmPrompt = "Proceed? "
            };
            Equal(true, source7H.HasPamConfigured());

            var target7H = new ManagedServer
            {
                Name = "Target7H",
                UsePam = true,
                PamSshCommand = "ssh app@internal",
                PamTotpSecret = "ORIGINALSECRET",
                PamTotpAlgorithm = "SHA1",
                PamTotpDigits = 6,
                PamTotpPeriodSeconds = 30,
                PamTotpPrompt = "OTP: ",
                PamConfirmPrompt = "[Y/n]"
            };

            var selectedTotpFields = new List<ImportFieldDecision>
            {
                new() { PropertyName = "PamTotpAlgorithm", UseIncoming = true },
                new() { PropertyName = "PamTotpDigits", UseIncoming = true },
                new() { PropertyName = "PamTotpPeriodSeconds", UseIncoming = true },
                new() { PropertyName = "PamTotpPrompt", UseIncoming = true },
                new() { PropertyName = "PamConfirmPrompt", UseIncoming = true }
            };
            ImportWizardEngine.ApplySelectedFields(target7H, source7H, selectedTotpFields);
            Equal("SHA256", target7H.PamTotpAlgorithm);
            Equal(8, target7H.PamTotpDigits);
            Equal(45, target7H.PamTotpPeriodSeconds);
            Equal("Token: ", target7H.PamTotpPrompt);
            Equal("Proceed? ", target7H.PamConfirmPrompt);
            // Existing PAM command and secret must remain untouched
            Equal("ssh app@internal", target7H.PamSshCommand);
            Equal("ORIGINALSECRET", target7H.PamTotpSecret);

            // 7I: Case-insensitive container matching in ConfigStore.Import with "UNGROUPEDSERVERS", "GROUPS", "SERVERS"
            var uppercaseContainersJson = """
            {
              "SCHEMAVERSION": 9,
              "UNGROUPEDSERVERS": [
                {
                  "NAME": "UpperUngrouped",
                  "HOST": "10.0.0.20",
                  "PORT": 22,
                  "USEPAM": true,
                  "PAMSSHCOMMAND": "ssh upper@target"
                }
              ],
              "GROUPS": [
                {
                  "NAME": "UpperGroup",
                  "SERVERS": [
                    {
                      "NAME": "UpperGroupServer",
                      "HOST": "10.0.0.21",
                      "PORT": 22
                    }
                  ]
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, uppercaseContainersJson);
            var importedUpper = ConfigStore.Import(tempFile);
            Equal(1, importedUpper.UngroupedServers.Count);
            Equal("UpperUngrouped", importedUpper.UngroupedServers[0].Name);
            Equal(true, importedUpper.UngroupedServers[0].HasExplicitUsePam);
            Equal(true, importedUpper.UngroupedServers[0].UsePam);
            Equal(1, importedUpper.Groups.Count);
            Equal("UpperGroup", importedUpper.Groups[0].Name);
            Equal(1, importedUpper.Groups[0].Servers.Count);
            Equal("UpperGroupServer", importedUpper.Groups[0].Servers[0].Name);

            // 7J: Partial import -> merge -> boolean change -> export -> re-import round-trip
            var roundTripId = Guid.NewGuid();
            var initialJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{roundTripId}}",
                  "Name": "RoundTripServer",
                  "Host": "10.0.0.30",
                  "Port": 22,
                  "UsePam": true,
                  "PamSshCommand": "ssh round@trip",
                  "PamAutoConfirm": true
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, initialJson);
            var importedInitial = ConfigStore.Import(tempFile);
            var initialServer = importedInitial.UngroupedServers.Single();
            Equal(true, initialServer.HasExplicitUsePam);
            Equal(true, initialServer.HasExplicitPamAutoConfirm);

            // Merge into local config
            var emptyLocalConfig = new ManagerConfig();
            var planInitial = ConfigTransfer.AnalyzeSmartImport(emptyLocalConfig, importedInitial);
            planInitial.Sessions.Single().Decision = ImportDecision.Add;
            var mergedConfig = ConfigTransfer.MergeSmartImport(emptyLocalConfig, importedInitial, planInitial);
            var serverInConfig = mergedConfig.UngroupedServers.Single();
            Equal(true, serverInConfig.UsePam);
            Equal(true, serverInConfig.PamAutoConfirm);

            // Change boolean in config: disable PamAutoConfirm
            serverInConfig.PamAutoConfirm = false;

            // Export to file
            ConfigStore.Export(tempFile, mergedConfig);
            var exportedText = File.ReadAllText(tempFile);
            // Verify HasExplicitUsePam and HasExplicitPamAutoConfirm are NOT serialized
            Equal(false, exportedText.Contains("HasExplicitUsePam"));
            Equal(false, exportedText.Contains("HasExplicitPamAutoConfirm"));

            // Re-import exported file
            var reimportedConfig = ConfigStore.Import(tempFile);
            var reimportedServer = reimportedConfig.UngroupedServers.Single();
            Equal(true, reimportedServer.UsePam);
            Equal(false, reimportedServer.PamAutoConfirm);
            // After re-import, explicit presence in JSON must restore HasExplicit to true
            Equal(true, reimportedServer.HasExplicitUsePam);
            Equal(true, reimportedServer.HasExplicitPamAutoConfirm);

            // Verify re-imported server in smart import against another config
            var targetConfig = new ManagerConfig
            {
                UngroupedServers = [new ManagedServer { Id = roundTripId, Name = "RoundTripServer", UsePam = true, PamAutoConfirm = true }]
            };
            var planReimported = ConfigTransfer.AnalyzeSmartImport(targetConfig, reimportedConfig);
            // PamAutoConfirm difference is detected because re-imported server has HasExplicitPamAutoConfirm == true
            Equal(true, planReimported.Fields.Any(f => f.PropertyName == "PamAutoConfirm"));

            planReimported.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var finalMerged = ConfigTransfer.MergeSmartImport(targetConfig, reimportedConfig, planReimported);
            Equal(false, finalMerged.UngroupedServers.Single().PamAutoConfirm);

            // Legacy Merge also accepts explicit PamAutoConfirm == false
            var legacyConflicts7J = ConfigTransfer.FindConflicts(targetConfig, reimportedConfig);
            var legacyDecisions7J = legacyConflicts7J.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var legacyMerged7J = ConfigTransfer.Merge(targetConfig, reimportedConfig, legacyDecisions7J);
            Equal(false, legacyMerged7J.UngroupedServers.Single().PamAutoConfirm);

            // 7K: JSON with only standard TOTP parameters or only PamAutoConfirm = true over local non-standard parameters
            var stdTotpServerId = Guid.NewGuid();
            var localNonStandard = new ManagedServer
            {
                Id = stdTotpServerId,
                Name = "StdTotpTarget",
                Host = "10.0.0.40",
                Port = 22,
                UsePam = true,
                PamSshCommand = "ssh std@target",
                PamPassword = "std-password",
                PamTotpSecret = "NONSTANDSECRET",
                PamTotpAlgorithm = "SHA256",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 60,
                PamTotpPrompt = "Local OTP: ",
                PamAutoConfirm = false,
                PamConfirmPrompt = "Local confirm: "
            };
            var localConfig7K = new ManagerConfig { UngroupedServers = [localNonStandard] };

            // Case A: Incoming JSON has ONLY standard TOTP parameters (no SSH command, no secret, no password)
            var stdTotpOnlyJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{stdTotpServerId}}",
                  "Name": "StdTotpTarget",
                  "Host": "10.0.0.40",
                  "Port": 22,
                  "PamTotpAlgorithm": "SHA1",
                  "PamTotpDigits": 6,
                  "PamTotpPeriodSeconds": 30,
                  "PamTotpPrompt": "OTP: ",
                  "PamConfirmPrompt": "[Y/n]"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, stdTotpOnlyJson);
            var importedStdTotp = ConfigStore.Import(tempFile);
            var incomingStdServer = importedStdTotp.UngroupedServers.Single();
            Equal(false, incomingStdServer.HasExplicitUsePam);
            Equal(true, incomingStdServer.HasAnyPamFieldPresent());

            // Field-by-field test: ApplySelectedFields applies standard parameters without skipUnsetPam blocking
            var targetForStdFields = new ManagedServer();
            targetForStdFields.CopyPamSettingsFrom(localNonStandard);
            var selectedStdFields = new List<ImportFieldDecision>
            {
                new() { PropertyName = "PamTotpAlgorithm", UseIncoming = true },
                new() { PropertyName = "PamTotpDigits", UseIncoming = true },
                new() { PropertyName = "PamTotpPeriodSeconds", UseIncoming = true },
                new() { PropertyName = "PamTotpPrompt", UseIncoming = true },
                new() { PropertyName = "PamConfirmPrompt", UseIncoming = true }
            };
            ImportWizardEngine.ApplySelectedFields(targetForStdFields, incomingStdServer, selectedStdFields);
            Equal("SHA1", targetForStdFields.PamTotpAlgorithm);
            Equal(6, targetForStdFields.PamTotpDigits);
            Equal(30, targetForStdFields.PamTotpPeriodSeconds);
            Equal("OTP: ", targetForStdFields.PamTotpPrompt);
            Equal("[Y/n]", targetForStdFields.PamConfirmPrompt);
            // Local populated fields preserved
            Equal("ssh std@target", targetForStdFields.PamSshCommand);
            Equal("std-password", targetForStdFields.PamPassword);
            Equal("NONSTANDSECRET", targetForStdFields.PamTotpSecret);

            // Smart Import test with UseIncoming
            var planStdTotp = ConfigTransfer.AnalyzeSmartImport(localConfig7K, importedStdTotp);
            planStdTotp.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedStdTotp = ConfigTransfer.MergeSmartImport(localConfig7K, importedStdTotp, planStdTotp);
            var mergedStdServer = mergedStdTotp.UngroupedServers.Single();
            Equal("SHA1", mergedStdServer.PamTotpAlgorithm);
            Equal(6, mergedStdServer.PamTotpDigits);
            Equal(30, mergedStdServer.PamTotpPeriodSeconds);
            Equal("OTP: ", mergedStdServer.PamTotpPrompt);
            Equal("[Y/n]", mergedStdServer.PamConfirmPrompt);
            Equal(true, mergedStdServer.UsePam);
            Equal("ssh std@target", mergedStdServer.PamSshCommand);
            Equal("std-password", mergedStdServer.PamPassword);
            Equal("NONSTANDSECRET", mergedStdServer.PamTotpSecret);

            // Legacy Merge test
            var legacyConflictsStd = ConfigTransfer.FindConflicts(localConfig7K, importedStdTotp);
            var legacyDecisionsStd = legacyConflictsStd.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var legacyMergedStd = ConfigTransfer.Merge(localConfig7K, importedStdTotp, legacyDecisionsStd);
            var legacyStdServer = legacyMergedStd.UngroupedServers.Single();
            Equal("SHA1", legacyStdServer.PamTotpAlgorithm);
            Equal(6, legacyStdServer.PamTotpDigits);
            Equal(30, legacyStdServer.PamTotpPeriodSeconds);
            Equal("OTP: ", legacyStdServer.PamTotpPrompt);
            Equal("[Y/n]", legacyStdServer.PamConfirmPrompt);
            Equal(true, legacyStdServer.UsePam);
            Equal("ssh std@target", legacyStdServer.PamSshCommand);
            Equal("std-password", legacyStdServer.PamPassword);
            Equal("NONSTANDSECRET", legacyStdServer.PamTotpSecret);

            // Case B: Incoming JSON has ONLY PamAutoConfirm: false over local session with PamAutoConfirm = true
            localNonStandard.PamAutoConfirm = true;
            var autoConfirmOnlyJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{stdTotpServerId}}",
                  "Name": "StdTotpTarget",
                  "Host": "10.0.0.40",
                  "Port": 22,
                  "PamAutoConfirm": false
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, autoConfirmOnlyJson);
            var importedAutoConfirm = ConfigStore.Import(tempFile);
            var incomingAutoServer = importedAutoConfirm.UngroupedServers.Single();
            Equal(false, incomingAutoServer.HasExplicitUsePam);
            Equal(true, incomingAutoServer.HasExplicitPamAutoConfirm);
            Equal(true, incomingAutoServer.HasAnyPamFieldPresent());

            var planAuto = ConfigTransfer.AnalyzeSmartImport(localConfig7K, importedAutoConfirm);
            planAuto.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedAuto = ConfigTransfer.MergeSmartImport(localConfig7K, importedAutoConfirm, planAuto);
            var mergedAutoServer = mergedAuto.UngroupedServers.Single();
            Equal(false, mergedAutoServer.PamAutoConfirm);
            Equal(true, mergedAutoServer.UsePam);
            Equal("SHA256", mergedAutoServer.PamTotpAlgorithm);
            Equal(8, mergedAutoServer.PamTotpDigits);
            Equal(60, mergedAutoServer.PamTotpPeriodSeconds);
            Equal("ssh std@target", mergedAutoServer.PamSshCommand);
            Equal("NONSTANDSECRET", mergedAutoServer.PamTotpSecret);

            // Legacy Merge with PamAutoConfirm only
            var legacyConflictsAuto = ConfigTransfer.FindConflicts(localConfig7K, importedAutoConfirm);
            var legacyDecisionsAuto = legacyConflictsAuto.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var legacyMergedAuto = ConfigTransfer.Merge(localConfig7K, importedAutoConfirm, legacyDecisionsAuto);
            var legacyAutoServer = legacyMergedAuto.UngroupedServers.Single();
            Equal(false, legacyAutoServer.PamAutoConfirm);
            Equal(true, legacyAutoServer.UsePam);
            Equal("SHA256", legacyAutoServer.PamTotpAlgorithm);
            Equal(8, legacyAutoServer.PamTotpDigits);
            Equal(60, legacyAutoServer.PamTotpPeriodSeconds);
            Equal("ssh std@target", legacyAutoServer.PamSshCommand);
            Equal("NONSTANDSECRET", legacyAutoServer.PamTotpSecret);

            // Case C: Incoming JSON has UsePam: false, PamAutoConfirm: false, and standard TOTP parameters, with command/password/secret omitted
            var partialPamJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{stdTotpServerId}}",
                  "Name": "StdTotpTarget",
                  "Host": "10.0.0.40",
                  "Port": 22,
                  "UsePam": false,
                  "PamAutoConfirm": false,
                  "PamTotpAlgorithm": "SHA1",
                  "PamTotpDigits": 6,
                  "PamTotpPeriodSeconds": 30,
                  "PamTotpPrompt": "OTP: ",
                  "PamConfirmPrompt": "[Y/n]"
                }
              ]
            }
            """;
            File.WriteAllText(tempFile, partialPamJson);
            var importedPartial = ConfigStore.Import(tempFile);
            var incomingPartialServer = importedPartial.UngroupedServers.Single();
            Equal(false, incomingPartialServer.HasAllPamPropertiesPresentInImport);
            Equal(true, incomingPartialServer.HasExplicitUsePam);
            Equal(true, incomingPartialServer.HasExplicitPamAutoConfirm);
            Equal(true, incomingPartialServer.HasAnyPamFieldPresent());
            Equal(false, incomingPartialServer.IsUnconfiguredPam());
            Equal(true, incomingPartialServer.HasPamConfigured());

            // Smart Import with UseIncoming
            var planPartial = ConfigTransfer.AnalyzeSmartImport(localConfig7K, importedPartial);
            planPartial.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedPartial = ConfigTransfer.MergeSmartImport(localConfig7K, importedPartial, planPartial);
            var sPartial = mergedPartial.UngroupedServers.Single();
            Equal(false, sPartial.UsePam);
            Equal(false, sPartial.PamAutoConfirm);
            Equal("SHA1", sPartial.PamTotpAlgorithm);
            Equal(6, sPartial.PamTotpDigits);
            Equal(30, sPartial.PamTotpPeriodSeconds);
            Equal("OTP: ", sPartial.PamTotpPrompt);
            Equal("[Y/n]", sPartial.PamConfirmPrompt);
            Equal("ssh std@target", sPartial.PamSshCommand);
            Equal("std-password", sPartial.PamPassword);
            Equal("NONSTANDSECRET", sPartial.PamTotpSecret);

            // Legacy Merge
            var legacyConflictsPartial = ConfigTransfer.FindConflicts(localConfig7K, importedPartial);
            var legacyDecisionsPartial = legacyConflictsPartial.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var legacyMergedPartial = ConfigTransfer.Merge(localConfig7K, importedPartial, legacyDecisionsPartial);
            var sLegacyPartial = legacyMergedPartial.UngroupedServers.Single();
            Equal(false, sLegacyPartial.UsePam);
            Equal(false, sLegacyPartial.PamAutoConfirm);
            Equal("SHA1", sLegacyPartial.PamTotpAlgorithm);
            Equal(6, sLegacyPartial.PamTotpDigits);
            Equal(30, sLegacyPartial.PamTotpPeriodSeconds);
            Equal("OTP: ", sLegacyPartial.PamTotpPrompt);
            Equal("[Y/n]", sLegacyPartial.PamConfirmPrompt);
            Equal("ssh std@target", sLegacyPartial.PamSshCommand);
            Equal("std-password", sLegacyPartial.PamPassword);
            Equal("NONSTANDSECRET", sLegacyPartial.PamTotpSecret);


            // 7L: Ordinary export of unconfigured PAM session must not overwrite local PAM settings on import
            var unconfiguredServerId = Guid.NewGuid();
            var localWithFullPam = new ManagedServer
            {
                Id = unconfiguredServerId,
                Name = "LocalPamServer",
                Host = "10.0.0.100",
                Port = 22,
                UsePam = true,
                PamSshCommand = "ssh -p 2200 admin@internal",
                PamPassword = "local-pam-password",
                PamTotpSecret = "MFRGGZDFMZTWQ2LK",
                PamTotpAlgorithm = "SHA256",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 60,
                PamTotpPrompt = "Local OTP: ",
                PamAutoConfirm = true,
                PamConfirmPrompt = "Local confirm [y/n]: "
            };
            var localConfig7L = new ManagerConfig { UngroupedServers = [localWithFullPam] };

            // Unconfigured server exported via standard export pipeline (CreateExport -> Export)
            var unconfiguredSource = new ManagedServer
            {
                Id = unconfiguredServerId,
                Name = "LocalPamServer",
                Host = "10.0.0.100",
                Port = 22
            };
            var sourceConfig7L = new ManagerConfig { UngroupedServers = [unconfiguredSource] };

            // Direct in-memory merge test without file export/import
            Equal(true, unconfiguredSource.IsUnconfiguredPam());
            Equal(false, unconfiguredSource.HasPamConfigured());

            // 1. Direct Smart Import with UseIncoming
            var planDirect = ConfigTransfer.AnalyzeSmartImport(localConfig7L, sourceConfig7L);
            planDirect.Sessions.Single().Decision = ImportDecision.UseIncoming;
            var mergedDirect = ConfigTransfer.MergeSmartImport(localConfig7L, sourceConfig7L, planDirect);
            var sDirect = mergedDirect.UngroupedServers.Single();
            Equal(true, sDirect.UsePam);
            Equal("ssh -p 2200 admin@internal", sDirect.PamSshCommand);
            Equal("local-pam-password", sDirect.PamPassword);
            Equal("MFRGGZDFMZTWQ2LK", sDirect.PamTotpSecret);
            Equal("SHA256", sDirect.PamTotpAlgorithm);
            Equal(8, sDirect.PamTotpDigits);
            Equal(60, sDirect.PamTotpPeriodSeconds);
            Equal("Local OTP: ", sDirect.PamTotpPrompt);
            Equal(true, sDirect.PamAutoConfirm);
            Equal("Local confirm [y/n]: ", sDirect.PamConfirmPrompt);

            // 2. Direct Legacy Merge
            var legacyConflictsDirect = ConfigTransfer.FindConflicts(localConfig7L, sourceConfig7L);
            var legacyDecisionsDirect = legacyConflictsDirect.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
            var legacyMergedDirect = ConfigTransfer.Merge(localConfig7L, sourceConfig7L, legacyDecisionsDirect);
            var sLegacyDirect = legacyMergedDirect.UngroupedServers.Single();
            Equal(true, sLegacyDirect.UsePam);
            Equal("ssh -p 2200 admin@internal", sLegacyDirect.PamSshCommand);
            Equal("local-pam-password", sLegacyDirect.PamPassword);
            Equal("MFRGGZDFMZTWQ2LK", sLegacyDirect.PamTotpSecret);
            Equal("SHA256", sLegacyDirect.PamTotpAlgorithm);
            Equal(8, sLegacyDirect.PamTotpDigits);
            Equal(60, sLegacyDirect.PamTotpPeriodSeconds);
            Equal("Local OTP: ", sLegacyDirect.PamTotpPrompt);
            Equal(true, sLegacyDirect.PamAutoConfirm);
            Equal("Local confirm [y/n]: ", sLegacyDirect.PamConfirmPrompt);

            var exportConfig7L = ConfigTransfer.CreateExport(sourceConfig7L, [unconfiguredServerId], includeEntryPoints: false);

            var tempFile7L = Path.Combine(Path.GetTempPath(), $"pam-export-7l-{Guid.NewGuid():N}.json");
            try
            {
                ConfigStore.Export(tempFile7L, exportConfig7L);
                var importedConfig7L = ConfigStore.Import(tempFile7L);
                var importedUnconfServer = importedConfig7L.UngroupedServers.Single();

                Equal(true, importedUnconfServer.IsUnconfiguredPam());
                Equal(false, importedUnconfServer.HasPamConfigured());

                // Mode 1: Smart Import with UseIncoming
                var planUseIncoming = ConfigTransfer.AnalyzeSmartImport(localConfig7L, importedConfig7L);
                planUseIncoming.Sessions.Single().Decision = ImportDecision.UseIncoming;
                var mergedUseIncoming = ConfigTransfer.MergeSmartImport(localConfig7L, importedConfig7L, planUseIncoming);
                var sA = mergedUseIncoming.UngroupedServers.Single();
                Equal(true, sA.UsePam);
                Equal("ssh -p 2200 admin@internal", sA.PamSshCommand);
                Equal("local-pam-password", sA.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sA.PamTotpSecret);
                Equal("SHA256", sA.PamTotpAlgorithm);
                Equal(8, sA.PamTotpDigits);
                Equal(60, sA.PamTotpPeriodSeconds);
                Equal("Local OTP: ", sA.PamTotpPrompt);
                Equal(true, sA.PamAutoConfirm);
                Equal("Local confirm [y/n]: ", sA.PamConfirmPrompt);

                // Mode 2: Field-by-field Smart Import (including cascade)
                var planFieldByField = ConfigTransfer.AnalyzeSmartImport(localConfig7L, importedConfig7L);
                // Verify AnalyzeFields marked all PAM fields with keepCurrent (UseIncoming == false)
                var pamFields7L = planFieldByField.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7L.All(f => !f.UseIncoming));

                // Even if ApplyFullImportCascade is executed, unconfigured incoming session does not override local PAM
                ImportWizardEngine.ApplyFullImportCascade(planFieldByField, [unconfiguredServerId]);
                pamFields7L = planFieldByField.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7L.All(f => !f.UseIncoming));

                var mergedFieldByField = ConfigTransfer.MergeSmartImport(localConfig7L, importedConfig7L, planFieldByField);
                var sB = mergedFieldByField.UngroupedServers.Single();
                Equal(true, sB.UsePam);
                Equal("ssh -p 2200 admin@internal", sB.PamSshCommand);
                Equal("local-pam-password", sB.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sB.PamTotpSecret);
                Equal("SHA256", sB.PamTotpAlgorithm);
                Equal(8, sB.PamTotpDigits);
                Equal(60, sB.PamTotpPeriodSeconds);
                Equal("Local OTP: ", sB.PamTotpPrompt);
                Equal(true, sB.PamAutoConfirm);
                Equal("Local confirm [y/n]: ", sB.PamConfirmPrompt);

                // Mode 3: Legacy ConfigTransfer.Merge
                var legacyConflicts7L = ConfigTransfer.FindConflicts(localConfig7L, importedConfig7L);
                var legacyDecisions7L = legacyConflicts7L.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
                var legacyMerged7L = ConfigTransfer.Merge(localConfig7L, importedConfig7L, legacyDecisions7L);
                var sC = legacyMerged7L.UngroupedServers.Single();
                Equal(true, sC.UsePam);
                Equal("ssh -p 2200 admin@internal", sC.PamSshCommand);
                Equal("local-pam-password", sC.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sC.PamTotpSecret);
                Equal("SHA256", sC.PamTotpAlgorithm);
                Equal(8, sC.PamTotpDigits);
                Equal(60, sC.PamTotpPeriodSeconds);
                Equal("Local OTP: ", sC.PamTotpPrompt);
                Equal(true, sC.PamAutoConfirm);
                Equal("Local confirm [y/n]: ", sC.PamConfirmPrompt);
            }
            finally
            {
                try { File.Delete(tempFile7L); } catch { }
            }

            // 7M: Full empty PAM block with ""/0 in JSON must not overwrite local configured PAM settings
            var emptyBlockServerId = Guid.NewGuid();
            var localWithPam7M = new ManagedServer
            {
                Id = emptyBlockServerId,
                Name = "LocalTarget7M",
                Host = "10.0.0.150",
                Port = 22,
                UsePam = true,
                PamSshCommand = "ssh -p 2222 admin@nested.internal",
                PamPassword = "secure-password-7m",
                PamTotpSecret = "MFRGGZDFMZTWQ2LK",
                PamTotpAlgorithm = "SHA512",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 45,
                PamTotpPrompt = "Enter OTP 7M: ",
                PamAutoConfirm = true,
                PamConfirmPrompt = "Proceed? [y/n]: "
            };
            var localConfig7M = new ManagerConfig { UngroupedServers = [localWithPam7M] };

            var fullEmptyPamJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{emptyBlockServerId}}",
                  "Name": "LocalTarget7M",
                  "Host": "10.0.0.150",
                  "Port": 22,
                  "UsePam": false,
                  "PamAutoConfirm": false,
                  "PamSshCommand": "",
                  "PamPassword": "",
                  "PamTotpSecret": "",
                  "PamTotpAlgorithm": "",
                  "PamTotpDigits": 0,
                  "PamTotpPeriodSeconds": 0,
                  "PamTotpPrompt": "",
                  "PamConfirmPrompt": ""
                }
              ]
            }
            """;
            var tempFile7M = Path.Combine(Path.GetTempPath(), $"pam-empty-block-7m-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempFile7M, fullEmptyPamJson);
                var importedEmptyConfig = ConfigStore.Import(tempFile7M);
                var importedEmptyServer = importedEmptyConfig.UngroupedServers.Single();

                Equal(false, importedEmptyServer.HasExplicitUsePam);
                Equal(false, importedEmptyServer.HasExplicitPamAutoConfirm);
                Equal(false, importedEmptyServer.HasAnyPamFieldPresent());
                Equal(true, importedEmptyServer.IsUnconfiguredPam());
                Equal(false, importedEmptyServer.HasPamConfigured());

                // Mode 1: Smart Import with UseIncoming
                var plan7M = ConfigTransfer.AnalyzeSmartImport(localConfig7M, importedEmptyConfig);
                plan7M.Sessions.Single().Decision = ImportDecision.UseIncoming;
                var merged7M = ConfigTransfer.MergeSmartImport(localConfig7M, importedEmptyConfig, plan7M);
                var sM1 = merged7M.UngroupedServers.Single();
                Equal(true, sM1.UsePam);
                Equal("ssh -p 2222 admin@nested.internal", sM1.PamSshCommand);
                Equal("secure-password-7m", sM1.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sM1.PamTotpSecret);
                Equal("SHA512", sM1.PamTotpAlgorithm);
                Equal(8, sM1.PamTotpDigits);
                Equal(45, sM1.PamTotpPeriodSeconds);
                Equal("Enter OTP 7M: ", sM1.PamTotpPrompt);
                Equal(true, sM1.PamAutoConfirm);
                Equal("Proceed? [y/n]: ", sM1.PamConfirmPrompt);

                // Mode 2: Field-by-field Smart Import (including cascade)
                var planField7M = ConfigTransfer.AnalyzeSmartImport(localConfig7M, importedEmptyConfig);
                var pamFields7M = planField7M.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7M.All(f => !f.UseIncoming));

                ImportWizardEngine.ApplyFullImportCascade(planField7M, [emptyBlockServerId]);
                pamFields7M = planField7M.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7M.All(f => !f.UseIncoming));

                var mergedField7M = ConfigTransfer.MergeSmartImport(localConfig7M, importedEmptyConfig, planField7M);
                var sM2 = mergedField7M.UngroupedServers.Single();
                Equal(true, sM2.UsePam);
                Equal("ssh -p 2222 admin@nested.internal", sM2.PamSshCommand);
                Equal("secure-password-7m", sM2.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sM2.PamTotpSecret);
                Equal("SHA512", sM2.PamTotpAlgorithm);
                Equal(8, sM2.PamTotpDigits);
                Equal(45, sM2.PamTotpPeriodSeconds);
                Equal("Enter OTP 7M: ", sM2.PamTotpPrompt);
                Equal(true, sM2.PamAutoConfirm);
                Equal("Proceed? [y/n]: ", sM2.PamConfirmPrompt);

                // Mode 3: Legacy ConfigTransfer.Merge
                var conflicts7M = ConfigTransfer.FindConflicts(localConfig7M, importedEmptyConfig);
                var decisions7M = conflicts7M.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
                var legacyMerged7M = ConfigTransfer.Merge(localConfig7M, importedEmptyConfig, decisions7M);
                var sM3 = legacyMerged7M.UngroupedServers.Single();
                Equal(true, sM3.UsePam);
                Equal("ssh -p 2222 admin@nested.internal", sM3.PamSshCommand);
                Equal("secure-password-7m", sM3.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sM3.PamTotpSecret);
                Equal("SHA512", sM3.PamTotpAlgorithm);
                Equal(8, sM3.PamTotpDigits);
                Equal(45, sM3.PamTotpPeriodSeconds);
                Equal("Enter OTP 7M: ", sM3.PamTotpPrompt);
                Equal(true, sM3.PamAutoConfirm);
                Equal("Proceed? [y/n]: ", sM3.PamConfirmPrompt);
            }
            finally
            {
                try { File.Delete(tempFile7M); } catch { }
            }

            // 7N: Mixed empty/default unconfigured PAM block in JSON must not overwrite local configured PAM settings
            var mixedBlockServerId = Guid.NewGuid();
            var localWithPam7N = new ManagedServer
            {
                Id = mixedBlockServerId,
                Name = "LocalTarget7N",
                Host = "10.0.0.160",
                Port = 22,
                UsePam = true,
                PamSshCommand = "ssh -p 2233 admin@nested2.internal",
                PamPassword = "secure-password-7n",
                PamTotpSecret = "MFRGGZDFMZTWQ2LK",
                PamTotpAlgorithm = "SHA512",
                PamTotpDigits = 8,
                PamTotpPeriodSeconds = 45,
                PamTotpPrompt = "Enter OTP 7N: ",
                PamAutoConfirm = true,
                PamConfirmPrompt = "Proceed 7N? [y/n]: "
            };
            var localConfig7N = new ManagerConfig { UngroupedServers = [localWithPam7N] };

            // All 10 PAM properties present, but mixed: PamTotpPrompt is empty string "",
            // while algorithm/digits/period/confirm are standard defaults (SHA1, 6, 30, "[Y/n]")
            var mixedEmptyDefaultPamJson = $$"""
            {
              "SchemaVersion": 9,
              "UngroupedServers": [
                {
                  "Id": "{{mixedBlockServerId}}",
                  "Name": "LocalTarget7N",
                  "Host": "10.0.0.160",
                  "Port": 22,
                  "UsePam": false,
                  "PamAutoConfirm": false,
                  "PamSshCommand": "",
                  "PamPassword": "",
                  "PamTotpSecret": "",
                  "PamTotpAlgorithm": "SHA1",
                  "PamTotpDigits": 6,
                  "PamTotpPeriodSeconds": 30,
                  "PamTotpPrompt": "",
                  "PamConfirmPrompt": "[Y/n]"
                }
              ]
            }
            """;
            var tempFile7N = Path.Combine(Path.GetTempPath(), $"pam-mixed-block-7n-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempFile7N, mixedEmptyDefaultPamJson);
                var importedMixedConfig = ConfigStore.Import(tempFile7N);
                var importedMixedServer = importedMixedConfig.UngroupedServers.Single();

                Equal(false, importedMixedServer.HasExplicitUsePam);
                Equal(false, importedMixedServer.HasExplicitPamAutoConfirm);
                Equal(false, importedMixedServer.HasAnyPamFieldPresent());
                Equal(true, importedMixedServer.IsUnconfiguredPam());
                Equal(false, importedMixedServer.HasPamConfigured());

                // Mode 1: Smart Import with UseIncoming
                var plan7N = ConfigTransfer.AnalyzeSmartImport(localConfig7N, importedMixedConfig);
                plan7N.Sessions.Single().Decision = ImportDecision.UseIncoming;
                var merged7N = ConfigTransfer.MergeSmartImport(localConfig7N, importedMixedConfig, plan7N);
                var sN1 = merged7N.UngroupedServers.Single();
                Equal(true, sN1.UsePam);
                Equal("ssh -p 2233 admin@nested2.internal", sN1.PamSshCommand);
                Equal("secure-password-7n", sN1.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sN1.PamTotpSecret);
                Equal("SHA512", sN1.PamTotpAlgorithm);
                Equal(8, sN1.PamTotpDigits);
                Equal(45, sN1.PamTotpPeriodSeconds);
                Equal("Enter OTP 7N: ", sN1.PamTotpPrompt);
                Equal(true, sN1.PamAutoConfirm);
                Equal("Proceed 7N? [y/n]: ", sN1.PamConfirmPrompt);

                // Mode 2: Field-by-field Smart Import (including cascade)
                var planField7N = ConfigTransfer.AnalyzeSmartImport(localConfig7N, importedMixedConfig);
                var pamFields7N = planField7N.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7N.All(f => !f.UseIncoming));

                ImportWizardEngine.ApplyFullImportCascade(planField7N, [mixedBlockServerId]);
                pamFields7N = planField7N.Fields
                    .Where(f => f.PropertyName.StartsWith("Pam", StringComparison.Ordinal) || f.PropertyName == "UsePam")
                    .ToList();
                Equal(true, pamFields7N.All(f => !f.UseIncoming));

                var mergedField7N = ConfigTransfer.MergeSmartImport(localConfig7N, importedMixedConfig, planField7N);
                var sN2 = mergedField7N.UngroupedServers.Single();
                Equal(true, sN2.UsePam);
                Equal("ssh -p 2233 admin@nested2.internal", sN2.PamSshCommand);
                Equal("secure-password-7n", sN2.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sN2.PamTotpSecret);
                Equal("SHA512", sN2.PamTotpAlgorithm);
                Equal(8, sN2.PamTotpDigits);
                Equal(45, sN2.PamTotpPeriodSeconds);
                Equal("Enter OTP 7N: ", sN2.PamTotpPrompt);
                Equal(true, sN2.PamAutoConfirm);
                Equal("Proceed 7N? [y/n]: ", sN2.PamConfirmPrompt);

                // Mode 3: Legacy ConfigTransfer.Merge
                var conflicts7N = ConfigTransfer.FindConflicts(localConfig7N, importedMixedConfig);
                var decisions7N = conflicts7N.ToDictionary(c => (c.Kind, c.IncomingId), _ => true);
                var legacyMerged7N = ConfigTransfer.Merge(localConfig7N, importedMixedConfig, decisions7N);
                var sN3 = legacyMerged7N.UngroupedServers.Single();
                Equal(true, sN3.UsePam);
                Equal("ssh -p 2233 admin@nested2.internal", sN3.PamSshCommand);
                Equal("secure-password-7n", sN3.PamPassword);
                Equal("MFRGGZDFMZTWQ2LK", sN3.PamTotpSecret);
                Equal("SHA512", sN3.PamTotpAlgorithm);
                Equal(8, sN3.PamTotpDigits);
                Equal(45, sN3.PamTotpPeriodSeconds);
                Equal("Enter OTP 7N: ", sN3.PamTotpPrompt);
                Equal(true, sN3.PamAutoConfirm);
                Equal("Proceed 7N? [y/n]: ", sN3.PamConfirmPrompt);
            }
            finally
            {
                try { File.Delete(tempFile7N); } catch { }
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}

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
        Equal(5, steps.Count);

        // Step 1: JumpHost prompt -> PAM SSH command wrapped in exec sh -c 'exec ...'
        Equal("jump$ ", steps[0].Prompt);
        Equal("exec sh -c 'exec ssh -p 2222 app@internal.host'", steps[0].Response);

        // Step 2: assword -> PAM password
        Equal("assword", steps[1].Prompt);
        Equal("pam-user-password", steps[1].Response);

        // Step 3: Confirm prompt -> "Y"
        Equal("[Y/n]", steps[2].Prompt);
        Equal("Y", steps[2].Response);

        // Step 4: Target shell prompt -> Root command
        Equal("target$ ", steps[3].Prompt);
        Equal("su -", steps[3].Response);

        // Step 5: assword -> Root password
        Equal("assword", steps[4].Prompt);
        Equal("root-admin-password", steps[4].Response);

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
        Equal(1, minimalSteps.Count);
        Equal("$", minimalSteps[0].Prompt);
        Equal("exec sh -c 'exec ssh min@internal'", minimalSteps[0].Response);

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
        Equal(1, customSteps.Count);
        Equal(@"exec sh -c 'exec ssh '\''custom'\''@'\''internal'\'''", customSteps[0].Response);

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
        Equal(2, sudoSteps.Count);
        Equal("exec sh -c 'exec ssh sudo-user@target'", sudoSteps[0].Response);
        Equal("target$ ", sudoSteps[1].Prompt);
        Equal("sudo -i", sudoSteps[1].Response);

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
        Equal(4, jhSteps.Count);
        Equal(false, jhSteps.Any(s => s.Prompt.Contains("OTP")));
        Equal("$", jhSteps[0].Prompt);
        Equal("source /etc/profile", jhSteps[0].Response);
        Equal("Password:", jhSteps[1].Prompt);
        Equal("jh-ssh-password", jhSteps[1].Response);
        Equal("jh$ ", jhSteps[2].Prompt);
        Equal("exec sh -c 'exec ssh target@host'", jhSteps[2].Response);
        Equal("assword", jhSteps[3].Prompt);
        Equal("target-pam-pass", jhSteps[3].Response);

        // Mismatched JumpHost proxy does NOT inject post-login command
        var mismatchedProxy = new BaseProxy
        {
            StartupServerId = Guid.NewGuid(),
            PostLoginCommand = "source /etc/wrong"
        };
        var mismatchedSteps = PamLoginPlan.BuildPromptResponses(pamTarget, jhServer, fixedTime, mismatchedProxy);
        Equal(2, mismatchedSteps.Count);
        Equal("jh$ ", mismatchedSteps[0].Prompt);
        Equal("exec sh -c 'exec ssh target@host'", mismatchedSteps[0].Response);

        // Finding 3: JumpHost root elevation is NOT injected into PAM sequence
        var jhWithRoot = new ManagedServer
        {
            Name = "JHWithRoot",
            RootLogin = "su -",
            RootPassword = "jh-root-pass",
            ShellPrompt = "jh$ "
        };
        var jhRootSteps = PamLoginPlan.BuildPromptResponses(pamTarget, jhWithRoot, fixedTime);
        Equal(2, jhRootSteps.Count);
        Equal("jh$ ", jhRootSteps[0].Prompt);
        Equal("exec sh -c 'exec ssh target@host'", jhRootSteps[0].Response);
        Equal("assword", jhRootSteps[1].Prompt);
        Equal("target-pam-pass", jhRootSteps[1].Response);
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
        Equal(target.PamPassword, steps[1].Response);
        Equal(target.RootPassword, steps[3].Response);

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

        var exported = ConfigTransfer.CreateExport(config, [jumpHost.Id, target.Id], true);

        var exportedJh = exported.FindServer(jumpHost.Id);
        var exportedTarget = exported.FindServer(target.Id);

        Equal(true, exportedJh is not null);
        Equal(true, exportedTarget is not null);
        Equal("", exportedJh!.PamPassword);
        Equal("", exportedJh.PamTotpSecret);
        Equal("target-pam-secret-preserved", exportedTarget!.PamPassword);
        Equal("target-totp-secret-preserved", exportedTarget.PamTotpSecret);
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

        // Verify that PAM command is wrapped in exec sh -c 'exec ...'
        var pamCommandStep = steps.First(s => s.Prompt == "jump$ ");
        Equal("exec sh -c 'exec ssh target-user@target-host'", pamCommandStep.Response);

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
        Equal(3, steps.Count);
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
}

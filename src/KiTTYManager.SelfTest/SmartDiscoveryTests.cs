using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private sealed record DiscoveryHopAttempt(
        IReadOnlyList<ManagedServer> Gateways, Guid? ProxyId);

    private sealed class TestSmartDiscoveryProbe : ISmartDiscoveryProbe
    {
        public HashSet<string> DirectlyReachable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(string user, string pass)>> ValidCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Hostnames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PrivilegeEscalationResult> Escalations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string gateway, string target)> HopReachability { get; } = [];
        public HashSet<(Guid proxy, string host)> ProxyAuthUnreachable { get; } = [];
        public HashSet<string> HostKeyRejectedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, HashSet<string>> ProxyReachable { get; } = [];
        public HashSet<string> DirectAuthUnreachable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action<string>? OnHostProbed { get; set; }
        public string AuthErrorMessage { get; set; } = "Неверные учетные данные";

        public List<(string host, string user, string pass)> AttemptedLogins { get; } = [];
        public List<(string host, Guid? proxyId)> ReachabilityAttempts { get; } = [];
        public List<List<(string Host, string Password)>> HopChains { get; } = [];
        public List<DiscoveryHopAttempt> HopAttempts { get; } = [];
        public List<(IReadOnlyList<ManagedServer> Gateways, IReadOnlyList<string> TargetHosts)> BatchHopAttempts { get; } = [];

        public Task<bool> ProbeReachabilityAsync(
            BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ReachabilityAttempts.Add((host, jumpHost?.Id));
            if (jumpHost != null && ProxyReachable.TryGetValue(jumpHost.Id, out var reachableHosts))
                return Task.FromResult(reachableHosts.Contains(host));
            return Task.FromResult(DirectlyReachable.Contains(host));
        }

        public Task<bool> ProbeHopReachabilityAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (intermediateGateways.Count == 0)
                return ProbeReachabilityAsync(jumpHost, targetHost, targetPort, timeout, cancellationToken);

            HopChains.Add(intermediateGateways.Select(g => (g.Host, g.Password)).ToList());
            HopAttempts.Add(new DiscoveryHopAttempt(
                intermediateGateways.Select(g => new ManagedServer
                {
                    Id = g.Id,
                    Name = g.Name,
                    Host = g.Host,
                    Port = g.Port,
                    Username = g.Username,
                    Password = g.Password,
                    PrivateKeyPath = g.PrivateKeyPath,
                    PrivateKeyPassphrase = g.PrivateKeyPassphrase,
                    UseKeyboardInteractive = g.UseKeyboardInteractive
                }).ToArray(),
                jumpHost?.Id));

            var lastGateway = intermediateGateways[^1].Host;
            return Task.FromResult(HopReachability.Contains((lastGateway, targetHost)));
        }

        public Task<IReadOnlyDictionary<string, bool>> ProbeHopBatchReachabilityAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            IReadOnlyList<string> targetHosts, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (intermediateGateways.Count > 0)
            {
                HopChains.Add(intermediateGateways.Select(g => (g.Host, g.Password)).ToList());
                HopAttempts.Add(new DiscoveryHopAttempt(
                    intermediateGateways.Select(g => new ManagedServer
                    {
                        Id = g.Id,
                        Name = g.Name,
                        Host = g.Host,
                        Port = g.Port,
                        Username = g.Username,
                        Password = g.Password,
                        PrivateKeyPath = g.PrivateKeyPath,
                        PrivateKeyPassphrase = g.PrivateKeyPassphrase,
                        UseKeyboardInteractive = g.UseKeyboardInteractive
                    }).ToArray(),
                    jumpHost?.Id));
            }
            BatchHopAttempts.Add((intermediateGateways, targetHosts.ToList()));
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in targetHosts)
            {
                if (intermediateGateways.Count == 0)
                {
                    results[host] = DirectlyReachable.Contains(host);
                }
                else
                {
                    var lastGateway = intermediateGateways[^1].Host;
                    results[host] = HopReachability.Contains((lastGateway, host));
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(results);
        }

        public Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            ManagedServer target, IReadOnlyList<string> candidatePasswords,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            AttemptedLogins.Add((target.Host, target.Username, target.Password));

            if (intermediateGateways.Count == 0 && DirectAuthUnreachable.Contains(target.Host))
            {
                return Task.FromResult(SmartDiscoveryAuthResult.FailedReachability("Сбой прямого сетевого подключения"));
            }

            if (HostKeyRejectedHosts.Contains(target.Host))
            {
                return Task.FromResult(SmartDiscoveryAuthResult.FailedHostKey($"SSH host key для {target.Host} отклонён"));
            }

            if (jumpHost != null && ProxyAuthUnreachable.Contains((jumpHost.Id, target.Host)))
                return Task.FromResult(SmartDiscoveryAuthResult.FailedReachability("Сбой соединения через точку входа"));

            if (ValidCredentials.TryGetValue(target.Host, out var validList))
            {
                if (validList.Any(v => v.user == target.Username && v.pass == target.Password))
                {
                    var hostname = Hostnames.TryGetValue(target.Host, out var h) ? h : target.Host;
                    PrivilegeEscalationResult escalation;
                    if (Escalations.TryGetValue(target.Host, out var e))
                    {
                        escalation = e;
                    }
                    else
                    {
                        escalation = new PrivilegeEscalationResult
                        {
                            Method = "Root",
                            RootCommand = "",
                            RootPassword = "",
                            StatusDescription = "Root доступ подтверждён",
                            Success = true
                        };
                    }
                    OnHostProbed?.Invoke(target.Host);
                    return Task.FromResult(SmartDiscoveryAuthResult.Succeeded(hostname, escalation, "SHA256:testkey", "ssh-rsa", 2048));
                }
            }

            return Task.FromResult(SmartDiscoveryAuthResult.FailedAuth(AuthErrorMessage));
        }
    }

    private static void SmartDiscoveryInputParserTests()
    {
        // 1. Parsing and deduplication of hosts
        var input = " 192.168.1.1, 10.0.0.1; 192.168.1.1\nsrv1.example.com\tadmin@10.0.0.2:22 192.168.1.1 ";
        var hosts = SmartDiscoveryInputParser.ParseHosts(input);
        Equal(4, hosts.Count);
        Equal("192.168.1.1", hosts[0]);
        Equal("10.0.0.1", hosts[1]);
        Equal("srv1.example.com", hosts[2]);
        Equal("10.0.0.2", hosts[3]);

        // 2. Rejecting invalid syntax
        var invalidInput = "valid.host.com 999.999.999.999 bad..host !!invalid!! 172.16.0.1";
        var filteredHosts = SmartDiscoveryInputParser.ParseHosts(invalidInput);
        Equal(2, filteredHosts.Count);
        Equal("valid.host.com", filteredHosts[0]);
        Equal("172.16.0.1", filteredHosts[1]);

        // 3. Parsing logins and general lists
        var listInput = "admin\nroot, user1; admin\n pass with space \n";
        var list = SmartDiscoveryInputParser.ParseList(listInput);
        Equal(4, list.Count);
        Equal("admin", list[0]);
        Equal("root", list[1]);
        Equal("user1", list[2]);
        Equal("pass with space", list[3]);

        // 4. Parsing passwords strictly by newline without trimming
        var pwInput = "  pass with leading/trailing  \r\npass,with,commas;and;semicolons\n\npass#3";
        var passwords = SmartDiscoveryInputParser.ParsePasswords(pwInput);
        Equal(3, passwords.Count);
        Equal("  pass with leading/trailing  ", passwords[0]);
        Equal("pass,with,commas;and;semicolons", passwords[1]);
        Equal("pass#3", passwords[2]);
    }

    private static void SmartSessionNameGeneratorTests()
    {
        var config = new ManagerConfig();
        var nyGroup = new ServerGroup { Name = "New York" };
        var chiGroup = new ServerGroup { Name = "Chicago" };
        var dalGroup = new ServerGroup { Name = "Dallas" };
        var mainSiteGroup = new ServerGroup { Name = "Main Site" };
        config.Groups.AddRange([nyGroup, chiGroup, dalGroup, mainSiteGroup]);

        // Existing server for subnet detection
        var existingServer = new ManagedServer
        {
            Name = "Chicago S1 Main",
            Host = "10.20.30.5",
            Port = 22
        };
        chiGroup.Servers.Add(existingServer);

        // Example 0: s1-group-main without region -> "Group S1 Main"
        var name0 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.9", config);
        Equal("Group S1 Main", name0);

        // Example 1: s1-group-main with target group "New York" -> "New York Group S1 Main"
        var name1 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.10", config, targetGroupId: nyGroup.Id);
        Equal("New York Group S1 Main", name1);

        // Example 2: s5-reserve with target group "Chicago" -> "Chicago S5 Reserve"
        var name2 = SmartSessionNameGenerator.ProposeName("s5-reserve", "192.168.1.11", config, targetGroupId: chiGroup.Id);
        Equal("Chicago S5 Reserve", name2);

        // Example 3: s5-map-reserve with target group "Dallas" -> "Dallas MapServer S5 Reserve"
        var name3 = SmartSessionNameGenerator.ProposeName("s5-map-reserve", "192.168.1.12", config, targetGroupId: dalGroup.Id);
        Equal("Dallas MapServer S5 Reserve", name3);

        // Region inferred from IP subnet matching existing server in Chicago
        var name4 = SmartSessionNameGenerator.ProposeName("s2-app-prod", "10.20.30.55", config);
        Equal("Chicago App S2 Prod", name4);

        // Missing region/site fallback without inventing unverified facts
        var name5 = SmartSessionNameGenerator.ProposeName("web-main", "192.168.1.20", config);
        Equal("Web Main", name5);

        // Uniqueness enforcement
        var name6 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.30", config, targetGroupId: nyGroup.Id, existingNamesInBatch: [name1]);
        Equal("New York Group S1 Main 2", name6);

        // Nested target group "Chicago / Main": region from the first path component, site from the leaf
        var nestedConfig = new ManagerConfig();
        var nestedChiGroup = new ServerGroup { Name = "Chicago" };
        var nestedMainGroup = new ServerGroup { Name = "Main" };
        nestedChiGroup.Groups.Add(nestedMainGroup);
        nestedConfig.Groups.Add(nestedChiGroup);
        var name7 = SmartSessionNameGenerator.ProposeName("chi-main-s1", "192.168.1.40", nestedConfig, targetGroupId: nestedMainGroup.Id);
        Equal("Chicago S1 Main", name7);

        // /24 region takes precedence over a top-level site-like group
        var name8 = SmartSessionNameGenerator.ProposeName("s3-app-prod", "10.20.30.77", config, targetGroupId: mainSiteGroup.Id);
        Equal(true, name8.StartsWith("Chicago ", StringComparison.Ordinal));
        Equal(true, name8.EndsWith(" Main Site", StringComparison.Ordinal));
        Equal("Chicago App S3 Main Site", name8);

        // Unknown leftover token is not promoted to role when a site exists
        var name9 = SmartSessionNameGenerator.ProposeName("legacy-main", "192.168.1.41", config, targetGroupId: nyGroup.Id);
        Equal("New York Main", name9);

        // /24 region resolves from the ancestry of the existing server's group, not its leaf name
        var ancestryConfig = new ManagerConfig();
        var ancestryChicago = new ServerGroup { Name = "Chicago" };
        var ancestryMain = new ServerGroup { Name = "Main" };
        ancestryChicago.Groups.Add(ancestryMain);
        ancestryConfig.Groups.Add(ancestryChicago);
        ancestryMain.Servers.Add(new ManagedServer { Name = "Chicago S1 Main", Host = "10.20.30.5", Port = 22 });
        var name10 = SmartSessionNameGenerator.ProposeName(
            "s2-app-prod", "10.20.30.55", ancestryConfig, targetGroupId: ancestryChicago.Id);
        Equal("Chicago App S2 Prod", name10);
        Equal(false, name10.Contains("Main", StringComparison.Ordinal));
    }

    private static void CredentialPrioritizationAndLimitsTests()
    {
        var config = new ManagerConfig();
        var probe = new TestSmartDiscoveryProbe();
        probe.DirectlyReachable.Add("10.0.0.1");
        probe.DirectlyReachable.Add("10.0.0.2");
        probe.DirectlyReachable.Add("10.0.0.3");

        // Host 1 and Host 2 share credentials ("admin", "secret123")
        probe.ValidCredentials["10.0.0.1"] = [("admin", "secret123")];
        probe.ValidCredentials["10.0.0.2"] = [("admin", "secret123")];
        // Host 3 has unknown credentials
        probe.ValidCredentials["10.0.0.3"] = [("root", "superroot")];

        // Host 4 has host-key rejected
        probe.DirectlyReachable.Add("10.0.0.4");
        probe.HostKeyRejectedHosts.Add("10.0.0.4");

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4"],
            Usernames = ["admin", "user"],
            Passwords = ["pass1", "pass2", "pass3", "secret123"],
            MaxParallelConnections = 1, // Sequential to verify prioritization order deterministically
            MaxAttemptsPerHost = 4
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var result = engine.DiscoverAsync().GetAwaiter().GetResult();

        // 10.0.0.1 and 10.0.0.2 should be discovered
        Equal(2, result.Discovered.Count);
        Equal(2, result.Problematic.Count);
        var prob3 = result.Problematic.Single(p => p.Host == "10.0.0.3");
        Equal("Аутентификация", prob3.Stage);

        var prob4 = result.Problematic.Single(p => p.Host == "10.0.0.4");
        Equal("HostKey", prob4.Stage);

        // Check that 10.0.0.2 tried ("admin", "secret123") first because 10.0.0.1 confirmed it
        var host2Attempts = probe.AttemptedLogins.Where(a => a.host == "10.0.0.2").ToList();
        Equal(1, host2Attempts.Count);
        Equal("admin", host2Attempts[0].user);
        Equal("secret123", host2Attempts[0].pass);

        // Host 3 attempts should be capped at MaxAttemptsPerHost (4)
        var host3Attempts = probe.AttemptedLogins.Where(a => a.host == "10.0.0.3").ToList();
        Equal(4, host3Attempts.Count);

        // Network connection failure during authentication must NOT burn credential attempts,
        // and must break early without trying remaining passwords.
        var unreachableProbe = new TestSmartDiscoveryProbe();
        unreachableProbe.DirectlyReachable.Add("10.0.0.5");
        unreachableProbe.DirectAuthUnreachable.Add("10.0.0.5");

        var unreachableOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.0.0.5"],
            Usernames = ["admin"],
            Passwords = ["pass1", "pass2", "pass3", "pass4", "pass5"],
            MaxParallelConnections = 1,
            MaxAttemptsPerHost = 3
        };

        var unreachableEngine = new SmartDiscoveryEngine(config, unreachableOptions, unreachableProbe);
        var unreachableResult = unreachableEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(0, unreachableResult.Discovered.Count);
        Equal(1, unreachableResult.Problematic.Count);
        var prob5 = unreachableResult.Problematic.Single(p => p.Host == "10.0.0.5");
        Equal("Доступность", prob5.Stage);

        // Crucial: Only 1 attempt made (broke early after first password failed on network reachability)
        var host5Attempts = unreachableProbe.AttemptedLogins.Where(a => a.host == "10.0.0.5").ToList();
        Equal(1, host5Attempts.Count);
    }

    private static void PrivilegeEscalationOrderingTests()
    {
        // 1. Test escalation result formatting and RootCommand separation
        var rootRes = new PrivilegeEscalationResult { Method = "Root", RootCommand = "", Success = true, StatusDescription = "Прямой root" };
        Equal(true, rootRes.Success);
        Equal("", rootRes.RootCommand);
        Equal(true, rootRes.ToString().Contains("Root"));

        var sudoNoPwRes = new PrivilegeEscalationResult { Method = "sudo (без пароля)", RootCommand = "sudo -i", Success = true, StatusDescription = "Доступен sudo без пароля" };
        Equal(true, sudoNoPwRes.Success);
        Equal("sudo -i", sudoNoPwRes.RootCommand);

        var sudoPwRes = new PrivilegeEscalationResult { Method = "sudo", RootCommand = "sudo -i", RootPassword = "rootpassword", Success = true, StatusDescription = "Повышение через sudo" };
        Equal("sudo -i", sudoPwRes.RootCommand);
        Equal("rootpassword", sudoPwRes.RootPassword);
        Equal("sudo -i", KittyCredentialDecoder.NormalizeRootCommand(sudoPwRes.RootCommand));

        var suPwRes = new PrivilegeEscalationResult { Method = "su", RootCommand = "su -", RootPassword = "supw", Success = true, StatusDescription = "Повышение через su" };
        Equal("su -", suPwRes.RootCommand);
        Equal("su -", KittyCredentialDecoder.NormalizeRootCommand(suPwRes.RootCommand));

        var failRes = new PrivilegeEscalationResult { Method = "Не повышено", RootCommand = "", Success = false, StatusDescription = "Сбой" };
        Equal(false, failRes.Success);

        // 2. Exercise actual privilege escalation probing order using ProbePrivilegeEscalationCoreAsync
        var executedCommands = new List<string>();
        var testedPasswords = new List<(string method, string password, string label)>();

        Task<(int ExitStatus, string Result)> MockRunCommandNonRoot(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            executedCommands.Add(cmd);
            if (cmd.StartsWith("id -u"))
                return Task.FromResult((0, "1000\n"));
            if (cmd.Contains("sudo -n id"))
                return Task.FromResult((1, "sudo: a password is required"));
            return Task.FromResult((1, "command not found"));
        }

        // Scenario A: su - password guessing runs first. If su - with an alternative candidate password succeeds,
        // login password is not tried and sudo is never executed.
        Task<(bool Success, string Method, string Command, string Password)> MockTryPasswordEscalationA(
            string method, string pw, string label, TimeSpan timeout, CancellationToken ct)
        {
            testedPasswords.Add((method, pw, label));
            if (method == "su" && pw == "altPass2")
            {
                return Task.FromResult((true, "su", "su -", pw));
            }
            return Task.FromResult((false, "Не повышено", "", ""));
        }

        var candidatePasswords = new[] { "altPass1", "altPass2", "loginPassword" };
        var resultA = DefaultSmartDiscoveryProbe.ProbePrivilegeEscalationCoreAsync(
            MockRunCommandNonRoot,
            MockTryPasswordEscalationA,
            "user",
            "loginPassword",
            candidatePasswords,
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        ).GetAwaiter().GetResult();

        Equal(true, resultA.Success);
        Equal("su", resultA.Method);
        Equal("su -", resultA.RootCommand);
        Equal("altPass2", resultA.RootPassword);

        // Assert: id -u was checked first; since su - succeeded, sudo command was NEVER executed!
        Equal(1, executedCommands.Count);
        Equal("id -u 2>/dev/null", executedCommands[0]);

        // Assert: alternative password altPass1 is tested first for su
        Equal(2, testedPasswords.Count);
        Equal(("su", "altPass1", "root"), testedPasswords[0]);
        // Assert: alternative password altPass2 is tested next and succeeds
        Equal(("su", "altPass2", "root"), testedPasswords[1]);
        // Assert: loginPassword was NOT tested because altPass2 succeeded first
        Equal(false, testedPasswords.Any(t => t.password == "loginPassword"));

        // Scenario B: All su - attempts fail. Then passwordless sudo is checked and fails.
        // Finally sudo -i password guessing runs, and login password succeeds.
        executedCommands.Clear();
        testedPasswords.Clear();

        Task<(bool Success, string Method, string Command, string Password)> MockTryPasswordEscalationB(
            string method, string pw, string label, TimeSpan timeout, CancellationToken ct)
        {
            testedPasswords.Add((method, pw, label));
            if (method == "sudo" && pw == "loginPassword")
            {
                return Task.FromResult((true, "sudo", "sudo -i", pw));
            }
            return Task.FromResult((false, "Не повышено", "", ""));
        }

        var resultB = DefaultSmartDiscoveryProbe.ProbePrivilegeEscalationCoreAsync(
            MockRunCommandNonRoot,
            MockTryPasswordEscalationB,
            "user",
            "loginPassword",
            candidatePasswords,
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        ).GetAwaiter().GetResult();

        Equal(true, resultB.Success);
        Equal("sudo", resultB.Method);
        Equal("sudo -i", resultB.RootCommand);
        Equal("loginPassword", resultB.RootPassword);

        // Sequence of tested passwords:
        // su attempts: altPass1 -> altPass2 -> loginPassword
        // sudo attempts: altPass1 -> altPass2 -> loginPassword (succeeds)
        Equal(6, testedPasswords.Count);
        Equal(("su", "altPass1", "root"), testedPasswords[0]);
        Equal(("su", "altPass2", "root"), testedPasswords[1]);
        Equal(("su", "loginPassword", "пользователя"), testedPasswords[2]);
        Equal(("sudo", "altPass1", "root"), testedPasswords[3]);
        Equal(("sudo", "altPass2", "root"), testedPasswords[4]);
        Equal(("sudo", "loginPassword", "пользователя"), testedPasswords[5]);

        // Executed commands: id -u first, then sudo -k & sudo -n id
        Equal(2, executedCommands.Count);
        Equal("id -u 2>/dev/null", executedCommands[0]);
        Equal("sudo -k 2>/dev/null; sudo -n id 2>/dev/null", executedCommands[1]);

        // Scenario C: All su - attempts fail. Then passwordless sudo succeeds, skipping sudo password attempts.
        executedCommands.Clear();
        testedPasswords.Clear();

        Task<(int ExitStatus, string Result)> MockRunCommandPasswordless(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            executedCommands.Add(cmd);
            if (cmd.StartsWith("id -u"))
                return Task.FromResult((0, "1000\n"));
            if (cmd.Contains("sudo -n id"))
                return Task.FromResult((0, "uid=0(root) gid=0(root) groups=0(root)\n"));
            return Task.FromResult((1, "failed"));
        }

        Task<(bool Success, string Method, string Command, string Password)> MockTryPasswordAllFail(
            string method, string pw, string label, TimeSpan timeout, CancellationToken ct)
        {
            testedPasswords.Add((method, pw, label));
            return Task.FromResult((false, "Не повышено", "", ""));
        }

        var resultC = DefaultSmartDiscoveryProbe.ProbePrivilegeEscalationCoreAsync(
            MockRunCommandPasswordless,
            MockTryPasswordAllFail,
            "user",
            "loginPassword",
            candidatePasswords,
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        ).GetAwaiter().GetResult();

        Equal(true, resultC.Success);
        Equal("sudo (без пароля)", resultC.Method);
        Equal("sudo -i", resultC.RootCommand);
        Equal("", resultC.RootPassword);
        // Only su attempts were made (3 of them) before passwordless sudo succeeded; 0 sudo password attempts!
        Equal(3, testedPasswords.Count);
        Equal(true, testedPasswords.All(t => t.method == "su"));
        Equal(2, executedCommands.Count);
        Equal("id -u 2>/dev/null", executedCommands[0]);
        Equal("sudo -k 2>/dev/null; sudo -n id 2>/dev/null", executedCommands[1]);
    }

    private static void MultiHopDiscoveryAndCyclePreventionTests()
    {
        var config = new ManagerConfig();
        var probe = new TestSmartDiscoveryProbe();

        // Host A is directly reachable
        probe.DirectlyReachable.Add("10.0.0.1");
        probe.ValidCredentials["10.0.0.1"] = [("root", "pwA")];
        probe.Hostnames["10.0.0.1"] = "srv-gw-main";

        // Host B is NOT directly reachable, but reachable through Host A
        probe.ValidCredentials["192.168.1.10"] = [("root", "pwB")];
        probe.Hostnames["192.168.1.10"] = "srv-app-prod";
        probe.HopReachability.Add(("10.0.0.1", "192.168.1.10"));

        // Host C is reachable only through Host B
        probe.ValidCredentials["172.16.0.5"] = [("root", "pwC")];
        probe.Hostnames["172.16.0.5"] = "srv-db-reserve";
        probe.HopReachability.Add(("192.168.1.10", "172.16.0.5"));

        // Host D is reachable via Host A and Host B, but has invalid credentials
        probe.HopReachability.Add(("10.0.0.1", "172.16.9.9"));
        probe.HopReachability.Add(("192.168.1.10", "172.16.9.9"));

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.0.0.1", "192.168.1.10", "172.16.0.5", "172.16.9.9"],
            Usernames = ["root"],
            Passwords = ["pwA", "pwB", "pwC"],
            MaxParallelConnections = 2,
            MaxAttemptsPerHost = 3
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var result = engine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(3, result.Discovered.Count);
        Equal(1, result.Problematic.Count);

        var probD = result.Problematic.Single(p => p.Host == "172.16.9.9");
        Equal("Аутентификация", probD.Stage);

        // Multi-hop attempt limit across all routes/gateways
        var hostDAttempts = probe.AttemptedLogins.Where(a => a.host == "172.16.9.9").ToList();
        Equal(options.MaxAttemptsPerHost, hostDAttempts.Count);

        var candA = result.Discovered.Single(c => c.Host == "10.0.0.1");
        var candB = result.Discovered.Single(c => c.Host == "192.168.1.10");
        var candC = result.Discovered.Single(c => c.Host == "172.16.0.5");

        // Gateway dependencies
        Equal(null, candA.RequiredPreviousServerId);
        Equal(candA.Id, candB.RequiredPreviousServerId);
        Equal(candB.Id, candC.RequiredPreviousServerId);

        // Link sources
        Equal(0, candA.DiscoveredLinksFrom.Count);
        Equal(candA.Id, candB.DiscoveredLinksFrom.Single());
        Equal(candB.Id, candC.DiscoveredLinksFrom.Single());

        // Route hops
        Equal(1, candA.RouteHops.Count);
        Equal(2, candB.RouteHops.Count);
        Equal(3, candC.RouteHops.Count);

        var savedGatewayConfig = new ManagerConfig();
        var savedGatewayGroup = new ServerGroup { Name = "Saved Gateway Group" };
        var savedGateway = new ManagedServer
        {
            Name = "Saved Group Gateway",
            Host = "10.20.0.2",
            Port = 22,
            Username = "saved",
            Password = "SAVED_PW",
            PrivateKeyPath = "C:\\keys\\saved.ppk"
        };
        savedGatewayGroup.Servers.Add(savedGateway);
        savedGatewayConfig.Groups.Add(savedGatewayGroup);
        var outsideEntry = new ManagedServer
        {
            Name = "Outside Entry",
            Host = "10.20.0.1",
            Port = 22,
            Username = "jump",
            Password = "OUTSIDE_PW"
        };
        var outsideHop = new ManagedServer
        {
            Name = "Outside Hop",
            Host = "10.20.0.3",
            Port = 22,
            Username = "hop",
            Password = "HOP_PW"
        };
        savedGatewayConfig.UngroupedServers.AddRange([outsideEntry, outsideHop]);
        var savedRouteProxy = new BaseProxy { Name = "Saved Route Proxy", Host = "127.0.0.1", Port = 1101 };
        var otherSavedProxy = new BaseProxy { Name = "Other Saved Proxy", Host = "127.0.0.1", Port = 1102 };
        savedGatewayConfig.BaseProxies.AddRange([otherSavedProxy, savedRouteProxy]);
        outsideEntry.PreferredRoute = new CachedRoute
        {
            ProxyId = savedRouteProxy.Id,
            ServerIds = [outsideEntry.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        savedGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = savedRouteProxy.Id,
            ServerIds = [outsideEntry.Id, outsideHop.Id, savedGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        savedGatewayConfig.Links.AddRange(
        [
            new ServerLink
            {
                FromServerId = outsideEntry.Id,
                ToServerId = outsideHop.Id,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastSuccessfulProxyId = savedRouteProxy.Id,
                LastStrategy = "DirectTcpIp"
            },
            new ServerLink
            {
                FromServerId = outsideHop.Id,
                ToServerId = savedGateway.Id,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastSuccessfulProxyId = savedRouteProxy.Id,
                LastStrategy = "DirectTcpIp"
            }
        ]);

        var savedGatewayProbe = new TestSmartDiscoveryProbe();
        const string savedTargetHost = "10.20.5.5";
        savedGatewayProbe.HopReachability.Add((savedGateway.Host, savedTargetHost));
        savedGatewayProbe.ValidCredentials[savedTargetHost] = [("root", "TARGET_PW")];

        var savedGatewayOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [savedTargetHost],
            Usernames = ["root"],
            Passwords = ["TARGET_PW"],
            TargetGroupId = savedGatewayGroup.Id,
            MaxParallelConnections = 1,
            AutoSelectJumpHost = true
        };

        var savedGatewayEngine = new SmartDiscoveryEngine(
            savedGatewayConfig, savedGatewayOptions, savedGatewayProbe);
        var savedGatewayResult = savedGatewayEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(false, savedGatewayResult.Discovered.Any(c => c.Id == savedGateway.Id));
        var savedTarget = savedGatewayResult.Discovered.Single();
        Equal(savedGateway.Id, savedTarget.RequiredPreviousServerId);
        Equal(savedGateway.Id, savedTarget.DiscoveredLinksFrom.Single());
        Equal(savedRouteProxy.Id, savedTarget.VerifiedProxyId);
        Equal(
            string.Join('/', outsideEntry.Host, outsideHop.Host, savedGateway.Host, savedTargetHost),
            string.Join('/', savedTarget.RouteHops.Select(hop => hop.Host)));

        var savedGatewayAttempt = savedGatewayProbe.HopAttempts.Last();
        Equal(savedRouteProxy.Id, savedGatewayAttempt.ProxyId);
        Equal(3, savedGatewayAttempt.Gateways.Count);
        Equal(outsideEntry.Host, savedGatewayAttempt.Gateways[0].Host);
        Equal(outsideHop.Host, savedGatewayAttempt.Gateways[1].Host);
        Equal(savedGateway.Id, savedGatewayAttempt.Gateways[2].Id);
        Equal("SAVED_PW", savedGatewayAttempt.Gateways[2].Password);
        Equal("C:\\keys\\saved.ppk", savedGatewayAttempt.Gateways[2].PrivateKeyPath);

        var savedCreated = SmartDiscoverySessionCreator.CreateSessions(
            savedGatewayConfig, savedGatewayResult.Discovered);
        Equal(1, savedCreated.Count);
        Equal(savedGateway.Id, savedCreated[0].RequiredPreviousServerId);
        Equal(true, savedGatewayConfig.Links.Any(
            link => link.FromServerId == savedGateway.Id && link.ToServerId == savedTarget.Id));
        Equal("SAVED_PW", savedGateway.Password);
        Equal("C:\\keys\\saved.ppk", savedGateway.PrivateKeyPath);

        var directSavedConfig = new ManagerConfig();
        var directSavedGroup = new ServerGroup { Name = "Direct Saved Group" };
        var directSavedGateway = new ManagedServer
        {
            Name = "Saved Direct Root",
            Host = "10.30.0.1",
            Port = 22,
            Username = "root",
            Password = "ROOT_PW",
            TryDirectWithoutJumphost = true
        };
        directSavedGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = Guid.Empty,
            ServerIds = [directSavedGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        directSavedGroup.Servers.Add(directSavedGateway);
        directSavedConfig.Groups.Add(directSavedGroup);
        directSavedConfig.BaseProxies.AddRange([
            new BaseProxy { Name = "Direct Auto Proxy 1", Host = "127.0.0.1", Port = 1201 },
            new BaseProxy { Name = "Direct Auto Proxy 2", Host = "127.0.0.1", Port = 1202 }
        ]);

        var directSavedProbe = new TestSmartDiscoveryProbe();
        const string directWaveGatewayHost = "10.30.0.9";
        const string directWaveTargetHost = "10.30.0.10";
        directSavedProbe.HopReachability.Add((directSavedGateway.Host, directWaveGatewayHost));
        directSavedProbe.HopReachability.Add((directWaveGatewayHost, directWaveTargetHost));
        directSavedProbe.ValidCredentials[directWaveGatewayHost] = [("root", "WAVE_PW")];
        directSavedProbe.ValidCredentials[directWaveTargetHost] = [("root", "WAVE_PW")];

        var directSavedOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [directWaveGatewayHost, directWaveTargetHost],
            Usernames = ["root"],
            Passwords = ["WAVE_PW"],
            TargetGroupId = directSavedGroup.Id,
            MaxParallelConnections = 1,
            AutoSelectJumpHost = true
        };

        var directSavedEngine = new SmartDiscoveryEngine(
            directSavedConfig, directSavedOptions, directSavedProbe);
        var directSavedResult = directSavedEngine.DiscoverAsync().GetAwaiter().GetResult();

        var directWaveGateway = directSavedResult.Discovered.Single(c => c.Host == directWaveGatewayHost);
        var directWaveTarget = directSavedResult.Discovered.Single(c => c.Host == directWaveTargetHost);
        Equal(directSavedGateway.Id, directWaveGateway.RequiredPreviousServerId);
        Equal(directWaveGateway.Id, directWaveTarget.RequiredPreviousServerId);
        Equal(
            string.Join('/', directSavedGateway.Host, directWaveGatewayHost, directWaveTargetHost),
            string.Join('/', directWaveTarget.RouteHops.Select(hop => hop.Host)));
        Equal(null, directSavedProbe.HopAttempts.Last().ProxyId);
        Equal(
            string.Join('/', directSavedGateway.Host, directWaveGatewayHost),
            string.Join('/', directSavedProbe.HopAttempts.Last().Gateways.Select(server => server.Host)));
        Equal(null, directWaveGateway.VerifiedProxyId);

        var directSavedCreated = SmartDiscoverySessionCreator.CreateSessions(
            directSavedConfig, directSavedResult.Discovered);
        Equal(2, directSavedCreated.Count);
        Equal(directSavedGateway.Id,
            directSavedCreated.Single(s => s.Host == directWaveGatewayHost).RequiredPreviousServerId);
        Equal(directWaveGateway.Id,
            directSavedCreated.Single(s => s.Host == directWaveTargetHost).RequiredPreviousServerId);
        Equal(true, directSavedConfig.Links.Any(
            link => link.FromServerId == directSavedGateway.Id && link.ToServerId == directWaveGateway.Id));

        // SOCKS5 probe banner read verification
        try
        {
            using (var noBannerSocks = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                noBannerSocks.Start();
                var port = ((System.Net.IPEndPoint)noBannerSocks.LocalEndpoint).Port;
                var serverTask = Task.Run(async () =>
                {
                    using var client = await noBannerSocks.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var greeting = new byte[3];
                    await stream.ReadExactlyAsync(greeting);
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                    var reqHeader = new byte[4];
                    await stream.ReadExactlyAsync(reqHeader);
                    var addrLen = reqHeader[3] switch
                    {
                        0x01 => 4,
                        0x03 => stream.ReadByte(),
                        0x04 => 16,
                        _ => 4
                    };
                    var addrBuf = new byte[addrLen + 2];
                    await stream.ReadExactlyAsync(addrBuf);
                    await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 22 });
                    client.Close();
                });

                var proxy = new BaseProxy { Host = "127.0.0.1", Port = port };
                var canConnectNoBanner = Socks5TcpProbe.CanConnectAsync(
                    proxy, "10.0.0.1", 22, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Equal(false, canConnectNoBanner);
                serverTask.GetAwaiter().GetResult();
            }

            using (var withBannerSocks = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                withBannerSocks.Start();
                var port = ((System.Net.IPEndPoint)withBannerSocks.LocalEndpoint).Port;
                var serverTask = Task.Run(async () =>
                {
                    using var client = await withBannerSocks.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var greeting = new byte[3];
                    await stream.ReadExactlyAsync(greeting);
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                    var reqHeader = new byte[4];
                    await stream.ReadExactlyAsync(reqHeader);
                    var addrLen = reqHeader[3] switch
                    {
                        0x01 => 4,
                        0x03 => stream.ReadByte(),
                        0x04 => 16,
                        _ => 4
                    };
                    var addrBuf = new byte[addrLen + 2];
                    await stream.ReadExactlyAsync(addrBuf);
                    await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 22 });
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("SSH-2.0-TestServer\r\n"));
                    await stream.ReadAsync(new byte[1]);
                });

                var proxy = new BaseProxy { Host = "127.0.0.1", Port = port };
                var canConnectWithBanner = Socks5TcpProbe.CanConnectAsync(
                    proxy, "10.0.0.1", 22, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Equal(true, canConnectWithBanner);
                serverTask.GetAwaiter().GetResult();
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Ignored in sandboxed test environments where loopback socket binding is restricted
        }

        // Phase 2 fallback to Phase 3 when direct connection is unreachable
        var fallbackConfig = new ManagerConfig();
        var fallbackGroup = new ServerGroup { Name = "Fallback Group" };
        var directGateway = new ManagedServer
        {
            Name = "Direct Gateway",
            Host = "10.50.0.1",
            Port = 22,
            Username = "root",
            Password = "GW_PASSWORD",
            TryDirectWithoutJumphost = true
        };
        directGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = Guid.Empty,
            ServerIds = [directGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        fallbackGroup.Servers.Add(directGateway);
        fallbackConfig.Groups.Add(fallbackGroup);

        var fallbackProbe = new TestSmartDiscoveryProbe();
        const string falsePositiveDirectHost = "10.50.0.2";
        fallbackProbe.DirectlyReachable.Add(falsePositiveDirectHost);
        fallbackProbe.DirectAuthUnreachable.Add(falsePositiveDirectHost);
        fallbackProbe.HopReachability.Add((directGateway.Host, falsePositiveDirectHost));
        fallbackProbe.ValidCredentials[falsePositiveDirectHost] = [("root", "TARGET_PASSWORD")];

        var fallbackOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [falsePositiveDirectHost],
            Usernames = ["root"],
            Passwords = ["TARGET_PASSWORD"],
            TargetGroupId = fallbackGroup.Id,
            MaxParallelConnections = 1,
            AutoSelectJumpHost = false
        };

        var fallbackEngine = new SmartDiscoveryEngine(fallbackConfig, fallbackOptions, fallbackProbe);
        var fallbackResult = fallbackEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, fallbackResult.Discovered.Count);
        Equal(0, fallbackResult.Problematic.Count);
        var discoveredCandidate = fallbackResult.Discovered.Single();
        Equal(falsePositiveDirectHost, discoveredCandidate.Host);
        Equal(directGateway.Id, discoveredCandidate.RequiredPreviousServerId);

        // Subgroup discovery seeds independent servers from ancestor/root group
        var hierarchyConfig = new ManagerConfig();
        var rootGroup = new ServerGroup { Name = "Root Group" };
        var subGroup = new ServerGroup { Name = "Sub Group" };
        rootGroup.Groups.Add(subGroup);
        hierarchyConfig.Groups.Add(rootGroup);

        var ancestorGateway = new ManagedServer
        {
            Name = "Ancestor Gateway",
            Host = "10.60.0.1",
            Port = 22,
            Username = "root",
            Password = "ROOT_PW",
            TryDirectWithoutJumphost = true
        };
        ancestorGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = Guid.Empty,
            ServerIds = [ancestorGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        rootGroup.Servers.Add(ancestorGateway);

        const string subTargetHost = "10.60.0.50";
        var hierarchyProbe = new TestSmartDiscoveryProbe();
        hierarchyProbe.HopReachability.Add((ancestorGateway.Host, subTargetHost));
        hierarchyProbe.ValidCredentials[subTargetHost] = [("root", "SUB_PW")];

        var hierarchyOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [subTargetHost],
            Usernames = ["root"],
            Passwords = ["SUB_PW"],
            TargetGroupId = subGroup.Id,
            MaxParallelConnections = 1
        };

        var hierarchyEngine = new SmartDiscoveryEngine(hierarchyConfig, hierarchyOptions, hierarchyProbe);
        var hierarchyResult = hierarchyEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, hierarchyResult.Discovered.Count);
        Equal(0, hierarchyResult.Problematic.Count);
        var subTarget = hierarchyResult.Discovered.Single();
        Equal(subTargetHost, subTarget.Host);
        Equal(ancestorGateway.Id, subTarget.RequiredPreviousServerId);

        // Phase 3 batch hop reachability optimization
        var batchOptConfig = new ManagerConfig();
        var batchOptGroup = new ServerGroup { Name = "Batch Group" };
        var batchOptGateway = new ManagedServer
        {
            Name = "Batch Gateway",
            Host = "10.70.0.1",
            Port = 22,
            Username = "root",
            Password = "GW_PW",
            TryDirectWithoutJumphost = true
        };
        batchOptGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = Guid.Empty,
            ServerIds = [batchOptGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        batchOptGroup.Servers.Add(batchOptGateway);
        batchOptConfig.Groups.Add(batchOptGroup);

        const string reachableHost = "10.70.0.10";
        const string unreachableHost = "10.70.0.11";

        var batchOptProbe = new TestSmartDiscoveryProbe();
        batchOptProbe.HopReachability.Add((batchOptGateway.Host, reachableHost));
        batchOptProbe.ValidCredentials[reachableHost] = [("root", "REACHABLE_PW")];
        batchOptProbe.ValidCredentials[unreachableHost] = [("root", "UNREACHABLE_PW")];

        var batchOptOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [reachableHost, unreachableHost],
            Usernames = ["root"],
            Passwords = ["REACHABLE_PW", "UNREACHABLE_PW"],
            TargetGroupId = batchOptGroup.Id,
            MaxParallelConnections = 1
        };

        var batchOptEngine = new SmartDiscoveryEngine(batchOptConfig, batchOptOptions, batchOptProbe);
        var batchOptResult = batchOptEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, batchOptResult.Discovered.Count);
        Equal(1, batchOptResult.Problematic.Count);
        Equal(reachableHost, batchOptResult.Discovered.Single().Host);
        Equal(unreachableHost, batchOptResult.Problematic.Single().Host);

        Equal(true, batchOptProbe.BatchHopAttempts.Any(b =>
            b.TargetHosts.Contains(reachableHost) && b.TargetHosts.Contains(unreachableHost)));

        Equal(true, batchOptProbe.AttemptedLogins.Any(a => a.host == reachableHost));
        Equal(false, batchOptProbe.AttemptedLogins.Any(a => a.host == unreachableHost));

        var defaultProbe = new DefaultSmartDiscoveryProbe();
        var directBatch = defaultProbe.ProbeHopBatchReachabilityAsync(
            null, [], ["127.0.0.1"], 1, TimeSpan.FromMilliseconds(50), CancellationToken.None).GetAwaiter().GetResult();
        Equal(1, directBatch.Count);

        // Regression test: A (root) -> B (subgroup) -> new target asserts B is RequiredPreviousServerId
        var abConfig = new ManagerConfig();
        var abProxy = new BaseProxy { Name = "AB Jumphost", Host = "127.0.0.1", Port = 2222 };
        abConfig.BaseProxies.Add(abProxy);

        var abRootGroup = new ServerGroup { Name = "AB Root Group" };
        var abSubGroup = new ServerGroup { Name = "AB Sub Group" };
        abRootGroup.Groups.Add(abSubGroup);
        abConfig.Groups.Add(abRootGroup);

        var serverA = new ManagedServer
        {
            Name = "Server A",
            Host = "10.80.0.1",
            Port = 22,
            Username = "root",
            Password = "PW_A",
            TryDirectWithoutJumphost = true,
            PreferredRoute = new CachedRoute
            {
                ProxyId = abProxy.Id,
                ServerIds = [Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        serverA.PreferredRoute.ServerIds[0] = serverA.Id;
        abRootGroup.Servers.Add(serverA);

        var serverB = new ManagedServer
        {
            Name = "Server B",
            Host = "10.80.0.2",
            Port = 22,
            Username = "root",
            Password = "PW_B",
            RequiredPreviousServerId = serverA.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = abProxy.Id,
                ServerIds = [serverA.Id, Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        serverB.PreferredRoute.ServerIds[1] = serverB.Id;
        abSubGroup.Servers.Add(serverB);

        abConfig.Links.Add(new ServerLink
        {
            FromServerId = serverA.Id,
            ToServerId = serverB.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastStrategy = "DirectTcpIp",
            LastSuccessfulProxyId = abProxy.Id
        });

        const string newTargetHost = "10.80.0.99";
        var abProbe = new TestSmartDiscoveryProbe();
        abProbe.HopReachability.Add((serverB.Host, newTargetHost));
        abProbe.ValidCredentials[newTargetHost] = [("root", "TARGET_PW")];

        var abOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [newTargetHost],
            Usernames = ["root"],
            Passwords = ["TARGET_PW"],
            TargetGroupId = abSubGroup.Id,
            MaxParallelConnections = 1
        };

        var abEngine = new SmartDiscoveryEngine(abConfig, abOptions, abProbe);
        var abResult = abEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, abResult.Discovered.Count);
        Equal(0, abResult.Problematic.Count);
        var abTarget = abResult.Discovered.Single();
        Equal(newTargetHost, abTarget.Host);
        Equal(serverB.Id, abTarget.RequiredPreviousServerId);
        Equal(string.Join('/', serverA.Host, serverB.Host, newTargetHost), string.Join('/', abTarget.RouteHops.Select(h => h.Host)));

        // Regression test: 3-level hierarchy: Root (A) -> Intermediate (B) -> Target (empty)
        // Target host is reachable only through B
        var threeLevelConfig = new ManagerConfig();
        var threeLevelProxy = new BaseProxy { Name = "3L JH", Host = "127.0.0.1", Port = 2300 };
        threeLevelConfig.BaseProxies.Add(threeLevelProxy);

        var threeLevelRoot = new ServerGroup { Name = "3L Root Group" };
        var threeLevelMid = new ServerGroup { Name = "3L Intermediate Group" };
        var threeLevelTargetGroup = new ServerGroup { Name = "3L Target Group" };
        threeLevelMid.Groups.Add(threeLevelTargetGroup);
        threeLevelRoot.Groups.Add(threeLevelMid);
        threeLevelConfig.Groups.Add(threeLevelRoot);

        var server3LA = new ManagedServer
        {
            Name = "Server 3L A",
            Host = "10.90.0.1",
            Port = 22,
            Username = "root",
            Password = "PW_A_3L",
            TryDirectWithoutJumphost = true,
            PreferredRoute = new CachedRoute
            {
                ProxyId = threeLevelProxy.Id,
                ServerIds = [Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        server3LA.PreferredRoute.ServerIds[0] = server3LA.Id;
        threeLevelRoot.Servers.Add(server3LA);

        var server3LB = new ManagedServer
        {
            Name = "Server 3L B",
            Host = "10.90.0.2",
            Port = 22,
            Username = "root",
            Password = "PW_B_3L",
            RequiredPreviousServerId = server3LA.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = threeLevelProxy.Id,
                ServerIds = [server3LA.Id, Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        server3LB.PreferredRoute.ServerIds[1] = server3LB.Id;
        threeLevelMid.Servers.Add(server3LB);

        threeLevelConfig.Links.Add(new ServerLink
        {
            FromServerId = server3LA.Id,
            ToServerId = server3LB.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastStrategy = "DirectTcpIp",
            LastSuccessfulProxyId = threeLevelProxy.Id
        });

        const string threeLevelTargetHost = "10.90.0.99";
        var threeLevelProbe = new TestSmartDiscoveryProbe();
        threeLevelProbe.HopReachability.Add((server3LB.Host, threeLevelTargetHost));
        threeLevelProbe.ValidCredentials[threeLevelTargetHost] = [("root", "TARGET_3L_PW")];

        var threeLevelOptions = new SmartDiscoveryOptions
        {
            TargetHosts = [threeLevelTargetHost],
            Usernames = ["root"],
            Passwords = ["TARGET_3L_PW"],
            TargetGroupId = threeLevelTargetGroup.Id,
            MaxParallelConnections = 1
        };

        var threeLevelEngine = new SmartDiscoveryEngine(threeLevelConfig, threeLevelOptions, threeLevelProbe);
        var threeLevelResult = threeLevelEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, threeLevelResult.Discovered.Count);
        Equal(0, threeLevelResult.Problematic.Count);
        var threeLevelDiscovered = threeLevelResult.Discovered.Single();
        Equal(threeLevelTargetHost, threeLevelDiscovered.Host);
        Equal(server3LB.Id, threeLevelDiscovered.RequiredPreviousServerId);
        Equal(string.Join('/', server3LA.Host, server3LB.Host, threeLevelTargetHost), string.Join('/', threeLevelDiscovered.RouteHops.Select(h => h.Host)));
    }

    private static void CancellationPreservesDiscoveredCandidatesTests()
    {
        var config = new ManagerConfig();
        var probe = new TestSmartDiscoveryProbe();
        probe.DirectlyReachable.Add("10.0.0.1");
        probe.DirectlyReachable.Add("10.0.0.2");
        probe.ValidCredentials["10.0.0.1"] = [("root", "pw")];
        probe.ValidCredentials["10.0.0.2"] = [("root", "pw")];

        var cts = new CancellationTokenSource();
        // Cancel after discovering host A (10.0.0.1) but before host B (10.0.0.2)
        probe.OnHostProbed = host =>
        {
            if (host == "10.0.0.1")
            {
                cts.Cancel();
            }
        };

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.0.0.1", "10.0.0.2"],
            Usernames = ["root"],
            Passwords = ["pw"],
            MaxParallelConnections = 1
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var result = engine.DiscoverAsync(cts.Token).GetAwaiter().GetResult();

        // Must not throw, returns host A in Discovered despite cancellation
        Equal(1, result.Discovered.Count);
        Equal("10.0.0.1", result.Discovered[0].Host);
        Equal(false, result.Discovered.Any(d => d.Host == "10.0.0.2"));
    }

    private static void StageSpecificRetryTests()
    {
        var config = new ManagerConfig();
        var probe = new TestSmartDiscoveryProbe();
        probe.DirectlyReachable.Add("10.0.0.1");
        // No valid credentials initially
        probe.ValidCredentials["10.0.0.1"] = [("root", "correct-password")];

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.0.0.1"],
            Usernames = ["root"],
            Passwords = ["wrong-password"]
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var initial = engine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(0, initial.Discovered.Count);
        Equal(1, initial.Problematic.Count);
        Equal("10.0.0.1", initial.Problematic[0].Host);

        // Now retry with updated options (correct password added)
        var updatedOptions = new SmartDiscoveryOptions
        {
            Usernames = ["root"],
            Passwords = ["correct-password"]
        };

        var retried = engine.RetryProblematicAsync(initial.Problematic, updatedOptions).GetAwaiter().GetResult();
        Equal(1, retried.Discovered.Count);
        Equal("10.0.0.1", retried.Discovered[0].Host);
        Equal("root", retried.Discovered[0].Username);
        Equal("correct-password", retried.Discovered[0].Password);

        // Verify retry with failing credentials updates problematic entry details
        var stillFailingOptions = new SmartDiscoveryOptions
        {
            Usernames = ["root"],
            Passwords = ["wrong-password-again"]
        };
        var failingRetry = engine.RetryProblematicAsync(initial.Problematic, stillFailingOptions).GetAwaiter().GetResult();
        Equal(0, failingRetry.Discovered.Count);
        Equal(1, failingRetry.Problematic.Count);
        Equal("10.0.0.1", failingRetry.Problematic[0].Host);
        Equal("Аутентификация", failingRetry.Problematic[0].Stage);

        // Also verify retry with existing gateways
        var candGateway = retried.Discovered[0];
        probe.HopReachability.Add(("10.0.0.1", "192.168.50.1"));
        probe.ValidCredentials["192.168.50.1"] = [("root", "secret")];

        var probDependent = new ProblematicServerCandidate
        {
            Host = "192.168.50.1",
            Stage = "Доступность",
            Reason = "Узел недоступен по TCP",
            CanRetry = true,
            IsSelected = true
        };

        var hopRetryOptions = new SmartDiscoveryOptions
        {
            Usernames = ["root"],
            Passwords = ["secret"]
        };

        var hopRetried = engine.RetryProblematicAsync(
            [probDependent], hopRetryOptions, existingGateways: [candGateway]).GetAwaiter().GetResult();
        Equal(1, hopRetried.Discovered.Count);
        Equal("192.168.50.1", hopRetried.Discovered[0].Host);
        Equal(candGateway.Id, hopRetried.Discovered[0].RequiredPreviousServerId);
    }

    private static void SessionCreationAndLinkRegistrationTests()
    {
        var config = new ManagerConfig();
        var parentGroup = new ServerGroup { Name = "Main Servers" };
        config.Groups.Add(parentGroup);

        var verifiedProxyGuid = Guid.NewGuid();

        var candGateway = new DiscoveredServerCandidate
        {
            Host = "10.0.0.1",
            Port = 22,
            Username = "admin",
            Password = "pw1",
            ProposedName = "Gateway Server",
            GroupId = parentGroup.Id,
            IsSelected = true
        };

        var candTarget = new DiscoveredServerCandidate
        {
            Host = "192.168.1.50",
            Port = 22,
            Username = "user",
            Password = "pw2",
            ProposedName = "Target Server",
            GroupId = parentGroup.Id,
            SubgroupName = "Sub Project",
            RequiredPreviousServerId = candGateway.Id,
            DiscoveredLinksFrom = [candGateway.Id],
            VerifiedProxyId = verifiedProxyGuid,
            PrivilegeEscalation = new PrivilegeEscalationResult
            {
                Method = "sudo",
                RootCommand = "sudo -i",
                RootPassword = "rootpw",
                Success = true
            },
            IsSelected = true
        };

        // 1. Creation with both gateway and target
        var created = SmartDiscoverySessionCreator.CreateSessions(config, [candGateway, candTarget]);
        Equal(2, created.Count);

        // Gateway in parentGroup
        Equal(true, parentGroup.Servers.Any(s => s.Id == candGateway.Id));

        // Target in child subgroup "Sub Project"
        var childSubgroup = parentGroup.Groups.Single(g => g.Name == "Sub Project");
        Equal(true, childSubgroup.Servers.Any(s => s.Id == candTarget.Id));

        var createdTarget = childSubgroup.Servers.Single(s => s.Id == candTarget.Id);
        Equal("sudo -i", createdTarget.RootLogin);
        Equal("sudo -i", KittyCredentialDecoder.NormalizeRootCommand(createdTarget.RootLogin));
        Equal("rootpw", createdTarget.RootPassword);
        Equal(candGateway.Id, createdTarget.RequiredPreviousServerId);
        Equal(verifiedProxyGuid, createdTarget.PreferredProxyId);

        // Directed link verification
        var link = config.Links.Single(l => l.FromServerId == candGateway.Id && l.ToServerId == candTarget.Id);
        Equal(true, link.Discovered);
        Equal(true, link.LastSuccessUtc != null);
        Equal(verifiedProxyGuid, link.LastSuccessfulProxyId);

        // 2. Group synchronization prioritizing edited GroupName (with Parent/Sub)
        var candEditedGroup = new DiscoveredServerCandidate
        {
            Host = "192.168.1.51",
            Port = 22,
            Username = "user",
            ProposedName = "Edited Group Server",
            GroupId = parentGroup.Id, // Stale group ID
            GroupName = "Custom Parent/Custom Sub", // User edited GroupName in UI
            IsSelected = true
        };
        var createdEdited = SmartDiscoverySessionCreator.CreateSessions(config, [candEditedGroup]);
        Equal(1, createdEdited.Count);
        var customParent = config.Groups.Single(g => g.Name == "Custom Parent");
        var customSub = customParent.Groups.Single(g => g.Name == "Custom Sub");
        Equal(true, customSub.Servers.Any(s => s.Id == candEditedGroup.Id));

        // 3. Explicit group clearing (empty/whitespace GroupName -> place in UngroupedServers)
        var candUngrouped = new DiscoveredServerCandidate
        {
            Host = "192.168.1.52",
            Port = 22,
            Username = "user",
            ProposedName = "Ungrouped Server",
            GroupId = parentGroup.Id, // Stale group ID
            GroupName = "   ", // Explicitly cleared by user
            IsSelected = true
        };
        var createdUngrouped = SmartDiscoverySessionCreator.CreateSessions(config, [candUngrouped]);
        Equal(1, createdUngrouped.Count);
        Equal(true, config.UngroupedServers.Any(s => s.Id == candUngrouped.Id));
        Equal(false, parentGroup.Servers.Any(s => s.Id == candUngrouped.Id));

        // 4. Duplicate protection on retry: calling CreateSessions again with already-created servers
        var duplicateCreated = SmartDiscoverySessionCreator.CreateSessions(config, [candGateway, candTarget, candEditedGroup, candUngrouped]);
        Equal(0, duplicateCreated.Count);
        Equal(4, config.AllServers().Count(s => s.Id == candGateway.Id || s.Id == candTarget.Id || s.Id == candEditedGroup.Id || s.Id == candUngrouped.Id));
        Equal(1, config.Links.Count(l => l.FromServerId == candGateway.Id && l.ToServerId == candTarget.Id));

        // 5. Missing gateway validation
        var orphanCand = new DiscoveredServerCandidate
        {
            Host = "172.16.1.1",
            ProposedName = "Orphan Server",
            RequiredPreviousServerId = Guid.NewGuid(), // Not in config and not in batch
            IsSelected = true
        };

        var thrown = false;
        try
        {
            SmartDiscoverySessionCreator.CreateSessions(config, [orphanCand]);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Equal(true, thrown);

        // 6. Direct connection proxy ID preservation: candidate with VerifiedProxyId = null
        // created with options containing a JumpHost must preserve PreferredProxyId = null and link proxy = null
        var candDirect = new DiscoveredServerCandidate
        {
            Host = "192.168.1.53",
            ProposedName = "Direct Server",
            VerifiedProxyId = null,
            DiscoveredLinksFrom = [candGateway.Id],
            IsSelected = true
        };
        var optionsWithJumpHost = new SmartDiscoveryOptions
        {
            JumpHost = new BaseProxy { Id = Guid.NewGuid(), Name = "Other JumpHost" }
        };
        var createdDirectList = SmartDiscoverySessionCreator.CreateSessions(config, [candDirect], optionsWithJumpHost);
        Equal(1, createdDirectList.Count);
        var createdDirect = createdDirectList[0];
        Equal(null, createdDirect.PreferredProxyId);
        var directLink = config.Links.Single(l => l.FromServerId == candGateway.Id && l.ToServerId == candDirect.Id);
        Equal(null, directLink.LastSuccessfulProxyId);

        // 7. Duplicate gateway: discovery and preview keep the verified candidate chain,
        // while creation reconciles the reused saved session (stale credentials and route).
        var duplicateGatewayConfig = new ManagerConfig();
        var jh = new BaseProxy { Id = Guid.NewGuid(), Name = "ValidJH", Enabled = true };
        duplicateGatewayConfig.BaseProxies.Add(jh);

        var savedUpstream = new ManagedServer
        {
            Name = "Saved Upstream", Host = "10.7.7.0", Port = 22, Username = "legacy", Password = "UPSTREAM"
        };
        duplicateGatewayConfig.UngroupedServers.Add(savedUpstream);
        var existingGateway = new ManagedServer
        {
            Name = "Existing Gateway", Host = "10.7.7.1", Port = 22, Username = "root", Password = "fresh",
            RequiredPreviousServerId = savedUpstream.Id,
            PreferredProxyId = jh.Id
        };
        duplicateGatewayConfig.UngroupedServers.Add(existingGateway);
        duplicateGatewayConfig.Links.Add(new ServerLink
        {
            FromServerId = savedUpstream.Id,
            ToServerId = existingGateway.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        });
        existingGateway.PreferredRoute = new CachedRoute
        {
            ProxyId = jh.Id,
            ServerIds = [savedUpstream.Id, existingGateway.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };

        var duplicateGatewayProbe = new TestSmartDiscoveryProbe();
        duplicateGatewayProbe.DirectlyReachable.Add("10.7.7.1");
        duplicateGatewayProbe.ProxyReachable[jh.Id] = ["10.7.7.1"];
        duplicateGatewayProbe.ValidCredentials["10.7.7.1"] = [("root", "fresh")];
        duplicateGatewayProbe.ValidCredentials["10.7.7.9"] = [("root", "pwB")];
        duplicateGatewayProbe.ValidCredentials["10.7.7.10"] = [("root", "pwC")];
        duplicateGatewayProbe.HopReachability.Add(("10.7.7.1", "10.7.7.9"));
        duplicateGatewayProbe.HopReachability.Add(("10.7.7.9", "10.7.7.10"));

        var duplicateGatewayOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.7.7.1", "10.7.7.9", "10.7.7.10"],
            Usernames = ["root"],
            Passwords = ["fresh", "pwB", "pwC"],
            JumpHost = jh,
            MaxParallelConnections = 1,
            MaxAttemptsPerHost = 3
        };

        var duplicateGatewayEngine = new SmartDiscoveryEngine(
            duplicateGatewayConfig, duplicateGatewayOptions, duplicateGatewayProbe);
        var duplicateGatewayResult = duplicateGatewayEngine.DiscoverAsync().GetAwaiter().GetResult();

        var duplicateGatewayCandidate = duplicateGatewayResult.Discovered.Single(c => c.Host == "10.7.7.1");
        var dependentCandidate = duplicateGatewayResult.Discovered.Single(c => c.Host == "10.7.7.9");
        var thirdHopCandidate = duplicateGatewayResult.Discovered.Single(c => c.Host == "10.7.7.10");
        Equal(true, duplicateGatewayCandidate.IsDuplicate);
        Equal(false, duplicateGatewayCandidate.IsSelected);
        Equal(existingGateway.Id, duplicateGatewayCandidate.ExistingServerId);
        Equal(duplicateGatewayCandidate.Id, dependentCandidate.RequiredPreviousServerId);
        Equal(duplicateGatewayCandidate.Id, dependentCandidate.DiscoveredLinksFrom.Single());
        Equal(dependentCandidate.Id, thirdHopCandidate.RequiredPreviousServerId);
        Equal(dependentCandidate.Id, thirdHopCandidate.DiscoveredLinksFrom.Single());

        var thirdHopChain = duplicateGatewayProbe.HopChains.Last();
        Equal(3, thirdHopChain.Count);
        Equal("10.7.7.0", thirdHopChain[0].Host);
        Equal("UPSTREAM", thirdHopChain[0].Password);
        Equal("10.7.7.1", thirdHopChain[1].Host);
        Equal("fresh", thirdHopChain[1].Password);
        Equal("10.7.7.9", thirdHopChain[2].Host);
        Equal("pwB", thirdHopChain[2].Password);

        var duplicateGatewayPreview = SmartDiscoverySessionCreator.CreatePreviewConfig(
            duplicateGatewayConfig, duplicateGatewayResult.Discovered);
        var previewCandidates = RoutePlanner.Candidates(duplicateGatewayPreview, thirdHopCandidate.Id);
        Equal(1, previewCandidates.Count);
        Equal(4, previewCandidates[0].Servers.Count);
        Equal(savedUpstream.Id, previewCandidates[0].Servers[0].Id);
        Equal(duplicateGatewayCandidate.Id, previewCandidates[0].Servers[1].Id);
        Equal("fresh", previewCandidates[0].Servers[1].Password);
        Equal(dependentCandidate.Id, previewCandidates[0].Servers[2].Id);
        Equal(thirdHopCandidate.Id, previewCandidates[0].Servers[3].Id);

        foreach (var c in duplicateGatewayResult.Discovered) c.IsSelected = !c.IsDuplicate;
        var duplicateGatewayCreated = SmartDiscoverySessionCreator.CreateSessions(
            duplicateGatewayConfig, duplicateGatewayResult.Discovered);
        Equal(2, duplicateGatewayCreated.Count);
        Equal(existingGateway.Id, duplicateGatewayCreated.Single(s => s.Host == "10.7.7.9").RequiredPreviousServerId);
        Equal(dependentCandidate.Id, duplicateGatewayCreated.Single(s => s.Host == "10.7.7.10").RequiredPreviousServerId);
        Equal("root", existingGateway.Username);
        Equal("fresh", existingGateway.Password);
        Equal(savedUpstream.Id, existingGateway.RequiredPreviousServerId);
        Equal(jh.Id, existingGateway.PreferredProxyId);
        Equal("UPSTREAM", savedUpstream.Password);
        Equal(true, duplicateGatewayConfig.Links.Any(
            l => l.FromServerId == existingGateway.Id && l.ToServerId == dependentCandidate.Id));
        Equal(true, duplicateGatewayConfig.Links.Any(
            l => l.FromServerId == dependentCandidate.Id && l.ToServerId == thirdHopCandidate.Id));

        // 7b. Negative scenario: Saved gateway G requires upstream U, but has no valid route to U.
        // When scanning targets G and T, G is identified as duplicate, but MUST NOT be used
        // as a direct gateway for T (no hop chains starting with G bypassing U), and T remains problematic.
        var noRouteGatewayConfig = new ManagerConfig();
        var missingUpstream = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Missing Upstream",
            Host = "10.88.0.1",
            Port = 22,
            Username = "root",
            Password = "pwU"
        };
        noRouteGatewayConfig.UngroupedServers.Add(missingUpstream);
        var unroutableGateway = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Unroutable Gateway",
            Host = "10.88.0.2",
            Port = 22,
            Username = "root",
            Password = "pwG",
            RequiredPreviousServerId = missingUpstream.Id
        };
        noRouteGatewayConfig.UngroupedServers.Add(unroutableGateway);

        var noRouteProbe = new TestSmartDiscoveryProbe();
        noRouteProbe.DirectlyReachable.Add("10.88.0.2");
        noRouteProbe.ValidCredentials["10.88.0.2"] = [("root", "pwG")];
        noRouteProbe.HopReachability.Add(("10.88.0.2", "10.88.0.3"));
        noRouteProbe.ValidCredentials["10.88.0.3"] = [("root", "pwT")];

        var noRouteOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.88.0.2", "10.88.0.3"],
            Usernames = ["root"],
            Passwords = ["pwG", "pwT"],
            MaxParallelConnections = 1,
            MaxAttemptsPerHost = 3
        };

        var noRouteEngine = new SmartDiscoveryEngine(noRouteGatewayConfig, noRouteOptions, noRouteProbe);
        var noRouteResult = noRouteEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, noRouteResult.Discovered.Count);
        Equal("10.88.0.2", noRouteResult.Discovered[0].Host);
        Equal(true, noRouteResult.Discovered[0].IsDuplicate);
        Equal(1, noRouteResult.Problematic.Count);
        Equal("10.88.0.3", noRouteResult.Problematic[0].Host);
        Equal(false, noRouteProbe.HopChains.Any(chain => chain.Count > 0 && chain[0].Host == "10.88.0.2"));

        // 7c. Disabled proxy scenario: saved gateway G has PreferredRoute through a disabled BaseProxy.
        // RoutePlanner rejects routes through disabled proxies. G must NOT be used as gateway for T,
        // no hop attempts starting with G, and T remains problematic.
        var disabledProxyConfig = new ManagerConfig();
        var disabledProxy = new BaseProxy { Id = Guid.NewGuid(), Name = "DisabledJH", Enabled = false };
        disabledProxyConfig.BaseProxies.Add(disabledProxy);
        var disabledUpstream = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Disabled Upstream",
            Host = "10.89.0.1",
            Port = 22,
            Username = "root",
            Password = "pwU"
        };
        disabledProxyConfig.UngroupedServers.Add(disabledUpstream);
        var disabledGatewayId = Guid.NewGuid();
        var disabledGateway = new ManagedServer
        {
            Id = disabledGatewayId,
            Name = "Disabled Gateway",
            Host = "10.89.0.2",
            Port = 22,
            Username = "root",
            Password = "pwG",
            RequiredPreviousServerId = disabledUpstream.Id,
            PreferredProxyId = disabledProxy.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = disabledProxy.Id,
                ServerIds = [disabledUpstream.Id, disabledGatewayId],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        disabledProxyConfig.UngroupedServers.Add(disabledGateway);
        disabledProxyConfig.Links.Add(new ServerLink
        {
            FromServerId = disabledUpstream.Id,
            ToServerId = disabledGateway.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        });

        var disabledProbe = new TestSmartDiscoveryProbe();
        disabledProbe.DirectlyReachable.Add("10.89.0.2");
        disabledProbe.ValidCredentials["10.89.0.2"] = [("root", "pwG")];
        disabledProbe.HopReachability.Add(("10.89.0.2", "10.89.0.3"));
        disabledProbe.ValidCredentials["10.89.0.3"] = [("root", "pwT")];

        var disabledOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.89.0.2", "10.89.0.3"],
            Usernames = ["root"],
            Passwords = ["pwG", "pwT"],
            MaxParallelConnections = 1,
            MaxAttemptsPerHost = 3
        };

        var disabledEngine = new SmartDiscoveryEngine(disabledProxyConfig, disabledOptions, disabledProbe);
        var disabledResult = disabledEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, disabledResult.Discovered.Count);
        Equal("10.89.0.2", disabledResult.Discovered[0].Host);
        Equal(true, disabledResult.Discovered[0].IsDuplicate);
        Equal(1, disabledResult.Problematic.Count);
        Equal("10.89.0.3", disabledResult.Problematic[0].Host);
        Equal(false, disabledProbe.HopChains.Any(chain => chain.Count > 0 && chain[0].Host == "10.89.0.2"));

        // 7d. Stale cache scenario: saved gateway G has PreferredRoute through an enabled proxy,
        // but the link U -> G is unconfirmed (LastSuccessUtc is null) and no other routes exist.
        // RoutePlanner.CandidateFromCached rejects unconfirmed links. G must NOT be used as gateway for T,
        // no hop attempts starting with G, and T remains problematic.
        var staleCacheConfig = new ManagerConfig();
        var staleProxy = new BaseProxy { Id = Guid.NewGuid(), Name = "StaleJH", Enabled = true };
        staleCacheConfig.BaseProxies.Add(staleProxy);
        var staleUpstream = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Stale Upstream",
            Host = "10.90.0.1",
            Port = 22,
            Username = "root",
            Password = "pwU"
        };
        staleCacheConfig.UngroupedServers.Add(staleUpstream);
        var staleGatewayId = Guid.NewGuid();
        var staleGateway = new ManagedServer
        {
            Id = staleGatewayId,
            Name = "Stale Gateway",
            Host = "10.90.0.2",
            Port = 22,
            Username = "root",
            Password = "pwG",
            RequiredPreviousServerId = staleUpstream.Id,
            PreferredProxyId = staleProxy.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = staleProxy.Id,
                ServerIds = [staleUpstream.Id, staleGatewayId],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        staleCacheConfig.UngroupedServers.Add(staleGateway);
        staleCacheConfig.Links.Add(new ServerLink
        {
            FromServerId = staleUpstream.Id,
            ToServerId = staleGateway.Id,
            LastSuccessUtc = null // Unconfirmed link!
        });

        var staleProbe = new TestSmartDiscoveryProbe();
        staleProbe.DirectlyReachable.Add("10.90.0.2");
        staleProbe.ValidCredentials["10.90.0.2"] = [("root", "pwG")];
        staleProbe.HopReachability.Add(("10.90.0.2", "10.90.0.3"));
        staleProbe.ValidCredentials["10.90.0.3"] = [("root", "pwT")];

        var staleOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.90.0.2", "10.90.0.3"],
            Usernames = ["root"],
            Passwords = ["pwG", "pwT"],
            MaxParallelConnections = 1,
            MaxAttemptsPerHost = 3
        };

        var staleEngine = new SmartDiscoveryEngine(staleCacheConfig, staleOptions, staleProbe);
        var staleResult = staleEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, staleResult.Discovered.Count);
        Equal("10.90.0.2", staleResult.Discovered[0].Host);
        Equal(true, staleResult.Discovered[0].IsDuplicate);
        Equal(1, staleResult.Problematic.Count);
        Equal("10.90.0.3", staleResult.Problematic[0].Host);
        Equal(false, staleProbe.HopChains.Any(chain => chain.Count > 0 && chain[0].Host == "10.90.0.2"));

        // 8. Reused duplicate in the middle: verified incoming link is registered and a route can be planned.
        var middleDuplicateConfig = new ManagerConfig();
        var savedMiddle = new ManagedServer
        {
            Name = "Saved Middle", Host = "10.8.8.1", Port = 22, Username = "legacy", Password = "STALE_MIDDLE",
            UseKeyboardInteractive = false
        };
        middleDuplicateConfig.UngroupedServers.Add(savedMiddle);
        var firstNew = new DiscoveredServerCandidate
        {
            Host = "10.8.8.0", ProposedName = "First", Username = "root", Password = "pwFirst"
        };
        var middleDuplicate = new DiscoveredServerCandidate
        {
            Host = "10.8.8.1", ProposedName = "Saved Middle", Username = "root", Password = "pwMiddle",
            IsDuplicate = true, IsSelected = false, ExistingServerId = savedMiddle.Id,
            RequiredPreviousServerId = firstNew.Id
        };
        middleDuplicate.DiscoveredLinksFrom.Add(firstNew.Id);
        var lastNew = new DiscoveredServerCandidate
        {
            Host = "10.8.8.2", ProposedName = "Last", Username = "root", Password = "pwLast",
            RequiredPreviousServerId = middleDuplicate.Id
        };
        lastNew.DiscoveredLinksFrom.Add(middleDuplicate.Id);

        var middleCreated = SmartDiscoverySessionCreator.CreateSessions(
            middleDuplicateConfig, [firstNew, middleDuplicate, lastNew]);
        Equal(2, middleCreated.Count);
        Equal(firstNew.Id, savedMiddle.RequiredPreviousServerId);
        Equal("pwMiddle", savedMiddle.Password);
        Equal(true, savedMiddle.UseKeyboardInteractive);
        Equal(true, middleDuplicateConfig.Links.Any(
            l => l.FromServerId == firstNew.Id && l.ToServerId == savedMiddle.Id && l.LastSuccessUtc is not null));
        Equal(true, middleDuplicateConfig.Links.Any(
            l => l.FromServerId == savedMiddle.Id && l.ToServerId == lastNew.Id && l.LastSuccessUtc is not null));
        var middlePaths = RoutePlanner.FindPaths(middleDuplicateConfig, lastNew.Id);
        Equal(true, middlePaths.Any(path => path.Select(s => s.Id).SequenceEqual([firstNew.Id, savedMiddle.Id, lastNew.Id])));

        // 9. Unselected new gateway behind a reused duplicate: the complete chain is validated before mutation.
        var brokenChainConfig = new ManagerConfig();
        var savedBrokenMiddle = new ManagedServer
        {
            Name = "Broken Middle", Host = "10.9.9.1", Port = 22, Username = "legacy", Password = "KEEP"
        };
        brokenChainConfig.UngroupedServers.Add(savedBrokenMiddle);
        var missingGateway = new DiscoveredServerCandidate
        {
            Host = "10.9.9.0", ProposedName = "Missing", Username = "root", Password = "pw", IsSelected = false
        };
        var brokenMiddle = new DiscoveredServerCandidate
        {
            Host = "10.9.9.1", ProposedName = "Broken Middle", Username = "root", Password = "pw",
            IsDuplicate = true, IsSelected = false, ExistingServerId = savedBrokenMiddle.Id,
            RequiredPreviousServerId = missingGateway.Id
        };
        brokenMiddle.DiscoveredLinksFrom.Add(missingGateway.Id);
        var brokenTarget = new DiscoveredServerCandidate
        {
            Host = "10.9.9.2", ProposedName = "Target", Username = "root", Password = "pw",
            RequiredPreviousServerId = brokenMiddle.Id
        };
        brokenTarget.DiscoveredLinksFrom.Add(brokenMiddle.Id);

        var rejected = false;
        try
        {
            SmartDiscoverySessionCreator.CreateSessions(
                brokenChainConfig, [missingGateway, brokenMiddle, brokenTarget]);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Equal(true, rejected);
        Equal(null, savedBrokenMiddle.RequiredPreviousServerId);
        Equal("KEEP", savedBrokenMiddle.Password);
        Equal(1, brokenChainConfig.AllServers().Count());
        Equal(0, brokenChainConfig.Links.Count);
        Equal(0, brokenChainConfig.Groups.Count);

        var planConfig = new ManagerConfig();
        var planRoot = new ServerGroup { Name = "Plan Root" };
        var planNested = new ServerGroup { Name = "Plan Nested" };
        planRoot.Groups.Add(planNested);
        planConfig.Groups.Add(planRoot);
        var planRootServer = new ManagedServer { Name = "Plan Root Server", Host = "10.40.0.1", Port = 22 };
        var planNestedServer = new ManagedServer { Name = "Plan Nested Server", Host = "10.40.0.2", Port = 22 };
        var planUngroupedServer = new ManagedServer { Name = "Plan Ungrouped Server", Host = "10.40.0.3", Port = 22 };
        planRoot.Servers.Add(planRootServer);
        planNested.Servers.Add(planNestedServer);
        planConfig.UngroupedServers.Add(planUngroupedServer);

        var editedDestinationCandidate = new DiscoveredServerCandidate
        {
            Host = "10.40.0.10",
            Port = 22,
            Username = "root",
            Password = "pw",
            ProposedName = "Edited Nested Destination",
            GroupId = planRoot.Id,
            GroupName = "Plan Root",
            SubgroupName = "Plan Nested",
            IsSelected = true
        };
        var editedDestination = SmartDiscoverySessionCreator.CreateSessions(
            planConfig, [editedDestinationCandidate]).Single();
        Equal(planNested.Id, planConfig.FindServerGroup(editedDestination.Id)?.Id);

        var ungroupedDestinationCandidate = new DiscoveredServerCandidate
        {
            Host = "10.40.0.11",
            Port = 22,
            Username = "root",
            Password = "pw",
            ProposedName = "New Ungrouped Destination",
            GroupId = planRoot.Id,
            GroupName = "   ",
            IsSelected = true
        };
        var ungroupedDestination = SmartDiscoverySessionCreator.CreateSessions(
            planConfig, [ungroupedDestinationCandidate]).Single();
        Equal(null, planConfig.FindServerGroup(ungroupedDestination.Id));

        var newlyCreatedSource = new ManagedServer { Name = "New Source", Host = "10.40.0.12", Port = 22 };
        planNested.Servers.Add(newlyCreatedSource);

        var independentByGroup = new Dictionary<Guid, IReadOnlyList<Guid>>
        {
            [planRoot.Id] = [planRootServer.Id],
            [planNested.Id] =
                [planNestedServer.Id, newlyCreatedSource.Id, editedDestination.Id],
            [Guid.Empty] = [planUngroupedServer.Id, ungroupedDestination.Id]
        };
        var createdIds = new[]
        {
            editedDestination.Id, ungroupedDestination.Id, newlyCreatedSource.Id
        };
        var plannedLinks = SmartDiscoverySessionCreator.PlanLinksToExistingGroupServers(
            planConfig, independentByGroup, createdIds);

        Equal(5, plannedLinks.Count);
        Equal(false, plannedLinks.Any(pair => createdIds.Contains(pair.SourceId)));
        Equal(true, plannedLinks.Any(pair => pair.SourceId == planRootServer.Id && pair.TargetId == editedDestination.Id));
        Equal(true, plannedLinks.Any(pair => pair.SourceId == planRootServer.Id && pair.TargetId == newlyCreatedSource.Id));
        Equal(true, plannedLinks.Any(pair =>
            pair.SourceId == planNestedServer.Id && pair.TargetId == editedDestination.Id &&
            pair.SourceName == planNestedServer.Name && pair.TargetName == editedDestination.Name));
        Equal(true, plannedLinks.Any(pair =>
            pair.SourceId == planNestedServer.Id && pair.TargetId == newlyCreatedSource.Id));
        Equal(true, plannedLinks.Any(pair =>
            pair.SourceId == planUngroupedServer.Id && pair.TargetId == ungroupedDestination.Id));
    }

    private static void AutoEntryPointSelectionTests()
    {
        var config = new ManagerConfig();
        var group = new ServerGroup { Name = "Target" };
        config.Groups.Add(group);

        var preferredProxy = new BaseProxy { Name = "Preferred", Host = "127.0.0.1", Port = 1081 };
        var otherProxy = new BaseProxy { Name = "Other", Host = "127.0.0.1", Port = 1082 };
        config.BaseProxies.AddRange([otherProxy, preferredProxy]);
        group.Servers.Add(new ManagedServer { Name = "Group Server", Host = "10.1.0.1", PreferredProxyId = preferredProxy.Id });

        // 1. A proxy preferred by a server in the target group is tried first
        var probe = new TestSmartDiscoveryProbe();
        probe.ProxyReachable[preferredProxy.Id] = ["10.9.0.1"];
        probe.ValidCredentials["10.9.0.1"] = [("root", "pw")];

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.0.1"],
            Usernames = ["root"],
            Passwords = ["pw"],
            TargetGroupId = group.Id,
            AutoSelectJumpHost = true,
            MaxParallelConnections = 1
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var result = engine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result.Discovered.Count);
        Equal(preferredProxy.Id, result.Discovered[0].VerifiedProxyId);
        Equal(1, probe.ReachabilityAttempts.Count);
        Equal(preferredProxy.Id, probe.ReachabilityAttempts[0].proxyId);

        // 2. Failover: the first preferred proxy is unreachable, the second works
        probe.ReachabilityAttempts.Clear();
        probe.ProxyReachable.Clear();
        probe.ProxyReachable[otherProxy.Id] = ["10.9.0.2"];
        probe.ValidCredentials["10.9.0.2"] = [("root", "pw")];

        var failoverOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.0.2"],
            Usernames = ["root"],
            Passwords = ["pw"],
            TargetGroupId = group.Id,
            AutoSelectJumpHost = true,
            MaxParallelConnections = 1
        };

        var failoverLines = new List<string>();
        var failoverEngine = new SmartDiscoveryEngine(config, failoverOptions, probe, log: failoverLines.Add);
        var failoverResult = failoverEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, failoverResult.Discovered.Count);
        Equal(otherProxy.Id, failoverResult.Discovered[0].VerifiedProxyId);
        Equal(2, probe.ReachabilityAttempts.Count);
        Equal(preferredProxy.Id, probe.ReachabilityAttempts[0].proxyId);
        Equal(otherProxy.Id, probe.ReachabilityAttempts[1].proxyId);
        Equal(true, failoverLines.Any(l => l.Contains(otherProxy.Name, StringComparison.Ordinal)));

        // 3. ensureEntryPoint is invoked once per proxy even with parallel hosts
        var managedConfig = new ManagerConfig();
        var startupServer = new ManagedServer { Name = "Startup", Host = "10.0.0.50" };
        managedConfig.UngroupedServers.Add(startupServer);
        var managedA = new BaseProxy { Name = "Managed A", Host = "127.0.0.1", Port = 1083, StartupServerId = startupServer.Id };
        var managedB = new BaseProxy { Name = "Managed B", Host = "127.0.0.1", Port = 1084, StartupServerId = startupServer.Id };
        managedConfig.BaseProxies.AddRange([managedA, managedB]);

        var managedProbe = new TestSmartDiscoveryProbe();
        managedProbe.ProxyReachable[managedA.Id] = ["10.9.1.1"];
        managedProbe.ProxyReachable[managedB.Id] = ["10.9.1.2", "10.9.1.3"];
        foreach (var host in new[] { "10.9.1.1", "10.9.1.2", "10.9.1.3" })
            managedProbe.ValidCredentials[host] = [("root", "pw")];

        var ensureCounts = new Dictionary<Guid, int>();
        Func<BaseProxy, CancellationToken, Task<bool>> ensureEntryPoint = (proxy, _) =>
        {
            lock (ensureCounts)
            {
                ensureCounts[proxy.Id] = ensureCounts.GetValueOrDefault(proxy.Id) + 1;
            }
            return Task.FromResult(true);
        };

        var managedOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.1.1", "10.9.1.2", "10.9.1.3"],
            Usernames = ["root"],
            Passwords = ["pw"],
            AutoSelectJumpHost = true,
            MaxParallelConnections = 3
        };

        var managedEngine = new SmartDiscoveryEngine(
            managedConfig, managedOptions, managedProbe, ensureEntryPoint: ensureEntryPoint);
        var managedResult = managedEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(3, managedResult.Discovered.Count);
        Equal(1, ensureCounts[managedA.Id]);
        Equal(1, ensureCounts[managedB.Id]);

        // 4. No enabled proxies -> direct connection
        var directConfig = new ManagerConfig();
        directConfig.BaseProxies.Add(new BaseProxy { Name = "Disabled", Enabled = false });
        var directProbe = new TestSmartDiscoveryProbe();
        directProbe.DirectlyReachable.Add("10.9.2.1");
        directProbe.ValidCredentials["10.9.2.1"] = [("root", "pw")];

        var directOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.2.1"],
            Usernames = ["root"],
            Passwords = ["pw"],
            AutoSelectJumpHost = true
        };

        var directEngine = new SmartDiscoveryEngine(directConfig, directOptions, directProbe);
        var directResult = directEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, directResult.Discovered.Count);
        Equal(null, directResult.Discovered[0].VerifiedProxyId);

        // 5. Auth failover: an external proxy is TCP-reachable but cannot authenticate,
        // so the managed proxy is started and wins
        var authFailoverConfig = new ManagerConfig();
        var authStartupServer = new ManagedServer { Name = "Bravo Startup", Host = "10.0.0.60" };
        authFailoverConfig.UngroupedServers.Add(authStartupServer);
        var alphaProxy = new BaseProxy { Name = "Alpha", Host = "127.0.0.1", Port = 1087 };
        var bravoProxy = new BaseProxy
        {
            Name = "Bravo", Host = "127.0.0.1", Port = 1088, StartupServerId = authStartupServer.Id
        };
        authFailoverConfig.BaseProxies.AddRange([alphaProxy, bravoProxy]);

        var authFailoverProbe = new TestSmartDiscoveryProbe();
        authFailoverProbe.ProxyReachable[alphaProxy.Id] = ["10.9.5.1"];
        authFailoverProbe.ProxyAuthUnreachable.Add((alphaProxy.Id, "10.9.5.1"));
        authFailoverProbe.ValidCredentials["10.9.5.1"] = [("root", "pw")];

        var authFailoverCounts = new Dictionary<Guid, int>();
        Func<BaseProxy, CancellationToken, Task<bool>> authFailoverEnsure = (proxy, _) =>
        {
            lock (authFailoverCounts)
            {
                authFailoverCounts[proxy.Id] = authFailoverCounts.GetValueOrDefault(proxy.Id) + 1;
            }
            return Task.FromResult(true);
        };

        var authFailoverOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.5.1"],
            Usernames = ["root"],
            Passwords = ["pw"],
            AutoSelectJumpHost = true,
            MaxParallelConnections = 1
        };

        var authFailoverEngine = new SmartDiscoveryEngine(
            authFailoverConfig, authFailoverOptions, authFailoverProbe, ensureEntryPoint: authFailoverEnsure);
        var authFailoverResult = authFailoverEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, authFailoverResult.Discovered.Count);
        Equal(bravoProxy.Id, authFailoverResult.Discovered[0].VerifiedProxyId);
        Equal(1, authFailoverCounts[bravoProxy.Id]);

        // 6. Duplicate detection: a host already present in the config is marked as a duplicate
        var duplicateConfig = new ManagerConfig();
        duplicateConfig.UngroupedServers.Add(new ManagedServer { Name = "Existing", Host = "10.9.6.1", Port = 22, Username = "root" });

        var duplicateProbe = new TestSmartDiscoveryProbe();
        duplicateProbe.DirectlyReachable.Add("10.9.6.1");
        duplicateProbe.DirectlyReachable.Add("10.9.6.2");
        duplicateProbe.ValidCredentials["10.9.6.1"] = [("root", "pw")];
        duplicateProbe.ValidCredentials["10.9.6.2"] = [("root", "pw")];

        var duplicateOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.6.1", "10.9.6.2"],
            Usernames = ["root"],
            Passwords = ["pw"],
            MaxParallelConnections = 1
        };

        var duplicateEngine = new SmartDiscoveryEngine(duplicateConfig, duplicateOptions, duplicateProbe);
        var duplicateResult = duplicateEngine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(2, duplicateResult.Discovered.Count);
        var duplicateCandidate = duplicateResult.Discovered.Single(c => c.Host == "10.9.6.1");
        var freshCandidate = duplicateResult.Discovered.Single(c => c.Host == "10.9.6.2");
        Equal(true, duplicateCandidate.IsDuplicate);
        Equal(false, duplicateCandidate.IsSelected);
        Equal(true, duplicateCandidate.StatusMessage.Contains("Дубль", StringComparison.Ordinal));
        Equal(false, freshCandidate.IsDuplicate);
        Equal(true, freshCandidate.IsSelected);
    }

    private static void ManagedEntryPointLinkTests()
    {
        var config = new ManagerConfig();
        var startupServer = new ManagedServer { Name = "Startup GW", Host = "10.0.0.1" };
        config.UngroupedServers.Add(startupServer);

        var managedProxy = new BaseProxy
        {
            Name = "Managed Proxy", Host = "127.0.0.1", Port = 1085, StartupServerId = startupServer.Id
        };
        config.BaseProxies.Add(managedProxy);

        var probe = new TestSmartDiscoveryProbe();
        probe.ProxyReachable[managedProxy.Id] = ["10.9.3.1"];
        probe.ValidCredentials["10.9.3.1"] = [("root", "pw")];

        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.3.1"],
            Usernames = ["root"],
            Passwords = ["pw"],
            AutoSelectJumpHost = true
        };

        var engine = new SmartDiscoveryEngine(config, options, probe);
        var result = engine.DiscoverAsync().GetAwaiter().GetResult();

        var candidate = result.Discovered.Single();
        Equal(managedProxy.Id, candidate.VerifiedProxyId);
        Equal(null, candidate.RequiredPreviousServerId);
        Equal(startupServer.Id, candidate.DiscoveredLinksFrom.Single());

        var created = SmartDiscoverySessionCreator.CreateSessions(config, [candidate]);
        Equal(1, created.Count);
        var link = config.Links.Single(l => l.FromServerId == startupServer.Id && l.ToServerId == candidate.Id);
        Equal(true, link.Discovered);
        Equal(managedProxy.Id, link.LastSuccessfulProxyId);
        Equal(true, ServerLinkPairPolicy.IsEntryPointLink(config, link));

        // An external proxy has no startup server, so no directed link is fabricated
        var externalProxy = new BaseProxy { Name = "External Proxy", Host = "127.0.0.1", Port = 1086 };
        config.BaseProxies.Clear();
        config.BaseProxies.Add(externalProxy);
        probe.ProxyReachable[externalProxy.Id] = ["10.9.3.2"];
        probe.ValidCredentials["10.9.3.2"] = [("root", "pw")];

        var externalOptions = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.3.2"],
            Usernames = ["root"],
            Passwords = ["pw"],
            AutoSelectJumpHost = true
        };

        var externalEngine = new SmartDiscoveryEngine(config, externalOptions, probe);
        var externalResult = externalEngine.DiscoverAsync().GetAwaiter().GetResult();

        var externalCandidate = externalResult.Discovered.Single();
        Equal(externalProxy.Id, externalCandidate.VerifiedProxyId);
        Equal(0, externalCandidate.DiscoveredLinksFrom.Count);

        var mapConfig = new ManagerConfig();
        var mapStartup = new ManagedServer { Name = "Map Startup", Host = "10.9.7.1" };
        var mapTarget = new ManagedServer { Name = "Map Target", Host = "10.9.7.2" };
        mapConfig.UngroupedServers.AddRange([mapStartup, mapTarget]);
        var mapProxy = new BaseProxy
        {
            Name = "Map Managed Proxy", Host = "127.0.0.1", Port = 1089, StartupServerId = mapStartup.Id
        };
        var mapExternalProxy = new BaseProxy { Name = "Map External Proxy", Host = "127.0.0.1", Port = 1090 };
        mapConfig.BaseProxies.AddRange([mapProxy, mapExternalProxy]);

        var mapEntryLink = new ServerLink
        {
            FromServerId = mapStartup.Id,
            ToServerId = mapTarget.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastSuccessfulProxyId = mapProxy.Id
        };
        var mapNormalLink = new ServerLink
        {
            FromServerId = mapTarget.Id,
            ToServerId = mapStartup.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        var mapExternalLink = new ServerLink
        {
            FromServerId = mapStartup.Id,
            ToServerId = mapTarget.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastSuccessfulProxyId = mapExternalProxy.Id
        };
        Equal(true, ServerLinkPairPolicy.IsEntryPointLink(mapConfig, mapEntryLink));
        Equal(false, ServerLinkPairPolicy.IsEntryPointLink(mapConfig, mapNormalLink));
        Equal(false, ServerLinkPairPolicy.IsEntryPointLink(mapConfig, mapExternalLink));

        mapConfig.Links.Add(mapEntryLink);
        var filteredWithoutReverse = mapConfig.Links
            .Where(link => !ServerLinkPairPolicy.IsEntryPointLink(mapConfig, link))
            .ToArray();
        Equal(0, LinkMapLayout.Build(mapConfig, filteredWithoutReverse).Edges.Count);

        mapConfig.Links.Add(mapNormalLink);
        var refreshedLinks = ServerLinkPairPolicy.ExcludeEntryPointLinks(mapConfig, mapConfig.Links);
        Equal(1, refreshedLinks.Count);
        Equal(mapNormalLink, refreshedLinks[0]);
        var filteredWithReverse = mapConfig.Links
            .Where(link => !ServerLinkPairPolicy.IsEntryPointLink(mapConfig, link))
            .ToArray();
        var filteredMap = LinkMapLayout.Build(mapConfig, filteredWithReverse);
        Equal(1, filteredMap.Edges.Count);
        Equal(true, filteredMap.Edges[0].IsDirected);
        Equal(true, filteredMap.Edges[0].AtoBAvailable ^ filteredMap.Edges[0].BtoAAvailable);

        // Re-checking the reverse direction through its startup proxy makes it an
        // entry-point link too; the filtered refresh result becomes empty.
        var mapTargetProxy = new BaseProxy
        {
            Name = "Map Target Proxy", Host = "127.0.0.1", Port = 1091, StartupServerId = mapTarget.Id
        };
        mapConfig.BaseProxies.Add(mapTargetProxy);
        mapNormalLink.LastSuccessfulProxyId = mapTargetProxy.Id;
        Equal(true, ServerLinkPairPolicy.IsEntryPointLink(mapConfig, mapNormalLink));
        Equal(0, ServerLinkPairPolicy.ExcludeEntryPointLinks(mapConfig, mapConfig.Links).Count);

        // The reverse check can later add an ordinary visible link; the refresh
        // result transitions from empty back to non-empty.
        var mapLateReverseLink = new ServerLink
        {
            FromServerId = mapTarget.Id,
            ToServerId = mapStartup.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        mapConfig.Links.Add(mapLateReverseLink);
        var refreshedAfterReverse = ServerLinkPairPolicy.ExcludeEntryPointLinks(mapConfig, mapConfig.Links);
        Equal(1, refreshedAfterReverse.Count);
        Equal(mapLateReverseLink, refreshedAfterReverse[0]);
    }

    private static void DiscoveryLoggingRedactionTests()
    {
        var config = new ManagerConfig();
        var probe = new TestSmartDiscoveryProbe();
        probe.DirectlyReachable.Add("10.9.4.1");
        probe.DirectlyReachable.Add("10.9.4.2");
        probe.ValidCredentials["10.9.4.1"] = [("root", "SENTINEL_LOGIN_PW")];
        probe.Escalations["10.9.4.1"] = new PrivilegeEscalationResult
        {
            Method = "sudo",
            RootCommand = "sudo -i",
            RootPassword = "SENTINEL_ROOT_PW",
            StatusDescription = "Root доступ подтверждён",
            Success = true
        };
        probe.AuthErrorMessage = "Неверные учетные данные: SENTINEL_LOGIN_PW";

        var lines = new List<string>();
        var options = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.9.4.1", "10.9.4.2"],
            Usernames = ["root"],
            Passwords = ["SENTINEL_LOGIN_PW"],
            MaxParallelConnections = 1
        };

        var engine = new SmartDiscoveryEngine(config, options, probe, log: lines.Add);
        var result = engine.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result.Discovered.Count);
        Equal(1, result.Problematic.Count);
        Equal(true, lines.Count > 0);
        Equal(true, lines.Any(l => l.Contains("Сканирование", StringComparison.Ordinal)));
        Equal(true, lines.Any(l => l.Contains("Фаза 1", StringComparison.Ordinal)));
        Equal(true, lines.Any(l => l.Contains("вход", StringComparison.Ordinal)));
        Equal(true, lines.Any(l => l.Contains("повышение прав", StringComparison.Ordinal)));
        Equal(false, lines.Any(l => l.Contains("SENTINEL_LOGIN_PW", StringComparison.Ordinal)));
        Equal(false, lines.Any(l => l.Contains("SENTINEL_ROOT_PW", StringComparison.Ordinal)));
    }

    private static void SmartDiscoveryPreviewConfigTests()
    {
        var source = new ManagerConfig();
        var sourceServer = new ManagedServer { Name = "Existing Server", Host = "10.0.0.1" };
        source.UngroupedServers.Add(sourceServer);

        var proxyId = Guid.NewGuid();
        var dependency = new DiscoveredServerCandidate
        {
            Host = "10.0.0.10",
            Port = 22,
            Username = "root",
            Password = "pw1",
            ProposedName = "Dependency",
            IsSelected = false
        };
        var dependent = new DiscoveredServerCandidate
        {
            Host = "10.0.0.11",
            Port = 22,
            Username = "admin",
            Password = "pw2",
            ProposedName = "Dependent",
            RequiredPreviousServerId = dependency.Id,
            DiscoveredLinksFrom = [dependency.Id],
            VerifiedProxyId = proxyId,
            PrivilegeEscalation = new PrivilegeEscalationResult
            {
                Method = "sudo",
                RootCommand = "sudo -i",
                RootPassword = "rootpw",
                Success = true
            },
            HostKeyFingerprint = "SHA256:abc",
            HostKeyAlgorithm = "ssh-rsa",
            HostKeyBits = 2048,
            IsSelected = true
        };

        var preview = SmartDiscoverySessionCreator.CreatePreviewConfig(source, [dependency, dependent]);

        // The source config is not mutated
        Equal(1, source.AllServers().Count());
        Equal(0, source.Links.Count);

        // All candidates become draft servers, including unselected dependencies
        Equal(3, preview.AllServers().Count());
        var draftDependency = preview.FindServer(dependency.Id);
        var draftDependent = preview.FindServer(dependent.Id);
        Equal(true, draftDependency != null);
        Equal(true, draftDependent != null);

        Equal("sudo -i", draftDependent!.RootLogin);
        Equal("rootpw", draftDependent.RootPassword);
        Equal("SHA256:abc", draftDependent.HostKeyFingerprint);
        Equal("ssh-rsa", draftDependent.HostKeyAlgorithm);
        Equal(2048, draftDependent.HostKeyBits);
        Equal(proxyId, draftDependent.PreferredProxyId);
        Equal(dependency.Id, draftDependent.RequiredPreviousServerId);

        var link = preview.Links.Single(l => l.FromServerId == dependency.Id && l.ToServerId == dependent.Id);
        Equal(true, link.Discovered);
        Equal("DirectTcpIp", link.LastStrategy);
        Equal(proxyId, link.LastSuccessfulProxyId);
        Equal(true, link.LastSuccessUtc != null);

        // Mutating the snapshot does not change the source config
        var previewSource = preview.FindServer(sourceServer.Id);
        Equal(true, previewSource != null);
        previewSource!.Name = "Mutated";
        preview.Links.Clear();
        Equal("Existing Server", source.UngroupedServers[0].Name);

        // CreateDraftServer preserves the persisted fields
        var draft = SmartDiscoverySessionCreator.CreateDraftServer(dependent);
        Equal(dependent.Id, draft.Id);
        Equal("Dependent", draft.Name);
        Equal("sudo -i", draft.RootLogin);
        Equal("rootpw", draft.RootPassword);
        Equal("SHA256:abc", draft.HostKeyFingerprint);
        Equal("ssh-rsa", draft.HostKeyAlgorithm);
        Equal(2048, draft.HostKeyBits);
        Equal(proxyId, draft.PreferredProxyId);
        Equal(dependency.Id, draft.RequiredPreviousServerId);
        Equal(15, draft.KeepaliveIntervalSeconds);
        Equal(true, draft.UseKeyboardInteractive);

        // A directly discovered candidate drafts as "try direct without jumphost"
        var directCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.12",
            Port = 22,
            Username = "root",
            Password = "pw3",
            ProposedName = "Direct",
            IsSelected = true
        };
        var directDraft = SmartDiscoverySessionCreator.CreateDraftServer(directCandidate);
        Equal(true, directDraft.TryDirectWithoutJumphost);
        preview.UngroupedServers.Add(directDraft);
        var directCandidates = RoutePlanner.Candidates(preview, directDraft.Id);
        Equal(1, directCandidates.Count);
        Equal(true, directCandidates[0].WithoutProxy);
        Equal(1, directCandidates[0].Servers.Count);

        // A proxy-free multi-hop chain is previewed as an explicit candidate
        var gatewayCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.13",
            Port = 22,
            Username = "root",
            Password = "pw4",
            ProposedName = "Gateway",
            IsSelected = true
        };
        var multiHopCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.14",
            Port = 22,
            Username = "root",
            Password = "pw5",
            ProposedName = "MultiHop",
            RequiredPreviousServerId = gatewayCandidate.Id,
            DiscoveredLinksFrom = [gatewayCandidate.Id],
            IsSelected = true
        };
        var multiHopPreview = SmartDiscoverySessionCreator.CreatePreviewConfig(
            source, [gatewayCandidate, multiHopCandidate]);
        var multiHopResult = SmartDiscoverySessionCreator.CreatePreviewCandidates(multiHopPreview, multiHopCandidate);
        Equal(1, multiHopResult.Count);
        Equal(true, multiHopResult[0].WithoutProxy);
        Equal(2, multiHopResult[0].Servers.Count);
        Equal(gatewayCandidate.Id, multiHopResult[0].Servers[0].Id);
        Equal(multiHopCandidate.Id, multiHopResult[0].Servers[1].Id);

        // Proxied candidates never use the proxy-free preview chain
        Equal(0, SmartDiscoverySessionCreator.CreatePreviewCandidates(preview, dependent).Count);

        // Group assignment helper: nested groups resolve to root name + subgroup sections
        var nestedConfig = new ManagerConfig();
        var regionGroup = new ServerGroup { Name = "Region" };
        var subGroup = new ServerGroup { Name = "Sub" };
        regionGroup.Groups.Add(subGroup);
        nestedConfig.Groups.Add(regionGroup);

        var (rootName, subgroupPath) = SmartDiscoverySessionCreator.ResolveGroupAssignment(nestedConfig, subGroup.Id, null);
        Equal("Region", rootName);
        Equal("Sub", subgroupPath);

        var (extraRootName, extraSubgroupPath) = SmartDiscoverySessionCreator.ResolveGroupAssignment(nestedConfig, subGroup.Id, "Extra");
        Equal("Region", extraRootName);
        Equal("Sub / Extra", extraSubgroupPath);

        var (topRootName, topSubgroupPath) = SmartDiscoverySessionCreator.ResolveGroupAssignment(nestedConfig, regionGroup.Id, "Sub");
        Equal("Region", topRootName);
        Equal("Sub", topSubgroupPath);

        var (noneRootName, noneSubgroupPath) = SmartDiscoverySessionCreator.ResolveGroupAssignment(nestedConfig, null, "X");
        Equal(null, noneRootName);
        Equal("X", noneSubgroupPath);

        // CreateSessions walks a multi-level subgroup chain "Sub / Extra"
        var nestedCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.20",
            Port = 22,
            Username = "root",
            Password = "pw6",
            ProposedName = "Nested Server",
            GroupId = subGroup.Id,
            GroupName = "Region",
            SubgroupName = "Sub / Extra",
            IsSelected = true
        };
        var nestedCreated = SmartDiscoverySessionCreator.CreateSessions(nestedConfig, [nestedCandidate]);
        Equal(1, nestedCreated.Count);
        var nestedExtra = regionGroup.Groups.Single(g => g.Name == "Sub").Groups.Single(g => g.Name == "Extra");
        Equal(true, nestedExtra.Servers.Any(s => s.Id == nestedCandidate.Id));

        // Duplicate root names: the selected group's ID ancestry wins over a same-named root
        var duplicateRootsConfig = new ManagerConfig();
        var firstRegion = new ServerGroup { Name = "Region" };
        var firstRegionSub = new ServerGroup { Name = "Sub" };
        firstRegion.Groups.Add(firstRegionSub);
        var secondRegion = new ServerGroup { Name = "Region" };
        var secondRegionSub = new ServerGroup { Name = "Sub" };
        secondRegion.Groups.Add(secondRegionSub);
        duplicateRootsConfig.Groups.AddRange([firstRegion, secondRegion]);

        var duplicateRootCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.30",
            Port = 22,
            Username = "root",
            Password = "pw7",
            ProposedName = "Duplicate Root Server",
            GroupId = secondRegionSub.Id,
            GroupName = "Region",
            SubgroupName = "Sub",
            IsSelected = true
        };
        var duplicateRootCreated = SmartDiscoverySessionCreator.CreateSessions(duplicateRootsConfig, [duplicateRootCandidate]);
        Equal(1, duplicateRootCreated.Count);
        Equal(false, firstRegionSub.Servers.Any(s => s.Id == duplicateRootCandidate.Id));
        Equal(true, secondRegionSub.Servers.Any(s => s.Id == duplicateRootCandidate.Id));

        // Duplicate sibling names: the selected group's ID ancestry wins over a same-named sibling
        var duplicateSiblingsConfig = new ManagerConfig();
        var siblingsRoot = new ServerGroup { Name = "Region" };
        var firstSiblingSub = new ServerGroup { Name = "Sub" };
        var secondSiblingSub = new ServerGroup { Name = "Sub" };
        siblingsRoot.Groups.AddRange([firstSiblingSub, secondSiblingSub]);
        duplicateSiblingsConfig.Groups.Add(siblingsRoot);

        var duplicateSiblingCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.31",
            Port = 22,
            Username = "root",
            Password = "pw8",
            ProposedName = "Duplicate Sibling Server",
            GroupId = secondSiblingSub.Id,
            GroupName = "Region",
            SubgroupName = "Sub",
            IsSelected = true
        };
        var duplicateSiblingCreated = SmartDiscoverySessionCreator.CreateSessions(duplicateSiblingsConfig, [duplicateSiblingCandidate]);
        Equal(1, duplicateSiblingCreated.Count);
        Equal(false, firstSiblingSub.Servers.Any(s => s.Id == duplicateSiblingCandidate.Id));
        Equal(true, secondSiblingSub.Servers.Any(s => s.Id == duplicateSiblingCandidate.Id));

        // Root password display mirrors login password masking
        var rootPasswordCandidate = new DiscoveredServerCandidate
        {
            Host = "10.0.0.21",
            PrivilegeEscalation = new() { RootPassword = "rootpw" }
        };
        Equal(new string('•', 6), rootPasswordCandidate.DisplayRootPassword);
        rootPasswordCandidate.ShowRootPassword = true;
        Equal("rootpw", rootPasswordCandidate.DisplayRootPassword);
    }

    private static void SmartDiscoveryIssue26EnhancementsTests()
    {
        // 1. Fast duplicate detection by (Host, Username):
        // Zero network attempts for hosts with matching (Host, Username) in config
        var config1 = new ManagerConfig();
        var existing1 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Existing Server 1",
            Host = "192.168.10.5",
            Port = 22,
            Username = "admin",
            Password = "pw1"
        };
        config1.UngroupedServers.Add(existing1);

        var probe1 = new TestSmartDiscoveryProbe();
        // probe1 has NO directly reachable hosts or credentials configured

        var options1 = new SmartDiscoveryOptions
        {
            TargetHosts = ["192.168.10.5"],
            Usernames = ["admin"],
            Passwords = ["anyPass"]
        };

        var engine1 = new SmartDiscoveryEngine(config1, options1, probe1);
        var result1 = engine1.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result1.Discovered.Count);
        Equal(0, result1.Problematic.Count);
        var cand1 = result1.Discovered[0];
        Equal(true, cand1.IsDuplicate);
        Equal(false, cand1.IsSelected);
        Equal(existing1.Name, cand1.ProposedName);
        Equal(existing1.Id, cand1.ExistingServerId);
        Equal("Дубль: сессия уже сохранена в программе", cand1.StatusMessage);
        Equal(0, probe1.ReachabilityAttempts.Count);
        Equal(0, probe1.AttemptedLogins.Count);

        // 2. Mixed usernames: duplicate username skipped without probe, new username probed over network
        var config2 = new ManagerConfig();
        var existing2 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Existing Server 2",
            Host = "192.168.10.6",
            Port = 22,
            Username = "admin",
            Password = "pw2"
        };
        config2.UngroupedServers.Add(existing2);

        var probe2 = new TestSmartDiscoveryProbe();
        probe2.DirectlyReachable.Add("192.168.10.6");
        probe2.ValidCredentials["192.168.10.6"] = [("deployer", "pwDeploy")];

        var options2 = new SmartDiscoveryOptions
        {
            TargetHosts = ["192.168.10.6"],
            Usernames = ["admin", "deployer"],
            Passwords = ["pwDeploy"]
        };

        var engine2 = new SmartDiscoveryEngine(config2, options2, probe2);
        var result2 = engine2.DiscoverAsync().GetAwaiter().GetResult();

        Equal(2, result2.Discovered.Count);
        var dupCand = result2.Discovered.Single(c => c.IsDuplicate);
        var newCand = result2.Discovered.Single(c => !c.IsDuplicate);

        Equal("admin", dupCand.Username);
        Equal(existing2.Name, dupCand.ProposedName);
        Equal(existing2.Id, dupCand.ExistingServerId);
        Equal("Дубль: сессия уже сохранена в программе", dupCand.StatusMessage);
        Equal(false, dupCand.IsSelected);

        Equal("deployer", newCand.Username);
        Equal(true, newCand.IsSelected);
        Equal(false, newCand.IsDuplicate);
        Equal(null, newCand.ExistingServerId);

        // probe2 attempted logins only for deployer, never for admin!
        Equal(true, probe2.AttemptedLogins.All(l => l.user == "deployer"));
        Equal(false, probe2.AttemptedLogins.Any(l => l.user == "admin"));

        // 3. PreferGroupGateways option:
        // When enabled with TargetGroupId: gateways probed first!
        var config3 = new ManagerConfig();
        var group3 = new ServerGroup { Name = "Branch" };
        var gateway3 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Branch Gateway",
            Host = "10.20.0.1",
            Port = 22,
            Username = "gwuser",
            Password = "gwpw",
            TryDirectWithoutJumphost = true
        };
        group3.Servers.Add(gateway3);
        config3.Groups.Add(group3);

        var probe3 = new TestSmartDiscoveryProbe();
        // 10.20.0.50 is reachable via gateway3
        probe3.HopReachability.Add(("10.20.0.1", "10.20.0.50"));
        probe3.ValidCredentials["10.20.0.50"] = [("root", "pw50")];

        var options3 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.20.0.50"],
            Usernames = ["root"],
            Passwords = ["pw50"],
            TargetGroupId = group3.Id,
            PreferGroupGateways = true
        };

        var engine3 = new SmartDiscoveryEngine(config3, options3, probe3);
        var result3 = engine3.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result3.Discovered.Count);
        var foundThroughGw = result3.Discovered[0];
        Equal(gateway3.Id, foundThroughGw.RequiredPreviousServerId);
        Equal(true, foundThroughGw.StatusMessage.Contains("Обнаружен через шлюз"));
        // Direct reachability was never attempted because gateway succeeded first!
        Equal(0, probe3.ReachabilityAttempts.Count);

        // 4. PreferGroupGateways fallback:
        // Host unreachable through gateway probes direct second!
        var config4 = new ManagerConfig();
        var group4 = new ServerGroup { Name = "Branch4" };
        var gateway4 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Branch4 Gateway",
            Host = "10.20.0.2",
            Port = 22,
            Username = "gwuser",
            Password = "gwpw",
            TryDirectWithoutJumphost = true
        };
        group4.Servers.Add(gateway4);
        config4.Groups.Add(group4);

        var probe4 = new TestSmartDiscoveryProbe();
        // 10.20.0.60 is NOT reachable via gateway, but IS directly reachable
        probe4.DirectlyReachable.Add("10.20.0.60");
        probe4.ValidCredentials["10.20.0.60"] = [("root", "pw60")];

        var options4 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.20.0.60"],
            Usernames = ["root"],
            Passwords = ["pw60"],
            TargetGroupId = group4.Id,
            PreferGroupGateways = true
        };

        var engine4 = new SmartDiscoveryEngine(config4, options4, probe4);
        var result4 = engine4.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result4.Discovered.Count);
        var fallbackCand = result4.Discovered[0];
        Equal("10.20.0.60", fallbackCand.Host);
        Equal(null, fallbackCand.RequiredPreviousServerId);
        // Gateway was attempted first, then direct reachability
        Equal(true, probe4.BatchHopAttempts.Any(b => b.TargetHosts.Contains("10.20.0.60")));
        Equal(true, probe4.ReachabilityAttempts.Any(r => r.host == "10.20.0.60"));

        // 5. InitialGatewayServerId priority:
        // When InitialGatewayServerId is set, it is tested first with distance -1
        var config5 = new ManagerConfig();
        var group5 = new ServerGroup { Name = "Branch5" };
        var serverA = new ManagedServer { Id = Guid.NewGuid(), Name = "Server A", Host = "10.30.0.1", Port = 22, TryDirectWithoutJumphost = true };
        var serverB = new ManagedServer { Id = Guid.NewGuid(), Name = "Server B", Host = "10.30.0.2", Port = 22, TryDirectWithoutJumphost = true };
        var serverC = new ManagedServer { Id = Guid.NewGuid(), Name = "Server C", Host = "10.30.0.3", Port = 22, TryDirectWithoutJumphost = true };
        group5.Servers.AddRange([serverA, serverB, serverC]);
        config5.Groups.Add(group5);

        var probe5 = new TestSmartDiscoveryProbe();
        probe5.HopReachability.Add(("10.30.0.3", "10.30.0.99"));
        probe5.ValidCredentials["10.30.0.99"] = [("root", "pw99")];

        var options5 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.30.0.99"],
            Usernames = ["root"],
            Passwords = ["pw99"],
            TargetGroupId = group5.Id,
            PreferGroupGateways = true,
            InitialGatewayServerId = serverC.Id
        };

        var engine5 = new SmartDiscoveryEngine(config5, options5, probe5);
        var result5 = engine5.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result5.Discovered.Count);
        Equal(serverC.Id, result5.Discovered[0].RequiredPreviousServerId);
        // Verify serverC was tried first
        Equal("10.30.0.3", probe5.HopChains[0][0].Host);

        // 6. Persistent gateway session probe:
        // Verify that OpenGatewaySessionAsync is used and held during batch probing and auth
        var sessionCreated = 0;
        var sessionDisposed = 0;
        var persistentProbe = new PersistentSessionMockProbe
        {
            OnSessionCreate = () => sessionCreated++,
            OnSessionDispose = () => sessionDisposed++
        };
        persistentProbe.HopReachability.Add(("10.40.0.1", "10.40.0.10"));
        persistentProbe.ValidCredentials["10.40.0.10"] = [("root", "pw10")];

        var config6 = new ManagerConfig();
        var group6 = new ServerGroup { Name = "Branch6" };
        var gw6 = new ManagedServer { Id = Guid.NewGuid(), Name = "GW6", Host = "10.40.0.1", Port = 22, TryDirectWithoutJumphost = true };
        group6.Servers.Add(gw6);
        config6.Groups.Add(group6);

        var options6 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.40.0.10"],
            Usernames = ["root"],
            Passwords = ["pw10"],
            TargetGroupId = group6.Id,
            PreferGroupGateways = true
        };

        var engine6 = new SmartDiscoveryEngine(config6, options6, persistentProbe);
        var result6 = engine6.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result6.Discovered.Count);
        Equal(1, sessionCreated);
        Equal(1, sessionDisposed);

        // 7. Duplicate uses saved ID and does not bypass RoutePlanner:
        var config7 = new ManagerConfig();
        var proxy7 = new BaseProxy { Name = "Proxy7", Host = "10.55.0.1", Port = 22 };
        config7.BaseProxies.Add(proxy7);
        var server7 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Server7",
            Host = "10.55.0.10",
            Port = 22,
            Username = "root",
            Password = "pw7",
            TryDirectWithoutJumphost = false
        };
        server7.PreferredRoute = new CachedRoute
        {
            ProxyId = proxy7.Id,
            ServerIds = [server7.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        config7.UngroupedServers.Add(server7);

        var probe7 = new TestSmartDiscoveryProbe();
        var options7 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.55.0.10"],
            Usernames = ["root"],
            Passwords = ["pw7"]
        };
        var engine7 = new SmartDiscoveryEngine(config7, options7, probe7);
        var result7 = engine7.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result7.Discovered.Count);
        var dup7 = result7.Discovered[0];
        Equal(true, dup7.IsDuplicate);
        Equal(server7.Id, dup7.Id); // Uses saved ID!
        Equal(server7.Id, dup7.ExistingServerId);
        Equal(proxy7.Id, dup7.VerifiedProxyId); // Validated route from RoutePlanner!
        Equal(0, probe7.AttemptedLogins.Count); // 0 network attempts!

        // 8. Host B discovered through host A found during direct probe in PreferGroupGateways:
        var config8 = new ManagerConfig();
        var group8 = new ServerGroup { Name = "Branch8" };
        var initialGw8 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "InitialGw8",
            Host = "10.60.0.1",
            Port = 22,
            Username = "gw",
            Password = "pw",
            TryDirectWithoutJumphost = true
        };
        group8.Servers.Add(initialGw8);
        config8.Groups.Add(group8);

        var probe8 = new TestSmartDiscoveryProbe();
        // Initial gateway cannot reach either Host A or Host B
        // Host A is directly reachable
        probe8.DirectlyReachable.Add("10.60.0.10");
        probe8.ValidCredentials["10.60.0.10"] = [("root", "pwA")];
        // Host B is reachable ONLY through Host A
        probe8.HopReachability.Add(("10.60.0.10", "10.60.0.20"));
        probe8.ValidCredentials["10.60.0.20"] = [("root", "pwB")];

        var options8 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.60.0.10", "10.60.0.20"],
            Usernames = ["root"],
            Passwords = ["pwA", "pwB"],
            TargetGroupId = group8.Id,
            PreferGroupGateways = true
        };
        var engine8 = new SmartDiscoveryEngine(config8, options8, probe8);
        var result8 = engine8.DiscoverAsync().GetAwaiter().GetResult();

        Equal(2, result8.Discovered.Count);
        Equal(0, result8.Problematic.Count);
        var candA = result8.Discovered.Single(c => c.Host == "10.60.0.10");
        var candB = result8.Discovered.Single(c => c.Host == "10.60.0.20");
        Equal(null, candA.RequiredPreviousServerId);
        Equal(candA.Id, candB.RequiredPreviousServerId); // B discovered through newly direct A!

        // 9. Confirmed credentials do not reintroduce excluded duplicate username:
        var config9 = new ManagerConfig();
        var existingOnHost2 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Host2-Root",
            Host = "10.70.0.2",
            Port = 22,
            Username = "root",
            Password = "pwRoot"
        };
        config9.UngroupedServers.Add(existingOnHost2);

        var probe9 = new TestSmartDiscoveryProbe();
        probe9.DirectlyReachable.Add("10.70.0.1");
        probe9.DirectlyReachable.Add("10.70.0.2");
        probe9.ValidCredentials["10.70.0.1"] = [("root", "pwRoot")];
        probe9.ValidCredentials["10.70.0.2"] = [("user2", "pwUser2")];

        var options9 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.70.0.1", "10.70.0.2"],
            Usernames = ["root", "user2"],
            Passwords = ["pwRoot", "pwUser2"]
        };
        var engine9 = new SmartDiscoveryEngine(config9, options9, probe9);
        var result9 = engine9.DiscoverAsync().GetAwaiter().GetResult();

        // Host 2 must NEVER have been probed with root (root was excluded by fast duplicate check)
        Equal(false, probe9.AttemptedLogins.Any(l => l.host == "10.70.0.2" && l.user == "root"));
        Equal(true, probe9.AttemptedLogins.Any(l => l.host == "10.70.0.2" && l.user == "user2"));

        // 10. New username on existing host creates a new candidate (IsDuplicate = false):
        var config10 = new ManagerConfig();
        var existing10 = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Existing10",
            Host = "10.80.0.5",
            Port = 22,
            Username = "admin",
            Password = "pwAdmin"
        };
        config10.UngroupedServers.Add(existing10);

        var probe10 = new TestSmartDiscoveryProbe();
        probe10.DirectlyReachable.Add("10.80.0.5");
        probe10.ValidCredentials["10.80.0.5"] = [("deployer", "pwDeploy")];

        var options10 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.80.0.5"],
            Usernames = ["deployer"],
            Passwords = ["pwDeploy"]
        };
        var engine10 = new SmartDiscoveryEngine(config10, options10, probe10);
        var result10 = engine10.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result10.Discovered.Count);
        var cand10 = result10.Discovered[0];
        Equal("deployer", cand10.Username);
        Equal(false, cand10.IsDuplicate); // NOT a duplicate!
        Equal(null, cand10.ExistingServerId);
        Equal(true, cand10.IsSelected);

        // 11. AutoSelectGatewayForGroup selects independent server in parent group when child group has no independent server:
        var config11 = new ManagerConfig();
        var rootGroup = new ServerGroup { Name = "RootGroup" };
        var subGroup = new ServerGroup { Name = "SubGroup" };
        rootGroup.Groups.Add(subGroup);
        config11.Groups.Add(rootGroup);

        var rootGw = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "RootGW",
            Host = "10.90.0.1",
            Port = 22,
            TryDirectWithoutJumphost = true
        };
        rootGw.PreferredRoute = new CachedRoute
        {
            ProxyId = Guid.Empty,
            ServerIds = [rootGw.Id],
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        rootGroup.Servers.Add(rootGw);

        var subServer = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "SubServer",
            Host = "10.90.0.2",
            Port = 22,
            RequiredPreviousServerId = rootGw.Id
        };
        subGroup.Servers.Add(subServer);

        var selectedId = SmartDiscoveryEngine.ResolveAutoSelectGatewayId(config11, subGroup.Id);
        Equal(rootGw.Id, selectedId); // Must select independent server from parent group!

        // 12. Saved session with empty login does not make new login 'deployer' a duplicate:
        var config12 = new ManagerConfig();
        var existingEmptyUser = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "EmptyUserHost",
            Host = "10.85.0.5",
            Port = 22,
            Username = "", // Empty login in saved session
            Password = ""
        };
        config12.UngroupedServers.Add(existingEmptyUser);

        var probe12 = new TestSmartDiscoveryProbe();
        probe12.DirectlyReachable.Add("10.85.0.5");
        probe12.ValidCredentials["10.85.0.5"] = [("deployer", "pwDeploy")];

        var options12 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.85.0.5"],
            Usernames = ["deployer"],
            Passwords = ["pwDeploy"]
        };
        var engine12 = new SmartDiscoveryEngine(config12, options12, probe12);
        var result12 = engine12.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result12.Discovered.Count);
        Equal(0, result12.Problematic.Count);
        var cand12 = result12.Discovered[0];
        Equal("deployer", cand12.Username);
        Equal(false, cand12.IsDuplicate); // Must NOT be treated as duplicate of empty-login session
        Equal(null, cand12.ExistingServerId);
        Equal(true, cand12.IsSelected);

        // 13. Strict case-sensitive separation between logins 'admin' and 'Admin':
        // Two hosts with logins 'admin' and 'Admin' must not cause conflicts or false credential matching.
        var config13 = new ManagerConfig();
        var existingAdmin = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Existing-admin-server",
            Host = "10.86.0.1",
            Port = 22,
            Username = "admin",
            Password = "pw1"
        };
        config13.UngroupedServers.Add(existingAdmin);

        var probe13 = new TestSmartDiscoveryProbe();
        probe13.DirectlyReachable.Add("10.86.0.1");
        probe13.DirectlyReachable.Add("10.86.0.2");
        probe13.ValidCredentials["10.86.0.1"] = [("admin", "pw1")];
        probe13.ValidCredentials["10.86.0.2"] = [("Admin", "pw2")];

        // 13a: 10.86.0.1 with "Admin" (different case) is NOT treated as duplicate of existing "admin"
        var options13a = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.86.0.1"],
            Usernames = ["Admin"],
            Passwords = ["pw1"]
        };
        var engine13a = new SmartDiscoveryEngine(config13, options13a, probe13);
        var result13a = engine13a.DiscoverAsync().GetAwaiter().GetResult();
        Equal(true, probe13.AttemptedLogins.Any(l => l.host == "10.86.0.1" && l.user == "Admin"));

        // 13b: Multi-host discovery with both 'admin' and 'Admin':
        // Both hosts discovered with their exact case-sensitive usernames without conflicts.
        var probe13b = new TestSmartDiscoveryProbe();
        probe13b.DirectlyReachable.Add("10.86.0.1");
        probe13b.DirectlyReachable.Add("10.86.0.2");
        probe13b.ValidCredentials["10.86.0.1"] = [("admin", "pw1")];
        probe13b.ValidCredentials["10.86.0.2"] = [("Admin", "pw2")];

        var options13b = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.86.0.1", "10.86.0.2"],
            Usernames = ["admin", "Admin"],
            Passwords = ["pw1", "pw2"],
            MaxParallelConnections = 1
        };
        var engine13b = new SmartDiscoveryEngine(new ManagerConfig(), options13b, probe13b);
        var result13b = engine13b.DiscoverAsync().GetAwaiter().GetResult();

        Equal(2, result13b.Discovered.Count);
        Equal(0, result13b.Problematic.Count);
        var candLower = result13b.Discovered.Single(c => c.Host == "10.86.0.1");
        var candUpper = result13b.Discovered.Single(c => c.Host == "10.86.0.2");
        Equal("admin", candLower.Username);
        Equal("Admin", candUpper.Username);
        Equal(false, candLower.IsDuplicate);
        Equal(false, candUpper.IsDuplicate);

        // 13c: Prioritized credentials test for case-sensitivity:
        // Host with only 'Admin' allowed must NOT attempt confirmed credentials with 'admin'
        var probe13c = new TestSmartDiscoveryProbe();
        probe13c.DirectlyReachable.Add("10.86.0.1");
        probe13c.DirectlyReachable.Add("10.86.0.2");
        probe13c.ValidCredentials["10.86.0.1"] = [("admin", "pw1")];
        probe13c.ValidCredentials["10.86.0.2"] = [("Admin", "pw2")];

        var engine13c1 = new SmartDiscoveryEngine(new ManagerConfig(), new SmartDiscoveryOptions
        {
            TargetHosts = ["10.86.0.1"],
            Usernames = ["admin"],
            Passwords = ["pw1"]
        }, probe13c);
        engine13c1.DiscoverAsync().GetAwaiter().GetResult();

        var engine13c2 = new SmartDiscoveryEngine(new ManagerConfig(), new SmartDiscoveryOptions
        {
            TargetHosts = ["10.86.0.2"],
            Usernames = ["Admin"],
            Passwords = ["pw2"]
        }, probe13c);
        var result13c2 = engine13c2.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result13c2.Discovered.Count);
        Equal("Admin", result13c2.Discovered[0].Username);
        Equal(false, probe13c.AttemptedLogins.Any(l => l.host == "10.86.0.2" && l.user == "admin"));

        // 14. Explicit initial gateway within group with valid cached route [U, G]:
        // When InitialGatewayServerId = G.Id and G requires U (both in target group),
        // G must be accepted as seed with priority -1 despite U being in group scope,
        // and target reachable only via G must be discovered through U -> G.
        var config14 = new ManagerConfig();
        var proxy14 = new BaseProxy { Id = Guid.NewGuid(), Name = "JH14", Host = "127.0.0.1", Port = 2222, Enabled = true };
        config14.BaseProxies.Add(proxy14);
        var testGroup14 = new ServerGroup { Name = "TestGroup14" };
        var serverU = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Server U",
            Host = "10.95.0.1",
            Port = 22,
            Username = "root",
            Password = "pwU",
            TryDirectWithoutJumphost = true,
            PreferredProxyId = proxy14.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy14.Id,
                ServerIds = [Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        serverU.PreferredRoute.ServerIds[0] = serverU.Id;
        var serverG = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Name = "Server G",
            Host = "10.95.0.2",
            Port = 22,
            Username = "root",
            Password = "pwG",
            RequiredPreviousServerId = serverU.Id,
            PreferredProxyId = proxy14.Id,
            PreferredRoute = new CachedRoute
            {
                ProxyId = proxy14.Id,
                ServerIds = [serverU.Id, Guid.Empty],
                LastSuccessUtc = DateTimeOffset.UtcNow
            }
        };
        serverG.PreferredRoute.ServerIds[1] = serverG.Id;
        testGroup14.Servers.AddRange([serverU, serverG]);
        config14.Groups.Add(testGroup14);
        config14.Links.Add(new ServerLink
        {
            FromServerId = serverU.Id,
            ToServerId = serverG.Id,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            LastSuccessfulProxyId = proxy14.Id
        });

        var probe14 = new TestSmartDiscoveryProbe();
        probe14.HopReachability.Add((serverG.Host, "10.95.0.99"));
        probe14.ValidCredentials["10.95.0.99"] = [("root", "pw99")];

        var options14 = new SmartDiscoveryOptions
        {
            TargetHosts = ["10.95.0.99"],
            Usernames = ["root"],
            Passwords = ["pw99"],
            TargetGroupId = testGroup14.Id,
            PreferGroupGateways = true,
            InitialGatewayServerId = serverG.Id,
            MaxParallelConnections = 1
        };

        var engine14 = new SmartDiscoveryEngine(config14, options14, probe14);
        var result14 = engine14.DiscoverAsync().GetAwaiter().GetResult();

        Equal(1, result14.Discovered.Count);
        Equal(0, result14.Problematic.Count);
        Equal("10.95.0.99", result14.Discovered[0].Host);
        Equal(serverG.Id, result14.Discovered[0].RequiredPreviousServerId);
        Equal(true, probe14.HopChains.Count > 0);
        Equal(2, probe14.HopChains[0].Count);
        Equal(serverU.Host, probe14.HopChains[0][0].Host);
        Equal(serverG.Host, probe14.HopChains[0][1].Host);
        Equal(string.Join('/', serverU.Host, serverG.Host, "10.95.0.99"), string.Join('/', result14.Discovered[0].RouteHops.Select(h => h.Host)));

        // Test 15: CandidateLinks draft config & LinkMapLayout handle duplicates safely without crashing
        var config15 = new ManagerConfig();
        var group15 = new ServerGroup { Id = Guid.NewGuid(), Name = "Group 15" };
        var s15A = new ManagedServer { Id = Guid.NewGuid(), Name = "Server 15A", Host = "10.15.0.1", Port = 22, Username = "admin" };
        var s15B = new ManagedServer { Id = Guid.NewGuid(), Name = "Server 15B", Host = "10.15.0.2", Port = 22, Username = "admin" };
        group15.Servers.Add(s15A);
        group15.Servers.Add(s15B);
        config15.Groups.Add(group15);
        config15.Links.Add(new ServerLink { FromServerId = s15A.Id, ToServerId = s15B.Id, Discovered = true, LastSuccessUtc = DateTimeOffset.UtcNow });

        var newCandId = Guid.NewGuid();
        var candidates15 = new List<DiscoveredServerCandidate>
        {
            new()
            {
                Id = s15A.Id,
                ExistingServerId = s15A.Id,
                ProposedName = s15A.Name,
                Host = s15A.Host,
                Port = 22,
                Username = s15A.Username,
                IsDuplicate = true,
                IsSelected = false
            },
            new()
            {
                Id = newCandId,
                ProposedName = "New Server 15C",
                Host = "10.15.0.3",
                Port = 22,
                Username = "admin",
                IsDuplicate = false,
                IsSelected = true,
                DiscoveredLinksFrom = [s15A.Id]
            }
        };

        var previewConfig15 = SmartDiscoverySessionCreator.CreatePreviewConfig(config15, candidates15);
        var allIds15 = previewConfig15.AllServers().Select(s => s.Id).ToList();
        Equal(allIds15.Count, allIds15.Distinct().Count()); // No duplicate server IDs across groups and ungrouped
        True(previewConfig15.FindServer(s15A.Id) is not null);
        True(previewConfig15.FindServer(newCandId) is not null);

        // LinkMapLayout.Build must succeed without ArgumentException
        var mapModel15 = LinkMapLayout.Build(previewConfig15);
        True(mapModel15.Nodes.Count > 0);

        // Deletion scenario (Astra review finding): deleting outgoing link A -> C in preview config
        // must sync back to candidate C so reopening preview does not restore the deleted link
        Equal(true, previewConfig15.Links.Any(l => l.FromServerId == s15A.Id && l.ToServerId == newCandId));
        candidates15[1].RequiredPreviousServerId = s15A.Id;
        var snapshotPairs15 = previewConfig15.Links.Select(l => (l.FromServerId, l.ToServerId)).ToHashSet();
        previewConfig15.Links.RemoveAll(l => l.FromServerId == s15A.Id && l.ToServerId == newCandId);
        SmartDiscoverySessionCreator.SyncDiscoveredLinks(snapshotPairs15, previewConfig15, candidates15, config15);

        // Candidate C's DiscoveredLinksFrom must no longer contain s15A.Id, and RequiredPreviousServerId cleared
        Equal(0, candidates15[1].DiscoveredLinksFrom.Count);
        Equal(null, candidates15[1].RequiredPreviousServerId);

        // Reopening / rebuilding preview config must NOT restore the deleted link
        var reopenedPreview = SmartDiscoverySessionCreator.CreatePreviewConfig(config15, candidates15);
        Equal(false, reopenedPreview.Links.Any(l => l.FromServerId == s15A.Id && l.ToServerId == newCandId));

        // Regression check (Astra review finding P1): closing an unchanged links preview
        // must NOT discard post-snapshot state (new candidates or concurrent live links)
        // Preview opened and snapshot taken BEFORE new candidate and concurrent link arrive:
        var postSnapshotPreview = SmartDiscoverySessionCreator.CreatePreviewConfig(config15, candidates15);
        var postSnapshotPairs = postSnapshotPreview.Links.Select(l => (l.FromServerId, l.ToServerId)).ToHashSet();

        // New candidate arrives and concurrent live link added AFTER snapshot was taken:
        var postSnapshotCandId = Guid.NewGuid();
        var candPost = new DiscoveredServerCandidate
        {
            Id = postSnapshotCandId,
            ProposedName = "Post-snapshot Candidate D",
            Host = "10.15.0.4",
            Port = 22,
            Username = "admin",
            DiscoveredLinksFrom = [s15B.Id],
            RequiredPreviousServerId = s15B.Id
        };
        candidates15.Add(candPost);
        var concurrentLiveLink = new ServerLink
        {
            FromServerId = s15B.Id,
            ToServerId = Guid.NewGuid(),
            LastSuccessUtc = DateTimeOffset.UtcNow
        };
        config15.Links.Add(concurrentLiveLink);

        // Preview closed without deletions:
        SmartDiscoverySessionCreator.SyncDiscoveredLinks(postSnapshotPairs, postSnapshotPreview, candidates15, config15);

        // CandPost must retain its incoming links and gateway
        Equal(1, candPost.DiscoveredLinksFrom.Count);
        Equal(s15B.Id, candPost.DiscoveredLinksFrom[0]);
        Equal(s15B.Id, candPost.RequiredPreviousServerId);
        True(config15.Links.Contains(concurrentLiveLink));

        // Defensive test: LinkMapLayout.Build handles config with duplicate server IDs gracefully
        var duplicateConfig = new ManagerConfig
        {
            Groups = [new ServerGroup { Servers = [s15A] }],
            UngroupedServers = [new ManagedServer { Id = s15A.Id, Name = "Duplicate Copy", Host = s15A.Host, Port = 22 }]
        };
        var defensiveMap = LinkMapLayout.Build(duplicateConfig);
        True(defensiveMap != null);
    }

    private sealed class PersistentSessionMockProbe : ISmartDiscoveryProbe
    {
        public HashSet<string> DirectlyReachable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(string user, string pass)>> ValidCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string gateway, string target)> HopReachability { get; } = [];
        public Action? OnSessionCreate { get; set; }
        public Action? OnSessionDispose { get; set; }

        public Task<bool> ProbeReachabilityAsync(
            BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(DirectlyReachable.Contains(host));

        public Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            ManagedServer target, IReadOnlyList<string> candidatePasswords,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (ValidCredentials.TryGetValue(target.Host, out var valid) && valid.Any(v => v.user == target.Username && v.pass == target.Password))
                return Task.FromResult(SmartDiscoveryAuthResult.Succeeded(target.Host, new PrivilegeEscalationResult { Method = "Root", Success = true }));
            return Task.FromResult(SmartDiscoveryAuthResult.FailedAuth("Bad credentials"));
        }

        public Task<bool> ProbeHopReachabilityAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var last = intermediateGateways.Count > 0 ? intermediateGateways[^1].Host : "";
            return Task.FromResult(HopReachability.Contains((last, targetHost)));
        }

        public Task<IDiscoveryGatewaySession?> OpenGatewaySessionAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            OnSessionCreate?.Invoke();
            var last = intermediateGateways.Count > 0 ? intermediateGateways[^1].Host : "";
            return Task.FromResult<IDiscoveryGatewaySession?>(new MockGatewaySession(this, last, OnSessionDispose));
        }

        private sealed class MockGatewaySession : IDiscoveryGatewaySession
        {
            private readonly PersistentSessionMockProbe parent;
            private readonly string gatewayHost;
            private readonly Action? onDispose;

            public MockGatewaySession(PersistentSessionMockProbe parent, string gatewayHost, Action? onDispose)
            {
                this.parent = parent;
                this.gatewayHost = gatewayHost;
                this.onDispose = onDispose;
            }

            public Task<bool> ProbeReachabilityAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
                Task.FromResult(parent.HopReachability.Contains((gatewayHost, host)));

            public Task<IReadOnlyDictionary<string, bool>> ProbeBatchReachabilityAsync(
                IReadOnlyList<string> hosts, int port, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in hosts)
                    dict[h] = parent.HopReachability.Contains((gatewayHost, h));
                return Task.FromResult<IReadOnlyDictionary<string, bool>>(dict);
            }

            public Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
                ManagedServer target, IReadOnlyList<string> candidatePasswords,
                TimeSpan timeout, CancellationToken cancellationToken) =>
                parent.AuthenticateAndProbeAsync(null, [], target, candidatePasswords, timeout, cancellationToken);

            public void Dispose() => onDispose?.Invoke();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

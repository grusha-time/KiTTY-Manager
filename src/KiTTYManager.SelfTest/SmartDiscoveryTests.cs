using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private sealed class TestSmartDiscoveryProbe : ISmartDiscoveryProbe
    {
        public HashSet<string> DirectlyReachable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(string user, string pass)>> ValidCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Hostnames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PrivilegeEscalationResult> Escalations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string gateway, string target)> HopReachability { get; } = [];
        public HashSet<string> HostKeyRejectedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action<string>? OnHostProbed { get; set; }

        public List<(string host, string user, string pass)> AttemptedLogins { get; } = [];

        public Task<bool> ProbeReachabilityAsync(
            BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(DirectlyReachable.Contains(host));
        }

        public Task<bool> ProbeHopReachabilityAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (intermediateGateways.Count == 0)
                return ProbeReachabilityAsync(jumpHost, targetHost, targetPort, timeout, cancellationToken);

            var lastGateway = intermediateGateways[^1].Host;
            return Task.FromResult(HopReachability.Contains((lastGateway, targetHost)));
        }

        public Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
            BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
            ManagedServer target, IReadOnlyList<string> candidatePasswords,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            AttemptedLogins.Add((target.Host, target.Username, target.Password));

            if (HostKeyRejectedHosts.Contains(target.Host))
            {
                return Task.FromResult(SmartDiscoveryAuthResult.FailedHostKey($"SSH host key для {target.Host} отклонён"));
            }

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

            return Task.FromResult(SmartDiscoveryAuthResult.FailedAuth("Неверные учетные данные"));
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
        config.Groups.AddRange([nyGroup, chiGroup, dalGroup]);

        // Existing server for subnet detection
        var existingServer = new ManagedServer
        {
            Name = "Chicago S1 Main",
            Host = "10.20.30.5",
            Port = 22
        };
        chiGroup.Servers.Add(existingServer);

        // Example 0: s1-group-main without region -> "Group Main S1"
        var name0 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.9", config);
        Equal("Group Main S1", name0);

        // Example 1: s1-group-main with target group "New York" -> "New York Group Main S1"
        var name1 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.10", config, targetGroupId: nyGroup.Id);
        Equal("New York Group Main S1", name1);

        // Example 2: s5-reserve with target group "Chicago" -> "Chicago S5 Reserve"
        var name2 = SmartSessionNameGenerator.ProposeName("s5-reserve", "192.168.1.11", config, targetGroupId: chiGroup.Id);
        Equal("Chicago S5 Reserve", name2);

        // Example 3: s5-map-reserve with target group "Dallas" -> "Dallas MapServer Reserve S5"
        var name3 = SmartSessionNameGenerator.ProposeName("s5-map-reserve", "192.168.1.12", config, targetGroupId: dalGroup.Id);
        Equal("Dallas MapServer Reserve S5", name3);

        // Region inferred from IP subnet matching existing server in Chicago
        var name4 = SmartSessionNameGenerator.ProposeName("s2-app-prod", "10.20.30.55", config);
        Equal("Chicago App Prod S2", name4);

        // Missing region/site fallback without inventing unverified facts
        var name5 = SmartSessionNameGenerator.ProposeName("web-main", "192.168.1.20", config);
        Equal("Web Main", name5);

        // Uniqueness enforcement
        var name6 = SmartSessionNameGenerator.ProposeName("s1-group-main", "192.168.1.30", config, targetGroupId: nyGroup.Id, existingNamesInBatch: [name1]);
        Equal("New York Group Main S1 2", name6);
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
        var testedPasswords = new List<(string password, string label)>();

        Task<(int ExitStatus, string Result)> MockRunCommandNonRoot(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            executedCommands.Add(cmd);
            if (cmd.StartsWith("id -u"))
                return Task.FromResult((0, "1000\n"));
            if (cmd.Contains("sudo -n id"))
                return Task.FromResult((1, "sudo: a password is required"));
            return Task.FromResult((1, "command not found"));
        }

        // Scenario A: Alternative candidate passwords from pool tested BEFORE login password;
        // if an alternative candidate password succeeds, login password is not tried.
        Task<(bool Success, string Method, string Command, string Password)> MockTryPasswordEscalationA(
            string pw, string label, TimeSpan timeout, CancellationToken ct)
        {
            testedPasswords.Add((pw, label));
            if (pw == "altPass2")
            {
                return Task.FromResult((true, "sudo", "sudo -i", pw));
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
        Equal("sudo", resultA.Method);
        Equal("sudo -i", resultA.RootCommand);
        Equal("altPass2", resultA.RootPassword);

        // Assert: id -u was checked first, then sudo -k & sudo -n id
        Equal(2, executedCommands.Count);
        Equal("id -u 2>/dev/null", executedCommands[0]);
        Equal("sudo -k 2>/dev/null; sudo -n id 2>/dev/null", executedCommands[1]);

        // Assert: alternative password altPass1 is tested first
        Equal(2, testedPasswords.Count);
        Equal(("altPass1", "root"), testedPasswords[0]);
        // Assert: alternative password altPass2 is tested next and succeeds
        Equal(("altPass2", "root"), testedPasswords[1]);
        // Assert: loginPassword was NOT tested because altPass2 succeeded first
        Equal(false, testedPasswords.Any(t => t.password == "loginPassword"));

        // Scenario B: Alternative passwords fail, login password is tested last
        executedCommands.Clear();
        testedPasswords.Clear();

        Task<(bool Success, string Method, string Command, string Password)> MockTryPasswordEscalationB(
            string pw, string label, TimeSpan timeout, CancellationToken ct)
        {
            testedPasswords.Add((pw, label));
            if (pw == "loginPassword")
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
        Equal("loginPassword", resultB.RootPassword);

        // Sequence: altPass1 -> altPass2 -> loginPassword (with "пользователя" label)
        Equal(3, testedPasswords.Count);
        Equal(("altPass1", "root"), testedPasswords[0]);
        Equal(("altPass2", "root"), testedPasswords[1]);
        Equal(("loginPassword", "пользователя"), testedPasswords[2]);

        // Scenario C: Passwordless sudo succeeds on step 2, skipping all password attempts
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

        var resultC = DefaultSmartDiscoveryProbe.ProbePrivilegeEscalationCoreAsync(
            MockRunCommandPasswordless,
            MockTryPasswordEscalationA,
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
        Equal(0, testedPasswords.Count);
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
    }
}

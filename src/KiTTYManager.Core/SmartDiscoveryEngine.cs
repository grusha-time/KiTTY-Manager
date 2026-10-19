using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KiTTYManager.Core;

public sealed record SmartDiscoveryResult(
    List<DiscoveredServerCandidate> Discovered,
    List<ProblematicServerCandidate> Problematic);

public interface ISmartDiscoveryProbe
{
    Task<bool> ProbeReachabilityAsync(
        BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken);

    Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        ManagedServer target, IReadOnlyList<string> candidatePasswords,
        TimeSpan timeout, CancellationToken cancellationToken);

    Task<bool> ProbeHopReachabilityAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SmartDiscoveryAuthResult
{
    public bool Success { get; set; }
    public string Hostname { get; set; } = "";
    public PrivilegeEscalationResult? Escalation { get; set; }
    public string HostKeyFingerprint { get; set; } = "";
    public string HostKeyAlgorithm { get; set; } = "";
    public int HostKeyBits { get; set; }
    public bool IsAuthFailure { get; set; }
    public bool IsUnreachable { get; set; }
    public bool IsHostKeyRejected { get; set; }
    public string ErrorMessage { get; set; } = "";

    public static SmartDiscoveryAuthResult Succeeded(
        string hostname, PrivilegeEscalationResult? escalation,
        string hostKeyFingerprint = "", string hostKeyAlgorithm = "", int hostKeyBits = 0) =>
        new()
        {
            Success = true,
            Hostname = hostname,
            Escalation = escalation,
            HostKeyFingerprint = hostKeyFingerprint,
            HostKeyAlgorithm = hostKeyAlgorithm,
            HostKeyBits = hostKeyBits
        };

    public static SmartDiscoveryAuthResult FailedAuth(string message) =>
        new() { Success = false, IsAuthFailure = true, ErrorMessage = message };

    public static SmartDiscoveryAuthResult FailedReachability(string message) =>
        new() { Success = false, IsUnreachable = true, ErrorMessage = message };

    public static SmartDiscoveryAuthResult FailedHostKey(string message) =>
        new() { Success = false, IsHostKeyRejected = true, ErrorMessage = message };
}

public class DefaultSmartDiscoveryProbe : ISmartDiscoveryProbe
{
    private readonly bool autoConfirmHostKeys;
    private readonly Func<ManagedServer, string, bool>? hostKeyVerifier;

    public DefaultSmartDiscoveryProbe(
        bool autoConfirmHostKeys = true,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null)
    {
        this.autoConfirmHostKeys = autoConfirmHostKeys;
        this.hostKeyVerifier = hostKeyVerifier;
    }

    public virtual async Task<bool> ProbeReachabilityAsync(
        BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (jumpHost != null)
        {
            return await Socks5TcpProbe.CanConnectAsync(jumpHost, host, port, timeout, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var banner = new byte[1];
            var read = await client.GetStream().ReadAsync(banner, 0, 1, cts.Token).ConfigureAwait(false);
            return read > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<bool> ProbeHopReachabilityAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (intermediateGateways.Count == 0)
            return await ProbeReachabilityAsync(jumpHost, targetHost, targetPort, timeout, cancellationToken).ConfigureAwait(false);

        var ssh = new SshConnectionService
        {
            Timeout = timeout,
            EndpointProbeTimeout = timeout,
            HostKeyVerifier = hostKeyVerifier ?? ((_, _) => autoConfirmHostKeys)
        };

        var candidate = new RouteCandidate(
            jumpHost ?? new BaseProxy(),
            intermediateGateways,
            WithoutProxy: jumpHost == null);

        try
        {
            using var route = await ssh.ConnectProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (route.Client == null || !route.Client.IsConnected) return false;

            // Probe target reachability via direct-tcpip port forward
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            route.Client.AddForwardedPort(forward);
            try
            {
                forward.Start();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                using var client = new TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, checked((int)forward.BoundPort), cts.Token).ConfigureAwait(false);
                var banner = new byte[1];
                var read = await client.GetStream().ReadAsync(banner, 0, 1, cts.Token).ConfigureAwait(false);
                return read > 0;
            }
            finally
            {
                try { forward.Stop(); } catch { }
                try { route.Client.RemoveForwardedPort(forward); } catch { }
                forward.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitStatus, string Result)> RunCommandAsync(
        SshClient client, string commandText, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cmd = client.CreateCommand(commandText);
        cmd.CommandTimeout = timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await cmd.ExecuteAsync(cts.Token).ConfigureAwait(false);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? "");
    }

    public virtual async Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        ManagedServer target, IReadOnlyList<string> candidatePasswords,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var hostKeyRejected = false;
        string? rejectedFingerprint = null;
        var ssh = new SshConnectionService
        {
            Timeout = timeout,
            EndpointProbeTimeout = timeout,
            HostKeyVerifier = (server, fingerprint) =>
            {
                if (autoConfirmHostKeys) return true;
                if (hostKeyVerifier != null)
                {
                    var ok = hostKeyVerifier(server, fingerprint);
                    if (!ok)
                    {
                        hostKeyRejected = true;
                        rejectedFingerprint = fingerprint;
                    }
                    return ok;
                }
                hostKeyRejected = true;
                rejectedFingerprint = fingerprint;
                return false;
            }
        };

        var allServers = new List<ManagedServer>(intermediateGateways) { target };
        var candidate = new RouteCandidate(
            jumpHost ?? new BaseProxy(),
            allServers,
            WithoutProxy: jumpHost == null);

        try
        {
            using var route = await ssh.ConnectProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (route.Client == null || !route.Client.IsConnected)
                return SmartDiscoveryAuthResult.FailedReachability("SSH-соединение не установилось.");

            // Probe remote hostname asynchronously with cancellation
            var (_, rawHostname) = await RunCommandAsync(
                route.Client, "hostname -f 2>/dev/null || hostname 2>/dev/null || uname -n", timeout, cancellationToken).ConfigureAwait(false);
            var firstLine = rawHostname.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            // Probe privilege escalation asynchronously
            var escalation = await ProbePrivilegeEscalationAsync(
                route.Client, target.Username, target.Password, candidatePasswords, timeout, cancellationToken).ConfigureAwait(false);

            return SmartDiscoveryAuthResult.Succeeded(
                firstLine,
                escalation,
                target.HostKeyFingerprint,
                target.HostKeyAlgorithm,
                target.HostKeyBits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (hostKeyRejected)
            {
                return SmartDiscoveryAuthResult.FailedHostKey($"SSH host-key {rejectedFingerprint} отклонён политикой безопасности.");
            }
            if (ex is SshAuthenticationException authEx)
            {
                return SmartDiscoveryAuthResult.FailedAuth(authEx.Message);
            }
            return SmartDiscoveryAuthResult.FailedReachability(ex.Message);
        }
    }

    internal static async Task<PrivilegeEscalationResult> ProbePrivilegeEscalationCoreAsync(
        Func<string, TimeSpan, CancellationToken, Task<(int ExitStatus, string Result)>> runCommand,
        Func<string, string, TimeSpan, CancellationToken, Task<(bool Success, string Method, string Command, string Password)>> tryPasswordEscalation,
        string username, string loginPassword, IReadOnlyList<string> candidatePasswords,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // 1. Check existing root
        var (idExit, idRes) = await runCommand("id -u 2>/dev/null", timeout, cancellationToken).ConfigureAwait(false);
        if (idExit == 0 && idRes.Trim() == "0")
        {
            return new PrivilegeEscalationResult
            {
                Method = "Root",
                RootCommand = "",
                RootPassword = "",
                StatusDescription = "Учётная запись уже обладает правами root",
                Success = true
            };
        }

        // 2. Check passwordless sudo (run sudo -k first to invalidate any cached ticket)
        var (sudoExit, sudoRes) = await runCommand("sudo -k 2>/dev/null; sudo -n id 2>/dev/null", timeout, cancellationToken).ConfigureAwait(false);
        if (sudoExit == 0 && sudoRes.Contains("uid=0"))
        {
            return new PrivilegeEscalationResult
            {
                Method = "sudo (без пароля)",
                RootCommand = "sudo -i",
                RootPassword = "",
                StatusDescription = "Доступен sudo без ввода пароля",
                Success = true
            };
        }

        // 3. Try OTHER candidate passwords first
        var otherPasswords = candidatePasswords.Where(p => p != loginPassword && p.Length > 0).Distinct().ToList();
        foreach (var pw in otherPasswords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (success, method, cmd, pass) = await tryPasswordEscalation(pw, "root", timeout, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                return new PrivilegeEscalationResult
                {
                    Method = method,
                    RootCommand = cmd,
                    RootPassword = pass,
                    StatusDescription = $"Повышение через {method} (пароль root) подтверждено",
                    Success = true
                };
            }
        }

        // 4. Try login password last
        if (!string.IsNullOrEmpty(loginPassword))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (success, method, cmd, pass) = await tryPasswordEscalation(loginPassword, "пользователя", timeout, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                return new PrivilegeEscalationResult
                {
                    Method = method,
                    RootCommand = cmd,
                    RootPassword = pass,
                    StatusDescription = $"Повышение через {method} (пароль пользователя) подтверждено",
                    Success = true
                };
            }
        }

        return new PrivilegeEscalationResult
        {
            Method = "Не повышено",
            RootCommand = "",
            RootPassword = "",
            StatusDescription = "Повышение привилегий не удалось",
            Success = false
        };
    }

    private static async Task<PrivilegeEscalationResult> ProbePrivilegeEscalationAsync(
        SshClient client, string username, string loginPassword,
        IReadOnlyList<string> candidatePasswords, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await ProbePrivilegeEscalationCoreAsync(
            (cmd, to, ct) => RunCommandAsync(client, cmd, to, ct),
            async (pw, label, to, ct) =>
            {
                var res = await TryPasswordEscalationAsync(client, pw, label, to, ct).ConfigureAwait(false);
                return (res.Success, res.Method, res.RootCommand, res.RootPassword);
            },
            username, loginPassword, candidatePasswords, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PrivilegeEscalationResult> TryPasswordEscalationAsync(
        SshClient client, string password, string passwordKindLabel, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepCts.CancelAfter(timeout);
        var ct = stepCts.Token;

        // Sudo check via input stream
        try
        {
            using var sudoCmd = client.CreateCommand("sudo -k -S -p '' id 2>/dev/null");
            sudoCmd.CommandTimeout = timeout;
            var sudoTask = sudoCmd.ExecuteAsync(ct);
            using (var stream = sudoCmd.CreateInputStream())
            {
                var pwBytes = Encoding.UTF8.GetBytes(password + "\n");
                await stream.WriteAsync(pwBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                Array.Clear(pwBytes);
            }
            await sudoTask.ConfigureAwait(false);
            if (sudoCmd.ExitStatus == 0 && (sudoCmd.Result ?? "").Contains("uid=0"))
            {
                return new PrivilegeEscalationResult
                {
                    Method = "sudo",
                    RootCommand = "sudo -i",
                    RootPassword = password,
                    StatusDescription = $"Повышение через sudo (пароль {passwordKindLabel}) подтверждено",
                    Success = true
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { }

        // Su check via input stream
        try
        {
            using var suCmd = client.CreateCommand("su -c 'id'");
            suCmd.CommandTimeout = timeout;
            var suTask = suCmd.ExecuteAsync(ct);
            using (var stream = suCmd.CreateInputStream())
            {
                var pwBytes = Encoding.UTF8.GetBytes(password + "\n");
                await stream.WriteAsync(pwBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                Array.Clear(pwBytes);
            }
            await suTask.ConfigureAwait(false);
            if (suCmd.ExitStatus == 0 && (suCmd.Result ?? "").Contains("uid=0"))
            {
                return new PrivilegeEscalationResult
                {
                    Method = "su",
                    RootCommand = "su -",
                    RootPassword = password,
                    StatusDescription = $"Повышение через su (пароль {passwordKindLabel}) подтверждено",
                    Success = true
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { }

        // PTY-based fallback for su if terminal is required
        try
        {
            var ptyModes = new Dictionary<TerminalModes, uint> { { TerminalModes.ECHO, 0 } };
            using var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096, ptyModes);
            shell.WriteLine("su -c 'id'");
            shell.Flush();
            var readBuffer = new byte[1024];
            var sw = Stopwatch.StartNew();
            var pwSent = false;
            var responseBuilder = new StringBuilder();
            var maxPtyWait = Math.Min(3000, (int)timeout.TotalMilliseconds);

            while (sw.ElapsedMilliseconds < maxPtyWait && !ct.IsCancellationRequested)
            {
                if (shell.DataAvailable)
                {
                    var count = await shell.ReadAsync(readBuffer, 0, readBuffer.Length, ct).ConfigureAwait(false);
                    if (count > 0)
                    {
                        var text = Encoding.UTF8.GetString(readBuffer, 0, count);
                        responseBuilder.Append(text);

                        if (!pwSent && (text.Contains("assword", StringComparison.OrdinalIgnoreCase) || text.Contains("ароль", StringComparison.OrdinalIgnoreCase)))
                        {
                            pwSent = true;
                            shell.WriteLine(password);
                            shell.Flush();
                        }

                        if (responseBuilder.ToString().Contains("uid=0"))
                        {
                            return new PrivilegeEscalationResult
                            {
                                Method = "su",
                                RootCommand = "su -",
                                RootPassword = password,
                                StatusDescription = $"Повышение через su (пароль {passwordKindLabel}) подтверждено",
                                Success = true
                            };
                        }
                    }
                }
                else
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { }

        return new PrivilegeEscalationResult { Method = "Не повышено", RootCommand = "", Success = false };
    }
}

public sealed class SmartDiscoveryEngine
{
    private readonly ManagerConfig config;
    private readonly SmartDiscoveryOptions options;
    private readonly ISmartDiscoveryProbe probe;
    private readonly IProgress<SmartDiscoveryProgress>? progress;
    private readonly IReadOnlyList<DiscoveredServerCandidate>? initialGateways;
    private readonly Func<ManagedServer, string, bool>? hostKeyVerifier;

    public SmartDiscoveryEngine(
        ManagerConfig config,
        SmartDiscoveryOptions options,
        ISmartDiscoveryProbe? probe = null,
        IProgress<SmartDiscoveryProgress>? progress = null,
        IReadOnlyList<DiscoveredServerCandidate>? initialGateways = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null)
    {
        this.config = config;
        this.options = options;
        this.hostKeyVerifier = hostKeyVerifier;
        this.probe = probe ?? new DefaultSmartDiscoveryProbe(config.AutoConfirmHostKeys, hostKeyVerifier);
        this.progress = progress;
        this.initialGateways = initialGateways;
    }

    public async Task<SmartDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var targetHosts = options.TargetHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var usernames = options.Usernames.Count > 0 ? options.Usernames : ["root"];
        var passwords = options.Passwords.Count > 0 ? options.Passwords : [""];

        var totalCount = targetHosts.Count;
        var discovered = new List<DiscoveredServerCandidate>();
        var problematic = new List<ProblematicServerCandidate>();
        var confirmedCredentials = new List<(string User, string Pass)>();
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (initialGateways != null)
        {
            foreach (var gw in initialGateways)
            {
                if (!string.IsNullOrWhiteSpace(gw.ProposedName))
                    reservedNames.Add(gw.ProposedName);
            }
        }
        var hostAttempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastHostProblems = new Dictionary<string, ProblematicServerCandidate>(StringComparer.OrdinalIgnoreCase);
        var syncLock = new object();

        ReportProgress(SmartDiscoveryProgressKind.Starting, "", $"Запуск сканирования {totalCount} хостов...", discovered.Count, problematic.Count, totalCount, 0);

        if (totalCount == 0)
        {
            ReportProgress(SmartDiscoveryProgressKind.Completed, "", "Список целевых хостов пуст.", 0, 0, 0, 100);
            return new SmartDiscoveryResult(discovered, problematic);
        }

        var maxParallel = Math.Max(1, options.MaxParallelConnections);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

        try
        {
            // Phase 1: Probe reachability directly or via selected JumpHost
            ReportProgress(SmartDiscoveryProgressKind.ReachabilityProbing, "", "Фаза 1: Проверка сетевой доступности портов 22...", discovered.Count, problematic.Count, totalCount, 5);

            var reachabilityResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var reachabilityTasks = targetHosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var isReachable = await probe.ProbeReachabilityAsync(
                        options.JumpHost, host, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);
                    lock (syncLock)
                    {
                        reachabilityResults[host] = isReachable;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(reachabilityTasks).ConfigureAwait(false);

            var directlyReachable = targetHosts.Where(h => reachabilityResults.TryGetValue(h, out var ok) && ok).ToList();
            var unreachableHosts = targetHosts.Where(h => !reachabilityResults.TryGetValue(h, out var ok) || !ok).ToList();

            // Phase 2: Credential verification on directly reachable hosts
            ReportProgress(SmartDiscoveryProgressKind.AuthenticationProbing, "", $"Фаза 2: Проверка учётных данных ({directlyReachable.Count} доступных узлов)...", discovered.Count, problematic.Count, totalCount, 25);

            var authTasks = directlyReachable.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (candidate, problem) = await AuthenticateHostAsync(
                        host, intermediateGateways: [], hopHops: [], cancellationToken).ConfigureAwait(false);

                    lock (syncLock)
                    {
                        if (candidate != null)
                        {
                            discovered.Add(candidate);
                            reservedNames.Add(candidate.ProposedName);
                            // Prioritize confirmed credential
                            confirmedCredentials.RemoveAll(c => c.User == candidate.Username && c.Pass == candidate.Password);
                            confirmedCredentials.Insert(0, (candidate.Username, candidate.Password));
                            ReportProgress(SmartDiscoveryProgressKind.HostSucceeded, host, $"Сессия найдена: {candidate.ProposedName}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                        }
                        else if (problem != null)
                        {
                            problematic.Add(problem);
                            ReportProgress(SmartDiscoveryProgressKind.HostFailed, host, $"Ошибка: {problem.Reason}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(authTasks).ConfigureAwait(false);

            // Phase 3: Multi-hop discovery for remaining unreachable hosts
            var allKnownGateways = discovered.Concat(initialGateways ?? []).ToList();
            if (unreachableHosts.Count > 0 && allKnownGateways.Count > 0)
            {
                ReportProgress(SmartDiscoveryProgressKind.MultiHopProbing, "", $"Фаза 3: Поиск multi-hop маршрутов ({unreachableHosts.Count} недоступных хостов)...", discovered.Count, problematic.Count, totalCount, 60);

                var availableGateways = allKnownGateways.ToList();
                const int maxHops = 3;

                while (unreachableHosts.Count > 0 && availableGateways.Count > 0)
                {
                    var nextWaveGateways = new List<DiscoveredServerCandidate>();

                    foreach (var gateway in availableGateways)
                    {
                        if (gateway.RouteHops.Count >= maxHops) continue;

                        var gatewayChain = BuildGatewayChain(gateway, discovered.Concat(initialGateways ?? []).ToList());

                        for (var i = unreachableHosts.Count - 1; i >= 0; i--)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var targetHost = unreachableHosts[i];

                            // If target host already exceeded max attempts across all routes, don't keep probing
                            lock (syncLock)
                            {
                                if (hostAttempts.TryGetValue(targetHost, out var att) && att >= options.MaxAttemptsPerHost)
                                    continue;
                            }

                            // Prevent cycles
                            if (gateway.RouteHops.Any(h => string.Equals(h.Host, targetHost, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var canHop = await probe.ProbeHopReachabilityAsync(
                                options.JumpHost, gatewayChain, targetHost, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);

                            if (canHop)
                            {
                                var (candidate, hopProblem) = await AuthenticateHostAsync(
                                    targetHost, gatewayChain, gateway.RouteHops, cancellationToken, previousServerId: gateway.Id).ConfigureAwait(false);

                                if (candidate != null)
                                {
                                    candidate.RequiredPreviousServerId = gateway.Id;
                                    candidate.DiscoveredLinksFrom = [gateway.Id];
                                    candidate.StatusMessage = $"Обнаружен через шлюз {gateway.ProposedName}";

                                    lock (syncLock)
                                    {
                                        discovered.Add(candidate);
                                        reservedNames.Add(candidate.ProposedName);
                                        nextWaveGateways.Add(candidate);
                                        unreachableHosts.RemoveAt(i);
                                        lastHostProblems.Remove(targetHost);

                                        confirmedCredentials.RemoveAll(c => c.User == candidate.Username && c.Pass == candidate.Password);
                                        confirmedCredentials.Insert(0, (candidate.Username, candidate.Password));

                                        ReportProgress(SmartDiscoveryProgressKind.HostSucceeded, targetHost, $"Сессия найдена через {gateway.ProposedName}: {candidate.ProposedName}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                                    }
                                }
                                else if (hopProblem != null)
                                {
                                    lock (syncLock)
                                    {
                                        lastHostProblems[targetHost] = hopProblem;
                                    }
                                }
                            }
                        }
                    }

                    availableGateways = nextWaveGateways;
                }
            }

            // Any remaining unreachable hosts are added to problematic (preserving specific auth or hostkey problems)
            foreach (var remaining in unreachableHosts)
            {
                ProblematicServerCandidate prob;
                lock (syncLock)
                {
                    if (lastHostProblems.TryGetValue(remaining, out var specificProb))
                    {
                        prob = specificProb;
                    }
                    else
                    {
                        prob = new ProblematicServerCandidate
                        {
                            Host = remaining,
                            Stage = "Доступность",
                            Reason = "Узел недоступен по TCP (порт 22) напрямую и через обнаруженные шлюзы",
                            CanRetry = true,
                            IsSelected = true
                        };
                    }
                }
                problematic.Add(prob);
            }

            ReportProgress(SmartDiscoveryProgressKind.Completed, "", $"Сканирование завершено: найдено {discovered.Count}, проблемных {problematic.Count}.", discovered.Count, problematic.Count, totalCount, 100);
            return new SmartDiscoveryResult(discovered, problematic);
        }
        catch (OperationCanceledException)
        {
            ReportProgress(SmartDiscoveryProgressKind.Cancelled, "", $"Сканирование остановлено пользователем. Найдено {discovered.Count} сессий.", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
            return new SmartDiscoveryResult(discovered, problematic);
        }

        async Task<(DiscoveredServerCandidate? Candidate, ProblematicServerCandidate? Problem)> AuthenticateHostAsync(
            string host,
            IReadOnlyList<ManagedServer> intermediateGateways,
            IReadOnlyList<RouteHop> hopHops,
            CancellationToken ct,
            Guid? previousServerId = null)
        {
            List<(string User, string Pass)> confirmedSnapshot;
            lock (syncLock)
            {
                confirmedSnapshot = confirmedCredentials.ToList();
            }
            var orderedCredentials = GetPrioritizedCredentials(usernames, passwords, confirmedSnapshot);
            var maxAttempts = Math.Max(1, options.MaxAttemptsPerHost);
            string lastReason = "Не удалось подобрать учётные данные";
            string currentStage = "Аутентификация";

            foreach (var (u, p) in orderedCredentials)
            {
                ct.ThrowIfCancellationRequested();

                int currentAttempts;
                lock (syncLock)
                {
                    hostAttempts.TryGetValue(host, out currentAttempts);
                    if (currentAttempts >= maxAttempts)
                    {
                        lastReason = $"Превышен лимит попыток подбора ({maxAttempts}) для узла {host}";
                        break;
                    }
                    hostAttempts[host] = ++currentAttempts;
                }

                var targetServer = new ManagedServer
                {
                    Host = host,
                    Port = 22,
                    Username = u,
                    Password = p,
                    UseKeyboardInteractive = true
                };

                var result = await probe.AuthenticateAndProbeAsync(
                    options.JumpHost, intermediateGateways, targetServer, options.Passwords, options.Timeout, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    string proposedName;
                    lock (syncLock)
                    {
                        proposedName = SmartSessionNameGenerator.ProposeName(
                            result.Hostname, host, config, options.TargetGroupId, options.TargetSubgroupName, reservedNames);
                        reservedNames.Add(proposedName);
                    }

                    var hops = new List<RouteHop>(hopHops)
                    {
                        new RouteHop(Guid.NewGuid(), proposedName, host, 22)
                    };

                    var candidate = new DiscoveredServerCandidate
                    {
                        Host = host,
                        Port = 22,
                        Hostname = result.Hostname,
                        ProposedName = proposedName,
                        GroupId = options.TargetGroupId,
                        GroupName = config.FindGroup(options.TargetGroupId ?? Guid.Empty)?.Name,
                        SubgroupName = options.TargetSubgroupName,
                        Username = u,
                        Password = p,
                        PrivilegeEscalation = result.Escalation ?? new(),
                        RouteHops = hops,
                        DiscoveredLinksFrom = previousServerId.HasValue ? [previousServerId.Value] : [],
                        RequiredPreviousServerId = previousServerId,
                        VerifiedProxyId = options.JumpHost?.Id,
                        HostKeyFingerprint = result.HostKeyFingerprint,
                        HostKeyAlgorithm = result.HostKeyAlgorithm,
                        HostKeyBits = result.HostKeyBits,
                        StatusMessage = intermediateGateways.Count > 0 ? "Обнаружен через шлюз" : "Готов к созданию",
                        IsSelected = true
                    };

                    return (candidate, null);
                }

                if (result.IsHostKeyRejected)
                {
                    currentStage = "HostKey";
                    lastReason = result.ErrorMessage;
                    break;
                }
                else if (result.IsAuthFailure)
                {
                    currentStage = "Аутентификация";
                    lastReason = $"Неверный логин или пароль: {result.ErrorMessage}";
                }
                else if (result.IsUnreachable)
                {
                    currentStage = "Доступность";
                    lastReason = $"Сбой соединения: {result.ErrorMessage}";
                }
            }

            var problem = new ProblematicServerCandidate
            {
                Host = host,
                Stage = currentStage,
                Reason = lastReason,
                CanRetry = true,
                IsSelected = true
            };

            lock (syncLock)
            {
                lastHostProblems[host] = problem;
            }

            return (null, problem);
        }
    }

    public async Task<SmartDiscoveryResult> RetryProblematicAsync(
        IReadOnlyList<ProblematicServerCandidate> problematicToRetry,
        SmartDiscoveryOptions updatedOptions,
        IReadOnlyList<DiscoveredServerCandidate>? existingGateways = null,
        CancellationToken cancellationToken = default)
    {
        var retryHosts = problematicToRetry.Where(p => p.IsSelected && p.CanRetry).Select(p => p.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (retryHosts.Count == 0) return new SmartDiscoveryResult([], []);

        var retryOptions = new SmartDiscoveryOptions
        {
            TargetHosts = retryHosts,
            Usernames = updatedOptions.Usernames,
            Passwords = updatedOptions.Passwords,
            TargetGroupId = updatedOptions.TargetGroupId,
            TargetSubgroupName = updatedOptions.TargetSubgroupName,
            JumpHost = updatedOptions.JumpHost,
            MaxParallelConnections = updatedOptions.MaxParallelConnections,
            MaxAttemptsPerHost = updatedOptions.MaxAttemptsPerHost,
            Timeout = updatedOptions.Timeout,
            EndpointProbeTimeout = updatedOptions.EndpointProbeTimeout
        };

        var retryEngine = new SmartDiscoveryEngine(config, retryOptions, probe, progress, existingGateways, hostKeyVerifier);
        return await retryEngine.DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<(string User, string Pass)> GetPrioritizedCredentials(
        IReadOnlyList<string> usernames,
        IReadOnlyList<string> passwords,
        IReadOnlyList<(string User, string Pass)> confirmed)
    {
        var result = new List<(string User, string Pass)>();
        var seen = new HashSet<(string, string)>();

        foreach (var c in confirmed)
        {
            if (seen.Add(c))
                result.Add(c);
        }

        foreach (var u in usernames)
        {
            foreach (var p in passwords)
            {
                if (seen.Add((u, p)))
                    result.Add((u, p));
            }
        }

        return result;
    }

    private static ManagedServer CandidateToServer(DiscoveredServerCandidate candidate) =>
        new()
        {
            Id = candidate.Id,
            Name = candidate.ProposedName,
            Host = candidate.Host,
            Port = candidate.Port,
            Username = candidate.Username,
            Password = candidate.Password,
            RootLogin = candidate.PrivilegeEscalation.Success ? candidate.PrivilegeEscalation.RootCommand : "",
            RootPassword = candidate.PrivilegeEscalation.Success ? candidate.PrivilegeEscalation.RootPassword : "",
            HostKeyFingerprint = candidate.HostKeyFingerprint,
            HostKeyAlgorithm = candidate.HostKeyAlgorithm,
            HostKeyBits = candidate.HostKeyBits,
            UseKeyboardInteractive = true
        };

    private static IReadOnlyList<ManagedServer> BuildGatewayChain(
        DiscoveredServerCandidate gateway,
        IReadOnlyList<DiscoveredServerCandidate> allDiscovered)
    {
        var chain = new List<ManagedServer>();
        var current = gateway;
        var visited = new HashSet<Guid>();

        while (current != null && visited.Add(current.Id))
        {
            chain.Insert(0, CandidateToServer(current));
            if (current.RequiredPreviousServerId.HasValue)
            {
                current = allDiscovered.FirstOrDefault(d => d.Id == current.RequiredPreviousServerId.Value);
            }
            else
            {
                break;
            }
        }

        return chain;
    }

    private static double CalculatePercentage(int discovered, int problem, int total)
    {
        if (total <= 0) return 100;
        var done = discovered + problem;
        return Math.Min(100.0, Math.Max(0.0, (done / (double)total) * 100.0));
    }

    private void ReportProgress(
        SmartDiscoveryProgressKind kind, string host, string message,
        int discovered, int problem, int total, double percentage)
    {
        progress?.Report(new SmartDiscoveryProgress
        {
            Kind = kind,
            Host = host,
            Message = message,
            DiscoveredCount = discovered,
            ProblemCount = problem,
            TotalCount = total,
            ProgressPercentage = percentage
        });
    }
}

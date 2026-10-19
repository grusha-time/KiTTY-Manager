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
    private readonly Action<string>? log;
    private readonly Func<BaseProxy, CancellationToken, Task<bool>>? ensureEntryPoint;
    private readonly object entryPointReadinessLock = new();
    private readonly Dictionary<Guid, Task<bool>> entryPointReadiness = [];

    public SmartDiscoveryEngine(
        ManagerConfig config,
        SmartDiscoveryOptions options,
        ISmartDiscoveryProbe? probe = null,
        IProgress<SmartDiscoveryProgress>? progress = null,
        IReadOnlyList<DiscoveredServerCandidate>? initialGateways = null,
        Func<ManagedServer, string, bool>? hostKeyVerifier = null,
        Action<string>? log = null,
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureEntryPoint = null)
    {
        this.config = config;
        this.options = options;
        this.hostKeyVerifier = hostKeyVerifier;
        this.probe = probe ?? new DefaultSmartDiscoveryProbe(config.AutoConfirmHostKeys, hostKeyVerifier);
        this.progress = progress;
        this.initialGateways = initialGateways;
        this.log = log;
        this.ensureEntryPoint = ensureEntryPoint;
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
        var entryPoints = ResolveEntryPoints();

        LogScanStart(totalCount, entryPoints);

        try
        {
            // Phase 1: Probe reachability directly or via an entry point
            ReportProgress(SmartDiscoveryProgressKind.ReachabilityProbing, "", "Фаза 1: Проверка сетевой доступности портов 22...", discovered.Count, problematic.Count, totalCount, 5);
            Log("Фаза 1: проверка сетевой доступности порта 22.");

            var reachableVia = new Dictionary<string, BaseProxy?>(StringComparer.OrdinalIgnoreCase);
            var reachabilityTasks = targetHosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (var entryPoint in entryPoints)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entryPoint != null && !await EnsureEntryPointReadyAsync(entryPoint, cancellationToken).ConfigureAwait(false))
                            continue;

                        var isReachable = await probe.ProbeReachabilityAsync(
                            entryPoint, host, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);
                        Log($"Хост {host}: доступность через {DescribeEntryPoint(entryPoint)} — {(isReachable ? "успех" : "неудача")}.");
                        if (isReachable)
                        {
                            lock (syncLock)
                            {
                                reachableVia[host] = entryPoint;
                            }
                            return;
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(reachabilityTasks).ConfigureAwait(false);

            var directlyReachable = targetHosts.Where(reachableVia.ContainsKey).ToList();
            var unreachableHosts = targetHosts.Where(h => !reachableVia.ContainsKey(h)).ToList();

            // Phase 2: Credential verification on directly reachable hosts
            ReportProgress(SmartDiscoveryProgressKind.AuthenticationProbing, "", $"Фаза 2: Проверка учётных данных ({directlyReachable.Count} доступных узлов)...", discovered.Count, problematic.Count, totalCount, 25);
            Log($"Фаза 2: проверка учётных данных для {directlyReachable.Count} доступных хостов.");

            var authTasks = directlyReachable.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    reachableVia.TryGetValue(host, out var winningEntryPoint);
                    var (candidate, problem) = await AuthenticateHostAsync(
                        host, winningEntryPoint, intermediateGateways: [], hopHops: [], cancellationToken).ConfigureAwait(false);

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
                            Log($"Хост {host}: сессия \"{candidate.ProposedName}\" найдена через {DescribeVerifiedEntryPoint(candidate.VerifiedProxyId)}.");
                        }
                        else if (problem != null)
                        {
                            problematic.Add(problem);
                            ReportProgress(SmartDiscoveryProgressKind.HostFailed, host, $"Ошибка: {problem.Reason}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                            Log($"Хост {host}: ошибка на этапе \"{problem.Stage}\".");
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
                Log($"Фаза 3: поиск multi-hop маршрутов для {unreachableHosts.Count} недоступных хостов.");

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

                            foreach (var entryPoint in entryPoints)
                            {
                                if (entryPoint != null && !await EnsureEntryPointReadyAsync(entryPoint, cancellationToken).ConfigureAwait(false))
                                    continue;

                                var canHop = await probe.ProbeHopReachabilityAsync(
                                    entryPoint, gatewayChain, targetHost, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);
                                Log($"Фаза 3: хост {targetHost} через шлюз {gateway.ProposedName} и {DescribeEntryPoint(entryPoint)} — {(canHop ? "доступен" : "недоступен")}.");

                                if (!canHop) continue;

                                var (candidate, hopProblem) = await AuthenticateHostAsync(
                                    targetHost, entryPoint, gatewayChain, gateway.RouteHops, cancellationToken, previousServerId: gateway.Id).ConfigureAwait(false);

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
                                    Log($"Хост {targetHost}: сессия \"{candidate.ProposedName}\" найдена через шлюз {gateway.ProposedName} и {DescribeVerifiedEntryPoint(candidate.VerifiedProxyId)}.");
                                    break;
                                }

                                if (hopProblem != null)
                                {
                                    lock (syncLock)
                                    {
                                        lastHostProblems[targetHost] = hopProblem;
                                    }
                                }

                                // An authentication or host-key problem will not change through another entry point.
                                if (hopProblem?.Stage != "Доступность")
                                    break;

                                lock (syncLock)
                                {
                                    if (hostAttempts.TryGetValue(targetHost, out var used) && used >= options.MaxAttemptsPerHost)
                                        break;
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
            Log($"Сканирование завершено: найдено {discovered.Count}, проблемных {problematic.Count}.");
            return new SmartDiscoveryResult(discovered, problematic);
        }
        catch (OperationCanceledException)
        {
            ReportProgress(SmartDiscoveryProgressKind.Cancelled, "", $"Сканирование остановлено пользователем. Найдено {discovered.Count} сессий.", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
            Log($"Сканирование отменено: найдено {discovered.Count}, проблемных {problematic.Count}.");
            return new SmartDiscoveryResult(discovered, problematic);
        }

        IReadOnlyList<BaseProxy?> ResolveAttemptEntryPoints(BaseProxy? winningEntryPoint)
        {
            if (!options.AutoSelectJumpHost || entryPoints.Count <= 1)
                return [winningEntryPoint];

            var ordered = new List<BaseProxy?>(entryPoints.Count);
            if (winningEntryPoint != null) ordered.Add(winningEntryPoint);
            foreach (var point in entryPoints)
            {
                if (point?.Id == winningEntryPoint?.Id) continue;
                ordered.Add(point);
            }
            return ordered;
        }

        async Task<(DiscoveredServerCandidate? Candidate, ProblematicServerCandidate? Problem)> AuthenticateHostAsync(
            string host,
            BaseProxy? entryPoint,
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
                var stopAll = false;

                foreach (var attemptEntryPoint in ResolveAttemptEntryPoints(entryPoint))
                {
                    if (attemptEntryPoint != null && !await EnsureEntryPointReadyAsync(attemptEntryPoint, ct).ConfigureAwait(false))
                        continue;

                    int currentAttempts;
                    lock (syncLock)
                    {
                        hostAttempts.TryGetValue(host, out currentAttempts);
                        if (currentAttempts >= maxAttempts)
                        {
                            lastReason = $"Превышен лимит попыток подбора ({maxAttempts}) для узла {host}";
                            stopAll = true;
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
                        attemptEntryPoint, intermediateGateways, targetServer, options.Passwords, options.Timeout, ct).ConfigureAwait(false);

                    if (result.Success)
                    {
                        var candidateId = Guid.NewGuid();
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

                        var discoveredLinksFrom = new List<Guid>();
                        if (previousServerId.HasValue)
                        {
                            discoveredLinksFrom.Add(previousServerId.Value);
                        }
                        else if (attemptEntryPoint?.StartupServerId is Guid startupId &&
                                 startupId != candidateId && config.FindServer(startupId) is not null)
                        {
                            discoveredLinksFrom.Add(startupId);
                        }

                        var candidate = new DiscoveredServerCandidate
                        {
                            Id = candidateId,
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
                            DiscoveredLinksFrom = discoveredLinksFrom,
                            RequiredPreviousServerId = previousServerId,
                            VerifiedProxyId = attemptEntryPoint?.Id,
                            HostKeyFingerprint = result.HostKeyFingerprint,
                            HostKeyAlgorithm = result.HostKeyAlgorithm,
                            HostKeyBits = result.HostKeyBits,
                            StatusMessage = intermediateGateways.Count > 0 ? "Обнаружен через шлюз" : "Готов к созданию",
                            IsSelected = true
                        };

                        Log($"Хост {host}: вход выполнен (пользователь {u}) через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}; повышение прав: {result.Escalation?.StatusDescription ?? "не подтверждено"}.");
                        return (candidate, null);
                    }

                    if (result.IsHostKeyRejected)
                    {
                        Log($"Хост {host}: host key отклонён через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}.");
                        currentStage = "HostKey";
                        lastReason = result.ErrorMessage;
                        stopAll = true;
                        break;
                    }
                    else if (result.IsAuthFailure)
                    {
                        Log($"Хост {host}: вход (пользователь {u}) не удался через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)} (аутентификация).");
                        currentStage = "Аутентификация";
                        lastReason = $"Неверный логин или пароль: {result.ErrorMessage}";
                        break;
                    }
                    else if (result.IsUnreachable)
                    {
                        Log($"Хост {host}: соединение (пользователь {u}) недоступно через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}.");
                        currentStage = "Доступность";
                        lastReason = $"Сбой соединения: {result.ErrorMessage}";
                    }
                }

                if (stopAll) break;
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
            AutoSelectJumpHost = updatedOptions.AutoSelectJumpHost,
            MaxParallelConnections = updatedOptions.MaxParallelConnections,
            MaxAttemptsPerHost = updatedOptions.MaxAttemptsPerHost,
            Timeout = updatedOptions.Timeout,
            EndpointProbeTimeout = updatedOptions.EndpointProbeTimeout
        };

        var retryEngine = new SmartDiscoveryEngine(
            config, retryOptions, probe, progress, existingGateways, hostKeyVerifier, log, ensureEntryPoint);
        Log($"Повтор проверки: {retryHosts.Count} хостов.");
        var result = await retryEngine.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        Log($"Повтор проверки завершён: найдено {result.Discovered.Count}, проблемных {result.Problematic.Count}.");
        return result;
    }

    private IReadOnlyList<BaseProxy?> ResolveEntryPoints()
    {
        if (!options.AutoSelectJumpHost)
            return [options.JumpHost];

        var enabled = config.BaseProxies
            .Where(proxy => proxy.Enabled)
            .DistinctBy(proxy => proxy.Id)
            .ToList();
        if (enabled.Count == 0) return [null];

        IEnumerable<ManagedServer> scopedServers;
        if (options.TargetGroupId.HasValue)
        {
            var group = config.FindGroup(options.TargetGroupId.Value);
            scopedServers = group != null ? ServersInGroup(group) : Enumerable.Empty<ManagedServer>();
        }
        else
        {
            scopedServers = config.AllServers();
        }

        var usage = enabled.ToDictionary(proxy => proxy.Id, _ => 0);
        foreach (var server in scopedServers)
        {
            var preferred = server.PreferredProxyId ?? server.PreferredRoute?.ProxyId;
            if (preferred.HasValue && usage.ContainsKey(preferred.Value))
                usage[preferred.Value]++;
        }

        var plannerOrder = RoutePlanner.OrderedProxies(config)
            .Select((proxy, index) => (proxy.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index);
        return enabled
            .OrderByDescending(proxy => usage[proxy.Id])
            .ThenBy(proxy => proxy.Id == config.PreferredProxyId ? 0 : 1)
            .ThenBy(proxy => plannerOrder.TryGetValue(proxy.Id, out var index) ? index : int.MaxValue)
            .ThenBy(proxy => proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(proxy => (BaseProxy?)proxy)
            .ToArray();
    }

    private async Task<bool> EnsureEntryPointReadyAsync(BaseProxy proxy, CancellationToken cancellationToken)
    {
        if (ensureEntryPoint == null || proxy.StartupServerId is null)
            return true;

        bool ready;
        try
        {
            ready = await GetEntryPointReadinessTask(proxy, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Log($"Точка входа {proxy.Name}: не удалось подтвердить готовность.");
            return false;
        }

        if (!ready)
            Log($"Точка входа {proxy.Name} не готова.");
        return ready;
    }

    private Task<bool> GetEntryPointReadinessTask(BaseProxy proxy, CancellationToken cancellationToken)
    {
        lock (entryPointReadinessLock)
        {
            if (entryPointReadiness.TryGetValue(proxy.Id, out var task)) return task;
            try
            {
                task = ensureEntryPoint!(proxy, cancellationToken);
            }
            catch (Exception ex)
            {
                task = Task.FromException<bool>(ex);
            }
            entryPointReadiness[proxy.Id] = task;
            return task;
        }
    }

    private void LogScanStart(int hostCount, IReadOnlyList<BaseProxy?> entryPoints)
    {
        var groupName = options.TargetGroupId.HasValue
            ? config.FindGroup(options.TargetGroupId.Value)?.Name ?? "неизвестная группа"
            : "без группы";
        var mode = options.AutoSelectJumpHost ? "автовыбор точки входа" : "точка входа задана вручную";
        var points = string.Join(", ", entryPoints.Select(DescribeEntryPoint));
        Log($"Сканирование: хостов {hostCount}, группа {groupName}, режим: {mode}, точки входа: {points}.");
    }

    private static string DescribeEntryPoint(BaseProxy? proxy) =>
        proxy is null ? "прямое подключение" : $"точка входа {proxy.Name}";

    private string DescribeVerifiedEntryPoint(Guid? proxyId) =>
        proxyId is null
            ? "прямое подключение"
            : config.BaseProxies.FirstOrDefault(p => p.Id == proxyId.Value) is { } proxy
                ? $"точка входа {proxy.Name}"
                : "неизвестная точка входа";

    private static IEnumerable<ManagedServer> ServersInGroup(ServerGroup group)
    {
        foreach (var server in group.Servers) yield return server;
        foreach (var child in group.Groups)
            foreach (var server in ServersInGroup(child))
                yield return server;
    }

    private void Log(string message)
    {
        if (log is null) return;
        try
        {
            log(message);
        }
        catch
        {
        }
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

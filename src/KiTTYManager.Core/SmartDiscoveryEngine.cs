using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KiTTYManager.Core;

public sealed record SmartDiscoveryResult(
    List<DiscoveredServerCandidate> Discovered,
    List<ProblematicServerCandidate> Problematic);

public interface IDiscoveryGatewaySession : IAsyncDisposable, IDisposable
{
    Task<bool> ProbeReachabilityAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, bool>> ProbeBatchReachabilityAsync(IReadOnlyList<string> hosts, int port, TimeSpan timeout, CancellationToken cancellationToken);
    Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(ManagedServer target, IReadOnlyList<string> candidatePasswords, TimeSpan timeout, CancellationToken cancellationToken, int attemptsPerPassword = 1);
}

public interface ISmartDiscoveryProbe
{
    Task<bool> ProbeReachabilityAsync(
        BaseProxy? jumpHost, string host, int port, TimeSpan timeout, CancellationToken cancellationToken);

    Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        ManagedServer target, IReadOnlyList<string> candidatePasswords,
        TimeSpan timeout, CancellationToken cancellationToken,
        int attemptsPerPassword = 1);

    Task<bool> ProbeHopReachabilityAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        string targetHost, int targetPort, TimeSpan timeout, CancellationToken cancellationToken);

    async Task<IReadOnlyDictionary<string, bool>> ProbeHopBatchReachabilityAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        IReadOnlyList<string> targetHosts, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in targetHosts)
        {
            results[host] = await ProbeHopReachabilityAsync(jumpHost, intermediateGateways, host, targetPort, timeout, cancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    Task<IDiscoveryGatewaySession?> OpenGatewaySessionAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<IDiscoveryGatewaySession?>(null);
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

    public virtual async Task<IReadOnlyDictionary<string, bool>> ProbeHopBatchReachabilityAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        IReadOnlyList<string> targetHosts, int targetPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (targetHosts.Count == 0) return results;

        if (intermediateGateways.Count == 0)
        {
            foreach (var host in targetHosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[host] = await ProbeReachabilityAsync(jumpHost, host, targetPort, timeout, cancellationToken).ConfigureAwait(false);
            }
            return results;
        }

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
            if (route.Client == null || !route.Client.IsConnected)
            {
                foreach (var host in targetHosts)
                    results[host] = false;
                return results;
            }

            var maxBatchParallel = Math.Min(8, Math.Max(1, targetHosts.Count));
            using var batchSemaphore = new SemaphoreSlim(maxBatchParallel, maxBatchParallel);

            var probeTasks = targetHosts.Select(async host =>
            {
                await batchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reachable = false;
                    ForwardedPortLocal? forward = null;
                    try
                    {
                        forward = new ForwardedPortLocal("127.0.0.1", 0, host, (uint)targetPort);
                        lock (route.Client)
                        {
                            route.Client.AddForwardedPort(forward);
                        }
                        forward.Start();

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(timeout);
                        using var client = new TcpClient();
                        await client.ConnectAsync(System.Net.IPAddress.Loopback, checked((int)forward.BoundPort), cts.Token).ConfigureAwait(false);
                        var banner = new byte[1];
                        var read = await client.GetStream().ReadAsync(banner, 0, 1, cts.Token).ConfigureAwait(false);
                        reachable = read > 0;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        reachable = false;
                    }
                    finally
                    {
                        if (forward != null)
                        {
                            try { forward.Stop(); } catch { }
                            try
                            {
                                lock (route.Client)
                                {
                                    route.Client.RemoveForwardedPort(forward);
                                }
                            }
                            catch { }
                            forward.Dispose();
                        }
                    }

                    return (Host: host, Reachable: reachable);
                }
                finally
                {
                    batchSemaphore.Release();
                }
            });

            var probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            foreach (var r in probeResults)
            {
                results[r.Host] = r.Reachable;
            }

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            foreach (var host in targetHosts)
                results[host] = false;
            return results;
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
        TimeSpan timeout, CancellationToken cancellationToken,
        int attemptsPerPassword = 1)
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
                route.Client, target.Username, target.Password, candidatePasswords, timeout, cancellationToken,
                attemptsPerPassword).ConfigureAwait(false);

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

    public virtual async Task<IDiscoveryGatewaySession?> OpenGatewaySessionAsync(
        BaseProxy? jumpHost, IReadOnlyList<ManagedServer> intermediateGateways,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (intermediateGateways.Count == 0) return null;

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

        ActiveRoute? route = null;
        ForwardedPortDynamic? dynamicPort = null;
        try
        {
            route = await ssh.ConnectProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (route.Client == null || !route.Client.IsConnected)
            {
                route.Dispose();
                return null;
            }

            dynamicPort = new ForwardedPortDynamic("127.0.0.1", 0);
            route.Client.AddForwardedPort(dynamicPort);
            dynamicPort.Start();

            var socksProxy = new BaseProxy
            {
                Id = Guid.NewGuid(),
                Name = "GatewaySocks5",
                Host = "127.0.0.1",
                Port = checked((int)dynamicPort.BoundPort),
                Enabled = true
            };

            return new DefaultDiscoveryGatewaySession(route, dynamicPort, socksProxy, autoConfirmHostKeys, hostKeyVerifier);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            dynamicPort?.Dispose();
            route?.Dispose();
            throw;
        }
        catch
        {
            dynamicPort?.Dispose();
            route?.Dispose();
            return null;
        }
    }

    private sealed class DefaultDiscoveryGatewaySession : IDiscoveryGatewaySession
    {
        private readonly ActiveRoute route;
        private readonly ForwardedPortDynamic dynamicPort;
        private readonly BaseProxy socksProxy;
        private readonly bool autoConfirmHostKeys;
        private readonly Func<ManagedServer, string, bool>? hostKeyVerifier;
        private int disposed;

        public DefaultDiscoveryGatewaySession(
            ActiveRoute route,
            ForwardedPortDynamic dynamicPort,
            BaseProxy socksProxy,
            bool autoConfirmHostKeys,
            Func<ManagedServer, string, bool>? hostKeyVerifier)
        {
            this.route = route;
            this.dynamicPort = dynamicPort;
            this.socksProxy = socksProxy;
            this.autoConfirmHostKeys = autoConfirmHostKeys;
            this.hostKeyVerifier = hostKeyVerifier;
        }

        public async Task<bool> ProbeReachabilityAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return await Socks5TcpProbe.CanConnectAsync(socksProxy, host, port, timeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyDictionary<string, bool>> ProbeBatchReachabilityAsync(
            IReadOnlyList<string> hosts, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (hosts.Count == 0) return results;

            var maxParallel = Math.Min(8, Math.Max(1, hosts.Count));
            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

            var tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ok = await ProbeReachabilityAsync(host, port, timeout, cancellationToken).ConfigureAwait(false);
                    return (Host: host, Reachable: ok);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var r = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var item in r)
            {
                results[item.Host] = item.Reachable;
            }
            return results;
        }

        public async Task<SmartDiscoveryAuthResult> AuthenticateAndProbeAsync(
            ManagedServer target, IReadOnlyList<string> candidatePasswords,
            TimeSpan timeout, CancellationToken cancellationToken,
            int attemptsPerPassword = 1)
        {
            var probe = new DefaultSmartDiscoveryProbe(autoConfirmHostKeys, hostKeyVerifier);
            return await probe.AuthenticateAndProbeAsync(
                socksProxy, [], target, candidatePasswords, timeout, cancellationToken,
                attemptsPerPassword).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { dynamicPort.Stop(); } catch { }
            try { route.Client?.RemoveForwardedPort(dynamicPort); } catch { }
            try { dynamicPort.Dispose(); } catch { }
            try { route.Dispose(); } catch { }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal static async Task<PrivilegeEscalationResult> ProbePrivilegeEscalationCoreAsync(
        Func<string, TimeSpan, CancellationToken, Task<(int ExitStatus, string Result)>> runCommand,
        Func<string, string, string, TimeSpan, CancellationToken, Task<(bool Success, string Method, string Command, string Password)>> tryPasswordEscalation,
        string username, string loginPassword, IReadOnlyList<string> candidatePasswords,
        TimeSpan timeout, CancellationToken cancellationToken,
        int attemptsPerPassword = 1)
    {
        var attempts = Math.Max(1, attemptsPerPassword);

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

        var otherPasswords = candidatePasswords.Where(p => p != loginPassword && p.Length > 0).Distinct().ToList();

        // 2. First priority: su - (su -c 'id') password guessing (other passwords first, login password second)
        foreach (var pw in otherPasswords)
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, method, cmd, pass) = await tryPasswordEscalation("su", pw, "root", timeout, cancellationToken).ConfigureAwait(false);
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
        }

        if (!string.IsNullOrEmpty(loginPassword))
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, method, cmd, pass) = await tryPasswordEscalation("su", loginPassword, "пользователя", timeout, cancellationToken).ConfigureAwait(false);
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
        }

        // 3. Second priority (only if all su - fail):
        // 3a. Check passwordless sudo (run sudo -k first to invalidate any cached ticket)
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

        // 3b. sudo -i password guessing (other passwords first, login password second)
        foreach (var pw in otherPasswords)
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, method, cmd, pass) = await tryPasswordEscalation("sudo", pw, "root", timeout, cancellationToken).ConfigureAwait(false);
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
        }

        if (!string.IsNullOrEmpty(loginPassword))
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, method, cmd, pass) = await tryPasswordEscalation("sudo", loginPassword, "пользователя", timeout, cancellationToken).ConfigureAwait(false);
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
        }

        // 4. If neither works -> Не повышено
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
        IReadOnlyList<string> candidatePasswords, TimeSpan timeout, CancellationToken cancellationToken,
        int attemptsPerPassword = 1)
    {
        return await ProbePrivilegeEscalationCoreAsync(
            (cmd, to, ct) => RunCommandAsync(client, cmd, to, ct),
            async (method, pw, label, to, ct) =>
            {
                var res = await TryPasswordEscalationAsync(client, method, pw, label, to, ct).ConfigureAwait(false);
                return (res.Success, res.Method, res.RootCommand, res.RootPassword);
            },
            username, loginPassword, candidatePasswords, timeout, cancellationToken,
            attemptsPerPassword).ConfigureAwait(false);
    }

    private static async Task<PrivilegeEscalationResult> TryPasswordEscalationAsync(
        SshClient client, string method, string password, string passwordKindLabel, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepCts.CancelAfter(timeout);
        var ct = stepCts.Token;

        if (method == "sudo")
        {
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

            return new PrivilegeEscalationResult { Method = "Не повышено", RootCommand = "", Success = false };
        }

        // method == "su"
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
    private readonly IProgress<DiscoveredServerCandidate>? candidateProgress;
    private readonly IProgress<ProblematicServerCandidate>? problemProgress;
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
        Func<BaseProxy, CancellationToken, Task<bool>>? ensureEntryPoint = null,
        IProgress<DiscoveredServerCandidate>? candidateProgress = null,
        IProgress<ProblematicServerCandidate>? problemProgress = null)
    {
        this.config = config;
        this.options = options;
        this.hostKeyVerifier = hostKeyVerifier;
        this.probe = probe ?? new DefaultSmartDiscoveryProbe(config.AutoConfirmHostKeys, hostKeyVerifier);
        this.progress = progress;
        this.initialGateways = initialGateways?.Select(CloneCandidate).ToList();
        this.log = log;
        this.ensureEntryPoint = ensureEntryPoint;
        this.candidateProgress = candidateProgress;
        this.problemProgress = problemProgress;
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

        var (rootGroupName, subgroupPath) =
            SmartDiscoverySessionCreator.ResolveGroupAssignment(config, options.TargetGroupId, options.TargetSubgroupName);

        // Fast Duplicate Detection by (Host, Username)
        var hostUsernames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allConfigServers = config.AllServers().ToList();

        foreach (var host in targetHosts)
        {
            var remainingUsernames = new List<string>();
            foreach (var u in usernames)
            {
                var existingServer = allConfigServers.FirstOrDefault(s =>
                    (s.Port <= 0 || s.Port == 22) &&
                    string.Equals((s.Host ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(s.Username) &&
                    string.Equals(s.Username, u, StringComparison.Ordinal));

                if (existingServer != null)
                {
                    if (discovered.Any(d => d.Id == existingServer.Id))
                        continue;

                    var existingRoute = RoutePlanner.Candidates(config, existingServer.Id).FirstOrDefault()
                        ?? (existingServer.PreferredRoute is not null ? RoutePlanner.CandidateFromCached(config, existingServer.PreferredRoute) : null);
                    var routeHops = existingRoute != null && existingRoute.Servers.Count > 0
                        ? existingRoute.Servers.Select(s => new RouteHop(s.Id, s.Name, s.Host, s.Port > 0 ? s.Port : 22)).ToList()
                        : new List<RouteHop> { new RouteHop(existingServer.Id, existingServer.Name, host, existingServer.Port > 0 ? existingServer.Port : 22) };
                    var verifiedProxyId = existingRoute != null && !existingRoute.WithoutProxy && existingRoute.Proxy.Id != Guid.Empty
                        ? existingRoute.Proxy.Id
                        : (Guid?)null;

                    var dupCandidate = new DiscoveredServerCandidate
                    {
                        Id = existingServer.Id,
                        Host = host,
                        Port = existingServer.Port > 0 ? existingServer.Port : 22,
                        Hostname = !string.IsNullOrWhiteSpace(existingServer.Host) ? existingServer.Host : host,
                        ProposedName = existingServer.Name,
                        GroupId = options.TargetGroupId,
                        GroupName = rootGroupName,
                        SubgroupName = subgroupPath,
                        Username = existingServer.Username,
                        Password = existingServer.Password,
                        PrivilegeEscalation = new PrivilegeEscalationResult(),
                        RouteHops = routeHops,
                        DiscoveredLinksFrom = [],
                        RequiredPreviousServerId = existingServer.RequiredPreviousServerId,
                        ExistingServerId = existingServer.Id,
                        VerifiedProxyId = existingServer.PreferredProxyId ?? verifiedProxyId,
                        HostKeyFingerprint = existingServer.HostKeyFingerprint,
                        HostKeyAlgorithm = existingServer.HostKeyAlgorithm,
                        HostKeyBits = existingServer.HostKeyBits,
                        StatusMessage = "Дубль: сессия уже сохранена в программе",
                        IsDuplicate = true,
                        IsSelected = false
                    };

                    discovered.Add(dupCandidate);
                    candidateProgress?.Report(CloneCandidate(dupCandidate));
                    reservedNames.Add(existingServer.Name);
                    Log($"Хост {host}: найден дубль существующей сессии \"{existingServer.Name}\" (пользователь {u}).");
                }
                else
                {
                    remainingUsernames.Add(u);
                }
            }

            if (remainingUsernames.Count > 0)
            {
                hostUsernames[host] = remainingUsernames;
            }
        }

        var activeHosts = targetHosts.Where(hostUsernames.ContainsKey).ToList();
        var hostAttempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastHostProblems = new Dictionary<string, ProblematicServerCandidate>(StringComparer.OrdinalIgnoreCase);
        var syncLock = new object();

        ReportProgress(SmartDiscoveryProgressKind.Starting, "", $"Запуск сканирования {totalCount} хостов...", discovered.Count, problematic.Count, totalCount, 0);

        if (totalCount == 0 || activeHosts.Count == 0)
        {
            ReportProgress(SmartDiscoveryProgressKind.Completed, "", $"Сканирование завершено: найдено {discovered.Count}, проблемных {problematic.Count}.", discovered.Count, problematic.Count, totalCount, 100);
            return new SmartDiscoveryResult(discovered, problematic);
        }

        var maxParallel = Math.Max(1, options.MaxParallelConnections);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var entryPoints = ResolveEntryPoints();

        LogScanStart(activeHosts.Count, entryPoints);

        async Task ProbeDirectReachabilityAsync(List<string> hostsToProbe, Dictionary<string, BaseProxy?> reachableVia)
        {
            var reachabilityTasks = hostsToProbe.Select(async host =>
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
        }

        async Task AuthenticateDirectHostsAsync(
            List<string> directlyReachable,
            Dictionary<string, BaseProxy?> reachableVia,
            List<string> unreachableHosts)
        {
            var authTasks = directlyReachable.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    reachableVia.TryGetValue(host, out var winningEntryPoint);
                    hostUsernames.TryGetValue(host, out var uList);
                    var (candidate, problem) = await AuthenticateHostAsync(
                        host, winningEntryPoint, intermediateGateways: [], hopHops: [], cancellationToken,
                        overrideUsernames: uList).ConfigureAwait(false);

                    lock (syncLock)
                    {
                        if (candidate != null)
                        {
                            discovered.Add(candidate);
                            candidateProgress?.Report(CloneCandidate(candidate));
                            reservedNames.Add(candidate.ProposedName);
                            // Prioritize confirmed credential
                            confirmedCredentials.RemoveAll(c => c.User == candidate.Username && c.Pass == candidate.Password);
                            confirmedCredentials.Insert(0, (candidate.Username, candidate.Password));
                            ReportProgress(SmartDiscoveryProgressKind.HostSucceeded, host, $"Сессия найдена: {candidate.ProposedName}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                            Log($"Хост {host}: сессия \"{candidate.ProposedName}\" найдена через {DescribeVerifiedEntryPoint(candidate.VerifiedProxyId)}.");
                        }
                        else if (problem != null)
                        {
                            if (problem.Stage == "Доступность")
                            {
                                unreachableHosts.Add(host);
                                Log($"Хост {host}: сбой доступности при прямой проверке ({problem.Reason}), направлен в multi-hop поиск.");
                            }
                            else
                            {
                                problematic.Add(problem);
                                problemProgress?.Report(problem);
                                ReportProgress(SmartDiscoveryProgressKind.HostFailed, host, $"Ошибка: {problem.Reason}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                                Log($"Хост {host}: ошибка на этапе \"{problem.Stage}\".");
                            }
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(authTasks).ConfigureAwait(false);
        }

        async Task ProbeGatewayWaveAsync(
            List<string> hostsToProbe,
            List<DiscoveredServerCandidate> gatewaysToUse,
            Dictionary<Guid, SavedGatewayRoute> routeMap,
            int maxHops = 3)
        {
            var availableGateways = gatewaysToUse.Where(g => !g.IsDuplicate || routeMap.ContainsKey(g.Id)).ToList();
            while (hostsToProbe.Count > 0 && availableGateways.Count > 0)
            {
                var nextWaveGateways = new List<DiscoveredServerCandidate>();

                foreach (var gateway in availableGateways)
                {
                    var gatewayChain = BuildGatewayChain(
                        gateway, discovered.Concat(initialGateways ?? []).ToList(), routeMap);
                    if (gatewayChain is null) continue;
                    var savedPrefixLength = gatewayChain.SavedRoute?.Servers.Count ?? 0;
                    if (gateway.RouteHops.Count - savedPrefixLength >= maxHops) continue;

                    var gatewayEntryPoints = !options.AutoSelectJumpHost
                        ? entryPoints
                        : gatewayChain.SavedRoute is { } savedRoute
                            ? (IReadOnlyList<BaseProxy?>)[savedRoute.WithoutProxy ? null : savedRoute.Proxy]
                            : entryPoints;

                    var nonRetryableOnThisGateway = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entryPoint in gatewayEntryPoints)
                    {
                        if (hostsToProbe.Count == 0) break;
                        cancellationToken.ThrowIfCancellationRequested();

                        if (entryPoint != null && !await EnsureEntryPointReadyAsync(entryPoint, cancellationToken).ConfigureAwait(false))
                            continue;

                        var candidateHosts = new List<string>();
                        lock (syncLock)
                        {
                            foreach (var targetHost in hostsToProbe)
                            {
                                if (nonRetryableOnThisGateway.Contains(targetHost))
                                    continue;
                                if (options.MaxAttemptsPerHost > 0 && hostAttempts.TryGetValue(targetHost, out var att) && att >= options.MaxAttemptsPerHost)
                                    continue;
                                if (gateway.RouteHops.Any(h => string.Equals(h.Host, targetHost, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                candidateHosts.Add(targetHost);
                            }
                        }

                        if (candidateHosts.Count == 0) continue;

                        IDiscoveryGatewaySession? gatewaySession = null;
                        try
                        {
                            gatewaySession = await probe.OpenGatewaySessionAsync(
                                entryPoint, gatewayChain.Servers, options.Timeout, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch { }

                        try
                        {
                            IReadOnlyDictionary<string, bool> batchResults;
                            if (gatewaySession != null)
                            {
                                batchResults = await gatewaySession.ProbeBatchReachabilityAsync(
                                    candidateHosts, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                batchResults = await probe.ProbeHopBatchReachabilityAsync(
                                    entryPoint, gatewayChain.Servers, candidateHosts, 22, options.EndpointProbeTimeout, cancellationToken).ConfigureAwait(false);
                            }

                            foreach (var targetHost in candidateHosts)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                lock (syncLock)
                                {
                                    if (!hostsToProbe.Contains(targetHost, StringComparer.OrdinalIgnoreCase))
                                        continue;
                                    if (options.MaxAttemptsPerHost > 0 && hostAttempts.TryGetValue(targetHost, out var used) && used >= options.MaxAttemptsPerHost)
                                        continue;
                                }

                                var canHop = batchResults.TryGetValue(targetHost, out var ok) && ok;
                                Log($"Хост {targetHost} через шлюз {gateway.ProposedName} и {DescribeEntryPoint(entryPoint)} — {(canHop ? "доступен" : "недоступен")}.");

                                if (!canHop) continue;

                                hostUsernames.TryGetValue(targetHost, out var uList);
                                var (candidate, hopProblem) = await AuthenticateHostAsync(
                                    targetHost, entryPoint, gatewayChain.Servers, gateway.RouteHops, cancellationToken,
                                    previousServerId: gateway.Id,
                                    lockEntryPoint: gatewayChain.SavedRoute is not null,
                                    gatewaySession: gatewaySession,
                                    overrideUsernames: uList).ConfigureAwait(false);

                                if (candidate != null)
                                {
                                    if (!candidate.IsDuplicate)
                                    {
                                        candidate.RequiredPreviousServerId = gateway.Id;
                                        candidate.DiscoveredLinksFrom = [gateway.Id];
                                        candidate.StatusMessage = $"Обнаружен через шлюз {gateway.ProposedName}";
                                    }
                                    else
                                    {
                                        if (!candidate.DiscoveredLinksFrom.Contains(gateway.Id))
                                            candidate.DiscoveredLinksFrom.Add(gateway.Id);
                                    }

                                    lock (syncLock)
                                    {
                                        discovered.Add(candidate);
                                        candidateProgress?.Report(CloneCandidate(candidate));
                                        reservedNames.Add(candidate.ProposedName);
                                        if (!candidate.IsDuplicate || routeMap.ContainsKey(candidate.Id))
                                        {
                                            nextWaveGateways.Add(candidate);
                                        }
                                        hostsToProbe.RemoveAll(h => string.Equals(h, targetHost, StringComparison.OrdinalIgnoreCase));
                                        lastHostProblems.Remove(targetHost);

                                        confirmedCredentials.RemoveAll(c => c.User == candidate.Username && c.Pass == candidate.Password);
                                        confirmedCredentials.Insert(0, (candidate.Username, candidate.Password));

                                        ReportProgress(SmartDiscoveryProgressKind.HostSucceeded, targetHost, $"Сессия найдена через {gateway.ProposedName}: {candidate.ProposedName}", discovered.Count, problematic.Count, totalCount, CalculatePercentage(discovered.Count, problematic.Count, totalCount));
                                    }
                                    Log($"Хост {targetHost}: сессия \"{candidate.ProposedName}\" найдена через шлюз {gateway.ProposedName} и {DescribeVerifiedEntryPoint(candidate.VerifiedProxyId)}.");
                                }
                                else if (hopProblem != null)
                                {
                                    lock (syncLock)
                                    {
                                        lastHostProblems[targetHost] = hopProblem;
                                    }

                                    if (hopProblem.Stage != "Доступность")
                                    {
                                        nonRetryableOnThisGateway.Add(targetHost);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (gatewaySession != null)
                            {
                                try
                                {
                                    await gatewaySession.DisposeAsync().ConfigureAwait(false);
                                }
                                catch { }
                            }
                        }
                    }
                }

                availableGateways = nextWaveGateways.Where(g => !g.IsDuplicate || routeMap.ContainsKey(g.Id)).ToList();
            }
        }

        try
        {
            var (savedGatewaySeeds, savedRouteByGatewayId) = ResolveSavedGateways();
            var allKnownGateways = savedGatewaySeeds
                .Concat(discovered.Where(g => g.IsDuplicate && savedRouteByGatewayId.ContainsKey(g.Id)))
                .Concat((initialGateways ?? []).Where(g => !g.IsDuplicate || savedRouteByGatewayId.ContainsKey(g.Id)))
                .DistinctBy(gateway => gateway.Id)
                .ToList();

            var preferGateways = options.PreferGroupGateways && options.TargetGroupId.HasValue && allKnownGateways.Count > 0;

            if (preferGateways)
            {
                ReportProgress(SmartDiscoveryProgressKind.MultiHopProbing, "", $"Фаза 1: Проверка через шлюзы группы ({activeHosts.Count} хостов)...", discovered.Count, problematic.Count, totalCount, 15);
                Log($"Фаза 1: проверка через шлюзы группы ({allKnownGateways.Count} шлюзов).");

                var unreachableHosts = activeHosts.ToList();
                await ProbeGatewayWaveAsync(unreachableHosts, allKnownGateways, savedRouteByGatewayId).ConfigureAwait(false);

                if (unreachableHosts.Count > 0)
                {
                    ReportProgress(SmartDiscoveryProgressKind.ReachabilityProbing, "", $"Фаза 2: Проверка прямой сетевой доступности ({unreachableHosts.Count} оставшихся хостов)...", discovered.Count, problematic.Count, totalCount, 50);
                    Log($"Фаза 2: проверка прямой доступности для {unreachableHosts.Count} хостов.");

                    var reachableVia = new Dictionary<string, BaseProxy?>(StringComparer.OrdinalIgnoreCase);
                    await ProbeDirectReachabilityAsync(unreachableHosts, reachableVia).ConfigureAwait(false);

                    var directlyReachable = unreachableHosts.Where(reachableVia.ContainsKey).ToList();
                    unreachableHosts.RemoveAll(reachableVia.ContainsKey);

                    if (directlyReachable.Count > 0)
                    {
                        var beforeDirectCount = discovered.Count;
                        ReportProgress(SmartDiscoveryProgressKind.AuthenticationProbing, "", $"Фаза 3: Проверка учётных данных ({directlyReachable.Count} доступных узлов)...", discovered.Count, problematic.Count, totalCount, 75);
                        await AuthenticateDirectHostsAsync(directlyReachable, reachableVia, unreachableHosts).ConfigureAwait(false);

                        var newlyDiscovered = discovered.Skip(beforeDirectCount).ToList();
                        if (newlyDiscovered.Count > 0 && unreachableHosts.Count > 0)
                        {
                            ReportProgress(SmartDiscoveryProgressKind.MultiHopProbing, "", $"Фаза 4: Поиск через вновь обнаруженные шлюзы ({unreachableHosts.Count} недоступных хостов)...", discovered.Count, problematic.Count, totalCount, 85);
                            Log($"Фаза 4: поиск через {newlyDiscovered.Count} вновь обнаруженных шлюзов для {unreachableHosts.Count} хостов.");
                            await ProbeGatewayWaveAsync(unreachableHosts, newlyDiscovered, savedRouteByGatewayId).ConfigureAwait(false);
                        }
                    }
                }

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
                    problemProgress?.Report(prob);
                }
            }
            else
            {
                // Phase 1: Probe reachability directly or via an entry point
                ReportProgress(SmartDiscoveryProgressKind.ReachabilityProbing, "", "Фаза 1: Проверка сетевой доступности портов 22...", discovered.Count, problematic.Count, totalCount, 5);
                Log("Фаза 1: проверка сетевой доступности порта 22.");

                var reachableVia = new Dictionary<string, BaseProxy?>(StringComparer.OrdinalIgnoreCase);
                await ProbeDirectReachabilityAsync(activeHosts, reachableVia).ConfigureAwait(false);

                var directlyReachable = activeHosts.Where(reachableVia.ContainsKey).ToList();
                var unreachableHosts = activeHosts.Where(h => !reachableVia.ContainsKey(h)).ToList();

                // Phase 2: Credential verification on directly reachable hosts
                ReportProgress(SmartDiscoveryProgressKind.AuthenticationProbing, "", $"Фаза 2: Проверка учётных данных ({directlyReachable.Count} доступных узлов)...", discovered.Count, problematic.Count, totalCount, 25);
                Log($"Фаза 2: проверка учётных данных для {directlyReachable.Count} доступных хостов.");

                await AuthenticateDirectHostsAsync(directlyReachable, reachableVia, unreachableHosts).ConfigureAwait(false);

                // Phase 3: Multi-hop discovery for remaining unreachable hosts
                if (unreachableHosts.Count > 0)
                {
                    var (postGatewaySeeds, postRouteByGatewayId) = ResolveSavedGateways();
                    var postAllGateways = postGatewaySeeds
                        .Concat(discovered.Where(g => !g.IsDuplicate || postRouteByGatewayId.ContainsKey(g.Id)))
                        .Concat((initialGateways ?? []).Where(g => !g.IsDuplicate || postRouteByGatewayId.ContainsKey(g.Id)))
                        .DistinctBy(gateway => gateway.Id)
                        .ToList();

                    if (postAllGateways.Count > 0)
                    {
                        ReportProgress(SmartDiscoveryProgressKind.MultiHopProbing, "", $"Фаза 3: Поиск multi-hop маршрутов ({unreachableHosts.Count} недоступных хостов)...", discovered.Count, problematic.Count, totalCount, 60);
                        Log($"Фаза 3: поиск multi-hop маршрутов для {unreachableHosts.Count} недоступных хостов.");

                        await ProbeGatewayWaveAsync(unreachableHosts, postAllGateways, postRouteByGatewayId).ConfigureAwait(false);
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
                    problemProgress?.Report(prob);
                }
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
            Guid? previousServerId = null,
            bool lockEntryPoint = false,
            IDiscoveryGatewaySession? gatewaySession = null,
            IReadOnlyList<string>? overrideUsernames = null)
        {
            List<(string User, string Pass)> confirmedSnapshot;
            lock (syncLock)
            {
                confirmedSnapshot = confirmedCredentials.ToList();
            }
            var targetUsers = (overrideUsernames != null && overrideUsernames.Count > 0) ? overrideUsernames : usernames;
            var orderedCredentials = GetPrioritizedCredentials(targetUsers, passwords, confirmedSnapshot);
            var maxAttempts = options.MaxAttemptsPerHost;
            var attemptsPerPassword = Math.Max(1, options.AttemptsPerPassword);
            string lastReason = "Не удалось подобрать учётные данные";
            string currentStage = "Аутентификация";
            var hadAuthFailure = false;
            string? lastAuthReason = null;
            string? lastUnreachableReason = null;

            var credentialsQueue = new Queue<(string User, string Pass)>(orderedCredentials);
            var retried = new HashSet<(string User, string Pass)>();
            var stopAll = false;

            while (credentialsQueue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (u, p) = credentialsQueue.Dequeue();

                for (var attemptIndex = 0; attemptIndex < attemptsPerPassword; attemptIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var reachedAny = false;

                    IReadOnlyList<BaseProxy?> attemptEntryPoints = lockEntryPoint
                        ? new BaseProxy?[] { entryPoint }
                        : ResolveAttemptEntryPoints(entryPoint);
                    foreach (var attemptEntryPoint in attemptEntryPoints)
                    {
                        if (attemptEntryPoint != null && !await EnsureEntryPointReadyAsync(attemptEntryPoint, ct).ConfigureAwait(false))
                            continue;

                        lock (syncLock)
                        {
                            if (maxAttempts > 0 && hostAttempts.TryGetValue(host, out var currentAttempts) && currentAttempts >= maxAttempts)
                            {
                                lastReason = $"Превышен лимит попыток подбора ({maxAttempts}) для узла {host}";
                                stopAll = true;
                                break;
                            }
                        }

                        var targetServer = new ManagedServer
                        {
                            Host = host,
                            Port = 22,
                            Username = u,
                            Password = p,
                            UseKeyboardInteractive = true
                        };

                        var result = gatewaySession != null
                            ? await gatewaySession.AuthenticateAndProbeAsync(
                                targetServer, options.Passwords, options.Timeout, ct,
                                attemptsPerPassword).ConfigureAwait(false)
                            : await probe.AuthenticateAndProbeAsync(
                                attemptEntryPoint, intermediateGateways, targetServer, options.Passwords, options.Timeout, ct,
                                attemptsPerPassword).ConfigureAwait(false);

                        if (result.Success)
                        {
                            reachedAny = true;
                            lock (syncLock)
                            {
                                hostAttempts.TryGetValue(host, out var used);
                                hostAttempts[host] = used + 1;
                            }

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

                            var existingServer = config.AllServers().FirstOrDefault(s =>
                                (s.Port <= 0 || s.Port == 22) &&
                                string.Equals((s.Host ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.Username, u, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(s.Username));
                            var isDuplicate = existingServer is not null;

                            var (rootGroupName, subgroupPath) =
                                SmartDiscoverySessionCreator.ResolveGroupAssignment(config, options.TargetGroupId, options.TargetSubgroupName);

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
                                Id = isDuplicate ? existingServer!.Id : candidateId,
                                Host = host,
                                Port = 22,
                                Hostname = result.Hostname,
                                ProposedName = proposedName,
                                GroupId = options.TargetGroupId,
                                GroupName = rootGroupName,
                                SubgroupName = subgroupPath,
                                Username = u,
                                Password = p,
                                PrivilegeEscalation = result.Escalation ?? new(),
                                RouteHops = hops,
                                DiscoveredLinksFrom = discoveredLinksFrom,
                                RequiredPreviousServerId = previousServerId,
                                ExistingServerId = existingServer?.Id,
                                VerifiedProxyId = attemptEntryPoint?.Id,
                                HostKeyFingerprint = result.HostKeyFingerprint,
                                HostKeyAlgorithm = result.HostKeyAlgorithm,
                                HostKeyBits = result.HostKeyBits,
                                StatusMessage = isDuplicate
                                    ? "Дубль: сессия с таким хостом уже существует"
                                    : intermediateGateways.Count > 0 ? "Обнаружен через шлюз" : "Готов к созданию",
                                IsDuplicate = isDuplicate,
                                IsSelected = !isDuplicate
                            };

                            Log($"Хост {host}: вход выполнен (пользователь {u}) через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}; повышение прав: {result.Escalation?.StatusDescription ?? "не подтверждено"}.");
                            return (candidate, null);
                        }

                        if (result.IsHostKeyRejected)
                        {
                            reachedAny = true;
                            lock (syncLock)
                            {
                                hostAttempts.TryGetValue(host, out var used);
                                hostAttempts[host] = used + 1;
                            }
                            Log($"Хост {host}: host key отклонён через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}.");
                            currentStage = "HostKey";
                            lastReason = result.ErrorMessage;
                            stopAll = true;
                            break;
                        }
                        else if (result.IsAuthFailure)
                        {
                            reachedAny = true;
                            hadAuthFailure = true;
                            lastAuthReason = $"Неверный логин или пароль: {result.ErrorMessage}";
                            lock (syncLock)
                            {
                                hostAttempts.TryGetValue(host, out var used);
                                hostAttempts[host] = used + 1;
                            }
                            Log($"Хост {host}: вход (пользователь {u}) не удался через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)} (аутентификация).");
                            break;
                        }
                        else if (result.IsUnreachable)
                        {
                            Log($"Хост {host}: соединение (пользователь {u}) недоступно через {DescribeVerifiedEntryPoint(attemptEntryPoint?.Id)}.");
                            lastUnreachableReason = $"Сбой соединения: {result.ErrorMessage}";
                        }
                    }

                    if (stopAll) break;
                    if (!reachedAny)
                    {
                        if (retried.Add((u, p)))
                        {
                            credentialsQueue.Enqueue((u, p));
                        }
                        break;
                    }
                }

                if (stopAll) break;
            }

            if (currentStage != "HostKey" && !stopAll)
            {
                if (hadAuthFailure)
                {
                    currentStage = "Аутентификация";
                    if (lastAuthReason != null)
                        lastReason = lastAuthReason;
                }
                else if (lastUnreachableReason != null)
                {
                    currentStage = "Доступность";
                    lastReason = lastUnreachableReason;
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
            AutoSelectJumpHost = updatedOptions.AutoSelectJumpHost,
            MaxParallelConnections = updatedOptions.MaxParallelConnections,
            MaxAttemptsPerHost = updatedOptions.MaxAttemptsPerHost,
            Timeout = updatedOptions.Timeout,
            EndpointProbeTimeout = updatedOptions.EndpointProbeTimeout,
            PreferGroupGateways = updatedOptions.PreferGroupGateways,
            InitialGatewayServerId = updatedOptions.InitialGatewayServerId,
            AttemptsPerPassword = updatedOptions.AttemptsPerPassword
        };

        var retryEngine = new SmartDiscoveryEngine(
            config, retryOptions, probe, progress, existingGateways, hostKeyVerifier, log, ensureEntryPoint,
            candidateProgress, problemProgress);
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
        var allowedUsers = new HashSet<string>(usernames, StringComparer.Ordinal);

        foreach (var c in confirmed)
        {
            if (allowedUsers.Contains(c.User) && seen.Add(c))
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

    public static DiscoveredServerCandidate CloneCandidate(DiscoveredServerCandidate c) =>
        new()
        {
            Id = c.Id,
            Host = c.Host,
            Port = c.Port,
            Hostname = c.Hostname,
            ProposedName = c.ProposedName,
            GroupId = c.GroupId,
            GroupName = c.GroupName,
            SubgroupName = c.SubgroupName,
            Username = c.Username,
            Password = c.Password,
            ShowPassword = c.ShowPassword,
            ShowRootPassword = c.ShowRootPassword,
            PrivilegeEscalation = new PrivilegeEscalationResult
            {
                Method = c.PrivilegeEscalation.Method,
                RootCommand = c.PrivilegeEscalation.RootCommand,
                RootPassword = c.PrivilegeEscalation.RootPassword,
                StatusDescription = c.PrivilegeEscalation.StatusDescription,
                Success = c.PrivilegeEscalation.Success
            },
            RouteHops = [.. c.RouteHops],
            DiscoveredLinksFrom = [.. c.DiscoveredLinksFrom],
            RequiredPreviousServerId = c.RequiredPreviousServerId,
            ExistingServerId = c.ExistingServerId,
            VerifiedProxyId = c.VerifiedProxyId,
            StatusMessage = c.StatusMessage,
            IsSelected = c.IsSelected,
            IsDuplicate = c.IsDuplicate,
            HostKeyFingerprint = c.HostKeyFingerprint,
            HostKeyAlgorithm = c.HostKeyAlgorithm,
            HostKeyBits = c.HostKeyBits
        };

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

    private (List<DiscoveredServerCandidate> Seeds, Dictionary<Guid, SavedGatewayRoute> Routes)
        ResolveSavedGateways()
    {
        var seeds = new List<DiscoveredServerCandidate>();
        var routes = new Dictionary<Guid, SavedGatewayRoute>();

        var seedDistance = new Dictionary<Guid, int>();

        HashSet<Guid> FindIndependentServerIds(HashSet<Guid> scope)
        {
            var independentIds = new HashSet<Guid>(ConnectivityBatchPlanner.DirectlyReachable(config, scope));

            foreach (var id in scope)
            {
                if (independentIds.Contains(id)) continue;
                var server = config.FindServer(id);
                if (server?.PreferredRoute is not null)
                {
                    var cachedCandidate = RoutePlanner.CandidateFromCached(config, server.PreferredRoute);
                    if (cachedCandidate is not null &&
                        cachedCandidate.Servers.Take(cachedCandidate.Servers.Count - 1).All(p => !scope.Contains(p.Id)))
                    {
                        independentIds.Add(id);
                        continue;
                    }
                }

                var candidates = RoutePlanner.Candidates(config, id);
                if (candidates.Any(c => c.Servers.Take(c.Servers.Count - 1).All(prefix => !scope.Contains(prefix.Id))))
                {
                    independentIds.Add(id);
                }
            }

            if (independentIds.Count == 0)
            {
                foreach (var id in scope)
                {
                    var server = config.FindServer(id);
                    if ((server?.PreferredRoute is not null && RoutePlanner.CandidateFromCached(config, server.PreferredRoute) is not null) ||
                        RoutePlanner.Candidates(config, id).Count > 0)
                    {
                        independentIds.Add(id);
                    }
                }
            }

            return independentIds;
        }

        void TryAddGatewaySeed(Guid id, HashSet<Guid> scope, int distance)
        {
            if (routes.ContainsKey(id)) return;
            var server = config.FindServer(id);
            if (server is null) return;

            bool IsCompatibleWithChosenJumpHost(RouteCandidate c) =>
                options.AutoSelectJumpHost ||
                (options.JumpHost == null ? c.WithoutProxy : !c.WithoutProxy && c.Proxy.Id == options.JumpHost.Id);

            RouteCandidate? route = null;

            if (server.PreferredRoute is not null)
            {
                var cachedCandidate = RoutePlanner.CandidateFromCached(config, server.PreferredRoute);
                if (cachedCandidate is not null &&
                    IsCompatibleWithChosenJumpHost(cachedCandidate) &&
                    (distance == -1 || cachedCandidate.Servers.Take(cachedCandidate.Servers.Count - 1).All(prefix => !scope.Contains(prefix.Id))))
                {
                    route = cachedCandidate;
                }
            }

            if (route is null && (server.PreferredRoute is null || distance == -1 || !options.AutoSelectJumpHost))
            {
                var candidates = RoutePlanner.Candidates(config, id);
                var ordered = RoutePlanner.OrderPreferred(
                        config, candidates, server.PreferredRoute)
                    .Where(IsCompatibleWithChosenJumpHost)
                    .Where(candidate => distance == -1 || candidate.Servers
                        .Take(candidate.Servers.Count - 1)
                        .All(prefixServer => !scope.Contains(prefixServer.Id)))
                    .ToArray();
                if (ordered.Length == 0 && distance == -1)
                {
                    ordered = RoutePlanner.OrderPreferred(config, candidates, server.PreferredRoute)
                        .Where(IsCompatibleWithChosenJumpHost)
                        .ToArray();
                }

                route = server.PreferredRoute is { } preferred
                    ? ordered.FirstOrDefault(candidate => RoutePreferencePolicy.Matches(preferred, candidate))
                    : null;
                route ??= ordered.FirstOrDefault(candidate =>
                    candidate.Servers.Count > 1 &&
                    config.Links.Any(link =>
                        link.FromServerId == candidate.Servers[^2].Id &&
                        link.ToServerId == id &&
                        link.LastSuccessUtc is not null));
                route ??= ordered.FirstOrDefault(candidate => candidate.Servers.Count > 1);
                route ??= ordered.FirstOrDefault();
            }

            if (route is null) return;

            seeds.Add(new DiscoveredServerCandidate
            {
                Id = server.Id,
                Host = server.Host,
                Port = server.Port > 0 ? server.Port : 22,
                ProposedName = server.Name,
                Username = server.Username,
                Password = server.Password,
                RouteHops = route.Servers
                    .Select(item => new RouteHop(item.Id, item.Name, item.Host, item.Port))
                    .ToList(),
                ExistingServerId = server.Id,
                IsDuplicate = true,
                IsSelected = false,
                VerifiedProxyId = route.WithoutProxy || route.Proxy.Id == Guid.Empty ? null : route.Proxy.Id
            });
            routes[server.Id] = new SavedGatewayRoute(route, route.Servers.ToArray());
            if (!seedDistance.ContainsKey(server.Id))
                seedDistance[server.Id] = distance;
        }

        if (options.InitialGatewayServerId.HasValue)
        {
            var initId = options.InitialGatewayServerId.Value;
            var initServer = config.FindServer(initId);
            if (initServer is not null)
            {
                var initScope = options.TargetGroupId.HasValue
                    ? (config.FindGroup(options.TargetGroupId.Value) is { } g ? ServersInGroup(g).Select(s => s.Id).ToHashSet() : new HashSet<Guid>())
                    : config.UngroupedServers.Select(s => s.Id).ToHashSet();
                initScope.Add(initId);
                TryAddGatewaySeed(initId, initScope, -1);
            }
        }

        if (options.TargetGroupId.HasValue)
        {
            var ancestry = config.GroupAncestry(options.TargetGroupId.Value);
            if (ancestry.Count > 0)
            {
                // Iterate over EVERY level in ancestry from target group up to root
                for (var i = ancestry.Count - 1; i >= 0; i--)
                {
                    var group = ancestry[i];
                    var distance = ancestry.Count - 1 - i;
                    var levelScope = ServersInGroup(group).Select(server => server.Id).ToHashSet();
                    foreach (var id in FindIndependentServerIds(levelScope))
                    {
                        TryAddGatewaySeed(id, levelScope, distance);
                    }
                }
            }
        }
        else
        {
            var ungroupedScope = config.UngroupedServers.Select(server => server.Id).ToHashSet();
            foreach (var id in FindIndependentServerIds(ungroupedScope))
            {
                TryAddGatewaySeed(id, ungroupedScope, 0);
            }
        }

        foreach (var server in config.AllServers())
        {
            if (routes.ContainsKey(server.Id)) continue;
            var route = server.PreferredRoute is not null
                ? RoutePlanner.CandidateFromCached(config, server.PreferredRoute)
                : null;
            if (route is null && server.PreferredRoute is null)
            {
                var candidates = RoutePlanner.Candidates(config, server.Id);
                route = RoutePlanner.OrderPreferred(config, candidates, server.PreferredRoute).FirstOrDefault();
            }
            if (route is not null)
            {
                if (server.RequiredPreviousServerId.HasValue &&
                    !route.Servers.Any(s => s.Id == server.RequiredPreviousServerId.Value))
                {
                    continue;
                }
                routes[server.Id] = new SavedGatewayRoute(route, route.Servers.ToArray());
            }
        }

        return (
            seeds
                .OrderBy(seed => seedDistance.TryGetValue(seed.Id, out var d) ? d : int.MaxValue)
                .ThenBy(seed => seed.RouteHops.Count)
                .ThenBy(seed => seed.ProposedName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(seed => seed.Id)
                .ToList(),
            routes);
    }

    private sealed record SavedGatewayRoute(
        RouteCandidate Route, IReadOnlyList<ManagedServer> Servers);

    private sealed record GatewayChain(
        IReadOnlyList<ManagedServer> Servers, RouteCandidate? SavedRoute);

    private static GatewayChain? BuildGatewayChain(
        DiscoveredServerCandidate gateway,
        IReadOnlyList<DiscoveredServerCandidate> allDiscovered,
        IReadOnlyDictionary<Guid, SavedGatewayRoute> savedRouteByGatewayId)
    {
        var chain = new List<ManagedServer>();
        var current = gateway;
        var visited = new HashSet<Guid>();
        RouteCandidate? savedRoute = null;

        while (current != null && visited.Add(current.Id))
        {
            if (savedRouteByGatewayId.TryGetValue(current.Id, out var saved))
            {
                chain.InsertRange(0, saved.Servers);
                savedRoute = saved.Route;
                break;
            }

            if (current.IsDuplicate)
            {
                return null;
            }

            chain.Insert(0, CandidateToServer(current));
            var reqPrev = current.RequiredPreviousServerId;
            if (reqPrev.HasValue)
            {
                var previousId = reqPrev.Value;
                if (savedRouteByGatewayId.TryGetValue(previousId, out var savedDependency))
                {
                    chain.InsertRange(0, savedDependency.Servers);
                    savedRoute = savedDependency.Route;
                    break;
                }
                current = allDiscovered.FirstOrDefault(d => d.Id == previousId);
                if (current is null)
                {
                    return null;
                }
            }
            else
            {
                break;
            }
        }

        return new GatewayChain(chain, savedRoute);
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

    public static Guid? ResolveAutoSelectGatewayId(ManagerConfig config, Guid groupId)
    {
        var ancestry = config.GroupAncestry(groupId);
        if (ancestry.Count == 0) return null;

        // Pass 1: Walk ancestry from target group up to root searching for an independent server
        for (var i = ancestry.Count - 1; i >= 0; i--)
        {
            var group = ancestry[i];
            var levelServers = group.Servers;
            if (levelServers.Count == 0) continue;

            var levelScope = levelServers.Select(s => s.Id).ToHashSet();
            var directlyReachable = ConnectivityBatchPlanner.DirectlyReachable(config, levelScope);
            var firstIndependent = directlyReachable.FirstOrDefault();
            if (firstIndependent != Guid.Empty)
                return firstIndependent;

            foreach (var server in levelServers)
            {
                if (server.PreferredRoute is not null)
                {
                    var cached = RoutePlanner.CandidateFromCached(config, server.PreferredRoute);
                    if (cached is not null && cached.Servers.Take(cached.Servers.Count - 1).All(p => !levelScope.Contains(p.Id)))
                        return server.Id;
                }
            }

            var candidateWithIndependentRoute = levelServers.FirstOrDefault(s =>
                RoutePlanner.Candidates(config, s.Id).Any(c => c.Servers.Take(c.Servers.Count - 1).All(p => !levelScope.Contains(p.Id))));
            if (candidateWithIndependentRoute != null)
                return candidateWithIndependentRoute.Id;
        }

        // Pass 2: If no independent server found across all levels, fallback across ancestry to any server with a route
        for (var i = ancestry.Count - 1; i >= 0; i--)
        {
            var group = ancestry[i];
            var anyWithRoute = group.Servers.FirstOrDefault(s => RoutePlanner.Candidates(config, s.Id).Count > 0);
            if (anyWithRoute != null)
                return anyWithRoute.Id;
        }

        // Pass 3: Fallback to the first server in the target group
        var targetGroup = ancestry[^1];
        if (targetGroup.Servers.Count > 0)
            return targetGroup.Servers[0].Id;

        return null;
    }
}

namespace KiTTYManager.Core;

public static class AccessGrantPolicy
{
    public static bool IsScriptOperationRunning(BaseProxy proxy, bool operationActive) =>
        operationActive && proxy.LastAccessScriptResult == "Attempted";

    /// <summary>Scheduled restart interval (mechanism A): every 23h55m.</summary>
    public static readonly TimeSpan ScheduledRestartInterval = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(55);
    /// <summary>Mechanism A skips if mechanism B restarted the script within this window.</summary>
    public static readonly TimeSpan MechanismACooldown = TimeSpan.FromHours(1);
    /// <summary>Mechanism B retry after complete failure (no servers reachable).</summary>
    public static readonly TimeSpan FailureRetryCooldown = TimeSpan.FromHours(1);
    /// <summary>Legacy alias used by ShouldRunAccessScript for jumphost cold-start.</summary>
    public static readonly TimeSpan ConfirmedAccessLifetime = ScheduledRestartInterval;
    public static readonly TimeSpan ScriptRetryCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Cold-start: should the script run when the jumphost is being launched from scratch?
    /// </summary>
    public static bool ShouldRunAccessScript(BaseProxy proxy, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(proxy.PostLoginCommand) &&
        ((proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy)) is null ||
         now - (proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy)) >= ScheduledRestartInterval) &&
        (proxy.LastAccessScriptAttemptUtc is null ||
         now - proxy.LastAccessScriptAttemptUtc >= ScriptRetryCooldown);

    /// <summary>
    /// Mechanism A: scheduled restart while jumphost is already running.
    /// Fires every N minutes (configurable, default 23h55m), but skips if
    /// mechanism B restarted within the last hour.
    /// </summary>
    public static bool ShouldRunScheduledRestart(BaseProxy proxy, DateTimeOffset now) =>
        proxy.EnableScheduledRestart &&
        !string.IsNullOrWhiteSpace(proxy.PostLoginCommand) &&
        ((proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy)) is not null ||
         HasUnconfirmedAttempt(proxy)) &&
        now >= ScheduledRunDueUtc(proxy);

    public static bool ShouldInitializeAccessBaseline(BaseProxy proxy, DateTimeOffset now) =>
        proxy.EnableScheduledRestart &&
        !string.IsNullOrWhiteSpace(proxy.PostLoginCommand) &&
        (proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy)) is null &&
        (proxy.LastAccessScriptAttemptUtc is null ||
         now - proxy.LastAccessScriptAttemptUtc >= (HasUnconfirmedAttempt(proxy)
             ? MechanismACooldown
             : ScriptRetryCooldown));

    public static bool ShouldRunStartupPreflight(BaseProxy proxy) =>
        proxy.Enabled &&
        proxy.EnableScheduledRestart &&
        !string.IsNullOrWhiteSpace(proxy.PostLoginCommand);

    /// <summary>
    /// Mechanism B: should we check control servers and potentially restart
    /// the script after a connection failure? Fires even if A recently ran.
    /// Respects a 1-hour cooldown after a complete failure (no servers reachable).
    /// </summary>
    public static bool ShouldCheckControlsOnFailure(BaseProxy proxy, DateTimeOffset now) =>
        proxy.EnableControlServerMechanism &&
        !string.IsNullOrWhiteSpace(proxy.PostLoginCommand) &&
        proxy.AccessProbeServerIds.Count > 0 &&
        (proxy.LastAccessScriptAttemptUtc is null ||
         now - proxy.LastAccessScriptAttemptUtc >= FailureRetryCooldown);

    public static void MarkScriptAttempt(BaseProxy proxy, DateTimeOffset now) =>
        (proxy.LastAccessScriptAttemptUtc, proxy.LastAccessScriptResult) = (now, "Attempted");

    public static void MarkScriptUnconfirmed(BaseProxy proxy, DateTimeOffset now)
    {
        proxy.LastAccessScriptAttemptUtc = now;
        proxy.LastAccessScriptResult = "Unconfirmed";
    }

    public static void MarkScriptSuccess(BaseProxy proxy, DateTimeOffset now)
    {
        proxy.LastAccessScriptAttemptUtc = now;
        proxy.LastAccessScriptSuccessUtc = now;
        proxy.LastAccessConfirmedUtc = now;
        proxy.AccessScheduleBaselineUtc = now;
        proxy.LastAccessScriptResult = "Verified";
    }

    public static bool MarkAccessConfirmedWithoutScript(BaseProxy proxy, DateTimeOffset now)
    {
        if (proxy.LastAccessConfirmedUtc is not null ||
            proxy.LastAccessScriptSuccessUtc is not null) return false;
        proxy.LastAccessConfirmedUtc = now;
        proxy.AccessScheduleBaselineUtc = now;
        proxy.LastAccessScriptResult = "AccessStillValid";
        return true;
    }

    public static void RebaseAccessConfirmation(BaseProxy proxy, DateTimeOffset now)
    {
        proxy.LastAccessConfirmedUtc = now;
        proxy.AccessScheduleBaselineUtc = now;
        proxy.LastAccessScriptResult = "AccessStillValid";
    }

    public static void ResetLearnedControlsAndRebaseSchedule(
        BaseProxy proxy, DateTimeOffset now)
    {
        proxy.AccessProbeServerIds.Clear();
        proxy.AccessScheduleBaselineUtc = now;
    }

    public static void RememberReachableControls(
        ManagerConfig config, Guid proxyId, IEnumerable<Guid> serverIds, DateTimeOffset now)
    {
        var proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
        if (proxy is null || string.IsNullOrWhiteSpace(proxy.PostLoginCommand)) return;

        var known = config.AllServers().Select(server => server.Id).ToHashSet();
        var limit = Math.Clamp(proxy.AccessProbeServerLimit, 1, 20);
        var reachable = serverIds
            .Where(id => id != Guid.Empty && known.Contains(id) &&
                         IsEligibleControl(config.FindServer(id), proxy))
            .Distinct()
            .Take(limit)
            .ToList();
        if (reachable.Count == 0) return;
        proxy.AccessProbeServerLimit = limit;
        proxy.AccessProbeServerIds = reachable;
        proxy.LastAccessScriptSuccessUtc = now;
        proxy.LastAccessConfirmedUtc = now;
        proxy.AccessScheduleBaselineUtc = now;
        proxy.LastAccessScriptResult = "Verified";
    }

    public static void RememberControlCandidates(
        ManagerConfig config, Guid proxyId, IEnumerable<Guid> serverIds)
    {
        var proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
        if (proxy is null) return;
        var candidates = serverIds
            .Where(id => IsEligibleControl(config.FindServer(id), proxy))
            .Distinct()
            .Take(Math.Clamp(proxy.AccessProbeServerLimit, 1, 20))
            .ToList();
        if (candidates.Count > 0) proxy.AccessProbeServerIds = candidates;
    }

    public static DateTimeOffset? NextScheduledRunUtc(BaseProxy proxy)
    {
        if (!proxy.Enabled || !proxy.EnableScheduledRestart ||
            string.IsNullOrWhiteSpace(proxy.PostLoginCommand)) return null;
        return ScheduledRunDueUtc(proxy);
    }

    public static DateTimeOffset? NextEligibleScheduledActionUtc(
        BaseProxy proxy, DateTimeOffset? promptCancelledUtc)
    {
        var due = NextScheduledRunUtc(proxy);
        if (due is null || promptCancelledUtc is null) return due;
        var promptEligible = AccessScriptConsolePolicy.PromptEligibleUtc(
            promptCancelledUtc.Value, proxy.ScheduledRestartMinutes);
        return promptEligible > due ? promptEligible : due;
    }

    public static TimeSpan NextAlarmPollDelay(
        DateTimeOffset now, DateTimeOffset? nextActionUtc, TimeSpan maximumDelay)
    {
        var maximum = maximumDelay > TimeSpan.Zero ? maximumDelay : TimeSpan.FromSeconds(30);
        if (nextActionUtc is null) return maximum;
        var remaining = nextActionUtc.Value - now;
        if (remaining <= TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
        return remaining < maximum ? remaining : maximum;
    }

    private static DateTimeOffset ScheduledRunDueUtc(BaseProxy proxy)
    {
        var confirmed = proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy);
        var scheduled = confirmed is { } success
            ? success + TimeSpan.FromMinutes(Math.Max(1, proxy.ScheduledRestartMinutes))
            : DateTimeOffset.MinValue;
        if (!HasUnconfirmedAttempt(proxy)) return scheduled;
        var retry = proxy.LastAccessScriptAttemptUtc!.Value + MechanismACooldown;
        return retry > scheduled ? retry : scheduled;
    }

    private static bool HasUnconfirmedAttempt(BaseProxy proxy) =>
        proxy.LastAccessScriptAttemptUtc is not null &&
        (proxy.LastAccessScriptResult is "Attempted" or "Unconfirmed" ||
         (proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy)) is null ||
         proxy.LastAccessScriptAttemptUtc >
         (proxy.AccessScheduleBaselineUtc ?? LatestConfirmationUtc(proxy))) &&
        (proxy.AccessScheduleBaselineUtc is null ||
         proxy.LastAccessScriptAttemptUtc > proxy.AccessScheduleBaselineUtc);

    private static DateTimeOffset? LatestConfirmationUtc(BaseProxy proxy) =>
        proxy.LastAccessScriptSuccessUtc is { } script && proxy.LastAccessConfirmedUtc is { } confirmed
            ? (script > confirmed ? script : confirmed)
            : proxy.LastAccessScriptSuccessUtc ?? proxy.LastAccessConfirmedUtc;

    public static bool ShouldRunAfterControlPreflight(int learnedControlCount, bool anyReachable) =>
        learnedControlCount <= 0 || !anyReachable;

    public static void RememberSuccessfulRoute(
        ManagerConfig config, Guid proxyId, IEnumerable<Guid> routeServerIds, DateTimeOffset _)
    {
        var proxy = config.BaseProxies.FirstOrDefault(item => item.Id == proxyId);
        if (proxy is null || string.IsNullOrWhiteSpace(proxy.PostLoginCommand)) return;

        var controls = routeServerIds.Reverse()
            .Concat(proxy.AccessProbeServerIds)
            .Where(id => IsEligibleControl(config.FindServer(id), proxy))
            .Distinct()
            .Take(Math.Clamp(proxy.AccessProbeServerLimit, 1, 20))
            .ToList();
        if (controls.Count > 0) proxy.AccessProbeServerIds = controls;
    }

    public static bool IsEligibleControl(ManagedServer? server, BaseProxy proxy)
    {
        if (server is null || server.Id == proxy.StartupServerId) return false;
        var host = server.Host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        return !System.Net.IPAddress.TryParse(host, out var address) ||
               !System.Net.IPAddress.IsLoopback(address);
    }
}

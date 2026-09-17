namespace KiTTYManager.Core;

public enum JumphostStartupEligibility
{
    Eligible,
    MissingCredentials,
    InFailureCooldown,
    ServerBeingEdited
}

public enum JumphostLaunchStatus
{
    Success,
    SkippedNotEligible,
    AbortedDueToEditing,
    FailedWithCooldown
}

public sealed class JumphostLaunchExecutionResult
{
    public JumphostLaunchStatus Status { get; set; }
    public JumphostStartupEligibility Eligibility { get; set; }
    public Exception? Exception { get; set; }
}

public static class JumphostStartupPolicy
{
    public static JumphostStartupEligibility CheckEligibility(
        BaseProxy proxy,
        ManagedServer? server,
        bool isManualLaunch,
        Func<Guid, bool>? isServerBeingEdited,
        DateTimeOffset now)
    {
        if (isManualLaunch) return JumphostStartupEligibility.Eligible;
        if (!AccessGrantPolicy.HasConnectionCredentials(server))
            return JumphostStartupEligibility.MissingCredentials;
        if (AccessGrantPolicy.IsStartupInCooldown(proxy, now))
            return JumphostStartupEligibility.InFailureCooldown;
        if (server is not null && isServerBeingEdited?.Invoke(server.Id) == true)
            return JumphostStartupEligibility.ServerBeingEdited;
        return JumphostStartupEligibility.Eligible;
    }

    public static void RecordStartupFailure(BaseProxy proxy, DateTimeOffset now)
    {
        proxy.LastStartupFailureUtc = now;
    }

    public static void RecordStartupSuccess(BaseProxy proxy, DateTimeOffset now)
    {
        proxy.LastStartupFailureUtc = null;
        proxy.LastSuccessUtc = now;
    }

    public static void RecordManualLaunch(BaseProxy proxy)
    {
        proxy.LastStartupFailureUtc = null;
    }

    public static void HandleAdoptedConsoleTimeout(BaseProxy proxy, DateTimeOffset now)
    {
        RecordStartupFailure(proxy, now);
    }

    public static void HandleStopUnhealthyProcessFailure(BaseProxy proxy, DateTimeOffset now)
    {
        RecordStartupFailure(proxy, now);
    }

    public static async Task<JumphostLaunchExecutionResult> ExecuteLaunchSequenceAsync(
        BaseProxy proxy,
        ManagedServer? server,
        bool isManualLaunch,
        Func<Guid, bool>? isServerBeingEdited,
        Func<Task>? preLaunchDelayAsync,
        Func<Task<int>> selectPortAndVerifyAsync,
        Func<int, Task<bool>> launchProcessAndWaitReadyAsync,
        Func<DateTimeOffset> getNow)
    {
        var now = getNow();
        var eligibility = CheckEligibility(proxy, server, isManualLaunch, isServerBeingEdited, now);
        if (eligibility != JumphostStartupEligibility.Eligible)
        {
            return new JumphostLaunchExecutionResult
            {
                Status = JumphostLaunchStatus.SkippedNotEligible,
                Eligibility = eligibility
            };
        }

        if (isManualLaunch)
        {
            RecordManualLaunch(proxy);
        }

        try
        {
            var port = await selectPortAndVerifyAsync();

            if (preLaunchDelayAsync is not null)
            {
                await preLaunchDelayAsync();
            }

            // Immediately before launch: re-verify credentials and active editing
            if (!isManualLaunch)
            {
                if (!AccessGrantPolicy.HasConnectionCredentials(server))
                {
                    return new JumphostLaunchExecutionResult
                    {
                        Status = JumphostLaunchStatus.SkippedNotEligible,
                        Eligibility = JumphostStartupEligibility.MissingCredentials
                    };
                }
                if (server is not null && isServerBeingEdited?.Invoke(server.Id) == true)
                {
                    return new JumphostLaunchExecutionResult
                    {
                        Status = JumphostLaunchStatus.AbortedDueToEditing,
                        Eligibility = JumphostStartupEligibility.ServerBeingEdited
                    };
                }
            }

            var ready = await launchProcessAndWaitReadyAsync(port);
            if (ready)
            {
                RecordStartupSuccess(proxy, getNow());
                return new JumphostLaunchExecutionResult { Status = JumphostLaunchStatus.Success };
            }

            RecordStartupFailure(proxy, getNow());
            return new JumphostLaunchExecutionResult { Status = JumphostLaunchStatus.FailedWithCooldown };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordStartupFailure(proxy, getNow());
            return new JumphostLaunchExecutionResult
            {
                Status = JumphostLaunchStatus.FailedWithCooldown,
                Exception = ex
            };
        }
    }
}

using KiTTYManager.Core;
using KiTTYManager.App;

internal sealed partial class SelfTestRunner
{
    private static void RouteAttemptLimitDefaultsAndRoundTrip()
    {
        Equal(10, new ManagerConfig().MaxRouteAttempts);

        // NormalizeLimit checks
        Equal(10, RouteAttemptBudget.NormalizeLimit(0));
        Equal(10, RouteAttemptBudget.NormalizeLimit(-5));
        Equal(100, RouteAttemptBudget.NormalizeLimit(101));
        Equal(1, RouteAttemptBudget.NormalizeLimit(1));
        Equal(100, RouteAttemptBudget.NormalizeLimit(100));
        Equal(50, RouteAttemptBudget.NormalizeLimit(50));

        var path = TempFile();
        try
        {
            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 25 });
            Equal(25, ConfigStore.Load(path).MaxRouteAttempts);

            // Invalid / boundary values loaded through ConfigStore are normalized
            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 0 });
            Equal(10, ConfigStore.Load(path).MaxRouteAttempts);

            ConfigStore.Save(path, new ManagerConfig { MaxRouteAttempts = 150 });
            Equal(100, ConfigStore.Load(path).MaxRouteAttempts);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void RouteAttemptBudgetEnforcementAndException()
    {
        var budget = new RouteAttemptBudget(3);
        Equal(3, budget.Limit);
        Equal(3, budget.Remaining);
        Equal(0, budget.AttemptCount);
        Equal(true, budget.HasCapacity);

        Equal(true, budget.TryAcquire());
        Equal(2, budget.Remaining);
        Equal(1, budget.AttemptCount);

        Equal(true, budget.TryAcquire());
        Equal(1, budget.Remaining);
        Equal(2, budget.AttemptCount);

        Equal(true, budget.TryAcquire());
        Equal(0, budget.Remaining);
        Equal(3, budget.AttemptCount);
        Equal(false, budget.HasCapacity);

        Equal(false, budget.TryAcquire());
        Equal(0, budget.Remaining);
        Equal(3, budget.AttemptCount);

        var ex = new RouteAttemptLimitException(budget.Limit, budget.AttemptCount);
        Equal(3, ex.AttemptLimit);
        Equal(3, ex.AttemptCount);
        Equal(RouteAttemptLimitException.FormatMessage(3), ex.Message);
        var expectedMessage = "Перебрали 3 вариантов маршрутов, ни один не сработал. Если нужно увеличить количество возможных вариантов, это можно сделать в настройках (пункт меню «Настройки» → блок «Подключение и сеть»).";
        Equal(expectedMessage, ex.Message);
    }

    private static void RouteAttemptLimitNonRetryableInBatchTasks()
    {
        var ex = new RouteAttemptLimitException(10, 10);
        // IsConnectivityFailure must return false for RouteAttemptLimitException
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(ex));
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(new AggregateException(ex)));
        Equal(false, TaskConnectionRecoveryPolicy.IsConnectivityFailure(new Exception("outer", ex)));
    }

    private static void ConnectFirstSuccessfulSharesBudgetWhenOmitted()
    {
        var config = new ManagerConfig { MaxRouteAttempts = 1 };
        var proxy = new BaseProxy { Name = "test-proxy", Host = "127.0.0.1", Port = 1, Enabled = true };
        var s1 = new ManagedServer { Name = "s1", Host = "127.0.0.1", Port = 22 };
        var s2 = new ManagedServer { Name = "s2", Host = "127.0.0.1", Port = 22 };
        var candidate1 = new RouteCandidate(proxy, [s1]);
        var candidate2 = new RouteCandidate(proxy, [s2]);

        var events = new List<SshTraceEvent>();
        var service = new SshConnectionService
        {
            Timeout = TimeSpan.FromMilliseconds(300),
            EndpointProbeTimeout = TimeSpan.FromMilliseconds(100),
            Trace = events.Add
        };

        try
        {
            service.ConnectFirstSuccessfulAsync(config, [candidate1, candidate2])
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Ожидался сбой подключения");
        }
        catch (Exception ex)
        {
            // One candidate takes the single budget attempt, the other exhausts the budget
            var starts = events.Count(e => e.Stage == SshTraceStage.RouteCandidate && e.Status == "START");
            Equal(1, starts);
            Equal(true, ex is RouteAttemptLimitException or InvalidOperationException);
        }
    }
}

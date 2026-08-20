using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ProxySettingsDialog : Window
{
    private readonly DispatcherTimer totpTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly IReadOnlyDictionary<Guid, string> serverNames;
    private readonly IReadOnlyDictionary<Guid, DateTimeOffset> promptCancellations;
    private readonly Func<Guid, BaseProxy?> liveProxy;
    private readonly Func<Guid, bool> scriptRunning;
    private readonly Func<Guid, string> consoleStatus;
    private readonly Func<Guid, int> closeExtraConsoles;
    private readonly HashSet<Guid> resetScheduleProxyIds = [];
    public IReadOnlySet<Guid> ResetScheduleProxyIds => resetScheduleProxyIds;
    public List<BaseProxy> Proxies { get; }
    public IReadOnlyList<SessionChoice> SessionChoices { get; }
    public ProxySettingsDialog(
        IEnumerable<BaseProxy> proxies, IEnumerable<ManagedServer> servers,
        IReadOnlyDictionary<Guid, DateTimeOffset>? promptCancellations = null,
        Func<Guid, BaseProxy?>? liveProxy = null,
        Func<Guid, bool>? scriptRunning = null,
        Func<Guid, string>? consoleStatus = null,
        Func<Guid, int>? closeExtraConsoles = null)
    {
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent();
        Width = Math.Min(1400, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        Height = Math.Min(980, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        var serverList = servers.OrderBy(s => s.Name).ToArray();
        serverNames = serverList.ToDictionary(server => server.Id, server => server.Name);
        this.promptCancellations = promptCancellations ??
            new Dictionary<Guid, DateTimeOffset>();
        this.liveProxy = liveProxy ?? (_ => null);
        this.scriptRunning = scriptRunning ?? (_ => false);
        this.consoleStatus = consoleStatus ?? (_ => "—");
        this.closeExtraConsoles = closeExtraConsoles ?? (_ => 0);
        SessionChoices = [new SessionChoice(null, "Не запускать автоматически"),
            .. serverList.Select(s => new SessionChoice(s.Id, $"{s.Name} ({s.Endpoint})"))];
        Proxies = proxies.Select(Clone).ToList();
        Grid.ItemsSource = Proxies;
        Algorithm.ItemsSource = new[] { "SHA1", "SHA256", "SHA512" };
        Grid.SelectedIndex = Proxies.Count == 0 ? -1 : 0;
        totpTimer.Tick += (_, _) => RefreshTotp();
        totpTimer.Start();
        Closed += (_, _) => totpTimer.Stop();
        RefreshTotp();
        RefreshAccessProbeSummary();
        if (Grid.SelectedItem is BaseProxy selected) RefreshConsoleStatus(selected);
    }
    private void Add_Click(object sender, RoutedEventArgs e) { Proxies.Add(new BaseProxy { Name = "Jumphost", Host = "127.0.0.1", Port = 5555 }); Refresh(); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is BaseProxy proxy) { Proxies.Remove(proxy); Refresh(); } }
    private void Refresh() { Grid.ItemsSource = null; Grid.ItemsSource = Proxies; }
    private void RefreshTotp()
    {
        foreach (var cell in VisualDescendants<TextBlock>(Grid)
                     .Where(text => Equals(text.Tag, "CurrentTotp")))
            RefreshTotpCell(cell);
        if (Grid.SelectedItem is BaseProxy proxy) RefreshAccessScriptStatus(proxy);
    }

    private void TotpCell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock cell) RefreshTotpCell(cell);
    }

    private static void RefreshTotpCell(TextBlock cell)
    {
        if (cell.DataContext is BaseProxy proxy) cell.Text = TotpDisplay(proxy);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static string TotpDisplay(BaseProxy proxy)
    {
        if (proxy.TotpSecret.Length == 0) return "—";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var code = TotpGenerator.Generate(proxy.TotpSecret, now,
                proxy.TotpDigits, proxy.TotpPeriodSeconds, proxy.TotpAlgorithm);
            var period = Math.Max(1, proxy.TotpPeriodSeconds);
            var remaining = period - now.ToUnixTimeSeconds() % period;
            return $"{code}  ({remaining} с)";
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return "неверный secret";
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAccessProbeSummary();
        if (Grid.SelectedItem is BaseProxy proxy) RefreshConsoleStatus(proxy);
    }

    private void RefreshConsoleStatus(BaseProxy proxy)
    {
        if (ConsoleStatus is not null) ConsoleStatus.Text = consoleStatus(proxy.Id);
    }

    private void CloseExtraConsoles_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not BaseProxy proxy) return;
        var closed = closeExtraConsoles(proxy.Id);
        ThemedMessageDialog.Show(this,
            closed > 0 ? $"Закрыто лишних консолей: {closed}." : "Лишних консолей не найдено.",
            "Консоли", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshConsoleStatus(proxy);
    }
    private void AccessProbeLimit_TextChanged(object sender, TextChangedEventArgs e) => RefreshAccessProbeSummary();
    private void ResetAccessProbe_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not BaseProxy proxy) return;
        AccessGrantPolicy.ResetLearnedControlsAndRebaseSchedule(proxy, DateTimeOffset.UtcNow);
        resetScheduleProxyIds.Add(proxy.Id);
        RefreshAccessProbeSummary();
    }

    private void RefreshAccessProbeSummary()
    {
        if (AccessProbeSummary is null) return;
        if (Grid.SelectedItem is not BaseProxy proxy)
        {
            AccessProbeSummary.Text = "—";
            return;
        }

        var names = proxy.AccessProbeServerIds
            .Select(id => serverNames.TryGetValue(id, out var name) ? name : id.ToString())
            .ToArray();
        AccessProbeSummary.Text = names.Length == 0
            ? $"0/{proxy.AccessProbeServerLimit}"
            : $"{names.Length}/{proxy.AccessProbeServerLimit}: {string.Join(", ", names)}";
        RefreshAccessScriptStatus(proxy);
    }

    private void RefreshAccessScriptStatus(BaseProxy proxy)
    {
        if (AccessScriptStatus is null) return;
        var runtime = liveProxy(proxy.Id) ?? proxy;
        var operationActive = AccessGrantPolicy.IsScriptOperationRunning(
            runtime, scriptRunning(proxy.Id));
        static string Time(DateTimeOffset? value) => value is null
            ? "—"
            : value.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        var result = operationActive ? "скрипт выполняется" : runtime.LastAccessScriptResult switch
        {
            "Verified" => "доступ подтверждён",
            "Attempted" => "команда отправлена",
            "Unconfirmed" => "запуск не подтверждён",
            "AccessStillValid" => "доступ уже действовал, скрипт не запускался",
            _ => "—"
        };
        var promptCancelled = promptCancellations.GetValueOrDefault(proxy.Id);
        var schedule = Clone(runtime);
        schedule.Enabled = proxy.Enabled;
        schedule.EnableScheduledRestart = proxy.EnableScheduledRestart;
        schedule.ScheduledRestartMinutes = proxy.ScheduledRestartMinutes;
        schedule.PostLoginCommand = proxy.PostLoginCommand;
        if (resetScheduleProxyIds.Contains(proxy.Id))
            schedule.AccessScheduleBaselineUtc = proxy.AccessScheduleBaselineUtc;
        var scheduled = AccessGrantPolicy.NextScheduledRunUtc(schedule);
        var next = AccessGrantPolicy.NextEligibleScheduledActionUtc(
            schedule, promptCancelled == default ? null : promptCancelled);
        var delayedByCancellation = next is not null && scheduled is not null && next > scheduled;
        var hasBaseline = (schedule.AccessScheduleBaselineUtc ??
                           schedule.LastAccessScriptSuccessUtc ?? schedule.LastAccessConfirmedUtc) is not null;
        var nextText = operationActive
            ? "после завершения скрипта"
            : !hasBaseline
            ? "после первоначальной проверки доступа"
            : next is null
            ? "не запланирован"
            : Time(next) + (next <= DateTimeOffset.UtcNow
                ? " (просрочено)"
                : delayedByCancellation ? " (после отмены)" : "");
        AccessScriptStatus.Text =
            $"Последняя попытка: {Time(runtime.LastAccessScriptAttemptUtc)}\n" +
            $"Последний успешный запуск скрипта: {Time(runtime.LastAccessScriptSuccessUtc)}\n" +
            $"Последнее подтверждение доступа: {Time(runtime.LastAccessConfirmedUtc ?? runtime.LastAccessScriptSuccessUtc)}\n" +
            $"Результат: {result}\n" +
            $"Следующий запуск: {nextText}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proxy in Proxies.Where(p => string.IsNullOrWhiteSpace(p.PostLoginPasswordPrompt)))
            proxy.PostLoginPasswordPrompt = "assword";
        if (Proxies.Any(p => p.UseAutomaticPort && p.StartupServerId is null))
        { ThemedMessageDialog.Show(this, "Автоматический порт можно выбрать только для точки входа с назначенной сессией jumphost.", "Проверьте точку входа", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Proxies.Any(p => string.IsNullOrWhiteSpace(p.Host) || (!p.UseAutomaticPort && p.Port is < 1 or > 65535)))
        { ThemedMessageDialog.Show(this, "Для каждого proxy укажите адрес и порт от 1 до 65535.", "Проверьте настройки", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Proxies.Any(p => p.TotpSecret.Length > 0 &&
            (p.TotpDigits is < 6 or > 8 || p.TotpPeriodSeconds < 1 ||
             p.TotpAlgorithm is not ("SHA1" or "SHA256" or "SHA512"))))
        { ThemedMessageDialog.Show(this, "Для TOTP выберите SHA1/SHA256/SHA512, 6–8 цифр и положительный интервал.", "Проверьте TOTP", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Proxies.Any(p => p.PostCommandReadyDelaySeconds is < 1 or > 600))
        { ThemedMessageDialog.Show(this, "Таймаут скрипта должен быть от 1 до 600 секунд.", "Проверьте таймаут", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Proxies.Any(p => p.AccessProbeServerLimit is < 1 or > 20))
        { ThemedMessageDialog.Show(this, "Количество контрольных серверов должно быть от 1 до 20.", "Проверьте контрольную выборку", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Proxies.Any(p => p.ScheduledRestartMinutes < 1))
        { ThemedMessageDialog.Show(this, "Интервал перезапуска должен быть не меньше 1 минуты.", "Проверьте расписание", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            foreach (var proxy in Proxies.Where(p => p.TotpSecret.Length > 0)) TotpGenerator.DecodeSecret(proxy.TotpSecret);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        { ThemedMessageDialog.Show(this, error.Message, "Проверьте TOTP secret", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        foreach (var proxy in Proxies)
        {
            var runtime = liveProxy(proxy.Id);
            if (runtime is null) continue;
            proxy.LastAccessScriptAttemptUtc = runtime.LastAccessScriptAttemptUtc;
            proxy.LastAccessScriptSuccessUtc = runtime.LastAccessScriptSuccessUtc;
            proxy.LastAccessConfirmedUtc = runtime.LastAccessConfirmedUtc;
            proxy.LastAccessScriptResult = runtime.LastAccessScriptResult;
            if (resetScheduleProxyIds.Contains(proxy.Id)) continue;
            proxy.AccessScheduleBaselineUtc = runtime.AccessScheduleBaselineUtc;
            proxy.AccessProbeServerIds = [.. runtime.AccessProbeServerIds];
        }
        foreach (var proxy in Proxies)
            proxy.AccessProbeServerIds = proxy.AccessProbeServerIds
                .Where(serverNames.ContainsKey)
                .Distinct()
                .Take(proxy.AccessProbeServerLimit)
                .ToList();
        DialogResult = true;
    }

    private static BaseProxy Clone(BaseProxy p) => new()
    {
        Id = p.Id, Name = p.Name, Host = p.Host, Port = p.Port, Enabled = p.Enabled,
        UseAutomaticPort = p.UseAutomaticPort,
        StartupServerId = p.StartupServerId, AutoStartWhenUnavailable = p.AutoStartWhenUnavailable,
        TotpSecret = p.TotpSecret, TotpAlgorithm = p.TotpAlgorithm, TotpDigits = p.TotpDigits,
        TotpPeriodSeconds = p.TotpPeriodSeconds, TotpPrompt = p.TotpPrompt,
        PostLoginCommand = p.PostLoginCommand,
        RepeatAccountPasswordAfterCommand = p.RepeatAccountPasswordAfterCommand,
        PostLoginPasswordPrompt = p.PostLoginPasswordPrompt,
        PostCommandReadyDelaySeconds = p.PostCommandReadyDelaySeconds,
        EnableScheduledRestart = p.EnableScheduledRestart,
        ScheduledRestartMinutes = p.ScheduledRestartMinutes,
        EnableControlServerMechanism = p.EnableControlServerMechanism,
        AccessProbeServerLimit = p.AccessProbeServerLimit,
        AccessProbeServerIds = [.. p.AccessProbeServerIds],
        LastAccessScriptAttemptUtc = p.LastAccessScriptAttemptUtc,
        LastAccessScriptSuccessUtc = p.LastAccessScriptSuccessUtc,
        LastAccessConfirmedUtc = p.LastAccessConfirmedUtc,
        AccessScheduleBaselineUtc = p.AccessScheduleBaselineUtc,
        LastAccessScriptResult = p.LastAccessScriptResult
    };

    public sealed record SessionChoice(Guid? Id, string Name)
    {
        public override string ToString() => Name;
    }
}

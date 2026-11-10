using System.Text.Json.Serialization;

namespace KiTTYManager.Core;

public sealed class ManagerConfig
{
    public int SchemaVersion { get; set; } = 9;
    public List<ServerGroup> Groups { get; set; } = [];
    public List<ManagedServer> UngroupedServers { get; set; } = [];
    public List<ServerLink> Links { get; set; } = [];
    public List<BaseProxy> BaseProxies { get; set; } = [];
    public Guid? PreferredProxyId { get; set; }
    public string KittyPath { get; set; } = "KiTTY\\kitty.exe";
    public string FirefoxPath { get; set; } = "firefox.exe";
    public string WinScpPath { get; set; } = "";
    public bool ClosePreferenceConfigured { get; set; }
    public bool CloseToTray { get; set; }
    public bool EnableLogging { get; set; }
    public bool WriteChangesImmediatelyToKitty { get; set; }
    public bool MaximizeKittyWindows { get; set; } = true;
    public bool CloseWebTunnelWithFirefox { get; set; } = true;
    public bool AutoDiscoverFirefoxProfile { get; set; } = true;
    public string FirefoxTemplateProfile { get; set; } = "";
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int EndpointProbeTimeoutSeconds { get; set; } = 4;
    public int MaxRouteAttempts { get; set; } = 20;
    public int MaxGroupServersInRouteAttempts { get; set; } = 2;
    public int TaskConnectionRecoveryMinutes { get; set; } = 1;
    public bool AutoConfirmHostKeys { get; set; } = true;
    public bool SuppressKittyChangeNotifications { get; set; } = true;
    public bool RaceBestEntryPoints { get; set; } = true;
    public bool SkipExistingLinksInMapCheck { get; set; } = true;
    public bool OfferStartMissingJumphosts { get; set; } = true;
    public bool FirefoxOptimizeRamCache { get; set; } = true;
    public bool FirefoxDisableSafeBrowsing { get; set; } = true;
    public bool FirefoxDisableHistoryAndIcons { get; set; } = true;
    public bool FirefoxClearCacheOnShutdown { get; set; } = true;
    public bool FirefoxCleanRemovedServerContainers { get; set; } = true;
    public bool FirefoxAcceptInsecureCerts { get; set; } = true;
    public List<FirefoxContainerCleanupTask> PendingFirefoxContainerCleanups { get; set; } = [];
    public List<string> NotifiedUpdateVersions { get; set; } = [];

    public bool HasNotifiedUpdateVersion(string version) =>
        NotifiedUpdateVersions.Any(v => string.Equals(v.TrimStart('v', 'V'), version.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase));

    public void RecordNotifiedUpdateVersion(string version)
    {
        if (!HasNotifiedUpdateVersion(version))
            NotifiedUpdateVersions.Add(version);
    }

    public ManagerConfig CloneForSave() => new()
    {
        SchemaVersion = SchemaVersion,
        Groups = Groups.Select(g => g.Clone()).ToList(),
        UngroupedServers = UngroupedServers.Select(s => s.Clone()).ToList(),
        Links = Links.Select(l => l.Clone()).ToList(),
        BaseProxies = BaseProxies.Select(p => p.Clone()).ToList(),
        PreferredProxyId = PreferredProxyId,
        KittyPath = KittyPath,
        FirefoxPath = FirefoxPath,
        WinScpPath = WinScpPath,
        ClosePreferenceConfigured = ClosePreferenceConfigured,
        CloseToTray = CloseToTray,
        EnableLogging = EnableLogging,
        WriteChangesImmediatelyToKitty = WriteChangesImmediatelyToKitty,
        MaximizeKittyWindows = MaximizeKittyWindows,
        CloseWebTunnelWithFirefox = CloseWebTunnelWithFirefox,
        AutoDiscoverFirefoxProfile = AutoDiscoverFirefoxProfile,
        FirefoxTemplateProfile = FirefoxTemplateProfile,
        ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
        EndpointProbeTimeoutSeconds = EndpointProbeTimeoutSeconds,
        MaxRouteAttempts = MaxRouteAttempts,
        MaxGroupServersInRouteAttempts = MaxGroupServersInRouteAttempts,
        TaskConnectionRecoveryMinutes = TaskConnectionRecoveryMinutes,
        AutoConfirmHostKeys = AutoConfirmHostKeys,
        SuppressKittyChangeNotifications = SuppressKittyChangeNotifications,
        RaceBestEntryPoints = RaceBestEntryPoints,
        SkipExistingLinksInMapCheck = SkipExistingLinksInMapCheck,
        OfferStartMissingJumphosts = OfferStartMissingJumphosts,
        FirefoxOptimizeRamCache = FirefoxOptimizeRamCache,
        FirefoxDisableSafeBrowsing = FirefoxDisableSafeBrowsing,
        FirefoxDisableHistoryAndIcons = FirefoxDisableHistoryAndIcons,
        FirefoxClearCacheOnShutdown = FirefoxClearCacheOnShutdown,
        FirefoxCleanRemovedServerContainers = FirefoxCleanRemovedServerContainers,
        FirefoxAcceptInsecureCerts = FirefoxAcceptInsecureCerts,
        PendingFirefoxContainerCleanups = PendingFirefoxContainerCleanups.Select(t => t.Clone()).ToList(),
        NotifiedUpdateVersions = [..NotifiedUpdateVersions]
    };
}

public sealed class FirefoxContainerCleanupTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid ServerId { get; set; }
    public Guid? WebId { get; set; }

    public FirefoxContainerCleanupTask Clone() => new()
    {
        Id = Id,
        ServerId = ServerId,
        WebId = WebId
    };
}

public sealed class ServerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новая группа";
    public List<ServerGroup> Groups { get; set; } = [];
    public List<ManagedServer> Servers { get; set; } = [];
    public override string ToString() => Name;

    public ServerGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Servers = Servers.Select(s => s.Clone()).ToList()
    };
}

public sealed class ManagedServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новый сервер";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    [JsonIgnore] public ImportedCredentialState PasswordImportState { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKeyPassphrase { get; set; } = "";
    public bool UseKeyboardInteractive { get; set; } = true;
    public string RootLogin { get; set; } = "";
    public string RootPassword { get; set; } = "";
    public string ShellPrompt { get; set; } = "$";
    public string ImportedCommand { get; set; } = "";
    public bool IgnoreImportedCommand { get; set; }
    public bool UsePam { get; set; }
    public string PamSshCommand { get; set; } = "";
    public string PamPassword { get; set; } = "";
    public string PamTotpSecret { get; set; } = "";
    public string PamTotpAlgorithm { get; set; } = "SHA1";
    public int PamTotpDigits { get; set; } = 6;
    public int PamTotpPeriodSeconds { get; set; } = 30;
    public string PamTotpPrompt { get; set; } = "OTP: ";
    public bool PamAutoConfirm { get; set; }
    public string PamConfirmPrompt { get; set; } = "[Y/n]";
    [JsonIgnore] public bool HasExplicitUsePam { get; set; } = true;
    [JsonIgnore] public bool HasExplicitPamAutoConfirm { get; set; } = true;
    [JsonIgnore] public bool HasAllPamPropertiesPresentInImport { get; set; } = true;
    private int _keepaliveIntervalSeconds = 15;
    private bool _enableTcpKeepalives = true;
    private bool _reconnectOnConnectionFailure = true;
    private bool _reconnectOnSystemWakeup = true;

    [JsonIgnore] internal bool HasPersistedKeepaliveIntervalSeconds { get; set; }
    [JsonIgnore] internal bool HasPersistedEnableTcpKeepalives { get; set; }
    [JsonIgnore] internal bool HasPersistedReconnectOnConnectionFailure { get; set; }
    [JsonIgnore] internal bool HasPersistedReconnectOnSystemWakeup { get; set; }

    [JsonIgnore]
    internal bool HasAllPersistedKeepaliveSettings =>
        HasPersistedKeepaliveIntervalSeconds &&
        HasPersistedEnableTcpKeepalives &&
        HasPersistedReconnectOnConnectionFailure &&
        HasPersistedReconnectOnSystemWakeup;

    public int KeepaliveIntervalSeconds
    {
        get => _keepaliveIntervalSeconds;
        set
        {
            _keepaliveIntervalSeconds = value;
            HasPersistedKeepaliveIntervalSeconds = true;
        }
    }

    public bool EnableTcpKeepalives
    {
        get => _enableTcpKeepalives;
        set
        {
            _enableTcpKeepalives = value;
            HasPersistedEnableTcpKeepalives = true;
        }
    }

    public bool ReconnectOnConnectionFailure
    {
        get => _reconnectOnConnectionFailure;
        set
        {
            _reconnectOnConnectionFailure = value;
            HasPersistedReconnectOnConnectionFailure = true;
        }
    }

    public bool ReconnectOnSystemWakeup
    {
        get => _reconnectOnSystemWakeup;
        set
        {
            _reconnectOnSystemWakeup = value;
            HasPersistedReconnectOnSystemWakeup = true;
        }
    }
    public KittySessionSnapshot? KittyBaseline { get; set; }
    public string HostKeyFingerprint { get; set; } = "";
    public string HostKeyAlgorithm { get; set; } = "";
    public int HostKeyBits { get; set; }
    public string? SourceSessionPath { get; set; }
    public string? SourceScriptPath { get; set; }
    public string SourceScriptContent { get; set; } = "";
    public ImportedProxy? ImportedProxy { get; set; }
    public List<string> ManagerOverrides { get; set; } = [];
    // Kept only so schema 5 configurations can be loaded and migrated safely.
    public List<string> IgnoredKittyProperties { get; set; } = [];
    public List<IgnoredKittyChange> IgnoredKittyChanges { get; set; } = [];
    public CachedRoute? PreferredRoute { get; set; }
    /// <summary>
    /// Если задано, перед этой сессией в любом маршруте обязательно должна идти
    /// указанная сессия. Это разделяет одинаковые private IP в разных сетях.
    /// </summary>
    public Guid? RequiredPreviousServerId { get; set; }
    /// <summary>
    /// Сначала пробовать подключение к этой сессии непосредственно с компьютера,
    /// без SOCKS/JH. Обычные маршруты остаются резервными.
    /// </summary>
    public bool TryDirectWithoutJumphost { get; set; }
    /// <summary>Последняя успешно использованная JH именно для этой сессии.</summary>
    public Guid? PreferredProxyId { get; set; }
    /// <summary>
    /// Дополнительные адреса, по которым этот сервер доступен, если основной
    /// Host:Port не подошёл (например, другой порт SSH внутри подсети проекта).
    /// Пробуются при любом подключении к серверу — и напрямую, и через цепочку.
    /// Пустой Host означает «тот же хост, что и основной».
    /// </summary>
    public List<ServerEndpoint> BackupEndpoints { get; set; } = [];
    /// <summary>
    /// Устаревшая общая память адреса для совместимости со старыми конфигурациями.
    /// После первого контекстного успеха порядок задаёт EndpointPreferences.
    /// </summary>
    public ServerEndpoint? PreferredEndpoint { get; set; }
    /// <summary>
    /// Последний успешный адрес отдельно для каждой пары JH→сервер или
    /// предыдущий сервер→следующий сервер.
    /// </summary>
    public List<EndpointPreference> EndpointPreferences { get; set; } = [];
    public DateTimeOffset? LastOriginalSessionFallbackUtc { get; set; }
    public List<WebInterface> WebInterfaces { get; set; } = [];
    [JsonIgnore] public string Endpoint => $"{Host}:{Port}";
    /// <summary>Host without a leading "user@" prefix (handles legacy import format).</summary>
    [JsonIgnore] public string CleanHost
    {
        get
        {
            var h = Host.Trim();
            var at = h.LastIndexOf('@');
            return at >= 0 && at < h.Length - 1 ? h[(at + 1)..] : h;
        }
    }
    /// <summary>Username: explicit field, or extracted from "user@host" if Username is empty.</summary>
    [JsonIgnore] public string EffectiveUsername
    {
        get
        {
            if (Username.Trim().Length > 0) return Username;
            var h = Host.Trim();
            var at = h.LastIndexOf('@');
            return at > 0 ? h[..at] : "";
        }
    }
    public override string ToString() => Name;

    public ManagedServer Clone()
    {
        var copy = new ManagedServer
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PasswordImportState = PasswordImportState,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            UseKeyboardInteractive = UseKeyboardInteractive,
            RootLogin = RootLogin,
            RootPassword = RootPassword,
            ShellPrompt = ShellPrompt,
            ImportedCommand = ImportedCommand,
            IgnoreImportedCommand = IgnoreImportedCommand,
            UsePam = UsePam,
            PamSshCommand = PamSshCommand,
            PamPassword = PamPassword,
            PamTotpSecret = PamTotpSecret,
            PamTotpAlgorithm = PamTotpAlgorithm,
            PamTotpDigits = PamTotpDigits,
            PamTotpPeriodSeconds = PamTotpPeriodSeconds,
            PamTotpPrompt = PamTotpPrompt,
            PamAutoConfirm = PamAutoConfirm,
            PamConfirmPrompt = PamConfirmPrompt,
            HasExplicitUsePam = HasExplicitUsePam,
            HasExplicitPamAutoConfirm = HasExplicitPamAutoConfirm,
            HasAllPamPropertiesPresentInImport = HasAllPamPropertiesPresentInImport,
            KeepaliveIntervalSeconds = KeepaliveIntervalSeconds,
            EnableTcpKeepalives = EnableTcpKeepalives,
            ReconnectOnConnectionFailure = ReconnectOnConnectionFailure,
            ReconnectOnSystemWakeup = ReconnectOnSystemWakeup,
            HasPersistedKeepaliveIntervalSeconds = HasPersistedKeepaliveIntervalSeconds,
            HasPersistedEnableTcpKeepalives = HasPersistedEnableTcpKeepalives,
            HasPersistedReconnectOnConnectionFailure = HasPersistedReconnectOnConnectionFailure,
            HasPersistedReconnectOnSystemWakeup = HasPersistedReconnectOnSystemWakeup,
            KittyBaseline = KittyBaseline?.Clone(),
            HostKeyFingerprint = HostKeyFingerprint,
            HostKeyAlgorithm = HostKeyAlgorithm,
            HostKeyBits = HostKeyBits,
            SourceSessionPath = SourceSessionPath,
            SourceScriptPath = SourceScriptPath,
            SourceScriptContent = SourceScriptContent,
            ImportedProxy = ImportedProxy is null ? null : new ImportedProxy
            {
                Host = ImportedProxy.Host,
                Port = ImportedProxy.Port,
                Method = ImportedProxy.Method
            },
            ManagerOverrides = [..ManagerOverrides],
            IgnoredKittyProperties = [..IgnoredKittyProperties],
            IgnoredKittyChanges = IgnoredKittyChanges.Select(c => c.Clone()).ToList(),
            PreferredRoute = PreferredRoute?.Clone(),
            RequiredPreviousServerId = RequiredPreviousServerId,
            TryDirectWithoutJumphost = TryDirectWithoutJumphost,
            PreferredProxyId = PreferredProxyId,
            BackupEndpoints = BackupEndpoints.Select(e => e.Clone()).ToList(),
            PreferredEndpoint = PreferredEndpoint?.Clone(),
            EndpointPreferences = EndpointPreferences.Select(ep => ep.Clone()).ToList(),
            LastOriginalSessionFallbackUtc = LastOriginalSessionFallbackUtc,
            WebInterfaces = WebInterfaces.Select(w => w.Clone()).ToList()
        };
        return copy;
    }
}

public sealed class IgnoredKittyChange
{
    public string PropertyName { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    public IgnoredKittyChange Clone() => new()
    {
        PropertyName = PropertyName,
        Fingerprint = Fingerprint
    };
}

public enum ImportedCredentialState { Unknown, Empty, Decoded, PresentButUndecodable }

public sealed class KittySessionSnapshot
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public bool UseKeyboardInteractive { get; set; } = true;
    public string RootLogin { get; set; } = "";
    public string RootPassword { get; set; } = "";
    public string ImportedCommand { get; set; } = "";
    public int KeepaliveIntervalSeconds { get; set; } = -1;
    public bool EnableTcpKeepalives { get; set; } = true;
    public bool ReconnectOnConnectionFailure { get; set; } = true;
    public bool ReconnectOnSystemWakeup { get; set; } = true;

    public KittySessionSnapshot Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        Username = Username,
        Password = Password,
        PrivateKeyPath = PrivateKeyPath,
        UseKeyboardInteractive = UseKeyboardInteractive,
        RootLogin = RootLogin,
        RootPassword = RootPassword,
        ImportedCommand = ImportedCommand,
        KeepaliveIntervalSeconds = KeepaliveIntervalSeconds,
        EnableTcpKeepalives = EnableTcpKeepalives,
        ReconnectOnConnectionFailure = ReconnectOnConnectionFailure,
        ReconnectOnSystemWakeup = ReconnectOnSystemWakeup
    };
}

public sealed class CachedRoute
{
    public Guid ProxyId { get; set; }
    public List<Guid> ServerIds { get; set; } = [];
    public double? LatencyMs { get; set; }
    public DateTimeOffset LastSuccessUtc { get; set; }

    public CachedRoute Clone() => new()
    {
        ProxyId = ProxyId,
        ServerIds = [..ServerIds],
        LatencyMs = LatencyMs,
        LastSuccessUtc = LastSuccessUtc
    };
}

public sealed class WebInterface
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Веб-интерфейс";
    public string Url { get; set; } = "http://";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ResolverAddress { get; set; } = "";
    public override string ToString() => Name;

    public WebInterface Clone() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Username = Username,
        Password = Password,
        ResolverAddress = ResolverAddress
    };
}

public sealed class BaseProxy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Jumphost";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public bool UseAutomaticPort { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? StartupServerId { get; set; }
    public bool AutoStartWhenUnavailable { get; set; }
    public string TotpSecret { get; set; } = "";
    public string TotpAlgorithm { get; set; } = "SHA1";
    public int TotpDigits { get; set; } = 6;
    public int TotpPeriodSeconds { get; set; } = 30;
    public string TotpPrompt { get; set; } = "TOTP:";
    public string PostLoginCommand { get; set; } = "";
    public bool RepeatAccountPasswordAfterCommand { get; set; }
    public string PostLoginPasswordPrompt { get; set; } = "assword";
    public int PostCommandReadyDelaySeconds { get; set; } = 180;
    public bool EnableScheduledRestart { get; set; } = true;
    public int ScheduledRestartMinutes { get; set; } = 1435; // 23h 55m
    public bool EnableControlServerMechanism { get; set; } = true;
    public int AccessProbeServerLimit { get; set; } = 5;
    public List<Guid> AccessProbeServerIds { get; set; } = [];
    public DateTimeOffset? LastAccessScriptAttemptUtc { get; set; }
    public DateTimeOffset? LastAccessScriptSuccessUtc { get; set; }
    public DateTimeOffset? LastAccessConfirmedUtc { get; set; }
    public DateTimeOffset? AccessScheduleBaselineUtc { get; set; }
    public string LastAccessScriptResult { get; set; } = "";
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public double? LastConnectLatencyMs { get; set; }
    public double? LastStartupLatencyMs { get; set; }
    public DateTimeOffset? LastStartupFailureUtc { get; set; }
    public override string ToString() => $"{Name} ({Host}:{Port})";

    public BaseProxy Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        UseAutomaticPort = UseAutomaticPort,
        Enabled = Enabled,
        StartupServerId = StartupServerId,
        AutoStartWhenUnavailable = AutoStartWhenUnavailable,
        TotpSecret = TotpSecret,
        TotpAlgorithm = TotpAlgorithm,
        TotpDigits = TotpDigits,
        TotpPeriodSeconds = TotpPeriodSeconds,
        TotpPrompt = TotpPrompt,
        PostLoginCommand = PostLoginCommand,
        RepeatAccountPasswordAfterCommand = RepeatAccountPasswordAfterCommand,
        PostLoginPasswordPrompt = PostLoginPasswordPrompt,
        PostCommandReadyDelaySeconds = PostCommandReadyDelaySeconds,
        EnableScheduledRestart = EnableScheduledRestart,
        ScheduledRestartMinutes = ScheduledRestartMinutes,
        EnableControlServerMechanism = EnableControlServerMechanism,
        AccessProbeServerLimit = AccessProbeServerLimit,
        AccessProbeServerIds = [..AccessProbeServerIds],
        LastAccessScriptAttemptUtc = LastAccessScriptAttemptUtc,
        LastAccessScriptSuccessUtc = LastAccessScriptSuccessUtc,
        LastAccessConfirmedUtc = LastAccessConfirmedUtc,
        AccessScheduleBaselineUtc = AccessScheduleBaselineUtc,
        LastAccessScriptResult = LastAccessScriptResult,
        LastSuccessUtc = LastSuccessUtc,
        LastConnectLatencyMs = LastConnectLatencyMs,
        LastStartupLatencyMs = LastStartupLatencyMs,
        LastStartupFailureUtc = LastStartupFailureUtc
    };
}

public sealed class ServerLink
{
    public Guid FromServerId { get; set; }
    public Guid ToServerId { get; set; }
    public bool Discovered { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string LastStrategy { get; set; } = "";
    public Guid? LastSuccessfulProxyId { get; set; }
    public double? LastLatencyMs { get; set; }
    public List<LinkProxyStatistic> ProxyStatistics { get; set; } = [];

    public ServerLink Clone() => new()
    {
        FromServerId = FromServerId,
        ToServerId = ToServerId,
        Discovered = Discovered,
        LastSuccessUtc = LastSuccessUtc,
        LastStrategy = LastStrategy,
        LastSuccessfulProxyId = LastSuccessfulProxyId,
        LastLatencyMs = LastLatencyMs,
        ProxyStatistics = ProxyStatistics.Select(s => s.Clone()).ToList()
    };
}

public sealed class LinkProxyStatistic
{
    public Guid ProxyId { get; set; }
    public DateTimeOffset LastSuccessUtc { get; set; }
    public double? LatencyMs { get; set; }
    public string Strategy { get; set; } = "";

    public LinkProxyStatistic Clone() => new()
    {
        ProxyId = ProxyId,
        LastSuccessUtc = LastSuccessUtc,
        LatencyMs = LatencyMs,
        Strategy = Strategy
    };
}

public sealed class ImportedProxy
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int Method { get; set; }
}

public sealed record RouteCandidate(
    BaseProxy Proxy, IReadOnlyList<ManagedServer> Servers, bool WithoutProxy = false);
public sealed record RouteHop(Guid ServerId, string ServerName, string Host, int Port);
public sealed record ConnectivityResult(
    Guid TargetId, bool Success, string Message, TimeSpan Duration, string Strategy = "", Guid? ProxyId = null, Guid SourceId = default);

/// <summary>
/// Альтернативный адрес подключения к серверу. Пустой <see cref="Host"/> означает
/// «использовать основной хост сервера»; <see cref="Port"/> обязателен (> 0).
/// </summary>
public sealed class ServerEndpoint : IEquatable<ServerEndpoint>
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    /// <summary>Адрес доступен только после перехода через другой сервер.</summary>
    public bool InternalOnly { get; set; }

    public ServerEndpoint() { }
    public ServerEndpoint(string host, int port, bool internalOnly = false) { Host = host; Port = port; InternalOnly = internalOnly; }
    public ServerEndpoint Clone() => new(Host, Port, InternalOnly);

    public bool Equals(ServerEndpoint? other) =>
        other is not null &&
        string.Equals(Host ?? "", other.Host ?? "", StringComparison.Ordinal) &&
        Port == other.Port;

    public override bool Equals(object? obj) => Equals(obj as ServerEndpoint);
    public override int GetHashCode() => HashCode.Combine(Host ?? "", Port);
}

public readonly record struct EndpointContext(Guid? ProxyId, Guid? PreviousServerId)
{
    public static EndpointContext Direct(Guid proxyId) => new(proxyId, null);
    public static EndpointContext Via(Guid previousServerId) => new(null, previousServerId);
}

public sealed class EndpointPreference
{
    public Guid? ProxyId { get; set; }
    public Guid? PreviousServerId { get; set; }
    public ServerEndpoint Endpoint { get; set; } = new();
    public DateTimeOffset LastSuccessUtc { get; set; }

    public EndpointPreference Clone() => new()
    {
        ProxyId = ProxyId,
        PreviousServerId = PreviousServerId,
        Endpoint = Endpoint.Clone(),
        LastSuccessUtc = LastSuccessUtc
    };
}

public static class ManagedServerPamExtensions
{
    public static bool IsFullUnconfiguredPamExport(this ManagedServer? s)
    {
        if (s is null) return false;
        return s.HasAllPamPropertiesPresentInImport &&
               s.HasExplicitUsePam && !s.UsePam &&
               s.HasExplicitPamAutoConfirm && !s.PamAutoConfirm &&
               string.IsNullOrWhiteSpace(s.PamSshCommand) &&
               string.IsNullOrEmpty(s.PamPassword) &&
               string.IsNullOrEmpty(s.PamTotpSecret) &&
               s.PamTotpAlgorithm == "SHA1" &&
               s.PamTotpDigits == 6 &&
               s.PamTotpPeriodSeconds == 30 &&
               s.PamTotpPrompt == "OTP: " &&
               s.PamConfirmPrompt == "[Y/n]";
    }

    public static bool IsUnconfiguredPam(this ManagedServer? s)
    {
        if (s is null) return true;
        return !s.HasAnyPamFieldPresent() || s.IsFullUnconfiguredPamExport();
    }

    public static bool HasPamConfigured(this ManagedServer? s) =>
        s is not null && !s.IsUnconfiguredPam();

    public static bool HasAnyPamFieldPresent(this ManagedServer? s) =>
        s is not null &&
        (s.HasExplicitUsePam ||
         s.HasExplicitPamAutoConfirm ||
         !string.IsNullOrWhiteSpace(s.PamSshCommand) ||
         !string.IsNullOrEmpty(s.PamPassword) ||
         !string.IsNullOrEmpty(s.PamTotpSecret) ||
         !string.IsNullOrWhiteSpace(s.PamTotpAlgorithm) ||
         s.PamTotpDigits > 0 ||
         s.PamTotpPeriodSeconds > 0 ||
         !string.IsNullOrWhiteSpace(s.PamTotpPrompt) ||
         !string.IsNullOrWhiteSpace(s.PamConfirmPrompt));

    public static void CopyPamSettingsFrom(this ManagedServer target, ManagedServer source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.UsePam = source.UsePam;
        target.HasExplicitUsePam = source.HasExplicitUsePam;
        target.PamSshCommand = source.PamSshCommand;
        target.PamPassword = source.PamPassword;
        target.PamTotpSecret = source.PamTotpSecret;
        target.PamTotpAlgorithm = source.PamTotpAlgorithm;
        target.PamTotpDigits = source.PamTotpDigits;
        target.PamTotpPeriodSeconds = source.PamTotpPeriodSeconds;
        target.PamTotpPrompt = source.PamTotpPrompt;
        target.PamAutoConfirm = source.PamAutoConfirm;
        target.HasExplicitPamAutoConfirm = source.HasExplicitPamAutoConfirm;
        target.PamConfirmPrompt = source.PamConfirmPrompt;
        target.HasAllPamPropertiesPresentInImport = source.HasAllPamPropertiesPresentInImport;
    }

    public static void MergePamPreservingPopulated(this ManagedServer replacement, ManagedServer existing)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (existing is null || !existing.HasPamConfigured()) return;

        if (!replacement.HasPamConfigured())
        {
            // Входной PAM не настроен (включая штатный экспорт сессии без PAM): сохраняем ВСЕ 10 полей из existing
            replacement.CopyPamSettingsFrom(existing);
            return;
        }

        // Входной PAM настроен: сохраняем из existing только незаполненные поля
        if (!replacement.HasExplicitUsePam)
            replacement.UsePam = existing.UsePam;

        if (!replacement.HasExplicitPamAutoConfirm)
            replacement.PamAutoConfirm = existing.PamAutoConfirm;

        if (string.IsNullOrWhiteSpace(replacement.PamSshCommand))
            replacement.PamSshCommand = existing.PamSshCommand;

        if (string.IsNullOrEmpty(replacement.PamPassword))
            replacement.PamPassword = existing.PamPassword;

        if (string.IsNullOrEmpty(replacement.PamTotpSecret))
            replacement.PamTotpSecret = existing.PamTotpSecret;

        if (string.IsNullOrWhiteSpace(replacement.PamTotpAlgorithm))
            replacement.PamTotpAlgorithm = existing.PamTotpAlgorithm;

        if (replacement.PamTotpDigits <= 0)
            replacement.PamTotpDigits = existing.PamTotpDigits;

        if (replacement.PamTotpPeriodSeconds <= 0)
            replacement.PamTotpPeriodSeconds = existing.PamTotpPeriodSeconds;

        if (string.IsNullOrWhiteSpace(replacement.PamTotpPrompt))
            replacement.PamTotpPrompt = existing.PamTotpPrompt;

        if (string.IsNullOrWhiteSpace(replacement.PamConfirmPrompt))
            replacement.PamConfirmPrompt = existing.PamConfirmPrompt;
    }
}

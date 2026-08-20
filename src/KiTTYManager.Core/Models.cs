using System.Text.Json.Serialization;

namespace KiTTYManager.Core;

public sealed class ManagerConfig
{
    public int SchemaVersion { get; set; } = 7;
    public List<ServerGroup> Groups { get; set; } = [];
    public List<ManagedServer> UngroupedServers { get; set; } = [];
    public List<ServerLink> Links { get; set; } = [];
    public List<BaseProxy> BaseProxies { get; set; } = [];
    public Guid? PreferredProxyId { get; set; }
    public string KittyPath { get; set; } = "KiTTY\\kitty.exe";
    public string FirefoxPath { get; set; } = "firefox.exe";
    public string FirefoxProfile { get; set; } = "kitty-manager";
    public bool ClosePreferenceConfigured { get; set; }
    public bool CloseToTray { get; set; }
    public bool EnableLogging { get; set; }
    public bool WriteChangesImmediatelyToKitty { get; set; }
    public bool CloseWebTunnelWithFirefox { get; set; }
    public bool TemporaryFirefoxProfiles { get; set; } = true;
    public bool ShareFirefoxProfileByGroup { get; set; }
    public bool UseInternalWebResolver { get; set; }
    public string FirefoxTemplateProfile { get; set; } = "";
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int EndpointProbeTimeoutSeconds { get; set; } = 4;
    public bool AutoConfirmHostKeys { get; set; } = true;
    public bool SuppressKittyChangeNotifications { get; set; } = true;
    public bool RaceBestEntryPoints { get; set; }
    public bool SkipExistingLinksInMapCheck { get; set; } = true;
}

public sealed class ServerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новая группа";
    public List<ServerGroup> Groups { get; set; } = [];
    public List<ManagedServer> Servers { get; set; } = [];
    public override string ToString() => Name;
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
    public KittySessionSnapshot? KittyBaseline { get; set; }
    public string HostKeyFingerprint { get; set; } = "";
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
}

public sealed class IgnoredKittyChange
{
    public string PropertyName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
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
}

public sealed class CachedRoute
{
    public Guid ProxyId { get; set; }
    public List<Guid> ServerIds { get; set; } = [];
    public double? LatencyMs { get; set; }
    public DateTimeOffset LastSuccessUtc { get; set; }
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
    public override string ToString() => $"{Name} ({Host}:{Port})";
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
}

public sealed class LinkProxyStatistic
{
    public Guid ProxyId { get; set; }
    public DateTimeOffset LastSuccessUtc { get; set; }
    public double? LatencyMs { get; set; }
    public string Strategy { get; set; } = "";
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
    public int Port { get; set; }

    public ServerEndpoint() { }
    public ServerEndpoint(string host, int port) { Host = host; Port = port; }

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
}

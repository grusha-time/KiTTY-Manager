using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;

namespace KiTTYManager.Core;

public sealed class SmartDiscoveryOptions
{
    public List<string> TargetHosts { get; set; } = [];
    public List<string> Usernames { get; set; } = [];
    public List<string> Passwords { get; set; } = [];
    public Guid? TargetGroupId { get; set; }
    public string? TargetSubgroupName { get; set; }
    public BaseProxy? JumpHost { get; set; }
    public bool AutoSelectJumpHost { get; set; }
    public int MaxParallelConnections { get; set; } = 4;
    public int MaxAttemptsPerHost { get; set; } = 5;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan EndpointProbeTimeout { get; set; } = TimeSpan.FromSeconds(4);
    public bool PreferGroupGateways { get; set; } = true;
    public Guid? InitialGatewayServerId { get; set; }
}

public sealed class PrivilegeEscalationResult
{
    public string Method { get; set; } = "";
    public string RootCommand { get; set; } = "";
    public string RootPassword { get; set; } = "";
    public string StatusDescription { get; set; } = "";
    public bool Success { get; set; }

    public override string ToString() =>
        Success ? $"{Method} ({StatusDescription})" : "Не повышено";
}

public sealed class DiscoveredServerCandidate : INotifyPropertyChanged
{
    private bool isSelected = true;
    private bool isDuplicate;
    private string proposedName = "";
    private Guid? groupId;
    private string? groupName;
    private string? subgroupName;
    private string username = "";
    private string password = "";
    private bool showPassword;
    private bool showRootPassword;
    private string statusMessage = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Hostname { get; set; } = "";

    public string ProposedName
    {
        get => proposedName;
        set { if (proposedName != value) { proposedName = value; OnPropertyChanged(); } }
    }

    public Guid? GroupId
    {
        get => groupId;
        set { if (groupId != value) { groupId = value; OnPropertyChanged(); } }
    }

    public string? GroupName
    {
        get => groupName;
        set { if (groupName != value) { groupName = value; OnPropertyChanged(); } }
    }

    public string? SubgroupName
    {
        get => subgroupName;
        set { if (subgroupName != value) { subgroupName = value; OnPropertyChanged(); } }
    }

    public string Username
    {
        get => username;
        set { if (username != value) { username = value; OnPropertyChanged(); } }
    }

    public string Password
    {
        get => password;
        set
        {
            if (password != value)
            {
                password = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayPassword));
            }
        }
    }

    public bool ShowPassword
    {
        get => showPassword;
        set
        {
            if (showPassword != value)
            {
                showPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayPassword));
            }
        }
    }

    public string DisplayPassword =>
        ShowPassword ? Password : (string.IsNullOrEmpty(Password) ? "" : new string('•', Math.Min(Math.Max(Password.Length, 6), 12)));

    public bool ShowRootPassword
    {
        get => showRootPassword;
        set
        {
            if (showRootPassword != value)
            {
                showRootPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayRootPassword));
            }
        }
    }

    public string DisplayRootPassword =>
        ShowRootPassword ? PrivilegeEscalation.RootPassword : (string.IsNullOrEmpty(PrivilegeEscalation.RootPassword) ? "" : new string('•', Math.Min(Math.Max(PrivilegeEscalation.RootPassword.Length, 6), 12)));

    public PrivilegeEscalationResult PrivilegeEscalation { get; set; } = new();
    public List<RouteHop> RouteHops { get; set; } = [];
    public List<Guid> DiscoveredLinksFrom { get; set; } = [];
    public Guid? RequiredPreviousServerId { get; set; }
    public Guid? ExistingServerId { get; set; }
    public Guid? VerifiedProxyId { get; set; }

    public string StatusMessage
    {
        get => statusMessage;
        set { if (statusMessage != value) { statusMessage = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected != value) { isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsDuplicate
    {
        get => isDuplicate;
        set { if (isDuplicate != value) { isDuplicate = value; OnPropertyChanged(); } }
    }

    public string HostKeyFingerprint { get; set; } = "";
    public string HostKeyAlgorithm { get; set; } = "";
    public int HostKeyBits { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{ProposedName} [{Host}:{Port}] ({Username})";
}

public sealed class ProblematicServerCandidate : INotifyPropertyChanged
{
    private bool isSelected = true;

    public string Host { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool CanRetry { get; set; } = true;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected != value)
            {
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => $"{Host} [{Stage}]: {Reason}";
}

public enum SmartDiscoveryProgressKind
{
    Starting,
    ReachabilityProbing,
    AuthenticationProbing,
    EscalationProbing,
    MultiHopProbing,
    HostSucceeded,
    HostFailed,
    Completed,
    Cancelled
}

public sealed class SmartDiscoveryProgress
{
    public SmartDiscoveryProgressKind Kind { get; set; }
    public string Host { get; set; } = "";
    public string Message { get; set; } = "";
    public int DiscoveredCount { get; set; }
    public int ProblemCount { get; set; }
    public int TotalCount { get; set; }
    public double ProgressPercentage { get; set; }
}

public static class SmartDiscoveryInputParser
{
    private static readonly char[] HostSeparators = [' ', '\t', '\r', '\n', ',', ';'];
    private static readonly char[] ListSeparators = ['\r', '\n', ',', ';'];

    public static List<string> ParseHosts(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var tokens = input.Split(HostSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in tokens)
        {
            var token = raw.Trim().Trim('"', '\'', '[', ']');
            if (string.IsNullOrEmpty(token)) continue;

            // Handle user@host
            var atIndex = token.LastIndexOf('@');
            if (atIndex >= 0 && atIndex < token.Length - 1)
                token = token[(atIndex + 1)..];

            // Handle host:port (for IPv4 or standard hostname)
            var colonIndex = token.LastIndexOf(':');
            if (colonIndex > 0 && !token.Contains("::") && int.TryParse(token[(colonIndex + 1)..], out _))
                token = token[..colonIndex];

            token = token.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            if (IsValidHost(token))
            {
                if (seen.Add(token))
                    result.Add(token);
            }
        }

        return result;
    }

    public static List<string> ParseList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var tokens = input.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            var cleaned = token.Trim();
            if (cleaned.Length > 0 && seen.Add(cleaned))
                result.Add(cleaned);
        }

        return result;
    }

    public static List<string> ParsePasswords(string? input)
    {
        if (string.IsNullOrEmpty(input)) return [];

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (line.Length > 0 && seen.Add(line))
                result.Add(line);
        }

        return result;
    }

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;

        if (Regex.IsMatch(host, @"^\d+\.\d+\.\d+\.\d+$"))
            return IPAddress.TryParse(host, out _);

        if (IPAddress.TryParse(host, out _)) return true;

        var type = Uri.CheckHostName(host);
        if (type == UriHostNameType.Dns)
        {
            // Verify no invalid characters like spaces or consecutive dots
            if (host.Contains("..")) return false;
            var parts = host.Split('.');
            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 63) return false;
                if (!char.IsLetterOrDigit(part[0]) || !char.IsLetterOrDigit(part[^1])) return false;
                if (!Regex.IsMatch(part, @"^[a-zA-Z0-9\-]+$")) return false;
            }
            if (parts.Length > 1 && Regex.IsMatch(parts[^1], @"^\d+$"))
                return false;

            return true;
        }

        return false;
    }
}

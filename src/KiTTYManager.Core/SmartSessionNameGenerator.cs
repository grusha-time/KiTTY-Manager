using System.Net;
using System.Text.RegularExpressions;

namespace KiTTYManager.Core;

public static class SmartSessionNameGenerator
{
    private static readonly Dictionary<string, string> StandardSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "Main",
        ["reserve"] = "Reserve",
        ["backup"] = "Backup",
        ["prod"] = "Prod",
        ["production"] = "Prod",
        ["dev"] = "Dev",
        ["devel"] = "Dev",
        ["development"] = "Dev",
        ["test"] = "Test",
        ["testing"] = "Test",
        ["qa"] = "Test",
        ["stage"] = "Stage",
        ["staging"] = "Stage",
        ["dr"] = "DR",
        ["standby"] = "Standby"
    };

    private static readonly Dictionary<string, string> StandardRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["group"] = "Group",
        ["map"] = "MapServer",
        ["mapserver"] = "MapServer",
        ["db"] = "DB",
        ["database"] = "DB",
        ["app"] = "App",
        ["application"] = "App",
        ["web"] = "Web",
        ["gw"] = "Gateway",
        ["gateway"] = "Gateway",
        ["srv"] = "Server",
        ["server"] = "Server",
        ["auth"] = "Auth",
        ["mon"] = "Monitor",
        ["monitor"] = "Monitor",
        ["monitoring"] = "Monitor",
        ["proxy"] = "Proxy",
        ["storage"] = "Storage",
        ["nas"] = "Storage",
        ["san"] = "Storage",
        ["api"] = "API",
        ["core"] = "Core",
        ["vpn"] = "VPN",
        ["log"] = "Logs",
        ["logs"] = "Logs",
        ["mail"] = "Mail",
        ["smtp"] = "Mail",
        ["dns"] = "DNS",
        ["ldap"] = "LDAP",
        ["cache"] = "Cache",
        ["redis"] = "Cache"
    };

    private static readonly Dictionary<string, string> KnownRegionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ny"] = "New York",
        ["nyc"] = "New York",
        ["chi"] = "Chicago",
        ["dal"] = "Dallas",
        ["lon"] = "London",
        ["fra"] = "Frankfurt",
        ["ams"] = "Amsterdam",
        ["par"] = "Paris",
        ["tok"] = "Tokyo",
        ["syd"] = "Sydney",
        ["msk"] = "Moscow",
        ["spb"] = "Saint Petersburg"
    };

    public static string ProposeName(
        string? hostname,
        string ip,
        ManagerConfig config,
        Guid? targetGroupId = null,
        string? targetSubgroupName = null,
        IEnumerable<string>? existingNamesInBatch = null)
    {
        var rawHost = (hostname ?? "").Trim();
        // If FQDN like s1-group-main.corp.domain, strip domain part
        var dotIdx = rawHost.IndexOf('.');
        if (dotIdx > 0 && !IPAddress.TryParse(rawHost, out _))
            rawHost = rawHost[..dotIdx];

        var tokens = Tokenize(rawHost);

        string? site = null;
        string? identifier = null;
        string? role = null;
        string? region = null;

        var remainingTokens = new List<string>(tokens);

        // 1. Identify Site / Environment
        for (var i = remainingTokens.Count - 1; i >= 0; i--)
        {
            var token = remainingTokens[i];
            if (StandardSites.TryGetValue(token, out var recognizedSite))
            {
                site = recognizedSite;
                remainingTokens.RemoveAt(i);
                break;
            }
        }

        // 2. Identify Server Identifier (s1 -> S1, s5 -> S5, node1 -> Node1, vm02 -> VM02)
        for (var i = 0; i < remainingTokens.Count; i++)
        {
            var token = remainingTokens[i];
            if (Regex.IsMatch(token, @"^[sS]\d+$"))
            {
                identifier = token.ToUpperInvariant();
                remainingTokens.RemoveAt(i);
                break;
            }
            if (Regex.IsMatch(token, @"^[a-zA-Z]+\d+$"))
            {
                identifier = Capitalize(token);
                remainingTokens.RemoveAt(i);
                break;
            }
        }

        // 3. Identify Role / Purpose
        // 3a. Predefined standard roles
        for (var i = 0; i < remainingTokens.Count; i++)
        {
            var token = remainingTokens[i];
            if (StandardRoles.TryGetValue(token, out var recognizedRole))
            {
                role = recognizedRole;
                remainingTokens.RemoveAt(i);
                break;
            }
        }

        // 3b. Match against existing server display names in config
        if (role == null)
        {
            var existingNames = config.AllServers().Select(s => s.Name).ToArray();
            for (var i = 0; i < remainingTokens.Count; i++)
            {
                var token = remainingTokens[i];
                var matchedWord = FindWordInExistingNames(token, existingNames);
                if (matchedWord != null)
                {
                    role = matchedWord;
                    remainingTokens.RemoveAt(i);
                    break;
                }
            }
        }

        // 4. Derive Region and Site from target group, IP subnet (/24) and hostname tokens
        // 4a. Target group: a nested path contributes region and site, a top-level group is the
        // region unless a matching /24 server proves a different region (then it becomes the site).
        string? groupSiteCandidate = null;
        string? topLevelGroupName = null;
        if (targetGroupId.HasValue)
        {
            var group = config.FindGroup(targetGroupId.Value);
            if (group != null && !IsGenericGroupName(group.Name))
            {
                var pathParts = config.GroupPath(group.Id)
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (pathParts.Length > 1)
                {
                    region = pathParts[0];
                    groupSiteCandidate = pathParts[^1];
                }
                else
                {
                    topLevelGroupName = group.Name;
                }
            }
        }

        // 4b. Match region from IP subnet (/24) among existing servers
        string? subnetRegion = null;
        if (IPAddress.TryParse(ip, out var parsedIp) && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var lastDot = ip.LastIndexOf('.');
            if (lastDot > 0)
            {
                var subnetPrefix = ip[..(lastDot + 1)];
                var matchingSubnetServer = config.AllServers().FirstOrDefault(s => s.Host.StartsWith(subnetPrefix, StringComparison.Ordinal));
                if (matchingSubnetServer != null)
                {
                    subnetRegion = ExtractRegionFromExistingServer(matchingSubnetServer, config);
                }
            }
        }

        if (region == null && topLevelGroupName != null)
        {
            if (subnetRegion != null && !string.Equals(subnetRegion, topLevelGroupName, StringComparison.OrdinalIgnoreCase))
            {
                region = subnetRegion;
                groupSiteCandidate = topLevelGroupName;
            }
            else
            {
                region = topLevelGroupName;
            }
        }

        if (region == null)
            region = subnetRegion;

        // 4c. Region from hostname tokens (e.g. "ny" -> "New York", or token matching group name)
        if (region == null)
        {
            for (var i = 0; i < remainingTokens.Count; i++)
            {
                var token = remainingTokens[i];
                if (KnownRegionCodes.TryGetValue(token, out var matchedRegion))
                {
                    region = matchedRegion;
                    remainingTokens.RemoveAt(i);
                    break;
                }
                var matchingGroup = config.AllGroups().FirstOrDefault(g => string.Equals(g.Name, token, StringComparison.OrdinalIgnoreCase));
                if (matchingGroup != null)
                {
                    region = matchingGroup.Name;
                    remainingTokens.RemoveAt(i);
                    break;
                }
            }
        }

        // 4d. An explicit subgroup name is the most specific site when it differs from the region
        if (!string.IsNullOrWhiteSpace(targetSubgroupName) && !IsGenericGroupName(targetSubgroupName) &&
            !string.Equals(targetSubgroupName, region, StringComparison.OrdinalIgnoreCase))
        {
            groupSiteCandidate = targetSubgroupName;
        }

        if (!string.IsNullOrWhiteSpace(groupSiteCandidate))
            site = groupSiteCandidate;

        // Remove hostname tokens already represented by the resolved region or site
        for (var i = remainingTokens.Count - 1; i >= 0; i--)
        {
            var token = remainingTokens[i];
            if (region != null && string.Equals(token, region, StringComparison.OrdinalIgnoreCase) ||
                site != null && string.Equals(token, site, StringComparison.OrdinalIgnoreCase))
            {
                remainingTokens.RemoveAt(i);
            }
        }

        // 5. Role and remaining tokens; a leftover token is not promoted to role when a site exists
        var roleOrToken = role ?? (site == null && remainingTokens.Count > 0 ? Capitalize(remainingTokens[0]) : null);
        if (role == null && site == null && remainingTokens.Count > 0)
        {
            remainingTokens.RemoveAt(0);
        }

        // 6. Build proposed name components:
        // When role is present: [Region] [Role] [Identifier] [Site] (e.g. "Group S1 Main", "Chicago App S2 Prod")
        // When role is absent: [Region] [Identifier] [Site] (e.g. "Chicago S5 Reserve")
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(region))
            parts.Add(region);

        if (roleOrToken != null)
            parts.Add(roleOrToken);

        if (!string.IsNullOrWhiteSpace(identifier))
            parts.Add(identifier);

        if (!string.IsNullOrWhiteSpace(site))
            parts.Add(site);

        string baseName;
        if (parts.Count > 0)
        {
            baseName = string.Join(" ", parts);
        }
        else if (tokens.Length > 0)
        {
            baseName = string.Join(" ", tokens.Select(Capitalize));
        }
        else
        {
            baseName = string.IsNullOrWhiteSpace(ip) ? "Новый сервер" : $"Host {ip}";
        }

        // 7. Ensure Uniqueness against config.AllServers() and existingNamesInBatch
        var existingSet = config.AllServers().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existingNamesInBatch != null)
        {
            foreach (var n in existingNamesInBatch)
                existingSet.Add(n);
        }

        if (!existingSet.Contains(baseName))
            return baseName;

        for (var counter = 2; ; counter++)
        {
            var candidate = $"{baseName} {counter}";
            if (!existingSet.Contains(candidate))
                return candidate;
        }
    }

    private static string[] Tokenize(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return [];
        return host.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length == 1) return text.ToUpperInvariant();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool IsGenericGroupName(string name) =>
        string.Equals(name, "Новая группа", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Новая подгруппа", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Group", StringComparison.OrdinalIgnoreCase);

    private static string? FindWordInExistingNames(string token, string[] existingNames)
    {
        foreach (var name in existingNames)
        {
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (string.Equals(word, token, StringComparison.OrdinalIgnoreCase))
                    return word;
            }
        }
        return null;
    }

    private static string? ExtractRegionFromExistingServer(ManagedServer server, ManagerConfig config)
    {
        var group = config.FindServerGroup(server.Id);
        if (group != null && !IsGenericGroupName(group.Name))
        {
            var pathParts = config.GroupPath(group.Id)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pathParts.Length > 0 && !IsGenericGroupName(pathParts[0]))
                return pathParts[0];
            return group.Name;
        }

        // Otherwise check first word of server name
        var words = server.Name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && !StandardRoles.ContainsKey(words[0]) && !StandardSites.ContainsKey(words[0]))
        {
            // If words[0] and words[1] together make a known region like "New York"
            if (words.Length >= 3 && string.Equals(words[0], "New", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(words[1], "York", StringComparison.OrdinalIgnoreCase))
                return "New York";

            return words[0];
        }

        return null;
    }
}

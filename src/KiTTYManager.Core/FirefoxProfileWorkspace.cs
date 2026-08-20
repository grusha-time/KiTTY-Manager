namespace KiTTYManager.Core;

public sealed class FirefoxProfileLockedException : IOException
{
    public string TemplateProfilePath { get; }

    public FirefoxProfileLockedException(string templateProfilePath, IOException inner)
        : base($"Шаблон профиля Firefox заблокирован запущенным процессом: {templateProfilePath}", inner)
    {
        TemplateProfilePath = templateProfilePath;
    }
}

public static class FirefoxProfileWorkspace
{
    public static IReadOnlyList<string> LaunchArguments(string profile, string url) =>
        ["-wait-for-browser", "-no-remote", "-profile", profile, url];

    public static string Create(string runtimeRoot, Guid serverId, Guid webId, string? templateProfile = null)
    {
        var path = Path.Combine(runtimeRoot, $"{serverId:N}-{webId:N}-{Guid.NewGuid():N}");
        if (!string.IsNullOrWhiteSpace(templateProfile) && Directory.Exists(templateProfile))
        {
            try
            {
                CopyDirectory(templateProfile, path);
            }
            catch (IOException ex)
            {
                try { Directory.Delete(path, true); } catch { }
                throw new FirefoxProfileLockedException(templateProfile, ex);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            // Skip history database (large, not needed for tunnel)
            if (name.Equals("places.sqlite", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(source, "places.sqlite"))) continue;
            File.Copy(file, Path.Combine(destination, name), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            // Skip cache and temporary directories
            if (name is "cache2" or "startupCache" or "locks" or "minidumps" or "thumbnails" or "bookmarkbackups") continue;
            CopyDirectory(dir, Path.Combine(destination, name));
        }
    }

    public static string Persistent(string persistentRoot, string key)
    {
        var path = Path.Combine(persistentRoot, key);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void ApplyPreferences(string profile, int port, bool useInternalResolver = false,
        IEnumerable<string>? internalDomains = null)
    {
        // user.js: browser behavior settings ONLY (no proxy settings).
        // Firefox internal code overrides socks_remote_dns when it sees proxy
        // configuration in user.js. Keeping proxy settings exclusively in
        // prefs.js makes Firefox treat them as its own saved state.
        File.WriteAllText(Path.Combine(profile, "user.js"), UserPreferences());
        // prefs.js: proxy settings written as if Firefox saved them itself.
        var prefsPath = Path.Combine(profile, "prefs.js");
        var lines = File.Exists(prefsPath) ? File.ReadAllLines(prefsPath).ToList() : [];
        SetPreference(lines, "network.proxy.type", useInternalResolver ? "2" : "1");
        SetPreference(lines, "network.proxy.autoconfig_url", useInternalResolver
            ? $"\"{InternalProxyPacUrl(port, internalDomains ?? [])}\"" : "\"\"");
        SetPreference(lines, "network.proxy.http", "\"\"");
        SetPreference(lines, "network.proxy.http_port", "0");
        SetPreference(lines, "network.proxy.ssl", "\"\"");
        SetPreference(lines, "network.proxy.ssl_port", "0");
        SetPreference(lines, "network.proxy.socks", useInternalResolver ? "\"\"" : "\"127.0.0.1\"");
        SetPreference(lines, "network.proxy.socks_port", useInternalResolver ? "0" : port.ToString());
        SetPreference(lines, "network.proxy.socks_version", "5");
        SetPreference(lines, "network.proxy.socks_remote_dns", "false");
        SetPreference(lines, "network.proxy.socks5_remote_dns", "false");
        SetPreference(lines, "network.proxy.share_proxy_settings", "false");
        SetPreference(lines, "network.proxy.proxy_over_tls", "false");
        SetPreference(lines, "network.proxy.no_proxies_on", "\"\"");
        SetPreference(lines, "network.trr.mode", "5");
        SetPreference(lines, "network.dns.disablePrefetch", "true");
        // Prevent Firefox from running startup migrations on fresh profiles.
        // Migrations override socks_remote_dns to true for SOCKS5 proxies.
        SetPreference(lines, "browser.migration.version", "999");
        SetPreference(lines, "browser.startup.homepage_override.buildID", "\"20260101000000\"");
        File.WriteAllLines(prefsPath, lines);
        // Delete the startup cache so Firefox re-reads all preferences fresh.
        var startupCache = Path.Combine(profile, "startupCache");
        try { if (Directory.Exists(startupCache)) Directory.Delete(startupCache, true); } catch { }
    }

    /// <summary>Removes the installation-wide lock created by older versions.</summary>
    public static void RemoveLegacyAutoConfig(string firefoxExePath)
    {
        try
        {
            var firefoxRoot = Path.GetDirectoryName(firefoxExePath)!;
            var cfgPath = Path.Combine(firefoxRoot, "kitty-manager.cfg");
            var autoconfigJs = Path.Combine(firefoxRoot, "defaults", "pref", "kitty-manager-autoconfig.js");
            if (File.Exists(cfgPath) && File.ReadAllText(cfgPath).Trim() ==
                "//\nlockPref(\"network.proxy.socks_remote_dns\", false);") File.Delete(cfgPath);
            if (File.Exists(autoconfigJs) && File.ReadAllText(autoconfigJs).Contains(
                    "general.config.filename\", \"kitty-manager.cfg", StringComparison.Ordinal))
                File.Delete(autoconfigJs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "Не удалось удалить устаревшую глобальную настройку DNS Firefox. " +
                "Закройте Firefox и проверьте права на его папку.", ex);
        }
    }

    private static void SetPreference(List<string> lines, string name, string value)
    {
        var prefix = $"user_pref(\"{name}\",";
        lines.RemoveAll(line => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        lines.Add($"user_pref(\"{name}\", {value});");
    }

    public static string InternalProxyPacUrl(int port, IEnumerable<string> domains)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var conditions = domains.Select(ResolvingSocks5Relay.NormalizeDomain)
            .Distinct(StringComparer.Ordinal)
            .Select(domain => $"host === '{domain.Replace("'", "\\'")}'")
            .ToArray();
        if (conditions.Length == 0) throw new ArgumentException("Не указаны домены внутреннего proxy.", nameof(domains));
        var script = "function FindProxyForURL(url, host) { host = host.toLowerCase().replace(/\\.$/, ''); " +
            $"if ({string.Join(" || ", conditions)}) return 'PROXY 127.0.0.1:{port}'; return 'DIRECT'; }}";
        return "data:application/x-ns-proxy-autoconfig," + Uri.EscapeDataString(script);
    }

    /// <summary>
    /// Browser behavior preferences (no proxy settings). Proxy settings are
    /// written exclusively to prefs.js to avoid Firefox internal overrides.
    /// </summary>
    private static string UserPreferences() =>
        // Prevent Firefox from running startup migrations on fresh profiles.
        // Migrations override socks_remote_dns to true for SOCKS5 proxies.
        "user_pref(\"browser.migration.version\", 999);\n" +
        "user_pref(\"browser.startup.homepage_override.buildID\", \"20260101000000\");\n" +
        "user_pref(\"network.proxy.socks_remote_dns\", false);\n" +
        "user_pref(\"network.proxy.socks5_remote_dns\", false);\n" +
        "user_pref(\"network.proxy.proxy_over_tls\", false);\n" +
        "user_pref(\"network.prefetch-next\", false);\n" +
        "user_pref(\"browser.aboutwelcome.enabled\", false);\n" +
        "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
        "user_pref(\"browser.startup.firstrunSkipsHomepage\", true);\n" +
        "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
        "user_pref(\"browser.startup.homepage_welcome_url\", \"\");\n" +
        "user_pref(\"browser.startup.homepage_welcome_url.additional\", \"\");\n" +
        "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n" +
        "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
        "user_pref(\"datareporting.policy.firstRunURL\", \"\");\n" +
        "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);\n" +
        "user_pref(\"browser.privatebrowsing.autostart\", true);\n" +
        "user_pref(\"browser.cache.disk.enable\", false);\n" +
        "user_pref(\"browser.cache.offline.enable\", false);\n";

    public static string Preferences(int port) =>
        "user_pref(\"network.proxy.type\", 1);\n" +
        "user_pref(\"network.proxy.socks\", \"127.0.0.1\");\n" +
        $"user_pref(\"network.proxy.socks_port\", {port});\n" +
        "user_pref(\"network.proxy.socks_version\", 5);\n" +
        "user_pref(\"network.proxy.socks_remote_dns\", false);\n" +
        "user_pref(\"network.proxy.socks5_remote_dns\", false);\n" +
        "user_pref(\"network.proxy.no_proxies_on\", \"\");\n" +
        "user_pref(\"network.trr.mode\", 5);\n" +
        "user_pref(\"network.dns.disablePrefetch\", true);\n" +
        "user_pref(\"network.prefetch-next\", false);\n" +
        "user_pref(\"browser.aboutwelcome.enabled\", false);\n" +
        "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
        "user_pref(\"browser.startup.firstrunSkipsHomepage\", true);\n" +
        "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
        "user_pref(\"browser.startup.homepage_welcome_url\", \"\");\n" +
        "user_pref(\"browser.startup.homepage_welcome_url.additional\", \"\");\n" +
        "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);\n" +
        "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
        "user_pref(\"datareporting.policy.firstRunURL\", \"\");\n" +
        "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);\n" +
        "user_pref(\"browser.privatebrowsing.autostart\", true);\n" +
        "user_pref(\"browser.cache.disk.enable\", false);\n" +
        "user_pref(\"browser.cache.offline.enable\", false);\n";
}

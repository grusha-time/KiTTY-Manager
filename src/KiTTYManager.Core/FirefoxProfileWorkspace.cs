using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiTTYManager.Core;

public sealed class FirefoxProfileLockedException : IOException
{
    public string TemplateProfilePath { get; }

    public FirefoxProfileLockedException(string templateProfilePath, Exception inner)
        : base($"Исходный профиль Firefox занят запущенным Firefox: {templateProfilePath}. Закройте Firefox и повторите.", inner)
    {
        TemplateProfilePath = templateProfilePath;
    }
}

public static class FirefoxProfileWorkspace
{
    private static readonly HashSet<string> ProfileLockFiles =
        new(["parent.lock", ".parentlock", "lock"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeSkipFiles =
        new(["sessionstore.jsonlz4", "sessionCheckpoints.json"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeSkipDirectories =
        new(["startupCache", "cache2", "locks", "minidumps", "thumbnails", "bookmarkbackups",
            "sessionstore-backups", "crashes", "datareporting", "saved-telemetry-pings"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> LaunchArguments(string profile, string url) =>
        ["-wait-for-browser", "-no-remote", "-profile", profile, url];

    public static string DiscoverDefaultProfile(string appDataDirectory)
    {
        var iniPath = Path.Combine(appDataDirectory, "Mozilla", "Firefox", "profiles.ini");
        if (!File.Exists(iniPath))
            throw new FileNotFoundException("Firefox profiles.ini не найден. Запустите Firefox хотя бы один раз либо отключите автоматический поиск и укажите профиль вручную.", iniPath);
        var sections = ParseIni(File.ReadAllLines(iniPath));
        var firefoxRoot = Path.GetDirectoryName(iniPath)!;
        var profiles = sections.Where(pair => pair.Key.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).Where(values => values.TryGetValue("Path", out var path) && !string.IsNullOrWhiteSpace(path)).ToList();
        var candidates = new List<string>();
        foreach (var install in sections.Where(pair => pair.Key.StartsWith("Install", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
            if (install.TryGetValue("Default", out var path) && !string.IsNullOrWhiteSpace(path))
                candidates.Add(ResolveProfilePath(firefoxRoot, path, true));
        foreach (var profile in profiles.Where(values => values.TryGetValue("Default", out var value) && value == "1"))
            candidates.Add(ResolveProfilePath(firefoxRoot, profile["Path"], !profile.TryGetValue("IsRelative", out var relative) || relative != "0"));
        foreach (var profile in profiles)
            candidates.Add(ResolveProfilePath(firefoxRoot, profile["Path"], !profile.TryGetValue("IsRelative", out var relative) || relative != "0"));
        var selected = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsReadyProfile);
        return selected ?? throw new InvalidDataException("В profiles.ini не найден готовый профиль Firefox с key4.db и cert9.db. Запустите основной профиль Firefox либо укажите его вручную.");
    }

    private static string ResolveProfilePath(string firefoxRoot, string configured, bool relative)
    {
        var path = relative ? Path.Combine(firefoxRoot, configured.Replace('/', Path.DirectorySeparatorChar)) : configured;
        return Path.GetFullPath(path);
    }

    private static bool IsReadyProfile(string path) => Directory.Exists(path)
        && File.Exists(Path.Combine(path, "key4.db"))
        && File.Exists(Path.Combine(path, "cert9.db"));

    public static void ValidateSourceProfile(string sourceProfile)
    {
        if (!Directory.Exists(sourceProfile))
            throw new DirectoryNotFoundException($"Профиль Firefox не найден: {sourceProfile}");
        foreach (var name in new[] { "key4.db", "cert9.db" })
            if (!File.Exists(Path.Combine(sourceProfile, name)))
                throw new InvalidDataException($"Выбранная папка не является готовым профилем Firefox: отсутствует {name}. Запустите Firefox хотя бы один раз для создания профиля.");
    }

    public static string Create(string runtimeRoot, Guid serverId, Guid webId, string? templateProfile = null)
    {
        if (string.IsNullOrWhiteSpace(templateProfile))
            throw new InvalidDataException("Не указан исходный профиль Firefox. Проверьте настройки приложения.");
        ValidateSourceProfile(templateProfile);
        var path = Path.Combine(runtimeRoot, $"{serverId:N}-{webId:N}-{Guid.NewGuid():N}");
        try
        {
            CopyProfile(templateProfile, path);
            // The lock detector has done its job during the copy; Firefox
            // recreates the file and reuses its mtime for crash detection, so
            // a stale timestamp must not leak into the runtime profile.
            foreach (var name in ProfileLockFiles)
            {
                var lockPath = Path.Combine(path, name);
                try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
            if (IsSharingViolation(ex)) throw new FirefoxProfileLockedException(templateProfile, ex);
            throw;
        }
        return path;
    }

    public static bool IsSharingViolation(Exception exception) => exception is IOException io
        && (OperatingSystem.IsWindows()
            ? (io.HResult & 0xFFFF) is 32 or 33
            : io.HResult is 11 or 16); // flock EWOULDBLOCK/EBUSY on Unix

    private static void CopyProfile(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            // History is large and unused by the tunnel session; its wal/shm
            // sidecars are meaningless without the main database.
            if (name.StartsWith("places.sqlite", StringComparison.OrdinalIgnoreCase)) continue;
            // Restoring a previous session inside a disposable tunnel window
            // reopens stale tabs through the tunnel and revives crash-restore
            // prompts from a source Firefox that was killed.
            if (RuntimeSkipFiles.Contains(name)) continue;
            // Database sidecars travel with their main file: a source Firefox
            // that was killed leaves committed transactions in -wal, and a
            // copy without them silently rolls the database back. A running
            // source is refused earlier by the parent.lock sharing violation.
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            // parent.lock is copied on purpose: reading it throws a sharing
            // violation while the source Firefox is running, which surfaces as
            // FirefoxProfileLockedException instead of a hot-copied profile.
            File.Copy(file, Path.Combine(destination, name), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (RuntimeSkipDirectories.Contains(name) ||
                (Path.GetFileName(source).Equals("storage", StringComparison.OrdinalIgnoreCase) &&
                 name.Equals("temp", StringComparison.OrdinalIgnoreCase))) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyProfile(directory, Path.Combine(destination, name));
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new(StringComparer.OrdinalIgnoreCase);
                result[line[1..^1]] = current;
                continue;
            }
            var equals = line.IndexOf('=');
            if (current is not null && equals > 0) current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return result;
    }

    public static void ApplyPreferences(string profile, int port, bool useInternalResolver = false,
        IEnumerable<string>? internalDomains = null)
    {
        // user.js: browser behavior settings ONLY (no proxy server settings).
        // Firefox internal code overrides socks_remote_dns when it sees proxy
        // configuration in user.js. Keeping proxy settings exclusively in
        // prefs.js makes Firefox treat them as its own saved state.
        File.WriteAllText(Path.Combine(profile, "user.js"), UserPreferences());

        var localDomains = useInternalResolver
            ? string.Join(',', (internalDomains ?? []).Select(ResolvingSocks5Relay.NormalizeDomain).Distinct())
            : "";

        // prefs.js: proxy settings written as if Firefox saved them itself.
        var prefsPath = Path.Combine(profile, "prefs.js");
        var lines = File.Exists(prefsPath) ? File.ReadAllLines(prefsPath).ToList() : [];
        SetPreference(lines, "network.proxy.type", "1");
        SetPreference(lines, "network.proxy.autoconfig_url", "\"\"");
        SetPreference(lines, "network.proxy.http", "\"\"");
        SetPreference(lines, "network.proxy.http_port", "0");
        SetPreference(lines, "network.proxy.ssl", "\"\"");
        SetPreference(lines, "network.proxy.ssl_port", "0");
        SetPreference(lines, "network.proxy.socks", "\"127.0.0.1\"");
        SetPreference(lines, "network.proxy.socks_port", port.ToString());
        SetPreference(lines, "network.proxy.socks_version", "5");
        SetPreference(lines, "network.proxy.socks_remote_dns", "false");
        SetPreference(lines, "network.proxy.socks5_remote_dns", "false");
        SetPreference(lines, "network.proxy.share_proxy_settings", "false");
        SetPreference(lines, "network.proxy.proxy_over_tls", "false");
        SetPreference(lines, "network.proxy.no_proxies_on", "\"\"");
        SetPreference(lines, "network.dns.localDomains", JsonSerializer.Serialize(localDomains));
        SetPreference(lines, "network.proxy.allow_hijacking_localhost", useInternalResolver ? "true" : "false");
        SetPreference(lines, "network.trr.mode", "5");
        SetPreference(lines, "network.dns.disablePrefetch", "true");
        SetPreference(lines, "network.prefetch-next", "false");
        RemovePreference(lines, "security.enterprise_roots.enabled");
        SetPreference(lines, "browser.startup.blankWindow", "false");
        // Prevent Firefox from running startup migrations on the copied
        // profile: migrations override socks_remote_dns to true for SOCKS5.
        SetPreference(lines, "browser.migration.version", "999");
        SetPreference(lines, "browser.startup.homepage_override.mstone", "\"ignore\"");
        SetPreference(lines, "browser.startup.homepage_override.buildID", "\"20260101000000\"");
        SetPreference(lines, "browser.aboutwelcome.enabled", "false");
        SetPreference(lines, "trailhead.firstrun.didSeeAboutWelcome", "true");
        SetPreference(lines, "browser.startup.firstrunSkipsHomepage", "true");
        SetPreference(lines, "browser.startup.homepage_welcome_url", "\"\"");
        SetPreference(lines, "browser.startup.homepage_welcome_url.additional", "\"\"");
        SetPreference(lines, "datareporting.policy.dataSubmissionPolicyBypassNotification", "true");
        SetPreference(lines, "datareporting.policy.firstRunURL", "\"\"");
        SetPreference(lines, "toolkit.telemetry.reportingpolicy.firstRun", "false");
        SetPreference(lines, "browser.messaging-system.whatsNewPanel.enabled", "false");
        SetPreference(lines, "browser.shell.checkDefaultBrowser", "false");
        SetPreference(lines, "doh-rollout.doneFirstRun", "true");
        SetPreference(lines, "doh-rollout.enabled", "false");
        SetPreference(lines, "app.update.auto", "false");
        SetPreference(lines, "app.update.enabled", "false");
        SetPreference(lines, "app.update.service.enabled", "false");
        SetPreference(lines, "app.update.doorhanger", "false");
        File.WriteAllLines(prefsPath, lines);

        // Delete the startup cache so Firefox re-reads all preferences fresh.
        var startupCache = Path.Combine(profile, "startupCache");
        try { if (Directory.Exists(startupCache)) Directory.Delete(startupCache, true); } catch { }
    }

    private static string UserPreferences() =>
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
        "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);\n" +
        "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);\n" +
        "user_pref(\"doh-rollout.doneFirstRun\", true);\n" +
        "user_pref(\"doh-rollout.enabled\", false);\n" +
        "user_pref(\"app.update.auto\", false);\n" +
        "user_pref(\"app.update.enabled\", false);\n" +
        "user_pref(\"app.update.service.enabled\", false);\n" +
        "user_pref(\"app.update.doorhanger\", false);\n" +
        "user_pref(\"app.update.silent\", false);\n" +
        "user_pref(\"toolkit.asyncshutdown.crash_timeout\", 0);\n" +
        "user_pref(\"messaging-system.rsexperimentloader.enabled\", false);\n" +
        "user_pref(\"app.normandy.enabled\", false);\n" +
        "user_pref(\"app.normandy.api_url\", \"\");\n" +
        "user_pref(\"app.shield.optoutstudy.enabled\", false);\n" +
        "user_pref(\"experiments.enabled\", false);\n" +
        "user_pref(\"experiments.supported\", false);\n" +
        "user_pref(\"nimbus.experiments.enabled\", false);\n" +
        "user_pref(\"toolkit.telemetry.enabled\", false);\n" +
        "user_pref(\"toolkit.telemetry.unified\", false);\n";

    public static bool CanMergeAndDelete(string profile)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return Directory.Exists(profile);
    }

    public static string StateSummary(string profile)
    {
        var logins = 0;
        try { logins = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "logins.json")))?["logins"]?.AsArray().Count ?? 0; }
        catch { }
        var overrides = 0;
        try { overrides = File.ReadLines(Path.Combine(profile, "cert_override.txt")).Count(IsCertificateOverrideEntry); }
        catch { }
        return $"ключи={(File.Exists(Path.Combine(profile, "key4.db")) ? "есть" : "нет")}; " +
            $"логинов={logins}; сертификаты={(File.Exists(Path.Combine(profile, "cert9.db")) ? "есть" : "нет")}; исключений={overrides}";
    }

    private static bool IsCertificateOverrideEntry(string line) =>
        !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#');

    private static void RemovePreference(List<string> lines, string name)
    {
        var prefix = $"user_pref(\"{name}\",";
        lines.RemoveAll(line => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
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
        RemovePreference(lines, name);
        lines.Add($"user_pref(\"{name}\", {value});");
    }
}

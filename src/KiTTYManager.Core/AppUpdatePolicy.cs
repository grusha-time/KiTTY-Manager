using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace KiTTYManager.Core;

public static partial class AppUpdatePolicy
{
    private static readonly Regex VersionRegex = new(
        @"^[vV]?(?<major>\d+)(\.(?<minor>\d+))?(\.(?<build>\d+))?(\.(?<revision>\d+))?",
        RegexOptions.Compiled);

    public static bool TryParseVersion(string? raw, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var match = VersionRegex.Match(raw.Trim());
        if (!match.Success) return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
        var build = match.Groups["build"].Success ? int.Parse(match.Groups["build"].Value) : 0;
        var revision = match.Groups["revision"].Success ? int.Parse(match.Groups["revision"].Value) : -1;

        version = revision >= 0
            ? new Version(major, minor, build, revision)
            : new Version(major, minor, build);
        return true;
    }

    public static DateTime CalculateNextScheduledTime(DateTime nowLocal, bool lastCheckFailed)
    {
        if (lastCheckFailed)
            return nowLocal.AddHours(1);

        var today10Am = nowLocal.Date.AddHours(10);
        if (nowLocal < today10Am)
            return today10Am;
        return today10Am.AddDays(1);
    }

    public static bool IsCheckDue(DateTime nowLocal, DateTime scheduledLocal)
    {
        // If system clock shifted backwards by more than 2 days, consider overdue to recover
        if (nowLocal < scheduledLocal - TimeSpan.FromDays(2))
            return true;

        return nowLocal >= scheduledLocal;
    }

    public static bool IsPackageAsset(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return false;
        var name = assetName.Trim();
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;

        // Matches KiTTYManager-*-windows-x64.zip or KiTTYManager-*-win-x64.zip
        var isKittyManager = name.StartsWith("KiTTYManager", StringComparison.OrdinalIgnoreCase);
        var isWindows = name.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("windows-x64", StringComparison.OrdinalIgnoreCase);
        var isX64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);

        return isKittyManager && isWindows && isX64;
    }

    public static AppReleaseAsset SelectPackageAsset(IEnumerable<AppReleaseAsset> assets)
    {
        var candidates = assets.Where(a => IsPackageAsset(a.Name)).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("В релизе не найден подходящий ZIP-архив обновления KiTTY Manager для Windows x64.");
        if (candidates.Count > 1)
            throw new InvalidOperationException($"В релизе обнаружено несколько подходящих архивов обновления ({candidates.Count}). Обновление отменено для исключения неоднозначности.");

        return candidates[0];
    }
}

using System.Diagnostics.CodeAnalysis;

namespace KiTTYManager.Core;

public sealed record AppReleaseAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string ContentType);

public sealed record AppReleaseInfo(
    string TagName,
    string Name,
    string Body,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    AppReleaseAsset Asset,
    Version NormalizedVersion,
    bool IsPrerelease = false)
{
    public bool IsNewerThan(string currentVersionString)
    {
        if (AppUpdatePolicy.TryParseVersion(currentVersionString, out var currentVersion))
            return NormalizedVersion > currentVersion;
        return false;
    }

    public bool IsSameVersionAs(string currentVersionString)
    {
        if (AppUpdatePolicy.TryParseVersion(currentVersionString, out var currentVersion))
            return NormalizedVersion == currentVersion;
        return false;
    }

    public bool IsOlderThan(string currentVersionString)
    {
        if (AppUpdatePolicy.TryParseVersion(currentVersionString, out var currentVersion))
            return NormalizedVersion < currentVersion;
        return false;
    }
}

public sealed record UpdateValidationResult(
    bool IsValid,
    string? ErrorMessage = null,
    IReadOnlyList<string>? ManifestEntries = null);

public enum UpdateCheckStatus
{
    Idle,
    Checking,
    UpdateAvailable,
    UpToDate,
    Failed
}

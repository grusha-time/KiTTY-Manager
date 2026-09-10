namespace KiTTYManager.App;

/// <summary>
/// Decides whether the web-session startup path may delete a prepared Firefox
/// profile. Once the browser process and the profile are handed to the
/// after-exit cleanup, the startup path must not touch the directory: the
/// browser is already reading it, and deleting unlocked files (prefs.js,
/// user.js, cert_override.txt, logins.json) leaves a running browser without
/// its configuration.
/// </summary>
public static class WebSessionCleanupPolicy
{
    public static bool ShouldDeletePreparedProfile(
        bool handedOffToCleanup, bool profileAttached, bool preparationSucceeded) =>
        !handedOffToCleanup && (profileAttached || preparationSucceeded);
}

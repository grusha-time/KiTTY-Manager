using KiTTYManager.Core;

namespace KiTTYManager.App;

public static class SessionOpenDispatcher
{
    public static Task OpenAsync(
        ManagedServer server,
        ManagerConfig config,
        Func<ManagedServer, Task> startJumphostAsync,
        Func<ManagedServer, Task> openRoutedConsoleAsync)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(startJumphostAsync);
        ArgumentNullException.ThrowIfNull(openRoutedConsoleAsync);

        if (config.BaseProxies.Any(proxy => proxy.Enabled && proxy.StartupServerId == server.Id))
        {
            return startJumphostAsync(server);
        }

        return openRoutedConsoleAsync(server);
    }
}

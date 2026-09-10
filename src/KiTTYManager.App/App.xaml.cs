using System.Windows;
using Application = System.Windows.Application;

namespace KiTTYManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DarkWindowChrome.InitializeApplicationTheme();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ApplyWindowTheme));
        base.OnStartup(e);
    }

    private static void ApplyWindowTheme(object sender, RoutedEventArgs e) =>
        DarkWindowChrome.ApplyApplicationStyle((Window)sender);
}

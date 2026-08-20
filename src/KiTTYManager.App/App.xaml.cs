using System.Windows;
using Application = System.Windows.Application;

namespace KiTTYManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DarkWindowChrome.InitializeApplicationTheme();
        base.OnStartup(e);
    }
}

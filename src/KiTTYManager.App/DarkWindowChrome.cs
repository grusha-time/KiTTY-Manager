using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KiTTYManager.App;

public static class DarkWindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int CaptionColor = 35;
    private const int DarkCaption = 0x00101010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DarkWindowChrome),
        new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    public static void InitializeApplicationTheme()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
        try
        {
            _ = SetPreferredAppMode(2); // ForceDark, before the first HWND is created.
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        var color = DarkCaption;
        _ = DwmSetWindowAttribute(handle, CaptionColor, ref color, sizeof(int));
        _ = SetWindowTheme(handle, "DarkMode_Explorer", null);
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _ = DwmFlush();
    }

    private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Window window || args.NewValue is not true) return;
        window.SourceInitialized += ApplyOnSourceInitialized;
        window.Loaded += ApplyOnLoaded;
        window.ContentRendered += ApplyOnFirstRender;
        window.Activated += ApplyOnFirstActivation;
    }

    private static void ApplyOnSourceInitialized(object? sender, EventArgs args) => Apply((Window)sender!);
    private static void ApplyOnLoaded(object sender, RoutedEventArgs args) => Apply((Window)sender);
    private static void ApplyOnFirstRender(object? sender, EventArgs args)
    {
        var window = (Window)sender!;
        Apply(window);
        window.ContentRendered -= ApplyOnFirstRender;
    }
    private static void ApplyOnFirstActivation(object? sender, EventArgs args)
    {
        var window = (Window)sender!;
        Apply(window);
        window.Activated -= ApplyOnFirstActivation;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height,
        uint flags);
}

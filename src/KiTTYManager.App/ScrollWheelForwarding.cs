using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KiTTYManager.App;

public static class ScrollWheelForwarding
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollWheelForwarding),
        new PropertyMetadata(false, EnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)e.OldValue) viewer.PreviewMouseWheel -= PreviewMouseWheel;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += PreviewMouseWheel;
    }

    private static void PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || CanScroll(viewer, e.Delta)) return;
        var parent = FindParentScrollViewer(viewer);
        if (parent is null) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = viewer
        });
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight > 0 &&
        (delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight);

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }
}

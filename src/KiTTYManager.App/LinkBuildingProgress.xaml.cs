using System.Windows;
using System.ComponentModel;

namespace KiTTYManager.App;

public partial class LinkBuildingProgress : Window
{
    private bool allowClose;
    public LinkBuildingProgress() => InitializeComponent();
    public Action? CancelRequested { get; init; }

    public void UpdateStatus(string text, int current, int total)
    {
        StatusText.Text = text;
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = current;
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    public void CloseCompleted()
    {
        allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }
}

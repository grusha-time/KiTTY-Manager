using System.ComponentModel;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class ActiveTunnelsWindow : Window
{
    private readonly TunnelService tunnelService;
    private bool forceClose;

    public ActiveTunnelsWindow(TunnelService tunnelService)
    {
        InitializeComponent();
        this.tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));

        tunnelService.Changed += OnTunnelServiceChanged;
        Closing += Window_Closing;
        Closed += Window_Closed;

        RefreshList();
    }

    private void OnTunnelServiceChanged()
    {
        RefreshList();
    }

    private void RefreshList()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshList);
            return;
        }

        var list = tunnelService.GetTunnels();
        TunnelsGrid.ItemsSource = list;
        var runningCount = list.Count(t => t.IsRunning);
        TunnelCountText.Text = $"Туннелей: {list.Count} (работает: {runningCount})";
        StopSelectedButton.IsEnabled = list.Count > 0;
        StopAllButton.IsEnabled = list.Count > 0;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (forceClose) return;

        if (tunnelService.HasActiveTunnels())
        {
            e.Cancel = true;
            var choice = ThemedMessageDialog.ShowChoice(
                this,
                "Есть активные SSH-туннели. При закрытии окна все они будут остановлены.\n\nВы действительно хотите закрыть окно и остановить туннели?",
                "Закрытие окна туннелей",
                "Закрыть и остановить",
                "Не закрывать",
                MessageBoxImage.Warning);

            if (choice == ThemedDialogChoice.Primary)
            {
                forceClose = true;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await tunnelService.StopAllAsync();
                    Close();
                });
            }
        }
    }

    public void ForceClose()
    {
        forceClose = true;
        try { Close(); } catch { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        tunnelService.Changed -= OnTunnelServiceChanged;
    }

    private async void StopSelected_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = TunnelsGrid.SelectedItems
            .OfType<ActiveTunnelItem>()
            .Select(t => t.Id)
            .ToList();

        if (selectedIds.Count == 0) return;

        await tunnelService.StopTunnelsAsync(selectedIds);
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        await tunnelService.StopAllAsync();
    }

    private void CopyBind_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelsGrid.SelectedItem is ActiveTunnelItem item)
        {
            try
            {
                System.Windows.Clipboard.SetText(item.BindDisplay);
            }
            catch { }
        }
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class BatchTunnelEditorWindow : Window
{
    private readonly ObservableCollection<TunnelServerChoice> servers;
    public BatchTunnelDefinition Result { get; private set; }

    public BatchTunnelEditorWindow(IEnumerable<BatchServerChoice> availableServers, BatchTunnelDefinition? source = null)
    {
        InitializeComponent();
        Result = source is null ? new() { Name = "Новый туннель", Kind = BatchTunnelKind.Remote } : Clone(source);
        servers = new(availableServers.Where(x => x.Selected).Select(x => new TunnelServerChoice(x.Id, x.Name, Result.ServerIds.Contains(x.Id))));
        ServersList.ItemsSource = servers;
        NameBox.Text = Result.Name; KindBox.SelectedIndex = Result.Kind == BatchTunnelKind.Local ? 0 : 1;
        BindHostBox.Text = Result.BindHost; BindPortBox.Text = Result.BindPort == 0 ? "" : Result.BindPort.ToString();
        DestinationHostBox.Text = Result.DestinationHost; DestinationPortBox.Text = Result.DestinationPort == 0 ? "" : Result.DestinationPort.ToString();
        AllServersBox.IsChecked = Result.ServerIds.Count == 0;
        UpdateKindHelp(); UpdateServerState();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKindHelp();
    private void UpdateKindHelp()
    {
        if (KindHelp is null) return;
        var local = KindBox.SelectedIndex == 0;
        KindHelp.Text = local
            ? "Local: менеджер открывает порт на вашем Windows-ПК. Подключение к нему уходит через выбранный сервер к целевому хосту."
            : "Remote: SSH-сервер открывает указанный порт. Подключения к этому порту идут через SSH к целевому хосту со стороны вашего ПК.";
        if (BindHostLabel is not null)
        {
            BindHostLabel.ToolTip = local
                ? "Адрес на вашем ПК, на котором откроется порт. Обычно 127.0.0.1 — тогда порт виден только самому ПК."
                : "Адрес на сервере, на котором откроется порт. 127.0.0.1 — порт виден только на самом сервере (безопасный вариант).";
            BindPortLabel.ToolTip = "Номер порта, который откроется для подключений (1–65535).";
            DestinationHostLabel.ToolTip = local
                ? "Адрес, который виден со стороны сервера: куда сервер будет думать, что он подключается."
                : "Адрес, который доступен с вашего ПК: куда на самом деле попадёт трафик (например, внутренний веб-сервер).";
            DestinationPortLabel.ToolTip = "Порт назначения, на который попадёт трафик.";
            FieldsHelp.Text = local
                ? "Пример: открыть на ПК 127.0.0.1:9000 и вести трафик через сервер на db.internal:5432 — как ssh -L 9000:db.internal:5432."
                : "Пример: открыть на сервере 127.0.0.1:8443 и вести трафик со стороны ПК на example.com:443 — как ssh -R 8443:example.com:443.";
        }
        if (KindBox.SelectedIndex == 0) AllServersBox.IsChecked = false;
        AllServersBox.IsEnabled = KindBox.SelectedIndex != 0;
        UpdateServerState();
    }
    private void AllServersBox_Changed(object sender, RoutedEventArgs e) => UpdateServerState();
    private void UpdateServerState() { if (ServersList is not null) ServersList.IsEnabled = AllServersBox.IsChecked != true; }
    private void ServerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ServerSearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(ServersList.ItemsSource).Filter = item => item is TunnelServerChoice x &&
            (string.IsNullOrEmpty(query) || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
    private void ServersList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (AllServersBox.IsChecked == true ||
            ItemsControl.ContainerFromElement(ServersList, e.OriginalSource as DependencyObject) is not ListBoxItem row ||
            row.Content is not TunnelServerChoice selected) return;
        selected.Selected = !selected.Selected; ServersList.Items.Refresh();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (AllServersBox.IsChecked != true && !servers.Any(x => x.Selected))
        {
            ThemedMessageDialog.Show(this, "Выберите хотя бы один сервер или вариант для всех выбранных серверов.", "Проверьте туннель", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (KindBox.SelectedIndex == 0 && servers.Count(x => x.Selected) != 1)
        {
            ThemedMessageDialog.Show(this, "Local-туннель должен идти через один конкретный сервер. Выберите ровно один.", "Проверьте туннель", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        Result = new()
        {
            Name = NameBox.Text.Trim(), Kind = KindBox.SelectedIndex == 0 ? BatchTunnelKind.Local : BatchTunnelKind.Remote,
            BindHost = BindHostBox.Text.Trim(), BindPort = int.TryParse(BindPortBox.Text, out var bindPort) ? bindPort : 0,
            DestinationHost = DestinationHostBox.Text.Trim(), DestinationPort = int.TryParse(DestinationPortBox.Text, out var destinationPort) ? destinationPort : 0,
            ServerIds = AllServersBox.IsChecked == true ? [] : servers.Where(x => x.Selected).Select(x => x.Id).ToList()
        };
        try { BatchTaskFile.Validate(new() { Name = "Проверка", Tunnels = [Result] }); }
        catch (Exception ex) { ThemedMessageDialog.Show(this, ex.Message, "Проверьте туннель", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    private static BatchTunnelDefinition Clone(BatchTunnelDefinition x) => new()
    {
        Name = x.Name, Kind = x.Kind, BindHost = x.BindHost, BindPort = x.BindPort,
        DestinationHost = x.DestinationHost, DestinationPort = x.DestinationPort,
        ServerIds = x.ServerIds.ToList(), ServerDisplay = x.ServerDisplay
    };
}

public sealed class TunnelServerChoice(Guid id, string name, bool selected)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool Selected { get; set; } = selected;
}

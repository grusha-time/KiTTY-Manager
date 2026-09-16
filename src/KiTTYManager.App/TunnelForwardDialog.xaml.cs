using System.Windows;
using System.Windows.Controls;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class TunnelForwardDialog : Window
{
    private readonly ManagedServer server;

    public TunnelDefinition? Result { get; private set; }

    public TunnelForwardDialog(ManagedServer server)
    {
        InitializeComponent();
        this.server = server ?? throw new ArgumentNullException(nameof(server));

        ServerInfoText.Text = $"Сессия: {server.Name} [{server.Host}:{server.Port}]";
        KindBox.SelectedIndex = 0;
        UpdateKindHelp();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateKindHelp();
    }

    private void UpdateKindHelp()
    {
        if (KindBox is null || KindHelpText is null) return;

        var selected = KindBox.SelectedIndex;
        var isDynamic = selected == 2;
        var isRemote = selected == 1;

        if (DestinationHostLabel != null)
            DestinationHostLabel.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
        if (DestinationHostBox != null)
            DestinationHostBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
        if (DestinationPortLabel != null)
            DestinationPortLabel.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
        if (DestinationPortBox != null)
            DestinationPortBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;

        if (isDynamic)
        {
            BindHostLabel.Text = "Где открыть SOCKS5 (ПК)";
            BindPortLabel.Text = "Порт SOCKS5";
            if (string.IsNullOrWhiteSpace(BindPortBox.Text))
            {
                BindPortBox.Text = "1080";
            }
            KindHelpText.Text = "Dynamic (-D / SOCKS): менеджер открывает локальный SOCKS5-прокси на вашем компьютере. Программы (браузер, утилиты), использующие этот прокси, получают доступ ко всей сети сервера.";
        }
        else if (isRemote)
        {
            BindHostLabel.Text = "Где открыть порт (сервер)";
            BindPortLabel.Text = "Какой порт открыть";
            KindHelpText.Text = "Remote (-R): SSH-сервер открывает указанный порт на своей стороне. Подключения к нему перенаправляются через SSH к целевому адресу со стороны вашего ПК.";
        }
        else
        {
            BindHostLabel.Text = "Где открыть порт (ПК)";
            BindPortLabel.Text = "Какой порт открыть";
            KindHelpText.Text = "Local (-L): менеджер открывает порт на вашем ПК. Подключения к нему перенаправляются через SSH к целевому адресу со стороны сервера.";
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindBox.SelectedIndex switch
        {
            1 => TunnelKind.Remote,
            2 => TunnelKind.Dynamic,
            _ => TunnelKind.Local
        };

        var bindPort = int.TryParse(BindPortBox.Text.Trim(), out var bp) ? bp : 0;
        var destPort = int.TryParse(DestinationPortBox.Text.Trim(), out var dp) ? dp : 0;

        var definition = new TunnelDefinition
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Kind = kind,
            BindHost = string.IsNullOrWhiteSpace(BindHostBox.Text) ? "127.0.0.1" : BindHostBox.Text.Trim(),
            BindPort = bindPort,
            DestinationHost = kind == TunnelKind.Dynamic ? "" : DestinationHostBox.Text.Trim(),
            DestinationPort = kind == TunnelKind.Dynamic ? 0 : destPort
        };

        try
        {
            TunnelPolicy.Validate(definition);
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, ex.Message, "Проверьте параметры туннеля", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = definition;
        DialogResult = true;
        Close();
    }
}

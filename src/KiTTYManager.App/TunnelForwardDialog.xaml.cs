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
        if (KindBox is null || KindHelp is null) return;

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
            KindHelp.Text = "Dynamic: менеджер поднимает локальный SOCKS5-прокси на вашем Windows-ПК. Любые программы могут направлять трафик через этот прокси во всю внутреннюю сеть сервера.";
            if (BindHostLabel != null && BindHostBox != null)
            {
                BindHostLabel.Text = "Где открыть SOCKS5 (ПК)";
                BindHostLabel.ToolTip = BindHostBox.ToolTip = "Адрес на вашем ПК, на котором откроется SOCKS5-прокси. Обычно 127.0.0.1 — тогда прокси доступен только самому ПК.";
            }
            if (BindPortLabel != null && BindPortBox != null)
            {
                BindPortLabel.Text = "Порт SOCKS5";
                BindPortLabel.ToolTip = BindPortBox.ToolTip = "Номер локального SOCKS5-порта, который откроется для подключений (например, 1080).";
            }
            if (BindPortBox != null && string.IsNullOrWhiteSpace(BindPortBox.Text))
            {
                BindPortBox.Text = "1080";
            }
            if (FieldsHelp != null)
            {
                FieldsHelp.Text = "Пример: открыть на ПК 127.0.0.1:1080 как SOCKS5-прокси — как ssh -D 1080.";
            }
        }
        else if (isRemote)
        {
            KindHelp.Text = "Remote: SSH-сервер открывает указанный порт. Подключения к этому порту идут через SSH к целевому хосту со стороны вашего ПК.";
            if (BindHostLabel != null && BindHostBox != null)
            {
                BindHostLabel.Text = "Где открыть порт";
                BindHostLabel.ToolTip = BindHostBox.ToolTip = "Адрес на сервере, на котором откроется порт. 127.0.0.1 — порт виден только на самом сервере (безопасный вариант).";
            }
            if (BindPortLabel != null && BindPortBox != null)
            {
                BindPortLabel.Text = "Какой порт открыть";
                BindPortLabel.ToolTip = BindPortBox.ToolTip = "Номер порта, который откроется для подключений (1–65535).";
            }
            if (DestinationHostLabel != null && DestinationHostBox != null)
            {
                DestinationHostLabel.ToolTip = DestinationHostBox.ToolTip = "Адрес, который доступен с вашего ПК: куда на самом деле попадёт трафик (например, внутренний веб-сервер).";
            }
            if (DestinationPortLabel != null && DestinationPortBox != null)
            {
                DestinationPortLabel.ToolTip = DestinationPortBox.ToolTip = "Порт назначения, на который попадёт трафик.";
            }
            if (FieldsHelp != null)
            {
                FieldsHelp.Text = "Пример: открыть на сервере 127.0.0.1:8443 и вести трафик со стороны ПК на example.com:443 — как ssh -R 8443:example.com:443.";
            }
        }
        else
        {
            KindHelp.Text = "Local: менеджер открывает порт на вашем Windows-ПК. Подключение к нему уходит через выбранный сервер к целевому хосту.";
            if (BindHostLabel != null && BindHostBox != null)
            {
                BindHostLabel.Text = "Где открыть порт";
                BindHostLabel.ToolTip = BindHostBox.ToolTip = "Адрес на вашем ПК, на котором откроется порт. Обычно 127.0.0.1 — тогда порт виден только самому ПК.";
            }
            if (BindPortLabel != null && BindPortBox != null)
            {
                BindPortLabel.Text = "Какой порт открыть";
                BindPortLabel.ToolTip = BindPortBox.ToolTip = "Номер порта, который откроется для подключений (1–65535).";
            }
            if (DestinationHostLabel != null && DestinationHostBox != null)
            {
                DestinationHostLabel.ToolTip = DestinationHostBox.ToolTip = "Адрес, который виден со стороны сервера: куда сервер будет думать, что он подключается.";
            }
            if (DestinationPortLabel != null && DestinationPortBox != null)
            {
                DestinationPortLabel.ToolTip = DestinationPortBox.ToolTip = "Порт назначения, на который попадёт трафик.";
            }
            if (FieldsHelp != null)
            {
                FieldsHelp.Text = "Пример: открыть на ПК 127.0.0.1:9000 и вести трафик через сервер на db.internal:5432 — как ssh -L 9000:db.internal:5432.";
            }
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

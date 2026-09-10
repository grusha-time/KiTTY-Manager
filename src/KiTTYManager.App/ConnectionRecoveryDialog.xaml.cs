using System.Windows;

namespace KiTTYManager.App;

public partial class ConnectionRecoveryDialog : Window
{
    public int? AdditionalMinutes { get; private set; }
    public ConnectionRecoveryDialog(string serverName, string operation, int minutes)
    {
        InitializeComponent();
        MessageText.Text = $"Связь с «{serverName}» не восстановилась за {minutes} мин. Текущее действие «{operation}» не пропущено.";
        MinutesBox.Text = Math.Max(1, minutes).ToString();
        Loaded += (_, _) => { MinutesBox.Focus(); MinutesBox.SelectAll(); };
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { AdditionalMinutes = null; DialogResult = false; }
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text.Trim(), out var value) || value is < 0 or > 99999)
        {
            ThemedMessageDialog.Show(this, "Введите от 0 до 99999 минут.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            MinutesBox.Focus(); MinutesBox.SelectAll(); return;
        }
        AdditionalMinutes = value; DialogResult = true;
    }
}

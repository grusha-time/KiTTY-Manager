using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class AnsibleReadinessWindow : Window
{
    private readonly Func<CancellationToken, Task<AnsibleReadinessReport>> check;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ObservableCollection<Row> rows = [];

    public AnsibleReadinessWindow(Func<CancellationToken, Task<AnsibleReadinessReport>> check)
    {
        InitializeComponent();
        this.check = check;
        ResultsGrid.ItemsSource = rows;
        Loaded += RunAsync;
        Closed += (_, _) => cancellation.Dispose();
    }

    private async void RunAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= RunAsync;
        try
        {
            var report = await check(cancellation.Token);
            foreach (var item in report.Items)
                rows.Add(new(item.Success ? "Готово" : item.Unknown ? "Неизвестно" : "Нужно включить",
                    item.Stage, item.Message, item.EnableCommand ?? "—"));
            SummaryText.Text = report.State switch
            {
                AnsibleReadinessState.Ready => "Ansible готов к запуску.",
                AnsibleReadinessState.RequiresConfiguration => "Требуется настройка Windows. Команды показаны только для отключённых компонентов.",
                _ => "Часть системных состояний определить не удалось. Подробности показаны в таблице."
            };
            CopyButton.IsEnabled = rows.Count > 0;
        }
        catch (OperationCanceledException) { SummaryText.Text = "Проверка остановлена."; }
        catch { SummaryText.Text = "Проверка завершилась непредвиденной ошибкой."; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder(SummaryText.Text);
        foreach (var row in rows)
            text.AppendLine().Append(row.Status).Append(" | ").Append(row.Stage).Append(" | ")
                .Append(row.Message).Append(" | ").Append(row.EnableCommand);
        System.Windows.Clipboard.SetText(text.ToString());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        cancellation.Cancel();
        base.OnClosed(e);
    }

    private sealed record Row(string Status, string Stage, string Message, string EnableCommand);
}

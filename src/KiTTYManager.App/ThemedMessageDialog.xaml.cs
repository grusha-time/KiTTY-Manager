using System.Windows;
using MediaBrush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;

namespace KiTTYManager.App;

public enum ThemedDialogChoice
{
    Closed,
    Primary,
    Secondary
}

public partial class ThemedMessageDialog : Window
{
    private MessageBoxResult result = MessageBoxResult.None;

    private ThemedMessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfigureIcon(image);
        ConfigureButtons(buttons);
    }

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new ThemedMessageDialog(message, title, buttons, image);
        owner ??= WpfApplication.Current?.MainWindow;
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
        _ = dialog.ShowDialog();
        return dialog.result;
    }

    public static ThemedDialogChoice ShowChoice(Window? owner, string message, string title,
        string primaryText, string secondaryText, MessageBoxImage image = MessageBoxImage.Question)
    {
        var dialog = new ThemedMessageDialog(message, title, MessageBoxButton.OK, image);
        dialog.ButtonsPanel.Children.Clear();
        dialog.AddButton(secondaryText, MessageBoxResult.No, false, false);
        dialog.AddButton(primaryText, MessageBoxResult.Yes, true, true);
        owner ??= WpfApplication.Current?.MainWindow;
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
        _ = dialog.ShowDialog();
        return dialog.result switch
        {
            MessageBoxResult.Yes => ThemedDialogChoice.Primary,
            MessageBoxResult.No => ThemedDialogChoice.Secondary,
            _ => ThemedDialogChoice.Closed
        };
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        (IconText.Text, IconBadge.Background) = image switch
        {
            MessageBoxImage.Error => ("!", BrushFrom("#7F1D1D")),
            MessageBoxImage.Warning => ("!", BrushFrom("#713F12")),
            MessageBoxImage.Question => ("?", BrushFrom("#26395C")),
            MessageBoxImage.Information => ("i", BrushFrom("#26395C")),
            _ => ("i", BrushFrom("#26395C"))
        };
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("Понятно", MessageBoxResult.OK, true, true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Отмена", MessageBoxResult.Cancel, false, true);
                AddButton("Продолжить", MessageBoxResult.OK, true, false);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Нет", MessageBoxResult.No, false, true);
                AddButton("Да", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Отмена", MessageBoxResult.Cancel, false, true);
                AddButton("Нет", MessageBoxResult.No, false, false);
                AddButton("Да", MessageBoxResult.Yes, true, false);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult buttonResult, bool primary, bool cancel)
    {
        var button = new WpfButton
        {
            Content = text,
            MinWidth = 100,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = primary,
            IsCancel = cancel
        };
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        button.Click += (_, _) => { result = buttonResult; DialogResult = true; };
        ButtonsPanel.Children.Add(button);
    }

    private static MediaBrush BrushFrom(string value) => (MediaBrush)new BrushConverter().ConvertFromString(value)!;
}

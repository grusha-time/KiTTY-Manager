using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KiTTYManager.App;

public partial class SecretBox : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));

    private bool syncing;
    private string undoSnapshot = "";

    public SecretBox() => InitializeComponent();

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value ?? "");
    }

    /// <summary>Сбрасывает режим показа пароля в скрытый.</summary>
    public void ResetToHidden()
    {
        if (HiddenBox.Visibility != Visibility.Visible)
        {
            HiddenBox.Visibility = Visibility.Visible;
            VisibleBox.Visibility = Visibility.Collapsed;
            RevealButton.ToolTip = "Показать пароль";
            RevealButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Показать пароль");
        }
    }

    private static void ValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (SecretBox)dependencyObject;
        control.SyncBoxes((string?)e.NewValue ?? "");
    }

    private void SyncBoxes(string value)
    {
        if (syncing) return;
        syncing = true;
        // Не перезаписываем PasswordBox/TextBox если значение совпадает —
        // иначе курсор сбрасывается в 0 и символы вставляются задом наперёд.
        if (HiddenBox.Password != value) HiddenBox.Password = value;
        if (VisibleBox.Text != value) VisibleBox.Text = value;
        syncing = false;
    }

    private void HiddenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!syncing) Value = HiddenBox.Password;
    }

    private void VisibleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!syncing) Value = VisibleBox.Text;
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        var reveal = HiddenBox.Visibility == Visibility.Visible;
        HiddenBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        VisibleBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.ToolTip = reveal ? "Скрыть пароль" : "Показать пароль";
        RevealButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            reveal ? "Скрыть пароль" : "Показать пароль");
        if (reveal)
        {
            VisibleBox.Focus();
            VisibleBox.CaretIndex = VisibleBox.Text.Length;
        }
        else HiddenBox.Focus();
    }

    private void SecretBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => undoSnapshot = Value;

    private void SecretBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Z || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Value = undoSnapshot;
        e.Handled = true;
    }
}

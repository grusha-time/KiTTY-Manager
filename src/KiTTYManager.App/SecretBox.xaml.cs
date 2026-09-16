using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KiTTYManager.Core;

namespace KiTTYManager.App;

public partial class SecretBox : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(SecretBox),
        new FrameworkPropertyMetadata(false, OnIsReadOnlyChanged));

    private bool syncing;
    private string undoSnapshot = "";

    public SecretBox()
    {
        InitializeComponent();
        HiddenBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, Undo_Executed, Undo_CanExecute));
        HiddenBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, Copy_Executed, Copy_CanExecute));
        HiddenBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, Cut_Executed, Cut_CanExecute));
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value ?? "");
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    private static void OnIsReadOnlyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (SecretBox)dependencyObject;
        control.ApplyReadOnlyState((bool)e.NewValue);
    }

    private void ApplyReadOnlyState(bool isReadOnly)
    {
        HiddenBox.IsHitTestVisible = !isReadOnly;
        HiddenBox.Focusable = !isReadOnly;
        VisibleBox.IsReadOnly = isReadOnly;
        VisibleBox.IsHitTestVisible = !isReadOnly;
        VisibleBox.Focusable = !isReadOnly;
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
        if (!IsReadOnly)
        {
            if (reveal)
            {
                VisibleBox.Focus();
                VisibleBox.CaretIndex = VisibleBox.Text.Length;
            }
            else HiddenBox.Focus();
        }
    }

    private void SecretBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => undoSnapshot = Value;

    private void SecretBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Z || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || IsReadOnly) return;
        Value = undoSnapshot;
        e.Handled = true;
    }

    private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !IsReadOnly && Value != undoSnapshot;
        e.Handled = true;
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsReadOnly) return;
        Value = undoSnapshot;
        e.Handled = true;
    }

    private (int Start, int Length) GetPasswordBoxSelection()
    {
        try
        {
            var selectionProp = typeof(PasswordBox).GetProperty("Selection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var selection = selectionProp?.GetValue(HiddenBox);
            if (selection == null)
            {
                var editorField = typeof(PasswordBox).GetField("_textEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var editor = editorField?.GetValue(HiddenBox);
                if (editor != null)
                {
                    var editorSelProp = editor.GetType().GetProperty("Selection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    selection = editorSelProp?.GetValue(editor);
                }
            }

            if (selection != null)
            {
                return SecretBoxEditing.ExtractSelection(selection);
            }
        }
        catch { }
        return (0, 0);
    }

    private void Copy_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        var sel = GetPasswordBoxSelection();
        e.CanExecute = SecretBoxEditing.CanCopy(sel.Length);
        e.Handled = true;
    }

    private void Copy_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var sel = GetPasswordBoxSelection();
        var text = SecretBoxEditing.GetSelectedText(Value, sel.Start, sel.Length);
        if (!string.IsNullOrEmpty(text))
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch { }
        }
        e.Handled = true;
    }

    private void Cut_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        var sel = GetPasswordBoxSelection();
        e.CanExecute = SecretBoxEditing.CanCut(IsReadOnly, sel.Length);
        e.Handled = true;
    }

    private void Cut_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsReadOnly) return;
        var sel = GetPasswordBoxSelection();
        var text = SecretBoxEditing.GetSelectedText(Value, sel.Start, sel.Length);
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            Value = SecretBoxEditing.RemoveSelectedText(Value, sel.Start, sel.Length);
            var targetCaret = Math.Min(sel.Start, Value?.Length ?? 0);
            SecretBoxEditing.SetSelection(HiddenBox, targetCaret, 0);
        }
        catch { }
        e.Handled = true;
    }
}

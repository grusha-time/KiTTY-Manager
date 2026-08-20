using System.Windows;

namespace KiTTYManager.App;

public partial class PromptDialog : Window
{
    public string Value => InputBox.Text.Trim();
    public PromptDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent(); Title = title; PromptText.Text = prompt; InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }
    private void Save_Click(object sender, RoutedEventArgs e) { if (Value.Length > 0) DialogResult = true; }
}

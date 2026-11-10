using System.Windows;

namespace KiTTYManager.App;

public partial class SessionNoteWindow : Window
{
    public SessionNoteWindow(string serverName, string note)
    {
        InitializeComponent();
        ServerNameText.Text = $"Заметка о сервере «{serverName}»";
        NoteBox.Text = note;
        Loaded += (_, _) =>
        {
            NoteBox.Focus();
            NoteBox.CaretIndex = NoteBox.Text.Length;
        };
    }

    public string Note => NoteBox.Text;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

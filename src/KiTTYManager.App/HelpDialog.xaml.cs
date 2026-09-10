using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace KiTTYManager.App;

public partial class HelpDialog : Window
{
    public HelpDialog()
    {
        InitializeComponent();
        SectionsList.ItemsSource = HelpContent.Entries.Select(entry => entry.Section).Distinct()
            .Prepend(HelpContent.AllSections).ToArray();
        SectionsList.SelectedIndex = 0;
    }

    private void RefreshEntries()
    {
        if (EntriesList is null || SearchBox is null || SectionsList is null) return;
        var entries = HelpContent.Search(SearchBox.Text, SectionsList.SelectedItem as string);
        EntriesList.ItemsSource = entries;
        ResultCount.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? $"{SectionsList.SelectedItem} · материалов: {entries.Count}"
            : $"Найдено во всех разделах: {entries.Count}";
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ScrollToTop();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshEntries();
    private void Section_Changed(object sender, SelectionChangedEventArgs e) => RefreshEntries();
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }
}

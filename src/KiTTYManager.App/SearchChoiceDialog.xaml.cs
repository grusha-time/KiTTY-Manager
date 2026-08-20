using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KiTTYManager.App;

public partial class SearchChoiceDialog : Window
{
    private readonly List<object> choices;
    private readonly HashSet<object> selections = [];
    private bool refreshingFilter;
    public IReadOnlyList<object> Selections => choices.Where(selections.Contains).ToArray();
    public SearchChoiceDialog(string title, string prompt, IEnumerable<object> values)
    {
        SourceInitialized += (_, _) => DarkWindowChrome.Apply(this);
        InitializeComponent(); Title = title; PromptText.Text = prompt; choices = values.ToList(); ChoicesList.ItemsSource = choices;
        ChoicesList.SelectionChanged += ChoicesList_SelectionChanged;
        Loaded += (_, _) => SearchText.Focus();
    }
    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchText.Text.Trim(); var view = CollectionViewSource.GetDefaultView(ChoicesList.ItemsSource);
        refreshingFilter = true;
        view.Filter = value => text.Length == 0 || (value?.ToString()?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
        view.Refresh();
        foreach (var choice in choices.Where(choice => selections.Contains(choice) && view.Contains(choice)))
            ChoicesList.SelectedItems.Add(choice);
        refreshingFilter = false;
        UpdateSelectedCount();
    }
    private void ChoicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingFilter) return;
        foreach (var item in e.RemovedItems.Cast<object>()) selections.Remove(item);
        foreach (var item in e.AddedItems.Cast<object>()) selections.Add(item);
        UpdateSelectedCount();
    }
    private void UpdateSelectedCount() => SelectedCountText.Text = $"Выбрано: {selections.Count}";
    private void Select_Click(object sender, RoutedEventArgs e) { if (selections.Count > 0) DialogResult = true; }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => ChoicesList.SelectAll();
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        selections.Clear();
        ChoicesList.UnselectAll();
        UpdateSelectedCount();
    }
}

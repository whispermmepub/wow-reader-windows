using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WowReader.Windows.Services;

namespace WowReader.Windows;

public sealed class VocabularyWindow : Window
{
    private readonly VocabularyStore _store;
    private readonly ListBox _words;
    private readonly TextBlock _source;
    private readonly TextBox _definition;
    private readonly Button _learned;
    private readonly Button _lookup;
    private readonly Button _remove;

    public VocabularyWindow(VocabularyStore store, string? selectedWord = null)
    {
        _store = store;
        Title = "Vocabulary · saved on this PC";
        Owner = Application.Current?.MainWindow;
        Width = 700;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "Your vocabulary", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(32, 52, 45)) });
        header.Children.Add(new TextBlock { Text = "Captured words and meanings are stored locally. Online lookup happens only when you press the Wiktionary button.", Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(101, 114, 107)), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _lookup = new Button { Content = "Open Wiktionary…", IsEnabled = false, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        _learned = new Button { Content = "Mark learned", IsEnabled = false, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        _remove = new Button { Content = "Delete", IsEnabled = false, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        var review = new Button { Content = "Review next card", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        var save = new Button { Content = "Save meaning", Padding = new Thickness(12, 7, 12, 7), Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        actions.Children.Add(save);
        actions.Children.Add(_lookup);
        actions.Children.Add(_learned);
        actions.Children.Add(_remove);
        actions.Children.Add(review);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _words = new ListBox { DisplayMemberPath = nameof(VocabularyEntry.Word), Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(_words, 0);
        body.Children.Add(_words);
        var detail = new StackPanel();
        Grid.SetColumn(detail, 2);
        detail.Children.Add(new TextBlock { Text = "Meaning or translation (optional)", Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 91)) });
        _source = new TextBlock { Margin = new Thickness(0, 5, 0, 8), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 126)), TextWrapping = TextWrapping.Wrap };
        detail.Children.Add(_source);
        _definition = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 230, Padding = new Thickness(8) };
        detail.Children.Add(_definition);
        detail.Children.Add(new TextBlock { Text = "You write definitions yourself; no dictionary database is bundled.", Margin = new Thickness(0, 8, 0, 0), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 126)), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(detail);
        root.Children.Add(body);
        Content = root;

        _words.SelectionChanged += (_, _) => ShowSelection();
        save.Click += (_, _) =>
        {
            if (_words.SelectedItem is not VocabularyEntry item) return;
            _store.SetDefinition(item.Word, _definition.Text);
            Refresh(item.Word);
        };
        _lookup.Click += (_, _) => OpenLookup();
        _learned.Click += (_, _) =>
        {
            if (_words.SelectedItem is not VocabularyEntry item) return;
            _store.SetLearned(item.Word, !item.Learned);
            Refresh(item.Word);
        };
        _remove.Click += (_, _) =>
        {
            if (_words.SelectedItem is not VocabularyEntry item) return;
            _store.Remove(item.Word);
            Refresh(null);
        };
        review.Click += (_, _) => ShowNextReviewCard();
        Refresh(selectedWord);
    }

    private void Refresh(string? preferredWord)
    {
        _words.ItemsSource = null;
        _words.ItemsSource = _store.All;
        if (preferredWord is not null)
            _words.SelectedItem = _store.All.FirstOrDefault(entry => string.Equals(entry.Word, preferredWord, StringComparison.OrdinalIgnoreCase));
        if (_words.SelectedItem is null && _words.Items.Count > 0) _words.SelectedIndex = 0;
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (_words.SelectedItem is not VocabularyEntry item)
        {
            _source.Text = "Select a captured word to add a local meaning.";
            _definition.Text = "";
            _lookup.IsEnabled = _learned.IsEnabled = _remove.IsEnabled = false;
            return;
        }
        _source.Text = $"Captured {item.FirstSeenAt.ToLocalTime():d} · last seen {item.LastSeenAt.ToLocalTime():g} · {item.LookupCount} capture(s) · {item.Learned switch { true => "Learned", false => "Needs review" }}" + (item.SourceBook.Length > 0 ? $" · from {item.SourceBook}" : "");
        _definition.Text = item.Definition;
        _lookup.IsEnabled = _learned.IsEnabled = _remove.IsEnabled = true;
        _learned.Content = item.Learned ? "Mark for review" : "Mark learned";
    }

    private void OpenLookup()
    {
        if (_words.SelectedItem is not VocabularyEntry item) return;
        try
        {
            var uri = "https://en.wiktionary.org/wiki/" + Uri.EscapeDataString(item.Word);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "The dictionary page could not be opened in the default browser.", "Wiktionary lookup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowNextReviewCard()
    {
        var entry = _store.All.FirstOrDefault(word => !word.Learned && !string.IsNullOrWhiteSpace(word.Definition));
        if (entry is null)
        {
            MessageBox.Show(this, "Add a meaning to an unlearned word before reviewing flashcards.", "Vocabulary review", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var card = new Window { Title = "Vocabulary review", Owner = this, Width = 440, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, ResizeMode = ResizeMode.NoResize };
        var content = new StackPanel { Margin = new Thickness(22), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = entry.Word, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(32, 52, 45)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var answer = new TextBlock { Text = entry.Definition, Visibility = Visibility.Collapsed, FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 91)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) };
        content.Children.Add(answer);
        var reveal = new Button { Content = "Reveal meaning", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
        reveal.Click += (_, _) => { answer.Visibility = Visibility.Visible; reveal.IsEnabled = false; };
        var learned = new Button { Content = "Mark learned", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 8, 12, 8), Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)), Foreground = Brushes.White };
        learned.Click += (_, _) => { _store.SetLearned(entry.Word, true); Refresh(entry.Word); card.Close(); };
        content.Children.Add(reveal);
        content.Children.Add(learned);
        card.Content = content;
        card.ShowDialog();
    }
}

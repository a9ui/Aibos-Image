using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private string _searchTermChipSignature = "";

    private static List<string> ParseActiveSearchTerms(string? query)
    {
        string normalized = SearchHistoryStore.NormalizeQuery(query ?? "");
        return normalized.Length == 0
            ? []
            : normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static term => term.Trim())
                .Where(static term => term.Length > 0)
                .ToList();
    }

    private void SyncSearchTermChips(string? query)
    {
        if (SearchTermChipsPanel is null || SearchTermChipsScroller is null)
            return;

        List<string> terms = ParseActiveSearchTerms(query);
        string signature = string.Join('\u001F', terms);
        if (string.Equals(signature, _searchTermChipSignature, StringComparison.Ordinal))
        {
            SearchTermChipsScroller.Visibility = terms.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        _searchTermChipSignature = signature;
        SearchTermChipsPanel.Children.Clear();
        for (int index = 0; index < terms.Count; index++)
            SearchTermChipsPanel.Children.Add(CreateSearchTermChip(index, terms[index]));

        SearchTermChipsScroller.Visibility = terms.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private Button CreateSearchTermChip(int index, string term)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = term,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "\u00D7",
            Margin = new Thickness(5, 0, 0, 0),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var chip = new Button
        {
            Content = content,
            Tag = new SearchTermChipReference(index, term),
            Style = (Style)FindResource("GhostButton"),
            Height = 20,
            MinWidth = 30,
            MaxWidth = 190,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 5, 2),
            FontSize = 11,
            ToolTip = $"Remove {term} from search",
        };
        AutomationProperties.SetName(chip, $"Remove search term {term}");
        AutomationProperties.SetHelpText(chip, "Remove this whole comma-separated term from the current search.");
        chip.Click += SearchTermChip_Click;
        return chip;
    }

    private void SearchTermChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SearchTermChipReference reference })
            return;

        RemoveSearchTerm(reference);
        e.Handled = true;
    }

    private bool RemoveSearchTerm(SearchTermChipReference reference, bool persist = true)
    {
        List<string> terms = ParseActiveSearchTerms(SearchInput.Text);
        int index = reference.Index;
        if (index < 0
            || index >= terms.Count
            || !string.Equals(terms[index], reference.Term, StringComparison.Ordinal))
        {
            index = terms.FindIndex(term =>
                string.Equals(term, reference.Term, StringComparison.Ordinal));
        }
        if (index < 0)
            return false;

        terms.RemoveAt(index);
        SetSearchQuery(string.Join(", ", terms), persist);
        CloseSearchHistoryAndFocusInput();
        return true;
    }

    public List<string> ActiveSearchTermsForSmoke
        => ParseActiveSearchTerms(SearchInput.Text);

    public bool ActiveSearchTermsAccessibilityReadyForSmoke
        => SearchTermChipsPanel.Children.OfType<Button>().All(chip =>
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(chip))
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(chip)));

    public bool RemoveSearchTermForSmoke(string term, bool persist = false)
    {
        Button? chip = SearchTermChipsPanel.Children
            .OfType<Button>()
            .FirstOrDefault(candidate =>
                candidate.Tag is SearchTermChipReference reference
                && string.Equals(reference.Term, term, StringComparison.Ordinal));
        return chip?.Tag is SearchTermChipReference reference
            && RemoveSearchTerm(reference, persist);
    }

    private sealed record SearchTermChipReference(int Index, string Term);
}

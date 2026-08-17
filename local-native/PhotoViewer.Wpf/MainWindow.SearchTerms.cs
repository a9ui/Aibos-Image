using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

internal sealed record SearchSuggestionItemView(
    string Tag,
    int MatchCount,
    Brush Background,
    Brush Foreground)
{
    public string UsageText => MatchCount > 0 ? $"{MatchCount:N0}×" : "↺";
}

public partial class MainWindow
{
    private const int MaxVisibleSearchTermChips = 64;
    private const int MaxSearchSuggestionCount = 10;
    private const int MaxIndexedSearchSuggestionCount = 50_000;
    private const int MaxSearchSuggestionTagCharacters = 160;
    private const long MaxSearchSuggestionPromptBytes = 128L * 1024 * 1024;
    private readonly ObservableCollection<SearchSuggestionItemView> _searchSuggestionEntries = new();
    private readonly List<string> _activeSearchTerms = [];
    private IReadOnlyList<SearchSuggestionCandidate> _searchSuggestionIndex = [];
    private CancellationTokenSource? _searchSuggestionIndexCts;
    private Task _searchSuggestionIndexBuildTask = Task.CompletedTask;
    private long _searchSuggestionCatalogRevision = -1;
    private long _searchSuggestionBuildRevision = -1;
    private string _searchTermChipSignature = "";
    private bool _normalizingSearchDraft;

    private static readonly SearchChipPaletteEntry[] SearchChipPalette =
    [
        CreateSearchChipPaletteEntry("#253C66", "#EAF2FF"),
        CreateSearchChipPaletteEntry("#3A285F", "#F4EEFF"),
        CreateSearchChipPaletteEntry("#49331D", "#FFF2DB"),
        CreateSearchChipPaletteEntry("#1F4A42", "#E8FFF9"),
        CreateSearchChipPaletteEntry("#4A2635", "#FFF0F5"),
        CreateSearchChipPaletteEntry("#2F3C25", "#F1FFE9"),
    ];

    private string CurrentSearchQuery
    {
        get
        {
            string committed = string.Join(", ", _activeSearchTerms);
            string draft = SearchInput?.Text ?? "";
            return SearchHistoryStore.NormalizeQuery(
                committed.Length == 0
                    ? draft
                    : string.IsNullOrWhiteSpace(draft)
                        ? committed
                        : $"{committed}, {draft}");
        }
    }

    private bool HasSearchComposerContent
        => _activeSearchTerms.Count > 0 || !string.IsNullOrEmpty(SearchInput?.Text);

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

    private void SetSearchComposerQuery(string? query)
    {
        _activeSearchTerms.Clear();
        _activeSearchTerms.AddRange(ParseActiveSearchTerms(query));
        _normalizingSearchDraft = true;
        try
        {
            if (!string.IsNullOrEmpty(SearchInput.Text))
                SearchInput.Clear();
        }
        finally
        {
            _normalizingSearchDraft = false;
        }
        _searchTermChipSignature = "";
        SyncSearchTermChips();
        RefreshSearchSuggestionSurface();
        UpdateSearchComposerPresentation();
    }

    private bool NormalizeCompletedSearchDraftTerms()
    {
        if (_normalizingSearchDraft)
            return false;

        string draft = SearchInput.Text;
        int lastComma = draft.LastIndexOf(',');
        if (lastComma < 0)
            return false;

        string completed = draft[..lastComma];
        string remainder = draft[(lastComma + 1)..];
        foreach (string term in ParseActiveSearchTerms(completed))
            _ = AddSearchTerm(term);

        _normalizingSearchDraft = true;
        try
        {
            SearchInput.Text = remainder.TrimStart();
            SearchInput.CaretIndex = SearchInput.Text.Length;
        }
        finally
        {
            _normalizingSearchDraft = false;
        }
        SyncSearchTermChips();
        return true;
    }

    private bool AddSearchTerm(string? rawTerm)
    {
        string normalized = SearchHistoryStore.NormalizeQuery(rawTerm ?? "");
        if (normalized.Length == 0
            || normalized.Length > SearchHistoryStore.MaxQueryLength
            || normalized.Contains(','))
        {
            return false;
        }

        string key = SearchHistoryStore.ComparisonKey(normalized);
        if (_activeSearchTerms.Any(term => string.Equals(
                SearchHistoryStore.ComparisonKey(term),
                key,
                StringComparison.Ordinal)))
        {
            return false;
        }

        _activeSearchTerms.Add(normalized);
        return true;
    }

    private bool CommitSearchDraftAsTerm(string? selectedTag = null)
    {
        string source = selectedTag ?? SearchInput.Text;
        List<string> terms = ParseActiveSearchTerms(source);
        bool changed = false;
        foreach (string term in terms)
            changed |= AddSearchTerm(term);

        bool hadDraft = SearchInput.Text.Length > 0;
        if (hadDraft)
        {
            _normalizingSearchDraft = true;
            try
            {
                SearchInput.Clear();
            }
            finally
            {
                _normalizingSearchDraft = false;
            }
        }

        SyncSearchTermChips();
        RefreshSearchSuggestionSurface();
        UpdateSearchComposerPresentation();
        if (changed && !hadDraft && !_initializing && !_settingSearchQuery)
            NotifySearchComposerChanged();
        return changed;
    }

    private bool RemoveLastCommittedSearchTerm()
    {
        if (SearchInput.Text.Length > 0 || _activeSearchTerms.Count == 0)
            return false;

        _activeSearchTerms.RemoveAt(_activeSearchTerms.Count - 1);
        SyncSearchTermChips();
        RefreshSearchSuggestionSurface();
        UpdateSearchComposerPresentation();
        if (!_initializing && !_settingSearchQuery)
            NotifySearchComposerChanged();
        return true;
    }

    private void NotifySearchComposerChanged()
    {
        ScheduleSearchFilter();
        ScheduleSearchStateSave();
    }

    private void UpdateSearchComposerPresentation()
    {
        if (SearchWatermark is not null)
            SearchWatermark.Visibility = HasSearchComposerContent ? Visibility.Collapsed : Visibility.Visible;
        if (ClearSearchInputButton is not null)
            ClearSearchInputButton.Visibility = HasSearchComposerContent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncSearchTermChips()
    {
        if (SearchTermChipsPanel is null || SearchTermChipsScroller is null)
            return;

        string signature = string.Join('\u001F', _activeSearchTerms);
        if (string.Equals(signature, _searchTermChipSignature, StringComparison.Ordinal))
        {
            SearchTermChipsScroller.Visibility = _activeSearchTerms.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        _searchTermChipSignature = signature;
        SearchTermChipsPanel.Children.Clear();
        int visibleCount = Math.Min(_activeSearchTerms.Count, MaxVisibleSearchTermChips);
        for (int index = 0; index < visibleCount; index++)
            SearchTermChipsPanel.Children.Add(CreateSearchTermChip(index, _activeSearchTerms[index]));
        if (_activeSearchTerms.Count > visibleCount)
        {
            SearchTermChipsPanel.Children.Add(new TextBlock
            {
                Text = $"+{_activeSearchTerms.Count - visibleCount:N0}",
                Foreground = (Brush)FindResource("TextTertiary"),
                Margin = new Thickness(2, 2, 6, 0),
                FontSize = 10.5,
                ToolTip = "Additional search terms are active but hidden from the bounded chip strip.",
            });
        }

        SearchTermChipsScroller.Visibility = _activeSearchTerms.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private Button CreateSearchTermChip(int index, string term)
    {
        SearchChipPaletteEntry colors = SearchChipColors(term);
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
            Style = (Style)FindResource("TransparentButton"),
            Background = colors.Background,
            Foreground = colors.Foreground,
            BorderBrush = colors.Foreground,
            BorderThickness = new Thickness(1),
            Height = 20,
            MinWidth = 30,
            MaxWidth = 190,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 5, 2),
            FontSize = 11,
            ToolTip = $"Remove {term} from search",
        };
        AutomationProperties.SetName(chip, $"Remove search term {term}");
        AutomationProperties.SetHelpText(chip, "Remove this whole search term from the current search.");
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
        int index = reference.Index;
        if (index < 0
            || index >= _activeSearchTerms.Count
            || !string.Equals(_activeSearchTerms[index], reference.Term, StringComparison.Ordinal))
        {
            index = _activeSearchTerms.FindIndex(term =>
                string.Equals(term, reference.Term, StringComparison.Ordinal));
        }
        if (index < 0)
            return false;

        bool previous = _suppressStateSave;
        _suppressStateSave = !persist;
        try
        {
            _activeSearchTerms.RemoveAt(index);
            SyncSearchTermChips();
            RefreshSearchSuggestionSurface();
            UpdateSearchComposerPresentation();
            if (!_initializing && !_settingSearchQuery)
            {
                ScheduleSearchFilter();
                if (persist)
                    ScheduleSearchStateSave();
            }
        }
        finally
        {
            _suppressStateSave = previous;
        }
        CloseSearchHistoryAndFocusInput();
        return true;
    }

    private Task EnsureSearchSuggestionIndexAsync()
    {
        long revision = _catalogContentRevision;
        if (_searchSuggestionCatalogRevision == revision)
            return Task.CompletedTask;
        if (_searchSuggestionBuildRevision == revision && !_searchSuggestionIndexBuildTask.IsCompleted)
            return _searchSuggestionIndexBuildTask;

        CancelSearchSuggestionIndexBuild();
        _searchSuggestionBuildRevision = revision;
        var cts = new CancellationTokenSource();
        _searchSuggestionIndexCts = cts;
        byte[][] prompts = _allTiles
            .Select(static tile => tile.PromptUtf8)
            .Where(static prompt => prompt.Length > 0)
            .ToArray();
        _searchSuggestionIndexBuildTask = BuildAndPublishSearchSuggestionIndexAsync(
            prompts,
            revision,
            cts);
        return _searchSuggestionIndexBuildTask;
    }

    private async Task BuildAndPublishSearchSuggestionIndexAsync(
        byte[][] prompts,
        long revision,
        CancellationTokenSource cts)
    {
        try
        {
            IReadOnlyList<SearchSuggestionCandidate> candidates = await Task.Run(
                () => BuildSearchSuggestionIndex(prompts, cts.Token),
                cts.Token);
            if (cts.IsCancellationRequested
                || revision != _catalogContentRevision
                || !ReferenceEquals(_searchSuggestionIndexCts, cts))
            {
                return;
            }

            _searchSuggestionIndex = candidates;
            _searchSuggestionCatalogRevision = revision;
            RefreshSearchSuggestionSurface();
        }
        catch (OperationCanceledException)
        {
            // A newer catalog or a closing window owns the derived in-memory index.
        }
        finally
        {
            if (ReferenceEquals(_searchSuggestionIndexCts, cts))
                _searchSuggestionIndexCts = null;
            cts.Dispose();
        }
    }

    private static IReadOnlyList<SearchSuggestionCandidate> BuildSearchSuggestionIndex(
        IReadOnlyList<byte[]> prompts,
        CancellationToken token)
    {
        var byKey = new Dictionary<string, SearchSuggestionAccumulator>(StringComparer.Ordinal);
        long inspectedBytes = 0;
        for (int index = 0; index < prompts.Count; index++)
        {
            if ((index & 63) == 0)
                token.ThrowIfCancellationRequested();
            byte[] promptBytes = prompts[index];
            if (promptBytes.Length == 0)
                continue;
            if (inspectedBytes + promptBytes.Length > MaxSearchSuggestionPromptBytes)
                break;
            inspectedBytes += promptBytes.Length;

            string prompt;
            try
            {
                prompt = MetadataIndexStore.DecodePrompt(promptBytes);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            foreach (string tag in ParsePromptTags(prompt))
            {
                if (tag.Length == 0 || tag.Length > MaxSearchSuggestionTagCharacters)
                    continue;
                string key = SearchHistoryStore.ComparisonKey(tag);
                if (key.Length == 0)
                    continue;
                if (byKey.TryGetValue(key, out SearchSuggestionAccumulator? existing))
                {
                    existing.MatchCount++;
                    continue;
                }
                if (byKey.Count >= MaxIndexedSearchSuggestionCount)
                    continue;
                byKey.Add(key, new SearchSuggestionAccumulator(tag, 1));
            }
        }

        return byKey.Values
            .Select(static candidate => new SearchSuggestionCandidate(candidate.Tag, candidate.MatchCount))
            .OrderByDescending(static candidate => candidate.MatchCount)
            .ThenBy(static candidate => candidate.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void InvalidateSearchSuggestionIndex()
    {
        _searchSuggestionCatalogRevision = -1;
        CancelSearchSuggestionIndexBuild();
        if (SearchInput?.IsKeyboardFocusWithin == true)
            _ = EnsureSearchSuggestionIndexAsync();
    }

    private void CancelSearchSuggestionIndexBuild()
    {
        CancellationTokenSource? cts = _searchSuggestionIndexCts;
        _searchSuggestionIndexCts = null;
        _searchSuggestionBuildRevision = -1;
        cts?.Cancel();
    }

    private void RefreshSearchSuggestionSurface()
    {
        if (SearchSuggestionList is null || SearchSuggestionSection is null)
            return;

        string draft = SearchHistoryStore.NormalizeQuery(SearchInput?.Text ?? "");
        if (draft.Length == 0 || draft.Contains(',') || draft.Length > MaxSearchSuggestionTagCharacters)
        {
            _searchSuggestionEntries.Clear();
            SearchSuggestionList.SelectedIndex = -1;
            SearchSuggestionSection.Visibility = Visibility.Collapsed;
            return;
        }

        string prefixKey = SearchHistoryStore.ComparisonKey(draft);
        var matches = new Dictionary<string, SearchSuggestionMatch>(StringComparer.Ordinal);
        foreach (SearchHistoryItemView history in _searchHistoryEntries)
        {
            foreach (string term in ParseActiveSearchTerms(history.Query))
                AddSearchSuggestionMatch(matches, term, 0, recent: true, prefixKey);
        }
        foreach (SearchSuggestionCandidate candidate in _searchSuggestionIndex)
            AddSearchSuggestionMatch(matches, candidate.Tag, candidate.MatchCount, recent: false, prefixKey);

        List<SearchSuggestionMatch> ranked = matches.Values
            .OrderByDescending(match => string.Equals(
                SearchHistoryStore.ComparisonKey(match.Tag),
                prefixKey,
                StringComparison.Ordinal))
            .ThenByDescending(static match => match.Recent)
            .ThenByDescending(static match => match.MatchCount)
            .ThenBy(static match => match.Tag.Length)
            .ThenBy(static match => match.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchSuggestionCount)
            .ToList();

        _searchSuggestionEntries.Clear();
        foreach (SearchSuggestionMatch match in ranked)
        {
            SearchChipPaletteEntry colors = SearchChipColors(match.Tag);
            _searchSuggestionEntries.Add(new SearchSuggestionItemView(
                match.Tag,
                match.MatchCount,
                colors.Background,
                colors.Foreground));
        }
        SearchSuggestionList.SelectedIndex = _searchSuggestionEntries.Count > 0 ? 0 : -1;
        SearchSuggestionSection.Visibility = _searchSuggestionEntries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AddSearchSuggestionMatch(
        Dictionary<string, SearchSuggestionMatch> matches,
        string tag,
        int matchCount,
        bool recent,
        string prefixKey)
    {
        string key = SearchHistoryStore.ComparisonKey(tag);
        if (!key.StartsWith(prefixKey, StringComparison.Ordinal)
            || _activeSearchTerms.Any(active => string.Equals(
                SearchHistoryStore.ComparisonKey(active),
                key,
                StringComparison.Ordinal)))
        {
            return;
        }

        if (matches.TryGetValue(key, out SearchSuggestionMatch? existing))
        {
            matches[key] = existing with
            {
                MatchCount = Math.Max(existing.MatchCount, matchCount),
                Recent = existing.Recent || recent,
            };
        }
        else
        {
            matches.Add(key, new SearchSuggestionMatch(tag, matchCount, recent));
        }
    }

    private bool FocusSearchSuggestionIndex(int index)
    {
        if (index < 0 || index >= _searchSuggestionEntries.Count)
            return false;
        SearchSuggestionList.SelectedIndex = index;
        SearchSuggestionList.UpdateLayout();
        bool focused = SearchSuggestionList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item
            && item.Focus();
        SearchHistoryAnnouncementText.Text =
            $"Search suggestion {index + 1} of {_searchSuggestionEntries.Count} selected.";
        return focused;
    }

    private bool CommitSearchSuggestion(SearchSuggestionItemView suggestion)
    {
        bool committed = CommitSearchDraftAsTerm(suggestion.Tag);
        CloseSearchSuggestionAndFocusInput(keepPopupOpen: true);
        return committed;
    }

    private void SearchSuggestionSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchSuggestionItemView suggestion })
            _ = CommitSearchSuggestion(suggestion);
        e.Handled = true;
    }

    private void SearchSuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearchHistoryAndFocusInput();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && SearchSuggestionList.SelectedItem is SearchSuggestionItemView suggestion)
        {
            e.Handled = true;
            _ = CommitSearchSuggestion(suggestion);
        }
    }

    private void SearchSuggestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = SearchSuggestionList.SelectedIndex;
        if (index >= 0)
        {
            SearchHistoryAnnouncementText.Text =
                $"Search suggestion {index + 1} of {_searchSuggestionEntries.Count} selected.";
        }
    }

    private void CloseSearchSuggestionAndFocusInput(bool keepPopupOpen)
    {
        _suppressSearchHistoryFocusOpen = true;
        SearchInput.Focus();
        SearchInput.CaretIndex = SearchInput.Text.Length;
        if (keepPopupOpen)
            SearchHistoryPopup.IsOpen = true;
        Dispatcher.BeginInvoke(
            () => _suppressSearchHistoryFocusOpen = false,
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static SearchChipPaletteEntry SearchChipColors(string term)
    {
        uint hash = 2166136261;
        foreach (char value in SearchHistoryStore.ComparisonKey(term))
        {
            hash ^= value;
            hash *= 16777619;
        }
        return SearchChipPalette[(int)(hash % (uint)SearchChipPalette.Length)];
    }

    private static SearchChipPaletteEntry CreateSearchChipPaletteEntry(string background, string foreground)
    {
        var backgroundBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        var foregroundBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        backgroundBrush.Freeze();
        foregroundBrush.Freeze();
        return new SearchChipPaletteEntry(backgroundBrush, foregroundBrush);
    }

    public List<string> ActiveSearchTermsForSmoke => [.. _activeSearchTerms];

    public bool ActiveSearchTermsAccessibilityReadyForSmoke
        => SearchTermChipsPanel.Children.OfType<Button>().All(chip =>
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(chip))
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(chip)));

    public bool ActiveSearchTermColorsReadyForSmoke
        => SearchChipPalette.Select(static entry => entry.Background.ToString()).Distinct(StringComparer.Ordinal).Count() >= 4
            && SearchTermChipsPanel.Children.OfType<Button>().All(chip =>
                chip.Background is SolidColorBrush && chip.Foreground is SolidColorBrush);

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

    public async Task RebuildSearchSuggestionsForSmokeAsync()
    {
        _searchSuggestionCatalogRevision = -1;
        await EnsureSearchSuggestionIndexAsync();
    }

    public void SetSearchDraftForSmoke(string draft)
    {
        SearchInput.Text = draft;
        SearchInput.CaretIndex = SearchInput.Text.Length;
        RefreshSearchSuggestionSurface();
    }

    public List<string> SearchSuggestionTagsForSmoke
        => _searchSuggestionEntries.Select(static item => item.Tag).ToList();

    public bool SearchSuggestionPopupVisibleForSmoke
        => SearchSuggestionSection.Visibility == Visibility.Visible;

    public bool SearchPopupOutsideDismissContractForSmoke
        => !SearchHistoryPopup.StaysOpen;

    public bool CommitTopSearchSuggestionWithKeyForSmoke(Key key)
    {
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source is null)
            return false;
        SearchInput.Focus();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        SearchInput.RaiseEvent(args);
        return args.Handled;
    }

    public bool ClickSearchSuggestionForSmoke(string tag)
    {
        SearchSuggestionItemView? suggestion = _searchSuggestionEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.Tag, tag, StringComparison.OrdinalIgnoreCase));
        return suggestion is not null && CommitSearchDraftAsTerm(suggestion.Tag);
    }

    public bool RemoveLastSearchTermWithBackspaceForSmoke()
    {
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source is null)
            return false;
        SearchInput.Focus();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Back)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        SearchInput.RaiseEvent(args);
        return args.Handled;
    }

    private sealed record SearchTermChipReference(int Index, string Term);
    private sealed record SearchSuggestionCandidate(string Tag, int MatchCount);
    private sealed class SearchSuggestionAccumulator(string tag, int matchCount)
    {
        public string Tag { get; } = tag;
        public int MatchCount { get; set; } = matchCount;
    }
    private sealed record SearchSuggestionMatch(string Tag, int MatchCount, bool Recent);
    private sealed record SearchChipPaletteEntry(Brush Background, Brush Foreground);
}

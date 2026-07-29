using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxFavoriteHistoryEntries = 100;

    private sealed record FavoriteHistoryChange(
        string Path,
        string FileName,
        int Before,
        int After);

    private sealed class FavoriteHistoryBatch
    {
        public required IReadOnlyList<FavoriteHistoryChange> Changes { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required string DisplayText { get; init; }
        public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private readonly ObservableCollection<FavoriteHistoryBatch> _favoriteHistory = [];
    private readonly Stack<FavoriteHistoryBatch> _favoriteUndoStack = [];
    private readonly Stack<FavoriteHistoryBatch> _favoriteRedoStack = [];
    private bool _applyingFavoriteHistory;

    private void InitializeFavoriteHistory()
    {
        FavoriteHistoryList.ItemsSource = _favoriteHistory;
        UpdateFavoriteHistorySurface();
    }

    private void RecordFavoriteHistory(IReadOnlyList<(Tile Tile, int Before, int After)> mutations)
    {
        if (_applyingFavoriteHistory)
            return;

        List<FavoriteHistoryChange> changes = mutations
            .Where(static mutation => mutation.Tile.IsRealFile && mutation.Before != mutation.After)
            .Select(static mutation => new FavoriteHistoryChange(
                NormalizeFavoritePath(mutation.Tile.Path),
                mutation.Tile.FileName,
                Math.Clamp(mutation.Before, 0, 5),
                Math.Clamp(mutation.After, 0, 5)))
            .ToList();
        if (changes.Count == 0)
            return;

        var batch = new FavoriteHistoryBatch
        {
            Changes = changes,
            Timestamp = DateTimeOffset.Now,
            DisplayText = DescribeFavoriteHistory(changes),
        };
        _favoriteUndoStack.Push(batch);
        _favoriteRedoStack.Clear();
        _favoriteHistory.Insert(0, batch);
        while (_favoriteHistory.Count > MaxFavoriteHistoryEntries)
            _favoriteHistory.RemoveAt(_favoriteHistory.Count - 1);
        UpdateFavoriteHistorySurface();
    }

    private static string DescribeFavoriteHistory(IReadOnlyList<FavoriteHistoryChange> changes)
    {
        if (changes.Count != 1)
        {
            int added = changes.Count(static change => change.Before == 0 && change.After > 0);
            int removed = changes.Count(static change => change.Before > 0 && change.After == 0);
            int lowered = changes.Count(static change => change.After > 0 && change.After < change.Before);
            int raised = changes.Count(static change => change.After > change.Before && change.Before > 0);
            var parts = new List<string>(4);
            if (added > 0) parts.Add($"added {added}");
            if (raised > 0) parts.Add($"raised {raised}");
            if (lowered > 0) parts.Add($"lowered {lowered}");
            if (removed > 0) parts.Add($"removed {removed}");
            string summary = parts.Count > 0 ? string.Join(", ", parts) : "levels changed";
            return $"{changes.Count:N0} images: {summary}";
        }

        FavoriteHistoryChange change = changes[0];
        string action = change switch
        {
            { Before: 0, After: > 0 } => "Added favorite",
            { Before: > 0, After: 0 } => "Removed favorite",
            _ when change.After < change.Before => "Lowered favorite",
            _ when change.After > change.Before => "Raised favorite",
            _ => "Changed favorite",
        };
        return $"{action}: {change.FileName} (Lv{change.Before} → Lv{change.After})";
    }

    private void FavoriteUndo_Click(object sender, RoutedEventArgs e)
        => UndoFavoriteChange();

    private void FavoriteRedo_Click(object sender, RoutedEventArgs e)
        => RedoFavoriteChange();

    private void FavoriteHistory_Click(object sender, RoutedEventArgs e)
    {
        FavoriteHistoryPopup.IsOpen = !FavoriteHistoryPopup.IsOpen;
        if (FavoriteHistoryPopup.IsOpen && _favoriteHistory.Count > 0)
            FavoriteHistoryList.ScrollIntoView(_favoriteHistory[0]);
    }

    private bool UndoFavoriteChange()
    {
        if (_favoriteUndoStack.TryPeek(out FavoriteHistoryBatch? batch) is false)
            return false;
        if (!ApplyFavoriteHistoryBatch(batch, useBefore: true, "Undid favorite change."))
            return false;

        _favoriteUndoStack.Pop();
        _favoriteRedoStack.Push(batch);
        UpdateFavoriteHistorySurface();
        return true;
    }

    private bool RedoFavoriteChange()
    {
        if (_favoriteRedoStack.TryPeek(out FavoriteHistoryBatch? batch) is false)
            return false;
        if (!ApplyFavoriteHistoryBatch(batch, useBefore: false, "Redid favorite change."))
            return false;

        _favoriteRedoStack.Pop();
        _favoriteUndoStack.Push(batch);
        UpdateFavoriteHistorySurface();
        return true;
    }

    private bool ApplyFavoriteHistoryBatch(FavoriteHistoryBatch batch, bool useBefore, string status)
    {
        if (!CanStartSharedStateAction(SharedStoreKind.Favorite))
            return false;

        var tilesByPath = _allTiles
            .Where(static tile => tile.IsRealFile)
            .GroupBy(static tile => NormalizeFavoritePath(tile.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<(Tile Tile, int Level)>(batch.Changes.Count);
        foreach (FavoriteHistoryChange change in batch.Changes)
        {
            if (!tilesByPath.TryGetValue(change.Path, out Tile? tile))
            {
                ShowFavoriteChangeStatus($"Cannot apply history because {change.FileName} is no longer in the catalog.");
                return false;
            }
            changes.Add((tile, useBefore ? change.Before : change.After));
        }

        _applyingFavoriteHistory = true;
        try
        {
            bool applied;
            if (ShouldUseFavoriteWriter())
            {
                applied = QueueFavoriteLevels(changes, status);
            }
            else
            {
                applied = true;
                foreach ((Tile tile, int level) in changes)
                {
                    if (!SetFavoriteLevel(tile, level))
                    {
                        applied = false;
                        break;
                    }
                }
                if (applied)
                    ShowFavoriteChangeStatus(status);
            }
            return applied;
        }
        finally
        {
            _applyingFavoriteHistory = false;
        }
    }

    private void UpdateFavoriteHistorySurface()
    {
        if (FavoriteUndoButton is null)
            return;

        FavoriteUndoButton.IsEnabled = _favoriteUndoStack.Count > 0;
        FavoriteRedoButton.IsEnabled = _favoriteRedoStack.Count > 0;
        FavoriteHistoryEmptyText.Visibility = _favoriteHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        FavoriteHistoryList.Visibility = _favoriteHistory.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        FavoriteHistoryButton.Content = _favoriteHistory.Count == 0
            ? "History"
            : $"History {_favoriteHistory.Count}";
    }

    private bool TryHandleFavoriteUndoRedo(Key key, ModifierKeys modifiers)
    {
        if (Modal.Visibility == Visibility.Visible || modifiers != ModifierKeys.Control)
            return false;
        if (key == Key.Z)
            return UndoFavoriteChange();
        if (key == Key.Y)
            return RedoFavoriteChange();
        return false;
    }

    public int FavoriteHistoryCountForSmoke => _favoriteHistory.Count;
    public bool FavoriteUndoAvailableForSmoke => FavoriteUndoButton.IsEnabled;
    public bool FavoriteRedoAvailableForSmoke => FavoriteRedoButton.IsEnabled;
    public bool FavoriteUndoForSmoke() => UndoFavoriteChange();
    public bool FavoriteRedoForSmoke() => RedoFavoriteChange();
    public bool FavoriteHistorySurfaceForSmoke
        => string.Equals(AutomationProperties.GetName(FavoriteUndoButton), "Undo favorite change", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(FavoriteRedoButton), "Redo favorite change", StringComparison.Ordinal)
            && string.Equals(AutomationProperties.GetName(FavoriteHistoryButton), "Show favorite change history", StringComparison.Ordinal)
            && FavoriteUndoButton.ToolTip?.ToString()?.Contains("Ctrl+Z", StringComparison.OrdinalIgnoreCase) == true
            && FavoriteRedoButton.ToolTip?.ToString()?.Contains("Ctrl+Y", StringComparison.OrdinalIgnoreCase) == true;
}

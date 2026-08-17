using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int FolderSetFavoriteDocumentVersion = 1;
    private const int MaxFavoriteFolderSets = 24;
    private const int MaxFoldersPerFavoriteSet = 32;
    private const long MaximumFolderSetFavoriteDocumentBytes = 256L * 1024;
    private static readonly JsonSerializerOptions FolderSetFavoriteJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
    };

    private static LocalPersistenceStorePath ResolvedFolderSetFavoritesStorePath
        => LocalPersistenceStorePath.ForStateSibling(
            ResolvedStatePath,
            LocalPersistenceStoreKind.FolderSetFavorites);

    private static string ResolvedFolderSetFavoritesPath
        => ResolvedFolderSetFavoritesStorePath.FullPath;

    private void RefreshFavoriteFolderSetViews(bool reportFailure = true)
    {
        FolderSetFavoriteReadResult read = ReadFolderSetFavoriteDocument(
            ResolvedFolderSetFavoritesStorePath);
        if (read.State == FolderSetFavoriteReadState.Protected)
        {
            if (reportFailure)
            {
                ReportPersistenceRefusal(
                    "Favorite folder sets",
                    ResolvedFolderSetFavoritesPath,
                    protectedFile: true);
            }
            return;
        }

        _favoriteFolderSetViews.Clear();
        foreach (List<string> folderSet in read.Document.FolderSets)
        {
            _favoriteFolderSetViews.Add(new FolderSetFavoriteView
            {
                FolderSet = folderSet.ToList(),
                Display = FormatFolderSetSummary(folderSet),
                Detail = FormatRecentFolderSet(folderSet),
            });
        }

        if (FavoriteFolderSetEmptyHint is not null)
        {
            FavoriteFolderSetEmptyHint.Visibility = _favoriteFolderSetViews.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static FolderSetFavoriteReadResult ReadFolderSetFavoriteDocument(
        LocalPersistenceStorePath path)
    {
        string fullPath = path.FullPath;
        try
        {
            // The typed path fixes the leaf to folder-set-favorites.json beside
            // the Viewer state store; content is validated from this same
            // bounded handle so a pre-check cannot race the read.
            // codeql[cs/path-injection]
            using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFolderSetFavoriteDocumentBytes)
            {
                return FolderSetFavoriteReadResult.Protected(
                    "favorite folder set file size was outside the supported bounds");
            }
            using JsonDocument parsed = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return FolderSetFavoriteReadResult.Protected(
                    "favorite folder set root was not an object");
            }

            FolderSetFavoriteDocument? document = parsed.RootElement.Deserialize<FolderSetFavoriteDocument>(
                FolderSetFavoriteJsonOptions);
            if (document is null
                || document.Version != FolderSetFavoriteDocumentVersion
                || document.FolderSets.Count > MaxFavoriteFolderSets)
            {
                return FolderSetFavoriteReadResult.Protected(
                    "favorite folder set version or count was unsupported");
            }

            var normalizedSets = new List<List<string>>(document.FolderSets.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<string>? candidate in document.FolderSets)
            {
                if (candidate is null || candidate.Count is 0 or > MaxFoldersPerFavoriteSet)
                {
                    return FolderSetFavoriteReadResult.Protected(
                        "favorite folder set entry was outside the supported bounds");
                }

                List<string> normalized = NormalizeFolderSet(candidate);
                if (normalized.Count != candidate.Count)
                {
                    return FolderSetFavoriteReadResult.Protected(
                        "favorite folder set entry contained an invalid or duplicate path");
                }

                string key = FormatRecentFolderSet(normalized);
                if (!seen.Add(key))
                {
                    return FolderSetFavoriteReadResult.Protected(
                        "favorite folder set entries were duplicated");
                }
                normalizedSets.Add(normalized);
            }

            document.FolderSets = normalizedSets;
            return FolderSetFavoriteReadResult.Loaded(document);
        }
        catch (FileNotFoundException)
        {
            return FolderSetFavoriteReadResult.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return FolderSetFavoriteReadResult.Missing();
        }
        catch (Exception error)
        {
            return FolderSetFavoriteReadResult.Protected(error.Message);
        }
    }

    private bool SaveCurrentFolderSetFavorite()
    {
        List<string> selected = NormalizeFolderSet(_landingFolderSet);
        if (selected.Count == 0 || selected.Count > MaxFoldersPerFavoriteSet)
            return false;

        string selectedKey = FormatRecentFolderSet(selected);
        return MutateFolderSetFavorites(
            current => new[] { selected }
                .Concat(current.Where(folderSet => !string.Equals(
                    FormatRecentFolderSet(folderSet),
                    selectedKey,
                    StringComparison.OrdinalIgnoreCase)))
                .Take(MaxFavoriteFolderSets)
                .Select(static folderSet => folderSet.ToList())
                .ToList(),
            retryAction: () => { SaveCurrentFolderSetFavorite(); });
    }

    private bool RemoveFolderSetFavorite(IReadOnlyList<string> selected)
    {
        string selectedKey = FormatRecentFolderSet(selected);
        if (string.IsNullOrWhiteSpace(selectedKey))
            return false;

        return MutateFolderSetFavorites(
            current => current
                .Where(folderSet => !string.Equals(
                    FormatRecentFolderSet(folderSet),
                    selectedKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(static folderSet => folderSet.ToList())
                .ToList(),
            retryAction: () => RemoveFolderSetFavorite(selected));
    }

    private bool MutateFolderSetFavorites(
        Func<List<List<string>>, List<List<string>>> mutation,
        Action retryAction)
    {
        LocalPersistenceStorePath path = ResolvedFolderSetFavoritesStorePath;
        bool protectedFile = false;
        bool saved = TryWithPersistenceLock(path.FullPath, () =>
        {
            FolderSetFavoriteReadResult latest = ReadFolderSetFavoriteDocument(path);
            if (latest.State == FolderSetFavoriteReadState.Protected)
            {
                protectedFile = true;
                return false;
            }

            List<List<string>> nextSets = mutation(latest.Document.FolderSets)
                .Take(MaxFavoriteFolderSets)
                .Select(static folderSet => folderSet.ToList())
                .ToList();
            var next = new FolderSetFavoriteDocument
            {
                Version = FolderSetFavoriteDocumentVersion,
                FolderSets = nextSets,
                UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                ExtensionData = CloneExtensionData(latest.Document.ExtensionData),
            };
            string json = JsonSerializer.Serialize(next, FolderSetFavoriteJsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFolderSetFavoriteDocumentBytes
                || !LocalPersistenceStoreFile.TryWriteAtomicText(path, json))
            {
                return false;
            }

            FolderSetFavoriteReadResult verification = ReadFolderSetFavoriteDocument(path);
            return verification.State == FolderSetFavoriteReadState.Loaded;
        });

        if (!saved)
        {
            ReportPersistenceRefusal(
                "Favorite folder sets",
                path.FullPath,
                protectedFile,
                protectedFile ? null : retryAction);
            return false;
        }

        RefreshFavoriteFolderSetViews(reportFailure: false);
        return true;
    }

    private void SaveCurrentFolderSetFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (SaveCurrentFolderSetFavorite())
            SetStatusToast("Favorite folder set saved.");
    }

    private async void FavoriteFolderSet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FolderSetFavoriteView favorite })
            return;

        SetLandingFolderSet(favorite.FolderSet);
        await LoadFolderSetAsync(favorite.FolderSet);
    }

    private void RemoveFavoriteFolderSet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FolderSetFavoriteView favorite })
            return;

        if (RemoveFolderSetFavorite(favorite.FolderSet))
            SetStatusToast("Folder set removed from favorites.");
    }

    public string FolderSetFavoritesPathForSmoke => ResolvedFolderSetFavoritesPath;
    public int FavoriteFolderSetCountForSmoke => _favoriteFolderSetViews.Count;
    public List<List<string>> FavoriteFolderSetsForSmoke
        => _favoriteFolderSetViews.Select(static item => item.FolderSet.ToList()).ToList();
    public bool SaveCurrentFolderSetFavoriteForSmoke()
        => SaveCurrentFolderSetFavorite();
    public bool RemoveFavoriteFolderSetForSmoke(int index)
        => index >= 0
            && index < _favoriteFolderSetViews.Count
            && RemoveFolderSetFavorite(_favoriteFolderSetViews[index].FolderSet);
    public bool SelectFavoriteFolderSetForSmoke(int index)
    {
        if (index < 0 || index >= _favoriteFolderSetViews.Count)
            return false;
        SetLandingFolderSet(_favoriteFolderSetViews[index].FolderSet);
        return true;
    }

    public bool LandingFolderNavigationSurfaceContractForSmoke
        => ChangeLandingFolderSetButton.IsEnabled
            && LandingFolderSetScrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            && LandingFolderSetScrollViewer.MaxHeight > 0
            && FavoriteFolderSetScrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            && FavoriteFolderSetScrollViewer.MaxHeight > 0
            && RecentFolderSetScrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            && RecentFolderSetScrollViewer.MaxHeight > 0
            && ItemsPanelIsVerticalStack(FavoriteFolderSetList)
            && ItemsPanelIsVerticalStack(RecentFolderSetList)
            && !string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetHelpText(
                    LandingFolderSetScrollViewer));

    public void UpdateLandingFolderNavigationLayoutForSmoke()
    {
        Landing.Visibility = Visibility.Visible;
        UpdateLayout();
    }

    public bool LandingFolderSetOverflowForSmoke
        => LandingFolderSetScrollViewer.ScrollableHeight > 0;

    private static bool ItemsPanelIsVerticalStack(ItemsControl control)
        => control.ItemsPanel?.LoadContent() is StackPanel
        {
            Orientation: Orientation.Vertical,
        };

    public async Task<bool> StartLandingFolderSetOpenForSmokeAsync()
        => await StartLandingFolderSetOpenAsync();
    public bool LandingFolderOpenBusyForSmoke
        => _landingOpenRequestInProgress
            && !OpenFolderSetButton.IsEnabled
            && ScanPanel.Visibility == Visibility.Visible
            && ScanBar.IsIndeterminate
            && string.Equals(
                OpenFolderSetButtonText.Text,
                UiLanguageResources.Text("UiOpeningFolderSet"),
                StringComparison.Ordinal);
}

public sealed class FolderSetFavoriteView
{
    public List<string> FolderSet { get; init; } = [];
    public string Display { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class FolderSetFavoriteDocument
{
    public int Version { get; set; } = 1;
    public List<List<string>> FolderSets { get; set; } = [];
    public string UpdatedAtUtc { get; set; } = "";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal enum FolderSetFavoriteReadState
{
    Missing,
    Loaded,
    Protected,
}

internal sealed record FolderSetFavoriteReadResult(
    FolderSetFavoriteReadState State,
    FolderSetFavoriteDocument Document,
    string? Error)
{
    public static FolderSetFavoriteReadResult Missing()
        => new(
            FolderSetFavoriteReadState.Missing,
            new FolderSetFavoriteDocument(),
            null);

    public static FolderSetFavoriteReadResult Loaded(FolderSetFavoriteDocument document)
        => new(FolderSetFavoriteReadState.Loaded, document, null);

    public static FolderSetFavoriteReadResult Protected(string error)
        => new(
            FolderSetFavoriteReadState.Protected,
            new FolderSetFavoriteDocument(),
            error);
}

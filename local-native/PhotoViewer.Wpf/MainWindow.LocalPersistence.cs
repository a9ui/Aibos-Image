using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int AiStyleDocumentVersion = 1;
    private const long MaximumAiStyleDocumentBytes = 4L * 1024 * 1024;
    private bool _aiStyleStoreReady;
    private bool _aiStyleWriteBlocked;
    private bool _favoriteActivityStoreReady;
    private bool _localPersistenceCompactionPending;
    private Dictionary<string, JsonElement>? _aiStyleExtensionData;
    private AiStyleDocument? _restoredAiStyleDocument;

    private static string AiStylePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoViewer.Wpf",
        "ai-styles.json");

    private static string ResolvedAiStylePath
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(ResolvedStatePath))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ai-styles.json");

    private static string ResolvedFavoriteActivityPath
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(ResolvedStatePath))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "favorite-activity.sqlite3");

    private void InitializeSplitLocalPersistence(ViewerState? legacyState)
    {
        _restoredAiStyleDocument = LoadOrMigrateAiStyleDocument(legacyState);
        LoadOrMigrateFavoriteActivity(legacyState?.FavoriteChangedAtUtcByPath);
    }

    private AiStyleDocument? LoadOrMigrateAiStyleDocument(ViewerState? legacyState)
    {
        AiStyleReadResult read = ReadAiStyleDocument(ResolvedAiStylePath);
        if (read.State == AiStyleReadState.Loaded)
        {
            _aiStyleStoreReady = true;
            _aiStyleExtensionData = CloneExtensionData(read.Document?.ExtensionData);
            if (HasLegacyAiStyleFields(legacyState))
                _localPersistenceCompactionPending = true;
            return read.Document;
        }
        if (read.State == AiStyleReadState.Protected)
        {
            _aiStyleWriteBlocked = true;
            _aiStyleStoreReady = false;
            return null;
        }

        AiStyleDocument legacyDocument = CreateAiStyleDocumentFromLegacy(legacyState);
        if (!HasAiStyleContent(legacyDocument))
        {
            _aiStyleStoreReady = true;
            return legacyDocument;
        }

        if (!TryCreateAiStyleDocument(
                ResolvedAiStylePath,
                legacyDocument,
                out AiStyleDocument? created,
                out bool protectedFile))
        {
            _aiStyleWriteBlocked = protectedFile;
            _aiStyleStoreReady = false;
            return null;
        }

        _aiStyleStoreReady = true;
        _aiStyleExtensionData = CloneExtensionData(created?.ExtensionData);
        _localPersistenceCompactionPending = true;
        return created ?? legacyDocument;
    }

    private void LoadOrMigrateFavoriteActivity(
        Dictionary<string, DateTimeOffset>? legacyActivity)
    {
        Dictionary<string, DateTimeOffset> normalizedLegacy =
            NormalizeFavoriteActivity(legacyActivity);
        FavoriteActivityStoreReadResult before = FavoriteActivityStore.Read(
            ResolvedFavoriteActivityPath,
            MaxPersistedFavoriteActivityEntries);
        if (before.State == FavoriteActivityStoreReadState.Protected)
        {
            _favoriteActivityStoreReady = false;
            RestoreFavoriteActivityDictionary(normalizedLegacy);
            return;
        }

        if (normalizedLegacy.Count > 0)
        {
            var expected = new Dictionary<string, DateTimeOffset>(
                before.Entries,
                StringComparer.OrdinalIgnoreCase);
            MergeFavoriteActivity(expected, normalizedLegacy);
            TrimFavoriteActivity(expected);
            FavoriteActivityStoreWriteResult migrated = FavoriteActivityStore.Upsert(
                ResolvedFavoriteActivityPath,
                normalizedLegacy,
                MaxPersistedFavoriteActivityEntries);
            if (!migrated.Saved)
            {
                _favoriteActivityStoreReady = false;
                RestoreFavoriteActivityDictionary(normalizedLegacy);
                return;
            }

            FavoriteActivityStoreReadResult after = FavoriteActivityStore.Read(
                ResolvedFavoriteActivityPath,
                MaxPersistedFavoriteActivityEntries);
            if (after.State != FavoriteActivityStoreReadState.Loaded
                || !FavoriteActivityEquals(expected, after.Entries))
            {
                _favoriteActivityStoreReady = false;
                RestoreFavoriteActivityDictionary(normalizedLegacy);
                return;
            }
            before = after;
        }

        _favoriteActivityStoreReady = true;
        RestoreFavoriteActivityDictionary(before.Entries);
        if (legacyActivity is not null)
            _localPersistenceCompactionPending = true;
    }

    private void RestoreFavoriteActivityDictionary(
        IReadOnlyDictionary<string, DateTimeOffset> activity)
    {
        _favoriteChangedAtUtcByPath.Clear();
        foreach ((string path, DateTimeOffset changedAtUtc) in activity)
            _favoriteChangedAtUtcByPath[path] = changedAtUtc;
        TrimFavoriteActivity(_favoriteChangedAtUtcByPath);
    }

    private static Dictionary<string, DateTimeOffset> NormalizeFavoriteActivity(
        IReadOnlyDictionary<string, DateTimeOffset>? activity)
    {
        var normalized = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, DateTimeOffset changedAtUtc) in activity
                     ?? new Dictionary<string, DateTimeOffset>())
        {
            if (string.IsNullOrWhiteSpace(path) || changedAtUtc == default)
                continue;
            string normalizedPath = NormalizeFavoritePath(path);
            DateTimeOffset normalizedTime = changedAtUtc.ToUniversalTime();
            if (!normalized.TryGetValue(normalizedPath, out DateTimeOffset current)
                || normalizedTime > current)
            {
                normalized[normalizedPath] = normalizedTime;
            }
        }
        TrimFavoriteActivity(normalized);
        return normalized;
    }

    private static void MergeFavoriteActivity(
        Dictionary<string, DateTimeOffset> destination,
        IReadOnlyDictionary<string, DateTimeOffset> source)
    {
        foreach ((string path, DateTimeOffset changedAtUtc) in source)
        {
            if (!destination.TryGetValue(path, out DateTimeOffset current)
                || changedAtUtc > current)
            {
                destination[path] = changedAtUtc;
            }
        }
    }

    private static bool FavoriteActivityEquals(
        IReadOnlyDictionary<string, DateTimeOffset> left,
        IReadOnlyDictionary<string, DateTimeOffset> right)
        => left.Count == right.Count
            && left.All(item => right.TryGetValue(item.Key, out DateTimeOffset value)
                && value == item.Value);

    private void RestoreAiStyles(ViewerState? legacyState)
    {
        AiStyleDocument? document = _aiStyleStoreReady
            ? _restoredAiStyleDocument
            : null;
        RestorePhotorealStyles(
            document?.PhotorealStyles ?? legacyState?.PhotorealStyles,
            document?.SelectedPhotorealStyleName
                ?? legacyState?.SelectedPhotorealStyleName);
        RestoreVideoStyles(
            document?.VideoStyles ?? legacyState?.VideoStyles,
            document?.SelectedVideoStyleName
                ?? legacyState?.SelectedVideoStyleName);
        RestoreI2iV3Styles(
            document?.I2iEditStyles ?? legacyState?.I2iEditStyles,
            document?.SelectedI2iEditStyleName
                ?? legacyState?.SelectedI2iEditStyleName);
    }

    private void SaveAiStyles()
    {
        if (_initializing || _suppressStateSave)
            return;
        if (!_aiStyleStoreReady)
        {
            // A protected or temporarily unavailable dedicated file is never
            // replaced. Keep using the legacy state fields until it is repaired.
            SaveState();
            return;
        }
        if (_aiStyleWriteBlocked)
        {
            ReportPersistenceRefusal(
                "AI Styles",
                ResolvedAiStylePath,
                protectedFile: true);
            return;
        }

        AiStyleDocument snapshot = CreateCurrentAiStyleDocument();
        if (!TrySaveAiStyleDocument(
                ResolvedAiStylePath,
                snapshot,
                out AiStyleDocument? saved,
                out bool protectedFile))
        {
            _aiStyleWriteBlocked = protectedFile;
            ReportPersistenceRefusal(
                "AI Styles",
                ResolvedAiStylePath,
                protectedFile,
                protectedFile ? null : SaveAiStyles);
            return;
        }

        _aiStyleExtensionData = CloneExtensionData(saved?.ExtensionData);
        ApplySavedAiStyleExtensionData(saved);
    }

    private AiStyleDocument CreateCurrentAiStyleDocument()
        => new()
        {
            Version = AiStyleDocumentVersion,
            PhotorealStyles = SnapshotPhotorealStyles(),
            SelectedPhotorealStyleName = _selectedPhotorealStyleName,
            VideoStyles = SnapshotVideoStyles(),
            SelectedVideoStyleName = _selectedVideoStyleName,
            I2iEditStyles = SnapshotI2iV3Styles(),
            SelectedI2iEditStyleName = _selectedI2iV3StyleName,
            ExtensionData = CloneExtensionData(_aiStyleExtensionData),
        };

    private static AiStyleDocument CreateAiStyleDocumentFromLegacy(ViewerState? state)
        => new()
        {
            Version = AiStyleDocumentVersion,
            PhotorealStyles = state?.PhotorealStyles,
            SelectedPhotorealStyleName = state?.SelectedPhotorealStyleName,
            VideoStyles = state?.VideoStyles,
            SelectedVideoStyleName = state?.SelectedVideoStyleName,
            I2iEditStyles = state?.I2iEditStyles,
            SelectedI2iEditStyleName = state?.SelectedI2iEditStyleName,
        };

    private static bool HasLegacyAiStyleFields(ViewerState? state)
        => state?.PhotorealStyles is not null
            || state?.SelectedPhotorealStyleName is not null
            || state?.VideoStyles is not null
            || state?.SelectedVideoStyleName is not null
            || state?.I2iEditStyles is not null
            || state?.SelectedI2iEditStyleName is not null;

    private static bool HasAiStyleContent(AiStyleDocument document)
        => document.PhotorealStyles is { Count: > 0 }
            || document.VideoStyles is { Count: > 0 }
            || document.I2iEditStyles is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(document.SelectedPhotorealStyleName)
            || !string.IsNullOrWhiteSpace(document.SelectedVideoStyleName)
            || !string.IsNullOrWhiteSpace(document.SelectedI2iEditStyleName)
            || document.ExtensionData is { Count: > 0 };

    private static AiStyleReadResult ReadAiStyleDocument(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return AiStyleReadResult.Missing();
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length is <= 0 or > MaximumAiStyleDocumentBytes)
                return AiStyleReadResult.Protected("AI Style file size was outside the supported bounds");
            using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return AiStyleReadResult.Protected("AI Style root was not an object");
            AiStyleDocument? value = document.RootElement.Deserialize<AiStyleDocument>(
                new JsonSerializerOptions { MaxDepth = 64 });
            if (value is null
                || value.Version != AiStyleDocumentVersion
                || !AreAiStyleCollectionsSupported(value))
            {
                return AiStyleReadResult.Protected("AI Style version or content was unsupported");
            }
            return AiStyleReadResult.Loaded(value);
        }
        catch (Exception error)
        {
            return AiStyleReadResult.Protected(error.Message);
        }
    }

    private static bool AreAiStyleCollectionsSupported(AiStyleDocument document)
    {
        if (document.PhotorealStyles is { Count: > MaxPhotorealStyleCount })
            return false;
        var photorealNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealStyleState? candidate in document.PhotorealStyles ?? [])
        {
            PhotorealStyleState? normalized = NormalizePhotorealStyle(candidate);
            if (normalized is null || !photorealNames.Add(normalized.Name))
                return false;
        }
        return AreViewerStyleCollectionsSupported(new ViewerState
        {
            VideoStyles = document.VideoStyles,
            I2iEditStyles = document.I2iEditStyles,
        });
    }

    private static bool TryCreateAiStyleDocument(
        string path,
        AiStyleDocument legacy,
        out AiStyleDocument? saved,
        out bool protectedFile)
    {
        AiStyleDocument? savedDocument = null;
        saved = null;
        protectedFile = false;
        bool malformed = false;
        bool result = TryWithPersistenceLock(path, () =>
        {
            AiStyleReadResult latest = ReadAiStyleDocument(path);
            if (latest.State == AiStyleReadState.Protected)
            {
                malformed = true;
                return false;
            }
            if (latest.State == AiStyleReadState.Loaded)
            {
                savedDocument = latest.Document;
                return true;
            }
            string json = JsonSerializer.Serialize(
                legacy,
                new JsonSerializerOptions { WriteIndented = true });
            if (!TryWriteAtomicText(path, json))
                return false;
            AiStyleReadResult verification = ReadAiStyleDocument(path);
            savedDocument = verification.Document;
            return verification.State == AiStyleReadState.Loaded;
        });
        saved = savedDocument;
        protectedFile = malformed;
        return result;
    }

    private static bool TrySaveAiStyleDocument(
        string path,
        AiStyleDocument current,
        out AiStyleDocument? saved,
        out bool protectedFile)
    {
        AiStyleDocument? savedDocument = null;
        saved = null;
        protectedFile = false;
        bool malformed = false;
        bool result = TryWithPersistenceLock(path, () =>
        {
            AiStyleReadResult latest = ReadAiStyleDocument(path);
            if (latest.State == AiStyleReadState.Protected)
            {
                malformed = true;
                return false;
            }
            MergeLatestAiStyleExtensionData(current, latest.Document);
            string json = JsonSerializer.Serialize(
                current,
                new JsonSerializerOptions { WriteIndented = true });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumAiStyleDocumentBytes)
                return false;
            if (!TryWriteAtomicText(path, json))
                return false;
            AiStyleReadResult verification = ReadAiStyleDocument(path);
            savedDocument = verification.Document;
            return verification.State == AiStyleReadState.Loaded;
        });
        saved = savedDocument;
        protectedFile = malformed;
        return result;
    }

    private static void MergeLatestAiStyleExtensionData(
        AiStyleDocument current,
        AiStyleDocument? latest)
    {
        if (latest is null)
            return;
        current.ExtensionData = CloneExtensionData(latest.ExtensionData);
        MergeStyleItemExtensionData(current.PhotorealStyles, latest.PhotorealStyles);
        MergeStyleItemExtensionData(current.VideoStyles, latest.VideoStyles);
        MergeStyleItemExtensionData(current.I2iEditStyles, latest.I2iEditStyles);
    }

    private static void MergeStyleItemExtensionData<T>(
        IEnumerable<T>? current,
        IEnumerable<T>? latest)
        where T : class
    {
        foreach (T item in current ?? [])
        {
            string name = item switch
            {
                PhotorealStyleState value => value.Name,
                VideoStyleState value => value.Name,
                I2iEditStyleState value => value.Name,
                _ => "",
            };
            T? match = (latest ?? []).FirstOrDefault(candidate =>
                string.Equals(
                    candidate switch
                    {
                        PhotorealStyleState value => value.Name,
                        VideoStyleState value => value.Name,
                        I2iEditStyleState value => value.Name,
                        _ => "",
                    },
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;
            Dictionary<string, JsonElement>? extensionData = match switch
            {
                PhotorealStyleState value => value.ExtensionData,
                VideoStyleState value => value.ExtensionData,
                I2iEditStyleState value => value.ExtensionData,
                _ => null,
            };
            switch (item)
            {
                case PhotorealStyleState value:
                    value.ExtensionData = CloneExtensionData(extensionData);
                    break;
                case VideoStyleState value:
                    value.ExtensionData = CloneExtensionData(extensionData);
                    break;
                case I2iEditStyleState value:
                    value.ExtensionData = CloneExtensionData(extensionData);
                    break;
            }
        }
    }

    private void ApplySavedAiStyleExtensionData(AiStyleDocument? saved)
    {
        if (saved is null)
            return;
        MergeStyleItemExtensionData(_photorealStyles, saved.PhotorealStyles);
        MergeStyleItemExtensionData(_videoStyles, saved.VideoStyles);
        MergeStyleItemExtensionData(_i2iV3Styles, saved.I2iEditStyles);
    }

    public string AiStylePathForSmoke => ResolvedAiStylePath;
    public string FavoriteActivityPathForSmoke => ResolvedFavoriteActivityPath;
    public bool SplitLocalPersistenceReadyForSmoke
        => _aiStyleStoreReady && _favoriteActivityStoreReady;

    public async Task<bool> PersistFavoriteActivityForSmokeAsync(
        string path,
        DateTimeOffset changedAtUtc,
        TimeSpan timeout)
    {
        string normalized = NormalizeFavoritePath(path);
        _favoriteChangedAtUtcByPath[normalized] = changedAtUtc.ToUniversalTime();
        ScheduleFavoritePresentationStateSave(
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
            {
                [normalized] = changedAtUtc.ToUniversalTime(),
            },
            includeFilterState: false);
        return await WaitForFavoritePresentationStateForSmokeAsync(timeout);
    }

    private enum AiStyleReadState
    {
        Missing,
        Loaded,
        Protected,
    }

    private sealed record AiStyleReadResult(
        AiStyleReadState State,
        AiStyleDocument? Document,
        string? Error)
    {
        public static AiStyleReadResult Missing()
            => new(AiStyleReadState.Missing, null, null);
        public static AiStyleReadResult Loaded(AiStyleDocument document)
            => new(AiStyleReadState.Loaded, document, null);
        public static AiStyleReadResult Protected(string error)
            => new(AiStyleReadState.Protected, null, error);
    }
}

public sealed class AiStyleDocument
{
    public int Version { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PhotorealStyleState>? PhotorealStyles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPhotorealStyleName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VideoStyleState>? VideoStyles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedVideoStyleName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<I2iEditStyleState>? I2iEditStyles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedI2iEditStyleName { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

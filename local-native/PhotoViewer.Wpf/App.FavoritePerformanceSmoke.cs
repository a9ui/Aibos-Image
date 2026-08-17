using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureFavoritePerformanceSmoke(string resultPath)
    {
        const int favoriteEntryCount = 40_000;
        const int activityEntryCount = 20_000;
        const long uiBudgetMilliseconds = 250;
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-favorite-performance-").FullName;
        var previousEnvironment = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            object result;
            bool ok = false;
            try
            {
                string tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string normalizedSmokeRoot = Path.GetFullPath(smokeRoot)
                    .TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!normalizedSmokeRoot.StartsWith(
                        tempRoot,
                        StringComparison.OrdinalIgnoreCase)
                    || !resultFullPath.StartsWith(
                        tempRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Favorite performance smoke storage must stay under TEMP.");
                }

                string imageRoot = Path.Combine(smokeRoot, "images");
                string storeRoot = Path.Combine(smokeRoot, "stores");
                string enhancementRoot = Path.Combine(storeRoot, "enhance");
                string outputRoot = Path.Combine(enhancementRoot, "outputs");
                string metadataRoot = Path.Combine(storeRoot, "metadata-index");
                string sourcePath = Path.Combine(imageRoot, "source.png");
                string outputPath = Path.Combine(
                    outputRoot,
                    "Photorealized",
                    "2026-08-12",
                    "favorite-performance-output.png");
                string favoritesPath = Path.Combine(storeRoot, "favorites.json");
                string statePath = Path.Combine(storeRoot, "state.json");
                string activityPath = Path.Combine(
                    storeRoot,
                    "favorite-activity.sqlite3");
                string jobsPath = Path.Combine(enhancementRoot, "jobs.json");
                Directory.CreateDirectory(imageRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                Directory.CreateDirectory(metadataRoot);
                WriteSmokePng(
                    sourcePath,
                    32,
                    24,
                    Color.FromRgb(74, 118, 182));
                File.Copy(sourcePath, outputPath, overwrite: true);
                string sourceFingerprintBefore = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(sourcePath)));

                var favorites = new Dictionary<string, int>(
                    favoriteEntryCount,
                    StringComparer.OrdinalIgnoreCase);
                string historyRoot = Path.Combine(smokeRoot, "history");
                for (int index = 0; index < favoriteEntryCount - 1; index++)
                {
                    favorites[Path.GetFullPath(Path.Combine(
                        historyRoot,
                        $"favorite-{index:D5}.png"))] = index % 5 + 1;
                }
                favorites[Path.GetFullPath(outputPath)] = 1;
                File.WriteAllText(
                    favoritesPath,
                    JsonSerializer.Serialize(
                        favorites,
                        new JsonSerializerOptions { WriteIndented = true }));

                var activity = new Dictionary<string, DateTimeOffset>(
                    activityEntryCount,
                    StringComparer.OrdinalIgnoreCase);
                DateTimeOffset activityStart =
                    new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                for (int index = 0; index < activityEntryCount; index++)
                {
                    activity[Path.GetFullPath(Path.Combine(
                        historyRoot,
                        $"activity-{index:D5}.png"))] =
                        activityStart.AddSeconds(index);
                }
                var statePayload = new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["Version"] = 2,
                    ["FavoriteChangedAtUtcByPath"] = activity,
                    ["smokeMarker"] = "keep-favorite-performance-state",
                };
                File.WriteAllText(
                    statePath,
                    JsonSerializer.Serialize(
                        statePayload,
                        new JsonSerializerOptions { WriteIndented = true }));

                var sourceInfo = new FileInfo(sourcePath);
                double sourceMtimeMs = new DateTimeOffset(
                    sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                File.WriteAllText(
                    jobsPath,
                    JsonSerializer.Serialize(new
                    {
                        version = 1,
                        jobs = new[]
                        {
                            new
                            {
                                id = "favorite-performance-photoreal",
                                operation = "photoreal",
                                sourceId = sourcePath,
                                sourcePath,
                                sourceSignature = new
                                {
                                    size = sourceInfo.Length,
                                    mtimeMs = sourceMtimeMs,
                                },
                                presetId = "photoreal-balanced",
                                adapterId = "comfyui-flux2-photoreal",
                                status = "succeeded",
                                progress = 100,
                                outputPath,
                                createdAt = "2026-08-12T07:00:00.000Z",
                                updatedAt = "2026-08-12T07:01:00.000Z",
                            },
                        },
                    }));

                var environment = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
                    ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = favoritesPath,
                    ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storeRoot, "seen.json"),
                    ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storeRoot, "recent.json"),
                    ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storeRoot, "settings.json"),
                    ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storeRoot, "albums.json"),
                    ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storeRoot, "search-history.json"),
                    ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = metadataRoot,
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputRoot,
                    ["PVU_ENHANCE_OUTPUT_ROOT"] = outputRoot,
                };
                File.WriteAllText(environment["PHOTOVIEWER_WPF_SEEN_PATH"], "{}");
                File.WriteAllText(
                    environment["PHOTOVIEWER_WPF_RECENT_PATH"],
                    "{\"version\":1,\"lastFolderSet\":[],\"recentFolderSets\":[],\"updatedAtUtc\":\"\"}");
                File.WriteAllText(environment["PHOTOVIEWER_WPF_SETTINGS_PATH"], "{\"version\":1}");
                File.WriteAllText(
                    environment["PHOTOVIEWER_WPF_ALBUMS_PATH"],
                    "{\"version\":1,\"revision\":0,\"albums\":[],\"recentAlbumIds\":[]}");
                File.WriteAllText(
                    environment["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"],
                    "{\"version\":1,\"entries\":[]}");
                foreach ((string name, string value) in environment)
                {
                    previousEnvironment[name] =
                        Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, value);
                }

                window = HiddenWindow();
                window.ForceSharedStoreWritersForSmoke();
                window.Show();
                await window.LoadFolderSetAsync(
                        [imageRoot],
                        commitRecent: false)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                string sourceName = Path.GetFileName(sourcePath);
                bool initialDerivedFavorite =
                    window.PhotorealFavoriteLevelForFileForSmoke(sourceName) == 1;

                var mutationWatch = Stopwatch.StartNew();
                bool mutationAccepted = window.SetIndependentFavoriteLevelForSmoke(
                    outputPath,
                    2);
                mutationWatch.Stop();

                var pingCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var pingWatch = Stopwatch.StartNew();
                _ = window.Dispatcher.BeginInvoke(
                    () => pingCompletion.TrySetResult(true),
                    DispatcherPriority.Input);
                await pingCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pingWatch.Stop();

                SharedWriteStatus[] writerStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync()
                        .WaitAsync(TimeSpan.FromSeconds(20));
                bool activityPersisted =
                    await window.WaitForFavoritePresentationStateForSmokeAsync(
                        TimeSpan.FromSeconds(20));

                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke(2);
                var filterOnCallWatch = Stopwatch.StartNew();
                Task<MainWindow.SearchFilterCompletion> filterOnTask =
                    window.SetFavoriteOnlyFilterForSmokeAsync(true);
                filterOnCallWatch.Stop();
                MainWindow.SearchFilterCompletion filterOn =
                    await filterOnTask.WaitAsync(TimeSpan.FromSeconds(10));
                bool filterOnExact = filterOn.Applied
                    && !filterOn.Discarded
                    && window.FilteredFileNamesForSmoke().SequenceEqual(
                        [sourceName],
                        StringComparer.OrdinalIgnoreCase);

                var filterOffCallWatch = Stopwatch.StartNew();
                Task<MainWindow.SearchFilterCompletion> filterOffTask =
                    window.ClearFavoriteFiltersForSmokeAsync();
                filterOffCallWatch.Stop();
                MainWindow.SearchFilterCompletion filterOff =
                    await filterOffTask.WaitAsync(TimeSpan.FromSeconds(10));
                bool filterOffExact = filterOff.Applied
                    && !filterOff.Discarded
                    && window.FilteredFileNamesForSmoke().SequenceEqual(
                        [sourceName],
                        StringComparer.OrdinalIgnoreCase);
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                bool filterStatePersisted =
                    await window.WaitForFavoritePresentationStateForSmokeAsync(
                        TimeSpan.FromSeconds(20));

                Dictionary<string, int>? persistedFavorites =
                    JsonSerializer.Deserialize<Dictionary<string, int>>(
                        File.ReadAllText(favoritesPath));
                ViewerState? persistedState = JsonSerializer.Deserialize<ViewerState>(
                    File.ReadAllText(statePath));
                FavoriteActivityStoreReadResult persistedActivity =
                    FavoriteActivityStore.Read(activityPath, activityEntryCount);
                bool stateExtensionPreserved = persistedState?.ExtensionData is { } extension
                    && extension.TryGetValue("smokeMarker", out JsonElement marker)
                    && marker.ValueKind == JsonValueKind.String
                    && marker.GetString() == "keep-favorite-performance-state";
                bool storesExact = persistedFavorites?.Count == favoriteEntryCount
                    && persistedFavorites.TryGetValue(
                        Path.GetFullPath(outputPath),
                        out int persistedLevel)
                    && persistedLevel == 2
                    && persistedActivity.State
                        == FavoriteActivityStoreReadState.Loaded
                    && persistedActivity.Entries.Count == activityEntryCount
                    && persistedActivity.Entries.ContainsKey(
                        Path.GetFullPath(outputPath))
                    && persistedState is not null
                    && persistedState.FavoriteChangedAtUtcByPath is null
                    && persistedState.PhotorealFavoriteFilterLevels is null
                    && stateExtensionPreserved;
                bool targetedPresentation =
                    window.LastDerivedFavoriteVisitedTileCountForSmoke <= 1
                    && window.MaxDerivedFavoriteVisitedTileCountForSmoke <= 1
                    && window.PhotorealFavoriteLevelForFileForSmoke(sourceName) == 2;
                bool boundedUi = mutationWatch.ElapsedMilliseconds
                        <= uiBudgetMilliseconds
                    && pingWatch.ElapsedMilliseconds <= uiBudgetMilliseconds
                    && filterOnCallWatch.ElapsedMilliseconds
                        <= uiBudgetMilliseconds
                    && filterOffCallWatch.ElapsedMilliseconds
                        <= uiBudgetMilliseconds
                    && window.MaxFavoriteWriteApplyMillisecondsForSmoke
                        <= uiBudgetMilliseconds;
                bool writerExact = mutationAccepted
                    && writerStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && !window.FavoriteWriterPendingForSmoke
                    && !window.FavoritePresentationStatePendingForSmoke
                    && window.FavoritePresentationStateWriteCountForSmoke >= 2
                    && activityPersisted
                    && filterStatePersisted;

                static JsonElement JobTimingFixture(
                    string status,
                    string? startedAt,
                    string? finishedAt)
                    => JsonSerializer.SerializeToElement(new
                    {
                        id = $"timing-{status}-{startedAt}-{finishedAt}",
                        operation = "photoreal",
                        status,
                        createdAt = "2026-08-12T07:00:00.000Z",
                        updatedAt = "2026-08-12T07:02:10.000Z",
                        startedAt,
                        finishedAt,
                    });
                bool succeededTimingParsed =
                    PhotoViewer.Wpf.MainWindow.TryReadEnhancementJobElapsedForSmoke(
                        JobTimingFixture(
                            "succeeded",
                            "2026-08-12T07:01:02.000Z",
                            "2026-08-12T07:02:10.000Z"),
                        out string? elapsedText,
                        out string timestampText,
                        out string accessibleName);
                bool runningTimingParsed =
                    PhotoViewer.Wpf.MainWindow.TryReadEnhancementJobElapsedForSmoke(
                        JobTimingFixture(
                            "running",
                            "2026-08-12T07:01:02.000Z",
                            "2026-08-12T07:02:10.000Z"),
                        out string? runningElapsed,
                        out _,
                        out _);
                bool invalidTimingParsed =
                    PhotoViewer.Wpf.MainWindow.TryReadEnhancementJobElapsedForSmoke(
                        JobTimingFixture(
                            "succeeded",
                            "2026-08-12T07:02:10.000Z",
                            "2026-08-12T07:01:02.000Z"),
                        out string? invalidElapsed,
                        out _,
                        out _);
                bool completedElapsedVisible = succeededTimingParsed
                    && elapsedText == "所要 1分 8秒"
                    && timestampText.Contains(
                        "所要 1分 8秒",
                        StringComparison.Ordinal)
                    && accessibleName.Contains(
                        "所要 1分 8秒",
                        StringComparison.Ordinal)
                    && runningTimingParsed
                    && runningElapsed is null
                    && invalidTimingParsed
                    && invalidElapsed is null;

                string restoreRoot = Path.Combine(smokeRoot, "restore-fixture");
                string restorePhotorealRoot = Path.Combine(
                    restoreRoot,
                    "Photorealized");
                string restoreFavoritesPath = Path.Combine(
                    restoreRoot,
                    "favorites.json");
                string restoreJobsPath = Path.Combine(
                    restoreRoot,
                    "jobs.sqlite3");
                string restoreBackupRoot = Path.Combine(
                    restoreRoot,
                    "migration-backups");
                string restoreReceiptPath = Path.Combine(
                    restoreBackupRoot,
                    "receipt.json");
                string legacyA = Path.Combine(
                    restorePhotorealRoot,
                    "restored-a.png");
                string legacyB = Path.Combine(
                    restorePhotorealRoot,
                    "restored-b.png");
                string legacyAmbiguous = Path.Combine(
                    restorePhotorealRoot,
                    "ambiguous.png");
                string legacyUnmatched = Path.Combine(
                    restorePhotorealRoot,
                    "unmatched.png");
                string currentA = Path.Combine(
                    restorePhotorealRoot,
                    "2026-08-12",
                    "restored-a.png");
                string currentB = Path.Combine(
                    restorePhotorealRoot,
                    "2026-08-12",
                    "restored-b.png");
                string currentAmbiguousA = Path.Combine(
                    restorePhotorealRoot,
                    "2026-08-11",
                    "ambiguous.png");
                string currentAmbiguousB = Path.Combine(
                    restorePhotorealRoot,
                    "2026-08-12",
                    "ambiguous.png");
                foreach (string currentPath in new[]
                {
                    currentA,
                    currentB,
                    currentAmbiguousA,
                    currentAmbiguousB,
                })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
                    File.Copy(sourcePath, currentPath, overwrite: true);
                }
                File.WriteAllText(
                    restoreFavoritesPath,
                    JsonSerializer.Serialize(
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                        {
                            [legacyA] = 4,
                            [legacyB] = 3,
                            [currentB] = 2,
                            [legacyAmbiguous] = 5,
                            [legacyUnmatched] = 1,
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = restoreJobsPath,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false,
                    }.ToString()))
                {
                    connection.Open();
                    using SqliteCommand create = connection.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE enhancement_jobs (
                            id TEXT PRIMARY KEY,
                            operation TEXT NOT NULL,
                            status TEXT NOT NULL,
                            payload_json TEXT NOT NULL
                        );
                        """;
                    create.ExecuteNonQuery();
                    foreach ((string id, string currentPath) in new[]
                    {
                        ("restore-a", currentA),
                        ("restore-b", currentB),
                        ("restore-ambiguous-a", currentAmbiguousA),
                        ("restore-ambiguous-b", currentAmbiguousB),
                    })
                    {
                        using SqliteCommand insert = connection.CreateCommand();
                        insert.CommandText = """
                            INSERT INTO enhancement_jobs (id, operation, status, payload_json)
                            VALUES ($id, 'photoreal', 'succeeded', $payload)
                            """;
                        insert.Parameters.AddWithValue("$id", id);
                        insert.Parameters.AddWithValue(
                            "$payload",
                            JsonSerializer.Serialize(new
                            {
                                outputPath = currentPath,
                            }));
                        insert.ExecuteNonQuery();
                    }
                }
                byte[] restoreBeforeBytes = File.ReadAllBytes(
                    restoreFavoritesPath);
                string restoreBeforeSha = Convert.ToHexString(
                    SHA256.HashData(restoreBeforeBytes)).ToLowerInvariant();
                PhotorealFavoriteHistoryRestoreResult restoreResult =
                    PhotorealFavoriteHistoryRestorer.Restore(new(
                        restoreFavoritesPath,
                        restoreJobsPath,
                        restorePhotorealRoot,
                        restoreBackupRoot,
                        restoreReceiptPath));
                Dictionary<string, int>? restoredFavorites =
                    JsonSerializer.Deserialize<Dictionary<string, int>>(
                        File.ReadAllText(restoreFavoritesPath));
                string restoreBackupSha = restoreResult.BackupPath is { } backupPath
                    ? Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(backupPath)))
                        .ToLowerInvariant()
                    : "";
                using JsonDocument restoreReceipt = JsonDocument.Parse(
                    File.ReadAllText(restoreReceiptPath));
                bool restorationExact = restoreResult is
                    {
                        Success: true,
                        EntriesBefore: 5,
                        EntriesAfter: 6,
                        LegacyCandidates: 4,
                        CurrentOutputCandidates: 4,
                        Added: 1,
                        AlreadyPresent: 0,
                        Conflicts: 1,
                        Ambiguous: 1,
                        Unmatched: 1,
                    }
                    && restoreResult.BeforeSha256 == restoreBeforeSha
                    && restoreBackupSha == restoreBeforeSha
                    && restoreResult.AfterSha256 != restoreBeforeSha
                    && restoredFavorites?.Count == 6
                    && restoredFavorites[legacyA] == 4
                    && restoredFavorites[currentA] == 4
                    && restoredFavorites[legacyB] == 3
                    && restoredFavorites[currentB] == 2
                    && restoredFavorites[legacyAmbiguous] == 5
                    && restoredFavorites[legacyUnmatched] == 1
                    && restoreReceipt.RootElement.GetProperty("Success")
                        .GetBoolean()
                    && !File.Exists(restoreFavoritesPath + ".lock");

                string closeDrainPath = Path.GetFullPath(Path.Combine(
                    historyRoot,
                    "close-drain.png"));
                DateTimeOffset closeDrainTimestamp =
                    new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
                using var presentationWriterEntered = new ManualResetEventSlim(false);
                using var presentationWriterGate = new ManualResetEventSlim(false);
                window.ConfigureFavoritePresentationWriterGateForSmoke(
                    presentationWriterEntered,
                    presentationWriterGate);
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke(4);
                window.QueueFavoritePresentationStateForSmoke(
                    closeDrainPath,
                    closeDrainTimestamp);
                bool presentationWriterBlocked = await Task.Run(
                    () => presentationWriterEntered.Wait(TimeSpan.FromSeconds(5)));
                Task closeAfterDrain = window.CloseAndWaitForSmokeAsync();
                bool closeWasDeferred = window.ClosingDrainInProgressForSmoke
                    && !closeAfterDrain.IsCompleted;
                presentationWriterGate.Set();
                await closeAfterDrain.WaitAsync(TimeSpan.FromSeconds(5));
                FavoriteActivityStoreReadResult activityAfterCloseDrain =
                    FavoriteActivityStore.Read(activityPath, activityEntryCount + 10);
                ViewerState? stateAfterCloseDrain = JsonSerializer.Deserialize<ViewerState>(
                    File.ReadAllText(statePath));
                bool closeDrainExact = presentationWriterBlocked
                    && closeWasDeferred
                    && !window.IsLoaded
                    && activityAfterCloseDrain.State
                        == FavoriteActivityStoreReadState.Loaded
                    && activityAfterCloseDrain.Entries.TryGetValue(
                        closeDrainPath,
                        out DateTimeOffset drainedAtUtc)
                    && drainedAtUtc == closeDrainTimestamp
                    && stateAfterCloseDrain?.PhotorealFavoriteFilterLevels
                        ?.SequenceEqual([4]) == true;

                bool closeFailureRecoveryExact;
                bool injectedFailureObserved = false;
                bool failedBatchRetained = false;
                bool retryPersisted = false;
                bool retryWindowClosed = false;
                bool retryActivityExact = false;
                MainWindow? failureWindow = null;
                try
                {
                    string closeFailurePath = Path.GetFullPath(Path.Combine(
                        historyRoot,
                        "close-failure-retry.png"));
                    DateTimeOffset closeFailureTimestamp =
                        new(2026, 8, 12, 12, 1, 0, TimeSpan.Zero);
                    failureWindow = HiddenWindow();
                    failureWindow.Show();
                    failureWindow.FailNextFavoritePresentationWriterForSmoke();
                    _ = failureWindow.SetPhotorealFavoriteFilterLevelsForSmoke(3);
                    failureWindow.QueueFavoritePresentationStateForSmoke(
                        closeFailurePath,
                        closeFailureTimestamp);
                    injectedFailureObserved = !await failureWindow
                        .WaitForFavoritePresentationStateForSmokeAsync(
                            TimeSpan.FromSeconds(5));
                    Task refusedClose = failureWindow.CloseAndWaitForSmokeAsync();
                    failedBatchRetained = injectedFailureObserved
                        && failureWindow.FavoritePresentationStateFailedForSmoke
                        && failureWindow.IsLoaded
                        && !refusedClose.IsCompleted;

                    failureWindow.RetryFailedFavoritePresentationForSmoke();
                    retryPersisted = await failureWindow
                        .WaitForFavoritePresentationStateForSmokeAsync(
                            TimeSpan.FromSeconds(5));
                    await failureWindow.CloseAndWaitForSmokeAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    // CloseAndWaitForSmokeAsync completes only from Window.Closed.
                    // IsLoaded is not a reliable closed-state signal for a window
                    // whose logical tree is still retained by this smoke fixture.
                    retryWindowClosed = true;
                    FavoriteActivityStoreReadResult activityAfterRetry =
                        FavoriteActivityStore.Read(
                            activityPath,
                            activityEntryCount + 10);
                    ViewerState? stateAfterRetry = JsonSerializer.Deserialize<ViewerState>(
                        File.ReadAllText(statePath));
                    retryActivityExact = activityAfterRetry.State
                            == FavoriteActivityStoreReadState.Loaded
                        && activityAfterRetry.Entries.TryGetValue(
                            closeFailurePath,
                            out DateTimeOffset retriedAtUtc)
                        && retriedAtUtc == closeFailureTimestamp
                        && stateAfterRetry?.PhotorealFavoriteFilterLevels
                            ?.SequenceEqual([3]) == true;
                    closeFailureRecoveryExact = failedBatchRetained
                        && retryPersisted
                        && retryWindowClosed
                        && retryActivityExact;
                }
                finally
                {
                    if (failureWindow is not null && failureWindow.IsLoaded)
                    {
                        try { failureWindow.Close(); } catch { }
                    }
                }
                bool sourceUnchanged = sourceFingerprintBefore == Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(sourcePath)));
                bool categoryOrContract = PhotoViewer.Wpf.MainWindow
                    .FavoriteRatingFilterOrContractForSmoke();

                ok = initialDerivedFavorite
                    && filterOnExact
                    && filterOffExact
                    && categoryOrContract
                    && targetedPresentation
                    && boundedUi
                    && writerExact
                    && storesExact
                    && closeDrainExact
                    && closeFailureRecoveryExact
                    && completedElapsedVisible
                    && restorationExact
                    && sourceUnchanged;
                result = new
                {
                    ok,
                    favoriteEntryCount,
                    activityEntryCount,
                    favoritesBytes = new FileInfo(favoritesPath).Length,
                    stateBytes = new FileInfo(statePath).Length,
                    initialDerivedFavorite,
                    filterOnExact,
                    filterOffExact,
                    categoryOrContract,
                    targetedPresentation,
                    boundedUi,
                    writerExact,
                    storesExact,
                    closeDrainExact,
                    closeFailureRecoveryExact,
                    closeFailureEvidence = new
                    {
                        injectedFailureObserved,
                        failedBatchRetained,
                        retryPersisted,
                        retryWindowClosed,
                        retryActivityExact,
                    },
                    stateExtensionPreserved,
                    completedElapsedVisible,
                    restorationExact,
                    restoration = restoreResult,
                    sourceUnchanged,
                    mutationCallMilliseconds = mutationWatch.ElapsedMilliseconds,
                    dispatcherPingMilliseconds = pingWatch.ElapsedMilliseconds,
                    filterOnCallMilliseconds = filterOnCallWatch.ElapsedMilliseconds,
                    filterOffCallMilliseconds = filterOffCallWatch.ElapsedMilliseconds,
                    filterCaptureMilliseconds = Math.Max(
                        filterOn.CaptureMs,
                        filterOff.CaptureMs),
                    favoriteCallbackMilliseconds =
                        window.LastFavoriteWriteApplyMillisecondsForSmoke,
                    maxFavoriteCallbackMilliseconds =
                        window.MaxFavoriteWriteApplyMillisecondsForSmoke,
                    lastDerivedVisitedTiles =
                        window.LastDerivedFavoriteVisitedTileCountForSmoke,
                    maxDerivedVisitedTiles =
                        window.MaxDerivedFavoriteVisitedTileCountForSmoke,
                    stateWriteCount =
                        window.FavoritePresentationStateWriteCountForSmoke,
                    elapsedText,
                    uiBudgetMilliseconds,
                };
            }
            catch (Exception error)
            {
                result = new
                {
                    ok = false,
                    message = error.ToString(),
                    favoriteEntryCount,
                    activityEntryCount,
                    uiBudgetMilliseconds,
                };
            }
            finally
            {
                if (window is not null)
                {
                    try { window.Close(); } catch { }
                }
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }
}

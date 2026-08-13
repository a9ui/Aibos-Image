using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoFavoriteSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-wpf-video-favorite-").FullName;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            MainWindow? reload = null;
            var previousEnvironment = new Dictionary<string, string?>(
                StringComparer.Ordinal);
            bool? previousHighContrastForSmoke = _highContrastForSmoke;
            bool ok = false;
            string failure = "";
            bool missingStateUnselected = false;
            bool maximumAndInvalidExclusion = false;
            bool originalUnratedFilter = false;
            bool categoryOr = false;
            bool modalOutputFavorite = false;
            bool modalPinnedFavoriteSource = false;
            bool optimisticRetryRollback = false;
            bool persistenceReload = false;
            bool layoutNotifications = false;
            bool surfaceContract = false;
            bool levelToggleRoundTrip = false;
            bool badgeVisualContract = false;
            bool contrastContract = false;
            bool highContrastContract = false;
            bool favoriteKeysRetained = false;
            FavoriteBadgeVisualSmokeSnapshot visualDiagnostic = default;
            try
            {
                string retiredFavoriteLabel = new(
                    ['\u30D5', '\u30A1', '\u30DC']);
                string imageRoot = Path.Combine(smokeRoot, "images");
                string storesRoot = Path.Combine(smokeRoot, "stores");
                string enhancementRoot = Path.Combine(storesRoot, "enhance");
                string outputRoot = Path.Combine(enhancementRoot, "outputs");
                string videoRoot = Path.Combine(outputRoot, "Videos");
                string photorealRoot = Path.Combine(outputRoot, "Photorealized");
                Directory.CreateDirectory(imageRoot);
                Directory.CreateDirectory(videoRoot);
                Directory.CreateDirectory(photorealRoot);

                string sourceA = Path.Combine(imageRoot, "a.png");
                string sourceB = Path.Combine(imageRoot, "b.png");
                string sourceWithoutVideo = Path.Combine(imageRoot, "no-video.png");
                string invalidSource = Path.Combine(imageRoot, "invalid.png");
                WriteSmokePng(sourceA, 64, 48, Colors.SteelBlue);
                WriteSmokePng(sourceB, 64, 48, Colors.DarkSeaGreen);
                WriteSmokePng(sourceWithoutVideo, 64, 48, Colors.DarkGoldenrod);
                WriteSmokePng(invalidSource, 64, 48, Colors.IndianRed);

                string photorealA = Path.Combine(photorealRoot, "photoreal-a.png");
                string photorealB = Path.Combine(photorealRoot, "photoreal-b.png");
                File.Copy(sourceA, photorealA);
                File.Copy(sourceB, photorealB);

                string videoAOlder = Path.Combine(
                    videoRoot,
                    "video-older__a__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__22467069a81f.mp4");
                string videoANewest = Path.Combine(
                    videoRoot,
                    "video-newest__photoreal-a__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__f102bafe68e9.mp4");
                string videoBOlder = Path.Combine(
                    videoRoot,
                    "video-b-older__b__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__22467069a81f.mp4");
                string videoBNewest = Path.Combine(
                    videoRoot,
                    "video-b-newest__photoreal-b__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__f102bafe68e9.mp4");
                string missingVideo = Path.Combine(
                    videoRoot,
                    "video-missing__a__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__22467069a81f.mp4");
                string invalidVideo = Path.Combine(
                    videoRoot,
                    "video-invalid__a__aaaaaaaaaaaaaaaa__wan22-ti2v-5b-normal-v1__22467069a81f.mp4");
                static void WriteMinimalMp4(string path)
                {
                    File.WriteAllBytes(
                        path,
                        [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109]);
                }
                WriteMinimalMp4(videoAOlder);
                WriteMinimalMp4(videoANewest);
                WriteMinimalMp4(videoBOlder);
                WriteMinimalMp4(videoBNewest);
                WriteMinimalMp4(invalidVideo);

                string jobsAPath = Path.Combine(smokeRoot, "jobs-a.json");
                string jobsBPath = Path.Combine(smokeRoot, "jobs-b.json");
                string jobsPath = Path.Combine(enhancementRoot, "jobs.json");
                string unusedUpscaleOutputA = Path.Combine(outputRoot, "unused-a.png");
                string unusedUpscaleOutputB = Path.Combine(outputRoot, "unused-b.png");
                WriteEnhancementOperationJobsFixture(
                    jobsAPath,
                    sourceA,
                    unusedUpscaleOutputA,
                    sourceA,
                    photorealA,
                    sourceA,
                    videoANewest,
                    videoAOlder,
                    invalidSource,
                    Path.Combine(outputRoot, "missing-a.png"),
                    Path.Combine(imageRoot, "missing-a.png"));
                WriteEnhancementOperationJobsFixture(
                    jobsBPath,
                    sourceB,
                    unusedUpscaleOutputB,
                    sourceB,
                    photorealB,
                    sourceB,
                    videoBNewest,
                    videoBOlder,
                    invalidSource,
                    Path.Combine(outputRoot, "missing-b.png"),
                    Path.Combine(imageRoot, "missing-b.png"));

                static JsonObject ReadJob(JsonObject root, string id)
                {
                    JsonArray jobs = root["jobs"]?.AsArray()
                        ?? throw new InvalidDataException("fixture jobs are missing");
                    return jobs.OfType<JsonObject>().First(job => string.Equals(
                        job["id"]?.GetValue<string>(),
                        id,
                        StringComparison.Ordinal));
                }

                JsonObject fixtureA = JsonNode.Parse(
                    File.ReadAllText(jobsAPath))?.AsObject()
                    ?? throw new InvalidDataException("fixture A is invalid");
                JsonObject fixtureB = JsonNode.Parse(
                    File.ReadAllText(jobsBPath))?.AsObject()
                    ?? throw new InvalidDataException("fixture B is invalid");
                var jobs = new JsonArray();
                jobs.Add(ReadJob(fixtureA, "photoreal-ok").DeepClone());
                JsonObject aOlder = (JsonObject)ReadJob(
                    fixtureA,
                    "video-older").DeepClone();
                JsonObject aNewest = (JsonObject)ReadJob(
                    fixtureA,
                    "video-newest").DeepClone();
                jobs.Add(aOlder);
                jobs.Add(aNewest);

                JsonObject bPhotoreal = (JsonObject)ReadJob(
                    fixtureB,
                    "photoreal-ok").DeepClone();
                bPhotoreal["id"] = "photoreal-b";
                JsonObject bOlder = (JsonObject)ReadJob(
                    fixtureB,
                    "video-older").DeepClone();
                bOlder["id"] = "video-b-older";
                JsonObject bNewest = (JsonObject)ReadJob(
                    fixtureB,
                    "video-newest").DeepClone();
                bNewest["id"] = "video-b-newest";
                bNewest["sourceProducerJobId"] = "photoreal-b";
                jobs.Add(bPhotoreal);
                jobs.Add(bOlder);
                jobs.Add(bNewest);

                JsonObject missing = (JsonObject)aOlder.DeepClone();
                missing["id"] = "video-missing";
                missing["outputPath"] = missingVideo;
                jobs.Add(missing);
                JsonObject invalid = (JsonObject)aOlder.DeepClone();
                invalid["id"] = "video-invalid";
                invalid["outputPath"] = invalidVideo;
                JsonObject invalidSignature = invalid["sourceSignature"]?.AsObject()
                    ?? throw new InvalidDataException("invalid fixture signature is missing");
                invalidSignature["size"] = new FileInfo(sourceA).Length + 1;
                jobs.Add(invalid);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
                File.WriteAllText(
                    jobsPath,
                    new JsonObject
                    {
                        ["version"] = 1,
                        ["jobs"] = jobs,
                    }.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }));

                string statePath = Path.Combine(storesRoot, "state.json");
                string favoritesPath = Path.Combine(storesRoot, "favorites.json");
                var favoriteSeed = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [sourceA] = 1,
                    [photorealA] = 2,
                    [photorealB] = 4,
                    [videoAOlder] = 1,
                    [videoANewest] = 3,
                    [missingVideo] = 5,
                    [invalidVideo] = 5,
                };
                Directory.CreateDirectory(storesRoot);
                File.WriteAllText(
                    favoritesPath,
                    JsonSerializer.Serialize(
                        favoriteSeed,
                        new JsonSerializerOptions { WriteIndented = true }));

                var environment = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
                    ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = favoritesPath,
                    ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storesRoot, "seen.json"),
                    ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storesRoot, "recent.json"),
                    ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storesRoot, "settings.json"),
                    ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storesRoot, "albums.json"),
                    ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storesRoot, "search.json"),
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputRoot,
                    ["PVU_ENHANCE_OUTPUT_ROOT"] = outputRoot,
                    ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storesRoot, "metadata-index"),
                };
                foreach ((string name, string value) in environment)
                {
                    previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, value);
                }

                window = new MainWindow();
                missingStateUnselected =
                    window.VideoFavoriteFilterLevelsForSmoke.Count == 0;
                window.Show();
                await window.LoadFolderSetAsync([imageRoot], commitRecent: false);
                string fileA = Path.GetFileName(sourceA);
                string fileB = Path.GetFileName(sourceB);
                string fileWithoutVideo = Path.GetFileName(sourceWithoutVideo);

                maximumAndInvalidExclusion =
                    window.VideoFavoriteLevelForFileForSmoke(fileA) == 3
                    && window.VideoFavoriteBadgeForFileForSmoke(fileA)
                    && window.VideoFavoriteLevelForFileForSmoke(fileB) == 0
                    && !window.VideoFavoriteBadgeForFileForSmoke(fileB)
                    && !window.VideoGeneratedForFileForSmoke(fileWithoutVideo)
                    && FavoriteFileContainsPath(favoritesPath, missingVideo)
                    && FavoriteFileContainsPath(favoritesPath, invalidVideo);

                window.SetUnfavoriteOnlyFilterForSmoke(true);
                originalUnratedFilter =
                    window.FilteredFileNamesForSmoke().ToHashSet(
                            StringComparer.OrdinalIgnoreCase)
                        .SetEquals([fileB, fileWithoutVideo, Path.GetFileName(invalidSource)])
                    && window.PhotorealFavoriteLevelForFileForSmoke(fileB) == 4;
                window.SetUnfavoriteOnlyFilterForSmoke(false);
                window.SetFavoriteOnlyFilterForSmoke(true);
                bool originalOneSelected =
                    window.SetFavoriteFilterLevelsForSmoke(1);
                bool photorealFourSelected =
                    window.SetPhotorealFavoriteFilterLevelsForSmoke(4);
                categoryOr = originalOneSelected
                    && photorealFourSelected
                    && window.FilteredFileNamesForSmoke().ToHashSet(
                            StringComparer.OrdinalIgnoreCase)
                        .SetEquals([fileA, fileB])
                    && !window.FilteredFileNamesForSmoke().Contains(
                        fileWithoutVideo,
                        StringComparer.OrdinalIgnoreCase);
                window.SetFavoriteOnlyFilterForSmoke(false);
                _ = window.SetFavoriteFilterLevelsForSmoke();
                _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                _ = window.SetVideoFavoriteFilterLevelsForSmoke();

                bool japaneseApplied = window.SetUiLanguageForSmoke(
                    UiLanguageResources.Japanese);
                bool shortJapaneseLabels = japaneseApplied
                    && window.FavoriteFilterGroupTitleForSmoke == "お気に入り"
                    && window.OriginalFavoriteFilterTitleForSmoke == "元画像"
                    && window.PhotorealFavoriteFilterTitleForSmoke == "実写"
                    && window.VideoFavoriteFilterTitleForSmoke == "動画"
                    && !string.Join(
                            " ",
                            window.FavoriteFilterGroupTitleForSmoke,
                            window.OriginalFavoriteFilterTitleForSmoke,
                            window.PhotorealFavoriteFilterTitleForSmoke,
                            window.VideoFavoriteFilterTitleForSmoke)
                        .Contains(retiredFavoriteLabel, StringComparison.Ordinal);
                surfaceContract = shortJapaneseLabels
                    && window.FavoriteFilterSurfaceContractForSmoke;
                levelToggleRoundTrip = new[] { "original", "photoreal", "video" }
                    .All(category =>
                        window.ToggleFavoriteLevelFilterForSmoke(category, 3)
                        && window.ToggleFavoriteLevelFilterForSmoke(category, 3));
                _ = window.SetUiLanguageForSmoke(UiLanguageResources.English);

                window.EnableModalVideoTransportStubForSmoke();
                bool selected = window.SelectFileNameForSmoke(fileA);
                bool opened = selected && window.OpenModalForSmoke();
                bool videoSelected = opened
                    && window.SelectModalVideoJobForSmoke("video-newest");
                modalOutputFavorite = videoSelected
                    && window.ModalFavoriteLevelForSmoke == 3
                    && window.SelectedFavoriteLevelForSmoke == 1
                    && window.AdjustModalFavoriteForSmoke(1)
                    && window.ModalFavoriteLevelForSmoke == 4
                    && window.SelectedFavoriteLevelForSmoke == 1
                    && window.VideoFavoriteLevelForFileForSmoke(fileA) == 4
                    && window.ModalVideoFavoriteActiveSurfaceForSmoke
                    && ReadFavoriteLevel(favoritesPath, videoANewest) == 4
                    && ReadFavoriteLevel(favoritesPath, sourceA) == 1;
                window.CloseModalForSmoke();

                // The production zoom path refreshes layout bindings only for
                // realized cards. Realize this exact fixture before counting
                // badge notifications so virtualization timing cannot make the
                // contract nondeterministic.
                _ = window.FavoriteBadgeVisualsForFileForSmoke(fileA);
                await Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.Render);
                Tile? tileA = window.TileForFileForSmoke(fileA);
                int layoutNotificationCount = 0;
                PropertyChangedEventHandler layoutHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(Tile.ShowVideoFavoriteBadge))
                        layoutNotificationCount++;
                };
                if (tileA is not null)
                    tileA.PropertyChanged += layoutHandler;
                try
                {
                    bool small = window.SetGridZoomForSmoke(20)
                        && tileA?.ShowVideoFavoriteBadge == false;
                    bool large = window.SetGridZoomForSmoke(200)
                        && tileA?.ShowVideoFavoriteBadge == true;
                    layoutNotifications = small
                        && large
                        && layoutNotificationCount >= 2;
                }
                finally
                {
                    if (tileA is not null)
                        tileA.PropertyChanged -= layoutHandler;
                }

                FavoriteBadgeVisualSmokeSnapshot visual =
                    window.FavoriteBadgeVisualsForFileForSmoke(fileA);
                visualDiagnostic = visual;
                badgeVisualContract = visual.PhotorealVisible
                    && visual.VideoVisible
                    && visual.PhotorealAutomationName ==
                        "Photoreal Favorite level 2"
                    && visual.VideoAutomationName == "Video Favorite level 4"
                    && !visual.PhotorealAutomationName.Contains(
                        retiredFavoriteLabel,
                        StringComparison.Ordinal)
                    && !visual.VideoAutomationName.Contains(
                        retiredFavoriteLabel,
                        StringComparison.Ordinal)
                    && visual.PhotorealBorderColor ==
                        ResourceColor("PhotorealFavoriteColor").ToString()
                    && visual.VideoBorderColor ==
                        ResourceColor("VideoFavoriteColor").ToString()
                    && visual.PhotorealBackgroundColor ==
                        ResourceColor("PhotorealFavoriteBackgroundColor").ToString()
                    && visual.VideoBackgroundColor ==
                        ResourceColor("VideoFavoriteBackgroundColor").ToString()
                    && ResourceColor("PhotorealFavoriteColor") !=
                        ResourceColor("VideoFavoriteColor");

                static double RelativeLuminance(Color color)
                {
                    static double Linear(byte channel)
                    {
                        double value = channel / 255d;
                        return value <= 0.04045
                            ? value / 12.92
                            : Math.Pow((value + 0.055) / 1.055, 2.4);
                    }
                    return 0.2126 * Linear(color.R)
                        + 0.7152 * Linear(color.G)
                        + 0.0722 * Linear(color.B);
                }
                static double Contrast(Color left, Color right)
                {
                    double a = RelativeLuminance(left);
                    double b = RelativeLuminance(right);
                    return (Math.Max(a, b) + 0.05)
                        / (Math.Min(a, b) + 0.05);
                }
                contrastContract =
                    Contrast(
                        ResourceColor("PhotorealFavoriteTextColor"),
                        ResourceColor("PhotorealFavoriteBackgroundColor")) >= 4.5
                    && Contrast(
                        ResourceColor("PhotorealFavoriteColor"),
                        ResourceColor("PhotorealFavoriteBackgroundColor")) >= 3
                    && Contrast(
                        ResourceColor("VideoFavoriteTextColor"),
                        ResourceColor("VideoFavoriteBackgroundColor")) >= 4.5
                    && Contrast(
                        ResourceColor("VideoFavoriteColor"),
                        ResourceColor("VideoFavoriteBackgroundColor")) >= 3;

                Color standardBlue = ResourceColor("PhotorealFavoriteColor");
                Color standardPurple = ResourceColor("VideoFavoriteColor");
                ApplyHighContrastPalette(true);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                FavoriteBadgeVisualSmokeSnapshot highContrastVisual =
                    window.FavoriteBadgeVisualsForFileForSmoke(fileA);
                highContrastContract = _highContrastPaletteApplied
                    && ResourceColor("PhotorealFavoriteColor") ==
                        SystemColors.HotTrackColor
                    && ResourceColor("VideoFavoriteColor") ==
                        SystemColors.HotTrackColor
                    && ResourceColor("PhotorealFavoriteTextColor") ==
                        SystemColors.WindowTextColor
                    && ResourceColor("VideoFavoriteTextColor") ==
                        SystemColors.WindowTextColor
                    && ResourceColor("PhotorealFavoriteBackgroundColor") ==
                        SystemColors.ControlColor
                    && ResourceColor("VideoFavoriteBackgroundColor") ==
                        SystemColors.ControlColor
                    && highContrastVisual.PhotorealBorderColor ==
                        SystemColors.HotTrackColor.ToString()
                    && highContrastVisual.VideoBorderColor ==
                        SystemColors.HotTrackColor.ToString();
                ApplyHighContrastPalette(false);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                highContrastContract = highContrastContract
                    && ResourceColor("PhotorealFavoriteColor") == standardBlue
                    && ResourceColor("VideoFavoriteColor") == standardPurple;

                bool filterFiveSet = window.SetVideoFavoriteFilterLevelsForSmoke(5);
                window.SetFavoriteOnlyFilterForSmoke(true);
                bool initiallyExcluded = !window.FilteredFileNamesForSmoke().Contains(
                    fileA,
                    StringComparer.OrdinalIgnoreCase);
                window.ForceSharedStoreWritersForSmoke();
                window.FailNextFavoriteWriterForSmoke();
                bool accepted = window.SetIndependentFavoriteLevelForSmoke(
                    videoANewest,
                    5);
                bool optimisticBadge = accepted
                    && window.VideoFavoriteLevelForFileForSmoke(fileA) == 5
                    && window.VideoFavoriteBadgeForFileForSmoke(fileA);
                _ = await window.PendingCatalogProjectionForSmoke();
                bool optimisticFilter = window.FilteredFileNamesForSmoke().Contains(
                    fileA,
                    StringComparer.OrdinalIgnoreCase);
                SharedWriteStatus[] failedStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                _ = await window.PendingCatalogProjectionForSmoke();
                bool rolledBack = failedStatuses.Contains(SharedWriteStatus.Failed)
                    && window.FailedFavoriteRetryPendingForSmoke
                    && window.VideoFavoriteLevelForFileForSmoke(fileA) == 4
                    && !window.FilteredFileNamesForSmoke().Contains(
                        fileA,
                        StringComparer.OrdinalIgnoreCase);
                window.RetryFailedFavoriteForSmoke();
                bool retryImmediate =
                    window.VideoFavoriteLevelForFileForSmoke(fileA) == 5;
                _ = await window.PendingCatalogProjectionForSmoke();
                bool retryFilterImmediate = window.FilteredFileNamesForSmoke().Contains(
                    fileA,
                    StringComparer.OrdinalIgnoreCase);
                SharedWriteStatus[] retryStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                optimisticRetryRollback = filterFiveSet
                    && initiallyExcluded
                    && optimisticBadge
                    && optimisticFilter
                    && rolledBack
                    && retryImmediate
                    && retryFilterImmediate
                    && retryStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && !window.FailedFavoriteRetryPendingForSmoke
                    && ReadFavoriteLevel(favoritesPath, videoANewest) == 5;

                // Reproduce the intermittent production drift directly: the
                // modal remains on A while the gallery selection moves to B.
                // A Favorite click must mutate the displayed A, must not reopen
                // the modal on B, and must not start another modal decode.
                window.SetFavoriteOnlyFilterForSmoke(false);
                _ = window.SetVideoFavoriteFilterLevelsForSmoke();
                _ = await window.PendingCatalogProjectionForSmoke();
                bool pinnedSourceSelected = window.SelectFileNameForSmoke(fileA);
                window.ConfigureImageDecodeDelaysForSmoke(
                    previewMilliseconds: 0,
                    modalMilliseconds: 150);
                bool pinnedModalOpened = pinnedSourceSelected
                    && window.OpenModalForSmoke()
                    && window.SelectModalOriginalVersionForSmoke();
                string? pinnedSourcePath = window.ModalSourcePathForSmoke;
                string? pinnedDisplayPath = window.ModalDisplayPathForSmoke;
                int pinnedDecodeStarts = window.ModalDecodeStartCountForSmoke;
                Task<bool> pinnedDecodeTask = pinnedModalOpened
                    ? window.WaitForModalFullDecodeForSmokeAsync()
                    : Task.FromResult(false);
                bool backgroundSelectionMoved = pinnedModalOpened
                    && window.SelectFileNameForSmoke(fileB);
                bool pinnedModalDecoded = await pinnedDecodeTask;
                window.ConfigureImageDecodeDelaysForSmoke(0, 0);
                bool pinnedVideoBoardOpened = backgroundSelectionMoved
                    && pinnedModalDecoded
                    && window.OpenVideoGenerationBoardForSmoke("original");
                bool pinnedVideoSource = pinnedVideoBoardOpened
                    && string.Equals(
                        window.VideoSourceIdentityForSmoke,
                        sourceA,
                        StringComparison.OrdinalIgnoreCase);
                window.CloseVideoGenerationBoardForSmoke();
                bool displayedFavoriteRaised = pinnedVideoSource
                    && window.AdjustModalFavoriteForSmoke(1);
                bool pinnedAfterFavorite = displayedFavoriteRaised
                    && string.Equals(
                        window.ModalSourcePathForSmoke,
                        pinnedSourcePath,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        window.ModalDisplayPathForSmoke,
                        pinnedDisplayPath,
                        StringComparison.OrdinalIgnoreCase)
                    && window.ModalDecodeStartCountForSmoke == pinnedDecodeStarts
                    && window.FavoriteLevelForFileForSmoke(fileA) == 2
                    && window.FavoriteLevelForFileForSmoke(fileB) == 0
                    && window.SelectedFavoriteLevelForSmoke == 0;
                bool pinnedFavoriteRestored = pinnedAfterFavorite
                    && window.AdjustModalFavoriteForSmoke(-1)
                    && window.FavoriteLevelForFileForSmoke(fileA) == 1
                    && window.FavoriteLevelForFileForSmoke(fileB) == 0
                    && string.Equals(
                        window.ModalSourcePathForSmoke,
                        pinnedSourcePath,
                        StringComparison.OrdinalIgnoreCase)
                    && window.ModalDecodeStartCountForSmoke == pinnedDecodeStarts;
                SharedWriteStatus[] pinnedFavoriteStatuses =
                    await window.DrainSharedStoreWritersForSmokeAsync();
                modalPinnedFavoriteSource = pinnedAfterFavorite
                    && pinnedFavoriteRestored
                    && pinnedFavoriteStatuses.All(static status =>
                        status == SharedWriteStatus.Succeeded)
                    && !window.FavoriteWriterPendingForSmoke;
                window.CloseModalForSmoke();
                _ = window.SetVideoFavoriteFilterLevelsForSmoke(5);
                window.SetFavoriteOnlyFilterForSmoke(true);
                _ = await window.PendingCatalogProjectionForSmoke();

                _ = await window.WaitForFavoritePresentationStateForSmokeAsync(
                    TimeSpan.FromSeconds(10));
                window.FlushStateForSmoke();
                ViewerState? persisted = JsonSerializer.Deserialize<ViewerState>(
                    File.ReadAllText(statePath));
                bool statePersisted = persisted?.VideoFavoriteFilterLevels
                    ?.SequenceEqual([5]) == true
                    && persisted.ShowFavoritesOnly;
                reload = new MainWindow();
                bool filterRestoredBeforeLoad = reload.VideoFavoriteFilterLevelsForSmoke
                    .SequenceEqual([5]);
                reload.Show();
                await reload.LoadFolderSetAsync([imageRoot], commitRecent: false);
                persistenceReload = statePersisted
                    && filterRestoredBeforeLoad
                    && reload.VideoFavoriteLevelForFileForSmoke(fileA) == 5
                    && reload.VideoFavoriteBadgeForFileForSmoke(fileA)
                    && reload.FilteredFileNamesForSmoke().SequenceEqual(
                        [fileA],
                        StringComparer.OrdinalIgnoreCase);
                favoriteKeysRetained =
                    FavoriteFileContainsPath(favoritesPath, missingVideo)
                    && FavoriteFileContainsPath(favoritesPath, invalidVideo)
                    && ReadFavoriteLevel(favoritesPath, missingVideo) == 5
                    && ReadFavoriteLevel(favoritesPath, invalidVideo) == 5;

                ok = missingStateUnselected
                    && maximumAndInvalidExclusion
                    && originalUnratedFilter
                    && categoryOr
                    && modalOutputFavorite
                    && modalPinnedFavoriteSource
                    && optimisticRetryRollback
                    && persistenceReload
                    && layoutNotifications
                    && surfaceContract
                    && levelToggleRoundTrip
                    && badgeVisualContract
                    && contrastContract
                    && highContrastContract
                    && favoriteKeysRetained
                    && NoPersistenceResidue(storesRoot);
                if (!ok)
                {
                    failure = "Video Favorite maximum/filter/mutation/UI contract failed.";
                }
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
            finally
            {
                try { reload?.Close(); } catch { }
                try { window?.Close(); } catch { }
                _highContrastForSmoke = previousHighContrastForSmoke;
                ApplyAccessibilityPreferences();
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    new
                    {
                        ok,
                        message = ok ? "Video Favorite badge and filter contract passed." : failure,
                        missingStateUnselected,
                        maximumAndInvalidExclusion,
                        originalUnratedFilter,
                        categoryOr,
                        modalOutputFavorite,
                        modalPinnedFavoriteSource,
                        optimisticRetryRollback,
                        persistenceReload,
                        layoutNotifications,
                        surfaceContract,
                        levelToggleRoundTrip,
                        badgeVisualContract,
                        contrastContract,
                        highContrastContract,
                        favoriteKeysRetained,
                        visualDiagnostic,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }
}

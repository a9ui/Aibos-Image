using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureImageDragInSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "aibos-wpf-image-drag-in-" + Guid.NewGuid().ToString("N"));
        string catalogFolder = Path.Combine(smokeRoot, "catalog");
        string externalFolder = Path.Combine(smokeRoot, "external");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string catalogImage = Path.Combine(catalogFolder, "catalog.png");
        string externalA = Path.Combine(externalFolder, "external-a.png");
        string externalB = Path.Combine(externalFolder, "external-b.png");
        string externalReplacement = Path.Combine(externalFolder, "external-replacement.png");
        string externalReplacementCandidate = Path.Combine(externalFolder, "external-replacement-candidate.png");
        string externalRaceA = Path.Combine(externalFolder, "external-race-a.png");
        string externalRaceB = Path.Combine(externalFolder, "external-race-b.png");
        string externalHealthChange = Path.Combine(externalFolder, "external-health-change.png");
        string externalProductionRetire = Path.Combine(
            externalFolder,
            "external-production-retire.png");
        string externalProductionGrowth = Path.Combine(
            externalFolder,
            "external-production-growth.png");
        string malformedImage = Path.Combine(externalFolder, "malformed.png");
        string oversizedDimensions = Path.Combine(externalFolder, "oversized-dimensions.png");
        string oversizedPayload = Path.Combine(externalFolder, "oversized-payload.png");

        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storeRoot, "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storeRoot, "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storeRoot, "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storeRoot, "recent-folders.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storeRoot, "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storeRoot, "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storeRoot, "search-history.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storeRoot, "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(storeRoot, "enhance", "jobs.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = Path.Combine(storeRoot, "outputs"),
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        object result = new { ok = false, message = "Smoke did not complete." };
        bool ok = false;
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(catalogFolder);
            Directory.CreateDirectory(externalFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(
                environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!)!);
            WriteSmokePng(catalogImage, 48, 32, Color.FromRgb(48, 96, 180));
            WriteSmokePng(externalA, 64, 40, Color.FromRgb(180, 80, 80));
            WriteSmokePng(externalB, 56, 36, Color.FromRgb(80, 170, 120));
            WriteSmokePng(externalReplacement, 52, 34, Color.FromRgb(170, 110, 55));
            WriteSmokePng(externalRaceA, 50, 38, Color.FromRgb(65, 115, 185));
            WriteSmokePng(externalRaceB, 46, 42, Color.FromRgb(145, 75, 175));
            WriteSmokePng(externalHealthChange, 58, 44, Color.FromRgb(155, 125, 45));
            WriteSmokePng(
                externalProductionRetire,
                54,
                40,
                Color.FromRgb(75, 135, 175));
            WriteSmokePng(
                externalProductionGrowth,
                60,
                46,
                Color.FromRgb(175, 105, 75));
            File.WriteAllText(malformedImage, "not an image");
            WriteSmokePng(
                oversizedDimensions,
                16_385,
                1,
                Color.FromRgb(96, 96, 96));
            using (var oversized = new FileStream(
                oversizedPayload,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                oversized.SetLength((512L * 1024 * 1024) + 1);
            }
            File.WriteAllText(
                environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!,
                "{\"jobs\":[]}");
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.LoadFolderAsync(catalogFolder);
                    bool catalogSelected = window.SelectFileNameForSmoke(
                        Path.GetFileName(catalogImage));
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    int catalogCountBefore = window.CatalogCountForSmoke;
                    int projectionCountBefore = window.FilteredCountForSmoke;
                    string? selectedBefore = window.SelectedPathForSmoke;
                    string[] folderSetBefore = window.CurrentFolderSetForSmoke.ToArray();
                    string stateBefore = FileFingerprint(
                        environment["PHOTOVIEWER_WPF_STATE_PATH"]!);
                    string recentBefore = FileFingerprint(
                        environment["PHOTOVIEWER_WPF_RECENT_PATH"]!);
                    string jobsBefore = FileFingerprint(
                        environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!);
                    string externalABefore = FileFingerprint(externalA);
                    string externalBBefore = FileFingerprint(externalB);
                    string[] durableStorePaths =
                    [
                        environment["PHOTOVIEWER_WPF_STATE_PATH"]!,
                        environment["PHOTOVIEWER_WPF_FAVORITES_PATH"]!,
                        environment["PHOTOVIEWER_WPF_SEEN_PATH"]!,
                        environment["PHOTOVIEWER_WPF_RECENT_PATH"]!,
                        environment["PHOTOVIEWER_WPF_SETTINGS_PATH"]!,
                        environment["PHOTOVIEWER_WPF_ALBUMS_PATH"]!,
                        environment["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"]!,
                        environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!,
                    ];
                    string StoreSetFingerprint()
                        => string.Join(
                            "|",
                            durableStorePaths.Select(path =>
                                $"{path.Length}:{FileFingerprint(path)}"));
                    string pendingInboxPath = Path.Combine(
                        Path.GetDirectoryName(environment[
                            "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!)!,
                        "enqueue-inbox",
                        "v1",
                        "pending");
                    int PendingReservationCount()
                        => Directory.Exists(pendingInboxPath)
                            ? Directory.GetFiles(
                                pendingInboxPath,
                                "*.json",
                                SearchOption.TopDirectoryOnly).Length
                            : 0;

                    int companionCalls = 0;
                    window.ConfigureModalEnhancementForSmoke((_, _) =>
                    {
                        Interlocked.Increment(ref companionCalls);
                        return Task.FromResult(new HttpResponseMessage(
                            HttpStatusCode.ServiceUnavailable));
                    });
                    window.ForceSharedStoreWritersForSmoke();

                    ExternalImageDropSmokeSnapshot malformed =
                        await window.DropExternalImagesForSmokeAsync(
                            [malformedImage]);
                    ExternalImageDropSmokeSnapshot dimensions =
                        await window.DropExternalImagesForSmokeAsync(
                            [oversizedDimensions]);
                    ExternalImageDropSmokeSnapshot count101 =
                        await window.DropExternalImagesForSmokeAsync(
                            Enumerable.Repeat(externalA, 101));
                    ExternalImageDropSmokeSnapshot bytes512 =
                        await window.DropExternalImagesForSmokeAsync(
                            [oversizedPayload]);
                    ViewerDropClassificationSmokeSnapshot mixed =
                        window.ClassifyViewerDropForSmoke(
                            [catalogFolder, externalA]);
                    ViewerDropClassificationSmokeSnapshot folders =
                        window.ClassifyViewerDropForSmoke([catalogFolder]);

                    window.FailNextSeenWriterForSmoke();
                    ExternalImageDropSmokeSnapshot accepted =
                        await window.DropExternalImagesForSmokeAsync(
                            [externalB, externalA, externalB.ToUpperInvariant()]);
                    await Task.Delay(250);
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool seenRolledBack = window.SelectedUnseenForSmoke
                        && window.FailedSeenRetryPendingForSmoke;
                    window.RetryFailedSeenForSmoke();
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool seenRetryApplied = !window.SelectedUnseenForSmoke
                        && !window.FailedSeenRetryPendingForSmoke;

                    bool aiSourcesAvailable =
                        window.ExternalFileDropExplicitAiSourcesAvailableForSmoke;
                    int deleteBackendCalls = 0;
                    window.SetRecycleBinDeleteBackendForSmoke(_ =>
                    {
                        Interlocked.Increment(ref deleteBackendCalls);
                        return RecycleBinDeleteResult.Success;
                    });
                    bool deleteRequestRejected =
                        !window.RequestDeleteSelectedForSmoke()
                        && deleteBackendCalls == 0;

                    bool previousWrapped = window.NavigateModalForSmoke(-1)
                        && string.Equals(
                            window.SelectedPathForSmoke,
                            Path.GetFullPath(externalA),
                            StringComparison.OrdinalIgnoreCase);
                    bool nextWrapped = window.NavigateModalForSmoke(1)
                        && string.Equals(
                            window.SelectedPathForSmoke,
                            Path.GetFullPath(externalB),
                            StringComparison.OrdinalIgnoreCase);
                    string[] cohort = window.ExternalFileDropCohortPathsForSmoke;
                    string[] filmstrip = window.ModalFilmstripPathsForSmoke;
                    bool orderAndFilmstrip = cohort.SequenceEqual(
                            [Path.GetFullPath(externalB), Path.GetFullPath(externalA)],
                            StringComparer.OrdinalIgnoreCase)
                        && filmstrip.SequenceEqual(
                            cohort,
                            StringComparer.OrdinalIgnoreCase);

                    window.FailNextFavoriteWriterForSmoke();
                    bool favoriteQueued =
                        window.SetSelectedFavoriteLevelForSmoke(4);
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool favoriteRolledBack = favoriteQueued
                        && window.SelectedFavoriteLevelForSmoke == 0
                        && window.FailedFavoriteRetryPendingForSmoke;
                    window.RetryFailedFavoriteForSmoke();
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool favoriteRetryApplied =
                        window.SelectedFavoriteLevelForSmoke == 4
                        && !window.FailedFavoriteRetryPendingForSmoke;

                    bool passive = companionCalls == 0
                        && string.Equals(
                            jobsBefore,
                            FileFingerprint(environment[
                                "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!),
                            StringComparison.Ordinal);
                    bool stateUnchangedWhileOpen = string.Equals(
                            stateBefore,
                            FileFingerprint(environment[
                                "PHOTOVIEWER_WPF_STATE_PATH"]!),
                            StringComparison.Ordinal)
                        && string.Equals(
                            recentBefore,
                            FileFingerprint(environment[
                                "PHOTOVIEWER_WPF_RECENT_PATH"]!),
                            StringComparison.Ordinal)
                        && folderSetBefore.SequenceEqual(
                            window.CurrentFolderSetForSmoke,
                            StringComparer.OrdinalIgnoreCase);
                    bool sourceUntouched = string.Equals(
                            externalABefore,
                            FileFingerprint(externalA),
                            StringComparison.Ordinal)
                        && string.Equals(
                            externalBBefore,
                            FileFingerprint(externalB),
                            StringComparison.Ordinal);

                    window.CloseModalForSmoke();
                    bool closeRestored =
                        !window.ExternalFileDropSessionActiveForSmoke
                        && !window.ModalVisibleForSmoke
                        && window.CatalogCountForSmoke == catalogCountBefore
                        && window.FilteredCountForSmoke == projectionCountBefore
                        && string.Equals(
                            window.SelectedPathForSmoke,
                            selectedBefore,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            stateBefore,
                            FileFingerprint(environment[
                                "PHOTOVIEWER_WPF_STATE_PATH"]!),
                            StringComparison.Ordinal);

                    bool catalogProjectionPrepared =
                        window.PrepareCatalogTileForExternalFileDropProjectionSmoke(
                            catalogImage,
                            favoriteLevel: 3);
                    ExternalImageDropSmokeSnapshot filteredCatalogPathDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [catalogImage]);
                    Tile? filterCohortTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    bool originalFavoriteProjectionApplied =
                        window.SetFavoriteFilterLevelsForSmoke(3);
                    window.SetFavoriteOnlyFilterForSmoke(true);
                    Tile? originalFavoriteProjectionTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    window.SetFavoriteOnlyFilterForSmoke(false);
                    _ = window.SetFavoriteFilterLevelsForSmoke();
                    bool photorealFavoriteProjectionApplied =
                        window.SetPhotorealFavoriteFilterLevelsForSmoke(3);
                    Tile? photorealFavoriteProjectionTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    _ = window.SetPhotorealFavoriteFilterLevelsForSmoke();
                    bool videoFavoriteProjectionApplied =
                        window.SetVideoFavoriteFilterLevelsForSmoke(3);
                    Tile? videoFavoriteProjectionTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    _ = window.SetVideoFavoriteFilterLevelsForSmoke();
                    Tile? filteredProjectionTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    bool filtersSwappedProjectionTile =
                        originalFavoriteProjectionApplied
                        && photorealFavoriteProjectionApplied
                        && videoFavoriteProjectionApplied
                        && filterCohortTile is not null
                        && originalFavoriteProjectionTile is not null
                        && photorealFavoriteProjectionTile is not null
                        && videoFavoriteProjectionTile is not null
                        && filteredProjectionTile is not null
                        && !ReferenceEquals(
                            filterCohortTile,
                            originalFavoriteProjectionTile)
                        && ReferenceEquals(
                            originalFavoriteProjectionTile,
                            photorealFavoriteProjectionTile)
                        && ReferenceEquals(
                            photorealFavoriteProjectionTile,
                            videoFavoriteProjectionTile)
                        && ReferenceEquals(
                            videoFavoriteProjectionTile,
                            filteredProjectionTile);
                    int filterDeleteCallsBefore = deleteBackendCalls;
                    bool filteredProjectionDeleteRejected =
                        !window.ExternalFileDropDeleteEnabledForSmoke
                        && !window.RequestDeleteSelectedForSmoke()
                        && deleteBackendCalls == filterDeleteCallsBefore;
                    bool filteredProjectionAiSourcesAvailable =
                        window.ExternalFileDropSourceValidForSmoke
                        && window.ExternalFileDropExplicitAiSourcesAvailableForSmoke;

                    int filteredProjectionProbeCalls = 0;
                    int filteredProjectionPostCalls = 0;
                    var filteredProjectionHealthEntered =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    var filteredProjectionHealthRelease =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        if (request.Method == HttpMethod.Get
                            && request.RequestUri?.AbsolutePath.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal) == true)
                        {
                            filteredProjectionProbeCalls++;
                            filteredProjectionHealthEntered.TrySetResult(true);
                            await filteredProjectionHealthRelease.Task.WaitAsync(token);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    "{\"capabilities\":{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":1,\"backendGeneration\":\"json-v1\"}}}",
                                    System.Text.Encoding.UTF8,
                                    "application/json"),
                            };
                        }
                        filteredProjectionPostCalls++;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        };
                    });
                    int filteredProjectionPendingBefore =
                        PendingReservationCount();
                    string filteredProjectionStoresBefore =
                        StoreSetFingerprint();
                    Task<ExternalFileDropPrePublishSmokeSnapshot>
                        filteredProjectionPublish =
                            window.PublishExternalFileDropReservationForSmokeAsync(
                                filteredProjectionTile!);
                    await filteredProjectionHealthEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds(10));
                    window.CloseModalForSmoke();
                    filteredProjectionHealthRelease.TrySetResult(true);
                    ExternalFileDropPrePublishSmokeSnapshot
                        filteredProjectionPublishResult =
                            await filteredProjectionPublish;
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool catalogProjectionRestored =
                        window.RestoreCatalogTileAfterExternalFileDropProjectionSmoke(
                            catalogImage);
                    bool catalogFilterProjectionSessionGuarded =
                        catalogProjectionPrepared
                        && catalogProjectionRestored
                        && filteredCatalogPathDrop.Accepted
                        && filtersSwappedProjectionTile
                        && filteredProjectionDeleteRejected
                        && filteredProjectionAiSourcesAvailable
                        && !filteredProjectionPublishResult.SavedForDelivery
                        && filteredProjectionPublishResult.StatusCode == 409
                        && filteredProjectionProbeCalls == 1
                        && filteredProjectionPostCalls == 0
                        && PendingReservationCount()
                            == filteredProjectionPendingBefore
                        && string.Equals(
                            filteredProjectionStoresBefore,
                            StoreSetFingerprint(),
                            StringComparison.Ordinal)
                        && !window.ExternalFileDropSessionActiveForSmoke;

                    ExternalImageDropSmokeSnapshot catalogPathDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [catalogImage]);
                    Tile? firstCatalogPathDropTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    window.CloseModalForSmoke();
                    ExternalImageDropSmokeSnapshot reopenedCatalogPathDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [catalogImage]);
                    Tile? reopenedCatalogPathDropTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    bool catalogPathSessionIsolation = catalogPathDrop.Accepted
                        && reopenedCatalogPathDrop.Accepted
                        && catalogPathDrop.ProjectionCount == projectionCountBefore
                        && reopenedCatalogPathDrop.ProjectionCount == projectionCountBefore
                        && firstCatalogPathDropTile is not null
                        && reopenedCatalogPathDropTile is not null
                        && !ReferenceEquals(
                            firstCatalogPathDropTile,
                            reopenedCatalogPathDropTile)
                        && !window.ValidateExternalFileDropTileForEnqueueForSmoke(
                            firstCatalogPathDropTile,
                            out _);
                    window.CloseModalForSmoke();

                    string externalRaceABefore = FileFingerprint(externalRaceA);
                    string externalRaceBBefore = FileFingerprint(externalRaceB);

                    ExternalImageDropSmokeSnapshot changedDrop =
                        await window.DropExternalImagesForSmokeAsync([externalA]);
                    DateTime growthLastWriteUtc = File.GetLastWriteTimeUtc(externalA);
                    using (var changed = new FileStream(
                        externalA,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        changed.WriteByte(0);
                    }
                    File.SetLastWriteTimeUtc(externalA, growthLastWriteUtc);
                    bool sourceChangeRejected = changedDrop.Accepted
                        && !window.ExternalFileDropSourceValidForSmoke
                        && !window.ExternalFileDropExplicitAiSourcesAvailableForSmoke
                        && companionCalls == 0;
                    bool growthRejected = sourceChangeRejected;
                    window.CloseModalForSmoke();

                    ExternalImageDropSmokeSnapshot replacementDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [externalReplacement]);
                    DateTime replacementLastWriteUtc =
                        File.GetLastWriteTimeUtc(externalReplacement);
                    long replacementLength = new FileInfo(externalReplacement).Length;
                    File.Copy(
                        externalReplacement,
                        externalReplacementCandidate,
                        overwrite: false);
                    File.SetLastWriteTimeUtc(
                        externalReplacementCandidate,
                        replacementLastWriteUtc);
                    await ReplaceSmokeFileWithBoundedSharingRetryAsync(
                        externalReplacementCandidate,
                        externalReplacement);
                    File.SetLastWriteTimeUtc(
                        externalReplacement,
                        replacementLastWriteUtc);
                    bool replacementRejected = replacementDrop.Accepted
                        && new FileInfo(externalReplacement).Length == replacementLength
                        && !window.ExternalFileDropSourceValidForSmoke
                        && !window.ExternalFileDropExplicitAiSourcesAvailableForSmoke
                        && companionCalls == 0;
                    window.CloseModalForSmoke();

                    ExternalImageDropSmokeSnapshot firstDropResult;
                    ExternalImageDropSmokeSnapshot secondDropResult;
                    bool secondDropEntered;
                    using (var firstDropEntered = new ManualResetEventSlim(false))
                    using (var releaseFirstDrop = new ManualResetEventSlim(false))
                    {
                        window.PauseNextExternalFileDropValidationForSmoke(
                            firstDropEntered,
                            releaseFirstDrop);
                        Task<ExternalImageDropSmokeSnapshot> firstDrop =
                            window.DropExternalImagesForSmokeAsync([externalRaceA]);
                        secondDropEntered = await Task.Run(
                            () => firstDropEntered.Wait(TimeSpan.FromSeconds(10)));
                        secondDropResult = await window.DropExternalImagesForSmokeAsync(
                            [externalRaceB]);
                        releaseFirstDrop.Set();
                        firstDropResult = await firstDrop;
                    }
                    bool secondDropRaceRetired = secondDropEntered
                        && !firstDropResult.Accepted
                        && secondDropResult.Accepted
                        && window.ExternalFileDropCohortPathsForSmoke.SequenceEqual(
                            [Path.GetFullPath(externalRaceB)],
                            StringComparer.OrdinalIgnoreCase);
                    window.CloseModalForSmoke();

                    ExternalImageDropSmokeSnapshot staleSession =
                        await window.DropExternalImagesForSmokeAsync([externalRaceA]);
                    Tile? capturedExternalTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    window.CloseModalForSmoke();
                    bool stalePrepublishRejected = staleSession.Accepted
                        && capturedExternalTile is not null
                        && !window.ValidateExternalFileDropTileForEnqueueForSmoke(
                            capturedExternalTile,
                            out string stalePrepublishError)
                        && !string.IsNullOrWhiteSpace(stalePrepublishError);
                    ExternalImageDropSmokeSnapshot reopenedSamePath =
                        await window.DropExternalImagesForSmokeAsync([externalRaceA]);
                    Tile? reopenedExternalTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    bool reopenedSessionRejectsCapturedTile =
                        reopenedSamePath.Accepted
                        && reopenedExternalTile is not null
                        && !ReferenceEquals(
                            capturedExternalTile,
                            reopenedExternalTile)
                        && !window.ValidateExternalFileDropTileForEnqueueForSmoke(
                            capturedExternalTile!,
                            out _);
                    window.CloseModalForSmoke();

                    int prepublishProbeCalls = 0;
                    int prepublishPostCalls = 0;
                    HttpResponseMessage ReadyInboxHealthResponse()
                        => new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "{\"capabilities\":{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":1,\"backendGeneration\":\"json-v1\"}}}",
                                System.Text.Encoding.UTF8,
                                "application/json"),
                        };
                    HttpResponseMessage ReadyI2iInboxHealthResponse()
                        => new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "{\"capabilities\":{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":1,\"backendGeneration\":\"json-v1\"},\"i2i\":{\"contractId\":\"PV-ENHANCE-I2I-001\",\"operation\":\"i2i\",\"readerReady\":true,\"writerEnabled\":true,\"backendConfigured\":true,\"ready\":true,\"supportedTargets\":[\"hair-color\"],\"backendId\":\"comfyui-flux2-i2i\",\"workflowRevision\":\"i2i-flux2-klein9b-sam31-v1\",\"maskRevision\":\"sam31-hair-mediapipe-face-v1\"}}}",
                                System.Text.Encoding.UTF8,
                                "application/json"),
                        };

                    ExternalImageDropSmokeSnapshot healthSessionDrop =
                        await window.DropExternalImagesForSmokeAsync([externalRaceA]);
                    Tile? healthSessionTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    var sessionHealthEntered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var sessionHealthRelease = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        if (request.Method == HttpMethod.Get
                            && request.RequestUri?.AbsolutePath.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal) == true)
                        {
                            Interlocked.Increment(ref prepublishProbeCalls);
                            sessionHealthEntered.TrySetResult(true);
                            await sessionHealthRelease.Task.WaitAsync(token);
                            return ReadyInboxHealthResponse();
                        }
                        Interlocked.Increment(ref prepublishPostCalls);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        };
                    });
                    int pendingBeforeSessionRetire = PendingReservationCount();
                    Task<ExternalFileDropPrePublishSmokeSnapshot> sessionPublish =
                        window.PublishExternalFileDropReservationForSmokeAsync(
                            healthSessionTile!);
                    await sessionHealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    window.CloseModalForSmoke();
                    sessionHealthRelease.TrySetResult(true);
                    ExternalFileDropPrePublishSmokeSnapshot sessionPublishResult =
                        await sessionPublish;
                    bool healthAwaitSessionRetireRejected =
                        healthSessionDrop.Accepted
                        && healthSessionTile is not null
                        && !sessionPublishResult.SavedForDelivery
                        && sessionPublishResult.StatusCode == 409
                        && PendingReservationCount() == pendingBeforeSessionRetire;

                    ExternalImageDropSmokeSnapshot healthSourceDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [externalHealthChange]);
                    Tile? healthSourceTile =
                        window.CaptureExternalFileDropTileForSmoke();
                    var sourceHealthEntered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var sourceHealthRelease = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        if (request.Method == HttpMethod.Get
                            && request.RequestUri?.AbsolutePath.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal) == true)
                        {
                            Interlocked.Increment(ref prepublishProbeCalls);
                            sourceHealthEntered.TrySetResult(true);
                            await sourceHealthRelease.Task.WaitAsync(token);
                            return ReadyInboxHealthResponse();
                        }
                        Interlocked.Increment(ref prepublishPostCalls);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        };
                    });
                    int pendingBeforeSourceChange = PendingReservationCount();
                    Task<ExternalFileDropPrePublishSmokeSnapshot> sourcePublish =
                        window.PublishExternalFileDropReservationForSmokeAsync(
                            healthSourceTile!);
                    await sourceHealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    DateTime healthSourceLastWriteUtc =
                        File.GetLastWriteTimeUtc(externalHealthChange);
                    using (var changed = new FileStream(
                        externalHealthChange,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        changed.WriteByte(0);
                    }
                    File.SetLastWriteTimeUtc(
                        externalHealthChange,
                        healthSourceLastWriteUtc);
                    sourceHealthRelease.TrySetResult(true);
                    ExternalFileDropPrePublishSmokeSnapshot sourcePublishResult =
                        await sourcePublish;
                    bool healthAwaitSourceChangeRejected =
                        healthSourceDrop.Accepted
                        && healthSourceTile is not null
                        && !sourcePublishResult.SavedForDelivery
                        && sourcePublishResult.StatusCode == 409
                        && PendingReservationCount() == pendingBeforeSourceChange;
                    bool prepublishZeroReservations = prepublishProbeCalls == 2
                        && prepublishPostCalls == 0
                        && pendingBeforeSessionRetire == 0
                        && pendingBeforeSourceChange == 0
                        && PendingReservationCount() == 0;
                    window.CloseModalForSmoke();

                    int productionActionProbeCalls = 0;
                    int productionActionPostCalls = 0;
                    string productionRetireSourceBefore =
                        FileFingerprint(externalProductionRetire);
                    ExternalImageDropSmokeSnapshot productionSessionDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [externalProductionRetire]);
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    string productionSessionStoresBefore =
                        StoreSetFingerprint();
                    var productionSessionHealthEntered =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    var productionSessionHealthRelease =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        if (request.Method == HttpMethod.Get
                            && request.RequestUri?.AbsolutePath.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal) == true)
                        {
                            int probe = Interlocked.Increment(
                                ref productionActionProbeCalls);
                            if (probe == 1)
                                return ReadyI2iInboxHealthResponse();
                            productionSessionHealthEntered.TrySetResult(true);
                            await productionSessionHealthRelease.Task.WaitAsync(token);
                            return ReadyI2iInboxHealthResponse();
                        }
                        Interlocked.Increment(ref productionActionPostCalls);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        };
                    });
                    bool productionI2iBoardOpened =
                        await window.OpenModalI2iEditBoardForSmokeAsync();
                    window.ConfigureI2iEditForSmoke(
                        hairColor: "deep blue",
                        details: "preserve face and background",
                        fixedSeed: false,
                        seedValue: "123");
                    int pendingBeforeProductionSessionRetire =
                        PendingReservationCount();
                    Task<bool> productionI2iQueue =
                        window.QueueI2iEditForSmokeAsync();
                    await productionSessionHealthEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds(10));
                    window.CloseModalForSmoke();
                    productionSessionHealthRelease.TrySetResult(true);
                    bool productionI2iQueued = await productionI2iQueue;
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool productionSessionStoresUnchanged = string.Equals(
                        productionSessionStoresBefore,
                        StoreSetFingerprint(),
                        StringComparison.Ordinal);
                    bool productionSessionRetireNoReservation =
                        productionSessionDrop.Accepted
                        && productionI2iBoardOpened
                        && !productionI2iQueued
                        && !window.ExternalFileDropSessionActiveForSmoke
                        && !window.ModalVisibleForSmoke
                        && PendingReservationCount()
                            == pendingBeforeProductionSessionRetire
                        && string.Equals(
                            productionRetireSourceBefore,
                            FileFingerprint(externalProductionRetire),
                            StringComparison.Ordinal);
                    window.CloseModalForSmoke();

                    ExternalImageDropSmokeSnapshot productionGrowthDrop =
                        await window.DropExternalImagesForSmokeAsync(
                            [externalProductionGrowth]);
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    string productionGrowthStoresBefore =
                        StoreSetFingerprint();
                    var productionGrowthHealthEntered =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    var productionGrowthHealthRelease =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        if (request.Method == HttpMethod.Get
                            && request.RequestUri?.AbsolutePath.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal) == true)
                        {
                            Interlocked.Increment(ref productionActionProbeCalls);
                            productionGrowthHealthEntered.TrySetResult(true);
                            await productionGrowthHealthRelease.Task.WaitAsync(token);
                            return ReadyInboxHealthResponse();
                        }
                        Interlocked.Increment(ref productionActionPostCalls);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        };
                    });
                    int pendingBeforeProductionGrowth = PendingReservationCount();
                    window.BeginModalEnhancementForSmoke();
                    await productionGrowthHealthEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds(10));
                    DateTime productionGrowthLastWriteUtc =
                        File.GetLastWriteTimeUtc(externalProductionGrowth);
                    using (var changed = new FileStream(
                        externalProductionGrowth,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        changed.WriteByte(0);
                    }
                    File.SetLastWriteTimeUtc(
                        externalProductionGrowth,
                        productionGrowthLastWriteUtc);
                    string productionGrowthMutationFingerprint =
                        FileFingerprint(externalProductionGrowth);
                    productionGrowthHealthRelease.TrySetResult(true);
                    await window.WaitForModalEnhancementRequestCompletionForSmokeAsync(
                        timeoutMilliseconds: 10_000);
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    bool productionSourceGrowthNoReservation =
                        productionGrowthDrop.Accepted
                        && !window.ExternalFileDropSourceValidForSmoke
                        && !string.IsNullOrWhiteSpace(
                            window.ModalEnhancementErrorForSmoke)
                        && PendingReservationCount() == pendingBeforeProductionGrowth
                        && string.Equals(
                            productionGrowthMutationFingerprint,
                            FileFingerprint(externalProductionGrowth),
                            StringComparison.Ordinal);
                    bool productionActionZeroReservations =
                        productionActionProbeCalls == 3
                        && productionActionPostCalls == 0
                        && pendingBeforeProductionSessionRetire == 0
                        && pendingBeforeProductionGrowth == 0
                        && PendingReservationCount() == 0;
                    bool productionStoresUnchanged =
                        productionSessionStoresUnchanged
                        && string.Equals(
                            productionGrowthStoresBefore,
                            StoreSetFingerprint(),
                            StringComparison.Ordinal);
                    window.CloseModalForSmoke();

                    _ = await window.DrainSharedStoreWritersForSmokeAsync();
                    string raceStoresBefore = StoreSetFingerprint();
                    bool catalogModalOpened = window.OpenModalForSmoke();
                    ExternalImageDropSmokeSnapshot modalCloseDropResult;
                    bool modalCloseEntered;
                    using (var modalDropEntered = new ManualResetEventSlim(false))
                    using (var releaseModalDrop = new ManualResetEventSlim(false))
                    {
                        window.PauseNextExternalFileDropValidationForSmoke(
                            modalDropEntered,
                            releaseModalDrop);
                        Task<ExternalImageDropSmokeSnapshot> modalCloseDrop =
                            window.DropExternalImagesForSmokeAsync([externalRaceA]);
                        modalCloseEntered = await Task.Run(
                            () => modalDropEntered.Wait(TimeSpan.FromSeconds(10)));
                        window.CloseModalForSmoke();
                        releaseModalDrop.Set();
                        modalCloseDropResult = await modalCloseDrop;
                    }
                    bool modalCloseRaceRetired = catalogModalOpened
                        && modalCloseEntered
                        && !modalCloseDropResult.Accepted
                        && !window.ModalVisibleForSmoke
                        && !window.ExternalFileDropSessionActiveForSmoke;
                    _ = await window.DrainSharedStoreWritersForSmokeAsync();

                    bool raceSourcesUnchanged = string.Equals(
                            externalRaceABefore,
                            FileFingerprint(externalRaceA),
                            StringComparison.Ordinal)
                        && string.Equals(
                            externalRaceBBefore,
                            FileFingerprint(externalRaceB),
                            StringComparison.Ordinal);
                    bool raceStoresUnchanged = string.Equals(
                        raceStoresBefore,
                        StoreSetFingerprint(),
                        StringComparison.Ordinal);

                    ExternalImageDropSmokeSnapshot folderRaceDropResult;
                    bool folderRaceEntered;
                    using (var folderDropEntered = new ManualResetEventSlim(false))
                    using (var releaseFolderDrop = new ManualResetEventSlim(false))
                    {
                        window.PauseNextExternalFileDropValidationForSmoke(
                            folderDropEntered,
                            releaseFolderDrop);
                        Task<ExternalImageDropSmokeSnapshot> folderRaceDrop =
                            window.DropExternalImagesForSmokeAsync([externalRaceB]);
                        folderRaceEntered = await Task.Run(
                            () => folderDropEntered.Wait(TimeSpan.FromSeconds(10)));
                        Task folderLoad = window.LoadFolderSetAsync(
                            [catalogFolder],
                            commitRecent: false);
                        releaseFolderDrop.Set();
                        folderRaceDropResult = await folderRaceDrop;
                        await folderLoad;
                    }
                    bool folderLoadRetired = folderRaceEntered
                        && !folderRaceDropResult.Accepted
                        && !window.ExternalFileDropSessionActiveForSmoke
                        && window.CatalogCountForSmoke == catalogCountBefore
                        && window.CurrentFolderSetForSmoke.SequenceEqual(
                            folderSetBefore,
                            StringComparer.OrdinalIgnoreCase);

                    bool invalidRejected = !malformed.Accepted
                        && !dimensions.Accepted
                        && !count101.Accepted
                        && !bytes512.Accepted;
                    bool mixedRejected = string.Equals(
                            mixed.Kind,
                            "Mixed",
                            StringComparison.Ordinal)
                        && !mixed.AffordanceAccepted;
                    bool folderAffordancePreserved = string.Equals(
                            folders.Kind,
                            "Folders",
                            StringComparison.Ordinal)
                        && folders.AffordanceAccepted;
                    bool acceptedContract = catalogSelected
                        && accepted.Accepted
                        && accepted.CohortCount == 2
                        && accepted.CatalogCount == catalogCountBefore
                        && accepted.ProjectionCount == projectionCountBefore + 2
                        && accepted.ModalVisible
                        && !accepted.DeleteEnabled
                        && window.ExternalFileDropSurfaceContractForSmoke;

                    ok = invalidRejected
                        && mixedRejected
                        && folderAffordancePreserved
                        && acceptedContract
                        && orderAndFilmstrip
                        && previousWrapped
                        && nextWrapped
                        && seenRolledBack
                        && seenRetryApplied
                        && favoriteRolledBack
                        && favoriteRetryApplied
                        && aiSourcesAvailable
                        && deleteRequestRejected
                        && passive
                        && stateUnchangedWhileOpen
                        && sourceUntouched
                        && closeRestored
                        && catalogFilterProjectionSessionGuarded
                        && catalogPathSessionIsolation
                        && sourceChangeRejected
                        && growthRejected
                        && replacementRejected
                        && secondDropRaceRetired
                        && modalCloseRaceRetired
                        && stalePrepublishRejected
                        && reopenedSessionRejectsCapturedTile
                        && healthAwaitSessionRetireRejected
                        && healthAwaitSourceChangeRejected
                        && prepublishZeroReservations
                        && productionSessionRetireNoReservation
                        && productionSourceGrowthNoReservation
                        && productionActionZeroReservations
                        && productionStoresUnchanged
                        && raceSourcesUnchanged
                        && raceStoresUnchanged
                        && folderLoadRetired;
                    result = new
                    {
                        ok,
                        invalidRejected,
                        mixedRejected,
                        folderAffordancePreserved,
                        acceptedContract,
                        orderAndFilmstrip,
                        previousWrapped,
                        nextWrapped,
                        seenRolledBack,
                        seenRetryApplied,
                        favoriteRolledBack,
                        favoriteRetryApplied,
                        aiSourcesAvailable,
                        deleteRequestRejected,
                        passive,
                        companionCalls,
                        stateUnchangedWhileOpen,
                        sourceUntouched,
                        closeRestored,
                        catalogFilterProjectionSessionGuarded,
                        filtersSwappedProjectionTile,
                        filteredProjectionDeleteRejected,
                        filteredProjectionAiSourcesAvailable,
                        filteredProjectionProbeCalls,
                        filteredProjectionPostCalls,
                        catalogPathSessionIsolation,
                        sourceChangeRejected,
                        growthRejected,
                        replacementRejected,
                        secondDropRaceRetired,
                        modalCloseRaceRetired,
                        stalePrepublishRejected,
                        reopenedSessionRejectsCapturedTile,
                        healthAwaitSessionRetireRejected,
                        healthAwaitSourceChangeRejected,
                        prepublishZeroReservations,
                        prepublishProbeCalls,
                        prepublishPostCalls,
                        productionSessionRetireNoReservation,
                        productionSourceGrowthNoReservation,
                        productionActionZeroReservations,
                        productionActionProbeCalls,
                        productionActionPostCalls,
                        productionStoresUnchanged,
                        raceSourcesUnchanged,
                        raceStoresUnchanged,
                        folderLoadRetired,
                        catalogCountBefore,
                        projectionCountBefore,
                        acceptedCohortCount = accepted.CohortCount,
                    };
                }
                catch (Exception ex)
                {
                    result = new
                    {
                        ok = false,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                        stackTrace = ex.ToString(),
                    };
                }
                finally
                {
                    try { window.Close(); } catch { }
                    foreach ((string key, string? value) in previousEnvironment)
                        Environment.SetEnvironmentVariable(key, value);
                    Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
                        JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true }));
                    try
                    {
                        if (Directory.Exists(smokeRoot))
                            Directory.Delete(smokeRoot, recursive: true);
                    }
                    catch
                    {
                    }
                    Shutdown(ok ? 0 : 1);
                }
            });
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                stackTrace = ex.ToString(),
            };
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                if (Directory.Exists(smokeRoot))
                    Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
            Shutdown(1);
        }
    }

    private static async Task ReplaceSmokeFileWithBoundedSharingRetryAsync(
        string replacementPath,
        string destinationPath)
    {
        const int maximumAttempts = 40;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                File.Move(replacementPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt + 1 < maximumAttempts
                && IsTransientSmokeFileSharingFailure(ex))
            {
                await Task.Delay(25);
            }
        }

        throw new InvalidOperationException(
            "The synthetic replacement did not complete within the bounded retry window.");
    }

    private static bool IsTransientSmokeFileSharingFailure(Exception exception)
    {
        int nativeError = exception.HResult & 0xffff;
        return exception is UnauthorizedAccessException
            || exception is IOException && nativeError is 5 or 32 or 33;
    }
}

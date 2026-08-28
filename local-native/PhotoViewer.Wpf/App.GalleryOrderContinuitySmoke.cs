using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureGalleryOrderContinuitySmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        DirectoryInfo smokeDirectory = Directory.CreateTempSubdirectory(
            "aibos-gallery-order-continuity-smoke-");
        string smokeRoot = smokeDirectory.FullName;
        string folder = smokeDirectory.CreateSubdirectory("images").FullName;
        string statePath = Path.Combine(smokeRoot, "state.json");
        string seenPath = Path.Combine(smokeRoot, "seen.json");
        string favoritesPath = Path.Combine(smokeRoot, "favorites.json");
        string recentPath = Path.Combine(smokeRoot, "recent.json");
        string jobsPath = Path.Combine(smokeRoot, "jobs.json");
        Environment.SetEnvironmentVariable("PHOTOVIEWER_WPF_STATE_PATH", statePath);
        Environment.SetEnvironmentVariable("PHOTOVIEWER_WPF_SEEN_PATH", seenPath);
        Environment.SetEnvironmentVariable("PHOTOVIEWER_WPF_FAVORITES_PATH", favoritesPath);
        Environment.SetEnvironmentVariable("PHOTOVIEWER_WPF_RECENT_PATH", recentPath);
        Environment.SetEnvironmentVariable("PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH", jobsPath);

        var names = new List<string>(48);
        for (int index = 0; index < 48; index++)
        {
            string name = $"image-{index:D2}.png";
            names.Add(name);
            WriteSmokePng(
                Path.Combine(folder, name),
                96,
                72,
                Color.FromRgb(
                    (byte)(40 + (index * 3) % 180),
                    (byte)(60 + (index * 5) % 160),
                    (byte)(80 + (index * 7) % 140)));
        }
        string sourceFingerprint = FolderFingerprint(folder);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow window = HiddenWindow();
        window.Width = 960;
        window.Height = 640;
        window.Show();
        window.Dispatcher.InvokeAsync(async () =>
        {
            bool ok = false;
            object result;
            try
            {
                await window.LoadFolderAsync(folder);
                window.SetSortByForSmoke("name");
                await window.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ContextIdle);
                string startName = names[0];
                string expectedNextName = names[1];
                int thumbnailSchedules = window.ScheduleThumbnailBatchForSmoke(
                    [startName, expectedNextName]);
                bool thumbnailBatchIdle =
                    await window.WaitForThumbnailViewportIdleForSmokeAsync(5_000);
                bool thumbnailsLoaded = thumbnailBatchIdle
                    && window.LoadedThumbnailCountForSmoke(
                        [startName, expectedNextName]) == 2;
                bool modalOpened = window.SelectFileNameForSmoke(startName)
                    && window.OpenModalForSmoke();
                int resetsBefore = window.ProjectionResetNotificationCountForSmoke;
                int movesBefore = window.ProjectionMoveNotificationCountForSmoke;
                int thumbnailCancelsBefore =
                    window.ThumbnailViewportCancelCountForSmoke;
                int loadedBefore = window.LoadedThumbnailCountForSmoke(
                    [startName, expectedNextName]);
                bool singleMovePublished = window.ReorderProjectionOneTileForSmoke(
                    expectedNextName,
                    names.Count - 1);
                int resetsAfterFirstMove =
                    window.ProjectionResetNotificationCountForSmoke;
                int movesAfterFirstMove =
                    window.ProjectionMoveNotificationCountForSmoke;
                int thumbnailCancelsAfterFirstMove =
                    window.ThumbnailViewportCancelCountForSmoke;
                bool singleMoveBackPublished = window.ReorderProjectionOneTileForSmoke(
                    names[^1],
                    0);
                int resetsAfterSecondMove =
                    window.ProjectionResetNotificationCountForSmoke;
                int movesAfterSecondMove =
                    window.ProjectionMoveNotificationCountForSmoke;
                bool moveOnly = singleMovePublished
                    && singleMoveBackPublished
                    && resetsAfterSecondMove == resetsBefore
                    && movesAfterSecondMove == movesBefore + 2;
                int loadedAfter = window.LoadedThumbnailCountForSmoke(
                    [startName, expectedNextName]);
                bool modalMovedToPinnedNeighbor = window.NavigateModalForSmoke(1)
                    && string.Equals(
                        window.ModalSourcePathForSmoke,
                        Path.Combine(folder, expectedNextName),
                        StringComparison.OrdinalIgnoreCase);
                string? modalName = Path.GetFileName(window.ModalSourcePathForSmoke);
                window.CloseModalForSmoke();

                bool scrolledAway = await window.ScrollGridToMiddleForSmokeAsync()
                    && window.GalleryVerticalOffsetForSmoke > 0.5;
                bool returnedToTop = await window.InvokeGalleryScrollToTopForSmokeAsync();
                bool toolbarContract = window.GalleryScrollToTopButtonContractForSmoke;

                bool realDoneSortSet = await window.SetSortByInteractiveForSmokeAsync(
                    "photoreal-completed-newest");
                bool realDoneScrolled = await window.ScrollGridToMiddleForSmokeAsync();
                await window.WaitForGridZoomAnchorForSmokeAsync();
                string? realDoneSourceName = window.CaptureGridViewportAnchorForSmoke();
                bool realDoneSelected = !string.IsNullOrWhiteSpace(realDoneSourceName)
                    && window.SelectFileNameForSmoke(realDoneSourceName);
                bool realDoneModalOpened = realDoneSelected
                    && window.OpenModalForSmoke();
                string? realDoneReturnAnchor =
                    window.ModalGalleryReturnAnchorPathForSmoke;
                bool realDoneHasStableNeighbor = realDoneModalOpened
                    && !string.IsNullOrWhiteSpace(realDoneReturnAnchor)
                    && !string.Equals(
                        Path.GetFileName(realDoneReturnAnchor),
                        realDoneSourceName,
                        StringComparison.OrdinalIgnoreCase);
                bool realDoneActivitySet = realDoneSourceName is not null
                    && window.SetSortActivityForSmoke(
                        realDoneSourceName,
                        "photoreal",
                        DateTimeOffset.UtcNow);
                var realDoneRefresh = await window.RefreshCurrentSortForSmokeAsync();
                bool realDoneMovedSourceToTop = realDoneSourceName is not null
                    && string.Equals(
                        window.FilteredFileNamesForSmoke(1).SingleOrDefault(),
                        realDoneSourceName,
                        StringComparison.OrdinalIgnoreCase);
                window.CloseModalForSmoke(restoreFocus: true);
                await window.WaitForGridZoomAnchorForSmokeAsync();
                bool realDoneReturnPreserved = realDoneSortSet
                    && realDoneScrolled
                    && realDoneHasStableNeighbor
                    && realDoneActivitySet
                    && realDoneRefresh.Applied
                    && !realDoneRefresh.Discarded
                    && realDoneMovedSourceToTop
                    && window.GalleryVerticalOffsetForSmoke > 0.5
                    && string.Equals(
                        window.LastGridZoomAnchorPathForSmoke,
                        realDoneReturnAnchor,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        window.SelectedFileNameForSmoke,
                        realDoneSourceName,
                        StringComparison.OrdinalIgnoreCase);
                bool sourcesUnchanged = string.Equals(
                    sourceFingerprint,
                    FolderFingerprint(folder),
                    StringComparison.Ordinal);
                bool isolated = new[]
                    {
                        folder,
                        statePath,
                        seenPath,
                        favoritesPath,
                        recentPath,
                        jobsPath,
                    }
                    .All(path => Path.GetFullPath(path).StartsWith(
                        Path.GetFullPath(smokeRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
                bool thumbnailsRetained = thumbnailsLoaded
                    && loadedBefore == 2
                    && loadedAfter == loadedBefore;
                ok = moveOnly
                    && thumbnailsRetained
                    && modalOpened
                    && modalMovedToPinnedNeighbor
                    && scrolledAway
                    && returnedToTop
                    && toolbarContract
                    && realDoneReturnPreserved
                    && sourcesUnchanged
                    && isolated;
                result = new
                {
                    ok,
                    message = ok
                        ? "one-item gallery reorder preserved thumbnails and modal neighbor order; toolbar returned the active gallery to the top"
                        : "gallery order continuity contract failed",
                    smokeRoot,
                    moveOnly,
                    singleMovePublished,
                    singleMoveBackPublished,
                    resetsBefore,
                    resetsAfterFirstMove,
                    resetsAfterSecondMove,
                    movesBefore,
                    movesAfterFirstMove,
                    movesAfterSecondMove,
                    thumbnailCancelsBefore,
                    thumbnailCancelsAfterFirstMove,
                    thumbnailsRetained,
                    thumbnailSchedules,
                    thumbnailBatchIdle,
                    loadedBefore,
                    loadedAfter,
                    modalOpened,
                    modalMovedToPinnedNeighbor,
                    expectedNextName,
                    modalName,
                    scrolledAway,
                    returnedToTop,
                    toolbarContract,
                    realDoneSortSet,
                    realDoneScrolled,
                    realDoneSourceName,
                    realDoneHasStableNeighbor,
                    realDoneActivitySet,
                    realDoneMovedSourceToTop,
                    realDoneReturnPreserved,
                    sourcesUnchanged,
                    isolated,
                };
            }
            catch (Exception ex)
            {
                result = new { ok = false, message = ex.ToString(), smokeRoot };
            }
            finally
            {
                try { if (window.IsLoaded) window.Close(); } catch { }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }
}

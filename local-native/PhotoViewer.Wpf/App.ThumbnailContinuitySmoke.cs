using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureThumbnailContinuitySmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory("aibos-thumbnail-continuity-").FullName;
        string folder = Path.Combine(smokeRoot, "images");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string lockedPath = Path.Combine(folder, "locked-valid.png");
        string anchorPath = Path.Combine(folder, "anchor-valid.png");
        string corruptPath = Path.Combine(folder, "corrupt.png");
        var previousEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_STATE_PATH"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_FAVORITES_PATH"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SEEN_PATH"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_RECENT_PATH"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SETTINGS_PATH"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ALBUMS_PATH"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Environment.GetEnvironmentVariable("PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"),
        };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
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
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            FileStream? exclusiveLock = null;
            bool ok = false;
            object result;
            try
            {
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(storeRoot);
                foreach ((string name, string value) in environment)
                    Environment.SetEnvironmentVariable(name, value);

                WriteSmokePng(lockedPath, 64, 48, Color.FromRgb(76, 132, 214));
                WriteSmokePng(anchorPath, 64, 48, Color.FromRgb(126, 190, 116));
                File.WriteAllBytes(corruptPath, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03]);
                var sourceHashesBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Path.GetFileName(lockedPath)] = HashFile(lockedPath),
                    [Path.GetFileName(anchorPath)] = HashFile(anchorPath),
                    [Path.GetFileName(corruptPath)] = HashFile(corruptPath),
                };

                exclusiveLock = new FileStream(
                    lockedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                window = HiddenWindow();
                window.Show();
                int loadCtsCreatedBefore = window.LoadCtsCreatedCountForSmoke;
                Task loadTask = window.LoadFolderAsync(folder);
                bool transientFailureObserved = await WaitForThumbnailContinuityConditionAsync(
                    () => window.ThumbnailDecodeAttemptCountForSmoke(Path.GetFileName(lockedPath)) >= 1,
                    timeoutMilliseconds: 4_000);
                int lockedAttemptsBeforeRelease =
                    window.ThumbnailDecodeAttemptCountForSmoke(Path.GetFileName(lockedPath));
                exclusiveLock.Dispose();
                exclusiveLock = null;
                await loadTask;

                bool lockedRecoveredWithoutReload = await window.WaitForThumbnailForSmokeAsync(
                    Path.GetFileName(lockedPath),
                    timeoutMilliseconds: 5_000);
                bool lockedFailureStateCleared =
                    window.ThumbnailDecodeAttemptCountForSmoke(Path.GetFileName(lockedPath)) == 0;
                bool corruptBecameTerminal = await WaitForThumbnailContinuityConditionAsync(
                    () => window.ThumbnailDecodeTerminalForSmoke(Path.GetFileName(corruptPath)),
                    timeoutMilliseconds: 6_000);
                int corruptAttempts = window.ThumbnailDecodeAttemptCountForSmoke(Path.GetFileName(corruptPath));
                int loadCtsCreatedAfterRecovery = window.LoadCtsCreatedCountForSmoke;
                bool singleFolderLoad =
                    loadCtsCreatedAfterRecovery - loadCtsCreatedBefore == 1;
                var sourceHashesAfterRecovery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Path.GetFileName(lockedPath)] = HashFile(lockedPath),
                    [Path.GetFileName(anchorPath)] = HashFile(anchorPath),
                    [Path.GetFileName(corruptPath)] = HashFile(corruptPath),
                };
                bool sourcesUnchanged = sourceHashesBefore.Count == sourceHashesAfterRecovery.Count
                    && sourceHashesBefore.All(pair =>
                        sourceHashesAfterRecovery.TryGetValue(pair.Key, out string? hash)
                        && string.Equals(hash, pair.Value, StringComparison.Ordinal));

                window.SeedLargeInteractionCatalogForSmoke(1_201);
                window.SetGridModeForSmoke();
                window.SetGridZoomForSmoke(600);
                bool sparseSettled = await window.WaitForGridRealizationIdleForSmokeAsync();
                int sparseColumns = window.GridColumnCountForSmoke;
                long sparseGeneration = window.GridLayoutGenerationForSmoke;

                window.SetGridZoomForSmoke(20);
                window.RunSingleGridLayoutPassForSmoke();
                bool progressiveSliceObserved =
                    window.GridRealizationContinuationPendingForSmoke
                    && window.GridVisiblePlaceholderCountForSmoke > 0
                    && window.GridVisiblePlaceholderCountForSmoke == window.GridVisibleUnrealizedCountForSmoke;
                int progressivePlaceholders = window.GridVisiblePlaceholderCountForSmoke;
                long progressiveGeneration = window.GridLayoutGenerationForSmoke;

                for (int index = 0; index < 4; index++)
                {
                    window.SetGridZoomForSmoke(index % 2 == 0 ? 600 : 20);
                    window.RunSingleGridLayoutPassForSmoke();
                }
                window.SetGridZoomForSmoke(20);
                window.RunSingleGridLayoutPassForSmoke();
                bool denseSettled = await window.WaitForGridRealizationIdleForSmokeAsync(
                    timeoutMilliseconds: 8_000);
                int denseColumns = window.GridColumnCountForSmoke;
                long finalGeneration = window.GridLayoutGenerationForSmoke;
                int finalPlaceholders = window.GridVisiblePlaceholderCountForSmoke;
                int finalUnrealized = window.GridVisibleUnrealizedCountForSmoke;
                int finalVisibleItems = window.GridVisibleItemCountForSmoke;
                int finalRealizedItems = window.GridRealizedCountForSmoke;
                bool layoutGenerationAdvanced =
                    progressiveGeneration > sparseGeneration
                    && finalGeneration > progressiveGeneration;
                bool finalVisibleCoverage =
                    denseSettled
                    && finalVisibleItems > 24
                    && finalRealizedItems >= finalVisibleItems
                    && finalPlaceholders == 0
                    && finalUnrealized == 0;
                bool densityChangedColumns =
                    sparseSettled
                    && sparseColumns == 1
                    && denseColumns > sparseColumns;

                ok = transientFailureObserved
                    && lockedAttemptsBeforeRelease >= 1
                    && lockedRecoveredWithoutReload
                    && lockedFailureStateCleared
                    && corruptBecameTerminal
                    && corruptAttempts == 4
                    && singleFolderLoad
                    && sourcesUnchanged
                    && progressiveSliceObserved
                    && layoutGenerationAdvanced
                    && densityChangedColumns
                    && finalVisibleCoverage;
                result = new
                {
                    ok,
                    message = ok
                        ? "Thumbnail retry and virtualized-grid continuity passed"
                        : "Thumbnail continuity smoke failed",
                    smokeRoot,
                    transientFailureObserved,
                    lockedAttemptsBeforeRelease,
                    lockedRecoveredWithoutReload,
                    lockedFailureStateCleared,
                    corruptBecameTerminal,
                    corruptAttempts,
                    singleFolderLoad,
                    sourcesUnchanged,
                    progressiveSliceObserved,
                    progressivePlaceholders,
                    sparseSettled,
                    sparseColumns,
                    denseSettled,
                    denseColumns,
                    layoutGenerationAdvanced,
                    sparseGeneration,
                    progressiveGeneration,
                    finalGeneration,
                    finalPlaceholders,
                    finalUnrealized,
                    finalVisibleItems,
                    finalRealizedItems,
                    finalVisibleCoverage,
                    sourceHashesBefore,
                    sourceHashesAfterRecovery,
                };
            }
            catch (Exception ex)
            {
                result = new
                {
                    ok = false,
                    message = ex.Message,
                    exception = ex.ToString(),
                    smokeRoot,
                };
            }
            finally
            {
                exclusiveLock?.Dispose();
                window?.Close();
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }

    private static async Task<bool> WaitForThumbnailContinuityConditionAsync(
        Func<bool> condition,
        int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

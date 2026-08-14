using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureAiProcessingMinimizeSmoke(string resultPath)
    {
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-ai-minimize-").FullName;
        string storesRoot = Path.Combine(smokeRoot, "stores");
        string outputsRoot = Path.Combine(smokeRoot, "outputs");
        Directory.CreateDirectory(storesRoot);
        Directory.CreateDirectory(outputsRoot);
        string statePath = Path.Combine(storesRoot, "state.json");
        string jobsPath = Path.Combine(storesRoot, "jobs.json");
        string fullResultPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullResultPath)
                ?? throw new InvalidOperationException(
                    "Result path has no parent."));

        string[] environmentNames =
        [
            "PHOTOVIEWER_WPF_STATE_PATH",
            "PHOTOVIEWER_WPF_FAVORITES_PATH",
            "PHOTOVIEWER_WPF_SEEN_PATH",
            "PHOTOVIEWER_WPF_RECENT_PATH",
            "PHOTOVIEWER_WPF_SETTINGS_PATH",
            "PHOTOVIEWER_WPF_ALBUMS_PATH",
            "PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH",
            "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
            "PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT",
            "PVU_ENHANCE_OUTPUT_ROOT",
            "PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY",
        ];
        var previousEnvironment = environmentNames.ToDictionary(
            static name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        MainWindow? window = null;
        object result;
        bool success = false;
        try
        {
            var environment = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
                ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(
                    storesRoot,
                    "favorites.json"),
                ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(
                    storesRoot,
                    "seen.json"),
                ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(
                    storesRoot,
                    "recent-folders.json"),
                ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(
                    storesRoot,
                    "settings.json"),
                ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(
                    storesRoot,
                    "albums.json"),
                ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(
                    storesRoot,
                    "search-history.json"),
                ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputsRoot,
                ["PVU_ENHANCE_OUTPUT_ROOT"] = outputsRoot,
                ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(
                    storesRoot,
                    "metadata-index"),
            };
            foreach ((string name, string value) in environment)
                Environment.SetEnvironmentVariable(name, value);

            File.WriteAllText(statePath, """
                {
                  "Version": 2,
                  "futureAiMinimizeState": { "keep": true }
                }
                """);
            File.WriteAllText(jobsPath, """
                { "version": 1, "jobs": [] }
                """);
            string stateBefore = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(statePath)));

            window = new MainWindow();
            window.EnableAiProcessingMinimizeNativeSuppressionForSmoke();
            window.ShowActivated = false;
            window.Opacity = 0;
            window.Show();
            int catalogCountBefore =
                window.CatalogTileCountForAiProcessingMinimizeSmoke;
            string? selectedBefore =
                window.SelectedPathForAiProcessingMinimizeSmoke;
            bool surfaceContract =
                window.AiProcessingMinimizeSurfaceContractForSmoke
                && PhotoViewer.Wpf.MainWindow
                    .AiProcessingTrayNativeExportsAvailableForSmoke;

            window.PrepareAiProcessingMinimizeTimersForSmoke();
            window.MinimizeForAiProcessingSmoke();
            bool lightweightBoundary =
                window.AiProcessingMinimizedModeForSmoke
                && window.AiProcessingUiTimersSuspendedForSmoke
                && window.ActiveEnhancementRevisionWatcherRunningForSmoke;
            bool trayBoundary = window.AiProcessingTrayVisibleForSmoke
                && !window.ShowInTaskbarForSmoke
                && window.AiProcessingTrayAddCountForSmoke == 1;
            window.TrackEnhancementNotificationJobForSmoke(
                "minimized-video-failed",
                "video");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "minimized-video-failed",
                "video",
                "failed");
            window.TrackEnhancementNotificationJobForSmoke(
                "minimized-upscale-succeeded",
                "upscale");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "minimized-upscale-succeeded",
                "upscale",
                "succeeded");
            bool notificationShown =
                window.AiProcessingTrayNotificationCountForSmoke == 2
                && window.LastAiProcessingTrayNotificationTitleForSmoke
                    .Contains("高画質化が完了", StringComparison.Ordinal);
            bool catalogPreservedWhileMinimized =
                window.CatalogTileCountForAiProcessingMinimizeSmoke
                    == catalogCountBefore
                && string.Equals(
                    window.SelectedPathForAiProcessingMinimizeSmoke,
                    selectedBefore,
                    StringComparison.Ordinal);
            string stateDuring = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(statePath)));
            bool durableStateUntouched = string.Equals(
                stateBefore,
                stateDuring,
                StringComparison.Ordinal);

            window.RestoreFromAiProcessingMinimizeForSmoke();
            bool restoredSameProcess =
                !window.AiProcessingMinimizedModeForSmoke
                && !window.AiProcessingTrayVisibleForSmoke
                && window.ShowInTaskbarForSmoke
                && window.SearchFilterTimerRunningForSmoke
                && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                && window.CatalogTileCountForAiProcessingMinimizeSmoke
                    == catalogCountBefore
                && string.Equals(
                    window.SelectedPathForAiProcessingMinimizeSmoke,
                    selectedBefore,
                    StringComparison.Ordinal)
                && window.AiProcessingMinimizeEnterCountForSmoke == 1
                && window.AiProcessingTrayRemoveCountForSmoke == 1;

            window.ToggleMaximizeForSmoke();
            bool fakeMaximizedBefore = window.FakeMaximizedForSmoke;
            window.MinimizeForAiProcessingSmoke();
            window.RestoreFromAiProcessingMinimizeForSmoke();
            bool fakeMaximizedRestored = window.FakeMaximizedForSmoke;
            window.ToggleMaximizeForSmoke();

            window.WindowState = WindowState.Maximized;
            bool nativeMaximizedBefore = window.NativeMaximizedForSmoke;
            window.MinimizeForAiProcessingSmoke();
            window.RestoreFromAiProcessingMinimizeForSmoke();
            bool nativeMaximizedRestored = window.NativeMaximizedForSmoke;
            window.WindowState = WindowState.Normal;
            bool windowStateRestored = fakeMaximizedBefore
                && fakeMaximizedRestored
                && nativeMaximizedBefore
                && nativeMaximizedRestored;

            success = surfaceContract
                && lightweightBoundary
                && trayBoundary
                && notificationShown
                && catalogPreservedWhileMinimized
                && durableStateUntouched
                && restoredSameProcess
                && windowStateRestored;
            result = new
            {
                success,
                surfaceContract,
                lightweightBoundary,
                trayBoundary,
                notificationShown,
                catalogPreservedWhileMinimized,
                durableStateUntouched,
                restoredSameProcess,
                windowStateRestored,
                fakeMaximizedRestored,
                nativeMaximizedRestored,
                restoredModeOff =
                    !window.AiProcessingMinimizedModeForSmoke,
                restoredTrayHidden =
                    !window.AiProcessingTrayVisibleForSmoke,
                restoredTaskbar = window.ShowInTaskbarForSmoke,
                restoredSearchTimer = window.SearchFilterTimerRunningForSmoke,
                restoredActiveWatcher =
                    window.ActiveEnhancementRevisionWatcherRunningForSmoke,
                catalogCountAfter =
                    window.CatalogTileCountForAiProcessingMinimizeSmoke,
                selectedAfter =
                    window.SelectedPathForAiProcessingMinimizeSmoke,
                catalogCountBefore,
                selectedBefore,
                trayAdds = window.AiProcessingTrayAddCountForSmoke,
                trayRemoves = window.AiProcessingTrayRemoveCountForSmoke,
                trayNotifications =
                    window.AiProcessingTrayNotificationCountForSmoke,
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                success = false,
                error = ex.ToString(),
            };
        }
        finally
        {
            try
            {
                window?.Close();
            }
            catch
            {
            }
            foreach ((string name, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(name, value);
            try
            {
                Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
        }

        File.WriteAllText(
            fullResultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(success ? 0 : 1);
    }
}

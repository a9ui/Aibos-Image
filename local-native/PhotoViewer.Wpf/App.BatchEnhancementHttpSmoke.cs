using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureSelectedBatchEnhancementHttpSmoke(
        string resultPath,
        IReadOnlyList<string> args)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string? folderValue = ArgValue(args.ToArray(), "--folder");
        string? storeRootValue = ArgValue(args.ToArray(), "--store-root");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        bool argumentsSafe = !string.IsNullOrWhiteSpace(folderValue)
            && !string.IsNullOrWhiteSpace(storeRootValue)
            && Path.GetFullPath(folderValue).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(storeRootValue).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
        if (!argumentsSafe)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(new
                {
                    ok = false,
                    message = "HTTP batch smoke requires --folder and --store-root under the OS TEMP directory.",
                }));
            Shutdown(1);
            return;
        }

        string folder = Path.GetFullPath(folderValue!);
        string storeRoot = Path.GetFullPath(storeRootValue!);
        string statePath = Path.Combine(storeRoot, "state.json");
        string favoritesPath = Path.Combine(storeRoot, "favorites.json");
        string seenPath = Path.Combine(storeRoot, "seen.json");
        string recentPath = Path.Combine(storeRoot, "recent-folders.json");
        string settingsPath = Path.Combine(storeRoot, "settings.json");
        string albumsPath = Path.Combine(storeRoot, "albums.json");
        string searchHistoryPath = Path.Combine(storeRoot, "search-history.json");
        string metadataIndexDirectory = Path.Combine(storeRoot, "metadata-index");
        string jobsPath = Path.Combine(storeRoot, "enhance", "jobs.json");
        string outputRoot = Path.Combine(storeRoot, "enhance", "outputs");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = favoritesPath,
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = seenPath,
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = recentPath,
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = settingsPath,
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = albumsPath,
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = searchHistoryPath,
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = metadataIndexDirectory,
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            bool ok = false;
            object result;
            try
            {
                foreach ((string key, string value) in environment)
                    Environment.SetEnvironmentVariable(key, value);
                foreach (string path in environment.Values.Where(static path => Path.HasExtension(path)))
                {
                    if (!File.Exists(path))
                        throw new FileNotFoundException("Required isolated smoke store is missing.", path);
                }

                window = HiddenWindow();
                window.SuppressStatePersistence();
                window.ConfigureModalEnhancementRequestProfileForSmoke(
                    "anime-sharp-x2",
                    "sharp-test",
                    2);
                window.ConfigureModalEnhancementConfirmationsForSmoke(
                    confirmLargeJob: true,
                    confirmOutputDelete: true);
                window.Show();
                await window.LoadFolderAsync(folder);
                int selectedCount = Math.Min(4, window.EnhancementWorkspaceCatalogPathsForSmoke.Count);
                if (selectedCount == 0 || !window.SelectRangeForSmoke(0, selectedCount - 1))
                    throw new InvalidOperationException("HTTP batch smoke could not select its isolated fixture sources.");

                string[] selectedSources = window.SelectedFileNamesForSmoke
                    .Select(name => Path.Combine(folder, name))
                    .ToArray();
                var sourceBefore = selectedSources.ToDictionary(
                    static path => path,
                    FileFingerprint,
                    StringComparer.OrdinalIgnoreCase);
                var storesBefore = environment
                    .Where(static pair => Path.HasExtension(pair.Value)
                        && !pair.Key.EndsWith("ENHANCEMENT_JOBS_PATH", StringComparison.Ordinal))
                    .ToDictionary(static pair => pair.Key, pair => FileFingerprint(pair.Value), StringComparer.Ordinal);

                await window.OpenBatchEnhancementForSmokeAsync();
                BatchEnhancementSmokeSnapshot preflight = window.BatchEnhancementForSmoke();
                await window.StartBatchEnhancementDoubleClickForSmokeAsync();
                BatchEnhancementSmokeSnapshot submitted = window.BatchEnhancementForSmoke();
                await window.ViewBatchEnhancementJobsForSmokeAsync();

                EnhancementJobsWorkspaceSmokeSnapshot terminal = window.EnhancementJobsWorkspaceForSmoke();
                for (int attempt = 0; attempt < 240
                    && (terminal.Active > 0 || terminal.Total < submitted.CreatedJobIds.Length); attempt++)
                {
                    await Task.Delay(500);
                    terminal = window.EnhancementJobsWorkspaceForSmoke();
                }

                window.SelectEnhancementJobsFilterForSmoke("completed");
                EnhancementJobsWorkspaceSmokeSnapshot completed = window.EnhancementJobsWorkspaceForSmoke();
                bool terminalHandoffVisible = submitted.CreatedJobIds.All(id =>
                    terminal.VisibleIds.Contains(id, StringComparer.Ordinal));
                bool allCreatedVisible = submitted.CreatedJobIds.All(id =>
                    completed.VisibleIds.Contains(id, StringComparer.Ordinal));
                foreach (string id in submitted.CreatedJobIds)
                    await window.DeleteEnhancementJobOutputForSmokeAsync(id);
                window.SelectEnhancementJobsFilterForSmoke("completed");
                EnhancementJobsWorkspaceSmokeSnapshot afterDelete = window.EnhancementJobsWorkspaceForSmoke();
                window.CloseEnhancementJobsForSmoke();

                var sourceAfter = selectedSources.ToDictionary(
                    static path => path,
                    FileFingerprint,
                    StringComparer.OrdinalIgnoreCase);
                var storesAfter = environment
                    .Where(static pair => Path.HasExtension(pair.Value)
                        && !pair.Key.EndsWith("ENHANCEMENT_JOBS_PATH", StringComparison.Ordinal))
                    .ToDictionary(static pair => pair.Key, pair => FileFingerprint(pair.Value), StringComparer.Ordinal);
                bool sourceUnchanged = sourceBefore.All(pair =>
                    sourceAfter.TryGetValue(pair.Key, out string? fingerprint) && fingerprint == pair.Value);
                bool storesUnchanged = storesBefore.All(pair =>
                    storesAfter.TryGetValue(pair.Key, out string? fingerprint) && fingerprint == pair.Value);
                bool outputsDeleted = !Directory.Exists(outputRoot)
                    || !Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Any();
                bool createdExactlyOnce = submitted.PostRequests == selectedCount
                    && submitted.CreatedJobIds.Length == selectedCount
                    && submitted.CreatedJobIds.Distinct(StringComparer.Ordinal).Count() == selectedCount;
                bool terminalSucceeded = terminal.Active == 0
                    && terminal.Total >= selectedCount
                    && terminal.Highlighted == selectedCount
                    && terminalHandoffVisible
                    && allCreatedVisible;
                bool deletedStateVisible = submitted.CreatedJobIds.All(id =>
                    afterDelete.VisibleIds.Contains(id, StringComparer.Ordinal));

                ok = preflight.PostRequests == 0
                    && preflight.Selected == selectedCount
                    && preflight.Eligible == selectedCount
                    && createdExactlyOnce
                    && submitted.MaxInFlight <= 4
                    && terminalSucceeded
                    && deletedStateVisible
                    && sourceUnchanged
                    && storesUnchanged
                    && outputsDeleted;
                result = new
                {
                    ok,
                    selectedCount,
                    preflightPostZero = preflight.PostRequests == 0,
                    createdExactlyOnce,
                    boundedConcurrency = submitted.MaxInFlight <= 4,
                    submitted,
                    terminalSucceeded,
                    terminalHandoffVisible,
                    terminal,
                    completed,
                    deletedStateVisible,
                    afterDelete,
                    sourceUnchanged,
                    storesUnchanged,
                    outputsDeleted,
                };
            }
            catch (Exception ex)
            {
                result = new
                {
                    ok = false,
                    message = $"HTTP batch smoke failed ({ex.GetType().Name}).",
                };
            }
            finally
            {
                window?.Close();
                foreach ((string key, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(key, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }
}

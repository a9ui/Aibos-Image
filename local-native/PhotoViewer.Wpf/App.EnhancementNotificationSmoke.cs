using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureEnhancementNotificationSmoke(string resultPath)
    {
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-enhancement-notification-").FullName;
        string storesRoot = Path.Combine(smokeRoot, "stores");
        string outputsRoot = Path.Combine(smokeRoot, "outputs");
        Directory.CreateDirectory(storesRoot);
        Directory.CreateDirectory(outputsRoot);
        string statePath = Path.Combine(storesRoot, "state.json");
        string fullResultPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullResultPath)
                ?? throw new InvalidOperationException("Result path has no parent."));

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
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
                ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storesRoot, "favorites.json"),
                ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storesRoot, "seen.json"),
                ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storesRoot, "recent-folders.json"),
                ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storesRoot, "settings.json"),
                ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storesRoot, "albums.json"),
                ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storesRoot, "search-history.json"),
                ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(storesRoot, "jobs.json"),
                ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputsRoot,
                ["PVU_ENHANCE_OUTPUT_ROOT"] = outputsRoot,
                ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storesRoot, "metadata-index"),
            };
            foreach ((string name, string value) in environment)
                Environment.SetEnvironmentVariable(name, value);

            File.WriteAllText(statePath, """
                {
                  "Version": 2,
                  "futureRoot": { "keep": true },
                  "EnhancementNotifications": {
                    "UpscaleSucceeded": false,
                    "futureNotificationMode": "keep"
                  }
                }
                """);

            window = new MainWindow();
            bool surfaceContract =
                window.EnhancementNotificationSurfaceContractForSmoke;
            window.SetEnhancementNotificationPreferencesForSmoke(
                upscaleSucceeded: true,
                upscaleFailed: false,
                photorealSucceeded: true,
                photorealFailed: true,
                videoSucceeded: false,
                videoFailed: true);

            using JsonDocument persisted = JsonDocument.Parse(
                File.ReadAllText(statePath));
            JsonElement root = persisted.RootElement;
            JsonElement preferences = root.GetProperty(
                "EnhancementNotifications");
            bool persistedSettings =
                preferences.GetProperty("UpscaleSucceeded").GetBoolean()
                && !preferences.GetProperty("UpscaleFailed").GetBoolean()
                && preferences.GetProperty("PhotorealSucceeded").GetBoolean()
                && preferences.GetProperty("PhotorealFailed").GetBoolean()
                && !preferences.GetProperty("VideoSucceeded").GetBoolean()
                && preferences.GetProperty("VideoFailed").GetBoolean();
            bool unknownFieldsPreserved =
                root.GetProperty("futureRoot").GetProperty("keep").GetBoolean()
                && preferences.GetProperty("futureNotificationMode")
                    .GetString() == "keep";

            string notificationSqlitePath = Path.Combine(
                storesRoot,
                "notification-reader.sqlite3");
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = notificationSqlitePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString()))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE enhancement_store_metadata (
                      singleton INTEGER PRIMARY KEY,
                      store_version INTEGER NOT NULL,
                      catalog_revision INTEGER NOT NULL
                    );
                    INSERT INTO enhancement_store_metadata
                      (singleton, store_version, catalog_revision)
                      VALUES (1, 1, 2);
                    CREATE TABLE enhancement_jobs (
                      position INTEGER NOT NULL UNIQUE,
                      id TEXT PRIMARY KEY,
                      status TEXT,
                      reader_payload_json TEXT NOT NULL
                    );
                    INSERT INTO enhancement_jobs
                      (position, id, status, reader_payload_json)
                      VALUES
                      (0, 'sqlite-video-failed', 'failed',
                       '{"id":"sqlite-video-failed","status":"failed","operation":"video"}'),
                      (1, 'sqlite-malformed-operation', 'failed',
                       '{"id":"sqlite-malformed-operation","status":"failed","operation":17}');
                    """;
                command.ExecuteNonQuery();
            }
            bool legacySqliteShapeSupported = string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationOperationForSmoke(
                        notificationSqlitePath,
                        "sqlite-video-failed"),
                    "video",
                    StringComparison.Ordinal)
                && string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationOperationForSmoke(
                        notificationSqlitePath,
                        "sqlite-malformed-operation"),
                    "unsupported",
                    StringComparison.Ordinal);

            window.ApplyEnhancementNotificationTerminalForSmoke(
                "historical",
                "upscale",
                "succeeded");
            bool historicalSuppressed =
                !window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationShownCountForSmoke == 0;

            window.TrackEnhancementNotificationJobForSmoke(
                "upscale-success",
                "upscale");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "upscale-success",
                "upscale",
                "succeeded");
            bool upscaleSuccessShown =
                window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationTitleForSmoke
                    .Contains("高画質化が完了", StringComparison.Ordinal)
                && window.EnhancementNotificationShownCountForSmoke == 1;
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "upscale-success",
                "upscale",
                "succeeded");
            bool duplicateSuppressed =
                window.EnhancementNotificationShownCountForSmoke == 1
                && window.PendingEnhancementNotificationCountForSmoke == 0;
            window.DismissEnhancementNotificationForSmoke();

            window.TrackEnhancementNotificationJobForSmoke(
                "upscale-failed-disabled",
                "upscale");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "upscale-failed-disabled",
                "upscale",
                "failed");
            bool disabledOutcomeSuppressed =
                !window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationShownCountForSmoke == 1;

            window.TrackEnhancementNotificationJobForSmoke(
                "malformed-terminal-operation",
                "video");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "malformed-terminal-operation",
                "unsupported",
                "failed");
            bool malformedOperationSuppressed =
                !window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationShownCountForSmoke == 1;

            window.TrackEnhancementNotificationJobForSmoke(
                "video-failed",
                "video");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "video-failed",
                "video",
                "failed");
            bool videoFailureShown =
                window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationTitleForSmoke
                    .Contains("動画化に失敗", StringComparison.Ordinal)
                && window.EnhancementNotificationMessageForSmoke
                    .Contains("現在設定でリトライ", StringComparison.Ordinal)
                && window.EnhancementNotificationShownCountForSmoke == 2;

            window.TrackEnhancementNotificationJobForSmoke(
                "photoreal-success",
                "photoreal");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "photoreal-success",
                "photoreal",
                "succeeded");
            bool secondNotificationQueued =
                window.PendingEnhancementNotificationCountForSmoke == 1;
            window.DismissEnhancementNotificationForSmoke();
            bool queuedNotificationShown =
                window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationTitleForSmoke
                    .Contains("実写化が完了", StringComparison.Ordinal)
                && window.EnhancementNotificationShownCountForSmoke == 3
                && window.PendingEnhancementNotificationCountForSmoke == 0;

            bool success = surfaceContract
                && persistedSettings
                && unknownFieldsPreserved
                && legacySqliteShapeSupported
                && historicalSuppressed
                && upscaleSuccessShown
                && duplicateSuppressed
                && disabledOutcomeSuppressed
                && malformedOperationSuppressed
                && videoFailureShown
                && secondNotificationQueued
                && queuedNotificationShown;
            result = new
            {
                success,
                surfaceContract,
                persistedSettings,
                unknownFieldsPreserved,
                legacySqliteShapeSupported,
                historicalSuppressed,
                upscaleSuccessShown,
                duplicateSuppressed,
                disabledOutcomeSuppressed,
                malformedOperationSuppressed,
                videoFailureShown,
                secondNotificationQueued,
                queuedNotificationShown,
                shownCount = window.EnhancementNotificationShownCountForSmoke,
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
            window?.Close();
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
        Shutdown((bool)(result.GetType().GetProperty("success")?.GetValue(result)
            ?? false)
                ? 0
                : 1);
    }
}

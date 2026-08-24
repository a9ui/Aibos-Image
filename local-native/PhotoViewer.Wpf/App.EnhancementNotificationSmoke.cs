using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureEnhancementNotificationSmoke(
        string resultPath,
        string[] args)
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
            string fixturePath = RequireVideoToolsV2ReaderArgument(
                args,
                "--fixture");
            using JsonDocument fixtureDocument = JsonDocument.Parse(
                File.ReadAllBytes(fixturePath));
            JsonElement readerFixtures = fixtureDocument.RootElement
                .GetProperty("readerFixtures");
            JsonElement editFixture = readerFixtures.GetProperty("edit");
            JsonElement finishFixture = readerFixtures.GetProperty("finish");

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
                  "UiLanguage": "ja",
                  "futureRoot": { "keep": true },
                  "EnhancementNotifications": {
                    "UpscaleSucceeded": false,
                    "VideoSucceeded": false,
                    "VideoFailed": true,
                    "futureNotificationMode": "keep"
                  }
                }
                """);

            window = new MainWindow();
            bool surfaceContract =
                window.EnhancementNotificationSurfaceContractForSmoke;
            bool missingFieldMigration =
                !window.VideoEditSucceededPreferenceForSmoke
                && window.VideoEditFailedPreferenceForSmoke
                && !window.VideoFinishSucceededPreferenceForSmoke
                && window.VideoFinishFailedPreferenceForSmoke;
            window.SetEnhancementNotificationPreferencesForSmoke(
                upscaleSucceeded: true,
                upscaleFailed: false,
                photorealSucceeded: true,
                photorealFailed: true,
                videoSucceeded: false,
                videoFailed: true,
                videoEditSucceeded: true,
                videoEditFailed: false,
                videoFinishSucceeded: false,
                videoFinishFailed: true);

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
                && preferences.GetProperty("VideoFailed").GetBoolean()
                && preferences.GetProperty("VideoEditSucceeded").GetBoolean()
                && !preferences.GetProperty("VideoEditFailed").GetBoolean()
                && !preferences.GetProperty("VideoFinishSucceeded").GetBoolean()
                && preferences.GetProperty("VideoFinishFailed").GetBoolean();
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

            string malformedStatePath = Path.Combine(
                storesRoot,
                "malformed-notification-state.json");
            File.WriteAllText(
                malformedStatePath,
                """
                {
                  "Version": 2,
                  "EnhancementNotifications": {
                    "VideoEditSucceeded": "not-a-boolean"
                  }
                }
                """);
            bool malformedStateSafeDefault = PhotoViewer.Wpf.MainWindow
                .MalformedEnhancementNotificationStateUsesSafeDefaultsForSmoke(
                    malformedStatePath);

            using JsonDocument editQueued = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "11111111-2222-4333-8444-555555555551",
                "queued");
            using JsonDocument editSucceeded = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "11111111-2222-4333-8444-555555555551",
                "succeeded");
            using JsonDocument editFailed = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "11111111-2222-4333-8444-555555555551",
                "failed");
            using JsonDocument finishQueued = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "22222222-3333-4444-8555-666666666662",
                "queued");
            using JsonDocument finishSucceeded =
                CreateVideoToolsV2WorkspaceJob(
                    finishFixture,
                    "22222222-3333-4444-8555-666666666662",
                    "succeeded");
            using JsonDocument finishFailed = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "22222222-3333-4444-8555-666666666662",
                "failed");
            using JsonDocument generationQueued =
                CreateOrdinaryVideoJob("queued");
            using JsonDocument generationSucceeded =
                CreateOrdinaryVideoJob("succeeded");
            using JsonDocument generationFailed =
                CreateOrdinaryVideoJob("failed");

            bool exactKindsClassified = string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationKindForSmoke(
                        editQueued.RootElement),
                    "video-edit",
                    StringComparison.Ordinal)
                && string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationKindForSmoke(
                        finishQueued.RootElement),
                    "video-finish",
                    StringComparison.Ordinal)
                && string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationKindForSmoke(
                        generationQueued.RootElement),
                    "video",
                    StringComparison.Ordinal);

            int beforeDisabledKinds =
                window.EnhancementNotificationShownCountForSmoke;
            window.TrackEnhancementNotificationJobForSmoke(
                editQueued.RootElement);
            window.ApplyEnhancementNotificationTerminalForSmoke(
                editFailed.RootElement);
            window.TrackEnhancementNotificationJobForSmoke(
                finishQueued.RootElement);
            window.ApplyEnhancementNotificationTerminalForSmoke(
                finishSucceeded.RootElement);
            window.TrackEnhancementNotificationJobForSmoke(
                generationQueued.RootElement);
            window.ApplyEnhancementNotificationTerminalForSmoke(
                generationSucceeded.RootElement);
            bool distinctSettingsSuppress =
                window.EnhancementNotificationShownCountForSmoke
                    == beforeDisabledKinds
                && !window.EnhancementNotificationVisibleForSmoke;

            window.SetEnhancementNotificationPreferencesForSmoke(
                upscaleSucceeded: true,
                upscaleFailed: false,
                photorealSucceeded: true,
                photorealFailed: true,
                videoSucceeded: true,
                videoFailed: true,
                videoEditSucceeded: true,
                videoEditFailed: true,
                videoFinishSucceeded: true,
                videoFinishFailed: true);

            bool ShowTerminalNotification(
                JsonElement active,
                JsonElement terminal,
                string expectedTitle)
            {
                int before = window.EnhancementNotificationShownCountForSmoke;
                window.TrackEnhancementNotificationJobForSmoke(active);
                window.ApplyEnhancementNotificationTerminalForSmoke(terminal);
                bool shown = window.EnhancementNotificationVisibleForSmoke
                    && string.Equals(
                        window.EnhancementNotificationTitleForSmoke,
                        expectedTitle,
                        StringComparison.Ordinal)
                    && window.EnhancementNotificationShownCountForSmoke
                        == before + 1;
                window.DismissEnhancementNotificationForSmoke();
                return shown;
            }

            bool editSuccessTitle = ShowTerminalNotification(
                editQueued.RootElement,
                editSucceeded.RootElement,
                "AI動画編集が完了しました");
            bool editFailureTitle = ShowTerminalNotification(
                editQueued.RootElement,
                editFailed.RootElement,
                "AI動画編集に失敗しました");
            bool finishSuccessTitle = ShowTerminalNotification(
                finishQueued.RootElement,
                finishSucceeded.RootElement,
                "AI動画高画質化が完了しました");
            bool finishFailureTitle = ShowTerminalNotification(
                finishQueued.RootElement,
                finishFailed.RootElement,
                "AI動画高画質化に失敗しました");
            bool generationSuccessTitle = ShowTerminalNotification(
                generationQueued.RootElement,
                generationSucceeded.RootElement,
                "AI動画生成が完了しました");
            bool generationFailureTitle = ShowTerminalNotification(
                generationQueued.RootElement,
                generationFailed.RootElement,
                "AI動画生成に失敗しました");

            using JsonDocument malformedV2 = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "33333333-4444-4555-8666-777777777773",
                "queued",
                video => video.Remove("delivery"),
                refreshPresetHash: true);
            using JsonDocument futureSchemaV2 =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "44444444-5555-4666-8777-888888888884",
                    "queued",
                    video => video["schemaVersion"] = 3,
                    refreshPresetHash: true);
            using JsonDocument futureProtocolV2 =
                CreateVideoToolsV2WorkspaceJob(
                    finishFixture,
                    "55555555-6666-4777-8888-999999999995",
                    "queued",
                    video => video["protocol"] =
                        "aibos-enhancement-video-tools-v3",
                    refreshPresetHash: true);
            int beforeProtectedRows =
                window.EnhancementNotificationShownCountForSmoke;
            bool unknownFutureProtected = new[]
                {
                    malformedV2.RootElement,
                    futureSchemaV2.RootElement,
                    futureProtocolV2.RootElement,
                }
                .All(job => string.Equals(
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementNotificationKindForSmoke(job),
                    "unsupported",
                    StringComparison.Ordinal));
            foreach (JsonElement protectedJob in new[]
                     {
                         malformedV2.RootElement,
                         futureSchemaV2.RootElement,
                         futureProtocolV2.RootElement,
                     })
            {
                window.TrackEnhancementNotificationJobForSmoke(protectedJob);
                window.ApplyEnhancementNotificationTerminalForSmoke(
                    protectedJob);
            }
            unknownFutureProtected &=
                window.EnhancementNotificationShownCountForSmoke
                    == beforeProtectedRows
                && !window.EnhancementNotificationVisibleForSmoke;

            int beforePassive =
                window.EnhancementNotificationShownCountForSmoke;
            _ = window.OpenVideoEditV2ForSmoke();
            _ = PhotoViewer.Wpf.MainWindow
                .ReadEnhancementNotificationKindForSmoke(
                    editQueued.RootElement);
            bool passiveReadNoNotification =
                window.EnhancementNotificationShownCountForSmoke
                    == beforePassive
                && !window.EnhancementNotificationVisibleForSmoke;

            bool compileReviewNotified =
                window.NotifyVideoEditCompileReviewForSmoke(
                    exactCandidateAccepted: true,
                    stale: false,
                    skipReviewAuthorization: false)
                && string.Equals(
                    window.EnhancementNotificationTitleForSmoke,
                    "編集指示を整えました。変換結果を確認してください",
                    StringComparison.Ordinal);
            window.DismissEnhancementNotificationForSmoke();
            int beforeCompileNegative =
                window.EnhancementNotificationShownCountForSmoke;
            bool compileResponseOnlyNoNotification =
                !window.NotifyVideoEditCompileReviewForSmoke(
                    exactCandidateAccepted: false,
                    stale: false,
                    skipReviewAuthorization: false)
                && !window.NotifyVideoEditCompileReviewForSmoke(
                    exactCandidateAccepted: true,
                    stale: true,
                    skipReviewAuthorization: false)
                && !window.NotifyVideoEditCompileReviewForSmoke(
                    exactCandidateAccepted: true,
                    stale: false,
                    skipReviewAuthorization: true)
                && window.EnhancementNotificationShownCountForSmoke
                    == beforeCompileNegative;

            bool skipAcceptedStartNotified =
                window.NotifyVideoEditStartAcceptedForSmoke(
                    skipReviewAuthorization: true,
                    acceptedOrSaved: true)
                && string.Equals(
                    window.EnhancementNotificationTitleForSmoke,
                    "AI動画編集を開始しました",
                    StringComparison.Ordinal);
            window.DismissEnhancementNotificationForSmoke();
            int beforeStartNegative =
                window.EnhancementNotificationShownCountForSmoke;
            bool disabledOrManualStartNoNotification =
                !window.NotifyVideoEditStartAcceptedForSmoke(
                    skipReviewAuthorization: true,
                    acceptedOrSaved: false)
                && !window.NotifyVideoEditStartAcceptedForSmoke(
                    skipReviewAuthorization: false,
                    acceptedOrSaved: true)
                && window.EnhancementNotificationShownCountForSmoke
                    == beforeStartNegative;

            window.SetEnhancementNotificationPreferencesForSmoke(
                upscaleSucceeded: true,
                upscaleFailed: false,
                photorealSucceeded: true,
                photorealFailed: true,
                videoSucceeded: false,
                videoFailed: true,
                videoEditSucceeded: false,
                videoEditFailed: false,
                videoFinishSucceeded: false,
                videoFinishFailed: true);
            int beforeDisabledPromptFlow =
                window.EnhancementNotificationShownCountForSmoke;
            bool notificationSettingOffSuppressesPromptFlow =
                !window.NotifyVideoEditCompileReviewForSmoke(
                    exactCandidateAccepted: true,
                    stale: false,
                    skipReviewAuthorization: false)
                && !window.NotifyVideoEditStartAcceptedForSmoke(
                    skipReviewAuthorization: true,
                    acceptedOrSaved: true)
                && window.EnhancementNotificationShownCountForSmoke
                    == beforeDisabledPromptFlow;

            window.SetEnhancementNotificationPreferencesForSmoke(
                upscaleSucceeded: true,
                upscaleFailed: false,
                photorealSucceeded: true,
                photorealFailed: true,
                videoSucceeded: false,
                videoFailed: true,
                videoEditSucceeded: true,
                videoEditFailed: false,
                videoFinishSucceeded: false,
                videoFinishFailed: true);
            int legacyPresentationBaseline =
                window.EnhancementNotificationShownCountForSmoke;

            window.ApplyEnhancementNotificationTerminalForSmoke(
                "historical",
                "upscale",
                "succeeded");
            bool historicalSuppressed =
                !window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline;

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
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 1;
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "upscale-success",
                "upscale",
                "succeeded");
            bool duplicateSuppressed =
                window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 1
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
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 1;

            window.TrackEnhancementNotificationJobForSmoke(
                "malformed-terminal-operation",
                "video");
            window.ApplyEnhancementNotificationTerminalForSmoke(
                "malformed-terminal-operation",
                "unsupported",
                "failed");
            bool malformedOperationSuppressed =
                !window.EnhancementNotificationVisibleForSmoke
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 1;

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
                    .Contains("AI動画生成に失敗", StringComparison.Ordinal)
                && window.EnhancementNotificationMessageForSmoke
                    .Contains("現在設定でリトライ", StringComparison.Ordinal)
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 2;

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
                && window.EnhancementNotificationShownCountForSmoke
                    == legacyPresentationBaseline + 3
                && window.PendingEnhancementNotificationCountForSmoke == 0;

            bool success = surfaceContract
                && missingFieldMigration
                && persistedSettings
                && unknownFieldsPreserved
                && legacySqliteShapeSupported
                && malformedStateSafeDefault
                && exactKindsClassified
                && distinctSettingsSuppress
                && editSuccessTitle
                && editFailureTitle
                && finishSuccessTitle
                && finishFailureTitle
                && generationSuccessTitle
                && generationFailureTitle
                && unknownFutureProtected
                && passiveReadNoNotification
                && compileReviewNotified
                && compileResponseOnlyNoNotification
                && skipAcceptedStartNotified
                && disabledOrManualStartNoNotification
                && notificationSettingOffSuppressesPromptFlow
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
                missingFieldMigration,
                persistedSettings,
                unknownFieldsPreserved,
                legacySqliteShapeSupported,
                malformedStateSafeDefault,
                exactKindsClassified,
                distinctSettingsSuppress,
                editSuccessTitle,
                editFailureTitle,
                finishSuccessTitle,
                finishFailureTitle,
                generationSuccessTitle,
                generationFailureTitle,
                unknownFutureProtected,
                passiveReadNoNotification,
                compileReviewNotified,
                compileResponseOnlyNoNotification,
                skipAcceptedStartNotified,
                disabledOrManualStartNoNotification,
                notificationSettingOffSuppressesPromptFlow,
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

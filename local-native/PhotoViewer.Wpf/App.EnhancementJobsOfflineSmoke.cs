using Microsoft.Data.Sqlite;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureEnhancementJobsOfflineSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resultFullPath.StartsWith(
                tempRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(1);
            return;
        }

        string fixtureRoot = Path.Combine(
            Path.GetDirectoryName(resultFullPath)!,
            "offline-fixture");
        string storesRoot = Path.Combine(fixtureRoot, "stores");
        string jobsPath = Path.Combine(storesRoot, "enhance", "jobs.sqlite3");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storesRoot, "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storesRoot, "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storesRoot, "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storesRoot, "recent.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storesRoot, "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storesRoot, "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storesRoot, "search.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storesRoot, "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(storesRoot);
            WriteEnhancementJobsHistoryWindowSqliteFixture(
                jobsPath,
                queuedCount: 1,
                runningCount: 1,
                terminalCount: 2);
            byte[] initialStoreBytes = File.ReadAllBytes(jobsPath);
            foreach ((string key, string value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            int starterCalls = 0;
            int identityProbes = 0;
            int jobApiTransportCalls = 0;
            window = HiddenWindow();
            window.ConfigureEnhancementCompanionAutoStartForSmoke(
                (request, _) =>
                {
                    if (request.Method == HttpMethod.Get
                        && request.RequestUri?.AbsolutePath
                            == "/api/enhance/identity")
                    {
                        identityProbes++;
                    }
                    else
                    {
                        jobApiTransportCalls++;
                    }
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException(
                            "Synthetic Companion is intentionally offline."));
                },
                _ =>
                {
                    starterCalls++;
                    return (false, "Offline smoke must never start a process.");
                });

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window.Show();
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                object result = new
                {
                    ok = false,
                    message = "Offline Jobs smoke did not complete.",
                };
                bool ok = false;
                try
                {
                    int ordinaryProbeCount =
                        window.EnhancementStoreProbeCountForSmoke;
                    await Task.Delay(3_500);
                    bool ordinaryIdleExact =
                        !window.ActiveEnhancementRevisionWatcherRunningForSmoke
                        && window.EnhancementStoreProbeCountForSmoke
                            == ordinaryProbeCount
                        && starterCalls == 0
                        && identityProbes == 0
                        && jobApiTransportCalls == 0;

                    await window.OpenEnhancementJobsForSmokeAsync();
                    EnhancementJobsWorkspaceSmokeSnapshot initial =
                        window.EnhancementJobsWorkspaceForSmoke();
                    byte[] afterInitialRead = File.ReadAllBytes(jobsPath);
                    int initialInventoryReads = initial.GetRequests;
                    int initialIdentityProbes = identityProbes;
                    await Task.Delay(1_250);
                    EnhancementJobsWorkspaceSmokeSnapshot afterIdle =
                        window.EnhancementJobsWorkspaceForSmoke();
                    bool initialStoreUnchanged =
                        initialStoreBytes.AsSpan().SequenceEqual(
                            afterInitialRead);
                    bool passiveOfflineExact =
                        initial.Visible
                        && initial.Total == 4
                        && initial.Active == 2
                        && initial.VisibleIds.SequenceEqual(
                            [
                                "history-running-0000",
                                "history-queued-0000",
                                "history-terminal-0001",
                                "history-terminal-0000",
                            ],
                            StringComparer.Ordinal)
                        && initial.HealthState == "Health unavailable"
                        && initial.QueuePauseLabel == "接続して再開"
                        && initial.QueuePauseEnabled
                        && !initial.Polling
                        && initial.Status.Contains(
                            "自動更新は停止",
                            StringComparison.Ordinal)
                        && starterCalls == 0
                        && jobApiTransportCalls == 0
                        && initialIdentityProbes == 1
                        && initialStoreUnchanged;
                    bool idleStable =
                        !afterIdle.Polling
                        && afterIdle.GetRequests == initialInventoryReads
                        && identityProbes == initialIdentityProbes
                        && starterCalls == 0
                        && jobApiTransportCalls == 0;

                    await window.RefreshEnhancementJobsForSmokeAsync();
                    EnhancementJobsWorkspaceSmokeSnapshot manualRefresh =
                        window.EnhancementJobsWorkspaceForSmoke();
                    bool manualRefreshExact =
                        manualRefresh.Total == initial.Total
                        && manualRefresh.VisibleIds.SequenceEqual(
                            initial.VisibleIds,
                            StringComparer.Ordinal)
                        && !manualRefresh.Polling
                        && manualRefresh.GetRequests
                            == initialInventoryReads + 1
                        && identityProbes == initialIdentityProbes + 1
                        && starterCalls == 0
                        && jobApiTransportCalls == 0
                        && initialStoreBytes.AsSpan().SequenceEqual(
                            File.ReadAllBytes(jobsPath));

                    MutateOfflineJobsFixture(
                        jobsPath,
                        "UPDATE enhancement_store_metadata SET catalog_revision = 2 WHERE singleton = 1;");
                    byte[] revisionBeforeRead = File.ReadAllBytes(jobsPath);
                    int probesBeforeRevision =
                        window.EnhancementStoreProbeCountForSmoke;
                    bool revisionApplied =
                        window.RefreshEnhancedStateIfChangedForSmoke();
                    bool revisionChangeExact =
                        revisionApplied
                        && window.EnhancementStoreProbeCountForSmoke
                            == probesBeforeRevision + 1
                        && !window.ActiveEnhancementRevisionWatcherRunningForSmoke
                        && revisionBeforeRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(jobsPath));

                    MutateOfflineJobsFixture(
                        jobsPath,
                        "UPDATE enhancement_store_metadata SET store_version = 2 WHERE singleton = 1;");
                    byte[] futureBeforeRead = File.ReadAllBytes(jobsPath);
                    await window.RefreshEnhancementJobsForSmokeAsync();
                    EnhancementJobsWorkspaceSmokeSnapshot future =
                        window.EnhancementJobsWorkspaceForSmoke();
                    bool futureStoreUnchanged =
                        futureBeforeRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(jobsPath));
                    bool futureRejected =
                        future.Total == initial.Total
                        && future.VisibleIds.SequenceEqual(
                            initial.VisibleIds,
                            StringComparer.Ordinal)
                        && future.Status.Contains(
                            "last valid snapshot",
                            StringComparison.OrdinalIgnoreCase)
                        && !future.Polling
                        && futureStoreUnchanged;

                    MutateOfflineJobsFixture(
                        jobsPath,
                        """
                        UPDATE enhancement_store_metadata
                        SET store_version = 1
                        WHERE singleton = 1;
                        UPDATE enhancement_jobs
                        SET reader_payload_json = '{'
                        WHERE id = 'history-terminal-0001';
                        """);
                    byte[] malformedBeforeRead = File.ReadAllBytes(jobsPath);
                    await window.RefreshEnhancementJobsForSmokeAsync();
                    EnhancementJobsWorkspaceSmokeSnapshot malformed =
                        window.EnhancementJobsWorkspaceForSmoke();
                    bool malformedStoreUnchanged =
                        malformedBeforeRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(jobsPath));
                    bool malformedRejected =
                        malformed.Total == initial.Total
                        && malformed.VisibleIds.SequenceEqual(
                            initial.VisibleIds,
                            StringComparer.Ordinal)
                        && malformed.Status.Contains(
                            "last valid snapshot",
                            StringComparison.OrdinalIgnoreCase)
                        && !malformed.Polling
                        && malformedStoreUnchanged;

                    bool explicitDurableWatcherExact =
                        window.ActivateEnhancementDurableRevisionWatcherForSmoke()
                        && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                        && starterCalls == 0
                        && jobApiTransportCalls == 0;

                    bool noMutationOrStart =
                        starterCalls == 0
                        && jobApiTransportCalls == 0;
                    ok = passiveOfflineExact
                        && ordinaryIdleExact
                        && idleStable
                        && manualRefreshExact
                        && revisionChangeExact
                        && futureRejected
                        && malformedRejected
                        && explicitDurableWatcherExact
                        && noMutationOrStart;
                    result = new
                    {
                        ok,
                        ordinaryIdleExact,
                        passiveOfflineExact,
                        idleStable,
                        manualRefreshExact,
                        revisionChangeExact,
                        futureRejected,
                        malformedRejected,
                        explicitDurableWatcherExact,
                        noMutationOrStart,
                        starterCalls,
                        identityProbes,
                        jobApiTransportCalls,
                        initialInventoryReads,
                        finalInventoryReads = malformed.GetRequests,
                        ordinaryProbeCount,
                        finalProbeCount =
                            window.EnhancementStoreProbeCountForSmoke,
                        initial = new
                        {
                            initial.Total,
                            initial.Active,
                            initial.VisibleIds,
                            initial.HealthState,
                            initial.QueuePauseLabel,
                            initial.QueuePauseEnabled,
                            initial.Polling,
                            initial.Status,
                            storeUnchanged = initialStoreUnchanged,
                        },
                        future = new
                        {
                            future.Total,
                            future.VisibleIds,
                            future.Polling,
                            future.Status,
                            storeUnchanged = futureStoreUnchanged,
                        },
                        malformed = new
                        {
                            malformed.Total,
                            malformed.VisibleIds,
                            malformed.Polling,
                            malformed.Status,
                            storeUnchanged = malformedStoreUnchanged,
                        },
                        actualCompanionStarted = false,
                        actualUserStoreRead = false,
                    };
                }
                catch (Exception ex)
                {
                    result = new
                    {
                        ok = false,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                    };
                }
                finally
                {
                    try { window.Close(); } catch { }
                    foreach ((string key, string? value) in previousEnvironment)
                        Environment.SetEnvironmentVariable(key, value);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
                        JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(ok ? 0 : 1);
                }
            }, DispatcherPriority.ApplicationIdle);
        }
        catch
        {
            try { window?.Close(); } catch { }
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Shutdown(1);
        }
    }

    private static void MutateOfflineJobsFixture(
        string jobsPath,
        string commandText)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = jobsPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        connection.Open();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = commandText;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }
}

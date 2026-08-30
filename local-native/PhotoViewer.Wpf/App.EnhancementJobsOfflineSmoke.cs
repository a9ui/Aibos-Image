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
        string savedDeliveryJobsPath = Path.Combine(
            storesRoot,
            "enhance",
            "saved-delivery-jobs.sqlite3");
        const string savedDeliverySourcePath =
            @"X:\synthetic\saved-delivery.png";
        const string stalePreArmSourcePath =
            @"X:\synthetic\stale-pre-arm.png";
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
            WriteEnhancementJobsHistoryWindowSqliteFixture(
                savedDeliveryJobsPath,
                queuedCount: 0,
                runningCount: 0,
                terminalCount: 0);
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
                        && !string.IsNullOrWhiteSpace(initial.HealthState)
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
                        && !string.IsNullOrWhiteSpace(future.Status)
                        && !string.Equals(
                            future.Status,
                            initial.Status,
                            StringComparison.Ordinal)
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
                        && !string.IsNullOrWhiteSpace(malformed.Status)
                        && !string.Equals(
                            malformed.Status,
                            initial.Status,
                            StringComparison.Ordinal)
                        && !malformed.Polling
                        && malformedStoreUnchanged;

                    bool explicitDurableWatcherExact =
                        window.ActivateEnhancementDurableRevisionWatcherForSmoke()
                        && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                        && starterCalls == 0
                        && jobApiTransportCalls == 0;

                    window.Close();
                    window = null;
                    Environment.SetEnvironmentVariable(
                        "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                        savedDeliveryJobsPath);
                    int savedDeliveryIdentityProbeStart = identityProbes;
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
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                    bool savedDeliveryBaselineLoaded =
                        window.RefreshEnhancedStateIfChangedForSmoke();
                    int emptyIdleProbeCount =
                        window.EnhancementStoreProbeCountForSmoke;
                    await Task.Delay(100);
                    bool savedDeliveryEmptyIdleExact =
                        savedDeliveryBaselineLoaded
                        && window.EnhancementJobsReadForSmoke == 0
                        && window.EnhancementCatalogRevisionForSmoke == 1
                        && !window.ActiveEnhancementRevisionWatcherRunningForSmoke
                        && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke
                        && window.EnhancementStoreProbeCountForSmoke
                            == emptyIdleProbeCount;
                    byte[] emptyStoreBeforeBoundedWatches =
                        File.ReadAllBytes(savedDeliveryJobsPath);

                    window.ConfigureSavedForDeliveryRevisionWatcherForSmoke(
                        intervalMilliseconds: 20,
                        maximumProbeCount: 2,
                        maximumElapsedMilliseconds: 5_000);
                    int countBoundProbeStart =
                        window.EnhancementStoreProbeCountForSmoke;
                    bool countBoundArmed =
                        window.ActivateSavedForDeliveryRevisionWatcherForSmoke();
                    bool countBoundStopped =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => !window.ActiveEnhancementRevisionWatcherRunningForSmoke,
                            timeoutMilliseconds: 2_000);
                    int countBoundProbeCount =
                        window.EnhancementStoreProbeCountForSmoke
                        - countBoundProbeStart;
                    bool savedDeliveryCountBound =
                        countBoundArmed
                        && countBoundStopped
                        && countBoundProbeCount == 2
                        && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke
                        && window.EnhancementCatalogRevisionForSmoke == 1;

                    window.ConfigureSavedForDeliveryRevisionWatcherForSmoke(
                        intervalMilliseconds: 20,
                        maximumProbeCount: 1_000,
                        maximumElapsedMilliseconds: 300);
                    int elapsedBoundProbeStart =
                        window.EnhancementStoreProbeCountForSmoke;
                    long elapsedBoundStartTick = Environment.TickCount64;
                    bool elapsedBoundArmed =
                        window.ActivateSavedForDeliveryRevisionWatcherForSmoke();
                    bool elapsedBoundStopped =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => !window.ActiveEnhancementRevisionWatcherRunningForSmoke,
                            timeoutMilliseconds: 2_000);
                    int elapsedBoundProbeCount =
                        window.EnhancementStoreProbeCountForSmoke
                        - elapsedBoundProbeStart;
                    long elapsedBoundMilliseconds =
                        Environment.TickCount64 - elapsedBoundStartTick;
                    bool savedDeliveryElapsedBound =
                        elapsedBoundArmed
                        && elapsedBoundStopped
                        && elapsedBoundProbeCount > 0
                        && elapsedBoundProbeCount < 1_000
                        && elapsedBoundMilliseconds >= 250
                        && elapsedBoundMilliseconds < 2_000
                        && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke
                        && window.EnhancementCatalogRevisionForSmoke == 1;
                    bool savedDeliveryBoundsReadOnly =
                        emptyStoreBeforeBoundedWatches.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));

                    window.ConfigureSavedForDeliveryRevisionWatcherForSmoke(
                        intervalMilliseconds: 20,
                        maximumProbeCount: 100,
                        maximumElapsedMilliseconds: 5_000);
                    int recoveryProbeStart =
                        window.EnhancementStoreProbeCountForSmoke;
                    bool savedDeliveryEmptyActivation =
                        window.ActivateSavedForDeliveryRevisionWatcherForSmoke()
                        && window.SavedForDeliveryCatalogAdoptionPendingForSmoke;
                    WriteSavedDeliveryEmptyCatalogRevision(
                        savedDeliveryJobsPath,
                        catalogRevision: 2);
                    bool savedDeliveryZeroActiveRevisionKeptWatch =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 2
                                && !window.HasActiveEnhancementQueueActivityForSmoke(
                                    savedDeliverySourcePath)
                                && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "queued",
                        catalogRevision: 3);
                    byte[] queuedStoreBeforeWatcherRead =
                        File.ReadAllBytes(savedDeliveryJobsPath);
                    bool savedDeliveryQueuedObserved =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 3
                                && window.HasActiveEnhancementQueueActivityForSmoke(
                                    savedDeliverySourcePath)
                                && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    int recoveryProbeCount =
                        window.EnhancementStoreProbeCountForSmoke
                        - recoveryProbeStart;
                    bool queuedWatcherReadOnly =
                        queuedStoreBeforeWatcherRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));

                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "canceled",
                        catalogRevision: 4);
                    byte[] terminalStoreBeforeWatcherRead =
                        File.ReadAllBytes(savedDeliveryJobsPath);
                    bool savedDeliveryTerminalStopped =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 4
                                && !window.HasActiveEnhancementQueueActivityForSmoke(
                                    savedDeliverySourcePath)
                                && !window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    bool terminalWatcherReadOnly =
                        terminalStoreBeforeWatcherRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));

                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "queued",
                        catalogRevision: 5,
                        jobId: "stale-pre-arm-active",
                        sourcePath: stalePreArmSourcePath,
                        position: 1);
                    bool savedDeliveryStaleBaselineActivation =
                        window.ActivateSavedForDeliveryRevisionWatcherForSmoke()
                        && window.SavedForDeliveryCatalogAdoptionPendingForSmoke;
                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "running",
                        catalogRevision: 6,
                        jobId: "stale-pre-arm-active",
                        sourcePath: stalePreArmSourcePath,
                        position: 1);
                    byte[] staleActiveStoreBeforeWatcherRead =
                        File.ReadAllBytes(savedDeliveryJobsPath);
                    bool savedDeliveryStaleActiveKeptWatch =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 6
                                && window.HasActiveEnhancementQueueActivityForSmoke(
                                    stalePreArmSourcePath)
                                && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    bool staleActiveWatcherReadOnly =
                        staleActiveStoreBeforeWatcherRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));

                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "queued",
                        catalogRevision: 7);
                    byte[] staleRaceRecoveryStoreBeforeWatcherRead =
                        File.ReadAllBytes(savedDeliveryJobsPath);
                    bool savedDeliveryStaleBaselineRecoveryObserved =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 7
                                && window.HasActiveEnhancementQueueActivityForSmoke(
                                    stalePreArmSourcePath)
                                && window.HasActiveEnhancementQueueActivityForSmoke(
                                    savedDeliverySourcePath)
                                && window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    bool staleRaceRecoveryWatcherReadOnly =
                        staleRaceRecoveryStoreBeforeWatcherRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));

                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "canceled",
                        catalogRevision: 8,
                        jobId: "stale-pre-arm-active",
                        sourcePath: stalePreArmSourcePath,
                        position: 1);
                    WriteSavedDeliveryAdoptionFixtureState(
                        savedDeliveryJobsPath,
                        status: "canceled",
                        catalogRevision: 9);
                    byte[] staleRaceTerminalStoreBeforeWatcherRead =
                        File.ReadAllBytes(savedDeliveryJobsPath);
                    bool savedDeliveryStaleBaselineTerminalStopped =
                        await WaitForEnhancementJobsOfflineSmokeConditionAsync(
                            () => window.EnhancementCatalogRevisionForSmoke == 9
                                && !window.HasActiveEnhancementQueueActivityForSmoke(
                                    stalePreArmSourcePath)
                                && !window.HasActiveEnhancementQueueActivityForSmoke(
                                    savedDeliverySourcePath)
                                && !window.ActiveEnhancementRevisionWatcherRunningForSmoke
                                && !window.SavedForDeliveryCatalogAdoptionPendingForSmoke,
                            timeoutMilliseconds: 3_000);
                    bool staleRaceTerminalWatcherReadOnly =
                        staleRaceTerminalStoreBeforeWatcherRead.AsSpan().SequenceEqual(
                            File.ReadAllBytes(savedDeliveryJobsPath));
                    bool savedDeliveryStaleActiveBaselineExact =
                        savedDeliveryStaleBaselineActivation
                        && savedDeliveryStaleActiveKeptWatch
                        && savedDeliveryStaleBaselineRecoveryObserved
                        && savedDeliveryStaleBaselineTerminalStopped;
                    bool savedDeliveryWatchReadOnly =
                        savedDeliveryBoundsReadOnly
                        && queuedWatcherReadOnly
                        && terminalWatcherReadOnly
                        && staleActiveWatcherReadOnly
                        && staleRaceRecoveryWatcherReadOnly
                        && staleRaceTerminalWatcherReadOnly;
                    bool savedDeliveryRecoveryExact =
                        savedDeliveryEmptyIdleExact
                        && savedDeliveryCountBound
                        && savedDeliveryElapsedBound
                        && savedDeliveryEmptyActivation
                        && savedDeliveryZeroActiveRevisionKeptWatch
                        && savedDeliveryQueuedObserved
                        && recoveryProbeCount > 0
                        && recoveryProbeCount <= 100
                        && savedDeliveryTerminalStopped
                        && savedDeliveryStaleActiveBaselineExact
                        && savedDeliveryWatchReadOnly
                        && starterCalls == 0
                        && identityProbes == savedDeliveryIdentityProbeStart
                        && jobApiTransportCalls == 0;

                    bool upscaleMutationGateExact =
                        ValidateUpscaleMutationGateForOfflineSmoke();
                    bool upscaleMutationIdentityExact =
                        ValidateUpscaleMutationIdentityForOfflineSmoke();
                    bool videoCancellationUnchanged =
                        ValidateVideoCancellationWithoutUpscaleGateForOfflineSmoke();

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
                        && savedDeliveryRecoveryExact
                        && upscaleMutationGateExact
                        && upscaleMutationIdentityExact
                        && videoCancellationUnchanged
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
                        savedDeliveryEmptyIdleExact,
                        savedDeliveryCountBound,
                        savedDeliveryElapsedBound,
                        savedDeliveryEmptyActivation,
                        savedDeliveryZeroActiveRevisionKeptWatch,
                        savedDeliveryQueuedObserved,
                        savedDeliveryTerminalStopped,
                        savedDeliveryStaleActiveBaselineExact,
                        savedDeliveryWatchReadOnly,
                        savedDeliveryRecoveryExact,
                        upscaleMutationGateExact,
                        upscaleMutationIdentityExact,
                        videoCancellationUnchanged,
                        noMutationOrStart,
                        starterCalls,
                        identityProbes,
                        jobApiTransportCalls,
                        initialInventoryReads,
                        finalInventoryReads = malformed.GetRequests,
                        ordinaryProbeCount,
                        finalProbeCount =
                            window.EnhancementStoreProbeCountForSmoke,
                        countBoundProbeCount,
                        elapsedBoundProbeCount,
                        elapsedBoundMilliseconds,
                        recoveryProbeCount,
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
                    try { window?.Close(); } catch { }
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

    private static bool ValidateUpscaleMutationGateForOfflineSmoke()
    {
        using JsonDocument validQueuedDocument =
            BuildUpscaleMutationSmokeProjection("queued", "true");
        using JsonDocument validFailedDocument =
            BuildUpscaleMutationSmokeProjection("failed", "true");
        using JsonDocument validSucceededDocument =
            BuildUpscaleMutationSmokeProjection("succeeded", "true");
        UpscaleMutationSurfaceSmokeSnapshot? validQueued =
            global::PhotoViewer.Wpf.MainWindow.ReadUpscaleMutationSurfaceForSmoke(
                validQueuedDocument.RootElement);
        UpscaleMutationSurfaceSmokeSnapshot? validFailed =
            global::PhotoViewer.Wpf.MainWindow.ReadUpscaleMutationSurfaceForSmoke(
                validFailedDocument.RootElement);
        UpscaleMutationSurfaceSmokeSnapshot? validSucceeded =
            global::PhotoViewer.Wpf.MainWindow.ReadUpscaleMutationSurfaceForSmoke(
                validSucceededDocument.RootElement);
        if (validQueued is not
            {
                UpscaleMutationSafe: true,
                ReaderOnly: false,
                ProtectedReaderOnly: false,
                SupportedMutation: true,
                CanCancel: true,
                CanRetry: false,
                CanDismiss: false,
                CanReorder: true,
                ActionPresentationExact: true,
            }
            || !validQueued.VisibleActionKinds.SequenceEqual(
                ["move-up", "move-down", "move-next", "cancel"],
                StringComparer.Ordinal)
            || validFailed is not
            {
                UpscaleMutationSafe: true,
                ReaderOnly: false,
                ProtectedReaderOnly: false,
                SupportedMutation: true,
                CanCancel: true,
                CanRetry: true,
                CanDismiss: true,
                CanReorder: false,
                ActionPresentationExact: true,
            }
            || validSucceeded is not
            {
                UpscaleMutationSafe: true,
                ReaderOnly: false,
                ProtectedReaderOnly: false,
                SupportedMutation: true,
                CanCancel: false,
                CanRetry: false,
                CanDismiss: false,
                CanReorder: false,
                CanUseOutput: true,
                CanDeleteOutput: true,
                ActionPresentationExact: true,
            })
        {
            return false;
        }

        string[][] invalidGateCases =
        [
            [],
            ["false"],
            ["null"],
            ["\"true\""],
            ["1"],
            ["{}"],
            ["[]"],
            ["true", "true"],
            ["true", "false"],
        ];
        foreach (string[] gateValues in invalidGateCases)
        {
            foreach (string status in new[]
                { "queued", "failed", "succeeded" })
            {
                using JsonDocument invalidDocument =
                    BuildUpscaleMutationSmokeProjection(
                        status,
                        gateValues);
                UpscaleMutationSurfaceSmokeSnapshot? invalid =
                    global::PhotoViewer.Wpf.MainWindow.ReadUpscaleMutationSurfaceForSmoke(
                        invalidDocument.RootElement);
                if (invalid is not
                    {
                        UpscaleMutationSafe: false,
                        ReaderOnly: true,
                        ProtectedReaderOnly: true,
                        SupportedMutation: false,
                        CanCancel: false,
                        CanRetry: false,
                        CanDismiss: false,
                        CanReorder: false,
                        CanUseOutput: false,
                        CanDeleteOutput: false,
                        ActionPresentationExact: true,
                    }
                    || invalid.VisibleActionKinds.Length != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateUpscaleMutationIdentityForOfflineSmoke()
    {
        using JsonDocument safeDocument =
            BuildUpscaleMutationSmokeProjection("queued", "true");
        using JsonDocument safeTwinDocument =
            BuildUpscaleMutationSmokeProjection("queued", "true");
        using JsonDocument protectedDocument =
            BuildUpscaleMutationSmokeProjection("queued", "false");
        bool safeComparisonRead = global::PhotoViewer.Wpf.MainWindow
            .TryCompareUpscaleWorkspaceImmutableIdentityForSmoke(
                safeDocument.RootElement,
                safeTwinDocument.RootElement,
                out bool safeIdentityMatches);
        bool gateComparisonRead = global::PhotoViewer.Wpf.MainWindow
            .TryCompareUpscaleWorkspaceImmutableIdentityForSmoke(
                safeDocument.RootElement,
                protectedDocument.RootElement,
                out bool gateChangeMatches);
        return safeComparisonRead
            && safeIdentityMatches
            && gateComparisonRead
            && !gateChangeMatches;
    }

    private static bool
        ValidateVideoCancellationWithoutUpscaleGateForOfflineSmoke()
    {
        using JsonDocument videoDocument = JsonDocument.Parse(
            """
            {
              "id": "video-cancel-smoke",
              "sourceId": "X:\\synthetic\\video-source.png",
              "sourcePath": "X:\\synthetic\\video-source.png",
              "presetId": "wan22-ti2v-5b-normal-v1",
              "adapterId": "comfyui-wan22-ti2v",
              "operation": "video",
              "status": "queued",
              "queueOrder": 0,
              "createdAt": "2026-08-30T00:00:00.000Z",
              "updatedAt": "2026-08-30T00:00:01.000Z"
            }
            """);
        return global::PhotoViewer.Wpf.MainWindow
            .TryReadEnhancementJobCancellationForSmoke(
                videoDocument.RootElement,
                out bool fullMutationSafe,
                out bool canCancel,
                out bool cancelVisible,
                out bool cancelEnabled,
                out string cancelLabel)
            && !fullMutationSafe
            && canCancel
            && cancelVisible
            && cancelEnabled
            && cancelLabel == "待機から外す";
    }

    private static JsonDocument BuildUpscaleMutationSmokeProjection(
        string status,
        params string[] gateValues)
    {
        string gateMembers = "";
        foreach (string gateValue in gateValues)
        {
            gateMembers +=
                ",\"upscaleMutationSafeV1\":" + gateValue;
        }
        string json =
            "{\"id\":\"upscale-gate-smoke\""
            + ",\"sourceId\":\"X:\\\\synthetic\\\\source.png\""
            + ",\"sourcePath\":\"X:\\\\synthetic\\\\source.png\""
            + ",\"presetId\":\"photo-detail-x4\""
            + ",\"adapterId\":\"realesrgan-ncnn\""
            + ",\"operation\":\"upscale\""
            + gateMembers
            + ",\"status\":" + JsonSerializer.Serialize(status)
            + ",\"queueOrder\":1"
            + ",\"outputPath\":\"X:\\\\synthetic\\\\output.webp\""
            + ",\"createdAt\":\"2026-08-30T00:00:00.000Z\""
            + ",\"updatedAt\":\"2026-08-30T00:00:01.000Z\"}";
        return JsonDocument.Parse(json);
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

    private static async Task<bool>
        WaitForEnhancementJobsOfflineSmokeConditionAsync(
            Func<bool> condition,
            int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64
            + Math.Max(1, timeoutMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private static void WriteSavedDeliveryEmptyCatalogRevision(
        string jobsPath,
        long catalogRevision)
    {
        if (catalogRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(catalogRevision));

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = jobsPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE enhancement_store_metadata
                SET catalog_revision = $catalogRevision
                WHERE singleton = 1;
                """;
            command.Parameters.AddWithValue("$catalogRevision", catalogRevision);
            command.ExecuteNonQuery();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }

    private static void WriteSavedDeliveryAdoptionFixtureState(
        string jobsPath,
        string status,
        long catalogRevision,
        string jobId = "saved-delivery-recovered",
        string sourcePath = @"X:\synthetic\saved-delivery.png",
        long position = 0)
    {
        if (status is not ("queued" or "running" or "canceled"))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (catalogRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(catalogRevision));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        if (position < 0)
            throw new ArgumentOutOfRangeException(nameof(position));

        string timestamp = $"2026-08-30T00:00:{catalogRevision:00}.000Z";
        string projection = JsonSerializer.Serialize(new
        {
            id = jobId,
            sourceId = sourcePath,
            sourcePath,
            presetId = "photo-detail-x4",
            adapterId = "realesrgan-ncnn",
            operation = "upscale",
            status,
            progress = 0,
            queueOrder = status == "queued" ? 0 : (int?)null,
            createdAt = "2026-08-30T00:00:00.000Z",
            updatedAt = timestamp,
        });

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
            command.CommandText = """
                INSERT INTO enhancement_jobs
                    (position, id, status, updated_at,
                     reader_payload_json, payload_json)
                VALUES ($position, $id, $status, $updatedAt,
                        $readerPayload, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    updated_at = excluded.updated_at,
                    reader_payload_json = excluded.reader_payload_json,
                    payload_json = excluded.payload_json;
                UPDATE enhancement_store_metadata
                SET catalog_revision = $catalogRevision
                WHERE singleton = 1;
                """;
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            command.Parameters.AddWithValue("$readerPayload", projection);
            command.Parameters.AddWithValue("$payload", projection);
            command.Parameters.AddWithValue("$catalogRevision", catalogRevision);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }
}

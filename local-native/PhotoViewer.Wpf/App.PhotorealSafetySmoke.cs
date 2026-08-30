using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CapturePhotorealSafetySmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
            "aibos-wpf-photoreal-safety-").FullName;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            MainWindow? jobsWindow = null;
            MainWindow? batchWindow = null;
            MainWindow? legacyMixedWindow = null;
            var previousEnvironment = new Dictionary<string, string?>(
                StringComparer.Ordinal);
            bool ok = false;
            string failure = "";
            bool initialSelectorsClosed = false;
            bool failureMatrixBlocked = false;
            bool validHealthPublished = false;
            bool validHealthEnabledSelectors = false;
            bool exactAllowlist = false;
            bool legacyRetryPreserved = false;
            bool unknownRowsReaderOnly = false;
            bool modalClassificationExact = false;
            bool unknownMutationRequestsZero = false;
            bool authoritativeBatchHealthBlocked = false;
            bool legacyTerminalReaderOnlyFailClosed = false;
            var failureEvidence = new List<object>();
            int enqueueMutationRequests = 0;
            int readerOnlyMutationRequests = 0;
            int readerOnlyTargetPlanRequests = 0;
            int authoritativeMutationRequests = 0;
            int legacyMixedMutationRequests = 0;
            try
            {
                string storeRoot = Path.Combine(smokeRoot, "stores");
                string jobsPath = Path.Combine(storeRoot, "enhance", "jobs.json");
                string jobsReaderOnlyPath = Path.Combine(
                    storeRoot,
                    "reader-only",
                    "jobs.json");
                string jobsBatchPath = Path.Combine(
                    storeRoot,
                    "batch-health",
                    "jobs.json");
                string jobsLegacyMixedPath = Path.Combine(
                    storeRoot,
                    "legacy-mixed",
                    "jobs.json");
                string outputRoot = Path.Combine(smokeRoot, "outputs");
                Directory.CreateDirectory(storeRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(
                    jobsReaderOnlyPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsBatchPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(
                    jobsLegacyMixedPath)!);
                Directory.CreateDirectory(outputRoot);
                var environment = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(storeRoot, "state.json"),
                    ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storeRoot, "favorites.json"),
                    ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storeRoot, "seen.json"),
                    ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storeRoot, "recent.json"),
                    ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storeRoot, "settings.json"),
                    ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storeRoot, "albums.json"),
                    ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storeRoot, "search.json"),
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = jobsPath,
                    ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputRoot,
                    ["PVU_ENHANCE_OUTPUT_ROOT"] = outputRoot,
                    ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storeRoot, "metadata-index"),
                };
                foreach ((string name, string value) in environment)
                {
                    previousEnvironment[name] =
                        Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, value);
                }

                string healthMode = "missing";
                window = HiddenWindow();
                window.SuppressStatePersistence();
                window.ConfigureModalEnhancementForSmoke((request, _) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method == HttpMethod.Get
                        && route.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        return HealthResponseForPhotorealSafetySmoke(healthMode);
                    }

                    if (request.Method != HttpMethod.Get)
                    {
                        Interlocked.Increment(ref enqueueMutationRequests);
                        string requestId = request.Headers.TryGetValues(
                                "Idempotency-Key",
                                out IEnumerable<string>? requestIds)
                            ? requestIds.Single()
                            : "missing-request-id";
                        return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                            HttpStatusCode.Accepted,
                            new
                            {
                                job = new
                                {
                                    id = "accepted-krea-job",
                                    operation = "photoreal",
                                    status = "queued",
                                },
                                receipt = new
                                {
                                    idempotencyKey = requestId,
                                    jobId = "accepted-krea-job",
                                },
                            }));
                    }

                    return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                        HttpStatusCode.NotFound,
                        new { error = "unexpected safety-smoke route" }));
                });
                window.Show();
                window.SelectPhotorealEngineForSmoke(
                    "comfyui-krea2-anything2real-v3-photoreal");
                initialSelectorsClosed =
                    window.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: false, ModalEnabled: false }
                    && window.KreaAnythingToReal1536SelectorForSmoke is
                    {
                        AppVisible: false,
                        ModalVisible: false,
                        AppEnabled: false,
                        ModalEnabled: false,
                        AppChecked: false,
                        ModalChecked: false,
                    };

                object requestBody = new
                {
                    operation = "photoreal",
                    sourceId = Path.Combine(smokeRoot, "synthetic-source.png"),
                    presetId = "photoreal-balanced",
                    adapterId =
                        "comfyui-krea2-anime-to-real-edit-v1-photoreal",
                };
                object author1536RequestBody = new
                {
                    operation = "photoreal",
                    sourceId = Path.Combine(
                        smokeRoot,
                        "synthetic-author-source.png"),
                    presetId = "photoreal-balanced",
                    adapterId =
                        "comfyui-krea2-anything2real-v3-photoreal",
                    maxDimension = 1536,
                };
                string pendingDirectory =
                    EnhancementEnqueueInboxStore.GetPendingDirectory(jobsPath);
                string[] failureModes =
                [
                    "timeout",
                    "unavailable",
                    "missing",
                    "false",
                    "malformed",
                    "duplicate-capability",
                    "duplicate-capabilities",
                ];
                bool allFailuresBlocked = true;
                foreach (string mode in failureModes)
                {
                    healthMode = mode;
                    int before = CountPendingPhotorealSafetySmoke(
                        pendingDirectory);
                    int mutationsBefore = enqueueMutationRequests;
                    bool createSaved =
                        await window.SendEnhancementEnqueueForSmokeAsync(
                            requestBody);
                    bool authorCreateSaved =
                        await window.SendEnhancementEnqueueForSmokeAsync(
                            author1536RequestBody);
                    var retry =
                        await window.SendKreaAnimeToRealRetryForSmokeAsync(
                            $"{mode}-single-retry");
                    var batch =
                        await window.SendKreaAnimeToRealRetryBatchForSmokeAsync(
                            $"{mode}-batch-retry");
                    var authorRetry =
                        await window
                            .SendKreaAnythingToReal1536RetryForSmokeAsync(
                                $"{mode}-author-single-retry");
                    var authorBatch =
                        await window
                            .SendKreaAnythingToReal1536RetryBatchForSmokeAsync(
                                $"{mode}-author-batch-retry");
                    int after = CountPendingPhotorealSafetySmoke(
                        pendingDirectory);
                    bool selectorsClosed =
                        window.KreaAnimeToRealV1SelectorEnabledForSmoke
                            is { AppEnabled: false, ModalEnabled: false }
                        && window.KreaAnythingToReal1536SelectorForSmoke is
                        {
                            AppVisible: false,
                            ModalVisible: false,
                            AppEnabled: false,
                            ModalEnabled: false,
                            AppChecked: false,
                            ModalChecked: false,
                        };
                    bool blocked = !createSaved
                        && !authorCreateSaved
                        && !retry.Ok
                        && !retry.SavedForDelivery
                        && batch.PublishedCount == 0
                        && !batch.SavedForDelivery
                        && !authorRetry.Ok
                        && !authorRetry.SavedForDelivery
                        && authorBatch.PublishedCount == 0
                        && !authorBatch.SavedForDelivery
                        && before == after
                        && mutationsBefore == enqueueMutationRequests
                        && selectorsClosed;
                    allFailuresBlocked &= blocked;
                    failureEvidence.Add(new
                    {
                        mode,
                        blocked,
                        pendingBefore = before,
                        pendingAfter = after,
                        mutationRequests =
                            enqueueMutationRequests - mutationsBefore,
                        retry.StatusCode,
                        authorRetryStatusCode = authorRetry.StatusCode,
                        selectorsClosed,
                    });
                }
                failureMatrixBlocked = allFailuresBlocked
                    && CountPendingPhotorealSafetySmoke(pendingDirectory) == 0
                    && enqueueMutationRequests == 0;

                healthMode = "valid";
                int validPendingBefore = CountPendingPhotorealSafetySmoke(
                    pendingDirectory);
                int validMutationsBefore = enqueueMutationRequests;
                using JsonDocument ambiguousDimensionRequest = JsonDocument.Parse(
                    "{\"operation\":\"photoreal\",\"sourceId\":\"synthetic\",\"presetId\":\"photoreal-balanced\",\"adapterId\":\"comfyui-krea2-anything2real-v3-photoreal\",\"maxDimension\":1280,\"maxDimension\":1536}");
                bool ambiguousDimensionBlocked =
                    !await window.SendEnhancementEnqueueForSmokeAsync(
                        ambiguousDimensionRequest.RootElement)
                    && CountPendingPhotorealSafetySmoke(pendingDirectory)
                        == validPendingBefore
                    && enqueueMutationRequests == validMutationsBefore;
                object[] forbiddenDimensionRequests =
                [
                    new
                    {
                        operation = "photoreal",
                        sourceId = "synthetic-flux-1536",
                        presetId = "photoreal-balanced",
                        adapterId = "comfyui-flux2-photoreal",
                        maxDimension = 1536,
                    },
                    new
                    {
                        operation = "photoreal",
                        sourceId = "synthetic-anime-1536",
                        presetId = "photoreal-balanced",
                        adapterId =
                            "comfyui-krea2-anime-to-real-edit-v1-photoreal",
                        maxDimension = 1536,
                    },
                    new
                    {
                        operation = "photoreal",
                        sourceId = "synthetic-unknown-1536",
                        presetId = "photoreal-balanced",
                        adapterId = "comfyui-future-photoreal-v9",
                        maxDimension = 1536,
                    },
                    new
                    {
                        operation = "photoreal",
                        sourceId = "synthetic-v3-2048",
                        presetId = "photoreal-balanced",
                        adapterId =
                            "comfyui-krea2-anything2real-v3-photoreal",
                        maxDimension = 2048,
                    },
                    new
                    {
                        operation = "photoreal",
                        sourceId = "synthetic-missing-adapter-1536",
                        presetId = "photoreal-balanced",
                        maxDimension = 1536,
                    },
                ];
                bool forbiddenDimensionsBlocked = true;
                foreach (object forbidden in forbiddenDimensionRequests)
                {
                    forbiddenDimensionsBlocked &=
                        !await window.SendEnhancementEnqueueForSmokeAsync(
                            forbidden)
                        && CountPendingPhotorealSafetySmoke(pendingDirectory)
                            == validPendingBefore
                        && enqueueMutationRequests == validMutationsBefore;
                }
                bool validAnimeCreate =
                    await window.SendEnhancementEnqueueForSmokeAsync(requestBody);
                bool validAuthorCreate =
                    await window.SendEnhancementEnqueueForSmokeAsync(
                        author1536RequestBody);
                var validAuthorRetry =
                    await window.SendKreaAnythingToReal1536RetryForSmokeAsync(
                        "valid-author-single-retry");
                var validAuthorBatch =
                    await window
                        .SendKreaAnythingToReal1536RetryBatchForSmokeAsync(
                            "valid-author-batch-retry");
                int validPendingAfter = CountPendingPhotorealSafetySmoke(
                    pendingDirectory);
                validHealthPublished = ambiguousDimensionBlocked
                    && forbiddenDimensionsBlocked
                    && !validAnimeCreate
                    && !validAuthorCreate
                    && validAuthorRetry.Ok
                    && !validAuthorRetry.SavedForDelivery
                    && validAuthorBatch.PublishedCount == 1
                    && !validAuthorBatch.SavedForDelivery
                    && validPendingAfter == validPendingBefore + 4
                    && enqueueMutationRequests == 4;
                validHealthEnabledSelectors =
                    window.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: true, ModalEnabled: true }
                    && window.KreaAnythingToReal1536SelectorForSmoke is
                    {
                        AppVisible: true,
                        ModalVisible: true,
                        AppEnabled: true,
                        ModalEnabled: true,
                        AppChecked: false,
                        ModalChecked: false,
                    };

                string[] knownAdapters =
                [
                    "comfyui-flux2-photoreal",
                    "comfyui-krea2-anything2real-v3-photoreal",
                    "comfyui-krea2-anime-to-real-edit-v1-photoreal",
                    "a1111-photoreal",
                ];
                exactAllowlist = knownAdapters.All(
                        PhotoViewer.Wpf.MainWindow
                            .IsKnownPhotorealAdapterIdForSmoke)
                    && !PhotoViewer.Wpf.MainWindow
                        .IsKnownPhotorealAdapterIdForSmoke(
                        "comfyui-future-photoreal-v9")
                    && !PhotoViewer.Wpf.MainWindow
                        .IsKnownPhotorealAdapterIdForSmoke(
                        "A1111-PHOTOREAL");
                using JsonDocument legacyJob = JsonDocument.Parse(
                    JsonSerializer.Serialize(PhotorealSafetyJob(
                        "legacy-failed",
                        "failed",
                        "a1111-photoreal")));
                PhotorealMutationSurfaceSmokeSnapshot? legacy =
                    PhotoViewer.Wpf.MainWindow
                        .ReadPhotorealMutationSurfaceForSmoke(
                        legacyJob.RootElement);
                legacyRetryPreserved = legacy is
                {
                    ReaderOnly: false,
                    SupportedMutation: true,
                    CanRetry: true,
                };

                string[] statuses =
                    ["queued", "running", "succeeded", "failed", "canceled", "deleted"];
                var unknownJobs = new List<object>();
                bool everyUnknownRowProtected = true;
                bool everyModalRowProtected = true;
                foreach (string status in statuses)
                {
                    unknownJobs.Add(PhotorealSafetyJob(
                        $"unknown-{status}",
                        status,
                        "comfyui-future-photoreal-v9"));
                }
                unknownJobs.AddRange(
                [
                    PhotorealSafetyJob(
                        "invalid-flux-1536",
                        "failed",
                        "comfyui-flux2-photoreal",
                        1536),
                    PhotorealSafetyJob(
                        "invalid-anime-1536",
                        "failed",
                        "comfyui-krea2-anime-to-real-edit-v1-photoreal",
                        1536),
                    PhotorealSafetyJob(
                        "invalid-unknown-1536",
                        "failed",
                        "comfyui-future-photoreal-v9",
                        1536),
                    PhotorealSafetyJob(
                        "invalid-v3-2048",
                        "failed",
                        "comfyui-krea2-anything2real-v3-photoreal",
                        2048),
                ]);
                foreach (object job in unknownJobs)
                {
                    using JsonDocument document = JsonDocument.Parse(
                        JsonSerializer.Serialize(job));
                    PhotorealMutationSurfaceSmokeSnapshot? surface =
                        PhotoViewer.Wpf.MainWindow
                            .ReadPhotorealMutationSurfaceForSmoke(
                            document.RootElement);
                    everyUnknownRowProtected &= surface is not null
                        && surface.ReaderOnly
                        && !surface.SupportedMutation
                        && !surface.CanCancel
                        && !surface.CanRetry
                        && !surface.CanDismiss
                        && !surface.CanReorder
                        && !surface.CanRerunWithCurrentSettings
                        && !surface.CanRerunNextWithCurrentSettings
                        && !surface.CanUpdateCurrentSettings
                        && !surface.CanUseOutput
                        && !surface.CanDeleteOutput
                        && surface.VisibleActionKinds.Length == 0;
                    everyModalRowProtected &=
                        !PhotoViewer.Wpf.MainWindow
                            .IsModalPhotorealJobMutationSafeForSmoke(
                            document.RootElement);
                }
                unknownRowsReaderOnly = everyUnknownRowProtected;
                modalClassificationExact = everyModalRowProtected
                    && knownAdapters.All(adapterId =>
                    {
                        using JsonDocument knownDocument = JsonDocument.Parse(
                            JsonSerializer.Serialize(PhotorealSafetyJob(
                                $"known-{adapterId}",
                                "failed",
                                adapterId)));
                        return PhotoViewer.Wpf.MainWindow
                            .IsModalPhotorealJobMutationSafeForSmoke(
                                knownDocument.RootElement);
                    });

                Environment.SetEnvironmentVariable(
                    "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                    jobsReaderOnlyPath);
                object jobsHealth = PhotorealSafetyJobsHealth(unknownJobs);
                jobsWindow = HiddenWindow();
                jobsWindow.SuppressStatePersistence();
                jobsWindow.ConfigureEnhancementJobsBulkConfirmationForSmoke(
                    static (_, _) => true);
                jobsWindow.ConfigureModalEnhancementForSmoke((request, _) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method == HttpMethod.Post
                        && route.EndsWith(
                            "/api/enhance/jobs/terminal/targets",
                            StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref readerOnlyTargetPlanRequests);
                        string body = request.Content?.ReadAsStringAsync()
                            .GetAwaiter().GetResult() ?? "";
                        using JsonDocument targetDocument = JsonDocument.Parse(body);
                        string requestedStatus = targetDocument.RootElement
                            .GetProperty("status").GetString() ?? "";
                        int protectedCount = unknownJobs.Count(job =>
                        {
                            using JsonDocument jobDocument = JsonDocument.Parse(
                                JsonSerializer.Serialize(job));
                            return jobDocument.RootElement.GetProperty("status")
                                .GetString() == requestedStatus;
                        });
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                new
                                {
                                    targetCount = 0,
                                    protectedCount,
                                    ids = Array.Empty<string>(),
                                }));
                    }
                    if (request.Method != HttpMethod.Get)
                    {
                        Interlocked.Increment(ref readerOnlyMutationRequests);
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.InternalServerError,
                                new { error = "reader-only mutation escaped" }));
                    }
                    if (route.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                jobsHealth));
                    }
                    if (route.EndsWith(
                            "/api/enhance/jobs",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                new { jobs = unknownJobs }));
                    }
                    return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                        HttpStatusCode.NotFound,
                        new { error = "unexpected reader-only route" }));
                });
                jobsWindow.Show();
                await jobsWindow.OpenEnhancementJobsForSmokeAsync();
                bool mutationsRejected =
                    !await jobsWindow.CancelEnhancementJobForSmokeAsync(
                        "unknown-queued")
                    && !await jobsWindow.CancelEnhancementJobForSmokeAsync(
                        "unknown-running")
                    && !await jobsWindow.MoveEnhancementJobForSmokeAsync(
                        "unknown-queued",
                        "next")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "unknown-failed")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "unknown-canceled")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "invalid-flux-1536")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "invalid-anime-1536")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "invalid-unknown-1536")
                    && !await jobsWindow.RetryEnhancementJobForSmokeAsync(
                        "invalid-v3-2048")
                    && !await jobsWindow.DismissEnhancementJobForSmokeAsync(
                        "unknown-failed")
                    && !await jobsWindow.RerunPhotorealJobForSmokeAsync(
                        "unknown-succeeded")
                    && !await jobsWindow.DeleteEnhancementJobOutputForSmokeAsync(
                        "unknown-succeeded")
                    && !await jobsWindow.UpdateQueuedPhotorealPromptsForSmokeAsync(
                        "unknown-queued")
                    && await jobsWindow.UpdateAllQueuedPhotorealPromptsForSmokeAsync()
                        == 0
                    && !await jobsWindow.CancelAllQueuedEnhancementJobsForSmokeAsync()
                    && await jobsWindow.RetryAllFailedEnhancementJobsForSmokeAsync()
                        == 0
                    && await jobsWindow.RetryAllCanceledEnhancementJobsForSmokeAsync()
                        == 0
                    && await jobsWindow.ClearAllFailedEnhancementJobsForSmokeAsync()
                        == 0
                    && await jobsWindow.ClearAllCanceledEnhancementJobsForSmokeAsync()
                        == 0;
                unknownMutationRequestsZero = mutationsRejected
                    && readerOnlyMutationRequests == 0
                    && readerOnlyTargetPlanRequests == 4
                    && !jobsWindow.RetryAllFailedEnhancementJobsControlForSmoke
                    && jobsWindow.ClearAllFailedEnhancementJobsControlForSmoke
                    && !jobsWindow.RetryAllCanceledEnhancementJobsControlForSmoke
                    && jobsWindow.ClearAllCanceledEnhancementJobsControlForSmoke;

                var knownKreaJobs = new List<object>
                {
                    PhotorealSafetyJob(
                        "known-krea-failed",
                        "failed",
                        "comfyui-krea2-anime-to-real-edit-v1-photoreal"),
                    PhotorealSafetyJob(
                        "known-author-1536-failed",
                        "failed",
                        "comfyui-krea2-anything2real-v3-photoreal",
                        photorealMaxDimension: 1536),
                };
                object batchHealth = PhotorealSafetyJobsHealth(knownKreaJobs);
                bool batchHealthAvailable = true;
                Environment.SetEnvironmentVariable(
                    "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                    jobsBatchPath);
                batchWindow = HiddenWindow();
                batchWindow.SuppressStatePersistence();
                batchWindow.ConfigureEnhancementJobsBulkConfirmationForSmoke(
                    static (_, _) => true);
                batchWindow.ConfigureModalEnhancementForSmoke((request, _) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method != HttpMethod.Get)
                    {
                        Interlocked.Increment(ref authoritativeMutationRequests);
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.InternalServerError,
                                new { error = "authoritative mutation escaped" }));
                    }
                    if (route.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(batchHealthAvailable
                            ? JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                batchHealth)
                            : JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.ServiceUnavailable,
                                new { error = "synthetic health unavailable" }));
                    }
                    if (route.EndsWith(
                            "/api/enhance/jobs",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                new { jobs = knownKreaJobs }));
                    }
                    return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                        HttpStatusCode.NotFound,
                        new { error = "unexpected authoritative route" }));
                });
                batchWindow.Show();
                await batchWindow.OpenEnhancementJobsForSmokeAsync();
                bool batchControlInitiallyEnabled =
                    batchWindow.RetryAllFailedEnhancementJobsControlForSmoke;
                batchHealthAvailable = false;
                string batchPendingDirectory =
                    EnhancementEnqueueInboxStore.GetPendingDirectory(
                        jobsBatchPath);
                int batchPendingBefore = CountPendingPhotorealSafetySmoke(
                    batchPendingDirectory);
                bool savedAuthorSingleRetryBlocked =
                    !await batchWindow.RetryEnhancementJobForSmokeAsync(
                        "known-author-1536-failed");
                int batchRetried =
                    await batchWindow.RetryAllFailedEnhancementJobsForSmokeAsync();
                int batchPendingAfter = CountPendingPhotorealSafetySmoke(
                    batchPendingDirectory);
                authoritativeBatchHealthBlocked = batchControlInitiallyEnabled
                    && savedAuthorSingleRetryBlocked
                    && batchRetried == 0
                    && batchPendingBefore == batchPendingAfter
                    && authoritativeMutationRequests == 0
                    && batchWindow.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: false, ModalEnabled: false }
                    && batchWindow.KreaAnythingToReal1536SelectorForSmoke is
                    {
                        AppVisible: false,
                        ModalVisible: false,
                        AppEnabled: false,
                        ModalEnabled: false,
                    };

                var legacyMixedJobs = new List<object>
                {
                    PhotorealSafetyJob(
                        "legacy-safe-photoreal-failed",
                        "failed",
                        "comfyui-flux2-photoreal"),
                    PhotorealSafetyJob(
                        "legacy-future-photoreal-failed",
                        "failed",
                        "comfyui-future-photoreal-v9"),
                };
                object legacyMixedHealth = PhotorealSafetyJobsHealth(
                    legacyMixedJobs,
                    terminalHistoryCapabilities: false);
                Environment.SetEnvironmentVariable(
                    "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH",
                    jobsLegacyMixedPath);
                legacyMixedWindow = HiddenWindow();
                legacyMixedWindow.SuppressStatePersistence();
                legacyMixedWindow.ConfigureEnhancementJobsBulkConfirmationForSmoke(
                    static (_, _) => true);
                legacyMixedWindow.ConfigureModalEnhancementForSmoke((request, _) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method != HttpMethod.Get)
                    {
                        Interlocked.Increment(ref legacyMixedMutationRequests);
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.InternalServerError,
                                new { error = "legacy mixed mutation escaped" }));
                    }
                    if (route.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                legacyMixedHealth));
                    }
                    if (route.EndsWith(
                            "/api/enhance/jobs",
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            JsonResponseForPhotorealSafetySmoke(
                                HttpStatusCode.OK,
                                new { jobs = legacyMixedJobs }));
                    }
                    return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                        HttpStatusCode.NotFound,
                        new { error = "unexpected legacy mixed route" }));
                });
                legacyMixedWindow.Show();
                await legacyMixedWindow.OpenEnhancementJobsForSmokeAsync();
                legacyTerminalReaderOnlyFailClosed =
                    legacyMixedWindow.RetryAllFailedEnhancementJobsControlForSmoke
                    && !legacyMixedWindow.ClearAllFailedEnhancementJobsControlForSmoke
                    && await legacyMixedWindow
                        .RetryAllFailedEnhancementJobsForSmokeAsync() == 0
                    && await legacyMixedWindow
                        .ClearAllFailedEnhancementJobsForSmokeAsync() == 0
                    && legacyMixedMutationRequests == 0;

                ok = initialSelectorsClosed
                    && failureMatrixBlocked
                    && validHealthPublished
                    && validHealthEnabledSelectors
                    && exactAllowlist
                    && legacyRetryPreserved
                    && unknownRowsReaderOnly
                    && modalClassificationExact
                    && unknownMutationRequestsZero
                    && authoritativeBatchHealthBlocked
                    && legacyTerminalReaderOnlyFailClosed;
                if (!ok)
                {
                    failure = "Photoreal safety invariants did not all hold.";
                }
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
            finally
            {
                if (legacyMixedWindow is not null)
                {
                    try { legacyMixedWindow.Close(); } catch { }
                }
                if (batchWindow is not null)
                {
                    try { batchWindow.Close(); } catch { }
                }
                if (jobsWindow is not null)
                {
                    try { jobsWindow.Close(); } catch { }
                }
                if (window is not null)
                {
                    try { window.Close(); } catch { }
                }
                foreach ((string name, string? value) in previousEnvironment)
                    Environment.SetEnvironmentVariable(name, value);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(new
                {
                    ok,
                    message = ok ? "Photoreal health and reader-only safety passed." : failure,
                    initialSelectorsClosed,
                    failureMatrixBlocked,
                    validHealthPublished,
                    validHealthEnabledSelectors,
                    exactAllowlist,
                    legacyRetryPreserved,
                    unknownRowsReaderOnly,
                    modalClassificationExact,
                    unknownMutationRequestsZero,
                    authoritativeBatchHealthBlocked,
                    legacyTerminalReaderOnlyFailClosed,
                    enqueueMutationRequests,
                    readerOnlyMutationRequests,
                    readerOnlyTargetPlanRequests,
                    authoritativeMutationRequests,
                    legacyMixedMutationRequests,
                    failureEvidence,
                }, new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }

    private static object PhotorealSafetyJob(
        string id,
        string status,
        string adapterId,
        int? photorealMaxDimension = null)
    {
        var job = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["operation"] = "photoreal",
            ["sourceId"] = Path.Combine(
                Path.GetTempPath(),
                "aibos-synthetic-source.png"),
            ["sourcePath"] = Path.Combine(
                Path.GetTempPath(),
                "aibos-synthetic-source.png"),
            ["presetId"] = adapterId == "a1111-photoreal"
                ? "anime-sharp-x2"
                : "photoreal-balanced",
            ["adapterId"] = adapterId,
            ["status"] = status,
            ["progress"] = status == "succeeded" ? 100 : 0,
            ["outputPath"] = status == "succeeded"
                ? Path.Combine(Path.GetTempPath(), $"{id}.png")
                : null,
            ["errorMessage"] = status == "failed" ? "synthetic" : null,
            ["cancelRequested"] = false,
            ["queueOrder"] = status == "queued" ? 1 : (int?)null,
            ["createdAt"] = "2026-08-30T00:00:00.000Z",
            ["updatedAt"] = "2026-08-30T00:00:01.000Z",
            ["sourceSignature"] = new { size = 1, mtimeMs = 1d },
        };
        if (photorealMaxDimension is int exactMaxDimension)
        {
            job["preset"] = new
            {
                options = new { maxDimension = exactMaxDimension },
            };
        }
        return job;
    }

    private static object PhotorealSafetyJobsHealth(
        IReadOnlyList<object> jobs,
        bool terminalHistoryCapabilities = true)
    {
        int Count(string status) => jobs.Count(job =>
        {
            using JsonDocument document = JsonDocument.Parse(
                JsonSerializer.Serialize(job));
            return document.RootElement.GetProperty("status").GetString()
                == status;
        });
        var capabilities = new Dictionary<string, object?>
        {
            ["queuedPhotorealSettingsUpdateV1"] = true,
            ["photorealPromptControlsV2"] = true,
            ["kreaAnimeToRealV1"] = true,
            ["kreaAnythingToReal1536V1"] = true,
            ["atomicImageEnqueueNext"] = true,
            ["queuedJobsBatchCancelV1"] = true,
            ["queuedJobsBatchReorderV1"] = true,
            ["durableEnqueueInboxV1"] = new
            {
                ready = true,
                protocolVersion = 1,
                backendGeneration = "json-v1",
            },
        };
        if (terminalHistoryCapabilities)
        {
            capabilities["terminalHistoryBatchDismissV1"] = true;
            capabilities["terminalHistoryTargetsV1"] = true;
            capabilities["terminalHistoryBatchRetryV1"] = true;
        }

        return new
        {
            version = 1,
            status = "working",
            issues = Array.Empty<string>(),
            runtime = new
            {
                sourceRevision = "photoreal-safety-smoke",
                sourceDirty = false,
                buildId = "photoreal-safety-smoke",
                serverStartedAtUtc = "2026-08-30T00:00:00.000Z",
                processId = 4242,
            },
            jobs = new
            {
                counts = new
                {
                    queued = Count("queued"),
                    running = Count("running"),
                    succeeded = Count("succeeded"),
                    failed = Count("failed"),
                    canceled = Count("canceled"),
                    deleted = Count("deleted"),
                },
                current = (object?)null,
                lastClaimAt = (string?)null,
                lastProgressAt = (string?)null,
                lastTerminalAt = "2026-08-30T00:00:01.000Z",
            },
            worker = new { paused = false },
            capabilities,
        };
    }

    private static Task<HttpResponseMessage>
        HealthResponseForPhotorealSafetySmoke(string mode)
    {
        if (mode == "timeout")
        {
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("synthetic health timeout"));
        }
        if (mode == "unavailable")
        {
            return Task.FromResult(JsonResponseForPhotorealSafetySmoke(
                HttpStatusCode.ServiceUnavailable,
                new { error = "synthetic unavailable" }));
        }

        string kreaMembers = mode switch
        {
            "valid" =>
                ",\"kreaAnimeToRealV1\":true,\"kreaAnythingToReal1536V1\":true",
            "false" =>
                ",\"kreaAnimeToRealV1\":false,\"kreaAnythingToReal1536V1\":false",
            "malformed" =>
                ",\"kreaAnimeToRealV1\":\"true\",\"kreaAnythingToReal1536V1\":\"true\"",
            "duplicate-capability" =>
                ",\"kreaAnimeToRealV1\":false,\"kreaAnimeToRealV1\":true,\"kreaAnythingToReal1536V1\":false,\"kreaAnythingToReal1536V1\":true",
            _ => "",
        };
        string capabilities =
            "{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":1,\"backendGeneration\":\"json-v1\"},\"photorealPromptControlsV2\":true"
            + kreaMembers
            + "}";
        string json = mode == "duplicate-capabilities"
            ? "{\"capabilities\":" + capabilities
                + ",\"capabilities\":" + capabilities + "}"
            : "{\"capabilities\":" + capabilities + "}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    private static HttpResponseMessage JsonResponseForPhotorealSafetySmoke(
        HttpStatusCode status,
        object payload)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

    private static int CountPendingPhotorealSafetySmoke(string pendingDirectory)
        => Directory.Exists(pendingDirectory)
            ? Directory.EnumerateFiles(pendingDirectory, "*.json").Count()
            : 0;
}

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
            var failureEvidence = new List<object>();
            int enqueueMutationRequests = 0;
            int readerOnlyMutationRequests = 0;
            int authoritativeMutationRequests = 0;
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
                string outputRoot = Path.Combine(smokeRoot, "outputs");
                Directory.CreateDirectory(storeRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(
                    jobsReaderOnlyPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsBatchPath)!);
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
                initialSelectorsClosed =
                    window.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: false, ModalEnabled: false };

                object requestBody = new
                {
                    operation = "photoreal",
                    sourceId = Path.Combine(smokeRoot, "synthetic-source.png"),
                    presetId = "photoreal-balanced",
                    adapterId =
                        "comfyui-krea2-anime-to-real-edit-v1-photoreal",
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
                    var retry =
                        await window.SendKreaAnimeToRealRetryForSmokeAsync(
                            $"{mode}-single-retry");
                    var batch =
                        await window.SendKreaAnimeToRealRetryBatchForSmokeAsync(
                            $"{mode}-batch-retry");
                    int after = CountPendingPhotorealSafetySmoke(
                        pendingDirectory);
                    bool selectorsClosed =
                        window.KreaAnimeToRealV1SelectorEnabledForSmoke
                            is { AppEnabled: false, ModalEnabled: false };
                    bool blocked = !createSaved
                        && !retry.Ok
                        && !retry.SavedForDelivery
                        && batch.PublishedCount == 0
                        && !batch.SavedForDelivery
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
                        selectorsClosed,
                    });
                }
                failureMatrixBlocked = allFailuresBlocked
                    && CountPendingPhotorealSafetySmoke(pendingDirectory) == 0
                    && enqueueMutationRequests == 0;

                healthMode = "valid";
                int validPendingBefore = CountPendingPhotorealSafetySmoke(
                    pendingDirectory);
                _ = await window.SendEnhancementEnqueueForSmokeAsync(requestBody);
                int validPendingAfter = CountPendingPhotorealSafetySmoke(
                    pendingDirectory);
                validHealthPublished = validPendingAfter
                    == validPendingBefore + 1;
                validHealthEnabledSelectors =
                    window.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: true, ModalEnabled: true };

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
                    object job = PhotorealSafetyJob(
                        $"unknown-{status}",
                        status,
                        "comfyui-future-photoreal-v9");
                    unknownJobs.Add(job);
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
                    && !jobsWindow.RetryAllFailedEnhancementJobsControlForSmoke
                    && !jobsWindow.ClearAllFailedEnhancementJobsControlForSmoke
                    && !jobsWindow.RetryAllCanceledEnhancementJobsControlForSmoke
                    && !jobsWindow.ClearAllCanceledEnhancementJobsControlForSmoke;

                var knownKreaJobs = new List<object>
                {
                    PhotorealSafetyJob(
                        "known-krea-failed",
                        "failed",
                        "comfyui-krea2-anime-to-real-edit-v1-photoreal"),
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
                int batchRetried =
                    await batchWindow.RetryAllFailedEnhancementJobsForSmokeAsync();
                authoritativeBatchHealthBlocked = batchControlInitiallyEnabled
                    && batchRetried == 0
                    && authoritativeMutationRequests == 0
                    && batchWindow.KreaAnimeToRealV1SelectorEnabledForSmoke
                        is { AppEnabled: false, ModalEnabled: false };

                ok = initialSelectorsClosed
                    && failureMatrixBlocked
                    && validHealthPublished
                    && validHealthEnabledSelectors
                    && exactAllowlist
                    && legacyRetryPreserved
                    && unknownRowsReaderOnly
                    && modalClassificationExact
                    && unknownMutationRequestsZero
                    && authoritativeBatchHealthBlocked;
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
                    enqueueMutationRequests,
                    readerOnlyMutationRequests,
                    authoritativeMutationRequests,
                    failureEvidence,
                }, new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(smokeRoot, recursive: true); } catch { }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }

    private static object PhotorealSafetyJob(
        string id,
        string status,
        string adapterId)
        => new
        {
            id,
            operation = "photoreal",
            sourceId = Path.Combine(Path.GetTempPath(), "aibos-synthetic-source.png"),
            sourcePath = Path.Combine(Path.GetTempPath(), "aibos-synthetic-source.png"),
            presetId = adapterId == "a1111-photoreal"
                ? "anime-sharp-x2"
                : "photoreal-balanced",
            adapterId,
            status,
            progress = status == "succeeded" ? 100 : 0,
            outputPath = status == "succeeded"
                ? Path.Combine(Path.GetTempPath(), $"{id}.png")
                : null,
            errorMessage = status == "failed" ? "synthetic" : null,
            cancelRequested = false,
            queueOrder = status == "queued" ? 1 : (int?)null,
            createdAt = "2026-08-30T00:00:00.000Z",
            updatedAt = "2026-08-30T00:00:01.000Z",
            sourceSignature = new { size = 1, mtimeMs = 1d },
        };

    private static object PhotorealSafetyJobsHealth(IReadOnlyList<object> jobs)
    {
        int Count(string status) => jobs.Count(job =>
        {
            using JsonDocument document = JsonDocument.Parse(
                JsonSerializer.Serialize(job));
            return document.RootElement.GetProperty("status").GetString()
                == status;
        });
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
            capabilities = new
            {
                queuedPhotorealSettingsUpdateV1 = true,
                photorealPromptControlsV2 = true,
                kreaAnimeToRealV1 = true,
                atomicImageEnqueueNext = true,
                terminalHistoryBatchDismissV1 = true,
                queuedJobsBatchCancelV1 = true,
                queuedJobsBatchReorderV1 = true,
                terminalHistoryTargetsV1 = true,
                terminalHistoryBatchRetryV1 = true,
                durableEnqueueInboxV1 = new
                {
                    ready = true,
                    protocolVersion = 1,
                    backendGeneration = "json-v1",
                },
            },
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

        string kreaMember = mode switch
        {
            "valid" => ",\"kreaAnimeToRealV1\":true",
            "false" => ",\"kreaAnimeToRealV1\":false",
            "malformed" => ",\"kreaAnimeToRealV1\":\"true\"",
            "duplicate-capability" =>
                ",\"kreaAnimeToRealV1\":false,\"kreaAnimeToRealV1\":true",
            _ => "",
        };
        string capabilities =
            "{\"durableEnqueueInboxV1\":{\"ready\":true,\"protocolVersion\":1,\"backendGeneration\":\"json-v1\"},\"photorealPromptControlsV2\":true"
            + kreaMember
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

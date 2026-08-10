using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureSelectedBatchEnhancementSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string progressPath = resultFullPath + ".progress";
        string smokeRoot = Directory.CreateTempSubdirectory("aibos-selected-batch-enhancement-").FullName;
        string folder = Path.Combine(smokeRoot, "images");
        string storeRoot = Path.Combine(smokeRoot, "stores");
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
            ["PVU_REALESRGAN_NCNN_ROOT"] = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_ROOT"),
            ["PVU_REALESRGAN_NCNN_EXE"] = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_EXE"),
            ["PVU_REALESRGAN_NCNN_MODEL_DIR"] = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_MODEL_DIR"),
        };
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

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            bool ok = false;
            object result;
            var requests = new List<string>();
            try
            {
                void Stage(string value) => File.WriteAllText(progressPath, value);
                Stage("fixture");
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(storeRoot);
                Directory.CreateDirectory(metadataIndexDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
                var sourcePaths = new List<string>(100);
                var expectedOriginalPrompts = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < 100; index++)
                {
                    string path = Path.Combine(folder, $"image-{index:000}.png");
                    WriteSmokePng(
                        path,
                        64,
                        48,
                        Color.FromRgb(
                            (byte)(40 + (index % 8) * 16),
                            (byte)(70 + (index % 7) * 17),
                            (byte)(110 + (index % 6) * 18)));
                    string embeddedPrompt = $"batch original prompt {index:000}";
                    InsertPngTextFixture(
                        path,
                        "parameters",
                        $"{embeddedPrompt}\nNegative prompt: batch negative\nSteps: 8, CFG scale: 1, Seed: {index}");
                    sourcePaths.Add(path);
                    expectedOriginalPrompts[path] = embeddedPrompt;
                }

                File.WriteAllText(statePath, "{\"version\":2,\"smokeMarker\":\"keep-state\"}");
                File.WriteAllText(favoritesPath, "{\"smokeMarker\":0}");
                File.WriteAllText(
                    seenPath,
                    JsonSerializer.Serialize(
                        sourcePaths.ToDictionary(static path => path, static _ => true, StringComparer.OrdinalIgnoreCase),
                        new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(recentPath, "{\"version\":1,\"lastFolderSet\":[],\"recentFolderSets\":[],\"updatedAtUtc\":\"\"}");
                File.WriteAllText(settingsPath, "{\"version\":1,\"smokeMarker\":\"keep-settings\"}");
                File.WriteAllText(albumsPath, "{\"version\":1,\"revision\":0,\"albums\":[],\"recentAlbumIds\":[],\"smokeMarker\":\"keep-albums\"}");
                File.WriteAllText(searchHistoryPath, "{\"version\":1,\"entries\":[],\"smokeMarker\":\"keep-search-history\"}");
                File.WriteAllText(jobsPath, "{\"version\":1,\"jobs\":[],\"smokeMarker\":\"keep-jobs\"}");
                foreach ((string key, string value) in environment)
                    Environment.SetEnvironmentVariable(key, value);

                static void SetAdapterOverrides(string? root, string? executable, string? modelDirectory)
                {
                    Environment.SetEnvironmentVariable("PVU_REALESRGAN_NCNN_ROOT", root);
                    Environment.SetEnvironmentVariable("PVU_REALESRGAN_NCNN_EXE", executable);
                    Environment.SetEnvironmentVariable("PVU_REALESRGAN_NCNN_MODEL_DIR", modelDirectory);
                }
                SetAdapterOverrides(null, null, null);

                var createdJobs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var postAttempts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var receivedPrompts = new ConcurrentDictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase);
                var receivedUpscaleSettings = new ConcurrentBag<(
                    string PresetId,
                    string AdapterId,
                    double Scale,
                    string OutputFormat)>();
                int nextJobId = 0;
                int inFlight = 0;
                int maxInFlight = 0;
                string? failOnceSource = null;
                string? responseLostAfterCreateSource = null;
                string? outcomeUnknownSource = null;

                object JobPayload(string source, string id)
                {
                    var info = new FileInfo(source);
                    return new
                    {
                        id,
                        sourceId = source,
                        sourcePath = source,
                        sourceSignature = new
                        {
                            size = info.Length,
                            mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        },
                        presetId = "anime-sharp-x2",
                        adapterId = "realesrgan-ncnn",
                        status = "queued",
                        progress = 0,
                        outputPath = (string?)null,
                        errorMessage = (string?)null,
                        createdAt = "2026-07-23T00:00:00.000Z",
                        updatedAt = "2026-07-23T00:00:01.000Z",
                    };
                }

                static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
                    => new(status)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    };

                window = HiddenWindow();
                window.SuppressStatePersistence();
                window.Show();
                await window.LoadFolderAsync(folder);
                Stage("loaded");
                window.UpdateLayout();
                const string currentSettingsPromptSentinel =
                    "current settings must not enter Original HQ";
                window.ConfigureModalPhotorealSettingsForSmoke(
                    0.4,
                    8,
                    1280,
                    currentSettingsPromptSentinel,
                    emptyPrompt: "current fallback must not enter Original HQ",
                    negativePrompt: "dummy negative");
                failOnceSource = window.EnhancementWorkspaceCatalogPathsForSmoke[4];
                responseLostAfterCreateSource = window.EnhancementWorkspaceCatalogPathsForSmoke[5];
                outcomeUnknownSource = window.EnhancementWorkspaceCatalogPathsForSmoke[6];
                window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    requests.Add($"{request.Method.Method} {route}");
                    if (request.Method == HttpMethod.Get
                        && route.EndsWith("/api/enhance/health", StringComparison.Ordinal))
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            capabilities = new
                            {
                                durableEnqueueInboxV1 = new
                                {
                                    ready = true,
                                    protocolVersion = 1,
                                    backendGeneration = "json-v1",
                                },
                            },
                        });
                    }
                    if (request.Method == HttpMethod.Get && route.EndsWith("/api/enhance/jobs", StringComparison.Ordinal))
                    {
                        object[] jobs = createdJobs
                            .Select(pair => JobPayload(pair.Key, pair.Value))
                            .ToArray();
                        return JsonResponse(HttpStatusCode.OK, new { jobs });
                    }
                    if (request.Method != HttpMethod.Post || !route.EndsWith("/api/enhance/jobs", StringComparison.Ordinal))
                        return JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected selected-batch smoke route" });

                    int currentInFlight = Interlocked.Increment(ref inFlight);
                    int observed;
                    while (currentInFlight > (observed = Volatile.Read(ref maxInFlight)))
                        Interlocked.CompareExchange(ref maxInFlight, currentInFlight, observed);
                    try
                    {
                        string idempotencyKey = request.Headers.TryGetValues(
                                "Idempotency-Key",
                                out IEnumerable<string>? values)
                            ? values.Single()
                            : throw new InvalidOperationException(
                                "Durable batch POST omitted Idempotency-Key.");
                        string body = request.Content is null
                            ? "{}"
                            : await request.Content.ReadAsStringAsync(token);
                        using JsonDocument document = JsonDocument.Parse(body);
                        string source = document.RootElement.GetProperty("sourceId").GetString() ?? "";
                        receivedUpscaleSettings.Add((
                            document.RootElement.GetProperty("presetId").GetString() ?? "",
                            document.RootElement.GetProperty("adapterId").GetString() ?? "",
                            document.RootElement.GetProperty("scale").GetDouble(),
                            document.RootElement.GetProperty("outputFormat").GetString() ?? ""));
                        string? receivedPrompt = document.RootElement.TryGetProperty(
                                "prompt",
                                out JsonElement promptElement)
                            ? promptElement.GetString()
                            : null;
                        receivedPrompts.TryAdd(source, receivedPrompt);
                        int attempt = postAttempts.AddOrUpdate(source, 1, static (_, previous) => previous + 1);
                        await Task.Delay(35, token);
                        if (string.Equals(source, failOnceSource, StringComparison.OrdinalIgnoreCase) && attempt == 1)
                        {
                            return JsonResponse(
                                HttpStatusCode.Conflict,
                                new
                                {
                                    error = "synthetic large-image confirmation",
                                    code = "UPSCALE_REQUIRES_CONFIRMATION",
                                });
                        }
                        if (string.Equals(source, outcomeUnknownSource, StringComparison.OrdinalIgnoreCase) && attempt == 1)
                            throw new HttpRequestException("synthetic transport loss before receipt");

                        string id = createdJobs.GetOrAdd(
                            source,
                            _ => $"batch-job-{Interlocked.Increment(ref nextJobId):000}");
                        if (string.Equals(source, responseLostAfterCreateSource, StringComparison.OrdinalIgnoreCase) && attempt == 1)
                            throw new HttpRequestException("synthetic transport loss after durable create");
                        return JsonResponse(HttpStatusCode.Accepted, new
                        {
                            receipt = new
                            {
                                idempotencyKey,
                                jobId = id,
                            },
                            job = JobPayload(source, id),
                        });
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight);
                    }
                });

                async Task<BatchEnhancementSmokeSnapshot> CaptureAdapterOverrideAsync(
                    string? root,
                    string? executable,
                    string? modelDirectory)
                {
                    window.ConfigureUpscaleSettingsForSmoke(
                        "anime-sharp-x2",
                        "realesrgan-ncnn",
                        2d,
                        "webp");
                    SetAdapterOverrides(root, executable, modelDirectory);
                    window.SelectRangeForSmoke(0, 0);
                    await window.OpenBatchEnhancementForSmokeAsync();
                    BatchEnhancementSmokeSnapshot snapshot = window.BatchEnhancementForSmoke();
                    window.CloseBatchEnhancementForSmoke();
                    return snapshot;
                }

                bool ordinaryBrowsingPassive = requests.Count == 0;
                var sourceBefore = sourcePaths.ToDictionary(
                    static path => path,
                    FileFingerprint,
                    StringComparer.OrdinalIgnoreCase);

                window.SelectRangeForSmoke(0, 0);
                await window.OpenBatchEnhancementForSmokeAsync();
                BatchEnhancementSmokeSnapshot one = window.BatchEnhancementForSmoke();
                window.CloseBatchEnhancementForSmoke();
                Stage("preflight-1");

                BatchEnhancementSmokeSnapshot customRoot = await CaptureAdapterOverrideAsync(
                    @"\\127.0.0.1\aibos-security-smoke\root",
                    null,
                    null);
                BatchEnhancementSmokeSnapshot customExecutable = await CaptureAdapterOverrideAsync(
                    null,
                    @"\\127.0.0.1\aibos-security-smoke\adapter.exe",
                    null);
                BatchEnhancementSmokeSnapshot customModelDirectory = await CaptureAdapterOverrideAsync(
                    null,
                    null,
                    @"\\127.0.0.1\aibos-security-smoke\models");
                BatchEnhancementSmokeSnapshot whitespaceOverride = await CaptureAdapterOverrideAsync(
                    " ",
                    null,
                    null);
                SetAdapterOverrides(
                    @"\\127.0.0.1\aibos-security-smoke\root",
                    @"\\127.0.0.1\aibos-security-smoke\adapter.exe",
                    @"\\127.0.0.1\aibos-security-smoke\models");
                window.ConfigureUpscaleSettingsForSmoke(
                    "anime-sharp-x2",
                    "comfyui",
                    2d,
                    "webp");
                var restoredUpscaleSettings = window.UpscaleSettingsForSmoke;
                Stage("adapter-security");

                window.SelectRangeForSmoke(0, 9);
                string[] batchDoubleClickSources = window
                    .EnhancementWorkspaceCatalogPathsForSmoke
                    .Take(10)
                    .ToArray();
                await window.OpenBatchEnhancementForSmokeAsync();
                BatchEnhancementSmokeSnapshot ten = window.BatchEnhancementForSmoke();
                window.CloseBatchEnhancementForSmoke();
                Stage("preflight-10");

                window.SelectRangeForSmoke(0, 99);
                await window.OpenBatchEnhancementForSmokeAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                BatchEnhancementSmokeSnapshot hundred = window.BatchEnhancementForSmoke();
                bool keyboardSurface = window.BatchEnhancementKeyboardSurfaceForSmoke;
                bool narrowLayoutFits = window.ApplyBatchEnhancementNarrowLayoutForSmoke();
                int postsBeforeReviewClose = window.BatchEnhancementForSmoke().PostRequests;
                bool escapeClosedReview = window.CloseTopmostOverlayForSmoke()
                    && !window.BatchEnhancementForSmoke().Visible;
                int postsAfterReviewClose = window.BatchEnhancementForSmoke().PostRequests;
                Stage("preflight-100");

                window.SelectRangeForSmoke(0, 9);
                await window.OpenBatchEnhancementForSmokeAsync();
                var storesBefore = environment
                    .Where(static pair => !pair.Key.EndsWith("METADATA_INDEX_DIRECTORY", StringComparison.Ordinal))
                    .ToDictionary(static pair => pair.Key, pair => FileFingerprint(pair.Value), StringComparer.Ordinal);
                await window.StartBatchEnhancementDoubleClickForSmokeAsync();
                BatchEnhancementSmokeSnapshot firstRun = window.BatchEnhancementForSmoke();
                Stage("first-run");
                await window.RetryFailedBatchEnhancementForSmokeAsync(confirmLarge: true);
                BatchEnhancementSmokeSnapshot afterRetry = window.BatchEnhancementForSmoke();
                Stage("retry");
                bool oneCreatedJobPerAcceptedSource = createdJobs.Count == 9
                    && createdJobs.Values.Distinct(StringComparer.Ordinal).Count() == 9
                    && postAttempts.Count == 10
                    && postAttempts.All(pair =>
                        string.Equals(pair.Key, failOnceSource, StringComparison.OrdinalIgnoreCase)
                            ? pair.Value == 2
                            : pair.Value == 1);
                static int CountBatchState(
                    BatchEnhancementSmokeSnapshot snapshot,
                    string state)
                    => snapshot.Items.Count(item => string.Equals(
                        item.State,
                        state,
                        StringComparison.Ordinal));
                bool transientReceiptsSaved = postAttempts.TryGetValue(responseLostAfterCreateSource!, out int lostAfterCreateAttempts)
                    && lostAfterCreateAttempts == 1
                    && postAttempts.TryGetValue(outcomeUnknownSource!, out int unknownAttempts)
                    && unknownAttempts == 1
                    && afterRetry.OutcomeUnknown == 0
                    && CountBatchState(afterRetry, "SavedForDelivery") == 2;

                await window.ViewBatchEnhancementJobsForSmokeAsync();
                EnhancementJobsWorkspaceSmokeSnapshot handoff = window.EnhancementJobsWorkspaceForSmoke();
                window.CloseEnhancementJobsForSmoke();
                Stage("handoff");

                window.SelectRangeForSmoke(0, 99);
                await window.OpenBatchEnhancementForSmokeAsync();
                BatchEnhancementSmokeSnapshot cancelPreflight = window.BatchEnhancementForSmoke();
                int cancelStartPosts = cancelPreflight.PostRequests;
                Task cancelRun = window.StartBatchEnhancementDoubleClickForSmokeAsync();
                for (int attempt = 0; attempt < 300
                    && window.BatchEnhancementForSmoke().PostRequests < cancelStartPosts + 8; attempt++)
                {
                    await Task.Delay(10);
                }
                window.StopBatchEnhancementForSmoke();
                await cancelRun;
                BatchEnhancementSmokeSnapshot afterStop = window.BatchEnhancementForSmoke();
                window.CloseBatchEnhancementForSmoke();
                Stage("stopped");

                var sourceAfter = sourcePaths.ToDictionary(
                    static path => path,
                    FileFingerprint,
                    StringComparer.OrdinalIgnoreCase);
                var storesAfter = environment
                    .Where(static pair => !pair.Key.EndsWith("METADATA_INDEX_DIRECTORY", StringComparison.Ordinal))
                    .ToDictionary(static pair => pair.Key, pair => FileFingerprint(pair.Value), StringComparer.Ordinal);
                bool sourceUnchanged = sourceBefore.All(pair =>
                    sourceAfter.TryGetValue(pair.Key, out string? fingerprint) && fingerprint == pair.Value);
                bool storesUnchanged = storesBefore.All(pair =>
                    storesAfter.TryGetValue(pair.Key, out string? fingerprint) && fingerprint == pair.Value);
                bool managedOutputsEmpty = !Directory.Exists(outputRoot)
                    || !Directory.EnumerateFileSystemEntries(outputRoot, "*", SearchOption.AllDirectories).Any();
                bool preflightPostZero = one.PostRequests == 0
                    && ten.PostRequests == 0
                    && hundred.PostRequests == 0;
                bool reviewCancelPostZero = postsBeforeReviewClose == postsAfterReviewClose;
                bool boundedConcurrency = maxInFlight <= 4
                    && firstRun.MaxInFlight <= 4
                    && afterStop.MaxInFlight <= 4;
                bool doubleClickSuppressed = firstRun.PostRequests == 10
                    && firstRun.Queued == 7
                    && firstRun.Failed == 1
                    && firstRun.OutcomeUnknown == 0
                    && CountBatchState(firstRun, "SavedForDelivery") == 2;
                bool doubleClickPromptProvenance = batchDoubleClickSources
                    .All(path => receivedPrompts.TryGetValue(
                            Path.GetFullPath(path),
                            out string? receivedPrompt)
                        && expectedOriginalPrompts.TryGetValue(
                            path,
                            out string? expectedPrompt)
                        && string.Equals(
                            receivedPrompt,
                            expectedPrompt,
                            StringComparison.Ordinal)
                        && !string.Equals(
                            receivedPrompt,
                            currentSettingsPromptSentinel,
                            StringComparison.Ordinal));
                bool failedOnlyRetry = afterRetry.PostRequests == 11
                    && afterRetry.Queued == 8
                    && afterRetry.Failed == 0
                    && afterRetry.OutcomeUnknown == 0
                    && CountBatchState(afterRetry, "SavedForDelivery") == 2
                    && oneCreatedJobPerAcceptedSource;
                bool handoffOk = handoff.Visible
                    && handoff.Filtered == 9
                    && handoff.Active == 9
                    && handoff.Highlighted == 8;
                bool durableReservationsNotStoppable = afterStop.Stopped == 0
                    && CountBatchState(afterStop, "Queued")
                        + CountBatchState(afterStop, "SavedForDelivery")
                        == cancelPreflight.Eligible
                    && createdJobs.Count > 10
                    && requests.All(static request => !request.Contains("/cancel", StringComparison.Ordinal));
                bool sizesOk = one.Selected == 1
                    && one.Eligible == 1
                    && ten.Selected == 10
                    && ten.Eligible == 10
                    && hundred.Selected == 100
                    && hundred.Eligible == 100;
                bool responsiveOpen = hundred.SynchronousOpenMilliseconds <= 100;
                static bool IsCustomAdapterDeferred(BatchEnhancementSmokeSnapshot snapshot)
                    => snapshot.AdapterStatus.Contains(
                        "custom configuration present; the companion will validate it",
                        StringComparison.Ordinal);
                bool customAdapterPathDeferred =
                    !IsCustomAdapterDeferred(one)
                    && IsCustomAdapterDeferred(customRoot)
                    && IsCustomAdapterDeferred(customExecutable)
                    && IsCustomAdapterDeferred(customModelDirectory)
                    && IsCustomAdapterDeferred(whitespaceOverride)
                    && customRoot.PostRequests == 0
                    && customExecutable.PostRequests == 0
                    && customModelDirectory.PostRequests == 0
                    && whitespaceOverride.PostRequests == 0;
                bool restoredComfySettings =
                    restoredUpscaleSettings.PresetId == "anime-sharp-x2"
                    && restoredUpscaleSettings.AdapterId == "comfyui"
                    && restoredUpscaleSettings.Scale == 2d
                    && restoredUpscaleSettings.OutputFormat == "webp";
                int expectedBatchPostCount = postAttempts.Values.Sum();
                bool batchPostSettingsMatch = expectedBatchPostCount > 0
                    && receivedUpscaleSettings.Count == expectedBatchPostCount
                    && receivedUpscaleSettings.All(static settings =>
                        settings.PresetId == "anime-sharp-x2"
                        && settings.AdapterId == "comfyui"
                        && settings.Scale == 2d
                        && settings.OutputFormat == "webp");

                ok = ordinaryBrowsingPassive
                    && sizesOk
                    && preflightPostZero
                    && reviewCancelPostZero
                    && escapeClosedReview
                    && keyboardSurface
                    && narrowLayoutFits
                    && responsiveOpen
                    && customAdapterPathDeferred
                    && restoredComfySettings
                    && batchPostSettingsMatch
                    && boundedConcurrency
                    && doubleClickSuppressed
                    && doubleClickPromptProvenance
                    && failedOnlyRetry
                    && transientReceiptsSaved
                    && handoffOk
                    && cancelPreflight.Selected == 100
                    && cancelPreflight.Eligible == 91
                    && durableReservationsNotStoppable
                    && sourceUnchanged
                    && storesUnchanged
                    && managedOutputsEmpty;
                result = new
                {
                    ok,
                    ordinaryBrowsingPassive,
                    one,
                    ten,
                    hundred,
                    preflightPostZero,
                    reviewCancelPostZero,
                    escapeClosedReview,
                    keyboardSurface,
                    narrowLayoutFits,
                    responsiveOpen,
                    customAdapterPathDeferred,
                    adapterSecurity = new
                    {
                        defaultStatus = one.AdapterStatus,
                        rootStatus = customRoot.AdapterStatus,
                        executableStatus = customExecutable.AdapterStatus,
                        modelDirectoryStatus = customModelDirectory.AdapterStatus,
                        whitespaceStatus = whitespaceOverride.AdapterStatus,
                    },
                    restoredComfySettings,
                    restoredUpscaleSettings = new
                    {
                        restoredUpscaleSettings.PresetId,
                        restoredUpscaleSettings.AdapterId,
                        restoredUpscaleSettings.Scale,
                        restoredUpscaleSettings.OutputFormat,
                    },
                    batchPostSettingsMatch,
                    batchPostSettingsCount = receivedUpscaleSettings.Count,
                    batchPostSettingVariants = receivedUpscaleSettings
                        .GroupBy(static settings => settings)
                        .Select(static group => new
                        {
                            group.Key.PresetId,
                            group.Key.AdapterId,
                            group.Key.Scale,
                            group.Key.OutputFormat,
                            RequestCount = group.Count(),
                        })
                        .ToArray(),
                    boundedConcurrency,
                    maxInFlight,
                    doubleClickSuppressed,
                    doubleClickPromptProvenance,
                    firstRun,
                    failedOnlyRetry,
                    transientReceiptsSaved,
                    afterRetry,
                    oneCreatedJobPerAcceptedSource,
                    handoff,
                    cancelPreflight,
                    durableReservationsNotStoppable,
                    afterStop,
                    sourceUnchanged,
                    storesUnchanged,
                    managedOutputsEmpty,
                    requestCount = requests.Count,
                    requests,
                };
                Stage("complete");
            }
            catch (Exception ex)
            {
                result = new { ok = false, message = ex.ToString(), requests };
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
            try
            {
                Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
            Shutdown(ok ? 0 : 1);
        }, DispatcherPriority.ContextIdle);
    }
}

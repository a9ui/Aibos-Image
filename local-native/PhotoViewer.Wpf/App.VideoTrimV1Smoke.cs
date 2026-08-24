using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private const string VideoTrimV1SmokeStagedJobId =
        "41000000-0000-4000-8000-000000000004";

    private async void CaptureVideoTrimV1Smoke(
        string resultPath,
        string[] arguments)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string fixturePath = RequireVideoToolsV2ReaderArgument(
            arguments,
            "--fixture");
        string? pairedJobPath = TryGetVideoTrimV1SmokeArgument(
            arguments,
            "--paired-job");
        bool pairedJobChecked = pairedJobPath is not null;
        bool pairedJobExact = !pairedJobChecked;
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-trim-v1-")
            .FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string outputRoot = Path.Combine(storeRoot, "outputs");
        string sourcePath = Path.Combine(sourceRoot, "trim-source.mp4");
        string secondPath = Path.Combine(sourceRoot, "second.mp4");
        string unsupportedPath = Path.Combine(sourceRoot, "unsupported.txt");
        var environment = new Dictionary<string, string?>
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
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputRoot,
        };
        Dictionary<string, string?> previousEnvironment = environment.Keys
            .ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        MainWindow? window = null;
        bool ok = false;
        object result = new { ok = false, message = "Video Trim smoke did not run." };

        try
        {
            if (pairedJobPath is not null)
                pairedJobExact = VerifyPairedVideoTrimV1SmokeJob(pairedJobPath);
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            WriteIsoBmffSmokeVideo(secondPath);
            File.WriteAllText(
                unsupportedPath,
                "not a supported video",
                new UTF8Encoding(false));
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            string sourceFingerprint = FingerprintVideoEditV2File(sourcePath);
            var sourceInfo = new FileInfo(sourcePath);
            string sourceSha256 = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(sourcePath)));
            using JsonDocument fixtureDocument = JsonDocument.Parse(
                File.ReadAllText(fixturePath));
            JsonElement fixture = fixtureDocument.RootElement;
            string currentHealth = BuildVideoTrimV1Health(
                fixture.GetProperty("currentHealth")
                    .GetProperty("capabilities")
                    .GetProperty("videoTrimV1"),
                ready: false);
            string readyHealth = BuildVideoTrimV1Health(
                fixture.GetProperty("futureReadyShape")
                    .GetProperty("capability"),
                ready: true);
            string probeResponse = fixture
                .GetProperty("sourceInspectionVectors")
                .GetProperty("probe")
                .GetProperty("response")
                .GetRawText();
            string previewResponse = fixture
                .GetProperty("sourceInspectionVectors")
                .GetProperty("preview")
                .GetProperty("response")
                .GetRawText();

            string materializedFixture = File.ReadAllText(fixturePath)
                .Replace(
                    "${OUTPUT_ROOT}",
                    outputRoot.Replace('\\', '/'),
                    StringComparison.Ordinal);
            JsonObject fixtureRoot = JsonNode.Parse(materializedFixture)!
                .AsObject();
            JsonArray durableJobs = fixtureRoot["durableJobVectors"]!["jobs"]!
                .AsArray();
            var jobs = durableJobs
                .Select(static node => node!.DeepClone().AsObject())
                .ToList();
            foreach (JsonObject job in jobs)
            {
                job["presetHash"] = global::PhotoViewer.Wpf.MainWindow
                    .ComputeVideoTrimV1PresetHashForSmoke(
                        JsonSerializer.SerializeToElement(job["videoTrim"]));
            }
            JsonObject malformed = fixtureRoot["negativeVectors"]!["malformedDurableJob"]!["job"]!
                .DeepClone().AsObject();
            JsonObject future = fixtureRoot["negativeVectors"]!["futureDurableJob"]!["job"]!
                .DeepClone().AsObject();
            JsonObject dependent = future.DeepClone().AsObject();
            dependent["id"] = "trim-fixture-dependent-future";
            dependent["sourceVideoJobId"] = VideoTrimV1SmokeStagedJobId;
            jobs.Add(malformed);
            jobs.Add(future);
            jobs.Add(dependent);
            string succeededOutput = jobs.Single(job =>
                    job["id"]?.GetValue<string>() == "trim-fixture-succeeded")
                ["outputPath"]!.GetValue<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(succeededOutput)!);
            WriteIsoBmffSmokeVideo(succeededOutput);

            var inspectionActions = new List<string>();
            var enqueueBodies = new List<string>();
            var mutationRoutes = new List<string>();
            int healthReads = 0;
            int jobsReads = 0;
            int wakeRequests = 0;
            int unexpectedRequests = 0;
            bool readyLane = false;
            bool loopbackOnly = true;

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            window.SetCanonicalPathResolverForSmoke(Path.GetFullPath);
            window.ConfigureModalEnhancementForSmoke(async (request, token) =>
            {
                string route = request.RequestUri?.AbsolutePath ?? "";
                loopbackOnly &= request.RequestUri is { IsLoopback: true };
                if (request.Method == HttpMethod.Get
                    && route == "/api/enhance/health")
                {
                    healthReads++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.OK,
                        readyLane ? readyHealth : currentHealth);
                }
                if (request.Method == HttpMethod.Get
                    && route == "/api/enhance/jobs")
                {
                    jobsReads++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.OK,
                        BuildVideoToolsV2FlowJobsResponse(jobs));
                }
                if (request.Method == HttpMethod.Post
                    && route == "/api/enhance/video-trim/v1/source-inspection")
                {
                    string body = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    using JsonDocument document = JsonDocument.Parse(body);
                    JsonElement root = document.RootElement;
                    string action = root.GetProperty("action").GetString() ?? "";
                    inspectionActions.Add(action);
                    JsonElement selector = root.GetProperty("source");
                    bool sourceExact = selector.GetProperty("kind").GetString()
                            == "displayed-file"
                        && selector.GetProperty("path").GetString() == sourcePath
                        && selector.GetProperty("size").GetInt64() == sourceInfo.Length
                        && selector.GetProperty("sha256").GetString() == sourceSha256;
                    bool exact = sourceExact && action switch
                    {
                        "probe" => root.EnumerateObject().Count() == 2,
                        "preview" =>
                            root.GetProperty("sourceIdentityDigest").GetString()
                                == new string('b', 64)
                            && root.GetProperty("selection")
                                .GetProperty("startFrame").GetInt32() == 24
                            && root.GetProperty("selection")
                                .GetProperty("endFrameExclusive").GetInt32() == 72
                            && root.GetProperty("frames").EnumerateArray()
                                .Select(static value => value.GetInt32())
                                .SequenceEqual([24, 47, 71]),
                        _ => false,
                    };
                    if (!exact)
                        unexpectedRequests++;
                    return VideoEditV2SmokeJsonResponse(
                        exact ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                        exact
                            ? action == "probe" ? probeResponse : previewResponse
                            : "{\"error\":\"bad trim inspection\"}");
                }
                if (request.Method == HttpMethod.Post
                    && route == "/api/enhance/jobs")
                {
                    string body = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    enqueueBodies.Add(body);
                    return BuildVideoToolsV2FlowAcceptedResponse(
                        request,
                        $"trim-enqueue-{enqueueBodies.Count}");
                }
                if (request.Method == HttpMethod.Post
                    && route == "/api/enhance/inbox/wake")
                {
                    wakeRequests++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.Accepted,
                        "{\"accepted\":true}");
                }
                if (TryHandleVideoTrimV1SmokeMutation(
                        request,
                        route,
                        jobs,
                        mutationRoutes,
                        out HttpResponseMessage mutationResponse))
                {
                    return mutationResponse;
                }
                unexpectedRequests++;
                return VideoEditV2SmokeJsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"error\":\"unexpected route\"}");
            });

            window.Show();
            Task smokeTask = await window.Dispatcher.InvokeAsync(async () =>
            {
                ExternalVideoDropSmokeSnapshot multiple =
                    await window.DropExternalVideoForSmokeAsync(
                        [sourcePath, secondPath]);
                ExternalVideoDropSmokeSnapshot unsupported =
                    await window.DropExternalVideoForSmokeAsync(
                        [unsupportedPath]);
                ExternalVideoDropSmokeSnapshot dropped =
                    await window.DropExternalVideoForSmokeAsync([sourcePath]);
                bool entryExact = !multiple.Accepted
                    && !unsupported.Accepted
                    && dropped.Accepted
                    && dropped.ModalVisible
                    && dropped.ShowingVideo
                    && dropped.SourcePinned
                    && window.VideoEditV2EntryVisibleForSmoke
                    && window.VideoTrimV1EntryVisibleForSmoke
                    && window.VideoTrimV1ExternalContextEntryForSmoke
                    && window.VideoFinishV2EntryVisibleForSmoke;

                int passiveBefore = healthReads + jobsReads
                    + inspectionActions.Count + enqueueBodies.Count
                    + mutationRoutes.Count + wakeRequests;
                string passiveStoreBefore = FingerprintVideoEditV2Tree(storeRoot);
                bool opened = window.OpenVideoTrimV1ForSmoke();
                bool passiveOpen = opened
                    && window.VideoTrimV1BoardVisibleForSmoke
                    && window.VideoTrimV1StartDisabledForSmoke
                    && passiveBefore == healthReads + jobsReads
                        + inspectionActions.Count + enqueueBodies.Count
                        + mutationRoutes.Count + wakeRequests
                    && passiveStoreBefore == FingerprintVideoEditV2Tree(storeRoot);

                _ = window.SetVideoTrimV1SelectionForSmoke(24, 72);
                bool loaded = await window.LoadVideoTrimV1FramesForSmokeAsync();
                (double overviewMaximum, double overviewValue) =
                    window.VideoTrimV1OverviewForSmoke;
                bool previewExact = loaded
                    && window.VideoTrimV1PreviewFramesForSmoke.SequenceEqual(
                        ["24", "47", "71"],
                        StringComparer.Ordinal)
                    && window.VideoTrimV1PreviewImagesLoadedForSmoke
                    && overviewMaximum == 240
                    && overviewValue == 72;

                int localHttpBefore = healthReads + jobsReads
                    + inspectionActions.Count + enqueueBodies.Count
                    + mutationRoutes.Count + wakeRequests;
                window.SetVideoTrimV1CurrentFrameForSmoke(47);
                window.UseVideoTrimV1CurrentStartForSmoke();
                window.StepVideoTrimV1SelectionForSmoke("start", -1);
                window.UseVideoTrimV1CurrentEndForSmoke();
                window.StepVideoTrimV1SelectionForSmoke("end", 1);
                window.SeekVideoTrimV1PreviewForSmoke("middle");
                (int localStart, int localEnd) =
                    window.VideoTrimV1SelectionForSmoke;
                int localCurrent = window.VideoTrimV1CurrentFrameForSmoke;
                bool localControlsExact =
                    (localStart, localEnd) == (46, 49)
                    && localCurrent == 47
                    && localHttpBefore == healthReads + jobsReads
                        + inspectionActions.Count + enqueueBodies.Count
                        + mutationRoutes.Count + wakeRequests;

                _ = window.SetVideoTrimV1SelectionForSmoke(24, 72);
                bool reloaded = await window.LoadVideoTrimV1FramesForSmokeAsync();
                bool currentDisabled = reloaded
                    && !await window.RefreshVideoTrimV1ReadinessForSmokeAsync()
                    && window.VideoTrimV1StartDisabledForSmoke;
                readyLane = true;
                bool futureReady = await window
                    .RefreshVideoTrimV1ReadinessForSmokeAsync();
                int enqueueBefore = enqueueBodies.Count;
                bool started = futureReady
                    && await window.StartVideoTrimV1ForSmokeAsync();
                bool durableExact = started
                    && enqueueBodies.Count == enqueueBefore + 1
                    && VideoTrimV1SmokeRequestExact(
                        enqueueBodies[^1],
                        sourcePath,
                        sourceInfo.Length,
                        sourceSha256,
                        "preserve");

                bool contractVectors = VerifyVideoTrimV1SmokeContractVectors(
                    sourcePath,
                    sourceInfo.Length,
                    sourceInfo.LastWriteTimeUtc,
                    sourceSha256);
                bool readerExact = VerifyVideoTrimV1SmokeReaderVectors(
                    jobs,
                    malformed,
                    future,
                    fixtureRoot["durableJobVectors"]![
                        "knownLifecyclePolicyVectors"]!.AsObject());
                object[] readerDiagnostics = jobs.Take(6).Select(job => new
                {
                    id = job["id"]?.GetValue<string>(),
                    presetHash = job["presetHash"]?.GetValue<string>(),
                    computedPresetHash = global::PhotoViewer.Wpf.MainWindow
                        .ComputeVideoTrimV1PresetHashForSmoke(
                            JsonSerializer.SerializeToElement(
                                job["videoTrim"])),
                    snapshot = global::PhotoViewer.Wpf.MainWindow
                        .ReadVideoTrimV1JobForSmoke(
                            JsonSerializer.SerializeToElement(job)),
                }).ToArray();

                JsonObject stagedSucceeded = BuildVideoTrimV1StagedInventoryJob(
                    durableJobs[2]!.AsObject(),
                    sourcePath,
                    smokeRoot,
                    outputRoot);
                jobs.Add(stagedSucceeded.DeepClone().AsObject());
                using JsonDocument inventoryDocument = JsonDocument.Parse(
                    "[" + stagedSucceeded.ToJsonString() + "]");
                bool inventoryBuilt = window.DiagnoseVideoTrimV1InventoryForSmoke(
                    inventoryDocument.RootElement[0],
                    out bool inventoryReaderExact,
                    out bool inventoryOutputExact,
                    out string? inventoryVersionKind);
                string[] inventoryRoots = window
                    .ResolveVideoToolsV2ManagedInventoryForSmoke(
                        inventoryDocument.RootElement,
                        out string[] inventoryKinds,
                        out string[] inventoryOutputs,
                        out string[] inventoryLabels);
                bool inventoryExact = inventoryRoots.Length == 1
                    && inventoryBuilt
                    && inventoryReaderExact
                    && inventoryOutputExact
                    && inventoryVersionKind == "trim"
                    && inventoryKinds.SequenceEqual(["trim"], StringComparer.Ordinal)
                    && inventoryOutputs.SequenceEqual(
                        [stagedSucceeded["outputPath"]!.GetValue<string>()],
                        StringComparer.OrdinalIgnoreCase)
                    && inventoryLabels.SequenceEqual(
                        ["トリム 1/1"],
                        StringComparer.Ordinal);
                bool versionReinitialized = inventoryExact
                    && window.ReinitializeModalVideoVersionsForSmoke();
                bool versionSelected = versionReinitialized
                    && window.SelectModalVideoJobForSmoke(
                        VideoTrimV1SmokeStagedJobId);
                bool expandedOutputExact = versionSelected
                    && window.ModalShowingVideoForSmoke
                    && window.ModalVideoVersionIndexForSmoke == 0
                    && window.ModalVideoVersionPlaybackMetadataForSmoke
                        .SequenceEqual([(24, 48)]);
                VideoTrimV1JobSmokeSnapshot? stagedReader =
                    global::PhotoViewer.Wpf.MainWindow
                        .ReadVideoTrimV1JobForSmoke(
                            JsonSerializer.SerializeToElement(stagedSucceeded));
                JsonObject stagedSourceIdDrift = stagedSucceeded
                    .DeepClone().AsObject();
                stagedSourceIdDrift["sourceId"] = sourcePath.ToLowerInvariant();
                VideoTrimV1JobSmokeSnapshot? stagedSourceIdDriftReader =
                    global::PhotoViewer.Wpf.MainWindow
                        .ReadVideoTrimV1JobForSmoke(
                            JsonSerializer.SerializeToElement(
                                stagedSourceIdDrift));
                bool stagedSourceIdProtected = stagedSourceIdDriftReader is
                {
                    Claimed: true,
                    ExactCurrent: false,
                    ReaderOnly: true,
                    SupportedMutation: false,
                    FilterKey: null,
                    VisibleActionKinds.Length: 0,
                };

                int passiveEnqueueBeforeJobs = enqueueBodies.Count;
                int passiveWakeBeforeJobs = wakeRequests;
                int passiveMutationsBeforeJobs = mutationRoutes.Count;
                await window.OpenEnhancementJobsForSmokeAsync();
                window.SelectEnhancementJobsVideoKindFilterForSmoke("trim");
                EnhancementJobsWorkspaceSmokeSnapshot workspace =
                    window.EnhancementJobsWorkspaceForSmoke();
                bool jobsFilterExact = workspace.Visible
                    && workspace.VisibleIds.Length == 7
                    && workspace.VisibleIds.Contains(
                        VideoTrimV1SmokeStagedJobId,
                        StringComparer.Ordinal)
                    && workspace.VisibleIds.Count(id => id.StartsWith(
                        "trim-fixture-",
                        StringComparison.Ordinal)) == 6
                    && workspace.VisibleOperationLabels.All(label =>
                        label.Contains("動画トリム", StringComparison.Ordinal))
                    && window.EnhancementJobsOperationFilterForSmoke == "video"
                    && window.EnhancementJobsVideoKindFilterForSmoke == "trim";
                bool passiveJobs = enqueueBodies.Count == passiveEnqueueBeforeJobs
                    && wakeRequests == passiveWakeBeforeJobs
                    && mutationRoutes.Count == passiveMutationsBeforeJobs;

                bool queuedCanceled = await window
                    .CancelEnhancementJobForSmokeAsync("trim-fixture-queued");
                bool runningCanceled = await window
                    .CancelEnhancementJobForSmokeAsync("trim-fixture-running");
                bool failedRetried = await window
                    .RetryEnhancementJobForSmokeAsync("trim-fixture-failed");
                bool terminalDismissed = await window
                    .DismissEnhancementJobForSmokeAsync("trim-fixture-canceled");
                bool deletedFixtureDismissed = await window
                    .DismissEnhancementJobForSmokeAsync("trim-fixture-deleted");
                int deletesBefore = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                bool protectedDelete = await window
                    .DeleteEnhancementJobOutputForSmokeAsync(
                        VideoTrimV1SmokeStagedJobId);
                int deletesProtected = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                jobs.RemoveAll(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-dependent-future");
                await window.RefreshEnhancementJobsForSmokeAsync();
                bool deletedAfterRelease = await window
                    .DeleteEnhancementJobOutputForSmokeAsync(
                        VideoTrimV1SmokeStagedJobId);
                JsonObject? deletedStagedJob = jobs.FirstOrDefault(job =>
                    job["id"]?.GetValue<string>()
                        == VideoTrimV1SmokeStagedJobId);
                VideoTrimV1JobSmokeSnapshot? deletedStaged =
                    deletedStagedJob is null
                        ? null
                        : global::PhotoViewer.Wpf.MainWindow
                            .ReadVideoTrimV1JobForSmoke(
                                JsonSerializer.SerializeToElement(
                                    deletedStagedJob));
                bool deletedTransitionExact = deletedStaged is
                {
                    ExactCurrent: true,
                    ReaderOnly: false,
                    SupportedMutation: true,
                    FilterKey: "trim",
                    Status: "deleted",
                    CanRetry: false,
                    CanDismiss: true,
                    CanDeleteOutput: false,
                };
                bool deletedStagedDismissed = await window
                    .DismissEnhancementJobForSmokeAsync(
                        VideoTrimV1SmokeStagedJobId);
                int deletesAfter = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                bool jobsActions = queuedCanceled
                    && runningCanceled
                    && failedRetried
                    && terminalDismissed
                    && deletedFixtureDismissed
                    && protectedDelete
                    && deletesProtected == deletesBefore
                    && deletedAfterRelease
                    && deletedTransitionExact
                    && deletedStagedDismissed
                    && deletesAfter == deletesBefore + 1;
                window.CloseEnhancementJobsForSmoke();

                bool sourceUntouched = sourceFingerprint
                    == FingerprintVideoEditV2File(sourcePath);
                bool networkExact = loopbackOnly
                    && unexpectedRequests == 0
                    && inspectionActions.SequenceEqual(
                        ["probe", "preview", "probe", "preview"],
                        StringComparer.Ordinal)
                    && enqueueBodies.Count == 1
                    && wakeRequests == 0;
                ok = entryExact
                    && passiveOpen
                    && previewExact
                    && localControlsExact
                    && currentDisabled
                    && durableExact
                    && contractVectors
                    && readerExact
                    && inventoryExact
                    && expandedOutputExact
                    && jobsFilterExact
                    && passiveJobs
                    && jobsActions
                    && sourceUntouched
                    && networkExact
                    && stagedReader is { ExactCurrent: true, ReaderOnly: false }
                    && stagedSourceIdProtected
                    && pairedJobExact;
                result = new
                {
                    ok,
                    pairedJobChecked,
                    pairedJobExact,
                    entryExact,
                    passiveOpen,
                    previewExact,
                    localControlsExact,
                    currentDisabled,
                    futureReady,
                    durableExact,
                    contractVectors,
                    readerExact,
                    inventoryExact,
                    expandedOutputExact,
                    versionReinitialized,
                    versionSelected,
                    jobsFilterExact,
                    passiveJobs,
                    jobsActions,
                    sourceUntouched,
                    networkExact,
                    localStart,
                    localEnd,
                    localCurrent,
                    readerDiagnostics,
                    inventoryRoots,
                    inventoryKinds,
                    inventoryOutputs,
                    inventoryLabels,
                    inventoryBuilt,
                    inventoryReaderExact,
                    inventoryOutputExact,
                    inventoryVersionKind,
                    stagedReader,
                    jobsVisibleIds = workspace.VisibleIds,
                    jobsVisibleLabels = workspace.VisibleOperationLabels,
                    inspectionActions,
                    healthReads,
                    jobsReads,
                    wakeRequests,
                    mutationRoutes,
                    unexpectedRequests,
                };
            });
            await smokeTask;
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                pairedJobChecked,
                pairedJobExact,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                stackTrace = ex.ToString(),
            };
        }
        finally
        {
            try { window?.Close(); } catch { }
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            TryDeleteVideoFinishV2SmokeRoot(smokeRoot);
            Shutdown(ok ? 0 : 1);
        }
    }

    private static bool VerifyPairedVideoTrimV1SmokeJob(string pairedJobPath)
    {
        SharedJsonDocumentReadResult pairedRead =
            SharedJsonDocumentReader.Read(pairedJobPath);
        if (pairedRead.Status is not SharedJsonDocumentReadStatus.Success
            || pairedRead.Json is null)
        {
            throw new InvalidDataException(
                pairedRead.Error ?? "The paired Video Trim Job is missing.");
        }

        using JsonDocument pairedDocument = JsonDocument.Parse(
            pairedRead.Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (pairedDocument.RootElement.ValueKind == JsonValueKind.Object)
        {
            VideoTrimV1JobSmokeSnapshot? pairedSnapshot =
                global::PhotoViewer.Wpf.MainWindow
                    .ReadVideoTrimV1JobForSmoke(pairedDocument.RootElement);
            bool exactQueued = pairedSnapshot is
            {
                Claimed: true,
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                FilterKey: "trim",
                Status: "queued",
                CanCancel: true,
                CanReorder: true,
            };
            if (!exactQueued)
            {
                throw new InvalidDataException(
                    "The paired Video Trim Job is not an exact mutable queued Job.");
            }
            return true;
        }
        if (pairedDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The paired Video Trim payload must be one Job or a Job array.");
        }
        VideoTrimV1JobSmokeSnapshot?[] snapshots = pairedDocument.RootElement
            .EnumerateArray()
            .Select(global::PhotoViewer.Wpf.MainWindow
                .ReadVideoTrimV1JobForSmoke)
            .ToArray();
        bool exact = snapshots.Length == 6
            && snapshots.Select(snapshot => snapshot?.Status).SequenceEqual(
                ["queued", "running", "succeeded", "failed", "canceled", "deleted"],
                StringComparer.Ordinal)
            && snapshots.All(snapshot => snapshot is
            {
                Claimed: true,
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                FilterKey: "trim",
            })
            && snapshots[0] is { CanCancel: true, CanReorder: true }
            && snapshots[1] is { CanCancel: true }
            && snapshots[2] is { CanUseOutput: true, CanDeleteOutput: true }
            && snapshots[3] is { CanRetry: true, CanDismiss: true }
            && snapshots[4] is { CanRetry: true, CanDismiss: true }
            && snapshots[5] is
            {
                CanRetry: false,
                CanDismiss: true,
                CanDeleteOutput: false,
                VisibleActionKinds: ["dismiss"],
            };
        if (!exact)
        {
            throw new InvalidDataException(
                "The paired SQLite Video Trim projections are not exact across all six states.");
        }
        return true;
    }

    private static string? TryGetVideoTrimV1SmokeArgument(
        string[] args,
        string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
            return null;
        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new InvalidDataException($"{name} requires a value.");
        }
        return Path.GetFullPath(args[index + 1]);
    }

    private static string BuildVideoTrimV1Health(
        JsonElement capability,
        bool ready)
    {
        JsonObject clone = JsonNode.Parse(capability.GetRawText())!.AsObject();
        if (ready)
        {
            JsonObject receipts = clone["receipts"]!.AsObject();
            receipts["receiptSetSha256"] = new string('a', 64);
        }
        var root = new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["videoTrimV1"] = clone,
            },
        };
        return BuildVideoToolsV2FlowReadyHealth(root.ToJsonString());
    }

    private static bool VideoTrimV1SmokeRequestExact(
        string json,
        string sourcePath,
        long sourceSize,
        string sourceSha256,
        string audioPolicy)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.EnumerateObject().Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    new[] { "mediaKind", "operation", "videoTrim" }
                        .Order(StringComparer.Ordinal),
                    StringComparer.Ordinal)
            && root.GetProperty("operation").GetString() == "video"
            && root.GetProperty("mediaKind").GetString() == "video")
        {
            JsonElement trim = root.GetProperty("videoTrim");
            JsonElement source = trim.GetProperty("source");
            return trim.GetProperty("schemaVersion").GetInt32() == 1
                && source.GetProperty("kind").GetString() == "displayed-file"
                && source.GetProperty("path").GetString() == sourcePath
                && source.GetProperty("size").GetInt64() == sourceSize
                && source.GetProperty("sha256").GetString() == sourceSha256
                && trim.GetProperty("selection").GetProperty("startFrame")
                    .GetInt32() == 24
                && trim.GetProperty("selection")
                    .GetProperty("endFrameExclusive").GetInt32() == 72
                && trim.GetProperty("audioPolicy").GetString() == audioPolicy
                && !trim.TryGetProperty("prompt", out _)
                && !trim.TryGetProperty("style", out _)
                && !trim.TryGetProperty("steps", out _)
                && !trim.TryGetProperty("seed", out _)
                && !trim.TryGetProperty("gpuLease", out _);
        }
        return false;
    }

    private static bool VerifyVideoTrimV1SmokeContractVectors(
        string sourcePath,
        long sourceSize,
        DateTime sourceMtimeUtc,
        string sourceSha256)
    {
        var longProbe = new VideoTrimV1SourceProbe(
            "mp4", "h264", "yuv420p", 8, "SDR",
            1920, 1080, 18_000, 60, 1, 300_000, 300, 1,
            1, 1, 0, 1, 60_000, 0,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        if (!VideoTrimV1Contract.TryPlan(
                longProbe,
                600,
                18_000,
                "preserve",
                out VideoTrimV1Plan longPlan)
            || longPlan.SelectedFrameCount != 17_400
            || longPlan.DurationNumerator != 290
            || longPlan.DurationDenominator != 1)
        {
            return false;
        }
        var selector = new VideoEditV2SourceSelector(
            "displayed-file",
            null,
            sourcePath,
            sourceSize,
            new DateTimeOffset(sourceMtimeUtc).ToUnixTimeMilliseconds(),
            sourceSha256);
        return VideoTrimV1Contract.TryPlan(
                longProbe,
                0,
                3,
                "mute",
                out VideoTrimV1Plan mutePlan)
            && global::PhotoViewer.Wpf.MainWindow
                .TryBuildVideoTrimV1RequestForSmoke(
                selector,
                mutePlan,
                out JsonElement muteRequest)
            && VideoTrimV1SmokeRequestExactSelection(
                muteRequest,
                0,
                3,
                "mute");
    }

    private static bool VideoTrimV1SmokeRequestExactSelection(
        JsonElement request,
        int start,
        int end,
        string audioPolicy)
    {
        JsonElement trim = request.GetProperty("videoTrim");
        return trim.GetProperty("selection").GetProperty("startFrame")
                .GetInt32() == start
            && trim.GetProperty("selection").GetProperty("endFrameExclusive")
                .GetInt32() == end
            && trim.GetProperty("audioPolicy").GetString() == audioPolicy;
    }

    private static bool VerifyVideoTrimV1SmokeReaderVectors(
        IReadOnlyCollection<JsonObject> jobs,
        JsonObject malformed,
        JsonObject future,
        JsonObject lifecyclePolicy)
    {
        VideoTrimV1JobSmokeSnapshot? queued = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-queued")));
        VideoTrimV1JobSmokeSnapshot? running = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-running")));
        VideoTrimV1JobSmokeSnapshot? succeeded = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-succeeded")));
        VideoTrimV1JobSmokeSnapshot? failed = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-failed")));
        VideoTrimV1JobSmokeSnapshot? canceled = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-canceled")));
        VideoTrimV1JobSmokeSnapshot? deleted = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(JsonSerializer.SerializeToElement(
                jobs.Single(job => job["id"]?.GetValue<string>()
                    == "trim-fixture-deleted")));
        VideoTrimV1JobSmokeSnapshot? malformedRead = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(malformed));
        VideoTrimV1JobSmokeSnapshot? futureRead = global::PhotoViewer.Wpf.MainWindow
            .ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(future));
        string[] attemptWorkerFields = lifecyclePolicy["attemptWorkerFields"]!
            .AsArray()
            .Select(static node => node!.GetValue<string>())
            .ToArray();
        string[] forbiddenStatuses = lifecyclePolicy["forbiddenStatuses"]!
            .AsArray()
            .Select(static node => node!.GetValue<string>())
            .ToArray();
        JsonObject runningLifecycle = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-running")
            .DeepClone().AsObject();
        runningLifecycle["runId"] = "trim-run-v1";
        runningLifecycle["workerInstanceId"] = "trim-worker-v1";
        runningLifecycle["lastHeartbeatAt"] = runningLifecycle["updatedAt"]!
            .DeepClone();
        runningLifecycle["lastProgressAt"] = runningLifecycle["updatedAt"]!
            .DeepClone();
        runningLifecycle["externalPromptId"] = "trim-prompt-v1";
        runningLifecycle["externalProcessId"] = 321;
        runningLifecycle["diagnostics"] = new JsonObject
        {
            ["warningLevel"] = "slow",
        };
        VideoTrimV1JobSmokeSnapshot? runningLifecycleRead =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningLifecycle));
        bool forbiddenLifecycleProtected = true;
        foreach (string status in forbiddenStatuses)
        {
            JsonObject baseline = jobs.Single(job =>
                    job["id"]?.GetValue<string>() == $"trim-fixture-{status}")
                .DeepClone().AsObject();
            foreach (string field in attemptWorkerFields)
            {
                baseline[field] = field switch
                {
                    "externalProcessId" => JsonValue.Create(321),
                    "diagnostics" => new JsonObject
                    {
                        ["warningLevel"] = "slow",
                    },
                    "lastHeartbeatAt" or "lastProgressAt" =>
                        baseline["updatedAt"]!.DeepClone(),
                    _ => JsonValue.Create($"trim-{field}-v1"),
                };
                VideoTrimV1JobSmokeSnapshot? protectedRead =
                    global::PhotoViewer.Wpf.MainWindow
                        .ReadVideoTrimV1JobForSmoke(
                            JsonSerializer.SerializeToElement(baseline));
                forbiddenLifecycleProtected &= protectedRead is
                {
                    Claimed: true,
                    ExactCurrent: false,
                    ReaderOnly: true,
                    SupportedMutation: false,
                    FilterKey: null,
                    VisibleActionKinds.Length: 0,
                };
                baseline.Remove(field);
            }
        }
        JsonObject compatibleUnknown = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-deleted")
            .DeepClone().AsObject();
        compatibleUnknown["futureCompatibleNote"] = "preserved";
        VideoTrimV1JobSmokeSnapshot? compatibleUnknownRead =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(compatibleUnknown));
        JsonObject privateExecution = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        JsonObject privateSource = privateExecution["videoTrim"]!["source"]!
            .AsObject();
        string privateSourcePath = privateSource["canonicalPath"]!
            .GetValue<string>();
        privateExecution["sourcePath"] = privateSourcePath;
        privateExecution["sourceSignature"] = privateSource["signature"]!
            .DeepClone();
        privateExecution["sourceSha256"] = privateSource["sha256"]!
            .DeepClone();
        VideoTrimV1JobSmokeSnapshot? privateExecutionExact =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(privateExecution));
        VideoTrimV1JobSmokeSnapshot? stableExponentExecution =
            ReadVideoTrimV1RawExecutionMtimeForSmoke(
                privateExecution,
                "1.7875296e12");
        VideoTrimV1JobSmokeSnapshot? lossyExecutionMtime =
            ReadVideoTrimV1RawExecutionMtimeForSmoke(
                privateExecution,
                "1787529600000.0000001");
        privateExecution["sourcePath"] = privateSourcePath.ToLowerInvariant();
        VideoTrimV1JobSmokeSnapshot? privateExecutionCaseDrift =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(privateExecution));
        privateExecution["sourcePath"] = privateSourcePath.Contains('/', StringComparison.Ordinal)
            ? privateSourcePath.Replace('/', '\\')
            : privateSourcePath.Replace('\\', '/');
        VideoTrimV1JobSmokeSnapshot? privateExecutionSlashDrift =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(privateExecution));

        JsonObject queuedOverflow = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        queuedOverflow["queueOrder"] = 2_147_483_648L;
        JsonObject runningProcessOverflow = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-running")
            .DeepClone().AsObject();
        runningProcessOverflow["externalProcessId"] = 2_147_483_648L;
        JsonObject failedLowerCode = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-failed")
            .DeepClone().AsObject();
        failedLowerCode["errorCode"] = "video-trim.failure";
        JsonObject failedSpaceCode = failedLowerCode.DeepClone().AsObject();
        failedSpaceCode["errorCode"] = "video trim failure";
        JsonObject failedControlMessage = failedLowerCode.DeepClone().AsObject();
        failedControlMessage["errorMessage"] = "Synthetic\0failure.";
        JsonObject timestampWithoutMilliseconds = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        timestampWithoutMilliseconds["createdAt"] = "2026-08-24T00:00:00Z";
        timestampWithoutMilliseconds["updatedAt"] = "2026-08-24T00:00:00Z";
        JsonObject timestampWithSevenDigits = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        timestampWithSevenDigits["createdAt"] =
            "2026-08-24T00:00:00.0000000Z";
        timestampWithSevenDigits["updatedAt"] =
            "2026-08-24T00:00:00.0000000Z";
        JsonObject runningRunControl = runningLifecycle.DeepClone().AsObject();
        runningRunControl["runId"] = "trim\0run";
        JsonObject runningWorkerControl = runningLifecycle.DeepClone().AsObject();
        runningWorkerControl["workerInstanceId"] = "trim\nworker";
        JsonObject runningPromptControl = runningLifecycle.DeepClone().AsObject();
        runningPromptControl["externalPromptId"] = "trim\0prompt";
        JsonObject runningDiagnosticsBoundary = runningLifecycle
            .DeepClone().AsObject();
        runningDiagnosticsBoundary["diagnostics"] = new JsonObject
        {
            ["payload"] = new string('x', 32_754),
        };
        JsonObject runningDiagnosticsOverflow = runningLifecycle
            .DeepClone().AsObject();
        runningDiagnosticsOverflow["diagnostics"] = new JsonObject
        {
            ["payload"] = new string('x', 32_755),
        };
        JsonObject sourceIdControl = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        sourceIdControl["sourceId"] = "catalog\0source";
        JsonObject sourceIdOverflow = jobs.Single(job =>
                job["id"]?.GetValue<string>() == "trim-fixture-queued")
            .DeepClone().AsObject();
        sourceIdOverflow["sourceId"] = new string('x', 32_769);
        bool scalarBoundariesExact =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(queuedOverflow)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningProcessOverflow)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(failedLowerCode)) is
                { ExactCurrent: true, ReaderOnly: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(failedSpaceCode)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(failedControlMessage)) is
                { ReaderOnly: true, SupportedMutation: false };
        bool remainingScalarBoundariesExact =
            global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(
                    timestampWithoutMilliseconds)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(timestampWithSevenDigits)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningRunControl)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningWorkerControl)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningPromptControl)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningDiagnosticsBoundary)) is
                { ExactCurrent: true, ReaderOnly: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(runningDiagnosticsOverflow)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(sourceIdControl)) is
                { ReaderOnly: true, SupportedMutation: false }
            && global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
                JsonSerializer.SerializeToElement(sourceIdOverflow)) is
                { ReaderOnly: true, SupportedMutation: false };
        return queued is
            {
                Claimed: true,
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                FilterKey: "trim",
                CanCancel: true,
                CanReorder: true,
            }
            && running is { CanCancel: true }
            && succeeded is
            {
                CanUseOutput: true,
                CanDeleteOutput: true,
            }
            && failed is { CanRetry: true, CanDismiss: true }
            && canceled is { CanRetry: true, CanDismiss: true }
            && deleted is
            {
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                FilterKey: "trim",
                Status: "deleted",
                CanRetry: false,
                CanDismiss: true,
                CanDeleteOutput: false,
                VisibleActionKinds: ["dismiss"],
            }
            && runningLifecycleRead is
            {
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                Status: "running",
            }
            && forbiddenLifecycleProtected
            && compatibleUnknownRead is
            {
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
                Status: "deleted",
            }
            && privateExecutionExact is
            {
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
            }
            && stableExponentExecution is
            {
                ExactCurrent: true,
                ReaderOnly: false,
                SupportedMutation: true,
            }
            && lossyExecutionMtime is
            {
                ReaderOnly: true,
                SupportedMutation: false,
                VisibleActionKinds.Length: 0,
            }
            && privateExecutionCaseDrift is
            {
                ReaderOnly: true,
                SupportedMutation: false,
                VisibleActionKinds.Length: 0,
            }
            && privateExecutionSlashDrift is
            {
                ReaderOnly: true,
                SupportedMutation: false,
                VisibleActionKinds.Length: 0,
            }
            && scalarBoundariesExact
            && remainingScalarBoundariesExact
            && malformedRead is
            {
                Claimed: true,
                ExactCurrent: false,
                ReaderOnly: true,
                SupportedMutation: false,
                FilterKey: null,
                VisibleActionKinds.Length: 0,
            }
            && futureRead is
            {
                Claimed: true,
                ExactCurrent: false,
                ReaderOnly: true,
                SupportedMutation: false,
                FilterKey: null,
                VisibleActionKinds.Length: 0,
            };
    }

    private static VideoTrimV1JobSmokeSnapshot?
        ReadVideoTrimV1RawExecutionMtimeForSmoke(
            JsonObject job,
            string rawMtime)
    {
        string json = job.ToJsonString();
        const string signatureMarker = "\"sourceSignature\":";
        const string mtimeMarker = "\"mtimeMs\":";
        int signatureIndex = json.LastIndexOf(
            signatureMarker,
            StringComparison.Ordinal);
        int valueStart = signatureIndex < 0
            ? -1
            : json.IndexOf(
                mtimeMarker,
                signatureIndex,
                StringComparison.Ordinal);
        if (valueStart < 0)
            throw new InvalidDataException("Private Trim sourceSignature is missing.");
        valueStart += mtimeMarker.Length;
        int valueEnd = valueStart;
        while (valueEnd < json.Length
            && json[valueEnd] is not (',' or '}'))
        {
            valueEnd++;
        }
        string drift = string.Concat(
            json.AsSpan(0, valueStart),
            rawMtime,
            json.AsSpan(valueEnd));
        using JsonDocument document = JsonDocument.Parse(drift);
        return global::PhotoViewer.Wpf.MainWindow.ReadVideoTrimV1JobForSmoke(
            document.RootElement);
    }

    private static JsonObject BuildVideoTrimV1StagedInventoryJob(
        JsonObject fixtureSucceeded,
        string originalPath,
        string smokeRoot,
        string outputRoot)
    {
        JsonObject job = fixtureSucceeded.DeepClone().AsObject();
        string stagedPath = Path.Combine(smokeRoot, "staged-owned.mp4");
        string outputPath = Path.Combine(
            outputRoot,
            "Videos",
            "2026-08-24",
            "trim-staged-succeeded.mp4");
        File.Copy(originalPath, stagedPath, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        WriteIsoBmffSmokeVideo(outputPath);
        var originalInfo = new FileInfo(originalPath);
        var stagedInfo = new FileInfo(stagedPath);
        JsonObject trim = job["videoTrim"]!.AsObject();
        JsonObject oldSource = trim["source"]!.AsObject();
        trim["source"] = new JsonObject
        {
            ["kind"] = "staged-displayed-file",
            ["originalCanonicalPath"] = originalPath,
            ["originalSignature"] = new JsonObject
            {
                ["size"] = originalInfo.Length,
                ["mtimeMs"] = new DateTimeOffset(originalInfo.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds(),
            },
            ["originalSha256"] = new string('d', 64),
            ["stagingCanonicalPath"] = stagedPath,
            ["stagingSignature"] = new JsonObject
            {
                ["size"] = stagedInfo.Length,
                ["mtimeMs"] = new DateTimeOffset(stagedInfo.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds(),
            },
            ["stagingSha256"] = new string('d', 64),
            ["probe"] = oldSource["probe"]!.DeepClone(),
            ["probeDigest"] = oldSource["probeDigest"]!.DeepClone(),
            ["sourceIdentityDigest"] = oldSource["sourceIdentityDigest"]!
                .DeepClone(),
            ["stagingOwnershipDigest"] = new string('c', 64),
        };
        trim["requested"]!["source"] = new JsonObject
        {
            ["kind"] = "displayed-file",
        };
        job.Remove("sourceVideoJobId");
        job["id"] = VideoTrimV1SmokeStagedJobId;
        job["sourceId"] = originalPath;
        job["outputPath"] = outputPath;
        job["outputBytes"] = new FileInfo(outputPath).Length;
        using JsonDocument trimDocument = JsonDocument.Parse(trim.ToJsonString());
        job["presetHash"] = global::PhotoViewer.Wpf.MainWindow
            .ComputeVideoTrimV1PresetHashForSmoke(trimDocument.RootElement);
        return job;
    }

    private static bool TryHandleVideoTrimV1SmokeMutation(
        HttpRequestMessage request,
        string route,
        List<JsonObject> jobs,
        List<string> routes,
        out HttpResponseMessage response)
    {
        response = null!;
        const string prefix = "/api/enhance/jobs/";
        if (!route.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        string[] parts = route[prefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
            return false;
        string id = Uri.UnescapeDataString(parts[0]);
        JsonObject? job = jobs.FirstOrDefault(candidate =>
            candidate["id"]?.GetValue<string>() == id);
        if (job is null)
        {
            response = VideoEditV2SmokeJsonResponse(
                HttpStatusCode.NotFound,
                "{\"error\":\"missing job\"}");
            return true;
        }
        routes.Add($"{request.Method.Method} {route}");
        string? action = parts.Length == 2 ? parts[1] : null;
        if (request.Method == HttpMethod.Post && action == "cancel")
        {
            job["status"] = "canceled";
            job["cancelRequested"] = true;
            job["progress"] = Math.Clamp(job["progress"]?.GetValue<int>() ?? 0, 0, 99);
            job.Remove("queueOrder");
            job["startedAt"] ??= "2026-08-24T00:00:01.000Z";
            job["finishedAt"] = "2026-08-24T00:00:03.000Z";
            job["updatedAt"] = "2026-08-24T00:00:03.000Z";
            response = VideoEditV2SmokeJsonResponse(
                HttpStatusCode.Accepted,
                "{\"accepted\":true}");
            return true;
        }
        if (request.Method == HttpMethod.Post && action == "retry")
        {
            job["status"] = "queued";
            job["progress"] = 0;
            job["cancelRequested"] = false;
            job["queueOrder"] = 10;
            job["updatedAt"] = job["createdAt"]!.DeepClone();
            job.Remove("startedAt");
            job.Remove("finishedAt");
            job.Remove("errorCode");
            job.Remove("errorMessage");
            return SetVideoTrimV1SmokeMutationResponse(
                BuildVideoToolsV2FlowAcceptedResponse(request, id),
                out response);
        }
        if (request.Method == HttpMethod.Delete && action == "output")
        {
            string? outputPath = job["outputPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
                File.Delete(outputPath);
            job["status"] = "deleted";
            job["progress"] = 100;
            job["cancelRequested"] = false;
            job["startedAt"] ??= "2026-08-24T00:00:01.000Z";
            job["finishedAt"] = "2026-08-24T00:00:06.000Z";
            job["updatedAt"] = "2026-08-24T00:00:06.000Z";
            foreach (string field in new[]
            {
                "queueOrder", "runId", "workerInstanceId",
                "lastHeartbeatAt", "lastProgressAt", "externalPromptId",
                "externalProcessId", "outputPath", "outputSha256",
                "outputBytes", "errorCode", "errorMessage", "diagnostics",
            })
            {
                job.Remove(field);
            }
            response = VideoEditV2SmokeJsonResponse(
                HttpStatusCode.OK,
                "{\"deleted\":true}");
            return true;
        }
        if (request.Method == HttpMethod.Delete && action is null)
        {
            jobs.Remove(job);
            response = VideoEditV2SmokeJsonResponse(
                HttpStatusCode.OK,
                "{\"dismissed\":true}");
            return true;
        }
        response = VideoEditV2SmokeJsonResponse(
            HttpStatusCode.NotFound,
            "{\"error\":\"unexpected mutation\"}");
        return true;
    }

    private static bool SetVideoTrimV1SmokeMutationResponse(
        HttpResponseMessage value,
        out HttpResponseMessage response)
    {
        response = value;
        return true;
    }
}

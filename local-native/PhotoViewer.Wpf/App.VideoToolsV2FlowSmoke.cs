using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private async void CaptureVideoToolsV2FlowSmoke(
        string resultPath,
        string[] arguments)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string fixturePath = RequireVideoToolsV2ReaderArgument(
            arguments,
            "--fixture");
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-tools-v2-flow-")
            .FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string outputRoot = Path.Combine(storeRoot, "outputs");
        string sourcePath = Path.Combine(sourceRoot, "flow-source.mp4");
        string unsupportedPath = Path.Combine(sourceRoot, "not-video.txt");
        string secondPath = Path.Combine(sourceRoot, "second.mp4");
        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = Path.Combine(
                storeRoot,
                "state.json"),
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(
                storeRoot,
                "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(
                storeRoot,
                "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(
                storeRoot,
                "recent-folders.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(
                storeRoot,
                "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(
                storeRoot,
                "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(
                storeRoot,
                "search-history.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(
                storeRoot,
                "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(
                storeRoot,
                "enhance",
                "jobs.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = outputRoot,
        };
        Dictionary<string, string?> previousEnvironment = environment.Keys
            .ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        MainWindow? window = null;
        bool ok = false;
        object result = new { ok = false, message = "Flow smoke did not run." };

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            WriteIsoBmffSmokeVideo(secondPath);
            File.WriteAllText(unsupportedPath, "not a video", new UTF8Encoding(false));
            string sourceFingerprint = FingerprintVideoEditV2File(sourcePath);
            string sourceSha256 = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(sourcePath)));
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            using JsonDocument fixtureDocument = JsonDocument.Parse(
                File.ReadAllText(fixturePath));
            JsonElement fixtures = fixtureDocument.RootElement
                .GetProperty("readerFixtures");
            JsonElement editFixture = fixtures.GetProperty("edit");
            JsonElement finishFixture = fixtures.GetProperty("finish");
            string editReadyHealth = BuildVideoToolsV2FlowReadyHealth(
                BuildVideoEditV2SmokeReadyHealthResponse());
            string finishReadyHealth = BuildVideoToolsV2FlowReadyHealth(
                BuildVideoFinishV2ReadyHealth(fixturePath));

            var transientActions = new List<string>();
            var enqueueKinds = new List<string>();
            var enqueueBodies = new List<string>();
            var mutationRoutes = new List<string>();
            var jobs = new List<JsonObject>();
            int healthReads = 0;
            int jobsReads = 0;
            int wakeRequests = 0;
            int unexpectedRequests = 0;
            bool loopbackOnly = true;
            int authenticatedRoundTrips = 0;
            bool authenticatedInnerExact = true;
            string? capturedSelector = null;
            string healthLane = "edit";

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            window.SetCanonicalPathResolverForSmoke(Path.GetFullPath);
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
                flowSender = async (request, token) =>
            {
                string route = request.RequestUri?.AbsolutePath ?? "";
                loopbackOnly &= request.RequestUri is { IsLoopback: true };
                if (request.Method == HttpMethod.Get
                    && route == "/api/enhance/health")
                {
                    healthReads++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.OK,
                        healthLane == "edit"
                            ? editReadyHealth
                            : finishReadyHealth);
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
                    && route == "/api/enhance/video-prompts/v2/edit/compile")
                {
                    string json = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;
                    if (!TryGetVideoEditV2SmokeAction(root, out string action))
                    {
                        unexpectedRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.BadRequest,
                            "{\"error\":\"bad action\"}");
                    }
                    transientActions.Add(action);
                    if (action == "probe")
                    {
                        JsonElement selector = root.GetProperty("source");
                        capturedSelector = selector.GetRawText();
                        bool exact = selector.GetProperty("kind").GetString()
                                == "displayed-file"
                            && selector.GetProperty("path").GetString()
                                == sourcePath
                            && selector.GetProperty("sha256").GetString()
                                == sourceSha256;
                        if (!exact)
                            unexpectedRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            exact ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                            exact
                                ? BuildVideoEditV2SmokeProbeResponse()
                                : "{\"error\":\"bad source\"}");
                    }
                    if (action == "preview")
                    {
                        int start = 0;
                        int end = 0;
                        bool exact = root.GetProperty("source").GetRawText()
                                == capturedSelector
                            && TryGetVideoEditV2SmokeSelection(
                                root,
                                out start,
                                out end);
                        if (!exact)
                            unexpectedRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            exact ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                            exact
                                ? BuildVideoEditV2SmokePreviewResponse(start, end)
                                : "{\"error\":\"bad preview\"}");
                    }
                    if (action == "compile")
                    {
                        string backendPrompt =
                            VideoEditV2TransientContract.OfficialV2VSystemPrompt
                            + "Change only the clothing color to blue. Preserve the subject, background, timing, and camera. "
                            + VideoEditV2TransientContract.OfficialContinuitySentence;
                        const string summaryJa =
                            "人物・背景・動き・カメラを保ち、服の色だけを青へ変えます。";
                        const string revision =
                            VideoEditV2TransientContract.OfficialPromptCompilerRevision;
                        string digest = "";
                        VideoEditV2RendererSidecar renderer = null!;
                        bool exact = root.GetProperty("source").GetRawText()
                                == capturedSelector
                            && root.GetProperty("instructionJa").GetString()
                                is { Length: > 0 }
                            && VideoEditV2TransientContract
                                .TryCreateOfficialRendererSidecarForSmoke(
                                    backendPrompt,
                                    "v2v",
                                    out renderer)
                            && VideoEditV2TransientContract
                                .TryComputeContextDigestFromCompileRequestForSmoke(
                                    root,
                                    backendPrompt,
                                    summaryJa,
                                    revision,
                                    renderer,
                                    out digest);
                        if (!exact)
                            unexpectedRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            exact ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                            exact
                                ? JsonSerializer.Serialize(new
                                {
                                    action = "compile",
                                    candidate = new
                                    {
                                        backendPrompt,
                                        summaryJa,
                                        compilerRevision = revision,
                                        contextDigest = digest,
                                        renderer = new
                                        {
                                            taskType = renderer.TaskType,
                                            guidanceMode = renderer.GuidanceMode,
                                            promptCompilerRevision =
                                                renderer.PromptCompilerRevision,
                                            rendererPromptSha256 =
                                                renderer.RendererPromptSha256,
                                        },
                                    },
                                })
                                : "{\"error\":\"bad compile\"}");
                    }
                    unexpectedRequests++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.NotFound,
                        "{\"error\":\"unexpected action\"}");
                }
                if (request.Method == HttpMethod.Post
                    && route == "/api/enhance/jobs")
                {
                    string body = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    using JsonDocument document = JsonDocument.Parse(body);
                    string kind = document.RootElement
                        .GetProperty("videoTools")
                        .GetProperty("kind")
                        .GetString() ?? "";
                    enqueueKinds.Add(kind);
                    enqueueBodies.Add(body);
                    return BuildVideoToolsV2FlowAcceptedResponse(
                        request,
                        $"flow-{kind}-{enqueueKinds.Count}");
                }
                if (request.Method == HttpMethod.Post
                    && route == "/api/enhance/inbox/wake")
                {
                    string body = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    if (body.Length != 0)
                        unexpectedRequests++;
                    wakeRequests++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.Accepted,
                        "{\"accepted\":true}");
                }
                if (TryHandleVideoToolsV2FlowMutation(
                        request,
                        route,
                        jobs,
                        mutationRoutes,
                        out HttpResponseMessage? mutationResponse))
                {
                    return mutationResponse;
                }
                unexpectedRequests++;
                return VideoEditV2SmokeJsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"error\":\"unexpected route\"}");
            };
            window.ConfigureEnhancementCompanionAutoStartForSmoke(
                async (request, token) =>
                {
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method == HttpMethod.Get
                        && route == "/api/enhance/identity"
                        && request.Headers.TryGetValues(
                            "X-Aibos-Companion-Challenge",
                            out IEnumerable<string>? challenges))
                    {
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.OK,
                            JsonSerializer.Serialize(
                                window.EnhancementCompanionIdentityPayloadForSmoke(
                                    challenges.Single())));
                    }
                    if (request.Method == HttpMethod.Post
                        && route == "/api/enhance/secure")
                    {
                        EnhancementCompanionSecureRequestSmokeSnapshot? decoded =
                            await window.DecodeEnhancementCompanionSecureRequestForSmokeAsync(
                                request,
                                token);
                        bool exact = decoded is not null
                            && decoded.Method == "GET"
                            && decoded.PathAndQuery.EndsWith(
                                "/api/enhance/health",
                                StringComparison.Ordinal)
                            && string.IsNullOrEmpty(decoded.BodyJson);
                        authenticatedInnerExact &= exact;
                        if (!exact)
                        {
                            return VideoEditV2SmokeJsonResponse(
                                HttpStatusCode.BadRequest,
                                "{\"error\":\"invalid secure request\"}");
                        }
                        authenticatedRoundTrips++;
                        using JsonDocument payload = JsonDocument.Parse(
                            editReadyHealth);
                        return window.EnhancementCompanionSecureResponseForSmoke(
                            request,
                            200,
                            payload.RootElement.Clone());
                    }
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.NotFound,
                        "{\"error\":\"unexpected auth route\"}");
                },
                static _ => (true, ""));

            window.Show();
            Task flowTask = await window.Dispatcher.InvokeAsync(async () =>
            {
                bool identityReady = await window
                    .EnsureEnhancementCompanionForExplicitActionForSmokeAsync();
                MainWindow.IdempotentEnhancementMutationSmokeSnapshot
                    authenticatedHealth = await window
                        .SendIdempotentEnhancementMutationForSmokeAsync(
                            HttpMethod.Get,
                            "api/enhance/health");
                bool authenticatedFake = authenticatedRoundTrips >= 1
                    && authenticatedInnerExact;
                window.ConfigureModalEnhancementForSmoke(flowSender);

                ExternalVideoDropSmokeSnapshot multiple =
                    await window.DropExternalVideoForSmokeAsync(
                        [sourcePath, secondPath]);
                ExternalVideoDropSmokeSnapshot unsupported =
                    await window.DropExternalVideoForSmokeAsync(
                        [unsupportedPath]);
                ExternalVideoDropSmokeSnapshot dropped =
                    await window.DropExternalVideoForSmokeAsync([sourcePath]);
                bool dropExact = !multiple.Accepted
                    && !unsupported.Accepted
                    && dropped.Accepted
                    && dropped.ModalVisible
                    && dropped.ShowingVideo
                    && dropped.SourcePinned
                    && window.ExternalVideoDropSessionActiveForSmoke
                    && window.VideoEditV2EntryVisibleForSmoke
                    && window.VideoFinishV2EntryVisibleForSmoke
                    && window.VideoEditV2ExternalContextEntryForSmoke
                    && window.VideoFinishV2ExternalContextEntryForSmoke;

                int passiveHttpBefore = healthReads + jobsReads
                    + enqueueKinds.Count + transientActions.Count
                    + mutationRoutes.Count + wakeRequests;
                string storesBeforeEdit = FingerprintVideoEditV2Tree(storeRoot);
                bool editOpened = window.OpenVideoEditV2ForSmoke();
                bool editPassive = editOpened
                    && passiveHttpBefore == healthReads + jobsReads
                        + enqueueKinds.Count + transientActions.Count
                        + mutationRoutes.Count + wakeRequests
                    && storesBeforeEdit == FingerprintVideoEditV2Tree(storeRoot);
                bool framesLoaded = await window.LoadVideoEditV2FramesForSmokeAsync()
                    && window.VideoEditV2PreviewFramesForSmoke.SequenceEqual(
                        ["f 0", "f 59", "f 119"],
                        StringComparer.Ordinal);
                window.SetVideoEditV2InstructionForSmoke(
                    "人物と背景と動きを保ち、服の色だけを青に変える");
                bool compiledReview = await window.CompileVideoEditV2ForSmokeAsync()
                    && window.VideoEditV2ReviewVisibleForSmoke
                    && window.ApplyVideoEditV2CandidateApprovalForSmoke();
                bool editReady = compiledReview
                    && await RefreshVideoToolsV2FlowEditReadinessAsync(window);
                int editEnqueueBeforeStart = enqueueKinds.Count;
                if (editReady)
                {
                    window.ModalVideoEditV2StartButton.RaiseEvent(
                        new System.Windows.RoutedEventArgs(
                            System.Windows.Controls.Button.ClickEvent,
                            window.ModalVideoEditV2StartButton));
                }
                for (int attempt = 0;
                     attempt < 200
                        && enqueueKinds.Count == editEnqueueBeforeStart;
                     attempt++)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    await Task.Delay(10);
                }
                bool editStarted = enqueueKinds.Count
                    == editEnqueueBeforeStart + 1;
                bool editDurable = editStarted
                    && enqueueKinds.SequenceEqual(["edit"], StringComparer.Ordinal)
                    && VideoToolsV2FlowEditRequestExact(enqueueBodies[0]);

                bool reopenedEdit = window.OpenVideoEditV2ForSmoke();
                bool skipFrames = reopenedEdit
                    && await window.LoadVideoEditV2FramesForSmokeAsync();
                window.SetVideoEditV2InstructionForSmoke(
                    "人物はそのまま、背景だけを夕方にする");
                window.SetVideoEditV2SkipReviewForSmoke(true);
                int editCountBeforeSkip = enqueueKinds.Count;
                bool skipCompiled = skipFrames
                    && await window.CompileVideoEditV2ForSmokeAsync();
                for (int attempt = 0;
                     attempt < 20 && enqueueKinds.Count == editCountBeforeSkip;
                     attempt++)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    await Task.Delay(10);
                }
                bool skipStarted = skipCompiled
                    && enqueueKinds.Count == editCountBeforeSkip + 1
                    && enqueueKinds[^1] == "edit"
                    && !window.VideoEditV2SkipReviewCheckedForSmoke;

                window.OpenVideoEditV2ForSmoke();
                window.ArmVideoEditV2StyleInvalidationForSmoke();
                bool styleSaved = window.SaveVideoEditV2StyleForSmoke(
                    "Flow style",
                    "人物とカメラを保ち、色だけを調整する",
                    "preserve",
                    "balanced",
                    "standard",
                    24,
                    "source-faithful");
                bool styleApplied = styleSaved
                    && window.ApplyVideoEditV2StyleForSmoke("Flow style")
                    && window.VideoEditV2CandidateStaleForStyleSmoke
                    && window.VideoEditV2ReadinessStaleForStyleSmoke;
                window.CloseVideoEditV2ForSmoke(stale: false);

                window.SetVideoToolsV2DefaultsForSmoke(
                    "preserve",
                    "balanced",
                    "standard",
                    24,
                    skipReview: false,
                    finishMode: "standard",
                    finishScale: 2);
                healthLane = "finish";
                string storesBeforeFinish = FingerprintVideoEditV2Tree(storeRoot);
                int finishPassiveBefore = healthReads + enqueueKinds.Count
                    + transientActions.Count;
                bool finishOpened = window.OpenVideoFinishV2ForSmoke();
                bool finishPassive = finishOpened
                    && window.VideoFinishV2ModeForSmoke == "standard"
                    && window.VideoFinishV2ScaleForSmoke == 2
                    && finishPassiveBefore == healthReads + enqueueKinds.Count
                        + transientActions.Count
                    && storesBeforeFinish == FingerprintVideoEditV2Tree(storeRoot);
                bool finishProbed = await window.ProbeVideoFinishV2ForSmokeAsync();
                bool finishReady = finishProbed
                    && await window.RefreshVideoFinishV2ReadinessForSmokeAsync();
                bool finishStarted = finishReady
                    && await window.StartVideoFinishV2ForSmokeAsync();
                bool finishDurable = finishStarted
                    && enqueueKinds.Count(static kind => kind == "finish") == 1
                    && VideoToolsV2FlowFinishRequestExact(enqueueBodies[^1]);

                bool qualityOpened = window.OpenVideoFinishV2ForSmoke();
                bool qualityProbed = qualityOpened
                    && await window.ProbeVideoFinishV2ForSmokeAsync();
                bool qualitySelected = qualityProbed
                    && window.SetVideoFinishV2ModeAndScaleForSmoke(
                        "quality",
                        4);
                int enqueueBeforeQuality = enqueueKinds.Count;
                bool qualityNoFallback = !qualitySelected
                    && !await window.RefreshVideoFinishV2ReadinessForSmokeAsync()
                    && !window.VideoFinishV2StartEnabledForSmoke
                    && !await window.StartVideoFinishV2ForSmokeAsync()
                    && enqueueKinds.Count == enqueueBeforeQuality
                    && window.VideoFinishV2ModeForSmoke == "quality"
                    && window.VideoFinishV2ScaleForSmoke == 4;
                window.CloseVideoFinishV2ForSmoke(stale: false);

                MainWindow.IdempotentEnhancementMutationSmokeSnapshot wake =
                    await window.SendIdempotentEnhancementMutationForSmokeAsync(
                        HttpMethod.Post,
                        "api/enhance/inbox/wake");
                bool bodylessWake = wake.Ok && wakeRequests == 1;

                VideoToolsV2FlowJobFixture jobFixture =
                    BuildVideoToolsV2FlowJobs(
                        smokeRoot,
                        outputRoot,
                        editFixture,
                        finishFixture);
                jobs.AddRange(jobFixture.Jobs.Select(static job =>
                    job.DeepClone().AsObject()));
                bool deliveryIdentity = jobFixture.DeliveryIdentity;

                int passiveEnqueueBeforeJobs = enqueueKinds.Count;
                int passiveWakeBeforeJobs = wakeRequests;
                await window.OpenEnhancementJobsForSmokeAsync();
                window.SelectEnhancementJobsVideoKindFilterForSmoke("edit");
                healthLane = "edit";
                EnhancementJobsWorkspaceSmokeSnapshot editJobs =
                    window.EnhancementJobsWorkspaceForSmoke();
                bool editFilter = editJobs.Visible
                    && editJobs.VisibleIds.Contains(
                        jobFixture.EditSucceededId,
                        StringComparer.Ordinal)
                    && editJobs.VisibleIds.Contains(
                        jobFixture.EditRunningId,
                        StringComparer.Ordinal)
                    && editJobs.VisibleOperationLabels.All(label =>
                        label.Contains("AI動画編集", StringComparison.Ordinal));
                window.SelectEnhancementJobsVideoKindFilterForSmoke("finish");
                EnhancementJobsWorkspaceSmokeSnapshot finishJobs =
                    window.EnhancementJobsWorkspaceForSmoke();
                bool finishFilter = finishJobs.VisibleIds.Contains(
                        jobFixture.FinishSucceededId,
                        StringComparer.Ordinal)
                    && finishJobs.VisibleOperationLabels.All(label =>
                        label.Contains("AI動画高画質化", StringComparison.Ordinal));
                bool passiveJobs = enqueueKinds.Count == passiveEnqueueBeforeJobs
                    && wakeRequests == passiveWakeBeforeJobs;

                using JsonDocument exactInventory = JsonDocument.Parse(
                    "[" + string.Join(
                        ',',
                        jobFixture.InventoryJobs.Select(static job =>
                            job.ToJsonString())) + "]");
                string[] inventoryRoots = window
                    .ResolveVideoToolsV2ManagedInventoryForSmoke(
                        exactInventory.RootElement,
                        out string[] inventoryKinds,
                        out string[] inventoryOutputs,
                        out string[] inventoryLabels);
                bool inventoryExact = inventoryRoots.Length == 3
                    && inventoryKinds.Contains("edit", StringComparer.Ordinal)
                    && inventoryKinds.Contains("finish", StringComparer.Ordinal)
                    && inventoryOutputs.Contains(
                        jobFixture.EditOutput,
                        StringComparer.OrdinalIgnoreCase)
                    && inventoryOutputs.Contains(
                        jobFixture.FinishOutput,
                        StringComparer.OrdinalIgnoreCase)
                    && inventoryLabels.Contains("AI編集 1/1", StringComparer.Ordinal)
                    && inventoryLabels.Any(label => label.StartsWith(
                        "AI高画質化 ",
                        StringComparison.Ordinal));
                bool revealed = window.RevealResolvedVideoToolsV2OutputForSmoke(
                    jobFixture.FinishSucceededId,
                    out string explorer,
                    out string[] explorerArguments)
                    && explorer.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                    && explorerArguments.SequenceEqual(
                        [$"/select,{jobFixture.FinishOutput}"],
                        StringComparer.Ordinal);

                window.SelectEnhancementJobsVideoKindFilterForSmoke("edit");
                bool queuedCanceled = await window
                    .CancelEnhancementJobForSmokeAsync(jobFixture.EditQueuedId);
                bool runningCanceled = await window
                    .CancelEnhancementJobForSmokeAsync(jobFixture.EditRunningId);
                bool failedRetried = await window
                    .RetryEnhancementJobForSmokeAsync(jobFixture.EditFailedId);
                bool terminalDismissed = await window
                    .DismissEnhancementJobForSmokeAsync(jobFixture.EditTerminalId);
                int deleteRequestsBefore = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                bool protectedDeleteAttempt = await window
                    .DeleteEnhancementJobOutputForSmokeAsync(
                        jobFixture.EditSucceededId);
                int deleteRequestsProtected = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                jobs.RemoveAll(job => string.Equals(
                    job["id"]?.GetValue<string>(),
                    jobFixture.DependentId,
                    StringComparison.Ordinal));
                await window.RefreshEnhancementJobsForSmokeAsync();
                bool deleteReleased = await window
                    .DeleteEnhancementJobOutputForSmokeAsync(
                        jobFixture.EditSucceededId);
                int deleteRequestsAfter = mutationRoutes.Count(route =>
                    route.EndsWith("/output", StringComparison.Ordinal));
                bool jobsActions = queuedCanceled
                    && runningCanceled
                    && failedRetried
                    && terminalDismissed
                    && protectedDeleteAttempt
                    && deleteRequestsProtected == deleteRequestsBefore
                    && deleteReleased
                    && deleteRequestsAfter == deleteRequestsBefore + 1;

                bool futureReadOnly = global::PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        jobFixture.FutureJob)
                    is
                    {
                        ExactCurrentVideoToolsV2: false,
                        ReaderOnly: true,
                        SupportedMutation: false,
                        CanCancel: false,
                        CanRetry: false,
                        CanDismiss: false,
                        CanDeleteOutput: false,
                    };
                window.CloseEnhancementJobsForSmoke();

                bool sourceUntouched = sourceFingerprint
                    == FingerprintVideoEditV2File(sourcePath);
                bool exactNetwork = loopbackOnly
                    && authenticatedFake
                    && unexpectedRequests == 0
                    && transientActions.Count(action => action == "compile") >= 2
                    && enqueueKinds.Count(kind => kind == "edit") == 2
                    && enqueueKinds.Count(kind => kind == "finish") == 1;
                ok = dropExact
                    && editPassive
                    && framesLoaded
                    && compiledReview
                    && editDurable
                    && skipStarted
                    && styleApplied
                    && finishPassive
                    && finishDurable
                    && qualityNoFallback
                    && bodylessWake
                    && deliveryIdentity
                    && editFilter
                    && finishFilter
                    && passiveJobs
                    && inventoryExact
                    && revealed
                    && jobsActions
                    && futureReadOnly
                    && sourceUntouched
                    && exactNetwork;
                result = new
                {
                    ok,
                    dropExact,
                    editPassive,
                    framesLoaded,
                    compiledReview,
                    editDurable,
                    skipStarted,
                    styleApplied,
                    finishPassive,
                    finishDurable,
                    qualityNoFallback,
                    bodylessWake,
                    deliveryIdentity,
                    editFilter,
                    finishFilter,
                    passiveJobs,
                    inventoryExact,
                    revealed,
                    jobsActions,
                    futureReadOnly,
                    sourceUntouched,
                    exactNetwork,
                    authenticatedFake,
                    authenticatedRoundTrips,
                    transientActions,
                    enqueueKinds,
                    healthReads,
                    jobsReads,
                    wakeRequests,
                    mutationRoutes,
                    unexpectedRequests,
                };
            });
            await flowTask;
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
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

    private static string BuildVideoToolsV2FlowReadyHealth(string healthJson)
    {
        JsonObject root = JsonNode.Parse(healthJson)!
            .AsObject();
        JsonObject capabilities = root["capabilities"]!.AsObject();
        capabilities["durableEnqueueInboxV1"] = new JsonObject
        {
            ["ready"] = true,
            ["protocolVersion"] = EnhancementEnqueueInboxStore.ProtocolVersion,
            ["backendGeneration"] = EnhancementEnqueueInboxStore.BackendGeneration,
        };
        return root.ToJsonString();
    }

    private static HttpResponseMessage BuildVideoToolsV2FlowAcceptedResponse(
        HttpRequestMessage request,
        string jobId)
    {
        string requestId = request.Headers.TryGetValues(
                "Idempotency-Key",
                out IEnumerable<string>? values)
            ? values.Single()
            : "missing-request-id";
        return VideoEditV2SmokeJsonResponse(
            HttpStatusCode.Accepted,
            JsonSerializer.Serialize(new
            {
                job = new { id = jobId },
                receipt = new
                {
                    idempotencyKey = requestId,
                    jobId,
                },
            }));
    }

    private static string BuildVideoToolsV2FlowJobsResponse(
        IReadOnlyCollection<JsonObject> jobs)
        => "{\"jobs\":[" + string.Join(
            ',',
            jobs.Select(static job => job.ToJsonString())) + "]}";

    private static bool TryHandleVideoToolsV2FlowMutation(
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
        string suffix = route[prefix.Length..];
        string[] parts = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
            return false;
        string id = Uri.UnescapeDataString(parts[0]);
        JsonObject? job = jobs.FirstOrDefault(candidate => string.Equals(
            candidate["id"]?.GetValue<string>(),
            id,
            StringComparison.Ordinal));
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
            response = VideoEditV2SmokeJsonResponse(
                HttpStatusCode.Accepted,
                "{\"accepted\":true}");
            return true;
        }
        if (request.Method == HttpMethod.Post && action == "retry")
        {
            job["status"] = "queued";
            job["progress"] = 0;
            response = BuildVideoToolsV2FlowAcceptedResponse(request, id);
            return true;
        }
        if (request.Method == HttpMethod.Delete && action == "output")
        {
            string? outputPath = job["outputPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(outputPath)
                && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            job["outputPath"] = null;
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

    private static bool VideoToolsV2FlowEditRequestExact(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement tools = root.GetProperty("videoTools");
        JsonElement selected = tools.GetProperty("selection");
        JsonElement compiled = tools.GetProperty("compiled");
        return tools.GetProperty("schemaVersion").GetInt32() == 2
            && tools.GetProperty("kind").GetString() == "edit"
            && selected.GetProperty("startFrame").GetInt32() == 0
            && selected.GetProperty("endFrameExclusive").GetInt32() == 120
            && compiled.GetProperty("summaryJa").GetString() is { Length: > 0 }
            && !tools.TryGetProperty("mode", out _)
            && !tools.TryGetProperty("scale", out _);
    }

    private static bool VideoToolsV2FlowFinishRequestExact(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement tools = document.RootElement.GetProperty("videoTools");
        return tools.GetProperty("schemaVersion").GetInt32() == 2
            && tools.GetProperty("kind").GetString() == "finish"
            && tools.GetProperty("mode").GetString() == "standard"
            && tools.GetProperty("scale").GetInt32() == 2
            && !tools.TryGetProperty("compiled", out _)
            && !tools.TryGetProperty("steps", out _)
            && !tools.TryGetProperty("style", out _);
    }

    private static async Task<bool>
        RefreshVideoToolsV2FlowEditReadinessAsync(MainWindow window)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
                "RefreshModalVideoEditV2WriterCapabilityAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(MainWindow),
                "RefreshModalVideoEditV2WriterCapabilityAsync");
        return method.Invoke(window, null) is Task<bool> task
            && await task;
    }

    private static VideoToolsV2FlowJobFixture BuildVideoToolsV2FlowJobs(
        string smokeRoot,
        string outputRoot,
        JsonElement editFixture,
        JsonElement finishFixture)
    {
        string videosRoot = Path.Combine(outputRoot, "Videos", "2026-08-24");
        string stagingRoot = Path.Combine(smokeRoot, "staging");
        Directory.CreateDirectory(videosRoot);
        Directory.CreateDirectory(stagingRoot);
        string originalPath = Path.Combine(smokeRoot, "original.mp4");
        string stagingPath = Path.Combine(stagingRoot, "source.mp4");
        string rootOutput = Path.Combine(videosRoot, "root.mp4");
        string editOutput = Path.Combine(videosRoot, "edit.mp4");
        string finishOutput = Path.Combine(videosRoot, "finish.mp4");
        foreach (string path in new[]
                 { originalPath, stagingPath, rootOutput, editOutput, finishOutput })
        {
            WriteIsoBmffSmokeVideo(path);
        }

        const string rootId = "41000000-0000-4000-8000-000000000001";
        const string editId = "41000000-0000-4000-8000-000000000002";
        const string finishId = "41000000-0000-4000-8000-000000000003";
        using JsonDocument root = CreateVideoToolsV2WorkspaceJob(
            finishFixture,
            rootId,
            "succeeded");
        JsonObject rootJob = JsonNode.Parse(root.RootElement.GetRawText())!
            .AsObject();
        rootOutput = MaterializeVideoToolsV2FlowOutput(rootJob, videosRoot);

        using JsonDocument edit = CreateVideoToolsV2WorkspaceJob(
            editFixture,
            editId,
            "succeeded",
            video =>
            {
                video["source"]!["producerJobId"] = rootId;
                video["source"]!["canonicalPath"] = rootOutput;
                ApplyInventorySourceSignature(
                    video["source"]!["signature"]!.AsObject(),
                    rootOutput);
                video["requested"]!["source"]!["sourceVideoJobId"] = rootId;
            },
            job => job["sourceVideoJobId"] = rootId,
            refreshPresetHash: true);
        JsonObject editJob = JsonNode.Parse(edit.RootElement.GetRawText())!
            .AsObject();
        editOutput = MaterializeVideoToolsV2FlowOutput(editJob, videosRoot);

        using JsonDocument finish = CreateVideoToolsV2WorkspaceJob(
            finishFixture,
            finishId,
            "succeeded",
            video => ApplyVideoToolsV2FlowManagedSource(
                video,
                editId,
                editOutput),
            job => job["sourceVideoJobId"] = editId,
            refreshPresetHash: true);
        JsonObject finishJob = JsonNode.Parse(finish.RootElement.GetRawText())!
            .AsObject();
        finishOutput = MaterializeVideoToolsV2FlowOutput(
            finishJob,
            videosRoot);
        const string queuedId = "42000000-0000-4000-8000-000000000001";
        const string runningId = "42000000-0000-4000-8000-000000000002";
        const string failedId = "42000000-0000-4000-8000-000000000003";
        const string terminalId = "42000000-0000-4000-8000-000000000004";
        using JsonDocument queued = CreateVideoToolsV2WorkspaceJob(
            editFixture,
            queuedId,
            "queued");
        using JsonDocument running = CreateVideoToolsV2WorkspaceJob(
            editFixture,
            runningId,
            "running",
            mutateJob: job => job["progress"] = 42);
        using JsonDocument failed = CreateVideoToolsV2WorkspaceJob(
            editFixture,
            failedId,
            "failed");
        using JsonDocument terminal = CreateVideoToolsV2WorkspaceJob(
            editFixture,
            terminalId,
            "canceled");
        const string dependentId = "42000000-0000-4000-8000-000000000005";
        using JsonDocument dependent = CreateVideoToolsV2WorkspaceJob(
            finishFixture,
            dependentId,
            "running",
            video => ApplyVideoToolsV2FlowManagedSource(
                video,
                editId,
                editOutput),
            job => job["sourceVideoJobId"] = editId,
            refreshPresetHash: true);
        using JsonDocument future = CreateVideoToolsV2WorkspaceJob(
            finishFixture,
            "42000000-0000-4000-8000-000000000006",
            "queued",
            video => video["schemaVersion"] = 3,
            refreshPresetHash: true);

        JsonObject[] all =
        [
            editJob,
            finishJob,
            rootJob,
            JsonNode.Parse(queued.RootElement.GetRawText())!.AsObject(),
            JsonNode.Parse(running.RootElement.GetRawText())!.AsObject(),
            JsonNode.Parse(failed.RootElement.GetRawText())!.AsObject(),
            JsonNode.Parse(terminal.RootElement.GetRawText())!.AsObject(),
            JsonNode.Parse(dependent.RootElement.GetRawText())!.AsObject(),
            JsonNode.Parse(future.RootElement.GetRawText())!.AsObject(),
        ];
        JsonElement delivery = finishFixture.GetProperty("video")
            .GetProperty("delivery");
        JsonElement probe = finishFixture.GetProperty("video")
            .GetProperty("source")
            .GetProperty("probe");
        bool deliveryIdentity = delivery.GetProperty("width").GetInt32()
                == probe.GetProperty("width").GetInt32() * 2
            && delivery.GetProperty("height").GetInt32()
                == probe.GetProperty("height").GetInt32() * 2
            && delivery.GetProperty("frameCount").GetInt32()
                == probe.GetProperty("frameCount").GetInt32()
            && delivery.GetProperty("fpsNumerator").GetInt32()
                == probe.GetProperty("fpsNumerator").GetInt32()
            && delivery.GetProperty("fpsDenominator").GetInt32()
                == probe.GetProperty("fpsDenominator").GetInt32()
            && delivery.GetProperty("videoPtsSha256").GetString()
                == probe.GetProperty("videoPtsSha256").GetString()
            && delivery.GetProperty("preserveSourceAudioPackets").GetBoolean()
            && probe.GetProperty("audio")
                    .GetProperty("packetPayloadSha256").GetString()
                is { Length: 64 };
        return new VideoToolsV2FlowJobFixture(
            all,
            [rootJob, editJob, finishJob],
            editId,
            finishId,
            queuedId,
            runningId,
            failedId,
            terminalId,
            dependentId,
            editOutput,
            finishOutput,
            deliveryIdentity,
            future.RootElement.Clone());
    }

    private static void ApplyVideoToolsV2FlowManagedSource(
        JsonObject video,
        string sourceJobId,
        string sourcePath)
    {
        JsonObject staged = video["source"]!.AsObject();
        JsonObject signature = staged["stagingSignature"]!
            .DeepClone().AsObject();
        ApplyInventorySourceSignature(signature, sourcePath);
        video["source"] = new JsonObject
        {
            ["kind"] = "managed-video-job",
            ["producerJobId"] = sourceJobId,
            ["canonicalPath"] = sourcePath,
            ["signature"] = signature,
            ["sha256"] = staged["stagingSha256"]!.DeepClone(),
            ["probe"] = staged["probe"]!.DeepClone(),
        };
        video["requested"]!["source"] = new JsonObject
        {
            ["kind"] = "managed-video-job",
            ["sourceVideoJobId"] = sourceJobId,
        };
    }

    private static string MaterializeVideoToolsV2FlowOutput(
        JsonObject job,
        string videosRoot)
    {
        string filename = Path.GetFileName(
            job["outputPath"]!.GetValue<string>());
        string outputPath = Path.Combine(videosRoot, filename);
        WriteIsoBmffSmokeVideo(outputPath);
        job["outputPath"] = outputPath;
        return outputPath;
    }

    private sealed record VideoToolsV2FlowJobFixture(
        JsonObject[] Jobs,
        JsonObject[] InventoryJobs,
        string EditSucceededId,
        string FinishSucceededId,
        string EditQueuedId,
        string EditRunningId,
        string EditFailedId,
        string EditTerminalId,
        string DependentId,
        string EditOutput,
        string FinishOutput,
        bool DeliveryIdentity,
        JsonElement FutureJob);
}

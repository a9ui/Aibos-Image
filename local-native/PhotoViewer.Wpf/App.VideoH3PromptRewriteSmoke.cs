using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoH3PromptRewriteSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "aibos-wpf-video-h3-prompt-" + Guid.NewGuid().ToString("N"));
        string sourceFolder = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourcePath = Path.Combine(
            sourceFolder,
            "synthetic-h3-prompt-source.png");
        string statePath = Path.Combine(storeRoot, "state.json");
        var environment = new Dictionary<string, string?>
        {
            ["PHOTOVIEWER_WPF_STATE_PATH"] = statePath,
            ["PHOTOVIEWER_WPF_FAVORITES_PATH"] = Path.Combine(storeRoot, "favorites.json"),
            ["PHOTOVIEWER_WPF_SEEN_PATH"] = Path.Combine(storeRoot, "seen.json"),
            ["PHOTOVIEWER_WPF_RECENT_PATH"] = Path.Combine(storeRoot, "recent-folders.json"),
            ["PHOTOVIEWER_WPF_SETTINGS_PATH"] = Path.Combine(storeRoot, "settings.json"),
            ["PHOTOVIEWER_WPF_ALBUMS_PATH"] = Path.Combine(storeRoot, "albums.json"),
            ["PHOTOVIEWER_WPF_SEARCH_HISTORY_PATH"] = Path.Combine(storeRoot, "search-history.json"),
            ["PHOTOVIEWER_WPF_METADATA_INDEX_DIRECTORY"] = Path.Combine(storeRoot, "metadata-index"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"] = Path.Combine(storeRoot, "enhance", "jobs.json"),
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = Path.Combine(storeRoot, "outputs"),
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        object result = new { ok = false, message = "Smoke did not complete." };
        bool ok = false;
        MainWindow? window = null;
        try
        {
            string? contractArgument = Environment.GetEnvironmentVariable(
                "AIBOS_VIDEO_V2_CONTRACT_PATH");
            if (string.IsNullOrWhiteSpace(contractArgument))
                throw new InvalidDataException(
                    "AIBOS_VIDEO_V2_CONTRACT_PATH is required.");
            string contractPath = Path.GetFullPath(contractArgument);
            byte[] contractBefore = File.ReadAllBytes(contractPath);
            using JsonDocument contractDocument = JsonDocument.Parse(
                contractBefore);
            JsonElement promptContract = contractDocument.RootElement
                .GetProperty("promptRewriteProtocol")
                .Clone();
            JsonElement routeFixture = promptContract.GetProperty("route");
            JsonElement sourceFixture = promptContract.GetProperty(
                "sourceFixture");
            JsonElement requestFixture = promptContract.GetProperty(
                "requestFixture");
            JsonElement responseFixture = promptContract.GetProperty(
                "responseFixture");
            string rewriteRevision = promptContract.GetProperty(
                "rewriteRevision").GetString()!;
            bool contractIdentity = promptContract.GetProperty(
                    "schemaVersion").GetInt32() == 1
                && promptContract.GetProperty("contractId").GetString()
                    == "PV-ENHANCE-VIDEO-H3-PROMPT-REWRITE-001"
                && promptContract.GetProperty("protocol").GetString()
                    == "aibos.enhancement-video-h3-prompt-rewrite/v1"
                && routeFixture.GetProperty("method").GetString() == "POST"
                && routeFixture.GetProperty("path").GetString()
                    == "/api/enhance/video-prompts/h3/rewrite"
                && routeFixture.GetProperty("loopbackOnly").GetBoolean()
                && routeFixture.GetProperty("cacheControl").GetString()
                    == "no-store, max-age=0"
                && rewriteRevision == "aibos-h3-i2va-local-v1";
            byte[] sourceBytes = Convert.FromBase64String(
                sourceFixture.GetProperty("pngBase64").GetString()!);
            string fixtureSourceSha256 = Convert.ToHexStringLower(
                SHA256.HashData(sourceBytes));
            bool sourceFixtureExact = string.Equals(
                    sourceFixture.GetProperty("fileName").GetString(),
                    Path.GetFileName(sourcePath),
                    StringComparison.Ordinal)
                && string.Equals(
                    sourceFixture.GetProperty("sha256").GetString(),
                    fixtureSourceSha256,
                    StringComparison.Ordinal)
                && string.Equals(
                    responseFixture.GetProperty("sourceSha256").GetString(),
                    fixtureSourceSha256,
                    StringComparison.Ordinal);

            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(
                environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!)!);
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.WriteAllText(
                environment["PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!,
                "{\"jobs\":[]}");
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            string basePrompt = requestFixture.GetProperty("prompt").GetString()!;
            string candidateA = responseFixture.GetProperty(
                "candidatePrompt").GetString()!;
            string candidateEdited = CreateVideoH3Candidate("CANDIDATE_EDITED");
            string candidateB = CreateVideoH3Candidate("CANDIDATE_BRAVO");
            string responseSourceSha256 = responseFixture.GetProperty(
                "sourceSha256").GetString()!;
            string responseCandidate = candidateA;
            string responseRevision = responseFixture.GetProperty(
                "rewriteRevision").GetString()!;
            string responseModelId = responseFixture.GetProperty(
                "modelId").GetString()!;
            double responseInferenceMilliseconds = responseFixture.GetProperty(
                "inferenceMilliseconds").GetDouble();
            JsonElement? activeErrorBody = null;
            HttpStatusCode activeErrorStatus = HttpStatusCode.OK;
            string latestRewriteBody = "";
            string latestRewritePath = "";
            int readinessGetCalls = 0;
            int rewritePostCalls = 0;
            int jobsPostCalls = 0;
            int enqueueHealthGetCalls = 0;
            TaskCompletionSource<bool>? enqueueHealthEntered = null;
            TaskCompletionSource<bool>? releaseEnqueueHealth = null;
            string rewriteResponseTransport = "normal";
            ChunkedFakeLoopbackStream? declaredOversizeStream = null;
            ChunkedFakeLoopbackStream? chunkedOversizeStream = null;

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    window.ConfigureModalEnhancementForSmoke(async (request, token) =>
                    {
                        string path = request.RequestUri?.AbsolutePath ?? "";
                        if (request.Method == HttpMethod.Get
                            && string.Equals(
                                path,
                                "/api/enhance/health",
                                StringComparison.Ordinal))
                        {
                            enqueueHealthGetCalls++;
                            enqueueHealthEntered?.TrySetResult(true);
                            if (releaseEnqueueHealth is not null)
                                await releaseEnqueueHealth.Task.WaitAsync(token);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    CreateVideoV2HealthJson(
                                        writerEnabled: true,
                                        ready: true,
                                        state: "ready",
                                        reasonCode: null),
                                    Encoding.UTF8,
                                    "application/json"),
                            };
                        }
                        if (request.Method == HttpMethod.Get
                            && path.EndsWith(
                                "/api/enhance/jobs",
                                StringComparison.Ordinal))
                        {
                            readinessGetCalls++;
                            return JsonResponse(
                                HttpStatusCode.OK,
                                new { jobs = Array.Empty<object>() });
                        }
                        if (request.Method == HttpMethod.Post
                            && string.Equals(
                                path,
                                "/api/enhance/video-prompts/h3/rewrite",
                                StringComparison.Ordinal))
                        {
                            rewritePostCalls++;
                            latestRewritePath = path;
                            latestRewriteBody = request.Content is null
                                ? ""
                                : await request.Content.ReadAsStringAsync(token);
                            if (activeErrorBody is JsonElement errorBody)
                            {
                                return JsonResponse(
                                    activeErrorStatus,
                                    errorBody);
                            }

                            if (!string.Equals(
                                    rewriteResponseTransport,
                                    "normal",
                                    StringComparison.Ordinal))
                            {
                                string oversizedJson = JsonSerializer.Serialize(
                                    new
                                    {
                                        candidatePrompt = responseCandidate,
                                        rewriteRevision = responseRevision,
                                        sourceSha256 = responseSourceSha256,
                                        modelId = responseModelId,
                                        inferenceMilliseconds =
                                            responseInferenceMilliseconds,
                                    })
                                    + new string(
                                        ' ',
                                        PhotoViewer.Wpf.MainWindow
                                            .VideoH3PromptRewriteResponseByteLimitForSmoke);
                                byte[] oversizedBytes = Encoding.UTF8.GetBytes(
                                    oversizedJson);
                                var stream = new ChunkedFakeLoopbackStream(
                                    oversizedBytes,
                                    maximumChunkBytes: 257);
                                var content = new StreamContent(stream);
                                content.Headers.TryAddWithoutValidation(
                                    "Content-Type",
                                    "application/json");
                                if (string.Equals(
                                        rewriteResponseTransport,
                                        "declared-oversize",
                                        StringComparison.Ordinal))
                                {
                                    content.Headers.ContentLength =
                                        oversizedBytes.Length;
                                    declaredOversizeStream = stream;
                                }
                                else
                                {
                                    chunkedOversizeStream = stream;
                                }
                                return new HttpResponseMessage(HttpStatusCode.OK)
                                {
                                    Content = content,
                                };
                            }
                            return JsonResponse(
                                HttpStatusCode.OK,
                                new
                                {
                                    candidatePrompt = responseCandidate,
                                    rewriteRevision = responseRevision,
                                    sourceSha256 = responseSourceSha256,
                                    modelId = responseModelId,
                                    inferenceMilliseconds =
                                        responseInferenceMilliseconds,
                                });
                        }
                        if (request.Method == HttpMethod.Post
                            && path.EndsWith(
                                "/api/enhance/jobs",
                                StringComparison.Ordinal))
                        {
                            jobsPostCalls++;
                        }
                        return JsonResponse(
                            HttpStatusCode.NotFound,
                            new { error = "unexpected smoke route" });
                    });

                    await window.LoadFolderAsync(sourceFolder);
                    bool selected = window.SelectFileNameForSmoke(
                        Path.GetFileName(sourcePath));
                    bool boardOpened = window.OpenVideoGenerationBoardForSmoke(
                        "original");
                    window.SetMiniMaxH3CapabilityForSmoke(
                        checkedHealth: true,
                        ready: true,
                        reasonCode: null);
                    window.SelectVideoModelForSmoke("minimax-h3");
                    window.ConfigureVideoGenerationForSmoke(
                        6,
                        16,
                        409_600,
                        basePrompt);

                    string[] surfaceIssues =
                        window.VideoH3PromptRewriteSurfaceIssuesForSmoke.ToArray();
                    bool surface = selected
                        && boardOpened
                        && surfaceIssues.Length == 0
                        && window.VideoH3PromptRewritePanelVisibleForSmoke;

                    bool rewriteAccepted =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    using JsonDocument requestDocument = JsonDocument.Parse(
                        latestRewriteBody);
                    JsonElement requestRoot = requestDocument.RootElement;
                    bool requestExact = HasExactNames(
                            requestRoot,
                            "sourceId",
                            "prompt",
                            "frameCount",
                            "playbackFps")
                        && ExactString(
                            requestRoot,
                            "sourceId",
                            Path.GetFullPath(sourcePath))
                        && ExactString(requestRoot, "prompt", basePrompt)
                        && requestRoot.TryGetProperty(
                            "frameCount",
                            out JsonElement frameCount)
                        && frameCount.TryGetInt32(out int frames)
                        && frames == requestFixture.GetProperty(
                            "frameCount").GetInt32()
                        && requestRoot.TryGetProperty(
                            "playbackFps",
                            out JsonElement playbackFps)
                        && playbackFps.TryGetInt32(out int fps)
                        && fps == requestFixture.GetProperty(
                            "playbackFps").GetInt32()
                        && string.Equals(
                            latestRewritePath,
                            routeFixture.GetProperty("path").GetString(),
                            StringComparison.Ordinal);
                    bool responseFixtureAccepted = PhotoViewer.Wpf.MainWindow
                        .TryParseVideoH3PromptRewriteResponseForSmoke(
                            responseFixture,
                            out string fixtureCandidate)
                        && string.Equals(
                            fixtureCandidate,
                            candidateA,
                            StringComparison.Ordinal);
                    bool candidateSeparate = rewriteAccepted
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            basePrompt,
                            StringComparison.Ordinal)
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            candidateA,
                            StringComparison.Ordinal)
                        && window.VideoH3PromptCandidateFreshForSmoke
                        && window.VideoH3PromptCandidateEditableForSmoke
                        && window.VideoH3PromptCandidateApplyEnabledForSmoke;

                    bool revisionFixturesExact = true;
                    int revisionFixtureCount = 0;
                    foreach (JsonElement revisionFixture in promptContract
                                 .GetProperty("revisionFixtures")
                                 .EnumerateArray())
                    {
                        revisionFixtureCount++;
                        using JsonDocument revisionResponse =
                            CreateVideoH3ResponseDocument(
                                responseFixture,
                                revisionFixture.GetProperty(
                                    "rewriteRevision").GetString()!);
                        bool revisionAccepted = PhotoViewer.Wpf.MainWindow
                            .TryParseVideoH3PromptRewriteResponseForSmoke(
                                revisionResponse.RootElement,
                                out string revisionCandidate);
                        revisionFixturesExact &= revisionAccepted
                                == revisionFixture.GetProperty(
                                    "expectedAccepted").GetBoolean()
                            && (!revisionAccepted
                                || string.Equals(
                                    revisionCandidate,
                                    candidateA,
                                    StringComparison.Ordinal));
                    }

                    bool errorFixturesFailClosed = true;
                    int errorFixtureCount = 0;
                    foreach (JsonElement errorFixture in promptContract
                                 .GetProperty("errorFixtures")
                                 .EnumerateArray())
                    {
                        errorFixtureCount++;
                        activeErrorStatus = (HttpStatusCode)errorFixture
                            .GetProperty("status").GetInt32();
                        activeErrorBody = errorFixture.GetProperty("body").Clone();
                        bool errorAccepted =
                            await window.RewriteVideoPromptForH3ForSmokeAsync();
                        errorFixturesFailClosed &= !errorAccepted
                            && string.Equals(
                                window.AuthoritativeVideoPromptForSmoke,
                                basePrompt,
                                StringComparison.Ordinal)
                            && string.Equals(
                                window.VideoH3PromptCandidateForSmoke,
                                candidateA,
                                StringComparison.Ordinal)
                            && window.VideoH3PromptCandidateFreshForSmoke;
                    }
                    activeErrorBody = null;
                    activeErrorStatus = HttpStatusCode.OK;

                    window.SetVideoH3PromptCandidateForSmoke(
                        new string('z', 2_001));
                    bool editorOversizeRejectedWhole =
                        window.VideoH3PromptCandidateForSmoke.Length == 2_001
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    string candidateEditedCrLf = candidateEdited.Replace(
                        "\n",
                        "\r\n",
                        StringComparison.Ordinal);
                    window.SetVideoH3PromptCandidateForSmoke(candidateEditedCrLf);
                    bool candidateEditable = string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            candidateEditedCrLf,
                            StringComparison.Ordinal)
                        && window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    string normalizedBoundaryCandidate =
                        PadVideoH3CandidateToLength(
                            CreateVideoH3Candidate("CANDIDATE_BOUNDARY"),
                            2_000);
                    int firstNewlineIndex = normalizedBoundaryCandidate.IndexOf(
                        '\n');
                    string rawOverLimitAfterCrLfNormalization =
                        normalizedBoundaryCandidate[..firstNewlineIndex]
                        + "\r\n"
                        + normalizedBoundaryCandidate[(firstNewlineIndex + 1)..];
                    window.SetVideoH3PromptCandidateForSmoke(
                        rawOverLimitAfterCrLfNormalization);
                    bool rawUtf16LimitCheckedBeforeNormalization =
                        rawOverLimitAfterCrLfNormalization.Length == 2_001
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke
                        && !PhotoViewer.Wpf.MainWindow.TryValidateVideoH3PromptForSmoke(
                            rawOverLimitAfterCrLfNormalization,
                            out _);
                    string duplicateMarkerCandidate = candidateEdited.Replace(
                        "CANDIDATE_EDITED.",
                        "CANDIDATE_EDITED overall_soundscape: embedded duplicate.",
                        StringComparison.Ordinal);
                    window.SetVideoH3PromptCandidateForSmoke(
                        duplicateMarkerCandidate);
                    bool wholePromptMarkerUniqueness =
                        !window.VideoH3PromptCandidateApplyEnabledForSmoke
                        && !PhotoViewer.Wpf.MainWindow.TryValidateVideoH3PromptForSmoke(
                            duplicateMarkerCandidate,
                            out _);
                    string wrongMarkerOrderCandidate =
                        "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\n"
                        + "integrated_multimodal_description: [Shot 1] A clearly adult idol waves.\n\n"
                        + "non_diegetic_music: Bright upbeat synth-pop.\n\n"
                        + "overall_soundscape: Quiet room ambience.";
                    bool wholePromptMarkerOrder =
                        !PhotoViewer.Wpf.MainWindow.TryValidateVideoH3PromptForSmoke(
                            wrongMarkerOrderCandidate,
                            out _);
                    window.SetVideoH3PromptCandidateForSmoke(candidateEditedCrLf);
                    candidateEditable &=
                        window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    window.FlushStateForSmoke();
                    string stateBeforeApply = File.ReadAllText(statePath);
                    bool candidateNotPersisted = !stateBeforeApply.Contains(
                            "CANDIDATE_EDITED",
                            StringComparison.Ordinal)
                        && stateBeforeApply.Contains(
                            basePrompt,
                            StringComparison.Ordinal);

                    using JsonDocument authoritativeQueue = JsonDocument.Parse(
                        window.BuildMiniMaxH3EnqueueRequestJsonForSmoke(
                            window.AuthoritativeVideoPromptForSmoke));
                    bool queueReadsOnlyInput = authoritativeQueue.RootElement
                            .TryGetProperty("video", out JsonElement queuedVideo)
                        && queuedVideo.TryGetProperty(
                            "requested",
                            out JsonElement queuedRequested)
                        && ExactString(
                            queuedRequested,
                            "prompt",
                            basePrompt)
                        && !authoritativeQueue.RootElement
                            .GetRawText()
                            .Contains("CANDIDATE_EDITED", StringComparison.Ordinal);

                    bool applied = window.ApplyVideoH3PromptCandidateForSmoke()
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            candidateEdited,
                            StringComparison.Ordinal)
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            candidateEdited,
                            StringComparison.Ordinal)
                        && window.VideoH3PromptUndoEnabledForSmoke
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    bool undone = window.UndoAppliedVideoH3PromptForSmoke()
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            basePrompt,
                            StringComparison.Ordinal)
                        && !window.VideoH3PromptUndoEnabledForSmoke;

                    responseCandidate = candidateA;
                    bool rewrittenForInputStale =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    window.SetAuthoritativeVideoPromptForSmoke(
                        basePrompt + " Changed after rewrite.");
                    bool inputStale = rewrittenForInputStale
                        && !window.VideoH3PromptCandidateFreshForSmoke
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            candidateA,
                            StringComparison.Ordinal);
                    window.SetAuthoritativeVideoPromptForSmoke(basePrompt);
                    bool revertedInputStillStale =
                        !window.VideoH3PromptCandidateFreshForSmoke;

                    bool rewrittenForStyleStale =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    bool styleSaved = window.SaveVideoStyleForSmoke(
                        "Same settings style");
                    bool styleStale = rewrittenForStyleStale
                        && styleSaved
                        && !window.VideoH3PromptCandidateFreshForSmoke
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;

                    bool rewrittenForModelStale =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    window.SelectVideoModelForSmoke("wan22-ti2v-5b");
                    bool panelHiddenForWan =
                        !window.VideoH3PromptRewritePanelVisibleForSmoke;
                    window.SelectVideoModelForSmoke("minimax-h3");
                    bool modelStale = rewrittenForModelStale
                        && panelHiddenForWan
                        && window.VideoH3PromptRewritePanelVisibleForSmoke
                        && !window.VideoH3PromptCandidateFreshForSmoke
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;

                    bool rewrittenForSourceStale =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    using (var changedSource = new FileStream(
                        sourcePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        changedSource.WriteByte(0);
                    }
                    bool freshAfterSourceChange =
                        window.VideoH3PromptCandidateFreshForSmoke;
                    bool sourceApplyRejected =
                        !window.ApplyVideoH3PromptCandidateForSmoke();
                    bool applyAfterSourceChange =
                        window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    bool sourceStale = rewrittenForSourceStale
                        && !freshAfterSourceChange
                        && sourceApplyRejected
                        && !applyAfterSourceChange;

                    string retainedBeforeInvalid =
                        window.VideoH3PromptCandidateForSmoke;
                    responseSourceSha256 = Convert.ToHexStringLower(
                        SHA256.HashData(File.ReadAllBytes(sourcePath)));
                    responseCandidate = new string('x', 2_001);
                    bool oversizeRejected =
                        !await window.RewriteVideoPromptForH3ForSmokeAsync()
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            retainedBeforeInvalid,
                            StringComparison.Ordinal)
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;

                    responseCandidate = candidateB;
                    responseSourceSha256 = new string('b', 64);
                    bool hashMismatchRejected =
                        !await window.RewriteVideoPromptForH3ForSmokeAsync()
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            retainedBeforeInvalid,
                            StringComparison.Ordinal)
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke;
                    responseSourceSha256 = Convert.ToHexStringLower(
                        SHA256.HashData(File.ReadAllBytes(sourcePath)));
                    window.SetAuthoritativeVideoPromptForSmoke(basePrompt);
                    bool rewrittenAfterInvalid =
                        await window.RewriteVideoPromptForH3ForSmokeAsync();
                    string retainedBeforeManualEdit =
                        window.VideoH3PromptCandidateForSmoke;
                    window.SetAuthoritativeVideoPromptForSmoke(
                        basePrompt + " Manual edit invalidates the candidate.");
                    bool manualEditInvalidatesUndoAndApply = rewrittenAfterInvalid
                        && !window.VideoH3PromptCandidateApplyEnabledForSmoke
                        && !window.VideoH3PromptUndoEnabledForSmoke
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            retainedBeforeManualEdit,
                            StringComparison.Ordinal);

                    string pendingInboxPath = Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(environment[
                            "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!))!,
                        "enqueue-inbox",
                        "v1",
                        "pending");
                    int PendingReservationCount()
                        => Directory.Exists(pendingInboxPath)
                            ? Directory.EnumerateFiles(
                                pendingInboxPath,
                                "*.json",
                                SearchOption.TopDirectoryOnly).Count()
                            : 0;
                    string transportInputBefore =
                        window.AuthoritativeVideoPromptForSmoke;
                    string transportCandidateBefore =
                        window.VideoH3PromptCandidateForSmoke;
                    string transportJobsBefore = FileFingerprint(environment[
                        "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!);
                    int transportReservationsBefore =
                        PendingReservationCount();
                    int transportJobsPostsBefore = jobsPostCalls;
                    rewriteResponseTransport = "declared-oversize";
                    bool declaredOversizeRejected =
                        !await window.RewriteVideoPromptForH3ForSmokeAsync();
                    rewriteResponseTransport = "chunked-oversize";
                    bool chunkedOversizeRejected =
                        !await window.RewriteVideoPromptForH3ForSmokeAsync();
                    rewriteResponseTransport = "normal";
                    int responseByteLimit = PhotoViewer.Wpf.MainWindow
                        .VideoH3PromptRewriteResponseByteLimitForSmoke;
                    bool responseTransportBounded =
                        declaredOversizeRejected
                        && chunkedOversizeRejected
                        && declaredOversizeStream is not null
                        && declaredOversizeStream.BytesRead == 0
                        && chunkedOversizeStream is not null
                        && chunkedOversizeStream.BytesRead > 0
                        && chunkedOversizeStream.BytesRead
                            <= responseByteLimit + 1
                        && chunkedOversizeStream.BytesRead
                            < chunkedOversizeStream.PayloadLength
                        && string.Equals(
                            window.AuthoritativeVideoPromptForSmoke,
                            transportInputBefore,
                            StringComparison.Ordinal)
                        && string.Equals(
                            window.VideoH3PromptCandidateForSmoke,
                            transportCandidateBefore,
                            StringComparison.Ordinal)
                        && string.Equals(
                            FileFingerprint(environment[
                                "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH"]!),
                            transportJobsBefore,
                            StringComparison.Ordinal)
                        && PendingReservationCount()
                            == transportReservationsBefore
                        && jobsPostCalls == transportJobsPostsBefore;
                    int reservationsBeforeSourceRace =
                        PendingReservationCount();
                    int jobsPostsBeforeSourceRace = jobsPostCalls;
                    enqueueHealthEntered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    releaseEnqueueHealth = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Task<bool> sourceRaceQueue =
                        window.QueueVideoGenerationForSmokeAsync();
                    bool sourceRaceReachedHealth = await enqueueHealthEntered.Task
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    DateTime sourceRaceLastWriteUtc =
                        File.GetLastWriteTimeUtc(sourcePath);
                    using (var changedDuringHealth = new FileStream(
                        sourcePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        changedDuringHealth.WriteByte(0);
                    }
                    File.SetLastWriteTimeUtc(sourcePath, sourceRaceLastWriteUtc);
                    releaseEnqueueHealth.TrySetResult(true);
                    bool sourceRaceQueued = await sourceRaceQueue;
                    enqueueHealthEntered = null;
                    releaseEnqueueHealth = null;
                    bool sourceChangedBeforePublishNoReservation =
                        sourceRaceReachedHealth
                        && !sourceRaceQueued
                        && enqueueHealthGetCalls == 1
                        && jobsPostCalls == jobsPostsBeforeSourceRace
                        && PendingReservationCount()
                            == reservationsBeforeSourceRace;

                    window.FlushStateForSmoke();
                    MainWindow reload = HiddenWindow();
                    bool candidateNotRestored = string.IsNullOrEmpty(
                        reload.VideoH3PromptCandidateForSmoke);
                    reload.Close();

                    bool contractUnchanged = File.ReadAllBytes(contractPath)
                        .SequenceEqual(contractBefore);
                    bool noQueueMutation = jobsPostCalls == 0
                        && readinessGetCalls == rewritePostCalls
                        && rewritePostCalls >= 8;
                    ok = contractIdentity
                        && sourceFixtureExact
                        && contractUnchanged
                        && surface
                        && requestExact
                        && responseFixtureAccepted
                        && revisionFixturesExact
                        && revisionFixtureCount >= 2
                        && errorFixturesFailClosed
                        && errorFixtureCount >= 1
                        && candidateSeparate
                        && editorOversizeRejectedWhole
                        && candidateEditable
                        && rawUtf16LimitCheckedBeforeNormalization
                        && wholePromptMarkerUniqueness
                        && wholePromptMarkerOrder
                        && candidateNotPersisted
                        && queueReadsOnlyInput
                        && applied
                        && undone
                        && inputStale
                        && revertedInputStillStale
                        && styleStale
                        && modelStale
                        && sourceStale
                        && oversizeRejected
                        && hashMismatchRejected
                        && manualEditInvalidatesUndoAndApply
                        && responseTransportBounded
                        && sourceChangedBeforePublishNoReservation
                        && candidateNotRestored
                        && noQueueMutation;
                    result = new
                    {
                        ok,
                        contractIdentity,
                        sourceFixtureExact,
                        contractUnchanged,
                        surface,
                        surfaceIssues,
                        requestExact,
                        responseFixtureAccepted,
                        revisionFixturesExact,
                        revisionFixtureCount,
                        errorFixturesFailClosed,
                        errorFixtureCount,
                        candidateSeparate,
                        editorOversizeRejectedWhole,
                        candidateEditable,
                        rawUtf16LimitCheckedBeforeNormalization,
                        wholePromptMarkerUniqueness,
                        wholePromptMarkerOrder,
                        candidateNotPersisted,
                        queueReadsOnlyInput,
                        applied,
                        undone,
                        inputStale,
                        revertedInputStillStale,
                        styleStale,
                        modelStale,
                        sourceStale,
                        rewrittenForSourceStale,
                        freshAfterSourceChange,
                        sourceApplyRejected,
                        applyAfterSourceChange,
                        sourceLengthAfterChange = new FileInfo(sourcePath).Length,
                        oversizeRejected,
                        hashMismatchRejected,
                        manualEditInvalidatesUndoAndApply,
                        responseTransportBounded,
                        declaredOversizeRejected,
                        declaredOversizeBytesRead =
                            declaredOversizeStream?.BytesRead ?? -1,
                        chunkedOversizeRejected,
                        chunkedOversizeBytesRead =
                            chunkedOversizeStream?.BytesRead ?? -1,
                        responseByteLimit,
                        sourceChangedBeforePublishNoReservation,
                        sourceRaceReachedHealth,
                        sourceRaceQueued,
                        enqueueHealthGetCalls,
                        candidateNotRestored,
                        noQueueMutation,
                        readinessGetCalls,
                        rewritePostCalls,
                        jobsPostCalls,
                    };
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
                    try { window.Close(); } catch { }
                    foreach ((string key, string? value) in previousEnvironment)
                        Environment.SetEnvironmentVariable(key, value);
                    Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
                        JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true }));
                    try
                    {
                        if (Directory.Exists(smokeRoot))
                            Directory.Delete(smokeRoot, recursive: true);
                    }
                    catch
                    {
                    }
                    Shutdown(ok ? 0 : 1);
                }
            });
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
            foreach ((string key, string? value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
            File.WriteAllText(
                resultFullPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                if (Directory.Exists(smokeRoot))
                    Directory.Delete(smokeRoot, recursive: true);
            }
            catch
            {
            }
            Shutdown(1);
        }
    }

    private static string CreateVideoH3Candidate(string marker)
        => "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\n"
            + "integrated_multimodal_description: [Shot 1] "
            + $"{marker}. Her short dark hair and red stage jacket remain continuous from the first frame as she smiles, steps sideways, and gives a bright wave while the camera makes a smooth arc.\n\n"
            + "overall_soundscape: Light footsteps, soft fabric motion, and quiet room ambience.\n\n"
            + "non_diegetic_music: Bright upbeat synth-pop at a moderate tempo.";

    private static string PadVideoH3CandidateToLength(
        string candidate,
        int targetLength)
    {
        if (candidate.Length > targetLength)
            throw new InvalidDataException(
                "The canonical H3 candidate exceeds its requested boundary.");
        int insertionIndex = candidate.IndexOf(
            "\n\noverall_soundscape:",
            StringComparison.Ordinal);
        if (insertionIndex < 0)
            throw new InvalidDataException(
                "The canonical H3 candidate is missing its soundscape marker.");
        return candidate.Insert(
            insertionIndex,
            new string('x', targetLength - candidate.Length));
    }

    private static JsonDocument CreateVideoH3ResponseDocument(
        JsonElement responseFixture,
        string rewriteRevision)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            candidatePrompt = responseFixture.GetProperty(
                "candidatePrompt").GetString(),
            rewriteRevision,
            sourceSha256 = responseFixture.GetProperty(
                "sourceSha256").GetString(),
            modelId = responseFixture.GetProperty("modelId").GetString(),
            inferenceMilliseconds = responseFixture.GetProperty(
                "inferenceMilliseconds").GetDouble(),
        }));

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        object payload)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class ChunkedFakeLoopbackStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int _maximumChunkBytes;
        private int _position;

        internal ChunkedFakeLoopbackStream(
            byte[] payload,
            int maximumChunkBytes)
        {
            _payload = payload;
            _maximumChunkBytes = Math.Max(1, maximumChunkBytes);
        }

        internal int BytesRead => _position;
        internal int PayloadLength => _payload.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> destination)
        {
            int remaining = _payload.Length - _position;
            if (remaining <= 0 || destination.Length == 0)
                return 0;

            int count = Math.Min(
                Math.Min(remaining, destination.Length),
                _maximumChunkBytes);
            _payload.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}

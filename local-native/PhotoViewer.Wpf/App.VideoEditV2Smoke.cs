using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class App
{
    private const string VideoEditV2SmokePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAACXBIWXMAAAPoAAAD6AG1e1JrAAAADElEQVQImWNgZGIGAAAOAAeCcsnOAAAAAElFTkSuQmCC";
    private const string VideoEditV2SmokePngSha256 =
        "999f1d1527ee7e79266f16add5430fff76b1225d742464a5b1ff1f02971bb8ee";

    private void CaptureVideoEditV2Smoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-edit-v2-")
            .FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourcePath = Path.Combine(sourceRoot, "synthetic-edit.mp4");
        string movPath = Path.Combine(sourceRoot, "synthetic-view-only.mov");
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
            ["PHOTOVIEWER_WPF_ENHANCEMENT_OUTPUT_ROOT"] = Path.Combine(
                storeRoot,
                "outputs"),
        };
        Dictionary<string, string?> previousEnvironment = environment.Keys
            .ToDictionary(
                static key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        MainWindow? window = null;
        object result = new { ok = false, message = "Smoke did not complete." };
        bool ok = false;

        try
        {
            bool purePlanner = VerifyVideoEditV2PurePlanner();
            bool parserVectors = VerifyVideoEditV2TransientParserVectors();
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(storeRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            File.Copy(sourcePath, movPath);
            string sourceBefore = FingerprintVideoEditV2File(sourcePath);
            string sourceSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(sourcePath)))
                .ToLowerInvariant();
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            int pathResolverCalls = 0;
            window.SetCanonicalPathResolverForSmoke(path =>
            {
                pathResolverCalls++;
                return Path.GetFullPath(path);
            });

            var actions = new List<string>();
            bool exactRouteOnly = true;
            bool exactRequests = true;
            bool malformedCompile = false;
            bool holdCompile = false;
            string? capturedSelector = null;
            TaskCompletionSource<bool>? compileEntered = null;
            TaskCompletionSource<bool>? releaseCompile = null;
            window.ConfigureModalEnhancementForSmoke(async (request, token) =>
            {
                if (request.Method == HttpMethod.Get
                    && string.Equals(
                        request.RequestUri?.AbsolutePath,
                        "/api/enhance/health",
                        StringComparison.Ordinal))
                {
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.OK,
                        BuildVideoEditV2SmokeDisabledHealthResponse());
                }
                exactRouteOnly &= request.Method == HttpMethod.Post
                    && string.Equals(
                        request.RequestUri?.AbsolutePath,
                        "/api/enhance/video-prompts/v2/edit/compile",
                        StringComparison.Ordinal);
                string json = request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(token);
                exactRequests &= Encoding.UTF8.GetByteCount(json)
                    <= VideoEditV2TransientContract.MaximumRequestBytes;
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!TryGetVideoEditV2SmokeAction(root, out string action))
                {
                    exactRequests = false;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.BadRequest,
                        "{\"error\":\"invalid action\"}");
                }
                actions.Add(action);

                switch (action)
                {
                    case "probe":
                    {
                        JsonElement source = default;
                        exactRequests &= HasExactVideoEditV2SmokeKeys(
                                root,
                                "action",
                                "source")
                            && root.TryGetProperty(
                                "source",
                                out source)
                            && HasExactVideoEditV2SmokeKeys(
                                source,
                                "kind",
                                "path",
                                "size",
                                "mtimeMs",
                                "sha256")
                            && source.TryGetProperty(
                                "kind",
                                out JsonElement kind)
                            && kind.GetString() == "displayed-file"
                            && source.TryGetProperty(
                                "sha256",
                                out JsonElement sha)
                            && sha.GetString() == sourceSha256;
                        capturedSelector = source.ValueKind == JsonValueKind.Object
                            ? source.GetRawText()
                            : null;
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.OK,
                            BuildVideoEditV2SmokeProbeResponse());
                    }
                    case "preview":
                    {
                        exactRequests &= HasExactVideoEditV2SmokeKeys(
                                root,
                                "action",
                                "source",
                                "selection")
                            && root.TryGetProperty(
                                "source",
                                out JsonElement source)
                            && string.Equals(
                                source.GetRawText(),
                                capturedSelector,
                                StringComparison.Ordinal)
                            && TryGetVideoEditV2SmokeSelection(
                                root,
                                out int start,
                                out int end);
                        TryGetVideoEditV2SmokeSelection(
                            root,
                            out int previewStart,
                            out int previewEnd);
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.OK,
                            BuildVideoEditV2SmokePreviewResponse(
                                previewStart,
                                previewEnd));
                    }
                    case "compile":
                    {
                        exactRequests &= HasExactVideoEditV2SmokeKeys(
                                root,
                                "action",
                                "source",
                                "selection",
                                "previews",
                                "instructionJa")
                            && root.TryGetProperty(
                                "source",
                                out JsonElement source)
                            && string.Equals(
                                source.GetRawText(),
                                capturedSelector,
                                StringComparison.Ordinal)
                            && root.TryGetProperty(
                                "previews",
                                out JsonElement identities)
                            && identities.ValueKind == JsonValueKind.Array
                            && identities.GetArrayLength() == 3;
                        if (holdCompile)
                        {
                            compileEntered?.TrySetResult(true);
                            if (releaseCompile is not null)
                                await releaseCompile.Task.WaitAsync(token);
                        }
                        string backendPrompt =
                            VideoEditV2TransientContract.OfficialV2VSystemPrompt
                            + "Change only the clothing color to blue. Preserve the subject, background, timing, and camera. "
                            + VideoEditV2TransientContract.OfficialContinuitySentence;
                        const string summaryJa =
                            "人物・背景・動き・カメラを保ち、服の色だけを青へ変えます。";
                        const string compilerRevision =
                            VideoEditV2TransientContract.OfficialPromptCompilerRevision;
                        if (!VideoEditV2TransientContract
                                .TryCreateOfficialRendererSidecarForSmoke(
                                    backendPrompt,
                                    "v2v",
                                    out VideoEditV2RendererSidecar renderer)
                            || !VideoEditV2TransientContract
                            .TryComputeContextDigestFromCompileRequestForSmoke(
                                root,
                                backendPrompt,
                                summaryJa,
                                compilerRevision,
                                renderer,
                                out string digest))
                        {
                            exactRequests = false;
                            return VideoEditV2SmokeJsonResponse(
                                HttpStatusCode.BadRequest,
                                "{\"error\":\"digest input invalid\"}");
                        }
                        string responseJson = JsonSerializer.Serialize(new
                        {
                            action = "compile",
                            candidate = malformedCompile
                                ? (object)new
                                {
                                    backendPrompt,
                                    summaryJa,
                                    compilerRevision,
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
                                    model = "forbidden-model",
                                }
                                : new
                                {
                                    backendPrompt,
                                    summaryJa,
                                    compilerRevision,
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
                        });
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.OK,
                            responseJson);
                    }
                    default:
                        exactRequests = false;
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.NotFound,
                            "{\"error\":\"unexpected action\"}");
                }
            });

            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    bool hiddenForImages = !window.VideoEditV2EntryVisibleForSmoke;
                    ExternalVideoDropSmokeSnapshot drop =
                        await window.DropExternalVideoForSmokeAsync([sourcePath]);
                    int pathResolverCallsAfterDrop = pathResolverCalls;
                    string storesBefore = FingerprintVideoEditV2Tree(storeRoot);
                    bool videoEntry = drop.Accepted
                        && window.VideoEditV2EntryVisibleForSmoke
                        && window.VideoEditV2ExternalContextEntryForSmoke
                        && window.VideoEditV2DedicatedContextIsExternalOnlyForSmoke;
                    bool boardOpened = window.OpenVideoEditV2ForSmoke();
                    bool externalStartsUnverified = boardOpened
                        && window.VideoEditV2ProbeAffordanceVisibleForSmoke
                        && window.VideoEditV2ProbeEnabledForSmoke
                        && !window.VideoEditV2ExactFrameControlsEnabledForSmoke
                        && window.VideoEditV2CompilerDisabledForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2TrimEntryEnabledForSmoke
                        && window.VideoEditV2PreviewFramesForSmoke
                            .All(static value => value == "f --");
                    string storesAfterOpen =
                        FingerprintVideoEditV2Tree(storeRoot);
                    bool passiveOpen = actions.Count == 0
                        && pathResolverCalls == pathResolverCallsAfterDrop
                        && string.Equals(
                            storesBefore,
                            storesAfterOpen,
                            StringComparison.Ordinal);

                    bool firstLoad = await window
                        .LoadVideoEditV2FramesForSmokeAsync();
                    string[] firstFrames =
                        window.VideoEditV2PreviewFramesForSmoke;
                    bool exactPreviewLoaded = firstLoad
                        && actions.SequenceEqual(["probe", "preview"])
                        && window.VideoEditV2ExactFrameControlsEnabledForSmoke
                        && window.VideoEditV2PreviewImagesLoadedForSmoke
                        && firstFrames.SequenceEqual(
                            ["f 0", "f 59", "f 119"])
                        && window.VideoEditV2StartDisabledForSmoke;
                    bool previewSeek = window.SeekVideoEditV2PreviewForSmoke(
                            "middle")
                        && Math.Abs(
                            window.VideoEditV2PlaybackPositionForSmoke
                                - 59d / 24d) < 0.001;

                    int callsBeforeSelection = actions.Count;
                    bool selectionChanged = window
                        .SetVideoEditV2SelectionForSmoke(24, 144);
                    bool selectionStalesWithoutNetwork = selectionChanged
                        && window.VideoEditV2PreviewStaleForSmoke
                        && window.VideoEditV2CompilerDisabledForSmoke
                        && actions.Count == callsBeforeSelection;
                    bool reloadedSelection = await window
                        .LoadVideoEditV2FramesForSmokeAsync();
                    string[] selectedFrames =
                        window.VideoEditV2PreviewFramesForSmoke;
                    bool halfOpenPreview = reloadedSelection
                        && selectedFrames.SequenceEqual(
                            ["f 24", "f 83", "f 143"])
                        && window.VideoEditV2RangeStatusForSmoke.Contains(
                            "[24, 144)",
                            StringComparison.Ordinal);

                    window.SetVideoEditV2InstructionForSmoke(
                        "人物と背景を保ち、服の色だけを青へ変える");
                    malformedCompile = true;
                    window.SetVideoEditV2SkipReviewForSmoke(true);
                    bool malformedRejected = !await window
                        .CompileVideoEditV2ForSmokeAsync();
                    bool skipConsumedOnFailure = malformedRejected
                        && !window.VideoEditV2SkipReviewCheckedForSmoke
                        && !window.VideoEditV2ReviewVisibleForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;

                    malformedCompile = false;
                    bool compiledForReview = await window
                        .CompileVideoEditV2ForSmokeAsync();
                    bool reviewWithoutStart = compiledForReview
                        && window.VideoEditV2ReviewVisibleForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;
                    bool transientApproval = window
                        .ApplyVideoEditV2CandidateApprovalForSmoke()
                        && window.VideoEditV2CandidateApprovedForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;

                    bool candidateAndPreviewStale = window
                            .SetVideoEditV2SelectionForSmoke(48, 168)
                        && window.VideoEditV2CandidateStaleForSmoke
                        && window.VideoEditV2PreviewStaleForSmoke
                        && window.VideoEditV2StartDisabledForSmoke;
                    bool reloadedForSkip = await window
                        .LoadVideoEditV2FramesForSmokeAsync();
                    window.SetVideoEditV2InstructionForSmoke(
                        "人物と背景を保ち、服の色だけを青へ変える");
                    window.SetVideoEditV2SkipReviewForSmoke(true);
                    bool compiledWithSkip = reloadedForSkip
                        && await window.CompileVideoEditV2ForSmokeAsync();
                    bool skipWriterFalse = compiledWithSkip
                        && !window.VideoEditV2SkipReviewCheckedForSmoke
                        && window.VideoEditV2ReviewVisibleForSmoke
                        && !window.VideoEditV2CandidateApprovedForSmoke
                        && window.VideoEditV2StartDisabledForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;

                    holdCompile = true;
                    compileEntered = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    releaseCompile = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    window.SetVideoEditV2SkipReviewForSmoke(true);
                    Task<bool> pendingCompile = window
                        .CompileVideoEditV2ForSmokeAsync();
                    await compileEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds(5));
                    bool busyCancel = window
                        .CancelVideoEditV2CompileForSmoke();
                    releaseCompile.TrySetResult(true);
                    bool canceledCompile = !await pendingCompile
                        && busyCancel
                        && !window.VideoEditV2CompilePendingForSmoke
                        && !window.VideoEditV2SkipReviewCheckedForSmoke
                        && window.VideoEditV2StartAttemptCountForSmoke == 0;
                    holdCompile = false;

                    string storesAfterActions =
                        FingerprintVideoEditV2Tree(storeRoot);
                    bool transientOnly = string.Equals(
                            storesBefore,
                            storesAfterActions,
                            StringComparison.Ordinal)
                        && pathResolverCalls == pathResolverCallsAfterDrop;
                    bool exactActionOrder = actions.SequenceEqual(
                    [
                        "probe", "preview",
                        "probe", "preview",
                        "compile", "compile",
                        "probe", "preview",
                        "compile", "compile",
                    ]);

                    bool escapeClosesBoard =
                        window.InvokePreviewKeyForSmoke(Key.Escape)
                        && !window.VideoEditV2BoardVisibleForSmoke
                        && window.VideoEditV2LastCloseWasStaleForSmoke;
                    bool reopenAfterEscape = window.OpenVideoEditV2ForSmoke();
                    bool closeClearsTransient = reopenAfterEscape
                        && !window.VideoEditV2ExactFrameControlsEnabledForSmoke;
                    window.CloseModalForSmoke();

                    ExternalVideoDropSmokeSnapshot movDrop =
                        await window.DropExternalVideoForSmokeAsync([movPath]);
                    bool movOpened = movDrop.Accepted
                        && window.OpenVideoEditV2ForSmoke();
                    int callsBeforeMovProbe = actions.Count;
                    bool movProbeUnsupported = movOpened
                        && !await window.LoadVideoEditV2FramesForSmokeAsync()
                        && actions.Count == callsBeforeMovProbe;
                    window.CloseModalForSmoke();

                    bool sourceUntouched = string.Equals(
                        sourceBefore,
                        FingerprintVideoEditV2File(sourcePath),
                        StringComparison.Ordinal);
                    bool noForbiddenCalls = exactRouteOnly
                        && exactRequests
                        && actions.All(static action =>
                            action is "probe" or "preview" or "compile");

                    ok = purePlanner
                        && parserVectors
                        && hiddenForImages
                        && videoEntry
                        && externalStartsUnverified
                        && passiveOpen
                        && exactPreviewLoaded
                        && previewSeek
                        && selectionStalesWithoutNetwork
                        && halfOpenPreview
                        && skipConsumedOnFailure
                        && reviewWithoutStart
                        && transientApproval
                        && candidateAndPreviewStale
                        && skipWriterFalse
                        && canceledCompile
                        && transientOnly
                        && exactActionOrder
                        && escapeClosesBoard
                        && closeClearsTransient
                        && movProbeUnsupported
                        && sourceUntouched
                        && noForbiddenCalls;
                    result = new
                    {
                        ok,
                        purePlanner,
                        parserVectors,
                        hiddenForImages,
                        videoEntry,
                        externalStartsUnverified,
                        passiveOpen,
                        exactPreviewLoaded,
                        firstFrames,
                        previewSeek,
                        selectionStalesWithoutNetwork,
                        halfOpenPreview,
                        selectedFrames,
                        skipConsumedOnFailure,
                        reviewWithoutStart,
                        transientApproval,
                        candidateAndPreviewStale,
                        skipWriterFalse,
                        canceledCompile,
                        transientOnly,
                        exactActionOrder,
                        actions,
                        exactRouteOnly,
                        exactRequests,
                        escapeClosesBoard,
                        closeClearsTransient,
                        movProbeUnsupported,
                        sourceUntouched,
                        noForbiddenCalls,
                        pathResolverCalls,
                        pathResolverCallsAfterDrop,
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
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(resultFullPath)!);
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

    private static bool VerifyVideoEditV2PurePlanner()
        => VideoEditV2Planner.TryPlan(
                7_200,
                24,
                1,
                24,
                144,
                out VideoEditV2SelectionPlan plan24,
                out _)
            && plan24.SelectedFrameCount == 120
            && plan24.StartPreviewFrame == 24
            && plan24.MiddlePreviewFrame == 83
            && plan24.EndPreviewFrame == 143
            && VideoEditV2Planner.TryPlan(
                9_000,
                30,
                1,
                30,
                180,
                out VideoEditV2SelectionPlan plan30,
                out _)
            && plan30.MaximumSelectionFrames == 150
            && VideoEditV2Planner.TryPlan(
                18_000,
                60,
                1,
                60,
                360,
                out VideoEditV2SelectionPlan plan60,
                out _)
            && plan60.MaximumSelectionFrames == 300
            && !VideoEditV2Planner.TryPlan(
                7_200,
                25,
                1,
                0,
                100,
                out _,
                out VideoEditV2PlanError unsupported)
            && unsupported == VideoEditV2PlanError.UnsupportedFps;

    private static bool VerifyVideoEditV2TransientParserVectors()
    {
        const string producerId = "11111111-2222-4333-8444-555555555555";
        if (!VideoEditV2TransientContract.TryCreateManagedSelector(
                producerId,
                out VideoEditV2SourceSelector selector)
            || !VideoEditV2Planner.TryPlan(
                240,
                24,
                1,
                24,
                144,
                out VideoEditV2SelectionPlan plan,
                out _))
        {
            return false;
        }
        var summary = new VideoEditV2SourceSummary(
            240,
            24,
            1,
            10_000,
            1_280,
            720);
        string validJson = BuildVideoEditV2SmokePreviewResponse(24, 144);
        using JsonDocument validDocument = JsonDocument.Parse(validJson);
        bool valid = VideoEditV2TransientContract.TryParsePreviewResponse(
            validDocument.RootElement,
            summary,
            plan,
            out VideoEditV2PreviewSet? previews,
            selector,
            "synthetic-source");
        using JsonDocument extraDocument = JsonDocument.Parse(
            validJson[..^1] + ",\"model\":\"forbidden\"}");
        bool extraRejected = !VideoEditV2TransientContract
            .TryParsePreviewResponse(
                extraDocument.RootElement,
                summary,
                plan,
                out _,
                selector,
                "synthetic-source");
        using JsonDocument oversizeDocument = JsonDocument.Parse(
            validJson.Replace(
                "\"encodedBytes\":90",
                "\"encodedBytes\":524289",
                StringComparison.Ordinal));
        bool oversizeRejected = !VideoEditV2TransientContract
            .TryParsePreviewResponse(
                oversizeDocument.RootElement,
                summary,
                plan,
                out _,
                selector,
                "synthetic-source");
        if (!valid || previews is null)
            return false;

        string backendPrompt =
            VideoEditV2TransientContract.OfficialV2VSystemPrompt
            + "Change only the clothing color to blue. Preserve the source except for the requested semantic edit. "
            + VideoEditV2TransientContract.OfficialContinuitySentence;
        const string summaryJa = "指定部分だけを変更し、他は維持します。";
        const string revision =
            VideoEditV2TransientContract.OfficialPromptCompilerRevision;
        if (!VideoEditV2TransientContract
            .TryCreateOfficialRendererSidecarForSmoke(
                backendPrompt,
                "v2v",
                out VideoEditV2RendererSidecar renderer))
        {
            return false;
        }
        bool categoryHeadingRejected = !VideoEditV2TransientContract
            .TryCreateOfficialRendererSidecarForSmoke(
                VideoEditV2TransientContract.OfficialV2VSystemPrompt
                    + "Edit task: change clothing color. "
                    + VideoEditV2TransientContract.OfficialContinuitySentence,
                "v2v",
                out _);
        string digest = VideoEditV2TransientContract.ComputeContextDigest(
            selector,
            plan,
            previews.Previews,
            "人物を保ち、服の色だけを青へ変える",
            backendPrompt,
            summaryJa,
            revision,
            renderer);
        bool crossLanguageDigest = string.Equals(
            digest,
            "6485039b3a85a796a23853da4dc35b0b6b4d040c5c8d8d21c7efa7f37a6ef06c",
            StringComparison.Ordinal);
        string candidateJson = JsonSerializer.Serialize(new
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
                    promptCompilerRevision = renderer.PromptCompilerRevision,
                    rendererPromptSha256 = renderer.RendererPromptSha256,
                },
            },
        });
        using JsonDocument candidateDocument = JsonDocument.Parse(candidateJson);
        bool candidateValid = VideoEditV2TransientContract.TryParseCompileResponse(
            candidateDocument.RootElement,
            selector,
            plan,
            previews.Previews,
            "人物を保ち、服の色だけを青へ変える",
            "synthetic-source",
            "synthetic-context",
            out VideoEditV2CompiledCandidate parsedCandidate);
        using JsonDocument forbiddenDocument = JsonDocument.Parse(
            candidateJson.Replace(
                "\"contextDigest\":",
                "\"png\":\"forbidden\",\"contextDigest\":",
                StringComparison.Ordinal));
        bool forbiddenRejected = !VideoEditV2TransientContract
            .TryParseCompileResponse(
                forbiddenDocument.RootElement,
                selector,
                plan,
                previews.Previews,
                "人物を保ち、服の色だけを青へ変える",
                "synthetic-source",
                "synthetic-context",
                out _);
        using JsonDocument forgedRendererDocument = JsonDocument.Parse(
            candidateJson.Replace(
                "\"taskType\":\"v2v\"",
                "\"taskType\":\"r2v\"",
                StringComparison.Ordinal));
        bool forgedRendererRejected = !VideoEditV2TransientContract
            .TryParseCompileResponse(
                forgedRendererDocument.RootElement,
                selector,
                plan,
                previews.Previews,
                "人物を保ち、服の色だけを青へ変える",
                "synthetic-source",
                "synthetic-context",
                out _);
        bool durableExact = candidateValid
            && VideoEditV2DurableContract.TryBuildEditRequest(
                "X:\\synthetic\\source.mp4",
                selector,
                plan,
                previews.Previews,
                "人物を保ち、服の色だけを青へ変える",
                parsedCandidate,
                new VideoEditV2DurableSettings(
                    AudioPolicy: "preserve",
                    Steps: 20,
                    Strength: 60,
                    MaximumPixelArea: 414_720),
                out JsonElement durableRequest)
            && HasExactVideoEditV2SmokeKeys(
                durableRequest,
                "sourceId",
                "operation",
                "mediaKind",
                "videoTools")
            && durableRequest.GetProperty("videoTools") is JsonElement tools
            && HasExactVideoEditV2SmokeKeys(
                tools,
                "schemaVersion",
                "kind",
                "source",
                "selection",
                "instructionJa",
                "compiled",
                "audioPolicy",
                "steps",
                "strength",
                "maximumPixelArea")
            && !tools.TryGetProperty("style", out _)
            && HasExactVideoEditV2SmokeKeys(
                tools.GetProperty("compiled").GetProperty("renderer"),
                "taskType",
                "guidanceMode",
                "promptCompilerRevision",
                "rendererPromptSha256")
            && tools.GetProperty("compiled").GetProperty("renderer")
                .GetProperty("taskType").GetString() == "v2v"
            && tools.GetProperty("strength").GetInt32() == 60;
        bool invalidDurableRejected = candidateValid
            && !VideoEditV2DurableContract.TryBuildEditRequest(
                "X:\\synthetic\\source.mp4",
                selector,
                plan,
                previews.Previews,
                "人物を保ち、服の色だけを青へ変える",
                parsedCandidate,
                new VideoEditV2DurableSettings(
                    AudioPolicy: "preserve",
                    Steps: 20,
                    Strength: 9,
                    MaximumPixelArea: 414_720),
                out _);
        bool displayedDurableExact =
            VideoEditV2TransientContract.TryCreateDisplayedFileSelector(
                "X:\\synthetic\\source.mp4",
                1_024,
                1_700_000_000_000,
                new string('b', 64),
                out VideoEditV2SourceSelector displayedSelector);
        if (displayedDurableExact)
        {
            string displayedDigest = VideoEditV2TransientContract
                .ComputeContextDigest(
                    displayedSelector,
                    plan,
                    previews.Previews,
                    "人物を保ち、服の色だけを青へ変える",
                    parsedCandidate.BackendPrompt,
                    parsedCandidate.SummaryJa,
                    parsedCandidate.CompilerRevision,
                    parsedCandidate.Renderer);
            VideoEditV2CompiledCandidate displayedCandidate =
                parsedCandidate with { ContextDigest = displayedDigest };
            displayedDurableExact = VideoEditV2DurableContract
                    .TryBuildEditRequest(
                        "X:\\synthetic\\source.mp4",
                        displayedSelector,
                        plan,
                        previews.Previews,
                        "人物を保ち、服の色だけを青へ変える",
                        displayedCandidate,
                        new VideoEditV2DurableSettings(
                            AudioPolicy: "mute",
                            Steps: 40,
                            Strength: 90,
                            MaximumPixelArea: 230_400),
                        out JsonElement displayedRequest)
                && HasExactVideoEditV2SmokeKeys(
                    displayedRequest.GetProperty("videoTools")
                        .GetProperty("source"),
                    "kind",
                    "path",
                    "size",
                    "mtimeMs",
                    "sha256")
                && displayedRequest.GetProperty("videoTools")
                    .GetProperty("audioPolicy").GetString() == "mute";
        }
        using JsonDocument readyHealth = JsonDocument.Parse(
            BuildVideoEditV2SmokeReadyHealthResponse());
        using JsonDocument disabledHealth = JsonDocument.Parse(
            BuildVideoEditV2SmokeDisabledHealthResponse());
        using JsonDocument additiveReadyHealth = JsonDocument.Parse(
            BuildVideoEditV2SmokeReadyHealthResponse().Replace(
                "\"readerReady\":true,",
                "\"readerReady\":true,\"unexpected\":true,",
                StringComparison.Ordinal));
        bool healthVectors = VideoEditV2DurableContract.IsExactReadyHealth(
                readyHealth.RootElement)
            && !VideoEditV2DurableContract.IsExactReadyHealth(
                disabledHealth.RootElement)
            && !VideoEditV2DurableContract.IsExactReadyHealth(
                additiveReadyHealth.RootElement);
        return valid
            && extraRejected
            && oversizeRejected
            && categoryHeadingRejected
            && crossLanguageDigest
            && candidateValid
            && forbiddenRejected
            && forgedRendererRejected
            && durableExact
            && invalidDurableRejected
            && displayedDurableExact
            && healthVectors;
    }

    private static string BuildVideoEditV2SmokeProbeResponse()
        => JsonSerializer.Serialize(new
        {
            action = "probe",
            source = new
            {
                frameCount = 240,
                fpsNumerator = 24,
                fpsDenominator = 1,
                durationMs = 10_000,
                width = 1_280,
                height = 720,
            },
        });

    private static string BuildVideoEditV2SmokeDisabledHealthResponse()
        => JsonSerializer.Serialize(new
        {
            capabilities = new
            {
                videoToolsV2 = new
                {
                    contractId = VideoEditV2DurableContract.ContractId,
                    protocol = VideoEditV2DurableContract.Protocol,
                    readerReady = true,
                    edit = new
                    {
                        writerEnabled = false,
                        backendConfigured = false,
                        runtimeVerified = false,
                        ready = false,
                        state = "disabled",
                        reasonCode =
                            "VIDEO_TOOLS_V2_EDIT_BACKEND_CANARY_REQUIRED",
                    },
                    finish = VideoEditV2SmokeDisabledFeature(
                        "VIDEO_TOOLS_V2_FINISH_RUNTIME_UNPINNED"),
                    finishModes = new
                    {
                        fast = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_FAST_CANARY_REQUIRED"),
                        standard = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_STANDARD_CANARY_REQUIRED"),
                        quality = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_QUALITY_MODE_MAPPING_CANARY_REQUIRED"),
                    },
                },
            },
        });

    private static string BuildVideoEditV2SmokeReadyHealthResponse()
    {
        static string Receipt(string name) => $"receipt-{name}-v1";
        return JsonSerializer.Serialize(new
        {
            capabilities = new
            {
                videoToolsV2 = new
                {
                    contractId = VideoEditV2DurableContract.ContractId,
                    protocol = VideoEditV2DurableContract.Protocol,
                    readerReady = true,
                    edit = new
                    {
                        writerEnabled = true,
                        backendConfigured = true,
                        runtimeVerified = true,
                        ready = true,
                        state = "ready",
                        reasonCode = (string?)null,
                        capabilityRevision =
                            VideoEditV2DurableContract.CapabilityRevision,
                        resolvedBackend = new
                        {
                            backendId =
                                "bernini-r-1.3b-edit-candidate-v1",
                            semanticRole = "semantic-v2v",
                            conditioningKind =
                                "source-video-conditioned-semantic-v2v",
                            genuineSourceVideoConditioning = true,
                            imageGuideRetake = false,
                            modelRevision = "bernini-r-1.3b-v1",
                            workflowRevision = "workflow-v1",
                            promptCompilerRevision = "compiler-v1",
                            timelineMappingRevision = "timeline-v1",
                            deliveryMappingRevision = "delivery-v1",
                        },
                        receipts = new
                        {
                            runtimeReceiptId = Receipt("runtime"),
                            modelReceiptId = Receipt("model"),
                            workflowReceiptId = Receipt("workflow"),
                            promptCompilerReceiptId = Receipt("compiler"),
                            timelineMapperReceiptId = Receipt("timeline"),
                            audioDeliveryReceiptId = Receipt("audio"),
                            qualityCanaryReceiptId = Receipt("quality"),
                            resourceCanaryReceiptId = Receipt("resource"),
                            cancelCanaryReceiptId = Receipt("cancel"),
                            recoveryCanaryReceiptId = Receipt("recovery"),
                            outputValidatorReceiptId = Receipt("output"),
                            receiptSetSha256 = new string('a', 64),
                        },
                        resourceBounds = new
                        {
                            maximumSourceBytes = 536_870_912,
                            maximumSourceDurationMs = 300_000,
                            maximumSourceWidth = 1_920,
                            maximumSourceHeight = 1_080,
                            maximumSourcePixelArea = 2_073_600,
                            maximumSourceFrames = 18_000,
                            allowedSourceFps = new[]
                                { "24/1", "30/1", "60/1" },
                            maximumSelectedDurationMs = 5_000,
                            maximumSelectedFrames = 300,
                            supportedMaximumPixelAreas = new[]
                                { 230_400, 307_200, 414_720 },
                            minimumSteps = 1,
                            maximumSteps = 40,
                            minimumStrength = 10,
                            maximumStrength = 100,
                            maximumConcurrentExecutions = 1,
                            maximumGpuVramBytes = 12_884_901_888L,
                            maximumHostRamBytes = 34_359_738_368L,
                            maximumScratchBytes = 8_589_934_592L,
                            maximumOutputBytes = 536_870_912L,
                            processTimeoutMs = 900_000,
                            cancelGraceMs = 10_000,
                        },
                        outputPolicy = new
                        {
                            revision =
                                "aibos-video-edit-child-mp4-validator-v1",
                            container = "mp4",
                            videoCodec = "h264",
                            pixelFormat = "yuv420p",
                            bitDepth = 8,
                            dynamicRange = "SDR",
                            videoStreamCount = 1,
                            maximumAudioStreamCount = 1,
                            subtitleStreamCount = 0,
                            dataStreamCount = 0,
                            attachmentStreamCount = 0,
                            maximumBytes = 536_870_912L,
                        },
                    },
                    finish = VideoEditV2SmokeDisabledFeature(
                        "VIDEO_TOOLS_V2_FINISH_RUNTIME_UNPINNED"),
                    finishModes = new
                    {
                        fast = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_FAST_CANARY_REQUIRED"),
                        standard = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_STANDARD_CANARY_REQUIRED"),
                        quality = VideoEditV2SmokeDisabledFeature(
                            "VIDEO_TOOLS_V2_FINISH_QUALITY_MODE_MAPPING_CANARY_REQUIRED"),
                    },
                },
            },
        });
    }

    private static object VideoEditV2SmokeDisabledFeature(string reasonCode)
        => new
        {
            writerEnabled = false,
            backendConfigured = false,
            runtimeVerified = false,
            ready = false,
            state = "disabled",
            reasonCode,
        };

    private static string BuildVideoEditV2SmokePreviewResponse(
        int startFrame,
        int endFrameExclusive)
    {
        int middleFrame = (startFrame + endFrameExclusive - 1) / 2;
        int endFrame = endFrameExclusive - 1;
        return JsonSerializer.Serialize(new
        {
            action = "preview",
            source = new
            {
                frameCount = 240,
                fpsNumerator = 24,
                fpsDenominator = 1,
                durationMs = 10_000,
                width = 1_280,
                height = 720,
            },
            previews = new[]
            {
                VideoEditV2SmokePreview("start", startFrame, '1'),
                VideoEditV2SmokePreview("middle", middleFrame, '2'),
                VideoEditV2SmokePreview("end", endFrame, '3'),
            },
        });
    }

    private static object VideoEditV2SmokePreview(
        string role,
        int sourceFrame,
        char decodedHashDigit)
        => new
        {
            role,
            sourceFrame,
            sourcePts = (sourceFrame * 1_000L).ToString(),
            decodedPixelSha256 = new string(decodedHashDigit, 64),
            decoderRevision = "aibos-ffmpeg-rgb24-v1-synthetic",
            mime = "image/png",
            width = 1,
            height = 1,
            encodedBytes = 90,
            encodedSha256 = VideoEditV2SmokePngSha256,
            base64 = VideoEditV2SmokePngBase64,
        };

    private static bool TryGetVideoEditV2SmokeAction(
        JsonElement root,
        out string action)
    {
        action = "";
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("action", out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is string parsed
            && (action = parsed) is not null;
    }

    private static bool TryGetVideoEditV2SmokeSelection(
        JsonElement root,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        return root.TryGetProperty("selection", out JsonElement selection)
            && HasExactVideoEditV2SmokeKeys(
                selection,
                "startFrame",
                "endFrameExclusive")
            && selection.TryGetProperty(
                "startFrame",
                out JsonElement startElement)
            && startElement.TryGetInt32(out start)
            && selection.TryGetProperty(
                "endFrameExclusive",
                out JsonElement endElement)
            && endElement.TryGetInt32(out end);
    }

    private static bool HasExactVideoEditV2SmokeKeys(
        JsonElement value,
        params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = value.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return names.SetEquals(expected);
    }

    private static HttpResponseMessage VideoEditV2SmokeJsonResponse(
        HttpStatusCode statusCode,
        string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string FingerprintVideoEditV2Tree(string root)
    {
        if (!Directory.Exists(root))
            return "missing";
        return string.Join(
            "|",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                    $"{Path.GetRelativePath(root, path)}:{FingerprintVideoEditV2File(path)}"));
    }

    private static string FingerprintVideoEditV2File(string path)
    {
        if (!File.Exists(path))
            return "missing";
        byte[] bytes = File.ReadAllBytes(path);
        return $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }
}

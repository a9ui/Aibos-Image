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
                        const string backendPrompt =
                            "Preserve the subject, background, timing, and camera. Change only the clothing color to blue.";
                        const string summaryJa =
                            "人物・背景・動き・カメラを保ち、服の色だけを青へ変えます。";
                        const string compilerRevision =
                            "aibos-video-edit-compiler-v1";
                        if (!VideoEditV2TransientContract
                            .TryComputeContextDigestFromCompileRequestForSmoke(
                                root,
                                backendPrompt,
                                summaryJa,
                                compilerRevision,
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
                                    model = "forbidden-model",
                                }
                                : new
                                {
                                    backendPrompt,
                                    summaryJa,
                                    compilerRevision,
                                    contextDigest = digest,
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
                        && window.VideoEditV2TrimDisabledForSmoke
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

        const string backendPrompt =
            "Preserve the source except for the requested semantic edit.";
        const string summaryJa = "指定部分だけを変更し、他は維持します。";
        const string revision = "aibos-video-edit-compiler-v1";
        string digest = VideoEditV2TransientContract.ComputeContextDigest(
            selector,
            plan,
            previews.Previews,
            "人物を保ち、服の色だけを青へ変える",
            backendPrompt,
            summaryJa,
            revision);
        bool crossLanguageDigest = string.Equals(
            digest,
            "c9ab8a21bc7eea58a609c408447ece6b547f6c074aea828173ab59740bf19e75",
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
            out _);
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
        return valid
            && extraRejected
            && oversizeRejected
            && crossLanguageDigest
            && candidateValid
            && forbiddenRejected;
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

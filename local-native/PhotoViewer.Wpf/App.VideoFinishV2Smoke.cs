using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureVideoFinishV2Smoke(
        string resultPath,
        IReadOnlyList<string> arguments)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string fixturePath = RequireVideoFinishV2Fixture(arguments);
        string smokeRoot = Directory.CreateTempSubdirectory(
                "aibos-wpf-video-finish-v2-")
            .FullName;
        string sourceRoot = Path.Combine(smokeRoot, "source");
        string storeRoot = Path.Combine(smokeRoot, "stores");
        string sourcePath = Path.Combine(
            sourceRoot,
            "synthetic-finish.mp4");
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
            bool plannerExact = VerifyVideoFinishV2Planner();
            bool requestExact = VerifyVideoFinishV2Request();
            (bool healthExact, bool requestedModeNoFallback) =
                VerifyVideoFinishV2Health(fixturePath);

            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(storeRoot);
            WriteIsoBmffSmokeVideo(sourcePath);
            string sourceBefore = FingerprintVideoEditV2File(sourcePath);
            string sourceSha256 = Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(sourcePath)));
            foreach ((string key, string? value) in environment)
                Environment.SetEnvironmentVariable(key, value);

            string disabledHealth = ReadVideoFinishV2DisabledHealth(
                fixturePath);
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            window = HiddenWindow();
            window.EnableModalVideoTransportStubForSmoke();
            int pathResolverCalls = 0;
            window.SetCanonicalPathResolverForSmoke(path =>
            {
                pathResolverCalls++;
                return Path.GetFullPath(path);
            });

            int probeRequests = 0;
            int healthReads = 0;
            int enqueueRequests = 0;
            int unexpectedRequests = 0;
            bool probeRequestExact = true;
            window.ConfigureModalEnhancementForSmoke(async (request, token) =>
            {
                string route = request.RequestUri?.AbsolutePath ?? "";
                if (request.Method == HttpMethod.Get
                    && string.Equals(
                        route,
                        "/api/enhance/health",
                        StringComparison.Ordinal))
                {
                    healthReads++;
                    return VideoEditV2SmokeJsonResponse(
                        HttpStatusCode.OK,
                        disabledHealth);
                }

                if (request.Method == HttpMethod.Post
                    && string.Equals(
                        route,
                        "/api/enhance/video-prompts/v2/edit/compile",
                        StringComparison.Ordinal))
                {
                    string json = request.Content is null
                        ? ""
                        : await request.Content.ReadAsStringAsync(token);
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(json);
                        JsonElement root = document.RootElement;
                        bool exact = HasExactVideoEditV2SmokeKeys(
                                root,
                                "action",
                                "source")
                            && root.TryGetProperty(
                                "action",
                                out JsonElement action)
                            && action.ValueKind == JsonValueKind.String
                            && action.GetString() == "probe"
                            && root.TryGetProperty(
                                "source",
                                out JsonElement source)
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
                                "size",
                                out JsonElement size)
                            && size.TryGetInt64(out long sourceSize)
                            && sourceSize == new FileInfo(sourcePath).Length
                            && source.TryGetProperty(
                                "sha256",
                                out JsonElement sha)
                            && sha.GetString() == sourceSha256;
                        probeRequestExact &= exact;
                        if (!exact)
                        {
                            unexpectedRequests++;
                            return VideoEditV2SmokeJsonResponse(
                                HttpStatusCode.BadRequest,
                                "{\"error\":\"invalid probe\"}");
                        }
                        probeRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.OK,
                            BuildVideoEditV2SmokeProbeResponse());
                    }
                    catch (JsonException)
                    {
                        probeRequestExact = false;
                        unexpectedRequests++;
                        return VideoEditV2SmokeJsonResponse(
                            HttpStatusCode.BadRequest,
                            "{\"error\":\"invalid json\"}");
                    }
                }

                if (request.Method == HttpMethod.Post)
                    enqueueRequests++;
                else
                    unexpectedRequests++;
                return VideoEditV2SmokeJsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"error\":\"unexpected request\"}");
            });

            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    bool hiddenForImages =
                        !window.VideoFinishV2EntryVisibleForSmoke;
                    ExternalVideoDropSmokeSnapshot drop =
                        await window.DropExternalVideoForSmokeAsync(
                            [sourcePath]);
                    int resolverAfterDrop = pathResolverCalls;
                    string storesBefore = FingerprintVideoEditV2Tree(
                        storeRoot);
                    bool entryVisible = drop.Accepted
                        && window.VideoFinishV2EntryVisibleForSmoke
                        && window.VideoFinishV2ExternalContextEntryForSmoke;

                    bool boardOpened = window.OpenVideoFinishV2ForSmoke();
                    string storesAfterOpen = FingerprintVideoEditV2Tree(
                        storeRoot);
                    bool passiveOpen = boardOpened
                        && probeRequests == 0
                        && healthReads == 0
                        && enqueueRequests == 0
                        && unexpectedRequests == 0
                        && pathResolverCalls == resolverAfterDrop
                        && string.Equals(
                            storesBefore,
                            storesAfterOpen,
                            StringComparison.Ordinal)
                        && window.VideoFinishV2ProbeVisibleForSmoke
                        && !window.VideoFinishV2ReadinessEnabledForSmoke
                        && !window.VideoFinishV2StartEnabledForSmoke
                        && window.VideoFinishV2ModeForSmoke == "standard"
                        && window.VideoFinishV2ScaleForSmoke == 2;

                    bool probed = await window
                        .ProbeVideoFinishV2ForSmokeAsync();
                    bool externalProbeExact = probed
                        && probeRequests == 1
                        && probeRequestExact
                        && healthReads == 0
                        && enqueueRequests == 0
                        && unexpectedRequests == 0
                        && window.VideoFinishV2ReadinessEnabledForSmoke
                        && !window.VideoFinishV2StartEnabledForSmoke
                        && window.VideoFinishV2PlanForSmoke.Contains(
                            "2560×1440",
                            StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(
                            window.VideoFinishV2EstimateForSmoke)
                        && !string.IsNullOrWhiteSpace(
                            window.VideoFinishV2PolicyForSmoke);

                    bool disabledReadiness = !await window
                        .RefreshVideoFinishV2ReadinessForSmokeAsync();
                    int requestsBeforeStart = probeRequests
                        + healthReads
                        + enqueueRequests
                        + unexpectedRequests;
                    bool startRejected = !await window
                        .StartVideoFinishV2ForSmokeAsync();
                    int requestsAfterStart = probeRequests
                        + healthReads
                        + enqueueRequests
                        + unexpectedRequests;
                    string storesAfterStart = FingerprintVideoEditV2Tree(
                        storeRoot);
                    bool currentDisabledNoPublish = disabledReadiness
                        && healthReads == 1
                        && startRejected
                        && window.VideoFinishV2StartAttemptCountForSmoke == 1
                        && !window.VideoFinishV2StartEnabledForSmoke
                        && requestsAfterStart == requestsBeforeStart
                        && enqueueRequests == 0
                        && unexpectedRequests == 0
                        && string.Equals(
                            storesBefore,
                            storesAfterStart,
                            StringComparison.Ordinal);

                    bool explicitScale4Bound = !window
                        .SetVideoFinishV2ModeAndScaleForSmoke(
                            "quality",
                            4)
                        && !window.VideoFinishV2StartEnabledForSmoke;
                    window.CloseVideoFinishV2ForSmoke(stale: false);
                    int callsBeforeProductionEntry = probeRequests
                        + healthReads
                        + enqueueRequests
                        + unexpectedRequests;
                    bool entryRerouted = window
                        .InvokeVideoFinishProductionEntryForSmoke()
                        && window.VideoFinishV2ModeForSmoke == "standard"
                        && window.VideoFinishV2ScaleForSmoke == 2
                        && probeRequests
                            + healthReads
                            + enqueueRequests
                            + unexpectedRequests
                            == callsBeforeProductionEntry;

                    bool escapeClosed = window
                        .InvokeVideoFinishV2EscapeForSmoke();
                    bool reopened = window.OpenVideoFinishV2ForSmoke()
                        && window.VideoFinishV2ProbeVisibleForSmoke
                        && !window.VideoFinishV2ReadinessEnabledForSmoke
                        && !window.VideoFinishV2StartEnabledForSmoke;
                    window.CloseModalForSmoke();
                    bool ancestorCloseCleared =
                        !window.VideoFinishV2BoardVisibleForSmoke;
                    bool lifecycleExact = escapeClosed
                        && reopened
                        && ancestorCloseCleared;

                    bool sourceUntouched = string.Equals(
                        sourceBefore,
                        FingerprintVideoEditV2File(sourcePath),
                        StringComparison.Ordinal);
                    bool storesUntouched = string.Equals(
                        storesBefore,
                        FingerprintVideoEditV2Tree(storeRoot),
                        StringComparison.Ordinal);

                    ok = plannerExact
                        && requestExact
                        && healthExact
                        && requestedModeNoFallback
                        && hiddenForImages
                        && entryVisible
                        && passiveOpen
                        && externalProbeExact
                        && currentDisabledNoPublish
                        && explicitScale4Bound
                        && entryRerouted
                        && lifecycleExact
                        && sourceUntouched
                        && storesUntouched;
                    result = new
                    {
                        ok,
                        requestExact,
                        plannerExact,
                        healthExact,
                        requestedModeNoFallback,
                        passiveOpen,
                        externalProbeExact,
                        currentDisabledNoPublish,
                        entryRerouted,
                        lifecycleExact,
                        sourceUntouched,
                        hiddenForImages,
                        entryVisible,
                        explicitScale4Bound,
                        storesUntouched,
                        probeRequestExact,
                        probeRequests,
                        healthReads,
                        enqueueRequests,
                        unexpectedRequests,
                        pathResolverCalls,
                        resolverAfterDrop,
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
                    RestoreVideoFinishV2SmokeEnvironment(
                        previousEnvironment);
                    WriteVideoFinishV2SmokeResult(resultFullPath, result);
                    TryDeleteVideoFinishV2SmokeRoot(smokeRoot);
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
            RestoreVideoFinishV2SmokeEnvironment(previousEnvironment);
            WriteVideoFinishV2SmokeResult(resultFullPath, result);
            TryDeleteVideoFinishV2SmokeRoot(smokeRoot);
            Shutdown(1);
        }
    }

    private static string RequireVideoFinishV2Fixture(
        IReadOnlyList<string> arguments)
    {
        int index = -1;
        for (int current = 0; current < arguments.Count; current++)
        {
            if (string.Equals(
                    arguments[current],
                    "--fixture",
                    StringComparison.Ordinal))
            {
                index = current;
                break;
            }
        }
        if (index < 0 || index + 1 >= arguments.Count)
            throw new InvalidOperationException("Finish v2 fixture is required.");
        string path = Path.GetFullPath(arguments[index + 1]);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "Finish v2 fixture was not found.",
                path);
    }

    private static bool VerifyVideoFinishV2Planner()
    {
        bool standardValid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                240,
                24,
                1,
                10_000,
                960,
                540,
                "standard",
                2,
                out VideoFinishV2Plan standard,
                out _);
        bool scale4Valid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                300,
                30,
                1,
                10_000,
                960,
                540,
                "fast",
                4,
                out VideoFinishV2Plan scale4,
                out _);
        bool qualityValid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                600,
                60,
                1,
                10_000,
                640,
                360,
                "quality",
                2,
                out _,
                out _);
        bool unsupportedRejected = !global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                250,
                25,
                1,
                10_000,
                640,
                360,
                "standard",
                2,
                out _,
                out VideoFinishV2PlanError unsupported);
        bool boundsRejected = !global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                240,
                24,
                1,
                10_000,
                1_280,
                720,
                "quality",
                4,
                out _,
                out VideoFinishV2PlanError outputBounds);
        return standardValid
            && standard.OutputWidth == 1_920
            && standard.OutputHeight == 1_080
            && standard.OutputPixelArea == 2_073_600
            && standard.EstimatedOutputFrameBytes == 8_294_400
            && scale4Valid
            && scale4.OutputWidth == 3_840
            && scale4.OutputHeight == 2_160
            && qualityValid
            && unsupportedRejected
            && unsupported == VideoFinishV2PlanError.UnsupportedFps
            && boundsRejected
            && outputBounds == VideoFinishV2PlanError.OutputBounds;
    }

    private static bool VerifyVideoFinishV2Request()
    {
        const string producerId = "11111111-2222-4333-8444-555555555555";
        bool managedSelectorValid =
            VideoEditV2TransientContract.TryCreateManagedSelector(
                producerId,
                out VideoEditV2SourceSelector managed);
        bool planValid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                240,
                24,
                1,
                10_000,
                960,
                540,
                "standard",
                2,
                out VideoFinishV2Plan plan,
                out _);
        JsonElement request = default;
        bool requestBuilt = managedSelectorValid
            && planValid
            && VideoFinishV2Contract.TryBuildFinishRequest(
                "X:\\synthetic\\managed-source.mp4",
                managed,
                plan,
                out request);
        if (!requestBuilt
            || !HasExactVideoEditV2SmokeKeys(
                request,
                "sourceId",
                "operation",
                "mediaKind",
                "videoTools")
            || request.GetProperty("operation").GetString() != "video"
            || request.GetProperty("mediaKind").GetString() != "video")
        {
            return false;
        }
        JsonElement tools = request.GetProperty("videoTools");
        JsonElement source = tools.GetProperty("source");
        bool managedExact = HasExactVideoEditV2SmokeKeys(
                tools,
                "schemaVersion",
                "kind",
                "source",
                "mode",
                "scale")
            && tools.GetProperty("schemaVersion").GetInt32() == 2
            && tools.GetProperty("kind").GetString() == "finish"
            && tools.GetProperty("mode").GetString() == "standard"
            && tools.GetProperty("scale").GetInt32() == 2
            && HasExactVideoEditV2SmokeKeys(
                source,
                "kind",
                "sourceVideoJobId")
            && source.GetProperty("kind").GetString()
                == "managed-video-job"
            && source.GetProperty("sourceVideoJobId").GetString()
                == producerId;

        bool displayedExact = VideoEditV2TransientContract
                .TryCreateDisplayedFileSelector(
                    "X:\\synthetic\\displayed-source.mp4",
                    4_096,
                    1_700_000_000_000,
                    new string('b', 64),
                    out VideoEditV2SourceSelector displayed)
            && VideoFinishV2Contract.TryBuildFinishRequest(
                "X:\\synthetic\\displayed-source.mp4",
                displayed,
                plan,
                out JsonElement displayedRequest)
            && HasExactVideoEditV2SmokeKeys(
                displayedRequest.GetProperty("videoTools")
                    .GetProperty("source"),
                "kind",
                "path",
                "size",
                "mtimeMs",
                "sha256");
        return managedExact && displayedExact;
    }

    private static (bool HealthExact, bool RequestedModeNoFallback)
        VerifyVideoFinishV2Health(string fixturePath)
    {
        bool standardValid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                240,
                24,
                1,
                10_000,
                960,
                540,
                "standard",
                2,
                out VideoFinishV2Plan standard,
                out _);
        bool qualityValid = global::PhotoViewer.Wpf.MainWindow
            .TryPlanVideoFinishV2ForSmoke(
                240,
                24,
                1,
                10_000,
                960,
                540,
                "quality",
                2,
                out VideoFinishV2Plan quality,
                out _);
        if (!standardValid || !qualityValid)
        {
            return (false, false);
        }

        string readyJson = BuildVideoFinishV2ReadyHealth(fixturePath);
        using JsonDocument ready = JsonDocument.Parse(readyJson);
        using JsonDocument disabled = JsonDocument.Parse(
            ReadVideoFinishV2DisabledHealth(fixturePath));
        using JsonDocument additive = JsonDocument.Parse(
            readyJson.Replace(
                "\"readerReady\":true,",
                "\"readerReady\":true,\"unexpected\":true,",
                StringComparison.Ordinal));
        bool healthExact = VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "standard",
                standard,
                4_096)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "standard",
                standard,
                exactSourceBytes: null)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "standard",
                standard,
                exactSourceBytes: 0)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "standard",
                standard,
                VideoFinishV2Contract.MaximumSourceBytes + 1)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                disabled.RootElement,
                "standard",
                standard,
                4_096)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                additive.RootElement,
                "standard",
                standard,
                4_096);
        bool requestedModeNoFallback =
            !VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "quality",
                quality,
                4_096)
            && !VideoFinishV2Contract.IsExactReadyHealth(
                ready.RootElement,
                "quality",
                standard,
                4_096);
        return (healthExact, requestedModeNoFallback);
    }

    private static string BuildVideoFinishV2ReadyHealth(string fixturePath)
    {
        JsonObject root = JsonNode.Parse(
                File.ReadAllText(fixturePath))
            ?.AsObject()
            ?? throw new InvalidDataException("Fixture root is missing.");
        JsonObject health = root["health"]?.DeepClone().AsObject()
            ?? throw new InvalidDataException("Fixture health is missing.");
        JsonObject vector = root["finishCapabilityNegativeVectors"]?
                ["shapeCompleteButUnresolvedReceipts"]?.AsObject()
            ?? throw new InvalidDataException(
                "Finish ready-shape vector is missing.");
        JsonObject tools = health["capabilities"]?["videoToolsV2"]
                ?.AsObject()
            ?? throw new InvalidDataException(
                "Fixture Video Tools health is missing.");
        tools["finish"] = vector["overallCapability"]?.DeepClone()
            ?? throw new InvalidDataException(
                "Finish overall shape is missing.");
        JsonObject modes = tools["finishModes"]?.AsObject()
            ?? throw new InvalidDataException("Finish modes are missing.");
        modes["standard"] = vector["modeCapability"]?.DeepClone()
            ?? throw new InvalidDataException(
                "Finish standard shape is missing.");
        return health.ToJsonString();
    }

    private static string ReadVideoFinishV2DisabledHealth(
        string fixturePath)
    {
        using JsonDocument fixture = JsonDocument.Parse(
            File.ReadAllText(fixturePath));
        return fixture.RootElement.GetProperty("health").GetRawText();
    }

    private static void RestoreVideoFinishV2SmokeEnvironment(
        IReadOnlyDictionary<string, string?> previous)
    {
        foreach ((string key, string? value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }

    private static void WriteVideoFinishV2SmokeResult(
        string resultPath,
        object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(
            resultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDeleteVideoFinishV2SmokeRoot(string smokeRoot)
    {
        try
        {
            if (Directory.Exists(smokeRoot))
                Directory.Delete(smokeRoot, recursive: true);
        }
        catch
        {
        }
    }
}

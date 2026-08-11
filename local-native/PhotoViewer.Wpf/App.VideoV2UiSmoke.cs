using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

public partial class App
{
    private async void CaptureVideoV2UiSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        MainWindow? window = null;
        try
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            string? contractArgument = Environment.GetEnvironmentVariable(
                "AIBOS_VIDEO_V2_CONTRACT_PATH");
            if (string.IsNullOrWhiteSpace(contractArgument))
                throw new InvalidDataException(
                    "AIBOS_VIDEO_V2_CONTRACT_PATH is required.");
            using JsonDocument contractDocument = JsonDocument.Parse(
                File.ReadAllBytes(Path.GetFullPath(contractArgument)));
            JsonElement canonicalValidVideo = contractDocument.RootElement
                .GetProperty("readerFixture")
                .GetProperty("jobs")
                .EnumerateArray()
                .Single(job => job.GetProperty("id").GetString()
                    == "valid-h3-video")
                .GetProperty("video");
            string adjacentDurationVideoJson = canonicalValidVideo
                .GetRawText()
                .Replace(
                    "5.166666666666667",
                    "5.166666666666668",
                    StringComparison.Ordinal);
            using JsonDocument adjacentDurationVideo = JsonDocument.Parse(
                adjacentDurationVideoJson);
            bool durationExact = PhotoViewer.Wpf.MainWindow
                    .IsExactMiniMaxH3VideoSnapshotForSmoke(canonicalValidVideo)
                && !PhotoViewer.Wpf.MainWindow.IsExactMiniMaxH3VideoSnapshotForSmoke(
                    adjacentDurationVideo.RootElement);
            string portraitVideoJson = canonicalValidVideo.GetRawText()
                .Replace("\"width\": 864", "\"width\": 512", StringComparison.Ordinal)
                .Replace("\"height\": 480", "\"height\": 768", StringComparison.Ordinal);
            using JsonDocument portraitVideo = JsonDocument.Parse(portraitVideoJson);
            using JsonDocument unalignedVideo = JsonDocument.Parse(
                portraitVideoJson.Replace(
                    "\"width\": 512",
                    "\"width\": 513",
                    StringComparison.Ordinal));
            using JsonDocument oversizedDimensionVideo = JsonDocument.Parse(
                portraitVideoJson.Replace(
                    "\"height\": 768",
                    "\"height\": 1376",
                    StringComparison.Ordinal));
            using JsonDocument oversizedAreaVideo = JsonDocument.Parse(
                portraitVideoJson
                    .Replace(
                        "\"width\": 512",
                        "\"width\": 832",
                        StringComparison.Ordinal)
                    .Replace(
                        "\"height\": 768",
                        "\"height\": 512",
                        StringComparison.Ordinal));
            bool canvasPolicyExact = PhotoViewer.Wpf.MainWindow
                    .IsExactMiniMaxH3VideoSnapshotForSmoke(portraitVideo.RootElement)
                && !PhotoViewer.Wpf.MainWindow.IsExactMiniMaxH3VideoSnapshotForSmoke(
                    unalignedVideo.RootElement)
                && !PhotoViewer.Wpf.MainWindow.IsExactMiniMaxH3VideoSnapshotForSmoke(
                    oversizedDimensionVideo.RootElement)
                && !PhotoViewer.Wpf.MainWindow.IsExactMiniMaxH3VideoSnapshotForSmoke(
                    oversizedAreaVideo.RootElement);

            window = new MainWindow();
            bool wanDefaultVisible = window.WanVideoControlsVisibleForSmoke;
            bool templateSurface = window.VideoPromptTemplateSurfaceForSmoke;
            bool templateIdsExact = window.VideoPromptTemplateIdsForSmoke
                .SequenceEqual(
                [
                    "custom",
                    "dynamic-general",
                    "cute-sexy",
                    "cinematic-camera",
                    "natural-visible",
                ],
                StringComparer.Ordinal);
            bool cuteSexySelected = window.SelectVideoPromptTemplateForSmoke(
                "cute-sexy");
            string cuteSexyPrompt = window.VideoPromptForSmoke;
            bool templateExact = templateSurface
                && templateIdsExact
                && cuteSexySelected
                && string.Equals(
                    window.SelectedVideoPromptTemplateIdForSmoke,
                    "cute-sexy",
                    StringComparison.Ordinal)
                && cuteSexyPrompt.Contains("cute", StringComparison.OrdinalIgnoreCase)
                && cuteSexyPrompt.Contains("seductive", StringComparison.OrdinalIgnoreCase)
                && cuteSexyPrompt.Contains("clearly readable motion", StringComparison.Ordinal)
                && cuteSexyPrompt.Contains("shy inviting expression", StringComparison.Ordinal)
                && cuteSexyPrompt.Contains("distinct alluring pose", StringComparison.Ordinal)
                && cuteSexyPrompt.Contains("do not invent a new hand gesture", StringComparison.OrdinalIgnoreCase);

            window.SetMiniMaxH3CapabilityForSmoke(
                checkedHealth: true,
                ready: false,
                reasonCode: "MINIMAX_H3_WRITER_DISABLED");
            window.SelectVideoModelForSmoke("minimax-h3");
            bool h3UnavailableSafe = !window.VideoModelRunnableForSmoke
                && window.MiniMaxH3SurfaceForSmoke
                && window.MiniMaxH3ReadinessTextForSmoke.Contains(
                    "ジョブ登録は現在無効",
                    StringComparison.Ordinal);
            bool h3UnavailableRunnable = window.VideoModelRunnableForSmoke;
            string[] h3UnavailableSurfaceIssues =
                window.MiniMaxH3SurfaceIssuesForSmoke.ToArray();
            string h3UnavailableReason = window.MiniMaxH3ReadinessTextForSmoke;

            using JsonDocument request = JsonDocument.Parse(
                window.BuildMiniMaxH3EnqueueRequestJsonForSmoke(
                    "  A gentle head turn in dawn light.  "));
            JsonElement requestRoot = request.RootElement;
            bool requestExact = HasExactNames(
                    requestRoot,
                    "sourceId",
                    "operation",
                    "mediaKind",
                    "presetId",
                    "adapterId",
                    "video")
                && ExactString(requestRoot, "operation", "video")
                && ExactString(requestRoot, "mediaKind", "video")
                && ExactString(
                    requestRoot,
                    "presetId",
                    "minimax-h3-i2v-preview-v1")
                && ExactString(
                    requestRoot,
                    "adapterId",
                    "minimax-h3-local-v1")
                && requestRoot.TryGetProperty("video", out JsonElement video)
                && HasExactNames(video, "requested")
                && video.TryGetProperty("requested", out JsonElement requested)
                && HasExactNames(requested, "prompt")
                && ExactString(
                    requested,
                    "prompt",
                    "A gentle head turn in dawn light.");

            string readyHealthJson = CreateVideoV2HealthJson(
                writerEnabled: true,
                ready: true,
                state: "ready",
                reasonCode: null);
            using JsonDocument readyHealth = JsonDocument.Parse(
                readyHealthJson);
            using JsonDocument disabledHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: false,
                    ready: false,
                    state: "disabled",
                    reasonCode: "MINIMAX_H3_WRITER_DISABLED"));
            using JsonDocument invalidSealHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: true,
                    ready: false,
                    state: "unverified",
                    reasonCode: "MINIMAX_H3_RUNTIME_SEAL_INVALID",
                    runtimeSealVerified: false));
            using JsonDocument mismatchedReasonHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: true,
                    ready: false,
                    state: "disabled",
                    reasonCode: "MINIMAX_H3_WRITER_DISABLED"));
            using JsonDocument inconsistentReadyHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: false,
                    ready: true,
                    state: "ready",
                    reasonCode: null));
            using JsonDocument malformedHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: true,
                    ready: true,
                    state: "ready",
                    reasonCode: null).Replace(
                        "minimax-h3-local-v1",
                        "future-h3-backend",
                        StringComparison.Ordinal));
            using JsonDocument malformedCanvasHealth = JsonDocument.Parse(
                CreateVideoV2HealthJson(
                    writerEnabled: true,
                    ready: true,
                    state: "ready",
                    reasonCode: null).Replace(
                        "\"maxPixelArea\":414720",
                        "\"maxPixelArea\":409600",
                        StringComparison.Ordinal));
            using JsonDocument duplicateCapabilitiesHealth = JsonDocument.Parse(
                "{\"capabilities\":{}," + readyHealthJson[1..]);
            using JsonDocument duplicateVideoV2Health = JsonDocument.Parse(
                readyHealthJson.Replace(
                    "\"videoV2\":{",
                    "\"videoV2\":{},\"videoV2\":{",
                    StringComparison.Ordinal));
            bool readyParsed = PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    readyHealth.RootElement,
                    out bool ready,
                    out string? readyReason);
            bool disabledParsed = PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    disabledHealth.RootElement,
                    out bool disabledReady,
                    out string? disabledReason);
            bool invalidSealParsed = PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    invalidSealHealth.RootElement,
                    out bool invalidSealReady,
                    out string? invalidSealReason);
            bool mismatchedRejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    mismatchedReasonHealth.RootElement,
                    out _,
                    out _);
            bool malformedRejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    malformedHealth.RootElement,
                    out _,
                    out _);
            bool malformedCanvasRejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    malformedCanvasHealth.RootElement,
                    out _,
                    out _);
            bool inconsistentReadyRejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    inconsistentReadyHealth.RootElement,
                    out _,
                    out _);
            bool duplicateCapabilitiesRejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    duplicateCapabilitiesHealth.RootElement,
                    out _,
                    out _);
            bool duplicateVideoV2Rejected = !PhotoViewer.Wpf.MainWindow
                .TryParseMiniMaxH3VideoCapabilityForSmoke(
                    duplicateVideoV2Health.RootElement,
                    out _,
                    out _);
            bool healthExact = readyParsed
                && ready
                && readyReason is null
                && disabledParsed
                && !disabledReady
                && string.Equals(
                    disabledReason,
                    "MINIMAX_H3_WRITER_DISABLED",
                    StringComparison.Ordinal)
                && invalidSealParsed
                && !invalidSealReady
                && string.Equals(
                    invalidSealReason,
                    "MINIMAX_H3_RUNTIME_SEAL_INVALID",
                    StringComparison.Ordinal)
                && mismatchedRejected
                && inconsistentReadyRejected
                && malformedRejected
                && malformedCanvasRejected
                && duplicateCapabilitiesRejected
                && duplicateVideoV2Rejected;

            window.SetMiniMaxH3CapabilityForSmoke(
                checkedHealth: true,
                ready: false,
                reasonCode: "MINIMAX_H3_RUNTIME_SEAL_INVALID");
            bool invalidSealReasonVisible = window.MiniMaxH3ReadinessTextForSmoke
                .Contains("seal", StringComparison.OrdinalIgnoreCase);

            window.SetMiniMaxH3CapabilityForSmoke(
                checkedHealth: true,
                ready: true,
                reasonCode: null);
            bool h3ReadySafe = window.VideoModelRunnableForSmoke
                && window.MiniMaxH3SurfaceForSmoke;
            bool h3ReadyRunnable = window.VideoModelRunnableForSmoke;
            string[] h3ReadySurfaceIssues =
                window.MiniMaxH3SurfaceIssuesForSmoke.ToArray();
            window.SelectVideoModelForSmoke("wan22-ti2v-5b");
            bool wanRestored = window.WanVideoControlsVisibleForSmoke
                && window.VideoModelRunnableForSmoke;

            string jobsPath = Environment.GetEnvironmentVariable(
                    "PHOTOVIEWER_WPF_ENHANCEMENT_JOBS_PATH")
                ?? throw new InvalidDataException(
                    "The isolated Enhancement jobs path is required.");
            string pendingInboxPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(jobsPath))!,
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

            string retryHealthMode = "disabled";
            int retryHealthGetCalls = 0;
            int retryPostCalls = 0;
            window.ConfigureModalEnhancementForSmoke((request, _) =>
            {
                string path = request.RequestUri?.AbsolutePath ?? "";
                if (request.Method == HttpMethod.Get
                    && string.Equals(
                        path,
                        "/api/enhance/health",
                        StringComparison.Ordinal))
                {
                    retryHealthGetCalls++;
                    if (retryHealthMode == "unavailable")
                    {
                        throw new HttpRequestException(
                            "synthetic unavailable health");
                    }

                    string healthJson = retryHealthMode switch
                    {
                        "ready" => CreateVideoV2HealthJson(
                            writerEnabled: true,
                            ready: true,
                            state: "ready",
                            reasonCode: null),
                        "malformed" => CreateVideoV2HealthJson(
                                writerEnabled: true,
                                ready: true,
                                state: "ready",
                                reasonCode: null)
                            .Replace(
                                "minimax-h3-local-v1",
                                "future-h3-backend",
                                StringComparison.Ordinal),
                        "seal-invalid" => CreateVideoV2HealthJson(
                            writerEnabled: true,
                            ready: false,
                            state: "unverified",
                            reasonCode: "MINIMAX_H3_RUNTIME_SEAL_INVALID",
                            runtimeSealVerified: false),
                        _ => CreateVideoV2HealthJson(
                            writerEnabled: false,
                            ready: false,
                            state: "disabled",
                            reasonCode: "MINIMAX_H3_WRITER_DISABLED"),
                    };
                    return Task.FromResult(new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            healthJson,
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                if (request.Method == HttpMethod.Post
                    && path.EndsWith("/retry", StringComparison.Ordinal))
                {
                    retryPostCalls++;
                    return Task.FromResult(new HttpResponseMessage(
                        HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent(
                            "{\"error\":\"synthetic lost immediate receipt\"}",
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound));
            });

            var disabledRetry = await window.RetryMiniMaxH3JobForSmokeAsync(
                "h3-disabled-retry");
            int afterDisabled = PendingReservationCount();
            retryHealthMode = "seal-invalid";
            var invalidSealRetry = await window.RetryMiniMaxH3JobForSmokeAsync(
                "h3-seal-invalid-retry");
            int afterInvalidSeal = PendingReservationCount();
            retryHealthMode = "malformed";
            var malformedRetry = await window.RetryMiniMaxH3JobForSmokeAsync(
                "h3-malformed-retry");
            int afterMalformed = PendingReservationCount();
            retryHealthMode = "unavailable";
            var unavailableRetry = await window.RetryMiniMaxH3JobForSmokeAsync(
                "h3-unavailable-retry");
            int afterUnavailable = PendingReservationCount();
            retryHealthMode = "ready";
            var readyRetry = await window.RetryMiniMaxH3JobForSmokeAsync(
                "h3-ready-retry");
            int afterReady = PendingReservationCount();
            bool h3RetryExactHealth = !disabledRetry.Ok
                && !disabledRetry.SavedForDelivery
                && !invalidSealRetry.Ok
                && !invalidSealRetry.SavedForDelivery
                && !malformedRetry.Ok
                && !malformedRetry.SavedForDelivery
                && !unavailableRetry.Ok
                && !unavailableRetry.SavedForDelivery
                && afterDisabled == 0
                && afterInvalidSeal == 0
                && afterMalformed == 0
                && afterUnavailable == 0
                && readyRetry.Ok
                && readyRetry.SavedForDelivery
                && afterReady == 1
                && retryHealthGetCalls == 5
                && retryPostCalls == 1;

            ok = wanDefaultVisible
                && templateExact
                && h3UnavailableSafe
                && requestExact
                && healthExact
                && invalidSealReasonVisible
                && h3ReadySafe
                && wanRestored
                && durationExact
                && canvasPolicyExact
                && h3RetryExactHealth;
            result = new
            {
                ok,
                wanDefaultVisible,
                templateSurface,
                templateIdsExact,
                cuteSexySelected,
                templateExact,
                h3UnavailableSafe,
                h3UnavailableRunnable,
                h3UnavailableSurfaceIssues,
                h3UnavailableReason,
                requestExact,
                healthExact,
                invalidSealReasonVisible,
                duplicateCapabilitiesRejected,
                duplicateVideoV2Rejected,
                h3ReadySafe,
                h3ReadyRunnable,
                h3ReadySurfaceIssues,
                wanRestored,
                durationExact,
                canvasPolicyExact,
                h3RetryExactHealth,
                afterDisabled,
                afterInvalidSeal,
                afterMalformed,
                afterUnavailable,
                afterReady,
                retryHealthGetCalls,
                retryPostCalls,
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
            window?.Close();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(ok ? 0 : 1);
    }

    private static string CreateVideoV2HealthJson(
        bool writerEnabled,
        bool ready,
        string state,
        string? reasonCode,
        bool runtimeSealVerified = true)
        => JsonSerializer.Serialize(new
        {
            capabilities = new
            {
                durableEnqueueInboxV1 = new
                {
                    ready = true,
                    protocolVersion = 1,
                    backendGeneration = "json-v1",
                },
                videoV2 = new
                {
                    contractId = "PV-ENHANCE-VIDEO-002",
                    protocol = "aibos.enhancement-video/v2",
                    readerReady = true,
                    writerEnabled,
                    backendConfigured = true,
                    runtimeSealVerified,
                    runtimeManifestVerified = true,
                    licenseAccepted = true,
                    modelsVerified = true,
                    workflowConfigured = true,
                    gpuCanaryVerified = true,
                    ready,
                    state,
                    reasonCode,
                    presetId = "minimax-h3-i2v-preview-v1",
                    backendId = "minimax-h3-local-v1",
                    workflowRevision = "minimax-h3-comfy-15-node-v1",
                    runtimeMode = "on-demand",
                    profile = new
                    {
                        canvasPolicy = new
                        {
                            kind = "source-aspect-aligned-v1",
                            alignment = 32,
                            minDimension = 256,
                            maxDimension = 1344,
                            maxPixelArea = 414720,
                        },
                        canary = new
                        {
                            width = 864,
                            height = 480,
                        },
                        frameCount = 124,
                        playbackFps = 24,
                        steps = 20,
                        audio = true,
                    },
                },
            },
        });

    private static bool HasExactNames(
        JsonElement element,
        params string[] expected)
    {
        string[] actual = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return actual.Length == expected.Length
            && actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool ExactString(
        JsonElement element,
        string propertyName,
        string expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.Ordinal);
}

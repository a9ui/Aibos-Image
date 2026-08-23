using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoViewer.Wpf;

public partial class App
{
    private async void CaptureVideoToolsSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        try
        {
            string? contractArgument = Environment.GetEnvironmentVariable(
                "AIBOS_VIDEO_TOOLS_CONTRACT_PATH");
            if (string.IsNullOrWhiteSpace(contractArgument))
            {
                throw new InvalidDataException(
                    "AIBOS_VIDEO_TOOLS_CONTRACT_PATH is required.");
            }
            using JsonDocument contractDocument = JsonDocument.Parse(
                File.ReadAllBytes(Path.GetFullPath(contractArgument)));
            JsonElement readerFixtures = contractDocument.RootElement
                .GetProperty("readerFixtures");
            string prompt = contractDocument.RootElement
                .GetProperty("promptInteropFixtures")
                .GetProperty("motionDirectorV1")
                .GetProperty("prompt")
                .GetString() ?? "";
            bool motionDirectorPromptInterop = PhotoViewer.Wpf.MainWindow
                .TryValidateVideoToolsRetakePromptForSmoke(
                    prompt,
                    out string normalizedMotionDirectorPrompt)
                && string.Equals(
                    prompt,
                    normalizedMotionDirectorPrompt,
                    StringComparison.Ordinal);

            bool shortestExact = PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    124,
                    0,
                    124d / 24d,
                    out var shortest)
                && shortest.ActualStartFrame == 0
                && shortest.ActualFrameCount == 124
                && shortest.FirstAnchorFrame == 0
                && shortest.LastAnchorFrame == 123;

            bool leftClamp = PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    362,
                    0,
                    1,
                    out var left)
                && left.SelectionStartFrame == 0
                && left.SelectionEndFrameExclusive == 24
                && left.ActualStartFrame == 0
                && left.ActualFrameCount == 124;
            bool rightClamp = PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    362,
                    14,
                    362d / 24d,
                    out var right)
                && right.ActualStartFrame == 238
                && right.ActualFrameCount == 124
                && right.LastAnchorFrame == 361;

            // 23 selected frames leave 101 padding frames in a 124-frame
            // window. The exact server tie-break puts the odd frame first.
            bool oddPaddingTieBreak = PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    362,
                    120d / 24d,
                    143d / 24d,
                    out var odd)
                && odd.SelectionStartFrame == 120
                && odd.SelectionEndFrameExclusive == 143
                && odd.ActualStartFrame == 69
                && odd.ActualFrameCount == 124
                && odd.FirstAnchorFrame == 69
                && odd.LastAnchorFrame == 192;

            bool invalidSelectionRejected =
                !PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    243,
                    3,
                    3,
                    out _)
                && !PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    240,
                    0,
                    1,
                    out _)
                && !PhotoViewer.Wpf.MainWindow.TryPlanVideoRetakeForSmoke(
                    243,
                    0,
                    11,
                    out _);

            using JsonDocument capability = JsonDocument.Parse(
                CreateVideoToolsHealthJson());
            bool capabilityExact = PhotoViewer.Wpf.MainWindow.TryParseVideoToolsCapabilityForSmoke(
                    capability.RootElement,
                    out var parsedCapability)
                && !parsedCapability.RetakeReady
                && parsedCapability.RetakeState == "disabled"
                && parsedCapability.RetakeReasonCode
                    == "RETAKE_RUNTIME_UNPINNED"
                && !parsedCapability.FinishReady
                && parsedCapability.FinishReasonCode
                    == "FINISH_RUNTIME_UNPINNED";
            using JsonDocument extraCapability = JsonDocument.Parse(
                CreateVideoToolsHealthJson().Replace(
                    "\"readerReady\": true",
                    "\"readerReady\": true, \"unknown\": true",
                    StringComparison.Ordinal));
            using JsonDocument wrongProtocol = JsonDocument.Parse(
                CreateVideoToolsHealthJson().Replace(
                    "aibos-enhancement-video-tools-v1",
                    "aibos-enhancement-video-tools-v2",
                    StringComparison.Ordinal));
            using JsonDocument unknownReason = JsonDocument.Parse(
                CreateVideoToolsHealthJson().Replace(
                    "RETAKE_RUNTIME_UNPINNED",
                    "RETAKE_UNKNOWN",
                    StringComparison.Ordinal));
            bool capabilityMalformedRejected =
                !PhotoViewer.Wpf.MainWindow.TryParseVideoToolsCapabilityForSmoke(
                    extraCapability.RootElement,
                    out _)
                && !PhotoViewer.Wpf.MainWindow.TryParseVideoToolsCapabilityForSmoke(
                    wrongProtocol.RootElement,
                    out _)
                && !PhotoViewer.Wpf.MainWindow.TryParseVideoToolsCapabilityForSmoke(
                    unknownReason.RootElement,
                    out _);

            JsonElement retakeRequest =
                PhotoViewer.Wpf.MainWindow.BuildVideoToolsRetakeRequestForSmoke(
                    @"C:\synthetic\source.png",
                    "11111111-1111-4111-8111-111111111111",
                    odd,
                    prompt,
                    20,
                    414_720);
            JsonElement retake = retakeRequest.GetProperty("videoTools");
            bool retakeRequestExact =
                ExactProperties(
                    retakeRequest,
                    "sourceId",
                    "sourceVideoJobId",
                    "operation",
                    "mediaKind",
                    "videoTools")
                && retakeRequest.GetProperty("sourceVideoJobId").GetString()
                    == "11111111-1111-4111-8111-111111111111"
                && retakeRequest.GetProperty("operation").GetString()
                    == "video"
                && retakeRequest.GetProperty("mediaKind").GetString()
                    == "video"
                && ExactProperties(
                    retake,
                    "schemaVersion",
                    "kind",
                    "selection",
                    "prompt",
                    "steps",
                    "maximumPixelArea")
                && retake.GetProperty("schemaVersion").GetInt32() == 1
                && retake.GetProperty("kind").GetString() == "retake"
                && retake.GetProperty("selection")
                    .GetProperty("startFrame").GetInt32() == 120
                && retake.GetProperty("selection")
                    .GetProperty("endFrameExclusive").GetInt32() == 143
                && !retake.TryGetProperty("actualWindow", out _)
                && !retakeRequest.TryGetProperty("sourcePath", out _)
                && !retakeRequest.TryGetProperty(
                    "sourceManagedOutputPath",
                    out _)
                && !retakeRequest.TryGetProperty("presetId", out _)
                && !retakeRequest.TryGetProperty("adapterId", out _)
                && !retakeRequest.TryGetProperty("video", out _);

            bool unsafeRequestRejected = ThrowsArgumentException(() =>
                    PhotoViewer.Wpf.MainWindow.BuildVideoToolsRetakeRequestForSmoke(
                        "source",
                        "../unsafe",
                        odd,
                        prompt,
                        20,
                        414_720))
                && ThrowsArgumentException(() =>
                    PhotoViewer.Wpf.MainWindow.BuildVideoToolsRetakeRequestForSmoke(
                        "source",
                        "11111111-1111-4111-8111-111111111111",
                        odd,
                        "not an H3 prompt",
                        20,
                        414_720))
                && ThrowsArgumentException(() =>
                    PhotoViewer.Wpf.MainWindow
                        .BuildVideoToolsFinishRequestForSmoke(
                            "source",
                            "valid-h3-video",
                            "faithful"));
            bool uppercaseUuidAccepted =
                PhotoViewer.Wpf.MainWindow
                    .BuildVideoToolsFinishRequestForSmoke(
                        "source",
                        "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE",
                        "faithful")
                    .GetProperty("sourceVideoJobId")
                    .GetString()
                    == "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE";

            bool finishPlan = PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "faithful",
                    864,
                    480,
                    24,
                    124,
                    124d / 24d,
                    audio: true,
                    out var faithful)
                && faithful.Scale == 2
                && faithful.OutputWidth == 1_728
                && faithful.OutputHeight == 960
                && faithful.PlaybackFps == 24
                && faithful.FrameCount == 124
                && faithful.AudioPreserved
                && !faithful.UsesInterpolation
                && PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "detail",
                    640,
                    360,
                    30,
                    180,
                    6,
                    audio: false,
                    out var detail)
                && detail.Mode == "detail"
                && detail.PlaybackFps == 30
                && detail.FrameCount == 180
                && !detail.AudioPreserved
                && !detail.UsesInterpolation;
            bool finishBounds =
                !PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "faithful", 2_000, 100, 24, 124, 124d / 24d,
                    audio: false, out _)
                && !PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "faithful", 100, 1_100, 24, 124, 124d / 24d,
                    audio: false, out _)
                && !PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "faithful", 640, 360, 25, 150, 6,
                    audio: false, out _)
                && !PhotoViewer.Wpf.MainWindow.TryPlanVideoFinishForSmoke(
                    "faithful", 640, 360, 30, 180, 6.1,
                    audio: false, out _);

            JsonElement finishRequest =
                PhotoViewer.Wpf.MainWindow.BuildVideoToolsFinishRequestForSmoke(
                    "source",
                    "22222222-2222-4222-8222-222222222222",
                    "detail");
            JsonElement finish = finishRequest.GetProperty("videoTools");
            bool finishRequestExact = ExactProperties(
                    finish,
                    "schemaVersion",
                    "kind",
                    "mode",
                    "scale")
                && finish.GetProperty("kind").GetString() == "finish"
                && finish.GetProperty("mode").GetString() == "detail"
                && finish.GetProperty("scale").GetInt32() == 2
                && !finishRequest.TryGetProperty("sourcePath", out _)
                && !finishRequest.TryGetProperty("video", out _);

            bool sourceGates =
                PhotoViewer.Wpf.MainWindow.IsVideoToolsSourceWithinBoundsForSmoke(
                    exactManagedVideoValidated: true,
                    isExactMiniMaxH3: true,
                    124d / 24d,
                    24,
                    124,
                    864,
                    480,
                    1_024,
                    "retake")
                && !PhotoViewer.Wpf.MainWindow.IsVideoToolsSourceWithinBoundsForSmoke(
                    exactManagedVideoValidated: false,
                    isExactMiniMaxH3: true,
                    124d / 24d,
                    24,
                    124,
                    864,
                    480,
                    1_024,
                    "retake")
                && !PhotoViewer.Wpf.MainWindow.IsVideoToolsSourceWithinBoundsForSmoke(
                    exactManagedVideoValidated: true,
                    isExactMiniMaxH3: false,
                    6,
                    30,
                    180,
                    640,
                    360,
                    1_024,
                    "retake")
                && PhotoViewer.Wpf.MainWindow.IsVideoToolsSourceWithinBoundsForSmoke(
                    exactManagedVideoValidated: true,
                    isExactMiniMaxH3: false,
                    6,
                    30,
                    180,
                    640,
                    360,
                    1_024,
                    "finish");

            using JsonDocument retakeWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("retake"),
                    "retake-reader-smoke",
                    "queued");
            bool retakeReaderProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        retakeWorkspaceJob.RootElement,
                        out string retakeReaderKind,
                        out string retakePresetSummary,
                        out string retakeOperationLabel,
                        out string retakeDetailText,
                        out bool retakeMutation,
                        out string[] retakeActions)
                && retakeReaderKind == "retake"
                && retakePresetSummary.Contains(
                    "区間を作り直す",
                    StringComparison.Ordinal)
                && retakeOperationLabel.Contains(
                    "RETAKE",
                    StringComparison.Ordinal)
                && retakeDetailText.Contains(
                    "読取専用",
                    StringComparison.Ordinal)
                && !retakeMutation
                && retakeActions.Length == 0;

            using JsonDocument finishWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("finish"),
                    "finish-reader-smoke",
                    "running");
            bool finishReaderProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        finishWorkspaceJob.RootElement,
                        out string finishReaderKind,
                        out string finishPresetSummary,
                        out string finishOperationLabel,
                        out string finishDetailText,
                        out bool finishMutation,
                        out string[] finishActions)
                && finishReaderKind == "finish"
                && finishPresetSummary.Contains(
                    "Faithful",
                    StringComparison.Ordinal)
                && finishOperationLabel.Contains(
                    "VIDEO HQ",
                    StringComparison.Ordinal)
                && finishDetailText.Contains(
                    "読取専用",
                    StringComparison.Ordinal)
                && !finishMutation
                && finishActions.Length == 0;

            using JsonDocument malformedWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("retake"),
                    "malformed-reader-smoke",
                    "queued",
                    static video => video["futureField"] = true);
            bool malformedReaderProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        malformedWorkspaceJob.RootElement,
                        out string malformedReaderKind,
                        out string malformedPresetSummary,
                        out _,
                        out _,
                        out bool malformedMutation,
                        out string[] malformedActions)
                && malformedReaderKind == "protected"
                && malformedPresetSummary.Contains(
                    "保護",
                    StringComparison.Ordinal)
                && !malformedMutation
                && malformedActions.Length == 0;

            using JsonDocument malformedShapeWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("retake"),
                    "malformed-shape-reader-smoke",
                    "running",
                    static video => video["source"]!["signature"] = "invalid");
            bool malformedShapeProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        malformedShapeWorkspaceJob.RootElement,
                        out string malformedShapeKind,
                        out _,
                        out _,
                        out _,
                        out bool malformedShapeMutation,
                        out string[] malformedShapeActions)
                && malformedShapeKind == "protected"
                && !malformedShapeMutation
                && malformedShapeActions.Length == 0;

            using JsonDocument futureWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("finish"),
                    "future-reader-smoke",
                    "queued",
                    static video =>
                    {
                        video["schemaVersion"] = 2;
                        video["protocol"] =
                            "aibos-enhancement-video-tools-v2";
                    });
            bool futureReaderProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        futureWorkspaceJob.RootElement,
                        out string futureReaderKind,
                        out _,
                        out _,
                        out _,
                        out bool futureMutation,
                        out string[] futureActions)
                && futureReaderKind == "protected"
                && !futureMutation
                && futureActions.Length == 0;
            using JsonDocument operationMismatchWorkspaceJob =
                CreateVideoToolsWorkspaceJob(
                    readerFixtures.GetProperty("finish"),
                    "operation-mismatch-reader-smoke",
                    "queued",
                    mutateJob: static job => job["operation"] = "upscale");
            bool operationMismatchProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsWorkspacePresentationForSmoke(
                        operationMismatchWorkspaceJob.RootElement,
                        out string operationMismatchKind,
                        out _,
                        out _,
                        out _,
                        out bool operationMismatchMutation,
                        out string[] operationMismatchActions)
                && operationMismatchKind == "protected"
                && !operationMismatchMutation
                && operationMismatchActions.Length == 0;
            bool readerSnapshots = retakeReaderProtected
                && finishReaderProtected
                && malformedReaderProtected
                && malformedShapeProtected
                && futureReaderProtected
                && operationMismatchProtected;
            const string dependencyProducerId =
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            using JsonDocument producerOnlyDependency =
                CreateVideoToolsDependencyPayload(
                    readerFixtures.GetProperty("finish"),
                    dependencyProducerId,
                    includeChild: false);
            bool producerInitiallyDeletable = PhotoViewer.Wpf.MainWindow
                    .TryReadEnhancementOutputDependencyForSmoke(
                        producerOnlyDependency.RootElement,
                        dependencyProducerId,
                        out bool baselineDependencyProtected,
                        out bool baselineCanDelete)
                && !baselineDependencyProtected
                && baselineCanDelete;
            using JsonDocument activeChildDependency =
                CreateVideoToolsDependencyPayload(
                    readerFixtures.GetProperty("finish"),
                    dependencyProducerId,
                    includeChild: true);
            bool activeVideoToolsDependencyProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadEnhancementOutputDependencyForSmoke(
                        activeChildDependency.RootElement,
                        dependencyProducerId,
                        out bool childDependencyProtected,
                        out bool childCanDelete)
                && childDependencyProtected
                && !childCanDelete;
            using JsonDocument mismatchedChildDependency =
                CreateVideoToolsDependencyPayload(
                    readerFixtures.GetProperty("finish"),
                    dependencyProducerId,
                    includeChild: true,
                    operationMismatch: true);
            bool mismatchedVideoDependencyProtected = PhotoViewer.Wpf.MainWindow
                    .TryReadEnhancementOutputDependencyForSmoke(
                        mismatchedChildDependency.RootElement,
                        dependencyProducerId,
                        out bool mismatchedDependencyProtected,
                        out bool mismatchedCanDelete)
                && mismatchedDependencyProtected
                && !mismatchedCanDelete;
            bool videoProducerDependencyGated = producerInitiallyDeletable
                && activeVideoToolsDependencyProtected
                && mismatchedVideoDependencyProtected;

            int healthGets = 0;
            int mutationRequests = 0;
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            PhotoViewer.Wpf.MainWindow window = HiddenWindow();
            bool japaneseLocaleApplied = window.SetUiLanguageForSmoke(
                UiLanguageResources.Japanese);
            window.ConfigureModalEnhancementForSmoke((request, _) =>
            {
                if (request.Method == HttpMethod.Get
                    && request.RequestUri?.AbsolutePath
                        == "/api/enhance/health")
                {
                    healthGets++;
                    return Task.FromResult(new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CreateVideoToolsHealthJson(),
                            Encoding.UTF8,
                            "application/json"),
                    });
                }

                mutationRequests++;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.MethodNotAllowed));
            });
            window.ConfigureVideoToolsSourceForSmoke(
                "11111111-1111-4111-8111-111111111111",
                isExactMiniMaxH3: true,
                124d / 24d,
                24,
                124,
                864,
                480,
                audio: true,
                prompt);
            window.Show();
            bool retakeOpened = window.OpenVideoToolsBoardForSmoke("retake");
            await window.RefreshVideoToolsCapabilityForSmokeAsync();
            bool retakePreview = window.VideoToolsBoardVisibleForSmoke
                && !window.VideoToolsStartEnabledForSmoke
                && window.VideoToolsPlanForSmoke.Contains(
                    "完成動画",
                    StringComparison.Ordinal)
                && window.VideoToolsPlanForSmoke.Contains(
                    "元音声",
                    StringComparison.Ordinal);
            bool finishOpened = window.OpenVideoToolsBoardForSmoke("finish");
            await window.RefreshVideoToolsCapabilityForSmokeAsync();
            bool finishPreview = window.VideoToolsBoardVisibleForSmoke
                && !window.VideoToolsStartEnabledForSmoke
                && window.VideoToolsPlanForSmoke.Contains(
                    "フレーム補間は行いません",
                    StringComparison.Ordinal);
            bool englishLocaleApplied = window.SetUiLanguageForSmoke(
                UiLanguageResources.English);
            bool englishRetakeOpened = window.OpenVideoToolsBoardForSmoke(
                "retake");
            await window.RefreshVideoToolsCapabilityForSmokeAsync();
            bool englishRetakePresentation = englishRetakeOpened
                && window.VideoToolsTitleForSmoke == "Retake interval"
                && window.VideoToolsSourceSummaryForSmoke.StartsWith(
                    "Input:",
                    StringComparison.Ordinal)
                && window.VideoToolsPlanForSmoke.Contains(
                    "The complete video preserves",
                    StringComparison.Ordinal)
                && window.VideoToolsPlanForSmoke.Contains(
                    "original audio",
                    StringComparison.Ordinal)
                && window.VideoToolsStatusForSmoke.Contains(
                    "not pinned and verified",
                    StringComparison.Ordinal)
                && window.VideoToolsCanvasLabelsForSmoke.SequenceEqual(
                    [
                        "Light · 230,400px",
                        "Standard · 307,200px",
                        "High quality · 414,720px",
                    ]);
            bool englishFinishOpened = window.OpenVideoToolsBoardForSmoke(
                "finish");
            await window.RefreshVideoToolsCapabilityForSmokeAsync();
            bool englishFinishPresentation = englishFinishOpened
                && window.VideoToolsTitleForSmoke == "Enhance video"
                && window.VideoToolsPlanForSmoke.Contains(
                    "It does not interpolate frames.",
                    StringComparison.Ordinal)
                && window.VideoToolsStatusForSmoke.Contains(
                    "not pinned and verified",
                    StringComparison.Ordinal);
            bool localeFocused = japaneseLocaleApplied
                && englishLocaleApplied
                && englishRetakePresentation
                && englishFinishPresentation;
            bool passiveOpen = retakeOpened
                && finishOpened
                && retakePreview
                && finishPreview
                && localeFocused
                && healthGets >= 2
                && mutationRequests == 0;
            window.Close();

            ok = shortestExact
                && leftClamp
                && rightClamp
                && oddPaddingTieBreak
                && invalidSelectionRejected
                && capabilityExact
                && capabilityMalformedRejected
                && motionDirectorPromptInterop
                && retakeRequestExact
                && unsafeRequestRejected
                && uppercaseUuidAccepted
                && finishPlan
                && finishBounds
                && finishRequestExact
                && sourceGates
                && readerSnapshots
                && videoProducerDependencyGated
                && localeFocused
                && passiveOpen;
            result = new
            {
                ok,
                shortestExact,
                leftClamp,
                rightClamp,
                oddPaddingTieBreak,
                invalidSelectionRejected,
                capabilityExact,
                capabilityMalformedRejected,
                motionDirectorPromptInterop,
                retakeRequestExact,
                unsafeRequestRejected,
                uppercaseUuidAccepted,
                finishPlan,
                finishBounds,
                finishRequestExact,
                sourceGates,
                readerSnapshots,
                retakeReaderProtected,
                finishReaderProtected,
                malformedReaderProtected,
                malformedShapeProtected,
                futureReaderProtected,
                operationMismatchProtected,
                videoProducerDependencyGated,
                producerInitiallyDeletable,
                activeVideoToolsDependencyProtected,
                mismatchedVideoDependencyProtected,
                localeFocused,
                englishRetakePresentation,
                englishFinishPresentation,
                passiveOpen,
                healthGets,
                mutationRequests,
                enqueueCallSites = mutationRequests,
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

        Directory.CreateDirectory(Path.GetDirectoryName(resultFullPath)!);
        File.WriteAllText(
            resultFullPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(ok ? 0 : 1);
    }

    private static string CreateVideoToolsHealthJson()
        => """
        {
          "capabilities": {
            "videoToolsV1": {
              "contractId": "PV-ENHANCE-VIDEO-TOOLS-001",
              "protocol": "aibos-enhancement-video-tools-v1",
              "readerReady": true,
              "retake": {
                "writerEnabled": false,
                "backendConfigured": false,
                "runtimeVerified": false,
                "ready": false,
                "state": "disabled",
                "reasonCode": "RETAKE_RUNTIME_UNPINNED"
              },
              "finish": {
                "writerEnabled": false,
                "backendConfigured": false,
                "runtimeVerified": false,
                "ready": false,
                "state": "disabled",
                "reasonCode": "FINISH_RUNTIME_UNPINNED"
              }
            }
          }
        }
        """;

    private static JsonDocument CreateVideoToolsWorkspaceJob(
        JsonElement fixture,
        string id,
        string status,
        Action<JsonObject>? mutateVideo = null,
        Action<JsonObject>? mutateJob = null)
    {
        JsonObject job = JsonNode.Parse(
                fixture.GetProperty("job").GetRawText())!
            .AsObject();
        JsonObject video = JsonNode.Parse(
                fixture.GetProperty("video").GetRawText())!
            .AsObject();
        mutateVideo?.Invoke(video);
        job["id"] = id;
        job["status"] = status;
        job["sourceId"] = "synthetic/source.png";
        job["sourcePath"] = @"C:\synthetic\source.png";
        job["createdAt"] = "2026-08-24T00:00:00.000Z";
        job["updatedAt"] = "2026-08-24T00:00:01.000Z";
        job["video"] = video;
        mutateJob?.Invoke(job);
        return JsonDocument.Parse(job.ToJsonString());
    }

    private static JsonDocument CreateVideoToolsDependencyPayload(
        JsonElement fixture,
        string producerId,
        bool includeChild,
        bool operationMismatch = false)
    {
        var producer = new JsonObject
        {
            ["id"] = producerId,
            ["sourceId"] = "synthetic/source.png",
            ["sourcePath"] = @"C:\synthetic\source.png",
            ["operation"] = "video",
            ["mediaKind"] = "video",
            ["presetId"] = "minimax-h3-i2v-preview-v1",
            ["adapterId"] = "minimax-h3-local-v1",
            ["status"] = "succeeded",
            ["progress"] = 100,
            ["outputPath"] = @"C:\synthetic\Videos\producer.mp4",
            ["createdAt"] = "2026-08-24T00:00:00.000Z",
            ["updatedAt"] = "2026-08-24T00:00:01.000Z",
        };
        var jobs = new JsonArray(producer);
        if (includeChild)
        {
            using JsonDocument childDocument = CreateVideoToolsWorkspaceJob(
                fixture,
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                "queued",
                mutateJob: job =>
                {
                    job["sourceVideoJobId"] = producerId.ToUpperInvariant();
                    if (operationMismatch)
                        job["operation"] = "upscale";
                });
            jobs.Add(JsonNode.Parse(childDocument.RootElement.GetRawText()));
        }
        return JsonDocument.Parse(
            new JsonObject { ["jobs"] = jobs }.ToJsonString());
    }

    private static bool ExactProperties(
        JsonElement element,
        params string[] names)
    {
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            count++;
            if (!expected.Remove(property.Name))
                return false;
        }
        return count == names.Length && expected.Count == 0;
    }

    private static bool ThrowsArgumentException(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

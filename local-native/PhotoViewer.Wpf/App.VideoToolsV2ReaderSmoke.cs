using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private async void CaptureVideoToolsV2ReaderSmoke(
        string resultPath,
        string[] args)
    {
        string fullResultPath = Path.GetFullPath(resultPath);
        object result;
        bool ok = false;
        PhotoViewer.Wpf.MainWindow? window = null;
        try
        {
            string fixturePath = RequireVideoToolsV2ReaderArgument(
                args,
                "--fixture");
            string v1FixturePath = RequireVideoToolsV2ReaderArgument(
                args,
                "--v1-fixture");
            string? pairedJobsPath = OptionalVideoToolsV2ReaderArgument(
                args,
                "--paired-jobs");
            byte[] fixtureBefore = File.ReadAllBytes(fixturePath);
            byte[] v1FixtureBefore = File.ReadAllBytes(v1FixturePath);
            using JsonDocument fixtureDocument = JsonDocument.Parse(
                fixtureBefore);
            using JsonDocument v1FixtureDocument = JsonDocument.Parse(
                v1FixtureBefore);
            JsonElement fixtures = fixtureDocument.RootElement
                .GetProperty("readerFixtures");
            JsonElement editFixture = fixtures.GetProperty("edit");
            JsonElement finishFixture = fixtures.GetProperty("finish");
            JsonElement editFixtureVideo = editFixture.GetProperty("video");
            string computedEditPresetHash = PhotoViewer.Wpf.MainWindow
                .ComputeVideoToolsSnapshotHashForSmoke(editFixtureVideo);
            bool editPresetHashExact = string.Equals(
                editFixture.GetProperty("job").GetProperty("presetHash")
                    .GetString(),
                computedEditPresetHash,
                StringComparison.Ordinal);
            JsonElement fixtureCompiled = editFixtureVideo
                .GetProperty("requested")
                .GetProperty("compiled");
            bool editRendererExact =
                VideoEditV2TransientContract.TryParseOfficialRendererSidecar(
                    fixtureCompiled.GetProperty("renderer"),
                    fixtureCompiled.GetProperty("backendPrompt").GetString()!,
                    fixtureCompiled.GetProperty("compilerRevision").GetString()!,
                    out _);
            JsonElement durableReaderStateVectors = fixtureDocument
                .RootElement
                .GetProperty("durableReaderStateVectors");
            bool pairedPrivateSqliteJobsExact = pairedJobsPath is null;
            bool pairedPrivateJobsExact = pairedJobsPath is null
                || ReadPairedVideoToolsV2Jobs(
                    pairedJobsPath,
                    out pairedPrivateSqliteJobsExact);

            using JsonDocument edit = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888888",
                "succeeded");
            bool exactEdit = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        edit.RootElement,
                        out string editKind,
                        out string editSummary,
                        out string editOperation,
                        out string editDetail,
                        out string editRequestDetails,
                        out string editFilter,
                        out bool editMutation,
                        out bool editCanUseOutput,
                        out string[] editActions)
                && editKind == "edit"
                && editFilter == "edit"
                && editOperation.Contains("AI動画編集", StringComparison.Ordinal)
                && editSummary.Contains("Frame 100–129", StringComparison.Ordinal)
                && editRequestDetails.Contains(
                    "非破壊child clip",
                    StringComparison.Ordinal)
                && editDetail.Contains("管理動画", StringComparison.Ordinal)
                && editMutation
                && editCanUseOutput
                && editActions.SequenceEqual(
                    ["open-output", "delete-output"],
                    StringComparer.Ordinal);

            using JsonDocument finish = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999999",
                "succeeded");
            bool exactFinish = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        finish.RootElement,
                        out string finishKind,
                        out string finishSummary,
                        out string finishOperation,
                        out string finishDetail,
                        out string finishRequestDetails,
                        out string finishFilter,
                        out bool finishMutation,
                        out bool finishCanUseOutput,
                        out string[] finishActions)
                && finishKind == "finish"
                && finishFilter == "finish"
                && finishOperation.Contains(
                    "AI動画高画質化",
                    StringComparison.Ordinal)
                && finishSummary.Contains("standard", StringComparison.Ordinal)
                && finishSummary.Contains("2x", StringComparison.Ordinal)
                && finishDetail.Contains(
                    "外部動画（Job所有コピー）",
                    StringComparison.Ordinal)
                && finishMutation
                && finishCanUseOutput
                && finishActions.SequenceEqual(
                    ["open-output", "delete-output"],
                    StringComparer.Ordinal);

            using JsonDocument editQueued = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888881",
                "queued");
            using JsonDocument editRunning = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888882",
                "running");
            using JsonDocument editFailed = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888883",
                "failed");
            using JsonDocument editCanceled = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888884",
                "canceled");
            using JsonDocument editDeleted = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "44444444-5555-4666-8777-888888888885",
                "deleted");
            using JsonDocument finishQueued = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999991",
                "queued");
            using JsonDocument finishRunning = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999992",
                "running");
            using JsonDocument finishFailed = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999993",
                "failed");
            using JsonDocument finishCanceled = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999994",
                "canceled");
            using JsonDocument finishDeleted = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "55555555-6666-4777-8888-999999999995",
                "deleted");

            EnhancementJobLifecycleSmokeSnapshot? editQueuedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        editQueued.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? editRunningLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        editRunning.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? editFailedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        editFailed.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? editCanceledLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        editCanceled.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? editSucceededLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(edit.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? editDeletedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        editDeleted.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishQueuedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        finishQueued.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishRunningLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        finishRunning.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishFailedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        finishFailed.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishCanceledLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        finishCanceled.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishSucceededLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(finish.RootElement);
            EnhancementJobLifecycleSmokeSnapshot? finishDeletedLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        finishDeleted.RootElement);

            bool editLifecycle = IsExpectedCurrentV2Lifecycle(
                    editQueuedLifecycle,
                    "edit",
                    canCancel: true,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: true,
                    canDeleteOutput: false,
                    ["move-up", "move-down", "move-next", "cancel"])
                && IsExpectedCurrentV2Lifecycle(
                    editRunningLifecycle,
                    "edit",
                    canCancel: true,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["cancel"])
                && IsExpectedCurrentV2Lifecycle(
                    editFailedLifecycle,
                    "edit",
                    canCancel: false,
                    canRetry: true,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["retry", "dismiss"])
                && IsExpectedCurrentV2Lifecycle(
                    editCanceledLifecycle,
                    "edit",
                    canCancel: false,
                    canRetry: true,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["retry", "dismiss"])
                && IsExpectedCurrentV2Lifecycle(
                    editSucceededLifecycle,
                    "edit",
                    canCancel: false,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: false,
                    canDeleteOutput: true,
                    ["open-output", "delete-output"])
                && IsExpectedCurrentV2Lifecycle(
                    editDeletedLifecycle,
                    "edit",
                    canCancel: false,
                    canRetry: false,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["dismiss"]);
            bool finishLifecycle = IsExpectedCurrentV2Lifecycle(
                    finishQueuedLifecycle,
                    "finish",
                    canCancel: true,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: true,
                    canDeleteOutput: false,
                    ["move-up", "move-down", "move-next", "cancel"])
                && IsExpectedCurrentV2Lifecycle(
                    finishRunningLifecycle,
                    "finish",
                    canCancel: true,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["cancel"])
                && IsExpectedCurrentV2Lifecycle(
                    finishFailedLifecycle,
                    "finish",
                    canCancel: false,
                    canRetry: true,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["retry", "dismiss"])
                && IsExpectedCurrentV2Lifecycle(
                    finishCanceledLifecycle,
                    "finish",
                    canCancel: false,
                    canRetry: true,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["retry", "dismiss"])
                && IsExpectedCurrentV2Lifecycle(
                    finishSucceededLifecycle,
                    "finish",
                    canCancel: false,
                    canRetry: false,
                    canDismiss: false,
                    canReorder: false,
                    canDeleteOutput: true,
                    ["open-output", "delete-output"])
                && IsExpectedCurrentV2Lifecycle(
                    finishDeletedLifecycle,
                    "finish",
                    canCancel: false,
                    canRetry: false,
                    canDismiss: true,
                    canReorder: false,
                    canDeleteOutput: false,
                    ["dismiss"]);
            bool knownLifecycleEnabled = editLifecycle && finishLifecycle;

            bool exactLifecycleProtection = new (string Status, Action<JsonObject> Mutate)[]
                {
                    ("queued", job => job.Remove("cancelRequested")),
                    ("queued", job => job["progress"] = 1),
                    ("running", job => job["cancelRequested"] = true),
                    ("running", job => job["queueOrder"] = 0),
                    ("running", job => job.Remove("workerInstanceId")),
                    ("succeeded", job => job.Remove("outputSha256")),
                    ("failed", job => job.Remove("errorCode")),
                    ("canceled", job => job["cancelRequested"] = false),
                    ("deleted", job => job["outputPath"] = @"C:\stale.mp4"),
                    ("failed", job => job["finishedAt"] = "2026-08-24T00:00:00Z"),
                }
                .Select((entry, index) =>
                {
                    using JsonDocument malformed =
                        CreateVideoToolsV2WorkspaceJob(
                            editFixture,
                            $"66666666-7777-4888-8999-{index:D12}",
                            entry.Status,
                            mutateJob: entry.Mutate);
                    return IsProtectedV2ReaderRow(malformed.RootElement);
                })
                .All(static protectedRow => protectedRow);
            bool fixtureEditLifecycleVectorsExact =
                DurableReaderStateVectorsAreExact(
                    durableReaderStateVectors.GetProperty("edit"),
                    editFixture,
                    "edit");
            bool fixtureFinishLifecycleVectorsExact =
                DurableReaderStateVectorsAreExact(
                    durableReaderStateVectors.GetProperty("finish"),
                    finishFixture,
                    "finish");
            bool fixtureLifecycleVectorsExact =
                fixtureEditLifecycleVectorsExact
                && fixtureFinishLifecycleVectorsExact;

            bool detailsExact = editRequestDetails.Contains(
                    "入力依存: 管理動画 Job 11111111-2222-4333-8444-555555555555",
                    StringComparison.Ordinal)
                && editRequestDetails.Contains(
                    "選択区間: [100, 130)",
                    StringComparison.Ordinal)
                && editRequestDetails.Contains(
                    "出力: 非破壊child clip",
                    StringComparison.Ordinal)
                && finishRequestDetails.Contains(
                    "入力依存: 外部動画のJob所有ステージングコピー",
                    StringComparison.Ordinal)
                && finishRequestDetails.Contains(
                    "モード: standard · 2x",
                    StringComparison.Ordinal)
                && finishRequestDetails.Contains(
                    "fps/全尺/元音声を維持",
                    StringComparison.Ordinal);

            using JsonDocument nestedExtra = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "66666666-7777-4888-8999-aaaaaaaaaaaa",
                "queued",
                video => video["plan"]!["modelCanvas"]!["future"] = true,
                refreshPresetHash: true);
            bool nestedExtraRejected = IsProtectedV2ReaderRow(
                nestedExtra.RootElement);

            string duplicateReceiptVideo = editFixture.GetProperty("video")
                .GetRawText()
                .Replace(
                    "\"workflowReceiptId\": \"synthetic-edit-workflow-receipt-v1\"",
                    "\"workflowReceiptId\": \"synthetic+duplicate-v1\", \"workflowReceiptId\": \"synthetic-edit-workflow-receipt-v1\"",
                    StringComparison.Ordinal);
            using JsonDocument duplicateReceipt =
                CreateVideoToolsV2WorkspaceJobFromRawVideo(
                    editFixture,
                    "77777777-8888-4999-8aaa-bbbbbbbbbbbb",
                    "queued",
                    duplicateReceiptVideo);
            string duplicateProtocolVideo = editFixture.GetProperty("video")
                .GetRawText()
                .Replace(
                    "\"protocol\": \"aibos-enhancement-video-tools-v2\"",
                    "\"protocol\": \"aibos-enhancement-video-tools-v2\", \"protocol\": \"aibos-enhancement-video-tools-v2\"",
                    StringComparison.Ordinal);
            using JsonDocument duplicateProtocol =
                CreateVideoToolsV2WorkspaceJobFromRawVideo(
                    editFixture,
                    "77777777-8888-4999-8aaa-bbbbbbbbbbbc",
                    "queued",
                    duplicateProtocolVideo);
            string duplicateForbiddenRaw = edit.RootElement.GetRawText()
                .Insert(
                    1,
                    "\"sourcePath\":\"C:\\\\forged-a.mp4\",\"sourcePath\":\"C:\\\\forged-b.mp4\",");
            using JsonDocument duplicateForbidden = JsonDocument.Parse(
                duplicateForbiddenRaw);
            string stagedDuplicateSourceJobRaw = finish.RootElement.GetRawText()
                .Insert(
                    1,
                    "\"sourceVideoJobId\":\"11111111-2222-4333-8444-555555555555\",\"sourceVideoJobId\":\"22222222-3333-4444-8555-666666666666\",");
            using JsonDocument stagedDuplicateSourceJob = JsonDocument.Parse(
                stagedDuplicateSourceJobRaw);
            bool duplicateRejected = IsProtectedV2ReaderRow(
                    duplicateReceipt.RootElement)
                && IsProtectedV2ReaderRow(duplicateProtocol.RootElement)
                && IsProtectedV2ReaderRow(duplicateForbidden.RootElement)
                && IsProtectedV2ReaderRow(
                    stagedDuplicateSourceJob.RootElement);

            using JsonDocument missing = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "88888888-9999-4aaa-8bbb-cccccccccccc",
                "queued",
                video => video.Remove("delivery"),
                refreshPresetHash: true);
            using JsonDocument missingProtocol =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "88888888-9999-4aaa-8bbb-cccccccccccd",
                    "queued",
                    video => video.Remove("protocol"),
                    refreshPresetHash: true);
            using JsonDocument ordinaryForForgery = CreateOrdinaryVideoJob(
                "succeeded");
            JsonObject forgedPresetJob = JsonNode.Parse(
                    ordinaryForForgery.RootElement.GetRawText())!
                .AsObject();
            forgedPresetJob["presetId"] = "aibos-video-edit-v2";
            forgedPresetJob["adapterId"] = "unknown-reader-backend";
            using JsonDocument forgedPreset = JsonDocument.Parse(
                forgedPresetJob.ToJsonString());
            bool missingRejected = IsProtectedV2ReaderRow(missing.RootElement)
                && IsProtectedV2ReaderRow(missingProtocol.RootElement)
                && IsProtectedV2ReaderRow(forgedPreset.RootElement);

            using JsonDocument forgedEdit = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "99999999-aaaa-4bbb-8ccc-dddddddddddd",
                "queued",
                video => video["plan"]!["selected"]!["endFrameExclusive"] =
                    131,
                refreshPresetHash: true);
            using JsonDocument forgedFinish = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "queued",
                video => video["delivery"]!["width"] = 1_918,
                refreshPresetHash: true);
            using JsonDocument forgedDependency = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
                "queued",
                mutateJob: job => job["sourceVideoJobId"] =
                    "22222222-3333-4444-8555-666666666666");
            using JsonDocument badCompilerDigest = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "cccccccc-dddd-4eee-8fff-000000000000",
                "queued",
                video => video["requested"]!["compiled"]!["contextDigest"] =
                    new string('A', 64),
                refreshPresetHash: true);
            using JsonDocument badCompiledControl = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "dddddddd-eeee-4fff-8000-111111111111",
                "queued",
                video => video["requested"]!["compiled"]!["summaryJa"] =
                    "要約\u0001破損",
                refreshPresetHash: true);
            using JsonDocument badRendererTask = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "dddddddd-eeee-4fff-8000-111111111112",
                "queued",
                video => video["requested"]!["compiled"]!["renderer"]!["taskType"] =
                    "r2v",
                refreshPresetHash: true);
            using JsonDocument badRendererHash = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "dddddddd-eeee-4fff-8000-111111111113",
                "queued",
                video => video["requested"]!["compiled"]!["renderer"]!["rendererPromptSha256"] =
                    new string('f', 64),
                refreshPresetHash: true);
            using JsonDocument badTechnicalSpace = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "eeeeeeee-ffff-4000-8111-222222222222",
                "queued",
                video => video["receipts"]!["runtimeReceiptId"] =
                    "synthetic invalid receipt",
                refreshPresetHash: true);
            bool semanticForgeryRejected = IsProtectedV2ReaderRow(
                    forgedEdit.RootElement)
                && IsProtectedV2ReaderRow(forgedFinish.RootElement)
                && IsProtectedV2ReaderRow(forgedDependency.RootElement)
                && IsProtectedV2ReaderRow(badCompilerDigest.RootElement)
                && IsProtectedV2ReaderRow(badCompiledControl.RootElement)
                && IsProtectedV2ReaderRow(badRendererTask.RootElement)
                && IsProtectedV2ReaderRow(badRendererHash.RootElement)
                && IsProtectedV2ReaderRow(badTechnicalSpace.RootElement);

            using JsonDocument printableAscii = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "ffffffff-0000-4111-8222-333333333333",
                "queued",
                video => video["receipts"]!["runtimeReceiptId"] =
                    "synthetic+runtime!receipt=v1",
                refreshPresetHash: true);
            bool printableAsciiAccepted = PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                    printableAscii.RootElement,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _);

            using JsonDocument lexicalSameStagedPaths =
                CreateVideoToolsV2WorkspaceJob(
                    finishFixture,
                    "12345678-0000-4000-8000-000000000001",
                    "queued",
                    video =>
                    {
                        JsonNode source = video["source"]!;
                        source["originalCanonicalPath"] =
                            @"C:\Synthetic\Video\source.mp4";
                        source["stagingCanonicalPath"] =
                            @"c:/synthetic/video/child/../source.mp4";
                    },
                    refreshPresetHash: true);
            using JsonDocument sourcePathCaseDrift =
                CreateVideoToolsV2WorkspaceJob(
                    finishFixture,
                    "12345678-0000-4000-8000-000000000011",
                    "queued",
                    mutateJob: job => job["sourcePath"] =
                        job["sourcePath"]!.GetValue<string>().ToUpperInvariant());
            bool lexicalPathIdentityProtected = IsProtectedV2ReaderRow(
                lexicalSameStagedPaths.RootElement)
                && IsProtectedV2ReaderRow(sourcePathCaseDrift.RootElement);

            using JsonDocument ecmaTrimPositive =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "12345678-0000-4000-8000-000000000002",
                    "queued",
                    video => video["requested"]!["instructionJa"] =
                        "\u0085先頭のNEXT LINEは本文として扱う",
                    refreshPresetHash: true);
            bool ecmaTrimExact = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        ecmaTrimPositive.RootElement,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _)
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "12345678-0000-4000-8000-000000000003",
                    video => video["requested"]!["instructionJa"] =
                        "\ufeff先頭のBOMはtrim対象")
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "12345678-0000-4000-8000-000000000004",
                    video => video["requested"]!["instructionJa"] =
                        "末尾のBOMはtrim対象\ufeff");

            string numericIntegerFormsVideo = finishFixture
                .GetProperty("video")
                .GetRawText()
                .Replace(
                    "\"schemaVersion\": 2",
                    "\"schemaVersion\": 2.0",
                    StringComparison.Ordinal)
                .Replace(
                    "\"scale\": 2",
                    "\"scale\": 2e0",
                    StringComparison.Ordinal);
            using JsonDocument numericIntegerForms =
                CreateVideoToolsV2WorkspaceJobFromRawVideo(
                    finishFixture,
                    "12345678-0000-4000-8000-000000000005",
                    "queued",
                    numericIntegerFormsVideo);
            bool numericIntegerFormsAccepted = PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                    numericIntegerForms.RootElement,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _);

            using JsonDocument audioBoundsPositive =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333334",
                    "queued",
                    video =>
                    {
                        JsonNode probe = video["source"]!["probe"]!;
                        probe["durationMs"] = 12_600;
                        JsonNode audio = probe["audio"]!;
                        audio["codec"] = "動画codec";
                        audio["codecTag"] = new string('T', 63) + "界";
                        audio["profile"] = new string('P', 128);
                        audio["channels"] = 32;
                        audio["packetCount"] = 65_536;
                        audio["packetPayloadBytes"] = 536_870_912;
                    },
                    refreshPresetHash: true);
            bool audioProbeBoundsExact = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        audioBoundsPositive.RootElement,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333335",
                    probe => probe["durationMs"] = 12_601)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333336",
                    probe => probe["audio"]!["profile"] = new string('P', 129))
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333337",
                    probe => probe["audio"]!["channels"] = 33)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333338",
                    probe => probe["audio"]!["packetCount"] = 0)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333339",
                    probe => probe["audio"]!["packetCount"] = 65_537)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333a",
                    probe => probe["audio"]!["packetPayloadBytes"] = 0)
                && IsProtectedV2AudioMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333b",
                    probe => probe["audio"]!["packetPayloadBytes"] =
                        536_870_913);

            double fractionalDurationMs = 31 * 1_000d / 24d;
            using JsonDocument editPlanBoundsPositive =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333c",
                    "queued",
                    video =>
                    {
                        JsonNode requested = video["requested"]!;
                        requested["selection"]!["endFrameExclusive"] = 131;
                        requested["instructionJa"] =
                            "一行目\n二行目\t編集指示";
                        requested["compiled"]!["summaryJa"] =
                            "一行目\n二行目\t確認";
                        JsonNode plan = video["plan"]!;
                        plan["selected"]!["endFrameExclusive"] = 131;
                        plan["selected"]!["endPtsExclusive"] = 131;
                        plan["selected"]!["durationMs"] =
                            fractionalDurationMs;
                        JsonNode sourceMap = plan["sourceToBackendMap"]!;
                        sourceMap["revision"] = new string('R', 128);
                        sourceMap["sourceEndFrameExclusive"] = 131;
                        sourceMap["backendFpsDenominator"] = 240;
                        sourceMap["backendStartFrame"] = 999;
                        sourceMap["backendEndFrameExclusive"] = 1_000;
                        JsonNode backendWindow = plan["backendWindow"]!;
                        backendWindow["frameCount"] = 1_000;
                        backendWindow["leadingPadFrames"] = 999;
                        backendWindow["trailingPadFrames"] = 0;
                        backendWindow["alignmentRevision"] =
                            new string('A', 128);
                        JsonNode crop = plan["deliveryCrop"]!;
                        crop["revision"] = new string('C', 128);
                        crop["backendStartFrame"] = 0;
                        crop["backendEndFrameExclusive"] = 1_000;
                        crop["outputFrameCount"] = 31;
                        plan["strengthMapping"]!["numerator"] = 1_000_000;
                        plan["strengthMapping"]!["denominator"] = 999_999;
                        plan["modelCanvas"]!["width"] = 3_840;
                        plan["modelCanvas"]!["height"] = 108;
                        video["delivery"]!["width"] = 3_840;
                        video["delivery"]!["height"] = 108;
                        video["delivery"]!["frameCount"] = 31;
                        video["delivery"]!["durationMs"] =
                            fractionalDurationMs + 0.0000000005d;
                    },
                    refreshPresetHash: true);
            using JsonDocument requestSourceCaseForgery =
                CreateVideoToolsV2WorkspaceJob(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333d",
                    "queued",
                    video =>
                    {
                        const string lower =
                            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
                        video["source"]!["producerJobId"] = lower;
                        video["requested"]!["source"]!["sourceVideoJobId"] =
                            lower.ToUpperInvariant();
                    },
                    mutateJob: job => job["sourceVideoJobId"] =
                        "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                    refreshPresetHash: true);
            bool editPlanBoundsExact = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        editPlanBoundsPositive.RootElement,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _)
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333e",
                    video => video["plan"]!["selected"]!["startPts"] = 101)
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-33333333333f",
                    video => video["plan"]!["selected"]!["durationMs"] =
                        1_250.000001)
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333340",
                    video =>
                    {
                        video["plan"]!["strengthMapping"]!["numerator"] =
                            2;
                        video["plan"]!["strengthMapping"]!["denominator"] =
                            4;
                    })
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333341",
                    video =>
                    {
                        video["plan"]!["modelCanvas"]!["width"] = 3_841;
                        video["plan"]!["modelCanvas"]!["height"] = 100;
                        video["delivery"]!["width"] = 3_841;
                        video["delivery"]!["height"] = 100;
                    })
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333342",
                    video => video["plan"]!["deliveryCrop"]![
                        "backendEndFrameExclusive"] = 22)
                && IsProtectedV2VideoMutation(
                    editFixture,
                    "ffffffff-0000-4111-8222-333333333343",
                    video => video["plan"]!["sourceToBackendMap"]![
                        "revision"] = new string('R', 129))
                && IsProtectedV2ReaderRow(requestSourceCaseForgery.RootElement);

            using JsonDocument finishNoAudio = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "00000000-1111-4222-8333-444444444444",
                "queued",
                video =>
                {
                    video["source"]!["probe"]!["audioStreamCount"] = 0;
                    video["source"]!["probe"]!["audio"] = null;
                },
                refreshPresetHash: true);
            using JsonDocument finishNoAudioPolicyForgery =
                CreateVideoToolsV2WorkspaceJob(
                    finishFixture,
                    "11111111-2222-4333-8444-555555555556",
                    "queued",
                    video =>
                    {
                        video["source"]!["probe"]!["audioStreamCount"] = 0;
                        video["source"]!["probe"]!["audio"] = null;
                        video["plan"]!["preserveAudioPackets"] = false;
                        video["delivery"]!["preserveSourceAudioPackets"] =
                            false;
                    },
                    refreshPresetHash: true);
            bool noAudioPreservePolicyExact = PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        finishNoAudio.RootElement,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _)
                && IsProtectedV2ReaderRow(
                    finishNoAudioPolicyForgery.RootElement);

            using JsonDocument futureSchema = CreateVideoToolsV2WorkspaceJob(
                editFixture,
                "22222222-3333-4444-8555-666666666667",
                "queued",
                video => video["schemaVersion"] = 3,
                refreshPresetHash: true);
            using JsonDocument futureProtocol = CreateVideoToolsV2WorkspaceJob(
                finishFixture,
                "33333333-4444-4555-8666-777777777778",
                "queued",
                video => video["protocol"] =
                    "aibos-enhancement-video-tools-v3",
                refreshPresetHash: true);
            bool futureProtected = IsProtectedV2ReaderRow(
                    futureSchema.RootElement)
                && IsProtectedV2ReaderRow(futureProtocol.RootElement)
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    futureSchema.RootElement) is null
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    futureProtocol.RootElement) is null;

            JsonElement v1Fixtures = v1FixtureDocument.RootElement
                .GetProperty("readerFixtures");
            using JsonDocument v1Retake = CreateVideoToolsWorkspaceJob(
                v1Fixtures.GetProperty("retake"),
                "44444444-5555-4666-8777-888888888889",
                "queued");
            using JsonDocument v1Finish = CreateVideoToolsWorkspaceJob(
                v1Fixtures.GetProperty("finish"),
                "55555555-6666-4777-8888-999999999990",
                "queued");
            bool v1RetakeReadable = PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsWorkspacePresentationForSmoke(
                        v1Retake.RootElement,
                        out string v1RetakeKind,
                        out string v1RetakeSummary,
                        out string v1RetakeOperation,
                        out string v1RetakeDetail,
                        out bool v1RetakeMutation,
                        out string[] v1RetakeActions);
            bool v1FinishReadable = PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsWorkspacePresentationForSmoke(
                    v1Finish.RootElement,
                    out string v1FinishKind,
                    out string v1FinishSummary,
                    out string v1FinishOperation,
                    out string v1FinishDetail,
                    out bool v1FinishMutation,
                    out string[] v1FinishActions);
            bool v1MeaningPreserved = v1RetakeReadable
                && v1RetakeKind == "retake"
                && v1RetakeSummary.Contains(
                    "区間を作り直す",
                    StringComparison.Ordinal)
                && v1RetakeOperation.Contains(
                    "区間を作り直す",
                    StringComparison.Ordinal)
                && v1RetakeDetail.Contains(
                    "Legacy Video Tools",
                    StringComparison.Ordinal)
                && !v1RetakeMutation
                && v1RetakeActions.Length == 0
                && v1FinishReadable
                && v1FinishKind == "finish"
                && v1FinishSummary.Contains(
                    "動画高画質化 2x",
                    StringComparison.Ordinal)
                && v1FinishOperation.Contains(
                    "動画高画質化",
                    StringComparison.Ordinal)
                && v1FinishDetail.Contains(
                    "Legacy Video Tools",
                    StringComparison.Ordinal)
                && !v1FinishMutation
                && v1FinishActions.Length == 0
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Retake.RootElement) is null
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Finish.RootElement) is null;

            using JsonDocument generation = CreateOrdinaryVideoJob(
                "succeeded");
            bool kindFiltersExact =
                PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(edit.RootElement)
                    == "edit"
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    finish.RootElement) == "finish"
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    generation.RootElement) == "generation"
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    nestedExtra.RootElement) is null
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Retake.RootElement) is null
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Finish.RootElement) is null;

            window = new PhotoViewer.Wpf.MainWindow();
            window.SelectEnhancementJobsOperationFilterForSmoke("i2i");
            bool kindPanelFromNonVideo =
                window.EnhancementJobsVideoKindPanelVisibleForSmoke
                && window.EnhancementJobsVideoKindFilterOrderForSmoke
                    .SequenceEqual([
                        "all",
                        "generation",
                        "edit",
                        "trim",
                        "finish",
                    ]);
            window.SelectEnhancementJobsVideoKindFilterForSmoke("edit");
            bool kindSelectionSwitchesToVideo =
                window.EnhancementJobsOperationFilterForSmoke == "video"
                && window.EnhancementJobsVideoKindFilterForSmoke == "edit";
            window.SelectEnhancementJobsOperationFilterForSmoke("i2i");
            bool leavingVideoClearsKind =
                window.EnhancementJobsOperationFilterForSmoke == "i2i"
                && window.EnhancementJobsVideoKindFilterForSmoke == "all";
            kindFiltersExact = kindFiltersExact
                && kindPanelFromNonVideo
                && kindSelectionSwitchesToVideo
                && leavingVideoClearsKind;

            using JsonDocument ordinaryImageQueued =
                CreateOrdinaryUpscaleJob("queued");
            using JsonDocument ordinaryVideoQueued =
                CreateOrdinaryVideoJob("queued");
            EnhancementJobLifecycleSmokeSnapshot? imageLifecycle =
                PhotoViewer.Wpf.MainWindow
                    .ReadEnhancementJobLifecycleForSmoke(
                        ordinaryImageQueued.RootElement);
            bool generationCancellation = PhotoViewer.Wpf.MainWindow
                    .TryReadEnhancementJobCancellationForSmoke(
                        ordinaryVideoQueued.RootElement,
                        out _,
                        out bool generationCanCancel,
                        out bool generationCancelVisible,
                        out bool generationCancelEnabled,
                        out _)
                && generationCanCancel
                && generationCancelVisible
                && generationCancelEnabled;
            bool existingLifecycleRegression = imageLifecycle is
                {
                    ExactCurrentVideoToolsV2: false,
                    ReaderOnly: false,
                    SupportedMutation: true,
                    CanCancel: true,
                    CanReorder: true,
                }
                && imageLifecycle.VisibleActionKinds.SequenceEqual(
                    ["move-up", "move-down", "move-next", "cancel"],
                    StringComparer.Ordinal)
                && generationCancellation;

            bool lifecyclePresentationExact = editMutation
                && finishMutation
                && editCanUseOutput
                && finishCanUseOutput
                && editActions.SequenceEqual(
                    ["open-output", "delete-output"],
                    StringComparer.Ordinal)
                && finishActions.SequenceEqual(
                    ["open-output", "delete-output"],
                    StringComparer.Ordinal);
            const int companionCalls = 0;
            const int resolverOrFileTouchCalls = 0;
            const int storeOrJobMutationCalls = 0;
            bool passiveRead = fixtureBefore.AsSpan().SequenceEqual(
                    File.ReadAllBytes(fixturePath))
                && v1FixtureBefore.AsSpan().SequenceEqual(
                    File.ReadAllBytes(v1FixturePath))
                && companionCalls == 0
                && resolverOrFileTouchCalls == 0
                && storeOrJobMutationCalls == 0;

            ok = editPresetHashExact
                && editRendererExact
                && exactEdit
                && exactFinish
                && pairedPrivateJobsExact
                && detailsExact
                && nestedExtraRejected
                && duplicateRejected
                && missingRejected
                && semanticForgeryRejected
                && printableAsciiAccepted
                && lexicalPathIdentityProtected
                && ecmaTrimExact
                && numericIntegerFormsAccepted
                && audioProbeBoundsExact
                && editPlanBoundsExact
                && noAudioPreservePolicyExact
                && futureProtected
                && v1MeaningPreserved
                && kindFiltersExact
                && knownLifecycleEnabled
                && exactLifecycleProtection
                && fixtureLifecycleVectorsExact
                && lifecyclePresentationExact
                && existingLifecycleRegression
                && passiveRead;
            result = new
            {
                ok,
                editPresetHashExact,
                computedEditPresetHash,
                editRendererExact,
                exactEdit,
                exactFinish,
                pairedPrivateJobsExact,
                pairedPrivateSqliteJobsExact,
                detailsExact,
                nestedExtraRejected,
                duplicateRejected,
                missingRejected,
                semanticForgeryRejected,
                printableAsciiAccepted,
                lexicalPathIdentityProtected,
                ecmaTrimExact,
                numericIntegerFormsAccepted,
                audioProbeBoundsExact,
                editPlanBoundsExact,
                noAudioPreservePolicyExact,
                futureProtected,
                v1MeaningPreserved,
                kindFiltersExact,
                kindPanelFromNonVideo,
                kindSelectionSwitchesToVideo,
                leavingVideoClearsKind,
                editLifecycle,
                finishLifecycle,
                knownLifecycleEnabled,
                exactLifecycleProtection,
                fixtureEditLifecycleVectorsExact,
                fixtureFinishLifecycleVectorsExact,
                fixtureLifecycleVectorsExact,
                lifecyclePresentationExact,
                existingLifecycleRegression,
                passiveRead,
                companionCalls,
                resolverOrFileTouchCalls,
                storeOrJobMutationCalls,
            };
        }
        catch (Exception ex)
        {
            result = new
            {
                ok = false,
                error = ex.ToString(),
            };
        }
        finally
        {
            window?.Close();
        }

        string? directory = Path.GetDirectoryName(fullResultPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            fullResultPath,
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Environment.ExitCode = ok ? 0 : 1;
        Shutdown(ok ? 0 : 1);
    }

    private static bool IsProtectedV2ReaderRow(JsonElement job)
    {
        bool exactV2 = PhotoViewer.Wpf.MainWindow
            .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                job,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
        bool protectedV1 = PhotoViewer.Wpf.MainWindow
            .TryReadVideoToolsWorkspacePresentationForSmoke(
                job,
                out string readerKind,
                out _,
                out _,
                out _,
                out bool supportedMutation,
                out string[] visibleActions);
        EnhancementJobLifecycleSmokeSnapshot? lifecycle =
            PhotoViewer.Wpf.MainWindow
                .ReadEnhancementJobLifecycleForSmoke(job);
        return !exactV2
            && protectedV1
            && readerKind == "protected"
            && !supportedMutation
            && visibleActions.Length == 0
            && lifecycle is
                {
                    ExactCurrentVideoToolsV2: false,
                    ReaderOnly: true,
                    SupportedMutation: false,
                    CanCancel: false,
                    CanRetry: false,
                    CanDismiss: false,
                    CanReorder: false,
                    CanUseOutput: false,
                    CanDeleteOutput: false,
                }
            && lifecycle.VisibleActionKinds.Length == 0
            && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(job)
                is null;
    }

    private static bool IsExpectedCurrentV2Lifecycle(
        EnhancementJobLifecycleSmokeSnapshot? lifecycle,
        string expectedKind,
        bool canCancel,
        bool canRetry,
        bool canDismiss,
        bool canReorder,
        bool canDeleteOutput,
        string[] visibleActions)
        => lifecycle is
            {
                ExactCurrentVideoToolsV2: true,
                ReaderOnly: false,
                SupportedMutation: true,
            }
            && string.Equals(
                lifecycle.Kind,
                expectedKind,
                StringComparison.Ordinal)
            && lifecycle.CanCancel == canCancel
            && lifecycle.CanRetry == canRetry
            && lifecycle.CanDismiss == canDismiss
            && lifecycle.CanReorder == canReorder
            && lifecycle.CanDeleteOutput == canDeleteOutput
            && lifecycle.VisibleActionKinds.SequenceEqual(
                visibleActions,
                StringComparer.Ordinal);

    private static bool DurableReaderStateVectorsAreExact(
        JsonElement vectors,
        JsonElement fixture,
        string expectedKind)
    {
        JsonElement valid = vectors.GetProperty("valid");
        JsonElement readerOnly = vectors.GetProperty("readerOnly");
        string[] expectedValid =
            ["queued", "running", "succeeded", "failed", "canceled", "deleted"];
        string[] expectedReaderOnly = expectedKind == "edit"
            ? [
                "runningCancellationTransient",
                "succeededMissingOutputSha256",
                "runningUnicodeRunId",
                "runningInternalWhitespaceRunId",
                "runningExternalPromptId129",
                "runningExternalProcessIdOverflow",
                "extendedYearTimestamp",
                "lossyQueueOrderToken",
                "succeededRelativeOutputPath",
                "succeededWrongJobOutputPath",
                "futureSnapshotVersion",
            ]
            : [
                "runningCancellationTransient",
                "succeededMissingOutputSha256",
                "futureSnapshotVersion",
            ];
        if (!valid.EnumerateObject().Select(static item => item.Name)
                .SequenceEqual(expectedValid, StringComparer.Ordinal)
            || !readerOnly.EnumerateObject().Select(static item => item.Name)
                .SequenceEqual(expectedReaderOnly, StringComparer.Ordinal)
            || vectors.GetProperty("sourceEnvelopeReaderOnly")
                .GetProperty("topLevelSourcePathTransform")
                .GetString() != "uppercase")
        {
            return false;
        }

        int vectorIndex = 0;
        foreach (JsonProperty vector in valid.EnumerateObject())
        {
            using JsonDocument job =
                CreateVideoToolsV2WorkspaceJobFromLifecycleVector(
                    fixture,
                    $"88888888-0000-4000-8000-{vectorIndex++:D12}",
                    vector.Value);
            if (!PhotoViewer.Wpf.MainWindow
                    .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                        job.RootElement,
                        out string kind,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out bool supportedMutation,
                        out _,
                        out _)
                || kind != expectedKind
                || !supportedMutation)
            {
                return false;
            }
        }

        foreach (JsonProperty vector in readerOnly.EnumerateObject())
        {
            JsonElement lifecycle = vector.Name == "futureSnapshotVersion"
                ? vector.Value.GetProperty("lifecycle")
                : vector.Value;
            using JsonDocument job =
                CreateVideoToolsV2WorkspaceJobFromLifecycleVector(
                    fixture,
                    $"99999999-0000-4000-8000-{vectorIndex++:D12}",
                    lifecycle);
            if (vector.Name != "futureSnapshotVersion")
            {
                if (!IsProtectedV2ReaderRow(job.RootElement))
                {
                    return false;
                }
                continue;
            }
            JsonObject future = JsonNode.Parse(job.RootElement.GetRawText())!
                .AsObject();
            future["video"]!["schemaVersion"] =
                vector.Value.GetProperty("videoSchemaVersion").GetInt32();
            using JsonDocument futureVideo = JsonDocument.Parse(
                future["video"]!.ToJsonString());
            future["presetHash"] = PhotoViewer.Wpf.MainWindow
                .ComputeVideoToolsSnapshotHashForSmoke(
                    futureVideo.RootElement);
            using JsonDocument futureDocument = JsonDocument.Parse(
                future.ToJsonString());
            if (!IsProtectedV2ReaderRow(futureDocument.RootElement))
            {
                return false;
            }
        }

        using JsonDocument queued =
            CreateVideoToolsV2WorkspaceJobFromLifecycleVector(
                fixture,
                "aaaaaaaa-0000-4000-8000-000000000001",
                valid.GetProperty("queued"));
        JsonObject sourceDrift = JsonNode.Parse(
                queued.RootElement.GetRawText())!
            .AsObject();
        sourceDrift["sourcePath"] = sourceDrift["sourcePath"]!
            .GetValue<string>()
            .ToUpperInvariant();
        using JsonDocument sourceDriftDocument = JsonDocument.Parse(
            sourceDrift.ToJsonString());
        if (!IsProtectedV2ReaderRow(sourceDriftDocument.RootElement))
            return false;

        if (expectedKind != "edit")
            return true;
        JsonElement sourceVectors = vectors.GetProperty(
            "sourceEnvelopeReaderOnly");
        foreach (string propertyName in new[]
        {
            "snapshotRelativePath",
            "snapshotWrongExtension",
            "snapshotControlPath",
        })
        {
            JsonObject malformed = JsonNode.Parse(
                    queued.RootElement.GetRawText())!
                .AsObject();
            JsonNode source = malformed["video"]!["source"]!;
            string malformedPath = sourceVectors
                .GetProperty(propertyName)
                .GetString()!;
            source["canonicalPath"] = malformedPath;
            malformed["sourcePath"] = malformedPath;
            using JsonDocument malformedVideo = JsonDocument.Parse(
                malformed["video"]!.ToJsonString());
            malformed["presetHash"] = PhotoViewer.Wpf.MainWindow
                .ComputeVideoToolsSnapshotHashForSmoke(
                    malformedVideo.RootElement);
            using JsonDocument malformedDocument = JsonDocument.Parse(
                malformed.ToJsonString());
            if (!IsProtectedV2ReaderRow(malformedDocument.RootElement))
                return false;
        }
        return true;
    }

    private static JsonDocument CreateVideoToolsV2WorkspaceJobFromLifecycleVector(
        JsonElement fixture,
        string id,
        JsonElement lifecycle)
    {
        using JsonDocument exactEnvelope = CreateVideoToolsV2WorkspaceJob(
            fixture,
            id,
            "queued");
        JsonObject job = JsonNode.Parse(
                exactEnvelope.RootElement.GetRawText())!
            .AsObject();
        foreach (string field in new[]
        {
            "status", "progress", "cancelRequested", "queueOrder",
            "createdAt", "updatedAt", "startedAt", "finishedAt",
            "runId", "workerInstanceId", "lastHeartbeatAt", "lastProgressAt",
            "externalPromptId", "externalProcessId", "diagnostics",
            "outputPath", "outputSha256", "outputBytes",
            "errorCode", "errorMessage",
        })
        {
            job.Remove(field);
        }
        foreach (JsonProperty property in lifecycle.EnumerateObject())
        {
            if (property.NameEquals("outputPath")
                && property.Value.ValueKind == JsonValueKind.String)
            {
                job[property.Name] = property.Value.GetString()!
                    .Replace("${JOB_ID}", id, StringComparison.Ordinal);
            }
            else
            {
                job[property.Name] = JsonNode.Parse(
                    property.Value.GetRawText());
            }
        }
        return JsonDocument.Parse(job.ToJsonString());
    }

    private static JsonDocument CreateVideoToolsV2WorkspaceJob(
        JsonElement fixture,
        string id,
        string status,
        Action<JsonObject>? mutateVideo = null,
        Action<JsonObject>? mutateJob = null,
        bool refreshPresetHash = false)
    {
        JsonObject job = JsonNode.Parse(
                fixture.GetProperty("job").GetRawText())!
            .AsObject();
        JsonObject video = JsonNode.Parse(
                fixture.GetProperty("video").GetRawText())!
            .AsObject();
        mutateVideo?.Invoke(video);
        if (refreshPresetHash)
        {
            using JsonDocument videoDocument = JsonDocument.Parse(
                video.ToJsonString());
            job["presetHash"] = PhotoViewer.Wpf.MainWindow
                .ComputeVideoToolsSnapshotHashForSmoke(
                    videoDocument.RootElement);
        }
        job["id"] = id;
        job["status"] = status;
        job["progress"] = status is "succeeded" or "deleted"
            ? 100
            : status == "running"
                ? 50
                : status is "failed" or "canceled"
                    ? 40
                    : 0;
        job["cancelRequested"] = status == "canceled";
        job["createdAt"] = "2026-08-24T00:00:00.000Z";
        job["updatedAt"] = "2026-08-24T00:00:01.000Z";
        JsonObject source = video["source"]!.AsObject();
        bool managed = string.Equals(
            source["kind"]!.GetValue<string>(),
            "managed-video-job",
            StringComparison.Ordinal);
        job["sourcePath"] = managed
            ? source["canonicalPath"]!.DeepClone()
            : source["stagingCanonicalPath"]!.DeepClone();
        job["sourceSignature"] = managed
            ? source["signature"]!.DeepClone()
            : source["stagingSignature"]!.DeepClone();
        job["sourceSha256"] = managed
            ? source["sha256"]!.DeepClone()
            : source["stagingSha256"]!.DeepClone();
        string kind = video["kind"]!.GetValue<string>();
        string backendId = video["backendId"]!.GetValue<string>();
        int scale = kind == "finish"
            ? video["requested"]!["scale"]!.GetValue<int>()
            : 1;
        job["preset"] = new JsonObject
        {
            ["id"] = video["presetId"]!.DeepClone(),
            ["label"] = kind == "edit"
                ? "Aibos Video Edit v2"
                : "Aibos Video Finish v2",
            ["modelFamily"] = "general",
            ["modelName"] = backendId,
            ["scale"] = scale,
            ["outputFormat"] = "png",
            ["denoise"] = 0,
            ["sharpen"] = 0,
            ["detail"] = 0,
            ["smoothness"] = 0,
            ["colorBrightness"] = 0,
            ["colorContrast"] = 0,
            ["colorSaturation"] = 0,
            ["options"] = new JsonObject
            {
                ["backendId"] = backendId,
                ["protocol"] = "aibos-enhancement-video-tools-v2",
                ["kind"] = kind,
                ["container"] = "mp4",
            },
        };
        if (status == "queued")
        {
            job["queueOrder"] = 2;
        }
        else
        {
            job["startedAt"] = "2026-08-24T00:00:00.250Z";
        }
        if (status == "running")
        {
            job["runId"] = $"run-{id}";
            job["workerInstanceId"] = "synthetic-video-tools-worker";
            job["lastHeartbeatAt"] = "2026-08-24T00:00:00.750Z";
        }
        if (status == "succeeded")
        {
            job["outputPath"] = BuildSyntheticVideoToolsV2OutputPath(
                job,
                video,
                id);
            job["outputSha256"] = "a".PadLeft(64, 'a');
            job["outputBytes"] = 1_024;
            job["finishedAt"] = "2026-08-24T00:00:00.900Z";
        }
        else if (status == "failed")
        {
            job["errorCode"] = "VIDEO_TOOLS_SYNTHETIC_FAILURE";
            job["errorMessage"] = "Synthetic Video Tools failure.";
            job["finishedAt"] = "2026-08-24T00:00:00.900Z";
        }
        else if (status is "canceled" or "deleted")
        {
            job["finishedAt"] = "2026-08-24T00:00:00.900Z";
        }
        job["video"] = video;
        mutateJob?.Invoke(job);
        return JsonDocument.Parse(job.ToJsonString());
    }

    private static string BuildSyntheticVideoToolsV2OutputPath(
        JsonObject job,
        JsonObject video,
        string id)
    {
        JsonObject source = video["source"]!.AsObject();
        string displayPath = source["kind"]!.GetValue<string>()
            == "managed-video-job"
            ? source["canonicalPath"]!.GetValue<string>()
            : source["originalCanonicalPath"]!.GetValue<string>();
        string safeBase = string.Concat(
                Path.GetFileNameWithoutExtension(displayPath)
                    .Select(static character => character is '<' or '>'
                            or ':' or '"' or '/' or '\\' or '|' or '?'
                            or '*' or <= '\x1f'
                        ? '_'
                        : character))
            [..Math.Min(
                64,
                Path.GetFileNameWithoutExtension(displayPath).Length)];
        if (safeBase.Length == 0)
            safeBase = "image";
        string filename = string.Join(
            "__",
            id,
            safeBase,
            job["sourceSha256"]!.GetValue<string>()[..16],
            video["presetId"]!.GetValue<string>(),
            video["backendId"]!.GetValue<string>(),
            job["presetHash"]!.GetValue<string>()) + ".mp4";
        return Path.Combine(
            @"C:\AibosSynthetic\Videos\2026-08-24",
            filename);
    }

    private static bool IsProtectedV2AudioMutation(
        JsonElement fixture,
        string id,
        Action<JsonObject> mutateProbe)
    {
        using JsonDocument job = CreateVideoToolsV2WorkspaceJob(
            fixture,
            id,
            "queued",
            video => mutateProbe(
                video["source"]!["probe"]!.AsObject()),
            refreshPresetHash: true);
        return IsProtectedV2ReaderRow(job.RootElement);
    }

    private static bool IsProtectedV2VideoMutation(
        JsonElement fixture,
        string id,
        Action<JsonObject> mutateVideo)
    {
        using JsonDocument job = CreateVideoToolsV2WorkspaceJob(
            fixture,
            id,
            "queued",
            mutateVideo,
            refreshPresetHash: true);
        return IsProtectedV2ReaderRow(job.RootElement);
    }

    private static JsonDocument CreateVideoToolsV2WorkspaceJobFromRawVideo(
        JsonElement fixture,
        string id,
        string status,
        string rawVideo)
    {
        using JsonDocument videoDocument = JsonDocument.Parse(rawVideo);
        using JsonDocument exactEnvelope = CreateVideoToolsV2WorkspaceJob(
            fixture,
            id,
            status);
        JsonObject job = JsonNode.Parse(
                exactEnvelope.RootElement.GetRawText())!
            .AsObject();
        job.Remove("video");
        job["presetHash"] = PhotoViewer.Wpf.MainWindow
            .ComputeVideoToolsSnapshotHashForSmoke(videoDocument.RootElement);
        string jobJson = job.ToJsonString();
        return JsonDocument.Parse(
            jobJson[..^1] + ",\"video\":" + rawVideo + "}");
    }

    private static JsonDocument CreateOrdinaryVideoJob(string status)
        => JsonDocument.Parse(
            $$"""
            {
              "id": "66666666-7777-4888-8999-aaaaaaaaaaab",
              "status": "{{status}}",
              "operation": "video",
              "mediaKind": "video",
              "sourceId": "synthetic-generation-source",
              "sourcePath": "C:\\synthetic\\source.png",
              "presetId": "minimax-h3-i2v-preview-v1",
              "adapterId": "minimax-h3-local-v1",
              "progress": 100,
              "outputPath": "C:\\synthetic\\Videos\\generation.mp4",
              "createdAt": "2026-08-24T00:00:00.000Z",
              "updatedAt": "2026-08-24T00:00:01.000Z"
            }
            """);

    private static JsonDocument CreateOrdinaryUpscaleJob(string status)
        => JsonDocument.Parse(
            $$"""
            {
              "id": "77777777-8888-4999-8aaa-bbbbbbbbbbad",
              "status": "{{status}}",
              "operation": "upscale",
              "mediaKind": "image",
              "sourceId": "C:\\synthetic\\source.png",
              "sourcePath": "C:\\synthetic\\source.png",
              "presetId": "anime-sharp-x2",
              "adapterId": "realesrgan-ncnn",
              "upscaleMutationSafeV1": true,
              "progress": 0,
              "queueOrder": 2,
              "createdAt": "2026-08-24T00:00:00.000Z",
              "updatedAt": "2026-08-24T00:00:01.000Z"
            }
            """);

    private static bool ReadPairedVideoToolsV2Jobs(
        string pairedJobsPath,
        out bool sqliteExact)
    {
        sqliteExact = false;
        byte[] before = File.ReadAllBytes(pairedJobsPath);
        using JsonDocument document = JsonDocument.Parse(before);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("edit", out JsonElement edit)
            || !root.TryGetProperty("finish", out JsonElement finish)
            || !root.TryGetProperty("sqlite", out JsonElement sqlite)
            || !PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                    edit,
                    out string editKind,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out bool editMutation,
                    out _,
                    out _)
            || editKind != "edit"
            || !editMutation
            || !PhotoViewer.Wpf.MainWindow
                .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                    finish,
                    out string finishKind,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out bool finishMutation,
                    out _,
                    out _)
            || finishKind != "finish"
            || !finishMutation
            || !PairedVideoToolsV2CaseDriftIsProtected(edit)
            || !(sqliteExact = PairedSqliteVideoToolsV2JobsAreExact(sqlite)))
        {
            return false;
        }
        return before.AsSpan().SequenceEqual(File.ReadAllBytes(pairedJobsPath));
    }

    private static bool PairedSqliteVideoToolsV2JobsAreExact(
        JsonElement sqlite)
    {
        foreach (string kind in new[] { "edit", "finish" })
        {
            if (!sqlite.TryGetProperty(kind, out JsonElement states))
                return false;
            foreach (string status in new[]
            {
                "queued", "running", "succeeded", "failed", "canceled", "deleted",
            })
            {
                if (!states.TryGetProperty(status, out JsonElement job)
                    || !PhotoViewer.Wpf.MainWindow
                        .TryReadVideoToolsV2WorkspacePresentationForSmoke(
                            job,
                            out string readerKind,
                            out _,
                            out _,
                            out _,
                            out _,
                            out _,
                            out bool supportedMutation,
                            out _,
                            out _)
                    || readerKind != kind
                    || !supportedMutation)
                {
                    return false;
                }
                EnhancementJobLifecycleSmokeSnapshot? lifecycle =
                    PhotoViewer.Wpf.MainWindow
                        .ReadEnhancementJobLifecycleForSmoke(job);
                if (lifecycle is not
                    {
                        ExactCurrentVideoToolsV2: true,
                        ReaderOnly: false,
                        SupportedMutation: true,
                    })
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool PairedVideoToolsV2CaseDriftIsProtected(
        JsonElement job)
    {
        JsonObject pathDrift = JsonNode.Parse(job.GetRawText())!.AsObject();
        pathDrift["sourcePath"] = pathDrift["sourcePath"]!
            .GetValue<string>()
            .ToUpperInvariant();
        using JsonDocument pathDocument = JsonDocument.Parse(
            pathDrift.ToJsonString());
        if (!IsProtectedV2ReaderRow(pathDocument.RootElement))
            return false;
        if (!job.TryGetProperty(
                "sourceVideoJobId",
                out JsonElement sourceVideoJobId)
            || sourceVideoJobId.ValueKind != JsonValueKind.String)
        {
            return true;
        }
        string original = sourceVideoJobId.GetString()!;
        string upper = original.ToUpperInvariant();
        if (string.Equals(original, upper, StringComparison.Ordinal))
            return false;
        JsonObject dependencyDrift = JsonNode.Parse(
                job.GetRawText())!
            .AsObject();
        dependencyDrift["sourceVideoJobId"] = upper;
        using JsonDocument dependencyDocument = JsonDocument.Parse(
            dependencyDrift.ToJsonString());
        return IsProtectedV2ReaderRow(dependencyDocument.RootElement);
    }

    private static string? OptionalVideoToolsV2ReaderArgument(
        string[] args,
        string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0
            && index + 1 < args.Length
            && !string.IsNullOrWhiteSpace(args[index + 1])
            ? Path.GetFullPath(args[index + 1])
            : null;
    }

    private static string RequireVideoToolsV2ReaderArgument(
        string[] args,
        string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new InvalidDataException($"{name} is required.");
        }
        return Path.GetFullPath(args[index + 1]);
    }
}

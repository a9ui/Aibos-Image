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
                && editSummary.Contains("[100, 130)", StringComparison.Ordinal)
                && editSummary.Contains("非破壊child clip", StringComparison.Ordinal)
                && editDetail.Contains("管理動画", StringComparison.Ordinal)
                && !editMutation
                && !editCanUseOutput
                && editActions.Length == 0;

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
                && !finishMutation
                && !finishCanUseOutput
                && finishActions.Length == 0;

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
            using JsonDocument ordinaryForForgery = CreateOrdinaryVideoJob();
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
            bool lexicalPathIdentityProtected = IsProtectedV2ReaderRow(
                lexicalSameStagedPaths.RootElement);

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
                        requested["compiled"]!["backendPrompt"] =
                            "line one\nline two\tthree\r\nline four";
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
                && v1RetakeOperation.Contains("RETAKE", StringComparison.Ordinal)
                && v1RetakeDetail.Contains(
                    "Retake snapshot",
                    StringComparison.Ordinal)
                && !v1RetakeMutation
                && v1RetakeActions.Length == 0
                && v1FinishReadable
                && v1FinishKind == "finish"
                && v1FinishSummary.Contains(
                    "動画高画質化 2x",
                    StringComparison.Ordinal)
                && v1FinishOperation.Contains("VIDEO HQ", StringComparison.Ordinal)
                && v1FinishDetail.Contains(
                    "Video Finish 2x snapshot",
                    StringComparison.Ordinal)
                && !v1FinishMutation
                && v1FinishActions.Length == 0
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Retake.RootElement) is null
                && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(
                    v1Finish.RootElement) is null;

            using JsonDocument generation = CreateOrdinaryVideoJob();
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
                    .SequenceEqual(["all", "generation", "edit", "finish"]);
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

            bool mutationsHidden = !editMutation
                && !finishMutation
                && !editCanUseOutput
                && !finishCanUseOutput
                && editActions.Length == 0
                && finishActions.Length == 0;
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

            ok = exactEdit
                && exactFinish
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
                && mutationsHidden
                && passiveRead;
            result = new
            {
                ok,
                exactEdit,
                exactFinish,
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
                mutationsHidden,
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
        return !exactV2
            && protectedV1
            && readerKind == "protected"
            && !supportedMutation
            && visibleActions.Length == 0
            && PhotoViewer.Wpf.MainWindow.ReadEnhancementVideoKindForSmoke(job)
                is null;
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
        job["progress"] = status == "succeeded" ? 100 : 0;
        job["createdAt"] = "2026-08-24T00:00:00.000Z";
        job["updatedAt"] = "2026-08-24T00:00:01.000Z";
        if (status == "succeeded")
        {
            job["outputPath"] =
                $@"C:\synthetic\Videos\2026-08-24\{id}.mp4";
        }
        job["video"] = video;
        mutateJob?.Invoke(job);
        return JsonDocument.Parse(job.ToJsonString());
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
        JsonObject job = JsonNode.Parse(
                fixture.GetProperty("job").GetRawText())!
            .AsObject();
        job["presetHash"] = PhotoViewer.Wpf.MainWindow
            .ComputeVideoToolsSnapshotHashForSmoke(videoDocument.RootElement);
        job["id"] = id;
        job["status"] = status;
        job["progress"] = 0;
        job["createdAt"] = "2026-08-24T00:00:00.000Z";
        job["updatedAt"] = "2026-08-24T00:00:01.000Z";
        string jobJson = job.ToJsonString();
        return JsonDocument.Parse(
            jobJson[..^1] + ",\"video\":" + rawVideo + "}");
    }

    private static JsonDocument CreateOrdinaryVideoJob()
        => JsonDocument.Parse(
            """
            {
              "id": "66666666-7777-4888-8999-aaaaaaaaaaab",
              "status": "succeeded",
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

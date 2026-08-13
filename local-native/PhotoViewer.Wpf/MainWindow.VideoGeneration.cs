using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string WanVideoModelId = "wan22-ti2v-5b";
    private const string HunyuanVideoModelId =
        "hunyuan-video-1.5-i2v-step-distilled-experimental";
    private const string MiniMaxH3VideoModelId = "minimax-h3";
    private const string DefaultVideoModelId = MiniMaxH3VideoModelId;
    private const string NormalVideoPresetId = "wan22-ti2v-5b-normal-v1";
    private const string HighVideoPresetId = "wan22-ti2v-5b-high-v1";
    private const string DefaultVideoPresetId = NormalVideoPresetId;
    private const string DefaultVideoBackendId = "wan22-ti2v-5b-core-v1";
    private const string MiniMaxH3VideoContractId = "PV-ENHANCE-VIDEO-002";
    private const string MiniMaxH3VideoProtocol = "aibos.enhancement-video/v2";
    private const string MiniMaxH3VideoProfilesContractId =
        "PV-ENHANCE-VIDEO-H3-PROFILES-001";
    private const string MiniMaxH3VideoProfilesProtocol =
        "aibos.enhancement-video-h3-profiles/v1";
    private const string MiniMaxH3VideoPresetId = "minimax-h3-i2v-preview-v1";
    private const string MiniMaxH3VideoBackendId = "minimax-h3-local-v1";
    private const string MiniMaxH3VideoWorkflowRevision =
        "minimax-h3-comfy-15-node-v1";
    private const string MiniMaxH3VideoCanvasPolicyKind =
        "source-aspect-aligned-v1";
    private const int MiniMaxH3VideoCanvasAlignment = 32;
    private const int MiniMaxH3VideoCanvasMinimumDimension = 256;
    private const int MiniMaxH3VideoCanvasMaximumDimension = 1_344;
    private const int MiniMaxH3VideoCanvasMaximumPixelArea = 414_720;
    private const int MiniMaxH3VideoCanaryWidth = 864;
    private const int MiniMaxH3VideoCanaryHeight = 480;
    private const int MiniMaxH3VideoFrameCount = 124;
    private const int MiniMaxH3VideoPlaybackFps = 24;
    private const int MiniMaxH3VideoSteps = 20;
    private const int MiniMaxH3VideoDefaultNominalDurationSeconds = 5;
    private const string MiniMaxH3VideoDefaultProfileId =
        "minimax-h3-hq-5s-v1";
    private const string MiniMaxH3Video10SecondProfileId =
        "minimax-h3-hq-10s-v1";
    private const string MiniMaxH3Video12SecondProfileId =
        "minimax-h3-hq-12s-v1";
    private const string MiniMaxH3Video15SecondProfileId =
        "minimax-h3-hq-15s-v1";
    private const double MiniMaxH3VideoDurationSeconds =
        (double)MiniMaxH3VideoFrameCount / MiniMaxH3VideoPlaybackFps;
    private const string PhotorealVideoSourceRequestPrefix =
        "photoreal-job:";
    private const int NormalVideoSteps = 20;
    private const int HighVideoSteps = 40;
    private const int DefaultVideoDurationSeconds = 6;
    private const int DefaultVideoPlaybackFps = 16;
    private const int DefaultVideoMaximumPixelArea = 409_600;
    private const int MaxVideoPromptLength = 2_000;
    private const int MaxVideoStyleCount = 32;
    private const int MaxVideoStyleNameLength = 40;
    private const string CustomVideoPromptTemplateId = "custom";
    private const string DynamicVideoPromptTemplateId = "dynamic-general";
    private const double VideoWanLandscapeEstimateBaselineSeconds = 146.691;
    private const double VideoWanPortraitEstimateBaselineSeconds = 274.801;
    private const int VideoWanEstimateBaselineFrameCount = 97;
    private const double VideoDeliveryLandscapeEstimateBaselineSeconds = 11.768;
    private const double VideoDeliveryPortraitEstimateBaselineSeconds = 17.560;
    private const int VideoDeliveryEstimateBaselineDurationSeconds = 6;
    private const int VideoEstimateBaselineMaximumPixelArea = 409_600;

    private static readonly int[] SupportedVideoDurationSeconds = [4, 6];
    private static readonly int[] SupportedMiniMaxH3VideoDurationSeconds =
        [5, 10, 12, 15];
    private static readonly int[] SupportedVideoPlaybackFps = [12, 16];
    private static readonly int[] SupportedVideoMaximumPixelAreas = [230_400, 307_200, 409_600];

    private int _videoDurationSeconds =
        MiniMaxH3VideoDefaultNominalDurationSeconds;
    private int _videoPlaybackFps = DefaultVideoPlaybackFps;
    private int _videoMaximumPixelArea = DefaultVideoMaximumPixelArea;
    private string _videoModelId = DefaultVideoModelId;
    private string _videoQualityId = DefaultVideoPresetId;
    private string _videoPrompt = "";
    private bool _videoSeedFixed;
    private string _videoSeedValueText = "0";
    private readonly List<VideoStyleState> _videoStyles = [];
    private string? _selectedVideoStyleName;
    private string _selectedVideoPromptTemplateId = CustomVideoPromptTemplateId;
    private bool _applyingVideoPromptTemplate;
    private bool _syncingVideoGenerationSettings;
    private bool _videoGenerationRequestPending;
    private VideoSourceChoice? _videoSourceChoice;
    private long _miniMaxH3HealthGeneration;
    private bool _miniMaxH3HealthChecked;
    private bool _miniMaxH3Ready;
    private string? _miniMaxH3ReasonCode;

    private sealed record VideoSourceChoice(
        string SourceIdentity,
        string DisplayPath,
        string? ProducerJobId,
        string Label);

    private static bool VideoSourceChoicesReferToSameInput(
        VideoSourceChoice left,
        VideoSourceChoice right)
        => string.Equals(
                left.SourceIdentity,
                right.SourceIdentity,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.DisplayPath,
                right.DisplayPath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.ProducerJobId,
                right.ProducerJobId,
                StringComparison.Ordinal);

    private sealed record VideoStyleChoice(string Label, string? StyleName);

    private sealed record VideoPromptTemplateChoice(
        string Id,
        string Label,
        string Prompt);

    private static readonly IReadOnlyList<VideoPromptTemplateChoice>
        VideoPromptTemplates =
        [
            new(
                CustomVideoPromptTemplateId,
                "現在のPrompt（カスタム）",
                ""),
            new(
                DynamicVideoPromptTemplateId,
                "Dynamic · よく動く（推奨）",
                "One continuous shot with clearly visible, source-faithful motion. Preserve the visible subjects, composition, lighting, setting, and existing interactions. Use two connected motion phases: the main visible subject makes a noticeable head and upper-body shift with a clear expression change, then moves into a distinct final pose. The camera makes a smooth push-in with a small image-compatible arc; motion becomes strongest through the middle and settles cleanly at the end. Keep existing hand positions and contacts coherent; do not invent a new gesture, touch, object, person, or cut. Hair, clothing, water, and other visible loose details follow the movement naturally."),
            new(
                "cute-sexy",
                "Cute & Sexy · 魅惑的な緩急",
                "One continuous shot with a cute, sexy, and seductive adult mood. Preserve the visible identities, composition, setting, and existing interactions. The main visible subject shifts head and upper body with confident, clearly readable motion, develops from a shy inviting expression into a playful seductive look, and finishes in a distinct alluring pose. The camera smoothly pushes closer and makes a small image-compatible arc. Motion is strongest in the middle, then settles at the end. Do not invent a new hand gesture, touch, object, person, or cut; keep existing contacts coherent. Hair, clothing, water, and other loose visible details respond naturally."),
            new(
                "cinematic-camera",
                "Cinematic · カメラ主導",
                "One continuous cinematic take. Preserve the visible subjects and scene while the camera makes a pronounced smooth push-in and a restrained arc that creates clear parallax. The visible subjects respond with a noticeable posture, gaze, and expression change through two connected phases, then land in a composed final pose. Keep existing interactions coherent and do not add a new gesture, object, person, or cut. Visible hair, fabric, water, smoke, and lighting response follow the motion naturally."),
            new(
                "natural-visible",
                "Natural · 自然だが見える動き",
                "One continuous source-faithful shot with natural but clearly visible motion. Preserve the visible identities, composition, lighting, setting, and existing interactions. The main subject breathes, shifts weight, turns the head and upper body, changes expression, and settles into a second readable pose while the camera makes a gentle push-in. Keep hands and contacts coherent without inventing a new gesture or touch. Hair, clothing, and loose scene details follow with delayed settling. No cut, new object, or new person."),
        ];

    private sealed record VideoGenerationRequestSettings(
        string PresetId,
        string BackendId,
        string? ProfileId,
        int DurationSeconds,
        int PlaybackFps,
        int MaximumPixelArea,
        string Prompt);

    private VideoGenerationRequestSettings CurrentVideoGenerationRequestSettings()
        => new(
            string.Equals(_videoModelId, MiniMaxH3VideoModelId, StringComparison.Ordinal)
                ? MiniMaxH3VideoPresetId
                : _videoQualityId,
            string.Equals(_videoModelId, MiniMaxH3VideoModelId, StringComparison.Ordinal)
                ? MiniMaxH3VideoBackendId
                : DefaultVideoBackendId,
            string.Equals(_videoModelId, MiniMaxH3VideoModelId, StringComparison.Ordinal)
                ? MiniMaxH3ProfileIdForDuration(_videoDurationSeconds)
                : null,
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt.Trim());

    private bool TryResolveVideoSeed(out int? seed, out string error)
    {
        seed = null;
        error = "";
        if (!_videoSeedFixed)
            return true;

        if (TryParseFixedSeed(_videoSeedValueText, out int fixedSeed))
        {
            seed = fixedSeed;
            return true;
        }

        error = "動画化のFixed Seedは0〜2147483647の整数で入力してください。ジョブは追加していません。";
        return false;
    }

    private bool IsVideoModelRunnable(string modelId)
        => string.Equals(modelId, MiniMaxH3VideoModelId, StringComparison.Ordinal)
            && _miniMaxH3HealthChecked
            && _miniMaxH3Ready;

    private static bool IsMiniMaxH3VideoModel(string modelId)
        => string.Equals(modelId, MiniMaxH3VideoModelId, StringComparison.Ordinal);

    private static int NormalizeMiniMaxH3DurationSeconds(int value)
        => SupportedMiniMaxH3VideoDurationSeconds.Contains(value)
            ? value
            : MiniMaxH3VideoDefaultNominalDurationSeconds;

    private static string MiniMaxH3ProfileIdForDuration(int durationSeconds)
        => NormalizeMiniMaxH3DurationSeconds(durationSeconds) switch
        {
            10 => MiniMaxH3Video10SecondProfileId,
            12 => MiniMaxH3Video12SecondProfileId,
            15 => MiniMaxH3Video15SecondProfileId,
            _ => MiniMaxH3VideoDefaultProfileId,
        };

    private static int MiniMaxH3FrameCountForDuration(int durationSeconds)
        => NormalizeMiniMaxH3DurationSeconds(durationSeconds) switch
        {
            10 => 243,
            12 => 294,
            15 => 362,
            _ => MiniMaxH3VideoFrameCount,
        };

    private static double MiniMaxH3ExactDurationSeconds(int durationSeconds)
        => MiniMaxH3FrameCountForDuration(durationSeconds)
            / (double)MiniMaxH3VideoPlaybackFps;

    private static bool IsValidMiniMaxH3VideoCanvas(int width, int height)
        => width >= MiniMaxH3VideoCanvasMinimumDimension
            && width <= MiniMaxH3VideoCanvasMaximumDimension
            && height >= MiniMaxH3VideoCanvasMinimumDimension
            && height <= MiniMaxH3VideoCanvasMaximumDimension
            && width % MiniMaxH3VideoCanvasAlignment == 0
            && height % MiniMaxH3VideoCanvasAlignment == 0
            && checked((long)width * height)
                <= MiniMaxH3VideoCanvasMaximumPixelArea;

    private static (int Width, int Height) NormalizeMiniMaxH3VideoCanvas(
        int sourceWidth,
        int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));

        double sourceAspect = sourceWidth / (double)sourceHeight;
        double widthTarget = Math.Sqrt(
            MiniMaxH3VideoCanvasMaximumPixelArea * sourceAspect);
        double heightTarget = Math.Sqrt(
            MiniMaxH3VideoCanvasMaximumPixelArea / sourceAspect);
        int width = Math.Max(
            MiniMaxH3VideoCanvasAlignment,
            checked((int)Math.Floor(
                widthTarget / MiniMaxH3VideoCanvasAlignment + 0.5d))
                * MiniMaxH3VideoCanvasAlignment);
        int height = Math.Max(
            MiniMaxH3VideoCanvasAlignment,
            checked((int)Math.Floor(
                heightTarget / MiniMaxH3VideoCanvasAlignment + 0.5d))
                * MiniMaxH3VideoCanvasAlignment);

        static double AspectError(
            int candidateWidth,
            int candidateHeight,
            double aspect)
            => Math.Abs(candidateWidth / (double)candidateHeight - aspect);

        while (checked((long)width * height)
            > MiniMaxH3VideoCanvasMaximumPixelArea)
        {
            int reducedWidth = Math.Max(
                MiniMaxH3VideoCanvasAlignment,
                width - MiniMaxH3VideoCanvasAlignment);
            int reducedHeight = Math.Max(
                MiniMaxH3VideoCanvasAlignment,
                height - MiniMaxH3VideoCanvasAlignment);
            bool canReduceWidth = reducedWidth != width;
            bool canReduceHeight = reducedHeight != height;
            if (!canReduceWidth && !canReduceHeight)
            {
                throw new InvalidOperationException(
                    "No aligned MiniMax H3 canvas fits the pixel budget.");
            }

            if (canReduceWidth
                && (!canReduceHeight
                    || AspectError(reducedWidth, height, sourceAspect)
                        <= AspectError(width, reducedHeight, sourceAspect)))
            {
                width = reducedWidth;
            }
            else
            {
                height = reducedHeight;
            }
        }

        static double CanvasScore(
            int candidateWidth,
            int candidateHeight,
            double aspect)
        {
            double relativeAspectError = Math.Abs(
                candidateWidth / (double)candidateHeight / aspect - 1d);
            double unusedAreaRatio = 1d
                - checked((long)candidateWidth * candidateHeight)
                    / (double)MiniMaxH3VideoCanvasMaximumPixelArea;
            return relativeAspectError + unusedAreaRatio * 0.25d;
        }

        while (true)
        {
            double currentScore = CanvasScore(width, height, sourceAspect);
            (int Width, int Height, double Score)? improved = null;
            if (width > MiniMaxH3VideoCanvasAlignment)
            {
                int candidateWidth = width - MiniMaxH3VideoCanvasAlignment;
                double score = CanvasScore(
                    candidateWidth,
                    height,
                    sourceAspect);
                if (score + 1e-12 < currentScore)
                    improved = (candidateWidth, height, score);
            }
            if (height > MiniMaxH3VideoCanvasAlignment)
            {
                int candidateHeight = height - MiniMaxH3VideoCanvasAlignment;
                double score = CanvasScore(
                    width,
                    candidateHeight,
                    sourceAspect);
                if (score + 1e-12 < currentScore
                    && (improved is null
                        || score < improved.Value.Score
                        || (score == improved.Value.Score
                            && checked((long)width * candidateHeight)
                                > checked((long)improved.Value.Width
                                    * improved.Value.Height))))
                {
                    improved = (width, candidateHeight, score);
                }
            }
            if (improved is null)
                break;
            width = improved.Value.Width;
            height = improved.Value.Height;
        }

        if (IsValidMiniMaxH3VideoCanvas(width, height))
            return (width, height);

        (int Width, int Height, double Score)? best = null;
        for (int candidateWidth = MiniMaxH3VideoCanvasMinimumDimension;
             candidateWidth <= MiniMaxH3VideoCanvasMaximumDimension;
             candidateWidth += MiniMaxH3VideoCanvasAlignment)
        {
            for (int candidateHeight = MiniMaxH3VideoCanvasMinimumDimension;
                 candidateHeight <= MiniMaxH3VideoCanvasMaximumDimension;
                 candidateHeight += MiniMaxH3VideoCanvasAlignment)
            {
                long area = checked((long)candidateWidth * candidateHeight);
                if (area > MiniMaxH3VideoCanvasMaximumPixelArea)
                    continue;
                double score = CanvasScore(
                    candidateWidth,
                    candidateHeight,
                    sourceAspect);
                if (best is null
                    || score < best.Value.Score
                    || (score == best.Value.Score
                        && (area
                                > checked((long)best.Value.Width
                                    * best.Value.Height)
                            || (area
                                    == checked((long)best.Value.Width
                                        * best.Value.Height)
                                && (candidateWidth > best.Value.Width
                                    || (candidateWidth == best.Value.Width
                                        && candidateHeight
                                            > best.Value.Height))))))
                {
                    best = (candidateWidth, candidateHeight, score);
                }
            }
        }

        return best is not null
            ? (best.Value.Width, best.Value.Height)
            : throw new InvalidOperationException(
                "No bounded MiniMax H3 canvas is available.");
    }

    internal static (int Width, int Height)
        NormalizeMiniMaxH3VideoCanvasForSmoke(
            int sourceWidth,
            int sourceHeight)
        => NormalizeMiniMaxH3VideoCanvas(sourceWidth, sourceHeight);

    private static bool IsVideoQualitySupported(string presetId)
        => presetId is NormalVideoPresetId or HighVideoPresetId;

    private static int VideoQualitySteps(string presetId)
        => string.Equals(presetId, HighVideoPresetId, StringComparison.Ordinal)
            ? HighVideoSteps
            : NormalVideoSteps;

    private static string VideoQualityLabel(string presetId)
        => string.Equals(presetId, HighVideoPresetId, StringComparison.Ordinal)
            ? "高品質 · 40 step"
            : "標準 · 20 step";

    private static string VideoModelLabel(string modelId)
        => modelId switch
        {
            HunyuanVideoModelId => "HunyuanVideo 1.5 — 実写・人物向け／実験",
            MiniMaxH3VideoModelId => "MiniMax H3 — 高画質・5〜15秒・音声あり",
            _ => "Wan2.2 TI2V 5B — アニメ・汎用",
        };

    private string VideoModelDescription(string modelId)
        => modelId switch
        {
            HunyuanVideoModelId =>
                "実写・人物の顔や手を重視する候補。12GBの隔離ランタイム実測前なので、現在は選択内容の確認だけできます。",
            MiniMaxH3VideoModelId =>
                "MiniMax H3の実測済み高画質プロファイル。元画像比率・32px単位・最大414,720px・24fps・20 step・H.264 / yuv420p・AAC音声あり。"
                + " RTX 4070 SUPER 12GBで5.167秒、10.125秒、12.250秒、15.083秒を選べます。長尺はRAMを大きく使うため、他の重い作業を閉じた就寝・外出中の実行を推奨します。"
                + MiniMaxH3ReadinessSuffix(),
            _ =>
                "RTX 4070 SUPER 12GBで検証済みのモデル。アニメ画像と汎用画像を、RIFE 4.25で正確な30fpsへ仕上げます。",
        };

    private string MiniMaxH3ReadinessSuffix()
        => " " + MiniMaxH3ReservationReadinessStatus();

    private string MiniMaxH3ReservationReadinessStatus()
    {
        if (!_miniMaxH3HealthChecked)
        {
            return "生成環境はまだ確認していません。"
                + "正確な契約を確認できるまでジョブは登録しません。";
        }

        if (_miniMaxH3Ready)
        {
            return "MiniMax H3の準備を確認しました。"
                + "実行すると既存のAI Jobsキューへ追加します。";
        }

        string reason = DescribeMiniMaxH3VideoReasonCode(
            _miniMaxH3ReasonCode);
        bool exactUnreadyContract = _miniMaxH3ReasonCode is not null
            && MiniMaxH3VideoReasonCodes.Contains(
                _miniMaxH3ReasonCode,
                StringComparer.Ordinal);
        return exactUnreadyContract
            ? reason + " 待機ジョブを登録できます。runtime準備後に実行します。"
            : reason + " 正確な契約を確認できるまでジョブは登録しません。";
    }

    private static string DescribeMiniMaxH3VideoReasonCode(string? reasonCode)
        => reasonCode switch
        {
            "MINIMAX_H3_WRITER_DISABLED" =>
                "MiniMax H3の実行runtimeは現在無効です。",
            "MINIMAX_H3_RUNTIME_SEAL_INVALID" =>
                "MiniMax H3の読取専用runtime sealを検証できません。",
            "MINIMAX_H3_RUNTIME_MANIFEST_INVALID" =>
                "MiniMax H3のruntime manifestを検証できません。",
            "MINIMAX_H3_LICENSE_NOT_ACCEPTED" =>
                "MiniMax H3のローカル利用同意を確認できません。",
            "MINIMAX_H3_MODELS_UNVERIFIED" =>
                "MiniMax H3のモデル一式を検証できません。",
            "MINIMAX_H3_WORKFLOW_UNVERIFIED" =>
                "MiniMax H3の固定workflowを検証できません。",
            "MINIMAX_H3_GPU_CANARY_UNVERIFIED" =>
                "MiniMax H3のGPU canary結果を確認できません。",
            "MINIMAX_H3_BACKEND_CONFIG_INVALID" =>
                "MiniMax H3のloopback backend設定を検証できません。",
            "MINIMAX_H3_PROFILES_UNAVAILABLE" =>
                "Aibos ImageのローカルAIサービスが実測済みの5・10・12・15秒プロファイルを公開していません。再起動してください。",
            "HEALTH_UNAVAILABLE" =>
                "Aibos ImageのローカルAIサービスからMiniMax H3の準備状態を取得できません。",
            _ =>
                "MiniMax H3の正確な準備状態を確認できません。",
        };

    private async Task RefreshMiniMaxH3VideoCapabilityAsync()
    {
        long generation = ++_miniMaxH3HealthGeneration;
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health");
        if (generation != _miniMaxH3HealthGeneration)
            return;

        _miniMaxH3HealthChecked = true;
        if (response.Ok
            && response.Payload is JsonElement payload
            && TryParseMiniMaxH3VideoCapability(
                payload,
                out MiniMaxH3VideoCapabilityState capability))
        {
            bool profilesReady =
                TryParseMiniMaxH3VideoProfilesCapability(payload);
            _miniMaxH3Ready = capability.Ready && profilesReady;
            _miniMaxH3ReasonCode = !profilesReady
                ? "MINIMAX_H3_PROFILES_UNAVAILABLE"
                : capability.Ready
                    ? null
                    : capability.ReasonCode;
        }
        else
        {
            _miniMaxH3Ready = false;
            _miniMaxH3ReasonCode = "HEALTH_UNAVAILABLE";
        }

        SyncVideoGenerationSettingsControls();
        if (IsMiniMaxH3VideoModel(_videoModelId)
            && (ModalVideoGenerationPopup.Visibility != Visibility.Visible
                || _videoSourceChoice is not null))
        {
            SetVideoGenerationSettingsStatus(
                MiniMaxH3ReservationReadinessStatus());
        }
    }

    private static string SelectedVideoModelId(ComboBox comboBox)
    {
        string? selected = (comboBox.SelectedItem as ComboBoxItem)
            ?.Tag
            ?.ToString();
        return string.Equals(
                selected,
                MiniMaxH3VideoModelId,
                StringComparison.Ordinal)
            ? MiniMaxH3VideoModelId
            : DefaultVideoModelId;
    }

    private static void SelectVideoModelId(
        ComboBox comboBox,
        string modelId)
    {
        ComboBoxItem? item = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                modelId,
                StringComparison.Ordinal));
        comboBox.SelectedItem = item
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string SelectedVideoQualityId(ComboBox comboBox)
    {
        string? selected = (comboBox.SelectedItem as ComboBoxItem)
            ?.Tag
            ?.ToString();
        return IsVideoQualitySupported(selected ?? "")
            ? selected!
            : DefaultVideoPresetId;
    }

    private static void SelectVideoQualityId(
        ComboBox comboBox,
        string presetId)
    {
        ComboBoxItem? item = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                presetId,
                StringComparison.Ordinal));
        comboBox.SelectedItem = item
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private bool TryCaptureVideoSource(
        Tile tile,
        string? requestedSource,
        out VideoSourceChoice source,
        out string error)
    {
        source = null!;
        error = "";
        if (!TryResolveEnhancementSourceIdentity(
                tile.Path,
                out string sourceIdentity)
            || !File.Exists(sourceIdentity))
        {
            error = "元画像が見つからないため動画化できません。";
            return false;
        }

        string? requestedPhotorealJobId = requestedSource is not null
            && requestedSource.StartsWith(
                PhotorealVideoSourceRequestPrefix,
                StringComparison.Ordinal)
                ? requestedSource[PhotorealVideoSourceRequestPrefix.Length..]
                : null;
        if (requestedPhotorealJobId is not null
            && string.IsNullOrWhiteSpace(requestedPhotorealJobId))
        {
            error = "実写化バージョンのJobを特定できません。";
            return false;
        }

        ManagedEnhancementVersion? photorealVersion = null;
        if (requestedSource is null
            && CurrentModalEnhancementVersionIsPhotoreal())
        {
            if (!TryGetExactDurableCurrentModalEnhancementVersion(
                    tile,
                    out ManagedEnhancementVersion current)
                || !string.Equals(
                    current.Operation,
                    "photoreal",
                    StringComparison.Ordinal))
            {
                error = "表示中の実写版が古いか、Jobを一意に特定できません。実写版を選び直してください。";
                return false;
            }
            photorealVersion = current;
        }
        else if (requestedPhotorealJobId is not null
            || string.Equals(
                requestedSource,
                "photoreal",
                StringComparison.Ordinal))
        {
            bool ambiguousJobId = false;
            foreach (ManagedEnhancementVersion candidate
                     in GetManagedEnhancementVersionsForPath(tile.Path))
            {
                if (!string.Equals(
                        candidate.Operation,
                        "photoreal",
                        StringComparison.Ordinal)
                    || !IsGloballyUniqueManagedJobId(candidate.JobId)
                    || (requestedPhotorealJobId is not null
                        && !string.Equals(
                            candidate.JobId,
                            requestedPhotorealJobId,
                            StringComparison.Ordinal))
                    || !TryCreateManagedEnhancedOutput(
                        tile,
                        candidate.Output.OutputPath,
                        candidate.Output.SourceSize,
                        candidate.Output.SourceMtimeMs,
                        out ManagedEnhancedOutput currentOutput))
                {
                    continue;
                }

                if (photorealVersion is not null)
                {
                    ambiguousJobId = true;
                    break;
                }
                photorealVersion = candidate with
                {
                    Output = currentOutput,
                };
                if (requestedPhotorealJobId is null)
                    break;
            }
            if (ambiguousJobId)
            {
                error = "同じJob IDの実写化バージョンが複数あるため選択できません。";
                return false;
            }
            if (photorealVersion is null)
            {
                error = "この画像には利用できる実写化バージョンがありません。";
                return false;
            }
        }

        if (photorealVersion is not null)
        {
            if (string.IsNullOrWhiteSpace(photorealVersion.JobId)
                || !File.Exists(photorealVersion.Output.OutputPath))
            {
                error = "実写化バージョンのJobまたは出力が見つかりません。";
                return false;
            }

            source = new VideoSourceChoice(
                sourceIdentity,
                photorealVersion.Output.OutputPath,
                photorealVersion.JobId,
                $"実写版 · {Path.GetFileName(photorealVersion.Output.OutputPath)}");
            return true;
        }

        string label = requestedSource is null
            && _modalShowingEnhanced
                ? "Original（高画質化表示は入力対象外）"
                : "Original";
        source = new VideoSourceChoice(
            sourceIdentity,
            sourceIdentity,
            null,
            label);
        return true;
    }

    private bool TryRevalidateCapturedVideoSource(
        out VideoSourceChoice source,
        out string error)
    {
        source = null!;
        error = "動画化の入力を選び直してください。";
        if (_videoSourceChoice is not VideoSourceChoice captured
            || !TryGetVideoGenerationSourceTile(out Tile tile))
        {
            return false;
        }

        string requestedSource = captured.ProducerJobId is null
            ? "original"
            : PhotorealVideoSourceRequestPrefix + captured.ProducerJobId;
        if (!TryCaptureVideoSource(
                tile,
                requestedSource,
                out VideoSourceChoice current,
                out error)
            || !VideoSourceChoicesReferToSameInput(current, captured))
        {
            if (string.IsNullOrWhiteSpace(error))
                error = "動画化の入力が設定中に変わりました。選び直してください。";
            return false;
        }

        source = current;
        return true;
    }

    private bool TryGetVideoGenerationSourceTile(out Tile tile)
    {
        tile = null!;
        if (Modal.Visibility == Visibility.Visible)
        {
            // The modal is pinned independently of the background gallery
            // selection. Favorite/filter publication is allowed to move that
            // selection, but every modal action must keep targeting the image
            // the user can still see.
            return TryGetModalSourceTile(out tile) && tile.IsRealFile;
        }

        if (SelectedTile() is not Tile { IsRealFile: true } selected)
            return false;
        tile = selected;
        return true;
    }

    private string? ValidateVideoSourceImmediatelyBeforePublish(
        Tile capturedTile,
        VideoSourceChoice capturedSource,
        VideoH3SourceStamp capturedStamp,
        bool capturedFromExternalFileDrop)
    {
        if (!TryGetVideoGenerationSourceTile(out Tile currentTile)
            || !ReferenceEquals(currentTile, capturedTile))
            return "動画化の入力セッションが準備確認中に変わりました。予約は保存していません。";

        if (capturedFromExternalFileDrop
            && !TryValidateExternalFileDropTileForEnqueue(
                capturedTile,
                out string externalError))
        {
            return string.IsNullOrWhiteSpace(externalError)
                ? "一時表示の入力セッションが終了しました。動画予約は保存していません。"
                : $"{externalError} 動画予約は保存していません。";
        }

        if (!TryCaptureVideoH3SourceStamp(
                out VideoSourceChoice currentSource,
                out VideoH3SourceStamp currentStamp,
                out string sourceError)
            || !VideoSourceChoicesReferToSameInput(
                currentSource,
                capturedSource)
            || !VideoH3SourceStampsEqual(currentStamp, capturedStamp))
        {
            return string.IsNullOrWhiteSpace(sourceError)
                ? "動画化の入力が準備確認中に変わりました。予約は保存していません。"
                : $"{sourceError} 予約は保存していません。";
        }

        return null;
    }

    private void PopulateGalleryVideoSourceMenu(
        MenuItem videoMenu,
        Tile tile)
    {
        videoMenu.Items.Clear();
        var original = new MenuItem
        {
            Header = "Originalから...",
            Tag = "original",
        };
        AutomationProperties.SetName(
            original,
            "Generate video from Original");
        original.Click += GalleryContextVideo_Click;
        videoMenu.Items.Add(original);

        ManagedEnhancementVersion[] photorealVersions =
            GetManagedEnhancementVersionsForPath(tile.Path)
                .Where(static version => string.Equals(
                    version.Operation,
                    "photoreal",
                    StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(version.JobId))
                .ToArray();
        HashSet<string> ambiguousJobIds = photorealVersions
            .GroupBy(static version => version.JobId, StringComparer.Ordinal)
            .Where(static group => group.Count() != 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        int versionNumber = 0;
        foreach (ManagedEnhancementVersion version in photorealVersions)
        {
            if (ambiguousJobIds.Contains(version.JobId))
                continue;
            string request = PhotorealVideoSourceRequestPrefix + version.JobId;
            if (!TryCaptureVideoSource(tile, request, out _, out _))
                continue;

            versionNumber++;
            string newestLabel = versionNumber == 1 ? "（最新）" : "";
            var item = new MenuItem
            {
                Header =
                    $"実写版 {versionNumber}{newestLabel} · "
                    + $"{Path.GetFileName(version.Output.OutputPath)}から...",
                Tag = request,
            };
            AutomationProperties.SetName(
                item,
                $"Generate video from photoreal version {versionNumber}");
            item.Click += GalleryContextVideo_Click;
            videoMenu.Items.Add(item);
        }

        if (versionNumber == 0)
        {
            var unavailable = new MenuItem
            {
                Header = "利用できる実写版はありません",
                IsEnabled = false,
            };
            AutomationProperties.SetName(
                unavailable,
                "No photoreal version available for video generation");
            videoMenu.Items.Add(unavailable);
        }
    }

    private static (
        int FrameCount,
        int EstimatedMinimumSeconds,
        int EstimatedMaximumSeconds)
        EstimateVideoGeneration(
            int durationSeconds,
            int playbackFps,
            int maximumPixelArea,
            int steps)
    {
        int frameCount = 4 * (durationSeconds * playbackFps / 4) + 1;
        double ScaleWan(double baselineSeconds)
            => baselineSeconds
                * frameCount
                / VideoWanEstimateBaselineFrameCount
                * maximumPixelArea
                / VideoEstimateBaselineMaximumPixelArea
                * steps
                / NormalVideoSteps;
        double ScaleDelivery(double baselineSeconds)
            => baselineSeconds
                * durationSeconds
                / VideoDeliveryEstimateBaselineDurationSeconds
                * maximumPixelArea
                / VideoEstimateBaselineMaximumPixelArea;
        double minimumSeconds =
            ScaleWan(VideoWanLandscapeEstimateBaselineSeconds)
            + ScaleDelivery(
                VideoDeliveryLandscapeEstimateBaselineSeconds);
        double maximumSeconds =
            ScaleWan(VideoWanPortraitEstimateBaselineSeconds)
            + ScaleDelivery(
                VideoDeliveryPortraitEstimateBaselineSeconds);
        return (
            frameCount,
            (int)Math.Round(
                minimumSeconds,
                MidpointRounding.AwayFromZero),
            (int)Math.Ceiling(maximumSeconds));
    }

    private string VideoGenerationEstimateText()
    {
        if (IsMiniMaxH3VideoModel(_videoModelId))
        {
            string measured = NormalizeMiniMaxH3DurationSeconds(
                _videoDurationSeconds) switch
            {
                10 => "約9分49秒以上",
                12 => "約12〜16分",
                15 => "約18分35秒以上",
                _ => "約3分50秒〜約9分7秒",
            };
            return $"完了目安: {measured}"
                + "（RTX 4070 SUPER 12GB実測・最大414,720px・24fps・20 step・キュー待ちを除く。長尺は他の重い作業を閉じて実行）";
        }
        if (!IsVideoModelRunnable(_videoModelId))
        {
            return "完了目安: 未計測（12GB環境の隔離評価を通過するまで実行しません）";
        }

        (
            _,
            int estimatedMinimumSeconds,
            int estimatedMaximumSeconds) = EstimateVideoGeneration(
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            VideoQualitySteps(_videoQualityId));
        static string FormatDuration(int seconds)
            => seconds >= 60
                ? $"{seconds / 60}分{seconds % 60:D2}秒"
                : $"{seconds}秒";
        return "完了目安: 約"
            + $"{FormatDuration(estimatedMinimumSeconds)}〜"
            + $"{FormatDuration(estimatedMaximumSeconds)}"
            + "（Wan生成＋RIFE 4.25仕上げ・RTX 4070 SUPER横長/縦長実測範囲・キュー待ちを除く）";
    }

    private string VideoGenerationDeliveryText()
    {
        if (IsMiniMaxH3VideoModel(_videoModelId))
        {
            int frameCount = MiniMaxH3FrameCountForDuration(
                _videoDurationSeconds);
            string exactDuration = MiniMaxH3ExactDurationSeconds(
                _videoDurationSeconds).ToString(
                    "F3",
                    CultureInfo.InvariantCulture);
            return $"元画像比率出力: 32px単位・最大414,720px・{frameCount}f・24fps・{exactDuration}秒 · 20 step"
                + " · H.264 / yuv420p · AAC音声あり";
        }
        int generationFrameCount =
            4 * (_videoDurationSeconds * _videoPlaybackFps / 4) + 1;
        int deliveryFrameCount = _videoDurationSeconds * 30;
        string duration = _videoDurationSeconds.ToString(
            "F3",
            CultureInfo.InvariantCulture);
        return $"生成: {_videoPlaybackFps} fps・{generationFrameCount}f"
            + $" → 最終出力: 30 fps・{deliveryFrameCount}f・{duration}秒"
            + " · RIFE 4.25 · H.264 / yuv420p · 音声なし";
    }

    private string VideoPixelBudgetHintText(bool includeDefaultPrompt)
    {
        if (IsMiniMaxH3VideoModel(_videoModelId))
        {
            return (includeDefaultPrompt
                    ? "空欄はH3のよく動く既定Dynamic。"
                    : "")
                + "H3 previewは元画像比率に近い32px単位のcanvasを最大414,720px内で選び、ぼかした両サイドを足しません。"
                + "長さは実測済み5/10/12/15秒から選び、FPSとstepは24fps・20 step固定です。"
                + "既存の単一GPUキューで1 jobずつ実行します。";
        }
        string promptHint = includeDefaultPrompt
            ? "空欄はNormalの既定モーション。"
            : "";
        string maximumPixelArea = _videoMaximumPixelArea.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        return promptHint
            + $"選択した画素数上限: {maximumPixelArea}px。"
            + "元画像比率を保ち、32px単位で上限内に自動調整します。"
            + "1 worker・GPU推論の並列化なし。";
    }

    private void OpenModalVideoGeneration_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard(requestedSource: null);

    private void OpenModalVideoSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAppSettings(focusShortcuts: false);
        SelectAppSettingsSection("video", bringIntoView: false);
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (AppSettingsDialog.Visibility != Visibility.Visible)
                    return;
                SelectAppSettingsSection("video", bringIntoView: true);
                SettingsVideoNav?.Focus();
            }),
            DispatcherPriority.Input);
    }

    private void GalleryContextVideo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTile() is not Tile { IsRealFile: true })
            return;

        OpenModal();
        if (Modal.Visibility != Visibility.Visible)
            return;
        string requestedSource = (sender as MenuItem)?.Tag?.ToString()
            ?? "original";
        _ = Dispatcher.BeginInvoke(
            new Action(() => OpenVideoGenerationBoard(requestedSource)),
            DispatcherPriority.Input);
    }

    private void ModalContextVideo_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard(requestedSource: null);

    private void OpenVideoGenerationBoard(string? requestedSource = "original")
    {
        if (ModalVideoGenerationPopup is null)
            return;

        CancelVideoH3PromptRewrite();
        _videoSourceChoice = null;
        string status;
        if (!TryGetVideoGenerationSourceTile(out Tile tile))
        {
            status = "入力画像を選び直してください。設定は確認できますが、入力が確定するまで実行できません。";
        }
        else if (!TryCaptureVideoSource(
                     tile,
                     requestedSource,
                     out VideoSourceChoice source,
                     out string sourceError))
        {
            status = $"入力を確定できません: {sourceError} 設定は確認できますが、実行は無効です。";
            SetTransientStatusToast(sourceError);
        }
        else
        {
            _videoSourceChoice = source;
            status = IsMiniMaxH3VideoModel(_videoModelId)
                ? MiniMaxH3ReservationReadinessStatus()
                : "旧動画モデル設定は新規生成に使いません。MiniMax H3へ切り替えてください。";
        }
        VideoH3PromptRewriteContextChanged(cancelPending: false);
        SyncVideoGenerationSettingsControls();
        VideoGenerationStatusText.Text = status;
        if (ModalUpscaleSettingsPopup is not null)
            ModalUpscaleSettingsPopup.Visibility = Visibility.Collapsed;
        if (ModalPhotorealSettingsPopup is not null)
            ModalPhotorealSettingsPopup.Visibility = Visibility.Collapsed;
        ModalVideoGenerationPopup.Visibility = Visibility.Visible;
        if (IsMiniMaxH3VideoModel(_videoModelId))
            _ = RefreshMiniMaxH3VideoCapabilityAsync();
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModalVideoGenerationPopup.Visibility == Visibility.Visible)
                    Keyboard.Focus(ModalVideoPromptTextBox);
            }),
            DispatcherPriority.Input);
    }

    private void CloseVideoGenerationBoard_Click(object sender, RoutedEventArgs e)
        => CloseModalVideoGenerationBoard();

    private void CloseModalVideoGenerationBoard()
    {
        CancelVideoH3PromptRewrite();
        if (ModalVideoGenerationPopup is not null)
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
        ModalOverflowButton?.Focus();
    }

    private void ModalVideoGenerationBackdrop_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModalVideoGenerationPopup.Visibility == Visibility.Visible
            && ReferenceEquals(e.OriginalSource, ModalVideoGenerationPopup))
        {
            CloseModalVideoGenerationBoard();
            e.Handled = true;
        }
    }

    private void VideoGenerationSetting_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || ModalVideoDurationComboBox is null
            || ModalVideoFpsComboBox is null
            || ModalVideoResolutionComboBox is null)
        {
            return;
        }

        ComboBox durationSource = ReferenceEquals(sender, AppVideoDurationComboBox)
            ? AppVideoDurationComboBox
            : ModalVideoDurationComboBox;
        ComboBox fpsSource = ReferenceEquals(sender, AppVideoFpsComboBox)
            ? AppVideoFpsComboBox
            : ModalVideoFpsComboBox;
        ComboBox resolutionSource = ReferenceEquals(sender, AppVideoResolutionComboBox)
            ? AppVideoResolutionComboBox
            : ModalVideoResolutionComboBox;
        _videoDurationSeconds = SelectedIntegerTag(
            durationSource,
            DefaultVideoDurationSeconds,
            SupportedVideoDurationSeconds);
        _videoPlaybackFps = SelectedIntegerTag(
            fpsSource,
            DefaultVideoPlaybackFps,
            SupportedVideoPlaybackFps);
        _videoMaximumPixelArea = SelectedIntegerTag(
            resolutionSource,
            DefaultVideoMaximumPixelArea,
            SupportedVideoMaximumPixelAreas);
        MarkVideoStyleAsCustom();
        VideoH3PromptRewriteContextChanged();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus("保存済み。次に追加する動画ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void MiniMaxH3Duration_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings || sender is not ComboBox source)
            return;

        _videoDurationSeconds = SelectedIntegerTag(
            source,
            MiniMaxH3VideoDefaultNominalDurationSeconds,
            SupportedMiniMaxH3VideoDurationSeconds);
        MarkVideoStyleAsCustom();
        VideoH3PromptRewriteContextChanged();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            $"MiniMax H3の高画質{MiniMaxH3ExactDurationSeconds(_videoDurationSeconds):F3}秒プロファイルを保存しました。次に追加する動画ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void VideoGenerationModel_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || AppVideoModelComboBox is null
            || ModalVideoModelComboBox is null)
            return;

        ComboBox source = ReferenceEquals(sender, AppVideoModelComboBox)
            ? AppVideoModelComboBox
            : ModalVideoModelComboBox;
        _videoModelId = SelectedVideoModelId(source);
        MarkVideoStyleAsCustom();
        VideoH3PromptRewriteContextChanged();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            _videoModelId switch
            {
                MiniMaxH3VideoModelId =>
                    DescribeMiniMaxH3VideoReasonCode(
                        _miniMaxH3HealthChecked
                            ? _miniMaxH3ReasonCode
                            : null)
                    + " 受動health確認中です。",
                _ => "旧動画モデルは新規生成UIから退役しています。MiniMax H3を使います。",
            });
        if (IsMiniMaxH3VideoModel(_videoModelId))
            _ = RefreshMiniMaxH3VideoCapabilityAsync();
        if (!_initializing)
            SaveState();
    }

    private void VideoGenerationQuality_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || AppVideoQualityComboBox is null
            || ModalVideoQualityComboBox is null)
        {
            return;
        }

        ComboBox source = ReferenceEquals(sender, AppVideoQualityComboBox)
            ? AppVideoQualityComboBox
            : ModalVideoQualityComboBox;
        _videoQualityId = SelectedVideoQualityId(source);
        MarkVideoStyleAsCustom();
        VideoH3PromptRewriteContextChanged();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            $"{VideoQualityLabel(_videoQualityId)}を次の動画ジョブに使います。");
        if (!_initializing)
            SaveState();
    }

    private void VideoSeedMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings || sender is not ComboBox source)
            return;

        _videoSeedFixed = SelectedSeedModeIsFixed(source);
        SyncVideoSeedControls();
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus(
            _videoSeedFixed
                ? TryParseFixedSeed(_videoSeedValueText, out _)
                    ? "Fixed Seedを保存しました。次の動画化ジョブから使われます。"
                    : "Fixed Seedは0〜2147483647の整数で入力してください。"
                : "Random Seedを使います。ジョブ追加時に新しいSeedを決めます。");
        if (!_initializing)
            SaveState();
    }

    private void VideoSeedValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings || sender is not TextBox source)
            return;

        _videoSeedValueText = source.Text;
        SyncVideoSeedControls();
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus(
            !_videoSeedFixed
                ? "Random Seedを使います。Fixedへ切り替えるまで数値は送信しません。"
                : TryParseFixedSeed(_videoSeedValueText, out _)
                    ? "Fixed Seedを保存しました。次の動画化ジョブから使われます。"
                    : "Fixed Seedは0〜2147483647の整数で入力してください。");
        if (!_initializing)
            SaveState();
    }

    private void VideoGenerationPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings)
            return;

        TextBox source = ReferenceEquals(sender, AppVideoPromptTextBox)
            ? AppVideoPromptTextBox
            : ModalVideoPromptTextBox;
        _videoPrompt = source.Text.Length <= MaxVideoPromptLength
            ? source.Text
            : source.Text[..MaxVideoPromptLength];
        MarkVideoPromptTemplateAsCustom();
        InvalidateVideoH3PromptUndoAfterManualEdit();
        MarkVideoStyleAsCustom();
        SyncVideoPromptPeer(source);
        VideoH3PromptRewriteContextChanged();
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus(
            string.IsNullOrWhiteSpace(_videoPrompt)
                ? IsMiniMaxH3VideoModel(_videoModelId)
                    ? "空欄はH3のよく動く既定Dynamicと画像に合う環境音を使います。保存済みです。"
                    : "空欄はNormalの既定モーションを使います。保存済みです。"
                : "保存済み。入力した動きが次の動画ジョブに使われます。");
        if (!_initializing)
            SaveState();
    }

    private void SyncVideoPromptPeer(TextBox source)
    {
        TextBox? peer = ReferenceEquals(source, AppVideoPromptTextBox)
            ? ModalVideoPromptTextBox
            : AppVideoPromptTextBox;
        if (peer is null || string.Equals(peer.Text, _videoPrompt, StringComparison.Ordinal))
            return;

        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            peer.Text = _videoPrompt;
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void ResetVideoGenerationSettings_Click(object sender, RoutedEventArgs e)
    {
        _selectedVideoStyleName = null;
        RestoreVideoGenerationSettings(
            null,
            null,
            null,
            null,
            null,
            null);
        RestoreVideoSeedSettings(null, null);
        VideoH3PromptRewriteContextChanged();
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoGenerationSettingsStatus(
            "MiniMax H3の高画質5.167秒・24fps・元画像比率・20 step既定へ戻しました。準備確認後に実行できます。");
        if (!_initializing)
            SaveState();
    }

    private void SetVideoGenerationSettingsStatus(string message)
    {
        if (VideoGenerationStatusText is not null)
            VideoGenerationStatusText.Text = message;
        if (AppVideoSettingsStatusText is not null)
            AppVideoSettingsStatusText.Text = message;
    }

    private void VideoPromptTemplate_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not VideoPromptTemplateChoice choice)
        {
            return;
        }

        _selectedVideoPromptTemplateId = choice.Id;
        if (string.Equals(
                choice.Id,
                CustomVideoPromptTemplateId,
                StringComparison.Ordinal))
        {
            RefreshVideoPromptTemplateControls();
            SetVideoGenerationSettingsStatus(
                "現在のPromptをそのまま使います。MiniMax H3では必要ならMiniMax語化できます。");
            return;
        }

        TextBox target = ReferenceEquals(sender, AppVideoPromptTemplateComboBox)
            ? AppVideoPromptTextBox
            : ModalVideoPromptTextBox;
        _applyingVideoPromptTemplate = true;
        try
        {
            target.Text = choice.Prompt;
        }
        finally
        {
            _applyingVideoPromptTemplate = false;
        }
        RefreshVideoPromptTemplateControls();
        SetVideoGenerationSettingsStatus(
            $"「{choice.Label}」をPromptへ反映しました。画像に合わせる場合はMiniMax語化してください。");
    }

    private void MarkVideoPromptTemplateAsCustom()
    {
        if (_applyingVideoPromptTemplate
            || string.Equals(
                _selectedVideoPromptTemplateId,
                CustomVideoPromptTemplateId,
                StringComparison.Ordinal))
        {
            return;
        }
        _selectedVideoPromptTemplateId = CustomVideoPromptTemplateId;
        RefreshVideoPromptTemplateControls();
    }

    private void RefreshVideoPromptTemplateControls()
    {
        if (ModalVideoPromptTemplateComboBox is null
            || AppVideoPromptTemplateComboBox is null)
        {
            return;
        }
        VideoPromptTemplateChoice selected = VideoPromptTemplates.First(
            template => string.Equals(
                template.Id,
                _selectedVideoPromptTemplateId,
                StringComparison.Ordinal));
        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            if (!ReferenceEquals(
                    ModalVideoPromptTemplateComboBox.ItemsSource,
                    VideoPromptTemplates))
            {
                ModalVideoPromptTemplateComboBox.ItemsSource =
                    VideoPromptTemplates;
            }
            if (!ReferenceEquals(
                    AppVideoPromptTemplateComboBox.ItemsSource,
                    VideoPromptTemplates))
            {
                AppVideoPromptTemplateComboBox.ItemsSource =
                    VideoPromptTemplates;
            }
            ModalVideoPromptTemplateComboBox.SelectedItem = selected;
            AppVideoPromptTemplateComboBox.SelectedItem = selected;
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void VideoStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings)
            return;

        VideoStyleChoice? choice = sender switch
        {
            ComboBox comboBox => comboBox.SelectedItem as VideoStyleChoice,
            ListBox listBox => listBox.SelectedItem as VideoStyleChoice,
            _ => null,
        };
        if (choice is null)
            return;

        if (choice.StyleName is null)
        {
            _selectedVideoStyleName = null;
            VideoH3PromptRewriteContextChanged();
            RefreshVideoStyleControls(updateNameFields: false);
            SetVideoStyleStatus("現在の設定を使用します。Styleにはまだ保存されていません。");
            if (!_initializing)
                SaveState();
            return;
        }

        VideoStyleState? style = FindVideoStyle(choice.StyleName);
        if (style is null)
            return;

        _selectedVideoStyleName = style.Name;
        _selectedVideoPromptTemplateId = CustomVideoPromptTemplateId;
        RestoreVideoGenerationSettings(
            style.DurationSeconds,
            style.PlaybackFps,
            style.MaximumPixelArea,
            style.Prompt,
            style.ModelId,
            style.QualityId);
        VideoH3PromptRewriteContextChanged();
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus($"「{style.Name}」を反映しました。次に追加する動画ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void SaveVideoStyle_Click(object sender, RoutedEventArgs e)
    {
        TextBox nameTextBox = ReferenceEquals(sender, SaveModalVideoStyleButton)
            ? ModalVideoStyleNameTextBox
            : AppVideoStyleNameTextBox;
        string name = nameTextBox.Text.Trim();
        if (!IsValidVideoStyleName(name))
        {
            SetVideoStyleStatus($"Style名は1～{MaxVideoStyleNameLength}文字で入力してください。制御文字は使えません。");
            return;
        }

        VideoStyleState style = CreateCurrentVideoStyle(name);
        int existingIndex = _videoStyles.FindIndex(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _videoStyles[existingIndex] = style;
        }
        else
        {
            if (_videoStyles.Count >= MaxVideoStyleCount)
            {
                SetVideoStyleStatus($"Styleは最大{MaxVideoStyleCount}件です。不要なStyleを削除してください。");
                return;
            }
            _videoStyles.Add(style);
        }

        _videoStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        _selectedVideoStyleName = style.Name;
        VideoH3PromptRewriteContextChanged();
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus(
            existingIndex >= 0
                ? $"「{style.Name}」を現在の設定で上書きしました。"
                : $"「{style.Name}」を保存しました。");
        if (!_initializing)
            SaveState();
    }

    private void DeleteVideoStyle_Click(object sender, RoutedEventArgs e)
    {
        VideoStyleState? style = FindVideoStyle(_selectedVideoStyleName);
        if (style is null)
        {
            SetVideoStyleStatus("削除する保存済みStyleを選んでください。");
            return;
        }

        _videoStyles.Remove(style);
        _selectedVideoStyleName = null;
        VideoH3PromptRewriteContextChanged();
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus($"「{style.Name}」を削除しました。現在の設定値はそのまま残ります。");
        if (!_initializing)
            SaveState();
    }

    private void RestoreVideoStyles(
        IEnumerable<VideoStyleState>? styles,
        string? selectedStyleName)
    {
        _videoStyles.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VideoStyleState? candidate in styles ?? [])
        {
            VideoStyleState? normalized = NormalizeVideoStyle(candidate);
            if (normalized is null || !names.Add(normalized.Name))
                continue;

            _videoStyles.Add(normalized);
            if (_videoStyles.Count >= MaxVideoStyleCount)
                break;
        }
        _videoStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));

        VideoStyleState? selected = FindVideoStyle(selectedStyleName);
        _selectedVideoStyleName = selected is not null && VideoStyleMatchesCurrent(selected)
            ? selected.Name
            : null;
        RefreshVideoStyleControls(updateNameFields: true);
    }

    private static VideoStyleState? NormalizeVideoStyle(VideoStyleState? candidate)
    {
        if (candidate is null)
            return null;

        string name = candidate.Name?.Trim() ?? "";
        if (!IsValidVideoStyleName(name)
            || candidate.ModelId is not (
                WanVideoModelId
                or HunyuanVideoModelId
                or MiniMaxH3VideoModelId)
            || !IsVideoQualitySupported(candidate.QualityId ?? "")
            || (!SupportedVideoDurationSeconds.Contains(candidate.DurationSeconds)
                && !SupportedMiniMaxH3VideoDurationSeconds.Contains(
                    candidate.DurationSeconds))
            || !SupportedVideoPlaybackFps.Contains(candidate.PlaybackFps)
            || !SupportedVideoMaximumPixelAreas.Contains(candidate.MaximumPixelArea))
        {
            return null;
        }

        string prompt = candidate.Prompt ?? "";
        if (prompt.Length > MaxVideoPromptLength)
            prompt = prompt[..MaxVideoPromptLength];
        return new VideoStyleState
        {
            Name = name,
            // Wan and Hunyuan remain readable in historical jobs, but saved
            // new-job styles are migrated to the only selectable writer.
            ModelId = MiniMaxH3VideoModelId,
            QualityId = candidate.QualityId!,
            DurationSeconds = NormalizeMiniMaxH3DurationSeconds(
                candidate.DurationSeconds),
            PlaybackFps = candidate.PlaybackFps,
            MaximumPixelArea = candidate.MaximumPixelArea,
            Prompt = prompt,
        };
    }

    private static bool IsValidVideoStyleName(string name)
        => name.Length is >= 1 and <= MaxVideoStyleNameLength
            && !name.Any(char.IsControl);

    private VideoStyleState CreateCurrentVideoStyle(string name)
        => new()
        {
            Name = name,
            ModelId = _videoModelId,
            QualityId = _videoQualityId,
            DurationSeconds = _videoDurationSeconds,
            PlaybackFps = _videoPlaybackFps,
            MaximumPixelArea = _videoMaximumPixelArea,
            Prompt = _videoPrompt,
        };

    private VideoStyleState? FindVideoStyle(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _videoStyles.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool VideoStyleMatchesCurrent(VideoStyleState style)
        => string.Equals(style.ModelId, _videoModelId, StringComparison.Ordinal)
            && string.Equals(style.QualityId, _videoQualityId, StringComparison.Ordinal)
            && style.DurationSeconds == _videoDurationSeconds
            && style.PlaybackFps == _videoPlaybackFps
            && style.MaximumPixelArea == _videoMaximumPixelArea
            && string.Equals(style.Prompt, _videoPrompt, StringComparison.Ordinal);

    private void MarkVideoStyleAsCustom()
    {
        if (_syncingVideoGenerationSettings)
            return;

        bool selectionChanged = _selectedVideoStyleName is not null;
        _selectedVideoStyleName = null;
        if (selectionChanged)
        {
            RefreshVideoStyleControls(updateNameFields: false);
            SetVideoStyleStatus("設定を変更しました。保存済みStyleは上書きされていません。");
        }
        else
        {
            RefreshVideoStyleSummary();
        }
    }

    private void RefreshVideoStyleControls(bool updateNameFields)
    {
        if (ModalVideoStyleComboBox is null
            || AppVideoStyleListBox is null
            || ModalVideoStyleNameTextBox is null
            || AppVideoStyleNameTextBox is null
            || DeleteModalVideoStyleButton is null
            || DeleteAppVideoStyleButton is null)
        {
            return;
        }

        var choices = new List<VideoStyleChoice>
        {
            new("カスタム（現在の設定）", null),
        };
        choices.AddRange(_videoStyles.Select(static style =>
            new VideoStyleChoice(style.Name, style.Name)));
        VideoStyleChoice selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.StyleName, _selectedVideoStyleName, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            ModalVideoStyleComboBox.ItemsSource = choices;
            AppVideoStyleListBox.ItemsSource = choices;
            ModalVideoStyleComboBox.SelectedItem = selectedChoice;
            AppVideoStyleListBox.SelectedItem = selectedChoice;
            bool canDelete = selectedChoice.StyleName is not null;
            DeleteModalVideoStyleButton.IsEnabled = canDelete;
            DeleteAppVideoStyleButton.IsEnabled = canDelete;
            if (updateNameFields)
            {
                string name = selectedChoice.StyleName ?? "";
                ModalVideoStyleNameTextBox.Text = name;
                AppVideoStyleNameTextBox.Text = name;
            }
            RefreshVideoStyleSummary();
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void RefreshVideoStyleSummary()
    {
        if (AppVideoStyleSummaryText is null)
            return;

        AppVideoStyleSummaryText.Text = IsMiniMaxH3VideoModel(_videoModelId)
            ? $"現在: {VideoModelLabel(_videoModelId)} / {MiniMaxH3ExactDurationSeconds(_videoDurationSeconds):F3}秒 / 24fps / 元画像比率・最大414,720px / 20 step / AAC"
            : $"現在: {VideoModelLabel(_videoModelId)} / {VideoQualityLabel(_videoQualityId)} / {_videoDurationSeconds}秒 / 生成{_videoPlaybackFps}fps / {_videoMaximumPixelArea.ToString("N0", CultureInfo.InvariantCulture)}px";
    }

    private void SetVideoStyleStatus(string message)
    {
        if (ModalVideoStyleStatusText is not null)
            ModalVideoStyleStatusText.Text = message;
        if (AppVideoStyleStatusText is not null)
            AppVideoStyleStatusText.Text = message;
    }

    private List<VideoStyleState>? SnapshotVideoStyles()
        => _videoStyles.Count == 0
            ? null
            : _videoStyles.Select(static style => new VideoStyleState
            {
                Name = style.Name,
                ModelId = style.ModelId,
                QualityId = style.QualityId,
                DurationSeconds = style.DurationSeconds,
                PlaybackFps = style.PlaybackFps,
                MaximumPixelArea = style.MaximumPixelArea,
                Prompt = style.Prompt,
            }).ToList();

    private void RestoreVideoGenerationSettings(
        int? durationSeconds,
        int? playbackFps,
        int? maximumPixelArea,
        string? prompt,
        string? modelId = null,
        string? qualityId = null)
    {
        _videoDurationSeconds = NormalizeMiniMaxH3DurationSeconds(
            durationSeconds ?? MiniMaxH3VideoDefaultNominalDurationSeconds);
        _videoPlaybackFps = playbackFps is int fps
            && SupportedVideoPlaybackFps.Contains(fps)
                ? fps
                : DefaultVideoPlaybackFps;
        _videoMaximumPixelArea = maximumPixelArea is int area
            && SupportedVideoMaximumPixelAreas.Contains(area)
                ? area
                : DefaultVideoMaximumPixelArea;
        // Retain legacy model ids in job readers, not in the new-job surface.
        // Persisted Wan/Hunyuan selections migrate to H3 without deleting the
        // user's prompt or other saved request values.
        _videoModelId = DefaultVideoModelId;
        _videoQualityId = IsVideoQualitySupported(qualityId ?? "")
            ? qualityId!
            : DefaultVideoPresetId;
        string restoredPrompt = prompt ?? "";
        _videoPrompt = restoredPrompt.Length <= MaxVideoPromptLength
            ? restoredPrompt
            : restoredPrompt[..MaxVideoPromptLength];
        _selectedVideoPromptTemplateId = CustomVideoPromptTemplateId;
        SyncVideoGenerationSettingsControls();
    }

    private void RestoreVideoSeedSettings(string? mode, int? value)
    {
        _videoSeedFixed = string.Equals(
            mode,
            FixedSeedMode,
            StringComparison.OrdinalIgnoreCase);
        _videoSeedValueText = RestoreSeedValueText(_videoSeedFixed, value);
        SyncVideoSeedControls();
    }

    private void SyncVideoSeedControls()
    {
        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            if (ModalVideoSeedModeComboBox is not null)
                SelectSeedMode(ModalVideoSeedModeComboBox, _videoSeedFixed);
            if (AppVideoSeedModeComboBox is not null)
                SelectSeedMode(AppVideoSeedModeComboBox, _videoSeedFixed);
            if (ModalVideoSeedValueTextBox is not null)
            {
                ModalVideoSeedValueTextBox.Text = _videoSeedValueText;
                ModalVideoSeedValueTextBox.IsEnabled = _videoSeedFixed;
            }
            if (AppVideoSeedValueTextBox is not null)
            {
                AppVideoSeedValueTextBox.Text = _videoSeedValueText;
                AppVideoSeedValueTextBox.IsEnabled = _videoSeedFixed;
            }
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void SyncVideoGenerationSettingsControls()
    {
        if (ModalVideoDurationComboBox is null
            || ModalVideoFpsComboBox is null
            || ModalVideoResolutionComboBox is null
            || ModalVideoH3DurationComboBox is null
            || ModalVideoQualityComboBox is null
            || ModalVideoPromptTextBox is null)
        {
            return;
        }

        _syncingVideoGenerationSettings = true;
        try
        {
            SelectIntegerTag(ModalVideoDurationComboBox, _videoDurationSeconds);
            SelectIntegerTag(
                ModalVideoH3DurationComboBox,
                NormalizeMiniMaxH3DurationSeconds(_videoDurationSeconds));
            SelectIntegerTag(ModalVideoFpsComboBox, _videoPlaybackFps);
            SelectIntegerTag(ModalVideoResolutionComboBox, _videoMaximumPixelArea);
            SelectVideoModelId(ModalVideoModelComboBox, _videoModelId);
            SelectVideoQualityId(
                ModalVideoQualityComboBox,
                _videoQualityId);
            ModalVideoPromptTextBox.Text = _videoPrompt;
            if (AppVideoDurationComboBox is not null)
                SelectIntegerTag(AppVideoDurationComboBox, _videoDurationSeconds);
            if (AppVideoH3DurationComboBox is not null)
            {
                SelectIntegerTag(
                    AppVideoH3DurationComboBox,
                    NormalizeMiniMaxH3DurationSeconds(
                        _videoDurationSeconds));
            }
            if (AppVideoFpsComboBox is not null)
                SelectIntegerTag(AppVideoFpsComboBox, _videoPlaybackFps);
            if (AppVideoResolutionComboBox is not null)
                SelectIntegerTag(AppVideoResolutionComboBox, _videoMaximumPixelArea);
            if (AppVideoModelComboBox is not null)
                SelectVideoModelId(AppVideoModelComboBox, _videoModelId);
            if (AppVideoQualityComboBox is not null)
            {
                SelectVideoQualityId(
                    AppVideoQualityComboBox,
                    _videoQualityId);
            }
            if (AppVideoPromptTextBox is not null)
                AppVideoPromptTextBox.Text = _videoPrompt;
            SyncVideoSeedControls();
            string modelDescription = VideoModelDescription(_videoModelId);
            bool h3Selected = IsMiniMaxH3VideoModel(_videoModelId);
            string qualityLabel = h3Selected
                ? "元画像比率プレビュー · 20 step"
                : VideoQualityLabel(_videoQualityId);
            ModalVideoPresetText.Text =
                $"{VideoModelLabel(_videoModelId)} · {qualityLabel}";
            ModalVideoModelDescriptionText.Text = modelDescription;
            AppVideoModelDescriptionText.Text = modelDescription;
            Visibility wanControlsVisibility = Visibility.Collapsed;
            if (ModalVideoWanControlsPanel is not null)
                ModalVideoWanControlsPanel.Visibility = wanControlsVisibility;
            if (ModalVideoWanTuningPanel is not null)
                ModalVideoWanTuningPanel.Visibility = wanControlsVisibility;
            if (AppVideoWanControlsPanel is not null)
                AppVideoWanControlsPanel.Visibility = wanControlsVisibility;
            Visibility h3ControlsVisibility = h3Selected
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (ModalVideoH3ControlsPanel is not null)
                ModalVideoH3ControlsPanel.Visibility = h3ControlsVisibility;
            if (AppVideoH3ControlsPanel is not null)
                AppVideoH3ControlsPanel.Visibility = h3ControlsVisibility;
            ModalVideoH3DurationComboBox.IsEnabled = h3Selected;
            if (AppVideoH3DurationComboBox is not null)
                AppVideoH3DurationComboBox.IsEnabled = h3Selected;
            bool qualityEnabled = string.Equals(
                _videoModelId,
                WanVideoModelId,
                StringComparison.Ordinal);
            ModalVideoQualityComboBox.IsEnabled = qualityEnabled;
            if (AppVideoQualityComboBox is not null)
                AppVideoQualityComboBox.IsEnabled = qualityEnabled;
            bool wanTuningEnabled = !h3Selected;
            ModalVideoDurationComboBox.IsEnabled = wanTuningEnabled;
            ModalVideoFpsComboBox.IsEnabled = wanTuningEnabled;
            ModalVideoResolutionComboBox.IsEnabled = wanTuningEnabled;
            if (AppVideoDurationComboBox is not null)
                AppVideoDurationComboBox.IsEnabled = wanTuningEnabled;
            if (AppVideoFpsComboBox is not null)
                AppVideoFpsComboBox.IsEnabled = wanTuningEnabled;
            if (AppVideoResolutionComboBox is not null)
                AppVideoResolutionComboBox.IsEnabled = wanTuningEnabled;
            bool seedControlsEnabled = !h3Selected;
            if (ModalVideoSeedModeComboBox is not null)
                ModalVideoSeedModeComboBox.IsEnabled = seedControlsEnabled;
            if (ModalVideoSeedValueTextBox is not null)
            {
                ModalVideoSeedValueTextBox.IsEnabled = seedControlsEnabled
                    && _videoSeedFixed;
            }
            if (AppVideoSeedModeComboBox is not null)
                AppVideoSeedModeComboBox.IsEnabled = seedControlsEnabled;
            if (AppVideoSeedValueTextBox is not null)
            {
                AppVideoSeedValueTextBox.IsEnabled = seedControlsEnabled
                    && _videoSeedFixed;
            }
            string promptHelp = h3Selected
                ? "Blank uses the built-in MiniMax H3 dynamic two-phase motion and image-consistent ambient sound prompt."
                : "Blank uses the built-in conservative Normal motion prompt.";
            AutomationProperties.SetHelpText(
                ModalVideoPromptTextBox,
                promptHelp);
            if (AppVideoPromptTextBox is not null)
                AutomationProperties.SetHelpText(AppVideoPromptTextBox, promptHelp);
            ModalVideoSourceText.Text = _videoSourceChoice is null
                ? "入力: 拡大画面を開いた時点の画像"
                : $"入力: {_videoSourceChoice.Label}";
            string estimateText = VideoGenerationEstimateText();
            if (ModalVideoGenerationEstimateText is not null)
                ModalVideoGenerationEstimateText.Text = estimateText;
            if (AppVideoGenerationEstimateText is not null)
                AppVideoGenerationEstimateText.Text = estimateText;
            string deliveryText = VideoGenerationDeliveryText();
            if (ModalVideoDeliveryText is not null)
                ModalVideoDeliveryText.Text = deliveryText;
            if (AppVideoDeliveryText is not null)
                AppVideoDeliveryText.Text = deliveryText;
            if (ModalVideoResolutionHintText is not null)
                ModalVideoResolutionHintText.Text =
                    VideoPixelBudgetHintText(false);
            if (AppVideoResolutionHintText is not null)
                AppVideoResolutionHintText.Text =
                    VideoPixelBudgetHintText(true);
        }
        finally
        {
            _syncingVideoGenerationSettings = false;
        }
        RefreshVideoPromptTemplateControls();
        RefreshVideoH3PromptRewriteControls();
        UpdateVideoGenerationActionControls();
    }

    private void UpdateVideoGenerationActionControls()
    {
        if (ModalVideoGenerateButton is null
            || QueueVideoGenerationButton is null)
        {
            return;
        }

        // This is presentation state only. The explicit execute path below
        // canonicalizes the selected source and verifies that it still exists
        // before sending any request.
        bool hasSource = TryGetVideoGenerationSourceTile(out _);
        bool capturedSourceReady = TryRevalidateCapturedVideoSource(out _, out _);
        bool modelRegistered = IsMiniMaxH3VideoModel(_videoModelId);
        bool seedReady = IsMiniMaxH3VideoModel(_videoModelId)
            || !_videoSeedFixed
            || TryParseFixedSeed(_videoSeedValueText, out _);
        ModalVideoGenerateButton.IsEnabled = hasSource && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.IsEnabled =
            capturedSourceReady
            && modelRegistered
            && seedReady
            && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.Content = _videoGenerationRequestPending
            ? "追加中..."
            : modelRegistered
                ? "H3動画化をキューへ追加"
                : "動画モデルを確認";
        AutomationProperties.SetName(
            QueueVideoGenerationButton,
            _videoGenerationRequestPending
                ? "Adding video generation job"
                : "Add video generation job");
    }

    private async void QueueVideoGeneration_Click(object sender, RoutedEventArgs e)
        => await QueueVideoGenerationAsync();

    private async Task<bool> QueueVideoGenerationAsync()
    {
        if (_videoGenerationRequestPending)
            return false;

        if (!TryRevalidateCapturedVideoSource(
                out VideoSourceChoice source,
                out string sourceError))
        {
            if (!string.IsNullOrWhiteSpace(sourceError))
                SetVideoGenerationSettingsStatus(sourceError);
            return false;
        }
        Tile? capturedSourceTile = TryGetVideoGenerationSourceTile(out Tile currentTile)
            ? currentTile
            : null;
        if (!IsMiniMaxH3VideoModel(_videoModelId))
        {
            SetVideoGenerationSettingsStatus(
                "旧Wan/Hunyuanモデルは新規動画化に使えません。MiniMax H3へ切り替えてください。ジョブは追加していません。");
            UpdateVideoGenerationActionControls();
            return false;
        }
        bool h3Selected = IsMiniMaxH3VideoModel(_videoModelId);
        int? seed = null;
        if (!h3Selected
            && !TryResolveVideoSeed(out seed, out string seedError))
        {
            SetVideoGenerationSettingsStatus(seedError);
            UpdateVideoGenerationActionControls();
            return false;
        }

        VideoGenerationRequestSettings settings =
            CurrentVideoGenerationRequestSettings();
        _videoGenerationRequestPending = true;
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus("ローカル動画生成の準備を確認しています...");
        try
        {
            Func<JsonElement, string?>? healthValidator = h3Selected
                ? CreateMiniMaxH3VideoHealthValidator()
                : seed.HasValue
                    ? CreateEnhancementCapabilityHealthValidator(
                        VideoSeedControlCapability,
                        "fixed video seeds")
                    : null;

            if (!TryRevalidateCapturedVideoSource(
                    out VideoSourceChoice revalidatedSource,
                    out sourceError)
                || !VideoSourceChoicesReferToSameInput(
                    revalidatedSource,
                    source))
            {
                _videoSourceChoice = null;
                SetVideoGenerationSettingsStatus(
                    string.IsNullOrWhiteSpace(sourceError)
                        ? "動画化の入力が準備確認中に変わりました。選び直してください。"
                        : sourceError);
                return false;
            }
            source = revalidatedSource;

            if (capturedSourceTile is null
                || !TryCaptureVideoH3SourceStamp(
                    out VideoSourceChoice stampedSource,
                    out VideoH3SourceStamp sourceStamp,
                    out sourceError)
                || !VideoSourceChoicesReferToSameInput(
                    stampedSource,
                    source))
            {
                _videoSourceChoice = null;
                SetVideoGenerationSettingsStatus(
                    string.IsNullOrWhiteSpace(sourceError)
                        ? "動画化の入力を再確認できません。選び直してください。"
                        : sourceError);
                return false;
            }
            source = stampedSource;
            bool capturedFromExternalFileDrop =
                IsExternalFileDropSessionTile(capturedSourceTile);

            Dictionary<string, object?> requestBody =
                BuildVideoGenerationRequestBody(
                    source,
                    settings,
                    h3Selected,
                    seed);

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                requestBody,
                includeQueuePlacementInBody: false,
                healthValidator: healthValidator,
                requireExactHealthValidation: h3Selected,
                recoverySourceIdentity: source.SourceIdentity,
                prePublishValidator: () =>
                    ValidateVideoSourceImmediatelyBeforePublish(
                        capturedSourceTile,
                        source,
                        sourceStamp,
                        capturedFromExternalFileDrop));
            if (response.SavedForDelivery)
            {
                SetVideoGenerationSettingsStatus(
                    "動画化の予約を保存しました。Jobsへの登録を継続しています。");
                SetTransientStatusToast(
                    $"{Path.GetFileName(source.SourceIdentity)}: 動画化の予約を保存しました。登録を継続しています。");
                ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
                return true;
            }
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || !payload.TryGetProperty("job", out JsonElement job)
                || job.ValueKind != JsonValueKind.Object)
            {
                SetVideoGenerationSettingsStatus(response.Error);
                return false;
            }

            TryGetStringProperty(job, "id", out string? jobId);
            ApplyActiveEnhancementQueueJobToVisibleCatalog(job, capturedSourceTile);
            string suffix = string.IsNullOrWhiteSpace(jobId)
                ? ""
                : $" ({jobId})";
            bool executionDeferred = payload.TryGetProperty(
                    "executionDeferred",
                    out JsonElement deferredElement)
                && deferredElement.ValueKind == JsonValueKind.True;
            if (executionDeferred)
            {
                TryGetStringProperty(
                    payload,
                    "executionReasonCode",
                    out string? executionReasonCode);
                string reason = DescribeMiniMaxH3VideoReasonCode(
                    executionReasonCode);
                SetVideoGenerationSettingsStatus(
                    $"MiniMax H3待機ジョブを登録しました{suffix}。{reason} runtime準備後に実行します。");
                SetTransientStatusToast(
                    $"{Path.GetFileName(source.SourceIdentity)}: MiniMax H3待機ジョブをJobsへ登録しました。");
            }
            else
            {
                SetVideoGenerationSettingsStatus(
                    $"動画ジョブを共有GPUキューへ追加しました{suffix}。");
                SetTransientStatusToast(
                    $"{Path.GetFileName(source.SourceIdentity)}: {source.Label}から動画化をJobsキューへ追加しました。");
            }
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
            QueueEnhancedStateRefreshIfChanged();
            return true;
        }
        finally
        {
            _videoGenerationRequestPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    private static Dictionary<string, object?> BuildVideoGenerationRequestBody(
        VideoSourceChoice source,
        VideoGenerationRequestSettings settings,
        bool h3Selected,
        int? seed)
    {
        object video = h3Selected
            ? new
            {
                requested = new
                {
                    profileId = settings.ProfileId,
                    prompt = settings.Prompt,
                },
            }
            : new
            {
                requested = new
                {
                    durationSeconds = settings.DurationSeconds,
                    playbackFps = settings.PlaybackFps,
                    maximumPixelArea = settings.MaximumPixelArea,
                    prompt = settings.Prompt,
                },
            };
        var requestBody = new Dictionary<string, object?>
        {
            ["sourceId"] = source.SourceIdentity,
            ["operation"] = "video",
            ["mediaKind"] = "video",
            ["presetId"] = settings.PresetId,
            ["adapterId"] = settings.BackendId,
            ["video"] = video,
        };
        if (!string.IsNullOrWhiteSpace(source.ProducerJobId))
            requestBody["sourceProducerJobId"] = source.ProducerJobId;
        if (!h3Selected && seed is int fixedSeed)
            requestBody["seed"] = fixedSeed;
        return requestBody;
    }

    public bool OpenVideoGenerationBoardForSmoke(
        string? requestedSource = "original")
    {
        OpenVideoGenerationBoard(requestedSource);
        return ModalVideoGenerationPopup.Visibility == Visibility.Visible;
    }

    public void CloseVideoGenerationBoardForSmoke()
        => CloseModalVideoGenerationBoard();

    public string? VideoSourceIdentityForSmoke
        => _videoSourceChoice?.SourceIdentity;

    public bool OpenDisplayedModalVideoGenerationBoardForSmoke()
    {
        OpenModalVideoGeneration_Click(this, new RoutedEventArgs());
        return ModalVideoGenerationPopup.Visibility == Visibility.Visible;
    }

    public (int DurationSeconds, int PlaybackFps, int MaximumPixelArea, string Prompt)
        VideoGenerationSettingsForSmoke
        => (
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt);

    public (bool Fixed, string Value, bool Valid) VideoSeedForSmoke
        => (
            _videoSeedFixed,
            _videoSeedValueText,
            !_videoSeedFixed || TryParseFixedSeed(_videoSeedValueText, out _));

    public bool VideoSeedSurfaceForSmoke
        => ModalVideoSeedModeComboBox is not null
            && ModalVideoSeedValueTextBox is not null
            && AppVideoSeedModeComboBox is not null
            && AppVideoSeedValueTextBox is not null
            && ModalVideoSeedValueTextBox.MaxLength == 10
            && AppVideoSeedValueTextBox.MaxLength == 10
            && AutomationProperties.GetName(ModalVideoSeedModeComboBox)
                == "Video generation seed mode"
            && AutomationProperties.GetName(AppVideoSeedModeComboBox)
                == "Default video generation seed mode";

    public (
        int FrameCount,
        int EstimatedMinimumSeconds,
        int EstimatedMaximumSeconds)
        VideoGenerationEstimateForSmoke
        => EstimateVideoGeneration(
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            VideoQualitySteps(_videoQualityId));

    public void ConfigureVideoGenerationForSmoke(
        int durationSeconds,
        int playbackFps,
        int maximumPixelArea,
        string prompt,
        string? qualityId = null)
    {
        RestoreVideoGenerationSettings(
            durationSeconds,
            playbackFps,
            maximumPixelArea,
            prompt,
            _videoModelId,
            qualityId ?? _videoQualityId);
        MarkVideoStyleAsCustom();
        VideoH3PromptRewriteContextChanged();
    }

    public void ConfigureVideoSeedForSmoke(bool fixedMode, string value)
    {
        _videoSeedFixed = fixedMode;
        _videoSeedValueText = value;
        SyncVideoSeedControls();
        UpdateVideoGenerationActionControls();
        if (!_initializing)
            SaveState();
    }

    public void SelectVideoModelForSmoke(string modelId)
    {
        _videoModelId = modelId;
        VideoH3PromptRewriteContextChanged();
        SyncVideoGenerationSettingsControls();
    }

    public void RestorePersistedVideoModelForSmoke(string modelId)
        => RestoreVideoGenerationSettings(
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt,
            modelId,
            _videoQualityId);

    public string VideoModelIdForSmoke => _videoModelId;
    public bool VideoModelRunnableForSmoke =>
        IsVideoModelRunnable(_videoModelId);

    public void SetMiniMaxH3CapabilityForSmoke(
        bool checkedHealth,
        bool ready,
        string? reasonCode)
    {
        _miniMaxH3HealthChecked = checkedHealth;
        _miniMaxH3Ready = checkedHealth && ready;
        _miniMaxH3ReasonCode = checkedHealth ? reasonCode : null;
        SyncVideoGenerationSettingsControls();
    }

    public async Task<string> RefreshMiniMaxH3VideoCapabilityForSmokeAsync()
    {
        await RefreshMiniMaxH3VideoCapabilityAsync();
        return MiniMaxH3ReservationReadinessStatus();
    }

    public string BuildMiniMaxH3EnqueueRequestJsonForSmoke(
        string prompt,
        int durationSeconds = MiniMaxH3VideoDefaultNominalDurationSeconds)
    {
        string boundedPrompt = prompt.Length <= MaxVideoPromptLength
            ? prompt
            : prompt[..MaxVideoPromptLength];
        var source = new VideoSourceChoice(
            "C:/synthetic/source.png",
            "C:/synthetic/source.png",
            null,
            "Original");
        var settings = new VideoGenerationRequestSettings(
            MiniMaxH3VideoPresetId,
            MiniMaxH3VideoBackendId,
            MiniMaxH3ProfileIdForDuration(durationSeconds),
            NormalizeMiniMaxH3DurationSeconds(durationSeconds),
            DefaultVideoPlaybackFps,
            DefaultVideoMaximumPixelArea,
            boundedPrompt.Trim());
        return JsonSerializer.Serialize(
            BuildVideoGenerationRequestBody(
                source,
                settings,
                h3Selected: true,
                seed: null));
    }

    public bool MiniMaxH3SurfaceForSmoke
        => MiniMaxH3SurfaceIssuesForSmoke.Count == 0;

    public IReadOnlyList<string> MiniMaxH3SurfaceIssuesForSmoke
    {
        get
        {
            var issues = new List<string>();
            if (!IsMiniMaxH3VideoModel(_videoModelId))
                issues.Add("model");
            if (!ModalVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .Any(static item => string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal)))
            {
                issues.Add("modal-model-option");
            }
            if (ModalVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .Any(static item => !string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal)))
            {
                issues.Add("modal-legacy-model-option");
            }
            if (!AppVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .Any(static item => string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal)))
            {
                issues.Add("app-model-option");
            }
            if (AppVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .Any(static item => !string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal)))
            {
                issues.Add("app-legacy-model-option");
            }
            if (ModalVideoWanControlsPanel.Visibility != Visibility.Collapsed)
                issues.Add("modal-wan-quality");
            if (ModalVideoWanTuningPanel.Visibility != Visibility.Collapsed)
                issues.Add("modal-wan-tuning");
            if (AppVideoWanControlsPanel.Visibility != Visibility.Collapsed)
                issues.Add("app-wan-controls");
            int[] expectedDurations = [5, 10, 12, 15];
            if (ModalVideoH3ControlsPanel.Visibility != Visibility.Visible
                || !ModalVideoH3DurationComboBox.Items
                    .OfType<ComboBoxItem>()
                    .Select(static item => int.TryParse(
                        item.Tag?.ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value)
                            ? value
                            : -1)
                    .SequenceEqual(expectedDurations))
            {
                issues.Add("modal-h3-duration-profiles");
            }
            if (AppVideoH3ControlsPanel.Visibility != Visibility.Visible
                || !AppVideoH3DurationComboBox.Items
                    .OfType<ComboBoxItem>()
                    .Select(static item => int.TryParse(
                        item.Tag?.ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value)
                            ? value
                            : -1)
                    .SequenceEqual(expectedDurations))
            {
                issues.Add("app-h3-duration-profiles");
            }
            if (ModalVideoPromptTextBox.Visibility != Visibility.Visible)
                issues.Add("modal-prompt");
            if (AppVideoPromptTextBox.Visibility != Visibility.Visible)
                issues.Add("app-prompt");
            if (!ModalVideoDeliveryText.Text.Contains(
                    "元画像比率出力: 32px単位・最大414,720px・124f・24fps・5.167秒",
                    StringComparison.Ordinal))
            {
                issues.Add("canvas-policy");
            }
            if (!ModalVideoDeliveryText.Text.Contains(
                    "AAC音声あり",
                    StringComparison.Ordinal))
            {
                issues.Add("audio");
            }
            if (!ModalVideoGenerationEstimateText.Text.Contains(
                    "約3分50秒",
                    StringComparison.Ordinal))
            {
                issues.Add("portrait-estimate");
            }
            if (!ModalVideoGenerationEstimateText.Text.Contains(
                    "約9分7秒",
                    StringComparison.Ordinal))
            {
                issues.Add("estimate");
            }
            if (!ModalVideoModelDescriptionText.Text.Contains(
                    "RTX 4070 SUPER 12GB",
                    StringComparison.Ordinal))
            {
                issues.Add("canary-description");
            }
            return issues;
        }
    }

    public bool WanVideoControlsVisibleForSmoke
        => string.Equals(_videoModelId, WanVideoModelId, StringComparison.Ordinal)
            && ModalVideoWanControlsPanel.Visibility == Visibility.Visible
            && ModalVideoWanTuningPanel.Visibility == Visibility.Visible
            && AppVideoWanControlsPanel.Visibility == Visibility.Visible;

    public bool LegacyVideoModelOptionsRetiredForSmoke
        => ModalVideoModelComboBox.Items.Count == 1
            && AppVideoModelComboBox.Items.Count == 1
            && ModalVideoModelComboBox.Visibility == Visibility.Collapsed
            && AppVideoModelComboBox.Visibility == Visibility.Collapsed
            && ModalVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .All(static item => string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal))
            && AppVideoModelComboBox.Items
                .OfType<ComboBoxItem>()
                .All(static item => string.Equals(
                    item.Tag?.ToString(),
                    MiniMaxH3VideoModelId,
                    StringComparison.Ordinal));

    public string MiniMaxH3ReadinessTextForSmoke
        => MiniMaxH3ReadinessSuffix();

    public string MiniMaxH3ReservationReadinessStatusForSmoke
        => MiniMaxH3ReservationReadinessStatus();

    public void SelectVideoQualityForSmoke(string presetId)
    {
        _videoQualityId = IsVideoQualitySupported(presetId)
            ? presetId
            : DefaultVideoPresetId;
        SyncVideoGenerationSettingsControls();
    }

    public string VideoQualityIdForSmoke => _videoQualityId;
    public int VideoQualityStepsForSmoke =>
        VideoQualitySteps(_videoQualityId);

    public bool VideoPromptTemplateSurfaceForSmoke
        => ModalVideoPromptTemplateComboBox is not null
            && AppVideoPromptTemplateComboBox is not null
            && AutomationProperties.GetName(ModalVideoPromptTemplateComboBox)
                == "Video prompt template"
            && AutomationProperties.GetName(AppVideoPromptTemplateComboBox)
                == "Video prompt template"
            && ModalVideoPromptTemplateComboBox.Items.Count
                == VideoPromptTemplates.Count
            && AppVideoPromptTemplateComboBox.Items.Count
                == VideoPromptTemplates.Count;

    public IReadOnlyList<string> VideoPromptTemplateIdsForSmoke
        => VideoPromptTemplates.Select(static template => template.Id).ToList();

    public string SelectedVideoPromptTemplateIdForSmoke
        => _selectedVideoPromptTemplateId;

    public string VideoPromptForSmoke => _videoPrompt;

    public bool SelectVideoPromptTemplateForSmoke(string templateId)
    {
        VideoPromptTemplateChoice? choice = ModalVideoPromptTemplateComboBox.Items
            .OfType<VideoPromptTemplateChoice>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                templateId,
                StringComparison.Ordinal));
        if (choice is null)
            return false;

        ModalVideoPromptTemplateComboBox.SelectedItem = choice;
        return string.Equals(
            _selectedVideoPromptTemplateId,
            templateId,
            StringComparison.Ordinal);
    }

    public bool VideoStyleSurfaceForSmoke
        => ModalVideoStyleComboBox is not null
            && AppVideoStyleListBox is not null
            && ModalVideoStyleNameTextBox.MaxLength == MaxVideoStyleNameLength
            && AppVideoStyleNameTextBox.MaxLength == MaxVideoStyleNameLength
            && AutomationProperties.GetName(ModalVideoStyleComboBox)
                == "Video generation style"
            && AutomationProperties.GetName(AppVideoStyleListBox)
                == "Saved video generation styles";

    public IReadOnlyList<string> VideoStyleNamesForSmoke
        => _videoStyles.Select(static style => style.Name).ToList();

    public string? SelectedVideoStyleNameForSmoke
        => _selectedVideoStyleName;

    public bool SaveVideoStyleForSmoke(string name)
    {
        AppVideoStyleNameTextBox.Text = name;
        SaveVideoStyle_Click(SaveAppVideoStyleButton, new RoutedEventArgs());
        return FindVideoStyle(name) is not null;
    }

    public bool SelectVideoStyleForSmoke(string name)
    {
        VideoStyleChoice? choice = ModalVideoStyleComboBox.Items
            .OfType<VideoStyleChoice>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.StyleName, name, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return false;

        ModalVideoStyleComboBox.SelectedItem = choice;
        return string.Equals(_selectedVideoStyleName, name, StringComparison.OrdinalIgnoreCase);
    }

    public bool DeleteSelectedVideoStyleForSmoke()
    {
        string? selectedName = _selectedVideoStyleName;
        DeleteVideoStyle_Click(DeleteAppVideoStyleButton, new RoutedEventArgs());
        return selectedName is not null && FindVideoStyle(selectedName) is null;
    }

    public (string Label, string? ProducerJobId)? VideoSourceForSmoke
        => _videoSourceChoice is null
            ? null
            : (_videoSourceChoice.Label, _videoSourceChoice.ProducerJobId);

    public bool OverrideVideoSourceLabelForSmoke(string label)
    {
        if (_videoSourceChoice is null || string.IsNullOrWhiteSpace(label))
            return false;
        _videoSourceChoice = _videoSourceChoice with { Label = label };
        return true;
    }

    public string[] GalleryVideoSourceRequestsForSmoke
    {
        get
        {
            if (SelectedTile() is not Tile { IsRealFile: true } tile)
                return [];
            var menu = new MenuItem();
            PopulateGalleryVideoSourceMenu(menu, tile);
            return menu.Items
                .OfType<MenuItem>()
                .Select(static item => item.Tag?.ToString())
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .ToArray();
        }
    }

    public bool SelectedPhotorealVideoSourceGlobalJobIdRejectedForSmoke(
        string jobId)
    {
        if (SelectedTile() is not Tile { IsRealFile: true } tile
            || !_ambiguousEnhancementJobIds.Add(jobId))
        {
            return false;
        }

        try
        {
            var menu = new MenuItem();
            PopulateGalleryVideoSourceMenu(menu, tile);
            string[] requests = menu.Items
                .OfType<MenuItem>()
                .Select(static item => item.Tag?.ToString())
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .ToArray();
            return requests.SequenceEqual(["original"], StringComparer.Ordinal)
                && !TryCaptureVideoSource(
                    tile,
                    PhotorealVideoSourceRequestPrefix + jobId,
                    out _,
                    out _);
        }
        finally
        {
            _ambiguousEnhancementJobIds.Remove(jobId);
        }
    }

    public Task<bool> QueueVideoGenerationForSmokeAsync()
        => QueueVideoGenerationAsync();

    public bool VideoGenerationQueueEnabledForSmoke
        => QueueVideoGenerationButton.IsEnabled;

    public bool ModalVideoGenerationBoardVisibleForSmoke
        => ModalVideoGenerationPopup.Visibility == Visibility.Visible;

    public string VideoGenerationStatusForSmoke
        => VideoGenerationStatusText.Text;

    public bool VideoGenerationSurfaceForSmoke
        => ModalVideoGenerateButton is not null
            && ModalVideoGenerationPopup is not null
            && ModalVideoGenerationPopup is Grid
            && VideoStyleSurfaceForSmoke
            && ModalVideoGenerationBoardBorder.MaxHeight <= 680
            && ModalVideoGenerationScrollViewer.VerticalScrollBarVisibility
                == ScrollBarVisibility.Auto
            && MiniMaxH3SurfaceForSmoke
            && LegacyVideoModelOptionsRetiredForSmoke
            && ModalVideoWanControlsPanel.Visibility == Visibility.Collapsed
            && ModalVideoWanTuningPanel.Visibility == Visibility.Collapsed
            && AppVideoWanControlsPanel.Visibility == Visibility.Collapsed
            && !ModalVideoQualityComboBox.IsEnabled
            && !AppVideoQualityComboBox.IsEnabled
            && !ModalVideoDurationComboBox.IsEnabled
            && !ModalVideoFpsComboBox.IsEnabled
            && !ModalVideoResolutionComboBox.IsEnabled
            && !AppVideoDurationComboBox.IsEnabled
            && !AppVideoFpsComboBox.IsEnabled
            && !AppVideoResolutionComboBox.IsEnabled
            && ModalVideoH3DurationComboBox.IsEnabled
            && AppVideoH3DurationComboBox.IsEnabled
            && !ModalVideoSeedModeComboBox.IsEnabled
            && !AppVideoSeedModeComboBox.IsEnabled
            && ModalVideoModelDescriptionText.Text.Contains(
                "RTX 4070 SUPER 12GB",
                StringComparison.Ordinal)
            && ModalVideoPromptTextBox.MaxLength == MaxVideoPromptLength
            && string.Equals(
                AutomationProperties.GetName(QueueVideoGenerationButton),
                "Add video generation job",
                StringComparison.Ordinal)
            && ModalVideoPresetText.Text.Contains(
                "MiniMax H3",
                StringComparison.Ordinal)
            && string.Equals(
                AppVideoDeliveryText.Text,
                ModalVideoDeliveryText.Text,
                StringComparison.Ordinal)
            && AppVideoDeliveryText.Text.Contains(
                "124f・24fps・5.167秒",
                StringComparison.Ordinal)
            && ModalVideoDeliveryText.Text.Contains(
                "AAC音声あり",
                StringComparison.Ordinal)
            && AppVideoResolutionHintText.Text.Contains(
                "最大414,720px",
                StringComparison.Ordinal)
            && ModalVideoResolutionHintText.Text.Contains(
                "H3 preview",
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText is not null
            && AppVideoGenerationEstimateText is not null
            && string.Equals(
                ModalVideoGenerationEstimateText.Text,
                AppVideoGenerationEstimateText.Text,
                StringComparison.Ordinal)
            && string.Equals(
                ModalVideoGenerationEstimateText.Text,
                VideoGenerationEstimateText(),
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText.Text.Contains(
                "約3分50秒〜約9分7秒",
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText.Text.Contains(
                "RTX 4070 SUPER 12GB",
                StringComparison.Ordinal)
            && AppVideoSettingsHeading is not null
            && SettingsVideoNav is not null;
}

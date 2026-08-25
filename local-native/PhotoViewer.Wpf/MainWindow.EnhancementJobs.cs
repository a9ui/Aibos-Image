using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementJobsThumbnailViewportLimit = 12;
    private const int EnhancementJobsThumbnailCacheLimit = 96;
    private const int EnhancementJobsPageSize = 100;
    private const int EnhancementJobsDefaultHistoryLimit = 500;
    private const int EnhancementQueuedJobsBatchLimit = 1_000;
    private const int EnhancementQueuedJobsBatchReorderLimit = 10_000;
    private const int EnhancementQueuedJobsBatchReorderMaximumBodyBytes =
        512 * 1024;
    private const int EnhancementTerminalHistoryBatchLimit = 1_000;
    private const int EnhancementJobRequestDetailsMaximumLength = 32_768;
    private const int EnhancementJobsWorkspaceMaximumRows = 100_000;
    private const int EnhancementJobsWorkspaceMaximumPayloadBytesPerRow =
        1024 * 1024;
    private const long EnhancementJobsWorkspaceMaximumTotalPayloadBytes =
        512L * 1024 * 1024;
    private const int EnhancementJobsSqliteMaximumValueBytes =
        EnhancementJobsWorkspaceMaximumPayloadBytesPerRow + 64 * 1024;
    private const int EnhancementJobsSqliteMaximumSqlBytes = 64 * 1024;
    private static readonly TimeSpan EnhancementJobsThumbnailViewportDebounce =
        TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan EnhancementJobsQueueReorderDebounce =
        TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan[] EnhancementJobsThumbnailRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];
    private const string UnsupportedEnhancementOperation = "unsupported";
    private const string VideoPreservationPreamble =
        "Animate the supplied image as the exact first frame. "
        + "Preserve the same character identity, face, hairstyle, body proportions, outfit, colors, line art, rendering style, composition, background, lighting, and aspect ratio. "
        + "Keep temporal motion coherent and physically plausible with stable anatomy and clean frame-to-frame consistency.";
    private const string VideoBlankPromptMotion =
        "Use subtle natural idle motion only: gentle breathing, an occasional blink, and restrained secondary motion in hair and clothing. "
        + "Keep the camera locked and preserve the original framing.";
    private const string VideoNegativePrompt =
        "low quality, worst quality, blurry, flicker, jitter, frame interpolation artifacts, identity drift, face distortion, deformed hands, extra limbs, missing limbs, warped anatomy, melting, morphing, duplicate character, camera shake, text, logo, watermark";
    private const string MiniMaxH3DefaultPositivePrompt =
        "Animate the supplied image with subtle natural motion. Keep the camera stable and preserve the exact subject, composition, lighting, and scene. Do not add new objects or cut to another scene. Use only quiet ambient sound, with no speech and no music.";
    private readonly record struct EnhancementWorkspaceStatusCounts(
        int Queued,
        int Running,
        int Succeeded,
        int Failed,
        int Canceled,
        int Deleted)
    {
        public int Total => checked(
            Queued + Running + Succeeded + Failed + Canceled + Deleted);
        public int Active => checked(Queued + Running);
        public int Completed => checked(Succeeded + Deleted);
    }
    private sealed record EnhancementWorkspaceSqliteSnapshot(
        List<EnhancementWorkspaceJobView> Jobs,
        EnhancementWorkspaceStatusCounts Counts);
    private sealed record EnhancementWorkspaceJobDetailIdentity(
        string Id,
        string Status,
        string Operation,
        string SourceId,
        string SourcePath,
        string PresetId,
        string AdapterId,
        int ApiOrdinal);
    private static readonly JsonSerializerOptions VideoStableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly List<EnhancementWorkspaceJobView> _enhancementWorkspaceJobs = [];
    private readonly Dictionary<string, BitmapSource> _enhancementWorkspaceThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enhancementWorkspaceThumbnailFailedJobIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enhancementWorkspaceHighlightedJobIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enhancementWorkspaceOptimisticallyHiddenJobIds = new(StringComparer.Ordinal);
    private DispatcherTimer _enhancementWorkspacePollTimer = null!;
    private DispatcherTimer _enhancementWorkspaceThumbnailViewportTimer = null!;
    private DispatcherTimer _enhancementWorkspaceThumbnailRetryTimer = null!;
    private CancellationTokenSource? _enhancementWorkspaceThumbnailCts;
    private CancellationTokenSource? _enhancementWorkspaceInventoryCts;
    private readonly SemaphoreSlim _enhancementWorkspaceSqliteReadGate =
        new(1, 1);
    private bool _enhancementWorkspaceThumbnailViewportLoadPending;
    private bool _suppressEnhancementWorkspaceThumbnailLoadsForSmoke;
    private int _enhancementWorkspaceLastThumbnailBatchSize;
    private int _enhancementWorkspaceThumbnailScrollCancellationCount;
    private int _enhancementWorkspaceScrollChangedCount;
    private int _enhancementWorkspaceThumbnailTimerRestartCount;
    private int _enhancementWorkspaceThumbnailRetryAttempt;
    private long _enhancementWorkspaceLastThumbnailScrollTimestamp;
    private bool _enhancementWorkspaceRefreshPending;
    private bool _enhancementWorkspaceHealthPollPending;
    private long _enhancementWorkspaceRefreshGeneration;
    private long _enhancementWorkspaceQueuePresentationRevision;
    private Task? _enhancementWorkspaceQueueOrderFlushTask;
    private string[]? _enhancementWorkspacePendingQueueOrder;
    private string[]? _enhancementWorkspaceConfirmedQueueOrder;
    private bool _enhancementWorkspaceMutationPending;
    private long _enhancementWorkspaceGeneration;
    private string _enhancementWorkspaceStatusFilter = "all";
    private string _enhancementWorkspaceOperationFilter = "all";
    private string _enhancementWorkspaceVideoKindFilter = "all";
    private int _enhancementJobsHistoryLimit = EnhancementJobsDefaultHistoryLimit;
    private bool _settingEnhancementJobsHistoryLimitSelection;
    private EnhancementWorkspaceStatusCounts? _enhancementWorkspaceTotalCounts;
    private int _enhancementWorkspacePageIndex;
    private int _enhancementWorkspaceFilteredCount;
    private DateTimeOffset _enhancementWorkspaceHighlightExpiresAt;
    private IInputElement? _enhancementWorkspaceFocusBeforeDialog;
    private int _enhancementWorkspaceGetCount;
    private int _enhancementWorkspacePollCount;
    private int _enhancementWorkspaceHealthGetCount;
    private bool? _enhancementWorkspaceHealthEndpointSupported;
    private string? _enhancementWorkspaceHealthInventorySignature;
    private bool? _enhancementWorkspaceHealthInventoryRevisionSupported;
    private long? _enhancementWorkspaceLastHealthInventoryRevision;
    private long _enhancementWorkspaceMutationDebtEpoch;
    private long _enhancementWorkspaceReconciledMutationDebtEpoch;
    private long? _enhancementWorkspaceMutationDebtMinimumInventoryRevision;
    private bool? _enhancementWorkspaceQueuePaused;
    private bool _enhancementWorkspaceQueueRecoveryRequired;
    private bool _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported;
    private bool _enhancementWorkspacePhotorealEnqueueNextSupported;
    private bool _enhancementWorkspaceTerminalHistoryBatchDismissSupported;
    private bool _enhancementWorkspaceQueuedJobsBatchCancelSupported;
    private bool _enhancementWorkspaceQueuedJobsBatchReorderSupported;
    private bool _enhancementWorkspaceTerminalHistoryTargetsSupported;
    private bool _enhancementWorkspaceTerminalHistoryBatchRetrySupported;
    private Func<string, string, bool>? _confirmEnhancementJobsBulkActionForSmoke;
    private bool _returnToEnhancementJobsAfterModalClose;
    private Tile? _enhancementJobsTemporaryVisibleTile;
    private string? _enhancementJobsTrustedModalSourcePath;
    private long? _enhancementJobsTrustedModalSourceSizeBytes;
    private string? _enhancementJobsTrustedModalOutputPath;
    private long? _enhancementJobsTrustedModalOutputSizeBytes;
    private readonly List<string> _enhancementJobsPreviousSelectionPaths = [];
    private string? _enhancementJobsPreviousPrimaryPath;
    private bool _enhancementJobsModalSelectionCaptured;
    private double _enhancementJobsReturnVerticalOffset;
    private string? _enhancementJobsReturnJobId;
    private double _enhancementJobsReturnAnchorViewportTop = double.NaN;
    private bool _enhancementJobsReturnViewportPending;

    private static string ReadEnhancementOperation(JsonElement job)
    {
        if (!job.TryGetProperty("operation", out JsonElement operation))
            return "upscale";
        if (operation.ValueKind != JsonValueKind.String)
            return UnsupportedEnhancementOperation;

        return operation.GetString() switch
        {
            "upscale" => "upscale",
            "photoreal" => "photoreal",
            "i2i" when IsI2iMutationSafe(job)
                || IsI2iV2MutationSafe(job)
                || IsI2iV3MutationSafe(job) => "i2i",
            "video" => "video",
            _ => UnsupportedEnhancementOperation,
        };
    }

    private static bool IsStructurallyVideoMutationSafe(JsonElement job)
        => IsWanV1VideoMutationSafe(job)
            || IsMiniMaxH3VideoMutationSafe(job);

    private bool IsVideoMutationSafe(JsonElement job)
        => IsWanV1VideoMutationSafe(job)
            || (IsMiniMaxH3VideoMutationSafe(job)
                && IsMiniMaxH3SourceCanvasCurrent(job));

    private static bool IsWanV1VideoMutationSafe(JsonElement job)
    {
        if (!TryReadOptionalVideoSourceProducerJobId(job, out _))
            return false;

        if ((job.TryGetProperty(
                    "cancelRequested",
                    out JsonElement cancelRequestedElement)
                && cancelRequestedElement.ValueKind is not (
                    JsonValueKind.True
                    or JsonValueKind.False
                    or JsonValueKind.Null))
            || !TryGetExactStringProperty(job, "mediaKind", "video")
            || !TryGetStringProperty(
                job,
                "presetId",
                out string? jobPresetId)
            || !TryGetVideoPresetSteps(
                jobPresetId,
                out int expectedSteps)
            || !TryGetExactStringProperty(
                job,
                "adapterId",
                "wan22-ti2v-5b-core-v1")
            || !TryGetStringProperty(job, "sourceSha256", out string? sourceSha256)
            || !IsLowerHex(sourceSha256, 64)
            || !TryGetStringProperty(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !HasExactVideoSnapshotProperties(video)
            || !TryGetStringProperty(
                video,
                "presetId",
                out string? videoPresetId)
            || !string.Equals(
                videoPresetId,
                jobPresetId,
                StringComparison.Ordinal)
            || !TryGetExactStringProperty(
                video,
                "backendId",
                "wan22-ti2v-5b-core-v1")
            || !TryGetExactStringProperty(
                video,
                "modelName",
                "wan2.2_ti2v_5B_fp16.safetensors")
            || !TryGetExactStringProperty(video, "codec", "h264")
            || !TryGetExactStringProperty(video, "container", "mp4")
            || !video.TryGetProperty("bitDepth", out JsonElement bitDepthElement)
            || !bitDepthElement.TryGetInt32(out int bitDepth)
            || bitDepth != 8
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || seed < 0
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                requested,
                "durationSeconds",
                "playbackFps",
                "maximumPixelArea",
                "prompt")
            || !video.TryGetProperty("effective", out JsonElement effective)
            || effective.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                effective,
                "frameCount",
                "width",
                "height",
                "positivePrompt",
                "negativePrompt",
                "steps",
                "cfg",
                "sampler",
                "scheduler",
                "shift",
                "denoise")
            || !requested.TryGetProperty(
                "durationSeconds",
                out JsonElement durationElement)
            || !durationElement.TryGetInt32(out int durationSeconds)
            || durationSeconds is not (4 or 6)
            || !requested.TryGetProperty(
                "playbackFps",
                out JsonElement playbackFpsElement)
            || !playbackFpsElement.TryGetInt32(out int playbackFps)
            || playbackFps is not (12 or 16)
            || !requested.TryGetProperty(
                "maximumPixelArea",
                out JsonElement maximumPixelAreaElement)
            || !maximumPixelAreaElement.TryGetInt32(out int maximumPixelArea)
            || maximumPixelArea is not (230400 or 307200 or 409600)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? prompt)
            || prompt!.Length > MaxVideoPromptLength
            || !effective.TryGetProperty(
                "frameCount",
                out JsonElement frameCountElement)
            || !frameCountElement.TryGetInt32(out int frameCount)
            || frameCount != checked(
                4 * (durationSeconds * playbackFps / 4) + 1)
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || width < 32
            || height < 32
            || width % 32 != 0
            || height % 32 != 0
            || checked((long)width * height) > maximumPixelArea
            || !TryGetStringProperty(
                effective,
                "positivePrompt",
                out string? positivePrompt)
            || !TryGetStringPropertyAllowEmpty(
                effective,
                "negativePrompt",
                out string? negativePrompt)
            || !string.Equals(
                positivePrompt,
                BuildVideoPositivePrompt(prompt),
                StringComparison.Ordinal)
            || !string.Equals(
                negativePrompt,
                VideoNegativePrompt,
                StringComparison.Ordinal)
            || !HasExactInt32(effective, "steps", expectedSteps)
            || !HasExactInt32(effective, "cfg", 5)
            || !TryGetExactStringProperty(effective, "sampler", "uni_pc")
            || !TryGetExactStringProperty(effective, "scheduler", "simple")
            || !HasExactInt32(effective, "shift", 8)
            || !HasExactInt32(effective, "denoise", 1)
            || !IsVideoDeliveryMutationSafe(
                video,
                durationSeconds))
        {
            return false;
        }

        if (!string.Equals(
                presetHash,
                HashStableJson(video)[..12],
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!job.TryGetProperty(
                "outputPath",
                out JsonElement outputPathElement)
            || outputPathElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (outputPathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(outputPathElement.GetString())
            || !TryGetStringProperty(job, "id", out string? jobId)
            || !TryGetStringProperty(
                job,
                "sourcePath",
                out string? sourcePath))
        {
            return false;
        }

        try
        {
            string expectedFileName = BuildVideoOutputFileName(
                jobId!,
                sourcePath!,
                sourceSha256!,
                jobPresetId!,
                presetHash!);
            return string.Equals(
                Path.GetFileName(outputPathElement.GetString()),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsMiniMaxH3VideoMutationSafe(JsonElement job)
    {
        if (!HasSingleProperty(job, "id")
            || !HasSingleProperty(job, "operation")
            || !HasSingleProperty(job, "mediaKind")
            || !HasSingleProperty(job, "presetId")
            || !HasSingleProperty(job, "adapterId")
            || !HasSingleProperty(job, "sourceId")
            || !HasSingleProperty(job, "sourcePath")
            || !HasSingleProperty(job, "sourceSignature")
            || !HasSingleProperty(job, "sourceSha256")
            || !HasSingleProperty(job, "presetHash")
            || !HasSingleProperty(job, "status")
            || !HasSingleProperty(job, "createdAt")
            || !HasSingleProperty(job, "updatedAt")
            || !HasSingleProperty(job, "video")
            || !TryReadOptionalVideoSourceProducerJobId(job, out _)
            || !TryGetStringProperty(job, "id", out string? jobId)
            || jobId!.Length > 128
            || !TryGetExactStringProperty(job, "operation", "video")
            || !TryGetExactStringProperty(job, "mediaKind", "video")
            || !TryGetExactStringProperty(
                job,
                "presetId",
                MiniMaxH3VideoPresetId)
            || !TryGetExactStringProperty(
                job,
                "adapterId",
                MiniMaxH3VideoBackendId)
            || !TryGetStringProperty(job, "sourceId", out _)
            || !TryGetStringProperty(job, "sourcePath", out string? sourcePath)
            || !TryGetStringProperty(job, "sourceSha256", out string? sourceSha256)
            || !IsLowerHex(sourceSha256, 64)
            || !TryGetStringProperty(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !TryGetStringProperty(job, "status", out string? status)
            || status is not ("queued" or "running" or "succeeded" or "failed" or "canceled" or "deleted")
            || !TryGetStringProperty(job, "createdAt", out string? createdAt)
            || !DateTimeOffset.TryParse(
                createdAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || !TryGetStringProperty(job, "updatedAt", out string? updatedAt)
            || !DateTimeOffset.TryParse(
                updatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || !job.TryGetProperty("sourceSignature", out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !HasExactProperties(signature, "size", "mtimeMs")
            || !signature.TryGetProperty("size", out JsonElement sizeElement)
            || !sizeElement.TryGetInt64(out long sourceSize)
            || sourceSize < 0
            || !signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement)
            || !mtimeElement.TryGetDouble(out double sourceMtimeMs)
            || !double.IsFinite(sourceMtimeMs)
            || !job.TryGetProperty("video", out JsonElement video)
            || !IsExactMiniMaxH3VideoSnapshot(video)
            || !string.Equals(
                presetHash,
                HashStableJson(video)[..12],
                StringComparison.Ordinal))
        {
            return false;
        }

        if (job.TryGetProperty(
                "cancelRequested",
                out JsonElement cancelRequestedElement))
        {
            if (!HasSingleProperty(job, "cancelRequested")
                || cancelRequestedElement.ValueKind is not (
                    JsonValueKind.True
                    or JsonValueKind.False
                    or JsonValueKind.Null))
            {
                return false;
            }
        }

        if (!job.TryGetProperty(
                "outputPath",
                out JsonElement outputPathElement)
            || outputPathElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (!HasSingleProperty(job, "outputPath")
            || outputPathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(outputPathElement.GetString()))
        {
            return false;
        }

        try
        {
            string expectedFileName = BuildVideoOutputFileName(
                jobId!,
                sourcePath!,
                sourceSha256!,
                MiniMaxH3VideoPresetId,
                presetHash!);
            return string.Equals(
                Path.GetFileName(outputPathElement.GetString()),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetMiniMaxH3SnapshotProfile(
        JsonElement requested,
        out int frameCount,
        out double durationSeconds,
        out int steps,
        out int maximumPixelArea)
    {
        frameCount = 0;
        durationSeconds = 0;
        steps = MiniMaxH3VideoSteps;
        maximumPixelArea = MiniMaxH3VideoCanvasMaximumPixelArea;
        string[] names = requested.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        string[] allowedNames =
            ["profileId", "prompt", "steps", "maximumPixelArea"];
        if (names.Length is < 1 or > 4
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || !names.Contains("prompt", StringComparer.Ordinal)
            || names.Any(name => !allowedNames.Contains(
                name,
                StringComparer.Ordinal)))
        {
            return false;
        }

        string? profileId = MiniMaxH3VideoDefaultProfileId;
        if (names.Contains("profileId", StringComparer.Ordinal)
            && !TryGetStringProperty(requested, "profileId", out profileId))
            return false;
        if (names.Contains("steps", StringComparer.Ordinal)
            && (!requested.TryGetProperty("steps", out JsonElement stepsElement)
                || !stepsElement.TryGetInt32(out steps)
                || steps < MiniMaxH3VideoMinimumSteps
                || steps > MiniMaxH3VideoMaximumSteps))
        {
            return false;
        }
        if (names.Contains("maximumPixelArea", StringComparer.Ordinal)
            && (!requested.TryGetProperty(
                    "maximumPixelArea",
                    out JsonElement maximumPixelAreaElement)
                || !maximumPixelAreaElement.TryGetInt32(out maximumPixelArea)
                || !SupportedMiniMaxH3VideoMaximumPixelAreas.Contains(
                    maximumPixelArea)))
        {
            return false;
        }

        frameCount = profileId switch
        {
            MiniMaxH3VideoDefaultProfileId => 124,
            MiniMaxH3Video10SecondProfileId => 243,
            MiniMaxH3Video12SecondProfileId => 294,
            MiniMaxH3Video15SecondProfileId => 362,
            _ => 0,
        };
        if (frameCount == 0)
            return false;
        durationSeconds = frameCount / (double)MiniMaxH3VideoPlaybackFps;
        return true;
    }

    private static bool IsExactMiniMaxH3VideoSnapshot(JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                video,
                "schemaVersion",
                "workflowRevision",
                "presetId",
                "backendId",
                "modelName",
                "model",
                "requested",
                "effective",
                "delivery",
                "seed",
                "codec",
                "container",
                "bitDepth")
            || !HasExactInt32(video, "schemaVersion", 2)
            || !TryGetExactStringProperty(
                video,
                "workflowRevision",
                MiniMaxH3VideoWorkflowRevision)
            || !TryGetExactStringProperty(
                video,
                "presetId",
                MiniMaxH3VideoPresetId)
            || !TryGetExactStringProperty(
                video,
                "backendId",
                MiniMaxH3VideoBackendId)
            || !TryGetExactStringProperty(video, "modelName", "MiniMax-H3")
            || !TryGetExactStringProperty(video, "codec", "h264")
            || !TryGetExactStringProperty(video, "container", "mp4")
            || !HasExactInt32(video, "bitDepth", 8)
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || seed < 0
            || !video.TryGetProperty("model", out JsonElement model)
            || model.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                model,
                "repository",
                "revision",
                "diffusion",
                "textEncoder",
                "videoVae",
                "audioVae")
            || !TryGetExactStringProperty(model, "repository", "Comfy-Org/MiniMax-H3")
            || !TryGetExactStringProperty(
                model,
                "revision",
                "014cd40f7e177756c6b2473c0d93b1c89a790dd2")
            || !TryGetExactStringProperty(
                model,
                "diffusion",
                "minimax_h3_fl2va_pruned_int8_convrot.safetensors")
            || !TryGetExactStringProperty(
                model,
                "textEncoder",
                "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors")
            || !TryGetExactStringProperty(
                model,
                "videoVae",
                "minimax_h3_video_vae_fp16.safetensors")
            || !TryGetExactStringProperty(
                model,
                "audioVae",
                "minimax_h3_audio_vae_fp32.safetensors")
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !TryGetMiniMaxH3SnapshotProfile(
                requested,
                out int expectedFrameCount,
                out double expectedDurationSeconds,
                out int expectedSteps,
                out int expectedMaximumPixelArea)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? requestedPrompt)
            || requestedPrompt!.Length > MaxVideoPromptLength
            || !string.Equals(
                requestedPrompt,
                requestedPrompt.Trim(),
                StringComparison.Ordinal)
            || !video.TryGetProperty("effective", out JsonElement effective)
            || effective.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                effective,
                "width",
                "height",
                "frameCount",
                "playbackFps",
                "steps",
                "sampler",
                "scheduler",
                "denoise",
                "positivePrompt")
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || !IsValidMiniMaxH3VideoCanvas(
                width,
                height,
                expectedMaximumPixelArea)
            || !HasExactInt32(
                effective,
                "frameCount",
                expectedFrameCount)
            || !HasExactInt32(
                effective,
                "playbackFps",
                MiniMaxH3VideoPlaybackFps)
            || !HasExactInt32(effective, "steps", expectedSteps)
            || !TryGetExactStringProperty(
                effective,
                "sampler",
                "res_multistep")
            || !TryGetExactStringProperty(effective, "scheduler", "simple")
            || !HasExactInt32(effective, "denoise", 1)
            || !TryGetStringProperty(
                effective,
                "positivePrompt",
                out string? positivePrompt)
            || !string.Equals(
                positivePrompt,
                requestedPrompt.Length == 0
                    ? MiniMaxH3DefaultPositivePrompt
                    : requestedPrompt,
                StringComparison.Ordinal)
            || !video.TryGetProperty("delivery", out JsonElement delivery)
            || delivery.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                delivery,
                "frameCount",
                "targetFps",
                "durationSeconds",
                "pixelFormat",
                "videoCodec",
                "audioCodec",
                "audio")
            || !HasExactInt32(
                delivery,
                "frameCount",
                expectedFrameCount)
            || !HasExactInt32(
                delivery,
                "targetFps",
                MiniMaxH3VideoPlaybackFps)
            || !delivery.TryGetProperty(
                "durationSeconds",
                out JsonElement durationElement)
            || !durationElement.TryGetDouble(out double durationSeconds)
            || !double.IsFinite(durationSeconds)
            || durationSeconds != expectedDurationSeconds
            || !TryGetExactStringProperty(delivery, "pixelFormat", "yuv420p")
            || !TryGetExactStringProperty(delivery, "videoCodec", "h264")
            || !TryGetExactStringProperty(delivery, "audioCodec", "aac")
            || !delivery.TryGetProperty("audio", out JsonElement audioElement)
            || audioElement.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetVideoPresetSteps(
        string? presetId,
        out int steps)
    {
        switch (presetId)
        {
            case NormalVideoPresetId:
                steps = NormalVideoSteps;
                return true;
            case HighVideoPresetId:
                steps = HighVideoSteps;
                return true;
            default:
                steps = 0;
                return false;
        }
    }

    private static bool TryReadOptionalVideoSourceProducerJobId(
        JsonElement job,
        out string? sourceProducerJobId)
    {
        sourceProducerJobId = null;
        JsonElement sourceProducerElement = default;
        int propertyCount = 0;
        foreach (JsonProperty property in job.EnumerateObject())
        {
            if (!property.NameEquals("sourceProducerJobId"))
                continue;
            propertyCount++;
            if (propertyCount > 1)
                return false;
            sourceProducerElement = property.Value;
        }

        if (propertyCount == 0)
            return true;
        if (sourceProducerElement.ValueKind != JsonValueKind.String)
            return false;

        string? value = sourceProducerElement.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        sourceProducerJobId = value;
        return true;
    }

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expectedNames)
    {
        string[] actualNames = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return actualNames.Length == expectedNames.Length
            && actualNames
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames);
    }

    private static bool HasSingleProperty(
        JsonElement element,
        string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Count(property =>
                property.NameEquals(propertyName)) == 1;

    private static bool HasExactVideoSnapshotProperties(
        JsonElement video)
        => video.TryGetProperty("delivery", out _)
            ? HasExactProperties(
                video,
                "presetId",
                "backendId",
                "modelName",
                "requested",
                "effective",
                "delivery",
                "seed",
                "codec",
                "container",
                "bitDepth")
            : HasExactProperties(
                video,
                "presetId",
                "backendId",
                "modelName",
                "requested",
                "effective",
                "seed",
                "codec",
                "container",
                "bitDepth");

    private static bool IsVideoDeliveryMutationSafe(
        JsonElement video,
        int durationSeconds)
    {
        if (!video.TryGetProperty("delivery", out JsonElement delivery))
            return true;

        return delivery.ValueKind == JsonValueKind.Object
            && HasExactProperties(
                delivery,
                "backendId",
                "model",
                "targetFps",
                "frameCount",
                "durationSeconds",
                "pixelFormat",
                "audio")
            && TryGetExactStringProperty(
                delivery,
                "backendId",
                "vs-rife-5.7.0-rife-4.25-v1")
            && TryGetExactStringProperty(delivery, "model", "4.25")
            && HasExactInt32(delivery, "targetFps", 30)
            && HasExactInt32(
                delivery,
                "frameCount",
                checked(durationSeconds * 30))
            && HasExactInt32(
                delivery,
                "durationSeconds",
                durationSeconds)
            && TryGetExactStringProperty(
                delivery,
                "pixelFormat",
                "yuv420p")
            && delivery.TryGetProperty(
                "audio",
                out JsonElement audioElement)
            && audioElement.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadMiniMaxH3VideoWorkspaceSnapshot(
        JsonElement job,
        out MiniMaxH3VideoWorkspaceSnapshot? snapshot)
    {
        snapshot = null;
        if (!job.TryGetProperty("video", out JsonElement video)
            || !IsExactMiniMaxH3VideoSnapshot(video)
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !TryGetMiniMaxH3SnapshotProfile(
                requested,
                out _,
                out _,
                out int steps,
                out int maximumPixelArea)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? prompt))
        {
            return false;
        }

        string profileId = MiniMaxH3VideoDefaultProfileId;
        if (requested.TryGetProperty("profileId", out _))
        {
            if (!TryGetStringProperty(
                    requested,
                    "profileId",
                    out string? parsedProfileId)
                || parsedProfileId is null)
            {
                return false;
            }
            profileId = parsedProfileId;
        }
        int nominalDurationSeconds = profileId switch
        {
            MiniMaxH3VideoDefaultProfileId => 5,
            MiniMaxH3Video10SecondProfileId => 10,
            MiniMaxH3Video12SecondProfileId => 12,
            MiniMaxH3Video15SecondProfileId => 15,
            _ => 0,
        };
        if (nominalDurationSeconds == 0)
            return false;

        snapshot = new MiniMaxH3VideoWorkspaceSnapshot(
            profileId,
            nominalDurationSeconds,
            maximumPixelArea,
            steps,
            prompt!);
        return true;
    }

    private static string BuildVideoOutputFileName(
        string jobId,
        string sourcePath,
        string sourceSha256,
        string presetId,
        string presetHash)
    {
        string safeJobId = SanitizeVideoOutputNamePart(
            jobId,
            maximumLength: 48,
            fallback: "");
        if (safeJobId.Length == 0)
            throw new ArgumentException("Video job id is empty.", nameof(jobId));
        string safeSourceName = SanitizeVideoOutputNamePart(
            Path.GetFileNameWithoutExtension(sourcePath),
            maximumLength: 64,
            fallback: "image");
        return $"{safeJobId}__{safeSourceName}__{sourceSha256[..16]}__{presetId}__{presetHash}.mp4";
    }

    private static string SanitizeVideoOutputNamePart(
        string value,
        int maximumLength,
        string fallback)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            bool invalid = character < ' '
                || character is '<' or '>' or ':' or '"' or '/'
                    or '\\' or '|' or '?' or '*';
            builder.Append(invalid ? '_' : character);
        }
        string sanitized = builder.ToString();
        if (sanitized.Length > maximumLength)
            sanitized = sanitized[..maximumLength];
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static string BuildVideoPositivePrompt(string prompt)
    {
        string requestedPrompt = prompt.Trim();
        return requestedPrompt.Length == 0
            ? $"{VideoPreservationPreamble} {VideoBlankPromptMotion}"
            : $"{VideoPreservationPreamble} Follow this motion direction: {requestedPrompt}";
    }

    private static string BuildEnhancementJobRequestDetails(
        JsonElement job,
        string operation,
        string id,
        string sourcePath,
        string presetId,
        string adapterId,
        I2iV3WorkspaceSnapshot? i2iV3Snapshot)
    {
        var builder = new StringBuilder();
        AppendEnhancementJobDetailLine(builder, "処理", operation switch
        {
            "upscale" => "高画質化",
            "photoreal" => "実写化",
            "i2i" => "AI編集",
            "video" => "動画化",
            _ => "未対応",
        });
        AppendEnhancementJobDetailLine(builder, "Job ID", id);
        AppendEnhancementJobDetailLine(
            builder,
            "元画像",
            string.IsNullOrWhiteSpace(sourcePath)
                ? "不明"
                : Path.GetFileName(sourcePath));
        AppendEnhancementJobDetailLine(builder, "Preset", presetId);
        AppendEnhancementJobDetailLine(builder, "Adapter", adapterId);

        bool promptWritten = false;
        if (operation == "video"
            && job.TryGetProperty("video", out JsonElement video)
            && video.ValueKind == JsonValueKind.Object)
        {
            if (video.TryGetProperty("requested", out JsonElement requested)
                && requested.ValueKind == JsonValueKind.Object)
            {
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "Prompt",
                    requested,
                    "prompt",
                    includeEmpty: true);
                AppendEnhancementJobDetailValue(builder, "Profile", requested, "profileId");
                AppendEnhancementJobDetailValue(builder, "長さ（秒）", requested, "durationSeconds");
                AppendEnhancementJobDetailValue(builder, "再生FPS", requested, "playbackFps");
                AppendEnhancementJobDetailValue(builder, "最大Pixel面積", requested, "maximumPixelArea");
                AppendEnhancementJobDetailValue(builder, "STEP", requested, "steps");
            }
            if (video.TryGetProperty("effective", out JsonElement effective)
                && effective.ValueKind == JsonValueKind.Object)
            {
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "実効Prompt",
                    effective,
                    "positivePrompt",
                    includeEmpty: false);
                AppendEnhancementJobDetailText(
                    builder,
                    "Negative Prompt",
                    effective,
                    "negativePrompt",
                    includeEmpty: true);
                AppendEnhancementJobDetailValue(builder, "幅", effective, "width");
                AppendEnhancementJobDetailValue(builder, "高さ", effective, "height");
                AppendEnhancementJobDetailValue(builder, "Frame数", effective, "frameCount");
                AppendEnhancementJobDetailValue(builder, "再生FPS", effective, "playbackFps");
                AppendEnhancementJobDetailValue(builder, "STEP", effective, "steps");
                AppendEnhancementJobDetailValue(builder, "CFG", effective, "cfg");
                AppendEnhancementJobDetailValue(builder, "Sampler", effective, "sampler");
                AppendEnhancementJobDetailValue(builder, "Scheduler", effective, "scheduler");
            }
            AppendEnhancementJobDetailValue(builder, "Seed", video, "seed");
        }
        else
        {
            if (i2iV3Snapshot is not null)
            {
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "全体",
                    i2iV3Snapshot.Overall,
                    includeEmpty: true);
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "表情",
                    i2iV3Snapshot.Expression,
                    includeEmpty: true);
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "服装",
                    i2iV3Snapshot.Outfit,
                    includeEmpty: true);
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "背景",
                    i2iV3Snapshot.Background,
                    includeEmpty: true);
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "ポーズ",
                    i2iV3Snapshot.Pose,
                    includeEmpty: true);
            }

            if (job.TryGetProperty("preset", out JsonElement preset)
                && preset.ValueKind == JsonValueKind.Object)
            {
                AppendEnhancementJobDetailValue(builder, "Denoise", preset, "denoise");
                if (preset.TryGetProperty("options", out JsonElement options)
                    && options.ValueKind == JsonValueKind.Object)
                {
                    if (i2iV3Snapshot is null)
                    {
                        promptWritten |= AppendEnhancementJobDetailText(
                            builder,
                            "全体",
                            options,
                            "overallInstruction",
                            includeEmpty: true);
                        promptWritten |= AppendEnhancementJobDetailText(
                            builder,
                            "表情",
                            options,
                            "expressionInstruction",
                            includeEmpty: true);
                        promptWritten |= AppendEnhancementJobDetailText(
                            builder,
                            "服装",
                            options,
                            "outfitInstruction",
                            includeEmpty: true);
                        promptWritten |= AppendEnhancementJobDetailText(
                            builder,
                            "背景",
                            options,
                            "backgroundInstruction",
                            includeEmpty: true);
                        promptWritten |= AppendEnhancementJobDetailText(
                            builder,
                            "ポーズ",
                            options,
                            "poseInstruction",
                            includeEmpty: true);
                    }
                    promptWritten |= AppendEnhancementJobDetailText(
                        builder,
                        i2iV3Snapshot is null ? "Prompt" : "合成Prompt",
                        options,
                        "prompt",
                        includeEmpty: true);
                    AppendEnhancementJobDetailText(
                        builder,
                        "Negative Prompt",
                        options,
                        "negativePrompt",
                        includeEmpty: true);
                    AppendEnhancementJobDetailValue(builder, "LoRA", options, "loraEnabled");
                    AppendEnhancementJobDetailValue(builder, "Strength", options, "strength");
                    AppendEnhancementJobDetailValue(builder, "STEP", options, "steps");
                    AppendEnhancementJobDetailValue(builder, "CFG", options, "cfgScale");
                    AppendEnhancementJobDetailValue(builder, "最大辺", options, "maxDimension");
                    AppendEnhancementJobDetailValue(builder, "Seed", options, "seed");
                    AppendEnhancementJobDetailValue(builder, "服装マスク", options, "outfitMaskMode");
                    AppendEnhancementJobDetailValue(builder, "マスク外縁", options, "outfitMaskExpandPixels", " px");
                }
            }

            if (!promptWritten)
            {
                promptWritten |= AppendEnhancementJobDetailText(
                    builder,
                    "Prompt",
                    job,
                    "prompt",
                    includeEmpty: true);
                AppendEnhancementJobDetailText(
                    builder,
                    "Negative Prompt",
                    job,
                    "negativePrompt",
                    includeEmpty: true);
            }
        }

        if (!promptWritten)
            AppendEnhancementJobDetailLine(builder, "Prompt", "このJobには保存されていません");

        string details = builder.ToString().TrimEnd();
        if (details.Length <= EnhancementJobRequestDetailsMaximumLength)
            return details;
        return details[..(EnhancementJobRequestDetailsMaximumLength - 2)] + "\n…";
    }

    private static bool AppendEnhancementJobDetailText(
        StringBuilder builder,
        string label,
        JsonElement parent,
        string propertyName,
        bool includeEmpty)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return AppendEnhancementJobDetailText(
            builder,
            label,
            value.GetString() ?? "",
            includeEmpty);
    }

    private static bool AppendEnhancementJobDetailText(
        StringBuilder builder,
        string label,
        string value,
        bool includeEmpty)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0 && !includeEmpty)
            return false;
        if (normalized.Length > MaxVideoPromptLength)
            normalized = normalized[..MaxVideoPromptLength] + "…";
        builder.Append(label).AppendLine(":");
        builder.AppendLine(normalized.Length == 0 ? "（空欄）" : normalized);
        return true;
    }

    private static void AppendEnhancementJobDetailValue(
        StringBuilder builder,
        string label,
        JsonElement parent,
        string propertyName,
        string suffix = "")
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
            return;
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null => "ランダム",
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
            return;
        AppendEnhancementJobDetailLine(builder, label, text + suffix);
    }

    private static void AppendEnhancementJobDetailLine(
        StringBuilder builder,
        string label,
        string value)
    {
        const int maximumValueLength = 1_024;
        string bounded = value.Length <= maximumValueLength
            ? value
            : value[..maximumValueLength] + "…";
        builder.Append(label).Append(": ").AppendLine(bounded);
    }

    private static bool HasExactInt32(
        JsonElement element,
        string propertyName,
        int expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.TryGetInt32(out int value)
            && value == expected;

    private static bool IsLowerHex(string? value, int length)
        => value is not null
            && value.Length == length
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private static string HashStableJson(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendStableJson(builder, element);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendStableJson(
        StringBuilder builder,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool firstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(
                                 static property => property.Name,
                                 StringComparer.Ordinal))
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append(
                        JsonSerializer.Serialize(
                            property.Name,
                            VideoStableJsonOptions));
                    builder.Append(':');
                    AppendStableJson(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    AppendStableJson(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(
                    JsonSerializer.Serialize(
                        element.GetString(),
                        VideoStableJsonOptions));
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer))
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                else
                    builder.Append(
                        element.GetDouble().ToString(
                            "R",
                            CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported JSON value in video snapshot.");
        }
    }

    private static bool IsImageEnhancementOperation(string? operation)
        => operation is "upscale" or "photoreal" or "i2i";

    private static string NormalizeEnhancementWorkspaceStatusFilter(string? filter)
        => filter is "queued" or "completed" or "failed" or "canceled"
            ? filter
            : "all";

    private static string NormalizeEnhancementWorkspaceOperationFilter(string? filter)
        => filter is "upscale" or "photoreal" or "video" or "i2i"
            ? filter
            : "all";

    private static string NormalizeEnhancementWorkspaceVideoKindFilter(
        string? filter)
        => filter is "generation" or "edit" or "trim" or "finish"
            ? filter
            : "all";

    private static int NormalizeEnhancementJobsHistoryLimit(int value)
        => value is 100 or 500 or 1_000
            ? value
            : EnhancementJobsDefaultHistoryLimit;

    private void SetEnhancementJobsHistoryLimit(int value, bool persist)
    {
        int normalized = NormalizeEnhancementJobsHistoryLimit(value);
        _enhancementJobsHistoryLimit = normalized;
        if (EnhancementJobsHistoryLimitComboBox is not null)
        {
            ComboBoxItem? item = EnhancementJobsHistoryLimitComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(candidate =>
                    int.TryParse(
                        Convert.ToString(candidate.Tag, CultureInfo.InvariantCulture),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int candidateValue)
                    && candidateValue == normalized);
            if (item is not null
                && !ReferenceEquals(
                    EnhancementJobsHistoryLimitComboBox.SelectedItem,
                    item))
            {
                _settingEnhancementJobsHistoryLimitSelection = true;
                try
                {
                    EnhancementJobsHistoryLimitComboBox.SelectedItem = item;
                }
                finally
                {
                    _settingEnhancementJobsHistoryLimitSelection = false;
                }
            }
        }
        if (persist)
            SaveState();
    }

    private async void EnhancementJobsHistoryLimit_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_settingEnhancementJobsHistoryLimitSelection
            || sender is not ComboBox
            {
                SelectedItem: ComboBoxItem { Tag: object tag },
            }
            || !int.TryParse(
                Convert.ToString(tag, CultureInfo.InvariantCulture),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int requested))
        {
            return;
        }

        int normalized = NormalizeEnhancementJobsHistoryLimit(requested);
        bool changed = normalized != _enhancementJobsHistoryLimit;
        SetEnhancementJobsHistoryLimit(normalized, persist: true);
        if (!changed || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        Interlocked.Exchange(ref _enhancementWorkspaceInventoryCts, null)?.Cancel();
        _enhancementWorkspaceGeneration++;
        _enhancementWorkspacePageIndex = 0;
        EnhancementJobsStatusText.Text =
            $"履歴の表示件数を最新 {normalized:N0}件へ変更しています…";
        await RefreshEnhancementJobsWorkspaceAsync(
            _enhancementWorkspaceGeneration,
            isPoll: false);
    }

    private bool MatchesEnhancementWorkspaceStatusFilter(
        EnhancementWorkspaceJobView job)
        => _enhancementWorkspaceStatusFilter switch
        {
            "queued" => job.Status == "queued",
            "failed" => job.Status == "failed",
            "canceled" => job.Status == "canceled",
            "completed" => job.Status is "succeeded" or "deleted",
            _ => true,
        };

    private bool MatchesEnhancementWorkspaceOperationFilter(
        EnhancementWorkspaceJobView job)
        => (_enhancementWorkspaceOperationFilter == "all"
                || string.Equals(
                    job.Operation,
                    _enhancementWorkspaceOperationFilter,
                    StringComparison.Ordinal))
            && MatchesEnhancementWorkspaceVideoKindFilter(job);

    private bool MatchesEnhancementWorkspaceVideoKindFilter(
        EnhancementWorkspaceJobView job)
        => _enhancementWorkspaceVideoKindFilter == "all"
            || string.Equals(
                job.VideoKindFilterKey,
                _enhancementWorkspaceVideoKindFilter,
                StringComparison.Ordinal);

    private string EnhancementWorkspaceOperationFilterLabel()
        => _enhancementWorkspaceOperationFilter switch
        {
            "upscale" => "高画質化",
            "photoreal" => "実写化",
            "video" when _enhancementWorkspaceVideoKindFilter == "edit" =>
                "AI動画編集",
            "video" when _enhancementWorkspaceVideoKindFilter == "finish" =>
                "AI動画高画質化",
            "video" when _enhancementWorkspaceVideoKindFilter == "trim" =>
                "動画トリム",
            "video" => "動画化",
            "i2i" => "AI編集",
            _ => "すべての処理",
        };

    private string EnhancementWorkspaceFilterLogMode()
        => $"{_enhancementWorkspaceStatusFilter}:"
            + $"{_enhancementWorkspaceOperationFilter}:"
            + _enhancementWorkspaceVideoKindFilter;

    private bool CanCancelAllQueuedEnhancementJobs()
        => _enhancementWorkspaceJobs.Any(job =>
            job.Status == "queued"
            && MatchesEnhancementWorkspaceOperationFilter(job)
            && job.CanCancel);

    private bool CanUpdateAllQueuedPhotorealPrompts()
        => _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported
            && _enhancementWorkspaceJobs.Any(static job =>
                job.CanUpdatePhotorealPrompts);

    private bool EnhancementWorkspaceHasCompleteTerminalHistory(string status)
        => _enhancementWorkspaceJobs.Count(job => job.Status == status)
            == EnhancementWorkspaceTotalStatusCount(status);

    private bool CanRetryAllTerminalEnhancementJobs(string status)
    {
        bool historyIsComplete =
            EnhancementWorkspaceHasCompleteTerminalHistory(status);
        if (!historyIsComplete)
        {
            return _enhancementWorkspaceTerminalHistoryBatchRetrySupported
                && EnhancementWorkspaceTotalStatusCount(status) > 0;
        }
        return _enhancementWorkspaceJobs.Any(job =>
                job.Status == status
                && MatchesEnhancementWorkspaceOperationFilter(job)
                && job.CanRetry);
    }

    private int EnhancementWorkspaceTotalStatusCount(string status)
    {
        EnhancementWorkspaceStatusCounts counts =
            _enhancementWorkspaceTotalCounts
            ?? CountEnhancementWorkspaceStatuses(_enhancementWorkspaceJobs);
        return status switch
        {
            "queued" => counts.Queued,
            "running" => counts.Running,
            "succeeded" => counts.Succeeded,
            "failed" => counts.Failed,
            "canceled" => counts.Canceled,
            "deleted" => counts.Deleted,
            _ => 0,
        };
    }

    private bool CanClearAllTerminalEnhancementJobs(string status)
    {
        int loadedStatusCount = _enhancementWorkspaceJobs.Count(job =>
            job.Status == status);
        int totalStatusCount = EnhancementWorkspaceTotalStatusCount(status);
        if (_enhancementWorkspaceTerminalHistoryTargetsSupported)
            return totalStatusCount > 0;
        return loadedStatusCount == totalStatusCount
            && _enhancementWorkspaceJobs.Any(job =>
                job.Status == status
                && MatchesEnhancementWorkspaceOperationFilter(job)
                && job.CanDismiss);
    }

    private EnhancementWorkspaceJobView[] BeginOptimisticBulkPresentation(
        IReadOnlySet<string> jobIds,
        bool hideRows)
    {
        EnhancementWorkspaceJobView[] visibleJobs =
            (EnhancementJobsList.ItemsSource
                as IEnumerable<EnhancementWorkspaceJobView>)?
                .Where(job => jobIds.Contains(job.Id))
                .ToArray()
            ?? [];
        foreach (EnhancementWorkspaceJobView job in visibleJobs)
            job.IsBusy = true;
        if (hideRows)
        {
            _enhancementWorkspaceOptimisticallyHiddenJobIds.UnionWith(jobIds);
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        }
        return visibleJobs;
    }

    private void EndOptimisticBulkPresentation(
        IReadOnlySet<string> jobIds,
        IReadOnlyList<EnhancementWorkspaceJobView> visibleJobs,
        bool revealRows)
    {
        foreach (EnhancementWorkspaceJobView job in visibleJobs)
            job.IsBusy = false;
        if (!revealRows)
            return;

        _enhancementWorkspaceOptimisticallyHiddenJobIds.ExceptWith(jobIds);
        if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
    }

    private void RefreshTerminalBulkControlPresentation(
        string status,
        Button retryButton,
        Button clearButton,
        string clearLabel)
    {
        EnhancementWorkspaceJobView[] terminalJobs = _enhancementWorkspaceJobs
            .Where(job =>
                job.Status == status
                && MatchesEnhancementWorkspaceOperationFilter(job))
            .ToArray();
        int retryableCount = terminalJobs.Count(static job => job.CanRetry);
        int loadedDismissibleCount = terminalJobs.Count(static job => job.CanDismiss);
        bool serverSelectsAllHistory =
            _enhancementWorkspaceTerminalHistoryTargetsSupported;
        int dismissibleCount = serverSelectsAllHistory
            && _enhancementWorkspaceOperationFilter == "all"
                ? EnhancementWorkspaceTotalStatusCount(status)
                : loadedDismissibleCount;
        int retryProtectedCount = terminalJobs.Length - retryableCount;
        int dismissProtectedCount = terminalJobs.Length - loadedDismissibleCount;
        bool legacyHistoryIsComplete =
            EnhancementWorkspaceHasCompleteTerminalHistory(status);
        bool serverRetriesAllHistory =
            _enhancementWorkspaceTerminalHistoryBatchRetrySupported;
        retryButton.Content = serverRetriesAllHistory
            ? "全部リトライ (全履歴)"
            : legacyHistoryIsComplete
            ? $"全部リトライ ({retryableCount:N0})"
            : "全部リトライ (更新が必要)";
        retryButton.ToolTip = !serverRetriesAllHistory
            && !legacyHistoryIsComplete
            ? "画面外の履歴を『全部』から漏らさないため、Companionの一括リトライ対応後に実行できます。"
            : serverRetriesAllHistory
                ? $"現在の種類フィルターに合う{status}履歴を、画面の表示件数に関係なく保存済み設定で一括再試行します。成功した元履歴だけを消します。"
            : retryableCount > 0
            ? $"再試行できる{retryableCount:N0}件を、それぞれの保存済みPrompt・設定で待機列の末尾へ追加します。"
                + (retryProtectedCount > 0
                    ? $" 保護対象{retryProtectedCount:N0}件は変更しません。"
                    : "")
            : "保存済み設定で安全に再試行できるJobはありません。future・malformed・read-only等の保護対象は変更しません。";
        clearButton.Content = serverSelectsAllHistory
            && _enhancementWorkspaceOperationFilter != "all"
                ? $"{clearLabel} (全履歴)"
                : $"{clearLabel} ({dismissibleCount:N0})";
        clearButton.ToolTip = !serverSelectsAllHistory
            && !legacyHistoryIsComplete
                ? "画面外の履歴を『全部』から漏らさないため、Companion更新後に一括消去できます。"
            : dismissibleCount > 0
            ? serverSelectsAllHistory
                ? $"現在の種類フィルターに合う{status}履歴を、画面の表示件数に関係なく一括で消します。元画像と出力ファイルは変更しません。"
                : $"削除可能な履歴{dismissibleCount:N0}件だけを消します。元画像と出力ファイルは変更しません。"
                + (dismissProtectedCount > 0
                    ? $" 保護対象{dismissProtectedCount:N0}件は残します。"
                    : "")
            : "削除可能な履歴はありません。future・malformed・read-only等の保護対象は残します。";
    }

    private void RefreshEnhancementQueueBulkControls()
    {
        if (EnhancementJobsClearQueuedButton is not null)
        {
            EnhancementWorkspaceJobView[] queuedJobs = _enhancementWorkspaceJobs
                .Where(job =>
                    job.Status == "queued"
                    && MatchesEnhancementWorkspaceOperationFilter(job))
                .ToArray();
            int cancelableCount = queuedJobs.Count(static job => job.CanCancel);
            int protectedCount = queuedJobs.Length - cancelableCount;
            string operationLabel = EnhancementWorkspaceOperationFilterLabel();
            EnhancementJobsClearQueuedButton.Content =
                $"待機中をすべて消す ({cancelableCount:N0})";
            EnhancementJobsClearQueuedButton.ToolTip = cancelableCount > 0
                ? $"現在の種類フィルター「{operationLabel}」で安全にキャンセルできる待機中 {cancelableCount:N0}件だけを消します。実行中のジョブは変えません。"
                    + (protectedCount > 0
                        ? $" future・malformed・read-only等の保護対象 {protectedCount:N0}件は残します。"
                        : "")
                : "安全にキャンセルできる待機中Jobはありません。future・malformed・read-only等の保護対象は残します。";
            EnhancementJobsClearQueuedButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanCancelAllQueuedEnhancementJobs();
        }

        if (EnhancementJobsQueuedBulkPanel is not null)
        {
            EnhancementJobsQueuedBulkPanel.Visibility =
                _enhancementWorkspaceStatusFilter == "queued"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (EnhancementJobsUpdateQueuedPromptsButton is not null)
        {
            EnhancementJobsUpdateQueuedPromptsButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanUpdateAllQueuedPhotorealPrompts();
        }

        if (EnhancementJobsRetryFailedButton is not null)
        {
            EnhancementJobsRetryFailedButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanRetryAllTerminalEnhancementJobs("failed");
        }

        if (EnhancementJobsClearFailedButton is not null)
        {
            EnhancementJobsClearFailedButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanClearAllTerminalEnhancementJobs("failed");
        }

        if (EnhancementJobsRetryCanceledButton is not null)
        {
            EnhancementJobsRetryCanceledButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanRetryAllTerminalEnhancementJobs("canceled");
        }

        if (EnhancementJobsClearCanceledButton is not null)
        {
            EnhancementJobsClearCanceledButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanClearAllTerminalEnhancementJobs("canceled");
        }

        if (EnhancementJobsRetryFailedButton is not null
            && EnhancementJobsClearFailedButton is not null)
        {
            RefreshTerminalBulkControlPresentation(
                "failed",
                EnhancementJobsRetryFailedButton,
                EnhancementJobsClearFailedButton,
                "失敗を全部消す");
        }

        if (EnhancementJobsRetryCanceledButton is not null
            && EnhancementJobsClearCanceledButton is not null)
        {
            RefreshTerminalBulkControlPresentation(
                "canceled",
                EnhancementJobsRetryCanceledButton,
                EnhancementJobsClearCanceledButton,
                "キャンセルを全部消す");
        }

        if (EnhancementJobsFailedBulkPanel is not null)
        {
            EnhancementJobsFailedBulkPanel.Visibility =
                _enhancementWorkspaceStatusFilter == "failed"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (EnhancementJobsCanceledBulkPanel is not null)
        {
            EnhancementJobsCanceledBulkPanel.Visibility =
                _enhancementWorkspaceStatusFilter == "canceled"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void InitializeEnhancementJobsWorkspace()
    {
        _enhancementWorkspacePollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _enhancementWorkspacePollTimer.Tick += EnhancementWorkspacePollTimer_Tick;
        _enhancementWorkspaceThumbnailViewportTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = EnhancementJobsThumbnailViewportDebounce,
        };
        _enhancementWorkspaceThumbnailViewportTimer.Tick +=
            EnhancementWorkspaceThumbnailViewportTimer_Tick;
        _enhancementWorkspaceThumbnailRetryTimer = new DispatcherTimer(
            DispatcherPriority.Background);
        _enhancementWorkspaceThumbnailRetryTimer.Tick +=
            EnhancementWorkspaceThumbnailRetryTimer_Tick;
        EnhancementJobsList.ItemContainerGenerator.StatusChanged +=
            EnhancementJobsItemContainerGenerator_StatusChanged;
    }

    private async void OpenEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => await OpenEnhancementJobsWorkspaceAsync("all");

    private async Task OpenEnhancementJobsWorkspaceAsync(
        string initialFilter,
        IReadOnlyCollection<string>? highlightedJobIds = null,
        IInputElement? focusToRestore = null,
        bool restoreReturnViewport = false,
        string? initialOperationFilter = null)
    {
        if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            return;

        var operationWatch = Stopwatch.StartNew();
        string operationOutcome = "failed";

        try
        {
            StopGalleryAutoScroll();
            SearchHistoryPopup.IsOpen = false;
            _enhancementWorkspaceFocusBeforeDialog = focusToRestore ?? Keyboard.FocusedElement;
            ResetEnhancementWorkspaceThumbnailRetry();
            _enhancementWorkspaceStatusFilter =
                NormalizeEnhancementWorkspaceStatusFilter(initialFilter);
            _enhancementWorkspaceOperationFilter = initialOperationFilter is null
                ? NormalizeEnhancementWorkspaceOperationFilter(initialFilter)
                : NormalizeEnhancementWorkspaceOperationFilter(
                    initialOperationFilter);
            _enhancementWorkspaceVideoKindFilter = "all";
            if (!restoreReturnViewport)
                _enhancementWorkspacePageIndex = 0;
            RefreshEnhancementWorkspaceFilterToggleStates();
            _enhancementWorkspaceHighlightedJobIds.Clear();
            if (highlightedJobIds is not null)
            {
                _enhancementWorkspaceHighlightedJobIds.UnionWith(
                    highlightedJobIds.Where(static id =>
                        !string.IsNullOrWhiteSpace(id)));
                _enhancementWorkspaceHighlightExpiresAt =
                    DateTimeOffset.UtcNow.AddSeconds(20);
            }
            else
            {
                _enhancementWorkspaceHighlightExpiresAt = default;
            }
            EnhancementJobsDialog.Visibility = Visibility.Visible;
            bool canReuseCachedInventory =
                _enhancementWorkspaceJobs.Count > 0
                && _enhancementWorkspaceHealthInventorySignature is not null
                && _enhancementWorkspaceHealthEndpointSupported != false;
            EnhancementJobsStatusText.Text = canReuseCachedInventory
                ? "Checking the cached jobs inventory..."
                : UsesDirectEnhancementJobsSqliteReader()
                    ? "ローカルJob履歴を読み込んでいます…"
                    : "Loading jobs from the local companion...";
            if (!canReuseCachedInventory)
            {
                _enhancementWorkspaceHealthEndpointSupported = null;
                _enhancementWorkspaceHealthInventorySignature = null;
            }
            _enhancementWorkspaceQueuePaused = null;
            _enhancementWorkspaceQueueRecoveryRequired = false;
            RefreshEnhancementQueuePauseControl();
            if (!canReuseCachedInventory)
                ApplyEnhancementQueueHealthUnavailable("Checking queue health...");
            EnhancementJobsEmptyText.Visibility = Visibility.Collapsed;
            if (!restoreReturnViewport && !canReuseCachedInventory)
                EnhancementJobsList.ItemsSource = null;
            long generation = ++_enhancementWorkspaceGeneration;
            _ = Dispatcher.BeginInvoke(
                EnhancementJobsRefreshButton.Focus,
                DispatcherPriority.Input);
            if (canReuseCachedInventory)
            {
                // Reuse the already validated active queue plus bounded history
                // view models when compact health proves that the inventory has
                // not changed. The health signature requests a new snapshot only
                // when counts, terminal history, queue ownership, or the
                // companion epoch actually changed.
                ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
                await PollEnhancementJobsWorkspaceAsync(generation);
                if (generation == _enhancementWorkspaceGeneration
                    && EnhancementJobsDialog.Visibility == Visibility.Visible)
                {
                    int activeCount = _enhancementWorkspaceJobs.Count(
                        static job => job.IsActive);
                    bool liveQueueHealth =
                        _enhancementWorkspaceHealthInventorySignature is not null;
                    bool automaticPollingAvailable =
                        liveQueueHealth
                        || !UsesDirectEnhancementJobsSqliteReader();
                    if (!_aiProcessingMinimizedMode
                        && activeCount > 0
                        && automaticPollingAvailable)
                        _enhancementWorkspacePollTimer.Start();
                    else
                        _enhancementWorkspacePollTimer.Stop();
                    EnhancementJobsStatusText.Text =
                        FormatEnhancementJobsInventoryStatus(
                            activeCount,
                            _enhancementWorkspaceJobs.Count(
                                static job => job.Status == "running"),
                            _enhancementWorkspaceJobs.Count(
                                static job => job.Status == "queued"),
                            automaticPollingAvailable);
                }
            }
            else
            {
                await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
            }
            if (restoreReturnViewport)
                await RestoreEnhancementJobsReturnViewportAsync();
            operationOutcome = EnhancementJobsDialog.Visibility == Visibility.Visible
                ? "completed"
                : "canceled";
        }
        finally
        {
            AibosOperationLog.Write(
                "jobs_workspace_open",
                operationOutcome,
                operationWatch.ElapsedMilliseconds,
                mode: EnhancementWorkspaceFilterLogMode(),
                itemCount: _enhancementWorkspaceJobs.Count);
        }
    }

    private void CloseEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => CloseEnhancementJobsWorkspace(restoreFocus: true);

    private void CloseEnhancementJobsWorkspace(bool restoreFocus)
    {
        if (EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceGeneration++;
        _enhancementWorkspacePollTimer.Stop();
        StopEnhancementWorkspaceThumbnailViewportDebounce();
        _enhancementWorkspaceThumbnailRetryTimer.Stop();
        _enhancementWorkspaceThumbnailRetryAttempt = 0;
        _enhancementWorkspaceThumbnailFailedJobIds.Clear();
        _enhancementWorkspaceThumbnailViewportLoadPending = false;
        Volatile.Read(ref _enhancementWorkspaceThumbnailCts)?.Cancel();
        Volatile.Read(ref _enhancementWorkspaceInventoryCts)?.Cancel();
        _enhancementWorkspaceOptimisticallyHiddenJobIds.Clear();
        EnhancementJobsDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus)
            RestoreOverlayFocus(_enhancementWorkspaceFocusBeforeDialog);
        _enhancementWorkspaceFocusBeforeDialog = null;
    }

    private void EnhancementJobsBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EnhancementJobsDialog.Visibility == Visibility.Visible && ReferenceEquals(e.OriginalSource, EnhancementJobsDialog))
        {
            CloseEnhancementJobsWorkspace(restoreFocus: true);
            e.Handled = true;
        }
    }

    private async void RefreshEnhancementJobs_Click(object sender, RoutedEventArgs e)
    {
        if (_enhancementWorkspaceMutationPending
            || _enhancementWorkspaceHealthPollPending)
            return;
        var operationWatch = Stopwatch.StartNew();
        string operationOutcome = "failed";
        try
        {
            ResetEnhancementWorkspaceThumbnailRetry();
            await RefreshEnhancementJobsWorkspaceAsync(
                _enhancementWorkspaceGeneration,
                isPoll: false);
            operationOutcome = EnhancementJobsDialog.Visibility == Visibility.Visible
                ? "completed"
                : "canceled";
        }
        catch (Exception ex)
        {
            PreserveEnhancementWorkspaceAfterRefreshFailure(
                "Jobs could not be refreshed. The last valid snapshot is still shown; retrying.");
            operationOutcome = $"failed:{ex.GetType().Name}";
        }
        finally
        {
            AibosOperationLog.Write(
                "jobs_workspace_refresh",
                operationOutcome,
                operationWatch.ElapsedMilliseconds,
                mode: EnhancementWorkspaceFilterLogMode(),
                itemCount: _enhancementWorkspaceJobs.Count);
        }
    }

    private async void ToggleEnhancementQueuePaused_Click(object sender, RoutedEventArgs e)
    {
        if (_enhancementWorkspaceQueuePaused is bool paused)
        {
            bool requestedPaused = paused
                ? false
                : !_enhancementWorkspaceQueueRecoveryRequired;
            await SetEnhancementQueuePausedAsync(requestedPaused);
        }
        else if (_usingDefaultModalEnhancementSender)
        {
            await SetEnhancementQueuePausedAsync(paused: false);
        }
    }

    private async Task<bool> SetEnhancementQueuePausedAsync(bool paused)
    {
        if (_enhancementWorkspaceMutationPending
            || _enhancementWorkspaceRefreshPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return false;
        }
        bool unknownExplicitResume =
            _enhancementWorkspaceQueuePaused is null
            && !paused
            && _usingDefaultModalEnhancementSender;
        bool hasCurrentState = _enhancementWorkspaceQueuePaused is bool;
        bool current = _enhancementWorkspaceQueuePaused ?? false;
        if (!hasCurrentState && !unknownExplicitResume)
        {
            return false;
        }
        if (!unknownExplicitResume
            && current == paused
            && !_enhancementWorkspaceQueueRecoveryRequired)
        {
            return true;
        }

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        EnhancementJobsRefreshButton.IsEnabled = false;
        RefreshEnhancementQueuePauseControl();
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            if (unknownExplicitResume)
            {
                EnhancementJobsStatusText.Text =
                    "ローカルAIサービスへ接続し、キュー状態を確認しています…";
                EnhancementApiResponse readiness =
                    await EnsureEnhancementCompanionReadyForExplicitActionAsync();
                if (generation != _enhancementWorkspaceGeneration
                    || EnhancementJobsDialog.Visibility != Visibility.Visible)
                {
                    return false;
                }
                if (!readiness.Ok)
                {
                    EnhancementJobsStatusText.Text = readiness.Error;
                    return false;
                }

                EnhancementQueueHealthView? refreshedHealth =
                    await RefreshEnhancementQueueHealthAsync(
                        generation,
                        isPoll: false);
                if (refreshedHealth is not EnhancementQueueHealthView health
                    || health.Paused is not bool observedPaused)
                {
                    EnhancementJobsStatusText.Text =
                        "キュー状態を確認できませんでした。ローカルAIサービスの詳細を確認してください。";
                    return false;
                }
                current = observedPaused;
                if (current == paused
                    && !health.QueueRecoveryRequired)
                {
                    EnhancementJobsStatusText.Text =
                        "ローカルAIサービスへ接続しました。キューはすでに動作中です。";
                    return true;
                }
            }

            EnhancementApiResponse response =
                await SendTrackedEnhancementWorkspaceMutationAsync(
                    () => SendEnhancementApiAsync(
                        HttpMethod.Post,
                        "api/enhance/queue",
                        new { paused }),
                    requireInventoryRevisionAdvanceOnAmbiguous: false);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return false;
            }
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("paused", out JsonElement pausedElement)
                || pausedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                EnhancementJobsStatusText.Text = response.Ok
                    ? "The companion returned an invalid queue pause response."
                    : response.Error;
                AibosOperationLog.Write(
                    "queue_pause_change",
                    "failed",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode,
                    response.Ok
                        ? "RESPONSE_CONTRACT_INVALID"
                        : EnhancementApiErrorCode(response),
                    mode: paused ? "pause" : "resume");
                return false;
            }

            bool persistedPaused = pausedElement.GetBoolean();
            if (persistedPaused != paused)
            {
                EnhancementJobsStatusText.Text =
                    "The companion did not apply the requested queue pause state.";
                return false;
            }

            _enhancementWorkspaceQueuePaused = persistedPaused;
            _enhancementWorkspaceQueueRecoveryRequired = false;
            RefreshEnhancementQueuePauseControl();
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return false;
            }
            EnhancementJobsStatusText.Text = persistedPaused
                ? "キューを一時停止しました。処理中の1件は完了し、次の待機ジョブから止まります。"
                : "キューを再開しました。待機順を維持したまま処理を続けます。";
            AibosOperationLog.Write(
                "queue_pause_change",
                "completed",
                operationWatch.ElapsedMilliseconds,
                response.StatusCode,
                mode: persistedPaused ? "pause" : "resume");
            return true;
        }
        finally
        {
            _enhancementWorkspaceMutationPending = false;
            EnhancementJobsRefreshButton.IsEnabled = !_enhancementWorkspaceRefreshPending;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private async void EnhancementWorkspacePollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_aiProcessingMinimizedMode)
            {
                _enhancementWorkspacePollTimer.Stop();
                return;
            }
            if (EnhancementJobsDialog.Visibility != Visibility.Visible
                || _enhancementWorkspaceMutationPending
                || _enhancementWorkspaceHealthPollPending
                || (_enhancementWorkspaceRefreshPending && _enhancementWorkspaceRefreshGeneration == _enhancementWorkspaceGeneration))
                return;

            _enhancementWorkspacePollCount++;
            await PollEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration);
        }
        catch (Exception ex)
        {
            PreserveEnhancementWorkspaceAfterRefreshFailure(
                "Jobs could not be refreshed. The last valid snapshot is still shown; retrying.");
            AibosOperationLog.Write(
                "jobs_workspace_poll",
                "failed",
                0,
                mode: ex.GetType().Name,
                itemCount: _enhancementWorkspaceJobs.Count);
        }
    }

    private void PreserveEnhancementWorkspaceAfterRefreshFailure(string message)
    {
        EnhancementJobsStatusText.Text = message;
        if (!_aiProcessingMinimizedMode
            && EnhancementJobsDialog.Visibility == Visibility.Visible
            && (_enhancementWorkspaceHealthInventorySignature is not null
                || !UsesDirectEnhancementJobsSqliteReader())
            && _enhancementWorkspaceJobs.Any(static job => job.IsActive))
        {
            _enhancementWorkspacePollTimer.Start();
        }
    }

    private async Task PollEnhancementJobsWorkspaceAsync(long generation)
    {
        if (_aiProcessingMinimizedMode
            || _enhancementWorkspaceHealthPollPending
            || _enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        _enhancementWorkspaceHealthPollPending = true;
        try
        {
            EnhancementQueueHealthView? health =
                await RefreshEnhancementQueueHealthAsync(generation, isPoll: true);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }

            string? observedSignature = health?.InventorySignature;
            if (observedSignature is not null
                && !HasEnhancementWorkspaceMutationDebt
                && string.Equals(
                    observedSignature,
                    _enhancementWorkspaceHealthInventorySignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            // The compact health payload is enough for idle progress ticks.
            // Fetch the active queue plus bounded history only when its status
            // counts/current job changed, or when an older companion cannot
            // provide health.
            await RefreshEnhancementJobsWorkspaceAsync(
                generation,
                isPoll: true,
                refreshHealth: false,
                observedHealthInventorySignature: observedSignature,
                observedHealthInventoryRevision: health?.InventoryRevision);
        }
        finally
        {
            _enhancementWorkspaceHealthPollPending = false;
        }
    }

    private async Task RefreshEnhancementJobsWorkspaceAsync(
        long generation,
        bool isPoll,
        bool refreshHealth = true,
        string? observedHealthInventorySignature = null,
        long? observedHealthInventoryRevision = null,
        int healthInventoryCoalesceAttemptsRemaining = 1)
    {
        if ((_enhancementWorkspaceRefreshPending && _enhancementWorkspaceRefreshGeneration == generation)
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return;

        _enhancementWorkspaceRefreshPending = true;
        _enhancementWorkspaceRefreshGeneration = generation;
        long queuePresentationRevision =
            _enhancementWorkspaceQueuePresentationRevision;
        string? coalescedHealthInventorySignature = null;
        long? coalescedHealthInventoryRevision = null;
        bool forceHealthPollAfterInventory = false;
        long mutationDebtEpochAtReadStart =
            _enhancementWorkspaceMutationDebtEpoch;
        long? mutationDebtMinimumInventoryRevisionAtReadStart =
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision;
        bool mutationDebtAtReadStart = mutationDebtEpochAtReadStart
            > _enhancementWorkspaceReconciledMutationDebtEpoch;
        EnhancementJobsRefreshButton.IsEnabled = false;
        RefreshEnhancementQueuePauseControl();
        if (!isPoll)
            EnhancementJobsStatusText.Text = "Refreshing jobs...";
        try
        {
            if (refreshHealth)
            {
                // Bind the Jobs snapshot only to a health signature observed
                // before that snapshot is requested. Reading health after an
                // in-flight jobs response can attach a newer runtime/count
                // signature to an older snapshot and suppress the next poll.
                EnhancementQueueHealthView? healthBeforeInventory =
                    await RefreshEnhancementQueueHealthAsync(generation, isPoll);
                if (generation != _enhancementWorkspaceGeneration
                    || EnhancementJobsDialog.Visibility != Visibility.Visible)
                {
                    return;
                }
                observedHealthInventorySignature =
                    healthBeforeInventory?.InventorySignature;
                observedHealthInventoryRevision =
                    healthBeforeInventory?.InventoryRevision;
            }

            _enhancementWorkspaceGetCount++;
            using var inventoryCts = new CancellationTokenSource();
            CancellationTokenSource? previousInventoryCts = Interlocked.Exchange(
                ref _enhancementWorkspaceInventoryCts,
                inventoryCts);
            previousInventoryCts?.Cancel();
            bool parsed;
            List<EnhancementWorkspaceJobView> jobs;
            string? error;
            try
            {
                (parsed, jobs, error) = await ReadEnhancementWorkspaceInventoryAsync(
                    inventoryCts.Token);
            }
            catch (OperationCanceledException) when (inventoryCts.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _enhancementWorkspaceInventoryCts,
                    null,
                    inventoryCts);
            }
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;

            if (!parsed)
            {
                string failure = string.IsNullOrWhiteSpace(error)
                    ? "Jobs could not be refreshed."
                    : error.Trim();
                PreserveEnhancementWorkspaceAfterRefreshFailure(
                    $"{failure} The last valid snapshot is still shown; retrying.");
                return;
            }

            // A queue mutation may finish while this GET is in flight. Applying
            // the older inventory would make the row jump back until the next
            // poll and makes "これを次に処理" look as though it did nothing.
            if (queuePresentationRevision
                != _enhancementWorkspaceQueuePresentationRevision)
            {
                return;
            }

            if (observedHealthInventorySignature is not null
                && _enhancementWorkspaceHealthEndpointSupported != false)
            {
                EnhancementQueueHealthView? healthAfterInventory =
                    await RefreshEnhancementQueueHealthAsync(generation, isPoll);
                if (generation != _enhancementWorkspaceGeneration
                    || EnhancementJobsDialog.Visibility != Visibility.Visible)
                {
                    return;
                }

                string? healthAfterInventorySignature =
                    healthAfterInventory?.InventorySignature;
                long? healthAfterInventoryRevision =
                    healthAfterInventory?.InventoryRevision;
                if (healthAfterInventorySignature is not null
                    && (!string.Equals(
                            healthAfterInventorySignature,
                            observedHealthInventorySignature,
                            StringComparison.Ordinal)
                        || mutationDebtAtReadStart
                            && healthAfterInventoryRevision
                                != observedHealthInventoryRevision))
                {
                    if (healthInventoryCoalesceAttemptsRemaining > 0)
                    {
                        // The inventory was in flight while queue/runtime state
                        // changed. Do not display it or mark the newer health
                        // signature handled; coalesce exactly one replacement
                        // snapshot read after this single-flight section exits.
                        coalescedHealthInventorySignature =
                            healthAfterInventorySignature;
                        coalescedHealthInventoryRevision =
                            healthAfterInventoryRevision;
                    }
                    else
                    {
                        // Continuous churn must not create an unbounded chain
                        // of snapshot reads. Apply this latest inventory
                        // without accepting a mismatched signature and keep one
                        // compact-health poll alive for the next reconciliation.
                        observedHealthInventorySignature = null;
                        observedHealthInventoryRevision = null;
                        forceHealthPollAfterInventory = true;
                    }
                }
            }

            // The post-inventory health read is also an await boundary. A
            // rapid optimistic reorder may happen while it is in flight, so
            // gate reconciliation again after every awaited read instead of
            // allowing an older inventory to overwrite the visible order.
            if (queuePresentationRevision
                != _enhancementWorkspaceQueuePresentationRevision)
            {
                return;
            }

            if (coalescedHealthInventorySignature is null)
            {
            bool activeMembershipChanged = !SameActiveEnhancementJobIds(
                _enhancementWorkspaceJobs,
                jobs);
            ApplyEnhancementWorkspaceHighlights(jobs);
            ReconcileEnhancementWorkspaceJobs(jobs);
            ReconcileEnhancementWorkspaceMutationDebt(
                mutationDebtAtReadStart,
                mutationDebtEpochAtReadStart,
                mutationDebtMinimumInventoryRevisionAtReadStart,
                observedHealthInventoryRevision);
            if (!isPoll || activeMembershipChanged)
                QueueEnhancedStateRefreshIfChanged();
            bool highlightedBatchAlreadyTerminal = _enhancementWorkspaceStatusFilter == "queued"
                && jobs.Any(static job => job.IsHighlighted)
                && !jobs.Any(static job => job.IsHighlighted && job.IsActive);
            if (highlightedBatchAlreadyTerminal)
            {
                _enhancementWorkspaceStatusFilter = "all";
                _enhancementWorkspacePageIndex = 0;
                RefreshEnhancementWorkspaceFilterToggleStates();
            }
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            EnhancementWorkspaceStatusCounts counts =
                _enhancementWorkspaceTotalCounts
                ?? CountEnhancementWorkspaceStatuses(jobs);
            int activeCount = counts.Active;
            int runningCount = counts.Running;
            int queuedCount = counts.Queued;
            int completedCount = counts.Completed;
            int failedCount = counts.Failed;
            int canceledCount = counts.Canceled;
            int loadedHistoryCount = jobs.Count(static job => !job.IsActive);
            int totalHistoryCount = counts.Total - counts.Active;
            bool liveQueueHealth =
                observedHealthInventorySignature is not null
                || _enhancementWorkspaceHealthInventorySignature is not null;
            bool automaticPollingAvailable =
                liveQueueHealth
                || !UsesDirectEnhancementJobsSqliteReader();
            RefreshEnhancementQueueBulkControls();
            EnhancementJobsHeaderSummary.Text =
                $"{counts.Total:N0} total  ·  {activeCount:N0} active  ·  {completedCount:N0} completed"
                + $"  ·  {failedCount:N0} failed  ·  {canceledCount:N0} canceled"
                + (loadedHistoryCount < totalHistoryCount
                    ? $"  ·  latest {loadedHistoryCount:N0}/{totalHistoryCount:N0} history loaded"
                    : "");
            EnhancementJobsStatusText.Text = FormatEnhancementJobsInventoryStatus(
                activeCount,
                runningCount,
                queuedCount,
                automaticPollingAvailable);
            if (highlightedBatchAlreadyTerminal)
                EnhancementJobsStatusText.Text += " The new batch already finished, so all highlighted jobs are shown.";
            if (!_aiProcessingMinimizedMode
                && (activeCount > 0 && automaticPollingAvailable
                    || forceHealthPollAfterInventory
                    || HasEnhancementWorkspaceMutationDebt))
                _enhancementWorkspacePollTimer.Start();
            else
                _enhancementWorkspacePollTimer.Stop();

            if (observedHealthInventorySignature is not null
                && !HasEnhancementWorkspaceMutationDebt)
            {
                _enhancementWorkspaceHealthInventorySignature =
                    observedHealthInventorySignature;
            }
            }
        }
        finally
        {
            if (_enhancementWorkspaceRefreshGeneration == generation)
            {
                _enhancementWorkspaceRefreshPending = false;
                if (EnhancementJobsRefreshButton is not null)
                    EnhancementJobsRefreshButton.IsEnabled = !_enhancementWorkspaceMutationPending;
                RefreshEnhancementQueuePauseControl();
            }
        }

        if (coalescedHealthInventorySignature is not null
            && generation == _enhancementWorkspaceGeneration
            && EnhancementJobsDialog.Visibility == Visibility.Visible)
        {
            await RefreshEnhancementJobsWorkspaceAsync(
                generation,
                isPoll,
                refreshHealth: false,
                observedHealthInventorySignature:
                    coalescedHealthInventorySignature,
                observedHealthInventoryRevision:
                    coalescedHealthInventoryRevision,
                healthInventoryCoalesceAttemptsRemaining:
                    healthInventoryCoalesceAttemptsRemaining - 1);
        }
    }

    private async Task<EnhancementQueueHealthView?> RefreshEnhancementQueueHealthAsync(
        long generation,
        bool isPoll)
    {
        if (isPoll && _enhancementWorkspaceHealthEndpointSupported == false)
            return null;

        _enhancementWorkspaceHealthGetCount++;
        EnhancementApiResponse response =
            await SendPassiveEnhancementReadAsync("api/enhance/health");
        if (generation != _enhancementWorkspaceGeneration
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return null;
        }

        if (response.StatusCode == 404)
        {
            _enhancementWorkspaceHealthEndpointSupported = false;
            _enhancementWorkspaceHealthInventoryRevisionSupported = false;
            _enhancementWorkspaceLastHealthInventoryRevision = null;
            ApplyEnhancementQueueHealthUnavailable(
                "Update the local companion to show queue health.");
            return null;
        }

        _enhancementWorkspaceHealthEndpointSupported = true;
        if (!response.Ok || response.Payload is not JsonElement payload)
        {
            ApplyEnhancementQueueHealthUnavailable(
                "Queue health could not be read. Jobs remain available.");
            return null;
        }

        if (!TryParseEnhancementQueueHealth(payload, out EnhancementQueueHealthView health))
        {
            ApplyEnhancementQueueHealthUnavailable(
                "The companion returned an unsupported health response.");
            return null;
        }

        ApplyEnhancementQueueHealth(health);
        return health;
    }

    private static bool TryParseEnhancementQueueHealth(
        JsonElement payload,
        out EnhancementQueueHealthView health)
    {
        health = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("version", out JsonElement versionElement)
            || !versionElement.TryGetInt32(out int version)
            || version != 1
            || !payload.TryGetProperty("status", out JsonElement statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? status = statusElement.GetString();
        if (status is not ("healthy" or "working" or "needs-attention")
            || !payload.TryGetProperty("issues", out JsonElement issuesElement)
            || issuesElement.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("jobs", out JsonElement jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Object
            || !jobsElement.TryGetProperty("counts", out JsonElement countsElement)
            || countsElement.ValueKind != JsonValueKind.Object
            || !TryReadNonNegativeCount(countsElement, "queued", out int queued)
            || !TryReadNonNegativeCount(countsElement, "running", out int running)
            || !TryReadNonNegativeCount(countsElement, "succeeded", out int succeeded)
            || !TryReadNonNegativeCount(countsElement, "failed", out int failed)
            || !TryReadNonNegativeCount(countsElement, "canceled", out int canceled)
            || !TryReadNonNegativeCount(countsElement, "deleted", out int deleted)
            || !payload.TryGetProperty("runtime", out JsonElement runtimeElement)
            || runtimeElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? firstIssueCode = null;
        foreach (JsonElement issueElement in issuesElement.EnumerateArray())
        {
            if (issueElement.ValueKind != JsonValueKind.String)
                return false;
            firstIssueCode ??= issueElement.GetString();
        }

        if (!TryReadOptionalHealthTimestamp(
                runtimeElement,
                "serverStartedAtUtc",
                out string serverStartedAtSignature)
            || serverStartedAtSignature == "-"
            || !runtimeElement.TryGetProperty(
                "processId",
                out JsonElement processIdElement)
            || !processIdElement.TryGetInt32(out int processId)
            || processId <= 0)
        {
            return false;
        }
        string buildIdSignature = "-";
        if (!runtimeElement.TryGetProperty("buildId", out JsonElement buildIdElement))
        {
            return false;
        }
        if (buildIdElement.ValueKind == JsonValueKind.String)
        {
            string? buildId = buildIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(buildId))
                buildIdSignature = buildId;
        }
        else if (buildIdElement.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        string revision = "Local AI revision unavailable";
        if (runtimeElement.TryGetProperty("sourceRevision", out JsonElement revisionElement))
        {
            if (revisionElement.ValueKind == JsonValueKind.String)
            {
                string? sourceRevision = revisionElement.GetString();
                if (!string.IsNullOrWhiteSpace(sourceRevision))
                {
                    string prefix = sourceRevision.Length > 8
                        ? sourceRevision[..8]
                        : sourceRevision;
                    revision = $"Local AI {prefix}";
                }
            }
            else if (revisionElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        string? currentJobId = null;
        int? currentProgress = null;
        DateTimeOffset? currentUpdatedAt = null;
        if (jobsElement.TryGetProperty("current", out JsonElement currentElement)
            && currentElement.ValueKind == JsonValueKind.Object)
        {
            if (currentElement.TryGetProperty("id", out JsonElement currentIdElement)
                && currentIdElement.ValueKind == JsonValueKind.String)
            {
                currentJobId = currentIdElement.GetString();
                if (string.IsNullOrWhiteSpace(currentJobId))
                    currentJobId = null;
            }
            if (currentElement.TryGetProperty("progress", out JsonElement progressElement)
                && progressElement.TryGetInt32(out int progress)
                && progress is >= 0 and <= 100)
            {
                currentProgress = progress;
            }
            if (currentElement.TryGetProperty("updatedAt", out JsonElement updatedAtElement)
                && updatedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    updatedAtElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset updatedAt))
            {
                currentUpdatedAt = updatedAt;
            }
        }
        if (!TryReadOptionalHealthTimestamp(
                jobsElement,
                "lastClaimAt",
                out string lastClaimAtSignature)
            || !TryReadOptionalHealthTimestamp(
                jobsElement,
                "lastProgressAt",
                out _)
            || !TryReadOptionalHealthTimestamp(
                jobsElement,
                "lastTerminalAt",
                out string lastTerminalAtSignature))
        {
            return false;
        }
        string catalogRevisionSignature = "-";
        string queueOrderRevisionSignature = "-";
        long? inventoryRevision = null;
        if (payload.TryGetProperty("store", out JsonElement storeElement))
        {
            if (storeElement.ValueKind != JsonValueKind.Object)
                return false;
            if (storeElement.TryGetProperty(
                    "inventoryRevision",
                    out JsonElement inventoryRevisionElement))
            {
                if (inventoryRevisionElement.ValueKind != JsonValueKind.Null)
                {
                    if (!inventoryRevisionElement.TryGetInt64(
                            out long parsedInventoryRevision)
                        || parsedInventoryRevision
                            is < 0 or > 9_007_199_254_740_991)
                    {
                        return false;
                    }
                    inventoryRevision = parsedInventoryRevision;
                }
            }
            if (storeElement.TryGetProperty(
                    "catalogRevision",
                    out JsonElement catalogRevisionElement))
            {
                if (!catalogRevisionElement.TryGetInt64(
                        out long catalogRevision)
                    || catalogRevision is < 0 or > 9_007_199_254_740_991)
                {
                    return false;
                }
                catalogRevisionSignature = catalogRevision.ToString(
                    CultureInfo.InvariantCulture);
            }
            if (storeElement.TryGetProperty(
                    "queueOrderRevision",
                    out JsonElement queueOrderRevisionElement))
            {
                if (!queueOrderRevisionElement.TryGetInt64(
                        out long queueOrderRevision)
                    || queueOrderRevision is < 0 or > 9_007_199_254_740_991)
                {
                    return false;
                }
                queueOrderRevisionSignature = queueOrderRevision.ToString(
                    CultureInfo.InvariantCulture);
            }
        }

        if (runtimeElement.TryGetProperty("sourceDirty", out JsonElement dirtyElement))
        {
            if (dirtyElement.ValueKind == JsonValueKind.True)
                revision += " · modified";
            else if (dirtyElement.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                return false;
        }
        else
        {
            return false;
        }

        bool? paused = null;
        if (payload.TryGetProperty("worker", out JsonElement workerElement))
        {
            if (workerElement.ValueKind != JsonValueKind.Object)
                return false;
            if (workerElement.TryGetProperty("paused", out JsonElement pausedElement))
            {
                if (pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    paused = pausedElement.GetBoolean();
                else if (pausedElement.ValueKind != JsonValueKind.Null)
                    return false;
            }
        }

        bool queuedPhotorealPromptUpdate = false;
        bool photorealPromptControls = false;
        bool atomicImageEnqueueNext = false;
        bool terminalHistoryBatchDismiss = false;
        bool queuedJobsBatchCancel = false;
        bool queuedJobsBatchReorder = false;
        bool terminalHistoryTargets = false;
        bool terminalHistoryBatchRetry = false;
        MiniMaxH3VideoCapabilityState? miniMaxH3Capability =
            TryParseMiniMaxH3VideoCapability(
                payload,
                out MiniMaxH3VideoCapabilityState parsedMiniMaxH3Capability)
                ? parsedMiniMaxH3Capability
                : null;
        if (payload.TryGetProperty(
                "capabilities",
                out JsonElement capabilitiesElement))
        {
            if (capabilitiesElement.ValueKind != JsonValueKind.Object)
                return false;
            if (capabilitiesElement.TryGetProperty(
                    "queuedPhotorealSettingsUpdateV1",
                    out JsonElement promptUpdateElement))
            {
                if (promptUpdateElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    queuedPhotorealPromptUpdate =
                        promptUpdateElement.GetBoolean();
                }
                else if (promptUpdateElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "terminalHistoryBatchDismissV1",
                    out JsonElement terminalHistoryBatchDismissElement))
            {
                if (terminalHistoryBatchDismissElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    terminalHistoryBatchDismiss =
                        terminalHistoryBatchDismissElement.GetBoolean();
                }
                else if (terminalHistoryBatchDismissElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "queuedJobsBatchCancelV1",
                    out JsonElement queuedJobsBatchCancelElement))
            {
                if (queuedJobsBatchCancelElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    queuedJobsBatchCancel =
                        queuedJobsBatchCancelElement.GetBoolean();
                }
                else if (queuedJobsBatchCancelElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "queuedJobsBatchReorderV1",
                    out JsonElement queuedJobsBatchReorderElement))
            {
                if (queuedJobsBatchReorderElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    queuedJobsBatchReorder =
                        queuedJobsBatchReorderElement.GetBoolean();
                }
                else if (queuedJobsBatchReorderElement.ValueKind
                    != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "terminalHistoryTargetsV1",
                    out JsonElement terminalHistoryTargetsElement))
            {
                if (terminalHistoryTargetsElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    terminalHistoryTargets =
                        terminalHistoryTargetsElement.GetBoolean();
                }
                else if (terminalHistoryTargetsElement.ValueKind
                    != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "terminalHistoryBatchRetryV1",
                    out JsonElement terminalHistoryBatchRetryElement))
            {
                if (terminalHistoryBatchRetryElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    terminalHistoryBatchRetry =
                        terminalHistoryBatchRetryElement.GetBoolean();
                }
                else if (terminalHistoryBatchRetryElement.ValueKind
                    != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "photorealPromptControlsV2",
                    out JsonElement photorealControlsElement))
            {
                if (photorealControlsElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    photorealPromptControls = photorealControlsElement.GetBoolean();
                }
                else if (photorealControlsElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }
            if (capabilitiesElement.TryGetProperty(
                    "atomicImageEnqueueNext",
                    out JsonElement enqueueNextElement))
            {
                if (enqueueNextElement.ValueKind is
                    JsonValueKind.True or JsonValueKind.False)
                {
                    atomicImageEnqueueNext = enqueueNextElement.GetBoolean();
                }
                else if (enqueueNextElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }
        }

        string stateLabel = status switch
        {
            "healthy" => "Healthy",
            "working" => "Working",
            _ => "Needs attention",
        };
        if (status != "needs-attention" && paused == true)
            stateLabel = "Paused";
        bool queueRecoveryRequired = string.Equals(
            firstIssueCode,
            "queued-without-pump",
            StringComparison.Ordinal);
        string? firstIssue = queueRecoveryRequired
            && miniMaxH3Capability is { Ready: false } unavailableMiniMaxH3
                ? DescribeMiniMaxH3QueueUnavailable(unavailableMiniMaxH3.ReasonCode)
                : DescribeEnhancementQueueHealthIssue(firstIssueCode);
        string detail = status == "needs-attention"
            ? firstIssue ?? "Queue attention is required."
            : paused == true && running > 0
                ? $"{running:N0} running now; {queued:N0} queued jobs will remain paused"
            : paused == true && queued > 0
                ? $"{queued:N0} queued; no new job will start"
            : paused == true
                ? "Queue is paused"
            : running == 0 && queued == 0
                ? "Queue is idle"
                : $"{running:N0} running / {queued:N0} queued";
        string foregroundResource = status switch
        {
            "healthy" => "Success",
            "working" => "AccentLight",
            _ => "Warning",
        };
        // Progress is applied directly from the compact health payload. Claim,
        // terminal, and companion-process identity belong in the inventory
        // signature because they catch a same-count replacement or a companion
        // restart without restoring full polling on each progress tick.
        string inventorySignature = FormattableString.Invariant(
            $"{queued}|{running}|{succeeded}|{failed}|{canceled}|{deleted}|{currentJobId ?? "-"}|{lastClaimAtSignature}|{lastTerminalAtSignature}|{catalogRevisionSignature}|{queueOrderRevisionSignature}|{serverStartedAtSignature}|{processId}|{buildIdSignature}");
        health = new EnhancementQueueHealthView(
            stateLabel,
            detail,
            revision,
            foregroundResource,
            paused,
            queueRecoveryRequired,
            queuedPhotorealPromptUpdate,
            photorealPromptControls && atomicImageEnqueueNext,
            terminalHistoryBatchDismiss,
            queuedJobsBatchCancel,
            queuedJobsBatchReorder,
            terminalHistoryTargets,
            terminalHistoryBatchRetry,
            inventorySignature,
            inventoryRevision,
            currentJobId,
            currentProgress,
            currentUpdatedAt);
        return true;
    }

    private static bool TryReadNonNegativeCount(
        JsonElement counts,
        string propertyName,
        out int value)
    {
        value = 0;
        return counts.TryGetProperty(propertyName, out JsonElement countElement)
            && countElement.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryReadOptionalHealthTimestamp(
        JsonElement parent,
        string propertyName,
        out string signature)
    {
        signature = "-";
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        signature = parsed.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture);
        return true;
    }

    private static string DescribeEnhancementQueueHealthIssue(string? issue)
        => issue switch
        {
            "multiple-running-jobs" => "More than one job is marked running.",
            "running-without-worker-identity" => "The running job has no worker identity.",
            "running-without-local-pump" => "A job is running without this process's queue pump.",
            "queued-without-pump" => "Queued work is waiting without a queue pump.",
            "worker-loop-failing" => "The worker loop reported a failure.",
            "non-loopback-server" => "The local companion is not loopback-only.",
            "non-loopback-comfyui" => "ComfyUI is not loopback-only.",
            _ => "Queue attention is required.",
        };

    private static string DescribeMiniMaxH3QueueUnavailable(string? reasonCode)
        => reasonCode switch
        {
            "MINIMAX_H3_RUNTIME_SEAL_INVALID" =>
                "MiniMax H3 sealed runtime is not mounted. Resume the queue to restore it.",
            "MINIMAX_H3_RUNTIME_MANIFEST_INVALID" =>
                "MiniMax H3 runtime verification failed. Restore the configured runtime, then resume.",
            "MINIMAX_H3_LICENSE_NOT_ACCEPTED" =>
                "MiniMax H3 license acceptance is not verified.",
            "MINIMAX_H3_MODELS_UNVERIFIED" =>
                "MiniMax H3 model verification failed.",
            "MINIMAX_H3_WORKFLOW_UNVERIFIED" =>
                "MiniMax H3 workflow verification failed.",
            "MINIMAX_H3_GPU_CANARY_UNVERIFIED" =>
                "MiniMax H3 GPU verification is incomplete.",
            "MINIMAX_H3_BACKEND_CONFIG_INVALID" =>
                "MiniMax H3 backend configuration is invalid.",
            "MINIMAX_H3_WRITER_DISABLED" =>
                "MiniMax H3 processing is disabled in the local companion.",
            _ => "MiniMax H3 is unavailable. Restore it, then resume the queue.",
        };

    private void ApplyEnhancementQueueHealth(EnhancementQueueHealthView health)
    {
        _enhancementWorkspaceHealthInventoryRevisionSupported =
            health.InventoryRevision is not null;
        _enhancementWorkspaceLastHealthInventoryRevision =
            health.InventoryRevision;
        _enhancementWorkspaceQueuePaused = health.Paused;
        _enhancementWorkspaceQueueRecoveryRequired = health.QueueRecoveryRequired;
        ApplyQueuedPhotorealPromptUpdateCapability(
            health.QueuedPhotorealPromptUpdate);
        ApplyPhotorealEnqueueNextCapability(health.PhotorealEnqueueNext);
        _enhancementWorkspaceTerminalHistoryBatchDismissSupported =
            health.TerminalHistoryBatchDismiss;
        _enhancementWorkspaceQueuedJobsBatchCancelSupported =
            health.QueuedJobsBatchCancel;
        _enhancementWorkspaceQueuedJobsBatchReorderSupported =
            health.QueuedJobsBatchReorder;
        _enhancementWorkspaceTerminalHistoryTargetsSupported =
            health.TerminalHistoryTargets
            && health.TerminalHistoryBatchDismiss;
        _enhancementWorkspaceTerminalHistoryBatchRetrySupported =
            health.TerminalHistoryTargets
            && health.TerminalHistoryBatchRetry;
        EnhancementJobsHealthStateText.Text = health.State;
        EnhancementJobsHealthStateText.Foreground =
            (Brush)FindResource(health.ForegroundResource);
        EnhancementJobsHealthDetailText.Text = health.Detail;
        EnhancementJobsHealthRevisionText.Text = health.Revision;
        if (health.CurrentJobId is not null
            && health.CurrentProgress is int currentProgress)
        {
            _enhancementWorkspaceJobs
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    health.CurrentJobId,
                    StringComparison.Ordinal))?
                .ApplyHealthProgress(currentProgress, health.CurrentUpdatedAt);
        }
        RefreshEnhancementQueuePauseControl();
    }

    private void ApplyEnhancementQueueHealthUnavailable(string detail)
    {
        _enhancementWorkspaceHealthInventorySignature = null;
        _enhancementWorkspaceHealthInventoryRevisionSupported = false;
        _enhancementWorkspaceLastHealthInventoryRevision = null;
        _enhancementWorkspaceQueuePaused = null;
        _enhancementWorkspaceQueueRecoveryRequired = false;
        ApplyQueuedPhotorealPromptUpdateCapability(false);
        ApplyPhotorealEnqueueNextCapability(false);
        _enhancementWorkspaceTerminalHistoryBatchDismissSupported = false;
        _enhancementWorkspaceQueuedJobsBatchCancelSupported = false;
        _enhancementWorkspaceQueuedJobsBatchReorderSupported = false;
        _enhancementWorkspaceTerminalHistoryTargetsSupported = false;
        _enhancementWorkspaceTerminalHistoryBatchRetrySupported = false;
        EnhancementJobsHealthStateText.Text = "Health unavailable";
        EnhancementJobsHealthStateText.Foreground =
            (Brush)FindResource("TextTertiary");
        EnhancementJobsHealthDetailText.Text = detail;
        EnhancementJobsHealthRevisionText.Text = "";
        RefreshEnhancementQueuePauseControl();
    }

    private bool UsesDirectEnhancementJobsSqliteReader()
    {
        if (!_usingDefaultModalEnhancementSender)
            return false;
        try
        {
            return IsEnhancementSqliteStore(ResolvedEnhancementJobsPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private string FormatEnhancementJobsInventoryStatus(
        int activeCount,
        int runningCount,
        int queuedCount,
        bool automaticPollingAvailable)
    {
        if (activeCount > 0 && automaticPollingAvailable)
        {
            return $"共有GPUキューを実行順で表示中です。実行中 {runningCount:N0}、待ち {queuedCount:N0}。履歴は最新 {_enhancementJobsHistoryLimit:N0}件まで読みます。";
        }
        if (!automaticPollingAvailable
            && UsesDirectEnhancementJobsSqliteReader())
        {
            return $"ローカルJob履歴を読み取り専用で表示中です。実行中記録 {runningCount:N0}、待ち記録 {queuedCount:N0}。ローカルAIサービスに未接続のため自動更新は停止しています。";
        }
        return $"Updated {DateTime.Now:HH:mm:ss}. Polling is stopped because no jobs are active.";
    }

    private void ApplyQueuedPhotorealPromptUpdateCapability(bool supported)
    {
        _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported = supported;
        foreach (EnhancementWorkspaceJobView job in _enhancementWorkspaceJobs)
            job.QueuedPhotorealPromptUpdateCapabilitySafe = supported;
        RefreshEnhancementQueueBulkControls();
    }

    private void ApplyPhotorealEnqueueNextCapability(bool supported)
    {
        _enhancementWorkspacePhotorealEnqueueNextSupported = supported;
        foreach (EnhancementWorkspaceJobView job in _enhancementWorkspaceJobs)
            job.PhotorealEnqueueNextCapabilitySafe = supported;
    }

    private void RefreshEnhancementQueuePauseControl()
    {
        if (EnhancementJobsPauseResumeButton is null)
            return;

        bool paused = _enhancementWorkspaceQueuePaused == true;
        bool resume = paused || _enhancementWorkspaceQueueRecoveryRequired;
        bool connectToResume =
            _enhancementWorkspaceQueuePaused is null
            && _usingDefaultModalEnhancementSender;
        EnhancementJobsPauseResumeButton.Content = connectToResume
            ? "接続して再開"
            : resume
                ? "再開"
                : "一時停止";
        EnhancementJobsPauseResumeButton.IsEnabled =
            (_enhancementWorkspaceQueuePaused.HasValue || connectToResume)
            && !_enhancementWorkspaceMutationPending
            && !_enhancementWorkspaceRefreshPending;
        AutomationProperties.SetName(
            EnhancementJobsPauseResumeButton,
            connectToResume
                ? "Connect local AI service and resume enhancement queue"
                : resume
                    ? "Resume enhancement queue"
                    : "Pause enhancement queue");
        EnhancementJobsPauseResumeButton.ToolTip = _enhancementWorkspaceQueuePaused.HasValue
            ? resume
                ? _enhancementWorkspaceQueueRecoveryRequired
                    ? "停止したqueue pumpを復旧し、待機順を保ったまま処理を再開します"
                    : "待機順を保ったままキュー処理を再開します"
                : "処理中の1件は完了させ、次の待機ジョブから止めます"
            : connectToResume
                ? "明示操作としてローカルAIサービスを開始し、状態を確認して必要ならキューを再開します"
                : "キュー停止に対応したローカルcompanionが必要です";
    }

    private void ApplyEnhancementWorkspaceHighlights(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        if (_enhancementWorkspaceHighlightExpiresAt <= DateTimeOffset.UtcNow)
        {
            _enhancementWorkspaceHighlightedJobIds.Clear();
            _enhancementWorkspaceHighlightExpiresAt = default;
        }
        foreach (EnhancementWorkspaceJobView job in jobs)
            job.IsHighlighted = _enhancementWorkspaceHighlightedJobIds.Contains(job.Id);
    }

    private void ReconcileEnhancementWorkspaceJobs(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        var existingById = new Dictionary<string, EnhancementWorkspaceJobView>(
            StringComparer.Ordinal);
        foreach (EnhancementWorkspaceJobView existing in _enhancementWorkspaceJobs)
            existingById.TryAdd(existing.Id, existing);
        var reconciled = new List<EnhancementWorkspaceJobView>(jobs.Count);
        foreach (EnhancementWorkspaceJobView candidate in jobs)
        {
            // Health is intentionally sampled before the jobs inventory. Make
            // that already-observed capability state authoritative for rows
            // that appear for the first time in the following inventory.
            candidate.QueuedPhotorealPromptUpdateCapabilitySafe =
                _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported;
            candidate.PhotorealEnqueueNextCapabilitySafe =
                _enhancementWorkspacePhotorealEnqueueNextSupported;
            if (existingById.TryGetValue(candidate.Id, out EnhancementWorkspaceJobView? existing)
                && existing.HasSameImmutableIdentity(candidate))
            {
                existing.RefreshFrom(candidate);
                reconciled.Add(existing);
            }
            else
            {
                reconciled.Add(candidate);
            }
        }

        _enhancementWorkspaceJobs.Clear();
        _enhancementWorkspaceJobs.AddRange(reconciled);
    }

    private static bool SameActiveEnhancementJobIds(
        IEnumerable<EnhancementWorkspaceJobView> current,
        IEnumerable<EnhancementWorkspaceJobView> incoming)
    {
        HashSet<string> activeIds = current
            .Where(static job => job.IsActive)
            .Select(static job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        return activeIds.SetEquals(
            incoming
                .Where(static job => job.IsActive)
                .Select(static job => job.Id));
    }

    private async Task<(bool Parsed, List<EnhancementWorkspaceJobView> Jobs, string? Error)>
        ReadEnhancementWorkspaceInventoryAsync(CancellationToken token)
    {
        if (_usingDefaultModalEnhancementSender)
        {
            string jobsPath;
            try
            {
                jobsPath = ResolvedEnhancementJobsPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return (
                    false,
                    [],
                    "The local Jobs database path could not be resolved safely.");
            }

            if (IsEnhancementSqliteStore(jobsPath))
                return await ReadEnhancementJobsWorkspaceSqliteAsync(
                    jobsPath,
                    token);
        }

        EnhancementApiResponse response = await SendPassiveEnhancementReadAsync(
            "api/enhance/jobs",
            token);
        if (!response.Ok || response.Payload is not JsonElement payload)
        {
            return (
                false,
                [],
                string.IsNullOrWhiteSpace(response.Error)
                    ? "Jobs could not be refreshed."
                    : response.Error.Trim());
        }
        (bool parsed, List<EnhancementWorkspaceJobView> jobs, string? error) =
            await ParseEnhancementWorkspaceJobsAsync(payload, token);
        if (parsed)
        {
            _enhancementWorkspaceTotalCounts =
                CountEnhancementWorkspaceStatuses(jobs);
        }
        return (parsed, jobs, error);
    }

    private static EnhancementWorkspaceStatusCounts
        CountEnhancementWorkspaceStatuses(
            IEnumerable<EnhancementWorkspaceJobView> jobs)
    {
        int queued = 0;
        int running = 0;
        int succeeded = 0;
        int failed = 0;
        int canceled = 0;
        int deleted = 0;
        foreach (EnhancementWorkspaceJobView job in jobs)
        {
            switch (job.Status)
            {
                case "queued":
                    queued++;
                    break;
                case "running":
                    running++;
                    break;
                case "succeeded":
                    succeeded++;
                    break;
                case "failed":
                    failed++;
                    break;
                case "canceled":
                    canceled++;
                    break;
                case "deleted":
                    deleted++;
                    break;
            }
        }
        return new EnhancementWorkspaceStatusCounts(
            queued,
            running,
            succeeded,
            failed,
            canceled,
            deleted);
    }

    private static EnhancementWorkspaceSqliteSnapshot
        ReadEnhancementJobsWorkspaceSqliteSnapshot(
            string path,
            int terminalHistoryLimit,
            CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (NormalizeEnhancementJobsHistoryLimit(terminalHistoryLimit)
            != terminalHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalHistoryLimit));
        }
        string fullPath = Path.GetFullPath(path);
        if (!IsEnhancementSqliteStore(fullPath))
        {
            throw new InvalidDataException(
                "The Jobs workspace direct reader requires a SQLite store.");
        }

        using SqliteConnection connection = OpenEnhancementSqliteReadConnection(fullPath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        _ = ReadEnhancementCatalogRevision(connection, transaction);
        EnhancementWorkspaceStatusCounts counts =
            ReadEnhancementWorkspaceStatusCounts(connection, transaction, token);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH recent_terminal AS (
                SELECT position, id, status, reader_payload_json
                FROM enhancement_jobs
                WHERE status IN ('succeeded', 'failed', 'canceled', 'deleted')
                ORDER BY updated_at DESC, position DESC
                LIMIT $terminalHistoryLimit
            ),
            visible_jobs AS (
                SELECT position, id, status, reader_payload_json
                FROM enhancement_jobs
                WHERE status IN ('queued', 'running')
                UNION ALL
                SELECT position, id, status, reader_payload_json
                FROM recent_terminal
            )
            SELECT position, id, status, reader_payload_json
            FROM visible_jobs
            ORDER BY position ASC
            """;
        command.Parameters.AddWithValue(
            "$terminalHistoryLimit",
            terminalHistoryLimit);
        using SqliteDataReader reader = command.ExecuteReader();
        var jobs = new List<EnhancementWorkspaceJobView>(
            Math.Min(counts.Total, counts.Active + terminalHistoryLimit));
        var jobIds = new HashSet<string>(StringComparer.Ordinal);
        long? previousPosition = null;
        long totalPayloadBytes = 0;
        int apiOrdinal = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (apiOrdinal >= EnhancementJobsWorkspaceMaximumRows)
            {
                throw new InvalidDataException(
                    "The Jobs workspace SQLite inventory exceeds the safe row limit.");
            }

            long position = ReadRequiredSqliteInteger(reader, 0, "job position");
            if (position < 0
                || previousPosition.HasValue && position <= previousPosition.Value)
            {
                throw new InvalidDataException(
                    "Enhancement SQLite job positions must be unique, non-negative, and strictly increasing.");
            }
            previousPosition = position;

            string id = ReadRequiredSqliteText(reader, 1, "job id");
            if (string.IsNullOrWhiteSpace(id) || !jobIds.Add(id))
            {
                throw new InvalidDataException(
                    "Enhancement SQLite job ids must be non-empty and unique.");
            }

            string status = ReadRequiredSqliteText(reader, 2, "job status");
            if (status is not ("queued" or "running" or "succeeded" or "failed" or "canceled" or "deleted"))
            {
                throw new InvalidDataException(
                    $"Enhancement SQLite job {id} has an unsupported Jobs workspace status.");
            }

            string payloadText = ReadRequiredSqliteText(
                reader,
                3,
                "Jobs workspace projection");
            int payloadBytes = Encoding.UTF8.GetByteCount(payloadText);
            if (payloadBytes > EnhancementJobsWorkspaceMaximumPayloadBytesPerRow)
            {
                throw new InvalidDataException(
                    $"Enhancement SQLite job {id} exceeds the safe per-job payload limit.");
            }
            if (totalPayloadBytes
                > EnhancementJobsWorkspaceMaximumTotalPayloadBytes - payloadBytes)
            {
                throw new InvalidDataException(
                    "The Jobs workspace SQLite inventory exceeds the safe total payload limit.");
            }
            totalPayloadBytes += payloadBytes;

            JsonDocument payload;
            try
            {
                payload = JsonDocument.Parse(payloadText);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Enhancement SQLite job {id} has malformed Jobs workspace projection JSON.",
                    ex);
            }

            using (payload)
            {
                JsonElement root = payload.RootElement;
                bool idMatches = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("id", out JsonElement payloadId)
                    && payloadId.ValueKind == JsonValueKind.String
                    && string.Equals(payloadId.GetString(), id, StringComparison.Ordinal);
                bool statusMatches = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("status", out JsonElement payloadStatus)
                    && payloadStatus.ValueKind == JsonValueKind.String
                    && string.Equals(
                        payloadStatus.GetString(),
                        status,
                        StringComparison.Ordinal);
                if (!idMatches || !statusMatches)
                {
                    throw new InvalidDataException(
                        $"Enhancement SQLite job {id} Jobs workspace projection does not match its row.");
                }
                if (!HasRequiredEnhancementWorkspaceProjectionIdentity(root))
                {
                    throw new InvalidDataException(
                        $"Enhancement SQLite job {id} is missing its Jobs workspace identity projection.");
                }

                EnhancementWorkspaceJobView? job = ParseEnhancementWorkspaceJob(
                    root,
                    apiOrdinal,
                    buildRequestDetails: false);
                if (job is null)
                {
                    throw new InvalidDataException(
                        $"Enhancement SQLite job {id} is not a valid Jobs workspace row.");
                }
                if (root.TryGetProperty("operation", out JsonElement declaredOperation)
                    && declaredOperation.ValueKind == JsonValueKind.String
                    && declaredOperation.GetString() is
                        "upscale" or "photoreal" or "i2i" or "video"
                    && !string.Equals(
                        declaredOperation.GetString(),
                        job.Operation,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Enhancement SQLite job {id} has an invalid operation projection.");
                }
                jobs.Add(job);
            }
            apiOrdinal++;
        }

        reader.Close();
        transaction.Commit();
        return new EnhancementWorkspaceSqliteSnapshot(jobs, counts);
    }

    private static EnhancementWorkspaceStatusCounts
        ReadEnhancementWorkspaceStatusCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status
            FROM enhancement_jobs
            LIMIT $maximumRowsPlusOne
            """;
        command.Parameters.AddWithValue(
            "$maximumRowsPlusOne",
            checked(EnhancementJobsWorkspaceMaximumRows + 1));
        using SqliteDataReader reader = command.ExecuteReader();
        int queued = 0;
        int running = 0;
        int succeeded = 0;
        int failed = 0;
        int canceled = 0;
        int deleted = 0;
        long total = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (total >= EnhancementJobsWorkspaceMaximumRows)
            {
                throw new InvalidDataException(
                    "The Jobs workspace SQLite inventory exceeds the safe row limit.");
            }
            string status = ReadRequiredSqliteText(reader, 0, "job status");
            switch (status)
            {
                case "queued":
                    queued++;
                    break;
                case "running":
                    running++;
                    break;
                case "succeeded":
                    succeeded++;
                    break;
                case "failed":
                    failed++;
                    break;
                case "canceled":
                    canceled++;
                    break;
                case "deleted":
                    deleted++;
                    break;
                default:
                    throw new InvalidDataException(
                        "The Jobs workspace SQLite inventory contains an unsupported status.");
            }
            total++;
        }
        return new EnhancementWorkspaceStatusCounts(
            queued,
            running,
            succeeded,
            failed,
            canceled,
            deleted);
    }

    private static bool HasRequiredEnhancementWorkspaceProjectionIdentity(
        JsonElement root)
    {
        if (!TryGetStringProperty(root, "sourceId", out _)
            || !TryGetStringProperty(root, "sourcePath", out _)
            || !TryGetStringProperty(root, "presetId", out _)
            || !TryGetStringProperty(root, "adapterId", out _)
            || !TryGetStringProperty(root, "createdAt", out string? createdAtText)
            || !TryGetStringProperty(root, "updatedAt", out string? updatedAtText)
            || !DateTimeOffset.TryParse(
                createdAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _)
            || !DateTimeOffset.TryParse(
                updatedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            return false;
        }

        return !root.TryGetProperty("operation", out JsonElement operation)
            || operation.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(operation.GetString());
    }

    private async Task<(bool Parsed, List<EnhancementWorkspaceJobView> Jobs, string? Error)>
        ReadEnhancementJobsWorkspaceSqliteAsync(
            string path,
            CancellationToken token = default)
    {
        var operationWatch = Stopwatch.StartNew();
        bool gateEntered = false;
        try
        {
            await _enhancementWorkspaceSqliteReadGate.WaitAsync(token);
            gateEntered = true;
            EnhancementWorkspaceSqliteSnapshot snapshot = await Task.Run(
                () => ReadEnhancementJobsWorkspaceSqliteSnapshot(
                    path,
                    _enhancementJobsHistoryLimit,
                    token),
                token);
            await Task.Run(
                () => FinalizeEnhancementWorkspaceJobs(snapshot.Jobs),
                token);
            _enhancementWorkspaceTotalCounts = snapshot.Counts;
            AibosOperationLog.Write(
                "jobs_sqlite_read",
                "completed",
                operationWatch.ElapsedMilliseconds,
                mode: "workspace",
                itemCount: snapshot.Jobs.Count);
            return (true, snapshot.Jobs, null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidDataException
            or JsonException
            or SqliteException)
        {
            AibosOperationLog.Write(
                "jobs_sqlite_read",
                "failed",
                operationWatch.ElapsedMilliseconds,
                errorCode: ex.GetType().Name,
                mode: "workspace");
            return (
                false,
                [],
                "Jobs could not be read safely from the local queue database.");
        }
        finally
        {
            if (gateEntered)
                _enhancementWorkspaceSqliteReadGate.Release();
        }
    }

    private static string ReadEnhancementJobRequestDetailsSqliteSnapshot(
        string path,
        EnhancementWorkspaceJobDetailIdentity expected,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        if (!IsEnhancementSqliteStore(fullPath))
        {
            throw new InvalidDataException(
                "Job details require a SQLite Jobs store.");
        }

        using SqliteConnection connection = OpenEnhancementSqliteReadConnection(fullPath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        _ = ReadEnhancementCatalogRevision(connection, transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, status, payload_json
            FROM enhancement_jobs
            WHERE id = $id
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException(
                "The selected Job no longer exists in the local queue database.");
        }

        token.ThrowIfCancellationRequested();
        string rowId = ReadRequiredSqliteText(reader, 0, "job id");
        string rowStatus = ReadRequiredSqliteText(reader, 1, "job status");
        string payloadText = ReadRequiredSqliteText(reader, 2, "job detail payload");
        if (reader.Read())
        {
            throw new InvalidDataException(
                "The selected Job id is not unique in the local queue database.");
        }
        reader.Close();

        if (!string.Equals(rowId, expected.Id, StringComparison.Ordinal)
            || !string.Equals(rowStatus, expected.Status, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected Job changed while its details were being read.");
        }
        int payloadBytes = Encoding.UTF8.GetByteCount(payloadText);
        if (payloadBytes > EnhancementJobsWorkspaceMaximumPayloadBytesPerRow)
        {
            throw new InvalidDataException(
                "The selected Job detail exceeds the safe per-job payload limit.");
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(payloadText);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The selected Job detail payload is malformed.",
                ex);
        }

        using (payload)
        {
            EnhancementWorkspaceJobView? detailed = ParseEnhancementWorkspaceJob(
                payload.RootElement,
                expected.ApiOrdinal);
            if (detailed is null
                || !string.Equals(detailed.Id, expected.Id, StringComparison.Ordinal)
                || !string.Equals(detailed.Status, expected.Status, StringComparison.Ordinal)
                || !string.Equals(detailed.Operation, expected.Operation, StringComparison.Ordinal)
                || !string.Equals(detailed.SourceId, expected.SourceId, StringComparison.Ordinal)
                || !string.Equals(detailed.SourcePath, expected.SourcePath, StringComparison.Ordinal)
                || !string.Equals(detailed.PresetId, expected.PresetId, StringComparison.Ordinal)
                || !string.Equals(detailed.AdapterId, expected.AdapterId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The selected Job detail does not match the displayed Jobs row.");
            }

            transaction.Commit();
            return detailed.RequestDetailsText;
        }
    }

    private async Task<string?> ReadEnhancementJobRequestDetailsSqliteAsync(
        string path,
        EnhancementWorkspaceJobDetailIdentity expected,
        CancellationToken token)
    {
        var operationWatch = Stopwatch.StartNew();
        bool gateEntered = false;
        try
        {
            await _enhancementWorkspaceSqliteReadGate.WaitAsync(token);
            gateEntered = true;
            string details = await Task.Run(
                () => ReadEnhancementJobRequestDetailsSqliteSnapshot(
                    path,
                    expected,
                    token),
                token);
            AibosOperationLog.Write(
                "jobs_sqlite_detail_read",
                "completed",
                operationWatch.ElapsedMilliseconds,
                mode: expected.Operation,
                itemCount: 1);
            return details;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidDataException
            or JsonException
            or SqliteException)
        {
            AibosOperationLog.Write(
                "jobs_sqlite_detail_read",
                "failed",
                operationWatch.ElapsedMilliseconds,
                errorCode: ex.GetType().Name,
                mode: expected.Operation);
            return null;
        }
        finally
        {
            if (gateEntered)
                _enhancementWorkspaceSqliteReadGate.Release();
        }
    }

    private async Task<bool> EnsureEnhancementJobRequestDetailsLoadedAsync(
        EnhancementWorkspaceJobView job,
        bool forceReload = false,
        string? jobsPathOverride = null,
        CancellationToken token = default)
    {
        if (!forceReload && job.RequestDetailsLoaded)
            return true;
        if (!_usingDefaultModalEnhancementSender && jobsPathOverride is null)
            return job.RequestDetailsLoaded;

        string jobsPath;
        try
        {
            jobsPath = jobsPathOverride is null
                ? ResolvedEnhancementJobsPath
                : Path.GetFullPath(jobsPathOverride);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
        if (!IsEnhancementSqliteStore(jobsPath))
            return job.RequestDetailsLoaded;

        var expected = new EnhancementWorkspaceJobDetailIdentity(
            job.Id,
            job.Status,
            job.Operation,
            job.SourceId,
            job.SourcePath,
            job.PresetId,
            job.AdapterId,
            job.ApiOrdinal);
        string? details = await ReadEnhancementJobRequestDetailsSqliteAsync(
            jobsPath,
            expected,
            token);
        if (details is null)
            return false;
        job.ApplyRequestDetails(details);
        return true;
    }

    internal async Task<(bool ReadOk, int JobCount, bool DetailsLazyLoaded, bool FullPayloadPreserved)>
        ReadEnhancementJobsWorkspaceSqliteForSmokeAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        (bool parsed, List<EnhancementWorkspaceJobView> jobs, string? _) =
            string.Equals(
                fullPath,
                ResolvedEnhancementJobsPath,
                StringComparison.OrdinalIgnoreCase)
                ? await ReadEnhancementWorkspaceInventoryAsync(CancellationToken.None)
                : await ReadEnhancementJobsWorkspaceSqliteAsync(fullPath);
        EnhancementWorkspaceJobView? target = jobs.FirstOrDefault(static job =>
            job.Id == "enhanced-ok");
        bool detailsStartedLazy = target is { RequestDetailsLoaded: false }
            && string.IsNullOrEmpty(target.RequestDetailsText);
        bool detailRead = target is not null
            && await EnsureEnhancementJobRequestDetailsLoadedAsync(
                target,
                jobsPathOverride: path);
        bool detailsLazyLoaded = detailsStartedLazy
            && detailRead
            && target is { RequestDetailsLoaded: true };
        bool fullPayloadPreserved = detailsLazyLoaded
            && target!.RequestDetailsText.Contains(
                "sqlite workspace full payload prompt",
                StringComparison.Ordinal)
            && target.RequestDetailsText.Contains(
                "sqlite workspace full payload negative",
                StringComparison.Ordinal)
            && target.RequestDetailsText.Contains(
                "CFG: 1.4",
                StringComparison.Ordinal)
            && !target.RequestDetailsText.Contains(
                "must-not-be-displayed",
                StringComparison.Ordinal);
        return (parsed, jobs.Count, detailsLazyLoaded, fullPayloadPreserved);
    }

    internal async Task<(bool ReadOk, int JobCount, bool DetailsLazyLoaded, bool OversizedDetailRejected)>
        ReadLargeEnhancementJobsWorkspaceSqliteForSmokeAsync(
            string path,
            string targetId,
            string detailMarker,
            string oversizedTargetId)
    {
        (bool parsed, List<EnhancementWorkspaceJobView> jobs, string? _) =
            await ReadEnhancementJobsWorkspaceSqliteAsync(Path.GetFullPath(path));
        bool allDetailsStartedLazy = jobs.All(static job =>
            !job.RequestDetailsLoaded
            && string.IsNullOrEmpty(job.RequestDetailsText));
        EnhancementWorkspaceJobView? target = jobs.FirstOrDefault(job =>
            string.Equals(job.Id, targetId, StringComparison.Ordinal));
        bool detailRead = target is not null
            && await EnsureEnhancementJobRequestDetailsLoadedAsync(
                target,
                jobsPathOverride: path);
        bool detailsLazyLoaded = allDetailsStartedLazy
            && detailRead
            && target is { RequestDetailsLoaded: true }
            && target.RequestDetailsText.Contains(
                detailMarker,
                StringComparison.Ordinal);
        EnhancementWorkspaceJobView? oversized = jobs.FirstOrDefault(job =>
            string.Equals(job.Id, oversizedTargetId, StringComparison.Ordinal));
        bool oversizedDetailRejected = oversized is { RequestDetailsLoaded: false }
            && !await EnsureEnhancementJobRequestDetailsLoadedAsync(
                oversized,
                jobsPathOverride: path)
            && !oversized.RequestDetailsLoaded
            && string.IsNullOrEmpty(oversized.RequestDetailsText);
        return (parsed, jobs.Count, detailsLazyLoaded, oversizedDetailRejected);
    }

    internal static void ValidateEnhancementJobsWorkspaceSqliteForSmoke(
        string path)
        => _ = ReadEnhancementJobsWorkspaceSqliteSnapshot(
            Path.GetFullPath(path),
            EnhancementJobsDefaultHistoryLimit,
            CancellationToken.None);

    internal static EnhancementJobsHistoryWindowSmokeSnapshot
        ReadEnhancementJobsHistoryWindowSqliteForSmoke(
            string path,
            int terminalHistoryLimit)
    {
        EnhancementWorkspaceSqliteSnapshot snapshot =
            ReadEnhancementJobsWorkspaceSqliteSnapshot(
                Path.GetFullPath(path),
                terminalHistoryLimit,
                CancellationToken.None);
        EnhancementWorkspaceJobView[] terminal = snapshot.Jobs
            .Where(static job => !job.IsActive)
            .ToArray();
        return new EnhancementJobsHistoryWindowSmokeSnapshot(
            terminalHistoryLimit,
            snapshot.Jobs.Count,
            snapshot.Counts.Total,
            snapshot.Counts.Active,
            terminal.Length,
            terminal.FirstOrDefault()?.Id,
            terminal.LastOrDefault()?.Id);
    }

    internal static bool TryReadEnhancementOutputDependencyForSmoke(
        JsonElement payload,
        string outputJobId,
        out bool dependencyProtected,
        out bool canDeleteOutput)
    {
        dependencyProtected = false;
        canDeleteOutput = false;
        if (!TryParseEnhancementWorkspaceJobs(
                payload,
                out List<EnhancementWorkspaceJobView> jobs,
                out _,
                element => TryGetStringProperty(
                        element,
                        "id",
                        out string? candidateId)
                    && string.Equals(
                        candidateId,
                        outputJobId,
                        StringComparison.Ordinal)))
        {
            return false;
        }

        EnhancementWorkspaceJobView? outputJob = jobs.FirstOrDefault(job =>
            string.Equals(job.Id, outputJobId, StringComparison.Ordinal));
        if (outputJob is null)
            return false;
        dependencyProtected = outputJob.OutputDependencyProtected;
        canDeleteOutput = outputJob.CanDeleteOutput;
        return true;
    }

    private async Task<(bool Parsed, List<EnhancementWorkspaceJobView> Jobs, string? Error)>
        ParseEnhancementWorkspaceJobsAsync(
            JsonElement payload,
            CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // API fallback and direct SQLite reads intentionally share the same
        // passive-read contract: parse and hash bounded JSON in the
        // background, but never resolve or open user media just to show Jobs.
        return await Task.Run(() =>
        {
            bool parsed = TryParseEnhancementWorkspaceJobs(
                payload,
                out List<EnhancementWorkspaceJobView> jobs,
                out string? error,
                videoMutationValidator: null,
                token);
            return (parsed, jobs, error);
        }, token);
    }

    private static bool TryParseEnhancementWorkspaceJobs(
        JsonElement payload,
        out List<EnhancementWorkspaceJobView> jobs,
        out string? error,
        Func<JsonElement, bool>? videoMutationValidator = null,
        CancellationToken token = default)
    {
        jobs = [];
        error = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("jobs", out JsonElement jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Array)
        {
            error = "The companion response does not contain a jobs array.";
            return false;
        }

        int apiOrdinal = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement element in jobsElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            EnhancementWorkspaceJobView? job = ParseEnhancementWorkspaceJob(
                element,
                apiOrdinal++,
                videoMutationValidator);
            if (job is null)
            {
                jobs.Clear();
                error = "The companion returned an invalid jobs row. The last valid snapshot was preserved.";
                return false;
            }
            if (!ids.Add(job.Id))
            {
                jobs.Clear();
                error = "The companion returned duplicate job identifiers. The last valid snapshot was preserved.";
                return false;
            }
            jobs.Add(job);
        }

        FinalizeEnhancementWorkspaceJobs(jobs);
        return true;
    }

    private static void FinalizeEnhancementWorkspaceJobs(
        List<EnhancementWorkspaceJobView> jobs)
    {
        HashSet<string> protectedProducerJobIds = jobs
            .Where(static job => job.Operation is "i2i" or "video"
                && job.IsActive
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId))
            .Select(static job => job.SourceProducerJobId!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> protectedVideoProducerJobIds = jobs
            .Where(static job => job.IsActive
                && !string.IsNullOrWhiteSpace(job.SourceVideoJobId))
            .Select(static job => job.SourceVideoJobId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> protectedManagedSourcePaths = jobs
            .Where(static job => job.Operation == "video"
                && job.IsActive
                && !string.IsNullOrWhiteSpace(job.SourcePath))
            .Select(static job => NormalizeEnhancementDependencyPath(job.SourcePath))
            .Where(static path => path is not null)
            .Select(static path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (EnhancementWorkspaceJobView job in jobs)
        {
            string? outputPath = NormalizeEnhancementDependencyPath(
                job.OutputPath);
            job.OutputDependencyProtected =
                job.IsImageOperation
                    && (protectedProducerJobIds.Contains(job.Id)
                        || outputPath is not null
                            && protectedManagedSourcePaths.Contains(outputPath))
                || job.IsVideoOperation
                    && protectedVideoProducerJobIds.Contains(job.Id);
        }

        AssignEnhancementWorkspaceQueuePositions(jobs);
        jobs.Sort(CompareEnhancementWorkspaceInventory);
    }

    private static string? NormalizeEnhancementDependencyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string trimmed = path.Trim();
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or IOException
                or System.Security.SecurityException)
        {
            // Preserve an exact malformed lexical dependency as a conservative
            // comparison key without probing or rewriting the untrusted path.
            return trimmed;
        }
    }

    private static void AssignEnhancementWorkspaceQueuePositions(
        IReadOnlyCollection<EnhancementWorkspaceJobView> jobs)
    {
        int position = 1;
        EnhancementWorkspaceJobView[] queued = jobs
            .Where(static candidate => candidate.Status == "queued")
            .OrderBy(static candidate => candidate.QueueOrder ?? int.MaxValue)
            .ThenBy(static candidate => candidate.CreatedAt)
            .ThenBy(static candidate => candidate.ApiOrdinal)
            .ToArray();
        bool queueMutationScopeSafe =
            queued.All(static candidate => candidate.QueueReorderSafe);
        foreach (EnhancementWorkspaceJobView job in queued)
        {
            job.QueuePosition = position++;
            job.QueueCount = queued.Length;
            job.QueueMutationScopeSafe = queueMutationScopeSafe;
        }
    }

    private static int CompareEnhancementWorkspaceInventory(
        EnhancementWorkspaceJobView left,
        EnhancementWorkspaceJobView right)
    {
        if (left.IsActive != right.IsActive)
            return left.IsActive ? -1 : 1;

        if (left.IsActive)
        {
            if (left.Status != right.Status)
                return left.Status == "running" ? -1 : 1;
            if (left.Status == "queued")
            {
                int position = (left.QueuePosition ?? int.MaxValue)
                    .CompareTo(right.QueuePosition ?? int.MaxValue);
                if (position != 0)
                    return position;
            }
            int created = left.CreatedAt.CompareTo(right.CreatedAt);
            return created != 0 ? created : left.ApiOrdinal.CompareTo(right.ApiOrdinal);
        }

        int updated = right.UpdatedAt.CompareTo(left.UpdatedAt);
        return updated != 0 ? updated : left.ApiOrdinal.CompareTo(right.ApiOrdinal);
    }

    private static EnhancementWorkspaceJobView? ParseEnhancementWorkspaceJob(
        JsonElement element,
        int apiOrdinal,
        Func<JsonElement, bool>? videoMutationValidator = null,
        bool buildRequestDetails = true)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(element, "id", out string? id)
            || !TryGetStringProperty(element, "status", out string? rawStatus))
        {
            return null;
        }

        string status = rawStatus!.Trim().ToLowerInvariant();
        if (status is not ("queued" or "running" or "succeeded" or "failed" or "canceled" or "deleted"))
            return null;

        TryGetStringProperty(element, "sourceId", out string? sourceId);
        TryGetStringProperty(element, "sourcePath", out string? sourcePath);
        TryGetStringProperty(
            element,
            "sourceProducerJobId",
            out string? sourceProducerJobId);
        TryGetStringProperty(
            element,
            "sourceVideoJobId",
            out string? sourceVideoJobId);
        if (sourceVideoJobId is not null
            && !IsSafeVideoToolsJobId(sourceVideoJobId))
        {
            sourceVideoJobId = null;
        }
        TryGetStringProperty(element, "presetId", out string? presetId);
        TryGetStringProperty(element, "adapterId", out string? adapterId);
        TryGetStringProperty(element, "outputPath", out string? outputPath);
        TryGetStringProperty(element, "errorMessage", out string? errorMessage);
        TryGetStringProperty(element, "createdAt", out string? createdAtText);
        TryGetStringProperty(element, "updatedAt", out string? updatedAtText);
        DateTimeOffset? startedAt = TryReadEnhancementJobTimestamp(
            element,
            "startedAt");
        DateTimeOffset? finishedAt = TryReadEnhancementJobTimestamp(
            element,
            "finishedAt");
        int progress = element.TryGetProperty("progress", out JsonElement progressElement)
            && progressElement.TryGetInt32(out int parsedProgress)
            ? Math.Clamp(parsedProgress, 0, 100)
            : 0;
        bool cancelRequested =
            element.TryGetProperty(
                "cancelRequested",
                out JsonElement cancelRequestedElement)
            && cancelRequestedElement.ValueKind == JsonValueKind.True;
        int? queueOrder = element.TryGetProperty("queueOrder", out JsonElement queueOrderElement)
            && queueOrderElement.ValueKind == JsonValueKind.Number
            && queueOrderElement.TryGetInt32(out int parsedQueueOrder)
            && parsedQueueOrder >= 0
            ? parsedQueueOrder
            : null;
        long? sourceSize = null;
        double? sourceMtimeMs = null;
        if (element.TryGetProperty("sourceSignature", out JsonElement signature)
            && signature.ValueKind == JsonValueKind.Object)
        {
            if (signature.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize))
                sourceSize = parsedSize;
            if (signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement) && mtimeElement.TryGetDouble(out double parsedMtime))
                sourceMtimeMs = parsedMtime;
        }

        DateTimeOffset.TryParse(createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset createdAt);
        DateTimeOffset.TryParse(updatedAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset updatedAt);
        if (updatedAt == default)
            updatedAt = createdAt == default ? DateTimeOffset.MinValue : createdAt;

        string operation = ReadEnhancementOperation(element);
        bool i2iV2EnvelopeClaimed = ClaimsI2iV2Envelope(element);
        I2iV2JobInfo? i2iV2Info = operation == "i2i"
            && TryReadI2iV2JobInfo(element, out I2iV2JobInfo parsedI2iV2)
                ? parsedI2iV2
                : null;
        I2iV3JobInfo? i2iV3Info = operation == "i2i"
            && TryReadI2iV3JobInfo(element, out I2iV3JobInfo parsedI2iV3)
                ? parsedI2iV3
                : null;
        bool i2iMutationSafe = operation == "i2i"
            && (IsI2iMutationSafe(element)
                || i2iV2Info is not null
                || i2iV3Info is not null);
        bool structurallySafeVideo = operation == "video"
            && IsStructurallyVideoMutationSafe(element);
        bool videoTrimEnvelopeClaimed =
            ClaimsVideoTrimV1WorkspaceSnapshot(element);
        VideoTrimV1ReaderSnapshot? videoTrimV1Snapshot =
            videoTrimEnvelopeClaimed
            && TryReadVideoTrimV1WorkspaceSnapshot(
                element,
                out VideoTrimV1ReaderSnapshot parsedVideoTrimV1Snapshot)
                ? parsedVideoTrimV1Snapshot
                : null;
        bool videoMutationSafe = operation == "video"
            && !videoTrimEnvelopeClaimed
            && (videoMutationValidator?.Invoke(element)
                ?? structurallySafeVideo);
        // The snapshot discriminator owns fail-closed protection even when
        // the surrounding operation field is missing or malformed. Exact
        // presentation still requires operation=video in the reader below.
        bool videoToolsEnvelopeClaimed =
            ClaimsVideoToolsWorkspaceSnapshot(element);
        VideoToolsV2ReaderSnapshot? videoToolsV2Snapshot =
            videoToolsEnvelopeClaimed
            && TryReadVideoToolsV2WorkspaceSnapshot(
                element,
                out VideoToolsV2ReaderSnapshot parsedVideoToolsV2Snapshot)
                ? parsedVideoToolsV2Snapshot
                : null;
        VideoToolsWorkspaceSnapshot? videoToolsSnapshot =
            videoToolsV2Snapshot is null
            && videoToolsEnvelopeClaimed
            && TryReadVideoToolsWorkspaceSnapshot(
                element,
                out VideoToolsWorkspaceSnapshot parsedVideoToolsSnapshot)
                ? parsedVideoToolsSnapshot
                : null;
        MiniMaxH3VideoWorkspaceSnapshot? miniMaxH3VideoSnapshot =
            operation == "video"
            && !videoTrimEnvelopeClaimed
            && TryReadMiniMaxH3VideoWorkspaceSnapshot(
                element,
                out MiniMaxH3VideoWorkspaceSnapshot? parsedVideoSnapshot)
                ? parsedVideoSnapshot
                : null;
        EnhancementVideoMutationProbe? videoMutationProbe =
            structurallySafeVideo
            && TryReadEnhancementVideoMutationProbe(
                element,
                out EnhancementVideoMutationProbe? parsedVideoMutationProbe)
                ? parsedVideoMutationProbe
                : null;
        bool queueReorderSafe = videoToolsV2Snapshot is not null
            || videoTrimV1Snapshot is not null
            || !videoToolsEnvelopeClaimed
                && !videoTrimEnvelopeClaimed
                && (operation is "upscale" or "photoreal"
                    || (operation == "i2i" && i2iMutationSafe)
                    || structurallySafeVideo);
        string resolvedPresetId = presetId ?? "Default preset";
        string resolvedAdapterId = adapterId ?? "local companion";
        var view = new EnhancementWorkspaceJobView(
            id!,
            sourceId ?? "",
            sourcePath ?? "",
            sourceProducerJobId,
            sourceVideoJobId,
            resolvedPresetId,
            resolvedAdapterId,
            operation,
            videoMutationSafe,
            queueReorderSafe,
            i2iMutationSafe,
            i2iV3Info is not null
                ? 3
                : i2iV2Info?.SchemaVersion ?? (i2iMutationSafe ? 1 : null),
            i2iV3Info is not null
                ? BuildI2iV3Summary(i2iV3Info.Snapshot)
                : i2iV2Info?.Target ?? (i2iMutationSafe ? "hair-color" : null),
            i2iV3Info is not null
                ? BuildI2iV3Summary(i2iV3Info.Snapshot)
                : i2iV2Info?.InstructionSummary,
            i2iV2EnvelopeClaimed || ClaimsI2iV3Envelope(element),
            status,
            cancelRequested,
            progress,
            outputPath,
            errorMessage,
            createdAt,
            updatedAt,
            startedAt,
            finishedAt,
            sourceSize,
            sourceMtimeMs,
            queueOrder,
            apiOrdinal,
            buildRequestDetails
                ? videoTrimV1Snapshot is VideoTrimV1ReaderSnapshot exactTrim
                    ? BuildVideoTrimV1RequestDetails(exactTrim)
                    : videoToolsV2Snapshot is VideoToolsV2ReaderSnapshot exactV2
                    ? BuildCurrentVideoToolsV2RequestDetails(exactV2)
                    : BuildEnhancementJobRequestDetails(
                        element,
                        operation,
                        id!,
                        sourcePath ?? "",
                        resolvedPresetId,
                        resolvedAdapterId,
                        i2iV3Info?.Snapshot)
                : "",
            buildRequestDetails,
            i2iV3Info?.Snapshot,
            miniMaxH3VideoSnapshot,
            videoToolsEnvelopeClaimed,
            videoToolsV2Snapshot?.Kind ?? videoToolsSnapshot?.Kind,
            videoToolsSnapshot?.FinishMode,
            videoToolsV2Snapshot,
            videoTrimEnvelopeClaimed,
            videoTrimV1Snapshot);
        if (videoMutationProbe is not null)
            view.AttachVideoMutationProbe(videoMutationProbe);
        return view;
    }

    private static string BuildCurrentVideoToolsV2RequestDetails(
        VideoToolsV2ReaderSnapshot snapshot)
        => BuildVideoToolsV2RequestDetails(snapshot)
            .Replace(
                "Protocol: Video Tools v2（読取専用）",
                "Protocol: Video Tools v2",
                StringComparison.Ordinal)
            .Replace(
                "保護: cancel/retry/remove/delete/reorder/saved rerunは無効です。元動画と入力依存は変更しません。",
                "Lifecycle: 状態に応じたcancel/retry/remove/delete/reorderを認証済みCompanionへ委譲します。元動画と入力依存は変更しません。",
                StringComparison.Ordinal);

    private static bool TryReadEnhancementVideoMutationProbe(
        JsonElement job,
        out EnhancementVideoMutationProbe? probe)
    {
        probe = null;
        if (!job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(job, "sourceSha256", out string? sourceSha256)
            || !IsLowerHex(sourceSha256, 64))
        {
            return false;
        }

        TryGetStringProperty(job, "sourceId", out string? sourceId);
        TryGetStringProperty(job, "sourcePath", out string? sourcePath);
        if (!TryReadOptionalVideoSourceProducerJobId(
                job,
                out string? sourceProducerJobId))
        {
            return false;
        }

        long? sourceSize = null;
        double? sourceMtimeMs = null;
        if (job.TryGetProperty(
                "sourceSignature",
                out JsonElement sourceSignature)
            && sourceSignature.ValueKind == JsonValueKind.Object)
        {
            if (sourceSignature.TryGetProperty(
                    "size",
                    out JsonElement sourceSizeElement)
                && sourceSizeElement.TryGetInt64(out long parsedSourceSize))
            {
                sourceSize = parsedSourceSize;
            }
            if (sourceSignature.TryGetProperty(
                    "mtimeMs",
                    out JsonElement sourceMtimeElement)
                && sourceMtimeElement.TryGetDouble(out double parsedSourceMtimeMs))
            {
                sourceMtimeMs = parsedSourceMtimeMs;
            }
        }

        bool requiresCurrentCanvasValidation = TryGetExactStringProperty(
            job,
            "presetId",
            MiniMaxH3VideoPresetId);
        int? effectiveWidth = null;
        int? effectiveHeight = null;
        if (requiresCurrentCanvasValidation)
        {
            if (!video.TryGetProperty(
                    "effective",
                    out JsonElement effective)
                || effective.ValueKind != JsonValueKind.Object
                || !effective.TryGetProperty(
                    "width",
                    out JsonElement widthElement)
                || !widthElement.TryGetInt32(out int parsedWidth)
                || !effective.TryGetProperty(
                    "height",
                    out JsonElement heightElement)
                || !heightElement.TryGetInt32(out int parsedHeight)
                || sourceSize is null
                || sourceMtimeMs is null)
            {
                return false;
            }
            effectiveWidth = parsedWidth;
            effectiveHeight = parsedHeight;
        }

        probe = new EnhancementVideoMutationProbe(
            HashStableJson(video),
            sourceSha256!,
            sourceId ?? "",
            sourcePath ?? "",
            sourceProducerJobId,
            sourceSize,
            sourceMtimeMs,
            effectiveWidth,
            effectiveHeight,
            requiresCurrentCanvasValidation);
        return true;
    }

    private static DateTimeOffset? TryReadEnhancementJobTimestamp(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement timestamp)
            || timestamp.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                timestamp.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return null;
        }

        return parsed;
    }

    private static bool ClaimsI2iV2Envelope(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Name is "presetId" or "adapterId"
                && property.Value.GetString() is
                    "flux2-i2i-edit-v2" or "comfyui-flux2-i2i-v2")
            {
                return true;
            }
            if (property.Name != "preset"
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (JsonProperty presetProperty in property.Value.EnumerateObject())
            {
                if (presetProperty.Name == "options"
                    && presetProperty.Value.ValueKind == JsonValueKind.Object
                    && presetProperty.Value.EnumerateObject().Any(static option =>
                        option.Name == "i2iSchemaVersion"
                        && option.Value.ValueKind == JsonValueKind.Number
                        && option.Value.TryGetInt32(out int version)
                        && version == 2))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool ClaimsI2iV3Envelope(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Name is "presetId" or "adapterId"
                && property.Value.GetString() is
                    "flux2-i2i-edit-v3" or "comfyui-flux2-i2i-v3")
            {
                return true;
            }
            if (property.Name != "preset"
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (JsonProperty presetProperty in property.Value.EnumerateObject())
            {
                if (presetProperty.Name == "options"
                    && presetProperty.Value.ValueKind == JsonValueKind.Object
                    && presetProperty.Value.EnumerateObject().Any(static option =>
                        option.Name == "i2iSchemaVersion"
                        && option.Value.ValueKind == JsonValueKind.Number
                        && option.Value.TryGetInt32(out int version)
                        && version == 3))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void EnhancementJobsStatusFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string filter } toggle)
        {
            string normalized = NormalizeEnhancementWorkspaceStatusFilter(filter);
            _enhancementWorkspaceStatusFilter = normalized == "all"
                || toggle.IsChecked != true
                    ? "all"
                    : normalized;
            RefreshEnhancementWorkspaceFilterToggleStates();
            _enhancementWorkspacePageIndex = 0;
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            RefreshEnhancementQueueBulkControls();
        }
    }

    private void EnhancementJobsOperationFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string filter } toggle)
        {
            string normalized = NormalizeEnhancementWorkspaceOperationFilter(filter);
            _enhancementWorkspaceOperationFilter = normalized == "all"
                || toggle.IsChecked != true
                    ? "all"
                    : normalized;
            if (_enhancementWorkspaceOperationFilter != "video")
                _enhancementWorkspaceVideoKindFilter = "all";
            RefreshEnhancementWorkspaceFilterToggleStates();
            _enhancementWorkspacePageIndex = 0;
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            RefreshEnhancementQueueBulkControls();
        }
    }

    private void EnhancementJobsVideoKindFilter_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string filter } toggle)
        {
            string normalized = NormalizeEnhancementWorkspaceVideoKindFilter(
                filter);
            _enhancementWorkspaceVideoKindFilter = normalized == "all"
                || toggle.IsChecked != true
                    ? "all"
                    : normalized;
            if (_enhancementWorkspaceVideoKindFilter != "all")
                _enhancementWorkspaceOperationFilter = "video";
            RefreshEnhancementWorkspaceFilterToggleStates();
            _enhancementWorkspacePageIndex = 0;
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            RefreshEnhancementQueueBulkControls();
        }
    }

    private void RefreshEnhancementWorkspaceFilterToggleStates()
    {
        EnhancementJobsAllFilter.IsChecked =
            _enhancementWorkspaceStatusFilter == "all";
        EnhancementJobsQueuedFilter.IsChecked =
            _enhancementWorkspaceStatusFilter == "queued";
        EnhancementJobsCompletedFilter.IsChecked =
            _enhancementWorkspaceStatusFilter == "completed";
        EnhancementJobsFailedFilter.IsChecked =
            _enhancementWorkspaceStatusFilter == "failed";
        EnhancementJobsCanceledFilter.IsChecked =
            _enhancementWorkspaceStatusFilter == "canceled";
        EnhancementJobsAllOperationsFilter.IsChecked =
            _enhancementWorkspaceOperationFilter == "all";
        EnhancementJobsUpscaleFilter.IsChecked =
            _enhancementWorkspaceOperationFilter == "upscale";
        EnhancementJobsPhotorealFilter.IsChecked =
            _enhancementWorkspaceOperationFilter == "photoreal";
        EnhancementJobsVideoFilter.IsChecked =
            _enhancementWorkspaceOperationFilter == "video";
        EnhancementJobsI2iFilter.IsChecked =
            _enhancementWorkspaceOperationFilter == "i2i";
        EnhancementJobsAllVideoKindsFilter.IsChecked =
            _enhancementWorkspaceVideoKindFilter == "all";
        EnhancementJobsVideoGenerationKindFilter.IsChecked =
            _enhancementWorkspaceVideoKindFilter == "generation";
        EnhancementJobsVideoEditKindFilter.IsChecked =
            _enhancementWorkspaceVideoKindFilter == "edit";
        EnhancementJobsVideoTrimKindFilter.IsChecked =
            _enhancementWorkspaceVideoKindFilter == "trim";
        EnhancementJobsVideoFinishKindFilter.IsChecked =
            _enhancementWorkspaceVideoKindFilter == "finish";
    }

    private void EnhancementJobsScrollTop_Click(object sender, RoutedEventArgs e)
        => FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToTop();

    private void EnhancementJobsScrollBottom_Click(object sender, RoutedEventArgs e)
        => FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToBottom();

    private void EnhancementJobsFirstJob_Click(object sender, RoutedEventArgs e)
    {
        _enhancementWorkspacePageIndex = 0;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToTop();
    }

    private void EnhancementJobsLastJob_Click(object sender, RoutedEventArgs e)
    {
        EnhancementJobsPageWindow page = CalculateEnhancementJobsPageWindow(
            _enhancementWorkspaceFilteredCount,
            int.MaxValue);
        _enhancementWorkspacePageIndex = page.PageIndex;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToBottom();
    }

    private void EnhancementJobsList_ScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (_aiProcessingMinimizedMode
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || Math.Abs(e.VerticalChange) < 0.01)
        {
            return;
        }

        _enhancementWorkspaceScrollChangedCount++;
        ResetEnhancementWorkspaceThumbnailRetry();
        CancellationTokenSource? activeLoad =
            Volatile.Read(ref _enhancementWorkspaceThumbnailCts);
        if (activeLoad is not null && !activeLoad.IsCancellationRequested)
        {
            activeLoad.Cancel();
            _enhancementWorkspaceThumbnailScrollCancellationCount++;
        }
        _enhancementWorkspaceLastThumbnailScrollTimestamp = Stopwatch.GetTimestamp();
        if (!_enhancementWorkspaceThumbnailViewportTimer.IsEnabled)
        {
            _enhancementWorkspaceThumbnailViewportTimer.Start();
            _enhancementWorkspaceThumbnailTimerRestartCount++;
        }
    }

    private void EnhancementWorkspaceThumbnailViewportTimer_Tick(
        object? sender,
        EventArgs e)
    {
        long lastScrollTimestamp = _enhancementWorkspaceLastThumbnailScrollTimestamp;
        if (lastScrollTimestamp > 0)
        {
            TimeSpan quietDuration = Stopwatch.GetElapsedTime(lastScrollTimestamp);
            TimeSpan remaining = EnhancementJobsThumbnailViewportDebounce - quietDuration;
            if (remaining > TimeSpan.Zero)
            {
                _enhancementWorkspaceThumbnailViewportTimer.Interval = remaining;
                return;
            }
        }

        StopEnhancementWorkspaceThumbnailViewportDebounce();
        QueueEnhancementWorkspaceVisibleThumbnailLoad();
    }

    private void EnhancementJobsItemContainerGenerator_StatusChanged(
        object? sender,
        EventArgs e)
    {
        if (EnhancementJobsList.ItemContainerGenerator.Status
                == GeneratorStatus.ContainersGenerated)
        {
            QueueEnhancementWorkspaceVisibleThumbnailLoad();
        }
    }

    private void EnhancementWorkspaceThumbnailRetryTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _enhancementWorkspaceThumbnailRetryTimer.Stop();
        _enhancementWorkspaceThumbnailFailedJobIds.Clear();
        QueueEnhancementWorkspaceVisibleThumbnailLoad();
    }

    private void ResetEnhancementWorkspaceThumbnailRetry()
    {
        _enhancementWorkspaceThumbnailRetryTimer?.Stop();
        _enhancementWorkspaceThumbnailRetryAttempt = 0;
        _enhancementWorkspaceThumbnailFailedJobIds.Clear();
    }

    private void ScheduleEnhancementWorkspaceThumbnailRetry()
    {
        if (_aiProcessingMinimizedMode
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || _enhancementWorkspaceThumbnailViewportTimer.IsEnabled
            || _enhancementWorkspaceThumbnailRetryTimer.IsEnabled
            || _enhancementWorkspaceThumbnailRetryAttempt
                >= EnhancementJobsThumbnailRetryDelays.Length)
        {
            return;
        }

        _enhancementWorkspaceThumbnailRetryTimer.Interval =
            EnhancementJobsThumbnailRetryDelays[
                _enhancementWorkspaceThumbnailRetryAttempt++];
        _enhancementWorkspaceThumbnailRetryTimer.Start();
    }

    private void StopEnhancementWorkspaceThumbnailViewportDebounce()
    {
        _enhancementWorkspaceThumbnailViewportTimer.Stop();
        _enhancementWorkspaceThumbnailViewportTimer.Interval =
            EnhancementJobsThumbnailViewportDebounce;
        _enhancementWorkspaceLastThumbnailScrollTimestamp = 0;
    }

    private void EnhancementJobsPreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_enhancementWorkspacePageIndex <= 0)
            return;

        _enhancementWorkspacePageIndex--;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToTop();
    }

    private void EnhancementJobsNextPage_Click(object sender, RoutedEventArgs e)
    {
        EnhancementJobsPageWindow page = CalculateEnhancementJobsPageWindow(
            _enhancementWorkspaceFilteredCount,
            _enhancementWorkspacePageIndex + 1);
        if (page.PageIndex == _enhancementWorkspacePageIndex)
            return;

        _enhancementWorkspacePageIndex = page.PageIndex;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.ScrollToTop();
    }

    private void ApplyEnhancementWorkspaceFilter(bool loadThumbnails)
    {
        EnhancementWorkspaceJobView[] filtered = _enhancementWorkspaceJobs
            .Where(job =>
                !_enhancementWorkspaceOptimisticallyHiddenJobIds.Contains(job.Id)
                &&
                MatchesEnhancementWorkspaceStatusFilter(job)
                && MatchesEnhancementWorkspaceOperationFilter(job))
            .ToArray();
        _enhancementWorkspaceFilteredCount = filtered.Length;
        EnhancementJobsPageWindow page = CalculateEnhancementJobsPageWindow(
            filtered.Length,
            _enhancementWorkspacePageIndex);
        _enhancementWorkspacePageIndex = page.PageIndex;
        EnhancementWorkspaceJobView[] visible = filtered
            .Skip(page.FirstIndex)
            .Take(page.ItemCount)
            .ToArray();
        EnhancementWorkspaceJobView[] current =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray()
            ?? [];
        bool sameItems = current.Length == visible.Length
            && current.Zip(visible, static (left, right) => ReferenceEquals(left, right)).All(static same => same);
        if (!sameItems)
            EnhancementJobsList.ItemsSource = visible;
        EnhancementJobsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnhancementJobsPaginationPanel.Visibility = filtered.Length > EnhancementJobsPageSize
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnhancementJobsPreviousPageButton.IsEnabled = page.PageIndex > 0;
        EnhancementJobsNextPageButton.IsEnabled = page.PageIndex + 1 < page.PageCount;
        EnhancementJobsPageText.Text = filtered.Length == 0
            ? "0 / 0"
            : $"{page.FirstIndex + 1:N0}–{page.FirstIndex + page.ItemCount:N0} / {filtered.Length:N0}";
        if (loadThumbnails)
            QueueEnhancementWorkspaceVisibleThumbnailLoad();
    }

    private static EnhancementJobsPageWindow CalculateEnhancementJobsPageWindow(
        int filteredCount,
        int requestedPageIndex)
    {
        int boundedCount = Math.Max(0, filteredCount);
        int pageCount = boundedCount == 0
            ? 0
            : (boundedCount + EnhancementJobsPageSize - 1) / EnhancementJobsPageSize;
        int pageIndex = pageCount == 0
            ? 0
            : Math.Clamp(requestedPageIndex, 0, pageCount - 1);
        int firstIndex = pageIndex * EnhancementJobsPageSize;
        int itemCount = Math.Min(
            EnhancementJobsPageSize,
            Math.Max(0, boundedCount - firstIndex));
        return new EnhancementJobsPageWindow(
            pageIndex,
            pageCount,
            firstIndex,
            itemCount);
    }

    private void QueueEnhancementWorkspaceVisibleThumbnailLoad(
        int realizationAttempt = 0)
    {
        if (_aiProcessingMinimizedMode
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || _suppressEnhancementWorkspaceThumbnailLoadsForSmoke
            || _enhancementWorkspaceThumbnailViewportLoadPending)
        {
            return;
        }

        _enhancementWorkspaceThumbnailViewportLoadPending = true;
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _enhancementWorkspaceThumbnailViewportLoadPending = false;
                if (_aiProcessingMinimizedMode
                    || EnhancementJobsDialog.Visibility != Visibility.Visible)
                    return;

                EnhancementWorkspaceJobView[] realized =
                    FindVisualDescendants<ListBoxItem>(EnhancementJobsList)
                        .Select(static container =>
                            container.DataContext as EnhancementWorkspaceJobView)
                        .Where(static job => job is not null)
                        .Select(static job => job!)
                        .DistinctBy(static job => job.Id, StringComparer.Ordinal)
                        .Where(static job => job.Thumbnail is null)
                        .Where(job => !_enhancementWorkspaceThumbnailFailedJobIds.Contains(job.Id))
                        .Take(EnhancementJobsThumbnailViewportLimit)
                        .ToArray();
                if (realized.Length == 0
                    && realizationAttempt < 2
                    && EnhancementJobsList.Items.Count > 0)
                {
                    _ = Dispatcher.BeginInvoke(
                        new Action(() =>
                            QueueEnhancementWorkspaceVisibleThumbnailLoad(
                                realizationAttempt + 1)),
                        DispatcherPriority.ContextIdle);
                    return;
                }
                BeginEnhancementWorkspaceThumbnailLoad(realized);
            }),
            DispatcherPriority.Loaded);
    }

    private void BeginEnhancementWorkspaceThumbnailLoad(
        IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        EnhancementWorkspaceJobView[] missing = jobs
            .Where(static job => job.Thumbnail is null)
            .Take(EnhancementJobsThumbnailViewportLimit)
            .ToArray();
        _enhancementWorkspaceLastThumbnailBatchSize = missing.Length;
        if (_aiProcessingMinimizedMode
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || missing.Length == 0)
            return;

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _enhancementWorkspaceThumbnailCts,
            cts);
        previous?.Cancel();
        long generation = _enhancementWorkspaceGeneration;
        _ = LoadEnhancementWorkspaceThumbnailsAsync(missing, generation, cts);
    }

    private async Task LoadEnhancementWorkspaceThumbnailsAsync(
        IReadOnlyList<EnhancementWorkspaceJobView> jobs,
        long generation,
        CancellationTokenSource cts)
    {
        bool retryMissingThumbnail = false;
        bool canceled = false;
        try
        {
            foreach (EnhancementWorkspaceJobView job in jobs)
            {
                cts.Token.ThrowIfCancellationRequested();
                string? canonicalSource = await Task.Run(
                    () => TryResolveEnhancementWorkspaceInput(
                        job,
                        out string resolvedSource)
                            ? resolvedSource
                            : null,
                    cts.Token);
                if (canonicalSource is null)
                {
                    retryMissingThumbnail = true;
                    _enhancementWorkspaceThumbnailFailedJobIds.Add(job.Id);
                    continue;
                }

                string cacheKey = $"{canonicalSource}|{job.SourceSize?.ToString(CultureInfo.InvariantCulture)}|{job.SourceMtimeMs?.ToString(CultureInfo.InvariantCulture)}";
                if (!_enhancementWorkspaceThumbnailCache.TryGetValue(cacheKey, out BitmapSource? thumbnail))
                {
                    thumbnail = await Task.Run(() => DecodeEnhancementWorkspaceThumbnail(canonicalSource), cts.Token);
                    if (thumbnail is null)
                    {
                        retryMissingThumbnail = true;
                        _enhancementWorkspaceThumbnailFailedJobIds.Add(job.Id);
                        continue;
                    }
                    if (_enhancementWorkspaceThumbnailCache.Count >= EnhancementJobsThumbnailCacheLimit)
                        _enhancementWorkspaceThumbnailCache.Remove(_enhancementWorkspaceThumbnailCache.Keys.First());
                    _enhancementWorkspaceThumbnailCache[cacheKey] = thumbnail;
                }

                if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                    return;
                _enhancementWorkspaceThumbnailFailedJobIds.Remove(job.Id);
                job.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            canceled = true;
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _enhancementWorkspaceThumbnailCts,
                null,
                cts);
            cts.Dispose();
            if (generation == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                if (retryMissingThumbnail)
                    ScheduleEnhancementWorkspaceThumbnailRetry();
                else
                    _enhancementWorkspaceThumbnailRetryAttempt = 0;
                if (!canceled
                    && jobs.Count >= EnhancementJobsThumbnailViewportLimit)
                {
                    _ = Dispatcher.BeginInvoke(
                        new Action(() =>
                            QueueEnhancementWorkspaceVisibleThumbnailLoad()),
                        DispatcherPriority.ContextIdle);
                }
            }
        }
    }

    private static BitmapSource? DecodeEnhancementWorkspaceThumbnail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 96;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    private bool TryResolveEnhancementWorkspaceCatalogSource(
        EnhancementWorkspaceJobView job,
        out string canonicalSource)
    {
        canonicalSource = "";
        if (!TryResolveEnhancementSourceIdentity(
                job.SourceId,
                out string sourceIdIdentity)
            || !File.Exists(sourceIdIdentity)
            || !SupportedImageExtensions.Contains(
                Path.GetExtension(sourceIdIdentity)))
        {
            return false;
        }

        canonicalSource = sourceIdIdentity;
        return true;
    }

    private bool TryResolveEnhancementWorkspaceInput(
        EnhancementWorkspaceJobView job,
        out string canonicalInput)
    {
        canonicalInput = "";
        if (job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementSourceIdentity(
                job.SourcePath,
                out string sourcePathIdentity)
            || !File.Exists(sourcePathIdentity)
            || !SupportedImageExtensions.Contains(
                Path.GetExtension(sourcePathIdentity)))
        {
            return false;
        }

        try
        {
            bool usesProducerManagedInput = job.IsVideoOperation
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId);
            bool usesDisplayedManagedInput = job.IsVideoOperation
                && TryResolveDisplayedManagedVideoSourcePath(
                    sourcePathIdentity,
                    out string displayedManagedInput)
                && EnhancementSourceIdentityComparer.Equals(
                    displayedManagedInput,
                    sourcePathIdentity);
            if (usesProducerManagedInput || usesDisplayedManagedInput)
            {
                string lexicalInput = Path.GetFullPath(job.SourcePath);
                string lexicalRoot =
                    Path.GetFullPath(ResolvedManagedEnhancementOutputsRoot);
                string canonicalRoot = Path.GetFullPath(
                    _resolveFinalPath(lexicalRoot));
                if (!IsPathInside(lexicalInput, lexicalRoot)
                    || !IsPathInside(sourcePathIdentity, canonicalRoot))
                {
                    return false;
                }
            }
            else if (!TryResolveEnhancementWorkspaceCatalogSource(
                         job,
                         out string canonicalCatalogSource)
                     || !EnhancementSourceIdentityComparer.Equals(
                         sourcePathIdentity,
                         canonicalCatalogSource))
            {
                return false;
            }

            var info = new FileInfo(sourcePathIdentity);
            double currentMtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (info.Length != job.SourceSize.Value || Math.Abs(currentMtimeMs - job.SourceMtimeMs.Value) > 1)
                return false;
            canonicalInput = sourcePathIdentity;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    private async void CancelEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanCancel)
            await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
    }

    private async void EnhancementJobAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: EnhancementWorkspaceJobView job,
                CommandParameter: string action,
            })
        {
            return;
        }

        switch (action)
        {
            case "move-up":
            case "move-down":
            case "move-next":
                await MoveEnhancementJobInQueueAsync(job, action[5..]);
                break;
            case "update-prompts":
                UpdateQueuedPhotorealPrompts_Click(sender, e);
                break;
            case "cancel":
                CancelEnhancementJob_Click(sender, e);
                break;
            case "retry":
                RetryEnhancementJob_Click(sender, e);
                break;
            case "rerun":
                RerunPhotorealJob_Click(sender, e);
                break;
            case "rerun-next":
                RerunPhotorealJobNext_Click(sender, e);
                break;
            case "video-rerun-saved":
                await RerunMiniMaxH3VideoWithSavedPromptAsync(job);
                break;
            case "video-edit-prompt":
                await EditMiniMaxH3VideoPromptAsync(job);
                break;
            case "i2i-v3-rerun":
                await RerunI2iV3JobAsync(job, enqueueNext: false);
                break;
            case "i2i-v3-rerun-next":
                await RerunI2iV3JobAsync(job, enqueueNext: true);
                break;
            case "i2i-v3-edit":
                await EditI2iV3JobSettingsAsync(job);
                break;
            case "dismiss":
                DismissEnhancementJob_Click(sender, e);
                break;
            case "open-output":
                OpenEnhancementOutput_Click(sender, e);
                break;
            case "delete-output":
                DeleteEnhancementOutput_Click(sender, e);
                break;
        }
    }

    private async void ToggleEnhancementJobDetails_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job } button)
            return;
        if (job.RequestDetailsExpanded)
        {
            job.RequestDetailsExpanded = false;
            if (_usingDefaultModalEnhancementSender)
                job.ClearRequestDetails();
            return;
        }

        button.IsEnabled = false;
        try
        {
            if (await EnsureEnhancementJobRequestDetailsLoadedAsync(
                    job,
                    token: _enhancementCompanionLifetimeCts.Token))
            {
                job.RequestDetailsExpanded = true;
            }
            else
            {
                EnhancementJobsStatusText.Text =
                    "Jobの詳細が更新されています。Jobsを更新して選び直してください。";
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the app cancels optional detail reads without changing Jobs.
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void CopyEnhancementJobDetails_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job } button)
            return;

        button.IsEnabled = false;
        try
        {
            if (!await EnsureEnhancementJobRequestDetailsLoadedAsync(
                    job,
                    forceReload: true,
                    token: _enhancementCompanionLifetimeCts.Token)
                || string.IsNullOrWhiteSpace(job.RequestDetailsText))
            {
                EnhancementJobsStatusText.Text =
                    "Jobの詳細が更新されています。Jobsを更新して選び直してください。";
                return;
            }
            Clipboard.SetText(job.RequestDetailsText);
            EnhancementJobsStatusText.Text =
                $"{job.SourceName} のPrompt・設定をコピーしました。";
        }
        catch (Exception ex) when (
            ex is ExternalException or InvalidOperationException)
        {
            button.ToolTip = $"コピーできませんでした: {ex.Message}";
            EnhancementJobsStatusText.Text =
                "Clipboardを使用中です。少し待ってからもう一度試してください。";
        }
        catch (OperationCanceledException)
        {
            // Closing the app cancels optional detail reads without changing Jobs.
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void RetryEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanRetry)
        {
            // Retry is an exact persisted-job mutation. The authenticated
            // companion revalidates the pinned source path, signature, hash,
            // and producer before it commits the replacement child. Avoid a
            // duplicate media read on the WPF dispatcher.
            await RunEnhancementWorkspaceMutationAsync(
                job,
                HttpMethod.Post,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry",
                "Retry queued. The original terminal history was removed.",
                removeTerminalOriginalAfterSuccess:
                    job.Status is "failed" or "canceled",
                operationLogName: "job_retry");
        }
    }

    private async void DismissEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanDismiss)
        {
            await RunEnhancementWorkspaceMutationAsync(
                job,
                HttpMethod.Delete,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}",
                "Job removed from history. Source and output files were not changed.");
        }
    }

    private async void MoveEnhancementJobInQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: EnhancementWorkspaceJobView job,
                CommandParameter: string move,
            }
            || move is not ("up" or "down" or "next")
            || !job.CanReorder)
        {
            return;
        }

        await MoveEnhancementJobInQueueAsync(job, move);
    }

    private async Task<bool> MoveEnhancementJobInQueueAsync(
        EnhancementWorkspaceJobView job,
        string move)
    {
        bool coalescedReorderActive =
            _enhancementWorkspaceQueueOrderFlushTask is not null;
        if ((_enhancementWorkspaceMutationPending && !coalescedReorderActive)
            || job.IsBusy
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || move is not ("up" or "down" or "next")
            || !job.CanReorder)
        {
            return false;
        }

        string[] previousOrder = CurrentEnhancementWorkspaceQueueOrder();
        string[] optimisticOrder = MoveEnhancementWorkspaceQueueId(
            previousOrder,
            job.Id,
            move);
        if (optimisticOrder.SequenceEqual(previousOrder, StringComparer.Ordinal))
            return false;

        if (_enhancementWorkspaceQueuedJobsBatchReorderSupported
            && _enhancementWorkspaceJobs
                .Where(static candidate => candidate.Status == "queued")
                .All(static candidate => candidate.CanReorder)
            && CanUseQueuedJobsBatchReorder(previousOrder))
        {
            return QueueCoalescedEnhancementWorkspaceOrder(
                previousOrder,
                optimisticOrder,
                move);
        }
        if (coalescedReorderActive)
            return false;

        return await MoveEnhancementJobInQueueLegacyAsync(
            job,
            move,
            previousOrder,
            optimisticOrder);
    }

    private async Task<bool> MoveEnhancementJobInQueueLegacyAsync(
        EnhancementWorkspaceJobView job,
        string move,
        string[] previousOrder,
        string[] optimisticOrder)
    {
        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        _enhancementWorkspaceQueuePresentationRevision++;
        ApplyEnhancementWorkspaceQueuePresentation(optimisticOrder);
        EnhancementJobsStatusText.Text = move == "next"
            ? "次の処理位置へすぐ反映しました。キューへ保存しています…"
            : "待機順へすぐ反映しました。キューへ保存しています…";
        try
        {
            EnhancementApiResponse response =
                await SendTrackedEnhancementWorkspaceMutationAsync(
                    () => SendEnhancementApiAsync(
                        HttpMethod.Post,
                        $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/queue",
                        new { move }));
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (!response.Ok)
            {
                _enhancementWorkspaceQueuePresentationRevision++;
                ApplyEnhancementWorkspaceQueuePresentation(previousOrder);
                EnhancementJobsStatusText.Text =
                    $"待機順を元に戻しました。{response.Error}";
                return false;
            }

            JsonElement movedElement = default;
            bool hasMovedResult = response.Payload is JsonElement payload
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("moved", out movedElement)
                && movedElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
            bool moved = hasMovedResult && movedElement.GetBoolean();
            bool authoritativeApplied = response.Payload is JsonElement queuePayload
                && TryReadEnhancementWorkspaceQueueOrder(
                    queuePayload,
                    out string[] authoritativeOrder)
                && ApplyEnhancementWorkspaceQueuePresentation(
                    authoritativeOrder);
            if (authoritativeApplied)
            {
                _enhancementWorkspaceQueuePresentationRevision++;
            }

            if (!moved && !authoritativeApplied)
            {
                _enhancementWorkspaceQueuePresentationRevision++;
                ApplyEnhancementWorkspaceQueuePresentation(previousOrder);
                EnhancementJobsStatusText.Text = hasMovedResult
                    ? "待機順は変わりませんでした。最新の一覧を更新してください。"
                    : "companionの応答を確認できないため、待機順を元に戻しました。";
                return false;
            }

            EnhancementJobsStatusText.Text = !moved && authoritativeApplied
                ? "最新の待機順に同期しました。"
                : move == "next"
                ? "このジョブを次の処理位置へ移動しました。"
                : "待機順を変更しました。";
            return true;
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private bool QueueCoalescedEnhancementWorkspaceOrder(
        string[] previousOrder,
        string[] optimisticOrder,
        string move)
    {
        bool startsFlush = _enhancementWorkspaceQueueOrderFlushTask is null;
        if (startsFlush)
        {
            _enhancementWorkspaceMutationPending = true;
            _enhancementWorkspaceConfirmedQueueOrder = previousOrder;
        }

        _enhancementWorkspaceQueuePresentationRevision++;
        if (!ApplyEnhancementWorkspaceQueuePresentation(optimisticOrder))
        {
            if (startsFlush)
            {
                _enhancementWorkspaceMutationPending = false;
                _enhancementWorkspaceConfirmedQueueOrder = null;
            }
            return false;
        }

        _enhancementWorkspacePendingQueueOrder = optimisticOrder;
        EnhancementJobsStatusText.Text = move == "next"
            ? "次の処理位置へすぐ反映しました。連続操作をまとめて保存します…"
            : "待機順へすぐ反映しました。連続操作をまとめて保存します…";
        RefreshEnhancementQueueBulkControls();
        RefreshEnhancementQueuePauseControl();
        if (startsFlush)
        {
            long generation = _enhancementWorkspaceGeneration;
            _enhancementWorkspaceQueueOrderFlushTask =
                FlushCoalescedEnhancementWorkspaceOrderAsync(generation);
        }
        return true;
    }

    private static bool CanUseQueuedJobsBatchReorder(
        IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count is < 1 or > EnhancementQueuedJobsBatchReorderLimit
            || orderedIds.Any(static id =>
                string.IsNullOrWhiteSpace(id) || id.Length > 255)
            || orderedIds.Distinct(StringComparer.Ordinal).Count()
                != orderedIds.Count)
        {
            return false;
        }

        string probeBody = JsonSerializer.Serialize(new
        {
            ids = orderedIds,
            orderRequestId = "00000000-0000-0000-0000-000000000000",
        });
        return Encoding.UTF8.GetByteCount(probeBody)
            <= EnhancementQueuedJobsBatchReorderMaximumBodyBytes;
    }

    private async Task FlushCoalescedEnhancementWorkspaceOrderAsync(
        long originatingGeneration)
    {
        bool refreshAuthoritativeOrder = false;
        string? failureMessage = null;
        try
        {
            await Task.Delay(EnhancementJobsQueueReorderDebounce);
            while (_enhancementWorkspacePendingQueueOrder is { } requestedOrder)
            {
                _enhancementWorkspacePendingQueueOrder = null;
                if (_enhancementWorkspaceConfirmedQueueOrder is { } confirmedOrder
                    && requestedOrder.SequenceEqual(
                        confirmedOrder,
                        StringComparer.Ordinal))
                {
                    if (_enhancementWorkspacePendingQueueOrder is null)
                        break;
                    await Task.Delay(EnhancementJobsQueueReorderDebounce);
                    continue;
                }

                string orderRequestId = Guid.NewGuid().ToString("D");
                EnhancementApiResponse response =
                    await SendTrackedEnhancementWorkspaceMutationAsync(
                        () => SendIdempotentEnhancementMutationAsync(
                            HttpMethod.Post,
                            "api/enhance/jobs/queued/order",
                            new
                            {
                                ids = requestedOrder,
                                orderRequestId,
                            }));
                _enhancementWorkspaceQueuePresentationRevision++;
                if (!response.Ok
                    || !TryParseQueuedJobsBatchReorderResponse(
                        response.Payload,
                        orderRequestId,
                        requestedOrder.Length))
                {
                    string errorCode = response.Ok
                        ? "QUEUED_ORDER_RESPONSE_INVALID"
                        : EnhancementApiErrorCode(response);
                    failureMessage = errorCode switch
                    {
                        "QUEUED_ORDER_SNAPSHOT_CHANGED" =>
                            "処理開始・追加・取消で待機列が変わったため、最新の順序へ同期しました。",
                        "QUEUED_ORDER_IDEMPOTENCY_CONFLICT" =>
                            "待機順の再送IDが競合したため、最新の順序へ同期しました。",
                        _ =>
                            $"待機順を保存できなかったため、最新の順序へ同期しました（{errorCode}）。",
                    };
                    refreshAuthoritativeOrder = true;
                    break;
                }

                _enhancementWorkspaceConfirmedQueueOrder = requestedOrder;
                if (_enhancementWorkspacePendingQueueOrder is null)
                    break;
                await Task.Delay(EnhancementJobsQueueReorderDebounce);
            }

            if (originatingGeneration != _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                refreshAuthoritativeOrder = true;
            }

            if (refreshAuthoritativeOrder)
            {
                _enhancementWorkspaceHealthInventorySignature = null;
                if (originatingGeneration == _enhancementWorkspaceGeneration
                    && EnhancementJobsDialog.Visibility == Visibility.Visible
                    && _enhancementWorkspaceConfirmedQueueOrder is { } confirmedOrder)
                {
                    _enhancementWorkspaceQueuePresentationRevision++;
                    ApplyEnhancementWorkspaceQueuePresentation(confirmedOrder);
                    EnhancementJobsStatusText.Text = "最新の待機順を確認しています…";
                }

                if (EnhancementJobsDialog.Visibility == Visibility.Visible)
                {
                    long refreshGeneration = _enhancementWorkspaceGeneration;
                    for (int attempt = 0;
                        attempt < 400
                            && _enhancementWorkspaceRefreshPending
                            && _enhancementWorkspaceRefreshGeneration
                                == refreshGeneration;
                        attempt++)
                    {
                        await Task.Delay(10);
                    }
                    await RefreshEnhancementJobsWorkspaceAsync(
                        refreshGeneration,
                        isPoll: false);
                    if (refreshGeneration == _enhancementWorkspaceGeneration
                        && EnhancementJobsDialog.Visibility == Visibility.Visible
                        && failureMessage is not null)
                    {
                        EnhancementJobsStatusText.Text = failureMessage;
                    }
                }
            }
            else if (originatingGeneration == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                EnhancementJobsStatusText.Text =
                    "連続した待機順の変更をまとめて保存しました。";
            }
        }
        catch (Exception ex)
        {
            _enhancementWorkspaceHealthInventorySignature = null;
            string transportFailure =
                $"待機順を保存できなかったため、最新の順序へ同期しました（{ex.GetType().Name}）。";
            if (originatingGeneration == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                _enhancementWorkspaceQueuePresentationRevision++;
                if (_enhancementWorkspaceConfirmedQueueOrder is { } confirmedOrder)
                    ApplyEnhancementWorkspaceQueuePresentation(confirmedOrder);
                EnhancementJobsStatusText.Text = "最新の待機順を確認しています…";
            }
            if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                long refreshGeneration = _enhancementWorkspaceGeneration;
                for (int attempt = 0;
                    attempt < 400
                        && _enhancementWorkspaceRefreshPending
                        && _enhancementWorkspaceRefreshGeneration
                            == refreshGeneration;
                    attempt++)
                {
                    await Task.Delay(10);
                }
                await RefreshEnhancementJobsWorkspaceAsync(
                    refreshGeneration,
                    isPoll: false);
                if (refreshGeneration == _enhancementWorkspaceGeneration
                    && EnhancementJobsDialog.Visibility == Visibility.Visible)
                {
                    EnhancementJobsStatusText.Text = transportFailure;
                }
            }
        }
        finally
        {
            _enhancementWorkspacePendingQueueOrder = null;
            _enhancementWorkspaceConfirmedQueueOrder = null;
            _enhancementWorkspaceQueueOrderFlushTask = null;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private static bool TryParseQueuedJobsBatchReorderResponse(
        JsonElement? payload,
        string expectedRequestId,
        int expectedCount)
    {
        if (payload is not JsonElement response
            || response.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(
                response,
                "orderRequestId",
                out string? orderRequestId)
            || !string.Equals(
                orderRequestId,
                expectedRequestId,
                StringComparison.Ordinal)
            || !response.TryGetProperty(
                "queueOrderRevision",
                out JsonElement revisionElement)
            || !revisionElement.TryGetInt64(out long revision)
            || revision < 0
            || !response.TryGetProperty(
                "orderedCount",
                out JsonElement orderedCountElement)
            || !orderedCountElement.TryGetInt32(out int orderedCount)
            || orderedCount != expectedCount
            || !response.TryGetProperty(
                "changedCount",
                out JsonElement changedCountElement)
            || !changedCountElement.TryGetInt32(out int changedCount)
            || changedCount < 0
            || changedCount > expectedCount
            || !response.TryGetProperty(
                "replayed",
                out JsonElement replayedElement)
            || replayedElement.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        return true;
    }

    private string[] CurrentEnhancementWorkspaceQueueOrder()
        => _enhancementWorkspaceJobs
            .Where(static candidate => candidate.Status == "queued")
            .OrderBy(static candidate => candidate.QueuePosition ?? int.MaxValue)
            .ThenBy(static candidate => candidate.CreatedAt)
            .ThenBy(static candidate => candidate.ApiOrdinal)
            .Select(static candidate => candidate.Id)
            .ToArray();

    private static string[] MoveEnhancementWorkspaceQueueId(
        IReadOnlyList<string> current,
        string id,
        string move)
    {
        var next = current.ToList();
        int from = next.FindIndex(candidate => string.Equals(
            candidate,
            id,
            StringComparison.Ordinal));
        if (from < 0)
            return current.ToArray();

        int to = move switch
        {
            "up" => Math.Max(0, from - 1),
            "down" => Math.Min(next.Count - 1, from + 1),
            "next" => 0,
            _ => from,
        };
        if (from == to)
            return current.ToArray();

        next.RemoveAt(from);
        next.Insert(to, id);
        return next.ToArray();
    }

    private bool ApplyEnhancementWorkspaceQueuePresentation(
        IReadOnlyList<string> orderedIds)
    {
        EnhancementWorkspaceJobView[] queued = _enhancementWorkspaceJobs
            .Where(static candidate => candidate.Status == "queued")
            .ToArray();
        if (orderedIds.Count != queued.Length
            || orderedIds.Distinct(StringComparer.Ordinal).Count()
                != orderedIds.Count)
        {
            return false;
        }

        var queuedById = new Dictionary<string, EnhancementWorkspaceJobView>(
            StringComparer.Ordinal);
        foreach (EnhancementWorkspaceJobView candidate in queued)
        {
            if (!queuedById.TryAdd(candidate.Id, candidate))
                return false;
        }
        if (orderedIds.Any(id => !queuedById.ContainsKey(id)))
            return false;

        for (int index = 0; index < orderedIds.Count; index++)
            queuedById[orderedIds[index]].ApplyQueuePresentation(
                index + 1,
                orderedIds.Count,
                index);

        _enhancementWorkspaceJobs.Sort(CompareEnhancementWorkspaceInventory);
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        RefreshEnhancementQueueBulkControls();
        return true;
    }

    private static bool TryReadEnhancementWorkspaceQueueOrder(
        JsonElement payload,
        out string[] order)
    {
        order = [];
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("queue", out JsonElement queue)
            || queue.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var ids = new List<string>();
        foreach (JsonElement element in queue.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetStringProperty(element, "id", out string? id)
                || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }
            ids.Add(id);
        }
        order = ids.ToArray();
        return true;
    }

    private async void UpdateQueuedPhotorealPrompts_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || !job.CanUpdatePhotorealPrompts)
        {
            return;
        }
        if (!TryResolveEnhancementWorkspaceInput(job, out string sourceIdentity))
        {
            EnhancementJobsStatusText.Text =
                "元画像を安全に解決できないため、Promptは変更していません。";
            return;
        }

        ModalPhotorealRequestSettings settings;
        if (!TryResolvePhotorealSeed(out int? seed, out string seedError))
        {
            EnhancementJobsStatusText.Text = seedError;
            return;
        }
        try
        {
            settings = await ResolvePhotorealRequestSettingsAsync(
                CurrentModalPhotorealRequestSettings(),
                sourceIdentity);
        }
        catch (InvalidOperationException ex)
        {
            EnhancementJobsStatusText.Text = ex.Message;
            return;
        }
        await RunEnhancementWorkspaceMutationAsync(
            job,
            HttpMethod.Post,
            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/prompts",
            "待機中の実写化ジョブを現在設定へ更新しました。個別Prompt変換と待ち順は維持しています。",
            CreateQueuedPhotorealSettingsUpdateBody(settings, seed));
    }

    private static Dictionary<string, object?>
        CreateQueuedPhotorealSettingsUpdateBody(
            ModalPhotorealRequestSettings settings,
            int? seed)
        => new(StringComparer.Ordinal)
        {
            ["prompt"] = settings.Prompt,
            ["negativePrompt"] = settings.NegativePrompt,
            ["loraEnabled"] = settings.LoraEnabled,
            ["strength"] = settings.Strength,
            ["steps"] = settings.Steps,
            ["cfgScale"] = settings.CfgScale,
            ["maxDimension"] = settings.MaxDimension,
            ["seed"] = seed,
        };

    private async void UpdateAllQueuedPhotorealPrompts_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || !CanUpdateAllQueuedPhotorealPrompts())
        {
            return;
        }

        EnhancementWorkspaceJobView[] queuedPhotorealJobs =
            _enhancementWorkspaceJobs
                .Where(static job => job.CanUpdatePhotorealPrompts)
                .ToArray();
        ModalPhotorealRequestSettings currentSettings =
            CurrentModalPhotorealRequestSettings();
        if (!TryResolvePhotorealSeed(out int? seed, out string seedError))
        {
            EnhancementJobsStatusText.Text = seedError;
            return;
        }
        long generation = _enhancementWorkspaceGeneration;
        int updatedCount = 0;
        int skippedCount = 0;
        string? firstError = null;

        _enhancementWorkspaceMutationPending = true;
        RefreshEnhancementQueueBulkControls();
        RefreshEnhancementQueuePauseControl();
        EnhancementJobsStatusText.Text =
            $"待機中の実写化 {queuedPhotorealJobs.Length:N0}件を現在設定へ更新しています…";
        try
        {
            foreach (EnhancementWorkspaceJobView job in queuedPhotorealJobs)
            {
                if (generation != _enhancementWorkspaceGeneration
                    || EnhancementJobsDialog.Visibility != Visibility.Visible)
                {
                    return;
                }

                if (!TryResolveEnhancementWorkspaceInput(
                        job,
                        out string sourceIdentity))
                {
                    skippedCount++;
                    firstError ??= $"{job.Id}: 元画像を解決できませんでした。";
                    continue;
                }

                ModalPhotorealRequestSettings settings;
                try
                {
                    settings = await ResolvePhotorealRequestSettingsAsync(
                        currentSettings,
                        sourceIdentity);
                }
                catch (InvalidOperationException ex)
                {
                    skippedCount++;
                    firstError ??= $"{job.Id}: {ex.Message}";
                    continue;
                }

                EnhancementApiResponse response =
                    await SendTrackedEnhancementWorkspaceMutationAsync(
                        () => SendEnhancementApiAsync(
                            HttpMethod.Post,
                            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/prompts",
                            CreateQueuedPhotorealSettingsUpdateBody(
                                settings,
                                seed)));
                if (response.Ok)
                {
                    updatedCount++;
                }
                else
                {
                    skippedCount++;
                    firstError ??= $"{job.Id}: {response.Error}";
                }
            }

            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }

            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
            EnhancementJobsStatusText.Text = skippedCount == 0
                ? $"待機中の実写化 {updatedCount:N0}件を現在のPrompt・LoRA・強さ・CFG・品質・解像度・Seedへ更新しました。元画像ごとの個別Prompt変換と待ち順は維持しています。"
                : $"{updatedCount:N0}件を更新、{skippedCount:N0}件をスキップしました。{firstError}";
        }
        finally
        {
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private async void CancelAllQueuedEnhancementJobs_Click(object sender, RoutedEventArgs e)
    {
        EnhancementWorkspaceJobView[] matchingQueuedJobs = _enhancementWorkspaceJobs
            .Where(job =>
                job.Status == "queued"
                && MatchesEnhancementWorkspaceOperationFilter(job))
            .ToArray();
        EnhancementWorkspaceJobView[] queuedJobs = matchingQueuedJobs
            .Where(static job => job.CanCancel)
            .OrderBy(static job => job.QueueOrder ?? int.MaxValue)
            .ThenBy(static job => job.CreatedAt)
            .ThenBy(static job => job.ApiOrdinal)
            .ToArray();
        int protectedCount = matchingQueuedJobs.Length - queuedJobs.Length;
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || queuedJobs.Length == 0
            || !CanCancelAllQueuedEnhancementJobs())
        {
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        HashSet<string> optimisticJobIds = queuedJobs
            .Select(static job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        EnhancementWorkspaceJobView[] optimisticVisibleJobs =
            BeginOptimisticBulkPresentation(
                optimisticJobIds,
                hideRows: true);
        RefreshEnhancementQueueBulkControls();
        EnhancementJobsClearQueuedButton.Content = "待機中を消しています…";
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            int canceledCount = 0;
            int failedCount = 0;
            int alreadyMissingCount = 0;
            int attemptedCount = 0;
            int unattemptedCount = 0;
            string? firstError = null;
            string operationLabel = EnhancementWorkspaceOperationFilterLabel();
            EnhancementJobsStatusText.Text =
                $"待機中の{operationLabel} {queuedJobs.Length:N0}件をキャンセルしています…";
            await Dispatcher.Yield(DispatcherPriority.Render);
            if (_enhancementWorkspaceQueuedJobsBatchCancelSupported)
            {
                foreach (string[] ids in optimisticJobIds.Chunk(
                    EnhancementQueuedJobsBatchLimit))
                {
                    attemptedCount += ids.Length;
                    EnhancementApiResponse response =
                        await SendTrackedEnhancementWorkspaceMutationAsync(
                            () => SendIdempotentEnhancementMutationAsync(
                                HttpMethod.Delete,
                                "api/enhance/jobs/queued/batch",
                                new { ids }));
                    if (!response.Ok
                        || !TryParseQueuedJobsBatchCancelResponse(
                            response.Payload,
                            ids.Length,
                            out int batchCanceledCount,
                            out int newlyProtectedCount,
                            out int missingCount))
                    {
                        failedCount += ids.Length;
                        firstError ??= response.Ok
                            ? "QUEUED_JOBS_BATCH_RESPONSE_INVALID"
                            : EnhancementApiErrorCode(response);
                        break;
                    }
                    canceledCount += batchCanceledCount;
                    protectedCount += newlyProtectedCount;
                    alreadyMissingCount += missingCount;
                }
                unattemptedCount = queuedJobs.Length - attemptedCount;
            }
            else
            {
                for (int index = 0; index < queuedJobs.Length; index++)
                {
                    EnhancementWorkspaceJobView job = queuedJobs[index];
                    attemptedCount++;
                    EnhancementApiResponse response =
                        await SendTrackedEnhancementWorkspaceMutationAsync(
                            () => SendEnhancementApiAsync(
                                HttpMethod.Post,
                                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel"));
                    if (response.Ok)
                    {
                        canceledCount++;
                    }
                    else
                    {
                        failedCount++;
                        firstError ??= response.Error;
                    }
                    if ((index + 1) % 25 == 0
                        && index + 1 < queuedJobs.Length)
                    {
                        EnhancementJobsStatusText.Text =
                            $"待機中の{operationLabel}をキャンセル中… {index + 1:N0}/{queuedJobs.Length:N0}件";
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
            }
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }

            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
            string operationFilterLabel = EnhancementWorkspaceOperationFilterLabel();
            EnhancementJobsStatusText.Text =
                $"現在の種類フィルター「{operationFilterLabel}」で表示された待機中 {canceledCount:N0}件をキャンセルしました。実行中のジョブは変更していません。"
                + (protectedCount > 0
                    ? $" 保護対象 {protectedCount:N0}件は残しました。"
                    : "")
                + (alreadyMissingCount > 0
                    ? $" {alreadyMissingCount:N0}件はすでに待機列から移動していました。"
                    : "")
                + (failedCount > 0
                    ? $" {failedCount:N0}件は失敗しました。{firstError}"
                    : "")
                + (unattemptedCount > 0
                    ? $" 残り {unattemptedCount:N0}件は安全のため未実行です。"
                    : "");
        }
        finally
        {
            EndOptimisticBulkPresentation(
                optimisticJobIds,
                optimisticVisibleJobs,
                revealRows: true);
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private static bool TryParseQueuedJobsBatchCancelResponse(
        JsonElement? payload,
        int requestedCount,
        out int canceledCount,
        out int protectedCount,
        out int missingCount)
    {
        canceledCount = 0;
        protectedCount = 0;
        missingCount = 0;
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("canceledCount", out JsonElement canceled)
            || !canceled.TryGetInt32(out canceledCount)
            || !root.TryGetProperty("protectedCount", out JsonElement protectedElement)
            || !protectedElement.TryGetInt32(out protectedCount)
            || !root.TryGetProperty("missingCount", out JsonElement missingElement)
            || !missingElement.TryGetInt32(out missingCount)
            || canceledCount < 0
            || protectedCount < 0
            || missingCount < 0
            || canceledCount + protectedCount + missingCount != requestedCount)
        {
            canceledCount = 0;
            protectedCount = 0;
            missingCount = 0;
            return false;
        }
        return true;
    }

    private async void RetryAllFailedEnhancementJobs_Click(
        object sender,
        RoutedEventArgs e)
        => await RetryAllFailedEnhancementJobsAsync();

    private async Task<int> RetryAllFailedEnhancementJobsAsync()
        => await RetryAllTerminalEnhancementJobsAsync("failed");

    private async void RetryAllCanceledEnhancementJobs_Click(
        object sender,
        RoutedEventArgs e)
        => await RetryAllTerminalEnhancementJobsAsync("canceled");

    private async Task<int> RetryAllCanceledEnhancementJobsAsync()
        => await RetryAllTerminalEnhancementJobsAsync("canceled");

    private async Task<int> RetryAllTerminalEnhancementJobsAsync(
        string terminalStatus)
    {
        if (_enhancementWorkspaceTerminalHistoryBatchRetrySupported)
        {
            return await RetryAllTerminalEnhancementJobsBatchAsync(
                terminalStatus);
        }
        if (!EnhancementWorkspaceHasCompleteTerminalHistory(terminalStatus))
        {
            EnhancementJobsStatusText.Text =
                "画面外の履歴を『全部』から漏らさないため、Companionの一括リトライ対応後に実行できます。";
            return 0;
        }
        int terminalCount = _enhancementWorkspaceJobs.Count(job =>
            job.Status == terminalStatus
            && MatchesEnhancementWorkspaceOperationFilter(job));
        EnhancementWorkspaceJobView[] terminalJobs = _enhancementWorkspaceJobs
            .Where(job =>
                job.Status == terminalStatus
                && MatchesEnhancementWorkspaceOperationFilter(job)
                && job.CanRetry)
            .OrderBy(static job => job.CreatedAt)
            .ThenBy(static job => job.ApiOrdinal)
            .ToArray();
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || terminalJobs.Length == 0)
        {
            return 0;
        }

        bool removeOriginalAfterSuccess =
            terminalStatus is "failed" or "canceled";
        string terminalLabel = terminalStatus == "failed" ? "失敗" : "キャンセル";
        int protectedCount = terminalCount - terminalJobs.Length;
        if (!ConfirmEnhancementJobsBulkAction(
                terminalStatus,
                retry: true,
                terminalJobs.Length,
                protectedCount))
        {
            return 0;
        }
        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        HashSet<string> optimisticJobIds = terminalJobs
            .Select(static job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        EnhancementWorkspaceJobView[] optimisticVisibleJobs =
            BeginOptimisticBulkPresentation(
                optimisticJobIds,
                hideRows: removeOriginalAfterSuccess);
        RefreshEnhancementQueueBulkControls();
        Button retryButton = terminalStatus == "failed"
            ? EnhancementJobsRetryFailedButton
            : EnhancementJobsRetryCanceledButton;
        retryButton.Content = "再試行を受付中…";
        long generation = _enhancementWorkspaceGeneration;
        int retriedCount = 0;
        int pendingCount = 0;
        int failedCount = 0;
        string? failure = null;
        int? failureStatus = null;
        EnhancementJobsStatusText.Text =
            $"{terminalLabel}したJob {terminalJobs.Length:N0}件を保存済み設定で再試行しています…";
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            long? retryBatchInventoryRevisionBeforeMutation =
                _enhancementWorkspaceLastHealthInventoryRevision;
            DurableEnhancementBatchResponse retryBatch =
                await TrySendDurableEnhancementRetryBatchAsync(terminalJobs);
            EnhancementApiResponse? retryBatchMutation = retryBatch.Responses
                .FirstOrDefault(EnhancementWorkspaceMutationMayHaveCommitted);
            if (retryBatchMutation is EnhancementApiResponse trackedRetry)
            {
                NoteEnhancementWorkspaceMutationDebt(
                    trackedRetry,
                    retryBatchInventoryRevisionBeforeMutation);
            }
            for (int index = 0; index < terminalJobs.Length; index++)
            {
                EnhancementWorkspaceJobView job = terminalJobs[index];
                EnhancementApiResponse retry = retryBatch.Responses[index];
                if (retry.SavedForDelivery)
                {
                    pendingCount++;
                    continue;
                }
                if (!retry.Ok)
                {
                    failedCount++;
                    failure ??= EnhancementApiErrorCode(retry);
                    failureStatus ??= retry.StatusCode;
                    continue;
                }

                if (removeOriginalAfterSuccess
                    && !_usingDefaultModalEnhancementSender)
                {
                    EnhancementApiResponse remove =
                        await SendTrackedEnhancementWorkspaceMutationAsync(
                            () => SendEnhancementApiAsync(
                                HttpMethod.Delete,
                                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}"));
                    if (!remove.Ok)
                    {
                        failedCount++;
                        failure ??= EnhancementApiErrorCode(remove);
                        failureStatus ??= remove.StatusCode;
                        continue;
                    }
                }
                retriedCount++;
                if ((index + 1) % 25 == 0
                    && index + 1 < terminalJobs.Length)
                {
                    EnhancementJobsStatusText.Text =
                        $"保存済み設定で再試行中… {index + 1:N0}/{terminalJobs.Length:N0}件";
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            if (generation == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                await RefreshEnhancementJobsWorkspaceAsync(
                    generation,
                    isPoll: false);
                int acceptedCount = retriedCount + pendingCount;
                string completedDetail = removeOriginalAfterSuccess
                    ? $"確認済み{retriedCount:N0}件の元{terminalLabel}履歴を消しました。"
                    : $"確認済み{retriedCount:N0}件を新しいジョブとして追加しました。";
                EnhancementJobsStatusText.Text =
                    $"{terminalLabel}したJobを保存済み設定で{acceptedCount:N0}件受付。{completedDetail}"
                    + (pendingCount > 0
                        ? $" {pendingCount:N0}件は登録確認中なので元履歴を残しています。"
                        : "")
                    + (protectedCount > 0
                        ? $" 安全に再試行できない保護対象 {protectedCount:N0}件は残しました。"
                        : "")
                    + (failedCount > 0
                        ? $" {failedCount:N0}件は失敗しました（{failure}）。"
                        : "");
            }
            string? operationError = failure
                ?? (pendingCount > 0 ? "RETRY_SAVED_FOR_DELIVERY" : null);
            AibosOperationLog.Write(
                $"{terminalStatus}_jobs_retry_all_saved",
                operationError is null ? "completed" : "partial",
                operationWatch.ElapsedMilliseconds,
                failureStatus ?? (pendingCount > 0 ? 202 : null),
                operationError,
                itemCount: retriedCount + pendingCount);
            return retriedCount;
        }
        finally
        {
            EndOptimisticBulkPresentation(
                optimisticJobIds,
                optimisticVisibleJobs,
                revealRows: removeOriginalAfterSuccess);
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private async Task<int> RetryAllTerminalEnhancementJobsBatchAsync(
        string terminalStatus)
    {
        int totalStatusCount = EnhancementWorkspaceTotalStatusCount(
            terminalStatus);
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || totalStatusCount == 0)
        {
            return 0;
        }

        string terminalLabel = terminalStatus == "failed"
            ? "失敗"
            : "キャンセル";
        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        HashSet<string> optimisticJobIds = new(StringComparer.Ordinal);
        EnhancementWorkspaceJobView[] optimisticVisibleJobs = [];
        Button retryButton = terminalStatus == "failed"
            ? EnhancementJobsRetryFailedButton
            : EnhancementJobsRetryCanceledButton;
        RefreshEnhancementQueueBulkControls();
        retryButton.Content = "対象を確認しています…";
        long generation = _enhancementWorkspaceGeneration;
        string? failure = null;
        int? failureStatus = null;
        int acceptedCount = 0;
        int retriedCount = 0;
        int replayedCount = 0;
        int protectedCount = 0;
        int missingCount = 0;
        int itemFailedCount = 0;
        int pendingCount = 0;
        int unconfirmedCount = 0;
        int attemptedCount = 0;
        int unattemptedCount = 0;
        try
        {
            EnhancementJobsStatusText.Text =
                $"{terminalLabel}履歴の再試行対象を確定しています…";
            await Dispatcher.Yield(DispatcherPriority.Render);
            EnhancementApiResponse plan = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/jobs/terminal/targets",
                new
                {
                    status = terminalStatus,
                    operation = _enhancementWorkspaceOperationFilter,
                    action = "retry",
                },
                maxResponseBytes: 600_000);
            if (!plan.Ok
                || !TryParseTerminalHistoryTargetPlanResponse(
                    plan.Payload,
                    out string[] retryIds,
                    out protectedCount))
            {
                failure = plan.Ok
                    ? "TERMINAL_RETRY_TARGET_PLAN_INVALID"
                    : EnhancementApiErrorCode(plan);
                failureStatus = plan.StatusCode;
                EnhancementJobsStatusText.Text =
                    $"{terminalLabel}履歴の再試行対象を安全に確定できませんでした。履歴とキューは変更していません（{failure}）。";
                AibosOperationLog.Write(
                    $"{terminalStatus}_jobs_retry_all_batch",
                    "failed",
                    operationWatch.ElapsedMilliseconds,
                    failureStatus,
                    failure,
                    itemCount: 0);
                return 0;
            }

            if (retryIds.Length == 0)
            {
                EnhancementJobsStatusText.Text = protectedCount > 0
                    ? $"{terminalLabel}履歴はすべて再試行保護対象なので変更しませんでした。"
                    : $"再試行できる{terminalLabel}履歴はありません。";
                return 0;
            }
            if (!ConfirmEnhancementJobsBulkAction(
                    terminalStatus,
                    retry: true,
                    retryIds.Length,
                    protectedCount))
            {
                return 0;
            }

            optimisticJobIds = retryIds.ToHashSet(StringComparer.Ordinal);
            optimisticVisibleJobs = BeginOptimisticBulkPresentation(
                optimisticJobIds,
                hideRows: true);
            retryButton.Content = "再試行を一括受付中…";
            EnhancementJobsStatusText.Text =
                $"{terminalLabel}したJob {retryIds.Length:N0}件を保存済み設定で一括再試行しています…";
            await Dispatcher.Yield(DispatcherPriority.Render);

            foreach (string[] ids in retryIds.Chunk(
                EnhancementTerminalHistoryBatchLimit))
            {
                attemptedCount += ids.Length;
                string batchRequestId = Guid.NewGuid().ToString("N");
                EnhancementApiResponse retry =
                    await SendTrackedEnhancementWorkspaceMutationAsync(
                        () => SendIdempotentEnhancementMutationAsync(
                            HttpMethod.Post,
                            "api/enhance/jobs/terminal/retry",
                            new
                            {
                                status = terminalStatus,
                                ids,
                                batchRequestId,
                            }));
                if (retry.SavedForDelivery)
                {
                    pendingCount += ids.Length;
                    failure ??= "RETRY_SAVED_FOR_DELIVERY";
                    failureStatus ??= retry.StatusCode;
                    break;
                }
                if (!retry.Ok
                    || !TryParseTerminalHistoryBatchRetryResponse(
                        retry.Payload,
                        batchRequestId,
                        ids,
                        out int batchRetriedCount,
                        out int batchReplayedCount,
                        out int batchProtectedCount,
                        out int batchMissingCount,
                        out int batchFailedCount))
                {
                    unconfirmedCount += ids.Length;
                    failure ??= retry.Ok
                        ? "TERMINAL_RETRY_BATCH_RESPONSE_INVALID"
                        : EnhancementApiErrorCode(retry);
                    failureStatus ??= retry.StatusCode;
                    break;
                }

                retriedCount += batchRetriedCount;
                replayedCount += batchReplayedCount;
                protectedCount += batchProtectedCount;
                missingCount += batchMissingCount;
                itemFailedCount += batchFailedCount;
                acceptedCount += batchRetriedCount + batchReplayedCount;
            }
            unattemptedCount = retryIds.Length - attemptedCount;

            if (generation == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                await RefreshEnhancementJobsWorkspaceAsync(
                    generation,
                    isPoll: false);
                string resultSummary = acceptedCount > 0
                    ? $"{terminalLabel}したJob {acceptedCount:N0}件を保存済み設定で受付し、元履歴を消しました。"
                    : $"{terminalLabel}したJobを再試行できませんでした。元履歴は残しています。";
                EnhancementJobsStatusText.Text = resultSummary
                    + (replayedCount > 0
                        ? $" うち{replayedCount:N0}件は同じ一括要求の確認済み結果です。"
                        : "")
                    + (protectedCount > 0
                        ? $" 保護対象 {protectedCount:N0}件は残しました。"
                        : "")
                    + (missingCount > 0
                        ? $" {missingCount:N0}件はすでに履歴から移動していました。"
                        : "")
                    + (itemFailedCount > 0
                        ? $" {itemFailedCount:N0}件は検証または登録に失敗し、元履歴を残しました。"
                        : "")
                    + (pendingCount > 0
                        ? $" {pendingCount:N0}件は登録確認中なので元履歴を残しています。"
                        : "")
                    + (unconfirmedCount > 0
                        ? $" {unconfirmedCount:N0}件は応答を確認できないため状態を再読込しました（{failure}）。"
                        : "")
                    + (unattemptedCount > 0
                        ? $" 残り {unattemptedCount:N0}件は安全のため未実行です。"
                        : "");
            }

            string? operationError = failure
                ?? (itemFailedCount > 0
                    ? "TERMINAL_RETRY_ITEM_FAILURE"
                    : null);
            AibosOperationLog.Write(
                $"{terminalStatus}_jobs_retry_all_batch",
                operationError is null ? "completed" : "partial",
                operationWatch.ElapsedMilliseconds,
                failureStatus,
                operationError,
                itemCount: acceptedCount);
            return acceptedCount;
        }
        finally
        {
            EndOptimisticBulkPresentation(
                optimisticJobIds,
                optimisticVisibleJobs,
                revealRows: true);
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private async void ClearAllFailedEnhancementJobs_Click(
        object sender,
        RoutedEventArgs e)
        => await ClearAllFailedEnhancementJobsAsync();

    private async Task<int> ClearAllFailedEnhancementJobsAsync()
        => await ClearAllTerminalEnhancementJobsAsync("failed");

    private async void ClearAllCanceledEnhancementJobs_Click(
        object sender,
        RoutedEventArgs e)
        => await ClearAllTerminalEnhancementJobsAsync("canceled");

    private async Task<int> ClearAllCanceledEnhancementJobsAsync()
        => await ClearAllTerminalEnhancementJobsAsync("canceled");

    private async Task<int> ClearAllTerminalEnhancementJobsAsync(
        string terminalStatus)
    {
        EnhancementWorkspaceJobView[] terminalJobs = _enhancementWorkspaceJobs
            .Where(job =>
                job.Status == terminalStatus
                && MatchesEnhancementWorkspaceOperationFilter(job))
            .ToArray();
        string[] dismissibleIds = terminalJobs
            .Where(static job => job.CanDismiss)
            .OrderBy(static job => job.CreatedAt)
            .ThenBy(static job => job.ApiOrdinal)
            .Select(static job => job.Id)
            .ToArray();
        int protectedCount = terminalJobs.Length - dismissibleIds.Length;
        int loadedStatusCount = _enhancementWorkspaceJobs.Count(job =>
            job.Status == terminalStatus);
        int totalStatusCount = EnhancementWorkspaceTotalStatusCount(
            terminalStatus);
        bool useExactTargetPlan =
            _enhancementWorkspaceTerminalHistoryTargetsSupported;
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || useExactTargetPlan && totalStatusCount == 0
            || !useExactTargetPlan && dismissibleIds.Length == 0)
        {
            return 0;
        }
        if (!useExactTargetPlan && loadedStatusCount != totalStatusCount)
        {
            EnhancementJobsStatusText.Text =
                "画面外の履歴を『全部』から漏らさないため、Companion更新後に一括消去できます。";
            return 0;
        }

        string terminalLabel = terminalStatus == "failed" ? "失敗" : "キャンセル";
        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        HashSet<string> optimisticJobIds = new(StringComparer.Ordinal);
        EnhancementWorkspaceJobView[] optimisticVisibleJobs = [];
        RefreshEnhancementQueueBulkControls();
        Button clearButton = terminalStatus == "failed"
            ? EnhancementJobsClearFailedButton
            : EnhancementJobsClearCanceledButton;
        clearButton.Content = "対象を確認しています…";
        long generation = _enhancementWorkspaceGeneration;
        int clearedCount = 0;
        int failedCount = 0;
        int alreadyMissingCount = 0;
        int attemptedCount = 0;
        int unattemptedCount = 0;
        string? failure = null;
        int? failureStatus = null;
        try
        {
            if (useExactTargetPlan)
            {
                EnhancementJobsStatusText.Text =
                    $"{terminalLabel}履歴の対象を確定しています…";
                await Dispatcher.Yield(DispatcherPriority.Render);
                EnhancementApiResponse plan = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    "api/enhance/jobs/terminal/targets",
                    new
                    {
                        status = terminalStatus,
                        operation = _enhancementWorkspaceOperationFilter,
                        action = "dismiss",
                    },
                    maxResponseBytes: 600_000);
                if (!plan.Ok
                    || !TryParseTerminalHistoryTargetPlanResponse(
                        plan.Payload,
                        out dismissibleIds,
                        out protectedCount))
                {
                    failure = plan.Ok
                        ? "TERMINAL_HISTORY_TARGET_PLAN_INVALID"
                        : EnhancementApiErrorCode(plan);
                    failureStatus = plan.StatusCode;
                    EnhancementJobsStatusText.Text =
                        $"{terminalLabel}履歴の対象を安全に確定できませんでした。履歴は変更していません（{failure}）。";
                    AibosOperationLog.Write(
                        $"{terminalStatus}_jobs_clear_all",
                        "failed",
                        operationWatch.ElapsedMilliseconds,
                        failureStatus,
                        failure,
                        itemCount: 0);
                    return 0;
                }
            }

            if (dismissibleIds.Length == 0)
            {
                EnhancementJobsStatusText.Text = protectedCount > 0
                    ? $"{terminalLabel}履歴はすべて保護対象なので変更しませんでした。"
                    : $"消去できる{terminalLabel}履歴はありません。";
                return 0;
            }
            if (!ConfirmEnhancementJobsBulkAction(
                    terminalStatus,
                    retry: false,
                    dismissibleIds.Length,
                    protectedCount))
            {
                return 0;
            }

            optimisticJobIds = dismissibleIds.ToHashSet(StringComparer.Ordinal);
            optimisticVisibleJobs = BeginOptimisticBulkPresentation(
                optimisticJobIds,
                hideRows: true);
            clearButton.Content = "履歴を消しています…";
            EnhancementJobsStatusText.Text =
                $"{terminalLabel}履歴 {dismissibleIds.Length:N0}件を消しています…";
            await Dispatcher.Yield(DispatcherPriority.Render);
            if (_enhancementWorkspaceTerminalHistoryBatchDismissSupported)
            {
                foreach (string[] ids in dismissibleIds.Chunk(
                    EnhancementTerminalHistoryBatchLimit))
                {
                    attemptedCount += ids.Length;
                    EnhancementApiResponse remove =
                        await SendTrackedEnhancementWorkspaceMutationAsync(
                            () => SendIdempotentEnhancementMutationAsync(
                                HttpMethod.Delete,
                                "api/enhance/jobs/terminal",
                                new { status = terminalStatus, ids }));
                    if (!remove.Ok
                        || !TryParseTerminalHistoryBatchDismissResponse(
                            remove.Payload,
                            ids.Length,
                            out int dismissedCount,
                            out int newlyProtectedCount,
                            out int missingCount))
                    {
                        failedCount += ids.Length;
                        failure ??= remove.Ok
                            ? "TERMINAL_HISTORY_BATCH_RESPONSE_INVALID"
                            : EnhancementApiErrorCode(remove);
                        failureStatus ??= remove.StatusCode;
                        break;
                    }
                    clearedCount += dismissedCount;
                    protectedCount += newlyProtectedCount;
                    alreadyMissingCount += missingCount;
                }
                unattemptedCount = dismissibleIds.Length - attemptedCount;
            }
            else
            {
                for (int index = 0; index < dismissibleIds.Length; index++)
                {
                    string id = dismissibleIds[index];
                    attemptedCount++;
                    EnhancementApiResponse remove =
                        await SendTrackedEnhancementWorkspaceMutationAsync(
                            () => SendIdempotentEnhancementMutationAsync(
                                HttpMethod.Delete,
                                $"api/enhance/jobs/{Uri.EscapeDataString(id)}"));
                    if (!remove.Ok)
                    {
                        failedCount++;
                        failure ??= EnhancementApiErrorCode(remove);
                        failureStatus ??= remove.StatusCode;
                        if (remove.StatusCode is 0 or 401 or 403
                            || remove.StatusCode >= 500)
                        {
                            break;
                        }
                        continue;
                    }
                    clearedCount++;
                    if ((index + 1) % 25 == 0
                        && index + 1 < dismissibleIds.Length)
                    {
                        EnhancementJobsStatusText.Text =
                            $"{terminalLabel}履歴を削除中… {index + 1:N0}/{dismissibleIds.Length:N0}件";
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
            }
            unattemptedCount = dismissibleIds.Length - attemptedCount;

            if (generation == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                await RefreshEnhancementJobsWorkspaceAsync(
                    generation,
                    isPoll: false);
                string resultSummary = clearedCount == 0 && failedCount > 0
                    ? $"{terminalLabel}履歴を削除できませんでした。元画像と出力ファイルは変更していません。"
                    : $"{terminalLabel}履歴 {clearedCount:N0}件を消しました。元画像と出力ファイルは変更していません。";
                EnhancementJobsStatusText.Text =
                    resultSummary
                    + (protectedCount > 0
                        ? $" 保護対象 {protectedCount:N0}件は残しました。"
                        : "")
                    + (alreadyMissingCount > 0
                        ? $" {alreadyMissingCount:N0}件はすでに履歴から消えていました。"
                        : "")
                    + (failedCount > 0
                        ? $" {failedCount:N0}件は削除できませんでした（{failure}）。"
                        : "")
                    + (unattemptedCount > 0
                        ? $" 残り {unattemptedCount:N0}件は安全のため未実行です。"
                        : "");
            }
            AibosOperationLog.Write(
                $"{terminalStatus}_jobs_clear_all",
                failure is null ? "completed" : "partial",
                operationWatch.ElapsedMilliseconds,
                failureStatus,
                failure,
                itemCount: clearedCount);
            return clearedCount;
        }
        finally
        {
            EndOptimisticBulkPresentation(
                optimisticJobIds,
                optimisticVisibleJobs,
                revealRows: true);
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
    }

    private static bool TryParseTerminalHistoryTargetPlanResponse(
        JsonElement? payload,
        out string[] ids,
        out int protectedCount)
    {
        ids = [];
        protectedCount = 0;
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("targetCount", out JsonElement targetCountElement)
            || !targetCountElement.TryGetInt32(out int targetCount)
            || targetCount < 0
            || targetCount > 10_000
            || !root.TryGetProperty("protectedCount", out JsonElement protectedElement)
            || !protectedElement.TryGetInt32(out protectedCount)
            || protectedCount < 0
            || !root.TryGetProperty("ids", out JsonElement idsElement)
            || idsElement.ValueKind != JsonValueKind.Array
            || idsElement.GetArrayLength() != targetCount)
        {
            protectedCount = 0;
            return false;
        }

        var parsedIds = new string[targetCount];
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement idElement in idsElement.EnumerateArray())
        {
            if (idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not string id
                || id.Length is < 1 or > 255
                || !uniqueIds.Add(id))
            {
                protectedCount = 0;
                return false;
            }
            parsedIds[index++] = id;
        }
        ids = parsedIds;
        return true;
    }

    private static bool TryParseTerminalHistoryBatchDismissResponse(
        JsonElement? payload,
        int requestedCount,
        out int dismissedCount,
        out int protectedCount,
        out int missingCount)
    {
        dismissedCount = 0;
        protectedCount = 0;
        missingCount = 0;
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("dismissedCount", out JsonElement dismissedElement)
            || !dismissedElement.TryGetInt32(out dismissedCount)
            || !root.TryGetProperty("protectedCount", out JsonElement protectedElement)
            || !protectedElement.TryGetInt32(out protectedCount)
            || !root.TryGetProperty("missingCount", out JsonElement missingElement)
            || !missingElement.TryGetInt32(out missingCount)
            || dismissedCount < 0
            || protectedCount < 0
            || missingCount < 0
            || dismissedCount + protectedCount + missingCount != requestedCount)
        {
            dismissedCount = 0;
            protectedCount = 0;
            missingCount = 0;
            return false;
        }
        return true;
    }

    private static bool TryParseTerminalHistoryBatchRetryResponse(
        JsonElement? payload,
        string expectedBatchRequestId,
        IReadOnlyCollection<string> expectedIds,
        out int retriedCount,
        out int replayedCount,
        out int protectedCount,
        out int missingCount,
        out int failedCount)
    {
        retriedCount = 0;
        replayedCount = 0;
        protectedCount = 0;
        missingCount = 0;
        failedCount = 0;
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(
                "batchRequestId",
                out JsonElement batchRequestIdElement)
            || batchRequestIdElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                batchRequestIdElement.GetString(),
                expectedBatchRequestId,
                StringComparison.Ordinal)
            || !root.TryGetProperty(
                "batchReplayed",
                out JsonElement batchReplayedElement)
            || batchReplayedElement.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty(
                "requestedCount",
                out JsonElement requestedCountElement)
            || !requestedCountElement.TryGetInt32(out int requestedCount)
            || requestedCount != expectedIds.Count
            || !TryReadNonNegativeBatchCount(
                root,
                "retriedCount",
                out retriedCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "replayedCount",
                out replayedCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "dismissedSourceCount",
                out int dismissedSourceCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "retainedSourceCount",
                out int retainedSourceCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "protectedCount",
                out protectedCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "missingCount",
                out missingCount)
            || !TryReadNonNegativeBatchCount(
                root,
                "failedCount",
                out failedCount)
            || retriedCount > requestedCount
            || replayedCount > requestedCount
            || protectedCount > requestedCount
            || missingCount > requestedCount
            || failedCount > requestedCount
            || retriedCount + replayedCount + protectedCount
                + missingCount + failedCount != requestedCount
            || dismissedSourceCount != retriedCount + replayedCount
            || retainedSourceCount
                != protectedCount + missingCount + failedCount
            || batchReplayedElement.GetBoolean() && retriedCount != 0
            || !root.TryGetProperty("results", out JsonElement resultsElement)
            || resultsElement.ValueKind != JsonValueKind.Array
            || resultsElement.GetArrayLength() != dismissedSourceCount
            || !root.TryGetProperty("failures", out JsonElement failuresElement)
            || failuresElement.ValueKind != JsonValueKind.Array
            || failuresElement.GetArrayLength() != retainedSourceCount)
        {
            retriedCount = 0;
            replayedCount = 0;
            protectedCount = 0;
            missingCount = 0;
            failedCount = 0;
            return false;
        }

        var requested = expectedIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count != expectedIds.Count)
            return false;
        var accounted = new HashSet<string>(StringComparer.Ordinal);
        int observedRetriedCount = 0;
        int observedReplayedCount = 0;
        foreach (JsonElement result in resultsElement.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object
                || !TryGetBoundedBatchString(
                    result,
                    "sourceJobId",
                    255,
                    out string sourceJobId)
                || !requested.Contains(sourceJobId)
                || !accounted.Add(sourceJobId)
                || !TryGetBoundedBatchString(
                    result,
                    "jobId",
                    255,
                    out _)
                || !result.TryGetProperty(
                    "replayed",
                    out JsonElement replayedElement)
                || replayedElement.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            if (replayedElement.GetBoolean())
                observedReplayedCount++;
            else
                observedRetriedCount++;
        }

        int observedProtectedCount = 0;
        int observedMissingCount = 0;
        int observedFailedCount = 0;
        foreach (JsonElement failure in failuresElement.EnumerateArray())
        {
            if (failure.ValueKind != JsonValueKind.Object
                || !TryGetBoundedBatchString(
                    failure,
                    "sourceJobId",
                    255,
                    out string sourceJobId)
                || !requested.Contains(sourceJobId)
                || !accounted.Add(sourceJobId)
                || !TryGetBoundedBatchString(
                    failure,
                    "outcome",
                    16,
                    out string outcome)
                || outcome is not ("protected" or "missing" or "failed")
                || !TryGetBoundedBatchString(
                    failure,
                    "code",
                    128,
                    out _)
                || !failure.TryGetProperty("status", out JsonElement status)
                || !status.TryGetInt32(out int statusCode)
                || statusCode is < 400 or > 599)
            {
                return false;
            }
            switch (outcome)
            {
                case "protected":
                    observedProtectedCount++;
                    break;
                case "missing":
                    observedMissingCount++;
                    break;
                default:
                    observedFailedCount++;
                    break;
            }
        }

        return accounted.Count == requestedCount
            && observedRetriedCount == retriedCount
            && observedReplayedCount == replayedCount
            && observedProtectedCount == protectedCount
            && observedMissingCount == missingCount
            && observedFailedCount == failedCount;
    }

    private static bool TryReadNonNegativeBatchCount(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement element)
            && element.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryGetBoundedBatchString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not string parsed
            || parsed.Length is < 1
            || parsed.Length > maximumLength)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private bool ConfirmEnhancementJobsBulkAction(
        string terminalStatus,
        bool retry,
        int actionCount,
        int protectedCount)
    {
        string terminalLabel = terminalStatus == "failed" ? "失敗" : "キャンセル済み";
        string actionLabel = retry ? "全部リトライ" : "全部消す";
        string detail = retry
            ? "各Jobに保存されたPrompt・STEP・CFG・Seed等の設定で、新しい待機ジョブを追加します。元画像と出力ファイルは変更しません。"
            : "対象の履歴だけを消します。元画像と出力ファイルは削除しません。";
        string operationLabel = EnhancementWorkspaceOperationFilterLabel();
        string message =
            $"現在の種類フィルター「{operationLabel}」に一致する{terminalLabel}の履歴 {actionCount:N0}件を{actionLabel}しますか？\n\n{detail}"
            + (protectedCount > 0
                ? $"\n\nfuture・malformed・read-only等の保護対象 {protectedCount:N0}件は残します。"
                : "");
        string title = $"{terminalLabel}を{actionLabel}";
        return _confirmEnhancementJobsBulkActionForSmoke?.Invoke(title, message)
            ?? MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButton.YesNo,
                retry ? MessageBoxImage.Question : MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async void RerunPhotorealJob_Click(object sender, RoutedEventArgs e)
        => await RerunPhotorealJobAsync(sender, enqueueNext: false);

    private async void RerunPhotorealJobNext_Click(object sender, RoutedEventArgs e)
        => await RerunPhotorealJobAsync(sender, enqueueNext: true);

    private async Task RerunPhotorealJobAsync(object sender, bool enqueueNext)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || (enqueueNext
                ? !job.CanRerunNextWithCurrentSettings
                : !job.CanRerunWithCurrentSettings)
            || !TryResolveEnhancementWorkspaceInput(
                job,
                out string sourceIdentity))
        {
            if (sender is Button { Tag: EnhancementWorkspaceJobView })
                EnhancementJobsStatusText.Text = "元画像を検証できないため、現在設定で再実写化できません。";
            return;
        }
        if (!TryResolvePhotorealSeed(out int? photorealSeed, out string seedError))
        {
            EnhancementJobsStatusText.Text = seedError;
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            Func<JsonElement, string?>? healthValidator =
                CreateImageEnhancementHealthValidator(
                    "photoreal",
                    enqueueNext,
                    requiresPhotorealSeedControl: photorealSeed.HasValue);

            ModalPhotorealRequestSettings settings;
            try
            {
                settings = await ResolvePhotorealRequestSettingsAsync(
                    CurrentModalPhotorealRequestSettings(),
                    sourceIdentity);
            }
            catch (InvalidOperationException ex)
            {
                EnhancementJobsStatusText.Text = ex.Message;
                return;
            }
            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                CreatePhotorealRequestBody(
                    sourceIdentity,
                    settings,
                    photorealSeed,
                    enqueueNext ? "next" : "last"),
                enqueueNext ? "next" : "last",
                healthValidator: healthValidator,
                recoverySourceIdentity: sourceIdentity);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (response.SavedForDelivery)
            {
                EnhancementJobsStatusText.Text =
                    "再実写化の予約を保存しました。Jobsへの登録を継続しています。";
                return;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            EnhancementJobsStatusText.Text = enqueueNext
                ? "現在のPositive・Negative・LoRA・強さ・CFG・品質・解像度で、実写化を現在の処理の次へ追加しました。"
                : "現在のPositive・Negative・LoRA・強さ・CFG・品質・解像度で再実写化を待機列へ追加しました。";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private sealed record MiniMaxH3VideoExplicitActionContext(
        long WorkspaceGeneration,
        EnhancementWorkspaceJobView Job,
        EnhancementVideoMutationProbe Probe,
        MiniMaxH3VideoWorkspaceSnapshot Snapshot,
        string ManagedOutputRoot,
        ExternalFileDropPrePublishGuard? SourcePathExternalGuard,
        ExternalFileDropPrePublishGuard? SourceIdExternalGuard);

    private sealed record MiniMaxH3VideoExplicitActionValidation(
        VideoSourceChoice? Source,
        long SourceIdentitySizeBytes,
        long DisplayPathSizeBytes,
        DateTime SourceIdentityLastWriteUtc,
        DateTime DisplayPathLastWriteUtc,
        string Error)
    {
        internal bool IsValid => Source is not null
            && string.IsNullOrWhiteSpace(Error);
    }

    private static MiniMaxH3VideoExplicitActionValidation
        InvalidMiniMaxH3VideoExplicitAction(string error)
        => new(
            null,
            0,
            0,
            default,
            default,
            error);

    private bool TryCaptureMiniMaxH3VideoExplicitActionContext(
        EnhancementWorkspaceJobView job,
        out MiniMaxH3VideoExplicitActionContext? context)
    {
        context = null;
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || !_enhancementWorkspaceJobs.Any(candidate =>
                ReferenceEquals(candidate, job))
            || job.Operation != "video"
            || job.Status != "succeeded"
            || !job.VideoMutationSafe
            || job.MiniMaxH3VideoSnapshot is not { } snapshot
            || !job.TryGetVideoMutationProbe(
                out EnhancementVideoMutationProbe? probe)
            || probe is null)
        {
            return false;
        }

        if (!TryCaptureMiniMaxH3VideoExternalSourceGuard(
                probe.SourcePath,
                out ExternalFileDropPrePublishGuard? sourcePathGuard)
            || !TryCaptureMiniMaxH3VideoExternalSourceGuard(
                probe.SourceId,
                out ExternalFileDropPrePublishGuard? sourceIdGuard))
        {
            return false;
        }

        context = new MiniMaxH3VideoExplicitActionContext(
            _enhancementWorkspaceGeneration,
            job,
            probe,
            snapshot,
            ResolvedManagedEnhancementOutputsRoot,
            sourcePathGuard,
            sourceIdGuard);
        return true;
    }

    private bool TryCaptureMiniMaxH3VideoExternalSourceGuard(
        string path,
        out ExternalFileDropPrePublishGuard? guard)
    {
        guard = null;
        if (!TryGetExternalFileDropSessionTile(path, out Tile externalTile))
            return true;
        if (!TryCaptureExternalFileDropPrePublishGuard(
                externalTile,
                out ExternalFileDropPrePublishGuard captured))
        {
            return false;
        }
        guard = captured;
        return true;
    }

    private bool IsMiniMaxH3VideoExternalSourceGuardCurrent(
        string path,
        ExternalFileDropPrePublishGuard? guard)
        => guard is ExternalFileDropPrePublishGuard captured
            ? IsExternalFileDropPrePublishGuardCurrent(captured)
            : !TryGetExternalFileDropSessionTile(path, out _);

    private bool IsMiniMaxH3VideoExplicitActionContextCurrent(
        MiniMaxH3VideoExplicitActionContext context)
        => context.WorkspaceGeneration == _enhancementWorkspaceGeneration
            && EnhancementJobsDialog.Visibility == Visibility.Visible
            && _enhancementWorkspaceJobs.Any(candidate =>
                ReferenceEquals(candidate, context.Job))
            && context.Job.Operation == "video"
            && context.Job.Status == "succeeded"
            && context.Job.VideoMutationSafe
            && Equals(
                context.Snapshot,
                context.Job.MiniMaxH3VideoSnapshot)
            && context.Job.TryGetVideoMutationProbe(
                out EnhancementVideoMutationProbe? currentProbe)
            && Equals(context.Probe, currentProbe)
            && IsMiniMaxH3VideoExternalSourceGuardCurrent(
                context.Probe.SourcePath,
                context.SourcePathExternalGuard)
            && IsMiniMaxH3VideoExternalSourceGuardCurrent(
                context.Probe.SourceId,
                context.SourceIdExternalGuard);

    private async Task<MiniMaxH3VideoExplicitActionValidation>
        ValidateMiniMaxH3VideoExplicitActionAsync(
            MiniMaxH3VideoExplicitActionContext context,
            CancellationToken token = default)
    {
        MiniMaxH3VideoExplicitActionValidation validation = await Task.Run(
            () => ValidateMiniMaxH3VideoExplicitActionOnWorker(
                context,
                validateContentHash: true,
                validateCurrentCanvas: true),
            token);
        if (!IsMiniMaxH3VideoExplicitActionContextCurrent(context))
        {
            return InvalidMiniMaxH3VideoExplicitAction(
                "確認中にJobsの内容が更新されました。もう一度選び直してください。");
        }
        return validation;
    }

    private MiniMaxH3VideoExplicitActionValidation
        ValidateMiniMaxH3VideoExplicitActionOnWorker(
            MiniMaxH3VideoExplicitActionContext context,
            bool validateContentHash,
            bool validateCurrentCanvas)
    {
        try
        {
            if (!TryResolveMiniMaxH3VideoActionPathOnWorker(
                    context.Probe.SourcePath,
                    context.SourcePathExternalGuard,
                    out string canonicalInput)
                || !TryValidateMiniMaxH3VideoActionInputOnWorker(
                    context.Probe,
                    canonicalInput,
                    validateContentHash,
                    validateCurrentCanvas,
                    out long inputSizeBytes,
                    out DateTime inputLastWriteUtc))
            {
                return InvalidMiniMaxH3VideoExplicitAction(
                    "この動画Jobの入力画像が変わったか見つからないため、同じ設定では再生成できません。");
            }

            if (!TryResolveMiniMaxH3ManagedOutputRootsOnWorker(
                    context.ManagedOutputRoot,
                    out string lexicalManagedRoot,
                    out string canonicalManagedRoot))
            {
                return InvalidMiniMaxH3VideoExplicitAction(
                    "この動画が使った入力画像を再確認できません。");
            }

            if (IsMiniMaxH3DisplayedManagedSourceOnWorker(
                    context.Probe.SourcePath,
                    canonicalInput,
                    lexicalManagedRoot,
                    canonicalManagedRoot))
            {
                return new MiniMaxH3VideoExplicitActionValidation(
                    new VideoSourceChoice(
                        canonicalInput,
                        canonicalInput,
                        null,
                        "生成画像",
                        UsesDisplayedFileDirectly: true),
                    inputSizeBytes,
                    inputSizeBytes,
                    inputLastWriteUtc,
                    inputLastWriteUtc,
                    "");
            }

            if (!TryResolveMiniMaxH3VideoActionPathOnWorker(
                    context.Probe.SourceId,
                    context.SourceIdExternalGuard,
                    out string canonicalSource)
                || !TryReadMiniMaxH3VideoActionSourceMetadataOnWorker(
                    canonicalSource,
                    out long sourceSizeBytes,
                    out DateTime sourceLastWriteUtc))
            {
                return InvalidMiniMaxH3VideoExplicitAction(
                    "この動画が使った元画像を再確認できません。");
            }

            if (!string.IsNullOrWhiteSpace(
                    context.Probe.SourceProducerJobId))
            {
                string lexicalInput = Path.GetFullPath(
                    context.Probe.SourcePath);
                if (!IsPathInside(lexicalInput, lexicalManagedRoot)
                    || !IsPathInside(canonicalInput, canonicalManagedRoot))
                {
                    return InvalidMiniMaxH3VideoExplicitAction(
                        "この動画が使った生成画像の所有範囲を確認できません。");
                }
                return new MiniMaxH3VideoExplicitActionValidation(
                    new VideoSourceChoice(
                        canonicalSource,
                        canonicalInput,
                        context.Probe.SourceProducerJobId,
                        "実写版",
                        UsesDisplayedFileDirectly: false),
                    sourceSizeBytes,
                    inputSizeBytes,
                    sourceLastWriteUtc,
                    inputLastWriteUtc,
                    "");
            }

            if (!EnhancementSourceIdentityComparer.Equals(
                    canonicalInput,
                    canonicalSource))
            {
                return InvalidMiniMaxH3VideoExplicitAction(
                    "この動画が使った元画像のidentityが変わりました。");
            }

            return new MiniMaxH3VideoExplicitActionValidation(
                new VideoSourceChoice(
                    canonicalInput,
                    canonicalInput,
                    null,
                    "Original",
                    UsesDisplayedFileDirectly: false),
                inputSizeBytes,
                inputSizeBytes,
                inputLastWriteUtc,
                inputLastWriteUtc,
                "");
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException)
        {
            return InvalidMiniMaxH3VideoExplicitAction(
                "この動画が使った入力画像を再確認できません。");
        }
    }

    private bool TryResolveMiniMaxH3VideoActionPathOnWorker(
        string path,
        ExternalFileDropPrePublishGuard? externalGuard,
        out string canonicalPath)
    {
        canonicalPath = "";
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (externalGuard is ExternalFileDropPrePublishGuard guard)
        {
            try
            {
                string resolved = Path.GetFullPath(
                    _resolveFinalPath(Path.GetFullPath(path)));
                if (!string.Equals(
                        resolved,
                        guard.CanonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                using var stream = new FileStream(
                    resolved,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4_096,
                    FileOptions.RandomAccess);
                if (!WindowsPathIdentity.TryGetFinalPath(
                        stream.SafeFileHandle,
                        out string openedCanonical)
                    || !string.Equals(
                        openedCanonical,
                        guard.CanonicalPath,
                        StringComparison.OrdinalIgnoreCase)
                    || !TryReadExternalFileDropSourceVersion(
                        stream.SafeFileHandle,
                        out ExternalFileDropSourceVersion current)
                    || !guard.SourceVersion.SameFileVersion(current))
                {
                    return false;
                }
                canonicalPath = openedCanonical;
                return true;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            string lexicalPath = Path.GetFullPath(path);
            canonicalPath = Path.GetFullPath(_resolveFinalPath(lexicalPath));
            return Path.IsPathFullyQualified(canonicalPath);
        }
        catch
        {
            canonicalPath = "";
            return false;
        }
    }

    private static bool TryReadMiniMaxH3VideoActionSourceMetadataOnWorker(
        string canonicalPath,
        out long sizeBytes,
        out DateTime lastWriteUtc)
    {
        sizeBytes = 0;
        lastWriteUtc = default;
        try
        {
            if (!SupportedImageExtensions.Contains(
                    Path.GetExtension(canonicalPath)))
            {
                return false;
            }
            var info = new FileInfo(canonicalPath);
            if (!info.Exists || info.Length <= 0)
                return false;
            sizeBytes = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            sizeBytes = 0;
            lastWriteUtc = default;
            return false;
        }
    }

    private static bool TryValidateMiniMaxH3VideoActionInputOnWorker(
        EnhancementVideoMutationProbe probe,
        string canonicalInput,
        bool validateContentHash,
        bool validateCurrentCanvas,
        out long sizeBytes,
        out DateTime lastWriteUtc)
    {
        sizeBytes = 0;
        lastWriteUtc = default;
        if (probe.SourceSize is not long expectedSize
            || probe.SourceMtimeMs is not double expectedMtimeMs
            || !TryReadMiniMaxH3VideoActionSourceMetadataOnWorker(
                canonicalInput,
                out long observedSize,
                out lastWriteUtc))
        {
            return false;
        }

        try
        {
            double currentMtimeMs = new DateTimeOffset(
                lastWriteUtc).ToUnixTimeMilliseconds();
            if (observedSize != expectedSize
                || Math.Abs(currentMtimeMs - expectedMtimeMs) > 1)
            {
                return false;
            }
            if (!validateContentHash)
            {
                sizeBytes = observedSize;
                return true;
            }

            using (var stream = new FileStream(
                canonicalInput,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan))
            {
                string currentSha256 = Convert.ToHexString(
                        SHA256.HashData(stream))
                    .ToLowerInvariant();
                if (!string.Equals(
                        currentSha256,
                        probe.SourceSha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!validateCurrentCanvas)
            {
                sizeBytes = observedSize;
                return true;
            }

            if (probe.EffectiveWidth is not int width
                || probe.EffectiveHeight is not int height
                || !TryReadMiniMaxH3SourceDimensions(
                    canonicalInput,
                    out int sourceWidth,
                    out int sourceHeight))
            {
                return false;
            }
            (int expectedWidth, int expectedHeight) =
                NormalizeMiniMaxH3VideoCanvas(sourceWidth, sourceHeight);
            if (width != expectedWidth || height != expectedHeight)
                return false;

            var after = new FileInfo(canonicalInput);
            double finalMtimeMs = new DateTimeOffset(
                after.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            sizeBytes = after.Length;
            lastWriteUtc = after.LastWriteTimeUtc;
            return sizeBytes == expectedSize
                && Math.Abs(finalMtimeMs - expectedMtimeMs) <= 1;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            sizeBytes = 0;
            lastWriteUtc = default;
            return false;
        }
    }

    private bool TryResolveMiniMaxH3ManagedOutputRootsOnWorker(
        string managedOutputRoot,
        out string lexicalRoot,
        out string canonicalRoot)
    {
        lexicalRoot = "";
        canonicalRoot = "";
        try
        {
            lexicalRoot = Path.GetFullPath(managedOutputRoot);
            canonicalRoot = Path.GetFullPath(_resolveFinalPath(lexicalRoot));
            return Path.IsPathFullyQualified(canonicalRoot);
        }
        catch
        {
            lexicalRoot = "";
            canonicalRoot = "";
            return false;
        }
    }

    private static bool IsMiniMaxH3DisplayedManagedSourceOnWorker(
        string lexicalSourcePath,
        string canonicalSourcePath,
        string lexicalManagedRoot,
        string canonicalManagedRoot)
    {
        try
        {
            string lexicalSource = Path.GetFullPath(lexicalSourcePath);
            if (!IsPathInside(lexicalSource, lexicalManagedRoot)
                || !IsPathInside(canonicalSourcePath, canonicalManagedRoot))
            {
                return false;
            }
            string relative = Path.GetRelativePath(
                canonicalManagedRoot,
                canonicalSourcePath);
            string[] parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            bool supportedFolder = parts.Length >= 1
                && parts[0] is "Upscaled" or "Photorealized" or "Edited";
            bool supportedLayout = parts.Length == 2
                || (parts.Length == 3
                    && DateTime.TryParseExact(
                        parts[1],
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _));
            return supportedFolder && supportedLayout;
        }
        catch
        {
            return false;
        }
    }

    private static VideoGenerationRequestSettings
        MiniMaxH3VideoRerunRequestSettings(
            MiniMaxH3VideoWorkspaceSnapshot snapshot)
        => new(
            MiniMaxH3VideoPresetId,
            MiniMaxH3VideoBackendId,
            snapshot.ProfileId,
            snapshot.NominalDurationSeconds,
            MiniMaxH3VideoPlaybackFps,
            snapshot.MaximumPixelArea,
            snapshot.Steps,
            snapshot.Prompt);

    private async Task<string?>
        ValidateMiniMaxH3VideoRerunSourceBeforePublishAsync(
            MiniMaxH3VideoExplicitActionContext context,
            VideoSourceChoice expectedSource,
            CancellationToken token)
    {
        if (!IsMiniMaxH3VideoExplicitActionContextCurrent(context))
            return "確認中にJobsの内容が更新されました。ジョブは追加していません。";

        MiniMaxH3VideoExplicitActionValidation validation = await Task.Run(
            () => ValidateMiniMaxH3VideoExplicitActionOnWorker(
                context,
                validateContentHash: true,
                validateCurrentCanvas: false),
            token);
        if (!IsMiniMaxH3VideoExplicitActionContextCurrent(context))
            return "確認中にJobsの内容が更新されました。ジョブは追加していません。";
        if (!validation.IsValid || validation.Source is null)
        {
            return string.IsNullOrWhiteSpace(validation.Error)
                ? "この動画が使った入力画像を再確認できません。"
                : validation.Error;
        }
        return VideoSourceChoicesReferToSameInput(
                validation.Source,
                expectedSource)
            ? null
            : "動画化の入力画像が確認中に変わりました。ジョブは追加していません。";
    }

    private async Task RerunMiniMaxH3VideoWithSavedPromptAsync(
        EnhancementWorkspaceJobView job)
    {
        if (_enhancementWorkspaceMutationPending
            || !job.CanRerunMiniMaxH3VideoWithSavedPrompt
            || !TryCaptureMiniMaxH3VideoExplicitActionContext(
                job,
                out MiniMaxH3VideoExplicitActionContext? context)
            || context is null)
        {
            EnhancementJobsStatusText.Text =
                "保存済みの動画Promptまたは入力画像を再確認できません。";
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = context.WorkspaceGeneration;
        var operationWatch = Stopwatch.StartNew();
        string? pendingDeliveryRequestId = null;
        try
        {
            EnhancementJobsStatusText.Text =
                "保存済みの動画Promptと入力画像を確認しています…";
            MiniMaxH3VideoExplicitActionValidation validation =
                await ValidateMiniMaxH3VideoExplicitActionAsync(context);
            if (!validation.IsValid || validation.Source is null)
            {
                EnhancementJobsStatusText.Text = validation.Error;
                return;
            }
            VideoSourceChoice source = validation.Source;
            if (!TryCaptureVideoSourceStamp(
                    source,
                    out VideoH3SourceStamp sourceStamp))
            {
                EnhancementJobsStatusText.Text =
                    "この動画が使った入力画像を固定できません。ジョブは追加していません。";
                return;
            }
            VideoGenerationRequestSettings settings =
                MiniMaxH3VideoRerunRequestSettings(context.Snapshot);
            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                BuildVideoGenerationRequestBody(
                    source,
                    settings,
                    h3Selected: true,
                    seed: null),
                includeQueuePlacementInBody: false,
                healthValidator: CreateMiniMaxH3VideoHealthValidator(
                    requireDisplayedManagedSource:
                        source.UsesDisplayedFileDirectly),
                requireExactHealthValidation: true,
                recoverySourceIdentity: source.SourceIdentity,
                prePublishValidator: () =>
                    IsMiniMaxH3VideoExplicitActionContextCurrent(context)
                        ? null
                        : "確認中にJobsの内容が更新されました。ジョブは追加していません。",
                asyncPrePublishValidator: token =>
                    ValidateMiniMaxH3VideoRerunSourceBeforePublishAsync(
                        context,
                        source,
                        token),
                onBeforeDurablePublish: item =>
                {
                    IDisposable publishLease =
                        AcquireVideoDurablePublishLease(
                            () => PinVideoSourceForDurablePublish(sourceStamp));
                    try
                    {
                        pendingDeliveryRequestId = item.RequestId;
                        RecordPendingVideoSourceDependency(
                            item.RequestId,
                            source);
                        return publishLease;
                    }
                    catch
                    {
                        publishLease.Dispose();
                        throw;
                    }
                });
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                AibosOperationLog.Write(
                    "video_job_rerun_saved_prompt",
                    "stale",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode,
                    "WORKSPACE_CHANGED");
                return;
            }
            if (response.SavedForDelivery)
            {
                EnhancementJobsStatusText.Text =
                    "同じPromptの動画化予約を保存しました。Jobsへの登録を継続しています。";
                AibosOperationLog.Write(
                    "video_job_rerun_saved_prompt",
                    "saved_for_delivery",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode);
                return;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                AibosOperationLog.Write(
                    "video_job_rerun_saved_prompt",
                    "failed",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode,
                    EnhancementApiErrorCode(response));
                return;
            }

            EnhancementJobsStatusText.Text =
                "保存された長さ・STEP・解像度・Promptで動画をもう1件追加しました。Seedは新しく生成されます。";
            await RefreshEnhancementJobsWorkspaceAsync(
                generation,
                isPoll: false);
            AibosOperationLog.Write(
                "video_job_rerun_saved_prompt",
                "completed",
                operationWatch.ElapsedMilliseconds,
                response.StatusCode,
                itemCount: 1);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingDeliveryRequestId))
            {
                _pendingVideoSourceDependencies.Remove(
                    pendingDeliveryRequestId);
            }
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private bool TryOpenMiniMaxH3VideoRerunSourceInViewer(
        EnhancementWorkspaceJobView job,
        VideoSourceChoice source,
        MiniMaxH3VideoExplicitActionValidation validation)
    {
        if (!source.UsesDisplayedFileDirectly)
        {
            return TryOpenEnhancementJobInViewer(
                job,
                preferredOutput: null,
                validatedCanonicalSource: source.SourceIdentity,
                validatedSourceSizeBytes:
                    validation.SourceIdentitySizeBytes,
                validatedSourceModifiedUtc:
                    validation.SourceIdentityLastWriteUtc,
                validatedOutputPath:
                    source.ProducerJobId is null
                        ? null
                        : source.DisplayPath,
                validatedOutputSizeBytes:
                    source.ProducerJobId is null
                        ? null
                        : validation.DisplayPathSizeBytes);
        }

        string canonicalSource = source.DisplayPath;
        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            candidate.IsRealFile
            && string.Equals(
                candidate.Path,
                canonicalSource,
                StringComparison.OrdinalIgnoreCase));
        if (tile is null)
        {
            tile = new Tile
            {
                Path = canonicalSource,
                FileName = Path.GetFileName(canonicalSource),
                IsRealFile = true,
                ModifiedUtc = validation.DisplayPathLastWriteUtc,
                Fav = FavoriteLevelForPath(canonicalSource),
            };
        }

        CaptureEnhancementJobsReturnViewport(job.Id);
        PrepareEnhancementJobsModalTile(
            tile,
            canonicalSource,
            validation.DisplayPathSizeBytes);
        _returnToEnhancementJobsAfterModalClose = true;
        CloseEnhancementJobsWorkspace(restoreFocus: false);
        SelectTile(tile);
        OpenModal();
        if (Modal.Visibility == Visibility.Visible
            && string.Equals(
                SelectedTile()?.Path,
                canonicalSource,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _returnToEnhancementJobsAfterModalClose = false;
        RestoreEnhancementJobsModalSelection();
        return false;
    }

    private async Task EditMiniMaxH3VideoPromptAsync(
        EnhancementWorkspaceJobView job)
    {
        if (_enhancementWorkspaceMutationPending
            || !job.CanEditMiniMaxH3VideoPrompt)
        {
            return;
        }

        Task refreshTask = _enhancedStateRefreshTask;
        if (!refreshTask.IsCompleted)
            EnhancementJobsStatusText.Text = "動画化に使った入力画像を確認しています…";
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_enhancementWorkspaceMutationPending
            || !job.CanEditMiniMaxH3VideoPrompt
            || !TryCaptureMiniMaxH3VideoExplicitActionContext(
                job,
                out MiniMaxH3VideoExplicitActionContext? context)
            || context is null)
        {
            return;
        }

        job.IsBusy = true;
        try
        {
            EnhancementJobsStatusText.Text =
                "動画化に使った入力画像を確認しています…";
            MiniMaxH3VideoExplicitActionValidation validation =
                await ValidateMiniMaxH3VideoExplicitActionAsync(context);
            if (!validation.IsValid || validation.Source is null)
            {
                EnhancementJobsStatusText.Text = validation.Error;
                return;
            }
            VideoSourceChoice source = validation.Source;
            if (!TryOpenMiniMaxH3VideoRerunSourceInViewer(
                    job,
                    source,
                    validation))
                return;

            if (!source.UsesDisplayedFileDirectly
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId))
            {
                int producerIndex = _modalEnhancementVersions.FindIndex(candidate =>
                    string.Equals(
                        candidate.JobId,
                        job.SourceProducerJobId,
                        StringComparison.Ordinal)
                    && candidate.Operation == "photoreal");
                if (producerIndex < 0)
                {
                    SetStatusToast(
                        "この動画が使った実写版を確認できません。Jobsを更新してから再度お試しください。");
                    return;
                }
                _modalEnhancementVersionIndex = producerIndex + 1;
                _modalShowingEnhanced = true;
                ManagedEnhancementVersion producer =
                    _modalEnhancementVersions[producerIndex];
                if (TryGetModalSourceTile(out Tile sourceTile))
                {
                    RememberModalDisplayPreference(
                        sourceTile,
                        ModalDisplayVersionKind.Photoreal,
                        producer.JobId);
                }
                OpenModal();
            }

            RestoreVideoGenerationSettings(
                context.Snapshot.NominalDurationSeconds,
                MiniMaxH3VideoPlaybackFps,
                context.Snapshot.MaximumPixelArea,
                context.Snapshot.Prompt,
                modelId: MiniMaxH3VideoModelId,
                qualityId: MiniMaxH3VideoPresetId,
                steps: context.Snapshot.Steps);
            OpenVideoGenerationBoard(requestedSource: null);
            if (ModalVideoGenerationPopup.Visibility == Visibility.Visible)
            {
                SetVideoGenerationSettingsStatus(
                    "このJobの入力画像・長さ・STEP・解像度・Promptを読み込みました。Promptを変更してからキューへ追加してください。開いただけでは追加されません。");
            }
        }
        finally
        {
            job.IsBusy = false;
        }
    }

    private async Task RerunI2iV3JobAsync(
        EnhancementWorkspaceJobView job,
        bool enqueueNext)
    {
        if (_enhancementWorkspaceMutationPending
            || job.I2iV3Snapshot is not I2iV3WorkspaceSnapshot snapshot
            || (enqueueNext ? !job.CanRerunI2iV3Next : !job.CanRerunI2iV3)
            || !TryResolveEnhancementWorkspaceInput(job, out _)
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource))
        {
            EnhancementJobsStatusText.Text =
                "元画像または保存済み設定を検証できないため、AI編集を再実行できません。";
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            string? ValidateHealth(JsonElement health)
            {
                if (!TryParseI2iV3Capability(
                        health,
                        out I2iV3CapabilityState capability)
                    || !capability.IsReady)
                {
                    return "The Aibos Image local AI service is not ready for unified AI editing. No job was added.";
                }
                return null;
            }

            var edits = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["overall"] = snapshot.Overall,
                ["expression"] = snapshot.Expression,
                ["outfit"] = snapshot.Outfit,
                ["background"] = snapshot.Background,
                ["pose"] = snapshot.Pose,
            };
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = canonicalSource,
                ["operation"] = "i2i",
                ["i2iSchemaVersion"] = 3,
                ["presetId"] = I2iV3PresetId,
                ["adapterId"] = I2iV3AdapterId,
                ["edits"] = edits,
                ["steps"] = snapshot.Steps,
                ["cfgScale"] = snapshot.CfgScale,
                ["outfitMaskMode"] = snapshot.OutfitMaskMode,
                ["outfitMaskExpandPixels"] = snapshot.OutfitMaskExpandPixels,
                ["seed"] = snapshot.Seed,
            };
            if (!string.IsNullOrWhiteSpace(job.SourceProducerJobId))
                body["sourceProducerJobId"] = job.SourceProducerJobId;

            string placement = enqueueNext ? "next" : "last";
            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                body,
                placement,
                healthValidator: ValidateHealth,
                recoverySourceIdentity: canonicalSource);
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (response.SavedForDelivery)
            {
                EnhancementJobsStatusText.Text =
                    "同じAI編集設定の予約を保存しました。Jobsへの登録を継続しています。";
                return;
            }
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || !TryGetUniqueI2iProperty(payload, "job", out JsonElement created)
                || !IsExactI2iV3Rerun(job, snapshot, created))
            {
                EnhancementJobsStatusText.Text = response.Ok
                    ? "再実行ジョブの保存結果を安全に確認できません。Jobsを更新してください。"
                    : response.Error;
                return;
            }

            EnhancementJobsStatusText.Text = enqueueNext
                ? "同じ5欄・STEP・CFG・マスク・Seedで、AI編集を現在処理の次へ1件追加しました。"
                : "同じ5欄・STEP・CFG・マスク・Seedで、AI編集を待機列へ1件追加しました。";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private bool IsExactI2iV3Rerun(
        EnhancementWorkspaceJobView sourceJob,
        I2iV3WorkspaceSnapshot expected,
        JsonElement created)
    {
        if (!TryReadI2iV3JobInfo(created, out I2iV3JobInfo info)
            || !TryGetUniqueI2iString(created, "status", out string? status)
            || status is not ("queued" or "running")
            || !TryGetUniqueI2iString(created, "sourceId", out string? sourceId)
            || !TryGetUniqueI2iString(created, "sourcePath", out string? sourcePath)
            || !TryResolveEnhancementSourceIdentity(
                sourceJob.SourceId,
                out string expectedSource)
            || !TryResolveEnhancementSourceIdentity(sourceId, out string createdSourceId)
            || !TryResolveEnhancementSourceIdentity(sourcePath, out string createdSourcePath)
            || !EnhancementSourceIdentityComparer.Equals(expectedSource, createdSourceId)
            || !EnhancementSourceIdentityComparer.Equals(expectedSource, createdSourcePath)
            || !TryReadOptionalI2iSourceProducerJobId(created, out string? producerId))
        {
            return false;
        }
        return string.Equals(
                producerId,
                sourceJob.SourceProducerJobId,
                StringComparison.Ordinal)
            && Equals(info.Snapshot, expected);
    }

    private async Task EditI2iV3JobSettingsAsync(EnhancementWorkspaceJobView job)
    {
        if (_enhancementWorkspaceMutationPending
            || !job.CanEditI2iV3Settings
            || job.I2iV3Snapshot is not I2iV3WorkspaceSnapshot snapshot)
        {
            return;
        }

        Task refreshTask = _enhancedStateRefreshTask;
        if (!refreshTask.IsCompleted)
            EnhancementJobsStatusText.Text = "元画像と実写版の情報を確認しています…";
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || !job.CanEditI2iV3Settings
            || !TryOpenEnhancementJobInViewer(job, preferredOutput: null))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(job.SourceProducerJobId))
        {
            int producerIndex = _modalEnhancementVersions.FindIndex(candidate =>
                string.Equals(
                    candidate.JobId,
                    job.SourceProducerJobId,
                    StringComparison.Ordinal)
                && candidate.Operation == "photoreal");
            if (producerIndex < 0)
            {
                SetStatusToast(
                    "このAI編集が使った実写版を確認できません。Jobsを更新してから再度お試しください。");
                return;
            }
            _modalEnhancementVersionIndex = producerIndex + 1;
            _modalShowingEnhanced = true;
            ManagedEnhancementVersion producer = _modalEnhancementVersions[producerIndex];
            if (TryGetModalSourceTile(out Tile sourceTile))
            {
                RememberModalDisplayPreference(
                    sourceTile,
                    ModalDisplayVersionKind.Photoreal,
                    producer.JobId);
            }
            OpenModal();
        }

        if (!await OpenI2iV3EditBoardAsync(snapshot))
            SetStatusToast("保存済みAI編集設定を開けませんでした。");
    }

    private async Task<EnhancementApiResponse> SendEnhancementWorkspaceRetryAsync(
        EnhancementWorkspaceJobView job)
    {
        Func<JsonElement, string?>? retryHealthValidator =
            CreateEnhancementRetryHealthValidator(job);
        return await SendEnhancementEnqueueAsync(
            body: null,
            queuePlacement: "last",
            retryJobId: job.Id,
            healthValidator: retryHealthValidator,
            requireExactHealthValidation: retryHealthValidator is not null,
            onBeforeDurablePublish: job.IsVideoOperation
                && !job.IsExactCurrentVideoToolsV2
                && !job.IsExactCurrentVideoTrimV1
                ? _ => AcquireVideoDurablePublishLease(
                    () => PinVideoRetrySourceForDurablePublish(job))
                : null);
    }

    private static string EnhancementApiErrorCode(
        EnhancementApiResponse response)
        => response.Payload is JsonElement payload
            && TryGetStringProperty(payload, "code", out string? code)
            && !string.IsNullOrWhiteSpace(code)
                ? code!
                : response.StatusCode == 503
                    && response.Payload is JsonElement busyPayload
                    && TryGetStringProperty(
                        busyPayload,
                        "error",
                        out string? busyError)
                    && string.Equals(
                        busyError,
                        "The local AI companion is busy.",
                        StringComparison.Ordinal)
                    ? "COMPANION_BUSY"
                : response.StatusCode == 0
                    ? "COMPANION_UNAVAILABLE"
                    : "API_ERROR";

    private bool HasEnhancementWorkspaceMutationDebt
        => _enhancementWorkspaceMutationDebtEpoch
            > _enhancementWorkspaceReconciledMutationDebtEpoch;

    private static bool EnhancementWorkspaceMutationMayHaveCommitted(
        EnhancementApiResponse response)
        => response.Ok
            || response.SavedForDelivery
            || !response.InnerStatusAuthoritative
            || response.StatusCode is 408 or 425 or 429
            || response.StatusCode >= 500;

    private void NoteEnhancementWorkspaceMutationDebt(
        EnhancementApiResponse response,
        long? inventoryRevisionBeforeMutation)
    {
        if (!EnhancementWorkspaceMutationMayHaveCommitted(response))
            return;

        _enhancementWorkspaceMutationDebtEpoch++;
        if ((response.SavedForDelivery || !response.Ok)
            && inventoryRevisionBeforeMutation is long previousRevision
            && previousRevision < 9_007_199_254_740_991)
        {
            long requiredRevision = previousRevision + 1;
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision =
                _enhancementWorkspaceMutationDebtMinimumInventoryRevision
                    is long existingRevision
                    ? Math.Max(existingRevision, requiredRevision)
                    : requiredRevision;
        }
        _enhancementWorkspaceHealthInventorySignature = null;
        if (!_aiProcessingMinimizedMode
            && EnhancementJobsDialog.Visibility == Visibility.Visible)
        {
            _enhancementWorkspacePollTimer.Start();
        }
    }

    private void ReconcileEnhancementWorkspaceMutationDebt(
        bool mutationDebtAtReadStart,
        long mutationDebtEpochAtReadStart,
        long? mutationDebtMinimumInventoryRevisionAtReadStart,
        long? observedHealthInventoryRevision)
    {
        if (!mutationDebtAtReadStart
            || mutationDebtMinimumInventoryRevisionAtReadStart is long
                minimumInventoryRevision
                && _enhancementWorkspaceHealthInventoryRevisionSupported == true
                && (observedHealthInventoryRevision is not long inventoryRevision
                    || inventoryRevision < minimumInventoryRevision))
        {
            return;
        }

        _enhancementWorkspaceReconciledMutationDebtEpoch = Math.Max(
            _enhancementWorkspaceReconciledMutationDebtEpoch,
            mutationDebtEpochAtReadStart);
        if (!HasEnhancementWorkspaceMutationDebt)
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision = null;
    }

    private async Task<EnhancementApiResponse>
        SendTrackedEnhancementWorkspaceMutationAsync(
            Func<Task<EnhancementApiResponse>> send,
            bool requireInventoryRevisionAdvanceOnAmbiguous = true)
    {
        long? inventoryRevisionBeforeMutation =
            requireInventoryRevisionAdvanceOnAmbiguous
                ? _enhancementWorkspaceLastHealthInventoryRevision
                : null;
        EnhancementApiResponse response = await send();
        NoteEnhancementWorkspaceMutationDebt(
            response,
            inventoryRevisionBeforeMutation);
        return response;
    }

    public bool EnhancementWorkspaceMutationDebtContractForSmoke()
    {
        long previousDebtEpoch = _enhancementWorkspaceMutationDebtEpoch;
        long previousReconciledEpoch =
            _enhancementWorkspaceReconciledMutationDebtEpoch;
        bool? previousRevisionSupported =
            _enhancementWorkspaceHealthInventoryRevisionSupported;
        long? previousMinimumRevision =
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision;
        string? previousSignature =
            _enhancementWorkspaceHealthInventorySignature;
        try
        {
            _enhancementWorkspaceMutationDebtEpoch = 0;
            _enhancementWorkspaceReconciledMutationDebtEpoch = 0;
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision = null;
            _enhancementWorkspaceHealthInventoryRevisionSupported = true;
            _enhancementWorkspaceHealthInventorySignature = "cached";
            var lostResponse = new EnhancementApiResponse(
                false,
                0,
                null,
                "synthetic lost response");
            NoteEnhancementWorkspaceMutationDebt(lostResponse, 41);
            long firstDebtEpoch = _enhancementWorkspaceMutationDebtEpoch;
            bool lostResponseStayedSticky = HasEnhancementWorkspaceMutationDebt
                && _enhancementWorkspaceHealthInventorySignature is null
                && _enhancementWorkspaceMutationDebtMinimumInventoryRevision
                    == 42;

            ReconcileEnhancementWorkspaceMutationDebt(
                mutationDebtAtReadStart: true,
                firstDebtEpoch,
                mutationDebtMinimumInventoryRevisionAtReadStart: 42,
                observedHealthInventoryRevision: 41);
            bool staleInventoryDidNotClearDebt =
                HasEnhancementWorkspaceMutationDebt;

            long olderReadEpoch = _enhancementWorkspaceMutationDebtEpoch;
            NoteEnhancementWorkspaceMutationDebt(
                new EnhancementApiResponse(
                    true,
                    200,
                    null,
                    "",
                    InnerStatusAuthoritative: true),
                41);
            long currentDebtEpoch = _enhancementWorkspaceMutationDebtEpoch;
            ReconcileEnhancementWorkspaceMutationDebt(
                mutationDebtAtReadStart: true,
                olderReadEpoch,
                mutationDebtMinimumInventoryRevisionAtReadStart: 42,
                observedHealthInventoryRevision: 42);
            bool olderReadDidNotClearNewDebt =
                HasEnhancementWorkspaceMutationDebt
                && _enhancementWorkspaceReconciledMutationDebtEpoch
                    == olderReadEpoch;
            ReconcileEnhancementWorkspaceMutationDebt(
                mutationDebtAtReadStart: true,
                currentDebtEpoch,
                mutationDebtMinimumInventoryRevisionAtReadStart: 42,
                observedHealthInventoryRevision: 42);
            bool authoritativeInventoryClearedDebt =
                !HasEnhancementWorkspaceMutationDebt;

            long epochBeforeDefinitiveRejection =
                _enhancementWorkspaceMutationDebtEpoch;
            NoteEnhancementWorkspaceMutationDebt(
                new EnhancementApiResponse(
                    false,
                    409,
                    null,
                    "synthetic conflict",
                    InnerStatusAuthoritative: true),
                42);
            bool definitiveRejectionAddedNoDebt =
                _enhancementWorkspaceMutationDebtEpoch
                    == epochBeforeDefinitiveRejection
                && !HasEnhancementWorkspaceMutationDebt;
            _enhancementWorkspaceHealthInventoryRevisionSupported = false;
            NoteEnhancementWorkspaceMutationDebt(lostResponse, null);
            long legacyDebtEpoch = _enhancementWorkspaceMutationDebtEpoch;
            ReconcileEnhancementWorkspaceMutationDebt(
                mutationDebtAtReadStart: true,
                legacyDebtEpoch,
                mutationDebtMinimumInventoryRevisionAtReadStart: null,
                observedHealthInventoryRevision: null);
            bool legacyInventoryWithoutRevisionClearedDebt =
                !HasEnhancementWorkspaceMutationDebt;
            return lostResponseStayedSticky
                && staleInventoryDidNotClearDebt
                && olderReadDidNotClearNewDebt
                && authoritativeInventoryClearedDebt
                && definitiveRejectionAddedNoDebt
                && legacyInventoryWithoutRevisionClearedDebt;
        }
        finally
        {
            _enhancementWorkspaceMutationDebtEpoch = previousDebtEpoch;
            _enhancementWorkspaceReconciledMutationDebtEpoch =
                previousReconciledEpoch;
            _enhancementWorkspaceMutationDebtMinimumInventoryRevision =
                previousMinimumRevision;
            _enhancementWorkspaceHealthInventoryRevisionSupported =
                previousRevisionSupported;
            _enhancementWorkspaceHealthInventorySignature = previousSignature;
        }
    }

    private async Task<bool> RunEnhancementWorkspaceMutationAsync(
        EnhancementWorkspaceJobView job,
        HttpMethod method,
        string route,
        string successMessage,
        object? body = null,
        bool removeTerminalOriginalAfterSuccess = false,
        string operationLogName = "job_mutation")
    {
        if (_enhancementWorkspaceMutationPending || job.IsBusy || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return false;

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        HashSet<string> optimisticJobIds = removeTerminalOriginalAfterSuccess
            ? [job.Id]
            : [];
        EnhancementWorkspaceJobView[] optimisticVisibleJobs =
            removeTerminalOriginalAfterSuccess
                ? BeginOptimisticBulkPresentation(
                    optimisticJobIds,
                    hideRows: true)
                : [];
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            bool isRetryEnqueue = method == HttpMethod.Post
                && body is null
                && string.Equals(
                    route,
                    $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry",
                    StringComparison.Ordinal);
            EnhancementApiResponse response =
                await SendTrackedEnhancementWorkspaceMutationAsync(
                    () => isRetryEnqueue
                        ? SendEnhancementWorkspaceRetryAsync(job)
                        : method == HttpMethod.Delete
                            ? SendIdempotentEnhancementMutationAsync(
                                method,
                                route,
                                body)
                            : SendEnhancementApiAsync(method, route, body));
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                AibosOperationLog.Write(
                    operationLogName,
                    "stale",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode,
                    "WORKSPACE_CHANGED");
                return false;
            }
            if (response.SavedForDelivery)
            {
                EnhancementJobsStatusText.Text =
                    removeTerminalOriginalAfterSuccess
                        ? "再試行の予約を保存しました。登録確認前なので元の履歴は残しています。"
                        : "再試行の予約を保存しました。Jobsへの登録を継続しています。";
                AibosOperationLog.Write(
                    operationLogName,
                    "saved_for_delivery",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode);
                return true;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                AibosOperationLog.Write(
                    operationLogName,
                    "failed",
                    operationWatch.ElapsedMilliseconds,
                    response.StatusCode,
                    EnhancementApiErrorCode(response));
                return false;
            }

            if (removeTerminalOriginalAfterSuccess
                && !_usingDefaultModalEnhancementSender)
            {
                EnhancementApiResponse removeResponse =
                    await SendTrackedEnhancementWorkspaceMutationAsync(
                        () => SendIdempotentEnhancementMutationAsync(
                            HttpMethod.Delete,
                            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}"));
                if (!removeResponse.Ok)
                {
                    EnhancementJobsStatusText.Text =
                        $"Retry was queued, but the original terminal history could not be removed. {removeResponse.Error}";
                    AibosOperationLog.Write(
                        operationLogName,
                        "partial",
                        operationWatch.ElapsedMilliseconds,
                        removeResponse.StatusCode,
                        EnhancementApiErrorCode(removeResponse));
                    await RefreshEnhancementJobsWorkspaceAsync(
                        generation,
                        isPoll: false);
                    return false;
                }
            }

            EnhancementJobsStatusText.Text = successMessage;
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
            AibosOperationLog.Write(
                operationLogName,
                "completed",
                operationWatch.ElapsedMilliseconds,
                response.StatusCode);
            return true;
        }
        finally
        {
            EndOptimisticBulkPresentation(
                optimisticJobIds,
                optimisticVisibleJobs,
                revealRows: true);
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private Func<JsonElement, string?>? CreateEnhancementRetryHealthValidator(
        EnhancementWorkspaceJobView job)
        => job.IsExactCurrentVideoTrimV1
            ? payload => VideoTrimV1Contract.IsExactReadyHealth(payload)
                ? null
                : VideoTrimV1Text("UiVideoTrimV1WriterPending")
            : CreateEnhancementRetryHealthValidator(
                job.Operation,
                job.VideoMutationSafe,
                job.PresetId,
                job.AdapterId,
                job.I2iSchemaVersion,
                job.I2iTarget);

    private Func<JsonElement, string?>? CreateEnhancementRetryHealthValidator(
        string operation,
        bool videoMutationSafe,
        string presetId,
        string adapterId,
        int? i2iSchemaVersion,
        string? i2iTarget)
    {
        if (string.Equals(operation, "video", StringComparison.Ordinal)
            && videoMutationSafe
            && string.Equals(
                presetId,
                MiniMaxH3VideoPresetId,
                StringComparison.Ordinal)
            && string.Equals(
                adapterId,
                MiniMaxH3VideoBackendId,
                StringComparison.Ordinal))
        {
            return CreateMiniMaxH3VideoHealthValidator();
        }

        if (!string.Equals(operation, "i2i", StringComparison.Ordinal))
            return null;

        if (i2iSchemaVersion == 2)
        {
            string target = i2iTarget ?? "";
            return payload => TryParseI2iV2Capability(
                    payload,
                    out I2iV2CapabilityState capability)
                && capability.IsReadyFor(target)
                    ? null
                    : "The Aibos Image local AI service is not ready for this AI edit Retry. No reservation was saved.";
        }

        return payload => TryParseI2iCapability(
                payload,
                out bool ready,
                out _)
            && ready
                ? null
                : "The Aibos Image local AI service is not ready for this AI edit Retry. No reservation was saved.";
    }

    private async void OpenEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            await OpenEnhancementWorkspaceOutputAsync(job);
    }

    private async Task<bool> OpenEnhancementWorkspaceOutputAsync(
        EnhancementWorkspaceJobView job)
    {
        long generation = _enhancementWorkspaceGeneration;
        if (job.IsVideoOperation && job.CanUseOutput)
        {
            Task refreshTask = _enhancedStateRefreshTask;
            if (!refreshTask.IsCompleted)
            {
                EnhancementJobsStatusText.Text =
                    "動画出力を確認しています。画面はそのまま操作できます。";
            }
            try
            {
                await refreshTask;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible
                || !_enhancementWorkspaceJobs.Contains(job)
                || !job.CanUseOutput)
            {
                return false;
            }
            if (!_enhancementReadOk)
            {
                EnhancementJobsStatusText.Text =
                    "動画出力を確認できませんでした。Jobsを更新してからもう一度試してください。";
                return false;
            }
        }

        return TryOpenEnhancementWorkspaceOutput(job);
    }

    private bool TryOpenEnhancementWorkspaceOutput(EnhancementWorkspaceJobView job)
    {
        if (!job.CanUseOutput)
        {
            EnhancementJobsStatusText.Text =
                "Open output unavailable: this operation or state is not eligible. The source image was not changed.";
            return false;
        }
        if (job.IsVideoOperation)
        {
            if (!TryResolveManagedVideoWorkspaceOutput(
                    job,
                    out ManagedVideoVersion video,
                    out string videoReason))
            {
                EnhancementJobsStatusText.Text =
                    $"Open output unavailable: {videoReason}. The source image was not changed.";
                return false;
            }

            return TryRevealEnhancementVideoOutputInExplorer(video);
        }
        if (!TryResolveManagedEnhancementWorkspaceOutput(job, out ManagedEnhancedOutput output, out string reason))
        {
            EnhancementJobsStatusText.Text = $"Open output unavailable: {reason}. The source image was not changed.";
            return false;
        }

        return TryOpenEnhancementJobInViewer(job, output);
    }

    private async void OpenEnhancementSourceInViewer_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            await OpenEnhancementSourceInViewerAsync(job);
    }

    private async Task<bool> OpenEnhancementSourceInViewerAsync(
        EnhancementWorkspaceJobView job)
    {
        var watch = Stopwatch.StartNew();
        string outcome = "failed";
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            if (job.IsVideoOperation && job.CanUseOutput)
            {
                // The jobs workspace already starts durable-state hydration in
                // the background after applying its inventory. Await that
                // single-flight task without probing or scanning SQLite again
                // on the input/UI thread.
                Task refreshTask = _enhancedStateRefreshTask;
                if (!refreshTask.IsCompleted)
                {
                    EnhancementJobsStatusText.Text =
                        "動画情報を読み込み中です。画面はそのまま操作できます。";
                }
                try
                {
                    await refreshTask;
                }
                catch (OperationCanceledException)
                {
                    outcome = "canceled";
                    return false;
                }

                if (generation != _enhancementWorkspaceGeneration
                    || EnhancementJobsDialog.Visibility != Visibility.Visible
                    || !_enhancementWorkspaceJobs.Contains(job)
                    || !job.CanUseOutput)
                {
                    outcome = "canceled";
                    return false;
                }
                if (!_enhancementReadOk)
                {
                    EnhancementJobsStatusText.Text =
                        "動画情報を確認できませんでした。Jobsを更新してからもう一度試してください。";
                    return false;
                }
            }

            bool opened = TryOpenEnhancementSourceInViewer(job);
            outcome = opened ? "completed" : "failed";
            return opened;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException)
        {
            Trace.TraceWarning(
                $"Enhancement workspace viewer open failed: {ex.GetType().Name}");
            if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                EnhancementJobsStatusText.Text =
                    "表示を切り替えられませんでした。Jobsを更新してからもう一度試してください。";
            }
            return false;
        }
        finally
        {
            AibosOperationLog.Write(
                "jobs_thumbnail_open",
                outcome,
                watch.ElapsedMilliseconds,
                mode: job.IsVideoOperation ? "video" : "image");
        }
    }

    private bool TryOpenEnhancementSourceInViewer(EnhancementWorkspaceJobView job)
    {
        if (job.IsVideoOperation && job.CanUseOutput)
        {
            if (!TryResolveManagedVideoWorkspaceOutput(
                    job,
                    out ManagedVideoVersion video,
                    out string reason))
            {
                EnhancementJobsStatusText.Text =
                    $"動画を開けません: {reason}. 元画像は変更されていません。";
                return false;
            }
            return TryOpenEnhancementVideoJobInViewer(job, video);
        }

        return TryOpenEnhancementJobInViewer(job, preferredOutput: null);
    }

    private bool TryRevealEnhancementVideoOutputInExplorer(
        ManagedVideoVersion video)
    {
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add($"/select,{video.Output.OutputPath}");
            if (!_explorerLauncher(startInfo))
            {
                EnhancementJobsStatusText.Text =
                    "動画の保存先をExplorerで開けませんでした。もう一度試してください。";
                return false;
            }

            EnhancementJobsStatusText.Text =
                "Explorerで完成動画の保存先を開きました。";
            return true;
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            Trace.TraceWarning(
                $"Enhancement video reveal failed: {ex.GetType().Name}");
            EnhancementJobsStatusText.Text =
                "動画の保存先をExplorerで開けませんでした。もう一度試してください。";
            return false;
        }
    }

    private bool TryOpenEnhancementJobInViewer(
        EnhancementWorkspaceJobView job,
        ManagedEnhancedOutput? preferredOutput,
        ManagedVideoVersion? preferredVideo = null,
        string? validatedCanonicalSource = null,
        long? validatedSourceSizeBytes = null,
        DateTime? validatedSourceModifiedUtc = null,
        string? validatedOutputPath = null,
        long? validatedOutputSizeBytes = null)
    {
        string canonicalSource;
        long sourceSizeBytes;
        DateTime sourceModifiedUtc;
        if (validatedCanonicalSource is not null)
        {
            if (!Path.IsPathFullyQualified(validatedCanonicalSource)
                || !SupportedImageExtensions.Contains(
                    Path.GetExtension(validatedCanonicalSource))
                || validatedSourceSizeBytes is not > 0
                || validatedSourceModifiedUtc is null)
            {
                EnhancementJobsStatusText.Text =
                    "元画像を安全に確認できないため、ビューワーで開けません。";
                return false;
            }
            canonicalSource = validatedCanonicalSource;
            sourceSizeBytes = validatedSourceSizeBytes.Value;
            sourceModifiedUtc = validatedSourceModifiedUtc.Value;
        }
        else if (!TryResolveEnhancementWorkspaceCatalogSource(
                     job,
                     out canonicalSource)
                 || !File.Exists(canonicalSource))
        {
            EnhancementJobsStatusText.Text = "元画像が見つからないため、ビューワーで開けません。";
            return false;
        }
        else
        {
            var sourceInfo = new FileInfo(canonicalSource);
            sourceSizeBytes = sourceInfo.Length;
            sourceModifiedUtc = sourceInfo.LastWriteTimeUtc;
        }

        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            candidate.IsRealFile
            && string.Equals(candidate.Path, canonicalSource, StringComparison.OrdinalIgnoreCase));
        if (tile is null)
        {
            tile = new Tile
            {
                Path = canonicalSource,
                FileName = Path.GetFileName(canonicalSource),
                IsRealFile = true,
                ModifiedUtc = sourceModifiedUtc,
                Fav = FavoriteLevelForPath(canonicalSource),
            };
        }

        CaptureEnhancementJobsReturnViewport(job.Id);
        PrepareEnhancementJobsModalTile(
            tile,
            canonicalSource,
            sourceSizeBytes,
            validatedOutputPath,
            validatedOutputSizeBytes);
        if (preferredVideo is not null)
        {
            RememberModalDisplayPreference(
                tile,
                ModalDisplayVersionKind.Video,
                preferredVideo.JobId);
        }
        _returnToEnhancementJobsAfterModalClose = true;
        CloseEnhancementJobsWorkspace(restoreFocus: false);
        SelectTile(tile);
        OpenModal();
        if (Modal.Visibility != Visibility.Visible
            || !string.Equals(SelectedTile()?.Path, canonicalSource, StringComparison.OrdinalIgnoreCase))
        {
            _returnToEnhancementJobsAfterModalClose = false;
            RestoreEnhancementJobsModalSelection();
            return false;
        }

        if (preferredOutput is null)
            return true;

        int versionIndex = _modalEnhancementVersions.FindIndex(candidate =>
            string.Equals(candidate.JobId, job.Id, StringComparison.Ordinal)
            || string.Equals(
                candidate.Output.OutputPath,
                preferredOutput.OutputPath,
                StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0)
        {
            _modalEnhancementVersions.Add(
                new ManagedEnhancementVersion(job.Id, job.Operation, preferredOutput));
            versionIndex = _modalEnhancementVersions.Count - 1;
        }

        _modalEnhancementVersionIndex = versionIndex + 1;
        _modalShowingEnhanced = true;
        RememberModalDisplayPreference(
            tile,
            ModalDisplayKindForOperation(
                _modalEnhancementVersions[versionIndex].Operation),
            _modalEnhancementVersions[versionIndex].JobId);
        OpenModal();
        bool opened = _modalShowingEnhanced
            && string.Equals(
                _modalDisplayPath,
                preferredOutput.OutputPath,
                StringComparison.OrdinalIgnoreCase);
        if (!opened)
            SetStatusToast("The managed output could not be selected in the Aibos viewer.");
        return opened;
    }

    private bool TryOpenEnhancementVideoJobInViewer(
        EnhancementWorkspaceJobView job,
        ManagedVideoVersion preferredVideo)
    {
        if (!TryOpenEnhancementJobInViewer(
                job,
                preferredOutput: null,
                preferredVideo))
            return false;

        if (_modalShowingVideo
            && _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count
            && string.Equals(
                _modalVideoVersions[_modalVideoVersionIndex].JobId,
                preferredVideo.JobId,
                StringComparison.Ordinal))
        {
            return true;
        }

        int versionIndex = _modalVideoVersions.FindIndex(candidate =>
            string.Equals(candidate.JobId, job.Id, StringComparison.Ordinal)
            && string.Equals(
                candidate.Output.OutputPath,
                preferredVideo.Output.OutputPath,
                StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0)
        {
            _modalVideoVersions.Add(preferredVideo);
            versionIndex = _modalVideoVersions.Count - 1;
        }

        StopAndHideModalVideo(clearSource: true);
        bool opened = ShowModalVideoVersion(
            versionIndex,
            autoplay: true);
        if (!opened)
            SetStatusToast("The managed video could not be selected in the Aibos viewer.");
        return opened;
    }

    private void PrepareEnhancementJobsModalTile(
        Tile tile,
        string canonicalSource,
        long sourceSizeBytes,
        string? validatedOutputPath = null,
        long? validatedOutputSizeBytes = null)
    {
        RestoreEnhancementJobsModalSelection();
        _enhancementJobsPreviousSelectionPaths.AddRange(_selectedPaths);
        _enhancementJobsPreviousPrimaryPath = _primarySelectedPath;
        _enhancementJobsModalSelectionCaptured = true;
        _enhancementJobsTrustedModalSourcePath = canonicalSource;
        _enhancementJobsTrustedModalSourceSizeBytes = sourceSizeBytes;
        _enhancementJobsTrustedModalOutputPath = validatedOutputPath;
        _enhancementJobsTrustedModalOutputSizeBytes =
            validatedOutputSizeBytes;
        if (!_tiles.Contains(tile))
        {
            _tiles.Add(tile);
            _enhancementJobsTemporaryVisibleTile = tile;
        }
    }

    private bool IsEnhancementJobsTrustedModalSource(Tile tile)
        => !string.IsNullOrWhiteSpace(_enhancementJobsTrustedModalSourcePath)
            && _enhancementJobsTrustedModalSourceSizeBytes is > 0
            && string.Equals(
                tile.Path,
                _enhancementJobsTrustedModalSourcePath,
                StringComparison.OrdinalIgnoreCase);

    private bool TryResolveEnhancementJobsTrustedModalSource(
        Tile tile,
        out string canonicalSource,
        out long sourceSizeBytes,
        out string reason)
    {
        canonicalSource = "";
        sourceSizeBytes = 0;
        reason = "the Jobs source is unavailable";
        if (!IsEnhancementJobsTrustedModalSource(tile)
            || string.IsNullOrWhiteSpace(tile.Path)
            || !Path.IsPathFullyQualified(tile.Path)
            || !SupportedImageExtensions.Contains(Path.GetExtension(tile.Path)))
        {
            return false;
        }

        // PrepareEnhancementJobsModalTile receives only a source that was
        // already canonicalized and validated for this explicit action. Do
        // not repeat path or media I/O on the dispatcher; the modal decoder
        // remains fail-closed if the file disappears before it is opened.
        canonicalSource = _enhancementJobsTrustedModalSourcePath!;
        sourceSizeBytes = _enhancementJobsTrustedModalSourceSizeBytes!.Value;
        reason = "";
        return true;
    }

    private bool TryResolveEnhancementJobsTrustedModalOutput(
        Tile tile,
        out string outputPath,
        out long outputSizeBytes)
    {
        outputPath = "";
        outputSizeBytes = 0;
        if (!IsEnhancementJobsTrustedModalSource(tile)
            || string.IsNullOrWhiteSpace(
                _enhancementJobsTrustedModalOutputPath)
            || _enhancementJobsTrustedModalOutputSizeBytes is not > 0
            || _modalEnhancementVersionIndex < 0
            || _modalEnhancementVersionIndex
                >= _modalEnhancementVersions.Count)
        {
            return false;
        }

        ManagedEnhancementVersion selected =
            _modalEnhancementVersions[_modalEnhancementVersionIndex];
        if (!string.Equals(
                selected.Output.OutputPath,
                _enhancementJobsTrustedModalOutputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        outputPath = _enhancementJobsTrustedModalOutputPath;
        outputSizeBytes =
            _enhancementJobsTrustedModalOutputSizeBytes.Value;
        return true;
    }

    private void RestoreEnhancementJobsModalSelection()
    {
        if (!_enhancementJobsModalSelectionCaptured)
            return;

        _enhancementJobsModalSelectionCaptured = false;
        Tile? temporaryTile = _enhancementJobsTemporaryVisibleTile;
        _enhancementJobsTemporaryVisibleTile = null;
        _enhancementJobsTrustedModalSourcePath = null;
        _enhancementJobsTrustedModalSourceSizeBytes = null;
        _enhancementJobsTrustedModalOutputPath = null;
        _enhancementJobsTrustedModalOutputSizeBytes = null;
        if (temporaryTile is not null)
            _tiles.Remove(temporaryTile);

        var previousPaths = new HashSet<string>(
            _enhancementJobsPreviousSelectionPaths,
            StringComparer.OrdinalIgnoreCase);
        List<Tile> restored = _tiles
            .Where(tile => previousPaths.Contains(tile.Path))
            .ToList();
        Tile? primary = restored.FirstOrDefault(tile =>
            string.Equals(
                tile.Path,
                _enhancementJobsPreviousPrimaryPath,
                StringComparison.OrdinalIgnoreCase))
            ?? restored.LastOrDefault();
        _enhancementJobsPreviousSelectionPaths.Clear();
        _enhancementJobsPreviousPrimaryPath = null;
        SetSelection(restored, primary);
    }

    private void ReturnToEnhancementJobsAfterModalClose(bool modalWasVisible)
    {
        if (!_returnToEnhancementJobsAfterModalClose || !modalWasVisible)
            return;

        _returnToEnhancementJobsAfterModalClose = false;
        RestoreEnhancementJobsModalSelection();
        string statusFilter = _enhancementWorkspaceStatusFilter;
        string operationFilter = _enhancementWorkspaceOperationFilter;
        _enhancementJobsReturnViewportPending = true;
        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                try
                {
                    if (IsLoaded
                        && IsVisible
                        && EnhancementJobsDialog.Visibility != Visibility.Visible)
                    {
                        await OpenEnhancementJobsWorkspaceAsync(
                            statusFilter,
                            focusToRestore: OpenEnhancementJobsButton,
                            restoreReturnViewport: true,
                            initialOperationFilter: operationFilter);
                    }
                }
                finally
                {
                    _enhancementJobsReturnViewportPending = false;
                }
            }),
            DispatcherPriority.Background);
    }

    private void CaptureEnhancementJobsReturnViewport(string jobId)
    {
        _enhancementJobsReturnJobId = jobId;
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        _enhancementJobsReturnVerticalOffset = viewer?.VerticalOffset ?? 0;
        _enhancementJobsReturnAnchorViewportTop = double.NaN;
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        if (viewer is not null
            && item is not null
            && TryGetEnhancementJobViewportTop(item, viewer, out double top))
        {
            _enhancementJobsReturnAnchorViewportTop = top;
        }
    }

    private async Task RestoreEnhancementJobsReturnViewportAsync()
    {
        double requestedOffset = Math.Max(0, _enhancementJobsReturnVerticalOffset);
        string? requestedJobId = _enhancementJobsReturnJobId;
        double requestedAnchorTop = _enhancementJobsReturnAnchorViewportTop;
        _enhancementJobsReturnVerticalOffset = 0;
        _enhancementJobsReturnJobId = null;
        _enhancementJobsReturnAnchorViewportTop = double.NaN;

        EnhancementWorkspaceJobView? restoredItem = null;

        void RestoreViewport()
        {
            EnhancementJobsList.UpdateLayout();
            ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
            if (viewer is null)
                return;

            if (restoredItem is not null && double.IsFinite(requestedAnchorTop))
            {
                EnhancementJobsList.ScrollIntoView(restoredItem);
                EnhancementJobsList.UpdateLayout();
                if (TryGetEnhancementJobViewportTop(
                        restoredItem,
                        viewer,
                        out double currentTop))
                {
                    viewer.ScrollToVerticalOffset(Math.Clamp(
                        viewer.VerticalOffset + currentTop - requestedAnchorTop,
                        0,
                        viewer.ScrollableHeight));
                    return;
                }
            }

            viewer.ScrollToVerticalOffset(Math.Min(requestedOffset, viewer.ScrollableHeight));
        }

        await Dispatcher.InvokeAsync(() =>
        {
            EnhancementJobsList.UpdateLayout();
            if (!string.IsNullOrWhiteSpace(requestedJobId))
            {
                restoredItem =
                    (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                        .FirstOrDefault(job => string.Equals(
                            job.Id,
                            requestedJobId,
                            StringComparison.Ordinal));
                EnhancementJobsList.SelectedItem = restoredItem;
            }
            RestoreViewport();
        }, DispatcherPriority.Loaded);

        await Dispatcher.InvokeAsync(RestoreViewport, DispatcherPriority.Render);
    }

    private bool TryGetEnhancementJobViewportTop(
        EnhancementWorkspaceJobView item,
        ScrollViewer viewer,
        out double top)
    {
        top = double.NaN;
        if (EnhancementJobsList.ItemContainerGenerator.ContainerFromItem(item)
                is not FrameworkElement container)
        {
            return false;
        }

        try
        {
            top = container.TranslatePoint(new Point(0, 0), viewer).Y;
            return double.IsFinite(top);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async void DeleteEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EnhancementWorkspaceJobView job }
            || _enhancementWorkspaceMutationPending
            || job.IsBusy
            || !job.CanDeleteOutput)
        {
            return;
        }

        bool validOutput;
        string reason;
        if (job.IsVideoOperation)
        {
            validOutput = TryResolveManagedVideoWorkspaceOutput(job, out _, out reason);
        }
        else
        {
            validOutput = TryResolveManagedEnhancementWorkspaceOutput(job, out _, out reason);
        }
        if (!validOutput)
        {
            EnhancementJobsStatusText.Text = $"Delete output unavailable: {reason}. The source image was not changed.";
            return;
        }

        string mediaName = job.IsVideoOperation ? "video" : "enhanced";
        bool confirmed = _confirmEnhancedOutputDeleteForSmoke?.Invoke() ?? MessageBox.Show(
                this,
                $"Delete only this managed {mediaName} output? The original source image will be kept.",
                $"Delete {mediaName} output",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
            return;

        _enhancementWorkspaceMutationPending = true;
        job.IsBusy = true;
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response =
                await SendTrackedEnhancementWorkspaceMutationAsync(
                    () => SendEnhancementApiAsync(
                        HttpMethod.Delete,
                        $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/output"));
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            ReloadEnhancedOutputsForVisibleCatalog();
            EnhancementJobsStatusText.Text =
                $"{(job.IsVideoOperation ? "Video" : "Enhanced")} output deleted. The original source image was kept.";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private bool TryResolveManagedEnhancementWorkspaceOutput(
        EnhancementWorkspaceJobView job,
        out ManagedEnhancedOutput managedOutput,
        out string reason)
    {
        managedOutput = null!;
        reason = "the output is missing, stale, or outside managed storage";
        if (!job.IsImageOperation
            || job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceInput(
                job,
                out string canonicalSource))
        {
            return false;
        }

        var tile = new Tile { Path = canonicalSource, IsRealFile = true };
        if (!TryCreateManagedEnhancedOutput(
                tile,
                job.OutputPath,
                job.SourceSize.Value,
                job.SourceMtimeMs.Value,
                out managedOutput))
            return false;

        reason = "";
        return true;
    }

    private bool TryResolveManagedVideoWorkspaceOutput(
        EnhancementWorkspaceJobView job,
        out ManagedVideoVersion managedVideo,
        out string reason)
    {
        managedVideo = null!;
        reason = "the video is missing, stale, malformed, or outside managed storage";
        if (job.VideoToolsV2Snapshot is not null
            || job.VideoTrimV1Snapshot is not null)
        {
            string expectedKind = job.VideoTrimV1Snapshot is not null
                ? "trim"
                : job.VideoToolsV2Snapshot!.Kind;
            if (!(job.CanUseVideoToolsV2Output
                    || job.CanUseVideoTrimV1Output)
                || !TryResolveVideoToolsV2ManagedOutput(
                    job.OutputPath!,
                    out string canonicalV2Output))
            {
                return false;
            }

            ManagedVideoVersion[] matches = _videoVersions.Values
                .SelectMany(static versions => versions)
                .Where(version => string.Equals(
                        version.JobId,
                        job.Id,
                        StringComparison.Ordinal)
                    && string.Equals(
                        version.Output.OutputPath,
                        canonicalV2Output,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Take(2)
                .ToArray();
            if (matches.Length != 1
                || !string.Equals(
                    matches[0].VersionKind,
                    expectedKind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    matches[0].PresetId,
                    job.PresetId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    matches[0].BackendId,
                    job.AdapterId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            managedVideo = matches[0];
            reason = "";
            return true;
        }

        if (!job.IsVideoOperation
            || job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource))
        {
            return false;
        }

        try
        {
            string canonicalJobOutput =
                _resolveFinalPath(Path.GetFullPath(job.OutputPath));
            ManagedVideoVersion? candidate = GetManagedVideoVersionsForPath(canonicalSource)
                .FirstOrDefault(version =>
                    string.Equals(version.JobId, job.Id, StringComparison.Ordinal)
                    && string.Equals(
                        version.Output.OutputPath,
                        canonicalJobOutput,
                        StringComparison.OrdinalIgnoreCase));
            if (candidate is null
                || candidate.Output.SourceSize != job.SourceSize.Value
                || Math.Abs(candidate.Output.SourceMtimeMs - job.SourceMtimeMs.Value) > 1
                || !string.Equals(
                    candidate.PresetId,
                    job.PresetId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    candidate.BackendId,
                    job.AdapterId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            managedVideo = candidate;
            reason = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    private bool ReloadEnhancedOutputsForVisibleCatalog()
    {
        if (!LoadEnhancedState())
            return false;

        ApplyEnhancedOutputsToVisibleCatalog();
        return true;
    }

    private void ApplyEnhancedOutputsToVisibleCatalog()
    {
        bool refreshPreferredThumbnail = false;
        var catalogTiles = new HashSet<Tile>(
            _allTiles,
            ReferenceEqualityComparer.Instance);
        foreach (Tile tile in EnumerateLiveTiles())
        {
            bool isCatalogTile = catalogTiles.Contains(tile);
            bool enhanced = isCatalogTile
                ? TryGetCatalogManagedEnhancedOutputForPath(
                    tile.Path,
                    out ManagedEnhancedOutput output)
                : TryGetManagedEnhancedOutputForPath(
                    tile.Path,
                    out output);
            string? outputPath = enhanced ? output.OutputPath : null;
            tile.EnhancedOutputPath = outputPath;
            ApplyTileEnhancementAvailability(
                tile,
                isCatalogTile
                    ? GetCatalogManagedEnhancementVersionsForPath(tile.Path)
                    : GetManagedEnhancementVersionsForPath(tile.Path));
            ApplyTileEnhancementQueueActivity(tile);
            ApplyTileVideoAvailability(
                tile,
                isCatalogTile
                    ? GetCatalogManagedVideoVersionsForPath(tile.Path)
                    : GetManagedVideoVersionsForPath(tile.Path));
            if (_useLastDisplayedImageVersionForThumbnails
                && tile.Thumbnail is not null
                && _thumbnailImageDisplayPreferencesByPath.ContainsKey(
                    tile.Path)
                && !string.Equals(
                    tile.ThumbnailAssetPath,
                    ResolveGalleryThumbnailAssetPath(tile),
                    StringComparison.OrdinalIgnoreCase))
            {
                refreshPreferredThumbnail = true;
            }
        }
        if (refreshPreferredThumbnail)
        {
            _thumbnailViewportRevision++;
            QueueGalleryThumbnailPreferenceRefresh();
        }
        if (!ExternalFileDropSessionActive
            && _sortBy is SortUpscaleNewestValue
            or SortUpscaleQueuedNewestValue
            or SortPhotorealNewestValue
            or SortPhotorealQueuedNewestValue
            or SortVideoNewestValue
            or SortVideoQueuedNewestValue)
        {
            _ = QueueCatalogProjection(
                debounce: false,
                reorderCatalog: true,
                selectFirst: false);
        }
        else if (!ExternalFileDropSessionActive
            && (_photorealFavoriteFilterLevels.Count > 0
                || _videoFavoriteFilterLevels.Count > 0))
        {
            _ = QueueCatalogProjection(
                debounce: false,
                reorderCatalog: false,
                selectFirst: false);
        }
    }

    public async Task OpenEnhancementJobsForSmokeAsync()
    {
        OpenEnhancementJobs_Click(this, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
    }

    public void ConfigureEnhancementJobsBulkConfirmationForSmoke(
        Func<string, string, bool>? confirmation)
        => _confirmEnhancementJobsBulkActionForSmoke = confirmation;

    public List<string> EnhancementWorkspaceCatalogPathsForSmoke
        => _allTiles.Where(static tile => tile.IsRealFile).Select(static tile => tile.Path).ToList();

    public void CloseEnhancementJobsForSmoke() => CloseEnhancementJobsWorkspace(restoreFocus: false);

    public bool EnhancementJobsVisibleForSmoke
        => EnhancementJobsDialog.Visibility == Visibility.Visible;

    public double EnhancementJobsVerticalOffsetForSmoke
        => FindVisualDescendant<ScrollViewer>(EnhancementJobsList)?.VerticalOffset ?? 0;

    public double EnhancementJobViewportTopForSmoke(string jobId)
    {
        EnhancementJobsList.UpdateLayout();
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        return viewer is not null
            && item is not null
            && TryGetEnhancementJobViewportTop(item, viewer, out double top)
                ? top
                : double.NaN;
    }

    public double PositionEnhancementJobForSmoke(
        string jobId,
        double requestedViewportTop)
    {
        EnhancementWorkspaceJobView? item =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?
                .FirstOrDefault(job => string.Equals(
                    job.Id,
                    jobId,
                    StringComparison.Ordinal));
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        if (item is null || viewer is null)
            return double.NaN;

        EnhancementJobsList.ScrollIntoView(item);
        EnhancementJobsList.UpdateLayout();
        if (!TryGetEnhancementJobViewportTop(item, viewer, out double currentTop))
            return double.NaN;

        viewer.ScrollToVerticalOffset(Math.Clamp(
            viewer.VerticalOffset + currentTop - requestedViewportTop,
            0,
            viewer.ScrollableHeight));
        EnhancementJobsList.UpdateLayout();
        return TryGetEnhancementJobViewportTop(item, viewer, out double positionedTop)
            ? positionedTop
            : double.NaN;
    }

    public string? SelectedEnhancementJobIdForSmoke
        => (EnhancementJobsList.SelectedItem as EnhancementWorkspaceJobView)?.Id;

    public double ScrollEnhancementJobsForSmoke(double offset)
    {
        EnhancementJobsList.UpdateLayout();
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        if (viewer is null)
            return 0;
        viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        EnhancementJobsList.UpdateLayout();
        return viewer.VerticalOffset;
    }

    public async Task<EnhancementJobsScrollPerformanceSmokeSnapshot>
        RunEnhancementJobsScrollPerformanceSmokeAsync(
            int jobCount,
            int scrollStepCount)
    {
        int boundedJobCount = Math.Max(1, jobCount);
        int boundedStepCount = Math.Max(1, scrollStepCount);
        _enhancementWorkspacePollTimer.Stop();
        StopEnhancementWorkspaceThumbnailViewportDebounce();
        Volatile.Read(ref _enhancementWorkspaceThumbnailCts)?.Cancel();
        _enhancementWorkspaceThumbnailCts = null;
        _suppressEnhancementWorkspaceThumbnailLoadsForSmoke = true;
        _enhancementWorkspaceLastThumbnailBatchSize = 0;
        _enhancementWorkspaceJobs.Clear();
        _enhancementWorkspaceJobs.Capacity = Math.Max(
            _enhancementWorkspaceJobs.Capacity,
            boundedJobCount);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < boundedJobCount; index++)
        {
            string status = (index % 5) switch
            {
                0 => "running",
                1 => "queued",
                2 => "failed",
                3 => "canceled",
                _ => "succeeded",
            };
            string operation = (index % 4) switch
            {
                0 => "upscale",
                1 => "photoreal",
                2 => "i2i",
                _ => "video",
            };
            DateTimeOffset createdAt = now.AddSeconds(-boundedJobCount + index);
            var job = new EnhancementWorkspaceJobView(
                $"synthetic-job-{index:D5}",
                $"synthetic-source-{index:D5}",
                Path.Combine(Path.GetTempPath(), $"synthetic-source-{index:D5}.png"),
                sourceProducerJobId: null,
                sourceVideoJobId: null,
                presetId: operation == "video"
                    ? "wan22-ti2v-5b-normal-v1"
                    : "synthetic-preset",
                adapterId: operation == "video"
                    ? "comfyui-wan22-ti2v"
                    : "synthetic-adapter",
                operation,
                videoMutationSafe: operation == "video",
                queueReorderSafe: true,
                i2iMutationSafe: operation == "i2i",
                i2iSchemaVersion: operation == "i2i" ? 2 : null,
                i2iTarget: operation == "i2i" ? "hair_color" : null,
                i2iInstructionSummary: operation == "i2i" ? "synthetic" : null,
                i2iV2EnvelopeClaimed: operation == "i2i",
                status,
                cancelRequested: false,
                progress: status == "running" ? 83 : status == "succeeded" ? 100 : 0,
                outputPath: status == "succeeded"
                    ? Path.Combine(Path.GetTempPath(), $"synthetic-output-{index:D5}.webp")
                    : null,
                errorMessage: status == "failed" ? "Synthetic failure" : null,
                createdAt,
                updatedAt: createdAt.AddSeconds(1),
                startedAt: status is "running" or "succeeded" ? createdAt : null,
                finishedAt: status is "failed" or "canceled" or "succeeded"
                    ? createdAt.AddSeconds(1)
                    : null,
                sourceSize: 1_024,
                sourceMtimeMs: createdAt.ToUnixTimeMilliseconds(),
                queueOrder: status == "queued" ? index : null,
                apiOrdinal: index,
                requestDetailsText:
                    $"処理: {operation}\nJob ID: synthetic-job-{index:D5}\nPrompt:\nsynthetic");
            if (status == "queued")
            {
                int queuePosition = index / 5 + 1;
                job.ApplyQueuePresentation(
                    queuePosition,
                    (boundedJobCount + 4) / 5,
                    index);
                if (operation == "photoreal")
                    job.QueuedPhotorealPromptUpdateCapabilitySafe = true;
            }
            if (operation == "photoreal")
                job.PhotorealEnqueueNextCapabilitySafe = true;
            _enhancementWorkspaceJobs.Add(job);
        }

        bool actionPresentationContract = _enhancementWorkspaceJobs.All(
            static job => job.ActionPresentationMatchesCapabilitiesForSmoke());

        _enhancementWorkspaceStatusFilter = "all";
        _enhancementWorkspaceOperationFilter = "all";
        _enhancementWorkspaceVideoKindFilter = "all";
        RefreshEnhancementWorkspaceFilterToggleStates();
        _enhancementWorkspacePageIndex = 0;
        EnhancementJobsDialog.Visibility = Visibility.Visible;
        var filterWatch = Stopwatch.StartNew();
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        filterWatch.Stop();

        var initialLayoutWatch = Stopwatch.StartNew();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        EnhancementJobsList.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
        EnhancementJobsList.UpdateLayout();
        initialLayoutWatch.Stop();

        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        if (viewer is null)
            throw new InvalidOperationException("Enhancement Jobs scroll viewer was not realized.");

        int scrollChangedBefore = _enhancementWorkspaceScrollChangedCount;
        int timerRestartBefore = _enhancementWorkspaceThumbnailTimerRestartCount;
        int cancellationBefore = _enhancementWorkspaceThumbnailScrollCancellationCount;
        using var syntheticActiveThumbnailLoad = new CancellationTokenSource();
        _enhancementWorkspaceThumbnailCts = syntheticActiveThumbnailLoad;
        int realizedPeak = FindVisualDescendants<ListBoxItem>(EnhancementJobsList).Count();
        int realizedButtonPeak = FindVisualDescendants<Button>(EnhancementJobsList).Count();
        var stepMilliseconds = new List<double>(boundedStepCount);
        for (int step = 0; step < boundedStepCount; step++)
        {
            double fraction = (step % 2 == 0)
                ? (step + 1d) / boundedStepCount
                : 1d - ((step + 1d) / boundedStepCount);
            var stepWatch = Stopwatch.StartNew();
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * fraction);
            EnhancementJobsList.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            stepWatch.Stop();
            stepMilliseconds.Add(stepWatch.Elapsed.TotalMilliseconds);
            realizedPeak = Math.Max(
                realizedPeak,
                FindVisualDescendants<ListBoxItem>(EnhancementJobsList).Count());
            realizedButtonPeak = Math.Max(
                realizedButtonPeak,
                FindVisualDescendants<Button>(EnhancementJobsList).Count());
        }

        await Task.Delay(EnhancementJobsThumbnailViewportDebounce + TimeSpan.FromMilliseconds(80));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Interlocked.CompareExchange(
            ref _enhancementWorkspaceThumbnailCts,
            null,
            syntheticActiveThumbnailLoad);

        double[] orderedSteps = stepMilliseconds.Order().ToArray();
        int p95Index = Math.Clamp(
            (int)Math.Ceiling(orderedSteps.Length * 0.95) - 1,
            0,
            orderedSteps.Length - 1);
        EnhancementJobsPageWindow page = CalculateEnhancementJobsPageWindow(
            _enhancementWorkspaceFilteredCount,
            _enhancementWorkspacePageIndex);
        var visible = (EnhancementJobsList.ItemsSource
                as IEnumerable<EnhancementWorkspaceJobView>)?
            .ToArray()
            ?? [];
        return new EnhancementJobsScrollPerformanceSmokeSnapshot(
            boundedJobCount,
            _enhancementWorkspaceFilteredCount,
            visible.Length,
            EnhancementJobsPageSize,
            page.PageCount,
            filterWatch.Elapsed.TotalMilliseconds,
            initialLayoutWatch.Elapsed.TotalMilliseconds,
            stepMilliseconds.Sum(),
            stepMilliseconds.Max(),
            orderedSteps[p95Index],
            _enhancementWorkspaceScrollChangedCount - scrollChangedBefore,
            _enhancementWorkspaceThumbnailTimerRestartCount - timerRestartBefore,
            _enhancementWorkspaceThumbnailScrollCancellationCount - cancellationBefore,
            _enhancementWorkspaceThumbnailViewportTimer.IsEnabled,
            actionPresentationContract,
            realizedPeak,
            realizedButtonPeak,
            _enhancementWorkspaceLastThumbnailBatchSize,
            viewer.ScrollableHeight);
    }

    public double EnhancementJobsVerticalThumbSlotHeightForSmoke
    {
        get
        {
            EnhancementJobsList.UpdateLayout();
            System.Windows.Controls.Primitives.ScrollBar? bar =
                FindVisualDescendants<System.Windows.Controls.Primitives.ScrollBar>(EnhancementJobsList)
                .FirstOrDefault(static candidate =>
                    candidate.Orientation == Orientation.Vertical
                    && candidate.IsVisible);
            System.Windows.Controls.Primitives.Track? track = bar is null
                ? null
                : FindVisualDescendant<System.Windows.Controls.Primitives.Track>(bar);
            return track?.Thumb is null
                ? 0
                : System.Windows.Controls.Primitives.LayoutInformation
                    .GetLayoutSlot(track.Thumb).Height;
        }
    }

    public void SelectEnhancementJobsFilterForSmoke(string filter)
    {
        _enhancementWorkspaceStatusFilter =
            NormalizeEnhancementWorkspaceStatusFilter(filter);
        _enhancementWorkspaceOperationFilter =
            NormalizeEnhancementWorkspaceOperationFilter(filter);
        if (_enhancementWorkspaceOperationFilter != "video")
            _enhancementWorkspaceVideoKindFilter = "all";
        RefreshEnhancementWorkspaceFilterToggleStates();
        _enhancementWorkspacePageIndex = 0;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        RefreshEnhancementQueueBulkControls();
    }

    public void SelectEnhancementJobsVideoKindFilterForSmoke(string filter)
    {
        _enhancementWorkspaceVideoKindFilter =
            NormalizeEnhancementWorkspaceVideoKindFilter(filter);
        if (_enhancementWorkspaceVideoKindFilter != "all")
            _enhancementWorkspaceOperationFilter = "video";
        RefreshEnhancementWorkspaceFilterToggleStates();
        _enhancementWorkspacePageIndex = 0;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        RefreshEnhancementQueueBulkControls();
    }

    public string EnhancementJobsOperationFilterForSmoke =>
        _enhancementWorkspaceOperationFilter;

    public string EnhancementJobsVideoKindFilterForSmoke =>
        _enhancementWorkspaceVideoKindFilter;

    public bool EnhancementJobsVideoKindPanelVisibleForSmoke =>
        EnhancementJobsVideoKindFiltersPanel.Visibility == Visibility.Visible;

    public void SelectEnhancementJobsStatusFilterForSmoke(string filter)
    {
        _enhancementWorkspaceStatusFilter =
            NormalizeEnhancementWorkspaceStatusFilter(filter);
        RefreshEnhancementWorkspaceFilterToggleStates();
        _enhancementWorkspacePageIndex = 0;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        RefreshEnhancementQueueBulkControls();
    }

    public void SelectEnhancementJobsOperationFilterForSmoke(string filter)
    {
        _enhancementWorkspaceOperationFilter =
            NormalizeEnhancementWorkspaceOperationFilter(filter);
        if (_enhancementWorkspaceOperationFilter != "video")
            _enhancementWorkspaceVideoKindFilter = "all";
        RefreshEnhancementWorkspaceFilterToggleStates();
        _enhancementWorkspacePageIndex = 0;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
        RefreshEnhancementQueueBulkControls();
    }

    public bool ToggleEnhancementJobsStatusFilterForSmoke(string filter)
        => ToggleEnhancementJobsFilterForSmoke(
            EnhancementJobsStatusFiltersPanel,
            filter,
            _enhancementWorkspaceStatusFilter);

    public bool ToggleEnhancementJobsOperationFilterForSmoke(string filter)
        => ToggleEnhancementJobsFilterForSmoke(
            EnhancementJobsOperationFiltersPanel,
            filter,
            _enhancementWorkspaceOperationFilter);

    public bool ToggleEnhancementJobsVideoKindFilterForSmoke(string filter)
        => ToggleEnhancementJobsFilterForSmoke(
            EnhancementJobsVideoKindFiltersPanel,
            filter,
            _enhancementWorkspaceVideoKindFilter);

    private static bool ToggleEnhancementJobsFilterForSmoke(
        Panel panel,
        string filter,
        string selectedFilter)
    {
        CheckBox? toggle = panel.Children
            .OfType<CheckBox>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                filter,
                StringComparison.Ordinal));
        if (toggle is null)
            return false;
        toggle.IsChecked = toggle.IsChecked != true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
        string expected = string.Equals(selectedFilter, filter, StringComparison.Ordinal)
            ? "all"
            : filter;
        return panel.Children
                .OfType<CheckBox>()
                .Count(static item => item.IsChecked == true) == 1
            && panel.Children
                .OfType<CheckBox>()
                .Single(static item => item.IsChecked == true)
                .Tag as string == expected;
    }

    public static EnhancementJobsPagingSmokeSnapshot
        CalculateEnhancementJobsPagingForSmoke(
            int filteredCount,
            int requestedPageIndex)
    {
        EnhancementJobsPageWindow page = CalculateEnhancementJobsPageWindow(
            filteredCount,
            requestedPageIndex);
        return new EnhancementJobsPagingSmokeSnapshot(
            EnhancementJobsPageSize,
            page.PageIndex,
            page.PageCount,
            page.FirstIndex,
            page.ItemCount);
    }

    public EnhancementJobsWorkspaceSmokeSnapshot EnhancementJobsWorkspaceForSmoke()
    {
        var visible = (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray() ?? [];
        return new EnhancementJobsWorkspaceSmokeSnapshot(
            EnhancementJobsDialog.Visibility == Visibility.Visible,
            _enhancementWorkspaceJobs.Count,
            visible.Length,
            _enhancementWorkspaceFilteredCount,
            _enhancementWorkspacePageIndex,
            CalculateEnhancementJobsPageWindow(
                _enhancementWorkspaceFilteredCount,
                _enhancementWorkspacePageIndex).PageCount,
            EnhancementJobsPageSize,
            _enhancementWorkspaceJobs.Count(static job => job.IsActive),
            _enhancementWorkspaceJobs.Count(static job => job.Status == "failed"),
            _enhancementWorkspaceJobs.Count(static job => job.Status == "canceled"),
            visible.Count(static job => job.IsHighlighted),
            _enhancementWorkspacePollTimer.IsEnabled,
            _enhancementWorkspaceGetCount,
            _enhancementWorkspacePollCount,
            EnhancementJobsStatusText.Text,
            _enhancementWorkspaceHealthGetCount,
            EnhancementJobsHealthStateText.Text,
            EnhancementJobsHealthDetailText.Text,
            EnhancementJobsHealthRevisionText.Text,
            _enhancementWorkspaceQueuePaused,
            Convert.ToString(EnhancementJobsPauseResumeButton.Content, CultureInfo.InvariantCulture) ?? "",
            EnhancementJobsPauseResumeButton.IsEnabled,
            _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported,
            _enhancementWorkspacePhotorealEnqueueNextSupported,
            visible.Select(static job => job.Id).ToArray(),
            visible.Select(static job => job.StatusLabel).ToArray(),
            visible.Select(static job => job.OperationLabel).ToArray());
    }

    public async Task<bool> SetEnhancementQueuePausedForSmokeAsync(bool paused)
    {
        bool changed = await SetEnhancementQueuePausedAsync(paused);
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return changed;
    }

    public void PrepareUnknownEnhancementQueueResumeForSmoke()
    {
        EnhancementJobsDialog.Visibility = Visibility.Visible;
        _enhancementWorkspaceQueuePaused = null;
        _enhancementWorkspaceQueueRecoveryRequired = false;
        _enhancementWorkspaceMutationPending = false;
        _enhancementWorkspaceRefreshPending = false;
        RefreshEnhancementQueuePauseControl();
    }

    public async Task<bool> CancelEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanCancel)
            return false;
        await RunEnhancementWorkspaceMutationAsync(job, HttpMethod.Post, $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/cancel", "Cancel requested.");
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> RetryEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanRetry)
            return false;
        bool completed = await RunEnhancementWorkspaceMutationAsync(
            job,
            HttpMethod.Post,
            $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry",
            "Retry queued. The original terminal history was removed.",
            removeTerminalOriginalAfterSuccess:
                job.Status is "failed" or "canceled",
            operationLogName: "job_retry");
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return completed;
    }

    public async Task<int> RetryAllFailedEnhancementJobsForSmokeAsync()
    {
        int count = await RetryAllFailedEnhancementJobsAsync();
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return count;
    }

    public async Task<int> ClearAllFailedEnhancementJobsForSmokeAsync()
    {
        int count = await ClearAllFailedEnhancementJobsAsync();
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return count;
    }

    public async Task<int> RetryAllCanceledEnhancementJobsForSmokeAsync()
    {
        int count = await RetryAllCanceledEnhancementJobsAsync();
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return count;
    }

    public async Task<int> ClearAllCanceledEnhancementJobsForSmokeAsync()
    {
        int count = await ClearAllCanceledEnhancementJobsAsync();
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return count;
    }

    public bool RetryAllFailedEnhancementJobsControlForSmoke =>
        EnhancementJobsRetryFailedButton.IsEnabled;

    public bool ClearAllFailedEnhancementJobsControlForSmoke =>
        EnhancementJobsClearFailedButton.IsEnabled;

    public bool RetryAllCanceledEnhancementJobsControlForSmoke =>
        EnhancementJobsRetryCanceledButton.IsEnabled;

    public bool ClearAllCanceledEnhancementJobsControlForSmoke =>
        EnhancementJobsClearCanceledButton.IsEnabled;

    public string RetryAllFailedEnhancementJobsLabelForSmoke =>
        Convert.ToString(
            EnhancementJobsRetryFailedButton.Content,
            CultureInfo.InvariantCulture) ?? "";

    public string RetryAllFailedEnhancementJobsToolTipForSmoke =>
        Convert.ToString(
            EnhancementJobsRetryFailedButton.ToolTip,
            CultureInfo.InvariantCulture) ?? "";

    public string RetryAllCanceledEnhancementJobsToolTipForSmoke =>
        Convert.ToString(
            EnhancementJobsRetryCanceledButton.ToolTip,
            CultureInfo.InvariantCulture) ?? "";

    public bool FailedBulkPanelVisibleForSmoke =>
        EnhancementJobsFailedBulkPanel.Visibility == Visibility.Visible;

    public bool CanceledBulkPanelVisibleForSmoke =>
        EnhancementJobsCanceledBulkPanel.Visibility == Visibility.Visible;

    public bool QueuedBulkPanelVisibleForSmoke =>
        EnhancementJobsQueuedBulkPanel.Visibility == Visibility.Visible;

    public string[] EnhancementJobsStatusFilterOrderForSmoke =>
        EnhancementJobsStatusFiltersPanel.Children
            .OfType<FrameworkElement>()
            .Where(static item => item.Tag is string)
            .Select(static item => (string)item.Tag)
            .ToArray();

    public string[] EnhancementJobsOperationFilterOrderForSmoke =>
        EnhancementJobsOperationFiltersPanel.Children
            .OfType<FrameworkElement>()
            .Where(static item => item.Tag is string)
            .Select(static item => (string)item.Tag)
            .ToArray();

    public string[] EnhancementJobsVideoKindFilterOrderForSmoke =>
        EnhancementJobsVideoKindFiltersPanel.Children
            .OfType<FrameworkElement>()
            .Where(static item => item.Tag is string)
            .Select(static item => (string)item.Tag)
            .ToArray();

    public int EnhancementJobsRealizedContainerCountForSmoke =>
        FindVisualDescendants<ListBoxItem>(EnhancementJobsList).Count();

    public int EnhancementJobsLastThumbnailBatchSizeForSmoke =>
        _enhancementWorkspaceLastThumbnailBatchSize;

    public int EnhancementJobsThumbnailViewportLimitForSmoke =>
        EnhancementJobsThumbnailViewportLimit;

    public int EnhancementJobsThumbnailScrollCancellationCountForSmoke =>
        _enhancementWorkspaceThumbnailScrollCancellationCount;

    public async Task<int> QueueVisibleEnhancementJobThumbnailsForSmokeAsync()
    {
        QueueEnhancementWorkspaceVisibleThumbnailLoad();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await Dispatcher.Yield(DispatcherPriority.Background);
        return _enhancementWorkspaceLastThumbnailBatchSize;
    }

    public async Task<(bool Ok, bool SavedForDelivery, int StatusCode, string Error)>
        RetryMiniMaxH3JobForSmokeAsync(string id)
    {
        // This isolated UI smoke probes only the H3 health gate with synthetic
        // ids and no persisted Jobs row. Product retry entry points always use
        // SendEnhancementWorkspaceRetryAsync and pin their persisted source.
        Func<JsonElement, string?>? healthValidator =
            CreateEnhancementRetryHealthValidator(
                operation: "video",
                videoMutationSafe: true,
                presetId: MiniMaxH3VideoPresetId,
                adapterId: MiniMaxH3VideoBackendId,
                i2iSchemaVersion: null,
                i2iTarget: null);
        EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
            body: null,
            queuePlacement: "last",
            retryJobId: id,
            healthValidator: healthValidator,
            requireExactHealthValidation: true);
        return (
            response.Ok,
            response.SavedForDelivery,
            response.StatusCode,
            response.Error);
    }

    public async Task<bool> MoveEnhancementJobForSmokeAsync(
        string id,
        string move,
        bool waitForWorkspaceIdle = true)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanReorder || move is not ("up" or "down" or "next"))
            return false;
        bool moved = await MoveEnhancementJobInQueueAsync(job, move);
        if (waitForWorkspaceIdle)
            await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return moved;
    }

    public async Task<bool> UpdateQueuedPhotorealPromptsForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanUpdatePhotorealPrompts)
            return false;

        UpdateQueuedPhotorealPrompts_Click(
            new Button { Tag = job },
            new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<int> UpdateAllQueuedPhotorealPromptsForSmokeAsync()
    {
        int eligibleCount = _enhancementWorkspaceJobs.Count(static job =>
            job.CanUpdatePhotorealPrompts);
        if (eligibleCount == 0 || !CanUpdateAllQueuedPhotorealPrompts())
            return 0;

        UpdateAllQueuedPhotorealPrompts_Click(this, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return eligibleCount;
    }

    public bool UpdateAllQueuedPhotorealPromptsControlForSmoke
        => EnhancementJobsUpdateQueuedPromptsButton.IsEnabled
            == (!_enhancementWorkspaceMutationPending
                && CanUpdateAllQueuedPhotorealPrompts())
            && !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(
                    EnhancementJobsUpdateQueuedPromptsButton));

    public async Task<bool> CancelAllQueuedEnhancementJobsForSmokeAsync()
    {
        if (!CanCancelAllQueuedEnhancementJobs())
            return false;
        CancelAllQueuedEnhancementJobs_Click(this, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> RerunPhotorealJobForSmokeAsync(
        string id,
        bool enqueueNext = false)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null
            || (enqueueNext
                ? !job.CanRerunNextWithCurrentSettings
                : !job.CanRerunWithCurrentSettings))
            return false;
        await RerunPhotorealJobAsync(new Button { Tag = job }, enqueueNext);
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public string EnhancementJobsStatusForSmoke
        => EnhancementJobsStatusText.Text;

    public async Task<bool> DeleteEnhancementJobOutputForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanUseOutput)
            return false;
        var button = new Button { Tag = job };
        DeleteEnhancementOutput_Click(button, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public async Task<bool> DismissEnhancementJobForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        if (job is null || !job.CanDismiss)
            return false;
        DismissEnhancementJob_Click(new Button { Tag = job }, new RoutedEventArgs());
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        return true;
    }

    public bool OpenEnhancementJobOutputForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementWorkspaceOutput(job);
    }

    public async Task<bool> OpenEnhancementJobOutputForSmokeAsync(string id)
    {
        EnhancementWorkspaceJobView? job =
            _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null
            && await OpenEnhancementWorkspaceOutputAsync(job);
    }

    public ExplorerRevealSmokeSnapshot RevealEnhancementJobOutputForSmoke(
        string id)
    {
        ProcessStartInfo? captured = null;
        Func<ProcessStartInfo, bool> previous = _explorerLauncher;
        _explorerLauncher = startInfo =>
        {
            captured = startInfo;
            return true;
        };
        try
        {
            EnhancementWorkspaceJobView? job =
                _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
            bool opened = job is not null
                && TryOpenEnhancementWorkspaceOutput(job);
            return new ExplorerRevealSmokeSnapshot(
                opened && captured is not null,
                captured?.FileName ?? "",
                captured?.ArgumentList.ToList() ?? [],
                captured?.Arguments ?? "",
                captured?.UseShellExecute ?? false,
                job is { IsVideoOperation: true, CanUseOutput: true },
                false,
                EnhancementJobsStatusText.Text,
                "jobs-output");
        }
        finally
        {
            _explorerLauncher = previous;
        }
    }

    public async Task<ExplorerRevealSmokeSnapshot>
        RevealEnhancementJobOutputForSmokeAsync(
            string id,
            Task enhancedStateRefreshTask)
    {
        ProcessStartInfo? captured = null;
        Func<ProcessStartInfo, bool> previous = _explorerLauncher;
        Task previousRefreshTask = _enhancedStateRefreshTask;
        _enhancedStateRefreshTask = enhancedStateRefreshTask;
        _explorerLauncher = startInfo =>
        {
            captured = startInfo;
            return true;
        };
        try
        {
            EnhancementWorkspaceJobView? job =
                _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
            bool opened = job is not null
                && await OpenEnhancementWorkspaceOutputAsync(job);
            return new ExplorerRevealSmokeSnapshot(
                opened && captured is not null,
                captured?.FileName ?? "",
                captured?.ArgumentList.ToList() ?? [],
                captured?.Arguments ?? "",
                captured?.UseShellExecute ?? false,
                job is { IsVideoOperation: true, CanUseOutput: true },
                false,
                EnhancementJobsStatusText.Text,
                "jobs-output");
        }
        finally
        {
            _explorerLauncher = previous;
            if (ReferenceEquals(_enhancedStateRefreshTask, enhancedStateRefreshTask))
                _enhancedStateRefreshTask = previousRefreshTask;
        }
    }

    public async Task<bool> OpenEnhancementJobSourceInViewerForSmokeAsync(
        string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null
            && await OpenEnhancementSourceInViewerAsync(job);
    }

    public bool EnhancementJobsHeaderChromeContractForSmoke
        => WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsCloseButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsRefreshButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsFirstJobButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(
                EnhancementJobsScrollTopButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(
                EnhancementJobsScrollBottomButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsLastJobButton)
            && !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(EnhancementJobsFirstJobButton))
            && !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(EnhancementJobsScrollTopButton))
            && !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(EnhancementJobsScrollBottomButton))
            && !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(EnhancementJobsLastJobButton))
            && string.Equals(
                AutomationProperties.GetName(EnhancementJobsVideoFilter),
                "Show video generation jobs",
                StringComparison.Ordinal);

    public bool ActivateEnhancementJobsScrollTopForSmoke()
    {
        EnhancementJobsScrollTopButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        EnhancementJobsList.UpdateLayout();
        return EnhancementJobsVerticalOffsetForSmoke < 0.5;
    }

    public bool ActivateEnhancementJobsScrollBottomForSmoke()
    {
        EnhancementJobsScrollBottomButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        EnhancementJobsList.UpdateLayout();
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        return viewer is not null
            && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.5;
    }

    public bool ActivateEnhancementJobsFirstJobForSmoke()
    {
        EnhancementJobsFirstJobButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        EnhancementJobsList.UpdateLayout();
        return _enhancementWorkspacePageIndex == 0
            && EnhancementJobsVerticalOffsetForSmoke < 0.5;
    }

    public bool ActivateEnhancementJobsLastJobForSmoke()
    {
        EnhancementJobsLastJobButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        EnhancementJobsList.UpdateLayout();
        EnhancementJobsPageWindow last = CalculateEnhancementJobsPageWindow(
            _enhancementWorkspaceFilteredCount,
            int.MaxValue);
        ScrollViewer? viewer = FindVisualDescendant<ScrollViewer>(EnhancementJobsList);
        return _enhancementWorkspacePageIndex == last.PageIndex
            && viewer is not null
            && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.5;
    }

    public bool ActivateEnhancementJobsCloseForSmoke()
    {
        EnhancementJobsCloseButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        return EnhancementJobsDialog.Visibility != Visibility.Visible;
    }

    public object? EnhancementJobViewIdentityForSmoke(string id)
        => _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);

    public async Task RefreshEnhancementJobsForSmokeAsync()
    {
        await RefreshEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration, isPoll: false);
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
    }

    public async Task PollEnhancementJobsForSmokeAsync()
    {
        bool resumePolling = _enhancementWorkspacePollTimer.IsEnabled;
        _enhancementWorkspacePollTimer.Stop();
        _enhancementWorkspacePollCount++;
        try
        {
            await PollEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration);
            await WaitForEnhancementWorkspaceIdleForSmokeAsync();
        }
        finally
        {
            if (resumePolling
                && !_aiProcessingMinimizedMode
                && EnhancementJobsDialog.Visibility == Visibility.Visible
                && _enhancementWorkspaceJobs.Any(static job => job.IsActive))
            {
                _enhancementWorkspacePollTimer.Start();
            }
        }
    }

    public async Task WaitForEnhancementJobsReturnForSmokeAsync()
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (EnhancementJobsDialog.Visibility == Visibility.Visible
                && !_enhancementWorkspaceRefreshPending
                && !_enhancementJobsReturnViewportPending)
            {
                return;
            }
            await Task.Delay(10);
        }
    }

    private async Task WaitForEnhancementWorkspaceIdleForSmokeAsync()
    {
        for (int attempt = 0; attempt < 400 && (_enhancementWorkspaceRefreshPending || _enhancementWorkspaceMutationPending); attempt++)
            await Task.Delay(10);
    }

    public static bool TryReadI2iV2WorkspacePresentationForSmoke(
        JsonElement job,
        out string operation,
        out string presetSummary,
        out string detailText,
        out bool supportedMutation)
    {
        operation = "";
        presetSummary = "";
        detailText = "";
        supportedMutation = false;
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(job, 0);
        if (view is null)
            return false;
        operation = view.Operation;
        presetSummary = view.PresetSummary;
        detailText = view.DetailText;
        supportedMutation = view.IsSupportedMutationOperation;
        return true;
    }

    public static bool TryReadI2iV3WorkspacePresentationForSmoke(
        JsonElement job,
        out string operation,
        out string presetSummary,
        out string detailText,
        out bool supportedMutation,
        out string[] actionKinds)
    {
        operation = "";
        presetSummary = "";
        detailText = "";
        supportedMutation = false;
        actionKinds = [];
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(job, 0);
        if (view is null)
            return false;
        view.PhotorealEnqueueNextCapabilitySafe = true;
        operation = view.Operation;
        presetSummary = view.PresetSummary;
        detailText = view.DetailText;
        supportedMutation = view.IsSupportedMutationOperation;
        actionKinds = new[]
            {
                view.Action1,
                view.Action2,
                view.Action3,
                view.Action4,
                view.Action5,
                view.DangerAction,
            }
            .Where(static action => action.Visible)
            .Select(static action => action.Kind)
            .ToArray();
        return true;
    }

    public static bool TryReadEnhancementJobElapsedForSmoke(
        JsonElement job,
        out string? elapsedText,
        out string timestampText,
        out string accessibleName)
    {
        elapsedText = null;
        timestampText = "";
        accessibleName = "";
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(job, 0);
        if (view is null)
            return false;
        elapsedText = view.ElapsedText;
        timestampText = view.TimestampText;
        accessibleName = view.AccessibleName;
        return true;
    }

    public static bool TryReadEnhancementJobQueueReorderSafetyForSmoke(
        JsonElement job,
        out bool supportedMutation,
        out bool queueReorderSafe,
        out bool reorderControlsVisible)
    {
        supportedMutation = false;
        queueReorderSafe = false;
        reorderControlsVisible = false;
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            static _ => false);
        if (view is null)
            return false;
        view.ApplyQueuePresentation(queuePosition: 2, queueCount: 3, queueOrder: 1);
        supportedMutation = view.IsSupportedMutationOperation;
        queueReorderSafe = view.QueueReorderSafe;
        reorderControlsVisible = view.ShowReorderControls;
        return true;
    }

    public static bool TryReadEnhancementJobCancellationForSmoke(
        JsonElement job,
        out bool fullMutationSafe,
        out bool canCancel,
        out bool cancelVisible,
        out bool cancelEnabled,
        out string cancelLabel)
    {
        fullMutationSafe = false;
        canCancel = false;
        cancelVisible = false;
        cancelEnabled = false;
        cancelLabel = "";
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            static _ => false);
        if (view is null)
            return false;

        EnhancementJobActionPresentation action = view.Status == "queued"
            ? view.Action5
            : view.Action1;
        fullMutationSafe = view.IsSupportedMutationOperation;
        canCancel = view.CanCancel;
        cancelVisible = action.Visible && action.Kind == "cancel";
        cancelEnabled = action.Enabled;
        cancelLabel = action.Label;
        return true;
    }

    public static bool TryReadEnhancementJobRequestDetailsForSmoke(
        JsonElement job,
        out string requestDetails)
    {
        requestDetails = "";
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(job, 0);
        if (view is null)
            return false;
        requestDetails = view.RequestDetailsText;
        return true;
    }

    public static EnhancementJobLifecycleSmokeSnapshot?
        ReadEnhancementJobLifecycleForSmoke(JsonElement job)
    {
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            static _ => false);
        if (view is null)
            return null;
        if (view.Status == "queued")
        {
            view.ApplyQueuePresentation(
                queuePosition: 2,
                queueCount: 3,
                queueOrder: view.QueueOrder ?? 1);
        }

        string[] visibleActions = new[]
            { view.Action1, view.Action2, view.Action3, view.Action4,
                view.Action5, view.DangerAction }
            .Where(static action => action.Visible)
            .Select(static action => action.Kind)
            .ToArray();
        return new EnhancementJobLifecycleSmokeSnapshot(
            view.IsExactCurrentVideoToolsV2,
            view.IsVideoToolsReaderOnly,
            view.IsSupportedMutationOperation,
            view.VideoToolsKind,
            view.Status,
            view.CanCancel,
            view.CanRetry,
            view.CanDismiss,
            view.CanReorder,
            view.CanUseOutput,
            view.CanDeleteOutput,
            visibleActions);
    }

    public static bool IsMiniMaxH3VideoMutationSafeForSmoke(JsonElement job)
        => IsMiniMaxH3VideoMutationSafe(job);

    public static bool IsExactMiniMaxH3VideoSnapshotForSmoke(JsonElement video)
        => IsExactMiniMaxH3VideoSnapshot(video);

    public static string ComputeMiniMaxH3VideoSnapshotHashForSmoke(
        JsonElement video)
        => HashStableJson(video);

    internal static bool TryCompareVideoWorkspaceImmutableIdentityForSmoke(
        JsonElement left,
        JsonElement right,
        out bool sameIdentity,
        out string leftFingerprint,
        out string rightFingerprint,
        out string? leftPrompt,
        out string? rightPrompt)
    {
        sameIdentity = false;
        leftFingerprint = "";
        rightFingerprint = "";
        leftPrompt = null;
        rightPrompt = null;
        EnhancementWorkspaceJobView? leftView = ParseEnhancementWorkspaceJob(
            left,
            0);
        EnhancementWorkspaceJobView? rightView = ParseEnhancementWorkspaceJob(
            right,
            1);
        if (leftView is null
            || rightView is null
            || !leftView.TryGetVideoMutationProbe(
                out EnhancementVideoMutationProbe? leftProbe)
            || !rightView.TryGetVideoMutationProbe(
                out EnhancementVideoMutationProbe? rightProbe)
            || leftProbe is null
            || rightProbe is null)
        {
            return false;
        }

        sameIdentity = leftView.HasSameImmutableIdentity(rightView);
        leftFingerprint = leftProbe.EnvelopeSha256;
        rightFingerprint = rightProbe.EnvelopeSha256;
        leftPrompt = leftView.MiniMaxH3VideoSnapshot?.Prompt;
        rightPrompt = rightView.MiniMaxH3VideoSnapshot?.Prompt;
        return true;
    }

    public static string BuildVideoOutputFileNameForSmoke(
        string jobId,
        string sourcePath,
        string sourceSha256,
        string presetId,
        string presetHash)
        => BuildVideoOutputFileName(
            jobId,
            sourcePath,
            sourceSha256,
            presetId,
            presetHash);

    public bool TryReadMiniMaxH3WorkspacePresentationForSmoke(
        JsonElement job,
        out string operation,
        out string presetSummary,
        out bool mutationSafe,
        out bool canUseOutput)
    {
        operation = "";
        presetSummary = "";
        mutationSafe = false;
        canUseOutput = false;
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            IsVideoMutationSafe);
        if (view is null)
            return false;
        operation = view.Operation;
        presetSummary = view.PresetSummary;
        mutationSafe = view.VideoMutationSafe;
        canUseOutput = view.CanUseOutput;
        return true;
    }

    public bool TryReadMiniMaxH3WorkspaceSourceForSmoke(
        JsonElement job,
        out string sourceVersionLabel,
        out string sourceName,
        out string canonicalInput)
    {
        sourceVersionLabel = "";
        sourceName = "";
        canonicalInput = "";
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            IsVideoMutationSafe);
        if (view is null)
            return false;

        sourceVersionLabel = view.SourceVersionLabel;
        sourceName = view.SourceName;
        return TryResolveEnhancementWorkspaceInput(view, out canonicalInput);
    }

    public bool TryBuildMiniMaxH3VideoRerunForSmoke(
        JsonElement job,
        out string[] actionKinds,
        out string profileId,
        out int nominalDurationSeconds,
        out int maximumPixelArea,
        out int steps,
        out string prompt,
        out string sourceIdentity,
        out string displayPath,
        out bool usesDisplayedFileDirectly,
        out string requestJson)
    {
        actionKinds = [];
        profileId = "";
        nominalDurationSeconds = 0;
        maximumPixelArea = 0;
        steps = 0;
        prompt = "";
        sourceIdentity = "";
        displayPath = "";
        usesDisplayedFileDirectly = false;
        requestJson = "";

        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            IsVideoMutationSafe);
        if (view?.MiniMaxH3VideoSnapshot is not { } snapshot
            || !view.CanRerunMiniMaxH3VideoWithSavedPrompt
            || !view.CanEditMiniMaxH3VideoPrompt
            || !view.ActionPresentationMatchesCapabilitiesForSmoke()
            || !view.TryGetVideoMutationProbe(
                out EnhancementVideoMutationProbe? probe)
            || probe is null)
        {
            return false;
        }

        var context = new MiniMaxH3VideoExplicitActionContext(
            WorkspaceGeneration: 0,
            view,
            probe,
            snapshot,
            ResolvedManagedEnhancementOutputsRoot,
            SourcePathExternalGuard: null,
            SourceIdExternalGuard: null);
        MiniMaxH3VideoExplicitActionValidation validation = Task.Run(
                () => ValidateMiniMaxH3VideoExplicitActionOnWorker(
                    context,
                    validateContentHash: true,
                    validateCurrentCanvas: true))
            .GetAwaiter()
            .GetResult();
        if (!validation.IsValid || validation.Source is null)
            return false;
        VideoSourceChoice source = validation.Source;

        actionKinds = new[]
            { view.Action1, view.Action2, view.Action3, view.Action4,
                view.Action5, view.DangerAction }
            .Where(static action => action.Visible)
            .Select(static action => action.Kind)
            .ToArray();
        profileId = snapshot.ProfileId;
        nominalDurationSeconds = snapshot.NominalDurationSeconds;
        maximumPixelArea = snapshot.MaximumPixelArea;
        steps = snapshot.Steps;
        prompt = snapshot.Prompt;
        sourceIdentity = source.SourceIdentity;
        displayPath = source.DisplayPath;
        usesDisplayedFileDirectly = source.UsesDisplayedFileDirectly;
        requestJson = JsonSerializer.Serialize(
            BuildVideoGenerationRequestBody(
                source,
                MiniMaxH3VideoRerunRequestSettings(snapshot),
                h3Selected: true,
                seed: null));
        return true;
    }

    public string[] ReadMiniMaxH3VideoActionKindsForSmoke(JsonElement job)
    {
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            IsVideoMutationSafe);
        return view is null
            ? []
            : new[]
                { view.Action1, view.Action2, view.Action3, view.Action4,
                    view.Action5, view.DangerAction }
                .Where(static action => action.Visible)
                .Select(static action => action.Kind)
                .ToArray();
    }
}

public sealed class EnhancementWorkspaceJobView : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isBusy;
    private bool _isHighlighted;
    private bool _requestDetailsExpanded;
    private string _requestDetailsText;
    private bool _requestDetailsLoaded;
    private bool _queuedPhotorealPromptUpdateCapabilitySafe;
    private bool _photorealEnqueueNextCapabilitySafe;
    private EnhancementVideoMutationProbe? _videoMutationProbe;

    internal EnhancementWorkspaceJobView(
        string id,
        string sourceId,
        string sourcePath,
        string? sourceProducerJobId,
        string? sourceVideoJobId,
        string presetId,
        string adapterId,
        string operation,
        bool videoMutationSafe,
        bool queueReorderSafe,
        bool i2iMutationSafe,
        int? i2iSchemaVersion,
        string? i2iTarget,
        string? i2iInstructionSummary,
        bool i2iV2EnvelopeClaimed,
        string status,
        bool cancelRequested,
        int progress,
        string? outputPath,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        long? sourceSize,
        double? sourceMtimeMs,
        int? queueOrder,
        int apiOrdinal,
        string requestDetailsText,
        bool requestDetailsLoaded = true,
        I2iV3WorkspaceSnapshot? i2iV3Snapshot = null,
        MiniMaxH3VideoWorkspaceSnapshot? miniMaxH3VideoSnapshot = null,
        bool videoToolsEnvelopeClaimed = false,
        string? videoToolsKind = null,
        string? videoToolsFinishMode = null,
        VideoToolsV2ReaderSnapshot? videoToolsV2Snapshot = null,
        bool videoTrimEnvelopeClaimed = false,
        VideoTrimV1ReaderSnapshot? videoTrimV1Snapshot = null)
    {
        Id = id;
        SourceId = sourceId;
        SourcePath = sourcePath;
        SourceProducerJobId = sourceProducerJobId;
        SourceVideoJobId = sourceVideoJobId;
        PresetId = presetId;
        AdapterId = adapterId;
        Operation = operation;
        VideoMutationSafe = videoMutationSafe;
        QueueReorderSafe = queueReorderSafe;
        I2iMutationSafe = i2iMutationSafe;
        I2iSchemaVersion = i2iSchemaVersion;
        I2iTarget = i2iTarget;
        I2iInstructionSummary = i2iInstructionSummary;
        I2iV2EnvelopeClaimed = i2iV2EnvelopeClaimed;
        I2iV3Snapshot = i2iV3Snapshot;
        MiniMaxH3VideoSnapshot = miniMaxH3VideoSnapshot;
        VideoToolsEnvelopeClaimed = videoToolsEnvelopeClaimed;
        VideoToolsKind = videoToolsKind;
        VideoToolsFinishMode = videoToolsFinishMode;
        VideoToolsV2Snapshot = videoToolsV2Snapshot;
        VideoTrimEnvelopeClaimed = videoTrimEnvelopeClaimed;
        VideoTrimV1Snapshot = videoTrimV1Snapshot;
        Status = status;
        CancelRequested = cancelRequested;
        Progress = progress;
        OutputPath = outputPath;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        SourceSize = sourceSize;
        SourceMtimeMs = sourceMtimeMs;
        QueueOrder = queueOrder;
        ApiOrdinal = apiOrdinal;
        _requestDetailsText = requestDetailsText;
        _requestDetailsLoaded = requestDetailsLoaded;
    }

    public string Id { get; }
    public string SourceId { get; }
    public string SourcePath { get; }
    public string? SourceProducerJobId { get; }
    public string? SourceVideoJobId { get; }
    public string PresetId { get; }
    public string AdapterId { get; }
    public string Operation { get; }
    public bool VideoMutationSafe { get; private set; }
    public bool QueueReorderSafe { get; }
    public bool I2iMutationSafe { get; }
    public int? I2iSchemaVersion { get; }
    public string? I2iTarget { get; }
    public string? I2iInstructionSummary { get; }
    public bool I2iV2EnvelopeClaimed { get; }
    public I2iV3WorkspaceSnapshot? I2iV3Snapshot { get; }
    public MiniMaxH3VideoWorkspaceSnapshot? MiniMaxH3VideoSnapshot { get; }
    public bool VideoToolsEnvelopeClaimed { get; }
    public string? VideoToolsKind { get; }
    public string? VideoToolsFinishMode { get; }
    internal VideoToolsV2ReaderSnapshot? VideoToolsV2Snapshot { get; }
    public bool VideoTrimEnvelopeClaimed { get; }
    internal VideoTrimV1ReaderSnapshot? VideoTrimV1Snapshot { get; }
    public bool IsExactCurrentVideoToolsV2 => VideoToolsV2Snapshot is not null;
    public bool IsExactCurrentVideoTrimV1 => VideoTrimV1Snapshot is not null;
    public bool IsVideoToolsReaderOnly =>
        VideoToolsEnvelopeClaimed && !IsExactCurrentVideoToolsV2;
    public bool IsVideoTrimReaderOnly =>
        VideoTrimEnvelopeClaimed && !IsExactCurrentVideoTrimV1;
    public bool IsProtectedVideoReaderOnly =>
        IsVideoToolsReaderOnly || IsVideoTrimReaderOnly;
    public string Status { get; private set; }
    public bool CancelRequested { get; private set; }
    public int Progress { get; private set; }
    public string? OutputPath { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public long? SourceSize { get; }
    public double? SourceMtimeMs { get; }
    public int? QueueOrder { get; private set; }
    public int ApiOrdinal { get; }
    public int? QueuePosition { get; set; }
    public int QueueCount { get; set; }
    public bool IsActive => Status is "queued" or "running";
    public bool IsImageOperation => Operation is "upscale" or "photoreal" or "i2i";
    public bool IsVideoOperation => Operation == "video";
    public bool IsKnownOperation =>
        Operation is "upscale" or "photoreal" or "i2i" or "video";
    public bool IsSupportedMutationOperation =>
        IsExactCurrentVideoToolsV2
        || IsExactCurrentVideoTrimV1
        || !IsProtectedVideoReaderOnly
            && (Operation is "upscale" or "photoreal"
                || (Operation == "i2i" && I2iMutationSafe)
                || (IsVideoOperation && VideoMutationSafe));
    public bool OutputDependencyProtected { get; set; }
    public bool QueueMutationScopeSafe { get; set; } = true;
    public bool CanCancel =>
        !_isBusy
        && !CancelRequested
        && !IsProtectedVideoReaderOnly
        && (Status is "queued" or "running"
            ? IsKnownOperation
            : !IsExactCurrentVideoToolsV2
                && !IsExactCurrentVideoTrimV1
                && Status == "failed"
                && IsSupportedMutationOperation);
    public bool ShowCancelAction =>
        !IsProtectedVideoReaderOnly
        && (Status is "queued" or "running"
            ? IsKnownOperation
            : !IsExactCurrentVideoToolsV2
                && !IsExactCurrentVideoTrimV1
                && Status == "failed"
                && IsSupportedMutationOperation);
    public string CancelToolTip => CanCancel
        ? Status == "queued"
            ? "この待機Jobだけをキャンセルします。実行中のJobは変更しません"
            : "この処理へ中断要求を送ります。現在の安全な境界で停止します"
        : CancelRequested
            ? "中断要求を受付済みです"
            : _isBusy
                ? "このJobの別操作が完了するまで待ってください"
                : "このJobは安全にキャンセルできないため保護されています";
    public bool CanRetry =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status is "failed" or "canceled";
    public string RetryLabel => VideoToolsV2Snapshot?.Kind == "edit"
        ? "同じ設定でAI動画編集をRetry"
        : VideoToolsV2Snapshot?.Kind == "finish"
            ? "同じモードで高画質化をRetry"
        : VideoTrimV1Snapshot is not null
            ? "同じ区間で動画トリムをRetry"
        : IsVideoOperation
            ? "動画をやり直す"
        : "元設定でRetry";
    public string RetryToolTip => VideoToolsV2Snapshot?.Kind == "edit"
        ? "保存されたEdit snapshot・Seed・Job所有入力を変更せず再試行"
        : VideoToolsV2Snapshot?.Kind == "finish"
            ? "保存されたモード・倍率・Job所有入力を変更せず再試行"
        : VideoTrimV1Snapshot is not null
            ? "保存されたexact frame区間・音声policy・Job所有入力を変更せず再試行"
        : IsVideoOperation
            ? "失敗・キャンセルした動画を、保存された長さ・STEP数・Prompt・Seedで再生成"
        : "失敗・キャンセル時に保存された元の設定で再試行";
    public bool CanDismiss =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status is "failed" or "canceled" or "deleted";
    public bool ShowReorderControls =>
        QueueReorderSafe
        && QueueMutationScopeSafe
        && Status == "queued";
    public bool CanReorder => !_isBusy && ShowReorderControls;
    public bool ShowMoveUp => ShowReorderControls && QueuePosition is > 1;
    public bool ShowMoveDown => ShowReorderControls
        && QueuePosition is int position
        && position < QueueCount;
    public bool ShowMoveNext => ShowMoveUp;
    public bool CanMoveUp => !_isBusy && ShowMoveUp;
    public bool CanMoveDown => !_isBusy && ShowMoveDown;
    public bool CanMoveNext => !_isBusy && ShowMoveNext;
    public bool CanRerunWithCurrentSettings =>
        !_isBusy
        && Operation == "photoreal"
        && Status is "succeeded" or "failed" or "canceled";
    public bool CanRerunNextWithCurrentSettings =>
        CanRerunWithCurrentSettings && PhotorealEnqueueNextCapabilitySafe;
    public bool CanRerunMiniMaxH3VideoWithSavedPrompt =>
        !_isBusy
        && Operation == "video"
        && VideoMutationSafe
        && MiniMaxH3VideoSnapshot is not null
        && Status == "succeeded";
    public bool CanEditMiniMaxH3VideoPrompt =>
        CanRerunMiniMaxH3VideoWithSavedPrompt;
    public bool CanRerunI2iV3 =>
        !_isBusy
        && I2iV3Snapshot is not null
        && I2iMutationSafe
        && Status is "succeeded" or "failed" or "canceled";
    public bool CanRerunI2iV3Next =>
        CanRerunI2iV3 && PhotorealEnqueueNextCapabilitySafe;
    public bool CanEditI2iV3Settings => CanRerunI2iV3;
    public bool CanUpdatePhotorealPrompts =>
        !_isBusy
        && !CancelRequested
        && Status == "queued"
        && Operation == "photoreal"
        && AdapterId == "comfyui-flux2-photoreal"
        && QueuedPhotorealPromptUpdateCapabilitySafe;
    public bool QueuedPhotorealPromptUpdateCapabilitySafe
    {
        get => _queuedPhotorealPromptUpdateCapabilitySafe;
        set
        {
            if (_queuedPhotorealPromptUpdateCapabilitySafe == value)
                return;
            _queuedPhotorealPromptUpdateCapabilitySafe = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(QueuedPhotorealPromptUpdateCapabilitySafe)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(CanUpdatePhotorealPrompts)));
            NotifyActionPresentationsChanged();
        }
    }
    public bool PhotorealEnqueueNextCapabilitySafe
    {
        get => _photorealEnqueueNextCapabilitySafe;
        set
        {
            if (_photorealEnqueueNextCapabilitySafe == value)
                return;
            _photorealEnqueueNextCapabilitySafe = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(PhotorealEnqueueNextCapabilitySafe)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(CanRerunNextWithCurrentSettings)));
            NotifyActionPresentationsChanged();
        }
    }
    public bool CanUseOutput =>
        !_isBusy
        && (IsSupportedMutationOperation
            || CanUseVideoToolsV2Output
            || CanUseVideoTrimV1Output)
        && Status == "succeeded"
        && !string.IsNullOrWhiteSpace(OutputPath);
    public bool CanUseVideoToolsV2Output =>
        !_isBusy
        && VideoToolsV2Snapshot is not null
        && Status == "succeeded"
        && OutputPath is { Length: > 0 and <= 32768 } outputPath
        && Path.IsPathFullyQualified(outputPath)
        && string.Equals(
            Path.GetExtension(outputPath),
            ".mp4",
            StringComparison.OrdinalIgnoreCase);
    public bool CanUseVideoTrimV1Output =>
        !_isBusy
        && VideoTrimV1Snapshot is not null
        && Status == "succeeded"
        && OutputPath is { Length: > 0 and <= 32768 } outputPath
        && Path.IsPathFullyQualified(outputPath)
        && string.Equals(
            Path.GetExtension(outputPath),
            ".mp4",
            StringComparison.OrdinalIgnoreCase);
    public bool CanDeleteOutput =>
        CanUseOutput
        && IsSupportedMutationOperation
        && !OutputDependencyProtected;
    public string RequestDetailsText => _requestDetailsText;
    public bool RequestDetailsLoaded => _requestDetailsLoaded;
    public bool RequestDetailsExpanded
    {
        get => _requestDetailsExpanded;
        set
        {
            if (_requestDetailsExpanded == value)
                return;
            _requestDetailsExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsButtonLabel)));
        }
    }
    public string RequestDetailsButtonLabel =>
        RequestDetailsExpanded ? "詳細を閉じる" : "詳細";

    internal void AttachVideoMutationProbe(
        EnhancementVideoMutationProbe probe)
        => _videoMutationProbe = probe;

    internal bool TryGetVideoMutationProbe(
        out EnhancementVideoMutationProbe? probe)
    {
        if (_videoMutationProbe is EnhancementVideoMutationProbe stored)
        {
            probe = stored;
            return true;
        }

        probe = null;
        return false;
    }

    internal void ApplyRequestDetails(string details)
    {
        string next = details ?? "";
        bool detailsChanged = !string.Equals(
            _requestDetailsText,
            next,
            StringComparison.Ordinal);
        bool loadedChanged = !_requestDetailsLoaded;
        _requestDetailsText = next;
        _requestDetailsLoaded = true;
        if (detailsChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsText)));
        }
        if (loadedChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsLoaded)));
        }
    }

    internal void ClearRequestDetails()
    {
        if (!_requestDetailsLoaded && _requestDetailsText.Length == 0)
            return;
        _requestDetailsText = "";
        _requestDetailsLoaded = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
            nameof(RequestDetailsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
            nameof(RequestDetailsLoaded)));
    }
    public EnhancementJobActionPresentation Action1 =>
        IsProtectedVideoReaderOnly
        ? IsVideoToolsReaderOnly && CanUseVideoToolsV2Output
            ? JobAction(
                "open-output",
                "Open output",
                OpenOutputToolTip,
                visible: true,
                enabled: true,
                minWidth: 88)
            : EnhancementJobActionPresentation.Hidden
        : CanRerunMiniMaxH3VideoWithSavedPrompt
        ? JobAction(
            "video-rerun-saved",
            "同じPromptでもう一度",
            "保存された長さ・STEP・解像度・Promptで新しい動画を追加。Seedは新しく生成",
            visible: true,
            enabled: true,
            minWidth: 138)
        : CanRerunI2iV3
            ? JobAction(
            "i2i-v3-rerun",
            "同じ設定でもう一度",
            "保存された5欄のPrompt・STEP・CFG・マスク・Seedをそのまま使って追加",
            visible: true,
            enabled: true,
            minWidth: 126)
        : Status switch
    {
        "queued" => JobAction(
            "move-up",
            "↑ 上へ",
            "待機順を1つ上へ",
            ShowMoveUp,
            CanMoveUp,
            58),
        "running" or "failed" => JobAction(
            "cancel",
            CancelLabel,
            CancelToolTip,
            ShowCancelAction,
            CanCancel,
            76),
        "canceled" => JobAction(
            "retry",
            RetryLabel,
            RetryToolTip,
            CanRetry,
            CanRetry,
            104),
        "succeeded" => JobAction(
            "rerun",
            "現在設定で再実写化",
            "設定画面の現在のPromptと実写化設定で新しいジョブを追加",
            CanRerunWithCurrentSettings,
            CanRerunWithCurrentSettings,
            126),
        _ => EnhancementJobActionPresentation.Hidden,
    };
    public EnhancementJobActionPresentation Action2 =>
        IsProtectedVideoReaderOnly
        ? EnhancementJobActionPresentation.Hidden
        : CanEditMiniMaxH3VideoPrompt
        ? JobAction(
            "video-edit-prompt",
            "Promptを変えて生成",
            "この動画の入力画像と保存設定を動画化ボードへ読み込みます。開くだけでは追加しません",
            visible: true,
            enabled: true,
            minWidth: 136)
        : CanRerunI2iV3
            ? JobAction(
            "i2i-v3-rerun-next",
            "同じ設定を次に",
            "保存された設定のまま、処理中ジョブの直後へ追加",
            visible: true,
            enabled: CanRerunI2iV3Next,
            minWidth: 116)
        : Status switch
    {
        "queued" => JobAction(
            "move-down",
            "↓ 下へ",
            "待機順を1つ下へ",
            ShowMoveDown,
            CanMoveDown,
            58),
        "failed" => JobAction(
            "retry",
            RetryLabel,
            RetryToolTip,
            CanRetry,
            CanRetry,
            104),
        "canceled" => JobAction(
            "rerun",
            "現在設定で再実写化",
            "設定画面の現在のPromptと実写化設定で新しいジョブを追加",
            CanRerunWithCurrentSettings,
            CanRerunWithCurrentSettings,
            126),
        "succeeded" => JobAction(
            "rerun-next",
            "現在設定で次に実写化",
            "現在の設定で、処理中ジョブの直後へ原子的に追加",
            CanRerunNextWithCurrentSettings,
            CanRerunNextWithCurrentSettings,
            144),
        _ => EnhancementJobActionPresentation.Hidden,
    };
    public EnhancementJobActionPresentation Action3 => IsProtectedVideoReaderOnly
        ? EnhancementJobActionPresentation.Hidden
        : CanRerunI2iV3
            ? JobAction(
            "i2i-v3-edit",
            "設定を編集して再実行",
            "元の5欄と数値設定を統合AI編集へ読み込みます。開くだけでは追加しません",
            visible: true,
            enabled: CanEditI2iV3Settings,
            minWidth: 136)
        : Status switch
    {
        "queued" => JobAction(
            "move-next",
            "これを次に処理",
            "画面へすぐ反映し、処理中ジョブの直後へ保存",
            ShowMoveNext,
            CanMoveNext,
            96,
            "このジョブを次に処理"),
        "failed" => JobAction(
            "rerun",
            "現在設定で再実写化",
            "設定画面の現在のPromptと実写化設定で新しいジョブを追加",
            CanRerunWithCurrentSettings,
            CanRerunWithCurrentSettings,
            126),
        "canceled" => JobAction(
            "rerun-next",
            "現在設定で次に実写化",
            "現在の設定で、処理中ジョブの直後へ原子的に追加",
            CanRerunNextWithCurrentSettings,
            CanRerunNextWithCurrentSettings,
            144),
        "succeeded" => JobAction(
            "open-output",
            "Open output",
            OpenOutputToolTip,
            CanUseOutput,
            CanUseOutput,
            88),
        _ => EnhancementJobActionPresentation.Hidden,
    };
    public EnhancementJobActionPresentation Action4 =>
        IsProtectedVideoReaderOnly
        ? EnhancementJobActionPresentation.Hidden
        : I2iV3Snapshot is not null && Status == "succeeded"
        ? JobAction(
            "open-output",
            "Open output",
            OpenOutputToolTip,
            CanUseOutput,
            CanUseOutput,
            88)
        : Status switch
    {
        "queued" => JobAction(
            "update-prompts",
            "現在設定へ更新",
            "現在のPrompt・LoRA・強さ・CFG・品質・解像度・Seedへ更新。元画像ごとの個別Prompt変換と待ち順は維持",
            CanUpdatePhotorealPrompts,
            CanUpdatePhotorealPrompts,
            114),
        "failed" => JobAction(
            "rerun-next",
            "現在設定で次に実写化",
            "現在の設定で、処理中ジョブの直後へ原子的に追加",
            CanRerunNextWithCurrentSettings,
            CanRerunNextWithCurrentSettings,
            144),
        _ => EnhancementJobActionPresentation.Hidden,
    };
    public EnhancementJobActionPresentation Action5 => IsProtectedVideoReaderOnly
        ? EnhancementJobActionPresentation.Hidden
        : Status == "queued"
        ? JobAction(
            "cancel",
            CancelLabel,
            CancelToolTip,
            ShowCancelAction,
            CanCancel,
            76)
        : EnhancementJobActionPresentation.Hidden;
    public EnhancementJobActionPresentation DangerAction =>
        IsProtectedVideoReaderOnly
        ? EnhancementJobActionPresentation.Hidden
        : Status switch
    {
        "failed" or "canceled" or "deleted" => JobAction(
            "dismiss",
            "Remove",
            "Remove this terminal job from history. Source and output files are not changed.",
            CanDismiss,
            CanDismiss,
            72,
            "Remove terminal job from history"),
        "succeeded" => JobAction(
            "delete-output",
            "Delete output",
            "",
            CanDeleteOutput,
            CanDeleteOutput,
            94),
        _ => EnhancementJobActionPresentation.Hidden,
    };

    private static EnhancementJobActionPresentation JobAction(
        string kind,
        string label,
        string toolTip,
        bool visible,
        bool enabled,
        double minWidth,
        string? automationName = null)
        => new(
            kind,
            label,
            toolTip,
            visible,
            enabled,
            minWidth,
            automationName ?? label);

    public bool ActionPresentationMatchesCapabilitiesForSmoke()
    {
        if (CanRerunI2iV3)
        {
            var expectedV3 = new List<string>
            {
                "i2i-v3-rerun",
                "i2i-v3-rerun-next",
                "i2i-v3-edit",
            };
            if (CanUseOutput)
                expectedV3.Add("open-output");
            if (CanDismiss)
                expectedV3.Add("dismiss");
            if (CanDeleteOutput)
                expectedV3.Add("delete-output");
            string[] actualV3 = new[]
                { Action1, Action2, Action3, Action4, Action5, DangerAction }
                .Where(static action => action.Visible)
                .Select(static action => action.Kind)
                .ToArray();
            return expectedV3.SequenceEqual(actualV3, StringComparer.Ordinal);
        }

        var expected = new List<string>(6);
        if (CanRerunMiniMaxH3VideoWithSavedPrompt)
            expected.Add("video-rerun-saved");
        if (CanEditMiniMaxH3VideoPrompt)
            expected.Add("video-edit-prompt");
        if (ShowMoveUp)
            expected.Add("move-up");
        if (ShowMoveDown)
            expected.Add("move-down");
        if (ShowMoveNext)
            expected.Add("move-next");
        if (CanUpdatePhotorealPrompts)
            expected.Add("update-prompts");
        if (ShowCancelAction)
            expected.Add("cancel");
        if (CanRetry)
            expected.Add("retry");
        if (CanRerunWithCurrentSettings)
            expected.Add("rerun");
        if (CanRerunNextWithCurrentSettings)
            expected.Add("rerun-next");
        if (CanDismiss)
            expected.Add("dismiss");
        if (CanUseOutput)
            expected.Add("open-output");
        if (CanDeleteOutput)
            expected.Add("delete-output");

        EnhancementJobActionPresentation[] actionSlots =
            [Action1, Action2, Action3, Action4, Action5, DangerAction];
        string[] actual = actionSlots
            .Where(static action => action.Visible)
            .Select(static action => action.Kind)
            .ToArray();
        return expected.SequenceEqual(actual, StringComparer.Ordinal);
    }
    public string ThumbnailToolTip => IsVideoOperation && CanUseOutput
        ? "完成動画をAibosの拡大ビューで再生"
        : "元画像をAibosのビューワーで開く";
    public string OpenOutputToolTip => IsVideoOperation
        ? "Explorerで完成動画の保存先を開く"
        : "このAI処理版をAibosの拡大ビューで開く";
    public string CancelLabel => Status switch
    {
        "queued" => "待機を削除",
        "running" when Operation == "photoreal" => "実写化を中止",
        "running" when Operation == "i2i" => "AI編集を中止",
        "running" when VideoToolsV2Snapshot?.Kind == "edit" =>
            "AI動画編集を中止",
        "running" when VideoToolsV2Snapshot?.Kind == "finish" =>
            "AI動画高画質化を中止",
        "running" when VideoTrimV1Snapshot is not null =>
            "動画トリムを中止",
        "running" when Operation == "video" => "動画化を中止",
        "running" when !IsImageOperation => "未対応操作",
        "running" => "高画質化を中止",
        _ => "キャンセル済みにする",
    };
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath) ? "Unknown source" : Path.GetFileName(SourcePath);
    public string SourceVersionLabel => VideoToolsV2Snapshot is { } v2Source
        ? v2Source.SourceKind == "managed-video-job"
            ? "管理動画"
            : "外部動画（Job所有コピー）"
        : VideoTrimV1Snapshot is { } trimSource
            ? trimSource.SourceKind == "managed-video-job"
                ? "管理動画"
                : "外部動画（Job所有コピー）"
        : IsVideoToolsReaderOnly
        ? "管理動画"
        : IsVideoTrimReaderOnly
        ? "管理動画（保護中）"
        : (IsVideoOperation || Operation == "i2i")
        && !string.IsNullOrWhiteSpace(SourceProducerJobId)
            ? "実写版"
            : IsVideoOperation
                ? ManagedStillSourceVersionLabel(SourcePath) ?? "Original"
                : "Original";

    private static string? ManagedStillSourceVersionLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            string[] parts = Path.GetFullPath(path)
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
            if (parts.Contains("Photorealized", StringComparer.Ordinal))
                return "実写版";
            if (parts.Contains("Upscaled", StringComparer.Ordinal))
                return "高画質版";
            if (parts.Contains("Edited", StringComparer.Ordinal))
                return "AI編集版";
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return null;
    }
    public string PresetSummary => VideoToolsV2Snapshot is { Kind: "edit" } editV2
        ? $"Video Tools v2 · AI動画編集 · [{editV2.SelectionStartFrame}, {editV2.SelectionEndFrameExclusive}) · 非破壊child clip"
        : VideoToolsV2Snapshot is { Kind: "finish" } finishV2
            ? $"Video Tools v2 · AI動画高画質化 · {finishV2.FinishMode} · {finishV2.FinishScale}x"
        : VideoTrimV1Snapshot is { } trimV1
            ? $"Video Trim v1 · [{trimV1.SelectionStartFrame}, {trimV1.SelectionEndFrameExclusive}) · {trimV1.OutputFrameCount} frame · {trimV1.AudioPolicy}"
        : VideoToolsKind == "retake"
        ? "Video Tools · 区間を作り直す · Reader-only"
        : VideoToolsKind == "finish"
            ? $"Video Tools · 動画高画質化 2x · {(VideoToolsFinishMode == "detail" ? "Detail" : "Faithful")} · Reader-only"
        : VideoToolsEnvelopeClaimed
            ? "Video Tools · 互換性を確認できないため保護"
        : VideoTrimEnvelopeClaimed
            ? "Video Trim · 互換性を確認できないため保護"
        : IsVideoOperation
        ? $"{(PresetId switch
        {
            "minimax-h3-i2v-preview-v1" => "MiniMax H3 Preview · 24 fps · 音声あり",
            "wan22-ti2v-5b-normal-v1" => "Wan2.2 TI2V 5B · 標準 · 20 step",
            "wan22-ti2v-5b-high-v1" => "Wan2.2 TI2V 5B · 高品質 · 40 step",
            _ => PresetId,
        })}  ·  {SourceVersionLabel}"
        : Operation == "i2i" && I2iV3Snapshot is I2iV3WorkspaceSnapshot v3
            ? $"Schema v3  ·  {I2iTarget ?? "統合編集"}  ·  STEP {v3.Steps}  ·  CFG {v3.CfgScale.ToString("0.0", CultureInfo.InvariantCulture)}  ·  {SourceVersionLabel}"
        : Operation == "i2i" && I2iMutationSafe
            ? $"Schema v{I2iSchemaVersion ?? 0}  ·  Target: {I2iTargetDisplayLabel}  ·  {SourceVersionLabel}"
        : $"{PresetId}  ·  {AdapterId}";
    public string OperationLabel => BuildOperationLabel();

    private string BuildOperationLabel()
    {
        if (VideoToolsV2Snapshot?.Kind == "edit")
            return "VIDEO EDIT  AI動画編集";
        if (VideoToolsV2Snapshot?.Kind == "finish")
            return "VIDEO HQ  AI動画高画質化";
        if (VideoTrimV1Snapshot is not null)
            return "VIDEO TRIM  動画トリム";
        if (VideoToolsKind == "retake")
            return "RETAKE  区間を作り直す";
        if (VideoToolsKind == "finish")
            return "VIDEO HQ  動画高画質化";
        if (VideoToolsEnvelopeClaimed)
            return "VIDEO TOOLS  保護中";
        if (VideoTrimEnvelopeClaimed)
            return "VIDEO TRIM  保護中";
        return Operation switch
        {
            "upscale" => "HQ  高画質化",
            "photoreal" => "REAL  実写化",
            "i2i" => "EDIT  AI編集",
            "video" => "VIDEO  動画化",
            _ => "UNSUPPORTED  未対応",
        };
    }
    public string? VideoKindFilterKey => !IsVideoOperation
        ? null
        : VideoTrimV1Snapshot is not null
            ? "trim"
            : VideoToolsV2Snapshot?.Kind
                ?? (VideoToolsEnvelopeClaimed || VideoTrimEnvelopeClaimed
                    ? null
                    : "generation");
    public string I2iTargetDisplayLabel => I2iTarget switch
    {
        "hair-color" => "髪色",
        "outfit" => "服装",
        "expression" => "表情",
        "background" => "場所・背景",
        "pose" => "ポーズ（実験的）",
        _ => "未対応",
    };
    private bool IsStructuredI2iEnvelope =>
        I2iV2EnvelopeClaimed
        || string.Equals(PresetId, "flux2-i2i-edit-v2", StringComparison.Ordinal)
        || string.Equals(
            AdapterId,
            "comfyui-flux2-i2i-v2",
            StringComparison.Ordinal);
    private string SafeI2iV2DetailText => !I2iMutationSafe
        ? "This AI edit row is incomplete or incompatible and remains protected from mutations."
        : I2iV3Snapshot is I2iV3WorkspaceSnapshot v3
            ? $"{I2iTarget ?? "統合編集"} · STEP {v3.Steps} · CFG {v3.CfgScale.ToString("0.0", CultureInfo.InvariantCulture)} · 服装マスク {v3.OutfitMaskMode} {v3.OutfitMaskExpandPixels}px"
        : !string.IsNullOrWhiteSpace(I2iInstructionSummary)
            ? $"{I2iTargetDisplayLabel}: {I2iInstructionSummary}"
            : $"{I2iTargetDisplayLabel}: verified public instruction is unavailable.";
    private string ProgressPercentText =>
        $"{Progress.ToString("0", CultureInfo.InvariantCulture)}%";
    private string PersistedStatusLabel => CancelRequested && Status == "running"
        ? $"中止処理中  ·  Running {ProgressPercentText}"
        : Status switch
        {
            "queued" => $"待ち順 {QueuePosition ?? 0}  ·  Queued {ProgressPercentText}",
            "running" => $"実行中  ·  Running {ProgressPercentText}",
            "succeeded" => "Completed",
            "failed" => "Failed",
            "canceled" => "Canceled",
            "deleted" => "Output deleted",
            _ => Status,
        };
    public string StatusLabel => _isBusy
        ? $"反映中  ·  {PersistedStatusLabel}"
        : PersistedStatusLabel;
    public string DetailText => VideoToolsV2Snapshot is { Kind: "edit" } editV2
        ? $"Video Tools v2 Edit Jobです。管理元は{SourceVersionLabel}、出力は[{editV2.SelectionStartFrame}, {editV2.SelectionEndFrameExclusive})だけの非破壊child clipです。状態に応じたJobs操作は認証済みCompanionが最終判定します。"
        : VideoToolsV2Snapshot is { Kind: "finish" } finishV2
            ? $"Video Tools v2 Finish Jobです。管理元は{SourceVersionLabel}、{finishV2.FinishScale}x出力でもfps・フレーム数・長さ・元音声を維持します。状態に応じたJobs操作は認証済みCompanionが最終判定します。"
        : VideoTrimV1Snapshot is { } trimV1
            ? $"Video Trim v1 Jobです。管理元は{SourceVersionLabel}、出力は[{trimV1.SelectionStartFrame}, {trimV1.SelectionEndFrameExclusive})の{trimV1.OutputFrameCount} frameです。映像と保持音声はexact区間へ再エンコードし、元動画は変更しません。"
        : VideoToolsKind == "retake"
        ? "Retake snapshotを読取専用で表示しています。選択区間と実際の差し替え窓、全尺・元音声保持の情報は保存済みです。runtime検証前の変更操作は無効です。"
        : VideoToolsKind == "finish"
            ? "Video Finish 2x snapshotを読取専用で表示しています。fps・フレーム数・長さ・音声保持の情報は保存済みです。runtime検証前の変更操作は無効です。"
        : VideoToolsEnvelopeClaimed
            ? "This Video Tools row is malformed, future, or incomplete and remains protected from every mutation action."
        : VideoTrimEnvelopeClaimed
            ? "This Video Trim row is malformed, future, or incomplete and remains protected from every mutation and output action."
        : IsStructuredI2iEnvelope
        ? SafeI2iV2DetailText
        : !string.IsNullOrWhiteSpace(ErrorMessage)
        ? ErrorMessage
        : CancelRequested && Status == "running"
            ? "Cancel requested. Waiting for the exact GPU prompt to settle before the next job starts."
        : IsVideoOperation
            ? !VideoMutationSafe
                ? "This video row is incomplete or incompatible and remains protected from mutations."
                : Status == "succeeded"
                    ? $"Managed video output from {SourceVersionLabel} is separate from the original source image."
                    : $"Video generation from {SourceVersionLabel} uses the same durable queue and GPU worker as image Enhancement."
            : Operation == "i2i"
                ? !I2iMutationSafe
                    ? "This AI edit row is incomplete or incompatible and remains protected from mutations."
                    : Status == "succeeded"
                        ? $"Managed AI edit output from {SourceVersionLabel} is separate from the original source image."
                        : $"AI hair-color editing from {SourceVersionLabel} uses the same durable queue and GPU worker."
            : !IsImageOperation
                ? "This operation is unsupported and protected from image actions."
                : Status == "succeeded"
                    ? "Managed output is separate from the original source."
                    : Status == "deleted"
                        ? "Managed output removed; original source kept."
                        : "Original source remains unchanged.";
    public TimeSpan? CompletedElapsed =>
        Status == "succeeded"
        && StartedAt is DateTimeOffset startedAt
        && FinishedAt is DateTimeOffset finishedAt
        && finishedAt >= startedAt
            ? finishedAt - startedAt
            : null;
    public string? ElapsedText => CompletedElapsed is TimeSpan elapsed
        ? $"所要 {FormatElapsedDuration(elapsed)}"
        : null;
    public string TimestampText
    {
        get
        {
            if (Status == "running")
            {
                return StartedAt is DateTimeOffset startedAt
                    ? $"Started {startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : "Start time unavailable";
            }
            string updated = UpdatedAt == DateTimeOffset.MinValue
                ? "Time unavailable"
                : $"Updated {UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            return ElapsedText is { } elapsed
                ? $"{updated} · {elapsed}"
                : updated;
        }
    }
    public string AccessibleName => ElapsedText is { } elapsed
        ? $"{SourceName}, {OperationLabel}, {StatusLabel}, {PresetId}, {elapsed}"
        : $"{SourceName}, {OperationLabel}, {StatusLabel}, {PresetId}";

    private static string FormatElapsedDuration(TimeSpan elapsed)
    {
        long totalSeconds = Math.Max(
            0,
            (long)Math.Round(
                elapsed.TotalSeconds,
                MidpointRounding.AwayFromZero));
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;
        if (hours > 0)
            return $"{hours}時間 {minutes}分 {seconds}秒";
        if (minutes > 0)
            return $"{minutes}分 {seconds}秒";
        return $"{seconds}秒";
    }

    public bool HasSameImmutableIdentity(EnhancementWorkspaceJobView candidate)
        => string.Equals(SourceId, candidate.SourceId, StringComparison.Ordinal)
            && string.Equals(SourcePath, candidate.SourcePath, StringComparison.Ordinal)
            && string.Equals(
                SourceProducerJobId,
                candidate.SourceProducerJobId,
                StringComparison.Ordinal)
            && string.Equals(
                SourceVideoJobId,
                candidate.SourceVideoJobId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(PresetId, candidate.PresetId, StringComparison.Ordinal)
            && string.Equals(AdapterId, candidate.AdapterId, StringComparison.Ordinal)
            && string.Equals(Operation, candidate.Operation, StringComparison.Ordinal)
            && VideoMutationSafe == candidate.VideoMutationSafe
            && QueueReorderSafe == candidate.QueueReorderSafe
            && I2iMutationSafe == candidate.I2iMutationSafe
            && I2iSchemaVersion == candidate.I2iSchemaVersion
            && string.Equals(I2iTarget, candidate.I2iTarget, StringComparison.Ordinal)
            && string.Equals(
                I2iInstructionSummary,
                candidate.I2iInstructionSummary,
                StringComparison.Ordinal)
            && I2iV2EnvelopeClaimed == candidate.I2iV2EnvelopeClaimed
            && Equals(I2iV3Snapshot, candidate.I2iV3Snapshot)
            && Equals(MiniMaxH3VideoSnapshot, candidate.MiniMaxH3VideoSnapshot)
            && VideoToolsEnvelopeClaimed == candidate.VideoToolsEnvelopeClaimed
            && string.Equals(
                VideoToolsKind,
                candidate.VideoToolsKind,
                StringComparison.Ordinal)
            && string.Equals(
                VideoToolsFinishMode,
                candidate.VideoToolsFinishMode,
                StringComparison.Ordinal)
            && Equals(VideoToolsV2Snapshot, candidate.VideoToolsV2Snapshot)
            && VideoTrimEnvelopeClaimed == candidate.VideoTrimEnvelopeClaimed
            && Equals(VideoTrimV1Snapshot, candidate.VideoTrimV1Snapshot)
            && Equals(_videoMutationProbe, candidate._videoMutationProbe)
            && CreatedAt == candidate.CreatedAt
            && SourceSize == candidate.SourceSize
            && SourceMtimeMs == candidate.SourceMtimeMs;

    public void RefreshFrom(EnhancementWorkspaceJobView candidate)
    {
        bool statusChanged = !string.Equals(Status, candidate.Status, StringComparison.Ordinal);
        bool cancelRequestedChanged = CancelRequested != candidate.CancelRequested;
        bool progressChanged = Progress != candidate.Progress;
        bool outputChanged = !string.Equals(OutputPath, candidate.OutputPath, StringComparison.OrdinalIgnoreCase);
        bool errorChanged = !string.Equals(ErrorMessage, candidate.ErrorMessage, StringComparison.Ordinal);
        bool updatedChanged = UpdatedAt != candidate.UpdatedAt;
        bool timingChanged = StartedAt != candidate.StartedAt
            || FinishedAt != candidate.FinishedAt;
        bool queueChanged = QueuePosition != candidate.QueuePosition;
        bool queueCountChanged = QueueCount != candidate.QueueCount;
        bool queueOrderChanged = QueueOrder != candidate.QueueOrder;
        bool queueMutationScopeChanged =
            QueueMutationScopeSafe != candidate.QueueMutationScopeSafe;
        bool outputDependencyChanged =
            OutputDependencyProtected != candidate.OutputDependencyProtected;
        bool requestDetailsChanged = candidate._requestDetailsLoaded
            && (!_requestDetailsLoaded
                || !string.Equals(
                    _requestDetailsText,
                    candidate._requestDetailsText,
                    StringComparison.Ordinal));
        bool requestDetailsInvalidated = _requestDetailsLoaded
            && !candidate._requestDetailsLoaded
            && (statusChanged
                || updatedChanged
                || outputChanged
                || errorChanged);

        Status = candidate.Status;
        CancelRequested = candidate.CancelRequested;
        Progress = candidate.Progress;
        OutputPath = candidate.OutputPath;
        ErrorMessage = candidate.ErrorMessage;
        UpdatedAt = candidate.UpdatedAt;
        StartedAt = candidate.StartedAt;
        FinishedAt = candidate.FinishedAt;
        QueueOrder = candidate.QueueOrder;
        QueuePosition = candidate.QueuePosition;
        QueueCount = candidate.QueueCount;
        QueueMutationScopeSafe = candidate.QueueMutationScopeSafe;
        OutputDependencyProtected = candidate.OutputDependencyProtected;
        if (candidate._requestDetailsLoaded)
        {
            _requestDetailsText = candidate._requestDetailsText;
            _requestDetailsLoaded = true;
        }
        else if (requestDetailsInvalidated)
        {
            _requestDetailsText = "";
            _requestDetailsLoaded = false;
            _requestDetailsExpanded = false;
        }
        IsHighlighted = candidate.IsHighlighted;

        if (requestDetailsChanged || requestDetailsInvalidated)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsLoaded)));
        }
        if (requestDetailsInvalidated)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
                nameof(RequestDetailsButtonLabel)));
        }

        if (progressChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        if (statusChanged
            || cancelRequestedChanged
            || progressChanged
            || queueChanged
            || queueCountChanged
            || queueOrderChanged
            || queueMutationScopeChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReorder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowReorderControls)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CancelLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunWithCurrentSettings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunNextWithCurrentSettings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdatePhotorealPrompts)));
        }
        if (statusChanged || outputChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailToolTip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenOutputToolTip)));
        }
        if (outputDependencyChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteOutput)));
        if (statusChanged
            || cancelRequestedChanged
            || outputChanged
            || queueChanged
            || queueCountChanged
            || queueOrderChanged
            || queueMutationScopeChanged
            || outputDependencyChanged)
        {
            NotifyActionPresentationsChanged();
        }
        if (statusChanged
            || cancelRequestedChanged
            || outputChanged
            || errorChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
        if (statusChanged || updatedChanged || timingChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompletedElapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedText)));
        }
        if (statusChanged
            || cancelRequestedChanged
            || progressChanged
            || queueChanged
            || timingChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public void ApplyHealthProgress(int progress, DateTimeOffset? updatedAt)
    {
        if (Status != "running")
            return;

        int nextProgress = Math.Clamp(progress, 0, 100);
        bool progressChanged = Progress != nextProgress;
        bool updatedChanged = updatedAt.HasValue && UpdatedAt != updatedAt.Value;
        if (!progressChanged && !updatedChanged)
            return;

        Progress = nextProgress;
        if (updatedAt.HasValue)
            UpdatedAt = updatedAt.Value;
        if (progressChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
        }
        if (updatedChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText)));
    }

    public void ApplyQueuePresentation(
        int queuePosition,
        int queueCount,
        int queueOrder)
    {
        bool positionChanged = QueuePosition != queuePosition;
        bool countChanged = QueueCount != queueCount;
        bool orderChanged = QueueOrder != queueOrder;
        QueuePosition = queuePosition;
        QueueCount = queueCount;
        QueueOrder = queueOrder;
        if (!positionChanged && !countChanged && !orderChanged)
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueuePosition)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveUp)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveDown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMoveNext)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveNext)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
        NotifyActionPresentationsChanged();
    }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value)
                return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReorder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMoveNext)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunWithCurrentSettings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRerunNextWithCurrentSettings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdatePhotorealPrompts)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUseOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
            NotifyActionPresentationsChanged();
        }
    }

    private void NotifyActionPresentationsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action1)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action2)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action3)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action4)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action5)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DangerAction)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record EnhancementJobActionPresentation(
    string Kind,
    string Label,
    string ToolTip,
    bool Visible,
    bool Enabled,
    double MinWidth,
    string AutomationName)
{
    public static EnhancementJobActionPresentation Hidden { get; } =
        new("", "", "", false, false, 0, "");
}

public sealed record EnhancementJobLifecycleSmokeSnapshot(
    bool ExactCurrentVideoToolsV2,
    bool ReaderOnly,
    bool SupportedMutation,
    string? Kind,
    string Status,
    bool CanCancel,
    bool CanRetry,
    bool CanDismiss,
    bool CanReorder,
    bool CanUseOutput,
    bool CanDeleteOutput,
    string[] VisibleActionKinds);

public sealed record MiniMaxH3VideoWorkspaceSnapshot(
    string ProfileId,
    int NominalDurationSeconds,
    int MaximumPixelArea,
    int Steps,
    string Prompt);

internal sealed record EnhancementVideoMutationProbe(
    string EnvelopeSha256,
    string SourceSha256,
    string SourceId,
    string SourcePath,
    string? SourceProducerJobId,
    long? SourceSize,
    double? SourceMtimeMs,
    int? EffectiveWidth,
    int? EffectiveHeight,
    bool RequiresCurrentCanvasValidation);

internal readonly record struct EnhancementQueueHealthView(
    string State,
    string Detail,
    string Revision,
    string ForegroundResource,
    bool? Paused,
    bool QueueRecoveryRequired,
    bool QueuedPhotorealPromptUpdate,
    bool PhotorealEnqueueNext,
    bool TerminalHistoryBatchDismiss,
    bool QueuedJobsBatchCancel,
    bool QueuedJobsBatchReorder,
    bool TerminalHistoryTargets,
    bool TerminalHistoryBatchRetry,
    string InventorySignature,
    long? InventoryRevision,
    string? CurrentJobId,
    int? CurrentProgress,
    DateTimeOffset? CurrentUpdatedAt);

public sealed record EnhancementJobsWorkspaceSmokeSnapshot(
    bool Visible,
    int Total,
    int Filtered,
    int FilteredTotal,
    int PageIndex,
    int PageCount,
    int PageSize,
    int Active,
    int Failed,
    int Canceled,
    int Highlighted,
    bool Polling,
    int GetRequests,
    int PollRequests,
    string Status,
    int HealthGetRequests,
    string HealthState,
    string HealthDetail,
    string HealthRevision,
    bool? QueuePaused,
    string QueuePauseLabel,
    bool QueuePauseEnabled,
    bool QueuedPhotorealPromptUpdateSupported,
    bool PhotorealEnqueueNextSupported,
    string[] VisibleIds,
    string[] VisibleStatusLabels,
    string[] VisibleOperationLabels);

internal sealed record EnhancementJobsHistoryWindowSmokeSnapshot(
    int HistoryLimit,
    int LoadedCount,
    int TotalCount,
    int ActiveCount,
    int TerminalCount,
    string? FirstTerminalId,
    string? LastTerminalId);

public sealed record EnhancementJobsPagingSmokeSnapshot(
    int PageSize,
    int PageIndex,
    int PageCount,
    int FirstIndex,
    int ItemCount);

public sealed record EnhancementJobsScrollPerformanceSmokeSnapshot(
    int JobCount,
    int FilteredCount,
    int VisibleCount,
    int PageSize,
    int PageCount,
    double FilterMilliseconds,
    double InitialLayoutMilliseconds,
    double TotalScrollMilliseconds,
    double MaximumScrollStepMilliseconds,
    double P95ScrollStepMilliseconds,
    int ScrollChangedCount,
    int ThumbnailTimerRestartCount,
    int ThumbnailScrollCancellationCount,
    bool ThumbnailDebouncePending,
    bool ActionPresentationContract,
    int RealizedContainerPeak,
    int RealizedButtonPeak,
    int ThumbnailBatchSize,
    double ScrollableHeight);

internal readonly record struct EnhancementJobsPageWindow(
    int PageIndex,
    int PageCount,
    int FirstIndex,
    int ItemCount);

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int EnhancementJobsThumbnailLimit = 48;
    private const int EnhancementJobsThumbnailCacheLimit = 96;
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
    private static readonly JsonSerializerOptions VideoStableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly List<EnhancementWorkspaceJobView> _enhancementWorkspaceJobs = [];
    private readonly Dictionary<string, BitmapSource> _enhancementWorkspaceThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enhancementWorkspaceHighlightedJobIds = new(StringComparer.Ordinal);
    private DispatcherTimer _enhancementWorkspacePollTimer = null!;
    private CancellationTokenSource? _enhancementWorkspaceThumbnailCts;
    private bool _enhancementWorkspaceRefreshPending;
    private bool _enhancementWorkspaceHealthPollPending;
    private long _enhancementWorkspaceRefreshGeneration;
    private long _enhancementWorkspaceQueuePresentationRevision;
    private bool _enhancementWorkspaceMutationPending;
    private long _enhancementWorkspaceGeneration;
    private string _enhancementWorkspaceFilter = "all";
    private DateTimeOffset _enhancementWorkspaceHighlightExpiresAt;
    private IInputElement? _enhancementWorkspaceFocusBeforeDialog;
    private int _enhancementWorkspaceGetCount;
    private int _enhancementWorkspacePollCount;
    private int _enhancementWorkspaceHealthGetCount;
    private bool? _enhancementWorkspaceHealthEndpointSupported;
    private string? _enhancementWorkspaceHealthInventorySignature;
    private bool? _enhancementWorkspaceQueuePaused;
    private bool _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported;
    private bool _enhancementWorkspacePhotorealEnqueueNextSupported;
    private bool _returnToEnhancementJobsAfterModalClose;
    private Tile? _enhancementJobsTemporaryVisibleTile;
    private string? _enhancementJobsTrustedModalSourcePath;
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
            "i2i" when IsI2iMutationSafe(job) || IsI2iV2MutationSafe(job) => "i2i",
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
            || prompt!.Length > 2_000
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
        out double durationSeconds)
    {
        frameCount = 0;
        durationSeconds = 0;
        string? profileId;
        if (HasExactProperties(requested, "prompt"))
        {
            profileId = MiniMaxH3VideoDefaultProfileId;
        }
        else if (HasExactProperties(requested, "profileId", "prompt")
            && TryGetStringProperty(
                requested,
                "profileId",
                out profileId))
        {
            // Parsed below.
        }
        else
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
                out double expectedDurationSeconds)
            || !TryGetStringPropertyAllowEmpty(
                requested,
                "prompt",
                out string? requestedPrompt)
            || requestedPrompt!.Length > 2_000
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
            || !IsValidMiniMaxH3VideoCanvas(width, height)
            || !HasExactInt32(
                effective,
                "frameCount",
                expectedFrameCount)
            || !HasExactInt32(
                effective,
                "playbackFps",
                MiniMaxH3VideoPlaybackFps)
            || !HasExactInt32(effective, "steps", MiniMaxH3VideoSteps)
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

    private bool CanCancelAllQueuedEnhancementJobs()
    {
        EnhancementWorkspaceJobView[] queued = _enhancementWorkspaceJobs
            .Where(static job => job.Status == "queued")
            .ToArray();
        return queued.Length > 0
            && queued.All(static job => job.IsSupportedMutationOperation);
    }

    private bool CanUpdateAllQueuedPhotorealPrompts()
        => _enhancementWorkspaceQueuedPhotorealPromptUpdateSupported
            && _enhancementWorkspaceJobs.Any(static job =>
                job.CanUpdatePhotorealPrompts);

    private bool CanRetryAllFailedEnhancementJobs()
        => _enhancementWorkspaceJobs.Any(static job =>
            job.Status == "failed" && job.CanRetry && job.CanDismiss);

    private bool CanClearAllFailedEnhancementJobs()
        => _enhancementWorkspaceJobs.Any(static job =>
            job.Status == "failed");

    private void RefreshEnhancementQueueBulkControls()
    {
        if (EnhancementJobsClearQueuedButton is not null)
        {
            EnhancementJobsClearQueuedButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanCancelAllQueuedEnhancementJobs();
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
                && CanRetryAllFailedEnhancementJobs();
        }

        if (EnhancementJobsClearFailedButton is not null)
        {
            EnhancementJobsClearFailedButton.IsEnabled =
                !_enhancementWorkspaceMutationPending
                && CanClearAllFailedEnhancementJobs();
        }
    }

    private void InitializeEnhancementJobsWorkspace()
    {
        _enhancementWorkspacePollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _enhancementWorkspacePollTimer.Tick += EnhancementWorkspacePollTimer_Tick;
    }

    private async void OpenEnhancementJobs_Click(object sender, RoutedEventArgs e)
        => await OpenEnhancementJobsWorkspaceAsync("all");

    private async Task OpenEnhancementJobsWorkspaceAsync(
        string initialFilter,
        IReadOnlyCollection<string>? highlightedJobIds = null,
        IInputElement? focusToRestore = null,
        bool restoreReturnViewport = false)
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
            _enhancementWorkspaceFilter = initialFilter is "queued" or "running" or "failed" or "canceled" or "completed" or "video"
                ? initialFilter
                : "all";
            EnhancementJobsAllFilter.IsChecked = _enhancementWorkspaceFilter == "all";
            EnhancementJobsQueuedFilter.IsChecked = _enhancementWorkspaceFilter == "queued";
            EnhancementJobsRunningFilter.IsChecked = _enhancementWorkspaceFilter == "running";
            EnhancementJobsFailedFilter.IsChecked = _enhancementWorkspaceFilter == "failed";
            EnhancementJobsCanceledFilter.IsChecked = _enhancementWorkspaceFilter == "canceled";
            EnhancementJobsCompletedFilter.IsChecked = _enhancementWorkspaceFilter == "completed";
            EnhancementJobsVideoFilter.IsChecked = _enhancementWorkspaceFilter == "video";
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
            EnhancementJobsStatusText.Text = "Loading jobs from the local companion...";
            _enhancementWorkspaceHealthEndpointSupported = null;
            _enhancementWorkspaceHealthInventorySignature = null;
            _enhancementWorkspaceQueuePaused = null;
            RefreshEnhancementQueuePauseControl();
            ApplyEnhancementQueueHealthUnavailable("Checking queue health...");
            EnhancementJobsEmptyText.Visibility = Visibility.Collapsed;
            if (!restoreReturnViewport)
                EnhancementJobsList.ItemsSource = null;
            long generation = ++_enhancementWorkspaceGeneration;
            _ = Dispatcher.BeginInvoke(
                EnhancementJobsRefreshButton.Focus,
                DispatcherPriority.Input);
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
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
                mode: _enhancementWorkspaceFilter,
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
        Interlocked.Exchange(ref _enhancementWorkspaceThumbnailCts, null)?.Cancel();
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
            await RefreshEnhancementJobsWorkspaceAsync(
                _enhancementWorkspaceGeneration,
                isPoll: false);
            operationOutcome = EnhancementJobsDialog.Visibility == Visibility.Visible
                ? "completed"
                : "canceled";
        }
        finally
        {
            AibosOperationLog.Write(
                "jobs_workspace_refresh",
                operationOutcome,
                operationWatch.ElapsedMilliseconds,
                mode: _enhancementWorkspaceFilter,
                itemCount: _enhancementWorkspaceJobs.Count);
        }
    }

    private async void ToggleEnhancementQueuePaused_Click(object sender, RoutedEventArgs e)
    {
        if (_enhancementWorkspaceQueuePaused is bool paused)
            await SetEnhancementQueuePausedAsync(!paused);
    }

    private async Task<bool> SetEnhancementQueuePausedAsync(bool paused)
    {
        if (_enhancementWorkspaceQueuePaused is not bool current
            || _enhancementWorkspaceMutationPending
            || _enhancementWorkspaceRefreshPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return false;
        }
        if (current == paused)
            return true;

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        EnhancementJobsRefreshButton.IsEnabled = false;
        RefreshEnhancementQueuePauseControl();
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/queue",
                new { paused });
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
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || _enhancementWorkspaceMutationPending
            || _enhancementWorkspaceHealthPollPending
            || (_enhancementWorkspaceRefreshPending && _enhancementWorkspaceRefreshGeneration == _enhancementWorkspaceGeneration))
            return;

        _enhancementWorkspacePollCount++;
        await PollEnhancementJobsWorkspaceAsync(_enhancementWorkspaceGeneration);
    }

    private async Task PollEnhancementJobsWorkspaceAsync(long generation)
    {
        if (_enhancementWorkspaceHealthPollPending
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
                && string.Equals(
                    observedSignature,
                    _enhancementWorkspaceHealthInventorySignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            // The compact health payload is enough for idle progress ticks.
            // Fetch the full inventory only when its status counts/current job
            // changed, or when an older companion cannot provide health.
            await RefreshEnhancementJobsWorkspaceAsync(
                generation,
                isPoll: true,
                refreshHealth: false,
                observedHealthInventorySignature: observedSignature);
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
        bool forceHealthPollAfterInventory = false;
        EnhancementJobsRefreshButton.IsEnabled = false;
        RefreshEnhancementQueuePauseControl();
        if (!isPoll)
            EnhancementJobsStatusText.Text = "Refreshing jobs...";
        try
        {
            if (refreshHealth)
            {
                // Bind the full inventory only to a health signature observed
                // before that inventory is requested. Reading health after an
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
            }

            _enhancementWorkspaceGetCount++;
            EnhancementApiResponse response = await SendEnhancementApiAsync(HttpMethod.Get, "api/enhance/jobs");
            if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                return;

            if (!response.Ok || response.Payload is not JsonElement payload)
            {
                EnhancementJobsStatusText.Text = response.Error;
                _enhancementWorkspacePollTimer.Stop();
                return;
            }

        if (!TryParseEnhancementWorkspaceJobs(
                payload,
                out List<EnhancementWorkspaceJobView> jobs,
                out string? error,
                IsVideoMutationSafe))
            {
                EnhancementJobsStatusText.Text = error ?? "The companion returned an invalid jobs response.";
                _enhancementWorkspacePollTimer.Stop();
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
                if (healthAfterInventorySignature is not null
                    && !string.Equals(
                        healthAfterInventorySignature,
                        observedHealthInventorySignature,
                        StringComparison.Ordinal))
                {
                    if (healthInventoryCoalesceAttemptsRemaining > 0)
                    {
                        // The inventory was in flight while queue/runtime state
                        // changed. Do not display it or mark the newer health
                        // signature handled; coalesce exactly one replacement
                        // full read after this single-flight section exits.
                        coalescedHealthInventorySignature =
                            healthAfterInventorySignature;
                    }
                    else
                    {
                        // Continuous churn must not create an unbounded chain
                        // of 24 MiB full reads. Apply this latest inventory
                        // without accepting a mismatched signature and keep one
                        // compact-health poll alive for the next reconciliation.
                        observedHealthInventorySignature = null;
                        forceHealthPollAfterInventory = true;
                    }
                }
            }

            if (coalescedHealthInventorySignature is null)
            {
            bool activeMembershipChanged = !SameActiveEnhancementJobIds(
                _enhancementWorkspaceJobs,
                jobs);
            ApplyEnhancementWorkspaceHighlights(jobs);
            ReconcileEnhancementWorkspaceJobs(jobs);
            if (!isPoll || activeMembershipChanged)
                QueueEnhancedStateRefreshIfChanged();
            bool highlightedBatchAlreadyTerminal = _enhancementWorkspaceFilter is "queued" or "running"
                && jobs.Any(static job => job.IsHighlighted)
                && !jobs.Any(static job => job.IsHighlighted && job.IsActive);
            if (highlightedBatchAlreadyTerminal)
            {
                _enhancementWorkspaceFilter = "all";
                EnhancementJobsAllFilter.IsChecked = true;
                EnhancementJobsQueuedFilter.IsChecked = false;
                EnhancementJobsRunningFilter.IsChecked = false;
            }
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            int activeCount = jobs.Count(static job => job.IsActive);
            int runningCount = jobs.Count(static job => job.Status == "running");
            int queuedCount = jobs.Count(static job => job.Status == "queued");
            int completedCount = jobs.Count(static job => job.Status is "succeeded" or "deleted");
            RefreshEnhancementQueueBulkControls();
            EnhancementJobsHeaderSummary.Text = $"{jobs.Count:N0} total  ·  {activeCount:N0} active  ·  {completedCount:N0} completed";
            EnhancementJobsStatusText.Text = activeCount > 0
                ? $"共有GPUキューを実行順で表示中です。実行中 {runningCount:N0}、待ち {queuedCount:N0}。"
                : $"Updated {DateTime.Now:HH:mm:ss}. Polling is stopped because no jobs are active.";
            if (highlightedBatchAlreadyTerminal)
                EnhancementJobsStatusText.Text += " The new batch already finished, so all highlighted jobs are shown.";
            if (activeCount > 0 || forceHealthPollAfterInventory)
                _enhancementWorkspacePollTimer.Start();
            else
                _enhancementWorkspacePollTimer.Stop();

            if (observedHealthInventorySignature is not null)
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
            await SendEnhancementApiAsync(HttpMethod.Get, "api/enhance/health");
        if (generation != _enhancementWorkspaceGeneration
            || EnhancementJobsDialog.Visibility != Visibility.Visible)
        {
            return null;
        }

        if (response.StatusCode == 404)
        {
            _enhancementWorkspaceHealthEndpointSupported = false;
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

        string? firstIssue = null;
        foreach (JsonElement issueElement in issuesElement.EnumerateArray())
        {
            if (issueElement.ValueKind != JsonValueKind.String)
                return false;
            firstIssue ??= DescribeEnhancementQueueHealthIssue(issueElement.GetString());
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
            $"{queued}|{running}|{succeeded}|{failed}|{canceled}|{deleted}|{currentJobId ?? "-"}|{lastClaimAtSignature}|{lastTerminalAtSignature}|{serverStartedAtSignature}|{processId}|{buildIdSignature}");
        health = new EnhancementQueueHealthView(
            stateLabel,
            detail,
            revision,
            foregroundResource,
            paused,
            queuedPhotorealPromptUpdate,
            photorealPromptControls && atomicImageEnqueueNext,
            inventorySignature,
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

    private void ApplyEnhancementQueueHealth(EnhancementQueueHealthView health)
    {
        _enhancementWorkspaceQueuePaused = health.Paused;
        ApplyQueuedPhotorealPromptUpdateCapability(
            health.QueuedPhotorealPromptUpdate);
        ApplyPhotorealEnqueueNextCapability(health.PhotorealEnqueueNext);
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
        _enhancementWorkspaceQueuePaused = null;
        ApplyQueuedPhotorealPromptUpdateCapability(false);
        ApplyPhotorealEnqueueNextCapability(false);
        EnhancementJobsHealthStateText.Text = "Health unavailable";
        EnhancementJobsHealthStateText.Foreground =
            (Brush)FindResource("TextTertiary");
        EnhancementJobsHealthDetailText.Text = detail;
        EnhancementJobsHealthRevisionText.Text = "";
        RefreshEnhancementQueuePauseControl();
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
        EnhancementJobsPauseResumeButton.Content = paused ? "再開" : "一時停止";
        EnhancementJobsPauseResumeButton.IsEnabled =
            _enhancementWorkspaceQueuePaused.HasValue
            && !_enhancementWorkspaceMutationPending
            && !_enhancementWorkspaceRefreshPending;
        AutomationProperties.SetName(
            EnhancementJobsPauseResumeButton,
            paused ? "Resume enhancement queue" : "Pause enhancement queue");
        EnhancementJobsPauseResumeButton.ToolTip = _enhancementWorkspaceQueuePaused.HasValue
            ? paused
                ? "待機順を保ったままキュー処理を再開します"
                : "処理中の1件は完了させ、次の待機ジョブから止めます"
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
        Dictionary<string, EnhancementWorkspaceJobView> existingById =
            _enhancementWorkspaceJobs.ToDictionary(static job => job.Id, StringComparer.Ordinal);
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

    private static bool TryParseEnhancementWorkspaceJobs(
        JsonElement payload,
        out List<EnhancementWorkspaceJobView> jobs,
        out string? error,
        Func<JsonElement, bool>? videoMutationValidator = null)
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
        foreach (JsonElement element in jobsElement.EnumerateArray())
        {
            EnhancementWorkspaceJobView? job = ParseEnhancementWorkspaceJob(
                element,
                apiOrdinal++,
                videoMutationValidator);
            if (job is not null)
                jobs.Add(job);
        }

        HashSet<string> protectedPhotorealJobIds = jobs
            .Where(static job => job.Operation == "i2i"
                && job.I2iMutationSafe
                && job.IsActive
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId))
            .Select(static job => job.SourceProducerJobId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (EnhancementWorkspaceJobView job in jobs)
        {
            job.OutputDependencyProtected = job.Operation == "photoreal"
                && protectedPhotorealJobIds.Contains(job.Id);
        }

        AssignEnhancementWorkspaceQueuePositions(jobs);
        jobs.Sort(CompareEnhancementWorkspaceInventory);
        return true;
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
            queued.All(static candidate => candidate.IsSupportedMutationOperation);
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
        Func<JsonElement, bool>? videoMutationValidator = null)
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
        bool i2iMutationSafe = operation == "i2i"
            && (IsI2iMutationSafe(element) || i2iV2Info is not null);
        return new EnhancementWorkspaceJobView(
            id!,
            sourceId ?? "",
            sourcePath ?? "",
            sourceProducerJobId,
            presetId ?? "Default preset",
            adapterId ?? "local companion",
            operation,
            operation == "video"
                && (videoMutationValidator?.Invoke(element)
                    ?? IsStructurallyVideoMutationSafe(element)),
            i2iMutationSafe,
            i2iV2Info?.SchemaVersion ?? (i2iMutationSafe ? 1 : null),
            i2iV2Info?.Target ?? (i2iMutationSafe ? "hair-color" : null),
            i2iV2Info?.InstructionSummary,
            i2iV2EnvelopeClaimed,
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
            apiOrdinal);
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

    private void EnhancementJobsFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter })
        {
            _enhancementWorkspaceFilter = filter;
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
        }
    }

    private void ApplyEnhancementWorkspaceFilter(bool loadThumbnails)
    {
        EnhancementWorkspaceJobView[] filtered = _enhancementWorkspaceJobs
            .Where(job => _enhancementWorkspaceFilter switch
            {
                "active" => job.IsActive,
                "queued" => job.Status == "queued",
                "running" => job.Status == "running",
                "failed" => job.Status == "failed",
                "canceled" => job.Status == "canceled",
                "completed" => job.Status is "succeeded" or "deleted",
                "video" => job.IsVideoOperation,
                _ => true,
            })
            .ToArray();
        EnhancementWorkspaceJobView[] current =
            (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray()
            ?? [];
        bool sameItems = current.Length == filtered.Length
            && current.Zip(filtered, static (left, right) => ReferenceEquals(left, right)).All(static same => same);
        if (!sameItems)
            EnhancementJobsList.ItemsSource = filtered;
        EnhancementJobsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (loadThumbnails)
            BeginEnhancementWorkspaceThumbnailLoad(filtered);
    }

    private void BeginEnhancementWorkspaceThumbnailLoad(IReadOnlyList<EnhancementWorkspaceJobView> jobs)
    {
        EnhancementWorkspaceJobView[] missing = jobs
            .Where(static job => job.Thumbnail is null)
            .Take(EnhancementJobsThumbnailLimit)
            .ToArray();
        if (EnhancementJobsDialog.Visibility != Visibility.Visible || missing.Length == 0)
            return;

        Interlocked.Exchange(ref _enhancementWorkspaceThumbnailCts, null)?.Cancel();
        var cts = new CancellationTokenSource();
        _enhancementWorkspaceThumbnailCts = cts;
        long generation = _enhancementWorkspaceGeneration;
        _ = LoadEnhancementWorkspaceThumbnailsAsync(missing, generation, cts);
    }

    private async Task LoadEnhancementWorkspaceThumbnailsAsync(
        IReadOnlyList<EnhancementWorkspaceJobView> jobs,
        long generation,
        CancellationTokenSource cts)
    {
        try
        {
            foreach (EnhancementWorkspaceJobView job in jobs)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!TryResolveEnhancementWorkspaceInput(
                        job,
                        out string canonicalSource))
                {
                    continue;
                }

                string cacheKey = $"{canonicalSource}|{job.SourceSize?.ToString(CultureInfo.InvariantCulture)}|{job.SourceMtimeMs?.ToString(CultureInfo.InvariantCulture)}";
                if (!_enhancementWorkspaceThumbnailCache.TryGetValue(cacheKey, out BitmapSource? thumbnail))
                {
                    thumbnail = await Task.Run(() => DecodeEnhancementWorkspaceThumbnail(canonicalSource), cts.Token);
                    if (thumbnail is null)
                        continue;
                    if (_enhancementWorkspaceThumbnailCache.Count >= EnhancementJobsThumbnailCacheLimit)
                        _enhancementWorkspaceThumbnailCache.Remove(_enhancementWorkspaceThumbnailCache.Keys.First());
                    _enhancementWorkspaceThumbnailCache[cacheKey] = thumbnail;
                }

                if (generation != _enhancementWorkspaceGeneration || EnhancementJobsDialog.Visibility != Visibility.Visible)
                    return;
                job.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_enhancementWorkspaceThumbnailCts, cts))
            {
                _enhancementWorkspaceThumbnailCts = null;
                cts.Dispose();
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
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalCatalogSource)
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
            bool usesPhotorealInput = job.IsVideoOperation
                && !string.IsNullOrWhiteSpace(job.SourceProducerJobId);
            if (usesPhotorealInput)
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
            else if (!EnhancementSourceIdentityComparer.Equals(
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

    private async void RetryEnhancementJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job } && job.CanRetry)
        {
            await RunEnhancementWorkspaceMutationAsync(
                job,
                HttpMethod.Post,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/retry",
                job.Status == "failed"
                    ? "Retry queued. The original failure was removed."
                    : "Retry queued as a new job.",
                removeFailedOriginalAfterSuccess: job.Status == "failed",
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
        if (_enhancementWorkspaceMutationPending
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
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/queue",
                new { move });
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

        Dictionary<string, EnhancementWorkspaceJobView> queuedById = queued
            .ToDictionary(static candidate => candidate.Id, StringComparer.Ordinal);
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

                EnhancementApiResponse response = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/prompts",
                    CreateQueuedPhotorealSettingsUpdateBody(settings, seed));
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
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || !CanCancelAllQueuedEnhancementJobs())
        {
            return;
        }

        _enhancementWorkspaceMutationPending = true;
        RefreshEnhancementQueueBulkControls();
        long generation = _enhancementWorkspaceGeneration;
        try
        {
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Delete,
                "api/enhance/jobs/queued");
            if (generation != _enhancementWorkspaceGeneration
                || EnhancementJobsDialog.Visibility != Visibility.Visible)
            {
                return;
            }
            if (!response.Ok)
            {
                EnhancementJobsStatusText.Text = response.Error;
                return;
            }

            EnhancementJobsStatusText.Text = "待機中のジョブをすべてキャンセルしました。実行中のジョブは変更していません。";
            await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: false);
        }
        finally
        {
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private async void RetryAllFailedEnhancementJobs_Click(
        object sender,
        RoutedEventArgs e)
        => await RetryAllFailedEnhancementJobsAsync();

    private async Task<int> RetryAllFailedEnhancementJobsAsync()
    {
        EnhancementWorkspaceJobView[] failed = _enhancementWorkspaceJobs
            .Where(static job =>
                job.Status == "failed" && job.CanRetry && job.CanDismiss)
            .OrderBy(static job => job.CreatedAt)
            .ThenBy(static job => job.ApiOrdinal)
            .ToArray();
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || failed.Length == 0)
        {
            return 0;
        }

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        RefreshEnhancementQueueBulkControls();
        long generation = _enhancementWorkspaceGeneration;
        int retriedCount = 0;
        int pendingCount = 0;
        int failedCount = 0;
        string? failure = null;
        int? failureStatus = null;
        EnhancementJobsStatusText.Text =
            $"失敗 {failed.Length:N0}件の再試行を予約しています…";
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            DurableEnhancementBatchResponse batch =
                await TrySendDurableEnhancementRetryBatchAsync(failed);
            for (int index = 0; index < failed.Length; index++)
            {
                EnhancementWorkspaceJobView job = failed[index];
                EnhancementApiResponse retry = batch.Responses[index];
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

                EnhancementApiResponse remove = await SendEnhancementApiAsync(
                    HttpMethod.Delete,
                    $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}");
                if (!remove.Ok)
                {
                    failedCount++;
                    failure ??= EnhancementApiErrorCode(remove);
                    failureStatus ??= remove.StatusCode;
                    continue;
                }
                retriedCount++;
                if ((index + 1) % 25 == 0 && index + 1 < failed.Length)
                {
                    EnhancementJobsStatusText.Text =
                        $"再試行を確認中… {index + 1:N0}/{failed.Length:N0}件";
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
                EnhancementJobsStatusText.Text = pendingCount == 0
                    && failedCount == 0
                        ? $"失敗 {retriedCount:N0}件を元設定で再試行し、元の失敗履歴を消しました。"
                        : $"再試行を{acceptedCount:N0}件受付。確認済み{retriedCount:N0}件の元失敗を消しました。"
                            + (pendingCount > 0
                                ? $" {pendingCount:N0}件は登録確認中なので元失敗を残しています。"
                                : "")
                            + (failedCount > 0
                                ? $" {failedCount:N0}件は失敗しました（{failure}）。"
                                : "");
            }
            string? operationError = failure
                ?? (pendingCount > 0 ? "RETRY_SAVED_FOR_DELIVERY" : null);
            AibosOperationLog.Write(
                "failed_jobs_retry_all",
                operationError is null ? "completed" : "partial",
                operationWatch.ElapsedMilliseconds,
                failureStatus ?? (pendingCount > 0 ? 202 : null),
                operationError,
                itemCount: retriedCount + pendingCount);
            return retriedCount;
        }
        finally
        {
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
    {
        string[] failedIds = _enhancementWorkspaceJobs
            .Where(static job => job.Status == "failed")
            .OrderBy(static job => job.CreatedAt)
            .ThenBy(static job => job.ApiOrdinal)
            .Select(static job => job.Id)
            .ToArray();
        if (_enhancementWorkspaceMutationPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || failedIds.Length == 0)
        {
            return 0;
        }

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
        RefreshEnhancementQueueBulkControls();
        long generation = _enhancementWorkspaceGeneration;
        int clearedCount = 0;
        string? failure = null;
        int? failureStatus = null;
        try
        {
            foreach (string id in failedIds)
            {
                EnhancementApiResponse remove = await SendEnhancementApiAsync(
                    HttpMethod.Delete,
                    $"api/enhance/jobs/{Uri.EscapeDataString(id)}");
                if (!remove.Ok)
                {
                    failure = EnhancementApiErrorCode(remove);
                    failureStatus = remove.StatusCode;
                    break;
                }
                clearedCount++;
            }

            if (generation == _enhancementWorkspaceGeneration
                && EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                await RefreshEnhancementJobsWorkspaceAsync(
                    generation,
                    isPoll: false);
                EnhancementJobsStatusText.Text = failure is null
                    ? $"失敗履歴 {clearedCount:N0}件を消しました。元画像と出力ファイルは変更していません。"
                    : $"{clearedCount:N0}件を消しました。残りは停止しました（{failure}）。";
            }
            AibosOperationLog.Write(
                "failed_jobs_clear_all",
                failure is null ? "completed" : "partial",
                operationWatch.ElapsedMilliseconds,
                failureStatus,
                failure,
                itemCount: clearedCount);
            return clearedCount;
        }
        finally
        {
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
            RefreshEnhancementQueuePauseControl();
        }
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
            requireExactHealthValidation: retryHealthValidator is not null);
    }

    private static string EnhancementApiErrorCode(
        EnhancementApiResponse response)
        => response.Payload is JsonElement payload
            && TryGetStringProperty(payload, "code", out string? code)
            && !string.IsNullOrWhiteSpace(code)
                ? code!
                : response.StatusCode == 0
                    ? "COMPANION_UNAVAILABLE"
                    : "API_ERROR";

    private async Task<bool> RunEnhancementWorkspaceMutationAsync(
        EnhancementWorkspaceJobView job,
        HttpMethod method,
        string route,
        string successMessage,
        object? body = null,
        bool removeFailedOriginalAfterSuccess = false,
        string operationLogName = "job_mutation")
    {
        if (_enhancementWorkspaceMutationPending || job.IsBusy || EnhancementJobsDialog.Visibility != Visibility.Visible)
            return false;

        var operationWatch = Stopwatch.StartNew();
        _enhancementWorkspaceMutationPending = true;
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
            EnhancementApiResponse response = isRetryEnqueue
                ? await SendEnhancementWorkspaceRetryAsync(job)
                : await SendEnhancementApiAsync(method, route, body);
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
                    removeFailedOriginalAfterSuccess
                        ? "再試行の予約を保存しました。登録確認前なので元の失敗履歴は残しています。"
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

            if (removeFailedOriginalAfterSuccess)
            {
                EnhancementApiResponse removeResponse =
                    await SendEnhancementApiAsync(
                        HttpMethod.Delete,
                        $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}");
                if (!removeResponse.Ok)
                {
                    EnhancementJobsStatusText.Text =
                        $"Retry was queued, but the original failure could not be removed. {removeResponse.Error}";
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
            job.IsBusy = false;
            _enhancementWorkspaceMutationPending = false;
            RefreshEnhancementQueueBulkControls();
        }
    }

    private Func<JsonElement, string?>? CreateEnhancementRetryHealthValidator(
        EnhancementWorkspaceJobView job)
        => CreateEnhancementRetryHealthValidator(
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

    private void OpenEnhancementOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            TryOpenEnhancementWorkspaceOutput(job);
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

    private void OpenEnhancementSourceInViewer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnhancementWorkspaceJobView job })
            TryOpenEnhancementSourceInViewer(job);
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
        ManagedVideoVersion? preferredVideo = null)
    {
        if (!TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource)
            || !File.Exists(canonicalSource))
        {
            EnhancementJobsStatusText.Text = "元画像が見つからないため、ビューワーで開けません。";
            return false;
        }

        Tile? tile = _allTiles.FirstOrDefault(candidate =>
            candidate.IsRealFile
            && string.Equals(candidate.Path, canonicalSource, StringComparison.OrdinalIgnoreCase));
        if (tile is null)
        {
            var sourceInfo = new FileInfo(canonicalSource);
            tile = new Tile
            {
                Path = canonicalSource,
                FileName = Path.GetFileName(canonicalSource),
                IsRealFile = true,
                ModifiedUtc = sourceInfo.LastWriteTimeUtc,
                Fav = FavoriteLevelForPath(canonicalSource),
            };
        }

        CaptureEnhancementJobsReturnViewport(job.Id);
        PrepareEnhancementJobsModalTile(tile, canonicalSource);
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

    private void PrepareEnhancementJobsModalTile(Tile tile, string canonicalSource)
    {
        RestoreEnhancementJobsModalSelection();
        _enhancementJobsPreviousSelectionPaths.AddRange(_selectedPaths);
        _enhancementJobsPreviousPrimaryPath = _primarySelectedPath;
        _enhancementJobsModalSelectionCaptured = true;
        _enhancementJobsTrustedModalSourcePath = canonicalSource;
        if (!_tiles.Contains(tile))
        {
            _tiles.Add(tile);
            _enhancementJobsTemporaryVisibleTile = tile;
        }
    }

    private bool IsEnhancementJobsTrustedModalSource(Tile tile)
        => !string.IsNullOrWhiteSpace(_enhancementJobsTrustedModalSourcePath)
            && string.Equals(
                tile.Path,
                _enhancementJobsTrustedModalSourcePath,
                StringComparison.OrdinalIgnoreCase);

    private bool TryResolveEnhancementJobsTrustedModalSource(
        Tile tile,
        out string canonicalSource,
        out string reason)
    {
        canonicalSource = "";
        reason = "the Jobs source is unavailable";
        if (!IsEnhancementJobsTrustedModalSource(tile)
            || string.IsNullOrWhiteSpace(tile.Path)
            || !Path.IsPathFullyQualified(tile.Path)
            || !SupportedImageExtensions.Contains(Path.GetExtension(tile.Path)))
        {
            return false;
        }

        try
        {
            canonicalSource = _resolveFinalPath(Path.GetFullPath(tile.Path));
            if (!File.Exists(canonicalSource))
                return false;
            reason = "";
            return true;
        }
        catch
        {
            canonicalSource = "";
            return false;
        }
    }

    private void RestoreEnhancementJobsModalSelection()
    {
        if (!_enhancementJobsModalSelectionCaptured)
            return;

        _enhancementJobsModalSelectionCaptured = false;
        Tile? temporaryTile = _enhancementJobsTemporaryVisibleTile;
        _enhancementJobsTemporaryVisibleTile = null;
        _enhancementJobsTrustedModalSourcePath = null;
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
        string filter = _enhancementWorkspaceFilter;
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
                            filter,
                            focusToRestore: OpenEnhancementJobsButton,
                            restoreReturnViewport: true);
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
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Delete,
                $"api/enhance/jobs/{Uri.EscapeDataString(job.Id)}/output");
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
        if (!job.IsVideoOperation
            || job.Status != "succeeded"
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || job.SourceSize is null
            || job.SourceMtimeMs is null
            || !TryResolveEnhancementWorkspaceCatalogSource(
                job,
                out string canonicalSource)
            || !ReloadEnhancedOutputsForVisibleCatalog())
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

    public List<string> EnhancementWorkspaceCatalogPathsForSmoke
        => _allTiles.Where(static tile => tile.IsRealFile).Select(static tile => tile.Path).ToList();

    public void CloseEnhancementJobsForSmoke() => CloseEnhancementJobsWorkspace(restoreFocus: false);

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
        _enhancementWorkspaceFilter = filter;
        ApplyEnhancementWorkspaceFilter(loadThumbnails: false);
    }

    public EnhancementJobsWorkspaceSmokeSnapshot EnhancementJobsWorkspaceForSmoke()
    {
        var visible = (EnhancementJobsList.ItemsSource as IEnumerable<EnhancementWorkspaceJobView>)?.ToArray() ?? [];
        return new EnhancementJobsWorkspaceSmokeSnapshot(
            EnhancementJobsDialog.Visibility == Visibility.Visible,
            _enhancementWorkspaceJobs.Count,
            visible.Length,
            _enhancementWorkspaceJobs.Count(static job => job.IsActive),
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
            job.Status == "failed"
                ? "Retry queued. The original failure was removed."
                : "Retry queued as a new job.",
            removeFailedOriginalAfterSuccess: job.Status == "failed",
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

    public bool RetryAllFailedEnhancementJobsControlForSmoke =>
        EnhancementJobsRetryFailedButton.IsEnabled;

    public bool ClearAllFailedEnhancementJobsControlForSmoke =>
        EnhancementJobsClearFailedButton.IsEnabled;

    public async Task<(bool Ok, bool SavedForDelivery, int StatusCode, string Error)>
        RetryMiniMaxH3JobForSmokeAsync(string id)
    {
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

    public bool OpenEnhancementJobSourceInViewerForSmoke(string id)
    {
        EnhancementWorkspaceJobView? job = _enhancementWorkspaceJobs.FirstOrDefault(job => job.Id == id);
        return job is not null && TryOpenEnhancementSourceInViewer(job);
    }

    public bool EnhancementJobsHeaderChromeContractForSmoke
        => WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsCloseButton)
            && WindowChrome.GetIsHitTestVisibleInChrome(EnhancementJobsRefreshButton)
            && string.Equals(
                AutomationProperties.GetName(EnhancementJobsVideoFilter),
                "Show video generation jobs",
                StringComparison.Ordinal);

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

    public static bool IsMiniMaxH3VideoMutationSafeForSmoke(JsonElement job)
        => IsMiniMaxH3VideoMutationSafe(job);

    public static bool IsExactMiniMaxH3VideoSnapshotForSmoke(JsonElement video)
        => IsExactMiniMaxH3VideoSnapshot(video);

    public static string ComputeMiniMaxH3VideoSnapshotHashForSmoke(
        JsonElement video)
        => HashStableJson(video);

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
}

public sealed class EnhancementWorkspaceJobView : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isBusy;
    private bool _isHighlighted;
    private bool _queuedPhotorealPromptUpdateCapabilitySafe;
    private bool _photorealEnqueueNextCapabilitySafe;

    public EnhancementWorkspaceJobView(
        string id,
        string sourceId,
        string sourcePath,
        string? sourceProducerJobId,
        string presetId,
        string adapterId,
        string operation,
        bool videoMutationSafe,
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
        int apiOrdinal)
    {
        Id = id;
        SourceId = sourceId;
        SourcePath = sourcePath;
        SourceProducerJobId = sourceProducerJobId;
        PresetId = presetId;
        AdapterId = adapterId;
        Operation = operation;
        VideoMutationSafe = videoMutationSafe;
        I2iMutationSafe = i2iMutationSafe;
        I2iSchemaVersion = i2iSchemaVersion;
        I2iTarget = i2iTarget;
        I2iInstructionSummary = i2iInstructionSummary;
        I2iV2EnvelopeClaimed = i2iV2EnvelopeClaimed;
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
    }

    public string Id { get; }
    public string SourceId { get; }
    public string SourcePath { get; }
    public string? SourceProducerJobId { get; }
    public string PresetId { get; }
    public string AdapterId { get; }
    public string Operation { get; }
    public bool VideoMutationSafe { get; }
    public bool I2iMutationSafe { get; }
    public int? I2iSchemaVersion { get; }
    public string? I2iTarget { get; }
    public string? I2iInstructionSummary { get; }
    public bool I2iV2EnvelopeClaimed { get; }
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
    public bool IsSupportedMutationOperation =>
        Operation is "upscale" or "photoreal"
        || (Operation == "i2i" && I2iMutationSafe)
        || (IsVideoOperation && VideoMutationSafe);
    public bool OutputDependencyProtected { get; set; }
    public bool QueueMutationScopeSafe { get; set; } = true;
    public bool CanCancel =>
        !_isBusy
        && !CancelRequested
        && IsSupportedMutationOperation
        && Status is "queued" or "running" or "failed";
    public bool CanRetry =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status is "failed" or "canceled";
    public bool CanDismiss =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status is "failed" or "canceled" or "deleted";
    public bool ShowReorderControls =>
        IsSupportedMutationOperation
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
        }
    }
    public bool CanUseOutput =>
        !_isBusy
        && IsSupportedMutationOperation
        && Status == "succeeded"
        && !string.IsNullOrWhiteSpace(OutputPath);
    public bool CanDeleteOutput => CanUseOutput && !OutputDependencyProtected;
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
        "running" when Operation == "video" => "動画化を中止",
        "running" when !IsImageOperation => "未対応操作",
        "running" => "高画質化を中止",
        _ => "キャンセル済みにする",
    };
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath) ? "Unknown source" : Path.GetFileName(SourcePath);
    public string SourceVersionLabel => (IsVideoOperation || Operation == "i2i")
        && !string.IsNullOrWhiteSpace(SourceProducerJobId)
            ? "実写版"
            : "Original";
    public string PresetSummary => IsVideoOperation
        ? $"{(PresetId switch
        {
            "minimax-h3-i2v-preview-v1" => "MiniMax H3 Preview · 24 fps · 音声あり",
            "wan22-ti2v-5b-normal-v1" => "Wan2.2 TI2V 5B · 標準 · 20 step",
            "wan22-ti2v-5b-high-v1" => "Wan2.2 TI2V 5B · 高品質 · 40 step",
            _ => PresetId,
        })}  ·  {SourceVersionLabel}"
        : Operation == "i2i" && I2iMutationSafe
            ? $"Schema v{I2iSchemaVersion ?? 0}  ·  Target: {I2iTargetDisplayLabel}  ·  {SourceVersionLabel}"
        : $"{PresetId}  ·  {AdapterId}";
    public string OperationLabel => Operation switch
    {
        "upscale" => "HQ  高画質化",
        "photoreal" => "REAL  実写化",
        "i2i" => "EDIT  AI編集",
        "video" => "VIDEO  動画化",
        _ => "UNSUPPORTED  未対応",
    };
    public string I2iTargetDisplayLabel => I2iTarget switch
    {
        "hair-color" => "髪色",
        "outfit" => "服装",
        "expression" => "表情",
        "background" => "場所・背景",
        "pose" => "ポーズ（実験的）",
        _ => "未対応",
    };
    private bool IsI2iV2Envelope =>
        I2iV2EnvelopeClaimed
        || string.Equals(PresetId, "flux2-i2i-edit-v2", StringComparison.Ordinal)
        || string.Equals(
            AdapterId,
            "comfyui-flux2-i2i-v2",
            StringComparison.Ordinal);
    private string SafeI2iV2DetailText => !I2iMutationSafe
        ? "This AI edit row is incomplete or incompatible and remains protected from mutations."
        : !string.IsNullOrWhiteSpace(I2iInstructionSummary)
            ? $"{I2iTargetDisplayLabel}: {I2iInstructionSummary}"
            : $"{I2iTargetDisplayLabel}: verified public instruction is unavailable.";
    public string StatusLabel => CancelRequested && Status == "running"
        ? $"中止処理中  ·  Running {Progress}%"
        : Status switch
        {
            "queued" => $"待ち順 {QueuePosition ?? 0}  ·  Queued {Progress}%",
            "running" => $"実行中  ·  Running {Progress}%",
            "succeeded" => "Completed",
            "failed" => "Failed",
            "canceled" => "Canceled",
            "deleted" => "Output deleted",
            _ => Status,
        };
    public string DetailText => IsI2iV2Envelope
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
            && string.Equals(PresetId, candidate.PresetId, StringComparison.Ordinal)
            && string.Equals(AdapterId, candidate.AdapterId, StringComparison.Ordinal)
            && string.Equals(Operation, candidate.Operation, StringComparison.Ordinal)
            && VideoMutationSafe == candidate.VideoMutationSafe
            && I2iMutationSafe == candidate.I2iMutationSafe
            && I2iSchemaVersion == candidate.I2iSchemaVersion
            && string.Equals(I2iTarget, candidate.I2iTarget, StringComparison.Ordinal)
            && string.Equals(
                I2iInstructionSummary,
                candidate.I2iInstructionSummary,
                StringComparison.Ordinal)
            && I2iV2EnvelopeClaimed == candidate.I2iV2EnvelopeClaimed
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
        IsHighlighted = candidate.IsHighlighted;

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal readonly record struct EnhancementQueueHealthView(
    string State,
    string Detail,
    string Revision,
    string ForegroundResource,
    bool? Paused,
    bool QueuedPhotorealPromptUpdate,
    bool PhotorealEnqueueNext,
    string InventorySignature,
    string? CurrentJobId,
    int? CurrentProgress,
    DateTimeOffset? CurrentUpdatedAt);

public sealed record EnhancementJobsWorkspaceSmokeSnapshot(
    bool Visible,
    int Total,
    int Filtered,
    int Active,
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

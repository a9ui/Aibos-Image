using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal sealed record VideoToolsV2ReaderSnapshot(
    string Kind,
    string SourceKind,
    string? SourceVideoJobId,
    string SourceCanonicalPath,
    string? SourceStagingCanonicalPath,
    long SourceSize,
    double SourceMtimeMs,
    string PresetId,
    string BackendId,
    int SourceWidth,
    int SourceHeight,
    int SourceFrameCount,
    int SourceFpsNumerator,
    int SourceFpsDenominator,
    int SourceDurationMs,
    int SourceAudioStreamCount,
    int OutputWidth,
    int OutputHeight,
    int OutputFrameCount,
    int OutputFpsNumerator,
    int OutputFpsDenominator,
    double OutputDurationMs,
    int? SelectionStartFrame,
    int? SelectionEndFrameExclusive,
    string? InstructionJa,
    string? CompiledSummaryJa,
    string? AudioPolicy,
    int? Steps,
    int? Strength,
    int? MaximumPixelArea,
    string? FinishMode,
    int? FinishScale);

public partial class MainWindow
{
    private const string VideoToolsV2Protocol =
        "aibos-enhancement-video-tools-v2";
    private const int VideoToolsV2SchemaVersion = 2;
    private const long VideoToolsV2MaximumSafeInteger =
        9_007_199_254_740_991;
    private const long VideoToolsV2MaximumSourceBytes = 536_870_912;
    private const long VideoToolsV2MaximumOutputBytes = 536_870_912;
    private const int VideoToolsV2MaximumSourceDurationMs = 300_000;
    private const int VideoToolsV2MaximumSourceFrames = 18_000;
    private const int VideoToolsV2MaximumSourceWidth = 1_920;
    private const int VideoToolsV2MaximumSourceHeight = 1_080;
    private const int VideoToolsV2MaximumSourcePixelArea = 2_073_600;
    private const int VideoToolsV2MaximumOutputWidth = 3_840;
    private const int VideoToolsV2MaximumOutputHeight = 2_160;
    private const long VideoToolsV2MaximumOutputPixelArea = 8_294_400;

    private static readonly int[] VideoToolsV2AllowedFps = [24, 30, 60];
    private static readonly int[] VideoToolsV2EditPixelAreas =
        [230_400, 307_200, 414_720];
    private static readonly HashSet<string> VideoToolsV2EditBackends = new(
        [
            "wan-vace-1.3b-edit-candidate-v1",
            "bernini-r-1.3b-edit-candidate-v1",
            "minimax-h3-masked-edit-research-v1",
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> VideoToolsV2FinishBackends = new(
        [
            "nvidia-vfx-vsr-1.2-candidate-v1",
            "seedvr2-3b-detail-candidate-v1",
            "nanovsr-1.7m-4x-candidate-v1",
        ],
        StringComparer.Ordinal);

    private readonly record struct VideoToolsV2SourceSnapshot(
        string Kind,
        string? ProducerJobId,
        string CanonicalPath,
        string? StagingCanonicalPath,
        long Size,
        double MtimeMs,
        int Width,
        int Height,
        int FrameCount,
        int FpsNumerator,
        int FpsDenominator,
        int DurationMs,
        int AudioStreamCount,
        long AudioPacketCount,
        string VideoPtsSha256,
        int BitDepth,
        string DynamicRange,
        long VideoTimeBaseNumerator,
        long VideoTimeBaseDenominator,
        long VideoStartTimestamp,
        string ExecutionCanonicalPath,
        long ExecutionSize,
        double ExecutionMtimeMs,
        string ExecutionSha256);

    private readonly record struct VideoToolsV2EditRequestSnapshot(
        int StartFrame,
        int EndFrameExclusive,
        string InstructionJa,
        string SummaryJa,
        string AudioPolicy,
        int Steps,
        int Strength,
        int MaximumPixelArea);

    private readonly record struct VideoToolsV2EditPlanSnapshot(
        double DurationMs,
        int OutputWidth,
        int OutputHeight,
        string AudioPlanKind,
        long? PacketStartIndex,
        long? PacketEndIndexExclusive);

    private static bool ClaimsVideoToolsV2WorkspaceSnapshot(JsonElement job)
    {
        if (!job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return video.TryGetProperty("protocol", out JsonElement protocol)
            && protocol.ValueKind == JsonValueKind.String
            && string.Equals(
                protocol.GetString(),
                VideoToolsV2Protocol,
                StringComparison.Ordinal);
    }

    private static bool TryReadVideoToolsV2WorkspaceSnapshot(
        JsonElement job,
        out VideoToolsV2ReaderSnapshot snapshot)
    {
        snapshot = null!;
        if (!ClaimsVideoToolsV2WorkspaceSnapshot(job)
            || !HasSingleProperty(job, "operation")
            || !VideoToolsExactString(job, "operation", "video")
            || !HasSingleProperty(job, "mediaKind")
            || !VideoToolsExactString(job, "mediaKind", "video")
            || !TryGetVideoToolsV2BoundedText(
                job,
                "sourceId",
                32_768,
                asciiOnly: false,
                out _)
            || !TryGetVideoToolsV2TechnicalText(
                job,
                "presetId",
                128,
                out string jobPresetId)
            || !TryGetVideoToolsV2TechnicalText(
                job,
                "adapterId",
                256,
                out string jobBackendId)
            || !TryGetSingleVideoToolsString(
                job,
                "presetHash",
                out string presetHash)
            || !IsLowerHex(presetHash, 12)
            || VideoToolsV2HasAnyProperty(job, "sourceProducerJobId")
            || VideoToolsV2HasAnyProperty(job, "sourceManagedOutputPath")
            || VideoToolsV2HasAnyProperty(job, "sourceRecoveredOutputPath")
            || VideoToolsV2HasAnyProperty(job, "sourceRecoveredAdapterId")
            || VideoToolsV2HasAnyProperty(job, "sourceRecoveredSignature")
            || VideoToolsV2HasAnyProperty(job, "sourceRecoveredSha256")
            || VideoToolsV2HasAnyProperty(job, "videoTrim")
            || !job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(job, "video")
            || !VideoToolsV2ExactInt32(
                video,
                "schemaVersion",
                VideoToolsV2SchemaVersion)
            || !VideoToolsExactString(
                video,
                "protocol",
                VideoToolsV2Protocol)
            || !TryGetSingleVideoToolsString(
                video,
                "kind",
                out string kind)
            || kind is not ("edit" or "finish")
            || !TryGetVideoToolsV2TechnicalText(
                video,
                "presetId",
                128,
                out string videoPresetId)
            || !TryGetVideoToolsV2TechnicalText(
                video,
                "backendId",
                256,
                out string videoBackendId)
            || !string.Equals(
                jobPresetId,
                videoPresetId,
                StringComparison.Ordinal)
            || !string.Equals(
                jobBackendId,
                videoBackendId,
                StringComparison.Ordinal)
            || !string.Equals(
                presetHash,
                HashStableJson(video)[..12],
                StringComparison.Ordinal)
            || !video.TryGetProperty("source", out JsonElement sourceElement)
            || !TryReadExactVideoToolsV2Source(
                sourceElement,
                out VideoToolsV2SourceSnapshot source)
            || !VideoToolsV2JobSourceMatches(job, source))
        {
            return false;
        }

        bool parsed = kind == "edit"
            ? TryReadExactVideoToolsV2Edit(
                video,
                source,
                videoPresetId,
                videoBackendId,
                out snapshot)
            : TryReadExactVideoToolsV2Finish(
                video,
                source,
                videoPresetId,
                videoBackendId,
                out snapshot);
        return parsed
            && VideoToolsV2JobPresetMatches(job, snapshot)
            && TryReadExactVideoToolsV2JobLifecycle(job, snapshot);
    }

    private static bool VideoToolsV2JobSourceMatches(
        JsonElement job,
        VideoToolsV2SourceSnapshot source)
    {
        if (!TryGetVideoToolsV2Path(
                job,
                "sourcePath",
                out string sourcePath)
            || !string.Equals(
                sourcePath,
                source.ExecutionCanonicalPath,
                StringComparison.Ordinal)
            || !job.TryGetProperty(
                "sourceSignature",
                out JsonElement sourceSignature)
            || !HasSingleProperty(job, "sourceSignature")
            || !TryReadVideoToolsV2Signature(
                sourceSignature,
                out long sourceSize,
                out long sourceMtimeMs)
            || sourceSize != source.ExecutionSize
            || sourceMtimeMs != source.ExecutionMtimeMs
            || !TryGetSingleVideoToolsString(
                job,
                "sourceSha256",
                out string sourceSha256)
            || !string.Equals(
                sourceSha256,
                source.ExecutionSha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (source.Kind == "managed-video-job")
        {
            return source.ProducerJobId is string producerJobId
                && TryGetSingleVideoToolsString(
                    job,
                    "sourceVideoJobId",
                    out string sourceVideoJobId)
                && IsSafeVideoToolsJobId(sourceVideoJobId)
                && string.Equals(
                    sourceVideoJobId,
                    producerJobId,
                    StringComparison.Ordinal);
        }

        return source.Kind == "staged-displayed-file"
            && !VideoToolsV2HasAnyProperty(job, "sourceVideoJobId");
    }

    private static bool TryReadExactVideoToolsV2Source(
        JsonElement source,
        out VideoToolsV2SourceSnapshot snapshot)
    {
        snapshot = default;
        if (source.ValueKind != JsonValueKind.Object
            || !TryGetSingleVideoToolsString(source, "kind", out string kind))
        {
            return false;
        }

        string? producerJobId = null;
        string canonicalPath;
        string? stagingCanonicalPath = null;
        long sourceSize;
        long sourceMtimeMs;
        long executionSize;
        long executionMtimeMs;
        string executionSha256;
        string executionCanonicalPath;
        JsonElement signature;
        JsonElement probe;
        if (kind == "managed-video-job")
        {
            if (!HasExactProperties(
                    source,
                    "kind",
                    "producerJobId",
                    "canonicalPath",
                    "signature",
                    "sha256",
                    "probe")
                || !TryGetSingleVideoToolsString(
                    source,
                    "producerJobId",
                    out producerJobId)
                || !IsSafeVideoToolsJobId(producerJobId)
                || !TryGetVideoToolsV2Path(source, "canonicalPath", out canonicalPath)
                || !source.TryGetProperty("signature", out signature)
                || !TryReadVideoToolsV2Signature(
                    signature,
                    out sourceSize,
                    out sourceMtimeMs)
                || !TryGetSingleVideoToolsString(
                    source,
                    "sha256",
                    out string sha256)
                || !IsLowerHex(sha256, 64)
                || !source.TryGetProperty("probe", out probe))
            {
                return false;
            }
            executionCanonicalPath = canonicalPath;
            executionSize = sourceSize;
            executionMtimeMs = sourceMtimeMs;
            executionSha256 = sha256;
        }
        else if (kind == "staged-displayed-file")
        {
            if (!HasExactProperties(
                    source,
                    "kind",
                    "originalCanonicalPath",
                    "originalSignature",
                    "originalSha256",
                    "stagingCanonicalPath",
                    "stagingSignature",
                    "stagingSha256",
                    "probe")
                || !TryGetVideoToolsV2Path(
                    source,
                    "originalCanonicalPath",
                    out canonicalPath)
                || !TryGetVideoToolsV2Path(
                    source,
                    "stagingCanonicalPath",
                    out stagingCanonicalPath)
                || VideoToolsV2WindowsLexicalPathsEqual(
                    canonicalPath,
                    stagingCanonicalPath)
                || !source.TryGetProperty(
                    "originalSignature",
                    out JsonElement originalSignature)
                || !source.TryGetProperty(
                    "stagingSignature",
                    out JsonElement stagingSignature)
                || !TryReadVideoToolsV2Signature(
                    originalSignature,
                    out sourceSize,
                    out sourceMtimeMs)
                || !TryReadVideoToolsV2Signature(
                    stagingSignature,
                    out long stagingSize,
                    out long stagingMtimeMs)
                || sourceSize != stagingSize
                || !TryGetSingleVideoToolsString(
                    source,
                    "originalSha256",
                    out string originalSha256)
                || !TryGetSingleVideoToolsString(
                    source,
                    "stagingSha256",
                    out string stagingSha256)
                || !IsLowerHex(originalSha256, 64)
                || !string.Equals(
                    originalSha256,
                    stagingSha256,
                    StringComparison.Ordinal)
                || !source.TryGetProperty("probe", out probe))
            {
                return false;
            }
            executionCanonicalPath = stagingCanonicalPath;
            executionSize = stagingSize;
            executionMtimeMs = stagingMtimeMs;
            executionSha256 = stagingSha256;
        }
        else
        {
            return false;
        }

        if (!TryReadExactVideoToolsV2Probe(
                probe,
                out int width,
                out int height,
                out int frameCount,
                out int fpsNumerator,
                out int fpsDenominator,
                out int durationMs,
                out int audioStreamCount,
                out long audioPacketCount,
                out string videoPtsSha256,
                out int bitDepth,
                out string dynamicRange,
                out long videoTimeBaseNumerator,
                out long videoTimeBaseDenominator,
                out long videoStartTimestamp))
        {
            return false;
        }

        snapshot = new VideoToolsV2SourceSnapshot(
            kind,
            producerJobId,
            canonicalPath,
            stagingCanonicalPath,
            sourceSize,
            sourceMtimeMs,
            width,
            height,
            frameCount,
            fpsNumerator,
            fpsDenominator,
            durationMs,
            audioStreamCount,
            audioPacketCount,
            videoPtsSha256,
            bitDepth,
            dynamicRange,
            videoTimeBaseNumerator,
            videoTimeBaseDenominator,
            videoStartTimestamp,
            executionCanonicalPath,
            executionSize,
            executionMtimeMs,
            executionSha256);
        return true;
    }

    private static bool TryReadExactVideoToolsV2JobLifecycle(
        JsonElement job,
        VideoToolsV2ReaderSnapshot snapshot)
    {
        if (!TryGetSingleVideoToolsString(job, "id", out string id)
            || !IsSafeVideoToolsJobId(id)
            || !TryGetSingleVideoToolsString(job, "status", out string status)
            || status is not ("queued" or "running" or "succeeded" or "failed" or "canceled" or "deleted")
            || !TryGetVideoToolsV2Int32(job, "progress", out int progress)
            || !TryGetVideoToolsV2Boolean(
                job,
                "cancelRequested",
                out bool cancelRequested)
            || !TryGetCanonicalVideoToolsV2Timestamp(
                job,
                "createdAt",
                out DateTimeOffset createdAt)
            || !TryGetCanonicalVideoToolsV2Timestamp(
                job,
                "updatedAt",
                out DateTimeOffset updatedAt)
            || updatedAt < createdAt)
        {
            return false;
        }

        bool hasQueueOrder = VideoToolsV2HasAnyProperty(job, "queueOrder");
        bool hasStartedAt = VideoToolsV2HasAnyProperty(job, "startedAt");
        bool hasFinishedAt = VideoToolsV2HasAnyProperty(job, "finishedAt");
        bool hasOutput = VideoToolsV2HasAnyProperty(job, "outputPath")
            || VideoToolsV2HasAnyProperty(job, "outputSha256")
            || VideoToolsV2HasAnyProperty(job, "outputBytes");
        bool hasError = VideoToolsV2HasAnyProperty(job, "errorCode")
            || VideoToolsV2HasAnyProperty(job, "errorMessage");
        bool hasAttempt = VideoToolsV2HasAnyProperty(job, "runId")
            || VideoToolsV2HasAnyProperty(job, "workerInstanceId")
            || VideoToolsV2HasAnyProperty(job, "lastHeartbeatAt")
            || VideoToolsV2HasAnyProperty(job, "lastProgressAt")
            || VideoToolsV2HasAnyProperty(job, "externalPromptId")
            || VideoToolsV2HasAnyProperty(job, "externalProcessId")
            || VideoToolsV2HasAnyProperty(job, "diagnostics");

        if (status == "queued")
        {
            return progress == 0
                && !cancelRequested
                && TryGetVideoToolsV2Integer(
                    job,
                    "queueOrder",
                    0,
                    VideoToolsV2MaximumSafeInteger,
                    out _)
                && !hasStartedAt
                && !hasFinishedAt
                && !hasOutput
                && !hasError
                && !hasAttempt;
        }

        if (hasQueueOrder
            || !TryGetCanonicalVideoToolsV2Timestamp(
                job,
                "startedAt",
                out DateTimeOffset startedAt)
            || startedAt < createdAt)
        {
            return false;
        }

        if (status == "running")
        {
            return progress is >= 1 and <= 99
                && !cancelRequested
                && !hasFinishedAt
                && !hasOutput
                && !hasError
                && TryReadExactVideoToolsV2RunningAttempt(
                    job,
                    startedAt,
                    updatedAt);
        }

        if (!TryGetCanonicalVideoToolsV2Timestamp(
                job,
                "finishedAt",
                out DateTimeOffset finishedAt)
            || finishedAt < startedAt
            || updatedAt < finishedAt
            || hasAttempt)
        {
            return false;
        }

        if (status == "succeeded")
        {
            return progress == 100
                && !cancelRequested
                && !hasError
                && TryGetVideoToolsV2Path(
                    job,
                    "outputPath",
                    out string outputPath)
                && VideoToolsV2ManagedOutputPathMatches(
                    job,
                    id,
                    snapshot,
                    outputPath)
                && TryGetSingleVideoToolsString(
                    job,
                    "outputSha256",
                    out string outputSha256)
                && IsLowerHex(outputSha256, 64)
                && TryGetVideoToolsV2Integer(
                    job,
                    "outputBytes",
                    1,
                    VideoToolsV2MaximumOutputBytes,
                    out _);
        }

        if (status == "failed")
        {
            return progress is >= 0 and <= 99
                && !cancelRequested
                && !hasOutput
                && TryGetVideoToolsV2Error(job);
        }

        if (status == "canceled")
        {
            return progress is >= 0 and <= 99
                && cancelRequested
                && !hasOutput
                && !hasError;
        }

        return progress == 100
            && !cancelRequested
            && !hasOutput
            && !hasError;
    }

    private static bool TryReadExactVideoToolsV2RunningAttempt(
        JsonElement job,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt)
    {
        if (!TryGetVideoToolsV2TechnicalText(job, "runId", 128, out _)
            || !TryGetVideoToolsV2TechnicalText(
                job,
                "workerInstanceId",
                128,
                out _)
            || !TryGetCanonicalVideoToolsV2Timestamp(
                job,
                "lastHeartbeatAt",
                out DateTimeOffset heartbeat)
            || heartbeat < startedAt
            || heartbeat > updatedAt)
        {
            return false;
        }

        if (VideoToolsV2HasAnyProperty(job, "lastProgressAt")
            && (!TryGetCanonicalVideoToolsV2Timestamp(
                    job,
                    "lastProgressAt",
                    out DateTimeOffset progressAt)
                || progressAt < startedAt
                || progressAt > updatedAt))
        {
            return false;
        }
        if (VideoToolsV2HasAnyProperty(job, "externalPromptId")
            && !TryGetVideoToolsV2TechnicalText(
                job,
                "externalPromptId",
                512,
                out _))
        {
            return false;
        }
        if (VideoToolsV2HasAnyProperty(job, "externalProcessId")
            && !TryGetVideoToolsV2Integer(
                job,
                "externalProcessId",
                1,
                int.MaxValue,
                out _))
        {
            return false;
        }
        return !VideoToolsV2HasAnyProperty(job, "diagnostics")
            || HasSingleProperty(job, "diagnostics")
                && job.TryGetProperty(
                    "diagnostics",
                    out JsonElement diagnostics)
                && diagnostics.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetVideoToolsV2Error(JsonElement job)
    {
        return TryGetSingleVideoToolsString(
                job,
                "errorCode",
                out string errorCode)
            && errorCode.Length is >= 1 and <= 128
            && errorCode.All(character => character is >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '_')
            && TryGetVideoToolsV2BoundedText(
                job,
                "errorMessage",
                2_000,
                asciiOnly: false,
                out _);
    }

    private static bool TryGetCanonicalVideoToolsV2Timestamp(
        JsonElement value,
        string name,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return TryGetSingleVideoToolsString(value, name, out string text)
            && DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal,
                out timestamp)
            && string.Equals(
                timestamp.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture),
                text,
                StringComparison.Ordinal);
    }

    private static bool VideoToolsV2JobPresetMatches(
        JsonElement job,
        VideoToolsV2ReaderSnapshot snapshot)
    {
        if (!job.TryGetProperty("preset", out JsonElement preset)
            || !HasSingleProperty(job, "preset")
            || !HasExactProperties(
                preset,
                "id",
                "label",
                "modelFamily",
                "modelName",
                "scale",
                "outputFormat",
                "denoise",
                "sharpen",
                "detail",
                "smoothness",
                "colorBrightness",
                "colorContrast",
                "colorSaturation",
                "options")
            || !VideoToolsExactString(preset, "id", snapshot.PresetId)
            || !VideoToolsExactString(
                preset,
                "label",
                snapshot.Kind == "edit"
                    ? "Aibos Video Edit v2"
                    : "Aibos Video Finish v2")
            || !VideoToolsExactString(preset, "modelFamily", "general")
            || !VideoToolsExactString(
                preset,
                "modelName",
                snapshot.BackendId)
            || !VideoToolsV2ExactInt32(
                preset,
                "scale",
                snapshot.Kind == "finish" ? snapshot.FinishScale!.Value : 1)
            || !VideoToolsExactString(preset, "outputFormat", "png")
            || !VideoToolsV2ExactInt32(preset, "denoise", 0)
            || !VideoToolsV2ExactInt32(preset, "sharpen", 0)
            || !VideoToolsV2ExactInt32(preset, "detail", 0)
            || !VideoToolsV2ExactInt32(preset, "smoothness", 0)
            || !VideoToolsV2ExactInt32(preset, "colorBrightness", 0)
            || !VideoToolsV2ExactInt32(preset, "colorContrast", 0)
            || !VideoToolsV2ExactInt32(preset, "colorSaturation", 0)
            || !preset.TryGetProperty("options", out JsonElement options)
            || !HasExactProperties(
                options,
                "backendId",
                "protocol",
                "kind",
                "container"))
        {
            return false;
        }
        return VideoToolsExactString(
                options,
                "backendId",
                snapshot.BackendId)
            && VideoToolsExactString(
                options,
                "protocol",
                VideoToolsV2Protocol)
            && VideoToolsExactString(options, "kind", snapshot.Kind)
            && VideoToolsExactString(options, "container", "mp4");
    }

    private static bool TryReadVideoToolsV2Signature(
        JsonElement signature,
        out long size,
        out long mtimeMs)
    {
        size = 0;
        mtimeMs = 0;
        return HasExactProperties(signature, "size", "mtimeMs")
            && TryGetVideoToolsV2Integer(
                signature,
                "size",
                1,
                VideoToolsV2MaximumSourceBytes,
                out size)
            && TryGetVideoToolsV2Integer(
                signature,
                "mtimeMs",
                -VideoToolsV2MaximumSafeInteger,
                VideoToolsV2MaximumSafeInteger,
                out mtimeMs);
    }

    private static bool TryReadExactVideoToolsV2Probe(
        JsonElement probe,
        out int width,
        out int height,
        out int frameCount,
        out int fpsNumerator,
        out int fpsDenominator,
        out int durationMs,
        out int audioStreamCount,
        out long audioPacketCount,
        out string videoPtsSha256,
        out int bitDepth,
        out string dynamicRange,
        out long videoTimeBaseNumerator,
        out long videoTimeBaseDenominator,
        out long videoStartTimestamp)
    {
        width = height = frameCount = fpsNumerator = fpsDenominator = 0;
        durationMs = audioStreamCount = bitDepth = 0;
        audioPacketCount = 0;
        videoTimeBaseNumerator = videoTimeBaseDenominator = 0;
        videoStartTimestamp = 0;
        videoPtsSha256 = dynamicRange = "";
        if (!HasExactProperties(
                probe,
                "container",
                "width",
                "height",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "videoStreamCount",
                "audioStreamCount",
                "videoTimeBaseNumerator",
                "videoTimeBaseDenominator",
                "videoStartTimestamp",
                "videoPtsSha256",
                "bitDepth",
                "dynamicRange",
                "audio")
            || !VideoToolsExactString(probe, "container", "mp4")
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "width",
                1,
                VideoToolsV2MaximumSourceWidth,
                out width)
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "height",
                1,
                VideoToolsV2MaximumSourceHeight,
                out height)
            || checked((long)width * height)
                > VideoToolsV2MaximumSourcePixelArea
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "frameCount",
                1,
                VideoToolsV2MaximumSourceFrames,
                out frameCount)
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "fpsNumerator",
                1,
                60,
                out fpsNumerator)
            || !VideoToolsV2AllowedFps.Contains(fpsNumerator)
            || !VideoToolsV2ExactInt32(probe, "fpsDenominator", 1)
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "durationMs",
                1,
                VideoToolsV2MaximumSourceDurationMs,
                out durationMs)
            || !VideoToolsV2ExactInt32(probe, "videoStreamCount", 1)
            || !TryGetBoundedVideoToolsV2Int(
                probe,
                "audioStreamCount",
                0,
                1,
                out audioStreamCount)
            || !TryGetPositiveVideoToolsV2Integer(
                probe,
                "videoTimeBaseNumerator",
                out videoTimeBaseNumerator)
            || !TryGetPositiveVideoToolsV2Integer(
                probe,
                "videoTimeBaseDenominator",
                out videoTimeBaseDenominator)
            || !TryGetVideoToolsV2Integer(
                probe,
                "videoStartTimestamp",
                -VideoToolsV2MaximumSafeInteger,
                VideoToolsV2MaximumSafeInteger,
                out videoStartTimestamp)
            || !TryGetSingleVideoToolsString(
                probe,
                "videoPtsSha256",
                out videoPtsSha256)
            || !IsLowerHex(videoPtsSha256, 64)
            || !VideoToolsV2ExactInt32(probe, "bitDepth", 8)
            || !TryGetSingleVideoToolsString(
                probe,
                "dynamicRange",
                out dynamicRange)
            || !string.Equals(dynamicRange, "SDR", StringComparison.Ordinal)
            || !probe.TryGetProperty("audio", out JsonElement audio)
            || !TryReadExactVideoToolsV2Audio(
                audio,
                audioStreamCount,
                out audioPacketCount))
        {
            return false;
        }

        fpsDenominator = 1;
        bitDepth = 8;
        double expectedDuration = frameCount * 1_000d / fpsNumerator;
        return Math.Abs(durationMs - expectedDuration) <= 100;
    }

    private static bool TryReadExactVideoToolsV2Audio(
        JsonElement audio,
        int audioStreamCount,
        out long packetCount)
    {
        packetCount = 0;
        if (audioStreamCount == 0)
            return audio.ValueKind == JsonValueKind.Null;
        if (audioStreamCount != 1
            || !HasExactProperties(
                audio,
                "codec",
                "codecTag",
                "profile",
                "sampleRate",
                "channels",
                "timeBaseNumerator",
                "timeBaseDenominator",
                "startTimestamp",
                "durationTimestamp",
                "packetCount",
                "packetPayloadBytes",
                "packetPayloadSha256")
            || !TryGetVideoToolsV2BoundedText(
                audio,
                "codec",
                64,
                asciiOnly: false,
                out _)
            || !TryGetVideoToolsV2BoundedText(
                audio,
                "codecTag",
                64,
                asciiOnly: false,
                out _)
            || !TryGetVideoToolsV2BoundedText(
                audio,
                "profile",
                128,
                asciiOnly: false,
                out _)
            || !TryGetBoundedVideoToolsV2Int(
                audio,
                "sampleRate",
                1,
                384_000,
                out _)
            || !TryGetBoundedVideoToolsV2Int(
                audio,
                "channels",
                1,
                32,
                out _)
            || !TryGetPositiveVideoToolsV2Integer(
                audio,
                "timeBaseNumerator",
                out _)
            || !TryGetPositiveVideoToolsV2Integer(
                audio,
                "timeBaseDenominator",
                out _)
            || !TryGetVideoToolsV2Integer(
                audio,
                "startTimestamp",
                -VideoToolsV2MaximumSafeInteger,
                VideoToolsV2MaximumSafeInteger,
                out _)
            || !TryGetPositiveVideoToolsV2Integer(
                audio,
                "durationTimestamp",
                out _)
            || !TryGetVideoToolsV2Integer(
                audio,
                "packetCount",
                1,
                65_536,
                out packetCount)
            || !TryGetVideoToolsV2Integer(
                audio,
                "packetPayloadBytes",
                1,
                VideoToolsV2MaximumSourceBytes,
                out _)
            || !TryGetSingleVideoToolsString(
                audio,
                "packetPayloadSha256",
                out string packetPayloadSha256)
            || !IsLowerHex(packetPayloadSha256, 64))
        {
            return false;
        }
        return true;
    }

    private static bool TryReadExactVideoToolsV2Edit(
        JsonElement video,
        VideoToolsV2SourceSnapshot source,
        string presetId,
        string backendId,
        out VideoToolsV2ReaderSnapshot snapshot)
    {
        snapshot = null!;
        if (!HasExactProperties(
                video,
                "schemaVersion",
                "protocol",
                "kind",
                "presetId",
                "backendId",
                "seed",
                "receipts",
                "source",
                "requested",
                "plan",
                "delivery")
            || !string.Equals(
                presetId,
                "aibos-video-edit-v2",
                StringComparison.Ordinal)
            || !VideoToolsV2EditBackends.Contains(backendId)
            || !TryGetBoundedVideoToolsV2Int(
                video,
                "seed",
                0,
                int.MaxValue,
                out _)
            || !video.TryGetProperty("receipts", out JsonElement receipts)
            || !HasExactProperties(
                receipts,
                "workflowReceiptId",
                "modelReceiptId",
                "runtimeReceiptId",
                "timelineMappingReceiptId",
                "deliveryMappingReceiptId")
            || !VideoToolsV2AllTechnicalFields(
                receipts,
                "workflowReceiptId",
                "modelReceiptId",
                "runtimeReceiptId",
                "timelineMappingReceiptId",
                "deliveryMappingReceiptId")
            || !video.TryGetProperty("requested", out JsonElement requested)
            || !TryReadExactVideoToolsV2EditRequest(
                requested,
                source,
                out VideoToolsV2EditRequestSnapshot request)
            || !video.TryGetProperty("plan", out JsonElement plan)
            || !TryReadExactVideoToolsV2EditPlan(
                plan,
                source,
                request,
                out VideoToolsV2EditPlanSnapshot editPlan)
            || !video.TryGetProperty("delivery", out JsonElement delivery)
            || !TryReadExactVideoToolsV2EditDelivery(
                delivery,
                source,
                request,
                editPlan))
        {
            return false;
        }

        int selectedFrames = request.EndFrameExclusive - request.StartFrame;
        snapshot = new VideoToolsV2ReaderSnapshot(
            "edit",
            source.Kind,
            source.ProducerJobId,
            source.CanonicalPath,
            source.StagingCanonicalPath,
            source.Size,
            source.MtimeMs,
            presetId,
            backendId,
            source.Width,
            source.Height,
            source.FrameCount,
            source.FpsNumerator,
            source.FpsDenominator,
            source.DurationMs,
            source.AudioStreamCount,
            editPlan.OutputWidth,
            editPlan.OutputHeight,
            selectedFrames,
            source.FpsNumerator,
            source.FpsDenominator,
            editPlan.DurationMs,
            request.StartFrame,
            request.EndFrameExclusive,
            request.InstructionJa,
            request.SummaryJa,
            request.AudioPolicy,
            request.Steps,
            request.Strength,
            request.MaximumPixelArea,
            null,
            null);
        return true;
    }

    private static bool TryReadExactVideoToolsV2EditRequest(
        JsonElement requested,
        VideoToolsV2SourceSnapshot source,
        out VideoToolsV2EditRequestSnapshot snapshot)
    {
        snapshot = default;
        if (!HasExactProperties(
                requested,
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
            || !VideoToolsV2ExactInt32(requested, "schemaVersion", 2)
            || !VideoToolsExactString(requested, "kind", "edit")
            || !requested.TryGetProperty(
                "source",
                out JsonElement requestSource)
            || !VideoToolsV2RequestSourceMatches(requestSource, source)
            || !requested.TryGetProperty(
                "selection",
                out JsonElement selection)
            || !HasExactProperties(
                selection,
                "startFrame",
                "endFrameExclusive")
            || !TryGetBoundedVideoToolsV2Int(
                selection,
                "startFrame",
                0,
                source.FrameCount - 1,
                out int startFrame)
            || !TryGetBoundedVideoToolsV2Int(
                selection,
                "endFrameExclusive",
                1,
                source.FrameCount,
                out int endFrameExclusive)
            || endFrameExclusive <= startFrame
            || endFrameExclusive - startFrame > 300
            || (long)(endFrameExclusive - startFrame)
                * 1_000
                * source.FpsDenominator
                > 5_000L * source.FpsNumerator
            || !TryGetVideoToolsV2BoundedText(
                requested,
                "instructionJa",
                4_000,
                asciiOnly: false,
                out string instructionJa)
            || !requested.TryGetProperty("compiled", out JsonElement compiled)
            || !HasExactProperties(
                compiled,
                "backendPrompt",
                "summaryJa",
                "compilerRevision",
                "contextDigest",
                "renderer")
            || !TryGetVideoToolsV2BoundedText(
                compiled,
                "backendPrompt",
                8_000,
                asciiOnly: false,
                out string backendPrompt)
            || !TryGetVideoToolsV2BoundedText(
                compiled,
                "summaryJa",
                2_000,
                asciiOnly: false,
                out string summaryJa)
            || !TryGetVideoToolsV2TechnicalText(
                compiled,
                "compilerRevision",
                128,
                out string compilerRevision)
            || !TryGetSingleVideoToolsString(
                compiled,
                "contextDigest",
                out string contextDigest)
            || !IsLowerHex(contextDigest, 64)
            || !compiled.TryGetProperty(
                "renderer",
                out JsonElement renderer)
            || !VideoEditV2TransientContract.TryParseOfficialRendererSidecar(
                renderer,
                backendPrompt,
                compilerRevision,
                out _)
            || !TryGetSingleVideoToolsString(
                requested,
                "audioPolicy",
                out string audioPolicy)
            || audioPolicy is not ("preserve" or "mute")
            || !TryGetBoundedVideoToolsV2Int(
                requested,
                "steps",
                1,
                40,
                out int steps)
            || !TryGetBoundedVideoToolsV2Int(
                requested,
                "strength",
                10,
                100,
                out int strength)
            || !TryGetBoundedVideoToolsV2Int(
                requested,
                "maximumPixelArea",
                VideoToolsV2EditPixelAreas[0],
                VideoToolsV2EditPixelAreas[^1],
                out int maximumPixelArea)
            || !VideoToolsV2EditPixelAreas.Contains(maximumPixelArea))
        {
            return false;
        }

        snapshot = new VideoToolsV2EditRequestSnapshot(
            startFrame,
            endFrameExclusive,
            instructionJa,
            summaryJa,
            audioPolicy,
            steps,
            strength,
            maximumPixelArea);
        return true;
    }

    private static bool VideoToolsV2RequestSourceMatches(
        JsonElement requestSource,
        VideoToolsV2SourceSnapshot source)
    {
        if (source.Kind == "managed-video-job")
        {
            return HasExactProperties(
                    requestSource,
                    "kind",
                    "sourceVideoJobId")
                && VideoToolsExactString(
                    requestSource,
                    "kind",
                    "managed-video-job")
                && TryGetSingleVideoToolsString(
                    requestSource,
                    "sourceVideoJobId",
                    out string sourceVideoJobId)
                && string.Equals(
                    sourceVideoJobId,
                    source.ProducerJobId,
                    StringComparison.Ordinal);
        }

        return source.Kind == "staged-displayed-file"
            && HasExactProperties(requestSource, "kind")
            && VideoToolsExactString(
                requestSource,
                "kind",
                "displayed-file");
    }

    private static bool TryReadExactVideoToolsV2EditPlan(
        JsonElement plan,
        VideoToolsV2SourceSnapshot source,
        VideoToolsV2EditRequestSnapshot request,
        out VideoToolsV2EditPlanSnapshot snapshot)
    {
        snapshot = default;
        if (!HasExactProperties(
                plan,
                "selected",
                "sourceToBackendMap",
                "backendWindow",
                "deliveryCrop",
                "strengthMapping",
                "modelCanvas",
                "audioPlan")
            || !plan.TryGetProperty("selected", out JsonElement selected)
            || !HasExactProperties(
                selected,
                "startFrame",
                "endFrameExclusive",
                "startPts",
                "endPtsExclusive",
                "durationMs")
            || !VideoToolsV2ExactInt32(
                selected,
                "startFrame",
                request.StartFrame)
            || !VideoToolsV2ExactInt32(
                selected,
                "endFrameExclusive",
                request.EndFrameExclusive)
            || !TryGetVideoToolsV2Integer(
                selected,
                "startPts",
                -VideoToolsV2MaximumSafeInteger,
                VideoToolsV2MaximumSafeInteger,
                out long startPts)
            || !TryGetVideoToolsV2Integer(
                selected,
                "endPtsExclusive",
                -VideoToolsV2MaximumSafeInteger,
                VideoToolsV2MaximumSafeInteger,
                out long endPtsExclusive)
            || endPtsExclusive <= startPts
            || !VideoToolsV2SelectedPtsMatch(
                source,
                request,
                startPts,
                endPtsExclusive)
            || !TryGetFiniteVideoToolsV2Number(
                selected,
                "durationMs",
                1,
                5_000,
                out double durationMs)
            || !VideoToolsV2DurationMatchesFrames(
                request.EndFrameExclusive - request.StartFrame,
                source.FpsNumerator,
                source.FpsDenominator,
                durationMs)
            || !plan.TryGetProperty(
                "sourceToBackendMap",
                out JsonElement sourceMap)
            || !TryReadExactVideoToolsV2SourceMap(
                sourceMap,
                source,
                request,
                out int backendStart,
                out int backendEnd)
            || !plan.TryGetProperty(
                "backendWindow",
                out JsonElement backendWindow)
            || !TryReadExactVideoToolsV2BackendWindow(
                backendWindow,
                backendStart,
                backendEnd,
                out int backendWindowFrameCount)
            || !plan.TryGetProperty(
                "deliveryCrop",
                out JsonElement deliveryCrop)
            || !TryReadExactVideoToolsV2DeliveryCrop(
                deliveryCrop,
                source,
                request,
                backendWindowFrameCount)
            || !plan.TryGetProperty(
                "strengthMapping",
                out JsonElement strengthMapping)
            || !HasExactProperties(
                strengthMapping,
                "mappingRevision",
                "numerator",
                "denominator")
            || !TryGetVideoToolsV2TechnicalText(
                strengthMapping,
                "mappingRevision",
                256,
                out _)
            || !TryGetVideoToolsV2Integer(
                strengthMapping,
                "numerator",
                0,
                1_000_000,
                out long strengthNumerator)
            || !TryGetVideoToolsV2Integer(
                strengthMapping,
                "denominator",
                1,
                1_000_000,
                out long strengthDenominator)
            || GreatestCommonDivisor(
                strengthNumerator,
                strengthDenominator) != 1
            || !plan.TryGetProperty(
                "modelCanvas",
                out JsonElement modelCanvas)
            || !HasExactProperties(
                modelCanvas,
                "width",
                "height",
                "maximumPixelArea")
            || !TryGetBoundedVideoToolsV2Int(
                modelCanvas,
                "width",
                1,
                VideoToolsV2MaximumOutputWidth,
                out int outputWidth)
            || !TryGetBoundedVideoToolsV2Int(
                modelCanvas,
                "height",
                1,
                VideoToolsV2MaximumOutputHeight,
                out int outputHeight)
            || !VideoToolsV2ExactInt32(
                modelCanvas,
                "maximumPixelArea",
                request.MaximumPixelArea)
            || checked((long)outputWidth * outputHeight)
                > request.MaximumPixelArea
            || !plan.TryGetProperty("audioPlan", out JsonElement audioPlan)
            || !TryReadExactVideoToolsV2AudioPlan(
                audioPlan,
                source,
                request.AudioPolicy,
                out string audioPlanKind,
                out long? packetStart,
                out long? packetEnd))
        {
            return false;
        }

        snapshot = new VideoToolsV2EditPlanSnapshot(
            durationMs,
            outputWidth,
            outputHeight,
            audioPlanKind,
            packetStart,
            packetEnd);
        return true;
    }

    private static bool TryReadExactVideoToolsV2SourceMap(
        JsonElement sourceMap,
        VideoToolsV2SourceSnapshot source,
        VideoToolsV2EditRequestSnapshot request,
        out int backendStart,
        out int backendEnd)
    {
        backendStart = backendEnd = 0;
        return HasExactProperties(
                sourceMap,
                "revision",
                "sourceFpsNumerator",
                "sourceFpsDenominator",
                "backendFpsNumerator",
                "backendFpsDenominator",
                "sourceStartFrame",
                "sourceEndFrameExclusive",
                "backendStartFrame",
                "backendEndFrameExclusive")
            && TryGetVideoToolsV2TechnicalText(
                sourceMap,
                "revision",
                256,
                out _)
            && VideoToolsV2ExactInt32(
                sourceMap,
                "sourceFpsNumerator",
                source.FpsNumerator)
            && VideoToolsV2ExactInt32(
                sourceMap,
                "sourceFpsDenominator",
                source.FpsDenominator)
            && TryGetBoundedVideoToolsV2Int(
                sourceMap,
                "backendFpsNumerator",
                1,
                240,
                out _)
            && TryGetBoundedVideoToolsV2Int(
                sourceMap,
                "backendFpsDenominator",
                1,
                240,
                out _)
            && VideoToolsV2ExactInt32(
                sourceMap,
                "sourceStartFrame",
                request.StartFrame)
            && VideoToolsV2ExactInt32(
                sourceMap,
                "sourceEndFrameExclusive",
                request.EndFrameExclusive)
            && TryGetBoundedVideoToolsV2Int(
                sourceMap,
                "backendStartFrame",
                0,
                999,
                out backendStart)
            && TryGetBoundedVideoToolsV2Int(
                sourceMap,
                "backendEndFrameExclusive",
                1,
                1_000,
                out backendEnd)
            && backendEnd > backendStart;
    }

    private static bool TryReadExactVideoToolsV2BackendWindow(
        JsonElement backendWindow,
        int backendStart,
        int backendEnd,
        out int frameCount)
    {
        frameCount = 0;
        if (!HasExactProperties(
                backendWindow,
                "frameCount",
                "leadingPadFrames",
                "trailingPadFrames",
                "alignmentRevision")
            || !TryGetBoundedVideoToolsV2Int(
                backendWindow,
                "frameCount",
                1,
                1_000,
                out frameCount)
            || !TryGetBoundedVideoToolsV2Int(
                backendWindow,
                "leadingPadFrames",
                0,
                999,
                out int leading)
            || !TryGetBoundedVideoToolsV2Int(
                backendWindow,
                "trailingPadFrames",
                0,
                999,
                out int trailing)
            || !TryGetVideoToolsV2TechnicalText(
                backendWindow,
                "alignmentRevision",
                256,
                out _))
        {
            return false;
        }
        return frameCount == checked(backendEnd - backendStart + leading + trailing);
    }

    private static bool TryReadExactVideoToolsV2DeliveryCrop(
        JsonElement crop,
        VideoToolsV2SourceSnapshot source,
        VideoToolsV2EditRequestSnapshot request,
        int backendWindowFrameCount)
        => HasExactProperties(
                crop,
                "revision",
                "backendStartFrame",
                "backendEndFrameExclusive",
                "outputFrameCount",
                "outputFpsNumerator",
                "outputFpsDenominator")
            && TryGetVideoToolsV2TechnicalText(
                crop,
                "revision",
                256,
                out _)
            && TryGetBoundedVideoToolsV2Int(
                crop,
                "backendStartFrame",
                0,
                999,
                out int cropStart)
            && TryGetBoundedVideoToolsV2Int(
                crop,
                "backendEndFrameExclusive",
                1,
                1_000,
                out int cropEnd)
            && cropEnd > cropStart
            && cropEnd <= backendWindowFrameCount
            && VideoToolsV2ExactInt32(
                crop,
                "outputFrameCount",
                request.EndFrameExclusive - request.StartFrame)
            && VideoToolsV2ExactInt32(
                crop,
                "outputFpsNumerator",
                source.FpsNumerator)
            && VideoToolsV2ExactInt32(
                crop,
                "outputFpsDenominator",
                source.FpsDenominator);

    private static bool TryReadExactVideoToolsV2AudioPlan(
        JsonElement audioPlan,
        VideoToolsV2SourceSnapshot source,
        string audioPolicy,
        out string kind,
        out long? packetStart,
        out long? packetEnd)
    {
        kind = "";
        packetStart = packetEnd = null;
        if (!TryGetSingleVideoToolsString(audioPlan, "kind", out kind))
            return false;
        if (kind == "preserve-packets")
        {
            if (audioPolicy != "preserve"
                || source.AudioStreamCount != 1
                || !HasExactProperties(
                    audioPlan,
                    "kind",
                    "policy",
                    "packetStartIndex",
                    "packetEndIndexExclusive",
                    "editListRevision")
                || !VideoToolsExactString(audioPlan, "policy", "preserve")
                || !TryGetVideoToolsV2Integer(
                    audioPlan,
                    "packetStartIndex",
                    0,
                    source.AudioPacketCount - 1,
                    out long start)
                || !TryGetVideoToolsV2Integer(
                    audioPlan,
                    "packetEndIndexExclusive",
                    1,
                    source.AudioPacketCount,
                    out long end)
                || end <= start
                || !TryGetVideoToolsV2TechnicalText(
                    audioPlan,
                    "editListRevision",
                    256,
                    out _))
            {
                return false;
            }
            packetStart = start;
            packetEnd = end;
            return true;
        }
        if (kind == "preserve-no-packets")
        {
            return audioPolicy == "preserve"
                && HasExactProperties(audioPlan, "kind", "policy")
                && VideoToolsExactString(audioPlan, "policy", "preserve");
        }
        return kind == "mute"
            && audioPolicy == "mute"
            && HasExactProperties(audioPlan, "kind", "policy")
            && VideoToolsExactString(audioPlan, "policy", "mute");
    }

    private static bool TryReadExactVideoToolsV2EditDelivery(
        JsonElement delivery,
        VideoToolsV2SourceSnapshot source,
        VideoToolsV2EditRequestSnapshot request,
        VideoToolsV2EditPlanSnapshot plan)
    {
        if (!HasExactProperties(
                delivery,
                "width",
                "height",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "videoPtsSha256",
                "childClip",
                "fullSourceSplice",
                "discardGeneratedAudio",
                "audioDelivery")
            || !VideoToolsV2ExactInt32(delivery, "width", plan.OutputWidth)
            || !VideoToolsV2ExactInt32(delivery, "height", plan.OutputHeight)
            || !VideoToolsV2ExactInt32(
                delivery,
                "frameCount",
                request.EndFrameExclusive - request.StartFrame)
            || !VideoToolsV2ExactInt32(
                delivery,
                "fpsNumerator",
                source.FpsNumerator)
            || !VideoToolsV2ExactInt32(
                delivery,
                "fpsDenominator",
                source.FpsDenominator)
            || !TryGetFiniteVideoToolsV2Number(
                delivery,
                "durationMs",
                1,
                5_000,
                out double deliveryDurationMs)
            || !VideoToolsV2DurationMatchesFrames(
                request.EndFrameExclusive - request.StartFrame,
                source.FpsNumerator,
                source.FpsDenominator,
                deliveryDurationMs)
            || !TryGetSingleVideoToolsString(
                delivery,
                "videoPtsSha256",
                out string videoPtsSha256)
            || !IsLowerHex(videoPtsSha256, 64)
            || !VideoToolsExactBoolean(delivery, "childClip", true)
            || !VideoToolsExactBoolean(delivery, "fullSourceSplice", false)
            || !VideoToolsExactBoolean(
                delivery,
                "discardGeneratedAudio",
                true)
            || !delivery.TryGetProperty(
                "audioDelivery",
                out JsonElement audioDelivery))
        {
            return false;
        }

        return TryReadExactVideoToolsV2AudioDelivery(
            audioDelivery,
            plan);
    }

    private static bool TryReadExactVideoToolsV2AudioDelivery(
        JsonElement delivery,
        VideoToolsV2EditPlanSnapshot plan)
    {
        if (!TryGetSingleVideoToolsString(delivery, "kind", out string kind))
            return false;
        if (plan.AudioPlanKind == "preserve-packets")
        {
            return kind == "remuxed-source-packets"
                && HasExactProperties(
                    delivery,
                    "kind",
                    "policy",
                    "audioStreamCount",
                    "sourcePacketStartIndex",
                    "sourcePacketEndIndexExclusive",
                    "sourcePacketPayloadSha256")
                && VideoToolsExactString(delivery, "policy", "preserve")
                && VideoToolsV2ExactInt32(delivery, "audioStreamCount", 1)
                && TryGetVideoToolsV2Integer(
                    delivery,
                    "sourcePacketStartIndex",
                    plan.PacketStartIndex!.Value,
                    plan.PacketStartIndex.Value,
                    out _)
                && TryGetVideoToolsV2Integer(
                    delivery,
                    "sourcePacketEndIndexExclusive",
                    plan.PacketEndIndexExclusive!.Value,
                    plan.PacketEndIndexExclusive.Value,
                    out _)
                && TryGetSingleVideoToolsString(
                    delivery,
                    "sourcePacketPayloadSha256",
                    out string packetSha256)
                && IsLowerHex(packetSha256, 64);
        }
        if (plan.AudioPlanKind == "preserve-no-packets")
        {
            return kind == "no-audio-preserve-empty"
                && HasExactProperties(
                    delivery,
                    "kind",
                    "policy",
                    "audioStreamCount")
                && VideoToolsExactString(delivery, "policy", "preserve")
                && VideoToolsV2ExactInt32(delivery, "audioStreamCount", 0);
        }
        return plan.AudioPlanKind == "mute"
            && kind == "no-audio-muted"
            && HasExactProperties(
                delivery,
                "kind",
                "policy",
                "audioStreamCount")
            && VideoToolsExactString(delivery, "policy", "mute")
            && VideoToolsV2ExactInt32(delivery, "audioStreamCount", 0);
    }

    private static bool TryReadExactVideoToolsV2Finish(
        JsonElement video,
        VideoToolsV2SourceSnapshot source,
        string presetId,
        string backendId,
        out VideoToolsV2ReaderSnapshot snapshot)
    {
        snapshot = null!;
        if (!HasExactProperties(
                video,
                "schemaVersion",
                "protocol",
                "kind",
                "presetId",
                "backendId",
                "receipts",
                "source",
                "requested",
                "plan",
                "delivery")
            || !string.Equals(
                presetId,
                "aibos-video-finish-v2",
                StringComparison.Ordinal)
            || !VideoToolsV2FinishBackends.Contains(backendId)
            || !video.TryGetProperty("receipts", out JsonElement receipts)
            || !HasExactProperties(
                receipts,
                "runtimeReceiptId",
                "modelReceiptId",
                "backendMappingRevision",
                "deliveryMappingReceiptId",
                "sceneCutCanaryReceiptId",
                "chunkSeamCanaryReceiptId")
            || !VideoToolsV2AllTechnicalFields(
                receipts,
                "runtimeReceiptId",
                "modelReceiptId",
                "backendMappingRevision",
                "deliveryMappingReceiptId",
                "sceneCutCanaryReceiptId",
                "chunkSeamCanaryReceiptId")
            || !video.TryGetProperty("requested", out JsonElement requested)
            || !HasExactProperties(
                requested,
                "schemaVersion",
                "kind",
                "source",
                "mode",
                "scale")
            || !VideoToolsV2ExactInt32(requested, "schemaVersion", 2)
            || !VideoToolsExactString(requested, "kind", "finish")
            || !requested.TryGetProperty("source", out JsonElement requestSource)
            || !VideoToolsV2RequestSourceMatches(requestSource, source)
            || !TryGetSingleVideoToolsString(
                requested,
                "mode",
                out string mode)
            || mode is not ("fast" or "standard" or "quality")
            || !TryGetBoundedVideoToolsV2Int(
                requested,
                "scale",
                2,
                4,
                out int scale)
            || scale is not (2 or 4)
            || !video.TryGetProperty("plan", out JsonElement plan)
            || !TryReadExactVideoToolsV2FinishPlan(
                plan,
                source,
                backendId,
                scale,
                out int outputWidth,
                out int outputHeight)
            || !video.TryGetProperty("delivery", out JsonElement delivery)
            || !TryReadExactVideoToolsV2FinishDelivery(
                delivery,
                source,
                outputWidth,
                outputHeight))
        {
            return false;
        }

        snapshot = new VideoToolsV2ReaderSnapshot(
            "finish",
            source.Kind,
            source.ProducerJobId,
            source.CanonicalPath,
            source.StagingCanonicalPath,
            source.Size,
            source.MtimeMs,
            presetId,
            backendId,
            source.Width,
            source.Height,
            source.FrameCount,
            source.FpsNumerator,
            source.FpsDenominator,
            source.DurationMs,
            source.AudioStreamCount,
            outputWidth,
            outputHeight,
            source.FrameCount,
            source.FpsNumerator,
            source.FpsDenominator,
            source.DurationMs,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            mode,
            scale);
        return true;
    }

    private static bool TryReadExactVideoToolsV2FinishPlan(
        JsonElement plan,
        VideoToolsV2SourceSnapshot source,
        string backendId,
        int scale,
        out int outputWidth,
        out int outputHeight)
    {
        outputWidth = outputHeight = 0;
        string expectedRole = backendId switch
        {
            "nvidia-vfx-vsr-1.2-candidate-v1" => "faithful",
            "seedvr2-3b-detail-candidate-v1" => "generative-detail",
            "nanovsr-1.7m-4x-candidate-v1" => "lightweight-4x",
            _ => "",
        };
        try
        {
            outputWidth = checked(source.Width * scale);
            outputHeight = checked(source.Height * scale);
        }
        catch (OverflowException)
        {
            return false;
        }

        return outputWidth <= VideoToolsV2MaximumOutputWidth
            && outputHeight <= VideoToolsV2MaximumOutputHeight
            && checked((long)outputWidth * outputHeight)
                <= VideoToolsV2MaximumOutputPixelArea
            && HasExactProperties(
                plan,
                "inputWidth",
                "inputHeight",
                "outputWidth",
                "outputHeight",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "videoPtsSha256",
                "semanticRole",
                "backendSetting",
                "backendDeliveryRevision",
                "bitDepth",
                "dynamicRange",
                "sceneCutPolicyRevision",
                "chunkPolicyRevision",
                "preserveAudioPackets",
                "frameInterpolation",
                "implicitCrop")
            && VideoToolsV2ExactInt32(plan, "inputWidth", source.Width)
            && VideoToolsV2ExactInt32(plan, "inputHeight", source.Height)
            && VideoToolsV2ExactInt32(plan, "outputWidth", outputWidth)
            && VideoToolsV2ExactInt32(plan, "outputHeight", outputHeight)
            && VideoToolsV2ExactInt32(plan, "frameCount", source.FrameCount)
            && VideoToolsV2ExactInt32(
                plan,
                "fpsNumerator",
                source.FpsNumerator)
            && VideoToolsV2ExactInt32(
                plan,
                "fpsDenominator",
                source.FpsDenominator)
            && VideoToolsV2ExactInt32(plan, "durationMs", source.DurationMs)
            && VideoToolsExactString(
                plan,
                "videoPtsSha256",
                source.VideoPtsSha256)
            && VideoToolsExactString(plan, "semanticRole", expectedRole)
            && TryGetVideoToolsV2TechnicalText(
                plan,
                "backendSetting",
                128,
                out _)
            && TryGetVideoToolsV2TechnicalText(
                plan,
                "backendDeliveryRevision",
                256,
                out _)
            && VideoToolsV2ExactInt32(plan, "bitDepth", source.BitDepth)
            && VideoToolsExactString(
                plan,
                "dynamicRange",
                source.DynamicRange)
            && TryGetVideoToolsV2TechnicalText(
                plan,
                "sceneCutPolicyRevision",
                256,
                out _)
            && TryGetVideoToolsV2TechnicalText(
                plan,
                "chunkPolicyRevision",
                256,
                out _)
            && VideoToolsExactBoolean(
                plan,
                "preserveAudioPackets",
                true)
            && VideoToolsExactBoolean(plan, "frameInterpolation", false)
            && VideoToolsExactBoolean(plan, "implicitCrop", false);
    }

    private static bool TryReadExactVideoToolsV2FinishDelivery(
        JsonElement delivery,
        VideoToolsV2SourceSnapshot source,
        int outputWidth,
        int outputHeight)
        => HasExactProperties(
                delivery,
                "width",
                "height",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "videoPtsSha256",
                "preserveSourceAudioPackets")
            && VideoToolsV2ExactInt32(delivery, "width", outputWidth)
            && VideoToolsV2ExactInt32(delivery, "height", outputHeight)
            && VideoToolsV2ExactInt32(
                delivery,
                "frameCount",
                source.FrameCount)
            && VideoToolsV2ExactInt32(
                delivery,
                "fpsNumerator",
                source.FpsNumerator)
            && VideoToolsV2ExactInt32(
                delivery,
                "fpsDenominator",
                source.FpsDenominator)
            && VideoToolsV2ExactInt32(
                delivery,
                "durationMs",
                source.DurationMs)
            && VideoToolsExactString(
                delivery,
                "videoPtsSha256",
                source.VideoPtsSha256)
            && VideoToolsExactBoolean(
                delivery,
                "preserveSourceAudioPackets",
                true);

    private static bool VideoToolsV2DurationMatchesFrames(
        int frameCount,
        int fpsNumerator,
        int fpsDenominator,
        double durationMs)
    {
        double exact = frameCount * 1_000d * fpsDenominator / fpsNumerator;
        return Math.Abs(durationMs - exact) <= 1e-9;
    }

    private static bool VideoToolsV2SelectedPtsMatch(
        VideoToolsV2SourceSnapshot source,
        VideoToolsV2EditRequestSnapshot request,
        long startPts,
        long endPtsExclusive)
    {
        try
        {
            long ticksNumerator = checked(
                (long)source.FpsDenominator
                * source.VideoTimeBaseDenominator);
            long ticksDenominator = checked(
                (long)source.FpsNumerator
                * source.VideoTimeBaseNumerator);
            if (ticksNumerator % ticksDenominator != 0)
                return true;
            long ticksPerFrame = ticksNumerator / ticksDenominator;
            long expectedStart = checked(
                source.VideoStartTimestamp
                + checked((long)request.StartFrame * ticksPerFrame));
            long expectedEnd = checked(
                source.VideoStartTimestamp
                + checked((long)request.EndFrameExclusive * ticksPerFrame));
            return startPts == expectedStart && endPtsExclusive == expectedEnd;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            long remainder = left % right;
            left = right;
            right = remainder;
        }
        return Math.Abs(left);
    }

    private static bool VideoToolsV2AllTechnicalFields(
        JsonElement element,
        params string[] propertyNames)
        => propertyNames.All(name => TryGetVideoToolsV2TechnicalText(
            element,
            name,
            256,
            out _));

    private static bool TryGetVideoToolsV2TechnicalText(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value)
    {
        int effectiveMaximumLength = Math.Min(maximumLength, 128);
        if (!TryGetVideoToolsV2BoundedText(
                element,
                propertyName,
                effectiveMaximumLength,
                asciiOnly: true,
                out value)
            || value.Any(static character => character is < '\x21' or > '\x7e'))
        {
            value = "";
            return false;
        }
        return true;
    }

    private static bool TryGetVideoToolsV2Path(
        JsonElement element,
        string propertyName,
        out string value)
    {
        if (!TryGetVideoToolsV2BoundedText(
                element,
                propertyName,
                32_768,
                asciiOnly: false,
                out value)
            || value.Any(static character =>
                character is <= '\x1f' or '\x7f')
            || value.Contains('/', StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value)
            || !string.Equals(
                Path.GetExtension(value),
                ".mp4",
                StringComparison.Ordinal))
        {
            value = "";
            return false;
        }
        try
        {
            return string.Equals(
                Path.GetFullPath(value),
                value,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            value = "";
            return false;
        }
    }

    private static bool VideoToolsV2ManagedOutputPathMatches(
        JsonElement job,
        string jobId,
        VideoToolsV2ReaderSnapshot snapshot,
        string outputPath)
    {
        if (!TryGetSingleVideoToolsString(
                job,
                "sourceSha256",
                out string sourceSha256)
            || !IsLowerHex(sourceSha256, 64)
            || !TryGetSingleVideoToolsString(
                job,
                "presetHash",
                out string presetHash)
            || !IsLowerHex(presetHash, 12))
        {
            return false;
        }

        string sourceBase = Path.GetFileNameWithoutExtension(
            snapshot.SourceCanonicalPath);
        var safeBase = new StringBuilder(Math.Min(sourceBase.Length, 64));
        foreach (char character in sourceBase)
        {
            if (safeBase.Length >= 64)
                break;
            safeBase.Append(character is '<' or '>' or ':' or '"'
                    or '/' or '\\' or '|' or '?' or '*'
                    or <= '\x1f'
                ? '_'
                : character);
        }
        if (safeBase.Length == 0)
            safeBase.Append("image");

        string expectedFilename = string.Join(
            "__",
            jobId,
            safeBase.ToString(),
            sourceSha256[..16],
            snapshot.PresetId,
            snapshot.BackendId,
            presetHash) + ".mp4";
        if (!string.Equals(
                Path.GetFileName(outputPath),
                expectedFilename,
                StringComparison.Ordinal))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(parentPath))
            return false;
        string parentName = Path.GetFileName(parentPath);
        if (string.Equals(parentName, "Videos", StringComparison.Ordinal))
            return true;
        string? grandparentPath = Path.GetDirectoryName(parentPath);
        return !string.IsNullOrWhiteSpace(grandparentPath)
            && string.Equals(
                Path.GetFileName(grandparentPath),
                "Videos",
                StringComparison.Ordinal)
            && DateTime.TryParseExact(
                parentName,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime outputDate)
            && string.Equals(
                outputDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),
                parentName,
                StringComparison.Ordinal);
    }

    private static bool VideoToolsV2WindowsLexicalPathsEqual(
        string left,
        string right)
        => string.Equals(
            NormalizeVideoToolsV2WindowsPathLexically(left),
            NormalizeVideoToolsV2WindowsPathLexically(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVideoToolsV2WindowsPathLexically(
        string path)
    {
        string windowsPath = path.Replace('/', '\\');
        bool unc = windowsPath.StartsWith("\\\\", StringComparison.Ordinal);
        bool rooted = unc
            || windowsPath.StartsWith("\\", StringComparison.Ordinal)
            || windowsPath.Length >= 3
                && char.IsAsciiLetter(windowsPath[0])
                && windowsPath[1] == ':'
                && windowsPath[2] == '\\';
        string[] parts = windowsPath.Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries);
        int protectedSegments = unc
            ? Math.Min(2, parts.Length)
            : parts.Length > 0 && parts[0].Length == 2 && parts[0][1] == ':'
                ? 1
                : 0;
        var normalized = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (normalized.Count > protectedSegments
                    && normalized[^1] != "..")
                {
                    normalized.RemoveAt(normalized.Count - 1);
                }
                else if (!rooted)
                {
                    normalized.Add(part);
                }
                continue;
            }
            normalized.Add(part);
        }
        string prefix = unc
            ? "\\\\"
            : windowsPath.StartsWith("\\", StringComparison.Ordinal)
                ? "\\"
                : "";
        return prefix + string.Join('\\', normalized);
    }

    private static bool TryGetVideoToolsV2BoundedText(
        JsonElement element,
        string propertyName,
        int maximumLength,
        bool asciiOnly,
        out string value)
    {
        value = "";
        if (!TryGetSingleVideoToolsString(
                element,
                propertyName,
                out string parsed)
            || parsed.Length is < 1
            || parsed.Length > maximumLength
            || IsEcmaScriptTrimCharacter(parsed[0])
            || IsEcmaScriptTrimCharacter(parsed[^1])
            || !HasWellFormedVideoToolsUtf16(parsed)
            || parsed.Any(static character =>
                character is <= '\x08'
                    or '\x0b'
                    or '\x0c'
                    or >= '\x0e' and <= '\x1f'
                    or '\x7f')
            || asciiOnly && parsed.Any(static character => character > 0x7f))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool IsEcmaScriptTrimCharacter(char character)
        => character is '\x0009'
            or '\x000a'
            or '\x000b'
            or '\x000c'
            or '\x000d'
            or '\x0020'
            or '\x00a0'
            or '\x1680'
            or >= '\x2000' and <= '\x200a'
            or '\x2028'
            or '\x2029'
            or '\x202f'
            or '\x205f'
            or '\x3000'
            or '\xfeff';

    private static bool TryGetPositiveVideoToolsV2Integer(
        JsonElement element,
        string propertyName,
        out long value)
        => TryGetVideoToolsV2Integer(
            element,
            propertyName,
            1,
            VideoToolsV2MaximumSafeInteger,
            out value);

    private static bool TryGetVideoToolsV2Integer(
        JsonElement element,
        string propertyName,
        long minimum,
        long maximum,
        out long value)
    {
        value = 0;
        if (!HasSingleProperty(element, propertyName)
            || !element.TryGetProperty(
                propertyName,
                out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !TryGetLosslessVideoToolsV2Number(property, out double numeric)
            || Math.Truncate(numeric) != numeric
            || numeric < minimum
            || numeric > maximum
            || numeric < -VideoToolsV2MaximumSafeInteger
            || numeric > VideoToolsV2MaximumSafeInteger)
        {
            return false;
        }
        value = checked((long)numeric);
        return true;
    }

    private static bool TryGetBoundedVideoToolsV2Int(
        JsonElement element,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        if (!TryGetVideoToolsV2Integer(
                element,
                propertyName,
                minimum,
                maximum,
                out long parsed))
        {
            return false;
        }
        value = (int)parsed;
        return true;
    }

    private static bool TryGetVideoToolsV2Int32(
        JsonElement element,
        string propertyName,
        out int value)
        => TryGetBoundedVideoToolsV2Int(
            element,
            propertyName,
            int.MinValue,
            int.MaxValue,
            out value);

    private static bool TryGetVideoToolsV2Boolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!HasSingleProperty(element, propertyName)
            || !element.TryGetProperty(
                propertyName,
                out JsonElement property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool VideoToolsV2ExactInt32(
        JsonElement element,
        string propertyName,
        int expected)
        => TryGetVideoToolsV2Integer(
            element,
            propertyName,
            expected,
            expected,
            out _);

    private static bool TryGetFiniteVideoToolsV2Number(
        JsonElement element,
        string propertyName,
        double minimum,
        double maximum,
        out double value)
    {
        value = 0;
        return HasSingleProperty(element, propertyName)
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && TryGetLosslessVideoToolsV2Number(property, out value)
            && value >= minimum
            && value <= maximum;
    }

    private static bool TryGetLosslessVideoToolsV2Number(
        JsonElement property,
        out double value)
    {
        value = 0;
        string token = property.GetRawText();
        if (token.Length is < 1 or > 256
            || !property.TryGetDouble(out value)
            || !double.IsFinite(value)
            || !TryNormalizeVideoToolsV2Decimal(
                token,
                out bool tokenNegative,
                out string tokenDigits,
                out long tokenExponent))
        {
            return false;
        }
        if (value == 0 && tokenDigits != "0")
            return false;
        if (BitConverter.DoubleToInt64Bits(value) == long.MinValue)
            return tokenDigits == "0";
        string roundTripped = value.ToString(
            "R",
            CultureInfo.InvariantCulture);
        return TryNormalizeVideoToolsV2Decimal(
                roundTripped,
                out bool roundTripNegative,
                out string roundTripDigits,
                out long roundTripExponent)
            && tokenNegative == roundTripNegative
            && string.Equals(
                tokenDigits,
                roundTripDigits,
                StringComparison.Ordinal)
            && tokenExponent == roundTripExponent;
    }

    private static bool TryNormalizeVideoToolsV2Decimal(
        string token,
        out bool negative,
        out string digits,
        out long exponent)
    {
        negative = token.StartsWith("-", StringComparison.Ordinal);
        string unsigned = negative ? token[1..] : token;
        int exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0
            ? unsigned
            : unsigned[..exponentIndex];
        string exponentText = exponentIndex < 0
            ? "0"
            : unsigned[(exponentIndex + 1)..];
        if (!long.TryParse(
                exponentText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long explicitExponent))
        {
            digits = "";
            exponent = 0;
            return false;
        }
        int decimalIndex = mantissa.IndexOf('.');
        int decimalPlaces = decimalIndex < 0
            ? 0
            : mantissa.Length - decimalIndex - 1;
        digits = mantissa.Replace(".", "", StringComparison.Ordinal)
            .TrimStart('0');
        if (digits.Length == 0)
        {
            negative = false;
            digits = "0";
            exponent = 0;
            return true;
        }
        try
        {
            exponent = checked(explicitExponent - decimalPlaces);
            while (digits.EndsWith('0'))
            {
                digits = digits[..^1];
                exponent = checked(exponent + 1);
            }
            return digits.All(char.IsAsciiDigit);
        }
        catch (OverflowException)
        {
            digits = "";
            exponent = 0;
            return false;
        }
    }

    private static bool VideoToolsV2HasAnyProperty(
        JsonElement element,
        string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Any(property =>
                property.NameEquals(propertyName));

    private static string BuildVideoToolsV2RequestDetails(
        VideoToolsV2ReaderSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.Kind == "edit"
            ? "処理: AI動画編集"
            : "処理: AI動画高画質化");
        builder.AppendLine("Protocol: Video Tools v2（読取専用）");
        builder.AppendLine(snapshot.SourceKind == "managed-video-job"
            ? $"入力依存: 管理動画 Job {snapshot.SourceVideoJobId}"
            : "入力依存: 外部動画のJob所有ステージングコピー");
        builder.AppendLine(
            $"入力: {snapshot.SourceWidth}×{snapshot.SourceHeight} · "
            + $"{snapshot.SourceFrameCount} frame · "
            + $"{snapshot.SourceFpsNumerator}/{snapshot.SourceFpsDenominator} fps · "
            + $"{snapshot.SourceDurationMs} ms");
        if (snapshot.Kind == "edit")
        {
            builder.AppendLine(
                $"選択区間: [{snapshot.SelectionStartFrame}, "
                + $"{snapshot.SelectionEndFrameExclusive})");
            builder.AppendLine($"指示: {snapshot.InstructionJa}");
            builder.AppendLine($"変換要約: {snapshot.CompiledSummaryJa}");
            builder.AppendLine(
                $"音声: {snapshot.AudioPolicy} · STEP {snapshot.Steps} · "
                + $"変更強度 {snapshot.Strength} · "
                + $"最大Pixel面積 {snapshot.MaximumPixelArea}");
            builder.AppendLine(
                $"出力: 非破壊child clip · {snapshot.OutputWidth}×"
                + $"{snapshot.OutputHeight} · {snapshot.OutputFrameCount} frame");
        }
        else
        {
            builder.AppendLine(
                $"モード: {snapshot.FinishMode} · {snapshot.FinishScale}x");
            builder.AppendLine(
                $"出力: {snapshot.OutputWidth}×{snapshot.OutputHeight} · "
                + $"{snapshot.OutputFrameCount} frame · fps/全尺/元音声を維持");
        }
        builder.Append(
            "保護: cancel/retry/remove/delete/reorder/saved rerunは無効です。"
            + "元動画と入力依存は変更しません。");
        return builder.ToString();
    }

    public static bool TryReadVideoToolsV2WorkspacePresentationForSmoke(
        JsonElement job,
        out string readerKind,
        out string presetSummary,
        out string operationLabel,
        out string detailText,
        out string requestDetails,
        out string videoKindFilterKey,
        out bool supportedMutation,
        out bool canUseOutput,
        out string[] visibleActionKinds)
    {
        readerKind = presetSummary = operationLabel = detailText = "";
        requestDetails = videoKindFilterKey = "";
        supportedMutation = canUseOutput = false;
        visibleActionKinds = [];
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            static _ => false);
        if (view?.VideoToolsV2Snapshot is null)
            return false;
        readerKind = view.VideoToolsKind ?? "";
        presetSummary = view.PresetSummary;
        operationLabel = view.OperationLabel;
        detailText = view.DetailText;
        requestDetails = view.RequestDetailsText;
        videoKindFilterKey = view.VideoKindFilterKey ?? "";
        supportedMutation = view.IsSupportedMutationOperation;
        canUseOutput = view.CanUseOutput;
        visibleActionKinds =
            new[] { view.Action1, view.Action2, view.Action3, view.Action4,
                view.Action5, view.DangerAction }
            .Where(static action => action.Visible)
            .Select(static action => action.Kind)
            .ToArray();
        return true;
    }

    public static string? ReadEnhancementVideoKindForSmoke(JsonElement job)
    {
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0,
            static _ => false);
        return view?.VideoKindFilterKey;
    }
}

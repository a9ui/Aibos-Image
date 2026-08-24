using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal sealed record VideoTrimV1ReaderSnapshot(
    string SourceKind,
    string? SourceVideoJobId,
    string SourceCanonicalPath,
    string? SourceStagingCanonicalPath,
    long SourceSize,
    double SourceMtimeMs,
    int SourceWidth,
    int SourceHeight,
    int SourceFrameCount,
    int SourceFpsNumerator,
    int SourceFpsDenominator,
    int SourceDurationMs,
    int SourceAudioStreamCount,
    int SelectionStartFrame,
    int SelectionEndFrameExclusive,
    string AudioPolicy,
    int OutputWidth,
    int OutputHeight,
    int OutputFrameCount,
    int OutputFpsNumerator,
    int OutputFpsDenominator,
    double OutputDurationMs,
    string DeliveryAudioKind,
    string PresetId,
    string AdapterId);

public partial class MainWindow
{
    private const string VideoTrimV1PresetId = "aibos-video-trim-v1";
    private const string VideoTrimV1AdapterId = "ffmpeg-video-trim-v1";

    private readonly record struct VideoTrimV1SourceSnapshot(
        string Kind,
        string? ProducerJobId,
        string CanonicalPath,
        string? StagingCanonicalPath,
        string ExecutionCanonicalPath,
        long ExecutionSize,
        double ExecutionMtimeMs,
        string ExecutionSha256,
        long Size,
        double MtimeMs,
        int Width,
        int Height,
        int FrameCount,
        int FpsNumerator,
        int FpsDenominator,
        int DurationMs,
        long DurationNumerator,
        long DurationDenominator,
        int AudioStreamCount,
        int TimeBaseNumerator,
        int TimeBaseDenominator,
        long VideoStartTimestamp,
        string VideoPtsSha256,
        string ProbeDigest,
        string SourceIdentityDigest);

    private static bool ClaimsVideoTrimV1WorkspaceSnapshot(JsonElement job)
        => job.ValueKind == JsonValueKind.Object
            && job.TryGetProperty("videoTrim", out _);

    private static bool TryReadVideoTrimV1WorkspaceSnapshot(
        JsonElement job,
        out VideoTrimV1ReaderSnapshot snapshot)
    {
        snapshot = null!;
        if (!ClaimsVideoTrimV1WorkspaceSnapshot(job)
            || !TrimV1ExactString(job, "operation", "video")
            || !TrimV1ExactString(job, "mediaKind", "video")
            || !TrimV1BoundedText(job, "sourceId", 32_768, out _)
            || !TrimV1ExactString(job, "presetId", VideoTrimV1PresetId)
            || !TrimV1ExactString(job, "adapterId", VideoTrimV1AdapterId)
            || !TrimV1SingleString(job, "presetHash", out string presetHash)
            || !TrimV1LowerHex(presetHash, 12)
            || !TrimV1SingleString(job, "status", out string status)
            || status is not ("queued" or "running" or "succeeded"
                or "failed" or "canceled" or "deleted")
            || !TrimV1Int32(job, "progress", out int progress)
            || !TrimV1Boolean(job, "cancelRequested", out bool cancelRequested)
            || !TryReadVideoTrimV1StateEnvelope(
                job,
                status,
                progress,
                cancelRequested)
            || !job.TryGetProperty("videoTrim", out JsonElement trim)
            || trim.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                trim,
                "schemaVersion",
                "protocol",
                "presetId",
                "adapterId",
                "receiptSetSha256",
                "source",
                "requested",
                "plan",
                "delivery")
            || !TrimV1ExactInt32(trim, "schemaVersion", 1)
            || !TrimV1ExactString(
                trim,
                "protocol",
                VideoTrimV1Contract.Protocol)
            || !TrimV1ExactString(trim, "presetId", VideoTrimV1PresetId)
            || !TrimV1ExactString(trim, "adapterId", VideoTrimV1AdapterId)
            || !TrimV1SingleString(
                trim,
                "receiptSetSha256",
                out string receiptSetSha)
            || !TrimV1LowerHex(receiptSetSha, 64)
            || !string.Equals(
                presetHash,
                HashStableJson(trim)[..12],
                StringComparison.Ordinal)
            || !trim.TryGetProperty("source", out JsonElement sourceElement)
            || !TryReadVideoTrimV1Source(
                sourceElement,
                out VideoTrimV1SourceSnapshot source)
            || !VideoTrimV1JobSourceMatches(job, source)
            || !trim.TryGetProperty("requested", out JsonElement requested)
            || !TryReadVideoTrimV1Requested(
                requested,
                source,
                out int startFrame,
                out int endFrameExclusive,
                out string audioPolicy)
            || !trim.TryGetProperty("plan", out JsonElement plan)
            || !TryReadVideoTrimV1Plan(
                plan,
                source,
                startFrame,
                endFrameExclusive,
                audioPolicy)
            || !trim.TryGetProperty("delivery", out JsonElement delivery)
            || !TryReadVideoTrimV1Delivery(
                delivery,
                source,
                startFrame,
                endFrameExclusive,
                audioPolicy,
                out int outputFrames,
                out double outputDurationMs,
                out string deliveryAudioKind))
        {
            return false;
        }

        snapshot = new(
            source.Kind,
            source.ProducerJobId,
            source.CanonicalPath,
            source.StagingCanonicalPath,
            source.Size,
            source.MtimeMs,
            source.Width,
            source.Height,
            source.FrameCount,
            source.FpsNumerator,
            source.FpsDenominator,
            source.DurationMs,
            source.AudioStreamCount,
            startFrame,
            endFrameExclusive,
            audioPolicy,
            source.Width,
            source.Height,
            outputFrames,
            source.FpsNumerator,
            source.FpsDenominator,
            outputDurationMs,
            deliveryAudioKind,
            VideoTrimV1PresetId,
            VideoTrimV1AdapterId);
        return true;
    }

    private static bool TryReadVideoTrimV1StateEnvelope(
        JsonElement job,
        string status,
        int progress,
        bool cancelRequested)
    {
        if (!TrimV1Timestamp(job, "createdAt", out DateTimeOffset created)
            || !TrimV1Timestamp(job, "updatedAt", out DateTimeOffset updated)
            || updated < created)
        {
            return false;
        }
        bool hasQueue = job.TryGetProperty("queueOrder", out JsonElement queue);
        bool hasStarted = job.TryGetProperty("startedAt", out _);
        bool hasFinished = job.TryGetProperty("finishedAt", out _);
        bool hasOutput = job.TryGetProperty("outputPath", out _)
            || job.TryGetProperty("outputSha256", out _)
            || job.TryGetProperty("outputBytes", out _);
        bool hasError = job.TryGetProperty("errorCode", out _)
            || job.TryGetProperty("errorMessage", out _);
        if (status == "queued")
        {
            return progress == 0
                && !cancelRequested
                && hasQueue
                && queue.ValueKind == JsonValueKind.Number
                && queue.TryGetInt32(out int queueOrder)
                && queueOrder >= 0
                && !hasStarted
                && !hasFinished
                && !hasOutput
                && !hasError
                && !HasVideoTrimV1AttemptWorkerFields(job);
        }
        if (!TrimV1Timestamp(job, "startedAt", out DateTimeOffset started)
            || started < created)
        {
            return false;
        }
        if (status == "running")
        {
            return progress is >= 1 and <= 99
                && !cancelRequested
                && !hasQueue
                && !hasFinished
                && !hasOutput
                && !hasError
                && TryReadVideoTrimV1RunningLifecycle(
                    job,
                    started,
                    updated);
        }
        if (!TrimV1Timestamp(job, "finishedAt", out DateTimeOffset finished)
            || finished < started
            || updated < finished
            || hasQueue
            || HasVideoTrimV1AttemptWorkerFields(job))
        {
            return false;
        }
        if (status == "succeeded")
        {
            return progress == 100
                && !cancelRequested
                && !hasError
                && TrimV1Path(job, "outputPath", out _)
                && TrimV1SingleString(
                    job,
                    "outputSha256",
                    out string outputSha)
                && TrimV1LowerHex(outputSha, 64)
                && TrimV1Int64(job, "outputBytes", out long outputBytes)
                && outputBytes is > 0 and <= VideoTrimV1Contract.MaximumSourceBytes;
        }
        if (status == "failed")
        {
            return progress is >= 0 and <= 99
                && !cancelRequested
                && !hasOutput
                && TrimV1SafeTechnical(job, "errorCode", 128, out _)
                && TrimV1BoundedText(job, "errorMessage", 2_000, out _);
        }
        if (status == "deleted")
        {
            return progress == 100
                && !cancelRequested
                && !hasOutput
                && !hasError;
        }
        return status == "canceled"
            && cancelRequested
            && progress is >= 0 and <= 99
            && !hasOutput
            && !hasError;
    }

    private static bool HasVideoTrimV1AttemptWorkerFields(JsonElement job)
        => job.TryGetProperty("runId", out _)
            || job.TryGetProperty("workerInstanceId", out _)
            || job.TryGetProperty("lastHeartbeatAt", out _)
            || job.TryGetProperty("lastProgressAt", out _)
            || job.TryGetProperty("externalPromptId", out _)
            || job.TryGetProperty("externalProcessId", out _)
            || job.TryGetProperty("diagnostics", out _);

    private static bool TryReadVideoTrimV1RunningLifecycle(
        JsonElement job,
        DateTimeOffset started,
        DateTimeOffset updated)
    {
        foreach (string name in new[]
        {
            "runId", "workerInstanceId", "lastHeartbeatAt", "lastProgressAt",
            "externalPromptId", "externalProcessId", "diagnostics",
        })
        {
            if (job.EnumerateObject().Count(property =>
                    string.Equals(property.Name, name, StringComparison.Ordinal)) > 1)
            {
                return false;
            }
        }
        if (job.TryGetProperty("runId", out _)
            && !TrimV1BoundedText(job, "runId", 128, out _))
        {
            return false;
        }
        if (job.TryGetProperty("workerInstanceId", out _)
            && !TrimV1BoundedText(job, "workerInstanceId", 128, out _))
        {
            return false;
        }
        if (job.TryGetProperty("externalPromptId", out _)
            && !TrimV1BoundedText(job, "externalPromptId", 512, out _))
        {
            return false;
        }
        if (job.TryGetProperty("externalProcessId", out _)
            && (!TrimV1Int32(job, "externalProcessId", out int processId)
                || processId <= 0))
        {
            return false;
        }
        foreach (string name in new[] { "lastHeartbeatAt", "lastProgressAt" })
        {
            if (job.TryGetProperty(name, out _)
                && (!TrimV1Timestamp(job, name, out DateTimeOffset timestamp)
                    || timestamp < started
                    || timestamp > updated))
            {
                return false;
            }
        }
        return !job.TryGetProperty("diagnostics", out JsonElement diagnostics)
            || diagnostics.ValueKind == JsonValueKind.Object
                && VideoTrimV1StableJsonLengthWithin(diagnostics, 32_768);
    }

    private static bool VideoTrimV1StableJsonLengthWithin(
        JsonElement value,
        int maximumLength)
    {
        try
        {
            var builder = new StringBuilder();
            AppendStableJson(builder, value);
            return builder.Length <= maximumLength;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadVideoTrimV1Source(
        JsonElement value,
        out VideoTrimV1SourceSnapshot source)
    {
        source = default;
        if (!TrimV1SingleString(value, "kind", out string kind))
            return false;

        string? producerJobId = null;
        string canonicalPath;
        string? stagingPath = null;
        string executionPath;
        long executionSize;
        double executionMtimeMs;
        string executionSha;
        long size;
        double mtimeMs;
        JsonElement probe;
        string probeDigest;
        string sourceIdentityDigest;
        if (kind == "managed-video-job")
        {
            if (!HasExactProperties(
                    value,
                    "kind",
                    "producerJobId",
                    "canonicalPath",
                    "signature",
                    "sha256",
                    "probe",
                    "probeDigest",
                    "sourceIdentityDigest",
                    "dependencyDigest")
                || !TrimV1SafeTechnical(
                    value,
                    "producerJobId",
                    512,
                    out producerJobId)
                || !TrimV1Path(value, "canonicalPath", out canonicalPath)
                || !value.TryGetProperty("signature", out JsonElement signature)
                || !TryReadVideoTrimV1Signature(signature, out size, out mtimeMs)
                || !TrimV1SingleString(value, "sha256", out executionSha)
                || !TrimV1LowerHex(executionSha, 64)
                || !TrimV1Sha(value, "dependencyDigest")
                || !value.TryGetProperty("probe", out probe)
                || !TrimV1SingleString(
                    value,
                    "probeDigest",
                    out probeDigest)
                || !TrimV1SingleString(
                    value,
                    "sourceIdentityDigest",
                    out sourceIdentityDigest))
            {
                return false;
            }
            executionPath = canonicalPath;
            executionSize = size;
            executionMtimeMs = mtimeMs;
        }
        else if (kind == "staged-displayed-file")
        {
            if (!HasExactProperties(
                    value,
                    "kind",
                    "originalCanonicalPath",
                    "originalSignature",
                    "originalSha256",
                    "stagingCanonicalPath",
                    "stagingSignature",
                    "stagingSha256",
                    "probe",
                    "probeDigest",
                    "sourceIdentityDigest",
                    "stagingOwnershipDigest")
                || !TrimV1Path(
                    value,
                    "originalCanonicalPath",
                    out canonicalPath)
                || !TrimV1Path(
                    value,
                    "stagingCanonicalPath",
                    out stagingPath)
                || !value.TryGetProperty(
                    "originalSignature",
                    out JsonElement originalSignature)
                || !TryReadVideoTrimV1Signature(
                    originalSignature,
                    out size,
                    out mtimeMs)
                || !value.TryGetProperty(
                    "stagingSignature",
                    out JsonElement stagingSignature)
                || !TryReadVideoTrimV1Signature(
                    stagingSignature,
                    out long stagingSize,
                    out double stagingMtimeMs)
                || stagingSize != size
                || !TrimV1Sha(value, "originalSha256")
                || !TrimV1SingleString(
                    value,
                    "stagingSha256",
                    out executionSha)
                || !TrimV1LowerHex(executionSha, 64)
                || !TrimV1Sha(value, "stagingOwnershipDigest")
                || !value.TryGetProperty("probe", out probe)
                || !TrimV1SingleString(
                    value,
                    "probeDigest",
                    out probeDigest)
                || !TrimV1SingleString(
                    value,
                    "sourceIdentityDigest",
                    out sourceIdentityDigest))
            {
                return false;
            }
            executionPath = stagingPath;
            executionSize = stagingSize;
            executionMtimeMs = stagingMtimeMs;
        }
        else
        {
            return false;
        }

        if (!TrimV1LowerHex(probeDigest, 64)
            || !TrimV1LowerHex(sourceIdentityDigest, 64)
            || !TryReadVideoTrimV1Probe(
                probe,
                out int width,
                out int height,
                out int frameCount,
                out int fpsNumerator,
                out int fpsDenominator,
                out int durationMs,
                out long durationNumerator,
                out long durationDenominator,
                out int audioStreams,
                out int timeBaseNumerator,
                out int timeBaseDenominator,
                out long startTimestamp,
                out string ptsSha))
        {
            return false;
        }
        source = new(
            kind,
            producerJobId,
            canonicalPath,
            stagingPath,
            executionPath,
            executionSize,
            executionMtimeMs,
            executionSha,
            size,
            mtimeMs,
            width,
            height,
            frameCount,
            fpsNumerator,
            fpsDenominator,
            durationMs,
            durationNumerator,
            durationDenominator,
            audioStreams,
            timeBaseNumerator,
            timeBaseDenominator,
            startTimestamp,
            ptsSha,
            probeDigest,
            sourceIdentityDigest);
        return true;
    }

    private static bool TryReadVideoTrimV1Signature(
        JsonElement value,
        out long size,
        out double mtimeMs)
    {
        size = 0;
        mtimeMs = 0;
        return HasExactProperties(value, "size", "mtimeMs")
            && TrimV1Int64(value, "size", out size)
            && size is > 0 and <= VideoTrimV1Contract.MaximumSourceBytes
            && TrimV1FiniteNumber(value, "mtimeMs", out mtimeMs)
            && Math.Abs(mtimeMs) <= 9_007_199_254_740_991d;
    }

    private static bool TryReadVideoTrimV1Probe(
        JsonElement value,
        out int width,
        out int height,
        out int frameCount,
        out int fpsNumerator,
        out int fpsDenominator,
        out int durationMs,
        out long durationNumerator,
        out long durationDenominator,
        out int audioStreams,
        out int timeBaseNumerator,
        out int timeBaseDenominator,
        out long startTimestamp,
        out string ptsSha)
    {
        width = height = frameCount = fpsNumerator = durationMs = 0;
        fpsDenominator = 1;
        durationNumerator = durationDenominator = 0;
        audioStreams = timeBaseNumerator = timeBaseDenominator = 0;
        startTimestamp = 0;
        ptsSha = "";
        return HasExactProperties(
                value,
                "container",
                "videoCodec",
                "pixelFormat",
                "bitDepth",
                "dynamicRange",
                "width",
                "height",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "durationNumerator",
                "durationDenominator",
                "videoStreamCount",
                "audioStreamCount",
                "extraStreamCount",
                "videoTimeBaseNumerator",
                "videoTimeBaseDenominator",
                "videoStartTimestamp",
                "videoPtsSha256")
            && TrimV1ExactString(value, "container", "mp4")
            && TrimV1ExactString(value, "videoCodec", "h264")
            && TrimV1ExactString(value, "pixelFormat", "yuv420p")
            && TrimV1ExactInt32(value, "bitDepth", 8)
            && TrimV1ExactString(value, "dynamicRange", "SDR")
            && TrimV1Int32(value, "width", out width)
            && width is > 0 and <= VideoTrimV1Contract.MaximumWidth
            && TrimV1Int32(value, "height", out height)
            && height is > 0 and <= VideoTrimV1Contract.MaximumHeight
            && (long)width * height <= VideoTrimV1Contract.MaximumPixelArea
            && TrimV1Int32(value, "frameCount", out frameCount)
            && frameCount is > 0 and <= VideoTrimV1Contract.MaximumFrames
            && TrimV1Int32(value, "fpsNumerator", out fpsNumerator)
            && fpsNumerator is 24 or 30 or 60
            && TrimV1Int32(value, "fpsDenominator", out fpsDenominator)
            && fpsDenominator == 1
            && TrimV1Int32(value, "durationMs", out durationMs)
            && durationMs is > 0 and <= VideoTrimV1Contract.MaximumDurationMs
            && TrimV1Int64(
                value,
                "durationNumerator",
                out durationNumerator)
            && durationNumerator > 0
            && TrimV1Int64(
                value,
                "durationDenominator",
                out durationDenominator)
            && durationDenominator > 0
            && TrimV1DurationMatchesFrames(
                frameCount,
                fpsNumerator,
                fpsDenominator,
                durationNumerator,
                durationDenominator)
            && TrimV1ExactInt32(value, "videoStreamCount", 1)
            && TrimV1Int32(value, "audioStreamCount", out audioStreams)
            && audioStreams is 0 or 1
            && TrimV1ExactInt32(value, "extraStreamCount", 0)
            && TrimV1Int32(
                value,
                "videoTimeBaseNumerator",
                out timeBaseNumerator)
            && timeBaseNumerator > 0
            && TrimV1Int32(
                value,
                "videoTimeBaseDenominator",
                out timeBaseDenominator)
            && timeBaseDenominator > 0
            && TrimV1Int64(
                value,
                "videoStartTimestamp",
                out startTimestamp)
            && TrimV1SingleString(
                value,
                "videoPtsSha256",
                out ptsSha)
            && TrimV1LowerHex(ptsSha, 64);
    }

    private static bool VideoTrimV1JobSourceMatches(
        JsonElement job,
        VideoTrimV1SourceSnapshot source)
    {
        bool hasPath = job.TryGetProperty("sourcePath", out _);
        bool hasSignature = job.TryGetProperty("sourceSignature", out _);
        bool hasSha = job.TryGetProperty("sourceSha256", out _);
        bool hasPrivateExecutionEnvelope = hasPath || hasSignature || hasSha;
        if (hasPrivateExecutionEnvelope
            && (!hasPath || !hasSignature || !hasSha
                || !TrimV1Path(job, "sourcePath", out string sourcePath)
                || !string.Equals(
                    sourcePath,
                    source.ExecutionCanonicalPath,
                    StringComparison.Ordinal)
                || !job.TryGetProperty(
                    "sourceSignature",
                    out JsonElement sourceSignature)
                || !TryReadVideoTrimV1Signature(
                    sourceSignature,
                    out long sourceSize,
                    out double sourceMtimeMs)
                || sourceSize != source.ExecutionSize
                || sourceMtimeMs != source.ExecutionMtimeMs
                || !TrimV1SingleString(
                    job,
                    "sourceSha256",
                    out string sourceSha)
                || !string.Equals(
                    sourceSha,
                    source.ExecutionSha256,
                    StringComparison.Ordinal)))
        {
            return false;
        }
        if (source.Kind == "managed-video-job")
        {
            return source.ProducerJobId is string producer
                && TrimV1SafeTechnical(
                    job,
                    "sourceVideoJobId",
                    512,
                    out string sourceVideoJobId)
                && string.Equals(
                    producer,
                    sourceVideoJobId,
                    StringComparison.Ordinal);
        }
        return !job.TryGetProperty("sourceVideoJobId", out _)
            && source.ProducerJobId is null
            && source.StagingCanonicalPath is not null
            && TrimV1BoundedText(job, "sourceId", 32_768, out string sourceId)
            && string.Equals(
                sourceId,
                source.CanonicalPath,
                StringComparison.Ordinal);
    }

    private static bool TryReadVideoTrimV1Requested(
        JsonElement value,
        VideoTrimV1SourceSnapshot source,
        out int start,
        out int end,
        out string audioPolicy)
    {
        start = end = 0;
        audioPolicy = "";
        if (!HasExactProperties(
                value,
                "schemaVersion",
                "source",
                "selection",
                "audioPolicy")
            || !TrimV1ExactInt32(value, "schemaVersion", 1)
            || !value.TryGetProperty("source", out JsonElement selector)
            || !TryReadVideoTrimV1RequestedSource(selector, source)
            || !value.TryGetProperty("selection", out JsonElement selection)
            || !TrimV1Selection(selection, source.FrameCount, out start, out end)
            || !TrimV1SingleString(value, "audioPolicy", out audioPolicy)
            || audioPolicy is not ("preserve" or "mute"))
        {
            return false;
        }
        return true;
    }

    private static bool TryReadVideoTrimV1RequestedSource(
        JsonElement selector,
        VideoTrimV1SourceSnapshot source)
    {
        if (source.Kind == "managed-video-job")
        {
            return HasExactProperties(
                    selector,
                    "kind",
                    "sourceVideoJobId")
                && TrimV1ExactString(
                    selector,
                    "kind",
                    "managed-video-job")
                && TrimV1SingleString(
                    selector,
                    "sourceVideoJobId",
                    out string producer)
                && string.Equals(
                    producer,
                    source.ProducerJobId,
                    StringComparison.Ordinal);
        }
        return HasExactProperties(selector, "kind")
            && TrimV1ExactString(selector, "kind", "displayed-file");
    }

    private static bool TryReadVideoTrimV1Plan(
        JsonElement value,
        VideoTrimV1SourceSnapshot source,
        int expectedStart,
        int expectedEnd,
        string audioPolicy)
    {
        if (!HasExactProperties(
                value,
                "revision",
                "selection",
                "startPts",
                "endPtsExclusive",
                "selectedFrameCount",
                "durationNumerator",
                "durationDenominator",
                "audioPlan")
            || !TrimV1ExactString(
                value,
                "revision",
                VideoTrimV1Contract.PlanRevision)
            || !value.TryGetProperty("selection", out JsonElement selection)
            || !TrimV1Selection(
                selection,
                source.FrameCount,
                out int start,
                out int end)
            || start != expectedStart
            || end != expectedEnd
            || !TrimV1Int64(value, "startPts", out long startPts)
            || !TrimV1Int64(
                value,
                "endPtsExclusive",
                out long endPtsExclusive)
            || !TrimV1FrameToPts(source, start, out long expectedStartPts)
            || !TrimV1FrameToPts(source, end, out long expectedEndPts)
            || startPts != expectedStartPts
            || endPtsExclusive != expectedEndPts
            || !TrimV1ExactInt32(
                value,
                "selectedFrameCount",
                end - start)
            || !TrimV1Int64(
                value,
                "durationNumerator",
                out long durationNumerator)
            || !TrimV1Int64(
                value,
                "durationDenominator",
                out long durationDenominator)
            || !TrimV1DurationMatchesFrames(
                end - start,
                source.FpsNumerator,
                source.FpsDenominator,
                durationNumerator,
                durationDenominator)
            || !value.TryGetProperty("audioPlan", out JsonElement audioPlan))
        {
            return false;
        }
        return audioPolicy == "preserve"
            ? HasExactProperties(audioPlan, "kind", "policy", "codec")
                && TrimV1ExactString(
                    audioPlan,
                    "kind",
                    "reencode-selected-interval")
                && TrimV1ExactString(audioPlan, "policy", "preserve")
                && TrimV1ExactString(audioPlan, "codec", "aac")
            : HasExactProperties(audioPlan, "kind", "policy")
                && TrimV1ExactString(audioPlan, "kind", "mute")
                && TrimV1ExactString(audioPlan, "policy", "mute");
    }

    private static bool TryReadVideoTrimV1Delivery(
        JsonElement value,
        VideoTrimV1SourceSnapshot source,
        int start,
        int end,
        string audioPolicy,
        out int outputFrames,
        out double outputDurationMs,
        out string audioKind)
    {
        outputFrames = 0;
        outputDurationMs = 0;
        audioKind = "";
        int selectedFrames = end - start;
        if (!HasExactProperties(
                value,
                "revision",
                "container",
                "videoCodec",
                "pixelFormat",
                "bitDepth",
                "dynamicRange",
                "width",
                "height",
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "timeBaseNumerator",
                "timeBaseDenominator",
                "durationNumerator",
                "durationDenominator",
                "firstRelativePts",
                "lastRelativePts",
                "videoPtsSha256",
                "audio")
            || !TrimV1ExactString(
                value,
                "revision",
                "aibos-video-trim-delivery-v1")
            || !TrimV1ExactString(value, "container", "mp4")
            || !TrimV1ExactString(value, "videoCodec", "h264")
            || !TrimV1ExactString(value, "pixelFormat", "yuv420p")
            || !TrimV1ExactInt32(value, "bitDepth", 8)
            || !TrimV1ExactString(value, "dynamicRange", "SDR")
            || !TrimV1ExactInt32(value, "width", source.Width)
            || !TrimV1ExactInt32(value, "height", source.Height)
            || !TrimV1ExactInt32(value, "frameCount", selectedFrames)
            || !TrimV1ExactInt32(
                value,
                "fpsNumerator",
                source.FpsNumerator)
            || !TrimV1ExactInt32(
                value,
                "fpsDenominator",
                source.FpsDenominator)
            || !TrimV1Int32(
                value,
                "timeBaseNumerator",
                out int timeBaseNumerator)
            || !TrimV1Int32(
                value,
                "timeBaseDenominator",
                out int timeBaseDenominator)
            || timeBaseNumerator <= 0
            || timeBaseDenominator <= 0
            || !TrimV1Int64(
                value,
                "durationNumerator",
                out long durationNumerator)
            || !TrimV1Int64(
                value,
                "durationDenominator",
                out long durationDenominator)
            || !TrimV1DurationMatchesFrames(
                selectedFrames,
                source.FpsNumerator,
                source.FpsDenominator,
                durationNumerator,
                durationDenominator)
            || !TrimV1ExactInt64(value, "firstRelativePts", 0)
            || !TrimV1ExactInt64(
                value,
                "lastRelativePts",
                selectedFrames - 1)
            || !TrimV1Sha(value, "videoPtsSha256")
            || !value.TryGetProperty("audio", out JsonElement audio)
            || !TryReadVideoTrimV1DeliveryAudio(
                audio,
                source.AudioStreamCount,
                audioPolicy,
                out audioKind))
        {
            return false;
        }
        outputFrames = selectedFrames;
        outputDurationMs =
            (double)durationNumerator / durationDenominator * 1_000d;
        return double.IsFinite(outputDurationMs)
            && outputDurationMs is > 0 and <= VideoTrimV1Contract.MaximumDurationMs;
    }

    private static bool TryReadVideoTrimV1DeliveryAudio(
        JsonElement value,
        int sourceAudioStreams,
        string audioPolicy,
        out string kind)
    {
        kind = "";
        if (!TrimV1SingleString(value, "kind", out kind))
            return false;
        if (audioPolicy == "mute")
        {
            return HasExactProperties(
                    value,
                    "kind",
                    "policy",
                    "audioStreamCount")
                && kind == "muted"
                && TrimV1ExactString(value, "policy", "mute")
                && TrimV1ExactInt32(value, "audioStreamCount", 0);
        }
        if (sourceAudioStreams == 0)
        {
            return HasExactProperties(
                    value,
                    "kind",
                    "policy",
                    "audioStreamCount",
                    "packetBitIdentityClaimed",
                    "sampleExactClaimed")
                && kind == "no-source-audio"
                && TrimV1ExactString(value, "policy", "preserve")
                && TrimV1ExactInt32(value, "audioStreamCount", 0)
                && TrimV1ExactBoolean(
                    value,
                    "packetBitIdentityClaimed",
                    false)
                && TrimV1ExactBoolean(value, "sampleExactClaimed", false);
        }
        return HasExactProperties(
                value,
                "kind",
                "policy",
                "audioStreamCount",
                "codec",
                "packetBitIdentityClaimed",
                "sampleExactClaimed")
            && kind == "aac-selected-interval"
            && TrimV1ExactString(value, "policy", "preserve")
            && TrimV1ExactInt32(value, "audioStreamCount", 1)
            && TrimV1ExactString(value, "codec", "aac")
            && TrimV1ExactBoolean(
                value,
                "packetBitIdentityClaimed",
                false)
            && TrimV1ExactBoolean(value, "sampleExactClaimed", false);
    }

    private static bool TrimV1Selection(
        JsonElement value,
        int sourceFrames,
        out int start,
        out int end)
    {
        start = end = 0;
        return HasExactProperties(
                value,
                "startFrame",
                "endFrameExclusive")
            && TrimV1Int32(value, "startFrame", out start)
            && TrimV1Int32(value, "endFrameExclusive", out end)
            && start >= 0
            && end > start
            && end <= sourceFrames;
    }

    private static bool TrimV1FrameToPts(
        VideoTrimV1SourceSnapshot source,
        int frame,
        out long pts)
    {
        pts = 0;
        try
        {
            long numerator = checked(
                (long)frame
                * source.FpsDenominator
                * source.TimeBaseDenominator);
            long denominator = checked(
                (long)source.FpsNumerator
                * source.TimeBaseNumerator);
            if (denominator <= 0 || numerator % denominator != 0)
                return false;
            pts = checked(source.VideoStartTimestamp + numerator / denominator);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TrimV1DurationMatchesFrames(
        int frames,
        int fpsNumerator,
        int fpsDenominator,
        long durationNumerator,
        long durationDenominator)
    {
        if (frames <= 0
            || fpsNumerator <= 0
            || fpsDenominator <= 0
            || durationNumerator <= 0
            || durationDenominator <= 0)
        {
            return false;
        }
        try
        {
            return checked((long)frames * fpsDenominator * durationDenominator)
                == checked(durationNumerator * fpsNumerator);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string BuildVideoTrimV1RequestDetails(
        VideoTrimV1ReaderSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("処理: 動画トリム");
        builder.AppendLine("Protocol: Video Trim v1");
        builder.AppendLine(FormattableString.Invariant(
            $"区間: [{snapshot.SelectionStartFrame}, {snapshot.SelectionEndFrameExclusive}) · {snapshot.OutputFrameCount} frame"));
        builder.AppendLine(FormattableString.Invariant(
            $"入力: {snapshot.SourceFrameCount} frame · {snapshot.SourceFpsNumerator}/{snapshot.SourceFpsDenominator} fps · {snapshot.SourceWidth}x{snapshot.SourceHeight}"));
        builder.AppendLine(FormattableString.Invariant(
            $"出力: {snapshot.OutputFrameCount} frame · {snapshot.OutputFpsNumerator}/{snapshot.OutputFpsDenominator} fps · {snapshot.OutputDurationMs:0.###} ms"));
        builder.AppendLine($"音声: {snapshot.AudioPolicy} · {snapshot.DeliveryAudioKind}");
        builder.Append("元動画を変更せず、exact区間をH.264/AAC policyで新しい管理動画へ書き出します。");
        return builder.ToString();
    }

    private static bool TrimV1ExactString(
        JsonElement value,
        string name,
        string expected)
        => TrimV1SingleString(value, name, out string actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TrimV1SingleString(
        JsonElement value,
        string name,
        out string result)
    {
        result = "";
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
                continue;
            count++;
            if (property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not string text)
            {
                return false;
            }
            result = text;
        }
        return count == 1;
    }

    private static bool TrimV1BoundedText(
        JsonElement value,
        string name,
        int maximumLength,
        out string result)
        => TrimV1SingleString(value, name, out result)
            && result.Length is > 0
            && result.Length <= maximumLength
            && !result.Any(char.IsControl);

    private static bool TrimV1SafeTechnical(
        JsonElement value,
        string name,
        int maximumLength,
        out string result)
        => TrimV1SingleString(value, name, out result)
            && result.Length is > 0
            && result.Length <= maximumLength
            && result.All(character => character is >= '!' and <= '~');

    private static bool TrimV1Path(
        JsonElement value,
        string name,
        out string path)
        => TrimV1BoundedText(value, name, 32_768, out path)
            && Path.IsPathFullyQualified(path);

    private static bool TrimV1Sha(JsonElement value, string name)
        => TrimV1SingleString(value, name, out string sha)
            && TrimV1LowerHex(sha, 64);

    private static bool TrimV1LowerHex(string value, int length)
        => value.Length == length
            && value.All(character => character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static bool TrimV1Int32(
        JsonElement value,
        string name,
        out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out result);
    }

    private static bool TrimV1Int64(
        JsonElement value,
        string name,
        out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out result)
            && result is >= -9_007_199_254_740_991L
                and <= 9_007_199_254_740_991L;
    }

    private static bool TrimV1ExactInt32(
        JsonElement value,
        string name,
        int expected)
        => TrimV1Int32(value, name, out int actual) && actual == expected;

    private static bool TrimV1ExactInt64(
        JsonElement value,
        string name,
        long expected)
        => TrimV1Int64(value, name, out long actual) && actual == expected;

    private static bool TrimV1Boolean(
        JsonElement value,
        string name,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True
                or JsonValueKind.False))
        {
            return false;
        }
        result = property.GetBoolean();
        return true;
    }

    private static bool TrimV1ExactBoolean(
        JsonElement value,
        string name,
        bool expected)
        => TrimV1Boolean(value, name, out bool actual) && actual == expected;

    private static bool TrimV1FiniteNumber(
        JsonElement value,
        string name,
        out double result)
    {
        result = 0;
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out result)
            && double.IsFinite(result)
            && VideoTrimV1LosslessJsonNumber(property.GetRawText(), result);
    }

    private static bool VideoTrimV1LosslessJsonNumber(
        string token,
        double value)
    {
        if (!TryNormalizeVideoTrimV1Decimal(
                token,
                out bool negative,
                out string digits,
                out int exponent))
        {
            return false;
        }
        if (value == 0 && digits != "0")
            return false;
        if (exponent >= 0
            && (value < -9_007_199_254_740_991d
                || value > 9_007_199_254_740_991d
                || Math.Truncate(value) != value))
        {
            return false;
        }
        if (BitConverter.DoubleToInt64Bits(value)
            == BitConverter.DoubleToInt64Bits(-0d))
        {
            return true;
        }
        string roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        return TryNormalizeVideoTrimV1Decimal(
                roundTrip,
                out bool roundTripNegative,
                out string roundTripDigits,
                out int roundTripExponent)
            && negative == roundTripNegative
            && string.Equals(digits, roundTripDigits, StringComparison.Ordinal)
            && exponent == roundTripExponent;
    }

    private static bool TryNormalizeVideoTrimV1Decimal(
        string token,
        out bool negative,
        out string digits,
        out int exponent)
    {
        exponent = 0;
        negative = token.StartsWith("-", StringComparison.Ordinal);
        string unsigned = negative ? token[1..] : token;
        int exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0
            ? unsigned
            : unsigned[..exponentIndex];
        string? exponentText = exponentIndex < 0
            ? null
            : unsigned[(exponentIndex + 1)..];
        int decimalIndex = mantissa.IndexOf('.');
        int decimalPlaces = decimalIndex < 0
            ? 0
            : mantissa.Length - decimalIndex - 1;
        digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal)
            .TrimStart('0');
        if (digits.Length == 0)
        {
            negative = false;
            digits = "0";
            exponent = 0;
            return true;
        }
        if (exponentText is not null
            && !int.TryParse(
                exponentText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out exponent))
        {
            exponent = 0;
            return false;
        }
        try
        {
            exponent = checked(exponent - decimalPlaces);
            while (digits.EndsWith('0'))
            {
                digits = digits[..^1];
                exponent = checked(exponent + 1);
            }
        }
        catch (OverflowException)
        {
            return false;
        }
        return digits.All(character => character is >= '0' and <= '9');
    }

    private static bool TrimV1Timestamp(
        JsonElement value,
        string name,
        out DateTimeOffset result)
    {
        result = default;
        return TrimV1SingleString(value, name, out string text)
            && text.Length == 24
            && DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal,
                out result);
    }

    public bool TryReadVideoTrimV1ForSmoke(
        JsonElement job,
        out string sourceKind,
        out int startFrame,
        out int endFrameExclusive,
        out string audioPolicy,
        out int outputFrameCount,
        out string detail)
    {
        sourceKind = "";
        startFrame = endFrameExclusive = outputFrameCount = 0;
        audioPolicy = detail = "";
        if (!TryReadVideoTrimV1WorkspaceSnapshot(job, out var snapshot))
            return false;
        sourceKind = snapshot.SourceKind;
        startFrame = snapshot.SelectionStartFrame;
        endFrameExclusive = snapshot.SelectionEndFrameExclusive;
        audioPolicy = snapshot.AudioPolicy;
        outputFrameCount = snapshot.OutputFrameCount;
        detail = BuildVideoTrimV1RequestDetails(snapshot);
        return true;
    }

    public static string ComputeVideoTrimV1PresetHashForSmoke(
        JsonElement videoTrim)
        => HashStableJson(videoTrim)[..12];

    public static VideoTrimV1JobSmokeSnapshot?
        ReadVideoTrimV1JobForSmoke(JsonElement job)
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
        return new(
            view.VideoTrimEnvelopeClaimed,
            view.IsExactCurrentVideoTrimV1,
            view.IsVideoTrimReaderOnly,
            view.IsSupportedMutationOperation,
            view.VideoKindFilterKey,
            view.OperationLabel,
            view.PresetSummary,
            view.Status,
            view.CanCancel,
            view.CanRetry,
            view.CanDismiss,
            view.CanReorder,
            view.CanUseOutput,
            view.CanDeleteOutput,
            visibleActions);
    }
}

public sealed record VideoTrimV1JobSmokeSnapshot(
    bool Claimed,
    bool ExactCurrent,
    bool ReaderOnly,
    bool SupportedMutation,
    string? FilterKey,
    string OperationLabel,
    string PresetSummary,
    string Status,
    bool CanCancel,
    bool CanRetry,
    bool CanDismiss,
    bool CanReorder,
    bool CanUseOutput,
    bool CanDeleteOutput,
    string[] VisibleActionKinds);

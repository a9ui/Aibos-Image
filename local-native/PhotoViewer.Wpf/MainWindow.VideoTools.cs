using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string VideoToolsContractId =
        "PV-ENHANCE-VIDEO-TOOLS-001";
    private const string VideoToolsProtocol =
        "aibos-enhancement-video-tools-v1";
    private const int VideoToolsSchemaVersion = 1;
    private const int VideoToolsPlaybackFps = 24;
    private const int VideoToolsMinimumSteps = 1;
    private const int VideoToolsMaximumSteps = 40;
    private const int VideoToolsDefaultSteps = 20;
    private const int VideoToolsDefaultMaximumPixelArea = 414_720;
    private const int VideoToolsMaximumSourceFrames = 450;
    private const int VideoToolsMaximumSourcePixelArea = 1_920 * 1_080;
    private const int VideoToolsMaximumOutputPixelArea = 3_840 * 2_160;
    private const long VideoToolsMaximumSourceBytes = 512L * 1024 * 1024;
    private const long VideoToolsMaximumSafeInteger = 9_007_199_254_740_991;
    private const double VideoToolsMaximumSourceDurationSeconds = 15.1;
    private static readonly int[] VideoToolsRetakeFrameCounts =
        [124, 243, 294, 362];
    private static readonly int[] VideoToolsCanvasTiers =
        [230_400, 307_200, 414_720];
    private const string VideoToolsH3ShotBeginning =
        "The shot begins in one continuous take.";
    private const string VideoToolsH3ShotDevelopment =
        "The motion develops continuously";
    private const string VideoToolsH3ShotEnding = "By the end,";
    private static readonly Regex VideoToolsForbiddenH3DslPattern = new(
        @"(?:\bTimeline\s*:|\bBeat\s+\d+\b|\(\d+(?:\.\d+)?-\d+(?:\.\d+)?\s+seconds\)|\b(?:Camera|Expression|Secondary motion|Protected literal continuity)\s*:)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex VideoToolsProtectedTokenPattern = new(
        @"⟦[DT][1-9]\d*⟧",
        RegexOptions.CultureInvariant);
    private static readonly Regex VideoToolsDialogueWrapperPattern = new(
        @"\(S[1-8]\) says <d>\[(?:English|Japanese|Chinese|Korean|Russian|Other)\] [^<>\r\n]+</d>",
        RegexOptions.CultureInvariant);

    private enum VideoToolsKind
    {
        Retake,
        Finish,
    }

    private enum VideoFinishMode
    {
        Faithful,
        Detail,
    }

    private readonly record struct VideoToolsFeatureCapability(
        bool Ready,
        string State,
        string? ReasonCode);

    private readonly record struct VideoToolsCapabilityState(
        VideoToolsFeatureCapability Retake,
        VideoToolsFeatureCapability Finish);

    private sealed record VideoToolsSourceChoice(
        string SourceId,
        string SourceVideoJobId,
        string OutputPath,
        bool IsExactMiniMaxH3,
        double DurationSeconds,
        int PlaybackFps,
        int FrameCount,
        int Width,
        int Height,
        bool Audio,
        string Prompt,
        int Steps,
        int MaximumPixelArea,
        long OutputSize,
        double OutputMtimeMs);

    public readonly record struct VideoRetakePlanSmokeSnapshot(
        int SourceFrameCount,
        int SelectionStartFrame,
        int SelectionEndFrameExclusive,
        int ActualStartFrame,
        int ActualFrameCount,
        int FirstAnchorFrame,
        int LastAnchorFrame,
        double SelectionStartSeconds,
        double SelectionEndSeconds,
        double ActualStartSeconds,
        double ActualEndSeconds);

    public readonly record struct VideoFinishPlanSmokeSnapshot(
        string Mode,
        int Scale,
        int SourceWidth,
        int SourceHeight,
        int OutputWidth,
        int OutputHeight,
        int PlaybackFps,
        int FrameCount,
        double DurationSeconds,
        bool AudioPreserved,
        bool UsesInterpolation);

    public readonly record struct VideoToolsCapabilitySmokeSnapshot(
        bool RetakeReady,
        string RetakeState,
        string? RetakeReasonCode,
        bool FinishReady,
        string FinishState,
        string? FinishReasonCode);

    private readonly record struct VideoToolsWorkspaceSnapshot(
        string Kind,
        string? FinishMode);

    private readonly record struct VideoToolsAudioIdentity(
        long TimeBaseNumerator,
        long TimeBaseDenominator,
        long DurationTimestamp,
        long PacketCount,
        long PacketPayloadBytes,
        string PacketPayloadSha256);

    private sealed record VideoToolsRetakeActionContext(
        long Generation,
        VideoToolsSourceChoice Source,
        VideoSourceChoice DependencySource,
        VideoRetakePlanSmokeSnapshot Plan,
        string Prompt,
        int Steps,
        int MaximumPixelArea,
        Tile? ModalTile,
        string? ModalSourcePath,
        VideoH3SourceStamp SourceStamp,
        string SourceSha256);

    private VideoToolsKind _videoToolsKind = VideoToolsKind.Retake;
    private VideoFinishMode _videoFinishMode = VideoFinishMode.Faithful;
    private VideoToolsSourceChoice? _videoToolsSource;
    private VideoToolsCapabilityState? _videoToolsCapability;
    private long _videoToolsHealthGeneration;
    private long _videoToolsActionGeneration;
    private bool _videoToolsRequestPending;
    private bool _syncingVideoToolsControls;
    private VideoToolsSourceChoice? _videoToolsSourceForSmoke;

    private string VideoToolsText(string resourceKey)
        => TryFindResource(resourceKey) as string ?? resourceKey;

    private string VideoToolsFormat(
        string resourceKey,
        params object?[] arguments)
        => string.Format(
            CultureInfo.CurrentUICulture,
            VideoToolsText(resourceKey),
            arguments);

    private static bool TryPlanVideoRetake(
        int sourceFrameCount,
        double selectionStartSeconds,
        double selectionEndSeconds,
        out VideoRetakePlanSmokeSnapshot plan)
    {
        plan = default;
        if (!VideoToolsRetakeFrameCounts.Contains(sourceFrameCount)
            || !double.IsFinite(selectionStartSeconds)
            || !double.IsFinite(selectionEndSeconds)
            || selectionStartSeconds < 0
            || selectionEndSeconds <= selectionStartSeconds)
        {
            return false;
        }

        double sourceDurationSeconds =
            (double)sourceFrameCount / VideoToolsPlaybackFps;
        const double secondsTolerance = 0.000_000_1;
        if (selectionEndSeconds > sourceDurationSeconds + secondsTolerance)
            return false;

        int selectionStartFrame = Math.Clamp(
            (int)Math.Floor(
                selectionStartSeconds * VideoToolsPlaybackFps),
            0,
            sourceFrameCount - 1);
        int selectionEndFrameExclusive = Math.Clamp(
            (int)Math.Ceiling(
                selectionEndSeconds * VideoToolsPlaybackFps),
            selectionStartFrame + 1,
            sourceFrameCount);
        int selectedFrameCount =
            selectionEndFrameExclusive - selectionStartFrame;
        int actualFrameCount = VideoToolsRetakeFrameCounts
            .Where(count => count >= selectedFrameCount
                && count <= sourceFrameCount)
            .DefaultIfEmpty(0)
            .First();
        if (actualFrameCount == 0)
            return false;

        int spareFrames = actualFrameCount - selectedFrameCount;
        // The server uses the same deterministic tie-break: when padding is
        // odd, put the extra frame before the selected interval.
        int centeredStart = selectionStartFrame - ((spareFrames + 1) / 2);
        int actualStartFrame = Math.Clamp(
            centeredStart,
            0,
            sourceFrameCount - actualFrameCount);
        int actualEndFrameExclusive =
            actualStartFrame + actualFrameCount;
        if (actualStartFrame > selectionStartFrame
            || actualEndFrameExclusive < selectionEndFrameExclusive)
        {
            return false;
        }

        plan = new VideoRetakePlanSmokeSnapshot(
            sourceFrameCount,
            selectionStartFrame,
            selectionEndFrameExclusive,
            actualStartFrame,
            actualFrameCount,
            actualStartFrame,
            actualEndFrameExclusive - 1,
            (double)selectionStartFrame / VideoToolsPlaybackFps,
            (double)selectionEndFrameExclusive / VideoToolsPlaybackFps,
            (double)actualStartFrame / VideoToolsPlaybackFps,
            (double)actualEndFrameExclusive / VideoToolsPlaybackFps);
        return true;
    }

    public static bool TryPlanVideoRetakeForSmoke(
        int sourceFrameCount,
        double selectionStartSeconds,
        double selectionEndSeconds,
        out VideoRetakePlanSmokeSnapshot plan)
        => TryPlanVideoRetake(
            sourceFrameCount,
            selectionStartSeconds,
            selectionEndSeconds,
            out plan);

    private static bool TryPlanVideoFinish(
        VideoFinishMode mode,
        int sourceWidth,
        int sourceHeight,
        int playbackFps,
        int frameCount,
        double durationSeconds,
        bool audio,
        out VideoFinishPlanSmokeSnapshot plan)
    {
        plan = default;
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || sourceWidth > 1_920
            || sourceHeight > 1_080
            || playbackFps is not (24 or 30)
            || frameCount <= 0
            || frameCount > VideoToolsMaximumSourceFrames
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0
            || durationSeconds > VideoToolsMaximumSourceDurationSeconds
            || durationSeconds != (double)frameCount / playbackFps)
        {
            return false;
        }

        try
        {
            int sourcePixelArea = checked(sourceWidth * sourceHeight);
            int outputWidth = checked(sourceWidth * 2);
            int outputHeight = checked(sourceHeight * 2);
            int outputPixelArea = checked(outputWidth * outputHeight);
            if (sourcePixelArea > VideoToolsMaximumSourcePixelArea
                || outputWidth > 3_840
                || outputHeight > 2_160
                || outputPixelArea > VideoToolsMaximumOutputPixelArea)
            {
                return false;
            }

            plan = new VideoFinishPlanSmokeSnapshot(
                mode == VideoFinishMode.Detail ? "detail" : "faithful",
                2,
                sourceWidth,
                sourceHeight,
                outputWidth,
                outputHeight,
                playbackFps,
                frameCount,
                durationSeconds,
                audio,
                UsesInterpolation: false);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TryPlanVideoFinishForSmoke(
        string mode,
        int sourceWidth,
        int sourceHeight,
        int playbackFps,
        int frameCount,
        double durationSeconds,
        bool audio,
        out VideoFinishPlanSmokeSnapshot plan)
    {
        plan = default;
        return TryParseVideoFinishMode(mode, out VideoFinishMode parsedMode)
            && TryPlanVideoFinish(
                parsedMode,
                sourceWidth,
                sourceHeight,
                playbackFps,
                frameCount,
                durationSeconds,
                audio,
                out plan);
    }

    private static bool TryParseVideoFinishMode(
        string? mode,
        out VideoFinishMode parsed)
    {
        if (string.Equals(mode, "faithful", StringComparison.Ordinal))
        {
            parsed = VideoFinishMode.Faithful;
            return true;
        }
        if (string.Equals(mode, "detail", StringComparison.Ordinal))
        {
            parsed = VideoFinishMode.Detail;
            return true;
        }
        parsed = default;
        return false;
    }

    private static bool TryParseVideoToolsCapability(
        JsonElement payload,
        out VideoToolsCapabilityState state)
    {
        state = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleVideoToolsProperty(payload, "capabilities")
            || !payload.TryGetProperty(
                "capabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleVideoToolsProperty(capabilities, "videoToolsV1")
            || !capabilities.TryGetProperty(
                "videoToolsV1",
                out JsonElement capability)
            || capability.ValueKind != JsonValueKind.Object
            || !HasExactVideoToolsProperties(
                capability,
                "contractId",
                "protocol",
                "readerReady",
                "retake",
                "finish")
            || !VideoToolsExactString(
                capability,
                "contractId",
                VideoToolsContractId)
            || !VideoToolsExactString(
                capability,
                "protocol",
                VideoToolsProtocol)
            || !VideoToolsExactBoolean(
                capability,
                "readerReady",
                expected: true)
            || !capability.TryGetProperty(
                "retake",
                out JsonElement retake)
            || !TryParseVideoToolsFeatureCapability(
                retake,
                VideoToolsKind.Retake,
                out VideoToolsFeatureCapability retakeState)
            || !capability.TryGetProperty(
                "finish",
                out JsonElement finish)
            || !TryParseVideoToolsFeatureCapability(
                finish,
                VideoToolsKind.Finish,
                out VideoToolsFeatureCapability finishState))
        {
            return false;
        }

        state = new VideoToolsCapabilityState(retakeState, finishState);
        return true;
    }

    private static bool TryParseVideoToolsFeatureCapability(
        JsonElement feature,
        VideoToolsKind kind,
        out VideoToolsFeatureCapability state)
    {
        state = default;
        if (feature.ValueKind != JsonValueKind.Object
            || !HasExactVideoToolsProperties(
                feature,
                "writerEnabled",
                "backendConfigured",
                "runtimeVerified",
                "ready",
                "state",
                "reasonCode")
            || !VideoToolsTryBoolean(
                feature,
                "writerEnabled",
                out bool writerEnabled)
            || !VideoToolsTryBoolean(
                feature,
                "backendConfigured",
                out bool backendConfigured)
            || !VideoToolsTryBoolean(
                feature,
                "runtimeVerified",
                out bool runtimeVerified)
            || !VideoToolsTryBoolean(feature, "ready", out bool ready)
            || !feature.TryGetProperty("state", out JsonElement stateElement)
            || stateElement.ValueKind != JsonValueKind.String
            || !feature.TryGetProperty(
                "reasonCode",
                out JsonElement reasonElement))
        {
            return false;
        }

        string featureState = stateElement.GetString() ?? "";
        string? reasonCode = reasonElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => reasonElement.GetString(),
            _ => "__invalid__",
        };
        bool allReadyFlags =
            writerEnabled && backendConfigured && runtimeVerified;
        if (ready)
        {
            if (!allReadyFlags
                || featureState != "ready"
                || reasonCode is not null)
            {
                return false;
            }
        }
        else if (featureState is not ("disabled" or "unverified")
            || string.IsNullOrWhiteSpace(reasonCode)
            || !VideoToolsReasonMatchesFlags(
                kind,
                reasonCode,
                featureState,
                writerEnabled,
                backendConfigured,
                runtimeVerified))
        {
            return false;
        }

        state = new VideoToolsFeatureCapability(
            ready,
            featureState,
            reasonCode);
        return true;
    }

    private static bool VideoToolsReasonMatchesFlags(
        VideoToolsKind kind,
        string reasonCode,
        string state,
        bool writerEnabled,
        bool backendConfigured,
        bool runtimeVerified)
    {
        _ = writerEnabled;
        _ = backendConfigured;
        string prefix = kind == VideoToolsKind.Retake
            ? "RETAKE_"
            : "FINISH_";
        bool knownRuntimeReason = reasonCode is var reason
            && (reason == prefix + "RUNTIME_UNPINNED"
                || reason == prefix + "WORKFLOW_INPUT_UNVERIFIED"
                || reason == prefix + "MODEL_LICENSE_RECEIPT_MISSING"
                || reason == prefix + "GPU_CANARY_MISSING"
                || reason == prefix + "OUTPUT_CANARY_MISSING");
        return knownRuntimeReason
            && state is "disabled" or "unverified"
            && !runtimeVerified;
    }

    private static bool HasSingleVideoToolsProperty(
        JsonElement element,
        string name)
    {
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
                count++;
        }
        return count == 1;
    }

    private static bool HasExactVideoToolsProperties(
        JsonElement element,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            count++;
            if (!remaining.Remove(property.Name))
                return false;
        }
        return count == names.Length && remaining.Count == 0;
    }

    private static bool VideoToolsExactString(
        JsonElement element,
        string name,
        string expected)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(
                value.GetString(),
                expected,
                StringComparison.Ordinal);

    private static bool VideoToolsExactBoolean(
        JsonElement element,
        string name,
        bool expected)
        => VideoToolsTryBoolean(element, name, out bool actual)
            && actual == expected;

    private static bool VideoToolsTryBoolean(
        JsonElement element,
        string name,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    public static bool TryParseVideoToolsCapabilityForSmoke(
        JsonElement payload,
        out VideoToolsCapabilitySmokeSnapshot snapshot)
    {
        snapshot = default;
        if (!TryParseVideoToolsCapability(payload, out var capability))
            return false;
        snapshot = new VideoToolsCapabilitySmokeSnapshot(
            capability.Retake.Ready,
            capability.Retake.State,
            capability.Retake.ReasonCode,
            capability.Finish.Ready,
            capability.Finish.State,
            capability.Finish.ReasonCode);
        return true;
    }

    private static bool ClaimsVideoToolsWorkspaceSnapshot(JsonElement job)
    {
        if (!job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        bool protocolClaimsVideoTools =
            HasSingleProperty(video, "protocol")
            && video.TryGetProperty("protocol", out JsonElement protocol)
            && protocol.ValueKind == JsonValueKind.String
            && protocol.GetString() is string protocolValue
            && protocolValue.StartsWith(
                "aibos-enhancement-video-tools-",
                StringComparison.Ordinal);
        return protocolClaimsVideoTools
            || (VideoToolsExactInt32(video, "schemaVersion", 1)
                && video.TryGetProperty("kind", out JsonElement kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() is "retake" or "finish");
    }

    private static bool TryReadVideoToolsWorkspaceSnapshot(
        JsonElement job,
        out VideoToolsWorkspaceSnapshot snapshot)
    {
        snapshot = default;
        if (!ClaimsVideoToolsWorkspaceSnapshot(job)
            || !HasSingleProperty(job, "operation")
            || !VideoToolsExactString(job, "operation", "video")
            || !HasSingleProperty(job, "mediaKind")
            || !VideoToolsExactString(job, "mediaKind", "video")
            || job.EnumerateObject().Any(static property =>
                property.NameEquals("sourceProducerJobId"))
            || !TryGetSingleVideoToolsString(
                job,
                "sourceVideoJobId",
                out string sourceVideoJobId)
            || !IsSafeVideoToolsJobId(sourceVideoJobId)
            || !TryGetSingleVideoToolsString(
                job,
                "presetId",
                out string jobPresetId)
            || !TryGetSingleVideoToolsString(
                job,
                "adapterId",
                out string jobBackendId)
            || !TryGetSingleVideoToolsString(
                job,
                "presetHash",
                out string presetHash)
            || !IsLowerHex(presetHash, 12)
            || !job.TryGetProperty("video", out JsonElement video)
            || !TryRebuildExactVideoToolsSnapshot(
                video,
                out JsonElement rebuilt,
                out VideoToolsWorkspaceSnapshot parsed)
            || !string.Equals(
                sourceVideoJobId,
                video.GetProperty("source").GetProperty("jobId").GetString(),
                StringComparison.Ordinal)
            || !string.Equals(
                jobPresetId,
                video.GetProperty("presetId").GetString(),
                StringComparison.Ordinal)
            || !string.Equals(
                jobBackendId,
                video.GetProperty("backendId").GetString(),
                StringComparison.Ordinal)
            || !string.Equals(
                HashStableJson(video),
                HashStableJson(rebuilt),
                StringComparison.Ordinal)
            || !string.Equals(
                presetHash,
                HashStableJson(video)[..12],
                StringComparison.Ordinal))
        {
            return false;
        }
        snapshot = parsed;
        return true;
    }

    private static bool TryRebuildExactVideoToolsSnapshot(
        JsonElement video,
        out JsonElement rebuilt,
        out VideoToolsWorkspaceSnapshot snapshot)
    {
        rebuilt = default;
        snapshot = default;
        if (!VideoToolsExactInt32(video, "schemaVersion", 1)
            || !VideoToolsExactString(video, "protocol", VideoToolsProtocol)
            || !video.TryGetProperty("kind", out JsonElement kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not string kind
            || kind is not ("retake" or "finish")
            || !video.TryGetProperty("source", out JsonElement source)
            || !TryReadExactVideoToolsSource(
                source,
                out string sourceJobId,
                out long sourceSize,
                out double sourceMtimeMs,
                out string sourceSha256,
                out int sourceWidth,
                out int sourceHeight,
                out int sourceFrameCount,
                out int fpsNumerator,
                out int durationMs,
                out int audioStreamCount,
                out VideoToolsAudioIdentity? audioIdentity)
            || !TryGetSingleVideoToolsString(
                video,
                "presetId",
                out string presetId)
            || !TryGetSingleVideoToolsString(
                video,
                "backendId",
                out string backendId)
            || !video.TryGetProperty(
                "requested",
                out JsonElement requested))
        {
            return false;
        }

        object? audioValue = audioIdentity is VideoToolsAudioIdentity audio
            ? new
            {
                codec = "aac",
                codecTag = "mp4a",
                profile = "LC",
                sampleRate = 32_000,
                channels = 2,
                timeBaseNumerator = audio.TimeBaseNumerator,
                timeBaseDenominator = audio.TimeBaseDenominator,
                startTimestamp = 0,
                durationTimestamp = audio.DurationTimestamp,
                packetCount = audio.PacketCount,
                packetPayloadBytes = audio.PacketPayloadBytes,
                packetPayloadSha256 = audio.PacketPayloadSha256,
            }
            : null;
        object sourceValue = new
        {
            jobId = sourceJobId,
            signature = new { size = sourceSize, mtimeMs = sourceMtimeMs },
            sha256 = sourceSha256,
            probe = new
            {
                container = "mp4",
                width = sourceWidth,
                height = sourceHeight,
                frameCount = sourceFrameCount,
                fpsNumerator,
                fpsDenominator = 1,
                durationMs,
                videoStreamCount = 1,
                audioStreamCount,
                audio = audioValue,
            },
        };
        if (kind == "retake")
        {
            if (!HasExactProperties(
                    video,
                    "schemaVersion",
                    "protocol",
                    "kind",
                    "presetId",
                    "backendId",
                    "seed",
                    "source",
                    "requested",
                    "plan",
                    "delivery")
                || presetId != "minimax-h3-retake-v1"
                || backendId != "minimax-h3-retake-v1"
                || !TryGetBoundedVideoToolsInt(
                    video,
                    "seed",
                    0,
                    int.MaxValue,
                    out int seed)
                || fpsNumerator != 24
                || audioStreamCount != 1
                || audioIdentity is null
                || !VideoToolsRetakeFrameCounts.Contains(sourceFrameCount)
                || !TryReadExactVideoToolsRetakeRequest(
                    requested,
                    sourceFrameCount,
                    out int selectionStartFrame,
                    out int selectionEndFrameExclusive,
                    out string prompt,
                    out int steps,
                    out int maximumPixelArea))
            {
                return false;
            }

            int selectedEndFrame = selectionEndFrameExclusive - 1;
            int selectedFrameCount =
                selectionEndFrameExclusive - selectionStartFrame;
            int actualFrameCount = VideoToolsRetakeFrameCounts
                .FirstOrDefault(count =>
                    count >= selectedFrameCount
                    && count <= sourceFrameCount);
            if (actualFrameCount == 0)
                return false;
            int centeredStart = (int)Math.Floor(
                (selectionStartFrame
                    + selectedEndFrame
                    - actualFrameCount
                    + 1) / 2d);
            int actualStartFrame = Math.Clamp(
                centeredStart,
                0,
                sourceFrameCount - actualFrameCount);
            int actualEndFrame =
                actualStartFrame + actualFrameCount - 1;
            int selectedStartMs =
                selectionStartFrame * 1_000 / VideoToolsPlaybackFps;
            int selectedEndMs = checked(
                (selectionEndFrameExclusive * 1_000
                    + VideoToolsPlaybackFps - 1)
                / VideoToolsPlaybackFps);
            int actualStartMs =
                actualStartFrame * 1_000 / VideoToolsPlaybackFps;
            int actualEndMs = checked(
                ((actualEndFrame + 1) * 1_000
                    + VideoToolsPlaybackFps - 1)
                / VideoToolsPlaybackFps);
            (int outputWidth, int outputHeight) =
                NormalizeMiniMaxH3VideoCanvas(
                    sourceWidth,
                    sourceHeight,
                    maximumPixelArea);
            object requestedValue = new
            {
                schemaVersion = 1,
                kind = "retake",
                selection = new
                {
                    startFrame = selectionStartFrame,
                    endFrameExclusive = selectionEndFrameExclusive,
                },
                prompt,
                steps,
                maximumPixelArea,
            };
            rebuilt = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                protocol = VideoToolsProtocol,
                kind = "retake",
                presetId = "minimax-h3-retake-v1",
                backendId = "minimax-h3-retake-v1",
                seed,
                source = sourceValue,
                requested = requestedValue,
                plan = new
                {
                    selected = new
                    {
                        startMs = selectedStartMs,
                        endMs = selectedEndMs,
                        startFrame = selectionStartFrame,
                        endFrame = selectedEndFrame,
                    },
                    actual = new
                    {
                        startMs = actualStartMs,
                        endMs = actualEndMs,
                        startFrame = actualStartFrame,
                        endFrame = actualEndFrame,
                        frameCount = actualFrameCount,
                    },
                    firstAnchorFrame = actualStartFrame,
                    lastAnchorFrame = actualEndFrame,
                },
                delivery = new
                {
                    targetFps = 24,
                    width = outputWidth,
                    height = outputHeight,
                    frameCount = sourceFrameCount,
                    durationMs,
                    preserveSourceAudio = true,
                    discardGeneratedAudio = true,
                    splicePrefixAndSuffix = true,
                },
            });
            snapshot = new VideoToolsWorkspaceSnapshot("retake", null);
            return true;
        }

        if (!HasExactProperties(
                video,
                "schemaVersion",
                "protocol",
                "kind",
                "presetId",
                "backendId",
                "source",
                "requested",
                "plan")
            || presetId != "aibos-video-finish-2x-v1"
            || backendId != "aibos-video-finish-2x-v1"
            || !TryReadExactVideoToolsFinishRequest(
                requested,
                out string mode))
        {
            return false;
        }
        int finishWidth = checked(sourceWidth * 2);
        int finishHeight = checked(sourceHeight * 2);
        if (finishWidth > 3_840
            || finishHeight > 2_160
            || checked(finishWidth * finishHeight)
                > VideoToolsMaximumOutputPixelArea)
        {
            return false;
        }
        rebuilt = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            protocol = VideoToolsProtocol,
            kind = "finish",
            presetId = "aibos-video-finish-2x-v1",
            backendId = "aibos-video-finish-2x-v1",
            source = sourceValue,
            requested = new
            {
                schemaVersion = 1,
                kind = "finish",
                mode,
                scale = 2,
            },
            plan = new
            {
                inputWidth = sourceWidth,
                inputHeight = sourceHeight,
                outputWidth = finishWidth,
                outputHeight = finishHeight,
                frameCount = sourceFrameCount,
                fpsNumerator,
                fpsDenominator = 1,
                durationMs,
                preserveAudio = true,
                temporalSceneCutReset = true,
            },
        });
        snapshot = new VideoToolsWorkspaceSnapshot("finish", mode);
        return true;
    }

    private static bool TryReadExactVideoToolsSource(
        JsonElement source,
        out string jobId,
        out long size,
        out double mtimeMs,
        out string sha256,
        out int width,
        out int height,
        out int frameCount,
        out int fpsNumerator,
        out int durationMs,
        out int audioStreamCount,
        out VideoToolsAudioIdentity? audioIdentity)
    {
        jobId = "";
        size = 0;
        mtimeMs = 0;
        sha256 = "";
        audioIdentity = null;
        width = height = frameCount = fpsNumerator = durationMs =
            audioStreamCount = 0;
        if (source.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                source,
                "jobId",
                "signature",
                "sha256",
                "probe")
            || !TryGetSingleVideoToolsString(
                source,
                "jobId",
                out jobId)
            || !IsSafeVideoToolsJobId(jobId)
            || !TryGetSingleVideoToolsString(
                source,
                "sha256",
                out sha256)
            || !IsLowerHex(sha256, 64)
            || !source.TryGetProperty(
                "signature",
                out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !HasExactProperties(signature, "size", "mtimeMs")
            || !signature.TryGetProperty("size", out JsonElement sizeElement)
            || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out size)
            || size is < 1 or > VideoToolsMaximumSourceBytes
            || !signature.TryGetProperty(
                "mtimeMs",
                out JsonElement mtimeElement)
            || !mtimeElement.TryGetDouble(out mtimeMs)
            || !double.IsFinite(mtimeMs)
            || !source.TryGetProperty("probe", out JsonElement probe)
            || probe.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
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
                "audio")
            || !VideoToolsExactString(probe, "container", "mp4")
            || !TryGetBoundedVideoToolsInt(probe, "width", 1, 1_920, out width)
            || !TryGetBoundedVideoToolsInt(probe, "height", 1, 1_080, out height)
            || checked(width * height) > VideoToolsMaximumSourcePixelArea
            || !TryGetBoundedVideoToolsInt(
                probe,
                "frameCount",
                1,
                VideoToolsMaximumSourceFrames,
                out frameCount)
            || !TryGetBoundedVideoToolsInt(
                probe,
                "fpsNumerator",
                24,
                30,
                out fpsNumerator)
            || fpsNumerator is not (24 or 30)
            || !VideoToolsExactInt32(probe, "fpsDenominator", 1)
            || !TryGetBoundedVideoToolsInt(
                probe,
                "durationMs",
                1,
                15_100,
                out durationMs)
            || !VideoToolsExactInt32(probe, "videoStreamCount", 1)
            || !TryGetBoundedVideoToolsInt(
                probe,
                "audioStreamCount",
                0,
                1,
                out audioStreamCount)
            || !probe.TryGetProperty("audio", out JsonElement audio)
            || !TryReadExactVideoToolsAudio(
                audio,
                audioStreamCount,
                out audioIdentity)
            || Math.Abs(
                durationMs
                    - frameCount * 1_000d / fpsNumerator) > 50)
        {
            return false;
        }
        return true;
    }

    private static bool TryReadExactVideoToolsAudio(
        JsonElement audio,
        int audioStreamCount,
        out VideoToolsAudioIdentity? identity)
    {
        identity = null;
        if (audioStreamCount == 0)
            return audio.ValueKind == JsonValueKind.Null;
        if (audioStreamCount != 1
            || audio.ValueKind != JsonValueKind.Object
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
            || !VideoToolsExactString(audio, "codec", "aac")
            || !VideoToolsExactString(audio, "codecTag", "mp4a")
            || !VideoToolsExactString(audio, "profile", "LC")
            || !VideoToolsExactInt32(audio, "sampleRate", 32_000)
            || !VideoToolsExactInt32(audio, "channels", 2)
            || !TryGetPositiveSafeVideoToolsInteger(
                audio,
                "timeBaseNumerator",
                VideoToolsMaximumSafeInteger,
                out long timeBaseNumerator)
            || !TryGetPositiveSafeVideoToolsInteger(
                audio,
                "timeBaseDenominator",
                VideoToolsMaximumSafeInteger,
                out long timeBaseDenominator)
            || !audio.TryGetProperty(
                "startTimestamp",
                out JsonElement startTimestamp)
            || startTimestamp.ValueKind != JsonValueKind.Number
            || !startTimestamp.TryGetInt64(out long start)
            || start != 0
            || !TryGetPositiveSafeVideoToolsInteger(
                audio,
                "durationTimestamp",
                VideoToolsMaximumSafeInteger,
                out long durationTimestamp)
            || !TryGetPositiveSafeVideoToolsInteger(
                audio,
                "packetCount",
                VideoToolsMaximumSafeInteger,
                out long packetCount)
            || !TryGetPositiveSafeVideoToolsInteger(
                audio,
                "packetPayloadBytes",
                VideoToolsMaximumSourceBytes,
                out long packetPayloadBytes)
            || !TryGetSingleVideoToolsString(
                audio,
                "packetPayloadSha256",
                out string packetPayloadSha256)
            || !IsLowerHex(packetPayloadSha256, 64))
        {
            return false;
        }
        identity = new VideoToolsAudioIdentity(
            timeBaseNumerator,
            timeBaseDenominator,
            durationTimestamp,
            packetCount,
            packetPayloadBytes,
            packetPayloadSha256);
        return true;
    }

    private static bool TryGetPositiveSafeVideoToolsInteger(
        JsonElement element,
        string propertyName,
        long maximum,
        out long value)
    {
        value = 0;
        return HasSingleProperty(element, propertyName)
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 1
            && value <= maximum;
    }

    private static bool TryReadExactVideoToolsRetakeRequest(
        JsonElement requested,
        int sourceFrameCount,
        out int startFrame,
        out int endFrameExclusive,
        out string prompt,
        out int steps,
        out int maximumPixelArea)
    {
        startFrame = endFrameExclusive = steps = maximumPixelArea = 0;
        prompt = "";
        if (requested.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                requested,
                "schemaVersion",
                "kind",
                "selection",
                "prompt",
                "steps",
                "maximumPixelArea")
            || !VideoToolsExactInt32(requested, "schemaVersion", 1)
            || !VideoToolsExactString(requested, "kind", "retake")
            || !requested.TryGetProperty(
                "selection",
                out JsonElement selection)
            || selection.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                selection,
                "startFrame",
                "endFrameExclusive")
            || !TryGetBoundedVideoToolsInt(
                selection,
                "startFrame",
                0,
                sourceFrameCount - 1,
                out startFrame)
            || !TryGetBoundedVideoToolsInt(
                selection,
                "endFrameExclusive",
                1,
                sourceFrameCount,
                out endFrameExclusive)
            || endFrameExclusive <= startFrame
            || !TryGetSingleVideoToolsString(
                requested,
                "prompt",
                out prompt)
            || !TryNormalizeAndValidateVideoToolsRetakePrompt(
                prompt,
                out string normalizedPrompt)
            || !string.Equals(
                prompt,
                normalizedPrompt,
                StringComparison.Ordinal)
            || !TryGetBoundedVideoToolsInt(
                requested,
                "steps",
                VideoToolsMinimumSteps,
                VideoToolsMaximumSteps,
                out steps)
            || !TryGetBoundedVideoToolsInt(
                requested,
                "maximumPixelArea",
                VideoToolsCanvasTiers[0],
                VideoToolsCanvasTiers[^1],
                out maximumPixelArea)
            || !VideoToolsCanvasTiers.Contains(maximumPixelArea))
        {
            return false;
        }
        return true;
    }

    private static bool TryReadExactVideoToolsFinishRequest(
        JsonElement requested,
        out string mode)
    {
        mode = "";
        return requested.ValueKind == JsonValueKind.Object
            && HasExactProperties(
                requested,
                "schemaVersion",
                "kind",
                "mode",
                "scale")
            && VideoToolsExactInt32(requested, "schemaVersion", 1)
            && VideoToolsExactString(requested, "kind", "finish")
            && TryGetSingleVideoToolsString(requested, "mode", out mode)
            && mode is "faithful" or "detail"
            && VideoToolsExactInt32(requested, "scale", 2);
    }

    private static bool TryGetSingleVideoToolsString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = "";
        if (!HasSingleProperty(element, propertyName)
            || !element.TryGetProperty(
                propertyName,
                out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not string parsed)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    public static bool TryReadVideoToolsWorkspacePresentationForSmoke(
        JsonElement job,
        out string readerKind,
        out string presetSummary,
        out string operationLabel,
        out string detailText,
        out bool supportedMutation,
        out string[] visibleActionKinds)
    {
        readerKind = "";
        presetSummary = "";
        operationLabel = "";
        detailText = "";
        supportedMutation = false;
        visibleActionKinds = [];
        EnhancementWorkspaceJobView? view = ParseEnhancementWorkspaceJob(
            job,
            0);
        if (view is null || !view.VideoToolsEnvelopeClaimed)
            return false;

        readerKind = view.VideoToolsKind ?? "protected";
        presetSummary = view.PresetSummary;
        operationLabel = view.OperationLabel;
        detailText = view.DetailText;
        supportedMutation = view.IsSupportedMutationOperation;
        visibleActionKinds = new[]
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

    public static string ComputeVideoToolsSnapshotHashForSmoke(
        JsonElement video)
        => HashStableJson(video)[..12];

    private static bool TryGetBoundedVideoToolsInt(
        JsonElement element,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return HasSingleProperty(element, propertyName)
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= minimum
            && value <= maximum;
    }

    private static bool VideoToolsExactInt32(
        JsonElement element,
        string propertyName,
        int expected)
        => TryGetBoundedVideoToolsInt(
            element,
            propertyName,
            expected,
            expected,
            out _);

    private static JsonElement BuildVideoToolsRetakeRequest(
        string sourceId,
        string sourceVideoJobId,
        VideoRetakePlanSmokeSnapshot plan,
        string prompt,
        int steps,
        int maximumPixelArea)
    {
        ValidateVideoToolsCommonRequest(sourceId, sourceVideoJobId);
        if (!TryNormalizeAndValidateVideoToolsRetakePrompt(
                prompt,
                out string normalizedPrompt))
        {
            throw new ArgumentException(
                "Retake prompt must use the exact bounded MiniMax H3 grammar.",
                nameof(prompt));
        }
        if (steps is < VideoToolsMinimumSteps or > VideoToolsMaximumSteps)
            throw new ArgumentOutOfRangeException(nameof(steps));
        if (!VideoToolsCanvasTiers.Contains(maximumPixelArea))
            throw new ArgumentOutOfRangeException(nameof(maximumPixelArea));
        if (!TryPlanVideoRetake(
                plan.SourceFrameCount,
                plan.SelectionStartSeconds,
                plan.SelectionEndSeconds,
                out VideoRetakePlanSmokeSnapshot canonical)
            || canonical.SelectionStartFrame != plan.SelectionStartFrame
            || canonical.SelectionEndFrameExclusive
                != plan.SelectionEndFrameExclusive)
        {
            throw new ArgumentException(
                "Retake selection is not canonical.",
                nameof(plan));
        }

        return JsonSerializer.SerializeToElement(new
        {
            sourceId,
            sourceVideoJobId,
            operation = "video",
            mediaKind = "video",
            videoTools = new
            {
                schemaVersion = VideoToolsSchemaVersion,
                kind = "retake",
                selection = new
                {
                    startFrame = canonical.SelectionStartFrame,
                    endFrameExclusive =
                        canonical.SelectionEndFrameExclusive,
                },
                prompt = normalizedPrompt,
                steps,
                maximumPixelArea,
            },
        });
    }

    private static JsonElement BuildVideoToolsFinishRequest(
        string sourceId,
        string sourceVideoJobId,
        VideoFinishMode mode)
    {
        ValidateVideoToolsCommonRequest(sourceId, sourceVideoJobId);
        return JsonSerializer.SerializeToElement(new
        {
            sourceId,
            sourceVideoJobId,
            operation = "video",
            mediaKind = "video",
            videoTools = new
            {
                schemaVersion = VideoToolsSchemaVersion,
                kind = "finish",
                mode = mode == VideoFinishMode.Detail
                    ? "detail"
                    : "faithful",
                scale = 2,
            },
        });
    }

    private static bool TryNormalizeAndValidateVideoToolsRetakePrompt(
        string raw,
        out string normalized)
    {
        if (!TryNormalizeAndValidateVideoH3Prompt(raw, out normalized)
            || !string.Equals(
                normalized,
                normalized.Trim(),
                StringComparison.Ordinal)
            || !HasWellFormedVideoToolsUtf16(normalized)
            || VideoToolsForbiddenH3DslPattern.IsMatch(normalized)
            || !normalized.Contains(
                VideoToolsH3ShotBeginning,
                StringComparison.Ordinal)
            || !normalized.Contains(
                VideoToolsH3ShotDevelopment,
                StringComparison.Ordinal)
            || !normalized.Contains(
                VideoToolsH3ShotEnding,
                StringComparison.Ordinal)
            || VideoToolsProtectedTokenPattern.IsMatch(normalized))
        {
            normalized = "";
            return false;
        }

        string withoutValidDialogue = VideoToolsDialogueWrapperPattern.Replace(
            normalized,
            "");
        if (withoutValidDialogue.Contains("<d>", StringComparison.Ordinal)
            || withoutValidDialogue.Contains("</d>", StringComparison.Ordinal))
        {
            normalized = "";
            return false;
        }
        return true;
    }

    private static bool HasWellFormedVideoToolsUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char codeUnit = value[index];
            if (char.IsHighSurrogate(codeUnit))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }
                index++;
            }
            else if (char.IsLowSurrogate(codeUnit))
            {
                return false;
            }
        }
        return true;
    }

    public static bool TryValidateVideoToolsRetakePromptForSmoke(
        string raw,
        out string normalized)
        => TryNormalizeAndValidateVideoToolsRetakePrompt(
            raw,
            out normalized);

    private static void ValidateVideoToolsCommonRequest(
        string sourceId,
        string sourceVideoJobId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)
            || sourceId.Length > 32_768)
        {
            throw new ArgumentException("Source ID is invalid.", nameof(sourceId));
        }
        if (!IsSafeVideoToolsJobId(sourceVideoJobId))
        {
            throw new ArgumentException(
                "Source video Job ID is invalid.",
                nameof(sourceVideoJobId));
        }
    }

    private static bool IsSafeVideoToolsJobId(string value)
    {
        if (value.Length == 32)
        {
            return value.All(static character =>
                character is >= 'a' and <= 'f'
                    or >= 'A' and <= 'F'
                    or >= '0' and <= '9');
        }

        return Guid.TryParseExact(value, "D", out Guid parsed)
            && string.Equals(
                value,
                parsed.ToString("D"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadOptionalVideoToolsSourceJobId(
        JsonElement job,
        out string? sourceVideoJobId)
    {
        sourceVideoJobId = null;
        int count = 0;
        foreach (JsonProperty property in job.EnumerateObject())
        {
            if (!property.NameEquals("sourceVideoJobId"))
                continue;
            count++;
            if (count > 1
                || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not string value
                || !IsSafeVideoToolsJobId(value))
            {
                return false;
            }
            sourceVideoJobId = value;
        }
        return true;
    }

    public static JsonElement BuildVideoToolsRetakeRequestForSmoke(
        string sourceId,
        string sourceVideoJobId,
        VideoRetakePlanSmokeSnapshot plan,
        string prompt,
        int steps,
        int maximumPixelArea)
        => BuildVideoToolsRetakeRequest(
            sourceId,
            sourceVideoJobId,
            plan,
            prompt,
            steps,
            maximumPixelArea);

    public static JsonElement BuildVideoToolsFinishRequestForSmoke(
        string sourceId,
        string sourceVideoJobId,
        string mode)
        => TryParseVideoFinishMode(mode, out VideoFinishMode parsed)
            ? BuildVideoToolsFinishRequest(
                sourceId,
                sourceVideoJobId,
                parsed)
            : throw new ArgumentException(
                "Finish mode must be faithful or detail.",
                nameof(mode));

    private static bool IsVideoToolsSourceWithinBounds(
        bool exactManagedVideoValidated,
        bool isExactMiniMaxH3,
        double durationSeconds,
        int playbackFps,
        int frameCount,
        int width,
        int height,
        long outputSize,
        VideoToolsKind kind)
    {
        if (!exactManagedVideoValidated
            || outputSize is <= 0 or > VideoToolsMaximumSourceBytes
            || playbackFps is not (24 or 30)
            || frameCount is <= 0 or > VideoToolsMaximumSourceFrames
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0
            || durationSeconds > VideoToolsMaximumSourceDurationSeconds
            || durationSeconds != (double)frameCount / playbackFps
            || width <= 0
            || height <= 0
            || width > 1_920
            || height > 1_080)
        {
            return false;
        }

        try
        {
            if (checked(width * height) > VideoToolsMaximumSourcePixelArea)
                return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        return kind != VideoToolsKind.Retake
            || (isExactMiniMaxH3
                && playbackFps == VideoToolsPlaybackFps
                && VideoToolsRetakeFrameCounts.Contains(frameCount));
    }

    public static bool IsVideoToolsSourceWithinBoundsForSmoke(
        bool exactManagedVideoValidated,
        bool isExactMiniMaxH3,
        double durationSeconds,
        int playbackFps,
        int frameCount,
        int width,
        int height,
        long outputSize,
        string kind)
        => (string.Equals(kind, "retake", StringComparison.Ordinal)
                || string.Equals(kind, "finish", StringComparison.Ordinal))
            && IsVideoToolsSourceWithinBounds(
                exactManagedVideoValidated,
                isExactMiniMaxH3,
                durationSeconds,
                playbackFps,
                frameCount,
                width,
                height,
                outputSize,
                string.Equals(kind, "retake", StringComparison.Ordinal)
                    ? VideoToolsKind.Retake
                    : VideoToolsKind.Finish);

    private bool TryCaptureDisplayedVideoToolsSource(
        VideoToolsKind kind,
        out VideoToolsSourceChoice source,
        out string error)
    {
        source = null!;
        error = VideoToolsText("UiVideoToolsSourceMissing");
        if (_videoToolsSourceForSmoke is VideoToolsSourceChoice smokeSource)
        {
            if (!IsSafeVideoToolsJobId(smokeSource.SourceVideoJobId)
                || !IsVideoToolsSourceWithinBounds(
                    exactManagedVideoValidated: true,
                    smokeSource.IsExactMiniMaxH3,
                    smokeSource.DurationSeconds,
                    smokeSource.PlaybackFps,
                    smokeSource.FrameCount,
                    smokeSource.Width,
                    smokeSource.Height,
                    smokeSource.OutputSize,
                    kind))
            {
                return false;
            }
            source = smokeSource;
            error = "";
            return true;
        }
        if (!TryGetModalSourceTile(out Tile tile)
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion video)
            || !IsSafeVideoToolsJobId(video.JobId)
            || !TryResolveEnhancementSourceIdentity(
                tile.Path,
                out string sourceId))
        {
            return false;
        }

        try
        {
            var output = new FileInfo(video.Output.OutputPath);
            if (!output.Exists
                || !IsVideoToolsSourceWithinBounds(
                    exactManagedVideoValidated: true,
                    video.IsMiniMaxH3,
                    video.DurationSeconds,
                    video.PlaybackFps,
                    video.FrameCount,
                    video.Width,
                    video.Height,
                    output.Length,
                    kind))
            {
                error = kind == VideoToolsKind.Retake
                    ? VideoToolsText("UiVideoToolsRetakeSourceUnsupported")
                    : VideoToolsText("UiVideoToolsSourceOutOfBounds");
                return false;
            }

            source = new VideoToolsSourceChoice(
                sourceId,
                video.JobId,
                output.FullName,
                video.IsMiniMaxH3,
                video.DurationSeconds,
                video.PlaybackFps,
                video.FrameCount,
                video.Width,
                video.Height,
                video.Delivery?.Audio == true,
                video.RequestedPrompt,
                video.Steps,
                video.MaximumPixelArea,
                output.Length,
                new DateTimeOffset(output.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds());
            error = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                or OverflowException)
        {
            return false;
        }
    }

    private bool TryRevalidateVideoToolsSource(
        VideoToolsSourceChoice captured,
        VideoToolsKind kind,
        out VideoToolsSourceChoice current,
        out string error)
    {
        current = null!;
        if (!TryCaptureDisplayedVideoToolsSource(kind, out current, out error)
            || !string.Equals(
                current.SourceId,
                captured.SourceId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                current.SourceVideoJobId,
                captured.SourceVideoJobId,
                StringComparison.Ordinal)
            || !string.Equals(
                current.OutputPath,
                captured.OutputPath,
                StringComparison.OrdinalIgnoreCase)
            || current.OutputSize != captured.OutputSize
            || Math.Abs(current.OutputMtimeMs - captured.OutputMtimeMs) > 1)
        {
            if (string.IsNullOrWhiteSpace(error))
                error = VideoToolsText("UiVideoToolsSourceChanged");
            return false;
        }
        return true;
    }

    private void SyncModalVideoToolsEntryPresentation()
    {
        if (ModalVideoRetakeButton is null || ModalVideoFinishButton is null)
            return;

        bool retakeReady = TryCaptureDisplayedVideoToolsSource(
            VideoToolsKind.Retake,
            out _,
            out _);
        bool finishReady = TryCaptureDisplayedVideoToolsSource(
            VideoToolsKind.Finish,
            out _,
            out _);
        ModalVideoRetakeButton.Visibility = retakeReady
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoFinishButton.Visibility = finishReady
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!retakeReady && _videoToolsKind == VideoToolsKind.Retake)
            CloseVideoToolsBoard(restoreFocus: false);
        else if (!finishReady && _videoToolsKind == VideoToolsKind.Finish)
            CloseVideoToolsBoard(restoreFocus: false);
    }

    private void OpenModalVideoRetake_Click(object sender, RoutedEventArgs e)
        => OpenVideoToolsBoard(VideoToolsKind.Retake);

    private void OpenModalVideoFinish_Click(object sender, RoutedEventArgs e)
        => OpenVideoToolsBoard(VideoToolsKind.Finish);

    private void OpenVideoToolsBoard(VideoToolsKind kind)
    {
        if (ModalVideoToolsPopup is null)
            return;
        if (!TryCaptureDisplayedVideoToolsSource(
                kind,
                out VideoToolsSourceChoice source,
                out string error))
        {
            SetTransientStatusToast(error);
            return;
        }

        _videoToolsActionGeneration++;
        _videoToolsKind = kind;
        _videoToolsSource = source;
        _videoToolsCapability = null;
        _videoFinishMode = VideoFinishMode.Faithful;
        _syncingVideoToolsControls = true;
        try
        {
            VideoToolsTitleText.Text = kind == VideoToolsKind.Retake
                ? VideoToolsText("UiVideoToolsRetakeAction")
                : VideoToolsText("UiVideoToolsFinishAction");
            VideoToolsSourceText.Text = VideoToolsFormat(
                "UiVideoToolsSourceFormat",
                Path.GetFileName(source.OutputPath),
                source.PlaybackFps,
                source.FrameCount,
                source.DurationSeconds.ToString(
                    "0.000",
                    CultureInfo.InvariantCulture));
            VideoToolsRetakePanel.Visibility = kind == VideoToolsKind.Retake
                ? Visibility.Visible
                : Visibility.Collapsed;
            VideoToolsFinishPanel.Visibility = kind == VideoToolsKind.Finish
                ? Visibility.Visible
                : Visibility.Collapsed;

            double defaultEnd = Math.Min(
                source.DurationSeconds,
                (double)VideoToolsRetakeFrameCounts[0]
                    / VideoToolsPlaybackFps);
            VideoToolsSelectionStartTextBox.Text = "0.000";
            VideoToolsSelectionEndTextBox.Text =
                defaultEnd.ToString("R", CultureInfo.InvariantCulture);
            VideoToolsPromptTextBox.Text = source.Prompt;
            VideoToolsStepsTextBox.Text = Math.Clamp(
                    source.Steps,
                    VideoToolsMinimumSteps,
                    VideoToolsMaximumSteps)
                .ToString(CultureInfo.InvariantCulture);
            SelectVideoToolsCanvasTier(
                VideoToolsCanvasTiers.Contains(source.MaximumPixelArea)
                    ? source.MaximumPixelArea
                    : VideoToolsDefaultMaximumPixelArea);
            SelectVideoToolsFinishMode(VideoFinishMode.Faithful);
        }
        finally
        {
            _syncingVideoToolsControls = false;
        }

        if (ModalUpscaleSettingsPopup is not null)
            ModalUpscaleSettingsPopup.Visibility = Visibility.Collapsed;
        if (ModalPhotorealSettingsPopup is not null)
            ModalPhotorealSettingsPopup.Visibility = Visibility.Collapsed;
        if (ModalVideoGenerationPopup is not null)
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
        ModalVideoToolsPopup.Visibility = Visibility.Visible;
        SyncVideoToolsControls();
        _ = RefreshVideoToolsCapabilityAsync();
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModalVideoToolsPopup.Visibility == Visibility.Visible)
                {
                    Keyboard.Focus(kind == VideoToolsKind.Retake
                        ? VideoToolsSelectionStartTextBox
                        : VideoToolsFinishModeComboBox);
                }
            }),
            DispatcherPriority.Input);
    }

    private async Task RefreshVideoToolsCapabilityAsync()
    {
        long generation = ++_videoToolsHealthGeneration;
        // Passive read performs only companion ownership verification and an
        // authenticated GET. It never launches, wakes, recovers, or pumps.
        EnhancementApiResponse response =
            await SendPassiveEnhancementReadAsync("api/enhance/health");
        if (generation != _videoToolsHealthGeneration
            || ModalVideoToolsPopup?.Visibility != Visibility.Visible)
        {
            return;
        }

        _videoToolsCapability = response.Ok
            && response.Payload is JsonElement payload
            && TryParseVideoToolsCapability(payload, out var capability)
                ? capability
                : null;
        SyncVideoToolsControls();
    }

    private void VideoToolsInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingVideoToolsControls)
        {
            _videoToolsActionGeneration++;
            SyncVideoToolsControls();
        }
    }

    private void VideoToolsFinishMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoToolsControls)
            return;
        string? mode = (VideoToolsFinishModeComboBox.SelectedItem
            as ComboBoxItem)?.Tag?.ToString();
        if (TryParseVideoFinishMode(mode, out VideoFinishMode parsed))
            _videoFinishMode = parsed;
        _videoToolsActionGeneration++;
        SyncVideoToolsControls();
    }

    private void SelectVideoToolsFinishMode(VideoFinishMode mode)
    {
        string id = mode == VideoFinishMode.Detail ? "detail" : "faithful";
        VideoToolsFinishModeComboBox.SelectedItem =
            VideoToolsFinishModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    id,
                    StringComparison.Ordinal));
    }

    private void SelectVideoToolsCanvasTier(int maximumPixelArea)
    {
        VideoToolsCanvasTierComboBox.SelectedItem =
            VideoToolsCanvasTierComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => int.TryParse(
                        item.Tag?.ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value)
                    && value == maximumPixelArea)
            ?? VideoToolsCanvasTierComboBox.Items
                .OfType<ComboBoxItem>()
                .LastOrDefault();
    }

    private bool TryReadVideoToolsRetakeInputs(
        VideoToolsSourceChoice source,
        out VideoRetakePlanSmokeSnapshot plan,
        out string prompt,
        out int steps,
        out int maximumPixelArea,
        out string error)
    {
        plan = default;
        prompt = VideoToolsPromptTextBox.Text ?? "";
        steps = 0;
        maximumPixelArea = 0;
        error = "";
        if (!double.TryParse(
                VideoToolsSelectionStartTextBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double startSeconds)
            || !double.TryParse(
                VideoToolsSelectionEndTextBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double endSeconds)
            || !TryPlanVideoRetake(
                source.FrameCount,
                startSeconds,
                endSeconds,
                out plan))
        {
            error = VideoToolsFormat(
                "UiVideoToolsRetakeRangeErrorFormat",
                source.DurationSeconds.ToString(
                    "0.000",
                    CultureInfo.InvariantCulture));
            return false;
        }
        if (!TryNormalizeAndValidateVideoToolsRetakePrompt(prompt, out _))
        {
            error = VideoToolsText("UiVideoToolsRetakePromptError");
            return false;
        }
        if (!int.TryParse(
                VideoToolsStepsTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out steps)
            || steps is < VideoToolsMinimumSteps or > VideoToolsMaximumSteps)
        {
            error = VideoToolsText("UiVideoToolsStepsError");
            return false;
        }
        if (!int.TryParse(
                (VideoToolsCanvasTierComboBox.SelectedItem as ComboBoxItem)
                    ?.Tag?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out maximumPixelArea)
            || !VideoToolsCanvasTiers.Contains(maximumPixelArea))
        {
            error = VideoToolsText("UiVideoToolsCanvasError");
            return false;
        }
        return true;
    }

    private void SyncVideoToolsControls()
    {
        if (VideoToolsStartButton is null
            || VideoToolsStatusText is null
            || _videoToolsSource is not VideoToolsSourceChoice captured)
        {
            return;
        }

        bool sourceCurrent = TryRevalidateVideoToolsSource(
            captured,
            _videoToolsKind,
            out VideoToolsSourceChoice source,
            out string sourceError);
        bool planReady;
        string planText;
        if (!sourceCurrent)
        {
            planReady = false;
            planText = sourceError;
        }
        else if (_videoToolsKind == VideoToolsKind.Retake)
        {
            planReady = TryReadVideoToolsRetakeInputs(
                source,
                out VideoRetakePlanSmokeSnapshot plan,
                out _,
                out _,
                out _,
                out string planError);
            bool intervalReady = plan.ActualFrameCount > 0;
            planText = intervalReady
                ? BuildVideoRetakePreviewText(source, plan)
                : planError;
            VideoToolsRetakePlanText.Text = planText;
            if (intervalReady && !planReady)
                planText = planError;
        }
        else
        {
            planReady = TryPlanVideoFinish(
                _videoFinishMode,
                source.Width,
                source.Height,
                source.PlaybackFps,
                source.FrameCount,
                source.DurationSeconds,
                source.Audio,
                out VideoFinishPlanSmokeSnapshot plan);
            planText = planReady
                ? BuildVideoFinishPreviewText(plan)
                : VideoToolsText("UiVideoToolsFinishLimitError");
            VideoToolsFinishPlanText.Text = planText;
        }

        VideoToolsFeatureCapability? feature = _videoToolsCapability is { } all
            ? _videoToolsKind == VideoToolsKind.Retake
                ? all.Retake
                : all.Finish
            : null;
        string availability = feature is null
            ? VideoToolsText("UiVideoToolsCapabilityUnavailable")
            : feature.Value.Ready
                ? VideoToolsText("UiVideoToolsRuntimeReady")
                : DescribeVideoToolsReason(feature.Value.ReasonCode);
        bool retakeReady = _videoToolsKind == VideoToolsKind.Retake
            && feature is { Ready: true };
        bool canStart = retakeReady
            && !_videoToolsRequestPending
            && sourceCurrent
            && planReady;
        VideoToolsStatusText.Text = _videoToolsRequestPending
            ? VideoToolsText("UiVideoToolsRetakePreparing")
            : planReady
                ? availability
                : planText;
        VideoToolsStartButton.IsEnabled = canStart;
        VideoToolsStartButton.Content = _videoToolsKind == VideoToolsKind.Retake
            ? _videoToolsRequestPending
                ? VideoToolsText("UiVideoToolsRetakeStartPending")
                : retakeReady
                    ? VideoToolsText("UiVideoToolsRetakeStartReady")
                    : VideoToolsText("UiVideoToolsStartButton")
            : VideoToolsText("UiVideoToolsStartButton");
        AutomationProperties.SetName(
            VideoToolsStartButton,
            VideoToolsStartButton.Content?.ToString() ?? "");
        AutomationProperties.SetHelpText(
            VideoToolsStartButton,
            retakeReady
                ? VideoToolsText("UiVideoToolsRetakeStartHelp")
                : VideoToolsText("UiVideoToolsStartHelp"));
        bool inputsEnabled = !_videoToolsRequestPending;
        VideoToolsSelectionStartTextBox.IsEnabled = inputsEnabled;
        VideoToolsSelectionEndTextBox.IsEnabled = inputsEnabled;
        VideoToolsPromptTextBox.IsEnabled = inputsEnabled;
        VideoToolsStepsTextBox.IsEnabled = inputsEnabled;
        VideoToolsCanvasTierComboBox.IsEnabled = inputsEnabled;
        VideoToolsFinishModeComboBox.IsEnabled = inputsEnabled;
    }

    private string BuildVideoRetakePreviewText(
        VideoToolsSourceChoice source,
        VideoRetakePlanSmokeSnapshot plan)
        => VideoToolsFormat(
            "UiVideoToolsRetakePreviewFormat",
            plan.SelectionStartSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            plan.SelectionEndSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            plan.SelectionStartFrame,
            plan.SelectionEndFrameExclusive - 1,
            plan.ActualStartSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            plan.ActualEndSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            plan.ActualFrameCount,
            plan.FirstAnchorFrame,
            plan.LastAnchorFrame,
            source.FrameCount,
            source.DurationSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            VideoToolsText(source.Audio
                ? "UiVideoToolsAudioOriginal"
                : "UiVideoToolsAudioNone"));

    private string BuildVideoFinishPreviewText(
        VideoFinishPlanSmokeSnapshot plan)
        => VideoToolsFormat(
            "UiVideoToolsFinishPreviewFormat",
            plan.SourceWidth,
            plan.SourceHeight,
            plan.OutputWidth,
            plan.OutputHeight,
            plan.PlaybackFps,
            plan.FrameCount,
            plan.DurationSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture),
            VideoToolsText(plan.AudioPreserved
                ? "UiVideoToolsAudioOriginal"
                : "UiVideoToolsAudioNone"));

    private string DescribeVideoToolsReason(string? reasonCode)
        => reasonCode switch
        {
            "RETAKE_RUNTIME_UNPINNED" =>
                VideoToolsText("UiVideoToolsRetakeRuntimeUnpinned"),
            "FINISH_RUNTIME_UNPINNED" =>
                VideoToolsText("UiVideoToolsFinishRuntimeUnpinned"),
            "RETAKE_WORKFLOW_INPUT_UNVERIFIED" or
            "FINISH_WORKFLOW_INPUT_UNVERIFIED" =>
                VideoToolsText("UiVideoToolsWorkflowUnverified"),
            "RETAKE_MODEL_LICENSE_RECEIPT_MISSING" or
            "FINISH_MODEL_LICENSE_RECEIPT_MISSING" =>
                VideoToolsText("UiVideoToolsLicenseMissing"),
            "RETAKE_GPU_CANARY_MISSING" or
            "FINISH_GPU_CANARY_MISSING" =>
                VideoToolsText("UiVideoToolsGpuCanaryMissing"),
            "RETAKE_OUTPUT_CANARY_MISSING" or
            "FINISH_OUTPUT_CANARY_MISSING" =>
                VideoToolsText("UiVideoToolsOutputCanaryMissing"),
            _ => VideoToolsText("UiVideoToolsRuntimeUnavailable"),
        };

    private string? ValidateExactVideoToolsRetakeHealth(JsonElement payload)
    {
        if (!TryParseVideoToolsCapability(payload, out var capability))
            return VideoToolsText("UiVideoToolsCapabilityUnavailable");
        return capability.Retake.Ready
            ? null
            : DescribeVideoToolsReason(capability.Retake.ReasonCode);
    }

    private bool TryCaptureVideoToolsRetakeModalContext(
        VideoToolsSourceChoice source,
        out Tile? modalTile,
        out string? modalSourcePath)
    {
        modalTile = null;
        modalSourcePath = null;
        if (_videoToolsSourceForSmoke is not null)
            return true;
        if (!TryGetModalSourceTile(out Tile tile)
            || !TryGetDisplayedModalVideoVersion(
                tile,
                out ManagedVideoVersion video)
            || !string.Equals(
                video.JobId,
                source.SourceVideoJobId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                video.Output.OutputPath,
                source.OutputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        modalTile = tile;
        modalSourcePath = tile.Path;
        return true;
    }

    private string? ValidateVideoToolsRetakeActionBeforePublish(
        VideoToolsRetakeActionContext context)
    {
        string stale = VideoToolsText("UiVideoToolsRetakeStale");
        if (!_videoToolsRequestPending
            || context.Generation != _videoToolsActionGeneration
            || _videoToolsKind != VideoToolsKind.Retake
            || ModalVideoToolsPopup?.Visibility != Visibility.Visible
            || _videoToolsSource is not VideoToolsSourceChoice captured
            || !Equals(captured, context.Source)
            || !TryRevalidateVideoToolsSource(
                context.Source,
                VideoToolsKind.Retake,
                out VideoToolsSourceChoice current,
                out _)
            || !Equals(current, context.Source)
            || !TryReadVideoToolsRetakeInputs(
                current,
                out VideoRetakePlanSmokeSnapshot plan,
                out string prompt,
                out int steps,
                out int maximumPixelArea,
                out _)
            || !TryNormalizeAndValidateVideoToolsRetakePrompt(
                prompt,
                out string normalizedPrompt)
            || !Equals(plan, context.Plan)
            || !string.Equals(
                normalizedPrompt,
                context.Prompt,
                StringComparison.Ordinal)
            || steps != context.Steps
            || maximumPixelArea != context.MaximumPixelArea
            || !TryCaptureVideoSourceStamp(
                context.DependencySource,
                out VideoH3SourceStamp currentStamp)
            || !VideoH3SourceStampsEqual(
                currentStamp,
                context.SourceStamp))
        {
            return stale;
        }

        if (context.ModalTile is Tile modalTile
            && (!TryGetModalSourceTile(out Tile currentTile)
                || !ReferenceEquals(currentTile, modalTile)
                || !string.Equals(
                    currentTile.Path,
                    context.ModalSourcePath,
                    StringComparison.OrdinalIgnoreCase)
                || !TryGetDisplayedModalVideoVersion(
                    currentTile,
                    out ManagedVideoVersion video)
                || !string.Equals(
                    video.JobId,
                    context.Source.SourceVideoJobId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    video.Output.OutputPath,
                    context.Source.OutputPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return stale;
        }
        return null;
    }

    private async Task<string?> ValidateVideoToolsRetakeActionBeforePublishAsync(
        VideoToolsRetakeActionContext context,
        CancellationToken token)
    {
        string? error = ValidateVideoToolsRetakeActionBeforePublish(context);
        if (error is not null)
            return error;
        try
        {
            string sourceSha256 = await ComputeVideoH3SourceSha256Async(
                context.Source.OutputPath,
                token);
            if (!string.Equals(
                    sourceSha256,
                    context.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !TryCaptureVideoSourceStamp(
                    context.DependencySource,
                    out VideoH3SourceStamp finalStamp)
                || !VideoH3SourceStampsEqual(
                    finalStamp,
                    context.SourceStamp))
            {
                return VideoToolsText("UiVideoToolsRetakeStale");
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return VideoToolsText("UiVideoToolsRetakeStale");
        }
        return ValidateVideoToolsRetakeActionBeforePublish(context);
    }

    private async Task<bool> StartVideoToolsRetakeAsync()
    {
        if (_videoToolsRequestPending)
            return false;
        SyncVideoToolsControls();
        if (_videoToolsKind != VideoToolsKind.Retake
            || VideoToolsStartButton?.IsEnabled != true
            || _videoToolsCapability is not { Retake.Ready: true }
            || _videoToolsSource is not VideoToolsSourceChoice captured)
        {
            if (ModalVideoToolsPopup?.Visibility == Visibility.Visible)
            {
                VideoToolsStatusText.Text = VideoToolsText(
                    "UiVideoToolsRuntimeUnavailable");
            }
            return false;
        }
        if (!TryRevalidateVideoToolsSource(
                captured,
                VideoToolsKind.Retake,
                out VideoToolsSourceChoice source,
                out string sourceError))
        {
            VideoToolsStatusText.Text = string.IsNullOrWhiteSpace(sourceError)
                ? VideoToolsText("UiVideoToolsRetakeStale")
                : sourceError;
            return false;
        }
        if (!TryReadVideoToolsRetakeInputs(
                source,
                out VideoRetakePlanSmokeSnapshot plan,
                out string prompt,
                out int steps,
                out int maximumPixelArea,
                out string inputError)
            || !TryNormalizeAndValidateVideoToolsRetakePrompt(
                prompt,
                out string normalizedPrompt))
        {
            VideoToolsStatusText.Text = string.IsNullOrWhiteSpace(inputError)
                ? VideoToolsText("UiVideoToolsRetakePromptError")
                : inputError;
            return false;
        }
        if (!TryCaptureVideoToolsRetakeModalContext(
                source,
                out Tile? modalTile,
                out string? modalSourcePath))
        {
            VideoToolsStatusText.Text = VideoToolsText(
                "UiVideoToolsRetakeStale");
            return false;
        }

        var dependencySource = new VideoSourceChoice(
            source.SourceId,
            source.OutputPath,
            source.SourceVideoJobId,
            "Video Tools Retake",
            UsesDisplayedFileDirectly: true);
        if (!TryCaptureVideoSourceStamp(
                dependencySource,
                out VideoH3SourceStamp sourceStamp))
        {
            VideoToolsStatusText.Text = VideoToolsText(
                "UiVideoToolsRetakeStale");
            return false;
        }

        long generation = ++_videoToolsActionGeneration;
        _videoToolsRequestPending = true;
        string? pendingDeliveryRequestId = null;
        string? actionStatus = null;
        SyncVideoToolsControls();
        try
        {
            string sourceSha256 = await ComputeVideoH3SourceSha256Async(
                source.OutputPath,
                CancellationToken.None);
            if (!TryCaptureVideoSourceStamp(
                    dependencySource,
                    out VideoH3SourceStamp hashedStamp)
                || !VideoH3SourceStampsEqual(sourceStamp, hashedStamp))
            {
                actionStatus = VideoToolsText("UiVideoToolsRetakeStale");
                return false;
            }

            var context = new VideoToolsRetakeActionContext(
                generation,
                source,
                dependencySource,
                plan,
                normalizedPrompt,
                steps,
                maximumPixelArea,
                modalTile,
                modalSourcePath,
                hashedStamp,
                sourceSha256);
            string? validationError =
                ValidateVideoToolsRetakeActionBeforePublish(context);
            if (validationError is not null)
            {
                actionStatus = validationError;
                return false;
            }

            JsonElement requestBody = BuildVideoToolsRetakeRequest(
                source.SourceId,
                source.SourceVideoJobId,
                plan,
                normalizedPrompt,
                steps,
                maximumPixelArea);
            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                requestBody,
                includeQueuePlacementInBody: false,
                healthValidator: ValidateExactVideoToolsRetakeHealth,
                requireExactHealthValidation: true,
                recoverySourceIdentity: source.SourceId,
                prePublishValidator: () =>
                    ValidateVideoToolsRetakeActionBeforePublish(context),
                asyncPrePublishValidator: token =>
                    ValidateVideoToolsRetakeActionBeforePublishAsync(
                        context,
                        token),
                onBeforeDurablePublish: item =>
                {
                    IDisposable publishLease =
                        AcquireVideoDurablePublishLease(
                            () => PinVideoSourceForDurablePublish(
                                context.SourceStamp));
                    try
                    {
                        pendingDeliveryRequestId = item.RequestId;
                        RecordPendingVideoSourceDependency(
                            item.RequestId,
                            context.DependencySource);
                        UpdateModalDisplayedDeletePresentation();
                        return publishLease;
                    }
                    catch
                    {
                        publishLease.Dispose();
                        throw;
                    }
                });

            bool hasJob = response.Ok
                && response.Payload is JsonElement payload
                && payload.TryGetProperty("job", out JsonElement job)
                && job.ValueKind == JsonValueKind.Object;
            if (response.SavedForDelivery || hasJob)
            {
                RecordActiveVideoSourceDependency(dependencySource);
                UpdateModalDisplayedDeletePresentation();
                string message = response.SavedForDelivery
                    ? VideoToolsText("UiVideoToolsRetakeSavedForDelivery")
                    : VideoToolsText("UiVideoToolsRetakeQueued");
                SetTransientStatusToast(message);
                if (generation == _videoToolsActionGeneration
                    && ModalVideoToolsPopup?.Visibility == Visibility.Visible)
                {
                    VideoToolsStatusText.Text = message;
                    CloseVideoToolsBoard(restoreFocus: true);
                }
                QueueEnhancedStateRefreshIfChanged();
                return true;
            }

            actionStatus = string.IsNullOrWhiteSpace(response.Error)
                ? VideoToolsText("UiVideoToolsRuntimeUnavailable")
                : response.Error;
            return false;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            actionStatus = VideoToolsText("UiVideoToolsRetakeStale");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingDeliveryRequestId))
                _pendingVideoSourceDependencies.Remove(pendingDeliveryRequestId);
            _videoToolsRequestPending = false;
            UpdateModalDisplayedDeletePresentation();
            SyncVideoToolsControls();
            if (actionStatus is not null
                && ModalVideoToolsPopup?.Visibility == Visibility.Visible)
            {
                VideoToolsStatusText.Text = actionStatus;
            }
        }
    }

    private async void StartVideoTools_Click(object sender, RoutedEventArgs e)
    {
        if (_videoToolsKind == VideoToolsKind.Retake)
            _ = await StartVideoToolsRetakeAsync();
    }

    private void CloseVideoTools_Click(object sender, RoutedEventArgs e)
        => CloseVideoToolsBoard(restoreFocus: true);

    private void ModalVideoToolsBackdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            CloseVideoToolsBoard(restoreFocus: true);
    }

    private void CloseVideoToolsBoard(bool restoreFocus)
    {
        _videoToolsHealthGeneration++;
        _videoToolsActionGeneration++;
        _videoToolsSource = null;
        _videoToolsCapability = null;
        if (ModalVideoToolsPopup is not null)
            ModalVideoToolsPopup.Visibility = Visibility.Collapsed;
        if (restoreFocus)
        {
            (_videoToolsKind == VideoToolsKind.Retake
                ? ModalVideoRetakeButton
                : ModalVideoFinishButton)?.Focus();
        }
    }

    public bool VideoToolsEntryVisibleForSmoke(string kind)
        => string.Equals(kind, "retake", StringComparison.Ordinal)
            ? ModalVideoRetakeButton?.Visibility == Visibility.Visible
            : string.Equals(kind, "finish", StringComparison.Ordinal)
                && ModalVideoFinishButton?.Visibility == Visibility.Visible;

    public bool VideoToolsBoardVisibleForSmoke
        => ModalVideoToolsPopup?.Visibility == Visibility.Visible;

    public bool VideoToolsStartEnabledForSmoke
        => VideoToolsStartButton?.IsEnabled == true;

    public bool VideoToolsRequestPendingForSmoke
        => _videoToolsRequestPending;

    public string VideoToolsStartContentForSmoke
        => VideoToolsStartButton?.Content?.ToString() ?? "";

    public string VideoToolsStartHelpForSmoke
        => VideoToolsStartButton is null
            ? ""
            : AutomationProperties.GetHelpText(VideoToolsStartButton);

    public string VideoToolsPlanForSmoke
        => _videoToolsKind == VideoToolsKind.Retake
            ? VideoToolsRetakePlanText?.Text ?? ""
            : VideoToolsFinishPlanText?.Text ?? "";

    public string VideoToolsTitleForSmoke
        => VideoToolsTitleText?.Text ?? "";

    public string VideoToolsSourceSummaryForSmoke
        => VideoToolsSourceText?.Text ?? "";

    public string VideoToolsStatusForSmoke
        => VideoToolsStatusText?.Text ?? "";

    public string[] VideoToolsCanvasLabelsForSmoke
        => VideoToolsCanvasTierComboBox?.Items
            .OfType<ComboBoxItem>()
            .Select(static item => item.Content?.ToString() ?? "")
            .ToArray() ?? [];

    public void ConfigureVideoToolsSourceForSmoke(
        string sourceVideoJobId,
        bool isExactMiniMaxH3,
        double durationSeconds,
        int playbackFps,
        int frameCount,
        int width,
        int height,
        bool audio,
        string prompt,
        string? outputPath = null)
    {
        string resolvedOutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? @"C:\synthetic\managed-video.mp4"
            : Path.GetFullPath(outputPath);
        long outputSize = 1_024;
        double outputMtimeMs = 1_000;
        if (File.Exists(resolvedOutputPath))
        {
            var info = new FileInfo(resolvedOutputPath);
            outputSize = info.Length;
            outputMtimeMs = new DateTimeOffset(info.LastWriteTimeUtc)
                .ToUnixTimeMilliseconds();
        }
        _videoToolsSourceForSmoke = new VideoToolsSourceChoice(
            "synthetic-source",
            sourceVideoJobId,
            resolvedOutputPath,
            isExactMiniMaxH3,
            durationSeconds,
            playbackFps,
            frameCount,
            width,
            height,
            audio,
            prompt,
            Steps: VideoToolsDefaultSteps,
            MaximumPixelArea: VideoToolsDefaultMaximumPixelArea,
            OutputSize: outputSize,
            OutputMtimeMs: outputMtimeMs);
        SyncModalVideoToolsEntryPresentation();
    }

    public void SetVideoToolsRetakeSelectionForSmoke(
        string startSeconds,
        string endSeconds)
    {
        VideoToolsSelectionStartTextBox.Text = startSeconds;
        VideoToolsSelectionEndTextBox.Text = endSeconds;
    }

    public bool VideoToolsSourceDependencyProtectedForSmoke(
        string sourceVideoJobId,
        string outputPath)
    {
        string? normalized = NormalizeEnhancementDependencyPath(outputPath);
        if (_activeVideoSourceProducerJobIds.Contains(sourceVideoJobId)
            || normalized is not null
                && _activeVideoManagedSourcePaths.Contains(normalized))
        {
            return true;
        }
        return _pendingVideoSourceDependencies.Values.Any(dependency =>
            !dependency.Complete
            || string.Equals(
                dependency.ProducerJobId,
                sourceVideoJobId,
                StringComparison.OrdinalIgnoreCase)
            || normalized is not null
                && string.Equals(
                    dependency.ManagedSourcePath,
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
    }

    public bool OpenVideoToolsBoardForSmoke(string kind)
    {
        if (string.Equals(kind, "retake", StringComparison.Ordinal))
            OpenVideoToolsBoard(VideoToolsKind.Retake);
        else if (string.Equals(kind, "finish", StringComparison.Ordinal))
            OpenVideoToolsBoard(VideoToolsKind.Finish);
        else
            return false;
        return VideoToolsBoardVisibleForSmoke;
    }

    public async Task RefreshVideoToolsCapabilityForSmokeAsync()
        => await RefreshVideoToolsCapabilityAsync();

    public async Task<bool> StartVideoToolsRetakeForSmokeAsync()
        => await StartVideoToolsRetakeAsync();

    public void CloseVideoToolsBoardForSmoke()
        => CloseVideoToolsBoard(restoreFocus: false);
}

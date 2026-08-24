using System.IO;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal enum VideoFinishV2PlanError
{
    None,
    InvalidMode,
    InvalidScale,
    InvalidSource,
    UnsupportedFps,
    OutputBounds,
}

internal sealed record VideoFinishV2Plan(
    string Mode,
    int Scale,
    int SourceFrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int DurationMs,
    int InputWidth,
    int InputHeight,
    long InputPixelArea,
    int OutputWidth,
    int OutputHeight,
    long OutputPixelArea,
    long EstimatedOutputFrameBytes);

internal static class VideoFinishV2Contract
{
    internal const string ContractId = "PV-ENHANCE-VIDEO-TOOLS-002";
    internal const string Protocol = "aibos-enhancement-video-tools-v2";
    internal const string CapabilityRevision =
        "aibos-video-finish-ready-v1";
    internal const string ModeCapabilityRevision =
        "aibos-video-finish-mode-ready-v1";
    internal const int MaximumSourceIdLength = 32_768;
    internal const long MaximumSafeInteger = 9_007_199_254_740_991;
    internal const long MaximumSourceBytes = 536_870_912;
    internal const int MaximumSourceDurationMs = 300_000;
    internal const int MaximumSourceWidth = 1_920;
    internal const int MaximumSourceHeight = 1_080;
    internal const int MaximumSourcePixelArea = 2_073_600;
    internal const int MaximumSourceFrames = 18_000;
    internal const int MaximumOutputWidth = 3_840;
    internal const int MaximumOutputHeight = 2_160;
    internal const int MaximumOutputPixelArea = 8_294_400;
    internal const long MaximumOutputBytes = 536_870_912;

    private const string BackendId =
        "nvidia-vfx-vsr-1.2-candidate-v1";
    private static readonly string[] AllowedSourceFps =
        ["24/1", "30/1", "60/1"];
    private static readonly int[] SupportedScales = [2, 4];

    private sealed record OverallBounds(
        long MaximumSourceBytes,
        long MaximumSourceDurationMs,
        long MaximumSourceWidth,
        long MaximumSourceHeight,
        long MaximumSourcePixelArea,
        long MaximumSourceFrames,
        IReadOnlySet<string> AllowedFps,
        IReadOnlySet<int> Scales,
        long MaximumOutputWidth,
        long MaximumOutputHeight,
        long MaximumOutputPixelArea,
        long MaximumDecodedFrameBytes,
        long MaximumOutputBytes);

    private sealed record ModeBounds(
        long MaximumSourceBytes,
        long MaximumSourceDurationMs,
        long MaximumSourceWidth,
        long MaximumSourceHeight,
        long MaximumSourcePixelArea,
        long MaximumSourceFrames,
        IReadOnlySet<string> AllowedFps,
        IReadOnlySet<int> Scales);

    internal static bool TryPlan(
        VideoEditV2SourceSummary source,
        string mode,
        int scale,
        out VideoFinishV2Plan plan,
        out VideoFinishV2PlanError error)
    {
        plan = null!;
        error = VideoFinishV2PlanError.None;
        if (mode is not ("fast" or "standard" or "quality"))
        {
            error = VideoFinishV2PlanError.InvalidMode;
            return false;
        }
        if (scale is not (2 or 4))
        {
            error = VideoFinishV2PlanError.InvalidScale;
            return false;
        }
        if (source.FrameCount is <= 0 or > MaximumSourceFrames
            || source.DurationMs is <= 0 or > MaximumSourceDurationMs
            || source.Width is <= 0 or > MaximumSourceWidth
            || source.Height is <= 0 or > MaximumSourceHeight)
        {
            error = VideoFinishV2PlanError.InvalidSource;
            return false;
        }
        if (!IsAllowedFps(source.FpsNumerator, source.FpsDenominator))
        {
            error = VideoFinishV2PlanError.UnsupportedFps;
            return false;
        }

        try
        {
            long inputPixels = checked((long)source.Width * source.Height);
            int outputWidth = checked(source.Width * scale);
            int outputHeight = checked(source.Height * scale);
            long outputPixels = checked((long)outputWidth * outputHeight);
            long outputFrameBytes = checked(outputPixels * 4L);
            if (inputPixels > MaximumSourcePixelArea
                || outputWidth > MaximumOutputWidth
                || outputHeight > MaximumOutputHeight
                || outputPixels > MaximumOutputPixelArea)
            {
                error = VideoFinishV2PlanError.OutputBounds;
                return false;
            }
            plan = new(
                mode,
                scale,
                source.FrameCount,
                source.FpsNumerator,
                source.FpsDenominator,
                source.DurationMs,
                source.Width,
                source.Height,
                inputPixels,
                outputWidth,
                outputHeight,
                outputPixels,
                outputFrameBytes);
            return true;
        }
        catch (OverflowException)
        {
            error = VideoFinishV2PlanError.OutputBounds;
            return false;
        }
    }

    internal static bool TryBuildFinishRequest(
        string sourceId,
        VideoEditV2SourceSelector source,
        VideoFinishV2Plan plan,
        out JsonElement request)
    {
        request = default;
        var summary = new VideoEditV2SourceSummary(
            plan.SourceFrameCount,
            plan.FpsNumerator,
            plan.FpsDenominator,
            plan.DurationMs,
            plan.InputWidth,
            plan.InputHeight);
        if (!IsSafeText(sourceId, MaximumSourceIdLength)
            || !IsValidSource(source)
            || !TryPlan(
                summary,
                plan.Mode,
                plan.Scale,
                out VideoFinishV2Plan canonical,
                out _)
            || canonical != plan)
        {
            return false;
        }

        object sourceBody = source.Kind switch
        {
            "managed-video-job" => new
            {
                kind = "managed-video-job",
                sourceVideoJobId = source.SourceVideoJobId,
            },
            "displayed-file" => new
            {
                kind = "displayed-file",
                path = source.Path,
                size = source.Size,
                mtimeMs = source.MtimeMs,
                sha256 = source.Sha256,
            },
            _ => throw new InvalidOperationException(
                "A validated Video Finish source became unsupported."),
        };
        request = JsonSerializer.SerializeToElement(new
        {
            sourceId,
            operation = "video",
            mediaKind = "video",
            videoTools = new
            {
                schemaVersion = 2,
                kind = "finish",
                source = sourceBody,
                mode = plan.Mode,
                scale = plan.Scale,
            },
        });
        return true;
    }

    internal static bool IsExactReadyHealth(
        JsonElement payload,
        string requestedMode,
        VideoFinishV2Plan plan,
        long? exactSourceBytes)
    {
        if (requestedMode != plan.Mode
            || exactSourceBytes is not long sourceBytes
            || sourceBytes <= 0
            || sourceBytes > MaximumSourceBytes
            || payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty(
                "capabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoToolsV2")
            || !capabilities.TryGetProperty(
                "videoToolsV2",
                out JsonElement videoTools)
            || !HasExactProperties(
                videoTools,
                "contractId",
                "protocol",
                "readerReady",
                "edit",
                "finish",
                "finishModes")
            || !IsExactString(videoTools, "contractId", ContractId)
            || !IsExactString(videoTools, "protocol", Protocol)
            || !IsExactBoolean(videoTools, "readerReady", expected: true)
            || !videoTools.TryGetProperty("edit", out JsonElement edit)
            || edit.ValueKind != JsonValueKind.Object
            || !videoTools.TryGetProperty("finish", out JsonElement finish)
            || !TryParseOverallFinish(finish, out OverallBounds? overall)
            || !videoTools.TryGetProperty(
                "finishModes",
                out JsonElement finishModes)
            || !HasExactProperties(
                finishModes,
                "fast",
                "standard",
                "quality")
            || !finishModes.TryGetProperty(
                requestedMode,
                out JsonElement requested)
            || !TryParseRequestedMode(
                requested,
                requestedMode,
                overall,
                out ModeBounds? mode))
        {
            return false;
        }

        string fps = FormattableString.Invariant(
            $"{plan.FpsNumerator}/{plan.FpsDenominator}");
        return plan.SourceFrameCount <= overall.MaximumSourceFrames
            && plan.DurationMs <= overall.MaximumSourceDurationMs
            && plan.InputWidth <= overall.MaximumSourceWidth
            && plan.InputHeight <= overall.MaximumSourceHeight
            && plan.InputPixelArea <= overall.MaximumSourcePixelArea
            && overall.AllowedFps.Contains(fps)
            && overall.Scales.Contains(plan.Scale)
            && plan.OutputWidth <= overall.MaximumOutputWidth
            && plan.OutputHeight <= overall.MaximumOutputHeight
            && plan.OutputPixelArea <= overall.MaximumOutputPixelArea
            && plan.EstimatedOutputFrameBytes
                <= overall.MaximumDecodedFrameBytes
            && sourceBytes <= overall.MaximumSourceBytes
            && sourceBytes <= mode.MaximumSourceBytes
            && plan.SourceFrameCount <= mode.MaximumSourceFrames
            && plan.DurationMs <= mode.MaximumSourceDurationMs
            && plan.InputWidth <= mode.MaximumSourceWidth
            && plan.InputHeight <= mode.MaximumSourceHeight
            && plan.InputPixelArea <= mode.MaximumSourcePixelArea
            && mode.AllowedFps.Contains(fps)
            && mode.Scales.Contains(plan.Scale);
    }

    private static bool TryParseOverallFinish(
        JsonElement finish,
        out OverallBounds bounds)
    {
        bounds = null!;
        if (!HasExactProperties(
                finish,
                "writerEnabled",
                "backendConfigured",
                "runtimeVerified",
                "ready",
                "state",
                "reasonCode",
                "capabilityRevision",
                "resolvedBackend",
                "receipts",
                "resourceBounds",
                "streamingPolicy",
                "outputPolicy")
            || !IsExactBoolean(finish, "writerEnabled", expected: true)
            || !IsExactBoolean(finish, "backendConfigured", expected: true)
            || !IsExactBoolean(finish, "runtimeVerified", expected: true)
            || !IsExactBoolean(finish, "ready", expected: true)
            || !IsExactString(finish, "state", "ready")
            || !IsExactNull(finish, "reasonCode")
            || !IsExactString(
                finish,
                "capabilityRevision",
                CapabilityRevision)
            || !finish.TryGetProperty(
                "resolvedBackend",
                out JsonElement backend)
            || !TryParseOverallBackend(backend)
            || !finish.TryGetProperty("receipts", out JsonElement receipts)
            || !TryParseOverallReceipts(receipts)
            || !finish.TryGetProperty(
                "resourceBounds",
                out JsonElement resources)
            || !TryParseOverallResources(resources, out bounds)
            || !finish.TryGetProperty(
                "streamingPolicy",
                out JsonElement streaming)
            || !TryParseStreamingPolicy(streaming)
            || !finish.TryGetProperty(
                "outputPolicy",
                out JsonElement output)
            || !TryParseOutputPolicy(output, bounds.MaximumOutputBytes))
        {
            bounds = null!;
            return false;
        }
        return true;
    }

    private static bool TryParseOverallBackend(JsonElement backend)
        => HasExactProperties(
                backend,
                "backendId",
                "semanticRole",
                "backendFamily",
                "package",
                "internalSdkVersion",
                "runnerRevision",
                "outputValidatorRevision",
                "attemptJournalRevision")
            && IsExactString(backend, "backendId", BackendId)
            && IsExactString(backend, "semanticRole", "faithful")
            && IsExactString(
                backend,
                "backendFamily",
                "NVIDIA VFX VideoSuperRes 1.2")
            && IsExactString(backend, "package", "nvidia-vfx 0.1.0.1")
            && IsExactString(backend, "internalSdkVersion", "1.2.0.0")
            && HasSafeAsciiProperty(backend, "runnerRevision", 128)
            && IsExactString(
                backend,
                "outputValidatorRevision",
                "aibos-video-finish-mp4-validator-v1")
            && IsExactString(
                backend,
                "attemptJournalRevision",
                "aibos-video-finish-attempt-journal-v1");

    private static bool TryParseOverallReceipts(JsonElement receipts)
    {
        string[] names =
        [
            "runtimeReceiptId",
            "packageReceiptId",
            "sdkReceiptId",
            "driverGpuReceiptId",
            "runnerReceiptId",
            "frameStreamingReceiptId",
            "timelinePreservationReceiptId",
            "audioPacketCopyReceiptId",
            "sceneCutReceiptId",
            "resourceCanaryReceiptId",
            "cancelCanaryReceiptId",
            "recoveryCanaryReceiptId",
            "journalReceiptId",
            "outputValidatorReceiptId",
        ];
        return HasExactProperties(
                receipts,
                names.Append("receiptSetSha256").ToArray())
            && names.All(name => HasSafeAsciiProperty(receipts, name, 128))
            && HasLowerSha256Property(receipts, "receiptSetSha256");
    }

    private static bool TryParseOverallResources(
        JsonElement resources,
        out OverallBounds bounds)
    {
        bounds = null!;
        if (!HasExactProperties(
                resources,
                "maximumSourceBytes",
                "maximumSourceDurationMs",
                "maximumSourceWidth",
                "maximumSourceHeight",
                "maximumSourcePixelArea",
                "maximumSourceFrames",
                "allowedSourceFps",
                "supportedScales",
                "maximumOutputWidth",
                "maximumOutputHeight",
                "maximumOutputPixelArea",
                "maximumConcurrentGpuJobs",
                "maximumBufferedFrames",
                "maximumDecodedFrameBytes",
                "maximumGpuVramBytes",
                "maximumHostRamBytes",
                "maximumScratchBytes",
                "maximumOutputBytes",
                "processTimeoutMs",
                "cancelGraceMs")
            || !IsExactInteger(
                resources,
                "maximumSourceBytes",
                MaximumSourceBytes)
            || !IsExactInteger(
                resources,
                "maximumSourceDurationMs",
                MaximumSourceDurationMs)
            || !IsExactInteger(
                resources,
                "maximumSourceWidth",
                MaximumSourceWidth)
            || !IsExactInteger(
                resources,
                "maximumSourceHeight",
                MaximumSourceHeight)
            || !IsExactInteger(
                resources,
                "maximumSourcePixelArea",
                MaximumSourcePixelArea)
            || !IsExactInteger(
                resources,
                "maximumSourceFrames",
                MaximumSourceFrames)
            || !TryGetExactStringSet(
                resources,
                "allowedSourceFps",
                AllowedSourceFps,
                out IReadOnlySet<string>? fps)
            || !TryGetExactIntegerSet(
                resources,
                "supportedScales",
                SupportedScales,
                out IReadOnlySet<int>? scales)
            || !IsExactInteger(
                resources,
                "maximumOutputWidth",
                MaximumOutputWidth)
            || !IsExactInteger(
                resources,
                "maximumOutputHeight",
                MaximumOutputHeight)
            || !IsExactInteger(
                resources,
                "maximumOutputPixelArea",
                MaximumOutputPixelArea)
            || !IsExactInteger(
                resources,
                "maximumConcurrentGpuJobs",
                1)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumBufferedFrames",
                out _)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumDecodedFrameBytes",
                out long maximumDecodedFrameBytes)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumGpuVramBytes",
                out _)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumHostRamBytes",
                out _)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumScratchBytes",
                out _)
            || !TryGetPositiveSafeInteger(
                resources,
                "maximumOutputBytes",
                out long maximumOutputBytes)
            || maximumOutputBytes > MaximumOutputBytes
            || !TryGetPositiveSafeInteger(
                resources,
                "processTimeoutMs",
                out _)
            || !TryGetPositiveSafeInteger(
                resources,
                "cancelGraceMs",
                out _))
        {
            return false;
        }
        bounds = new(
            MaximumSourceBytes,
            MaximumSourceDurationMs,
            MaximumSourceWidth,
            MaximumSourceHeight,
            MaximumSourcePixelArea,
            MaximumSourceFrames,
            fps,
            scales,
            MaximumOutputWidth,
            MaximumOutputHeight,
            MaximumOutputPixelArea,
            maximumDecodedFrameBytes,
            maximumOutputBytes);
        return true;
    }

    private static bool TryParseStreamingPolicy(JsonElement streaming)
        => HasExactProperties(
                streaming,
                "revision",
                "boundedFrameStreaming",
                "sourceLengthDependentRetention",
                "maximumConcurrentGpuJobs",
                "frameCountPreserved",
                "rationalFpsPreserved",
                "fullVideoPtsSequencePreserved",
                "durationPreserved",
                "frameInterpolation",
                "frameRateConversion",
                "generatedAudioAllowed",
                "sourceAudioPacketIdentityPreserved")
            && IsExactString(
                streaming,
                "revision",
                "aibos-video-finish-bounded-streaming-v1")
            && IsExactBoolean(
                streaming,
                "boundedFrameStreaming",
                expected: true)
            && IsExactBoolean(
                streaming,
                "sourceLengthDependentRetention",
                expected: false)
            && IsExactInteger(
                streaming,
                "maximumConcurrentGpuJobs",
                1)
            && IsExactBoolean(streaming, "frameCountPreserved", true)
            && IsExactBoolean(streaming, "rationalFpsPreserved", true)
            && IsExactBoolean(
                streaming,
                "fullVideoPtsSequencePreserved",
                true)
            && IsExactBoolean(streaming, "durationPreserved", true)
            && IsExactBoolean(streaming, "frameInterpolation", false)
            && IsExactBoolean(streaming, "frameRateConversion", false)
            && IsExactBoolean(streaming, "generatedAudioAllowed", false)
            && IsExactBoolean(
                streaming,
                "sourceAudioPacketIdentityPreserved",
                true);

    private static bool TryParseOutputPolicy(
        JsonElement output,
        long maximumOutputBytes)
        => HasExactProperties(
                output,
                "revision",
                "container",
                "videoCodec",
                "pixelFormat",
                "bitDepth",
                "dynamicRange",
                "videoStreamCount",
                "maximumAudioStreamCount",
                "subtitleStreamCount",
                "dataStreamCount",
                "attachmentStreamCount",
                "implicitCrop",
                "maximumWidth",
                "maximumHeight",
                "maximumPixelArea",
                "maximumBytes")
            && IsExactString(
                output,
                "revision",
                "aibos-video-finish-mp4-validator-v1")
            && IsExactString(output, "container", "mp4")
            && IsExactString(output, "videoCodec", "h264")
            && IsExactString(output, "pixelFormat", "yuv420p")
            && IsExactInteger(output, "bitDepth", 8)
            && IsExactString(output, "dynamicRange", "SDR")
            && IsExactInteger(output, "videoStreamCount", 1)
            && IsExactInteger(output, "maximumAudioStreamCount", 1)
            && IsExactInteger(output, "subtitleStreamCount", 0)
            && IsExactInteger(output, "dataStreamCount", 0)
            && IsExactInteger(output, "attachmentStreamCount", 0)
            && IsExactBoolean(output, "implicitCrop", false)
            && IsExactInteger(
                output,
                "maximumWidth",
                MaximumOutputWidth)
            && IsExactInteger(
                output,
                "maximumHeight",
                MaximumOutputHeight)
            && IsExactInteger(
                output,
                "maximumPixelArea",
                MaximumOutputPixelArea)
            && IsExactInteger(output, "maximumBytes", maximumOutputBytes);

    private static bool TryParseRequestedMode(
        JsonElement capability,
        string requestedMode,
        OverallBounds overall,
        out ModeBounds bounds)
    {
        bounds = null!;
        if (!HasExactProperties(
                capability,
                "writerEnabled",
                "backendConfigured",
                "runtimeVerified",
                "ready",
                "state",
                "reasonCode",
                "modeCapabilityRevision",
                "mode",
                "resolvedBackend",
                "receipts",
                "sourceBounds",
                "supportedScales",
                "deliveryPolicy")
            || !IsExactBoolean(capability, "writerEnabled", true)
            || !IsExactBoolean(capability, "backendConfigured", true)
            || !IsExactBoolean(capability, "runtimeVerified", true)
            || !IsExactBoolean(capability, "ready", true)
            || !IsExactString(capability, "state", "ready")
            || !IsExactNull(capability, "reasonCode")
            || !IsExactString(
                capability,
                "modeCapabilityRevision",
                ModeCapabilityRevision)
            || !IsExactString(capability, "mode", requestedMode)
            || !capability.TryGetProperty(
                "resolvedBackend",
                out JsonElement backend)
            || !TryParseModeBackend(backend, requestedMode)
            || !capability.TryGetProperty(
                "receipts",
                out JsonElement receipts)
            || !TryParseModeReceipts(receipts)
            || !capability.TryGetProperty(
                "sourceBounds",
                out JsonElement sourceBounds)
            || !TryParseModeSourceBounds(
                sourceBounds,
                overall,
                out bounds)
            || !TryGetExactIntegerSet(
                capability,
                "supportedScales",
                SupportedScales,
                out IReadOnlySet<int>? scales)
            || !capability.TryGetProperty(
                "deliveryPolicy",
                out JsonElement delivery)
            || !TryParseModeDelivery(delivery))
        {
            bounds = null!;
            return false;
        }
        bounds = bounds with { Scales = scales };
        return true;
    }

    private static bool TryParseModeBackend(
        JsonElement backend,
        string requestedMode)
    {
        if (!HasExactProperties(
                backend,
                "backendId",
                "semanticRole",
                "backendSetting",
                "modeMappingRevision",
                "deliveryMappingRevision",
                "sceneCutPolicyRevision")
            || !IsExactString(backend, "backendId", BackendId)
            || !IsExactString(backend, "semanticRole", "faithful")
            || !TryGetSingleString(
                backend,
                "backendSetting",
                out string setting)
            || requestedMode switch
            {
                "fast" => setting is not ("LOW" or "MEDIUM"),
                "standard" => setting != "HIGH",
                "quality" => setting != "ULTRA",
                _ => true,
            }
            || !HasSafeAsciiProperty(
                backend,
                "modeMappingRevision",
                128)
            || !HasSafeAsciiProperty(
                backend,
                "deliveryMappingRevision",
                128)
            || !HasSafeAsciiProperty(
                backend,
                "sceneCutPolicyRevision",
                128))
        {
            return false;
        }
        return true;
    }

    private static bool TryParseModeReceipts(JsonElement receipts)
    {
        string[] names =
        [
            "modeMappingReceiptId",
            "qualityCanaryReceiptId",
            "sourceBoundCanaryReceiptId",
            "scale2CanaryReceiptId",
            "scale4CanaryReceiptId",
            "deliveryMappingReceiptId",
            "sceneCutCanaryReceiptId",
        ];
        return HasExactProperties(
                receipts,
                names.Append("modeReceiptSetSha256").ToArray())
            && names.All(name => HasSafeAsciiProperty(receipts, name, 128))
            && HasLowerSha256Property(receipts, "modeReceiptSetSha256");
    }

    private static bool TryParseModeSourceBounds(
        JsonElement source,
        OverallBounds overall,
        out ModeBounds bounds)
    {
        bounds = null!;
        if (!HasExactProperties(
                source,
                "maximumSourceBytes",
                "maximumSourceDurationMs",
                "maximumSourceWidth",
                "maximumSourceHeight",
                "maximumSourcePixelArea",
                "maximumSourceFrames",
                "allowedSourceFps")
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourceBytes",
                out long bytes)
            || bytes > overall.MaximumSourceBytes
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourceDurationMs",
                out long duration)
            || duration > overall.MaximumSourceDurationMs
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourceWidth",
                out long width)
            || width > overall.MaximumSourceWidth
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourceHeight",
                out long height)
            || height > overall.MaximumSourceHeight
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourcePixelArea",
                out long pixels)
            || pixels > overall.MaximumSourcePixelArea
            || !TryGetPositiveSafeInteger(
                source,
                "maximumSourceFrames",
                out long frames)
            || frames > overall.MaximumSourceFrames
            || !TryGetBoundedStringSet(
                source,
                "allowedSourceFps",
                AllowedSourceFps,
                out IReadOnlySet<string>? fps)
            || fps.Any(value => !overall.AllowedFps.Contains(value)))
        {
            return false;
        }
        bounds = new(
            bytes,
            duration,
            width,
            height,
            pixels,
            frames,
            fps,
            new HashSet<int>());
        return true;
    }

    private static bool TryParseModeDelivery(JsonElement delivery)
        => HasExactProperties(
                delivery,
                "revision",
                "explicitScale4Required",
                "scale4OutputBoundsRequired",
                "silentScaleFallback",
                "silentModeFallback",
                "frameCountPreserved",
                "rationalFpsPreserved",
                "fullVideoPtsSequencePreserved",
                "frameInterpolation",
                "frameRateConversion",
                "implicitCrop",
                "sourceAudioPacketIdentityPreserved")
            && IsExactString(
                delivery,
                "revision",
                "aibos-video-finish-delivery-v1")
            && IsExactBoolean(delivery, "explicitScale4Required", true)
            && IsExactBoolean(
                delivery,
                "scale4OutputBoundsRequired",
                true)
            && IsExactBoolean(delivery, "silentScaleFallback", false)
            && IsExactBoolean(delivery, "silentModeFallback", false)
            && IsExactBoolean(delivery, "frameCountPreserved", true)
            && IsExactBoolean(delivery, "rationalFpsPreserved", true)
            && IsExactBoolean(
                delivery,
                "fullVideoPtsSequencePreserved",
                true)
            && IsExactBoolean(delivery, "frameInterpolation", false)
            && IsExactBoolean(delivery, "frameRateConversion", false)
            && IsExactBoolean(delivery, "implicitCrop", false)
            && IsExactBoolean(
                delivery,
                "sourceAudioPacketIdentityPreserved",
                true);

    private static bool IsValidSource(VideoEditV2SourceSelector source)
    {
        if (source.Kind == "managed-video-job")
        {
            return source.SourceVideoJobId is string jobId
                && IsProducerJobId(jobId)
                && source.Path is null
                && source.Size is null
                && source.MtimeMs is null
                && source.Sha256 is null;
        }
        return source.Kind == "displayed-file"
            && source.SourceVideoJobId is null
            && source.Path is string path
            && !string.IsNullOrWhiteSpace(path)
            && path.Length <= 32_768
            && Path.IsPathFullyQualified(path)
            && source.Size is long size
            && size is > 0 and <= MaximumSourceBytes
            && source.MtimeMs is long mtimeMs
            && Math.Abs((decimal)mtimeMs) <= MaximumSafeInteger
            && source.Sha256 is string sha256
            && IsLowerSha256(sha256);
    }

    private static bool IsAllowedFps(int numerator, int denominator)
        => denominator == 1 && numerator is 24 or 30 or 60;

    private static bool IsProducerJobId(string value)
        => value.Length == 32
            ? value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
                    or >= 'A' and <= 'F')
            : Guid.TryParseExact(value, "D", out _);

    private static bool IsSafeText(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength)
        {
            return false;
        }
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }
                index++;
                continue;
            }
            if (char.IsLowSurrogate(character) || char.IsControl(character))
                return false;
        }
        return true;
    }

    private static bool IsSafeAsciiToken(string value, int maximumLength)
        => value.Length is > 0
            && value.Length <= maximumLength
            && value.All(static character =>
                character is >= (char)0x21 and <= (char)0x7e);

    private static bool IsLowerSha256(string value)
        => value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private static bool HasLowerSha256Property(
        JsonElement element,
        string name)
        => TryGetSingleString(element, name, out string value)
            && IsLowerSha256(value);

    private static bool HasSafeAsciiProperty(
        JsonElement element,
        string name,
        int maximumLength)
        => TryGetSingleString(element, name, out string value)
            && IsSafeAsciiToken(value, maximumLength);

    private static bool HasSingleProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
                count++;
        }
        return count == 1;
    }

    private static bool HasExactProperties(
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

    private static bool TryGetSingleString(
        JsonElement element,
        string name,
        out string value)
    {
        value = "";
        return HasSingleProperty(element, name)
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is string parsed
            && (value = parsed) is not null;
    }

    private static bool IsExactString(
        JsonElement element,
        string name,
        string expected)
        => TryGetSingleString(element, name, out string value)
            && string.Equals(value, expected, StringComparison.Ordinal);

    private static bool IsExactBoolean(
        JsonElement element,
        string name,
        bool expected)
        => HasSingleProperty(element, name)
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean() == expected;

    private static bool IsExactNull(JsonElement element, string name)
        => HasSingleProperty(element, name)
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Null;

    private static bool IsExactInteger(
        JsonElement element,
        string name,
        long expected)
        => TryGetSafeInteger(element, name, expected, expected, out _);

    private static bool TryGetPositiveSafeInteger(
        JsonElement element,
        string name,
        out long value)
        => TryGetSafeInteger(
            element,
            name,
            1,
            MaximumSafeInteger,
            out value);

    private static bool TryGetSafeInteger(
        JsonElement element,
        string name,
        long minimum,
        long maximum,
        out long value)
    {
        value = 0;
        if (!HasSingleProperty(element, name)
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out double number)
            || !double.IsFinite(number)
            || Math.Truncate(number) != number
            || number < minimum
            || number > maximum
            || number < -MaximumSafeInteger
            || number > MaximumSafeInteger)
        {
            return false;
        }
        value = checked((long)number);
        return true;
    }

    private static bool TryGetExactStringSet(
        JsonElement element,
        string name,
        IReadOnlyList<string> expected,
        out IReadOnlySet<string> values)
    {
        values = null!;
        if (!TryGetBoundedStringSet(
                element,
                name,
                expected,
                out IReadOnlySet<string>? parsed)
            || parsed.Count != expected.Count
            || expected.Any(value => !parsed.Contains(value)))
        {
            return false;
        }
        values = parsed;
        return true;
    }

    private static bool TryGetBoundedStringSet(
        JsonElement element,
        string name,
        IReadOnlyList<string> allowed,
        out IReadOnlySet<string> values)
    {
        values = null!;
        if (!HasSingleProperty(element, name)
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() is < 1)
        {
            return false;
        }
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not string value
                || !allowed.Contains(value, StringComparer.Ordinal)
                || !parsed.Add(value))
            {
                return false;
            }
        }
        values = parsed;
        return true;
    }

    private static bool TryGetExactIntegerSet(
        JsonElement element,
        string name,
        IReadOnlyList<int> expected,
        out IReadOnlySet<int> values)
    {
        values = null!;
        if (!HasSingleProperty(element, name)
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() != expected.Count)
        {
            return false;
        }
        var parsed = new HashSet<int>();
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetDouble(out double numeric)
                || !double.IsFinite(numeric)
                || Math.Truncate(numeric) != numeric
                || numeric is < int.MinValue or > int.MaxValue
                || !parsed.Add((int)numeric))
            {
                return false;
            }
        }
        if (expected.Any(value => !parsed.Contains(value)))
            return false;
        values = parsed;
        return true;
    }
}

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

internal sealed record VideoTrimV1SourceProbe(
    string Container,
    string VideoCodec,
    string PixelFormat,
    int BitDepth,
    string DynamicRange,
    int Width,
    int Height,
    int FrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int DurationMs,
    long DurationNumerator,
    long DurationDenominator,
    int VideoStreamCount,
    int AudioStreamCount,
    int ExtraStreamCount,
    int VideoTimeBaseNumerator,
    int VideoTimeBaseDenominator,
    long VideoStartTimestamp,
    string VideoPtsSha256,
    string ProbeDigest,
    string SourceIdentityDigest);

internal sealed record VideoTrimV1Plan(
    int SourceFrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int StartFrame,
    int EndFrameExclusive,
    int SelectedFrameCount,
    long DurationNumerator,
    long DurationDenominator,
    string AudioPolicy)
{
    internal int StartPreviewFrame => StartFrame;
    internal int MiddlePreviewFrame =>
        checked((int)(((long)StartFrame + EndFrameExclusive - 1) / 2));
    internal int EndPreviewFrame => EndFrameExclusive - 1;
    internal bool SupportsThreePointPreview => SelectedFrameCount >= 3;
}

internal sealed record VideoTrimV1PreviewPayload(
    string Role,
    int SourceFrame,
    string SourcePts,
    string DecodedPixelSha256,
    string DecoderRevision,
    string Mime,
    int Width,
    int Height,
    int EncodedBytes,
    string EncodedSha256,
    BitmapImage Image);

internal sealed record VideoTrimV1PreviewSet(
    VideoEditV2SourceSelector Source,
    VideoTrimV1SourceProbe Probe,
    VideoTrimV1Plan Plan,
    IReadOnlyList<VideoTrimV1PreviewPayload> Previews,
    string SourceStamp);

internal static class VideoTrimV1Contract
{
    internal const string ContractId = "PV-ENHANCE-VIDEO-TRIM-001";
    internal const string Protocol = "aibos-enhancement-video-trim-v1";
    internal const string InspectionRevision =
        "aibos-video-trim-source-inspection-v1";
    internal const string CapabilityRevision = "aibos-video-trim-ready-v1";
    internal const string PlanRevision = "aibos-video-trim-plan-v1";
    internal const string JournalRevision =
        "aibos-video-trim-attempt-journal-v1";
    internal const string OutputValidatorRevision =
        "aibos-video-trim-child-mp4-validator-v1";
    internal const string SourceInspectionRoute =
        "api/enhance/video-trim/v1/source-inspection";
    internal const int MaximumRequestBytes = 128 * 1024;
    internal const int MaximumProbeResponseBytes = 128 * 1024;
    internal const int MaximumPreviewResponseBytes = 2_113_536;
    internal const int MaximumPreviewEncodedBytes = 512 * 1024;
    internal const int MaximumPreviewTotalEncodedBytes = 1_572_864;
    internal const int MaximumPreviewEdge = 384;
    internal const int MaximumPreviewPixels = 147_456;
    internal const long MaximumSourceBytes = 536_870_912;
    internal const int MaximumDurationMs = 300_000;
    internal const int MaximumWidth = 1_920;
    internal const int MaximumHeight = 1_080;
    internal const int MaximumPixelArea = 2_073_600;
    internal const int MaximumFrames = 18_000;

    private static readonly byte[] PngSignature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool TryPlan(
        VideoTrimV1SourceProbe source,
        int startFrame,
        int endFrameExclusive,
        string audioPolicy,
        out VideoTrimV1Plan plan)
    {
        plan = null!;
        if (!IsValidProbe(source)
            || audioPolicy is not ("preserve" or "mute")
            || startFrame < 0
            || endFrameExclusive <= startFrame
            || endFrameExclusive > source.FrameCount)
        {
            return false;
        }

        try
        {
            int selected = checked(endFrameExclusive - startFrame);
            long numerator = checked((long)selected * source.FpsDenominator);
            long denominator = source.FpsNumerator;
            long divisor = GreatestCommonDivisor(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
            if (selected > MaximumFrames
                || checked(numerator * 1_000) / denominator
                    > MaximumDurationMs)
            {
                return false;
            }

            plan = new(
                source.FrameCount,
                source.FpsNumerator,
                source.FpsDenominator,
                startFrame,
                endFrameExclusive,
                selected,
                numerator,
                denominator,
                audioPolicy);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static string BuildProbeRequestJson(
        VideoEditV2SourceSelector source)
        => JsonSerializer.Serialize(new
        {
            action = "probe",
            source = BuildSource(source),
        });

    internal static string BuildPreviewRequestJson(
        VideoEditV2SourceSelector source,
        VideoTrimV1Plan plan,
        string sourceIdentityDigest)
    {
        if (!plan.SupportsThreePointPreview
            || !IsLowerSha256(sourceIdentityDigest))
        {
            throw new ArgumentException("A three-frame exact preview is required.");
        }
        return JsonSerializer.Serialize(new
        {
            action = "preview",
            source = BuildSource(source),
            sourceIdentityDigest,
            selection = new
            {
                startFrame = plan.StartFrame,
                endFrameExclusive = plan.EndFrameExclusive,
            },
            frames = new[]
            {
                plan.StartPreviewFrame,
                plan.MiddlePreviewFrame,
                plan.EndPreviewFrame,
            },
        });
    }

    internal static bool TryParseProbeResponse(
        JsonElement root,
        out VideoTrimV1SourceProbe probe)
    {
        probe = null!;
        return HasExactProperties(
                root,
                "action",
                "protocol",
                "inspectionRevision",
                "source")
            && IsExactString(root, "action", "probe")
            && IsExactString(root, "protocol", Protocol)
            && IsExactString(
                root,
                "inspectionRevision",
                InspectionRevision)
            && root.TryGetProperty("source", out JsonElement source)
            && TryParseSourceProbe(source, out probe);
    }

    internal static bool TryParsePreviewResponse(
        JsonElement root,
        VideoEditV2SourceSelector source,
        VideoTrimV1SourceProbe probe,
        VideoTrimV1Plan plan,
        string sourceStamp,
        out VideoTrimV1PreviewSet previewSet)
    {
        previewSet = null!;
        if (!plan.SupportsThreePointPreview
            || !HasExactProperties(
                root,
                "action",
                "protocol",
                "inspectionRevision",
                "sourceIdentityDigest",
                "selection",
                "previews")
            || !IsExactString(root, "action", "preview")
            || !IsExactString(root, "protocol", Protocol)
            || !IsExactString(
                root,
                "inspectionRevision",
                InspectionRevision)
            || !IsExactString(
                root,
                "sourceIdentityDigest",
                probe.SourceIdentityDigest)
            || !root.TryGetProperty("selection", out JsonElement selection)
            || !HasExactProperties(
                selection,
                "startFrame",
                "endFrameExclusive")
            || !IsExactInteger(selection, "startFrame", plan.StartFrame)
            || !IsExactInteger(
                selection,
                "endFrameExclusive",
                plan.EndFrameExclusive)
            || !root.TryGetProperty("previews", out JsonElement previews)
            || previews.ValueKind != JsonValueKind.Array
            || previews.GetArrayLength() != 3)
        {
            return false;
        }

        string[] roles = ["start", "middle", "end"];
        int[] frames =
        [
            plan.StartPreviewFrame,
            plan.MiddlePreviewFrame,
            plan.EndPreviewFrame,
        ];
        var parsed = new List<VideoTrimV1PreviewPayload>(3);
        int totalBytes = 0;
        int index = 0;
        foreach (JsonElement item in previews.EnumerateArray())
        {
            if (!TryParsePreview(item, roles[index], frames[index], out var value))
                return false;
            try
            {
                totalBytes = checked(totalBytes + value.EncodedBytes);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (totalBytes > MaximumPreviewTotalEncodedBytes)
                return false;
            parsed.Add(value);
            index++;
        }

        previewSet = new(source, probe, plan, parsed, sourceStamp);
        return true;
    }

    internal static bool TryBuildRequest(
        VideoEditV2SourceSelector source,
        VideoTrimV1Plan plan,
        out JsonElement request)
    {
        request = default;
        if (!IsValidSource(source)
            || plan.AudioPolicy is not ("preserve" or "mute")
            || plan.StartFrame < 0
            || plan.EndFrameExclusive <= plan.StartFrame
            || plan.EndFrameExclusive > plan.SourceFrameCount
            || plan.SelectedFrameCount
                != plan.EndFrameExclusive - plan.StartFrame)
        {
            return false;
        }
        request = JsonSerializer.SerializeToElement(new
        {
            operation = "video",
            mediaKind = "video",
            videoTrim = new
            {
                schemaVersion = 1,
                source = BuildSource(source),
                selection = new
                {
                    startFrame = plan.StartFrame,
                    endFrameExclusive = plan.EndFrameExclusive,
                },
                audioPolicy = plan.AudioPolicy,
            },
        });
        return true;
    }

    internal static bool IsExactReadyHealth(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("videoTrimV1", out JsonElement trim)
            || !HasExactProperties(
                trim,
                "contractId",
                "protocol",
                "readerReady",
                "writerEnabled",
                "runtimeVerified",
                "ready",
                "state",
                "reasonCode",
                "capabilityRevision",
                "resolvedRuntime",
                "receipts",
                "resourceBounds",
                "outputPolicy")
            || !IsExactString(trim, "contractId", ContractId)
            || !IsExactString(trim, "protocol", Protocol)
            || !IsExactBoolean(trim, "readerReady", true)
            || !IsExactBoolean(trim, "writerEnabled", true)
            || !IsExactBoolean(trim, "runtimeVerified", true)
            || !IsExactBoolean(trim, "ready", true)
            || !IsExactString(trim, "state", "ready")
            || !IsExactNull(trim, "reasonCode")
            || !IsExactString(
                trim,
                "capabilityRevision",
                CapabilityRevision)
            || !trim.TryGetProperty(
                "resolvedRuntime",
                out JsonElement runtime)
            || !HasExactProperties(
                runtime,
                "runtimeRevision",
                "ffmpegRevision",
                "ffprobeRevision",
                "planRevision",
                "journalRevision",
                "outputValidatorRevision")
            || !HasSafeAscii(runtime, "runtimeRevision")
            || !HasSafeAscii(runtime, "ffmpegRevision")
            || !HasSafeAscii(runtime, "ffprobeRevision")
            || !IsExactString(runtime, "planRevision", PlanRevision)
            || !IsExactString(runtime, "journalRevision", JournalRevision)
            || !IsExactString(
                runtime,
                "outputValidatorRevision",
                OutputValidatorRevision)
            || !trim.TryGetProperty("receipts", out JsonElement receipts)
            || !TryParseReceipts(receipts)
            || !trim.TryGetProperty(
                "resourceBounds",
                out JsonElement resources)
            || !TryParseResourceBounds(resources)
            || !trim.TryGetProperty("outputPolicy", out JsonElement output)
            || !TryParseOutputPolicy(output))
        {
            return false;
        }
        return true;
    }

    internal static string FormatFrameTime(
        int frame,
        int fpsNumerator,
        int fpsDenominator)
    {
        if (frame < 0 || !IsAllowedFps(fpsNumerator, fpsDenominator))
            return "--";
        double seconds = (double)frame * fpsDenominator / fpsNumerator;
        return seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static object BuildSource(VideoEditV2SourceSelector source)
        => source.Kind switch
        {
            "managed-video-job" => new
            {
                kind = source.Kind,
                sourceVideoJobId = source.SourceVideoJobId,
            },
            "displayed-file" => new
            {
                kind = source.Kind,
                path = source.Path,
                size = source.Size,
                mtimeMs = source.MtimeMs,
                sha256 = source.Sha256,
            },
            _ => throw new ArgumentException("Unsupported Video Trim source."),
        };

    private static bool IsValidSource(VideoEditV2SourceSelector source)
        => source.Kind == "managed-video-job"
            ? source.SourceVideoJobId is { Length: > 0 and <= 512 }
                && source.Path is null
                && source.Size is null
                && source.MtimeMs is null
                && source.Sha256 is null
            : source.Kind == "displayed-file"
                && source.SourceVideoJobId is null
                && source.Path is { Length: > 0 and <= 32_767 } path
                && Path.IsPathFullyQualified(path)
                && source.Size is > 0 and <= MaximumSourceBytes
                && source.MtimeMs is >= -9_007_199_254_740_991
                    and <= 9_007_199_254_740_991
                && IsLowerSha256(source.Sha256);

    private static bool TryParseSourceProbe(
        JsonElement source,
        out VideoTrimV1SourceProbe probe)
    {
        probe = null!;
        if (!HasExactProperties(
                source,
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
                "videoPtsSha256",
                "probeDigest",
                "sourceIdentityDigest")
            || !TryGetInt32(source, "width", out int width)
            || !TryGetInt32(source, "height", out int height)
            || !TryGetInt32(source, "frameCount", out int frameCount)
            || !TryGetInt32(source, "fpsNumerator", out int fpsNumerator)
            || !TryGetInt32(source, "fpsDenominator", out int fpsDenominator)
            || !TryGetInt32(source, "durationMs", out int durationMs)
            || !TryGetInt64(source, "durationNumerator", out long durationNumerator)
            || !TryGetInt64(source, "durationDenominator", out long durationDenominator)
            || !TryGetInt32(source, "videoStreamCount", out int videoStreams)
            || !TryGetInt32(source, "audioStreamCount", out int audioStreams)
            || !TryGetInt32(source, "extraStreamCount", out int extraStreams)
            || !TryGetInt32(source, "videoTimeBaseNumerator", out int timeBaseNumerator)
            || !TryGetInt32(source, "videoTimeBaseDenominator", out int timeBaseDenominator)
            || !TryGetInt64(source, "videoStartTimestamp", out long startTimestamp)
            || !TryGetString(source, "videoPtsSha256", out string ptsSha)
            || !TryGetString(source, "probeDigest", out string probeDigest)
            || !TryGetString(source, "sourceIdentityDigest", out string identityDigest))
        {
            return false;
        }
        probe = new(
            ReadString(source, "container"),
            ReadString(source, "videoCodec"),
            ReadString(source, "pixelFormat"),
            ReadInt32(source, "bitDepth"),
            ReadString(source, "dynamicRange"),
            width,
            height,
            frameCount,
            fpsNumerator,
            fpsDenominator,
            durationMs,
            durationNumerator,
            durationDenominator,
            videoStreams,
            audioStreams,
            extraStreams,
            timeBaseNumerator,
            timeBaseDenominator,
            startTimestamp,
            ptsSha,
            probeDigest,
            identityDigest);
        return IsValidProbe(probe);
    }

    private static bool IsValidProbe(VideoTrimV1SourceProbe probe)
    {
        if (probe.Container != "mp4"
            || probe.VideoCodec != "h264"
            || probe.PixelFormat != "yuv420p"
            || probe.BitDepth != 8
            || probe.DynamicRange != "SDR"
            || probe.Width is <= 0 or > MaximumWidth
            || probe.Height is <= 0 or > MaximumHeight
            || (long)probe.Width * probe.Height > MaximumPixelArea
            || probe.FrameCount is <= 0 or > MaximumFrames
            || !IsAllowedFps(probe.FpsNumerator, probe.FpsDenominator)
            || probe.DurationMs is <= 0 or > MaximumDurationMs
            || probe.DurationNumerator <= 0
            || probe.DurationDenominator <= 0
            || probe.VideoStreamCount != 1
            || probe.AudioStreamCount is < 0 or > 1
            || probe.ExtraStreamCount != 0
            || probe.VideoTimeBaseNumerator <= 0
            || probe.VideoTimeBaseDenominator <= 0
            || !IsLowerSha256(probe.VideoPtsSha256)
            || !IsLowerSha256(probe.ProbeDigest)
            || !IsLowerSha256(probe.SourceIdentityDigest))
        {
            return false;
        }
        try
        {
            return checked(
                    (long)probe.FrameCount * probe.FpsDenominator
                        * probe.DurationDenominator)
                == checked(
                    probe.DurationNumerator * probe.FpsNumerator);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParsePreview(
        JsonElement item,
        string expectedRole,
        int expectedFrame,
        out VideoTrimV1PreviewPayload preview)
    {
        preview = null!;
        if (!HasExactProperties(
                item,
                "role",
                "sourceFrame",
                "sourcePts",
                "decodedPixelSha256",
                "decoderRevision",
                "mime",
                "width",
                "height",
                "encodedBytes",
                "encodedSha256",
                "base64")
            || !IsExactString(item, "role", expectedRole)
            || !IsExactInteger(item, "sourceFrame", expectedFrame)
            || !TryGetString(item, "sourcePts", out string sourcePts)
            || !long.TryParse(
                sourcePts,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long parsedPts)
            || !string.Equals(
                parsedPts.ToString(CultureInfo.InvariantCulture),
                sourcePts,
                StringComparison.Ordinal)
            || !TryGetString(item, "decodedPixelSha256", out string decodedSha)
            || !IsLowerSha256(decodedSha)
            || !TryGetString(item, "decoderRevision", out string decoderRevision)
            || string.IsNullOrWhiteSpace(decoderRevision)
            || decoderRevision.Length > 128
            || !IsExactString(item, "mime", "image/png")
            || !TryGetInt32(item, "width", out int width)
            || !TryGetInt32(item, "height", out int height)
            || width is <= 0 or > MaximumPreviewEdge
            || height is <= 0 or > MaximumPreviewEdge
            || (long)width * height > MaximumPreviewPixels
            || !TryGetInt32(item, "encodedBytes", out int encodedBytes)
            || encodedBytes is <= 0 or > MaximumPreviewEncodedBytes
            || !TryGetString(item, "encodedSha256", out string encodedSha)
            || !IsLowerSha256(encodedSha)
            || !TryGetString(item, "base64", out string encodedBase64))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encodedBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (bytes.Length != encodedBytes
            || bytes.Length < PngSignature.Length
            || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                Convert.FromHexString(encodedSha)))
        {
            return false;
        }

        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (image.PixelWidth != width || image.PixelHeight != height)
                return false;
            preview = new(
                expectedRole,
                expectedFrame,
                sourcePts,
                decodedSha,
                decoderRevision,
                "image/png",
                width,
                height,
                encodedBytes,
                encodedSha,
                image);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidDataException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryParseReceipts(JsonElement receipts)
    {
        string[] names =
        [
            "ffmpegReceiptId",
            "ffprobeReceiptId",
            "runtimeReceiptId",
            "qualityReceiptId",
            "resourceReceiptId",
            "cancelReceiptId",
            "recoveryReceiptId",
            "outputReceiptId",
        ];
        return HasExactProperties(
                receipts,
                names.Append("receiptSetSha256").ToArray())
            && names.All(name => HasSafeAscii(receipts, name))
            && TryGetString(
                receipts,
                "receiptSetSha256",
                out string receiptSetSha)
            && IsLowerSha256(receiptSetSha)
            && receiptSetSha.Any(character => character != '0');
    }

    private static bool TryParseResourceBounds(JsonElement value)
        => HasExactProperties(
                value,
                "maximumConcurrentJobs",
                "maximumGpuLeases",
                "maximumModelMounts",
                "maximumArgvEntries",
                "maximumArgvEntryBytes",
                "maximumArgvBytes",
                "maximumCapturedStderrBytes",
                "maximumHostRamBytes",
                "maximumScratchBytes",
                "maximumOutputBytes",
                "processTimeoutMs",
                "cancelGraceMs")
            && IsExactInteger(value, "maximumConcurrentJobs", 1)
            && IsExactInteger(value, "maximumGpuLeases", 0)
            && IsExactInteger(value, "maximumModelMounts", 0)
            && IsExactInteger(value, "maximumArgvEntries", 96)
            && IsExactInteger(value, "maximumArgvEntryBytes", 4_096)
            && IsExactInteger(value, "maximumArgvBytes", 32_768)
            && IsExactInteger(value, "maximumCapturedStderrBytes", 1_048_576)
            && IsExactInteger(value, "maximumHostRamBytes", 4_294_967_296)
            && IsExactInteger(value, "maximumScratchBytes", 2_147_483_648)
            && IsExactInteger(value, "maximumOutputBytes", MaximumSourceBytes)
            && IsExactInteger(value, "processTimeoutMs", 3_600_000)
            && IsExactInteger(value, "cancelGraceMs", 30_000);

    private static bool TryParseOutputPolicy(JsonElement value)
        => HasExactProperties(
                value,
                "revision",
                "container",
                "videoCodec",
                "pixelFormat",
                "bitDepth",
                "dynamicRange",
                "videoStreamCount",
                "maximumAudioStreamCount",
                "extraStreamCount",
                "maximumBytes",
                "exactSelectedFrameCount",
                "exactSourceRationalFps",
                "zeroOriginPtsDigest",
                "atomicNoReplacePublish",
                "reopenPublishedBytes")
            && IsExactString(value, "revision", OutputValidatorRevision)
            && IsExactString(value, "container", "mp4")
            && IsExactString(value, "videoCodec", "h264")
            && IsExactString(value, "pixelFormat", "yuv420p")
            && IsExactInteger(value, "bitDepth", 8)
            && IsExactString(value, "dynamicRange", "SDR")
            && IsExactInteger(value, "videoStreamCount", 1)
            && IsExactInteger(value, "maximumAudioStreamCount", 1)
            && IsExactInteger(value, "extraStreamCount", 0)
            && IsExactInteger(value, "maximumBytes", MaximumSourceBytes)
            && IsExactBoolean(value, "exactSelectedFrameCount", true)
            && IsExactBoolean(value, "exactSourceRationalFps", true)
            && IsExactBoolean(value, "zeroOriginPtsDigest", true)
            && IsExactBoolean(value, "atomicNoReplacePublish", true)
            && IsExactBoolean(value, "reopenPublishedBytes", true);

    private static bool IsAllowedFps(int numerator, int denominator)
        => denominator == 1 && numerator is 24 or 30 or 60;

    private static long GreatestCommonDivisor(long left, long right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            long remainder = left % right;
            left = right;
            right = remainder;
        }
        return Math.Max(1, left);
    }

    private static bool HasExactProperties(
        JsonElement value,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var expected = names.ToHashSet(StringComparer.Ordinal);
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
                return false;
            count++;
        }
        return count == expected.Count;
    }

    private static bool TryGetString(
        JsonElement value,
        string name,
        out string result)
    {
        result = "";
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is string text
            && (result = text) is not null;
    }

    private static string ReadString(JsonElement value, string name)
        => value.GetProperty(name).GetString() ?? "";

    private static int ReadInt32(JsonElement value, string name)
        => value.GetProperty(name).GetInt32();

    private static bool TryGetInt32(
        JsonElement value,
        string name,
        out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out result);
    }

    private static bool TryGetInt64(
        JsonElement value,
        string name,
        out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out result);
    }

    private static bool IsExactString(
        JsonElement value,
        string name,
        string expected)
        => TryGetString(value, name, out string actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool IsExactInteger(
        JsonElement value,
        string name,
        long expected)
        => TryGetInt64(value, name, out long actual) && actual == expected;

    private static bool IsExactBoolean(
        JsonElement value,
        string name,
        bool expected)
        => value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == (expected
                ? JsonValueKind.True
                : JsonValueKind.False);

    private static bool IsExactNull(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Null;

    private static bool HasSafeAscii(JsonElement value, string name)
        => TryGetString(value, name, out string text)
            && text.Length is > 0 and <= 128
            && text.All(character => character is >= '!' and <= '~');

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
}

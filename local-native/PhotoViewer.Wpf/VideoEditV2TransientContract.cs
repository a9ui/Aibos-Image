using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

internal sealed record VideoEditV2SourceSelector(
    string Kind,
    string? SourceVideoJobId,
    string? Path,
    long? Size,
    long? MtimeMs,
    string? Sha256);

internal sealed record VideoEditV2SourceSummary(
    int FrameCount,
    int FpsNumerator,
    int FpsDenominator,
    int DurationMs,
    int Width,
    int Height);

internal sealed record VideoEditV2PreviewPayload(
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

internal sealed record VideoEditV2PreviewSet(
    VideoEditV2SourceSelector Source,
    VideoEditV2SourceSummary Summary,
    VideoEditV2SelectionPlan Selection,
    IReadOnlyList<VideoEditV2PreviewPayload> Previews,
    string SourceStamp);

internal sealed record VideoEditV2CompiledCandidate(
    string BackendPrompt,
    string SummaryJa,
    string CompilerRevision,
    string ContextDigest,
    string SourceStamp,
    string ContextStamp);

internal static class VideoEditV2TransientContract
{
    internal const string Route =
        "api/enhance/video-prompts/v2/edit/compile";
    internal const int MaximumRequestBytes = 128 * 1024;
    internal const int MaximumActionResponseBytes = 128 * 1024;
    internal const int MaximumPreviewResponseBytes = 2_113_536;
    internal const int MaximumPreviewEncodedBytes = 512 * 1024;
    internal const int MaximumPreviewTotalEncodedBytes = 1_572_864;
    internal const int MaximumPreviewEdge = 384;
    internal const int MaximumPreviewPixels = 147_456;
    internal const int MaximumInstructionLength = 4_000;
    internal const int MaximumBackendPromptLength = 8_000;
    internal const int MaximumSummaryLength = 2_000;
    internal const int MaximumCompilerRevisionLength = 128;

    private static readonly byte[] PngSignature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool TryCreateManagedSelector(
        string producerJobId,
        out VideoEditV2SourceSelector selector)
    {
        selector = null!;
        if (!IsProducerJobId(producerJobId))
            return false;
        selector = new(
            "managed-video-job",
            producerJobId,
            Path: null,
            Size: null,
            MtimeMs: null,
            Sha256: null);
        return true;
    }

    internal static bool TryCreateDisplayedFileSelector(
        string canonicalPath,
        long size,
        long mtimeMs,
        string sha256,
        out VideoEditV2SourceSelector selector)
    {
        selector = null!;
        if (string.IsNullOrWhiteSpace(canonicalPath)
            || canonicalPath.Length > 32_767
            || !Path.IsPathFullyQualified(canonicalPath)
            || size is <= 0 or > 536_870_912
            || Math.Abs((decimal)mtimeMs) > 9_007_199_254_740_991m
            || !IsLowerSha256(sha256))
        {
            return false;
        }
        selector = new(
            "displayed-file",
            SourceVideoJobId: null,
            canonicalPath,
            size,
            mtimeMs,
            sha256);
        return true;
    }

    internal static string BuildProbeRequestJson(
        VideoEditV2SourceSelector source)
        => BuildRequest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", "probe");
            writer.WritePropertyName("source");
            WriteSource(writer, source);
            writer.WriteEndObject();
        });

    internal static string BuildPreviewRequestJson(
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection)
        => BuildRequest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", "preview");
            writer.WritePropertyName("source");
            WriteSource(writer, source);
            writer.WritePropertyName("selection");
            WriteSelection(writer, selection);
            writer.WriteEndObject();
        });

    internal static string BuildCompileRequestJson(
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection,
        IReadOnlyList<VideoEditV2PreviewPayload> previews,
        string instructionJa)
        => BuildRequest(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", "compile");
            writer.WritePropertyName("source");
            WriteSource(writer, source);
            writer.WritePropertyName("selection");
            WriteSelection(writer, selection);
            writer.WritePropertyName("previews");
            WritePreviewIdentities(writer, previews);
            writer.WriteString("instructionJa", instructionJa);
            writer.WriteEndObject();
        });

    internal static bool TryParseProbeResponse(
        JsonElement root,
        out VideoEditV2SourceSummary summary)
    {
        summary = null!;
        return HasExactProperties(root, "action", "source")
            && IsExactString(root, "action", "probe")
            && root.TryGetProperty("source", out JsonElement source)
            && TryParseSourceSummary(source, out summary);
    }

    internal static bool TryParsePreviewResponse(
        JsonElement root,
        VideoEditV2SourceSummary expectedSummary,
        VideoEditV2SelectionPlan expectedSelection,
        out VideoEditV2PreviewSet? parsed,
        VideoEditV2SourceSelector source,
        string sourceStamp)
    {
        parsed = null;
        if (!HasExactProperties(root, "action", "source", "previews")
            || !IsExactString(root, "action", "preview")
            || !root.TryGetProperty("source", out JsonElement sourceElement)
            || !TryParseSourceSummary(sourceElement, out var summary)
            || summary != expectedSummary
            || !root.TryGetProperty("previews", out JsonElement previewsElement)
            || previewsElement.ValueKind != JsonValueKind.Array
            || previewsElement.GetArrayLength() != 3)
        {
            return false;
        }

        string[] roles = ["start", "middle", "end"];
        int[] frames =
        [
            expectedSelection.StartPreviewFrame,
            expectedSelection.MiddlePreviewFrame,
            expectedSelection.EndPreviewFrame,
        ];
        var previews = new List<VideoEditV2PreviewPayload>(3);
        int totalEncodedBytes = 0;
        int index = 0;
        foreach (JsonElement item in previewsElement.EnumerateArray())
        {
            if (!TryParsePreview(item, roles[index], frames[index], out var preview))
                return false;
            try
            {
                totalEncodedBytes = checked(totalEncodedBytes + preview.EncodedBytes);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (totalEncodedBytes > MaximumPreviewTotalEncodedBytes)
                return false;
            previews.Add(preview);
            index++;
        }

        parsed = new(
            source,
            summary,
            expectedSelection,
            previews,
            sourceStamp);
        return true;
    }

    internal static bool TryParseCompileResponse(
        JsonElement root,
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection,
        IReadOnlyList<VideoEditV2PreviewPayload> previews,
        string instructionJa,
        string sourceStamp,
        string contextStamp,
        out VideoEditV2CompiledCandidate candidate)
    {
        candidate = null!;
        if (!HasExactProperties(root, "action", "candidate")
            || !IsExactString(root, "action", "compile")
            || !root.TryGetProperty("candidate", out JsonElement value)
            || !HasExactProperties(
                value,
                "backendPrompt",
                "summaryJa",
                "compilerRevision",
                "contextDigest")
            || !TryGetExactString(value, "backendPrompt", out string backendPrompt)
            || !TryGetExactString(value, "summaryJa", out string summaryJa)
            || !TryGetExactString(value, "compilerRevision", out string compilerRevision)
            || !TryGetExactString(value, "contextDigest", out string contextDigest)
            || !IsSafeText(backendPrompt, MaximumBackendPromptLength, allowLineBreaks: true)
            || !IsSafeText(summaryJa, MaximumSummaryLength, allowLineBreaks: true)
            || !IsSafeCompilerRevision(compilerRevision)
            || !IsLowerSha256(contextDigest))
        {
            return false;
        }

        string expectedDigest = ComputeContextDigest(
            source,
            selection,
            previews,
            instructionJa,
            backendPrompt,
            summaryJa,
            compilerRevision);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(contextDigest),
                Encoding.ASCII.GetBytes(expectedDigest)))
        {
            return false;
        }

        candidate = new(
            backendPrompt,
            summaryJa,
            compilerRevision,
            contextDigest,
            sourceStamp,
            contextStamp);
        return true;
    }

    internal static string ComputeContextDigest(
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection,
        IReadOnlyList<VideoEditV2PreviewPayload> previews,
        string instructionJa,
        string backendPrompt,
        string summaryJa,
        string compilerRevision)
    {
        byte[] canonical = BuildUtf8(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("source");
            WriteSource(writer, source);
            writer.WritePropertyName("selection");
            WriteSelection(writer, selection);
            writer.WritePropertyName("previews");
            WritePreviewIdentities(writer, previews);
            writer.WriteString("instructionJa", instructionJa);
            writer.WriteString("backendPrompt", backendPrompt);
            writer.WriteString("summaryJa", summaryJa);
            writer.WriteString("compilerRevision", compilerRevision);
            writer.WriteEndObject();
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    internal static bool TryComputeContextDigestFromCompileRequestForSmoke(
        JsonElement request,
        string backendPrompt,
        string summaryJa,
        string compilerRevision,
        out string contextDigest)
    {
        contextDigest = "";
        if (!HasExactProperties(
                request,
                "action",
                "source",
                "selection",
                "previews",
                "instructionJa")
            || !IsExactString(request, "action", "compile")
            || !request.TryGetProperty("source", out JsonElement source)
            || !request.TryGetProperty("selection", out JsonElement selection)
            || !request.TryGetProperty("previews", out JsonElement previews)
            || !TryGetExactString(
                request,
                "instructionJa",
                out string instructionJa)
            || !IsSafeInstruction(instructionJa)
            || !IsSafeText(
                backendPrompt,
                MaximumBackendPromptLength,
                allowLineBreaks: true)
            || !IsSafeText(
                summaryJa,
                MaximumSummaryLength,
                allowLineBreaks: true)
            || !IsSafeCompilerRevision(compilerRevision))
        {
            return false;
        }

        byte[] canonical = BuildUtf8(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("source");
            source.WriteTo(writer);
            writer.WritePropertyName("selection");
            selection.WriteTo(writer);
            writer.WritePropertyName("previews");
            previews.WriteTo(writer);
            writer.WriteString("instructionJa", instructionJa);
            writer.WriteString("backendPrompt", backendPrompt);
            writer.WriteString("summaryJa", summaryJa);
            writer.WriteString("compilerRevision", compilerRevision);
            writer.WriteEndObject();
        });
        contextDigest = Convert.ToHexString(SHA256.HashData(canonical))
            .ToLowerInvariant();
        return true;
    }

    internal static bool IsSafeInstruction(string value)
        => IsSafeText(value, MaximumInstructionLength, allowLineBreaks: true);

    internal static bool IsLowerSha256(string value)
        => value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    internal static bool SameSource(
        VideoEditV2SourceSelector left,
        VideoEditV2SourceSelector right)
        => left == right;

    private static bool TryParseSourceSummary(
        JsonElement value,
        out VideoEditV2SourceSummary summary)
    {
        summary = null!;
        if (!HasExactProperties(
                value,
                "frameCount",
                "fpsNumerator",
                "fpsDenominator",
                "durationMs",
                "width",
                "height")
            || !TryGetInt32(value, "frameCount", out int frameCount)
            || !TryGetInt32(value, "fpsNumerator", out int fpsNumerator)
            || !TryGetInt32(value, "fpsDenominator", out int fpsDenominator)
            || !TryGetInt32(value, "durationMs", out int durationMs)
            || !TryGetInt32(value, "width", out int width)
            || !TryGetInt32(value, "height", out int height)
            || !VideoEditV2Planner.IsSupportedFps(fpsNumerator, fpsDenominator)
            || frameCount is <= 0 or > VideoEditV2Planner.MaximumSourceFrames
            || durationMs is <= 0 or > 300_000
            || width is <= 0 or > 1_920
            || height is <= 0 or > 1_080
            || checked((long)width * height) > 2_073_600)
        {
            return false;
        }
        summary = new(
            frameCount,
            fpsNumerator,
            fpsDenominator,
            durationMs,
            width,
            height);
        return true;
    }

    private static bool TryParsePreview(
        JsonElement value,
        string expectedRole,
        int expectedFrame,
        out VideoEditV2PreviewPayload preview)
    {
        preview = null!;
        if (!HasExactProperties(
                value,
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
            || !TryGetExactString(value, "role", out string role)
            || !string.Equals(role, expectedRole, StringComparison.Ordinal)
            || !TryGetInt32(value, "sourceFrame", out int sourceFrame)
            || sourceFrame != expectedFrame
            || !TryGetExactString(value, "sourcePts", out string sourcePts)
            || !long.TryParse(
                sourcePts,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _)
            || !TryGetExactString(
                value,
                "decodedPixelSha256",
                out string decodedPixelSha256)
            || !IsLowerSha256(decodedPixelSha256)
            || !TryGetExactString(
                value,
                "decoderRevision",
                out string decoderRevision)
            || !IsSafeAsciiToken(decoderRevision, 128)
            || !TryGetExactString(value, "mime", out string mime)
            || !string.Equals(mime, "image/png", StringComparison.Ordinal)
            || !TryGetInt32(value, "width", out int width)
            || !TryGetInt32(value, "height", out int height)
            || width is <= 0 or > MaximumPreviewEdge
            || height is <= 0 or > MaximumPreviewEdge
            || checked((long)width * height) > MaximumPreviewPixels
            || !TryGetInt32(value, "encodedBytes", out int encodedBytes)
            || encodedBytes is <= 0 or > MaximumPreviewEncodedBytes
            || !TryGetExactString(
                value,
                "encodedSha256",
                out string encodedSha256)
            || !IsLowerSha256(encodedSha256)
            || !TryGetExactString(value, "base64", out string base64)
            || base64.Length > ((MaximumPreviewEncodedBytes + 2) / 3) * 4)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (!string.Equals(
                Convert.ToBase64String(bytes),
                base64,
                StringComparison.Ordinal)
            || bytes.Length != encodedBytes
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes),
                Convert.FromHexString(encodedSha256))
            || !HasExactPngHeader(bytes, width, height))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            if (image.PixelWidth != width || image.PixelHeight != height)
                return false;
            image.Freeze();
            preview = new(
                role,
                sourceFrame,
                sourcePts,
                decodedPixelSha256,
                decoderRevision,
                mime,
                width,
                height,
                encodedBytes,
                encodedSha256,
                image);
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or NotSupportedException
                or IOException
                or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExactPngHeader(
        ReadOnlySpan<byte> bytes,
        int expectedWidth,
        int expectedHeight)
        => bytes.Length >= 33
            && bytes[..PngSignature.Length].SequenceEqual(PngSignature)
            && BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) == 13
            && bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
            && BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4))
                == (uint)expectedWidth
            && BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4))
                == (uint)expectedHeight;

    private static string BuildRequest(Action<Utf8JsonWriter> write)
    {
        byte[] utf8 = BuildUtf8(write);
        if (utf8.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException(
                "The Video Edit v2 transient request exceeded its byte limit.");
        }
        return Encoding.UTF8.GetString(utf8);
    }

    private static byte[] BuildUtf8(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            write(writer);
        }
        return stream.ToArray();
    }

    private static void WriteSource(
        Utf8JsonWriter writer,
        VideoEditV2SourceSelector source)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", source.Kind);
        if (string.Equals(
                source.Kind,
                "managed-video-job",
                StringComparison.Ordinal))
        {
            writer.WriteString("sourceVideoJobId", source.SourceVideoJobId);
        }
        else
        {
            writer.WriteString("path", source.Path);
            writer.WriteNumber("size", source.Size!.Value);
            writer.WriteNumber("mtimeMs", source.MtimeMs!.Value);
            writer.WriteString("sha256", source.Sha256);
        }
        writer.WriteEndObject();
    }

    private static void WriteSelection(
        Utf8JsonWriter writer,
        VideoEditV2SelectionPlan selection)
    {
        writer.WriteStartObject();
        writer.WriteNumber("startFrame", selection.StartFrame);
        writer.WriteNumber("endFrameExclusive", selection.EndFrameExclusive);
        writer.WriteEndObject();
    }

    private static void WritePreviewIdentities(
        Utf8JsonWriter writer,
        IReadOnlyList<VideoEditV2PreviewPayload> previews)
    {
        writer.WriteStartArray();
        foreach (VideoEditV2PreviewPayload preview in previews)
        {
            writer.WriteStartObject();
            writer.WriteString("role", preview.Role);
            writer.WriteNumber("sourceFrame", preview.SourceFrame);
            writer.WriteString("sourcePts", preview.SourcePts);
            writer.WriteString(
                "decodedPixelSha256",
                preview.DecodedPixelSha256);
            writer.WriteString("decoderRevision", preview.DecoderRevision);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static bool HasExactProperties(
        JsonElement value,
        params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return false;
        }
        return names.SetEquals(expected);
    }

    private static bool IsExactString(
        JsonElement value,
        string propertyName,
        string expected)
        => TryGetExactString(value, propertyName, out string actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryGetExactString(
        JsonElement value,
        string propertyName,
        out string result)
    {
        result = "";
        return value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is string parsed
            && (result = parsed) is not null;
    }

    private static bool TryGetInt32(
        JsonElement value,
        string propertyName,
        out int result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out result);
    }

    private static bool IsSafeText(
        string value,
        int maximumLength,
        bool allowLineBreaks)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsControl(current)
                && (!allowLineBreaks || current is not ('\r' or '\n' or '\t')))
            {
                return false;
            }
            if (char.IsLowSurrogate(current))
                return false;
            if (!char.IsHighSurrogate(current))
                continue;
            if (index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }
            index++;
        }
        return true;
    }

    private static bool IsSafeCompilerRevision(string value)
        => IsSafeAsciiToken(value, MaximumCompilerRevisionLength);

    private static bool IsSafeAsciiToken(string value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && value.All(static character => character is >= '!' and <= '~');

    private static bool IsProducerJobId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string compact = value.Replace("-", "", StringComparison.Ordinal);
        return compact.Length == 32
            && compact.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
                    or >= 'A' and <= 'F')
            && (value.Length == 32
                || value.Length == 36
                    && value[8] == '-'
                    && value[13] == '-'
                    && value[18] == '-'
                    && value[23] == '-');
    }
}

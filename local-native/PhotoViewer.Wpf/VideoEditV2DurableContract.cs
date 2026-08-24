using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoViewer.Wpf;

internal sealed record VideoEditV2DurableSettings(
    string AudioPolicy,
    int Steps,
    int Strength,
    int MaximumPixelArea);

internal static class VideoEditV2DurableContract
{
    internal const string ContractId = "PV-ENHANCE-VIDEO-TOOLS-002";
    internal const string Protocol = "aibos-enhancement-video-tools-v2";
    internal const string CapabilityRevision =
        "aibos-video-edit-ready-v1";
    internal const int MaximumSourceIdLength = 32_768;
    internal const long MaximumSafeInteger = 9_007_199_254_740_991;
    internal const long MaximumSourceBytes = 536_870_912;
    internal const long MaximumOutputBytes = 536_870_912;

    private static readonly int[] MaximumPixelAreas =
        [230_400, 307_200, 414_720];
    private static readonly string[] AllowedSourceFps =
        ["24/1", "30/1", "60/1"];

    internal static bool TryBuildEditRequest(
        string sourceId,
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection,
        IReadOnlyList<VideoEditV2PreviewPayload> previews,
        string instructionJa,
        VideoEditV2CompiledCandidate compiled,
        VideoEditV2DurableSettings settings,
        out JsonElement request)
    {
        request = default;
        if (!IsSafeText(
                sourceId,
                MaximumSourceIdLength,
                allowLineBreaks: false,
                requireTrimmed: false)
            || !IsValidSource(source)
            || !IsCanonicalSelection(selection)
            || previews.Count != 3
            || !IsSafeText(
                instructionJa,
                VideoEditV2TransientContract.MaximumInstructionLength,
                allowLineBreaks: true,
                requireTrimmed: true)
            || !IsSafeText(
                compiled.BackendPrompt,
                VideoEditV2TransientContract.MaximumBackendPromptLength,
                allowLineBreaks: true,
                requireTrimmed: true)
            || !IsSafeText(
                compiled.SummaryJa,
                VideoEditV2TransientContract.MaximumSummaryLength,
                allowLineBreaks: true,
                requireTrimmed: true)
            || !IsSafeAsciiToken(
                compiled.CompilerRevision,
                VideoEditV2TransientContract.MaximumCompilerRevisionLength)
            || !VideoEditV2TransientContract.IsExactOfficialRendererSidecar(
                compiled.Renderer,
                compiled.BackendPrompt,
                compiled.CompilerRevision)
            || !VideoEditV2TransientContract.IsLowerSha256(
                compiled.ContextDigest)
            || !TryValidateSettings(settings))
        {
            return false;
        }

        string expectedDigest = VideoEditV2TransientContract
            .ComputeContextDigest(
                source,
                selection,
                previews,
                instructionJa,
                compiled.BackendPrompt,
                compiled.SummaryJa,
                compiled.CompilerRevision,
                compiled.Renderer);
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expectedDigest),
                System.Text.Encoding.ASCII.GetBytes(
                    compiled.ContextDigest)))
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
                "A validated Video Edit source kind became unsupported."),
        };
        request = JsonSerializer.SerializeToElement(new
        {
            sourceId,
            operation = "video",
            mediaKind = "video",
            videoTools = new
            {
                schemaVersion = 2,
                kind = "edit",
                source = sourceBody,
                selection = new
                {
                    startFrame = selection.StartFrame,
                    endFrameExclusive = selection.EndFrameExclusive,
                },
                instructionJa,
                compiled = new
                {
                    backendPrompt = compiled.BackendPrompt,
                    summaryJa = compiled.SummaryJa,
                    compilerRevision = compiled.CompilerRevision,
                    contextDigest = compiled.ContextDigest,
                    renderer = new
                    {
                        taskType = compiled.Renderer.TaskType,
                        guidanceMode = compiled.Renderer.GuidanceMode,
                        promptCompilerRevision =
                            compiled.Renderer.PromptCompilerRevision,
                        rendererPromptSha256 =
                            compiled.Renderer.RendererPromptSha256,
                    },
                },
                audioPolicy = settings.AudioPolicy,
                steps = settings.Steps,
                strength = settings.Strength,
                maximumPixelArea = settings.MaximumPixelArea,
            },
        });
        return true;
    }

    internal static bool TryValidateSettings(
        VideoEditV2DurableSettings settings)
        => settings.AudioPolicy is "preserve" or "mute"
            && settings.Steps is >= 1 and <= 40
            && settings.Strength is >= 10 and <= 100
            && MaximumPixelAreas.Contains(settings.MaximumPixelArea);

    internal static bool IsExactReadyHealth(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(payload, "capabilities")
            || !payload.TryGetProperty(
                "capabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !HasSingleProperty(capabilities, "videoToolsV2")
            || !capabilities.TryGetProperty(
                "videoToolsV2",
                out JsonElement capability)
            || !HasExactProperties(
                capability,
                "contractId",
                "protocol",
                "readerReady",
                "edit",
                "finish",
                "finishModes")
            || !IsExactString(capability, "contractId", ContractId)
            || !IsExactString(capability, "protocol", Protocol)
            || !IsExactBoolean(capability, "readerReady", expected: true)
            || !capability.TryGetProperty("edit", out JsonElement edit)
            || !TryParseReadyEdit(edit)
            || !capability.TryGetProperty("finish", out JsonElement finish)
            || !IsExactDisabledFeature(
                finish,
                "VIDEO_TOOLS_V2_FINISH_RUNTIME_UNPINNED")
            || !capability.TryGetProperty(
                "finishModes",
                out JsonElement finishModes)
            || !HasExactProperties(
                finishModes,
                "fast",
                "standard",
                "quality")
            || !finishModes.TryGetProperty("fast", out JsonElement fast)
            || !IsExactDisabledFeature(
                fast,
                "VIDEO_TOOLS_V2_FINISH_FAST_CANARY_REQUIRED")
            || !finishModes.TryGetProperty(
                "standard",
                out JsonElement standard)
            || !IsExactDisabledFeature(
                standard,
                "VIDEO_TOOLS_V2_FINISH_STANDARD_CANARY_REQUIRED")
            || !finishModes.TryGetProperty(
                "quality",
                out JsonElement quality)
            || !IsExactDisabledFeature(
                quality,
                "VIDEO_TOOLS_V2_FINISH_QUALITY_MODE_MAPPING_CANARY_REQUIRED"))
        {
            return false;
        }
        return true;
    }

    private static bool TryParseReadyEdit(JsonElement edit)
    {
        if (!HasExactProperties(
                edit,
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
                "outputPolicy")
            || !IsExactBoolean(edit, "writerEnabled", expected: true)
            || !IsExactBoolean(edit, "backendConfigured", expected: true)
            || !IsExactBoolean(edit, "runtimeVerified", expected: true)
            || !IsExactBoolean(edit, "ready", expected: true)
            || !IsExactString(edit, "state", "ready")
            || !IsExactNull(edit, "reasonCode")
            || !IsExactString(
                edit,
                "capabilityRevision",
                CapabilityRevision)
            || !edit.TryGetProperty(
                "resolvedBackend",
                out JsonElement backend)
            || !TryParseResolvedBackend(backend)
            || !edit.TryGetProperty("receipts", out JsonElement receipts)
            || !TryParseReceipts(receipts)
            || !edit.TryGetProperty(
                "resourceBounds",
                out JsonElement resources)
            || !TryParseResourceBounds(resources, out long maximumOutputBytes)
            || !edit.TryGetProperty(
                "outputPolicy",
                out JsonElement outputPolicy)
            || !TryParseOutputPolicy(outputPolicy, maximumOutputBytes))
        {
            return false;
        }
        return true;
    }

    private static bool TryParseResolvedBackend(JsonElement backend)
        => HasExactProperties(
                backend,
                "backendId",
                "semanticRole",
                "conditioningKind",
                "genuineSourceVideoConditioning",
                "imageGuideRetake",
                "modelRevision",
                "workflowRevision",
                "promptCompilerRevision",
                "timelineMappingRevision",
                "deliveryMappingRevision")
            && IsExactString(
                backend,
                "backendId",
                "bernini-r-1.3b-edit-candidate-v1")
            && IsExactString(backend, "semanticRole", "semantic-v2v")
            && IsExactString(
                backend,
                "conditioningKind",
                "source-video-conditioned-semantic-v2v")
            && IsExactBoolean(
                backend,
                "genuineSourceVideoConditioning",
                expected: true)
            && IsExactBoolean(
                backend,
                "imageGuideRetake",
                expected: false)
            && HasSafeAsciiProperty(backend, "modelRevision", 128)
            && HasSafeAsciiProperty(backend, "workflowRevision", 128)
            && HasSafeAsciiProperty(
                backend,
                "promptCompilerRevision",
                128)
            && HasSafeAsciiProperty(
                backend,
                "timelineMappingRevision",
                128)
            && HasSafeAsciiProperty(
                backend,
                "deliveryMappingRevision",
                128);

    private static bool TryParseReceipts(JsonElement receipts)
    {
        string[] receiptIds =
        [
            "runtimeReceiptId",
            "modelReceiptId",
            "workflowReceiptId",
            "promptCompilerReceiptId",
            "timelineMapperReceiptId",
            "audioDeliveryReceiptId",
            "qualityCanaryReceiptId",
            "resourceCanaryReceiptId",
            "cancelCanaryReceiptId",
            "recoveryCanaryReceiptId",
            "outputValidatorReceiptId",
        ];
        if (!HasExactProperties(
                receipts,
                receiptIds.Append("receiptSetSha256").ToArray()))
        {
            return false;
        }
        return receiptIds.All(name => HasSafeAsciiProperty(
                receipts,
                name,
                128))
            && receipts.TryGetProperty(
                "receiptSetSha256",
                out JsonElement digest)
            && digest.ValueKind == JsonValueKind.String
            && digest.GetString() is string digestText
            && VideoEditV2TransientContract.IsLowerSha256(digestText);
    }

    private static bool TryParseResourceBounds(
        JsonElement resources,
        out long maximumOutputBytes)
    {
        maximumOutputBytes = 0;
        if (!HasExactProperties(
                resources,
                "maximumSourceBytes",
                "maximumSourceDurationMs",
                "maximumSourceWidth",
                "maximumSourceHeight",
                "maximumSourcePixelArea",
                "maximumSourceFrames",
                "allowedSourceFps",
                "maximumSelectedDurationMs",
                "maximumSelectedFrames",
                "supportedMaximumPixelAreas",
                "minimumSteps",
                "maximumSteps",
                "minimumStrength",
                "maximumStrength",
                "maximumConcurrentExecutions",
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
                300_000)
            || !IsExactInteger(resources, "maximumSourceWidth", 1_920)
            || !IsExactInteger(resources, "maximumSourceHeight", 1_080)
            || !IsExactInteger(
                resources,
                "maximumSourcePixelArea",
                2_073_600)
            || !IsExactInteger(resources, "maximumSourceFrames", 18_000)
            || !IsExactStringArray(
                resources,
                "allowedSourceFps",
                AllowedSourceFps)
            || !IsExactInteger(
                resources,
                "maximumSelectedDurationMs",
                5_000)
            || !IsExactInteger(resources, "maximumSelectedFrames", 300)
            || !IsExactIntegerArray(
                resources,
                "supportedMaximumPixelAreas",
                MaximumPixelAreas)
            || !IsExactInteger(resources, "minimumSteps", 1)
            || !IsExactInteger(resources, "maximumSteps", 40)
            || !IsExactInteger(resources, "minimumStrength", 10)
            || !IsExactInteger(resources, "maximumStrength", 100)
            || !IsExactInteger(
                resources,
                "maximumConcurrentExecutions",
                1)
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
                out maximumOutputBytes)
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
        return true;
    }

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
                "maximumBytes")
            && IsExactString(
                output,
                "revision",
                "aibos-video-edit-child-mp4-validator-v1")
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
            && IsExactInteger(output, "maximumBytes", maximumOutputBytes);

    private static bool IsExactDisabledFeature(
        JsonElement feature,
        string reasonCode)
        => HasExactProperties(
                feature,
                "writerEnabled",
                "backendConfigured",
                "runtimeVerified",
                "ready",
                "state",
                "reasonCode")
            && IsExactBoolean(feature, "writerEnabled", expected: false)
            && IsExactBoolean(
                feature,
                "backendConfigured",
                expected: false)
            && IsExactBoolean(
                feature,
                "runtimeVerified",
                expected: false)
            && IsExactBoolean(feature, "ready", expected: false)
            && IsExactString(feature, "state", "disabled")
            && IsExactString(feature, "reasonCode", reasonCode);

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
        if (source.Kind != "displayed-file"
            || source.SourceVideoJobId is not null
            || source.Path is not string path
            || string.IsNullOrWhiteSpace(path)
            || path.Length > 32_767
            || !Path.IsPathFullyQualified(path)
            || source.Size is not long size
            || size is <= 0 or > MaximumSourceBytes
            || source.MtimeMs is not long mtimeMs
            || Math.Abs((decimal)mtimeMs) > MaximumSafeInteger
            || source.Sha256 is not string sha256
            || !VideoEditV2TransientContract.IsLowerSha256(sha256))
        {
            return false;
        }
        return true;
    }

    private static bool IsCanonicalSelection(VideoEditV2SelectionPlan plan)
        => VideoEditV2Planner.TryPlan(
                plan.SourceFrameCount,
                plan.FpsNumerator,
                plan.FpsDenominator,
                plan.StartFrame,
                plan.EndFrameExclusive,
                out VideoEditV2SelectionPlan canonical,
                out _)
            && canonical == plan
            && plan.SelectedFrameCount <= 300;

    private static bool IsProducerJobId(string value)
    {
        if (value.Length == 32)
        {
            return value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
                    or >= 'A' and <= 'F');
        }
        return Guid.TryParseExact(value, "D", out _);
    }

    private static bool IsSafeText(
        string value,
        int maximumLength,
        bool allowLineBreaks,
        bool requireTrimmed)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || requireTrimmed
                && !string.Equals(
                    value,
                    value.Trim(),
                    StringComparison.Ordinal))
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
            if (char.IsLowSurrogate(character)
                || char.IsControl(character)
                    && !(allowLineBreaks
                        && character is '\r' or '\n' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeAsciiToken(string value, int maximumLength)
        => value.Length is > 0
            && value.Length <= maximumLength
            && value.All(static character =>
                character is >= (char)0x21 and <= (char)0x7e);

    private static bool HasSafeAsciiProperty(
        JsonElement element,
        string name,
        int maximumLength)
        => element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is string value
            && IsSafeAsciiToken(value, maximumLength);

    private static bool HasSingleProperty(JsonElement element, string name)
    {
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

    private static bool IsExactString(
        JsonElement element,
        string name,
        string expected)
        => element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(
                property.GetString(),
                expected,
                StringComparison.Ordinal);

    private static bool IsExactBoolean(
        JsonElement element,
        string name,
        bool expected)
        => element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind is (
                JsonValueKind.True or JsonValueKind.False)
            && property.GetBoolean() == expected;

    private static bool IsExactNull(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Null;

    private static bool IsExactInteger(
        JsonElement element,
        string name,
        long expected)
        => element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out long value)
            && value == expected;

    private static bool TryGetPositiveSafeInteger(
        JsonElement element,
        string name,
        out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value is > 0 and <= MaximumSafeInteger;
    }

    private static bool IsExactStringArray(
        JsonElement element,
        string name,
        IReadOnlyList<string> expected)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() != expected.Count)
        {
            return false;
        }
        int index = 0;
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !string.Equals(
                    item.GetString(),
                    expected[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
            index++;
        }
        return true;
    }

    private static bool IsExactIntegerArray(
        JsonElement element,
        string name,
        IReadOnlyList<int> expected)
    {
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() != expected.Count)
        {
            return false;
        }
        int index = 0;
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetInt32(out int value)
                || value != expected[index])
            {
                return false;
            }
            index++;
        }
        return true;
    }
}

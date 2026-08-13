using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string I2iV2Operation = "i2i";
    private const string I2iV2ContractId = "PV-ENHANCE-I2I-002";
    private const string I2iV2PresetId = "flux2-i2i-edit-v2";
    private const string I2iV2AdapterId = "comfyui-flux2-i2i-v2";
    private const string I2iV2WorkflowRevision = "i2i-flux2-klein9b-multitarget-v2";
    private const string I2iV2MaskRevision = "sam31-mediapipe-target-v2";
    private const int I2iV2InstructionMaximumCodePoints = 2_000;
    private const int I2iV2DetailsMaximumCodePoints = 2_000;
    private const int I2iV2PromptMaximumCodePoints = 8_000;

    private static readonly I2iV2TargetChoice[] I2iV2Targets =
    [
        new(
            "outfit",
            "服装",
            "顔・髪・体格・ポーズ・カメラ・背景を保ち、指定した服装だけを変更します。"),
        new(
            "expression",
            "表情",
            "同じ人物・顔立ち・視線・頭の角度・構図を保ち、指定した表情だけを変更します。"),
        new(
            "background",
            "場所・背景",
            "人物を元画像から保護し、指定した場所・背景だけを変更します。"),
        new(
            "pose",
            "ポーズ（実験的）",
            "顔の同一性と元の画角を保護しながらポーズを変更します。体や手の再生成を伴う実験的な処理です。"),
    ];

    private static readonly I2iV2TemplateChoice[] I2iV2Templates =
    [
        new(
            "outfit-athletic-teal",
            "深い青緑のスポーツウェア",
            "outfit",
            "Change the outfit to an opaque dark teal sleeveless athletic top with the same scoop neckline, length, fit, and silhouette as the existing top."),
        new(
            "outfit-athletic-black",
            "黒のノースリーブスポーツウェア",
            "outfit",
            "Change the outfit to an opaque matte black sleeveless top with the same scoop neckline, length, fit, and silhouette as the existing top."),
        new(
            "outfit-knit-cream-sleeveless",
            "クリーム色のノースリーブニット",
            "outfit",
            "Change the outfit to an opaque cream ribbed knit sleeveless top with the same scoop neckline, length, fit, and silhouette as the existing top."),
        new(
            "expression-gentle-smile",
            "自然な微笑み",
            "expression",
            "Change the expression to a gentle closed-mouth smile while keeping the same gaze, head angle, facial structure, and composition."),
        new(
            "expression-subtle-smirk",
            "控えめな片側の微笑み",
            "expression",
            "Change only the mouth to a subtle asymmetric closed-mouth smirk while keeping the eyes, gaze, head angle, and facial structure unchanged."),
        new(
            "expression-worried-mouth",
            "少し困った口元",
            "expression",
            "Change only the mouth to a slightly worried closed-mouth expression with gently downturned lip corners while keeping the eyes, gaze, head angle, and facial structure unchanged."),
        new(
            "background-neon-alley",
            "雨のネオン街",
            "background",
            "Change the location to a rainy neon-lit city alley at night while keeping the person, camera position, crop, and subject lighting direction unchanged."),
        new(
            "background-beach",
            "夕方の海辺",
            "background",
            "Change the location to a quiet beach at golden hour while keeping the person, pose, camera, crop, and scale unchanged."),
        new(
            "background-studio",
            "シンプルな撮影スタジオ",
            "background",
            "Change the background to a clean warm-gray photography studio with soft natural shadows while keeping the person and composition unchanged."),
        new(
            "pose-relaxed",
            "自然に力を抜いた立ち姿",
            "pose",
            "Change the pose to a relaxed standing posture with lowered shoulders and arms resting naturally while keeping the same head position, camera, crop, and person identity."),
        new(
            "pose-hand-on-hip",
            "片手を腰に添える",
            "pose",
            "Change the pose so one hand rests naturally on the hip while keeping the same head position, body scale, camera, crop, and person identity."),
        new(
            "pose-three-quarter",
            "軽い斜め向き",
            "pose",
            "Change the body pose to a subtle three-quarter turn while keeping the face directed as before and preserving the same camera, crop, and person identity."),
    ];
    private static readonly string[] I2iV2OptionalTimestampFields =
    [
        "startedAt",
        "finishedAt",
        "lastHeartbeatAt",
        "lastProgressAt",
    ];
    private static readonly string[] I2iV2OptionalIdFields =
    [
        "runId",
        "workerInstanceId",
        "externalPromptId",
    ];

    private bool _i2iV2CapabilityCheckPending;
    private bool _i2iV2CapabilityUnknown;
    private bool _i2iV2RequestPending;
    private bool _syncingI2iV2Controls;
    private long _i2iV2BoardGeneration;
    private IInputElement? _i2iV2FocusBeforeBoard;
    private I2iEditSource? _i2iV2EditSource;
    private I2iV2CapabilityState? _i2iV2Capability;

    private sealed record I2iV2TargetChoice(
        string Id,
        string Label,
        string PreservationExplanation);

    private sealed record I2iV2TemplateChoice(
        string Id,
        string Label,
        string Target,
        string Instruction)
    {
        public static I2iV2TemplateChoice Custom(string target)
            => new("custom", "カスタム", target, "");
    }

    private sealed record I2iV2CapabilityState(
        bool ReaderReady,
        bool WriterEnabled,
        bool BackendConfigured,
        bool PromptPolicyConfigured,
        bool DeclaredReady,
        IReadOnlySet<string> SupportedTargets,
        string AdapterId,
        string? PromptPolicyRevision,
        string? PromptPolicySha256,
        string IssueCode)
    {
        public bool IsReadyFor(string target)
            => ReaderReady
                && WriterEnabled
                && BackendConfigured
                && PromptPolicyConfigured
                && DeclaredReady
                && !string.IsNullOrWhiteSpace(PromptPolicyRevision)
                && !string.IsNullOrWhiteSpace(PromptPolicySha256)
                && SupportedTargets.Contains(target);
    }

    private sealed record I2iV2JobInfo(
        int SchemaVersion,
        string Target,
        string Instruction,
        string InstructionSummary,
        string Details,
        int Seed,
        string PromptPolicyRevision,
        string PromptPolicySha256,
        string AdapterId);

    private static bool IsKnownI2iV2Target(string? target)
        => target is "outfit" or "expression" or "background" or "pose";

    private static bool IsNormalizedI2iV2Text(
        string? value,
        int maximumCodePoints,
        bool allowEmpty)
    {
        if (value is null
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.EnumerateRunes().Take(maximumCodePoints + 1).Count()
                > maximumCodePoints)
        {
            return false;
        }
        return allowEmpty || value.Length > 0;
    }

    private static string BuildI2iV2InstructionSummary(string instruction)
    {
        string compact = string.Join(
            " ",
            instruction.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        const int maximum = 160;
        return compact.Length <= maximum
            ? compact
            : compact[..(maximum - 1)] + "…";
    }

    private static bool TryReadNullableI2iV2String(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!TryGetUniqueI2iProperty(element, propertyName, out JsonElement property))
            return false;
        if (property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return value is not null;
    }

    private static bool TryParseI2iV2Capability(
        JsonElement payload,
        out I2iV2CapabilityState capability)
    {
        capability = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(payload, "capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(capabilities, "i2iV2", out JsonElement i2i)
            || i2i.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iString(i2i, "contractId", out string? contractId)
            || !string.Equals(contractId, I2iV2ContractId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(i2i, "operation", out string? operation)
            || !string.Equals(operation, I2iV2Operation, StringComparison.Ordinal)
            || !TryGetUniqueI2iInt32(i2i, "schemaVersion", out int schemaVersion)
            || schemaVersion != 2
            || !TryGetUniqueI2iBoolean(i2i, "readerReady", out bool readerReady)
            || !TryGetUniqueI2iBoolean(i2i, "writerEnabled", out bool writerEnabled)
            || !TryGetUniqueI2iBoolean(i2i, "backendConfigured", out bool backendConfigured)
            || !TryGetUniqueI2iBoolean(i2i, "promptPolicyConfigured", out bool promptPolicyConfigured)
            || !TryGetUniqueI2iBoolean(i2i, "ready", out bool ready)
            || !TryGetUniqueI2iProperty(i2i, "supportedTargets", out JsonElement supportedTargets)
            || supportedTargets.ValueKind != JsonValueKind.Array
            || !TryGetUniqueI2iString(i2i, "backendId", out string? backendId)
            || !string.Equals(backendId, I2iV2AdapterId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(i2i, "workflowRevision", out string? workflowRevision)
            || !string.Equals(workflowRevision, I2iV2WorkflowRevision, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(i2i, "maskRevision", out string? maskRevision)
            || !string.Equals(maskRevision, I2iV2MaskRevision, StringComparison.Ordinal)
            || !TryReadNullableI2iV2String(i2i, "promptPolicyRevision", out string? promptPolicyRevision)
            || !TryReadNullableI2iV2String(i2i, "promptPolicySha256", out string? promptPolicySha256)
            || !TryReadNullableI2iV2String(i2i, "issueCode", out string? issueCode))
        {
            return false;
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement targetElement in supportedTargets.EnumerateArray())
        {
            if (targetElement.ValueKind != JsonValueKind.String
                || !IsKnownI2iV2Target(targetElement.GetString())
                || !targets.Add(targetElement.GetString()!))
            {
                return false;
            }
        }

        bool policyRevisionValid = promptPolicyRevision is null
            || IsNormalizedI2iV2Text(promptPolicyRevision, 128, allowEmpty: false);
        bool policyShaValid = promptPolicySha256 is null
            || IsLowerHex(promptPolicySha256, 64);
        bool issueCodeValid = issueCode is null
            || IsNormalizedI2iV2Text(issueCode, 128, allowEmpty: false);
        if (!policyRevisionValid
            || !policyShaValid
            || !issueCodeValid
            || (promptPolicyConfigured
                && (promptPolicyRevision is null || promptPolicySha256 is null)))
        {
            return false;
        }

        capability = new I2iV2CapabilityState(
            readerReady,
            writerEnabled,
            backendConfigured,
            promptPolicyConfigured,
            ready,
            targets,
            backendId!,
            promptPolicyRevision,
            promptPolicySha256,
            issueCode ?? "WORKFLOW_UNVERIFIED");
        return true;
    }

    private static bool IsI2iV2MutationSafe(JsonElement job)
        => TryReadI2iV2JobInfo(job, out _);

    private static bool TryGetOptionalUniqueI2iV2Property(
        JsonElement element,
        string propertyName,
        out bool present,
        out JsonElement value)
    {
        present = false;
        value = default;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                continue;
            if (present)
                return false;
            present = true;
            value = property.Value;
        }
        return true;
    }

    private static bool IsValidI2iV2Timestamp(string? value)
        => IsNormalizedI2iV2Text(value, 128, allowEmpty: false)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);

    private static bool HasSafeI2iV2MutableEnvelope(JsonElement job)
    {
        if (!TryGetUniqueI2iString(job, "id", out string? id)
            || !IsNormalizedI2iV2Text(id, 128, allowEmpty: false)
            || !TryGetUniqueI2iString(job, "status", out string? status)
            || status is not (
                "queued"
                or "running"
                or "succeeded"
                or "failed"
                or "canceled"
                or "deleted")
            || !TryGetUniqueI2iInt32(job, "progress", out int progress)
            || progress is < 0 or > 100
            || !TryGetUniqueI2iString(job, "createdAt", out string? createdAt)
            || !IsValidI2iV2Timestamp(createdAt)
            || !TryGetUniqueI2iString(job, "updatedAt", out string? updatedAt)
            || !IsValidI2iV2Timestamp(updatedAt))
        {
            return false;
        }

        if (!TryGetOptionalUniqueI2iV2Property(
                job,
                "cancelRequested",
                out bool hasCancelRequested,
                out JsonElement cancelRequested)
            || (hasCancelRequested
                && cancelRequested.ValueKind is not (
                    JsonValueKind.True
                    or JsonValueKind.False))
            || !TryGetOptionalUniqueI2iV2Property(
                job,
                "queueOrder",
                out bool hasQueueOrder,
                out JsonElement queueOrder)
            || (hasQueueOrder
                && (queueOrder.ValueKind != JsonValueKind.Number
                    || !queueOrder.TryGetInt32(out int parsedQueueOrder)
                    || parsedQueueOrder < 0))
            || !TryGetOptionalUniqueI2iV2Property(
                job,
                "outputPath",
                out bool hasOutputPath,
                out JsonElement outputPath)
            || (hasOutputPath
                && (outputPath.ValueKind != JsonValueKind.String
                    || !IsNormalizedI2iV2Text(
                        outputPath.GetString(),
                        32_767,
                        allowEmpty: false)))
            || (status == "succeeded" && !hasOutputPath)
            || !TryGetOptionalUniqueI2iV2Property(
                job,
                "errorMessage",
                out bool hasErrorMessage,
                out JsonElement errorMessage)
            || (hasErrorMessage
                && (errorMessage.ValueKind != JsonValueKind.String
                    || !IsNormalizedI2iV2Text(
                        errorMessage.GetString(),
                        16_384,
                        allowEmpty: false))))
        {
            return false;
        }

        foreach (string timestampName in I2iV2OptionalTimestampFields)
        {
            if (!TryGetOptionalUniqueI2iV2Property(
                    job,
                    timestampName,
                    out bool present,
                    out JsonElement timestamp)
                || (present
                    && (timestamp.ValueKind != JsonValueKind.String
                        || !IsValidI2iV2Timestamp(timestamp.GetString()))))
            {
                return false;
            }
        }

        foreach (string stringName in I2iV2OptionalIdFields)
        {
            if (!TryGetOptionalUniqueI2iV2Property(
                    job,
                    stringName,
                    out bool present,
                    out JsonElement value)
                || (present
                    && (value.ValueKind != JsonValueKind.String
                        || !IsNormalizedI2iV2Text(
                            value.GetString(),
                            256,
                            allowEmpty: false))))
            {
                return false;
            }
        }

        return TryGetOptionalUniqueI2iV2Property(
                job,
                "externalProcessId",
                out bool hasExternalProcessId,
                out JsonElement externalProcessId)
            && (!hasExternalProcessId
                || (externalProcessId.ValueKind == JsonValueKind.Number
                    && externalProcessId.TryGetInt32(out int processId)
                    && processId > 0));
    }

    private static bool TryReadI2iV2JobInfo(
        JsonElement job,
        out I2iV2JobInfo info)
    {
        info = null!;
        if (job.ValueKind != JsonValueKind.Object
            || !HasSafeI2iV2MutableEnvelope(job)
            || !TryGetUniqueI2iString(job, "operation", out string? operation)
            || !string.Equals(operation, I2iV2Operation, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "mediaKind", out string? mediaKind)
            || !string.Equals(mediaKind, "image", StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "adapterId", out string? adapterId)
            || !string.Equals(adapterId, I2iV2AdapterId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "presetId", out string? presetId)
            || !string.Equals(presetId, I2iV2PresetId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "sourceId", out _)
            || !TryGetUniqueI2iString(job, "sourcePath", out _)
            || !TryGetUniqueI2iProperty(job, "sourceSignature", out JsonElement sourceSignature)
            || sourceSignature.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iDouble(sourceSignature, "size", out double sourceSize)
            || sourceSize < 0d
            || !TryGetUniqueI2iDouble(sourceSignature, "mtimeMs", out _)
            || !TryReadOptionalI2iSourceProducerJobId(job, out _)
            || job.EnumerateObject().Any(static property =>
                property.Name is "sourceOutputPath"
                    or "sourceImagePath"
                    or "sourceRecoveredOutputPath"
                    or "sourceRecoveredAdapterId"
                    or "sourceRecoveredSignature"
                    or "sourceRecoveredSha256")
            || !TryGetUniqueI2iProperty(job, "preset", out JsonElement preset)
            || preset.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(preset, "options", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object
            || !HasExactI2iV2PresetEnvelope(job, preset)
            || !TryGetUniqueI2iInt32(options, "i2iSchemaVersion", out int schemaVersion)
            || schemaVersion != 2
            || !TryGetUniqueI2iString(options, "target", out string? target)
            || !IsKnownI2iV2Target(target)
            || !TryGetUniqueI2iString(options, "instruction", out string? instruction)
            || !IsNormalizedI2iV2Text(
                instruction,
                I2iV2InstructionMaximumCodePoints,
                allowEmpty: false)
            || !TryGetUniqueI2iStringAllowEmpty(options, "details", out string? details)
            || !IsNormalizedI2iV2Text(
                details,
                I2iV2DetailsMaximumCodePoints,
                allowEmpty: true)
            || !TryGetUniqueI2iBoolean(options, "preserveIdentity", out bool preserveIdentity)
            || !preserveIdentity
            || !TryGetUniqueI2iBoolean(options, "preserveComposition", out bool preserveComposition)
            || !preserveComposition
            || !TryGetUniqueI2iString(options, "prompt", out string? prompt)
            || !IsNormalizedI2iV2Text(
                prompt,
                I2iV2PromptMaximumCodePoints,
                allowEmpty: false)
            || !TryGetUniqueI2iStringAllowEmpty(options, "negativePrompt", out string? negativePrompt)
            || !string.Equals(negativePrompt, "", StringComparison.Ordinal)
            || !TryGetUniqueI2iInt32(options, "steps", out int steps)
            || steps != 8
            || !TryGetUniqueI2iDouble(options, "cfgScale", out double cfgScale)
            || cfgScale != 1d
            || !TryGetUniqueI2iInt32(options, "maxDimension", out int maxDimension)
            || maxDimension != 1280
            || !TryGetUniqueI2iInt32(options, "seed", out int seed)
            || seed < 0
            || !TryGetUniqueI2iString(options, "workflowRevision", out string? workflowRevision)
            || !string.Equals(workflowRevision, I2iV2WorkflowRevision, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(options, "maskRevision", out string? maskRevision)
            || !string.Equals(maskRevision, I2iV2MaskRevision, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(options, "promptPolicyRevision", out string? promptPolicyRevision)
            || !IsNormalizedI2iV2Text(promptPolicyRevision, 128, allowEmpty: false)
            || !TryGetUniqueI2iString(options, "promptPolicySha256", out string? promptPolicySha256)
            || !IsLowerHex(promptPolicySha256, 64)
            || !TryGetUniqueI2iString(options, "promptSnapshotSha256", out string? promptSnapshotSha256)
            || !IsLowerHex(promptSnapshotSha256, 64)
            || !string.Equals(
                promptSnapshotSha256,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt!)))
                    .ToLowerInvariant(),
                StringComparison.Ordinal)
            || !TryGetUniqueI2iBoolean(options, "loraEnabled", out bool loraEnabled)
            || loraEnabled)
        {
            return false;
        }

        info = new I2iV2JobInfo(
            schemaVersion,
            target!,
            instruction!,
            BuildI2iV2InstructionSummary(instruction!),
            details!,
            seed,
            promptPolicyRevision!,
            promptPolicySha256!,
            adapterId!);
        return true;
    }

    private static bool HasExactI2iV2PresetEnvelope(
        JsonElement job,
        JsonElement preset)
    {
        if (!TryGetUniqueI2iString(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !TryGetUniqueI2iString(preset, "id", out string? presetId)
            || !string.Equals(presetId, I2iV2PresetId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(preset, "label", out string? label)
            || !string.Equals(label, "FLUX.2 guided edit v2", StringComparison.Ordinal)
            || !TryGetUniqueI2iString(preset, "modelFamily", out string? modelFamily)
            || !string.Equals(modelFamily, "photo", StringComparison.Ordinal)
            || !TryGetUniqueI2iString(preset, "modelName", out string? modelName)
            || !string.Equals(modelName, "flux-2-klein-9b-Q4_K_M.gguf", StringComparison.Ordinal)
            || !TryGetUniqueI2iDouble(preset, "scale", out double scale)
            || scale != 1d
            || !TryGetUniqueI2iString(preset, "outputFormat", out string? outputFormat)
            || !string.Equals(outputFormat, "png", StringComparison.Ordinal)
            || !TryGetUniqueI2iDouble(preset, "denoise", out double denoise)
            || denoise != 0d
            || !TryGetUniqueI2iDouble(preset, "sharpen", out double sharpen)
            || sharpen != 0d
            || !TryGetUniqueI2iDouble(preset, "detail", out double detail)
            || detail != 0d
            || !TryGetUniqueI2iDouble(preset, "smoothness", out double smoothness)
            || smoothness != 0d
            || !TryGetUniqueI2iDouble(preset, "colorBrightness", out double brightness)
            || brightness != 0d
            || !TryGetUniqueI2iDouble(preset, "colorContrast", out double contrast)
            || contrast != 0d
            || !TryGetUniqueI2iDouble(preset, "colorSaturation", out double saturation)
            || saturation != 0d)
        {
            return false;
        }

        string expectedHash = ComputeI2iV2PresetHash(preset);
        return string.Equals(presetHash, expectedHash, StringComparison.Ordinal);
    }

    private static string ComputeI2iV2PresetHash(JsonElement preset)
    {
        string[] hashedPropertyNames =
        [
            "id",
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
            "options",
        ];
        var builder = new StringBuilder();
        builder.Append('{');
        for (int index = 0; index < hashedPropertyNames.Length; index++)
        {
            string propertyName = hashedPropertyNames[index];
            if (!TryGetUniqueI2iProperty(preset, propertyName, out JsonElement value))
                return "";
            if (index > 0)
                builder.Append(',');
            builder.Append(JsonSerializer.Serialize(propertyName, VideoStableJsonOptions));
            builder.Append(':');
            AppendI2iJsonInDocumentOrder(builder, value);
        }
        builder.Append('}');
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant()[..12];
    }

    private async void OpenModalI2iEditV2_Click(
        object sender,
        RoutedEventArgs e)
        => await OpenI2iV2EditBoardAsync();

    private async Task<bool> OpenI2iV2EditBoardAsync()
    {
        if (!TryResolveCurrentModalI2iEditSource(
                out I2iEditSource source,
                out string error))
        {
            SetStatusToast(error);
            return false;
        }
        if (I2iEditDialog?.Visibility == Visibility.Visible)
        {
            if (_i2iRequestPending)
            {
                SetStatusToast("別のAI編集をキューへ追加中です。完了後に切り替えてください。");
                return false;
            }
            CloseI2iEditBoard(restoreFocus: false);
        }

        _i2iV2FocusBeforeBoard = Keyboard.FocusedElement;
        _i2iV2EditSource = source;
        _i2iV2Capability = null;
        _i2iV2CapabilityUnknown = false;
        _i2iV2CapabilityCheckPending = true;
        _i2iV2RequestPending = false;
        long generation = ++_i2iV2BoardGeneration;

        _syncingI2iV2Controls = true;
        I2iV2TargetComboBox.ItemsSource = I2iV2Targets;
        I2iV2TargetComboBox.SelectedIndex = 0;
        RebuildI2iV2TemplateChoices();
        I2iV2InstructionTextBox.Text = "";
        I2iV2DetailsTextBox.Text = "";
        SelectSeedMode(I2iV2SeedModeComboBox, fixedMode: false);
        I2iV2SeedValueTextBox.Text = DefaultFixedSeedValue.ToString(
            CultureInfo.InvariantCulture);
        I2iV2SeedValueTextBox.IsEnabled = false;
        _syncingI2iV2Controls = false;

        I2iV2SourceText.Text = source.Label;
        I2iV2EditDialog.Visibility = Visibility.Visible;
        UpdateI2iV2BoardPresentation("バックエンドの対応状態を確認しています…");
        _ = Dispatcher.BeginInvoke(
            I2iV2InstructionTextBox.Focus,
            DispatcherPriority.Input);
        await RefreshI2iV2CapabilityForBoardAsync(generation);
        return I2iV2EditDialog.Visibility == Visibility.Visible;
    }

    private async Task RefreshI2iV2CapabilityForBoardAsync(long generation)
    {
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            timeoutMilliseconds: DurableEnqueueActionDeadlineMilliseconds);
        if (generation != _i2iV2BoardGeneration
            || I2iV2EditDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        _i2iV2CapabilityCheckPending = false;
        EnhancementEnqueueBackendMode mode = EnhancementEnqueueProbePolicy.Classify(
            response.Ok,
            response.StatusCode,
            response.Payload);
        if (mode == EnhancementEnqueueBackendMode.Unknown)
        {
            _i2iV2Capability = null;
            _i2iV2CapabilityUnknown = true;
            UpdateI2iV2BoardPresentation(
                "バックエンドのAI編集対応を確認できません。ジョブは追加されません。");
            return;
        }

        _i2iV2CapabilityUnknown = false;
        if (!response.Ok
            || response.Payload is not JsonElement payload
            || !TryParseI2iV2Capability(payload, out I2iV2CapabilityState capability))
        {
            _i2iV2Capability = null;
            UpdateI2iV2BoardPresentation(
                "新しいAI編集の対応状態を確認できません。既存Jobsと髪色編集は引き続き利用できます。");
            return;
        }

        _i2iV2Capability = capability;
        string target = SelectedI2iV2Target();
        UpdateI2iV2BoardPresentation(
            capability.IsReadyFor(target)
                ? "変更内容を確認して、明示的にキューへ追加してください。"
                : $"選択した編集はまだ準備中です（{capability.IssueCode}）。ジョブは追加されません。");
    }

    private string SelectedI2iV2Target()
        => I2iV2TargetComboBox?.SelectedItem is I2iV2TargetChoice target
            ? target.Id
            : "";

    private void RebuildI2iV2TemplateChoices()
    {
        string target = SelectedI2iV2Target();
        I2iV2TemplateChoice[] choices =
        [
            I2iV2TemplateChoice.Custom(target),
            .. I2iV2Templates.Where(template => template.Target == target),
        ];
        I2iV2TemplateComboBox.ItemsSource = choices;
        I2iV2TemplateComboBox.SelectedIndex = 0;
    }

    private void I2iV2Target_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingI2iV2Controls || I2iV2TemplateComboBox is null)
            return;
        _syncingI2iV2Controls = true;
        RebuildI2iV2TemplateChoices();
        I2iV2InstructionTextBox.Text = "";
        I2iV2DetailsTextBox.Text = "";
        _syncingI2iV2Controls = false;
        UpdateI2iV2BoardPresentation();
    }

    private void I2iV2Template_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingI2iV2Controls
            || I2iV2TemplateComboBox?.SelectedItem is not I2iV2TemplateChoice template
            || template.Id == "custom")
        {
            return;
        }
        _syncingI2iV2Controls = true;
        I2iV2InstructionTextBox.Text = template.Instruction;
        _syncingI2iV2Controls = false;
        UpdateI2iV2BoardPresentation();
        I2iV2InstructionTextBox.Focus();
        I2iV2InstructionTextBox.CaretIndex = I2iV2InstructionTextBox.Text.Length;
    }

    private void I2iV2Instruction_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingI2iV2Controls
            && I2iV2TemplateComboBox?.SelectedItem is I2iV2TemplateChoice template
            && template.Id != "custom")
        {
            _syncingI2iV2Controls = true;
            I2iV2TemplateComboBox.SelectedIndex = 0;
            _syncingI2iV2Controls = false;
        }
        UpdateI2iV2BoardPresentation();
    }

    private void I2iV2EditField_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateI2iV2BoardPresentation();

    private void I2iV2SeedMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingI2iV2Controls || I2iV2SeedValueTextBox is null)
            return;
        I2iV2SeedValueTextBox.IsEnabled = SelectedSeedModeIsFixed(
            I2iV2SeedModeComboBox);
        UpdateI2iV2BoardPresentation();
    }

    private bool TryResolveI2iV2Seed(out int? seed, out string error)
    {
        seed = null;
        error = "";
        if (!SelectedSeedModeIsFixed(I2iV2SeedModeComboBox))
            return true;
        if (TryParseFixedSeed(
                I2iV2SeedValueTextBox.Text.Trim(),
                out int fixedSeed)
            && fixedSeed >= 0)
        {
            seed = fixedSeed;
            return true;
        }
        error = "Fixed seed は 0〜2147483647 の整数で入力してください。";
        return false;
    }

    private void UpdateI2iV2BoardPresentation(string? statusOverride = null)
    {
        if (I2iV2EditDialog is null)
            return;

        string target = SelectedI2iV2Target();
        string instruction = I2iV2InstructionTextBox?.Text.Trim() ?? "";
        string details = I2iV2DetailsTextBox?.Text.Trim() ?? "";
        bool fieldsValid = IsKnownI2iV2Target(target)
            && IsNormalizedI2iV2Text(
                instruction,
                I2iV2InstructionMaximumCodePoints,
                allowEmpty: false)
            && IsNormalizedI2iV2Text(
                details,
                I2iV2DetailsMaximumCodePoints,
                allowEmpty: true);
        bool seedValid = TryResolveI2iV2Seed(out _, out string seedError);
        string sourceError = "AI編集の入力を確認できません。ボードを開き直してください。";
        bool sourceValid = _i2iV2EditSource is not null
            && RevalidateI2iEditSource(_i2iV2EditSource, out _, out sourceError);
        bool targetReady = _i2iV2Capability?.IsReadyFor(target) == true;
        bool targetQueueable = targetReady;

        I2iV2SeedStatusText.Text = seedValid ? "" : seedError;
        I2iV2SeedStatusText.Visibility = seedValid
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (I2iV2TargetComboBox.SelectedItem is I2iV2TargetChoice choice)
            I2iV2PreservationText.Text = choice.PreservationExplanation;

        string status = statusOverride
            ?? (_i2iV2CapabilityCheckPending
                ? "バックエンドの対応状態を確認しています…"
                : _i2iV2CapabilityUnknown
                    ? "バックエンドのAI編集対応を確認できません。ジョブは追加されません。"
                    : !targetReady
                        ? "選択したAI編集がreadyになるまでジョブは追加できません。"
                        : !sourceValid
                            ? sourceError
                            : !fieldsValid
                                ? "変更内容（プロンプト）は必須です。各入力は2000文字相当までです。"
                                : target == "pose"
                                    ? "ポーズ変更は実験的です。顔と構図を保護しますが、体や手は再生成されます。"
                                    : "顔の同一性と元の構図を基本設定として自動保持します。");
        I2iV2EditStatusText.Text = status;
        I2iV2QueueButton.IsEnabled = targetQueueable
            && !_i2iV2CapabilityCheckPending
            && !_i2iV2RequestPending
            && sourceValid
            && fieldsValid
            && seedValid;
        I2iV2CloseButton.IsEnabled = !_i2iV2RequestPending;
        I2iV2ResetButton.IsEnabled = !_i2iV2RequestPending;
    }

    private void ResetI2iV2Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_i2iV2RequestPending)
            return;
        _syncingI2iV2Controls = true;
        RebuildI2iV2TemplateChoices();
        I2iV2InstructionTextBox.Text = "";
        I2iV2DetailsTextBox.Text = "";
        SelectSeedMode(I2iV2SeedModeComboBox, fixedMode: false);
        I2iV2SeedValueTextBox.Text = DefaultFixedSeedValue.ToString(
            CultureInfo.InvariantCulture);
        I2iV2SeedValueTextBox.IsEnabled = false;
        _syncingI2iV2Controls = false;
        UpdateI2iV2BoardPresentation();
        I2iV2InstructionTextBox.Focus();
    }

    private async void QueueI2iV2Edit_Click(object sender, RoutedEventArgs e)
        => await QueueI2iV2EditAsync();

    private async Task<bool> QueueI2iV2EditAsync()
    {
        if (_i2iV2RequestPending
            || _i2iV2EditSource is not I2iEditSource source)
        {
            return false;
        }
        if (!RevalidateI2iEditSource(source, out Tile tile, out string sourceError))
        {
            UpdateI2iV2BoardPresentation(sourceError);
            return false;
        }
        Func<string?>? prePublishValidator =
            CaptureExternalFileDropPrePublishValidator(tile);

        string target = SelectedI2iV2Target();
        I2iV2CapabilityState? boardCapability = _i2iV2Capability;
        if (_i2iV2CapabilityCheckPending
            || boardCapability is null
            || !boardCapability.IsReadyFor(target))
        {
            UpdateI2iV2BoardPresentation(
                "バックエンドのAI編集対応を確認できません。ジョブは追加されません。");
            return false;
        }
        string instruction = I2iV2InstructionTextBox.Text.Trim();
        string details = I2iV2DetailsTextBox.Text.Trim();
        bool seedValid = TryResolveI2iV2Seed(
            out int? seed,
            out string seedError);
        if (!IsKnownI2iV2Target(target)
            || !IsNormalizedI2iV2Text(
                instruction,
                I2iV2InstructionMaximumCodePoints,
                allowEmpty: false)
            || !IsNormalizedI2iV2Text(
                details,
                I2iV2DetailsMaximumCodePoints,
                allowEmpty: true)
            || !seedValid)
        {
            UpdateI2iV2BoardPresentation(
                string.IsNullOrWhiteSpace(instruction)
                    ? "変更内容（プロンプト）を入力してください。"
                    : seedError.Length > 0
                        ? seedError
                        : "変更内容と補足はそれぞれ2000文字相当までです。");
            return false;
        }

        _i2iV2RequestPending = true;
        UpdateI2iV2BoardPresentation("AI編集の追加準備をしています…");
        try
        {
            I2iV2CapabilityState? validatedCapability = null;
            string? ValidateI2iV2Health(JsonElement healthPayload)
            {
                if (!TryParseI2iV2Capability(
                        healthPayload,
                        out I2iV2CapabilityState capability)
                    || !capability.IsReadyFor(target))
                {
                    _i2iV2Capability = null;
                    _i2iV2CapabilityUnknown = false;
                    return "The Aibos Image local AI service is not ready for the selected AI edit. No job was added.";
                }
                validatedCapability = capability;
                _i2iV2Capability = capability;
                _i2iV2CapabilityUnknown = false;
                return null;
            }

            if (!RevalidateI2iEditSource(source, out tile, out sourceError))
            {
                UpdateI2iV2BoardPresentation(sourceError);
                return false;
            }

            var requestBody = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = source.SourcePath,
                ["operation"] = I2iV2Operation,
                ["presetId"] = I2iV2PresetId,
                ["adapterId"] = I2iV2AdapterId,
                ["target"] = target,
                ["instruction"] = instruction,
            };
            if (details.Length > 0)
                requestBody["details"] = details;
            if (!string.IsNullOrWhiteSpace(source.SourceProducerJobId))
                requestBody["sourceProducerJobId"] = source.SourceProducerJobId;
            if (seed.HasValue)
                requestBody["seed"] = seed.Value;

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                requestBody,
                healthValidator: ValidateI2iV2Health,
                recoverySourceIdentity: source.SourcePath,
                prePublishValidator: prePublishValidator);
            if (response.SavedForDelivery)
            {
                _i2iV2RequestPending = false;
                CloseI2iV2EditBoard(restoreFocus: false);
                SetTransientStatusToast(
                    $"{tile.FileName}: {I2iV2TargetLabel(target)}のAI編集予約を保存しました。Jobsへの登録を継続しています。");
                return true;
            }
            if (!response.Ok
                || validatedCapability is not I2iV2CapabilityState capability
                || response.Payload is not JsonElement payload
                || !TryGetUniqueI2iProperty(payload, "job", out JsonElement job)
                || !IsI2iV2JobForExpectedSource(
                    job,
                    source,
                    target,
                    instruction,
                    details,
                    seed,
                    capability))
            {
                UpdateI2iV2BoardPresentation(
                    response.Ok
                        ? "AI編集ジョブの保存結果を安全に確認できません。Jobsで状態を確認してください。"
                        : response.Error);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(source.SourceProducerJobId))
                _activeI2iSourceProducerJobIds.Add(source.SourceProducerJobId);
            ApplyActiveEnhancementQueueJobToVisibleCatalog(job, tile);
            if (Modal.Visibility == Visibility.Visible
                && ParseModalEnhancementJob(job) is ModalEnhancementJobSnapshot snapshot)
            {
                ApplyModalEnhancementJob(tile, snapshot);
            }
            QueueEnhancedStateRefreshIfChanged();
            _i2iV2RequestPending = false;
            CloseI2iV2EditBoard(restoreFocus: false);
            SetTransientStatusToast(
                $"{tile.FileName}: {I2iV2TargetLabel(target)}のAI編集を共有GPUキューに追加しました。");
            return true;
        }
        finally
        {
            _i2iV2RequestPending = false;
            if (I2iV2EditDialog?.Visibility == Visibility.Visible)
                UpdateI2iV2BoardPresentation();
        }
    }

    private bool IsI2iV2JobForExpectedSource(
        JsonElement job,
        I2iEditSource expected,
        string expectedTarget,
        string expectedInstruction,
        string expectedDetails,
        int? expectedSeed,
        I2iV2CapabilityState capability)
    {
        if (!TryReadI2iV2JobInfo(job, out I2iV2JobInfo info)
            || !TryGetUniqueI2iString(job, "status", out string? status)
            || status is not ("queued" or "running")
            || !TryGetUniqueI2iString(job, "sourceId", out string? sourceId)
            || !TryGetUniqueI2iString(job, "sourcePath", out string? sourcePath)
            || !TryResolveEnhancementSourceIdentity(sourceId, out string resolvedSourceId)
            || !TryResolveEnhancementSourceIdentity(sourcePath, out string resolvedSourcePath)
            || !EnhancementSourceIdentityComparer.Equals(
                resolvedSourceId,
                expected.SourcePath)
            || !EnhancementSourceIdentityComparer.Equals(
                resolvedSourcePath,
                expected.SourcePath)
            || !TryReadOptionalI2iSourceProducerJobId(
                job,
                out string? sourceProducerJobId))
        {
            return false;
        }

        return string.Equals(info.Target, expectedTarget, StringComparison.Ordinal)
            && string.Equals(info.Instruction, expectedInstruction, StringComparison.Ordinal)
            && string.Equals(info.Details, expectedDetails, StringComparison.Ordinal)
            && (!expectedSeed.HasValue || info.Seed == expectedSeed.Value)
            && string.Equals(info.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            && string.Equals(
                info.PromptPolicyRevision,
                capability.PromptPolicyRevision,
                StringComparison.Ordinal)
            && string.Equals(
                info.PromptPolicySha256,
                capability.PromptPolicySha256,
                StringComparison.Ordinal)
            && string.Equals(
                sourceProducerJobId,
                expected.SourceProducerJobId,
                StringComparison.Ordinal);
    }

    private static string I2iV2TargetLabel(string? target)
        => target switch
        {
            "outfit" => "服装",
            "expression" => "表情",
            "background" => "場所・背景",
            "pose" => "ポーズ（実験的）",
            _ => "未対応ターゲット",
        };

    private void CloseI2iV2Edit_Click(object sender, RoutedEventArgs e)
        => CloseI2iV2EditBoard(restoreFocus: true);

    private void I2iV2EditBackdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, I2iV2EditDialog))
            CloseI2iV2EditBoard(restoreFocus: true);
    }

    private void CloseI2iV2EditBoard(bool restoreFocus)
    {
        if (I2iV2EditDialog is null || _i2iV2RequestPending)
            return;
        IInputElement? focus = _i2iV2FocusBeforeBoard;
        _i2iV2FocusBeforeBoard = null;
        _i2iV2EditSource = null;
        _i2iV2Capability = null;
        _i2iV2CapabilityUnknown = false;
        _i2iV2CapabilityCheckPending = false;
        _i2iV2BoardGeneration++;
        I2iV2EditDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus && focus is not null)
            _ = Dispatcher.BeginInvoke(
                () => Keyboard.Focus(focus),
                DispatcherPriority.Input);
    }

    public static bool IsI2iV2MutationSafeForSmoke(JsonElement job)
        => IsI2iV2MutationSafe(job);

    public Task<bool> OpenModalI2iV2EditBoardForSmokeAsync()
        => OpenI2iV2EditBoardAsync();

    public bool I2iV2EditBoardVisibleForSmoke =>
        I2iV2EditDialog.Visibility == Visibility.Visible;

    public static bool IsManagedI2iVersionCandidateForSmoke(JsonElement job)
        => IsI2iMutationSafe(job) || IsI2iV2MutationSafe(job);

    public static bool TryReadI2iV2JobInfoForSmoke(
        JsonElement job,
        out int schemaVersion,
        out string target,
        out string instructionSummary)
    {
        schemaVersion = 0;
        target = "";
        instructionSummary = "";
        if (!TryReadI2iV2JobInfo(job, out I2iV2JobInfo info))
            return false;
        schemaVersion = info.SchemaVersion;
        target = info.Target;
        instructionSummary = info.InstructionSummary;
        return true;
    }

    public static string ComputeI2iV2PresetHashForSmoke(JsonElement preset)
        => ComputeI2iV2PresetHash(preset);

    public static bool TryParseI2iV2CapabilityForSmoke(
        JsonElement health,
        string target,
        out bool ready,
        out string issueCode)
    {
        ready = false;
        issueCode = "WORKFLOW_UNVERIFIED";
        if (!TryParseI2iV2Capability(
                health,
                out I2iV2CapabilityState capability))
        {
            return false;
        }
        ready = capability.IsReadyFor(target);
        issueCode = capability.IssueCode;
        return true;
    }
}

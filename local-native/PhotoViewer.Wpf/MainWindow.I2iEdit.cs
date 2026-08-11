using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string I2iOperation = "i2i";
    private const string I2iTarget = "hair-color";
    private const string I2iContractId = "PV-ENHANCE-I2I-001";
    private const string I2iPresetId = "flux2-i2i-hair-v1";
    private const string I2iAdapterId = "comfyui-flux2-i2i";
    private const string I2iWorkflowRevision = "i2i-flux2-klein9b-sam31-v1";
    private const string I2iMaskRevision = "sam31-hair-mediapipe-face-v1";
    private const int I2iHairColorMaximumCharacters = 160;
    private const int I2iDetailsMaximumCharacters = 240;

    private bool _i2iCapabilityReady;
    private bool _i2iCapabilityUnknown;
    private bool _i2iCapabilityCheckPending;
    private bool _i2iRequestPending;
    private bool _syncingI2iSeedControls;
    private long _i2iBoardGeneration;
    private IInputElement? _i2iFocusBeforeBoard;
    private I2iEditSource? _i2iEditSource;

    private sealed record I2iEditSource(
        string SourcePath,
        string? SourceProducerJobId,
        string Label,
        long ModalGeneration);

    private static bool IsI2iMutationSafe(JsonElement job)
    {
        if (job.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(job, "operation", out JsonElement operation)
            || operation.ValueKind != JsonValueKind.String
            || !string.Equals(operation.GetString(), I2iOperation, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "mediaKind", out string? mediaKind)
            || !string.Equals(mediaKind, "image", StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "adapterId", out string? adapterId)
            || !string.Equals(adapterId, I2iAdapterId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "presetId", out string? presetId)
            || !string.Equals(presetId, I2iPresetId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(job, "sourceId", out _)
            || !TryGetUniqueI2iString(job, "sourcePath", out _)
            || !TryGetUniqueI2iProperty(
                job,
                "sourceSignature",
                out JsonElement sourceSignature)
            || sourceSignature.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iDouble(
                sourceSignature,
                "size",
                out double sourceSize)
            || sourceSize < 0d
            || !TryGetUniqueI2iDouble(
                sourceSignature,
                "mtimeMs",
                out _)
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
            || !HasExactI2iPresetEnvelope(job, preset))
        {
            return false;
        }

        return TryGetUniqueI2iInt32(options, "i2iSchemaVersion", out int schemaVersion)
            && schemaVersion == 1
            && TryGetUniqueI2iString(options, "target", out string? target)
            && string.Equals(target, I2iTarget, StringComparison.Ordinal)
            && TryGetUniqueI2iString(options, "hairColor", out string? hairColor)
            && IsNormalizedI2iText(
                hairColor,
                I2iHairColorMaximumCharacters,
                allowEmpty: false)
            && TryGetUniqueI2iStringAllowEmpty(options, "details", out string? details)
            && IsNormalizedI2iText(
                details,
                I2iDetailsMaximumCharacters,
                allowEmpty: true)
            && TryGetUniqueI2iBoolean(options, "preserveIdentity", out bool preserveIdentity)
            && preserveIdentity
            && TryGetUniqueI2iString(options, "prompt", out string? prompt)
            && string.Equals(
                prompt,
                BuildExpectedI2iPrompt(hairColor!, details!),
                StringComparison.Ordinal)
            && TryGetUniqueI2iStringAllowEmpty(
                options,
                "negativePrompt",
                out string? negativePrompt)
            && string.Equals(negativePrompt, "", StringComparison.Ordinal)
            && TryGetUniqueI2iInt32(options, "steps", out int steps)
            && steps == 8
            && TryGetUniqueI2iDouble(options, "cfgScale", out double cfgScale)
            && cfgScale == 1d
            && TryGetUniqueI2iInt32(options, "maxDimension", out int maxDimension)
            && maxDimension == 1280
            && TryGetUniqueI2iInt32(options, "seed", out int seed)
            && seed >= 0
            && TryGetUniqueI2iString(
                options,
                "workflowRevision",
                out string? workflowRevision)
            && string.Equals(
                workflowRevision,
                I2iWorkflowRevision,
                StringComparison.Ordinal)
            && TryGetUniqueI2iString(
                options,
                "maskRevision",
                out string? maskRevision)
            && string.Equals(maskRevision, I2iMaskRevision, StringComparison.Ordinal)
            && TryGetUniqueI2iBoolean(options, "loraEnabled", out bool loraEnabled)
            && !loraEnabled;
    }

    private static string BuildExpectedI2iPrompt(
        string hairColor,
        string details)
    {
        string requested = details.Length > 0
            ? $"Change only the hair color to {hairColor}. Hair-color details: {details}."
            : $"Change only the hair color to {hairColor}.";
        return string.Join(
            " ",
            requested,
            "Retain the same recognizable person, face geometry and proportions, expression, gaze, age, skin appearance, and every unrequested facial detail.",
            "Retain the existing hairstyle, hair length and shape, pose, body proportions, hands, camera, crop, background, lighting, visual medium, and every other unrequested detail.");
    }

    private static bool HasExactI2iPresetEnvelope(
        JsonElement job,
        JsonElement preset)
    {
        if (!TryGetUniqueI2iString(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !TryGetUniqueI2iString(preset, "id", out string? presetId)
            || !string.Equals(presetId, I2iPresetId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(preset, "label", out string? label)
            || !string.Equals(
                label,
                "FLUX.2 hair-color edit v1",
                StringComparison.Ordinal)
            || !TryGetUniqueI2iString(
                preset,
                "modelFamily",
                out string? modelFamily)
            || !string.Equals(modelFamily, "photo", StringComparison.Ordinal)
            || !TryGetUniqueI2iString(
                preset,
                "modelName",
                out string? modelName)
            || !string.Equals(
                modelName,
                "flux-2-klein-9b-Q4_K_M.gguf",
                StringComparison.Ordinal)
            || !TryGetUniqueI2iDouble(preset, "scale", out double scale)
            || scale != 1d
            || !TryGetUniqueI2iString(
                preset,
                "outputFormat",
                out string? outputFormat)
            || !string.Equals(outputFormat, "png", StringComparison.Ordinal)
            || !TryGetUniqueI2iDouble(preset, "denoise", out double denoise)
            || denoise != 0d
            || !TryGetUniqueI2iDouble(preset, "sharpen", out double sharpen)
            || sharpen != 0d
            || !TryGetUniqueI2iDouble(preset, "detail", out double detail)
            || detail != 0d
            || !TryGetUniqueI2iDouble(
                preset,
                "smoothness",
                out double smoothness)
            || smoothness != 0d
            || !TryGetUniqueI2iDouble(
                preset,
                "colorBrightness",
                out double colorBrightness)
            || colorBrightness != 0d
            || !TryGetUniqueI2iDouble(
                preset,
                "colorContrast",
                out double colorContrast)
            || colorContrast != 0d
            || !TryGetUniqueI2iDouble(
                preset,
                "colorSaturation",
                out double colorSaturation)
            || colorSaturation != 0d)
        {
            return false;
        }

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
            if (!TryGetUniqueI2iProperty(
                    preset,
                    propertyName,
                    out JsonElement value))
            {
                return false;
            }
            if (index > 0)
                builder.Append(',');
            builder.Append(
                JsonSerializer.Serialize(
                    propertyName,
                    VideoStableJsonOptions));
            builder.Append(':');
            AppendI2iJsonInDocumentOrder(builder, value);
        }
        builder.Append('}');
        string expectedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant()[..12];
        return string.Equals(presetHash, expectedHash, StringComparison.Ordinal);
    }

    private static void AppendI2iJsonInDocumentOrder(
        StringBuilder builder,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool firstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append(
                        JsonSerializer.Serialize(
                            property.Name,
                            VideoStableJsonOptions));
                    builder.Append(':');
                    AppendI2iJsonInDocumentOrder(builder, property.Value);
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
                    AppendI2iJsonInDocumentOrder(builder, item);
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
                    "Unsupported JSON value in I2I preset snapshot.");
        }
    }

    private static bool TryGetUniqueI2iProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        bool found = false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                continue;
            if (found)
                return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static bool TryGetUniqueI2iString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        return TryGetUniqueI2iProperty(element, propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString());
    }

    private static bool TryGetUniqueI2iStringAllowEmpty(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        return TryGetUniqueI2iProperty(element, propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }

    private static bool TryGetUniqueI2iInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        return TryGetUniqueI2iProperty(element, propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryGetUniqueI2iDouble(
        JsonElement element,
        string propertyName,
        out double value)
    {
        value = 0;
        return TryGetUniqueI2iProperty(element, propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static bool TryGetUniqueI2iBoolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!TryGetUniqueI2iProperty(element, propertyName, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool IsNormalizedI2iText(
        string? value,
        int maximumCharacters,
        bool allowEmpty)
    {
        if (value is null
            || value.Length > maximumCharacters
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        return allowEmpty || value.Length > 0;
    }

    private static bool TryReadOptionalI2iSourceProducerJobId(
        JsonElement job,
        out string? sourceProducerJobId)
    {
        sourceProducerJobId = null;
        int matches = 0;
        JsonElement value = default;
        foreach (JsonProperty property in job.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    "sourceProducerJobId",
                    StringComparison.Ordinal))
            {
                continue;
            }
            matches++;
            value = property.Value;
        }
        if (matches == 0)
            return true;
        if (matches != 1 || value.ValueKind != JsonValueKind.String)
            return false;
        sourceProducerJobId = value.GetString();
        return !string.IsNullOrWhiteSpace(sourceProducerJobId)
            && sourceProducerJobId.Length <= 128;
    }

    private bool TryBuildManagedI2iVersion(
        JsonElement job,
        IReadOnlyDictionary<string, ManagedPhotorealVideoSource> photorealSources,
        out string resolvedSource,
        out ManagedEnhancedOutput managedOutput,
        out IReadOnlyList<string> catalogAliases)
    {
        resolvedSource = "";
        managedOutput = null!;
        catalogAliases = [];
        if (!(IsI2iMutationSafe(job) || IsI2iV2MutationSafe(job))
            || !TryBuildManagedEnhancedOutput(
                job,
                out resolvedSource,
                out managedOutput,
                out catalogAliases)
            || !IsManagedI2iOutputPath(managedOutput.OutputPath)
            || !TryReadOptionalI2iSourceProducerJobId(
                job,
                out string? sourceProducerJobId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceProducerJobId))
            return true;

        if (!photorealSources.TryGetValue(
                sourceProducerJobId,
                out ManagedPhotorealVideoSource? producer)
            || !EnhancementSourceIdentityComparer.Equals(
                producer.ResolvedSource,
                resolvedSource)
            || producer.SourceSize != managedOutput.SourceSize
            || Math.Abs(producer.SourceMtimeMs - managedOutput.SourceMtimeMs) > 1
            || !TryResolveManagedEnhancementOutputPath(
                producer.OutputPath,
                out string currentProducerOutput)
            || !string.Equals(
                currentProducerOutput,
                producer.OutputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool IsManagedI2iOutputPath(string outputPath)
    {
        try
        {
            string managedRoot = Path.GetFullPath(
                _resolveFinalPath(Path.GetFullPath(
                    ResolvedManagedEnhancementOutputsRoot)));
            string canonicalOutput = Path.GetFullPath(_resolveFinalPath(outputPath));
            string relative = Path.GetRelativePath(managedRoot, canonicalOutput);
            string[] parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                && string.Equals(parts[0], "Edited", StringComparison.Ordinal)
                && !parts.Any(static part => part == "..");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseI2iCapability(
        JsonElement payload,
        out bool ready,
        out string issueCode)
    {
        ready = false;
        issueCode = "WORKFLOW_UNVERIFIED";
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(
                payload,
                "capabilities",
                out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(capabilities, "i2i", out JsonElement i2i)
            || i2i.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iString(i2i, "contractId", out string? contractId)
            || !string.Equals(contractId, I2iContractId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(i2i, "operation", out string? operation)
            || !string.Equals(operation, I2iOperation, StringComparison.Ordinal)
            || !TryGetUniqueI2iBoolean(i2i, "readerReady", out bool readerReady)
            || !TryGetUniqueI2iBoolean(i2i, "writerEnabled", out bool writerEnabled)
            || !TryGetUniqueI2iBoolean(
                i2i,
                "backendConfigured",
                out bool backendConfigured)
            || !TryGetUniqueI2iBoolean(i2i, "ready", out bool backendReady)
            || !TryGetUniqueI2iProperty(
                i2i,
                "supportedTargets",
                out JsonElement supportedTargets)
            || supportedTargets.ValueKind != JsonValueKind.Array
            || !TryGetUniqueI2iString(i2i, "backendId", out string? backendId)
            || !string.Equals(backendId, I2iAdapterId, StringComparison.Ordinal)
            || !TryGetUniqueI2iString(
                i2i,
                "workflowRevision",
                out string? workflowRevision)
            || !string.Equals(
                workflowRevision,
                I2iWorkflowRevision,
                StringComparison.Ordinal)
            || !TryGetUniqueI2iString(
                i2i,
                "maskRevision",
                out string? maskRevision)
            || !string.Equals(maskRevision, I2iMaskRevision, StringComparison.Ordinal))
        {
            return false;
        }

        bool supportsHairColor = false;
        foreach (JsonElement target in supportedTargets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.String)
                return false;
            supportsHairColor |= string.Equals(
                target.GetString(),
                I2iTarget,
                StringComparison.Ordinal);
        }

        if (TryGetUniqueI2iProperty(i2i, "issueCode", out JsonElement issue))
        {
            if (issue.ValueKind == JsonValueKind.String)
                issueCode = issue.GetString() ?? issueCode;
            else if (issue.ValueKind != JsonValueKind.Null)
                return false;
        }

        ready = readerReady
            && writerEnabled
            && backendConfigured
            && backendReady
            && supportsHairColor;
        return true;
    }

    private bool TryResolveCurrentModalI2iEditSource(
        out I2iEditSource source,
        out string error)
    {
        source = null!;
        error = "";
        if (Modal.Visibility != Visibility.Visible)
        {
            error = "拡大表示の画像を確認できません。";
            return false;
        }
        if (SelectedTile() is not Tile { IsRealFile: true } tile)
        {
            error = "拡大表示中のOriginal画像を確認できません。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_modalSourceTilePath)
            || !EnhancementSourceIdentityComparer.Equals(
                _modalSourceTilePath,
                tile.Path))
        {
            error = "拡大表示の画像が変わりました。表示を確認してから開き直してください。";
            return false;
        }
        if (!File.Exists(tile.Path)
            || !TryResolveEnhancementSourceIdentity(tile.Path, out string sourceIdentity)
            || !File.Exists(sourceIdentity))
        {
            error = "AI編集できるOriginal画像を確認できません。";
            return false;
        }

        string? sourceProducerJobId = null;
        string sourceLabel = "Original";
        if (_modalShowingVideo)
        {
            error = "動画はAI編集の入力にできません。Originalか実写化版を表示してください。";
            return false;
        }
        if (_modalShowingEnhanced)
        {
            if (!CurrentModalEnhancementVersionIsPhotoreal()
                || !TryGetExactDurableCurrentModalEnhancementVersion(
                    tile,
                    out ManagedEnhancementVersion photoreal)
                || photoreal.Recovered
                || string.IsNullOrWhiteSpace(photoreal.JobId))
            {
                error = "AI編集は表示中のOriginalか、正確に確認できる実写化版から開始できます。";
                return false;
            }
            sourceProducerJobId = photoreal.JobId;
            sourceLabel = CurrentModalEnhancementVersionLabel();
        }

        source = new I2iEditSource(
            sourceIdentity,
            sourceProducerJobId,
            sourceLabel,
            _modalEnhancementGeneration);
        return true;
    }

    private bool RevalidateI2iEditSource(
        I2iEditSource expected,
        out Tile tile,
        out string error)
    {
        tile = null!;
        error = "AI編集の入力が変わりました。ボードを開き直してください。";
        if (SelectedTile() is not Tile { IsRealFile: true } selected)
            return false;
        if (!File.Exists(selected.Path)
            || !TryResolveEnhancementSourceIdentity(selected.Path, out string sourceIdentity)
            || !EnhancementSourceIdentityComparer.Equals(
                sourceIdentity,
                expected.SourcePath))
        {
            return false;
        }

        if (Modal.Visibility != Visibility.Visible
            || expected.ModalGeneration != _modalEnhancementGeneration
            || _modalShowingVideo
            || string.IsNullOrWhiteSpace(_modalSourceTilePath)
            || !EnhancementSourceIdentityComparer.Equals(
                _modalSourceTilePath,
                selected.Path))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expected.SourceProducerJobId))
        {
            if (_modalShowingEnhanced)
                return false;
        }
        else if (!CurrentModalEnhancementVersionIsPhotoreal()
            || !TryGetExactDurableCurrentModalEnhancementVersion(
                selected,
                out ManagedEnhancementVersion photoreal)
            || !string.Equals(
                photoreal.JobId,
                expected.SourceProducerJobId,
                StringComparison.Ordinal))
        {
            return false;
        }

        tile = selected;
        return true;
    }

    private async void OpenModalI2iEdit_Click(
        object sender,
        RoutedEventArgs e)
        => await OpenI2iEditBoardAsync();

    private async Task<bool> OpenI2iEditBoardAsync()
    {
        if (!TryResolveCurrentModalI2iEditSource(
                out I2iEditSource source,
                out string error))
        {
            SetStatusToast(error);
            return false;
        }

        _i2iFocusBeforeBoard = Keyboard.FocusedElement;
        _i2iEditSource = source;
        _i2iCapabilityReady = false;
        _i2iCapabilityUnknown = false;
        _i2iCapabilityCheckPending = true;
        _i2iRequestPending = false;
        long generation = ++_i2iBoardGeneration;

        I2iHairColorTextBox.Text = "";
        I2iDetailsTextBox.Text = "";
        _syncingI2iSeedControls = true;
        SelectSeedMode(I2iSeedModeComboBox, fixedMode: false);
        I2iSeedValueTextBox.Text = DefaultFixedSeedValue.ToString(
            CultureInfo.InvariantCulture);
        I2iSeedValueTextBox.IsEnabled = false;
        _syncingI2iSeedControls = false;
        I2iSourceText.Text = source.Label;
        I2iEditDialog.Visibility = Visibility.Visible;
        UpdateI2iEditBoardPresentation("バックエンドの準備状態を確認しています…");
        _ = Dispatcher.BeginInvoke(
            I2iHairColorTextBox.Focus,
            DispatcherPriority.Input);

        await RefreshI2iCapabilityForBoardAsync(generation);
        return I2iEditDialog.Visibility == Visibility.Visible;
    }

    private async Task RefreshI2iCapabilityForBoardAsync(long generation)
    {
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            timeoutMilliseconds: DurableEnqueueActionDeadlineMilliseconds);
        if (generation != _i2iBoardGeneration
            || I2iEditDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        _i2iCapabilityCheckPending = false;
        EnhancementEnqueueBackendMode mode = EnhancementEnqueueProbePolicy.Classify(
            response.Ok,
            response.StatusCode,
            response.Payload);
        if (mode == EnhancementEnqueueBackendMode.Unknown)
        {
            _i2iCapabilityReady = false;
            _i2iCapabilityUnknown = true;
            UpdateI2iEditBoardPresentation(
                "バックエンドのAI編集対応を確認できません。ジョブは追加されません。");
            return;
        }

        _i2iCapabilityUnknown = false;
        if (!response.Ok
            || response.Payload is not JsonElement payload
            || !TryParseI2iCapability(
                payload,
                out bool ready,
                out string issueCode))
        {
            _i2iCapabilityReady = false;
            UpdateI2iEditBoardPresentation(
                "AI編集の対応状態を確認できません。Jobsと既存画像は引き続き利用できます。");
            return;
        }

        _i2iCapabilityReady = ready;
        UpdateI2iEditBoardPresentation(
            ready
                ? "髪色を入力して、明示的にキューへ追加してください。"
                : $"AI編集バックエンドは準備中です（{issueCode}）。ジョブは追加されません。");
    }

    private void I2iEditField_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateI2iEditBoardPresentation();

    private void I2iSeedMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingI2iSeedControls || I2iSeedValueTextBox is null)
            return;
        I2iSeedValueTextBox.IsEnabled = SelectedSeedModeIsFixed(
            I2iSeedModeComboBox);
        UpdateI2iEditBoardPresentation();
    }

    private bool TryResolveI2iSeed(out int? seed, out string error)
    {
        seed = null;
        error = "";
        if (!SelectedSeedModeIsFixed(I2iSeedModeComboBox))
            return true;
        if (TryParseFixedSeed(I2iSeedValueTextBox.Text.Trim(), out int fixedSeed)
            && fixedSeed >= 0)
        {
            seed = fixedSeed;
            return true;
        }
        error = "Fixed seed は 0〜2147483647 の整数で入力してください。";
        return false;
    }

    private void UpdateI2iEditBoardPresentation(string? statusOverride = null)
    {
        if (I2iEditDialog is null)
            return;

        string hairColor = I2iHairColorTextBox?.Text.Trim() ?? "";
        string details = I2iDetailsTextBox?.Text.Trim() ?? "";
        bool fieldsValid = hairColor.Length is > 0 and <= I2iHairColorMaximumCharacters
            && details.Length <= I2iDetailsMaximumCharacters;
        bool seedValid = TryResolveI2iSeed(out _, out string seedError);
        string sourceError = "AI編集の入力を確認できません。ボードを開き直してください。";
        bool sourceValid = _i2iEditSource is not null
            && RevalidateI2iEditSource(_i2iEditSource, out _, out sourceError);
        I2iSeedStatusText.Text = seedValid ? "" : seedError;
        I2iSeedStatusText.Visibility = seedValid
            ? Visibility.Collapsed
            : Visibility.Visible;

        string status = statusOverride
            ?? (_i2iCapabilityCheckPending
                ? "バックエンドの準備状態を確認しています…"
                : _i2iCapabilityUnknown
                    ? "バックエンドのAI編集対応を確認できません。ジョブは追加されません。"
                    : !_i2iCapabilityReady
                        ? "AI編集バックエンドがreadyになるまでジョブは追加できません。"
                        : !sourceValid
                            ? sourceError
                            : !fieldsValid
                                ? "髪色は必須です。補足は240文字まで入力できます。"
                                : "髪色だけを編集し、顔と髪以外の領域を自動保持します。");
        I2iEditStatusText.Text = status;
        I2iQueueButton.IsEnabled = _i2iCapabilityReady
            && !_i2iCapabilityCheckPending
            && !_i2iRequestPending
            && sourceValid
            && fieldsValid
            && seedValid;
        I2iCloseButton.IsEnabled = !_i2iRequestPending;
    }

    private async void QueueI2iEdit_Click(object sender, RoutedEventArgs e)
        => await QueueI2iEditAsync();

    private async Task<bool> QueueI2iEditAsync()
    {
        if (_i2iRequestPending)
            return false;
        if (_i2iEditSource is not I2iEditSource source)
        {
            UpdateI2iEditBoardPresentation(
                "AI編集の入力を確認できません。ボードを開き直してください。");
            return false;
        }
        if (!RevalidateI2iEditSource(
                source,
                out Tile tile,
                out string sourceError))
        {
            UpdateI2iEditBoardPresentation(sourceError);
            return false;
        }
        Func<string?>? prePublishValidator =
            CaptureExternalFileDropPrePublishValidator(tile);
        if (!_i2iCapabilityReady || _i2iCapabilityCheckPending)
        {
            UpdateI2iEditBoardPresentation(
                "バックエンドのAI編集対応を確認できません。ジョブは追加されません。");
            return false;
        }

        string hairColor = I2iHairColorTextBox.Text.Trim();
        string details = I2iDetailsTextBox.Text.Trim();
        bool seedValid = TryResolveI2iSeed(
            out int? seed,
            out string seedError);
        if (hairColor.Length == 0
            || hairColor.Length > I2iHairColorMaximumCharacters
            || details.Length > I2iDetailsMaximumCharacters
            || !seedValid)
        {
            UpdateI2iEditBoardPresentation(
                hairColor.Length == 0
                    ? "髪色を入力してください。"
                    : hairColor.Length > I2iHairColorMaximumCharacters
                        ? "髪色は160文字以内で入力してください。"
                        : details.Length > I2iDetailsMaximumCharacters
                            ? "補足は240文字以内で入力してください。"
                            : seedError);
            return false;
        }

        _i2iRequestPending = true;
        UpdateI2iEditBoardPresentation("AI編集の追加準備をしています…");
        try
        {
            string? ValidateI2iHealth(JsonElement healthPayload)
            {
                string issueCode = "WORKFLOW_UNVERIFIED";
                bool supported = TryParseI2iCapability(
                        healthPayload,
                        out bool ready,
                        out issueCode)
                    && ready;
                _i2iCapabilityReady = supported;
                _i2iCapabilityUnknown = false;
                return supported
                    ? null
                    : $"The running H25 companion is not ready for AI editing ({issueCode}). No job was added.";
            }

            if (!RevalidateI2iEditSource(source, out tile, out sourceError))
            {
                UpdateI2iEditBoardPresentation(sourceError);
                return false;
            }

            var requestBody = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = source.SourcePath,
                ["operation"] = I2iOperation,
                ["presetId"] = I2iPresetId,
                ["adapterId"] = I2iAdapterId,
                ["target"] = I2iTarget,
                ["hairColor"] = hairColor,
                ["details"] = details,
            };
            if (!string.IsNullOrWhiteSpace(source.SourceProducerJobId))
                requestBody["sourceProducerJobId"] = source.SourceProducerJobId;
            if (seed.HasValue)
                requestBody["seed"] = seed.Value;

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                requestBody,
                healthValidator: ValidateI2iHealth,
                recoverySourceIdentity: source.SourcePath,
                prePublishValidator: prePublishValidator);
            if (response.SavedForDelivery)
            {
                _i2iRequestPending = false;
                CloseI2iEditBoard(restoreFocus: false);
                SetTransientStatusToast(
                    $"{tile.FileName}: AI編集の予約を保存しました。Jobsへの登録を継続しています。");
                return true;
            }
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || !payload.TryGetProperty("job", out JsonElement job)
                || !IsI2iJobForExpectedSource(job, source))
            {
                UpdateI2iEditBoardPresentation(
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
            _i2iRequestPending = false;
            CloseI2iEditBoard(restoreFocus: false);
            SetTransientStatusToast(
                $"{tile.FileName}: AI編集を共有GPUキューに追加しました。");
            return true;
        }
        finally
        {
            _i2iRequestPending = false;
            if (I2iEditDialog?.Visibility == Visibility.Visible)
                UpdateI2iEditBoardPresentation();
        }
    }

    private bool IsI2iJobForExpectedSource(
        JsonElement job,
        I2iEditSource expected)
    {
        if (!IsI2iMutationSafe(job)
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

        return string.Equals(
            sourceProducerJobId,
            expected.SourceProducerJobId,
            StringComparison.Ordinal);
    }

    private void CloseI2iEdit_Click(object sender, RoutedEventArgs e)
        => CloseI2iEditBoard(restoreFocus: true);

    private void I2iEditBackdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, I2iEditDialog))
            CloseI2iEditBoard(restoreFocus: true);
    }

    private void CloseI2iEditBoard(bool restoreFocus)
    {
        if (I2iEditDialog is null || _i2iRequestPending)
            return;
        IInputElement? focus = _i2iFocusBeforeBoard;
        _i2iFocusBeforeBoard = null;
        _i2iEditSource = null;
        _i2iCapabilityReady = false;
        _i2iCapabilityUnknown = false;
        _i2iCapabilityCheckPending = false;
        _i2iBoardGeneration++;
        I2iEditDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus && focus is not null)
            _ = Dispatcher.BeginInvoke(
                () => Keyboard.Focus(focus),
                DispatcherPriority.Input);
    }

    private void UpdateModalI2iEditActionAvailability()
    {
        if (ModalI2iEditButton is null || ModalContextI2iEdit is null)
            return;
        bool available = TryResolveCurrentModalI2iEditSource(
            out _,
            out string error);
        ModalI2iEditButton.IsEnabled = available && !_modalEnhancementRequestPending;
        ModalI2iEditButton.ToolTip = available
            ? "表示中のOriginalまたは実写化版の髪色をAI編集"
            : error;
        ModalContextI2iEdit.IsEnabled = ModalI2iEditButton.IsEnabled;
        ModalContextI2iEdit.ToolTip = ModalI2iEditButton.ToolTip;
        if (ModalI2iV2EditButton is not null && ModalContextI2iV2Edit is not null)
        {
            ModalI2iV2EditButton.IsEnabled = ModalI2iEditButton.IsEnabled;
            ModalI2iV2EditButton.ToolTip = available
                ? "表示中のOriginalまたは実写化版の服装・表情・背景・ポーズをAI編集"
                : error;
            ModalContextI2iV2Edit.IsEnabled = ModalI2iV2EditButton.IsEnabled;
            ModalContextI2iV2Edit.ToolTip = ModalI2iV2EditButton.ToolTip;
        }
    }

    public static bool IsI2iMutationSafeForSmoke(JsonElement job)
        => IsI2iMutationSafe(job);

    public static bool IsI2iCapabilityReadyForSmoke(JsonElement health)
        => TryParseI2iCapability(health, out bool ready, out _) && ready;

    public static bool TryParseI2iCapabilityForSmoke(
        JsonElement health,
        out bool ready,
        out string issueCode)
        => TryParseI2iCapability(health, out ready, out issueCode);

    public static string ReadEnhancementOperationForI2iSmoke(JsonElement job)
        => ReadEnhancementOperation(job);

    public async Task<bool> OpenModalI2iEditBoardForSmokeAsync()
        => await OpenI2iEditBoardAsync();

    public void ConfigureI2iEditForSmoke(
        string hairColor,
        string details,
        bool fixedSeed,
        string seedValue)
    {
        I2iHairColorTextBox.Text = hairColor;
        I2iDetailsTextBox.Text = details;
        _syncingI2iSeedControls = true;
        SelectSeedMode(I2iSeedModeComboBox, fixedSeed);
        I2iSeedValueTextBox.Text = seedValue;
        I2iSeedValueTextBox.IsEnabled = fixedSeed;
        _syncingI2iSeedControls = false;
        UpdateI2iEditBoardPresentation();
    }

    public Task<bool> QueueI2iEditForSmokeAsync() => QueueI2iEditAsync();

    public bool I2iEditBoardVisibleForSmoke =>
        I2iEditDialog.Visibility == Visibility.Visible;

    public bool I2iEditQueueEnabledForSmoke => I2iQueueButton.IsEnabled;

    public string I2iEditSourceLabelForSmoke => I2iSourceText.Text;

    public string I2iEditStatusForSmoke => I2iEditStatusText.Text;

    public bool I2iEditResponsiveSurfaceForSmoke =>
        I2iEditBoardBorder.Width <= 440
        && I2iEditBoardBorder.MaxHeight <= 640
        && I2iEditScrollViewer.VerticalScrollBarVisibility
            == ScrollBarVisibility.Auto;
}

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
    private const string I2iV3ContractId = "PV-ENHANCE-I2I-003";
    private const string I2iV3PresetId = "flux2-i2i-edit-v3";
    private const string I2iV3AdapterId = "comfyui-flux2-i2i-v3";
    private const string I2iV3WorkflowRevision = "i2i-flux2-klein9b-unified-v3";
    private const string I2iV3MaskRevision = "sam31-mediapipe-multiregion-envelope-v3";
    private const int I2iV3MaximumDirectiveCodePoints = 2_000;
    private const int I2iV3MaximumPromptCodePoints = 8_000;
    private const int I2iV3MaximumStyleCount = 32;
    private const int I2iV3MaximumStyleNameLength = 48;
    private const int I2iV3AutomaticOutfitExpansion = 64;
    private static readonly string[] I2iV3DirectiveKeys =
        ["overall", "expression", "outfit", "background", "pose"];

    private bool _i2iV3CapabilityCheckPending;
    private bool _i2iV3CapabilityUnknown;
    private bool _i2iV3RequestPending;
    private bool _syncingI2iV3Controls;
    private long _i2iV3BoardGeneration;
    private IInputElement? _i2iV3FocusBeforeBoard;
    private I2iEditSource? _i2iV3EditSource;
    private I2iV3CapabilityState? _i2iV3Capability;
    private readonly List<I2iEditStyleState> _i2iV3Styles = [];
    private string? _selectedI2iV3StyleName;

    private sealed record I2iV3StyleChoice(string Label, string? StyleName);

    private sealed record I2iV3CapabilityState(
        bool ReaderReady,
        bool WriterEnabled,
        bool BackendConfigured,
        bool DeclaredReady,
        string AdapterId,
        string IssueCode)
    {
        public bool IsReady => ReaderReady
            && WriterEnabled
            && BackendConfigured
            && DeclaredReady;
    }

    private sealed record I2iV3JobInfo(
        I2iV3WorkspaceSnapshot Snapshot,
        string AdapterId);

    private static bool IsNormalizedI2iV3Text(
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

    private static bool TryParseI2iV3Capability(
        JsonElement payload,
        out I2iV3CapabilityState capability)
    {
        capability = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(payload, "capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(capabilities, "i2iV3", out JsonElement i2i)
            || i2i.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iString(i2i, "contractId", out string? contractId)
            || contractId != I2iV3ContractId
            || !TryGetUniqueI2iString(i2i, "operation", out string? operation)
            || operation != "i2i"
            || !TryGetUniqueI2iInt32(i2i, "schemaVersion", out int schemaVersion)
            || schemaVersion != 3
            || !TryGetUniqueI2iBoolean(i2i, "readerReady", out bool readerReady)
            || !TryGetUniqueI2iBoolean(i2i, "writerEnabled", out bool writerEnabled)
            || !TryGetUniqueI2iBoolean(i2i, "backendConfigured", out bool backendConfigured)
            || !TryGetUniqueI2iBoolean(i2i, "ready", out bool ready)
            || !TryGetUniqueI2iProperty(i2i, "directiveKeys", out JsonElement directiveKeys)
            || directiveKeys.ValueKind != JsonValueKind.Array
            || !TryGetUniqueI2iProperty(i2i, "steps", out JsonElement steps)
            || steps.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iInt32(steps, "min", out int minimumSteps)
            || minimumSteps != 4
            || !TryGetUniqueI2iInt32(steps, "max", out int maximumSteps)
            || maximumSteps != 20
            || !TryGetUniqueI2iInt32(steps, "default", out int defaultSteps)
            || defaultSteps != 8
            || !TryGetUniqueI2iProperty(i2i, "cfgScale", out JsonElement cfg)
            || cfg.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iDouble(cfg, "min", out double minimumCfg)
            || minimumCfg != 0.5d
            || !TryGetUniqueI2iDouble(cfg, "max", out double maximumCfg)
            || maximumCfg != 3d
            || !TryGetUniqueI2iDouble(cfg, "default", out double defaultCfg)
            || defaultCfg != 1d
            || !TryGetUniqueI2iProperty(i2i, "outfitMask", out JsonElement outfitMask)
            || outfitMask.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iProperty(outfitMask, "modes", out JsonElement modes)
            || modes.ValueKind != JsonValueKind.Array
            || !TryGetUniqueI2iProperty(outfitMask, "manualExpandPixels", out JsonElement manual)
            || manual.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iInt32(manual, "min", out int minimumExpansion)
            || minimumExpansion != 0
            || !TryGetUniqueI2iInt32(manual, "max", out int maximumExpansion)
            || maximumExpansion != 160
            || !TryGetUniqueI2iInt32(manual, "default", out int defaultExpansion)
            || defaultExpansion != 32
            || !TryGetUniqueI2iInt32(outfitMask, "automaticExpandPixels", out int automaticExpansion)
            || automaticExpansion != I2iV3AutomaticOutfitExpansion
            || !TryGetUniqueI2iString(i2i, "backendId", out string? backendId)
            || backendId != I2iV3AdapterId
            || !TryGetUniqueI2iString(i2i, "workflowRevision", out string? workflowRevision)
            || workflowRevision != I2iV3WorkflowRevision
            || !TryGetUniqueI2iString(i2i, "maskRevision", out string? maskRevision)
            || maskRevision != I2iV3MaskRevision
            || !TryGetUniqueI2iProperty(i2i, "issueCode", out JsonElement issue))
        {
            return false;
        }

        string[] parsedKeys = directiveKeys.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null)
            .Where(static item => item is not null)
            .Cast<string>()
            .ToArray();
        string[] parsedModes = modes.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null)
            .Where(static item => item is not null)
            .Cast<string>()
            .ToArray();
        if (parsedKeys.Length != directiveKeys.GetArrayLength()
            || !parsedKeys.SequenceEqual(I2iV3DirectiveKeys, StringComparer.Ordinal)
            || parsedModes.Length != modes.GetArrayLength()
            || !parsedModes.SequenceEqual(["auto", "manual"], StringComparer.Ordinal))
        {
            return false;
        }

        string issueCode;
        if (issue.ValueKind == JsonValueKind.Null)
        {
            issueCode = "";
        }
        else if (issue.ValueKind == JsonValueKind.String
            && IsNormalizedI2iV3Text(issue.GetString(), 128, allowEmpty: false))
        {
            issueCode = issue.GetString()!;
        }
        else
        {
            return false;
        }

        capability = new I2iV3CapabilityState(
            readerReady,
            writerEnabled,
            backendConfigured,
            ready,
            backendId!,
            issueCode);
        bool readinessConsistent = ready == (readerReady && writerEnabled && backendConfigured);
        bool issueConsistent = ready
            ? string.IsNullOrEmpty(issueCode)
            : !string.IsNullOrEmpty(issueCode);
        return readinessConsistent && issueConsistent;
    }

    private static string BuildI2iV3Prompt(I2iV3WorkspaceSnapshot snapshot)
    {
        var lines = new List<string>(6);
        if (snapshot.Overall.Length > 0)
            lines.Add($"Overall: {snapshot.Overall}");
        if (snapshot.Expression.Length > 0)
            lines.Add($"Expression: {snapshot.Expression}");
        if (snapshot.Outfit.Length > 0)
            lines.Add($"Outfit: {snapshot.Outfit}");
        if (snapshot.Background.Length > 0)
            lines.Add($"Background: {snapshot.Background}");
        if (snapshot.Pose.Length > 0)
            lines.Add($"Pose: {snapshot.Pose}");
        if (snapshot.Overall.Length == 0)
            lines.Add("Leave unlisted regions unchanged.");
        return string.Join('\n', lines);
    }

    private static string BuildI2iV3Summary(I2iV3WorkspaceSnapshot snapshot)
    {
        string[] labels =
        [
            snapshot.Overall.Length > 0 ? "全体" : "",
            snapshot.Expression.Length > 0 ? "表情" : "",
            snapshot.Outfit.Length > 0 ? "服装" : "",
            snapshot.Background.Length > 0 ? "背景" : "",
            snapshot.Pose.Length > 0 ? "ポーズ" : "",
        ];
        return string.Join("・", labels.Where(static label => label.Length > 0));
    }

    private static bool TryReadI2iV3JobInfo(
        JsonElement job,
        out I2iV3JobInfo info)
    {
        info = null!;
        if (job.ValueKind != JsonValueKind.Object
            || !HasSafeI2iV2MutableEnvelope(job)
            || !TryGetUniqueI2iString(job, "operation", out string? operation)
            || operation != "i2i"
            || !TryGetUniqueI2iString(job, "mediaKind", out string? mediaKind)
            || mediaKind != "image"
            || !TryGetUniqueI2iString(job, "adapterId", out string? adapterId)
            || adapterId != I2iV3AdapterId
            || !TryGetUniqueI2iString(job, "presetId", out string? presetId)
            || presetId != I2iV3PresetId
            || !TryGetUniqueI2iString(job, "sourceId", out _)
            || !TryGetUniqueI2iString(job, "sourcePath", out _)
            || !TryGetUniqueI2iProperty(job, "sourceSignature", out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iDouble(signature, "size", out double sourceSize)
            || sourceSize < 0d
            || !TryGetUniqueI2iDouble(signature, "mtimeMs", out _)
            || !TryReadOptionalI2iSourceProducerJobId(job, out _)
            || job.EnumerateObject().Any(static property => property.Name is
                "sourceOutputPath" or "sourceImagePath" or
                "sourceRecoveredOutputPath" or "sourceRecoveredAdapterId" or
                "sourceRecoveredSignature" or "sourceRecoveredSha256")
            || !TryGetUniqueI2iProperty(job, "preset", out JsonElement preset)
            || preset.ValueKind != JsonValueKind.Object
            || !HasExactI2iV3PresetEnvelope(job, preset)
            || !TryGetUniqueI2iProperty(preset, "options", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object
            || !TryGetUniqueI2iInt32(options, "i2iSchemaVersion", out int schemaVersion)
            || schemaVersion != 3
            || !TryGetUniqueI2iStringAllowEmpty(options, "overallInstruction", out string? overall)
            || !TryGetUniqueI2iStringAllowEmpty(options, "expressionInstruction", out string? expression)
            || !TryGetUniqueI2iStringAllowEmpty(options, "outfitInstruction", out string? outfit)
            || !TryGetUniqueI2iStringAllowEmpty(options, "backgroundInstruction", out string? background)
            || !TryGetUniqueI2iStringAllowEmpty(options, "poseInstruction", out string? pose)
            || !new[] { overall, expression, outfit, background, pose }.All(value =>
                IsNormalizedI2iV3Text(value, I2iV3MaximumDirectiveCodePoints, allowEmpty: true))
            || !new[] { overall, expression, outfit, background, pose }.Any(static value => value!.Length > 0)
            || !TryGetUniqueI2iString(options, "prompt", out string? prompt)
            || !IsNormalizedI2iV3Text(prompt, I2iV3MaximumPromptCodePoints, allowEmpty: false)
            || !TryGetUniqueI2iStringAllowEmpty(options, "negativePrompt", out string? negativePrompt)
            || negativePrompt != ""
            || !TryGetUniqueI2iInt32(options, "steps", out int steps)
            || steps is < 4 or > 20
            || !TryGetUniqueI2iDouble(options, "cfgScale", out double cfgScale)
            || cfgScale is < 0.5d or > 3d
            || !TryGetUniqueI2iInt32(options, "maxDimension", out int maxDimension)
            || maxDimension != 1280
            || !TryGetUniqueI2iInt32(options, "seed", out int seed)
            || seed < 0
            || !TryGetUniqueI2iString(options, "outfitMaskMode", out string? maskMode)
            || maskMode is not ("auto" or "manual")
            || !TryGetUniqueI2iInt32(options, "outfitMaskExpandPixels", out int expansion)
            || expansion is < 0 or > 160
            || (maskMode == "auto" && expansion != I2iV3AutomaticOutfitExpansion)
            || !TryGetUniqueI2iString(options, "workflowRevision", out string? workflowRevision)
            || workflowRevision != I2iV3WorkflowRevision
            || !TryGetUniqueI2iString(options, "maskRevision", out string? maskRevision)
            || maskRevision != I2iV3MaskRevision
            || !TryGetUniqueI2iString(options, "promptSnapshotSha256", out string? promptHash)
            || !IsLowerHex(promptHash, 64)
            || !TryGetUniqueI2iBoolean(options, "loraEnabled", out bool loraEnabled)
            || loraEnabled)
        {
            return false;
        }

        var snapshot = new I2iV3WorkspaceSnapshot(
            overall!, expression!, outfit!, background!, pose!,
            steps, cfgScale, maskMode!, expansion, seed);
        string exactPrompt = BuildI2iV3Prompt(snapshot);
        if (!string.Equals(prompt, exactPrompt, StringComparison.Ordinal)
            || !string.Equals(
                promptHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactPrompt)))
                    .ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return false;
        }

        info = new I2iV3JobInfo(snapshot, adapterId!);
        return true;
    }

    private static bool HasExactI2iV3PresetEnvelope(
        JsonElement job,
        JsonElement preset)
    {
        if (!TryGetUniqueI2iString(job, "presetHash", out string? presetHash)
            || !IsLowerHex(presetHash, 12)
            || !TryGetUniqueI2iString(preset, "id", out string? id)
            || id != I2iV3PresetId
            || !TryGetUniqueI2iString(preset, "label", out string? label)
            || label != "FLUX.2 unified guided edit v3"
            || !TryGetUniqueI2iString(preset, "modelFamily", out string? family)
            || family != "photo"
            || !TryGetUniqueI2iString(preset, "modelName", out string? model)
            || model != "flux-2-klein-9b-Q4_K_M.gguf"
            || !TryGetUniqueI2iDouble(preset, "scale", out double scale)
            || scale != 1d
            || !TryGetUniqueI2iString(preset, "outputFormat", out string? format)
            || format != "png")
        {
            return false;
        }
        foreach (string property in new[]
        {
            "denoise", "sharpen", "detail", "smoothness",
            "colorBrightness", "colorContrast", "colorSaturation",
        })
        {
            if (!TryGetUniqueI2iDouble(preset, property, out double value) || value != 0d)
                return false;
        }
        return string.Equals(
            presetHash,
            ComputeI2iV2PresetHash(preset),
            StringComparison.Ordinal);
    }

    private static bool IsI2iV3MutationSafe(JsonElement job)
        => TryReadI2iV3JobInfo(job, out _);

    private async void OpenModalI2iEditV3_Click(object sender, RoutedEventArgs e)
        => await OpenI2iV3EditBoardAsync();

    private async Task<bool> OpenI2iV3EditBoardAsync(
        I2iV3WorkspaceSnapshot? initialSettings = null)
    {
        if (!TryResolveCurrentModalI2iEditSource(
                out I2iEditSource source,
                out string error))
        {
            SetStatusToast(error);
            return false;
        }
        if (I2iEditDialog?.Visibility == Visibility.Visible)
            CloseI2iEditBoard(restoreFocus: false);
        if (I2iV2EditDialog?.Visibility == Visibility.Visible)
            CloseI2iV2EditBoard(restoreFocus: false);

        _i2iV3FocusBeforeBoard = Keyboard.FocusedElement;
        _i2iV3EditSource = source;
        _i2iV3Capability = null;
        _i2iV3CapabilityUnknown = false;
        _i2iV3CapabilityCheckPending = true;
        _i2iV3RequestPending = false;
        long generation = ++_i2iV3BoardGeneration;

        _syncingI2iV3Controls = true;
        I2iEditStyleState? selectedStyle = initialSettings is null
            ? FindI2iV3Style(_selectedI2iV3StyleName)
            : null;
        ApplyI2iV3SettingsToControls(
            initialSettings
            ?? selectedStyle?.ToSnapshot()
            ?? I2iV3WorkspaceSnapshot.Default,
            selectStyle: initialSettings is null,
            fixedSeed: initialSettings is not null
                || string.Equals(selectedStyle?.SeedMode, FixedSeedMode, StringComparison.Ordinal));
        RefreshI2iV3StyleControls(updateName: true);
        _syncingI2iV3Controls = false;
        I2iV3SourceText.Text = source.Label;
        I2iV3EditScrollViewer.ScrollToTop();
        I2iV3EditDialog.Visibility = Visibility.Visible;
        UpdateI2iV3BoardPresentation("バックエンドの統合AI編集対応を確認しています…");
        _ = Dispatcher.BeginInvoke(I2iV3OverallTextBox.Focus, DispatcherPriority.Input);
        await RefreshI2iV3CapabilityForBoardAsync(generation);
        return I2iV3EditDialog.Visibility == Visibility.Visible;
    }

    private async Task RefreshI2iV3CapabilityForBoardAsync(long generation)
    {
        EnhancementApiResponse response = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            timeoutMilliseconds: DurableEnqueueActionDeadlineMilliseconds);
        if (generation != _i2iV3BoardGeneration
            || I2iV3EditDialog.Visibility != Visibility.Visible)
        {
            return;
        }

        _i2iV3CapabilityCheckPending = false;
        EnhancementEnqueueBackendMode mode = EnhancementEnqueueProbePolicy.Classify(
            response.Ok,
            response.StatusCode,
            response.Payload);
        if (mode == EnhancementEnqueueBackendMode.Unknown)
        {
            _i2iV3Capability = null;
            _i2iV3CapabilityUnknown = true;
            UpdateI2iV3BoardPresentation(
                "バックエンドの統合AI編集対応を確認できません。ジョブは追加されません。");
            return;
        }

        _i2iV3CapabilityUnknown = false;
        if (!response.Ok
            || response.Payload is not JsonElement payload
            || !TryParseI2iV3Capability(payload, out I2iV3CapabilityState capability))
        {
            _i2iV3Capability = null;
            UpdateI2iV3BoardPresentation(
                "統合AI編集の対応状態を確認できません。既存Jobsは変更されません。");
            return;
        }

        _i2iV3Capability = capability;
        UpdateI2iV3BoardPresentation(
            capability.IsReady
                ? "入力した欄だけを変更します。GOを押すまでジョブは追加されません。"
                : $"統合AI編集はまだ準備中です（{capability.IssueCode}）。");
    }

    private static string ReadComboTag(ComboBox comboBox, string fallback)
        => comboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? tag
            : fallback;

    private I2iV3WorkspaceSnapshot ReadI2iV3Controls(int resolvedSeed)
        => new(
            I2iV3OverallTextBox.Text.Trim(),
            I2iV3ExpressionTextBox.Text.Trim(),
            I2iV3OutfitTextBox.Text.Trim(),
            I2iV3BackgroundTextBox.Text.Trim(),
            I2iV3PoseTextBox.Text.Trim(),
            (int)Math.Round(I2iV3StepsSlider.Value),
            Math.Round(I2iV3CfgSlider.Value, 1),
            ReadComboTag(I2iV3OutfitMaskModeComboBox, "auto"),
            ReadComboTag(I2iV3OutfitMaskModeComboBox, "auto") == "auto"
                ? I2iV3AutomaticOutfitExpansion
                : (int)Math.Round(I2iV3OutfitMaskExpandSlider.Value),
            resolvedSeed);

    private bool TryResolveI2iV3Seed(out int? seed, out string error)
    {
        seed = null;
        error = "";
        if (!SelectedSeedModeIsFixed(I2iV3SeedModeComboBox))
            return true;
        if (TryParseFixedSeed(I2iV3SeedValueTextBox.Text.Trim(), out int fixedSeed))
        {
            seed = fixedSeed;
            return true;
        }
        error = "Fixed seed は 0〜2147483647 の整数で入力してください。";
        return false;
    }

    private static bool IsValidI2iV3Snapshot(I2iV3WorkspaceSnapshot snapshot)
        => new[]
            {
                snapshot.Overall, snapshot.Expression, snapshot.Outfit,
                snapshot.Background, snapshot.Pose,
            }.All(value => IsNormalizedI2iV3Text(
                value,
                I2iV3MaximumDirectiveCodePoints,
                allowEmpty: true))
            && new[]
            {
                snapshot.Overall, snapshot.Expression, snapshot.Outfit,
                snapshot.Background, snapshot.Pose,
            }.Any(static value => value.Length > 0)
            && snapshot.Steps is >= 4 and <= 20
            && snapshot.CfgScale is >= 0.5d and <= 3d
            && snapshot.OutfitMaskMode is "auto" or "manual"
            && snapshot.OutfitMaskExpandPixels is >= 0 and <= 160
            && (snapshot.OutfitMaskMode != "auto"
                || snapshot.OutfitMaskExpandPixels == I2iV3AutomaticOutfitExpansion)
            && snapshot.Seed >= 0
            && IsNormalizedI2iV3Text(
                BuildI2iV3Prompt(snapshot),
                I2iV3MaximumPromptCodePoints,
                allowEmpty: false);

    private void UpdateI2iV3BoardPresentation(string? statusOverride = null)
    {
        // Slider and ComboBox change events fire while BAML is still creating
        // later named controls. Do not read the board until the complete
        // fail-closed surface exists.
        if (I2iV3EditDialog is null
            || I2iV3OverallTextBox is null
            || I2iV3ExpressionTextBox is null
            || I2iV3OutfitTextBox is null
            || I2iV3BackgroundTextBox is null
            || I2iV3PoseTextBox is null
            || I2iV3StepsSlider is null
            || I2iV3CfgSlider is null
            || I2iV3OutfitMaskModeComboBox is null
            || I2iV3OutfitMaskExpandSlider is null
            || I2iV3SeedModeComboBox is null
            || I2iV3SeedValueTextBox is null
            || I2iV3EditStatusText is null
            || I2iV3QueueButton is null)
            return;
        bool seedValid = TryResolveI2iV3Seed(out int? seed, out string seedError);
        I2iV3WorkspaceSnapshot snapshot = ReadI2iV3Controls(seed ?? 0);
        bool fieldsValid = IsValidI2iV3Snapshot(snapshot);
        string sourceError = "AI編集の入力を確認できません。ボードを開き直してください。";
        bool sourceValid = _i2iV3EditSource is not null
            && RevalidateI2iEditSource(_i2iV3EditSource, out _, out sourceError);
        bool ready = _i2iV3Capability?.IsReady == true;
        I2iV3StepsValueText.Text = snapshot.Steps.ToString(CultureInfo.InvariantCulture);
        I2iV3CfgValueText.Text = snapshot.CfgScale.ToString("0.0", CultureInfo.InvariantCulture);
        I2iV3OutfitMaskExpandValueText.Text = snapshot.OutfitMaskMode == "auto"
            ? $"自動 {I2iV3AutomaticOutfitExpansion} px"
            : $"{snapshot.OutfitMaskExpandPixels} px";

        I2iV3EditStatusText.Text = statusOverride
            ?? (_i2iV3CapabilityCheckPending
                ? "バックエンドの統合AI編集対応を確認しています…"
                : _i2iV3CapabilityUnknown
                    ? "バックエンドの対応を確認できません。ジョブは追加されません。"
                    : !ready
                        ? "統合AI編集がreadyになるまでジョブは追加できません。"
                        : !sourceValid
                            ? sourceError
                            : !seedValid
                                ? seedError
                                : !fieldsValid
                                    ? "少なくとも1欄へ変更内容を入力してください。各欄は2000文字相当までです。"
                                    : $"{BuildI2iV3Summary(snapshot)}を1件のジョブとして追加します。空欄は変更しません。");
        I2iV3QueueButton.IsEnabled = ready
            && !_i2iV3CapabilityCheckPending
            && !_i2iV3RequestPending
            && sourceValid
            && seedValid
            && fieldsValid;
        I2iV3CloseButton.IsEnabled = !_i2iV3RequestPending;
        I2iV3ResetButton.IsEnabled = !_i2iV3RequestPending;
        I2iV3SaveStyleButton.IsEnabled = !_i2iV3RequestPending;
        I2iV3DeleteStyleButton.IsEnabled = !_i2iV3RequestPending
            && FindI2iV3Style(_selectedI2iV3StyleName) is not null;
    }

    private void I2iV3EditField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingI2iV3Controls && !ReferenceEquals(sender, I2iV3StyleNameTextBox))
            MarkI2iV3StyleAsCustom();
        UpdateI2iV3BoardPresentation();
    }

    private void I2iV3SettingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingI2iV3Controls)
            MarkI2iV3StyleAsCustom();
        UpdateI2iV3BoardPresentation();
    }

    private void I2iV3OutfitMaskMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (I2iV3OutfitMaskExpandSlider is null)
            return;
        I2iV3OutfitMaskExpandSlider.IsEnabled =
            ReadComboTag(I2iV3OutfitMaskModeComboBox, "auto") == "manual";
        if (!_syncingI2iV3Controls)
            MarkI2iV3StyleAsCustom();
        UpdateI2iV3BoardPresentation();
    }

    private void I2iV3SeedMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (I2iV3SeedValueTextBox is null)
            return;
        I2iV3SeedValueTextBox.IsEnabled = SelectedSeedModeIsFixed(I2iV3SeedModeComboBox);
        if (!_syncingI2iV3Controls)
            MarkI2iV3StyleAsCustom();
        UpdateI2iV3BoardPresentation();
    }

    private void ResetI2iV3Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_i2iV3RequestPending)
            return;
        _syncingI2iV3Controls = true;
        _selectedI2iV3StyleName = null;
        ApplyI2iV3SettingsToControls(
            I2iV3WorkspaceSnapshot.Default,
            selectStyle: true,
            fixedSeed: false);
        RefreshI2iV3StyleControls(updateName: true);
        _syncingI2iV3Controls = false;
        UpdateI2iV3BoardPresentation();
        I2iV3OverallTextBox.Focus();
    }

    private async void QueueI2iV3Edit_Click(object sender, RoutedEventArgs e)
        => await QueueI2iV3EditAsync();

    private async Task<bool> QueueI2iV3EditAsync(string queuePlacement = "last")
    {
        if (queuePlacement is not ("last" or "next"))
            throw new ArgumentOutOfRangeException(nameof(queuePlacement));

        if (_i2iV3RequestPending || _i2iV3EditSource is not I2iEditSource source)
            return false;
        if (!RevalidateI2iEditSource(source, out Tile tile, out string sourceError))
        {
            UpdateI2iV3BoardPresentation(sourceError);
            return false;
        }
        Func<string?>? prePublishValidator = CaptureExternalFileDropPrePublishValidator(tile);
        int? requestedSeed = null;
        string seedError = "";
        if (_i2iV3CapabilityCheckPending
            || _i2iV3Capability is not I2iV3CapabilityState boardCapability
            || !boardCapability.IsReady
            || !TryResolveI2iV3Seed(out requestedSeed, out seedError))
        {
            UpdateI2iV3BoardPresentation(
                seedError.Length > 0
                    ? seedError
                    : "バックエンドの統合AI編集対応を確認できません。");
            return false;
        }
        I2iV3WorkspaceSnapshot requested = ReadI2iV3Controls(requestedSeed ?? 0);
        if (!IsValidI2iV3Snapshot(requested))
        {
            UpdateI2iV3BoardPresentation("少なくとも1欄へ変更内容を入力してください。");
            return false;
        }

        _i2iV3RequestPending = true;
        UpdateI2iV3BoardPresentation("統合AI編集の追加準備をしています…");
        try
        {
            I2iV3CapabilityState? validatedCapability = null;
            string? ValidateHealth(JsonElement health)
            {
                if (!TryParseI2iV3Capability(health, out I2iV3CapabilityState parsed)
                    || !parsed.IsReady)
                {
                    _i2iV3Capability = null;
                    return "The Aibos Image local AI service is not ready for unified AI editing. No job was added.";
                }
                validatedCapability = parsed;
                _i2iV3Capability = parsed;
                return null;
            }

            var edits = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["overall"] = requested.Overall,
                ["expression"] = requested.Expression,
                ["outfit"] = requested.Outfit,
                ["background"] = requested.Background,
                ["pose"] = requested.Pose,
            };
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceId"] = source.SourcePath,
                ["operation"] = "i2i",
                ["i2iSchemaVersion"] = 3,
                ["presetId"] = I2iV3PresetId,
                ["adapterId"] = I2iV3AdapterId,
                ["edits"] = edits,
                ["steps"] = requested.Steps,
                ["cfgScale"] = requested.CfgScale,
                ["outfitMaskMode"] = requested.OutfitMaskMode,
                ["outfitMaskExpandPixels"] = requested.OutfitMaskExpandPixels,
            };
            if (!string.IsNullOrWhiteSpace(source.SourceProducerJobId))
                body["sourceProducerJobId"] = source.SourceProducerJobId;
            if (requestedSeed.HasValue)
                body["seed"] = requestedSeed.Value;
            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                body,
                queuePlacement,
                healthValidator: ValidateHealth,
                recoverySourceIdentity: source.SourcePath,
                prePublishValidator: prePublishValidator);
            if (response.SavedForDelivery)
            {
                _i2iV3RequestPending = false;
                CloseI2iV3EditBoard(restoreFocus: false);
                SetTransientStatusToast(
                    $"{tile.FileName}: 統合AI編集予約を保存しました。Jobsへの登録を継続しています。");
                return true;
            }
            if (!response.Ok
                || validatedCapability is null
                || response.Payload is not JsonElement payload
                || !TryGetUniqueI2iProperty(payload, "job", out JsonElement job)
                || !IsI2iV3JobForExpectedSource(job, source, requested, requestedSeed))
            {
                UpdateI2iV3BoardPresentation(
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
            _i2iV3RequestPending = false;
            CloseI2iV3EditBoard(restoreFocus: false);
            SetTransientStatusToast(
                $"{tile.FileName}: {BuildI2iV3Summary(requested)}のAI編集をJobsへ1件追加しました。");
            return true;
        }
        finally
        {
            _i2iV3RequestPending = false;
            if (I2iV3EditDialog?.Visibility == Visibility.Visible)
                UpdateI2iV3BoardPresentation();
        }
    }

    private bool IsI2iV3JobForExpectedSource(
        JsonElement job,
        I2iEditSource expected,
        I2iV3WorkspaceSnapshot requested,
        int? requestedSeed)
    {
        if (!TryReadI2iV3JobInfo(job, out I2iV3JobInfo info)
            || !TryGetUniqueI2iString(job, "status", out string? status)
            || status is not ("queued" or "running")
            || !TryGetUniqueI2iString(job, "sourceId", out string? sourceId)
            || !TryGetUniqueI2iString(job, "sourcePath", out string? sourcePath)
            || !TryResolveEnhancementSourceIdentity(sourceId, out string resolvedId)
            || !TryResolveEnhancementSourceIdentity(sourcePath, out string resolvedPath)
            || !EnhancementSourceIdentityComparer.Equals(resolvedId, expected.SourcePath)
            || !EnhancementSourceIdentityComparer.Equals(resolvedPath, expected.SourcePath)
            || !TryReadOptionalI2iSourceProducerJobId(job, out string? producerId))
        {
            return false;
        }
        I2iV3WorkspaceSnapshot actual = info.Snapshot;
        return string.Equals(producerId, expected.SourceProducerJobId, StringComparison.Ordinal)
            && string.Equals(actual.Overall, requested.Overall, StringComparison.Ordinal)
            && string.Equals(actual.Expression, requested.Expression, StringComparison.Ordinal)
            && string.Equals(actual.Outfit, requested.Outfit, StringComparison.Ordinal)
            && string.Equals(actual.Background, requested.Background, StringComparison.Ordinal)
            && string.Equals(actual.Pose, requested.Pose, StringComparison.Ordinal)
            && actual.Steps == requested.Steps
            && actual.CfgScale == requested.CfgScale
            && actual.OutfitMaskMode == requested.OutfitMaskMode
            && actual.OutfitMaskExpandPixels == requested.OutfitMaskExpandPixels
            && (!requestedSeed.HasValue || actual.Seed == requestedSeed.Value);
    }

    private void CloseI2iV3Edit_Click(object sender, RoutedEventArgs e)
        => CloseI2iV3EditBoard(restoreFocus: true);

    private void I2iV3EditBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, I2iV3EditDialog))
            CloseI2iV3EditBoard(restoreFocus: true);
    }

    private void CloseI2iV3EditBoard(bool restoreFocus)
    {
        if (I2iV3EditDialog is null || _i2iV3RequestPending)
            return;
        IInputElement? focus = _i2iV3FocusBeforeBoard;
        _i2iV3FocusBeforeBoard = null;
        _i2iV3EditSource = null;
        _i2iV3Capability = null;
        _i2iV3CapabilityUnknown = false;
        _i2iV3CapabilityCheckPending = false;
        _i2iV3BoardGeneration++;
        I2iV3EditDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus && focus is not null)
            _ = Dispatcher.BeginInvoke(() => Keyboard.Focus(focus), DispatcherPriority.Input);
    }

    private void ApplyI2iV3SettingsToControls(
        I2iV3WorkspaceSnapshot snapshot,
        bool selectStyle,
        bool fixedSeed)
    {
        I2iV3OverallTextBox.Text = snapshot.Overall;
        I2iV3ExpressionTextBox.Text = snapshot.Expression;
        I2iV3OutfitTextBox.Text = snapshot.Outfit;
        I2iV3BackgroundTextBox.Text = snapshot.Background;
        I2iV3PoseTextBox.Text = snapshot.Pose;
        I2iV3StepsSlider.Value = snapshot.Steps;
        I2iV3CfgSlider.Value = snapshot.CfgScale;
        I2iV3OutfitMaskModeComboBox.SelectedIndex = snapshot.OutfitMaskMode == "manual" ? 1 : 0;
        I2iV3OutfitMaskExpandSlider.Value = snapshot.OutfitMaskExpandPixels;
        I2iV3OutfitMaskExpandSlider.IsEnabled = snapshot.OutfitMaskMode == "manual";
        SelectSeedMode(I2iV3SeedModeComboBox, fixedSeed);
        I2iV3SeedValueTextBox.Text = snapshot.Seed.ToString(CultureInfo.InvariantCulture);
        I2iV3SeedValueTextBox.IsEnabled = fixedSeed;
        if (!selectStyle)
            _selectedI2iV3StyleName = null;
    }

    private I2iEditStyleState? FindI2iV3Style(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _i2iV3Styles.FirstOrDefault(style =>
                string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidI2iV3StyleName(string name)
        => name.Length is >= 1 and <= I2iV3MaximumStyleNameLength
            && !name.Any(char.IsControl);

    private void I2iV3Style_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingI2iV3Controls
            || I2iV3StyleComboBox.SelectedItem is not I2iV3StyleChoice choice)
        {
            return;
        }
        I2iEditStyleState? style = FindI2iV3Style(choice.StyleName);
        if (style is null)
        {
            _selectedI2iV3StyleName = null;
            RefreshI2iV3StyleControls(updateName: false);
            I2iV3StyleStatusText.Text = "現在の未保存設定です。";
            return;
        }
        _syncingI2iV3Controls = true;
        _selectedI2iV3StyleName = style.Name;
        ApplyI2iV3SettingsToControls(
            style.ToSnapshot(),
            selectStyle: true,
            fixedSeed: string.Equals(style.SeedMode, FixedSeedMode, StringComparison.Ordinal));
        RefreshI2iV3StyleControls(updateName: true);
        _syncingI2iV3Controls = false;
        I2iV3StyleStatusText.Text = $"「{style.Name}」を反映しました。GOを押すまで追加されません。";
        UpdateI2iV3BoardPresentation();
        if (!_initializing)
            SaveState();
    }

    private void SaveI2iV3Style_Click(object sender, RoutedEventArgs e)
    {
        string name = I2iV3StyleNameTextBox.Text.Trim();
        if (!IsValidI2iV3StyleName(name))
        {
            I2iV3StyleStatusText.Text = $"Style名は1〜{I2iV3MaximumStyleNameLength}文字で入力してください。";
            return;
        }
        if (!TryResolveI2iV3Seed(out int? seed, out string seedError))
        {
            I2iV3StyleStatusText.Text = seedError;
            return;
        }
        I2iV3WorkspaceSnapshot snapshot = ReadI2iV3Controls(seed ?? DefaultFixedSeedValue);
        I2iEditStyleState saved = I2iEditStyleState.FromSnapshot(
            name,
            snapshot,
            SelectedSeedModeIsFixed(I2iV3SeedModeComboBox)
                ? FixedSeedMode
                : RandomSeedMode);
        int index = _i2iV3Styles.FindIndex(style =>
            string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _i2iV3Styles[index] = saved;
        else if (_i2iV3Styles.Count >= I2iV3MaximumStyleCount)
        {
            I2iV3StyleStatusText.Text = $"Styleは最大{I2iV3MaximumStyleCount}件です。";
            return;
        }
        else
            _i2iV3Styles.Add(saved);
        _i2iV3Styles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        _selectedI2iV3StyleName = saved.Name;
        RefreshI2iV3StyleControls(updateName: true);
        I2iV3StyleStatusText.Text = index >= 0
            ? $"「{saved.Name}」を現在の入力と設定で上書きしました。"
            : $"「{saved.Name}」を保存しました。";
        if (!_initializing)
            SaveState();
    }

    private void DeleteI2iV3Style_Click(object sender, RoutedEventArgs e)
    {
        I2iEditStyleState? style = FindI2iV3Style(_selectedI2iV3StyleName);
        if (style is null)
        {
            I2iV3StyleStatusText.Text = "削除する保存済みStyleを選んでください。";
            return;
        }
        _i2iV3Styles.Remove(style);
        _selectedI2iV3StyleName = null;
        RefreshI2iV3StyleControls(updateName: true);
        I2iV3StyleStatusText.Text = $"「{style.Name}」を削除しました。現在の入力は残ります。";
        if (!_initializing)
            SaveState();
    }

    private void MarkI2iV3StyleAsCustom()
    {
        if (_syncingI2iV3Controls || _selectedI2iV3StyleName is null)
            return;
        _selectedI2iV3StyleName = null;
        RefreshI2iV3StyleControls(updateName: false);
        I2iV3StyleStatusText.Text = "設定を変更しました。保存済みStyleは上書きされていません。";
    }

    private void RefreshI2iV3StyleControls(bool updateName)
    {
        if (I2iV3StyleComboBox is null)
            return;
        var choices = new List<I2iV3StyleChoice>
        {
            new("現在の設定（未保存）", null),
        };
        choices.AddRange(_i2iV3Styles.Select(style => new I2iV3StyleChoice(style.Name, style.Name)));
        I2iV3StyleChoice selected = choices.FirstOrDefault(choice =>
            string.Equals(choice.StyleName, _selectedI2iV3StyleName, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
        bool prior = _syncingI2iV3Controls;
        _syncingI2iV3Controls = true;
        I2iV3StyleComboBox.ItemsSource = choices;
        I2iV3StyleComboBox.SelectedItem = selected;
        if (updateName)
            I2iV3StyleNameTextBox.Text = _selectedI2iV3StyleName ?? "";
        I2iV3DeleteStyleButton.IsEnabled = selected.StyleName is not null;
        _syncingI2iV3Controls = prior;
    }

    private void RestoreI2iV3Styles(
        IEnumerable<I2iEditStyleState>? styles,
        string? selectedName)
    {
        _i2iV3Styles.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (I2iEditStyleState? candidate in styles ?? [])
        {
            I2iEditStyleState? normalized = I2iEditStyleState.Normalize(candidate);
            if (normalized is null || !names.Add(normalized.Name))
                continue;
            _i2iV3Styles.Add(normalized);
            if (_i2iV3Styles.Count >= I2iV3MaximumStyleCount)
                break;
        }
        _i2iV3Styles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        _selectedI2iV3StyleName = FindI2iV3Style(selectedName)?.Name;
        RefreshI2iV3StyleControls(updateName: true);
    }

    private List<I2iEditStyleState>? SnapshotI2iV3Styles()
        => _i2iV3Styles.Count == 0
            ? null
            : _i2iV3Styles.Select(static style => I2iEditStyleState.Normalize(style)!).ToList();

    public static bool IsI2iV3MutationSafeForSmoke(JsonElement job)
        => IsI2iV3MutationSafe(job);

    public bool I2iV3EditBoardVisibleForSmoke
        => I2iV3EditDialog.Visibility == Visibility.Visible;

    public bool I2iV3StyleSurfaceForSmoke
        => I2iV3StyleComboBox is not null
            && I2iV3StyleNameTextBox.MaxLength == I2iV3MaximumStyleNameLength
            && I2iV3OverallTextBox.MaxLength == I2iV3MaximumDirectiveCodePoints
            && I2iV3StepsSlider.Minimum == 4
            && I2iV3StepsSlider.Maximum == 20
            && I2iV3CfgSlider.Minimum == 0.5d
            && I2iV3CfgSlider.Maximum == 3d
            && I2iV3OutfitMaskExpandSlider.Maximum == 160;

    public static bool TryReadI2iV3JobInfoForSmoke(
        JsonElement job,
        out string summary,
        out int steps,
        out double cfgScale)
    {
        summary = "";
        steps = 0;
        cfgScale = 0;
        if (!TryReadI2iV3JobInfo(job, out I2iV3JobInfo info))
            return false;
        summary = BuildI2iV3Summary(info.Snapshot);
        steps = info.Snapshot.Steps;
        cfgScale = info.Snapshot.CfgScale;
        return true;
    }

    public static bool TryParseI2iV3CapabilityForSmoke(
        JsonElement health,
        out bool ready,
        out string issueCode)
    {
        ready = false;
        issueCode = "INVALID_CAPABILITY";
        if (!TryParseI2iV3Capability(health, out I2iV3CapabilityState capability))
            return false;
        ready = capability.IsReady;
        issueCode = capability.IssueCode;
        return true;
    }
}

public sealed record I2iV3WorkspaceSnapshot(
    string Overall,
    string Expression,
    string Outfit,
    string Background,
    string Pose,
    int Steps,
    double CfgScale,
    string OutfitMaskMode,
    int OutfitMaskExpandPixels,
    int Seed)
{
    public static I2iV3WorkspaceSnapshot Default { get; } = new(
        "", "", "", "", "", 8, 1d, "auto", 64, 123456789);
}

public sealed class I2iEditStyleState
{
    public string Name { get; set; } = "";
    public string Overall { get; set; } = "";
    public string Expression { get; set; } = "";
    public string Outfit { get; set; } = "";
    public string Background { get; set; } = "";
    public string Pose { get; set; } = "";
    public int Steps { get; set; } = 8;
    public double CfgScale { get; set; } = 1d;
    public string OutfitMaskMode { get; set; } = "auto";
    public int OutfitMaskExpandPixels { get; set; } = 64;
    public string SeedMode { get; set; } = "random";
    public int Seed { get; set; } = 123456789;
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public I2iV3WorkspaceSnapshot ToSnapshot()
        => new(
            Overall, Expression, Outfit, Background, Pose,
            Steps, CfgScale, OutfitMaskMode, OutfitMaskExpandPixels, Seed);

    public static I2iEditStyleState FromSnapshot(
        string name,
        I2iV3WorkspaceSnapshot snapshot,
        string seedMode = "fixed")
        => new()
        {
            Name = name,
            Overall = snapshot.Overall,
            Expression = snapshot.Expression,
            Outfit = snapshot.Outfit,
            Background = snapshot.Background,
            Pose = snapshot.Pose,
            Steps = snapshot.Steps,
            CfgScale = snapshot.CfgScale,
            OutfitMaskMode = snapshot.OutfitMaskMode,
            OutfitMaskExpandPixels = snapshot.OutfitMaskExpandPixels,
            SeedMode = seedMode,
            Seed = snapshot.Seed,
        };

    public static I2iEditStyleState? Normalize(I2iEditStyleState? value)
    {
        if (value is null)
            return null;
        string name = value.Name?.Trim() ?? "";
        string overall = value.Overall?.Trim() ?? "";
        string expression = value.Expression?.Trim() ?? "";
        string outfit = value.Outfit?.Trim() ?? "";
        string background = value.Background?.Trim() ?? "";
        string pose = value.Pose?.Trim() ?? "";
        if (name.Length is < 1 or > 48
            || name.Any(char.IsControl)
            || new[] { overall, expression, outfit, background, pose }
                .Any(static text => text.EnumerateRunes().Take(2_001).Count() > 2_000)
            || value.Steps is < 4 or > 20
            || value.CfgScale is < 0.5d or > 3d
            || value.OutfitMaskMode is not ("auto" or "manual")
            || value.OutfitMaskExpandPixels is < 0 or > 160
            || (value.OutfitMaskMode == "auto" && value.OutfitMaskExpandPixels != 64)
            || value.SeedMode is not ("random" or "fixed")
            || value.Seed < 0)
        {
            return null;
        }
        return FromSnapshot(
            name,
            new I2iV3WorkspaceSnapshot(
                overall, expression, outfit, background, pose,
                value.Steps, value.CfgScale, value.OutfitMaskMode,
                value.OutfitMaskExpandPixels, value.Seed),
            value.SeedMode);
    }
}

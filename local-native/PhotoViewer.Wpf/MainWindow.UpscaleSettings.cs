using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string DefaultUpscalePresetId = "anime-sharp-x2";
    private const string DefaultUpscaleAdapterId = "comfyui";
    private const double DefaultUpscaleScale = 2d;
    private const string DefaultUpscaleOutputFormat = "webp";

    private static readonly UpscalePresetDefinition[] UpscalePresetDefinitions =
    [
        new("anime-sharp-x2", "Anime clean x2", "Anime / illustration", 2d, "webp"),
        new("anime-detail-x4", "Anime crisp detail x4", "Anime detail", 4d, "webp"),
        new("photo-natural-x2", "Photo natural x2", "Photo / realistic", 2d, "jpg"),
        new("photo-detail-x4", "Photo texture detail x4", "Photo texture", 4d, "webp"),
        new("general-balanced-x4", "General balanced x4", "General", 4d, "webp"),
        new("general-max-x6", "General strong detail x6", "General strong detail", 6d, "webp"),
    ];

    private static readonly double[] UpscaleScaleChoices = [1.5d, 2d, 3d, 4d, 6d, 8d];

    private string _upscaleOutputFormat = DefaultUpscaleOutputFormat;
    private bool _syncingUpscaleSettingsControls;

    private readonly record struct UpscalePresetDefinition(
        string Id,
        string Label,
        string FamilyLabel,
        double Scale,
        string OutputFormat);

    private void RestoreUpscaleSettings(ViewerState? state)
    {
        _modalEnhancementPresetId = NormalizeUpscalePresetId(state?.UpscalePresetId);
        _modalEnhancementAdapterId = NormalizeUpscaleAdapterId(
            state?.UpscaleAdapterId);
        _modalEnhancementScale = NormalizeUpscaleScale(
            state?.UpscaleScale,
            _modalEnhancementAdapterId,
            fallback: PresetForId(_modalEnhancementPresetId).Scale);
        _upscaleOutputFormat = NormalizeUpscaleOutputFormat(
            state?.UpscaleOutputFormat,
            PresetForId(_modalEnhancementPresetId).OutputFormat);
        SyncUpscaleSettingsControls();
    }

    private static string NormalizeUpscalePresetId(string? value)
        => UpscalePresetDefinitions.Any(preset =>
                string.Equals(preset.Id, value, StringComparison.Ordinal))
            ? value!
            : DefaultUpscalePresetId;

    private static string NormalizeUpscaleAdapterId(string? value)
        => value is "realesrgan-ncnn" or "comfyui"
            ? value
            : DefaultUpscaleAdapterId;

    private static string NormalizeUpscaleOutputFormat(
        string? value,
        string fallback = DefaultUpscaleOutputFormat)
        => value is "png" or "webp" or "jpg"
            ? value
            : fallback is "png" or "webp" or "jpg"
                ? fallback
                : DefaultUpscaleOutputFormat;

    private static double NormalizeUpscaleScale(
        double? value,
        string adapterId,
        double fallback = DefaultUpscaleScale)
    {
        double candidate = value is double requested
            && double.IsFinite(requested)
            ? requested
            : fallback;
        double maximum = string.Equals(
            adapterId,
            "realesrgan-ncnn",
            StringComparison.Ordinal)
                ? 4d
                : 8d;
        return UpscaleScaleChoices
            .Where(scale => scale <= maximum)
            .OrderBy(scale => Math.Abs(scale - candidate))
            .ThenBy(static scale => scale)
            .First();
    }

    private static UpscalePresetDefinition PresetForId(string? presetId)
        => UpscalePresetDefinitions.FirstOrDefault(preset =>
                string.Equals(preset.Id, presetId, StringComparison.Ordinal))
            is { Id.Length: > 0 } matched
                ? matched
                : UpscalePresetDefinitions[0];

    private void SyncUpscaleSettingsControls()
    {
        if (AppUpscalePresetComboBox is null
            || AppUpscaleAdapterComboBox is null
            || AppUpscaleScaleComboBox is null
            || AppUpscaleFormatComboBox is null
            || ModalUpscalePresetComboBox is null
            || ModalUpscaleAdapterComboBox is null
            || ModalUpscaleScaleComboBox is null
            || ModalUpscaleFormatComboBox is null)
        {
            return;
        }

        _syncingUpscaleSettingsControls = true;
        try
        {
            _modalEnhancementPresetId = NormalizeUpscalePresetId(
                _modalEnhancementPresetId);
            _modalEnhancementAdapterId = NormalizeUpscaleAdapterId(
                _modalEnhancementAdapterId);
            _modalEnhancementScale = NormalizeUpscaleScale(
                _modalEnhancementScale,
                _modalEnhancementAdapterId);
            _upscaleOutputFormat = NormalizeUpscaleOutputFormat(
                _upscaleOutputFormat);

            foreach (ComboBox combo in new[]
                     {
                         AppUpscalePresetComboBox,
                         ModalUpscalePresetComboBox,
                     })
            {
                SelectComboItemByTag(combo, _modalEnhancementPresetId);
            }
            foreach (ComboBox combo in new[]
                     {
                         AppUpscaleAdapterComboBox,
                         ModalUpscaleAdapterComboBox,
                     })
            {
                SelectComboItemByTag(combo, _modalEnhancementAdapterId);
            }
            foreach (ComboBox combo in new[]
                     {
                         AppUpscaleScaleComboBox,
                         ModalUpscaleScaleComboBox,
                     })
            {
                SetUpscaleScaleItemAvailability(combo);
                SelectComboItemByTag(
                    combo,
                    _modalEnhancementScale.ToString(
                        "0.#",
                        CultureInfo.InvariantCulture));
            }
            foreach (ComboBox combo in new[]
                     {
                         AppUpscaleFormatComboBox,
                         ModalUpscaleFormatComboBox,
                     })
            {
                SelectComboItemByTag(combo, _upscaleOutputFormat);
            }
        }
        finally
        {
            _syncingUpscaleSettingsControls = false;
        }

        RefreshUpscaleSettingsPresentation();
    }

    private static void SelectComboItemByTag(ComboBox combo, string value)
    {
        ComboBoxItem? match = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                value,
                StringComparison.Ordinal));
        if (match is not null && !ReferenceEquals(combo.SelectedItem, match))
            combo.SelectedItem = match;
    }

    private void SetUpscaleScaleItemAvailability(ComboBox combo)
    {
        bool ncnn = string.Equals(
            _modalEnhancementAdapterId,
            "realesrgan-ncnn",
            StringComparison.Ordinal);
        foreach (ComboBoxItem item in combo.Items.OfType<ComboBoxItem>())
        {
            item.IsEnabled = !ncnn
                || !double.TryParse(
                    item.Tag?.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double scale)
                || scale <= 4d;
        }
    }

    private static string? SelectedComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void AppUpscaleSetting_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyUpscaleSettingSelection(sender, fromAppSettings: true);

    private void ModalUpscaleSetting_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyUpscaleSettingSelection(sender, fromAppSettings: false);

    private void ApplyUpscaleSettingSelection(
        object sender,
        bool fromAppSettings)
    {
        if (_syncingUpscaleSettingsControls || sender is not ComboBox combo)
            return;

        if (ReferenceEquals(combo, AppUpscalePresetComboBox)
            || ReferenceEquals(combo, ModalUpscalePresetComboBox))
        {
            UpscalePresetDefinition preset = PresetForId(SelectedComboTag(combo));
            _modalEnhancementPresetId = preset.Id;
            _modalEnhancementScale = NormalizeUpscaleScale(
                preset.Scale,
                _modalEnhancementAdapterId);
            _upscaleOutputFormat = preset.OutputFormat;
        }
        else if (ReferenceEquals(combo, AppUpscaleAdapterComboBox)
            || ReferenceEquals(combo, ModalUpscaleAdapterComboBox))
        {
            _modalEnhancementAdapterId = NormalizeUpscaleAdapterId(
                SelectedComboTag(combo));
            _modalEnhancementScale = NormalizeUpscaleScale(
                _modalEnhancementScale,
                _modalEnhancementAdapterId);
        }
        else if (ReferenceEquals(combo, AppUpscaleScaleComboBox)
            || ReferenceEquals(combo, ModalUpscaleScaleComboBox))
        {
            string? scaleText = SelectedComboTag(combo);
            double? selectedScale = double.TryParse(
                scaleText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedScale)
                    ? parsedScale
                    : null;
            _modalEnhancementScale = NormalizeUpscaleScale(
                selectedScale,
                _modalEnhancementAdapterId);
        }
        else if (ReferenceEquals(combo, AppUpscaleFormatComboBox)
            || ReferenceEquals(combo, ModalUpscaleFormatComboBox))
        {
            _upscaleOutputFormat = NormalizeUpscaleOutputFormat(
                SelectedComboTag(combo));
        }
        else
        {
            return;
        }

        SyncUpscaleSettingsControls();
        SetUpscaleSettingsStatus(fromAppSettings
            ? "保存済み。次に追加するAI高画質化ジョブから使います。"
            : "保存済み。次のAI高画質化から使います。");
        if (!_initializing)
            SaveState();
    }

    private void RefreshUpscaleSettingsPresentation()
    {
        UpscalePresetDefinition preset = PresetForId(_modalEnhancementPresetId);
        string method = string.Equals(
            _modalEnhancementAdapterId,
            "comfyui",
            StringComparison.Ordinal)
                ? "ComfyUI quality (overlap blend)"
                : "Real-ESRGAN fast (tile seams possible)";
        string scale = _modalEnhancementScale.ToString(
            "0.#",
            CultureInfo.InvariantCulture);
        string summary =
            $"{preset.Label} / {method} / {scale}x / {_upscaleOutputFormat.ToUpperInvariant()}";
        if (AppUpscaleSettingsHintText is not null)
            AppUpscaleSettingsHintText.Text = summary;
        if (ModalUpscaleSettingsHintText is not null)
            ModalUpscaleSettingsHintText.Text = summary;
    }

    private void SetUpscaleSettingsStatus(string text)
    {
        if (AppUpscaleSettingsStatusText is not null)
            AppUpscaleSettingsStatusText.Text = text;
        if (ModalUpscaleSettingsStatusText is not null)
            ModalUpscaleSettingsStatusText.Text = text;
    }

    private void ResetAppUpscaleSettings_Click(object sender, RoutedEventArgs e)
        => ResetUpscaleSettings();

    private void ResetModalUpscaleSettings_Click(object sender, RoutedEventArgs e)
        => ResetUpscaleSettings();

    private void ResetUpscaleSettings()
    {
        _modalEnhancementPresetId = DefaultUpscalePresetId;
        _modalEnhancementAdapterId = DefaultUpscaleAdapterId;
        _modalEnhancementScale = DefaultUpscaleScale;
        _upscaleOutputFormat = DefaultUpscaleOutputFormat;
        SyncUpscaleSettingsControls();
        SetUpscaleSettingsStatus("既定値へ戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void ToggleModalUpscaleSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ModalUpscaleSettingsPopup is null)
            return;

        SyncUpscaleSettingsControls();
        bool opening = ModalUpscaleSettingsPopup.Visibility != Visibility.Visible;
        if (opening)
        {
            if (ModalPhotorealSettingsPopup is not null)
                ModalPhotorealSettingsPopup.Visibility = Visibility.Collapsed;
            if (ModalVideoGenerationPopup is not null)
                ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
        }
        ModalUpscaleSettingsPopup.Visibility = opening
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (opening)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (ModalUpscaleSettingsPopup.Visibility == Visibility.Visible)
                        Keyboard.Focus(ModalUpscalePresetComboBox);
                }),
                DispatcherPriority.Input);
        }
    }

    private void ModalUpscaleSettingsBackdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ModalUpscaleSettingsPopup.Visibility == Visibility.Visible
            && ReferenceEquals(e.OriginalSource, ModalUpscaleSettingsPopup))
        {
            CloseModalUpscaleSettingsBoard();
            e.Handled = true;
        }
    }

    private void CloseModalUpscaleSettingsBoard()
    {
        ModalUpscaleSettingsPopup.Visibility = Visibility.Collapsed;
        ModalUpscaleSettingsButton?.Focus();
    }

    public void ConfigureUpscaleSettingsForSmoke(
        string presetId,
        string adapterId,
        double scale,
        string outputFormat)
    {
        _modalEnhancementPresetId = NormalizeUpscalePresetId(presetId);
        _modalEnhancementAdapterId = NormalizeUpscaleAdapterId(adapterId);
        _modalEnhancementScale = NormalizeUpscaleScale(
            scale,
            _modalEnhancementAdapterId);
        _upscaleOutputFormat = NormalizeUpscaleOutputFormat(outputFormat);
        SyncUpscaleSettingsControls();
        if (!_initializing)
            SaveState();
    }

    public void RestoreUpscaleSettingsForSmoke(ViewerState state)
        => RestoreUpscaleSettings(state);

    public (string PresetId, string AdapterId, double Scale, string OutputFormat)
        UpscaleSettingsForSmoke => (
            _modalEnhancementPresetId,
            _modalEnhancementAdapterId,
            _modalEnhancementScale,
            _upscaleOutputFormat);

    public bool UpscaleSettingsSurfaceContractForSmoke
        => SettingsUpscaleNav is not null
            && string.Equals(
                SettingsUpscaleNav.Tag?.ToString(),
                "upscale",
                StringComparison.Ordinal)
            && ModalUpscaleSettingsButton is not null
            && ModalUpscaleSettingsPopup is not null
            && AutomationProperties.GetName(ModalUpscaleSettingsButton)
                == "Open AI upscale settings"
            && AppUpscalePresetComboBox.Items.Count
                == UpscalePresetDefinitions.Length
            && ModalUpscalePresetComboBox.Items.Count
                == UpscalePresetDefinitions.Length;

    public bool OpenModalUpscaleSettingsForSmoke()
    {
        if (ModalUpscaleSettingsPopup.Visibility != Visibility.Visible)
            ToggleModalUpscaleSettings_Click(this, new RoutedEventArgs());
        return ModalUpscaleSettingsPopup.Visibility == Visibility.Visible;
    }

    public bool ModalUpscaleSettingsVisibleForSmoke
        => ModalUpscaleSettingsPopup.Visibility == Visibility.Visible;
}

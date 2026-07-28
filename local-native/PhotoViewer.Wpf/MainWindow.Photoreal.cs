using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const double DefaultPhotorealStrength = 0.68;
    private const double DefaultPhotorealStructureStrength = 0.4;
    private const int DefaultPhotorealSteps = 30;
    private const int DefaultPhotorealMaxDimension = 768;

    private string _modalEnhancementOperation = "upscale";
    private double _modalPhotorealStrength = DefaultPhotorealStrength;
    private double _modalPhotorealStructureStrength = DefaultPhotorealStructureStrength;
    private int _modalPhotorealSteps = DefaultPhotorealSteps;
    private int _modalPhotorealMaxDimension = DefaultPhotorealMaxDimension;
    private bool _syncingModalPhotorealSettings;

    private sealed record ModalPhotorealRequestSettings(
        double Strength,
        double StructureStrength,
        int Steps,
        double CfgScale,
        int MaxDimension);

    private ModalPhotorealRequestSettings CurrentModalPhotorealRequestSettings()
        => new(
            _modalPhotorealStrength,
            _modalPhotorealStructureStrength,
            _modalPhotorealSteps,
            7,
            _modalPhotorealMaxDimension);

    private async void StartModalPhotoreal_Click(object sender, RoutedEventArgs e)
        => await StartModalEnhancementOperationAsync("photoreal");

    private void ToggleModalPhotorealSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ModalPhotorealSettingsPopup is null)
            return;

        SyncModalPhotorealSettingsControls();
        ModalPhotorealSettingsPopup.IsOpen = !ModalPhotorealSettingsPopup.IsOpen;
    }

    private void ModalPhotorealSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingModalPhotorealSettings
            || ModalPhotorealStrengthSlider is null
            || ModalPhotorealStructureSlider is null)
        {
            return;
        }

        _modalPhotorealStrength = Math.Clamp(ModalPhotorealStrengthSlider.Value / 100d, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(ModalPhotorealStructureSlider.Value / 100d, 0, 1.2);
        RefreshModalPhotorealSettingLabels();
        if (!_initializing)
            SaveState();
    }

    private void ModalPhotorealSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings
            || ModalPhotorealStepsComboBox is null
            || ModalPhotorealSizeComboBox is null)
        {
            return;
        }

        _modalPhotorealSteps = SelectedIntegerTag(
            ModalPhotorealStepsComboBox,
            DefaultPhotorealSteps,
            [20, 24, 30, 36]);
        _modalPhotorealMaxDimension = SelectedIntegerTag(
            ModalPhotorealSizeComboBox,
            DefaultPhotorealMaxDimension,
            [512, 640, 768]);
        if (!_initializing)
            SaveState();
    }

    private static int SelectedIntegerTag(ComboBox comboBox, int fallback, IReadOnlyCollection<int> supported)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: object tag }
            && int.TryParse(Convert.ToString(tag, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && supported.Contains(parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static void SelectIntegerTag(ComboBox comboBox, int value)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem { Tag: object tag }
                && int.TryParse(Convert.ToString(tag, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed == value)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void RestoreModalPhotorealSettings(
        double? strength,
        double? structureStrength,
        int? steps,
        int? maxDimension)
    {
        _modalPhotorealStrength = Math.Clamp(strength ?? DefaultPhotorealStrength, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(
            structureStrength ?? DefaultPhotorealStructureStrength,
            0,
            1.2);
        _modalPhotorealSteps = steps is 20 or 24 or 30 or 36
            ? steps.Value
            : DefaultPhotorealSteps;
        _modalPhotorealMaxDimension = maxDimension is 512 or 640 or 768
            ? maxDimension.Value
            : DefaultPhotorealMaxDimension;
        SyncModalPhotorealSettingsControls();
    }

    private void SyncModalPhotorealSettingsControls()
    {
        if (ModalPhotorealStrengthSlider is null
            || ModalPhotorealStructureSlider is null
            || ModalPhotorealStepsComboBox is null
            || ModalPhotorealSizeComboBox is null)
        {
            return;
        }

        _syncingModalPhotorealSettings = true;
        try
        {
            ModalPhotorealStrengthSlider.Value = _modalPhotorealStrength * 100;
            ModalPhotorealStructureSlider.Value = _modalPhotorealStructureStrength * 100;
            SelectIntegerTag(ModalPhotorealStepsComboBox, _modalPhotorealSteps);
            SelectIntegerTag(ModalPhotorealSizeComboBox, _modalPhotorealMaxDimension);
            RefreshModalPhotorealSettingLabels();
        }
        finally
        {
            _syncingModalPhotorealSettings = false;
        }
    }

    private void RefreshModalPhotorealSettingLabels()
    {
        if (ModalPhotorealStrengthValue is not null)
            ModalPhotorealStrengthValue.Text = $"{Math.Round(_modalPhotorealStrength * 100):0}%";
        if (ModalPhotorealStructureValue is not null)
            ModalPhotorealStructureValue.Text = $"{Math.Round(_modalPhotorealStructureStrength * 100):0}%";
    }

    public (double Strength, double StructureStrength, int Steps, int MaxDimension) ModalPhotorealSettingsForSmoke
        => (
            _modalPhotorealStrength,
            _modalPhotorealStructureStrength,
            _modalPhotorealSteps,
            _modalPhotorealMaxDimension);

    public string ModalEnhancementOperationForSmoke => _modalEnhancementOperation;

    public bool ModalPhotorealToolbarContractForSmoke
        => !ReferenceEquals(ModalEnhanceButton, ModalPhotorealButton)
            && string.Equals(ModalEnhanceButtonLabel.Text, "AI高画質化", StringComparison.Ordinal)
            && string.Equals(ModalPhotorealButtonLabel.Text, "AI実写化", StringComparison.Ordinal)
            && AutomationProperties.GetName(ModalPhotorealButton) == "AI実写化"
            && ModalPhotorealSettingsPopup is not null;

    public void ConfigureModalPhotorealSettingsForSmoke(
        double strength,
        double structureStrength,
        int steps,
        int maxDimension)
        => RestoreModalPhotorealSettings(strength, structureStrength, steps, maxDimension);

    public async Task<bool> StartModalPhotorealForSmokeAsync(int timeoutMilliseconds = 3000)
    {
        StartModalPhotoreal_Click(this, new RoutedEventArgs());
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(timeoutMilliseconds);
        return completed && !string.IsNullOrWhiteSpace(_modalEnhancementJobId);
    }
}

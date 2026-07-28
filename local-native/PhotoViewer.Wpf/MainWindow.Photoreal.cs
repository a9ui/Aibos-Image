using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const double DefaultPhotorealStrength = 0.8;
    private const double DefaultPhotorealStructureStrength = 1.0;
    private const int DefaultPhotorealSteps = 8;
    private const int DefaultPhotorealMaxDimension = 1280;

    private string _modalEnhancementOperation = "upscale";
    private readonly List<ManagedEnhancementVersion> _modalEnhancementVersions = [];
    private int _modalEnhancementVersionIndex;
    private string? _modalEnhancementVersionsSourcePath;
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

    private void InitializeModalEnhancementVersions(Tile tile)
    {
        _modalEnhancementVersions.Clear();
        _modalEnhancementVersionsSourcePath = tile.Path;
        foreach (ManagedEnhancementVersion candidate in GetManagedEnhancementVersionsForPath(tile.Path))
        {
            if (TryCreateManagedEnhancedOutput(
                    tile,
                    candidate.Output.OutputPath,
                    candidate.Output.SourceSize,
                    candidate.Output.SourceMtimeMs,
                    out ManagedEnhancedOutput current)
                && !_modalEnhancementVersions.Any(version =>
                    string.Equals(
                        version.Output.OutputPath,
                        current.OutputPath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                _modalEnhancementVersions.Add(candidate with { Output = current });
            }
        }

        if (_modalEnhancementVersions.Count == 0
            && TryGetManagedEnhancedOutputForPath(tile.Path, out ManagedEnhancedOutput fallback)
            && TryCreateManagedEnhancedOutput(
                tile,
                fallback.OutputPath,
                fallback.SourceSize,
                fallback.SourceMtimeMs,
                out ManagedEnhancedOutput currentFallback))
        {
            _modalEnhancementVersions.Add(
                new ManagedEnhancementVersion("", "upscale", currentFallback));
        }

        _modalEnhancementVersionIndex = _modalEnhancementVersions.Count > 0 ? 1 : 0;
        _modalShowingEnhanced = _modalEnhancementVersionIndex > 0;
    }

    private void ClearModalEnhancementVersions()
    {
        _modalEnhancementVersions.Clear();
        _modalEnhancementVersionIndex = 0;
        _modalEnhancementVersionsSourcePath = null;
    }

    private bool TryGetCurrentModalEnhancementVersion(
        Tile tile,
        out ManagedEnhancementVersion version)
    {
        version = null!;
        if (!_modalShowingEnhanced
            || !string.Equals(
                _modalEnhancementVersionsSourcePath,
                tile.Path,
                StringComparison.OrdinalIgnoreCase)
            || _modalEnhancementVersionIndex < 1
            || _modalEnhancementVersionIndex > _modalEnhancementVersions.Count)
        {
            return false;
        }

        ManagedEnhancementVersion candidate =
            _modalEnhancementVersions[_modalEnhancementVersionIndex - 1];
        if (!TryCreateManagedEnhancedOutput(
                tile,
                candidate.Output.OutputPath,
                candidate.Output.SourceSize,
                candidate.Output.SourceMtimeMs,
                out ManagedEnhancedOutput current))
        {
            return false;
        }

        version = candidate with { Output = current };
        return true;
    }

    private bool TryGetPreferredModalEnhancementVersion(
        Tile tile,
        out ManagedEnhancementVersion version)
    {
        if (TryGetCurrentModalEnhancementVersion(tile, out version))
            return true;

        version = null!;
        if (!string.Equals(
                _modalEnhancementVersionsSourcePath,
                tile.Path,
                StringComparison.OrdinalIgnoreCase)
            || _modalEnhancementVersions.Count == 0)
        {
            return false;
        }

        ManagedEnhancementVersion candidate = _modalEnhancementVersions[0];
        if (!TryCreateManagedEnhancedOutput(
                tile,
                candidate.Output.OutputPath,
                candidate.Output.SourceSize,
                candidate.Output.SourceMtimeMs,
                out ManagedEnhancedOutput current))
        {
            return false;
        }

        version = candidate with { Output = current };
        return true;
    }

    private void ApplyModalEnhancementVersions(
        Tile tile,
        IReadOnlyList<ModalEnhancementJobSnapshot> jobs)
    {
        if (!TryResolveEnhancementSourceIdentity(tile.Path, out string sourceIdentity))
            return;

        string? selectedPath = TryGetCurrentModalEnhancementVersion(tile, out ManagedEnhancementVersion selected)
            ? selected.Output.OutputPath
            : null;
        bool keepEnhancedSelected = _modalShowingEnhanced;
        var next = new List<ManagedEnhancementVersion>();
        foreach (ModalEnhancementJobSnapshot job in jobs)
        {
            if (job is not
                {
                    Status: "succeeded",
                    OutputPath: not null,
                    SourceSize: not null,
                    SourceMtimeMs: not null,
                }
                || !TryResolveEnhancementSourceIdentity(
                    job.SourcePath,
                    out string jobSourcePathIdentity)
                || !TryResolveEnhancementSourceIdentity(
                    job.SourceId,
                    out string jobSourceIdIdentity)
                || !EnhancementSourceIdentityComparer.Equals(
                    jobSourcePathIdentity,
                    sourceIdentity)
                || !EnhancementSourceIdentityComparer.Equals(
                    jobSourceIdIdentity,
                    sourceIdentity)
                || !TryCreateManagedEnhancedOutput(
                    tile,
                    job.OutputPath,
                    job.SourceSize.Value,
                    job.SourceMtimeMs.Value,
                    out ManagedEnhancedOutput output)
                || next.Any(candidate =>
                    string.Equals(
                        candidate.Output.OutputPath,
                        output.OutputPath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            next.Add(new ManagedEnhancementVersion(job.Id, job.Operation, output));
        }

        _modalEnhancementVersions.Clear();
        _modalEnhancementVersions.AddRange(next);
        _modalEnhancementVersionsSourcePath = tile.Path;
        if (_modalEnhancementVersions.Count == 0)
        {
            _modalEnhancementVersionIndex = 0;
            _modalShowingEnhanced = false;
            tile.Enhanced = false;
            tile.EnhancedOutputPath = null;
            _enhancedOutputs.Remove(sourceIdentity);
            _enhancementVersions.Remove(sourceIdentity);
            string? emptyAlias = NormalizeCatalogEnhancementPath(tile.Path);
            if (emptyAlias is not null)
            {
                _catalogEnhancedOutputsByPath.Remove(emptyAlias);
                _catalogEnhancementVersionsByPath.Remove(emptyAlias);
            }
        }
        else
        {
            int preservedIndex = selectedPath is null
                ? -1
                : _modalEnhancementVersions.FindIndex(candidate =>
                    string.Equals(
                        candidate.Output.OutputPath,
                        selectedPath,
                        StringComparison.OrdinalIgnoreCase));
            _modalEnhancementVersionIndex = preservedIndex >= 0
                ? preservedIndex + 1
                : keepEnhancedSelected ? 1 : 0;
            _modalShowingEnhanced = _modalEnhancementVersionIndex > 0;
            ManagedEnhancedOutput latest = _modalEnhancementVersions[0].Output;
            tile.Enhanced = true;
            tile.EnhancedOutputPath = latest.OutputPath;
            _enhancedOutputs[sourceIdentity] = latest;
            _enhancementVersions[sourceIdentity] = [.. _modalEnhancementVersions];
            string? alias = NormalizeCatalogEnhancementPath(tile.Path);
            if (alias is not null)
            {
                _catalogEnhancedOutputsByPath[alias] = latest;
                _catalogEnhancementVersionsByPath[alias] =
                    _enhancementVersions[sourceIdentity];
            }
        }
        UpdateModalEnhancedControls(_modalEnhancementVersions.Count > 0);
    }

    private void UpsertModalEnhancementVersion(
        Tile tile,
        ModalEnhancementJobSnapshot job,
        ManagedEnhancedOutput output)
    {
        if (!string.Equals(
                _modalEnhancementVersionsSourcePath,
                tile.Path,
                StringComparison.OrdinalIgnoreCase))
        {
            _modalEnhancementVersions.Clear();
            _modalEnhancementVersionsSourcePath = tile.Path;
        }

        _modalEnhancementVersions.RemoveAll(candidate =>
            string.Equals(
                candidate.Output.OutputPath,
                output.OutputPath,
                StringComparison.OrdinalIgnoreCase));
        _modalEnhancementVersions.Insert(
            0,
            new ManagedEnhancementVersion(job.Id, job.Operation, output));
        _modalEnhancementVersionIndex = 1;
        _modalShowingEnhanced = true;

        if (TryResolveEnhancementSourceIdentity(tile.Path, out string sourceIdentity))
        {
            _enhancedOutputs[sourceIdentity] = output;
            _enhancementVersions[sourceIdentity] = [.. _modalEnhancementVersions];
            string? alias = NormalizeCatalogEnhancementPath(tile.Path);
            if (alias is not null)
            {
                _catalogEnhancedOutputsByPath[alias] = output;
                _catalogEnhancementVersionsByPath[alias] =
                    _enhancementVersions[sourceIdentity];
            }
        }
    }

    private void RemoveModalEnhancementVersion(Tile tile, string jobId)
    {
        _modalEnhancementVersions.RemoveAll(candidate =>
            string.Equals(candidate.JobId, jobId, StringComparison.Ordinal));
        _modalEnhancementVersionIndex = 0;
        _modalShowingEnhanced = false;
        if (!TryResolveEnhancementSourceIdentity(tile.Path, out string sourceIdentity))
            return;

        if (_modalEnhancementVersions.Count == 0)
        {
            tile.Enhanced = false;
            tile.EnhancedOutputPath = null;
            _enhancedOutputs.Remove(sourceIdentity);
            _enhancementVersions.Remove(sourceIdentity);
            string? emptyAlias = NormalizeCatalogEnhancementPath(tile.Path);
            if (emptyAlias is not null)
            {
                _catalogEnhancedOutputsByPath.Remove(emptyAlias);
                _catalogEnhancementVersionsByPath.Remove(emptyAlias);
            }
            return;
        }

        ManagedEnhancedOutput latest = _modalEnhancementVersions[0].Output;
        tile.Enhanced = true;
        tile.EnhancedOutputPath = latest.OutputPath;
        _enhancedOutputs[sourceIdentity] = latest;
        _enhancementVersions[sourceIdentity] = [.. _modalEnhancementVersions];
        string? alias = NormalizeCatalogEnhancementPath(tile.Path);
        if (alias is not null)
        {
            _catalogEnhancedOutputsByPath[alias] = latest;
            _catalogEnhancementVersionsByPath[alias] =
                _enhancementVersions[sourceIdentity];
        }
    }

    private bool CycleModalEnhancementVersion(int delta)
    {
        if (Modal.Visibility != Visibility.Visible
            || SelectedTile() is not Tile tile
            || _modalEnhancementVersions.Count == 0)
        {
            return false;
        }

        int total = _modalEnhancementVersions.Count + 1;
        int current = _modalShowingEnhanced
            ? Math.Clamp(_modalEnhancementVersionIndex, 1, total - 1)
            : 0;
        int next = (current + delta) % total;
        if (next < 0)
            next += total;
        _modalEnhancementVersionIndex = next;
        _modalShowingEnhanced = next > 0;
        OpenModal();
        ShowModalInteractionFeedback(CurrentModalEnhancementVersionLabel());
        return true;
    }

    private string CurrentModalEnhancementVersionLabel()
    {
        if (!_modalShowingEnhanced
            || _modalEnhancementVersionIndex < 1
            || _modalEnhancementVersionIndex > _modalEnhancementVersions.Count)
        {
            return "Original";
        }

        ManagedEnhancementVersion version =
            _modalEnhancementVersions[_modalEnhancementVersionIndex - 1];
        string operation = version.Operation == "photoreal"
            ? "実写"
            : "高画質";
        return _modalEnhancementVersions.Count == 1
            ? version.Operation == "photoreal" ? "Photoreal" : "Enhanced"
            : $"{operation} {_modalEnhancementVersionIndex}/{_modalEnhancementVersions.Count}";
    }

    private string? CurrentModalEnhancementVersionJobId()
        => _modalShowingEnhanced
            && _modalEnhancementVersionIndex >= 1
            && _modalEnhancementVersionIndex <= _modalEnhancementVersions.Count
            ? _modalEnhancementVersions[_modalEnhancementVersionIndex - 1].JobId
            : null;

    private bool CurrentModalEnhancementVersionIsPhotoreal()
        => _modalShowingEnhanced
            && _modalEnhancementVersionIndex >= 1
            && _modalEnhancementVersionIndex <= _modalEnhancementVersions.Count
            && _modalEnhancementVersions[_modalEnhancementVersionIndex - 1].Operation == "photoreal";

    private ModalPhotorealRequestSettings CurrentModalPhotorealRequestSettings()
        => new(
            _modalPhotorealStrength,
            _modalPhotorealStructureStrength,
            _modalPhotorealSteps,
            1,
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
            [4, 6, 8]);
        _modalPhotorealMaxDimension = SelectedIntegerTag(
            ModalPhotorealSizeComboBox,
            DefaultPhotorealMaxDimension,
            [768, 1024, 1280]);
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
        _modalPhotorealSteps = steps is 4 or 6 or 8
            ? steps.Value
            : DefaultPhotorealSteps;
        _modalPhotorealMaxDimension = maxDimension is 768 or 1024 or 1280
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

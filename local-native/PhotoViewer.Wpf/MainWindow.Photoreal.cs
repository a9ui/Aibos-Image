using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private const int MaxPhotorealStyleCount = 32;
    private const int MaxPhotorealStyleNameLength = 40;
    private const int MaxPhotorealPromptLength = 2_000;
    private const string DefaultPhotorealPrompt =
        "Turn the input into a real camera photograph of the same adult Japanese woman. " +
        "Preserve her identity, Japanese and East Asian facial proportions, and the exact expression and emotion in the source, especially her eyebrows, eyes, eyelids, mouth, gaze, tears, blush, and tension. Do not add a smile unless the source is smiling. " +
        "Preserve her hair, body, pose, hands, crop, clothing, accessories, background, lighting, atmosphere, occlusions, and visible adult anatomy. Keep blindfolded eyes hidden and correct visible hands to five natural fingers. " +
        "Use natural skin, hair, materials, soft low-contrast exposure, and photographic optics. Do not westernize, censor, change her mood, reveal hidden features, alter garments, use HDR, or produce anime, CGI, or doll imagery.";

    private string _modalEnhancementOperation = "upscale";
    private readonly List<ManagedEnhancementVersion> _modalEnhancementVersions = [];
    private int _modalEnhancementVersionIndex;
    private string? _modalEnhancementVersionsSourcePath;
    private double _modalPhotorealStrength = DefaultPhotorealStrength;
    private double _modalPhotorealStructureStrength = DefaultPhotorealStructureStrength;
    private int _modalPhotorealSteps = DefaultPhotorealSteps;
    private int _modalPhotorealMaxDimension = DefaultPhotorealMaxDimension;
    private string _modalPhotorealPrompt = DefaultPhotorealPrompt;
    private bool _syncingModalPhotorealSettings;
    private readonly List<PhotorealStyleState> _photorealStyles = [];
    private string? _selectedPhotorealStyleName;

    private sealed record PhotorealStyleChoice(string Label, string? StyleName);

    private sealed record ModalPhotorealRequestSettings(
        double Strength,
        double StructureStrength,
        int Steps,
        double CfgScale,
        int MaxDimension,
        string Prompt);

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
            ApplyTileEnhancementAvailability(tile, _modalEnhancementVersions);
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
            ApplyTileEnhancementAvailability(tile, _modalEnhancementVersions);
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
        ApplyTileEnhancementAvailability(tile, _modalEnhancementVersions);

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
            ApplyTileEnhancementAvailability(tile, _modalEnhancementVersions);
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
        ApplyTileEnhancementAvailability(tile, _modalEnhancementVersions);
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
            _modalPhotorealMaxDimension,
            _modalPhotorealPrompt.Trim());

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
        MarkPhotorealStyleAsCustom();
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
            [4, 6, 8, 12]);
        _modalPhotorealMaxDimension = SelectedIntegerTag(
            ModalPhotorealSizeComboBox,
            DefaultPhotorealMaxDimension,
            [768, 1024, 1280]);
        MarkPhotorealStyleAsCustom();
        if (!_initializing)
            SaveState();
    }

    private void ModalPhotorealPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || ModalPhotorealPromptTextBox is null)
            return;

        _modalPhotorealPrompt = ModalPhotorealPromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncAppPhotorealPromptControl();
        if (!_initializing)
            SaveState();
    }

    private void ResetModalPhotorealPrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealPrompt();

    private void AppPhotorealPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || AppPhotorealPromptTextBox is null)
            return;

        _modalPhotorealPrompt = AppPhotorealPromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        AppPhotorealPromptStatusText.Text = "保存済み。拡大画面のAI実写化設定と共通です。";
        if (!_initializing)
            SaveState();
    }

    private void ResetAppPhotorealPrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealPrompt();

    private void PhotorealStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings)
            return;

        PhotorealStyleChoice? choice = sender switch
        {
            ComboBox comboBox => comboBox.SelectedItem as PhotorealStyleChoice,
            ListBox listBox => listBox.SelectedItem as PhotorealStyleChoice,
            _ => null,
        };
        if (choice is null)
            return;

        if (choice.StyleName is null)
        {
            _selectedPhotorealStyleName = null;
            RefreshPhotorealStyleControls(updateNameFields: false);
            SetPhotorealStyleStatus("現在の設定を使用します。Styleにはまだ保存されていません。");
            if (!_initializing)
                SaveState();
            return;
        }

        PhotorealStyleState? style = FindPhotorealStyle(choice.StyleName);
        if (style is null)
            return;

        _selectedPhotorealStyleName = style.Name;
        RestoreModalPhotorealSettings(
            style.Strength,
            style.StructureStrength,
            style.Steps,
            style.MaxDimension,
            style.Prompt);
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealStyleStatus($"「{style.Name}」を反映しました。次に追加するAI実写化ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void SavePhotorealStyle_Click(object sender, RoutedEventArgs e)
    {
        TextBox nameTextBox = ReferenceEquals(sender, SaveModalPhotorealStyleButton)
            ? ModalPhotorealStyleNameTextBox
            : AppPhotorealStyleNameTextBox;
        string name = nameTextBox.Text.Trim();
        if (!IsValidPhotorealStyleName(name))
        {
            SetPhotorealStyleStatus($"Style名は1～{MaxPhotorealStyleNameLength}文字で入力してください。制御文字は使えません。");
            return;
        }

        PhotorealStyleState style = CreateCurrentPhotorealStyle(name);
        int existingIndex = _photorealStyles.FindIndex(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _photorealStyles[existingIndex] = style;
        }
        else
        {
            if (_photorealStyles.Count >= MaxPhotorealStyleCount)
            {
                SetPhotorealStyleStatus($"Styleは最大{MaxPhotorealStyleCount}件です。不要なStyleを削除してください。");
                return;
            }
            _photorealStyles.Add(style);
        }

        _photorealStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        _selectedPhotorealStyleName = style.Name;
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealStyleStatus(
            existingIndex >= 0
                ? $"「{style.Name}」を現在の設定で上書きしました。"
                : $"「{style.Name}」を保存しました。");
        if (!_initializing)
            SaveState();
    }

    private void DeletePhotorealStyle_Click(object sender, RoutedEventArgs e)
    {
        PhotorealStyleState? style = FindPhotorealStyle(_selectedPhotorealStyleName);
        if (style is null)
        {
            SetPhotorealStyleStatus("削除する保存済みStyleを選んでください。");
            return;
        }

        _photorealStyles.Remove(style);
        _selectedPhotorealStyleName = null;
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealStyleStatus($"「{style.Name}」を削除しました。現在の設定値はそのまま残ります。");
        if (!_initializing)
            SaveState();
    }

    private void RestorePhotorealStyles(
        IEnumerable<PhotorealStyleState>? styles,
        string? selectedStyleName)
    {
        _photorealStyles.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotorealStyleState? candidate in styles ?? [])
        {
            PhotorealStyleState? normalized = NormalizePhotorealStyle(candidate);
            if (normalized is null || !names.Add(normalized.Name))
                continue;

            _photorealStyles.Add(normalized);
            if (_photorealStyles.Count >= MaxPhotorealStyleCount)
                break;
        }
        _photorealStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));

        PhotorealStyleState? selected = FindPhotorealStyle(selectedStyleName);
        _selectedPhotorealStyleName = selected is not null && PhotorealStyleMatchesCurrent(selected)
            ? selected.Name
            : null;
        RefreshPhotorealStyleControls(updateNameFields: true);
    }

    private static PhotorealStyleState? NormalizePhotorealStyle(PhotorealStyleState? candidate)
    {
        if (candidate is null)
            return null;

        string name = candidate.Name?.Trim() ?? "";
        if (!IsValidPhotorealStyleName(name)
            || !double.IsFinite(candidate.Strength)
            || !double.IsFinite(candidate.StructureStrength)
            || candidate.Steps is not (4 or 6 or 8 or 12)
            || candidate.MaxDimension is not (768 or 1024 or 1280))
        {
            return null;
        }

        string prompt = candidate.Prompt ?? "";
        if (prompt.Length > MaxPhotorealPromptLength)
            prompt = prompt[..MaxPhotorealPromptLength];
        return new PhotorealStyleState
        {
            Name = name,
            Strength = Math.Clamp(candidate.Strength, 0.2, 0.8),
            StructureStrength = Math.Clamp(candidate.StructureStrength, 0, 1.2),
            Steps = candidate.Steps,
            MaxDimension = candidate.MaxDimension,
            Prompt = prompt,
        };
    }

    private static bool IsValidPhotorealStyleName(string name)
        => name.Length is >= 1 and <= MaxPhotorealStyleNameLength
            && !name.Any(char.IsControl);

    private PhotorealStyleState CreateCurrentPhotorealStyle(string name)
        => new()
        {
            Name = name,
            Strength = _modalPhotorealStrength,
            StructureStrength = _modalPhotorealStructureStrength,
            Steps = _modalPhotorealSteps,
            MaxDimension = _modalPhotorealMaxDimension,
            Prompt = _modalPhotorealPrompt,
        };

    private PhotorealStyleState? FindPhotorealStyle(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _photorealStyles.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool PhotorealStyleMatchesCurrent(PhotorealStyleState style)
        => Math.Abs(style.Strength - _modalPhotorealStrength) < 0.0001
            && Math.Abs(style.StructureStrength - _modalPhotorealStructureStrength) < 0.0001
            && style.Steps == _modalPhotorealSteps
            && style.MaxDimension == _modalPhotorealMaxDimension
            && string.Equals(style.Prompt, _modalPhotorealPrompt, StringComparison.Ordinal);

    private void MarkPhotorealStyleAsCustom()
    {
        if (_syncingModalPhotorealSettings)
            return;

        bool selectionChanged = _selectedPhotorealStyleName is not null;
        _selectedPhotorealStyleName = null;
        if (selectionChanged)
        {
            RefreshPhotorealStyleControls(updateNameFields: false);
            SetPhotorealStyleStatus("設定を変更しました。保存済みStyleは上書きされていません。");
        }
        else
        {
            RefreshPhotorealStyleSummary();
        }
    }

    private void RefreshPhotorealStyleControls(bool updateNameFields)
    {
        if (ModalPhotorealStyleComboBox is null
            || AppPhotorealStyleListBox is null
            || ModalPhotorealStyleNameTextBox is null
            || AppPhotorealStyleNameTextBox is null
            || DeleteModalPhotorealStyleButton is null
            || DeleteAppPhotorealStyleButton is null)
        {
            return;
        }

        var choices = new List<PhotorealStyleChoice>
        {
            new("カスタム（現在の設定）", null),
        };
        choices.AddRange(_photorealStyles.Select(static style =>
            new PhotorealStyleChoice(style.Name, style.Name)));
        PhotorealStyleChoice selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.StyleName, _selectedPhotorealStyleName, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        bool wasSyncing = _syncingModalPhotorealSettings;
        _syncingModalPhotorealSettings = true;
        try
        {
            ModalPhotorealStyleComboBox.ItemsSource = choices;
            AppPhotorealStyleListBox.ItemsSource = choices;
            ModalPhotorealStyleComboBox.SelectedItem = selectedChoice;
            AppPhotorealStyleListBox.SelectedItem = selectedChoice;
            bool canDelete = selectedChoice.StyleName is not null;
            DeleteModalPhotorealStyleButton.IsEnabled = canDelete;
            DeleteAppPhotorealStyleButton.IsEnabled = canDelete;
            if (updateNameFields)
            {
                string name = selectedChoice.StyleName ?? "";
                ModalPhotorealStyleNameTextBox.Text = name;
                AppPhotorealStyleNameTextBox.Text = name;
            }
            RefreshPhotorealStyleSummary();
        }
        finally
        {
            _syncingModalPhotorealSettings = wasSyncing;
        }
    }

    private void RefreshPhotorealStyleSummary()
    {
        if (AppPhotorealStyleSummaryText is null)
            return;

        AppPhotorealStyleSummaryText.Text =
            $"現在: {Math.Round(_modalPhotorealStrength * 100):0}% / 構図 {Math.Round(_modalPhotorealStructureStrength * 100):0}% / {_modalPhotorealSteps} step / {_modalPhotorealMaxDimension} px";
    }

    private void SetPhotorealStyleStatus(string message)
    {
        if (ModalPhotorealStyleStatusText is not null)
            ModalPhotorealStyleStatusText.Text = message;
        if (AppPhotorealStyleStatusText is not null)
            AppPhotorealStyleStatusText.Text = message;
    }

    private List<PhotorealStyleState>? SnapshotPhotorealStyles()
        => _photorealStyles.Count == 0
            ? null
            : _photorealStyles.Select(static style => new PhotorealStyleState
            {
                Name = style.Name,
                Strength = style.Strength,
                StructureStrength = style.StructureStrength,
                Steps = style.Steps,
                MaxDimension = style.MaxDimension,
                Prompt = style.Prompt,
            }).ToList();

    private void RefreshEnhancementOutputRootSettings()
    {
        if (EnhancementOutputRootTextBox is null
            || ChangeEnhancementOutputRootButton is null
            || OpenEnhancementOutputRootButton is null
            || EnhancementOutputRootStatusText is null)
        {
            return;
        }

        try
        {
            string root = ResolvedManagedEnhancementOutputsRoot;
            EnhancementOutputRootTextBox.Text = root;
            EnhancementOutputRootTextBox.ToolTip = root;
            bool environmentOverride =
                SharedDataRootActivation.TryGetManagedOutputsRootEnvironmentOverride(
                    out _,
                    out string? environmentVariable);
            ChangeEnhancementOutputRootButton.IsEnabled = !environmentOverride;
            OpenEnhancementOutputRootButton.IsEnabled = Directory.Exists(root);
            EnhancementOutputRootStatusText.Text = environmentOverride
                ? $"{environmentVariable} が優先されています。アプリから変更するには環境変数を解除してください。"
                : Directory.Exists(root)
                    ? "現在の出力先です。変更後は、次に処理を開始する待機ジョブから使われます。"
                    : "設定先が見つかりません。既存のフォルダを選び直してください。";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            EnhancementOutputRootTextBox.Text = "";
            EnhancementOutputRootTextBox.ToolTip = null;
            ChangeEnhancementOutputRootButton.IsEnabled = false;
            OpenEnhancementOutputRootButton.IsEnabled = false;
            EnhancementOutputRootStatusText.Text =
                $"出力先設定を読み込めませんでした（{ex.GetType().Name}）。";
        }
    }

    private void ChangeEnhancementOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        RefreshEnhancementOutputRootSettings();
        if (!ChangeEnhancementOutputRootButton.IsEnabled)
            return;

        string currentRoot = EnhancementOutputRootTextBox.Text;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "AI高画質化・AI実写化の出力先を選択",
            Multiselect = false,
        };
        if (Directory.Exists(currentRoot))
            dialog.InitialDirectory = currentRoot;
        if (dialog.ShowDialog(this) != true)
            return;

        if (!SharedDataRootActivation.TryWriteManagedOutputsRoot(
                ResolvedEnhancementJobsPath,
                dialog.FolderName,
                out string normalizedRoot,
                out string? error))
        {
            EnhancementOutputRootStatusText.Text =
                error ?? "出力先を変更できませんでした。";
            return;
        }

        RefreshEnhancementOutputRootSettings();
        EnhancementOutputRootStatusText.Text =
            $"{normalizedRoot} に変更しました。次に処理を開始する待機ジョブから使われます。";
    }

    private void OpenEnhancementOutputRoot_Click(object sender, RoutedEventArgs e)
        => TryOpenEnhancementOutputRoot();

    private bool TryOpenEnhancementOutputRoot()
    {
        RefreshEnhancementOutputRootSettings();
        string root = EnhancementOutputRootTextBox.Text;
        if (!Directory.Exists(root))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(root);
            if (!_explorerLauncher(startInfo))
            {
                EnhancementOutputRootStatusText.Text =
                    "出力先フォルダを開けませんでした。アクセスを確認してください。";
                return false;
            }

            EnhancementOutputRootStatusText.Text = "出力先フォルダを開きました。";
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            Trace.TraceWarning(
                $"Enhancement output root open failed: {ex.GetType().Name}");
            EnhancementOutputRootStatusText.Text =
                "出力先フォルダを開けませんでした。アクセスを確認してください。";
            return false;
        }
    }

    private void ResetPhotorealPrompt()
    {
        _modalPhotorealPrompt = DefaultPhotorealPrompt;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        if (AppPhotorealPromptStatusText is not null)
            AppPhotorealPromptStatusText.Text = "初期プロンプトに戻しました。";
        if (!_initializing)
            SaveState();
    }

    private void SyncAppPhotorealPromptControl()
    {
        if (AppPhotorealPromptTextBox is null)
            return;

        bool wasSyncing = _syncingModalPhotorealSettings;
        _syncingModalPhotorealSettings = true;
        try
        {
            if (!string.Equals(AppPhotorealPromptTextBox.Text, _modalPhotorealPrompt, StringComparison.Ordinal))
                AppPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
        }
        finally
        {
            _syncingModalPhotorealSettings = wasSyncing;
        }
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
        int? maxDimension,
        string? prompt = null)
    {
        _modalPhotorealStrength = Math.Clamp(strength ?? DefaultPhotorealStrength, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(
            structureStrength ?? DefaultPhotorealStructureStrength,
            0,
            1.2);
        _modalPhotorealSteps = steps is 4 or 6 or 8 or 12
            ? steps.Value
            : DefaultPhotorealSteps;
        _modalPhotorealMaxDimension = maxDimension is 768 or 1024 or 1280
            ? maxDimension.Value
            : DefaultPhotorealMaxDimension;
        string restoredPrompt = prompt ?? DefaultPhotorealPrompt;
        _modalPhotorealPrompt = restoredPrompt.Length <= MaxPhotorealPromptLength
            ? restoredPrompt
            : restoredPrompt[..MaxPhotorealPromptLength];
        SyncModalPhotorealSettingsControls();
    }

    private void SyncModalPhotorealSettingsControls()
    {
        if (ModalPhotorealStrengthSlider is null
            || ModalPhotorealStructureSlider is null
            || ModalPhotorealStepsComboBox is null
            || ModalPhotorealSizeComboBox is null
            || ModalPhotorealPromptTextBox is null)
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
            ModalPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            if (AppPhotorealPromptTextBox is not null)
                AppPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            RefreshModalPhotorealSettingLabels();
            RefreshPhotorealStyleSummary();
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

    public (double Strength, double StructureStrength, int Steps, int MaxDimension, string Prompt) ModalPhotorealSettingsForSmoke
        => (
            _modalPhotorealStrength,
            _modalPhotorealStructureStrength,
            _modalPhotorealSteps,
            _modalPhotorealMaxDimension,
            _modalPhotorealPrompt);

    public string ModalEnhancementOperationForSmoke => _modalEnhancementOperation;

    public bool ModalPhotorealToolbarContractForSmoke
        => !ReferenceEquals(ModalEnhanceButton, ModalPhotorealButton)
            && string.Equals(ModalEnhanceButtonLabel.Text, "AI高画質化", StringComparison.Ordinal)
            && string.Equals(ModalPhotorealButtonLabel.Text, "AI実写化", StringComparison.Ordinal)
            && AutomationProperties.GetName(ModalPhotorealButton) == "AI実写化"
            && ModalPhotorealSettingsPopup is not null
            && GalleryContextMenuContractForSmoke;

    public void ConfigureModalPhotorealSettingsForSmoke(
        double strength,
        double structureStrength,
        int steps,
        int maxDimension,
        string prompt = "")
    {
        RestoreModalPhotorealSettings(strength, structureStrength, steps, maxDimension, prompt);
        MarkPhotorealStyleAsCustom();
    }

    public void ResetModalPhotorealPromptForSmoke()
        => ResetModalPhotorealPrompt_Click(this, new RoutedEventArgs());

    public bool AppPhotorealPromptSurfaceForSmoke
        => AppPhotorealPromptTextBox.MaxLength == 2_000
            && AppPhotorealPromptTextBox.AcceptsReturn
            && string.Equals(
                AutomationProperties.GetName(AppPhotorealPromptTextBox),
                "Default AI photorealization prompt",
                StringComparison.Ordinal)
            && string.Equals(AppPhotorealPromptTextBox.Text, _modalPhotorealPrompt, StringComparison.Ordinal);

    public bool PhotorealStyleSurfaceForSmoke
        => ModalPhotorealStyleComboBox is not null
            && AppPhotorealStyleListBox is not null
            && ModalPhotorealStyleNameTextBox.MaxLength == MaxPhotorealStyleNameLength
            && AppPhotorealStyleNameTextBox.MaxLength == MaxPhotorealStyleNameLength
            && AutomationProperties.GetName(ModalPhotorealStyleComboBox)
                == "AI photorealization style"
            && AutomationProperties.GetName(AppPhotorealStyleListBox)
                == "Saved AI photorealization styles";

    public IReadOnlyList<string> PhotorealStyleNamesForSmoke
        => _photorealStyles.Select(static style => style.Name).ToList();

    public bool SavePhotorealStyleForSmoke(string name)
    {
        AppPhotorealStyleNameTextBox.Text = name;
        SavePhotorealStyle_Click(SaveAppPhotorealStyleButton, new RoutedEventArgs());
        return FindPhotorealStyle(name) is not null;
    }

    public bool SelectPhotorealStyleForSmoke(string name)
    {
        PhotorealStyleChoice? choice = ModalPhotorealStyleComboBox.Items
            .OfType<PhotorealStyleChoice>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.StyleName, name, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return false;

        ModalPhotorealStyleComboBox.SelectedItem = choice;
        return string.Equals(_selectedPhotorealStyleName, name, StringComparison.OrdinalIgnoreCase);
    }

    public bool DeleteSelectedPhotorealStyleForSmoke()
    {
        string? selectedName = _selectedPhotorealStyleName;
        DeletePhotorealStyle_Click(DeleteAppPhotorealStyleButton, new RoutedEventArgs());
        return selectedName is not null && FindPhotorealStyle(selectedName) is null;
    }

    public bool AppEnhancementOutputRootSurfaceForSmoke
        => EnhancementOutputRootTextBox.IsReadOnly
            && string.Equals(
                AutomationProperties.GetName(EnhancementOutputRootTextBox),
                "AI enhancement output root",
                StringComparison.Ordinal)
            && string.Equals(
                EnhancementOutputRootTextBox.Text,
                ResolvedManagedEnhancementOutputsRoot,
                StringComparison.OrdinalIgnoreCase);

    public bool SetEnhancementOutputRootForSmoke(string root)
    {
        bool saved = SharedDataRootActivation.TryWriteManagedOutputsRoot(
            ResolvedEnhancementJobsPath,
            root,
            out _,
            out _);
        RefreshEnhancementOutputRootSettings();
        return saved;
    }

    public void SetAppPhotorealPromptForSmoke(string prompt)
        => AppPhotorealPromptTextBox.Text = prompt;

    public void ResetAppPhotorealPromptForSmoke()
        => ResetAppPhotorealPrompt_Click(this, new RoutedEventArgs());

    public string DefaultModalPhotorealPromptForSmoke => DefaultPhotorealPrompt;

    public async Task<bool> StartModalPhotorealForSmokeAsync(int timeoutMilliseconds = 3000)
    {
        StartModalPhotoreal_Click(this, new RoutedEventArgs());
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(timeoutMilliseconds);
        return completed && !string.IsNullOrWhiteSpace(_modalEnhancementJobId);
    }
}

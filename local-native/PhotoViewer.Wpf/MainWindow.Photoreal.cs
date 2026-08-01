using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const double DefaultPhotorealStrength = 0.8;
    private const double DefaultPhotorealStructureStrength = 1.0;
    private const double DefaultPhotorealCfgScale = 1.0;
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
    private double _modalPhotorealCfgScale = DefaultPhotorealCfgScale;
    private int _modalPhotorealSteps = DefaultPhotorealSteps;
    private int _modalPhotorealMaxDimension = DefaultPhotorealMaxDimension;
    private string _modalPhotorealPrompt = DefaultPhotorealPrompt;
    private bool _syncingModalPhotorealSettings;
    private readonly List<PhotorealStyleState> _photorealStyles = [];
    private string? _selectedPhotorealStyleName;
    private bool _syncingModalEnhancementVersionSelection;
    private readonly Dictionary<string, ModalDisplayPreference>
        _modalDisplayPreferencesByPath = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PhotorealStyleChoice(string Label, string? StyleName);
    private enum ModalDisplayVersionKind
    {
        Original,
        Upscale,
        Photoreal,
        Video,
    }

    private sealed record ModalDisplayPreference(
        ModalDisplayVersionKind Kind,
        string? JobId);

    private sealed record ModalDisplayVersionChoice(
        ModalDisplayVersionKind Kind,
        int VersionIndex,
        string Label);

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

        _modalEnhancementVersionIndex = 0;
        _modalShowingEnhanced = false;
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

    private bool TryGetDeletableCurrentModalEnhancementVersion(
        Tile tile,
        out ManagedEnhancementVersion version)
    {
        version = null!;
        if (!TryGetCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion displayed)
            || !IsGloballyUniqueManagedJobId(displayed.JobId))
        {
            return false;
        }

        ManagedEnhancementVersion? unique = null;
        foreach (ManagedEnhancementVersion candidate
                 in GetManagedEnhancementVersionsForPath(tile.Path))
        {
            if (!string.Equals(
                    candidate.JobId,
                    displayed.JobId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (unique is not null
                || !TryCreateManagedEnhancedOutput(
                    tile,
                    candidate.Output.OutputPath,
                    candidate.Output.SourceSize,
                    candidate.Output.SourceMtimeMs,
                    out ManagedEnhancedOutput current))
            {
                return false;
            }

            unique = candidate with { Output = current };
        }

        if (unique is null
            || !string.Equals(
                unique.Operation,
                displayed.Operation,
                StringComparison.Ordinal)
            || !string.Equals(
                unique.Output.OutputPath,
                displayed.Output.OutputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        version = unique;
        return true;
    }

    public bool DisplayedManagedImageDeleteVerifiedForSmoke
        => SelectedTile() is Tile tile
            && TryGetDeletableCurrentModalEnhancementVersion(tile, out _);

    public bool DisplayedManagedImageDuplicateJobRejectedForSmoke()
    {
        if (SelectedTile() is not Tile tile
            || !TryGetDeletableCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion displayed)
            || GetManagedEnhancementVersionsForPath(tile.Path)
                is not List<ManagedEnhancementVersion> versions)
        {
            return false;
        }

        versions.Add(displayed);
        try
        {
            return !TryGetDeletableCurrentModalEnhancementVersion(
                tile,
                out _);
        }
        finally
        {
            versions.RemoveAt(versions.Count - 1);
        }
    }

    public bool DisplayedManagedImageGlobalJobIdRejectedForSmoke()
    {
        if (SelectedTile() is not Tile tile
            || !TryGetDeletableCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion displayed)
            || !_ambiguousEnhancementJobIds.Add(displayed.JobId))
        {
            return false;
        }

        try
        {
            return !TryGetDeletableCurrentModalEnhancementVersion(tile, out _);
        }
        finally
        {
            _ambiguousEnhancementJobIds.Remove(displayed.JobId);
        }
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
            if (!IsImageEnhancementOperation(job.Operation)
                || job is not
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
        if (!IsImageEnhancementOperation(job.Operation))
            return;

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
        RememberModalDisplayPreference(
            tile,
            ModalDisplayKindForOperation(job.Operation),
            job.Id);
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
        RememberModalDisplayPreference(
            tile,
            ModalDisplayVersionKind.Original,
            null);
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

    private static ModalDisplayVersionKind ModalDisplayKindForOperation(
        string operation)
        => string.Equals(operation, "photoreal", StringComparison.Ordinal)
            ? ModalDisplayVersionKind.Photoreal
            : ModalDisplayVersionKind.Upscale;

    private void RememberModalDisplayPreference(
        Tile tile,
        ModalDisplayVersionKind kind,
        string? jobId)
    {
        _modalDisplayPreferencesByPath[tile.Path] =
            new ModalDisplayPreference(kind, jobId);
    }

    private bool RestoreModalDisplayPreference(Tile tile)
    {
        ModalDisplayPreference preference;
        bool forceVideoOnly =
            VideoOnlyFilter?.IsChecked == true && _modalVideoVersions.Count > 0;
        if (forceVideoOnly)
        {
            _modalDisplayPreferencesByPath.TryGetValue(
                tile.Path,
                out ModalDisplayPreference? remembered);
            preference = new ModalDisplayPreference(
                ModalDisplayVersionKind.Video,
                remembered?.Kind == ModalDisplayVersionKind.Video
                    ? remembered.JobId
                    : null);
        }
        else if (!_modalDisplayPreferencesByPath.TryGetValue(
                     tile.Path,
                     out preference!))
        {
            preference = new ModalDisplayPreference(
                ModalDisplayVersionKind.Original,
                null);
        }

        _modalShowingEnhanced = false;
        _modalEnhancementVersionIndex = 0;
        if (preference.Kind == ModalDisplayVersionKind.Video)
        {
            int videoIndex = string.IsNullOrWhiteSpace(preference.JobId)
                ? -1
                : _modalVideoVersions.FindIndex(version => string.Equals(
                    version.JobId,
                    preference.JobId,
                    StringComparison.Ordinal));
            if (videoIndex < 0 && forceVideoOnly)
                videoIndex = 0;
            if (videoIndex >= 0)
            {
                _modalVideoVersionIndex = videoIndex;
                return true;
            }
        }
        else if (preference.Kind is
                 ModalDisplayVersionKind.Upscale or
                 ModalDisplayVersionKind.Photoreal)
        {
            int imageIndex = string.IsNullOrWhiteSpace(preference.JobId)
                ? -1
                : _modalEnhancementVersions.FindIndex(version =>
                    string.Equals(
                        version.JobId,
                        preference.JobId,
                        StringComparison.Ordinal)
                    && ModalDisplayKindForOperation(version.Operation)
                        == preference.Kind);
            if (imageIndex >= 0)
            {
                _modalEnhancementVersionIndex = imageIndex + 1;
                _modalShowingEnhanced = true;
            }
        }

        if (preference.Kind != ModalDisplayVersionKind.Original
            && !_modalShowingEnhanced)
        {
            RememberModalDisplayPreference(
                tile,
                ModalDisplayVersionKind.Original,
                null);
        }

        return false;
    }

    private bool CycleModalEnhancementVersion(int delta)
    {
        if (Modal.Visibility != Visibility.Visible)
            return false;

        IReadOnlyList<ModalDisplayVersionChoice> choices =
            BuildModalDisplayVersionChoices();
        if (choices.Count < 2)
            return false;

        int current = choices.ToList().FindIndex(IsCurrentModalDisplayChoice);
        if (current < 0)
            current = 0;
        int next = (current + delta) % choices.Count;
        if (next < 0)
            next += choices.Count;
        return ApplyModalDisplayVersionChoice(choices[next], showFeedback: true);
    }

    private string CurrentModalEnhancementVersionLabel()
    {
        if (_modalShowingVideo
            && _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count)
        {
            return ModalVideoVersionChoiceLabel(_modalVideoVersionIndex);
        }
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

    private string ModalEnhancementVersionChoiceLabel(int versionIndex)
    {
        if (versionIndex <= 0
            || versionIndex > _modalEnhancementVersions.Count)
        {
            return "Original";
        }

        ManagedEnhancementVersion version =
            _modalEnhancementVersions[versionIndex - 1];
        int operationTotal = _modalEnhancementVersions.Count(candidate =>
            string.Equals(candidate.Operation, version.Operation, StringComparison.Ordinal));
        int operationIndex = _modalEnhancementVersions
            .Take(versionIndex)
            .Count(candidate => string.Equals(
                candidate.Operation,
                version.Operation,
                StringComparison.Ordinal));
        string operation = string.Equals(
            version.Operation,
            "photoreal",
            StringComparison.Ordinal)
                ? "実写"
                : "高画質";
        string latest = operationIndex == 1 ? "（最新）" : "";
        return $"{operation} {operationIndex}/{operationTotal}{latest}";
    }

    private string ModalVideoVersionChoiceLabel(int versionIndex)
    {
        if (versionIndex < 0 || versionIndex >= _modalVideoVersions.Count)
            return "動画";

        ManagedVideoVersion version = _modalVideoVersions[versionIndex];
        string latest = versionIndex == 0 ? "（最新）" : "";
        return $"動画 {versionIndex + 1}/{_modalVideoVersions.Count}{latest} ・ "
            + $"{version.DurationSeconds:0.#}秒 {version.PlaybackFps}fps";
    }

    private IReadOnlyList<ModalDisplayVersionChoice>
        BuildModalDisplayVersionChoices()
    {
        var choices = new List<ModalDisplayVersionChoice>
        {
            new(ModalDisplayVersionKind.Original, 0, "Original"),
        };
        choices.AddRange(Enumerable.Range(1, _modalEnhancementVersions.Count)
            .Select(index => new ModalDisplayVersionChoice(
                ModalDisplayKindForOperation(
                    _modalEnhancementVersions[index - 1].Operation),
                index,
                ModalEnhancementVersionChoiceLabel(index))));
        choices.AddRange(Enumerable.Range(0, _modalVideoVersions.Count)
            .Select(index => new ModalDisplayVersionChoice(
                ModalDisplayVersionKind.Video,
                index,
                ModalVideoVersionChoiceLabel(index))));
        return choices;
    }

    private bool IsCurrentModalDisplayChoice(ModalDisplayVersionChoice choice)
        => choice.Kind switch
        {
            ModalDisplayVersionKind.Original =>
                !_modalShowingVideo && !_modalShowingEnhanced,
            ModalDisplayVersionKind.Video =>
                _modalShowingVideo
                && choice.VersionIndex == _modalVideoVersionIndex,
            ModalDisplayVersionKind.Upscale or
            ModalDisplayVersionKind.Photoreal =>
                !_modalShowingVideo
                && _modalShowingEnhanced
                && choice.VersionIndex == _modalEnhancementVersionIndex
                && choice.VersionIndex >= 1
                && choice.VersionIndex <= _modalEnhancementVersions.Count
                && ModalDisplayKindForOperation(
                    _modalEnhancementVersions[choice.VersionIndex - 1].Operation)
                    == choice.Kind,
            _ => false,
        };

    private void RefreshModalEnhancementVersionSelector(bool canShowEnhanced)
    {
        if (ModalEnhancementVersionComboBox is null)
            return;

        IReadOnlyList<ModalDisplayVersionChoice> choices =
            BuildModalDisplayVersionChoices();
        ModalDisplayVersionChoice selectedChoice =
            choices.FirstOrDefault(IsCurrentModalDisplayChoice) ?? choices[0];

        _syncingModalEnhancementVersionSelection = true;
        try
        {
            ModalEnhancementVersionComboBox.ItemsSource = choices;
            ModalEnhancementVersionComboBox.SelectedItem = selectedChoice;
            ModalEnhancementVersionComboBox.IsEnabled = choices.Count > 1;
            ModalEnhancementVersionComboBox.ToolTip = choices.Count > 1
                ? "Original、高画質化、実写化、動画化の全保存版を選択"
                : "AI処理済み画像・動画はありません";
            AutomationProperties.SetName(
                ModalEnhancementVersionComboBox,
                $"表示中: {selectedChoice.Label}. Original、高画質化、実写化、動画化から選択");
        }
        finally
        {
            _syncingModalEnhancementVersionSelection = false;
        }
    }

    private void ModalEnhancementVersion_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingModalEnhancementVersionSelection
            || sender is not ComboBox
            {
                SelectedItem: ModalDisplayVersionChoice choice,
            }
            || Modal.Visibility != Visibility.Visible)
        {
            return;
        }

        if (IsCurrentModalDisplayChoice(choice))
            return;

        if (!ApplyModalDisplayVersionChoice(choice, showFeedback: true))
        {
            SetStatusToast(
                "表示バージョンが古いか一意に特定できません。現在の表示を維持しました。");
            RefreshModalEnhancementVersionSelector(
                canShowEnhanced: _modalEnhancementVersions.Count > 0);
        }
    }

    private bool ApplyModalDisplayVersionChoice(
        ModalDisplayVersionChoice choice,
        bool showFeedback)
    {
        if (Modal.Visibility != Visibility.Visible
            || SelectedTile() is not Tile tile)
        {
            return false;
        }

        bool applied;
        if (choice.Kind == ModalDisplayVersionKind.Video)
        {
            if (choice.VersionIndex < 0
                || choice.VersionIndex >= _modalVideoVersions.Count)
            {
                return false;
            }
            applied = ShowModalVideoVersion(
                choice.VersionIndex,
                autoplay: true);
            if (applied)
            {
                RememberModalDisplayPreference(
                    tile,
                    ModalDisplayVersionKind.Video,
                    _modalVideoVersions[choice.VersionIndex].JobId);
            }
        }
        else
        {
            if (_modalShowingVideo)
                StopAndHideModalVideo(clearSource: true);

            if (choice.Kind == ModalDisplayVersionKind.Original)
            {
                _modalEnhancementVersionIndex = 0;
                _modalShowingEnhanced = false;
                RememberModalDisplayPreference(
                    tile,
                    ModalDisplayVersionKind.Original,
                    null);
            }
            else
            {
                if (choice.VersionIndex < 1
                    || choice.VersionIndex > _modalEnhancementVersions.Count)
                {
                    return false;
                }
                ManagedEnhancementVersion version =
                    _modalEnhancementVersions[choice.VersionIndex - 1];
                if (ModalDisplayKindForOperation(version.Operation) != choice.Kind)
                    return false;
                _modalEnhancementVersionIndex = choice.VersionIndex;
                _modalShowingEnhanced = true;
                RememberModalDisplayPreference(
                    tile,
                    choice.Kind,
                    version.JobId);
            }
            OpenModal();
            applied = true;
        }

        if (applied && showFeedback)
            ShowModalInteractionFeedback(choice.Label);
        return applied;
    }

    public bool SelectModalEnhancementJobVersionForSmoke(string jobId)
    {
        int index = _modalEnhancementVersions.FindIndex(version =>
            string.Equals(version.JobId, jobId, StringComparison.Ordinal));
        if (index < 0)
            return false;

        int versionIndex = index + 1;
        ManagedEnhancementVersion version = _modalEnhancementVersions[index];
        return ApplyModalDisplayVersionChoice(
            new ModalDisplayVersionChoice(
                ModalDisplayKindForOperation(version.Operation),
                versionIndex,
                ModalEnhancementVersionChoiceLabel(versionIndex)),
            showFeedback: false);
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
            _modalPhotorealCfgScale,
            _modalPhotorealMaxDimension,
            _modalPhotorealPrompt.Trim());

    private async void StartModalPhotoreal_Click(object sender, RoutedEventArgs e)
        => await StartModalEnhancementOperationAsync("photoreal");

    private void ToggleModalPhotorealSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ModalPhotorealSettingsPopup is null)
            return;

        SyncModalPhotorealSettingsControls();
        bool opening = ModalPhotorealSettingsPopup.Visibility != Visibility.Visible;
        if (opening && ModalVideoGenerationPopup is not null)
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
        ModalPhotorealSettingsPopup.Visibility = opening
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (opening)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (ModalPhotorealSettingsPopup.Visibility == Visibility.Visible)
                        Keyboard.Focus(ModalPhotorealPromptTextBox);
                }),
                DispatcherPriority.Input);
        }
    }

    private void ModalPhotorealSettingsBackdrop_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModalPhotorealSettingsPopup.Visibility == Visibility.Visible
            && ReferenceEquals(e.OriginalSource, ModalPhotorealSettingsPopup))
        {
            CloseModalPhotorealSettingsBoard();
            e.Handled = true;
        }
    }

    private void CloseModalPhotorealSettingsBoard()
    {
        ModalPhotorealSettingsPopup.Visibility = Visibility.Collapsed;
        ModalPhotorealSettingsButton?.Focus();
    }

    public bool ModalSettingsBackdropDismissContractForSmoke()
    {
        if (Modal.Visibility != Visibility.Visible
            || SelectedTile() is not Tile selected)
            return false;

        string selectedPath = selected.Path;
        WindowState state = WindowState;
        bool fullScreen = _modalFullScreen;
        Modal.UpdateLayout();

        bool RaiseBackdropClick(Grid overlay)
        {
            var args = new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = overlay,
            };
            overlay.RaiseEvent(args);
            return args.Handled;
        }

        ModalPhotorealSettingsPopup.Visibility = Visibility.Visible;
        Minimize_Click(this, new RoutedEventArgs());
        bool photorealCaptionGuard =
            ModalPhotorealSettingsPopup.Visibility == Visibility.Collapsed
            && WindowState == state;
        ModalPhotorealSettingsPopup.Visibility = Visibility.Visible;
        bool photorealHandled = RaiseBackdropClick(
            ModalPhotorealSettingsPopup);
        bool photorealDismissedOnly =
            photorealHandled
            && photorealCaptionGuard
            && ModalPhotorealSettingsPopup.Visibility == Visibility.Collapsed
            && Modal.Visibility == Visibility.Visible;

        ModalVideoGenerationPopup.Visibility = Visibility.Visible;
        bool fakeMaximized = _fakeMaximized;
        Rect bounds = new(Left, Top, Width, Height);
        Maximize_Click(this, new RoutedEventArgs());
        bool videoCaptionGuard =
            ModalVideoGenerationPopup.Visibility == Visibility.Collapsed
            && _fakeMaximized == fakeMaximized
            && new Rect(Left, Top, Width, Height) == bounds;
        ModalVideoGenerationPopup.Visibility = Visibility.Visible;
        bool videoHandled = RaiseBackdropClick(
            ModalVideoGenerationPopup);
        bool videoDismissedOnly =
            videoHandled
            && videoCaptionGuard
            && ModalVideoGenerationPopup.Visibility == Visibility.Collapsed
            && Modal.Visibility == Visibility.Visible;
        ModalSettingsBackdropDiagnosticForSmoke =
            $"photoHandled={photorealHandled};photoCaption={photorealCaptionGuard};"
            + $"photoDismissed={photorealDismissedOnly};videoHandled={videoHandled};"
            + $"videoCaption={videoCaptionGuard};videoDismissed={videoDismissedOnly}";
        return photorealDismissedOnly
            && videoDismissedOnly
            && SelectedTile() is Tile current
            && string.Equals(
                current.Path,
                selectedPath,
                StringComparison.OrdinalIgnoreCase)
            && WindowState == state
            && _modalFullScreen == fullScreen
            && ModalPhotorealSettingsPopup is Grid
            && ModalVideoGenerationPopup is Grid;
    }

    public string ModalSettingsBackdropDiagnosticForSmoke { get; private set; } = "";

    public bool OpenModalPhotorealSettingsForSmoke()
    {
        if (ModalPhotorealSettingsPopup.Visibility != Visibility.Visible)
            ToggleModalPhotorealSettings_Click(this, new RoutedEventArgs());
        return ModalPhotorealSettingsPopup.Visibility == Visibility.Visible;
    }

    public bool ModalSettingsBoardHasKeyboardFocusForSmoke
        => (ModalPhotorealSettingsPopup.Visibility == Visibility.Visible
                && ModalPhotorealSettingsPopup.IsKeyboardFocusWithin)
            || (ModalVideoGenerationPopup.Visibility == Visibility.Visible
                && ModalVideoGenerationPopup.IsKeyboardFocusWithin);

    public bool ModalPhotorealSettingsVisibleForSmoke
        => ModalPhotorealSettingsPopup.Visibility == Visibility.Visible;

    private void ModalPhotorealSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingModalPhotorealSettings
            || ModalPhotorealStrengthSlider is null
            || ModalPhotorealStructureSlider is null
            || ModalPhotorealCfgScaleSlider is null)
        {
            return;
        }

        _modalPhotorealStrength = Math.Clamp(ModalPhotorealStrengthSlider.Value / 100d, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(ModalPhotorealStructureSlider.Value / 100d, 0, 1.2);
        _modalPhotorealCfgScale = Math.Clamp(ModalPhotorealCfgScaleSlider.Value / 100d, 1, 2);
        MarkPhotorealStyleAsCustom();
        RefreshModalPhotorealSettingLabels();
        if (!_initializing)
            SaveState();
    }

    private void AppPhotorealSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingModalPhotorealSettings
            || AppPhotorealStrengthSlider is null
            || AppPhotorealStructureSlider is null
            || AppPhotorealCfgScaleSlider is null)
        {
            return;
        }

        _modalPhotorealStrength = Math.Clamp(AppPhotorealStrengthSlider.Value / 100d, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(AppPhotorealStructureSlider.Value / 100d, 0, 1.2);
        _modalPhotorealCfgScale = Math.Clamp(AppPhotorealCfgScaleSlider.Value / 100d, 1, 2);
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealSettingsStatus("保存済み。次に追加する実写化ジョブから使われます。");
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

    private void AppPhotorealSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings
            || AppPhotorealStepsComboBox is null
            || AppPhotorealSizeComboBox is null)
        {
            return;
        }

        _modalPhotorealSteps = SelectedIntegerTag(
            AppPhotorealStepsComboBox,
            DefaultPhotorealSteps,
            [4, 6, 8, 12]);
        _modalPhotorealMaxDimension = SelectedIntegerTag(
            AppPhotorealSizeComboBox,
            DefaultPhotorealMaxDimension,
            [768, 1024, 1280]);
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealSettingsStatus("保存済み。次に追加する実写化ジョブから使われます。");
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
            style.Prompt,
            style.CfgScale);
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
            || (candidate.CfgScale is double cfgScale
                && (!double.IsFinite(cfgScale) || cfgScale is < 1 or > 2))
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
            CfgScale = candidate.CfgScale is double persistedCfgScale
                ? Math.Clamp(persistedCfgScale, 1, 2)
                : DefaultPhotorealCfgScale,
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
            CfgScale = _modalPhotorealCfgScale,
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
            && Math.Abs((style.CfgScale ?? DefaultPhotorealCfgScale) - _modalPhotorealCfgScale) < 0.0001
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
            $"現在: 強さ {Math.Round(_modalPhotorealStrength * 100):0}% / 構図 {Math.Round(_modalPhotorealStructureStrength * 100):0}% / CFG {_modalPhotorealCfgScale:0.00} / {_modalPhotorealSteps} step / {_modalPhotorealMaxDimension} px";
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
                CfgScale = style.CfgScale,
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

    private void ResetAppPhotorealSettings_Click(object sender, RoutedEventArgs e)
    {
        _selectedPhotorealStyleName = null;
        RestoreModalPhotorealSettings(
            DefaultPhotorealStrength,
            DefaultPhotorealStructureStrength,
            DefaultPhotorealSteps,
            DefaultPhotorealMaxDimension,
            DefaultPhotorealPrompt,
            DefaultPhotorealCfgScale);
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealSettingsStatus("実写化設定を既定値に戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void SetPhotorealSettingsStatus(string message)
    {
        if (AppPhotorealSettingsStatusText is not null)
            AppPhotorealSettingsStatusText.Text = message;
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
        string? prompt = null,
        double? cfgScale = null)
    {
        _modalPhotorealStrength = Math.Clamp(strength ?? DefaultPhotorealStrength, 0.2, 0.8);
        _modalPhotorealStructureStrength = Math.Clamp(
            structureStrength ?? DefaultPhotorealStructureStrength,
            0,
            1.2);
        _modalPhotorealCfgScale = Math.Clamp(
            cfgScale ?? DefaultPhotorealCfgScale,
            1,
            2);
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
            || ModalPhotorealCfgScaleSlider is null
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
            ModalPhotorealCfgScaleSlider.Value = _modalPhotorealCfgScale * 100;
            SelectIntegerTag(ModalPhotorealStepsComboBox, _modalPhotorealSteps);
            SelectIntegerTag(ModalPhotorealSizeComboBox, _modalPhotorealMaxDimension);
            ModalPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            if (AppPhotorealPromptTextBox is not null)
                AppPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            if (AppPhotorealStrengthSlider is not null)
                AppPhotorealStrengthSlider.Value = _modalPhotorealStrength * 100;
            if (AppPhotorealStructureSlider is not null)
                AppPhotorealStructureSlider.Value = _modalPhotorealStructureStrength * 100;
            if (AppPhotorealCfgScaleSlider is not null)
                AppPhotorealCfgScaleSlider.Value = _modalPhotorealCfgScale * 100;
            if (AppPhotorealStepsComboBox is not null)
                SelectIntegerTag(AppPhotorealStepsComboBox, _modalPhotorealSteps);
            if (AppPhotorealSizeComboBox is not null)
                SelectIntegerTag(AppPhotorealSizeComboBox, _modalPhotorealMaxDimension);
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
        if (ModalPhotorealCfgScaleValue is not null)
            ModalPhotorealCfgScaleValue.Text = _modalPhotorealCfgScale.ToString("0.00", CultureInfo.InvariantCulture);
        if (AppPhotorealStrengthValue is not null)
            AppPhotorealStrengthValue.Text = $"{Math.Round(_modalPhotorealStrength * 100):0}%";
        if (AppPhotorealStructureValue is not null)
            AppPhotorealStructureValue.Text = $"{Math.Round(_modalPhotorealStructureStrength * 100):0}%";
        if (AppPhotorealCfgScaleValue is not null)
            AppPhotorealCfgScaleValue.Text = _modalPhotorealCfgScale.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public (double Strength, double StructureStrength, int Steps, double CfgScale, int MaxDimension, string Prompt) ModalPhotorealSettingsForSmoke
        => (
            _modalPhotorealStrength,
            _modalPhotorealStructureStrength,
            _modalPhotorealSteps,
            _modalPhotorealCfgScale,
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
        string prompt = "",
        double cfgScale = DefaultPhotorealCfgScale)
    {
        RestoreModalPhotorealSettings(strength, structureStrength, steps, maxDimension, prompt, cfgScale);
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

    public bool AppPhotorealSettingsSurfaceForSmoke
        => AppPhotorealStrengthSlider.Minimum == 20
            && AppPhotorealStrengthSlider.Maximum == 80
            && AppPhotorealStructureSlider.Minimum == 0
            && AppPhotorealStructureSlider.Maximum == 120
            && AppPhotorealCfgScaleSlider.Minimum == 100
            && AppPhotorealCfgScaleSlider.Maximum == 200
            && AppPhotorealStepsComboBox.Items.Count == 4
            && AppPhotorealSizeComboBox.Items.Count == 3;

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

    public void ResetAppPhotorealSettingsForSmoke()
        => ResetAppPhotorealSettings_Click(this, new RoutedEventArgs());

    public string DefaultModalPhotorealPromptForSmoke => DefaultPhotorealPrompt;

    public async Task<bool> StartModalPhotorealForSmokeAsync(int timeoutMilliseconds = 3000)
    {
        StartModalPhotoreal_Click(this, new RoutedEventArgs());
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(timeoutMilliseconds);
        return completed && !string.IsNullOrWhiteSpace(_modalEnhancementJobId);
    }
}

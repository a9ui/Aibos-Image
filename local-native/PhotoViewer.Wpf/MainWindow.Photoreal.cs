using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const bool DefaultPhotorealLoraEnabled = false;
    private const bool LegacyPhotorealStyleLoraEnabled = true;
    private const double DefaultPhotorealStrength = 0.4;
    private const double DefaultPhotorealCfgScale = 1.0;
    private const bool DefaultPhotorealNegativePromptEnabled = false;
    private const int DefaultPhotorealSteps = 8;
    private const int DefaultPhotorealMaxDimension = 1280;
    private const int MaxPhotorealStyleCount = 32;
    private const int MaxPhotorealStyleNameLength = 40;
    private const int MaxPhotorealPromptLength = 2_000;
    // Keep the persisted selection token longer than any valid user Style name
    // so a previously saved user Style can never collide with a built-in.
    private const string BuiltInPhotorealStylePrefix =
        "aibos:built-in-photoreal-style:";
    private const string PhotorealUpscalePresetId = "photo-natural-x2";
    private const string PhotorealUpscaleAdapterId = "realesrgan-ncnn";
    private const int PhotorealUpscaleScale = 2;
    private static readonly string DefaultPhotorealPrompt =
        WpfLocalPromptPolicy.Current.Photoreal!.Prompt;
    private static readonly string DefaultPhotorealEmptyPrompt =
        WpfLocalPromptPolicy.Current.Photoreal!.EmptyPrompt;
    private static readonly string DefaultPhotorealNegativePrompt =
        WpfLocalPromptPolicy.Current.Photoreal!.NegativePrompt;

    private string _modalEnhancementOperation = "upscale";
    private readonly List<ManagedEnhancementVersion> _modalEnhancementVersions = [];
    private int _modalEnhancementVersionIndex;
    private string? _modalEnhancementVersionsSourcePath;
    private bool _modalPhotorealLoraEnabled = DefaultPhotorealLoraEnabled;
    private double _modalPhotorealStrength = DefaultPhotorealStrength;
    private double _modalPhotorealCfgScale = DefaultPhotorealCfgScale;
    private int _modalPhotorealSteps = DefaultPhotorealSteps;
    private int _modalPhotorealMaxDimension = DefaultPhotorealMaxDimension;
    private string _modalPhotorealPrompt = DefaultPhotorealPrompt;
    private string _modalPhotorealEmptyPrompt = DefaultPhotorealEmptyPrompt;
    private string _modalPhotorealNegativePrompt = DefaultPhotorealNegativePrompt;
    private bool _modalPhotorealNegativePromptEnabled =
        DefaultPhotorealNegativePromptEnabled;
    private bool _photorealSeedFixed;
    private string _photorealSeedValueText = "0";
    private bool _syncingModalPhotorealSettings;
    private readonly List<PhotorealStyleState> _photorealStyles = [];
    private string? _selectedPhotorealStyleName;
    private bool _syncingModalEnhancementVersionSelection;
    private bool? _recoveredPhotorealSourceUpscaleSupported;
    private readonly Dictionary<string, ModalDisplayPreference>
        _modalDisplayPreferencesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModalDisplayPreference>
        _thumbnailImageDisplayPreferencesByPath =
            new(StringComparer.OrdinalIgnoreCase);

    private sealed record BuiltInPhotorealStyle(
        string Id,
        string Label,
        string Prompt)
    {
        public string SelectionKey => BuiltInPhotorealStylePrefix + Id;
    }

    private sealed record PhotorealStyleChoice(
        string Label,
        string? StyleName,
        string? BuiltInId = null)
    {
        public string? SelectionKey => BuiltInId is null
            ? StyleName
            : BuiltInPhotorealStylePrefix + BuiltInId;
    }

    private static readonly IReadOnlyList<BuiltInPhotorealStyle>
        BuiltInPhotorealStyles =
        [
            new(
                "soft-beauty-glamour",
                "標準: Soft Beauty Glamour",
                BuildBuiltInPhotorealPrompt(
                    "Use soft beauty glamour photography with flattering diffused light, luminous natural-looking skin, subtle realistic beauty retouching, gentle tonal transitions, realistic individual hair strands, and an elegant but believable photographic finish. Preserve the original expression; do not add a smile or redesign the face.")),
            new(
                "beauty-natural",
                "標準: 美肌ナチュラル",
                BuildBuiltInPhotorealPrompt(
                    "Use natural beauty portrait photography with soft natural light, clean healthy skin, restrained realistic retouching, gentle contrast, and believable everyday photographic detail.")),
            new(
                "lifestyle-beauty",
                "標準: 生活感",
                BuildBuiltInPhotorealPrompt(
                    "Use natural lifestyle beauty photography with available window light, restrained retouching, realistic everyday skin and fabric texture, neutral color grading, subtle imperfections, and a lived-in photographic atmosphere.")),
            new(
                "clean-beauty",
                "標準: クリーンビューティー",
                BuildBuiltInPhotorealPrompt(
                    "Use clean beauty portrait photography with even diffused studio lighting, clear luminous skin, controlled highlights, refined but realistic retouching, and clean natural color. Keep natural skin texture and avoid a plastic or porcelain finish.")),
            new(
                "cinematic-glamour",
                "標準: シネマティック",
                BuildBuiltInPhotorealPrompt(
                    "Use cinematic beauty glamour photography with directional natural light, soft highlight rolloff, subtle shadow depth, restrained saturation, atmospheric separation, and elegant filmic color grading.")),
            new(
                "wet-underwater-beauty",
                "標準: Wet / Underwater",
                BuildBuiltInPhotorealPrompt(
                    "Use wet or underwater beauty editorial photography consistent with the source scene, with physically believable water, wet hair, bubbles or caustic light only when visibly present, soft skin highlights, and a natural photographic finish.")),
        ];

    private static string BuildBuiltInPhotorealPrompt(string styleDirection)
        => "Transform the supplied image into a faithful realistic photograph. "
            + "Preserve the visible subject identity, facial proportions, expression, "
            + "hairstyle, hair color, body shape, pose, clothing, camera angle, "
            + "framing, and background layout. "
            + styleDirection;

    private static BuiltInPhotorealStyle? FindBuiltInPhotorealStyle(
        string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey)
            || !selectionKey.StartsWith(
                BuiltInPhotorealStylePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string id = selectionKey[BuiltInPhotorealStylePrefix.Length..];
        return BuiltInPhotorealStyles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
    }
    private enum ModalDisplayVersionKind
    {
        Original,
        Upscale,
        Photoreal,
        I2i,
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
        bool LoraEnabled,
        double Strength,
        int Steps,
        double CfgScale,
        int MaxDimension,
        string Prompt,
        string NegativePrompt);

    private sealed record UpscaleRequestSource(
        string? SourceProducerJobId,
        string? SourceRecoveredOutputPath)
    {
        public bool UsesPhotorealSource =>
            !string.IsNullOrWhiteSpace(SourceProducerJobId);

        public bool UsesRecoveredPhotorealSource =>
            !string.IsNullOrWhiteSpace(SourceRecoveredOutputPath);
    }

    private void InitializeModalEnhancementVersions(Tile tile)
    {
        _modalEnhancementVersions.Clear();
        _modalEnhancementVersionsSourcePath = tile.Path;
        foreach (ManagedEnhancementVersion candidate in GetManagedEnhancementVersionsForPath(tile.Path))
        {
            // The shared enhancement snapshot already canonicalized and
            // validated every entry. Copy its inventory without resolving all
            // historical outputs again on the UI thread; the single version
            // that is actually displayed is revalidated below by
            // TryGetCurrent/PreferredModalEnhancementVersion.
            if (!_modalEnhancementVersions.Any(version =>
                    string.Equals(
                        version.Output.OutputPath,
                        candidate.Output.OutputPath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                _modalEnhancementVersions.Add(candidate);
            }
        }

        if (_modalEnhancementVersions.Count == 0
            && TryGetManagedEnhancedOutputForPath(tile.Path, out ManagedEnhancedOutput fallback))
        {
            _modalEnhancementVersions.Add(
                new ManagedEnhancementVersion("", "upscale", fallback));
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

    private bool TryGetExactDurableCurrentModalEnhancementVersion(
        Tile tile,
        out ManagedEnhancementVersion version)
    {
        version = null!;
        if (!TryGetCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion displayed)
            || displayed.Recovered
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
                || candidate.Recovered
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

    private bool TryGetDeletableCurrentModalEnhancementVersion(
        Tile tile,
        out ManagedEnhancementVersion version)
    {
        version = null!;
        if (!TryGetExactDurableCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion exact)
            || IsManagedImageOutputDependencyProtected(exact))
        {
            return false;
        }

        version = exact;
        return true;
    }

    private bool IsManagedImageOutputDependencyProtected(
        ManagedEnhancementVersion version)
    {
        if (!_enhancementReadOk
            || !_activeVideoDependencySnapshotComplete
            || _activeI2iSourceProducerJobIds.Contains(version.JobId)
            || _activeVideoSourceProducerJobIds.Contains(version.JobId)
            || IsPendingVideoSourceDependencyProtected(version))
        {
            return true;
        }

        string? normalizedOutputPath = NormalizeEnhancementDependencyPath(
            version.Output.OutputPath);
        return normalizedOutputPath is null
            || _activeVideoManagedSourcePaths.Contains(normalizedOutputPath);
    }

    public bool DisplayedManagedImageDeleteVerifiedForSmoke
        => TryGetModalSourceTile(out Tile tile)
            && TryGetDeletableCurrentModalEnhancementVersion(tile, out _);

    public bool ReinitializeModalEnhancementVersionsForSmoke()
    {
        if (!TryGetModalSourceTile(out Tile tile))
            return false;

        InitializeModalEnhancementVersions(tile);
        return _modalEnhancementVersions.Count > 0;
    }

    public bool DisplayedManagedImageDuplicateJobRejectedForSmoke()
    {
        if (!TryGetModalSourceTile(out Tile tile)
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
        if (!TryGetModalSourceTile(out Tile tile)
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
        ManagedEnhancementVersion[] recoveredVersions =
            GetManagedEnhancementVersionsForPath(tile.Path)
                .Where(static version => version.Recovered)
                .ToArray();
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
        foreach (ManagedEnhancementVersion recovered in recoveredVersions)
        {
            if (next.Any(candidate => string.Equals(
                    candidate.Output.OutputPath,
                    recovered.Output.OutputPath,
                    StringComparison.OrdinalIgnoreCase))
                || !TryCreateManagedEnhancedOutput(
                    tile,
                    recovered.Output.OutputPath,
                    recovered.Output.SourceSize,
                    recovered.Output.SourceMtimeMs,
                    out ManagedEnhancedOutput current))
            {
                continue;
            }
            next.Add(recovered with { Output = current });
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
        RebuildManagedFavoriteSourcePathIndexes();
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
        RebuildManagedFavoriteSourcePathIndexes();
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
            RebuildManagedFavoriteSourcePathIndexes();
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
        RebuildManagedFavoriteSourcePathIndexes();
    }

    private static ModalDisplayVersionKind ModalDisplayKindForOperation(
        string operation)
        => operation switch
        {
            "photoreal" => ModalDisplayVersionKind.Photoreal,
            "i2i" => ModalDisplayVersionKind.I2i,
            _ => ModalDisplayVersionKind.Upscale,
        };

    private void RememberModalDisplayPreference(
        Tile tile,
        ModalDisplayVersionKind kind,
        string? jobId)
    {
        var preference = new ModalDisplayPreference(kind, jobId);
        _modalDisplayPreferencesByPath[tile.Path] = preference;
        if (kind == ModalDisplayVersionKind.Video)
            return;
        if (kind == ModalDisplayVersionKind.Original)
        {
            if (_thumbnailImageDisplayPreferencesByPath.Remove(tile.Path))
                InvalidateGalleryThumbnailPreference(tile);
            return;
        }

        if (_thumbnailImageDisplayPreferencesByPath.TryGetValue(
                tile.Path,
                out ModalDisplayPreference? current)
            && current == preference)
        {
            return;
        }

        _thumbnailImageDisplayPreferencesByPath[tile.Path] = preference;
        InvalidateGalleryThumbnailPreference(tile);
    }

    private string ResolveGalleryThumbnailAssetPath(Tile tile)
    {
        if (!_useLastDisplayedImageVersionForThumbnails
            || !_thumbnailImageDisplayPreferencesByPath.TryGetValue(
                tile.Path,
                out ModalDisplayPreference? preference)
            || preference.Kind == ModalDisplayVersionKind.Original
            || preference.Kind == ModalDisplayVersionKind.Video
            || string.IsNullOrWhiteSpace(preference.JobId))
        {
            return tile.Path;
        }

        ManagedEnhancementVersion? version =
            GetManagedEnhancementVersionsForPath(tile.Path)
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.JobId,
                        preference.JobId,
                        StringComparison.Ordinal)
                    && ModalDisplayKindForOperation(candidate.Operation)
                        == preference.Kind);
        if (version is not null
            && TryCreateManagedEnhancedOutput(
                tile,
                version.Output.OutputPath,
                version.Output.SourceSize,
                version.Output.SourceMtimeMs,
                out ManagedEnhancedOutput current))
        {
            return current.OutputPath;
        }

        _thumbnailImageDisplayPreferencesByPath.Remove(tile.Path);
        _thumbnailDecodeFailures.TryRemove(tile.Path, out _);
        return tile.Path;
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
            ManagedEnhancementVersion? recoveredDefault =
                _modalEnhancementVersions.FirstOrDefault();
            preference = recoveredDefault?.Recovered == true
                ? new ModalDisplayPreference(
                    ModalDisplayKindForOperation(recoveredDefault.Operation),
                    recoveredDefault.JobId)
                : new ModalDisplayPreference(
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
                 ModalDisplayVersionKind.Photoreal or
                 ModalDisplayVersionKind.I2i)
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
                RememberModalDisplayPreference(
                    tile,
                    preference.Kind,
                    _modalEnhancementVersions[imageIndex].JobId);
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
            return ModalOriginalVersionChoiceLabel();
        }

        return ModalEnhancementVersionChoiceLabel(_modalEnhancementVersionIndex);
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
        string operation = version.Operation switch
        {
            "photoreal" => "実写化",
            "i2i" => "AI編集",
            _ => "高画質化",
        };
        return $"{operation} {operationIndex}/{operationTotal}";
    }

    private string ModalVideoVersionChoiceLabel(int versionIndex)
    {
        if (versionIndex < 0 || versionIndex >= _modalVideoVersions.Count)
            return "生成";

        ManagedVideoVersion version = _modalVideoVersions[versionIndex];
        string kind = version.VersionKind switch
        {
            "edit" => "AI編集",
            "trim" => "トリム",
            "finish" => "AI高画質化",
            _ => "生成",
        };
        int kindTotal = _modalVideoVersions.Count(candidate =>
            string.Equals(
                candidate.VersionKind,
                version.VersionKind,
                StringComparison.Ordinal));
        int kindIndex = _modalVideoVersions
            .Take(versionIndex + 1)
            .Count(candidate => string.Equals(
                candidate.VersionKind,
                version.VersionKind,
                StringComparison.Ordinal));
        return $"{kind} {kindIndex}/{kindTotal}";
    }

    private string ModalOriginalVersionChoiceLabel()
        => ExternalVideoDropSessionActive
            || string.Equals(
                Path.GetExtension(_modalSourceTilePath),
                ManagedVideoExtension,
                StringComparison.OrdinalIgnoreCase)
                ? "元動画"
                : "Original";

    public string OriginalVideoVersionLabelForSmoke(string path)
    {
        string? previous = _modalSourceTilePath;
        _modalSourceTilePath = path;
        try
        {
            return ModalOriginalVersionChoiceLabel();
        }
        finally
        {
            _modalSourceTilePath = previous;
        }
    }

    private IReadOnlyList<ModalDisplayVersionChoice>
        BuildModalDisplayVersionChoices()
    {
        var choices = new List<ModalDisplayVersionChoice>
        {
            new(
                ModalDisplayVersionKind.Original,
                0,
                ModalOriginalVersionChoiceLabel()),
        };
        IEnumerable<int> enhancementIndices = Enumerable.Range(
            1,
            _modalEnhancementVersions.Count);
        choices.AddRange(enhancementIndices
            .Where(index => string.Equals(
                _modalEnhancementVersions[index - 1].Operation,
                "photoreal",
                StringComparison.Ordinal))
            .Select(index => new ModalDisplayVersionChoice(
                ModalDisplayKindForOperation(
                    _modalEnhancementVersions[index - 1].Operation),
                index,
                ModalEnhancementVersionChoiceLabel(index))));
        choices.AddRange(enhancementIndices
            .Where(index => string.Equals(
                _modalEnhancementVersions[index - 1].Operation,
                "upscale",
                StringComparison.Ordinal))
            .Select(index => new ModalDisplayVersionChoice(
                ModalDisplayKindForOperation(
                    _modalEnhancementVersions[index - 1].Operation),
                index,
                ModalEnhancementVersionChoiceLabel(index))));
        choices.AddRange(enhancementIndices
            .Where(index => string.Equals(
                _modalEnhancementVersions[index - 1].Operation,
                "i2i",
                StringComparison.Ordinal))
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
            ModalDisplayVersionKind.Photoreal or
            ModalDisplayVersionKind.I2i =>
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
                ? "Original、高画質化、実写化、AI編集、動画化の全保存版を選択"
                : "AI処理済み画像・動画はありません";
            AutomationProperties.SetName(
                ModalEnhancementVersionComboBox,
                $"表示中: {selectedChoice.Label}. Original、高画質化、実写化、AI編集、動画化から選択");
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
        if (!TryGetModalSourceTile(out Tile tile))
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
                if (ModalDisplayKindForOperation(version.Operation) != choice.Kind
                    || !TryCreateManagedEnhancedOutput(
                        tile,
                        version.Output.OutputPath,
                        version.Output.SourceSize,
                        version.Output.SourceMtimeMs,
                        out ManagedEnhancedOutput current))
                {
                    return false;
                }
                _modalEnhancementVersions[choice.VersionIndex - 1] =
                    version with { Output = current };
                _modalEnhancementVersionIndex = choice.VersionIndex;
                _modalShowingEnhanced = true;
                RememberModalDisplayPreference(
                    tile,
                    choice.Kind,
                    version.JobId);
            }
            OpenModal(tile);
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

    public bool MarkDisplayedPhotorealAsRecoveredForSmoke()
    {
        if (!_modalShowingEnhanced
            || _modalEnhancementVersionIndex < 1
            || _modalEnhancementVersionIndex > _modalEnhancementVersions.Count)
        {
            return false;
        }

        int index = _modalEnhancementVersionIndex - 1;
        ManagedEnhancementVersion displayed = _modalEnhancementVersions[index];
        if (!string.Equals(
                displayed.Operation,
                "photoreal",
                StringComparison.Ordinal))
        {
            return false;
        }

        _modalEnhancementVersions[index] = displayed with { Recovered = true };
        return true;
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

    private bool CurrentModalEnhancementVersionIsI2i()
        => _modalShowingEnhanced
            && _modalEnhancementVersionIndex >= 1
            && _modalEnhancementVersionIndex <= _modalEnhancementVersions.Count
            && _modalEnhancementVersions[_modalEnhancementVersionIndex - 1].Operation == "i2i";

    private bool TryResolveModalUpscaleProfile(
        Tile tile,
        out UpscaleRequestSource requestSource,
        out string presetId,
        out string adapterId,
        out double scale,
        out string error)
    {
        requestSource = new UpscaleRequestSource(null, null);
        presetId = _modalEnhancementPresetId;
        adapterId = _modalEnhancementAdapterId;
        scale = _modalEnhancementScale;
        error = "";
        if (!CurrentModalEnhancementVersionIsPhotoreal())
            return true;

        if (!TryGetCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion displayed)
            || !string.Equals(
                displayed.Operation,
                "photoreal",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(displayed.JobId))
        {
            error =
                "表示中の実写化バージョンを一意に確認できません。バージョンを選び直してから高画質化してください。";
            return false;
        }

        if (displayed.Recovered)
        {
            // Recovered versions are intentionally not deletable because no
            // durable job row owns them. Recovery already tied this exact,
            // canonical managed output to the current Original by filename
            // hash and source signature. HQ therefore uses a separate,
            // non-destructive proof: orphan job id plus that validated path.
            requestSource = new UpscaleRequestSource(
                displayed.JobId,
                displayed.Output.OutputPath);
        }
        else
        {
            // Keep the existing durable producer proof for ordinary managed
            // photoreal versions. The deletion verifier also proves the exact
            // displayed job id/output pair is globally unambiguous.
            if (!TryGetDeletableCurrentModalEnhancementVersion(
                    tile,
                    out ManagedEnhancementVersion verified))
            {
                error =
                    "表示中の実写化バージョンを一意に確認できません。バージョンを選び直してから高画質化してください。";
                return false;
            }
            requestSource = new UpscaleRequestSource(verified.JobId, null);
        }
        presetId = PhotorealUpscalePresetId;
        adapterId = PhotorealUpscaleAdapterId;
        scale = PhotorealUpscaleScale;
        return true;
    }

    private bool TryResolveLatestPhotorealUpscaleProfile(
        Tile tile,
        out UpscaleRequestSource requestSource,
        out string presetId,
        out string adapterId,
        out double scale,
        out string error)
    {
        requestSource = new UpscaleRequestSource(null, null);
        presetId = PhotorealUpscalePresetId;
        adapterId = PhotorealUpscaleAdapterId;
        scale = PhotorealUpscaleScale;
        error = "";

        foreach (ManagedEnhancementVersion candidate
                 in GetManagedEnhancementVersionsForPath(tile.Path))
        {
            if (!string.Equals(
                    candidate.Operation,
                    "photoreal",
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(candidate.JobId)
                || !TryCreateManagedEnhancedOutput(
                    tile,
                    candidate.Output.OutputPath,
                    candidate.Output.SourceSize,
                    candidate.Output.SourceMtimeMs,
                    out ManagedEnhancedOutput current)
                || !candidate.Recovered
                    && !IsGloballyUniqueManagedJobId(candidate.JobId))
            {
                continue;
            }

            requestSource = candidate.Recovered
                ? new UpscaleRequestSource(candidate.JobId, current.OutputPath)
                : new UpscaleRequestSource(candidate.JobId, null);
            return true;
        }

        error =
            "高画質化できる実写版が見つかりません。先にAI実写化を完了するか、実写版のJob参照を確認してください。";
        return false;
    }

    private bool TryResolveModalPhotorealUpscaleProfile(
        Tile tile,
        out UpscaleRequestSource requestSource,
        out string presetId,
        out string adapterId,
        out double scale,
        out string error)
    {
        if (CurrentModalEnhancementVersionIsPhotoreal())
        {
            bool resolved = TryResolveModalUpscaleProfile(
                tile,
                out requestSource,
                out presetId,
                out adapterId,
                out scale,
                out error);
            return resolved && requestSource.UsesPhotorealSource;
        }

        return TryResolveLatestPhotorealUpscaleProfile(
            tile,
            out requestSource,
            out presetId,
            out adapterId,
            out scale,
            out error);
    }

    private static string? ResolveOriginalPromptSnapshot(
        Tile tile,
        string sourceIdentity)
        => ResolveOriginalPromptSnapshot(tile.Prompt, sourceIdentity);

    private static string? ResolveOriginalPromptSnapshot(
        string? indexedOriginalPrompt,
        string sourceIdentity)
    {
        string embeddedPrompt =
            ReadPngParametersMetadata(sourceIdentity, CancellationToken.None)
                ?.Prompt
                ?.Trim()
            ?? "";
        if (embeddedPrompt.Length > 0)
            return embeddedPrompt;

        // The index is itself a snapshot of Original metadata and is useful
        // for formats without a directly readable PNG parameters chunk. It is
        // never replaced with the current photoreal settings or a default.
        string indexedPrompt = indexedOriginalPrompt?.Trim() ?? "";
        return indexedPrompt.Length == 0 ? null : indexedPrompt;
    }

    private Dictionary<string, object?> CreateUpscaleRequestBody(
        string sourceIdentity,
        UpscaleRequestSource requestSource,
        string presetId,
        string adapterId,
        double scale,
        string? originalPromptSnapshot,
        bool? confirmLargeJob,
        string? queuePlacement = null,
        string? outputFormat = null,
        bool includeOperation = true,
        bool includeNullOriginalPrompt = true)
    {
        if (requestSource.UsesRecoveredPhotorealSource
            && !requestSource.UsesPhotorealSource)
        {
            throw new InvalidOperationException(
                "A recovered photoreal HQ source requires its recovered job id.");
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceId"] = sourceIdentity,
        };
        if (requestSource.UsesPhotorealSource)
            body["sourceProducerJobId"] = requestSource.SourceProducerJobId;
        if (requestSource.UsesRecoveredPhotorealSource)
            body["sourceRecoveredOutputPath"] =
                requestSource.SourceRecoveredOutputPath;
        if (includeOperation)
            body["operation"] = "upscale";
        body["presetId"] = presetId;
        body["adapterId"] = adapterId;
        body["scale"] = scale;

        // Only an Original/already-upscaled source may carry a client prompt.
        // Producer and Recovered photoreal inputs are resolved by the backend
        // from their exact managed version and never inherit current defaults.
        if (!requestSource.UsesPhotorealSource
            && (includeNullOriginalPrompt
                || !string.IsNullOrWhiteSpace(originalPromptSnapshot)))
        {
            body["prompt"] = string.IsNullOrWhiteSpace(originalPromptSnapshot)
                ? null
                : originalPromptSnapshot;
        }
        if (confirmLargeJob is bool confirmed)
            body["confirmLargeJob"] = confirmed;
        if (!string.IsNullOrWhiteSpace(queuePlacement))
            body["queuePlacement"] = queuePlacement;
        if (!string.IsNullOrWhiteSpace(outputFormat))
            body["outputFormat"] = outputFormat;
        return body;
    }

    private bool HasRecoveredPhotorealUpscaleSource(Tile tile)
        => GetManagedEnhancementVersionsForPath(tile.Path).Any(version =>
            version.Recovered
            && string.Equals(
                version.Operation,
                "photoreal",
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(version.JobId)
            && TryCreateManagedEnhancedOutput(
                tile,
                version.Output.OutputPath,
                version.Output.SourceSize,
                version.Output.SourceMtimeMs,
                out _));

    private async Task<bool?> RefreshRecoveredPhotorealSourceUpscaleCapabilityAsync(
        CancellationToken token = default)
    {
        _recoveredPhotorealSourceUpscaleSupported = null;
        UpdateModalEnhancementActionControls();
        EnhancementApiResponse health = await SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/health",
            token: token);
        if (token.IsCancellationRequested)
            return _recoveredPhotorealSourceUpscaleSupported;
        _recoveredPhotorealSourceUpscaleSupported = health.Ok
            && health.Payload is JsonElement payload
                ? HasEnhancementCapability(
                    payload,
                    RecoveredPhotorealSourceUpscaleCapability)
                : null;
        UpdateModalEnhancementActionControls();
        return _recoveredPhotorealSourceUpscaleSupported;
    }

    private ModalPhotorealRequestSettings CurrentModalPhotorealRequestSettings()
    {
        string prompt = _modalPhotorealPrompt.Trim();
        if (prompt.Length == 0)
            prompt = _modalPhotorealEmptyPrompt.Trim();

        return new(
            _modalPhotorealLoraEnabled,
            _modalPhotorealStrength,
            _modalPhotorealSteps,
            _modalPhotorealCfgScale,
            _modalPhotorealMaxDimension,
            prompt,
            _modalPhotorealNegativePromptEnabled
                ? _modalPhotorealNegativePrompt.Trim()
                : "");
    }

    private bool TryResolvePhotorealSeed(out int? seed, out string error)
    {
        seed = null;
        error = "";
        if (!_photorealSeedFixed)
            return true;

        if (TryParseFixedSeed(_photorealSeedValueText, out int fixedSeed))
        {
            seed = fixedSeed;
            return true;
        }

        error = "実写化のFixed Seedは0〜2147483647の整数で入力してください。ジョブは追加していません。";
        return false;
    }

    private static Dictionary<string, object?> CreatePhotorealRequestBody(
        string sourceIdentity,
        ModalPhotorealRequestSettings settings,
        int? seed,
        string? queuePlacement = null)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["sourceId"] = sourceIdentity,
            ["operation"] = "photoreal",
            ["presetId"] = "photoreal-balanced",
            ["adapterId"] = "comfyui-flux2-photoreal",
            ["loraEnabled"] = settings.LoraEnabled,
            ["strength"] = settings.Strength,
            ["steps"] = settings.Steps,
            ["cfgScale"] = settings.CfgScale,
            ["maxDimension"] = settings.MaxDimension,
            ["prompt"] = settings.Prompt,
            ["negativePrompt"] = settings.NegativePrompt,
        };
        if (seed is int fixedSeed)
            requestBody["seed"] = fixedSeed;
        if (!string.IsNullOrWhiteSpace(queuePlacement))
            requestBody["queuePlacement"] = queuePlacement;
        return requestBody;
    }

    private async void StartModalPhotoreal_Click(object sender, RoutedEventArgs e)
        => await StartModalEnhancementOperationAsync("photoreal");

    private async void StartModalPhotorealUpscale_Click(
        object sender,
        RoutedEventArgs e)
        => await StartModalEnhancementOperationAsync(
            "upscale",
            requirePhotorealSource: true);

    private void ToggleModalPhotorealSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ModalPhotorealSettingsPopup is null)
            return;

        SyncModalPhotorealSettingsControls();
        bool opening = ModalPhotorealSettingsPopup.Visibility != Visibility.Visible;
        if (opening && ModalUpscaleSettingsPopup is not null)
            ModalUpscaleSettingsPopup.Visibility = Visibility.Collapsed;
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

        ModalUpscaleSettingsPopup.Visibility = Visibility.Visible;
        bool upscaleHandled = RaiseBackdropClick(
            ModalUpscaleSettingsPopup);
        bool upscaleDismissedOnly =
            upscaleHandled
            && ModalUpscaleSettingsPopup.Visibility == Visibility.Collapsed
            && Modal.Visibility == Visibility.Visible;

        ModalPhotorealSettingsPopup.Visibility = Visibility.Visible;
        bool photorealHandled = RaiseBackdropClick(
            ModalPhotorealSettingsPopup);
        bool photorealDismissedOnly =
            photorealHandled
            && ModalPhotorealSettingsPopup.Visibility == Visibility.Collapsed
            && Modal.Visibility == Visibility.Visible;

        ModalVideoGenerationPopup.Visibility = Visibility.Visible;
        bool videoHandled = RaiseBackdropClick(
            ModalVideoGenerationPopup);
        bool videoDismissedOnly =
            videoHandled
            && ModalVideoGenerationPopup.Visibility == Visibility.Collapsed
            && Modal.Visibility == Visibility.Visible;
        ModalSettingsBackdropDiagnosticForSmoke =
            $"upscaleHandled={upscaleHandled};"
            + $"upscaleDismissed={upscaleDismissedOnly};photoHandled={photorealHandled};"
            + $"photoDismissed={photorealDismissedOnly};videoHandled={videoHandled};"
            + $"videoDismissed={videoDismissedOnly}";
        return upscaleDismissedOnly
            && photorealDismissedOnly
            && videoDismissedOnly
            && SelectedTile() is Tile current
            && string.Equals(
                current.Path,
                selectedPath,
                StringComparison.OrdinalIgnoreCase)
            && WindowState == state
            && _modalFullScreen == fullScreen
            && ModalUpscaleSettingsPopup is Grid
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
        => (ModalUpscaleSettingsPopup.Visibility == Visibility.Visible
                && ModalUpscaleSettingsPopup.IsKeyboardFocusWithin)
            || (ModalPhotorealSettingsPopup.Visibility == Visibility.Visible
                && ModalPhotorealSettingsPopup.IsKeyboardFocusWithin)
            || (ModalVideoGenerationPopup.Visibility == Visibility.Visible
                && ModalVideoGenerationPopup.IsKeyboardFocusWithin);

    public bool ModalPhotorealSettingsVisibleForSmoke
        => ModalPhotorealSettingsPopup.Visibility == Visibility.Visible;

    private void ModalPhotorealSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingModalPhotorealSettings
            || ModalPhotorealStrengthSlider is null
            || ModalPhotorealCfgScaleSlider is null)
        {
            return;
        }

        _modalPhotorealStrength = Math.Clamp(ModalPhotorealStrengthSlider.Value / 100d, 0.2, 0.8);
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
            || AppPhotorealCfgScaleSlider is null)
        {
            return;
        }

        _modalPhotorealStrength = Math.Clamp(AppPhotorealStrengthSlider.Value / 100d, 0.2, 0.8);
        _modalPhotorealCfgScale = Math.Clamp(AppPhotorealCfgScaleSlider.Value / 100d, 1, 2);
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealSettingsStatus("保存済み。次に追加する実写化ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void PhotorealLoraEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingModalPhotorealSettings
            || sender is not CheckBox checkBox
            || AppPhotorealLoraEnabledCheckBox is null
            || ModalPhotorealLoraEnabledCheckBox is null)
        {
            return;
        }

        _modalPhotorealLoraEnabled = checkBox.IsChecked == true;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealSettingsStatus(
            _modalPhotorealLoraEnabled
                ? "WarmBloodAban Anything-to-Real LoRAを比較用に使います。"
                : "標準のFLUX.2 Klein本体だけを使います。");
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

    private void PhotorealSeedMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || sender is not ComboBox source)
            return;

        _photorealSeedFixed = SelectedSeedModeIsFixed(source);
        SyncPhotorealSeedControls();
        SetPhotorealSeedStatus(
            _photorealSeedFixed
                ? TryParseFixedSeed(_photorealSeedValueText, out _)
                    ? "Fixed Seedを保存しました。次の実写化ジョブから使われます。"
                    : "Fixed Seedは0〜2147483647の整数で入力してください。"
                : "Random Seedを使います。ジョブ追加時に新しいSeedを決めます。");
        if (!_initializing)
            SaveState();
    }

    private void PhotorealSeedValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || sender is not TextBox source)
            return;

        _photorealSeedValueText = source.Text;
        SyncPhotorealSeedControls();
        SetPhotorealSeedStatus(
            !_photorealSeedFixed
                ? "Random Seedを使います。Fixedへ切り替えるまで数値は送信しません。"
                : TryParseFixedSeed(_photorealSeedValueText, out _)
                    ? "Fixed Seedを保存しました。次の実写化ジョブから使われます。"
                    : "Fixed Seedは0〜2147483647の整数で入力してください。");
        if (!_initializing)
            SaveState();
    }

    private void ModalPhotorealPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || ModalPhotorealPromptTextBox is null)
            return;

        _modalPhotorealPrompt = ModalPhotorealPromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncAppPhotorealPromptControls();
        if (!_initializing)
            SaveState();
    }

    private void ResetModalPhotorealPrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealPrompt();

    private void ModalPhotorealEmptyPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || ModalPhotorealEmptyPromptTextBox is null)
            return;

        _modalPhotorealEmptyPrompt = ModalPhotorealEmptyPromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncAppPhotorealPromptControls();
        if (!_initializing)
            SaveState();
    }

    private void ResetModalPhotorealEmptyPrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealEmptyPrompt();

    private void ModalPhotorealNegativePrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || ModalPhotorealNegativePromptTextBox is null)
            return;

        _modalPhotorealNegativePrompt = ModalPhotorealNegativePromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncAppPhotorealPromptControls();
        if (!_initializing)
            SaveState();
    }

    private void ResetModalPhotorealNegativePrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealNegativePrompt();

    private void PhotorealNegativePromptEnabled_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || sender is not CheckBox checkBox)
            return;

        _modalPhotorealNegativePromptEnabled = checkBox.IsChecked == true;
        SyncModalPhotorealSettingsControls();
        SetPhotorealPromptStatus(
            _modalPhotorealNegativePromptEnabled
                ? "Negative conditioningを新しい実写化jobへ送信します。"
                : "Negative文は保存したまま、新しい実写化jobへは送信しません。");
        if (!_initializing)
            SaveState();
    }

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

    private void AppPhotorealEmptyPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || AppPhotorealEmptyPromptTextBox is null)
            return;

        _modalPhotorealEmptyPrompt = AppPhotorealEmptyPromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealPromptStatus("保存済み。Positiveが空欄のときだけ使われます。");
        if (!_initializing)
            SaveState();
    }

    private void ResetAppPhotorealEmptyPrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealEmptyPrompt();

    private void AppPhotorealNegativePrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingModalPhotorealSettings || AppPhotorealNegativePromptTextBox is null)
            return;

        _modalPhotorealNegativePrompt = AppPhotorealNegativePromptTextBox.Text;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealPromptStatus("保存済み。Negative conditioningとして送信されます。");
        if (!_initializing)
            SaveState();
    }

    private void ResetAppPhotorealNegativePrompt_Click(object sender, RoutedEventArgs e)
        => ResetPhotorealNegativePrompt();

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

        if (choice.BuiltInId is not null)
        {
            BuiltInPhotorealStyle? builtIn = FindBuiltInPhotorealStyle(
                choice.SelectionKey);
            if (builtIn is null)
                return;

            _selectedPhotorealStyleName = builtIn.SelectionKey;
            RestoreModalPhotorealSettings(
                _modalPhotorealStrength,
                _modalPhotorealSteps,
                _modalPhotorealMaxDimension,
                builtIn.Prompt,
                _modalPhotorealCfgScale,
                builtIn.Prompt,
                _modalPhotorealNegativePrompt,
                _modalPhotorealLoraEnabled,
                _modalPhotorealNegativePromptEnabled);
            RefreshPhotorealStyleControls(updateNameFields: true);
            SetPhotorealStyleStatus(
                $"「{builtIn.Label}」を反映しました。LoRA・強度・CFG・品質・Negativeは変更していません。");
            if (!_initializing)
            {
                SaveAiStyles();
                SaveState();
            }
            return;
        }

        if (choice.StyleName is null)
        {
            _selectedPhotorealStyleName = null;
            RefreshPhotorealStyleControls(updateNameFields: false);
            SetPhotorealStyleStatus("現在の設定を使用します。Styleにはまだ保存されていません。");
            if (!_initializing)
            {
                SaveAiStyles();
                SaveState();
            }
            return;
        }

        PhotorealStyleState? style = FindPhotorealStyle(choice.StyleName);
        if (style is null)
            return;

        _selectedPhotorealStyleName = style.Name;
        RestoreModalPhotorealSettings(
            style.Strength,
            style.Steps,
            style.MaxDimension,
            style.Prompt,
            style.CfgScale,
            style.EmptyPrompt,
            style.NegativePrompt,
            style.LoraEnabled);
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealStyleStatus($"「{style.Name}」を反映しました。次に追加するAI実写化ジョブから使われます。");
        if (!_initializing)
        {
            SaveAiStyles();
            SaveState();
        }
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
            SaveAiStyles();
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
            SaveAiStyles();
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

        BuiltInPhotorealStyle? builtIn = FindBuiltInPhotorealStyle(
            selectedStyleName);
        if (builtIn is not null && BuiltInPhotorealStyleMatchesCurrent(builtIn))
        {
            _selectedPhotorealStyleName = builtIn.SelectionKey;
        }
        else
        {
            PhotorealStyleState? selected = FindPhotorealStyle(selectedStyleName);
            _selectedPhotorealStyleName = selected is not null
                && PhotorealStyleMatchesCurrent(selected)
                    ? selected.Name
                    : null;
        }
        RefreshPhotorealStyleControls(updateNameFields: true);
    }

    private static PhotorealStyleState? NormalizePhotorealStyle(PhotorealStyleState? candidate)
    {
        if (candidate is null)
            return null;

        string name = candidate.Name?.Trim() ?? "";
        if (!IsValidPhotorealStyleName(name)
            || !double.IsFinite(candidate.Strength)
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
        string emptyPrompt = candidate.EmptyPrompt ?? DefaultPhotorealEmptyPrompt;
        if (emptyPrompt.Length > MaxPhotorealPromptLength)
            emptyPrompt = emptyPrompt[..MaxPhotorealPromptLength];
        string negativePrompt = candidate.NegativePrompt ?? DefaultPhotorealNegativePrompt;
        if (negativePrompt.Length > MaxPhotorealPromptLength)
            negativePrompt = negativePrompt[..MaxPhotorealPromptLength];
        return new PhotorealStyleState
        {
            Name = name,
            // Styles saved before the toggle existed always used this LoRA.
            LoraEnabled = candidate.LoraEnabled ?? LegacyPhotorealStyleLoraEnabled,
            Strength = Math.Clamp(candidate.Strength, 0.2, 0.8),
            StructureStrength = candidate.StructureStrength is double legacyStructure
                && double.IsFinite(legacyStructure)
                    ? legacyStructure
                    : null,
            CfgScale = candidate.CfgScale is double persistedCfgScale
                ? Math.Clamp(persistedCfgScale, 1, 2)
                : DefaultPhotorealCfgScale,
            Steps = candidate.Steps,
            MaxDimension = candidate.MaxDimension,
            Prompt = prompt,
            EmptyPrompt = emptyPrompt,
            NegativePrompt = negativePrompt,
            ExtensionData = candidate.ExtensionData is null
                ? null
                : new Dictionary<string, System.Text.Json.JsonElement>(
                    candidate.ExtensionData,
                    StringComparer.Ordinal),
        };
    }

    private static bool IsValidPhotorealStyleName(string name)
        => name.Length is >= 1 and <= MaxPhotorealStyleNameLength
            && !name.Any(char.IsControl);

    private PhotorealStyleState CreateCurrentPhotorealStyle(string name)
        => new()
        {
            Name = name,
            LoraEnabled = _modalPhotorealLoraEnabled,
            Strength = _modalPhotorealStrength,
            CfgScale = _modalPhotorealCfgScale,
            Steps = _modalPhotorealSteps,
            MaxDimension = _modalPhotorealMaxDimension,
            Prompt = _modalPhotorealPrompt,
            EmptyPrompt = _modalPhotorealEmptyPrompt,
            NegativePrompt = _modalPhotorealNegativePrompt,
        };

    private PhotorealStyleState? FindPhotorealStyle(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _photorealStyles.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool BuiltInPhotorealStyleMatchesCurrent(
        BuiltInPhotorealStyle style)
        => string.Equals(
                style.Prompt,
                _modalPhotorealPrompt,
                StringComparison.Ordinal)
            && string.Equals(
                style.Prompt,
                _modalPhotorealEmptyPrompt,
                StringComparison.Ordinal);

    private bool PhotorealStyleMatchesCurrent(PhotorealStyleState style)
        => (style.LoraEnabled ?? LegacyPhotorealStyleLoraEnabled) == _modalPhotorealLoraEnabled
            && Math.Abs(style.Strength - _modalPhotorealStrength) < 0.0001
            && Math.Abs((style.CfgScale ?? DefaultPhotorealCfgScale) - _modalPhotorealCfgScale) < 0.0001
            && style.Steps == _modalPhotorealSteps
            && style.MaxDimension == _modalPhotorealMaxDimension
            && string.Equals(style.Prompt, _modalPhotorealPrompt, StringComparison.Ordinal)
            && string.Equals(
                style.EmptyPrompt ?? DefaultPhotorealEmptyPrompt,
                _modalPhotorealEmptyPrompt,
                StringComparison.Ordinal)
            && string.Equals(
                style.NegativePrompt ?? DefaultPhotorealNegativePrompt,
                _modalPhotorealNegativePrompt,
                StringComparison.Ordinal);

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
            if (!_initializing)
                SaveAiStyles();
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
        choices.AddRange(BuiltInPhotorealStyles.Select(static style =>
            new PhotorealStyleChoice(style.Label, null, style.Id)));
        choices.AddRange(_photorealStyles.Select(static style =>
            new PhotorealStyleChoice(style.Name, style.Name)));
        PhotorealStyleChoice selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.SelectionKey,
                    _selectedPhotorealStyleName,
                    StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        bool wasSyncing = _syncingModalPhotorealSettings;
        _syncingModalPhotorealSettings = true;
        try
        {
            ModalPhotorealStyleComboBox.ItemsSource = choices;
            AppPhotorealStyleListBox.ItemsSource = choices;
            ModalPhotorealStyleComboBox.SelectedItem = selectedChoice;
            AppPhotorealStyleListBox.SelectedItem = selectedChoice;
            bool canDelete = selectedChoice.StyleName is not null
                && selectedChoice.BuiltInId is null;
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

        string loraSummary = _modalPhotorealLoraEnabled
            ? $"WarmBloodAban {Math.Round(_modalPhotorealStrength * 100):0}%"
            : "LoRA OFF";
        AppPhotorealStyleSummaryText.Text =
            $"現在: {loraSummary} / CFG {_modalPhotorealCfgScale:0.00} / {_modalPhotorealSteps} step / {_modalPhotorealMaxDimension} px";
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
                LoraEnabled = style.LoraEnabled,
                Strength = style.Strength,
                StructureStrength = style.StructureStrength,
                CfgScale = style.CfgScale,
                Steps = style.Steps,
                MaxDimension = style.MaxDimension,
                Prompt = style.Prompt,
                EmptyPrompt = style.EmptyPrompt,
                NegativePrompt = style.NegativePrompt,
                ExtensionData = style.ExtensionData is null
                    ? null
                    : new Dictionary<string, System.Text.Json.JsonElement>(
                        style.ExtensionData,
                        StringComparer.Ordinal),
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
        SetPhotorealPromptStatus("Positiveを初期値に戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void ResetPhotorealEmptyPrompt()
    {
        _modalPhotorealEmptyPrompt = DefaultPhotorealEmptyPrompt;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealPromptStatus("空欄時Positiveを初期値に戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void ResetPhotorealNegativePrompt()
    {
        _modalPhotorealNegativePrompt = DefaultPhotorealNegativePrompt;
        MarkPhotorealStyleAsCustom();
        SyncModalPhotorealSettingsControls();
        SetPhotorealPromptStatus("Negativeを初期値に戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void ResetAppPhotorealSettings_Click(object sender, RoutedEventArgs e)
    {
        _selectedPhotorealStyleName = null;
        RestoreModalPhotorealSettings(
            DefaultPhotorealStrength,
            DefaultPhotorealSteps,
            DefaultPhotorealMaxDimension,
            DefaultPhotorealPrompt,
            DefaultPhotorealCfgScale,
            DefaultPhotorealEmptyPrompt,
            DefaultPhotorealNegativePrompt,
            DefaultPhotorealLoraEnabled,
            DefaultPhotorealNegativePromptEnabled);
        RestorePhotorealSeedSettings(null, null);
        RefreshPhotorealStyleControls(updateNameFields: true);
        SetPhotorealSettingsStatus("実写化設定を既定値に戻しました。");
        if (!_initializing)
        {
            SaveAiStyles();
            SaveState();
        }
    }

    private void SetPhotorealSettingsStatus(string message)
    {
        if (AppPhotorealSettingsStatusText is not null)
            AppPhotorealSettingsStatusText.Text = message;
    }

    private void SetPhotorealSeedStatus(string message)
    {
        SetPhotorealSettingsStatus(message);
        if (ModalPhotorealSeedStatusText is not null)
            ModalPhotorealSeedStatusText.Text = message;
    }

    private void SetPhotorealPromptStatus(string message)
    {
        if (AppPhotorealPromptStatusText is not null)
            AppPhotorealPromptStatusText.Text = message;
        if (ModalPhotorealPromptStatusText is not null)
            ModalPhotorealPromptStatusText.Text = message;
    }

    private void SyncAppPhotorealPromptControls()
    {
        if (AppPhotorealPromptTextBox is null
            || AppPhotorealEmptyPromptTextBox is null
            || AppPhotorealNegativePromptTextBox is null)
            return;

        bool wasSyncing = _syncingModalPhotorealSettings;
        _syncingModalPhotorealSettings = true;
        try
        {
            if (!string.Equals(AppPhotorealPromptTextBox.Text, _modalPhotorealPrompt, StringComparison.Ordinal))
                AppPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            if (!string.Equals(AppPhotorealEmptyPromptTextBox.Text, _modalPhotorealEmptyPrompt, StringComparison.Ordinal))
                AppPhotorealEmptyPromptTextBox.Text = _modalPhotorealEmptyPrompt;
            if (!string.Equals(AppPhotorealNegativePromptTextBox.Text, _modalPhotorealNegativePrompt, StringComparison.Ordinal))
                AppPhotorealNegativePromptTextBox.Text = _modalPhotorealNegativePrompt;
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
        int? steps,
        int? maxDimension,
        string? prompt = null,
        double? cfgScale = null,
        string? emptyPrompt = null,
        string? negativePrompt = null,
        bool? loraEnabled = null,
        bool? negativePromptEnabled = null)
    {
        _modalPhotorealLoraEnabled = loraEnabled ?? DefaultPhotorealLoraEnabled;
        _modalPhotorealStrength = Math.Clamp(strength ?? DefaultPhotorealStrength, 0.2, 0.8);
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
        string restoredEmptyPrompt = emptyPrompt ?? DefaultPhotorealEmptyPrompt;
        _modalPhotorealEmptyPrompt = restoredEmptyPrompt.Length <= MaxPhotorealPromptLength
            ? restoredEmptyPrompt
            : restoredEmptyPrompt[..MaxPhotorealPromptLength];
        string restoredNegativePrompt = negativePrompt ?? DefaultPhotorealNegativePrompt;
        _modalPhotorealNegativePrompt = restoredNegativePrompt.Length <= MaxPhotorealPromptLength
            ? restoredNegativePrompt
            : restoredNegativePrompt[..MaxPhotorealPromptLength];
        if (negativePromptEnabled.HasValue)
            _modalPhotorealNegativePromptEnabled = negativePromptEnabled.Value;
        SyncModalPhotorealSettingsControls();
    }

    private void RestorePhotorealSeedSettings(string? mode, int? value)
    {
        _photorealSeedFixed = string.Equals(
            mode,
            FixedSeedMode,
            StringComparison.OrdinalIgnoreCase);
        _photorealSeedValueText = RestoreSeedValueText(
            _photorealSeedFixed,
            value);
        SyncPhotorealSeedControls();
    }

    private void SyncPhotorealSeedControls()
    {
        bool wasSyncing = _syncingModalPhotorealSettings;
        _syncingModalPhotorealSettings = true;
        try
        {
            if (ModalPhotorealSeedModeComboBox is not null)
                SelectSeedMode(ModalPhotorealSeedModeComboBox, _photorealSeedFixed);
            if (AppPhotorealSeedModeComboBox is not null)
                SelectSeedMode(AppPhotorealSeedModeComboBox, _photorealSeedFixed);
            if (ModalPhotorealSeedValueTextBox is not null)
            {
                ModalPhotorealSeedValueTextBox.Text = _photorealSeedValueText;
                ModalPhotorealSeedValueTextBox.IsEnabled = _photorealSeedFixed;
            }
            if (AppPhotorealSeedValueTextBox is not null)
            {
                AppPhotorealSeedValueTextBox.Text = _photorealSeedValueText;
                AppPhotorealSeedValueTextBox.IsEnabled = _photorealSeedFixed;
            }
        }
        finally
        {
            _syncingModalPhotorealSettings = wasSyncing;
        }
    }

    private void SyncModalPhotorealSettingsControls()
    {
        if (ModalPhotorealLoraEnabledCheckBox is null
            || AppPhotorealLoraEnabledCheckBox is null
            || ModalPhotorealNegativePromptEnabledCheckBox is null
            || AppPhotorealNegativePromptEnabledCheckBox is null
            || ModalPhotorealStrengthSlider is null
            || ModalPhotorealCfgScaleSlider is null
            || ModalPhotorealStepsComboBox is null
            || ModalPhotorealSizeComboBox is null
            || ModalPhotorealPromptTextBox is null
            || ModalPhotorealEmptyPromptTextBox is null
            || ModalPhotorealNegativePromptTextBox is null)
        {
            return;
        }

        _syncingModalPhotorealSettings = true;
        try
        {
            ModalPhotorealLoraEnabledCheckBox.IsChecked = _modalPhotorealLoraEnabled;
            AppPhotorealLoraEnabledCheckBox.IsChecked = _modalPhotorealLoraEnabled;
            ModalPhotorealNegativePromptEnabledCheckBox.IsChecked =
                _modalPhotorealNegativePromptEnabled;
            AppPhotorealNegativePromptEnabledCheckBox.IsChecked =
                _modalPhotorealNegativePromptEnabled;
            ModalPhotorealStrengthSlider.Value = _modalPhotorealStrength * 100;
            ModalPhotorealStrengthSlider.IsEnabled = _modalPhotorealLoraEnabled;
            ModalPhotorealCfgScaleSlider.Value = _modalPhotorealCfgScale * 100;
            SelectIntegerTag(ModalPhotorealStepsComboBox, _modalPhotorealSteps);
            SelectIntegerTag(ModalPhotorealSizeComboBox, _modalPhotorealMaxDimension);
            ModalPhotorealPromptTextBox.Text = _modalPhotorealPrompt;
            ModalPhotorealEmptyPromptTextBox.Text = _modalPhotorealEmptyPrompt;
            ModalPhotorealNegativePromptTextBox.Text = _modalPhotorealNegativePrompt;
            SyncAppPhotorealPromptControls();
            if (AppPhotorealStrengthSlider is not null)
            {
                AppPhotorealStrengthSlider.Value = _modalPhotorealStrength * 100;
                AppPhotorealStrengthSlider.IsEnabled = _modalPhotorealLoraEnabled;
            }
            if (AppPhotorealCfgScaleSlider is not null)
                AppPhotorealCfgScaleSlider.Value = _modalPhotorealCfgScale * 100;
            if (AppPhotorealStepsComboBox is not null)
                SelectIntegerTag(AppPhotorealStepsComboBox, _modalPhotorealSteps);
            if (AppPhotorealSizeComboBox is not null)
                SelectIntegerTag(AppPhotorealSizeComboBox, _modalPhotorealMaxDimension);
            SyncPhotorealSeedControls();
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
        if (ModalPhotorealCfgScaleValue is not null)
            ModalPhotorealCfgScaleValue.Text = _modalPhotorealCfgScale.ToString("0.00", CultureInfo.InvariantCulture);
        if (AppPhotorealStrengthValue is not null)
            AppPhotorealStrengthValue.Text = $"{Math.Round(_modalPhotorealStrength * 100):0}%";
        if (AppPhotorealCfgScaleValue is not null)
            AppPhotorealCfgScaleValue.Text = _modalPhotorealCfgScale.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public (bool LoraEnabled, double Strength, int Steps, double CfgScale, int MaxDimension, string Prompt, string EmptyPrompt, string NegativePrompt, bool NegativePromptEnabled, string EffectivePrompt, string EffectiveNegativePrompt) ModalPhotorealSettingsForSmoke
        => (
            _modalPhotorealLoraEnabled,
            _modalPhotorealStrength,
            _modalPhotorealSteps,
            _modalPhotorealCfgScale,
            _modalPhotorealMaxDimension,
            _modalPhotorealPrompt,
            _modalPhotorealEmptyPrompt,
            _modalPhotorealNegativePrompt,
            _modalPhotorealNegativePromptEnabled,
            CurrentModalPhotorealRequestSettings().Prompt,
            CurrentModalPhotorealRequestSettings().NegativePrompt);

    public (bool Fixed, string Value, bool Valid) PhotorealSeedForSmoke
        => (
            _photorealSeedFixed,
            _photorealSeedValueText,
            !_photorealSeedFixed
                || TryParseFixedSeed(_photorealSeedValueText, out _));

    public string PhotorealSeedStatusForSmoke
        => ModalPhotorealSeedStatusText?.Text
            ?? AppPhotorealSettingsStatusText?.Text
            ?? "";

    public bool PhotorealSeedSurfaceForSmoke
        => ModalPhotorealSeedModeComboBox is not null
            && ModalPhotorealSeedValueTextBox is not null
            && AppPhotorealSeedModeComboBox is not null
            && AppPhotorealSeedValueTextBox is not null
            && ModalPhotorealSeedValueTextBox.MaxLength == 10
            && AppPhotorealSeedValueTextBox.MaxLength == 10
            && AutomationProperties.GetName(ModalPhotorealSeedModeComboBox)
                == "AI photorealization seed mode"
            && AutomationProperties.GetName(AppPhotorealSeedModeComboBox)
                == "Default AI photorealization seed mode";

    public string ModalEnhancementOperationForSmoke => _modalEnhancementOperation;

    public (
        bool Ok,
        string? SourceProducerJobId,
        string? SourceRecoveredOutputPath,
        string PresetId,
        string AdapterId,
        double Scale,
        string Error) ModalUpscaleProfileForSmoke
    {
        get
        {
            if (SelectedTile() is not Tile tile)
                return (false, null, null, "", "", 0, "No selected tile");
            bool ok = TryResolveModalUpscaleProfile(
                tile,
                out UpscaleRequestSource requestSource,
                out string presetId,
                out string adapterId,
                out double scale,
                out string error);
            return (
                ok,
                requestSource.SourceProducerJobId,
                requestSource.SourceRecoveredOutputPath,
                presetId,
                adapterId,
                scale,
                error);
        }
    }

    public bool ModalHqButtonEnabledForSmoke => ModalEnhanceButton.IsEnabled;

    public bool ModalPhotorealUpscaleButtonEnabledForSmoke =>
        ModalPhotorealUpscaleButton.IsEnabled;

    public bool SelectModalOriginalVersionForSmoke()
        => ApplyModalDisplayVersionChoice(
            new ModalDisplayVersionChoice(
                ModalDisplayVersionKind.Original,
                0,
                "Original"),
            showFeedback: false);

    public async Task<bool> StartModalPhotorealUpscaleForSmokeAsync(
        int timeoutMilliseconds = 3000)
    {
        StartModalPhotorealUpscale_Click(this, new RoutedEventArgs());
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(
            timeoutMilliseconds);
        return completed && _modalEnhancementJobStatus is "queued" or "running";
    }

    public bool ModalPhotorealToolbarContractForSmoke
        => !ReferenceEquals(ModalEnhanceButton, ModalPhotorealButton)
            && string.Equals(
                ModalEnhanceButtonLabel.Text,
                "HQ",
                StringComparison.Ordinal)
            && string.Equals(ModalPhotorealButtonLabel.Text, "実写化", StringComparison.Ordinal)
            && AutomationProperties.GetName(ModalPhotorealButton) == "実写化"
            && ModalPhotorealSettingsPopup is not null
            && GalleryContextMenuContractForSmoke;

    public void ConfigureModalPhotorealSettingsForSmoke(
        double strength,
        int steps,
        int maxDimension,
        string prompt = "",
        double cfgScale = DefaultPhotorealCfgScale,
        string? emptyPrompt = null,
        string? negativePrompt = null,
        bool? loraEnabled = null,
        bool? negativePromptEnabled = null)
    {
        RestoreModalPhotorealSettings(
            strength,
            steps,
            maxDimension,
            prompt,
            cfgScale,
            emptyPrompt,
            negativePrompt,
            loraEnabled,
            negativePromptEnabled);
        MarkPhotorealStyleAsCustom();
    }

    public void ConfigurePhotorealSeedForSmoke(bool fixedMode, string value)
    {
        _photorealSeedFixed = fixedMode;
        _photorealSeedValueText = value;
        SyncPhotorealSeedControls();
        if (!_initializing)
            SaveState();
    }

    public void SetModalPhotorealNegativePromptEnabledForSmoke(bool enabled)
    {
        ModalPhotorealNegativePromptEnabledCheckBox.IsChecked = enabled;
    }

    public void ResetModalPhotorealPromptForSmoke()
        => ResetModalPhotorealPrompt_Click(this, new RoutedEventArgs());

    public void ResetModalPhotorealEmptyPromptForSmoke()
        => ResetModalPhotorealEmptyPrompt_Click(this, new RoutedEventArgs());

    public void ResetModalPhotorealNegativePromptForSmoke()
        => ResetModalPhotorealNegativePrompt_Click(this, new RoutedEventArgs());

    public bool AppPhotorealPromptSurfaceForSmoke
        => AppPhotorealPromptTextBox.MaxLength == 2_000
            && AppPhotorealPromptTextBox.AcceptsReturn
            && string.Equals(
                AutomationProperties.GetName(AppPhotorealPromptTextBox),
                "Default AI photorealization positive prompt",
                StringComparison.Ordinal)
            && AppPhotorealEmptyPromptTextBox.MaxLength == 2_000
            && AppPhotorealEmptyPromptTextBox.AcceptsReturn
            && string.Equals(
                AutomationProperties.GetName(AppPhotorealEmptyPromptTextBox),
                "AI photorealization positive prompt used when blank",
                StringComparison.Ordinal)
            && AppPhotorealNegativePromptTextBox.MaxLength == 2_000
            && AppPhotorealNegativePromptTextBox.AcceptsReturn
            && string.Equals(
                AutomationProperties.GetName(AppPhotorealNegativePromptTextBox),
                "Default AI photorealization negative prompt",
                StringComparison.Ordinal)
            && AutomationProperties.GetHelpText(AppPhotorealNegativePromptTextBox)
                ?.Contains("sent only when", StringComparison.Ordinal) == true
            && AutomationProperties.GetName(AppPhotorealNegativePromptEnabledCheckBox)
                == "Send negative conditioning for AI photorealization"
            && AutomationProperties.GetName(ModalPhotorealNegativePromptEnabledCheckBox)
                == "Send negative conditioning for this AI photorealization"
            && string.Equals(AppPhotorealPromptTextBox.Text, _modalPhotorealPrompt, StringComparison.Ordinal);

    public bool AppPhotorealSettingsSurfaceForSmoke
        => AutomationProperties.GetName(AppPhotorealLoraEnabledCheckBox)
                == "Use WarmBloodAban Anything-to-Real LoRA for comparison by default"
            && AutomationProperties.GetName(ModalPhotorealLoraEnabledCheckBox)
                == "Use WarmBloodAban Anything-to-Real LoRA for comparison"
            && AppPhotorealStrengthSlider.Minimum == 20
            && AppPhotorealStrengthSlider.Maximum == 80
            && AppPhotorealCfgScaleSlider.Minimum == 100
            && AppPhotorealCfgScaleSlider.Maximum == 200
            && AppPhotorealStepsComboBox.Items.Count == 4
            && AppPhotorealSizeComboBox.Items.Count == 3;

    public (bool AppChecked, bool ModalChecked, bool AppStrengthEnabled, bool ModalStrengthEnabled) PhotorealLoraControlsForSmoke
        => (
            AppPhotorealLoraEnabledCheckBox.IsChecked == true,
            ModalPhotorealLoraEnabledCheckBox.IsChecked == true,
            AppPhotorealStrengthSlider.IsEnabled,
            ModalPhotorealStrengthSlider.IsEnabled);

    public bool PhotorealStyleSurfaceForSmoke
        => ModalPhotorealStyleComboBox is not null
            && AppPhotorealStyleListBox is not null
            && ModalPhotorealStyleNameTextBox.MaxLength == MaxPhotorealStyleNameLength
            && AppPhotorealStyleNameTextBox.MaxLength == MaxPhotorealStyleNameLength
            && AutomationProperties.GetName(ModalPhotorealStyleComboBox)
                == "AI photorealization style"
            && AutomationProperties.GetName(AppPhotorealStyleListBox)
                == "AI photorealization styles";

    public IReadOnlyList<string> PhotorealStyleNamesForSmoke
        => _photorealStyles.Select(static style => style.Name).ToList();

    public IReadOnlyList<string> BuiltInPhotorealStyleIdsForSmoke
        => BuiltInPhotorealStyles.Select(static style => style.Id).ToList();

    public string? SelectedBuiltInPhotorealStyleIdForSmoke
        => FindBuiltInPhotorealStyle(_selectedPhotorealStyleName)?.Id;

    public bool BuiltInPhotorealStyleDeleteDisabledForSmoke
        => DeleteModalPhotorealStyleButton.IsEnabled == false
            && DeleteAppPhotorealStyleButton.IsEnabled == false;

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

    public bool SelectBuiltInPhotorealStyleForSmoke(string id)
    {
        PhotorealStyleChoice? choice = ModalPhotorealStyleComboBox.Items
            .OfType<PhotorealStyleChoice>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.BuiltInId, id, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return false;

        ModalPhotorealStyleComboBox.SelectedItem = choice;
        return string.Equals(
            _selectedPhotorealStyleName,
            BuiltInPhotorealStylePrefix + id,
            StringComparison.OrdinalIgnoreCase);
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

    public void SetAppPhotorealEmptyPromptForSmoke(string prompt)
        => AppPhotorealEmptyPromptTextBox.Text = prompt;

    public void SetAppPhotorealNegativePromptForSmoke(string prompt)
        => AppPhotorealNegativePromptTextBox.Text = prompt;

    public void ResetAppPhotorealPromptForSmoke()
        => ResetAppPhotorealPrompt_Click(this, new RoutedEventArgs());

    public void ResetAppPhotorealEmptyPromptForSmoke()
        => ResetAppPhotorealEmptyPrompt_Click(this, new RoutedEventArgs());

    public void ResetAppPhotorealNegativePromptForSmoke()
        => ResetAppPhotorealNegativePrompt_Click(this, new RoutedEventArgs());

    public void ResetAppPhotorealSettingsForSmoke()
        => ResetAppPhotorealSettings_Click(this, new RoutedEventArgs());

    public string DefaultModalPhotorealPromptForSmoke => DefaultPhotorealPrompt;
    public string DefaultModalPhotorealEmptyPromptForSmoke => DefaultPhotorealEmptyPrompt;
    public string DefaultModalPhotorealNegativePromptForSmoke => DefaultPhotorealNegativePrompt;

    public async Task<bool> StartModalPhotorealForSmokeAsync(int timeoutMilliseconds = 3000)
    {
        StartModalPhotoreal_Click(this, new RoutedEventArgs());
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(timeoutMilliseconds);
        return completed && !string.IsNullOrWhiteSpace(_modalEnhancementJobId);
    }

    public async Task<bool> StartModalPhotorealWithShortcutForSmokeAsync(int timeoutMilliseconds = 3000)
    {
        bool handled = InvokePreviewKeyForSmoke(Key.R, ModifierKeys.None);
        bool completed = await WaitForModalEnhancementRequestForSmokeAsync(timeoutMilliseconds);
        return handled && completed && !string.IsNullOrWhiteSpace(_modalEnhancementJobId);
    }
}

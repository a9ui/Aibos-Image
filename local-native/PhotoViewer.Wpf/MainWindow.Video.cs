using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string ManagedVideoFolderName = "Videos";
    private const string ManagedVideoExtension = ".mp4";
    private const int ManagedVideoAlignment = 32;
    private const long ManagedVideoMaximumPixelArea = 409_600;

    private readonly Dictionary<string, List<ManagedVideoVersion>> _videoVersions =
        new(EnhancementSourceIdentityComparer);
    private readonly Dictionary<string, List<ManagedVideoVersion>> _catalogVideoVersionsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagedVideoVersion> _modalVideoVersions = [];
    private int _modalVideoVersionIndex;
    private int _videoCandidateCount;
    private bool _modalShowingVideo;
    private bool _modalVideoPlaying;
    private bool _modalVideoAutoplayPending;
    private bool _suppressModalVideoVersionSelection;
    private bool _modalVideoTransportStubForSmoke;
    private TaskCompletionSource<bool>? _modalVideoMediaOpenCompletion;
    private string? _modalVideoMediaFailureForSmoke;

    private sealed record ManagedVideoOutput(
        string OutputPath,
        long SourceSize,
        double SourceMtimeMs);

    private sealed record ManagedVideoVersion(
        string JobId,
        string PresetId,
        string BackendId,
        double DurationSeconds,
        int PlaybackFps,
        int FrameCount,
        int Width,
        int Height,
        string RequestedPrompt,
        string PositivePrompt,
        string NegativePrompt,
        int Seed,
        string Codec,
        int BitDepth,
        DateTimeOffset CreatedAt,
        ManagedVideoOutput Output);

    private sealed record ModalVideoVersionChoice(int Index, string Label);

    private bool TryBuildManagedVideoVersion(
        JsonElement job,
        out string resolvedSource,
        out ManagedVideoVersion version,
        out IReadOnlyList<string> catalogAliases)
    {
        resolvedSource = "";
        version = null!;
        catalogAliases = [];
        if (!TryGetStringProperty(job, "id", out string? jobId)
            || !TryGetStringProperty(job, "sourcePath", out string? sourcePath)
            || !TryGetStringProperty(job, "sourceId", out string? sourceId)
            || !TryGetStringProperty(job, "outputPath", out string? outputPath)
            || !TryGetExactStringProperty(job, "mediaKind", "video")
            || !job.TryGetProperty("sourceSignature", out JsonElement signature)
            || signature.ValueKind != JsonValueKind.Object
            || !signature.TryGetProperty("size", out JsonElement sizeElement)
            || !sizeElement.TryGetInt64(out long sourceSize)
            || !signature.TryGetProperty("mtimeMs", out JsonElement mtimeElement)
            || !mtimeElement.TryGetDouble(out double sourceMtimeMs)
            || !job.TryGetProperty("video", out JsonElement video)
            || video.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(video, "presetId", out string? presetId)
            || !TryGetStringProperty(video, "backendId", out string? backendId)
            || !video.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !requested.TryGetProperty("durationSeconds", out JsonElement durationElement)
            || !durationElement.TryGetDouble(out double durationSeconds)
            || !requested.TryGetProperty("playbackFps", out JsonElement fpsElement)
            || !fpsElement.TryGetInt32(out int playbackFps)
            || !TryGetStringPropertyAllowEmpty(requested, "prompt", out string? requestedPrompt)
            || !video.TryGetProperty("effective", out JsonElement effective)
            || effective.ValueKind != JsonValueKind.Object
            || !effective.TryGetProperty("frameCount", out JsonElement frameCountElement)
            || !frameCountElement.TryGetInt32(out int frameCount)
            || !effective.TryGetProperty("width", out JsonElement widthElement)
            || !widthElement.TryGetInt32(out int width)
            || !effective.TryGetProperty("height", out JsonElement heightElement)
            || !heightElement.TryGetInt32(out int height)
            || !TryGetStringProperty(effective, "positivePrompt", out string? positivePrompt)
            || !TryGetStringPropertyAllowEmpty(effective, "negativePrompt", out string? negativePrompt)
            || !video.TryGetProperty("seed", out JsonElement seedElement)
            || !seedElement.TryGetInt32(out int seed)
            || !TryGetStringProperty(video, "codec", out string? codec)
            || !video.TryGetProperty("bitDepth", out JsonElement bitDepthElement)
            || !bitDepthElement.TryGetInt32(out int bitDepth))
        {
            return false;
        }

        if (!double.IsFinite(durationSeconds)
            || durationSeconds <= 0
            || durationSeconds > 60
            || playbackFps <= 0
            || playbackFps > 60
            || frameCount != checked((int)(4 * Math.Floor(durationSeconds * playbackFps / 4d) + 1))
            || width < ManagedVideoAlignment
            || height < ManagedVideoAlignment
            || width % ManagedVideoAlignment != 0
            || height % ManagedVideoAlignment != 0
            || checked((long)width * height) > ManagedVideoMaximumPixelArea
            || seed < 0
            || !string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)
            || bitDepth != 8)
        {
            return false;
        }

        int outputPlaybackFps = playbackFps;
        int outputFrameCount = frameCount;
        if (video.TryGetProperty("delivery", out _))
        {
            if (video.EnumerateObject().Count(static property =>
                    property.NameEquals("delivery")) != 1
                || !durationElement.TryGetInt32(
                    out int deliveryDurationSeconds)
                || durationSeconds != deliveryDurationSeconds
                || deliveryDurationSeconds is not (4 or 6)
                || !IsVideoDeliveryMutationSafe(
                    video,
                    deliveryDurationSeconds))
            {
                return false;
            }

            outputPlaybackFps = 30;
            outputFrameCount = checked(deliveryDurationSeconds * 30);
        }

        try
        {
            if (!TryResolveEnhancementSourceIdentity(sourcePath, out string resolvedSourcePath)
                || !TryResolveEnhancementSourceIdentity(sourceId, out string resolvedSourceId)
                || !EnhancementSourceIdentityComparer.Equals(resolvedSourcePath, resolvedSourceId)
                || !File.Exists(resolvedSourcePath))
            {
                return false;
            }

            var sourceInfo = new FileInfo(resolvedSourcePath);
            double currentMtimeMs =
                new DateTimeOffset(sourceInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (sourceInfo.Length != sourceSize || Math.Abs(currentMtimeMs - sourceMtimeMs) > 1)
                return false;

            string lexicalOutput = Path.GetFullPath(outputPath!);
            string canonicalOutput = _resolveFinalPath(lexicalOutput);
            string lexicalRoot = Path.GetFullPath(
                Path.Combine(ResolvedManagedEnhancementOutputsRoot, ManagedVideoFolderName));
            string canonicalRoot = _resolveFinalPath(lexicalRoot);
            if (!string.Equals(
                    Path.GetDirectoryName(lexicalOutput),
                    lexicalRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(canonicalOutput),
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetExtension(canonicalOutput),
                    ManagedVideoExtension,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(canonicalOutput)
                || new FileInfo(canonicalOutput).Length <= 0)
            {
                return false;
            }

            DateTimeOffset createdAt = DateTimeOffset.MinValue;
            if (TryGetStringProperty(job, "createdAt", out string? createdAtText))
                DateTimeOffset.TryParse(
                    createdAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out createdAt);

            resolvedSource = resolvedSourcePath;
            version = new ManagedVideoVersion(
                jobId!,
                presetId!,
                backendId!,
                durationSeconds,
                outputPlaybackFps,
                outputFrameCount,
                width,
                height,
                requestedPrompt!,
                positivePrompt!,
                negativePrompt!,
                seed,
                codec!.ToLowerInvariant(),
                bitDepth,
                createdAt,
                new ManagedVideoOutput(canonicalOutput, sourceSize, sourceMtimeMs));
            catalogAliases = new[] { sourcePath, sourceId, resolvedSourcePath }
                .Select(NormalizeCatalogEnhancementPath)
                .Where(static path => path is not null)
                .Select(static path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetExactStringProperty(
        JsonElement element,
        string propertyName,
        string expected)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool TryGetStringPropertyAllowEmpty(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

    private IReadOnlyList<ManagedVideoVersion> GetManagedVideoVersionsForPath(string path)
    {
        string? alias = NormalizeCatalogEnhancementPath(path);
        if (alias is not null
            && _catalogVideoVersionsByPath.TryGetValue(
                alias,
                out List<ManagedVideoVersion>? catalogVersions))
        {
            return catalogVersions;
        }

        if (TryResolveEnhancementSourceIdentity(path, out string identity)
            && _videoVersions.TryGetValue(identity, out List<ManagedVideoVersion>? versions))
        {
            return versions;
        }

        return Array.Empty<ManagedVideoVersion>();
    }

    private IReadOnlyList<ManagedVideoVersion> GetCatalogManagedVideoVersionsForPath(string path)
    {
        string? alias = NormalizeCatalogEnhancementPath(path);
        return alias is not null
            && _catalogVideoVersionsByPath.TryGetValue(
                alias,
                out List<ManagedVideoVersion>? versions)
            ? versions
            : Array.Empty<ManagedVideoVersion>();
    }

    private void ApplyTileVideoAvailability(Tile tile)
    {
        IReadOnlyList<ManagedVideoVersion> versions =
            GetCatalogManagedVideoVersionsForPath(tile.Path);
        tile.VideoGenerated = versions.Count > 0;
        tile.VideoOutputPath = versions.Count > 0
            ? versions[0].Output.OutputPath
            : null;
    }

    private void InitializeModalVideoVersions(Tile tile)
    {
        string? selectedJobId = _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count
                ? _modalVideoVersions[_modalVideoVersionIndex].JobId
                : null;
        _modalVideoVersions.Clear();
        _modalVideoVersions.AddRange(GetManagedVideoVersionsForPath(tile.Path));
        _modalVideoVersionIndex = selectedJobId is null
            ? 0
            : Math.Max(
                0,
                _modalVideoVersions.FindIndex(version =>
                    string.Equals(version.JobId, selectedJobId, StringComparison.Ordinal)));
        RefreshModalVideoVersionChoices();
    }

    private void ClearModalVideoVersions()
    {
        StopAndHideModalVideo(clearSource: true);
        _modalVideoVersions.Clear();
        _modalVideoVersionIndex = 0;
        RefreshModalVideoVersionChoices();
    }

    private void RefreshModalVideoVersionChoices()
    {
        if (ModalVideoVersionComboBox is null || ModalVideoPlaybackButton is null)
            return;

        _suppressModalVideoVersionSelection = true;
        try
        {
            ModalVideoVersionComboBox.ItemsSource = _modalVideoVersions
                .Select((version, index) => new ModalVideoVersionChoice(
                    index,
                    $"V{index + 1} · {version.DurationSeconds:0.#}s · "
                        + $"{version.PlaybackFps}fps · {version.FrameCount}f · "
                        + $"{version.Width}×{version.Height}"))
                .ToArray();
            ModalVideoVersionComboBox.SelectedIndex = _modalVideoVersions.Count == 0
                ? -1
                : Math.Clamp(_modalVideoVersionIndex, 0, _modalVideoVersions.Count - 1);
        }
        finally
        {
            _suppressModalVideoVersionSelection = false;
        }

        bool available = _modalVideoVersions.Count > 0;
        ModalVideoVersionComboBox.Visibility = available
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoPlaybackButton.Visibility = available
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoPlaybackButton.IsEnabled = available;
        UpdateModalVideoPlaybackPresentation();
    }

    private bool ShouldAutoplayModalVideo()
        => VideoOnlyFilter?.IsChecked == true && _modalVideoVersions.Count > 0;

    private bool ShowModalVideoVersion(int index, bool autoplay)
    {
        if (Modal.Visibility != Visibility.Visible
            || index < 0
            || index >= _modalVideoVersions.Count)
        {
            return false;
        }

        ManagedVideoVersion version = _modalVideoVersions[index];
        if (!File.Exists(version.Output.OutputPath))
            return false;

        _modalVideoVersionIndex = index;
        _modalShowingVideo = true;
        _modalVideoPlaying = autoplay;
        _modalVideoAutoplayPending = autoplay;
        _modalVideoMediaFailureForSmoke = null;
        _modalVideoMediaOpenCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        ModalVideo.Visibility = Visibility.Visible;
        ModalBitmap.Visibility = Visibility.Collapsed;
        ModalArtBase.Visibility = Visibility.Collapsed;
        ModalArtGlow.Visibility = Visibility.Collapsed;

        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Stop();
                ModalVideo.Source = new Uri(version.Output.OutputPath, UriKind.Absolute);
                if (autoplay)
                    ModalVideo.Play();
                else
                    ModalVideo.Pause();
            }
            catch (Exception ex)
            {
                _modalVideoMediaFailureForSmoke = ex.Message;
                _modalVideoMediaOpenCompletion.TrySetResult(false);
                StopAndHideModalVideo(clearSource: true);
                return false;
            }
        }
        else
        {
            _modalVideoMediaOpenCompletion.TrySetResult(true);
        }

        RefreshModalVideoVersionChoices();
        ModalVideoVersionComboBox.SelectedIndex = index;
        ModalSourceLabel.Text = $"Video V{index + 1}";
        ModalFileSizeText.Text = FormatFileSizeMb(new FileInfo(version.Output.OutputPath).Length);
        return true;
    }

    private void StopAndHideModalVideo(bool clearSource)
    {
        if (ModalVideo is not null && !_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Stop();
                if (clearSource)
                    ModalVideo.Source = null;
            }
            catch
            {
            }
        }

        _modalShowingVideo = false;
        _modalVideoPlaying = false;
        _modalVideoAutoplayPending = false;
        if (ModalVideo is not null)
            ModalVideo.Visibility = Visibility.Collapsed;
        RestoreModalImageVisibility();
        if (Modal?.Visibility == Visibility.Visible)
        {
            bool canShowEnhanced = SelectedTile() is Tile selected
                && TryGetModalEnhancedOutput(selected, out _);
            UpdateModalEnhancedControls(canShowEnhanced);
            if (!string.IsNullOrWhiteSpace(_modalDisplayPath)
                && File.Exists(_modalDisplayPath))
            {
                ModalFileSizeText.Text =
                    FormatFileSizeMb(new FileInfo(_modalDisplayPath).Length);
            }
        }
        UpdateModalVideoPlaybackPresentation();
    }

    private void RestoreModalImageVisibility()
    {
        if (ModalBitmap is null || ModalArtBase is null || ModalArtGlow is null)
            return;

        bool hasBitmap = ModalBitmap.Source is not null;
        ModalBitmap.Visibility = hasBitmap ? Visibility.Visible : Visibility.Collapsed;
        ModalArtBase.Visibility = hasBitmap ? Visibility.Collapsed : Visibility.Visible;
        ModalArtGlow.Visibility = hasBitmap ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool ToggleModalVideoPlayback()
    {
        if (Modal.Visibility != Visibility.Visible || _modalVideoVersions.Count == 0)
            return false;

        if (!_modalShowingVideo)
            return ShowModalVideoVersion(_modalVideoVersionIndex, autoplay: true);

        try
        {
            if (_modalVideoPlaying)
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Pause();
                _modalVideoPlaying = false;
                _modalVideoAutoplayPending = false;
            }
            else
            {
                if (!_modalVideoTransportStubForSmoke)
                    ModalVideo.Play();
                _modalVideoPlaying = true;
                _modalVideoAutoplayPending = true;
            }
        }
        catch
        {
            StopAndHideModalVideo(clearSource: true);
            return false;
        }

        UpdateModalVideoPlaybackPresentation();
        return true;
    }

    private void UpdateModalVideoPlaybackPresentation()
    {
        if (ModalVideoPlaybackButtonLabel is null || ModalVideoPlaybackButton is null)
            return;

        ModalVideoPlaybackButtonLabel.Text = _modalShowingVideo && _modalVideoPlaying
            ? "一時停止"
            : "動画再生";
        string shortcut = BindingText(ViewerKeyAction.ToggleVideoPlayback);
        ModalVideoPlaybackButton.ToolTip =
            $"動画を再生 / 一時停止 ({shortcut})";
        AutomationProperties.SetName(
            ModalVideoPlaybackButton,
            _modalShowingVideo && _modalVideoPlaying
                ? "Pause generated video"
                : "Play generated video");
    }

    private void ModalVideoPlayback_Click(object sender, RoutedEventArgs e)
        => ToggleModalVideoPlayback();

    private void ModalVideoVersion_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressModalVideoVersionSelection
            || ModalVideoVersionComboBox.SelectedItem is not ModalVideoVersionChoice choice
            || choice.Index < 0
            || choice.Index >= _modalVideoVersions.Count)
        {
            return;
        }

        _modalVideoVersionIndex = choice.Index;
        if (_modalShowingVideo)
            ShowModalVideoVersion(choice.Index, autoplay: true);
    }

    private void ModalVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        if (_modalVideoAutoplayPending)
            ModalVideo.Play();
        _modalVideoPlaying = _modalVideoAutoplayPending;
        _modalVideoMediaOpenCompletion?.TrySetResult(true);
        UpdateModalVideoPlaybackPresentation();
    }

    private void ModalVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        ModalVideo.Position = TimeSpan.Zero;
        ModalVideo.Pause();
        _modalVideoPlaying = false;
        _modalVideoAutoplayPending = false;
        UpdateModalVideoPlaybackPresentation();
    }

    private void ModalVideo_MediaFailed(
        object sender,
        System.Windows.ExceptionRoutedEventArgs e)
    {
        if (!_modalShowingVideo)
            return;

        _modalVideoMediaFailureForSmoke =
            e.ErrorException?.Message ?? "Media Foundation rejected the video.";
        _modalVideoMediaOpenCompletion?.TrySetResult(false);
        StopAndHideModalVideo(clearSource: true);
        SetStatusToast("動画を再生できません。元画像を表示します。");
    }

    public void SetVideoOnlyFilterForSmoke(bool enabled)
    {
        VideoOnlyFilter.IsChecked = enabled;
        ApplyFilters();
    }

    public bool VideoGeneratedForFileForSmoke(string fileName)
        => _allTiles.FirstOrDefault(tile =>
            string.Equals(tile.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.VideoGenerated == true;

    public string? VideoOutputPathForFileForSmoke(string fileName)
        => _allTiles.FirstOrDefault(tile =>
            string.Equals(tile.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.VideoOutputPath;

    public int VideoCandidateCountForSmoke => _videoCandidateCount;
    public int VideoVersionCountForSmoke => _modalVideoVersions.Count;
    public int ModalVideoVersionIndexForSmoke => _modalVideoVersionIndex;
    public bool ModalShowingVideoForSmoke => _modalShowingVideo;
    public bool ModalVideoPlayingForSmoke => _modalVideoPlaying;
    public string? ModalVideoPathForSmoke =>
        _modalVideoVersionIndex >= 0 && _modalVideoVersionIndex < _modalVideoVersions.Count
            ? _modalVideoVersions[_modalVideoVersionIndex].Output.OutputPath
            : null;
    public string? ModalVideoMediaFailureForSmoke =>
        _modalVideoMediaFailureForSmoke;
    public string[] ModalVideoVersionLabelsForSmoke =>
        ModalVideoVersionComboBox.Items
            .OfType<ModalVideoVersionChoice>()
            .Select(static choice => choice.Label)
            .ToArray();
    public (int PlaybackFps, int FrameCount)[]
        ModalVideoVersionPlaybackMetadataForSmoke =>
            _modalVideoVersions
                .Select(static version =>
                    (version.PlaybackFps, version.FrameCount))
                .ToArray();
    public bool ModalVideoHasNaturalDurationForSmoke =>
        _modalVideoTransportStubForSmoke
        || (ModalVideo.NaturalDuration.HasTimeSpan
            && ModalVideo.NaturalDuration.TimeSpan > TimeSpan.Zero);

    public void EnableModalVideoTransportStubForSmoke()
        => _modalVideoTransportStubForSmoke = true;

    public async Task<bool> WaitForModalVideoMediaOpenedForSmokeAsync(
        int timeoutMilliseconds = 10_000)
    {
        TaskCompletionSource<bool>? completion =
            _modalVideoMediaOpenCompletion;
        if (completion is null)
            return false;

        try
        {
            return await completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(
                    Math.Max(1, timeoutMilliseconds)));
        }
        catch (TimeoutException)
        {
            _modalVideoMediaFailureForSmoke =
                "Timed out waiting for MediaOpened.";
            return false;
        }
    }

    public async Task<bool> WaitForModalVideoPlaybackProgressForSmokeAsync(
        int timeoutMilliseconds = 5_000)
    {
        if (_modalVideoTransportStubForSmoke)
            return true;

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            Math.Max(1, timeoutMilliseconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!_modalShowingVideo || !_modalVideoPlaying)
                return false;
            if (ModalVideo.Position > TimeSpan.Zero)
                return true;
            await Task.Delay(50);
        }
        _modalVideoMediaFailureForSmoke =
            "Timed out waiting for video playback progress.";
        return false;
    }

    public async Task<bool> WaitForModalVideoPauseSettledForSmokeAsync()
    {
        if (_modalVideoTransportStubForSmoke)
            return !_modalVideoPlaying;
        if (!_modalShowingVideo || _modalVideoPlaying)
            return false;

        TimeSpan before = ModalVideo.Position;
        await Task.Delay(300);
        TimeSpan after = ModalVideo.Position;
        return _modalShowingVideo
            && !_modalVideoPlaying
            && Math.Abs((after - before).TotalMilliseconds) <= 100;
    }

    public bool ToggleModalVideoPlaybackForSmoke()
        => ToggleModalVideoPlayback();

    public bool SelectModalVideoVersionForSmoke(int index)
    {
        if (index < 0 || index >= _modalVideoVersions.Count)
            return false;
        _modalVideoVersionIndex = index;
        return !_modalShowingVideo || ShowModalVideoVersion(index, autoplay: true);
    }
}

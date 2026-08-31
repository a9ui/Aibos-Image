using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private CancellationTokenSource? _modalMetadataCts;
    private PngParametersMetadata? _currentModalMetadata;
    private string? _currentModalMetadataPath;
    private string? _modalMetadataLoadingPath;
    private long _modalMetadataGeneration;
    private int _modalMetadataReadStartCountForSmoke;
    private int _modalMetadataApplyCountForSmoke;

    private void QueueModalMetadataSidebarRefresh(
        string displayPath,
        bool settleForNavigation = false)
    {
        CancelModalMetadataRefresh(clearCurrent: true);

        if (_modalShowingVideo)
        {
            SyncModalMetadataSidebar();
            return;
        }

        if (_currentPreviewMetadata is not null
            && string.Equals(
                _currentPreviewMetadataPath,
                displayPath,
                StringComparison.OrdinalIgnoreCase))
        {
            _currentModalMetadata = _currentPreviewMetadata;
            _currentModalMetadataPath = displayPath;
            SyncModalMetadataSidebar();
            return;
        }

        if (!string.Equals(
                Path.GetExtension(displayPath),
                ".png",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(displayPath))
        {
            _currentModalMetadataPath = displayPath;
            SyncModalMetadataSidebar();
            return;
        }

        long generation = ++_modalMetadataGeneration;
        var cts = new CancellationTokenSource();
        _modalMetadataCts = cts;
        _modalMetadataLoadingPath = displayPath;
        SyncModalMetadataSidebar();
        _ = LoadModalPngMetadataAsync(
            displayPath,
            generation,
            cts.Token,
            settleForNavigation);
    }

    private async Task LoadModalPngMetadataAsync(
        string path,
        long generation,
        CancellationToken token,
        bool settleForNavigation)
    {
        PngParametersMetadata? metadata;
        try
        {
            if (settleForNavigation)
                await Task.Delay(ModalNavigationSettleMilliseconds, token);
            Interlocked.Increment(ref _modalMetadataReadStartCountForSmoke);
            metadata = await Task.Run(
                () => ReadPngParametersMetadata(path, token),
                token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            metadata = null;
        }

        if (token.IsCancellationRequested
            || generation != _modalMetadataGeneration
            || _modalShowingVideo
            || Modal.Visibility != Visibility.Visible
            || !string.Equals(
                _modalDisplayPath,
                path,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentModalMetadata = metadata;
        _currentModalMetadataPath = path;
        _modalMetadataLoadingPath = null;
        Interlocked.Increment(ref _modalMetadataApplyCountForSmoke);
        SyncModalMetadataSidebar();
    }

    public int ModalMetadataReadStartCountForSmoke
        => Volatile.Read(ref _modalMetadataReadStartCountForSmoke);

    public int ModalMetadataApplyCountForSmoke
        => Volatile.Read(ref _modalMetadataApplyCountForSmoke);

    private void CancelModalMetadataRefresh(bool clearCurrent)
    {
        _modalMetadataGeneration++;
        _modalMetadataCts?.Cancel();
        _modalMetadataCts?.Dispose();
        _modalMetadataCts = null;
        _modalMetadataLoadingPath = null;
        if (!clearCurrent)
            return;
        _currentModalMetadata = null;
        _currentModalMetadataPath = null;
    }

    private PngParametersMetadata? CurrentDisplayedModalPngMetadata()
    {
        if (_modalShowingVideo || string.IsNullOrWhiteSpace(_modalDisplayPath))
            return null;
        if (string.Equals(
                _currentModalMetadataPath,
                _modalDisplayPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return _currentModalMetadata;
        }
        return string.Equals(
                _currentPreviewMetadataPath,
                _modalDisplayPath,
                StringComparison.OrdinalIgnoreCase)
            ? _currentPreviewMetadata
            : null;
    }

    private ManagedVideoVersion? CurrentDisplayedModalVideoVersion()
        => _modalShowingVideo
            && _modalVideoVersionIndex >= 0
            && _modalVideoVersionIndex < _modalVideoVersions.Count
                ? _modalVideoVersions[_modalVideoVersionIndex]
                : null;

    private List<string> CurrentModalOriginalPromptSearchMatches()
        => TryGetModalSourceTile(out Tile tile)
            ? PromptSearchMatches(tile.PromptUtf8, CurrentSearchQuery)
            : [];

    private void SyncModalMetadataSidebarForDisplayedVersion()
    {
        if (CurrentDisplayedModalVideoVersion() is ManagedVideoVersion video)
        {
            SyncModalVideoSettingsSidebar(video);
            return;
        }

        PngParametersMetadata? metadata = CurrentDisplayedModalPngMetadata();
        bool loading = !string.IsNullOrWhiteSpace(_modalDisplayPath)
            && string.Equals(
                _modalMetadataLoadingPath,
                _modalDisplayPath,
                StringComparison.OrdinalIgnoreCase);
        bool hasPrompt = !string.IsNullOrWhiteSpace(metadata?.Prompt);
        bool hasNegative = !string.IsNullOrWhiteSpace(metadata?.NegativePrompt);
        string settingsText = metadata is not null && metadata.Settings.Count > 0
            ? string.Join(
                "  ·  ",
                metadata.Settings.Select(static pair => $"{pair.Key}: {pair.Value}"))
            : loading
                ? "PNG metadata loading…"
                : "No generation settings metadata for this version.";

        ModalSettingsText.Text = settingsText;
        ModalMetadataStatusText.Text = metadata is null
            ? loading
                ? "このバージョンのPNG生成設定を読み込み中…"
                : "このバージョンにはPNG生成設定がありません"
            : metadata.Settings.Count > 0
                ? settingsText
                : "このバージョンのPNG parametersを読み込みました";
        SyncModalPromptChips(
            hasPrompt ? metadata!.Prompt : "",
            CurrentModalOriginalPromptSearchMatches());
        ModalPromptText.Text = hasPrompt
            ? string.Join(", ", _modalPromptChipTags)
            : "-";
        ModalNegativeText.Text = hasNegative ? metadata!.NegativePrompt : "-";
        CopyModalMetadataButton.IsEnabled = metadata is not null;
        CopyModalMetadataButton.ToolTip = metadata is null
            ? "この表示バージョンにコピー可能なPNG生成設定はありません"
            : "この表示バージョンのPNG生成設定をコピー";
        CopyModalPromptButton.IsEnabled = hasPrompt;
        CopyModalPromptButton.ToolTip = hasPrompt
            ? "この表示バージョンのPromptをコピー"
            : "この表示バージョンにPromptはありません";
        CopyModalNegativeButton.IsEnabled = hasNegative;
        CopyModalNegativeButton.ToolTip = hasNegative
            ? "この表示バージョンのNegative promptをコピー"
            : "この表示バージョンにNegative promptはありません";
    }

    private void SyncModalVideoSettingsSidebar(ManagedVideoVersion video)
    {
        string settings = BuildManagedVideoSettingsText(video);
        string prompt = string.IsNullOrWhiteSpace(video.PositivePrompt)
            ? video.RequestedPrompt
            : video.PositivePrompt;
        bool hasPrompt = !string.IsNullOrWhiteSpace(prompt);
        bool hasNegative = !string.IsNullOrWhiteSpace(video.NegativePrompt);

        ModalMetadataStatusText.Text = string.IsNullOrWhiteSpace(video.JobId)
            ? "動画化の実行時設定（Job ID不明）"
            : $"動画化 Job {video.JobId} の実行時設定";
        ModalSettingsText.Text = settings;
        SyncModalPromptChips(hasPrompt ? prompt : "");
        ModalPromptText.Text = hasPrompt
            ? string.Join(", ", _modalPromptChipTags)
            : "-";
        ModalNegativeText.Text = hasNegative ? video.NegativePrompt : "-";
        CopyModalMetadataButton.IsEnabled = true;
        CopyModalMetadataButton.ToolTip = "この動画化バージョンの実行時設定をコピー";
        CopyModalPromptButton.IsEnabled = hasPrompt;
        CopyModalPromptButton.ToolTip = hasPrompt
            ? "この動画化バージョンのPositive promptをコピー"
            : "この動画化バージョンにPromptはありません";
        CopyModalNegativeButton.IsEnabled = hasNegative;
        CopyModalNegativeButton.ToolTip = hasNegative
            ? "この動画化バージョンのNegative promptをコピー"
            : "この動画化バージョンにNegative promptはありません";
    }

    private static string BuildManagedVideoSettingsText(ManagedVideoVersion video)
    {
        if (video.IsMiniMaxH3)
        {
            return string.Join(
                "  ·  ",
                new[]
                {
                    "Preset: MiniMax H3 Preview",
                    $"Backend: {video.BackendId}",
                    $"Model: {video.ModelName}",
                    "Model revision: 014cd40f7e177756c6b2473c0d93b1c89a790dd2",
                    $"Duration: {video.DurationSeconds.ToString("0.#######", CultureInfo.InvariantCulture)} sec",
                    $"Playback FPS: {video.PlaybackFps}",
                    $"Frames: {video.FrameCount}",
                    $"Size: {video.Width} x {video.Height}",
                    $"Steps: {video.Steps}",
                    $"Sampler: {video.Sampler}",
                    $"Scheduler: {video.Scheduler}",
                    $"Denoise: {video.Denoise}",
                    $"Seed: {video.Seed}",
                    $"Container: {video.Container}",
                    $"Codec: {video.Codec}",
                    $"Bit depth: {video.BitDepth}",
                    video.Delivery is null
                        ? "Delivery: unavailable"
                        : $"Delivery: {video.Delivery.VideoCodec} / {video.Delivery.PixelFormat} / {video.Delivery.AudioCodec} audio / {video.Delivery.TargetFps} fps / {video.Delivery.FrameCount} frames / audio {video.Delivery.Audio}",
                });
        }

        return string.Join(
            "  ·  ",
            new[]
            {
                $"Preset: {video.PresetId}",
                $"Backend: {video.BackendId}",
                $"Model: {video.ModelName}",
                $"Duration: {video.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)} sec",
                $"Requested FPS: {video.RequestedPlaybackFps}",
                $"Playback FPS: {video.PlaybackFps}",
                $"Native frames: {video.NativeFrameCount}",
                $"Output frames: {video.FrameCount}",
                $"Maximum pixels: {video.MaximumPixelArea}",
                $"Size: {video.Width} x {video.Height}",
                $"Steps: {video.Steps}",
                $"CFG: {video.Cfg}",
                $"Sampler: {video.Sampler}",
                $"Scheduler: {video.Scheduler}",
                $"Shift: {video.Shift}",
                $"Denoise: {video.Denoise}",
                $"Seed: {video.Seed}",
                $"Container: {video.Container}",
                $"Codec: {video.Codec}",
                $"Bit depth: {video.BitDepth}",
                video.Delivery is null
                    ? "Delivery: none in stored snapshot"
                    : $"Delivery: {video.Delivery.BackendId} / model {video.Delivery.Model} / {video.Delivery.TargetFps} fps / {video.Delivery.FrameCount} frames / {video.Delivery.PixelFormat} / audio {video.Delivery.Audio}",
            });
    }

    private static string BuildManagedVideoCopyText(ManagedVideoVersion video)
    {
        var result = new StringBuilder();
        result.AppendLine($"Operation: video");
        result.AppendLine($"Job ID: {video.JobId}");
        result.AppendLine($"Requested prompt: {video.RequestedPrompt}");
        result.AppendLine($"Positive prompt: {video.PositivePrompt}");
        result.AppendLine($"Negative prompt: {video.NegativePrompt}");
        result.AppendLine(BuildManagedVideoSettingsText(video));
        if (!string.IsNullOrWhiteSpace(video.SourceProducerJobId))
            result.AppendLine($"Source producer job ID: {video.SourceProducerJobId}");
        return result.ToString().TrimEnd();
    }

    private void CopyModalPrompt_Click(object sender, RoutedEventArgs e)
        => CopyDisplayedModalPrompt(
            sender as Button ?? CopyModalPromptButton,
            negative: false);

    private void CopyModalNegative_Click(object sender, RoutedEventArgs e)
        => CopyDisplayedModalPrompt(
            sender as Button ?? CopyModalNegativeButton,
            negative: true);

    private void CopyModalMetadata_Click(object sender, RoutedEventArgs e)
    {
        string text = CurrentDisplayedModalVideoVersion() is ManagedVideoVersion video
            ? BuildManagedVideoCopyText(video)
            : CurrentDisplayedModalPngMetadata() is PngParametersMetadata metadata
                ? BuildPngMetadataCopyText(metadata)
                : "";
        CopyDisplayedModalText(
            sender as Button ?? CopyModalMetadataButton,
            text);
    }

    private void CopyDisplayedModalPrompt(Button button, bool negative)
    {
        string text;
        if (CurrentDisplayedModalVideoVersion() is ManagedVideoVersion video)
        {
            text = negative
                ? video.NegativePrompt
                : string.IsNullOrWhiteSpace(video.PositivePrompt)
                    ? video.RequestedPrompt
                    : video.PositivePrompt;
        }
        else if (CurrentDisplayedModalPngMetadata() is PngParametersMetadata metadata)
        {
            text = negative
                ? metadata.NegativePrompt
                : FormatPromptTagsForDisplay(metadata.Prompt);
        }
        else
        {
            text = "";
        }
        CopyDisplayedModalText(button, text);
    }

    private void CopyDisplayedModalText(Button button, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _lastMetadataCopyText = text.Trim();
        try
        {
            Clipboard.SetText(_lastMetadataCopyText);
            button.Content = "Copied";
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            button.Content = "Copy";
            button.ToolTip = $"Copy failed: {ex.Message}";
        }
    }

    public async Task<ModalMetadataSmokeSnapshot>
        WaitForModalDisplayedMetadataForSmokeAsync(
            string expectedDisplayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDisplayPath);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            ModalMetadataSmokeSnapshot snapshot = ModalMetadataForSmoke();
            if (string.Equals(
                    _modalDisplayPath,
                    expectedDisplayPath,
                    StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(_modalMetadataLoadingPath)
                && snapshot.MetadataCurrent)
            {
                return snapshot;
            }
            await Task.Delay(10);
        }
        return ModalMetadataForSmoke();
    }

    public ModalMetadataSmokeSnapshot DisplayedModalMetadataForSmoke =>
        ModalMetadataForSmoke();
}

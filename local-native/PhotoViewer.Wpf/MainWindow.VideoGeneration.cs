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
    private const string DefaultVideoPresetId = "wan22-ti2v-5b-normal-v1";
    private const string DefaultVideoBackendId = "wan22-ti2v-5b-core-v1";
    private const int DefaultVideoDurationSeconds = 6;
    private const int DefaultVideoPlaybackFps = 16;
    private const int DefaultVideoMaximumPixelArea = 409_600;
    private const int MaxVideoPromptLength = 2_000;

    private static readonly int[] SupportedVideoDurationSeconds = [4, 6];
    private static readonly int[] SupportedVideoPlaybackFps = [12, 16];
    private static readonly int[] SupportedVideoMaximumPixelAreas = [230_400, 307_200, 409_600];

    private int _videoDurationSeconds = DefaultVideoDurationSeconds;
    private int _videoPlaybackFps = DefaultVideoPlaybackFps;
    private int _videoMaximumPixelArea = DefaultVideoMaximumPixelArea;
    private string _videoPrompt = "";
    private bool _syncingVideoGenerationSettings;
    private bool _videoGenerationRequestPending;

    private sealed record VideoGenerationRequestSettings(
        string PresetId,
        string BackendId,
        int DurationSeconds,
        int PlaybackFps,
        int MaximumPixelArea,
        string Prompt);

    private VideoGenerationRequestSettings CurrentVideoGenerationRequestSettings()
        => new(
            DefaultVideoPresetId,
            DefaultVideoBackendId,
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt.Trim());

    private void OpenModalVideoGeneration_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard();

    private void GalleryContextVideo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTile() is not Tile { IsRealFile: true })
            return;

        OpenModal();
        if (Modal.Visibility != Visibility.Visible)
            return;
        _ = Dispatcher.BeginInvoke(
            new Action(OpenVideoGenerationBoard),
            DispatcherPriority.Input);
    }

    private void ModalContextVideo_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard();

    private void OpenVideoGenerationBoard()
    {
        if (ModalVideoGenerationPopup is null
            || SelectedTile() is not Tile { IsRealFile: true })
        {
            return;
        }

        SyncVideoGenerationSettingsControls();
        VideoGenerationStatusText.Text =
            "実行すると既存のAI Jobsキューへ追加します。画像閲覧だけでは開始しません。";
        ModalVideoGenerationPopup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(
            new Action(() => ModalVideoPromptTextBox.Focus()),
            DispatcherPriority.Input);
    }

    private void CloseVideoGenerationBoard_Click(object sender, RoutedEventArgs e)
    {
        if (ModalVideoGenerationPopup is not null)
            ModalVideoGenerationPopup.IsOpen = false;
    }

    private void VideoGenerationSetting_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || ModalVideoDurationComboBox is null
            || ModalVideoFpsComboBox is null
            || ModalVideoResolutionComboBox is null)
        {
            return;
        }

        ComboBox durationSource = ReferenceEquals(sender, AppVideoDurationComboBox)
            ? AppVideoDurationComboBox
            : ModalVideoDurationComboBox;
        ComboBox fpsSource = ReferenceEquals(sender, AppVideoFpsComboBox)
            ? AppVideoFpsComboBox
            : ModalVideoFpsComboBox;
        ComboBox resolutionSource = ReferenceEquals(sender, AppVideoResolutionComboBox)
            ? AppVideoResolutionComboBox
            : ModalVideoResolutionComboBox;
        _videoDurationSeconds = SelectedIntegerTag(
            durationSource,
            DefaultVideoDurationSeconds,
            SupportedVideoDurationSeconds);
        _videoPlaybackFps = SelectedIntegerTag(
            fpsSource,
            DefaultVideoPlaybackFps,
            SupportedVideoPlaybackFps);
        _videoMaximumPixelArea = SelectedIntegerTag(
            resolutionSource,
            DefaultVideoMaximumPixelArea,
            SupportedVideoMaximumPixelAreas);
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus("保存済み。次に追加する動画ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void VideoGenerationPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings)
            return;

        TextBox source = ReferenceEquals(sender, AppVideoPromptTextBox)
            ? AppVideoPromptTextBox
            : ModalVideoPromptTextBox;
        _videoPrompt = source.Text.Length <= MaxVideoPromptLength
            ? source.Text
            : source.Text[..MaxVideoPromptLength];
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            string.IsNullOrWhiteSpace(_videoPrompt)
                ? "空欄はNormalの既定モーションを使います。保存済みです。"
                : "保存済み。入力した動きが次の動画ジョブに使われます。");
        if (!_initializing)
            SaveState();
    }

    private void ResetVideoGenerationSettings_Click(object sender, RoutedEventArgs e)
    {
        RestoreVideoGenerationSettings(null, null, null, null);
        SetVideoGenerationSettingsStatus(
            "6秒・16fps・最大409,600px・Normal既定プロンプトに戻しました。");
        if (!_initializing)
            SaveState();
    }

    private void SetVideoGenerationSettingsStatus(string message)
    {
        if (VideoGenerationStatusText is not null)
            VideoGenerationStatusText.Text = message;
        if (AppVideoSettingsStatusText is not null)
            AppVideoSettingsStatusText.Text = message;
    }

    private void RestoreVideoGenerationSettings(
        int? durationSeconds,
        int? playbackFps,
        int? maximumPixelArea,
        string? prompt)
    {
        _videoDurationSeconds = durationSeconds is int duration
            && SupportedVideoDurationSeconds.Contains(duration)
                ? duration
                : DefaultVideoDurationSeconds;
        _videoPlaybackFps = playbackFps is int fps
            && SupportedVideoPlaybackFps.Contains(fps)
                ? fps
                : DefaultVideoPlaybackFps;
        _videoMaximumPixelArea = maximumPixelArea is int area
            && SupportedVideoMaximumPixelAreas.Contains(area)
                ? area
                : DefaultVideoMaximumPixelArea;
        string restoredPrompt = prompt ?? "";
        _videoPrompt = restoredPrompt.Length <= MaxVideoPromptLength
            ? restoredPrompt
            : restoredPrompt[..MaxVideoPromptLength];
        SyncVideoGenerationSettingsControls();
    }

    private void SyncVideoGenerationSettingsControls()
    {
        if (ModalVideoDurationComboBox is null
            || ModalVideoFpsComboBox is null
            || ModalVideoResolutionComboBox is null
            || ModalVideoPromptTextBox is null)
        {
            return;
        }

        _syncingVideoGenerationSettings = true;
        try
        {
            SelectIntegerTag(ModalVideoDurationComboBox, _videoDurationSeconds);
            SelectIntegerTag(ModalVideoFpsComboBox, _videoPlaybackFps);
            SelectIntegerTag(ModalVideoResolutionComboBox, _videoMaximumPixelArea);
            ModalVideoPromptTextBox.Text = _videoPrompt;
            if (AppVideoDurationComboBox is not null)
                SelectIntegerTag(AppVideoDurationComboBox, _videoDurationSeconds);
            if (AppVideoFpsComboBox is not null)
                SelectIntegerTag(AppVideoFpsComboBox, _videoPlaybackFps);
            if (AppVideoResolutionComboBox is not null)
                SelectIntegerTag(AppVideoResolutionComboBox, _videoMaximumPixelArea);
            if (AppVideoPromptTextBox is not null)
                AppVideoPromptTextBox.Text = _videoPrompt;
        }
        finally
        {
            _syncingVideoGenerationSettings = false;
        }
        UpdateVideoGenerationActionControls();
    }

    private void UpdateVideoGenerationActionControls()
    {
        if (ModalVideoGenerateButton is null
            || QueueVideoGenerationButton is null)
        {
            return;
        }

        // This is presentation state only. The explicit execute path below
        // canonicalizes the selected source and verifies that it still exists
        // before sending any request.
        bool hasSource = SelectedTile() is { IsRealFile: true };
        ModalVideoGenerateButton.IsEnabled = hasSource && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.IsEnabled = hasSource && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.Content = _videoGenerationRequestPending
            ? "追加中..."
            : "動画化を実行";
        AutomationProperties.SetName(
            QueueVideoGenerationButton,
            _videoGenerationRequestPending
                ? "Adding video generation job"
                : "Add video generation job");
    }

    private async void QueueVideoGeneration_Click(object sender, RoutedEventArgs e)
        => await QueueVideoGenerationAsync();

    private async Task<bool> QueueVideoGenerationAsync()
    {
        if (_videoGenerationRequestPending
            || SelectedTile() is not Tile { IsRealFile: true } tile
            || !TryResolveEnhancementSourceIdentity(tile.Path, out string sourceIdentity)
            || !File.Exists(sourceIdentity))
        {
            return false;
        }

        VideoGenerationRequestSettings settings =
            CurrentVideoGenerationRequestSettings();
        _videoGenerationRequestPending = true;
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus("ローカル動画生成の準備を確認しています...");
        try
        {
            EnhancementApiResponse readiness =
                await EnsureEnhancementCompanionReadyForExplicitActionAsync(sourceIdentity);
            if (!readiness.Ok)
            {
                SetVideoGenerationSettingsStatus(readiness.Error);
                return false;
            }

            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/jobs",
                new
                {
                    sourceId = sourceIdentity,
                    operation = "video",
                    mediaKind = "video",
                    presetId = settings.PresetId,
                    adapterId = settings.BackendId,
                    video = new
                    {
                        requested = new
                        {
                            durationSeconds = settings.DurationSeconds,
                            playbackFps = settings.PlaybackFps,
                            maximumPixelArea = settings.MaximumPixelArea,
                            prompt = settings.Prompt,
                        },
                    },
                });
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || !payload.TryGetProperty("job", out JsonElement job)
                || job.ValueKind != JsonValueKind.Object)
            {
                SetVideoGenerationSettingsStatus(response.Error);
                return false;
            }

            TryGetStringProperty(job, "id", out string? jobId);
            string suffix = string.IsNullOrWhiteSpace(jobId)
                ? ""
                : $" ({jobId})";
            SetVideoGenerationSettingsStatus(
                $"動画ジョブを共有GPUキューへ追加しました{suffix}。");
            SetTransientStatusToast(
                $"{tile.FileName}: 動画化をJobsキューへ追加しました。");
            ModalVideoGenerationPopup.IsOpen = false;
            return true;
        }
        finally
        {
            _videoGenerationRequestPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    public bool OpenVideoGenerationBoardForSmoke()
    {
        OpenVideoGenerationBoard();
        return ModalVideoGenerationPopup.IsOpen;
    }

    public (int DurationSeconds, int PlaybackFps, int MaximumPixelArea, string Prompt)
        VideoGenerationSettingsForSmoke
        => (
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt);

    public void ConfigureVideoGenerationForSmoke(
        int durationSeconds,
        int playbackFps,
        int maximumPixelArea,
        string prompt)
        => RestoreVideoGenerationSettings(
            durationSeconds,
            playbackFps,
            maximumPixelArea,
            prompt);

    public Task<bool> QueueVideoGenerationForSmokeAsync()
        => QueueVideoGenerationAsync();

    public bool VideoGenerationSurfaceForSmoke
        => ModalVideoGenerateButton is not null
            && ModalVideoGenerationPopup is not null
            && ModalVideoPromptTextBox.MaxLength == MaxVideoPromptLength
            && string.Equals(
                AutomationProperties.GetName(QueueVideoGenerationButton),
                "Add video generation job",
                StringComparison.Ordinal)
            && ModalVideoPresetText.Text.Contains(
                "Wan2.2 TI2V 5B",
                StringComparison.Ordinal)
            && AppVideoSettingsHeading is not null
            && SettingsVideoNav is not null;
}

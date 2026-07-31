using System.Globalization;
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
    private const string WanVideoModelId = "wan22-ti2v-5b";
    private const string HunyuanVideoModelId =
        "hunyuan-video-1.5-i2v-step-distilled-experimental";
    private const string DefaultVideoModelId = WanVideoModelId;
    private const string DefaultVideoPresetId = "wan22-ti2v-5b-normal-v1";
    private const string DefaultVideoBackendId = "wan22-ti2v-5b-core-v1";
    private const int DefaultVideoDurationSeconds = 6;
    private const int DefaultVideoPlaybackFps = 16;
    private const int DefaultVideoMaximumPixelArea = 409_600;
    private const int MaxVideoPromptLength = 2_000;
    private const double VideoWanLandscapeEstimateBaselineSeconds = 146.691;
    private const double VideoWanPortraitEstimateBaselineSeconds = 274.801;
    private const int VideoWanEstimateBaselineFrameCount = 97;
    private const double VideoDeliveryLandscapeEstimateBaselineSeconds = 11.768;
    private const double VideoDeliveryPortraitEstimateBaselineSeconds = 17.560;
    private const int VideoDeliveryEstimateBaselineDurationSeconds = 6;
    private const int VideoEstimateBaselineMaximumPixelArea = 409_600;

    private static readonly int[] SupportedVideoDurationSeconds = [4, 6];
    private static readonly int[] SupportedVideoPlaybackFps = [12, 16];
    private static readonly int[] SupportedVideoMaximumPixelAreas = [230_400, 307_200, 409_600];

    private int _videoDurationSeconds = DefaultVideoDurationSeconds;
    private int _videoPlaybackFps = DefaultVideoPlaybackFps;
    private int _videoMaximumPixelArea = DefaultVideoMaximumPixelArea;
    private string _videoModelId = DefaultVideoModelId;
    private string _videoPrompt = "";
    private bool _syncingVideoGenerationSettings;
    private bool _videoGenerationRequestPending;
    private VideoSourceChoice? _videoSourceChoice;

    private sealed record VideoSourceChoice(
        string SourceIdentity,
        string DisplayPath,
        string? ProducerJobId,
        string Label);

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

    private static bool IsVideoModelRunnable(string modelId)
        => string.Equals(modelId, WanVideoModelId, StringComparison.Ordinal);

    private static string VideoModelLabel(string modelId)
        => string.Equals(modelId, HunyuanVideoModelId, StringComparison.Ordinal)
            ? "HunyuanVideo 1.5 — 実写・人物向け／実験"
            : "Wan2.2 TI2V 5B — アニメ・汎用／標準";

    private static string VideoModelDescription(string modelId)
        => string.Equals(modelId, HunyuanVideoModelId, StringComparison.Ordinal)
            ? "実写・人物の顔や手を重視する候補。12GBの隔離ランタイム実測前なので、現在は選択内容の確認だけできます。"
            : "RTX 4070 SUPER 12GBで検証済みの標準モデル。アニメ画像と汎用画像を、RIFE 4.25で正確な30fpsへ仕上げます。";

    private static string SelectedVideoModelId(ComboBox comboBox)
    {
        string? selected = (comboBox.SelectedItem as ComboBoxItem)
            ?.Tag
            ?.ToString();
        return selected is WanVideoModelId or HunyuanVideoModelId
            ? selected
            : DefaultVideoModelId;
    }

    private static void SelectVideoModelId(
        ComboBox comboBox,
        string modelId)
    {
        ComboBoxItem? item = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                modelId,
                StringComparison.Ordinal));
        comboBox.SelectedItem = item
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private bool TryCaptureVideoSource(
        Tile tile,
        string? requestedSource,
        out VideoSourceChoice source,
        out string error)
    {
        source = null!;
        error = "";
        if (!TryResolveEnhancementSourceIdentity(
                tile.Path,
                out string sourceIdentity)
            || !File.Exists(sourceIdentity))
        {
            error = "元画像が見つからないため動画化できません。";
            return false;
        }

        ManagedEnhancementVersion? photorealVersion = null;
        if (requestedSource is null
            && TryGetCurrentModalEnhancementVersion(
                tile,
                out ManagedEnhancementVersion current)
            && string.Equals(
                current.Operation,
                "photoreal",
                StringComparison.Ordinal))
        {
            photorealVersion = current;
        }
        else if (string.Equals(
            requestedSource,
            "photoreal",
            StringComparison.Ordinal))
        {
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
                        out ManagedEnhancedOutput currentOutput))
                {
                    continue;
                }

                photorealVersion = candidate with
                {
                    Output = currentOutput,
                };
                break;
            }
            if (photorealVersion is null)
            {
                error = "この画像には利用できる実写化バージョンがありません。";
                return false;
            }
        }

        if (photorealVersion is not null)
        {
            if (string.IsNullOrWhiteSpace(photorealVersion.JobId)
                || !File.Exists(photorealVersion.Output.OutputPath))
            {
                error = "実写化バージョンのJobまたは出力が見つかりません。";
                return false;
            }

            source = new VideoSourceChoice(
                sourceIdentity,
                photorealVersion.Output.OutputPath,
                photorealVersion.JobId,
                $"実写版 · {Path.GetFileName(photorealVersion.Output.OutputPath)}");
            return true;
        }

        string label = requestedSource is null
            && _modalShowingEnhanced
                ? "Original（高画質化表示は入力対象外）"
                : "Original";
        source = new VideoSourceChoice(
            sourceIdentity,
            sourceIdentity,
            null,
            label);
        return true;
    }

    private static (
        int FrameCount,
        int EstimatedMinimumSeconds,
        int EstimatedMaximumSeconds)
        EstimateVideoGeneration(int durationSeconds, int playbackFps, int maximumPixelArea)
    {
        int frameCount = 4 * (durationSeconds * playbackFps / 4) + 1;
        double ScaleWan(double baselineSeconds)
            => baselineSeconds
                * frameCount
                / VideoWanEstimateBaselineFrameCount
                * maximumPixelArea
                / VideoEstimateBaselineMaximumPixelArea;
        double ScaleDelivery(double baselineSeconds)
            => baselineSeconds
                * durationSeconds
                / VideoDeliveryEstimateBaselineDurationSeconds
                * maximumPixelArea
                / VideoEstimateBaselineMaximumPixelArea;
        double minimumSeconds =
            ScaleWan(VideoWanLandscapeEstimateBaselineSeconds)
            + ScaleDelivery(
                VideoDeliveryLandscapeEstimateBaselineSeconds);
        double maximumSeconds =
            ScaleWan(VideoWanPortraitEstimateBaselineSeconds)
            + ScaleDelivery(
                VideoDeliveryPortraitEstimateBaselineSeconds);
        return (
            frameCount,
            (int)Math.Round(
                minimumSeconds,
                MidpointRounding.AwayFromZero),
            (int)Math.Ceiling(maximumSeconds));
    }

    private string VideoGenerationEstimateText()
    {
        if (!IsVideoModelRunnable(_videoModelId))
        {
            return "完了目安: 未計測（12GB環境の隔離評価を通過するまで実行しません）";
        }

        (
            _,
            int estimatedMinimumSeconds,
            int estimatedMaximumSeconds) = EstimateVideoGeneration(
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea);
        static string FormatDuration(int seconds)
            => seconds >= 60
                ? $"{seconds / 60}分{seconds % 60:D2}秒"
                : $"{seconds}秒";
        return "完了目安: 約"
            + $"{FormatDuration(estimatedMinimumSeconds)}〜"
            + $"{FormatDuration(estimatedMaximumSeconds)}"
            + "（Wan生成＋RIFE 4.25仕上げ・RTX 4070 SUPER横長/縦長実測範囲・キュー待ちを除く）";
    }

    private string VideoGenerationDeliveryText()
    {
        int generationFrameCount =
            4 * (_videoDurationSeconds * _videoPlaybackFps / 4) + 1;
        int deliveryFrameCount = _videoDurationSeconds * 30;
        string duration = _videoDurationSeconds.ToString(
            "F3",
            CultureInfo.InvariantCulture);
        return $"生成: {_videoPlaybackFps} fps・{generationFrameCount}f"
            + $" → 最終出力: 30 fps・{deliveryFrameCount}f・{duration}秒"
            + " · RIFE 4.25 · H.264 / yuv420p · 音声なし";
    }

    private string VideoPixelBudgetHintText(bool includeDefaultPrompt)
    {
        string promptHint = includeDefaultPrompt
            ? "空欄はNormalの既定モーション。"
            : "";
        string maximumPixelArea = _videoMaximumPixelArea.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        return promptHint
            + $"選択した画素数上限: {maximumPixelArea}px。"
            + "元画像比率を保ち、32px単位で上限内に自動調整します。"
            + "1 worker・GPU推論の並列化なし。";
    }

    private void OpenModalVideoGeneration_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard();

    private void GalleryContextVideo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTile() is not Tile { IsRealFile: true })
            return;

        OpenModal();
        if (Modal.Visibility != Visibility.Visible)
            return;
        string requestedSource = (sender as MenuItem)?.Tag?.ToString()
            ?? "original";
        _ = Dispatcher.BeginInvoke(
            new Action(() => OpenVideoGenerationBoard(requestedSource)),
            DispatcherPriority.Input);
    }

    private void ModalContextVideo_Click(object sender, RoutedEventArgs e)
        => OpenVideoGenerationBoard();

    private void OpenVideoGenerationBoard(string? requestedSource = null)
    {
        if (ModalVideoGenerationPopup is null
            || SelectedTile() is not Tile { IsRealFile: true } tile)
        {
            return;
        }
        if (!TryCaptureVideoSource(
                tile,
                requestedSource,
                out VideoSourceChoice source,
                out string sourceError))
        {
            SetTransientStatusToast(sourceError);
            return;
        }

        _videoSourceChoice = source;
        SyncVideoGenerationSettingsControls();
        VideoGenerationStatusText.Text =
            IsVideoModelRunnable(_videoModelId)
                ? "実行すると既存のAI Jobsキューへ追加します。画像閲覧だけでは開始しません。"
                : "この実験モデルは12GB実測前のため、現在は実行できません。Wanを選ぶと実行できます。";
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

    private void VideoGenerationModel_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || AppVideoModelComboBox is null
            || ModalVideoModelComboBox is null)
            return;

        ComboBox source = ReferenceEquals(sender, AppVideoModelComboBox)
            ? AppVideoModelComboBox
            : ModalVideoModelComboBox;
        _videoModelId = SelectedVideoModelId(source);
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            IsVideoModelRunnable(_videoModelId)
                ? "Wan2.2を使用します。保存済みです。"
                : "HunyuanVideo 1.5は実写・人物向けの実験候補です。12GB実測前のため実行は無効です。");
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
        RestoreVideoGenerationSettings(null, null, null, null, null);
        SetVideoGenerationSettingsStatus(
            "6秒・生成16fps・最終30fps・画素数上限409,600px・Normal既定プロンプトに戻しました。");
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
        string? prompt,
        string? modelId = null)
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
        _videoModelId = modelId is WanVideoModelId or HunyuanVideoModelId
            ? modelId
            : DefaultVideoModelId;
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
            SelectVideoModelId(ModalVideoModelComboBox, _videoModelId);
            ModalVideoPromptTextBox.Text = _videoPrompt;
            if (AppVideoDurationComboBox is not null)
                SelectIntegerTag(AppVideoDurationComboBox, _videoDurationSeconds);
            if (AppVideoFpsComboBox is not null)
                SelectIntegerTag(AppVideoFpsComboBox, _videoPlaybackFps);
            if (AppVideoResolutionComboBox is not null)
                SelectIntegerTag(AppVideoResolutionComboBox, _videoMaximumPixelArea);
            if (AppVideoModelComboBox is not null)
                SelectVideoModelId(AppVideoModelComboBox, _videoModelId);
            if (AppVideoPromptTextBox is not null)
                AppVideoPromptTextBox.Text = _videoPrompt;
            string modelDescription = VideoModelDescription(_videoModelId);
            ModalVideoPresetText.Text = VideoModelLabel(_videoModelId);
            ModalVideoModelDescriptionText.Text = modelDescription;
            AppVideoModelDescriptionText.Text = modelDescription;
            ModalVideoSourceText.Text = _videoSourceChoice is null
                ? "入力: 拡大画面を開いた時点の画像"
                : $"入力: {_videoSourceChoice.Label}";
            string estimateText = VideoGenerationEstimateText();
            if (ModalVideoGenerationEstimateText is not null)
                ModalVideoGenerationEstimateText.Text = estimateText;
            if (AppVideoGenerationEstimateText is not null)
                AppVideoGenerationEstimateText.Text = estimateText;
            string deliveryText = VideoGenerationDeliveryText();
            if (ModalVideoDeliveryText is not null)
                ModalVideoDeliveryText.Text = deliveryText;
            if (AppVideoDeliveryText is not null)
                AppVideoDeliveryText.Text = deliveryText;
            if (ModalVideoResolutionHintText is not null)
                ModalVideoResolutionHintText.Text =
                    VideoPixelBudgetHintText(false);
            if (AppVideoResolutionHintText is not null)
                AppVideoResolutionHintText.Text =
                    VideoPixelBudgetHintText(true);
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
        bool capturedSourceReady = _videoSourceChoice is VideoSourceChoice source
            && File.Exists(source.SourceIdentity)
            && File.Exists(source.DisplayPath);
        bool modelReady = IsVideoModelRunnable(_videoModelId);
        ModalVideoGenerateButton.IsEnabled = hasSource && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.IsEnabled =
            capturedSourceReady
            && modelReady
            && !_videoGenerationRequestPending;
        QueueVideoGenerationButton.Content = _videoGenerationRequestPending
            ? "追加中..."
            : modelReady
                ? "動画化を実行"
                : "実験モデルは準備中";
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
            || _videoSourceChoice is not VideoSourceChoice source
            || !File.Exists(source.SourceIdentity)
            || !File.Exists(source.DisplayPath)
            || !IsVideoModelRunnable(_videoModelId))
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
                await EnsureEnhancementCompanionReadyForExplicitActionAsync(
                    source.SourceIdentity);
            if (!readiness.Ok)
            {
                SetVideoGenerationSettingsStatus(readiness.Error);
                return false;
            }

            var requestBody = new Dictionary<string, object?>
            {
                ["sourceId"] = source.SourceIdentity,
                ["operation"] = "video",
                ["mediaKind"] = "video",
                ["presetId"] = settings.PresetId,
                ["adapterId"] = settings.BackendId,
                ["video"] = new
                {
                    requested = new
                    {
                        durationSeconds = settings.DurationSeconds,
                        playbackFps = settings.PlaybackFps,
                        maximumPixelArea = settings.MaximumPixelArea,
                        prompt = settings.Prompt,
                    },
                },
            };
            if (!string.IsNullOrWhiteSpace(source.ProducerJobId))
                requestBody["sourceProducerJobId"] = source.ProducerJobId;

            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                "api/enhance/jobs",
                requestBody);
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
                $"{Path.GetFileName(source.SourceIdentity)}: {source.Label}から動画化をJobsキューへ追加しました。");
            ModalVideoGenerationPopup.IsOpen = false;
            return true;
        }
        finally
        {
            _videoGenerationRequestPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    public bool OpenVideoGenerationBoardForSmoke(
        string? requestedSource = null)
    {
        OpenVideoGenerationBoard(requestedSource);
        return ModalVideoGenerationPopup.IsOpen;
    }

    public (int DurationSeconds, int PlaybackFps, int MaximumPixelArea, string Prompt)
        VideoGenerationSettingsForSmoke
        => (
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt);

    public (
        int FrameCount,
        int EstimatedMinimumSeconds,
        int EstimatedMaximumSeconds)
        VideoGenerationEstimateForSmoke
        => EstimateVideoGeneration(
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea);

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

    public void SelectVideoModelForSmoke(string modelId)
    {
        _videoModelId = modelId;
        SyncVideoGenerationSettingsControls();
    }

    public string VideoModelIdForSmoke => _videoModelId;
    public bool VideoModelRunnableForSmoke =>
        IsVideoModelRunnable(_videoModelId);

    public (string Label, string? ProducerJobId)? VideoSourceForSmoke
        => _videoSourceChoice is null
            ? null
            : (_videoSourceChoice.Label, _videoSourceChoice.ProducerJobId);

    public Task<bool> QueueVideoGenerationForSmokeAsync()
        => QueueVideoGenerationAsync();

    public bool VideoGenerationSurfaceForSmoke
        => ModalVideoGenerateButton is not null
            && ModalVideoGenerationPopup is not null
            && ModalVideoModelComboBox.Items.Count == 2
            && AppVideoModelComboBox.Items.Count == 2
            && ModalVideoModelDescriptionText.Text.Contains(
                "12GB",
                StringComparison.Ordinal)
            && ModalVideoPromptTextBox.MaxLength == MaxVideoPromptLength
            && string.Equals(
                AutomationProperties.GetName(QueueVideoGenerationButton),
                "Add video generation job",
                StringComparison.Ordinal)
            && ModalVideoPresetText.Text.Contains(
                "Wan2.2 TI2V 5B",
                StringComparison.Ordinal)
            && string.Equals(
                AppVideoGenerationFpsLabel.Text,
                "生成FPS",
                StringComparison.Ordinal)
            && string.Equals(
                ModalVideoGenerationFpsLabel.Text,
                "生成FPS",
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(AppVideoFpsComboBox),
                "Default video generation FPS",
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(ModalVideoFpsComboBox),
                "Video generation FPS",
                StringComparison.Ordinal)
            && string.Equals(
                AppVideoPixelBudgetLabel.Text,
                "画素数上限",
                StringComparison.Ordinal)
            && string.Equals(
                ModalVideoPixelBudgetLabel.Text,
                "画素数上限",
                StringComparison.Ordinal)
            && string.Equals(
                AppVideoDeliveryText.Text,
                ModalVideoDeliveryText.Text,
                StringComparison.Ordinal)
            && AppVideoDeliveryText.Text.Contains(
                $"生成: {_videoPlaybackFps} fps・"
                    + $"{4 * (_videoDurationSeconds * _videoPlaybackFps / 4) + 1}f",
                StringComparison.Ordinal)
            && ModalVideoDeliveryText.Text.Contains(
                $"最終出力: 30 fps・{_videoDurationSeconds * 30}f・"
                    + $"{_videoDurationSeconds}.000秒 · RIFE 4.25",
                StringComparison.Ordinal)
            && AppVideoResolutionHintText.Text.Contains(
                $"{_videoMaximumPixelArea.ToString(
                    "N0",
                    CultureInfo.InvariantCulture)}px",
                StringComparison.Ordinal)
            && ModalVideoResolutionHintText.Text.Contains(
                "上限内に自動調整",
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText is not null
            && AppVideoGenerationEstimateText is not null
            && string.Equals(
                ModalVideoGenerationEstimateText.Text,
                AppVideoGenerationEstimateText.Text,
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText.Text.Contains(
                "完了目安: 約1分01秒〜1分53秒",
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText.Text.Contains(
                "RTX 4070 SUPER横長/縦長実測範囲",
                StringComparison.Ordinal)
            && ModalVideoGenerationEstimateText.Text.Contains(
                "Wan生成＋RIFE 4.25仕上げ",
                StringComparison.Ordinal)
            && AppVideoSettingsHeading is not null
            && SettingsVideoNav is not null;
}

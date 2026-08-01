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
    private const string NormalVideoPresetId = "wan22-ti2v-5b-normal-v1";
    private const string HighVideoPresetId = "wan22-ti2v-5b-high-v1";
    private const string DefaultVideoPresetId = NormalVideoPresetId;
    private const string DefaultVideoBackendId = "wan22-ti2v-5b-core-v1";
    private const string PhotorealVideoSourceRequestPrefix =
        "photoreal-job:";
    private const int NormalVideoSteps = 20;
    private const int HighVideoSteps = 40;
    private const int DefaultVideoDurationSeconds = 6;
    private const int DefaultVideoPlaybackFps = 16;
    private const int DefaultVideoMaximumPixelArea = 409_600;
    private const int MaxVideoPromptLength = 2_000;
    private const int MaxVideoStyleCount = 32;
    private const int MaxVideoStyleNameLength = 40;
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
    private string _videoQualityId = DefaultVideoPresetId;
    private string _videoPrompt = "";
    private readonly List<VideoStyleState> _videoStyles = [];
    private string? _selectedVideoStyleName;
    private bool _syncingVideoGenerationSettings;
    private bool _videoGenerationRequestPending;
    private VideoSourceChoice? _videoSourceChoice;

    private sealed record VideoSourceChoice(
        string SourceIdentity,
        string DisplayPath,
        string? ProducerJobId,
        string Label);

    private sealed record VideoStyleChoice(string Label, string? StyleName);

    private sealed record VideoGenerationRequestSettings(
        string PresetId,
        string BackendId,
        int DurationSeconds,
        int PlaybackFps,
        int MaximumPixelArea,
        string Prompt);

    private VideoGenerationRequestSettings CurrentVideoGenerationRequestSettings()
        => new(
            _videoQualityId,
            DefaultVideoBackendId,
            _videoDurationSeconds,
            _videoPlaybackFps,
            _videoMaximumPixelArea,
            _videoPrompt.Trim());

    private static bool IsVideoModelRunnable(string modelId)
        => string.Equals(modelId, WanVideoModelId, StringComparison.Ordinal);

    private static bool IsVideoQualitySupported(string presetId)
        => presetId is NormalVideoPresetId or HighVideoPresetId;

    private static int VideoQualitySteps(string presetId)
        => string.Equals(presetId, HighVideoPresetId, StringComparison.Ordinal)
            ? HighVideoSteps
            : NormalVideoSteps;

    private static string VideoQualityLabel(string presetId)
        => string.Equals(presetId, HighVideoPresetId, StringComparison.Ordinal)
            ? "高品質 · 40 step"
            : "標準 · 20 step";

    private static string VideoModelLabel(string modelId)
        => string.Equals(modelId, HunyuanVideoModelId, StringComparison.Ordinal)
            ? "HunyuanVideo 1.5 — 実写・人物向け／実験"
            : "Wan2.2 TI2V 5B — アニメ・汎用";

    private static string VideoModelDescription(string modelId)
        => string.Equals(modelId, HunyuanVideoModelId, StringComparison.Ordinal)
            ? "実写・人物の顔や手を重視する候補。12GBの隔離ランタイム実測前なので、現在は選択内容の確認だけできます。"
            : "RTX 4070 SUPER 12GBで検証済みのモデル。アニメ画像と汎用画像を、RIFE 4.25で正確な30fpsへ仕上げます。";

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

    private static string SelectedVideoQualityId(ComboBox comboBox)
    {
        string? selected = (comboBox.SelectedItem as ComboBoxItem)
            ?.Tag
            ?.ToString();
        return IsVideoQualitySupported(selected ?? "")
            ? selected!
            : DefaultVideoPresetId;
    }

    private static void SelectVideoQualityId(
        ComboBox comboBox,
        string presetId)
    {
        ComboBoxItem? item = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                presetId,
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

        string? requestedPhotorealJobId = requestedSource is not null
            && requestedSource.StartsWith(
                PhotorealVideoSourceRequestPrefix,
                StringComparison.Ordinal)
                ? requestedSource[PhotorealVideoSourceRequestPrefix.Length..]
                : null;
        if (requestedPhotorealJobId is not null
            && string.IsNullOrWhiteSpace(requestedPhotorealJobId))
        {
            error = "実写化バージョンのJobを特定できません。";
            return false;
        }

        ManagedEnhancementVersion? photorealVersion = null;
        if (requestedSource is null
            && CurrentModalEnhancementVersionIsPhotoreal())
        {
            if (!TryGetDeletableCurrentModalEnhancementVersion(
                    tile,
                    out ManagedEnhancementVersion current)
                || !string.Equals(
                    current.Operation,
                    "photoreal",
                    StringComparison.Ordinal))
            {
                error = "表示中の実写版が古いか、Jobを一意に特定できません。実写版を選び直してください。";
                return false;
            }
            photorealVersion = current;
        }
        else if (requestedPhotorealJobId is not null
            || string.Equals(
                requestedSource,
                "photoreal",
                StringComparison.Ordinal))
        {
            bool ambiguousJobId = false;
            foreach (ManagedEnhancementVersion candidate
                     in GetManagedEnhancementVersionsForPath(tile.Path))
            {
                if (!string.Equals(
                        candidate.Operation,
                        "photoreal",
                        StringComparison.Ordinal)
                    || !IsGloballyUniqueManagedJobId(candidate.JobId)
                    || (requestedPhotorealJobId is not null
                        && !string.Equals(
                            candidate.JobId,
                            requestedPhotorealJobId,
                            StringComparison.Ordinal))
                    || !TryCreateManagedEnhancedOutput(
                        tile,
                        candidate.Output.OutputPath,
                        candidate.Output.SourceSize,
                        candidate.Output.SourceMtimeMs,
                        out ManagedEnhancedOutput currentOutput))
                {
                    continue;
                }

                if (photorealVersion is not null)
                {
                    ambiguousJobId = true;
                    break;
                }
                photorealVersion = candidate with
                {
                    Output = currentOutput,
                };
                if (requestedPhotorealJobId is null)
                    break;
            }
            if (ambiguousJobId)
            {
                error = "同じJob IDの実写化バージョンが複数あるため選択できません。";
                return false;
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

    private bool TryRevalidateCapturedVideoSource(
        out VideoSourceChoice source,
        out string error)
    {
        source = null!;
        error = "動画化の入力を選び直してください。";
        if (_videoSourceChoice is not VideoSourceChoice captured
            || SelectedTile() is not Tile { IsRealFile: true } tile)
        {
            return false;
        }

        string requestedSource = captured.ProducerJobId is null
            ? "original"
            : PhotorealVideoSourceRequestPrefix + captured.ProducerJobId;
        if (!TryCaptureVideoSource(
                tile,
                requestedSource,
                out VideoSourceChoice current,
                out error)
            || !Equals(current, captured))
        {
            if (string.IsNullOrWhiteSpace(error))
                error = "動画化の入力が設定中に変わりました。選び直してください。";
            return false;
        }

        source = current;
        return true;
    }

    private void PopulateGalleryVideoSourceMenu(
        MenuItem videoMenu,
        Tile tile)
    {
        videoMenu.Items.Clear();
        var original = new MenuItem
        {
            Header = "Originalから...",
            Tag = "original",
        };
        AutomationProperties.SetName(
            original,
            "Generate video from Original");
        original.Click += GalleryContextVideo_Click;
        videoMenu.Items.Add(original);

        ManagedEnhancementVersion[] photorealVersions =
            GetManagedEnhancementVersionsForPath(tile.Path)
                .Where(static version => string.Equals(
                    version.Operation,
                    "photoreal",
                    StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(version.JobId))
                .ToArray();
        HashSet<string> ambiguousJobIds = photorealVersions
            .GroupBy(static version => version.JobId, StringComparer.Ordinal)
            .Where(static group => group.Count() != 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        int versionNumber = 0;
        foreach (ManagedEnhancementVersion version in photorealVersions)
        {
            if (ambiguousJobIds.Contains(version.JobId))
                continue;
            string request = PhotorealVideoSourceRequestPrefix + version.JobId;
            if (!TryCaptureVideoSource(tile, request, out _, out _))
                continue;

            versionNumber++;
            string newestLabel = versionNumber == 1 ? "（最新）" : "";
            var item = new MenuItem
            {
                Header =
                    $"実写版 {versionNumber}{newestLabel} · "
                    + $"{Path.GetFileName(version.Output.OutputPath)}から...",
                Tag = request,
            };
            AutomationProperties.SetName(
                item,
                $"Generate video from photoreal version {versionNumber}");
            item.Click += GalleryContextVideo_Click;
            videoMenu.Items.Add(item);
        }

        if (versionNumber == 0)
        {
            var unavailable = new MenuItem
            {
                Header = "利用できる実写版はありません",
                IsEnabled = false,
            };
            AutomationProperties.SetName(
                unavailable,
                "No photoreal version available for video generation");
            videoMenu.Items.Add(unavailable);
        }
    }

    private static (
        int FrameCount,
        int EstimatedMinimumSeconds,
        int EstimatedMaximumSeconds)
        EstimateVideoGeneration(
            int durationSeconds,
            int playbackFps,
            int maximumPixelArea,
            int steps)
    {
        int frameCount = 4 * (durationSeconds * playbackFps / 4) + 1;
        double ScaleWan(double baselineSeconds)
            => baselineSeconds
                * frameCount
                / VideoWanEstimateBaselineFrameCount
                * maximumPixelArea
                / VideoEstimateBaselineMaximumPixelArea
                * steps
                / NormalVideoSteps;
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
            _videoMaximumPixelArea,
            VideoQualitySteps(_videoQualityId));
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
        => OpenVideoGenerationBoard(requestedSource: null);

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
        => OpenVideoGenerationBoard(requestedSource: null);

    private void OpenVideoGenerationBoard(string? requestedSource = "original")
    {
        if (ModalVideoGenerationPopup is null)
            return;

        _videoSourceChoice = null;
        string status;
        if (SelectedTile() is not Tile { IsRealFile: true } tile)
        {
            status = "入力画像を選び直してください。設定は確認できますが、入力が確定するまで実行できません。";
        }
        else if (!TryCaptureVideoSource(
                     tile,
                     requestedSource,
                     out VideoSourceChoice source,
                     out string sourceError))
        {
            status = $"入力を確定できません: {sourceError} 設定は確認できますが、実行は無効です。";
            SetTransientStatusToast(sourceError);
        }
        else
        {
            _videoSourceChoice = source;
            status = IsVideoModelRunnable(_videoModelId)
                ? "実行すると既存のAI Jobsキューへ追加します。画像閲覧だけでは開始しません。"
                : "この実験モデルは12GB実測前のため、現在は実行できません。Wanを選ぶと実行できます。";
        }
        SyncVideoGenerationSettingsControls();
        VideoGenerationStatusText.Text = status;
        if (ModalPhotorealSettingsPopup is not null)
            ModalPhotorealSettingsPopup.Visibility = Visibility.Collapsed;
        ModalVideoGenerationPopup.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModalVideoGenerationPopup.Visibility == Visibility.Visible)
                    Keyboard.Focus(ModalVideoPromptTextBox);
            }),
            DispatcherPriority.Input);
    }

    private void CloseVideoGenerationBoard_Click(object sender, RoutedEventArgs e)
        => CloseModalVideoGenerationBoard();

    private void CloseModalVideoGenerationBoard()
    {
        if (ModalVideoGenerationPopup is not null)
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
        ModalVideoGenerateButton?.Focus();
    }

    private void ModalVideoGenerationBackdrop_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModalVideoGenerationPopup.Visibility == Visibility.Visible
            && ReferenceEquals(e.OriginalSource, ModalVideoGenerationPopup))
        {
            CloseModalVideoGenerationBoard();
            e.Handled = true;
        }
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
        MarkVideoStyleAsCustom();
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
        MarkVideoStyleAsCustom();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            IsVideoModelRunnable(_videoModelId)
                ? "Wan2.2を使用します。保存済みです。"
                : "HunyuanVideo 1.5は実写・人物向けの実験候補です。12GB実測前のため実行は無効です。");
        if (!_initializing)
            SaveState();
    }

    private void VideoGenerationQuality_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings
            || AppVideoQualityComboBox is null
            || ModalVideoQualityComboBox is null)
        {
            return;
        }

        ComboBox source = ReferenceEquals(sender, AppVideoQualityComboBox)
            ? AppVideoQualityComboBox
            : ModalVideoQualityComboBox;
        _videoQualityId = SelectedVideoQualityId(source);
        MarkVideoStyleAsCustom();
        SyncVideoGenerationSettingsControls();
        SetVideoGenerationSettingsStatus(
            $"{VideoQualityLabel(_videoQualityId)}を次の動画ジョブに使います。");
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
        MarkVideoStyleAsCustom();
        SyncVideoPromptPeer(source);
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus(
            string.IsNullOrWhiteSpace(_videoPrompt)
                ? "空欄はNormalの既定モーションを使います。保存済みです。"
                : "保存済み。入力した動きが次の動画ジョブに使われます。");
        if (!_initializing)
            SaveState();
    }

    private void SyncVideoPromptPeer(TextBox source)
    {
        TextBox? peer = ReferenceEquals(source, AppVideoPromptTextBox)
            ? ModalVideoPromptTextBox
            : AppVideoPromptTextBox;
        if (peer is null || string.Equals(peer.Text, _videoPrompt, StringComparison.Ordinal))
            return;

        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            peer.Text = _videoPrompt;
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void ResetVideoGenerationSettings_Click(object sender, RoutedEventArgs e)
    {
        _selectedVideoStyleName = null;
        RestoreVideoGenerationSettings(
            null,
            null,
            null,
            null,
            null,
            null);
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoGenerationSettingsStatus(
            "6秒・生成16fps・最終30fps・画素数上限409,600px・標準20 step・Normal既定プロンプトに戻しました。");
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

    private void VideoStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVideoGenerationSettings)
            return;

        VideoStyleChoice? choice = sender switch
        {
            ComboBox comboBox => comboBox.SelectedItem as VideoStyleChoice,
            ListBox listBox => listBox.SelectedItem as VideoStyleChoice,
            _ => null,
        };
        if (choice is null)
            return;

        if (choice.StyleName is null)
        {
            _selectedVideoStyleName = null;
            RefreshVideoStyleControls(updateNameFields: false);
            SetVideoStyleStatus("現在の設定を使用します。Styleにはまだ保存されていません。");
            if (!_initializing)
                SaveState();
            return;
        }

        VideoStyleState? style = FindVideoStyle(choice.StyleName);
        if (style is null)
            return;

        _selectedVideoStyleName = style.Name;
        RestoreVideoGenerationSettings(
            style.DurationSeconds,
            style.PlaybackFps,
            style.MaximumPixelArea,
            style.Prompt,
            style.ModelId,
            style.QualityId);
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus($"「{style.Name}」を反映しました。次に追加する動画ジョブから使われます。");
        if (!_initializing)
            SaveState();
    }

    private void SaveVideoStyle_Click(object sender, RoutedEventArgs e)
    {
        TextBox nameTextBox = ReferenceEquals(sender, SaveModalVideoStyleButton)
            ? ModalVideoStyleNameTextBox
            : AppVideoStyleNameTextBox;
        string name = nameTextBox.Text.Trim();
        if (!IsValidVideoStyleName(name))
        {
            SetVideoStyleStatus($"Style名は1～{MaxVideoStyleNameLength}文字で入力してください。制御文字は使えません。");
            return;
        }

        VideoStyleState style = CreateCurrentVideoStyle(name);
        int existingIndex = _videoStyles.FindIndex(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _videoStyles[existingIndex] = style;
        }
        else
        {
            if (_videoStyles.Count >= MaxVideoStyleCount)
            {
                SetVideoStyleStatus($"Styleは最大{MaxVideoStyleCount}件です。不要なStyleを削除してください。");
                return;
            }
            _videoStyles.Add(style);
        }

        _videoStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        _selectedVideoStyleName = style.Name;
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus(
            existingIndex >= 0
                ? $"「{style.Name}」を現在の設定で上書きしました。"
                : $"「{style.Name}」を保存しました。");
        if (!_initializing)
            SaveState();
    }

    private void DeleteVideoStyle_Click(object sender, RoutedEventArgs e)
    {
        VideoStyleState? style = FindVideoStyle(_selectedVideoStyleName);
        if (style is null)
        {
            SetVideoStyleStatus("削除する保存済みStyleを選んでください。");
            return;
        }

        _videoStyles.Remove(style);
        _selectedVideoStyleName = null;
        RefreshVideoStyleControls(updateNameFields: true);
        SetVideoStyleStatus($"「{style.Name}」を削除しました。現在の設定値はそのまま残ります。");
        if (!_initializing)
            SaveState();
    }

    private void RestoreVideoStyles(
        IEnumerable<VideoStyleState>? styles,
        string? selectedStyleName)
    {
        _videoStyles.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VideoStyleState? candidate in styles ?? [])
        {
            VideoStyleState? normalized = NormalizeVideoStyle(candidate);
            if (normalized is null || !names.Add(normalized.Name))
                continue;

            _videoStyles.Add(normalized);
            if (_videoStyles.Count >= MaxVideoStyleCount)
                break;
        }
        _videoStyles.Sort(static (left, right) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));

        VideoStyleState? selected = FindVideoStyle(selectedStyleName);
        _selectedVideoStyleName = selected is not null && VideoStyleMatchesCurrent(selected)
            ? selected.Name
            : null;
        RefreshVideoStyleControls(updateNameFields: true);
    }

    private static VideoStyleState? NormalizeVideoStyle(VideoStyleState? candidate)
    {
        if (candidate is null)
            return null;

        string name = candidate.Name?.Trim() ?? "";
        if (!IsValidVideoStyleName(name)
            || candidate.ModelId is not (WanVideoModelId or HunyuanVideoModelId)
            || !IsVideoQualitySupported(candidate.QualityId ?? "")
            || !SupportedVideoDurationSeconds.Contains(candidate.DurationSeconds)
            || !SupportedVideoPlaybackFps.Contains(candidate.PlaybackFps)
            || !SupportedVideoMaximumPixelAreas.Contains(candidate.MaximumPixelArea))
        {
            return null;
        }

        string prompt = candidate.Prompt ?? "";
        if (prompt.Length > MaxVideoPromptLength)
            prompt = prompt[..MaxVideoPromptLength];
        return new VideoStyleState
        {
            Name = name,
            ModelId = candidate.ModelId,
            QualityId = candidate.QualityId!,
            DurationSeconds = candidate.DurationSeconds,
            PlaybackFps = candidate.PlaybackFps,
            MaximumPixelArea = candidate.MaximumPixelArea,
            Prompt = prompt,
        };
    }

    private static bool IsValidVideoStyleName(string name)
        => name.Length is >= 1 and <= MaxVideoStyleNameLength
            && !name.Any(char.IsControl);

    private VideoStyleState CreateCurrentVideoStyle(string name)
        => new()
        {
            Name = name,
            ModelId = _videoModelId,
            QualityId = _videoQualityId,
            DurationSeconds = _videoDurationSeconds,
            PlaybackFps = _videoPlaybackFps,
            MaximumPixelArea = _videoMaximumPixelArea,
            Prompt = _videoPrompt,
        };

    private VideoStyleState? FindVideoStyle(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _videoStyles.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool VideoStyleMatchesCurrent(VideoStyleState style)
        => string.Equals(style.ModelId, _videoModelId, StringComparison.Ordinal)
            && string.Equals(style.QualityId, _videoQualityId, StringComparison.Ordinal)
            && style.DurationSeconds == _videoDurationSeconds
            && style.PlaybackFps == _videoPlaybackFps
            && style.MaximumPixelArea == _videoMaximumPixelArea
            && string.Equals(style.Prompt, _videoPrompt, StringComparison.Ordinal);

    private void MarkVideoStyleAsCustom()
    {
        if (_syncingVideoGenerationSettings)
            return;

        bool selectionChanged = _selectedVideoStyleName is not null;
        _selectedVideoStyleName = null;
        if (selectionChanged)
        {
            RefreshVideoStyleControls(updateNameFields: false);
            SetVideoStyleStatus("設定を変更しました。保存済みStyleは上書きされていません。");
        }
        else
        {
            RefreshVideoStyleSummary();
        }
    }

    private void RefreshVideoStyleControls(bool updateNameFields)
    {
        if (ModalVideoStyleComboBox is null
            || AppVideoStyleListBox is null
            || ModalVideoStyleNameTextBox is null
            || AppVideoStyleNameTextBox is null
            || DeleteModalVideoStyleButton is null
            || DeleteAppVideoStyleButton is null)
        {
            return;
        }

        var choices = new List<VideoStyleChoice>
        {
            new("カスタム（現在の設定）", null),
        };
        choices.AddRange(_videoStyles.Select(static style =>
            new VideoStyleChoice(style.Name, style.Name)));
        VideoStyleChoice selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.StyleName, _selectedVideoStyleName, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        bool wasSyncing = _syncingVideoGenerationSettings;
        _syncingVideoGenerationSettings = true;
        try
        {
            ModalVideoStyleComboBox.ItemsSource = choices;
            AppVideoStyleListBox.ItemsSource = choices;
            ModalVideoStyleComboBox.SelectedItem = selectedChoice;
            AppVideoStyleListBox.SelectedItem = selectedChoice;
            bool canDelete = selectedChoice.StyleName is not null;
            DeleteModalVideoStyleButton.IsEnabled = canDelete;
            DeleteAppVideoStyleButton.IsEnabled = canDelete;
            if (updateNameFields)
            {
                string name = selectedChoice.StyleName ?? "";
                ModalVideoStyleNameTextBox.Text = name;
                AppVideoStyleNameTextBox.Text = name;
            }
            RefreshVideoStyleSummary();
        }
        finally
        {
            _syncingVideoGenerationSettings = wasSyncing;
        }
    }

    private void RefreshVideoStyleSummary()
    {
        if (AppVideoStyleSummaryText is null)
            return;

        AppVideoStyleSummaryText.Text =
            $"現在: {VideoModelLabel(_videoModelId)} / {VideoQualityLabel(_videoQualityId)} / {_videoDurationSeconds}秒 / 生成{_videoPlaybackFps}fps / {_videoMaximumPixelArea.ToString("N0", CultureInfo.InvariantCulture)}px";
    }

    private void SetVideoStyleStatus(string message)
    {
        if (ModalVideoStyleStatusText is not null)
            ModalVideoStyleStatusText.Text = message;
        if (AppVideoStyleStatusText is not null)
            AppVideoStyleStatusText.Text = message;
    }

    private List<VideoStyleState>? SnapshotVideoStyles()
        => _videoStyles.Count == 0
            ? null
            : _videoStyles.Select(static style => new VideoStyleState
            {
                Name = style.Name,
                ModelId = style.ModelId,
                QualityId = style.QualityId,
                DurationSeconds = style.DurationSeconds,
                PlaybackFps = style.PlaybackFps,
                MaximumPixelArea = style.MaximumPixelArea,
                Prompt = style.Prompt,
            }).ToList();

    private void RestoreVideoGenerationSettings(
        int? durationSeconds,
        int? playbackFps,
        int? maximumPixelArea,
        string? prompt,
        string? modelId = null,
        string? qualityId = null)
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
        _videoQualityId = IsVideoQualitySupported(qualityId ?? "")
            ? qualityId!
            : DefaultVideoPresetId;
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
            || ModalVideoQualityComboBox is null
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
            SelectVideoQualityId(
                ModalVideoQualityComboBox,
                _videoQualityId);
            ModalVideoPromptTextBox.Text = _videoPrompt;
            if (AppVideoDurationComboBox is not null)
                SelectIntegerTag(AppVideoDurationComboBox, _videoDurationSeconds);
            if (AppVideoFpsComboBox is not null)
                SelectIntegerTag(AppVideoFpsComboBox, _videoPlaybackFps);
            if (AppVideoResolutionComboBox is not null)
                SelectIntegerTag(AppVideoResolutionComboBox, _videoMaximumPixelArea);
            if (AppVideoModelComboBox is not null)
                SelectVideoModelId(AppVideoModelComboBox, _videoModelId);
            if (AppVideoQualityComboBox is not null)
            {
                SelectVideoQualityId(
                    AppVideoQualityComboBox,
                    _videoQualityId);
            }
            if (AppVideoPromptTextBox is not null)
                AppVideoPromptTextBox.Text = _videoPrompt;
            string modelDescription = VideoModelDescription(_videoModelId);
            string qualityLabel = VideoQualityLabel(_videoQualityId);
            ModalVideoPresetText.Text =
                $"{VideoModelLabel(_videoModelId)} · {qualityLabel}";
            ModalVideoModelDescriptionText.Text = modelDescription;
            AppVideoModelDescriptionText.Text = modelDescription;
            bool qualityEnabled = IsVideoModelRunnable(_videoModelId);
            ModalVideoQualityComboBox.IsEnabled = qualityEnabled;
            if (AppVideoQualityComboBox is not null)
                AppVideoQualityComboBox.IsEnabled = qualityEnabled;
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
        bool capturedSourceReady = TryRevalidateCapturedVideoSource(out _, out _);
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
        if (_videoGenerationRequestPending)
            return false;

        if (!TryRevalidateCapturedVideoSource(
                out VideoSourceChoice source,
                out string sourceError))
        {
            if (!string.IsNullOrWhiteSpace(sourceError))
                SetVideoGenerationSettingsStatus(sourceError);
            return false;
        }
        if (!IsVideoModelRunnable(_videoModelId))
            return false;

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

            if (!TryRevalidateCapturedVideoSource(
                    out VideoSourceChoice revalidatedSource,
                    out sourceError)
                || !Equals(revalidatedSource, source))
            {
                _videoSourceChoice = null;
                SetVideoGenerationSettingsStatus(
                    string.IsNullOrWhiteSpace(sourceError)
                        ? "動画化の入力が準備確認中に変わりました。選び直してください。"
                        : sourceError);
                return false;
            }
            source = revalidatedSource;

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
            ModalVideoGenerationPopup.Visibility = Visibility.Collapsed;
            return true;
        }
        finally
        {
            _videoGenerationRequestPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    public bool OpenVideoGenerationBoardForSmoke(
        string? requestedSource = "original")
    {
        OpenVideoGenerationBoard(requestedSource);
        return ModalVideoGenerationPopup.Visibility == Visibility.Visible;
    }

    public bool OpenDisplayedModalVideoGenerationBoardForSmoke()
    {
        OpenModalVideoGeneration_Click(this, new RoutedEventArgs());
        return ModalVideoGenerationPopup.Visibility == Visibility.Visible;
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
            _videoMaximumPixelArea,
            VideoQualitySteps(_videoQualityId));

    public void ConfigureVideoGenerationForSmoke(
        int durationSeconds,
        int playbackFps,
        int maximumPixelArea,
        string prompt,
        string? qualityId = null)
    {
        RestoreVideoGenerationSettings(
            durationSeconds,
            playbackFps,
            maximumPixelArea,
            prompt,
            _videoModelId,
            qualityId ?? _videoQualityId);
        MarkVideoStyleAsCustom();
    }

    public void SelectVideoModelForSmoke(string modelId)
    {
        _videoModelId = modelId;
        SyncVideoGenerationSettingsControls();
    }

    public string VideoModelIdForSmoke => _videoModelId;
    public bool VideoModelRunnableForSmoke =>
        IsVideoModelRunnable(_videoModelId);

    public void SelectVideoQualityForSmoke(string presetId)
    {
        _videoQualityId = IsVideoQualitySupported(presetId)
            ? presetId
            : DefaultVideoPresetId;
        SyncVideoGenerationSettingsControls();
    }

    public string VideoQualityIdForSmoke => _videoQualityId;
    public int VideoQualityStepsForSmoke =>
        VideoQualitySteps(_videoQualityId);

    public bool VideoStyleSurfaceForSmoke
        => ModalVideoStyleComboBox is not null
            && AppVideoStyleListBox is not null
            && ModalVideoStyleNameTextBox.MaxLength == MaxVideoStyleNameLength
            && AppVideoStyleNameTextBox.MaxLength == MaxVideoStyleNameLength
            && AutomationProperties.GetName(ModalVideoStyleComboBox)
                == "Video generation style"
            && AutomationProperties.GetName(AppVideoStyleListBox)
                == "Saved video generation styles";

    public IReadOnlyList<string> VideoStyleNamesForSmoke
        => _videoStyles.Select(static style => style.Name).ToList();

    public string? SelectedVideoStyleNameForSmoke
        => _selectedVideoStyleName;

    public bool SaveVideoStyleForSmoke(string name)
    {
        AppVideoStyleNameTextBox.Text = name;
        SaveVideoStyle_Click(SaveAppVideoStyleButton, new RoutedEventArgs());
        return FindVideoStyle(name) is not null;
    }

    public bool SelectVideoStyleForSmoke(string name)
    {
        VideoStyleChoice? choice = ModalVideoStyleComboBox.Items
            .OfType<VideoStyleChoice>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.StyleName, name, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
            return false;

        ModalVideoStyleComboBox.SelectedItem = choice;
        return string.Equals(_selectedVideoStyleName, name, StringComparison.OrdinalIgnoreCase);
    }

    public bool DeleteSelectedVideoStyleForSmoke()
    {
        string? selectedName = _selectedVideoStyleName;
        DeleteVideoStyle_Click(DeleteAppVideoStyleButton, new RoutedEventArgs());
        return selectedName is not null && FindVideoStyle(selectedName) is null;
    }

    public (string Label, string? ProducerJobId)? VideoSourceForSmoke
        => _videoSourceChoice is null
            ? null
            : (_videoSourceChoice.Label, _videoSourceChoice.ProducerJobId);

    public string[] GalleryVideoSourceRequestsForSmoke
    {
        get
        {
            if (SelectedTile() is not Tile { IsRealFile: true } tile)
                return [];
            var menu = new MenuItem();
            PopulateGalleryVideoSourceMenu(menu, tile);
            return menu.Items
                .OfType<MenuItem>()
                .Select(static item => item.Tag?.ToString())
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .ToArray();
        }
    }

    public bool SelectedPhotorealVideoSourceGlobalJobIdRejectedForSmoke(
        string jobId)
    {
        if (SelectedTile() is not Tile { IsRealFile: true } tile
            || !_ambiguousEnhancementJobIds.Add(jobId))
        {
            return false;
        }

        try
        {
            var menu = new MenuItem();
            PopulateGalleryVideoSourceMenu(menu, tile);
            string[] requests = menu.Items
                .OfType<MenuItem>()
                .Select(static item => item.Tag?.ToString())
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .ToArray();
            return requests.SequenceEqual(["original"], StringComparer.Ordinal)
                && !TryCaptureVideoSource(
                    tile,
                    PhotorealVideoSourceRequestPrefix + jobId,
                    out _,
                    out _);
        }
        finally
        {
            _ambiguousEnhancementJobIds.Remove(jobId);
        }
    }

    public Task<bool> QueueVideoGenerationForSmokeAsync()
        => QueueVideoGenerationAsync();

    public bool VideoGenerationQueueEnabledForSmoke
        => QueueVideoGenerationButton.IsEnabled;

    public bool ModalVideoGenerationBoardVisibleForSmoke
        => ModalVideoGenerationPopup.Visibility == Visibility.Visible;

    public string VideoGenerationStatusForSmoke
        => VideoGenerationStatusText.Text;

    public bool VideoGenerationSurfaceForSmoke
        => ModalVideoGenerateButton is not null
            && ModalVideoGenerationPopup is not null
            && ModalVideoGenerationPopup is Grid
            && VideoStyleSurfaceForSmoke
            && ModalVideoGenerationBoardBorder.MaxHeight <= 680
            && ModalVideoGenerationScrollViewer.VerticalScrollBarVisibility
                == ScrollBarVisibility.Auto
            && ModalVideoModelComboBox.Items.Count == 2
            && AppVideoModelComboBox.Items.Count == 2
            && ModalVideoQualityComboBox.Items.Count == 2
            && AppVideoQualityComboBox.Items.Count == 2
            && string.Equals(
                SelectedVideoQualityId(ModalVideoQualityComboBox),
                _videoQualityId,
                StringComparison.Ordinal)
            && string.Equals(
                SelectedVideoQualityId(AppVideoQualityComboBox),
                _videoQualityId,
                StringComparison.Ordinal)
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
            && string.Equals(
                ModalVideoGenerationEstimateText.Text,
                VideoGenerationEstimateText(),
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

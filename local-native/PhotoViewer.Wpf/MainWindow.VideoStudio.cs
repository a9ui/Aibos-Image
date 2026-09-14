using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _syncingVideoStudio;
    private Window? _videoStyleManagementWindow;

    private void RefreshVideoStudio()
    {
        if (_syncingVideoStudio || VideoSourceKindComboBox is null
            || VideoAutomationSettingsHost is null || VideoSubmissionPreviewExpander is null || ModalBitmap is null) return;
        _syncingVideoStudio = true;
        try
        {
            string selection = _videoProgramOverrideSourceKey == VideoProgramSourceKey() ? _videoProgramSourceOverride : "auto";
            VideoSourceKindComboBox.SelectedIndex = selection switch { "anime" => 1, "photoreal" => 2, _ => 0 };
            VideoSourceFileText.Text = _videoSourceChoice is { } source ? Path.GetFileName(source.SourceIdentity) : "対象画像を選んでください";
            ModalVideoSourceText.Text = _videoSourceChoice is { } chosen
                ? $"{chosen.Label} · 今回は{(EffectiveVideoProgramSourceKind() == "photoreal" ? "実写" : "アニメ")}として動画化"
                : "画像を開いてから動画化を選んでください。";
            if (_videoSourceChoice is { } image)
            {
                // Reuse an already decoded matching image; this panel never
                // reads arbitrary image paths or changes the captured source.
                if (string.Equals(_modalDisplayPath, image.DisplayPath, StringComparison.OrdinalIgnoreCase))
                    VideoSourceThumbnail.Source = ModalBitmap.Source;
                else if (image.DisplayPath == image.SourceIdentity && TryGetVideoGenerationSourceTile(out Tile tile)
                    && HasOriginalResidentThumbnail(tile)) VideoSourceThumbnail.Source = tile.Thumbnail;
                else VideoSourceThumbnail.Source = null;
            }
            else VideoSourceThumbnail.Source = null;
            VideoSourceThumbnail.Visibility = VideoSourceThumbnail.Source is null ? Visibility.Collapsed : Visibility.Visible;
            if (ModalVideoPromptAuthoringHost.Content is VideoPromptAuthoringControl editor) editor.UseVideoMenuLayout();
            if (VideoAutomationSettingsHost.Content is not VideoPromptAutomationControl)
            {
                var options = new VideoPromptAutomationControl();
                options.Changed += apply =>
                {
                    apply(_videoPromptProgram);
                    MarkVideoStyleAsCustom();
                    InvalidateVideoProgramAuthoring();
                    RefreshVideoPromptAuthoringControls();
                };
                VideoAutomationSettingsHost.Content = options;
            }
            ((VideoPromptAutomationControl)VideoAutomationSettingsHost.Content).Load(_videoPromptProgram, _videoEnhanceBeforeEnqueue);
            CancelVideoSubmissionButton.Visibility = _videoAutomaticSubmissionPending && !_videoGenerationRequestPending
                ? Visibility.Visible : Visibility.Collapsed;
            CancelVideoSubmissionButton.IsEnabled = _videoActiveSubmission is not null;
            RefreshVideoSubmissionPreview();
        }
        finally { _syncingVideoStudio = false; }
    }

    private void VideoSourceKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVideoStudio || _syncingVideoAuthoringControls) return;
        if (ModalVideoPromptAuthoringHost.Content is VideoPromptAuthoringControl editor)
            editor.SelectSource(VideoSourceKindComboBox.SelectedIndex switch { 1 => "anime", 2 => "photoreal", _ => "auto" });
    }

    private void VideoSubmissionPreview_Expanded(object sender, RoutedEventArgs e) => RefreshVideoSubmissionPreview();

    private void RefreshVideoSubmissionPreview()
    {
        if (VideoSubmissionPreviewExpander?.IsExpanded != true) return;
        string prompt = _videoPrompt;
        string error = "";
        if (_videoPromptProgram.Enabled)
            _videoPromptProgram.TryResolveH3(EffectiveVideoProgramSourceKind(), VideoProgramSourcePrompt(), out prompt, out error);
        VideoResolvedPromptPreview.Text = prompt;
        VideoSubmissionPreviewHelp.Text = error.Length > 0 ? error : _videoEnhanceBeforeEnqueue
            ? "強化前の本文です。AI強化は「キューに追加」を押したときに行います。編集元の本文は書き換えません。"
            : "現在の選択を反映した本文です。この内容を動画生成へ渡します。";
        bool hasLast = _videoLastSubmittedPrompt.Length > 0;
        VideoLastSubmissionLabel.Visibility = VideoLastSubmissionPreview.Visibility = hasLast ? Visibility.Visible : Visibility.Collapsed;
        VideoLastSubmissionLabel.Text = _videoLastSubmissionEnhanced ? "直前の登録内容（AI強化あり）" : "直前の登録内容（AI強化なし）";
        VideoLastSubmissionPreview.Text = _videoLastSubmittedPrompt;
    }

    private void OpenVideoStyleManagement_Click(object sender, RoutedEventArgs e)
    {
        if (_videoStyleManagementWindow is not null) { _videoStyleManagementWindow.Activate(); return; }
        object content = VideoStyleManagementExpander.Content;
        VideoStyleManagementExpander.Content = null;
        var window = new Window
        {
            Owner = this, Title = "動画スタイルの管理", Width = 560, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(240, ActualHeight - 60), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BgElevated"), Foreground = (Brush)FindResource("TextPrimary"),
            Content = new Border { Padding = new Thickness(20), Child = (UIElement)content },
        };
        _videoStyleManagementWindow = window;
        window.Closed += (_, _) =>
        {
            ((Border)window.Content).Child = null;
            VideoStyleManagementExpander.Content = content;
            _videoStyleManagementWindow = null;
        };
        window.ShowDialog();
    }

    public bool VideoStudioLayoutForSmoke => VideoCommonSettingsPanel.IsVisible
        && ModalVideoH3DurationComboBox.IsVisible && ModalVideoH3ResolutionComboBox.IsVisible
        && !VideoPromptPreparationExpander.IsVisible && !VideoStyleManagementExpander.IsVisible
        && !LegacyMotionDirectorPanel.IsVisible && VideoSourceKindComboBox.IsVisible;
    public void OpenVideoSubmissionPreviewForSmoke() => VideoSubmissionPreviewExpander.IsExpanded = true;
    public string VideoResolvedPreviewForSmoke => VideoResolvedPromptPreview.Text;

    public void RestoreChangedVideoQualityForSmoke()
    {
        int current = ModalVideoH3ResolutionComboBox.SelectedIndex;
        ModalVideoH3ResolutionComboBox.SelectedIndex = current == 0 ? 1 : 0;
        ModalVideoH3ResolutionComboBox.SelectedIndex = current;
    }

    public bool VerifyVideoStyleManagementForSmoke(Action<FrameworkElement> capture)
    {
        object before = VideoStyleManagementExpander.Content;
        bool shown = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_videoStyleManagementWindow is not { } window) return;
            window.UpdateLayout();
            shown = SaveModalVideoStyleButton.IsVisible && DeleteModalVideoStyleButton.IsVisible;
            capture(window);
            window.Close();
        }));
        OpenVideoStyleManagement_Click(this, new RoutedEventArgs());
        return shown && ReferenceEquals(before, VideoStyleManagementExpander.Content) && _videoStyleManagementWindow is null;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _videoEnhanceBeforeEnqueue;
    private bool _videoAutomaticSubmissionPending;
    private bool VideoPromptPreparationPending => _videoH3RewritePending || _videoProgramMetadataPending;

    private bool HasCurrentVideoProgramCandidate()
        => VideoProgramCandidateCanApply() && IsVideoH3PromptCandidateFresh()
            && !string.IsNullOrWhiteSpace(_videoH3PromptCandidate);

    private void RefreshVideoSubmissionPresentation(bool modelRegistered)
    {
        string? programError = ValidateVideoProgramForEnqueue();
        bool needsPreparation = modelRegistered && !_videoEnhanceBeforeEnqueue && programError is not null;
        bool needsReview = needsPreparation && HasCurrentVideoProgramCandidate();
        string label = _videoGenerationRequestPending ? "キューへ登録中…"
            : _videoAutomaticSubmissionPending ? "AIでプロンプトを強化中…" : "H3動画化をキューへ追加";
        string help = VideoPromptPreparationPending
            ? VideoH3Localized("UiVideoSubmitPreparingHelp", "画像と選択内容から生成用プロンプトを準備しています。")
            : needsReview
                ? VideoH3Localized("UiVideoSubmitReviewHelp", "生成用プロンプトができました。内容を確認して「この内容を反映」を押すと、キューへ追加できます。")
                : needsPreparation
                    ? programError!
                    : VideoH3Localized("UiVideoSubmitReadyHelp", "現在の生成用プロンプトで動画をキューへ追加します。");
        QueueVideoGenerationButton.Content = label;
        QueueVideoGenerationButton.ToolTip = help;
        VideoEnhanceBeforeEnqueueCheckBox.IsEnabled = !_videoAutomaticSubmissionPending && !_videoGenerationRequestPending && !VideoPromptPreparationPending;
        AutomationProperties.SetName(QueueVideoGenerationButton, _videoGenerationRequestPending
            ? "Adding video generation job" : "Add video generation job");
        AutomationProperties.SetHelpText(QueueVideoGenerationButton, help);
        if (VideoSubmissionGuideText is not null)
        {
            VideoSubmissionGuideText.Text = help;
            VideoSubmissionGuideBorder.Visibility = modelRegistered && (needsPreparation || VideoPromptPreparationPending)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ModalVideoH3PromptAssistantTitle is not null)
        {
            ModalVideoH3PromptAssistantTitle.Text = VideoH3Localized("UiVideoProgramPreparationTitle", "生成用プロンプト");
            ModalVideoH3PromptAssistantHelp.Text = _videoPromptProgram.Enabled
                ? VideoH3Localized("UiVideoProgramPreparationHelp", "必要な場合だけ、強化内容を先に確認・調整できます。通常は下のチェックとキュー追加ボタンで操作します。")
                : VideoH3Localized("UiVideoH3PromptAssistantHelp", "入力欄のプロンプトをMiniMax H3向けに整えます。");
        }
        if (VideoPromptPreparationStateText is not null)
            VideoPromptPreparationStateText.Text = VideoPromptPreparationPending ? "準備中"
                : needsReview ? "確認・反映が必要" : needsPreparation ? "本文の変更が未反映" : "使用できます";
        if (VideoSubmissionSummaryButton is not null)
            VideoSubmissionSummaryButton.Content = $"{_videoDurationSeconds}秒 · {(_videoMaximumPixelArea >= 414720 ? "高画質" : _videoMaximumPixelArea >= 307200 ? "標準" : "軽量")} · 音声あり  ▾";
    }

    // The preview panel remains available for reviewing enhancement separately.
    private async Task<bool> PrepareVideoPromptForSubmissionAsync()
    {
        if (_videoGenerationRequestPending || VideoPromptPreparationPending) return false;
        VideoPromptPreparationExpander.IsExpanded = true;
        if (!await RewriteVideoPromptProgramAsync())
        {
            SetVideoGenerationSettingsStatus(ModalVideoH3PromptRewriteStatusText.Text);
            ModalVideoH3PromptRewriteStatusText.BringIntoView();
            UpdateVideoGenerationActionControls();
            return false;
        }
        SetVideoGenerationSettingsStatus("");
        OpenVideoPromptPreparation();
        UpdateVideoGenerationActionControls();
        return true;
    }

    private void OpenVideoPromptPreparation_Click(object sender, RoutedEventArgs e) => OpenVideoPromptPreparation();

    private void OpenVideoPromptPreparation()
    {
        SetVideoGenerationSettingsStatus("");
        VideoPromptPreparationExpander.IsExpanded = true;
        if (HasCurrentVideoProgramCandidate())
        {
            ModalVideoH3PromptReviewPanel.BringIntoView();
            Keyboard.Focus(ModalVideoH3PromptCandidateTextBox);
        }
        else
        {
            ModalVideoH3RewritePromptButton.BringIntoView();
            Keyboard.Focus(ModalVideoH3RewritePromptButton);
        }
    }

    private void OpenVideoOutputSettings_Click(object sender, RoutedEventArgs e)
    {
        VideoOutputSettingsExpander.IsExpanded = true;
        ModalVideoH3DurationComboBox.BringIntoView();
        Keyboard.Focus(ModalVideoH3DurationComboBox);
    }

    private bool ApplyVideoCandidateAndShowSubmission()
    {
        if (!ApplyVideoH3PromptCandidate()) return false;
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus("");
        VideoPromptPreparationExpander.IsExpanded = false;
        QueueVideoGenerationButton.BringIntoView();
        Keyboard.Focus(QueueVideoGenerationButton);
        return true;
    }

    private void VideoEnhanceBeforeEnqueue_Changed(object sender, RoutedEventArgs e)
    {
        _videoEnhanceBeforeEnqueue = VideoEnhanceBeforeEnqueueCheckBox.IsChecked == true;
        InvalidateVideoProgramAuthoring();
        RefreshVideoPromptAuthoringControls();
        UpdateVideoGenerationActionControls();
    }

    private async Task<bool> SubmitVideoGenerationAsync()
    {
        if (_videoAutomaticSubmissionPending || _videoGenerationRequestPending || VideoPromptPreparationPending) return false;
        if (!_videoEnhanceBeforeEnqueue) return await QueueVideoGenerationAsync();
        _videoAutomaticSubmissionPending = true;
        UpdateVideoGenerationActionControls();
        try
        {
            SetVideoGenerationSettingsStatus("画像と本文からプロンプトを強化しています。完了後にキューへ登録します。");
            if (!await RewriteVideoPromptProgramAsync())
            {
                SetVideoGenerationSettingsStatus(ModalVideoH3PromptRewriteStatusText.Text);
                return false;
            }
            if (!ApplyVideoH3PromptCandidate())
            {
                // An identical valid result needs no edit, but still must match
                // the captured program/source and pass the existing H3 checks.
                if (!VideoProgramCandidateCanApply() || !IsVideoH3PromptCandidateFresh()
                    || !TryNormalizeAndValidateVideoH3Prompt(_videoH3PromptCandidate, out string same) || same != _videoPrompt)
                {
                    SetVideoGenerationSettingsStatus("強化中に画像または本文が変わりました。確認して追加し直してください。");
                    return false;
                }
                RecordAppliedVideoProgram();
            }
            return await QueueVideoGenerationAsync();
        }
        finally
        {
            _videoAutomaticSubmissionPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    public void SetVideoEnhanceBeforeEnqueueForSmoke(bool value) => VideoEnhanceBeforeEnqueueCheckBox.IsChecked = value;
    public Task<bool> SubmitVideoGenerationForSmokeAsync() => SubmitVideoGenerationAsync();
    public Task<bool> PrepareVideoPromptForSubmissionForSmokeAsync() => PrepareVideoPromptForSubmissionAsync();
    public void OpenVideoPromptPreparationForSmoke() => OpenVideoPromptPreparation();
    public bool ApplyVideoCandidateAndShowSubmissionForSmoke() => ApplyVideoCandidateAndShowSubmission();
    public string VideoSubmissionActionForSmoke => QueueVideoGenerationButton.Content?.ToString() ?? "";
    public string VideoSubmissionGuideForSmoke => VideoSubmissionGuideText.Text;
    public string VideoPreparationTitleForSmoke => ModalVideoH3PromptAssistantTitle.Text;
    public FrameworkElement VideoReviewPanelForSmoke => ModalVideoH3PromptReviewPanel;
    public FrameworkElement VideoSubmissionGuidePanelForSmoke => VideoSubmissionGuideBorder;
    public FrameworkElement VideoSubmissionFooterForSmoke => VideoSubmissionFooter;
    public bool VideoPreparationExpandedForSmoke => VideoPromptPreparationExpander.IsExpanded;
    public bool VideoMenuDetailsCollapsedForSmoke => !VideoStyleManagementExpander.IsExpanded && !VideoOutputSettingsExpander.IsExpanded;
    public bool VideoFullH3PromptPreservesSourceForSmoke => ((VideoPromptAuthoringControl)ModalVideoPromptAuthoringHost.Content).FullH3PreservesSourceForSmoke();
    public bool VideoSubmissionFooterFixedForSmoke()
    {
        ModalVideoGenerationScrollViewer.ScrollToTop();
        UpdateLayout();
        double top = QueueVideoGenerationButton.TranslatePoint(new Point(), ModalVideoGenerationBoardBorder).Y;
        ModalVideoGenerationScrollViewer.ScrollToBottom();
        UpdateLayout();
        double bottom = QueueVideoGenerationButton.TranslatePoint(new Point(), ModalVideoGenerationBoardBorder).Y;
        bool fixedVisible = Math.Abs(top - bottom) < 1 && top >= 0
            && bottom + QueueVideoGenerationButton.ActualHeight <= ModalVideoGenerationBoardBorder.ActualHeight;
        ModalVideoGenerationScrollViewer.ScrollToTop();
        UpdateLayout();
        return fixedVisible;
    }
}

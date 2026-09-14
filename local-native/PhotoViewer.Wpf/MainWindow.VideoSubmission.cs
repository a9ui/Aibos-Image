using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _videoEnhanceAtExecution;
    private bool _videoAutomaticSubmissionPending;
    private bool VideoPromptPreparationPending => _videoH3RewritePending || _videoProgramMetadataPending;

    private bool HasCurrentVideoProgramCandidate()
        => VideoProgramCandidateCanApply() && IsVideoH3PromptCandidateFresh()
            && !string.IsNullOrWhiteSpace(_videoH3PromptCandidate);

    private void RefreshVideoSubmissionPresentation(bool modelRegistered)
    {
        string? programError = ValidateVideoProgramForEnqueue();
        bool needsPreparation = modelRegistered && !_videoEnhanceAtExecution && programError is not null;
        bool needsReview = needsPreparation && HasCurrentVideoProgramCandidate();
        string label = _videoGenerationRequestPending ? "キューへ登録中…"
            : _videoAutomaticSubmissionPending ? "キューへ登録中…" : "キューに追加";
        string help = _videoAutomaticSubmissionPending
            ? "キューへ登録しています。AI強化は、このジョブの順番が来てから実行します。"
            : needsPreparation ? programError!
            : _videoEnhanceAtExecution ? "先にキューへ追加します。順番が来ると、AIでプロンプトを強化してから動画を生成します。"
            : "現在の本文と選択内容で、キューに追加します。";
        QueueVideoGenerationButton.Content = label;
        QueueVideoGenerationButton.ToolTip = help;
        VideoEnhanceAtExecutionCheckBox.IsEnabled = !_videoGenerationRequestPending;
        AutomationProperties.SetName(QueueVideoGenerationButton, _videoGenerationRequestPending
            ? "Adding video generation job" : "Add video generation job");
        AutomationProperties.SetHelpText(QueueVideoGenerationButton, help);
        if (VideoSubmissionGuideText is not null)
        {
            VideoSubmissionGuideText.Text = help;
            VideoSubmissionGuideBorder.Visibility = modelRegistered && needsPreparation
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
        RefreshVideoStudio();
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

    private void VideoEnhanceAtExecution_Changed(object sender, RoutedEventArgs e)
    {
        _videoEnhanceAtExecution = VideoEnhanceAtExecutionCheckBox.IsChecked == true;
        InvalidateVideoProgramAuthoring();
        RefreshVideoPromptAuthoringControls();
        UpdateVideoGenerationActionControls();
    }

    private long _videoSubmissionRevision;
    private long? _videoActiveSubmission;
    private string _videoSubmissionStopReason = "";
    private string _videoLastSubmittedPrompt = "";
    private bool _videoLastSubmissionEnhanced;

    private string VideoSubmissionInput()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Program = _videoPromptProgram, Settings = CurrentVideoGenerationRequestSettings(),
            Source = VideoProgramSourceKey(), Mode = _videoH3RewriteMode,
            Kind = EffectiveVideoProgramSourceKind(),
            Enhance = _videoEnhanceAtExecution, ValidSteps = _videoStepsInputValid,
        });

    private void InvalidateAutomaticVideoSubmission()
    {
        // Never revive a canceled attempt when an edit is later undone.
        if (_videoActiveSubmission is null) return;
        _videoActiveSubmission = null;
        _videoSubmissionStopReason = "内容が変わったため、今回の追加を取り消しました。新しい内容で追加してください。";
        SetVideoGenerationSettingsStatus(_videoSubmissionStopReason);
    }

    private void CancelVideoSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (!_videoAutomaticSubmissionPending || _videoGenerationRequestPending) return;
        _videoActiveSubmission = null;
        CancelVideoH3PromptRewrite();
        _videoSubmissionStopReason = "追加を取り消しました。キューには追加していません。";
        SetVideoGenerationSettingsStatus(_videoSubmissionStopReason);
        RefreshVideoSubmissionPresentation(IsMiniMaxH3VideoModel(_videoModelId));
    }

    private async Task<bool> SubmitVideoGenerationAsync()
    {
        if (_videoAutomaticSubmissionPending || _videoGenerationRequestPending || VideoPromptPreparationPending) return false;
        ApplyDirectVideoSourceVariant();
        if (!_videoEnhanceAtExecution) return await QueueVideoGenerationAsync();
        long attempt = ++_videoSubmissionRevision;
        _videoActiveSubmission = attempt;
        _videoSubmissionStopReason = "";
        string input = VideoSubmissionInput();
        _videoAutomaticSubmissionPending = true;
        UpdateVideoGenerationActionControls();
        string? ValidateAttempt() => _videoActiveSubmission == attempt && input == VideoSubmissionInput()
            ? null : "内容の変更または取消しのため追加していません。新しい内容で追加してください。";
        try
        {
            string instruction = _videoPrompt;
            if (_videoPromptProgram.Enabled && !_videoPromptProgram.TryCompile(
                EffectiveVideoProgramSourceKind(), VideoProgramSourcePrompt(), MiniMaxH3FrameCountForDuration(_videoDurationSeconds),
                out instruction, out string error))
            {
                SetVideoGenerationSettingsStatus(error);
                return false;
            }
            if (!TryBuildVideoH3RewriteRequestPrompt(instruction, _videoH3RewriteMode, out instruction))
            {
                SetVideoGenerationSettingsStatus("AI強化用の指示が長すぎます。8,000文字以内にしてください。");
                return false;
            }
            // Freeze only the instruction. Inference belongs to the claimed job.
            return await QueueVideoGenerationAsync(validateSubmission: ValidateAttempt,
                promptEnhancement: new VideoPromptEnhancement(1, instruction));
        }
        finally
        {
            _videoActiveSubmission = null;
            _videoAutomaticSubmissionPending = false;
            UpdateVideoGenerationActionControls();
        }
    }

    private void RecordVideoSubmissionPreview(string prompt, bool enhanced)
    {
        _videoLastSubmittedPrompt = prompt;
        _videoLastSubmissionEnhanced = enhanced;
        RefreshVideoSubmissionPreview();
    }

    public void CancelVideoSubmissionForSmoke() => CancelVideoSubmission_Click(this, new RoutedEventArgs());
    public void CloseVideoSubmissionForSmoke() => CloseModalVideoGenerationBoard();
    public string LastSubmittedVideoPromptForSmoke => _videoLastSubmittedPrompt;
    public bool VideoSubmissionCancelVisibleForSmoke => CancelVideoSubmissionButton.Visibility == Visibility.Visible;

    public void SetVideoEnhanceAtExecutionForSmoke(bool value) => VideoEnhanceAtExecutionCheckBox.IsChecked = value;
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

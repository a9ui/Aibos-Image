using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool VideoPromptPreparationPending => _videoH3RewritePending || _videoProgramMetadataPending;

    private bool HasCurrentVideoProgramCandidate()
        => VideoProgramCandidateCanApply() && IsVideoH3PromptCandidateFresh()
            && !string.IsNullOrWhiteSpace(_videoH3PromptCandidate);

    private void RefreshVideoSubmissionPresentation(bool modelRegistered)
    {
        bool needsPreparation = modelRegistered && ValidateVideoProgramForEnqueue() is not null;
        bool needsReview = needsPreparation && HasCurrentVideoProgramCandidate();
        string label = _videoGenerationRequestPending ? "追加中..."
            : VideoPromptPreparationPending ? VideoH3Localized("UiVideoSubmitPreparing", "生成用プロンプトを準備中…")
            : !modelRegistered ? "動画モデルを確認"
            : needsReview ? VideoH3Localized("UiVideoSubmitReview", "生成用プロンプトを確認")
            : needsPreparation ? VideoH3Localized("UiVideoSubmitPrepare", "生成用プロンプトを準備")
            : "H3動画化をキューへ追加";
        string help = VideoPromptPreparationPending
            ? VideoH3Localized("UiVideoSubmitPreparingHelp", "画像と選択内容から生成用プロンプトを準備しています。")
            : needsReview
                ? VideoH3Localized("UiVideoSubmitReviewHelp", "生成用プロンプトができました。内容を確認して「この内容を反映」を押すと、キューへ追加できます。")
                : needsPreparation
                    ? VideoH3Localized("UiVideoSubmitPrepareHelp", "本文の選択を動画に使うため、生成用プロンプトの準備が必要です。下のボタンで準備し、内容を確認して反映してください。")
                    : VideoH3Localized("UiVideoSubmitReadyHelp", "現在の生成用プロンプトで動画をキューへ追加します。");
        QueueVideoGenerationButton.Content = label;
        QueueVideoGenerationButton.ToolTip = help;
        AutomationProperties.SetName(QueueVideoGenerationButton, _videoGenerationRequestPending
            ? "Adding video generation job" : needsPreparation || VideoPromptPreparationPending
                ? label : "Add video generation job");
        AutomationProperties.SetHelpText(QueueVideoGenerationButton, help);
        if (VideoSubmissionGuideText is not null)
        {
            VideoSubmissionGuideText.Text = help;
            VideoSubmissionGuideBorder.Visibility = modelRegistered && (needsPreparation || VideoPromptPreparationPending)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ModalVideoH3PromptAssistantTitle is not null)
        {
            ModalVideoH3PromptAssistantTitle.Text = _videoPromptProgram.Enabled
                ? VideoH3Localized("UiVideoProgramPreparationTitle", "生成用プロンプトの準備・確認")
                : VideoH3Localized("UiMotionDirectorAiProposalTitle", "AI提案（任意）");
            ModalVideoH3PromptAssistantHelp.Text = _videoPromptProgram.Enabled
                ? VideoH3Localized("UiVideoProgramPreparationHelp", "本文で選んだ内容をローカルAIで動画用に整えます。内容を確認して反映した後、キューへ追加してください。")
                : VideoH3Localized("UiVideoH3PromptAssistantHelp", "入力欄のプロンプトをMiniMax H3向けに整えます。");
        }
    }

    // The visible action guides preparation and review. Durable publication
    // remains a separate click, through the unchanged enqueue validation.
    private async Task<bool> SubmitVideoGenerationAsync()
    {
        if (_videoGenerationRequestPending || VideoPromptPreparationPending) return false;
        if (IsMiniMaxH3VideoModel(_videoModelId) && ValidateVideoProgramForEnqueue() is not null)
        {
            if (!HasCurrentVideoProgramCandidate())
            {
                ModalVideoH3RewritePromptButton.BringIntoView();
                if (!await RewriteVideoPromptProgramAsync())
                {
                    SetVideoGenerationSettingsStatus(ModalVideoH3PromptRewriteStatusText.Text);
                    ModalVideoH3PromptRewriteStatusText.BringIntoView();
                    UpdateVideoGenerationActionControls();
                    return false;
                }
            }
            SetVideoGenerationSettingsStatus(VideoH3Localized("UiVideoSubmitReviewHelp",
                "生成用プロンプトができました。内容を確認して「この内容を反映」を押すと、キューへ追加できます。"));
            ModalVideoH3PromptReviewPanel.BringIntoView();
            Keyboard.Focus(ModalVideoH3PromptCandidateTextBox);
            UpdateVideoGenerationActionControls();
            return false;
        }
        return await QueueVideoGenerationAsync();
    }

    private bool ApplyVideoCandidateAndShowSubmission()
    {
        if (!ApplyVideoH3PromptCandidate()) return false;
        UpdateVideoGenerationActionControls();
        SetVideoGenerationSettingsStatus(VideoH3Localized("UiVideoSubmitAppliedHelp",
            "生成用プロンプトを反映しました。下の「H3動画化をキューへ追加」で登録できます。"));
        QueueVideoGenerationButton.BringIntoView();
        Keyboard.Focus(QueueVideoGenerationButton);
        return true;
    }

    public Task<bool> SubmitVideoGenerationForSmokeAsync() => SubmitVideoGenerationAsync();
    public bool ApplyVideoCandidateAndShowSubmissionForSmoke() => ApplyVideoCandidateAndShowSubmission();
    public string VideoSubmissionActionForSmoke => QueueVideoGenerationButton.Content?.ToString() ?? "";
    public string VideoSubmissionGuideForSmoke => VideoSubmissionGuideText.Text;
    public string VideoPreparationTitleForSmoke => ModalVideoH3PromptAssistantTitle.Text;
    public FrameworkElement VideoReviewPanelForSmoke => ModalVideoH3PromptReviewPanel;
    public FrameworkElement VideoSubmissionGuidePanelForSmoke => VideoSubmissionGuideBorder;
}

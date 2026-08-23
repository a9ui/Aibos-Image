using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _syncingMotionDirectorControls;
    private bool _motionDirectorCandidateOrigin;
    private long _motionDirectorSelectionRevision;
    private long _motionDirectorCandidateSelectionRevision;
    private int _motionDirectorCandidateFrameCount;

    private void MotionDirectorAction_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_syncingMotionDirectorControls
            || ModalMotionDirectorActionsPanel is null)
        {
            return;
        }

        CheckBox[] selected = MotionDirectorActionCheckBoxes()
            .Where(static checkBox => checkBox.IsChecked == true)
            .ToArray();
        if (selected.Length > MotionDirectorPlanner.MaximumSelectedActions
            && sender is CheckBox changed)
        {
            _syncingMotionDirectorControls = true;
            try
            {
                changed.IsChecked = false;
            }
            finally
            {
                _syncingMotionDirectorControls = false;
            }
        }

        _motionDirectorSelectionRevision++;
        RefreshVideoH3PromptRewriteControls();
    }

    private void MotionDirectorCamera_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingMotionDirectorControls
            || ModalMotionDirectorCameraComboBox is null)
        {
            return;
        }

        _motionDirectorSelectionRevision++;
        RefreshVideoH3PromptRewriteControls();
    }

    private void BuildMotionDirectorCandidate_Click(
        object sender,
        RoutedEventArgs e)
        => BuildMotionDirectorCandidate();

    private bool BuildMotionDirectorCandidate()
    {
        if (_videoH3RewritePending
            || !IsMiniMaxH3VideoModel(_videoModelId)
            || !TryBuildCurrentMotionDirectorPlan(out MotionDirectorPlan plan))
        {
            RefreshVideoH3PromptRewriteControls();
            return false;
        }
        if (!TryNormalizeAndValidateVideoH3Prompt(
                plan.CandidatePrompt,
                out string normalizedCandidate))
        {
            SetVideoH3PromptRewriteStatus(VideoH3Localized(
                "UiVideoH3StatusInvalidCandidate",
                "候補はH3形式ではないか、8000文字を超えています。入力には反映していません。"));
            return false;
        }
        if (!TryCaptureVideoH3SourceStamp(
                out _,
                out VideoH3SourceStamp sourceStamp,
                out string sourceError))
        {
            SetVideoH3PromptRewriteStatus(sourceError);
            return false;
        }

        _videoH3PromptCandidate = normalizedCandidate;
        _videoH3CandidateBasePrompt = _videoPrompt;
        _videoH3CandidateSourceStamp = sourceStamp;
        _videoH3CandidateStyleName = _selectedVideoStyleName;
        _videoH3CandidateMode = null;
        _videoH3CandidateContextRevision = _videoH3RewriteContextRevision;
        _videoH3CandidateRewriteRevision = null;
        _videoH3CandidateSourceSha256 = null;
        _motionDirectorCandidateOrigin = true;
        _motionDirectorCandidateSelectionRevision =
            _motionDirectorSelectionRevision;
        _motionDirectorCandidateFrameCount = plan.FrameCount;
        RefreshVideoH3PromptRewriteControls(updateStatus: false);
        SetVideoH3PromptRewriteStatus(VideoH3Localized(
            "UiMotionDirectorStatusReady",
            "時間割つき候補を作りました。入力プロンプトへ反映するまで動画化には使われません。"));
        return true;
    }

    private bool TryBuildCurrentMotionDirectorPlan(
        out MotionDirectorPlan plan)
        => MotionDirectorPlanner.TryBuild(
            MiniMaxH3FrameCountForDuration(_videoDurationSeconds),
            MiniMaxH3VideoPlaybackFps,
            SelectedMotionDirectorActionIds(),
            SelectedMotionDirectorCameraId(),
            out plan,
            out _);

    private IReadOnlyList<string> SelectedMotionDirectorActionIds()
        => MotionDirectorActionCheckBoxes()
            .Where(static checkBox => checkBox.IsChecked == true)
            .Select(static checkBox => checkBox.Tag?.ToString() ?? "")
            .Where(static id => id.Length > 0)
            .ToArray();

    private string SelectedMotionDirectorCameraId()
        => (ModalMotionDirectorCameraComboBox?.SelectedItem as ComboBoxItem)
            ?.Tag?.ToString() ?? "fixed";

    private IEnumerable<CheckBox> MotionDirectorActionCheckBoxes()
        => ModalMotionDirectorActionsPanel?.Children
                .OfType<CheckBox>()
            ?? Enumerable.Empty<CheckBox>();

    private void RefreshMotionDirectorControls(bool h3Selected)
    {
        if (ModalMotionDirectorActionsPanel is null
            || ModalMotionDirectorCameraComboBox is null
            || ModalMotionDirectorTimelineText is null
            || ModalMotionDirectorWarningText is null
            || ModalMotionDirectorRiskText is null
            || ModalMotionDirectorBuildButton is null)
        {
            return;
        }

        CheckBox[] actionCheckBoxes = MotionDirectorActionCheckBoxes().ToArray();
        int selectedCount = actionCheckBoxes.Count(static checkBox =>
            checkBox.IsChecked == true);
        bool directorEnabled = h3Selected && !_videoH3RewritePending;
        _syncingMotionDirectorControls = true;
        try
        {
            foreach (CheckBox checkBox in actionCheckBoxes)
            {
                checkBox.IsEnabled = directorEnabled
                    && (checkBox.IsChecked == true
                        || selectedCount < MotionDirectorPlanner.MaximumSelectedActions);
            }
            ModalMotionDirectorCameraComboBox.IsEnabled = directorEnabled;
        }
        finally
        {
            _syncingMotionDirectorControls = false;
        }

        if (!TryBuildCurrentMotionDirectorPlan(out MotionDirectorPlan plan))
        {
            ModalMotionDirectorBuildButton.IsEnabled = false;
            ModalMotionDirectorTimelineText.Text = VideoH3Localized(
                "UiMotionDirectorSelectAction",
                "動きを1つ以上選んでください。");
            ModalMotionDirectorRiskText.Text = "";
            ModalMotionDirectorWarningText.Text = "";
            ModalMotionDirectorWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        ModalMotionDirectorBuildButton.IsEnabled = directorEnabled;
        ModalMotionDirectorTimelineText.Text = DescribeMotionDirectorTimeline(plan);
        string riskLabel = plan.Risk switch
        {
            MotionDirectorRiskLevel.High => VideoH3Localized(
                "UiMotionDirectorRiskHigh",
                "Risk 高"),
            MotionDirectorRiskLevel.Medium => VideoH3Localized(
                "UiMotionDirectorRiskMedium",
                "Risk 中"),
            _ => VideoH3Localized(
                "UiMotionDirectorRiskLow",
                "Risk 低"),
        };
        ModalMotionDirectorRiskText.Text = riskLabel;

        string warning = DescribeMotionDirectorWarning(plan);
        ModalMotionDirectorWarningText.Text = warning;
        ModalMotionDirectorWarningText.Visibility = warning.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private string DescribeMotionDirectorTimeline(MotionDirectorPlan plan)
    {
        var result = new StringBuilder();
        result.Append(MotionDirectorPlanner.FormatSeconds(
            plan.FrameCount,
            plan.PlaybackFps));
        result.Append(" s · ");
        result.Append(plan.FrameCount.ToString(CultureInfo.InvariantCulture));
        result.Append("f @ 24fps");

        foreach (MotionDirectorActionDefinition action in plan.Actions)
        {
            MotionDirectorSegment first = plan.Segments.First(segment =>
                string.Equals(segment.ActionId, action.Id, StringComparison.Ordinal));
            MotionDirectorSegment last = plan.Segments.Last(segment =>
                string.Equals(segment.ActionId, action.Id, StringComparison.Ordinal));
            result.Append('\n');
            result.Append(first.StartFrame.ToString(
                CultureInfo.InvariantCulture));
            result.Append('–');
            result.Append(last.EndFrame.ToString(
                CultureInfo.InvariantCulture));
            result.Append("f · ");
            result.Append(MotionDirectorPlanner.FormatSeconds(
                first.StartFrame,
                plan.PlaybackFps));
            result.Append('–');
            result.Append(MotionDirectorPlanner.FormatSeconds(
                last.EndFrame,
                plan.PlaybackFps));
            result.Append("  ");
            result.Append(TryFindResource(action.LabelResourceKey) as string
                ?? action.EnglishLabel);
        }

        MotionDirectorSegment hold = plan.Segments[^1];
        result.Append('\n');
        result.Append(hold.StartFrame.ToString(CultureInfo.InvariantCulture));
        result.Append('–');
        result.Append(hold.EndFrame.ToString(CultureInfo.InvariantCulture));
        result.Append("f · ");
        result.Append(MotionDirectorPlanner.FormatSeconds(
            hold.StartFrame,
            plan.PlaybackFps));
        result.Append('–');
        result.Append(MotionDirectorPlanner.FormatSeconds(
            hold.EndFrame,
            plan.PlaybackFps));
        result.Append("  ");
        result.Append(VideoH3Localized("UiMotionDirectorHold", "静かなhold"));
        return result.ToString();
    }

    private string DescribeMotionDirectorWarning(MotionDirectorPlan plan)
    {
        var warnings = new List<string>();
        if (string.Equals(
                plan.WarningResourceKey,
                "UiMotionDirectorWarningFallback",
                StringComparison.Ordinal))
        {
            warnings.Add(VideoH3Localized(
                "UiMotionDirectorWarningFallback",
                "大きな動きと追従カメラは崩れやすいため、今回は固定カメラへ安全に切り替えます。"));
        }
        if (plan.DroppedActions.Count > 0)
        {
            string names = string.Join(
                "、",
                plan.DroppedActions.Select(action =>
                    TryFindResource(action.LabelResourceKey) as string
                        ?? action.EnglishLabel));
            string format = VideoH3Localized(
                "UiMotionDirectorWarningDropped",
                "尺に収まらないため、優先度の低い動き「{0}」を外します。");
            warnings.Add(string.Format(
                CultureInfo.CurrentCulture,
                format,
                names));
        }
        return string.Join(" ", warnings);
    }

    private void ClearMotionDirectorCandidateOrigin()
    {
        _motionDirectorCandidateOrigin = false;
        _motionDirectorCandidateSelectionRevision = 0;
        _motionDirectorCandidateFrameCount = 0;
    }

    private bool IsMotionDirectorCandidateFresh()
        => _motionDirectorCandidateOrigin
            && IsMiniMaxH3VideoModel(_videoModelId)
            && MotionDirectorCandidateContextMatches()
            && _motionDirectorCandidateSelectionRevision
                == _motionDirectorSelectionRevision
            && _motionDirectorCandidateFrameCount
                == MiniMaxH3FrameCountForDuration(_videoDurationSeconds)
            && _videoH3CandidateSourceStamp is VideoH3SourceStamp priorSource
            && TryCaptureVideoH3SourceStamp(
                out _,
                out VideoH3SourceStamp currentSource,
                out _)
            && VideoH3SourceStampsEqual(priorSource, currentSource);

    private bool MotionDirectorCandidateContextMatches()
        => string.Equals(
                _videoH3CandidateBasePrompt,
                _videoPrompt,
                StringComparison.Ordinal)
            && string.Equals(
                _videoH3CandidateStyleName,
                _selectedVideoStyleName,
                StringComparison.Ordinal)
            && _videoH3CandidateContextRevision
                == _videoH3RewriteContextRevision;

    public bool BuildMotionDirectorCandidateForSmoke()
        => BuildMotionDirectorCandidate();

    public void SetMotionDirectorSelectionForSmoke(
        IEnumerable<string> actionIds,
        string cameraId)
    {
        var selected = new HashSet<string>(actionIds, StringComparer.Ordinal);
        _syncingMotionDirectorControls = true;
        try
        {
            foreach (CheckBox checkBox in MotionDirectorActionCheckBoxes())
            {
                checkBox.IsChecked = selected.Contains(
                    checkBox.Tag?.ToString() ?? "");
            }
            ModalMotionDirectorCameraComboBox.SelectedItem =
                ModalMotionDirectorCameraComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Tag?.ToString(),
                        cameraId,
                        StringComparison.Ordinal));
        }
        finally
        {
            _syncingMotionDirectorControls = false;
        }
        _motionDirectorSelectionRevision++;
        RefreshVideoH3PromptRewriteControls();
    }

    public bool MotionDirectorCandidateFreshForSmoke
        => IsMotionDirectorCandidateFresh();

    public void SetMotionDirectorStyleContextForSmoke(string? styleName)
    {
        _selectedVideoStyleName = styleName;
        VideoH3PromptRewriteContextChanged();
    }

    public string MotionDirectorTimelineForSmoke
        => ModalMotionDirectorTimelineText.Text;

    public string MotionDirectorWarningForSmoke
        => ModalMotionDirectorWarningText.Text;

    public bool MotionDirectorBoardWidthContractForSmoke
        => Math.Abs(ModalVideoGenerationBoardBorder.Width - 430d) < 0.01
            && ModalMotionDirectorActionsPanel.Orientation
                == Orientation.Horizontal
            && ModalMotionDirectorTimelineText.TextWrapping
                == TextWrapping.Wrap;

    public IReadOnlyList<string> MotionDirectorSurfaceIssuesForSmoke
    {
        get
        {
            var issues = new List<string>();
            CheckBox[] actions = MotionDirectorActionCheckBoxes().ToArray();
            if (actions.Length != MotionDirectorPlanner.ActionCatalog.Count)
                issues.Add("action-count");
            if (actions.Any(checkBox =>
                    AutomationProperties.GetName(checkBox).Length == 0
                    || AutomationProperties.GetHelpText(checkBox).Length == 0))
            {
                issues.Add("action-a11y");
            }
            if (ModalMotionDirectorCameraComboBox.Items.Count
                    != MotionDirectorPlanner.CameraCatalog.Count
                || AutomationProperties.GetName(
                    ModalMotionDirectorCameraComboBox).Length == 0
                || AutomationProperties.GetHelpText(
                    ModalMotionDirectorCameraComboBox).Length == 0)
            {
                issues.Add("camera-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalMotionDirectorBuildButton).Length == 0
                || AutomationProperties.GetHelpText(
                    ModalMotionDirectorBuildButton).Length == 0)
            {
                issues.Add("build-a11y");
            }
            if (AutomationProperties.GetName(
                    ModalMotionDirectorTimelineText).Length == 0)
            {
                issues.Add("timeline-a11y");
            }
            if (ModalVideoH3RewritePromptButton.Visibility
                != Visibility.Visible)
            {
                issues.Add("ai-proposal");
            }
            return issues;
        }
    }
}

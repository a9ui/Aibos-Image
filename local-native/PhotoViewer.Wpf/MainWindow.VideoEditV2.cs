using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int VideoEditV2MinimumSteps = 1;
    private const int VideoEditV2MaximumSteps = 40;
    private const int VideoEditV2DefaultSteps = 20;

    private VideoEditV2SourceChoice? _videoEditV2Source;
    private VideoEditV2SelectionPlan? _videoEditV2Plan;
    private VideoEditV2CompiledCandidate? _videoEditV2Candidate;
    private VideoEditV2ExternalProbe? _videoEditV2ExternalProbe;
    private bool _videoEditV2CandidateStale;
    private bool _videoEditV2Syncing;
    private bool _videoEditV2MediaHooked;
    private bool _videoEditV2Closing;
    private bool _videoEditV2LastCloseWasStale;
    private int _videoEditV2StartAttemptCount;

    private sealed record VideoEditV2SourceChoice(
        string SourceStamp,
        string BaseSourceStamp,
        string DisplayName,
        bool Managed,
        bool ExactTimeline,
        int FpsNumerator,
        int FpsDenominator,
        int FrameCount,
        double PlaybackDurationSeconds,
        int PlaybackWidth,
        int PlaybackHeight,
        string? SourceVideoJobId,
        ExternalVideoSourceSeamSmokeSnapshot? ExternalSeam);

    private sealed record VideoEditV2ExternalProbe(
        string BaseSourceStamp,
        int FpsNumerator,
        int FpsDenominator,
        int FrameCount,
        string ProbeRevision);

    private sealed record VideoEditV2CompiledCandidate(
        string BackendPrompt,
        string SummaryJa,
        string CompilerRevision,
        string ContextDigest,
        string SourceStamp,
        string ContextStamp);

    private string VideoEditV2Text(string key)
        => FindResource(key) as string ?? key;

    private string VideoEditV2Format(string key, params object[] values)
        => string.Format(
            CultureInfo.CurrentCulture,
            VideoEditV2Text(key),
            values);

    private static string VideoEditV2BaseStamp(
        ExternalVideoSourceSeamSmokeSnapshot capture)
        => string.Join(
            ':',
            "displayed-file",
            capture.Generation.ToString(CultureInfo.InvariantCulture),
            capture.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
            capture.FileIndex.ToString(CultureInfo.InvariantCulture),
            capture.Length.ToString(CultureInfo.InvariantCulture),
            capture.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
            capture.CreationUtcTicks.ToString(CultureInfo.InvariantCulture));

    private static string VideoEditV2ManagedStamp(
        string jobId,
        long size,
        double mtimeMs)
        => string.Join(
            ':',
            "managed-video-job",
            jobId,
            size.ToString(CultureInfo.InvariantCulture),
            mtimeMs.ToString("R", CultureInfo.InvariantCulture));

    private bool TryCaptureDisplayedVideoEditV2Source(
        out VideoEditV2SourceChoice source)
    {
        source = null!;
        if (Modal.Visibility != Visibility.Visible
            || !_modalShowingVideo
            || ModalVideo.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (TryCaptureExternalVideoSourceSeam(
                out ExternalVideoSourceSeamSmokeSnapshot external))
        {
            string baseStamp = VideoEditV2BaseStamp(external);
            VideoEditV2ExternalProbe? probe = _videoEditV2ExternalProbe;
            bool exact = probe is not null
                && string.Equals(
                    probe.BaseSourceStamp,
                    baseStamp,
                    StringComparison.Ordinal)
                && VideoEditV2Planner.IsSupportedFps(
                    probe.FpsNumerator,
                    probe.FpsDenominator)
                && probe.FrameCount is > 0
                    and <= VideoEditV2Planner.MaximumSourceFrames;
            int fpsNumerator = exact ? probe!.FpsNumerator : 0;
            int fpsDenominator = exact ? probe!.FpsDenominator : 1;
            int frameCount = exact ? probe!.FrameCount : 0;
            string sourceStamp = exact
                ? $"{baseStamp}:probe:{probe!.ProbeRevision}:{fpsNumerator}:{frameCount}"
                : baseStamp;
            source = new VideoEditV2SourceChoice(
                sourceStamp,
                baseStamp,
                Path.GetFileName(external.CanonicalPath),
                Managed: false,
                ExactTimeline: exact,
                fpsNumerator,
                fpsDenominator,
                frameCount,
                Math.Max(0, _modalVideoDurationSeconds),
                Math.Max(0, ModalVideo.NaturalVideoWidth),
                Math.Max(0, ModalVideo.NaturalVideoHeight),
                SourceVideoJobId: null,
                external);
            return true;
        }

        if (!TryCapturePassiveDisplayedManagedVideoEditV2Source(
                out ManagedVideoVersion video))
        {
            return false;
        }

        string managedStamp = VideoEditV2ManagedStamp(
            video.JobId,
            video.Output.SourceSize,
            video.Output.SourceMtimeMs);
        bool exactManagedTimeline = VideoEditV2Planner.IsSupportedFps(
                video.PlaybackFps,
                1)
            && video.FrameCount is > 0
                and <= VideoEditV2Planner.MaximumSourceFrames
            && (long)video.FrameCount
                <= (long)VideoEditV2Planner.MaximumSourceSeconds
                    * video.PlaybackFps;
        source = new VideoEditV2SourceChoice(
            managedStamp,
            managedStamp,
            Path.GetFileName(video.Output.OutputPath),
            Managed: true,
            ExactTimeline: exactManagedTimeline,
            video.PlaybackFps,
            1,
            video.FrameCount,
            exactManagedTimeline
                ? (double)video.FrameCount / video.PlaybackFps
                : Math.Max(0, video.DurationSeconds),
            Math.Max(0, video.Width),
            Math.Max(0, video.Height),
            video.JobId,
            ExternalSeam: null);
        return true;
    }

    private bool TryCapturePassiveDisplayedManagedVideoEditV2Source(
        out ManagedVideoVersion video)
    {
        video = null!;
        if (Modal.Visibility != Visibility.Visible
            || !_modalShowingVideo
            || string.IsNullOrWhiteSpace(_modalSourceTilePath)
            || _modalVideoVersionIndex < 0
            || _modalVideoVersionIndex >= _modalVideoVersions.Count
            || !_modalNavigationSnapshot.Any(candidate => string.Equals(
                    candidate.Path,
                    _modalSourceTilePath,
                    StringComparison.OrdinalIgnoreCase))
                && !_tiles.Any(candidate => string.Equals(
                    candidate.Path,
                    _modalSourceTilePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ManagedVideoVersion hydrated =
            _modalVideoVersions[_modalVideoVersionIndex];
        if (string.IsNullOrWhiteSpace(hydrated.JobId)
            || string.IsNullOrWhiteSpace(hydrated.Output.OutputPath)
            || hydrated.Output.SourceSize <= 0
            || !double.IsFinite(hydrated.Output.SourceMtimeMs))
        {
            return false;
        }

        // This is a passive copy from the immutable, already-hydrated modal
        // Job snapshot. Do not canonicalize, open, measure, hash, probe, or
        // call TryValidateManagedVideoVersion from board open/hydration.
        video = hydrated;
        return true;
    }

    private void SyncModalVideoEditV2EntryPresentation()
    {
        if (ModalVideoEditV2Button is null
            || ModalContextVideoEditV2 is null)
        {
            return;
        }

        bool visible = Modal.Visibility == Visibility.Visible
            && _modalShowingVideo
            && ModalVideo.Visibility == Visibility.Visible;
        ModalVideoEditV2Button.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoEditV2Button.IsEnabled = visible;
        ModalContextVideoEditV2.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalContextVideoEditV2.IsEnabled = visible;

        if (!visible && ModalVideoEditV2BoardVisible)
            InvalidateModalVideoEditV2ForSourceChange();
    }

    private void ModalVideoEditV2Video_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (!_videoEditV2MediaHooked && ReferenceEquals(sender, ModalVideo))
        {
            _videoEditV2MediaHooked = true;
            ModalVideo.MediaOpened += ModalVideoEditV2Video_MediaOpened;
        }
        SyncModalVideoEditV2EntryPresentation();
    }

    private void ModalVideoEditV2Video_MediaOpened(
        object? sender,
        RoutedEventArgs e)
    {
        SyncModalVideoEditV2EntryPresentation();
        if (ModalVideoEditV2Popup?.Visibility != Visibility.Visible)
            return;

        if (!TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || _videoEditV2Source is null
            || !string.Equals(
                current.BaseSourceStamp,
                _videoEditV2Source.BaseSourceStamp,
                StringComparison.Ordinal))
        {
            MarkModalVideoEditV2SourceStale();
            return;
        }

        _videoEditV2Source = current;
        ApplyModalVideoEditV2SourceToControls(resetRange: false);
    }

    private void ModalVideoEditV2Video_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
        => SyncModalVideoEditV2EntryPresentation();

    private void ModalVideoEditV2Popup_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false
            && !_videoEditV2Closing
            && _videoEditV2Source is not null)
        {
            CloseModalVideoEditV2Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private void OpenModalVideoEditV2_Click(
        object sender,
        RoutedEventArgs e)
        => OpenModalVideoEditV2Board();

    private void OpenModalVideoEditV2Board()
    {
        SyncModalVideoEditV2EntryPresentation();
        if (ModalVideoEditV2Button.Visibility != Visibility.Visible
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice source))
        {
            SetTransientStatusToast(
                VideoEditV2Text("UiVideoEditV2SourceUnavailable"));
            return;
        }

        if (ModalVideoToolsPopup?.Visibility == Visibility.Visible)
            CloseVideoToolsBoard(restoreFocus: false);
        if (ModalVideoGenerationPopup?.Visibility == Visibility.Visible)
            CloseModalVideoGenerationBoard();

        _videoEditV2Source = source;
        _videoEditV2Plan = null;
        _videoEditV2Candidate = null;
        _videoEditV2CandidateStale = false;
        _videoEditV2LastCloseWasStale = false;
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2InstructionTextBox.Text = "";
            ModalVideoEditV2AudioComboBox.SelectedIndex = 0;
            ModalVideoEditV2StrengthComboBox.SelectedIndex = 1;
            ModalVideoEditV2CanvasComboBox.SelectedIndex = 2;
            ModalVideoEditV2StyleComboBox.SelectedIndex = 0;
            ModalVideoEditV2StepsSlider.Value = VideoEditV2DefaultSteps;
            ModalVideoEditV2StepsTextBox.Text =
                VideoEditV2DefaultSteps.ToString(CultureInfo.InvariantCulture);
            ModalVideoEditV2SkipReviewCheckBox.IsChecked = false;
            ModalVideoEditV2CompiledPromptTextBox.Text = "";
            ModalVideoEditV2SummaryText.Text = "";
            ModalVideoEditV2ReviewPanel.Visibility = Visibility.Collapsed;
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2CompileUnavailable");
        }
        finally
        {
            _videoEditV2Syncing = false;
        }

        ApplyModalVideoEditV2SourceToControls(resetRange: true);
        ModalVideoEditV2Popup.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModalVideoEditV2Popup.Visibility != Visibility.Visible)
                    return;
                if (_videoEditV2Source?.ExactTimeline == true)
                    Keyboard.Focus(ModalVideoEditV2StartFrameTextBox);
                else
                    Keyboard.Focus(ModalVideoEditV2ProbeButton);
            }));
    }

    private void ApplyModalVideoEditV2SourceToControls(bool resetRange)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source)
            return;

        bool exact = source.ExactTimeline;
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2SourceText.Text = source.Managed
                ? VideoEditV2Format(
                    "UiVideoEditV2ManagedSourceFormat",
                    source.DisplayName,
                    source.FpsNumerator,
                    source.FrameCount,
                    source.PlaybackWidth,
                    source.PlaybackHeight)
                : source.ExactTimeline
                    ? VideoEditV2Format(
                        "UiVideoEditV2ExternalExactSourceFormat",
                        source.DisplayName,
                        source.FpsNumerator,
                        source.FrameCount,
                        source.PlaybackWidth,
                        source.PlaybackHeight)
                : VideoEditV2Format(
                    "UiVideoEditV2ExternalSourceFormat",
                    source.DisplayName,
                    source.PlaybackDurationSeconds.ToString(
                        "0.###",
                        CultureInfo.CurrentCulture),
                    source.PlaybackWidth > 0
                        ? source.PlaybackWidth
                            .ToString(CultureInfo.CurrentCulture)
                        : "--",
                    source.PlaybackHeight > 0
                        ? source.PlaybackHeight
                            .ToString(CultureInfo.CurrentCulture)
                        : "--");

            int fpsIndex = Array.IndexOf(
                VideoEditV2Planner.SupportedSourceFps.ToArray(),
                source.FpsNumerator);
            ModalVideoEditV2FpsComboBox.SelectedIndex = exact
                ? fpsIndex
                : -1;
            ModalVideoEditV2FpsComboBox.IsEnabled = false;
            ModalVideoEditV2StartFrameTextBox.IsEnabled = exact;
            ModalVideoEditV2EndFrameTextBox.IsEnabled = exact;
            ModalVideoEditV2StartSlider.IsEnabled = exact;
            ModalVideoEditV2EndSlider.IsEnabled = exact;
            ModalVideoEditV2UseCurrentStartButton.IsEnabled = exact;
            ModalVideoEditV2UseCurrentEndButton.IsEnabled = exact;
            ModalVideoEditV2ProbeButton.Visibility = !source.Managed && !exact
                ? Visibility.Visible
                : Visibility.Collapsed;
            ModalVideoEditV2ProbeButton.IsEnabled = false;
            ModalVideoEditV2ProbeStatusText.Text = exact
                ? VideoEditV2Text(source.Managed
                    ? "UiVideoEditV2ManagedTimelineExact"
                    : "UiVideoEditV2ExternalTimelineExact")
                : VideoEditV2Text("UiVideoEditV2ExternalTimelineUnverified");

            if (exact)
            {
                int defaultEnd = Math.Min(
                    source.FrameCount,
                    VideoEditV2Planner.MaximumSelectionFrameCount(
                        source.FpsNumerator,
                        source.FpsDenominator));
                ModalVideoEditV2StartSlider.Maximum = Math.Max(
                    0,
                    source.FrameCount - 1);
                ModalVideoEditV2EndSlider.Maximum = source.FrameCount;
                if (resetRange
                    || !int.TryParse(
                        ModalVideoEditV2StartFrameTextBox.Text,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int existingStart)
                    || !int.TryParse(
                        ModalVideoEditV2EndFrameTextBox.Text,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int existingEnd)
                    || existingStart < 0
                    || existingEnd <= existingStart
                    || existingEnd > source.FrameCount)
                {
                    ModalVideoEditV2StartFrameTextBox.Text = "0";
                    ModalVideoEditV2EndFrameTextBox.Text =
                        defaultEnd.ToString(CultureInfo.InvariantCulture);
                    ModalVideoEditV2StartSlider.Value = 0;
                    ModalVideoEditV2EndSlider.Value = defaultEnd;
                }
            }
            else
            {
                ModalVideoEditV2StartFrameTextBox.Text = "";
                ModalVideoEditV2EndFrameTextBox.Text = "";
                ModalVideoEditV2StartSlider.Maximum = 1;
                ModalVideoEditV2EndSlider.Maximum = 1;
                ModalVideoEditV2StartSlider.Value = 0;
                ModalVideoEditV2EndSlider.Value = 1;
            }
        }
        finally
        {
            _videoEditV2Syncing = false;
        }

        RefreshModalVideoEditV2Plan(markCandidateStale: false);
    }

    private void CloseModalVideoEditV2_Click(
        object sender,
        RoutedEventArgs e)
        => CloseModalVideoEditV2Board(restoreFocus: true, stale: false);

    private void CloseModalVideoEditV2Board(
        bool restoreFocus,
        bool stale)
    {
        _videoEditV2LastCloseWasStale = stale;
        _videoEditV2Closing = true;
        try
        {
            if (ModalVideoEditV2Popup is not null)
                ModalVideoEditV2Popup.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _videoEditV2Closing = false;
        }
        _videoEditV2Source = null;
        _videoEditV2Plan = null;
        _videoEditV2Candidate = null;
        _videoEditV2CandidateStale = stale;
        if (restoreFocus
            && ModalVideoEditV2Button?.Visibility == Visibility.Visible)
        {
            ModalVideoEditV2Button.Focus();
        }
    }

    private bool ModalVideoEditV2BoardVisible
        => ModalVideoEditV2Popup?.Visibility == Visibility.Visible;

    private void InvalidateModalVideoEditV2ForSourceChange()
    {
        if (ModalVideoEditV2BoardVisible)
        {
            CloseModalVideoEditV2Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private void FocusModalVideoEditV2Board()
    {
        if (!ModalVideoEditV2BoardVisible
            || ModalVideoEditV2Popup.IsKeyboardFocusWithin)
        {
            return;
        }
        Keyboard.Focus(_videoEditV2Source?.ExactTimeline == true
            ? ModalVideoEditV2StartFrameTextBox
            : ModalVideoEditV2ProbeButton);
    }

    private void MarkModalVideoEditV2SourceStale()
    {
        if (ModalVideoEditV2Popup?.Visibility != Visibility.Visible)
            return;
        _videoEditV2Plan = null;
        _videoEditV2CandidateStale = true;
        ModalVideoEditV2RangeStatusText.Text =
            VideoEditV2Text("UiVideoEditV2SourceUnavailable");
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateStale");
        SetModalVideoEditV2ExactControlsEnabled(false);
    }

    private void SetModalVideoEditV2ExactControlsEnabled(bool enabled)
    {
        ModalVideoEditV2StartFrameTextBox.IsEnabled = enabled;
        ModalVideoEditV2EndFrameTextBox.IsEnabled = enabled;
        ModalVideoEditV2StartSlider.IsEnabled = enabled;
        ModalVideoEditV2EndSlider.IsEnabled = enabled;
        ModalVideoEditV2UseCurrentStartButton.IsEnabled = enabled;
        ModalVideoEditV2UseCurrentEndButton.IsEnabled = enabled;
        ModalVideoEditV2CompileButton.IsEnabled = false;
        ModalVideoEditV2TrimButton.IsEnabled = false;
        ModalVideoEditV2StartButton.IsEnabled = false;
    }

    private void ModalVideoEditV2Fps_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_videoEditV2Syncing)
            RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void ModalVideoEditV2RangeText_Changed(
        object sender,
        TextChangedEventArgs e)
    {
        if (_videoEditV2Syncing)
            return;
        RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void ModalVideoEditV2StartSlider_Changed(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoEditV2Syncing
            || _videoEditV2Source?.ExactTimeline != true)
        {
            return;
        }
        SetModalVideoEditV2RangeText(
            checked((int)Math.Round(e.NewValue)),
            endFrameExclusive: null);
    }

    private void ModalVideoEditV2EndSlider_Changed(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoEditV2Syncing
            || _videoEditV2Source?.ExactTimeline != true)
        {
            return;
        }
        SetModalVideoEditV2RangeText(
            startFrame: null,
            checked((int)Math.Round(e.NewValue)));
    }

    private void SetModalVideoEditV2RangeText(
        int? startFrame,
        int? endFrameExclusive)
    {
        _videoEditV2Syncing = true;
        try
        {
            if (startFrame.HasValue)
                ModalVideoEditV2StartFrameTextBox.Text = startFrame.Value
                    .ToString(CultureInfo.InvariantCulture);
            if (endFrameExclusive.HasValue)
                ModalVideoEditV2EndFrameTextBox.Text = endFrameExclusive.Value
                    .ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _videoEditV2Syncing = false;
        }
        RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void ModalVideoEditV2UseCurrentStart_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetModalVideoEditV2CurrentFrame(out int frame))
            return;
        SetModalVideoEditV2RangeText(frame, endFrameExclusive: null);
    }

    private void ModalVideoEditV2UseCurrentEnd_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryGetModalVideoEditV2CurrentFrame(out int frame)
            || _videoEditV2Source is not VideoEditV2SourceChoice source)
        {
            return;
        }
        SetModalVideoEditV2RangeText(
            startFrame: null,
            Math.Min(source.FrameCount, checked(frame + 1)));
    }

    private bool TryGetModalVideoEditV2CurrentFrame(out int frame)
    {
        frame = 0;
        if (_videoEditV2Source is not VideoEditV2SourceChoice source
            || !source.ExactTimeline)
        {
            return false;
        }
        double seconds = _modalVideoTransportStubForSmoke
            ? ModalVideoSeekSlider.Value
            : ModalVideo.Position.TotalSeconds;
        if (!double.IsFinite(seconds))
            return false;
        frame = Math.Clamp(
            checked((int)Math.Floor(
                seconds * source.FpsNumerator / source.FpsDenominator)),
            0,
            source.FrameCount - 1);
        return true;
    }

    private void ModalVideoEditV2Input_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoEditV2Syncing
            || ReferenceEquals(sender, ModalVideoEditV2SkipReviewCheckBox))
        {
            return;
        }
        RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void ModalVideoEditV2Steps_Changed(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoEditV2Syncing
            || ModalVideoEditV2StepsTextBox is null)
            return;
        int steps = checked((int)Math.Round(e.NewValue));
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2StepsTextBox.Text =
                steps.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _videoEditV2Syncing = false;
        }
        RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void ModalVideoEditV2StepsText_Changed(
        object sender,
        TextChangedEventArgs e)
    {
        if (_videoEditV2Syncing)
            return;
        if (int.TryParse(
                ModalVideoEditV2StepsTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int steps)
            && steps is >= VideoEditV2MinimumSteps
                and <= VideoEditV2MaximumSteps)
        {
            _videoEditV2Syncing = true;
            try
            {
                ModalVideoEditV2StepsSlider.Value = steps;
            }
            finally
            {
                _videoEditV2Syncing = false;
            }
        }
        RefreshModalVideoEditV2Plan(markCandidateStale: true);
    }

    private void RefreshModalVideoEditV2Plan(bool markCandidateStale)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source)
        {
            _videoEditV2Plan = null;
            return;
        }

        if (!source.ExactTimeline)
        {
            _videoEditV2Plan = null;
            SetModalVideoEditV2ExactControlsEnabled(false);
            ModalVideoEditV2RangeStatusText.Text =
                VideoEditV2Text("UiVideoEditV2ExternalTimelineUnverified");
            ShowModalVideoEditV2PlaybackOnlyPreviews(
                source.PlaybackDurationSeconds);
            if (markCandidateStale)
                MarkModalVideoEditV2CandidateStale();
            return;
        }

        int startFrame = 0;
        int endFrameExclusive = 0;
        bool parsed = int.TryParse(
                ModalVideoEditV2StartFrameTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out startFrame)
            && int.TryParse(
                ModalVideoEditV2EndFrameTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out endFrameExclusive);
        VideoEditV2PlanError error = VideoEditV2PlanError.InvalidRange;
        VideoEditV2SelectionPlan plan = null!;
        bool planned = parsed
            && VideoEditV2Planner.TryPlan(
                source.FrameCount,
                source.FpsNumerator,
                source.FpsDenominator,
                startFrame,
                endFrameExclusive,
                out plan,
                out error);
        if (!planned)
        {
            _videoEditV2Plan = null;
            ModalVideoEditV2RangeStatusText.Text = VideoEditV2Text(
                parsed
                    ? error switch
                    {
                        VideoEditV2PlanError.SelectionTooLong =>
                            "UiVideoEditV2RangeTooLong",
                        VideoEditV2PlanError.UnsupportedFps =>
                            "UiVideoEditV2UnsupportedFps",
                        VideoEditV2PlanError.SourceOutOfBounds =>
                            "UiVideoEditV2SourceOutOfBounds",
                        _ => "UiVideoEditV2RangeInvalid",
                    }
                    : "UiVideoEditV2RangeInvalid");
            SetModalVideoEditV2PreviewButtonsEnabled(false);
            ModalVideoEditV2CompileButton.IsEnabled = false;
            ModalVideoEditV2StartButton.IsEnabled = false;
            if (markCandidateStale)
                MarkModalVideoEditV2CandidateStale();
            return;
        }

        _videoEditV2Plan = plan;
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2StartSlider.Value = plan.StartFrame;
            ModalVideoEditV2EndSlider.Value = plan.EndFrameExclusive;
        }
        finally
        {
            _videoEditV2Syncing = false;
        }
        ModalVideoEditV2RangeStatusText.Text = VideoEditV2Format(
            "UiVideoEditV2RangeFormat",
            plan.StartFrame,
            plan.EndFrameExclusive,
            plan.SelectedFrameCount,
            plan.StartSeconds.ToString("0.000", CultureInfo.CurrentCulture),
            plan.EndSeconds.ToString("0.000", CultureInfo.CurrentCulture),
            plan.MaximumSelectionFrames);
        UpdateModalVideoEditV2PlanPreviews(plan);
        ModalVideoEditV2CompileButton.IsEnabled = false;
        ModalVideoEditV2TrimButton.IsEnabled = false;
        ModalVideoEditV2StartButton.IsEnabled = false;
        if (markCandidateStale)
            MarkModalVideoEditV2CandidateStale();
    }

    private void UpdateModalVideoEditV2PlanPreviews(
        VideoEditV2SelectionPlan plan)
    {
        SetModalVideoEditV2Preview(
            ModalVideoEditV2StartPreviewFrameText,
            ModalVideoEditV2StartPreviewTimeText,
            plan.StartPreviewFrame,
            plan.FpsNumerator,
            plan.FpsDenominator);
        SetModalVideoEditV2Preview(
            ModalVideoEditV2MiddlePreviewFrameText,
            ModalVideoEditV2MiddlePreviewTimeText,
            plan.MiddlePreviewFrame,
            plan.FpsNumerator,
            plan.FpsDenominator);
        SetModalVideoEditV2Preview(
            ModalVideoEditV2EndPreviewFrameText,
            ModalVideoEditV2EndPreviewTimeText,
            plan.EndPreviewFrame,
            plan.FpsNumerator,
            plan.FpsDenominator);
        SetModalVideoEditV2PreviewButtonsEnabled(true);
    }

    private static void SetModalVideoEditV2Preview(
        TextBlock frameText,
        TextBlock timeText,
        int frame,
        int fpsNumerator,
        int fpsDenominator)
    {
        frameText.Text = $"f {frame.ToString(CultureInfo.InvariantCulture)}";
        timeText.Text = $"{VideoEditV2Planner.FormatFrameTime(
            frame,
            fpsNumerator,
            fpsDenominator)} s";
    }

    private void ShowModalVideoEditV2PlaybackOnlyPreviews(
        double durationSeconds)
    {
        bool available = double.IsFinite(durationSeconds)
            && durationSeconds > 0;
        double end = available ? Math.Max(0, durationSeconds - 0.001) : 0;
        (TextBlock Frame, TextBlock Time, double Seconds)[] items =
        [
            (ModalVideoEditV2StartPreviewFrameText,
                ModalVideoEditV2StartPreviewTimeText, 0),
            (ModalVideoEditV2MiddlePreviewFrameText,
                ModalVideoEditV2MiddlePreviewTimeText,
                available ? durationSeconds / 2 : 0),
            (ModalVideoEditV2EndPreviewFrameText,
                ModalVideoEditV2EndPreviewTimeText, end),
        ];
        foreach ((TextBlock frame, TextBlock time, double seconds) in items)
        {
            frame.Text = "f --";
            time.Text = available
                ? $"~{seconds.ToString("0.000", CultureInfo.InvariantCulture)} s"
                : "-- s";
        }
        SetModalVideoEditV2PreviewButtonsEnabled(available);
    }

    private void SetModalVideoEditV2PreviewButtonsEnabled(bool enabled)
    {
        ModalVideoEditV2StartPreviewButton.IsEnabled = enabled;
        ModalVideoEditV2MiddlePreviewButton.IsEnabled = enabled;
        ModalVideoEditV2EndPreviewButton.IsEnabled = enabled;
    }

    private void ModalVideoEditV2PreviewSeek_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target })
            return;

        double seconds;
        if (_videoEditV2Plan is VideoEditV2SelectionPlan plan)
        {
            int frame = target switch
            {
                "start" => plan.StartPreviewFrame,
                "middle" => plan.MiddlePreviewFrame,
                "end" => plan.EndPreviewFrame,
                _ => -1,
            };
            if (frame < 0)
                return;
            seconds = (double)frame
                * plan.FpsDenominator
                / plan.FpsNumerator;
        }
        else if (_videoEditV2Source is VideoEditV2SourceChoice source
            && !source.ExactTimeline
            && source.PlaybackDurationSeconds > 0)
        {
            seconds = target switch
            {
                "start" => 0,
                "middle" => source.PlaybackDurationSeconds / 2,
                "end" => Math.Max(0, source.PlaybackDurationSeconds - 0.001),
                _ => double.NaN,
            };
        }
        else
        {
            return;
        }

        if (double.IsFinite(seconds))
            SeekModalVideoToSeconds(seconds);
    }

    private void ProbeModalVideoEditV2Frames_Click(
        object sender,
        RoutedEventArgs e)
    {
        ModalVideoEditV2ProbeStatusText.Text =
            VideoEditV2Text("UiVideoEditV2ProbePending");
        ModalVideoEditV2ReadinessText.Text =
            VideoEditV2Text("UiVideoEditV2WriterPending");
    }

    private void CompileModalVideoEditV2Prompt_Click(
        object sender,
        RoutedEventArgs e)
    {
        RefreshModalVideoEditV2Plan(markCandidateStale: false);
        if (_videoEditV2Source?.ExactTimeline != true
            || _videoEditV2Plan is null)
        {
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2ExternalTimelineUnverified");
            return;
        }
        if (string.IsNullOrWhiteSpace(
                ModalVideoEditV2InstructionTextBox.Text))
        {
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2InstructionRequired");
            return;
        }

        _videoEditV2Candidate = null;
        _videoEditV2CandidateStale = false;
        ModalVideoEditV2ReviewPanel.Visibility = Visibility.Collapsed;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CompileUnavailable");
        ModalVideoEditV2StartButton.IsEnabled = false;
    }

    private void MarkModalVideoEditV2CandidateStale()
    {
        if (_videoEditV2Candidate is null)
            return;
        string current = BuildModalVideoEditV2ContextStamp();
        if (string.Equals(
                current,
                _videoEditV2Candidate.ContextStamp,
                StringComparison.Ordinal))
        {
            return;
        }
        _videoEditV2CandidateStale = true;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateStale");
        ModalVideoEditV2StartButton.IsEnabled = false;
    }

    private string BuildModalVideoEditV2ContextStamp()
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source
            || _videoEditV2Plan is not VideoEditV2SelectionPlan plan)
        {
            return "invalid";
        }

        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        string audio = SelectedModalVideoEditV2Tag(
            ModalVideoEditV2AudioComboBox);
        string strength = SelectedModalVideoEditV2Tag(
            ModalVideoEditV2StrengthComboBox);
        string canvas = SelectedModalVideoEditV2Tag(
            ModalVideoEditV2CanvasComboBox);
        string style = SelectedModalVideoEditV2Tag(
            ModalVideoEditV2StyleComboBox);
        string steps = ModalVideoEditV2StepsTextBox.Text.Trim();
        var builder = new StringBuilder(512);
        builder.Append(source.SourceStamp).Append('\n')
            .Append(plan.StartFrame).Append(':')
            .Append(plan.EndFrameExclusive).Append(':')
            .Append(plan.FpsNumerator).Append('/')
            .Append(plan.FpsDenominator).Append('\n')
            .Append(instruction).Append('\n')
            .Append(audio).Append(':')
            .Append(strength).Append(':')
            .Append(canvas).Append(':')
            .Append(style).Append(':')
            .Append(steps);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string SelectedModalVideoEditV2Tag(ComboBox comboBox)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private static bool IsSafeModalVideoEditV2CompilerText(
        string value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsControl(current))
                return false;
            if (char.IsLowSurrogate(current))
                return false;
            if (!char.IsHighSurrogate(current))
                continue;
            if (index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }
            index++;
        }
        return true;
    }

    private static bool IsSafeModalVideoEditV2CompilerRevision(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 64
            || value[0] is < 'a' or > 'z')
        {
            return false;
        }
        return value.All(static character =>
            character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '_' or '-');
    }

    private static bool IsLowerHexModalVideoEditV2Digest(string value)
        => value.Length == 64
            && value.All(static character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f');

    private bool ApplyModalVideoEditV2CompiledCandidate(
        string backendPrompt,
        string summaryJa,
        string compilerRevision,
        string contextDigest)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source
            || !source.ExactTimeline
            || _videoEditV2Plan is null
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || !string.Equals(
                current.SourceStamp,
                source.SourceStamp,
                StringComparison.Ordinal)
            || !IsSafeModalVideoEditV2CompilerText(
                backendPrompt,
                8_000)
            || !IsSafeModalVideoEditV2CompilerText(
                summaryJa,
                2_000)
            || !IsSafeModalVideoEditV2CompilerRevision(compilerRevision)
            || !IsLowerHexModalVideoEditV2Digest(contextDigest))
        {
            return false;
        }

        string contextStamp = BuildModalVideoEditV2ContextStamp();
        if (string.Equals(contextStamp, "invalid", StringComparison.Ordinal))
            return false;
        _videoEditV2Candidate = new VideoEditV2CompiledCandidate(
            backendPrompt,
            summaryJa,
            compilerRevision,
            contextDigest,
            source.SourceStamp,
            contextStamp);
        _videoEditV2CandidateStale = false;
        ModalVideoEditV2CompiledPromptTextBox.Text =
            _videoEditV2Candidate.BackendPrompt;
        ModalVideoEditV2SummaryText.Text =
            _videoEditV2Candidate.SummaryJa;
        ModalVideoEditV2ReviewPanel.Visibility = Visibility.Visible;
        bool skipReview = ModalVideoEditV2SkipReviewCheckBox.IsChecked == true;
        ModalVideoEditV2CompileStatusText.Text = VideoEditV2Text(
            skipReview
                ? "UiVideoEditV2CandidateAutoSuppressed"
                : "UiVideoEditV2CandidateReady");
        ModalVideoEditV2StartButton.IsEnabled = false;
        return true;
    }

    private void StartModalVideoEditV2_Click(
        object sender,
        RoutedEventArgs e)
    {
        _videoEditV2StartAttemptCount++;
        ModalVideoEditV2ReadinessText.Text =
            VideoEditV2Text("UiVideoEditV2WriterPending");
        ModalVideoEditV2StartButton.IsEnabled = false;
    }

    private void ModalVideoEditV2_RightClick(
        object sender,
        MouseButtonEventArgs e)
    {
        SyncModalVideoEditV2EntryPresentation();
        if (ModalVideoEditV2Button.Visibility != Visibility.Visible
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || current.Managed
            || ModalContextMenu?.IsEnabled != false)
        {
            return;
        }

        ModalVideoEditV2ExternalContextMenu.PlacementTarget = ModalImageArea;
        ModalVideoEditV2ExternalContextMenu.Placement = PlacementMode.MousePoint;
        ModalVideoEditV2ExternalContextMenu.IsOpen = true;
        e.Handled = true;
    }

    internal static bool TryPlanVideoEditV2ForSmoke(
        int sourceFrameCount,
        int fps,
        int startFrame,
        int endFrameExclusive,
        out VideoEditV2SelectionPlan plan,
        out VideoEditV2PlanError error)
        => VideoEditV2Planner.TryPlan(
            sourceFrameCount,
            fps,
            1,
            startFrame,
            endFrameExclusive,
            out plan,
            out error);

    public bool VideoEditV2EntryVisibleForSmoke
    {
        get
        {
            SyncModalVideoEditV2EntryPresentation();
            return ModalVideoEditV2Button?.Visibility == Visibility.Visible
                && ModalContextVideoEditV2?.Visibility == Visibility.Visible;
        }
    }

    public bool OpenVideoEditV2ForSmoke()
    {
        OpenModalVideoEditV2Board();
        return ModalVideoEditV2Popup?.Visibility == Visibility.Visible;
    }

    public bool VideoEditV2ExactFrameControlsEnabledForSmoke
        => ModalVideoEditV2StartFrameTextBox?.IsEnabled == true
            && ModalVideoEditV2EndFrameTextBox?.IsEnabled == true
            && ModalVideoEditV2StartSlider?.IsEnabled == true
            && ModalVideoEditV2EndSlider?.IsEnabled == true;

    public bool VideoEditV2ProbeAffordanceVisibleForSmoke
        => ModalVideoEditV2ProbeButton?.Visibility == Visibility.Visible;

    public bool VideoEditV2StartDisabledForSmoke
        => ModalVideoEditV2StartButton?.IsEnabled == false;

    public bool VideoEditV2BoardVisibleForSmoke
        => ModalVideoEditV2BoardVisible;

    public bool VideoEditV2CompilerDisabledForSmoke
        => ModalVideoEditV2CompileButton?.IsEnabled == false;

    public bool VideoEditV2TrimDisabledForSmoke
        => ModalVideoEditV2TrimButton?.IsEnabled == false;

    public bool VideoEditV2ReviewVisibleForSmoke
        => ModalVideoEditV2ReviewPanel?.Visibility == Visibility.Visible;

    public bool VideoEditV2CandidateStaleForSmoke
        => _videoEditV2CandidateStale;

    public bool VideoEditV2LastCloseWasStaleForSmoke
        => _videoEditV2LastCloseWasStale;

    public int VideoEditV2StartAttemptCountForSmoke
        => _videoEditV2StartAttemptCount;

    public string VideoEditV2RangeStatusForSmoke
        => ModalVideoEditV2RangeStatusText?.Text ?? "";

    public string[] VideoEditV2PreviewFramesForSmoke =>
    [
        ModalVideoEditV2StartPreviewFrameText?.Text ?? "",
        ModalVideoEditV2MiddlePreviewFrameText?.Text ?? "",
        ModalVideoEditV2EndPreviewFrameText?.Text ?? "",
    ];

    public bool SetVideoEditV2ExternalProbeForSmoke(
        int fps,
        int frameCount)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice
            {
                Managed: false,
            } source
            || !VideoEditV2Planner.TryPlan(
                frameCount,
                fps,
                1,
                0,
                Math.Min(
                    frameCount,
                    VideoEditV2Planner.MaximumSelectionFrameCount(fps, 1)),
                out _,
                out _))
        {
            return false;
        }

        _videoEditV2ExternalProbe = new VideoEditV2ExternalProbe(
            source.BaseSourceStamp,
            fps,
            1,
            frameCount,
            "synthetic-probe-v1");
        _modalVideoDurationSeconds = (double)frameCount / fps;
        ResetModalVideoTimeline(_modalVideoDurationSeconds, show: true);
        if (!TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice exact))
        {
            return false;
        }
        _videoEditV2Source = exact;
        ApplyModalVideoEditV2SourceToControls(resetRange: true);
        return exact.ExactTimeline;
    }

    public bool SetVideoEditV2SelectionForSmoke(
        int startFrame,
        int endFrameExclusive)
    {
        if (_videoEditV2Source?.ExactTimeline != true)
            return false;
        SetModalVideoEditV2RangeText(startFrame, endFrameExclusive);
        return _videoEditV2Plan is not null;
    }

    public void SetVideoEditV2InstructionForSmoke(string instruction)
        => ModalVideoEditV2InstructionTextBox.Text = instruction;

    public void SetVideoEditV2SkipReviewForSmoke(bool skip)
        => ModalVideoEditV2SkipReviewCheckBox.IsChecked = skip;

    public bool ApplyVideoEditV2CompiledCandidateForSmoke(
        string backendPrompt,
        string summaryJa)
        => ApplyModalVideoEditV2CompiledCandidate(
            backendPrompt,
            summaryJa,
            "synthetic-compiler-v1",
            new string('a', 64));

    public bool ApplyVideoEditV2CompiledCandidateForSmoke(
        string backendPrompt,
        string summaryJa,
        string compilerRevision,
        string contextDigest)
        => ApplyModalVideoEditV2CompiledCandidate(
            backendPrompt,
            summaryJa,
            compilerRevision,
            contextDigest);

    public bool SeekVideoEditV2PreviewForSmoke(string target)
    {
        Button? button = target switch
        {
            "start" => ModalVideoEditV2StartPreviewButton,
            "middle" => ModalVideoEditV2MiddlePreviewButton,
            "end" => ModalVideoEditV2EndPreviewButton,
            _ => null,
        };
        if (button?.IsEnabled != true)
            return false;
        ModalVideoEditV2PreviewSeek_Click(
            button,
            new RoutedEventArgs(Button.ClickEvent, button));
        return true;
    }

    public double VideoEditV2PlaybackPositionForSmoke
        => ModalVideoSeekSlider?.Value ?? 0;

    public bool VideoEditV2ExternalContextEntryForSmoke
        => ModalVideoEditV2ExternalContextMenu?.Items
            .OfType<MenuItem>()
            .Any(item => string.Equals(
                AutomationProperties.GetName(item),
                VideoEditV2Text("UiVideoEditV2ActionAutomation"),
                StringComparison.Ordinal)) == true;

    public bool VideoEditV2DedicatedContextIsExternalOnlyForSmoke
        => TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            && !current.Managed
            && ModalContextMenu?.IsEnabled == false;

    public bool InvalidateVideoEditV2ForSourceNavigationForSmoke()
    {
        InvalidateModalVideoEditV2ForSourceChange();
        return !ModalVideoEditV2BoardVisible
            && _videoEditV2LastCloseWasStale;
    }

    public void CloseVideoEditV2ForSmoke(bool stale = false)
        => CloseModalVideoEditV2Board(
            restoreFocus: false,
            stale);
}

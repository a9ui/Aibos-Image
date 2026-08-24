using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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
    private VideoEditV2PreviewSet? _videoEditV2PreviewSet;
    private CancellationTokenSource? _videoEditV2LoadCts;
    private CancellationTokenSource? _videoEditV2CompileCts;
    private long _videoEditV2LoadGeneration;
    private long _videoEditV2CompileGeneration;
    private bool _videoEditV2LoadPending;
    private bool _videoEditV2CompilePending;
    private bool _videoEditV2PreviewStale;
    private bool _videoEditV2CandidateStale;
    private bool _videoEditV2CandidateApproved;
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
        int DurationMs,
        int Width,
        int Height,
        string ProbeRevision);

    private sealed record VideoEditV2ExplicitSourceCapture(
        VideoEditV2SourceChoice Source,
        VideoEditV2SourceSelector Selector);

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
                exact
                    ? probe!.DurationMs / 1000d
                    : Math.Max(0, _modalVideoDurationSeconds),
                exact
                    ? probe!.Width
                    : Math.Max(0, ModalVideo.NaturalVideoWidth),
                exact
                    ? probe!.Height
                    : Math.Max(0, ModalVideo.NaturalVideoHeight),
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
        SyncModalVideoTrimV1EntryPresentation();

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
        if (ModalVideoTrimV1BoardVisible)
            CloseModalVideoTrimV1Board(restoreFocus: false, stale: false);
        if (ModalVideoGenerationPopup?.Visibility == Visibility.Visible)
            CloseModalVideoGenerationBoard();

        CancelModalVideoEditV2TransientActions();
        ResetModalVideoEditV2DurableState();
        _videoEditV2Source = source;
        _videoEditV2Plan = null;
        _videoEditV2Candidate = null;
        _videoEditV2PreviewSet = null;
        _videoEditV2PreviewStale = false;
        _videoEditV2CandidateStale = false;
        _videoEditV2CandidateApproved = false;
        _videoEditV2LastCloseWasStale = false;
        if (!_videoEditV2SessionInputsInitialized)
        {
            ModalVideoEditV2InstructionTextBox.Text = "";
            ModalVideoEditV2StyleNameTextBox.Text = "";
            ApplyVideoEditV2DefaultsToBoardIfNeeded();
        }
        _videoEditV2Syncing = true;
        try
        {
            ModalVideoEditV2CompiledPromptTextBox.Text = "";
            ModalVideoEditV2SummaryText.Text = "";
            ClearModalVideoEditV2PreviewImages();
            ModalVideoEditV2ReviewPanel.Visibility = Visibility.Collapsed;
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2CompileIdle");
            ModalVideoEditV2StyleStatusText.Text = "";
        }
        finally
        {
            _videoEditV2Syncing = false;
        }

        ApplyModalVideoEditV2SourceToControls(resetRange: true);
        ModalVideoEditV2Popup.Visibility = Visibility.Visible;
        ModalVideoEditV2TrimButton.IsEnabled = true;
        RefreshModalVideoEditV2ActionControls();
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModalVideoEditV2Popup.Visibility != Visibility.Visible)
                    return;
                Keyboard.Focus(ModalVideoEditV2ProbeButton);
            }));
    }

    private void ApplyModalVideoEditV2SourceToControls(bool resetRange)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source)
            return;

        bool exact = source.ExactTimeline;
        bool loaded = HasCurrentModalVideoEditV2PreviewSet(source);
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
            ModalVideoEditV2StartFrameTextBox.IsEnabled = loaded;
            ModalVideoEditV2EndFrameTextBox.IsEnabled = loaded;
            ModalVideoEditV2StartSlider.IsEnabled = loaded;
            ModalVideoEditV2EndSlider.IsEnabled = loaded;
            ModalVideoEditV2UseCurrentStartButton.IsEnabled = loaded;
            ModalVideoEditV2UseCurrentEndButton.IsEnabled = loaded;
            ModalVideoEditV2ProbeButton.Visibility = Visibility.Visible;
            ModalVideoEditV2ProbeButton.IsEnabled = !_videoEditV2CompilePending;
            ModalVideoEditV2ProbeStatusText.Text = loaded
                ? VideoEditV2Text("UiVideoEditV2PreviewReady")
                : exact && source.Managed
                    ? VideoEditV2Text("UiVideoEditV2ManagedFramesRequired")
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
                VideoEditV2SelectionPlan? loadedSelection = loaded
                    ? _videoEditV2PreviewSet!.Selection
                    : null;
                if (loadedSelection is not null)
                {
                    ModalVideoEditV2StartFrameTextBox.Text =
                        loadedSelection.StartFrame.ToString(
                            CultureInfo.InvariantCulture);
                    ModalVideoEditV2EndFrameTextBox.Text =
                        loadedSelection.EndFrameExclusive.ToString(
                            CultureInfo.InvariantCulture);
                    ModalVideoEditV2StartSlider.Value =
                        loadedSelection.StartFrame;
                    ModalVideoEditV2EndSlider.Value =
                        loadedSelection.EndFrameExclusive;
                }
                else if (resetRange
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
        RefreshModalVideoEditV2ActionControls();
    }

    private void CloseModalVideoEditV2_Click(
        object sender,
        RoutedEventArgs e)
        => CloseModalVideoEditV2Board(restoreFocus: true, stale: false);

    private void CloseModalVideoEditV2Board(
        bool restoreFocus,
        bool stale)
    {
        CancelModalVideoEditV2TransientActions();
        CancelModalVideoEditV2DurableActions();
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
        _videoEditV2PreviewSet = null;
        _videoEditV2ExternalProbe = null;
        _videoEditV2PreviewStale = stale;
        _videoEditV2CandidateStale = stale;
        _videoEditV2CandidateApproved = false;
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
        Keyboard.Focus(HasCurrentModalVideoEditV2PreviewSet(
                _videoEditV2Source)
            ? ModalVideoEditV2StartFrameTextBox
            : ModalVideoEditV2ProbeButton);
    }

    private void MarkModalVideoEditV2SourceStale()
    {
        if (ModalVideoEditV2Popup?.Visibility != Visibility.Visible)
            return;
        CancelModalVideoEditV2TransientActions();
        CancelModalVideoEditV2DurableActions();
        _videoEditV2Plan = null;
        _videoEditV2PreviewSet = null;
        _videoEditV2PreviewStale = true;
        _videoEditV2CandidateStale = true;
        ModalVideoEditV2RangeStatusText.Text =
            VideoEditV2Text("UiVideoEditV2SourceUnavailable");
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateStale");
        SetModalVideoEditV2ExactControlsEnabled(false);
    }

    private void CancelModalVideoEditV2TransientActions()
    {
        _videoEditV2LoadGeneration++;
        _videoEditV2CompileGeneration++;
        _videoEditV2LoadPending = false;
        _videoEditV2CompilePending = false;
        CancellationTokenSource? load = Interlocked.Exchange(
            ref _videoEditV2LoadCts,
            null);
        CancellationTokenSource? compile = Interlocked.Exchange(
            ref _videoEditV2CompileCts,
            null);
        TryCancelModalVideoEditV2Token(load);
        TryCancelModalVideoEditV2Token(compile);
    }

    private static void TryCancelModalVideoEditV2Token(
        CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
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
        ModalVideoEditV2TrimButton.IsEnabled = ModalVideoEditV2BoardVisible;
        ModalVideoEditV2StartButton.IsEnabled =
            CanStartModalVideoEditV2Durably();
        ModalVideoEditV2StartButton.Content = VideoEditV2Text(
            _videoEditV2RequestPending
                ? "UiVideoEditV2Preparing"
                : _videoEditV2WriterReady
                    ? "UiVideoEditV2StartReady"
                    : "UiVideoEditV2StartPending");
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
            RefreshModalVideoEditV2ActionControls();
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
            MarkModalVideoEditV2PreviewStaleIfNeeded();
            if (markCandidateStale)
                MarkModalVideoEditV2CandidateStale();
            RefreshModalVideoEditV2ActionControls();
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
        ModalVideoEditV2TrimButton.IsEnabled = ModalVideoEditV2BoardVisible;
        ModalVideoEditV2StartButton.IsEnabled = false;
        MarkModalVideoEditV2PreviewStaleIfNeeded();
        if (markCandidateStale)
            MarkModalVideoEditV2CandidateStale();
        RefreshModalVideoEditV2ActionControls();
    }

    private bool HasCurrentModalVideoEditV2PreviewSet(
        VideoEditV2SourceChoice? source = null)
    {
        source ??= _videoEditV2Source;
        return source is not null
            && _videoEditV2PreviewSet is VideoEditV2PreviewSet previews
            && !_videoEditV2PreviewStale
            && string.Equals(
                previews.SourceStamp,
                source.SourceStamp,
                StringComparison.Ordinal);
    }

    private bool HasFreshModalVideoEditV2PreviewsForPlan()
        => HasCurrentModalVideoEditV2PreviewSet()
            && _videoEditV2Plan is VideoEditV2SelectionPlan plan
            && _videoEditV2PreviewSet!.Selection.StartFrame
                == plan.StartFrame
            && _videoEditV2PreviewSet.Selection.EndFrameExclusive
                == plan.EndFrameExclusive;

    private void MarkModalVideoEditV2PreviewStaleIfNeeded()
    {
        if (_videoEditV2PreviewSet is null
            || HasFreshModalVideoEditV2PreviewsForPlan())
        {
            return;
        }
        _videoEditV2PreviewStale = true;
        ModalVideoEditV2ProbeStatusText.Text =
            VideoEditV2Text("UiVideoEditV2PreviewStale");
        SetModalVideoEditV2PreviewImageOpacity(0.38);
    }

    private void RefreshModalVideoEditV2ActionControls()
    {
        if (ModalVideoEditV2ProbeButton is null
            || ModalVideoEditV2CompileButton is null)
        {
            return;
        }

        bool boardVisible = ModalVideoEditV2BoardVisible;
        ModalVideoEditV2ProbeButton.IsEnabled = boardVisible
            && (_videoEditV2LoadPending || !_videoEditV2CompilePending);
        ModalVideoEditV2ProbeButton.Content = VideoEditV2Text(
            _videoEditV2LoadPending
                ? "UiVideoEditV2CancelButton"
                : HasCurrentModalVideoEditV2PreviewSet()
                    ? "UiVideoEditV2ReloadAction"
                    : "UiVideoEditV2ProbeAction");
        ModalVideoEditV2CompileButton.IsEnabled = boardVisible
            && (_videoEditV2CompilePending
                || !_videoEditV2LoadPending
                    && HasFreshModalVideoEditV2PreviewsForPlan()
                    && VideoEditV2TransientContract.IsSafeInstruction(
                        ModalVideoEditV2InstructionTextBox.Text.Trim()));
        ModalVideoEditV2CompileButton.Content = VideoEditV2Text(
            _videoEditV2CompilePending
                ? "UiVideoEditV2CancelButton"
                : "UiVideoEditV2CompileButton");
        ModalVideoEditV2SkipReviewCheckBox.IsEnabled =
            !_videoEditV2LoadPending
            && !_videoEditV2CompilePending;
        ModalVideoEditV2ApplyButton.IsEnabled =
            _videoEditV2Candidate is not null
            && !_videoEditV2CandidateStale
            && !_videoEditV2CandidateApproved
            && !_videoEditV2CompilePending;
        ModalVideoEditV2StartButton.IsEnabled = false;
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

    private void ApplyModalVideoEditV2PreviewImages(
        IReadOnlyList<VideoEditV2PreviewPayload> previews)
    {
        if (previews.Count != 3)
            return;
        ModalVideoEditV2StartPreviewImage.Source = previews[0].Image;
        ModalVideoEditV2MiddlePreviewImage.Source = previews[1].Image;
        ModalVideoEditV2EndPreviewImage.Source = previews[2].Image;
        SetModalVideoEditV2PreviewImageOpacity(1);
    }

    private void ClearModalVideoEditV2PreviewImages()
    {
        ModalVideoEditV2StartPreviewImage.Source = null;
        ModalVideoEditV2MiddlePreviewImage.Source = null;
        ModalVideoEditV2EndPreviewImage.Source = null;
        SetModalVideoEditV2PreviewImageOpacity(1);
    }

    private void SetModalVideoEditV2PreviewImageOpacity(double opacity)
    {
        ModalVideoEditV2StartPreviewImage.Opacity = opacity;
        ModalVideoEditV2MiddlePreviewImage.Opacity = opacity;
        ModalVideoEditV2EndPreviewImage.Opacity = opacity;
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

    private async void ProbeModalVideoEditV2Frames_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoEditV2LoadPending)
        {
            _videoEditV2LoadGeneration++;
            CancellationTokenSource? active = Interlocked.Exchange(
                ref _videoEditV2LoadCts,
                null);
            TryCancelModalVideoEditV2Token(active);
            _videoEditV2LoadPending = false;
            ModalVideoEditV2ProbeStatusText.Text =
                VideoEditV2Text("UiVideoEditV2ActionCanceled");
            RefreshModalVideoEditV2ActionControls();
            return;
        }
        await LoadModalVideoEditV2FramesAsync();
    }

    private async Task<bool> LoadModalVideoEditV2FramesAsync()
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice initialSource
            || !ModalVideoEditV2BoardVisible
            || _videoEditV2CompilePending)
        {
            return false;
        }

        long generation = ++_videoEditV2LoadGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoEditV2LoadCts,
            cts);
        prior?.Cancel();
        prior?.Dispose();
        _videoEditV2LoadPending = true;
        _videoEditV2PreviewStale = _videoEditV2PreviewSet is not null;
        _videoEditV2CandidateApproved = false;
        ModalVideoEditV2ProbeStatusText.Text =
            VideoEditV2Text("UiVideoEditV2ProbeWorking");
        RefreshModalVideoEditV2ActionControls();

        try
        {
            VideoEditV2ExplicitSourceCapture? capture =
                await CaptureModalVideoEditV2ExplicitSourceAsync(
                    initialSource,
                    cts.Token);
            if (capture is null
                || !IsModalVideoEditV2LoadCurrent(
                    generation,
                    initialSource.BaseSourceStamp))
            {
                SetModalVideoEditV2ProbeStatusIfCurrent(
                    generation,
                    VideoEditV2Text(
                        string.Equals(
                            Path.GetExtension(
                                initialSource.ExternalSeam?.CanonicalPath),
                            ".mp4",
                            StringComparison.OrdinalIgnoreCase)
                            || initialSource.Managed
                                ? "UiVideoEditV2SourceUnavailable"
                                : "UiVideoEditV2ProbeUnsupportedContainer"));
                return false;
            }

            string probeJson = VideoEditV2TransientContract
                .BuildProbeRequestJson(capture.Selector);
            EnhancementApiResponse probeResponse =
                await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    VideoEditV2TransientContract.Route,
                    token: cts.Token,
                    exactBodyJson: probeJson,
                    maxResponseBytes: VideoEditV2TransientContract
                        .MaximumActionResponseBytes,
                    timeoutError: VideoEditV2Text(
                        "UiVideoEditV2ActionTimedOut"));
            if (!IsModalVideoEditV2LoadCurrent(
                    generation,
                    initialSource.BaseSourceStamp))
            {
                return false;
            }
            if (!probeResponse.Ok
                || probeResponse.Payload is not JsonElement probePayload
                || !VideoEditV2TransientContract.TryParseProbeResponse(
                    probePayload,
                    out VideoEditV2SourceSummary summary)
                || capture.Source.Managed
                    && !ModalVideoEditV2ManagedSummaryMatches(
                        capture.Source,
                        summary)
                || !TryBuildModalVideoEditV2PreviewPlan(
                    summary,
                    out VideoEditV2SelectionPlan previewPlan))
            {
                SetModalVideoEditV2ProbeStatusIfCurrent(
                    generation,
                    VideoEditV2Text("UiVideoEditV2ProbeFailed"));
                return false;
            }

            string previewJson = VideoEditV2TransientContract
                .BuildPreviewRequestJson(capture.Selector, previewPlan);
            EnhancementApiResponse previewResponse =
                await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    VideoEditV2TransientContract.Route,
                    token: cts.Token,
                    exactBodyJson: previewJson,
                    maxResponseBytes: VideoEditV2TransientContract
                        .MaximumPreviewResponseBytes,
                    timeoutError: VideoEditV2Text(
                        "UiVideoEditV2ActionTimedOut"));
            if (!IsModalVideoEditV2LoadCurrent(
                    generation,
                    initialSource.BaseSourceStamp)
                || !previewResponse.Ok
                || previewResponse.Payload is not JsonElement previewPayload
                || !VideoEditV2TransientContract.TryParsePreviewResponse(
                    previewPayload,
                    summary,
                    previewPlan,
                    out VideoEditV2PreviewSet? previewSet,
                    capture.Selector,
                    initialSource.BaseSourceStamp)
                || previewSet is null
                || capture.Source.ExternalSeam is not null
                    && !TryRevalidateExternalVideoSourceSeam(
                        capture.Source.ExternalSeam))
            {
                SetModalVideoEditV2ProbeStatusIfCurrent(
                    generation,
                    VideoEditV2Text("UiVideoEditV2ProbeFailed"));
                return false;
            }

            if (!capture.Source.Managed)
            {
                _videoEditV2ExternalProbe = new(
                    initialSource.BaseSourceStamp,
                    summary.FpsNumerator,
                    summary.FpsDenominator,
                    summary.FrameCount,
                    summary.DurationMs,
                    summary.Width,
                    summary.Height,
                    capture.Selector.Sha256!);
            }
            if (!TryCaptureDisplayedVideoEditV2Source(
                    out VideoEditV2SourceChoice exactSource)
                || !string.Equals(
                    exactSource.BaseSourceStamp,
                    initialSource.BaseSourceStamp,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _videoEditV2Source = exactSource;
            _modalVideoDurationSeconds = summary.DurationMs / 1000d;
            ResetModalVideoTimeline(
                _modalVideoDurationSeconds,
                show: true);
            _videoEditV2PreviewSet = previewSet with
            {
                SourceStamp = exactSource.SourceStamp,
            };
            _videoEditV2PreviewStale = false;
            _videoEditV2Candidate = null;
            _videoEditV2CandidateStale = false;
            _videoEditV2CandidateApproved = false;
            _videoEditV2Plan = previewPlan;
            ApplyModalVideoEditV2SourceToControls(resetRange: true);
            ApplyModalVideoEditV2PreviewImages(previewSet.Previews);
            ModalVideoEditV2ProbeStatusText.Text =
                VideoEditV2Text("UiVideoEditV2PreviewReady");
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2CompileIdle");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetModalVideoEditV2ProbeStatusIfCurrent(
                generation,
                VideoEditV2Text("UiVideoEditV2ActionCanceled"));
            return false;
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidDataException
                or ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            SetModalVideoEditV2ProbeStatusIfCurrent(
                generation,
                VideoEditV2Text("UiVideoEditV2ProbeFailed"));
            return false;
        }
        finally
        {
            if (generation == _videoEditV2LoadGeneration)
            {
                _videoEditV2LoadPending = false;
                if (ReferenceEquals(_videoEditV2LoadCts, cts))
                    _videoEditV2LoadCts = null;
                RefreshModalVideoEditV2ActionControls();
            }
            cts.Dispose();
        }
    }

    private async Task<VideoEditV2ExplicitSourceCapture?>
        CaptureModalVideoEditV2ExplicitSourceAsync(
            VideoEditV2SourceChoice expected,
            CancellationToken token)
    {
        if (expected.Managed)
        {
            return expected.SourceVideoJobId is string jobId
                && VideoEditV2TransientContract.TryCreateManagedSelector(
                    jobId,
                    out VideoEditV2SourceSelector managed)
                && TryCaptureDisplayedVideoEditV2Source(
                    out VideoEditV2SourceChoice current)
                && current.Managed
                && string.Equals(
                    current.BaseSourceStamp,
                    expected.BaseSourceStamp,
                    StringComparison.Ordinal)
                    ? new(current, managed)
                    : null;
        }

        if (expected.ExternalSeam is not ExternalVideoSourceSeamSmokeSnapshot seam
            || !string.Equals(
                Path.GetExtension(seam.CanonicalPath),
                ".mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        ExternalVideoSourceIdentityCapture? captured =
            await CaptureExternalVideoSourceIdentityForEditV2Async(
                seam,
                token);
        if (captured is null
            || !VideoEditV2TransientContract.TryCreateDisplayedFileSelector(
                captured.CanonicalPath,
                captured.Size,
                captured.MtimeMs,
                captured.Sha256,
                out VideoEditV2SourceSelector displayed)
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice currentExternal)
            || currentExternal.Managed
            || !string.Equals(
                currentExternal.BaseSourceStamp,
                expected.BaseSourceStamp,
                StringComparison.Ordinal))
        {
            return null;
        }
        return new(currentExternal, displayed);
    }

    private bool IsModalVideoEditV2LoadCurrent(
        long generation,
        string expectedBaseSourceStamp)
        => generation == _videoEditV2LoadGeneration
            && _videoEditV2LoadPending
            && ModalVideoEditV2BoardVisible
            && TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            && string.Equals(
                current.BaseSourceStamp,
                expectedBaseSourceStamp,
                StringComparison.Ordinal);

    private void SetModalVideoEditV2ProbeStatusIfCurrent(
        long generation,
        string status)
    {
        if (generation == _videoEditV2LoadGeneration
            && ModalVideoEditV2BoardVisible)
        {
            ModalVideoEditV2ProbeStatusText.Text = status;
        }
    }

    private static bool ModalVideoEditV2ManagedSummaryMatches(
        VideoEditV2SourceChoice source,
        VideoEditV2SourceSummary summary)
        => source.Managed
            && source.FpsNumerator == summary.FpsNumerator
            && source.FpsDenominator == summary.FpsDenominator
            && source.FrameCount == summary.FrameCount
            && source.PlaybackWidth == summary.Width
            && source.PlaybackHeight == summary.Height;

    private bool TryBuildModalVideoEditV2PreviewPlan(
        VideoEditV2SourceSummary summary,
        out VideoEditV2SelectionPlan plan)
    {
        if (int.TryParse(
                ModalVideoEditV2StartFrameTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int start)
            && int.TryParse(
                ModalVideoEditV2EndFrameTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int end)
            && VideoEditV2Planner.TryPlan(
                summary.FrameCount,
                summary.FpsNumerator,
                summary.FpsDenominator,
                start,
                end,
                out plan,
                out _))
        {
            return true;
        }
        int defaultEnd = Math.Min(
            summary.FrameCount,
            VideoEditV2Planner.MaximumSelectionFrameCount(
                summary.FpsNumerator,
                summary.FpsDenominator));
        return VideoEditV2Planner.TryPlan(
            summary.FrameCount,
            summary.FpsNumerator,
            summary.FpsDenominator,
            0,
            defaultEnd,
            out plan,
            out _);
    }

    private async void CompileModalVideoEditV2Prompt_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoEditV2CompilePending)
        {
            _videoEditV2CompileGeneration++;
            CancellationTokenSource? active = Interlocked.Exchange(
                ref _videoEditV2CompileCts,
                null);
            TryCancelModalVideoEditV2Token(active);
            _videoEditV2CompilePending = false;
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2ActionCanceled");
            RefreshModalVideoEditV2ActionControls();
            return;
        }

        bool skipReviewAuthorization =
            ModalVideoEditV2SkipReviewCheckBox.IsChecked == true;
        ModalVideoEditV2SkipReviewCheckBox.IsChecked = false;
        RefreshModalVideoEditV2Plan(markCandidateStale: false);
        if (_videoEditV2Source?.ExactTimeline != true
            || _videoEditV2Plan is null
            || !HasFreshModalVideoEditV2PreviewsForPlan())
        {
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2PreviewRequired");
            return;
        }
        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        if (!VideoEditV2TransientContract.IsSafeInstruction(instruction))
        {
            ModalVideoEditV2CompileStatusText.Text =
                VideoEditV2Text("UiVideoEditV2InstructionRequired");
            return;
        }

        await CompileModalVideoEditV2PromptAsync(
            instruction,
            skipReviewAuthorization);
    }

    private async Task<bool> CompileModalVideoEditV2PromptAsync(
        string instruction,
        bool skipReviewAuthorization)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice initialSource
            || _videoEditV2Plan is not VideoEditV2SelectionPlan selection
            || _videoEditV2PreviewSet is not VideoEditV2PreviewSet previews
            || !HasFreshModalVideoEditV2PreviewsForPlan())
        {
            return false;
        }

        _videoEditV2Candidate = null;
        _videoEditV2CandidateStale = false;
        _videoEditV2CandidateApproved = false;
        ModalVideoEditV2ReviewPanel.Visibility = Visibility.Collapsed;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CompileWorking");
        ModalVideoEditV2StartButton.IsEnabled = false;

        string contextStamp = BuildModalVideoEditV2ContextStamp();
        long generation = ++_videoEditV2CompileGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoEditV2CompileCts,
            cts);
        prior?.Cancel();
        prior?.Dispose();
        _videoEditV2CompilePending = true;
        RefreshModalVideoEditV2ActionControls();

        try
        {
            VideoEditV2ExplicitSourceCapture? capture =
                await CaptureModalVideoEditV2ExplicitSourceAsync(
                    initialSource,
                    cts.Token);
            if (capture is null
                || !VideoEditV2TransientContract.SameSource(
                    capture.Selector,
                    previews.Source)
                || !IsModalVideoEditV2CompileCurrent(
                    generation,
                    contextStamp,
                    previews))
            {
                SetModalVideoEditV2CompileStatusIfCurrent(
                    generation,
                    VideoEditV2Text("UiVideoEditV2SourceUnavailable"));
                return false;
            }

            string requestJson = VideoEditV2TransientContract
                .BuildCompileRequestJson(
                    capture.Selector,
                    selection,
                    previews.Previews,
                    instruction);
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                VideoEditV2TransientContract.Route,
                token: cts.Token,
                exactBodyJson: requestJson,
                maxResponseBytes: VideoEditV2TransientContract
                    .MaximumActionResponseBytes,
                timeoutError: VideoEditV2Text(
                    "UiVideoEditV2ActionTimedOut"));
            if (!IsModalVideoEditV2CompileCurrent(
                    generation,
                    contextStamp,
                    previews))
            {
                return false;
            }
            if (!response.Ok
                || response.Payload is not JsonElement payload
                || !VideoEditV2TransientContract.TryParseCompileResponse(
                    payload,
                    capture.Selector,
                    selection,
                    previews.Previews,
                    instruction,
                    initialSource.SourceStamp,
                    contextStamp,
                    out VideoEditV2CompiledCandidate candidate))
            {
                SetModalVideoEditV2CompileStatusIfCurrent(
                    generation,
                    VideoEditV2Text("UiVideoEditV2CompileFailed"));
                return false;
            }

            if (!ApplyModalVideoEditV2CompiledCandidate(
                    candidate,
                    skipReviewAuthorization))
            {
                return false;
            }
            if (!skipReviewAuthorization)
                _ = ShowVideoEditCompileReviewNotification();
            bool automaticStartScheduled = skipReviewAuthorization
                && await HandleModalVideoEditV2SkipReviewAsync(candidate);
            SetTransientStatusToast(VideoEditV2Text(
                automaticStartScheduled
                    ? "UiVideoEditV2Preparing"
                    : skipReviewAuthorization
                    ? "UiVideoEditV2SkipNotStarted"
                    : "UiVideoEditV2CandidateReady"));
            return true;
        }
        catch (OperationCanceledException)
        {
            SetModalVideoEditV2CompileStatusIfCurrent(
                generation,
                VideoEditV2Text("UiVideoEditV2ActionCanceled"));
            return false;
        }
        catch (Exception ex) when (
            ex is IOException
                or InvalidDataException
                or ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            SetModalVideoEditV2CompileStatusIfCurrent(
                generation,
                VideoEditV2Text("UiVideoEditV2CompileFailed"));
            return false;
        }
        finally
        {
            if (generation == _videoEditV2CompileGeneration)
            {
                _videoEditV2CompilePending = false;
                if (ReferenceEquals(_videoEditV2CompileCts, cts))
                    _videoEditV2CompileCts = null;
                RefreshModalVideoEditV2ActionControls();
            }
            cts.Dispose();
        }
    }

    private bool IsModalVideoEditV2CompileCurrent(
        long generation,
        string expectedContextStamp,
        VideoEditV2PreviewSet expectedPreviews)
        => generation == _videoEditV2CompileGeneration
            && _videoEditV2CompilePending
            && ModalVideoEditV2BoardVisible
            && ReferenceEquals(_videoEditV2PreviewSet, expectedPreviews)
            && HasFreshModalVideoEditV2PreviewsForPlan()
            && string.Equals(
                BuildModalVideoEditV2ContextStamp(),
                expectedContextStamp,
                StringComparison.Ordinal);

    private void SetModalVideoEditV2CompileStatusIfCurrent(
        long generation,
        string status)
    {
        if (generation == _videoEditV2CompileGeneration
            && ModalVideoEditV2BoardVisible)
        {
            ModalVideoEditV2CompileStatusText.Text = status;
        }
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
        _videoEditV2CandidateApproved = false;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateStale");
        ModalVideoEditV2StartButton.IsEnabled = false;
        RefreshModalVideoEditV2ActionControls();
    }

    private string BuildModalVideoEditV2ContextStamp()
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice
            || _videoEditV2Plan is not VideoEditV2SelectionPlan plan
            || _videoEditV2PreviewSet is not VideoEditV2PreviewSet previews
            || !HasFreshModalVideoEditV2PreviewsForPlan())
        {
            return "invalid";
        }

        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        if (!VideoEditV2TransientContract.IsSafeInstruction(instruction))
            return "invalid";
        return VideoEditV2TransientContract.BuildCompileRequestJson(
            previews.Source,
            plan,
            previews.Previews,
            instruction);
    }

    private bool ApplyModalVideoEditV2CompiledCandidate(
        VideoEditV2CompiledCandidate candidate,
        bool skipReviewAuthorization)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source
            || !source.ExactTimeline
            || _videoEditV2Plan is not VideoEditV2SelectionPlan
            || !HasFreshModalVideoEditV2PreviewsForPlan()
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || !string.Equals(
                current.SourceStamp,
                source.SourceStamp,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.SourceStamp,
                source.SourceStamp,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal))
        {
            return false;
        }

        _videoEditV2Candidate = candidate;
        _videoEditV2CandidateStale = false;
        _videoEditV2CandidateApproved = false;
        ModalVideoEditV2CompiledPromptTextBox.Text =
            _videoEditV2Candidate.BackendPrompt;
        ModalVideoEditV2SummaryText.Text =
            _videoEditV2Candidate.SummaryJa;
        ModalVideoEditV2ReviewPanel.Visibility = Visibility.Visible;
        ModalVideoEditV2CompileStatusText.Text = VideoEditV2Text(
            skipReviewAuthorization
                ? "UiVideoEditV2SkipNotStarted"
                : "UiVideoEditV2CandidateReady");
        ModalVideoEditV2StartButton.IsEnabled = false;
        RefreshModalVideoEditV2ActionControls();
        return true;
    }

    private void ApplyModalVideoEditV2Candidate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_videoEditV2Candidate is null
            || _videoEditV2CandidateStale
            || !HasFreshModalVideoEditV2PreviewsForPlan()
            || !string.Equals(
                _videoEditV2Candidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal))
        {
            MarkModalVideoEditV2CandidateStale();
            return;
        }

        _videoEditV2CandidateApproved = true;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateApproved");
        ModalVideoEditV2ReadinessText.Text =
            VideoEditV2Text("UiVideoEditV2WriterChecking");
        RefreshModalVideoEditV2ActionControls();
        _ = RefreshModalVideoEditV2WriterCapabilityAsync();
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

    public bool VideoEditV2ProbeEnabledForSmoke
        => ModalVideoEditV2ProbeButton?.IsEnabled == true;

    public bool VideoEditV2StartDisabledForSmoke
        => ModalVideoEditV2StartButton?.IsEnabled == false;

    public bool VideoEditV2BoardVisibleForSmoke
        => ModalVideoEditV2BoardVisible;

    public bool VideoEditV2CompilerDisabledForSmoke
        => ModalVideoEditV2CompileButton?.IsEnabled == false;

    public bool VideoEditV2TrimEntryEnabledForSmoke
        => ModalVideoEditV2TrimButton?.IsEnabled == true;

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
            checked((int)Math.Round(frameCount * 1000d / fps)),
            Math.Max(1, source.PlaybackWidth),
            Math.Max(1, source.PlaybackHeight),
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
        => ApplyVideoEditV2CompiledCandidateForSmoke(
            backendPrompt,
            summaryJa,
            "synthetic-compiler-v1",
            contextDigest: null);

    public bool ApplyVideoEditV2CompiledCandidateForSmoke(
        string backendPrompt,
        string summaryJa,
        string compilerRevision,
        string? contextDigest)
    {
        if (_videoEditV2Source is not VideoEditV2SourceChoice source
            || _videoEditV2Plan is not VideoEditV2SelectionPlan plan
            || _videoEditV2PreviewSet is not VideoEditV2PreviewSet previews)
        {
            return false;
        }
        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        string expectedDigest = VideoEditV2TransientContract
            .ComputeContextDigest(
                previews.Source,
                plan,
                previews.Previews,
                instruction,
                backendPrompt,
                summaryJa,
                compilerRevision);
        if (contextDigest is not null
            && !string.Equals(
                contextDigest,
                expectedDigest,
                StringComparison.Ordinal))
        {
            return false;
        }
        return ApplyModalVideoEditV2CompiledCandidate(
            new(
                backendPrompt,
                summaryJa,
                compilerRevision,
                expectedDigest,
                source.SourceStamp,
                BuildModalVideoEditV2ContextStamp()),
            skipReviewAuthorization: false);
    }

    public Task<bool> LoadVideoEditV2FramesForSmokeAsync()
        => LoadModalVideoEditV2FramesAsync();

    public Task<bool> CompileVideoEditV2ForSmokeAsync()
    {
        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        bool skip = ModalVideoEditV2SkipReviewCheckBox.IsChecked == true;
        ModalVideoEditV2SkipReviewCheckBox.IsChecked = false;
        return CompileModalVideoEditV2PromptAsync(instruction, skip);
    }

    public bool ApplyVideoEditV2CandidateApprovalForSmoke()
    {
        ApplyModalVideoEditV2Candidate_Click(
            ModalVideoEditV2ApplyButton,
            new RoutedEventArgs(Button.ClickEvent, ModalVideoEditV2ApplyButton));
        return _videoEditV2CandidateApproved;
    }

    public bool VideoEditV2SkipReviewCheckedForSmoke
        => ModalVideoEditV2SkipReviewCheckBox.IsChecked == true;

    public bool VideoEditV2PreviewImagesLoadedForSmoke
        => ModalVideoEditV2StartPreviewImage.Source is not null
            && ModalVideoEditV2MiddlePreviewImage.Source is not null
            && ModalVideoEditV2EndPreviewImage.Source is not null;

    public bool VideoEditV2PreviewStaleForSmoke
        => _videoEditV2PreviewStale;

    public bool VideoEditV2CompilePendingForSmoke
        => _videoEditV2CompilePending;

    public bool CancelVideoEditV2CompileForSmoke()
    {
        if (!_videoEditV2CompilePending)
            return false;
        _videoEditV2CompileGeneration++;
        CancellationTokenSource? active = Interlocked.Exchange(
            ref _videoEditV2CompileCts,
            null);
        TryCancelModalVideoEditV2Token(active);
        _videoEditV2CompilePending = false;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2ActionCanceled");
        RefreshModalVideoEditV2ActionControls();
        return true;
    }

    public bool VideoEditV2CandidateApprovedForSmoke
        => _videoEditV2CandidateApproved;

    public string VideoEditV2CompileStatusForSmoke
        => ModalVideoEditV2CompileStatusText?.Text ?? "";

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

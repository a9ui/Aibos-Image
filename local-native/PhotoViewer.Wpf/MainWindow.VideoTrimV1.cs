using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private VideoEditV2SourceChoice? _videoTrimV1Source;
    private VideoEditV2SourceSelector? _videoTrimV1Selector;
    private VideoTrimV1SourceProbe? _videoTrimV1Probe;
    private VideoTrimV1Plan? _videoTrimV1Plan;
    private VideoTrimV1PreviewSet? _videoTrimV1Previews;
    private CancellationTokenSource? _videoTrimV1InspectionCts;
    private long _videoTrimV1InspectionGeneration;
    private bool _videoTrimV1Syncing;
    private bool _videoTrimV1InspectionPending;
    private bool _videoTrimV1Closing;
    private bool _videoTrimV1WriterReady;
    private int _videoTrimV1StartAttemptCount;

    private string VideoTrimV1Text(string key)
        => FindResource(key) as string ?? key;

    private string VideoTrimV1Format(string key, params object[] values)
        => string.Format(
            CultureInfo.CurrentCulture,
            VideoTrimV1Text(key),
            values);

    private bool ModalVideoTrimV1BoardVisible =>
        ModalVideoTrimV1Popup?.Visibility == Visibility.Visible;

    private void SyncModalVideoTrimV1EntryPresentation()
    {
        if (ModalVideoTrimV1Button is null
            || ModalContextVideoTrimV1 is null
            || ModalVideoTrimV1ExternalMenuItem is null)
        {
            return;
        }
        bool visible = Modal.Visibility == Visibility.Visible
            && _modalShowingVideo
            && ModalVideo.Visibility == Visibility.Visible;
        ModalVideoTrimV1Button.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoTrimV1Button.IsEnabled = visible;
        ModalContextVideoTrimV1.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalContextVideoTrimV1.IsEnabled = visible;
        ModalVideoTrimV1ExternalMenuItem.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoTrimV1ExternalMenuItem.IsEnabled = visible;
        if (!visible && ModalVideoTrimV1BoardVisible)
            CloseModalVideoTrimV1Board(restoreFocus: false, stale: true);
        else if (visible
            && ModalVideoTrimV1BoardVisible
            && (_videoTrimV1Source is not VideoEditV2SourceChoice opened
                || !TryCaptureDisplayedVideoEditV2Source(
                    out VideoEditV2SourceChoice current)
                || !string.Equals(
                    opened.BaseSourceStamp,
                    current.BaseSourceStamp,
                    StringComparison.Ordinal)))
        {
            CloseModalVideoTrimV1Board(restoreFocus: false, stale: true);
        }
    }

    private void OpenModalVideoTrimV1_Click(
        object sender,
        RoutedEventArgs e)
        => OpenModalVideoTrimV1Board();

    private void OpenModalVideoTrimV1Board()
    {
        SyncModalVideoTrimV1EntryPresentation();
        if (ModalVideoTrimV1Button.Visibility != Visibility.Visible
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice source))
        {
            SetTransientStatusToast(
                VideoTrimV1Text("UiVideoTrimV1SourceUnavailable"));
            return;
        }

        if (ModalVideoEditV2BoardVisible)
            CloseModalVideoEditV2Board(restoreFocus: false, stale: false);
        if (ModalVideoFinishV2BoardVisible)
            CloseModalVideoFinishV2Board(restoreFocus: false, stale: false);
        if (ModalVideoToolsPopup?.Visibility == Visibility.Visible)
            CloseVideoToolsBoard(restoreFocus: false);
        if (ModalVideoGenerationPopup?.Visibility == Visibility.Visible)
            CloseModalVideoGenerationBoard();

        CancelModalVideoTrimV1Inspection();
        ResetModalVideoTrimV1DurableState();
        _videoTrimV1Source = source;
        _videoTrimV1Selector = null;
        _videoTrimV1Probe = null;
        _videoTrimV1Plan = null;
        _videoTrimV1Previews = null;
        _videoTrimV1Syncing = true;
        try
        {
            ModalVideoTrimV1StartFrameTextBox.Text = "0";
            ModalVideoTrimV1EndFrameTextBox.Text = "";
            ModalVideoTrimV1AudioComboBox.SelectedIndex = 0;
            ModalVideoTrimV1OverviewProgressBar.Minimum = 0;
            ModalVideoTrimV1OverviewProgressBar.Maximum = 1;
            ModalVideoTrimV1OverviewProgressBar.Value = 0;
            ModalVideoTrimV1CurrentFrameText.Text = "--";
            ModalVideoTrimV1ProbeStatusText.Text = source.Managed
                ? VideoTrimV1Text("UiVideoTrimV1ProbeRequiredManaged")
                : VideoTrimV1Text("UiVideoTrimV1ProbeRequiredExternal");
            ModalVideoTrimV1RangeStatusText.Text =
                VideoTrimV1Text("UiVideoTrimV1RangePending");
            ModalVideoTrimV1ReadinessText.Text =
                VideoTrimV1Text("UiVideoTrimV1WriterPending");
            ClearModalVideoTrimV1PreviewImages();
        }
        finally
        {
            _videoTrimV1Syncing = false;
        }
        ModalVideoTrimV1SourceText.Text = VideoTrimV1Format(
            "UiVideoTrimV1SourceFormat",
            source.DisplayName);
        ModalVideoTrimV1Popup.Visibility = Visibility.Visible;
        RefreshModalVideoTrimV1Presentation();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ModalVideoTrimV1BoardVisible)
                Keyboard.Focus(ModalVideoTrimV1ProbeButton);
        }));
    }

    private void CloseModalVideoTrimV1_Click(
        object sender,
        RoutedEventArgs e)
        => CloseModalVideoTrimV1Board(restoreFocus: true, stale: false);

    private void CloseModalVideoTrimV1Board(
        bool restoreFocus,
        bool stale)
    {
        if (_videoTrimV1Closing)
            return;
        _videoTrimV1Closing = true;
        try
        {
            CancelModalVideoTrimV1Inspection();
            ResetModalVideoTrimV1DurableState();
            ModalVideoTrimV1Popup.Visibility = Visibility.Collapsed;
            _videoTrimV1Source = null;
            _videoTrimV1Selector = null;
            _videoTrimV1Probe = null;
            _videoTrimV1Plan = null;
            _videoTrimV1Previews = null;
        }
        finally
        {
            _videoTrimV1Closing = false;
        }
        if (restoreFocus && ModalVideoTrimV1Button.IsVisible)
            Keyboard.Focus(ModalVideoTrimV1Button);
    }

    private void ModalVideoTrimV1Popup_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false
            && !_videoTrimV1Closing
            && _videoTrimV1Source is not null)
        {
            CloseModalVideoTrimV1Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private void ModalVideoTrimV1Backdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ModalVideoTrimV1Popup))
            CloseModalVideoTrimV1Board(restoreFocus: true, stale: false);
    }

    private void ModalVideoTrimV1_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        CloseModalVideoTrimV1Board(restoreFocus: true, stale: false);
    }

    private void CancelModalVideoTrimV1Inspection()
    {
        _videoTrimV1InspectionGeneration++;
        _videoTrimV1InspectionPending = false;
        CancellationTokenSource? cts = Interlocked.Exchange(
            ref _videoTrimV1InspectionCts,
            null);
        TryCancelModalVideoEditV2Token(cts);
    }

    private async void ProbeModalVideoTrimV1_Click(
        object sender,
        RoutedEventArgs e)
        => await LoadModalVideoTrimV1FramesAsync();

    private async Task<bool> LoadModalVideoTrimV1FramesAsync()
    {
        if (_videoTrimV1InspectionPending
            || _videoTrimV1Source is not VideoEditV2SourceChoice source
            || !ModalVideoTrimV1BoardVisible)
        {
            return false;
        }

        long generation = ++_videoTrimV1InspectionGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _videoTrimV1InspectionCts,
            cts);
        TryCancelModalVideoEditV2Token(previous);
        previous?.Dispose();
        _videoTrimV1InspectionPending = true;
        _videoTrimV1WriterReady = false;
        ModalVideoTrimV1ProbeStatusText.Text =
            VideoTrimV1Text("UiVideoTrimV1ProbeLoading");
        RefreshModalVideoTrimV1ActionControls();

        try
        {
            VideoEditV2ExplicitSourceCapture? capture =
                await CaptureModalVideoEditV2ExplicitSourceAsync(
                    source,
                    cts.Token);
            if (!IsModalVideoTrimV1InspectionCurrent(
                    generation,
                    source.BaseSourceStamp)
                || capture is null)
            {
                return false;
            }

            string probeJson =
                VideoTrimV1Contract.BuildProbeRequestJson(capture.Selector);
            EnhancementApiResponse probeResponse =
                await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    VideoTrimV1Contract.SourceInspectionRoute,
                    token: cts.Token,
                    exactBodyJson: probeJson,
                    maxResponseBytes:
                        VideoTrimV1Contract.MaximumProbeResponseBytes,
                    timeoutError: VideoTrimV1Text(
                        "UiVideoTrimV1ActionTimedOut"));
            if (!IsModalVideoTrimV1InspectionCurrent(
                    generation,
                    source.BaseSourceStamp)
                || !probeResponse.Ok
                || probeResponse.Payload is not JsonElement probePayload
                || !VideoTrimV1Contract.TryParseProbeResponse(
                    probePayload,
                    out VideoTrimV1SourceProbe probe))
            {
                ModalVideoTrimV1ProbeStatusText.Text =
                    string.IsNullOrWhiteSpace(probeResponse.Error)
                        ? VideoTrimV1Text("UiVideoTrimV1ProbeFailed")
                        : probeResponse.Error;
                return false;
            }

            int start = TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1StartFrameTextBox,
                out int requestedStart)
                    ? requestedStart
                    : 0;
            int end = TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1EndFrameTextBox,
                out int requestedEnd)
                    ? requestedEnd
                    : probe.FrameCount;
            start = Math.Clamp(start, 0, probe.FrameCount - 1);
            end = Math.Clamp(end, start + 1, probe.FrameCount);
            string audio = ReadModalVideoTrimV1AudioPolicy();
            if (!VideoTrimV1Contract.TryPlan(
                    probe,
                    start,
                    end,
                    audio,
                    out VideoTrimV1Plan plan))
            {
                ModalVideoTrimV1ProbeStatusText.Text =
                    VideoTrimV1Text("UiVideoTrimV1ProbeFailed");
                return false;
            }

            string sourceStamp = string.Join(
                ':',
                source.BaseSourceStamp,
                "video-trim-v1",
                probe.ProbeDigest,
                probe.SourceIdentityDigest);
            VideoTrimV1PreviewSet? previewSet = null;
            if (plan.SupportsThreePointPreview)
            {
                string previewJson =
                    VideoTrimV1Contract.BuildPreviewRequestJson(
                        capture.Selector,
                        plan,
                        probe.SourceIdentityDigest);
                EnhancementApiResponse previewResponse =
                    await SendEnhancementApiAsync(
                        HttpMethod.Post,
                        VideoTrimV1Contract.SourceInspectionRoute,
                        token: cts.Token,
                        exactBodyJson: previewJson,
                        maxResponseBytes:
                            VideoTrimV1Contract.MaximumPreviewResponseBytes,
                        timeoutError: VideoTrimV1Text(
                            "UiVideoTrimV1ActionTimedOut"));
                if (!IsModalVideoTrimV1InspectionCurrent(
                        generation,
                        source.BaseSourceStamp)
                    || !previewResponse.Ok
                    || previewResponse.Payload is not JsonElement previewPayload
                    || !VideoTrimV1Contract.TryParsePreviewResponse(
                        previewPayload,
                        capture.Selector,
                        probe,
                        plan,
                        sourceStamp,
                        out previewSet))
                {
                    ModalVideoTrimV1ProbeStatusText.Text =
                        string.IsNullOrWhiteSpace(previewResponse.Error)
                            ? VideoTrimV1Text("UiVideoTrimV1PreviewFailed")
                            : previewResponse.Error;
                    return false;
                }
            }

            _videoTrimV1Source = source with
            {
                SourceStamp = sourceStamp,
                ExactTimeline = true,
                FpsNumerator = probe.FpsNumerator,
                FpsDenominator = probe.FpsDenominator,
                FrameCount = probe.FrameCount,
                PlaybackDurationSeconds =
                    (double)probe.DurationNumerator
                        / probe.DurationDenominator,
                PlaybackWidth = probe.Width,
                PlaybackHeight = probe.Height,
            };
            _videoTrimV1Selector = capture.Selector;
            _videoTrimV1Probe = probe;
            _videoTrimV1Plan = plan;
            _videoTrimV1Previews = previewSet;
            ApplyModalVideoTrimV1LoadedState();
            ModalVideoTrimV1ProbeStatusText.Text = plan.SupportsThreePointPreview
                ? VideoTrimV1Text("UiVideoTrimV1ProbeReady")
                : VideoTrimV1Text("UiVideoTrimV1ShortSelectionReady");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException)
        {
            if (ModalVideoTrimV1BoardVisible)
            {
                ModalVideoTrimV1ProbeStatusText.Text =
                    VideoTrimV1Text("UiVideoTrimV1ProbeFailed");
            }
            return false;
        }
        finally
        {
            if (generation == _videoTrimV1InspectionGeneration)
            {
                _videoTrimV1InspectionPending = false;
                if (ReferenceEquals(_videoTrimV1InspectionCts, cts))
                    _videoTrimV1InspectionCts = null;
                RefreshModalVideoTrimV1ActionControls();
            }
            cts.Dispose();
        }
    }

    private bool IsModalVideoTrimV1InspectionCurrent(
        long generation,
        string expectedBaseSourceStamp)
        => generation == _videoTrimV1InspectionGeneration
            && _videoTrimV1InspectionPending
            && ModalVideoTrimV1BoardVisible
            && TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            && string.Equals(
                current.BaseSourceStamp,
                expectedBaseSourceStamp,
                StringComparison.Ordinal);

    private void ApplyModalVideoTrimV1LoadedState()
    {
        if (_videoTrimV1Probe is not VideoTrimV1SourceProbe probe
            || _videoTrimV1Plan is not VideoTrimV1Plan plan)
        {
            return;
        }
        _videoTrimV1Syncing = true;
        try
        {
            ModalVideoTrimV1StartFrameTextBox.Text =
                plan.StartFrame.ToString(CultureInfo.InvariantCulture);
            ModalVideoTrimV1EndFrameTextBox.Text =
                plan.EndFrameExclusive.ToString(CultureInfo.InvariantCulture);
            ModalVideoTrimV1OverviewProgressBar.Maximum = probe.FrameCount;
            ModalVideoTrimV1OverviewProgressBar.Value =
                plan.EndFrameExclusive;
            ApplyModalVideoTrimV1PreviewImages();
        }
        finally
        {
            _videoTrimV1Syncing = false;
        }
        RefreshModalVideoTrimV1Presentation();
    }

    private void ModalVideoTrimV1Input_Changed(
        object sender,
        RoutedEventArgs e)
    {
        // Text/selection events can fire while InitializeComponent is still
        // constructing the board. The initialized board handles later user
        // changes; partial XAML construction must stay side-effect free.
        if (!_videoTrimV1Syncing
            && ModalVideoTrimV1ReadinessText is not null)
            RefreshModalVideoTrimV1Plan();
    }

    private void RefreshModalVideoTrimV1Plan()
    {
        _videoTrimV1WriterReady = false;
        if (_videoTrimV1Probe is VideoTrimV1SourceProbe probe
            && TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1StartFrameTextBox,
                out int start)
            && TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1EndFrameTextBox,
                out int end)
            && VideoTrimV1Contract.TryPlan(
                probe,
                start,
                end,
                ReadModalVideoTrimV1AudioPolicy(),
                out VideoTrimV1Plan plan))
        {
            if (_videoTrimV1Plan != plan)
            {
                _videoTrimV1Previews = null;
                ClearModalVideoTrimV1PreviewImages();
            }
            _videoTrimV1Plan = plan;
        }
        else
        {
            _videoTrimV1Plan = null;
            _videoTrimV1Previews = null;
            ClearModalVideoTrimV1PreviewImages();
        }
        ModalVideoTrimV1ReadinessText.Text =
            VideoTrimV1Text("UiVideoTrimV1WriterPending");
        RefreshModalVideoTrimV1Presentation();
    }

    private void RefreshModalVideoTrimV1Presentation()
    {
        UpdateModalVideoTrimV1CurrentPosition();
        if (_videoTrimV1Probe is VideoTrimV1SourceProbe probe
            && _videoTrimV1Plan is VideoTrimV1Plan plan)
        {
            ModalVideoTrimV1OverviewProgressBar.Maximum = probe.FrameCount;
            ModalVideoTrimV1OverviewProgressBar.Value =
                plan.EndFrameExclusive;
            ModalVideoTrimV1RangeStatusText.Text = VideoTrimV1Format(
                "UiVideoTrimV1RangeFormat",
                plan.StartFrame,
                plan.EndFrameExclusive,
                plan.SelectedFrameCount,
                VideoTrimV1Contract.FormatFrameTime(
                    plan.StartFrame,
                    plan.FpsNumerator,
                    plan.FpsDenominator),
                VideoTrimV1Contract.FormatFrameTime(
                    plan.EndFrameExclusive,
                    plan.FpsNumerator,
                    plan.FpsDenominator),
                probe.FrameCount);
            UpdateModalVideoTrimV1PreviewLabels(plan);
        }
        else
        {
            ModalVideoTrimV1RangeStatusText.Text =
                VideoTrimV1Text("UiVideoTrimV1RangeInvalid");
            UpdateModalVideoTrimV1PreviewLabels(null);
        }
        RefreshModalVideoTrimV1ActionControls();
    }

    private void RefreshModalVideoTrimV1ActionControls()
    {
        bool loaded = _videoTrimV1Probe is not null
            && _videoTrimV1Selector is not null;
        bool planReady = loaded && _videoTrimV1Plan is not null;
        ModalVideoTrimV1ProbeButton.IsEnabled =
            !_videoTrimV1InspectionPending;
        ModalVideoTrimV1ReadinessButton.IsEnabled = planReady
            && !_videoTrimV1InspectionPending
            && !_videoTrimV1RequestPending;
        ModalVideoTrimV1StartButton.IsEnabled = planReady
            && _videoTrimV1WriterReady
            && !_videoTrimV1InspectionPending
            && !_videoTrimV1RequestPending;
        ModalVideoTrimV1StartButton.Content = VideoTrimV1Text(
            ModalVideoTrimV1StartButton.IsEnabled
                ? "UiVideoTrimV1StartAction"
                : "UiVideoTrimV1StartPending");
        foreach (Control control in new Control[]
        {
            ModalVideoTrimV1StartFrameTextBox,
            ModalVideoTrimV1EndFrameTextBox,
            ModalVideoTrimV1AudioComboBox,
            ModalVideoTrimV1UseCurrentStartButton,
            ModalVideoTrimV1UseCurrentEndButton,
            ModalVideoTrimV1StartMinusButton,
            ModalVideoTrimV1StartPlusButton,
            ModalVideoTrimV1EndMinusButton,
            ModalVideoTrimV1EndPlusButton,
        })
        {
            control.IsEnabled = loaded && !_videoTrimV1RequestPending;
        }
        bool canSeek = _videoTrimV1Plan is not null;
        ModalVideoTrimV1StartPreviewButton.IsEnabled = canSeek;
        ModalVideoTrimV1MiddlePreviewButton.IsEnabled = canSeek;
        ModalVideoTrimV1EndPreviewButton.IsEnabled = canSeek;
    }

    private static bool TryReadModalVideoTrimV1Frame(
        TextBox textBox,
        out int frame)
        => int.TryParse(
            textBox.Text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out frame);

    private string ReadModalVideoTrimV1AudioPolicy()
        => (ModalVideoTrimV1AudioComboBox.SelectedItem as ComboBoxItem)
            ?.Tag?.ToString() ?? "preserve";

    private void ModalVideoTrimV1UseCurrentStart_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (TryGetModalVideoTrimV1CurrentFrame(out int frame))
            ModalVideoTrimV1StartFrameTextBox.Text =
                frame.ToString(CultureInfo.InvariantCulture);
    }

    private void ModalVideoTrimV1UseCurrentEnd_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (TryGetModalVideoTrimV1CurrentFrame(out int frame)
            && _videoTrimV1Probe is VideoTrimV1SourceProbe probe)
        {
            ModalVideoTrimV1EndFrameTextBox.Text = Math.Min(
                    probe.FrameCount,
                    frame + 1)
                .ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ModalVideoTrimV1StepFrame_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || _videoTrimV1Probe is not VideoTrimV1SourceProbe probe)
        {
            return;
        }
        string[] parts = tag.Split(':');
        if (parts.Length != 2
            || !int.TryParse(
                parts[1],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int delta))
        {
            return;
        }
        TextBox target = parts[0] == "start"
            ? ModalVideoTrimV1StartFrameTextBox
            : ModalVideoTrimV1EndFrameTextBox;
        if (!TryReadModalVideoTrimV1Frame(target, out int current))
            return;
        int maximum = parts[0] == "start"
            ? Math.Max(0, probe.FrameCount - 1)
            : probe.FrameCount;
        target.Text = Math.Clamp(current + delta, 0, maximum)
            .ToString(CultureInfo.InvariantCulture);
    }

    private bool TryGetModalVideoTrimV1CurrentFrame(out int frame)
    {
        frame = 0;
        if (_videoTrimV1Probe is not VideoTrimV1SourceProbe probe
            || !double.IsFinite(ModalVideo.Position.TotalSeconds))
        {
            return false;
        }
        double exact = ModalVideo.Position.TotalSeconds
            * probe.FpsNumerator / probe.FpsDenominator;
        if (!double.IsFinite(exact))
            return false;
        frame = Math.Clamp(
            // TimeSpan has 100 ns resolution. Converting an exact rational
            // frame position to ticks can land a fraction below the integer;
            // absorb only that representation error, never a visible frame.
            (int)Math.Floor(exact + 0.000_01),
            0,
            probe.FrameCount - 1);
        return true;
    }

    private void UpdateModalVideoTrimV1CurrentPosition()
    {
        ModalVideoTrimV1CurrentFrameText.Text =
            TryGetModalVideoTrimV1CurrentFrame(out int frame)
                ? VideoTrimV1Format(
                    "UiVideoTrimV1CurrentFrameFormat",
                    frame,
                    VideoTrimV1Contract.FormatFrameTime(
                        frame,
                        _videoTrimV1Probe!.FpsNumerator,
                        _videoTrimV1Probe.FpsDenominator))
                : "--";
    }

    private void ModalVideoTrimV1PreviewSeek_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string role }
            || _videoTrimV1Plan is not VideoTrimV1Plan plan)
        {
            return;
        }
        int frame = role switch
        {
            "start" => plan.StartPreviewFrame,
            "middle" => plan.MiddlePreviewFrame,
            "end" => plan.EndPreviewFrame,
            _ => -1,
        };
        if (frame < 0)
            return;
        ModalVideo.Position = TimeSpan.FromSeconds(
            (double)frame * plan.FpsDenominator / plan.FpsNumerator);
        UpdateModalVideoTrimV1CurrentPosition();
    }

    private void UpdateModalVideoTrimV1PreviewLabels(VideoTrimV1Plan? plan)
    {
        (TextBlock Frame, TextBlock Time)[] labels =
        [
            (ModalVideoTrimV1StartPreviewFrameText,
                ModalVideoTrimV1StartPreviewTimeText),
            (ModalVideoTrimV1MiddlePreviewFrameText,
                ModalVideoTrimV1MiddlePreviewTimeText),
            (ModalVideoTrimV1EndPreviewFrameText,
                ModalVideoTrimV1EndPreviewTimeText),
        ];
        int[] frames = plan is null
            ? [-1, -1, -1]
            :
            [
                plan.StartPreviewFrame,
                plan.MiddlePreviewFrame,
                plan.EndPreviewFrame,
            ];
        for (int index = 0; index < labels.Length; index++)
        {
            labels[index].Frame.Text = frames[index] < 0
                ? "--"
                : frames[index].ToString(CultureInfo.InvariantCulture);
            labels[index].Time.Text = plan is null
                ? "--"
                : VideoTrimV1Contract.FormatFrameTime(
                    frames[index],
                    plan.FpsNumerator,
                    plan.FpsDenominator) + " s";
        }
    }

    private void ApplyModalVideoTrimV1PreviewImages()
    {
        ClearModalVideoTrimV1PreviewImages();
        if (_videoTrimV1Previews is not VideoTrimV1PreviewSet set
            || set.Previews.Count != 3)
        {
            return;
        }
        ModalVideoTrimV1StartPreviewImage.Source = set.Previews[0].Image;
        ModalVideoTrimV1MiddlePreviewImage.Source = set.Previews[1].Image;
        ModalVideoTrimV1EndPreviewImage.Source = set.Previews[2].Image;
    }

    private void ClearModalVideoTrimV1PreviewImages()
    {
        if (ModalVideoTrimV1StartPreviewImage is null)
            return;
        ModalVideoTrimV1StartPreviewImage.Source = null;
        ModalVideoTrimV1MiddlePreviewImage.Source = null;
        ModalVideoTrimV1EndPreviewImage.Source = null;
    }

    public bool VideoTrimV1EntryVisibleForSmoke
    {
        get
        {
            SyncModalVideoTrimV1EntryPresentation();
            return ModalVideoTrimV1Button.Visibility == Visibility.Visible
                && ModalContextVideoTrimV1.Visibility == Visibility.Visible;
        }
    }

    public bool VideoTrimV1ExternalContextEntryForSmoke
    {
        get
        {
            SyncModalVideoTrimV1EntryPresentation();
            return ModalVideoTrimV1ExternalMenuItem.Visibility
                    == Visibility.Visible
                && ModalVideoTrimV1ExternalMenuItem.IsEnabled;
        }
    }

    public bool OpenVideoTrimV1ForSmoke()
    {
        OpenModalVideoTrimV1Board();
        return ModalVideoTrimV1BoardVisible;
    }

    public bool VideoTrimV1BoardVisibleForSmoke =>
        ModalVideoTrimV1BoardVisible;

    public bool VideoTrimV1StartDisabledForSmoke =>
        !ModalVideoTrimV1StartButton.IsEnabled;

    public bool VideoTrimV1ProbeEnabledForSmoke =>
        ModalVideoTrimV1ProbeButton.IsEnabled;

    public Task<bool> LoadVideoTrimV1FramesForSmokeAsync()
        => LoadModalVideoTrimV1FramesAsync();

    public bool SetVideoTrimV1SelectionForSmoke(
        int startFrame,
        int endFrameExclusive)
    {
        ModalVideoTrimV1StartFrameTextBox.Text =
            startFrame.ToString(CultureInfo.InvariantCulture);
        ModalVideoTrimV1EndFrameTextBox.Text =
            endFrameExclusive.ToString(CultureInfo.InvariantCulture);
        RefreshModalVideoTrimV1Plan();
        return _videoTrimV1Plan is not null;
    }

    public string[] VideoTrimV1PreviewFramesForSmoke =>
        _videoTrimV1Plan is VideoTrimV1Plan plan
            ?
            [
                plan.StartPreviewFrame.ToString(CultureInfo.InvariantCulture),
                plan.MiddlePreviewFrame.ToString(CultureInfo.InvariantCulture),
                plan.EndPreviewFrame.ToString(CultureInfo.InvariantCulture),
            ]
            : [];

    public bool VideoTrimV1PreviewImagesLoadedForSmoke =>
        ModalVideoTrimV1StartPreviewImage.Source is not null
        && ModalVideoTrimV1MiddlePreviewImage.Source is not null
        && ModalVideoTrimV1EndPreviewImage.Source is not null;

    public (int StartFrame, int EndFrameExclusive)
        VideoTrimV1SelectionForSmoke
        => (
            TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1StartFrameTextBox,
                out int start) ? start : -1,
            TryReadModalVideoTrimV1Frame(
                ModalVideoTrimV1EndFrameTextBox,
                out int end) ? end : -1);

    public (double Maximum, double Value) VideoTrimV1OverviewForSmoke
        => (ModalVideoTrimV1OverviewProgressBar.Maximum,
            ModalVideoTrimV1OverviewProgressBar.Value);

    public void SetVideoTrimV1CurrentFrameForSmoke(int frame)
    {
        if (_videoTrimV1Probe is not VideoTrimV1SourceProbe probe)
            return;
        int bounded = Math.Clamp(frame, 0, probe.FrameCount - 1);
        ModalVideo.Position = TimeSpan.FromSeconds(
            (double)bounded * probe.FpsDenominator / probe.FpsNumerator);
        UpdateModalVideoTrimV1CurrentPosition();
    }

    public void UseVideoTrimV1CurrentStartForSmoke()
        => ModalVideoTrimV1UseCurrentStart_Click(
            ModalVideoTrimV1UseCurrentStartButton,
            new RoutedEventArgs());

    public void UseVideoTrimV1CurrentEndForSmoke()
        => ModalVideoTrimV1UseCurrentEnd_Click(
            ModalVideoTrimV1UseCurrentEndButton,
            new RoutedEventArgs());

    public void StepVideoTrimV1SelectionForSmoke(
        string edge,
        int delta)
    {
        Button button = edge == "start"
            ? delta < 0
                ? ModalVideoTrimV1StartMinusButton
                : ModalVideoTrimV1StartPlusButton
            : delta < 0
                ? ModalVideoTrimV1EndMinusButton
                : ModalVideoTrimV1EndPlusButton;
        ModalVideoTrimV1StepFrame_Click(button, new RoutedEventArgs());
    }

    public void SeekVideoTrimV1PreviewForSmoke(string role)
    {
        Button button = role switch
        {
            "start" => ModalVideoTrimV1StartPreviewButton,
            "middle" => ModalVideoTrimV1MiddlePreviewButton,
            "end" => ModalVideoTrimV1EndPreviewButton,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        ModalVideoTrimV1PreviewSeek_Click(button, new RoutedEventArgs());
    }

    public int VideoTrimV1CurrentFrameForSmoke
        => TryGetModalVideoTrimV1CurrentFrame(out int frame) ? frame : -1;

    public int VideoTrimV1StartAttemptCountForSmoke =>
        _videoTrimV1StartAttemptCount;

    public void CloseVideoTrimV1ForSmoke()
        => CloseModalVideoTrimV1Board(restoreFocus: false, stale: false);
}

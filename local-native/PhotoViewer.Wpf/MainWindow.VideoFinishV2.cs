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
    private sealed record VideoFinishV2ProbeState(
        string BaseSourceStamp,
        VideoEditV2SourceSelector Selector,
        VideoEditV2SourceSummary Summary);

    private sealed record VideoFinishV2ActionContext(
        long Generation,
        VideoEditV2SourceChoice Source,
        VideoEditV2SourceSelector Selector,
        VideoEditV2SourceSummary Summary,
        VideoFinishV2Plan Plan,
        string SourceId,
        VideoSourceChoice DependencySource,
        VideoH3SourceStamp SourceStamp,
        JsonElement RequestBody);

    private VideoEditV2SourceChoice? _videoFinishV2Source;
    private VideoEditV2SourceChoice? _videoFinishV2SourceForSmoke;
    private VideoFinishV2ProbeState? _videoFinishV2Probe;
    private VideoEditV2SourceSummary? _videoFinishV2Summary;
    private VideoFinishV2Plan? _videoFinishV2Plan;
    private CancellationTokenSource? _videoFinishV2ProbeCts;
    private CancellationTokenSource? _videoFinishV2StartCts;
    private long _videoFinishV2ProbeGeneration;
    private long _videoFinishV2HealthGeneration;
    private long _videoFinishV2ActionGeneration;
    private bool _videoFinishV2ProbePending;
    private bool _videoFinishV2HealthPending;
    private bool _videoFinishV2RequestPending;
    private bool _videoFinishV2WriterReady;
    private bool _videoFinishV2Syncing;
    private bool _videoFinishV2Closing;
    private bool _videoFinishV2WindowStateHooked;
    private bool _videoFinishV2LastCloseWasStale;
    private string? _videoFinishV2ReadyContextStamp;
    private int _videoFinishV2StartAttemptCount;

    private string VideoFinishV2Text(string key)
        => FindResource(key) as string ?? key;

    private string VideoFinishV2Format(string key, params object[] values)
        => string.Format(
            CultureInfo.CurrentCulture,
            VideoFinishV2Text(key),
            values);

    private bool TryCaptureDisplayedVideoFinishV2Source(
        out VideoEditV2SourceChoice source)
    {
        if (_videoFinishV2SourceForSmoke is VideoEditV2SourceChoice smoke)
        {
            source = smoke;
            return true;
        }
        return TryCaptureDisplayedVideoEditV2Source(out source);
    }

    private void SyncModalVideoFinishV2EntryPresentation()
    {
        if (ModalVideoFinishButton is null
            || ModalContextVideoFinishV2 is null
            || ModalVideoFinishV2ExternalMenuItem is null)
        {
            return;
        }

        bool visible = TryCaptureDisplayedVideoFinishV2Source(out var current);
        ModalVideoFinishButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoFinishButton.IsEnabled = visible;
        ModalContextVideoFinishV2.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalContextVideoFinishV2.IsEnabled = visible;
        ModalVideoFinishV2ExternalMenuItem.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModalVideoFinishV2ExternalMenuItem.IsEnabled = visible;

        if (!ModalVideoFinishV2BoardVisible)
            return;
        if (!visible
            || _videoFinishV2Source is not VideoEditV2SourceChoice opened
            || !string.Equals(
                current.BaseSourceStamp,
                opened.BaseSourceStamp,
                StringComparison.Ordinal))
        {
            CloseModalVideoFinishV2Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private bool TryBuildVideoFinishV2Summary(
        VideoEditV2SourceChoice source,
        out VideoEditV2SourceSummary summary)
    {
        summary = null!;
        if (!source.Managed)
        {
            if (_videoFinishV2Probe is not VideoFinishV2ProbeState probe
                || !string.Equals(
                    probe.BaseSourceStamp,
                    source.BaseSourceStamp,
                    StringComparison.Ordinal))
            {
                return false;
            }
            summary = probe.Summary;
            return true;
        }

        if (!source.ExactTimeline
            || source.PlaybackDurationSeconds <= 0
            || source.PlaybackWidth <= 0
            || source.PlaybackHeight <= 0)
        {
            return false;
        }
        try
        {
            int durationMs = checked((int)Math.Round(
                source.PlaybackDurationSeconds * 1_000d,
                MidpointRounding.AwayFromZero));
            var candidate = new VideoEditV2SourceSummary(
                source.FrameCount,
                source.FpsNumerator,
                source.FpsDenominator,
                durationMs,
                source.PlaybackWidth,
                source.PlaybackHeight);
            if (!VideoFinishV2Contract.TryPlan(
                    candidate,
                    "standard",
                    2,
                    out _,
                    out _))
            {
                return false;
            }
            summary = candidate;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void OpenModalVideoFinishV2Board()
    {
        SyncModalVideoFinishV2EntryPresentation();
        if (ModalVideoFinishButton?.Visibility != Visibility.Visible
            || !TryCaptureDisplayedVideoFinishV2Source(
                out VideoEditV2SourceChoice source))
        {
            SetTransientStatusToast(
                VideoFinishV2Text("UiVideoFinishV2SourceUnavailable"));
            return;
        }

        if (ModalVideoEditV2Popup?.Visibility == Visibility.Visible)
        {
            CloseModalVideoEditV2Board(
                restoreFocus: false,
                stale: true);
        }
        if (ModalVideoTrimV1BoardVisible)
            CloseModalVideoTrimV1Board(restoreFocus: false, stale: false);
        if (ModalVideoToolsPopup?.Visibility == Visibility.Visible)
            CloseVideoToolsBoard(restoreFocus: false);
        if (ModalVideoGenerationPopup?.Visibility == Visibility.Visible)
            CloseModalVideoGenerationBoard();

        CancelModalVideoFinishV2Actions();
        _videoFinishV2Source = source;
        _videoFinishV2Probe = null;
        _videoFinishV2Summary = null;
        _videoFinishV2Plan = null;
        _videoFinishV2WriterReady = false;
        _videoFinishV2ReadyContextStamp = null;
        _videoFinishV2LastCloseWasStale = false;
        ApplyVideoFinishV2DefaultsToBoardIfNeeded();
        _videoFinishV2Syncing = true;
        try
        {
            ModalVideoFinishV2ProbeStatusText.Text = source.Managed
                ? VideoFinishV2Text("UiVideoFinishV2ManagedExact")
                : VideoFinishV2Text("UiVideoFinishV2ProbeRequired");
            ModalVideoFinishV2ReadinessText.Text =
                VideoFinishV2Text("UiVideoFinishV2ReadinessPending");
        }
        finally
        {
            _videoFinishV2Syncing = false;
        }

        if (TryBuildVideoFinishV2Summary(source, out var summary))
            _videoFinishV2Summary = summary;
        if (!_videoFinishV2WindowStateHooked)
        {
            _videoFinishV2WindowStateHooked = true;
            StateChanged += ModalVideoFinishV2Window_StateChanged;
        }
        ModalVideoFinishV2Popup.Visibility = Visibility.Visible;
        RefreshModalVideoFinishV2Presentation(invalidateReadiness: false);
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!ModalVideoFinishV2BoardVisible)
                    return;
                Keyboard.Focus(source.Managed
                    ? ModalVideoFinishV2ModeComboBox
                    : ModalVideoFinishV2ProbeButton);
            }),
            DispatcherPriority.Input);
    }

    private void CloseModalVideoFinishV2_Click(
        object sender,
        RoutedEventArgs e)
        => CloseModalVideoFinishV2Board(restoreFocus: true, stale: false);

    private void ModalVideoFinishV2Backdrop_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            CloseModalVideoFinishV2Board(
                restoreFocus: true,
                stale: false);
        }
    }

    private void ModalVideoFinishV2_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        CloseModalVideoFinishV2Board(restoreFocus: true, stale: true);
        e.Handled = true;
    }

    private void ModalVideoFinishV2Popup_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false
            && !_videoFinishV2Closing
            && _videoFinishV2Source is not null)
        {
            CloseModalVideoFinishV2Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private void ModalVideoFinishV2Window_StateChanged(
        object? sender,
        EventArgs e)
    {
        if (WindowState == WindowState.Minimized
            && ModalVideoFinishV2BoardVisible)
        {
            CloseModalVideoFinishV2Board(
                restoreFocus: false,
                stale: true);
        }
    }

    private void CloseModalVideoFinishV2Board(
        bool restoreFocus,
        bool stale)
    {
        CancelModalVideoFinishV2Actions();
        _videoFinishV2LastCloseWasStale = stale;
        _videoFinishV2Closing = true;
        try
        {
            if (ModalVideoFinishV2Popup is not null)
                ModalVideoFinishV2Popup.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _videoFinishV2Closing = false;
        }
        _videoFinishV2Source = null;
        _videoFinishV2Probe = null;
        _videoFinishV2Summary = null;
        _videoFinishV2Plan = null;
        _videoFinishV2WriterReady = false;
        _videoFinishV2ReadyContextStamp = null;
        if (restoreFocus
            && ModalVideoFinishButton?.Visibility == Visibility.Visible)
        {
            ModalVideoFinishButton.Focus();
        }
    }

    private void CancelModalVideoFinishV2Actions()
    {
        _videoFinishV2ProbeGeneration++;
        _videoFinishV2HealthGeneration++;
        _videoFinishV2ActionGeneration++;
        _videoFinishV2ProbePending = false;
        _videoFinishV2HealthPending = false;
        _videoFinishV2RequestPending = false;
        CancellationTokenSource? probe = Interlocked.Exchange(
            ref _videoFinishV2ProbeCts,
            null);
        CancellationTokenSource? start = Interlocked.Exchange(
            ref _videoFinishV2StartCts,
            null);
        TryCancelModalVideoEditV2Token(probe);
        TryCancelModalVideoEditV2Token(start);
    }

    private bool ModalVideoFinishV2BoardVisible
        => ModalVideoFinishV2Popup?.Visibility == Visibility.Visible;

    private void FocusModalVideoFinishV2Board()
    {
        if (!ModalVideoFinishV2BoardVisible
            || ModalVideoFinishV2Popup.IsKeyboardFocusWithin)
        {
            return;
        }
        Keyboard.Focus(
            ModalVideoFinishV2ProbeButton.Visibility == Visibility.Visible
                ? ModalVideoFinishV2ProbeButton
                : ModalVideoFinishV2ModeComboBox);
    }

    private void ModalVideoFinishV2Input_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_videoFinishV2Syncing)
            return;
        RefreshModalVideoFinishV2Presentation(invalidateReadiness: true);
    }

    private string ReadModalVideoFinishV2Mode()
        => (ModalVideoFinishV2ModeComboBox.SelectedItem as ComboBoxItem)
            ?.Tag?.ToString() ?? "";

    private int ReadModalVideoFinishV2Scale()
        => int.TryParse(
                (ModalVideoFinishV2ScaleComboBox.SelectedItem as ComboBoxItem)
                    ?.Tag?.ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int scale)
            ? scale
            : 0;

    private void RefreshModalVideoFinishV2Presentation(
        bool invalidateReadiness)
    {
        if (_videoFinishV2Source is not VideoEditV2SourceChoice source)
            return;
        if (invalidateReadiness)
            InvalidateModalVideoFinishV2Readiness();

        ModalVideoFinishV2ProbeButton.Visibility = source.Managed
            ? Visibility.Collapsed
            : Visibility.Visible;
        ModalVideoFinishV2ProbeButton.IsEnabled = !source.Managed
            && !_videoFinishV2ProbePending
            && !_videoFinishV2RequestPending;
        ModalVideoFinishV2SourceText.Text = BuildVideoFinishV2SourceText(
            source);
        ModalVideoFinishV2ModeDetailText.Text = ReadModalVideoFinishV2Mode()
            switch
            {
                "fast" => VideoFinishV2Text("UiVideoFinishV2ModeFastHelp"),
                "standard" => VideoFinishV2Text(
                    "UiVideoFinishV2ModeStandardHelp"),
                "quality" => VideoFinishV2Text(
                    "UiVideoFinishV2ModeQualityHelp"),
                _ => VideoFinishV2Text("UiVideoFinishV2PlanInvalid"),
            };

        _videoFinishV2Plan = null;
        if (_videoFinishV2Summary is VideoEditV2SourceSummary summary
            && VideoFinishV2Contract.TryPlan(
                summary,
                ReadModalVideoFinishV2Mode(),
                ReadModalVideoFinishV2Scale(),
                out VideoFinishV2Plan plan,
                out VideoFinishV2PlanError error))
        {
            _videoFinishV2Plan = plan;
            ModalVideoFinishV2PlanText.Text = VideoFinishV2Format(
                "UiVideoFinishV2PlanFormat",
                plan.InputWidth,
                plan.InputHeight,
                plan.OutputWidth,
                plan.OutputHeight,
                plan.Scale,
                plan.SourceFrameCount,
                $"{plan.FpsNumerator}/{plan.FpsDenominator}",
                (plan.DurationMs / 1_000d).ToString(
                    "0.###",
                    CultureInfo.CurrentCulture));
            ModalVideoFinishV2EstimateText.Text = VideoFinishV2Format(
                "UiVideoFinishV2EstimateFormat",
                plan.OutputPixelArea.ToString("N0", CultureInfo.CurrentCulture),
                FormatVideoFinishV2Bytes(plan.EstimatedOutputFrameBytes),
                FormatVideoFinishV2Bytes(
                    VideoFinishV2Contract.MaximumOutputBytes));
        }
        else
        {
            ModalVideoFinishV2PlanText.Text = VideoFinishV2Text(
                _videoFinishV2Summary is null
                    ? "UiVideoFinishV2ProbeRequired"
                    : ReadModalVideoFinishV2Scale() == 4
                    ? "UiVideoFinishV2Scale4OutOfBounds"
                    : "UiVideoFinishV2PlanInvalid");
            ModalVideoFinishV2EstimateText.Text = VideoFinishV2Text(
                "UiVideoFinishV2EstimatePending");
        }
        ModalVideoFinishV2PolicyText.Text = VideoFinishV2Text(
            "UiVideoFinishV2PreservePolicy");
        RefreshModalVideoFinishV2ActionControls();
    }

    private string BuildVideoFinishV2SourceText(
        VideoEditV2SourceChoice source)
    {
        if (_videoFinishV2Summary is VideoEditV2SourceSummary summary)
        {
            return VideoFinishV2Format(
                source.Managed
                    ? "UiVideoFinishV2ManagedSourceFormat"
                    : "UiVideoFinishV2ExternalExactSourceFormat",
                source.DisplayName,
                $"{summary.FpsNumerator}/{summary.FpsDenominator}",
                summary.FrameCount,
                summary.Width,
                summary.Height);
        }
        return VideoFinishV2Format(
            "UiVideoFinishV2ExternalSourceFormat",
            source.DisplayName,
            source.PlaybackDurationSeconds.ToString(
                "0.###",
                CultureInfo.CurrentCulture),
            Math.Max(0, source.PlaybackWidth),
            Math.Max(0, source.PlaybackHeight));
    }

    private static string FormatVideoFinishV2Bytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        return bytes >= mebibyte
            ? $"{bytes / mebibyte:0.##} MiB"
            : $"{bytes / 1024d:0.##} KiB";
    }

    private void InvalidateModalVideoFinishV2Readiness()
    {
        _videoFinishV2HealthGeneration++;
        _videoFinishV2HealthPending = false;
        _videoFinishV2WriterReady = false;
        _videoFinishV2ReadyContextStamp = null;
        if (ModalVideoFinishV2BoardVisible)
        {
            ModalVideoFinishV2ReadinessText.Text = VideoFinishV2Text(
                "UiVideoFinishV2ReadinessPending");
        }
    }

    private string BuildModalVideoFinishV2ContextStamp()
    {
        if (_videoFinishV2Source is not VideoEditV2SourceChoice source
            || _videoFinishV2Summary is not VideoEditV2SourceSummary summary
            || _videoFinishV2Plan is not VideoFinishV2Plan plan)
        {
            return "invalid";
        }
        return string.Join(
            ':',
            source.BaseSourceStamp,
            summary.FrameCount.ToString(CultureInfo.InvariantCulture),
            summary.FpsNumerator.ToString(CultureInfo.InvariantCulture),
            summary.FpsDenominator.ToString(CultureInfo.InvariantCulture),
            summary.DurationMs.ToString(CultureInfo.InvariantCulture),
            summary.Width.ToString(CultureInfo.InvariantCulture),
            summary.Height.ToString(CultureInfo.InvariantCulture),
            plan.Mode,
            plan.Scale.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshModalVideoFinishV2ActionControls()
    {
        if (ModalVideoFinishV2StartButton is null)
            return;
        bool planReady = _videoFinishV2Plan is not null
            && _videoFinishV2Summary is not null
            && (_videoFinishV2Source?.Managed == true
                || _videoFinishV2Probe is not null);
        string contextStamp = BuildModalVideoFinishV2ContextStamp();
        ModalVideoFinishV2ReadinessButton.IsEnabled = planReady
            && !_videoFinishV2ProbePending
            && !_videoFinishV2HealthPending
            && !_videoFinishV2RequestPending;
        ModalVideoFinishV2StartButton.IsEnabled = planReady
            && _videoFinishV2WriterReady
            && !_videoFinishV2ProbePending
            && !_videoFinishV2HealthPending
            && !_videoFinishV2RequestPending
            && string.Equals(
                _videoFinishV2ReadyContextStamp,
                contextStamp,
                StringComparison.Ordinal);
        ModalVideoFinishV2StartButton.Content = VideoFinishV2Text(
            ModalVideoFinishV2StartButton.IsEnabled
                ? "UiVideoFinishV2StartAction"
                : "UiVideoFinishV2StartPending");
        AutomationProperties.SetHelpText(
            ModalVideoFinishV2StartButton,
            VideoFinishV2Text("UiVideoFinishV2StartHelp"));
    }

    private async void ProbeModalVideoFinishV2_Click(
        object sender,
        RoutedEventArgs e)
        => await ProbeModalVideoFinishV2Async();

    private async Task<bool> ProbeModalVideoFinishV2Async()
    {
        if (_videoFinishV2Source is not VideoEditV2SourceChoice
            {
                Managed: false,
            } initial
            || _videoFinishV2ProbePending
            || _videoFinishV2RequestPending)
        {
            return false;
        }
        long generation = ++_videoFinishV2ProbeGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoFinishV2ProbeCts,
            cts);
        TryCancelModalVideoEditV2Token(prior);
        prior?.Dispose();
        _videoFinishV2ProbePending = true;
        _videoFinishV2Probe = null;
        _videoFinishV2Summary = null;
        InvalidateModalVideoFinishV2Readiness();
        ModalVideoFinishV2ProbeStatusText.Text = VideoFinishV2Text(
            "UiVideoFinishV2ProbeWorking");
        RefreshModalVideoFinishV2Presentation(invalidateReadiness: false);

        try
        {
            VideoEditV2ExplicitSourceCapture? capture =
                await CaptureModalVideoEditV2ExplicitSourceAsync(
                    initial,
                    cts.Token);
            if (capture is null
                || !IsModalVideoFinishV2ProbeCurrent(
                    generation,
                    initial.BaseSourceStamp))
            {
                SetModalVideoFinishV2ProbeStatus(
                    generation,
                    VideoFinishV2Text("UiVideoFinishV2ProbeFailed"));
                return false;
            }
            string requestJson = VideoEditV2TransientContract
                .BuildProbeRequestJson(capture.Selector);
            EnhancementApiResponse response = await SendEnhancementApiAsync(
                HttpMethod.Post,
                VideoEditV2TransientContract.Route,
                token: cts.Token,
                exactBodyJson: requestJson,
                maxResponseBytes: VideoEditV2TransientContract
                    .MaximumActionResponseBytes,
                timeoutError: VideoFinishV2Text(
                    "UiVideoFinishV2ActionTimedOut"));
            if (!IsModalVideoFinishV2ProbeCurrent(
                    generation,
                    initial.BaseSourceStamp)
                || !response.Ok
                || response.Payload is not JsonElement payload
                || !VideoEditV2TransientContract.TryParseProbeResponse(
                    payload,
                    out VideoEditV2SourceSummary summary)
                || capture.Source.ExternalSeam is not null
                    && !TryRevalidateExternalVideoSourceSeam(
                        capture.Source.ExternalSeam)
                || !TryCaptureDisplayedVideoFinishV2Source(out var current)
                || !string.Equals(
                    current.BaseSourceStamp,
                    initial.BaseSourceStamp,
                    StringComparison.Ordinal))
            {
                SetModalVideoFinishV2ProbeStatus(
                    generation,
                    VideoFinishV2Text("UiVideoFinishV2ProbeFailed"));
                return false;
            }
            _videoFinishV2Source = current;
            _videoFinishV2Probe = new(
                current.BaseSourceStamp,
                capture.Selector,
                summary);
            _videoFinishV2Summary = summary;
            ModalVideoFinishV2ProbeStatusText.Text = VideoFinishV2Text(
                "UiVideoFinishV2ProbeReady");
            RefreshModalVideoFinishV2Presentation(invalidateReadiness: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetModalVideoFinishV2ProbeStatus(
                generation,
                VideoFinishV2Text("UiVideoFinishV2ActionCanceled"));
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
            SetModalVideoFinishV2ProbeStatus(
                generation,
                VideoFinishV2Text("UiVideoFinishV2ProbeFailed"));
            return false;
        }
        finally
        {
            if (generation == _videoFinishV2ProbeGeneration)
            {
                _videoFinishV2ProbePending = false;
                if (ReferenceEquals(_videoFinishV2ProbeCts, cts))
                    _videoFinishV2ProbeCts = null;
                RefreshModalVideoFinishV2ActionControls();
            }
            cts.Dispose();
        }
    }

    private bool IsModalVideoFinishV2ProbeCurrent(
        long generation,
        string baseSourceStamp)
        => generation == _videoFinishV2ProbeGeneration
            && _videoFinishV2ProbePending
            && ModalVideoFinishV2BoardVisible
            && TryCaptureDisplayedVideoFinishV2Source(out var current)
            && string.Equals(
                current.BaseSourceStamp,
                baseSourceStamp,
                StringComparison.Ordinal);

    private void SetModalVideoFinishV2ProbeStatus(
        long generation,
        string value)
    {
        if (generation == _videoFinishV2ProbeGeneration
            && ModalVideoFinishV2BoardVisible)
        {
            ModalVideoFinishV2ProbeStatusText.Text = value;
        }
    }

    private async void RefreshModalVideoFinishV2Readiness_Click(
        object sender,
        RoutedEventArgs e)
        => await RefreshModalVideoFinishV2CapabilityAsync();

    private async Task<bool> RefreshModalVideoFinishV2CapabilityAsync()
    {
        if (_videoFinishV2Plan is not VideoFinishV2Plan plan
            || _videoFinishV2Source is not VideoEditV2SourceChoice source
            || _videoFinishV2HealthPending
            || _videoFinishV2RequestPending)
        {
            return false;
        }
        string contextStamp = BuildModalVideoFinishV2ContextStamp();
        long generation = ++_videoFinishV2HealthGeneration;
        _videoFinishV2HealthPending = true;
        _videoFinishV2WriterReady = false;
        _videoFinishV2ReadyContextStamp = null;
        ModalVideoFinishV2ReadinessText.Text = VideoFinishV2Text(
            "UiVideoFinishV2ReadinessChecking");
        RefreshModalVideoFinishV2ActionControls();
        try
        {
            EnhancementApiResponse response =
                await SendPassiveEnhancementReadAsync("api/enhance/health");
            if (generation != _videoFinishV2HealthGeneration
                || !ModalVideoFinishV2BoardVisible
                || !ReferenceEquals(_videoFinishV2Plan, plan)
                || !ReferenceEquals(_videoFinishV2Source, source)
                || !string.Equals(
                    contextStamp,
                    BuildModalVideoFinishV2ContextStamp(),
                    StringComparison.Ordinal))
            {
                return false;
            }
            long? knownBytes = TryCaptureModalVideoFinishV2SourceBytes(
                    source,
                    out long exactSourceBytes)
                ? exactSourceBytes
                : null;
            _videoFinishV2WriterReady = response.Ok
                && response.Payload is JsonElement payload
                && VideoFinishV2Contract.IsExactReadyHealth(
                    payload,
                    plan.Mode,
                    plan,
                    knownBytes);
            _videoFinishV2ReadyContextStamp = _videoFinishV2WriterReady
                ? contextStamp
                : null;
            ModalVideoFinishV2ReadinessText.Text = VideoFinishV2Text(
                _videoFinishV2WriterReady
                    ? "UiVideoFinishV2ReadinessReady"
                    : "UiVideoFinishV2ReadinessUnavailable");
            return _videoFinishV2WriterReady;
        }
        finally
        {
            if (generation == _videoFinishV2HealthGeneration)
            {
                _videoFinishV2HealthPending = false;
                RefreshModalVideoFinishV2ActionControls();
            }
        }
    }

    private bool TryCaptureModalVideoFinishV2SourceBytes(
        VideoEditV2SourceChoice source,
        out long sourceBytes)
    {
        sourceBytes = 0;
        if (!source.Managed)
        {
            if (source.ExternalSeam is not { Length: > 0 } seam
                || !TryRevalidateExternalVideoSourceSeam(seam))
            {
                return false;
            }
            sourceBytes = seam.Length;
            return sourceBytes <= VideoFinishV2Contract.MaximumSourceBytes;
        }

        if (!TryCapturePassiveDisplayedManagedVideoEditV2Source(
                out ManagedVideoVersion managed)
            || !string.Equals(
                managed.JobId,
                source.SourceVideoJobId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(_modalSourceTilePath)
            || !TryResolveEnhancementSourceIdentity(
                _modalSourceTilePath,
                out string sourceId)
            || !TryCaptureVideoSourceStamp(
                new VideoSourceChoice(
                    sourceId,
                    managed.Output.OutputPath,
                    managed.JobId,
                    "Video Finish v2 readiness source",
                    UsesDisplayedFileDirectly: true),
                out VideoH3SourceStamp stamp))
        {
            return false;
        }
        sourceBytes = stamp.Length;
        return sourceBytes is > 0
            and <= VideoFinishV2Contract.MaximumSourceBytes;
    }

    private async void StartModalVideoFinishV2_Click(
        object sender,
        RoutedEventArgs e)
        => await StartModalVideoFinishV2Async();

    private bool CanStartModalVideoFinishV2()
        => _videoFinishV2WriterReady
            && !_videoFinishV2ProbePending
            && !_videoFinishV2HealthPending
            && !_videoFinishV2RequestPending
            && _videoFinishV2Source is not null
            && _videoFinishV2Summary is not null
            && _videoFinishV2Plan is not null
            && string.Equals(
                _videoFinishV2ReadyContextStamp,
                BuildModalVideoFinishV2ContextStamp(),
                StringComparison.Ordinal);

    private async Task<bool> StartModalVideoFinishV2Async()
    {
        _videoFinishV2StartAttemptCount++;
        if (!CanStartModalVideoFinishV2())
            return false;

        long generation = ++_videoFinishV2ActionGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoFinishV2StartCts,
            cts);
        TryCancelModalVideoEditV2Token(prior);
        prior?.Dispose();
        _videoFinishV2RequestPending = true;
        string? pendingDeliveryRequestId = null;
        string? actionStatus = null;
        ModalVideoFinishV2ReadinessText.Text = VideoFinishV2Text(
            "UiVideoFinishV2Preparing");
        RefreshModalVideoFinishV2ActionControls();

        try
        {
            VideoFinishV2ActionContext? context =
                await CaptureModalVideoFinishV2DurableContextAsync(
                    generation,
                    cts.Token);
            if (context is null)
            {
                actionStatus = VideoFinishV2Text(
                    "UiVideoFinishV2StartStale");
                return false;
            }

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                context.RequestBody,
                includeQueuePlacementInBody: false,
                token: cts.Token,
                healthValidator: payload =>
                    VideoFinishV2Contract.IsExactReadyHealth(
                        payload,
                        context.Plan.Mode,
                        context.Plan,
                        context.SourceStamp.Length)
                        ? null
                        : VideoFinishV2Text(
                            "UiVideoFinishV2ReadinessUnavailable"),
                requireExactHealthValidation: true,
                recoverySourceIdentity: context.SourceId,
                prePublishValidator: () =>
                    ValidateModalVideoFinishV2BeforePublish(context),
                asyncPrePublishValidator: token =>
                    ValidateModalVideoFinishV2BeforePublishAsync(
                        context,
                        token),
                onBeforeDurablePublish: item =>
                {
                    IDisposable publishLease = AcquireVideoDurablePublishLease(
                        () => PinVideoSourceForDurablePublish(
                            context.SourceStamp));
                    try
                    {
                        pendingDeliveryRequestId = item.RequestId;
                        RecordPendingVideoSourceDependency(
                            item.RequestId,
                            context.DependencySource);
                        UpdateModalDisplayedDeletePresentation();
                        return publishLease;
                    }
                    catch
                    {
                        publishLease.Dispose();
                        throw;
                    }
                });
            bool hasJob = response.Ok
                && response.Payload is JsonElement payload
                && payload.TryGetProperty("job", out JsonElement job)
                && job.ValueKind == JsonValueKind.Object;
            if (response.SavedForDelivery || hasJob)
            {
                RecordActiveVideoSourceDependency(context.DependencySource);
                UpdateModalDisplayedDeletePresentation();
                string message = VideoFinishV2Text(
                    response.SavedForDelivery
                        ? "UiVideoFinishV2SavedForDelivery"
                        : "UiVideoFinishV2Queued");
                SetTransientStatusToast(message);
                if (generation == _videoFinishV2ActionGeneration
                    && ModalVideoFinishV2BoardVisible)
                {
                    ModalVideoFinishV2ReadinessText.Text = message;
                    CloseModalVideoFinishV2Board(
                        restoreFocus: true,
                        stale: false);
                }
                QueueEnhancedStateRefreshIfChanged();
                return true;
            }

            _videoFinishV2WriterReady = false;
            _videoFinishV2ReadyContextStamp = null;
            actionStatus = string.IsNullOrWhiteSpace(response.Error)
                ? VideoFinishV2Text(
                    "UiVideoFinishV2ReadinessUnavailable")
                : response.Error;
            return false;
        }
        catch (OperationCanceledException)
        {
            actionStatus = VideoFinishV2Text(
                "UiVideoFinishV2ActionCanceled");
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
            actionStatus = VideoFinishV2Text("UiVideoFinishV2StartStale");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingDeliveryRequestId))
                _pendingVideoSourceDependencies.Remove(pendingDeliveryRequestId);
            if (generation == _videoFinishV2ActionGeneration)
            {
                _videoFinishV2RequestPending = false;
                if (ReferenceEquals(_videoFinishV2StartCts, cts))
                    _videoFinishV2StartCts = null;
                if (ModalVideoFinishV2BoardVisible
                    && !string.IsNullOrWhiteSpace(actionStatus))
                {
                    ModalVideoFinishV2ReadinessText.Text = actionStatus;
                }
                RefreshModalVideoFinishV2ActionControls();
                UpdateModalDisplayedDeletePresentation();
            }
            cts.Dispose();
        }
    }

    private async Task<VideoFinishV2ActionContext?>
        CaptureModalVideoFinishV2DurableContextAsync(
            long generation,
            CancellationToken token)
    {
        if (generation != _videoFinishV2ActionGeneration
            || !_videoFinishV2RequestPending
            || _videoFinishV2Source is not VideoEditV2SourceChoice source
            || _videoFinishV2Summary is not VideoEditV2SourceSummary summary
            || _videoFinishV2Plan is not VideoFinishV2Plan plan
            || !string.Equals(
                _videoFinishV2ReadyContextStamp,
                BuildModalVideoFinishV2ContextStamp(),
                StringComparison.Ordinal))
        {
            return null;
        }
        VideoEditV2ExplicitSourceCapture? capture =
            await CaptureModalVideoEditV2ExplicitSourceAsync(source, token);
        if (capture is null
            || !source.Managed
                && (_videoFinishV2Probe is not VideoFinishV2ProbeState probe
                    || !VideoEditV2TransientContract.SameSource(
                        probe.Selector,
                        capture.Selector))
            || !TryCreateModalVideoFinishV2Dependency(
                capture,
                out string sourceId,
                out VideoSourceChoice dependency)
            || !TryCaptureVideoSourceStamp(
                dependency,
                out VideoH3SourceStamp sourceStamp)
            || sourceStamp.Length is <= 0
                or > VideoFinishV2Contract.MaximumSourceBytes
            || !VideoFinishV2Contract.TryBuildFinishRequest(
                sourceId,
                capture.Selector,
                plan,
                out JsonElement request))
        {
            return null;
        }
        return new(
            generation,
            capture.Source,
            capture.Selector,
            summary,
            plan,
            sourceId,
            dependency,
            sourceStamp,
            request);
    }

    private bool TryCreateModalVideoFinishV2Dependency(
        VideoEditV2ExplicitSourceCapture capture,
        out string sourceId,
        out VideoSourceChoice dependency)
    {
        sourceId = "";
        dependency = null!;
        if (!capture.Source.Managed)
        {
            if (capture.Selector.Path is not string path)
                return false;
            sourceId = path;
            dependency = new(
                sourceId,
                path,
                ProducerJobId: null,
                "Video Finish v2 displayed source",
                UsesDisplayedFileDirectly: true);
            return true;
        }

        if (capture.Selector.SourceVideoJobId is not string producerJobId
            || !TryCapturePassiveDisplayedManagedVideoEditV2Source(
                out ManagedVideoVersion managed)
            || !string.Equals(
                managed.JobId,
                producerJobId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(_modalSourceTilePath)
            || !TryResolveEnhancementSourceIdentity(
                _modalSourceTilePath,
                out sourceId))
        {
            return false;
        }
        dependency = new(
            sourceId,
            managed.Output.OutputPath,
            producerJobId,
            "Video Finish v2 managed source",
            UsesDisplayedFileDirectly: true);
        return true;
    }

    private string? ValidateModalVideoFinishV2BeforePublish(
        VideoFinishV2ActionContext context)
    {
        string stale = VideoFinishV2Text("UiVideoFinishV2StartStale");
        if (context.Generation != _videoFinishV2ActionGeneration
            || !_videoFinishV2RequestPending
            || !ModalVideoFinishV2BoardVisible
            || _videoFinishV2Plan != context.Plan
            || _videoFinishV2Summary != context.Summary
            || !string.Equals(
                _videoFinishV2ReadyContextStamp,
                BuildModalVideoFinishV2ContextStamp(),
                StringComparison.Ordinal)
            || !TryCaptureDisplayedVideoFinishV2Source(out var current)
            || !string.Equals(
                current.BaseSourceStamp,
                context.Source.BaseSourceStamp,
                StringComparison.Ordinal)
            || context.Source.ExternalSeam is not null
                && !TryRevalidateExternalVideoSourceSeam(
                    context.Source.ExternalSeam)
            || !TryCaptureVideoSourceStamp(
                context.DependencySource,
                out VideoH3SourceStamp currentStamp)
            || !VideoH3SourceStampsEqual(
                currentStamp,
                context.SourceStamp)
            || !VideoFinishV2Contract.TryBuildFinishRequest(
                context.SourceId,
                context.Selector,
                context.Plan,
                out JsonElement currentRequest)
            || !string.Equals(
                currentRequest.GetRawText(),
                context.RequestBody.GetRawText(),
                StringComparison.Ordinal))
        {
            return stale;
        }
        return null;
    }

    private async Task<string?> ValidateModalVideoFinishV2BeforePublishAsync(
        VideoFinishV2ActionContext context,
        CancellationToken token)
    {
        string stale = VideoFinishV2Text("UiVideoFinishV2StartStale");
        if (ValidateModalVideoFinishV2BeforePublish(context) is not null)
            return stale;
        VideoEditV2ExplicitSourceCapture? capture =
            await CaptureModalVideoEditV2ExplicitSourceAsync(
                context.Source,
                token);
        if (capture is null
            || !VideoEditV2TransientContract.SameSource(
                capture.Selector,
                context.Selector))
        {
            return stale;
        }

        VideoEditV2SourceSummary refreshedSummary;
        if (context.Source.Managed)
        {
            if (!TryCaptureDisplayedVideoFinishV2Source(out var current)
                || !TryBuildVideoFinishV2Summary(
                    current,
                    out refreshedSummary))
            {
                return stale;
            }
        }
        else
        {
            string probeJson = VideoEditV2TransientContract
                .BuildProbeRequestJson(capture.Selector);
            EnhancementApiResponse probeResponse =
                await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    VideoEditV2TransientContract.Route,
                    token: token,
                    exactBodyJson: probeJson,
                    maxResponseBytes: VideoEditV2TransientContract
                        .MaximumActionResponseBytes,
                    timeoutError: VideoFinishV2Text(
                        "UiVideoFinishV2ActionTimedOut"));
            if (!probeResponse.Ok
                || probeResponse.Payload is not JsonElement payload
                || !VideoEditV2TransientContract.TryParseProbeResponse(
                    payload,
                    out refreshedSummary))
            {
                return stale;
            }
        }
        if (refreshedSummary != context.Summary
            || !VideoFinishV2Contract.TryPlan(
                refreshedSummary,
                context.Plan.Mode,
                context.Plan.Scale,
                out VideoFinishV2Plan refreshedPlan,
                out _)
            || refreshedPlan != context.Plan
            || !VideoFinishV2Contract.TryBuildFinishRequest(
                context.SourceId,
                capture.Selector,
                refreshedPlan,
                out JsonElement refreshedRequest)
            || !string.Equals(
                refreshedRequest.GetRawText(),
                context.RequestBody.GetRawText(),
                StringComparison.Ordinal)
            || ValidateModalVideoFinishV2BeforePublish(context) is not null)
        {
            return stale;
        }
        return null;
    }

    internal static bool TryPlanVideoFinishV2ForSmoke(
        int frameCount,
        int fpsNumerator,
        int fpsDenominator,
        int durationMs,
        int width,
        int height,
        string mode,
        int scale,
        out VideoFinishV2Plan plan,
        out VideoFinishV2PlanError error)
        => VideoFinishV2Contract.TryPlan(
            new(
                frameCount,
                fpsNumerator,
                fpsDenominator,
                durationMs,
                width,
                height),
            mode,
            scale,
            out plan,
            out error);

    public void ConfigureVideoFinishV2ManagedSourceForSmoke(
        string sourceVideoJobId,
        int fps,
        int frameCount,
        int durationMs,
        int width,
        int height)
    {
        string stamp = $"managed-smoke:{sourceVideoJobId}";
        _videoFinishV2SourceForSmoke = new(
            stamp,
            stamp,
            "managed-smoke.mp4",
            Managed: true,
            ExactTimeline: true,
            fps,
            1,
            frameCount,
            durationMs / 1_000d,
            width,
            height,
            sourceVideoJobId,
            ExternalSeam: null);
        SyncModalVideoFinishV2EntryPresentation();
    }

    public bool VideoFinishV2EntryVisibleForSmoke
    {
        get
        {
            SyncModalVideoFinishV2EntryPresentation();
            return ModalVideoFinishButton?.Visibility == Visibility.Visible
                && ModalContextVideoFinishV2?.Visibility == Visibility.Visible;
        }
    }

    public bool OpenVideoFinishV2ForSmoke()
    {
        OpenModalVideoFinishV2Board();
        return ModalVideoFinishV2BoardVisible;
    }

    public bool InvokeVideoFinishProductionEntryForSmoke()
    {
        OpenModalVideoFinish_Click(
            ModalVideoFinishButton,
            new RoutedEventArgs(
                Button.ClickEvent,
                ModalVideoFinishButton));
        return ModalVideoFinishV2BoardVisible
            && ModalVideoToolsPopup?.Visibility != Visibility.Visible;
    }

    public bool VideoFinishV2BoardVisibleForSmoke
        => ModalVideoFinishV2BoardVisible;

    public bool VideoFinishV2ProbeVisibleForSmoke
        => ModalVideoFinishV2ProbeButton?.Visibility == Visibility.Visible;

    public bool VideoFinishV2StartEnabledForSmoke
        => ModalVideoFinishV2StartButton?.IsEnabled == true;

    public bool VideoFinishV2ReadinessEnabledForSmoke
        => ModalVideoFinishV2ReadinessButton?.IsEnabled == true;

    public string VideoFinishV2ModeForSmoke
        => ReadModalVideoFinishV2Mode();

    public int VideoFinishV2ScaleForSmoke
        => ReadModalVideoFinishV2Scale();

    public string VideoFinishV2PlanForSmoke
        => ModalVideoFinishV2PlanText?.Text ?? "";

    public string VideoFinishV2PolicyForSmoke
        => ModalVideoFinishV2PolicyText?.Text ?? "";

    public string VideoFinishV2EstimateForSmoke
        => ModalVideoFinishV2EstimateText?.Text ?? "";

    public int VideoFinishV2StartAttemptCountForSmoke
        => _videoFinishV2StartAttemptCount;

    public bool VideoFinishV2ExternalContextEntryForSmoke
        => ModalVideoEditV2ExternalContextMenu?.Items
            .OfType<MenuItem>()
            .Any(item => string.Equals(
                AutomationProperties.GetName(item),
                VideoFinishV2Text("UiVideoFinishV2ActionAutomation"),
                StringComparison.Ordinal)) == true;

    public Task<bool> ProbeVideoFinishV2ForSmokeAsync()
        => ProbeModalVideoFinishV2Async();

    public Task<bool> RefreshVideoFinishV2ReadinessForSmokeAsync()
        => RefreshModalVideoFinishV2CapabilityAsync();

    public Task<bool> StartVideoFinishV2ForSmokeAsync()
        => StartModalVideoFinishV2Async();

    public bool SetVideoFinishV2ModeAndScaleForSmoke(
        string mode,
        int scale)
    {
        ComboBoxItem? modeItem = ModalVideoFinishV2ModeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                mode,
                StringComparison.Ordinal));
        ComboBoxItem? scaleItem = ModalVideoFinishV2ScaleComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                scale.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        if (modeItem is null || scaleItem is null)
            return false;
        ModalVideoFinishV2ModeComboBox.SelectedItem = modeItem;
        ModalVideoFinishV2ScaleComboBox.SelectedItem = scaleItem;
        RefreshModalVideoFinishV2Presentation(invalidateReadiness: true);
        return _videoFinishV2Plan is not null;
    }

    public bool CloseVideoFinishV2ForSmoke(bool stale)
    {
        CloseModalVideoFinishV2Board(restoreFocus: false, stale);
        return !ModalVideoFinishV2BoardVisible
            && _videoFinishV2LastCloseWasStale == stale;
    }

    public bool InvokeVideoFinishV2EscapeForSmoke()
    {
        if (!ModalVideoFinishV2BoardVisible)
            return false;
        return InvokePreviewKeyForSmoke(Key.Escape)
            && !ModalVideoFinishV2BoardVisible
            && _videoFinishV2LastCloseWasStale
            && Modal.Visibility == Visibility.Visible;
    }
}

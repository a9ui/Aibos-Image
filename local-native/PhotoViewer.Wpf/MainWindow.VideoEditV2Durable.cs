using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private sealed record VideoEditV2DurableActionContext(
        long Generation,
        VideoEditV2SourceChoice Source,
        VideoEditV2SourceSelector Selector,
        VideoEditV2SelectionPlan Selection,
        VideoEditV2PreviewSet Previews,
        VideoEditV2CompiledCandidate Candidate,
        string InstructionJa,
        VideoEditV2DurableSettings Settings,
        string SourceId,
        VideoSourceChoice DependencySource,
        VideoH3SourceStamp SourceStamp,
        JsonElement RequestBody);

    private CancellationTokenSource? _videoEditV2StartCts;
    private long _videoEditV2HealthGeneration;
    private long _videoEditV2ActionGeneration;
    private bool _videoEditV2WriterReady;
    private bool _videoEditV2RequestPending;

    private void ResetModalVideoEditV2DurableState()
    {
        _videoEditV2HealthGeneration++;
        _videoEditV2ActionGeneration++;
        _videoEditV2WriterReady = false;
        _videoEditV2RequestPending = false;
        CancellationTokenSource? active = Interlocked.Exchange(
            ref _videoEditV2StartCts,
            null);
        TryCancelModalVideoEditV2Token(active);
    }

    private void CancelModalVideoEditV2DurableActions()
        => ResetModalVideoEditV2DurableState();

    private bool CanStartModalVideoEditV2Durably()
        => _videoEditV2WriterReady
            && !_videoEditV2RequestPending
            && !_videoEditV2LoadPending
            && !_videoEditV2CompilePending
            && _videoEditV2CandidateApproved
            && !_videoEditV2CandidateStale
            && _videoEditV2Candidate is not null
            && _videoEditV2Source?.ExactTimeline == true
            && _videoEditV2Plan is not null
            && HasFreshModalVideoEditV2PreviewsForPlan()
            && string.Equals(
                _videoEditV2Candidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal)
            && TryReadModalVideoEditV2DurableSettings(out _);

    private bool TryReadModalVideoEditV2DurableSettings(
        out VideoEditV2DurableSettings settings)
    {
        settings = null!;
        string? audioPolicy =
            (ModalVideoEditV2AudioComboBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString();
        string? strengthTag =
            (ModalVideoEditV2StrengthComboBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString();
        int strength = strengthTag switch
        {
            "light" => 30,
            "balanced" => 60,
            "strong" => 90,
            _ => 0,
        };
        if (!int.TryParse(
                (ModalVideoEditV2CanvasComboBox.SelectedItem as ComboBoxItem)
                    ?.Tag?.ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int maximumPixelArea)
            || !int.TryParse(
                ModalVideoEditV2StepsTextBox.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int steps))
        {
            return false;
        }
        var parsed = new VideoEditV2DurableSettings(
            audioPolicy ?? "",
            steps,
            strength,
            maximumPixelArea);
        if (!VideoEditV2DurableContract.TryValidateSettings(parsed))
            return false;
        settings = parsed;
        return true;
    }

    private async Task<bool> RefreshModalVideoEditV2WriterCapabilityAsync()
    {
        long generation = ++_videoEditV2HealthGeneration;
        _videoEditV2WriterReady = false;
        if (ModalVideoEditV2BoardVisible)
        {
            ModalVideoEditV2ReadinessText.Text =
                VideoEditV2Text("UiVideoEditV2WriterChecking");
            RefreshModalVideoEditV2ActionControls();
        }

        EnhancementApiResponse response =
            await SendPassiveEnhancementReadAsync("api/enhance/health");
        if (generation != _videoEditV2HealthGeneration
            || !ModalVideoEditV2BoardVisible)
        {
            return false;
        }

        _videoEditV2WriterReady = response.Ok
            && response.Payload is JsonElement payload
            && VideoEditV2DurableContract.IsExactReadyHealth(payload);
        ModalVideoEditV2ReadinessText.Text = VideoEditV2Text(
            _videoEditV2WriterReady
                ? "UiVideoEditV2WriterReady"
                : "UiVideoEditV2WriterPending");
        RefreshModalVideoEditV2ActionControls();
        return _videoEditV2WriterReady;
    }

    private string? ValidateExactVideoEditV2Health(JsonElement payload)
        => VideoEditV2DurableContract.IsExactReadyHealth(payload)
            ? null
            : VideoEditV2Text("UiVideoEditV2WriterPending");

    private async Task<bool> HandleModalVideoEditV2SkipReviewAsync(
        VideoEditV2CompiledCandidate expectedCandidate)
    {
        bool ready = await RefreshModalVideoEditV2WriterCapabilityAsync();
        if (!ready
            || !ModalVideoEditV2BoardVisible
            || !ReferenceEquals(_videoEditV2Candidate, expectedCandidate)
            || _videoEditV2CandidateStale
            || !HasFreshModalVideoEditV2PreviewsForPlan()
            || !string.Equals(
                expectedCandidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal))
        {
            return false;
        }

        _videoEditV2CandidateApproved = true;
        ModalVideoEditV2ReviewPanel.Visibility = Visibility.Collapsed;
        ModalVideoEditV2CompileStatusText.Text =
            VideoEditV2Text("UiVideoEditV2CandidateApproved");
        RefreshModalVideoEditV2ActionControls();
        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
                await StartModalVideoEditV2Async(
                    skipReviewAuthorization: true)),
            DispatcherPriority.Background);
        return true;
    }

    private async void StartModalVideoEditV2_Click(
        object sender,
        RoutedEventArgs e)
        => await StartModalVideoEditV2Async(
            skipReviewAuthorization: false);

    private async Task<bool> StartModalVideoEditV2Async(
        bool skipReviewAuthorization = false)
    {
        _videoEditV2StartAttemptCount++;
        if (!CanStartModalVideoEditV2Durably())
        {
            if (ModalVideoEditV2BoardVisible)
            {
                ModalVideoEditV2ReadinessText.Text =
                    VideoEditV2Text("UiVideoEditV2WriterPending");
                RefreshModalVideoEditV2ActionControls();
            }
            return false;
        }

        long generation = ++_videoEditV2ActionGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? prior = Interlocked.Exchange(
            ref _videoEditV2StartCts,
            cts);
        TryCancelModalVideoEditV2Token(prior);
        prior?.Dispose();
        _videoEditV2RequestPending = true;
        string? pendingDeliveryRequestId = null;
        string? actionStatus = null;
        ModalVideoEditV2ReadinessText.Text =
            VideoEditV2Text("UiVideoEditV2Preparing");
        RefreshModalVideoEditV2ActionControls();

        try
        {
            VideoEditV2DurableActionContext? context =
                await CaptureModalVideoEditV2DurableContextAsync(
                    generation,
                    cts.Token);
            if (context is null)
            {
                actionStatus = VideoEditV2Text("UiVideoEditV2StartStale");
                return false;
            }

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                context.RequestBody,
                includeQueuePlacementInBody: false,
                token: cts.Token,
                healthValidator: ValidateExactVideoEditV2Health,
                requireExactHealthValidation: true,
                recoverySourceIdentity: context.SourceId,
                prePublishValidator: () =>
                    ValidateModalVideoEditV2BeforePublish(context),
                asyncPrePublishValidator: token =>
                    ValidateModalVideoEditV2BeforePublishAsync(
                        context,
                        token),
                onBeforeDurablePublish: item =>
                {
                    IDisposable publishLease =
                        AcquireVideoDurablePublishLease(
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
                _ = ShowVideoEditStartAcceptedNotification(
                    skipReviewAuthorization,
                    acceptedOrSaved: true);
                RecordActiveVideoSourceDependency(context.DependencySource);
                UpdateModalDisplayedDeletePresentation();
                string message = VideoEditV2Text(
                    response.SavedForDelivery
                        ? "UiVideoEditV2SavedForDelivery"
                        : "UiVideoEditV2Queued");
                SetTransientStatusToast(message);
                if (generation == _videoEditV2ActionGeneration
                    && ModalVideoEditV2BoardVisible)
                {
                    ModalVideoEditV2ReadinessText.Text = message;
                    CloseModalVideoEditV2Board(
                        restoreFocus: true,
                        stale: false);
                }
                QueueEnhancedStateRefreshIfChanged();
                return true;
            }

            _videoEditV2WriterReady = false;
            actionStatus = string.IsNullOrWhiteSpace(response.Error)
                ? VideoEditV2Text("UiVideoEditV2WriterPending")
                : response.Error;
            return false;
        }
        catch (OperationCanceledException)
        {
            actionStatus = VideoEditV2Text("UiVideoEditV2ActionCanceled");
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
            actionStatus = VideoEditV2Text("UiVideoEditV2StartStale");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingDeliveryRequestId))
            {
                _pendingVideoSourceDependencies.Remove(
                    pendingDeliveryRequestId);
            }
            if (generation == _videoEditV2ActionGeneration)
            {
                _videoEditV2RequestPending = false;
                if (ReferenceEquals(_videoEditV2StartCts, cts))
                    _videoEditV2StartCts = null;
                if (ModalVideoEditV2BoardVisible
                    && !string.IsNullOrWhiteSpace(actionStatus))
                {
                    ModalVideoEditV2ReadinessText.Text = actionStatus;
                }
                RefreshModalVideoEditV2ActionControls();
                UpdateModalDisplayedDeletePresentation();
            }
            cts.Dispose();
        }
    }

    private async Task<VideoEditV2DurableActionContext?>
        CaptureModalVideoEditV2DurableContextAsync(
            long generation,
            CancellationToken token)
    {
        if (generation != _videoEditV2ActionGeneration
            || !_videoEditV2RequestPending
            || _videoEditV2Source is not VideoEditV2SourceChoice source
            || _videoEditV2Plan is not VideoEditV2SelectionPlan selection
            || _videoEditV2PreviewSet is not VideoEditV2PreviewSet previews
            || _videoEditV2Candidate is not VideoEditV2CompiledCandidate candidate
            || !_videoEditV2CandidateApproved
            || _videoEditV2CandidateStale
            || !HasFreshModalVideoEditV2PreviewsForPlan()
            || !TryReadModalVideoEditV2DurableSettings(
                out VideoEditV2DurableSettings settings))
        {
            return null;
        }

        string instruction = ModalVideoEditV2InstructionTextBox.Text.Trim();
        if (!string.Equals(
                candidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal))
        {
            return null;
        }
        VideoEditV2ExplicitSourceCapture? capture =
            await CaptureModalVideoEditV2ExplicitSourceAsync(source, token);
        if (capture is null
            || !VideoEditV2TransientContract.SameSource(
                capture.Selector,
                previews.Source)
            || !TryCreateModalVideoEditV2Dependency(
                capture,
                out string sourceId,
                out VideoSourceChoice dependencySource)
            || !TryCaptureVideoSourceStamp(
                dependencySource,
                out VideoH3SourceStamp sourceStamp)
            || !VideoEditV2DurableContract.TryBuildEditRequest(
                sourceId,
                capture.Selector,
                selection,
                previews.Previews,
                instruction,
                candidate,
                settings,
                out JsonElement requestBody))
        {
            return null;
        }

        return new VideoEditV2DurableActionContext(
            generation,
            capture.Source,
            capture.Selector,
            selection,
            previews,
            candidate,
            instruction,
            settings,
            sourceId,
            dependencySource,
            sourceStamp,
            requestBody);
    }

    private bool TryCreateModalVideoEditV2Dependency(
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
            dependency = new VideoSourceChoice(
                sourceId,
                path,
                ProducerJobId: null,
                "Video Edit v2 displayed source",
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
        dependency = new VideoSourceChoice(
            sourceId,
            managed.Output.OutputPath,
            producerJobId,
            "Video Edit v2 managed source",
            UsesDisplayedFileDirectly: true);
        return true;
    }

    private string? ValidateModalVideoEditV2BeforePublish(
        VideoEditV2DurableActionContext context)
    {
        string stale = VideoEditV2Text("UiVideoEditV2StartStale");
        if (context.Generation != _videoEditV2ActionGeneration
            || !_videoEditV2RequestPending
            || !ModalVideoEditV2BoardVisible
            || !ReferenceEquals(_videoEditV2Candidate, context.Candidate)
            || !ReferenceEquals(_videoEditV2PreviewSet, context.Previews)
            || _videoEditV2CandidateStale
            || !_videoEditV2CandidateApproved
            || _videoEditV2Plan != context.Selection
            || !HasFreshModalVideoEditV2PreviewsForPlan()
            || !string.Equals(
                context.Candidate.ContextStamp,
                BuildModalVideoEditV2ContextStamp(),
                StringComparison.Ordinal)
            || !TryReadModalVideoEditV2DurableSettings(out var settings)
            || settings != context.Settings
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || !string.Equals(
                current.SourceStamp,
                context.Source.SourceStamp,
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
            || !VideoEditV2DurableContract.TryBuildEditRequest(
                context.SourceId,
                context.Selector,
                context.Selection,
                context.Previews.Previews,
                context.InstructionJa,
                context.Candidate,
                context.Settings,
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

    private async Task<string?> ValidateModalVideoEditV2BeforePublishAsync(
        VideoEditV2DurableActionContext context,
        CancellationToken token)
    {
        string stale = VideoEditV2Text("UiVideoEditV2StartStale");
        if (ValidateModalVideoEditV2BeforePublish(context) is not null)
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

        string probeJson = VideoEditV2TransientContract
            .BuildProbeRequestJson(capture.Selector);
        EnhancementApiResponse probeResponse = await SendEnhancementApiAsync(
            HttpMethod.Post,
            VideoEditV2TransientContract.Route,
            token: token,
            exactBodyJson: probeJson,
            maxResponseBytes: VideoEditV2TransientContract
                .MaximumActionResponseBytes,
            timeoutError: VideoEditV2Text("UiVideoEditV2ActionTimedOut"));
        if (!probeResponse.Ok
            || probeResponse.Payload is not JsonElement probePayload
            || !VideoEditV2TransientContract.TryParseProbeResponse(
                probePayload,
                out VideoEditV2SourceSummary summary)
            || summary != context.Previews.Summary)
        {
            return stale;
        }

        string previewJson = VideoEditV2TransientContract
            .BuildPreviewRequestJson(
                capture.Selector,
                context.Selection);
        EnhancementApiResponse previewResponse = await SendEnhancementApiAsync(
            HttpMethod.Post,
            VideoEditV2TransientContract.Route,
            token: token,
            exactBodyJson: previewJson,
            maxResponseBytes: VideoEditV2TransientContract
                .MaximumPreviewResponseBytes,
            timeoutError: VideoEditV2Text("UiVideoEditV2ActionTimedOut"));
        if (!previewResponse.Ok
            || previewResponse.Payload is not JsonElement previewPayload
            || !VideoEditV2TransientContract.TryParsePreviewResponse(
                previewPayload,
                summary,
                context.Selection,
                out VideoEditV2PreviewSet? refreshed,
                capture.Selector,
                context.Source.SourceStamp)
            || refreshed is null
            || !VideoEditV2DurableContract.TryBuildEditRequest(
                context.SourceId,
                capture.Selector,
                context.Selection,
                refreshed.Previews,
                context.InstructionJa,
                context.Candidate,
                context.Settings,
                out JsonElement refreshedRequest)
            || !string.Equals(
                refreshedRequest.GetRawText(),
                context.RequestBody.GetRawText(),
                StringComparison.Ordinal)
            || ValidateModalVideoEditV2BeforePublish(context) is not null)
        {
            return stale;
        }
        return null;
    }

    internal static bool TryBuildVideoEditV2DurableRequestForSmoke(
        string sourceId,
        VideoEditV2SourceSelector source,
        VideoEditV2SelectionPlan selection,
        IReadOnlyList<VideoEditV2PreviewPayload> previews,
        string instructionJa,
        VideoEditV2CompiledCandidate compiled,
        VideoEditV2DurableSettings settings,
        out JsonElement request)
        => VideoEditV2DurableContract.TryBuildEditRequest(
            sourceId,
            source,
            selection,
            previews,
            instructionJa,
            compiled,
            settings,
            out request);

    internal static bool IsExactVideoEditV2ReadyHealthForSmoke(
        JsonElement payload)
        => VideoEditV2DurableContract.IsExactReadyHealth(payload);
}

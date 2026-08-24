using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private sealed record VideoTrimV1DurableContext(
        long Generation,
        VideoEditV2SourceChoice Source,
        VideoEditV2SourceSelector Selector,
        VideoTrimV1SourceProbe Probe,
        VideoTrimV1Plan Plan,
        string SourceId,
        VideoSourceChoice Dependency,
        VideoH3SourceStamp SourceStamp,
        JsonElement Request);

    private CancellationTokenSource? _videoTrimV1StartCts;
    private long _videoTrimV1HealthGeneration;
    private long _videoTrimV1ActionGeneration;
    private bool _videoTrimV1RequestPending;

    private void ResetModalVideoTrimV1DurableState()
    {
        _videoTrimV1HealthGeneration++;
        _videoTrimV1ActionGeneration++;
        _videoTrimV1WriterReady = false;
        _videoTrimV1RequestPending = false;
        CancellationTokenSource? cts = Interlocked.Exchange(
            ref _videoTrimV1StartCts,
            null);
        TryCancelModalVideoEditV2Token(cts);
    }

    private async void RefreshModalVideoTrimV1Readiness_Click(
        object sender,
        RoutedEventArgs e)
        => await RefreshModalVideoTrimV1WriterCapabilityAsync();

    private async Task<bool> RefreshModalVideoTrimV1WriterCapabilityAsync()
    {
        long generation = ++_videoTrimV1HealthGeneration;
        _videoTrimV1WriterReady = false;
        if (ModalVideoTrimV1BoardVisible)
        {
            ModalVideoTrimV1ReadinessText.Text =
                VideoTrimV1Text("UiVideoTrimV1WriterChecking");
            RefreshModalVideoTrimV1ActionControls();
        }
        EnhancementApiResponse response =
            await SendPassiveEnhancementReadAsync("api/enhance/health");
        if (generation != _videoTrimV1HealthGeneration
            || !ModalVideoTrimV1BoardVisible)
        {
            return false;
        }
        _videoTrimV1WriterReady = response.Ok
            && response.Payload is JsonElement payload
            && VideoTrimV1Contract.IsExactReadyHealth(payload);
        ModalVideoTrimV1ReadinessText.Text = VideoTrimV1Text(
            _videoTrimV1WriterReady
                ? "UiVideoTrimV1WriterReady"
                : "UiVideoTrimV1WriterPending");
        RefreshModalVideoTrimV1ActionControls();
        return _videoTrimV1WriterReady;
    }

    private string? ValidateExactVideoTrimV1Health(JsonElement payload)
        => VideoTrimV1Contract.IsExactReadyHealth(payload)
            ? null
            : VideoTrimV1Text("UiVideoTrimV1WriterPending");

    private async void StartModalVideoTrimV1_Click(
        object sender,
        RoutedEventArgs e)
        => await StartModalVideoTrimV1Async();

    private async Task<bool> StartModalVideoTrimV1Async()
    {
        _videoTrimV1StartAttemptCount++;
        if (!_videoTrimV1WriterReady
            || _videoTrimV1RequestPending
            || _videoTrimV1InspectionPending
            || _videoTrimV1Source is null
            || _videoTrimV1Selector is null
            || _videoTrimV1Probe is null
            || _videoTrimV1Plan is null)
        {
            if (ModalVideoTrimV1BoardVisible)
            {
                ModalVideoTrimV1ReadinessText.Text =
                    VideoTrimV1Text("UiVideoTrimV1WriterPending");
                RefreshModalVideoTrimV1ActionControls();
            }
            return false;
        }

        long generation = ++_videoTrimV1ActionGeneration;
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _videoTrimV1StartCts,
            cts);
        TryCancelModalVideoEditV2Token(previous);
        previous?.Dispose();
        _videoTrimV1RequestPending = true;
        string? pendingDeliveryRequestId = null;
        string? actionStatus = null;
        ModalVideoTrimV1ReadinessText.Text =
            VideoTrimV1Text("UiVideoTrimV1Preparing");
        RefreshModalVideoTrimV1ActionControls();

        try
        {
            VideoTrimV1DurableContext? context =
                await CaptureModalVideoTrimV1DurableContextAsync(
                    generation,
                    cts.Token);
            if (context is null)
            {
                actionStatus = VideoTrimV1Text("UiVideoTrimV1StartStale");
                return false;
            }

            EnhancementApiResponse response = await SendEnhancementEnqueueAsync(
                context.Request,
                includeQueuePlacementInBody: false,
                token: cts.Token,
                healthValidator: ValidateExactVideoTrimV1Health,
                requireExactHealthValidation: true,
                recoverySourceIdentity: context.SourceId,
                prePublishValidator: () =>
                    ValidateModalVideoTrimV1BeforePublish(context),
                onBeforeDurablePublish: item =>
                {
                    IDisposable lease = AcquireVideoDurablePublishLease(
                        () => PinVideoSourceForDurablePublish(
                            context.SourceStamp));
                    try
                    {
                        pendingDeliveryRequestId = item.RequestId;
                        RecordPendingVideoSourceDependency(
                            item.RequestId,
                            context.Dependency);
                        UpdateModalDisplayedDeletePresentation();
                        return lease;
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                });

            bool hasJob = response.Ok
                && response.Payload is JsonElement payload
                && payload.TryGetProperty("job", out JsonElement job)
                && job.ValueKind == JsonValueKind.Object;
            if (response.SavedForDelivery || hasJob)
            {
                RecordActiveVideoSourceDependency(context.Dependency);
                UpdateModalDisplayedDeletePresentation();
                string message = VideoTrimV1Text(
                    response.SavedForDelivery
                        ? "UiVideoTrimV1SavedForDelivery"
                        : "UiVideoTrimV1Queued");
                SetTransientStatusToast(message);
                if (generation == _videoTrimV1ActionGeneration
                    && ModalVideoTrimV1BoardVisible)
                {
                    ModalVideoTrimV1ReadinessText.Text = message;
                    CloseModalVideoTrimV1Board(
                        restoreFocus: true,
                        stale: false);
                }
                QueueEnhancedStateRefreshIfChanged();
                return true;
            }

            _videoTrimV1WriterReady = false;
            actionStatus = string.IsNullOrWhiteSpace(response.Error)
                ? VideoTrimV1Text("UiVideoTrimV1WriterPending")
                : response.Error;
            return false;
        }
        catch (OperationCanceledException)
        {
            actionStatus = VideoTrimV1Text("UiVideoTrimV1ActionCanceled");
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
            actionStatus = VideoTrimV1Text("UiVideoTrimV1StartStale");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pendingDeliveryRequestId))
                _pendingVideoSourceDependencies.Remove(pendingDeliveryRequestId);
            if (generation == _videoTrimV1ActionGeneration)
            {
                _videoTrimV1RequestPending = false;
                if (ReferenceEquals(_videoTrimV1StartCts, cts))
                    _videoTrimV1StartCts = null;
                if (ModalVideoTrimV1BoardVisible
                    && !string.IsNullOrWhiteSpace(actionStatus))
                {
                    ModalVideoTrimV1ReadinessText.Text = actionStatus;
                }
                RefreshModalVideoTrimV1ActionControls();
                UpdateModalDisplayedDeletePresentation();
            }
            cts.Dispose();
        }
    }

    private async Task<VideoTrimV1DurableContext?>
        CaptureModalVideoTrimV1DurableContextAsync(
            long generation,
            CancellationToken token)
    {
        if (generation != _videoTrimV1ActionGeneration
            || !_videoTrimV1RequestPending
            || _videoTrimV1Source is not VideoEditV2SourceChoice source
            || _videoTrimV1Selector is not VideoEditV2SourceSelector selector
            || _videoTrimV1Probe is not VideoTrimV1SourceProbe probe
            || _videoTrimV1Plan is not VideoTrimV1Plan plan)
        {
            return null;
        }
        VideoEditV2ExplicitSourceCapture? capture =
            await CaptureModalVideoEditV2ExplicitSourceAsync(source, token);
        if (capture is null
            || !VideoEditV2TransientContract.SameSource(
                capture.Selector,
                selector)
            || !TryCreateModalVideoTrimV1Dependency(
                capture,
                out string sourceId,
                out VideoSourceChoice dependency)
            || !TryCaptureVideoSourceStamp(
                dependency,
                out VideoH3SourceStamp sourceStamp)
            || !VideoTrimV1Contract.TryBuildRequest(
                capture.Selector,
                plan,
                out JsonElement request))
        {
            return null;
        }
        return new(
            generation,
            source,
            capture.Selector,
            probe,
            plan,
            sourceId,
            dependency,
            sourceStamp,
            request);
    }

    private bool TryCreateModalVideoTrimV1Dependency(
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
                "Video Trim v1 displayed source",
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
            "Video Trim v1 managed source",
            UsesDisplayedFileDirectly: true);
        return true;
    }

    private string? ValidateModalVideoTrimV1BeforePublish(
        VideoTrimV1DurableContext context)
    {
        string stale = VideoTrimV1Text("UiVideoTrimV1StartStale");
        if (context.Generation != _videoTrimV1ActionGeneration
            || !_videoTrimV1RequestPending
            || !ModalVideoTrimV1BoardVisible
            || _videoTrimV1Source != context.Source
            || _videoTrimV1Selector != context.Selector
            || _videoTrimV1Probe != context.Probe
            || _videoTrimV1Plan != context.Plan
            || !TryCaptureDisplayedVideoEditV2Source(
                out VideoEditV2SourceChoice current)
            || !string.Equals(
                current.BaseSourceStamp,
                context.Source.BaseSourceStamp,
                StringComparison.Ordinal)
            || context.Source.ExternalSeam is not null
                && !TryRevalidateExternalVideoSourceSeam(
                    context.Source.ExternalSeam)
            || !TryCaptureVideoSourceStamp(
                context.Dependency,
                out VideoH3SourceStamp currentStamp)
            || !VideoH3SourceStampsEqual(currentStamp, context.SourceStamp)
            || !VideoTrimV1Contract.TryBuildRequest(
                context.Selector,
                context.Plan,
                out JsonElement currentRequest)
            || !string.Equals(
                currentRequest.GetRawText(),
                context.Request.GetRawText(),
                StringComparison.Ordinal))
        {
            return stale;
        }
        return null;
    }

    internal static bool TryBuildVideoTrimV1RequestForSmoke(
        VideoEditV2SourceSelector source,
        VideoTrimV1Plan plan,
        out JsonElement request)
        => VideoTrimV1Contract.TryBuildRequest(source, plan, out request);

    internal static bool IsExactVideoTrimV1ReadyHealthForSmoke(
        JsonElement health)
        => VideoTrimV1Contract.IsExactReadyHealth(health);

    public Task<bool> RefreshVideoTrimV1ReadinessForSmokeAsync()
        => RefreshModalVideoTrimV1WriterCapabilityAsync();

    public Task<bool> StartVideoTrimV1ForSmokeAsync()
        => StartModalVideoTrimV1Async();
}

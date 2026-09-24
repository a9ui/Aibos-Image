using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private Func<Task>? _beforePhotorealSettingsResolutionForSmoke;

    private async Task<bool> CloseDuringPhotorealRerunEventForSmokeAsync()
    {
        string source = Path.Combine(Path.GetDirectoryName(ResolvedEnhancementJobsPath)!, "synthetic-rerun.png");
        File.WriteAllText(source, "synthetic metadata-read fixture");
        var info = new FileInfo(source);
        var job = new EnhancementWorkspaceJobView(
            id: "synthetic-close-rerun", sourceId: source, sourcePath: source,
            sourceProducerJobId: null, sourceVideoJobId: null,
            presetId: "synthetic", adapterId: "synthetic", operation: "photoreal",
            photorealMutationSafe: true, videoMutationSafe: false, queueReorderSafe: false,
            i2iMutationSafe: false, i2iSchemaVersion: null, i2iTarget: null,
            i2iInstructionSummary: null, i2iV2EnvelopeClaimed: false,
            status: "succeeded", cancelRequested: false, progress: 100,
            outputPath: null, errorMessage: null, createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow, startedAt: null, finishedAt: null,
            sourceSize: info.Length, sourceMtimeMs: new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            queueOrder: null, apiOrdinal: 0, requestDetailsText: "")
        { PhotorealEnqueueNextCapabilitySafe = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int unhandled = 0;
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            unhandled++;
            args.Handled = true; // Record a test failure instead of terminating the harness.
        }
        Dispatcher.UnhandledException += OnUnhandled;
        _beforePhotorealSettingsResolutionForSmoke = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            RerunPhotorealJobNext_Click(new Button { Tag = job }, new RoutedEventArgs());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Close();
            release.SetResult();
            for (int attempt = 0; (_closingDrainInProgress || _durableEnqueueOperations != 0) && attempt < 100; attempt++)
                await Task.Delay(10);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            return unhandled == 0 && IsLoaded && !_closingDrainInProgress
                && !_allowCloseAfterSharedDrain && _durableEnqueueOperations == 0 && !job.IsBusy;
        }
        finally
        {
            release.TrySetResult();
            _beforePhotorealSettingsResolutionForSmoke = null;
            Dispatcher.UnhandledException -= OnUnhandled;
        }
    }

    public async Task<IReadOnlyDictionary<string, bool>> DurableEnqueuePublicationForSmokeAsync()
    {
        var checks = new Dictionary<string, bool>();
        ConfigureModalEnhancementForSmoke((request, _) =>
        {
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException("Synthetic unknown health must not send a mutation.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        object Body(int index = 0) => new { operation = "upscale", adapterId = "synthetic", sourceId = $"synthetic-{index}" };
        string pendingDirectory = EnhancementEnqueueInboxStore.GetPendingDirectory(ResolvedEnhancementJobsPath);
        int SavedCount() => Directory.Exists(pendingDirectory) ? Directory.GetFiles(pendingDirectory, "*.json").Length : 0;

        using (var canceled = new CancellationTokenSource())
        {
            int pins = 0;
            using IDisposable held = AcquireEnhancementJobsWriteLeaseForDurablePublish();
            Task<EnhancementApiResponse> pending = SendEnhancementEnqueueAsync(Body(), token: canceled.Token,
                onBeforeDurablePublish: _ => { pins++; return null; });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            checks["dispatcherResponsiveDuringLockWait"] = !pending.IsCompleted;
            canceled.Cancel();
            held.Dispose();
            bool canceledBeforeCommit = false;
            try { await pending; }
            catch (OperationCanceledException) { canceledBeforeCommit = true; }
            checks["cancelBeforeAdmissionPublishesNothing"] = canceledBeforeCommit && pins == 0 && SavedCount() == 0;
        }

        bool contextCurrent = true;
        using (IDisposable held = AcquireEnhancementJobsWriteLeaseForDurablePublish())
        {
            Task<EnhancementApiResponse> pending = SendEnhancementEnqueueAsync(Body(),
                prePublishValidator: () => contextCurrent ? null : "synthetic context changed");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            contextCurrent = false;
            held.Dispose();
            EnhancementApiResponse refused = await pending;
            checks["contextRecheckedAfterLockWait"] = refused.StatusCode == 409
                && refused.Error == "synthetic context changed" && SavedCount() == 0;
        }

        bool stopped = false;
        using (IDisposable held = AcquireEnhancementJobsWriteLeaseForDurablePublish())
        {
            Task<DurableEnhancementBatchResponse> pending = TrySendDurableEnhancementBatchAsync([Body()],
                shouldStopBeforeFirstPublish: () => stopped);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            stopped = true;
            held.Dispose();
            DurableEnhancementBatchResponse refused = await pending;
            checks["batchStopDuringLockWaitPublishesNothing"] = refused.PublishedCount == 0
                && refused.Responses.Single().StatusCode == 499 && SavedCount() == 0;
        }

        using (var canceledByValidation = new CancellationTokenSource())
        {
            int pins = 0;
            bool canceledBeforePin = false;
            try
            {
                await SendEnhancementEnqueueAsync(Body(), token: canceledByValidation.Token,
                    prePublishValidator: () => { canceledByValidation.Cancel(); return null; },
                    onBeforeDurablePublish: _ => { pins++; return null; });
            }
            catch (OperationCanceledException) { canceledBeforePin = true; }
            using IDisposable released = AcquireEnhancementJobsWriteLeaseForDurablePublish();
            checks["validationCancellationPrecedesSourcePin"] = canceledBeforePin && pins == 0 && SavedCount() == 0;
        }

        using (var canceledByStopCheck = new CancellationTokenSource())
        {
            DurableEnhancementBatchResponse refused = await TrySendDurableEnhancementBatchAsync([Body()],
                token: canceledByStopCheck.Token,
                shouldStopBeforeFirstPublish: () => { canceledByStopCheck.Cancel(); return false; });
            using IDisposable released = AcquireEnhancementJobsWriteLeaseForDurablePublish();
            checks["batchValidationCancellationPublishesNothing"] = refused.PublishedCount == 0
                && refused.Responses.Single().StatusCode == 499 && SavedCount() == 0;
        }

        checks["closeAdmissionRefusalDoesNotEscapeRealUiEvent"] =
            await CloseDuringPhotorealRerunEventForSmokeAsync() && SavedCount() == 0;

        using (var late = new CancellationTokenSource())
        {
            bool callbackOnUi = false;
            EnhancementApiResponse saved = await SendEnhancementEnqueueAsync(Body(), token: late.Token,
                onBeforeDurablePublish: _ => { callbackOnUi = Dispatcher.CheckAccess(); late.Cancel(); return null; });
            checks["lateCancelReturnsDurableIdentity"] = saved.SavedForDelivery && SavedCount() == 1 && callbackOnUi;
        }

        using (var partial = new CancellationTokenSource())
        {
            DurableEnhancementBatchResponse result = await TrySendDurableEnhancementBatchAsync(
                Enumerable.Range(0, 1001).Select(index => (object?)Body(index)).ToArray(),
                token: partial.Token, onFirstPublish: partial.Cancel);
            checks["partialBatchRetainsCommittedItems"] = result.PublishedCount == 1000
                && result.Responses.Take(1000).All(response => response.SavedForDelivery)
                && result.Responses[1000].StatusCode == 499 && SavedCount() == 2;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        using (IDisposable held = AcquireEnhancementJobsWriteLeaseForDurablePublish())
        {
            Task<EnhancementApiResponse> pending = SendEnhancementEnqueueAsync(Body(),
                prePublishValidator: () => "synthetic refusal during close");
            Close();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            held.Dispose();
            EnhancementApiResponse refused = await pending;
            for (int attempt = 0; _closingDrainInProgress && attempt < 100; attempt++)
                await Task.Delay(10);
            checks["saveFailureDuringCloseKeepsWindowUsable"] = refused.StatusCode == 409
                && IsLoaded && !_closingDrainInProgress && !_allowCloseAfterSharedDrain;
        }

        var failureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task failedAcknowledgement = CompleteDurableEnqueueUiActionAsync(async () =>
        {
            await SendEnhancementEnqueueAsync(Body());
            failureSaved.SetResult();
            await failureGate.Task;
            throw new IOException("synthetic UI acknowledgement failure");
        });
        await failureSaved.Task;
        Close();
        failureGate.SetResult();
        await failedAcknowledgement;
        for (int attempt = 0; _closingDrainInProgress && attempt < 100; attempt++)
            await Task.Delay(10);
        checks["acknowledgementFailureRetainsSavedReservationAndWindow"] = IsLoaded
            && !_closingDrainInProgress && !_allowCloseAfterSharedDrain && SavedCount() == 3;

        var timeoutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task slowAcknowledgement = CompleteDurableEnqueueUiActionAsync(async () =>
        {
            await SendEnhancementEnqueueAsync(Body());
            timeoutSaved.SetResult();
            await timeoutGate.Task;
        });
        await timeoutSaved.Task;
        Close();
        bool admittedDuringClose = false;
        await CompleteDurableEnqueueUiActionAsync(() =>
        {
            admittedDuringClose = true;
            return Task.CompletedTask;
        });
        for (int attempt = 0; _closingDrainInProgress && attempt < 400; attempt++)
            await Task.Delay(10);
        checks["acknowledgementTimeoutRetainsSavedReservationAndWindow"] = IsLoaded
            && !_closingDrainInProgress && !_allowCloseAfterSharedDrain && SavedCount() == 4
            && !slowAcknowledgement.IsCompleted && !admittedDuringClose;
        timeoutGate.SetResult();
        await slowAcknowledgement;

        bool acknowledged = false;
        bool closedAfterAcknowledgement = false;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => { closedAfterAcknowledgement = acknowledged; closed.TrySetResult(); };
        using (IDisposable held = AcquireEnhancementJobsWriteLeaseForDurablePublish())
        {
            var acknowledgementGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var savedBeforeAcknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<EnhancementApiResponse> SubmitAndAcknowledge()
                => CompleteDurableEnqueueUiActionAsync(async () =>
            {
                EnhancementApiResponse result = await SendEnhancementEnqueueAsync(Body());
                savedBeforeAcknowledgement.SetResult();
                await acknowledgementGate.Task;
                acknowledged = result.SavedForDelivery;
                return result;
            });
            Task<EnhancementApiResponse> pending = SubmitAndAcknowledge();
            Close();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            checks["closeWaitsForPublication"] = !closed.Task.IsCompleted && !pending.IsCompleted;
            held.Dispose();
            await savedBeforeAcknowledgement.Task;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            checks["closeWaitsAcrossCallerAcknowledgementAwait"] = !closed.Task.IsCompleted
                && !pending.IsCompleted && SavedCount() == 5;
            acknowledgementGate.SetResult();
            await pending;
        }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        checks["closeFollowsUiAcknowledgement"] = closedAfterAcknowledgement && SavedCount() == 5
            && _durableEnqueueOperations == 0;
        return checks;
    }
}

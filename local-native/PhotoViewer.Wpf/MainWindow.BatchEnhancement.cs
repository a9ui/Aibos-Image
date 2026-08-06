using System.ComponentModel;
using System.Diagnostics;
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
    private const int BatchEnhancementMaxConcurrentRequests = 1;
    private const int BatchEnhancementLargeSelectionThreshold = 25;
    private static readonly string[] RequiredNcnnModelFiles =
    [
        "realesr-animevideov3-x2.param",
        "realesr-animevideov3-x2.bin",
        "realesr-animevideov3-x3.param",
        "realesr-animevideov3-x3.bin",
        "realesr-animevideov3-x4.param",
        "realesr-animevideov3-x4.bin",
        "realesrgan-x4plus.param",
        "realesrgan-x4plus.bin",
    ];

    private readonly List<BatchEnhancementItemView> _batchEnhancementItems = [];
    private readonly HashSet<string> _batchEnhancementCreatedJobIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _batchEnhancementPreflightJobIds = new(StringComparer.Ordinal);
    private IInputElement? _batchEnhancementFocusBeforeDialog;
    private bool _batchEnhancementChecking;
    private bool _batchEnhancementCompanionReady;
    private bool _batchEnhancementRequestPending;
    private bool _batchEnhancementStopRequested;
    private bool _batchEnhancementDurablePublishCommitted;
    private bool _batchEnhancementCompleted;
    private long _batchEnhancementGeneration;
    private int _batchEnhancementSelectedCount;
    private int _batchEnhancementGetCount;
    private int _batchEnhancementPostCount;
    private int _batchEnhancementInFlight;
    private int _batchEnhancementMaxInFlight;
    private long _batchEnhancementSynchronousOpenMilliseconds;

    private void InitializeBatchEnhancementWorkflow()
    {
        Closed += (_, _) =>
        {
            _batchEnhancementGeneration++;
            _batchEnhancementStopRequested = true;
        };
    }

    private async void OpenBatchEnhancement_Click(object sender, RoutedEventArgs e)
        => await OpenBatchEnhancementPreflightAsync();

    private async Task OpenBatchEnhancementPreflightAsync()
    {
        if (_batchEnhancementRequestPending || BatchEnhancementDialog.Visibility == Visibility.Visible)
            return;

        var selected = SelectedTiles()
            .Select(static tile => new BatchEnhancementSourceSnapshot(
                tile.Path,
                tile.FileName,
                tile.IsRealFile,
                tile.Prompt))
            .ToArray();
        if (selected.Length == 0)
        {
            SetStatusToast("Select one or more source images first.");
            return;
        }

        var openWatch = Stopwatch.StartNew();
        StopGalleryAutoScroll();
        SearchHistoryPopup.IsOpen = false;
        _batchEnhancementFocusBeforeDialog = Keyboard.FocusedElement;
        _batchEnhancementSelectedCount = selected.Length;
        _batchEnhancementItems.Clear();
        _batchEnhancementItems.AddRange(selected.Select(static source => BatchEnhancementItemView.Checking(source.DisplayName, source.Path)));
        _batchEnhancementCreatedJobIds.Clear();
        _batchEnhancementPreflightJobIds.Clear();
        _batchEnhancementChecking = true;
        _batchEnhancementCompanionReady = false;
        _batchEnhancementRequestPending = false;
        _batchEnhancementStopRequested = false;
        _batchEnhancementDurablePublishCommitted = false;
        _batchEnhancementCompleted = false;
        _batchEnhancementInFlight = 0;
        _batchEnhancementMaxInFlight = 0;
        BatchEnhancementAllowLargeCheckBox.IsChecked = false;
        BatchEnhancementItemsList.ItemsSource = _batchEnhancementItems.ToArray();
        BatchEnhancementDialog.Visibility = Visibility.Visible;
        BatchEnhancementCompanionStatusText.Text = "Checking the local companion and selected sources...";
        string adapterId = _modalEnhancementAdapterId;
        BatchEnhancementAdapterStatusText.Text = string.Equals(
            adapterId,
            "comfyui",
            StringComparison.Ordinal)
                ? "ComfyUI AI upscale · checking companion availability"
                : "Real-ESRGAN fast GPU · checking local installation";
        BatchEnhancementStatusText.Text = "Review only. No enhancement jobs have been created.";
        RefreshBatchEnhancementSurface();
        long generation = ++_batchEnhancementGeneration;
        _ = Dispatcher.BeginInvoke(FocusFirstAvailableBatchEnhancementControl, DispatcherPriority.Input);
        openWatch.Stop();
        _batchEnhancementSynchronousOpenMilliseconds = openWatch.ElapsedMilliseconds;

        Task<List<BatchEnhancementItemView>> sourceCheck = Task.Run(() => BuildBatchEnhancementItems(selected));
        Task<BatchEnhancementAdapterAvailability> adapterCheck = Task.Run(
            () => CheckBatchEnhancementAdapterAvailability(adapterId));
        _batchEnhancementGetCount++;
        Task<EnhancementApiResponse> companionCheck = SendEnhancementApiAsync(
            HttpMethod.Get,
            "api/enhance/jobs",
            timeoutMilliseconds: DurableEnqueueActionDeadlineMilliseconds);
        await Task.WhenAll(sourceCheck, adapterCheck, companionCheck);

        if (generation != _batchEnhancementGeneration || BatchEnhancementDialog.Visibility != Visibility.Visible)
            return;

        _batchEnhancementItems.Clear();
        _batchEnhancementItems.AddRange(await sourceCheck);
        BatchEnhancementAdapterAvailability adapter = await adapterCheck;
        EnhancementApiResponse response = await companionCheck;
        _batchEnhancementCompanionReady = response.Ok && response.Payload is JsonElement;
        if (_batchEnhancementCompanionReady
            && response.Payload is JsonElement payload
            && TryReadEnhancementJobInventory(
                payload,
                out HashSet<string> activeSources,
                out HashSet<string> knownJobIds))
        {
            _batchEnhancementPreflightJobIds.UnionWith(knownJobIds);
            foreach (BatchEnhancementItemView item in _batchEnhancementItems.Where(
                         static item => item.State == BatchEnhancementItemState.Ready))
            {
                if (activeSources.Contains(item.SourceIdentity))
                    item.MarkSkipped("Already queued or running.");
            }
            BatchEnhancementCompanionStatusText.Text = "Local companion connected. Starting the batch will create one job per eligible source.";
        }
        else
        {
            _batchEnhancementCompanionReady = false;
            BatchEnhancementCompanionStatusText.Text = response.Ok
                ? "The local companion returned an invalid jobs response."
                : response.Error;
        }

        BatchEnhancementAdapterStatusText.Text = adapter.Message;
        _batchEnhancementChecking = false;
        BatchEnhancementItemsList.ItemsSource = _batchEnhancementItems.ToArray();
        RefreshBatchEnhancementSurface();
        _ = Dispatcher.BeginInvoke(FocusFirstAvailableBatchEnhancementControl, DispatcherPriority.Input);
    }

    private List<BatchEnhancementItemView> BuildBatchEnhancementItems(
        IReadOnlyList<BatchEnhancementSourceSnapshot> selected)
    {
        var result = new List<BatchEnhancementItemView>(selected.Count);
        var canonicalSources = new HashSet<string>(EnhancementSourceIdentityComparer);
        foreach (BatchEnhancementSourceSnapshot source in selected)
        {
            if (!source.IsRealFile)
            {
                result.Add(BatchEnhancementItemView.Skipped(source.DisplayName, source.Path, "Not a source file."));
                continue;
            }
            if (!SupportedImageExtensions.Contains(Path.GetExtension(source.Path)))
            {
                result.Add(BatchEnhancementItemView.Skipped(source.DisplayName, source.Path, "Unsupported image format."));
                continue;
            }
            if (!TryResolveEnhancementSourceIdentity(source.Path, out string sourceIdentity))
            {
                result.Add(BatchEnhancementItemView.Skipped(source.DisplayName, source.Path, "Source path could not be resolved."));
                continue;
            }
            if (!File.Exists(sourceIdentity))
            {
                result.Add(BatchEnhancementItemView.Skipped(source.DisplayName, source.Path, "Source file is missing."));
                continue;
            }
            if (!canonicalSources.Add(sourceIdentity))
            {
                result.Add(BatchEnhancementItemView.Skipped(source.DisplayName, source.Path, "Duplicate source in this selection."));
                continue;
            }

            string? prompt = ResolveOriginalPromptSnapshot(
                source.Prompt,
                sourceIdentity);
            result.Add(BatchEnhancementItemView.Ready(
                source.DisplayName,
                source.Path,
                sourceIdentity,
                string.IsNullOrWhiteSpace(prompt) ? null : prompt));
        }
        return result;
    }

    private static BatchEnhancementAdapterAvailability CheckBatchEnhancementAdapterAvailability(
        string adapterId)
    {
        if (string.Equals(adapterId, "comfyui", StringComparison.Ordinal))
        {
            return new BatchEnhancementAdapterAvailability(
                false,
                "ComfyUI AI upscale · the companion will validate its workflow and models when submitting");
        }

        try
        {
            string? customRoot = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_ROOT");
            string? customExecutable = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_EXE");
            string? customModelDirectory = Environment.GetEnvironmentVariable("PVU_REALESRGAN_NCNN_MODEL_DIR");
            if (customRoot is not null
                || customExecutable is not null
                || customModelDirectory is not null)
            {
                return new BatchEnhancementAdapterAvailability(
                    false,
                    "Real-ESRGAN fast GPU · custom configuration present; the companion will validate it when submitting");
            }

            const string root = @"C:\AI\RealESRGAN-ncnn-vulkan";
            string executable = Path.Combine(root, "realesrgan-ncnn-vulkan.exe");
            string modelDirectory = Path.Combine(root, "models");
            string[] required =
            [
                executable,
                modelDirectory,
                .. RequiredNcnnModelFiles.Select(file => Path.Combine(modelDirectory, file)),
            ];
            int missing = required.Count(path => !File.Exists(path) && !Directory.Exists(path));
            return missing == 0
                ? new BatchEnhancementAdapterAvailability(
                    true,
                    "Real-ESRGAN fast GPU · local executable and required models detected")
                : new BatchEnhancementAdapterAvailability(
                    false,
                    $"Real-ESRGAN fast GPU · {missing:N0} required local item{(missing == 1 ? "" : "s")} not detected; the companion will verify again when submitting");
        }
        catch
        {
            return new BatchEnhancementAdapterAvailability(
                false,
                "Real-ESRGAN fast GPU · local installation could not be inspected; the companion will verify when submitting");
        }
    }

    private bool TryReadEnhancementJobInventory(
        JsonElement payload,
        out HashSet<string> activeSources,
        out HashSet<string> knownJobIds)
    {
        activeSources = new HashSet<string>(EnhancementSourceIdentityComparer);
        knownJobIds = new HashSet<string>(StringComparer.Ordinal);
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("jobs", out JsonElement jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement job in jobs.EnumerateArray())
        {
            if (TryGetStringProperty(job, "id", out string? id))
                knownJobIds.Add(id!);
            if (!TryGetStringProperty(job, "status", out string? status)
                || status is not ("queued" or "running")
                || !TryGetStringProperty(job, "sourceId", out string? sourceId)
                || !TryResolveEnhancementSourceIdentity(sourceId, out string sourceIdentity))
            {
                continue;
            }
            activeSources.Add(sourceIdentity);
        }
        return true;
    }

    private async void StartBatchEnhancement_Click(object sender, RoutedEventArgs e)
    {
        if (_batchEnhancementRequestPending || _batchEnhancementChecking)
            return;

        BatchEnhancementItemView[] ready = _batchEnhancementItems
            .Where(static item => item.State == BatchEnhancementItemState.Ready)
            .ToArray();
        await RunBatchEnhancementAsync(ready, retry: false);
    }

    private async void RetryFailedBatchEnhancement_Click(object sender, RoutedEventArgs e)
    {
        if (_batchEnhancementRequestPending || _batchEnhancementChecking)
            return;

        BatchEnhancementItemView[] failed = _batchEnhancementItems
            .Where(static item => item.CanRetry)
            .ToArray();
        foreach (BatchEnhancementItemView item in failed)
            item.ResetForRetry();
        await RunBatchEnhancementAsync(failed, retry: true);
    }

    private async Task RunBatchEnhancementAsync(
        IReadOnlyList<BatchEnhancementItemView> items,
        bool retry)
    {
        if (_batchEnhancementRequestPending || items.Count == 0)
            return;

        _batchEnhancementRequestPending = true;
        _batchEnhancementStopRequested = false;
        _batchEnhancementDurablePublishCommitted = false;
        _batchEnhancementCompleted = false;
        BatchEnhancementStatusText.Text = retry
            ? $"Retrying {items.Count:N0} failed source{(items.Count == 1 ? "" : "s")}..."
            : $"Submitting {items.Count:N0} eligible source{(items.Count == 1 ? "" : "s")}...";
        RefreshBatchEnhancementSurface();
        bool confirmLarge = BatchEnhancementAllowLargeCheckBox.IsChecked == true;
        DurableEnhancementBatchResponse durableBatch =
            await TrySendDurableEnhancementBatchAsync(
                items.Select(item => (object?)CreateBatchEnhancementRequestBody(
                    item.SourceIdentity,
                    item.Prompt,
                    confirmLarge)).ToArray(),
                onFirstPublish: OnFirstDurableBatchPublish,
                shouldStopBeforeFirstPublish: () => _batchEnhancementStopRequested);
        _batchEnhancementPostCount += durableBatch.NudgeCount;
        for (int index = 0; index < items.Count; index++)
        {
            BatchEnhancementItemView item = items[index];
            EnhancementApiResponse response = durableBatch.Responses[index];
            if (response.StatusCode == 499)
                continue;
            item.MarkSubmitting();
            if (response.SavedForDelivery)
            {
                item.MarkSavedForDelivery();
            }
            else if (response.Ok
                && response.Payload is JsonElement payload
                && TryReadCreatedBatchEnhancementJob(
                    payload,
                    item.SourceIdentity,
                    out string? jobId))
            {
                item.MarkQueued(jobId!);
                _batchEnhancementCreatedJobIds.Add(jobId!);
            }
            else
            {
                item.MarkFailed(string.IsNullOrWhiteSpace(response.Error)
                    ? "The durable queue reservation could not be confirmed."
                    : response.Error);
            }
        }

        foreach (BatchEnhancementItemView item in items.Where(static item => item.State == BatchEnhancementItemState.Ready))
            item.MarkStopped("Not sent because batch submission was stopped.");

        _batchEnhancementRequestPending = false;
        _batchEnhancementCompleted = true;
        int queued = _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Queued);
        int failed = _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Failed);
        int saved = _batchEnhancementItems.Count(
            static item => item.State == BatchEnhancementItemState.SavedForDelivery);
        int outcomeUnknown = _batchEnhancementItems.Count(
            static item => item.State == BatchEnhancementItemState.OutcomeUnknown);
        int stopped = _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Stopped);
        BatchEnhancementStatusText.Text = _batchEnhancementStopRequested
            ? $"Stopped. {queued:N0} created · {saved:N0} saved for delivery · {failed:N0} failed · {outcomeUnknown:N0} receipt unknown · {stopped:N0} not sent. Created jobs were not canceled."
            : $"{queued:N0} created · {saved:N0} saved for delivery · {failed:N0} failed · {outcomeUnknown:N0} receipt unknown. Original sources were not changed.";
        RefreshBatchEnhancementSurface();
        BatchEnhancementItemsList.Items.Refresh();
        _ = Dispatcher.BeginInvoke(FocusFirstAvailableBatchEnhancementControl, DispatcherPriority.Input);
    }

    private async Task SubmitBatchEnhancementWorkerAsync(
        IReadOnlyList<BatchEnhancementItemView> items,
        Func<int> nextIndex,
        bool confirmLarge)
    {
        while (true)
        {
            if (_batchEnhancementStopRequested)
                return;
            int index = nextIndex();
            if (index >= items.Count)
                return;
            if (_batchEnhancementStopRequested)
                return;

            BatchEnhancementItemView item = items[index];
            item.MarkSubmitting();
            int inFlight = Interlocked.Increment(ref _batchEnhancementInFlight);
            int observedMaximum;
            while (inFlight > (observedMaximum = Volatile.Read(ref _batchEnhancementMaxInFlight)))
                Interlocked.CompareExchange(ref _batchEnhancementMaxInFlight, inFlight, observedMaximum);
            try
            {
                _batchEnhancementPostCount++;
                EnhancementApiResponse response = await SendEnhancementApiAsync(
                    HttpMethod.Post,
                    "api/enhance/jobs",
                    CreateBatchEnhancementRequestBody(
                        item.SourceIdentity,
                        item.Prompt,
                        confirmLarge));
                if (response.Ok
                    && response.Payload is JsonElement payload
                    && TryReadCreatedBatchEnhancementJob(payload, item.SourceIdentity, out string? jobId))
                {
                    item.MarkQueued(jobId!);
                    _batchEnhancementCreatedJobIds.Add(jobId!);
                }
                else if (response.StatusCode == 409
                    && response.Payload is JsonElement conflict
                    && TryGetStringProperty(conflict, "code", out string? code)
                    && string.Equals(code, "UPSCALE_REQUIRES_CONFIRMATION", StringComparison.Ordinal))
                {
                    item.MarkFailed("Large image needs confirmation. Enable the large-image option, then retry failed.");
                }
                else if (!IsDefinitiveNoCreateResponse(response))
                {
                    bool reconciled = await TryReconcileAmbiguousBatchEnhancementResponseAsync(item);
                    if (!reconciled)
                    {
                        item.MarkOutcomeUnknown(
                            "The request may have reached the companion, but no receipt was returned. Check Jobs before trying again; Aibos will not resend it automatically.");
                    }
                }
                else
                {
                    item.MarkFailed(string.IsNullOrWhiteSpace(response.Error)
                        ? "The companion did not return a created job."
                        : response.Error);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _batchEnhancementInFlight);
            }
        }
    }

    private static bool IsDefinitiveNoCreateResponse(EnhancementApiResponse response)
    {
        if (response.StatusCode is >= 400 and < 500)
            return true;
        return response.StatusCode == 503
            && response.Payload is JsonElement payload
            && TryGetStringProperty(payload, "code", out string? code)
            && string.Equals(code, "BACKEND_NOT_AVAILABLE", StringComparison.Ordinal);
    }

    private async Task<bool> TryReconcileAmbiguousBatchEnhancementResponseAsync(BatchEnhancementItemView item)
    {
        _batchEnhancementGetCount++;
        EnhancementApiResponse response = await SendEnhancementApiAsync(HttpMethod.Get, "api/enhance/jobs");
        if (!response.Ok
            || response.Payload is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("jobs", out JsonElement jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? reconciledJobId = null;
        foreach (JsonElement job in jobs.EnumerateArray())
        {
            if (!TryGetStringProperty(job, "id", out string? jobId))
                continue;
            if (reconciledJobId is null
                && !_batchEnhancementPreflightJobIds.Contains(jobId!)
                && TryReadBatchEnhancementJobElement(job, item.SourceIdentity, out _))
            {
                reconciledJobId = jobId;
            }
        }
        if (reconciledJobId is null)
            return false;

        item.MarkQueued(reconciledJobId);
        _batchEnhancementCreatedJobIds.Add(reconciledJobId);
        return true;
    }

    private bool TryReadCreatedBatchEnhancementJob(
        JsonElement payload,
        string expectedSourceIdentity,
        out string? jobId)
    {
        jobId = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("job", out JsonElement job)
            || !TryReadBatchEnhancementJobElement(job, expectedSourceIdentity, out jobId))
        {
            jobId = null;
            return false;
        }
        return true;
    }

    private bool TryReadBatchEnhancementJobElement(
        JsonElement job,
        string expectedSourceIdentity,
        out string? jobId)
    {
        jobId = null;
        if (job.ValueKind != JsonValueKind.Object
            || !TryGetStringProperty(job, "id", out jobId)
            || !TryGetStringProperty(job, "sourceId", out string? sourceId)
            || !TryGetStringProperty(job, "sourcePath", out string? sourcePath)
            || !TryResolveEnhancementSourceIdentity(sourceId, out string sourceIdIdentity)
            || !TryResolveEnhancementSourceIdentity(sourcePath, out string sourcePathIdentity))
        {
            jobId = null;
            return false;
        }
        bool matches = EnhancementSourceIdentityComparer.Equals(sourceIdIdentity, expectedSourceIdentity)
            && EnhancementSourceIdentityComparer.Equals(sourcePathIdentity, expectedSourceIdentity);
        if (!matches)
            jobId = null;
        return matches;
    }

    private void FocusFirstAvailableBatchEnhancementControl()
    {
        Button[] controls =
        [
            BatchEnhancementStartButton,
            BatchEnhancementRetryFailedButton,
            BatchEnhancementViewJobsButton,
            BatchEnhancementCancelButton,
            BatchEnhancementCloseButton,
        ];
        Button? target = controls.FirstOrDefault(static control =>
            control.Visibility == Visibility.Visible
            && control.IsEnabled
            && control.Focusable);
        _ = target?.Focus();
    }

    private Dictionary<string, object?> CreateBatchEnhancementRequestBody(
        string sourceIdentity,
        string? prompt,
        bool confirmLarge)
        => CreateUpscaleRequestBody(
            sourceIdentity,
            new UpscaleRequestSource(null, null),
            _modalEnhancementPresetId,
            _modalEnhancementAdapterId,
            _modalEnhancementScale,
            prompt,
            confirmLarge ? true : null,
            outputFormat: _upscaleOutputFormat,
            includeOperation: false,
            includeNullOriginalPrompt: false);

    private void CancelBatchEnhancement_Click(object sender, RoutedEventArgs e)
    {
        if (_batchEnhancementRequestPending)
        {
            RequestStopBatchEnhancement();
            return;
        }
        CloseBatchEnhancementDialog(restoreFocus: true);
    }

    private void OnFirstDurableBatchPublish()
    {
        if (_batchEnhancementDurablePublishCommitted)
            return;
        _batchEnhancementDurablePublishCommitted = true;
        BatchEnhancementStatusText.Text =
            "Queue reservations are saved locally. Delivery to Jobs will continue; saved reservations can no longer be stopped here.";
        RefreshBatchEnhancementSurface();
    }

    private void RequestStopBatchEnhancement()
    {
        if (!_batchEnhancementRequestPending || _batchEnhancementStopRequested)
            return;
        if (_batchEnhancementDurablePublishCommitted)
        {
            BatchEnhancementStatusText.Text =
                "Queue reservations are already saved locally. Delivery to Jobs will continue.";
            RefreshBatchEnhancementSurface();
            return;
        }
        _batchEnhancementStopRequested = true;
        BatchEnhancementStatusText.Text = "Stopping after the current requests finish. Created jobs will keep running.";
        RefreshBatchEnhancementSurface();
    }

    private void BatchEnhancementBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (BatchEnhancementDialog.Visibility == Visibility.Visible
            && ReferenceEquals(e.OriginalSource, BatchEnhancementDialog)
            && !_batchEnhancementRequestPending)
        {
            CloseBatchEnhancementDialog(restoreFocus: true);
            e.Handled = true;
        }
    }

    private void CloseBatchEnhancementDialog(bool restoreFocus)
    {
        if (BatchEnhancementDialog.Visibility != Visibility.Visible)
            return;
        if (_batchEnhancementRequestPending)
        {
            RequestStopBatchEnhancement();
            return;
        }

        _batchEnhancementGeneration++;
        BatchEnhancementDialog.Visibility = Visibility.Collapsed;
        if (restoreFocus)
            RestoreOverlayFocus(_batchEnhancementFocusBeforeDialog);
        _batchEnhancementFocusBeforeDialog = null;
    }

    private async void ViewBatchEnhancementJobs_Click(object sender, RoutedEventArgs e)
    {
        bool hasSavedOrUnknownOutcome = _batchEnhancementItems.Any(
            static item => item.State is BatchEnhancementItemState.SavedForDelivery
                or BatchEnhancementItemState.OutcomeUnknown);
        if ((_batchEnhancementCreatedJobIds.Count == 0 && !hasSavedOrUnknownOutcome)
            || _batchEnhancementRequestPending)
            return;

        string[] ids = _batchEnhancementCreatedJobIds.ToArray();
        IInputElement? focusToRestore = _batchEnhancementFocusBeforeDialog;
        CloseBatchEnhancementDialog(restoreFocus: false);
        await OpenEnhancementJobsWorkspaceAsync(
            initialFilter: "all",
            highlightedJobIds: ids,
            focusToRestore: focusToRestore);
    }

    private void RefreshBatchEnhancementSurface()
    {
        if (BatchEnhancementDialog is null)
            return;

        int eligible = _batchEnhancementItems.Count(static item => item.IsEligible);
        int skipped = _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Skipped);
        int failed = _batchEnhancementItems.Count(static item => item.CanRetry);
        int outcomeUnknown = _batchEnhancementItems.Count(
            static item => item.State == BatchEnhancementItemState.OutcomeUnknown);
        int savedForDelivery = _batchEnhancementItems.Count(
            static item => item.State == BatchEnhancementItemState.SavedForDelivery);
        BatchEnhancementCountsText.Text = _batchEnhancementChecking
            ? $"{_batchEnhancementSelectedCount:N0} selected · checking eligibility"
            : $"{_batchEnhancementSelectedCount:N0} selected · {eligible:N0} eligible · {skipped:N0} skipped";
        BatchEnhancementEmptyText.Visibility = _batchEnhancementItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BatchEnhancementWarningBorder.Visibility =
            _batchEnhancementSelectedCount >= BatchEnhancementLargeSelectionThreshold
                ? Visibility.Visible
                : Visibility.Collapsed;
        BatchEnhancementStartButton.Content = $"Enhance {eligible:N0} selected";
        BatchEnhancementStartButton.IsEnabled = !_batchEnhancementChecking
            && !_batchEnhancementRequestPending
            && !_batchEnhancementCompleted
            && eligible > 0;
        BatchEnhancementStartButton.Visibility = _batchEnhancementCompleted
            ? Visibility.Collapsed
            : Visibility.Visible;
        BatchEnhancementRetryFailedButton.Content = $"Retry failed ({failed:N0})";
        BatchEnhancementRetryFailedButton.Visibility = !_batchEnhancementRequestPending
            && _batchEnhancementCompleted
            && failed > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        BatchEnhancementRetryFailedButton.IsEnabled = !_batchEnhancementChecking;
        BatchEnhancementViewJobsButton.Visibility = !_batchEnhancementRequestPending
            && (_batchEnhancementCreatedJobIds.Count > 0
                || savedForDelivery > 0
                || outcomeUnknown > 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        BatchEnhancementCancelButton.Content = _batchEnhancementRequestPending
            ? (_batchEnhancementDurablePublishCommitted
                ? "Saved"
                : _batchEnhancementStopRequested ? "Stopping..." : "Stop unsent")
            : "Close";
        BatchEnhancementCancelButton.IsEnabled = !_batchEnhancementStopRequested
            && !(_batchEnhancementRequestPending
                && _batchEnhancementDurablePublishCommitted);
        BatchEnhancementCloseButton.IsEnabled = !_batchEnhancementRequestPending;
        BatchEnhancementAllowLargeCheckBox.IsEnabled = !_batchEnhancementRequestPending;
        AutomationProperties.SetHelpText(
            BatchEnhancementStartButton,
            "Saves every explicit queue reservation locally. Only an inbox-capable companion receives an immediate delivery nudge. Opening or closing this review creates no jobs and starts no background process.");
    }

    public async Task OpenBatchEnhancementForSmokeAsync()
    {
        await OpenBatchEnhancementPreflightAsync();
        for (int attempt = 0; attempt < 500 && _batchEnhancementChecking; attempt++)
            await Task.Delay(10);
    }

    public async Task StartBatchEnhancementForSmokeAsync()
    {
        StartBatchEnhancement_Click(this, new RoutedEventArgs());
        await WaitForBatchEnhancementIdleForSmokeAsync();
    }

    public async Task StartBatchEnhancementDoubleClickForSmokeAsync()
    {
        StartBatchEnhancement_Click(this, new RoutedEventArgs());
        StartBatchEnhancement_Click(this, new RoutedEventArgs());
        await WaitForBatchEnhancementIdleForSmokeAsync();
    }

    public async Task RetryFailedBatchEnhancementForSmokeAsync(bool confirmLarge)
    {
        BatchEnhancementAllowLargeCheckBox.IsChecked = confirmLarge;
        RetryFailedBatchEnhancement_Click(this, new RoutedEventArgs());
        await WaitForBatchEnhancementIdleForSmokeAsync();
    }

    public void StopBatchEnhancementForSmoke() => RequestStopBatchEnhancement();

    public void CloseBatchEnhancementForSmoke() => CloseBatchEnhancementDialog(restoreFocus: false);

    public bool BatchEnhancementKeyboardSurfaceForSmoke
        => KeyboardNavigation.GetTabNavigation(BatchEnhancementDialogSurface) == KeyboardNavigationMode.Cycle
            && BatchEnhancementStartButton.IsKeyboardFocused;

    public bool ApplyBatchEnhancementNarrowLayoutForSmoke()
    {
        Width = 900;
        Height = 620;
        UpdateLayout();
        if (BatchEnhancementDialog.Visibility != Visibility.Visible
            || BatchEnhancementDialogSurface.ActualWidth <= 0
            || BatchEnhancementDialogSurface.ActualHeight <= 0)
        {
            return false;
        }

        Rect startBounds = BatchEnhancementStartButton
            .TransformToAncestor(BatchEnhancementDialogSurface)
            .TransformBounds(new Rect(BatchEnhancementStartButton.RenderSize));
        Rect surfaceBounds = new(new Point(0, 0), BatchEnhancementDialogSurface.RenderSize);
        return surfaceBounds.Contains(startBounds.TopLeft)
            && surfaceBounds.Contains(startBounds.BottomRight)
            && BatchEnhancementDialogSurface.ActualWidth <= BatchEnhancementDialog.ActualWidth
            && BatchEnhancementDialogSurface.ActualHeight <= BatchEnhancementDialog.ActualHeight;
    }

    public async Task ViewBatchEnhancementJobsForSmokeAsync()
    {
        ViewBatchEnhancementJobs_Click(this, new RoutedEventArgs());
        for (int attempt = 0; attempt < 500 && EnhancementJobsDialog.Visibility != Visibility.Visible; attempt++)
            await Task.Delay(10);
        await WaitForEnhancementWorkspaceIdleForSmokeAsync();
    }

    public BatchEnhancementSmokeSnapshot BatchEnhancementForSmoke()
        => new(
            BatchEnhancementDialog.Visibility == Visibility.Visible,
            _batchEnhancementSelectedCount,
            _batchEnhancementItems.Count(static item => item.IsEligible),
            _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Skipped),
            _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Queued),
            _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Failed),
            _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.OutcomeUnknown),
            _batchEnhancementItems.Count(static item => item.State == BatchEnhancementItemState.Stopped),
            _batchEnhancementChecking,
            _batchEnhancementRequestPending,
            _batchEnhancementGetCount,
            _batchEnhancementPostCount,
            _batchEnhancementMaxInFlight,
            _batchEnhancementSynchronousOpenMilliseconds,
            BatchEnhancementAdapterStatusText.Text,
            _batchEnhancementCreatedJobIds.ToArray(),
            _batchEnhancementItems.Select(static item => new BatchEnhancementItemSmokeSnapshot(
                item.DisplayName,
                item.State.ToString(),
                item.JobId,
                item.DetailText)).ToArray());

    private async Task WaitForBatchEnhancementIdleForSmokeAsync()
    {
        for (int attempt = 0; attempt < 1_000 && (_batchEnhancementChecking || _batchEnhancementRequestPending); attempt++)
            await Task.Delay(10);
    }
}

public sealed class BatchEnhancementItemView : INotifyPropertyChanged
{
    private BatchEnhancementItemState _state;
    private string _detailText;
    private string? _jobId;

    private BatchEnhancementItemView(
        string displayName,
        string sourcePath,
        string sourceIdentity,
        string? prompt,
        BatchEnhancementItemState state,
        string detailText)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(sourcePath) : displayName;
        SourcePath = sourcePath;
        SourceIdentity = sourceIdentity;
        Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        _state = state;
        _detailText = detailText;
    }

    public string DisplayName { get; }
    public string SourcePath { get; }
    public string SourceIdentity { get; }
    public string? Prompt { get; }
    public BatchEnhancementItemState State => _state;
    public string StatusLabel => _state switch
    {
        BatchEnhancementItemState.Checking => "Checking",
        BatchEnhancementItemState.Ready => "Ready",
        BatchEnhancementItemState.Skipped => "Skipped",
        BatchEnhancementItemState.Submitting => "Submitting",
        BatchEnhancementItemState.Queued => "Created",
        BatchEnhancementItemState.SavedForDelivery => "Saved",
        BatchEnhancementItemState.Failed => "Failed",
        BatchEnhancementItemState.OutcomeUnknown => "Check Jobs",
        BatchEnhancementItemState.Stopped => "Not sent",
        _ => _state.ToString(),
    };
    public string DetailText => _detailText;
    public string? JobId => _jobId;
    public bool IsEligible => _state is BatchEnhancementItemState.Ready
        or BatchEnhancementItemState.Submitting
        or BatchEnhancementItemState.Queued
        or BatchEnhancementItemState.SavedForDelivery
        or BatchEnhancementItemState.Failed
        or BatchEnhancementItemState.Stopped;
    public bool CanRetry => _state == BatchEnhancementItemState.Failed;
    public string AccessibleName => $"{DisplayName}, {StatusLabel}, {DetailText}";

    public static BatchEnhancementItemView Checking(string displayName, string sourcePath)
        => new(displayName, sourcePath, "", null, BatchEnhancementItemState.Checking, "Checking source eligibility.");

    public static BatchEnhancementItemView Ready(
        string displayName,
        string sourcePath,
        string sourceIdentity,
        string? prompt)
        => new(displayName, sourcePath, sourceIdentity, prompt, BatchEnhancementItemState.Ready, "Ready to create one enhancement job.");

    public static BatchEnhancementItemView Skipped(string displayName, string sourcePath, string reason)
        => new(displayName, sourcePath, "", null, BatchEnhancementItemState.Skipped, reason);

    public void MarkSkipped(string reason) => SetState(BatchEnhancementItemState.Skipped, reason, null);

    public void MarkSubmitting() => SetState(BatchEnhancementItemState.Submitting, "Creating job...", null);

    public void MarkQueued(string jobId) => SetState(BatchEnhancementItemState.Queued, $"Job {jobId} created.", jobId);

    public void MarkSavedForDelivery() => SetState(
        BatchEnhancementItemState.SavedForDelivery,
        "予約を保存しました。Jobsへの登録を継続しています。",
        null);

    public void MarkFailed(string reason) => SetState(BatchEnhancementItemState.Failed, reason, null);

    public void MarkOutcomeUnknown(string reason) => SetState(BatchEnhancementItemState.OutcomeUnknown, reason, null);

    public void MarkStopped(string reason) => SetState(BatchEnhancementItemState.Stopped, reason, null);

    public void ResetForRetry() => SetState(BatchEnhancementItemState.Ready, "Ready to retry this failed source.", null);

    private void SetState(BatchEnhancementItemState state, string detailText, string? jobId)
    {
        _state = state;
        _detailText = detailText;
        _jobId = jobId;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JobId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEligible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum BatchEnhancementItemState
{
    Checking,
    Ready,
    Skipped,
    Submitting,
    Queued,
    SavedForDelivery,
    Failed,
    OutcomeUnknown,
    Stopped,
}

internal readonly record struct BatchEnhancementSourceSnapshot(
    string Path,
    string DisplayName,
    bool IsRealFile,
    string? Prompt);

internal readonly record struct BatchEnhancementAdapterAvailability(
    bool Detected,
    string Message);

public sealed record BatchEnhancementSmokeSnapshot(
    bool Visible,
    int Selected,
    int Eligible,
    int Skipped,
    int Queued,
    int Failed,
    int OutcomeUnknown,
    int Stopped,
    bool Checking,
    bool Pending,
    int GetRequests,
    int PostRequests,
    int MaxInFlight,
    long SynchronousOpenMilliseconds,
    string AdapterStatus,
    string[] CreatedJobIds,
    BatchEnhancementItemSmokeSnapshot[] Items);

public sealed record BatchEnhancementItemSmokeSnapshot(
    string Name,
    string State,
    string? JobId,
    string Detail);

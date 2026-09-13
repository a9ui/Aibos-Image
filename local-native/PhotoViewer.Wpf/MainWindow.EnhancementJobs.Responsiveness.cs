using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private long? _enhancementWorkspaceRequestedRefreshGeneration;
    private bool _enhancementWorkspaceReconciliationScheduled;

    private void CompleteEnhancementWorkspaceMutation()
    {
        _enhancementWorkspaceMutationPending = false;
        ScheduleRequestedEnhancementWorkspaceReconciliation();
    }

    // A receipt acknowledges publication, not the contents or relative order
    // of its optional row. Read the authoritative inventory without waiting
    // for health; this also includes queue orders shifted by enqueue-next.
    private async Task RefreshConfirmedEnhancementEnqueueWorkspaceAsync(
        IReadOnlyList<EnhancementApiResponse> responses)
    {
        EnhancementApiResponse? confirmed = responses.FirstOrDefault(
            static response => response.Ok && !response.SavedForDelivery);
        if (confirmed is null)
            return;
        long presentationRevision = ++_enhancementWorkspaceQueuePresentationRevision;
        long generation = _enhancementWorkspaceGeneration;
        NoteEnhancementWorkspaceMutationDebt(confirmed, null);
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || _aiProcessingMinimizedMode || Dispatcher.HasShutdownStarted)
            return;
        try
        {
            _enhancementWorkspaceGetCount++;
            var (parsed, jobs, _) = await ReadEnhancementWorkspaceInventoryAsync(
                CancellationToken.None);
            if (!parsed || generation != _enhancementWorkspaceGeneration
                || presentationRevision != _enhancementWorkspaceQueuePresentationRevision
                || _enhancementWorkspaceQueueOrderFlushTask is not null
                || EnhancementJobsDialog.Visibility != Visibility.Visible
                || _aiProcessingMinimizedMode || Dispatcher.HasShutdownStarted)
                return;

            // Also invalidate a full read that started while this read awaited
            // storage. Its later health response cannot replace this inventory.
            _enhancementWorkspaceQueuePresentationRevision++;
            ApplyEnhancementWorkspaceHighlights(jobs);
            ReconcileEnhancementWorkspaceJobs(jobs);
            ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
            RefreshEnhancementQueueBulkControls();
            EnhancementJobsHeaderSummary.Text = "登録後の一覧を反映しました · 稼働状況を確認しています…";
        }
        catch (Exception ex)
        {
            // The action is already accepted. A presentation failure must not
            // turn it into an enqueue retry or discard the current valid rows.
            AibosOperationLog.Write("jobs_enqueue_reconcile", "failed", 0,
                mode: ex.GetType().Name);
        }
        finally
        {
            RequestEnhancementWorkspaceReconciliation();
        }
    }

    // Only exact cancellation responses enter this path. Enqueue receipts
    // instead use the authoritative reader above.
    private async Task ApplyConfirmedEnhancementWorkspaceResponsesAsync(
        IReadOnlyList<EnhancementApiResponse> responses,
        string expectedJobId)
    {
        EnhancementApiResponse[] confirmed = responses
            .Where(static response => response.Ok && !response.SavedForDelivery)
            .ToArray();
        if (confirmed.Length == 0)
            return;

        long presentationRevision = ++_enhancementWorkspaceQueuePresentationRevision;
        long generation = _enhancementWorkspaceGeneration;
        NoteEnhancementWorkspaceMutationDebt(confirmed[0], null);
        int ordinal = _enhancementWorkspaceJobs.Count;
        List<EnhancementWorkspaceJobView> candidates = await Task.Run(() =>
        {
            var parsed = new List<EnhancementWorkspaceJobView>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (EnhancementApiResponse response in confirmed)
            {
                try
                {
                    if (response.Payload is not JsonElement payload
                        || payload.ValueKind != JsonValueKind.Object
                        || !HasSingleProperty(payload, "job")
                        || !payload.TryGetProperty("job", out JsonElement row)
                        || row.ValueKind != JsonValueKind.Object
                        || !HasSingleProperty(row, "id")
                        || !HasSingleProperty(row, "status")
                        || !HasRequiredEnhancementWorkspaceProjectionIdentity(row)
                        || Encoding.UTF8.GetByteCount(row.GetRawText())
                            > EnhancementJobsWorkspaceMaximumPayloadBytesPerRow)
                        continue;

                    EnhancementWorkspaceJobView? candidate = ParseEnhancementWorkspaceJob(
                        row, ordinal + parsed.Count, buildRequestDetails: false);
                    if (candidate is null
                        || !string.Equals(candidate.Id, expectedJobId, StringComparison.Ordinal))
                        continue;
                    if (candidate.IsActive
                        && (!HasSingleProperty(row, "cancelRequested")
                            || !row.TryGetProperty("cancelRequested", out JsonElement cancellation)
                            || cancellation.ValueKind != JsonValueKind.True))
                        continue;
                    if (!ids.Add(candidate.Id))
                        return [];
                    parsed.Add(candidate);
                }
                catch (Exception ex) when (ex is JsonException
                    or InvalidOperationException or ArgumentException or FormatException or OverflowException)
                {
                    // A partial/unsupported response falls back to the normal
                    // authoritative reader. It never turns an accepted action
                    // into a retry, nor grants permissions to a guessed row.
                }
            }
            return parsed;
        });

        if (presentationRevision != _enhancementWorkspaceQueuePresentationRevision
            || generation != _enhancementWorkspaceGeneration
            || _enhancementWorkspaceQueueOrderFlushTask is not null
            || Dispatcher.HasShutdownStarted)
        {
            RequestEnhancementWorkspaceReconciliation();
            return;
        }

        var updated = new List<EnhancementWorkspaceJobView>(_enhancementWorkspaceJobs);
        bool changed = false;
        foreach (EnhancementWorkspaceJobView candidate in candidates)
        {
            int index = updated.FindIndex(existing =>
                string.Equals(existing.Id, candidate.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                EnhancementWorkspaceJobView existing = updated[index];
                // A worker may claim a queued job before cancel is handled.
                // Accept that forward transition with cancellation pending,
                // but never let a stale response move a running job backward.
                if (!existing.HasSameImmutableIdentity(candidate)
                    || existing.UpdatedAt > candidate.UpdatedAt
                    || existing.Status == "running" && candidate.Status == "queued"
                    || !existing.IsActive && candidate.IsActive)
                    continue;
                updated[index] = candidate;
            }
            else
            {
                // A cancel response must only update its exact existing row.
                continue;
            }
            changed = true;
        }

        if (changed)
        {
            FinalizeEnhancementWorkspaceJobs(updated);
            ApplyEnhancementWorkspaceHighlights(updated);
            ReconcileEnhancementWorkspaceJobs(updated);
            if (EnhancementJobsDialog.Visibility == Visibility.Visible)
            {
                ApplyEnhancementWorkspaceFilter(loadThumbnails: true);
                RefreshEnhancementQueueBulkControls();
                // The receipt proves these rows, not a fresh database total.
                EnhancementJobsHeaderSummary.Text = "変更を反映しました · 全体の件数を確認しています…";
            }
        }
        RequestEnhancementWorkspaceReconciliation();
    }

    private void RequestEnhancementWorkspaceReconciliation()
    {
        if (EnhancementJobsDialog.Visibility != Visibility.Visible
            || _aiProcessingMinimizedMode || Dispatcher.HasShutdownStarted)
            return;
        _enhancementWorkspaceRequestedRefreshGeneration = _enhancementWorkspaceGeneration;
        ScheduleRequestedEnhancementWorkspaceReconciliation();
    }

    private void ScheduleRequestedEnhancementWorkspaceReconciliation()
    {
        if (_enhancementWorkspaceRequestedRefreshGeneration is not long generation
            || generation != _enhancementWorkspaceGeneration
            || _enhancementWorkspaceReconciliationScheduled
            || _enhancementWorkspaceMutationPending
            || _enhancementWorkspaceRefreshPending
            || _enhancementWorkspaceHealthPollPending
            || EnhancementJobsDialog.Visibility != Visibility.Visible
            || _aiProcessingMinimizedMode || Dispatcher.HasShutdownStarted)
            return;

        _enhancementWorkspaceReconciliationScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            _enhancementWorkspaceReconciliationScheduled = false;
            if (_enhancementWorkspaceRequestedRefreshGeneration != generation
                || generation != _enhancementWorkspaceGeneration)
            {
                ScheduleRequestedEnhancementWorkspaceReconciliation();
                return;
            }
            if (_enhancementWorkspaceMutationPending || _enhancementWorkspaceRefreshPending
                || _enhancementWorkspaceHealthPollPending)
                return; // Their finally blocks preserve and drain this request.
            _enhancementWorkspaceRequestedRefreshGeneration = null;
            try
            {
                await RefreshEnhancementJobsWorkspaceAsync(generation, isPoll: true);
            }
            catch (Exception ex)
            {
                PreserveEnhancementWorkspaceAfterRefreshFailure(
                    "変更を受け付けました。Jobs全体の状態を再確認します。");
                AibosOperationLog.Write("jobs_response_reconcile", "failed", 0,
                    mode: ex.GetType().Name);
            }
        }), DispatcherPriority.Background);
    }

    public Task ApplyConfirmedEnhancementResponseForSmokeAsync(
        JsonElement payload, bool savedForDelivery = false, string? expectedJobId = null)
    {
        EnhancementApiResponse[] responses =
            [new(true, 200, payload.Clone(), "", SavedForDelivery: savedForDelivery)];
        return expectedJobId is null
            ? RefreshConfirmedEnhancementEnqueueWorkspaceAsync(responses)
            : ApplyConfirmedEnhancementWorkspaceResponsesAsync(responses, expectedJobId);
    }

    public async Task WaitForEnhancementReconciliationForSmokeAsync()
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (!_enhancementWorkspaceReconciliationScheduled
                && !_enhancementWorkspaceRefreshPending
                && _enhancementWorkspaceRequestedRefreshGeneration != _enhancementWorkspaceGeneration)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The requested Jobs reconciliation did not settle.");
    }
}

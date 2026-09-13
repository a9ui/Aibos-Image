using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private long? _enhancementWorkspaceRequestedRefreshGeneration;
    private bool _enhancementWorkspaceReconciliationScheduled;

    // Only explicit, authenticated mutation results enter this path. A saved
    // inbox reservation is not a Job and must never become a fabricated row.
    private async Task ApplyConfirmedEnhancementWorkspaceResponsesAsync(
        IReadOnlyList<EnhancementApiResponse> responses,
        string? expectedJobId = null)
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
                        || expectedJobId is not null
                            && !string.Equals(candidate.Id, expectedJobId, StringComparison.Ordinal))
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
                if (!existing.HasSameImmutableIdentity(candidate)
                    || existing.UpdatedAt > candidate.UpdatedAt
                    || !existing.IsActive && candidate.IsActive)
                    continue;
                updated[index] = candidate;
            }
            else
            {
                // A cancel response must only update its exact existing row.
                if (expectedJobId is not null)
                    continue;
                updated.Add(candidate);
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
        => ApplyConfirmedEnhancementWorkspaceResponsesAsync(
            [new EnhancementApiResponse(true, 200, payload.Clone(), "",
                SavedForDelivery: savedForDelivery)], expectedJobId);

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

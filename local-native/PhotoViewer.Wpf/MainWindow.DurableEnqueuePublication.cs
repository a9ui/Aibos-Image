using System.IO;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private int _durableEnqueueOperations;
    private TaskCompletionSource? _durableEnqueueDrained;
    private bool _durableEnqueueCloseSaveFailed;

    private sealed class DurableEnqueueOperation(MainWindow owner) : IDisposable
    {
        private MainWindow? _owner = owner;
        private bool _completed;

        internal void MarkCompleted() => _completed = true;

        public void Dispose()
        {
            MainWindow? current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            current.Dispatcher.VerifyAccess();
            if (!_completed && current._closingDrainInProgress)
                current._durableEnqueueCloseSaveFailed = true;
            if (--current._durableEnqueueOperations == 0)
                current._durableEnqueueDrained?.TrySetResult();
        }
    }

    private sealed class DurableEnqueueValidationException(string message) : IOException(message);

    // UI action entry points own this scope through their result presentation
    // and finally blocks. A dispatcher priority cannot stand in for that ACK:
    // callers can yield or await after SendEnhancementEnqueueAsync returns.
    private async Task<T> CompleteDurableEnqueueUiActionAsync<T>(Func<Task<T>> action)
    {
        Dispatcher.VerifyAccess();
        if (_closingDrainInProgress || _allowCloseAfterSharedDrain
            || _enhancementCompanionLifetimeCts.IsCancellationRequested)
            return default!;
        using DurableEnqueueOperation acknowledgement = BeginDurableEnqueueOperation();
        try
        {
            T result = await action();
            acknowledgement.MarkCompleted();
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException
            or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            // This boundary is awaited by async-void event handlers. Refusal
            // after an earlier await must not escape as an unhandled UI error.
            // Leave the scope incomplete so a pending close also fails safely.
            SetStatusToast("The queue action or its result display was interrupted. Saved reservations are retained; check Jobs before retrying.");
            return default!;
        }
    }

    private async Task CompleteDurableEnqueueUiActionAsync(Func<Task> action)
        => await CompleteDurableEnqueueUiActionAsync(async () =>
        {
            await action();
            return true;
        });

    private DurableEnqueueOperation BeginDurableEnqueueOperation()
    {
        Dispatcher.VerifyAccess();
        if (_closingDrainInProgress || _allowCloseAfterSharedDrain
            || _enhancementCompanionLifetimeCts.IsCancellationRequested)
        {
            throw new OperationCanceledException("The window is closing; no new queue reservation was started.");
        }
        if (_durableEnqueueOperations++ == 0)
            _durableEnqueueDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new DurableEnqueueOperation(this);
    }

    private async Task<SharedWriteStatus> DrainDurableEnqueueOperationsAsync(CancellationToken token)
    {
        if (_durableEnqueueOperations != 0 && _durableEnqueueDrained is { } pending)
            await pending.Task.WaitAsync(token);
        return _durableEnqueueCloseSaveFailed ? SharedWriteStatus.Failed : SharedWriteStatus.Succeeded;
    }

    private async Task PublishDurableEnqueueAsync(
        EnhancementEnqueueInboxItem[] items,
        Func<IDisposable?> beforePublish,
        CancellationToken token)
    {
        // Immutable wire items and the resolved destination are captured on UI.
        // Only publication serialization, lock wait and disk IO move off UI.
        string jobsPath = ResolvedEnhancementJobsPath;
        await Task.Run(() => EnhancementEnqueueInboxStore.Publish(
            jobsPath,
            items,
            beforePublish: () => Dispatcher.Invoke(() =>
            {
                token.ThrowIfCancellationRequested();
                // Revalidate editable context after waiting for the Jobs lock.
                // Existing source pins/overlays remain one UI-owned callback.
                return beforePublish();
            }),
            cancellationToken: token), token);
        // Do not cancel after publication: callers must acknowledge the saved
        // identity and mark durable Companion work even after a late cancel.
    }
}

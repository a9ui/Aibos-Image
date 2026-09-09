using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _companionControlPending;

    public Task StartEnhancementCompanionApiForApplicationLaunchAsync()
        => StartCompanionApiOnlyAsync();

    private async void StartCompanion_Click(object sender, RoutedEventArgs e)
        => await StartCompanionApiOnlyAsync();

    private async Task StartCompanionApiOnlyAsync()
    {
        if (_companionControlPending || _enhancementCompanionLifetimeCts.IsCancellationRequested)
            return;
        SetCompanionControlsPending(true);
        CompanionControlStatusText.Text = "サーバーに接続しています…";
        try
        {
            // Starting the API is not consent to recover, drain, resume or wake Jobs.
            EnhancementApiResponse response = await EnsureEnhancementCompanionApiReadyAsync(
                token: _enhancementCompanionLifetimeCts.Token,
                recoverQueueBeforeHealth: false);
            if (response.Ok && response.Payload is JsonElement payload
                && TryParseEnhancementQueueHealth(payload, out EnhancementQueueHealthView health))
            {
                ApplyEnhancementQueueHealth(health);
                CompanionControlStatusText.Text = "接続済み（キューの再開は別操作）";
            }
            else
                CompanionControlStatusText.Text = "接続できませんでした。閲覧は継続できます。";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            CompanionControlStatusText.Text = "サーバーを起動できませんでした。";
        }
        finally { SetCompanionControlsPending(false); }
    }

    private void SetCompanionControlsPending(bool pending)
    {
        _companionControlPending = pending;
        CompanionStartButton.IsEnabled = !pending;
        CompanionRestartButton.IsEnabled = !pending;
        CompanionStopButton.IsEnabled = !pending;
    }

    private async void StopCompanion_Click(object sender, RoutedEventArgs e)
        => await StopCompanionFromJobsAsync(restart: false);

    private async void RestartCompanion_Click(object sender, RoutedEventArgs e)
        => await StopCompanionFromJobsAsync(restart: true);

    private async Task StopCompanionFromJobsAsync(bool restart)
    {
        if (_companionControlPending) return;
        if (MessageBox.Show(this,
            "サーバーを停止すると実行中のAI処理が中断される場合があります。Jobsの記録は削除しません。\n続けますか？",
            restart ? "サーバーを再起動" : "サーバーを停止",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetCompanionControlsPending(true);
        bool stopped = false;
        bool locked = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_enhancementCompanionLifetimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _enhancementCompanionLaunchGate.WaitAsync(timeout.Token);
            locked = true;
            if (!TryGetOrCreateEnhancementCompanionAuthToken(out string authToken, out _))
                throw new InvalidOperationException();
            EnhancementCompanionOwnershipProbe proof =
                await ProbeEnhancementCompanionOwnershipAsync(authToken, timeout.Token);
            if (!proof.Verified || proof.Payload is not JsonElement identity)
                throw new InvalidOperationException();
            using Process observed = Process.GetProcessById(identity.GetProperty("processId").GetInt32());
            // Pin the OS process handle, then freshly prove the same server epoch.
            _ = observed.Handle;
            EnhancementCompanionOwnershipProbe repeated =
                await ProbeEnhancementCompanionOwnershipAsync(authToken, timeout.Token);
            if (!repeated.Verified || repeated.Payload is not JsonElement current
                || observed.HasExited
                || current.GetProperty("processId").GetInt32() != observed.Id
                || current.GetProperty("instanceId").GetString() != identity.GetProperty("instanceId").GetString()
                || current.GetProperty("serverStartedAtUtc").GetString() != identity.GetProperty("serverStartedAtUtc").GetString())
                throw new InvalidOperationException();
            // Explicit Stop/Restart consent, distinct from automatic window-close policy.
            observed.Kill(entireProcessTree: true);
            await observed.WaitForExitAsync(timeout.Token);
            if (_ownedEnhancementCompanion?.Id == observed.Id) ReleaseOwnedEnhancementCompanion();
            _enhancementCompanionOwnershipVerified = false;
            _verifiedEnhancementCompanionInstanceId = null;
            _verifiedEnhancementCompanionServerStartedAtUtc = null;
            stopped = true;
            ApplyEnhancementQueueHealthUnavailable("サーバーを停止しました。Jobsの記録は保持されています。");
            CompanionControlStatusText.Text = "サーバー停止済み";
        }
        catch (Exception)
        {
            CompanionControlStatusText.Text = "停止を確認できませんでした。別のサーバーは停止しません。";
        }
        finally
        {
            if (locked) _enhancementCompanionLaunchGate.Release();
            SetCompanionControlsPending(false);
        }
        if (stopped && restart) await StartCompanionApiOnlyAsync();
    }
}

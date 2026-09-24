using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private bool _companionControlPending;
    private CancellationTokenSource? _companionControlCts;

    public Task StartEnhancementCompanionApiForApplicationLaunchAsync()
        => StartCompanionApiOnlyAsync();

    private async void StartCompanion_Click(object sender, RoutedEventArgs e)
        => await StartCompanionApiOnlyAsync();

    private async Task StartCompanionApiOnlyAsync()
    {
        if (_companionControlPending || _enhancementCompanionLifetimeCts.IsCancellationRequested)
            return;
        using var control = CancellationTokenSource.CreateLinkedTokenSource(_enhancementCompanionLifetimeCts.Token);
        control.CancelAfter(TimeSpan.FromSeconds(45));
        _companionControlCts = control;
        SetCompanionControlsPending(true);
        CompanionControlStatusText.Text = "サーバーに接続しています…";
        try
        {
            // Starting the API is not consent to recover, drain, resume or wake Jobs.
            EnhancementApiResponse response = await EnsureEnhancementCompanionApiReadyAsync(
                token: control.Token,
                preparation: EnhancementApiPreparation.Health);
            control.Token.ThrowIfCancellationRequested();
            if (response.Ok && response.Payload is JsonElement payload
                && TryParseEnhancementQueueHealth(payload, out EnhancementQueueHealthView health))
            {
                ApplyEnhancementQueueHealth(health);
                CompanionControlStatusText.Text = "サーバー接続済み";
            }
            else if (response.InnerStatusAuthoritative
                && EnhancementApiErrorCode(response) == "QUEUE_HEALTH_UNAVAILABLE")
            {
                ApplyCompanionQueueRecoveryRequired();
            }
            else
            {
                ApplyEnhancementQueueHealthUnavailable("サーバーに接続できません。起動または再起動を試してください。");
                CompanionControlStatusText.Text = "接続できませんでした。起動・再起動をもう一度試せます。";
            }
        }
        catch (OperationCanceledException)
        {
            CompanionControlStatusText.Text = "接続待ちを終了しました。起動・再起動をもう一度試せます。";
        }
        catch (Exception)
        {
            CompanionControlStatusText.Text = "サーバーを起動できませんでした。";
        }
        finally
        {
            _companionControlCts = null;
            SetCompanionControlsPending(false);
        }
    }

    private void SetCompanionControlsPending(bool pending)
    {
        _companionControlPending = pending;
        CompanionStartButton.IsEnabled = !pending;
        CompanionRestartButton.IsEnabled = !pending;
        CompanionStopButton.IsEnabled = !pending;
        CompanionCancelButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        CompanionCancelButton.IsEnabled = pending;
        RefreshEnhancementQueuePauseControl();
    }

    private void CancelCompanionControl_Click(object sender, RoutedEventArgs e)
    {
        CompanionCancelButton.IsEnabled = false;
        CompanionControlStatusText.Text = "接続待ちを取り消しています…";
        _companionControlCts?.Cancel();
    }

    private async void StopCompanion_Click(object sender, RoutedEventArgs e)
        => await StopCompanionFromJobsAsync(restart: false);

    private async void RestartCompanion_Click(object sender, RoutedEventArgs e)
        => await StopCompanionFromJobsAsync(restart: true);

    private async Task StopCompanionFromJobsAsync(bool restart, bool confirmedForSmoke = false)
    {
        if (_companionControlPending) return;
        if (!confirmedForSmoke && MessageBox.Show(this,
            "サーバーを停止すると実行中のAI処理が中断される場合があります。Jobsの記録は削除しません。\n続けますか？",
            restart ? "サーバーを再起動" : "サーバーを停止",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetCompanionControlsPending(true);
        bool stopped = false;
        bool locked = false;
        try
        {
            InvalidateEnhancementCompanionOperations();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_enhancementCompanionLifetimeCts.Token);
            _companionControlCts = timeout;
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _enhancementCompanionLaunchGate.WaitAsync(timeout.Token);
            locked = true;
            // Only retire a missing/exited owned epoch; retain live-owner constraints.
            RetireExitedEnhancementCompanion();
            if (!TryGetOrCreateEnhancementCompanionAuthToken(out string authToken, out _))
                throw new InvalidOperationException();
            EnhancementCompanionOwnershipProbe proof =
                await ProbeEnhancementCompanionOwnershipAsync(authToken, timeout.Token);
            // With no owned process and no response there is nothing we may
            // signal. Restart can still try the normal authenticated startup.
            if (proof.TransportUnavailable && _ownedEnhancementCompanion is null)
            {
                stopped = true;
                ApplyEnhancementQueueHealthUnavailable("サーバーは応答していません。Jobsの記録は保持されています。");
                CompanionControlStatusText.Text = "サーバーから応答がありません";
            }
            else
            {
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
        }
        catch (OperationCanceledException)
        {
            CompanionControlStatusText.Text = "停止の確認を終了しました。再操作できます。";
        }
        catch (Exception)
        {
            CompanionControlStatusText.Text = "停止を確認できませんでした。別のサーバーは停止しません。";
        }
        finally
        {
            if (locked) _enhancementCompanionLaunchGate.Release();
            _companionControlCts = null;
            SetCompanionControlsPending(false);
        }
        if (stopped && restart) await StartCompanionApiOnlyAsync();
    }

    private void ApplyCompanionQueueRecoveryRequired()
    {
        ApplyEnhancementQueueHealthUnavailable("キューの状態を読めません。「復旧して再開」で中断した処理を確認し、待機順を保って再開します。");
        EnhancementJobsHealthStateText.Text = "キューの復旧待ち";
        CompanionControlStatusText.Text = "サーバー接続済み";
    }

    public async Task<bool> AuthenticatedCompanionStopForSmokeAsync()
    {
        // A short-lived synthetic process, no listener, media, or user state.
        var start = new ProcessStartInfo(System.IO.Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Threading.Thread]::Sleep(30000)");
        using Process child = Process.Start(start)!;
        string epoch = DateTimeOffset.UtcNow.ToString("O");
        int scenario = 0;
        int requests = 0;
        ConfigureEnhancementCompanionAutoStartForSmoke((request, token) =>
        {
            requests++;
            string challenge = request.Headers.GetValues(EnhancementCompanionChallengeHeader).Single();
            var payload = new Dictionary<string, object>(EnhancementCompanionIdentityPayloadForSmoke(
                challenge, child.Id, scenario == 1 && requests == 2 ? DateTimeOffset.UtcNow.ToString("O") : epoch));
            if (scenario == 0) payload["proof"] = "invalid";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(payload)) });
        }, _ => throw new InvalidOperationException("Stop must never launch a server."));
        try
        {
            await StopCompanionFromJobsAsync(false, confirmedForSmoke: true);
            bool unknownPreserved = !child.HasExited && requests == 1;
            scenario = 1;
            requests = 0;
            await StopCompanionFromJobsAsync(false, confirmedForSmoke: true);
            bool changedEpochPreserved = !child.HasExited && requests == 2;
            var expiredStart = new ProcessStartInfo(start.FileName)
            { UseShellExecute = false, CreateNoWindow = true };
            expiredStart.ArgumentList.Add("-NoProfile");
            expiredStart.ArgumentList.Add("-Command");
            expiredStart.ArgumentList.Add("exit 0");
            _ownedEnhancementCompanion = Process.Start(expiredStart)!;
            await _ownedEnhancementCompanion.WaitForExitAsync();
            _ownedEnhancementCompanionInstanceId = "expired-synthetic-owner";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _beforeCompanionRecoveryForSmoke = async _ =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            CancellationToken oldEpoch = CaptureEnhancementCompanionOperationToken();
            KickEnhancementCompanionRecoveryAfterDurablePublish(null,
                "00000000-0000-4000-8000-000000000003", actionEpoch: oldEpoch);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task priorRecovery = _enhancementCompanionDurableRecoveryTask!;
            scenario = 2;
            requests = 0;
            await StopCompanionFromJobsAsync(false, confirmedForSmoke: true);
            release.TrySetResult();
            await priorRecovery.WaitAsync(TimeSpan.FromSeconds(10));
            // A late pre-stop publisher/finally must not reschedule into the new epoch.
            KickEnhancementCompanionRecoveryAfterDurablePublish(null,
                "00000000-0000-4000-8000-000000000003", actionEpoch: oldEpoch);
            bool passiveReplacementStopped;
            using (Process replacement = Process.Start(start)!)
            {
                _ownedEnhancementCompanion = Process.GetProcessById(replacement.Id);
                _ownedEnhancementCompanionInstanceId = "new-api-only-synthetic-owner";
                _ownedEnhancementCompanionDurableWorkActivated = 0;
                string replacementEpoch = DateTimeOffset.UtcNow.ToString("O");
                int replacementRequests = 0;
                ConfigureEnhancementCompanionAutoStartForSmoke((request, token) =>
                {
                    replacementRequests++;
                    string challenge = request.Headers.GetValues(EnhancementCompanionChallengeHeader).Single();
                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(
                        EnhancementCompanionIdentityPayloadForSmoke(challenge, replacement.Id, replacementEpoch))) });
                }, _ => throw new InvalidOperationException("Old recovery must not start a replacement."));
                try
                {
                    EnhancementCompanionOwnershipProbe replacementProof =
                        await ProbeEnhancementCompanionOwnershipAsync(_enhancementCompanionAuthToken!, CancellationToken.None);
                    foreach (bool schedule in new[] { true, false })
                        KickEnhancementCompanionRecoveryAfterDurablePublish(null,
                            "00000000-0000-4000-8000-000000000003",
                            scheduleRecovery: schedule, actionEpoch: oldEpoch);
                    bool unchanged = replacementProof.Verified && replacementRequests == 1
                        && _ownedEnhancementCompanionDurableWorkActivated == 0;
                    CompleteOwnedEnhancementCompanionForApplicationClose();
                    passiveReplacementStopped = unchanged && replacement.WaitForExit(5000);
                }
                finally
                {
                    if (!replacement.HasExited) replacement.Kill();
                    ReleaseOwnedEnhancementCompanion();
                }
            }
            return unknownPreserved && changedEpochPreserved && child.HasExited && requests == 2
                && passiveReplacementStopped
                && oldEpoch.IsCancellationRequested
                && !CaptureEnhancementCompanionOperationToken().IsCancellationRequested
                && !_enhancementCompanionDurableRecoveryRequested
                && !_enhancementCompanionDurableRecoveryRunning;
        }
        finally { if (!child.HasExited) child.Kill(); }
    }
}

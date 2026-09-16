using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    public async Task<bool> CompanionRecoveryControlsForSmokeAsync(JsonElement validHealth, string screenshotPath)
    {
        bool listening = false;
        bool unavailable = false;
        int starts = 0, mutations = 0;
        void Configure() => ConfigureEnhancementCompanionAutoStartForSmoke(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/enhance/identity")
            {
                if (!listening) throw new HttpRequestException("Synthetic offline server.");
                string challenge = request.Headers.GetValues(EnhancementCompanionChallengeHeader).Single();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(EnhancementCompanionIdentityPayloadForSmoke(challenge))),
                };
            }
            var inner = await DecodeEnhancementCompanionSecureRequestForSmokeAsync(request, token);
            if (inner?.Method != "GET" || inner.PathAndQuery != "/api/enhance/health") mutations++;
            return unavailable
                ? EnhancementCompanionSecureResponseForSmoke(request, 503,
                    new { code = "QUEUE_HEALTH_UNAVAILABLE", error = "Synthetic unrecovered WAL." })
                : EnhancementCompanionSecureResponseForSmoke(request, 200, validHealth);
        }, _ => { starts++; listening = true; return (true, ""); });

        Configure();
        PrepareUnknownEnhancementQueueResumeForSmoke();
        await StopCompanionFromJobsAsync(restart: true, confirmedForSmoke: true);
        bool offlineRestart = starts == 1 && _enhancementWorkspaceQueuePaused.HasValue
            && CompanionStartButton.IsEnabled && !_companionControlPending;

        var exit = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        exit.ArgumentList.Add("-NoProfile");
        exit.ArgumentList.Add("-Command");
        exit.ArgumentList.Add("exit 0");
        _ownedEnhancementCompanion = Process.Start(exit)!;
        await _ownedEnhancementCompanion.WaitForExitAsync();
        _ownedEnhancementCompanionInstanceId = "expired-owner-must-not-block-reconnect";
        _enhancementCompanionOwnershipVerified = true;
        var probe = await EnsureEnhancementCompanionOwnershipForPassiveReadAsync(CancellationToken.None);
        bool deadEpochRetired = probe is null && _ownedEnhancementCompanion is null && starts == 1;

        unavailable = true;
        await StartCompanionApiOnlyAsync();
        await RefreshEnhancementQueueHealthAsync(_enhancementWorkspaceGeneration, isPoll: false);
        bool walRecoveryVisible = _enhancementWorkspaceQueuePaused is null
            && EnhancementJobsPauseResumeButton.IsEnabled
            && (string)EnhancementJobsPauseResumeButton.Content == "復旧して再開"
            && CompanionControlStatusText.Text.Contains("接続済み")
            && EnhancementJobsHealthStateText.Text == "キューの復旧待ち";
        RenderCompanionRecoveryForSmoke(screenshotPath);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureEnhancementCompanionAutoStartForSmoke(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation must interrupt the probe.");
        }, _ => throw new InvalidOperationException("A waiting probe must not launch a server."));
        Task waiting = StartCompanionApiOnlyAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bool cancelAvailable = CompanionCancelButton.IsEnabled
            && CompanionCancelButton.Visibility == Visibility.Visible
            && !EnhancementJobsPauseResumeButton.IsEnabled;
        CancelCompanionControl_Click(this, new RoutedEventArgs());
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        bool cancelRestored = !_companionControlPending && _companionControlCts is null
            && CompanionStartButton.IsEnabled && CompanionRestartButton.IsEnabled;
        var bounded = await ProbeEnhancementCompanionOwnershipAsync(
            _enhancementCompanionAuthToken!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        bool probeBounded = !bounded.Verified && bounded.TransportUnavailable;

        unavailable = false;
        Configure();
        await StartCompanionApiOnlyAsync();
        bool retryAfterCancel = _enhancementWorkspaceQueuePaused.HasValue && starts == 1;

        string root = Path.Combine(Path.GetDirectoryName(screenshotPath)!, "runtime-candidates");
        string dedicated = Path.Combine(root, "Aibos Image", "CompanionRuntime", "node.exe");
        string outside = Path.Combine(root, "user-node.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dedicated)!);
        File.WriteAllText(dedicated, "synthetic, never executed");
        File.WriteAllText(outside, "synthetic, never executed");
        bool runtimeBounded = ValidateNodeExecutableCandidateForSmoke(root, dedicated)
            && !ValidateNodeExecutableCandidateForSmoke(root, outside);
        return offlineRestart && deadEpochRetired && walRecoveryVisible && cancelAvailable
            && cancelRestored && probeBounded && retryAfterCancel && runtimeBounded && mutations == 0;
    }

    private void RenderCompanionRecoveryForSmoke(string path)
    {
        var panel = (FrameworkElement)((FrameworkElement)((FrameworkElement)CompanionStartButton.Parent).Parent).Parent;
        panel.Measure(new Size(260, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 260, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var image = new RenderTargetBitmap(260, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}

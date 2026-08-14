using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int MaxTrackedEnhancementNotificationJobs = 4_096;
    private const int MaxPendingEnhancementNotifications = 8;
    private static readonly TimeSpan EnhancementNotificationDisplayDuration =
        TimeSpan.FromSeconds(6);

    private readonly Dictionary<string, string> _trackedEnhancementNotificationJobs =
        new(StringComparer.Ordinal);
    private readonly Queue<EnhancementNotificationPresentation>
        _pendingEnhancementNotifications = new();
    private EnhancementNotificationPreferences _enhancementNotificationPreferences =
        EnhancementNotificationPreferences.Default;
    private Dictionary<string, JsonElement>? _enhancementNotificationStateExtensionData;
    private DispatcherTimer _enhancementNotificationDismissTimer = null!;
    private bool _syncingEnhancementNotificationControls;
    private int _enhancementNotificationShownCount;

    private sealed record EnhancementNotificationJobState(
        string Id,
        string Operation,
        string Status);

    private sealed record EnhancementNotificationPresentation(
        string Title,
        string Message,
        bool Failed);

    private readonly record struct EnhancementNotificationPreferences(
        bool UpscaleSucceeded,
        bool UpscaleFailed,
        bool PhotorealSucceeded,
        bool PhotorealFailed,
        bool VideoSucceeded,
        bool VideoFailed)
    {
        public static EnhancementNotificationPreferences Default => new(
            UpscaleSucceeded: true,
            UpscaleFailed: true,
            PhotorealSucceeded: true,
            PhotorealFailed: true,
            VideoSucceeded: true,
            VideoFailed: true);

        public bool Allows(string operation, string status)
            => (operation, status) switch
            {
                ("upscale", "succeeded") => UpscaleSucceeded,
                ("upscale", "failed") => UpscaleFailed,
                ("photoreal", "succeeded") => PhotorealSucceeded,
                ("photoreal", "failed") => PhotorealFailed,
                ("video", "succeeded") => VideoSucceeded,
                ("video", "failed") => VideoFailed,
                _ => false,
            };

        public static EnhancementNotificationPreferences FromState(
            EnhancementNotificationState? state)
            => state is null
                ? Default
                : new(
                    state.UpscaleSucceeded ?? true,
                    state.UpscaleFailed ?? true,
                    state.PhotorealSucceeded ?? true,
                    state.PhotorealFailed ?? true,
                    state.VideoSucceeded ?? true,
                    state.VideoFailed ?? true);

        public EnhancementNotificationState ToState(
            Dictionary<string, JsonElement>? extensionData)
            => new()
            {
                UpscaleSucceeded = UpscaleSucceeded,
                UpscaleFailed = UpscaleFailed,
                PhotorealSucceeded = PhotorealSucceeded,
                PhotorealFailed = PhotorealFailed,
                VideoSucceeded = VideoSucceeded,
                VideoFailed = VideoFailed,
                ExtensionData = CloneExtensionData(extensionData),
            };
    }

    private void InitializeEnhancementNotifications()
    {
        _enhancementNotificationDismissTimer = new DispatcherTimer(
            DispatcherPriority.Background)
        {
            Interval = EnhancementNotificationDisplayDuration,
        };
        _enhancementNotificationDismissTimer.Tick += (_, _) =>
        {
            if (WindowState == WindowState.Minimized || !IsActive)
            {
                _enhancementNotificationDismissTimer.Stop();
                _enhancementNotificationDismissTimer.Start();
                return;
            }
            _enhancementNotificationDismissTimer.Stop();
            HideCurrentEnhancementNotification(showNext: true);
        };
    }

    private void StopEnhancementNotifications()
    {
        _enhancementNotificationDismissTimer.Stop();
        _pendingEnhancementNotifications.Clear();
    }

    private static bool IsEnhancementNotificationOperation(string operation)
        => operation is "upscale" or "photoreal" or "video";

    private void RestoreEnhancementNotificationPreferences(ViewerState? state)
    {
        _enhancementNotificationStateExtensionData = CloneExtensionData(
            state?.EnhancementNotifications?.ExtensionData);
        SetEnhancementNotificationPreferences(
            EnhancementNotificationPreferences.FromState(
                state?.EnhancementNotifications),
            persist: false);
    }

    private void EnhancementNotificationPreference_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_initializing || _syncingEnhancementNotificationControls)
            return;

        SetEnhancementNotificationPreferences(new(
            UpscaleSucceeded:
                NotifyUpscaleSucceededCheckBox.IsChecked == true,
            UpscaleFailed:
                NotifyUpscaleFailedCheckBox.IsChecked == true,
            PhotorealSucceeded:
                NotifyPhotorealSucceededCheckBox.IsChecked == true,
            PhotorealFailed:
                NotifyPhotorealFailedCheckBox.IsChecked == true,
            VideoSucceeded:
                NotifyVideoSucceededCheckBox.IsChecked == true,
            VideoFailed:
                NotifyVideoFailedCheckBox.IsChecked == true),
            persist: true);
    }

    private void SetEnhancementNotificationPreferences(
        EnhancementNotificationPreferences preferences,
        bool persist)
    {
        _enhancementNotificationPreferences = preferences;
        _syncingEnhancementNotificationControls = true;
        try
        {
            NotifyUpscaleSucceededCheckBox.IsChecked =
                preferences.UpscaleSucceeded;
            NotifyUpscaleFailedCheckBox.IsChecked = preferences.UpscaleFailed;
            NotifyPhotorealSucceededCheckBox.IsChecked =
                preferences.PhotorealSucceeded;
            NotifyPhotorealFailedCheckBox.IsChecked =
                preferences.PhotorealFailed;
            NotifyVideoSucceededCheckBox.IsChecked = preferences.VideoSucceeded;
            NotifyVideoFailedCheckBox.IsChecked = preferences.VideoFailed;
        }
        finally
        {
            _syncingEnhancementNotificationControls = false;
        }

        if (persist)
            SaveState();
    }

    private void TrackActiveEnhancementNotificationJob(JsonElement job)
    {
        if (!TryGetStringProperty(job, "id", out string? id)
            || !TryGetStringProperty(job, "status", out string? status)
            || status is not ("queued" or "running"))
        {
            return;
        }
        TrackActiveEnhancementNotificationJob(id!, ReadEnhancementOperation(job));
    }

    private void TrackActiveEnhancementNotificationJob(
        string id,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !IsEnhancementNotificationOperation(operation))
        {
            return;
        }
        if (_trackedEnhancementNotificationJobs.ContainsKey(id))
        {
            _trackedEnhancementNotificationJobs[id] = operation;
            return;
        }
        if (_trackedEnhancementNotificationJobs.Count
            >= MaxTrackedEnhancementNotificationJobs)
        {
            string oldest = _trackedEnhancementNotificationJobs.Keys.First();
            _trackedEnhancementNotificationJobs.Remove(oldest);
        }
        _trackedEnhancementNotificationJobs[id] = operation;
    }

    private IReadOnlySet<string> SnapshotTrackedEnhancementNotificationJobIds()
        => _trackedEnhancementNotificationJobs.Keys.ToHashSet(
            StringComparer.Ordinal);

    private void ApplyEnhancementNotificationSnapshot(
        IReadOnlyList<EnhancementNotificationJobState> activeJobs,
        IReadOnlyList<EnhancementNotificationJobState> terminalJobs)
    {
        foreach (EnhancementNotificationJobState terminal in terminalJobs)
        {
            if (!_trackedEnhancementNotificationJobs.Remove(
                    terminal.Id,
                    out _))
            {
                continue;
            }
            if (IsEnhancementNotificationOperation(terminal.Operation)
                && terminal.Status is "succeeded" or "failed"
                && _enhancementNotificationPreferences.Allows(
                    terminal.Operation,
                    terminal.Status))
            {
                EnqueueEnhancementNotification(
                    terminal.Operation,
                    terminal.Status);
            }
        }

        foreach (EnhancementNotificationJobState active in activeJobs)
            TrackActiveEnhancementNotificationJob(active.Id, active.Operation);
    }

    private void EnqueueEnhancementNotification(string operation, string status)
    {
        string operationLabel = operation switch
        {
            "upscale" => "高画質化",
            "photoreal" => "実写化",
            "video" => "動画化",
            _ => "AI処理",
        };
        bool failed = status == "failed";
        var presentation = new EnhancementNotificationPresentation(
            failed
                ? $"{operationLabel}に失敗しました"
                : $"{operationLabel}が完了しました",
            failed
                ? "Jobsで内容を確認し、必要なら現在設定でリトライできます。"
                : "結果はJobsまたは元画像のバージョン切替から確認できます。",
            failed);

        if (EnhancementResultToast.Visibility != Visibility.Visible)
        {
            ShowEnhancementNotification(presentation);
            return;
        }
        if (_pendingEnhancementNotifications.Count
            < MaxPendingEnhancementNotifications)
        {
            _pendingEnhancementNotifications.Enqueue(presentation);
        }
    }

    private void ShowEnhancementNotification(
        EnhancementNotificationPresentation presentation)
    {
        EnhancementNotificationTitleText.Text = presentation.Title;
        EnhancementNotificationMessageText.Text = presentation.Message;
        EnhancementResultToastBorder.BorderBrush = (Brush)FindResource(
            presentation.Failed ? "Danger" : "Success");
        AutomationProperties.SetName(
            EnhancementResultToast,
            $"{presentation.Title}。{presentation.Message}");
        EnhancementResultToast.Visibility = Visibility.Visible;
        _enhancementNotificationShownCount++;
        _enhancementNotificationDismissTimer.Stop();
        _enhancementNotificationDismissTimer.Start();
    }

    private void HideCurrentEnhancementNotification(bool showNext)
    {
        _enhancementNotificationDismissTimer.Stop();
        EnhancementResultToast.Visibility = Visibility.Collapsed;
        if (showNext && _pendingEnhancementNotifications.TryDequeue(
                out EnhancementNotificationPresentation? next))
        {
            ShowEnhancementNotification(next);
        }
    }

    private void DismissEnhancementNotification_Click(
        object sender,
        RoutedEventArgs e)
        => HideCurrentEnhancementNotification(showNext: true);

    public bool EnhancementNotificationSurfaceContractForSmoke
        => string.Equals(
                AutomationProperties.GetName(EnhancementResultToast),
                "AI processing result notification",
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(
                    NotifyUpscaleSucceededCheckBox),
                "Notify when upscale completes",
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(NotifyVideoFailedCheckBox),
                "Notify when video generation fails",
                StringComparison.Ordinal);

    public void SetEnhancementNotificationPreferencesForSmoke(
        bool upscaleSucceeded,
        bool upscaleFailed,
        bool photorealSucceeded,
        bool photorealFailed,
        bool videoSucceeded,
        bool videoFailed)
        => SetEnhancementNotificationPreferences(new(
            upscaleSucceeded,
            upscaleFailed,
            photorealSucceeded,
            photorealFailed,
            videoSucceeded,
            videoFailed),
            persist: true);

    public void TrackEnhancementNotificationJobForSmoke(
        string id,
        string operation)
        => TrackActiveEnhancementNotificationJob(id, operation);

    public void ApplyEnhancementNotificationTerminalForSmoke(
        string id,
        string operation,
        string status)
        => ApplyEnhancementNotificationSnapshot(
            [],
            [new EnhancementNotificationJobState(id, operation, status)]);

    public void DismissEnhancementNotificationForSmoke()
        => HideCurrentEnhancementNotification(showNext: true);

    public bool EnhancementNotificationVisibleForSmoke
        => EnhancementResultToast.Visibility == Visibility.Visible;
    public string EnhancementNotificationTitleForSmoke
        => EnhancementNotificationTitleText.Text;
    public string EnhancementNotificationMessageForSmoke
        => EnhancementNotificationMessageText.Text;
    public int EnhancementNotificationShownCountForSmoke
        => _enhancementNotificationShownCount;
    public int PendingEnhancementNotificationCountForSmoke
        => _pendingEnhancementNotifications.Count;

    public static string? ReadEnhancementNotificationOperationForSmoke(
        string jobsPath,
        string jobId)
    {
        using JsonDocument document = OpenEnhancementJobsDocument(
            Path.GetFullPath(jobsPath),
            out _);
        if (!document.RootElement.TryGetProperty(
                "jobs",
                out JsonElement jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement job in jobs.EnumerateArray())
        {
            if (TryGetStringProperty(job, "id", out string? id)
                && string.Equals(id, jobId, StringComparison.Ordinal))
            {
                return ReadEnhancementOperation(job);
            }
        }
        return null;
    }
}

public sealed class EnhancementNotificationState
{
    public bool? UpscaleSucceeded { get; set; }
    public bool? UpscaleFailed { get; set; }
    public bool? PhotorealSucceeded { get; set; }
    public bool? PhotorealFailed { get; set; }
    public bool? VideoSucceeded { get; set; }
    public bool? VideoFailed { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

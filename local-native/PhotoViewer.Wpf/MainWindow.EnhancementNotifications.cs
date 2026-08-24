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
        bool VideoFailed,
        bool VideoEditSucceeded,
        bool VideoEditFailed,
        bool VideoFinishSucceeded,
        bool VideoFinishFailed)
    {
        public static EnhancementNotificationPreferences Default => new(
            UpscaleSucceeded: true,
            UpscaleFailed: true,
            PhotorealSucceeded: true,
            PhotorealFailed: true,
            VideoSucceeded: true,
            VideoFailed: true,
            VideoEditSucceeded: true,
            VideoEditFailed: true,
            VideoFinishSucceeded: true,
            VideoFinishFailed: true);

        public bool Allows(string operation, string status)
            => (operation, status) switch
            {
                ("upscale", "succeeded") => UpscaleSucceeded,
                ("upscale", "failed") => UpscaleFailed,
                ("photoreal", "succeeded") => PhotorealSucceeded,
                ("photoreal", "failed") => PhotorealFailed,
                ("video", "succeeded") => VideoSucceeded,
                ("video", "failed") => VideoFailed,
                ("video-edit", "succeeded") => VideoEditSucceeded,
                ("video-edit", "failed") => VideoEditFailed,
                ("video-finish", "succeeded") => VideoFinishSucceeded,
                ("video-finish", "failed") => VideoFinishFailed,
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
                    state.VideoFailed ?? true,
                    state.VideoEditSucceeded
                        ?? state.VideoSucceeded
                        ?? true,
                    state.VideoEditFailed
                        ?? state.VideoFailed
                        ?? true,
                    state.VideoFinishSucceeded
                        ?? state.VideoSucceeded
                        ?? true,
                    state.VideoFinishFailed
                        ?? state.VideoFailed
                        ?? true);

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
                VideoEditSucceeded = VideoEditSucceeded,
                VideoEditFailed = VideoEditFailed,
                VideoFinishSucceeded = VideoFinishSucceeded,
                VideoFinishFailed = VideoFinishFailed,
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
            if (WindowState == WindowState.Minimized)
            {
                _enhancementNotificationDismissTimer.Stop();
                return;
            }
            if (!IsActive)
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
        => operation is "upscale"
            or "photoreal"
            or "video"
            or "video-edit"
            or "video-finish";

    private static string ReadEnhancementNotificationOperation(
        JsonElement job)
    {
        if (TryReadVideoToolsV2WorkspaceSnapshot(
                job,
                out VideoToolsV2ReaderSnapshot snapshot))
        {
            return snapshot.Kind switch
            {
                "edit" => "video-edit",
                "finish" => "video-finish",
                _ => UnsupportedEnhancementOperation,
            };
        }

        // Any Video Tools-shaped row which is not an exact current v2
        // snapshot stays unclassified. In particular, malformed and future
        // rows must never inherit ordinary video-generation notifications.
        if (ClaimsVideoToolsWorkspaceSnapshot(job))
            return UnsupportedEnhancementOperation;

        return ReadEnhancementOperation(job);
    }

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
                NotifyVideoFailedCheckBox.IsChecked == true,
            VideoEditSucceeded:
                NotifyVideoEditSucceededCheckBox.IsChecked == true,
            VideoEditFailed:
                NotifyVideoEditFailedCheckBox.IsChecked == true,
            VideoFinishSucceeded:
                NotifyVideoFinishSucceededCheckBox.IsChecked == true,
            VideoFinishFailed:
                NotifyVideoFinishFailedCheckBox.IsChecked == true),
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
            NotifyVideoEditSucceededCheckBox.IsChecked =
                preferences.VideoEditSucceeded;
            NotifyVideoEditFailedCheckBox.IsChecked =
                preferences.VideoEditFailed;
            NotifyVideoFinishSucceededCheckBox.IsChecked =
                preferences.VideoFinishSucceeded;
            NotifyVideoFinishFailedCheckBox.IsChecked =
                preferences.VideoFinishFailed;
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
        TrackActiveEnhancementNotificationJob(
            id!,
            ReadEnhancementNotificationOperation(job));
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
                    out string? trackedOperation))
            {
                continue;
            }
            if (string.Equals(
                    trackedOperation,
                    terminal.Operation,
                    StringComparison.Ordinal)
                && IsEnhancementNotificationOperation(terminal.Operation)
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
            "upscale" => EnhancementNotificationText(
                "UiNotifyKindUpscale",
                "高画質化"),
            "photoreal" => EnhancementNotificationText(
                "UiNotifyKindPhotoreal",
                "実写化"),
            "video" => EnhancementNotificationText(
                "UiNotifyKindVideoGeneration",
                "AI動画生成"),
            "video-edit" => EnhancementNotificationText(
                "UiNotifyKindVideoEdit",
                "AI動画編集"),
            "video-finish" => EnhancementNotificationText(
                "UiNotifyKindVideoFinish",
                "AI動画高画質化"),
            _ => EnhancementNotificationText(
                "UiNotifyKindAiProcessing",
                "AI処理"),
        };
        bool failed = status == "failed";
        var presentation = new EnhancementNotificationPresentation(
            failed
                ? string.Format(
                    EnhancementNotificationText(
                        "UiNotifyFailedTitleFormat",
                        "{0}に失敗しました"),
                    operationLabel)
                : string.Format(
                    EnhancementNotificationText(
                        "UiNotifySucceededTitleFormat",
                        "{0}が完了しました"),
                    operationLabel),
            failed
                ? EnhancementNotificationText(
                    "UiNotifyFailedMessage",
                    "Jobsで内容を確認し、必要なら現在設定でリトライできます。")
                : operation is "video" or "video-edit" or "video-finish"
                    ? EnhancementNotificationText(
                        "UiNotifyVideoSucceededMessage",
                        "結果はJobsまたは動画のバージョン切替から確認できます。")
                    : EnhancementNotificationText(
                        "UiNotifyImageSucceededMessage",
                        "結果はJobsまたは元画像のバージョン切替から確認できます。"),
            failed);

        QueueEnhancementNotification(presentation);
    }

    private string EnhancementNotificationText(string key, string fallback)
        => TryFindResource(key) is string text
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : fallback;

    private bool ShowVideoEditCompileReviewNotification()
    {
        if (!_enhancementNotificationPreferences.Allows(
                "video-edit",
                "succeeded"))
        {
            return false;
        }

        QueueEnhancementNotification(new(
            EnhancementNotificationText(
                "UiNotifyVideoEditCompileReadyTitle",
                "編集指示を整えました。変換結果を確認してください"),
            EnhancementNotificationText(
                "UiNotifyVideoEditCompileReadyMessage",
                "変換後の指示と日本語の要約を確認してから開始できます。"),
            Failed: false));
        return true;
    }

    private bool ShowVideoEditStartAcceptedNotification(
        bool skipReviewAuthorization,
        bool acceptedOrSaved)
    {
        if (!skipReviewAuthorization
            || !acceptedOrSaved
            || !_enhancementNotificationPreferences.Allows(
                "video-edit",
                "succeeded"))
        {
            return false;
        }

        QueueEnhancementNotification(new(
            EnhancementNotificationText(
                "UiNotifyVideoEditStartedTitle",
                "AI動画編集を開始しました"),
            EnhancementNotificationText(
                "UiNotifyVideoEditStartedMessage",
                "進み具合とキャンセルはJobsで確認できます。"),
            Failed: false));
        return true;
    }

    private void QueueEnhancementNotification(
        EnhancementNotificationPresentation presentation)
    {

        _ = ShowAiProcessingTrayNotification(
            presentation.Title,
            presentation.Message,
            presentation.Failed);

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
        if (WindowState != WindowState.Minimized)
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
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(
                    NotifyVideoEditSucceededCheckBox),
                "Notify when AI video edit completes",
                StringComparison.Ordinal)
            && string.Equals(
                AutomationProperties.GetName(
                    NotifyVideoFinishFailedCheckBox),
                "Notify when AI video enhancement fails",
                StringComparison.Ordinal);

    public void SetEnhancementNotificationPreferencesForSmoke(
        bool upscaleSucceeded,
        bool upscaleFailed,
        bool photorealSucceeded,
        bool photorealFailed,
        bool videoSucceeded,
        bool videoFailed,
        bool videoEditSucceeded = true,
        bool videoEditFailed = true,
        bool videoFinishSucceeded = true,
        bool videoFinishFailed = true)
        => SetEnhancementNotificationPreferences(new(
            upscaleSucceeded,
            upscaleFailed,
            photorealSucceeded,
            photorealFailed,
            videoSucceeded,
            videoFailed,
            videoEditSucceeded,
            videoEditFailed,
            videoFinishSucceeded,
            videoFinishFailed),
            persist: true);

    public bool VideoEditSucceededPreferenceForSmoke
        => _enhancementNotificationPreferences.VideoEditSucceeded;
    public bool VideoEditFailedPreferenceForSmoke
        => _enhancementNotificationPreferences.VideoEditFailed;
    public bool VideoFinishSucceededPreferenceForSmoke
        => _enhancementNotificationPreferences.VideoFinishSucceeded;
    public bool VideoFinishFailedPreferenceForSmoke
        => _enhancementNotificationPreferences.VideoFinishFailed;

    public void TrackEnhancementNotificationJobForSmoke(
        string id,
        string operation)
        => TrackActiveEnhancementNotificationJob(id, operation);

    public void TrackEnhancementNotificationJobForSmoke(JsonElement job)
        => TrackActiveEnhancementNotificationJob(job);

    public void ApplyEnhancementNotificationTerminalForSmoke(
        string id,
        string operation,
        string status)
        => ApplyEnhancementNotificationSnapshot(
            [],
            [new EnhancementNotificationJobState(id, operation, status)]);

    public void ApplyEnhancementNotificationTerminalForSmoke(JsonElement job)
    {
        if (!TryGetStringProperty(job, "id", out string? id)
            || !TryGetStringProperty(job, "status", out string? status))
        {
            return;
        }
        ApplyEnhancementNotificationSnapshot(
            [],
            [new EnhancementNotificationJobState(
                id!,
                ReadEnhancementNotificationOperation(job),
                status!.ToLowerInvariant())]);
    }

    public bool NotifyVideoEditCompileReviewForSmoke(
        bool exactCandidateAccepted,
        bool stale,
        bool skipReviewAuthorization)
        => exactCandidateAccepted
            && !stale
            && !skipReviewAuthorization
            && ShowVideoEditCompileReviewNotification();

    public bool NotifyVideoEditStartAcceptedForSmoke(
        bool skipReviewAuthorization,
        bool acceptedOrSaved)
        => ShowVideoEditStartAcceptedNotification(
            skipReviewAuthorization,
            acceptedOrSaved);

    public static string ReadEnhancementNotificationKindForSmoke(
        JsonElement job)
        => ReadEnhancementNotificationOperation(job);

    public static bool MalformedEnhancementNotificationStateUsesSafeDefaultsForSmoke(
        string statePath)
    {
        if (TryReadViewerStateFile(
                Path.GetFullPath(statePath),
                out ViewerState? malformedState))
        {
            return false;
        }
        EnhancementNotificationPreferences preferences =
            EnhancementNotificationPreferences.FromState(
                malformedState?.EnhancementNotifications);
        return preferences.VideoSucceeded
            && preferences.VideoFailed
            && preferences.VideoEditSucceeded
            && preferences.VideoEditFailed
            && preferences.VideoFinishSucceeded
            && preferences.VideoFinishFailed;
    }

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
                return ReadEnhancementNotificationOperation(job);
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
    public bool? VideoEditSucceeded { get; set; }
    public bool? VideoEditFailed { get; set; }
    public bool? VideoFinishSucceeded { get; set; }
    public bool? VideoFinishFailed { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

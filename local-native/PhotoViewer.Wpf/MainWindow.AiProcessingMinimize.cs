using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const int WindowMessageAiProcessingTray = 0x8000 + 73;
    private const int WindowMessageLeftButtonUp = 0x0202;
    private const int WindowMessageLeftButtonDoubleClick = 0x0203;
    private const int NotifyIconSelect = 0x0400;
    private const int NotifyIconKeySelect = 0x0401;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconModify = 0x00000001;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconInfo = 0x00000010;
    private const uint NotifyIconInfoFlagInfo = 0x00000001;
    private const uint NotifyIconInfoFlagError = 0x00000003;
    private const uint AiProcessingTrayIconId = 1;

    private bool _aiProcessingMinimizedMode;
    private bool _aiProcessingTrayIconVisible;
    private bool _suppressAiProcessingTrayNativeCallsForSmoke;
    private bool _resumeSearchFilterAfterAiProcessingMinimize;
    private bool _resumeModalVideoAfterAiProcessingMinimize;
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;
    private WindowState _aiProcessingRestoreWindowState = WindowState.Normal;
    private bool _aiProcessingRestoreFakeMaximized;
    private uint _taskbarCreatedMessage;
    private nint _aiProcessingTrayIconHandle;
    private bool _aiProcessingTrayIconHandleOwned;
    private int _aiProcessingMinimizeEnterCount;
    private int _aiProcessingTrayAddCount;
    private int _aiProcessingTrayRemoveCount;
    private int _aiProcessingTrayNotificationCount;
    private string _lastAiProcessingTrayNotificationTitle = "";

    private void InitializeAiProcessingMinimize()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    private void DisposeAiProcessingMinimize()
    {
        RemoveAiProcessingTrayIcon();
        _aiProcessingMinimizedMode = false;
    }

    private bool HandleAiProcessingTrayMessage(
        int message,
        nint lParam,
        ref bool handled)
    {
        if (message == WindowMessageAiProcessingTray)
        {
            int trayMessage = unchecked((int)((long)lParam & 0xffff));
            if (trayMessage is WindowMessageLeftButtonUp
                or WindowMessageLeftButtonDoubleClick
                or NotifyIconSelect
                or NotifyIconKeySelect)
            {
                Dispatcher.BeginInvoke(
                    RestoreFromAiProcessingMinimize,
                    DispatcherPriority.Input);
            }
            handled = true;
            return true;
        }

        if (_taskbarCreatedMessage != 0
            && unchecked((uint)message) == _taskbarCreatedMessage
            && _aiProcessingMinimizedMode)
        {
            _aiProcessingTrayIconVisible = false;
            bool trayVisible = TryShowAiProcessingTrayIcon();
            ShowInTaskbar = !trayVisible;
            handled = true;
            return true;
        }

        return false;
    }

    private void EnterAiProcessingMinimize()
    {
        if (_aiProcessingMinimizedMode)
            return;

        _aiProcessingMinimizedMode = true;
        _aiProcessingMinimizeEnterCount++;
        _aiProcessingRestoreWindowState = _lastNonMinimizedWindowState
            == WindowState.Minimized
                ? WindowState.Normal
                : _lastNonMinimizedWindowState;
        _aiProcessingRestoreFakeMaximized = _fakeMaximized;
        _resumeSearchFilterAfterAiProcessingMinimize =
            _searchFilterTimer.IsEnabled;
        _resumeModalVideoAfterAiProcessingMinimize =
            _modalShowingVideo && _modalVideoPlaying;

        _searchFilterTimer.Stop();
        CommitPendingGalleryWheelZoom();
        StopGalleryAutoScroll();
        _modalFeedbackTimer.Stop();
        _modalFavoritePulseTimer.Stop();
        _modalChromeTransientTimer.Stop();
        _modalTransformQualityTimer.Stop();
        _modalEnhancementPollTimer.Stop();
        _modalEnhancementGeneration++;
        CancelModalEnhancementRefreshRequest();
        _enhancementWorkspacePollTimer.Stop();
        _enhancementWorkspaceThumbnailViewportTimer.Stop();
        _enhancementWorkspaceThumbnailViewportLoadPending = false;
        Volatile.Read(ref _enhancementWorkspaceThumbnailCts)?.Cancel();
        CancelThumbnailViewportLoading();
        CancelPreviewTabHoverDecode();
        if (_thumbnailPreferenceRefreshOperation is
            { Status: DispatcherOperationStatus.Pending } pendingRefresh)
        {
            pendingRefresh.Abort();
            _thumbnailPreferenceRefreshOperation = null;
        }
        PauseModalVideoForAiProcessingMinimize();
        _enhancementNotificationDismissTimer.Stop();

        bool trayVisible = TryShowAiProcessingTrayIcon();
        ShowInTaskbar = !trayVisible;
    }

    private void ExitAiProcessingMinimize()
    {
        if (!_aiProcessingMinimizedMode)
            return;

        _aiProcessingMinimizedMode = false;
        ShowInTaskbar = true;
        RemoveAiProcessingTrayIcon();

        if (_resumeSearchFilterAfterAiProcessingMinimize)
            _searchFilterTimer.Start();
        _resumeSearchFilterAfterAiProcessingMinimize = false;

        if (EnhancementResultToast.Visibility == Visibility.Visible)
        {
            _enhancementNotificationDismissTimer.Stop();
            _enhancementNotificationDismissTimer.Start();
        }

        ResumeModalVideoAfterAiProcessingMinimize();
        Dispatcher.BeginInvoke(
            ResumeInteractivePresentationAfterAiProcessingMinimize,
            DispatcherPriority.Loaded);
    }

    private void RestoreFromAiProcessingMinimize()
    {
        if (!_aiProcessingMinimizedMode)
            return;

        ShowInTaskbar = true;
        WindowState target = _aiProcessingRestoreWindowState == WindowState.Minimized
            ? WindowState.Normal
            : _aiProcessingRestoreWindowState;
        WindowState = target;
        if (_aiProcessingMinimizedMode)
        {
            ExitAiProcessingMinimize();
            _fakeMaximized = _aiProcessingRestoreFakeMaximized;
            _lastNonMinimizedWindowState = target;
            UpdateWindowMaximizePresentation();
        }
        _ = Activate();
    }

    internal void ActivateFromSecondaryInstance()
    {
        if (_aiProcessingMinimizedMode)
        {
            RestoreFromAiProcessingMinimize();
        }
        else
        {
            ShowInTaskbar = true;
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = _lastNonMinimizedWindowState == WindowState.Minimized
                    ? WindowState.Normal
                    : _lastNonMinimizedWindowState;
            }
            _ = Activate();
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            Hide();
            ShowInTaskbar = true;
            Show();
            handle = new WindowInteropHelper(this).Handle;
            _ = Activate();
        }
        if (handle != 0)
            _ = SetForegroundWindow(handle);
    }

    private async void ResumeInteractivePresentationAfterAiProcessingMinimize()
    {
        if (_aiProcessingMinimizedMode || WindowState == WindowState.Minimized)
            return;

        if (RowsList.Visibility == Visibility.Visible)
        {
            ScheduleListThumbnailViewport();
        }
        else if (_galleryVirtualizingPanel is { } panel)
        {
            ScheduleThumbnailViewportRange(
                panel.FirstVisibleIndex,
                panel.LastVisibleIndex,
                panel.FirstRealizedIndex,
                panel.LastRealizedIndex);
        }

        if (EnhancementJobsDialog.Visibility == Visibility.Visible)
        {
            try
            {
                await RefreshEnhancementJobsWorkspaceAsync(
                    _enhancementWorkspaceGeneration,
                    isPoll: false);
            }
            catch
            {
                // A passive refresh failure remains visible in Jobs. It must
                // not make restore fail or mutate queue state.
            }
        }

        if (Modal.Visibility == Visibility.Visible
            && TryGetModalSourceTile(out Tile tile))
        {
            BeginModalEnhancementRefresh(tile.Path);
        }
    }

    private void PauseModalVideoForAiProcessingMinimize()
    {
        _modalVideoTimelineTimer?.Stop();
        _modalVideoAutoplayPending = false;
        if (!_modalShowingVideo || !_modalVideoPlaying)
            return;

        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Pause();
            }
            catch
            {
            }
        }
        _modalVideoPlaying = false;
        UpdateModalVideoPlaybackPresentation();
    }

    private void ResumeModalVideoAfterAiProcessingMinimize()
    {
        bool resume = _resumeModalVideoAfterAiProcessingMinimize;
        _resumeModalVideoAfterAiProcessingMinimize = false;
        if (!resume || !_modalShowingVideo)
            return;

        if (!_modalVideoTransportStubForSmoke)
        {
            try
            {
                ModalVideo.Play();
            }
            catch
            {
                return;
            }
        }
        _modalVideoPlaying = true;
        _modalVideoTimelineTimer?.Start();
        UpdateModalVideoPlaybackPresentation();
    }

    private bool TryShowAiProcessingTrayIcon()
    {
        if (_aiProcessingTrayIconVisible)
            return true;
        if (_suppressAiProcessingTrayNativeCallsForSmoke)
        {
            _aiProcessingTrayIconVisible = true;
            _aiProcessingTrayAddCount++;
            return true;
        }

        nint windowHandle = new System.Windows.Interop.WindowInteropHelper(this)
            .Handle;
        if (windowHandle == 0)
            return false;

        nint iconHandle = EnsureAiProcessingTrayIconHandle();
        if (iconHandle == 0)
            return false;

        NativeNotifyIconData data = CreateAiProcessingNotifyIconData(
            windowHandle,
            NotifyIconMessage | NotifyIconIcon | NotifyIconTip);
        data.IconHandle = iconHandle;
        data.Tip = "Aibos Image — AI processing continues";
        if (!ShellNotifyIcon(NotifyIconAdd, ref data))
        {
            ReleaseAiProcessingTrayIconHandle();
            return false;
        }

        _aiProcessingTrayIconVisible = true;
        _aiProcessingTrayAddCount++;
        return true;
    }

    private void RemoveAiProcessingTrayIcon()
    {
        if (_aiProcessingTrayIconVisible)
        {
            if (!_suppressAiProcessingTrayNativeCallsForSmoke)
            {
                nint windowHandle = new System.Windows.Interop.WindowInteropHelper(
                    this).Handle;
                if (windowHandle != 0)
                {
                    NativeNotifyIconData data =
                        CreateAiProcessingNotifyIconData(windowHandle, 0);
                    _ = ShellNotifyIcon(NotifyIconDelete, ref data);
                }
            }
            _aiProcessingTrayIconVisible = false;
            _aiProcessingTrayRemoveCount++;
        }
        ReleaseAiProcessingTrayIconHandle();
    }

    private bool ShowAiProcessingTrayNotification(
        string title,
        string message,
        bool failed)
    {
        if (!_aiProcessingMinimizedMode || !_aiProcessingTrayIconVisible)
            return false;

        string boundedTitle = BoundNotifyIconText(title, 63);
        string boundedMessage = BoundNotifyIconText(message, 255);
        _lastAiProcessingTrayNotificationTitle = boundedTitle;
        if (_suppressAiProcessingTrayNativeCallsForSmoke)
        {
            _aiProcessingTrayNotificationCount++;
            return true;
        }

        nint windowHandle = new System.Windows.Interop.WindowInteropHelper(this)
            .Handle;
        if (windowHandle == 0)
            return false;
        NativeNotifyIconData data = CreateAiProcessingNotifyIconData(
            windowHandle,
            NotifyIconInfo);
        data.InfoTitle = boundedTitle;
        data.Info = boundedMessage;
        data.InfoFlags = failed
            ? NotifyIconInfoFlagError
            : NotifyIconInfoFlagInfo;
        if (!ShellNotifyIcon(NotifyIconModify, ref data))
            return false;
        _aiProcessingTrayNotificationCount++;
        return true;
    }

    private nint EnsureAiProcessingTrayIconHandle()
    {
        if (_aiProcessingTrayIconHandle != 0)
            return _aiProcessingTrayIconHandle;

        string assemblyPath = typeof(MainWindow).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return 0;
        nint[] large = new nint[1];
        nint[] small = new nint[1];
        if (ExtractIconEx(assemblyPath, 0, large, small, 1) == 0)
            return 0;

        nint selected = small[0] != 0 ? small[0] : large[0];
        nint unused = selected == small[0] ? large[0] : small[0];
        if (unused != 0 && unused != selected)
            _ = DestroyIcon(unused);
        _aiProcessingTrayIconHandle = selected;
        _aiProcessingTrayIconHandleOwned = selected != 0;
        return selected;
    }

    private void ReleaseAiProcessingTrayIconHandle()
    {
        nint icon = _aiProcessingTrayIconHandle;
        bool owned = _aiProcessingTrayIconHandleOwned;
        _aiProcessingTrayIconHandle = 0;
        _aiProcessingTrayIconHandleOwned = false;
        if (owned && icon != 0)
            _ = DestroyIcon(icon);
    }

    private static NativeNotifyIconData CreateAiProcessingNotifyIconData(
        nint windowHandle,
        uint flags)
        => new()
        {
            Size = (uint)Marshal.SizeOf<NativeNotifyIconData>(),
            WindowHandle = windowHandle,
            IconId = AiProcessingTrayIconId,
            Flags = flags,
            CallbackMessage = WindowMessageAiProcessingTray,
            Tip = "",
            Info = "",
            InfoTitle = "",
        };

    private static string BoundNotifyIconText(string? value, int maxLength)
    {
        string text = value?.Trim() ?? "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public void EnableAiProcessingMinimizeNativeSuppressionForSmoke()
        => _suppressAiProcessingTrayNativeCallsForSmoke = true;

    public void PrepareAiProcessingMinimizeTimersForSmoke()
    {
        _searchFilterTimer.Start();
        _modalEnhancementPollTimer.Start();
        _enhancementWorkspacePollTimer.Start();
        _enhancementWorkspaceThumbnailViewportTimer.Start();
        _activeEnhancementStateRefreshTimer.Start();
    }

    public void MinimizeForAiProcessingSmoke()
        => WindowState = WindowState.Minimized;

    public void RestoreFromAiProcessingMinimizeForSmoke()
        => RestoreFromAiProcessingMinimize();

    public bool AiProcessingMinimizedModeForSmoke
        => _aiProcessingMinimizedMode;
    public bool AiProcessingTrayVisibleForSmoke
        => _aiProcessingTrayIconVisible;
    public bool AiProcessingUiTimersSuspendedForSmoke
        => !_searchFilterTimer.IsEnabled
            && !_modalEnhancementPollTimer.IsEnabled
            && !_enhancementWorkspacePollTimer.IsEnabled
            && !_enhancementWorkspaceThumbnailViewportTimer.IsEnabled;
    public bool ActiveEnhancementRevisionWatcherRunningForSmoke
        => _activeEnhancementStateRefreshTimer.IsEnabled;
    public bool SearchFilterTimerRunningForSmoke
        => _searchFilterTimer.IsEnabled;
    public bool ShowInTaskbarForSmoke
        => ShowInTaskbar;
    public bool AiProcessingMinimizeSurfaceContractForSmoke
        => LandingMinimizeButton.ToolTip?.ToString()?.Contains(
                "AI processing continues",
                StringComparison.Ordinal) == true
            && ModalMinimizeButton.ToolTip?.ToString()?.Contains(
                "AI processing continues",
                StringComparison.Ordinal) == true
            && AutomationProperties.GetName(LandingMinimizeButton).Contains(
                "AI processing continues",
                StringComparison.Ordinal)
            && AutomationProperties.GetName(ModalMinimizeButton).Contains(
                "AI processing continues",
                StringComparison.Ordinal);
    public int AiProcessingMinimizeEnterCountForSmoke
        => _aiProcessingMinimizeEnterCount;
    public int AiProcessingTrayAddCountForSmoke
        => _aiProcessingTrayAddCount;
    public int AiProcessingTrayRemoveCountForSmoke
        => _aiProcessingTrayRemoveCount;
    public int AiProcessingTrayNotificationCountForSmoke
        => _aiProcessingTrayNotificationCount;
    public string LastAiProcessingTrayNotificationTitleForSmoke
        => _lastAiProcessingTrayNotificationTitle;
    public static bool AiProcessingTrayNativeExportsAvailableForSmoke
    {
        get
        {
            if (!NativeLibrary.TryLoad("shell32.dll", out nint shell32))
                return false;
            try
            {
                return NativeLibrary.TryGetExport(
                        shell32,
                        "Shell_NotifyIconW",
                        out _)
                    && NativeLibrary.TryGetExport(
                        shell32,
                        "ExtractIconExW",
                        out _);
            }
            finally
            {
                NativeLibrary.Free(shell32);
            }
        }
    }
    public int CatalogTileCountForAiProcessingMinimizeSmoke
        => _allTiles.Count;
    public string? SelectedPathForAiProcessingMinimizeSmoke
        => SelectedTile()?.Path;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NativeNotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        [Out] nint[] largeIcons,
        [Out] nint[] smallIcons,
        uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint IconId;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIconHandle;
    }
}

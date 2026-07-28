using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class MainWindow : Window
{
    private enum ModalPointerState
    {
        Visible,
        ArmedToHide,
        Hidden,
        Interacting,
        Panning,
    }

    private enum ModalEdgeTarget
    {
        None,
        Previous,
        Next,
    }

    private static readonly StringComparer EnhancementSourceIdentityComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HttpClient ModalEnhancementHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".avif", ".bmp", ".gif", ".tif", ".tiff",
    };
    private static readonly HashSet<string> NativeSizedDecodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private const int MinParallelThumbnailCount = 32;
    private const int MaxThumbnailDecodeWorkers = 12;
    private const int InitialThumbnailPrefetchCount = 16;
    private const int MaxThumbnailDecodeAttempts = 4;
    private const int FirstThumbnailDecodeRetryDelayMilliseconds = 120;
    private const int SecondThumbnailDecodeRetryDelayMilliseconds = 300;
    private const int ThirdThumbnailDecodeRetryDelayMilliseconds = 750;
    private const int LegacyResidentThumbnailCountHint = 128;
    private const long MinResidentThumbnailBudgetBytes = 192L * 1024 * 1024;
    private const long MaxResidentThumbnailBudgetBytes = 512L * 1024 * 1024;
    private const long FallbackResidentThumbnailBudgetBytes = 256L * 1024 * 1024;
    private const int TransientStatusToastMilliseconds = 850;
    private const int MinParallelCatalogPreparationCount = 512;
    private const int MaxCatalogPreparationWorkers = 4;
    private const int ImmediateViewportThumbnailCount = 1;
    private const int ViewportThumbnailSettleDelayMilliseconds = 250;
    private const int MaxMetadataReadWorkers = 4;
    private const int MaxPngMetadataChunkBytes = 4 * 1024 * 1024;
    private const long MaxOriginalSizeDecodePixelCount = 10_000_000;
    private const int MaxDecodedPixelCount = 10_000_000;
    private const int MaxDecodedLongEdge = 16_384;
    private const int DecodePixelBudgetMultiplier = 5;
    private const int DecodeLongEdgeMultiplier = 8;
    private const int SearchFilterDebounceMilliseconds = 50;
    private const int SearchStateSaveDebounceMilliseconds = 300;
    private const int MaxVirtualizedContainerSmokeCount = 512;
    private const int MaxMaterializedSelectionVisualItems = 2_048;
    private const int MaxRecentFolderSets = 12;
    private const int PersistenceLockTimeoutMilliseconds = 2_000;
    private const int PersistenceLockRetryMilliseconds = 25;
    private const long AsyncSharedStoreThresholdBytes = 1_048_576;
    private const int SharedStoreWriterBatchDelayMilliseconds = 300;
    private readonly SemaphoreSlim _sharedStoreWriteKernelGate = new(1, 1);
    private const double MinCardWidth = 20;
    private const double MaxCardWidth = 600;
    private const double DefaultCardWidth = 200;
    private const double CardWidthStep = 20;
    private const double DesignWindowMinWidth = 900;
    private const double DesignWindowMinHeight = 560;
    private const double WideSidebarWidth = 232;
    private const double CompactSidebarRailWidth = 48;
    private const double DefaultRightPanelWidth = 340;
    private const double MinRightPanelWidth = 320;
    private const double MaxRightPanelWidth = 420;
    private const double AdaptiveWorkbenchThreshold = 1180;
    private const double ModalCompactToolbarThreshold = 1080;
    private const double AdaptivePreviewMinHeight = 168;
    private const double AdaptivePreviewMaxHeight = 232;
    private const double AdaptivePreviewHeightRatio = 0.30;
    private const double ModalZoomMin = 0.25;
    private const double ModalZoomMax = 10;
    private const double ModalEdgeNavigationDefaultPercent = 5;
    private const double ModalEdgeNavigationMinPercent = 0;
    private const double ModalEdgeNavigationMaxPercent = 20;
    private const string EnhancedStatusBorderDefaultColor = "#38BDF8";
    private const double ModalZoomKeyboardStep = 1.15;
    private const double ModalZoomWheelStep = 1.08;
    private const int ModalTransformAnimationMilliseconds = 110;
    private const int ModalTransformQualitySettleMilliseconds = 140;
    private const int ModalChromeRevealAnimationMilliseconds = 90;
    private const int ModalChromeTransientMilliseconds = 800;
    private const int ModalFavoritePulseMilliseconds = 620;
    private const double ModalFilmstripHoverZone = 176;
    private const int MaxModalFilmstripWindowItems = 101;
    private const int MaxModalPromptTagCount = 160;
    private const int MaxModalPromptScanCharacters = 65_536;
    private const int MaxModalPromptTagCharacters = 512;
    private const int ImmediateModalPromptChipCount = 16;
    private const int ModalPromptChipBatchCount = 12;
    private const string MaximizeWindowIconGeometry = "M4,4 H20 V20 H4 Z";
    private const string RestoreWindowIconGeometry = "M7,4 H20 V17 H17 V7 H7 Z M4,7 H17 V20 H4 Z";
    private const string DisplayStyleStandard = "standard";
    private const string DisplayStyleCompact = "compact";
    private const string DisplayStylePoster = "poster";
    private const string AspectOriginalValue = "original";
    private const string AspectSquareValue = "square";
    private const string AspectPortraitValue = "portrait";
    private const string SortModifiedNewestValue = "modified-newest";
    private const string SortModifiedOldestValue = "modified-oldest";
    private const string SortCreatedNewestValue = "created-newest";
    private const string SortCreatedOldestValue = "created-oldest";
    private const string SortNameValue = "name";
    private const string SortRandomValue = "random";
    // Runtime state is manual From/To only. These names are accepted only while migrating old files.
    private const string DatePresetNoneValue = "none";
    private const string DatePresetManualValue = "manual";
    private const string ModalMetadataPromptTab = "prompt";
    private const string ModalMetadataNegativeTab = "negative";
    private const string ModalMetadataSettingsTab = "settings";
    private const int MinFavoriteFilterLevel = 1;
    private const int MaxFavoriteFilterLevel = 5;
    private const int MaxPersistedPreviewTabs = 30;
    private static readonly JsonSerializerOptions SharedRecentJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly ResettableObservableCollection<Tile> _tiles = new();
    private readonly SnapshotCollectionView<Tile> _tilesView;
    private readonly ResettableObservableCollection<Tile> _modalFilmstripTiles = new();
    private readonly ObservableCollection<string> _landingFolderSet = new();
    private readonly ObservableCollection<FolderBucketView> _folderBucketViews = new();
    private readonly ObservableCollection<RecentFolderSetView> _recentFolderSetViews = new();
    private readonly ObservableCollection<PreviewTabView> _previewTabs = new();
    private readonly ObservableCollection<SearchHistoryItemView> _searchHistoryEntries = new();
    private CancellationTokenSource? _searchHistoryReadCts;
    private long _searchHistoryUiGeneration;
    private bool _suppressSearchHistoryFocusOpen;
    private List<Tile> _allTiles = new();
    private readonly List<Tile> _closedPreviewTabs = new();
    private readonly HashSet<string> _pinnedPreviewPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteDirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenDirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FavoritePendingMutation> _pendingFavoriteMutations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SeenPendingMutation> _pendingSeenMutations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenFolderBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedFolderBucketKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Tile> _canonicalSelectionMarkers = [];
    private readonly HashSet<string> _activeAlbumMemberPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeAlbumMemberOrder = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeAlbumId;
    private string? _activeAlbumName;
    private int _activeAlbumMemberCount;
    private int _activeAlbumOutsideCount;
    private int _activeAlbumMissingCount;
    private int _activeAlbumAsyncGeneration;
    private readonly SemaphoreSlim _activeAlbumAvailabilityGate = new(1, 1);
    private CancellationTokenSource? _activeAlbumAvailabilityCts;
    private int _activeAlbumAvailabilityPendingCount;
    private int _activeAlbumAvailabilityActiveCount;
    private int _activeAlbumAvailabilityMaxConcurrentCount;
    private int _activeAlbumAvailabilityCanceledCount;
    private int _activeAlbumAvailabilityStartedCount;
    private int _activeAlbumAvailabilityApplyCount;
    private int _activeAlbumAvailabilityDelayForSmokeMs;
    private int _activeAlbumSourceApplyCount;
    private bool _lastActiveAlbumAvailabilityOffDispatcher;
    private readonly Dictionary<string, ManagedEnhancedOutput> _enhancedOutputs = new(EnhancementSourceIdentityComparer);
    private readonly Dictionary<string, ManagedEnhancedOutput> _catalogEnhancedOutputsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ManagedEnhancementVersion>> _enhancementVersions =
        new(EnhancementSourceIdentityComparer);
    private readonly Dictionary<string, List<ManagedEnhancementVersion>> _catalogEnhancementVersionsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _restoredPreviewTabPaths = [];
    private readonly SemaphoreSlim _thumbnailDecodeGate = new(MaxThumbnailDecodeWorkers, MaxThumbnailDecodeWorkers);
    private readonly ConcurrentDictionary<string, byte> _thumbnailLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ThumbnailDecodeFailure> _thumbnailDecodeFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Tile> _residentThumbnailLru = new();
    private readonly Dictionary<string, LinkedListNode<Tile>> _residentThumbnailNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _residentThumbnailByteSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _protectedResidentThumbnailPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _residentThumbnailBudgetBytes = ResolveResidentThumbnailBudgetBytes();
    private int _maxResidentThumbnailCount;
    private long _residentThumbnailBytes;
    private long _maxResidentThumbnailBytes;
    private long _maxProtectedResidentThumbnailBytes;
    private long _maxEffectiveResidentThumbnailBudgetBytes;
    private int _visibleThumbnailEvictionCount;
    private int _activeThumbnailDecodeWorkers;
    private int _maxActiveThumbnailDecodeWorkers;
    private int _thumbnailViewportScheduleCount;
    private int _thumbnailViewportCancelCount;
    private int _thumbnailViewportDuplicateSuppressedCount;
    private CancellationTokenSource? _thumbnailViewportCts;
    private string? _thumbnailViewportSignature;
    private long _thumbnailViewportRevision;
    private double _lastListThumbnailVerticalOffset;
    private VirtualizingWrapPanel? _galleryVirtualizingPanel;
    private int _retiredPreparedCatalogLayoutAppliedCount;
    private int _retiredPreparedCatalogLayoutRejectedCount;
    private long _retiredCatalogLayoutMaxMeasureMilliseconds;
    private VirtualizingMeasureDiagnostic _retiredCatalogLayoutMaxMeasureDiagnostic =
        new("", 0, 0, -1);
    private readonly List<VirtualizingPanelPhaseDiagnostic> _retiredCatalogPanelPhaseDiagnostics = [];
    private bool _catalogPanelPhaseDiagnosticOverflow;
    private string _catalogInteractionDiagnosticOperation = "";
    private int _thumbnailBrowserCacheHits;
    private int _lastInitialUnseenCount;
    private int _enhancementJobsRead;
    private int _enhancedCandidateCount;
    private int _favoriteSaveAttemptCount;
    private int _sharedRecentCommitAttemptCount;
    private int _sharedRecentCommitSuccessCount;
    private bool _enhancementReadOk = true;
    private string? _enhancementReadError;
    private DateTime _enhancementJobsLastWriteTimeUtc;
    private long _enhancementJobsLength = -1;
    private Rect _restoreBounds;
    private bool _fakeMaximized;
    private bool _normalizingNativeMaximize;
    private Func<Rect> _currentMonitorWorkArea = null!;
    private Func<Rect> _currentMonitorBounds = null!;
    private bool _modalFullScreen;
    private Rect _modalFullScreenRestoreBounds;
    private bool _modalFullScreenRestoreFakeMaximized;
    private WindowState _modalFullScreenRestoreWindowState = WindowState.Normal;
    private ResizeMode _modalFullScreenRestoreResizeMode = ResizeMode.CanResize;
    private double _modalFullScreenRestoreMinWidth = DesignWindowMinWidth;
    private double _modalFullScreenRestoreMinHeight = DesignWindowMinHeight;
    private bool _initializing = true;
    private bool _suppressStateSave;
    private bool _favoritesWriteBlocked;
    private bool _seenWriteBlocked;
    private SharedStoreWriter<FavoriteDelta>? _favoriteWriter;
    private SharedStoreWriter<SeenDelta>? _Ûµã‹h‘éì¶»§q«^uÌ¡	ÕÑÑ½¸¹±¥­Ù•¹Ğ°1…¹‘¥¹5¥¹¥µ¥é•	ÕÑÑ½¸¤¤ì(€€€€€€€‰½½°µ¥¹¥µ¥é•€ô]¥¹‘½İMÑ…Ñ”€ôô]¥¹‘½İMÑ…Ñ”¹5¥¹¥µ¥é•ì(€€€€€€€]¥¹‘½İMÑ…Ñ”€ôÁÉ•Ù¥½ÕÌ€ôô]¥¹‘½İMÑ…Ñ”¹5¥¹¥µ¥é•€ü]¥¹‘½İMÑ…Ñ”¹9½Éµ…°€èÁÉ•Ù¥½ÕÌì(€€€€€€€É•ÑÕÉ¸µ¥¹¥µ¥é•ì(€€€ô(€€€ÁÕ‰±¥Œ‰½½°M¡½ÉÑÕÑ¥Í½Ù•É…‰¥±¥Ñå½¹ÑÉ…Ñ½ÉMµ½­”(€€€€€€€€ôø¹•İmtì1…¹‘¥¹M¡½ÉÑÕÑÍ	ÕÑÑ½¸°Y¥•İ•ÉM¡½ÉÑÕÑÍ	ÕÑÑ½¸°5½‘…±M¡½ÉÑÕÑÍ	ÕÑÑ½¸ô¹±°¡‰ÕÑÑ½¸€ôø(€€€€€€€€€€€ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡‰ÕÑÑ½¸¤°€‰=Á•¸­•å‰½…ÉÍ¡½ÉÑÕÑÌˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€˜˜€…ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡‰ÕÑÑ½¸¹Q½½±Q¥Àü¹Q½MÑÉ¥¹œ ¤¤¤ì(€€€ÁÕ‰±¥Œ‰½½°M¥‘•‰…ÉM•ÑÑ¥¹ÍA¥¹¹•‘½ÉMµ½­”(€€€€€€€€ôøÉ¥¹•ÑI½Ü¡Y¥ÍÕ…±QÉ••!•±Á•È¹•ÑA…É•¹Ğ¡M¥‘•‰…ÉÁÁM•ÑÑ¥¹Í	ÕÑÑ½¸¤…ÌU%±•µ•¹Ğ€üüM¥‘•‰…ÉÁÁM•ÑÑ¥¹Í	ÕÑÑ½¸¤€ôô€Ä(€€€€€€€€€€€€˜˜M¥‘•‰…ÉÁÁM•ÑÑ¥¹Í	ÕÑÑ½¸¹Y¥Í¥‰¥±¥Ñä€ôôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”(€€€€€€€€€€€€˜˜ÍÑÉ¥¹œ¹ÅÕ…±Ì¡ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡M¥‘•‰…ÉÁÁM•ÑÑ¥¹Í	ÕÑÑ½¸¤°€‰=Á•¸…ÁÀÍ•ÑÑ¥¹Ì™É½´Í¥‘•‰…Èˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ì(€€€ÁÕ‰±¥Œ‰½½°M¥‘•‰…ÉMÉ½±±½¹ÑÉ…Ñ½ÉMµ½­”(€€€ì(€€€€€€€•Ğ(€€€€€€€ì(€€€€€€€€€€€1¥ÍĞñMÉ½±±	…ÈøÙ•ÉÑ¥…±	…ÉÌ€ô¥¹‘Y¥ÍÕ…±•Í•¹‘…¹ÑÌñMÉ½±±	…Èø¡M¥‘•‰…ÉMÉ½±±Y¥•İ•È¤(€€€€€€€€€€€€€€€€¹]¡•É”¡ÍÑ…Ñ¥ŒÍÉ½±±	…È€ôøÍÉ½±±	…È¹%ÍY¥Í¥‰±”€˜˜ÍÉ½±±	…È¹=É¥•¹Ñ…Ñ¥½¸€ôô=É¥•¹Ñ…Ñ¥½¸¹Y•ÉÑ¥…°¤(€€€€€€€€€€€€€€€€¹Q½1¥ÍĞ ¤ì(€€€€€€€€€€€É•ÑÕÉ¸MÉ½±±Y¥•İ•È¹•ÑY•ÉÑ¥…±MÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¡M¥‘•‰…ÉMÉ½±±Y¥•İ•È¤€ôôMÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¹ÕÑ¼(€€€€€€€€€€€€€€€€˜˜MÉ½±±Y¥•İ•È¹•Ñ!½É¥é½¹Ñ…±MÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¡M¥‘•‰…ÉMÉ½±±Y¥•İ•È¤€ôôMÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¹¥Í…‰±•(€€€€€€€€€€€€€€€€˜˜Ù•ÉÑ¥…±	…ÉÌ¹½Õ¹Ğ€ø€À(€€€€€€€€€€€€€€€€˜˜Ù•ÉÑ¥…±	…ÉÌ¹±°¡ÍÉ½±±	…È€ôøÍÉ½±±	…È¹ÑÕ…±]¥‘Ñ €ø€À€˜˜%Í%¹Í¥‘•]¥¹‘½İ½¹Ñ•¹Ñ½ÉMµ½­”¡ÍÉ½±±	…È¤¤ì(€€€€€€€ô(€€€ô(€€€ÁÕ‰±¥Œ‰½½°M¥‘•‰…É	ÕÑÑ½¹Q•áÑ¥ÑÍ½ÉMµ½­”(€€€€€€€€ôøM¥‘•‰…ÉQ•áÑ½¹ÑÉ½±Í½ÉMµ½­” ¤¹±°¡½¹ÑÉ½±Q•áÑ¥ÑÍ½ÉMµ½­”¤ì(€€€ÁÕ‰±¥Œ1¥ÍĞñÍÑÉ¥¹œøM¥‘•‰…É±¥ÁÁ•‘	ÕÑÑ½¹9…µ•Í½ÉMµ½­”(€€€€€€€€ôøM¥‘•‰…ÉQ•áÑ½¹ÑÉ½±Í½ÉMµ½­” ¤(€€€€€€€€€€€€¹]¡•É”¡½¹ÑÉ½°€ôø€…½¹ÑÉ½±Q•áÑ¥ÑÍ½ÉMµ½­”¡½¹ÑÉ½°¤¤(€€€€€€€€€€€€¹M•±•Ğ¡ÍÑ…Ñ¥Œ½¹ÑÉ½°€ôø½¹ÑÉ½°¹9…µ”¤(€€€€€€€€€€€€¹Q½1¥ÍĞ ¤ì(€€€ÁÕ‰±¥Œ‰½½°Y¥Í¥‰±•]½É­‰•¹¡¡É½µ•½¹Ñ…¥¹•‘½ÉMµ½­”(€€€ì(€€€€€€€•Ğ(€€€€€€€ì(€€€€€€€€€€€É…µ•İ½É­±•µ•¹ĞÍ¥‘•‰…ÉM•ÑÑ¥¹Ì€ô}…‘…ÁÑ¥Ù•]½É­‰•¹ (€€€€€€€€€€€€€€€€ü9…ÉÉ½İM¥‘•‰…ÉM•ÑÑ¥¹Í	ÕÑÑ½¸(€€€€€€€€€€€€€€€€èM¥‘•‰…ÉÁÁM•ÑÑ¥¹Í	ÕÑÑ½¸ì(€€€€€€€€€€€É•ÑÕÉ¸¹•ÜÉ…µ•İ½É­±•µ•¹Ñmt(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€M•…É¡	½á½¹Ñ…¥¹•È°(€€€€€€€€€€€€€€€€€€€!•…‘•ÉÉ…I•¥½¸°(€€€€€€€€€€€€€€€€€€€Í¥‘•‰…ÉM•ÑÑ¥¹Ì°(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€¹]¡•É”¡ÍÑ…Ñ¥Œ•±•µ•¹Ğ€ôø•±•µ•¹Ğ¹%ÍY¥Í¥‰±”¤(€€€€€€€€€€€€€€€€¹±°¡%Í%¹Í¥‘•]¥¹‘½İ½¹Ñ•¹Ñ½ÉMµ½­”¤ì(€€€€€€€ô(€€€ô(€€€ÁÕ‰±¥Œ‘½Õ‰±”!•…‘•ÉÉ…I•¥½¹]¥‘Ñ¡½ÉMµ½­”€ôø!•…‘•ÉÉ…I•¥½¸¹ÑÕ…±]¥‘Ñ ì(€€€ÁÕ‰±¥Œ‘½Õ‰±”M•…É¡5¥¹¥µÕµ]¥‘Ñ¡½ÉMµ½­”€ôøM•…É¡	½á½¹Ñ…¥¹•È¹5¥¹]¥‘Ñ ì((€€€ÁÉ¥Ù…Ñ”‰½½°%Í%¹Í¥‘•]¥¹‘½İ½¹Ñ•¹Ñ½ÉMµ½­”¡É…µ•İ½É­±•µ•¹Ğ•±•µ•¹Ğ¤(€€€ì(€€€€€€€¥˜€¡½¹Ñ•¹Ğ¥Ì¹½ĞÉ…µ•İ½É­±•µ•¹ĞÉ½½Ğñğ€…•±•µ•¹Ğ¹%ÍY¥Í¥‰±”ñğ•±•µ•¹Ğ¹ÑÕ…±]¥‘Ñ €ğô€Àñğ•±•µ•¹Ğ¹ÑÕ…±!•¥¡Ğ€ğô€À¤(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€I•Ğ‰½Õ¹‘Ì€ô•±•µ•¹Ğ¹QÉ…¹Í™½ÉµQ½¹•ÍÑ½È¡É½½Ğ¤¹QÉ…¹Í™½Éµ	½Õ¹‘Ì¡¹•ÜI•Ğ¡•±•µ•¹Ğ¹I•¹‘•ÉM¥é”¤¤ì(€€€€€€€€€€€É•ÑÕÉ¸‰½Õ¹‘Ì¹1•™Ğ€øô€´À¸Ô(€€€€€€€€€€€€€€€€˜˜‰½Õ¹‘Ì¹Q½À€øô€´À¸Ô(€€€€€€€€€€€€€€€€˜˜‰½Õ¹‘Ì¹I¥¡Ğ€ğôÉ½½Ğ¹ÑÕ…±]¥‘Ñ €¬€À¸Ô(€€€€€€€€€€€€€€€€˜˜‰½Õ¹‘Ì¹	½ÑÑ½´€ğôÉ½½Ğ¹ÑÕ…±!•¥¡Ğ€¬€À¸Ôì(€€€€€€€ô(€€€€€€€…Ñ €¡%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”‰½½°½¹ÑÉ½±Q•áÑ¥ÑÍ½ÉMµ½­”¡½¹Ñ•¹Ñ½¹ÑÉ½°½¹ÑÉ½°¤(€€€ì(€€€€€€€¥˜€ …½¹ÑÉ½°¹%ÍY¥Í¥‰±”ñğ½¹ÑÉ½°¹½¹Ñ•¹Ğ¥Ì¹½ĞÍÑÉ¥¹œÑ•áĞ¤(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€½¹Ñ•¹ÑAÉ•Í•¹Ñ•ÈüÁÉ•Í•¹Ñ•È€ô¥¹‘Y¥ÍÕ…±•Í•¹‘…¹Ğñ½¹Ñ•¹ÑAÉ•Í•¹Ñ•Èø¡½¹ÑÉ½°¤ì(€€€€€€€¥˜€¡ÁÉ•Í•¹Ñ•È¥Ì¹Õ±°ñğÁÉ•Í•¹Ñ•È¹ÑÕ…±]¥‘Ñ €ğô€ÀñğÁÉ•Í•¹Ñ•È¹ÑÕ…±!•¥¡Ğ€ğô€À¤(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€Ù…È™½Éµ…ÑÑ•€ô¹•Ü½Éµ…ÑÑ•‘Q•áĞ (€€€€€€€€€€€Ñ•áĞ°(€€€€€€€€€€€Õ±ÑÕÉ•%¹™¼¹ÕÉÉ•¹ÑU%Õ±ÑÕÉ”°(€€€€€€€€€€€½¹ÑÉ½°¹±½İ¥É•Ñ¥½¸°(€€€€€€€€€€€¹•ÜQåÁ•™…”¡½¹ÑÉ½°¹½¹Ñ…µ¥±ä°½¹ÑÉ½°¹½¹ÑMÑå±”°½¹ÑÉ½°¹½¹Ñ]•¥¡Ğ°½¹ÑÉ½°¹½¹ÑMÑÉ•Ñ ¤°(€€€€€€€€€€€½¹ÑÉ½°¹½¹ÑM¥é”°(€€€€€€€€€€€	ÉÕÍ¡•Ì¹	±…¬°(€€€€€€€€€€€Y¥ÍÕ…±QÉ••!•±Á•È¹•ÑÁ¤¡½¹ÑÉ½°¤¹A¥á•±ÍA•É¥À¤ì(€€€€€€€‘½Õ‰±”…Ù…¥±…‰±•]¥‘Ñ €ô½¹ÑÉ½°¥ÌI…‘¥½	ÕÑÑ½¸(€€€€€€€€€€€€ü5…Ñ ¹5…à À°½¹ÑÉ½°¹ÑÕ…±]¥‘Ñ €´€ÄÈ¤(€€€€€€€€€€€€èÁÉ•Í•¹Ñ•È¹ÑÕ…±]¥‘Ñ ì(€€€€€€€‘½Õ‰±”…Ù…¥±…‰±•!•¥¡Ğ€ô½¹ÑÉ½°¥ÌI…‘¥½	ÕÑÑ½¸(€€€€€€€€€€€€ü5…Ñ ¹5…à À°½¹ÑÉ½°¹ÑÕ…±!•¥¡Ğ€´€à¤(€€€€€€€€€€€€èÁÉ•Í•¹Ñ•È¹ÑÕ…±!•¥¡Ğì(€€€€€€€É•ÑÕÉ¸™½Éµ…ÑÑ•¹]¥‘Ñ¡%¹±Õ‘¥¹QÉ…¥±¥¹]¡¥Ñ•ÍÁ…”€ğô…Ù…¥±…‰±•]¥‘Ñ €¬€È(€€€€€€€€€€€€˜˜™½Éµ…ÑÑ•¹!•¥¡Ğ€ğô…Ù…¥±…‰±•!•¥¡Ğ€¬€Èì(€€€ô((€€€ÁÉ¥Ù…Ñ”%¹Õµ•É…‰±”ñ½¹Ñ•¹Ñ½¹ÑÉ½°øM¥‘•‰…ÉQ•áÑ½¹ÑÉ½±Í½ÉMµ½­” ¤(€€€ì(€€€€€€€å¥•±É•ÑÕÉ¸M¥‘•‰…É‘‘½±‘•É	ÕÑÑ½¸ì(€€€€€€€å¥•±É•ÑÕÉ¸M¥‘•‰…É¡…¹•½±‘•É	ÕÑÑ½¸ì(€€€€€€€å¥•±É•ÑÕÉ¸MÑå±•MÑ…¹‘…Éì(€€€€€€€å¥•±É•ÑÕÉ¸MÑå±•½µÁ…Ğì(€€€€€€€å¥•±É•ÑÕÉ¸ÍÁ•Ñ=É¥¥¹…±	ÕÑÑ½¸ì(€€€ô(€€€ÁÕ‰±¥Œ‰½½°Ñ¥Ù…Ñ•M¡½ÉÑÕÑ¹ÑÉå½ÉMµ½­”¡ÍÑÉ¥¹œÍÕÉ™…”¤(€€€ì(€€€€€€€	ÕÑÑ½¸‰ÕÑÑ½¸€ôÍÕÉ™…”¹QÉ¥´ ¤¹Q½1½İ•É%¹Ù…É¥…¹Ğ ¤Íİ¥Ñ (€€€€€€€ì(€€€€€€€€€€€€‰±…¹‘¥¹œˆ€ôø1…¹‘¥¹M¡½ÉÑÕÑÍ	ÕÑÑ½¸°(€€€€€€€€€€€€‰µ½‘…°ˆ€ôø5½‘…±M¡½ÉÑÕÑÍ	ÕÑÑ½¸°(€€€€€€€€€€€|€ôøY¥•İ•ÉM¡½ÉÑÕÑÍ	ÕÑÑ½¸°(€€€€€€€ôì(€€€€€€€‰ÕÑÑ½¸¹I…¥Í•Ù•¹Ğ¡¹•ÜI½ÕÑ•‘Ù•¹ÑÉÌ¡	ÕÑÑ½¸¹±¥­Ù•¹Ğ°‰ÕÑÑ½¸¤¤ì(€€€€€€€É•ÑÕÉ¸ÁÁM•ÑÑ¥¹Í¥…±½œ¹Y¥Í¥‰¥±¥Ñä€ôôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”ì(€€€ô(€€€ÁÕ‰±¥Œ‰½½°ÁÁM•ÑÑ¥¹ÍMÉ½±±½¹ÑÉ…Ñ½ÉMµ½­”(€€€€€€€€ôøMÉ½±±Y¥•İ•È¹•ÑY•ÉÑ¥…±MÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¡ÁÁM•ÑÑ¥¹ÍMÉ½±±Y¥•İ•È¤€ôôMÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¹ÕÑ¼(€€€€€€€€€€€€˜˜MÉ½±±Y¥•İ•È¹•Ñ!½É¥é½¹Ñ…±MÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¡ÁÁM•ÑÑ¥¹ÍMÉ½±±Y¥•İ•È¤€ôôMÉ½±±	…ÉY¥Í¥‰¥±¥Ñä¹¥Í…‰±•(€€€€€€€€€€€€˜˜€…ÁÁM•ÑÑ¥¹ÍMÉ½±±Y¥•İ•È¹…¹½¹Ñ•¹ÑMÉ½±°(€€€€€€€€€€€€˜˜É¥¹•ÑI½Ü¡ÁÁM•ÑÑ¥¹ÍMÉ½±±Y¥•İ•È¤€ôô€Ä(€€€€€€€€€€€€˜˜É¥¹•ÑI½Ü¡ÁÁM•ÑÑ¥¹Í½¹•	ÕÑÑ½¸¤€ôô€È(€€€€€€€€€€€€˜˜ÁÁM•ÑÑ¥¹Í¥…±½MÕÉ™…”¹5…á!•¥¡Ğ€ø€Àì(€€€ÁÕ‰±¥Œ‰½½°ÁÁM•ÑÑ¥¹Í%¹Í¥‘•±¥­MÑ…åÍ=Á•¹½ÉMµ½­” ¤(€€€€€€€€ôøÁÁM•ÑÑ¥¹Í¥…±½œ¹Y¥Í¥‰¥±¥Ñä€ôôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”(€€€€€€€€€€€€˜˜€…QÉå±½Í•ÁÁM•ÑÑ¥¹ÍÉ½µ	…­‘É½À¡ÁÁM•ÑÑ¥¹Í¥…±½MÕÉ™…”¤(€€€€€€€€€€€€˜˜ÁÁM•ÑÑ¥¹Í¥…±½œ¹Y¥Í¥‰¥±¥Ñä€ôôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”ì(€€€ÁÕ‰±¥Œ‰½½°ÁÁM•ÑÑ¥¹Í	…­‘É½Á±½Í•Í½ÉMµ½­” ¤(€€€€€€€€ôøQÉå±½Í•ÁÁM•ÑÑ¥¹ÍÉ½µ	…­‘É½À¡ÁÁM•ÑÑ¥¹Í¥…±½œ¤(€€€€€€€€€€€€˜˜ÁÁM•ÑÑ¥¹Í¥…±½œ¹Y¥Í¥‰¥±¥Ñä€„ôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”ì(€€€ÁÕ‰±¥Œ‰½½°¥…¹½ÍÑ¥ÍMÕÉ™…•½¹ÑÉ…Ñ½ÉMµ½­”(€€€€€€€€ôø€…ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡¥…¹½ÍÑ¥ÍQ•áĞ¤¤(€€€€€€€€€€€€˜˜€…ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡½Áå¥…¹½ÍÑ¥Í	ÕÑÑ½¸¤¤(€€€€€€€€€€€€˜˜ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ1¥Ù•M•ÑÑ¥¹œ¡¥…¹½ÍÑ¥ÍMÑ…ÑÕÍQ•áĞ¤€ôôÕÑ½µ…Ñ¥½¹1¥Ù•M•ÑÑ¥¹œ¹A½±¥Ñ”(€€€€€€€€€€€€˜˜ÁÁM•ÑÑ¥¹Í¥…±½MÕÉ™…”¹5…á!•¥¡Ğ€ø€Àì(€€€ÁÕ‰±¥Œ‰½½°!½Ù•É¹‘Q½½±Ñ¥Á½¹ÑÉ…Ñ½ÉMµ½­”(€€€€€€€€ôø¹•İmt(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€‰%½¹	ÕÑÑ½¸ˆ°€‰%½¹	ÕÑÑ½¹Ñ¥Ù”ˆ°€‰…ÁÑ¥½¹	ÕÑÑ½¸ˆ°€‰±½Í•	ÕÑÑ½¸ˆ°€‰M¥‘•‰…É1¥¹¬ˆ°€‰QÉ…¹ÍÁ…É•¹Ñ	ÕÑÑ½¸ˆ°(€€€€€€€€€€€€€€€€‰¡½ÍÑ	ÕÑÑ½¸ˆ°€‰…¹•É	ÕÑÑ½¸ˆ°€‰MÑ•Á	ÕÑÑ½¸ˆ°€‰AÉ¥µ…Éå	ÕÑÑ½¸ˆ°€‰	É½İÍ•	ÕÑÑ½¸ˆ°€‰…¹•É¥±±•ˆ°(€€€€€€€€€€€ô(€€€€€€€€€€€€¹±°¡MÑå±•!…Í¹…‰±•‘!½Ù•É½ÉMµ½­”¤(€€€€€€€€€€€€˜˜ÁÁ±¥…Ñ¥½¸¹ÕÉÉ•¹Ğü¹QÉå¥¹‘I•Í½ÕÉ”¡ÑåÁ•½˜¡Q½½±Q¥À¤¤¥ÌMÑå±”Ñ½½±Ñ¥ÁMÑå±”(€€€€€€€€€€€€˜˜Ñ½½±Ñ¥ÁMÑå±”¹M•ÑÑ•ÉÌ¹=™QåÁ”ñM•ÑÑ•Èø ¤¹¹ä¡Í•ÑÑ•È€ôøÍ•ÑÑ•È¹AÉ½Á•ÉÑä€ôô½¹ÑÉ½°¹	…­É½Õ¹‘AÉ½Á•ÉÑä€˜˜Í•ÑÑ•È¹Y…±Õ”¥Ì	ÉÕÍ ¤(€€€€€€€€€€€€˜˜Ñ½½±Ñ¥ÁMÑå±”¹M•ÑÑ•ÉÌ¹=™QåÁ”ñM•ÑÑ•Èø ¤¹¹ä¡Í•ÑÑ•È€ôøÍ•ÑÑ•È¹AÉ½Á•ÉÑä€ôô½¹ÑÉ½°¹½É•É½Õ¹‘AÉ½Á•ÉÑä€˜˜Í•ÑÑ•È¹Y…±Õ”¥Ì	ÉÕÍ ¤(€€€€€€€€€€€€˜˜Ñ½½±Ñ¥ÁMÑå±”¹M•ÑÑ•ÉÌ¹=™QåÁ”ñM•ÑÑ•Èø ¤¹¹ä¡Í•ÑÑ•È€ôøÍ•ÑÑ•È¹AÉ½Á•ÉÑä€ôô½¹ÑÉ½°¹Q•µÁ±…Ñ•AÉ½Á•ÉÑä€˜˜Í•ÑÑ•È¹Y…±Õ”¥Ì½¹ÑÉ½±Q•µÁ±…Ñ”¤ì(€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°MÑå±•!…Í¹…‰±•‘!½Ù•É½ÉMµ½­”¡ÍÑÉ¥¹œÉ•Í½ÕÉ•-•ä¤(€€€ì(€€€€€€€¥˜€¡ÁÁ±¥…Ñ¥½¸¹ÕÉÉ•¹Ğü¹QÉå¥¹‘I•Í½ÕÉ”¡É•Í½ÕÉ•-•ä¤¥Ì¹½ĞMÑå±”ÍÑå±”¤(€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€½¹ÑÉ½±Q•µÁ±…Ñ”üÑ•µÁ±…Ñ”€ôÍÑå±”¹M•ÑÑ•ÉÌ(€€€€€€€€€€€€¹=™QåÁ”ñM•ÑÑ•Èø ¤(€€€€€€€€€€€€¹¥ÉÍÑ=É•™…Õ±Ğ¡Í•ÑÑ•È€ôøÍ•ÑÑ•È¹AÉ½Á•ÉÑä€ôô½¹ÑÉ½°¹Q•µÁ±…Ñ•AÉ½Á•ÉÑä¤(€€€€€€€€€€€€ü¹Y…±Õ”…Ì½¹ÑÉ½±Q•µÁ±…Ñ”ì(€€€€€€€É•ÑÕÉ¸Ñ•µÁ±…Ñ”ü¹QÉ¥•ÉÌ¹=™QåÁ”ñ5Õ±Ñ¥QÉ¥•Èø ¤¹¹ä¡ÑÉ¥•È€ôø(€€€€€€€€€€€ÑÉ¥•È¹½¹‘¥Ñ¥½¹Ì¹¹ä¡½¹‘¥Ñ¥½¸€ôø½¹‘¥Ñ¥½¸¹AÉ½Á•ÉÑä€ôôU%±•µ•¹Ğ¹%Í5½ÕÍ•=Ù•ÉAÉ½Á•ÉÑä€˜˜ÅÕ…±Ì¡½¹‘¥Ñ¥½¸¹Y…±Õ”°ÑÉÕ”¤¤(€€€€€€€€€€€€˜˜ÑÉ¥•È¹½¹‘¥Ñ¥½¹Ì¹¹ä¡½¹‘¥Ñ¥½¸€ôø½¹‘¥Ñ¥½¸¹AÉ½Á•ÉÑä€ôôU%±•µ•¹Ğ¹%Í¹…‰±•‘AÉ½Á•ÉÑä€˜˜ÅÕ…±Ì¡½¹‘¥Ñ¥½¸¹Y…±Õ”°ÑÉÕ”¤¤¤€ôôÑÉÕ”ì(€€€ô(€€€ÁÕ‰±¥Œ¥…¹½ÍÑ¥ÍMµ½­•M¹…ÁÍ¡½Ğ½Áå¥…¹½ÍÑ¥Í½ÉMµ½­”¡‰½½°¥¹©•Ñ±¥Á‰½…É‘…¥±ÕÉ”¤(€€€ì(€€€€€€€Ñ¥½¸ñÍÑÉ¥¹œøÁÉ•Ù¥½ÕÌ€ô}‘¥…¹½ÍÑ¥Í±¥Á‰½…É‘]É¥Ñ•Èì(€€€€€€€}‘¥…¹½ÍÑ¥Í±¥Á‰½…É‘]É¥Ñ•È€ô¥¹©•Ñ±¥Á‰½…É‘…¥±ÕÉ”(€€€€€€€€€€€€ü|€ôøÑ¡É½Ü¹•ÜáÑ•É¹…±á•ÁÑ¥½¸ ‰±¥Á‰½…ÉÕ¹…Ù…¥±…‰±”ˆ¤(€€€€€€€€€€€€è|€ôøìôì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€‰½½°½Á¥•€ôQÉå½Áå¥…¹½ÍÑ¥Ì ¤ì(€€€€€€€€€€€É•ÑÕÉ¸¹•Ü¥…¹½ÍÑ¥ÍMµ½­•M¹…ÁÍ¡½Ğ¡½Á¥•°}±…ÍÑ¥…¹½ÍÑ¥Í½ÁåQ•áĞ°¥…¹½ÍÑ¥ÍMÑ…ÑÕÍQ•áĞ¹Q•áĞ°¥…¹½ÍÑ¥ÍMÕÉ™…•½¹ÑÉ…Ñ½ÉMµ½­”°%ÍM•ÑÑ¥¹Í¥…±½½ÕÍ•‘½ÉMµ½­”¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€}‘¥…¹½ÍÑ¥Í±¥Á‰½…É‘]É¥Ñ•È€ôÁÉ•Ù¥½ÕÌì(€€€€€€€ô(€€€ô(€€€ÁÕ‰±¥Œ‰½½°½ÕÍ¥…¹½ÍÑ¥Í½ÉMµ½­” ¤€ôø½Áå¥…¹½ÍÑ¥Í	ÕÑÑ½¸¹½ÕÌ ¤ì(€€€ÁÕ‰±¥Œ‰½½°½ÕÍÁÁM•ÑÑ¥¹Í½¹•½ÉMµ½­” ¤€ôøÁÁM•ÑÑ¥¹Í½¹•	ÕÑÑ½¸¹½ÕÌ ¤ì(€€€ÁÕ‰±¥Œ‰½½°M¡…É•‘…Ñ…1½…Ñ¥½¹MÕÉ™…•½¹ÑÉ…Ñ½ÉMµ½­”(€€€€€€€€ôøÍÑÉ¥¹œ¹ÅÕ…±Ì¡M•ÑÑ¥¹ÍMÑ½É…•9…Ø¹Q…œü¹Q½MÑÉ¥¹œ ¤°€‰ÍÑ½É…”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€˜˜ÍÑÉ¥¹œ¹ÅÕ…±Ì (€€€€€€€€€€€€€€€ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡M•ÑÑ¥¹ÍMÑ½É…•9…Ø¤°(€€€€€€€€€€€€€€€€‰M¡…É•‘…Ñ„ÍÑ½É…”ˆ°(€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€˜˜M¡…É•‘…Ñ…I½½ÑQ•áÑ	½à¹%ÍI•…‘=¹±ä(€€€€€€€€€€€€˜˜ÍÑÉ¥¹œ¹ÅÕ…±Ì (€€€€€€€€€€€€€€€ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡M¡…É•‘…Ñ…I½½ÑQ•áÑ	½à¤°(€€€€€€€€€€€€€€€€‰ÕÉÉ•¹ĞÍ¡…É•‘…Ñ„±½…Ñ¥½¸ˆ°(€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€˜˜ÍÑÉ¥¹œ¹ÅÕ…±Ì (€€€€€€€€€€€€€€€ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ9…µ”¡=Á•¹M¡…É•‘…Ñ…½±‘•É	ÕÑÑ½¸¤°(€€€€€€€€€€€€€€€€‰=Á•¸ÕÉÉ•¹ĞÍ¡…É•‘…Ñ„™½±‘•Èˆ°(€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€˜˜€…ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡=Á•¹M¡…É•‘…Ñ…½±‘•É	ÕÑÑ½¸¹Q½½±Q¥Àü¹Q½MÑÉ¥¹œ ¤(€€€€€€€€€€€€€€€€üüÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ!•±ÁQ•áĞ¡=Á•¹M¡…É•‘…Ñ…½±‘•É	ÕÑÑ½¸¤¤(€€€€€€€€€€€€˜˜ÕÑ½µ…Ñ¥½¹AÉ½Á•ÉÑ¥•Ì¹•Ñ1¥Ù•M•ÑÑ¥¹œ¡M¡…É•‘…Ñ…MÑ…ÑÕÍQ•áĞ¤€ôôÕÑ½µ…Ñ¥½¹1¥Ù•M•ÑÑ¥¹œ¹A½±¥Ñ”ì((€€€ÁÕ‰±¥ŒM¡…É•‘…Ñ…1½…Ñ¥½¹Mµ½­•M¹…ÁÍ¡½Ğ…ÁÑÕÉ•M¡…É•‘…Ñ…1½…Ñ¥½¹½ÉMµ½­” (€€€€€€€ÍÑÉ¥¹œÍ•¹…É¥¼°(€€€€€€€ÍÑÉ¥¹œÉ½½ÑA…Ñ °(€€€€€€€‰½½°‘¥É•Ñ½Éåá¥ÍÑÌ°(€€€€€€€ÍÑÉ¥¹œ±…Õ¹¡•É	•¡…Ù¥½È¤(€€€ì(€€€€€€€Õ¹ŒñM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±ĞüøÁÉ•Ù¥½ÕÍÑ¥Ù…Ñ¥½¹AÉ½Ù¥‘•È€ô(€€€€€€€€€€€}Í¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹AÉ½Ù¥‘•Èì(€€€€€€€Õ¹ŒñÍÑÉ¥¹œ°‰½½°øÁÉ•Ù¥½ÕÍ¥É•Ñ½Éåá¥ÍÑÌ€ô}Í¡…É•‘…Ñ…I½½Ñ¥É•Ñ½Éåá¥ÍÑÌì(€€€€€€€Õ¹ŒñAÉ½•ÍÍMÑ…ÉÑ%¹™¼°‰½½°øÁÉ•Ù¥½ÕÍ1…Õ¹¡•È€ô}•áÁ±½É•É1…Õ¹¡•Èì(€€€€€€€AÉ½•ÍÍMÑ…ÉÑ%¹™¼ü…ÁÑÕÉ•€ô¹Õ±°ì(€€€€€€€ÍÑÉ¥¹œ¹½Éµ…±¥é•‘I½½Ğ€ôA…Ñ ¹•ÑÕ±±A…Ñ ¡É½½ÑA…Ñ ¤ì(€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ…Ñ¥Ù…Ñ¥½¸€ôÍ•¹…É¥¼Íİ¥Ñ (€€€€€€€ì(€€€€€€€€€€€€‰Í¡…É•ˆ€ôø¹•ÜM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ (€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹MÑ…ÑÕÌ¹Ñ¥Ù…Ñ•°(€€€€€€€€€€€€€€€¹½Éµ…±¥é•‘I½½Ğ°(€€€€€€€€€€€€€€€¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°ÍÑÉ¥¹œø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€1½…Ñ½ÉA…Ñ €ôA…Ñ ¹½µ‰¥¹”¡¹½Éµ…±¥é•‘I½½Ğ°€‰Í¡…É•µÉ½½Ğ¹ØÄ¹©Í½¸ˆ¤°(€€€€€€€€€€€€€€€I•Í½±ÕÑ¥½¹MÑ…ÑÕÌ€ôM¡…É•‘…Ñ…I½½ÑI•Í½±ÕÑ¥½¹MÑ…ÑÕÌ¹I•Í½±Ù•°(€€€€€€€€€€€ô°(€€€€€€€€€€€€‰±•…äˆ€ôø¹•ÜM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ (€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹MÑ…ÑÕÌ¹Ñ¥Ù…Ñ•°(€€€€€€€€€€€€€€€¹½Éµ…±¥é•‘I½½Ğ°(€€€€€€€€€€€€€€€¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°ÍÑÉ¥¹œø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€1½…Ñ½ÉA…Ñ €ôA…Ñ ¹½µ‰¥¹”¡¹½Éµ…±¥é•‘I½½Ğ°€‰µ¥ÍÍ¥¹œµÍ¡…É•µÉ½½Ğ¹ØÄ¹©Í½¸ˆ¤°(€€€€€€€€€€€€€€€I•Í½±ÕÑ¥½¹MÑ…ÑÕÌ€ôM¡…É•‘…Ñ…I½½ÑI•Í½±ÕÑ¥½¹MÑ…ÑÕÌ¹1•…å…±±‰…¬°(€€€€€€€€€€€ô°(€€€€€€€€€€€€‰±•…äµÕ¹¥¹¥Ñ¥…±¥é•ˆ€ôø¹•ÜM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ (€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹MÑ…ÑÕÌ¹1•…åU¹¥¹¥Ñ¥…±¥é•°(€€€€€€€€€€€€€€€¹½Éµ…±¥é•‘I½½Ğ°(€€€€€€€€€€€€€€€¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°ÍÑÉ¥¹œø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€1½…Ñ½ÉA…Ñ €ôA…Ñ ¹½µ‰¥¹”¡¹½Éµ…±¥é•‘I½½Ğ°€‰µ¥ÍÍ¥¹œµÍ¡…É•µÉ½½Ğ¹ØÄ¹©Í½¸ˆ¤°(€€€€€€€€€€€€€€€I•Í½±ÕÑ¥½¹MÑ…ÑÕÌ€ôM¡…É•‘…Ñ…I½½ÑI•Í½±ÕÑ¥½¹MÑ…ÑÕÌ¹U¹…Ù…¥±…‰±”°(€€€€€€€€€€€ô°(€€€€€€€€€€€€‰½Ù•ÉÉ¥‘•Ìˆ€ôø¹•ÜM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ (€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹MÑ…ÑÕÌ¹=Ù•ÉÉ¥‘•Í=¹±ä°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°ÍÑÉ¥¹œø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹Õ±°¤°(€€€€€€€€€€€|€ôø¹•ÜM¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹I•ÍÕ±Ğ (€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹MÑ…ÑÕÌ¹U¹…Ù…¥±…‰±”°(€€€€€€€€€€€€€€€¹Õ±°°(€€€€€€€€€€€€€€€¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°ÍÑÉ¥¹œø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€€‰Í¡…É•µÉ½½ĞµÕ¹…Ù…¥±…‰±”ˆ°(€€€€€€€€€€€€€€€€‰¥¹©•Ñ•Õ¹…Ù…¥±…‰±”ÍÑ…Ñ”ˆ¤°(€€€€€€€ôì((€€€€€€€}Í¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹AÉ½Ù¥‘•È€ô€ ¤€ôø…Ñ¥Ù…Ñ¥½¸ì(€€€€€€€}Í¡…É•‘…Ñ…I½½Ñ¥É•Ñ½Éåá¥ÍÑÌ€ôÁ…Ñ €ôø(€€€€€€€€€€€‘¥É•Ñ½Éåá¥ÍÑÌ(€€€€€€€€€€€€˜˜ÍÑÉ¥¹œ¹ÅÕ…±Ì (€€€€€€€€€€€€€€€A…Ñ ¹•ÑÕ±±A…Ñ ¡Á…Ñ ¤°(€€€€€€€€€€€€€€€¹½Éµ…±¥é•‘I½½Ğ°(€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤ì(€€€€€€€}•áÁ±½É•É1…Õ¹¡•È€ô¥¹™¼€ôø(€€€€€€€ì(€€€€€€€€€€€…ÁÑÕÉ•€ô¥¹™¼ì(€€€€€€€€€€€É•ÑÕÉ¸±…Õ¹¡•É	•¡…Ù¥½ÈÍİ¥Ñ (€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€‰ÍÕ•ÍÌˆ€ôøÑÉÕ”°(€€€€€€€€€€€€€€€€‰™…¥±ÕÉ”ˆ€ôø™…±Í”°(€€€€€€€€€€€€€€€€‰Ñ¡É½Üˆ€ôøÑ¡É½Ü¹•Ü%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸ ‰¥¹©•Ñ•Í¡…É•‘…Ñ„Í¡•±°™…¥±ÕÉ”ˆ¤°(€€€€€€€€€€€€€€€|€ôøÑ¡É½Ü¹•ÜÉÕµ•¹Ñ=ÕÑ=™I…¹•á•ÁÑ¥½¸¡¹…µ•½˜¡±…Õ¹¡•É	•¡…Ù¥½È¤¤°(€€€€€€€€€€€ôì(€€€€€€€ôì((€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€¥˜€¡ÁÁM•ÑÑ¥¹Í¥…±½œ¹Y¥Í¥‰¥±¥Ñä€„ôY¥Í¥‰¥±¥Ñä¹Y¥Í¥‰±”¤(€€€€€€€€€€€€€€€=Á•¹ÁÁM•ÑÑ¥¹Í½ÉMµ½­” ¤ì(€€€€€€€€€€€M•±•ÑÁÁM•ÑÑ¥¹ÍM•Ñ¥½¸ ‰ÍÑ½É…”ˆ°‰É¥¹%¹Ñ½Y¥•ÜèÑÉÕ”¤ì(€€€€€€€€€€€‰½½°½Á•¹¹…‰±•€ô=Á•¹M¡…É•‘…Ñ…½±‘•É	ÕÑÑ½¸¹%Í¹…‰±•ì(€€€€€€€€€€€‰½½°½Á•¹•€ôQÉå=Á•¹M¡…É•‘…Ñ…½±‘•È ¤ì(€€€€€€€€€€€É•ÑÕÉ¸¹•ÜM¡…É•‘…Ñ…1½…Ñ¥½¹Mµ½­•M¹…ÁÍ¡½Ğ (€€€€€€€€€€€€€€€Í•¹…É¥¼°(€€€€€€€€€€€€€€€}Í¡…É•‘…Ñ…1½…Ñ¥½¸¹-¥¹°(€€€€€€€€€€€€€€€M¡…É•‘…Ñ…5½‘•Q•áĞ¹Q•áĞ°(€€€€€€€€€€€€€€€M¡…É•‘…Ñ…•ÍÉ¥ÁÑ¥½¹Q•áĞ¹Q•áĞ°(€€€€€€€€€€€€€€€M¡…É•‘…Ñ…I½½ÑQ•áÑ	½à¹Q•áĞ°(€€€€€€€€€€€€€€€½Á•¹¹…‰±•°(€€€€€€€€€€€€€€€½Á•¹•°(€€€€€€€€€€€€€€€…ÁÑÕÉ•¥Ì¹½Ğ¹Õ±°°(€€€€€€€€€€€€€€€…ÁÑÕÉ•ü¹¥±•9…µ”€üü€ˆˆ°(€€€€€€€€€€€€€€€…ÁÑÕÉ•ü¹ÉÕµ•¹Ñ1¥ÍĞ¹Q½1¥ÍĞ ¤€üümt°(€€€€€€€€€€€€€€€…ÁÑÕÉ•ü¹ÉÕµ•¹ÑÌ€üü€ˆˆ°(€€€€€€€€€€€€€€€…ÁÑÕÉ•ü¹UÍ•M¡•±±á•ÕÑ”€üü™…±Í”°(€€€€€€€€€€€€€€€M¡…É•‘…Ñ…MÑ…ÑÕÍQ•áĞ¹Q•áĞ°(€€€€€€€€€€€€€€€M¡…É•‘…Ñ…1½…Ñ¥½¹MÕÉ™…•½¹ÑÉ…Ñ½ÉMµ½­”°(€€€€€€€€€€€€€€€%ÍM•ÑÑ¥¹Í¥…±½½ÕÍ•‘½ÉMµ½­”¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€}Í¡…É•‘…Ñ…I½½ÑÑ¥Ù…Ñ¥½¹AÉ½Ù¥‘•È€ôÁÉ•Ù¥½ÕÍÑ¥Ù…Ñ¥½¹AÉ½Ù¥‘•Èì(€€€€€€€€€€€}Í¡…É•‘…Ñ…I½½Ñ¥É•Ñ½Éåá¥ÍÑÌ€ôÁÉ•Ù¥½ÕÍ¥É•Ñ½Éåá¥ÍÑÌì(€€€€€€€€€€€}•áÁ±½É•É1…Õ¹¡•È€ôÁÉ•Ù¥½ÕÍ1…Õ¹¡•Èì(€€€€€€€€€€€I•™É•Í¡M¡…É•‘…Ñ…M•ÑÑ¥¹Ì ¤ì(€€€€€€€ô(€€€ô((€€€ÁÕ‰±¥ŒáÑ•É¹…±=Á•¹Mµ½­•M¹…ÁÍ¡½ĞÑ¥Ù…Ñ•áÑ•É¹…±=Á•¹½ÉMµ½­”¡ÍÑÉ¥¹œ±…Õ¹¡•É	•¡…Ù¥½È¤(€€€ì(€€€€€€€AÉ½•ÍÍMÑ…ÉÑ%¹™¼ü…ÁÑÕÉ•€ô¹Õ±°ì(€€€€€€€Õ¹ŒñAÉ½•ÍÍMÑ…ÉÑ%¹™¼°‰½½°øÁÉ
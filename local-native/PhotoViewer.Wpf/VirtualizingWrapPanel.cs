using System.Globalization;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public sealed class VirtualizingWrapPanelRangeChangedEventArgs(
    int firstVisibleIndex,
    int lastVisibleIndex,
    int firstRealizedIndex,
    int lastRealizedIndex) : EventArgs
{
    public int FirstVisibleIndex { get; } = firstVisibleIndex;
    public int LastVisibleIndex { get; } = lastVisibleIndex;
    public int FirstRealizedIndex { get; } = firstRealizedIndex;
    public int LastRealizedIndex { get; } = lastRealizedIndex;
}

internal readonly record struct VirtualizingLayoutItem(string Group, double ItemHeight);

internal readonly record struct ItemsResetPreparationSlice(
    bool Complete,
    int DetachedContainerCount,
    double GeneratorRemoveMs,
    double ForgetDeferredMeasureMs,
    double RemoveInternalChildRangeMs,
    double RemoveInternalChildRangeThreadCpuMs,
    double PanelTotalMs,
    double PanelThreadCpuMs);

internal readonly record struct VirtualizingMeasureDiagnostic(
    string Operation,
    long LayoutGeneration,
    double WallMs,
    double ThreadCpuMs);

internal readonly record struct VirtualizingLayoutContext(
    double AvailableWidth,
    double ItemWidth,
    double ItemHeight,
    double HorizontalSpacing,
    double VerticalSpacing,
    bool ForceSingleColumn,
    bool ShowGroupHeaders,
    double GroupHeaderHeight);

internal readonly record struct VirtualizingGroupHeaderInfo(string Label, int Count);

internal sealed record PreparedVirtualizingLayout(
    VirtualizingLayoutContext Context,
    int ItemCount,
    List<double> RowTops,
    List<double> RowHeights,
    List<int> RowFirstIndices,
    List<int> RowItemCounts,
    List<VirtualizingGroupHeaderInfo?> RowHeaders,
    int[] ItemRows,
    Size Extent,
    int Columns,
    double CellWidth);

/// <summary>
/// Pixel-scrolling virtualizing panel for the gallery's uniform-width,
/// variable-height cards.  The complete item order owns the scroll extent;
/// only visible rows plus a small overscan are materialized.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private const double DefaultItemWidth = 204;
    private const double DefaultItemHeight = 304;
    // Extreme thumbnail zoom can expose hundreds of cards at once. Preparing
    // them in bounded Dispatcher slices keeps input and native window messages
    // responsive while the same visible + overscan range fills progressively.
    // A card template is materially more expensive than the panel's row math.
    // At minimum zoom a single viewport can contain hundreds of cards, and a
    // 96-container slice held the dispatcher for more than 750 ms on the 10k
    // catalog gate. Keep each realization slice below one input-stall budget;
    // the remaining viewport is already represented by progressive placeholders.
    private const int InteractiveContainersPerMeasure = 1;
    private const int MediumDensityContainersPerMeasure = 4;
    private const int DenseContainersPerMeasure = 16;
    // Leave a small margin beyond the 15 ms input heartbeat before the next
    // progressive Render invalidation. Equal-frame scheduling let the render
    // continuation and input timer race on busy runners, occasionally joining
    // two otherwise bounded card preparations into one visible input gap.
    private const int RealizationContinuationDelayMilliseconds = 20;
    private static readonly Brush ProgressivePlaceholderBrush = CreateProgressivePlaceholderBrush();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(
        IntPtr thread,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty OverscanRowsProperty = DependencyProperty.Register(
        nameof(OverscanRows),
        typeof(int),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ForceSingleColumnProperty = DependencyProperty.Register(
        nameof(ForceSingleColumn),
        typeof(bool),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ShowGroupHeadersProperty = DependencyProperty.Register(
        nameof(ShowGroupHeaders),
        typeof(bool),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutPropertyChanged));

    public static readonly DependencyProperty GroupHeaderHeightProperty = DependencyProperty.Register(
        nameof(GroupHeaderHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(46d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutPropertyChanged));

    private List<double> _rowTops = [];
    private List<double> _rowHeights = [];
    private List<int> _rowFirstIndices = [];
    private List<int> _rowItemCounts = [];
    private List<VirtualizingGroupHeaderInfo?> _rowHeaders = [];
    private int[] _itemRows = [];
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private bool _layoutDirty = true;
    private int _layoutItemCount = -1;
    private int _columns = 1;
    private double _cellWidth = DefaultItemWidth;
    private double _layoutWidth = -1;
    private double _layoutItemWidthSignature = -1;
    private double _layoutItemHeightSignature = -1;
    private IReadOnlyList<Tile>? _layoutSource;
    private int _firstVisibleIndex = -1;
    private int _lastVisibleIndex = -1;
    private int _firstRealizedIndex = -1;
    private int _lastRealizedIndex = -1;
    private bool _realizationContinuationPending;
    private int _priorityRealizationIndex = -1;
    private readonly HashSet<UIElement> _deferredMeasureContainers = [];
    private bool _itemsResetPreparationActive;
    private int _itemsResetGeneratorPosition = -1;
    private VirtualizedGalleryListBox? _itemsResetContainerReuseOwner;
    private long _layoutGeneration;
    private PreparedVirtualizingLayout? _preparedLayout;
    private int _preparedLayoutAppliedCount;
    private int _preparedLayoutRejectedCount;
    private long _maxMeasureMilliseconds;
    private string _diagnosticOperation = "";
    private bool _diagnosticMeasureThreadCpuEnabled;
    private VirtualizingMeasureDiagnostic _maxMeasureDiagnostic =
        new("", 0, 0, -1);

    public event EventHandler<VirtualizingWrapPanelRangeChangedEventArgs>? RealizedRangeChanged;

    public VirtualizingWrapPanel()
        => Unloaded += (_, _) => CancelPendingRealization();

    internal void CancelPendingRealization()
    {
        _layoutGeneration++;
        _realizationContinuationPending = false;
        _priorityRealizationIndex = -1;
        _deferredMeasureContainers.Clear();
        InvalidateVisual();
    }

    internal void ResumePendingRealization()
        => InvalidateMeasure();

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public int OverscanRows
    {
        get => (int)GetValue(OverscanRowsProperty);
        set => SetValue(OverscanRowsProperty, value);
    }

    public bool ForceSingleColumn
    {
        get => (bool)GetValue(ForceSingleColumnProperty);
        set => SetValue(ForceSingleColumnProperty, value);
    }

    public bool ShowGroupHeaders
    {
        get => (bool)GetValue(ShowGroupHeadersProperty);
        set => SetValue(ShowGroupHeadersProperty, value);
    }

    public double GroupHeaderHeight
    {
        get => (double)GetValue(GroupHeaderHeightProperty);
        set => SetValue(GroupHeaderHeightProperty, value);
    }

    public int FirstVisibleIndex => _firstVisibleIndex;
    public int LastVisibleIndex => _lastVisibleIndex;
    public int FirstRealizedIndex => _firstRealizedIndex;
    public int LastRealizedIndex => _lastRealizedIndex;
    public int ColumnCount => _columns;
    public int RealizedItemCount => InternalChildren.Count;
    public bool RealizationContinuationPending => _realizationContinuationPending;
    public int VisiblePlaceholderCount => _realizationContinuationPending ? CountVisibleUnrealizedItems() : 0;
    public int VisibleUnrealizedItemCount => CountVisibleUnrealizedItems();
    public long LayoutGeneration => _layoutGeneration;
    internal int PreparedLayoutAppliedCount => _preparedLayoutAppliedCount;
    internal int PreparedLayoutRejectedCount => _preparedLayoutRejectedCount;
    internal long MaxMeasureMilliseconds => _maxMeasureMilliseconds;
    internal VirtualizingMeasureDiagnostic MaxMeasureDiagnostic => _maxMeasureDiagnostic;

    internal void SetDiagnosticOperation(string operation)
    {
        _diagnosticOperation = operation ?? "";
        _diagnosticMeasureThreadCpuEnabled =
            !string.IsNullOrEmpty(_diagnosticOperation);
    }

    internal void SetLayoutSource(IReadOnlyList<Tile> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(_layoutSource, source))
            return;
        _layoutSource = source;
        MarkLayoutDirty();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void InvalidateItemLayout()
    {
        _preparedLayout = null;
        MarkLayoutDirty();
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal bool BeginItemsResetPreparation()
    {
        if (_itemsResetPreparationActive)
            throw new InvalidOperationException("Items reset preparation is already active.");
        _realizationContinuationPending = false;
        _priorityRealizationIndex = -1;
        _deferredMeasureContainers.Clear();
        int realizedCount = InternalChildren.Count;
        if (realizedCount == 0)
            return false;

        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int childIndex = 0; childIndex < realizedCount; childIndex++)
        {
            if (generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0)) >= 0)
                continue;
            ResyncGeneratorVisuals();
            return false;
        }

        _itemsResetPreparationActive = true;
        _itemsResetGeneratorPosition = realizedCount - 1;
        _itemsResetContainerReuseOwner =
            ItemsControl.GetItemsOwner(this) as VirtualizedGalleryListBox;
        return true;
    }

    internal ItemsResetPreparationSlice PrepareNextItemsResetSlice()
    {
        if (!_itemsResetPreparationActive)
            throw new InvalidOperationException("Items reset preparation is not active.");

        // Match ordinary viewport cleanup: release one generator entry before
        // detaching its tail visual. Reset preparation previously removed every
        // visual first and only then updated the generator map, leaving WPF to
        // unlink a still-live container while the visual tree was changing.
        // Keep the official generator-first pair as one bounded container unit
        // and yield before the next item.
        double generatorRemoveMs = 0;
        double forgetDeferredMeasureMs = 0;
        double removeInternalChildRangeMs = 0;
        double removeInternalChildRangeThreadCpuMs = 0;
        int detachedContainerCount = 0;
        long sliceStarted = Stopwatch.GetTimestamp();
        long sliceCpuStarted = ReadCurrentThreadCpuTimeTicks();
        if (_itemsResetGeneratorPosition >= 0)
        {
            int childIndex = InternalChildren.Count - 1;
            UIElement detachedChild = InternalChildren[childIndex];
            long generatorStarted = Stopwatch.GetTimestamp();
            ItemContainerGenerator.Remove(
                new GeneratorPosition(_itemsResetGeneratorPosition, 0),
                1);
            long generatorFinished = Stopwatch.GetTimestamp();
            ForgetDeferredMeasureRange(childIndex, 1);
            long forgetFinished = Stopwatch.GetTimestamp();
            long visualCpuStarted = ReadCurrentThreadCpuTimeTicks();
            RemoveInternalChildRange(childIndex, 1);
            long visualCpuFinished = ReadCurrentThreadCpuTimeTicks();
            long visualFinished = Stopwatch.GetTimestamp();
            // Keep custom reuse ownership out of WPF's unlink callbacks. The
            // container becomes reusable only after the visual detach above.
            if (detachedChild is ListBoxItem detachedContainer)
                _itemsResetContainerReuseOwner?.CacheDetachedContainer(detachedContainer);
            detachedContainerCount = 1;
            generatorRemoveMs =
                Stopwatch.GetElapsedTime(generatorStarted, generatorFinished).TotalMilliseconds;
            forgetDeferredMeasureMs =
                Stopwatch.GetElapsedTime(generatorFinished, forgetFinished).TotalMilliseconds;
            removeInternalChildRangeMs =
                Stopwatch.GetElapsedTime(forgetFinished, visualFinished).TotalMilliseconds;
            removeInternalChildRangeThreadCpuMs =
                visualCpuStarted >= 0 && visualCpuFinished >= visualCpuStarted
                    ? TimeSpan.FromTicks(visualCpuFinished - visualCpuStarted).TotalMilliseconds
                    : -1;
            _itemsResetGeneratorPosition--;
        }

        long sliceCpuFinished = ReadCurrentThreadCpuTimeTicks();
        long sliceFinished = Stopwatch.GetTimestamp();
        double panelThreadCpuMs =
            sliceCpuStarted >= 0 && sliceCpuFinished >= sliceCpuStarted
                ? TimeSpan.FromTicks(sliceCpuFinished - sliceCpuStarted).TotalMilliseconds
                : -1;
        return new ItemsResetPreparationSlice(
            _itemsResetGeneratorPosition < 0,
            detachedContainerCount,
            generatorRemoveMs,
            forgetDeferredMeasureMs,
            removeInternalChildRangeMs,
            removeInternalChildRangeThreadCpuMs,
            Stopwatch.GetElapsedTime(sliceStarted, sliceFinished).TotalMilliseconds,
            panelThreadCpuMs);
    }

    private static long ReadCurrentThreadCpuTimeTicks()
    {
        return GetThreadTimes(
            GetCurrentThread(),
            out _,
            out _,
            out long kernelTime,
            out long userTime)
            ? kernelTime + userTime
            : -1;
    }

    internal void CompleteItemsResetPreparation()
    {
        _itemsResetPreparationActive = false;
        _itemsResetGeneratorPosition = -1;
        _itemsResetContainerReuseOwner = null;
    }

    internal void CancelItemsResetPreparation()
    {
        if (!_itemsResetPreparationActive)
            return;
        ClearVisualChildren();
        ItemContainerGenerator.RemoveAll();
        CompleteItemsResetPreparation();
        MarkLayoutDirty();
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal VirtualizingLayoutContext? CaptureLayoutContext()
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        double availableWidth = ResolveViewportLength(_viewport.Width, ActualWidth, 1);
        double itemWidth = ResolveItemWidth(owner, 0);
        double itemHeight = ResolveItemHeight(owner, 0);
        if (!double.IsFinite(availableWidth)
            || !double.IsFinite(itemWidth)
            || availableWidth <= 0
            || itemWidth <= 0)
        {
            return null;
        }

        return new VirtualizingLayoutContext(
            availableWidth,
            itemWidth,
            itemHeight,
            Math.Max(0, HorizontalSpacing),
            Math.Max(0, VerticalSpacing),
            ForceSingleColumn,
            ShowGroupHeaders,
            Math.Max(24, GroupHeaderHeight));
    }

    internal void SetPreparedLayout(PreparedVirtualizingLayout? layout)
    {
        _preparedLayout = layout;
        MarkLayoutDirty();
        InvalidateMeasure();
        InvalidateVisual();
    }

    internal static PreparedVirtualizingLayout PrepareLayout(
        IReadOnlyList<VirtualizingLayoutItem> items,
        VirtualizingLayoutContext context,
        CancellationToken cancellationToken)
    {
        int itemCount = items.Count;
        double cellWidth = Math.Max(1, context.ItemWidth + context.HorizontalSpacing);
        int columns = context.ForceSingleColumn
            ? 1
            : Math.Max(
                1,
                (int)Math.Floor(
                    (Math.Max(1, context.AvailableWidth) + context.HorizontalSpacing)
                    / cellWidth));
        var rowTops = new List<double>(Math.Max(1, (itemCount + columns - 1) / columns));
        var rowHeights = new List<double>(rowTops.Capacity);
        var rowFirstIndices = new List<int>(rowTops.Capacity);
        var rowItemCounts = new List<int>(rowTops.Capacity);
        var rowHeaders = new List<VirtualizingGroupHeaderInfo?>(rowTops.Capacity);
        var itemRows = new int[itemCount];
        double y = 0;
        int groupStart = 0;
        while (groupStart < itemCount)
        {
            if ((groupStart & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            string group = items[groupStart].Group;
            int groupEnd = context.ShowGroupHeaders ? groupStart + 1 : itemCount;
            while (context.ShowGroupHeaders
                && groupEnd < itemCount
                && string.Equals(items[groupEnd].Group, group, StringComparison.Ordinal))
            {
                if ((groupEnd & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                groupEnd++;
            }

            if (context.ShowGroupHeaders)
            {
                AddPreparedRow(
                    rowTops,
                    rowHeights,
                    rowFirstIndices,
                    rowItemCounts,
                    rowHeaders,
                    groupStart,
                    0,
                    y,
                    context.GroupHeaderHeight,
                    new VirtualizingGroupHeaderInfo(group, groupEnd - groupStart));
                y += context.GroupHeaderHeight;
            }

            for (int first = groupStart; first < groupEnd; first += columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Math.Min(groupEnd, first + columns);
                double rowHeight = 1;
                for (int index = first; index < end; index++)
                {
                    double itemHeight = double.IsFinite(context.ItemHeight) && context.ItemHeight > 0
                        ? context.ItemHeight
                        : Math.Max(1, items[index].ItemHeight);
                    rowHeight = Math.Max(rowHeight, itemHeight + context.VerticalSpacing);
                }
                int row = rowTops.Count;
                AddPreparedRow(
                    rowTops,
                    rowHeights,
                    rowFirstIndices,
                    rowItemCounts,
                    rowHeaders,
                    first,
                    end - first,
                    y,
                    rowHeight,
                    null);
                for (int index = first; index < end; index++)
                    itemRows[index] = row;
                y += rowHeight;
            }

            groupStart = groupEnd;
        }

        return new PreparedVirtualizingLayout(
            context,
            itemCount,
            rowTops,
            rowHeights,
            rowFirstIndices,
            rowItemCounts,
            rowHeaders,
            itemRows,
            new Size(Math.Max(context.AvailableWidth, columns * cellWidth), y),
            columns,
            cellWidth);
    }

    internal static PreparedVirtualizingLayout PrepareUniformLayout(
        int itemCount,
        VirtualizingLayoutContext context,
        string group,
        double itemHeight,
        CancellationToken cancellationToken)
    {
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        cancellationToken.ThrowIfCancellationRequested();

        double cellWidth = Math.Max(1, context.ItemWidth + context.HorizontalSpacing);
        int columns = context.ForceSingleColumn
            ? 1
            : Math.Max(
                1,
                (int)Math.Floor(
                    (Math.Max(1, context.AvailableWidth) + context.HorizontalSpacing)
                    / cellWidth));
        int itemRowCount = (itemCount + columns - 1) / columns;
        int totalRowCount = itemRowCount + (context.ShowGroupHeaders && itemCount > 0 ? 1 : 0);
        var rowTops = new List<double>(totalRowCount);
        var rowHeights = new List<double>(totalRowCount);
        var rowFirstIndices = new List<int>(totalRowCount);
        var rowItemCounts = new List<int>(totalRowCount);
        var rowHeaders = new List<VirtualizingGroupHeaderInfo?>(totalRowCount);
        var itemRows = new int[itemCount];
        double y = 0;
        if (context.ShowGroupHeaders && itemCount > 0)
        {
            AddPreparedRow(
                rowTops,
                rowHeights,
                rowFirstIndices,
                rowItemCounts,
                rowHeaders,
                0,
                0,
                y,
                context.GroupHeaderHeight,
                new VirtualizingGroupHeaderInfo(group, itemCount));
            y += context.GroupHeaderHeight;
        }

        double rowHeight = Math.Max(1, itemHeight) + context.VerticalSpacing;
        for (int first = 0; first < itemCount; first += columns)
        {
            if ((first & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(columns, itemCount - first);
            int row = rowTops.Count;
            AddPreparedRow(
                rowTops,
                rowHeights,
                rowFirstIndices,
                rowItemCounts,
                rowHeaders,
                first,
                count,
                y,
                rowHeight,
                null);
            for (int index = first; index < first + count; index++)
                itemRows[index] = row;
            y += rowHeight;
        }

        return new PreparedVirtualizingLayout(
            context,
            itemCount,
            rowTops,
            rowHeights,
            rowFirstIndices,
            rowItemCounts,
            rowHeaders,
            itemRows,
            new Size(Math.Max(context.AvailableWidth, columns * cellWidth), y),
            columns,
            cellWidth);
    }

    private static void AddPreparedRow(
        List<double> rowTops,
        List<double> rowHeights,
        List<int> rowFirstIndices,
        List<int> rowItemCounts,
        List<VirtualizingGroupHeaderInfo?> rowHeaders,
        int firstIndex,
        int itemCount,
        double top,
        double height,
        VirtualizingGroupHeaderInfo? header)
    {
        rowFirstIndices.Add(firstIndex);
        rowItemCounts.Add(itemCount);
        rowTops.Add(top);
        rowHeights.Add(height);
        rowHeaders.Add(header);
    }

    public double GetItemViewportTop(int index)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        if (index < 0 || index >= count)
            return double.NaN;
        EnsureLayout(owner, count, ResolveViewportLength(_viewport.Width, ActualWidth, 1));
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        return row >= 0 && row < _rowTops.Count ? _rowTops[row] - _offset.Y : double.NaN;
    }

    public bool RestoreItemViewportTop(int index, double viewportTop)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        if (index < 0 || index >= count || !double.IsFinite(viewportTop))
            return false;
        EnsureLayout(owner, count, ResolveViewportLength(_viewport.Width, ActualWidth, 1));
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        if (row < 0 || row >= _rowTops.Count)
            return false;
        SetVerticalOffset(_rowTops[row] - viewportTop);
        return true;
    }

    public bool BringItemIntoView(int index)
    {
        if (_itemsResetPreparationActive)
            return false;
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        if (index < 0 || index >= count)
            return false;

        EnsureLayout(owner, count, ResolveViewportLength(_viewport.Width, ActualWidth, 1));
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        if (row < 0 || row >= _rowTops.Count)
            return false;
        _priorityRealizationIndex = index;
        BringRowIntoView(row);
        InvalidateMeasure();
        return true;
    }

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is not VirtualizingWrapPanel panel)
            return;
        panel._preparedLayout = null;
        panel.MarkLayoutDirty();
        panel.InvalidateMeasure();
        panel.InvalidateVisual();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                RemoveChangedVisualRange(args.Position, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveChangedVisualRange(args.OldPosition, args.ItemUICount);
                break;
        }
        MarkLayoutDirty();
        InvalidateVisual();
    }

    private void RemoveChangedVisualRange(GeneratorPosition position, int count)
    {
        if (count <= 0)
            return;

        int childIndex = position.Index + (position.Offset > 0 ? 1 : 0);
        if (childIndex < 0 || childIndex + count > InternalChildren.Count)
        {
            ResyncGeneratorVisuals();
            return;
        }

        // ItemContainerGenerator has already applied the source mutation when
        // this notification reaches the panel. Mirror WPF's
        // VirtualizingStackPanel behavior and detach only the corresponding
        // visual range; calling generator.Remove here would mutate the new map.
        ForgetDeferredMeasureRange(childIndex, count);
        RemoveInternalChildRange(childIndex, count);
    }

    private void ClearVisualChildren()
    {
        _deferredMeasureContainers.Clear();
        if (InternalChildren.Count > 0)
            RemoveInternalChildRange(0, InternalChildren.Count);
    }

    private void ResyncGeneratorVisuals()
    {
        ItemContainerGenerator.RemoveAll();
        ClearVisualChildren();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var measureWatch = Stopwatch.StartNew();
        long measureCpuStarted = _diagnosticMeasureThreadCpuEnabled
            ? ReadCurrentThreadCpuTimeTicks()
            : -1;
        string measureOperation = _diagnosticOperation;
        long measureGeneration = _layoutGeneration;
        Size CompleteMeasure(Size result)
        {
            measureWatch.Stop();
            _maxMeasureMilliseconds = Math.Max(_maxMeasureMilliseconds, measureWatch.ElapsedMilliseconds);
            long measureCpuFinished = _diagnosticMeasureThreadCpuEnabled
                ? ReadCurrentThreadCpuTimeTicks()
                : -1;
            double wallMs = measureWatch.Elapsed.TotalMilliseconds;
            double threadCpuMs =
                measureCpuStarted >= 0 && measureCpuFinished >= measureCpuStarted
                    ? TimeSpan.FromTicks(measureCpuFinished - measureCpuStarted).TotalMilliseconds
                    : -1;
            if (_diagnosticMeasureThreadCpuEnabled
                && wallMs > _maxMeasureDiagnostic.WallMs)
            {
                _maxMeasureDiagnostic = new VirtualizingMeasureDiagnostic(
                    measureOperation,
                    measureGeneration,
                    wallMs,
                    threadCpuMs);
            }
            return result;
        }

        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int itemCount = owner?.Items.Count ?? 0;
        double viewportWidth = ResolveViewportLength(availableSize.Width, ActualWidth, 1);
        double viewportHeight = ResolveViewportLength(availableSize.Height, ActualHeight, ScrollOwner?.ActualHeight ?? 1);
        _viewport = new Size(viewportWidth, viewportHeight);

        if (_itemsResetPreparationActive)
        {
            UpdateScrollInfo();
            return CompleteMeasure(availableSize);
        }

        EnsureLayout(owner, itemCount, viewportWidth);
        CoerceOffsets();

        if (itemCount == 0 || _rowTops.Count == 0)
        {
            bool emptyCleanupComplete = CleanupItems(0, -1);
            UpdateRange(-1, -1, -1, -1);
            UpdateScrollInfo();
            if (!emptyCleanupComplete)
                ScheduleRealizationContinuation();
            return CompleteMeasure(availableSize);
        }

        int firstVisibleRow = FindFirstRowWhoseBottomExceeds(_offset.Y);
        int lastVisibleRow = FindLastRowWhoseTopPrecedes(_offset.Y + _viewport.Height);
        int overscan = Math.Max(0, OverscanRows);
        int firstRealizedRow = Math.Max(0, firstVisibleRow - overscan);
        int lastRealizedRow = Math.Min(_rowTops.Count - 1, lastVisibleRow + overscan);
        int firstVisibleIndex = FirstItemIndexForRows(firstVisibleRow, lastVisibleRow, itemCount);
        int lastVisibleIndex = LastItemIndexForRows(firstVisibleRow, lastVisibleRow, itemCount);
        int firstRealizedIndex = FirstItemIndexForRows(firstRealizedRow, lastRealizedRow, itemCount);
        int lastRealizedIndex = LastItemIndexForRows(firstRealizedRow, lastRealizedRow, itemCount);

        bool cleanupComplete = CleanupItems(firstRealizedIndex, lastRealizedIndex);
        int visibleItemCount = Math.Max(0, lastVisibleIndex - firstVisibleIndex + 1);
        int containerBudget = visibleItemCount >= 128
            ? DenseContainersPerMeasure
            : visibleItemCount >= 32
                ? MediumDensityContainersPerMeasure
                : InteractiveContainersPerMeasure;
        if (_priorityRealizationIndex >= firstRealizedIndex
            && _priorityRealizationIndex <= lastRealizedIndex)
        {
            int priorityIndex = _priorityRealizationIndex;
            if (!RealizePriorityItem(priorityIndex, out bool consumedPrioritySlice))
            {
                UpdateRange(
                    firstVisibleIndex,
                    lastVisibleIndex,
                    priorityIndex,
                    priorityIndex);
                UpdateScrollInfo();
                ScheduleRealizationContinuation();
                return CompleteMeasure(availableSize);
            }
            _priorityRealizationIndex = -1;
            if (consumedPrioritySlice)
            {
                // A distant Home/End jump first prepares the target container,
                // then measures it on this pass. Do not also generate another
                // visible card in the same Render frame; let Input run before
                // the continuation fills the rest of the viewport.
                UpdateRange(
                    firstVisibleIndex,
                    lastVisibleIndex,
                    priorityIndex,
                    priorityIndex);
                UpdateScrollInfo();
                ScheduleRealizationContinuation();
                return CompleteMeasure(availableSize);
            }
        }
        bool realizationComplete = RealizeItems(
            firstRealizedIndex,
            lastRealizedIndex,
            containerBudget,
            out int lastProcessedIndex);
        int reportedFirstRealizedIndex = lastProcessedIndex >= firstRealizedIndex ? firstRealizedIndex : -1;
        UpdateRange(firstVisibleIndex, lastVisibleIndex, reportedFirstRealizedIndex, lastProcessedIndex);
        UpdateScrollInfo();
        if (!cleanupComplete || !realizationComplete)
            ScheduleRealizationContinuation();
        else if (_realizationContinuationPending)
        {
            _realizationContinuationPending = false;
            InvalidateVisual();
        }
        return CompleteMeasure(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_itemsResetPreparationActive)
            return finalSize;
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            UIElement child = InternalChildren[childIndex];
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0 || _columns <= 0)
                continue;

            int row = itemIndex < _itemRows.Length ? _itemRows[itemIndex] : -1;
            if (row < 0 || row >= _rowTops.Count)
                continue;
            int column = Math.Max(0, itemIndex - _rowFirstIndices[row]);
            double x = column * _cellWidth - _offset.X;
            double y = _rowTops[row] - _offset.Y;
            child.Arrange(new Rect(x, y, _cellWidth, _rowHeights[row]));
        }
        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
        => BringItemIntoView(index);

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (_itemsResetPreparationActive)
            return Rect.Empty;
        UIElement? child = visual as UIElement;
        while (child is not null && !InternalChildren.Contains(child))
            child = VisualTreeHelper.GetParent(child) as UIElement;
        if (child is null)
            return Rect.Empty;

        int childIndex = InternalChildren.IndexOf(child);
        int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
            return Rect.Empty;
        int row = itemIndex < _itemRows.Length ? _itemRows[itemIndex] : -1;
        BringRowIntoView(row);
        return ItemRect(itemIndex);
    }

    private void BringRowIntoView(int row)
    {
        double rowTop = _rowTops[row];
        double rowBottom = rowTop + _rowHeights[row];
        if (rowTop < _offset.Y)
            SetVerticalOffset(rowTop);
        else if (rowBottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(rowBottom - _viewport.Height);
    }

    private Rect ItemRect(int index)
    {
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        if (row < 0 || row >= _rowTops.Count)
            return Rect.Empty;
        int column = Math.Max(0, index - _rowFirstIndices[row]);
        return new Rect(column * _cellWidth, _rowTops[row] - _offset.Y, _cellWidth, _rowHeights[row]);
    }

    private void EnsureLayout(ItemsControl? owner, int itemCount, double availableWidth)
    {
        double itemWidthSignature = ResolveItemWidth(owner, 0);
        double itemHeightSignature = ResolveItemHeight(owner, 0);
        if (_preparedLayout is { } prepared)
        {
            var currentContext = new VirtualizingLayoutContext(
                availableWidth,
                itemWidthSignature,
                itemHeightSignature,
                Math.Max(0, HorizontalSpacing),
                Math.Max(0, VerticalSpacing),
                ForceSingleColumn,
                ShowGroupHeaders,
                Math.Max(24, GroupHeaderHeight));
            if (prepared.ItemCount == itemCount
                && LayoutContextsMatch(prepared.Context, currentContext))
            {
                _layoutGeneration++;
                _layoutDirty = false;
                _realizationContinuationPending = false;
                _layoutItemCount = itemCount;
                _layoutWidth = availableWidth;
                _layoutItemWidthSignature = itemWidthSignature;
                _layoutItemHeightSignature = itemHeightSignature;
                _rowTops = prepared.RowTops;
                _rowHeights = prepared.RowHeights;
                _rowFirstIndices = prepared.RowFirstIndices;
                _rowItemCounts = prepared.RowItemCounts;
                _rowHeaders = prepared.RowHeaders;
                _itemRows = prepared.ItemRows;
                _extent = prepared.Extent;
                _columns = prepared.Columns;
                _cellWidth = prepared.CellWidth;
                _preparedLayout = null;
                _preparedLayoutAppliedCount++;
                ScrollOwner?.InvalidateScrollInfo();
                InvalidateVisual();
                return;
            }
            _preparedLayout = null;
            _preparedLayoutRejectedCount++;
        }

        if (!_layoutDirty
            && _layoutItemCount == itemCount
            && AreClose(_layoutWidth, availableWidth)
            && AreClose(_layoutItemWidthSignature, itemWidthSignature)
            && AreClose(_layoutItemHeightSignature, itemHeightSignature))
        {
            return;
        }

        _layoutGeneration++;
        _layoutDirty = false;
        _realizationContinuationPending = false;
        _layoutItemCount = itemCount;
        _layoutWidth = availableWidth;
        _layoutItemWidthSignature = itemWidthSignature;
        _layoutItemHeightSignature = itemHeightSignature;
        _rowTops.Clear();
        _rowHeights.Clear();
        _rowFirstIndices.Clear();
        _rowItemCounts.Clear();
        _rowHeaders.Clear();
        if (itemCount > _itemRows.Length)
            _itemRows = new int[itemCount];

        double spacingX = Math.Max(0, HorizontalSpacing);
        double spacingY = Math.Max(0, VerticalSpacing);
        _cellWidth = Math.Max(1, itemWidthSignature + spacingX);
        _columns = ForceSingleColumn
            ? 1
            : Math.Max(1, (int)Math.Floor((Math.Max(1, availableWidth) + spacingX) / _cellWidth));
        double y = 0;
        int groupStart = 0;
        while (groupStart < itemCount)
        {
            string group = ResolveGroup(owner, groupStart);
            int groupEnd = ShowGroupHeaders ? groupStart + 1 : itemCount;
            while (ShowGroupHeaders && groupEnd < itemCount && string.Equals(ResolveGroup(owner, groupEnd), group, StringComparison.Ordinal))
                groupEnd++;

            if (ShowGroupHeaders)
            {
                double headerHeight = Math.Max(24, GroupHeaderHeight);
                AddRow(groupStart, 0, y, headerHeight, new VirtualizingGroupHeaderInfo(group, groupEnd - groupStart));
                y += headerHeight;
            }

            for (int first = groupStart; first < groupEnd; first += _columns)
            {
                int end = Math.Min(groupEnd, first + _columns);
                double rowHeight = 1;
                for (int index = first; index < end; index++)
                    rowHeight = Math.Max(rowHeight, ResolveItemHeight(owner, index) + spacingY);
                int row = _rowTops.Count;
                AddRow(first, end - first, y, rowHeight, null);
                for (int index = first; index < end; index++)
                    _itemRows[index] = row;
                y += rowHeight;
            }

            groupStart = groupEnd;
        }

        _extent = new Size(Math.Max(availableWidth, _columns * _cellWidth), y);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    private static bool LayoutContextsMatch(
        VirtualizingLayoutContext left,
        VirtualizingLayoutContext right)
        => AreClose(left.AvailableWidth, right.AvailableWidth)
            && AreClose(left.ItemWidth, right.ItemWidth)
            && ((!double.IsFinite(left.ItemHeight) && !double.IsFinite(right.ItemHeight))
                || AreClose(left.ItemHeight, right.ItemHeight))
            && AreClose(left.HorizontalSpacing, right.HorizontalSpacing)
            && AreClose(left.VerticalSpacing, right.VerticalSpacing)
            && left.ForceSingleColumn == right.ForceSingleColumn
            && left.ShowGroupHeaders == right.ShowGroupHeaders
            && AreClose(left.GroupHeaderHeight, right.GroupHeaderHeight);

    private void AddRow(int firstIndex, int itemCount, double top, double height, VirtualizingGroupHeaderInfo? header)
    {
        _rowFirstIndices.Add(firstIndex);
        _rowItemCounts.Add(itemCount);
        _rowTops.Add(top);
        _rowHeights.Add(height);
        _rowHeaders.Add(header);
    }

    private string ResolveGroup(ItemsControl? owner, int index)
        => ResolveTile(owner, index)?.Group ?? string.Empty;

    private int FirstItemIndexForRows(int firstRow, int lastRow, int itemCount)
    {
        for (int row = Math.Max(0, firstRow); row <= Math.Min(lastRow, _rowFirstIndices.Count - 1); row++)
        {
            if (_rowItemCounts[row] > 0)
                return _rowFirstIndices[row];
        }

        return _rowFirstIndices.Count == 0
            ? -1
            : Math.Clamp(_rowFirstIndices[Math.Clamp(firstRow, 0, _rowFirstIndices.Count - 1)], 0, itemCount - 1);
    }

    private int LastItemIndexForRows(int firstRow, int lastRow, int itemCount)
    {
        for (int row = Math.Min(lastRow, _rowFirstIndices.Count - 1); row >= Math.Max(0, firstRow); row--)
        {
            if (_rowItemCounts[row] > 0)
                return Math.Min(itemCount - 1, _rowFirstIndices[row] + _rowItemCounts[row] - 1);
        }

        return _rowFirstIndices.Count == 0
            ? -1
            : Math.Clamp(_rowFirstIndices[Math.Clamp(lastRow, 0, _rowFirstIndices.Count - 1)], 0, itemCount - 1);
    }

    private double ResolveItemWidth(ItemsControl? owner, int index)
    {
        if (double.IsFinite(ItemWidth) && ItemWidth > 0)
            return ItemWidth;
        if (ResolveTile(owner, index) is Tile tile)
            return Math.Max(1, tile.CardWidth + 4);
        return DefaultItemWidth;
    }

    private double ResolveItemHeight(ItemsControl? owner, int index)
    {
        if (double.IsFinite(ItemHeight) && ItemHeight > 0)
            return ItemHeight;
        if (ResolveTile(owner, index) is Tile tile)
            return Math.Max(1, tile.CardHeight + 4);
        return DefaultItemHeight;
    }

    private Tile? ResolveTile(ItemsControl? owner, int index)
    {
        if (index < 0)
            return null;
        if (_layoutSource is not null && index < _layoutSource.Count)
            return _layoutSource[index];
        return owner is not null && index < owner.Items.Count
            ? owner.Items[index] as Tile
            : null;
    }

    private bool RealizeItems(
        int firstIndex,
        int lastIndex,
        int containerBudget,
        out int lastProcessedIndex)
    {
        lastProcessedIndex = -1;
        if (firstIndex < 0 || lastIndex < firstIndex)
            return true;

        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
        int newlyRealizedCount = 0;
        using (generator.StartAt(startPosition, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                bool newlyRealized;
                if (generator.GenerateNext(out newlyRealized) is not UIElement child)
                    continue;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                    newlyRealizedCount++;
                    if (containerBudget == InteractiveContainersPerMeasure)
                    {
                        // Preparing a WPF card and measuring its visual tree in
                        // one Render pass can exceed an input heartbeat on
                        // slower runners. Keep the progressive placeholder for
                        // one frame and perform the first measure from the next
                        // input-separated continuation.
                        _deferredMeasureContainers.Add(child);
                        return false;
                    }
                }

                int row = itemIndex < _itemRows.Length ? _itemRows[itemIndex] : -1;
                double height = row >= 0 && row < _rowHeights.Count ? _rowHeights[row] : DefaultItemHeight;
                bool measureWasDeferred = _deferredMeasureContainers.Contains(child);
                bool measureWasInvalid = !child.IsMeasureValid;
                child.Measure(new Size(_cellWidth, height));
                _deferredMeasureContainers.Remove(child);
                lastProcessedIndex = itemIndex;
                if (containerBudget == InteractiveContainersPerMeasure
                    && measureWasDeferred
                    && measureWasInvalid
                    && itemIndex < lastIndex)
                {
                    return false;
                }
                if (newlyRealizedCount >= Math.Max(1, containerBudget) && itemIndex < lastIndex)
                    return false;
            }
        }
        return true;
    }

    private bool RealizePriorityItem(int index, out bool consumedMeasureSlice)
    {
        consumedMeasureSlice = false;
        if (index < 0)
            return false;

        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition position = generator.GeneratorPositionFromIndex(index);
        int childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        using (generator.StartAt(position, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            if (generator.GenerateNext(out bool newlyRealized) is not UIElement child)
                return false;
            if (newlyRealized)
            {
                if (childIndex >= InternalChildren.Count)
                    AddInternalChild(child);
                else
                    InsertInternalChild(childIndex, child);
                generator.PrepareItemContainer(child);
                _deferredMeasureContainers.Add(child);
                consumedMeasureSlice = true;
                return false;
            }

            int row = index < _itemRows.Length ? _itemRows[index] : -1;
            double height = row >= 0 && row < _rowHeights.Count ? _rowHeights[row] : DefaultItemHeight;
            consumedMeasureSlice = !child.IsMeasureValid;
            child.Measure(new Size(_cellWidth, height));
            _deferredMeasureContainers.Remove(child);
            return true;
        }
    }

    private void ScheduleRealizationContinuation()
    {
        if (_realizationContinuationPending)
            return;
        long generation = _layoutGeneration;
        _realizationContinuationPending = true;
        InvalidateVisual();
        _ = ContinueRealizationAsync(generation);
    }

    private async Task ContinueRealizationAsync(long generation)
    {
        await Task.Delay(RealizationContinuationDelayMilliseconds).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != _layoutGeneration || !_realizationContinuationPending)
                return;
            _realizationContinuationPending = false;
            InvalidateVisual();
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                // Re-measure from current items/viewport state instead of
                // retaining a range that may have gone stale after input.
                InvalidateMeasure();
        }, DispatcherPriority.ContextIdle);
    }

    private void MarkLayoutDirty()
    {
        _layoutDirty = true;
        _realizationContinuationPending = false;
        _priorityRealizationIndex = -1;
    }

    private static Brush CreateProgressivePlaceholderBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x24));
        brush.Freeze();
        return brush;
    }

    private bool CleanupItems(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        int childIndex = InternalChildren.Count - 1;
        while (childIndex >= 0)
        {
            GeneratorPosition position = new(childIndex, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < 0)
            {
                // The generator and visual collection must stay index-aligned.
                // A single negative mapping cannot identify which child became
                // orphaned after an unexpected mutation, so rebuild the bounded
                // realized slice instead of guessing and shifting every later
                // container by one.
                ResyncGeneratorVisuals();
                return true;
            }
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                childIndex--;
                continue;
            }

            UIElement detachedChild = InternalChildren[childIndex];
            // This is an ordinary viewport eviction, not a source Reset.
            // Remove preserves ListBox.SelectedItems bookkeeping; recycling
            // the range here can leave WPF's logical selection detached from
            // a correctly selected realized container. Detach one card per
            // Render pass so a distant UIA/keyboard jump cannot unlink the
            // complete old viewport ahead of Input.
            generator.Remove(position, 1);
            ForgetDeferredMeasureRange(childIndex, 1);
            RemoveInternalChildRange(childIndex, 1);
            if (detachedChild is ListBoxItem detachedContainer
                && ItemsControl.GetItemsOwner(this) is VirtualizedGalleryListBox owner)
            {
                owner.CacheDetachedContainer(detachedContainer);
            }
            return false;
        }

        return true;
    }

    private void ForgetDeferredMeasureRange(int start, int count)
    {
        int end = Math.Min(InternalChildren.Count, start + count);
        for (int index = Math.Max(0, start); index < end; index++)
            _deferredMeasureContainers.Remove(InternalChildren[index]);
    }

    private int FindFirstRowWhoseBottomExceeds(double offset)
    {
        int low = 0;
        int high = _rowTops.Count - 1;
        int answer = high;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_rowTops[middle] + _rowHeights[middle] > offset)
            {
                answer = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }
        return Math.Clamp(answer, 0, _rowTops.Count - 1);
    }

    private int FindLastRowWhoseTopPrecedes(double offset)
    {
        int low = 0;
        int high = _rowTops.Count - 1;
        int answer = 0;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_rowTops[middle] < offset)
            {
                answer = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return Math.Clamp(answer, 0, _rowTops.Count - 1);
    }

    private void UpdateRange(int firstVisible, int lastVisible, int firstRealized, int lastRealized)
    {
        if (_firstVisibleIndex == firstVisible
            && _lastVisibleIndex == lastVisible
            && _firstRealizedIndex == firstRealized
            && _lastRealizedIndex == lastRealized)
        {
            return;
        }

        _firstVisibleIndex = firstVisible;
        _lastVisibleIndex = lastVisible;
        _firstRealizedIndex = firstRealized;
        _lastRealizedIndex = lastRealized;
        InvalidateVisual();
        RealizedRangeChanged?.Invoke(
            this,
            new VirtualizingWrapPanelRangeChangedEventArgs(firstVisible, lastVisible, firstRealized, lastRealized));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // Reset preparation deliberately yields between one-container detach
        // units. Rendering the stale projection at every yield rebuilt all
        // visible date-header FormattedText objects before Input could run,
        // even though that frame was immediately replaced. Keep the last
        // composed frame until the atomic collection publication finishes.
        if (_itemsResetPreparationActive)
            return;
        DrawProgressivePlaceholders(drawingContext);
        if (!ShowGroupHeaders || _rowHeaders.Count == 0)
            return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xD1, 0xFF, 0xFF, 0xFF));
        var countBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        var linePen = new Pen(new SolidColorBrush(Color.FromArgb(0x2D, 0xFF, 0xFF, 0xFF)), 1);
        labelBrush.Freeze();
        countBrush.Freeze();
        linePen.Freeze();

        for (int row = 0; row < _rowHeaders.Count; row++)
        {
            VirtualizingGroupHeaderInfo? header = _rowHeaders[row];
            if (header is null)
                continue;
            double y = _rowTops[row] - _offset.Y;
            if (y + _rowHeights[row] < 0 || y > _viewport.Height)
                continue;

            string label = string.IsNullOrWhiteSpace(header.Value.Label) ? "Unknown date" : header.Value.Label;
            var labelText = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 14.5, labelBrush, pixelsPerDip);
            string count = $"  ·  {header.Value.Count:N0} images";
            var countText = new FormattedText(count, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11.5, countBrush, pixelsPerDip);
            double baselineY = y + Math.Max(4, (_rowHeights[row] - labelText.Height) / 2);
            drawingContext.DrawText(labelText, new Point(2, baselineY));
            drawingContext.DrawText(countText, new Point(6 + labelText.Width, baselineY + 2));
            double lineStart = Math.Min(_viewport.Width - 12, 18 + labelText.Width + countText.Width);
            if (lineStart < _viewport.Width - 12)
                drawingContext.DrawLine(linePen, new Point(lineStart, y + (_rowHeights[row] / 2)), new Point(_viewport.Width - 12, y + (_rowHeights[row] / 2)));
        }
    }

    private void DrawProgressivePlaceholders(DrawingContext drawingContext)
    {
        if (!_realizationContinuationPending
            || _firstVisibleIndex < 0
            || _lastVisibleIndex < _firstVisibleIndex)
        {
            return;
        }

        double cardWidth = Math.Max(1, _cellWidth - Math.Max(0, HorizontalSpacing));
        double spacingY = Math.Max(0, VerticalSpacing);
        for (int index = _firstVisibleIndex; index <= _lastVisibleIndex; index++)
        {
            if (IsItemContainerRealized(index))
                continue;
            int row = index < _itemRows.Length ? _itemRows[index] : -1;
            if (row < 0 || row >= _rowTops.Count || _rowItemCounts[row] <= 0)
                continue;
            int column = Math.Max(0, index - _rowFirstIndices[row]);
            double x = column * _cellWidth - _offset.X;
            double y = _rowTops[row] - _offset.Y;
            double cardHeight = Math.Max(1, _rowHeights[row] - spacingY);
            drawingContext.DrawRoundedRectangle(
                ProgressivePlaceholderBrush,
                null,
                new Rect(x, y, cardWidth, cardHeight),
                3,
                3);
        }
    }

    private int CountVisibleUnrealizedItems()
    {
        if (_firstVisibleIndex < 0
            || _lastVisibleIndex < _firstVisibleIndex)
        {
            return 0;
        }

        int count = 0;
        for (int index = _firstVisibleIndex; index <= _lastVisibleIndex; index++)
        {
            if (!IsItemContainerRealized(index))
                count++;
        }
        return count;
    }

    private bool IsItemContainerRealized(int index)
        => ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.ContainerFromIndex(index) is UIElement container
            && !_deferredMeasureContainers.Contains(container);

    private void CoerceOffsets()
    {
        double maxHorizontal = Math.Max(0, _extent.Width - _viewport.Width);
        double maxVertical = Math.Max(0, _extent.Height - _viewport.Height);
        _offset.X = Math.Clamp(_offset.X, 0, maxHorizontal);
        _offset.Y = Math.Clamp(_offset.Y, 0, maxVertical);
    }

    private void UpdateScrollInfo()
    {
        CoerceOffsets();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private static bool AreClose(double left, double right)
        => Math.Abs(left - right) < 0.1;

    private static double ResolveViewportLength(double candidate, double fallback, double finalFallback)
    {
        if (double.IsFinite(candidate) && candidate > 0)
            return candidate;
        if (double.IsFinite(fallback) && fallback > 0)
            return fallback;
        return Math.Max(1, finalFallback);
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - Math.Max(24, AverageRowHeight() * 0.25));
    public void LineDown() => SetVerticalOffset(VerticalOffset + Math.Max(24, AverageRowHeight() * 0.25));
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 24);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 24);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - Math.Max(48, AverageRowHeight()));
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Math.Max(48, AverageRowHeight()));
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 48);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 48);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

    public void SetHorizontalOffset(double offset)
    {
        double normalized = CanHorizontallyScroll ? offset : 0;
        normalized = Math.Clamp(normalized, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        if (AreClose(normalized, _offset.X))
            return;
        _offset.X = normalized;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        double normalized = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (AreClose(normalized, _offset.Y))
            return;
        _offset.Y = normalized;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    private double AverageRowHeight()
        => _rowHeights.Count == 0 ? DefaultItemHeight : _extent.Height / _rowHeights.Count;

}

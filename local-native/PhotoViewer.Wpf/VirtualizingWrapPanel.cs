using System.Globalization;
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
    private const int MaxNewContainersPerMeasure = 24;
    private static readonly Brush ProgressivePlaceholderBrush = CreateProgressivePlaceholderBrush();

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

    private readonly List<double> _rowTops = [];
    private readonly List<double> _rowHeights = [];
    private readonly List<int> _rowFirstIndices = [];
    private readonly List<int> _rowItemCounts = [];
    private readonly List<GroupHeaderInfo?> _rowHeaders = [];
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

    public event EventHandler<VirtualizingWrapPanelRangeChangedEventArgs>? RealizedRangeChanged;

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

    internal void SetLayoutSource(IReadOnlyList<Tile> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(_layoutSource, source))
            return;
        _layoutSource = source;
        _layoutDirty = true;
        InvalidateMeasure();
    }

    public void InvalidateItemLayout()
    {
        _layoutDirty = true;
        InvalidateMeasure();
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
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        if (index < 0 || index >= count)
            return false;

        EnsureLayout(owner, count, ResolveViewportLength(_viewport.Width, ActualWidth, 1));
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        if (row < 0 || row >= _rowTops.Count)
            return false;
        BringRowIntoView(row);
        return true;
    }

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is not VirtualizingWrapPanel panel)
            return;
        panel._layoutDirty = true;
        panel.InvalidateMeasure();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        _layoutDirty = true;
        base.OnItemsChanged(sender, args);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int itemCount = owner?.Items.Count ?? 0;
        double viewportWidth = ResolveViewportLength(availableSize.Width, ActualWidth, 1);
        double viewportHeight = ResolveViewportLength(availableSize.Height, ActualHeight, ScrollOwner?.ActualHeight ?? 1);
        _viewport = new Size(viewportWidth, viewportHeight);

        EnsureLayout(owner, itemCount, viewportWidth);
        CoerceOffsets();

        if (itemCount == 0 || _rowTops.Count == 0)
        {
            CleanupItems(0, -1);
            UpdateRange(-1, -1, -1, -1);
            UpdateScrollInfo();
            return availableSize;
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

        CleanupItems(firstRealizedIndex, lastRealizedIndex);
        bool realizationComplete = RealizeItems(firstRealizedIndex, lastRealizedIndex, out int lastProcessedIndex);
        int reportedFirstRealizedIndex = lastProcessedIndex >= firstRealizedIndex ? firstRealizedIndex : -1;
        UpdateRange(firstVisibleIndex, lastVisibleIndex, reportedFirstRealizedIndex, lastProcessedIndex);
        UpdateScrollInfo();
        if (!realizationComplete)
            ScheduleRealizationContinuation();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
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
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;
        if (index < 0 || index >= count)
            return;

        EnsureLayout(owner, count, ResolveViewportLength(_viewport.Width, ActualWidth, 1));
        int row = index < _itemRows.Length ? _itemRows[index] : -1;
        if (row < 0 || row >= _rowTops.Count)
            return;
        BringRowIntoView(row);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
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
        if (!_layoutDirty
            && _layoutItemCount == itemCount
            && AreClose(_layoutWidth, availableWidth)
            && AreClose(_layoutItemWidthSignature, itemWidthSignature)
            && AreClose(_layoutItemHeightSignature, itemHeightSignature))
        {
            return;
        }

        _layoutDirty = false;
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
                AddRow(groupStart, 0, y, headerHeight, new GroupHeaderInfo(group, groupEnd - groupStart));
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
    }

    private void AddRow(int firstIndex, int itemCount, double top, double height, GroupHeaderInfo? header)
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

    private bool RealizeItems(int firstIndex, int lastIndex, out int lastProcessedIndex)
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
                }

                int row = itemIndex < _itemRows.Length ? _itemRows[itemIndex] : -1;
                double height = row >= 0 && row < _rowHeights.Count ? _rowHeights[row] : DefaultItemHeight;
                child.Measure(new Size(_cellWidth, height));
                lastProcessedIndex = itemIndex;
                if (newlyRealizedCount >= MaxNewContainersPerMeasure && itemIndex < lastIndex)
                    return false;
            }
        }
        return true;
    }

    private void ScheduleRealizationContinuation()
    {
        if (_realizationContinuationPending)
            return;
        _realizationContinuationPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _realizationContinuationPending = false;
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                // Re-measure from current items/viewport state instead of
                // retaining a range that may have gone stale after input.
                InvalidateMeasure();
        }, DispatcherPriority.Background);
    }

    private static Brush CreateProgressivePlaceholderBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x24));
        brush.Freeze();
        return brush;
    }

    private void CleanupItems(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            GeneratorPosition position = new(childIndex, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
                continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
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
        RealizedRangeChanged?.Invoke(
            this,
            new VirtualizingWrapPanelRangeChangedEventArgs(firstVisible, lastVisible, firstRealized, lastRealized));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
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
            GroupHeaderInfo? header = _rowHeaders[row];
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

    private readonly record struct GroupHeaderInfo(string Label, int Count);
}

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public sealed class VirtualizedGalleryListBox : ListBox
{
    private const int MaxDetachedContainerPoolSize = 32;
    private GalleryAutomationProjectionIndex? _automationProjection;
    private GalleryAutomationProjectionIndex? _pendingAutomationProjection;
    private DispatcherOperation? _automationRealizeOperation;
    private long _automationProjectionGeneration;
    private long _pendingAutomationRealizeGeneration = -1;
    private int _pendingAutomationRealizeIndex = -1;
    private int _automationRealizeDispatchCount;
    private int _automationRealizeCoalescedCount;
    private long _automationRealizeMaxDispatchMilliseconds;
    private long _automationLookupMaxMilliseconds;
    // WPF empties its own recycling queue on a collection Reset. This bounded
    // pool accepts only containers the custom panel has already removed from
    // both the generator map and visual tree.
    private readonly Queue<ListBoxItem> _detachedContainerPool = [];
    private readonly HashSet<ListBoxItem> _detachedContainerSet = [];
    private int _detachedContainerCreatedCount;
    private int _detachedContainerReuseCount;

    public VirtualizedGalleryListBox()
    {
        LayoutUpdated += (_, _) => CompleteAutomationRealizeIfPossible();
        Unloaded += (_, _) =>
        {
            _automationProjectionGeneration++;
            CancelAutomationRealization();
            ReleaseAutomationProjections();
            _detachedContainerPool.Clear();
            _detachedContainerSet.Clear();
        };
    }

    public bool ReuseDetachedContainers { get; set; }

    internal long AutomationProjectionGeneration => _automationProjectionGeneration;
    internal int AutomationRealizeDispatchCount => _automationRealizeDispatchCount;
    internal int AutomationRealizeCoalescedCount => _automationRealizeCoalescedCount;
    internal bool AutomationRealizePending => _pendingAutomationRealizeIndex >= 0;
    internal long AutomationRealizeMaxDispatchMilliseconds
        => _automationRealizeMaxDispatchMilliseconds;
    internal long AutomationLookupMaxMilliseconds => _automationLookupMaxMilliseconds;
    internal int DetachedContainerCreatedCount => _detachedContainerCreatedCount;
    internal int DetachedContainerReuseCount => _detachedContainerReuseCount;

    internal void StageAutomationProjection(GalleryAutomationProjectionIndex projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.AddReference();
        _pendingAutomationProjection?.Release();
        _pendingAutomationProjection = projection;
    }

    internal void CancelAutomationRealization()
    {
        if (_automationRealizeOperation is { Status: DispatcherOperationStatus.Pending } operation)
            operation.Abort();
        _automationRealizeOperation = null;
        _pendingAutomationRealizeGeneration = -1;
        _pendingAutomationRealizeIndex = -1;
    }

    internal bool TryResolveAutomationItem(object? item, out int index)
    {
        index = -1;
        if (item is not Tile tile)
            return false;

        if (_automationProjection is { } projection
            && projection.TryResolve(tile, Items, out index))
        {
            return true;
        }

        // A missing projection can occur briefly while an external collection
        // mutation is being reconciled. Never turn that window into a 100k
        // synchronous UI Automation scan.
        index = Items.Count <= 512 ? Items.IndexOf(item) : -1;
        return index >= 0;
    }

    internal bool IsAutomationItemCurrent(
        object? item,
        long generation,
        int index)
        => generation == _automationProjectionGeneration
            && index >= 0
            && index < Items.Count
            && ReferenceEquals(Items[index], item);

    internal int FindAutomationItemByName(string name, int startIndex)
        => _automationProjection is { } projection
            ? projection.FindByName(name, startIndex, Items)
            : FindAutomationItemByNameFallback(name, startIndex);

    internal void RequestAutomationRealize(
        object item,
        long generation,
        int index)
    {
        EnsureAutomationItemCurrent(item, generation, index);
        if (ItemContainerGenerator.ContainerFromIndex(index) is not null)
            return;

        if (_pendingAutomationRealizeGeneration == generation
            && _pendingAutomationRealizeIndex == index)
        {
            _automationRealizeCoalescedCount++;
            return;
        }

        CancelAutomationRealization();
        _pendingAutomationRealizeGeneration = generation;
        _pendingAutomationRealizeIndex = index;
        _automationRealizeDispatchCount++;
        _automationRealizeOperation = Dispatcher.BeginInvoke(() =>
        {
            _automationRealizeOperation = null;
            if (!IsAutomationItemCurrent(item, generation, index)
                || Dispatcher.HasShutdownStarted
                || Dispatcher.HasShutdownFinished)
            {
                CancelAutomationRealization();
                return;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                ScrollIntoView(Items[index]);
                CompleteAutomationRealizeIfPossible();
            }
            finally
            {
                watch.Stop();
                _automationRealizeMaxDispatchMilliseconds = Math.Max(
                    _automationRealizeMaxDispatchMilliseconds,
                    watch.ElapsedMilliseconds);
            }
        }, DispatcherPriority.Input);
    }

    internal void EnsureAutomationItemCurrent(
        object? item,
        long generation,
        int index)
    {
        if (!IsAutomationItemCurrent(item, generation, index))
            throw new ElementNotAvailableException("The gallery item is no longer in the current projection.");
    }

    internal void RecordAutomationLookup(long elapsedMilliseconds)
        => _automationLookupMaxMilliseconds = Math.Max(
            _automationLookupMaxMilliseconds,
            elapsedMilliseconds);

    internal void ResetAutomationLookupMetrics()
    {
        _automationLookupMaxMilliseconds = 0;
        _automationRealizeMaxDispatchMilliseconds = 0;
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new VirtualizedGalleryListBoxAutomationPeer(this);

    protected override DependencyObject GetContainerForItemOverride()
    {
        if (ReuseDetachedContainers)
        {
            int candidateCount = _detachedContainerPool.Count;
            while (candidateCount-- > 0)
            {
                ListBoxItem candidate = _detachedContainerPool.Dequeue();
                if (VisualTreeHelper.GetParent(candidate) is null)
                {
                    _detachedContainerSet.Remove(candidate);
                    _detachedContainerReuseCount++;
                    return candidate;
                }
                _detachedContainerPool.Enqueue(candidate);
            }
        }

        _detachedContainerCreatedCount++;
        return base.GetContainerForItemOverride();
    }

    internal void CacheDetachedContainer(ListBoxItem container)
    {
        // Callers must finish generator and visual-tree detachment first.
        // GetContainerForItemOverride also verifies that invariant before use.
        if (!ReuseDetachedContainers
            || _detachedContainerPool.Count >= MaxDetachedContainerPoolSize
            || !_detachedContainerSet.Add(container))
        {
            return;
        }

        _detachedContainerPool.Enqueue(container);
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        _automationProjectionGeneration++;
        _automationProjection?.Release();
        _automationProjection = null;
        CancelAutomationRealization();
        TryAdoptPendingAutomationProjection();
        base.OnItemsChanged(e);
    }

    private void TryAdoptPendingAutomationProjection()
    {
        if (_pendingAutomationProjection is not { } pending)
            return;
        if (pending.Count != Items.Count)
        {
            pending.Release();
            _pendingAutomationProjection = null;
            return;
        }
        if (!pending.Matches(Items))
        {
            pending.Release();
            _pendingAutomationProjection = null;
            return;
        }

        _automationProjection = pending;
        _pendingAutomationProjection = null;
    }

    private void ReleaseAutomationProjections()
    {
        _automationProjection?.Release();
        _automationProjection = null;
        _pendingAutomationProjection?.Release();
        _pendingAutomationProjection = null;
    }

    private int FindAutomationItemByNameFallback(string name, int startIndex)
    {
        if (Items.Count > 512)
            return -1;

        for (int index = Math.Max(0, startIndex); index < Items.Count; index++)
        {
            if (Items[index] is Tile tile
                && string.Equals(tile.FileName, name, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private void CompleteAutomationRealizeIfPossible()
    {
        int index = _pendingAutomationRealizeIndex;
        if (index < 0)
            return;
        if (_pendingAutomationRealizeGeneration != _automationProjectionGeneration)
        {
            CancelAutomationRealization();
            return;
        }
        if (ItemContainerGenerator.ContainerFromIndex(index) is not null)
            CancelAutomationRealization();
    }
}

public sealed class VirtualizedGalleryListBoxAutomationPeer(VirtualizedGalleryListBox owner)
    : ListBoxAutomationPeer(owner)
{
    private readonly IItemContainerProvider _itemContainerProvider =
        new GalleryItemContainerProvider(owner);

    internal VirtualizedGalleryListBox GalleryOwner => owner;

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.ItemContainer
            ? _itemContainerProvider
            : base.GetPattern(patternInterface);

    protected override ItemAutomationPeer CreateItemAutomationPeer(object item)
        => new VirtualizedGalleryListBoxItemAutomationPeer(item, this);

    protected override ItemAutomationPeer FindOrCreateItemAutomationPeer(object item)
    {
        ItemAutomationPeer peer = base.FindOrCreateItemAutomationPeer(item);
        if (peer is VirtualizedGalleryListBoxItemAutomationPeer galleryPeer
            && owner.TryResolveAutomationItem(item, out int index))
        {
            galleryPeer.AdoptProjection(owner.AutomationProjectionGeneration, index);
        }
        return peer;
    }

    internal ItemAutomationPeer GetOrCreateItemPeer(object item)
        => FindOrCreateItemAutomationPeer(item);

    private IRawElementProviderSimple? FindItemByProperty(
        IRawElementProviderSimple? startAfter,
        int propertyId,
        object? value)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            int startIndex = ResolveStartIndex(startAfter);
            int index;
            if (propertyId == 0)
            {
                index = startIndex;
            }
            else if (propertyId == AutomationElementIdentifiers.NameProperty.Id)
            {
                index = value is string name
                    ? owner.FindAutomationItemByName(name, startIndex)
                    : -1;
            }
            else if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id)
            {
                bool listItem = value is int controlTypeId
                    && controlTypeId == ControlType.ListItem.Id;
                index = listItem ? startIndex : -1;
            }
            else if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id)
            {
                // Gallery item containers do not publish a stable AutomationId.
                // Returning no match preserves the ItemContainer contract
                // without materializing a peer for every item in a 100k view.
                index = -1;
            }
            else
            {
                throw new ArgumentException(
                    "Only Name, AutomationId, and ControlType are supported.",
                    nameof(propertyId));
            }

            if (index < 0 || index >= owner.Items.Count)
                return null;
            ItemAutomationPeer peer = FindOrCreateItemAutomationPeer(owner.Items[index]);
            return ProviderFromPeer(peer);
        }
        finally
        {
            watch.Stop();
            owner.RecordAutomationLookup(watch.ElapsedMilliseconds);
        }
    }

    private int ResolveStartIndex(IRawElementProviderSimple? startAfter)
    {
        if (startAfter is null)
            return 0;
        if (PeerFromProvider(startAfter)
            is not VirtualizedGalleryListBoxItemAutomationPeer startPeer
            || !ReferenceEquals(startPeer.GalleryPeer, this))
        {
            return -1;
        }

        startPeer.EnsureCurrent();
        return startPeer.ProjectedIndex + 1;
    }

    private sealed class GalleryItemContainerProvider(VirtualizedGalleryListBox owner)
        : IItemContainerProvider
    {
        public IRawElementProviderSimple? FindItemByProperty(
            IRawElementProviderSimple? startAfter,
            int propertyId,
            object? value)
        {
            if (UIElementAutomationPeer.CreatePeerForElement(owner)
                is not VirtualizedGalleryListBoxAutomationPeer peer)
            {
                return null;
            }
            return peer.FindItemByProperty(startAfter, propertyId, value);
        }
    }
}

public sealed class VirtualizedGalleryListBoxItemAutomationPeer(
    object item,
    SelectorAutomationPeer selectorAutomationPeer)
    : ListBoxItemAutomationPeer(item, selectorAutomationPeer)
{
    private long _projectionGeneration = -1;
    private int _projectedIndex = -1;
    private ISelectionItemProvider? _guardedSelectionProvider;
    private IVirtualizedItemProvider? _guardedVirtualizedItemProvider;

    internal VirtualizedGalleryListBoxAutomationPeer GalleryPeer
        => (VirtualizedGalleryListBoxAutomationPeer)ItemsControlAutomationPeer;
    internal int ProjectedIndex => _projectedIndex;

    internal void AdoptProjection(long generation, int index)
    {
        _projectionGeneration = generation;
        _projectedIndex = index;
    }

    internal void EnsureCurrent()
        => GalleryPeer.GalleryOwner.EnsureAutomationItemCurrent(
            Item,
            _projectionGeneration,
            _projectedIndex);

    private bool IsCurrent
        => GalleryPeer.GalleryOwner.IsAutomationItemCurrent(
            Item,
            _projectionGeneration,
            _projectedIndex);

    public override object? GetPattern(PatternInterface patternInterface)
    {
        object? basePattern = base.GetPattern(patternInterface);
        if (patternInterface == PatternInterface.SelectionItem
            && basePattern is ISelectionItemProvider selectionProvider)
        {
            return _guardedSelectionProvider ??=
                new GuardedSelectionItemProvider(this, selectionProvider);
        }
        if (patternInterface == PatternInterface.VirtualizedItem
            && basePattern is IVirtualizedItemProvider)
        {
            return _guardedVirtualizedItemProvider ??=
                new GuardedVirtualizedItemProvider(this);
        }
        return basePattern;
    }

    protected override string GetNameCore()
    {
        if (!IsCurrent)
            return "";
        return Item is Tile tile && !string.IsNullOrWhiteSpace(tile.FileName)
            ? tile.FileName
            : base.GetNameCore();
    }

    protected override string GetHelpTextCore()
    {
        if (!IsCurrent)
            return "";
        return Item is Tile tile
            ? tile.AutomationHelpText
            : base.GetHelpTextCore();
    }

    private sealed class GuardedVirtualizedItemProvider(
        VirtualizedGalleryListBoxItemAutomationPeer peer)
        : IVirtualizedItemProvider
    {
        public void Realize()
        {
            peer.EnsureCurrent();
            peer.GalleryPeer.GalleryOwner.RequestAutomationRealize(
                peer.Item,
                peer._projectionGeneration,
                peer._projectedIndex);
        }
    }

    private sealed class GuardedSelectionItemProvider(
        VirtualizedGalleryListBoxItemAutomationPeer peer,
        ISelectionItemProvider inner)
        : ISelectionItemProvider
    {
        public bool IsSelected
        {
            get
            {
                peer.EnsureCurrent();
                return peer.Item is Tile tile
                    ? tile.IsCanonicalSelected
                    : inner.IsSelected;
            }
        }

        public IRawElementProviderSimple SelectionContainer
        {
            get
            {
                peer.EnsureCurrent();
                return inner.SelectionContainer;
            }
        }

        public void AddToSelection()
        {
            peer.EnsureCurrent();
            inner.AddToSelection();
        }

        public void RemoveFromSelection()
        {
            peer.EnsureCurrent();
            inner.RemoveFromSelection();
        }

        public void Select()
        {
            peer.EnsureCurrent();
            inner.Select();
        }
    }
}

internal sealed class GalleryAutomationProjectionIndex
{
    private const int MaxPooledDictionaries = 2;
    private static readonly ConcurrentBag<Dictionary<string, int>> DictionaryPool = [];
    private static int _pooledDictionaryCount;
    private static long _createdCount;
    private static long _activeCreatorCount;

    private Dictionary<string, int>? _firstIndexByName;
    private readonly WeakReference<Tile>? _first;
    private readonly WeakReference<Tile>? _last;
    private int _referenceCount = 1;
    private int _creatorReleased;

    private GalleryAutomationProjectionIndex(
        int count,
        Dictionary<string, int> firstIndexByName,
        Tile? first,
        Tile? last)
    {
        Interlocked.Increment(ref _createdCount);
        Interlocked.Increment(ref _activeCreatorCount);
        Count = count;
        _firstIndexByName = firstIndexByName;
        _first = first is null ? null : new WeakReference<Tile>(first);
        _last = last is null ? null : new WeakReference<Tile>(last);
    }

    internal int Count { get; }
    internal static long CreatedCount => Volatile.Read(ref _createdCount);
    internal static long ActiveCreatorCount => Volatile.Read(ref _activeCreatorCount);

    internal static GalleryAutomationProjectionIndex Create(
        IReadOnlyList<Tile> items,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (count < 0 || count > items.Count)
            throw new ArgumentOutOfRangeException(nameof(count));

        Dictionary<string, int> names = RentDictionary(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                if ((index & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index > 0
                        && count > 16_384
                        && cancellationToken.CanBeCanceled
                        && (index & 1023) == 0)
                    {
                        Thread.Yield();
                    }
                }
                Tile tile = items[index];
                names.TryAdd(tile.FileName, index);
            }

            return new GalleryAutomationProjectionIndex(
                count,
                names,
                count == 0 ? null : items[0],
                count == 0 ? null : items[count - 1]);
        }
        catch
        {
            ReturnDictionary(names);
            throw;
        }
    }

    internal void AddReference()
    {
        while (true)
        {
            int count = Volatile.Read(ref _referenceCount);
            if (count <= 0)
                throw new ObjectDisposedException(nameof(GalleryAutomationProjectionIndex));
            if (Interlocked.CompareExchange(ref _referenceCount, count + 1, count) == count)
                return;
        }
    }

    internal void Release()
    {
        int count = Interlocked.Decrement(ref _referenceCount);
        if (count > 0)
            return;
        if (count < 0)
            throw new InvalidOperationException("Automation projection reference count became negative.");

        Dictionary<string, int>? names = Interlocked.Exchange(ref _firstIndexByName, null);
        if (names is not null)
            ReturnDictionary(names);
    }

    internal void ReleaseCreator()
    {
        if (Interlocked.Exchange(ref _creatorReleased, 1) == 0)
        {
            Interlocked.Decrement(ref _activeCreatorCount);
            Release();
        }
    }

    internal bool Matches(ItemCollection items)
    {
        if (items.Count != Count)
            return false;
        if (Count == 0)
            return true;
        return _first?.TryGetTarget(out Tile? first) == true
            && _last?.TryGetTarget(out Tile? last) == true
            && ReferenceEquals(items[0], first)
            && ReferenceEquals(items[Count - 1], last);
    }

    internal bool TryResolve(Tile tile, ItemCollection items, out int index)
    {
        index = FindByName(tile.FileName, 0, items);
        while (index >= 0 && index < Count)
        {
            if (ReferenceEquals(items[index], tile))
                return true;
            index = FindByName(tile.FileName, index + 1, items);
        }
        index = -1;
        return false;
    }

    internal int FindByName(string name, int startIndex, ItemCollection items)
    {
        Dictionary<string, int> names = _firstIndexByName
            ?? throw new ObjectDisposedException(nameof(GalleryAutomationProjectionIndex));
        if (!names.TryGetValue(name, out int firstIndex))
            return -1;
        int index = Math.Max(startIndex, firstIndex);
        for (; index < Count; index++)
        {
            if (items[index] is Tile tile
                && string.Equals(tile.FileName, name, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static Dictionary<string, int> RentDictionary(int count)
    {
        bool canReuseOffDispatcher = Application.Current is null
            || !Application.Current.Dispatcher.CheckAccess();
        if (canReuseOffDispatcher && DictionaryPool.TryTake(out Dictionary<string, int>? names))
        {
            Interlocked.Decrement(ref _pooledDictionaryCount);
            names.Clear();
            names.EnsureCapacity(Math.Min(count, 131_072));
            return names;
        }

        return new Dictionary<string, int>(
            Math.Min(count, 131_072),
            StringComparer.Ordinal);
    }

    private static void ReturnDictionary(Dictionary<string, int> names)
    {
        int pooled = Interlocked.Increment(ref _pooledDictionaryCount);
        if (pooled <= MaxPooledDictionaries)
        {
            DictionaryPool.Add(names);
            return;
        }

        Interlocked.Decrement(ref _pooledDictionaryCount);
    }
}

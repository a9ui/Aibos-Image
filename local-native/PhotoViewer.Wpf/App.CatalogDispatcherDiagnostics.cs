using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private enum CatalogDispatcherEventKind : byte
    {
        OperationPosted,
        OperationStarted,
        OperationCompleted,
        OperationAborted,
        OperationPriorityChanged,
        DispatcherInactive,
        HeartbeatStarted,
        HeartbeatCompleted,
    }

    private readonly record struct CatalogHeartbeatMarker(
        long Tag,
        long UiThreadCpuTicks);

    private readonly record struct CatalogDispatcherRawEvent(
        long Sequence,
        long Timestamp,
        long UiThreadCpuTicks,
        uint CurrentThreadId,
        CatalogDispatcherEventKind Kind,
        int OperationIdentity,
        string CallbackName,
        DispatcherPriority Priority,
        string AppOperation,
        long HeartbeatTag);

    private sealed record CatalogDispatcherOperationIdentity(
        int Value,
        string CallbackName);

    private sealed class CatalogDispatcherOperationBuilder(int identity)
    {
        public int Identity { get; } = identity;
        public string CallbackName { get; set; } = "";
        public DispatcherPriority Priority { get; set; } = DispatcherPriority.Invalid;
        public string AppOperation { get; set; } = "";
        public long PostedTimestamp { get; set; }
        public long PostedCpuTicks { get; set; } = -1;
        public uint PostedThreadId { get; set; }
        public long StartedTimestamp { get; set; }
        public long StartedCpuTicks { get; set; } = -1;
        public long TerminalTimestamp { get; set; }
        public long TerminalCpuTicks { get; set; } = -1;
        public string TerminalKind { get; set; } = "";
    }

    private sealed record CatalogDispatcherOperationDiagnostic(
        int Identity,
        string AppOperation,
        string CallbackName,
        string Priority,
        long PostedTimestamp,
        long StartedTimestamp,
        long TerminalTimestamp,
        string TerminalKind,
        double QueueWallMs,
        double QueueUiCpuMs,
        double ExecutionWallMs,
        double ExecutionUiCpuMs);

    private sealed record CatalogPanelPhaseOverlapDiagnostic(
        string Phase,
        string AppOperation,
        long LayoutGeneration,
        double WallMs,
        double UiCpuMs,
        int FirstVisibleIndex,
        int LastVisibleIndex,
        int FirstRealizedIndex,
        int LastRealizedIndex,
        int ContainerCount);

    private sealed record CatalogDispatcherHeartbeatDiagnostic(
        string Operation,
        long ProjectionGeneration,
        double RawGapMs,
        double UiThreadCpuMs,
        long HeartbeatTag,
        int HeartbeatOperationIdentity,
        double HeartbeatQueueWallMs,
        double HeartbeatQueueUiCpuMs,
        double HeartbeatStartMarkerDelayMs,
        double ExcessQueueUiCpuMs,
        double StrictSchedulerQueueDelayMs,
        double ProductGapMs,
        double ActiveOperationOverlapMs,
        double PanelPhaseOverlapMs,
        string PreviousDispatcherEvent,
        double PreviousDispatcherEventAgeMs,
        string Classification,
        IReadOnlyList<CatalogDispatcherOperationDiagnostic> ActiveOperations,
        IReadOnlyList<CatalogPanelPhaseOverlapDiagnostic> PanelPhases);

    private sealed class CatalogDispatcherDiagnosticSummary
    {
        public bool SensorValid { get; init; }
        public bool HooksStarted { get; init; }
        public bool HooksStopped { get; init; }
        public int RawEventCount { get; init; }
        public bool RingOverflow { get; init; }
        public int UiThreadCpuReadFailureCount { get; init; }
        public int ActiveStackOverflowCount { get; init; }
        public int ActiveStackMismatchCount { get; init; }
        public int LifecycleTimestampInversionCount { get; init; }
        public int ConcurrentPostedStartReorderCount { get; init; }
        public int StartedWithoutTerminalCount { get; init; }
        public int BoundaryTruncatedOperationCount { get; init; }
        public int HeartbeatLifecycleMissingCount { get; init; }
        public bool PanelPhaseOverflow { get; init; }
        public int PanelPhaseInvalidCount { get; init; }
        public int RawOverBudgetCount { get; init; }
        public int SchedulerQueueDelayCount { get; init; }
        public int ActiveOperationDiagnosticCount { get; init; }
        public int InconclusiveCount { get; init; }
        public double MaxProductGapMs { get; init; }
        public double MaxStrictSchedulerQueueDelayMs { get; init; }
        public IReadOnlyList<CatalogDispatcherHeartbeatDiagnostic> OverBudgetHeartbeats { get; init; } = [];
    }

    private sealed class CatalogDispatcherDiagnosticRecorder : IDisposable
    {
        // Harness-only headroom for the sealed UIA/keyboard measurement.
        private const int EventCapacity = 524_288;
        private const int ActiveStackCapacity = 32;
        private const uint ThreadQueryLimitedInformation = 0x0800;
        private static readonly FieldInfo? DispatcherOperationMethodField =
            typeof(DispatcherOperation).GetField(
                "_method",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Dispatcher _dispatcher;
        private readonly Func<string> _appOperationProvider;
        private readonly CatalogDispatcherRawEvent[] _events =
            new CatalogDispatcherRawEvent[EventCapacity];
        private readonly ConditionalWeakTable<
            DispatcherOperation,
            CatalogDispatcherOperationIdentity> _operationIdentities = new();
        private readonly DispatcherOperation?[] _activeStack =
            new DispatcherOperation?[ActiveStackCapacity];
        private readonly uint _uiThreadId;
        private readonly IntPtr _uiThreadHandle;
        private int _nextEventIndex = -1;
        private int _ringOverflow;
        private int _uiThreadCpuReadFailureCount;
        private int _activeDepth;
        private int _activeStackOverflowCount;
        private int _activeStackMismatchCount;
        private long _nextHeartbeatTag;
        private int _nextOperationIdentity;
        private bool _started;
        private bool _stopped;

        public CatalogDispatcherDiagnosticRecorder(
            Dispatcher dispatcher,
            Func<string> appOperationProvider)
        {
            _dispatcher = dispatcher;
            _appOperationProvider = appOperationProvider;
            _uiThreadId = GetCurrentThreadId();
            _uiThreadHandle = OpenThread(
                ThreadQueryLimitedInformation,
                inheritHandle: false,
                _uiThreadId);
        }

        public bool Start()
        {
            if (_started
                || !_dispatcher.CheckAccess()
                || _uiThreadHandle == IntPtr.Zero)
            {
                return false;
            }

            DispatcherHooks hooks = _dispatcher.Hooks;
            hooks.OperationPosted += Hooks_OperationPosted;
            hooks.OperationStarted += Hooks_OperationStarted;
            hooks.OperationCompleted += Hooks_OperationCompleted;
            hooks.OperationAborted += Hooks_OperationAborted;
            _started = true;
            return true;
        }

        public void PrepareCapacityForMeasurement()
        {
            if (!_dispatcher.CheckAccess() || _started)
                throw new InvalidOperationException(
                    "Dispatcher diagnostic capacity must be prepared before hooks start.");

            // Commit the fixed harness buffer before the product memory
            // baseline so recording does not masquerade as product growth.
            for (int index = 0; index < _events.Length; index += 32)
            {
                _events[index] = new CatalogDispatcherRawEvent(
                    -1,
                    -1,
                    -1,
                    0,
                    CatalogDispatcherEventKind.DispatcherInactive,
                    0,
                    "",
                    DispatcherPriority.Invalid,
                    "",
                    0);
            }
            Array.Clear(_events);
        }

        public void StopRecording() => StopHooks();

        public long ReadUiThreadCpuTicks()
        {
            if (_uiThreadHandle == IntPtr.Zero
                || !GetThreadTimes(
                    _uiThreadHandle,
                    out _,
                    out _,
                    out long kernelTime,
                    out long userTime))
            {
                Interlocked.Increment(ref _uiThreadCpuReadFailureCount);
                return -1;
            }
            return kernelTime + userTime;
        }

        public CatalogHeartbeatMarker MarkHeartbeatStarted()
        {
            long tag = Interlocked.Increment(ref _nextHeartbeatTag);
            DispatcherOperation? operation =
                _activeDepth > 0 ? _activeStack[_activeDepth - 1] : null;
            long cpuTicks = Record(
                CatalogDispatcherEventKind.HeartbeatStarted,
                operation,
                tag);
            return new CatalogHeartbeatMarker(tag, cpuTicks);
        }

        public void MarkHeartbeatCompleted(long tag)
        {
            DispatcherOperation? operation =
                _activeDepth > 0 ? _activeStack[_activeDepth - 1] : null;
            Record(
                CatalogDispatcherEventKind.HeartbeatCompleted,
                operation,
                tag);
        }

        public CatalogDispatcherDiagnosticSummary StopAndAnalyze(
            IReadOnlyList<DispatcherHeartbeatDiagnosticSample> heartbeatSamples,
            IReadOnlyList<VirtualizingPanelPhaseDiagnostic> panelPhases,
            bool panelPhaseOverflow,
            long measurementStartTimestamp,
            long measurementEndTimestamp,
            long heartbeatBudgetMs)
        {
            DispatcherOperation?[] boundaryOperations = StopHooks();
            int eventCount = Math.Min(
                Math.Max(0, Volatile.Read(ref _nextEventIndex) + 1),
                _events.Length);
            var rawEvents = new List<CatalogDispatcherRawEvent>(eventCount);
            for (int index = 0; index < eventCount; index++)
            {
                CatalogDispatcherRawEvent item = _events[index];
                if (item.Timestamp >= measurementStartTimestamp
                    && item.Timestamp <= measurementEndTimestamp)
                {
                    rawEvents.Add(item);
                }
            }
            rawEvents.Sort(static (left, right) =>
            {
                int timestamp = left.Timestamp.CompareTo(right.Timestamp);
                return timestamp != 0
                    ? timestamp
                    : left.Sequence.CompareTo(right.Sequence);
            });

            var operationBuilders =
                new Dictionary<int, CatalogDispatcherOperationBuilder>();
            var heartbeatOperations = new Dictionary<long, int>();
            foreach (CatalogDispatcherRawEvent item in rawEvents)
            {
                if (item.OperationIdentity <= 0)
                    continue;
                if (!operationBuilders.TryGetValue(
                    item.OperationIdentity,
                    out CatalogDispatcherOperationBuilder? builder))
                {
                    builder =
                        new CatalogDispatcherOperationBuilder(item.OperationIdentity)
                        {
                            CallbackName = item.CallbackName,
                        };
                    operationBuilders.Add(item.OperationIdentity, builder);
                }
                if (item.Priority != DispatcherPriority.Invalid)
                    builder.Priority = item.Priority;
                if (item.Kind == CatalogDispatcherEventKind.OperationStarted
                    && !string.IsNullOrEmpty(item.AppOperation))
                    builder.AppOperation = item.AppOperation;
                else if (string.IsNullOrEmpty(builder.AppOperation)
                    && !string.IsNullOrEmpty(item.AppOperation))
                    builder.AppOperation = item.AppOperation;
                switch (item.Kind)
                {
                    case CatalogDispatcherEventKind.OperationPosted:
                        if (builder.PostedTimestamp == 0)
                        {
                            builder.PostedTimestamp = item.Timestamp;
                            builder.PostedCpuTicks = item.UiThreadCpuTicks;
                            builder.PostedThreadId = item.CurrentThreadId;
                        }
                        break;
                    case CatalogDispatcherEventKind.OperationStarted:
                        if (builder.StartedTimestamp == 0)
                        {
                            builder.StartedTimestamp = item.Timestamp;
                            builder.StartedCpuTicks = item.UiThreadCpuTicks;
                        }
                        break;
                    case CatalogDispatcherEventKind.OperationCompleted:
                    case CatalogDispatcherEventKind.OperationAborted:
                        builder.TerminalTimestamp = item.Timestamp;
                        builder.TerminalCpuTicks = item.UiThreadCpuTicks;
                        builder.TerminalKind =
                            item.Kind == CatalogDispatcherEventKind.OperationCompleted
                                ? "Completed"
                                : "Aborted";
                        break;
                    case CatalogDispatcherEventKind.HeartbeatStarted:
                        if (item.HeartbeatTag > 0)
                            heartbeatOperations[item.HeartbeatTag] =
                                item.OperationIdentity;
                        break;
                }
            }

            var boundarySet = new HashSet<int>();
            foreach (DispatcherOperation? operation in boundaryOperations)
            {
                if (operation is not null)
                    boundarySet.Add(IdentityFor(operation).Value);
            }
            int lifecycleTimestampInversionCount = 0;
            int concurrentPostedStartReorderCount = 0;
            int startedWithoutTerminalCount = 0;
            int boundaryTruncatedOperationCount = 0;
            foreach ((int operationIdentity, CatalogDispatcherOperationBuilder builder)
                in operationBuilders)
            {
                if (builder.PostedTimestamp > 0
                    && builder.StartedTimestamp > 0
                    && builder.PostedTimestamp > builder.StartedTimestamp)
                {
                    // WPF raises OperationPosted outside its dispatcher lock.
                    // A worker-thread poster can therefore be observed after
                    // the UI thread has already started the queued operation.
                    if (builder.PostedThreadId == _uiThreadId)
                        lifecycleTimestampInversionCount++;
                    else
                        concurrentPostedStartReorderCount++;
                }
                if (builder.StartedTimestamp > 0
                        && builder.TerminalTimestamp > 0
                        && builder.StartedTimestamp > builder.TerminalTimestamp)
                {
                    lifecycleTimestampInversionCount++;
                }
                if (builder.StartedTimestamp > 0 && builder.TerminalTimestamp == 0)
                {
                    if (boundarySet.Contains(operationIdentity))
                        boundaryTruncatedOperationCount++;
                    else
                        startedWithoutTerminalCount++;
                }
            }

            int panelPhaseInvalidCount = panelPhases.Count(static phase =>
                phase.StartTimestamp <= 0
                || phase.EndTimestamp <= phase.StartTimestamp
                || phase.StartCpuTicks < 0
                || phase.EndCpuTicks < phase.StartCpuTicks);
            int heartbeatLifecycleMissingCount = 0;
            int rawOverBudgetCount = 0;
            int schedulerQueueDelayCount = 0;
            int activeOperationDiagnosticCount = 0;
            int inconclusiveCount = 0;
            double maxProductGapMs = 0;
            double maxStrictSchedulerQueueDelayMs = 0;
            var overBudget = new List<CatalogDispatcherHeartbeatDiagnostic>();

            foreach (DispatcherHeartbeatDiagnosticSample heartbeat in heartbeatSamples)
            {
                double rawGapMs = TicksToMilliseconds(
                    heartbeat.EndTimestamp - heartbeat.StartTimestamp);
                if (rawGapMs <= heartbeatBudgetMs)
                {
                    maxProductGapMs = Math.Max(maxProductGapMs, rawGapMs);
                    continue;
                }
                rawOverBudgetCount++;
                CatalogDispatcherOperationBuilder? heartbeatBuilder = null;
                int heartbeatOperationIdentity = 0;
                if (heartbeat.HeartbeatTag <= 0
                    || !heartbeatOperations.TryGetValue(
                        heartbeat.HeartbeatTag,
                        out heartbeatOperationIdentity)
                    || !operationBuilders.TryGetValue(
                        heartbeatOperationIdentity,
                        out heartbeatBuilder)
                    || heartbeatBuilder.PostedTimestamp <= 0
                    || heartbeatBuilder.StartedTimestamp <= 0
                    || heartbeatBuilder.TerminalTimestamp <= 0)
                {
                    heartbeatLifecycleMissingCount++;
                }

                long queueSegmentStartTimestamp = heartbeatBuilder is null
                    ? 0
                    : Math.Max(
                        heartbeat.StartTimestamp,
                        heartbeatBuilder.PostedTimestamp);
                long queueSegmentEndTimestamp = heartbeatBuilder is null
                    ? 0
                    : Math.Min(
                        heartbeat.EndTimestamp,
                        heartbeatBuilder.StartedTimestamp);
                var activeOperations = new List<CatalogDispatcherOperationDiagnostic>();
                double activeOperationOverlapMs = 0;
                foreach ((int operationIdentity, CatalogDispatcherOperationBuilder builder)
                    in operationBuilders)
                {
                    if (operationIdentity == heartbeatOperationIdentity
                        || builder.StartedTimestamp <= 0
                        || builder.TerminalTimestamp <= builder.StartedTimestamp)
                    {
                        continue;
                    }
                    long overlapTicks = OverlapTicks(
                        heartbeat.StartTimestamp,
                        heartbeat.EndTimestamp,
                        builder.StartedTimestamp,
                        builder.TerminalTimestamp);
                    if (overlapTicks <= 0)
                        continue;
                    activeOperationOverlapMs += TicksToMilliseconds(overlapTicks);
                    activeOperations.Add(ToDiagnostic(builder));
                }

                var overlappingPanelPhases =
                    new List<CatalogPanelPhaseOverlapDiagnostic>();
                double panelPhaseOverlapMs = 0;
                foreach (VirtualizingPanelPhaseDiagnostic phase in panelPhases)
                {
                    long overlapTicks = OverlapTicks(
                        heartbeat.StartTimestamp,
                        heartbeat.EndTimestamp,
                        phase.StartTimestamp,
                        phase.EndTimestamp);
                    if (overlapTicks <= 0)
                        continue;
                    panelPhaseOverlapMs += TicksToMilliseconds(overlapTicks);
                    overlappingPanelPhases.Add(new(
                        phase.Phase,
                        phase.Operation,
                        phase.LayoutGeneration,
                        TicksToMilliseconds(
                            phase.EndTimestamp - phase.StartTimestamp),
                        CpuTicksToMilliseconds(
                            phase.StartCpuTicks,
                            phase.EndCpuTicks),
                        phase.FirstVisibleIndex,
                        phase.LastVisibleIndex,
                        phase.FirstRealizedIndex,
                        phase.LastRealizedIndex,
                        phase.ContainerCount));
                }

                double queueWallMs = heartbeatBuilder is null
                    ? -1
                    : TicksToMilliseconds(
                        heartbeatBuilder.StartedTimestamp
                            - heartbeatBuilder.PostedTimestamp);
                double queueCpuMs = heartbeatBuilder is null
                    ? -1
                    : CpuTicksToMilliseconds(
                        heartbeatBuilder.PostedCpuTicks,
                        heartbeatBuilder.StartedCpuTicks);
                double heartbeatStartMarkerDelayMs = heartbeatBuilder is null
                    ? -1
                    : TicksToMilliseconds(
                        heartbeat.EndTimestamp
                            - heartbeatBuilder.StartedTimestamp);
                bool queueLifecycleExact = heartbeatBuilder is not null
                    && queueSegmentEndTimestamp > queueSegmentStartTimestamp
                    && heartbeatStartMarkerDelayMs >= 0
                    && heartbeatStartMarkerDelayMs <= 1;
                long strictQueueDelayTicks = 0;
                CatalogDispatcherRawEvent? previousEvent = null;
                long strictQueueBoundaryTimestamp = 0;
                double excessQueueCpuMs = -1;
                if (queueLifecycleExact
                    && heartbeatOperationIdentity > 0
                    && heartbeatBuilder is not null)
                {
                    strictQueueDelayTicks = StrictSchedulerQueueDelayTicks(
                        queueSegmentStartTimestamp,
                        queueSegmentEndTimestamp,
                        heartbeatOperationIdentity,
                        heartbeatBuilder,
                        operationBuilders,
                        panelPhases,
                        rawEvents,
                        out previousEvent,
                        out strictQueueBoundaryTimestamp,
                        out excessQueueCpuMs);
                }
                string previousDispatcherEvent =
                    previousEvent?.Kind.ToString() ?? "";
                double previousDispatcherEventAgeMs =
                    previousEvent is { } prior
                        ? TicksToMilliseconds(
                            strictQueueBoundaryTimestamp - prior.Timestamp)
                        : -1;
                double strictSchedulerQueueDelayMs =
                    TicksToMilliseconds(strictQueueDelayTicks);
                double productGapMs =
                    Math.Max(0, rawGapMs - strictSchedulerQueueDelayMs);
                maxProductGapMs = Math.Max(maxProductGapMs, productGapMs);
                maxStrictSchedulerQueueDelayMs = Math.Max(
                    maxStrictSchedulerQueueDelayMs,
                    strictSchedulerQueueDelayMs);
                string classification;
                if (heartbeatBuilder is null)
                {
                    classification = "INCONCLUSIVE";
                    inconclusiveCount++;
                }
                else if (strictSchedulerQueueDelayMs > 0
                    && productGapMs <= heartbeatBudgetMs)
                {
                    classification = "SCHEDULER_QUEUE_DELAY_DIAGNOSTIC";
                    schedulerQueueDelayCount++;
                }
                else if (productGapMs > heartbeatBudgetMs
                    && (activeOperationOverlapMs > 0 || panelPhaseOverlapMs > 0))
                {
                    classification = "ACTIVE_OPERATION_DIAGNOSTIC";
                    activeOperationDiagnosticCount++;
                }
                else
                {
                    classification = "INCONCLUSIVE";
                    inconclusiveCount++;
                }

                overBudget.Add(new(
                    heartbeat.Operation,
                    heartbeat.ProjectionGeneration,
                    rawGapMs,
                    heartbeat.UiThreadCpuMs,
                    heartbeat.HeartbeatTag,
                    heartbeatBuilder?.Identity ?? -1,
                    queueWallMs,
                    queueCpuMs,
                    heartbeatStartMarkerDelayMs,
                    excessQueueCpuMs,
                    strictSchedulerQueueDelayMs,
                    productGapMs,
                    activeOperationOverlapMs,
                    panelPhaseOverlapMs,
                    previousDispatcherEvent,
                    previousDispatcherEventAgeMs,
                    classification,
                    activeOperations,
                    overlappingPanelPhases));
            }

            bool sensorValid =
                _started
                && _stopped
                && Volatile.Read(ref _ringOverflow) == 0
                && Volatile.Read(ref _uiThreadCpuReadFailureCount) == 0
                && _activeStackOverflowCount == 0
                && _activeStackMismatchCount == 0
                && lifecycleTimestampInversionCount == 0
                && startedWithoutTerminalCount == 0
                && heartbeatLifecycleMissingCount == 0
                && !panelPhaseOverflow
                && panelPhaseInvalidCount == 0;
            var summary = new CatalogDispatcherDiagnosticSummary
            {
                SensorValid = sensorValid,
                HooksStarted = _started,
                HooksStopped = _stopped,
                RawEventCount = rawEvents.Count,
                RingOverflow = Volatile.Read(ref _ringOverflow) != 0,
                UiThreadCpuReadFailureCount =
                    Volatile.Read(ref _uiThreadCpuReadFailureCount),
                ActiveStackOverflowCount = _activeStackOverflowCount,
                ActiveStackMismatchCount = _activeStackMismatchCount,
                LifecycleTimestampInversionCount = lifecycleTimestampInversionCount,
                ConcurrentPostedStartReorderCount =
                    concurrentPostedStartReorderCount,
                StartedWithoutTerminalCount = startedWithoutTerminalCount,
                BoundaryTruncatedOperationCount = boundaryTruncatedOperationCount,
                HeartbeatLifecycleMissingCount = heartbeatLifecycleMissingCount,
                PanelPhaseOverflow = panelPhaseOverflow,
                PanelPhaseInvalidCount = panelPhaseInvalidCount,
                RawOverBudgetCount = rawOverBudgetCount,
                SchedulerQueueDelayCount = schedulerQueueDelayCount,
                ActiveOperationDiagnosticCount = activeOperationDiagnosticCount,
                InconclusiveCount = inconclusiveCount,
                MaxProductGapMs = maxProductGapMs,
                MaxStrictSchedulerQueueDelayMs =
                    maxStrictSchedulerQueueDelayMs,
                OverBudgetHeartbeats = overBudget,
            };
            // The summary is value-only. Release DispatcherOperation graphs
            // before the harness takes its post-measurement full-GC snapshot.
            Array.Clear(_events);
            _operationIdentities.Clear();
            return summary;
        }

        public void Dispose()
        {
            StopHooks();
            Array.Clear(_events);
            if (_uiThreadHandle != IntPtr.Zero)
                CloseHandle(_uiThreadHandle);
        }

        private DispatcherOperation?[] StopHooks()
        {
            if (!_stopped)
            {
                if (_started)
                {
                    DispatcherHooks hooks = _dispatcher.Hooks;
                    hooks.OperationPosted -= Hooks_OperationPosted;
                    hooks.OperationStarted -= Hooks_OperationStarted;
                    hooks.OperationCompleted -= Hooks_OperationCompleted;
                    hooks.OperationAborted -= Hooks_OperationAborted;
                }
                _stopped = true;
            }
            var boundary = new DispatcherOperation?[_activeDepth];
            Array.Copy(_activeStack, boundary, _activeDepth);
            return boundary;
        }

        private long Record(
            CatalogDispatcherEventKind kind,
            DispatcherOperation? operation,
            long heartbeatTag = 0)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long cpuTicks = ReadUiThreadCpuTicks();
            long sequence = Interlocked.Increment(ref _nextEventIndex);
            if ((ulong)sequence >= (ulong)_events.Length)
            {
                Interlocked.Exchange(ref _ringOverflow, 1);
                return cpuTicks;
            }

            DispatcherPriority priority = DispatcherPriority.Invalid;
            int operationIdentity = 0;
            string callbackName = "";
            if (operation is not null)
            {
                CatalogDispatcherOperationIdentity identity =
                    IdentityFor(operation);
                operationIdentity = identity.Value;
                callbackName = identity.CallbackName;
                try { priority = operation.Priority; }
                catch (InvalidOperationException) { }
            }
            _events[sequence] = new CatalogDispatcherRawEvent(
                sequence,
                timestamp,
                cpuTicks,
                GetCurrentThreadId(),
                kind,
                operationIdentity,
                callbackName,
                priority,
                _appOperationProvider(),
                heartbeatTag);
            return cpuTicks;
        }

        private CatalogDispatcherOperationIdentity IdentityFor(
            DispatcherOperation operation)
            => _operationIdentities.GetValue(
                operation,
                key => new CatalogDispatcherOperationIdentity(
                    Interlocked.Increment(ref _nextOperationIdentity),
                    DispatcherOperationCallbackName(key)));

        private void Hooks_OperationPosted(object? sender, DispatcherHookEventArgs e)
            => Record(CatalogDispatcherEventKind.OperationPosted, e.Operation);

        private void Hooks_OperationStarted(object? sender, DispatcherHookEventArgs e)
        {
            if (GetCurrentThreadId() == _uiThreadId)
            {
                if (_activeDepth >= _activeStack.Length)
                    _activeStackOverflowCount++;
                else
                    _activeStack[_activeDepth++] = e.Operation;
            }
            Record(CatalogDispatcherEventKind.OperationStarted, e.Operation);
        }

        private void Hooks_OperationCompleted(object? sender, DispatcherHookEventArgs e)
        {
            Record(CatalogDispatcherEventKind.OperationCompleted, e.Operation);
            PopActive(e.Operation);
        }

        private void Hooks_OperationAborted(object? sender, DispatcherHookEventArgs e)
            => Record(CatalogDispatcherEventKind.OperationAborted, e.Operation);

        private void Hooks_OperationPriorityChanged(object? sender, DispatcherHookEventArgs e)
            => Record(CatalogDispatcherEventKind.OperationPriorityChanged, e.Operation);

        private void Hooks_DispatcherInactive(object? sender, EventArgs e)
            => Record(CatalogDispatcherEventKind.DispatcherInactive, operation: null);

        private void PopActive(DispatcherOperation operation)
        {
            if (GetCurrentThreadId() != _uiThreadId)
                return;
            if (_activeDepth <= 0
                || !ReferenceEquals(_activeStack[_activeDepth - 1], operation))
            {
                _activeStackMismatchCount++;
                return;
            }
            _activeStack[--_activeDepth] = null;
        }

        private static CatalogDispatcherOperationDiagnostic ToDiagnostic(
            CatalogDispatcherOperationBuilder builder)
            => new(
                builder.Identity,
                builder.AppOperation,
                builder.CallbackName,
                builder.Priority.ToString(),
                builder.PostedTimestamp,
                builder.StartedTimestamp,
                builder.TerminalTimestamp,
                builder.TerminalKind,
                builder.PostedTimestamp > 0 && builder.StartedTimestamp > 0
                    ? TicksToMilliseconds(
                        builder.StartedTimestamp - builder.PostedTimestamp)
                    : -1,
                CpuTicksToMilliseconds(
                    builder.PostedCpuTicks,
                    builder.StartedCpuTicks),
                builder.StartedTimestamp > 0 && builder.TerminalTimestamp > 0
                    ? TicksToMilliseconds(
                        builder.TerminalTimestamp - builder.StartedTimestamp)
                    : -1,
                CpuTicksToMilliseconds(
                    builder.StartedCpuTicks,
                    builder.TerminalCpuTicks));

        private static long StrictSchedulerQueueDelayTicks(
            long queueStart,
            long queueEnd,
            int heartbeatOperationIdentity,
            CatalogDispatcherOperationBuilder heartbeatBuilder,
            IReadOnlyDictionary<int, CatalogDispatcherOperationBuilder>
                operationBuilders,
            IReadOnlyList<VirtualizingPanelPhaseDiagnostic> panelPhases,
            IReadOnlyList<CatalogDispatcherRawEvent> rawEvents,
            out CatalogDispatcherRawEvent? firstBoundaryEvent,
            out long firstBoundaryStartTimestamp,
            out double firstBoundaryCpuMs)
        {
            firstBoundaryEvent = null;
            firstBoundaryStartTimestamp = 0;
            firstBoundaryCpuMs = -1;
            if (queueEnd <= queueStart
                || heartbeatBuilder.StartedCpuTicks < 0)
            {
                return 0;
            }

            var blockers = new List<(long Start, long End, long StartCpuTicks)>();
            foreach ((int operationIdentity, CatalogDispatcherOperationBuilder builder)
                in operationBuilders)
            {
                if (operationIdentity == heartbeatOperationIdentity
                    || builder.StartedTimestamp <= 0
                    || builder.TerminalTimestamp <= builder.StartedTimestamp)
                {
                    continue;
                }
                long start = Math.Max(queueStart, builder.StartedTimestamp);
                long end = Math.Min(queueEnd, builder.TerminalTimestamp);
                if (end > start)
                    blockers.Add((start, end, builder.StartedCpuTicks));
            }
            foreach (VirtualizingPanelPhaseDiagnostic phase in panelPhases)
            {
                long start = Math.Max(queueStart, phase.StartTimestamp);
                long end = Math.Min(queueEnd, phase.EndTimestamp);
                if (end > start)
                    blockers.Add((start, end, phase.StartCpuTicks));
            }
            blockers.Sort(static (left, right) =>
            {
                int start = left.Start.CompareTo(right.Start);
                return start != 0 ? start : left.End.CompareTo(right.End);
            });

            long strictTicks = 0;
            long cursor = queueStart;
            foreach ((long start, long end, long startCpuTicks) in blockers)
            {
                if (start > cursor)
                {
                    AddStrictFreeSegment(
                        cursor,
                        start,
                        startCpuTicks,
                        rawEvents,
                        ref strictTicks,
                        ref firstBoundaryEvent,
                        ref firstBoundaryStartTimestamp,
                        ref firstBoundaryCpuMs);
                }
                if (end > cursor)
                    cursor = end;
            }
            if (cursor < queueEnd)
            {
                AddStrictFreeSegment(
                    cursor,
                    queueEnd,
                    heartbeatBuilder.StartedCpuTicks,
                    rawEvents,
                    ref strictTicks,
                    ref firstBoundaryEvent,
                    ref firstBoundaryStartTimestamp,
                    ref firstBoundaryCpuMs);
            }
            return strictTicks;
        }

        private static void AddStrictFreeSegment(
            long start,
            long end,
            long endCpuTicks,
            IReadOnlyList<CatalogDispatcherRawEvent> rawEvents,
            ref long strictTicks,
            ref CatalogDispatcherRawEvent? firstBoundaryEvent,
            ref long firstBoundaryStartTimestamp,
            ref double firstBoundaryCpuMs)
        {
            if (end <= start || endCpuTicks < 0)
                return;
            CatalogDispatcherRawEvent? previous =
                PreviousDispatcherEvent(rawEvents, start);
            if (previous is not { } boundary
                || boundary.Kind is not (
                    CatalogDispatcherEventKind.OperationCompleted
                    or CatalogDispatcherEventKind.OperationAborted
                    or CatalogDispatcherEventKind.DispatcherInactive)
                || boundary.UiThreadCpuTicks < 0
                || endCpuTicks != boundary.UiThreadCpuTicks)
            {
                return;
            }

            strictTicks += end - start;
            if (firstBoundaryEvent is null)
            {
                firstBoundaryEvent = boundary;
                firstBoundaryStartTimestamp = start;
                firstBoundaryCpuMs = 0;
            }
        }

        private static CatalogDispatcherRawEvent? PreviousDispatcherEvent(
            IReadOnlyList<CatalogDispatcherRawEvent> events,
            long timestamp)
        {
            for (int index = events.Count - 1; index >= 0; index--)
            {
                CatalogDispatcherRawEvent item = events[index];
                if (item.Timestamp > timestamp)
                    continue;
                if (item.Kind is CatalogDispatcherEventKind.OperationCompleted
                    or CatalogDispatcherEventKind.OperationAborted
                    or CatalogDispatcherEventKind.DispatcherInactive
                    or CatalogDispatcherEventKind.OperationStarted)
                {
                    return item;
                }
            }
            return null;
        }

        private static string DispatcherOperationCallbackName(
            DispatcherOperation operation)
        {
            try
            {
                if (DispatcherOperationMethodField?.GetValue(operation)
                    is not Delegate callback)
                {
                    return "";
                }
                MethodInfo method = callback.Method;
                string declaringType =
                    method.DeclaringType?.FullName ?? "<unknown>";
                string targetType =
                    callback.Target?.GetType().FullName ?? "<static>";
                return $"{declaringType}.{method.Name} [target={targetType}]";
            }
            catch
            {
                return "";
            }
        }

        private static long OverlapTicks(
            long start,
            long end,
            long candidateStart,
            long candidateEnd)
            => Math.Max(
                0,
                Math.Min(end, candidateEnd) - Math.Max(start, candidateStart));

        private static double CpuTicksToMilliseconds(long start, long end)
            => start >= 0 && end >= start
                ? TimeSpan.FromTicks(end - start).TotalMilliseconds
                : -1;

        private static long MillisecondsToTicks(double milliseconds)
            => (long)Math.Ceiling(
                milliseconds
                    * Stopwatch.Frequency
                    / 1000d);

        private static double TicksToMilliseconds(long ticks)
            => ticks * 1000d / Stopwatch.Frequency;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(
            IntPtr thread,
            out long creationTime,
            out long exitTime,
            out long kernelTime,
            out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

export type ThumbnailWarmupPriority = 'focused' | 'visible' | 'nearby';

type PendingWarmup = {
  path: string;
  dirPath: string;
  contextKey: string;
  priority: ThumbnailWarmupPriority;
  sequence: number;
};

type SentWarmup = {
  priority: ThumbnailWarmupPriority;
  sentAt: number;
};

type TimerId = ReturnType<typeof setTimeout>;

export type ThumbnailWarmupBatcherOptions = {
  dispatch: (paths: string[], dirPath: string, priority: ThumbnailWarmupPriority) => void;
  delayMs?: number;
  dedupeMs?: number;
  highPriorityDedupeMs?: number;
  now?: () => number;
  setTimer?: (callback: () => void, delayMs: number) => TimerId;
  clearTimer?: (timerId: TimerId) => void;
};

export type ThumbnailWarmupEnqueueOptions = {
  dirPath: string;
  contextKey: string;
  priority: ThumbnailWarmupPriority;
};

const DEFAULT_DELAY_MS = 40;
const DEFAULT_DEDUPE_MS = 3500;
const DEFAULT_HIGH_PRIORITY_DEDUPE_MS = 900;
const PRIORITY_RANK: Record<ThumbnailWarmupPriority, number> = {
  focused: 0,
  visible: 1,
  nearby: 2,
};

function isHigherPriority(next: ThumbnailWarmupPriority, previous: ThumbnailWarmupPriority) {
  return PRIORITY_RANK[next] < PRIORITY_RANK[previous];
}

function getWarmupKey(dirPath: string, contextKey: string, imagePath: string) {
  return `${dirPath}\u0000${contextKey}\u0000${imagePath}`;
}

export function createThumbnailWarmupBatcher({
  dispatch,
  delayMs = DEFAULT_DELAY_MS,
  dedupeMs = DEFAULT_DEDUPE_MS,
  highPriorityDedupeMs = DEFAULT_HIGH_PRIORITY_DEDUPE_MS,
  now = () => Date.now(),
  setTimer = (callback, delay) => setTimeout(callback, delay),
  clearTimer = (timerId) => clearTimeout(timerId),
}: ThumbnailWarmupBatcherOptions) {
  const pending = new Map<string, PendingWarmup>();
  const sent = new Map<string, SentWarmup>();
  let sequence = 0;
  let timerId: TimerId | null = null;

  const highPriorityDedupeWindowMs = Math.min(dedupeMs, Math.max(0, highPriorityDedupeMs));

  const getSkipWindowMs = (
    previousPriority: ThumbnailWarmupPriority,
    nextPriority: ThumbnailWarmupPriority
  ) => (
    previousPriority === 'nearby' || nextPriority === 'nearby'
      ? dedupeMs
      : highPriorityDedupeWindowMs
  );

  const pruneSent = (currentTime: number) => {
    for (const [key, record] of sent) {
      if (currentTime - record.sentAt > dedupeMs) {
        sent.delete(key);
      }
    }
  };

  const scheduleFlush = () => {
    if (timerId !== null) return;
    timerId = setTimer(() => {
      timerId = null;
      flush();
    }, delayMs);
  };

  const shouldSkipSent = (
    key: string,
    priority: ThumbnailWarmupPriority,
    currentTime: number
  ) => {
    const record = sent.get(key);
    if (!record) return false;
    if (currentTime - record.sentAt > getSkipWindowMs(record.priority, priority)) {
      return false;
    }
    return !isHigherPriority(priority, record.priority);
  };

  const enqueue = (
    paths: string[],
    { dirPath, contextKey, priority }: ThumbnailWarmupEnqueueOptions
  ) => {
    if (paths.length === 0) return;

    const currentTime = now();
    pruneSent(currentTime);
    for (const imagePath of paths) {
      if (!imagePath) continue;
      const key = getWarmupKey(dirPath, contextKey, imagePath);
      if (shouldSkipSent(key, priority, currentTime)) continue;

      const existing = pending.get(key);
      if (existing) {
        if (isHigherPriority(priority, existing.priority)) {
          existing.priority = priority;
        }
        continue;
      }

      pending.set(key, {
        path: imagePath,
        dirPath,
        contextKey,
        priority,
        sequence: sequence++,
      });
    }

    if (pending.size > 0) {
      scheduleFlush();
    }
  };

  function flush(allowedPriorities?: ReadonlySet<ThumbnailWarmupPriority>) {
    if (pending.size === 0) return;

    const currentTime = now();
    const selectedEntries = Array.from(pending.entries())
      .filter(([, record]) => !allowedPriorities || allowedPriorities.has(record.priority));
    if (selectedEntries.length === 0) return;
    const records = selectedEntries
      .map(([key, record]) => ({ key, ...record }))
      .sort((a, b) => PRIORITY_RANK[a.priority] - PRIORITY_RANK[b.priority] || a.sequence - b.sequence);
    for (const [key] of selectedEntries) pending.delete(key);

    const groups = new Map<string, PendingWarmup[]>();
    for (const record of records) {
      const groupKey = `${record.dirPath}\u0000${record.priority}`;
      const group = groups.get(groupKey);
      if (group) {
        group.push(record);
      } else {
        groups.set(groupKey, [record]);
      }
      sent.set(record.key, {
        priority: record.priority,
        sentAt: currentTime,
      });
    }

    for (const group of groups.values()) {
      const [first] = group;
      if (!first) continue;
      dispatch(group.map((record) => record.path), first.dirPath, first.priority);
    }
  }

  const clear = () => {
    pending.clear();
    sent.clear();
    if (timerId !== null) {
      clearTimer(timerId);
      timerId = null;
    }
  };

  return {
    enqueue,
    flush: () => flush(),
    flushHighPriority: () => flush(new Set<ThumbnailWarmupPriority>(['focused', 'visible'])),
    clear,
    getPendingCount: () => pending.size,
    getSentCount: () => sent.size,
  };
}

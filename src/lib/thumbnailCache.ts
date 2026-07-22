import fs from 'fs';
import os from 'os';
import path from 'path';
import crypto from 'crypto';
import sharp from 'sharp';

export const THUMB_DIR = path.join(/*turbopackIgnore: true*/ process.cwd(), '.cache', 'thumbs');
export const DISPLAY_DIR = path.join(/*turbopackIgnore: true*/ process.cwd(), '.cache', 'display');
const THUMB_WIDTH = 300;
const DISPLAY_MAX_SIZE = 2200;
const RIFF_HEADER = Buffer.from('RIFF');
const WEBP_HEADER = Buffer.from('WEBP');
const DEFAULT_THUMB_CONCURRENCY = Math.max(
  4,
  Math.min(12, typeof os.availableParallelism === 'function' ? os.availableParallelism() : os.cpus().length)
);
export const MAX_THUMB_CONCURRENCY = Math.max(
  1,
  Math.min(
    16,
    Number.parseInt(process.env.PV_THUMB_CONCURRENCY || String(DEFAULT_THUMB_CONCURRENCY), 10) ||
      DEFAULT_THUMB_CONCURRENCY
  )
);
const WARMUP_RESERVED_VISIBLE_SLOTS = MAX_THUMB_CONCURRENCY >= 6 ? 2 : 1;
const MAX_WARMUP_WORKERS = Math.max(1, MAX_THUMB_CONCURRENCY - WARMUP_RESERVED_VISIBLE_SLOTS);
export const MAX_PENDING_WARM_THUMB_TASKS = 1200;

type WarmupState = {
  running: boolean;
  total: number;
  queued: number;
  completed: number;
  failed: number;
  startedAt: string | null;
  updatedAt: string | null;
  source: string;
  current: string;
};

interface ThumbTask {
  cacheKey: string;
  priority: number;
  sequence: number;
  warmup: boolean;
  started: boolean;
  run: () => Promise<void>;
  resolve: () => void;
  reject: (error: unknown) => void;
  promise: Promise<void>;
}

type ThumbnailResult = {
  thumbPath: string;
  created: boolean;
  versionMatched?: boolean;
};

type DisplayImageResult = {
  displayPath: string;
  created: boolean;
  versionMatched?: boolean;
};

sharp.concurrency(MAX_THUMB_CONCURRENCY);
sharp.cache({ files: 100, memory: 256 });

let activeThumbJobs = 0;
let thumbJobSequence = 0;
let warmupGeneration = 0;

const thumbQueue: ThumbTask[] = [];
const pendingThumbs = new Map<string, ThumbTask>();
const WEBP_HEADER_BYTES = 16;
const usableCacheMemo = new Map<string, { size: number; mtimeMs: number; usable: boolean }>();
const warmupState: WarmupState = {
  running: false,
  total: 0,
  queued: 0,
  completed: 0,
  failed: 0,
  startedAt: null,
  updatedAt: null,
  source: '',
  current: '',
};

export function ensureThumbDir() {
  if (!fs.existsSync(THUMB_DIR)) {
    fs.mkdirSync(THUMB_DIR, { recursive: true });
  }
}

export function ensureDisplayDir() {
  if (!fs.existsSync(DISPLAY_DIR)) {
    fs.mkdirSync(DISPLAY_DIR, { recursive: true });
  }
}

function getCacheVersionKey(resolved: string, cacheVersion: string) {
  return `${resolved}|${cacheVersion}`;
}

function getDisplayCacheVersionKey(resolved: string, cacheVersion: string) {
  return `${getCacheVersionKey(resolved, cacheVersion)}|display:${DISPLAY_MAX_SIZE}`;
}

export async function getThumbnailPath(resolved: string, cacheVersion?: string) {
  if (cacheVersion) {
    const hash = Buffer.from(getCacheVersionKey(resolved, cacheVersion)).toString('base64url');
    return path.join(/*turbopackIgnore: true*/ THUMB_DIR, `${hash}.webp`);
  }
  const stat = await fs.promises.stat(resolved);
  const hash = Buffer.from(getCacheVersionKey(resolved, String(stat.mtimeMs))).toString('base64url');
  return path.join(/*turbopackIgnore: true*/ THUMB_DIR, `${hash}.webp`);
}

export async function getDisplayPath(resolved: string, cacheVersion?: string) {
  if (cacheVersion) {
    const hash = Buffer.from(getDisplayCacheVersionKey(resolved, cacheVersion)).toString('base64url');
    return path.join(/*turbopackIgnore: true*/ DISPLAY_DIR, `${hash}.webp`);
  }
  const stat = await fs.promises.stat(resolved);
  const hash = Buffer.from(getDisplayCacheVersionKey(resolved, String(stat.mtimeMs))).toString('base64url');
  return path.join(/*turbopackIgnore: true*/ DISPLAY_DIR, `${hash}.webp`);
}

async function hasWebpHeader(filePath: string) {
  const stat = await fs.promises.stat(filePath);
  if (stat.size <= WEBP_HEADER_BYTES) return false;
  const handle = await fs.promises.open(filePath, 'r');
  try {
    const header = Buffer.allocUnsafe(WEBP_HEADER_BYTES);
    const { bytesRead } = await handle.read(header, 0, WEBP_HEADER_BYTES, 0);
    const declaredSize = bytesRead >= 8 ? header.readUInt32LE(4) : -1;
    const chunkType = bytesRead >= 16 ? header.subarray(12, 16).toString('ascii') : '';
    return (
      bytesRead === WEBP_HEADER_BYTES &&
      header.subarray(0, 4).equals(RIFF_HEADER) &&
      header.subarray(8, 12).equals(WEBP_HEADER) &&
      declaredSize + 8 === stat.size &&
      (chunkType === 'VP8 ' || chunkType === 'VP8L' || chunkType === 'VP8X')
    );
  } finally {
    await handle.close();
  }
}

async function isUsableCachedImage(filePath: string) {
  try {
    const stat = await fs.promises.stat(filePath);
    if (stat.size <= WEBP_HEADER_BYTES) return false;
    const memo = usableCacheMemo.get(filePath);
    if (memo && memo.size === stat.size && memo.mtimeMs === stat.mtimeMs) return memo.usable;
    const usable = await hasWebpHeader(filePath);
    usableCacheMemo.set(filePath, { size: stat.size, mtimeMs: stat.mtimeMs, usable });
    return usable;
  } catch {
    usableCacheMemo.delete(filePath);
    return false;
  }
}

async function getCurrentCacheVersion(resolved: string) {
  const stat = await fs.promises.stat(resolved);
  return String(stat.mtimeMs);
}

async function removeBrokenCacheFile(filePath: string) {
  usableCacheMemo.delete(filePath);
  await fs.promises.rm(filePath, { force: true }).catch(() => {});
}

function getTempCachePath(finalPath: string) {
  const dir = path.dirname(finalPath);
  const suffix = crypto.randomBytes(6).toString('hex');
  return path.join(dir, `tmp-${process.pid}-${Date.now()}-${suffix}.tmp`);
}

function wait(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isTransientWindowsFileError(error: unknown) {
  if (!error || typeof error !== 'object') return false;
  const code = (error as NodeJS.ErrnoException).code;
  return code === 'EPERM' || code === 'EBUSY' || code === 'EACCES';
}

async function renameCacheFileWithRetry(tmpPath: string, finalPath: string) {
  const delays = [20, 50, 100, 200];
  for (let attempt = 0; ; attempt++) {
    try {
      await fs.promises.rm(finalPath, { force: true }).catch(() => {});
      await fs.promises.rename(tmpPath, finalPath);
      return;
    } catch (error) {
      if (!isTransientWindowsFileError(error) || attempt >= delays.length) throw error;
      await wait(delays[attempt]);
    }
  }
}

async function writeCacheAtomically(finalPath: string, write: (tmpPath: string) => Promise<void>) {
  const tmpPath = getTempCachePath(finalPath);
  try {
    await write(tmpPath);
    await renameCacheFileWithRetry(tmpPath, finalPath);
    usableCacheMemo.delete(finalPath);
  } catch (error) {
    await fs.promises.rm(tmpPath, { force: true }).catch(() => {});
    throw error;
  }
}

function takeNextThumbTask(): ThumbTask | null {
  if (thumbQueue.length === 0) return null;

  let bestIndex = 0;
  for (let i = 1; i < thumbQueue.length; i++) {
    const current = thumbQueue[i];
    const best = thumbQueue[bestIndex];
    if (compareThumbnailQueueEntries(current, best) < 0) {
      bestIndex = i;
    }
  }

  const [task] = thumbQueue.splice(bestIndex, 1);
  return task ?? null;
}

export type ThumbnailQueuePolicyEntry = Pick<ThumbTask, 'priority' | 'sequence' | 'warmup'>;

export function compareThumbnailQueueEntries(
  first: ThumbnailQueuePolicyEntry,
  second: ThumbnailQueuePolicyEntry,
) {
  if (first.priority !== second.priority) return first.priority - second.priority;
  // A real image response waiting on the same decode class always owns the
  // slot before speculative warmup work.
  if (first.warmup !== second.warmup) return first.warmup ? 1 : -1;
  // Visible/nearby warmups describe a moving viewport. Newer work is more
  // likely to remain on screen; background catalog warmup stays FIFO.
  if (first.warmup && first.priority <= 1) return second.sequence - first.sequence;
  return first.sequence - second.sequence;
}

export function selectSupersededWarmupQueueEntries<T extends ThumbnailQueuePolicyEntry>(
  entries: readonly T[],
  maximum = MAX_PENDING_WARM_THUMB_TASKS,
): T[] {
  const warmupTasks = entries
    .filter((task) => task.warmup)
    // Discard the least relevant class first, then the oldest request inside
    // that class. Direct response work is never eligible for trimming.
    .sort((first, second) => (
      second.priority - first.priority || first.sequence - second.sequence
    ));
  return warmupTasks.slice(0, Math.max(0, warmupTasks.length - Math.max(0, maximum)));
}

function trimSupersededWarmupTasks() {
  const superseded = selectSupersededWarmupQueueEntries(
    thumbQueue.filter((task) => !task.started),
  );
  for (const task of superseded) {
    const index = thumbQueue.indexOf(task);
    if (index >= 0) thumbQueue.splice(index, 1);
    if (pendingThumbs.get(task.cacheKey) === task) pendingThumbs.delete(task.cacheKey);
    task.reject(new Error('Thumbnail warmup superseded by a newer viewport.'));
  }
}

function promotePendingTask(task: ThumbTask, priority: number, warmup: boolean) {
  if (task.started) return;
  if (priority < task.priority) task.priority = priority;
  if (!warmup) {
    task.warmup = false;
  } else if (priority <= 1) {
    task.sequence = thumbJobSequence++;
  }
}

function runNextThumbJob() {
  while (activeThumbJobs < MAX_THUMB_CONCURRENCY) {
    const next = takeNextThumbTask();
    if (!next) return;

    next.started = true;
    activeThumbJobs += 1;
    void next.run()
      .then(next.resolve)
      .catch(next.reject)
      .finally(() => {
        activeThumbJobs = Math.max(0, activeThumbJobs - 1);
        runNextThumbJob();
      });
  }
}

export async function ensureThumbnail(
  resolved: string,
  priority: number,
  cacheVersion?: string,
  warmup = false,
): Promise<ThumbnailResult> {
  ensureThumbDir();
  const thumbPath = await getThumbnailPath(resolved, cacheVersion);

  const pending = pendingThumbs.get(thumbPath);
  if (pending) {
    promotePendingTask(pending, priority, warmup);
    await pending.promise;
    return { thumbPath, created: true };
  }

  if (fs.existsSync(/*turbopackIgnore: true*/ thumbPath) && await isUsableCachedImage(thumbPath)) {
    return { thumbPath, created: false, versionMatched: true };
  }

  if (cacheVersion) {
    const currentVersion = await getCurrentCacheVersion(resolved);
    if (currentVersion !== cacheVersion) {
      const currentThumbPath = await getThumbnailPath(resolved, currentVersion);
      const currentPending = pendingThumbs.get(currentThumbPath);
      if (currentPending) {
        promotePendingTask(currentPending, priority, warmup);
        await currentPending.promise;
        return { thumbPath: currentThumbPath, created: true, versionMatched: false };
      }
      if (fs.existsSync(/*turbopackIgnore: true*/ currentThumbPath) && await isUsableCachedImage(currentThumbPath)) {
        return { thumbPath: currentThumbPath, created: false, versionMatched: false };
      }
      return ensureThumbnail(resolved, priority, currentVersion, warmup).then((result) => ({
        ...result,
        versionMatched: false,
      }));
    }
  }

  await removeBrokenCacheFile(thumbPath);

  let resolveTask!: () => void;
  let rejectTask!: (error: unknown) => void;
  const promise = new Promise<void>((resolve, reject) => {
    resolveTask = resolve;
    rejectTask = reject;
  });

  const task: ThumbTask = {
    cacheKey: thumbPath,
    priority,
    sequence: thumbJobSequence++,
    warmup,
    started: false,
    resolve: resolveTask,
    reject: rejectTask,
    promise,
    run: async () => {
      await writeCacheAtomically(thumbPath, async (tmpPath) => {
        await sharp(resolved, { sequentialRead: true, failOn: 'none' })
          .resize(THUMB_WIDTH, undefined, { withoutEnlargement: true })
          .webp({ quality: 72, effort: 2 })
          .toFile(tmpPath);
      });
    },
  };

  pendingThumbs.set(thumbPath, task);
  thumbQueue.push(task);
  if (warmup) trimSupersededWarmupTasks();
  runNextThumbJob();

  try {
    await promise;
  } finally {
    if (pendingThumbs.get(thumbPath) === task) pendingThumbs.delete(thumbPath);
  }

  return { thumbPath, created: true, versionMatched: true };
}

export async function ensureDisplayImage(
  resolved: string,
  priority: number,
  cacheVersion?: string
): Promise<DisplayImageResult> {
  ensureDisplayDir();
  const displayPath = await getDisplayPath(resolved, cacheVersion);

  const pending = pendingThumbs.get(displayPath);
  if (pending) {
    if (!pending.started && priority < pending.priority) {
      pending.priority = priority;
    }
    await pending.promise;
    return { displayPath, created: true };
  }

  if (fs.existsSync(/*turbopackIgnore: true*/ displayPath) && await isUsableCachedImage(displayPath)) {
    return { displayPath, created: false, versionMatched: true };
  }

  if (cacheVersion) {
    const currentVersion = await getCurrentCacheVersion(resolved);
    if (currentVersion !== cacheVersion) {
      const currentDisplayPath = await getDisplayPath(resolved, currentVersion);
      const currentPending = pendingThumbs.get(currentDisplayPath);
      if (currentPending) {
        if (!currentPending.started && priority < currentPending.priority) {
          currentPending.priority = priority;
        }
        await currentPending.promise;
        return { displayPath: currentDisplayPath, created: true, versionMatched: false };
      }
      if (fs.existsSync(/*turbopackIgnore: true*/ currentDisplayPath) && await isUsableCachedImage(currentDisplayPath)) {
        return { displayPath: currentDisplayPath, created: false, versionMatched: false };
      }
      return ensureDisplayImage(resolved, priority, currentVersion).then((result) => ({
        ...result,
        versionMatched: false,
      }));
    }
  }

  await removeBrokenCacheFile(displayPath);

  let resolveTask!: () => void;
  let rejectTask!: (error: unknown) => void;
  const promise = new Promise<void>((resolve, reject) => {
    resolveTask = resolve;
    rejectTask = reject;
  });

  const task: ThumbTask = {
    cacheKey: displayPath,
    priority,
    sequence: thumbJobSequence++,
    warmup: false,
    started: false,
    resolve: resolveTask,
    reject: rejectTask,
    promise,
    run: async () => {
      await writeCacheAtomically(displayPath, async (tmpPath) => {
        await sharp(resolved, { sequentialRead: true, failOn: 'none' })
          .resize(DISPLAY_MAX_SIZE, DISPLAY_MAX_SIZE, { fit: 'inside', withoutEnlargement: true })
          .webp({ quality: 86, effort: 2 })
          .toFile(tmpPath);
      });
    },
  };

  pendingThumbs.set(displayPath, task);
  thumbQueue.push(task);
  runNextThumbJob();

  try {
    await promise;
  } finally {
    if (pendingThumbs.get(displayPath) === task) pendingThumbs.delete(displayPath);
  }

  return { displayPath, created: true, versionMatched: true };
}

export function enqueueThumbnails(paths: string[], priority: number) {
  const uniquePaths = Array.from(new Set(paths.filter(Boolean)));
  for (const target of uniquePaths) {
    void ensureThumbnail(target, priority, undefined, true).catch(() => {
      // Best-effort warmup; visible image requests can retry.
    });
  }
  return getThumbnailWarmupState();
}

export function startThumbnailWarmup(paths: string[], source: string) {
  const uniquePaths = Array.from(new Set(paths.filter(Boolean)));
  const generation = ++warmupGeneration;
  const now = new Date().toISOString();

  warmupState.running = uniquePaths.length > 0;
  warmupState.total = uniquePaths.length;
  warmupState.queued = uniquePaths.length;
  warmupState.completed = 0;
  warmupState.failed = 0;
  warmupState.startedAt = now;
  warmupState.updatedAt = now;
  warmupState.source = source;
  warmupState.current = '';

  if (uniquePaths.length === 0) return getThumbnailWarmupState();

  let cursor = 0;
  const workerCount = Math.min(MAX_WARMUP_WORKERS, uniquePaths.length);

  const runWorker = async () => {
    while (generation === warmupGeneration) {
      const currentIndex = cursor++;
      if (currentIndex >= uniquePaths.length) return;
      const target = uniquePaths[currentIndex];
      warmupState.current = target;
      warmupState.queued = Math.max(0, uniquePaths.length - currentIndex - 1);
      try {
        await ensureThumbnail(target, 3, undefined, true);
        warmupState.completed += 1;
      } catch {
        warmupState.failed += 1;
      } finally {
        warmupState.updatedAt = new Date().toISOString();
      }
    }
  };

  void Promise.all(Array.from({ length: workerCount }, runWorker)).finally(() => {
    if (generation !== warmupGeneration) return;
    warmupState.running = false;
    warmupState.queued = 0;
    warmupState.current = '';
    warmupState.updatedAt = new Date().toISOString();
  });

  return getThumbnailWarmupState();
}

export function cancelThumbnailWarmup() {
  warmupGeneration += 1;
  warmupState.running = false;
  warmupState.queued = 0;
  warmupState.current = '';
  warmupState.updatedAt = new Date().toISOString();
  return getThumbnailWarmupState();
}

export function getThumbnailWarmupState(): WarmupState & {
  activeThumbJobs: number;
  pendingThumbs: number;
  queuedThumbJobs: number;
  queuedWarmupJobs: number;
  maxPendingWarmupTasks: number;
  maxConcurrency: number;
  maxWarmupWorkers: number;
} {
  return {
    ...warmupState,
    activeThumbJobs,
    pendingThumbs: pendingThumbs.size,
    queuedThumbJobs: thumbQueue.length,
    queuedWarmupJobs: thumbQueue.filter((task) => task.warmup).length,
    maxPendingWarmupTasks: MAX_PENDING_WARM_THUMB_TASKS,
    maxConcurrency: MAX_THUMB_CONCURRENCY,
    maxWarmupWorkers: MAX_WARMUP_WORKERS,
  };
}

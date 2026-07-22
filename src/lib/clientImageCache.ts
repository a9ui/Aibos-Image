'use client';

type CacheKind = 'thumb' | 'display';

export class ImageRequestError extends Error {
  constructor(public readonly status: number) {
    super(`Image fetch failed: ${status}`);
    this.name = 'ImageRequestError';
  }
}

export function isImageSessionExpiredError(error: unknown): error is ImageRequestError {
  return error instanceof ImageRequestError && error.status === 410;
}

interface CacheEntry {
  objectUrl?: string;
  promise?: Promise<string>;
  abortController?: AbortController;
  consumers: number;
  lastUsed: number;
}

const MAX_ENTRIES: Record<CacheKind, number> = {
  thumb: 2400,
  display: 160,
};

const caches: Record<CacheKind, Map<string, CacheEntry>> = {
  thumb: new Map(),
  display: new Map(),
};

function trimCache(kind: CacheKind) {
  const cache = caches[kind];
  const limit = MAX_ENTRIES[kind];
  if (cache.size <= limit) return;

  const entries = Array.from(cache.entries())
    .filter(([, entry]) => entry.objectUrl && !entry.promise)
    .sort((a, b) => a[1].lastUsed - b[1].lastUsed);

  for (const [key, entry] of entries) {
    if (cache.size <= limit) break;
    if (entry.objectUrl) URL.revokeObjectURL(entry.objectUrl);
    cache.delete(key);
  }
}

export function getCachedImageUrl(cacheKey: string, kind: CacheKind) {
  const entry = caches[kind].get(cacheKey);
  if (!entry?.objectUrl) return null;
  entry.lastUsed = performance.now();
  return entry.objectUrl;
}

export function evictCachedImageUrl(cacheKey: string, kind: CacheKind) {
  const entry = caches[kind].get(cacheKey);
  entry?.abortController?.abort();
  if (entry?.objectUrl) URL.revokeObjectURL(entry.objectUrl);
  caches[kind].delete(cacheKey);
}

type CachedImageLoadHandle = {
  promise: Promise<string>;
  cancel: () => void;
};

function createResolvedHandle(objectUrl: string): CachedImageLoadHandle {
  return {
    promise: Promise.resolve(objectUrl),
    cancel: () => {},
  };
}

export function loadCancellableCachedImageUrl(cacheKey: string, requestUrl: string, kind: CacheKind): CachedImageLoadHandle {
  const cache = caches[kind];
  const existing = cache.get(cacheKey);
  if (existing?.objectUrl) {
    existing.lastUsed = performance.now();
    return createResolvedHandle(existing.objectUrl);
  }
  if (existing?.promise) {
    existing.consumers += 1;
    existing.lastUsed = performance.now();
    let cancelled = false;
    return {
      promise: existing.promise,
      cancel: () => {
        if (cancelled) return;
        cancelled = true;
        existing.consumers = Math.max(0, existing.consumers - 1);
        if (existing.consumers === 0 && !existing.objectUrl) {
          existing.abortController?.abort();
        }
      },
    };
  }

  const abortController = new AbortController();
  const entry: CacheEntry = {
    abortController,
    consumers: 1,
    lastUsed: performance.now(),
  };

  entry.promise = fetch(requestUrl, { cache: 'force-cache', signal: abortController.signal })
    .then((response) => {
      if (!response.ok) throw new ImageRequestError(response.status);
      return response.blob();
    })
    .then((blob) => {
      if (abortController.signal.aborted) {
        throw new DOMException('Image load aborted', 'AbortError');
      }
      const objectUrl = URL.createObjectURL(blob);
      entry.objectUrl = objectUrl;
      entry.promise = undefined;
      entry.abortController = undefined;
      entry.lastUsed = performance.now();
      trimCache(kind);
      return objectUrl;
    })
    .catch((error) => {
      cache.delete(cacheKey);
      throw error;
    });

  cache.set(cacheKey, entry);
  let cancelled = false;
  return {
    promise: entry.promise,
    cancel: () => {
      if (cancelled) return;
      cancelled = true;
      entry.consumers = Math.max(0, entry.consumers - 1);
      if (entry.consumers === 0 && !entry.objectUrl) {
        abortController.abort();
      }
    },
  };
}

export function loadCachedImageUrl(cacheKey: string, requestUrl: string, kind: CacheKind) {
  return loadCancellableCachedImageUrl(cacheKey, requestUrl, kind).promise;
}

export function clearClientImageCacheForTests() {
  for (const cache of Object.values(caches)) {
    for (const entry of cache.values()) {
      entry.abortController?.abort();
      if (entry.objectUrl) URL.revokeObjectURL(entry.objectUrl);
    }
    cache.clear();
  }
}

export function getClientImageCacheStatsForTests(kind: CacheKind) {
  const cache = caches[kind];
  return {
    entries: cache.size,
    pending: Array.from(cache.values()).filter((entry) => entry.promise).length,
    objectUrls: Array.from(cache.values()).filter((entry) => entry.objectUrl).length,
    consumers: Array.from(cache.values()).reduce((count, entry) => count + entry.consumers, 0),
  };
}

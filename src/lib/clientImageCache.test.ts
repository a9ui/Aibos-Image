import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearClientImageCacheForTests,
  getClientImageCacheStatsForTests,
  isImageSessionExpiredError,
  loadCachedImageUrl,
  loadCancellableCachedImageUrl,
} from './clientImageCache';

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function createResponse(body = 'image') {
  return {
    ok: true,
    status: 200,
    blob: () => Promise.resolve(new Blob([body])),
  } as Response;
}

describe('client image cache', () => {
  let createObjectUrl: ReturnType<typeof vi.fn>;
  let revokeObjectUrl: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    clearClientImageCacheForTests();
    let objectUrlSequence = 0;
    createObjectUrl = vi.fn(() => {
      objectUrlSequence += 1;
      return `blob:test-${objectUrlSequence}`;
    });
    revokeObjectUrl = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: createObjectUrl,
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: revokeObjectUrl,
    });
  });

  afterEach(() => {
    clearClientImageCacheForTests();
    vi.unstubAllGlobals();
  });

  it('aborts stale cancellable loads before object URL creation', async () => {
    const deferred = createDeferred<Response>();
    let aborts = 0;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      init?.signal?.addEventListener('abort', () => {
        aborts += 1;
        deferred.reject(new DOMException('aborted', 'AbortError'));
      });
      return deferred.promise;
    }));

    const load = loadCancellableCachedImageUrl('display-a', '/api/image/a', 'display');
    load.cancel();

    await expect(load.promise).rejects.toMatchObject({ name: 'AbortError' });
    expect(aborts).toBe(1);
    expect(createObjectUrl).not.toHaveBeenCalled();
    expect(getClientImageCacheStatsForTests('display')).toEqual({
      entries: 0,
      pending: 0,
      objectUrls: 0,
      consumers: 0,
    });
  });

  it('keeps a shared pending load alive until the last consumer cancels', async () => {
    const deferred = createDeferred<Response>();
    let aborts = 0;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      init?.signal?.addEventListener('abort', () => {
        aborts += 1;
        deferred.reject(new DOMException('aborted', 'AbortError'));
      });
      return deferred.promise;
    }));

    const first = loadCancellableCachedImageUrl('display-a', '/api/image/a', 'display');
    const second = loadCancellableCachedImageUrl('display-a', '/api/image/a', 'display');
    first.cancel();
    deferred.resolve(createResponse());

    await expect(second.promise).resolves.toBe('blob:test-1');
    expect(aborts).toBe(0);
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(getClientImageCacheStatsForTests('display')).toMatchObject({
      entries: 1,
      pending: 0,
      objectUrls: 1,
    });
  });

  it('reuses existing object URLs without another fetch', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(createResponse())));

    await expect(loadCachedImageUrl('display-a', '/api/image/a', 'display')).resolves.toBe('blob:test-1');
    await expect(loadCachedImageUrl('display-a', '/api/image/a', 'display')).resolves.toBe('blob:test-1');

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
  });

  it('preserves a 410 response as a distinguishable expired-session error', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: false,
      status: 410,
    } as Response)));

    const error = await loadCachedImageUrl('display-expired', '/api/image/expired', 'display')
      .then(() => null, (reason: unknown) => reason);

    expect(isImageSessionExpiredError(error)).toBe(true);
    expect(error).toMatchObject({ name: 'ImageRequestError', status: 410 });
    expect(getClientImageCacheStatsForTests('display')).toEqual({
      entries: 0,
      pending: 0,
      objectUrls: 0,
      consumers: 0,
    });
  });

  it('keeps only the current display load active in a rapid navigation sequence', async () => {
    const deferredResponses = Array.from({ length: 5 }, () => createDeferred<Response>());
    const fetchQueue = [...deferredResponses];
    let aborts = 0;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      const deferred = fetchQueue.shift();
      if (!deferred) throw new Error('unexpected fetch');
      init?.signal?.addEventListener('abort', () => {
        aborts += 1;
        deferred.reject(new DOMException('aborted', 'AbortError'));
      });
      return deferred.promise;
    }));

    const loads = Array.from({ length: 5 }, (_, index) =>
      loadCancellableCachedImageUrl(`display-${index}`, `/api/image/${index}`, 'display')
    );

    for (const staleLoad of loads.slice(0, 4)) {
      staleLoad.cancel();
    }
    deferredResponses[4]?.resolve(createResponse('current'));

    await Promise.all(loads.slice(0, 4).map((load) => expect(load.promise).rejects.toMatchObject({ name: 'AbortError' })));
    await expect(loads[4].promise).resolves.toBe('blob:test-1');

    expect(aborts).toBe(4);
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(getClientImageCacheStatsForTests('display')).toMatchObject({
      entries: 1,
      pending: 0,
      objectUrls: 1,
    });
  });
});

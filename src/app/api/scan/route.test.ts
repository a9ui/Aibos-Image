import { NextRequest } from 'next/server';
import { afterEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => {
  class MockScanAbortedError extends Error {
    constructor() {
      super('Scan aborted');
      this.name = 'ScanAbortedError';
    }
  }
  return {
    scanDirectory: vi.fn(),
    setIndex: vi.fn(),
    cancelThumbnailWarmup: vi.fn(),
    MockScanAbortedError,
  };
});

vi.mock('@/lib/indexer', () => ({
  scanDirectory: mocks.scanDirectory,
  setIndex: mocks.setIndex,
  ScanAbortedError: mocks.MockScanAbortedError,
  isScanAbortedError: (error: unknown) => error instanceof mocks.MockScanAbortedError,
}));

vi.mock('@/lib/thumbnailCache', () => ({
  cancelThumbnailWarmup: mocks.cancelThumbnailWarmup,
}));

import { GET } from './route';

function scanRequest(dir: string, accept?: string, signal?: AbortSignal) {
  return new NextRequest(`http://127.0.0.1/api/scan?dir=${encodeURIComponent(dir)}`, {
    headers: accept ? { accept } : undefined,
    signal,
  });
}

afterEach(() => {
  vi.clearAllMocks();
});

describe('GET /api/scan lifecycle', () => {
  it('returns the stable viewer index token in the completion event', async () => {
    mocks.scanDirectory.mockResolvedValue([]);
    mocks.setIndex.mockReturnValue('idx_folder_set');

    const response = await GET(scanRequest('C:/Pictures/A'));
    expect(response.status).toBe(200);
    const body = await response.text();

    expect(mocks.setIndex).toHaveBeenCalledWith([], 'c:\\pictures\\a');
    expect(body).toContain('"type":"complete"');
    expect(body).toContain('"indexToken":"idx_folder_set"');
  });

  it('rejects an overlapping canonical folder set and releases it after cancellation', async () => {
    mocks.scanDirectory.mockImplementation((_root: string, _progress: unknown, options: { signal?: AbortSignal }) => (
      new Promise((_resolve, reject) => {
        options.signal?.addEventListener('abort', () => reject(new mocks.MockScanAbortedError()), { once: true });
      })
    ));

    const first = await GET(scanRequest('C:/Pictures/A\nC:/Pictures/B'));
    const duplicate = await GET(scanRequest('c:/pictures/b\nC:/PICTURES/a'));

    expect(first.status).toBe(200);
    expect(duplicate.status).toBe(409);
    expect(await duplicate.json()).toMatchObject({ retryable: true });

    const eventSourceConflict = await GET(
      scanRequest('c:/pictures/b\nC:/PICTURES/a', 'text/event-stream')
    );
    expect(eventSourceConflict.status).toBe(200);
    expect(eventSourceConflict.headers.get('content-type')).toContain('text/event-stream');
    const eventSourceBody = await eventSourceConflict.text();
    expect(eventSourceBody).toContain('"type":"error"');
    expect(eventSourceBody).toContain('already running');

    await first.body?.cancel();
    await vi.waitFor(async () => {
      const retry = await GET(scanRequest('C:/Pictures/B\nC:/Pictures/A'));
      if (retry.status !== 200) throw new Error('scan reservation was not released');
      await retry.body?.cancel();
    });
  });

  it('aborts on request disconnect, publishes no partial index, and releases the folder set', async () => {
    const signals: AbortSignal[] = [];
    mocks.scanDirectory.mockImplementation((_root: string, _progress: unknown, options: { signal?: AbortSignal }) => (
      new Promise((_resolve, reject) => {
        if (!options.signal) throw new Error('Expected a scan abort signal.');
        signals.push(options.signal);
        options.signal.addEventListener('abort', () => reject(new mocks.MockScanAbortedError()), { once: true });
      })
    ));
    const requestAbort = new AbortController();
    const first = await GET(scanRequest('C:/Pictures/Cancelable', undefined, requestAbort.signal));

    requestAbort.abort();

    await vi.waitFor(() => {
      expect(signals[0]?.aborted).toBe(true);
    });
    await vi.waitFor(async () => {
      const retry = await GET(scanRequest('c:/pictures/cancelable'));
      if (retry.status !== 200) throw new Error('scan reservation was not released');
      await retry.body?.cancel();
    });
    expect(mocks.setIndex).not.toHaveBeenCalled();
    await first.body?.cancel().catch(() => {});
  });
});

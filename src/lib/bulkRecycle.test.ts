import { describe, expect, it, vi } from 'vitest';
import {
  formatBulkRecycleProgress,
  recycleImagesSequentially,
  snapshotBulkRecycleTargets,
} from './bulkRecycle';

describe('bulk Recycle Bin operations', () => {
  it('takes a stable unique snapshot without changing target order', () => {
    expect(snapshotBulkRecycleTargets(['a', 'b', 'a', '', 'c'])).toEqual(['a', 'b', 'c']);
  });

  it('runs sequentially and guards rejected or failed deletes', async () => {
    let active = 0;
    let maxActive = 0;
    const progress = vi.fn();
    const recycle = vi.fn(async (id: string) => {
      active += 1;
      maxActive = Math.max(maxActive, active);
      await Promise.resolve();
      active -= 1;
      if (id === 'throw') throw new Error('unavailable');
      return id !== 'fail';
    });

    const result = await recycleImagesSequentially(
      ['ok', 'fail', 'throw', 'after'],
      recycle,
      progress,
    );

    expect(maxActive).toBe(1);
    expect(recycle.mock.calls.map(([id]) => id)).toEqual(['ok', 'fail', 'throw', 'after']);
    expect(result).toEqual({
      total: 4,
      processed: 4,
      successCount: 2,
      failureCount: 2,
      succeededIds: ['ok', 'after'],
      failedIds: ['fail', 'throw'],
    });
    expect(progress).toHaveBeenLastCalledWith({
      total: 4,
      processed: 4,
      successCount: 2,
      failureCount: 2,
    });
  });

  it('formats full success, partial failure, and retryable total failure', () => {
    expect(formatBulkRecycleProgress({
      total: 2, processed: 2, successCount: 2, failureCount: 0,
    })).toBe('Moved 2 image(s) to Recycle Bin.');
    expect(formatBulkRecycleProgress({
      total: 3, processed: 3, successCount: 2, failureCount: 1,
    })).toBe('Moved 2/3 image(s) to Recycle Bin. 1 failed and remain selected for retry.');
    expect(formatBulkRecycleProgress({
      total: 2, processed: 2, successCount: 0, failureCount: 2,
    })).toBe('Could not move 2 image(s) to Recycle Bin. They remain selected for retry.');
  });
});

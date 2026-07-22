import { describe, expect, it } from 'vitest';
import {
  DEFAULT_THUMB_SIZE,
  MAX_THUMB_SIZE,
  MIN_THUMB_SIZE,
  clampThumbnailSize,
  getThumbnailGridCellWidth,
  getThumbnailGridColumns,
} from './thumbnailSizing';

describe('thumbnail sizing', () => {
  it('supports a denser 20px minimum and clamps persisted values safely', () => {
    expect(MIN_THUMB_SIZE).toBe(20);
    expect(clampThumbnailSize(-1)).toBe(MIN_THUMB_SIZE);
    expect(clampThumbnailSize(Number.NaN)).toBe(DEFAULT_THUMB_SIZE);
    expect(clampThumbnailSize(9_999)).toBe(MAX_THUMB_SIZE);
  });

  it.each([1280, 1920, 2560, 3840])(
    'makes the maximum endpoint exactly one column at %ipx',
    (availableWidth) => {
      expect(getThumbnailGridColumns({
        availableWidth,
        thumbSize: MAX_THUMB_SIZE,
        gap: 16,
      })).toBe(1);
    },
  );

  it('renders more columns at the new minimum without overflowing cell width', () => {
    const previousColumns = getThumbnailGridColumns({
      availableWidth: 1200,
      thumbSize: 40,
      gap: 16,
    });
    const denseColumns = getThumbnailGridColumns({
      availableWidth: 1200,
      thumbSize: MIN_THUMB_SIZE,
      gap: 16,
    });
    expect(denseColumns).toBeGreaterThan(previousColumns);
    expect(getThumbnailGridCellWidth({
      availableWidth: 1200,
      columns: denseColumns,
      gap: 16,
    })).toBeGreaterThanOrEqual(MIN_THUMB_SIZE);
  });
});

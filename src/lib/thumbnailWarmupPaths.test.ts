import { describe, expect, it } from 'vitest';
import { collectLimitedThumbnailWarmupPaths } from './thumbnailWarmupPaths';

describe('collectLimitedThumbnailWarmupPaths', () => {
  it('stops scanning once enough accepted paths are collected', () => {
    const images = Array.from({ length: 1000 }, (_, index) => ({ id: `image-${index}.png` }));
    let inspected = 0;

    const paths = collectLimitedThumbnailWarmupPaths(images, (imagePath) => {
      inspected += 1;
      return imagePath.endsWith('.png');
    }, 5);

    expect(paths).toEqual([
      'image-0.png',
      'image-1.png',
      'image-2.png',
      'image-3.png',
      'image-4.png',
    ]);
    expect(inspected).toBe(5);
  });

  it('continues past rejected paths without scanning the full list after the limit is met', () => {
    const images = Array.from({ length: 1000 }, (_, index) => ({ id: `image-${index}.png` }));
    let inspected = 0;

    const paths = collectLimitedThumbnailWarmupPaths(images, (imagePath) => {
      inspected += 1;
      const index = Number(imagePath.match(/\d+/)?.[0] ?? -1);
      return index % 10 === 0;
    }, 3);

    expect(paths).toEqual(['image-0.png', 'image-10.png', 'image-20.png']);
    expect(inspected).toBe(21);
  });

  it('returns an empty path list for zero or negative limits', () => {
    const images = [{ id: 'a.png' }];
    let inspected = 0;

    const paths = collectLimitedThumbnailWarmupPaths(images, () => {
      inspected += 1;
      return true;
    }, 0);

    expect(paths).toEqual([]);
    expect(inspected).toBe(0);
  });
});

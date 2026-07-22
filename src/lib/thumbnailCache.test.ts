import fs from 'fs';
import os from 'os';
import path from 'path';
import sharp from 'sharp';
import { describe, expect, it } from 'vitest';
import {
  MAX_PENDING_WARM_THUMB_TASKS,
  compareThumbnailQueueEntries,
  ensureDisplayImage,
  ensureThumbnail,
  getDisplayPath,
  getThumbnailPath,
  selectSupersededWarmupQueueEntries,
} from './thumbnailCache';

describe('thumbnail queue policy', () => {
  it('serves direct image responses before speculative work at the same priority', () => {
    const direct = { priority: 0, sequence: 1, warmup: false };
    const warmup = { priority: 0, sequence: 2, warmup: true };

    expect(compareThumbnailQueueEntries(direct, warmup)).toBeLessThan(0);
    expect(compareThumbnailQueueEntries(warmup, direct)).toBeGreaterThan(0);
  });

  it('prefers the newest moving-viewport warmup but keeps background warmup FIFO', () => {
    const oldVisible = { priority: 1, sequence: 10, warmup: true };
    const newVisible = { priority: 1, sequence: 20, warmup: true };
    const oldBackground = { priority: 3, sequence: 10, warmup: true };
    const newBackground = { priority: 3, sequence: 20, warmup: true };

    expect(compareThumbnailQueueEntries(newVisible, oldVisible)).toBeLessThan(0);
    expect(compareThumbnailQueueEntries(oldVisible, newVisible)).toBeGreaterThan(0);
    expect(compareThumbnailQueueEntries(oldBackground, newBackground)).toBeLessThan(0);
    expect(compareThumbnailQueueEntries(newBackground, oldBackground)).toBeGreaterThan(0);
  });

  it('keeps speculative pending work under an explicit finite ceiling', () => {
    expect(MAX_PENDING_WARM_THUMB_TASKS).toBe(1200);

    const oldVisible = { priority: 1, sequence: 10, warmup: true };
    const newVisible = { priority: 1, sequence: 20, warmup: true };
    const oldBackground = { priority: 3, sequence: 1, warmup: true };
    const newBackground = { priority: 3, sequence: 2, warmup: true };
    const direct = { priority: 3, sequence: 0, warmup: false };
    expect(selectSupersededWarmupQueueEntries([
      newVisible,
      direct,
      newBackground,
      oldVisible,
      oldBackground,
    ], 2)).toEqual([oldBackground, newBackground]);
  });
});

describe('thumbnail cache', () => {
  it('computes versioned cache paths without reading the source file', async () => {
    const missingSourcePath = path.join(os.tmpdir(), `pvu-missing-${Date.now()}.png`);

    await expect(getThumbnailPath(missingSourcePath, '1234.5')).resolves.toContain('.webp');
    await expect(getDisplayPath(missingSourcePath, '1234.5')).resolves.toContain('.webp');
    await expect(getThumbnailPath(missingSourcePath)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('keeps versioned cache paths compatible with stat-based paths', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-version-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 12,
        height: 12,
        channels: 4,
        background: '#446622ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const stat = fs.statSync(sourcePath);

    await expect(getThumbnailPath(sourcePath, String(stat.mtimeMs))).resolves.toBe(await getThumbnailPath(sourcePath));
    await expect(getDisplayPath(sourcePath, String(stat.mtimeMs))).resolves.toBe(await getDisplayPath(sourcePath));
  });

  it('does not write current thumbnails under a stale versioned cache key', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-stale-version-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 12,
        height: 12,
        channels: 4,
        background: '#226644ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const stalePath = await getThumbnailPath(sourcePath, '1');
    const result = await ensureThumbnail(sourcePath, 0, '1');

    expect(result.versionMatched).toBe(false);
    expect(result.thumbPath).not.toBe(stalePath);
    expect(fs.existsSync(stalePath)).toBe(false);
    await expect(sharp(result.thumbPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });

  it('replaces a broken existing thumbnail cache file', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-cache-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 12,
        height: 12,
        channels: 4,
        background: '#224466ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const thumbPath = await getThumbnailPath(sourcePath);
    fs.mkdirSync(path.dirname(thumbPath), { recursive: true });
    fs.writeFileSync(thumbPath, '');

    await ensureThumbnail(sourcePath, 0);

    const stat = fs.statSync(thumbPath);
    expect(stat.size).toBeGreaterThan(0);
    await expect(sharp(thumbPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });

  it('replaces a non-webp existing thumbnail cache file without deleting healthy caches', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-cache-kind-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 12,
        height: 12,
        channels: 4,
        background: '#664422ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const thumbPath = await getThumbnailPath(sourcePath);
    fs.mkdirSync(path.dirname(thumbPath), { recursive: true });
    fs.writeFileSync(thumbPath, 'this is not a webp cache file');

    await ensureThumbnail(sourcePath, 0);

    await expect(sharp(thumbPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });

  it('replaces a cache file with only a fake webp container header', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-cache-fake-webp-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 12,
        height: 12,
        channels: 4,
        background: '#662244ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const thumbPath = await getThumbnailPath(sourcePath);
    fs.mkdirSync(path.dirname(thumbPath), { recursive: true });
    const fake = Buffer.alloc(32);
    fake.write('RIFF', 0, 'ascii');
    fake.writeUInt32LE(24, 4);
    fake.write('WEBP', 8, 'ascii');
    fake.write('NOPE', 12, 'ascii');
    fs.writeFileSync(thumbPath, fake);

    await ensureThumbnail(sourcePath, 0);

    await expect(sharp(thumbPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });

  it('coalesces concurrent thumbnail generation into one valid cache file', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-thumb-concurrent-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 64,
        height: 64,
        channels: 4,
        background: '#aa6633ff',
      },
    })
      .png()
      .toFile(sourcePath);

    const results = await Promise.all(Array.from({ length: 6 }, () => ensureThumbnail(sourcePath, 0)));
    const thumbPath = await getThumbnailPath(sourcePath);

    expect(new Set(results.map((result) => result.thumbPath))).toEqual(new Set([thumbPath]));
    expect(fs.existsSync(thumbPath)).toBe(true);
    await expect(sharp(thumbPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });

  it('coalesces concurrent display-image generation into one valid cache file', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-display-concurrent-'));
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 96,
        height: 72,
        channels: 4,
        background: '#3366aaff',
      },
    })
      .png()
      .toFile(sourcePath);

    const results = await Promise.all(Array.from({ length: 6 }, () => ensureDisplayImage(sourcePath, 0)));
    const displayPath = await getDisplayPath(sourcePath);

    expect(new Set(results.map((result) => result.displayPath))).toEqual(new Set([displayPath]));
    expect(fs.existsSync(displayPath)).toBe(true);
    await expect(sharp(displayPath, { failOn: 'none' }).metadata()).resolves.toMatchObject({
      format: 'webp',
    });
  });
});

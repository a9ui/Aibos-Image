import fs from 'fs';
import os from 'os';
import path from 'path';
import crypto from 'crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ImageFile } from './types';
import { getFolderBuckets, getIndex, getTags, ScanAbortedError, scanDirectory, searchIndex, setIndex } from './indexer';

const ONE_BY_ONE_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
  'base64'
);

const createdRoots: string[] = [];
const cacheFiles: string[] = [];

function cacheHash(dirPath: string) {
  return crypto.createHash('md5').update(path.resolve(dirPath).toLowerCase()).digest('hex');
}

function rememberCacheFiles(dirPath: string) {
  const hash = cacheHash(dirPath);
  cacheFiles.push(
    path.join(process.cwd(), '.cache', `index_${hash}.json`),
    path.join(process.cwd(), '.cache', `folders_${hash}.json`)
  );
}

function writePng(filePath: string) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, ONE_BY_ONE_PNG);
}

function writeImagePlaceholder(filePath: string) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, Buffer.from('placeholder image bytes'));
}

function makeIndexedImage(filename: string, index: number): ImageFile {
  const absolutePath = path.join('C:\\PhotoViewerTest', filename);
  const encodedPath = encodeURIComponent(absolutePath);
  return {
    id: absolutePath,
    filename,
    absolutePath,
    fileUrl: `/api/image?path=${encodedPath}&thumb=true`,
    displayUrl: `/api/image?path=${encodedPath}&display=true`,
    fullUrl: `/api/image?path=${encodedPath}`,
    metadata: null,
    createdAt: index,
    mtime: index,
  };
}

afterEach(() => {
  vi.useRealTimers();
  setIndex([]);
  for (const filePath of cacheFiles.splice(0)) {
    fs.rmSync(filePath, { force: true });
  }
  for (const root of createdRoots.splice(0)) {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

describe('scanDirectory', () => {
  it('keeps independent bounded index snapshots for separate viewer folder sets', () => {
    const alpha = makeIndexedImage('set-a\\alpha.png', 1);
    alpha.metadata = { prompt: 'alpha-tag, shared-tag', negativePrompt: '', settings: {} };
    const beta = makeIndexedImage('set-b\\beta.png', 2);
    beta.metadata = { prompt: 'beta-tag, shared-tag', negativePrompt: '', settings: {} };

    const tokenA = setIndex([alpha], 'c:\\PhotoViewerTest\\set-a');
    const tokenB = setIndex([beta], 'c:\\PhotoViewerTest\\set-b');
    expect(tokenA).toMatch(/^idx_/);
    expect(tokenB).toMatch(/^idx_/);
    expect(tokenA).not.toBe(tokenB);

    expect(searchIndex('', 0, 20, 'name', undefined, undefined, undefined, undefined, undefined, undefined, tokenA)
      .results.map((image) => image.filename)).toEqual(['set-a\\alpha.png']);
    expect(searchIndex('', 0, 20, 'name', undefined, undefined, undefined, undefined, undefined, undefined, tokenB)
      .results.map((image) => image.filename)).toEqual(['set-b\\beta.png']);
    expect(getTags(20, tokenA)).toEqual([{ tag: 'alpha-tag', count: 1 }, { tag: 'shared-tag', count: 1 }]);
    expect(getTags(20, tokenB)).toEqual([{ tag: 'beta-tag', count: 1 }, { tag: 'shared-tag', count: 1 }]);
    expect(getFolderBuckets('C:\\PhotoViewerTest', 20, tokenA).map((folder) => folder.key)).toEqual(['set-a']);
    expect(getFolderBuckets('C:\\PhotoViewerTest', 20, tokenB).map((folder) => folder.key)).toEqual(['set-b']);

    // Legacy tokenless callers retain the latest active-index behaviour.
    expect(searchIndex('', 0, 20, 'name').results.map((image) => image.filename)).toEqual(['set-b\\beta.png']);
    expect(setIndex([alpha], 'c:\\PhotoViewerTest\\set-a')).toBe(tokenA);
    expect(getIndex(tokenA).map((image) => image.filename)).toEqual(['set-a\\alpha.png']);
  });

  it('expires idle snapshots and bounds the session map', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-18T00:00:00Z'));
    const firstToken = setIndex([makeIndexedImage('first.png', 1)], 'c:\\session-0');
    for (let index = 1; index <= 8; index += 1) {
      vi.advanceTimersByTime(1);
      setIndex([makeIndexedImage(`image-${index}.png`, index)], `c:\\session-${index}`);
    }
    expect(getIndex(firstToken).map((image) => image.filename)).toEqual([]);

    const ttlToken = setIndex([makeIndexedImage('ttl.png', 1)], 'c:\\ttl');
    vi.advanceTimersByTime(30 * 60 * 1000 + 1);
    expect(getIndex(ttlToken).map((image) => image.filename)).toEqual([]);
  });

  it('detects new PNGs added inside nested folders after folder-signature caching', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const nested = path.join(root, '2026-05-18', 'nested');
    writePng(path.join(nested, 'first.png'));

    expect(await scanDirectory(root)).toHaveLength(1);
    expect(await scanDirectory(root)).toHaveLength(1);

    writePng(path.join(nested, 'second.png'));
    const future = new Date(Date.now() + 2000);
    fs.utimesSync(nested, future, future);

    const afterNestedAdd = await scanDirectory(root);
    expect(afterNestedAdd.map((image) => image.filename).sort()).toEqual(['first.png', 'second.png']);
  });

  it('can force a full verification scan for same-name overwrites', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const imageDir = path.join(root, '2026-05-18');
    const imagePath = path.join(imageDir, 'overwrite.png');
    const inactiveFolderTime = new Date(Date.now() - 72 * 60 * 60 * 1000);
    writePng(imagePath);
    fs.utimesSync(imageDir, inactiveFolderTime, inactiveFolderTime);

    const initial = await scanDirectory(root);
    expect(initial).toHaveLength(1);
    expect(await scanDirectory(root)).toHaveLength(1);

    const future = new Date(Date.now() + 3000);
    fs.utimesSync(imagePath, future, future);
    fs.utimesSync(imageDir, inactiveFolderTime, inactiveFolderTime);

    const fastRefresh = await scanDirectory(root);
    expect(fastRefresh[0].mtime).toBe(initial[0].mtime);

    const verifiedRefresh = await scanDirectory(root, undefined, { forceFull: true });
    expect(verifiedRefresh[0].mtime).toBeGreaterThan(initial[0].mtime);
  });

  it('abandons a scan without publishing a partial cache snapshot', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const imagePath = path.join(root, 'abortable.png');
    writePng(imagePath);
    const initial = await scanDirectory(root);
    const cachePath = path.join(process.cwd(), '.cache', `index_${cacheHash(root)}.json`);
    const before = fs.readFileSync(cachePath, 'utf-8');

    const future = new Date(Date.now() + 3000);
    fs.utimesSync(imagePath, future, future);
    const abortController = new AbortController();

    await expect(scanDirectory(root, (_processed, _total, _newFiles, status) => {
      if (status?.stage === 'scanning') abortController.abort();
    }, { forceFull: true, signal: abortController.signal })).rejects.toBeInstanceOf(ScanAbortedError);

    expect(fs.readFileSync(cachePath, 'utf-8')).toBe(before);
    const recovered = await scanDirectory(root, undefined, { forceFull: true });
    expect(recovered[0].mtime).toBeGreaterThan(initial[0].mtime);
  });

  it('indexes newly added non-PNG image files', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    writePng(path.join(root, 'initial.png'));
    expect(await scanDirectory(root)).toHaveLength(1);

    writeImagePlaceholder(path.join(root, 'new-photo.jpg'));
    writeImagePlaceholder(path.join(root, 'new-render.webp'));
    const future = new Date(Date.now() + 2000);
    fs.utimesSync(root, future, future);

    const afterAdd = await scanDirectory(root);
    expect(afterAdd.map((image) => image.filename).sort()).toEqual([
      'initial.png',
      'new-photo.jpg',
      'new-render.webp',
    ]);
  });

  it('scans newly added top-level folders even when their folder mtime predates the last scan', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    writePng(path.join(root, 'existing', 'first.png'));
    expect(await scanDirectory(root)).toHaveLength(1);
    expect(await scanDirectory(root)).toHaveLength(1);

    const imported = path.join(root, 'imported-old');
    writePng(path.join(imported, 'second.png'));
    const past = new Date(Date.now() - 60_000);
    fs.utimesSync(imported, past, past);

    const afterImport = await scanDirectory(root);
    expect(afterImport.map((image) => image.filename).sort()).toEqual(['first.png', 'second.png']);
  });

  it('does not let a signature-only empty folder skip later uncached child images', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const olderFolder = path.join(root, '2026-05-17');
    fs.mkdirSync(olderFolder, { recursive: true });
    const stablePast = new Date(Date.now() - 72 * 60 * 60 * 1000);
    fs.utimesSync(olderFolder, stablePast, stablePast);

    expect(await scanDirectory(root)).toHaveLength(0);

    writePng(path.join(olderFolder, 'late-child.png'));
    fs.utimesSync(olderFolder, stablePast, stablePast);

    const afterChildAdd = await scanDirectory(root);
    expect(afterChildAdd.map((image) => image.filename)).toEqual(['late-child.png']);
  });

  it('does not let a signature-only nested folder skip later uncached deeper images', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const nested = path.join(root, '2026-05-17', 'child');
    fs.mkdirSync(nested, { recursive: true });
    const stablePast = new Date(Date.now() - 72 * 60 * 60 * 1000);
    fs.utimesSync(nested, stablePast, stablePast);
    fs.utimesSync(path.dirname(nested), stablePast, stablePast);

    expect(await scanDirectory(root)).toHaveLength(0);

    writePng(path.join(nested, 'deep.png'));
    fs.utimesSync(nested, stablePast, stablePast);
    fs.utimesSync(path.dirname(nested), stablePast, stablePast);

    const afterDeepAdd = await scanDirectory(root);
    expect(afterDeepAdd.map((image) => image.filename)).toEqual(['deep.png']);
  });

  it('rescans recently active existing folders even when the folder signature looks unchanged', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    const today = path.join(root, '2026-05-19');
    const stableTimestamp = new Date();
    writePng(path.join(today, 'first.png'));
    fs.utimesSync(today, stableTimestamp, stableTimestamp);

    expect(await scanDirectory(root)).toHaveLength(1);
    expect(await scanDirectory(root)).toHaveLength(1);

    writePng(path.join(today, 'second.png'));
    fs.utimesSync(today, stableTimestamp, stableTimestamp);

    const afterAdd = await scanDirectory(root);
    expect(afterAdd.map((image) => image.filename).sort()).toEqual(['first.png', 'second.png']);
  });

  it('treats an unreadable scan root as a failed scan instead of a zero-result success', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-'));
    createdRoots.push(root);
    rememberCacheFiles(root);

    writePng(path.join(root, '2026-05-17', 'old.png'));
    expect(await scanDirectory(root)).toHaveLength(1);

    const cachePath = path.join(process.cwd(), '.cache', `index_${cacheHash(root)}.json`);
    const before = fs.readFileSync(cachePath, 'utf-8');

    fs.renameSync(root, `${root}-offline`);
    createdRoots.push(`${root}-offline`);

    await expect(scanDirectory(root)).rejects.toThrow();
    expect(fs.readFileSync(cachePath, 'utf-8')).toBe(before);
  });

  it('filters search and folder buckets across newline-separated folder sets', async () => {
    const rootA = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-a-'));
    const rootB = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-b-'));
    const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'photoviewer-indexer-out-'));
    createdRoots.push(rootA, rootB, outside);
    rememberCacheFiles(rootA);
    rememberCacheFiles(rootB);
    rememberCacheFiles(outside);

    writePng(path.join(rootA, 'day-a', 'a.png'));
    writePng(path.join(rootB, 'day-b', 'b.png'));
    writePng(path.join(outside, 'day-out', 'out.png'));

    const images = [
      ...(await scanDirectory(rootA)),
      ...(await scanDirectory(rootB)),
      ...(await scanDirectory(outside)),
    ];
    setIndex(images);

    const dirSet = `${rootA}\n${rootB}`;
    const result = searchIndex('', 0, 20, 'name', undefined, undefined, undefined, dirSet);
    expect(result.total).toBe(2);
    expect(result.results.map((image) => image.filename).sort()).toEqual(['a.png', 'b.png']);

    const folders = getFolderBuckets(dirSet);
    expect(folders.map((folder) => folder.key).sort()).toEqual(['0:day-a', '1:day-b']);
  });

  it('keeps random ordering stable for the same seed and changes it for another seed', () => {
    setIndex([
      makeIndexedImage('alpha.png', 1),
      makeIndexedImage('beta.png', 2),
      makeIndexedImage('gamma.png', 3),
      makeIndexedImage('delta.png', 4),
      makeIndexedImage('epsilon.png', 5),
      makeIndexedImage('zeta.png', 6),
    ]);

    const first = searchIndex('', 0, 20, 'random', undefined, undefined, undefined, undefined, undefined, 'seed-a')
      .results
      .map((image) => image.filename);
    const repeat = searchIndex('', 0, 20, 'random', undefined, undefined, undefined, undefined, undefined, 'seed-a')
      .results
      .map((image) => image.filename);
    const reshuffled = searchIndex('', 0, 20, 'random', undefined, undefined, undefined, undefined, undefined, 'seed-b')
      .results
      .map((image) => image.filename);

    expect(repeat).toEqual(first);
    expect(reshuffled).not.toEqual(first);
    expect([...first].sort()).toEqual(['alpha.png', 'beta.png', 'delta.png', 'epsilon.png', 'gamma.png', 'zeta.png']);
    expect([...reshuffled].sort()).toEqual(['alpha.png', 'beta.png', 'delta.png', 'epsilon.png', 'gamma.png', 'zeta.png']);
  });

  it('clamps invalid search pagination inputs', () => {
    setIndex([
      makeIndexedImage('alpha.png', 1),
      makeIndexedImage('beta.png', 2),
      makeIndexedImage('gamma.png', 3),
    ]);

    const invalid = searchIndex('', Number.NaN, Number.NaN);
    expect(invalid.page).toBe(0);
    expect(invalid.totalPages).toBe(1);
    expect(invalid.results.map((image) => image.filename)).toEqual(['gamma.png', 'beta.png', 'alpha.png']);

    const negative = searchIndex('', -10, -2);
    expect(negative.page).toBe(0);
    expect(negative.results).toHaveLength(1);
    expect(negative.results[0].filename).toBe('gamma.png');
  });
});

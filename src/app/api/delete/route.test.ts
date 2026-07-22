import { NextRequest } from 'next/server';

import { describe, expect, it, vi } from 'vitest';

import {
  canonicalPathKey,
  createDeleteHandler,
  isPathInsideDirectory,
  type DeleteRouteDependencies,
} from './deleteHandler';

const PROJECT_ROOT = 'C:\\repo\\PhotoViewer';
const INDEXED_PATH = 'D:\\Images\\Target.PNG';
const THUMB_PATH = 'C:\\cache\\thumb.webp';
const DISPLAY_PATH = 'C:\\cache\\display.webp';

function deleteRequest(filePath?: string, headers?: Record<string, string>) {
  const url = new URL('http://127.0.0.1/api/delete');
  if (filePath !== undefined) url.searchParams.set('path', filePath);
  return new NextRequest(url, { method: 'DELETE', headers });
}

function createDependencies(
  overrides: Partial<DeleteRouteDependencies> = {}
): DeleteRouteDependencies {
  return {
    platform: 'win32',
    projectRoot: vi.fn(() => PROJECT_ROOT),
    getIndexedPaths: vi.fn(() => [INDEXED_PATH]),
    exists: vi.fn(() => true),
    realPath: vi.fn((filePath) => filePath),
    isSupportedImagePath: vi.fn(() => true),
    getDerivedPaths: vi.fn(async () => [THUMB_PATH, DISPLAY_PATH]),
    recycleFile: vi.fn(async () => undefined),
    removeFromIndex: vi.fn(() => undefined),
    removeDerivedImages: vi.fn(async () => undefined),
    ...overrides,
  };
}

describe('delete route safety', () => {
  it('rejects a cross-origin request before consulting the index or recycle backend', async () => {
    const dependencies = createDependencies();

    const response = await createDeleteHandler(dependencies)(
      deleteRequest(INDEXED_PATH, {
        host: '127.0.0.1',
        origin: 'https://evil.example',
        'sec-fetch-site': 'cross-site',
      }),
    );

    expect(response.status).toBe(403);
    expect(dependencies.getIndexedPaths).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('returns 400 when path is missing without consulting the index or filesystem', async () => {
    const dependencies = createDependencies();

    const response = await createDeleteHandler(dependencies)(deleteRequest());

    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ error: 'Missing path' });
    expect(dependencies.getIndexedPaths).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('returns 403 for the project root or any lexical child before index lookup', async () => {
    const dependencies = createDependencies();
    const handler = createDeleteHandler(dependencies);

    const rootResponse = await handler(deleteRequest(PROJECT_ROOT));
    const childResponse = await handler(deleteRequest(`${PROJECT_ROOT}\\src\\image.png`));

    expect(rootResponse.status).toBe(403);
    expect(childResponse.status).toBe(403);
    expect(dependencies.getIndexedPaths).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('returns 403 for a path outside the active index', async () => {
    const dependencies = createDependencies({
      getIndexedPaths: vi.fn(() => ['D:\\Images\\Other.png']),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({
      error: 'Can only delete images from the active index',
    });
    expect(dependencies.exists).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('returns 404 when an indexed image no longer exists', async () => {
    const dependencies = createDependencies({ exists: vi.fn(() => false) });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(404);
    expect(dependencies.isSupportedImagePath).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('returns 415 for an existing indexed path with an unsupported format', async () => {
    const dependencies = createDependencies({
      isSupportedImagePath: vi.fn(() => false),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(415);
    expect(dependencies.realPath).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('blocks a canonical filesystem alias that resolves inside the project root', async () => {
    const dependencies = createDependencies({
      realPath: vi.fn((filePath) => (
        canonicalPathKey(filePath, 'win32') === canonicalPathKey(INDEXED_PATH, 'win32')
          ? `${PROJECT_ROOT}\\private.png`
          : filePath
      )),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(403);
    expect(dependencies.getDerivedPaths).not.toHaveBeenCalled();
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
  });

  it('blocks a reparse-point swap into the project root after cache preparation', async () => {
    let derivedPathsPrepared = false;
    let targetResolutionCount = 0;
    const dependencies = createDependencies({
      realPath: vi.fn((filePath) => {
        if (canonicalPathKey(filePath, 'win32') !== canonicalPathKey(INDEXED_PATH, 'win32')) {
          return filePath;
        }

        targetResolutionCount += 1;
        if (targetResolutionCount === 1) return 'D:\\resolved-images\\target.png';
        expect(derivedPathsPrepared).toBe(true);
        return `${PROJECT_ROOT}\\src\\swapped-target.png`;
      }),
      getDerivedPaths: vi.fn(async () => {
        derivedPathsPrepared = true;
        return [THUMB_PATH, DISPLAY_PATH];
      }),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({
      error: 'Cannot delete files inside the project directory',
    });
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
    expect(dependencies.removeFromIndex).not.toHaveBeenCalled();
    expect(dependencies.removeDerivedImages).not.toHaveBeenCalled();
  });

  it('blocks a reparse-point swap to a different external source before recycle', async () => {
    let targetResolutionCount = 0;
    const dependencies = createDependencies({
      realPath: vi.fn((filePath) => {
        if (canonicalPathKey(filePath, 'win32') !== canonicalPathKey(INDEXED_PATH, 'win32')) {
          return filePath;
        }

        targetResolutionCount += 1;
        return targetResolutionCount === 1
          ? 'D:\\resolved-images\\target.png'
          : 'E:\\other-images\\replacement.png';
      }),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(409);
    expect(await response.json()).toEqual({
      error: 'Delete target changed before recycle operation',
    });
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
    expect(dependencies.removeFromIndex).not.toHaveBeenCalled();
    expect(dependencies.removeDerivedImages).not.toHaveBeenCalled();
  });

  it('recycles first, then removes the exact indexed spelling and derived cache', async () => {
    const order: string[] = [];
    const dependencies = createDependencies({
      getDerivedPaths: vi.fn(async () => {
        order.push('derive');
        return [THUMB_PATH, DISPLAY_PATH];
      }),
      recycleFile: vi.fn(async () => {
        order.push('recycle');
      }),
      removeFromIndex: vi.fn(() => {
        order.push('index');
      }),
      removeDerivedImages: vi.fn(async () => {
        order.push('derived-cleanup');
      }),
    });

    // A differently cased request must still match the Windows index entry.
    const response = await createDeleteHandler(dependencies)(
      deleteRequest('d:\\images\\target.png')
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ success: true, deletedTo: 'recycle-bin' });
    expect(order).toEqual(['derive', 'recycle', 'index', 'derived-cleanup']);
    expect(dependencies.recycleFile).toHaveBeenCalledWith(INDEXED_PATH);
    expect(dependencies.removeFromIndex).toHaveBeenCalledWith(INDEXED_PATH);
    expect(dependencies.removeDerivedImages).toHaveBeenCalledWith([
      THUMB_PATH,
      DISPLAY_PATH,
    ]);
  });

  it('returns 500 and leaves index and derived catalog untouched on recycle failure', async () => {
    const dependencies = createDependencies({
      recycleFile: vi.fn(async () => {
        throw new Error('Recycle Bin unavailable');
      }),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(500);
    expect(await response.json()).toEqual({ error: 'Error: Recycle Bin unavailable' });
    expect(dependencies.removeFromIndex).not.toHaveBeenCalled();
    expect(dependencies.removeDerivedImages).not.toHaveBeenCalled();
  });

  it('has no hard-delete fallback when the Recycle Bin operation fails', async () => {
    const sourceDeletionAttempts: string[] = [];
    const dependencies = createDependencies({
      recycleFile: vi.fn(async (filePath) => {
        sourceDeletionAttempts.push(`recycle:${filePath}`);
        throw new Error('rejected');
      }),
    });

    await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(sourceDeletionAttempts).toEqual([`recycle:${INDEXED_PATH}`]);
    expect(dependencies.removeFromIndex).not.toHaveBeenCalled();
    expect(dependencies.removeDerivedImages).not.toHaveBeenCalled();
  });

  it('keeps a successful response when post-recycle derived cleanup fails', async () => {
    const dependencies = createDependencies({
      removeDerivedImages: vi.fn(async () => {
        throw new Error('cache file locked');
      }),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(200);
    expect(dependencies.recycleFile).toHaveBeenCalledOnce();
    expect(dependencies.removeFromIndex).toHaveBeenCalledOnce();
    expect(dependencies.removeDerivedImages).toHaveBeenCalledOnce();
  });

  it('does not recycle when derived path preparation fails', async () => {
    const dependencies = createDependencies({
      getDerivedPaths: vi.fn(async () => {
        throw new Error('stat failed');
      }),
    });

    const response = await createDeleteHandler(dependencies)(deleteRequest(INDEXED_PATH));

    expect(response.status).toBe(500);
    expect(dependencies.recycleFile).not.toHaveBeenCalled();
    expect(dependencies.removeFromIndex).not.toHaveBeenCalled();
  });
});

describe('canonical path safety semantics', () => {
  it('normalizes Windows case and dot segments but keeps POSIX case-sensitive', () => {
    expect(canonicalPathKey('C:\\Images\\sub\\..\\Photo.PNG', 'win32')).toBe(
      canonicalPathKey('c:\\images\\photo.png', 'win32')
    );
    expect(canonicalPathKey('/images/Photo.PNG', 'linux')).not.toBe(
      canonicalPathKey('/images/photo.png', 'linux')
    );
  });

  it('treats Windows casing as identical without accepting sibling prefixes', () => {
    expect(isPathInsideDirectory('C:\\Repo', 'c:\\repo\\src\\image.png', 'win32')).toBe(true);
    expect(isPathInsideDirectory('C:\\Repo', 'C:\\Repo-backup\\image.png', 'win32')).toBe(false);
    expect(isPathInsideDirectory('C:\\Repo', 'D:\\Repo\\image.png', 'win32')).toBe(false);
  });
});

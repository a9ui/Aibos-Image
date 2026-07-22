import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { buildAlbumSource } from './albumSource';
import { mutateAlbums, readAlbums } from './albums';
import { clearIndexSessionsForTests, getIndex, hasIndexSession, setIndex } from './indexer';
import type { ImageFile } from './types';

let root = '';
let target = '';

function image(imagePath: string): ImageFile {
  return {
    id: imagePath,
    filename: path.basename(imagePath),
    absolutePath: imagePath,
    fileUrl: `/api/image?path=${encodeURIComponent(imagePath)}&thumb=true`,
    displayUrl: `/api/image?path=${encodeURIComponent(imagePath)}&display=true`,
    fullUrl: `/api/image?path=${encodeURIComponent(imagePath)}`,
    metadata: null,
    createdAt: 1,
    mtime: 1,
  };
}

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-album-source-'));
  target = path.join(root, 'albums.json');
  clearIndexSessionsForTests();
});

afterEach(async () => {
  clearIndexSessionsForTests();
  await fs.rm(root, { recursive: true, force: true });
});

describe('Album source session', () => {
  it('classifies current, outside, and missing members without replacing the catalog or fallback index', async () => {
    const currentPath = path.join(root, 'catalog', 'current.jpg');
    const outsidePath = path.join(root, 'outside', 'outside.png');
    const missingPath = path.join(root, 'missing.webp');
    await fs.mkdir(path.dirname(currentPath), { recursive: true });
    await fs.mkdir(path.dirname(outsidePath), { recursive: true });
    await fs.writeFile(currentPath, 'current');
    await fs.writeFile(outsidePath, 'outside');

    const catalogImage = image(currentPath);
    const catalogToken = setIndex([catalogImage], 'catalog-v1');
    expect(catalogToken).toBeTruthy();

    await mutateAlbums(target, { action: 'create', name: 'Mixed', albumId: 'mixed' });
    await mutateAlbums(target, {
      action: 'add',
      albumId: 'mixed',
      paths: [currentPath, outsidePath, missingPath],
    });
    const document = await readAlbums(target);
    if (!document.ok) throw new Error(document.error);

    const source = await buildAlbumSource(document.document, 'mixed', catalogToken);
    expect(source).not.toBeNull();
    expect(source?.members.map((member) => member.availability)).toEqual(['current', 'outside', 'missing']);
    expect(source?.images.map((entry) => entry.id)).toEqual([currentPath, outsidePath]);
    expect(source?.images.every((entry) => entry.fullUrl.includes(`indexToken=${encodeURIComponent(source.sourceToken)}`))).toBe(true);
    expect(getIndex(source!.sourceToken).map((entry) => entry.id)).toEqual([currentPath, outsidePath]);

    expect(hasIndexSession(catalogToken)).toBe(true);
    expect(getIndex(catalogToken)).toEqual([catalogImage]);
    expect(getIndex()).toEqual([catalogImage]);
  });

  it('reports an expired catalog token conservatively while keeping existing members available', async () => {
    const outsidePath = path.join(root, 'existing.gif');
    await fs.writeFile(outsidePath, 'outside');
    await mutateAlbums(target, { action: 'create', name: 'Expired', albumId: 'expired' });
    await mutateAlbums(target, { action: 'add', albumId: 'expired', paths: [outsidePath] });
    const document = await readAlbums(target);
    if (!document.ok) throw new Error(document.error);

    const source = await buildAlbumSource(document.document, 'expired', 'idx_missing');
    expect(source).toMatchObject({ catalogExpired: true, members: [{ availability: 'outside' }] });
    expect(source?.images).toHaveLength(1);
  });
});

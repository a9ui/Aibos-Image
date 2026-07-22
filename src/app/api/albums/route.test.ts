import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { GET, POST } from './route';
import { DELETE as DELETE_ALBUM, PATCH } from './[id]/route';
import { DELETE as REMOVE_MEMBERS, POST as ADD_MEMBERS } from './[id]/members/route';
import { POST as MARK_RECENT } from './[id]/recent/route';
import { POST as BUILD_SOURCE } from './[id]/source/route';
import { POST as CLEANUP_PATHS } from './members/cleanup/route';
import { clearIndexSessionsForTests, setIndex } from '@/lib/indexer';
import type { ImageFile } from '@/lib/types';

let root = '';
let target = '';
const previousOverride = process.env.PVU_ALBUMS_PATH;

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-albums-route-'));
  target = path.join(root, 'albums.json');
  process.env.PVU_ALBUMS_PATH = target;
  clearIndexSessionsForTests();
});

afterEach(async () => {
  if (previousOverride === undefined) delete process.env.PVU_ALBUMS_PATH;
  else process.env.PVU_ALBUMS_PATH = previousOverride;
  clearIndexSessionsForTests();
  await fs.rm(root, { recursive: true, force: true });
});

function request(body: string) {
  return new Request('http://127.0.0.1/api/albums', { method: 'POST', headers: { 'content-type': 'application/json' }, body });
}

function operationRequest(url: string, method: string, body: Record<string, unknown>) {
  return new Request(url, {
    method,
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

function context(id: string) {
  return { params: Promise.resolve({ id }) };
}

describe('Album operation API', () => {
  it('reads absent shared state without creating it and creates by operation', async () => {
    const empty = await GET();
    expect(await empty.json()).toMatchObject({ ok: true, exists: false, document: { revision: 0, albums: [] } });
    const created = await POST(request(JSON.stringify({ name: 'Browser Album', expectedRevision: 0 })));
    expect(created.status).toBe(200);
    expect(await created.json()).toMatchObject({ ok: true, changed: true, document: { revision: 1 }, album: { name: 'Browser Album' } });
  });

  it.each([
    ['invalid JSON', '{'],
    ['array body', '[]'],
    ['missing name', '{}'],
    ['oversized name', JSON.stringify({ name: 'x'.repeat(121) })],
    ['invalid revision', JSON.stringify({ name: 'A', expectedRevision: -1 })],
  ])('rejects %s without publishing state', async (_name, body) => {
    const response = await POST(request(body));
    expect(response.status).toBe(400);
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('maps protected future state and stale revisions to conflicts', async () => {
    const future = JSON.stringify({ version: 2, revision: 9, updatedAtUtc: '2026-07-20T00:00:00Z', albums: [], recentAlbumIds: [] });
    await fs.writeFile(target, future, 'utf8');
    const protectedResponse = await POST(request(JSON.stringify({ name: 'Blocked' })));
    expect(protectedResponse.status).toBe(409);
    expect(await fs.readFile(target, 'utf8')).toBe(future);

    await fs.rm(target);
    await POST(request(JSON.stringify({ name: 'Created' })));
    const stale = await POST(request(JSON.stringify({ name: 'Stale', expectedRevision: 0 })));
    expect(stale.status).toBe(409);
    expect(await stale.json()).toMatchObject({ conflict: true, document: { revision: 1 } });
  });

  it('exposes update, member, recent, and delete operations without a snapshot PUT', async () => {
    const createdResponse = await POST(request(JSON.stringify({ name: 'Operations' })));
    const created = await createdResponse.json();
    const albumId = created.album.id as string;
    const imagePath = path.join(root, 'member.jpg');

    const addedResponse = await ADD_MEMBERS(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}/members`,
      'POST',
      { paths: [imagePath], expectedRevision: 1 },
    ), context(albumId));
    const added = await addedResponse.json();
    expect(added).toMatchObject({ ok: true, changed: true, document: { revision: 2 }, album: { members: [{ imagePath }] } });
    const memberId = added.album.members[0].id as string;

    const patchedResponse = await PATCH(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}`,
      'PATCH',
      { name: 'Renamed', pinned: true, coverMemberId: memberId, expectedRevision: 2 },
    ), context(albumId));
    expect(await patchedResponse.json()).toMatchObject({
      ok: true,
      document: { revision: 3 },
      album: { name: 'Renamed', pinned: true, coverMemberId: memberId },
    });

    const recentResponse = await MARK_RECENT(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}/recent`,
      'POST',
      { expectedRevision: 3 },
    ), context(albumId));
    expect(await recentResponse.json()).toMatchObject({ ok: true, changed: false, document: { revision: 3 } });

    const removedResponse = await REMOVE_MEMBERS(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}/members`,
      'DELETE',
      { memberIds: [memberId], expectedRevision: 3 },
    ), context(albumId));
    expect(await removedResponse.json()).toMatchObject({ ok: true, document: { revision: 4 }, album: { coverMemberId: null, members: [] } });

    const deletedResponse = await DELETE_ALBUM(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}`,
      'DELETE',
      { expectedRevision: 4 },
    ), context(albumId));
    expect(await deletedResponse.json()).toMatchObject({ ok: true, document: { revision: 5, albums: [], recentAlbumIds: [] } });
  });

  it('builds a guarded source and keeps Recycle cleanup as a separate operation', async () => {
    const currentPath = path.join(root, 'current.jpg');
    const outsidePath = path.join(root, 'outside.png');
    const missingPath = path.join(root, 'missing.webp');
    await fs.writeFile(currentPath, 'current');
    await fs.writeFile(outsidePath, 'outside');
    const catalogImage: ImageFile = {
      id: currentPath,
      filename: 'current.jpg',
      absolutePath: currentPath,
      fileUrl: `/api/image?path=${encodeURIComponent(currentPath)}&thumb=true`,
      displayUrl: `/api/image?path=${encodeURIComponent(currentPath)}&display=true`,
      fullUrl: `/api/image?path=${encodeURIComponent(currentPath)}`,
      metadata: null,
      createdAt: 1,
      mtime: 1,
    };
    const catalogIndexToken = setIndex([catalogImage], 'route-catalog');

    const created = await (await POST(request(JSON.stringify({ name: 'Source' })))).json();
    const albumId = created.album.id as string;
    await ADD_MEMBERS(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}/members`,
      'POST',
      { paths: [currentPath, outsidePath, missingPath], expectedRevision: 1 },
    ), context(albumId));

    const sourceResponse = await BUILD_SOURCE(operationRequest(
      `http://127.0.0.1/api/albums/${albumId}/source`,
      'POST',
      { catalogIndexToken, expectedRevision: 2 },
    ), context(albumId));
    expect(sourceResponse.status).toBe(200);
    const source = await sourceResponse.json();
    expect(source).toMatchObject({
      ok: true,
      source: {
        documentRevision: 2,
        members: [
          { imagePath: currentPath, availability: 'current' },
          { imagePath: outsidePath, availability: 'outside' },
          { imagePath: missingPath, availability: 'missing', image: null },
        ],
      },
    });
    expect(source.source.images).toHaveLength(2);

    const cleanupResponse = await CLEANUP_PATHS(operationRequest(
      'http://127.0.0.1/api/albums/members/cleanup',
      'POST',
      { paths: [currentPath], expectedRevision: 2 },
    ));
    expect(await cleanupResponse.json()).toMatchObject({ ok: true, document: { revision: 3 } });
    const remaining = await GET();
    expect(await remaining.json()).toMatchObject({
      document: { albums: [{ members: [{ imagePath: outsidePath }, { imagePath: missingPath }] }] },
    });
  });
});

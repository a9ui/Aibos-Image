import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import type { Stats } from 'fs';

import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';

import { mutateAlbums, readAlbums } from './albums';
import { buildAlbumSource } from './albumSource';
import { clearIndexSessionsForTests } from './indexer';

const ALBUM_COUNT = 1_000;
const ACTIVE_MEMBER_COUNT = 10_000;
const MEMBER_COUNT = 100_000;
let root = '';
let target = '';

beforeAll(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-albums-scale-'));
  target = path.join(root, 'albums.json');
  const stamp = '2026-07-20T00:00:00.000Z';
  let memberIndex = 0;
  const remainingMembers = MEMBER_COUNT - ACTIVE_MEMBER_COUNT;
  const remainingAlbums = ALBUM_COUNT - 1;
  const albums = Array.from({ length: ALBUM_COUNT }, (_, albumIndex) => {
    const memberCount = albumIndex === 0
      ? ACTIVE_MEMBER_COUNT
      : Math.floor(remainingMembers / remainingAlbums)
        + (albumIndex <= remainingMembers % remainingAlbums ? 1 : 0);
    const members = Array.from({ length: memberCount }, () => {
      const index = memberIndex;
      memberIndex += 1;
      return {
        id: `member-${index}`,
        imagePath: path.join(root, 'catalog', `${String(index).padStart(6, '0')}.jpg`),
        addedAtUtc: stamp,
        ...(index === 0 || index === MEMBER_COUNT - 1 ? { futureMember: `edge-${index}` } : {}),
      };
    });
    return {
      id: `scale-${albumIndex}`,
      name: albumIndex === 0 ? '10k active Album' : `Scale Album ${albumIndex}`,
      pinned: false,
      coverMemberId: members[0]?.id ?? null,
      createdAtUtc: stamp,
      updatedAtUtc: stamp,
      revision: 1,
      members,
      ...(albumIndex === ALBUM_COUNT - 1 ? { futureAlbum: { keep: true } } : {}),
    };
  });
  if (memberIndex !== MEMBER_COUNT) throw new Error(`Scale fixture built ${memberIndex} members`);
  await fs.writeFile(target, JSON.stringify({
    version: 1,
    revision: 1,
    updatedAtUtc: stamp,
    futureRoot: { keep: true },
    recentAlbumIds: albums.slice(0, 30).map((album) => album.id),
    albums,
  }), 'utf8');
}, 60_000);

afterAll(async () => {
  const resolved = path.resolve(root);
  const temp = path.resolve(os.tmpdir());
  if (resolved.startsWith(`${temp}${path.sep}pvu-albums-scale-`)) {
    await fs.rm(resolved, { recursive: true, force: true });
  }
});

describe('Album store catalog scale', () => {
  it('reads and atomically mutates 1,000 Albums / 100,000 memberships with a 10,000-member active Album', async () => {
    const readStarted = performance.now();
    const read = await readAlbums(target);
    const readMs = performance.now() - readStarted;
    expect(read).toMatchObject({ ok: true, document: { revision: 1, albums: { length: ALBUM_COUNT } } });
    if (!read.ok) throw new Error(read.error);
    expect(read.document.albums[0].members).toHaveLength(ACTIVE_MEMBER_COUNT);
    expect(read.document.albums.reduce((total, album) => total + album.members.length, 0)).toBe(MEMBER_COUNT);
    expect(read.document.albums[0].members[0].futureMember).toBe('edge-0');
    expect(read.document.albums[ALBUM_COUNT - 1].members.at(-1)?.futureMember).toBe(`edge-${MEMBER_COUNT - 1}`);
    expect(readMs).toBeLessThan(30_000);
    const payloadBytes = Buffer.byteLength(JSON.stringify(read.document), 'utf8');
    expect(payloadBytes).toBeGreaterThan(1_000_000);
    expect(payloadBytes).toBeLessThan(64 * 1024 * 1024);

    const writeStarted = performance.now();
    const updated = await mutateAlbums(target, {
      action: 'update',
      albumId: 'scale-0',
      pinned: true,
      expectedRevision: 1,
    });
    const writeMs = performance.now() - writeStarted;
    expect(updated).toMatchObject({ ok: true, changed: true, document: { revision: 2 }, album: { revision: 2, pinned: true } });
    expect(updated.album?.members).toHaveLength(ACTIVE_MEMBER_COUNT);
    expect(writeMs).toBeLessThan(30_000);

    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored.futureRoot).toEqual({ keep: true });
    expect(stored.albums[0].members[0].futureMember).toBe('edge-0');
    expect(stored.albums.at(-1).members.at(-1).futureMember).toBe(`edge-${MEMBER_COUNT - 1}`);
    expect(stored.albums.at(-1).futureAlbum).toEqual({ keep: true });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 60_000);

  it('builds the 10,000-member active source without an O(catalog x Album) lookup', async () => {
    const read = await readAlbums(target);
    if (!read.ok) throw new Error(read.error);
    const stat = vi.spyOn(fs, 'stat').mockImplementation(async () => ({
      isFile: () => true,
      mtimeMs: 1,
      birthtimeMs: 1,
    }) as Stats);
    try {
      const started = performance.now();
      const source = await buildAlbumSource(read.document, 'scale-0');
      const sourceMs = performance.now() - started;
      expect(source).toMatchObject({
        members: { length: ACTIVE_MEMBER_COUNT },
        images: { length: ACTIVE_MEMBER_COUNT },
      });
      expect(source?.members.every((member) => member.availability === 'outside')).toBe(true);
      expect(sourceMs).toBeLessThan(20_000);
    } finally {
      stat.mockRestore();
      clearIndexSessionsForTests();
    }
  }, 30_000);
});

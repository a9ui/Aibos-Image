import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { mutateAlbums, readAlbums } from './albums';

let root = '';
let target = '';

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-albums-'));
  target = path.join(root, 'albums.json');
});

afterEach(async () => {
  await fs.rm(root, { recursive: true, force: true });
});

describe('shared Album store', () => {
  it('treats a missing store as empty without publishing a file', async () => {
    const result = await readAlbums(target);
    expect(result).toMatchObject({ ok: true, exists: false, document: { version: 1, revision: 0, albums: [], recentAlbumIds: [] } });
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('reads the known versionless empty document without rewriting it and upgrades on the first real mutation', async () => {
    const legacy = JSON.stringify({ albums: [], futureRoot: { keep: true } });
    await fs.writeFile(target, legacy, 'utf8');

    const read = await readAlbums(target);
    expect(read).toMatchObject({
      ok: true,
      exists: true,
      document: { version: 1, revision: 0, albums: [], recentAlbumIds: [], futureRoot: { keep: true } },
    });
    expect(await fs.readFile(target, 'utf8')).toBe(legacy);

    const mutated = await mutateAlbums(target, {
      action: 'create',
      name: 'First v1 Album',
      albumId: 'first-v1',
      expectedRevision: 0,
    });
    expect(mutated).toMatchObject({ ok: true, document: { version: 1, revision: 1 } });
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored).toMatchObject({
      version: 1,
      revision: 1,
      futureRoot: { keep: true },
      albums: [{ id: 'first-v1' }],
    });
  });

  it('protects ambiguous non-empty or partially-versioned legacy documents', async () => {
    const candidates = [
      JSON.stringify({ albums: [{ id: 'legacy-with-unknown-shape' }] }),
      JSON.stringify({ albums: [], revision: 4 }),
    ];
    for (const original of candidates) {
      await fs.writeFile(target, original, 'utf8');
      expect(await readAlbums(target)).toMatchObject({ ok: false, malformed: true });
      expect(await mutateAlbums(target, { action: 'create', name: 'Refuse' })).toMatchObject({ ok: false, changed: false });
      expect(await fs.readFile(target, 'utf8')).toBe(original);
    }
  });

  it('supports operation mutations with monotonic revisions and idempotent membership', async () => {
    const first = await mutateAlbums(target, { action: 'create', name: '  Trips  ', albumId: 'album-1', expectedRevision: 0 });
    expect(first).toMatchObject({ ok: true, changed: true, document: { revision: 1 }, album: { id: 'album-1', name: 'Trips', revision: 1 } });

    const imagePath = path.join(root, 'one.jpg');
    const added = await mutateAlbums(target, { action: 'add', albumId: 'album-1', paths: [imagePath, imagePath], expectedRevision: 1 });
    expect(added).toMatchObject({ ok: true, changed: true, document: { revision: 2 }, album: { revision: 2 } });
    expect(added.album?.members).toHaveLength(1);

    const duplicate = await mutateAlbums(target, { action: 'add', albumId: 'album-1', paths: [imagePath], expectedRevision: 2 });
    expect(duplicate).toMatchObject({ ok: true, changed: false, document: { revision: 2 } });

    const memberId = added.album!.members[0].id;
    const updated = await mutateAlbums(target, { action: 'update', albumId: 'album-1', pinned: true, coverMemberId: memberId, expectedRevision: 2 });
    expect(updated).toMatchObject({ ok: true, changed: true, document: { revision: 3 }, album: { revision: 3, pinned: true, coverMemberId: memberId } });

    const removed = await mutateAlbums(target, { action: 'remove', albumId: 'album-1', memberIds: [memberId], expectedRevision: 3 });
    expect(removed).toMatchObject({ ok: true, changed: true, document: { revision: 4 }, album: { revision: 4, coverMemberId: null, members: [] } });

    const deleted = await mutateAlbums(target, { action: 'delete', albumId: 'album-1', expectedRevision: 4 });
    expect(deleted).toMatchObject({ ok: true, changed: true, document: { revision: 5, albums: [], recentAlbumIds: [] } });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.tmp') || name.endsWith('.lock'))).toEqual([]);
  });

  it('preserves unknown root, Album, and member fields through later operations', async () => {
    const original = {
      version: 1,
      revision: 7,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      futureRoot: { keep: true },
      recentAlbumIds: ['album-1'],
      albums: [{
        id: 'album-1',
        name: 'Keep fields',
        pinned: false,
        coverMemberId: 'member-1',
        createdAtUtc: '2026-07-20T00:00:00.000Z',
        updatedAtUtc: '2026-07-20T00:00:00.000Z',
        revision: 3,
        futureAlbum: ['keep'],
        members: [{
          id: 'member-1',
          imagePath: path.join(root, 'one.jpg'),
          addedAtUtc: '2026-07-20T00:00:00.000Z',
          futureMember: 42,
        }],
      }],
    };
    await fs.writeFile(target, JSON.stringify(original), 'utf8');

    const result = await mutateAlbums(target, { action: 'update', albumId: 'album-1', name: 'Renamed', expectedRevision: 7 });
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(result).toMatchObject({ ok: true, document: { revision: 8 }, album: { revision: 4 } });
    expect(stored.futureRoot).toEqual({ keep: true });
    expect(stored.albums[0].futureAlbum).toEqual(['keep']);
    expect(stored.albums[0].members[0].futureMember).toBe(42);
  });

  it.each([
    ['malformed JSON', '{not-json', { malformed: true, futureVersion: false }],
    ['future version', JSON.stringify({ version: 2, revision: 0, updatedAtUtc: '2026-07-20T00:00:00Z', albums: [], recentAlbumIds: [] }), { malformed: false, futureVersion: true }],
    ['duplicate Album ids', JSON.stringify({
      version: 1,
      revision: 1,
      updatedAtUtc: '2026-07-20T00:00:00Z',
      recentAlbumIds: [],
      albums: [0, 1].map(() => ({ id: 'same', name: 'Same', pinned: false, coverMemberId: null, createdAtUtc: '2026-07-20T00:00:00Z', updatedAtUtc: '2026-07-20T00:00:00Z', revision: 1, members: [] })),
    }), { malformed: true, futureVersion: false }],
  ])('refuses to overwrite protected state: %s', async (_name, original, expected) => {
    await fs.writeFile(target, original, 'utf8');
    const read = await readAlbums(target);
    expect(read).toMatchObject({ ok: false, ...expected });
    const mutation = await mutateAlbums(target, { action: 'create', name: 'No overwrite' });
    expect(mutation).toMatchObject({ ok: false, ...expected, changed: false });
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('rejects stale revisions without mutating the latest document', async () => {
    await mutateAlbums(target, { action: 'create', name: 'First', albumId: 'first' });
    const before = await fs.readFile(target, 'utf8');
    const result = await mutateAlbums(target, { action: 'create', name: 'Stale', expectedRevision: 0 });
    expect(result).toMatchObject({ ok: false, conflict: true, changed: false, document: { revision: 1 } });
    expect(await fs.readFile(target, 'utf8')).toBe(before);
  });

  it('lets one same-revision writer win and applies the loser only after an explicit latest-revision retry', async () => {
    const addedPath = path.join(root, 'retry-added.jpg');
    await mutateAlbums(target, { action: 'create', name: 'Before', albumId: 'retry-race' });
    const rename = { action: 'update' as const, albumId: 'retry-race', name: 'After', expectedRevision: 1 };
    const add = { action: 'add' as const, albumId: 'retry-race', paths: [addedPath], expectedRevision: 1 };

    const results = await Promise.all([
      mutateAlbums(target, rename),
      mutateAlbums(target, add),
    ]);
    const winnerIndex = results.findIndex((result) => result.ok);
    const loserIndex = results.findIndex((result) => !result.ok && result.conflict);
    expect(winnerIndex).toBeGreaterThanOrEqual(0);
    expect(loserIndex).toBeGreaterThanOrEqual(0);
    expect(results.filter((result) => result.ok)).toHaveLength(1);
    expect(results[loserIndex]).toMatchObject({ changed: false, document: { revision: 2 } });

    const loser = loserIndex === 0 ? rename : add;
    const retry = await mutateAlbums(target, { ...loser, expectedRevision: 2 });
    expect(retry).toMatchObject({ ok: true, changed: true, document: { revision: 3 } });

    const stored = await readAlbums(target);
    if (!stored.ok) throw new Error(stored.error);
    const album = stored.document.albums.find((candidate) => candidate.id === 'retry-race')!;
    expect(album.name).toBe('After');
    expect(album.members.map((member) => member.imagePath)).toContain(path.resolve(addedPath));
  });

  it('serializes concurrent latest-on-disk operations without lost updates', async () => {
    const results = await Promise.all(Array.from({ length: 20 }, (_, index) => mutateAlbums(target, {
      action: 'create' as const,
      name: `Album ${index}`,
      albumId: `album-${index}`,
    })));
    const stored = await readAlbums(target);
    expect(results.every((result) => result.ok)).toBe(true);
    expect(stored).toMatchObject({ ok: true, document: { revision: 20 } });
    if (!stored.ok) throw new Error(stored.error);
    expect(stored.document.albums.map((album) => album.id).sort()).toEqual(Array.from({ length: 20 }, (_, index) => `album-${index}`).sort());
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('serializes conflicting operations on the same Album without field loss or resurrection', async () => {
    const seedPath = path.join(root, 'seed.jpg');
    const addedPath = path.join(root, 'added.jpg');
    const coverPath = path.join(root, 'cover.jpg');
    await mutateAlbums(target, { action: 'create', name: 'Race', albumId: 'race' });
    const seeded = await mutateAlbums(target, { action: 'add', albumId: 'race', paths: [seedPath, coverPath] });
    const coverId = seeded.album!.members.find((member) => member.imagePath === coverPath)!.id;
    await mutateAlbums(target, { action: 'update', albumId: 'race', coverMemberId: coverId });

    const renameAdd = await Promise.all([
      mutateAlbums(target, { action: 'update', albumId: 'race', name: 'Renamed' }),
      mutateAlbums(target, { action: 'add', albumId: 'race', paths: [addedPath] }),
    ]);
    expect(renameAdd.every((result) => result.ok)).toBe(true);
    let current = await readAlbums(target);
    if (!current.ok) throw new Error(current.error);
    let album = current.document.albums.find((candidate) => candidate.id === 'race')!;
    expect(album.name).toBe('Renamed');
    expect(album.members.map((member) => member.imagePath)).toContain(path.resolve(addedPath));

    const pinRename = await Promise.all([
      mutateAlbums(target, { action: 'update', albumId: 'race', pinned: true }),
      mutateAlbums(target, { action: 'update', albumId: 'race', name: 'Pinned and renamed' }),
    ]);
    expect(pinRename.every((result) => result.ok)).toBe(true);
    current = await readAlbums(target);
    if (!current.ok) throw new Error(current.error);
    album = current.document.albums.find((candidate) => candidate.id === 'race')!;
    expect(album).toMatchObject({ name: 'Pinned and renamed', pinned: true });

    const removeCover = await Promise.all([
      mutateAlbums(target, { action: 'remove', albumId: 'race', memberIds: [coverId] }),
      mutateAlbums(target, { action: 'update', albumId: 'race', coverMemberId: coverId }),
    ]);
    expect(removeCover.some((result) => result.ok)).toBe(true);
    current = await readAlbums(target);
    if (!current.ok) throw new Error(current.error);
    album = current.document.albums.find((candidate) => candidate.id === 'race')!;
    expect(album.members.some((member) => member.id === coverId)).toBe(false);
    expect(album.coverMemberId).toBeNull();

    const deleteAdd = await Promise.all([
      mutateAlbums(target, { action: 'delete', albumId: 'race' }),
      mutateAlbums(target, { action: 'add', albumId: 'race', paths: [path.join(root, 'late.jpg')] }),
    ]);
    expect(deleteAdd.some((result) => result.ok)).toBe(true);
    current = await readAlbums(target);
    if (!current.ok) throw new Error(current.error);
    expect(current.document.albums.some((candidate) => candidate.id === 'race')).toBe(false);
  });

  it('cleans a recycled source identity from every Album in one document revision', async () => {
    const imagePath = path.join(root, 'shared.jpg');
    await mutateAlbums(target, { action: 'create', name: 'One', albumId: 'one' });
    await mutateAlbums(target, { action: 'create', name: 'Two', albumId: 'two' });
    await mutateAlbums(target, { action: 'add', albumId: 'one', paths: [imagePath] });
    await mutateAlbums(target, { action: 'add', albumId: 'two', paths: [imagePath] });
    const before = await readAlbums(target);
    if (!before.ok) throw new Error(before.error);
    const beforeAlbumRevisions = before.document.albums.map((album) => album.revision);

    const result = await mutateAlbums(target, { action: 'cleanupPaths', paths: [imagePath], expectedRevision: before.document.revision });
    expect(result).toMatchObject({ ok: true, changed: true, document: { revision: before.document.revision + 1 } });
    expect(result.document!.albums.every((album) => album.members.length === 0)).toBe(true);
    expect(result.document!.albums.map((album) => album.revision)).toEqual(beforeAlbumRevisions.map((revision) => revision + 1));
  });

  it('rejects paths that are not supported by both Browser and WPF', async () => {
    await mutateAlbums(target, { action: 'create', name: 'Shared', albumId: 'shared' });
    const result = await mutateAlbums(target, {
      action: 'add',
      albumId: 'shared',
      paths: [path.join(root, 'unsupported.bmp')],
    });
    expect(result).toMatchObject({ ok: false, changed: false, error: 'paths must contain bounded absolute supported image paths.' });
  });
});

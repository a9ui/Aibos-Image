import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';
import { promisify } from 'util';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { mutateAlbums, readAlbums } from './albums';
import { withRecoverableDirectoryWriteLock } from './fileWriteLock';

const execFileAsync = promisify(execFile);
const wpfExecutable = path.resolve(
  process.env.PVU_WPF_EXECUTABLE
    ?? 'local-native/PhotoViewer.Wpf/bin/Release/net8.0-windows/PhotoViewer.Wpf.exe',
);
const canRunWpf = process.platform === 'win32' && await fs.stat(wpfExecutable).then(() => true).catch(() => false);
const crossRuntimeDescribe = canRunWpf ? describe : describe.skip;
const wpfScaleIt = canRunWpf && process.env.PVU_RUN_WPF_ALBUM_SCALE === '1' ? it : it.skip;

async function waitForFile(target: string, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await fs.stat(target).then(() => true).catch(() => false)) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`Timed out waiting for ${target}`);
}

let root = '';

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-albums-cross-runtime-'));
});

afterEach(async () => {
  await fs.rm(root, { recursive: true, force: true });
});

crossRuntimeDescribe('Browser/WPF Album store compatibility', () => {
  it('does not reap a replacement live WPF generation after Browser stale inspection', async () => {
    const target = path.join(root, 'albums.json');
    const lockPath = `${target}.lock`;
    const ownerPath = path.join(lockPath, 'owner.json');
    const readyPath = path.join(root, 'wpf-lock-ready');
    const releasePath = path.join(root, 'wpf-lock-release');
    const resultPath = path.join(root, 'wpf-lock-result.json');
    const staleToken = '66666666666666666666666666666666';
    const seed = {
      version: 1,
      revision: 7,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      albums: [{
        id: 'preserved',
        name: 'Preserved',
        pinned: false,
        coverMemberId: 'preserved-member',
        createdAtUtc: '2026-07-20T00:00:00.000Z',
        updatedAtUtc: '2026-07-20T00:00:00.000Z',
        revision: 1,
        members: [{
          id: 'preserved-member',
          imagePath: path.join(root, 'preserved.jpg'),
          addedAtUtc: '2026-07-20T00:00:00.000Z',
          futureMember: { keep: true },
        }],
        futureAlbum: ['keep'],
      }],
      recentAlbumIds: ['preserved'],
      futureRoot: { keep: true },
    };
    await fs.writeFile(target, `${JSON.stringify(seed, null, 2)}\n`, 'utf8');
    await fs.mkdir(lockPath);
    await fs.writeFile(ownerPath, `${JSON.stringify({
      protocol: 'photoviewer.shared-directory-lock/v1',
      token: staleToken,
      pid: 2_147_483_647,
      createdAtUtc: new Date(0).toISOString(),
    })}\n`, 'utf8');
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(ownerPath, old, old);
    await fs.utimes(lockPath, old, old);
    let reportInspected!: () => void;
    let releaseReaper!: () => void;
    const inspected = new Promise<void>((resolve) => { reportInspected = resolve; });
    const reaperCanContinue = new Promise<void>((resolve) => { releaseReaper = resolve; });
    let criticalSections = 0;
    const browserAttempt = withRecoverableDirectoryWriteLock(target, async () => {
      criticalSections += 1;
    }, {
      retryDelayMs: 1,
      timeoutMs: 100,
      staleMs: 1,
      testHooks: {
        afterStaleOwnerInspected: async () => {
          reportInspected();
          await reaperCanContinue;
        },
      },
    });

    await inspected;
    await fs.rm(lockPath, { recursive: true, force: true });
    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-lock-holder-smoke', resultPath,
      '--album-path', target,
      '--ready-path', readyPath,
      '--release-path', releasePath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);
    const liveOwnerBefore = await fs.readFile(ownerPath, 'utf8');
    releaseReaper();

    await expect(browserAttempt).rejects.toThrow(/timed out waiting for shared state lock/i);
    expect(criticalSections).toBe(0);
    expect(await fs.readFile(ownerPath, 'utf8')).toBe(liveOwnerBefore);
    await fs.writeFile(releasePath, 'release', 'utf8');
    await wpfRun;
    expect(JSON.parse(await fs.readFile(resultPath, 'utf8'))).toMatchObject({ ok: true, residueFree: true });

    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored).toEqual(seed);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 30_000);

  it('releases a replacement WPF generation even when the delayed Browser reaper temporarily claims its marker', async () => {
    const target = path.join(root, 'release-race-albums.json');
    const lockPath = `${target}.lock`;
    const ownerPath = path.join(lockPath, 'owner.json');
    const readyPath = path.join(root, 'release-race-wpf-ready');
    const releasePath = path.join(root, 'release-race-wpf-release');
    const resultPath = path.join(root, 'release-race-wpf-result.json');
    const seed = {
      version: 1,
      revision: 9,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      albums: [],
      recentAlbumIds: [],
      futureRoot: { keep: 'release-race' },
    };
    await fs.writeFile(target, `${JSON.stringify(seed, null, 2)}\n`, 'utf8');
    await fs.mkdir(lockPath);
    await fs.writeFile(ownerPath, `${JSON.stringify({
      protocol: 'photoviewer.shared-directory-lock/v1',
      token: '77777777777777777777777777777777',
      pid: 2_147_483_647,
      createdAtUtc: new Date(0).toISOString(),
    })}\n`, 'utf8');
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(ownerPath, old, old);
    await fs.utimes(lockPath, old, old);

    let reportInspected!: () => void;
    let releaseReaper!: () => void;
    let reportMarkerClaimed!: () => void;
    let allowTokenCheck!: () => void;
    const inspected = new Promise<void>((resolve) => { reportInspected = resolve; });
    const reaperCanClaim = new Promise<void>((resolve) => { releaseReaper = resolve; });
    const markerClaimed = new Promise<void>((resolve) => { reportMarkerClaimed = resolve; });
    const tokenCheckAllowed = new Promise<void>((resolve) => { allowTokenCheck = resolve; });
    let browserCriticalSections = 0;
    const browserAttempt = withRecoverableDirectoryWriteLock(target, async () => {
      browserCriticalSections += 1;
    }, {
      retryDelayMs: 1,
      timeoutMs: 1_000,
      staleMs: 1,
      testHooks: {
        afterStaleOwnerInspected: async () => {
          reportInspected();
          await reaperCanClaim;
        },
        afterOwnerMarkerClaimed: async () => {
          reportMarkerClaimed();
          await tokenCheckAllowed;
        },
      },
    });

    await inspected;
    await fs.rm(lockPath, { recursive: true, force: true });
    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-lock-holder-smoke', resultPath,
      '--album-path', target,
      '--ready-path', readyPath,
      '--release-path', releasePath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);
    releaseReaper();
    await markerClaimed;
    await fs.writeFile(releasePath, 'release', 'utf8');
    await waitForFile(resultPath);
    expect(JSON.parse(await fs.readFile(resultPath, 'utf8'))).toMatchObject({ ok: true, residueFree: true });
    allowTokenCheck();

    await expect(browserAttempt).resolves.toBeUndefined();
    await wpfRun;
    expect(browserCriticalSections).toBe(1);
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual(seed);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 30_000);

  it('retries WPF release when owner.json is restored between its missing-owner read and claim scan', async () => {
    const target = path.join(root, 'release-restore-race-albums.json');
    const lockPath = `${target}.lock`;
    const ownerPath = path.join(lockPath, 'owner.json');
    const readyPath = path.join(root, 'release-restore-wpf-ready');
    const releasePath = path.join(root, 'release-restore-wpf-release');
    const missingReadyPath = path.join(root, 'release-restore-missing-ready');
    const missingContinuePath = path.join(root, 'release-restore-missing-continue');
    const resultPath = path.join(root, 'release-restore-wpf-result.json');
    const seed = {
      version: 1,
      revision: 10,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      albums: [],
      recentAlbumIds: [],
      futureRoot: { keep: 'release-restore-race' },
    };
    await fs.writeFile(target, `${JSON.stringify(seed, null, 2)}\n`, 'utf8');

    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-lock-holder-smoke', resultPath,
      '--album-path', target,
      '--ready-path', readyPath,
      '--release-path', releasePath,
      '--release-missing-ready-path', missingReadyPath,
      '--release-missing-continue-path', missingContinuePath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);

    const owner = JSON.parse(await fs.readFile(ownerPath, 'utf8')) as { token: string; pid: number };
    const claimPath = path.join(
      lockPath,
      `.claim.${'c'.repeat(32)}.${2_147_483_647}.${Date.now()}.${'d'.repeat(32)}.json`,
    );
    await fs.rename(ownerPath, claimPath);
    await fs.writeFile(releasePath, 'release', 'utf8');
    await waitForFile(missingReadyPath);

    await fs.rename(claimPath, ownerPath);
    await fs.writeFile(missingContinuePath, 'continue', 'utf8');
    await wpfRun;

    expect(JSON.parse(await fs.readFile(resultPath, 'utf8'))).toMatchObject({ ok: true, residueFree: true });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual(seed);
    expect(owner.pid).toBeGreaterThan(0);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 30_000);

  it('keeps a WPF payload owner authoritative after its delayed Browser reaper crashes', async () => {
    const target = path.join(root, 'crashed-reaper-albums.json');
    const lockPath = `${target}.lock`;
    const ownerPath = path.join(lockPath, 'owner.json');
    const readyPath = path.join(root, 'crashed-reaper-wpf-ready');
    const releasePath = path.join(root, 'crashed-reaper-wpf-release');
    const resultPath = path.join(root, 'crashed-reaper-wpf-result.json');
    const seed = {
      version: 1,
      revision: 11,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      albums: [],
      recentAlbumIds: [],
      futureRoot: { keep: 'crashed-reaper' },
    };
    await fs.writeFile(target, `${JSON.stringify(seed, null, 2)}\n`, 'utf8');
    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-lock-holder-smoke', resultPath,
      '--album-path', target,
      '--ready-path', readyPath,
      '--release-path', releasePath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);

    const wpfOwner = JSON.parse(await fs.readFile(ownerPath, 'utf8')) as { token: string; pid: number };
    const abandonedClaim = `.claim.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.${2_147_483_647}.${Date.now() - 60_000}.cccccccccccccccccccccccccccccccc.json`;
    const abandonedClaimPath = path.join(lockPath, abandonedClaim);
    await fs.rename(ownerPath, abandonedClaimPath);
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);
    let browserCriticalSections = 0;

    await expect(withRecoverableDirectoryWriteLock(target, async () => {
      browserCriticalSections += 1;
    }, {
      retryDelayMs: 1,
      timeoutMs: 100,
      staleMs: 1,
    })).rejects.toThrow(/timed out waiting for shared state lock/i);
    expect(browserCriticalSections).toBe(0);
    expect(JSON.parse(await fs.readFile(abandonedClaimPath, 'utf8'))).toMatchObject(wpfOwner);

    await fs.writeFile(releasePath, 'release', 'utf8');
    await wpfRun;
    expect(JSON.parse(await fs.readFile(resultPath, 'utf8'))).toMatchObject({ ok: true, residueFree: true });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual(seed);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 30_000);

  wpfScaleIt('keeps WPF Album reads and operation publishes bounded at 1,000 Albums / 100,000 memberships', async () => {
    const target = path.join(root, 'albums.json');
    const resultPath = path.join(root, 'wpf-scale-result.json');
    const memberRoot = path.join(root, 'catalog');
    const timestamp = '2026-07-20T00:00:00.000Z';
    const albums = Array.from({ length: 1_000 }, (_, albumIndex) => {
      const albumId = `scale-${String(albumIndex).padStart(4, '0')}`;
      const members = Array.from({ length: 100 }, (_, memberIndex) => ({
        id: `${albumId}-member-${String(memberIndex).padStart(3, '0')}`,
        imagePath: path.join(memberRoot, String(albumIndex), `${memberIndex}.jpg`),
        addedAtUtc: timestamp,
        ...(albumIndex === 0 && memberIndex === 0 ? { futureMember: { keep: true } } : {}),
      }));
      return {
        id: albumId,
        name: `Scale Album ${albumIndex}`,
        pinned: albumIndex < 10,
        coverMemberId: members[0].id,
        createdAtUtc: timestamp,
        updatedAtUtc: timestamp,
        revision: 1,
        members,
        ...(albumIndex === 0 ? { futureAlbum: ['keep'] } : {}),
      };
    });
    await fs.writeFile(target, JSON.stringify({
      version: 1,
      revision: 5,
      updatedAtUtc: timestamp,
      albums,
      recentAlbumIds: albums.slice(0, 10).map((album) => album.id),
      futureRoot: { keep: true },
    }), 'utf8');

    const startedAt = Date.now();
    await execFileAsync(wpfExecutable, [
      '--album-store-smoke', resultPath,
      '--album-path', target,
      '--member-path', path.join(root, 'wpf-scale-new.jpg'),
    ], { windowsHide: true, timeout: 120_000, maxBuffer: 4 * 1024 * 1024 });
    const elapsedMs = Date.now() - startedAt;
    const wpfResult = JSON.parse(await fs.readFile(resultPath, 'utf8'));
    expect(wpfResult).toMatchObject({
      ok: true,
      initialRevision: 5,
      finalRevision: 9,
      unknownRootPreserved: true,
      liveOwnerLockPreserved: true,
      noResidue: true,
      albumCount: 1_001,
    });
    expect(elapsedMs).toBeLessThan(60_000);

    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored.futureRoot).toEqual({ keep: true });
    expect(stored.albums[0].futureAlbum).toEqual(['keep']);
    expect(stored.albums[0].members[0].futureMember).toEqual({ keep: true });
  }, 125_000);

  it('lets WPF upgrade the known versionless empty store without a read-time rewrite', async () => {
    const target = path.join(root, 'albums.json');
    const resultPath = path.join(root, 'wpf-legacy-result.json');
    const wpfImage = path.join(root, 'wpf-legacy.jpg');
    const legacy = JSON.stringify({ albums: [], futureRoot: { keep: true } });
    await fs.writeFile(target, legacy, 'utf8');

    const browserRead = await readAlbums(target);
    expect(browserRead).toMatchObject({ ok: true, document: { version: 1, revision: 0, albums: [] } });
    expect(await fs.readFile(target, 'utf8')).toBe(legacy);

    await execFileAsync(wpfExecutable, [
      '--album-store-smoke', resultPath,
      '--album-path', target,
      '--member-path', wpfImage,
    ], { windowsHide: true, timeout: 30_000 });
    const wpfResult = JSON.parse(await fs.readFile(resultPath, 'utf8'));
    expect(wpfResult).toMatchObject({
      ok: true,
      initialRevision: 0,
      finalRevision: 4,
      unknownRootPreserved: true,
      legacyMigration: { Ok: true },
    });
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored).toMatchObject({ version: 1, revision: 4, futureRoot: { keep: true } });
  });

  it('interleaves both runtimes without losing fields or revisions', async () => {
    const target = path.join(root, 'albums.json');
    const resultPath = path.join(root, 'wpf-result.json');
    const browserImage = path.join(root, 'browser.jpg');
    const wpfImage = path.join(root, 'wpf.jpg');

    const browserCreated = await mutateAlbums(target, {
      action: 'create',
      name: 'Browser before WPF',
      albumId: 'browser-before',
      expectedRevision: 0,
    });
    await mutateAlbums(target, {
      action: 'add',
      albumId: 'browser-before',
      paths: [browserImage],
      expectedRevision: browserCreated.document!.revision,
    });
    const seeded = JSON.parse(await fs.readFile(target, 'utf8'));
    seeded.futureRoot = { keep: true };
    seeded.albums[0].futureAlbum = ['keep'];
    seeded.albums[0].members[0].futureMember = 42;
    await fs.writeFile(target, `${JSON.stringify(seeded, null, 2)}\n`, 'utf8');

    try {
      await execFileAsync(wpfExecutable, [
        '--album-store-smoke', resultPath,
        '--album-path', target,
        '--member-path', wpfImage,
      ], { windowsHide: true, timeout: 30_000 });
    } catch (error) {
      const report = await fs.readFile(resultPath, 'utf8').catch(() => '<result missing>');
      throw new Error(`${error instanceof Error ? error.message : String(error)}\n${report}`);
    }
    const wpfResult = JSON.parse(await fs.readFile(resultPath, 'utf8'));
    expect(wpfResult).toMatchObject({
      ok: true,
      initialRevision: 2,
      finalRevision: 6,
      created: 'Succeeded',
      added: 'Succeeded',
      updated: 'Succeeded',
      cleaned: 'Succeeded',
      stale: 'Conflict',
      unknownRootPreserved: true,
      noResidue: true,
      albumCount: 2,
    });

    const browserAfter = await mutateAlbums(target, {
      action: 'create',
      name: 'Browser after WPF',
      albumId: 'browser-after',
      expectedRevision: 6,
    });
    expect(browserAfter).toMatchObject({ ok: true, document: { revision: 7 } });
    const final = await readAlbums(target);
    expect(final).toMatchObject({ ok: true, document: { revision: 7 } });
    if (!final.ok) throw new Error(final.error);
    expect(final.document.albums.map((album) => album.id)).toEqual(expect.arrayContaining([
      'browser-before',
      wpfResult.albumId,
      'browser-after',
    ]));
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));
    expect(stored.futureRoot).toEqual({ keep: true });
    expect(stored.albums.find((album: { id: string }) => album.id === 'browser-before').futureAlbum).toEqual(['keep']);
    expect(stored.albums.find((album: { id: string }) => album.id === 'browser-before').members[0].futureMember).toBe(42);
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.lock') || name.endsWith('.tmp'))).toEqual([]);
  });

  it('serializes simultaneous Browser and WPF writers without lost updates', async () => {
    const target = path.join(root, 'albums.json');
    const resultPath = path.join(root, 'wpf-concurrent-result.json');
    const readyPath = path.join(root, 'wpf-ready');
    const goPath = path.join(root, 'start');
    const countPerRuntime = 16;

    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-concurrent-writer-smoke', resultPath,
      '--album-path', target,
      '--count', String(countPerRuntime),
      '--prefix', 'wpf',
      '--ready-path', readyPath,
      '--go-path', goPath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);
    const browserRun = Promise.all(Array.from({ length: countPerRuntime }, (_, index) => mutateAlbums(target, {
      action: 'create' as const,
      name: `Browser concurrent ${index}`,
      albumId: `browser-${String(index).padStart(3, '0')}`,
    })));
    await fs.writeFile(goPath, 'go', 'utf8');

    const [browserResults] = await Promise.all([browserRun, wpfRun]);
    expect(browserResults.every((result) => result.ok)).toBe(true);
    const wpfResult = JSON.parse(await fs.readFile(resultPath, 'utf8'));
    expect(wpfResult).toMatchObject({ ok: true, count: countPerRuntime });

    const final = await readAlbums(target);
    expect(final).toMatchObject({ ok: true, document: { revision: countPerRuntime * 2 } });
    if (!final.ok) throw new Error(final.error);
    expect(final.document.albums).toHaveLength(countPerRuntime * 2);
    expect(final.document.albums.filter((album) => album.id.startsWith('browser-'))).toHaveLength(countPerRuntime);
    expect(final.document.albums.filter((album) => album.id.startsWith('wpf-'))).toHaveLength(countPerRuntime);
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.lock') || name.endsWith('.tmp'))).toEqual([]);
  });

  it('serializes Browser add and WPF remove on the same Album without losing either intent', async () => {
    const target = path.join(root, 'albums.json');
    const resultPath = path.join(root, 'wpf-member-result.json');
    const readyPath = path.join(root, 'wpf-member-ready');
    const goPath = path.join(root, 'wpf-member-go');
    const removedPath = path.join(root, 'remove-by-wpf.jpg');
    const addedPath = path.join(root, 'add-by-browser.jpg');
    await mutateAlbums(target, { action: 'create', name: 'Shared race', albumId: 'shared-race' });
    await mutateAlbums(target, { action: 'add', albumId: 'shared-race', paths: [removedPath] });

    const wpfRun = execFileAsync(wpfExecutable, [
      '--album-member-writer-smoke', resultPath,
      '--album-path', target,
      '--album-id', 'shared-race',
      '--member-path', removedPath,
      '--ready-path', readyPath,
      '--go-path', goPath,
    ], { windowsHide: true, timeout: 30_000 });
    await waitForFile(readyPath);

    const [browserResult] = await Promise.all([
      mutateAlbums(target, { action: 'add', albumId: 'shared-race', paths: [addedPath] }),
      (async () => {
        await fs.writeFile(goPath, 'go', 'utf8');
        await wpfRun;
      })(),
    ]);
    expect(browserResult).toMatchObject({ ok: true, changed: true });
    const wpfResult = JSON.parse(await fs.readFile(resultPath, 'utf8'));
    expect(wpfResult).toMatchObject({ ok: true, status: 'Succeeded', removed: true });

    const final = await readAlbums(target);
    expect(final).toMatchObject({ ok: true, document: { revision: 4 } });
    if (!final.ok) throw new Error(final.error);
    const members = final.document.albums.find((album) => album.id === 'shared-race')!.members;
    expect(members.map((member) => member.imagePath)).toContain(path.resolve(addedPath));
    expect(members.map((member) => member.imagePath)).not.toContain(path.resolve(removedPath));
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.lock') || name.endsWith('.tmp'))).toEqual([]);
  }, 30_000);
});

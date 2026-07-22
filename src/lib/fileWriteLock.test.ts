import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { withFileWriteLock, withRecoverableDirectoryWriteLock } from './fileWriteLock';

let root = '';
let target = '';

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-file-lock-'));
  target = path.join(root, 'shared.json');
});

afterEach(async () => {
  await fs.rm(root, { recursive: true, force: true });
});

describe('withFileWriteLock', () => {
  it('serializes concurrent writers and removes the lock afterward', async () => {
    const events: string[] = [];
    let releaseFirst!: () => void;
    const firstCanFinish = new Promise<void>((resolve) => { releaseFirst = resolve; });

    const first = withFileWriteLock(target, async () => {
      events.push('first-start');
      await firstCanFinish;
      events.push('first-end');
    }, { retryDelayMs: 1, timeoutMs: 1_000 });
    await new Promise((resolve) => setTimeout(resolve, 5));
    const second = withFileWriteLock(target, async () => {
      events.push('second-start');
      events.push('second-end');
    }, { retryDelayMs: 1, timeoutMs: 1_000 });

    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(events).toEqual(['first-start']);
    releaseFirst();
    await Promise.all([first, second]);

    expect(events).toEqual(['first-start', 'first-end', 'second-start', 'second-end']);
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('queues same-process contenders FIFO without starving behind the shared lock timeout', async () => {
    const acquisitionOrder: number[] = [];

    const writers = Array.from({ length: 30 }, (_, index) =>
      withFileWriteLock(
        target,
        async () => {
          acquisitionOrder.push(index);
          await new Promise((resolve) => setTimeout(resolve, 8));
        },
        { retryDelayMs: 1, timeoutMs: 20 },
      ),
    );

    await expect(Promise.all(writers)).resolves.toHaveLength(30);
    expect(acquisitionOrder).toEqual(Array.from({ length: 30 }, (_, index) => index));
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('fails closed instead of pathname-reaping a stale legacy file lock', async () => {
    const lockPath = `${target}.lock`;
    await fs.writeFile(lockPath, 'orphan', 'utf8');
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);

    await expect(withFileWriteLock(target, async () => 'never', {
      retryDelayMs: 1,
      timeoutMs: 10,
      staleMs: 30_000,
    })).rejects.toThrow(/timed out waiting for shared state lock/i);

    expect(await fs.readFile(lockPath, 'utf8')).toBe('orphan');
  });

  it('times out without deleting a live lock', async () => {
    const lockPath = `${target}.lock`;
    await fs.writeFile(lockPath, 'active', 'utf8');

    await expect(withFileWriteLock(target, async () => 'never', {
      retryDelayMs: 1,
      timeoutMs: 10,
      staleMs: 60_000,
    })).rejects.toThrow(/timed out waiting for shared state lock/i);
    expect(await fs.readFile(lockPath, 'utf8')).toBe('active');
  });

  it('never reaps a stale-looking lock while its recorded owner process is still alive', async () => {
    const lockPath = `${target}.lock`;
    const owner = JSON.stringify({ pid: process.pid, createdAtUtc: new Date(0).toISOString() });
    await fs.writeFile(lockPath, owner, 'utf8');
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);

    await expect(withFileWriteLock(target, async () => 'never', {
      retryDelayMs: 1,
      timeoutMs: 10,
      staleMs: 1,
    })).rejects.toThrow(/timed out waiting for shared state lock/i);
    expect(await fs.readFile(lockPath, 'utf8')).toBe(owner);
  });

  it('cleans only target-specific Browser and WPF atomic temp residue after acquiring the lock', async () => {
    const wpfResidue = path.join(root, '.shared.json.crashed-wpf.tmp');
    const browserResidue = path.join(root, 'shared-crashed-browser.tmp');
    const unrelated = path.join(root, 'other-crashed-browser.tmp');
    await Promise.all([
      fs.writeFile(wpfResidue, 'wpf orphan', 'utf8'),
      fs.writeFile(browserResidue, 'browser orphan', 'utf8'),
      fs.writeFile(unrelated, 'keep', 'utf8'),
    ]);

    await withFileWriteLock(target, async () => {
      await expect(fs.stat(wpfResidue)).rejects.toMatchObject({ code: 'ENOENT' });
      await expect(fs.stat(browserResidue)).rejects.toMatchObject({ code: 'ENOENT' });
      expect(await fs.readFile(unrelated, 'utf8')).toBe('keep');
    });

    expect(await fs.readFile(unrelated, 'utf8')).toBe('keep');
  });
});

const directoryLockProtocol = 'photoviewer.shared-directory-lock/v1';
const deadPid = 2_147_483_647;

function directoryOwner(token: string, pid = deadPid) {
  return {
    protocol: directoryLockProtocol,
    token,
    pid,
    createdAtUtc: new Date(0).toISOString(),
  };
}

async function writeDirectoryOwner(lockPath: string, token: string, pid = deadPid) {
  await fs.mkdir(lockPath);
  const ownerPath = path.join(lockPath, 'owner.json');
  await fs.writeFile(ownerPath, `${JSON.stringify(directoryOwner(token, pid))}\n`, 'utf8');
  const old = new Date(Date.now() - 60_000);
  await fs.utimes(ownerPath, old, old);
  await fs.utimes(lockPath, old, old);
}

describe('withRecoverableDirectoryWriteLock', () => {
  it('serializes writers and removes the generation directory afterward', async () => {
    const events: string[] = [];
    let releaseFirst!: () => void;
    let signalFirstStarted!: () => void;
    const firstCanFinish = new Promise<void>((resolve) => { releaseFirst = resolve; });
    const firstStarted = new Promise<void>((resolve) => { signalFirstStarted = resolve; });
    const first = withRecoverableDirectoryWriteLock(target, async () => {
      events.push('first-start');
      signalFirstStarted();
      await firstCanFinish;
      events.push('first-end');
    }, { retryDelayMs: 1, timeoutMs: 1_000 });
    await firstStarted;
    const second = withRecoverableDirectoryWriteLock(target, async () => {
      events.push('second-start');
    }, { retryDelayMs: 1, timeoutMs: 1_000 });

    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(events).toEqual(['first-start']);
    releaseFirst();
    await Promise.all([first, second]);

    expect(events).toEqual(['first-start', 'first-end', 'second-start']);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock'))).toEqual([]);
  });

  it('retries release when a delayed reaper restores owner.json between read and claim scan', async () => {
    const lockPath = `${target}.lock`;
    const ownerPath = path.join(lockPath, 'owner.json');
    const claimPath = path.join(
      lockPath,
      `.claim.${'a'.repeat(32)}.${deadPid}.${Date.now()}.${'b'.repeat(32)}.json`,
    );
    let restored = false;

    await expect(withRecoverableDirectoryWriteLock(target, async () => {
      await fs.rename(ownerPath, claimPath);
    }, {
      retryDelayMs: 1,
      timeoutMs: 200,
      testHooks: {
        afterReleaseOwnerMissing: async () => {
          if (restored) return;
          restored = true;
          await fs.rename(claimPath, ownerPath);
        },
      },
    })).resolves.toBeUndefined();

    expect(restored).toBe(true);
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock'))).toEqual([]);
  });

  it('recovers a stale exact-generation owner and cleans retired residue', async () => {
    const lockPath = `${target}.lock`;
    await writeDirectoryOwner(lockPath, '11111111111111111111111111111111');

    await expect(withRecoverableDirectoryWriteLock(target, async () => 'recovered', {
      retryDelayMs: 1,
      timeoutMs: 100,
      staleMs: 1,
    })).resolves.toBe('recovered');

    expect((await fs.readdir(root)).filter((name) => name.includes('.lock'))).toEqual([]);
  });

  it('recovers an abandoned exact-generation claim after its claimant died', async () => {
    const lockPath = `${target}.lock`;
    const ownerToken = '22222222222222222222222222222222';
    const claimToken = '33333333333333333333333333333333';
    const claimedAt = Date.now() - 60_000;
    await fs.mkdir(lockPath);
    await fs.writeFile(
      path.join(lockPath, `.claim.${ownerToken}.${deadPid}.${claimedAt}.${claimToken}.json`),
      `${JSON.stringify(directoryOwner(ownerToken))}\n`,
      'utf8',
    );
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);

    await expect(withRecoverableDirectoryWriteLock(target, async () => 'recovered-claim', {
      retryDelayMs: 1,
      timeoutMs: 100,
      staleMs: 1,
    })).resolves.toBe('recovered-claim');
    expect((await fs.readdir(root)).filter((name) => name.includes('.lock'))).toEqual([]);
  });

  it('never recovers an abandoned reaper claim while its payload owner is still alive', async () => {
    const lockPath = `${target}.lock`;
    const inspectedToken = '88888888888888888888888888888888';
    const liveOwnerToken = '99999999999999999999999999999999';
    const claimToken = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    const claimedAt = Date.now() - 60_000;
    const claimName = `.claim.${inspectedToken}.${deadPid}.${claimedAt}.${claimToken}.json`;
    await fs.mkdir(lockPath);
    await fs.writeFile(
      path.join(lockPath, claimName),
      `${JSON.stringify(directoryOwner(liveOwnerToken, process.pid))}\n`,
      'utf8',
    );
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);
    let entered = 0;

    await expect(withRecoverableDirectoryWriteLock(target, async () => {
      entered += 1;
    }, {
      retryDelayMs: 1,
      timeoutMs: 20,
      staleMs: 1,
    })).rejects.toThrow(/timed out waiting for shared state lock/i);

    expect(entered).toBe(0);
    expect(JSON.parse(await fs.readFile(path.join(lockPath, claimName), 'utf8'))).toMatchObject({
      token: liveOwnerToken,
      pid: process.pid,
    });
  });

  it('does not remove a replacement live generation after stale inspection is paused', async () => {
    const lockPath = `${target}.lock`;
    const staleToken = '44444444444444444444444444444444';
    const liveToken = '55555555555555555555555555555555';
    await writeDirectoryOwner(lockPath, staleToken);
    let releaseReaper!: () => void;
    let reportInspected!: () => void;
    const reaperCanContinue = new Promise<void>((resolve) => { releaseReaper = resolve; });
    const inspected = new Promise<void>((resolve) => { reportInspected = resolve; });
    let entered = 0;

    const attempt = withRecoverableDirectoryWriteLock(target, async () => {
      entered += 1;
    }, {
      retryDelayMs: 1,
      timeoutMs: 30,
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
    await writeDirectoryOwner(lockPath, liveToken, process.pid);
    releaseReaper();

    await expect(attempt).rejects.toThrow(/timed out waiting for shared state lock/i);
    expect(entered).toBe(0);
    expect(JSON.parse(await fs.readFile(path.join(lockPath, 'owner.json'), 'utf8'))).toMatchObject({
      token: liveToken,
      pid: process.pid,
    });
  });
});

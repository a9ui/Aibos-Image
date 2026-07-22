import { randomUUID } from 'crypto';
import { promises as fs } from 'fs';
import path from 'path';

export interface FileWriteLockOptions {
  timeoutMs?: number;
  retryDelayMs?: number;
  /**
   * Retained for call-site compatibility. Legacy file locks are deliberately
   * never reaped: a pathname unlink cannot prove it still names the inspected
   * generation.
   */
  staleMs?: number;
}

export interface RecoverableDirectoryWriteLockOptions extends FileWriteLockOptions {
  /** Test-only deterministic pause after a stale owner was inspected. */
  testHooks?: {
    afterStaleOwnerInspected?: (owner: Readonly<SharedDirectoryLockOwner>) => Promise<void>;
    afterOwnerMarkerClaimed?: () => Promise<void>;
    afterReleaseOwnerMissing?: () => Promise<void>;
  };
}

const DEFAULT_TIMEOUT_MS = 2_000;
const DEFAULT_RETRY_DELAY_MS = 25;
const DEFAULT_STALE_MS = 30_000;
const DIRECTORY_LOCK_PROTOCOL = 'photoviewer.shared-directory-lock/v1';
const DIRECTORY_LOCK_OWNER = 'owner.json';
const LOCK_TOKEN_PATTERN = /^[0-9a-f]{32}$/i;
const CLAIM_NAME_PATTERN = /^\.claim\.([0-9a-f]{32})\.([1-9][0-9]*)\.([0-9]+)\.([0-9a-f]{32})\.json$/i;

export interface SharedDirectoryLockOwner {
  protocol: typeof DIRECTORY_LOCK_PROTOCOL;
  token: string;
  pid: number;
  createdAtUtc: string;
}

const inProcessLockTails = new Map<string, Promise<unknown>>();

function delay(ms: number) {
  return new Promise<void>((resolve) => setTimeout(resolve, ms));
}

function inProcessLockKey(lockPath: string) {
  const resolved = path.resolve(lockPath);
  return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

async function withInProcessLockTurn<T>(lockPath: string, action: () => Promise<T>): Promise<T> {
  const key = inProcessLockKey(lockPath);
  const previous = inProcessLockTails.get(key) ?? Promise.resolve();
  const current = previous.catch(() => undefined).then(action);
  inProcessLockTails.set(key, current);

  try {
    return await current;
  } finally {
    if (inProcessLockTails.get(key) === current) {
      inProcessLockTails.delete(key);
    }
  }
}

function newLockToken() {
  return randomUUID().replaceAll('-', '');
}

function parseDirectoryLockOwner(value: unknown): SharedDirectoryLockOwner | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const candidate = value as Record<string, unknown>;
  if (candidate.protocol !== DIRECTORY_LOCK_PROTOCOL
    || typeof candidate.token !== 'string'
    || !LOCK_TOKEN_PATTERN.test(candidate.token)
    || typeof candidate.pid !== 'number'
    || !Number.isSafeInteger(candidate.pid)
    || candidate.pid <= 0
    || typeof candidate.createdAtUtc !== 'string'
    || !Number.isFinite(Date.parse(candidate.createdAtUtc))) {
    return null;
  }
  return candidate as unknown as SharedDirectoryLockOwner;
}

async function readDirectoryLockOwner(ownerPath: string) {
  try {
    return parseDirectoryLockOwner(JSON.parse(await fs.readFile(ownerPath, 'utf8')));
  } catch {
    return null;
  }
}

function hasLiveProcess(pid: number) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM means the process exists but this process may not signal it.
    // Unknown platform errors fail closed so recovery cannot create a second
    // owner when process liveness is uncertain.
    return (error as NodeJS.ErrnoException)?.code !== 'ESRCH';
  }
}

function directoryClaimName(ownerToken: string, claimToken: string) {
  return `.claim.${ownerToken}.${process.pid}.${Date.now()}.${claimToken}.json`;
}

async function restoreClaimToOwner(claimPath: string, ownerPath: string) {
  try {
    await fs.rename(claimPath, ownerPath);
  } catch {
    // A failed restore is deliberately fail-closed. The abandoned claim can be
    // recovered later only after its claimant is proven dead and stale.
  }
}

async function retireClaimedDirectory(
  lockPath: string,
  claimPath: string,
  ownerToken: string,
  claimToken: string,
) {
  const retiredPath = `${lockPath}.retired.${ownerToken}.${claimToken}`;
  try {
    await fs.rename(lockPath, retiredPath);
  } catch {
    await restoreClaimToOwner(claimPath, path.join(lockPath, DIRECTORY_LOCK_OWNER));
    return false;
  }

  // The generic lock pathname is now absent and can be acquired by the next
  // writer. Cleanup addresses only the generation-specific retired directory,
  // so it cannot remove a replacement owner.
  await fs.rm(retiredPath, { recursive: true, force: true }).catch(() => {});
  return true;
}

async function claimOwnerAndRetire(
  lockPath: string,
  expectedOwnerToken: string,
  testHooks?: RecoverableDirectoryWriteLockOptions['testHooks'],
) {
  const ownerPath = path.join(lockPath, DIRECTORY_LOCK_OWNER);
  const claimToken = newLockToken();
  const claimPath = path.join(lockPath, directoryClaimName(expectedOwnerToken, claimToken));
  try {
    // Renaming the marker inside the non-empty lock directory is the atomic
    // generation claim. Until the directory itself is retired, no replacement
    // lock directory can occupy lockPath.
    await fs.rename(ownerPath, claimPath);
    await testHooks?.afterOwnerMarkerClaimed?.();
  } catch (error) {
    return (error as NodeJS.ErrnoException)?.code === 'ENOENT'
      && !await fs.stat(lockPath).then(() => true).catch(() => false);
  }

  const claimedOwner = await readDirectoryLockOwner(claimPath);
  if (!claimedOwner || claimedOwner.token !== expectedOwnerToken) {
    await restoreClaimToOwner(claimPath, ownerPath);
    return false;
  }
  return retireClaimedDirectory(lockPath, claimPath, expectedOwnerToken, claimToken);
}

async function recoverAbandonedClaim(lockPath: string, staleMs: number) {
  let names: string[];
  try {
    names = await fs.readdir(lockPath);
  } catch (error) {
    return (error as NodeJS.ErrnoException)?.code === 'ENOENT';
  }
  const claims = names.filter((name) => CLAIM_NAME_PATTERN.test(name));
  if (claims.length !== 1 || names.length !== 1) return false;
  const match = CLAIM_NAME_PATTERN.exec(claims[0]);
  if (!match) return false;
  const [, , claimantPidText, claimedAtText] = match;
  const claimantPid = Number(claimantPidText);
  const claimedAt = Number(claimedAtText);
  if (!Number.isSafeInteger(claimedAt)
    || Date.now() - claimedAt <= staleMs
    || hasLiveProcess(claimantPid)) {
    return false;
  }

  const oldClaimPath = path.join(lockPath, claims[0]);
  const owner = await readDirectoryLockOwner(oldClaimPath);
  // A crashed delayed reaper may have claimed a replacement live owner's
  // marker before it noticed the token mismatch. Never retire that generation
  // while the payload's actual owner is still alive.
  if (!owner || hasLiveProcess(owner.pid)) return false;
  const claimToken = newLockToken();
  const newClaimPath = path.join(lockPath, directoryClaimName(owner.token, claimToken));
  try {
    // Only one recovery contender can move the abandoned marker.
    await fs.rename(oldClaimPath, newClaimPath);
  } catch {
    return false;
  }
  return retireClaimedDirectory(lockPath, newClaimPath, owner.token, claimToken);
}

async function recoverStaleDirectoryLock(
  lockPath: string,
  staleMs: number,
  testHooks?: RecoverableDirectoryWriteLockOptions['testHooks'],
) {
  let lockStat;
  try {
    lockStat = await fs.stat(lockPath);
  } catch (error) {
    return (error as NodeJS.ErrnoException)?.code === 'ENOENT';
  }
  // Legacy file locks and unexpected objects are never pathname-reaped.
  if (!lockStat.isDirectory() || Date.now() - lockStat.mtimeMs <= staleMs) return false;

  const ownerPath = path.join(lockPath, DIRECTORY_LOCK_OWNER);
  const ownerStat = await fs.stat(ownerPath).catch(() => null);
  if (!ownerStat) {
    const recoveredClaim = await recoverAbandonedClaim(lockPath, staleMs);
    if (recoveredClaim) return true;
    // An empty, stale initialization directory can be removed safely: a
    // replacement compliant lock is pre-populated and therefore non-empty.
    const names = await fs.readdir(lockPath).catch(() => null);
    if (names?.length === 0) {
      return fs.rmdir(lockPath).then(() => true).catch(() => false);
    }
    return false;
  }
  if (Date.now() - ownerStat.mtimeMs <= staleMs) return false;
  const owner = await readDirectoryLockOwner(ownerPath);
  if (!owner || hasLiveProcess(owner.pid)) return false;

  await testHooks?.afterStaleOwnerInspected?.(owner);
  return claimOwnerAndRetire(lockPath, owner.token, testHooks);
}

async function releaseOwnedDirectoryLock(
  lockPath: string,
  ownerToken: string,
  timeoutMs = DEFAULT_TIMEOUT_MS,
  testHooks?: RecoverableDirectoryWriteLockOptions['testHooks'],
) {
  const deadline = Date.now() + timeoutMs;
  while (true) {
    if (!await fs.stat(lockPath).then(() => true).catch(() => false)) return true;
    const owner = await readDirectoryLockOwner(path.join(lockPath, DIRECTORY_LOCK_OWNER));
    if (owner) {
      if (owner.token !== ownerToken) return false;
      if (await claimOwnerAndRetire(lockPath, ownerToken)) return true;
    } else {
      // A delayed stale reaper may have temporarily claimed this live owner's
      // marker using the inspected generation's filename. Wait for that
      // claimant to restore it; returning here would strand a live lock after
      // the owner's critical section has already ended.
      await testHooks?.afterReleaseOwnerMissing?.();
      const names = await fs.readdir(lockPath).catch(() => [] as string[]);
      const ownedClaim = await Promise.all(names
        .filter((name) => CLAIM_NAME_PATTERN.test(name))
        .map(async (name) => ({
          name,
          owner: await readDirectoryLockOwner(path.join(lockPath, name)),
        })))
        .then((claims) => claims.find((claim) => claim.owner?.token === ownerToken));
      if (ownedClaim) {
        const claimToken = newLockToken();
        const ownedClaimPath = path.join(lockPath, ownedClaim.name);
        const releaseClaimPath = path.join(lockPath, directoryClaimName(ownerToken, claimToken));
        try {
          // This process is the payload owner and its critical section has ended.
          // Taking the exact marker from a delayed/crashed reaper is therefore
          // safe, and prevents a live process from being stranded behind Busy.
          await fs.rename(ownedClaimPath, releaseClaimPath);
          if (await retireClaimedDirectory(lockPath, releaseClaimPath, ownerToken, claimToken)) return true;
        } catch {
          // The reaper may have restored the marker between scan and rename.
          // Retry and claim owner.json normally.
        }
      }
    }
    if (Date.now() >= deadline) return false;
    await delay(Math.min(DEFAULT_RETRY_DELAY_MS, Math.max(1, deadline - Date.now())));
  }
}

async function removeOrphanedAtomicTemps(target: string) {
  const dir = path.dirname(target);
  const fileName = path.basename(target);
  const browserPrefix = `${path.basename(target, path.extname(target))}-`;
  let names: string[];
  try {
    names = await fs.readdir(dir);
  } catch {
    return;
  }

  await Promise.all(names
    .filter((name) => name.endsWith('.tmp')
      && (name.startsWith(`.${fileName}.`) || name.startsWith(browserPrefix)))
    .map((name) => fs.unlink(path.join(dir, name)).catch(() => {})));
}

async function removeRetiredDirectoryLocks(lockPath: string) {
  const dir = path.dirname(lockPath);
  const prefix = `${path.basename(lockPath)}.retired.`;
  let names: string[];
  try {
    names = await fs.readdir(dir);
  } catch {
    return;
  }
  await Promise.all(names
    .filter((name) => name.startsWith(prefix))
    .map((name) => fs.rm(path.join(dir, name), { recursive: true, force: true }).catch(() => {})));
}

/**
 * Serializes read/merge/replace operations across Browser and WPF processes.
 * The shared legacy protocol is a create-new `<target>.lock` file. Stale
 * pathname deletion is intentionally disabled because the path may have been
 * replaced after inspection. Callers that require automatic crash recovery
 * must use `withRecoverableDirectoryWriteLock`.
 */
export async function withFileWriteLock<T>(
  target: string,
  action: () => Promise<T>,
  options: FileWriteLockOptions = {},
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const retryDelayMs = options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS;
  const lockPath = `${target}.lock`;

  // A process-local FIFO prevents same-runtime callers from racing each other
  // and starving behind the timeout. Every turn still acquires the shared
  // create-new file lock, so Browser/WPF and other-process safety is unchanged.
  return withInProcessLockTurn(lockPath, async () => {
    // Time only this caller's shared-file-lock acquisition. Waiting for an
    // earlier caller in this process must not consume its cross-process budget.
    const startedAt = Date.now();
    await fs.mkdir(path.dirname(target), { recursive: true });

    while (true) {
      let handle: Awaited<ReturnType<typeof fs.open>> | undefined;
      try {
        handle = await fs.open(lockPath, 'wx');
        await handle.writeFile(`${JSON.stringify({ pid: process.pid, createdAtUtc: new Date().toISOString() })}\n`, 'utf8');
        // Owning the shared lock proves no compliant writer can still own a
        // target-specific temp. Remove crash residue from either runtime before
        // beginning the next read/merge/replace transaction.
        await removeOrphanedAtomicTemps(target);
      } catch (error) {
        if (handle) {
          await handle.close().catch(() => {});
          await fs.unlink(lockPath).catch(() => {});
        }
        if ((error as NodeJS.ErrnoException)?.code !== 'EEXIST') throw error;

        if (Date.now() - startedAt >= timeoutMs) {
          throw new Error(`Timed out waiting for shared state lock: ${path.basename(lockPath)}`);
        }
        await delay(retryDelayMs);
        continue;
      }

      try {
        return await action();
      } finally {
        await handle!.close().catch(() => {});
        await fs.unlink(lockPath).catch(() => {});
      }
    }
  });
}

/**
 * Shared Browser/WPF Album lock protocol.
 *
 * `<target>.lock` is a non-empty directory published atomically from a
 * generation-specific candidate. Release/recovery first atomically renames
 * `owner.json` to a generation claim inside that directory, then atomically
 * moves the whole directory to a generation-specific retired pathname. A
 * delayed reaper can therefore never delete a replacement live generation.
 */
export async function withRecoverableDirectoryWriteLock<T>(
  target: string,
  action: () => Promise<T>,
  options: RecoverableDirectoryWriteLockOptions = {},
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const retryDelayMs = options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS;
  const staleMs = options.staleMs ?? DEFAULT_STALE_MS;
  const lockPath = `${target}.lock`;

  return withInProcessLockTurn(lockPath, async () => {
    const startedAt = Date.now();
    await fs.mkdir(path.dirname(target), { recursive: true });

    while (true) {
      const ownerToken = newLockToken();
      const candidatePath = `${lockPath}.candidate.${ownerToken}`;
      const owner: SharedDirectoryLockOwner = {
        protocol: DIRECTORY_LOCK_PROTOCOL,
        token: ownerToken,
        pid: process.pid,
        createdAtUtc: new Date().toISOString(),
      };
      let acquired = false;
      try {
        await fs.mkdir(candidatePath);
        await fs.writeFile(
          path.join(candidatePath, DIRECTORY_LOCK_OWNER),
          `${JSON.stringify(owner)}\n`,
          { encoding: 'utf8', flag: 'wx' },
        );
        await fs.rename(candidatePath, lockPath);
        acquired = true;
      } catch (error) {
        await fs.rm(candidatePath, { recursive: true, force: true }).catch(() => {});
        const code = (error as NodeJS.ErrnoException)?.code;
        if (!['EEXIST', 'EPERM', 'EACCES', 'ENOTEMPTY'].includes(code ?? '')) throw error;
      }

      if (!acquired) {
        await recoverStaleDirectoryLock(lockPath, staleMs, options.testHooks);
        if (Date.now() - startedAt >= timeoutMs) {
          throw new Error(`Timed out waiting for shared state lock: ${path.basename(lockPath)}`);
        }
        await delay(retryDelayMs);
        continue;
      }

      await Promise.all([
        removeOrphanedAtomicTemps(target),
        removeRetiredDirectoryLocks(lockPath),
      ]);
      try {
        return await action();
      } finally {
        await releaseOwnedDirectoryLock(lockPath, ownerToken, timeoutMs, options.testHooks);
      }
    }
  });
}

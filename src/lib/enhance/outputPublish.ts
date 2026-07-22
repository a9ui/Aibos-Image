import fs from 'fs';

const DEFAULT_WINDOWS_RETRY_DELAYS_MS = [25, 50, 100, 200, 400, 800, 1600] as const;

export interface EnhancementOutputPublishDependencies {
  rename: (source: string, destination: string) => Promise<void>;
  copyFile: (source: string, destination: string) => Promise<void>;
  remove: (target: string) => Promise<void>;
  wait: (delayMs: number) => Promise<void>;
}

const defaultDependencies: EnhancementOutputPublishDependencies = {
  rename: (source, destination) => fs.promises.rename(source, destination),
  copyFile: (source, destination) => fs.promises.copyFile(source, destination),
  remove: (target) => fs.promises.rm(target, { force: true }),
  wait: (delayMs) => new Promise((resolve) => setTimeout(resolve, delayMs)),
};

export function isTransientEnhancementPublishError(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const code = (error as NodeJS.ErrnoException).code;
  return code === 'EBUSY' || code === 'EPERM' || code === 'EACCES';
}

/**
 * Publishes a completed enhancement without exposing the adapter's temporary
 * path. Windows scanners, indexers, and recently closed decoders can briefly
 * retain a file handle after Sharp finishes. Retry that narrow failure class,
 * then use a fully awaited copy. The queue marks the job succeeded only after
 * this function returns, so a copied destination is not served mid-write.
 */
export async function publishEnhancementOutput(
  temporaryPath: string,
  destinationPath: string,
  options: {
    dependencies?: Partial<EnhancementOutputPublishDependencies>;
    retryDelaysMs?: readonly number[];
    cleanupRetryDelaysMs?: readonly number[];
  } = {},
): Promise<'rename' | 'copy' | 'copy-with-stale-temporary'> {
  const dependencies = { ...defaultDependencies, ...options.dependencies };
  const retryDelaysMs = options.retryDelaysMs ?? DEFAULT_WINDOWS_RETRY_DELAYS_MS;
  const cleanupRetryDelaysMs = options.cleanupRetryDelaysMs ?? retryDelaysMs;
  let lastTransientError: unknown = null;

  for (let attempt = 0; ; attempt += 1) {
    try {
      await dependencies.rename(temporaryPath, destinationPath);
      return 'rename';
    } catch (error) {
      if (!isTransientEnhancementPublishError(error)) throw error;
      lastTransientError = error;
      if (attempt >= retryDelaysMs.length) break;
      await dependencies.wait(retryDelaysMs[attempt]);
    }
  }

  try {
    await dependencies.copyFile(temporaryPath, destinationPath);
  } catch (copyError) {
    const error = new Error(
      `Could not publish the enhanced output: rename remained locked after retries and the copy fallback failed: ${
        copyError instanceof Error ? copyError.message : String(copyError)
      }`,
      { cause: copyError },
    );
    (error as NodeJS.ErrnoException).code = (copyError as NodeJS.ErrnoException)?.code;
    (error as NodeJS.ErrnoException & { renameError?: unknown }).renameError = lastTransientError;
    throw error;
  }

  // The destination is fully published once copyFile resolves. Cleanup is a
  // separate best-effort concern: a decoder or scanner can keep the source
  // temporary open on Windows even though the completed destination is safe
  // to use. Retry that residue without turning a valid output into a failed
  // job, and report when a later sweep still needs to remove it.
  for (let attempt = 0; ; attempt += 1) {
    try {
      await dependencies.remove(temporaryPath);
      return 'copy';
    } catch (cleanupError) {
      if (
        !isTransientEnhancementPublishError(cleanupError) ||
        attempt >= cleanupRetryDelaysMs.length
      ) {
        return 'copy-with-stale-temporary';
      }
      await dependencies.wait(cleanupRetryDelaysMs[attempt]);
    }
  }
}

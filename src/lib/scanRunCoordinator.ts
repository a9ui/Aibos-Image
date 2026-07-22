import path from 'path';

const activeScanKeys = new Set<string>();

/**
 * Folder ordering affects the viewer's folder labels, but it must not allow
 * two scans of the same roots to mutate the same cache files concurrently.
 */
export function canonicalScanFolderSet(dirs: readonly string[]): string {
  return Array.from(new Set(dirs.map((dir) => path.resolve(dir).toLowerCase())))
    .sort()
    .join('\n');
}

export function reserveScanRun(dirs: readonly string[]): (() => void) | null {
  const key = canonicalScanFolderSet(dirs);
  if (activeScanKeys.has(key)) return null;
  activeScanKeys.add(key);

  let released = false;
  return () => {
    if (released) return;
    released = true;
    activeScanKeys.delete(key);
  };
}

export function resetScanRunsForTests() {
  activeScanKeys.clear();
}

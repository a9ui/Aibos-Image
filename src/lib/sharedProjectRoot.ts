import fs from 'fs';
import path from 'path';

function findProjectRoot(start: string) {
  let current = path.resolve(start);
  while (true) {
    if (fs.existsSync(path.join(current, 'project.toml'))) return current;
    const parent = path.dirname(current);
    if (parent === current) return null;
    current = parent;
  }
}

/**
 * Return the checkout that owns user-visible shared state. Linked worktrees
 * point at the main checkout's common Git directory; storing state beside the
 * worktree would otherwise create an invisible second Favorite history.
 */
export function resolveSharedProjectRoot(start = process.cwd()) {
  const projectRoot = findProjectRoot(start) ?? path.resolve(start);
  const dotGitPath = path.join(projectRoot, '.git');
  try {
    if (fs.statSync(dotGitPath).isDirectory()) return projectRoot;
  } catch {
    return projectRoot;
  }

  try {
    const pointer = fs.readFileSync(dotGitPath, 'utf8').split(/\r?\n/, 1)[0]?.trim() ?? '';
    const match = /^gitdir:\s*(.+)$/i.exec(pointer);
    if (!match) return projectRoot;

    const gitDir = path.resolve(projectRoot, match[1]);
    const commonDirValue = fs.readFileSync(path.join(gitDir, 'commondir'), 'utf8').split(/\r?\n/, 1)[0]?.trim();
    if (!commonDirValue) return projectRoot;

    const commonGitDir = path.resolve(gitDir, commonDirValue);
    const mainCheckout = path.dirname(commonGitDir);
    return fs.existsSync(path.join(mainCheckout, 'project.toml')) ? mainCheckout : projectRoot;
  } catch {
    return projectRoot;
  }
}

export type SharedCacheEntry = 'favorites.json' | 'seen.json' | 'recent-folders.json' | 'search-history.json' | 'albums.json' | 'settings.json' | 'enhance';

export function resolveSharedCachePath(fileName: SharedCacheEntry, overridePath?: string, start = process.cwd()) {
  if (overridePath) return path.resolve(overridePath);

  const projectRoot = resolveSharedProjectRoot(start);
  // Keep every supported suffix literal. Turbopack otherwise treats a dynamic
  // cache child as a broad filesystem pattern and scans the full thumbnail
  // cache in a real user checkout during production builds.
  switch (fileName) {
    case 'favorites.json':
      return path.join(projectRoot, '.cache', 'favorites.json');
    case 'seen.json':
      return path.join(projectRoot, '.cache', 'seen.json');
    case 'recent-folders.json':
      return path.join(projectRoot, '.cache', 'recent-folders.json');
    case 'search-history.json':
      return path.join(projectRoot, '.cache', 'search-history.json');
    case 'albums.json':
      return path.join(projectRoot, '.cache', 'albums.json');
    case 'settings.json':
      return path.join(projectRoot, '.cache', 'settings.json');
    case 'enhance':
      return path.join(projectRoot, '.cache', 'enhance');
  }
}

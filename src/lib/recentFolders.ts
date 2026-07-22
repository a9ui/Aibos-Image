import { formatDirSet, parseDirSet } from './pathSet';

export type SharedRecentFolders = {
  version: 1;
  lastFolderSet: string[];
  recentFolderSets: string[][];
  updatedAtUtc: string;
};

export type RecentFolderMemory = {
  recentDirs: string[];
  lastDirSet: string;
};

export const MAX_RECENT_FOLDER_SETS = 12;

function normalizeFolderSet(value: unknown): string[] {
  if (Array.isArray(value)) {
    return parseDirSet(value.filter((item): item is string => typeof item === 'string').join('\n'));
  }
  if (typeof value === 'string') {
    return parseDirSet(value);
  }
  return [];
}

function normalizeRecentFolderSets(value: unknown): string[][] {
  if (!Array.isArray(value)) return [];

  const seen = new Set<string>();
  const sets: string[][] = [];
  for (const rawSet of value) {
    const folderSet = normalizeFolderSet(rawSet);
    if (folderSet.length === 0) continue;
    const key = formatDirSet(folderSet).toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    sets.push(folderSet);
    if (sets.length >= MAX_RECENT_FOLDER_SETS) break;
  }
  return sets;
}

/**
 * Normalize the browser-local representation without discarding valid user
 * entries. Folder spelling and in-set order come from the first (newest)
 * occurrence; set identity is case-insensitive and capped to the UI limit.
 */
export function normalizeRecentFolderMemory(value: unknown): RecentFolderMemory {
  const source = value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
  const rawRecentDirs = Array.isArray(source.recentDirs) ? source.recentDirs : [];
  const recentFolderSets = normalizeRecentFolderSets(rawRecentDirs);
  const lastFolderSet = normalizeFolderSet(source.lastDirSet);
  return {
    recentDirs: recentFolderSets.map((folderSet) => formatDirSet(folderSet)),
    lastDirSet: formatDirSet(lastFolderSet),
  };
}

/** Keep a completed folder set newest-first while preserving all other sets. */
export function rememberRecentFolderSet(
  current: RecentFolderMemory,
  folderSet: string
): RecentFolderMemory {
  const normalizedCurrent = normalizeRecentFolderMemory(current);
  const normalizedFolderSet = formatDirSet(parseDirSet(folderSet));
  if (!normalizedFolderSet) return normalizedCurrent;
  return normalizeRecentFolderMemory({
    lastDirSet: normalizedFolderSet,
    recentDirs: [normalizedFolderSet, ...normalizedCurrent.recentDirs],
  });
}

/** Merge additive legacy/shared memory without changing caller-defined recency. */
export function mergeRecentFolderMemories(
  preferred: RecentFolderMemory,
  additional: RecentFolderMemory
): RecentFolderMemory {
  const primary = normalizeRecentFolderMemory(preferred);
  const fallback = normalizeRecentFolderMemory(additional);
  return normalizeRecentFolderMemory({
    recentDirs: [...primary.recentDirs, ...fallback.recentDirs],
    lastDirSet: primary.lastDirSet || fallback.lastDirSet,
  });
}

export function normalizeSharedRecentFolders(value: unknown, now = new Date().toISOString()): SharedRecentFolders {
  const source = value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
  const lastFolderSet = normalizeFolderSet(source.lastFolderSet);
  const recentFolderSets = normalizeRecentFolderSets(source.recentFolderSets);
  const updatedAtUtc = typeof source.updatedAtUtc === 'string' && source.updatedAtUtc
    ? source.updatedAtUtc
    : now;

  return {
    version: 1,
    lastFolderSet,
    recentFolderSets,
    updatedAtUtc,
  };
}

export function buildSharedRecentFolders(memory: RecentFolderMemory, now = new Date().toISOString()): SharedRecentFolders {
  const normalizedMemory = normalizeRecentFolderMemory(memory);
  const lastFolderSet = parseDirSet(normalizedMemory.lastDirSet);
  const recentFolderSets = normalizeRecentFolderSets(
    normalizedMemory.recentDirs.map((item) => parseDirSet(item))
  );
  const mergedRecent = normalizeRecentFolderSets([
    lastFolderSet,
    ...recentFolderSets,
  ]);

  return {
    version: 1,
    lastFolderSet,
    recentFolderSets: mergedRecent,
    updatedAtUtc: now,
  };
}

export function sharedRecentToLocalMemory(shared: SharedRecentFolders): RecentFolderMemory {
  return normalizeRecentFolderMemory({
    recentDirs: shared.recentFolderSets.map((folderSet) => formatDirSet(folderSet)),
    lastDirSet: formatDirSet(shared.lastFolderSet),
  });
}

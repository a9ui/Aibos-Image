import fs from 'fs';
import path from 'path';
import fg from 'fast-glob';
import crypto from 'crypto';
import { extractSDMetadata } from './pngParser';
import type { CacheData, ImageFile, SDMetadata } from './types';
import { IMAGE_GLOB_EXTENSIONS } from './imageFormats';
import { basenameFromPath, parseDirSet } from './pathSet';
import { shouldReportScanProgress } from './scanProgress';

const CACHE_DIR = path.join(/*turbopackIgnore: true*/ process.cwd(), '.cache');
const CACHE_VERSION = 1;
const FOLDER_SIGNATURE_CACHE_VERSION = 2;
const MAX_TAG_RESULTS = 2000;
const MAX_SEARCH_CACHE_ENTRIES = 8;
const ROOT_FOLDER_KEY = '__ROOT__';
const OUTSIDE_FOLDER_KEY = '__OUTSIDE__';
const SCAN_ROOT_KEY = '__SCAN_ROOT__';
const RECENT_SCAN_TARGET_VERIFY_MS = 48 * 60 * 60 * 1000;

type SortBy = 'newest' | 'oldest' | 'created-newest' | 'created-oldest' | 'name' | 'random';

type ScanStage = 'preparing' | 'scanning';

type ScanProgressStatus = {
  stage?: ScanStage;
  message?: string;
};

type ScanTarget = {
  key: string;
  absPath: string;
  recursive: boolean;
  mtime: number;
};

interface FolderBucket {
  key: string;
  label: string;
  count: number;
}

function ensureDir(dir: string) {
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
}

function cacheFilePath(dirPath: string): string {
  const hash = crypto.createHash('md5').update(dirPath.toLowerCase()).digest('hex');
  return path.join(/*turbopackIgnore: true*/ CACHE_DIR, `index_${hash}.json`);
}

function folderSignatureFilePath(dirPath: string): string {
  const hash = crypto.createHash('md5').update(dirPath.toLowerCase()).digest('hex');
  return path.join(/*turbopackIgnore: true*/ CACHE_DIR, `folders_${hash}.json`);
}

const cacheByDir = new Map<string, CacheData>();
const folderSignaturesByDir = new Map<string, Record<string, string>>();

function normalizeFolderSignatures(value: unknown): Record<string, string> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  const normalized: Record<string, string> = {};
  for (const [key, raw] of Object.entries(value)) {
    if (!key) continue;
    if (typeof raw === 'string' || typeof raw === 'number') {
      normalized[key] = String(raw);
    }
  }
  return normalized;
}

function loadFolderSignatures(dirPath: string): Record<string, string> {
  const memorySignatures = folderSignaturesByDir.get(dirPath);
  if (memorySignatures) return memorySignatures;

  const p = folderSignatureFilePath(dirPath);
  if (fs.existsSync(/*turbopackIgnore: true*/ p)) {
    try {
      const data = JSON.parse(fs.readFileSync(/*turbopackIgnore: true*/ p, 'utf-8')) as {
        version?: number;
        folders?: Record<string, string | number>;
      };
      if (data.version === FOLDER_SIGNATURE_CACHE_VERSION && data.folders && typeof data.folders === 'object') {
        const normalized = normalizeFolderSignatures(data.folders);
        folderSignaturesByDir.set(dirPath, normalized);
        return normalized;
      }
    } catch {
      // corrupted folder signature cache, rebuild it from the filesystem
    }
  }

  const emptySignatures: Record<string, string> = {};
  folderSignaturesByDir.set(dirPath, emptySignatures);
  return emptySignatures;
}

function saveFolderSignatures(dirPath: string, folders: Record<string, string>) {
  ensureDir(CACHE_DIR);
  const p = folderSignatureFilePath(dirPath);
  fs.writeFileSync(/*turbopackIgnore: true*/ p, JSON.stringify({
    version: FOLDER_SIGNATURE_CACHE_VERSION,
    dirPath,
    updatedAt: new Date().toISOString(),
    folders,
  }), 'utf-8');
  folderSignaturesByDir.set(dirPath, folders);
}

function toGlobPath(value: string) {
  return fg.escapePath(value.replace(/\\/g, '/'));
}

function buildFolderSignature(baseMtime: number, nestedDirectories: Array<{ path: string; mtime: number }>) {
  const hash = crypto.createHash('sha1');
  hash.update(String(Math.trunc(baseMtime)));
  for (const directory of nestedDirectories) {
    hash.update('\0');
    hash.update(directory.path.toLowerCase());
    hash.update('\0');
    hash.update(String(Math.trunc(directory.mtime)));
  }
  return `${Math.trunc(baseMtime)}:${nestedDirectories.length}:${hash.digest('base64url')}`;
}

function isRecentlyActiveScanTarget(mtime: number, now: number) {
  return Number.isFinite(mtime) && mtime > 0 && now - mtime >= 0 && now - mtime <= RECENT_SCAN_TARGET_VERIFY_MS;
}

async function buildScanTargetSignature(
  rootDir: string,
  target: ScanTarget,
  signal?: AbortSignal
): Promise<{ signature: string; failed: boolean }> {
  throwIfScanAborted(signal);
  const nested: Array<{ path: string; mtime: number }> = [];
  if (!target.recursive) {
    return { signature: buildFolderSignature(target.mtime, nested), failed: false };
  }
  try {
    const directories = (await fg(`${toGlobPath(target.absPath)}/**`, {
      absolute: true,
      caseSensitiveMatch: false,
      onlyDirectories: true,
      stats: true,
      followSymbolicLinks: false,
    })) as Array<string | { path: string; stats?: fs.Stats }>;
    throwIfScanAborted(signal);

    for (const entry of directories) {
      throwIfScanAborted(signal);
      const entryPath = typeof entry === 'string' ? entry : entry.path;
      const absPath = path.resolve(entryPath);
      const rel = path.relative(rootDir, absPath);
      const parts = rel.split(/[\\/]+/).filter(Boolean);
      if (parts.length <= 1) continue;

      const stat = typeof entry === 'string' ? fs.statSync(absPath) : entry.stats;
      if (!stat) continue;
      nested.push({
        path: path.relative(rootDir, absPath),
        mtime: stat.mtimeMs,
      });
    }
  } catch (error) {
    if (isScanAbortedError(error)) throw error;
    // Missing permission or a transient directory read failure should not break scan.
    // The target folder mtime still gives the fast path a conservative baseline.
    return { signature: buildFolderSignature(target.mtime, nested), failed: true };
  }

  nested.sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: 'base' }));
  return { signature: buildFolderSignature(target.mtime, nested), failed: false };
}

function loadCache(dirPath: string): CacheData {
  const memoryCache = cacheByDir.get(dirPath);
  if (memoryCache) return memoryCache;

  const p = cacheFilePath(dirPath);
  if (fs.existsSync(/*turbopackIgnore: true*/ p)) {
    try {
      const data = JSON.parse(fs.readFileSync(/*turbopackIgnore: true*/ p, 'utf-8')) as CacheData;
      if (data.version === CACHE_VERSION) {
        cacheByDir.set(dirPath, data);
        return data;
      }
    } catch {
      // corrupted cache, start fresh
    }
  }
  const emptyCache = { version: CACHE_VERSION, dirPath, files: {}, lastScan: '' };
  cacheByDir.set(dirPath, emptyCache);
  return emptyCache;
}

function cloneCache(cache: CacheData): CacheData {
  return {
    ...cache,
    files: Object.fromEntries(
      Object.entries(cache.files).map(([filePath, entry]) => [filePath, { ...entry }])
    ),
  };
}

function saveCache(cache: CacheData) {
  ensureDir(CACHE_DIR);
  const p = cacheFilePath(cache.dirPath);
  fs.writeFileSync(/*turbopackIgnore: true*/ p, JSON.stringify(cache), 'utf-8');
  cacheByDir.set(cache.dirPath, cache);
}

function toSearchMetadata(metadata: SDMetadata | null): SDMetadata | null {
  if (!metadata) return null;
  return {
    prompt: metadata.prompt ?? '',
    negativePrompt: metadata.negativePrompt ?? '',
    settings: metadata.settings ?? {},
  };
}

export type ScanCallback = (
  processed: number,
  total: number,
  newFiles: number,
  status?: ScanProgressStatus
) => void;
export interface ScanOptions {
  forceFull?: boolean;
  signal?: AbortSignal;
}

export class ScanAbortedError extends Error {
  constructor() {
    super('Scan aborted');
    this.name = 'ScanAbortedError';
  }
}

export function isScanAbortedError(error: unknown): error is ScanAbortedError {
  return error instanceof ScanAbortedError;
}

function throwIfScanAborted(signal?: AbortSignal) {
  if (signal?.aborted) throw new ScanAbortedError();
}

/**
 * Incrementally scan a directory for PNG files and extract SD metadata.
 * Only parses files whose mtime/size changed and removes stale entries.
 */
export async function scanDirectory(
  dirPath: string,
  onProgress?: ScanCallback,
  options: ScanOptions = {}
): Promise<ImageFile[]> {
  throwIfScanAborted(options.signal);
  const normalised = path.resolve(dirPath);
  // Keep mutations private until the scan has completed. An aborted scan must
  // not leak a partial snapshot into the next scan through the memory cache.
  const cache = cloneCache(loadCache(normalised));
  const folderSignatures = loadFolderSignatures(normalised);
  const nextFolderSignatures: Record<string, string> = {};
  const cachedByScanKey = new Map<string, string[]>();
  for (const cachedPath of Object.keys(cache.files)) {
    const rel = path.relative(normalised, cachedPath);
    if (rel.startsWith('..') || path.isAbsolute(rel)) continue;
    const parts = rel.split(/[\\/]+/).filter(Boolean);
    const scanKey = parts.length <= 1 ? SCAN_ROOT_KEY : path.resolve(normalised, parts[0]);
    const bucket = cachedByScanKey.get(scanKey) ?? [];
    bucket.push(cachedPath);
    cachedByScanKey.set(scanKey, bucket);
  }

  const scanTargets: ScanTarget[] = [];
  const currentScanKeys = new Set<string>();
  const changedScanKeys = new Set<string>();
  const failedScanKeys = new Set<string>();
  let signatureChanged = false;
  let skippedCachedFiles = 0;

  const rootStat = fs.statSync(normalised);
  throwIfScanAborted(options.signal);
  if (!rootStat.isDirectory()) {
    throw new Error(`Scan target is not a directory: ${normalised}`);
  }
  const rootEntries = fs.readdirSync(normalised, { withFileTypes: true });

  scanTargets.push({ key: SCAN_ROOT_KEY, absPath: normalised, recursive: false, mtime: rootStat.mtimeMs });

  for (const entry of rootEntries) {
    throwIfScanAborted(options.signal);
    if (!entry.isDirectory()) continue;
    const absPath = path.resolve(normalised, entry.name);
    try {
      const stat = fs.statSync(absPath);
      scanTargets.push({ key: absPath, absPath, recursive: true, mtime: stat.mtimeMs });
    } catch {
      currentScanKeys.add(absPath);
      failedScanKeys.add(absPath);
    }
  }

  const allEntries: Array<string | { path: string; stats?: fs.Stats }> = [];
  const now = Date.now();
  const prepareTotal = Math.max(1, scanTargets.length);
  let preparedTargets = 0;
  let lastPrepareProgress = -1;
  let lastPrepareProgressAt = 0;

  onProgress?.(0, prepareTotal, 0, {
    stage: 'preparing',
    message: 'Preparing file list...',
  });
  lastPrepareProgress = 0;
  lastPrepareProgressAt = Date.now();

  const reportPrepareProgress = () => {
    if (!onProgress) return;
    const nowMs = Date.now();
    if (!shouldReportScanProgress({
      processed: preparedTargets,
      total: prepareTotal,
      lastReportedProcessed: lastPrepareProgress,
      lastReportedAt: lastPrepareProgressAt,
      now: nowMs,
    })) {
      return;
    }

    onProgress(preparedTargets, prepareTotal, 0, {
      stage: 'preparing',
      message: 'Preparing file list...',
    });
    lastPrepareProgress = preparedTargets;
    lastPrepareProgressAt = nowMs;
  };

  for (const target of scanTargets) {
    throwIfScanAborted(options.signal);
    currentScanKeys.add(target.key);
    const { signature: currentSignature, failed: signatureFailed } = await buildScanTargetSignature(
      normalised,
      target,
      options.signal,
    );
    throwIfScanAborted(options.signal);

    const previousSignature = folderSignatures[target.key];
    const cachedPaths = cachedByScanKey.get(target.key) ?? [];
    const verifyRecentTarget = isRecentlyActiveScanTarget(target.mtime, now);
    const hasCachedFilesForTarget = cachedPaths.length > 0;
    const unchanged = !options.forceFull &&
      !signatureFailed &&
      !verifyRecentTarget &&
      previousSignature === currentSignature &&
      hasCachedFilesForTarget;
    if (unchanged) {
      nextFolderSignatures[target.key] = currentSignature;
      skippedCachedFiles += cachedPaths.length;
      if (previousSignature !== currentSignature) signatureChanged = true;
      preparedTargets++;
      reportPrepareProgress();
      continue;
    }

    signatureChanged = true;
    changedScanKeys.add(target.key);
    const escapedTarget = toGlobPath(target.absPath);
    const pattern =
      target.recursive
        ? `${escapedTarget}/**/*.${IMAGE_GLOB_EXTENSIONS}`
        : `${escapedTarget}/*.${IMAGE_GLOB_EXTENSIONS}`;

    try {
      const entries = (await fg(pattern, {
        absolute: true,
        caseSensitiveMatch: false,
        stats: true,
        onlyFiles: true,
        followSymbolicLinks: false,
      })) as Array<string | { path: string; stats?: fs.Stats }>;
      throwIfScanAborted(options.signal);
      allEntries.push(...entries);
      if (!signatureFailed) {
        nextFolderSignatures[target.key] = currentSignature;
      }
    } catch (error) {
      if (isScanAbortedError(error)) throw error;
      failedScanKeys.add(target.key);
      changedScanKeys.delete(target.key);
    }

    preparedTargets++;
    reportPrepareProgress();
  }

  for (const key of Object.keys(folderSignatures)) {
    if (!currentScanKeys.has(key)) signatureChanged = true;
  }

  const total = skippedCachedFiles + allEntries.length;
  const progressStep = Math.max(25, Math.floor(total / 150));
  const existingPaths = new Set(Object.keys(cache.files));
  const currentPaths = new Set<string>();
  let processed = skippedCachedFiles;
  let newFiles = 0;
  let changed = false;

  for (const target of scanTargets) {
    if (!changedScanKeys.has(target.key)) {
      for (const cachedPath of cachedByScanKey.get(target.key) ?? []) {
        currentPaths.add(cachedPath);
      }
    }
  }
  for (const failedKey of failedScanKeys) {
    for (const cachedPath of cachedByScanKey.get(failedKey) ?? []) {
      currentPaths.add(cachedPath);
    }
  }

  if (onProgress && processed > 0) {
    onProgress(Math.min(processed, total), total, newFiles, {
      stage: 'scanning',
      message: 'Scanning files...',
    });
  } else {
    onProgress?.(0, Math.max(1, total), newFiles, {
      stage: 'scanning',
      message: total > 0 ? 'Scanning files...' : 'No image files found.',
    });
  }

  for (const entry of allEntries) {
    throwIfScanAborted(options.signal);
    const entryPath = typeof entry === 'string' ? entry : entry.path;
    const norm = path.resolve(entryPath);
    currentPaths.add(norm);

    let stat: fs.Stats | undefined = typeof entry === 'string' ? undefined : entry.stats;
    if (!stat) {
      try {
        stat = fs.statSync(norm);
      } catch {
        processed++;
        continue;
      }
    }

    const mtime = stat.mtimeMs;
    const size = stat.size;
    const createdAt = stat.birthtimeMs || stat.ctimeMs || mtime;
    const cached = cache.files[norm];

    if (cached && cached.mtime === mtime && (cached.size === undefined || cached.size === size)) {
      if (typeof cached.createdAt !== 'number') {
        cached.createdAt = createdAt;
        changed = true;
      }
      if (typeof cached.size !== 'number') {
        cached.size = size;
        changed = true;
      }
      processed++;
      if (onProgress && (processed % progressStep === 0 || processed === total)) {
        onProgress(processed, total, newFiles, {
          stage: 'scanning',
          message: 'Scanning files...',
        });
      }
      continue;
    }

    const metadata = extractSDMetadata(norm);
    throwIfScanAborted(options.signal);
    cache.files[norm] = { mtime, size, createdAt, metadata };
    changed = true;
    newFiles++;
    processed++;

    if (onProgress && (processed % progressStep === 0 || processed === total)) {
      onProgress(processed, total, newFiles, {
        stage: 'scanning',
        message: 'Scanning files...',
      });
    }
  }

  for (const p of existingPaths) {
    throwIfScanAborted(options.signal);
    if (!currentPaths.has(p)) {
      delete cache.files[p];
      changed = true;
    }
  }

  if (changed) {
    throwIfScanAborted(options.signal);
    cache.lastScan = new Date().toISOString();
    saveCache(cache);
  }
  if (signatureChanged) {
    saveFolderSignatures(normalised, nextFolderSignatures);
  }

  return buildImageFiles(cache);
}

function buildImageFiles(cache: CacheData): ImageFile[] {
  return Object.entries(cache.files).map(([absPath, entry]) => {
    const encodedPath = encodeURIComponent(absPath);
    const versionParam = encodeURIComponent(String(entry.mtime));
    return {
      id: absPath,
      filename: path.basename(absPath),
      absolutePath: absPath,
      fileUrl: `/api/image?path=${encodedPath}&thumb=true&v=${versionParam}`,
      displayUrl: `/api/image?path=${encodedPath}&display=true&v=${versionParam}`,
      fullUrl: `/api/image?path=${encodedPath}&v=${versionParam}`,
      metadata: toSearchMetadata(entry.metadata),
      createdAt: entry.createdAt ?? entry.mtime,
      mtime: entry.mtime,
    };
  });
}

const INDEX_SESSION_TTL_MS = 30 * 60 * 1000;
const MAX_INDEX_SESSIONS = 8;

type IndexState = {
  images: ImageFile[];
  sortedBySort: Map<string, ImageFile[]>;
  searchTextById: Map<string, string> | null;
  searchCache: Map<string, ImageFile[]>;
  tagCache: Array<{ tag: string; count: number }> | null;
  folderCache: Map<string, FolderBucket[]>;
};

type IndexSession = {
  state: IndexState;
  lastUsedAt: number;
};

function createIndexState(images: ImageFile[] = []): IndexState {
  return {
    images,
    sortedBySort: new Map<string, ImageFile[]>(),
    searchTextById: null,
    searchCache: new Map<string, ImageFile[]>(),
    tagCache: null,
    folderCache: new Map<string, FolderBucket[]>(),
  };
}

let fallbackIndexState = createIndexState();
const indexSessions = new Map<string, IndexSession>();

function invalidateDerivedCaches(state: IndexState) {
  state.sortedBySort = new Map<string, ImageFile[]>();
  state.searchTextById = null;
  state.searchCache = new Map<string, ImageFile[]>();
  state.tagCache = null;
  state.folderCache = new Map<string, FolderBucket[]>();
}

function createIndexSessionToken(canonicalKey: string) {
  return `idx_${crypto.createHash('sha256').update(canonicalKey).digest('base64url').slice(0, 32)}`;
}

function pruneIndexSessions(now = Date.now()) {
  for (const [token, session] of indexSessions) {
    if (now - session.lastUsedAt > INDEX_SESSION_TTL_MS) indexSessions.delete(token);
  }
  if (indexSessions.size <= MAX_INDEX_SESSIONS) return;
  const oldest = [...indexSessions.entries()]
    .sort(([, a], [, b]) => a.lastUsedAt - b.lastUsedAt)
    .slice(0, indexSessions.size - MAX_INDEX_SESSIONS);
  for (const [token] of oldest) indexSessions.delete(token);
}

function getIndexState(indexToken?: string): IndexState | null {
  if (!indexToken) {
    ensureIndexLoaded();
    return fallbackIndexState;
  }
  pruneIndexSessions();
  const session = indexSessions.get(indexToken);
  if (!session) return null;
  session.lastUsedAt = Date.now();
  return session.state;
}

export function hasIndexSession(indexToken: string | null | undefined) {
  if (!indexToken) return true;
  return getIndexState(indexToken) !== null;
}

export function clearIndexSessionsForTests() {
  indexSessions.clear();
}

function compareByFilename(a: ImageFile, b: ImageFile) {
  return a.filename.localeCompare(b.filename, undefined, { sensitivity: 'base' });
}

function getRandomSortKey(imageId: string, seed: string) {
  let hash = 2166136261;
  const value = `${seed}\0${imageId.toLowerCase()}`;
  for (let i = 0; i < value.length; i += 1) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0;
}

function rememberSortedSource(state: IndexState, cacheKey: string, sorted: ImageFile[]) {
  if (cacheKey.startsWith('random:')) {
    for (const key of state.sortedBySort.keys()) {
      if (key.startsWith('random:') && key !== cacheKey) {
        state.sortedBySort.delete(key);
      }
    }
  }
  state.sortedBySort.set(cacheKey, sorted);
}

function getSortedSource(state: IndexState, sortBy: SortBy, randomSeed = '') {
  const cacheKey = sortBy === 'random' ? `${sortBy}:${randomSeed || 'default'}` : sortBy;
  const cached = state.sortedBySort.get(cacheKey);
  if (cached) return cached;

  let sorted: ImageFile[];
  switch (sortBy) {
    case 'oldest':
      sorted = [...state.images].sort((a, b) => a.mtime - b.mtime);
      break;
    case 'created-newest':
      sorted = [...state.images].sort((a, b) => {
        const byCreated = (b.createdAt ?? b.mtime) - (a.createdAt ?? a.mtime);
        if (byCreated !== 0) return byCreated;
        const byModified = b.mtime - a.mtime;
        if (byModified !== 0) return byModified;
        return compareByFilename(a, b);
      });
      break;
    case 'created-oldest':
      sorted = [...state.images].sort((a, b) => {
        const byCreated = (a.createdAt ?? a.mtime) - (b.createdAt ?? b.mtime);
        if (byCreated !== 0) return byCreated;
        const byModified = a.mtime - b.mtime;
        if (byModified !== 0) return byModified;
        return compareByFilename(a, b);
      });
      break;
    case 'name':
      sorted = [...state.images].sort(compareByFilename);
      break;
    case 'random': {
      const seed = randomSeed || 'default';
      sorted = state.images
        .map((image) => ({ image, randomKey: getRandomSortKey(image.id, seed) }))
        .sort((a, b) => {
          const bySeed = a.randomKey - b.randomKey;
          if (bySeed !== 0) return bySeed;
          return compareByFilename(a.image, b.image);
        })
        .map((item) => item.image);
      break;
    }
    case 'newest':
    default:
      sorted = [...state.images].sort((a, b) => b.mtime - a.mtime);
      break;
  }

  rememberSortedSource(state, cacheKey, sorted);
  return sorted;
}

function ensureSearchTextCache(state: IndexState) {
  if (state.searchTextById) return;
  state.searchTextById = new Map<string, string>();
  for (const image of state.images) {
    const prompt = image.metadata?.prompt?.toLowerCase() || '';
    const filename = image.filename.toLowerCase();
    state.searchTextById.set(image.id, `${prompt}\n${filename}`);
  }
}

function normalizeDirPaths(dirPath?: string): string[] {
  const normalized: string[] = [];
  const seen = new Set<string>();
  for (const raw of parseDirSet(dirPath)) {
    try {
      const resolved = path.resolve(raw);
      const key = resolved.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      normalized.push(resolved);
    } catch {
      // ignore invalid path fragments
    }
  }
  return normalized;
}

function getTopFolderKey(absPath: string, normalizedDir?: string): string {
  if (!normalizedDir) {
    const parsed = path.parse(absPath);
    const rel = absPath.slice(parsed.root.length);
    const parts = rel.split(/[\\/]+/).filter(Boolean);
    if (parts.length <= 1) return ROOT_FOLDER_KEY;
    return parts[0];
  }

  const rel = path.relative(normalizedDir, absPath);
  if (!rel || rel === '.') return ROOT_FOLDER_KEY;
  if (rel.startsWith('..') || path.isAbsolute(rel)) return OUTSIDE_FOLDER_KEY;
  const parts = rel.split(/[\\/]+/).filter(Boolean);
  if (parts.length <= 1) return ROOT_FOLDER_KEY;
  return parts[0];
}

function getFolderKeyForDirs(absPath: string, normalizedDirs: string[]): string {
  if (normalizedDirs.length === 0) return getTopFolderKey(absPath);

  for (let index = 0; index < normalizedDirs.length; index += 1) {
    const folderKey = getTopFolderKey(absPath, normalizedDirs[index]);
    if (folderKey === OUTSIDE_FOLDER_KEY) continue;
    return normalizedDirs.length === 1 ? folderKey : `${index}:${folderKey}`;
  }

  return OUTSIDE_FOLDER_KEY;
}

function buildSearchCacheKey(
  query: string,
  sortBy: SortBy,
  dateFrom?: string,
  dateTo?: string,
  dirPath?: string,
  hiddenFolders?: string[],
  randomSeed?: string
): string {
  const normalizedDirs = normalizeDirPaths(dirPath);
  const hidden = (hiddenFolders ?? [])
    .filter(Boolean)
    .map((v) => v.trim())
    .filter(Boolean)
    .sort();

  return JSON.stringify({
    q: query,
    sortBy,
    randomSeed: sortBy === 'random' ? randomSeed ?? '' : '',
    dateFrom: dateFrom ?? '',
    dateTo: dateTo ?? '',
    dirPaths: normalizedDirs,
    hidden,
  });
}

function buildFilteredAndSorted(
  state: IndexState,
  query: string,
  sortBy: SortBy,
  dateFrom?: string,
  dateTo?: string,
  dirPath?: string,
  hiddenFolders?: string[],
  randomSeed?: string
): ImageFile[] {
  const source = getSortedSource(state, sortBy, randomSeed);
  const terms = query
    .split(',')
    .map((token) => token.trim().toLowerCase())
    .filter(Boolean);

  if (terms.length > 0) {
    ensureSearchTextCache(state);
  }

  const normalizedDirs = normalizeDirPaths(dirPath);
  const hiddenSet = new Set(
    (hiddenFolders ?? [])
      .map((folder) => folder.trim())
      .filter(Boolean)
  );

  const fromTs = dateFrom
    ? new Date(`${dateFrom}T00:00:00`).getTime()
    : Number.NEGATIVE_INFINITY;
  const toTs = dateTo
    ? new Date(`${dateTo}T23:59:59.999`).getTime()
    : Number.POSITIVE_INFINITY;

  if (normalizedDirs.length === 0 && hiddenSet.size === 0 && terms.length === 0 && !dateFrom && !dateTo) {
    return source;
  }

  const filtered: ImageFile[] = [];
  for (const image of source) {
    if (normalizedDirs.length > 0) {
      const folderKey = getFolderKeyForDirs(image.id, normalizedDirs);
      if (folderKey === OUTSIDE_FOLDER_KEY) continue;
      if (hiddenSet.has(folderKey)) continue;
    }

    const created = image.createdAt ?? image.mtime;
    if (created < fromTs || created > toTs) continue;

    if (terms.length > 0) {
      const haystack = state.searchTextById?.get(image.id) ?? '';
      if (!terms.every((term) => haystack.includes(term))) continue;
    }

    filtered.push(image);
  }

  return filtered;
}

function pushSearchCache(state: IndexState, key: string, items: ImageFile[]) {
  if (state.searchCache.has(key)) {
    state.searchCache.delete(key);
  }
  state.searchCache.set(key, items);

  while (state.searchCache.size > MAX_SEARCH_CACHE_ENTRIES) {
    const oldest = state.searchCache.keys().next().value as string | undefined;
    if (!oldest) break;
    state.searchCache.delete(oldest);
  }
}

export function getIndex(indexToken?: string): ImageFile[] {
  return getIndexState(indexToken)?.images ?? [];
}

function normalizeIndexImages(images: ImageFile[]) {
  return images.map((image) => ({
    ...image,
    metadata: toSearchMetadata(image.metadata ?? null),
  }));
}

export function setIndex(images: ImageFile[], canonicalSessionKey?: string) {
  const normalizedImages = normalizeIndexImages(images);
  fallbackIndexState = createIndexState(normalizedImages);
  if (!canonicalSessionKey) {
    if (images.length === 0) clearIndexSessionsForTests();
    return undefined;
  }

  const token = createIndexSessionToken(canonicalSessionKey);
  pruneIndexSessions();
  indexSessions.set(token, {
    state: createIndexState(normalizedImages),
    lastUsedAt: Date.now(),
  });
  pruneIndexSessions();
  return token;
}

/**
 * Registers a guarded secondary source (for example an Album) without replacing
 * either the fallback index or the active catalog session. Every image route
 * still validates the returned token against this exact path set.
 */
export function createIndexSession(images: ImageFile[], canonicalSessionKey: string) {
  const normalizedImages = normalizeIndexImages(images);
  const token = createIndexSessionToken(canonicalSessionKey);
  pruneIndexSessions();
  indexSessions.set(token, {
    state: createIndexState(normalizedImages),
    lastUsedAt: Date.now(),
  });
  pruneIndexSessions();
  return token;
}

function ensureIndexLoaded() {
  if (fallbackIndexState.images.length > 0) return;

  ensureDir(CACHE_DIR);
  const seen = new Map<string, ImageFile>();
  try {
    const currentFiles = fs.readdirSync(/*turbopackIgnore: true*/ CACHE_DIR).filter((f) => f.startsWith('index_'));
    const allFiles = new Map<string, string>();
    for (const fileName of currentFiles) {
      allFiles.set(fileName, path.join(/*turbopackIgnore: true*/ CACHE_DIR, fileName));
    }
    for (const fp of allFiles.values()) {
      try {
        const data = JSON.parse(fs.readFileSync(/*turbopackIgnore: true*/ fp, 'utf-8')) as CacheData;
        if (data.version === CACHE_VERSION) {
          const images = buildImageFiles(data);
          for (const image of images) {
            seen.set(image.id, image);
          }
        }
      } catch {
        // skip corrupted cache file
      }
    }
  } catch {
    // no cache dir yet
  }

  fallbackIndexState = createIndexState(Array.from(seen.values()));
}

export function searchIndex(
  query: string,
  page: number,
  size: number,
  sortBy: SortBy = 'newest',
  dateFrom?: string,
  dateTo?: string,
  favIds?: Set<string>,
  dirPath?: string,
  hiddenFolders?: string[],
  randomSeed?: string,
  indexToken?: string
): { results: ImageFile[]; total: number; page: number; totalPages: number } {
  const state = getIndexState(indexToken);
  if (!state) return { results: [], total: 0, page: 0, totalPages: 1 };

  const key = buildSearchCacheKey(query, sortBy, dateFrom, dateTo, dirPath, hiddenFolders, randomSeed);
  let filtered = state.searchCache.get(key);
  if (!filtered) {
    filtered = buildFilteredAndSorted(state, query, sortBy, dateFrom, dateTo, dirPath, hiddenFolders, randomSeed);
    pushSearchCache(state, key, filtered);
  }

  const pageSize = Number.isFinite(size) ? Math.max(1, Math.trunc(size)) : 100;
  const safePage = Number.isFinite(page) ? Math.max(0, Math.trunc(page)) : 0;
  const total = filtered.length;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const start = safePage * pageSize;
  const pageResults = filtered.slice(start, start + pageSize);
  const results = favIds
    ? pageResults.map((img) => ({ ...img, isFavorite: favIds.has(img.id) }))
    : pageResults;

  return { results, total, page: safePage, totalPages };
}

function cleanTag(raw: string): string {
  let s = raw.trim();
  s = s.replace(/^[\(\[\{]+/, '').replace(/[\)\]\}]+$/, '');
  s = s.trim().toLowerCase();
  if (!s || s === 'break' || s.startsWith('\\n') || s.includes('\n')) return '';
  return s;
}

export function getTags(limit = MAX_TAG_RESULTS, indexToken?: string): Array<{ tag: string; count: number }> {
  const state = getIndexState(indexToken);
  if (!state) return [];

  if (!state.tagCache) {
    const tagCounts = new Map<string, number>();
    for (const image of state.images) {
      const prompt = image.metadata?.prompt;
      if (!prompt) continue;

      const parts = prompt.split(',');
      for (const part of parts) {
        const tag = cleanTag(part);
        if (tag && tag.length >= 2) {
          tagCounts.set(tag, (tagCounts.get(tag) || 0) + 1);
        }
      }
    }

    state.tagCache = [...tagCounts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([tag, count]) => ({ tag, count }));
  }

  return state.tagCache.slice(0, Math.max(1, limit));
}

export function getFolderBuckets(
  dirPath: string,
  limit = 200,
  indexToken?: string,
): Array<{ key: string; label: string; count: number }> {
  const state = getIndexState(indexToken);
  if (!state) return [];

  const normalizedDirs = normalizeDirPaths(dirPath);
  if (normalizedDirs.length === 0) return [];

  const cacheKey = `${normalizedDirs.join('\u0001')}|${limit}`;
  const cached = state.folderCache.get(cacheKey);
  if (cached) return cached;

  const counts = new Map<string, number>();
  for (const image of state.images) {
    const folderKey = getFolderKeyForDirs(image.id, normalizedDirs);
    if (folderKey === OUTSIDE_FOLDER_KEY) continue;
    counts.set(folderKey, (counts.get(folderKey) || 0) + 1);
  }

  const buckets: FolderBucket[] = [...counts.entries()]
    .sort((a, b) => {
      if (b[1] !== a[1]) return b[1] - a[1];
      return a[0].localeCompare(b[0], undefined, { sensitivity: 'base' });
    })
    .slice(0, Math.max(1, limit))
    .map(([key, count]) => ({
      key,
      label: formatFolderBucketLabel(key, normalizedDirs),
      count,
    }));

  state.folderCache.set(cacheKey, buckets);
  return buckets;
}

function formatFolderBucketLabel(key: string, normalizedDirs: string[]) {
  const [maybeIndex, ...rest] = key.split(':');
  if (normalizedDirs.length > 1 && rest.length > 0) {
    const index = Number(maybeIndex);
    const folderKey = rest.join(':');
    const rootLabel = Number.isInteger(index) && normalizedDirs[index]
      ? basenameFromPath(normalizedDirs[index]) || normalizedDirs[index]
      : 'root';
    return `${rootLabel} / ${folderKey === ROOT_FOLDER_KEY ? '(root)' : folderKey}`;
  }

  return key === ROOT_FOLDER_KEY ? '(root)' : key;
}

export function removeFromIndex(absPath: string, indexToken?: string) {
  const targetState = getIndexState(indexToken);
  if (targetState) {
    targetState.images = targetState.images.filter((img) => img.id !== absPath);
    invalidateDerivedCaches(targetState);
  }
  if (fallbackIndexState !== targetState) {
    fallbackIndexState.images = fallbackIndexState.images.filter((img) => img.id !== absPath);
    invalidateDerivedCaches(fallbackIndexState);
  }
  for (const session of indexSessions.values()) {
    if (session.state === targetState) continue;
    if (!session.state.images.some((image) => image.id === absPath)) continue;
    session.state.images = session.state.images.filter((image) => image.id !== absPath);
    invalidateDerivedCaches(session.state);
  }

  const normalised = path.resolve(absPath);
  for (const cache of cacheByDir.values()) {
    if (cache.files[normalised]) {
      delete cache.files[normalised];
      cache.lastScan = new Date().toISOString();
      void fs.promises.writeFile(/*turbopackIgnore: true*/ cacheFilePath(cache.dirPath), JSON.stringify(cache), 'utf-8').catch(() => {
        // A later refresh can rebuild the disk cache if this background write fails.
      });
      return;
    }
  }

  void removeFromDiskCaches(normalised);
}

async function removeFromDiskCaches(normalisedPath: string) {
  ensureDir(CACHE_DIR);
  let cacheFiles: string[] = [];
  try {
    cacheFiles = (await fs.promises.readdir(/*turbopackIgnore: true*/ CACHE_DIR)).filter((f) => f.startsWith('index_'));
  } catch {
    return;
  }

  for (const cf of cacheFiles) {
    const fp = path.join(/*turbopackIgnore: true*/ CACHE_DIR, cf);
    try {
      const raw = await fs.promises.readFile(/*turbopackIgnore: true*/ fp, 'utf-8');
      const data = JSON.parse(raw) as CacheData;
      if (data.files[normalisedPath]) {
        delete data.files[normalisedPath];
        await fs.promises.writeFile(/*turbopackIgnore: true*/ fp, JSON.stringify(data), 'utf-8');
        break;
      }
    } catch {
      // skip broken cache file
    }
  }
}

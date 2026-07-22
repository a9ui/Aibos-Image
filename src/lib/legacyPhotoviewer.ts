import fs from 'fs';
import path from 'path';
import crypto from 'crypto';
import type { AppSettings } from './types';
import { DEFAULT_KEY_BINDINGS } from './types';

const LEGACY_PROJECT_DIR = process.env.PV_LEGACY_PHOTOVIEWER_DIR
  ? path.resolve(process.env.PV_LEGACY_PHOTOVIEWER_DIR)
  : '';
const LEGACY_CACHE_DIR = LEGACY_PROJECT_DIR
  ? path.join(/*turbopackIgnore: true*/ LEGACY_PROJECT_DIR, '.cache')
  : '';
const MAX_FAVORITE_LEVEL = 5;

function legacyCacheExists() {
  if (!LEGACY_CACHE_DIR) return false;
  return fs.existsSync(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR);
}

function readJsonFile<T>(filePath: string): T | null {
  try {
    return JSON.parse(fs.readFileSync(/*turbopackIgnore: true*/ filePath, 'utf-8')) as T;
  } catch {
    return null;
  }
}

function normalizeFavorites(value: unknown): Record<string, number> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  const normalized: Record<string, number> = {};
  for (const [id, levelValue] of Object.entries(value)) {
    if (!id) continue;
    const level = typeof levelValue === 'number'
      ? Math.max(0, Math.min(MAX_FAVORITE_LEVEL, Math.trunc(levelValue)))
      : levelValue
        ? 1
        : 0;
    if (level > 0) normalized[id] = level;
  }
  return normalized;
}

export function mergeWithLegacyFavorites(current: Record<string, number>): Record<string, number> {
  if (!legacyCacheExists()) return current;
  const legacy = normalizeFavorites(readJsonFile(path.join(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR, 'favorites.json')));
  const merged = { ...legacy };
  for (const [id, level] of Object.entries(current)) {
    merged[id] = Math.max(merged[id] ?? 0, level);
  }
  return merged;
}

export function loadLegacySettings(): AppSettings | null {
  if (!legacyCacheExists()) return null;
  const parsed = readJsonFile<Partial<AppSettings>>(path.join(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR, 'settings.json'));
  if (!parsed) return null;
  return {
    keyBindings: { ...DEFAULT_KEY_BINDINGS, ...(parsed.keyBindings || {}) },
    confirmBeforeDelete: parsed.confirmBeforeDelete ?? true,
  };
}

export function legacyCacheFilePath(fileName: string): string | null {
  if (!legacyCacheExists()) return null;
  const resolved = path.join(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR, path.basename(fileName));
  return fs.existsSync(/*turbopackIgnore: true*/ resolved) ? resolved : null;
}

export function cacheHashForDir(dirPath: string) {
  return crypto.createHash('md5').update(path.resolve(dirPath).toLowerCase()).digest('hex');
}

function decodeJsonStringLiteral(value: string): string | null {
  try {
    return JSON.parse(`"${value}"`) as string;
  } catch {
    return null;
  }
}

function readDirPathFromIndexCache(filePath: string): string | null {
  let fd: number | null = null;
  try {
    fd = fs.openSync(/*turbopackIgnore: true*/ filePath, 'r');
    const buffer = Buffer.alloc(64 * 1024);
    const bytesRead = fs.readSync(fd, buffer, 0, buffer.length, 0);
    const prefix = buffer.subarray(0, bytesRead).toString('utf-8');
    const match = prefix.match(/"dirPath"\s*:\s*"((?:\\.|[^"\\])*)"/);
    if (!match) return null;
    return decodeJsonStringLiteral(match[1]);
  } catch {
    return null;
  } finally {
    if (fd !== null) {
      try { fs.closeSync(fd); } catch { /* ignore */ }
    }
  }
}

export function getLegacyRecentDirSets(limit = 12): string[] {
  if (!legacyCacheExists()) return [];
  let files: fs.Dirent[] = [];
  try {
    files = fs.readdirSync(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR, { withFileTypes: true });
  } catch {
    return [];
  }

  const candidates = files
    .filter((entry) => entry.isFile() && entry.name.startsWith('index_') && entry.name.endsWith('.json'))
    .map((entry) => {
      const filePath = path.join(/*turbopackIgnore: true*/ LEGACY_CACHE_DIR, entry.name);
      try {
        return { filePath, mtimeMs: fs.statSync(/*turbopackIgnore: true*/ filePath).mtimeMs };
      } catch {
        return null;
      }
    })
    .filter((entry): entry is { filePath: string; mtimeMs: number } => Boolean(entry))
    .sort((a, b) => b.mtimeMs - a.mtimeMs);

  const recent: string[] = [];
  const seen = new Set<string>();
  for (const candidate of candidates) {
    const dirPath = readDirPathFromIndexCache(candidate.filePath);
    if (!dirPath) continue;
    const key = path.resolve(dirPath).toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    recent.push(path.resolve(dirPath));
    if (recent.length >= limit) break;
  }
  return recent;
}

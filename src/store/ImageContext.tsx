'use client';

import React, {
  createContext, useContext, useState, useEffect,
  useCallback, useRef, ReactNode
} from 'react';
import type {
  ImageFile,
  KeyBindings,
  SearchResponse,
  ThumbnailStatusBorderSettings,
  ThumbnailStatusBorderSettingsPatch,
} from '../lib/types';
import { DEFAULT_KEY_BINDINGS, DEFAULT_THUMBNAIL_STATUS_BORDERS } from '../lib/types';
import {
  getClientFilteredLoadedIds,
  nextAfterClientFilterMutation,
  type FolderSortBy,
} from '../lib/viewerUi';
import { removeImageSlot } from '../lib/imageListState';
import { formatDirSet, parseDirSet } from '../lib/pathSet';
import { migrateLegacyPhotoviewerState } from '../lib/localStorageMigration';
import {
  DEFAULT_SHOW_UNSEEN_MARKERS,
  matchesFavoriteLevel,
  readFavoriteFilterLevelsPreference,
  toggleFavoriteFilterLevel as toggleFavoriteFilterLevelValue,
  type FavoriteFilterLevel,
} from '../lib/browserUiPreferences';
import {
  PREVIEW_TAB_STORAGE_KEY,
  normalizePersistedPreviewTabs,
  serializePersistedPreviewTabs,
  type PersistedPreviewTabs,
} from '../lib/previewTabPersistence';
import {
  getSparseModalNavigationRanges,
  type SparseModalNavigationIntent,
} from '../lib/modalNavigation';
import { MAX_THUMB_SIZE, MIN_THUMB_SIZE } from '../lib/thumbnailSizing';
import {
  isValidThumbnailStatusBordersDocument,
  normalizeThumbnailStatusBorders,
} from '../lib/thumbnailStatusBorders';

// ── View settings ──
export type ViewMode = 'grid' | 'list';
export type AspectMode = 'original' | 'square' | 'portrait';
export type DisplayStyle = 'standard' | 'compact' | 'poster';
export type SortBy = 'newest' | 'oldest' | 'created-newest' | 'created-oldest' | 'name' | 'random';
export type SearchErrorKind = 'transient' | 'session-expired';

export interface DeleteImageOptions {
  favoriteConfirmed?: boolean;
}

export interface ViewSettings {
  viewMode: ViewMode;
  thumbSize: number;       // px, range 20-600; 600 is the one-column endpoint
  aspectMode: AspectMode;
  displayStyle: DisplayStyle;
  columns: number;         // 0 = auto
  sidebarOpen: boolean;
  rightPanelOpen: boolean;
  rightPanelWidth: number; // px
  sortBy: SortBy;
  randomSeed: string;
  folderSortBy: FolderSortBy;
  modalEdgeRatio: number;
  modalFilmstripOpen: boolean;
  enhanceQueueOpen: boolean;
  dateFrom: string;
  dateTo: string;
  hiddenFolders: string[];
  showUnseenMarkers: boolean;
  foldersExpanded: boolean;
}

const DEFAULT_VIEW: ViewSettings = {
  viewMode: 'grid',
  thumbSize: 200,
  aspectMode: 'original',
  displayStyle: 'standard',
  columns: 0,
  sidebarOpen: true,
  rightPanelOpen: true,
  rightPanelWidth: 320,
  sortBy: 'newest',
  randomSeed: 'default',
  folderSortBy: 'name-asc',
  modalEdgeRatio: 0.28,
  modalFilmstripOpen: true,
  enhanceQueueOpen: true,
  dateFrom: '',
  dateTo: '',
  hiddenFolders: [],
  showUnseenMarkers: DEFAULT_SHOW_UNSEEN_MARKERS,
  foldersExpanded: true,
};

const SCROLL_MEMORY_FLUSH_DELAY_MS = 500;
const SEEN_IMAGES_FLUSH_DELAY_MS = 900;
const FAVORITES_FLUSH_DELAY_MS = 300;
const VIEW_SETTINGS_FLUSH_DELAY_MS = 300;
const FAVORITES_PENDING_STORAGE_KEY = 'pvu_favorites_pending';
const FAVORITES_SHARED_MIGRATION_STORAGE_KEY = 'pvu_favorites_shared_migration_v1';
const KEEPALIVE_PAYLOAD_LIMIT_BYTES = 60 * 1024;
const AUTO_THUMB_WARM_DELAY_MS = 4200;
const AUTO_THUMB_WARM_LIMIT = 1200;
const MAX_FAVORITE_LEVEL = 5;
const MAX_CLOSED_PREVIEW_TABS = 30;
const MIN_RIGHT_PANEL_WIDTH = 240;
const MAX_RIGHT_PANEL_WIDTH = 900;
const MIN_MODAL_EDGE_RATIO = 0.10;
const MAX_MODAL_EDGE_RATIO = 0.40;
const MAX_RANDOM_SEED_LENGTH = 256;
const MAX_HIDDEN_FOLDERS = 500;
const MAX_HIDDEN_FOLDER_LENGTH = 4096;

interface FavoriteFilterNavigationSnapshot {
  showFavOnly: boolean;
  showUnfavOnly: boolean;
  favoriteFilterLevels: FavoriteFilterLevel[];
  showEnhancedOnly: boolean;
  enhancedSourceIds: Record<string, true>;
}

interface PendingFavoriteFilterMutation {
  currentId: string;
  previousOrderedIds: string[];
  searchResults: Array<ImageFile | null>;
  filter: FavoriteFilterNavigationSnapshot;
  modalWasOpen: boolean;
  activeWasOpenTab: boolean;
  expectedLevel: number;
}

export type ModalNavigationResolution =
  | { status: 'found'; index: number; id: string; filteredOrderedIds: string[] | null }
  | { status: 'empty'; filteredOrderedIds: string[] | null }
  | { status: 'stale' }
  | { status: 'unavailable' };

function sameFavoriteFilter(
  left: FavoriteFilterNavigationSnapshot,
  right: FavoriteFilterNavigationSnapshot
): boolean {
  return left.showFavOnly === right.showFavOnly
    && left.showUnfavOnly === right.showUnfavOnly
    && left.showEnhancedOnly === right.showEnhancedOnly
    && left.favoriteFilterLevels.length === right.favoriteFilterLevels.length
    && left.favoriteFilterLevels.every((level, index) => level === right.favoriteFilterLevels[index]);
}

const VIEW_MODES: readonly ViewMode[] = ['grid', 'list'];
const ASPECT_MODES: readonly AspectMode[] = ['original', 'square', 'portrait'];
const DISPLAY_STYLES: readonly DisplayStyle[] = ['standard', 'compact', 'poster'];
const SORT_ORDERS: readonly SortBy[] = [
  'newest', 'oldest', 'created-newest', 'created-oldest', 'name', 'random',
];
const FOLDER_SORT_ORDERS: readonly FolderSortBy[] = [
  'name-asc', 'name-desc', 'count-desc', 'count-asc',
];

export function reorderPreviewTabIds(tabIds: string[], id: string, destinationIndex: number): string[] {
  if (!id || !Number.isInteger(destinationIndex)) return tabIds;
  if (new Set(tabIds).size !== tabIds.length) return tabIds;
  const sourceIndex = tabIds.indexOf(id);
  if (sourceIndex < 0 || destinationIndex < 0 || destinationIndex >= tabIds.length) return tabIds;
  if (sourceIndex === destinationIndex) return tabIds;

  const next = tabIds.filter((tabId) => tabId !== id);
  next.splice(destinationIndex, 0, id);
  return next;
}

function readOwnStoredValue(stored: object, key: string): unknown {
  try {
    // Do not trust inherited values (or accessors). localStorage normally
    // contains plain JSON, but this also makes the boundary safe for callers
    // that hand us a prototype-backed object in tests or future migrations.
    const descriptor = Object.getOwnPropertyDescriptor(stored, key);
    return descriptor && 'value' in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
}

function normalizeEnum<T extends string>(value: unknown, allowed: readonly T[], fallback: T): T {
  return typeof value === 'string' && allowed.includes(value as T) ? value as T : fallback;
}

function normalizeBoolean(value: unknown, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value : fallback;
}

function normalizeBoundedNumber(value: unknown, fallback: number, min: number, max: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  return Math.min(max, Math.max(min, value));
}

function normalizeStoredDate(value: unknown): string {
  if (typeof value !== 'string' || value === '') return value === '' ? '' : DEFAULT_VIEW.dateFrom;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return DEFAULT_VIEW.dateFrom;

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31) return DEFAULT_VIEW.dateFrom;

  const parsed = new Date(Date.UTC(year, month - 1, day));
  return parsed.getUTCFullYear() === year
    && parsed.getUTCMonth() === month - 1
    && parsed.getUTCDate() === day
    ? value
    : DEFAULT_VIEW.dateFrom;
}

function normalizeHiddenFolders(value: unknown): string[] {
  if (!Array.isArray(value)) return [];

  const normalized: string[] = [];
  const seen = new Set<string>();
  for (const candidate of value) {
    if (normalized.length >= MAX_HIDDEN_FOLDERS) break;
    if (typeof candidate !== 'string') continue;
    const folder = candidate.trim();
    if (!folder || folder.length > MAX_HIDDEN_FOLDER_LENGTH) continue;
    const dedupeKey = folder.toLowerCase();
    if (seen.has(dedupeKey)) continue;
    seen.add(dedupeKey);
    normalized.push(folder);
  }
  return normalized;
}

function normalizeRandomSeed(value: unknown): string {
  if (typeof value !== 'string') return DEFAULT_VIEW.randomSeed;
  const seed = value.trim();
  return seed && seed.length <= MAX_RANDOM_SEED_LENGTH ? seed : DEFAULT_VIEW.randomSeed;
}

export function normalizeStoredView(value: unknown): ViewSettings {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { ...DEFAULT_VIEW, hiddenFolders: [] };
  }

  const stored = value as object;
  return {
    viewMode: normalizeEnum(readOwnStoredValue(stored, 'viewMode'), VIEW_MODES, DEFAULT_VIEW.viewMode),
    thumbSize: normalizeBoundedNumber(
      readOwnStoredValue(stored, 'thumbSize'), DEFAULT_VIEW.thumbSize, MIN_THUMB_SIZE, MAX_THUMB_SIZE,
    ),
    aspectMode: normalizeEnum(readOwnStoredValue(stored, 'aspectMode'), ASPECT_MODES, DEFAULT_VIEW.aspectMode),
    displayStyle: normalizeEnum(readOwnStoredValue(stored, 'displayStyle'), DISPLAY_STYLES, DEFAULT_VIEW.displayStyle),
    // `columns` belonged to an older UI. The current UI is size-driven and
    // has no way to change or clear a persisted fixed-column value.
    columns: 0,
    sidebarOpen: normalizeBoolean(readOwnStoredValue(stored, 'sidebarOpen'), DEFAULT_VIEW.sidebarOpen),
    rightPanelOpen: normalizeBoolean(readOwnStoredValue(stored, 'rightPanelOpen'), DEFAULT_VIEW.rightPanelOpen),
    rightPanelWidth: normalizeBoundedNumber(
      readOwnStoredValue(stored, 'rightPanelWidth'), DEFAULT_VIEW.rightPanelWidth,
      MIN_RIGHT_PANEL_WIDTH, MAX_RIGHT_PANEL_WIDTH,
    ),
    sortBy: normalizeEnum(readOwnStoredValue(stored, 'sortBy'), SORT_ORDERS, DEFAULT_VIEW.sortBy),
    randomSeed: normalizeRandomSeed(readOwnStoredValue(stored, 'randomSeed')),
    folderSortBy: normalizeEnum(
      readOwnStoredValue(stored, 'folderSortBy'), FOLDER_SORT_ORDERS, DEFAULT_VIEW.folderSortBy,
    ),
    modalEdgeRatio: normalizeBoundedNumber(
      readOwnStoredValue(stored, 'modalEdgeRatio'), DEFAULT_VIEW.modalEdgeRatio,
      MIN_MODAL_EDGE_RATIO, MAX_MODAL_EDGE_RATIO,
    ),
    modalFilmstripOpen: normalizeBoolean(
      readOwnStoredValue(stored, 'modalFilmstripOpen'), DEFAULT_VIEW.modalFilmstripOpen,
    ),
    enhanceQueueOpen: normalizeBoolean(
      readOwnStoredValue(stored, 'enhanceQueueOpen'), DEFAULT_VIEW.enhanceQueueOpen,
    ),
    dateFrom: normalizeStoredDate(readOwnStoredValue(stored, 'dateFrom')),
    dateTo: normalizeStoredDate(readOwnStoredValue(stored, 'dateTo')),
    hiddenFolders: normalizeHiddenFolders(readOwnStoredValue(stored, 'hiddenFolders')),
    showUnseenMarkers: normalizeBoolean(
      readOwnStoredValue(stored, 'showUnseenMarkers'), DEFAULT_VIEW.showUnseenMarkers,
    ),
    // Older snapshots predate this field. An invalid value must not collapse
    // the only folder-management surface after reload.
    foldersExpanded: normalizeBoolean(
      readOwnStoredValue(stored, 'foldersExpanded'), DEFAULT_VIEW.foldersExpanded,
    ),
  };
}

// ── Context shape ──
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

/**
 * Keep the permissive per-level normalization used by existing local data, but
 * distinguish an invalid document from an intentionally empty favorite map.
 */
function readStoredFavoritesSnapshot(serialized: string): Record<string, number> | null {
  const parsed: unknown = JSON.parse(serialized);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  return normalizeFavorites(parsed);
}

function normalizeSeenImageIds(value: unknown): Record<string, true> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  const normalized: Record<string, true> = {};
  for (const [id, marker] of Object.entries(value)) {
    if (id && marker) normalized[id] = true;
  }
  return normalized;
}

function mergeSeenImageIds(
  first: Record<string, true>,
  second: Record<string, true>,
): Record<string, true> {
  return { ...first, ...second };
}

function mergeFavorites(
  sharedFavorites: Record<string, number>,
  localFavorites: Record<string, number>,
  localDirtyIds: ReadonlySet<string>,
): Record<string, number> {
  const merged = { ...sharedFavorites };
  for (const id of localDirtyIds) {
    const level = localFavorites[id] ?? 0;
    if (level > 0) merged[id] = level;
    else delete merged[id];
  }
  return merged;
}

function reconcileFavoriteWriteResponse(
  serverFavorites: Record<string, number>,
  requestSnapshot: Record<string, number>,
  currentFavorites: Record<string, number>,
) {
  const reconciled = { ...serverFavorites };
  const candidateIds = new Set([...Object.keys(requestSnapshot), ...Object.keys(currentFavorites)]);
  for (const id of candidateIds) {
    const requestedLevel = requestSnapshot[id] ?? 0;
    const currentLevel = currentFavorites[id] ?? 0;
    if (requestedLevel === currentLevel) continue;
    if (currentLevel > 0) reconciled[id] = currentLevel;
    else delete reconciled[id];
  }

  const currentIds = Object.keys(currentFavorites);
  const reconciledIds = Object.keys(reconciled);
  if (currentIds.length === reconciledIds.length
    && currentIds.every((id) => currentFavorites[id] === reconciled[id])) {
    return currentFavorites;
  }
  return reconciled;
}

interface PendingFavoritesJournal {
  version: 1 | 2;
  revision: string;
  dirtyIds: string[];
  baseFavorites: Record<string, number>;
  baseKnownIds: string[];
  /** Exact desired levels for dirtyIds; absence means an intentional clear. */
  desiredFavorites: Record<string, number> | null;
}

function readExactFavoriteJournalMap(value: unknown): Record<string, number> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const normalized: Record<string, number> = {};
  for (const [id, rawLevel] of Object.entries(value)) {
    if (!id.trim() || typeof rawLevel !== 'number' || !Number.isFinite(rawLevel)) return null;
    const level = Math.max(0, Math.min(MAX_FAVORITE_LEVEL, Math.trunc(rawLevel)));
    if (level > 0) normalized[id] = level;
  }
  return normalized;
}

function readPendingFavoritesJournal(serialized: string | null): PendingFavoritesJournal | null {
  if (!serialized) return null;
  try {
    const parsed: unknown = JSON.parse(serialized);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
    const candidate = parsed as Partial<PendingFavoritesJournal>;
    if ((candidate.version !== 1 && candidate.version !== 2)
      || typeof candidate.revision !== 'string' || !candidate.revision) return null;
    if (!Array.isArray(candidate.dirtyIds) || !Array.isArray(candidate.baseKnownIds)) return null;
    if (!candidate.baseFavorites || typeof candidate.baseFavorites !== 'object' || Array.isArray(candidate.baseFavorites)) {
      return null;
    }
    if (candidate.dirtyIds.some((id) => typeof id !== 'string' || !id.trim())) return null;
    const dirtyIds = [...new Set(candidate.dirtyIds as string[])];
    const dirtySet = new Set(dirtyIds);
    const baseKnownIds = [...new Set(candidate.baseKnownIds.filter(
      (id): id is string => typeof id === 'string' && dirtySet.has(id)
    ))];
    const baseFavorites = candidate.version === 2
      ? readExactFavoriteJournalMap(candidate.baseFavorites)
      : normalizeFavorites(candidate.baseFavorites);
    if (baseFavorites === null) return null;
    const desiredFavorites = candidate.version === 2
      ? readExactFavoriteJournalMap(candidate.desiredFavorites)
      : null;
    if (candidate.version === 2 && desiredFavorites === null) return null;
    if (dirtyIds.length === 0) return null;
    return {
      version: candidate.version,
      revision: candidate.revision,
      dirtyIds,
      baseFavorites,
      baseKnownIds,
      desiredFavorites,
    };
  } catch {
    return null;
  }
}

function createPersistenceRevision() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function utf8ByteLength(value: string) {
  return new TextEncoder().encode(value).byteLength;
}

function writeJsonLocalStorage(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // ignore storage quota or private-mode failures
  }
}

function withIndexToken(url: string, indexToken: string | null) {
  if (!indexToken || /[?&]indexToken=/.test(url)) return url;
  return `${url}${url.includes('?') ? '&' : '?'}indexToken=${encodeURIComponent(indexToken)}`;
}

function catalogIdentity(dirSet: string): string {
  return parseDirSet(dirSet)
    .map((dir) => dir.replace(/\//g, '\\').replace(/[\\]+$/, '').toLowerCase())
    .join('\n');
}

class SearchRequestError extends Error {
  constructor(message: string, readonly kind: SearchErrorKind) {
    super(message);
    this.name = 'SearchRequestError';
  }
}

type ScanEventStage = 'preparing' | 'scanning' | 'complete';

function normalizeScanEventProgress(data: Record<string, unknown>) {
  const { processed, total, newFiles } = data;
  if (![processed, total, newFiles].every((value) => typeof value === 'number' && Number.isFinite(value) && value >= 0)) {
    return null;
  }
  const stage: ScanEventStage | undefined = data.stage === 'preparing'
    || data.stage === 'scanning'
    || data.stage === 'complete'
    ? data.stage
    : undefined;
  return {
    processed: processed as number,
    total: total as number,
    newFiles: newFiles as number,
    stage,
    message: typeof data.message === 'string' ? data.message : undefined,
  };
}

type SharedSettingsPatch =
  | { keyBindings: KeyBindings }
  | { confirmBeforeDelete: boolean }
  | { thumbnailStatusBorders: ThumbnailStatusBorderSettingsPatch };

function normalizeThumbnailStatusBorderPatch(
  value: ThumbnailStatusBorderSettingsPatch,
): ThumbnailStatusBorderSettingsPatch | null {
  if (!isValidThumbnailStatusBordersDocument(value)) return null;
  const normalized = normalizeThumbnailStatusBorders(value);
  const patch: ThumbnailStatusBorderSettingsPatch = {};
  if (Object.hasOwn(value, 'favorite')) patch.favorite = normalized.favorite;
  if (Object.hasOwn(value, 'enhanced')) patch.enhanced = normalized.enhanced;
  return Object.keys(patch).length > 0 ? patch : null;
}

async function saveSharedSettings(patch: SharedSettingsPatch): Promise<SharedSettingsSaveResult> {
  let response: Response;
  try {
    response = await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    });
  } catch {
    return {
      ok: false,
      error: 'Could not reach the local settings service. Try again.',
    };
  }

  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    return {
      ok: false,
      status: response.status,
      error: 'The local settings service returned an invalid response. Try again.',
    };
  }

  if (!response.ok) {
    if (response.status === 409) {
      return {
        ok: false,
        status: response.status,
        error: 'The shared settings file changed or needs attention. Fix it, then retry.',
      };
    }
    if (response.status === 503) {
      return {
        ok: false,
        status: response.status,
        error: 'Shared settings are temporarily unavailable. Try again.',
      };
    }
    return {
      ok: false,
      status: response.status,
      error: 'Could not save shared settings. Try again.',
    };
  }

  if (!isMatchingSettingsSaveResponse(payload, patch)) {
    return {
      ok: false,
      status: response.status,
      error: 'The local settings service did not confirm the saved value. Try again.',
    };
  }

  if ('thumbnailStatusBorders' in patch) {
    const record = payload as Record<string, unknown>;
    return {
      ok: true,
      thumbnailStatusBorders: normalizeThumbnailStatusBorders(record.thumbnailStatusBorders),
    };
  }
  return { ok: true };
}

function isMatchingSettingsSaveResponse(payload: unknown, patch: SharedSettingsPatch): boolean {
  if (!payload || typeof payload !== 'object' || (payload as { ok?: unknown }).ok !== true) return false;
  const record = payload as Record<string, unknown>;
  if ('confirmBeforeDelete' in patch) {
    return record.confirmBeforeDelete === patch.confirmBeforeDelete;
  }
  if ('thumbnailStatusBorders' in patch) {
    if (!isValidThumbnailStatusBordersDocument(record.thumbnailStatusBorders)) return false;
    const saved = normalizeThumbnailStatusBorders(record.thumbnailStatusBorders);
    const requested = patch.thumbnailStatusBorders;
    return (!requested.favorite
        || (saved.favorite.enabled === requested.favorite.enabled
          && saved.favorite.color === requested.favorite.color.toLowerCase()))
      && (!requested.enhanced
        || (saved.enhanced.enabled === requested.enhanced.enabled
          && saved.enhanced.color === requested.enhanced.color.toLowerCase()));
  }
  const savedBindings = record.keyBindings;
  if (!savedBindings || typeof savedBindings !== 'object') return false;
  return (Object.keys(patch.keyBindings) as Array<keyof KeyBindings>).every(
    (action) => (savedBindings as Record<string, unknown>)[action] === patch.keyBindings[action]
  );
}

export type SharedSettingsSaveResult =
  | { ok: true; thumbnailStatusBorders?: ThumbnailStatusBorderSettings }
  | { ok: false; error: string; status?: number };

export interface OpenExternalOptions {
  indexToken?: string | null;
  display?: 'source' | 'enhanced';
  jobId?: string | null;
}

export async function requestOpenExternal(
  id: string,
  options: OpenExternalOptions = {},
): Promise<boolean> {
  const params = new URLSearchParams({ path: id });
  if (options.indexToken) params.set('indexToken', options.indexToken);
  if (options.display) params.set('display', options.display);
  if (options.jobId) params.set('jobId', options.jobId);

  try {
    const response = await fetch(`/api/open?${params.toString()}`, { method: 'POST' });
    return response.ok;
  } catch {
    return false;
  }
}

interface Ctx {
  // App phase
  phase: 'landing' | 'scanning' | 'viewer';
  setPhase: (p: 'landing' | 'scanning' | 'viewer') => void;
  dirPath: string;
  setDirPath: (d: string) => void;
  indexToken: string | null;

  // Scan progress
  scanProgress: { processed: number; total: number; newFiles: number; stage?: 'preparing' | 'scanning' | 'complete'; message?: string } | null;
  scanError: string | null;
  dismissScanError: () => void;

  // Search
  searchQuery: string;
  setSearchQuery: (q: string) => void;
  searchResults: Array<ImageFile | null>;
  searchTotal: number;
  isSearching: boolean;
  searchError: string | null;
  searchErrorKind: SearchErrorKind | null;
  ensureSearchRange: (startIndex: number, endIndex: number) => void;
  resolveModalNavigationTarget: (
    currentIndex: number,
    intent: SparseModalNavigationIntent
  ) => Promise<ModalNavigationResolution>;
  retrySearch: () => void;
  reportImageSessionExpired: () => void;
  rescanExpiredSearchSession: () => void;
  dismissSearchError: () => void;

  // Favorites
  favorites: Record<string, number>;
  toggleFavorite: (id: string) => void;
  cycleFavoriteLevel: (id: string) => void;
  decreaseFavoriteLevel: (id: string) => void;
  clearFavorite: (id: string) => void;
  setFavoriteLevels: (ids: readonly string[], level: number) => void;
  adjustFavoriteLevels: (ids: readonly string[], delta: number) => void;
  showFavOnly: boolean;
  setShowFavOnly: (v: boolean) => void;
  showUnfavOnly: boolean;
  setShowUnfavOnly: (v: boolean) => void;
  favoriteFilterLevels: FavoriteFilterLevel[];
  toggleFavoriteFilterLevel: (v: FavoriteFilterLevel) => void;
  clearFavoriteFilterLevels: () => void;
  showEnhancedOnly: boolean;
  setShowEnhancedOnly: (v: boolean) => void;
  enhancedSourceIds: Record<string, true>;

  // Modal
  selectedIndex: number | null;
  setSelectedIndex: (i: number | null) => void;
  modalImageIds: string[];
  setModalImageIds: (ids: string[]) => void;
  openModalAtImage: (id: string, fallbackIndex: number | null, orderedIds?: string[]) => void;
  selectedIds: string[];
  selectImage: (
    image: ImageFile,
    orderedIds: string[],
    options?: { range?: boolean; toggle?: boolean }
  ) => void;
  clearSelection: () => void;

  // Right preview panel
  previewTabIds: string[];
  pinnedPreviewIds: string[];
  activePreviewId: string | null;
  previewById: Record<string, ImageFile>;
  showPreviewImage: (image: ImageFile, options?: { makeActive?: boolean }) => void;
  openPreviewTab: (image: ImageFile, options?: { makeActive?: boolean; pin?: boolean }) => void;
  setActivePreviewId: (id: string | null) => void;
  closePreviewTab: (id: string) => void;
  reorderPreviewTab: (id: string, destinationIndex: number) => void;
  togglePinPreviewTab: (id: string) => void;
  closedPreviewTabCount: number;
  restoreLastClosedPreview: () => void;
  closeAllPreviews: () => void;
  seenImageIds: Record<string, true>;
  markImageSeen: (id: string) => void;
  revealImageId: string | null;
  requestRevealImage: (id: string) => void;
  consumeRevealImage: () => void;

  // Settings
  keyBindings: KeyBindings;
  setKeyBindings: (kb: KeyBindings) => Promise<SharedSettingsSaveResult>;
  confirmBeforeDelete: boolean;
  setConfirmBeforeDelete: (v: boolean) => Promise<SharedSettingsSaveResult>;
  thumbnailStatusBorders: ThumbnailStatusBorderSettings;
  setThumbnailStatusBorders: (v: ThumbnailStatusBorderSettingsPatch) => Promise<SharedSettingsSaveResult>;
  showSettings: boolean;
  setShowSettings: (v: boolean) => void;

  // View settings
  view: ViewSettings;
  setView: (v: Partial<ViewSettings>) => void;
  setSearchScrollPosition: (key: string, value: number) => void;
  getSearchScrollPosition: (key: string) => number | null;

  // lightweight profiler
  perfEnabled: boolean;
  setPerfEnabled: (v: boolean) => void;
  perfStats: { searchCount: number; lastSearchMs: number; avgSearchMs: number };

  // Actions
  startScan: (options?: { full?: boolean; dir?: string; onComplete?: (dir: string) => void; preserveViewer?: boolean }) => void;
  cancelScan: () => void;
  deleteImage: (id: string, options?: DeleteImageOptions) => Promise<boolean>;
  openExternal: (id: string, options?: OpenExternalOptions) => Promise<boolean>;
  totalIndexed: number;
  setTotalIndexed: (n: number) => void;
}

const ImageContext = createContext<Ctx | undefined>(undefined);

export function ImageProvider({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<'landing' | 'scanning' | 'viewer'>('landing');
  const [dirPath, setDirPath] = useState('');
  const [indexToken, setIndexToken] = useState<string | null>(null);
  const [scanProgress, setScanProgress] = useState<{ processed: number; total: number; newFiles: number; stage?: 'preparing' | 'scanning' | 'complete'; message?: string } | null>(null);
  const [scanError, setScanError] = useState<string | null>(null);
  const scanRunRef = useRef(0);
  const activeScanRunRef = useRef<{ runId: number; cancelTransport: () => void } | null>(null);

  const [searchQuery, setSearchQueryRaw] = useState('');
  const [searchResults, setSearchResults] = useState<Array<ImageFile | null>>([]);
  const [searchTotal, setSearchTotal] = useState(0);
  const [isSearching, setIsSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [searchErrorKind, setSearchErrorKind] = useState<SearchErrorKind | null>(null);
  const imageSessionExpiredReportedRef = useRef(false);

  const [favorites, setFavorites] = useState<Record<string, number>>({});
  const [showFavOnlyState, setShowFavOnlyState] = useState(false);
  const [showUnfavOnlyState, setShowUnfavOnlyState] = useState(false);
  const [favoriteFilterLevels, setFavoriteFilterLevels] = useState<FavoriteFilterLevel[]>([]);
  const [showEnhancedOnlyState, setShowEnhancedOnlyState] = useState(false);
  const [enhancedSourceIds, setEnhancedSourceIds] = useState<Record<string, true>>({});
  const [enhanceJobsActive, setEnhanceJobsActive] = useState(false);

  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [modalImageIds, setModalImageIdsState] = useState<string[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [selectionAnchorId, setSelectionAnchorId] = useState<string | null>(null);
  const [previewTabIds, setPreviewTabIds] = useState<string[]>([]);
  const [pinnedPreviewIds, setPinnedPreviewIds] = useState<string[]>([]);
  const [activePreviewId, setActivePreviewIdState] = useState<string | null>(null);
  const [previewById, setPreviewById] = useState<Record<string, ImageFile>>({});
  const [closedPreviewStack, setClosedPreviewStack] = useState<string[]>([]);
  const [closedPreviewById, setClosedPreviewById] = useState<Record<string, ImageFile>>({});
  const [keyBindings, setKeyBindingsState] = useState<KeyBindings>(DEFAULT_KEY_BINDINGS);
  const [confirmBeforeDelete, setConfirmBeforeDeleteState] = useState(true);
  const [thumbnailStatusBorders, setThumbnailStatusBordersState] = useState<ThumbnailStatusBorderSettings>(() => ({
    favorite: { ...DEFAULT_THUMBNAIL_STATUS_BORDERS.favorite },
    enhanced: { ...DEFAULT_THUMBNAIL_STATUS_BORDERS.enhanced },
  }));
  const [showSettings, setShowSettings] = useState(false);
  const [totalIndexed, setTotalIndexed] = useState(0);
  const [view, setViewState] = useState<ViewSettings>(DEFAULT_VIEW);
  const [perfEnabled, setPerfEnabledState] = useState(false);
  const [perfStats, setPerfStats] = useState({ searchCount: 0, lastSearchMs: 0, avgSearchMs: 0 });
  const [seenImageIds, setSeenImageIds] = useState<Record<string, true>>({});
  const [revealImageId, setRevealImageId] = useState<string | null>(null);
  const [uiPreferencesHydrated, setUiPreferencesHydrated] = useState(false);
  const [previewTabsPersistenceReady, setPreviewTabsPersistenceReady] = useState(false);
  const keyBindingsMutationGenerationRef = useRef(0);
  const confirmDeleteMutationGenerationRef = useRef(0);
  const thumbnailStatusBordersMutationGenerationRef = useRef(0);
  const keyBindingsSaveQueueRef = useRef<Promise<void>>(Promise.resolve());
  const confirmDeleteSaveQueueRef = useRef<Promise<void>>(Promise.resolve());
  const thumbnailStatusBordersSaveQueueRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    const retainedIds = new Set(closedPreviewStack);
    setClosedPreviewById((previous) => {
      const keys = Object.keys(previous);
      if (keys.every((id) => retainedIds.has(id))) return previous;
      const next: Record<string, ImageFile> = {};
      for (const id of closedPreviewStack) {
        if (previous[id]) next[id] = previous[id];
      }
      return next;
    });
  }, [closedPreviewStack]);

  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const scrollMemoryRef = useRef<Record<string, number>>({});
  const scrollMemoryFlushRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const seenImageIdsRef = useRef<Record<string, true>>({});
  const seenImagesFlushRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const favoritesFlushRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const viewSettingsFlushRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingViewSettingsRef = useRef<ViewSettings | null>(null);
  const searchQueryRef = useRef('');
  const favoritesRef = useRef<Record<string, number>>({});
  const favoriteLocalStorageReadOnlyRef = useRef(false);
  const favoriteServerBaseRef = useRef<Record<string, number>>({});
  const favoriteSharedSnapshotKnownRef = useRef(false);
  const favoritesHydratedRef = useRef(false);
  const favoriteDirtyIdsRef = useRef<Set<string>>(new Set());
  const favoritePendingBaseRef = useRef<Record<string, number>>({});
  const favoritePendingBaseKnownIdsRef = useRef<Set<string>>(new Set());
  const favoritePendingRevisionRef = useRef<string | null>(null);
  const favoritePendingJournalQuarantinedRef = useRef(false);
  const favoriteWriteInFlightRef = useRef(false);
  const favoriteFlushRequestedRef = useRef(false);
  const favoriteFlushFunctionRef = useRef<(lifecycle: boolean) => void>(() => {});
  const favoriteRefreshInFlightRef = useRef(false);
  const favoriteSharedWriteVersionRef = useRef(0);
  const favoriteLegacyMigrationIdsRef = useRef<Set<string>>(new Set());
  const favoriteLegacyMigrationCompleteRef = useRef(false);
  const favoriteLegacyMigrationSnapshotRef = useRef<Record<string, number>>({});
  const favoriteLegacyMigrationReadableRef = useRef(false);
  const providerMountedRef = useRef(true);
  const seenSharedDirtyRef = useRef(false);
  const seenLastLifecycleBodyRef = useRef<string | null>(null);
  const searchMetaRef = useRef<{
    query: string;
    sortBy: SortBy;
    randomSeed: string;
    dateFrom: string;
    dateTo: string;
    hiddenFolders: string[];
    hiddenFoldersKey: string;
    dirPath: string;
    indexToken: string | null;
  }>({
    query: '',
    sortBy: DEFAULT_VIEW.sortBy,
    randomSeed: DEFAULT_VIEW.randomSeed,
    dateFrom: '',
    dateTo: '',
    hiddenFolders: [],
    hiddenFoldersKey: '',
    dirPath: '',
    indexToken: null,
  });
  const searchGenerationRef = useRef(0);
  const committedSearchGenerationRef = useRef(0);
  const lastFailedSearchPageRef = useRef<number | null>(null);
  const searchResultsRef = useRef<Array<ImageFile | null>>([]);
  const searchTotalRef = useRef(0);
  const pendingPreviewTabsRestoreRef = useRef<PersistedPreviewTabs | null>(null);
  const previewTabsHadStoredValueRef = useRef(false);
  const previewTabsUserModifiedRef = useRef(false);
  const loadedPagesRef = useRef<Set<string>>(new Set());
  const pendingPagesRef = useRef<Set<string>>(new Set());
  const pendingSearchPagePromisesRef = useRef<Map<string, Promise<void>>>(new Map());
  const pendingSearchControllersRef = useRef<Map<string, AbortController>>(new Map());
  const warmedThumbDirRef = useRef('');
  const activeCatalogIdentityRef = useRef('');
  const selectedIndexRef = useRef<number | null>(null);
  const activePreviewIdRef = useRef<string | null>(null);
  const previewTabIdsRef = useRef<string[]>([]);
  const pinnedPreviewIdsRef = useRef<string[]>([]);
  const favoriteFilterNavigationRef = useRef<FavoriteFilterNavigationSnapshot>({
    showFavOnly: false,
    showUnfavOnly: false,
    favoriteFilterLevels: [],
    showEnhancedOnly: false,
    enhancedSourceIds: {},
  });
  const pendingFavoriteFilterMutationRef = useRef<PendingFavoriteFilterMutation | null>(null);
  useEffect(() => {
    selectedIndexRef.current = selectedIndex;
    activePreviewIdRef.current = activePreviewId;
    previewTabIdsRef.current = previewTabIds;
    pinnedPreviewIdsRef.current = pinnedPreviewIds;
    favoriteFilterNavigationRef.current = {
      showFavOnly: showFavOnlyState,
      showUnfavOnly: showUnfavOnlyState,
      favoriteFilterLevels,
      showEnhancedOnly: showEnhancedOnlyState,
      enhancedSourceIds,
    };
  }, [
    activePreviewId,
    enhancedSourceIds,
    favoriteFilterLevels,
    pinnedPreviewIds,
    previewTabIds,
    selectedIndex,
    showEnhancedOnlyState,
    showFavOnlyState,
    showUnfavOnlyState,
  ]);
  const PAGE_SIZE = 100;
  const buildHiddenFoldersKey = useCallback(
    (folders: string[]) => folders.slice().sort().join('\u0001'),
    []
  );
  const flushViewSettings = useCallback(() => {
    const snapshot = pendingViewSettingsRef.current;
    pendingViewSettingsRef.current = null;
    if (snapshot) writeJsonLocalStorage('pvu_view', snapshot);
  }, []);
  const scheduleViewSettingsPersist = useCallback((next: ViewSettings) => {
    pendingViewSettingsRef.current = next;
    if (viewSettingsFlushRef.current) {
      clearTimeout(viewSettingsFlushRef.current);
    }
    viewSettingsFlushRef.current = setTimeout(() => {
      viewSettingsFlushRef.current = null;
      flushViewSettings();
    }, VIEW_SETTINGS_FLUSH_DELAY_MS);
  }, [flushViewSettings]);
  const persistFavoritesLocal = useCallback((snapshot: Record<string, number>) => {
    if (favoriteLocalStorageReadOnlyRef.current) return;
    const serialized = JSON.stringify(snapshot);
    try {
      localStorage.setItem('pvu_favorites', serialized);
      if (Object.keys(snapshot).length > 0) {
        localStorage.setItem('pvu_favorites_backup', serialized);
      }
    } catch { /* ignore */ }
  }, []);
  const completeFavoriteLegacyMigration = useCallback(() => {
    try {
      localStorage.setItem(FAVORITES_SHARED_MIGRATION_STORAGE_KEY, '1');
      favoriteLegacyMigrationCompleteRef.current = true;
      favoriteLegacyMigrationIdsRef.current.clear();
    } catch { /* retry safely on the next hydration */ }
  }, []);
  const persistPendingFavoritesJournal = useCallback(() => {
    const dirtyIds = [...favoriteDirtyIdsRef.current];
    if (dirtyIds.length === 0) {
      // A v1 journal cannot be replayed safely without its exact local mirror.
      // Preserve it byte-for-byte until an explicit user mutation creates v2.
      if (favoritePendingJournalQuarantinedRef.current) return;
      favoritePendingRevisionRef.current = null;
      try {
        localStorage.removeItem(FAVORITES_PENDING_STORAGE_KEY);
      } catch { /* ignore */ }
      return;
    }
    const revision = favoritePendingRevisionRef.current ?? createPersistenceRevision();
    favoritePendingRevisionRef.current = revision;
    const desiredFavorites: Record<string, number> = {};
    for (const id of dirtyIds) {
      const level = favoritesRef.current[id] ?? 0;
      if (level > 0) desiredFavorites[id] = level;
    }
    writeJsonLocalStorage(FAVORITES_PENDING_STORAGE_KEY, {
      version: 2,
      revision,
      dirtyIds,
      baseFavorites: favoritePendingBaseRef.current,
      baseKnownIds: [...favoritePendingBaseKnownIdsRef.current],
      desiredFavorites,
    } satisfies PendingFavoritesJournal);
  }, []);
  const flushFavoriteToShared = useCallback((lifecycle: boolean) => {
    if (!favoritesHydratedRef.current || favoriteDirtyIdsRef.current.size === 0) return;
    if (favoriteWriteInFlightRef.current) {
      if (!lifecycle) favoriteFlushRequestedRef.current = true;
      return;
    }

    const dirtyIds = [...favoriteDirtyIdsRef.current];
    if (dirtyIds.some((id) => !favoritePendingBaseKnownIdsRef.current.has(id))) return;
    const revision = favoritePendingRevisionRef.current;
    if (!revision) return;
    const snapshot = favoritesRef.current;
    const favoritesPayload: Record<string, number> = {};
    const basePayload: Record<string, number> = {};
    for (const id of dirtyIds) {
      const level = snapshot[id] ?? 0;
      const baseLevel = favoritePendingBaseRef.current[id] ?? 0;
      if (level > 0) favoritesPayload[id] = level;
      if (baseLevel > 0) basePayload[id] = baseLevel;
    }
    const body = JSON.stringify({ favorites: favoritesPayload, baseFavorites: basePayload });
    if (lifecycle && utf8ByteLength(body) > KEEPALIVE_PAYLOAD_LIMIT_BYTES) {
      // The browser caps all in-flight keepalive bodies to roughly 64 KiB.
      // The durable local journal retries an oversized exact delta next load.
      return;
    }

    favoriteWriteInFlightRef.current = true;
    void fetch('/api/favorites', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body,
      ...(lifecycle ? { keepalive: true } : {}),
    })
      .then(async (response) => {
        if (!response.ok) return;
        const data = await response.json();
        if (data?.malformed) return;
        const serverFavorites = normalizeFavorites(data?.favorites);
        if (!providerMountedRef.current) return;
        favoriteSharedWriteVersionRef.current += 1;
        const migrationIds = favoriteLegacyMigrationIdsRef.current;
        if (migrationIds.size > 0 && [...migrationIds].every(
          (id) => (serverFavorites[id] ?? 0) === (snapshot[id] ?? 0)
        )) {
          completeFavoriteLegacyMigration();
        }
        let storedJournal: PendingFavoritesJournal | null = null;
        try {
          storedJournal = readPendingFavoritesJournal(
            localStorage.getItem(FAVORITES_PENDING_STORAGE_KEY)
          );
        } catch {
          return;
        }
        const currentRevision = favoritePendingRevisionRef.current;
        if (!currentRevision || storedJournal?.revision !== currentRevision) return;

        favoriteServerBaseRef.current = serverFavorites;
        favoriteSharedSnapshotKnownRef.current = true;
        const reconciled = reconcileFavoriteWriteResponse(
          serverFavorites,
          snapshot,
          favoritesRef.current,
        );
        favoritesRef.current = reconciled;
        persistFavoritesLocal(reconciled);
        setFavorites(reconciled);

        if (currentRevision !== revision) {
          for (const id of [...favoriteDirtyIdsRef.current]) {
            const currentLevel = reconciled[id] ?? 0;
            const serverLevel = serverFavorites[id] ?? 0;
            if (currentLevel === serverLevel) {
              favoriteDirtyIdsRef.current.delete(id);
              delete favoritePendingBaseRef.current[id];
              favoritePendingBaseKnownIdsRef.current.delete(id);
              continue;
            }
            if (serverLevel > 0) favoritePendingBaseRef.current[id] = serverLevel;
            else delete favoritePendingBaseRef.current[id];
            favoritePendingBaseKnownIdsRef.current.add(id);
          }
          persistPendingFavoritesJournal();
          favoriteFlushRequestedRef.current = favoriteDirtyIdsRef.current.size > 0;
          return;
        }

        favoriteDirtyIdsRef.current.clear();
        favoritePendingBaseRef.current = {};
        favoritePendingBaseKnownIdsRef.current.clear();
        favoritePendingRevisionRef.current = null;
        persistPendingFavoritesJournal();
      })
      .catch(() => {})
      .finally(() => {
        favoriteWriteInFlightRef.current = false;
        if (!providerMountedRef.current) return;
        if (favoriteFlushRequestedRef.current && favoriteDirtyIdsRef.current.size > 0) {
          favoriteFlushRequestedRef.current = false;
          if (favoritesFlushRef.current) clearTimeout(favoritesFlushRef.current);
          favoritesFlushRef.current = setTimeout(() => {
            favoritesFlushRef.current = null;
            favoriteFlushFunctionRef.current(false);
          }, FAVORITES_FLUSH_DELAY_MS);
        }
      });
  }, [completeFavoriteLegacyMigration, persistFavoritesLocal, persistPendingFavoritesJournal]);
  useEffect(() => {
    favoriteFlushFunctionRef.current = flushFavoriteToShared;
  }, [flushFavoriteToShared]);
  const scheduleFavoriteFlush = useCallback(() => {
    if (favoritesFlushRef.current) clearTimeout(favoritesFlushRef.current);
    favoritesFlushRef.current = setTimeout(() => {
      favoritesFlushRef.current = null;
      favoriteFlushFunctionRef.current(false);
    }, FAVORITES_FLUSH_DELAY_MS);
  }, []);
  const commitFavoriteMutation = useCallback((
    next: Record<string, number>,
    changedIds: readonly string[],
  ) => {
    favoritePendingJournalQuarantinedRef.current = false;
    favoritesRef.current = next;
    for (const id of changedIds) {
      if (favoriteDirtyIdsRef.current.has(id)) continue;
      favoriteDirtyIdsRef.current.add(id);
      if (favoriteSharedSnapshotKnownRef.current) {
        const baseLevel = favoriteServerBaseRef.current[id] ?? 0;
        if (baseLevel > 0) favoritePendingBaseRef.current[id] = baseLevel;
        favoritePendingBaseKnownIdsRef.current.add(id);
      }
    }
    favoritePendingRevisionRef.current = createPersistenceRevision();
    persistFavoritesLocal(next);
    persistPendingFavoritesJournal();
    if (favoritesHydratedRef.current) scheduleFavoriteFlush();
  }, [persistFavoritesLocal, persistPendingFavoritesJournal, scheduleFavoriteFlush]);
  const reconcileFavoritesFromShared = useCallback((serverFavorites: Record<string, number>) => {
    favoriteServerBaseRef.current = serverFavorites;
    favoriteSharedSnapshotKnownRef.current = true;
    const dirtyIds = new Set(favoriteDirtyIdsRef.current);
    for (const id of dirtyIds) {
      if (favoritePendingBaseKnownIdsRef.current.has(id)) continue;
      const baseLevel = serverFavorites[id] ?? 0;
      if (baseLevel > 0) favoritePendingBaseRef.current[id] = baseLevel;
      favoritePendingBaseKnownIdsRef.current.add(id);
    }

    // A failed or malformed first GET must not permanently skip the one-time
    // Browser-local import. The same reconciliation is therefore used by the
    // first successful hydration and by a later focus/visibility recovery.
    if (!favoritePendingJournalQuarantinedRef.current
      && favoriteLegacyMigrationReadableRef.current
      && !favoriteLegacyMigrationCompleteRef.current) {
      let addedLegacyFavorite = false;
      for (const id of Object.keys(favoriteLegacyMigrationSnapshotRef.current)) {
        if (Object.hasOwn(serverFavorites, id) || dirtyIds.has(id)) continue;
        dirtyIds.add(id);
        favoriteDirtyIdsRef.current.add(id);
        delete favoritePendingBaseRef.current[id];
        favoritePendingBaseKnownIdsRef.current.add(id);
        favoriteLegacyMigrationIdsRef.current.add(id);
        addedLegacyFavorite = true;
      }
      if (addedLegacyFavorite) {
        favoritePendingRevisionRef.current ??= createPersistenceRevision();
      } else {
        completeFavoriteLegacyMigration();
      }
    }

    const merged = mergeFavorites(serverFavorites, favoritesRef.current, dirtyIds);
    favoritesRef.current = merged;
    if (!favoritePendingJournalQuarantinedRef.current) persistFavoritesLocal(merged);
    setFavorites(merged);
    persistPendingFavoritesJournal();
    if (favoriteDirtyIdsRef.current.size > 0) scheduleFavoriteFlush();
  }, [completeFavoriteLegacyMigration, persistFavoritesLocal, persistPendingFavoritesJournal, scheduleFavoriteFlush]);
  const mergeSharedSeen = useCallback((sharedSeen: Record<string, true>) => {
    const merged = mergeSeenImageIds(sharedSeen, seenImageIdsRef.current);
    seenImageIdsRef.current = merged;
    setSeenImageIds((currentSeen) => mergeSeenImageIds(sharedSeen, currentSeen));
    return merged;
  }, []);
  const syncSeenToShared = useCallback((snapshot: Record<string, true>, lifecycle = false) => {
    if (Object.keys(snapshot).length === 0) return;
    const body = JSON.stringify({ seen: snapshot });
    if (lifecycle) {
      if (utf8ByteLength(body) > KEEPALIVE_PAYLOAD_LIMIT_BYTES) return;
      if (seenLastLifecycleBodyRef.current === body) return;
      seenLastLifecycleBodyRef.current = body;
    }
    fetch('/api/seen', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body,
      ...(lifecycle ? { keepalive: true } : {}),
    })
      .then(async (response) => {
        if (!response.ok) return;
        const data = await response.json();
        if (data?.malformed) return;
        if (!providerMountedRef.current) return;
        mergeSharedSeen(normalizeSeenImageIds(data?.seen));
        seenSharedDirtyRef.current = Object.keys(seenImageIdsRef.current)
          .some((id) => !snapshot[id]);
      })
      .catch(() => {});
  }, [mergeSharedSeen]);

  // ── Load favorites + view settings from localStorage ──
  useEffect(() => {
    migrateLegacyPhotoviewerState();
    let localFavorites: Record<string, number> = {};
    let localFavoritesReadable = true;
    let localPrimaryReadable = false;
    let favoriteLegacyMigrationComplete = false;
    try {
      favoriteLegacyMigrationComplete = localStorage.getItem(FAVORITES_SHARED_MIGRATION_STORAGE_KEY) === '1';
      const stored = localStorage.getItem('pvu_favorites');
      // A backup is recovery data only when the primary key is absent. An
      // intentionally empty or malformed primary must never be replaced by it.
      const source = stored === null
        ? localStorage.getItem('pvu_favorites_backup')
        : stored;
      if (source !== null) {
        const parsed = readStoredFavoritesSnapshot(source);
        if (parsed === null) {
          favoriteLocalStorageReadOnlyRef.current = true;
          localFavoritesReadable = false;
        } else {
          localPrimaryReadable = stored !== null;
          localFavorites = parsed;
          setFavorites(localFavorites);
          favoritesRef.current = localFavorites;
        }
      }
    } catch {
      // Preserve corrupt recovery data until a user explicitly changes
      // favorites; automatic hydration must not replace the only evidence.
      favoriteLocalStorageReadOnlyRef.current = true;
      localFavoritesReadable = false;
    }
    favoriteLegacyMigrationCompleteRef.current = favoriteLegacyMigrationComplete;
    favoriteLegacyMigrationSnapshotRef.current = localFavorites;
    favoriteLegacyMigrationReadableRef.current = localFavoritesReadable;
    let pendingJournal: PendingFavoritesJournal | null = null;
    let pendingJournalSerialized: string | null = null;
    favoritePendingJournalQuarantinedRef.current = false;
    try {
      pendingJournalSerialized = localStorage.getItem(FAVORITES_PENDING_STORAGE_KEY);
      pendingJournal = readPendingFavoritesJournal(pendingJournalSerialized);
    } catch {
      favoritePendingJournalQuarantinedRef.current = true;
    }
    if (pendingJournal) {
      const canReplayExactly = pendingJournal.version === 2 || localPrimaryReadable;
      favoritePendingJournalQuarantinedRef.current = !canReplayExactly;
      if (canReplayExactly) {
        favoriteDirtyIdsRef.current = new Set(pendingJournal.dirtyIds);
        favoritePendingBaseRef.current = pendingJournal.baseFavorites;
        favoritePendingBaseKnownIdsRef.current = new Set(pendingJournal.baseKnownIds);
        favoritePendingRevisionRef.current = pendingJournal.revision;
        if (pendingJournal.desiredFavorites) {
          const restored = { ...localFavorites };
          for (const id of pendingJournal.dirtyIds) {
            const level = pendingJournal.desiredFavorites[id] ?? 0;
            if (level > 0) restored[id] = level;
            else delete restored[id];
          }
          localFavorites = restored;
          favoritesRef.current = restored;
          setFavorites(restored);
        }
      }
    } else if (pendingJournalSerialized !== null) {
      // Unknown/future/malformed recovery data is evidence, not garbage. Keep
      // it byte-for-byte until an explicit favorite mutation creates v2.
      favoritePendingJournalQuarantinedRef.current = true;
    }
    fetch('/api/favorites')
      .then(async (response) => {
        if (!response.ok) throw new Error(`favorite hydration failed: ${response.status}`);
        return response.json();
      })
      .then((data) => {
        if (data?.malformed) return;
        const serverFavorites = normalizeFavorites(data?.favorites);
        reconcileFavoritesFromShared(serverFavorites);
      })
      .catch(() => {})
      .finally(() => {
        favoritesHydratedRef.current = true;
        persistPendingFavoritesJournal();
        if (favoriteDirtyIdsRef.current.size > 0) scheduleFavoriteFlush();
      });
    try {
      const sv = localStorage.getItem('pvu_view');
      if (sv) {
        const normalizedView = normalizeStoredView(JSON.parse(sv));
        setViewState(normalizedView);
        writeJsonLocalStorage('pvu_view', normalizedView);
      }
    } catch { /* ignore */ }
    try {
      const pinned = localStorage.getItem('pvu_pinned_tabs');
      if (pinned) {
        const parsed = JSON.parse(pinned);
        if (Array.isArray(parsed)) {
          setPinnedPreviewIds(parsed.filter((v): v is string => typeof v === 'string'));
        }
      }
    } catch { /* ignore */ }
    try {
      const storedPreviewTabs = localStorage.getItem(PREVIEW_TAB_STORAGE_KEY);
      if (storedPreviewTabs) {
        previewTabsHadStoredValueRef.current = true;
        const restored = normalizePersistedPreviewTabs(JSON.parse(storedPreviewTabs));
        if (restored.tabIds.length > 0) {
          pendingPreviewTabsRestoreRef.current = restored;
        } else {
          setPreviewTabsPersistenceReady(true);
        }
      } else {
        setPreviewTabsPersistenceReady(true);
      }
    } catch {
      // A malformed snapshot intentionally restores no tabs. It is safe to
      // replace only after the user next changes the preview tab state.
      setPreviewTabsPersistenceReady(true);
    }
    try {
      const perf = localStorage.getItem('pvu_perf_enabled');
      if (perf === '1') setPerfEnabledState(true);
    } catch { /* ignore */ }
    try {
      const favOnly = localStorage.getItem('pvu_fav_only');
      if (favOnly === '1') setShowFavOnlyState(true);
      const unfavOnly = localStorage.getItem('pvu_unfav_only');
      if (unfavOnly === '1') setShowUnfavOnlyState(true);
      setFavoriteFilterLevels(readFavoriteFilterLevelsPreference(
        localStorage.getItem('pvu_fav_levels'),
        localStorage.getItem('pvu_fav_level')
      ));
      const enhancedOnly = localStorage.getItem('pvu_enhanced_only');
      if (enhancedOnly === '1') setShowEnhancedOnlyState(true);
    } catch { /* ignore */ }
    try {
      const memory = localStorage.getItem('pvu_scroll_memory');
      if (memory) {
        const parsed = JSON.parse(memory);
        if (parsed && typeof parsed === 'object') {
          scrollMemoryRef.current = parsed as Record<string, number>;
        }
      }
    } catch { /* ignore */ }
    try {
      const seen = localStorage.getItem('pvu_seen_images');
      if (seen) {
        const normalized = normalizeSeenImageIds(JSON.parse(seen));
        seenImageIdsRef.current = normalized;
        setSeenImageIds(normalized);
      }
    } catch { /* ignore */ }
    fetch('/api/seen')
      .then((response) => response.json())
      .then((data) => {
        if (data?.malformed) return;
        const localSeen = seenImageIdsRef.current;
        const merged = mergeSharedSeen(normalizeSeenImageIds(data?.seen));
        if (Object.keys(localSeen).some((id) => !data?.seen?.[id])) {
          seenSharedDirtyRef.current = true;
          syncSeenToShared(merged);
        }
      })
      .catch(() => {});
    setUiPreferencesHydrated(true);
  }, [mergeSharedSeen, persistPendingFavoritesJournal, reconcileFavoritesFromShared, scheduleFavoriteFlush, syncSeenToShared]);

  const refreshFavoritesFromShared = useCallback(async () => {
    if (!favoritesHydratedRef.current
      || favoriteWriteInFlightRef.current
      || favoriteRefreshInFlightRef.current) return;

    favoriteRefreshInFlightRef.current = true;
    const sharedWriteVersion = favoriteSharedWriteVersionRef.current;
    try {
      const response = await fetch('/api/favorites');
      if (!response.ok) return;
      const data = await response.json();
      if (data?.malformed
        || !providerMountedRef.current
        || sharedWriteVersion !== favoriteSharedWriteVersionRef.current) return;

      reconcileFavoritesFromShared(normalizeFavorites(data?.favorites));
    } catch {
      // Focus recovery is opportunistic. The existing UI and pending journal
      // remain usable when the local server is temporarily unavailable.
    } finally {
      favoriteRefreshInFlightRef.current = false;
    }
  }, [reconcileFavoritesFromShared]);

  useEffect(() => {
    try {
      localStorage.setItem('pvu_pinned_tabs', JSON.stringify(pinnedPreviewIds));
    } catch { /* ignore */ }
  }, [pinnedPreviewIds]);

  useEffect(() => {
    if (!previewTabsHadStoredValueRef.current && !previewTabsUserModifiedRef.current) return;
    if (!previewTabsPersistenceReady && !previewTabsUserModifiedRef.current) return;
    writeJsonLocalStorage(
      PREVIEW_TAB_STORAGE_KEY,
      serializePersistedPreviewTabs(previewTabIds, activePreviewId),
    );
  }, [activePreviewId, previewTabIds, previewTabsPersistenceReady]);

  useEffect(() => {
    try {
      localStorage.setItem('pvu_perf_enabled', perfEnabled ? '1' : '0');
    } catch { /* ignore */ }
  }, [perfEnabled]);

  useEffect(() => {
    if (!uiPreferencesHydrated) return;
    try {
      localStorage.setItem('pvu_fav_only', showFavOnlyState ? '1' : '0');
      localStorage.setItem('pvu_unfav_only', showUnfavOnlyState ? '1' : '0');
      localStorage.setItem('pvu_fav_levels', JSON.stringify(favoriteFilterLevels));
      localStorage.setItem('pvu_enhanced_only', showEnhancedOnlyState ? '1' : '0');
    } catch { /* ignore */ }
  }, [favoriteFilterLevels, showEnhancedOnlyState, showFavOnlyState, showUnfavOnlyState, uiPreferencesHydrated]);

  const refreshEnhancedSources = useCallback(async () => {
    try {
      const res = await fetch('/api/enhance/jobs', { cache: 'no-store' });
      const data = await res.json();
      if (!res.ok || !Array.isArray(data.jobs)) return;
      const next: Record<string, true> = {};
      let hasActiveJobs = false;
      for (const job of data.jobs) {
        if (job?.status === 'queued' || job?.status === 'running') {
          hasActiveJobs = true;
        }
        if (job?.status === 'succeeded' && typeof job.sourceId === 'string' && job.outputPath) {
          next[job.sourceId] = true;
        }
      }
      setEnhancedSourceIds(next);
      setEnhanceJobsActive(hasActiveJobs);
    } catch {
      // Enhancement filtering is best-effort; the viewer remains usable.
    }
  }, []);

  useEffect(() => {
    void refreshEnhancedSources();
    const onChanged = () => void refreshEnhancedSources();
    window.addEventListener('pvu-enhance-jobs-changed', onChanged);
    return () => window.removeEventListener('pvu-enhance-jobs-changed', onChanged);
  }, [refreshEnhancedSources]);

  useEffect(() => {
    if (!enhanceJobsActive && !showEnhancedOnlyState) return;
    const timer = window.setInterval(() => {
      void refreshEnhancedSources();
    }, 1000);
    return () => window.clearInterval(timer);
  }, [enhanceJobsActive, refreshEnhancedSources, showEnhancedOnlyState]);

  useEffect(() => {
    if (showFavOnlyState && showUnfavOnlyState) {
      setShowUnfavOnlyState(false);
    }
  }, [showFavOnlyState, showUnfavOnlyState]);

  useEffect(() => {
    searchResultsRef.current = searchResults;
  }, [searchResults]);

  useEffect(() => {
    searchTotalRef.current = searchTotal;
  }, [searchTotal]);

  const flushLifecycleState = useCallback(() => {
    if (favoritesFlushRef.current) {
      clearTimeout(favoritesFlushRef.current);
      favoritesFlushRef.current = null;
    }
    persistFavoritesLocal(favoritesRef.current);
    persistPendingFavoritesJournal();
    flushFavoriteToShared(true);
    if (viewSettingsFlushRef.current) {
      clearTimeout(viewSettingsFlushRef.current);
      viewSettingsFlushRef.current = null;
      flushViewSettings();
    }
    if (scrollMemoryFlushRef.current) {
      clearTimeout(scrollMemoryFlushRef.current);
    }
    if (seenImagesFlushRef.current) {
      clearTimeout(seenImagesFlushRef.current);
    }
    writeJsonLocalStorage('pvu_scroll_memory', scrollMemoryRef.current);
    writeJsonLocalStorage('pvu_seen_images', seenImageIdsRef.current);
    if (seenSharedDirtyRef.current) syncSeenToShared(seenImageIdsRef.current, true);
  }, [flushFavoriteToShared, flushViewSettings, persistFavoritesLocal, persistPendingFavoritesJournal, syncSeenToShared]);

  useEffect(() => {
    providerMountedRef.current = true;
    const onPageHide = () => flushLifecycleState();
    const onFocus = () => void refreshFavoritesFromShared();
    const onVisibilityChange = () => {
      if (document.visibilityState === 'hidden') flushLifecycleState();
      else void refreshFavoritesFromShared();
    };
    window.addEventListener('pagehide', onPageHide);
    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      window.removeEventListener('pagehide', onPageHide);
      window.removeEventListener('focus', onFocus);
      document.removeEventListener('visibilitychange', onVisibilityChange);
      providerMountedRef.current = false;
      flushLifecycleState();
    };
  }, [flushLifecycleState, refreshFavoritesFromShared]);

  // ── Load key bindings from server ──
  useEffect(() => {
    const controller = new AbortController();
    const keyBindingsGeneration = keyBindingsMutationGenerationRef.current;
    const confirmDeleteGeneration = confirmDeleteMutationGenerationRef.current;
    const thumbnailStatusBordersGeneration = thumbnailStatusBordersMutationGenerationRef.current;
    let active = true;

    fetch('/api/settings', { signal: controller.signal })
      .then(r => r.ok ? r.json() : null)
      .then(data => {
        if (!active || !data || typeof data !== 'object') return;
        if (keyBindingsMutationGenerationRef.current === keyBindingsGeneration && data.keyBindings) {
          setKeyBindingsState({ ...DEFAULT_KEY_BINDINGS, ...data.keyBindings });
        }
        if (confirmDeleteMutationGenerationRef.current === confirmDeleteGeneration
          && typeof data.confirmBeforeDelete === 'boolean') {
          setConfirmBeforeDeleteState(data.confirmBeforeDelete);
        }
        if (thumbnailStatusBordersMutationGenerationRef.current === thumbnailStatusBordersGeneration) {
          setThumbnailStatusBordersState(normalizeThumbnailStatusBorders(data.thumbnailStatusBorders));
        }
      })
      .catch(() => {});

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const setKeyBindings = useCallback((kb: KeyBindings): Promise<SharedSettingsSaveResult> => {
    const operation = keyBindingsSaveQueueRef.current.then(async () => {
      const result = await saveSharedSettings({ keyBindings: kb });
      if (result.ok && providerMountedRef.current) {
        keyBindingsMutationGenerationRef.current += 1;
        setKeyBindingsState(kb);
      }
      return result;
    });
    keyBindingsSaveQueueRef.current = operation.then(() => undefined, () => undefined);
    return operation;
  }, []);

  const setConfirmBeforeDelete = useCallback((v: boolean): Promise<SharedSettingsSaveResult> => {
    const operation = confirmDeleteSaveQueueRef.current.then(async () => {
      const result = await saveSharedSettings({ confirmBeforeDelete: v });
      if (result.ok && providerMountedRef.current) {
        confirmDeleteMutationGenerationRef.current += 1;
        setConfirmBeforeDeleteState(v);
      }
      return result;
    });
    confirmDeleteSaveQueueRef.current = operation.then(() => undefined, () => undefined);
    return operation;
  }, []);

  const setThumbnailStatusBorders = useCallback((
    value: ThumbnailStatusBorderSettingsPatch,
  ): Promise<SharedSettingsSaveResult> => {
    const normalized = normalizeThumbnailStatusBorderPatch(value);
    if (!normalized) {
      return Promise.resolve({
        ok: false,
        error: 'Choose at least one valid thumbnail border preference to save.',
      });
    }
    const operation = thumbnailStatusBordersSaveQueueRef.current.then(async () => {
      const result = await saveSharedSettings({ thumbnailStatusBorders: normalized });
      if (result.ok && providerMountedRef.current) {
        thumbnailStatusBordersMutationGenerationRef.current += 1;
        setThumbnailStatusBordersState(result.thumbnailStatusBorders
          ?? ((current) => ({ ...current, ...normalized })));
      }
      return result;
    });
    thumbnailStatusBordersSaveQueueRef.current = operation.then(() => undefined, () => undefined);
    return operation;
  }, []);

  const queueFavoriteFilterNavigation = useCallback((
    previousFavorites: Record<string, number>,
    nextFavorites: Record<string, number>,
    mutatedIds: readonly string[]
  ) => {
    const filter = favoriteFilterNavigationRef.current;
    if (!filter.showFavOnly && !filter.showUnfavOnly) return;

    const results = searchResultsRef.current;
    const modalIndex = selectedIndexRef.current;
    const modalId = modalIndex !== null ? results[modalIndex]?.id ?? null : null;
    const currentId = modalId ?? activePreviewIdRef.current;
    if (!currentId || !mutatedIds.includes(currentId)) return;

    const existing = pendingFavoriteFilterMutationRef.current;
    if (existing && existing.currentId === currentId && sameFavoriteFilter(existing.filter, filter)) {
      existing.expectedLevel = nextFavorites[currentId] ?? 0;
      return;
    }

    pendingFavoriteFilterMutationRef.current = {
      currentId,
      previousOrderedIds: getClientFilteredLoadedIds({
        searchResults: results,
        favorites: previousFavorites,
        showFavOnly: filter.showFavOnly,
        showUnfavOnly: filter.showUnfavOnly,
        favoriteFilterLevels: filter.favoriteFilterLevels,
        showEnhancedOnly: filter.showEnhancedOnly,
        enhancedSourceIds: filter.enhancedSourceIds,
      }),
      searchResults: results,
      filter,
      modalWasOpen: modalIndex !== null,
      activeWasOpenTab: previewTabIdsRef.current.includes(currentId),
      expectedLevel: nextFavorites[currentId] ?? 0,
    };
  }, []);

  const toggleFavorite = useCallback((id: string) => {
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    const next = { ...previous };
    if ((next[id] ?? 0) > 0) delete next[id];
    else next[id] = 1;
    queueFavoriteFilterNavigation(previous, next, [id]);
    commitFavoriteMutation(next, [id]);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  const cycleFavoriteLevel = useCallback((id: string) => {
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    const next = { ...previous };
    const current = next[id] ?? 0;
    next[id] = Math.min(MAX_FAVORITE_LEVEL, current + 1);
    queueFavoriteFilterNavigation(previous, next, [id]);
    commitFavoriteMutation(next, [id]);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  const clearFavorite = useCallback((id: string) => {
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    if (!(id in previous)) return;
    const next = { ...previous };
    delete next[id];
    queueFavoriteFilterNavigation(previous, next, [id]);
    commitFavoriteMutation(next, [id]);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  const setFavoriteLevels = useCallback((ids: readonly string[], level: number) => {
    if (!Number.isInteger(level) || level < 0 || level > MAX_FAVORITE_LEVEL) return;
    const targetIds = [...new Set(ids.filter((id): id is string => typeof id === 'string' && id.length > 0))];
    if (targetIds.length === 0) return;
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    let next: Record<string, number> | null = null;
    const changedIds: string[] = [];
    for (const id of targetIds) {
      const current = previous[id] ?? 0;
      if (current === level) continue;
      changedIds.push(id);
      if (!next) next = { ...previous };
      if (level === 0) delete next[id];
      else next[id] = level;
    }
    if (!next) return;
    queueFavoriteFilterNavigation(previous, next, changedIds);
    commitFavoriteMutation(next, changedIds);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  const adjustFavoriteLevels = useCallback((ids: readonly string[], delta: number) => {
    if (!Number.isInteger(delta) || delta === 0) return;
    const targetIds = [...new Set(ids.filter((id): id is string => typeof id === 'string' && id.length > 0))];
    if (targetIds.length === 0) return;
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    let next: Record<string, number> | null = null;
    const changedIds: string[] = [];
    for (const id of targetIds) {
      const current = previous[id] ?? 0;
      const level = Math.max(0, Math.min(MAX_FAVORITE_LEVEL, current + delta));
      if (current === level) continue;
      changedIds.push(id);
      if (!next) next = { ...previous };
      if (level === 0) delete next[id];
      else next[id] = level;
    }
    if (!next) return;
    queueFavoriteFilterNavigation(previous, next, changedIds);
    commitFavoriteMutation(next, changedIds);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  const decreaseFavoriteLevel = useCallback((id: string) => {
    favoriteLocalStorageReadOnlyRef.current = false;
    const previous = favoritesRef.current;
    const current = previous[id] ?? 0;
    if (current <= 0) return;
    const next = { ...previous };
    if (current <= 1) delete next[id];
    else next[id] = current - 1;
    queueFavoriteFilterNavigation(previous, next, [id]);
    commitFavoriteMutation(next, [id]);
    setFavorites(next);
  }, [commitFavoriteMutation, queueFavoriteFilterNavigation]);

  useEffect(() => {
    const pending = pendingFavoriteFilterMutationRef.current;
    if (!pending) return;
    pendingFavoriteFilterMutationRef.current = null;

    const currentFilter = favoriteFilterNavigationRef.current;
    if (
      (!currentFilter.showFavOnly && !currentFilter.showUnfavOnly)
      || !sameFavoriteFilter(pending.filter, currentFilter)
      || searchResultsRef.current !== pending.searchResults
      || (favorites[pending.currentId] ?? 0) !== pending.expectedLevel
    ) return;

    const results = pending.searchResults;
    const currentModalIndex = selectedIndexRef.current;
    const currentModalId = currentModalIndex !== null ? results[currentModalIndex]?.id ?? null : null;
    const currentUiId = pending.modalWasOpen ? currentModalId : activePreviewIdRef.current;
    if (currentUiId !== pending.currentId) return;

    const nextOrderedIds = getClientFilteredLoadedIds({
      searchResults: results,
      favorites,
      showFavOnly: pending.filter.showFavOnly,
      showUnfavOnly: pending.filter.showUnfavOnly,
      favoriteFilterLevels: pending.filter.favoriteFilterLevels,
      showEnhancedOnly: pending.filter.showEnhancedOnly,
      enhancedSourceIds: pending.filter.enhancedSourceIds,
    });
    const navigation = nextAfterClientFilterMutation(
      pending.currentId,
      pending.previousOrderedIds,
      nextOrderedIds
    );
    if (!navigation.shouldSync) return;

    const nextImageIndex = navigation.nextId
      ? results.findIndex((image) => image?.id === navigation.nextId)
      : -1;
    const nextImage = nextImageIndex >= 0 ? results[nextImageIndex] : null;
    const nextId = nextImage?.id ?? null;

    if (pending.modalWasOpen) {
      setModalImageIdsState(navigation.orderedIds);
      setSelectedIndex(nextImageIndex >= 0 ? nextImageIndex : null);
    }
    setSelectedIds(nextId ? [nextId] : []);
    setSelectionAnchorId(nextId);
    setActivePreviewIdState(nextId);
    if (nextImage) {
      setPreviewById((previous) => ({ ...previous, [nextImage.id]: nextImage }));
      if (pending.activeWasOpenTab) {
        previewTabsUserModifiedRef.current = true;
        setPreviewTabIds((previous) => (
          previous.includes(nextImage.id) ? previous : [...previous, nextImage.id]
        ));
      }
    }
  }, [favorites]);

  const setShowFavOnly = useCallback((value: boolean) => {
    setShowFavOnlyState(value);
    if (value) {
      setShowUnfavOnlyState(false);
    }
  }, []);

  const toggleFavoriteFilterLevel = useCallback((value: FavoriteFilterLevel) => {
    setFavoriteFilterLevels((prev) => toggleFavoriteFilterLevelValue(prev, value));
    setShowFavOnlyState(true);
    setShowUnfavOnlyState(false);
  }, []);

  const clearFavoriteFilterLevels = useCallback(() => {
    setFavoriteFilterLevels([]);
  }, []);

  const setShowUnfavOnly = useCallback((value: boolean) => {
    setShowUnfavOnlyState(value);
    if (value) {
      setShowFavOnlyState(false);
    }
  }, []);

  const setShowEnhancedOnly = useCallback((value: boolean) => {
    setShowEnhancedOnlyState(value);
    if (value) void refreshEnhancedSources();
  }, [refreshEnhancedSources]);

  // ── Search (debounced) ──
  const getSearchPageKey = useCallback((generation: number, page: number) => `${generation}:${page}`, []);

  const isSearchPageComplete = useCallback((page: number) => {
    const total = searchTotalRef.current;
    if (total <= 0) return false;
    const start = page * PAGE_SIZE;
    const end = Math.min(total, start + PAGE_SIZE);
    if (start >= end) return true;
    const results = searchResultsRef.current;
    for (let i = start; i < end; i++) {
      if (!results[i]) return false;
    }
    return true;
  }, []);

  const abortPendingSearchRequests = useCallback(() => {
    for (const controller of pendingSearchControllersRef.current.values()) {
      controller.abort();
    }
    pendingSearchControllersRef.current = new Map();
  }, []);

  const doSearchPage = useCallback(async (
    query: string,
    page: number,
    sortBy: SortBy,
    randomSeed: string,
    dateFrom: string,
    dateTo: string,
    hiddenFolders: string[],
    currentDirPath: string,
    currentIndexToken: string | null,
    generation = searchGenerationRef.current
  ) => {
    const pageKey = getSearchPageKey(generation, page);
    const existingPagePromise = pendingSearchPagePromisesRef.current.get(pageKey);
    if (existingPagePromise) {
      await existingPagePromise;
      return;
    }
    if (loadedPagesRef.current.has(pageKey) && isSearchPageComplete(page)) return;
    loadedPagesRef.current.delete(pageKey);

    let resolvePagePromise!: () => void;
    const pagePromise = new Promise<void>((resolve) => {
      resolvePagePromise = resolve;
    });
    pendingSearchPagePromisesRef.current.set(pageKey, pagePromise);

    pendingPagesRef.current.add(pageKey);
    setIsSearching(true);
    if (generation === searchGenerationRef.current) {
      setSearchError(null);
      setSearchErrorKind(null);
    }
    const startedAt = performance.now();
    const abortController = new AbortController();
    pendingSearchControllersRef.current.set(pageKey, abortController);
    try {
      const fromParam = dateFrom ? `&dateFrom=${encodeURIComponent(dateFrom)}` : '';
      const toParam = dateTo ? `&dateTo=${encodeURIComponent(dateTo)}` : '';
      const randomSeedParam = sortBy === 'random'
        ? `&randomSeed=${encodeURIComponent(randomSeed || DEFAULT_VIEW.randomSeed)}`
        : '';
      const hiddenParam = hiddenFolders.length > 0
        ? `&hiddenFolders=${encodeURIComponent(JSON.stringify(hiddenFolders))}`
        : '';
      const dirParam = currentDirPath ? `&dir=${encodeURIComponent(currentDirPath)}` : '';
      const indexTokenParam = currentIndexToken ? `&indexToken=${encodeURIComponent(currentIndexToken)}` : '';
      const url = `/api/search?q=${encodeURIComponent(query)}&page=${page}&size=${PAGE_SIZE}&sortBy=${sortBy}${randomSeedParam}${fromParam}${toParam}${hiddenParam}${dirParam}${indexTokenParam}`;
      const res = await fetch(url, { signal: abortController.signal });
      const data = await res.json().catch(() => null) as SearchResponse | { error?: unknown } | null;
      if (!res.ok) {
        const errorData = data as { error?: unknown } | null;
        const message = errorData && typeof errorData.error === 'string'
          ? errorData.error
          : `Search request failed (${res.status}).`;
        throw new SearchRequestError(message, res.status === 410 ? 'session-expired' : 'transient');
      }
      if (!data || !Array.isArray((data as SearchResponse).results)
        || !Number.isFinite((data as SearchResponse).total)) {
        throw new Error('Search returned an invalid response.');
      }
      const searchData = data as SearchResponse;

      // Ignore stale responses from previous query/sort/date state.
      const meta = searchMetaRef.current;
      if (
        generation !== searchGenerationRef.current ||
        meta.query !== query ||
        meta.sortBy !== sortBy ||
        meta.randomSeed !== randomSeed ||
        meta.dateFrom !== dateFrom ||
        meta.dateTo !== dateTo ||
        meta.hiddenFoldersKey !== buildHiddenFoldersKey(hiddenFolders) ||
        meta.dirPath !== currentDirPath ||
        meta.indexToken !== currentIndexToken
      ) {
        return;
      }

      const withFavs = searchData.results.map((img) => ({
        ...img,
        fileUrl: withIndexToken(img.fileUrl, currentIndexToken),
        displayUrl: withIndexToken(img.displayUrl, currentIndexToken),
        fullUrl: withIndexToken(img.fullUrl, currentIndexToken),
        isFavorite: !!favoritesRef.current[img.id],
      }));

      const replacePreviousResults = committedSearchGenerationRef.current !== generation;
      committedSearchGenerationRef.current = generation;
      lastFailedSearchPageRef.current = null;
      searchTotalRef.current = searchData.total;
      setSearchTotal((prevTotal) => (prevTotal === searchData.total ? prevTotal : searchData.total));
      const previousResults = searchResultsRef.current;
      const needsResize = replacePreviousResults || previousResults.length !== searchData.total;
      const nextResults = needsResize
        ? Array<ImageFile | null>(searchData.total).fill(null)
        : [...previousResults];
      if (needsResize && !replacePreviousResults) {
        const keep = Math.min(previousResults.length, nextResults.length);
        for (let i = 0; i < keep; i++) nextResults[i] = previousResults[i];
      }
      const base = page * PAGE_SIZE;
      for (let i = 0; i < withFavs.length; i++) {
        const idx = base + i;
        if (idx < nextResults.length) nextResults[idx] = withFavs[i];
      }
      searchResultsRef.current = nextResults;
      setSearchResults(nextResults);
      loadedPagesRef.current.add(pageKey);
      setPerfStats((prev) => {
        const lastSearchMs = Math.round((performance.now() - startedAt) * 10) / 10;
        const searchCount = prev.searchCount + 1;
        const avgSearchMs = Math.round((((prev.avgSearchMs * prev.searchCount) + lastSearchMs) / searchCount) * 10) / 10;
        return { searchCount, lastSearchMs, avgSearchMs };
      });
    } catch (e) {
      if (abortController.signal.aborted) return;
      if (generation !== searchGenerationRef.current) return;
      lastFailedSearchPageRef.current = page;
      const message = e instanceof Error && e.message
        ? e.message
        : 'Search failed. Please retry.';
      const kind = e instanceof SearchRequestError ? e.kind : 'transient';
      setSearchError(message);
      setSearchErrorKind(kind);
      console.error('Search failed', e);
    } finally {
      pendingSearchControllersRef.current.delete(pageKey);
      if (pendingSearchPagePromisesRef.current.get(pageKey) === pagePromise) {
        pendingSearchPagePromisesRef.current.delete(pageKey);
      }
      resolvePagePromise();
      if (generation === searchGenerationRef.current) {
        pendingPagesRef.current.delete(pageKey);
      }
      if (generation === searchGenerationRef.current && pendingPagesRef.current.size === 0) {
        setIsSearching(false);
      }
    }
  }, [buildHiddenFoldersKey, getSearchPageKey, isSearchPageComplete]);

  const resolveModalNavigationTarget = useCallback(async (
    currentIndex: number,
    intent: SparseModalNavigationIntent
  ): Promise<ModalNavigationResolution> => {
    const generation = searchGenerationRef.current;
    const committedGeneration = committedSearchGenerationRef.current;
    const total = searchTotalRef.current;
    const meta = searchMetaRef.current;
    const metaSnapshot = {
      query: meta.query,
      sortBy: meta.sortBy,
      randomSeed: meta.randomSeed,
      dateFrom: meta.dateFrom,
      dateTo: meta.dateTo,
      hiddenFolders: meta.hiddenFolders,
      hiddenFoldersKey: meta.hiddenFoldersKey,
      dirPath: meta.dirPath,
      indexToken: meta.indexToken,
    };
    const filter = favoriteFilterNavigationRef.current;
    const favoritesSnapshot = favoritesRef.current;
    const enhancedSourceIdsSnapshot = filter.enhancedSourceIds;
    const hasClientFilters = filter.showFavOnly || filter.showUnfavOnly || filter.showEnhancedOnly;

    const isCurrentWindow = () => {
      const currentMeta = searchMetaRef.current;
      if (
        generation !== searchGenerationRef.current
        || committedGeneration !== generation
        || committedSearchGenerationRef.current !== generation
        || searchTotalRef.current !== total
        || currentMeta.query !== metaSnapshot.query
        || currentMeta.sortBy !== metaSnapshot.sortBy
        || currentMeta.randomSeed !== metaSnapshot.randomSeed
        || currentMeta.dateFrom !== metaSnapshot.dateFrom
        || currentMeta.dateTo !== metaSnapshot.dateTo
        || currentMeta.hiddenFoldersKey !== metaSnapshot.hiddenFoldersKey
        || currentMeta.dirPath !== metaSnapshot.dirPath
        || currentMeta.indexToken !== metaSnapshot.indexToken
      ) return false;

      if (!hasClientFilters) return true;
      const currentFilter = favoriteFilterNavigationRef.current;
      if (!sameFavoriteFilter(filter, currentFilter)) return false;
      if ((filter.showFavOnly || filter.showUnfavOnly) && favoritesRef.current !== favoritesSnapshot) return false;
      if (filter.showEnhancedOnly && currentFilter.enhancedSourceIds !== enhancedSourceIdsSnapshot) return false;
      return true;
    };

    const filteredOrderedIds = () => hasClientFilters
      ? getClientFilteredLoadedIds({
          searchResults: searchResultsRef.current,
          favorites: favoritesSnapshot,
          showFavOnly: filter.showFavOnly,
          showUnfavOnly: filter.showUnfavOnly,
          favoriteFilterLevels: filter.favoriteFilterLevels,
          showEnhancedOnly: filter.showEnhancedOnly,
          enhancedSourceIds: enhancedSourceIdsSnapshot,
        })
      : null;

    const matchesFilter = (image: ImageFile) => {
      if (!hasClientFilters) return true;
      const level = favoritesSnapshot[image.id] ?? 0;
      const matchesFavorite = filter.showFavOnly
        ? matchesFavoriteLevel(level, filter.favoriteFilterLevels)
        : filter.showUnfavOnly
          ? level === 0
          : true;
      const matchesEnhanced = filter.showEnhancedOnly
        ? Boolean(enhancedSourceIdsSnapshot[image.id])
        : true;
      return matchesFavorite && matchesEnhanced;
    };

    if (total <= 0) return { status: 'empty', filteredOrderedIds: hasClientFilters ? [] : null };
    if (!isCurrentWindow()) return { status: 'stale' };

    const attemptedPages = new Set<number>();
    const ranges = getSparseModalNavigationRanges(currentIndex, total, intent);
    for (const range of ranges) {
      for (
        let index = range.start;
        range.step > 0 ? index <= range.end : index >= range.end;
        index += range.step
      ) {
        let image = searchResultsRef.current[index];
        if (!image) {
          const page = Math.floor(index / PAGE_SIZE);
          if (!attemptedPages.has(page)) {
            attemptedPages.add(page);
            await doSearchPage(
              metaSnapshot.query,
              page,
              metaSnapshot.sortBy,
              metaSnapshot.randomSeed,
              metaSnapshot.dateFrom,
              metaSnapshot.dateTo,
              metaSnapshot.hiddenFolders,
              metaSnapshot.dirPath,
              metaSnapshot.indexToken,
              generation,
            );
          }
          if (!isCurrentWindow()) return { status: 'stale' };
          image = searchResultsRef.current[index];
          // Never jump across an unknown slot: it may be the nearest match.
          if (!image) return { status: 'unavailable' };
        }
        if (!isCurrentWindow()) return { status: 'stale' };
        if (matchesFilter(image)) {
          return {
            status: 'found',
            index,
            id: image.id,
            filteredOrderedIds: filteredOrderedIds(),
          };
        }
      }
    }

    if (intent !== 'delete') {
      const current = searchResultsRef.current[currentIndex];
      if (current && matchesFilter(current)) {
        return {
          status: 'found',
          index: currentIndex,
          id: current.id,
          filteredOrderedIds: filteredOrderedIds(),
        };
      }
    }
    return { status: 'empty', filteredOrderedIds: filteredOrderedIds() };
  }, [doSearchPage]);

  const resetSearch = useCallback((
    query: string,
    sortBy: SortBy,
    randomSeed: string,
    dateFrom: string,
    dateTo: string,
    hiddenFolders: string[],
    currentDirPath: string,
    currentIndexToken: string | null,
  ) => {
    const preserveCurrentResults = searchMetaRef.current.dirPath === currentDirPath;
    searchMetaRef.current = {
      query,
      sortBy,
      randomSeed,
      dateFrom,
      dateTo,
      hiddenFolders,
      hiddenFoldersKey: buildHiddenFoldersKey(hiddenFolders),
      dirPath: currentDirPath,
      indexToken: currentIndexToken,
    };
    searchGenerationRef.current += 1;
    abortPendingSearchRequests();
    loadedPagesRef.current = new Set();
    pendingPagesRef.current = new Set();
    lastFailedSearchPageRef.current = null;
    setSearchError(null);
    setSearchErrorKind(null);
    if (!preserveCurrentResults) {
      searchTotalRef.current = 0;
      searchResultsRef.current = [];
      setSearchTotal(0);
      setSearchResults([]);
    }
    setIsSearching(false);
  }, [abortPendingSearchRequests, buildHiddenFoldersKey]);

  const ensureSearchRange = useCallback((startIndex: number, endIndex: number) => {
    if (startIndex > endIndex) return;
    if (searchTotal <= 0 && searchResults.length === 0) return;

    const safeStart = Math.max(0, startIndex);
    const safeEnd = Math.min(Math.max(0, endIndex), Math.max(0, searchTotal - 1));
    if (safeStart > safeEnd) return;

    const startPage = Math.floor(safeStart / PAGE_SIZE);
    const endPage = Math.floor(safeEnd / PAGE_SIZE);
    const meta = searchMetaRef.current;
    const generation = searchGenerationRef.current;
    for (let page = startPage; page <= endPage; page++) {
      const pageKey = getSearchPageKey(generation, page);
      if (pendingPagesRef.current.has(pageKey)) continue;
      if (loadedPagesRef.current.has(pageKey) && isSearchPageComplete(page)) continue;
      loadedPagesRef.current.delete(pageKey);
      void doSearchPage(
        meta.query,
        page,
        meta.sortBy,
        meta.randomSeed,
        meta.dateFrom,
        meta.dateTo,
        meta.hiddenFolders,
        meta.dirPath,
        meta.indexToken,
        generation
      );
    }
  }, [doSearchPage, getSearchPageKey, isSearchPageComplete, searchResults.length, searchTotal]);

  const retrySearch = useCallback(() => {
    if (searchErrorKind === 'session-expired') return;
    const meta = searchMetaRef.current;
    const generation = searchGenerationRef.current;
    const page = lastFailedSearchPageRef.current ?? 0;
    setSearchError(null);
    setSearchErrorKind(null);
    void doSearchPage(
      meta.query,
      page,
      meta.sortBy,
      meta.randomSeed,
      meta.dateFrom,
      meta.dateTo,
      meta.hiddenFolders,
      meta.dirPath,
      meta.indexToken,
      generation,
    );
  }, [doSearchPage, searchErrorKind]);

  const dismissSearchError = useCallback(() => {
    setSearchError(null);
    setSearchErrorKind(null);
  }, []);

  useEffect(() => {
    if (indexToken) imageSessionExpiredReportedRef.current = false;
  }, [indexToken]);

  const setSearchQuery = useCallback((q: string) => {
    setSearchQueryRaw(q);
    searchQueryRef.current = q;
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      debounceRef.current = undefined;
      resetSearch(q, view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders, dirPath, indexToken);
      void doSearchPage(q, 0, view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders, dirPath, indexToken);
    }, 150);
  }, [dirPath, doSearchPage, indexToken, resetSearch, view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders]);

  useEffect(() => {
    // A sort/date/folder/session change searches immediately with the latest
    // query. Cancel an older query timer so its captured options cannot run
    // later and roll the search window back to stale settings.
    if (debounceRef.current) {
      clearTimeout(debounceRef.current);
      debounceRef.current = undefined;
    }
    if (phase !== 'viewer') return;
    const query = searchQueryRef.current;
    resetSearch(query, view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders, dirPath, indexToken);
    void doSearchPage(query, 0, view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders, dirPath, indexToken);
    return () => {
      if (debounceRef.current) {
        clearTimeout(debounceRef.current);
        debounceRef.current = undefined;
      }
    };
  }, [view.sortBy, view.randomSeed, view.dateFrom, view.dateTo, view.hiddenFolders, dirPath, indexToken, phase, doSearchPage, resetSearch]);

  useEffect(() => {
    if (phase !== 'viewer' || !dirPath.trim() || totalIndexed <= 0 || searchTotal <= 0) return;
    const key = `${dirPath}\u0001${totalIndexed}`;
    if (warmedThumbDirRef.current === key) return;
    const timer = window.setTimeout(() => {
      if (warmedThumbDirRef.current === key) return;
      if (document.visibilityState !== 'visible') return;
      warmedThumbDirRef.current = key;
      void fetch('/api/thumbs/warm', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          dir: dirPath,
          indexToken,
          priority: 'background',
          limit: Math.min(totalIndexed, AUTO_THUMB_WARM_LIMIT),
        }),
      }).catch(() => {
        // Best-effort cache fill; visible thumbnail requests can still generate files.
      });
    }, AUTO_THUMB_WARM_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [dirPath, indexToken, phase, searchTotal, totalIndexed]);

  // Re-apply favorites flag when favorites change
  useEffect(() => {
    favoritesRef.current = favorites;
  }, [favorites]);

  // A saved preview tab only becomes live once it is present in the current
  // search index. This avoids resurrecting paths from an earlier scan and does
  // not open the modal or start any enhancement work.
  useEffect(() => {
    const persisted = pendingPreviewTabsRestoreRef.current;
    if (!persisted) return;

    const byId = new Map<string, ImageFile>();
    for (const image of searchResults) {
      if (image) byId.set(image.id, image);
    }
    const restoredIds = persisted.tabIds.filter((id) => byId.has(id));

    if (restoredIds.length > 0) {
      setPreviewById((current) => {
        const next = { ...current };
        for (const id of restoredIds) {
          const image = byId.get(id);
          if (image) next[id] = image;
        }
        return next;
      });
      setPreviewTabIds((current) => {
        const next = [
          ...restoredIds,
          ...current.filter((id) => !restoredIds.includes(id)),
        ];
        return next.length === current.length && next.every((id, index) => id === current[index])
          ? current
          : next;
      });
      setActivePreviewIdState((current) => {
        if (persisted.activeId && byId.has(persisted.activeId)) return persisted.activeId;
        if (current) return current;
        return restoredIds[0] ?? null;
      });
    }

    const currentSearchIsComplete = searchTotal > 0
      && searchResults.length === searchTotal
      && searchResults.every(Boolean);
    if (currentSearchIsComplete) {
      pendingPreviewTabsRestoreRef.current = null;
      setPreviewTabsPersistenceReady(true);
    }
  }, [searchResults, searchTotal]);

  // Search/filter refreshes may temporarily omit an open preview tab. Keep its
  // last usable snapshot until the user closes/recycles it or a different
  // catalog is successfully adopted. Matching results still refresh the
  // snapshot with current URLs/metadata.
  useEffect(() => {
    const byId = new Map<string, ImageFile>();
    for (const image of searchResults) {
      if (image) byId.set(image.id, image);
    }
    const retainedSnapshotIds = new Set([
      ...previewTabIdsRef.current,
      ...(pendingPreviewTabsRestoreRef.current?.tabIds ?? []),
    ]);

    setPreviewById((prev) => {
      if (Object.keys(prev).length === 0) return prev;
      const next: Record<string, ImageFile> = {};
      let changed = false;
      for (const id of Object.keys(prev)) {
        const matched = byId.get(id);
        if (matched) {
          next[id] = matched;
          if (matched !== prev[id]) changed = true;
        } else if (retainedSnapshotIds.has(id)) {
          next[id] = prev[id];
        } else {
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [searchResults]);

  const clearCatalogOwnedUiState = useCallback(() => {
    selectedIndexRef.current = null;
    activePreviewIdRef.current = null;
    previewTabIdsRef.current = [];
    pinnedPreviewIdsRef.current = [];
    pendingFavoriteFilterMutationRef.current = null;
    pendingPreviewTabsRestoreRef.current = null;
    previewTabsHadStoredValueRef.current = true;
    previewTabsUserModifiedRef.current = true;

    setSelectedIndex(null);
    setModalImageIdsState([]);
    setSelectedIds([]);
    setSelectionAnchorId(null);
    setRevealImageId(null);
    setPreviewTabIds([]);
    setPinnedPreviewIds([]);
    setActivePreviewIdState(null);
    setPreviewById({});
    setClosedPreviewStack([]);
    setClosedPreviewById({});
    setPreviewTabsPersistenceReady(true);

    writeJsonLocalStorage(
      PREVIEW_TAB_STORAGE_KEY,
      serializePersistedPreviewTabs([], null),
    );
    writeJsonLocalStorage('pvu_pinned_tabs', []);
  }, []);

  // ── Scan ──
  const cancelActiveScan = useCallback((resetUi: boolean) => {
    const activeRun = activeScanRunRef.current;
    if (!activeRun) return false;
    activeRun.cancelTransport();
    if (resetUi) {
      setScanProgress(null);
      setScanError(null);
      setPhase('landing');
    }
    return true;
  }, []);

  const cancelScan = useCallback(() => {
    cancelActiveScan(true);
  }, [cancelActiveScan]);

  useEffect(() => () => {
    cancelActiveScan(false);
  }, [cancelActiveScan]);

  const startScan = useCallback((options: { full?: boolean; dir?: string; onComplete?: (dir: string) => void; preserveViewer?: boolean } = {}) => {
    const scanDir = formatDirSet(parseDirSet(options.dir ?? dirPath));
    if (!scanDir || phase === 'scanning' || activeScanRunRef.current) return;
    const preserveViewer = options.preserveViewer === true && phase === 'viewer';
    const nextCatalogIdentity = catalogIdentity(scanDir);
    const replacesActiveCatalog = Boolean(activeCatalogIdentityRef.current)
      && activeCatalogIdentityRef.current !== nextCatalogIdentity;
    if (scanDir !== dirPath) setDirPath(scanDir);
    if (!preserveViewer) setIndexToken(null);
    setScanError(null);
    warmedThumbDirRef.current = '';
    if (!preserveViewer) {
      setViewState((prev) => {
        if (prev.hiddenFolders.length === 0 && !prev.dateFrom && !prev.dateTo) return prev;
        const next = { ...prev, hiddenFolders: [] as string[], dateFrom: '', dateTo: '' };
        scheduleViewSettingsPersist(next);
        return next;
      });
      setPhase('scanning');
    }
    setScanProgress({ processed: 0, total: 1, newFiles: 0, stage: 'preparing', message: 'Preparing file list...' });
    const scanRunId = scanRunRef.current + 1;
    scanRunRef.current = scanRunId;

    const params = new URLSearchParams({ dir: scanDir });
    if (options.full) params.set('full', '1');
    const es = new EventSource(`/api/scan?${params.toString()}`);
    let settled = false;
    const isCurrentRun = () => scanRunRef.current === scanRunId;
    const settleScanTransport = () => {
      if (settled) return false;
      settled = true;
      if (activeScanRunRef.current?.runId === scanRunId) {
        activeScanRunRef.current = null;
      }
      es.close();
      return true;
    };
    const cancelTransport = () => {
      if (settled) return;
      if (isCurrentRun()) scanRunRef.current = scanRunId + 1;
      settleScanTransport();
    };
    activeScanRunRef.current = { runId: scanRunId, cancelTransport };
    const failScan = (message: string) => {
      if (!isCurrentRun() || !settleScanTransport()) return;
      console.error('Scan error', message);
      setScanProgress(null);
      if (preserveViewer) {
        setSearchError(`Automatic viewer session refresh failed: ${message}`);
        setSearchErrorKind('session-expired');
        setPhase('viewer');
      } else {
        setScanError(message);
        setPhase('landing');
      }
    };
    es.onmessage = (event) => {
      if (settled || !isCurrentRun()) return;
      let data: unknown;
      try {
        data = JSON.parse(event.data);
      } catch {
        failScan('The scan stream returned malformed data.');
        return;
      }
      if (!data || typeof data !== 'object' || Array.isArray(data)) {
        failScan('The scan stream returned an unknown event.');
        return;
      }
      const eventData = data as Record<string, unknown>;
      const type = eventData.type;
      if (type !== 'progress' && type !== 'complete' && type !== 'error') {
        failScan('The scan stream returned an unknown event.');
        return;
      }
      if (type === 'progress') {
        const progress = normalizeScanEventProgress(eventData);
        if (!progress) {
          failScan('The scan stream returned invalid progress data.');
          return;
        }
        setScanProgress({
          ...progress,
        });
      } else if (type === 'complete') {
        const progress = normalizeScanEventProgress(eventData);
        if (!progress) {
          failScan('The scan stream returned invalid completion data.');
          return;
        }
        if (!settleScanTransport()) return;
        setScanProgress({
          ...progress,
        });
        setTotalIndexed(progress.processed);
        setIndexToken(typeof eventData.indexToken === 'string' && eventData.indexToken.trim()
          ? eventData.indexToken
          : null);
        if (replacesActiveCatalog) clearCatalogOwnedUiState();
        activeCatalogIdentityRef.current = nextCatalogIdentity;
        setSearchError(null);
        setSearchErrorKind(null);
        options.onComplete?.(scanDir);
        setPhase('viewer');
      } else if (type === 'error') {
        failScan(typeof eventData.message === 'string' && eventData.message.trim()
          ? eventData.message
          : 'The scan could not be completed.');
      }
    };
    es.onerror = () => {
      failScan('Connection lost before the scan completed.');
    };
  }, [clearCatalogOwnedUiState, dirPath, phase, scheduleViewSettingsPersist]);

  const dismissScanError = useCallback(() => {
    setScanError(null);
  }, []);

  const reportImageSessionExpired = useCallback(() => {
    if (imageSessionExpiredReportedRef.current) return;
    imageSessionExpiredReportedRef.current = true;
    setSearchError('This viewer session expired. Refreshing the current folder set automatically.');
    setSearchErrorKind('session-expired');
    if (dirPath.trim()) startScan({ dir: dirPath, preserveViewer: true });
  }, [dirPath, startScan]);

  const rescanExpiredSearchSession = useCallback(() => {
    if (searchErrorKind !== 'session-expired' || !dirPath.trim()) return;
    setSearchError(null);
    setSearchErrorKind(null);
    startScan({ dir: dirPath, preserveViewer: true });
  }, [dirPath, searchErrorKind, startScan]);

  // ── Delete ──
  const deleteImage = useCallback(async (
    id: string,
    options: DeleteImageOptions = {},
  ): Promise<boolean> => {
    // UI dialogs are the first safety layer. Keep the same invariant here so a
    // future caller cannot bypass favorite protection by invoking the action
    // directly or by racing a favorite change after a bulk snapshot.
    if ((favoritesRef.current[id] ?? 0) > 0 && options.favoriteConfirmed !== true) {
      console.warn('Favorite source delete blocked until explicitly confirmed.', id);
      return false;
    }
    try {
      const tokenParam = indexToken ? `&indexToken=${encodeURIComponent(indexToken)}` : '';
      const res = await fetch(`/api/delete?path=${encodeURIComponent(id)}${tokenParam}`, { method: 'DELETE' });
      if (res.ok) {
        searchGenerationRef.current += 1;
        const generation = searchGenerationRef.current;
        committedSearchGenerationRef.current = generation;
        abortPendingSearchRequests();
        loadedPagesRef.current = new Set();
        pendingPagesRef.current = new Set();
        setIsSearching(false);
        const nextResults = removeImageSlot(searchResultsRef.current, id);
        const nextTotal = Math.max(0, searchTotalRef.current - 1);
        searchResultsRef.current = nextResults;
        searchTotalRef.current = nextTotal;
        setSearchResults(nextResults);
        setSearchTotal(nextTotal);
        setTotalIndexed(prev => Math.max(0, prev - 1));

        // A successful source recycle invalidates every browser-owned UI
        // reference to the path. Shared Favorite/Seen history and enhancement
        // jobs are intentionally independent records and are not deleted here.
        const remainingTabIds = previewTabIdsRef.current.filter(tabId => tabId !== id);
        const remainingPinnedIds = pinnedPreviewIdsRef.current.filter(pinnedId => pinnedId !== id);
        const currentActiveId = activePreviewIdRef.current;
        const nextActiveId = currentActiveId === id
          ? remainingTabIds[remainingTabIds.length - 1] ?? null
          : currentActiveId;
        const pendingRestore = pendingPreviewTabsRestoreRef.current;
        const remainingPendingTabIds = pendingRestore?.tabIds.filter(tabId => tabId !== id) ?? [];
        const nextPendingRestore = pendingRestore && remainingPendingTabIds.length > 0
          ? serializePersistedPreviewTabs(
            remainingPendingTabIds,
            pendingRestore.activeId === id ? null : pendingRestore.activeId,
          )
          : null;

        previewTabIdsRef.current = remainingTabIds;
        pinnedPreviewIdsRef.current = remainingPinnedIds;
        activePreviewIdRef.current = nextActiveId;
        pendingPreviewTabsRestoreRef.current = nextPendingRestore;
        if (!nextPendingRestore) setPreviewTabsPersistenceReady(true);
        if (!pendingRestore) previewTabsUserModifiedRef.current = true;

        const persistedTabIds = [
          ...remainingPendingTabIds,
          ...remainingTabIds.filter(tabId => !remainingPendingTabIds.includes(tabId)),
        ];
        const persistedActiveId = nextPendingRestore?.activeId
          ?? (nextActiveId && persistedTabIds.includes(nextActiveId) ? nextActiveId : null);
        writeJsonLocalStorage(
          PREVIEW_TAB_STORAGE_KEY,
          serializePersistedPreviewTabs(persistedTabIds, persistedActiveId),
        );
        writeJsonLocalStorage('pvu_pinned_tabs', remainingPinnedIds);

        setModalImageIdsState(prev => prev.filter(imageId => imageId !== id));
        setSelectedIds(prev => prev.filter(selectedId => selectedId !== id));
        setSelectionAnchorId(anchorId => anchorId === id ? null : anchorId);
        setPreviewTabIds(prev => prev.filter(tabId => tabId !== id));
        setPinnedPreviewIds(prev => prev.filter(pinnedId => pinnedId !== id));
        setActivePreviewIdState(activeId => activeId === id ? nextActiveId : activeId);
        setPreviewById(prev => {
          if (!(id in prev)) return prev;
          const next = { ...prev };
          delete next[id];
          return next;
        });
        setClosedPreviewStack(prev => prev.filter(closedId => closedId !== id));
        setClosedPreviewById(prev => {
          if (!(id in prev)) return prev;
          const next = { ...prev };
          delete next[id];
          return next;
        });
        setRevealImageId(revealId => revealId === id ? null : revealId);
        if (pendingFavoriteFilterMutationRef.current?.currentId === id) {
          pendingFavoriteFilterMutationRef.current = null;
        }
        const meta = searchMetaRef.current;
        void doSearchPage(
          meta.query,
          0,
          meta.sortBy,
          meta.randomSeed,
          meta.dateFrom,
          meta.dateTo,
          meta.hiddenFolders,
          meta.dirPath,
          meta.indexToken,
          generation
        );
        return true;
      }
      const data = await res.json().catch(() => ({}));
      console.error('Delete failed', data?.error || res.statusText);
      return false;
    } catch (error) {
      console.error('Delete failed', error);
      return false;
    }
  }, [abortPendingSearchRequests, doSearchPage, indexToken]);

  // ── Open in external viewer ──
  const openExternal = useCallback((id: string, options: OpenExternalOptions = {}) => {
    return requestOpenExternal(id, {
      ...options,
      indexToken: options.indexToken === undefined ? indexToken : options.indexToken,
    });
  }, [indexToken]);

  const setView = useCallback((partial: Partial<ViewSettings>) => {
    setViewState(prev => {
      const next = { ...prev, ...partial };
      scheduleViewSettingsPersist(next);
      return next;
    });
  }, [scheduleViewSettingsPersist]);

  const setPerfEnabled = useCallback((v: boolean) => {
    setPerfEnabledState(v);
  }, []);

  const setSearchScrollPosition = useCallback((key: string, value: number) => {
    const next = { ...scrollMemoryRef.current, [key]: value };
    const keys = Object.keys(next);
    if (keys.length > 80) {
      // lightweight cap: keep latest 80 keys
      for (let i = 0; i < keys.length - 80; i += 1) {
        delete next[keys[i]];
      }
    }
    scrollMemoryRef.current = next;
    if (scrollMemoryFlushRef.current) {
      clearTimeout(scrollMemoryFlushRef.current);
    }
    scrollMemoryFlushRef.current = setTimeout(() => {
      scrollMemoryFlushRef.current = null;
      writeJsonLocalStorage('pvu_scroll_memory', scrollMemoryRef.current);
    }, SCROLL_MEMORY_FLUSH_DELAY_MS);
  }, []);

  const getSearchScrollPosition = useCallback((key: string) => {
    const value = scrollMemoryRef.current[key];
    return typeof value === 'number' ? value : null;
  }, []);

  const setActivePreviewId = useCallback((id: string | null) => {
    previewTabsUserModifiedRef.current = true;
    setActivePreviewIdState(id);
  }, []);

  const markImageSeen = useCallback((id: string) => {
    if (seenImageIdsRef.current[id]) return;
    seenImageIdsRef.current = { ...seenImageIdsRef.current, [id]: true };
    seenSharedDirtyRef.current = true;
    seenLastLifecycleBodyRef.current = null;
    writeJsonLocalStorage('pvu_seen_images', seenImageIdsRef.current);
    if (seenImagesFlushRef.current) {
      clearTimeout(seenImagesFlushRef.current);
    }
    seenImagesFlushRef.current = setTimeout(() => {
      seenImagesFlushRef.current = null;
      const snapshot = seenImageIdsRef.current;
      writeJsonLocalStorage('pvu_seen_images', snapshot);
      syncSeenToShared(snapshot);
    }, SEEN_IMAGES_FLUSH_DELAY_MS);
    setSeenImageIds((prev) => {
      if (prev[id]) return prev;
      return { ...prev, [id]: true };
    });
  }, [syncSeenToShared]);

  const requestRevealImage = useCallback((id: string) => {
    setRevealImageId(id);
  }, []);

  const consumeRevealImage = useCallback(() => {
    setRevealImageId(null);
  }, []);

  const setModalImageIds = useCallback((ids: string[]) => {
    setModalImageIdsState(Array.from(new Set(ids.filter(Boolean))));
  }, []);

  const openModalAtImage = useCallback((
    id: string,
    fallbackIndex: number | null,
    orderedIds?: string[]
  ) => {
    markImageSeen(id);
    setModalImageIdsState(
      orderedIds && orderedIds.length > 0
        ? Array.from(new Set(orderedIds.filter(Boolean)))
        : []
    );
    const resolvedIndex = fallbackIndex !== null && fallbackIndex >= 0
      ? fallbackIndex
      : searchResults.findIndex((image) => image?.id === id);
    setSelectedIndex(resolvedIndex >= 0 ? resolvedIndex : null);
  }, [markImageSeen, searchResults]);

  const showPreviewImage = useCallback((
    image: ImageFile,
    options?: { makeActive?: boolean }
  ) => {
    const { makeActive = true } = options || {};
    setPreviewById((prev) => ({ ...prev, [image.id]: image }));
    if (makeActive) {
      setActivePreviewIdState(image.id);
    }
  }, []);

  const openPreviewTab = useCallback((
    image: ImageFile,
    options?: { makeActive?: boolean; pin?: boolean }
  ) => {
    const { makeActive = true, pin = false } = options || {};
    previewTabsUserModifiedRef.current = true;
    setPreviewById((prev) => ({ ...prev, [image.id]: image }));
    setPreviewTabIds((prev) => (prev.includes(image.id) ? prev : [...prev, image.id]));
    if (makeActive) {
      setActivePreviewIdState(image.id);
    }
    if (pin) {
      setPinnedPreviewIds((prev) => (prev.includes(image.id) ? prev : [...prev, image.id]));
    }
  }, []);

  const togglePinPreviewTab = useCallback((id: string) => {
    setPinnedPreviewIds((prev) =>
      prev.includes(id) ? prev.filter((item) => item !== id) : [...prev, id]
    );
  }, []);

  const reorderPreviewTab = useCallback((id: string, destinationIndex: number) => {
    setPreviewTabIds((previous) => {
      const next = reorderPreviewTabIds(previous, id, destinationIndex);
      if (next === previous) return previous;
      previewTabsUserModifiedRef.current = true;
      return next;
    });
  }, []);

  const closePreviewTab = useCallback((id: string) => {
    previewTabsUserModifiedRef.current = true;
    const existing = previewById[id];
    if (existing) {
      setClosedPreviewById((prev) => ({ ...prev, [id]: existing }));
      setClosedPreviewStack((prev) => [id, ...prev.filter((item) => item !== id)].slice(0, MAX_CLOSED_PREVIEW_TABS));
    }
    setPreviewTabIds((prev) => {
      const remaining = prev.filter((tabId) => tabId !== id);
      setActivePreviewIdState((active) => {
        if (active !== id) return active;
        return remaining.length > 0 ? remaining[remaining.length - 1] : null;
      });
      return remaining;
    });
    setSelectedIds((prev) => prev.filter((selectedId) => selectedId !== id));
    setPreviewById((prev) => {
      const next = { ...prev };
      delete next[id];
      return next;
    });
  }, [previewById]);

  const restoreLastClosedPreview = useCallback(() => {
    setClosedPreviewStack((prev) => {
      if (prev.length === 0) return prev;
      const [id, ...rest] = prev;
      const fromSearch = searchResults.find((entry): entry is ImageFile => Boolean(entry?.id === id));
      const fromCache = closedPreviewById[id];
      const image = fromSearch ?? fromCache;
      if (image) {
        previewTabsUserModifiedRef.current = true;
        setPreviewById((current) => ({ ...current, [id]: image }));
        setPreviewTabIds((current) => (current.includes(id) ? current : [id, ...current]));
        setActivePreviewIdState(id);
      }
      return rest;
    });
  }, [closedPreviewById, searchResults]);

  const clearSelection = useCallback(() => {
    setSelectedIds([]);
    setSelectionAnchorId(null);
  }, []);

  const closeAllPreviews = useCallback(() => {
    previewTabsUserModifiedRef.current = true;
    setActivePreviewIdState(null);
    setSelectedIds([]);
    setSelectionAnchorId(null);
    setPreviewById((prev) => {
      if (previewTabIds.length === 0) return {};
      const next: Record<string, ImageFile> = {};
      for (const id of previewTabIds) {
        if (prev[id]) next[id] = prev[id];
      }
      return next;
    });
  }, [previewTabIds]);

  const selectImage = useCallback((
    image: ImageFile,
    orderedIds: string[],
    options?: { range?: boolean; toggle?: boolean }
  ) => {
    const { range = false, toggle = false } = options || {};
    const targetId = image.id;

    showPreviewImage(image, { makeActive: true });

    if (range) {
      const anchor = selectionAnchorId ?? targetId;
      const anchorIndex = orderedIds.indexOf(anchor);
      const targetIndex = orderedIds.indexOf(targetId);
      if (anchorIndex >= 0 && targetIndex >= 0) {
        const [start, end] = anchorIndex < targetIndex
          ? [anchorIndex, targetIndex]
          : [targetIndex, anchorIndex];
        const rangeIds = orderedIds.slice(start, end + 1);
        if (toggle) {
          setSelectedIds((prev) => Array.from(new Set([...prev, ...rangeIds])));
        } else {
          setSelectedIds(rangeIds);
        }
      } else {
        setSelectedIds([targetId]);
      }
      setSelectionAnchorId(anchor);
      return;
    }

    if (toggle) {
      setSelectedIds((prev) =>
        prev.includes(targetId)
          ? prev.filter((id) => id !== targetId)
          : [...prev, targetId]
      );
      setSelectionAnchorId(targetId);
      return;
    }

    setSelectedIds([targetId]);
    setSelectionAnchorId(targetId);
  }, [selectionAnchorId, showPreviewImage]);

  return (
    <ImageContext.Provider value={{
      phase, setPhase, dirPath, setDirPath, indexToken, scanProgress, scanError, dismissScanError,
      searchQuery, setSearchQuery, searchResults, searchTotal,
      isSearching, searchError, searchErrorKind, ensureSearchRange, retrySearch, reportImageSessionExpired, rescanExpiredSearchSession, dismissSearchError,
      resolveModalNavigationTarget,
      favorites, toggleFavorite, showFavOnly: showFavOnlyState, setShowFavOnly,
      showUnfavOnly: showUnfavOnlyState, setShowUnfavOnly,
      cycleFavoriteLevel, decreaseFavoriteLevel, clearFavorite, setFavoriteLevels, adjustFavoriteLevels,
      favoriteFilterLevels, toggleFavoriteFilterLevel, clearFavoriteFilterLevels,
      showEnhancedOnly: showEnhancedOnlyState, setShowEnhancedOnly, enhancedSourceIds,
      selectedIndex, setSelectedIndex,
      modalImageIds, setModalImageIds, openModalAtImage,
      selectedIds, selectImage, clearSelection,
      previewTabIds, activePreviewId, previewById, showPreviewImage, openPreviewTab, setActivePreviewId, closePreviewTab, reorderPreviewTab, closeAllPreviews,
      seenImageIds, markImageSeen, revealImageId, requestRevealImage, consumeRevealImage,
      pinnedPreviewIds, togglePinPreviewTab, closedPreviewTabCount: closedPreviewStack.length, restoreLastClosedPreview,
      keyBindings, setKeyBindings, confirmBeforeDelete, setConfirmBeforeDelete,
      thumbnailStatusBorders, setThumbnailStatusBorders, showSettings, setShowSettings,
      view, setView,
      setSearchScrollPosition, getSearchScrollPosition,
      perfEnabled, setPerfEnabled, perfStats,
      startScan, cancelScan, deleteImage, openExternal,
      totalIndexed, setTotalIndexed,
    }}>
      {children}
    </ImageContext.Provider>
  );
}

export function useImageStore() {
  const ctx = useContext(ImageContext);
  if (!ctx) throw new Error('useImageStore must be inside ImageProvider');
  return ctx;
}

import { matchesFavoriteLevel, type FavoriteFilterLevel } from './browserUiPreferences';

export type FolderSortBy = 'name-asc' | 'name-desc' | 'count-desc' | 'count-asc';

export interface FolderBucket {
  key: string;
  label: string;
  count: number;
}

export interface ResultCountLabelArgs {
  searchQuery: string;
  searchTotal: number;
  totalIndexed: number;
  dateFrom?: string;
  dateTo?: string;
  hiddenFolders?: string[];
  loadedCount?: number;
  shownCount?: number;
}

export interface LoadedResultCountArgs {
  searchResults: Array<{ id: string } | null | undefined>;
  favorites: Record<string, number>;
  showFavOnly: boolean;
  showUnfavOnly: boolean;
  favoriteFilterLevels: FavoriteFilterLevel[];
  showEnhancedOnly: boolean;
  enhancedSourceIds: Record<string, true>;
}

export interface ClientFilterMutationNavigation {
  shouldSync: boolean;
  orderedIds: string[];
  nextId: string | null;
}

export interface GridMetricsSnapshot {
  scrollTop: number;
  viewportHeight: number;
  rowHeight: number;
  gridColumns: number;
  fullCount: number;
  totalHeight: number;
  anchorIndex?: number;
  anchorTop?: number;
  anchorViewportOffset?: number;
}

export function withGridPointerAnchor(
  metrics: GridMetricsSnapshot | null,
  anchorIndex: number,
  anchorTop: number,
  scrollTop: number
): GridMetricsSnapshot | null {
  if (
    !metrics ||
    !Number.isInteger(anchorIndex) ||
    anchorIndex < 0 ||
    anchorIndex >= metrics.fullCount ||
    !Number.isFinite(anchorTop) ||
    !Number.isFinite(scrollTop)
  ) {
    return metrics;
  }

  return {
    ...metrics,
    scrollTop,
    anchorIndex,
    anchorTop,
    anchorViewportOffset: anchorTop - scrollTop,
  };
}

export interface ArrowSelectionArgs {
  key: string;
  viewMode: 'grid' | 'list';
  gridColumns: number;
  currentIndex: number;
  itemCount: number;
}

const INTERACTIVE_SHORTCUT_TARGET_SELECTOR = [
  'input',
  'textarea',
  'select',
  'button',
  '[contenteditable]:not([contenteditable="false"])',
].join(', ');

const VIEWER_SHORTCUT_CONTEXT_SELECTOR = [
  '[role="dialog"]',
  '[role="alertdialog"]',
].join(', ');

const VIEWER_SHORTCUT_OVERLAY_SELECTOR = [
  '.modal-overlay',
  '.settings-overlay',
  '.confirm-overlay',
].join(', ');

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

export function shouldIgnoreViewerShortcut(
  target: EventTarget | null,
  root: Pick<Document, 'querySelector'> | null = typeof document === 'undefined' ? null : document
): boolean {
  const element = target && typeof (target as Element).closest === 'function'
    ? target as Element
    : null;

  if (isInteractiveShortcutTarget(target)) return true;
  if (element?.closest(VIEWER_SHORTCUT_CONTEXT_SELECTOR)) return true;
  return Boolean(root?.querySelector(VIEWER_SHORTCUT_OVERLAY_SELECTOR));
}

export function isInteractiveShortcutTarget(target: EventTarget | null): boolean {
  const element = target && typeof (target as Element).closest === 'function'
    ? target as Element
    : null;
  return Boolean(element?.closest(INTERACTIVE_SHORTCUT_TARGET_SELECTOR));
}

export function getEmptyResultMessage(searchQuery: string, hasClientFilters: boolean): string {
  const query = searchQuery.trim();
  if (query) return `No images found for query: ${query}`;
  if (hasClientFilters) return 'No images match the active filters.';
  return 'No supported images were found in the selected folders.';
}

/**
 * Server search is sparse/paged. Count only materialized entries, then apply
 * the same client-side predicates used by the gallery before calling it shown.
 */
export function getLoadedResultCounts({
  searchResults,
  favorites,
  showFavOnly,
  showUnfavOnly,
  favoriteFilterLevels,
  showEnhancedOnly,
  enhancedSourceIds,
}: LoadedResultCountArgs) {
  let loadedCount = 0;
  let shownCount = 0;
  const hasClientFilters = showFavOnly || showUnfavOnly || showEnhancedOnly;

  for (const image of searchResults) {
    if (!image) continue;
    loadedCount += 1;
    const level = favorites[image.id] ?? 0;
    const matchesFavorite = showFavOnly
      ? matchesFavoriteLevel(level, favoriteFilterLevels)
      : showUnfavOnly
        ? level === 0
        : true;
    const matchesEnhanced = showEnhancedOnly ? Boolean(enhancedSourceIds[image.id]) : true;
    if (matchesFavorite && matchesEnhanced) shownCount += 1;
  }

  return {
    loadedCount,
    shownCount: hasClientFilters ? shownCount : loadedCount,
    hasClientFilters,
  };
}

export function getClientFilteredLoadedIds({
  searchResults,
  favorites,
  showFavOnly,
  showUnfavOnly,
  favoriteFilterLevels,
  showEnhancedOnly,
  enhancedSourceIds,
}: LoadedResultCountArgs): string[] {
  const orderedIds: string[] = [];
  for (const image of searchResults) {
    if (!image) continue;
    const level = favorites[image.id] ?? 0;
    const matchesFavorite = showFavOnly
      ? matchesFavoriteLevel(level, favoriteFilterLevels)
      : showUnfavOnly
        ? level === 0
        : true;
    const matchesEnhanced = showEnhancedOnly ? Boolean(enhancedSourceIds[image.id]) : true;
    if (matchesFavorite && matchesEnhanced) orderedIds.push(image.id);
  }
  return orderedIds;
}

/**
 * Resolve the image that should take over when a local mutation removes the
 * current image from an exact client-side filter. The pre-mutation order is
 * authoritative: prefer the next surviving image, then the previous one.
 * `shouldSync` stays false for matching changes and unrelated/currently hidden
 * images so hydration or filter changes cannot accidentally move the viewer.
 */
export function nextAfterClientFilterMutation(
  currentId: string | null,
  previousOrderedIds: readonly string[],
  nextOrderedIds: readonly string[]
): ClientFilterMutationNavigation {
  const previousIds = Array.from(new Set(previousOrderedIds.filter(Boolean)));
  const orderedIds = Array.from(new Set(nextOrderedIds.filter(Boolean)));
  const previousIndex = currentId ? previousIds.indexOf(currentId) : -1;
  if (!currentId || previousIndex < 0 || orderedIds.includes(currentId)) {
    return { shouldSync: false, orderedIds, nextId: currentId };
  }

  const nextIdSet = new Set(orderedIds);
  for (let index = previousIndex + 1; index < previousIds.length; index += 1) {
    if (nextIdSet.has(previousIds[index])) {
      return { shouldSync: true, orderedIds, nextId: previousIds[index] };
    }
  }
  for (let index = previousIndex - 1; index >= 0; index -= 1) {
    if (nextIdSet.has(previousIds[index])) {
      return { shouldSync: true, orderedIds, nextId: previousIds[index] };
    }
  }
  return { shouldSync: true, orderedIds, nextId: null };
}

export function sortFolderBuckets(
  buckets: FolderBucket[],
  sortBy: FolderSortBy
): FolderBucket[] {
  return [...buckets].sort((left, right) => {
    if (sortBy === 'name-asc') {
      return left.label.localeCompare(right.label, undefined, { sensitivity: 'base' });
    }
    if (sortBy === 'name-desc') {
      return right.label.localeCompare(left.label, undefined, { sensitivity: 'base' });
    }
    if (sortBy === 'count-asc') {
      if (left.count !== right.count) return left.count - right.count;
      return left.label.localeCompare(right.label, undefined, { sensitivity: 'base' });
    }
    if (left.count !== right.count) return right.count - left.count;
    return left.label.localeCompare(right.label, undefined, { sensitivity: 'base' });
  });
}

export function getResultCountLabel({
  searchQuery,
  searchTotal,
  totalIndexed,
  dateFrom,
  dateTo,
  hiddenFolders = [],
  loadedCount,
  shownCount,
}: ResultCountLabelArgs): string {
  const hasServerFilters = Boolean(
    searchQuery.trim() ||
    dateFrom ||
    dateTo ||
    hiddenFolders.length > 0
  );

  const fallbackShownCount = hasServerFilters ? searchTotal : totalIndexed;
  const shown = Math.max(0, Math.trunc(shownCount ?? loadedCount ?? fallbackShownCount));
  const shownLabel = `${shown.toLocaleString()} shown`;

  return hasServerFilters
    ? `${shownLabel} · ${searchTotal.toLocaleString()} filtered · ${totalIndexed.toLocaleString()} indexed`
    : `${shownLabel} · ${totalIndexed.toLocaleString()} indexed`;
}

export function getZoomCenteredScrollTop(
  previous: GridMetricsSnapshot,
  next: GridMetricsSnapshot
): number {
  const previousScrollTop = Number.isFinite(previous.scrollTop)
    ? Math.max(0, previous.scrollTop)
    : 0;
  const hasFiniteNextBounds = Number.isFinite(next.viewportHeight) &&
    Number.isFinite(next.totalHeight) &&
    next.viewportHeight >= 0 &&
    next.totalHeight >= 0;
  const maxScrollTop = hasFiniteNextBounds
    ? Math.max(0, next.totalHeight - next.viewportHeight)
    : previousScrollTop;
  if (
    !Number.isFinite(previous.viewportHeight) ||
    !Number.isFinite(previous.rowHeight) ||
    !Number.isFinite(previous.gridColumns) ||
    !Number.isFinite(previous.fullCount) ||
    !Number.isFinite(next.viewportHeight) ||
    !Number.isFinite(next.rowHeight) ||
    !Number.isFinite(next.gridColumns) ||
    !Number.isFinite(next.fullCount) ||
    !Number.isFinite(next.totalHeight) ||
    previous.fullCount <= 0 ||
    next.fullCount <= 0 ||
    previous.gridColumns <= 0 ||
    next.gridColumns <= 0 ||
    previous.rowHeight <= 0 ||
    next.rowHeight <= 0 ||
    previous.viewportHeight < 0 ||
    next.viewportHeight < 0 ||
    next.totalHeight < 0
  ) {
    return clamp(previousScrollTop, 0, maxScrollTop);
  }

  if (
    previous.anchorIndex !== undefined &&
    previous.anchorIndex === next.anchorIndex &&
    previous.anchorViewportOffset !== undefined &&
    next.anchorTop !== undefined &&
    Number.isFinite(previous.anchorViewportOffset) &&
    Number.isFinite(next.anchorTop)
  ) {
    return clamp(next.anchorTop - previous.anchorViewportOffset, 0, maxScrollTop);
  }

  const previousCenterY = previousScrollTop + previous.viewportHeight / 2;
  const previousRow = clamp(
    Math.floor(previousCenterY / previous.rowHeight),
    0,
    Math.max(0, Math.ceil(previous.fullCount / previous.gridColumns) - 1)
  );
  const rowOffsetRatio = clamp(
    (previousCenterY - previousRow * previous.rowHeight) / previous.rowHeight,
    0,
    1
  );
  const anchorColumn = clamp(
    Math.floor(previous.gridColumns / 2),
    0,
    Math.max(0, previous.gridColumns - 1)
  );
  const anchorIndex = clamp(
    previousRow * previous.gridColumns + anchorColumn,
    0,
    Math.max(0, previous.fullCount - 1)
  );
  const nextRow = Math.floor(anchorIndex / next.gridColumns);
  const nextCenterY = nextRow * next.rowHeight + next.rowHeight * rowOffsetRatio;
  return clamp(nextCenterY - next.viewportHeight / 2, 0, maxScrollTop);
}

export function getArrowSelectionIndex({
  key,
  viewMode,
  gridColumns,
  currentIndex,
  itemCount,
}: ArrowSelectionArgs): number | null {
  if (itemCount <= 0) return null;

  if (currentIndex < 0 || currentIndex >= itemCount) {
    return key === 'ArrowLeft' || key === 'ArrowUp' ? itemCount - 1 : 0;
  }

  const rowDelta = viewMode === 'grid' ? Math.max(1, gridColumns) : 1;
  const delta =
    key === 'ArrowLeft' ? -1 :
    key === 'ArrowRight' ? 1 :
    key === 'ArrowUp' ? -rowDelta :
    key === 'ArrowDown' ? rowDelta :
    0;

  if (delta === 0) return null;
  return clamp(currentIndex + delta, 0, itemCount - 1);
}

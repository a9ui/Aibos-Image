export type ModalSwipeNavigation = 'prev' | 'next' | 'close' | null;
export type ModalClickTarget = 'image' | 'empty';
export type ModalClickAction = 'toggleChrome' | 'prev' | 'next' | 'close';
export type SparseModalNavigationIntent = 'prev' | 'next' | 'delete';

export interface SparseModalNavigationRange {
  start: number;
  end: number;
  step: 1 | -1;
}

export interface ModalOrderReconcileResult {
  orderedIds: string[];
  selectedId: string | null;
}

export const DEFAULT_MODAL_EDGE_RATIO = 0.28;
export const MIN_MODAL_EDGE_RATIO = 0.1;
export const MAX_MODAL_EDGE_RATIO = 0.4;

export function clampModalEdgeRatio(edgeRatio: number) {
  if (!Number.isFinite(edgeRatio)) return DEFAULT_MODAL_EDGE_RATIO;
  return Math.min(MAX_MODAL_EDGE_RATIO, Math.max(MIN_MODAL_EDGE_RATIO, edgeRatio));
}

export function getModalClickAction(
  target: ModalClickTarget,
  relativeX: number,
  width: number,
  edgeRatio = DEFAULT_MODAL_EDGE_RATIO
): ModalClickAction {
  const safeWidth = Math.max(1, width);
  const safeEdgeRatio = clampModalEdgeRatio(edgeRatio);
  const leftBoundary = safeWidth * safeEdgeRatio;
  const rightBoundary = safeWidth * (1 - safeEdgeRatio);

  if (relativeX <= leftBoundary) return 'prev';
  if (relativeX >= rightBoundary) return 'next';
  return target === 'image' ? 'toggleChrome' : 'close';
}

export function getSwipeNavigation(
  deltaX: number,
  deltaY: number,
  elapsedMs: number,
  width: number,
  height: number,
): ModalSwipeNavigation {
  const horizontalThreshold = Math.min(180, Math.max(72, width * 0.16));
  const verticalThreshold = Math.min(180, Math.max(72, height * 0.16));
  const horizontalVelocity = elapsedMs > 0 ? deltaX / elapsedMs : 0;
  const verticalVelocity = elapsedMs > 0 ? deltaY / elapsedMs : 0;
  const velocityThreshold = 0.55;

  if (deltaY > 0 && Math.abs(deltaY) > Math.abs(deltaX)) {
    if (deltaY >= verticalThreshold || verticalVelocity >= velocityThreshold) return 'close';
    return null;
  }
  if (Math.abs(deltaX) <= Math.abs(deltaY)) return null;
  if (deltaX <= -horizontalThreshold || horizontalVelocity <= -velocityThreshold) return 'next';
  if (deltaX >= horizontalThreshold || horizontalVelocity >= velocityThreshold) return 'prev';
  return null;
}

/**
 * Describe the absolute search-index order used to resolve modal navigation.
 * Search results are sparse and paged, so callers can load each encountered
 * page before deciding whether an entry matches a client-side filter.
 *
 * Normal navigation wraps once without revisiting the current image. Delete
 * navigation does not wrap: the item shifted into the deleted slot is the
 * first candidate, followed by later items, then earlier items in reverse.
 */
export function getSparseModalNavigationRanges(
  currentIndex: number,
  total: number,
  intent: SparseModalNavigationIntent
): SparseModalNavigationRange[] {
  const safeTotal = Math.max(0, Math.trunc(total));
  if (safeTotal === 0) return [];

  if (intent === 'delete') {
    const deletedSlot = Math.min(safeTotal, Math.max(0, Math.trunc(currentIndex)));
    const ranges: SparseModalNavigationRange[] = [];
    if (deletedSlot < safeTotal) {
      ranges.push({ start: deletedSlot, end: safeTotal - 1, step: 1 });
    }
    const previousStart = Math.min(safeTotal - 1, deletedSlot - 1);
    if (previousStart >= 0) {
      ranges.push({ start: previousStart, end: 0, step: -1 });
    }
    return ranges;
  }

  const current = Math.min(safeTotal - 1, Math.max(0, Math.trunc(currentIndex)));
  if (intent === 'next') {
    const ranges: SparseModalNavigationRange[] = [];
    if (current + 1 < safeTotal) {
      ranges.push({ start: current + 1, end: safeTotal - 1, step: 1 });
    }
    if (current > 0) {
      ranges.push({ start: 0, end: current - 1, step: 1 });
    }
    return ranges;
  }

  const ranges: SparseModalNavigationRange[] = [];
  if (current > 0) {
    ranges.push({ start: current - 1, end: 0, step: -1 });
  }
  if (current + 1 < safeTotal) {
    ranges.push({ start: safeTotal - 1, end: current + 1, step: -1 });
  }
  return ranges;
}

function uniqueImageIds(ids: string[]) {
  return Array.from(new Set(ids.filter(Boolean)));
}

export function reconcileModalOrderAfterFilterChange(
  currentId: string | null,
  previousOrderedIds: string[],
  nextOrderedIds: string[]
): ModalOrderReconcileResult {
  const orderedIds = uniqueImageIds(nextOrderedIds);
  if (orderedIds.length === 0) {
    return { orderedIds, selectedId: null };
  }

  const orderedIdSet = new Set(orderedIds);
  if (currentId && orderedIdSet.has(currentId)) {
    return { orderedIds, selectedId: currentId };
  }

  const previousIds = uniqueImageIds(previousOrderedIds);
  const previousIndex = currentId ? previousIds.indexOf(currentId) : -1;
  if (previousIndex < 0) {
    return { orderedIds, selectedId: orderedIds[0] ?? null };
  }

  for (let i = previousIndex + 1; i < previousIds.length; i++) {
    if (orderedIdSet.has(previousIds[i])) {
      return { orderedIds, selectedId: previousIds[i] };
    }
  }

  for (let i = previousIndex - 1; i >= 0; i--) {
    if (orderedIdSet.has(previousIds[i])) {
      return { orderedIds, selectedId: previousIds[i] };
    }
  }

  return { orderedIds, selectedId: orderedIds[0] ?? null };
}

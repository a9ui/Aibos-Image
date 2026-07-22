import { describe, expect, it } from 'vitest';
import {
  clampModalEdgeRatio,
  getModalClickAction,
  getSparseModalNavigationRanges,
  getSwipeNavigation,
  reconcileModalOrderAfterFilterChange,
} from './modalNavigation';

describe('getSparseModalNavigationRanges', () => {
  it('walks next through the remaining full order before wrapping', () => {
    expect(getSparseModalNavigationRanges(99, 250, 'next')).toEqual([
      { start: 100, end: 249, step: 1 },
      { start: 0, end: 98, step: 1 },
    ]);
  });

  it('walks previous through the remaining full order before wrapping', () => {
    expect(getSparseModalNavigationRanges(100, 250, 'prev')).toEqual([
      { start: 99, end: 0, step: -1 },
      { start: 249, end: 101, step: -1 },
    ]);
  });

  it('prefers the image shifted into a deleted slot, then earlier images', () => {
    expect(getSparseModalNavigationRanges(99, 199, 'delete')).toEqual([
      { start: 99, end: 198, step: 1 },
      { start: 98, end: 0, step: -1 },
    ]);
    expect(getSparseModalNavigationRanges(200, 200, 'delete')).toEqual([
      { start: 199, end: 0, step: -1 },
    ]);
  });

  it('returns no ranges for an empty or one-item non-delete order', () => {
    expect(getSparseModalNavigationRanges(0, 0, 'next')).toEqual([]);
    expect(getSparseModalNavigationRanges(0, 1, 'next')).toEqual([]);
    expect(getSparseModalNavigationRanges(0, 1, 'prev')).toEqual([]);
  });
});

describe('getSwipeNavigation', () => {
  it('moves to the next image when dragging left far enough', () => {
    expect(getSwipeNavigation(-130, 0, 500, 800, 600)).toBe('next');
  });

  it('moves to the previous image when dragging right far enough', () => {
    expect(getSwipeNavigation(130, 0, 500, 800, 600)).toBe('prev');
  });

  it('closes the preview when dragging down far enough', () => {
    expect(getSwipeNavigation(12, 130, 500, 800, 600)).toBe('close');
    expect(getSwipeNavigation(12, -130, 500, 800, 600)).toBeNull();
  });

  it('accepts a fast flick even when distance is short', () => {
    expect(getSwipeNavigation(-60, 0, 80, 800, 600)).toBe('next');
    expect(getSwipeNavigation(60, 0, 80, 800, 600)).toBe('prev');
    expect(getSwipeNavigation(0, 60, 80, 800, 600)).toBe('close');
  });

  it('ignores small slow drags', () => {
    expect(getSwipeNavigation(40, 0, 300, 800, 600)).toBeNull();
    expect(getSwipeNavigation(0, 40, 300, 800, 600)).toBeNull();
  });
});

describe('getModalClickAction', () => {
  it('navigates previous from the left 28 percent', () => {
    expect(getModalClickAction('image', 100, 1000)).toBe('prev');
    expect(getModalClickAction('empty', 279, 1000)).toBe('prev');
  });

  it('navigates next from the right 28 percent', () => {
    expect(getModalClickAction('image', 720, 1000)).toBe('next');
    expect(getModalClickAction('empty', 900, 1000)).toBe('next');
  });

  it('toggles chrome for the center image and closes from center empty space', () => {
    expect(getModalClickAction('image', 500, 1000)).toBe('toggleChrome');
    expect(getModalClickAction('empty', 500, 1000)).toBe('close');
  });

  it('uses custom edge ratios', () => {
    expect(getModalClickAction('image', 240, 1000, 0.24)).toBe('prev');
    expect(getModalClickAction('image', 250, 1000, 0.24)).toBe('toggleChrome');
    expect(getModalClickAction('empty', 760, 1000, 0.24)).toBe('next');
    expect(getModalClickAction('empty', 750, 1000, 0.24)).toBe('close');
  });

  it('clamps unsafe edge ratios', () => {
    expect(clampModalEdgeRatio(0.03)).toBe(0.1);
    expect(clampModalEdgeRatio(0.55)).toBe(0.4);
    expect(clampModalEdgeRatio(Number.NaN)).toBe(0.28);
  });
});

describe('reconcileModalOrderAfterFilterChange', () => {
  it('keeps the current image when it remains visible', () => {
    expect(
      reconcileModalOrderAfterFilterChange('b', ['a', 'b', 'c'], ['a', 'b', 'c'])
    ).toEqual({ orderedIds: ['a', 'b', 'c'], selectedId: 'b' });
  });

  it('selects the next visible image when the current image is filtered out', () => {
    expect(
      reconcileModalOrderAfterFilterChange('b', ['a', 'b', 'c', 'd'], ['a', 'c', 'd'])
    ).toEqual({ orderedIds: ['a', 'c', 'd'], selectedId: 'c' });
  });

  it('selects the previous visible image when the removed image was last', () => {
    expect(
      reconcileModalOrderAfterFilterChange('d', ['a', 'b', 'c', 'd'], ['a', 'b', 'c'])
    ).toEqual({ orderedIds: ['a', 'b', 'c'], selectedId: 'c' });
  });

  it('returns no selection when no filtered images remain', () => {
    expect(
      reconcileModalOrderAfterFilterChange('a', ['a'], [])
    ).toEqual({ orderedIds: [], selectedId: null });
  });

  it('falls back to the first visible image if the previous order is missing the current image', () => {
    expect(
      reconcileModalOrderAfterFilterChange('x', ['a', 'b'], ['b', 'c'])
    ).toEqual({ orderedIds: ['b', 'c'], selectedId: 'b' });
  });
});

import { describe, expect, it } from 'vitest';
import {
  getEmptyResultMessage,
  getArrowSelectionIndex,
  getClientFilteredLoadedIds,
  getLoadedResultCounts,
  nextAfterClientFilterMutation,
  getResultCountLabel,
  getZoomCenteredScrollTop,
  isInteractiveShortcutTarget,
  shouldIgnoreViewerShortcut,
  sortFolderBuckets,
  withGridPointerAnchor,
  type FolderBucket,
} from './viewerUi';

describe('shouldIgnoreViewerShortcut', () => {
  it('blocks shortcuts from interactive controls and their descendants', () => {
    const button = document.createElement('button');
    const icon = document.createElement('span');
    button.appendChild(icon);

    expect(shouldIgnoreViewerShortcut(button, document)).toBe(true);
    expect(shouldIgnoreViewerShortcut(icon, document)).toBe(true);
    expect(isInteractiveShortcutTarget(icon)).toBe(true);
    expect(isInteractiveShortcutTarget(document.body)).toBe(false);
  });

  it('blocks shortcuts while a modal or confirmation overlay is mounted', () => {
    const overlay = document.createElement('div');
    overlay.className = 'settings-overlay';
    document.body.appendChild(overlay);

    expect(shouldIgnoreViewerShortcut(document.body, document)).toBe(true);
    overlay.remove();
    expect(shouldIgnoreViewerShortcut(document.body, document)).toBe(false);
  });
});

describe('getEmptyResultMessage', () => {
  it('describes query, client-filter, and folder-empty states', () => {
    expect(getEmptyResultMessage(' portrait ', false)).toBe('No images found for query: portrait');
    expect(getEmptyResultMessage('', true)).toBe('No images match the active filters.');
    expect(getEmptyResultMessage('', false)).toBe('No supported images were found in the selected folders.');
  });
});

describe('sortFolderBuckets', () => {
  const buckets: FolderBucket[] = [
    { key: 'zeta', label: 'Zeta', count: 3 },
    { key: 'alpha', label: 'Alpha', count: 9 },
    { key: 'beta', label: 'Beta', count: 9 },
  ];

  it('sorts folders by name ascending', () => {
    expect(sortFolderBuckets(buckets, 'name-asc').map((bucket) => bucket.label)).toEqual([
      'Alpha',
      'Beta',
      'Zeta',
    ]);
  });

  it('sorts folders by count descending and breaks ties by name', () => {
    expect(sortFolderBuckets(buckets, 'count-desc').map((bucket) => bucket.label)).toEqual([
      'Alpha',
      'Beta',
      'Zeta',
    ]);
  });
});

describe('getResultCountLabel', () => {
  it('separates the loaded shown count from the indexed total without filters', () => {
    expect(getResultCountLabel({
      searchQuery: '',
      searchTotal: 42,
      totalIndexed: 900,
      hiddenFolders: [],
      loadedCount: 100,
      shownCount: 100,
    })).toBe('100 shown · 900 indexed');
  });

  it('keeps server-filtered and indexed totals distinct from what is loaded', () => {
    expect(getResultCountLabel({
      searchQuery: 'portrait',
      searchTotal: 42,
      totalIndexed: 900,
      hiddenFolders: [],
      loadedCount: 10,
      shownCount: 10,
    })).toBe('10 shown · 42 filtered · 900 indexed');
  });

  it('makes date-filtered counts explicit without claiming sparse matches are total matches', () => {
    expect(getResultCountLabel({
      searchQuery: '',
      searchTotal: 5807,
      totalIndexed: 71773,
      dateFrom: '2026-04-26',
      dateTo: '2026-05-25',
      hiddenFolders: [],
      loadedCount: 100,
      shownCount: 15,
    })).toBe('15 shown · 5,807 filtered · 71,773 indexed');
  });
});

describe('getLoadedResultCounts', () => {
  const searchResults = [
    { id: 'favorite' },
    null,
    { id: 'unrated' },
    { id: 'enhanced' },
  ];

  it('counts only loaded client matches instead of sparse server slots', () => {
    expect(getLoadedResultCounts({
      searchResults,
      favorites: { favorite: 3 },
      showFavOnly: true,
      showUnfavOnly: false,
      favoriteFilterLevels: [3],
      showEnhancedOnly: false,
      enhancedSourceIds: { enhanced: true },
    })).toEqual({ loadedCount: 3, shownCount: 1, hasClientFilters: true });
  });

  it('uses all materialized results as shown when client filters are off', () => {
    expect(getLoadedResultCounts({
      searchResults,
      favorites: { favorite: 3 },
      showFavOnly: false,
      showUnfavOnly: false,
      favoriteFilterLevels: [],
      showEnhancedOnly: false,
      enhancedSourceIds: {},
    })).toEqual({ loadedCount: 3, shownCount: 3, hasClientFilters: false });
  });
});

describe('client-filter favorite mutation navigation', () => {
  it('keeps sparse loaded order while applying exact favorite and enhanced predicates', () => {
    expect(getClientFilteredLoadedIds({
      searchResults: [{ id: 'a' }, null, { id: 'b' }, { id: 'c' }],
      favorites: { a: 2, b: 3, c: 2 },
      showFavOnly: true,
      showUnfavOnly: false,
      favoriteFilterLevels: [2],
      showEnhancedOnly: true,
      enhancedSourceIds: { a: true, b: true },
    })).toEqual(['a']);
  });

  it('uses the next survivor at the removed position, then falls back to previous', () => {
    expect(nextAfterClientFilterMutation('b', ['a', 'b', 'c'], ['a', 'c']))
      .toEqual({ shouldSync: true, orderedIds: ['a', 'c'], nextId: 'c' });
    expect(nextAfterClientFilterMutation('c', ['a', 'b', 'c'], ['a', 'b']))
      .toEqual({ shouldSync: true, orderedIds: ['a', 'b'], nextId: 'b' });
  });

  it('closes only after the current filtered image leaves and no survivor remains', () => {
    expect(nextAfterClientFilterMutation('only', ['only'], []))
      .toEqual({ shouldSync: true, orderedIds: [], nextId: null });
    expect(nextAfterClientFilterMutation('b', ['a', 'b'], ['a', 'b']))
      .toEqual({ shouldSync: false, orderedIds: ['a', 'b'], nextId: 'b' });
    expect(nextAfterClientFilterMutation('outside', ['a', 'b'], ['a']))
      .toEqual({ shouldSync: false, orderedIds: ['a'], nextId: 'outside' });
  });
});

describe('getZoomCenteredScrollTop', () => {
  it('keeps the same center row visible after zooming in', () => {
    const nextScrollTop = getZoomCenteredScrollTop(
      {
        scrollTop: 1200,
        viewportHeight: 800,
        rowHeight: 200,
        gridColumns: 5,
        fullCount: 500,
        totalHeight: 20000,
      },
      {
        scrollTop: 1200,
        viewportHeight: 800,
        rowHeight: 320,
        gridColumns: 3,
        fullCount: 500,
        totalHeight: 54000,
      }
    );

    expect(nextScrollTop).toBeGreaterThan(1200);
    expect(nextScrollTop).toBeLessThan(54000);
  });

  it('clamps to valid bounds when the new content is shorter', () => {
    const nextScrollTop = getZoomCenteredScrollTop(
      {
        scrollTop: 1500,
        viewportHeight: 900,
        rowHeight: 240,
        gridColumns: 4,
        fullCount: 24,
        totalHeight: 2400,
      },
      {
        scrollTop: 1500,
        viewportHeight: 900,
        rowHeight: 180,
        gridColumns: 6,
        fullCount: 24,
        totalHeight: 900,
      }
    );

    expect(nextScrollTop).toBe(0);
  });

  it('keeps the same visible item at the same viewport offset when columns change', () => {
    const nextScrollTop = getZoomCenteredScrollTop(
      {
        scrollTop: 1200,
        viewportHeight: 800,
        rowHeight: 200,
        gridColumns: 5,
        fullCount: 500,
        totalHeight: 20000,
        anchorIndex: 42,
        anchorTop: 1600,
        anchorViewportOffset: 400,
      },
      {
        scrollTop: 1200,
        viewportHeight: 800,
        rowHeight: 320,
        gridColumns: 3,
        fullCount: 500,
        totalHeight: 54000,
        anchorIndex: 42,
        anchorTop: 4480,
        anchorViewportOffset: 3280,
      }
    );

    expect(nextScrollTop).toBe(4080);
  });

  it('uses the explicit item top for a grid with date-section offsets', () => {
    const nextScrollTop = getZoomCenteredScrollTop(
      {
        scrollTop: 920,
        viewportHeight: 700,
        rowHeight: 180,
        gridColumns: 4,
        fullCount: 80,
        totalHeight: 5000,
        anchorIndex: 31,
        anchorTop: 1240,
        anchorViewportOffset: 320,
      },
      {
        scrollTop: 920,
        viewportHeight: 700,
        rowHeight: 260,
        gridColumns: 3,
        fullCount: 80,
        totalHeight: 7600,
        anchorIndex: 31,
        anchorTop: 2790,
        anchorViewportOffset: 1870,
      }
    );

    expect(nextScrollTop).toBe(2470);
  });

  it('clamps explicit anchors at both document edges', () => {
    const previous = {
      scrollTop: 320,
      viewportHeight: 600,
      rowHeight: 180,
      gridColumns: 4,
      fullCount: 120,
      totalHeight: 6000,
      anchorIndex: 12,
      anchorTop: 540,
      anchorViewportOffset: 220,
    };

    expect(getZoomCenteredScrollTop(previous, {
      ...previous,
      totalHeight: 10000,
      anchorTop: 80,
    })).toBe(0);
    expect(getZoomCenteredScrollTop(previous, {
      ...previous,
      totalHeight: 10000,
      anchorTop: 9900,
    })).toBe(9400);
  });

  it('keeps a pointer-selected image stable in a one-column mobile grid', () => {
    const previous = withGridPointerAnchor({
      scrollTop: 2100,
      viewportHeight: 560,
      rowHeight: 220,
      gridColumns: 2,
      fullCount: 80,
      totalHeight: 9000,
    }, 23, 2420, 2100);

    expect(previous).not.toBeNull();
    expect(getZoomCenteredScrollTop(previous!, {
      scrollTop: 2100,
      viewportHeight: 560,
      rowHeight: 420,
      gridColumns: 1,
      fullCount: 80,
      totalHeight: 34000,
      anchorIndex: 23,
      anchorTop: 9660,
      anchorViewportOffset: 7560,
    })).toBe(9340);
  });

  it('returns a finite bounded value for empty or invalid layout metrics', () => {
    expect(getZoomCenteredScrollTop(
      {
        scrollTop: 420,
        viewportHeight: 640,
        rowHeight: 220,
        gridColumns: 4,
        fullCount: 40,
        totalHeight: 3000,
      },
      {
        scrollTop: 420,
        viewportHeight: 640,
        rowHeight: 220,
        gridColumns: 4,
        fullCount: 0,
        totalHeight: 0,
      }
    )).toBe(0);

    expect(getZoomCenteredScrollTop(
      {
        scrollTop: Number.NaN,
        viewportHeight: 640,
        rowHeight: 220,
        gridColumns: 4,
        fullCount: 40,
        totalHeight: 3000,
      },
      {
        scrollTop: 0,
        viewportHeight: Number.NaN,
        rowHeight: 0,
        gridColumns: 0,
        fullCount: 40,
        totalHeight: Number.NaN,
      }
    )).toBe(0);
  });
});

describe('withGridPointerAnchor', () => {
  it('records the cursor image without accepting invalid virtual slots', () => {
    const metrics = {
      scrollTop: 1200,
      viewportHeight: 800,
      rowHeight: 200,
      gridColumns: 5,
      fullCount: 50,
      totalHeight: 4000,
    };

    expect(withGridPointerAnchor(metrics, 17, 1400, 1200)).toEqual({
      ...metrics,
      anchorIndex: 17,
      anchorTop: 1400,
      anchorViewportOffset: 200,
    });
    expect(withGridPointerAnchor(metrics, 50, 1400, 1200)).toBe(metrics);
    expect(withGridPointerAnchor(metrics, 17, Number.NaN, 1200)).toBe(metrics);
  });
});

describe('getArrowSelectionIndex', () => {
  it('moves horizontally by one item', () => {
    expect(getArrowSelectionIndex({
      key: 'ArrowRight',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 2,
      itemCount: 10,
    })).toBe(3);
    expect(getArrowSelectionIndex({
      key: 'ArrowLeft',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 2,
      itemCount: 10,
    })).toBe(1);
  });

  it('moves vertically by one row in grid mode', () => {
    expect(getArrowSelectionIndex({
      key: 'ArrowDown',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 2,
      itemCount: 10,
    })).toBe(6);
    expect(getArrowSelectionIndex({
      key: 'ArrowUp',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 6,
      itemCount: 10,
    })).toBe(2);
  });

  it('moves vertically by one item in list mode', () => {
    expect(getArrowSelectionIndex({
      key: 'ArrowDown',
      viewMode: 'list',
      gridColumns: 4,
      currentIndex: 2,
      itemCount: 10,
    })).toBe(3);
    expect(getArrowSelectionIndex({
      key: 'ArrowUp',
      viewMode: 'list',
      gridColumns: 4,
      currentIndex: 2,
      itemCount: 10,
    })).toBe(1);
  });

  it('chooses an edge item when no current selection is present', () => {
    expect(getArrowSelectionIndex({
      key: 'ArrowDown',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: -1,
      itemCount: 10,
    })).toBe(0);
    expect(getArrowSelectionIndex({
      key: 'ArrowUp',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: -1,
      itemCount: 10,
    })).toBe(9);
  });

  it('clamps at collection edges', () => {
    expect(getArrowSelectionIndex({
      key: 'ArrowLeft',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 0,
      itemCount: 10,
    })).toBe(0);
    expect(getArrowSelectionIndex({
      key: 'ArrowDown',
      viewMode: 'grid',
      gridColumns: 4,
      currentIndex: 8,
      itemCount: 10,
    })).toBe(9);
  });
});

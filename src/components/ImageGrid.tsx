'use client';

import React, { useCallback, useMemo, useRef, useEffect, useLayoutEffect, useState } from 'react';
import type { ImageFile } from '../lib/types';
import type { ThumbnailStatusBorderSettings } from '../lib/types';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import { isUnseenMarkerVisible, matchesFavoriteLevel } from '../lib/browserUiPreferences';
import {
  getArrowSelectionIndex,
  getEmptyResultMessage,
  getZoomCenteredScrollTop,
  shouldIgnoreViewerShortcut,
  withGridPointerAnchor,
  type GridMetricsSnapshot,
} from '../lib/viewerUi';
import { reconcileModalOrderAfterFilterChange } from '../lib/modalNavigation';
import { createThumbnailWarmupBatcher, type ThumbnailWarmupPriority } from '../lib/thumbnailWarmupBatcher';
import {
  DEFAULT_THUMB_SIZE,
  THUMB_ZOOM_STEP,
  clampThumbnailSize,
  getThumbnailGridCellWidth,
  getThumbnailGridColumns,
} from '../lib/thumbnailSizing';
import { Minus } from 'lucide-react';
import { buildImageIndexById } from '../lib/imageListState';
import { getThumbnailStatusBorderPresentation } from '../lib/thumbnailStatusBorders';
import {
  buildDateSectionLayout,
  findDateSectionAnchorIndex,
  findDateSectionItemTop,
  formatCompactImageDate,
  formatImageDate,
  GRID_GAP,
  LIST_ROW_HEIGHT,
  shouldUseDateSectionLayout,
  type DateSectionLayout,
  type DateSectionLayoutEntry,
} from '../lib/dateSectionLayout';
import CachedImage from './CachedImage';
import { ScanErrorNotice } from './ScanErrorNotice';
import statusBorderStyles from './ImageGridStatusBorders.module.css';

const OVERSCAN_ROWS = 4;
const SEARCH_PAGE_SIZE = 100;
const COMPACT_ACTIONS_MAX_WIDTH = 150;
const MODAL_WARMUP_DELAY_MS = 1400;
const MODAL_WARMUP_INTERVAL_MS = 2200;
const MODAL_WARMUP_THUMB_BUDGET = 18;
const MODAL_WARMUP_SEARCH_RADIUS = 120;
const BACKGROUND_SEARCH_PAGE_DELAY_MS = 180;
const BACKGROUND_SEARCH_PAGES = 1;
const SCROLL_MEMORY_WRITE_DELAY_MS = 180;
const VISIBLE_THUMB_RESEND_MS = 900;

const ThumbnailStatusBorderOverlay = React.memo(function ThumbnailStatusBorderOverlay({
  favorite,
  enhanced,
  settings,
  placement,
}: {
  favorite: boolean;
  enhanced: boolean;
  settings: ThumbnailStatusBorderSettings;
  placement: 'grid' | 'list';
}) {
  const presentation = getThumbnailStatusBorderPresentation({ favorite, enhanced, settings });
  if (!presentation.favoriteColor && !presentation.enhancedColor && !presentation.enhancedRainbow) return null;

  const className = [
    statusBorderStyles.overlay,
    statusBorderStyles[placement],
    presentation.favoriteColor ? statusBorderStyles.hasFavorite : '',
    presentation.enhancedColor || presentation.enhancedRainbow ? statusBorderStyles.hasEnhanced : '',
    presentation.enhancedRainbow ? statusBorderStyles.enhancedRainbow : '',
  ].filter(Boolean).join(' ');
  const style = {
    '--favorite-thumbnail-border-color': presentation.favoriteColor ?? 'transparent',
    '--enhanced-thumbnail-border-color': presentation.enhancedColor ?? 'transparent',
  } as React.CSSProperties;

  return (
    <span
      className={className}
      style={style}
      data-testid="thumbnail-status-borders"
      data-favorite-border={presentation.favoriteColor ?? undefined}
      data-enhanced-border={presentation.enhancedRainbow ? 'rainbow' : presentation.enhancedColor ?? undefined}
      data-enhanced-border-mode={presentation.enhancedRainbow ? 'rainbow' : presentation.enhancedColor ? 'solid' : undefined}
      aria-hidden="true"
    />
  );
});

function shouldIgnoreGalleryZoomShortcut(target: EventTarget | null): boolean {
  const element = target && typeof (target as Element).closest === 'function'
    ? target as Element
    : null;
  return Boolean(element?.closest([
    'input:not([type="range"])',
    'textarea',
    'select',
    '[contenteditable]:not([contenteditable="false"])',
  ].join(', ')));
}

function withThumbPriorityParams(fileUrl: string, priority: ThumbnailWarmupPriority = 'visible') {
  const separator = fileUrl.includes('?') ? '&' : '?';
  return `${fileUrl}${separator}priority=${priority}`;
}

function dispatchThumbnailWarmup(
  paths: string[],
  dirPath: string,
  priority: ThumbnailWarmupPriority = 'nearby',
  indexToken?: string | null
) {
  if (paths.length === 0) return;
  void fetch('/api/thumbs/warm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ paths, dir: dirPath, priority, ...(indexToken ? { indexToken } : {}) }),
  }).catch(() => {
    // Best-effort only; rendered image requests can still generate thumbs.
  });
}

function getDragImageContentType(filename: string) {
  const extension = filename.split('.').pop()?.toLowerCase();
  if (extension === 'jpg' || extension === 'jpeg') return 'image/jpeg';
  if (extension === 'webp') return 'image/webp';
  if (extension === 'avif') return 'image/avif';
  if (extension === 'gif') return 'image/gif';
  return 'image/png';
}

function formatSectionDate(value?: number): string {
  if (!value) return '';
  const date = new Date(value);
  return `${date.getMonth() + 1}月${date.getDate()}日`;
}

type SectionLayoutEntry =
  | {
      type: 'header';
      key: string;
      top: number;
      height: number;
      dateLabel: string;
    }
  | {
      type: 'item';
      key: string;
      virtualIndex: number;
      top: number;
      left: number;
      width: number;
      height: number;
    };

function getGridItemTop(
  sectionLayout: DateSectionLayout | null,
  virtualIndex: number,
  rowHeight: number,
  gridColumns: number
): number | null {
  if (virtualIndex < 0) return null;
  const sectionTop = findDateSectionItemTop(sectionLayout, virtualIndex);
  if (sectionTop !== null) return sectionTop;
  if (gridColumns <= 0 || rowHeight <= 0) return null;
  return Math.floor(virtualIndex / gridColumns) * rowHeight;
}

function getZoomAnchorIndex({
  sectionLayout,
  scrollTop,
  viewportHeight,
  contentWidth,
  contentOffsetTop,
  rowHeight,
  gridColumns,
  fullCount,
}: {
  sectionLayout: DateSectionLayout | null;
  scrollTop: number;
  viewportHeight: number;
  contentWidth: number;
  contentOffsetTop: number;
  rowHeight: number;
  gridColumns: number;
  fullCount: number;
}): number | null {
  if (fullCount <= 0) return null;

  const anchorY = scrollTop + Math.max(0, viewportHeight) / 2 - Math.max(0, contentOffsetTop);
  const sectionAnchorIndex = findDateSectionAnchorIndex(
    sectionLayout,
    anchorY,
    Math.max(0, contentWidth) / 2
  );
  if (sectionAnchorIndex !== null) return sectionAnchorIndex;

  const safeColumns = Math.max(1, gridColumns);
  const safeRowHeight = Math.max(1, rowHeight);
  const totalRows = Math.max(1, Math.ceil(fullCount / safeColumns));
  const row = Math.min(
    Math.max(0, Math.floor(anchorY / safeRowHeight)),
    totalRows - 1
  );
  const column = Math.min(Math.floor(safeColumns / 2), safeColumns - 1);
  return Math.min(fullCount - 1, row * safeColumns + column);
}

export default function ImageGrid() {
  const {
    searchQuery: catalogSearchQuery,
    searchResults: catalogSearchResults,
    searchTotal: catalogSearchTotal,
    isSearching: catalogIsSearching,
    searchError: catalogSearchError,
    searchErrorKind: catalogSearchErrorKind,
    ensureSearchRange: ensureCatalogSearchRange,
    retrySearch: retryCatalogSearch,
    rescanExpiredSearchSession: rescanCatalogSession,
    dismissSearchError: dismissCatalogSearchError,
    selectImage, openPreviewTab, cycleFavoriteLevel, decreaseFavoriteLevel, favorites, view, setView, selectedIds, showFavOnly, showUnfavOnly, favoriteFilterLevels,
    showEnhancedOnly, enhancedSourceIds,
    closeAllPreviews, clearSelection, setSearchScrollPosition, getSearchScrollPosition,
    seenImageIds, markImageSeen, revealImageId, consumeRevealImage, openModalAtImage,
    modalImageIds, setModalImageIds, selectedIndex, setSelectedIndex,
    requestRevealImage, showSettings,
    thumbnailStatusBorders,
    dirPath, indexToken: catalogIndexToken,
  } = useImageStore();
  const { activeSource, refreshActiveSource } = useOptionalAlbumStore();
  const searchQuery = activeSource ? '' : catalogSearchQuery;
  const searchResults = activeSource ? activeSource.images : catalogSearchResults;
  const searchTotal = activeSource ? activeSource.images.length : catalogSearchTotal;
  const isSearching = activeSource ? false : catalogIsSearching;
  const searchError = activeSource ? null : catalogSearchError;
  const searchErrorKind = activeSource ? null : catalogSearchErrorKind;
  const indexToken = activeSource?.sourceToken ?? catalogIndexToken;
  const ensureSearchRange = useCallback((start: number, end: number) => {
    if (!activeSource) ensureCatalogSearchRange(start, end);
  }, [activeSource, ensureCatalogSearchRange]);
  const retrySearch = useCallback(() => {
    if (activeSource) void refreshActiveSource();
    else retryCatalogSearch();
  }, [activeSource, refreshActiveSource, retryCatalogSearch]);
  const rescanExpiredSearchSession = useCallback(() => {
    if (activeSource) void refreshActiveSource();
    else rescanCatalogSession();
  }, [activeSource, refreshActiveSource, rescanCatalogSession]);
  const dismissSearchError = activeSource ? () => {} : dismissCatalogSearchError;

  const containerRef = useRef<HTMLDivElement>(null);
  const thumbSizeRef = useRef(view.thumbSize);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(0);
  const [viewportWidth, setViewportWidth] = useState(0);
  const [horizontalPadding, setHorizontalPadding] = useState(0);
  const [verticalPaddingTop, setVerticalPaddingTop] = useState(0);
  const preloadRef = useRef<Set<string>>(new Set());
  const visibleWarmRef = useRef<Map<string, number>>(new Map());
  const modalWarmCursorRef = useRef(0);
  const restoredScrollKeyRef = useRef<string | null>(null);
  const previousThumbSizeRef = useRef(view.thumbSize);
  const previousGridMetricsRef = useRef<GridMetricsSnapshot | null>(null);
  const warmupBatcherRef = useRef<ReturnType<typeof createThumbnailWarmupBatcher> | null>(null);
  const [rovingImageId, setRovingImageId] = useState<string | null>(null);
  const pendingPrimaryFocusIdRef = useRef<string | null>(null);
  const clientFilterPagingDemandRef = useRef<{
    contextKey: string;
    targetMatchCount: number;
  } | null>(null);

  useEffect(() => {
    const batcher = createThumbnailWarmupBatcher({
      dispatch: (paths, currentDirPath, priority) => dispatchThumbnailWarmup(
        paths,
        currentDirPath,
        priority,
        indexToken
      ),
    });
    warmupBatcherRef.current = batcher;
    return () => {
      batcher.clear();
      if (warmupBatcherRef.current === batcher) warmupBatcherRef.current = null;
    };
  }, [indexToken]);

  useEffect(() => {
    thumbSizeRef.current = view.thumbSize;
  }, [view.thumbSize]);

  const activeAlbumId = activeSource?.album.id ?? null;
  const scrollMemoryKey = useMemo(
    () => JSON.stringify({
      source: activeAlbumId ? { kind: 'album', id: activeAlbumId } : { kind: 'catalog' },
      dir: dirPath,
      q: searchQuery,
      sortBy: view.sortBy,
      randomSeed: view.sortBy === 'random' ? view.randomSeed : '',
      dateFrom: view.dateFrom,
      dateTo: view.dateTo,
      mode: view.viewMode,
      style: view.displayStyle,
      fav: showFavOnly ? 1 : 0,
      unfav: showUnfavOnly ? 1 : 0,
      favLevels: showFavOnly ? favoriteFilterLevels.join(',') : '',
      enhanced: showEnhancedOnly ? 1 : 0,
      hiddenFolders: view.hiddenFolders,
    }),
    [
      activeAlbumId,
      favoriteFilterLevels,
      dirPath,
      searchQuery,
      showFavOnly,
      showEnhancedOnly,
      showUnfavOnly,
      view.dateFrom,
      view.dateTo,
      view.displayStyle,
      view.hiddenFolders,
      view.randomSeed,
      view.sortBy,
      view.viewMode,
    ]
  );

  useEffect(() => {
    const container = containerRef.current;
    const scrollEl = container?.closest('.viewer-main') as HTMLElement | null;
    if (!scrollEl) return;
    const remembered = getSearchScrollPosition(scrollMemoryKey);
    if (remembered === null) return;
    if (restoredScrollKeyRef.current === scrollMemoryKey) return;
    restoredScrollKeyRef.current = scrollMemoryKey;
    requestAnimationFrame(() => {
      scrollEl.scrollTop = remembered;
      setScrollTop(remembered);
    });
  }, [getSearchScrollPosition, scrollMemoryKey]);

  useEffect(() => {
    const timeoutId = window.setTimeout(() => {
      setSearchScrollPosition(scrollMemoryKey, scrollTop);
    }, SCROLL_MEMORY_WRITE_DELAY_MS);
    return () => window.clearTimeout(timeoutId);
  }, [scrollMemoryKey, scrollTop, setSearchScrollPosition]);

  const loadedOrderedIds = useMemo(
    () => searchResults.filter((img): img is ImageFile => Boolean(img)).map((img) => img.id),
    [searchResults]
  );

  const isClientFiltered = showFavOnly || showUnfavOnly || showEnhancedOnly;

  const clientFilteredVisible = useMemo(() => {
    if (!isClientFiltered) return [] as Array<{ image: ImageFile; sourceIndex: number }>;
    const items: Array<{ image: ImageFile; sourceIndex: number }> = [];
    for (let i = 0; i < searchResults.length; i++) {
      const img = searchResults[i];
      if (!img) continue;
      const level = favorites[img.id] ?? 0;
      const matchesFavorite = showFavOnly
        ? matchesFavoriteLevel(level, favoriteFilterLevels)
        : showUnfavOnly
          ? level === 0
          : true;
      const matchesEnhanced = showEnhancedOnly ? Boolean(enhancedSourceIds[img.id]) : true;
      if (matchesFavorite && matchesEnhanced) {
        items.push({ image: img, sourceIndex: i });
      }
    }
    return items;
  }, [enhancedSourceIds, favoriteFilterLevels, isClientFiltered, showEnhancedOnly, showFavOnly, showUnfavOnly, searchResults, favorites]);

  const filteredOrderedIds = useMemo(
    () => clientFilteredVisible.map((item) => item.image.id),
    [clientFilteredVisible]
  );

  const keyboardSelectionItems = useMemo(() => {
    if (isClientFiltered) {
      return clientFilteredVisible.map((item) => ({
        image: item.image,
        sourceIndex: item.sourceIndex,
      }));
    }
    const items: Array<{ image: ImageFile; sourceIndex: number }> = [];
    for (let i = 0; i < searchResults.length; i++) {
      const image = searchResults[i];
      if (image) items.push({ image, sourceIndex: i });
    }
    return items;
  }, [clientFilteredVisible, isClientFiltered, searchResults]);

  useEffect(() => {
    const availableIds = keyboardSelectionItems.map((item) => item.image.id);
    setRovingImageId((current) => (
      current && availableIds.includes(current) ? current : availableIds[0] ?? null
    ));
  }, [keyboardSelectionItems]);

  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds]);
  const searchResultIndexById = useMemo(
    () => buildImageIndexById(searchResults),
    [searchResults]
  );
  const selectedZoomAnchorIndex = useMemo(() => {
    const selectedId = selectedIds[selectedIds.length - 1];
    if (!selectedId) return null;
    if (isClientFiltered) {
      const filteredIndex = clientFilteredVisible.findIndex((item) => item.image.id === selectedId);
      return filteredIndex >= 0 ? filteredIndex : null;
    }
    return searchResultIndexById.get(selectedId) ?? null;
  }, [clientFilteredVisible, isClientFiltered, searchResultIndexById, selectedIds]);

  useEffect(() => {
    const commitThumbSize = (next: number) => {
      const clamped = clampThumbnailSize(next);
      if (clamped === thumbSizeRef.current) return;
      thumbSizeRef.current = clamped;
      setView({ thumbSize: clamped });
    };

    const canZoomGallery = () => (
      view.viewMode === 'grid' &&
      selectedIndex === null &&
      !showSettings &&
      Boolean(containerRef.current)
    );

    const onWheel = (event: WheelEvent) => {
      if ((!event.ctrlKey && !event.metaKey) || !canZoomGallery() || event.deltaY === 0) return;
      if (
        shouldIgnoreGalleryZoomShortcut(event.target) ||
        shouldIgnoreViewerShortcut(document.body)
      ) return;
      const container = containerRef.current;
      if (!container) return;
      const target = event.target instanceof Element ? event.target : null;
      const anchorElement = target?.closest<HTMLElement>('[data-grid-index]') ?? null;
      const selectedAlreadyAnchored = selectedZoomAnchorIndex !== null &&
        previousGridMetricsRef.current?.anchorIndex === selectedZoomAnchorIndex;
      const scrollEl = container.closest('.viewer-main') as HTMLElement | null;
      if (!selectedAlreadyAnchored && anchorElement && container.contains(anchorElement)) {
        previousGridMetricsRef.current = withGridPointerAnchor(
          previousGridMetricsRef.current,
          Number.parseInt(anchorElement.dataset.gridIndex ?? '', 10),
          Number.parseFloat(anchorElement.style.top),
          scrollEl?.scrollTop ?? previousGridMetricsRef.current?.scrollTop ?? 0
        );
      }

      event.preventDefault();
      commitThumbSize(thumbSizeRef.current + (event.deltaY > 0 ? -THUMB_ZOOM_STEP : THUMB_ZOOM_STEP));
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if ((!event.ctrlKey && !event.metaKey) || event.altKey || !canZoomGallery()) return;
      if (
        shouldIgnoreGalleryZoomShortcut(event.target) ||
        shouldIgnoreViewerShortcut(document.body)
      ) return;

      const key = event.key;
      const next = key === '+' || key === '='
        ? thumbSizeRef.current + THUMB_ZOOM_STEP
        : key === '-' || key === '_'
          ? thumbSizeRef.current - THUMB_ZOOM_STEP
          : key === '0'
            ? DEFAULT_THUMB_SIZE
            : null;
      if (next === null) return;

      event.preventDefault();
      commitThumbSize(next);
    };

    window.addEventListener('wheel', onWheel, { passive: false, capture: true });
    window.addEventListener('keydown', onKeyDown, { passive: false, capture: true });
    return () => {
      window.removeEventListener('wheel', onWheel, { capture: true } as EventListenerOptions);
      window.removeEventListener('keydown', onKeyDown, { capture: true } as EventListenerOptions);
    };
  }, [selectedIndex, selectedZoomAnchorIndex, setView, showSettings, view.viewMode]);
  const modalImageIdKey = modalImageIds.join('\u0001');

  useEffect(() => {
    if (!isClientFiltered || modalImageIds.length === 0) return;
    // Favorite/unrated membership changes are reconciled only by the explicit
    // local mutation that caused them. Reacting here would also move the modal
    // during shared-state hydration or when a filter is merely toggled.
    if (showFavOnly || showUnfavOnly || !showEnhancedOnly) return;
    const currentId = selectedIndex !== null ? searchResults[selectedIndex]?.id ?? null : null;
    const nextState = reconcileModalOrderAfterFilterChange(
      currentId,
      modalImageIds,
      filteredOrderedIds
    );
    const nextKey = nextState.orderedIds.join('\u0001');
    if (modalImageIdKey !== nextKey) {
      setModalImageIds(nextState.orderedIds);
    }

    const nextIndex = nextState.selectedId
      ? searchResultIndexById.get(nextState.selectedId) ?? -1
      : null;
    const resolvedNextIndex = nextIndex !== null && nextIndex >= 0 ? nextIndex : null;
    if (selectedIndex !== resolvedNextIndex) {
      setSelectedIndex(resolvedNextIndex);
    }
  }, [
    filteredOrderedIds,
    isClientFiltered,
    modalImageIdKey,
    modalImageIds,
    searchResultIndexById,
    searchResults,
    selectedIndex,
    setModalImageIds,
    setSelectedIndex,
    showEnhancedOnly,
    showFavOnly,
    showUnfavOnly,
  ]);

  const handleImageDragStart = (event: React.DragEvent, imageId: string, filename: string, fullUrl: string) => {
    const absoluteUrl = new URL(fullUrl, window.location.origin).toString();
    event.dataTransfer.effectAllowed = 'copy';
    event.dataTransfer.setData('text/uri-list', absoluteUrl);
    event.dataTransfer.setData('text/plain', imageId);
    event.dataTransfer.setData('DownloadURL', `${getDragImageContentType(filename)}:${filename}:${absoluteUrl}`);
  };

  const openImageDetail = (image: ImageFile, sourceIndex: number) => {
    markImageSeen(image.id);
    setRovingImageId(image.id);
    openPreviewTab(image, { makeActive: true, pin: true });
    openModalAtImage(
      image.id,
      sourceIndex >= 0 ? sourceIndex : null,
      isClientFiltered ? filteredOrderedIds : []
    );
  };

  const selectFromPrimaryControl = (
    event: React.MouseEvent<HTMLButtonElement> | React.KeyboardEvent<HTMLButtonElement>,
    image: ImageFile,
  ) => {
    markImageSeen(image.id);
    setRovingImageId(image.id);
    selectImage(
      image,
      isClientFiltered ? filteredOrderedIds : loadedOrderedIds,
      { range: event.shiftKey, toggle: event.ctrlKey || event.metaKey }
    );
  };

  const handlePrimaryControlKeyDown = (
    event: React.KeyboardEvent<HTMLButtonElement>,
    image: ImageFile,
    sourceIndex: number,
  ) => {
    if (event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      selectFromPrimaryControl(event, image);
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      openImageDetail(image, sourceIndex);
    }
  };

  const aspectRatioValue = view.aspectMode === 'square' ? 1 : 2 / 3;

  const gridColumns = useMemo(() => {
    if (view.viewMode !== 'grid') return 1;
    const available = Math.max(1, viewportWidth - horizontalPadding);
    return getThumbnailGridColumns({
      availableWidth: available,
      thumbSize: view.thumbSize,
      gap: GRID_GAP,
      explicitColumns: view.columns,
    });
  }, [horizontalPadding, view.columns, view.thumbSize, view.viewMode, viewportWidth]);

  const gridCellWidth = useMemo(() => {
    if (view.viewMode !== 'grid') return 0;
    const available = Math.max(1, viewportWidth - horizontalPadding);
    return getThumbnailGridCellWidth({
      availableWidth: available,
      columns: gridColumns,
      gap: GRID_GAP,
    });
  }, [gridColumns, horizontalPadding, view.viewMode, viewportWidth]);

  const gridCellHeight = useMemo(() => {
    if (view.viewMode !== 'grid') return 0;
    if (view.aspectMode === 'square') return gridCellWidth;
    return Math.round(gridCellWidth / aspectRatioValue);
  }, [aspectRatioValue, gridCellWidth, view.aspectMode, view.viewMode]);
  const compactGridActions = view.viewMode === 'grid' && gridCellWidth > 0 && gridCellWidth <= COMPACT_ACTIONS_MAX_WIDTH;

  useEffect(() => {
    if (selectedIndex !== null || showSettings) return;
    const arrowKeys = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown']);

    const onKeyDown = (event: KeyboardEvent) => {
      if (!arrowKeys.has(event.key)) return;
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const target = event.target instanceof Element ? event.target : null;
      const isPrimaryImageControl = Boolean(target?.closest('[data-image-primary="true"]'));
      if (shouldIgnoreViewerShortcut(event.target) && !isPrimaryImageControl) return;

      const currentId = selectedIds[selectedIds.length - 1] ?? null;
      const currentIndex = currentId
        ? keyboardSelectionItems.findIndex((item) => item.image.id === currentId)
        : -1;
      const targetIndex = getArrowSelectionIndex({
        key: event.key,
        viewMode: view.viewMode,
        gridColumns,
        currentIndex,
        itemCount: keyboardSelectionItems.length,
      });
      if (targetIndex === null) return;

      const targetItem = keyboardSelectionItems[targetIndex];
      if (!targetItem) return;

      event.preventDefault();
      markImageSeen(targetItem.image.id);
      setRovingImageId(targetItem.image.id);
      pendingPrimaryFocusIdRef.current = targetItem.image.id;
      selectImage(
        targetItem.image,
        isClientFiltered ? filteredOrderedIds : loadedOrderedIds
      );
      requestRevealImage(targetItem.image.id);
    };

    window.addEventListener('keydown', onKeyDown, { passive: false });
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [
    filteredOrderedIds,
    gridColumns,
    isClientFiltered,
    keyboardSelectionItems,
    loadedOrderedIds,
    markImageSeen,
    requestRevealImage,
    selectImage,
    selectedIds,
    selectedIndex,
    showSettings,
    view.viewMode,
  ]);

  const showDateSeparators = view.sortBy === 'created-newest' || view.sortBy === 'created-oldest';
  const imageObjectStyle: React.CSSProperties = view.displayStyle === 'poster'
    ? {}
    : view.aspectMode === 'original'
      ? { objectFit: 'contain', background: 'var(--bg-tertiary)' }
      : {};

  const fullCount = isClientFiltered ? clientFilteredVisible.length : searchTotal;

  useEffect(() => {
    const container = containerRef.current;
    const scrollEl = container?.closest('.viewer-main') as HTMLElement | null;
    if (!container || !scrollEl) return;

    let metricsFrameId: number | null = null;

    const updateMetrics = () => {
      metricsFrameId = null;
      const style = window.getComputedStyle(container);
      const leftPad = Number.parseFloat(style.paddingLeft || '0') || 0;
      const rightPad = Number.parseFloat(style.paddingRight || '0') || 0;
      const topPad = Number.parseFloat(style.paddingTop || '0') || 0;
      setHorizontalPadding(leftPad + rightPad);
      setVerticalPaddingTop(topPad);
      setViewportHeight(scrollEl.clientHeight);
      setViewportWidth(container.clientWidth);
      setScrollTop(scrollEl.scrollTop);
    };

    const scheduleMetricsUpdate = () => {
      if (metricsFrameId !== null) return;
      metricsFrameId = window.requestAnimationFrame(updateMetrics);
    };

    updateMetrics();
    scrollEl.addEventListener('scroll', scheduleMetricsUpdate, { passive: true });
    const resizeObserver = new ResizeObserver(scheduleMetricsUpdate);
    resizeObserver.observe(scrollEl);
    resizeObserver.observe(container);
    window.addEventListener('resize', scheduleMetricsUpdate);

    return () => {
      scrollEl.removeEventListener('scroll', scheduleMetricsUpdate);
      resizeObserver.disconnect();
      window.removeEventListener('resize', scheduleMetricsUpdate);
      if (metricsFrameId !== null) {
        window.cancelAnimationFrame(metricsFrameId);
      }
    };
  }, [fullCount, isSearching]);
  const loadedSearchCount = useMemo(
    () => searchResults.reduce((count, image) => count + (image ? 1 : 0), 0),
    [searchResults]
  );
  const canUseSectionLayout = shouldUseDateSectionLayout({
    showDateSeparators,
    itemCount: fullCount,
    loadedSearchCount,
    searchTotal,
    isClientFiltered,
  });

  const sectionLayout = useMemo(() => {
    if (!canUseSectionLayout) return null;

    return buildDateSectionLayout({
      itemCount: fullCount,
      viewMode: view.viewMode,
      gridColumns,
      gridCellWidth,
      gridCellHeight,
      getImageAt: (index) => (
        isClientFiltered
          ? clientFilteredVisible[index]?.image ?? null
          : searchResults[index] ?? null
      ),
    });
  }, [
    clientFilteredVisible,
    fullCount,
    gridCellHeight,
    gridCellWidth,
    gridColumns,
    searchResults,
    canUseSectionLayout,
    isClientFiltered,
    view.viewMode,
  ]);

  const virtualRange = useMemo(() => {
    if (fullCount <= 0) return { start: 0, end: -1, totalHeight: 0 };

    if (sectionLayout) {
      const top = Math.max(0, scrollTop - viewportHeight * 0.75);
      const bottom = scrollTop + viewportHeight * 1.75;
      let start = Number.POSITIVE_INFINITY;
      let end = -1;
      for (const entry of sectionLayout.entries) {
        if (entry.top + entry.height < top || entry.top > bottom) continue;
        if (entry.type !== 'item') continue;
        start = Math.min(start, entry.virtualIndex);
        end = Math.max(end, entry.virtualIndex);
      }
      return {
        start: Number.isFinite(start) ? start : 0,
        end,
        totalHeight: sectionLayout.totalHeight,
      };
    }

    if (view.viewMode === 'list') {
      const start = Math.max(0, Math.floor(scrollTop / LIST_ROW_HEIGHT) - OVERSCAN_ROWS);
      const end = Math.min(
        fullCount - 1,
        Math.ceil((scrollTop + viewportHeight) / LIST_ROW_HEIGHT) + OVERSCAN_ROWS
      );
      return {
        start,
        end,
        totalHeight: fullCount * LIST_ROW_HEIGHT,
      };
    }

    const rowHeight = Math.max(1, gridCellHeight + GRID_GAP);
    const totalRows = Math.ceil(fullCount / gridColumns);
    const startRow = Math.max(0, Math.floor(scrollTop / rowHeight) - OVERSCAN_ROWS);
    const endRow = Math.min(
      totalRows - 1,
      Math.ceil((scrollTop + viewportHeight) / rowHeight) + OVERSCAN_ROWS
    );

    return {
      start: startRow * gridColumns,
      end: Math.min(fullCount - 1, (endRow + 1) * gridColumns - 1),
      totalHeight: Math.max(1, totalRows * rowHeight - GRID_GAP),
    };
  }, [fullCount, gridCellHeight, gridColumns, scrollTop, sectionLayout, view.viewMode, viewportHeight]);

  const visiblePriorityRange = useMemo(() => {
    if (fullCount <= 0) return { start: 0, end: -1 };

    if (sectionLayout) {
      let start = Number.POSITIVE_INFINITY;
      let end = -1;
      for (const entry of sectionLayout.entries) {
        if (entry.top + entry.height < scrollTop || entry.top > scrollTop + viewportHeight) continue;
        if (entry.type !== 'item') continue;
        start = Math.min(start, entry.virtualIndex);
        end = Math.max(end, entry.virtualIndex);
      }
      return {
        start: Number.isFinite(start) ? start : 0,
        end,
      };
    }

    if (view.viewMode === 'list') {
      return {
        start: Math.max(0, Math.floor(scrollTop / LIST_ROW_HEIGHT)),
        end: Math.min(fullCount - 1, Math.ceil((scrollTop + viewportHeight) / LIST_ROW_HEIGHT)),
      };
    }

    const rowHeight = Math.max(1, gridCellHeight + GRID_GAP);
    const startRow = Math.max(0, Math.floor(scrollTop / rowHeight));
    const endRow = Math.min(
      Math.ceil(fullCount / gridColumns) - 1,
      Math.ceil((scrollTop + viewportHeight) / rowHeight)
    );
    return {
      start: startRow * gridColumns,
      end: Math.min(fullCount - 1, (endRow + 1) * gridColumns - 1),
    };
  }, [fullCount, gridCellHeight, gridColumns, scrollTop, sectionLayout, view.viewMode, viewportHeight]);

  const getThumbPriority = (virtualIndex: number): ThumbnailWarmupPriority => (
    virtualIndex >= visiblePriorityRange.start && virtualIndex <= visiblePriorityRange.end
      ? 'visible'
      : 'nearby'
  );

  const visibleSelectedZoomAnchorIndex = selectedZoomAnchorIndex !== null &&
    selectedZoomAnchorIndex >= visiblePriorityRange.start &&
    selectedZoomAnchorIndex <= visiblePriorityRange.end
    ? selectedZoomAnchorIndex
    : null;

  useLayoutEffect(() => {
    if (view.viewMode !== 'grid') {
      previousThumbSizeRef.current = view.thumbSize;
      previousGridMetricsRef.current = null;
      return;
    }

    const rowHeight = Math.max(1, gridCellHeight + GRID_GAP);
    const previousMetrics = previousGridMetricsRef.current;
    const previousThumbSize = previousThumbSizeRef.current;
    const thumbSizeChanged = previousThumbSize !== view.thumbSize;
    const geometryChanged = Boolean(previousMetrics && (
      previousMetrics.gridColumns !== gridColumns ||
      previousMetrics.rowHeight !== rowHeight
    ));
    const shouldPreservePreviousAnchor = thumbSizeChanged || geometryChanged;
    const container = containerRef.current;
    const scrollEl = container?.closest('.viewer-main') as HTMLElement | null;
    const centeredAnchorIndex = getZoomAnchorIndex({
        sectionLayout,
        scrollTop,
        viewportHeight,
        contentWidth: Math.max(0, viewportWidth - horizontalPadding),
        contentOffsetTop: verticalPaddingTop,
        rowHeight,
        gridColumns,
        fullCount,
      }) ?? undefined;
    const previousAnchorWasVisibleSelection = selectedZoomAnchorIndex !== null &&
      previousMetrics?.anchorIndex === selectedZoomAnchorIndex;
    const anchorIndex = shouldPreservePreviousAnchor
      ? previousAnchorWasVisibleSelection
        ? selectedZoomAnchorIndex ?? undefined
        : previousMetrics?.anchorIndex ?? visibleSelectedZoomAnchorIndex ?? centeredAnchorIndex
      : visibleSelectedZoomAnchorIndex ?? centeredAnchorIndex;
    const anchorTop = anchorIndex !== undefined
      ? getGridItemTop(sectionLayout, anchorIndex, rowHeight, gridColumns)
      : null;
    const nextMetrics: GridMetricsSnapshot = {
      scrollTop,
      viewportHeight,
      rowHeight,
      gridColumns,
      fullCount,
      totalHeight: scrollEl?.scrollHeight ?? virtualRange.totalHeight,
      anchorIndex,
      anchorTop: anchorTop ?? undefined,
      anchorViewportOffset: anchorTop !== null ? anchorTop - scrollTop : undefined,
    };
    let metricsForRef = nextMetrics;

    if (
      previousMetrics &&
      shouldPreservePreviousAnchor &&
      previousMetrics.fullCount > 0
    ) {
      if (scrollEl) {
        const nextScrollTop = getZoomCenteredScrollTop(previousMetrics, nextMetrics);
        scrollEl.scrollTop = nextScrollTop;
        setScrollTop(nextScrollTop);
        metricsForRef = {
          ...nextMetrics,
          scrollTop: nextScrollTop,
          anchorViewportOffset: anchorTop !== null ? anchorTop - nextScrollTop : undefined,
        };
      }
    }

    previousThumbSizeRef.current = view.thumbSize;
    previousGridMetricsRef.current = metricsForRef;
  }, [
    fullCount,
    gridCellHeight,
    gridColumns,
    horizontalPadding,
    selectedZoomAnchorIndex,
    sectionLayout,
    scrollTop,
    view.thumbSize,
    view.viewMode,
    verticalPaddingTop,
    viewportHeight,
    viewportWidth,
    virtualRange.totalHeight,
    visibleSelectedZoomAnchorIndex,
  ]);

  useEffect(() => {
    if (isClientFiltered || searchTotal === 0) return;
    if (virtualRange.end < virtualRange.start) return;
    ensureSearchRange(virtualRange.start, virtualRange.end);
  }, [ensureSearchRange, isClientFiltered, searchTotal, virtualRange.end, virtualRange.start]);

  useEffect(() => {
    if (isClientFiltered) return;
    if (searchTotal <= 0) return;
    if (loadedSearchCount >= searchTotal) return;
    if (virtualRange.end < virtualRange.start) return;

    const prefetchStart = Math.min(searchTotal - 1, Math.max(0, virtualRange.end + 1));
    const prefetchEnd = Math.min(searchTotal - 1, prefetchStart + SEARCH_PAGE_SIZE * BACKGROUND_SEARCH_PAGES - 1);
    let hasMissingAhead = false;
    for (let i = prefetchStart; i <= prefetchEnd; i += 1) {
      if (searchResults[i] === null) {
        hasMissingAhead = true;
        break;
      }
    }
    if (!hasMissingAhead) return;

    const timeoutId = window.setTimeout(() => {
      ensureSearchRange(prefetchStart, prefetchEnd);
    }, isSearching ? BACKGROUND_SEARCH_PAGE_DELAY_MS * 2 : BACKGROUND_SEARCH_PAGE_DELAY_MS);

    return () => window.clearTimeout(timeoutId);
  }, [
    ensureSearchRange,
    isSearching,
    loadedSearchCount,
    searchResults,
    searchTotal,
    isClientFiltered,
    virtualRange.end,
    virtualRange.start,
  ]);

  useEffect(() => {
    if (!isClientFiltered) {
      clientFilterPagingDemandRef.current = null;
      return;
    }
    if (searchTotal <= 0) return;

    if (loadedSearchCount >= searchTotal) return;

    const rowHeight = view.viewMode === 'list'
      ? LIST_ROW_HEIGHT
      : Math.max(1, gridCellHeight + GRID_GAP);
    const visibleRows = Math.max(1, Math.ceil(Math.max(1, viewportHeight) / rowHeight));
    const targetMatches = view.viewMode === 'list'
      ? visibleRows + OVERSCAN_ROWS * 6
      : gridColumns * (visibleRows + OVERSCAN_ROWS * 6);
    const matchBuffer = Math.max(24, targetMatches);
    const isNearFilteredEnd = virtualRange.end >= Math.max(0, clientFilteredVisible.length - 1);
    const pagingContextKey = `${scrollMemoryKey}\u0001${indexToken ?? ''}`;
    const previousDemand = clientFilterPagingDemandRef.current;
    const sameContext = previousDemand?.contextKey === pagingContextKey;
    let targetMatchCount = sameContext
      ? Math.max(matchBuffer, previousDemand.targetMatchCount)
      : matchBuffer;

    if (isNearFilteredEnd) {
      // Keep the demand made at the old bottom alive while sparse source pages
      // arrive. Newly appended matches move the bottom away from scrollTop;
      // recomputing only `isNearFilteredEnd` would stop after one batch.
      targetMatchCount = Math.max(
        targetMatchCount,
        clientFilteredVisible.length + matchBuffer,
      );
    }
    clientFilterPagingDemandRef.current = { contextKey: pagingContextKey, targetMatchCount };
    if (clientFilteredVisible.length >= targetMatchCount) return;

    const firstMissing = searchResults.findIndex((image) => image === null);
    if (firstMissing < 0) return;
    ensureSearchRange(firstMissing, Math.min(searchTotal - 1, firstMissing + SEARCH_PAGE_SIZE * 2 - 1));
  }, [
    ensureSearchRange,
    clientFilteredVisible.length,
    gridCellHeight,
    gridColumns,
    loadedSearchCount,
    searchResults,
    searchTotal,
    indexToken,
    isClientFiltered,
    scrollMemoryKey,
    view.viewMode,
    viewportHeight,
    virtualRange.end,
  ]);

  useEffect(() => {
    if (searchTotal <= 0) return;
    if (fullCount <= 0) return;
    const start = Math.max(0, virtualRange.start - gridColumns * 2);
    const end = Math.min(fullCount - 1, virtualRange.end + gridColumns * 8);
    const budget = 80;
    let queued = 0;
    const warmPaths: string[] = [];
    for (let i = start; i <= end && queued < budget; i++) {
      const image = isClientFiltered
        ? clientFilteredVisible[i]?.image
        : searchResults[i];
      if (!image) continue;
      if (preloadRef.current.has(image.id)) continue;
      preloadRef.current.add(image.id);
      warmPaths.push(image.id);
      queued++;
    }
    warmupBatcherRef.current?.enqueue(warmPaths, {
      dirPath,
      contextKey: scrollMemoryKey,
      priority: 'nearby',
    });
    if (preloadRef.current.size > 400) {
      const trimmed = Array.from(preloadRef.current).slice(-300);
      preloadRef.current = new Set(trimmed);
    }
  }, [clientFilteredVisible, dirPath, fullCount, gridColumns, isClientFiltered, scrollMemoryKey, searchResults, searchTotal, virtualRange.end, virtualRange.start]);

  useEffect(() => {
    if (searchTotal <= 0) return;
    if (fullCount <= 0) return;
    if (visiblePriorityRange.end < visiblePriorityRange.start) return;

    const warmPaths: string[] = [];
    const now = Date.now();
    for (let i = visiblePriorityRange.start; i <= visiblePriorityRange.end; i++) {
      const image = isClientFiltered
        ? clientFilteredVisible[i]?.image
        : searchResults[i];
      if (!image) continue;
      const key = `${scrollMemoryKey}\u0001${image.id}`;
      const lastWarmAt = visibleWarmRef.current.get(key);
      if (lastWarmAt !== undefined && now - lastWarmAt < VISIBLE_THUMB_RESEND_MS) continue;
      visibleWarmRef.current.delete(key);
      visibleWarmRef.current.set(key, now);
      warmPaths.push(image.id);
    }

    warmupBatcherRef.current?.enqueue(warmPaths, {
      dirPath,
      contextKey: scrollMemoryKey,
      priority: 'visible',
    });
    if (warmPaths.length > 0) {
      // Visible cards must preempt the delayed nearby/overscan batch. Their
      // <img> requests still work independently if this best-effort warmup fails.
      warmupBatcherRef.current?.flushHighPriority();
    }
    if (visibleWarmRef.current.size > 1200) {
      const trimmed = Array.from(visibleWarmRef.current.entries()).slice(-800);
      visibleWarmRef.current = new Map(trimmed);
    }
  }, [
    clientFilteredVisible,
    dirPath,
    fullCount,
    isClientFiltered,
    scrollMemoryKey,
    searchResults,
    searchTotal,
    visiblePriorityRange.end,
    visiblePriorityRange.start,
  ]);

  useEffect(() => {
    if (selectedIndex === null || searchTotal <= 0) {
      modalWarmCursorRef.current = 0;
      return;
    }

    const warmModalBackground = () => {
      if (document.visibilityState !== 'visible') return;

      const totalSlots = searchResults.length;
      if (totalSlots <= 0) return;
      const windowStart = Math.max(0, selectedIndex - MODAL_WARMUP_SEARCH_RADIUS);
      const windowEnd = Math.min(searchTotal - 1, selectedIndex + MODAL_WARMUP_SEARCH_RADIUS);
      let hasMissingNearby = false;
      for (let index = windowStart; index <= windowEnd; index += 1) {
        if (searchResults[index] === null) {
          hasMissingNearby = true;
          break;
        }
      }
      if (hasMissingNearby) {
        ensureSearchRange(windowStart, windowEnd);
      }

      const offsets: number[] = [0];
      for (let distance = 1; distance <= MODAL_WARMUP_THUMB_BUDGET; distance += 1) {
        offsets.push(distance, -distance);
      }
      const cursor = modalWarmCursorRef.current % Math.max(1, offsets.length);
      let inspected = 0;
      let queued = 0;
      const budget = MODAL_WARMUP_THUMB_BUDGET;
      const warmPaths: string[] = [];

      while (inspected < offsets.length && queued < budget) {
        const offset = offsets[(cursor + inspected) % offsets.length];
        const index = selectedIndex + offset;
        if (index < 0 || index >= totalSlots) {
          inspected++;
          continue;
        }
        const image = searchResults[index];
        if (image && !preloadRef.current.has(image.id)) {
          preloadRef.current.add(image.id);
          warmPaths.push(image.id);
          queued++;
        }
        inspected++;
      }
      warmupBatcherRef.current?.enqueue(warmPaths, {
        dirPath,
        contextKey: scrollMemoryKey,
        priority: 'nearby',
      });

      modalWarmCursorRef.current = (cursor + inspected) % offsets.length;
      if (preloadRef.current.size > 800) {
        const trimmed = Array.from(preloadRef.current).slice(-600);
        preloadRef.current = new Set(trimmed);
      }
    };

    const timeoutId = window.setTimeout(warmModalBackground, MODAL_WARMUP_DELAY_MS);
    const intervalId = window.setInterval(warmModalBackground, MODAL_WARMUP_INTERVAL_MS);
    return () => {
      window.clearTimeout(timeoutId);
      window.clearInterval(intervalId);
    };
  }, [dirPath, ensureSearchRange, scrollMemoryKey, searchResults, searchTotal, selectedIndex]);

  useEffect(() => {
    if (!revealImageId) return;
    const container = containerRef.current;
    const scrollEl = container?.closest('.viewer-main') as HTMLElement | null;
    if (!container || !scrollEl) return;
    const primaryContainer = container;

    const visibleIndex = isClientFiltered
      ? filteredOrderedIds.indexOf(revealImageId)
      : searchResultIndexById.get(revealImageId) ?? -1;

    if (visibleIndex < 0) {
      consumeRevealImage();
      return;
    }

    const sectionItemTop = findDateSectionItemTop(sectionLayout, visibleIndex);
    const targetTop = sectionItemTop !== null
      ? Math.max(0, sectionItemTop - viewportHeight * 0.35)
      : view.viewMode === 'list'
        ? Math.max(0, visibleIndex * LIST_ROW_HEIGHT - viewportHeight * 0.35)
        : Math.max(
          0,
          Math.floor(visibleIndex / gridColumns) * Math.max(1, gridCellHeight + GRID_GAP) - viewportHeight * 0.35
        );

    requestAnimationFrame(() => {
      scrollEl.scrollTop = targetTop;
      setScrollTop(targetTop);
      consumeRevealImage();
      if (pendingPrimaryFocusIdRef.current === revealImageId) {
        requestAnimationFrame(() => {
          const primaryControl = Array.from(
            primaryContainer.querySelectorAll<HTMLButtonElement>('[data-image-primary="true"]')
          ).find((control) => control.dataset.imageId === revealImageId);
          if (primaryControl) {
            primaryControl.focus();
            pendingPrimaryFocusIdRef.current = null;
          }
        });
      }
    });
  }, [
    consumeRevealImage,
    filteredOrderedIds,
    gridCellHeight,
    gridColumns,
    revealImageId,
    searchResults,
    searchResultIndexById,
    sectionLayout,
    isClientFiltered,
    view.viewMode,
    viewportHeight,
  ]);

  const rovingVirtualIndex = rovingImageId
    ? (isClientFiltered
      ? clientFilteredVisible.findIndex((item) => item.image.id === rovingImageId)
      : searchResultIndexById.get(rovingImageId) ?? -1)
    : -1;
  const rovingIsRendered = rovingVirtualIndex >= virtualRange.start && rovingVirtualIndex <= virtualRange.end;
  const fallbackRovingImage = virtualRange.start >= 0
    ? (isClientFiltered
      ? clientFilteredVisible[virtualRange.start]?.image ?? null
      : searchResults[virtualRange.start] ?? null)
    : null;
  const effectiveRovingImageId = rovingIsRendered
    ? rovingImageId
    : fallbackRovingImage?.id ?? rovingImageId;

  if (searchError && fullCount === 0) {
    return (
      <div className="empty-state">
        <ScanErrorNotice
          subject="search"
          message={searchError}
          canRetry
          recoveryAction={searchErrorKind === 'session-expired' ? 'rescan' : 'retry'}
          onRetry={searchErrorKind === 'session-expired' ? rescanExpiredSearchSession : retrySearch}
          onDismiss={dismissSearchError}
        />
      </div>
    );
  }

  if (!isSearching && fullCount === 0) {
    return (
      <div className="empty-state">
        <svg width="64" height="64" viewBox="0 0 24 24" fill="none"
          stroke="currentColor" strokeWidth="1">
          <circle cx="12" cy="12" r="10" />
          <line x1="15" y1="9" x2="9" y2="15" />
          <line x1="9" y1="9" x2="15" y2="15" />
        </svg>
        <p>{getEmptyResultMessage(searchQuery, isClientFiltered)}</p>
      </div>
    );
  }

  const renderListItem = (virtualIndex: number, layoutEntry?: Extract<DateSectionLayoutEntry, { type: 'item' }>) => {
    const sourceIndex = isClientFiltered ? clientFilteredVisible[virtualIndex]?.sourceIndex ?? -1 : virtualIndex;
    const image = isClientFiltered ? clientFilteredVisible[virtualIndex]?.image ?? null : searchResults[sourceIndex];
    const top = layoutEntry?.top ?? virtualIndex * LIST_ROW_HEIGHT;
    const height = layoutEntry?.height ?? LIST_ROW_HEIGHT;

    if (!image) {
      return (
        <div
          key={`slot-list-${virtualIndex}`}
          className="list-item virtual-list-item placeholder"
          style={{ top, height }}
        >
          <div className="list-thumb list-thumb-placeholder" />
          <div className="list-info">
            <span className="list-filename placeholder-line" />
            <span className="list-path placeholder-line short" />
            <span className="list-prompt placeholder-line" />
          </div>
        </div>
      );
    }

    const favLevel = favorites[image.id] ?? 0;
    const isFav = favLevel > 0;
    const isEnhanced = Boolean(enhancedSourceIds[image.id]);
    const isSelected = selectedIdSet.has(image.id);
    const isUnseen = isUnseenMarkerVisible(view.showUnseenMarkers, Boolean(seenImageIds[image.id]));
    const thumbPriority = getThumbPriority(virtualIndex);
    const previousImage = isClientFiltered
      ? clientFilteredVisible[virtualIndex - 1]?.image ?? null
      : searchResults[sourceIndex - 1] ?? null;
    const dateLabel = formatImageDate(image.createdAt);
    const showDateSeparator = showDateSeparators && !sectionLayout && dateLabel !== formatImageDate(previousImage?.createdAt);
    return (
      <div
        key={`slot-list-${virtualIndex}`}
        className={`list-item virtual-list-item ${isSelected ? 'is-selected' : ''} ${isUnseen ? 'is-unseen' : ''} ${showDateSeparator ? 'has-date-separator' : ''}`}
        style={{ top, height }}
        role="group"
        aria-label={`Image ${image.filename}`}
      >
        {showDateSeparator && <div className="date-separator list-date-separator">{dateLabel}</div>}
        <button
          className="list-item-primary"
          type="button"
          data-image-primary="true"
          data-image-id={image.id}
          aria-label={`Select ${image.filename}${isSelected ? ', selected' : ''}. Press Enter to open.`}
          aria-pressed={isSelected}
          tabIndex={effectiveRovingImageId === image.id ? 0 : -1}
          draggable
          onFocus={() => setRovingImageId(image.id)}
          onDragStart={(event) => handleImageDragStart(event, image.id, image.filename, image.fullUrl)}
          onClick={(event) => selectFromPrimaryControl(event, image)}
          onDoubleClick={(event) => {
            event.stopPropagation();
            openImageDetail(image, sourceIndex);
          }}
          onKeyDown={(event) => handlePrimaryControlKeyDown(event, image, sourceIndex)}
        >
          <div className={`list-thumb ${statusBorderStyles.thumbnailHost}`}>
            <CachedImage
              src={image.fileUrl}
              requestSrc={withThumbPriorityParams(image.fileUrl, thumbPriority)}
              fallbackSrc={image.fullUrl}
              cacheKind="thumb"
              alt={image.filename}
              loading={thumbPriority === 'visible' ? 'eager' : 'lazy'}
              decoding="async"
              fetchPriority={thumbPriority === 'visible' ? 'high' : 'auto'}
            />
            <ThumbnailStatusBorderOverlay
              favorite={isFav}
              enhanced={isEnhanced}
              settings={thumbnailStatusBorders}
              placement="list"
            />
          </div>
          <div className="list-info">
            <span className="list-filename">{image.filename}</span>
            <span className="list-path" title={image.id}>{image.id}</span>
            <span className="list-prompt">
              {image.metadata?.prompt?.substring(0, 150) || '(no metadata)'}
            </span>
          </div>
        </button>
        <div className="list-actions">
          <button
            className={`card-fav-step ${isFav ? 'active' : ''}`}
            disabled={!isFav}
            onClick={(event) => {
              event.stopPropagation();
              decreaseFavoriteLevel(image.id);
            }}
            style={{ position: 'static' }}
            title="Decrease favorite level"
            aria-label={`Decrease favorite level for ${image.filename}`}
          >
            <Minus size={13} aria-hidden="true" />
          </button>
          <button
            className={`card-fav ${isFav ? 'active' : ''}`}
            onClick={(event) => { event.stopPropagation(); cycleFavoriteLevel(image.id); }}
            style={{ opacity: 1, position: 'static' }}
            title="Increase favorite level"
            aria-label={`Increase favorite level for ${image.filename}`}
          >
            <svg width="16" height="16" viewBox="0 0 24 24"
              fill={isFav ? 'var(--favorite)' : 'none'}
              stroke={isFav ? 'var(--favorite)' : 'currentColor'} strokeWidth="2">
              <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z" />
            </svg>
            {favLevel > 0 && <span style={{ marginLeft: 4, fontSize: 11, fontWeight: 700 }}>{favLevel}</span>}
          </button>
        </div>
      </div>
    );
  };

  const renderGridItem = (virtualIndex: number, layoutEntry?: Extract<DateSectionLayoutEntry, { type: 'item' }>) => {
    const sourceIndex = isClientFiltered ? clientFilteredVisible[virtualIndex]?.sourceIndex ?? -1 : virtualIndex;
    const image = isClientFiltered ? clientFilteredVisible[virtualIndex]?.image ?? null : searchResults[sourceIndex];
    const row = Math.floor(virtualIndex / gridColumns);
    const col = virtualIndex % gridColumns;
    const top = layoutEntry?.top ?? row * (gridCellHeight + GRID_GAP);
    const left = layoutEntry?.left ?? col * (gridCellWidth + GRID_GAP);
    const width = layoutEntry?.width ?? gridCellWidth;
    const height = layoutEntry?.height ?? gridCellHeight;

    if (!image) {
      return (
        <div
          key={`slot-grid-${virtualIndex}`}
          className="image-card virtual-grid-item placeholder"
          data-grid-index={virtualIndex}
          style={{ top, left, width, height }}
        />
      );
    }

    const favLevel = favorites[image.id] ?? 0;
    const isFav = favLevel > 0;
    const isEnhanced = Boolean(enhancedSourceIds[image.id]);
    const isSelected = selectedIdSet.has(image.id);
    const isUnseen = isUnseenMarkerVisible(view.showUnseenMarkers, Boolean(seenImageIds[image.id]));
    const thumbPriority = getThumbPriority(virtualIndex);
    const previousImage = isClientFiltered
      ? clientFilteredVisible[virtualIndex - 1]?.image ?? null
      : searchResults[sourceIndex - 1] ?? null;
    const dateLabel = formatImageDate(image.createdAt);
    const compactDateLabel = formatCompactImageDate(image.createdAt);
    const showDateSeparator = showDateSeparators && !sectionLayout && dateLabel !== formatImageDate(previousImage?.createdAt);
    return (
      <div
        key={`slot-grid-${virtualIndex}`}
        className={`image-card virtual-grid-item ${isSelected ? 'is-selected' : ''} ${isUnseen ? 'is-unseen' : ''} ${showDateSeparator ? 'has-date-separator' : ''}`}
        data-grid-index={virtualIndex}
        style={{ top, left, width, height }}
        role="group"
        aria-label={`Image ${image.filename}`}
      >
        {showDateSeparator && (
          <div className="date-separator card-date-separator">
            {compactGridActions ? compactDateLabel : dateLabel}
          </div>
        )}
        <button
          className="image-card-primary"
          type="button"
          data-image-primary="true"
          data-image-id={image.id}
          aria-label={`Select ${image.filename}${isSelected ? ', selected' : ''}. Press Enter to open.`}
          aria-pressed={isSelected}
          tabIndex={effectiveRovingImageId === image.id ? 0 : -1}
          draggable
          onFocus={() => setRovingImageId(image.id)}
          onDragStart={(event) => handleImageDragStart(event, image.id, image.filename, image.fullUrl)}
          onClick={(event) => selectFromPrimaryControl(event, image)}
          onDoubleClick={(event) => {
            event.stopPropagation();
            openImageDetail(image, sourceIndex);
          }}
          onKeyDown={(event) => handlePrimaryControlKeyDown(event, image, sourceIndex)}
        >
          <CachedImage
            src={image.fileUrl}
            requestSrc={withThumbPriorityParams(image.fileUrl, thumbPriority)}
            fallbackSrc={image.fullUrl}
            cacheKind="thumb"
            alt={image.filename}
            loading={thumbPriority === 'visible' ? 'eager' : 'lazy'}
            decoding="async"
            fetchPriority={thumbPriority === 'visible' ? 'high' : 'auto'}
            style={imageObjectStyle}
          />
          <div className="card-overlay" aria-hidden="true">
            <span className="card-filename">{image.filename}</span>
          </div>
        </button>
        <ThumbnailStatusBorderOverlay
          favorite={isFav}
          enhanced={isEnhanced}
          settings={thumbnailStatusBorders}
          placement="grid"
        />
        {compactGridActions ? (
          favLevel > 0 && (
            <div className="card-fav-count" title={`Favorite level ${favLevel}`}>
              <svg width="12" height="12" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21.23l8.84-8.84a5.5 5.5 0 0 0 0-7.78z" />
              </svg>
              <span>{favLevel}</span>
            </div>
          )
        ) : (
          <div className="card-fav-stack">
            <button
              className={`card-fav-step ${isFav ? 'active' : ''}`}
              disabled={!isFav}
              onClick={(event) => {
                event.stopPropagation();
                decreaseFavoriteLevel(image.id);
              }}
              title="Decrease favorite level"
              aria-label={`Decrease favorite level for ${image.filename}`}
            >
              <Minus size={13} aria-hidden="true" />
            </button>
            <button
              className={`card-fav ${isFav ? 'active' : ''}`}
              onClick={(event) => {
                event.stopPropagation();
                cycleFavoriteLevel(image.id);
              }}
              title="Increase favorite level"
              aria-label={`Increase favorite level for ${image.filename}`}
            >
              <svg width="16" height="16" viewBox="0 0 24 24"
                fill={isFav ? 'var(--favorite)' : 'none'}
                stroke={isFav ? 'var(--favorite)' : 'currentColor'} strokeWidth="2">
                <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z" />
              </svg>
              {favLevel > 0 && <span style={{ marginLeft: 4, fontSize: 11, fontWeight: 700 }}>{favLevel}</span>}
            </button>
          </div>
        )}
      </div>
    );
  };

  const items: React.ReactNode[] = [];
  if (sectionLayout) {
    const top = Math.max(0, scrollTop - viewportHeight * 0.75);
    const bottom = scrollTop + viewportHeight * 1.75;
    for (const entry of sectionLayout.entries) {
      if (entry.top + entry.height < top || entry.top > bottom) continue;
      if (entry.type === 'header') {
        items.push(
          <div
            key={entry.key}
            className="date-section-header"
            style={{ top: entry.top, height: entry.height }}
          >
            <span>{entry.dateLabel}</span>
            <i aria-hidden="true" />
          </div>
        );
      } else {
        items.push(view.viewMode === 'list'
          ? renderListItem(entry.virtualIndex, entry)
          : renderGridItem(entry.virtualIndex, entry));
      }
    }
  } else {
    for (let i = virtualRange.start; i <= virtualRange.end; i++) {
      items.push(view.viewMode === 'list' ? renderListItem(i) : renderGridItem(i));
    }
  }

  return (
    <div className="grid-container" ref={containerRef}
      onClick={(e) => { if (e.target === e.currentTarget) clearSelection(); }}>
      {searchError && (
        <ScanErrorNotice
          subject="search"
          message={searchError}
          canRetry
          recoveryAction={searchErrorKind === 'session-expired' ? 'rescan' : 'retry'}
          onRetry={searchErrorKind === 'session-expired' ? rescanExpiredSearchSession : retrySearch}
          onDismiss={dismissSearchError}
        />
      )}
      <div
        className={`virtual-canvas ${view.viewMode === 'list' ? 'is-list' : 'is-grid'} display-style-${view.displayStyle}`}
        data-testid="image-grid-background"
        style={{ height: virtualRange.totalHeight }}
        onClick={(event) => {
          if (event.target === event.currentTarget) clearSelection();
        }}
      >
        {items}
      </div>
      {isSearching && <div className="virtual-loading-indicator" />}
    </div>
  );
}

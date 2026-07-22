'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import { clampModalEdgeRatio, getModalClickAction, getSwipeNavigation, type ModalClickAction } from '../lib/modalNavigation';
import { loadCachedImageUrl } from '../lib/clientImageCache';
import { useDialogFocus } from '../lib/useDialogFocus';
import { buildImageIndexById } from '../lib/imageListState';
import { buildPngMetadataRows, formatPngMetadataRowsForCopy } from '../lib/pngMetadataRows';
import { isInteractiveShortcutTarget } from '../lib/viewerUi';
import { formatFileSizeMb } from '../lib/displayedAsset';
import CachedImage from './CachedImage';
import ModalFilmstrip, { type ModalFilmstripItem } from './ModalFilmstrip';
import { cancelEnhancementJob, createEnhancementJob, deleteEnhancementOutput, getEnhancementSettings } from './EnhanceQueuePanel';
import { MetadataTabList, type MetadataTab } from './MetadataTabList';
import { ChevronLeft, ChevronRight, Minus, X } from 'lucide-react';

type PointerGesture = {
  mode: 'pan' | 'swipe';
  pointerId: number;
  startX: number;
  startY: number;
  startTime: number;
  lastX: number;
  lastTime: number;
  panStart: { x: number; y: number };
  moved: boolean;
};

type ModalContextMenuPosition = {
  x: number;
  y: number;
};

type ClipboardTarget = 'prompt' | 'negative' | 'png';

type ClipboardFeedback = {
  target: ClipboardTarget;
  status: 'copied' | 'failed';
};

type ModalEnhancementJob = {
  id: string;
  sourceId: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'canceled' | 'deleted';
  progress: number;
  outputPath?: string;
  errorMessage?: string;
  createdAt?: string;
  presetId?: string;
  presetHash?: string;
  preset?: {
    label: string;
    modelFamily?: 'anime' | 'photo' | 'general';
    modelName?: string;
    scale: number;
    outputFormat: string;
    denoise?: number;
    sharpen?: number;
    detail?: number;
    smoothness?: number;
    colorBrightness?: number;
    colorContrast?: number;
    colorSaturation?: number;
  };
};

type DisplayedAssetResponse = {
  success?: boolean;
  opened?: 'source' | 'enhanced';
  sizeBytes?: number;
  fallback?: { code?: string; message?: string };
  error?: string;
};

function isActiveEnhancementJob(job: ModalEnhancementJob | null) {
  return job?.status === 'queued' || job?.status === 'running';
}

function formatEnhancementVersion(job: ModalEnhancementJob, index: number) {
  const preset = job.preset;
  const scale = preset ? `${preset.scale}x` : 'custom';
  const format = preset?.outputFormat ? preset.outputFormat.toUpperCase() : 'OUTPUT';
  const shortHash = job.presetHash ? job.presetHash.slice(0, 6) : job.id.slice(0, 6);
  return `V${index + 1} ${scale} ${format} ${shortHash}`;
}

function formatEnhancementDetails(job: ModalEnhancementJob | null) {
  if (!job) return '';
  const preset = job.preset;
  if (!preset) return job.presetId ?? job.id;
  const denoise = typeof preset.denoise === 'number' ? ` / denoise ${preset.denoise}` : '';
  const sharpen = typeof preset.sharpen === 'number' ? ` / sharpen ${preset.sharpen}` : '';
  const detail = typeof preset.detail === 'number' ? ` / detail ${preset.detail}` : '';
  const smoothness = typeof preset.smoothness === 'number' ? ` / smooth ${preset.smoothness}` : '';
  const brightness = typeof preset.colorBrightness === 'number' ? ` / brightness ${preset.colorBrightness}` : '';
  const contrast = typeof preset.colorContrast === 'number' ? ` / contrast ${preset.colorContrast}` : '';
  const saturation = typeof preset.colorSaturation === 'number' ? ` / saturation ${preset.colorSaturation}` : '';
  const family = preset.modelFamily ? `${preset.modelFamily} / ` : '';
  return `${preset.label} / ${family}${preset.scale}x / ${preset.outputFormat.toUpperCase()}${denoise}${sharpen}${detail}${smoothness}${brightness}${contrast}${saturation}`;
}

const MAX_READY_FULL_IMAGE_IDS = 120;
const FULL_IMAGE_KEY_SEPARATOR = '\u0000';
export const MODAL_CHROME_TRANSIENT_MS = 900;
export const MODAL_FILMSTRIP_HOVER_ZONE_PX = 128;

function splitPromptTags(prompt: string): string[] {
  const seen = new Set<string>();
  const tags: string[] = [];

  for (const rawTag of prompt.split(',')) {
    const tag = rawTag
      .replace(/^[\s([{]+/, '')
      .replace(/[\s)\]}]+$/, '')
      .trim();
    const key = tag.toLowerCase();
    if (!tag || key.length < 2 || seen.has(key) || tag.includes('\n')) continue;
    seen.add(key);
    tags.push(tag);
    if (tags.length >= 160) break;
  }

  return tags;
}

function parseSearchTags(query: string): string[] {
  return query
    .split(',')
    .map((token) => token.trim())
    .filter(Boolean);
}

function getRelativeClickPosition(event: React.MouseEvent<HTMLElement>) {
  const area = event.currentTarget.closest('.modal-image-area') ?? event.currentTarget;
  const rect = area.getBoundingClientRect();
  return {
    x: event.clientX - rect.left,
    width: rect.width,
  };
}

function isPointOnMainImage(area: HTMLElement, clientX: number, clientY: number) {
  const image = area.querySelector('.modal-main-image');
  if (!image) return false;
  const rect = image.getBoundingClientRect();
  return (
    clientX >= rect.left &&
    clientX <= rect.right &&
    clientY >= rect.top &&
    clientY <= rect.bottom
  );
}

function isShortcutKey(key: string, binding: string) {
  return key.toLowerCase() === binding.toLowerCase();
}

function formatShortcutKey(key: string) {
  const map: Record<string, string> = {
    ArrowLeft: 'Left',
    ArrowRight: 'Right',
    ArrowUp: 'Up',
    ArrowDown: 'Down',
    Escape: 'Esc',
    Delete: 'Del',
    ' ': 'Space',
    Enter: 'Enter',
  };
  return map[key] || key.toUpperCase();
}

function shouldKeepNativeInteractiveKey(target: EventTarget | null, key: string) {
  const element = target && typeof (target as Element).closest === 'function'
    ? target as Element
    : null;
  if (!element || !isInteractiveShortcutTarget(target)) return false;
  const button = element.closest('button');
  if (!button) return true;
  return key === 'Enter' || key === ' ';
}

function getFullImageKey(id: string, fullUrl: string) {
  return `${id}${FULL_IMAGE_KEY_SEPARATOR}${fullUrl}`;
}

export default function ImageModal() {
  const {
    searchResults: catalogSearchResults,
    searchTotal: catalogSearchTotal,
    searchQuery: catalogSearchQuery,
    setSearchQuery,
    selectedIndex,
    setSelectedIndex,
    modalImageIds,
    setModalImageIds,
    ensureSearchRange: ensureCatalogSearchRange,
    resolveModalNavigationTarget: resolveCatalogNavigationTarget,
    cycleFavoriteLevel,
    decreaseFavoriteLevel,
    favorites,
    markImageSeen,
    requestRevealImage,
    keyBindings,
    deleteImage: deleteCatalogImage,
    confirmBeforeDelete,
    setConfirmBeforeDelete,
    view,
    setView,
    indexToken: catalogIndexToken,
    reportImageSessionExpired: reportCatalogSessionExpired,
  } = useImageStore();
  const { activeSource, closeAlbum, recycleSource, refreshActiveSource, openPicker } = useOptionalAlbumStore();
  const searchResults = activeSource ? activeSource.images : catalogSearchResults;
  const searchTotal = activeSource ? activeSource.images.length : catalogSearchTotal;
  const searchQuery = activeSource ? '' : catalogSearchQuery;
  const indexToken = activeSource?.sourceToken ?? catalogIndexToken;
  const ensureSearchRange = useCallback((start: number, end: number) => {
    if (!activeSource) ensureCatalogSearchRange(start, end);
  }, [activeSource, ensureCatalogSearchRange]);
  const reportImageSessionExpired = useCallback(() => {
    if (activeSource) void refreshActiveSource();
    else reportCatalogSessionExpired();
  }, [activeSource, refreshActiveSource, reportCatalogSessionExpired]);
  const resolveModalNavigationTarget = useCallback(async (
    currentIndex: number,
    intent: 'prev' | 'next' | 'delete',
  ) => {
    if (!activeSource) return resolveCatalogNavigationTarget(currentIndex, intent);
    if (searchResults.length === 0) return { status: 'empty' as const, filteredOrderedIds: [] };
    const currentId = searchResults[currentIndex]?.id;
    const orderedIds = modalImageIds.length > 0
      ? modalImageIds.filter((id) => searchResults.some((image) => image?.id === id))
      : searchResults.flatMap((image) => image ? [image.id] : []);
    if (orderedIds.length === 0) return { status: 'empty' as const, filteredOrderedIds: [] };
    const position = Math.max(0, orderedIds.indexOf(currentId ?? ''));
    const nextPosition = intent === 'prev'
      ? (position - 1 + orderedIds.length) % orderedIds.length
      : intent === 'delete'
        ? Math.min(position, orderedIds.length - 1)
        : (position + 1) % orderedIds.length;
    const id = orderedIds[nextPosition];
    const index = searchResults.findIndex((image) => image?.id === id);
    return index >= 0
      ? { status: 'found' as const, index, id, filteredOrderedIds: orderedIds }
      : { status: 'unavailable' as const, filteredOrderedIds: orderedIds };
  }, [activeSource, modalImageIds, resolveCatalogNavigationTarget, searchResults]);
  const deleteImage = activeSource ? recycleSource : deleteCatalogImage;

  const [showConfirmDelete, setShowConfirmDelete] = useState(false);
  const [sidebarTab, setSidebarTab] = useState<MetadataTab>('prompt');
  const [sidebarCollapsed, setSidebarCollapsed] = useState(true);
  const [isMobileSheet, setIsMobileSheet] = useState(false);
  const [clipboardFeedback, setClipboardFeedback] = useState<ClipboardFeedback | null>(null);
  const [flipped, setFlipped] = useState(false);
  const [manualChromeVisible, setManualChromeVisible] = useState(true);
  const [transientChromeVisible, setTransientChromeVisible] = useState(false);
  const [filmstripHoverVisible, setFilmstripHoverVisible] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [isEnhancing, setIsEnhancing] = useState(false);
  const [showEnhanced, setShowEnhanced] = useState(false);
  const [displayedAssetSizeBytes, setDisplayedAssetSizeBytes] = useState<number | null>(null);
  const [displayedAssetStatus, setDisplayedAssetStatus] = useState('');
  const [enhancementJobs, setEnhancementJobs] = useState<ModalEnhancementJob[]>([]);
  const [enhanceError, setEnhanceError] = useState('');
  const [selectedEnhancedJobId, setSelectedEnhancedJobId] = useState('');
  const [pendingAutoShowJobId, setPendingAutoShowJobId] = useState('');
  const [favoriteFeedback, setFavoriteFeedback] = useState<{ level: number; token: number } | null>(null);
  const [contextMenu, setContextMenu] = useState<ModalContextMenuPosition | null>(null);

  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [swipeOffset, setSwipeOffset] = useState({ x: 0, y: 0 });
  const [readyFullImageKeys, setReadyFullImageKeys] = useState<string[]>([]);
  const [failedFullImageKeys, setFailedFullImageKeys] = useState<string[]>([]);
  const pointerGesture = useRef<PointerGesture | null>(null);
  const suppressNextClick = useRef(false);
  const pendingSingleClick = useRef<number | null>(null);
  const previousSelectedIndexRef = useRef<number | null>(null);
  const favoriteFeedbackTimer = useRef<number | null>(null);
  const clipboardFeedbackTimer = useRef<number | null>(null);
  const chromeIdleTimer = useRef<number | null>(null);
  const enhancedDisplayChoiceRef = useRef<Record<string, boolean>>({});
  const modalBodyRef = useRef<HTMLDivElement>(null);
  const modalCloseButtonRef = useRef<HTMLButtonElement>(null);
  const metadataSidebarRef = useRef<HTMLElement>(null);
  const contextMenuRef = useRef<HTMLDivElement>(null);
  const confirmPanelRef = useRef<HTMLDivElement>(null);
  const confirmCancelButtonRef = useRef<HTMLButtonElement>(null);
  const navigationRequestRef = useRef(0);
  const navigationPendingRef = useRef(false);
  const displayedAssetRequestRef = useRef(0);

  useEffect(() => {
    const previousSelectedIndex = previousSelectedIndexRef.current;
    if (selectedIndex === null) {
      setZoom(1);
      setPan({ x: 0, y: 0 });
      setSwipeOffset({ x: 0, y: 0 });
      setManualChromeVisible(true);
      setTransientChromeVisible(false);
      setFilmstripHoverVisible(false);
      setSidebarCollapsed(true);
      pointerGesture.current = null;
      if (pendingSingleClick.current !== null) {
        window.clearTimeout(pendingSingleClick.current);
        pendingSingleClick.current = null;
      }
      if (favoriteFeedbackTimer.current !== null) {
        window.clearTimeout(favoriteFeedbackTimer.current);
        favoriteFeedbackTimer.current = null;
      }
      setFavoriteFeedback(null);
      setContextMenu(null);
      previousSelectedIndexRef.current = null;
      return;
    }

    if (previousSelectedIndex === null) {
      setZoom(1);
      setSidebarCollapsed(true);
    }
    setPan({ x: 0, y: 0 });
    setSwipeOffset({ x: 0, y: 0 });
    setContextMenu(null);
    previousSelectedIndexRef.current = selectedIndex;
  }, [selectedIndex]);

  useEffect(() => () => {
    if (pendingSingleClick.current !== null) {
      window.clearTimeout(pendingSingleClick.current);
      pendingSingleClick.current = null;
    }
    if (favoriteFeedbackTimer.current !== null) {
      window.clearTimeout(favoriteFeedbackTimer.current);
      favoriteFeedbackTimer.current = null;
    }
    if (chromeIdleTimer.current !== null) {
      window.clearTimeout(chromeIdleTimer.current);
      chromeIdleTimer.current = null;
    }
  }, []);

  useEffect(() => {
    if (selectedIndex === null || searchTotal <= 0) return;
    ensureSearchRange(selectedIndex - 2, selectedIndex + 2);
  }, [ensureSearchRange, searchTotal, selectedIndex]);

  const searchResultIndexById = useMemo(
    () => buildImageIndexById(searchResults),
    [searchResults]
  );

  const handleWheel = useCallback((e: React.WheelEvent) => {
    e.preventDefault();
    const delta = e.deltaY > 0 ? 0.9 : 1.1;
    setZoom((currentZoom) => {
      const nextZoom = Math.min(Math.max(currentZoom * delta, 0.25), 10);
      if (nextZoom <= 1 && (pan.x !== 0 || pan.y !== 0)) {
        setPan({ x: 0, y: 0 });
      }
      return nextZoom;
    });
  }, [pan.x, pan.y]);

  const resetZoom = useCallback(() => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
    setSwipeOffset({ x: 0, y: 0 });
  }, []);

  const applyNavigationResolution = useCallback((resolution: Awaited<ReturnType<typeof resolveModalNavigationTarget>>) => {
    if (resolution.status !== 'found') return;
    if (resolution.filteredOrderedIds !== null) {
      setModalImageIds(resolution.filteredOrderedIds);
    } else if (modalImageIds.length > 0) {
      setModalImageIds([]);
    }
    setSelectedIndex(resolution.index);
  }, [modalImageIds.length, setModalImageIds, setSelectedIndex]);

  const navigate = useCallback(async (intent: 'prev' | 'next') => {
    if (selectedIndex === null || searchTotal <= 0) return;
    if (navigationPendingRef.current) return;
    setContextMenu(null);
    setFlipped(false);
    navigationPendingRef.current = true;
    const requestId = ++navigationRequestRef.current;
    try {
      const resolution = await resolveModalNavigationTarget(selectedIndex, intent);
      if (navigationRequestRef.current !== requestId) return;
      applyNavigationResolution(resolution);
    } finally {
      if (navigationRequestRef.current === requestId) {
        navigationPendingRef.current = false;
      }
    }
  }, [applyNavigationResolution, resolveModalNavigationTarget, searchTotal, selectedIndex]);

  const goPrev = useCallback(() => {
    void navigate('prev');
  }, [navigate]);

  const goNext = useCallback(() => {
    void navigate('next');
  }, [navigate]);

  const close = useCallback(() => {
    navigationRequestRef.current += 1;
    navigationPendingRef.current = false;
    const current = selectedIndex !== null ? searchResults[selectedIndex] : null;
    if (current) {
      requestRevealImage(current.id);
    }
    setSelectedIndex(null);
    setModalImageIds([]);
    setFlipped(false);
    setManualChromeVisible(true);
    setTransientChromeVisible(false);
    setFilmstripHoverVisible(false);
    setContextMenu(null);
  }, [requestRevealImage, searchResults, selectedIndex, setModalImageIds, setSelectedIndex]);

  useDialogFocus({
    open: selectedIndex !== null,
    dialogRef: modalBodyRef,
    initialFocusRef: modalCloseButtonRef,
  });
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const media = window.matchMedia('(max-width: 768px)');
    const sync = () => setIsMobileSheet(media.matches);
    sync();
    media.addEventListener?.('change', sync);
    return () => media.removeEventListener?.('change', sync);
  }, []);
  useDialogFocus({
    open: isMobileSheet && !sidebarCollapsed && selectedIndex !== null,
    dialogRef: metadataSidebarRef,
    onEscape: () => setSidebarCollapsed(true),
  });
  useDialogFocus({
    open: showConfirmDelete,
    dialogRef: confirmPanelRef,
    initialFocusRef: confirmCancelButtonRef,
    onEscape: () => setShowConfirmDelete(false),
  });

  const img = selectedIndex !== null ? searchResults[selectedIndex] : null;
  const modalEdgeRatio = clampModalEdgeRatio(view.modalEdgeRatio);
  const modalEdgeZoneWidth = `${Math.round(modalEdgeRatio * 1000) / 10}%`;
  const modalOrderIndex = img && modalImageIds.length > 0 ? modalImageIds.indexOf(img.id) : -1;
  const modalCounter = modalOrderIndex >= 0
    ? `${modalOrderIndex + 1} / ${modalImageIds.length}`
    : `${(selectedIndex ?? 0) + 1} / ${searchTotal}`;
  const filmstripOpen = view.modalFilmstripOpen !== false;
  const chromeHidden = !manualChromeVisible && !transientChromeVisible && !showConfirmDelete && !contextMenu;
  const filmstripInLayout = manualChromeVisible && filmstripOpen;
  const filmstripOverlayVisible = filmstripHoverVisible && !filmstripInLayout;
  const cursorHidden = chromeHidden && !filmstripOverlayVisible;
  const filmstripTotal = modalImageIds.length > 0 ? modalImageIds.length : searchTotal;
  const filmstripActiveIndex = modalOrderIndex >= 0 ? modalOrderIndex : (selectedIndex ?? -1);
  const favLevel = img ? (favorites[img.id] ?? 0) : 0;
  const isFav = favLevel > 0;
  const shouldConfirmImageDelete = confirmBeforeDelete || isFav;
  const currentEnhancementJobs = img ? enhancementJobs.filter((job) => job.sourceId === img.id) : [];
  const succeededEnhancementJobs = currentEnhancementJobs.filter((job) => job.status === 'succeeded' && job.outputPath);
  const activeEnhancementJob = currentEnhancementJobs.find((job) => job.status === 'running' || job.status === 'queued') ?? null;
  const failedEnhancementJob = currentEnhancementJobs.find((job) => job.status === 'failed' && job.errorMessage) ?? null;
  const visibleEnhanceError = enhanceError || failedEnhancementJob?.errorMessage || '';
  const selectedEnhancedJob = succeededEnhancementJobs.find((job) => job.id === selectedEnhancedJobId) ?? succeededEnhancementJobs[0] ?? null;
  const displayedEnhancedJobId = selectedEnhancedJob?.id ?? '';
  const enhancedSrc = selectedEnhancedJob
    ? `/api/enhance/output?jobId=${encodeURIComponent(selectedEnhancedJob.id)}`
    : '';
  const hasEnhancedOutput = Boolean(enhancedSrc);
  const enhancementInProgress = isActiveEnhancementJob(activeEnhancementJob);
  const modalImageSrc = img ? (showEnhanced && enhancedSrc ? enhancedSrc : (img.displayUrl || img.fullUrl)) : '';
  const fullImageKey = img ? getFullImageKey(img.id, modalImageSrc) : '';
  const fullImageReady = fullImageKey
    ? readyFullImageKeys.includes(fullImageKey) && !failedFullImageKeys.includes(fullImageKey)
    : false;

  const getFilmstripItem = useCallback((logicalIndex: number): ModalFilmstripItem | null => {
    if (logicalIndex < 0 || logicalIndex >= filmstripTotal) return null;
    if (modalImageIds.length > 0) {
      const id = modalImageIds[logicalIndex];
      const sourceIndex = searchResultIndexById.get(id);
      const image = sourceIndex === undefined ? null : searchResults[sourceIndex];
      return image && sourceIndex !== undefined ? { image, sourceIndex } : null;
    }
    const image = searchResults[logicalIndex];
    return image ? { image, sourceIndex: logicalIndex } : null;
  }, [filmstripTotal, modalImageIds, searchResultIndexById, searchResults]);

  const requestFilmstripRange = useCallback((startIndex: number, endIndex: number) => {
    if (modalImageIds.length === 0) ensureSearchRange(startIndex, endIndex);
  }, [ensureSearchRange, modalImageIds.length]);

  const selectFilmstripItem = useCallback((item: ModalFilmstripItem) => {
    if (item.sourceIndex === selectedIndex && item.image.id === img?.id) return;
    navigationRequestRef.current += 1;
    navigationPendingRef.current = false;
    setFlipped(false);
    setSelectedIndex(item.sourceIndex);
  }, [img?.id, selectedIndex, setSelectedIndex]);

  const toggleFilmstrip = useCallback(() => {
    setView({ modalFilmstripOpen: !filmstripOpen });
  }, [filmstripOpen, setView]);

  useEffect(() => {
    if (!contextMenu) return;
    const firstEnabledItem = contextMenuRef.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)');
    firstEnabledItem?.focus();

    const closeOnOutsidePointer = (event: PointerEvent) => {
      const target = event.target;
      if (target instanceof Node && contextMenuRef.current?.contains(target)) return;
      setContextMenu(null);
    };
    document.addEventListener('pointerdown', closeOnOutsidePointer, true);
    return () => document.removeEventListener('pointerdown', closeOnOutsidePointer, true);
  }, [contextMenu]);

  useEffect(() => {
    if (img) markImageSeen(img.id);
  }, [img, markImageSeen]);

  useEffect(() => {
    if (!img) {
      setEnhancementJobs([]);
      setEnhanceError('');
      setSelectedEnhancedJobId('');
      setPendingAutoShowJobId('');
      setShowEnhanced(false);
      return;
    }

    let cancelled = false;
    let pollTimeoutId: number | null = null;
    let consecutivePollFailures = 0;
    const schedulePoll = (delayMs = 1000) => {
      if (cancelled || pollTimeoutId !== null) return;
      pollTimeoutId = window.setTimeout(() => {
        pollTimeoutId = null;
        void loadJobs();
      }, delayMs);
    };
    const scheduleRetry = () => {
      consecutivePollFailures += 1;
      schedulePoll(Math.min(1000 * (2 ** Math.min(consecutivePollFailures - 1, 3)), 8000));
    };
    const loadJobs = async () => {
      try {
        const tokenParam = indexToken ? `&indexToken=${encodeURIComponent(indexToken)}` : '';
        const res = await fetch(`/api/enhance/jobs?sourceId=${encodeURIComponent(img.id)}${tokenParam}`, { cache: 'no-store' });
        const data = await res.json().catch(() => ({}));
        if (cancelled) return;
        if (!res.ok) {
          scheduleRetry();
          return;
        }
        consecutivePollFailures = 0;
        const jobs = Array.isArray(data.jobs) ? data.jobs as ModalEnhancementJob[] : [];
        const succeeded = jobs.filter((job) => job.status === 'succeeded' && job.outputPath);
        const autoShowJob = pendingAutoShowJobId
          ? succeeded.find((job) => job.id === pendingAutoShowJobId)
          : null;
        const hasSavedDisplayChoice = Object.prototype.hasOwnProperty.call(enhancedDisplayChoiceRef.current, img.id);
        setEnhancementJobs(jobs);
        if (jobs.some((job) => job.status === 'queued' || job.status === 'running' || job.status === 'succeeded')) {
          setEnhanceError('');
        }
        if (autoShowJob) {
          enhancedDisplayChoiceRef.current[img.id] = true;
          setSelectedEnhancedJobId(autoShowJob.id);
          setShowEnhanced(true);
          setPendingAutoShowJobId('');
        } else {
          setSelectedEnhancedJobId((current) => (
            current && succeeded.some((job) => job.id === current)
              ? current
              : succeeded[0]?.id ?? ''
          ));
          if (succeeded.length > 0) {
            setShowEnhanced(hasSavedDisplayChoice ? enhancedDisplayChoiceRef.current[img.id] : true);
          }
        }
        if (succeeded.length === 0) {
          delete enhancedDisplayChoiceRef.current[img.id];
          setShowEnhanced(false);
        }
        if (pendingAutoShowJobId || jobs.some((job) => job.status === 'queued' || job.status === 'running')) {
          schedulePoll();
        }
      } catch {
        if (!cancelled) scheduleRetry();
      }
    };

    void loadJobs();
    const onChanged = () => void loadJobs();
    window.addEventListener('pvu-enhance-jobs-changed', onChanged);
    return () => {
      cancelled = true;
      if (pollTimeoutId !== null) {
        window.clearTimeout(pollTimeoutId);
      }
      window.removeEventListener('pvu-enhance-jobs-changed', onChanged);
    };
  }, [img, indexToken, pendingAutoShowJobId]);

  const showFavoriteFeedback = useCallback((level: number) => {
    setFavoriteFeedback({ level, token: Date.now() });
    if (favoriteFeedbackTimer.current !== null) {
      window.clearTimeout(favoriteFeedbackTimer.current);
    }
    favoriteFeedbackTimer.current = window.setTimeout(() => {
      favoriteFeedbackTimer.current = null;
      setFavoriteFeedback(null);
    }, 650);
  }, []);

  const increaseFavorite = useCallback(() => {
    if (!img) return;
    const nextLevel = Math.min(5, favLevel + 1);
    cycleFavoriteLevel(img.id);
    if (chromeHidden) showFavoriteFeedback(nextLevel);
  }, [chromeHidden, cycleFavoriteLevel, favLevel, img, showFavoriteFeedback]);

  const decreaseFavorite = useCallback(() => {
    if (!img) return;
    const nextLevel = Math.max(0, favLevel - 1);
    decreaseFavoriteLevel(img.id);
    if (chromeHidden) showFavoriteFeedback(nextLevel);
  }, [chromeHidden, decreaseFavoriteLevel, favLevel, img, showFavoriteFeedback]);

  const rememberFullImageReady = useCallback((key: string) => {
    if (!key) return;
    setFailedFullImageKeys((prev) => prev.filter((item) => item !== key));
    setReadyFullImageKeys((prev) => (
      prev.includes(key) ? prev : [...prev, key].slice(-MAX_READY_FULL_IMAGE_IDS)
    ));
  }, []);

  const rememberFullImageFailed = useCallback((key: string) => {
    if (!key) return;
    setFailedFullImageKeys((prev) => (
      prev.includes(key) ? prev : [...prev, key].slice(-MAX_READY_FULL_IMAGE_IDS)
    ));
  }, []);

  useEffect(() => {
    if (!img || selectedIndex === null) return;

    const candidates = new Map<string, { id: string; displayUrl: string; priority: 'focused' | 'nearby' }>();
    candidates.set(img.id, { id: img.id, displayUrl: img.displayUrl || img.fullUrl, priority: 'focused' });

    const addCandidate = (candidate: typeof img | null | undefined) => {
      if (!candidate) return;
      candidates.set(candidate.id, { id: candidate.id, displayUrl: candidate.displayUrl || candidate.fullUrl, priority: 'nearby' });
    };

    if (modalImageIds.length > 0 && modalOrderIndex >= 0) {
      const prevId = modalImageIds[modalOrderIndex > 0 ? modalOrderIndex - 1 : modalImageIds.length - 1];
      const nextId = modalImageIds[modalOrderIndex < modalImageIds.length - 1 ? modalOrderIndex + 1 : 0];
      addCandidate(searchResults.find((image) => image?.id === prevId) ?? null);
      addCandidate(searchResults.find((image) => image?.id === nextId) ?? null);
    } else {
      addCandidate(searchResults[selectedIndex > 0 ? selectedIndex - 1 : searchTotal - 1]);
      addCandidate(searchResults[selectedIndex < searchTotal - 1 ? selectedIndex + 1 : 0]);
    }

    let cancelled = false;
    for (const candidate of candidates.values()) {
      const key = getFullImageKey(candidate.id, candidate.displayUrl);
      if (readyFullImageKeys.includes(key) || failedFullImageKeys.includes(key)) continue;
      const separator = candidate.displayUrl.includes('?') ? '&' : '?';
      const requestUrl = `${candidate.displayUrl}${separator}priority=${candidate.priority}`;
      loadCachedImageUrl(candidate.displayUrl, requestUrl, 'display')
        .then(() => {
          if (!cancelled) rememberFullImageReady(key);
        })
        .catch(() => {
          if (!cancelled) rememberFullImageFailed(key);
        });
    }

    return () => {
      cancelled = true;
    };
  }, [
    failedFullImageKeys,
    img,
    modalImageIds,
    modalOrderIndex,
    readyFullImageKeys,
    rememberFullImageFailed,
    rememberFullImageReady,
    searchResults,
    searchTotal,
    selectedIndex,
  ]);

  const handleDelete = useCallback(async (favoriteConfirmed = false) => {
    if (!img || isDeleting) return;
    const deletedIndex = selectedIndex ?? -1;
    navigationRequestRef.current += 1;
    navigationPendingRef.current = true;
    setIsDeleting(true);
    setShowConfirmDelete(false);
    let deletionSucceeded = false;
    try {
      const ok = await deleteImage(img.id, { favoriteConfirmed });
      if (!ok) return;
      deletionSucceeded = true;

      // Album recycling selects the next surviving member from the refreshed
      // Album source. Catalog deletion keeps its sparse-search resolver.
      if (activeSource) return;

      const resolution = await resolveModalNavigationTarget(deletedIndex, 'delete');
      if (resolution.status === 'found') {
        applyNavigationResolution(resolution);
      } else if (resolution.status === 'empty' || resolution.status === 'unavailable') {
        // The deleted path no longer exists. An unavailable neighbor must not
        // leave a blank modal, and closing must not reveal the deleted path.
        setSelectedIndex(null);
        setModalImageIds([]);
        setFlipped(false);
        setManualChromeVisible(true);
        setTransientChromeVisible(false);
        setFilmstripHoverVisible(false);
      }
      // A stale result belongs to a replaced query/index window. Do not let it
      // close or move the newer window.
    } catch {
      if (deletionSucceeded) {
        setSelectedIndex(null);
        setModalImageIds([]);
        setFlipped(false);
        setManualChromeVisible(true);
        setTransientChromeVisible(false);
        setFilmstripHoverVisible(false);
      }
    } finally {
      navigationPendingRef.current = false;
      setIsDeleting(false);
    }
  }, [activeSource, applyNavigationResolution, deleteImage, img, isDeleting, resolveModalNavigationTarget, selectedIndex, setModalImageIds, setSelectedIndex]);

  const handleEnhance = useCallback(async () => {
    if (!img || isEnhancing || isActiveEnhancementJob(activeEnhancementJob)) return;
    setIsEnhancing(true);
    setEnhanceError('');
    try {
      const job = await createEnhancementJob(img.id, getEnhancementSettings(), indexToken);
      setSelectedEnhancedJobId(job.id);
      setPendingAutoShowJobId(job.id);
    } catch (error) {
      setEnhanceError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsEnhancing(false);
    }
  }, [activeEnhancementJob, img, indexToken, isEnhancing]);

  const handleDeleteEnhancedOutput = useCallback(async () => {
    if (!selectedEnhancedJob) return;
    const ok = window.confirm('Delete only the selected enhanced output? The original source image will not be touched.');
    if (!ok) return;
    await deleteEnhancementOutput(selectedEnhancedJob.id);
    if (img) delete enhancedDisplayChoiceRef.current[img.id];
    setSelectedEnhancedJobId('');
    setShowEnhanced(false);
  }, [img, selectedEnhancedJob]);

  const handleCancelEnhance = useCallback(async () => {
    if (!activeEnhancementJob) return;
    try {
      await cancelEnhancementJob(activeEnhancementJob.id);
    } catch (error) {
      setEnhanceError(error instanceof Error ? error.message : String(error));
    }
  }, [activeEnhancementJob]);

  const toggleEnhancedView = useCallback(() => {
    if (!img || !hasEnhancedOutput) return;
    setDisplayedAssetStatus('');
    setShowEnhanced((current) => {
      const next = !current;
      enhancedDisplayChoiceRef.current[img.id] = next;
      return next;
    });
  }, [hasEnhancedOutput, img]);

  const resolveDisplayedAsset = useCallback(async (launch: boolean) => {
    if (!img) return;
    const requestId = ++displayedAssetRequestRef.current;
    const params = new URLSearchParams({ path: img.id });
    if (indexToken) params.set('indexToken', indexToken);
    if (showEnhanced && displayedEnhancedJobId) {
      params.set('display', 'enhanced');
      params.set('jobId', displayedEnhancedJobId);
    }

    try {
      const response = await fetch(`/api/open?${params.toString()}`, {
        method: launch ? 'POST' : 'GET',
        cache: 'no-store',
      });
      const data = await response.json() as DisplayedAssetResponse;
      if (requestId !== displayedAssetRequestRef.current) return;
      if (!response.ok || !data.success) {
        setDisplayedAssetStatus(data.error || (launch
          ? 'Could not open the displayed image. You can retry.'
          : 'Could not read the displayed file size.'));
        if (!launch) setDisplayedAssetSizeBytes(null);
        return;
      }

      setDisplayedAssetSizeBytes(typeof data.sizeBytes === 'number' ? data.sizeBytes : null);
      if (data.fallback) {
        enhancedDisplayChoiceRef.current[img.id] = false;
        setShowEnhanced(false);
        setDisplayedAssetStatus(data.fallback.message || 'Enhanced output is unavailable; using Original instead.');
      } else if (launch) {
        setDisplayedAssetStatus(data.opened === 'enhanced'
          ? 'Opened the displayed Enhanced image.'
          : 'Opened the displayed Original image.');
      }
    } catch {
      if (requestId !== displayedAssetRequestRef.current) return;
      setDisplayedAssetStatus(launch
        ? 'Could not open the displayed image. You can retry.'
        : 'Could not read the displayed file size.');
      if (!launch) setDisplayedAssetSizeBytes(null);
    }
  }, [displayedEnhancedJobId, img, indexToken, showEnhanced]);

  useEffect(() => {
    setDisplayedAssetStatus('');
  }, [img?.id]);

  useEffect(() => {
    if (!img) {
      displayedAssetRequestRef.current += 1;
      setDisplayedAssetSizeBytes(null);
      setDisplayedAssetStatus('');
      return;
    }
    void resolveDisplayedAsset(false);
    return () => {
      displayedAssetRequestRef.current += 1;
    };
  }, [img, resolveDisplayedAsset]);

  const clearChromeIdleTimer = useCallback(() => {
    if (chromeIdleTimer.current === null) return;
    window.clearTimeout(chromeIdleTimer.current);
    chromeIdleTimer.current = null;
  }, []);

  const scheduleTransientChromeHide = useCallback(() => {
    clearChromeIdleTimer();
    if (selectedIndex === null || showConfirmDelete || manualChromeVisible) return;
    chromeIdleTimer.current = window.setTimeout(() => {
      chromeIdleTimer.current = null;
      setTransientChromeVisible(false);
    }, MODAL_CHROME_TRANSIENT_MS);
  }, [clearChromeIdleTimer, manualChromeVisible, selectedIndex, showConfirmDelete]);

  const revealChromeForActivity = useCallback(() => {
    if (selectedIndex === null || showConfirmDelete || manualChromeVisible) return;
    setTransientChromeVisible(true);
    scheduleTransientChromeHide();
  }, [manualChromeVisible, scheduleTransientChromeHide, selectedIndex, showConfirmDelete]);

  const toggleManualChrome = useCallback(() => {
    clearChromeIdleTimer();
    const nextVisible = !manualChromeVisible;
    setManualChromeVisible(nextVisible);
    setTransientChromeVisible(false);
    setFilmstripHoverVisible(false);
  }, [clearChromeIdleTimer, manualChromeVisible]);

  const handleModalPointerMove = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    if (selectedIndex === null || showConfirmDelete) return;
    revealChromeForActivity();
    if (event.pointerType === 'touch') return;
    const rect = event.currentTarget.getBoundingClientRect();
    const distanceFromBottom = rect.bottom - event.clientY;
    const isNearBottom = distanceFromBottom >= 0 && distanceFromBottom <= MODAL_FILMSTRIP_HOVER_ZONE_PX;
    setFilmstripHoverVisible(isNearBottom);
  }, [revealChromeForActivity, selectedIndex, showConfirmDelete]);

  const handleModalPointerLeave = useCallback(() => {
    setFilmstripHoverVisible(false);
  }, []);

  useEffect(() => {
    if (selectedIndex === null) {
      clearChromeIdleTimer();
      return;
    }
    if (showConfirmDelete) {
      clearChromeIdleTimer();
      setFilmstripHoverVisible(false);
      return;
    }
    if (manualChromeVisible) {
      clearChromeIdleTimer();
      setTransientChromeVisible(false);
      return;
    }
    if (!transientChromeVisible) {
      clearChromeIdleTimer();
      return;
    }
    scheduleTransientChromeHide();
    return clearChromeIdleTimer;
  }, [clearChromeIdleTimer, manualChromeVisible, scheduleTransientChromeHide, selectedIndex, showConfirmDelete, transientChromeVisible]);

  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
    const key = e.key;
    if (contextMenu && key === keyBindings.closeModal) {
      e.preventDefault();
      setContextMenu(null);
      modalBodyRef.current?.focus();
      return;
    }
    if (key === keyBindings.closeModal) {
      close();
      return;
    }
    if (shouldKeepNativeInteractiveKey(e.target, key)) return;
    if (showConfirmDelete || isDeleting) return;
    revealChromeForActivity();

    if (key === keyBindings.prevImage) goPrev();
    else if (key === keyBindings.nextImage) goNext();
    else if (isShortcutKey(key, keyBindings.toggleFavorite) && img) increaseFavorite();
    else if (isShortcutKey(key, keyBindings.decreaseFavorite) && img) decreaseFavorite();
    else if (key === keyBindings.deleteImage) {
      if (shouldConfirmImageDelete) setShowConfirmDelete(true);
      else void handleDelete();
    } else if (isShortcutKey(key, keyBindings.flipHorizontal)) setFlipped((f) => !f);
    else if (key === keyBindings.zoomIn || key === '+') {
      e.preventDefault();
      setZoom((currentZoom) => Math.min(currentZoom * 1.15, 10));
    } else if (key === keyBindings.zoomOut) {
      e.preventDefault();
      setZoom((currentZoom) => {
        const nextZoom = Math.max(currentZoom / 1.15, 0.25);
        if (nextZoom <= 1) setPan({ x: 0, y: 0 });
        return nextZoom;
      });
    } else if (key === keyBindings.zoomReset) {
      e.preventDefault();
      resetZoom();
    }
    else if (isShortcutKey(key, keyBindings.toggleFilmstrip)) {
      e.preventDefault();
      toggleFilmstrip();
    }
    else if (isShortcutKey(key, keyBindings.addToAlbum) && img) {
      e.preventDefault();
      openPicker([img.id]);
    }
    else if (key.toLowerCase() === 'e' && hasEnhancedOutput) {
      e.preventDefault();
      toggleEnhancedView();
    }
    else if (isShortcutKey(key, keyBindings.enhanceImage) && img && !isEnhancing && !enhancementInProgress) {
      e.preventDefault();
      void handleEnhance();
    }
    else if (key === ' ') {
      e.preventDefault();
      setSidebarCollapsed((prev) => !prev);
    }
    else if (key === 'Enter' && img) {
      e.preventDefault();
      void resolveDisplayedAsset(true);
    }
  }, [close, contextMenu, decreaseFavorite, enhancementInProgress, goNext, goPrev, handleDelete, handleEnhance, hasEnhancedOutput, img, increaseFavorite, isDeleting, isEnhancing, keyBindings, openPicker, resetZoom, resolveDisplayedAsset, revealChromeForActivity, shouldConfirmImageDelete, showConfirmDelete, toggleEnhancedView, toggleFilmstrip]);

  useEffect(() => {
    if (selectedIndex !== null) {
      window.addEventListener('keydown', handleKeyDown);
      document.body.style.overflow = 'hidden';
    }
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
      document.body.style.overflow = '';
    };
  }, [handleKeyDown, selectedIndex]);

  const copyToClipboard = useCallback(async (target: ClipboardTarget, text: string) => {
    if (clipboardFeedbackTimer.current !== null) {
      window.clearTimeout(clipboardFeedbackTimer.current);
      clipboardFeedbackTimer.current = null;
    }
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(text);
      setClipboardFeedback({ target, status: 'copied' });
    } catch {
      setClipboardFeedback({ target, status: 'failed' });
    }
    clipboardFeedbackTimer.current = window.setTimeout(() => {
      clipboardFeedbackTimer.current = null;
      setClipboardFeedback(null);
    }, 1500);
  }, []);

  useEffect(() => () => {
    if (clipboardFeedbackTimer.current !== null) {
      window.clearTimeout(clipboardFeedbackTimer.current);
      clipboardFeedbackTimer.current = null;
    }
  }, []);

  const clipboardButtonLabel = useCallback((target: ClipboardTarget) => {
    if (clipboardFeedback?.target !== target) return 'Copy';
    return clipboardFeedback.status === 'copied' ? 'Copied' : 'Copy failed';
  }, [clipboardFeedback]);

  const handlePointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const target = e.target as HTMLElement;
    if (target.closest('.zoom-indicator')) return;
    if (target.closest('.modal-topbar')) return;
    if (target.closest('.modal-sidebar')) return;
    if (target.closest('.modal-filmstrip-shell')) return;

    const startsOnImage = Boolean(target.closest('.modal-main-image'));
    const mode = zoom > 1 && startsOnImage ? 'pan' : 'swipe';
    const now = performance.now();
    pointerGesture.current = {
      mode,
      pointerId: e.pointerId,
      startX: e.clientX,
      startY: e.clientY,
      startTime: now,
      lastX: e.clientX,
      lastTime: now,
      panStart: { ...pan },
      moved: false,
    };
    suppressNextClick.current = false;
    if (mode === 'pan') {
      e.preventDefault();
    }
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // Pointer capture can fail in tests or if the pointer has already ended.
    }
  }, [pan, zoom]);

  const handlePointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const gesture = pointerGesture.current;
    if (!gesture || gesture.pointerId !== e.pointerId) return;

    const dx = e.clientX - gesture.startX;
    const dy = e.clientY - gesture.startY;
    if (Math.abs(dx) + Math.abs(dy) > 4) {
      gesture.moved = true;
    }
    gesture.lastX = e.clientX;
    gesture.lastTime = performance.now();

    if (gesture.mode === 'pan') {
      setPan({
        x: gesture.panStart.x + dx,
        y: gesture.panStart.y + dy,
      });
      return;
    }

    setSwipeOffset({ x: dx, y: dy });
  }, []);

  const finishPointerGesture = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const gesture = pointerGesture.current;
    if (!gesture || gesture.pointerId !== e.pointerId) return;
    pointerGesture.current = null;

    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      // Ignore browsers/tests that do not hold capture.
    }

    const dx = e.clientX - gesture.startX;
    const dy = e.clientY - gesture.startY;
    const elapsedMs = Math.max(1, performance.now() - gesture.startTime);
    suppressNextClick.current = gesture.moved;

    if (gesture.mode === 'pan') {
      return;
    }

    setSwipeOffset({ x: 0, y: 0 });
    if (!gesture.moved) return;

    const rect = e.currentTarget.getBoundingClientRect();
    const navigation = getSwipeNavigation(dx, dy, elapsedMs, rect.width, rect.height);
    if (navigation === 'prev') goPrev();
    else if (navigation === 'next') goNext();
    else if (navigation === 'close') close();
  }, [close, goNext, goPrev]);

  const cancelPointerGesture = useCallback(() => {
    pointerGesture.current = null;
    setSwipeOffset({ x: 0, y: 0 });
  }, []);

  const runModalClickAction = useCallback((action: ModalClickAction) => {
    if (action === 'toggleChrome') toggleManualChrome();
    else if (action === 'prev') goPrev();
    else if (action === 'next') goNext();
    else close();
  }, [close, goNext, goPrev, toggleManualChrome]);

  const scheduleSingleClickAction = useCallback((action: ModalClickAction) => {
    if (pendingSingleClick.current !== null) {
      window.clearTimeout(pendingSingleClick.current);
    }
    pendingSingleClick.current = window.setTimeout(() => {
      pendingSingleClick.current = null;
      runModalClickAction(action);
    }, 180);
  }, [runModalClickAction]);

  const cancelSingleClickAction = useCallback(() => {
    if (pendingSingleClick.current !== null) {
      window.clearTimeout(pendingSingleClick.current);
      pendingSingleClick.current = null;
    }
  }, []);

  const handleImageAreaClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    if (suppressNextClick.current) {
      suppressNextClick.current = false;
      return;
    }
    if (e.detail > 1) {
      cancelSingleClickAction();
      return;
    }
    const target = e.target as HTMLElement;
    if (target.closest('.zoom-indicator')) return;
    if (target.closest('.modal-filmstrip-shell')) return;
    const { x, width } = getRelativeClickPosition(e);
    const clickTarget = target.closest('.modal-main-image') || isPointOnMainImage(e.currentTarget, e.clientX, e.clientY)
      ? 'image'
      : 'empty';
    scheduleSingleClickAction(getModalClickAction(clickTarget, x, width, modalEdgeRatio));
  }, [cancelSingleClickAction, modalEdgeRatio, scheduleSingleClickAction]);

  const handleImageAreaDoubleClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    e.preventDefault();
    cancelSingleClickAction();
    const target = e.target as HTMLElement;
    if (target.closest('.zoom-indicator')) return;
    if (target.closest('.modal-sidebar')) return;
    if (target.closest('.modal-filmstrip-shell')) return;
    if (!target.closest('.modal-main-image') && !isPointOnMainImage(e.currentTarget, e.clientX, e.clientY)) return;
    setSidebarCollapsed((prev) => !prev);
  }, [cancelSingleClickAction]);

  const handleImageClick = useCallback((e: React.MouseEvent<HTMLImageElement>) => {
    e.stopPropagation();
    if (suppressNextClick.current) {
      suppressNextClick.current = false;
      return;
    }
    if (e.detail > 1) {
      cancelSingleClickAction();
      return;
    }
    const { x, width } = getRelativeClickPosition(e);
    scheduleSingleClickAction(getModalClickAction('image', x, width, modalEdgeRatio));
  }, [cancelSingleClickAction, modalEdgeRatio, scheduleSingleClickAction]);

  const handleImageDoubleClick = useCallback((e: React.MouseEvent<HTMLImageElement>) => {
    e.preventDefault();
    e.stopPropagation();
    cancelSingleClickAction();
    setSidebarCollapsed((prev) => !prev);
  }, [cancelSingleClickAction]);

  const handleImageAreaContextMenu = useCallback((event: React.MouseEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();
    cancelSingleClickAction();
    pointerGesture.current = null;
    setSwipeOffset({ x: 0, y: 0 });
    const menuWidth = 248;
    const menuHeight = 372;
    setContextMenu({
      x: Math.max(8, Math.min(event.clientX, window.innerWidth - menuWidth - 8)),
      y: Math.max(8, Math.min(event.clientY, window.innerHeight - menuHeight - 8)),
    });
  }, [cancelSingleClickAction]);

  const handleContextMenuKeyDown = useCallback((event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      setContextMenu(null);
      modalBodyRef.current?.focus();
      return;
    }
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    const items = Array.from(contextMenuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') ?? []);
    if (items.length === 0) return;
    event.preventDefault();
    event.stopPropagation();
    const currentIndex = items.indexOf(document.activeElement as HTMLButtonElement);
    const nextIndex = event.key === 'Home'
      ? 0
      : event.key === 'End'
        ? items.length - 1
        : event.key === 'ArrowUp'
          ? (currentIndex <= 0 ? items.length - 1 : currentIndex - 1)
          : (currentIndex + 1) % items.length;
    items[nextIndex]?.focus();
  }, []);

  if (selectedIndex === null) return null;

  if (!img) {
    return (
      <div className="modal-overlay">
        <div className="modal-backdrop" data-testid="modal-backdrop" aria-hidden="true" onClick={close} />
        <div ref={modalBodyRef} className="modal-body" role="dialog" aria-modal="true" aria-label="Image preview loading" tabIndex={-1}>
          <div className="modal-topbar">
            <div className="modal-topbar-left">
              <span className="modal-filename">Loading...</span>
              <span className="modal-counter">{selectedIndex + 1} / {searchTotal}</span>
            </div>
          </div>
        </div>
      </div>
    );
  }

  const raw = img.metadata?.raw || '';
  const prompt = img.metadata?.prompt || raw.split('Negative prompt:')[0] || '';
  const negative = img.metadata?.negativePrompt || raw.split('Negative prompt:')[1]?.split(/\nSteps:/)[0] || '';
  const settingsRaw = Object.entries(img.metadata?.settings || {})
    .map(([k, v]) => `${k}: ${v}`)
    .join(', ')
    || (raw.match(/\nSteps:[\s\S]*$/) ? raw.match(/\nSteps:([\s\S]*$)/)?.[1] || '' : '');
  const promptTags = splitPromptTags(prompt);
  const pngMetadataRows = buildPngMetadataRows(img.metadata);
  const pngMetadataCopyText = formatPngMetadataRowsForCopy(pngMetadataRows);
  const addPromptTagToSearch = (tag: string) => {
    const currentTags = parseSearchTags(catalogSearchQuery);
    const currentKeys = new Set(currentTags.map((item) => item.toLowerCase()));
    const nextTags = currentKeys.has(tag.toLowerCase()) ? currentTags : [...currentTags, tag];
    if (activeSource) closeAlbum();
    else {
      setSelectedIndex(null);
      setModalImageIds([]);
    }
    setFlipped(false);
    setManualChromeVisible(true);
    setTransientChromeVisible(false);
    setFilmstripHoverVisible(false);
    setSearchQuery(nextTags.join(', '));
  };

  return (
    <>
      <div className="modal-overlay">
        <div className="modal-backdrop" data-testid="modal-backdrop" aria-hidden="true" onClick={close} />

        <div
          ref={modalBodyRef}
          className={`modal-body ${chromeHidden ? 'chrome-hidden' : ''} ${cursorHidden ? 'cursor-hidden' : ''}`}
          role="dialog"
          aria-modal="true"
          aria-label={`Image preview: ${img.filename}`}
          tabIndex={-1}
          onPointerMove={handleModalPointerMove}
          onPointerLeave={handleModalPointerLeave}
          data-manual-chrome={manualChromeVisible ? 'visible' : 'hidden'}
        >
          <div className="modal-topbar">
            <div className="modal-topbar-left">
              <span className="modal-filename">{img.filename}</span>
              <span className="modal-counter">{modalCounter}</span>
              <span className="modal-file-size" aria-label="Displayed file size">{formatFileSizeMb(displayedAssetSizeBytes)}</span>
            </div>
            <div className="modal-topbar-right">
              <button className={`modal-icon-btn ${isFav ? 'fav-active' : ''}`} onClick={increaseFavorite} title="Favorite +1" aria-label="Increase favorite level">
                F
                {favLevel > 0 && <span style={{ marginLeft: 4, fontSize: 11, fontWeight: 700 }}>{favLevel}</span>}
              </button>
              <button className="modal-icon-btn" onClick={decreaseFavorite} title="Favorite -1" aria-label="Decrease favorite level">
                <Minus size={16} aria-hidden="true" />
              </button>
              <button className="modal-icon-btn" onClick={() => setFlipped((f) => !f)} title="Flip" aria-label="Flip horizontally">H</button>
              <button className="modal-icon-btn" onClick={() => void resolveDisplayedAsset(true)} title="Open external" aria-label="Open in external viewer">O</button>
              <button
                className="modal-icon-btn"
                onClick={(event) => {
                  event.stopPropagation();
                  void handleEnhance();
                }}
                title="Enhance"
                aria-label="Enhance image"
                disabled={isEnhancing || enhancementInProgress}
              >
                {isEnhancing ? '...' : enhancementInProgress ? `${activeEnhancementJob?.progress ?? 0}%` : 'AI'}
              </button>
              {enhancementInProgress && (
                <div className="modal-enhance-progress" title="Enhancement progress">
                  <div style={{ width: `${Math.max(0, Math.min(100, activeEnhancementJob?.progress ?? 0))}%` }} />
                </div>
              )}
              {enhancementInProgress && activeEnhancementJob && (
                <button
                  className="modal-icon-btn danger"
                  onClick={(event) => {
                    event.stopPropagation();
                    void handleCancelEnhance();
                  }}
                  title="Cancel enhancement"
                  aria-label="Cancel enhancement"
                >
                  <X size={16} aria-hidden="true" />
                </button>
              )}
              {visibleEnhanceError && !enhancementInProgress && (
                <div className="modal-enhance-error" title={visibleEnhanceError}>
                  {visibleEnhanceError}
                </div>
              )}
              {succeededEnhancementJobs.length > 0 && (
                <select
                  className="modal-enhance-version-select"
                  value={selectedEnhancedJob?.id ?? ''}
                  onChange={(event) => {
                    setSelectedEnhancedJobId(event.target.value);
                    if (img) enhancedDisplayChoiceRef.current[img.id] = true;
                    setShowEnhanced(true);
                  }}
                  onClick={(event) => event.stopPropagation()}
                  title={formatEnhancementDetails(selectedEnhancedJob)}
                  aria-label="Enhanced version"
                >
                  {succeededEnhancementJobs.map((job, index) => (
                    <option key={job.id} value={job.id}>
                      {formatEnhancementVersion(job, index)}
                    </option>
                  ))}
                </select>
              )}
              <button
                className={`modal-icon-btn ${showEnhanced ? 'fav-active' : ''}`}
                onClick={(event) => {
                  event.stopPropagation();
                  toggleEnhancedView();
                }}
                title={hasEnhancedOutput ? `Toggle original/enhanced (E): ${formatEnhancementDetails(selectedEnhancedJob)}` : 'Toggle original/enhanced (E)'}
                aria-label="Toggle original or enhanced image"
                disabled={!hasEnhancedOutput}
              >
                {showEnhanced ? 'UP' : 'OR'}
              </button>
              <button
                className="modal-icon-btn danger"
                onClick={(event) => {
                  event.stopPropagation();
                  void handleDeleteEnhancedOutput();
                }}
                title="Delete selected enhanced output only"
                aria-label="Delete selected enhanced output only"
                disabled={!selectedEnhancedJob}
              >
                UP-
              </button>
              <button
                className="modal-icon-btn"
                onClick={() => {
                  if (shouldConfirmImageDelete) setShowConfirmDelete(true);
                  else void handleDelete();
                }}
                title="Move to Recycle Bin"
                aria-label="Move image to Recycle Bin"
                disabled={isDeleting}
              >
                {isDeleting ? '...' : 'D'}
              </button>
              <button
                className={`modal-icon-btn ${filmstripOpen ? 'fav-active' : ''}`}
                onClick={toggleFilmstrip}
                title={`${filmstripOpen ? 'Hide' : 'Show'} filmstrip (${formatShortcutKey(keyBindings.toggleFilmstrip)})`}
                aria-label={`${filmstripOpen ? 'Hide' : 'Show'} image filmstrip`}
                aria-expanded={filmstripOpen}
                aria-controls="modal-filmstrip"
                aria-keyshortcuts={formatShortcutKey(keyBindings.toggleFilmstrip)}
              >
                FS
              </button>
              <button
                className="modal-icon-btn modal-metadata-toggle"
                onClick={() => setSidebarCollapsed((prev) => !prev)}
                title={sidebarCollapsed ? 'Show sidebar' : 'Hide sidebar'}
                aria-label={sidebarCollapsed ? 'Show metadata sidebar' : 'Hide metadata sidebar'}
                aria-expanded={!sidebarCollapsed}
                aria-controls="modal-metadata-sidebar"
              >
                {sidebarCollapsed
                  ? <ChevronLeft size={16} aria-hidden="true" />
                  : <ChevronRight size={16} aria-hidden="true" />}
              </button>
              <button ref={modalCloseButtonRef} className="modal-icon-btn close" onClick={close} title="Close" aria-label="Close image preview">
                <X size={16} aria-hidden="true" />
              </button>
            </div>
          </div>
          <div className="modal-displayed-asset-status" role="status" aria-live="polite">
            {displayedAssetStatus}
          </div>

          <div className="modal-main-column" data-testid="modal-main-column">
            <div
              className="modal-image-area"
              data-testid="modal-image-area"
              onWheel={handleWheel}
              onPointerDown={handlePointerDown}
              onPointerMove={handlePointerMove}
              onPointerUp={finishPointerGesture}
              onPointerCancel={cancelPointerGesture}
              onClick={handleImageAreaClick}
              onDoubleClick={handleImageAreaDoubleClick}
              onContextMenu={handleImageAreaContextMenu}
            >
            <div
              className="modal-edge-zone left"
              style={{ width: modalEdgeZoneWidth }}
              aria-hidden="true"
            >
              <span><ChevronLeft size={30} strokeWidth={1.9} /></span>
            </div>
            <div
              className="modal-edge-zone right"
              style={{ width: modalEdgeZoneWidth }}
              aria-hidden="true"
            >
              <span><ChevronRight size={30} strokeWidth={1.9} /></span>
            </div>
            {!fullImageReady && (
              <CachedImage
                key={`thumb-preview-${img.id}`}
                src={img.fileUrl}
                requestSrc={`${img.fileUrl}${img.fileUrl.includes('?') ? '&' : '?'}priority=visible`}
                fallbackSrc={img.fullUrl}
                cacheKind="thumb"
                alt={img.filename}
                className={`modal-main-image modal-thumb-preview is-thumb-preview ${swipeOffset.x !== 0 || swipeOffset.y !== 0 ? 'dragging' : ''}`}
                loading="eager"
                decoding="async"
                fetchPriority="high"
                onClick={handleImageClick}
                onDoubleClick={handleImageDoubleClick}
                style={{
                  transform: `translate(${pan.x + swipeOffset.x}px, ${pan.y + swipeOffset.y}px) scale(${zoom}) scaleX(${flipped ? -1 : 1})`,
                }}
                draggable={false}
              />
            )}
            <CachedImage
              key={fullImageKey}
              src={modalImageSrc}
              requestSrc={`${modalImageSrc}${modalImageSrc.includes('?') ? '&' : '?'}priority=focused`}
              fallbackSrc={img.fullUrl}
              cacheKind="display"
              alt={img.filename}
              className={`modal-main-image modal-full-image ${swipeOffset.x !== 0 || swipeOffset.y !== 0 ? 'dragging' : ''} ${fullImageReady ? 'is-full-ready' : 'is-full-loading'}`}
              loading="eager"
              decoding="async"
              fetchPriority="high"
              onClick={handleImageClick}
              onDoubleClick={handleImageDoubleClick}
              onError={(event) => {
                rememberFullImageFailed(fullImageKey);
              }}
              onSessionExpired={() => {
                rememberFullImageFailed(fullImageKey);
                reportImageSessionExpired();
              }}
              onLoad={(event) => {
                const currentSrc = event.currentTarget.currentSrc || event.currentTarget.src;
                if (currentSrc.startsWith('blob:') || currentSrc.includes(modalImageSrc) || currentSrc.includes(img.fullUrl)) {
                  rememberFullImageReady(fullImageKey);
                }
              }}
              style={{
                transform: `translate(${pan.x + swipeOffset.x}px, ${pan.y + swipeOffset.y}px) scale(${zoom}) scaleX(${flipped ? -1 : 1})`,
              }}
              draggable={false}
            />

              <div className="zoom-indicator">
                <span aria-label="Zoom level">{Math.round(zoom * 100)}%</span>
                <button className="zoom-reset" onClick={resetZoom} title="Reset zoom" aria-label="Reset zoom">
                  <X size={14} aria-hidden="true" />
                </button>
              </div>

              {favoriteFeedback && (
                <div className="modal-favorite-feedback" key={favoriteFeedback.token} aria-live="polite">
                  <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21.23l8.84-8.84a5.5 5.5 0 0 0 0-7.78z" />
                  </svg>
                  <span>{favoriteFeedback.level > 0 ? favoriteFeedback.level : 'OFF'}</span>
                </div>
              )}

              {contextMenu && (
                <div
                  ref={contextMenuRef}
                  className="modal-context-menu"
                  role="menu"
                  aria-label="Image actions"
                  style={{ left: contextMenu.x, top: contextMenu.y }}
                  onClick={(event) => event.stopPropagation()}
                  onContextMenu={(event) => event.preventDefault()}
                  onKeyDown={handleContextMenuKeyDown}
                >
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); increaseFavorite(); }}>
                    Favorite +1
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); decreaseFavorite(); }}>
                    Favorite -1
                  </button>
                  <button
                    type="button"
                    role="menuitem"
                    disabled={!hasEnhancedOutput}
                    onClick={() => { setContextMenu(null); toggleEnhancedView(); }}
                  >
                    {showEnhanced ? 'Show Original image' : 'Show Enhanced image'}
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); void resolveDisplayedAsset(true); }}>
                    Open displayed image externally
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); openPicker([img.id]); }}>
                    Add image to Album
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); toggleFilmstrip(); }}>
                    {filmstripOpen ? 'Hide Filmstrip' : 'Show Filmstrip'}
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); setSidebarCollapsed((prev) => !prev); }}>
                    {sidebarCollapsed ? 'Show metadata' : 'Hide metadata'}
                  </button>
                  <button type="button" role="menuitem" onClick={() => { setContextMenu(null); resetZoom(); }}>
                    Reset zoom
                  </button>
                  <div className="modal-context-menu-separator" role="separator" />
                  <button
                    type="button"
                    role="menuitem"
                    className="danger"
                    disabled={isDeleting}
                    onClick={() => {
                      setContextMenu(null);
                      setShowConfirmDelete(true);
                    }}
                  >
                    Move image to Recycle Bin…
                  </button>
                </div>
              )}
            </div>

            {filmstripInLayout && (
              <ModalFilmstrip
                total={filmstripTotal}
                activeIndex={filmstripActiveIndex}
                getItem={getFilmstripItem}
                onNeedRange={requestFilmstripRange}
                onSelect={selectFilmstripItem}
                onNavigate={(intent) => {
                  if (intent === 'prev') goPrev();
                  else goNext();
                }}
                onCollapse={toggleFilmstrip}
                onSessionExpired={reportImageSessionExpired}
                toggleShortcut={formatShortcutKey(keyBindings.toggleFilmstrip)}
                presentation="layout"
              />
            )}

            {filmstripOverlayVisible && (
              <ModalFilmstrip
                total={filmstripTotal}
                activeIndex={filmstripActiveIndex}
                getItem={getFilmstripItem}
                onNeedRange={requestFilmstripRange}
                onSelect={selectFilmstripItem}
                onNavigate={(intent) => {
                  if (intent === 'prev') goPrev();
                  else goNext();
                }}
                onCollapse={() => setFilmstripHoverVisible(false)}
                onSessionExpired={reportImageSessionExpired}
                toggleShortcut={formatShortcutKey(keyBindings.toggleFilmstrip)}
                presentation="overlay"
              />
            )}
          </div>

          {isMobileSheet && !sidebarCollapsed && (
            <button type="button" className="modal-sheet-backdrop" aria-label="Close metadata sidebar" onClick={() => setSidebarCollapsed(true)} />
          )}
          <aside
            ref={metadataSidebarRef}
            id="modal-metadata-sidebar"
            className={`modal-sidebar ${sidebarCollapsed ? 'hidden' : ''}`}
            role={isMobileSheet ? 'dialog' : undefined}
            aria-modal={isMobileSheet || undefined}
            aria-label="Image metadata"
            tabIndex={isMobileSheet ? -1 : undefined}
          >
            <MetadataTabList
              activeTab={sidebarTab}
              onActiveTabChange={setSidebarTab}
              panelId="modal-metadata-panel"
            />

            <div className="sidebar-content">
              <div
                id="modal-metadata-panel"
                role="tabpanel"
                aria-labelledby={`modal-metadata-panel-tab-${sidebarTab}`}
              >
              {sidebarTab === 'prompt' && (
                <div className="meta-section">
                  <div className="meta-header">
                    <span className="meta-label">Prompt</span>
                    <button
                      className="copy-btn"
                      onClick={() => void copyToClipboard('prompt', prompt)}
                      title="Copy prompt metadata"
                      aria-label="Copy prompt metadata"
                    >
                      {clipboardButtonLabel('prompt')}
                    </button>
                  </div>
                  {promptTags.length > 0 ? (
                    <div className="prompt-tag-list" aria-label="Prompt tags">
                      {promptTags.map((tag) => (
                        <button
                          key={tag}
                          className="prompt-tag"
                          onClick={() => addPromptTagToSearch(tag)}
                          title={`Add "${tag}" to search`}
                        >
                          {tag}
                        </button>
                      ))}
                    </div>
                  ) : (
                    <p className="meta-text">{prompt || 'No prompt metadata.'}</p>
                  )}
                </div>
              )}

              {sidebarTab === 'negative' && (
                <div className="meta-section">
                  <div className="meta-header">
                    <span className="meta-label">Negative Prompt</span>
                    <button
                      className="copy-btn"
                      onClick={() => void copyToClipboard('negative', negative)}
                      title="Copy negative prompt metadata"
                      aria-label="Copy negative prompt metadata"
                    >
                      {clipboardButtonLabel('negative')}
                    </button>
                  </div>
                  <p className="meta-text">{negative || 'No negative prompt metadata.'}</p>
                </div>
              )}

              {sidebarTab === 'settings' && (
                <div className="meta-section">
                  <span className="meta-label">Generation Settings</span>
                  <code className="meta-code">{settingsRaw || 'No settings metadata.'}</code>
                </div>
              )}
              </div>

              <div className="meta-section png-metadata-section">
                <div className="meta-header">
                  <span className="meta-label">PNG Info</span>
                  <button
                    className="copy-btn"
                    onClick={() => void copyToClipboard('png', pngMetadataCopyText)}
                    disabled={pngMetadataRows.length === 0}
                    title="Copy PNG metadata"
                    aria-label="Copy PNG metadata"
                  >
                    {clipboardButtonLabel('png')}
                  </button>
                </div>
                {pngMetadataRows.length > 0 ? (
                  <table className="png-metadata-table">
                    <tbody>
                      {pngMetadataRows.map((row, index) => (
                        <tr key={`${row.label}-${index}`}>
                          <th scope="row">{row.label}</th>
                          <td>{row.value}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                ) : (
                  <p className="meta-text">No PNG metadata.</p>
                )}
              </div>
            </div>

            <div className="sidebar-footer">
              <kbd>{keyBindings.prevImage}</kbd> <kbd>{keyBindings.nextImage}</kbd> Next/Prev
              {' | '}
              <kbd>{formatShortcutKey(keyBindings.toggleFavorite)}</kbd> Favorite
              {' | '}
              <kbd>{formatShortcutKey(keyBindings.decreaseFavorite)}</kbd> Favorite -1
              {' | '}
              <kbd>{formatShortcutKey(keyBindings.flipHorizontal)}</kbd> Flip
              {' | '}
              <kbd>{formatShortcutKey(keyBindings.enhanceImage)}</kbd> AI
              {' | '}
              <kbd>{formatShortcutKey(keyBindings.toggleFilmstrip)}</kbd> Filmstrip
              {' | '}
              <kbd>E</kbd> Original/Upscaled
              {' | '}
              <kbd>Del</kbd> Recycle
            </div>
          </aside>
        </div>
      </div>

      {showConfirmDelete && (
        <div className="confirm-overlay">
          <div className="confirm-backdrop" aria-hidden="true" onClick={() => setShowConfirmDelete(false)} />
          <div ref={confirmPanelRef} className="confirm-panel" role="alertdialog" aria-modal="true" aria-labelledby="image-delete-title" tabIndex={-1}>
            <h3 id="image-delete-title">Move this image to Recycle Bin?</h3>
            <p>{img.filename}</p>
            {isFav ? (
              <p role="status">This image is favorite level {favLevel}. Favorite images always require confirmation.</p>
            ) : (
              <label className="sidebar-toggle" style={{ justifyContent: 'center', marginBottom: '1rem' }}>
                <input
                  type="checkbox"
                  checked={!confirmBeforeDelete}
                  onChange={(e) => setConfirmBeforeDelete(!e.target.checked)}
                />
                <span>Do not ask again</span>
              </label>
            )}
            <div className="confirm-actions">
              <button ref={confirmCancelButtonRef} className="btn-cancel" onClick={() => setShowConfirmDelete(false)}>Cancel</button>
              <button className="btn-danger" onClick={() => void handleDelete(true)} disabled={isDeleting}>
                {isDeleting ? 'Moving...' : 'Move to Recycle Bin'}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

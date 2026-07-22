'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import CachedImage from './CachedImage';
import { EnhanceSettingsControls, createEnhancementJob, getEnhancementSettings } from './EnhanceQueuePanel';
import { useDialogFocus } from '../lib/useDialogFocus';
import { getFavoriteDeleteProtection, shouldConfirmSourceDelete } from '../lib/favoriteDeleteProtection';
import { formatBulkRecycleProgress, recycleImagesSequentially } from '../lib/bulkRecycle';

const MIN_PANEL_WIDTH = 240;
const MAX_PANEL_WIDTH = 900;
const PANEL_WIDTH_STEP = 20;
const PANEL_WIDTH_LARGE_STEP = 100;

function clampPanelWidth(width: number) {
  return Math.max(MIN_PANEL_WIDTH, Math.min(MAX_PANEL_WIDTH, width));
}

interface PreviewResizeHandleProps {
  width: number;
  onPointerDown: (event: React.PointerEvent<HTMLDivElement>) => void;
  onMouseDown: (event: React.MouseEvent<HTMLDivElement>) => void;
  onKeyDown: (event: React.KeyboardEvent<HTMLDivElement>) => void;
}

function PreviewResizeHandle({ width, onPointerDown, onMouseDown, onKeyDown }: PreviewResizeHandleProps) {
  return (
    <div
      className="preview-resize-handle"
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize preview panel"
      aria-valuemin={MIN_PANEL_WIDTH}
      aria-valuemax={MAX_PANEL_WIDTH}
      aria-valuenow={width}
      aria-valuetext={`${width} pixels`}
      tabIndex={0}
      onPointerDown={onPointerDown}
      onMouseDown={onMouseDown}
      onKeyDown={onKeyDown}
    />
  );
}

function formatDateTime(value?: number): string {
  if (!value) return '-';
  return new Date(value).toLocaleString();
}

function formatOpenExternalResult(succeeded: number, total: number) {
  const failed = Math.max(0, total - succeeded);
  return `Opened ${succeeded}/${total} image(s) in external viewer.${failed > 0 ? ` ${failed} failed.` : ''}`;
}

export default function RightPreviewPanel() {
  const {
    previewTabIds,
    activePreviewId,
    previewById,
    cycleFavoriteLevel,
    decreaseFavoriteLevel,
    setFavoriteLevels,
    adjustFavoriteLevels,
    favorites,
    openExternal,
    selectedIds,
    searchResults: catalogSearchResults,
    deleteImage: deleteCatalogImage,
    view,
    setView,
    confirmBeforeDelete,
    setConfirmBeforeDelete,
    indexToken: catalogIndexToken,
  } = useImageStore();
  const { activeSource, recycleSource } = useOptionalAlbumStore();
  const searchResults = activeSource ? activeSource.images : catalogSearchResults;
  const deleteImage = activeSource ? recycleSource : deleteCatalogImage;
  const indexToken = activeSource?.sourceToken ?? catalogIndexToken;

  const [confirmBulkDelete, setConfirmBulkDelete] = useState(false);
  const [bulkDeleteFavoriteCount, setBulkDeleteFavoriteCount] = useState(0);
  const confirmPanelRef = useRef<HTMLDivElement>(null);
  const confirmCancelButtonRef = useRef<HTMLButtonElement>(null);
  const [bulkMessage, setBulkMessage] = useState('');
  const [enhanceMessage, setEnhanceMessage] = useState('');
  const [showDetails, setShowDetails] = useState(false);
  const selectedCount = selectedIds.length;
  const favoriteTargets = useMemo(() => {
    const availableIds = new Set((searchResults ?? []).flatMap((image) => image ? [image.id] : []));
    return [...new Set(selectedIds.filter((id) => availableIds.has(id)))];
  }, [searchResults, selectedIds]);
  const ignoredSelectionCount = selectedCount - favoriteTargets.length;
  const favoriteLevels = [...new Set(favoriteTargets.map((id) => favorites[id] ?? 0))];
  const favoriteSelectionState = favoriteTargets.length === 0
    ? selectedCount > 0 ? 'Selected images are no longer in the current result.' : 'No selection'
    : favoriteLevels.length === 1
      ? `Lv${favoriteLevels[0]} for ${favoriteTargets.length} selected`
      : `Mixed levels (${favoriteLevels.sort((a, b) => a - b).map((level) => `Lv${level}`).join(', ')}) for ${favoriteTargets.length} selected`;

  useDialogFocus({
    open: confirmBulkDelete,
    dialogRef: confirmPanelRef,
    initialFocusRef: confirmCancelButtonRef,
    onEscape: () => setConfirmBulkDelete(false),
  });

  const [panelWidth, setPanelWidth] = useState(() => clampPanelWidth(view.rightPanelWidth ?? 320));
  const isResizing = useRef(false);
  const dragStartX = useRef(0);
  const dragStartWidth = useRef(0);
  const widthRef = useRef(panelWidth);

  useEffect(() => {
    widthRef.current = panelWidth;
  }, [panelWidth]);

  const setWidth = useCallback((nextWidth: number) => {
    const next = clampPanelWidth(nextWidth);
    widthRef.current = next;
    setPanelWidth(next);
    return next;
  }, []);

  const beginResize = (clientX: number) => {
    isResizing.current = true;
    dragStartX.current = clientX;
    dragStartWidth.current = widthRef.current;
  };

  const onHandlePointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    beginResize(event.clientX);
    event.currentTarget.setPointerCapture?.(event.pointerId);
    event.preventDefault();
  };

  const onHandleMouseDown = (event: React.MouseEvent<HTMLDivElement>) => {
    if (event.button !== 0 || isResizing.current) return;
    beginResize(event.clientX);
    event.preventDefault();
  };

  const onHandleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const step = event.shiftKey ? PANEL_WIDTH_LARGE_STEP : PANEL_WIDTH_STEP;
    let nextWidth: number | null = null;
    if (event.key === 'ArrowLeft') nextWidth = panelWidth + step;
    else if (event.key === 'ArrowRight') nextWidth = panelWidth - step;
    else if (event.key === 'Home') nextWidth = MIN_PANEL_WIDTH;
    else if (event.key === 'End') nextWidth = MAX_PANEL_WIDTH;
    if (nextWidth === null) return;

    event.preventDefault();
    setView({ rightPanelWidth: setWidth(nextWidth) });
  };

  useEffect(() => {
    const onMove = (clientX: number) => {
      if (!isResizing.current) return;
      const delta = dragStartX.current - clientX;
      setWidth(dragStartWidth.current + delta);
    };

    const onUp = () => {
      if (!isResizing.current) return;
      isResizing.current = false;
      setView({ rightPanelWidth: widthRef.current });
    };

    const onMouseMove = (event: MouseEvent) => onMove(event.clientX);
    const onPointerMove = (event: PointerEvent) => onMove(event.clientX);
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('mouseup', onUp);
    document.addEventListener('pointerup', onUp);
    return () => {
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('pointermove', onPointerMove);
      document.removeEventListener('mouseup', onUp);
      document.removeEventListener('pointerup', onUp);
    };
  }, [setView, setWidth]);

  const handleSetFavoriteSelected = (level: number) => {
    if (favoriteTargets.length === 0) return;
    setFavoriteLevels(favoriteTargets, level);
    setBulkMessage(level === 0
      ? `Cleared favorite level for ${favoriteTargets.length} image(s).`
      : `Set favorite level ${level} for ${favoriteTargets.length} image(s).`);
  };

  const handleAdjustFavoriteSelected = (delta: number) => {
    if (favoriteTargets.length === 0) return;
    adjustFavoriteLevels(favoriteTargets, delta);
    setBulkMessage(`${delta > 0 ? 'Increased' : 'Decreased'} favorite level for ${favoriteTargets.length} image(s).`);
  };

  const handleOpenSelected = async () => {
    if (selectedCount === 0) return;
    let succeeded = 0;
    for (const id of selectedIds) {
      if (await openExternal(id, { indexToken })) succeeded += 1;
    }
    setBulkMessage(formatOpenExternalResult(succeeded, selectedCount));
  };

  const handleOpenActive = async (id: string) => {
    const succeeded = await openExternal(id, { indexToken });
    setBulkMessage(formatOpenExternalResult(succeeded ? 1 : 0, 1));
  };

  const enhanceOne = async (id: string) => {
    try {
      setView({ enhanceQueueOpen: true });
      const job = await createEnhancementJob(id, getEnhancementSettings(), indexToken);
      setEnhanceMessage(`Enhance queued: ${job.id.slice(0, 8)}`);
    } catch (err) {
      setEnhanceMessage(err instanceof Error ? err.message : String(err));
    }
  };

  const enhanceSelected = async () => {
    if (selectedCount === 0) return;
    let queued = 0;
    setView({ enhanceQueueOpen: true });
    for (const id of selectedIds) {
      try {
        await createEnhancementJob(id, getEnhancementSettings(), indexToken);
        queued += 1;
      } catch (err) {
        setEnhanceMessage(`Queued ${queued}/${selectedCount}. ${err instanceof Error ? err.message : String(err)}`);
        return;
      }
    }
    setEnhanceMessage(`Enhance queued for ${queued} image(s).`);
  };

  const executeBulkDelete = async (favoriteConfirmed = false) => {
    if (selectedCount === 0) return;
    const targets = [...selectedIds];
    const result = await recycleImagesSequentially(targets, (id) => (
      deleteImage(id, { favoriteConfirmed })
    ), (progress) => {
      setBulkMessage(formatBulkRecycleProgress(progress));
    });
    setConfirmBulkDelete(false);
    setBulkDeleteFavoriteCount(0);
    setBulkMessage(formatBulkRecycleProgress(result));
  };

  const handleBulkDeleteRequest = () => {
    if (selectedCount === 0) return;
    const protection = getFavoriteDeleteProtection(selectedIds, favorites);
    setBulkDeleteFavoriteCount(protection.favoriteCount);
    if (shouldConfirmSourceDelete(confirmBeforeDelete, protection)) setConfirmBulkDelete(true);
    else void executeBulkDelete();
  };

  const panelStyle = { width: panelWidth, minWidth: panelWidth, maxWidth: panelWidth };
  const resizeHandle = (
    <PreviewResizeHandle
      width={panelWidth}
      onPointerDown={onHandlePointerDown}
      onMouseDown={onHandleMouseDown}
      onKeyDown={onHandleKeyDown}
    />
  );

  const activeId = activePreviewId;
  const active = activeId ? previewById[activeId] : null;
  const previewSrc = active ? (active.displayUrl || active.fileUrl) : null;
  const activeIsOutsideCurrentSearch = Boolean(
    activeId
    && active
    && !searchResults.some((image) => image?.id === activeId)
  );

  if (!view.rightPanelOpen) return null;

  if (previewTabIds.length === 0 && !activePreviewId) {
    return (
      <aside className="preview-panel empty" style={panelStyle}>
        {resizeHandle}
        <div className="preview-empty">
          <p>Click an image to open preview.</p>
          <p>Use Ctrl/Shift for multi-select.</p>
        </div>
      </aside>
    );
  }

  return (
    <>
      <aside className="preview-panel" style={panelStyle}>
        {resizeHandle}

        <div className="bulk-toolbar">
          <span className="bulk-count">{selectedCount > 0 ? `${selectedCount} selected` : 'No selection'}</span>
          <div className="bulk-favorite-controls" role="group" aria-label="Favorite level for selected images">
            <span className="bulk-favorite-state" role="status" aria-live="polite">{favoriteSelectionState}</span>
            <button className="pill" onClick={() => handleAdjustFavoriteSelected(-1)} disabled={favoriteTargets.length === 0} aria-label="Decrease favorite level for selected images">Lv −</button>
            <button className="pill" onClick={() => handleSetFavoriteSelected(0)} disabled={favoriteTargets.length === 0}>Clear</button>
            {[1, 2, 3, 4, 5].map((level) => (
              <button key={level} className="pill" onClick={() => handleSetFavoriteSelected(level)} disabled={favoriteTargets.length === 0} aria-label={`Set selected images to favorite level ${level}`}>Lv{level}</button>
            ))}
            <button className="pill" onClick={() => handleAdjustFavoriteSelected(1)} disabled={favoriteTargets.length === 0} aria-label="Increase favorite level for selected images">Lv +</button>
          </div>
          {ignoredSelectionCount > 0 && <span className="bulk-stale-selection">{ignoredSelectionCount} unavailable</span>}
          <button className="pill" onClick={() => void handleOpenSelected()} disabled={selectedCount === 0}>Open</button>
          <button className="pill" onClick={() => void enhanceSelected()} disabled={selectedCount === 0}>Enhance selected</button>
          <button className="pill danger" onClick={handleBulkDeleteRequest} disabled={selectedCount === 0}>Recycle</button>
        </div>
        {bulkMessage && <div className="bulk-message">{bulkMessage}</div>}
        {enhanceMessage && <div className="bulk-message">{enhanceMessage}</div>}
        <EnhanceSettingsControls />

        {!active ? (
          <div className="preview-empty">
            <p>Click an image to preview it, or choose a saved tab below.</p>
          </div>
        ) : (
          <div className="preview-content">
            {activeIsOutsideCurrentSearch && (
              <div
                className="preview-context-status"
                role="status"
                aria-live="polite"
                aria-atomic="true"
                aria-label="Preview search availability"
              >
                <strong>Outside current search/filter</strong>
                <span>Modal navigation is unavailable until filters change to include this image.</span>
              </div>
            )}
            <div className="preview-main">
              <div className="preview-image-wrap">
                <CachedImage
                  key={active.id}
                  src={previewSrc ?? active.fileUrl}
                  requestSrc={`${previewSrc ?? active.fileUrl}${(previewSrc ?? active.fileUrl).includes('?') ? '&' : '?'}priority=visible`}
                  fallbackSrc={active.fullUrl}
                  cacheKind="display"
                  alt={active.filename}
                  className="preview-image"
                  loading="eager"
                  decoding="async"
                  fetchPriority="high"
                />
              </div>

              <div className="preview-actions">
                <button className={`pill ${(favorites[active.id] ?? 0) > 0 ? 'active' : ''}`} onClick={() => cycleFavoriteLevel(active.id)}>
                  Favorite +1 {favorites[active.id] ? `(${favorites[active.id]})` : ''}
                </button>
                <button className="pill" onClick={() => decreaseFavoriteLevel(active.id)}>Favorite -1</button>
                <button className="pill" onClick={() => void handleOpenActive(active.id)}>Open External</button>
                <button className="pill" onClick={() => void enhanceOne(active.id)}>Enhance</button>
                <button className={`pill ${showDetails ? 'active' : ''}`} onClick={() => setShowDetails((v) => !v)}>
                  {showDetails ? 'Hide Details' : 'Show Details'}
                </button>
              </div>
            </div>

            {showDetails && (
              <div className="preview-details-pane">
                <div className="preview-meta">
                  <div className="preview-meta-row">
                    <span className="preview-meta-label">Path</span>
                    <span className="preview-meta-value" title={active.id}>{active.id}</span>
                  </div>
                  <div className="preview-meta-row">
                    <span className="preview-meta-label">Created</span>
                    <span className="preview-meta-value">{formatDateTime(active.createdAt)}</span>
                  </div>
                  <div className="preview-meta-row">
                    <span className="preview-meta-label">Modified</span>
                    <span className="preview-meta-value">{formatDateTime(active.mtime)}</span>
                  </div>
                  <div className="preview-meta-block">
                    <div className="preview-meta-label">Prompt</div>
                    <div className="preview-meta-text">{active.metadata?.prompt || '-'}</div>
                  </div>
                  <div className="preview-meta-block">
                    <div className="preview-meta-label">Negative Prompt</div>
                    <div className="preview-meta-text">{active.metadata?.negativePrompt || '-'}</div>
                  </div>
                </div>
              </div>
            )}
          </div>
        )}
      </aside>

      {confirmBulkDelete && (
        <div className="confirm-overlay">
          <div className="confirm-backdrop" aria-hidden="true" onClick={() => {
            setConfirmBulkDelete(false);
            setBulkDeleteFavoriteCount(0);
          }} />
          <div ref={confirmPanelRef} className="confirm-panel" role="alertdialog" aria-modal="true" aria-labelledby="preview-bulk-delete-title" tabIndex={-1}>
            <h3 id="preview-bulk-delete-title">Move selected images to Recycle Bin?</h3>
            <p>{selectedCount} image(s) will be moved to Recycle Bin.</p>
            {bulkDeleteFavoriteCount > 0 ? (
              <p role="status">
                {bulkDeleteFavoriteCount} favorite image(s) are included. Favorite images always require confirmation.
              </p>
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
              <button ref={confirmCancelButtonRef} className="btn-cancel" onClick={() => {
                setConfirmBulkDelete(false);
                setBulkDeleteFavoriteCount(0);
              }}>Cancel</button>
              <button className="btn-danger" onClick={() => void executeBulkDelete(true)}>Move to Recycle Bin</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

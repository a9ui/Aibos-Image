'use client';

import React, { useEffect, useRef, useState } from 'react';
import { RotateCcw, Pin, PinOff, X } from 'lucide-react';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import CachedImage from './CachedImage';

interface HoverPreviewState {
  id: string;
  left: number;
  top: number;
}

type PreviewTabDropPosition = 'before' | 'after';

export default function BottomPreviewTabs() {
  const {
    previewTabIds,
    activePreviewId,
    previewById,
    searchResults: catalogSearchResults,
    pinnedPreviewIds,
    setActivePreviewId,
    setSelectedIndex,
    closePreviewTab,
    reorderPreviewTab,
    togglePinPreviewTab,
    closedPreviewTabCount,
    restoreLastClosedPreview,
  } = useImageStore();
  const { activeSource } = useOptionalAlbumStore();
  const searchResults = activeSource ? activeSource.images : catalogSearchResults;

  const [hoverPreview, setHoverPreview] = useState<HoverPreviewState | null>(null);
  const [draggedTabId, setDraggedTabId] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<{ id: string; position: PreviewTabDropPosition } | null>(null);
  const [focusedTabId, setFocusedTabId] = useState<string | null>(
    () => activePreviewId ?? previewTabIds[0] ?? null
  );
  const tabRefs = useRef<Record<string, HTMLButtonElement | null>>({});
  const restoreButtonRef = useRef<HTMLButtonElement>(null);
  const previousTabCountRef = useRef(previewTabIds.length);
  const focusRestoredTabRef = useRef(false);

  const activeId = activePreviewId;
  const hoveredImage = hoverPreview ? previewById[hoverPreview.id] : null;

  useEffect(() => {
    setFocusedTabId((current) => (
      current && previewTabIds.includes(current)
        ? current
        : activePreviewId && previewTabIds.includes(activePreviewId)
          ? activePreviewId
          : previewTabIds[0] ?? null
    ));
  }, [activePreviewId, previewTabIds]);

  useEffect(() => {
    const previouslyHadTabs = previousTabCountRef.current > 0;
    previousTabCountRef.current = previewTabIds.length;
    if (previouslyHadTabs && previewTabIds.length === 0 && closedPreviewTabCount > 0) {
      restoreButtonRef.current?.focus();
      return;
    }
    if (focusRestoredTabRef.current && previewTabIds.length > 0) {
      const nextId = activePreviewId ?? previewTabIds[0];
      tabRefs.current[nextId]?.focus();
      focusRestoredTabRef.current = false;
    }
  }, [activePreviewId, closedPreviewTabCount, previewTabIds]);

  const restoreRecentPreview = () => {
    focusRestoredTabRef.current = true;
    restoreLastClosedPreview();
  };

  if (previewTabIds.length === 0) {
    if (closedPreviewTabCount === 0) return null;
    return (
      <div className="bottom-preview-tabs bottom-preview-restore-surface" role="region" aria-label="Recently closed preview tabs">
        <button
          ref={restoreButtonRef}
          className="bottom-preview-action-btn"
          type="button"
          onClick={restoreRecentPreview}
          aria-keyshortcuts="Control+Shift+T Meta+Shift+T"
          aria-label={`Restore last closed preview tab. ${closedPreviewTabCount} recently closed tab${closedPreviewTabCount === 1 ? '' : 's'} available.`}
          title={`Restore recently closed tab (${closedPreviewTabCount} available)`}
        >
          <RotateCcw size={14} aria-hidden="true" />
          Restore
        </button>
        <span className="bottom-preview-restore-count" aria-hidden="true">{closedPreviewTabCount}</span>
      </div>
    );
  }

  const showHoverPreview = (
    event: React.MouseEvent<HTMLElement>,
    id: string
  ) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const width = 220;
    const height = 280;
    const left = Math.min(
      Math.max(12, rect.left),
      Math.max(12, window.innerWidth - width - 12)
    );
    const top = Math.max(12, rect.top - height - 12);
    setHoverPreview({ id, left, top });
  };

  const moveTabFocus = (id: string, direction: 'next' | 'previous' | 'first' | 'last') => {
    const currentIndex = previewTabIds.indexOf(id);
    if (currentIndex < 0) return;
    const nextIndex = direction === 'first'
      ? 0
      : direction === 'last'
        ? previewTabIds.length - 1
        : (currentIndex + (direction === 'next' ? 1 : -1) + previewTabIds.length) % previewTabIds.length;
    const nextId = previewTabIds[nextIndex];
    setFocusedTabId(nextId);
    tabRefs.current[nextId]?.focus();
  };

  const reorderFromDrop = (id: string, targetId: string, position: PreviewTabDropPosition) => {
    const sourceIndex = previewTabIds.indexOf(id);
    const targetIndex = previewTabIds.indexOf(targetId);
    if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) return;
    let destinationIndex = position === 'before' ? targetIndex : targetIndex + 1;
    if (sourceIndex < destinationIndex) destinationIndex -= 1;
    reorderPreviewTab(id, destinationIndex);
  };

  const moveTabWithKeyboard = (id: string, direction: 'previous' | 'next') => {
    const sourceIndex = previewTabIds.indexOf(id);
    if (sourceIndex < 0) return;
    const destinationIndex = sourceIndex + (direction === 'previous' ? -1 : 1);
    if (destinationIndex < 0 || destinationIndex >= previewTabIds.length) return;
    reorderPreviewTab(id, destinationIndex);
    setFocusedTabId(id);
    window.requestAnimationFrame(() => tabRefs.current[id]?.focus());
  };

  const getDropPosition = (event: React.DragEvent<HTMLDivElement>): PreviewTabDropPosition => {
    const bounds = event.currentTarget.getBoundingClientRect();
    return event.clientX < bounds.left + bounds.width / 2 ? 'before' : 'after';
  };

  return (
    <div className="bottom-preview-tabs">
      {closedPreviewTabCount > 0 && (
        <div className="bottom-preview-actions">
          <button
            className="bottom-preview-action-btn"
            type="button"
            onClick={restoreRecentPreview}
            aria-keyshortcuts="Control+Shift+T Meta+Shift+T"
            aria-label={`Restore last closed preview tab. ${closedPreviewTabCount} recently closed tab${closedPreviewTabCount === 1 ? '' : 's'} available.`}
            title={`Restore recently closed tab (${closedPreviewTabCount} available)`}
          >
            <RotateCcw size={14} aria-hidden="true" />
            Restore
          </button>
        </div>
      )}
      <div className="bottom-preview-tabs-scroll" role="tablist" aria-label="Open preview tabs">
        {previewTabIds.map((id) => {
          const img = previewById[id];
          const label = img?.filename ?? id.split(/[/\\]/).pop() ?? id;
          const isActive = id === activeId;
          const isPinned = pinnedPreviewIds.includes(id);
          const openFullView = () => {
            setFocusedTabId(id);
            setActivePreviewId(id);
            const idx = searchResults.findIndex((entry) => entry?.id === id);
            if (idx >= 0) setSelectedIndex(idx);
          };
          const closeByMiddleClick = (event: React.MouseEvent<HTMLButtonElement>) => {
            if (event.button !== 1) return;
            event.preventDefault();
            event.stopPropagation();
            closePreviewTab(id);
          };
          const onTabKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
            if (event.altKey && event.shiftKey && event.key === 'ArrowRight') {
              event.preventDefault();
              moveTabWithKeyboard(id, 'next');
            } else if (event.altKey && event.shiftKey && event.key === 'ArrowLeft') {
              event.preventDefault();
              moveTabWithKeyboard(id, 'previous');
            } else if (event.key === 'ArrowRight') {
              event.preventDefault();
              moveTabFocus(id, 'next');
            } else if (event.key === 'ArrowLeft') {
              event.preventDefault();
              moveTabFocus(id, 'previous');
            } else if (event.key === 'Home') {
              event.preventDefault();
              moveTabFocus(id, 'first');
            } else if (event.key === 'End') {
              event.preventDefault();
              moveTabFocus(id, 'last');
            } else if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              openFullView();
            }
          };

          return (
            <div
              key={id}
              data-testid={`bottom-preview-tab-${id}`}
              data-preview-tab-id={id}
              className={`bottom-preview-tab ${isActive ? 'active' : ''} ${draggedTabId === id ? 'is-dragging' : ''} ${dropTarget?.id === id ? `is-drop-${dropTarget.position}` : ''}`}
              draggable
              onDragStart={(event) => {
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', id);
                setDraggedTabId(id);
                setDropTarget(null);
              }}
              onDragOver={(event) => {
                if (!draggedTabId || draggedTabId === id) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                setDropTarget({
                  id,
                  position: getDropPosition(event),
                });
              }}
              onDrop={(event) => {
                event.preventDefault();
                const sourceId = draggedTabId || event.dataTransfer.getData('text/plain');
                const position = dropTarget?.id === id ? dropTarget.position : getDropPosition(event);
                if (sourceId) reorderFromDrop(sourceId, id, position);
                setDraggedTabId(null);
                setDropTarget(null);
              }}
              onDragEnd={() => {
                setDraggedTabId(null);
                setDropTarget(null);
              }}
              onMouseEnter={(event) => showHoverPreview(event, id)}
              onMouseMove={(event) => showHoverPreview(event, id)}
              onMouseLeave={() => setHoverPreview((prev) => (prev?.id === id ? null : prev))}
            >
              <button
                ref={(element) => { tabRefs.current[id] = element; }}
                className="bottom-preview-tab-main"
                type="button"
                role="tab"
                aria-selected={isActive}
                aria-label={`Open preview ${label}`}
                aria-keyshortcuts="Alt+Shift+ArrowLeft Alt+Shift+ArrowRight"
                aria-posinset={previewTabIds.indexOf(id) + 1}
                aria-setsize={previewTabIds.length}
                tabIndex={focusedTabId === id ? 0 : -1}
                onClick={openFullView}
                onAuxClick={closeByMiddleClick}
                onFocus={() => setFocusedTabId(id)}
                onKeyDown={onTabKeyDown}
                title={`${label} — drag to reorder, or Alt+Shift+Left/Right`}
              >
                <span className="bottom-preview-tab-label">{label}</span>
              </button>
              <button
                className={`bottom-preview-tab-pin ${isPinned ? 'active' : ''}`}
                onClick={(event) => {
                  event.stopPropagation();
                  togglePinPreviewTab(id);
                }}
                title={isPinned ? 'Unpin tab' : 'Pin tab'}
                aria-label={`${isPinned ? 'Unpin' : 'Pin'} preview ${label}`}
                type="button"
              >
                {isPinned ? <PinOff size={13} aria-hidden="true" /> : <Pin size={13} aria-hidden="true" />}
              </button>
              <button
                className="bottom-preview-tab-close"
                onClick={(event) => {
                  event.stopPropagation();
                  closePreviewTab(id);
                }}
                aria-label={`Close preview ${label}`}
                title={`Close ${label}`}
                type="button"
              >
                <X size={14} aria-hidden="true" />
              </button>
            </div>
          );
        })}
      </div>
      {hoveredImage && hoverPreview && (
        <div
          className="bottom-preview-floating-hover"
          style={{ left: hoverPreview.left, top: hoverPreview.top }}
        >
          <div className="bottom-preview-floating-thumb">
            <CachedImage
              src={hoveredImage.fileUrl}
              requestSrc={`${hoveredImage.fileUrl}${hoveredImage.fileUrl.includes('?') ? '&' : '?'}priority=visible`}
              fallbackSrc={hoveredImage.fullUrl}
              cacheKind="thumb"
              alt={hoveredImage.filename}
            />
          </div>
          <div className="bottom-preview-floating-meta">
            <div className="bottom-preview-floating-name">{hoveredImage.filename}</div>
            <div className="bottom-preview-floating-path" title={hoveredImage.id}>{hoveredImage.id}</div>
          </div>
        </div>
      )}
    </div>
  );
}

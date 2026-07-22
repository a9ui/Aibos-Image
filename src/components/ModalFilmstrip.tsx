'use client';

import React, { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ImageFile } from '../lib/types';
import CachedImage from './CachedImage';

const FILMSTRIP_ITEM_WIDTH = 64;
const FILMSTRIP_ITEM_HEIGHT = 92;
const FILMSTRIP_ITEM_GAP = 8;
const FILMSTRIP_ITEM_PITCH = FILMSTRIP_ITEM_WIDTH + FILMSTRIP_ITEM_GAP;
const FILMSTRIP_OVERSCAN_ITEMS = 8;
const FILMSTRIP_FALLBACK_VIEWPORT_WIDTH = 1280;

export interface ModalFilmstripItem {
  image: ImageFile;
  sourceIndex: number;
}

interface ModalFilmstripProps {
  total: number;
  activeIndex: number;
  getItem: (logicalIndex: number) => ModalFilmstripItem | null;
  onNeedRange: (startIndex: number, endIndex: number) => void;
  onSelect: (item: ModalFilmstripItem) => void;
  onNavigate: (intent: 'prev' | 'next') => void;
  onCollapse: () => void;
  onSessionExpired: () => void;
  toggleShortcut: string;
  presentation?: 'layout' | 'overlay';
}

interface FilmstripWindow {
  start: number;
  end: number;
}

function getFilmstripWindow(
  total: number,
  scrollLeft: number,
  viewportWidth: number,
): FilmstripWindow {
  if (total <= 0) return { start: 0, end: -1 };
  const safeWidth = viewportWidth > 0 ? viewportWidth : FILMSTRIP_FALLBACK_VIEWPORT_WIDTH;
  const firstVisible = Math.max(0, Math.floor(scrollLeft / FILMSTRIP_ITEM_PITCH));
  const visibleCount = Math.max(1, Math.ceil(safeWidth / FILMSTRIP_ITEM_PITCH));
  return {
    start: Math.max(0, firstVisible - FILMSTRIP_OVERSCAN_ITEMS),
    end: Math.min(total - 1, firstVisible + visibleCount + FILMSTRIP_OVERSCAN_ITEMS - 1),
  };
}

function appendPriority(url: string) {
  return `${url}${url.includes('?') ? '&' : '?'}priority=visible`;
}

function ModalFilmstrip({
  total,
  activeIndex,
  getItem,
  onNeedRange,
  onSelect,
  onNavigate,
  onCollapse,
  onSessionExpired,
  toggleShortcut,
  presentation = 'layout',
}: ModalFilmstripProps) {
  const scrollerRef = useRef<HTMLDivElement>(null);
  const [renderWindow, setRenderWindow] = useState<FilmstripWindow>(() => (
    getFilmstripWindow(total, Math.max(0, activeIndex) * FILMSTRIP_ITEM_PITCH, FILMSTRIP_FALLBACK_VIEWPORT_WIDTH)
  ));

  const updateRenderWindow = useCallback(() => {
    const scroller = scrollerRef.current;
    const next = getFilmstripWindow(
      total,
      scroller?.scrollLeft ?? Math.max(0, activeIndex) * FILMSTRIP_ITEM_PITCH,
      scroller?.clientWidth ?? FILMSTRIP_FALLBACK_VIEWPORT_WIDTH,
    );
    setRenderWindow((current) => (
      current.start === next.start && current.end === next.end ? current : next
    ));
  }, [activeIndex, total]);

  useEffect(() => {
    const scroller = scrollerRef.current;
    if (!scroller || activeIndex < 0 || activeIndex >= total) {
      updateRenderWindow();
      return;
    }
    const viewportWidth = scroller.clientWidth || FILMSTRIP_FALLBACK_VIEWPORT_WIDTH;
    scroller.scrollLeft = Math.max(
      0,
      activeIndex * FILMSTRIP_ITEM_PITCH - (viewportWidth - FILMSTRIP_ITEM_WIDTH) / 2,
    );
    updateRenderWindow();
  }, [activeIndex, total, updateRenderWindow]);

  useEffect(() => {
    onNeedRange(renderWindow.start, renderWindow.end);
  }, [onNeedRange, renderWindow.end, renderWindow.start]);

  useEffect(() => {
    const sync = () => updateRenderWindow();
    window.addEventListener('resize', sync);
    if (typeof ResizeObserver === 'undefined' || !scrollerRef.current) {
      return () => window.removeEventListener('resize', sync);
    }
    const observer = new ResizeObserver(sync);
    observer.observe(scrollerRef.current);
    return () => {
      observer.disconnect();
      window.removeEventListener('resize', sync);
    };
  }, [updateRenderWindow]);

  const renderedIndexes = useMemo(() => {
    const indexes: number[] = [];
    for (let index = renderWindow.start; index <= renderWindow.end; index += 1) {
      indexes.push(index);
    }
    return indexes;
  }, [renderWindow.end, renderWindow.start]);

  const stopPropagation = useCallback((event: React.SyntheticEvent) => {
    event.stopPropagation();
  }, []);

  return (
    <section
      id="modal-filmstrip"
      className={`modal-filmstrip-shell is-${presentation}`}
      aria-label="Image filmstrip"
      data-presentation={presentation}
      onClick={stopPropagation}
      onDoubleClick={stopPropagation}
      onPointerDown={stopPropagation}
      onPointerMove={stopPropagation}
      onPointerUp={stopPropagation}
      onPointerCancel={stopPropagation}
    >
      <div className="modal-filmstrip-header">
        <span>{activeIndex >= 0 ? activeIndex + 1 : 0} / {total}</span>
        {presentation === 'layout' ? (
          <button
            type="button"
            className="modal-filmstrip-collapse"
            onClick={onCollapse}
            title={`Hide filmstrip (${toggleShortcut})`}
            aria-label="Hide image filmstrip"
            aria-controls="modal-filmstrip"
            aria-keyshortcuts={toggleShortcut}
          >
            Hide
          </button>
        ) : (
          <span className="modal-filmstrip-transient-label">Move away to hide</span>
        )}
      </div>
      <div
        ref={scrollerRef}
        className="modal-filmstrip-scroller"
        role="listbox"
        aria-label="Image filmstrip thumbnails"
        aria-orientation="horizontal"
        onScroll={updateRenderWindow}
        onKeyDown={(event) => {
          if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
          event.preventDefault();
          event.stopPropagation();
          onNavigate(event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? 'prev' : 'next');
        }}
        onWheel={(event) => {
          event.stopPropagation();
          if (Math.abs(event.deltaY) > Math.abs(event.deltaX) && scrollerRef.current) {
            event.preventDefault();
            scrollerRef.current.scrollLeft += event.deltaY;
            updateRenderWindow();
          }
        }}
      >
        <div
          className="modal-filmstrip-track"
          style={{
            width: Math.max(FILMSTRIP_ITEM_PITCH, total * FILMSTRIP_ITEM_PITCH),
            height: FILMSTRIP_ITEM_HEIGHT,
          }}
        >
          {renderedIndexes.map((logicalIndex) => {
            const item = getItem(logicalIndex);
            const isCurrent = logicalIndex === activeIndex;
            const positionStyle = {
              left: logicalIndex * FILMSTRIP_ITEM_PITCH,
              width: FILMSTRIP_ITEM_WIDTH,
              height: FILMSTRIP_ITEM_HEIGHT,
            };
            if (!item) {
              return (
                <div
                  key={`pending-${logicalIndex}`}
                  className="modal-filmstrip-item is-loading"
                  style={positionStyle}
                  role="option"
                  aria-disabled="true"
                  aria-selected={isCurrent}
                  aria-posinset={logicalIndex + 1}
                  aria-setsize={total}
                  aria-label={`Image ${logicalIndex + 1} of ${total} is loading`}
                />
              );
            }
            const { image } = item;
            return (
              <button
                key={image.id}
                type="button"
                className={`modal-filmstrip-item ${isCurrent ? 'is-current' : ''}`}
                style={positionStyle}
                role="option"
                aria-selected={isCurrent}
                aria-current={isCurrent ? 'true' : undefined}
                aria-posinset={logicalIndex + 1}
                aria-setsize={total}
                aria-label={`Open ${image.filename}, image ${logicalIndex + 1} of ${total}`}
                title={image.filename}
                data-filmstrip-index={logicalIndex}
                data-source-index={item.sourceIndex}
                onClick={() => onSelect(item)}
              >
                <CachedImage
                  src={image.fileUrl}
                  requestSrc={appendPriority(image.fileUrl)}
                  fallbackSrc={image.fullUrl}
                  cacheKind="thumb"
                  alt=""
                  className="modal-filmstrip-image"
                  loading="lazy"
                  decoding="async"
                  onSessionExpired={onSessionExpired}
                  draggable={false}
                />
              </button>
            );
          })}
        </div>
      </div>
    </section>
  );
}

export default memo(ModalFilmstrip);

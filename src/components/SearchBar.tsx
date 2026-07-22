'use client';

import React, { useState, useEffect, useId, useRef, useCallback } from 'react';
import { GripVertical, X } from 'lucide-react';
import { useImageStore } from '../store/ImageContext';

interface TagEntry { tag: string; count: number; }

interface SearchHistoryResponse {
  ok?: boolean;
  entries?: string[];
  malformed?: boolean;
  futureVersion?: boolean;
  error?: string;
}

interface TagPointerDrag {
  pointerId: number;
  fromIndex: number;
  tag: string;
  startX: number;
  startY: number;
  targetIndex: number;
  active: boolean;
  snapshot: string[];
  captureElement: HTMLSpanElement;
}

interface TagDesktopDrag {
  fromIndex: number;
  tag: string;
  snapshot: string[];
}

type TagReorderResult = 'moved' | 'same' | 'stale';

const POINTER_REORDER_THRESHOLD_PX = 8;

function parseQueryTags(query: string): string[] {
  return query
    .split(',')
    .map((token) => token.trim())
    .filter(Boolean);
}

function chipToneClass(tag: string): string {
  let hash = 0;
  for (let i = 0; i < tag.length; i++) {
    hash = (hash * 31 + tag.charCodeAt(i)) >>> 0;
  }
  return `tone-${hash % 6}`;
}

function sameTagOrder(first: readonly string[], second: readonly string[]) {
  return first.length === second.length && first.every((tag, index) => tag === second[index]);
}

export default function SearchBar() {
  const { searchQuery, setSearchQuery, indexToken } = useImageStore();
  const [committedTags, setCommittedTags] = useState<string[]>(() => parseQueryTags(searchQuery));
  const [inputToken, setInputToken] = useState('');
  const [tags, setTags] = useState<TagEntry[]>([]);
  const [isLoadingTags, setIsLoadingTags] = useState(true);
  const [suggestions, setSuggestions] = useState<TagEntry[]>([]);
  const [showDropdown, setShowDropdown] = useState(false);
  const [selectedIdx, setSelectedIdx] = useState(-1);
  const [historyEntries, setHistoryEntries] = useState<string[]>([]);
  const [showHistory, setShowHistory] = useState(false);
  const [selectedHistoryIdx, setSelectedHistoryIdx] = useState(-1);
  const [isLoadingHistory, setIsLoadingHistory] = useState(false);
  const [historyStatus, setHistoryStatus] = useState('');
  const [historyNavigationStatus, setHistoryNavigationStatus] = useState('');
  const [draggingIdx, setDraggingIdx] = useState<number | null>(null);
  const [dragOverIdx, setDragOverIdx] = useState<number | null>(null);
  const [tagArrangementStatus, setTagArrangementStatus] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const chipRefs = useRef(new Map<number, HTMLSpanElement>());
  const historyButtonRefs = useRef(new Map<number, HTMLButtonElement>());
  const committedTagsRef = useRef(committedTags);
  const pointerDragRef = useRef<TagPointerDrag | null>(null);
  const desktopDragRef = useRef<TagDesktopDrag | null>(null);
  const focusAfterTagChangeRef = useRef<{ index?: number; input?: boolean } | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const historyRequestGenerationRef = useRef(0);
  const suppressNextHistoryOpenRef = useRef(false);
  const lastSentQueryRef = useRef(searchQuery);
  const suggestionListboxId = useId();
  const suggestionStatusId = useId();
  const historyListboxId = useId();
  const historyStatusId = useId();

  const composedQuery = [...committedTags, inputToken.trim()].filter(Boolean).join(', ');

  useEffect(() => {
    committedTagsRef.current = committedTags;
  }, [committedTags]);

  useEffect(() => {
    if (searchQuery === lastSentQueryRef.current) return;
    setCommittedTags(parseQueryTags(searchQuery));
    setInputToken('');
    setSuggestions([]);
    setShowDropdown(false);
    setShowHistory(false);
    setSelectedIdx(-1);
    setSelectedHistoryIdx(-1);
    setHistoryNavigationStatus('');
    lastSentQueryRef.current = searchQuery;
  }, [searchQuery]);

  useEffect(() => {
    const pending = focusAfterTagChangeRef.current;
    if (!pending) return;
    focusAfterTagChangeRef.current = null;
    if (typeof pending.index === 'number') chipRefs.current.get(pending.index)?.focus();
    else if (pending.input) inputRef.current?.focus();
  }, [committedTags]);

  useEffect(() => {
    let isCurrent = true;
    const tokenParam = indexToken ? `?indexToken=${encodeURIComponent(indexToken)}` : '';
    fetch(`/api/tags${tokenParam}`)
      .then((r) => r.json())
      .then((data) => {
        if (isCurrent && data.tags) setTags(data.tags);
      })
      .catch(() => {})
      .finally(() => {
        if (isCurrent) setIsLoadingTags(false);
      });
    return () => {
      isCurrent = false;
    };
  }, [indexToken]);

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    if (composedQuery === searchQuery || composedQuery === lastSentQueryRef.current) return;
    debounceRef.current = setTimeout(() => {
      if (composedQuery === lastSentQueryRef.current) return;
      lastSentQueryRef.current = composedQuery;
      setSearchQuery(composedQuery);
    }, 200);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [composedQuery, searchQuery, setSearchQuery]);

  useEffect(() => {
    if (!inputToken.trim() || tags.length === 0) {
      setSuggestions([]);
      return;
    }
    const lastToken = inputToken.trim().toLowerCase();
    if (lastToken.length < 1) {
      setSuggestions([]);
      return;
    }

    const matched = tags
      .filter((t) => {
        if (!t.tag.includes(lastToken) || t.tag === lastToken) return false;
        return !committedTags.includes(t.tag);
      })
      .sort((a, b) => {
        const aStarts = a.tag.startsWith(lastToken) ? 0 : 1;
        const bStarts = b.tag.startsWith(lastToken) ? 0 : 1;
        if (aStarts !== bStarts) return aStarts - bStarts;
        return b.count - a.count;
      })
      .slice(0, 8);

    setSuggestions(matched);
    setSelectedIdx(-1);
  }, [inputToken, tags, committedTags]);

  const commitSearchHistory = useCallback(async (rawQuery: string) => {
    const query = parseQueryTags(rawQuery).join(', ');
    if (!query) return;
    try {
      const response = await fetch('/api/search-history', {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ query }),
      });
      const data = await response.json() as SearchHistoryResponse;
      if (!response.ok || data.ok === false) {
        setHistoryStatus(data.error || 'Search history could not be saved.');
        return;
      }
    } catch {
      setHistoryStatus('Search history could not be saved.');
    }
  }, []);

  const reloadSearchHistory = useCallback(async () => {
    const generation = ++historyRequestGenerationRef.current;
    setShowHistory(true);
    setShowDropdown(false);
    setSelectedHistoryIdx(-1);
    setHistoryNavigationStatus('');
    setIsLoadingHistory(true);
    try {
      const response = await fetch('/api/search-history', { cache: 'no-store' });
      const data = await response.json() as SearchHistoryResponse;
      if (generation !== historyRequestGenerationRef.current) return;
      setHistoryEntries(Array.isArray(data.entries) ? data.entries : []);
      if (data.malformed || data.futureVersion) {
        setHistoryStatus(data.error || 'Search history is protected and cannot be displayed.');
      } else if (!response.ok || data.ok === false) {
        setHistoryStatus(data.error || 'Search history could not be loaded.');
      } else {
        setHistoryStatus('');
      }
    } catch {
      if (generation === historyRequestGenerationRef.current) {
        setHistoryEntries([]);
        setHistoryStatus('Search history could not be loaded.');
      }
    } finally {
      if (generation === historyRequestGenerationRef.current) setIsLoadingHistory(false);
    }
  }, []);

  const selectHistory = useCallback((rawQuery: string) => {
    const tagsFromHistory = parseQueryTags(rawQuery);
    const query = tagsFromHistory.join(', ');
    if (!query) return;
    committedTagsRef.current = tagsFromHistory;
    setCommittedTags(tagsFromHistory);
    setInputToken('');
    setSuggestions([]);
    setShowDropdown(false);
    setShowHistory(false);
    setSelectedIdx(-1);
    setSelectedHistoryIdx(-1);
    setHistoryNavigationStatus('');
    lastSentQueryRef.current = query;
    setSearchQuery(query);
    void commitSearchHistory(query);
    if (document.activeElement !== inputRef.current) suppressNextHistoryOpenRef.current = true;
    inputRef.current?.focus();
  }, [commitSearchHistory, setSearchQuery]);

  const deleteHistory = useCallback(async (query?: string) => {
    try {
      const response = await fetch('/api/search-history', {
        method: 'DELETE',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(query ? { query } : { clear: true }),
      });
      const data = await response.json() as SearchHistoryResponse;
      if (!response.ok || data.ok === false) {
        setHistoryStatus(data.error || 'Search history could not be changed.');
        return;
      }
      setHistoryEntries(Array.isArray(data.entries) ? data.entries : []);
      setSelectedHistoryIdx(-1);
      setHistoryNavigationStatus('');
      setHistoryStatus(query ? 'Removed search from history.' : 'Cleared search history.');
    } catch {
      setHistoryStatus('Search history could not be changed.');
    }
  }, []);

  const addTag = useCallback((tag: string) => {
    const parsed = parseQueryTags(tag);
    if (parsed.length === 0) return;

    const next = [...committedTagsRef.current];
    for (const item of parsed) {
      if (!next.includes(item)) next.push(item);
    }
    committedTagsRef.current = next;
    setCommittedTags(next);

    setInputToken('');
    setSuggestions([]);
    setShowDropdown(false);
    setShowHistory(false);
    setSelectedIdx(-1);
    setSelectedHistoryIdx(-1);
    setHistoryNavigationStatus('');
    void commitSearchHistory(next.join(', '));
    inputRef.current?.focus();
  }, [commitSearchHistory]);

  const removeTagAt = useCallback((index: number, tag: string) => {
    setCommittedTags((prev) => prev.filter((_, itemIndex) => itemIndex !== index));
    setTagArrangementStatus(`Removed tag ${tag}.`);
  }, []);

  const commitTagReorder = useCallback((
    from: number,
    to: number,
    expectedTag?: string,
    expectedSnapshot?: readonly string[],
  ): TagReorderResult => {
    const current = committedTagsRef.current;
    if (
      from < 0 || to < 0 || from >= current.length || to >= current.length ||
      (expectedTag !== undefined && current[from] !== expectedTag) ||
      (expectedSnapshot !== undefined && !sameTagOrder(current, expectedSnapshot))
    ) {
      return 'stale';
    }
    if (from === to) return 'same';

    const next = [...current];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    committedTagsRef.current = next;
    focusAfterTagChangeRef.current = { index: to };
    setCommittedTags(next);
    setTagArrangementStatus(`Moved tag ${moved} to position ${to + 1} of ${next.length}.`);
    return 'moved';
  }, []);

  const findClosestChipIndex = useCallback((clientX: number, clientY: number) => {
    let closestIndex: number | null = null;
    let closestDistance = Number.POSITIVE_INFINITY;
    for (const [index, chip] of chipRefs.current) {
      const rect = chip.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) continue;
      const deltaX = clientX - (rect.left + rect.width / 2);
      const deltaY = clientY - (rect.top + rect.height / 2);
      const distance = deltaX * deltaX + deltaY * deltaY;
      if (distance < closestDistance) {
        closestDistance = distance;
        closestIndex = index;
      }
    }
    return closestIndex;
  }, []);

  const clearPointerDrag = useCallback((session: TagPointerDrag) => {
    pointerDragRef.current = null;
    setDraggingIdx(null);
    setDragOverIdx(null);
    try {
      if (session.captureElement.hasPointerCapture?.(session.pointerId)) {
        session.captureElement.releasePointerCapture(session.pointerId);
      }
    } catch {
      // Pointer capture may already have been released by the browser.
    }
  }, []);

  const handleChipPointerDown = useCallback((
    event: React.PointerEvent<HTMLSpanElement>,
    tag: string,
    index: number,
  ) => {
    if (event.pointerType === 'mouse' || event.isPrimary === false || event.button !== 0) return;
    const sourceChip = chipRefs.current.get(index);
    if (!sourceChip) return;

    sourceChip.focus();
    const session: TagPointerDrag = {
      pointerId: event.pointerId,
      fromIndex: index,
      tag,
      startX: event.clientX,
      startY: event.clientY,
      targetIndex: index,
      active: false,
      snapshot: [...committedTagsRef.current],
      captureElement: event.currentTarget,
    };
    pointerDragRef.current = session;
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // Capture is best-effort on older embedded browsers.
    }
  }, []);

  const handleChipPointerMove = useCallback((event: React.PointerEvent<HTMLSpanElement>) => {
    const session = pointerDragRef.current;
    if (!session || session.pointerId !== event.pointerId) return;
    if (!sameTagOrder(committedTagsRef.current, session.snapshot)) {
      if (session.active) setTagArrangementStatus(`Reordering tag ${session.tag} canceled because the tag list changed.`);
      clearPointerDrag(session);
      return;
    }

    const distance = Math.hypot(event.clientX - session.startX, event.clientY - session.startY);
    if (!session.active && distance < POINTER_REORDER_THRESHOLD_PX) return;
    if (!session.active) {
      session.active = true;
      setDraggingIdx(session.fromIndex);
      setDragOverIdx(session.fromIndex);
    }
    event.preventDefault();

    const targetIndex = findClosestChipIndex(event.clientX, event.clientY);
    if (targetIndex === null || targetIndex === session.targetIndex) return;
    session.targetIndex = targetIndex;
    setDragOverIdx(targetIndex);
  }, [clearPointerDrag, findClosestChipIndex]);

  const finishChipPointerDrag = useCallback((event: React.PointerEvent<HTMLSpanElement>) => {
    const session = pointerDragRef.current;
    if (!session || session.pointerId !== event.pointerId) return;
    const releaseDistance = Math.hypot(event.clientX - session.startX, event.clientY - session.startY);
    const shouldCommit = session.active || releaseDistance >= POINTER_REORDER_THRESHOLD_PX;
    if (shouldCommit) {
      event.preventDefault();
      const releaseTargetIndex = findClosestChipIndex(event.clientX, event.clientY);
      if (releaseTargetIndex !== null) session.targetIndex = releaseTargetIndex;
      const result = commitTagReorder(
        session.fromIndex,
        session.targetIndex,
        session.tag,
        session.snapshot,
      );
      if (result === 'stale') {
        setTagArrangementStatus(`Reordering tag ${session.tag} canceled because the tag list changed.`);
      }
    }
    clearPointerDrag(session);
  }, [clearPointerDrag, commitTagReorder, findClosestChipIndex]);

  const cancelChipPointerDrag = useCallback((event: React.PointerEvent<HTMLSpanElement>) => {
    const session = pointerDragRef.current;
    if (!session || session.pointerId !== event.pointerId) return;
    if (session.active) setTagArrangementStatus(`Reordering tag ${session.tag} canceled.`);
    clearPointerDrag(session);
  }, [clearPointerDrag]);

  useEffect(() => () => {
    const session = pointerDragRef.current;
    pointerDragRef.current = null;
    if (!session) return;
    try {
      if (session.captureElement.hasPointerCapture?.(session.pointerId)) {
        session.captureElement.releasePointerCapture(session.pointerId);
      }
    } catch {
      // The element may already be detached during unmount.
    }
  }, []);

  const handleChipKeyDown = (event: React.KeyboardEvent<HTMLSpanElement>, tag: string, index: number) => {
    if (event.target !== event.currentTarget) return;

    if (event.altKey && event.shiftKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
      event.preventDefault();
      const delta = event.key === 'ArrowLeft' ? -1 : 1;
      const nextIndex = Math.max(0, Math.min(committedTags.length - 1, index + delta));
      if (nextIndex === index) {
        setTagArrangementStatus(`Tag ${tag} is already ${index === 0 ? 'first' : 'last'}.`);
        return;
      }

      commitTagReorder(index, nextIndex, tag);
      return;
    }

    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      const remainingCount = committedTags.length - 1;
      focusAfterTagChangeRef.current = remainingCount > 0
        ? { index: Math.min(index, remainingCount - 1) }
        : { input: true };
      removeTagAt(index, tag);
    }
  };

  const announceHistorySelection = (index: number) => {
    const query = historyEntries[index];
    if (!query) return;
    setHistoryNavigationStatus(`Search history ${index + 1} of ${historyEntries.length}: ${query}`);
  };

  const moveHistorySelection = (
    direction: -1 | 1,
    moveFocus: boolean,
    originIndex = selectedHistoryIdx,
  ) => {
    if (historyEntries.length === 0) return;
    const lastIndex = historyEntries.length - 1;
    const nextIndex = originIndex < 0
      ? direction > 0 ? 0 : lastIndex
      : (originIndex + direction + historyEntries.length) % historyEntries.length;
    setSelectedHistoryIdx(nextIndex);
    announceHistorySelection(nextIndex);
    if (moveFocus) historyButtonRefs.current.get(nextIndex)?.focus();
  };

  const handleHistoryRowKeyDown = (
    event: React.KeyboardEvent<HTMLButtonElement>,
    query: string,
    index: number,
  ) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      moveHistorySelection(event.key === 'ArrowDown' ? 1 : -1, true, index);
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      selectHistory(query);
      return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      setShowHistory(false);
      setSelectedHistoryIdx(-1);
      setHistoryNavigationStatus('');
      if (document.activeElement !== inputRef.current) suppressNextHistoryOpenRef.current = true;
      inputRef.current?.focus();
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (showHistory) {
      if (e.key === 'Escape') {
        e.preventDefault();
        setShowHistory(false);
        setSelectedHistoryIdx(-1);
        setHistoryNavigationStatus('');
        inputRef.current?.focus();
        return;
      }
      if (isLoadingHistory && (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter')) {
        // The previous disk snapshot is deliberately retained until the new
        // read resolves, but it must not be keyboard-selectable while hidden.
        // Otherwise an entry removed by WPF could be invisibly recommitted.
        e.preventDefault();
        return;
      }
      if (historyEntries.length > 0 && e.key === 'ArrowDown') {
        e.preventDefault();
        moveHistorySelection(1, false);
        return;
      }
      if (historyEntries.length > 0 && e.key === 'ArrowUp') {
        e.preventDefault();
        moveHistorySelection(-1, false);
        return;
      }
      if (e.key === 'Enter' && selectedHistoryIdx >= 0) {
        e.preventDefault();
        selectHistory(historyEntries[selectedHistoryIdx]);
        return;
      }
    }

    if (suggestions.length > 0) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setShowDropdown(true);
        setSelectedIdx((prev) => Math.min(prev + 1, suggestions.length - 1));
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        setShowDropdown(true);
        setSelectedIdx((prev) => Math.max(prev - 1, -1));
      } else if (e.key === 'Enter' && selectedIdx >= 0) {
        e.preventDefault();
        addTag(suggestions[selectedIdx].tag);
        return;
      } else if (e.key === 'Tab' && selectedIdx >= 0) {
        e.preventDefault();
        addTag(suggestions[selectedIdx].tag);
        return;
      } else if (e.key === 'Escape') {
        e.preventDefault();
        setShowDropdown(false);
        setSelectedIdx(-1);
        return;
      }
    }

    if (e.key === 'Enter' || e.key === ',') {
      if (inputToken.trim()) {
        e.preventDefault();
        addTag(inputToken);
      }
      return;
    }

    if (e.key === 'Backspace' && !inputToken && committedTags.length > 0) {
      e.preventDefault();
      setCommittedTags((prev) => prev.slice(0, -1));
    }
  };

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (
        dropdownRef.current && !dropdownRef.current.contains(e.target as Node) &&
        inputRef.current && !inputRef.current.contains(e.target as Node)
      ) {
        setShowDropdown(false);
        setShowHistory(false);
        setHistoryNavigationStatus('');
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const isSuggestionListOpen = showDropdown && suggestions.length > 0;
  const isHistoryPopupOpen = showHistory && !inputToken.trim();
  const activeSuggestionId = isSuggestionListOpen && selectedIdx >= 0
    ? `${suggestionListboxId}-option-${selectedIdx}`
    : undefined;
  const suggestionStatus = inputToken.trim()
    ? isLoadingTags
      ? 'Loading tag suggestions.'
      : suggestions.length > 0
        ? `${suggestions.length} tag suggestions available. Use the up and down arrow keys to review them.`
        : 'No tag suggestions available.'
    : '';

  return (
    <div className="search-bar-wrapper" ref={wrapperRef}>
      <div className="search-bar">
        <svg aria-hidden="true" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <circle cx="11" cy="11" r="8" />
          <line x1="21" y1="21" x2="16.65" y2="16.65" />
        </svg>

        <div className="search-chip-area">
          {committedTags.length > 0 && (
            <span className="search-chip-list" role="list" aria-label="Committed search tags">
              {committedTags.map((tag, index) => (
                <span
                  key={`${tag}-${index}`}
                  ref={(node) => {
                    if (node) chipRefs.current.set(index, node);
                    else chipRefs.current.delete(index);
                  }}
                  className={`search-chip ${chipToneClass(tag)} ${draggingIdx !== null && draggingIdx === index ? 'is-dragging' : ''} ${dragOverIdx !== null && dragOverIdx === index ? 'is-drag-over' : ''}`}
                  role="listitem"
                  tabIndex={0}
                  aria-posinset={index + 1}
                  aria-setsize={committedTags.length}
                  aria-label={`Search tag ${tag}, position ${index + 1} of ${committedTags.length}. Drag the handle or use Alt Shift Left or Right to reorder. Delete or Backspace to remove.`}
                  draggable
                  onKeyDown={(event) => handleChipKeyDown(event, tag, index)}
                  onDragStart={(e) => {
                    desktopDragRef.current = {
                      fromIndex: index,
                      tag,
                      snapshot: [...committedTagsRef.current],
                    };
                    setDraggingIdx(index);
                    setDragOverIdx(index);
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData('text/plain', String(index));
                  }}
                  onDragOver={(e) => {
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'move';
                    setDragOverIdx(index);
                  }}
                  onDrop={(e) => {
                    e.preventDefault();
                    const session = desktopDragRef.current;
                    const from = session?.fromIndex
                      ?? parseInt(e.dataTransfer.getData('text/plain') || '-1', 10);
                    const result = commitTagReorder(
                      from,
                      index,
                      session?.tag,
                      session?.snapshot,
                    );
                    if (result === 'stale') {
                      setTagArrangementStatus('Tag reorder canceled because the tag list changed.');
                    }
                    desktopDragRef.current = null;
                    setDraggingIdx(null);
                    setDragOverIdx(null);
                  }}
                  onDragEnd={() => {
                    desktopDragRef.current = null;
                    setDraggingIdx(null);
                    setDragOverIdx(null);
                  }}
                >
                  <span
                    className="search-chip-drag-handle"
                    data-testid="search-tag-drag-handle"
                    aria-hidden="true"
                    onPointerDown={(event) => handleChipPointerDown(event, tag, index)}
                    onPointerMove={handleChipPointerMove}
                    onPointerUp={finishChipPointerDrag}
                    onPointerCancel={cancelChipPointerDrag}
                    onLostPointerCapture={cancelChipPointerDrag}
                  >
                    <GripVertical size={13} aria-hidden="true" />
                  </span>
                  <span className="search-chip-label">{tag}</span>
                  <button
                    className="search-chip-remove"
                    type="button"
                    onClick={() => removeTagAt(index, tag)}
                    onKeyDown={(event) => event.stopPropagation()}
                    title={`Remove ${tag}`}
                    aria-label={`Remove tag ${tag}`}
                  >
                    <X size={13} aria-hidden="true" />
                  </button>
                </span>
              ))}
            </span>
          )}

          <input
            ref={inputRef}
            type="text"
            className="search-input"
            role="combobox"
            aria-label="Search tags"
            aria-autocomplete="list"
            aria-expanded={isSuggestionListOpen}
            aria-controls={isSuggestionListOpen ? suggestionListboxId : undefined}
            aria-activedescendant={activeSuggestionId}
            aria-describedby={`${suggestionStatusId} ${historyStatusId}`}
            placeholder={committedTags.length === 0 ? 'Search tags. Use comma or Enter to add.' : 'Add another tag...'}
            value={inputToken}
            onChange={(e) => {
              setInputToken(e.target.value);
              setShowHistory(false);
              setShowDropdown(true);
              setSelectedIdx(-1);
              setSelectedHistoryIdx(-1);
              setHistoryNavigationStatus('');
            }}
            onFocus={() => {
              if (suppressNextHistoryOpenRef.current) {
                suppressNextHistoryOpenRef.current = false;
                return;
              }
              if (!inputToken.trim()) void reloadSearchHistory();
              else if (suggestions.length > 0) setShowDropdown(true);
            }}
            onClick={() => {
              if (!inputToken.trim() && !showHistory) void reloadSearchHistory();
            }}
            onBlur={(event) => {
              const nextFocus = event.relatedTarget;
              if (nextFocus instanceof Node && wrapperRef.current?.contains(nextFocus)) return;
              void commitSearchHistory([...committedTagsRef.current, event.currentTarget.value]
                .filter(Boolean)
                .join(', '));
            }}
            onKeyDown={handleKeyDown}
          />
        </div>

        {composedQuery && (
          <button
            className="search-clear"
            type="button"
            onClick={() => {
              setCommittedTags([]);
              setInputToken('');
              setShowDropdown(false);
              setShowHistory(false);
              setHistoryNavigationStatus('');
            }}
            title="Clear search"
            aria-label="Clear all search tags"
          >
            <svg aria-hidden="true" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <line x1="18" y1="6" x2="6" y2="18" />
              <line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>
        )}
      </div>

      <div id={suggestionStatusId} className="search-suggestion-status" role="status" aria-live="polite" aria-label="Tag suggestion status">
        {suggestionStatus}
      </div>
      <div id={historyStatusId} className="search-suggestion-status" role="status" aria-live="polite" aria-label="Search history status">
        {[historyStatus, historyNavigationStatus].filter(Boolean).join(' ')}
      </div>
      <div className="search-suggestion-status" role="status" aria-live="polite" aria-atomic="true" aria-label="Search tag arrangement">
        {tagArrangementStatus}
      </div>

      {isSuggestionListOpen && (
        <div
          id={suggestionListboxId}
          className="search-dropdown"
          ref={dropdownRef}
          role="listbox"
          aria-label="Tag suggestions"
        >
          {suggestions.map((s, i) => (
            <button
              id={`${suggestionListboxId}-option-${i}`}
              key={s.tag}
              className={`search-suggestion ${i === selectedIdx ? 'selected' : ''}`}
              type="button"
              role="option"
              aria-selected={i === selectedIdx}
              tabIndex={-1}
              onMouseDown={(e) => {
                e.preventDefault();
                addTag(s.tag);
              }}
              onMouseEnter={() => setSelectedIdx(i)}
            >
              <span className="suggestion-tag">{s.tag}</span>
              <span className="suggestion-count">{s.count}</span>
            </button>
          ))}
        </div>
      )}

      {isHistoryPopupOpen && (
        <div
          id={historyListboxId}
          className="search-dropdown search-history-dropdown"
          ref={dropdownRef}
          role="region"
          aria-label="Search history"
        >
          <div className="search-history-heading">
            <span>Recent searches</span>
            {historyEntries.length > 0 && (
              <button
                type="button"
                className="search-history-clear"
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => void deleteHistory()}
              >
                Clear all
              </button>
            )}
          </div>
          <div>
            {isLoadingHistory && <div className="search-history-empty">Loading search history...</div>}
            {!isLoadingHistory && historyEntries.length === 0 && (
              <div className="search-history-empty">No recent searches</div>
            )}
            {!isLoadingHistory && historyEntries.map((query, index) => (
              <div className="search-history-row" key={`${query}-${index}`}>
                <button
                  ref={(node) => {
                    if (node) historyButtonRefs.current.set(index, node);
                    else historyButtonRefs.current.delete(index);
                  }}
                  className={`search-history-select ${index === selectedHistoryIdx ? 'selected' : ''}`}
                  type="button"
                  aria-label={`Use search ${query}`}
                  onMouseDown={(event) => {
                    event.preventDefault();
                  }}
                  onClick={() => selectHistory(query)}
                  onMouseEnter={() => setSelectedHistoryIdx(index)}
                  onFocus={() => setSelectedHistoryIdx(index)}
                  onKeyDown={(event) => handleHistoryRowKeyDown(event, query, index)}
                >
                  {query}
                </button>
                <button
                  className="search-history-remove"
                  type="button"
                  title={`Remove ${query} from search history`}
                  aria-label={`Remove ${query} from search history`}
                  onMouseDown={(event) => {
                    event.preventDefault();
                    event.stopPropagation();
                  }}
                  onClick={(event) => {
                    event.stopPropagation();
                    void deleteHistory(query);
                  }}
                >
                  <X size={14} aria-hidden="true" />
                </button>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

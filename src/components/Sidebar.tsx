'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import { getLoadedResultCounts, getResultCountLabel, sortFolderBuckets, type FolderBucket } from '../lib/viewerUi';
import { appendDirSet, summarizeDirSet } from '../lib/pathSet';
import { FAVORITE_FILTER_LEVELS } from '../lib/browserUiPreferences';
import { useDialogFocus } from '../lib/useDialogFocus';
import { MAX_THUMB_SIZE, MIN_THUMB_SIZE, THUMB_ZOOM_STEP } from '../lib/thumbnailSizing';

function createRandomSeed() {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export default function Sidebar() {
  const {
    view,
    setView,
    dirPath,
    setDirPath,
    startScan,
    totalIndexed,
    searchTotal,
    searchResults,
    searchQuery,
    favorites,
    showFavOnly,
    setShowFavOnly,
    showUnfavOnly,
    setShowUnfavOnly,
    favoriteFilterLevels,
    toggleFavoriteFilterLevel,
    clearFavoriteFilterLevels,
    showEnhancedOnly,
    setShowEnhancedOnly,
    enhancedSourceIds,
    setShowSettings,
    setPhase,
    perfEnabled,
    setPerfEnabled,
    perfStats,
    indexToken,
  } = useImageStore();
  const { document: albumDocument, albums, activeSource, setLibraryOpen, openAlbum, closeAlbum } = useOptionalAlbumStore();
  const sidebarAlbums = useMemo(() => {
    const recentOrder = new Map((albumDocument?.recentAlbumIds ?? []).map((id, index) => [id, index]));
    return [...albums]
      .sort((left, right) => {
        if (left.pinned !== right.pinned) return left.pinned ? -1 : 1;
        return (recentOrder.get(left.id) ?? 999) - (recentOrder.get(right.id) ?? 999);
      })
      .slice(0, 6);
  }, [albumDocument?.recentAlbumIds, albums]);

  const [folderBuckets, setFolderBuckets] = useState<FolderBucket[]>([]);
  const [loadingFolders, setLoadingFolders] = useState(false);
  const [selectedFolderKeys, setSelectedFolderKeys] = useState<string[]>([]);
  const [lastSelectedFolderKey, setLastSelectedFolderKey] = useState<string | null>(null);
  const [addingFolder, setAddingFolder] = useState(false);
  const [folderActionError, setFolderActionError] = useState('');
  const foldersToggleRef = useRef<HTMLButtonElement>(null);
  const foldersContentRef = useRef<HTMLDivElement>(null);
  const drawerRef = useRef<HTMLElement>(null);
  const [isMobile, setIsMobile] = useState(false);
  const foldersExpanded = view.foldersExpanded;

  const hiddenFolderSet = useMemo(() => new Set(view.hiddenFolders), [view.hiddenFolders]);
  const selectedFolderSet = useMemo(() => new Set(selectedFolderKeys), [selectedFolderKeys]);
  const sortedFolderBuckets = useMemo(
    () => sortFolderBuckets(folderBuckets, view.folderSortBy),
    [folderBuckets, view.folderSortBy]
  );
  const loadedResultCounts = useMemo(() => getLoadedResultCounts({
    searchResults,
    favorites,
    showFavOnly,
    showUnfavOnly,
    favoriteFilterLevels,
    showEnhancedOnly,
    enhancedSourceIds,
  }), [
    enhancedSourceIds,
    favoriteFilterLevels,
    favorites,
    searchResults,
    showEnhancedOnly,
    showFavOnly,
    showUnfavOnly,
  ]);
  const resultCountLabel = useMemo(() => getResultCountLabel({
    searchQuery,
    searchTotal,
    totalIndexed,
    dateFrom: view.dateFrom,
    dateTo: view.dateTo,
    hiddenFolders: view.hiddenFolders,
    loadedCount: loadedResultCounts.loadedCount,
    shownCount: loadedResultCounts.shownCount,
  }), [
    loadedResultCounts.loadedCount,
    loadedResultCounts.shownCount,
    searchQuery,
    searchTotal,
    totalIndexed,
    view.dateFrom,
    view.dateTo,
    view.hiddenFolders,
  ]);

  useEffect(() => {
    if (!dirPath) {
      setFolderBuckets([]);
      return;
    }

    let cancelled = false;
    setLoadingFolders(true);

    const tokenParam = indexToken ? `&indexToken=${encodeURIComponent(indexToken)}` : '';
    fetch(`/api/folders?dir=${encodeURIComponent(dirPath)}${tokenParam}`)
      .then((response) => response.json())
      .then((data) => {
        if (cancelled) return;
        if (Array.isArray(data.folders)) {
          setFolderBuckets(
            data.folders.filter(
              (folder: unknown): folder is FolderBucket =>
                !!folder &&
                typeof folder === 'object' &&
                typeof (folder as FolderBucket).key === 'string' &&
                typeof (folder as FolderBucket).label === 'string' &&
                typeof (folder as FolderBucket).count === 'number'
            )
          );
          return;
        }
        setFolderBuckets([]);
      })
      .catch(() => {
        if (!cancelled) setFolderBuckets([]);
      })
      .finally(() => {
        if (!cancelled) setLoadingFolders(false);
      });

    return () => {
      cancelled = true;
    };
  }, [dirPath, indexToken, totalIndexed]);

  useEffect(() => {
    const available = new Set(folderBuckets.map((folder) => folder.key));
    setSelectedFolderKeys((prev) => prev.filter((key) => available.has(key)));
    setLastSelectedFolderKey((prev) => (prev && available.has(prev) ? prev : null));
  }, [folderBuckets]);

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const media = window.matchMedia('(max-width: 768px)');
    const sync = () => setIsMobile(media.matches);
    sync();
    media.addEventListener?.('change', sync);
    return () => media.removeEventListener?.('change', sync);
  }, []);

  useDialogFocus({
    open: isMobile && view.sidebarOpen,
    dialogRef: drawerRef,
    onEscape: () => setView({ sidebarOpen: false }),
  });

  if (!view.sidebarOpen) return null;

  const toggleFolderVisibility = (folderKey: string) => {
    const isHidden = view.hiddenFolders.includes(folderKey);
    const next = isHidden
      ? view.hiddenFolders.filter((item) => item !== folderKey)
      : [...view.hiddenFolders, folderKey];
    setView({ hiddenFolders: next });
  };

  const selectFolder = (folderKey: string, event: React.MouseEvent<HTMLElement>) => {
    const additive = event.ctrlKey || event.metaKey;
    const range = event.shiftKey && lastSelectedFolderKey;
    const sortedKeys = sortedFolderBuckets.map((folder) => folder.key);

    if (range) {
      const from = sortedKeys.indexOf(lastSelectedFolderKey);
      const to = sortedKeys.indexOf(folderKey);
      if (from >= 0 && to >= 0) {
        const [start, end] = from < to ? [from, to] : [to, from];
        const rangeKeys = sortedKeys.slice(start, end + 1);
        setSelectedFolderKeys((prev) => additive
          ? Array.from(new Set([...prev, ...rangeKeys]))
          : rangeKeys);
        setLastSelectedFolderKey(folderKey);
        return;
      }
    }

    setSelectedFolderKeys((prev) => {
      if (additive) {
        return prev.includes(folderKey)
          ? prev.filter((key) => key !== folderKey)
          : [...prev, folderKey];
      }
      return [folderKey];
    });
    setLastSelectedFolderKey(folderKey);
  };

  const showSelectedFolders = () => {
    const selected = new Set(selectedFolderKeys);
    if (selected.size === 0) return;
    setView({ hiddenFolders: view.hiddenFolders.filter((key) => !selected.has(key)) });
  };

  const hideSelectedFolders = () => {
    if (selectedFolderKeys.length === 0) return;
    setView({ hiddenFolders: Array.from(new Set([...view.hiddenFolders, ...selectedFolderKeys])) });
  };

  const clearSelectedFolders = () => {
    setSelectedFolderKeys([]);
    setLastSelectedFolderKey(null);
  };

  const showAllFolders = () => {
    setView({ hiddenFolders: [] });
  };

  const hideAllFolders = () => {
    setView({ hiddenFolders: sortedFolderBuckets.map((folder) => folder.key) });
  };

  const invertFolderVisibility = () => {
    const hidden = new Set(view.hiddenFolders);
    setView({
      hiddenFolders: sortedFolderBuckets
        .filter((folder) => !hidden.has(folder.key))
        .map((folder) => folder.key),
    });
  };

  const toggleFoldersExpanded = () => {
    const focusedChild = foldersExpanded
      && Boolean(foldersContentRef.current?.contains(document.activeElement));
    if (focusedChild) foldersToggleRef.current?.focus();
    setView({ foldersExpanded: !foldersExpanded });
  };

  const addFolderFromSidebar = async () => {
    if (addingFolder) return;
    setAddingFolder(true);
    setFolderActionError('');
    try {
      const res = await fetch('/api/browse?multi=1', { method: 'POST' });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || 'Failed to open folder dialog.');
      const folders = Array.isArray(data.paths)
        ? data.paths.filter((item: unknown): item is string => typeof item === 'string' && item.trim().length > 0)
        : typeof data.path === 'string' && data.path.trim()
          ? [data.path]
          : [];
      if (folders.length === 0) return;
      const nextDirSet = appendDirSet(dirPath, folders);
      setDirPath(nextDirSet);
      startScan({ dir: nextDirSet });
    } catch (error) {
      setFolderActionError(error instanceof Error ? error.message : String(error));
    } finally {
      setAddingFolder(false);
    }
  };

  const applyDisplayStyle = (displayStyle: typeof view.displayStyle) => {
    if (displayStyle === 'compact') {
      setView({ displayStyle, viewMode: 'grid', aspectMode: 'square', thumbSize: 140 });
      return;
    }
    if (displayStyle === 'poster') {
      setView({ displayStyle, viewMode: 'grid', aspectMode: 'portrait', thumbSize: 240 });
      return;
    }
    setView({ displayStyle });
  };

  return (
    <>
      {isMobile && <button type="button" className="sidebar-drawer-backdrop" aria-label="Close filters" onClick={() => setView({ sidebarOpen: false })} />}
      <aside
        ref={drawerRef}
        className={`sidebar ${isMobile ? 'sidebar-drawer' : ''}`}
        role={isMobile ? 'dialog' : undefined}
        aria-modal={isMobile || undefined}
        aria-label={isMobile ? 'Filters and display options' : undefined}
        tabIndex={isMobile ? -1 : undefined}
      >
      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <rect x="3" y="4" width="18" height="16" rx="2" /><path d="M7 4V2M17 4V2" />
          </svg>
          <span>Albums</span>
        </div>
        {activeSource && (
          <div className="sidebar-album-active">
            <strong>{activeSource.album.name}</strong>
            <span>{activeSource.images.length}/{activeSource.members.length} available</span>
            <button className="sidebar-link" onClick={closeAlbum}>Return to catalog</button>
          </div>
        )}
        <div className="sidebar-album-list">
          {sidebarAlbums.map((album) => (
            <button key={album.id} className={activeSource?.album.id === album.id ? 'active' : ''} onClick={() => void openAlbum(album.id)}>
              <span>{album.pinned ? '● ' : ''}{album.name}</span><small>{album.members.length}</small>
            </button>
          ))}
        </div>
        <button className="sidebar-link" onClick={() => setLibraryOpen(true)}>Manage Albums</button>
      </div>

      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
          </svg>
          <span>Folder</span>
        </div>

        <p className="sidebar-path" title={dirPath}>{summarizeDirSet(dirPath) || dirPath}</p>
        <p className="sidebar-meta" aria-label={`Results: ${resultCountLabel}`}>{resultCountLabel}</p>
        <div className="sidebar-actions">
          <button className="sidebar-link" onClick={() => void addFolderFromSidebar()} disabled={addingFolder}>
            {addingFolder ? 'Adding folder...' : 'Add folder'}
          </button>
          <button className="sidebar-link" onClick={() => setPhase('landing')}>Change folder</button>
        </div>
        {folderActionError && <p className="sidebar-error">{folderActionError}</p>}
      </div>

      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
          </svg>
          <span>Filter</span>
        </div>

        <div className="favorite-filter-block">
          <label className="sidebar-toggle">
            <input type="checkbox" checked={showFavOnly} onChange={(e) => setShowFavOnly(e.target.checked)} />
            <span>Favorites</span>
          </label>

          {showFavOnly && (
            <div className="favorite-level-checks" aria-label="Favorite levels">
              <button
                className={`favorite-level-check ${favoriteFilterLevels.length === 0 ? 'is-active' : ''}`}
                type="button"
                onClick={clearFavoriteFilterLevels}
              >
                All
              </button>
              {FAVORITE_FILTER_LEVELS.map((level) => {
                const checked = favoriteFilterLevels.includes(level);
                return (
                  <label key={level} className={`favorite-level-check ${checked ? 'is-active' : ''}`}>
                    <input
                      aria-label={`Favorite level ${level}`}
                      type="checkbox"
                      checked={checked}
                      onChange={() => toggleFavoriteFilterLevel(level)}
                    />
                    <svg className="favorite-level-heart" width="12" height="12" viewBox="0 0 24 24" aria-hidden="true">
                      <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21.23l8.84-8.84a5.5 5.5 0 0 0 0-7.78z" />
                    </svg>
                    <span>{level}</span>
                  </label>
                );
              })}
            </div>
          )}
        </div>

        <label className="sidebar-toggle" style={{ marginTop: '0.35rem' }}>
          <input type="checkbox" checked={showUnfavOnly} onChange={(e) => setShowUnfavOnly(e.target.checked)} />
          <span>Unrated only</span>
        </label>

        <label className="sidebar-toggle" style={{ marginTop: '0.35rem' }}>
          <input type="checkbox" checked={showEnhancedOnly} onChange={(e) => setShowEnhancedOnly(e.target.checked)} />
          <span>Enhanced only</span>
        </label>

        <div className="sidebar-date-range">
          <label className="sidebar-date-label">
            <span>Date from</span>
            <input
              type="date"
              className="sidebar-date-input"
              value={view.dateFrom}
              onChange={(e) => setView({ dateFrom: e.target.value })}
            />
          </label>

          <label className="sidebar-date-label">
            <span>Date to</span>
            <input
              type="date"
              className="sidebar-date-input"
              value={view.dateTo}
              onChange={(e) => setView({ dateTo: e.target.value })}
            />
          </label>

          {(view.dateFrom || view.dateTo) && (
            <button className="sidebar-link" onClick={() => setView({ dateFrom: '', dateTo: '' })}>
              Clear date filter
            </button>
          )}
        </div>
      </div>

      <div className="sidebar-section">
        <button
          ref={foldersToggleRef}
          type="button"
          className="sidebar-section-header sidebar-section-toggle"
          aria-expanded={foldersExpanded}
          aria-controls="sidebar-folders-content"
          onClick={toggleFoldersExpanded}
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <path d="M3 4h18M3 12h18M3 20h18" />
          </svg>
          <span>Folders</span>
          <svg className="sidebar-section-chevron" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <polyline points="6 9 12 15 18 9" />
          </svg>
        </button>

        <div ref={foldersContentRef} id="sidebar-folders-content" hidden={!foldersExpanded}>
            {loadingFolders && <p className="sidebar-meta">Loading folder list...</p>}

            {!loadingFolders && folderBuckets.length === 0 && (
              <p className="sidebar-meta">No folders found under this root.</p>
            )}

            {folderBuckets.length > 0 && (
              <>
            <div className="sidebar-pills" style={{ marginBottom: '0.5rem' }}>
              <button
                type="button"
                className={`pill ${view.folderSortBy === 'name-asc' ? 'active' : ''}`}
                onClick={() => setView({ folderSortBy: 'name-asc' })}
                aria-pressed={view.folderSortBy === 'name-asc'}
                aria-label="Sort folders A to Z"
              >
                A-Z
              </button>
              <button
                type="button"
                className={`pill ${view.folderSortBy === 'name-desc' ? 'active' : ''}`}
                onClick={() => setView({ folderSortBy: 'name-desc' })}
                aria-pressed={view.folderSortBy === 'name-desc'}
                aria-label="Sort folders Z to A"
              >
                Z-A
              </button>
              <button
                type="button"
                className={`pill ${view.folderSortBy === 'count-desc' ? 'active' : ''}`}
                onClick={() => setView({ folderSortBy: 'count-desc' })}
                aria-pressed={view.folderSortBy === 'count-desc'}
                aria-label="Sort folders by image count"
              >
                Count
              </button>
            </div>

            <div className="sidebar-pills" style={{ marginBottom: '0.5rem' }}>
              <button className="pill" onClick={showAllFolders}>Show all</button>
              <button className="pill" onClick={hideAllFolders}>Hide all</button>
              <button className="pill" onClick={invertFolderVisibility}>Invert</button>
            </div>

            <div className="sidebar-pills" style={{ marginBottom: '0.5rem' }}>
              <button className="pill" onClick={showSelectedFolders} disabled={selectedFolderKeys.length === 0}>
                Show selected
              </button>
              <button className="pill" onClick={hideSelectedFolders} disabled={selectedFolderKeys.length === 0}>
                Hide selected
              </button>
              <button className="pill" onClick={clearSelectedFolders} disabled={selectedFolderKeys.length === 0}>
                Clear selection
              </button>
            </div>

            <div className="sidebar-folder-list">
              {sortedFolderBuckets.map((folder) => {
                const isVisible = !hiddenFolderSet.has(folder.key);
                const isSelected = selectedFolderSet.has(folder.key);
                return (
                  <div
                    key={folder.key}
                    className={`sidebar-folder-item ${isVisible ? 'is-active' : ''} ${isSelected ? 'is-selected' : ''}`}
                    title={`${folder.label} (${folder.count})`}
                    role="group"
                    aria-label={`Folder ${folder.label}`}
                  >
                    <input
                      type="checkbox"
                      className="sidebar-folder-select"
                      checked={isSelected}
                      onChange={() => {}}
                      onClick={(event) => {
                        selectFolder(folder.key, event);
                      }}
                      aria-label={`Select ${folder.label}`}
                    />
                    <button
                      type="button"
                      className="sidebar-folder-primary"
                      onClick={(event) => selectFolder(folder.key, event)}
                      aria-pressed={isSelected}
                      aria-label={`Select folder ${folder.label}, ${folder.count.toLocaleString()} images`}
                    >
                      <span className="sidebar-folder-label">{folder.label}</span>
                      <span className="sidebar-folder-count">{folder.count.toLocaleString()}</span>
                    </button>
                    <button
                      type="button"
                      className={`sidebar-folder-visibility ${isVisible ? 'is-visible' : ''}`}
                      onClick={(event) => {
                        event.stopPropagation();
                        toggleFolderVisibility(folder.key);
                      }}
                      title={isVisible ? 'Hide folder' : 'Show folder'}
                      aria-pressed={isVisible}
                      aria-label={`${isVisible ? 'Hide' : 'Show'} folder ${folder.label}`}
                    >
                      {isVisible ? 'Shown' : 'Hidden'}
                    </button>
                  </div>
                );
              })}
            </div>
              </>
            )}

            {view.hiddenFolders.length > 0 && (
              <div className="sidebar-folder-actions">
                <button className="sidebar-link" style={{ marginTop: '0.55rem' }} onClick={showAllFolders}>
                  Show all folders
                </button>
              </div>
            )}
        </div>
      </div>

      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <line x1="12" y1="5" x2="12" y2="19" />
            <polyline points="19 12 12 19 5 12" />
          </svg>
          <span>Sort</span>
        </div>

        <div className="sidebar-pills">
          {(['newest', 'oldest', 'created-newest', 'created-oldest', 'name', 'random'] as const).map((s) => (
            <button key={s} className={`pill ${view.sortBy === s ? 'active' : ''}`} onClick={() => setView({ sortBy: s })}>
              {s === 'newest'
                ? 'Modified new'
                : s === 'oldest'
                  ? 'Modified old'
                  : s === 'created-newest'
                    ? 'Created new'
                    : s === 'created-oldest'
                      ? 'Created old'
                      : s === 'random'
                        ? 'Random'
                        : 'Name'}
            </button>
          ))}
        </div>
        {view.sortBy === 'random' && (
          <button className="sidebar-link" style={{ marginTop: '0.55rem' }} onClick={() => setView({ randomSeed: createRandomSeed() })}>
            Reshuffle
          </button>
        )}
      </div>

      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <rect x="3" y="3" width="7" height="7" />
            <rect x="14" y="3" width="7" height="7" />
            <rect x="14" y="14" width="7" height="7" />
            <rect x="3" y="14" width="7" height="7" />
          </svg>
          <span>Display</span>
        </div>

        <div className="sidebar-row">
          <span className="sidebar-row-label">Mode</span>
          <div className="sidebar-pills">
            <button className={`pill ${view.viewMode === 'grid' ? 'active' : ''}`} onClick={() => setView({ viewMode: 'grid' })}>Grid</button>
            <button className={`pill ${view.viewMode === 'list' ? 'active' : ''}`} onClick={() => setView({ viewMode: 'list' })}>List</button>
          </div>
        </div>

        <div className="sidebar-row">
          <span className="sidebar-row-label">Style</span>
          <div className="sidebar-pills">
            {(['standard', 'compact', 'poster'] as const).map((style) => (
              <button
                key={style}
                className={`pill ${view.displayStyle === style ? 'active' : ''}`}
                onClick={() => applyDisplayStyle(style)}
              >
                {style === 'standard' ? 'Standard' : style === 'compact' ? 'Compact' : 'Poster'}
              </button>
            ))}
          </div>
        </div>

        <div className="sidebar-row">
          <span className="sidebar-row-label">Aspect</span>
          <div className="sidebar-pills">
            {(['original', 'square', 'portrait'] as const).map((a) => (
              <button key={a} className={`pill ${view.aspectMode === a ? 'active' : ''}`} onClick={() => setView({ aspectMode: a })}>
                {a === 'original' ? 'Original' : a === 'square' ? '1:1' : '2:3'}
              </button>
            ))}
          </div>
        </div>

        <div className="sidebar-row sidebar-size-row" role="group" aria-label="Thumbnail size control">
          <span className="sidebar-row-label">Size</span>
          <input
            type="range"
            className="sidebar-slider"
            aria-label="Thumbnail size"
            min={MIN_THUMB_SIZE}
            max={MAX_THUMB_SIZE}
            step={THUMB_ZOOM_STEP}
            value={view.thumbSize}
            onChange={(e) => setView({ thumbSize: parseInt(e.target.value, 10) })}
          />
          <span className="sidebar-row-value">
            {view.thumbSize >= MAX_THUMB_SIZE ? '1 column' : `${view.thumbSize}px`}
          </span>
        </div>
      </div>

      <div className="sidebar-bottom">
        <label className="sidebar-toggle" style={{ marginBottom: '0.5rem' }}>
          <input type="checkbox" checked={perfEnabled} onChange={(e) => setPerfEnabled(e.target.checked)} />
          <span>Perf overlay</span>
        </label>
        {perfEnabled && (
          <div className="sidebar-meta" style={{ marginBottom: '0.5rem' }}>
            Search avg: {perfStats.avgSearchMs}ms / last: {perfStats.lastSearchMs}ms / count: {perfStats.searchCount}
          </div>
        )}
        <button className="sidebar-link" onClick={() => setShowSettings(true)}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
          </svg>
          Settings
        </button>
      </div>
      </aside>
    </>
  );
}

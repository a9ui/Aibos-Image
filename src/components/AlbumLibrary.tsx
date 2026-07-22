'use client';

import React, { useMemo, useRef, useState } from 'react';
import {
  FolderOpen,
  Image as ImageIcon,
  Images,
  LibraryBig,
  Pin,
  PinOff,
  Plus,
  RefreshCw,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-react';

import { useDialogFocus } from '../lib/useDialogFocus';
import { useAlbumStore } from '../store/AlbumContext';
import { useImageStore } from '../store/ImageContext';

export default function AlbumLibrary() {
  const {
    document,
    albums,
    activeSource,
    loading,
    error,
    libraryOpen,
    setLibraryOpen,
    createAlbum,
    updateAlbum,
    deleteAlbum,
    openAlbum,
    refreshAlbums,
  } = useAlbumStore();
  const { selectedIds } = useImageStore();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const [name, setName] = useState('');
  const [editingId, setEditingId] = useState('');
  const [editingName, setEditingName] = useState('');
  const [confirmDeleteId, setConfirmDeleteId] = useState('');
  const [busy, setBusy] = useState(false);

  useDialogFocus({
    open: libraryOpen,
    dialogRef,
    initialFocusRef: closeRef,
    onEscape: () => setLibraryOpen(false),
  });

  const orderedAlbums = useMemo(() => {
    const recentOrder = new Map((document?.recentAlbumIds ?? []).map((id, index) => [id, index]));
    return [...albums].sort((left, right) => {
      if (left.pinned !== right.pinned) return left.pinned ? -1 : 1;
      const leftRecent = recentOrder.get(left.id) ?? Number.MAX_SAFE_INTEGER;
      const rightRecent = recentOrder.get(right.id) ?? Number.MAX_SAFE_INTEGER;
      if (leftRecent !== rightRecent) return leftRecent - rightRecent;
      return left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
    });
  }, [albums, document?.recentAlbumIds]);

  if (!libraryOpen) return null;

  const run = async (operation: () => Promise<unknown>) => {
    setBusy(true);
    try { await operation(); } finally { setBusy(false); }
  };

  const create = async (event: React.FormEvent) => {
    event.preventDefault();
    const normalized = name.trim();
    if (!normalized) return;
    await run(async () => {
      const album = await createAlbum(normalized);
      if (album) setName('');
    });
  };

  return (
    <div className="album-dialog-overlay">
      <button className="album-dialog-backdrop" title="Close Album library" aria-label="Close Album library" onClick={() => setLibraryOpen(false)} />
      <div ref={dialogRef} className="album-dialog" role="dialog" aria-modal="true" aria-labelledby="album-library-title" tabIndex={-1}>
        <div className="album-dialog-header">
          <div className="album-dialog-title-lockup">
            <span className="album-dialog-title-icon" aria-hidden="true"><LibraryBig size={21} /></span>
            <div>
              <h2 id="album-library-title">Albums</h2>
              <p>One shared library across Browser and WPF</p>
            </div>
          </div>
          <button ref={closeRef} className="icon-btn" title="Close Album library" aria-label="Close Album library" onClick={() => setLibraryOpen(false)}><X size={18} /></button>
        </div>

        <div className="album-dialog-stats" aria-label="Album library summary">
          <span><strong>{orderedAlbums.length}</strong> Albums</span>
          <span><strong>{selectedIds.length}</strong> Selected</span>
          <span className="album-shared-status"><ShieldCheck size={14} aria-hidden="true" /> Shared store</span>
        </div>

        <form className="album-create-card" onSubmit={create}>
          <div className="album-create-copy">
            <span>New collection</span>
            <strong>Create once, use everywhere</strong>
          </div>
          <div className="album-create-row">
            <input value={name} maxLength={120} onChange={(event) => setName(event.target.value)} placeholder="New Album name" aria-label="New Album name" />
            <button className="btn-primary" title="Create a shared Album" disabled={busy || !name.trim()}><Plus size={15} aria-hidden="true" />Create</button>
            <button type="button" className="btn-secondary album-refresh-button" title="Reload the shared Album library" aria-label="Refresh Albums" onClick={() => void run(refreshAlbums)} disabled={busy}>
              <RefreshCw size={15} />
            </button>
          </div>
        </form>

        {error && <p className="album-error" role="alert">{error}</p>}
        {loading ? <div className="album-empty" role="status"><span className="album-empty-icon is-loading"><RefreshCw size={22} /></span><strong>Loading Albums…</strong><span>Reading the shared Browser/WPF library.</span></div> : orderedAlbums.length === 0 ? (
          <div className="album-empty"><span className="album-empty-icon" aria-hidden="true"><Images size={24} /></span><strong>Your shared library is ready</strong><span>Create an Album, then add selected images from either app.</span></div>
        ) : (
          <div className="album-list">
            {orderedAlbums.map((album) => {
              const isActive = activeSource?.album.id === album.id;
              const coverCandidate = isActive
                ? activeSource.members.find((member) => selectedIds.includes(member.imagePath))
                : undefined;
              return (
                <section key={album.id} className={`album-row${isActive ? ' active' : ''}`} aria-label={`Album ${album.name}`}>
                  <button className="album-open" title={`Open ${album.name} in the gallery`} disabled={busy || loading} onClick={() => void run(() => openAlbum(album.id))}>
                    <span className="album-cover-chip" aria-hidden="true"><FolderOpen size={19} /></span>
                    <span>
                      <span className="album-row-name"><strong>{album.name}</strong>{isActive && <em>Open</em>}</span>
                      <small><span>{album.members.length} items</span><span>Revision {album.revision}</span></small>
                    </span>
                  </button>
                  <div className="album-row-actions">
                    <button className="icon-btn" title={album.pinned ? 'Unpin Album' : 'Pin Album'} disabled={busy} onClick={() => void run(() => updateAlbum(album.id, { pinned: !album.pinned }))}>
                      {album.pinned ? <PinOff size={16} /> : <Pin size={16} />}
                    </button>
                    {coverCandidate && (
                      <button className="icon-btn" title="Use selected image as Album cover" disabled={busy} onClick={() => void run(() => updateAlbum(album.id, { coverMemberId: coverCandidate.memberId }))}>
                        <ImageIcon size={16} />
                      </button>
                    )}
                    <button className="btn-link" title={`Rename ${album.name}`} disabled={busy} onClick={() => {
                      setEditingId(album.id);
                      setEditingName(album.name);
                      setConfirmDeleteId('');
                    }}>Rename</button>
                    <button className="icon-btn danger" title="Delete Album only" disabled={busy} onClick={() => setConfirmDeleteId(album.id)}><Trash2 size={16} /></button>
                  </div>
                  {editingId === album.id && (
                    <form className="album-inline-form" onSubmit={(event) => {
                      event.preventDefault();
                      if (!editingName.trim()) return;
                      void run(async () => {
                        if (await updateAlbum(album.id, { name: editingName.trim() })) setEditingId('');
                      });
                    }}>
                      <input value={editingName} maxLength={120} onChange={(event) => setEditingName(event.target.value)} aria-label={`Rename ${album.name}`} />
                      <button className="btn-primary" title="Save the Album name" disabled={busy || !editingName.trim()}>Save</button>
                      <button type="button" className="btn-secondary" title="Cancel renaming" onClick={() => setEditingId('')}>Cancel</button>
                    </form>
                  )}
                  {confirmDeleteId === album.id && (
                    <div className="album-delete-confirm" role="alert">
                      <span>Delete this Album? Source images are not recycled.</span>
                      <button className="btn-danger" title="Delete the Album without recycling source images" disabled={busy} onClick={() => void run(async () => {
                        if (await deleteAlbum(album.id)) setConfirmDeleteId('');
                      })}>Delete Album</button>
                      <button className="btn-secondary" title="Cancel Album deletion" onClick={() => setConfirmDeleteId('')}>Cancel</button>
                    </div>
                  )}
                </section>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}

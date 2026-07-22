'use client';

import React, { useRef, useState } from 'react';
import { FolderPlus, X } from 'lucide-react';

import { useDialogFocus } from '../lib/useDialogFocus';
import { useAlbumStore } from '../store/AlbumContext';

export default function AlbumPicker() {
  const {
    albums,
    loading,
    error,
    pickerOpen,
    pickerTargetPaths,
    setPickerOpen,
    createAlbum,
    addMembers,
  } = useAlbumStore();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const [name, setName] = useState('');
  const [busyId, setBusyId] = useState('');

  useDialogFocus({
    open: pickerOpen,
    dialogRef,
    initialFocusRef: closeRef,
    onEscape: () => setPickerOpen(false),
  });

  if (!pickerOpen) return null;

  const add = async (albumId: string) => {
    setBusyId(albumId);
    try {
      if (await addMembers(albumId, pickerTargetPaths)) setPickerOpen(false);
    } finally {
      setBusyId('');
    }
  };

  const createAndAdd = async (event: React.FormEvent) => {
    event.preventDefault();
    const normalized = name.trim();
    if (!normalized) return;
    setBusyId('create');
    try {
      const album = await createAlbum(normalized);
      if (album && await addMembers(album.id, pickerTargetPaths)) {
        setName('');
        setPickerOpen(false);
      }
    } finally {
      setBusyId('');
    }
  };

  return (
    <div className="album-dialog-overlay">
      <button className="album-dialog-backdrop" aria-label="Close Add to Album" onClick={() => setPickerOpen(false)} />
      <div ref={dialogRef} className="album-dialog album-picker" role="dialog" aria-modal="true" aria-labelledby="album-picker-title" tabIndex={-1}>
        <div className="album-dialog-header">
          <div>
            <h2 id="album-picker-title">Add to Album</h2>
            <p>{pickerTargetPaths.length} selected image(s)</p>
          </div>
          <button ref={closeRef} className="icon-btn" aria-label="Close Add to Album" onClick={() => setPickerOpen(false)}><X size={18} /></button>
        </div>
        {error && <p className="album-error" role="alert">{error}</p>}
        <div className="album-picker-list">
          {albums.map((album) => (
            <button key={album.id} className="album-picker-option" disabled={Boolean(busyId) || loading} onClick={() => void add(album.id)}>
              <FolderPlus size={17} />
              <span>{album.name}<small>{album.members.length} member(s)</small></span>
              {busyId === album.id && <span>Adding…</span>}
            </button>
          ))}
          {albums.length === 0 && <p className="album-empty">Create the first Album below.</p>}
        </div>
        <form className="album-create-row" onSubmit={createAndAdd}>
          <input value={name} maxLength={120} onChange={(event) => setName(event.target.value)} placeholder="New Album name" aria-label="New Album name" />
          <button className="btn-primary" disabled={Boolean(busyId) || loading || !name.trim()}>Create & Add</button>
        </form>
      </div>
    </div>
  );
}

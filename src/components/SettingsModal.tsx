'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useImageStore } from '../store/ImageContext';
import {
  DEFAULT_KEY_BINDINGS,
  DEFAULT_THUMBNAIL_STATUS_BORDERS,
  type KeyBindings,
  type ThumbnailStatusBorderSettings,
  type ThumbnailStatusBorderSettingsPatch,
} from '../lib/types';
import { clampModalEdgeRatio } from '../lib/modalNavigation';
import { useDialogFocus } from '../lib/useDialogFocus';
import {
  getKeyBindingConflicts,
  isUnboundKeyBinding,
} from '../lib/keyBindings';
import RuntimeDiagnosticsSection from './RuntimeDiagnosticsSection';
import statusBorderStyles from './SettingsModalStatusBorders.module.css';

const KEY_LABELS: Record<keyof KeyBindings, string> = {
  nextImage: 'Next image',
  prevImage: 'Previous image',
  toggleFavorite: 'Toggle favorite',
  decreaseFavorite: 'Decrease favorite',
  deleteImage: 'Delete image',
  closeModal: 'Close modal',
  flipHorizontal: 'Flip horizontal',
  enhanceImage: 'Enhance image',
  toggleFilmstrip: 'Toggle modal filmstrip',
  addToAlbum: 'Add selection to Album',
  zoomIn: 'Zoom in',
  zoomOut: 'Zoom out',
  zoomReset: 'Reset zoom',
};

export default function SettingsModal() {
  const {
    showSettings,
    setShowSettings,
    keyBindings,
    setKeyBindings,
    confirmBeforeDelete,
    setConfirmBeforeDelete,
    thumbnailStatusBorders,
    setThumbnailStatusBorders,
    view,
    setView,
  } = useImageStore();

  const [recording, setRecording] = useState<keyof KeyBindings | null>(null);
  const [draftKeyBindings, setDraftKeyBindings] = useState<KeyBindings>(keyBindings);
  const [confirmDraft, setConfirmDraft] = useState(confirmBeforeDelete);
  const [thumbnailBordersDraft, setThumbnailBordersDraft] = useState<ThumbnailStatusBorderSettings>(thumbnailStatusBorders);
  const [failedConfirmValue, setFailedConfirmValue] = useState<boolean | null>(null);
  const [keyBindingsSaveError, setKeyBindingsSaveError] = useState('');
  const [confirmSaveError, setConfirmSaveError] = useState('');
  const [thumbnailBordersSaveError, setThumbnailBordersSaveError] = useState('');
  const [savingKeyBindings, setSavingKeyBindings] = useState(false);
  const [savingConfirm, setSavingConfirm] = useState(false);
  const [savingThumbnailBorders, setSavingThumbnailBorders] = useState(false);
  const modalEdgePercent = Math.round(clampModalEdgeRatio(view.modalEdgeRatio) * 100);
  const panelRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);
  const keyBindingsSaveAttemptRef = useRef(0);
  const confirmSaveAttemptRef = useRef(0);
  const thumbnailBordersSaveAttemptRef = useRef(0);
  const dirtyKeyBindingActionsRef = useRef<Set<keyof KeyBindings>>(new Set());
  const dirtyThumbnailBorderStatusesRef = useRef<Set<keyof ThumbnailStatusBorderSettings>>(new Set());
  const close = useCallback(() => {
    keyBindingsSaveAttemptRef.current += 1;
    confirmSaveAttemptRef.current += 1;
    thumbnailBordersSaveAttemptRef.current += 1;
    setShowSettings(false);
    setRecording(null);
    setDraftKeyBindings(keyBindings);
    setConfirmDraft(confirmBeforeDelete);
    setThumbnailBordersDraft(thumbnailStatusBorders);
    setFailedConfirmValue(null);
    setKeyBindingsSaveError('');
    setConfirmSaveError('');
    setThumbnailBordersSaveError('');
    dirtyKeyBindingActionsRef.current.clear();
    dirtyThumbnailBorderStatusesRef.current.clear();
  }, [confirmBeforeDelete, keyBindings, setShowSettings, thumbnailStatusBorders]);

  useEffect(() => {
    if (!showSettings) return;
    const dirtyActions = dirtyKeyBindingActionsRef.current;
    if (dirtyActions.size === 0) {
      setDraftKeyBindings(keyBindings);
      setKeyBindingsSaveError('');
      setRecording(null);
      return;
    }
    setDraftKeyBindings((current) => {
      const merged = { ...keyBindings };
      for (const action of dirtyActions) merged[action] = current[action];
      return merged;
    });
  }, [keyBindings, showSettings]);

  useEffect(() => {
    if (!showSettings) return;
    setConfirmDraft(confirmBeforeDelete);
    setFailedConfirmValue(null);
    setConfirmSaveError('');
  }, [confirmBeforeDelete, showSettings]);

  useEffect(() => {
    if (!showSettings) return;
    const dirtyStatuses = dirtyThumbnailBorderStatusesRef.current;
    setThumbnailBordersDraft((current) => ({
      favorite: dirtyStatuses.has('favorite') ? current.favorite : thumbnailStatusBorders.favorite,
      enhanced: dirtyStatuses.has('enhanced') ? current.enhanced : thumbnailStatusBorders.enhanced,
    }));
    setThumbnailBordersSaveError('');
  }, [showSettings, thumbnailStatusBorders]);

  useDialogFocus({
    open: showSettings,
    dialogRef: panelRef,
    initialFocusRef: closeButtonRef,
    onEscape: close,
  });

  const conflicts = useMemo(
    () => getKeyBindingConflicts(draftKeyBindings),
    [draftKeyBindings]
  );
  const conflictByAction = useMemo(() => {
    const next = new Map<keyof KeyBindings, Array<keyof KeyBindings>>();
    for (const conflict of conflicts) {
      for (const action of conflict.actions) {
        next.set(action, conflict.actions.filter((item) => item !== action));
      }
    }
    return next;
  }, [conflicts]);
  const hasBindingConflicts = conflicts.length > 0;

  const handleKeyCapture = useCallback((e: React.KeyboardEvent, action: keyof KeyBindings) => {
    e.preventDefault();
    e.stopPropagation();
    setDraftKeyBindings((current) => ({ ...current, [action]: e.key }));
    dirtyKeyBindingActionsRef.current.add(action);
    setKeyBindingsSaveError('');
    setRecording(null);
  }, []);
  const saveKeyBindings = useCallback(async () => {
    if (hasBindingConflicts || savingKeyBindings) return;
    const attempt = keyBindingsSaveAttemptRef.current + 1;
    keyBindingsSaveAttemptRef.current = attempt;
    setSavingKeyBindings(true);
    setKeyBindingsSaveError('');
    try {
      const result = await setKeyBindings(draftKeyBindings);
      if (keyBindingsSaveAttemptRef.current !== attempt) return;
      if (!result.ok) {
        setKeyBindingsSaveError(`${result.error} Draft preserved; retry when ready.`);
        return;
      }
      close();
    } catch {
      if (keyBindingsSaveAttemptRef.current !== attempt) return;
      setKeyBindingsSaveError('Could not reach the local settings service. Draft preserved; retry when ready.');
    } finally {
      setSavingKeyBindings(false);
    }
  }, [close, draftKeyBindings, hasBindingConflicts, savingKeyBindings, setKeyBindings]);
  const resetKeyBindings = useCallback(() => {
    setDraftKeyBindings(DEFAULT_KEY_BINDINGS);
    dirtyKeyBindingActionsRef.current = new Set(Object.keys(DEFAULT_KEY_BINDINGS) as Array<keyof KeyBindings>);
    setKeyBindingsSaveError('');
    setRecording(null);
  }, []);

  const saveConfirmBeforeDelete = useCallback(async (nextValue: boolean) => {
    if (savingConfirm) return;
    const savedValue = confirmBeforeDelete;
    const attempt = confirmSaveAttemptRef.current + 1;
    confirmSaveAttemptRef.current = attempt;
    setConfirmDraft(nextValue);
    setFailedConfirmValue(null);
    setConfirmSaveError('');
    setSavingConfirm(true);
    try {
      const result = await setConfirmBeforeDelete(nextValue);
      if (confirmSaveAttemptRef.current !== attempt) return;
      if (!result.ok) {
        setConfirmDraft(savedValue);
        setFailedConfirmValue(nextValue);
        setConfirmSaveError(`${result.error} The saved value was restored.`);
        return;
      }
      setConfirmDraft(nextValue);
    } catch {
      if (confirmSaveAttemptRef.current !== attempt) return;
      setConfirmDraft(savedValue);
      setFailedConfirmValue(nextValue);
      setConfirmSaveError('Could not reach the local settings service. The saved value was restored.');
    } finally {
      setSavingConfirm(false);
    }
  }, [confirmBeforeDelete, savingConfirm, setConfirmBeforeDelete]);

  const updateThumbnailBorderDraft = useCallback((
    status: keyof ThumbnailStatusBorderSettings,
    patch: Partial<ThumbnailStatusBorderSettings[typeof status]>,
  ) => {
    dirtyThumbnailBorderStatusesRef.current.add(status);
    setThumbnailBordersSaveError('');
    setThumbnailBordersDraft((current) => ({
      ...current,
      [status]: { ...current[status], ...patch },
    }));
  }, []);

  const resetThumbnailBorders = useCallback(() => {
    dirtyThumbnailBorderStatusesRef.current = new Set(['favorite', 'enhanced']);
    setThumbnailBordersSaveError('');
    setThumbnailBordersDraft({
      favorite: { ...DEFAULT_THUMBNAIL_STATUS_BORDERS.favorite },
      enhanced: { ...DEFAULT_THUMBNAIL_STATUS_BORDERS.enhanced },
    });
  }, []);

  const saveThumbnailBorders = useCallback(async () => {
    if (savingThumbnailBorders) return;
    const dirtyStatuses = [...dirtyThumbnailBorderStatusesRef.current];
    if (dirtyStatuses.length === 0) return;
    const patch: ThumbnailStatusBorderSettingsPatch = {};
    for (const status of dirtyStatuses) patch[status] = thumbnailBordersDraft[status];
    const attempt = thumbnailBordersSaveAttemptRef.current + 1;
    thumbnailBordersSaveAttemptRef.current = attempt;
    setSavingThumbnailBorders(true);
    setThumbnailBordersSaveError('');
    try {
      const result = await setThumbnailStatusBorders(patch);
      if (thumbnailBordersSaveAttemptRef.current !== attempt) return;
      if (!result.ok) {
        setThumbnailBordersSaveError(`${result.error} Draft preserved; retry when ready.`);
        return;
      }
      dirtyThumbnailBorderStatusesRef.current.clear();
    } catch {
      if (thumbnailBordersSaveAttemptRef.current !== attempt) return;
      setThumbnailBordersSaveError('Could not reach the local settings service. Draft preserved; retry when ready.');
    } finally {
      setSavingThumbnailBorders(false);
    }
  }, [savingThumbnailBorders, setThumbnailStatusBorders, thumbnailBordersDraft]);

  if (!showSettings) return null;

  return (
    <div className="settings-overlay">
      <div data-testid="settings-backdrop" className="settings-backdrop" aria-hidden="true" onClick={close} />
      <div ref={panelRef} className="settings-panel" role="dialog" aria-modal="true" aria-labelledby="settings-title" tabIndex={-1}>
        <div className="settings-header">
          <h2 id="settings-title">Settings</h2>
          <button ref={closeButtonRef} className="icon-btn" onClick={close} title="Close" aria-label="Close settings">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <line x1="18" y1="6" x2="6" y2="18" />
              <line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>
        </div>

        <div className="settings-body">
          <RuntimeDiagnosticsSection />

          <h3 style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', marginBottom: '1rem' }}>
            Behavior
          </h3>
          <div className="setting-row">
            <span className="setting-label">Confirm before delete</span>
            <label className="sidebar-toggle">
              <input
                aria-label="Confirm before delete"
                aria-describedby={confirmSaveError ? 'confirm-delete-save-error' : undefined}
                type="checkbox"
                checked={confirmDraft}
                disabled={savingConfirm}
                onChange={(e) => { void saveConfirmBeforeDelete(e.target.checked); }}
              />
              <span>{savingConfirm ? 'Saving…' : confirmDraft ? 'Enabled' : 'Disabled'}</span>
            </label>
          </div>
          {confirmSaveError && failedConfirmValue !== null && (
            <div id="confirm-delete-save-error" className="settings-save-error" role="alert">
              <span>{confirmSaveError}</span>
              <button
                type="button"
                className="sidebar-link"
                disabled={savingConfirm}
                onClick={() => { void saveConfirmBeforeDelete(failedConfirmValue); }}
              >
                {savingConfirm ? 'Retrying…' : 'Retry delete confirmation change'}
              </button>
            </div>
          )}
          <div className="setting-row">
            <span className="setting-label">Unseen dots</span>
            <label className="sidebar-toggle">
              <input
                aria-label="Show unseen markers"
                type="checkbox"
                checked={view.showUnseenMarkers}
                onChange={(e) => setView({ showUnseenMarkers: e.target.checked })}
              />
              <span>{view.showUnseenMarkers ? 'Enabled' : 'Disabled'}</span>
            </label>
          </div>
          <h3 style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', margin: '1rem 0 0.35rem' }}>
            Thumbnail status borders
          </h3>
          {(['favorite', 'enhanced'] as const).map((status) => {
            const label = status === 'favorite' ? 'Favorite' : 'AI enhanced';
            const preference = thumbnailBordersDraft[status];
            return (
              <div className="setting-row" key={status}>
                <span className="setting-label">{label} thumbnail border</span>
                <div className={statusBorderStyles.control}>
                  <label className="sidebar-toggle">
                    <input
                      aria-label={`Show ${status} thumbnail border`}
                      type="checkbox"
                      checked={preference.enabled}
                      disabled={savingThumbnailBorders}
                      onChange={(event) => updateThumbnailBorderDraft(status, { enabled: event.target.checked })}
                    />
                    <span>{preference.enabled ? 'Enabled' : 'Disabled'}</span>
                  </label>
                  <input
                    className={statusBorderStyles.colorInput}
                    aria-label={`${label} thumbnail border color`}
                    type="color"
                    value={preference.color}
                    disabled={!preference.enabled || savingThumbnailBorders}
                    onChange={(event) => updateThumbnailBorderDraft(status, { color: event.target.value })}
                  />
                  <code className={statusBorderStyles.colorValue}>
                    {preference.color}
                  </code>
                </div>
              </div>
            );
          })}
          <div className="sidebar-actions" style={{ marginTop: '0.75rem' }}>
            <button type="button" className="sidebar-link" onClick={resetThumbnailBorders} disabled={savingThumbnailBorders}>
              Reset border defaults
            </button>
            <button
              type="button"
              className="sidebar-link"
              onClick={() => { void saveThumbnailBorders(); }}
              disabled={savingThumbnailBorders}
            >
              {savingThumbnailBorders
                ? 'Saving thumbnail borders…'
                : thumbnailBordersSaveError
                  ? 'Retry save thumbnail borders'
                  : 'Save thumbnail borders'}
            </button>
          </div>
          {thumbnailBordersSaveError && (
            <p className="settings-save-error" role="alert">
              {thumbnailBordersSaveError}
            </p>
          )}
          <div className="setting-row">
            <span className="setting-label">Modal edge navigation zone</span>
            <div className="setting-range-control">
              <input
                type="range"
                min="10"
                max="40"
                step="1"
                value={modalEdgePercent}
                onChange={(e) => setView({ modalEdgeRatio: clampModalEdgeRatio(Number(e.target.value) / 100) })}
              />
              <span className="setting-value">{modalEdgePercent}%</span>
            </div>
          </div>

          <h3 style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', margin: '1rem 0' }}>
            Key bindings
          </h3>
          {(Object.keys(KEY_LABELS) as (keyof KeyBindings)[]).map((action) => {
            const conflictsWith = conflictByAction.get(action) ?? [];
            const isUnbound = isUnboundKeyBinding(draftKeyBindings[action]);
            const errorId = `key-binding-error-${action}`;
            return (
            <div key={action} className="setting-row">
              <span className="setting-label">{KEY_LABELS[action]}</span>
              <button
                type="button"
                className={`setting-key ${recording === action ? 'recording' : ''}`}
                onClick={() => setRecording(action)}
                onKeyDown={recording === action ? (e) => handleKeyCapture(e, action) : undefined}
                aria-label={`${KEY_LABELS[action]} binding`}
                aria-invalid={conflictsWith.length > 0 || undefined}
                aria-describedby={conflictsWith.length > 0 || isUnbound ? errorId : undefined}
              >
                {recording === action ? 'Press key...' : formatKey(draftKeyBindings[action])}
              </button>
              {conflictsWith.length > 0 ? (
                <p id={errorId} className="sidebar-error" role="alert">
                  Also assigned to {conflictsWith.map((item) => KEY_LABELS[item]).join(', ')}.
                </p>
              ) : isUnbound ? (
                <p id={errorId} className="sidebar-error" role="status">
                  No collision-free migration key was available. Choose a key to enable this action.
                </p>
              ) : null}
            </div>
            );
          })}
          <div className="sidebar-actions" style={{ marginTop: '0.75rem' }}>
            <button type="button" className="sidebar-link" onClick={resetKeyBindings} disabled={savingKeyBindings}>
              Reset to defaults
            </button>
            <button
              type="button"
              className="sidebar-link"
              onClick={() => { void saveKeyBindings(); }}
              disabled={hasBindingConflicts || savingKeyBindings}
              aria-describedby={keyBindingsSaveError ? 'key-bindings-save-error' : undefined}
            >
              {savingKeyBindings
                ? 'Saving key bindings…'
                : keyBindingsSaveError
                  ? 'Retry save key bindings'
                  : 'Save key bindings'}
            </button>
          </div>
          {keyBindingsSaveError && (
            <p id="key-bindings-save-error" className="settings-save-error" role="alert">
              {keyBindingsSaveError}
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

function formatKey(key: string): string {
  if (isUnboundKeyBinding(key)) return 'Unassigned';
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

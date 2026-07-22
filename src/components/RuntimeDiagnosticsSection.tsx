'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';

import {
  formatRuntimeDiagnosticsCopy,
  normalizeRuntimeDiagnostics,
  runtimeSourceStateLabel,
  shortRuntimeRevision,
  type RuntimeDiagnostics,
} from '../lib/runtimeDiagnostics';

type RuntimeLoadState =
  | { status: 'idle' | 'loading' | 'error' }
  | { status: 'ready'; runtime: RuntimeDiagnostics };

type CopyStatus = 'idle' | 'copying' | 'success' | 'error';

export default function RuntimeDiagnosticsSection() {
  const [runtimeState, setRuntimeState] = useState<RuntimeLoadState>({ status: 'idle' });
  const [copyStatus, setCopyStatus] = useState<CopyStatus>('idle');
  const runtimeAbortRef = useRef<AbortController | null>(null);
  const runtimeRequestSequenceRef = useRef(0);
  const copySequenceRef = useRef(0);

  const loadRuntime = useCallback(async () => {
    const requestSequence = ++runtimeRequestSequenceRef.current;
    copySequenceRef.current += 1;
    runtimeAbortRef.current?.abort();
    const controller = new AbortController();
    runtimeAbortRef.current = controller;
    setRuntimeState({ status: 'loading' });
    setCopyStatus('idle');

    try {
      const response = await fetch('/api/runtime', {
        cache: 'no-store',
        signal: controller.signal,
      });
      if (!response.ok) throw new Error('Runtime request failed.');
      const normalized = normalizeRuntimeDiagnostics(await response.json());
      if (!normalized.ok) throw new Error('Runtime payload was invalid.');
      if (controller.signal.aborted || requestSequence !== runtimeRequestSequenceRef.current) return;
      setRuntimeState({ status: 'ready', runtime: normalized.value });
    } catch {
      if (controller.signal.aborted || requestSequence !== runtimeRequestSequenceRef.current) return;
      setRuntimeState({ status: 'error' });
    } finally {
      if (runtimeAbortRef.current === controller) runtimeAbortRef.current = null;
    }
  }, []);

  useEffect(() => {
    void loadRuntime();
    return () => {
      runtimeRequestSequenceRef.current += 1;
      runtimeAbortRef.current?.abort();
      runtimeAbortRef.current = null;
      copySequenceRef.current += 1;
    };
  }, [loadRuntime]);

  const copyRuntimeDiagnostics = useCallback(async () => {
    if (runtimeState.status !== 'ready') return;
    const copySequence = ++copySequenceRef.current;
    setCopyStatus('copying');
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable.');
      await navigator.clipboard.writeText(formatRuntimeDiagnosticsCopy(
        runtimeState.runtime,
        navigator.userAgent,
      ));
      if (copySequence !== copySequenceRef.current) return;
      setCopyStatus('success');
    } catch {
      if (copySequence !== copySequenceRef.current) return;
      setCopyStatus('error');
    }
  }, [runtimeState]);

  return (
    <section className="settings-runtime" aria-labelledby="settings-runtime-title">
      <div className="settings-section-header">
        <h3 id="settings-runtime-title">Runtime / Version</h3>
        <button
          type="button"
          className="sidebar-link settings-runtime-reload"
          onClick={() => void loadRuntime()}
          disabled={runtimeState.status === 'loading'}
        >
          {runtimeState.status === 'loading' ? 'Loading…' : 'Reload'}
        </button>
      </div>

      {runtimeState.status === 'loading' && (
        <p className="settings-runtime-message" role="status" aria-live="polite">
          Loading runtime details…
        </p>
      )}
      {runtimeState.status === 'error' && (
        <p className="settings-runtime-message error" role="alert">
          Runtime details could not be loaded. Use Reload to try again.
        </p>
      )}
      {runtimeState.status === 'ready' && (
        <>
          <dl className="settings-runtime-grid">
            <div className="settings-runtime-row">
              <dt>Product</dt>
              <dd>{runtimeState.runtime.product}</dd>
            </div>
            <div className="settings-runtime-row">
              <dt>Source revision</dt>
              <dd title={runtimeState.runtime.sourceRevision ?? undefined}>
                {shortRuntimeRevision(runtimeState.runtime.sourceRevision)}
              </dd>
            </div>
            <div className="settings-runtime-row">
              <dt>Source state</dt>
              <dd
                aria-label="Source state"
                className={runtimeState.runtime.sourceDirty === true
                  ? 'is-dirty'
                  : runtimeState.runtime.sourceDirty === false
                    ? 'is-clean'
                    : undefined}
              >
                {runtimeSourceStateLabel(runtimeState.runtime.sourceDirty)}
              </dd>
            </div>
            <div className="settings-runtime-row">
              <dt>Build ID</dt>
              <dd title={runtimeState.runtime.buildId ?? undefined}>
                {runtimeState.runtime.buildId ?? 'Unavailable'}
              </dd>
            </div>
            <div className="settings-runtime-row">
              <dt>Build completed (UTC)</dt>
              <dd>
                {runtimeState.runtime.buildCompletedAtUtc
                  ? <time dateTime={runtimeState.runtime.buildCompletedAtUtc}>{runtimeState.runtime.buildCompletedAtUtc}</time>
                  : 'Unavailable'}
              </dd>
            </div>
            <div className="settings-runtime-row">
              <dt>Local server</dt>
              <dd>{runtimeState.runtime.serverPort ? `127.0.0.1:${runtimeState.runtime.serverPort}` : 'Unavailable'}</dd>
            </div>
          </dl>
          <div className="settings-runtime-actions">
            <button
              type="button"
              className="sidebar-link"
              onClick={() => void copyRuntimeDiagnostics()}
              disabled={copyStatus === 'copying'}
            >
              {copyStatus === 'copying' ? 'Copying…' : 'Copy diagnostics'}
            </button>
            <p
              className={`settings-runtime-copy-status ${copyStatus === 'error' ? 'error' : ''}`}
              role={copyStatus === 'error' ? 'alert' : 'status'}
              aria-live="polite"
              aria-atomic="true"
            >
              {copyStatus === 'success'
                ? 'Diagnostics copied.'
                : copyStatus === 'error'
                  ? 'Could not copy diagnostics.'
                  : ''}
            </p>
          </div>
        </>
      )}
    </section>
  );
}

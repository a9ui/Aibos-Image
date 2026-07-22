import React from 'react';

export interface ScanProgressState {
  processed: number;
  total: number;
  newFiles: number;
  stage?: 'preparing' | 'scanning' | 'complete' | 'error' | string;
  message?: string;
}

export interface ScanProgressPresentation {
  processed: number;
  total: number;
  percent: number;
  unit: 'folders' | 'files' | 'overall';
  isDeterminate: boolean;
  message: string;
  statusText: string;
  isError: boolean;
}

export function getScanProgressPresentation(progress: ScanProgressState): ScanProgressPresentation {
  const total = Math.max(0, progress.total || 0);
  const isDeterminate = total > 0;
  const processed = isDeterminate
    ? Math.min(total, Math.max(0, progress.processed || 0))
    : Math.max(0, progress.processed || 0);
  const percent = isDeterminate ? Math.round((processed / total) * 100) : 0;
  const unit = progress.message?.startsWith('[')
    ? 'overall'
    : progress.stage === 'preparing'
      ? 'folders'
      : 'files';
  const isError = progress.stage === 'error';
  const message = progress.message ?? (
    isError
      ? 'Scan failed.'
      : progress.stage === 'complete'
        ? 'Scan complete.'
        : progress.stage === 'preparing'
          ? 'Preparing file list...'
          : 'Scanning files...'
  );
  const countText = isDeterminate
    ? `${processed.toLocaleString()} of ${total.toLocaleString()} ${unit}, ${percent}% complete.`
    : 'Progress total is not available yet.';
  const newFilesText = progress.stage !== 'preparing' && progress.newFiles > 0
    ? ` ${progress.newFiles.toLocaleString()} new ${progress.newFiles === 1 ? 'file' : 'files'} found.`
    : '';

  return {
    processed,
    total,
    percent,
    unit,
    isDeterminate,
    message,
    statusText: `${message} ${countText}${newFilesText}`,
    isError,
  };
}

export function ScanProgressStatus({
  progress,
  onCancel,
}: {
  progress: ScanProgressState;
  onCancel?: () => void;
}) {
  const presentation = getScanProgressPresentation(progress);

  return (
    <div className="progress-container">
      <div className="progress-label" aria-hidden="true">
        <span>
          {presentation.processed.toLocaleString()} / {presentation.total.toLocaleString()} {presentation.unit}
          {progress.stage !== 'preparing' && progress.newFiles > 0 && ` (${progress.newFiles} new)`}
        </span>
        <span>{presentation.percent}%</span>
      </div>
      <div
        className="progress-bar"
        role="progressbar"
        aria-label="Scan progress"
        aria-valuemin={presentation.isDeterminate ? 0 : undefined}
        aria-valuemax={presentation.isDeterminate ? presentation.total : undefined}
        aria-valuenow={presentation.isDeterminate ? presentation.processed : undefined}
        aria-valuetext={presentation.statusText}
      >
        <div
          className="progress-fill"
          style={{ width: `${presentation.percent}%` }}
        />
      </div>
      <div className="progress-waiting" role="status" aria-live="polite" aria-atomic="true">
        {presentation.statusText}
      </div>
      {onCancel && (
        <div className="progress-actions">
          <button type="button" className="browse-btn progress-cancel-btn" onClick={onCancel}>
            Cancel scan
          </button>
        </div>
      )}
      {presentation.isError && (
        <div className="landing-error" role="alert">
          Scan error: {presentation.message}
        </div>
      )}
    </div>
  );
}

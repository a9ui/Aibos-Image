import React from 'react';

interface ScanErrorNoticeProps {
  message: string;
  canRetry: boolean;
  onRetry: () => void;
  onDismiss: () => void;
  subject?: 'scan' | 'search';
  recoveryAction?: 'retry' | 'rescan';
}

export function ScanErrorNotice({
  message,
  canRetry,
  onRetry,
  onDismiss,
  subject = 'scan',
  recoveryAction = 'retry',
}: ScanErrorNoticeProps) {
  const isSearch = subject === 'search';
  const isSessionExpired = recoveryAction === 'rescan';
  const label = isSessionExpired ? 'Session expired' : isSearch ? 'Search' : 'Scan';
  const retryLabel = isSessionExpired ? 'Rescan folder set' : isSearch ? 'Retry search' : 'Retry scan';
  const retryAriaLabel = isSessionExpired
    ? 'Rescan the current folder set to refresh the viewer session'
    : isSearch
    ? 'Retry the current search'
    : 'Retry scan with the current folder set';
  const disabledRetryAriaLabel = isSessionExpired
    ? 'Rescan is unavailable because no folder set is selected'
    : isSearch
    ? 'Retry search is unavailable'
    : 'Retry scan is unavailable because no folder set is selected';

  return (
    <div className="landing-error scan-error-notice" role="alert">
      <span>{isSessionExpired ? `${label}:` : `${label} error:`} {message}</span>
      <div className="scan-error-actions">
        <button
          className="sidebar-link"
          type="button"
          onClick={onRetry}
          disabled={!canRetry}
          aria-label={canRetry
            ? retryAriaLabel
            : disabledRetryAriaLabel}
        >
          {retryLabel}
        </button>
        <button className="sidebar-link" type="button" onClick={onDismiss}>
          Dismiss
        </button>
      </div>
    </div>
  );
}

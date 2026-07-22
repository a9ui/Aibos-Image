import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ScanErrorNotice } from './ScanErrorNotice';

describe('ScanErrorNotice', () => {
  it('announces the error and provides retry and dismiss controls', async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    const onDismiss = vi.fn();
    render(
      <ScanErrorNotice
        message="Connection lost before the scan completed."
        canRetry
        onRetry={onRetry}
        onDismiss={onDismiss}
      />
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Scan error: Connection lost before the scan completed.');
    await user.click(screen.getByRole('button', { name: 'Retry scan with the current folder set' }));
    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('keeps retry unavailable without a folder set while allowing dismissal', () => {
    render(
      <ScanErrorNotice
        message="The scan could not be completed."
        canRetry={false}
        onRetry={vi.fn()}
        onDismiss={vi.fn()}
      />
    );

    expect(screen.getByRole('button', { name: 'Retry scan is unavailable because no folder set is selected' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeEnabled();
  });

  it('uses a rescan action for an expired viewer session instead of retrying the same search', async () => {
    const user = userEvent.setup();
    const onRescan = vi.fn();
    render(
      <ScanErrorNotice
        subject="search"
        recoveryAction="rescan"
        message="This viewer session expired."
        canRetry
        onRetry={onRescan}
        onDismiss={vi.fn()}
      />
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Session expired: This viewer session expired.');
    await user.click(screen.getByRole('button', { name: 'Rescan the current folder set to refresh the viewer session' }));
    expect(onRescan).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Retry the current search' })).not.toBeInTheDocument();
  });
});

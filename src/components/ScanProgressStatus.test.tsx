import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ScanProgressStatus, getScanProgressPresentation } from './ScanProgressStatus';

describe('ScanProgressStatus', () => {
  it('offers an explicit cancel action only when the parent supplies the active scan callback', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    const { rerender } = render(<ScanProgressStatus progress={{
      processed: 4,
      total: 10,
      newFiles: 1,
      stage: 'scanning',
    }} onCancel={onCancel} />);

    await user.click(screen.getByRole('button', { name: 'Cancel scan' }));
    expect(onCancel).toHaveBeenCalledTimes(1);

    rerender(<ScanProgressStatus progress={{
      processed: 4,
      total: 10,
      newFiles: 1,
      stage: 'scanning',
    }} />);
    expect(screen.queryByRole('button', { name: 'Cancel scan' })).not.toBeInTheDocument();
  });

  it('exposes preparing progress with determinate values', () => {
    render(<ScanProgressStatus progress={{
      processed: 0,
      total: 4,
      newFiles: 0,
      stage: 'preparing',
      message: 'Preparing file list...',
    }} />);

    const progressbar = screen.getByRole('progressbar', { name: 'Scan progress' });
    expect(progressbar).toHaveAttribute('aria-valuemin', '0');
    expect(progressbar).toHaveAttribute('aria-valuemax', '4');
    expect(progressbar).toHaveAttribute('aria-valuenow', '0');
    expect(screen.getByRole('status')).toHaveTextContent('Preparing file list... 0 of 4 folders, 0% complete.');
  });

  it('keeps zero-total work indeterminate and announces the scanning state', () => {
    render(<ScanProgressStatus progress={{
      processed: 0,
      total: 0,
      newFiles: 0,
      stage: 'scanning',
      message: 'Waiting for file count...',
    }} />);

    const progressbar = screen.getByRole('progressbar', { name: 'Scan progress' });
    expect(progressbar).not.toHaveAttribute('aria-valuemin');
    expect(progressbar).not.toHaveAttribute('aria-valuemax');
    expect(progressbar).not.toHaveAttribute('aria-valuenow');
    expect(progressbar).toHaveAttribute('aria-valuetext', 'Waiting for file count... Progress total is not available yet.');
  });

  it('reports scanning and complete stages with count and new-file detail', () => {
    const scanning = getScanProgressPresentation({
      processed: 4,
      total: 10,
      newFiles: 2,
      stage: 'scanning',
      message: 'Scanning files...',
    });
    const complete = getScanProgressPresentation({
      processed: 10,
      total: 10,
      newFiles: 2,
      stage: 'complete',
    });

    expect(scanning.statusText).toBe('Scanning files... 4 of 10 files, 40% complete. 2 new files found.');
    expect(complete.statusText).toBe('Scan complete. 10 of 10 files, 100% complete. 2 new files found.');
  });

  it('exposes failures as an alert without removing the status text', () => {
    render(<ScanProgressStatus progress={{
      processed: 3,
      total: 10,
      newFiles: 1,
      stage: 'error',
      message: 'Connection lost before completion.',
    }} />);

    expect(screen.getByRole('alert')).toHaveTextContent('Scan error: Connection lost before completion.');
    expect(screen.getByRole('status')).toHaveTextContent('Connection lost before completion. 3 of 10 files, 30% complete. 1 new file found.');
  });
});

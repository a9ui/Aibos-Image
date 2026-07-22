import React, { useRef, useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { useDialogFocus } from './useDialogFocus';

function DialogFocusHarness() {
  const [baseOpen, setBaseOpen] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const baseRef = useRef<HTMLDivElement>(null);
  const baseCloseRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLDivElement>(null);
  const confirmCancelRef = useRef<HTMLButtonElement>(null);

  useDialogFocus({
    open: baseOpen,
    dialogRef: baseRef,
    initialFocusRef: baseCloseRef,
    onEscape: () => setBaseOpen(false),
  });
  useDialogFocus({
    open: confirmOpen,
    dialogRef: confirmRef,
    initialFocusRef: confirmCancelRef,
    onEscape: () => setConfirmOpen(false),
  });

  return (
    <>
      <button type="button" onClick={() => setBaseOpen(true)}>Open dialog</button>
      {baseOpen && (
        <div ref={baseRef} role="dialog" aria-label="Base dialog" tabIndex={-1}>
          <button ref={baseCloseRef} type="button" onClick={() => setBaseOpen(false)}>Close base</button>
          <button type="button" aria-disabled="true">Unavailable action</button>
          <button type="button" onClick={() => setConfirmOpen(true)}>Delete</button>
        </div>
      )}
      {confirmOpen && (
        <div ref={confirmRef} role="alertdialog" aria-label="Confirm delete" tabIndex={-1}>
          <button ref={confirmCancelRef} type="button" onClick={() => setConfirmOpen(false)}>Cancel</button>
          <button type="button">Confirm</button>
        </div>
      )}
    </>
  );
}

describe('useDialogFocus', () => {
  it('focuses the supplied initial control and wraps Tab in the dialog', async () => {
    const user = userEvent.setup();
    render(<DialogFocusHarness />);

    await user.click(screen.getByRole('button', { name: 'Open dialog' }));
    const close = screen.getByRole('button', { name: 'Close base' });
    const deleteButton = screen.getByRole('button', { name: 'Delete' });
    expect(close).toHaveFocus();

    await user.tab({ shift: true });
    expect(deleteButton).toHaveFocus();
    await user.tab();
    expect(close).toHaveFocus();
  });

  it('keeps Escape on the nested confirmation and restores focus to its opener', async () => {
    const user = userEvent.setup();
    render(<DialogFocusHarness />);

    const opener = screen.getByRole('button', { name: 'Open dialog' });
    await user.click(opener);
    const deleteButton = screen.getByRole('button', { name: 'Delete' });
    await user.click(deleteButton);
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Base dialog' })).toBeInTheDocument();
    expect(deleteButton).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Close base' }));
    expect(opener).toHaveFocus();
  });
});

'use client';

import { useLayoutEffect, useRef, type RefObject } from 'react';

const dialogStack: symbol[] = [];
const dialogElements = new Map<symbol, HTMLElement>();

function removeFromDialogStack(token: symbol) {
  const index = dialogStack.lastIndexOf(token);
  if (index >= 0) dialogStack.splice(index, 1);
  dialogElements.delete(token);
}

function getTopDialog() {
  const token = dialogStack[dialogStack.length - 1];
  return token ? dialogElements.get(token) ?? null : null;
}

function isFocusable(element: HTMLElement) {
  return !element.hasAttribute('disabled')
    && element.getAttribute('aria-disabled') !== 'true'
    && Number(element.getAttribute('tabindex') ?? '0') >= 0
    && !element.hidden
    && !element.closest('[hidden], [aria-hidden="true"]');
}

function getFocusableElements(container: HTMLElement) {
  return Array.from(container.querySelectorAll<HTMLElement>([
    'a[href]',
    'button',
    'input',
    'select',
    'textarea',
    '[tabindex]:not([tabindex="-1"])',
  ].join(','))).filter(isFocusable);
}

function focusDialogTarget(
  dialog: HTMLElement,
  initialFocusRef?: RefObject<HTMLElement | null>,
) {
  const initial = initialFocusRef?.current;
  if (initial && dialog.contains(initial) && isFocusable(initial)) {
    initial.focus();
    return;
  }

  const [firstFocusable] = getFocusableElements(dialog);
  if (firstFocusable) {
    firstFocusable.focus();
    return;
  }

  dialog.focus();
}

/**
 * Keeps only the visually top-most dialog keyboard-active, traps Tab inside it,
 * and returns focus to its opener when it closes. The stack makes nested
 * confirmations safe without each dialog needing to know about its parent.
 */
export function useDialogFocus({
  open,
  dialogRef,
  initialFocusRef,
  onEscape,
}: {
  open: boolean;
  dialogRef: RefObject<HTMLElement | null>;
  initialFocusRef?: RefObject<HTMLElement | null>;
  onEscape?: () => void;
}) {
  const tokenRef = useRef<symbol | null>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const onEscapeRef = useRef(onEscape);

  useLayoutEffect(() => {
    onEscapeRef.current = onEscape;
  }, [onEscape]);

  useLayoutEffect(() => {
    if (!open) return;

    const dialog = dialogRef.current;
    if (!dialog) return;

    const token = Symbol('dialog-focus');
    tokenRef.current = token;
    openerRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    dialogStack.push(token);
    dialogElements.set(token, dialog);
    focusDialogTarget(dialog, initialFocusRef);

    const onKeyDown = (event: KeyboardEvent) => {
      if (dialogStack[dialogStack.length - 1] !== token) return;

      if (event.key === 'Escape' && onEscapeRef.current) {
        event.preventDefault();
        event.stopPropagation();
        onEscapeRef.current();
        return;
      }

      if (event.key !== 'Tab') return;
      const focusable = getFocusableElements(dialog);
      if (focusable.length === 0) {
        event.preventDefault();
        dialog.focus();
        return;
      }

      const currentIndex = focusable.indexOf(document.activeElement as HTMLElement);
      if (event.shiftKey) {
        if (currentIndex <= 0) {
          event.preventDefault();
          focusable[focusable.length - 1].focus();
        }
      } else if (currentIndex < 0 || currentIndex === focusable.length - 1) {
        event.preventDefault();
        focusable[0].focus();
      }
    };

    // Capture keeps a nested confirmation from leaking Escape/Tab to its parent
    // ImageModal window listener.
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('keydown', onKeyDown, true);
      removeFromDialogStack(token);
      tokenRef.current = null;

      const opener = openerRef.current;
      const topDialog = getTopDialog();
      if (opener && document.contains(opener) && (!topDialog || topDialog.contains(opener))) {
        opener.focus();
      } else if (topDialog) {
        focusDialogTarget(topDialog);
      }
    };
  }, [dialogRef, initialFocusRef, open]);
}

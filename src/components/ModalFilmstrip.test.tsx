import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ImageFile } from '../lib/types';
import ModalFilmstrip, { type ModalFilmstripItem } from './ModalFilmstrip';

vi.mock('./CachedImage', () => ({
  default: ({ src, className }: { src: string; className?: string }) => (
    <span className={className} data-mock-image-src={src} />
  ),
}));

function itemAt(index: number): ModalFilmstripItem {
  const image: ImageFile = {
    id: `C:/images/image-${index}.png`,
    filename: `image-${index}.png`,
    absolutePath: `C:/images/image-${index}.png`,
    fileUrl: `/api/image?thumb=${index}`,
    displayUrl: `/api/image?display=${index}`,
    fullUrl: `/api/image?full=${index}`,
    metadata: null,
    createdAt: index,
    mtime: index,
  };
  return { image, sourceIndex: index };
}

describe('ModalFilmstrip virtualization', () => {
  it('keeps a 100,000-image strip to a bounded viewport DOM and marks the current image', () => {
    const onNeedRange = vi.fn();
    render(
      <ModalFilmstrip
        total={100_000}
        activeIndex={50_000}
        getItem={(index) => itemAt(index)}
        onNeedRange={onNeedRange}
        onSelect={vi.fn()}
        onNavigate={vi.fn()}
        onCollapse={vi.fn()}
        onSessionExpired={vi.fn()}
        toggleShortcut="T"
      />
    );

    const options = screen.getAllByRole('option');
    expect(options.length).toBeGreaterThan(1);
    expect(options.length).toBeLessThan(40);
    const current = screen.getByRole('option', { name: 'Open image-50000.png, image 50001 of 100000' });
    expect(current).toHaveAttribute('aria-current', 'true');
    expect(current).toHaveAttribute('aria-selected', 'true');
    expect(current).toHaveAttribute('aria-posinset', '50001');
    expect(current).toHaveAttribute('aria-setsize', '100000');
    expect(onNeedRange).toHaveBeenCalledWith(expect.any(Number), expect.any(Number));
    expect(screen.getByRole('listbox', { name: 'Image filmstrip thumbnails' }))
      .toHaveAttribute('aria-orientation', 'horizontal');
    expect(current.style.width).toBe('64px');
    expect(current.style.height).toBe('92px');
  });

  it('selects a visible thumbnail and exposes an accessible collapse shortcut', () => {
    const onSelect = vi.fn();
    const onNavigate = vi.fn();
    const onCollapse = vi.fn();
    render(
      <ModalFilmstrip
        total={40}
        activeIndex={10}
        getItem={(index) => itemAt(index)}
        onNeedRange={vi.fn()}
        onSelect={onSelect}
        onNavigate={onNavigate}
        onCollapse={onCollapse}
        onSessionExpired={vi.fn()}
        toggleShortcut="T"
      />
    );

    fireEvent.click(screen.getByRole('option', { name: 'Open image-11.png, image 12 of 40' }));
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ sourceIndex: 11 }));

    const collapse = screen.getByRole('button', { name: 'Hide image filmstrip' });
    expect(collapse).toHaveAttribute('aria-keyshortcuts', 'T');
    fireEvent.click(collapse);
    expect(onCollapse).toHaveBeenCalledTimes(1);

    const option = screen.getByRole('option', { name: 'Open image-11.png, image 12 of 40' });
    option.focus();
    fireEvent.keyDown(option, { key: 'ArrowLeft' });
    fireEvent.keyDown(option, { key: 'ArrowRight' });
    expect(onNavigate).toHaveBeenNthCalledWith(1, 'prev');
    expect(onNavigate).toHaveBeenNthCalledWith(2, 'next');
  });
});

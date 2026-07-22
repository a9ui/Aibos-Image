import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ImageRequestError } from '../lib/clientImageCache';
import CachedImage from './CachedImage';

const cacheMocks = vi.hoisted(() => ({
  evict: vi.fn(),
  load: vi.fn(),
}));

vi.mock('../lib/clientImageCache', async (importOriginal) => ({
  ...await importOriginal<typeof import('../lib/clientImageCache')>(),
  evictCachedImageUrl: cacheMocks.evict,
  getCachedImageUrl: () => null,
  loadCancellableCachedImageUrl: cacheMocks.load,
}));

describe('CachedImage expired viewer session recovery', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    cacheMocks.load.mockReturnValue({
      promise: Promise.resolve('blob:cached-image'),
      cancel: vi.fn(),
    });
  });

  it('notifies once and settles on a non-network placeholder instead of retrying the expired URL', async () => {
    cacheMocks.load.mockReturnValue({
      promise: Promise.reject(new ImageRequestError(410)),
      cancel: vi.fn(),
    });
    const onSessionExpired = vi.fn();
    const { rerender } = render(
      <CachedImage
        src="/api/image?path=first&indexToken=expired"
        fallbackSrc="/api/image?path=first&indexToken=expired&full=1"
        cacheKind="display"
        alt="first.png"
        onSessionExpired={onSessionExpired}
      />
    );

    await waitFor(() => expect(onSessionExpired).toHaveBeenCalledTimes(1));
    const image = screen.getByRole('img', { name: 'first.png' });
    expect(image).toHaveAttribute('data-image-session-expired', 'true');
    expect(image.getAttribute('src')).toMatch(/^data:image\/gif/);

    rerender(
      <CachedImage
        src="/api/image?path=first&indexToken=expired"
        fallbackSrc="/api/image?path=first&indexToken=expired&full=1"
        cacheKind="display"
        alt="first.png"
        onSessionExpired={onSessionExpired}
      />
    );

    expect(onSessionExpired).toHaveBeenCalledTimes(1);
    expect(cacheMocks.load).toHaveBeenCalledTimes(1);
  });

  it('settles after both a direct thumbnail URL and its fallback fail', async () => {
    const onError = vi.fn();
    const { rerender } = render(
      <CachedImage
        src="/api/image?path=first"
        requestSrc="/api/image?path=first&priority=visible"
        fallbackSrc="/api/image?path=first&full=1"
        cacheKind="thumb"
        alt="first.png"
        onError={onError}
      />
    );

    const image = screen.getByRole('img', { name: 'first.png' });
    fireEvent.error(image);
    await waitFor(() => expect(image.getAttribute('src')).toContain('full=1'));
    expect(onError).not.toHaveBeenCalled();

    fireEvent.error(image);
    await waitFor(() => expect(image).toHaveAttribute('data-image-terminal-failure', 'true'));
    expect(image.getAttribute('src')).toMatch(/^data:image\/gif/);
    expect(onError).toHaveBeenCalledTimes(1);

    rerender(
      <CachedImage
        src="/api/image?path=second"
        requestSrc="/api/image?path=second&priority=visible"
        fallbackSrc="/api/image?path=second&full=1"
        cacheKind="thumb"
        alt="second.png"
        onError={onError}
      />
    );
    await waitFor(() => expect(screen.getByRole('img', { name: 'second.png' }))
      .not.toHaveAttribute('data-image-terminal-failure'));
    expect(screen.getByRole('img', { name: 'second.png' }).getAttribute('src')).toContain('priority=visible');
  });

  it('settles after a cached display load and its direct fallback both fail', async () => {
    cacheMocks.load.mockReturnValue({
      promise: Promise.reject(new Error('network unavailable')),
      cancel: vi.fn(),
    });
    const onError = vi.fn();
    render(
      <CachedImage
        src="/api/image?path=display"
        requestSrc="/api/image?path=display&priority=focused"
        fallbackSrc="/api/image?path=display&full=1"
        cacheKind="display"
        alt="display.png"
        onError={onError}
      />
    );

    const image = screen.getByRole('img', { name: 'display.png' });
    await waitFor(() => expect(image.getAttribute('src')).toContain('full=1'));
    fireEvent.error(image);
    await waitFor(() => expect(image).toHaveAttribute('data-image-terminal-failure', 'true'));
    expect(image.getAttribute('src')).toMatch(/^data:image\/gif/);
    expect(onError).toHaveBeenCalledTimes(1);
  });

  it('keeps the display image on a local placeholder until the shared blob request resolves', async () => {
    let resolveLoad!: (value: string) => void;
    cacheMocks.load.mockReturnValue({
      promise: new Promise<string>((resolve) => {
        resolveLoad = resolve;
      }),
      cancel: vi.fn(),
    });

    render(
      <CachedImage
        src="/api/image?path=display"
        requestSrc="/api/image?path=display&priority=focused"
        fallbackSrc="/api/image?path=display&full=1"
        cacheKind="display"
        alt="display.png"
      />
    );

    const image = screen.getByRole('img', { name: 'display.png' });
    expect(image.getAttribute('src')).toMatch(/^data:image\/gif/);
    expect(image.getAttribute('src')).not.toContain('priority=focused');

    resolveLoad('blob:cached-display');
    await waitFor(() => expect(image).toHaveAttribute('src', 'blob:cached-display'));
  });
});

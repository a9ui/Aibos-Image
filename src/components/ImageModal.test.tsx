import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_KEY_BINDINGS, type ImageFile } from '../lib/types';
import ImageModal, {
  MODAL_CHROME_TRANSIENT_MS,
  MODAL_FILMSTRIP_HOVER_ZONE_PX,
} from './ImageModal';

const store = vi.hoisted(() => ({
  deleteImage: vi.fn(),
  resolveModalNavigationTarget: vi.fn(),
  setSelectedIndex: vi.fn(),
  setModalImageIds: vi.fn(),
  ensureSearchRange: vi.fn(),
  setView: vi.fn(),
  cycleFavoriteLevel: vi.fn(),
  decreaseFavoriteLevel: vi.fn(),
  searchResults: [] as Array<Record<string, unknown>>,
  modalImageIds: [] as string[],
  selectedIndex: 0 as number | null,
  favorites: {} as Record<string, number>,
  view: { modalEdgeRatio: 0.28, modalFilmstripOpen: true },
  reportImageSessionExpired: vi.fn(),
  searchQuery: '',
  setSearchQuery: vi.fn(),
}));

const albumStore = vi.hoisted(() => ({
  openPicker: vi.fn(),
  closeAlbum: vi.fn(),
  activeSource: null as null | Record<string, unknown>,
}));

const fixtures = vi.hoisted(() => {
  const first = {
    id: 'C:/images/first.png',
    filename: 'first.png',
    absolutePath: 'C:/images/first.png',
    fileUrl: '/api/image?first',
    displayUrl: '/api/image?first&display=1',
    fullUrl: '/api/image?first&full=1',
    metadata: null,
    createdAt: 1,
    mtime: 1,
  };
  return {
    first,
    second: {
      ...first,
      id: 'C:/images/second.png',
      filename: 'second.png',
      absolutePath: 'C:/images/second.png',
      fileUrl: '/api/image?second',
      displayUrl: '/api/image?second&display=1',
      fullUrl: '/api/image?second&full=1',
    },
    third: {
      ...first,
      id: 'C:/images/third.png',
      filename: 'third.png',
      absolutePath: 'C:/images/third.png',
      fileUrl: '/api/image?third',
      displayUrl: '/api/image?third&display=1',
      fullUrl: '/api/image?third&full=1',
    },
  };
});

vi.mock('../store/ImageContext', () => ({
  useImageStore: () => ({
    searchResults: store.searchResults,
    searchTotal: store.searchResults.length,
    searchQuery: store.searchQuery,
    setSearchQuery: store.setSearchQuery,
    selectedIndex: store.selectedIndex,
    setSelectedIndex: store.setSelectedIndex,
    modalImageIds: store.modalImageIds,
    setModalImageIds: store.setModalImageIds,
    ensureSearchRange: store.ensureSearchRange,
    resolveModalNavigationTarget: store.resolveModalNavigationTarget,
    cycleFavoriteLevel: store.cycleFavoriteLevel,
    decreaseFavoriteLevel: store.decreaseFavoriteLevel,
    favorites: store.favorites,
    markImageSeen: vi.fn(),
    requestRevealImage: vi.fn(),
    keyBindings: DEFAULT_KEY_BINDINGS,
    deleteImage: store.deleteImage,
    openExternal: vi.fn(),
    confirmBeforeDelete: false,
    setConfirmBeforeDelete: vi.fn(),
    view: store.view,
    setView: store.setView,
    indexToken: null,
    reportImageSessionExpired: store.reportImageSessionExpired,
  }),
}));

vi.mock('../store/AlbumContext', () => ({
  useOptionalAlbumStore: () => ({
    activeSource: albumStore.activeSource,
    closeAlbum: albumStore.closeAlbum,
    recycleSource: vi.fn(async () => false),
    refreshActiveSource: vi.fn(async () => null),
    openPicker: albumStore.openPicker,
  }),
}));

vi.mock('../lib/clientImageCache', async (importOriginal) => ({
  ...await importOriginal<typeof import('../lib/clientImageCache')>(),
  loadCachedImageUrl: vi.fn(async () => '/cached-image'),
}));

const firstImage = fixtures.first as ImageFile;
const secondImage = fixtures.second as ImageFile;
const thirdImage = fixtures.third as ImageFile;

function mockRect(element: Element, rect: Partial<DOMRect>) {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    x: 0,
    y: 0,
    top: 0,
    right: 1000,
    bottom: 1000,
    left: 0,
    width: 1000,
    height: 1000,
    toJSON: () => ({}),
    ...rect,
  });
}

function toggleManualChromeFromImage(filename = firstImage.filename) {
  const area = screen.getByTestId('modal-image-area');
  mockRect(area, {});
  const image = screen.getAllByRole('img', { name: filename })
    .find((candidate) => candidate.classList.contains('modal-full-image'));
  if (!image) throw new Error('expected the full modal image');
  fireEvent.click(image, { clientX: 500, clientY: 500, detail: 1 });
  act(() => {
    vi.advanceTimersByTime(180);
  });
}

function openImageActions() {
  const area = screen.getByTestId('modal-image-area');
  fireEvent.contextMenu(area, { clientX: 420, clientY: 260 });
  return screen.getByRole('menu', { name: 'Image actions' });
}

describe('ImageModal sparse navigation lock', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    store.searchResults = [fixtures.first, fixtures.second];
    store.selectedIndex = 0;
    store.favorites = {};
    store.modalImageIds = [];
    store.view = { modalEdgeRatio: 0.28, modalFilmstripOpen: true };
    store.reportImageSessionExpired.mockReset();
    store.searchQuery = '';
    store.setSearchQuery.mockReset();
    albumStore.openPicker.mockReset();
    albumStore.closeAlbum.mockReset();
    albumStore.activeSource = null;
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({ jobs: [] }),
    }) as Response));
    store.resolveModalNavigationTarget.mockResolvedValue({
      status: 'found',
      index: 1,
      id: secondImage.id,
      filteredOrderedIds: null,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps manually visible chrome open, then briefly reveals manually hidden chrome with its cursor', async () => {
    vi.useFakeTimers();
    render(<ImageModal />);
    const dialog = screen.getByRole('dialog');
    mockRect(dialog, {});

    await act(async () => {
      await Promise.resolve();
    });
    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS * 10);
    });
    expect(dialog).not.toHaveClass('chrome-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'visible');

    toggleManualChromeFromImage();
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'hidden');

    fireEvent.pointerMove(dialog, { pointerId: 1, pointerType: 'mouse', clientX: 500, clientY: 500 });
    expect(dialog).not.toHaveClass('chrome-hidden');
    expect(dialog).not.toHaveClass('cursor-hidden');

    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS);
    });
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'hidden');
  });

  it('closes the preview from center black space inside the image area', () => {
    vi.useFakeTimers();
    render(<ImageModal />);
    const area = screen.getByTestId('modal-image-area');
    mockRect(area, {});

    fireEvent.click(area, { clientX: 500, clientY: 500, detail: 1 });
    act(() => {
      vi.advanceTimersByTime(180);
    });
    expect(store.setSelectedIndex).toHaveBeenCalledWith(null);
    expect(store.setModalImageIds).toHaveBeenCalledWith([]);
  });

  it('closes the preview from the outer black backdrop', () => {
    render(<ImageModal />);
    fireEvent.click(screen.getByTestId('modal-backdrop'));
    expect(store.setSelectedIndex).toHaveBeenCalledWith(null);
    expect(store.setModalImageIds).toHaveBeenCalledWith([]);
  });

  it('keeps left and right empty edge zones as navigation while center double-click does not close', async () => {
    vi.useFakeTimers();
    render(<ImageModal />);
    const area = screen.getByTestId('modal-image-area');
    mockRect(area, {});

    fireEvent.click(area, { clientX: 100, clientY: 500, detail: 1 });
    act(() => {
      vi.advanceTimersByTime(180);
    });
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'prev');

    store.resolveModalNavigationTarget.mockClear();
    fireEvent.click(area, { clientX: 900, clientY: 500, detail: 1 });
    act(() => {
      vi.advanceTimersByTime(180);
    });
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'next');

    store.setSelectedIndex.mockClear();
    fireEvent.doubleClick(area, { clientX: 500, clientY: 500, detail: 2 });
    expect(store.setSelectedIndex).not.toHaveBeenCalledWith(null);
  });

  it('closes the preview when an unzoomed image is dragged downward', () => {
    render(<ImageModal />);
    const area = screen.getByTestId('modal-image-area');
    mockRect(area, { width: 1000, height: 800, right: 1000, bottom: 800 });

    fireEvent.pointerDown(area, { pointerId: 9, pointerType: 'mouse', button: 0, clientX: 500, clientY: 280 });
    fireEvent.pointerMove(area, { pointerId: 9, pointerType: 'mouse', clientX: 510, clientY: 470 });
    fireEvent.pointerUp(area, { pointerId: 9, pointerType: 'mouse', clientX: 510, clientY: 470 });

    expect(store.setSelectedIndex).toHaveBeenCalledWith(null);
    expect(store.setModalImageIds).toHaveBeenCalledWith([]);
    expect(store.resolveModalNavigationTarget).not.toHaveBeenCalled();
  });

  it('keeps the manual hidden choice when navigation changes the selected image', async () => {
    vi.useFakeTimers();
    const { rerender } = render(<ImageModal />);
    const dialog = screen.getByRole('dialog');
    mockRect(dialog, {});
    toggleManualChromeFromImage();

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.nextImage });
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'next');
    expect(store.setSelectedIndex).toHaveBeenCalledWith(1);

    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS);
    });
    store.selectedIndex = 1;
    rerender(<ImageModal />);
    expect(screen.getByText('second.png')).toBeVisible();
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'hidden');
  });

  it('keeps the manual hidden choice after delete moves to exactly one adjacent image', async () => {
    vi.useFakeTimers();
    store.searchResults = [fixtures.first, fixtures.second, fixtures.third];
    store.selectedIndex = 1;
    store.deleteImage.mockResolvedValue(true);
    store.resolveModalNavigationTarget.mockResolvedValue({
      status: 'found',
      index: 2,
      id: thirdImage.id,
      filteredOrderedIds: null,
    });
    const { rerender } = render(<ImageModal />);
    const dialog = screen.getByRole('dialog');
    mockRect(dialog, {});
    toggleManualChromeFromImage(secondImage.filename);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(store.deleteImage).toHaveBeenCalledTimes(1);
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledTimes(1);
    expect(store.setSelectedIndex).toHaveBeenCalledWith(2);

    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS);
    });
    store.selectedIndex = 2;
    rerender(<ImageModal />);
    expect(screen.getByText('third.png')).toBeVisible();
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'hidden');
  });

  it('keeps the manual hidden choice while switching original and enhanced output', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({
        jobs: [{
          id: 'enhanced-1',
          sourceId: firstImage.id,
          status: 'succeeded',
          progress: 100,
          outputPath: 'C:/enhanced/first.webp',
        }],
      }),
    }) as Response));
    render(<ImageModal />);
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    const dialog = screen.getByRole('dialog');
    mockRect(dialog, {});
    const outputToggle = screen.getByRole('button', { name: 'Toggle original or enhanced image' });
    expect(outputToggle).not.toBeDisabled();
    toggleManualChromeFromImage();
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');

    fireEvent.click(outputToggle);
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
    expect(dialog).toHaveAttribute('data-manual-chrome', 'hidden');
  });

  it('retries enhancement polling after a transient API failure', async () => {
    vi.useFakeTimers();
    let jobFetches = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        jobFetches += 1;
        if (jobFetches === 1) {
          return { ok: false, json: async () => ({ error: 'temporary' }) } as Response;
        }
        return {
          ok: true,
          json: async () => ({
            jobs: [{
              id: 'enhanced-after-retry',
              sourceId: firstImage.id,
              status: 'succeeded',
              progress: 100,
              outputPath: 'C:/enhance/outputs/retry.webp',
            }],
          }),
        } as Response;
      }
      return {
        ok: true,
        json: async () => ({ success: true, opened: 'source', sizeBytes: 1024 }),
      } as Response;
    }));

    render(<ImageModal />);
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(jobFetches).toBe(1);

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(jobFetches).toBe(2);
    expect(screen.getByRole('button', { name: 'Toggle original or enhanced image' })).toHaveTextContent('UP');
  });

  it('keeps clipboard feedback scoped to one button and catches a later rejection', async () => {
    vi.useFakeTimers();
    const writeText = vi.fn()
      .mockResolvedValueOnce(undefined)
      .mockRejectedValueOnce(new Error('clipboard denied'));
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    store.searchResults = [{
      ...fixtures.first,
      metadata: {
        raw: 'sunset, lake\nNegative prompt: blurry\nSteps: 20',
        prompt: 'sunset, lake',
        negativePrompt: 'blurry',
        settings: { Steps: '20' },
      },
    }];

    render(<ImageModal />);
    fireEvent.click(screen.getByRole('button', { name: 'Show metadata sidebar' }));
    const promptCopy = screen.getByRole('button', { name: 'Copy prompt metadata' });
    const pngCopy = screen.getByRole('button', { name: 'Copy PNG metadata' });

    fireEvent.click(promptCopy);
    await act(async () => { await Promise.resolve(); });
    expect(promptCopy).toHaveTextContent('Copied');
    expect(pngCopy).toHaveTextContent('Copy');

    act(() => { vi.advanceTimersByTime(1000); });
    fireEvent.click(screen.getByRole('tab', { name: 'Negative' }));
    const negativeCopy = screen.getByRole('button', { name: 'Copy negative prompt metadata' });
    fireEvent.click(negativeCopy);
    await act(async () => { await Promise.resolve(); });
    act(() => { vi.advanceTimersByTime(600); });

    expect(negativeCopy).toHaveTextContent('Copy failed');
    expect(pngCopy).toHaveTextContent('Copy');
    expect(writeText).toHaveBeenNthCalledWith(1, 'sunset, lake');
    expect(writeText).toHaveBeenNthCalledWith(2, 'blurry');
  });

  it('shows the displayed asset capacity and uses the same Original/Enhanced target for Enter and O', async () => {
    const openCalls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        return {
          ok: true,
          json: async () => ({
            jobs: [{
              id: 'enhanced-1',
              sourceId: firstImage.id,
              status: 'succeeded',
              progress: 100,
              outputPath: 'C:/enhance/outputs/first.webp',
            }],
          }),
        } as Response;
      }
      if (url.includes('/api/open')) {
        const method = init?.method ?? 'GET';
        openCalls.push({ url, method });
        const enhanced = new URL(url, 'http://127.0.0.1').searchParams.get('display') === 'enhanced';
        return {
          ok: true,
          json: async () => ({
            success: true,
            opened: enhanced ? 'enhanced' : 'source',
            sizeBytes: enhanced ? 2_621_440 : 1_048_576,
          }),
        } as Response;
      }
      throw new Error(`unexpected fetch ${url}`);
    }));

    render(<ImageModal />);
    await waitFor(() => expect(screen.getByLabelText('Displayed file size')).toHaveTextContent('2.50MB'));

    const outputToggle = screen.getByRole('button', { name: 'Toggle original or enhanced image' });
    expect(outputToggle).toHaveTextContent('UP');
    fireEvent.keyDown(window, { key: 'Enter' });
    await waitFor(() => expect(openCalls.some((call) => call.method === 'POST' && call.url.includes('display=enhanced'))).toBe(true));

    fireEvent.click(outputToggle);
    await waitFor(() => expect(screen.getByLabelText('Displayed file size')).toHaveTextContent('1.00MB'));
    fireEvent.click(screen.getByRole('button', { name: 'Open in external viewer' }));
    await waitFor(() => expect(openCalls.some((call) => call.method === 'POST' && !call.url.includes('display=enhanced'))).toBe(true));

    const postsBeforeNativeEnter = openCalls.filter((call) => call.method === 'POST').length;
    fireEvent.keyDown(screen.getByRole('combobox', { name: 'Enhanced version' }), { key: 'Enter' });
    expect(openCalls.filter((call) => call.method === 'POST')).toHaveLength(postsBeforeNativeEnter);
  });

  it('falls back visibly to Original capacity when the Enhanced asset is unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        return {
          ok: true,
          json: async () => ({
            jobs: [{
              id: 'enhanced-missing',
              sourceId: firstImage.id,
              status: 'succeeded',
              progress: 100,
              outputPath: 'C:/enhance/outputs/missing.webp',
            }],
          }),
        } as Response;
      }
      const enhanced = new URL(url, 'http://127.0.0.1').searchParams.get('display') === 'enhanced';
      return {
        ok: true,
        json: async () => enhanced
          ? {
              success: true,
              opened: 'source',
              sizeBytes: 524_288,
              fallback: {
                code: 'enhanced-output-missing',
                message: 'Displayed Enhanced output is unavailable; using Original instead.',
              },
            }
          : { success: true, opened: 'source', sizeBytes: 524_288 },
      } as Response;
    }));

    render(<ImageModal />);

    await waitFor(() => expect(screen.getByLabelText('Displayed file size')).toHaveTextContent('0.50MB'));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('using Original instead'));
    expect(screen.getByRole('button', { name: 'Toggle original or enhanced image' })).toHaveTextContent('OR');
  });

  it('reports a recoverable shell failure without leaking the shell detail', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        return { ok: true, json: async () => ({ jobs: [] }) } as Response;
      }
      if (init?.method === 'POST') {
        return {
          ok: false,
          json: async () => ({ error: 'Open external application failed' }),
        } as Response;
      }
      return {
        ok: true,
        json: async () => ({ success: true, opened: 'source', sizeBytes: 0 }),
      } as Response;
    }));

    render(<ImageModal />);
    await waitFor(() => expect(screen.getByLabelText('Displayed file size')).toHaveTextContent('0.00MB'));
    fireEvent.keyDown(window, { key: 'Enter' });
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Open external application failed'));
  });

  it('routes safe image actions from the context menu and confirms before Recycle', async () => {
    const openCalls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        return { ok: true, json: async () => ({ jobs: [] }) } as Response;
      }
      if (url.includes('/api/open')) {
        const method = init?.method ?? 'GET';
        openCalls.push({ url, method });
        return {
          ok: true,
          json: async () => ({ success: true, opened: 'source', sizeBytes: 1024 }),
        } as Response;
      }
      throw new Error(`unexpected fetch ${url}`);
    }));

    render(<ImageModal />);

    let menu = openImageActions();
    expect(screen.getByRole('dialog')).toHaveAttribute('data-manual-chrome', 'visible');
    expect(within(menu).getByRole('menuitem', { name: 'Show Enhanced image' })).toBeDisabled();
    expect(store.resolveModalNavigationTarget).not.toHaveBeenCalled();
    expect(store.setSelectedIndex).not.toHaveBeenCalled();

    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Favorite +1' }));
    expect(store.cycleFavoriteLevel).toHaveBeenCalledTimes(1);

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Favorite -1' }));
    expect(store.decreaseFavoriteLevel).toHaveBeenCalledTimes(1);

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Open displayed image externally' }));
    await waitFor(() => expect(openCalls.some((call) => call.method === 'POST')).toBe(true));

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Add image to Album' }));
    expect(albumStore.openPicker).toHaveBeenCalledWith([firstImage.id]);

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Hide Filmstrip' }));
    expect(store.setView).toHaveBeenCalledWith({ modalFilmstripOpen: false });

    expect(screen.getByRole('complementary', { name: 'Image metadata' })).toHaveClass('hidden');
    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Show metadata' }));
    expect(screen.getByRole('complementary', { name: 'Image metadata' })).not.toHaveClass('hidden');

    const area = screen.getByTestId('modal-image-area');
    fireEvent.wheel(area, { deltaY: -120 });
    expect(screen.getByLabelText('Zoom level')).toHaveTextContent('110%');
    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Reset zoom' }));
    expect(screen.getByLabelText('Zoom level')).toHaveTextContent('100%');

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Move image to Recycle Bin…' }));
    expect(await screen.findByRole('alertdialog')).toHaveTextContent('Move this image to Recycle Bin?');
    expect(store.deleteImage).not.toHaveBeenCalled();
  });

  it('returns from an Album to catalog search when a prompt tag is selected', () => {
    store.searchQuery = 'landscape';
    albumStore.activeSource = {
      album: { id: 'album-1', name: 'Review', members: [] },
      sourceToken: 'album-source-token',
      members: [],
      images: [{
        ...fixtures.first,
        metadata: {
          prompt: 'cat, dramatic lighting',
          negativePrompt: '',
          raw: '',
          settings: {},
        },
      }],
    };

    render(<ImageModal />);
    fireEvent.click(screen.getByRole('button', { name: 'cat' }));

    expect(albumStore.closeAlbum).toHaveBeenCalledTimes(1);
    expect(store.setSearchQuery).toHaveBeenCalledWith('landscape, cat');
  });

  it('toggles a valid Enhanced output from the context menu', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/enhance/jobs')) {
        return {
          ok: true,
          json: async () => ({
            jobs: [{
              id: 'enhanced-context',
              sourceId: firstImage.id,
              status: 'succeeded',
              progress: 100,
              outputPath: 'C:/enhance/outputs/context.webp',
            }],
          }),
        } as Response;
      }
      return {
        ok: true,
        json: async () => ({ success: true, opened: 'enhanced', sizeBytes: 2048 }),
      } as Response;
    }));

    render(<ImageModal />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Toggle original or enhanced image' })).toHaveTextContent('UP'));

    let menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Show Original image' }));
    expect(screen.getByRole('button', { name: 'Toggle original or enhanced image' })).toHaveTextContent('OR');

    menu = openImageActions();
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Show Enhanced image' }));
    expect(screen.getByRole('button', { name: 'Toggle original or enhanced image' })).toHaveTextContent('UP');
  });

  it('closes the context menu on Escape, outside pointer, and image navigation', async () => {
    render(<ImageModal />);

    openImageActions();
    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.closeModal });
    expect(screen.queryByRole('menu', { name: 'Image actions' })).not.toBeInTheDocument();
    expect(store.setSelectedIndex).not.toHaveBeenCalled();

    openImageActions();
    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole('menu', { name: 'Image actions' })).not.toBeInTheDocument();

    openImageActions();
    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.nextImage });
    expect(screen.queryByRole('menu', { name: 'Image actions' })).not.toBeInTheDocument();
    await waitFor(() => expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'next'));
  });

  it('keeps one zoom readout and wires the visible flip, metadata, reset, and close buttons', () => {
    render(<ImageModal />);

    const fullImage = screen.getAllByRole('img', { name: firstImage.filename })
      .find((candidate) => candidate.classList.contains('modal-full-image'));
    expect(fullImage).toBeDefined();
    if (!fullImage) throw new Error('expected the full modal image');

    expect(screen.getAllByLabelText('Zoom level')).toHaveLength(1);
    expect(screen.getByLabelText('Zoom level')).toHaveTextContent('100%');
    fireEvent.click(screen.getByRole('button', { name: 'Flip horizontally' }));
    expect(fullImage.style.transform).toContain('scaleX(-1)');

    fireEvent.click(screen.getByRole('button', { name: 'Show metadata sidebar' }));
    expect(screen.getByRole('complementary', { name: 'Image metadata' })).not.toHaveClass('hidden');

    fireEvent.wheel(screen.getByTestId('modal-image-area'), { deltaY: -120 });
    expect(screen.getByLabelText('Zoom level')).toHaveTextContent('110%');
    fireEvent.click(screen.getByRole('button', { name: 'Reset zoom' }));
    expect(screen.getByLabelText('Zoom level')).toHaveTextContent('100%');

    fireEvent.click(screen.getByRole('button', { name: 'Close image preview' }));
    expect(store.setSelectedIndex).toHaveBeenCalledWith(null);
  });

  it('releases navigation after a failed delete so next still works', async () => {
    store.deleteImage.mockResolvedValue(false);
    render(<ImageModal />);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    await waitFor(() => expect(store.deleteImage).toHaveBeenCalledWith(firstImage.id, { favoriteConfirmed: false }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Move image to Recycle Bin' })).not.toBeDisabled();
    });

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.nextImage });
    await waitFor(() => {
      expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'next');
      expect(store.setSelectedIndex).toHaveBeenCalledWith(1);
    });
  });

  it('keeps the existing thumbnail visible and reports an expired full-image session once', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/enhance/jobs')) {
        return { ok: true, json: async () => ({ jobs: [] }) } as Response;
      }
      return new Response('expired', { status: 410 });
    }));

    render(<ImageModal />);

    await waitFor(() => expect(store.reportImageSessionExpired).toHaveBeenCalledTimes(1));
    const images = screen.getAllByRole('img', { name: firstImage.filename });
    const thumbnail = images.find((image) => image.classList.contains('modal-thumb-preview'));
    const fullImage = images.find((image) => image.classList.contains('modal-full-image'));
    expect(thumbnail).toBeDefined();
    expect(fullImage).toBeDefined();
    if (!thumbnail || !fullImage) throw new Error('expected thumbnail and full modal images');
    expect(thumbnail.getAttribute('src')).toContain('/api/image?first');
    expect(thumbnail).toHaveClass('is-thumb-preview');
    expect(fullImage).toHaveAttribute('data-image-session-expired', 'true');
    expect(fullImage).toHaveClass('is-full-loading');
  });

  it('shows a virtualized filmstrip, selects its thumbnail, and toggles it with the configured key', async () => {
    store.searchResults = [fixtures.first, fixtures.second, fixtures.third];
    render(<ImageModal />);

    const strip = screen.getByRole('region', { name: 'Image filmstrip' });
    expect(strip).toBeVisible();
    expect(strip).toHaveClass('is-layout');
    expect(strip).toHaveAttribute('data-presentation', 'layout');
    expect(within(screen.getByTestId('modal-image-area')).queryByRole('region', { name: 'Image filmstrip' })).toBeNull();
    expect(within(screen.getByTestId('modal-main-column')).getByRole('region', { name: 'Image filmstrip' })).toBe(strip);
    expect(screen.getByRole('option', { name: 'Open first.png, image 1 of 3' }))
      .toHaveAttribute('aria-current', 'true');

    const thirdOption = screen.getByRole('option', { name: 'Open third.png, image 3 of 3' });
    fireEvent.pointerDown(thirdOption, { pointerId: 7, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(thirdOption, { pointerId: 7, clientX: 180, clientY: 10 });
    fireEvent.pointerUp(thirdOption, { pointerId: 7, clientX: 180, clientY: 10 });
    expect(store.resolveModalNavigationTarget).not.toHaveBeenCalled();

    fireEvent.click(thirdOption);
    expect(store.setSelectedIndex).toHaveBeenCalledWith(2);
    expect(store.resolveModalNavigationTarget).not.toHaveBeenCalled();

    const toggle = screen.getAllByRole('button', { name: 'Hide image filmstrip' })
      .find((button) => button.hasAttribute('aria-expanded'));
    expect(toggle).toBeDefined();
    if (!toggle) throw new Error('expected the toolbar filmstrip toggle');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(toggle).toHaveAttribute('aria-keyshortcuts', 'T');

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.toggleFilmstrip });
    expect(store.setView).toHaveBeenCalledWith({ modalFilmstripOpen: false });
  });

  it('reveals the bottom portrait filmstrip as a front overlay only inside the bottom hover zone', async () => {
    vi.useFakeTimers();
    render(<ImageModal />);
    const dialog = screen.getByRole('dialog');
    mockRect(dialog, {});
    toggleManualChromeFromImage();
    expect(screen.queryByRole('region', { name: 'Image filmstrip' })).toBeNull();

    fireEvent.pointerMove(dialog, {
      pointerId: 2,
      pointerType: 'mouse',
      clientX: 500,
      clientY: 1000 - MODAL_FILMSTRIP_HOVER_ZONE_PX + 1,
    });
    const overlayStrip = screen.getByRole('region', { name: 'Image filmstrip' });
    expect(overlayStrip).toHaveClass('is-overlay');
    expect(overlayStrip).toHaveAttribute('data-presentation', 'overlay');
    expect(within(screen.getByTestId('modal-main-column')).getByRole('region', { name: 'Image filmstrip' }))
      .toBe(overlayStrip);

    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS);
    });
    expect(dialog).toHaveClass('chrome-hidden');
    expect(dialog).not.toHaveClass('cursor-hidden');
    expect(screen.getByRole('region', { name: 'Image filmstrip' })).toBe(overlayStrip);

    fireEvent.pointerMove(dialog, {
      pointerId: 2,
      pointerType: 'mouse',
      clientX: 500,
      clientY: 500,
    });
    expect(screen.queryByRole('region', { name: 'Image filmstrip' })).toBeNull();
    act(() => {
      vi.advanceTimersByTime(MODAL_CHROME_TRANSIENT_MS);
    });
    expect(dialog).toHaveClass('chrome-hidden', 'cursor-hidden');
  });

  it('routes modal shortcuts from a focused action button without double navigation', async () => {
    render(<ImageModal />);
    const favoriteButton = screen.getByRole('button', { name: 'Increase favorite level' });
    favoriteButton.focus();
    fireEvent.click(favoriteButton);
    expect(store.cycleFavoriteLevel).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(favoriteButton, { key: DEFAULT_KEY_BINDINGS.nextImage });
    await waitFor(() => expect(store.resolveModalNavigationTarget).toHaveBeenCalledTimes(1));
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'next');

    fireEvent.keyDown(favoriteButton, { key: DEFAULT_KEY_BINDINGS.toggleFilmstrip });
    expect(store.setView).toHaveBeenCalledTimes(1);
    expect(store.setView).toHaveBeenCalledWith({ modalFilmstripOpen: false });

    fireEvent.keyDown(favoriteButton, { key: ' ' });
    fireEvent.keyDown(favoriteButton, { key: 'Enter' });
    expect(store.cycleFavoriteLevel).toHaveBeenCalledTimes(1);
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledTimes(1);
  });

  it('always confirms a favorite even when ordinary delete confirmation is disabled', async () => {
    store.favorites = { [firstImage.id]: 4 };
    store.deleteImage.mockResolvedValue(false);
    render(<ImageModal />);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('favorite level 4');
    expect(within(dialog).queryByText('Do not ask again')).not.toBeInTheDocument();
    expect(store.deleteImage).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(store.deleteImage).not.toHaveBeenCalled();

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    const confirmedDialog = await screen.findByRole('alertdialog');
    fireEvent.click(within(confirmedDialog).getByRole('button', { name: 'Move to Recycle Bin' }));
    await waitFor(() => {
      expect(store.deleteImage).toHaveBeenCalledWith(firstImage.id, { favoriteConfirmed: true });
    });
  });

  it('closes and clears a deleted modal when its sparse neighbor is unavailable', async () => {
    store.deleteImage.mockResolvedValue(true);
    store.resolveModalNavigationTarget.mockResolvedValue({ status: 'unavailable' });
    render(<ImageModal />);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    await waitFor(() => {
      expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'delete');
      expect(store.setSelectedIndex).toHaveBeenCalledWith(null);
      expect(store.setModalImageIds).toHaveBeenCalledWith([]);
    });
  });

  it('does not overwrite a newer window when delete resolution becomes stale', async () => {
    store.deleteImage.mockResolvedValue(true);
    store.resolveModalNavigationTarget.mockResolvedValue({ status: 'stale' });
    render(<ImageModal />);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    await waitFor(() => expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(0, 'delete'));
    expect(store.setSelectedIndex).not.toHaveBeenCalled();
    expect(store.setModalImageIds).not.toHaveBeenCalled();
  });

  it('moves exactly once from a deleted middle image to its original right neighbor', async () => {
    store.searchResults = [fixtures.first, fixtures.second, fixtures.third];
    store.selectedIndex = 1;
    store.deleteImage.mockResolvedValue(true);
    store.resolveModalNavigationTarget.mockResolvedValue({
      status: 'found',
      index: 2,
      id: thirdImage.id,
      filteredOrderedIds: null,
    });
    const { rerender } = render(<ImageModal />);

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });
    await waitFor(() => expect(store.setSelectedIndex).toHaveBeenCalledWith(2));
    expect(store.deleteImage).toHaveBeenCalledTimes(1);
    expect(store.deleteImage).toHaveBeenCalledWith(secondImage.id, { favoriteConfirmed: false });
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledTimes(1);
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledWith(1, 'delete');
    expect(store.setSelectedIndex).toHaveBeenCalledTimes(1);

    // A late search refresh compacts [first, deleted, third] to [first, third].
    // It changes the surviving image's numeric slot, but must not trigger a
    // second navigation/selection commit that skips over it.
    store.searchResults = [fixtures.first, fixtures.third];
    store.selectedIndex = 1;
    rerender(<ImageModal />);

    expect(await screen.findByText('third.png')).toBeVisible();
    await Promise.resolve();
    expect(store.resolveModalNavigationTarget).toHaveBeenCalledTimes(1);
    expect(store.setSelectedIndex).toHaveBeenCalledTimes(1);
  });
});

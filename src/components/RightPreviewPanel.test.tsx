import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ImageFile } from '../lib/types';
import { useImageStore } from '../store/ImageContext';
import { useOptionalAlbumStore } from '../store/AlbumContext';
import RightPreviewPanel from './RightPreviewPanel';

vi.mock('../store/ImageContext', () => ({
  useImageStore: vi.fn(),
}));

vi.mock('../store/AlbumContext', () => ({
  useOptionalAlbumStore: vi.fn(),
}));

vi.mock('./EnhanceQueuePanel', () => ({
  EnhanceSettingsControls: () => null,
  createEnhancementJob: vi.fn(),
  getEnhancementSettings: vi.fn(),
}));

vi.mock('./CachedImage', () => ({
  default: ({ alt }: { alt: string }) => <span data-image-alt={alt} />,
}));

const setView = vi.fn();

const previewImage: ImageFile = {
  id: 'C:/images/preview.png',
  filename: 'preview.png',
  absolutePath: 'C:/images/preview.png',
  fileUrl: '/api/image?preview',
  displayUrl: '/api/image?preview&display=1',
  fullUrl: '/api/image?preview&full=1',
  metadata: { prompt: '', negativePrompt: '', settings: {} },
  createdAt: 1,
  mtime: 1,
};

function createStore(options: { width?: number; withPreview?: boolean; selectedIds?: string[]; favorites?: Record<string, number>; searchResults?: ImageFile[] } = {}) {
  const withPreview = options.withPreview ?? false;
  return {
    previewTabIds: withPreview ? [previewImage.id] : [],
    activePreviewId: withPreview ? previewImage.id : null,
    previewById: withPreview ? { [previewImage.id]: previewImage } : {},
    cycleFavoriteLevel: vi.fn(),
    decreaseFavoriteLevel: vi.fn(),
    setFavoriteLevels: vi.fn(),
    adjustFavoriteLevels: vi.fn(),
    favorites: options.favorites ?? {},
    openExternal: vi.fn(),
    selectedIds: options.selectedIds ?? [],
    searchResults: options.searchResults ?? [],
    clearSelection: vi.fn(),
    deleteImage: vi.fn(),
    view: {
      rightPanelOpen: true,
      rightPanelWidth: options.width ?? 320,
      enhanceQueueOpen: true,
    },
    setView,
    confirmBeforeDelete: true,
    setConfirmBeforeDelete: vi.fn(),
  } as unknown as ReturnType<typeof useImageStore>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(useImageStore).mockReturnValue(createStore());
  vi.mocked(useOptionalAlbumStore).mockReturnValue({
    activeSource: null,
    recycleSource: vi.fn(),
  } as unknown as ReturnType<typeof useOptionalAlbumStore>);
});

describe('RightPreviewPanel resize separator', () => {
  it('keeps the empty panel resize handle as an accessible vertical separator', () => {
    render(<RightPreviewPanel />);

    const separator = screen.getByRole('separator', { name: 'Resize preview panel' });
    expect(separator).toHaveAttribute('aria-orientation', 'vertical');
    expect(separator).toHaveAttribute('aria-valuemin', '240');
    expect(separator).toHaveAttribute('aria-valuemax', '900');
    expect(separator).toHaveAttribute('aria-valuenow', '320');
    expect(separator).toHaveAttribute('tabindex', '0');
  });

  it('preserves mouse resizing and persists the released width', () => {
    render(<RightPreviewPanel />);
    const separator = screen.getByRole('separator', { name: 'Resize preview panel' });

    fireEvent.mouseDown(separator, { button: 0, clientX: 600 });
    fireEvent.mouseMove(document, { clientX: 500 });
    expect(separator).toHaveAttribute('aria-valuenow', '420');
    fireEvent.mouseUp(document);

    expect(setView).toHaveBeenLastCalledWith({ rightPanelWidth: 420 });
  });

  it('clamps keyboard resizing and persists every keyboard width change', () => {
    render(<RightPreviewPanel />);
    const separator = screen.getByRole('separator', { name: 'Resize preview panel' });

    separator.focus();
    fireEvent.keyDown(separator, { key: 'ArrowLeft' });
    expect(separator).toHaveAttribute('aria-valuenow', '340');
    expect(setView).toHaveBeenLastCalledWith({ rightPanelWidth: 340 });

    fireEvent.keyDown(separator, { key: 'ArrowRight', shiftKey: true });
    expect(separator).toHaveAttribute('aria-valuenow', '240');
    expect(setView).toHaveBeenLastCalledWith({ rightPanelWidth: 240 });

    fireEvent.keyDown(separator, { key: 'End' });
    expect(separator).toHaveAttribute('aria-valuenow', '900');
    fireEvent.keyDown(separator, { key: 'ArrowLeft', shiftKey: true });
    expect(separator).toHaveAttribute('aria-valuenow', '900');
    expect(setView).toHaveBeenLastCalledWith({ rightPanelWidth: 900 });
  });

  it('uses the same separator in the normal preview panel', () => {
    const { rerender } = render(<RightPreviewPanel />);
    vi.mocked(useImageStore).mockReturnValue(createStore({ withPreview: true }));
    rerender(<RightPreviewPanel />);

    expect(screen.getAllByRole('separator', { name: 'Resize preview panel' })).toHaveLength(1);
  });
});

describe('RightPreviewPanel bulk favorite levels', () => {
  it('reports mixed selection, applies an exact level once, and leaves stale paths disabled', () => {
    const selectedIds = [previewImage.id, 'C:/images/second.png', 'C:/images/stale.png'];
    const secondImage = { ...previewImage, id: 'C:/images/second.png' };
    const store = createStore({
      withPreview: true,
      selectedIds,
      searchResults: [previewImage, secondImage],
      favorites: { [previewImage.id]: 1, [secondImage.id]: 4 },
    });
    vi.mocked(useImageStore).mockReturnValue(store);

    render(<RightPreviewPanel />);

    expect(screen.getByText('Mixed levels (Lv1, Lv4) for 2 selected')).toBeInTheDocument();
    expect(screen.getByText('1 unavailable')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Set selected images to favorite level 3' }));
    expect(store.setFavoriteLevels).toHaveBeenCalledWith([previewImage.id, secondImage.id], 3);
    fireEvent.click(screen.getByRole('button', { name: 'Decrease favorite level for selected images' }));
    expect(store.adjustFavoriteLevels).toHaveBeenCalledWith([previewImage.id, secondImage.id], -1);
  });

  it('disables favorite mutations when every selected path is stale', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({
      withPreview: true,
      selectedIds: ['C:/images/stale.png'],
      searchResults: [],
    }));
    render(<RightPreviewPanel />);

    expect(screen.getByText(/no longer in the current result/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Set selected images to favorite level 1' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Increase favorite level for selected images' })).toBeDisabled();
  });
});

describe('RightPreviewPanel external open results', () => {
  it('uses the Album source token and reports measured current/outside/missing outcomes', async () => {
    const outsideId = 'D:/outside/outside.png';
    const missingId = 'E:/missing/missing.png';
    const outsideImage = { ...previewImage, id: outsideId, filename: 'outside.png' };
    const store = createStore({
      withPreview: true,
      selectedIds: [previewImage.id, outsideId, missingId],
      searchResults: [previewImage],
    });
    vi.mocked(store.openExternal).mockImplementation(async (id) => id !== missingId);
    vi.mocked(useImageStore).mockReturnValue(store);
    vi.mocked(useOptionalAlbumStore).mockReturnValue({
      activeSource: {
        sourceToken: 'album-source-token',
        images: [previewImage, outsideImage],
      },
      recycleSource: vi.fn(),
    } as unknown as ReturnType<typeof useOptionalAlbumStore>);

    render(<RightPreviewPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Open' }));

    expect(await screen.findByText('Opened 2/3 image(s) in external viewer. 1 failed.')).toBeInTheDocument();
    expect(store.openExternal).toHaveBeenNthCalledWith(1, previewImage.id, { indexToken: 'album-source-token' });
    expect(store.openExternal).toHaveBeenNthCalledWith(2, outsideId, { indexToken: 'album-source-token' });
    expect(store.openExternal).toHaveBeenNthCalledWith(3, missingId, { indexToken: 'album-source-token' });
  });

  it('reports a failed single open and passes the catalog session token', async () => {
    const store = createStore({ withPreview: true, searchResults: [previewImage] });
    store.indexToken = 'catalog-token';
    vi.mocked(store.openExternal).mockResolvedValue(false);
    vi.mocked(useImageStore).mockReturnValue(store);

    render(<RightPreviewPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Open External' }));

    await waitFor(() => {
      expect(store.openExternal).toHaveBeenCalledWith(previewImage.id, { indexToken: 'catalog-token' });
    });
    expect(screen.getByText('Opened 0/1 image(s) in external viewer. 1 failed.')).toBeInTheDocument();
  });
});

describe('RightPreviewPanel bulk recycle', () => {
  it('requires confirmation for any favorite even when ordinary confirmation is disabled', () => {
    const secondId = 'C:/images/second.png';
    const store = createStore({
      withPreview: true,
      selectedIds: [previewImage.id, secondId],
      searchResults: [previewImage, { ...previewImage, id: secondId }],
      favorites: { [secondId]: 5 },
    });
    store.confirmBeforeDelete = false;
    vi.mocked(useImageStore).mockReturnValue(store);
    render(<RightPreviewPanel />);

    fireEvent.click(screen.getByRole('button', { name: 'Recycle' }));

    expect(screen.getByRole('alertdialog')).toHaveTextContent('1 favorite image(s) are included');
    expect(screen.queryByText('Do not ask again')).not.toBeInTheDocument();
    expect(store.deleteImage).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(store.deleteImage).not.toHaveBeenCalled();
  });

  it('keeps failed image ids selected for retry after a partial recycle', async () => {
    const secondId = 'C:/images/second.png';
    const store = createStore({
      withPreview: true,
      selectedIds: [previewImage.id, secondId],
      searchResults: [previewImage, { ...previewImage, id: secondId }],
    });
    vi.mocked(store.deleteImage)
      .mockResolvedValueOnce(true)
      .mockResolvedValueOnce(false);
    vi.mocked(useImageStore).mockReturnValue(store);
    render(<RightPreviewPanel />);

    fireEvent.click(screen.getByRole('button', { name: 'Recycle' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move to Recycle Bin' }));

    expect(await screen.findByText('Moved 1/2 image(s) to Recycle Bin. 1 failed and remain selected for retry.'))
      .toBeInTheDocument();
    expect(store.deleteImage).toHaveBeenNthCalledWith(1, previewImage.id, { favoriteConfirmed: true });
    expect(store.deleteImage).toHaveBeenNthCalledWith(2, secondId, { favoriteConfirmed: true });
    expect(store.clearSelection).not.toHaveBeenCalled();
  });
});

describe('RightPreviewPanel search availability', () => {
  it('explains why modal navigation is unavailable for an active tab outside the loaded result', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({ withPreview: true, searchResults: [] }));

    render(<RightPreviewPanel />);

    const status = screen.getByRole('status', { name: 'Preview search availability' });
    expect(status).toHaveTextContent('Outside current search/filter');
    expect(status).toHaveTextContent('Modal navigation is unavailable until filters change to include this image.');
    expect(screen.getByRole('button', { name: /Favorite \+1/i })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Open External' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Show Details' })).toBeEnabled();
  });

  it('hides the status when the active image joins the loaded result', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({ withPreview: true, searchResults: [] }));
    const { rerender } = render(<RightPreviewPanel />);
    expect(screen.getByRole('status', { name: 'Preview search availability' })).toBeInTheDocument();

    vi.mocked(useImageStore).mockReturnValue(createStore({ withPreview: true, searchResults: [previewImage] }));
    rerender(<RightPreviewPanel />);

    expect(screen.queryByRole('status', { name: 'Preview search availability' })).not.toBeInTheDocument();
  });

  it('does not show search availability without an active preview tab', () => {
    render(<RightPreviewPanel />);

    expect(screen.queryByRole('status', { name: 'Preview search availability' })).not.toBeInTheDocument();
  });
});

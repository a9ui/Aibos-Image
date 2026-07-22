import React from 'react';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ImageFile } from '../lib/types';
import { useImageStore } from '../store/ImageContext';
import BottomPreviewTabs from './BottomPreviewTabs';

vi.mock('../store/ImageContext', () => ({
  useImageStore: vi.fn(),
}));

vi.mock('./CachedImage', () => ({
  default: ({ alt }: { alt: string }) => <span data-testid="bottom-preview-hover-image" data-image-alt={alt} />,
}));

const firstImage: ImageFile = {
  id: 'C:/images/first.png',
  filename: 'first.png',
  absolutePath: 'C:/images/first.png',
  fileUrl: '/api/image?first',
  displayUrl: '/api/image?first&display=1',
  fullUrl: '/api/image?first&full=1',
  metadata: { prompt: '', negativePrompt: '', settings: {} },
  createdAt: 1,
  mtime: 1,
};

const secondImage: ImageFile = {
  ...firstImage,
  id: 'C:/images/second.png',
  filename: 'second.png',
  absolutePath: 'C:/images/second.png',
};

const setActivePreviewId = vi.fn();
const setSelectedIndex = vi.fn();
const closePreviewTab = vi.fn();
const reorderPreviewTab = vi.fn();
const togglePinPreviewTab = vi.fn();
const restoreLastClosedPreview = vi.fn();

function createStore(options: { activeId?: string | null; pinnedIds?: string[] } = {}) {
  return {
    previewTabIds: [firstImage.id, secondImage.id],
    activePreviewId: options.activeId === undefined ? firstImage.id : options.activeId,
    previewById: {
      [firstImage.id]: firstImage,
      [secondImage.id]: secondImage,
    },
    searchResults: [firstImage, secondImage],
    pinnedPreviewIds: options.pinnedIds ?? [],
    closedPreviewTabCount: 0,
    setActivePreviewId,
    setSelectedIndex,
    closePreviewTab,
    reorderPreviewTab,
    togglePinPreviewTab,
    restoreLastClosedPreview,
  } as unknown as ReturnType<typeof useImageStore>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(useImageStore).mockReturnValue(createStore());
});

describe('BottomPreviewTabs', () => {
  it('does not render a bottom surface when both open and closed tab history are empty', () => {
    vi.mocked(useImageStore).mockReturnValue({
      ...createStore(),
      previewTabIds: [],
      activePreviewId: null,
      closedPreviewTabCount: 0,
    });
    render(<BottomPreviewTabs />);

    expect(screen.queryByRole('region', { name: 'Recently closed preview tabs' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tablist', { name: 'Open preview tabs' })).not.toBeInTheDocument();
  });

  it('shows the compact Restore surface when only closed tab history remains', () => {
    vi.mocked(useImageStore).mockReturnValue({
      ...createStore(),
      previewTabIds: [],
      activePreviewId: null,
      closedPreviewTabCount: 2,
    });
    render(<BottomPreviewTabs />);

    const restore = screen.getByRole('button', { name: /restore last closed preview tab/i });
    expect(screen.getByRole('region', { name: 'Recently closed preview tabs' })).toBeInTheDocument();
    expect(restore).toHaveAttribute('aria-keyshortcuts', 'Control+Shift+T Meta+Shift+T');
    fireEvent.click(restore);
    expect(restoreLastClosedPreview).toHaveBeenCalledTimes(1);
  });

  it('keeps the first tab keyboard reachable while a restored active id is unavailable', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({ activeId: null }));
    render(<BottomPreviewTabs />);

    expect(screen.getByRole('tab', { name: /open preview first\.png/i }))
      .toHaveAttribute('tabindex', '0');
    expect(screen.getByRole('tab', { name: /open preview second\.png/i }))
      .toHaveAttribute('tabindex', '-1');
  });

  it('uses a roving tab stop and activates the focused preview with keyboard', () => {
    render(<BottomPreviewTabs />);

    const firstTab = screen.getByRole('tab', { name: /open preview first\.png/i });
    const secondTab = screen.getByRole('tab', { name: /open preview second\.png/i });
    expect(firstTab).toHaveAttribute('aria-selected', 'true');
    expect(firstTab).toHaveAttribute('tabindex', '0');

    firstTab.focus();
    fireEvent.keyDown(firstTab, { key: 'ArrowRight' });
    expect(secondTab).toHaveFocus();
    expect(secondTab).toHaveAttribute('tabindex', '0');

    fireEvent.keyDown(secondTab, { key: 'Enter' });
    expect(setActivePreviewId).toHaveBeenCalledWith(secondImage.id);
    expect(setSelectedIndex).toHaveBeenCalledWith(1);

    fireEvent.keyDown(secondTab, { key: ' ' });
    expect(setActivePreviewId).toHaveBeenLastCalledWith(secondImage.id);

    fireEvent.keyDown(secondTab, { key: 'Home' });
    expect(firstTab).toHaveFocus();
    fireEvent.keyDown(firstTab, { key: 'End' });
    expect(secondTab).toHaveFocus();
  });

  it('reorders with the documented Alt+Shift+Arrow chord without changing active state', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({ pinnedIds: [secondImage.id] }));
    render(<BottomPreviewTabs />);
    const firstTab = screen.getByRole('tab', { name: /open preview first\.png/i });

    fireEvent.keyDown(firstTab, { key: 'ArrowRight', altKey: true, shiftKey: true });

    expect(reorderPreviewTab).toHaveBeenCalledWith(firstImage.id, 1);
    expect(setActivePreviewId).not.toHaveBeenCalled();
    expect(firstTab).toHaveAttribute('aria-keyshortcuts', 'Alt+Shift+ArrowLeft Alt+Shift+ArrowRight');
  });

  it('shows a before-or-after drop marker and dispatches a pointer reorder', () => {
    render(<BottomPreviewTabs />);
    const firstTab = screen.getByRole('tab', { name: /open preview first\.png/i });
    const secondTab = screen.getByRole('tab', { name: /open preview second\.png/i });
    const firstSurface = screen.getByTestId(`bottom-preview-tab-${firstImage.id}`);
    const secondSurface = screen.getByTestId(`bottom-preview-tab-${secondImage.id}`);
    Object.defineProperty(secondSurface, 'getBoundingClientRect', {
      value: () => ({ left: 0, width: 100 }),
    });
    const dataTransfer = {
      effectAllowed: '',
      dropEffect: '',
      setData: vi.fn(),
      getData: vi.fn(() => firstImage.id),
    };

    fireEvent.dragStart(firstSurface, { dataTransfer });
    fireEvent.dragOver(secondSurface, { dataTransfer, clientX: 80 });
    expect(secondSurface).toHaveClass('is-drop-after');
    fireEvent.drop(secondSurface, { dataTransfer, clientX: 80 });

    expect(reorderPreviewTab).toHaveBeenCalledWith(firstImage.id, 1);
    expect(setActivePreviewId).not.toHaveBeenCalled();
  });

  it('keeps pin and close as independently reachable sibling controls', () => {
    render(<BottomPreviewTabs />);

    const firstTab = screen.getByRole('tab', { name: /open preview first\.png/i });
    const pin = screen.getByRole('button', { name: /pin preview first\.png/i });
    const close = screen.getByRole('button', { name: /close preview first\.png/i });

    expect(within(firstTab).queryByRole('button')).not.toBeInTheDocument();

    fireEvent.click(pin);
    expect(togglePinPreviewTab).toHaveBeenCalledWith(firstImage.id);
    expect(setActivePreviewId).not.toHaveBeenCalled();

    fireEvent.click(close);
    expect(closePreviewTab).toHaveBeenCalledWith(firstImage.id);
  });

  it('closes a preview from the tab middle click without activating it', () => {
    render(<BottomPreviewTabs />);

    fireEvent(
      screen.getByRole('tab', { name: /open preview second\.png/i }),
      new MouseEvent('auxclick', { bubbles: true, button: 1 })
    );

    expect(closePreviewTab).toHaveBeenCalledWith(secondImage.id);
    expect(setActivePreviewId).not.toHaveBeenCalled();
  });

  it('shows and clears the hover preview from the complete tab surface', () => {
    render(<BottomPreviewTabs />);
    const firstTab = screen.getByRole('tab', { name: /open preview first\.png/i });

    fireEvent.mouseEnter(firstTab);
    expect(screen.getByTestId('bottom-preview-hover-image')).toHaveAttribute('data-image-alt', 'first.png');

    fireEvent.mouseLeave(firstTab);
    expect(screen.queryByTestId('bottom-preview-hover-image')).not.toBeInTheDocument();
  });
});

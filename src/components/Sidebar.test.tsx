import React from 'react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Sidebar from './Sidebar';
import { useImageStore } from '../store/ImageContext';

vi.mock('../store/ImageContext', () => ({
  useImageStore: vi.fn(),
}));

const toggleFavoriteFilterLevel = vi.fn();
const clearFavoriteFilterLevels = vi.fn();
const setView = vi.fn();
const sidebarCss = readFileSync(join(process.cwd(), 'src/app/globals.css'), 'utf8');

function createStore(options: {
  dirPath?: string;
  hiddenFolders?: string[];
  searchTotal?: number;
  totalIndexed?: number;
  searchResults?: Array<{ id: string } | null>;
  showFavOnly?: boolean;
  favorites?: Record<string, number>;
  foldersExpanded?: boolean;
  indexToken?: string | null;
} = {}) {
  return {
    view: {
      viewMode: 'grid',
      thumbSize: 200,
      aspectMode: 'original',
      displayStyle: 'standard',
      columns: 0,
      sidebarOpen: true,
      rightPanelOpen: true,
      rightPanelWidth: 320,
      sortBy: 'newest',
      randomSeed: 'default',
      folderSortBy: 'name-asc',
      modalEdgeRatio: 0.28,
      enhanceQueueOpen: true,
      dateFrom: '',
      dateTo: '',
      hiddenFolders: options.hiddenFolders ?? [],
      showUnseenMarkers: false,
      foldersExpanded: options.foldersExpanded ?? true,
    },
    setView,
    setSearchQuery: vi.fn(),
    dirPath: options.dirPath ?? '',
    setDirPath: vi.fn(),
    startScan: vi.fn(),
    totalIndexed: options.totalIndexed ?? 0,
    searchTotal: options.searchTotal ?? 0,
    searchResults: options.searchResults ?? [],
    searchQuery: '',
    favorites: options.favorites ?? {},
    showFavOnly: options.showFavOnly ?? true,
    setShowFavOnly: vi.fn(),
    showUnfavOnly: false,
    setShowUnfavOnly: vi.fn(),
    favoriteFilterLevels: [2, 4],
    toggleFavoriteFilterLevel,
    clearFavoriteFilterLevels,
    showEnhancedOnly: false,
    setShowEnhancedOnly: vi.fn(),
    enhancedSourceIds: {},
    setShowSettings: vi.fn(),
    setPhase: vi.fn(),
    perfEnabled: false,
    setPerfEnabled: vi.fn(),
    perfStats: { searchCount: 0, lastSearchMs: 0, avgSearchMs: 0 },
    indexToken: options.indexToken ?? null,
  } as unknown as ReturnType<typeof useImageStore>;
}

describe('Sidebar favorite level controls', () => {
  beforeEach(() => {
    setView.mockReset();
    toggleFavoriteFilterLevel.mockReset();
    clearFavoriteFilterLevels.mockReset();
    vi.mocked(useImageStore).mockReturnValue(createStore());
  });

  it('renders independent levels and All semantics', async () => {
    const user = userEvent.setup();
    render(<Sidebar />);

    expect(screen.getByRole('checkbox', { name: 'Favorite level 1' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Favorite level 2' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Favorite level 4' })).toBeChecked();

    await user.click(screen.getByRole('checkbox', { name: 'Favorite level 1' }));
    expect(toggleFavoriteFilterLevel).toHaveBeenCalledWith(1);

    await user.click(screen.getByRole('button', { name: 'All' }));
    expect(clearFavoriteFilterLevels).toHaveBeenCalledTimes(1);
  });

  it('keeps custom date inputs while omitting quick-search and date presets', () => {
    render(<Sidebar />);

    expect(screen.queryByText('Quick Search')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Today' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '7d' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '30d' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'This year' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Date from')).toBeInTheDocument();
    expect(screen.getByLabelText('Date to')).toBeInTheDocument();
  });

  it('changes only the gallery thumbnail size from the bounded Size slider', () => {
    render(<Sidebar />);

    const slider = screen.getByRole('slider', { name: 'Thumbnail size' });
    expect(slider).toHaveAttribute('min', '20');
    expect(slider).toHaveAttribute('max', '600');
    expect(slider).toHaveAttribute('step', '20');
    fireEvent.change(slider, { target: { value: '260' } });

    expect(setView).toHaveBeenCalledWith({ thumbSize: 260 });
  });

  it('labels the maximum thumbnail endpoint as a one-column layout', () => {
    const store = createStore();
    store.view.thumbSize = 600;
    vi.mocked(useImageStore).mockReturnValue(store);

    render(<Sidebar />);

    expect(screen.getByText('1 column')).toBeInTheDocument();
  });

  it('keeps the Size slider shrinkable without creating horizontal sidebar scroll', () => {
    render(<Sidebar />);

    const sizeRow = screen.getByRole('group', { name: 'Thumbnail size control' });
    expect(sizeRow).toHaveClass('sidebar-size-row');
    expect(within(sizeRow).getByRole('slider', { name: 'Thumbnail size' })).toBeVisible();

    const sidebarRule = sidebarCss.match(/\.sidebar\s*\{([\s\S]*?)\}/)?.[1] ?? '';
    const sectionRule = sidebarCss.match(/\.sidebar-section\s*\{([\s\S]*?)\}/)?.[1] ?? '';
    const sizeSliderRule = sidebarCss.match(/\.sidebar-size-row \.sidebar-slider\s*\{([\s\S]*?)\}/)?.[1] ?? '';
    expect(sidebarRule).toMatch(/overflow-x:\s*hidden/);
    expect(sectionRule).toMatch(/min-width:\s*0/);
    expect(sizeSliderRule).toMatch(/min-width:\s*0/);
    expect(sizeSliderRule).toMatch(/width:\s*0/);
  });

  it('labels sparse client-filtered matches separately from indexed totals without a live region', () => {
    vi.mocked(useImageStore).mockReturnValue(createStore({
      searchTotal: 900,
      totalIndexed: 900,
      searchResults: [{ id: 'favorite' }, null, { id: 'unrated' }],
      favorites: { favorite: 2 },
    }));

    render(<Sidebar />);

    const count = screen.getByText('1 shown · 900 indexed');
    expect(count).toHaveAttribute('aria-label', 'Results: 1 shown · 900 indexed');
    expect(count).not.toHaveAttribute('aria-live');
  });

  it('collapses and restores the Folders section from its heading', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<Sidebar />);

    const foldersToggle = screen.getByRole('button', { name: 'Folders' });
    const emptyFoldersMessage = screen.getByText('No folders found under this root.');
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'true');
    expect(emptyFoldersMessage).toBeVisible();

    await user.click(foldersToggle);
    expect(setView).toHaveBeenCalledWith({ foldersExpanded: false });
    vi.mocked(useImageStore).mockReturnValue(createStore({ foldersExpanded: false }));
    rerender(<Sidebar />);
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'false');
    expect(emptyFoldersMessage).not.toBeVisible();

    await user.click(foldersToggle);
    expect(setView).toHaveBeenCalledWith({ foldersExpanded: true });
    vi.mocked(useImageStore).mockReturnValue(createStore({ foldersExpanded: true }));
    rerender(<Sidebar />);
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'true');
    expect(emptyFoldersMessage).toBeVisible();
  });

  it('returns focus to the folders heading before collapsing its focused child', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      json: () => Promise.resolve({ folders: [{ key: 'alpha', label: 'Alpha', count: 12 }] }),
    })));
    vi.mocked(useImageStore).mockReturnValue(createStore({ dirPath: 'C:/images' }));
    render(<Sidebar />);

    const foldersToggle = screen.getByRole('button', { name: 'Folders' });
    const child = await screen.findByRole('button', { name: 'Select folder Alpha, 12 images' });
    child.focus();
    fireEvent.click(foldersToggle);

    expect(foldersToggle).toHaveFocus();
    expect(setView).toHaveBeenCalledWith({ foldersExpanded: false });
  });
});

describe('Sidebar folder controls', () => {
  beforeEach(() => {
    setView.mockReset();
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      json: () => Promise.resolve({
        folders: [
          { key: 'alpha', label: 'Alpha', count: 12 },
          { key: 'beta', label: 'Beta', count: 3 },
        ],
      }),
    })));
    vi.mocked(useImageStore).mockReturnValue(createStore({ dirPath: 'C:/images' }));
  });

  it('scopes folder buckets to the active catalog index session', async () => {
    const fetchMock = vi.mocked(globalThis.fetch);
    vi.mocked(useImageStore).mockReturnValue(createStore({
      dirPath: 'C:/images',
      indexToken: 'catalog token/1',
    }));

    render(<Sidebar />);
    await screen.findByRole('button', { name: 'Select folder Alpha, 12 images' });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/folders?dir=C%3A%2Fimages&indexToken=catalog%20token%2F1'
    );
  });

  it('exposes folder selection and visibility as separate keyboard controls', async () => {
    const user = userEvent.setup();
    render(<Sidebar />);

    const selectAlpha = await screen.findByRole('button', { name: 'Select folder Alpha, 12 images' });
    const visibilityAlpha = screen.getByRole('button', { name: 'Hide folder Alpha' });

    expect(selectAlpha).toHaveAttribute('aria-pressed', 'false');
    expect(visibilityAlpha).toHaveAttribute('aria-pressed', 'true');
    expect(within(selectAlpha).queryByRole('checkbox')).not.toBeInTheDocument();

    selectAlpha.focus();
    await user.keyboard('{Enter}');
    expect(selectAlpha).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('checkbox', { name: 'Select Alpha' })).toBeChecked();

    await user.click(visibilityAlpha);
    expect(setView).toHaveBeenCalledWith({ hiddenFolders: ['alpha'] });
  });

  it('keeps folder sorting and collapse controls semantically stateful by keyboard', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<Sidebar />);

    const foldersToggle = screen.getByRole('button', { name: 'Folders' });
    const selectAlpha = await screen.findByRole('button', { name: 'Select folder Alpha, 12 images' });
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'true');
    expect(selectAlpha).toBeVisible();

    foldersToggle.focus();
    await user.keyboard(' ');
    expect(setView).toHaveBeenCalledWith({ foldersExpanded: false });
    vi.mocked(useImageStore).mockReturnValue(createStore({ dirPath: 'C:/images', foldersExpanded: false }));
    rerender(<Sidebar />);
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'false');
    expect(selectAlpha).not.toBeVisible();

    await user.keyboard('{Enter}');
    expect(setView).toHaveBeenCalledWith({ foldersExpanded: true });
    vi.mocked(useImageStore).mockReturnValue(createStore({ dirPath: 'C:/images', foldersExpanded: true }));
    rerender(<Sidebar />);
    expect(foldersToggle).toHaveAttribute('aria-expanded', 'true');
    expect(selectAlpha).toBeVisible();
    expect(screen.getByRole('button', { name: 'Sort folders A to Z' })).toHaveAttribute('aria-pressed', 'true');
    await user.click(screen.getByRole('button', { name: 'Sort folders by image count' }));
    expect(setView).toHaveBeenCalledWith({ folderSortBy: 'count-desc' });
  });
});

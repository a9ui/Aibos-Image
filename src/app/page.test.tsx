import React, { useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  useImageStore: vi.fn(),
  cancelScan: vi.fn(),
  startScan: vi.fn(),
  setDirPath: vi.fn(),
  fetch: vi.fn(),
}));

vi.mock('../store/ImageContext', () => ({
  ImageProvider: ({ children }: { children: React.ReactNode }) => children,
  useImageStore: mocks.useImageStore,
}));

vi.mock('../components/SearchBar', () => ({ default: () => null }));
vi.mock('../components/ImageGrid', () => ({ default: () => null }));
vi.mock('../components/ImageModal', () => ({ default: () => null }));
vi.mock('../components/SettingsModal', () => ({ default: () => null }));
vi.mock('../components/Sidebar', () => ({ default: () => null }));
vi.mock('../components/RightPreviewPanel', () => ({ default: () => null }));
vi.mock('../components/BottomPreviewTabs', () => ({ default: () => null }));
vi.mock('../components/EnhanceQueuePanel', () => ({ default: () => null }));
vi.mock('../lib/localStorageMigration', () => ({ migrateLegacyPhotoviewerState: vi.fn() }));
vi.mock('../lib/useDialogFocus', () => ({ useDialogFocus: vi.fn() }));

import App from './page';

const baseView = {
  viewMode: 'grid' as const,
  thumbSize: 200,
  aspectMode: 'original' as const,
  displayStyle: 'standard' as const,
  columns: 0,
  sidebarOpen: true,
  rightPanelOpen: true,
  rightPanelWidth: 320,
  sortBy: 'newest' as const,
  randomSeed: 'default',
  folderSortBy: 'name-asc' as const,
  modalEdgeRatio: 0.28,
  enhanceQueueOpen: true,
  dateFrom: '',
  dateTo: '',
  hiddenFolders: [],
  showUnseenMarkers: true,
  foldersExpanded: true,
};

function snapshotFolderMemory() {
  return {
    recent: localStorage.getItem('pvu_recent_dirs'),
    last: localStorage.getItem('pvu_last_dir_set'),
    imported: localStorage.getItem('pvu_server_legacy_imported'),
  };
}

describe('landing scan cancellation focus', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('pvu_recent_dirs', JSON.stringify(['C:/previous']));
    localStorage.setItem('pvu_last_dir_set', 'C:/images');
    localStorage.setItem('pvu_server_legacy_imported', '1');
    mocks.cancelScan.mockReset();
    mocks.startScan.mockReset();
    mocks.setDirPath.mockReset();
    mocks.fetch.mockReset();
    mocks.fetch.mockResolvedValue({ ok: true, json: async () => ({}) });
    vi.stubGlobal('fetch', mocks.fetch);

    mocks.useImageStore.mockImplementation(() => {
      const [phase, setPhase] = useState<'landing' | 'scanning' | 'viewer'>('scanning');
      const cancelScan = () => {
        mocks.cancelScan();
        setPhase('landing');
      };
      return {
        phase,
        dirPath: 'C:/images',
        setDirPath: mocks.setDirPath,
        startScan: mocks.startScan,
        cancelScan,
        scanProgress: {
          processed: 4,
          total: 10,
          newFiles: 1,
          stage: 'scanning' as const,
          message: 'Scanning files...',
        },
        scanError: null,
        dismissScanError: vi.fn(),
        searchTotal: 0,
        searchResults: [],
        totalIndexed: 0,
        searchQuery: '',
        setPhase,
        view: baseView,
        setView: vi.fn(),
        selectedIds: [],
        deleteImage: vi.fn(async () => false),
        cycleFavoriteLevel: vi.fn(),
        decreaseFavoriteLevel: vi.fn(),
        selectedIndex: null,
        keyBindings: { deleteImage: 'Delete', toggleFavorite: 'f', decreaseFavorite: 'd' },
        confirmBeforeDelete: true,
        setConfirmBeforeDelete: vi.fn(),
        restoreLastClosedPreview: vi.fn(),
        setShowSettings: vi.fn(),
        favorites: {},
        showFavOnly: false,
        showUnfavOnly: false,
        favoriteFilterLevels: [],
        showEnhancedOnly: false,
        enhancedSourceIds: {},
      };
    });
  });

  it('returns keyboard focus to Open folder set and announces cancellation without persisting folder memory', async () => {
    const user = userEvent.setup();
    render(<App />);

    await waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
    const folderMemoryBeforeCancel = snapshotFolderMemory();
    mocks.fetch.mockClear();

    const cancelButton = screen.getByRole('button', { name: 'Cancel scan' });
    cancelButton.focus();
    expect(cancelButton).toHaveFocus();
    await user.keyboard('{Enter}');

    const openFolderSet = await screen.findByRole('button', { name: 'Open folder set' });
    await waitFor(() => expect(openFolderSet).toHaveFocus());
    expect(screen.getByRole('status')).toHaveTextContent('Scan cancelled. Folder selection preserved.');
    expect(screen.queryByRole('button', { name: 'Cancel scan' })).not.toBeInTheDocument();
    expect(mocks.cancelScan).toHaveBeenCalledTimes(1);
    expect(mocks.startScan).not.toHaveBeenCalled();
    expect(mocks.setDirPath).not.toHaveBeenCalled();
    expect(mocks.fetch).not.toHaveBeenCalled();
    expect(snapshotFolderMemory()).toEqual(folderMemoryBeforeCancel);
  });
});

import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useAlbumStore } from '../store/AlbumContext';
import { useImageStore } from '../store/ImageContext';
import AlbumLibrary from './AlbumLibrary';

vi.mock('../lib/useDialogFocus', () => ({ useDialogFocus: vi.fn() }));
vi.mock('../store/AlbumContext', () => ({ useAlbumStore: vi.fn() }));
vi.mock('../store/ImageContext', () => ({ useImageStore: vi.fn() }));

const album = {
  id: 'album-1',
  name: 'Review Album',
  pinned: false,
  coverMemberId: null,
  createdAtUtc: '2026-07-20T00:00:00.000Z',
  updatedAtUtc: '2026-07-20T00:00:00.000Z',
  revision: 1,
  members: [{
    id: 'member-1',
    imagePath: 'C:\\photos\\one.png',
    addedAtUtc: '2026-07-20T00:00:00.000Z',
  }],
};

function expectEveryButtonHasTooltip() {
  const buttons = screen.getAllByRole('button');
  expect(buttons.length).toBeGreaterThan(0);
  expect(buttons.every((button) => Boolean(button.title.trim()))).toBe(true);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(useAlbumStore).mockReturnValue({
    document: {
      version: 1,
      revision: 1,
      updatedAtUtc: '2026-07-20T00:00:00.000Z',
      albums: [album],
      recentAlbumIds: ['album-1'],
    },
    albums: [album],
    activeSource: {
      album,
      members: [{ memberId: 'member-1', imagePath: album.members[0].imagePath }],
    },
    loading: false,
    error: '',
    libraryOpen: true,
    setLibraryOpen: vi.fn(),
    createAlbum: vi.fn(async () => album),
    updateAlbum: vi.fn(async () => true),
    deleteAlbum: vi.fn(async () => true),
    openAlbum: vi.fn(async () => true),
    refreshAlbums: vi.fn(async () => undefined),
  } as unknown as ReturnType<typeof useAlbumStore>);
  vi.mocked(useImageStore).mockReturnValue({
    selectedIds: [album.members[0].imagePath],
  } as unknown as ReturnType<typeof useImageStore>);
});

describe('AlbumLibrary button affordances', () => {
  it('keeps every initial and confirmation action equipped with a nonempty tooltip', () => {
    render(<AlbumLibrary />);
    expectEveryButtonHasTooltip();

    fireEvent.click(screen.getByTitle('Rename Review Album'));
    expectEveryButtonHasTooltip();
    fireEvent.click(screen.getByTitle('Cancel renaming'));

    fireEvent.click(screen.getByTitle('Delete Album only'));
    expectEveryButtonHasTooltip();
  });
});

import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  deleteImage: vi.fn(),
  fetch: vi.fn(),
}));

vi.mock('./ImageContext', () => ({
  useImageStore: () => ({
    indexToken: 'catalog-token',
    selectedIds: [],
    clearSelection: vi.fn(),
    setSelectedIndex: vi.fn(),
    selectedIndex: null,
    setModalImageIds: vi.fn(),
    deleteImage: mocks.deleteImage,
    favorites: {},
    setPhase: vi.fn(),
  }),
}));

import { AlbumProvider, useAlbumStore } from './AlbumContext';

const imagePath = 'C:\\photos\\recycled & recover.jpg';
const stamp = '2026-07-20T00:00:00.000Z';
const album = {
  id: 'album-1',
  name: 'Recovery',
  pinned: false,
  coverMemberId: null,
  createdAtUtc: stamp,
  updatedAtUtc: stamp,
  revision: 1,
  members: [{ id: 'member-1', imagePath, addedAtUtc: stamp }],
};
const document = {
  version: 1 as const,
  revision: 1,
  updatedAtUtc: stamp,
  albums: [album],
  recentAlbumIds: ['album-1'],
};

function jsonResponse(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function source(availability: 'current' | 'missing' | null) {
  const members = availability === null ? [] : [{
    memberId: 'member-1',
    imagePath,
    availability,
    image: availability === 'current' ? {
      id: imagePath,
      filename: 'recycled & recover.jpg',
      absolutePath: imagePath,
      fileUrl: '/api/image',
      displayUrl: '/api/image',
      fullUrl: '/api/image',
      metadata: null,
      createdAt: 1,
      mtime: 1,
    } : null,
  }];
  return {
    album: { ...album, members: availability === null ? [] : album.members },
    documentRevision: availability === null ? 2 : 1,
    sourceToken: 'source-token',
    catalogExpired: false,
    members,
    images: members.flatMap((member) => member.image ? [member.image] : []),
  };
}

function Harness() {
  const store = useAlbumStore();
  const missingPaths = store.activeSource?.members
    .filter((member) => member.availability === 'missing')
    .map((member) => member.imagePath) ?? [];
  return (
    <>
      <button type="button" onClick={() => void store.openAlbum('album-1')}>Open Album</button>
      <button type="button" onClick={() => void store.recycleSource(imagePath)}>Recycle source</button>
      <button type="button" onClick={() => void store.cleanupPaths(missingPaths)}>Retry cleanup</button>
      <output aria-label="album error">{store.error}</output>
      <output aria-label="availability">{store.activeSource?.members[0]?.availability ?? 'none'}</output>
      <output aria-label="member count">{store.activeSource?.members.length ?? -1}</output>
    </>
  );
}

describe('Album recycle cleanup recovery', () => {
  beforeEach(() => {
    mocks.deleteImage.mockReset().mockResolvedValue(true);
    mocks.fetch.mockReset();
    vi.stubGlobal('fetch', mocks.fetch);
  });

  it('records recent against latest state without a revision before opening the source', async () => {
    mocks.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith('/recent')) {
        return jsonResponse({ ok: true, changed: false, document, album });
      }
      if (url.includes('/source')) {
        return jsonResponse({ ok: true, source: source('current') });
      }
      throw new Error(`Unexpected fetch: ${url} ${init?.method ?? 'GET'}`);
    });

    const user = userEvent.setup();
    render(<AlbumProvider><Harness /></AlbumProvider>);
    await user.click(screen.getByRole('button', { name: 'Open Album' }));

    await waitFor(() => expect(screen.getByLabelText('availability')).toHaveTextContent('current'));
    const recentCall = mocks.fetch.mock.calls.find(([url]) => String(url).endsWith('/recent'));
    expect(JSON.parse(String(recentCall?.[1]?.body))).toEqual({});
  });

  it('stops a deleted Album 404 before source refresh and preserves the mutation error', async () => {
    mocks.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith('/recent')) {
        return jsonResponse({ ok: false, changed: false, error: 'Album not found.' }, 404);
      }
      if (url.includes('/source')) {
        return jsonResponse({ ok: true, source: source('current') });
      }
      throw new Error(`Unexpected fetch: ${url} ${init?.method ?? 'GET'}`);
    });

    const user = userEvent.setup();
    render(<AlbumProvider><Harness /></AlbumProvider>);
    await user.click(screen.getByRole('button', { name: 'Open Album' }));

    await waitFor(() => expect(screen.getByLabelText('album error')).toHaveTextContent('Album not found.'));
    expect(mocks.fetch.mock.calls.some(([url]) => String(url).includes('/source'))).toBe(false);
    expect(screen.getByLabelText('availability')).toHaveTextContent('none');
  });

  it('keeps source Recycle successful, exposes pending cleanup after refresh, and retries against latest state', async () => {
    let cleanupAttempts = 0;
    mocks.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.endsWith('/recent')) {
        return jsonResponse({ ok: true, changed: false, document, album });
      }
      if (url.includes('/source')) {
        return jsonResponse({
          ok: true,
          source: source(cleanupAttempts === 0 ? 'current' : cleanupAttempts === 1 ? 'missing' : null),
        });
      }
      if (url === '/api/albums/members/cleanup') {
        cleanupAttempts += 1;
        return cleanupAttempts === 1
          ? jsonResponse({ ok: false, changed: false, error: 'shared Album store is busy' }, 503)
          : jsonResponse({ ok: true, changed: true, document: { ...document, revision: 2, albums: [{ ...album, members: [] }] } });
      }
      throw new Error(`Unexpected fetch: ${url} ${init?.method ?? 'GET'}`);
    });

    const user = userEvent.setup();
    render(<AlbumProvider><Harness /></AlbumProvider>);

    await user.click(screen.getByRole('button', { name: 'Open Album' }));
    await waitFor(() => expect(screen.getByLabelText('availability')).toHaveTextContent('current'));

    await user.click(screen.getByRole('button', { name: 'Recycle source' }));
    await waitFor(() => {
      expect(screen.getByLabelText('availability')).toHaveTextContent('missing');
      expect(screen.getByLabelText('album error')).toHaveTextContent('cleanup is pending');
    });
    expect(mocks.deleteImage).toHaveBeenCalledWith(imagePath, {});
    const firstCleanup = mocks.fetch.mock.calls.find(([url]) => String(url) === '/api/albums/members/cleanup');
    expect(JSON.parse(String(firstCleanup?.[1]?.body))).toEqual({ paths: [imagePath] });

    await user.click(screen.getByRole('button', { name: 'Retry cleanup' }));
    await waitFor(() => {
      expect(screen.getByLabelText('member count')).toHaveTextContent('0');
      expect(screen.getByLabelText('album error')).toBeEmptyDOMElement();
    });
    expect(cleanupAttempts).toBe(2);
  });
});

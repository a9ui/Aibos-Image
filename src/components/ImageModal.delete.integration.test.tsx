import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ImageModal from './ImageModal';
import { ImageProvider, useImageStore } from '../store/ImageContext';
import { DEFAULT_KEY_BINDINGS, type ImageFile } from '../lib/types';

vi.mock('../lib/clientImageCache', async (importOriginal) => ({
  ...await importOriginal<typeof import('../lib/clientImageCache')>(),
  loadCachedImageUrl: vi.fn(async () => '/cached-image'),
}));

const images: ImageFile[] = ['first', 'second', 'third'].map((name, index) => ({
  id: `C:/images/${name}.png`,
  filename: `${name}.png`,
  absolutePath: `C:/images/${name}.png`,
  fileUrl: `/api/image?${name}`,
  displayUrl: `/api/image?${name}&display=1`,
  fullUrl: `/api/image?${name}&full=1`,
  metadata: null,
  createdAt: index + 1,
  mtime: index + 1,
}));

function ModalDeleteIntegrationProbe() {
  const {
    phase,
    setPhase,
    setDirPath,
    searchResults,
    selectedIndex,
    openModalAtImage,
  } = useImageStore();
  const currentId = selectedIndex === null ? '' : searchResults[selectedIndex]?.id ?? '';
  const [transitions, setTransitions] = React.useState<string[]>([]);
  const previousCurrentIdRef = React.useRef('');

  React.useEffect(() => {
    if (!currentId || previousCurrentIdRef.current === currentId) return;
    previousCurrentIdRef.current = currentId;
    setTransitions((current) => [...current, currentId]);
  }, [currentId]);

  const loaded = searchResults.filter((image): image is ImageFile => Boolean(image));
  return (
    <div>
      <output aria-label="integration phase">{phase}</output>
      <output aria-label="integration results">{loaded.map((image) => image.id).join(',')}</output>
      <output aria-label="integration current">{currentId}</output>
      <output aria-label="integration transitions">{transitions.join(',')}</output>
      <button type="button" onClick={() => {
        setDirPath('C:/images');
        setPhase('viewer');
      }}>Load three-image fixture</button>
      <button type="button" disabled={loaded.length !== 3} onClick={() => {
        openModalAtImage(images[1].id, 1);
      }}>Open middle image</button>
    </div>
  );
}

describe('ImageModal delete neighbor integration', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.clearAllMocks();
  });

  it('stays on the original right neighbor after the delayed search refresh', async () => {
    let deleted = false;
    let lateSearchCount = 0;
    let resolveLateSearch!: (response: Response) => void;
    const lateSearch = new Promise<Response>((resolve) => {
      resolveLateSearch = resolve;
    });
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/api/search')) {
        if (deleted) {
          lateSearchCount += 1;
          return lateSearch;
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({ results: images, total: images.length, page: 0, totalPages: 1 }),
        } as Response;
      }
      if (url.includes('/api/delete')) {
        deleted = true;
        return { ok: true, status: 200, json: async () => ({ ok: true }) } as Response;
      }
      if (url.includes('/api/settings')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            keyBindings: DEFAULT_KEY_BINDINGS,
            confirmBeforeDelete: false,
          }),
        } as Response;
      }
      return {
        ok: true,
        status: 200,
        json: async () => url.includes('/api/favorites')
          ? { favorites: {} }
          : url.includes('/api/seen')
            ? { seen: {} }
            : url.includes('/api/enhance/jobs')
              ? { jobs: [] }
              : {},
      } as Response;
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <ImageProvider>
        <ModalDeleteIntegrationProbe />
        <ImageModal />
      </ImageProvider>
    );

    await user.click(screen.getByRole('button', { name: 'Load three-image fixture' }));
    await waitFor(() => expect(screen.getByRole('status', { name: 'integration results' }))
      .toHaveTextContent(images.map((image) => image.id).join(',')));
    await user.click(screen.getByRole('button', { name: 'Open middle image' }));
    await waitFor(() => expect(screen.getByRole('status', { name: 'integration current' }))
      .toHaveTextContent(images[1].id));

    fireEvent.keyDown(window, { key: DEFAULT_KEY_BINDINGS.deleteImage });

    await waitFor(() => expect(screen.getByRole('status', { name: 'integration current' }))
      .toHaveTextContent(images[2].id));
    await waitFor(() => expect(screen.getByRole('status', { name: 'integration transitions' }))
      .toHaveTextContent(`${images[1].id},${images[2].id}`));
    expect(fetchMock.mock.calls.filter(([input]) => String(input).includes('/api/delete'))).toHaveLength(1);
    expect(lateSearchCount).toBe(1);

    resolveLateSearch({
      ok: true,
      status: 200,
      json: async () => ({ results: [images[0], images[2]], total: 2, page: 0, totalPages: 1 }),
    } as Response);

    await waitFor(() => expect(screen.getByRole('status', { name: 'integration results' }))
      .toHaveTextContent(`${images[0].id},${images[2].id}`));
    expect(screen.getByRole('status', { name: 'integration current' })).toHaveTextContent(images[2].id);
    await waitFor(() => expect(screen.getByRole('status', { name: 'integration transitions' }))
      .toHaveTextContent(`${images[1].id},${images[2].id}`));
    expect(fetchMock.mock.calls.filter(([input]) => String(input).includes('/api/delete'))).toHaveLength(1);
  });
});

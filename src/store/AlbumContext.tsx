'use client';

import React, {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';

import type { AlbumRecord, AlbumsDocument } from '../lib/albums';
import type { AlbumSourceSnapshot } from '../lib/albumSourceTypes';
import type { DeleteImageOptions } from './ImageContext';
import { useImageStore } from './ImageContext';

interface MutationResponse {
  ok: boolean;
  document?: AlbumsDocument | null;
  album?: AlbumRecord;
  conflict?: boolean;
  error?: string;
}

interface AlbumContextValue {
  document: AlbumsDocument | null;
  albums: AlbumRecord[];
  activeSource: AlbumSourceSnapshot | null;
  loading: boolean;
  error: string;
  libraryOpen: boolean;
  pickerOpen: boolean;
  pickerTargetPaths: string[];
  setLibraryOpen: (open: boolean) => void;
  setPickerOpen: (open: boolean) => void;
  openPicker: (paths?: readonly string[]) => void;
  refreshAlbums: () => Promise<AlbumsDocument | null>;
  refreshActiveSource: () => Promise<AlbumSourceSnapshot | null>;
  openAlbum: (albumId: string) => Promise<boolean>;
  closeAlbum: () => void;
  createAlbum: (name: string) => Promise<AlbumRecord | null>;
  updateAlbum: (albumId: string, patch: { name?: string; pinned?: boolean; coverMemberId?: string | null }) => Promise<boolean>;
  deleteAlbum: (albumId: string) => Promise<boolean>;
  addMembers: (albumId: string, paths: readonly string[]) => Promise<boolean>;
  removeMembers: (albumId: string, options: { memberIds?: readonly string[]; paths?: readonly string[] }) => Promise<boolean>;
  cleanupPaths: (paths: readonly string[]) => Promise<boolean>;
  recycleSource: (imagePath: string, options?: DeleteImageOptions) => Promise<boolean>;
}

const AlbumContext = createContext<AlbumContextValue | undefined>(undefined);

function messageFromPayload(payload: unknown, fallback: string) {
  if (payload && typeof payload === 'object' && typeof (payload as { error?: unknown }).error === 'string') {
    return (payload as { error: string }).error;
  }
  return fallback;
}

export function AlbumProvider({ children }: { children: ReactNode }) {
  const {
    indexToken,
    selectedIds,
    clearSelection,
    setSelectedIndex,
    selectedIndex,
    setModalImageIds,
    deleteImage,
    favorites,
    setPhase,
  } = useImageStore();
  const [document, setDocument] = useState<AlbumsDocument | null>(null);
  const documentRef = useRef<AlbumsDocument | null>(null);
  const [activeSource, setActiveSource] = useState<AlbumSourceSnapshot | null>(null);
  const activeSourceRef = useRef<AlbumSourceSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerTargetPaths, setPickerTargetPaths] = useState<string[]>([]);

  const commitDocument = useCallback((next: AlbumsDocument | null) => {
    documentRef.current = next;
    setDocument(next);
  }, []);

  const commitSource = useCallback((next: AlbumSourceSnapshot | null) => {
    activeSourceRef.current = next;
    setActiveSource(next);
  }, []);

  const refreshAlbums = useCallback(async () => {
    try {
      const response = await fetch('/api/albums', { cache: 'no-store' });
      const payload = await response.json() as { ok?: boolean; document?: AlbumsDocument; error?: string };
      if (!response.ok || !payload.ok || !payload.document) {
        setError(messageFromPayload(payload, 'Could not read the shared Album library.'));
        return null;
      }
      commitDocument(payload.document);
      setError('');
      return payload.document;
    } catch {
      setError('Could not reach the shared Album library.');
      return null;
    }
  }, [commitDocument]);

  const changeLibraryOpen = useCallback((open: boolean) => {
    setLibraryOpen(open);
    if (!open) return;
    setLoading(true);
    void refreshAlbums().finally(() => setLoading(false));
  }, [refreshAlbums]);

  const mutate = useCallback(async (
    url: string,
    method: 'POST' | 'PATCH' | 'DELETE',
    body: Record<string, unknown>,
    options: { includeExpectedRevision?: boolean } = {},
  ): Promise<MutationResponse | null> => {
    const expectedRevision = documentRef.current?.revision;
    const includeExpectedRevision = options.includeExpectedRevision !== false;
    try {
      const response = await fetch(url, {
        method,
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          ...body,
          ...(!includeExpectedRevision || expectedRevision === undefined ? {} : { expectedRevision }),
        }),
      });
      const payload = await response.json() as MutationResponse;
      if (!response.ok || !payload.ok || !payload.document) {
        if (response.status === 409 || payload.conflict) await refreshAlbums();
        setError(messageFromPayload(payload, 'The Album operation failed.'));
        return null;
      }
      commitDocument(payload.document);
      setError('');
      return payload;
    } catch {
      setError('Could not reach the shared Album library.');
      return null;
    }
  }, [commitDocument, refreshAlbums]);

  const refreshSourceById = useCallback(async (albumId: string) => {
    try {
      const response = await fetch(`/api/albums/${encodeURIComponent(albumId)}/source`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ ...(indexToken ? { catalogIndexToken: indexToken } : {}) }),
      });
      const payload = await response.json() as { ok?: boolean; source?: AlbumSourceSnapshot; error?: string };
      if (!response.ok || !payload.ok || !payload.source) {
        if (response.status === 404) commitSource(null);
        setError(messageFromPayload(payload, 'Could not build the Album source.'));
        return null;
      }
      commitSource(payload.source);
      setError('');
      return payload.source;
    } catch {
      setError('Could not reach the Album source service.');
      return null;
    }
  }, [commitSource, indexToken]);

  const refreshActiveSource = useCallback(async () => {
    const albumId = activeSourceRef.current?.album.id;
    return albumId ? refreshSourceById(albumId) : null;
  }, [refreshSourceById]);

  useEffect(() => {
    const albumId = activeSourceRef.current?.album.id;
    if (albumId) void refreshSourceById(albumId);
  }, [indexToken, refreshSourceById]);

  const openAlbum = useCallback(async (albumId: string) => {
    // Recent ordering is a commutative latest-state operation. Do not attach a
    // stale UI revision, and do not continue into source refresh after a 404 or
    // other failed mutation because a successful refresh would hide that error.
    const recent = await mutate(
      `/api/albums/${encodeURIComponent(albumId)}/recent`,
      'POST',
      {},
      { includeExpectedRevision: false },
    );
    if (!recent) return false;
    const source = await refreshSourceById(albumId);
    if (!source) return false;
    clearSelection();
    setModalImageIds([]);
    setSelectedIndex(null);
    changeLibraryOpen(false);
    setPhase('viewer');
    return true;
  }, [changeLibraryOpen, clearSelection, mutate, refreshSourceById, setModalImageIds, setPhase, setSelectedIndex]);

  const closeAlbum = useCallback(() => {
    commitSource(null);
    clearSelection();
    setModalImageIds([]);
    setSelectedIndex(null);
  }, [clearSelection, commitSource, setModalImageIds, setSelectedIndex]);

  const createAlbum = useCallback(async (name: string) => {
    const result = await mutate('/api/albums', 'POST', { name });
    return result?.album ?? null;
  }, [mutate]);

  const updateAlbum = useCallback(async (
    albumId: string,
    patch: { name?: string; pinned?: boolean; coverMemberId?: string | null },
  ) => {
    const result = await mutate(`/api/albums/${encodeURIComponent(albumId)}`, 'PATCH', patch);
    if (result && activeSourceRef.current?.album.id === albumId) await refreshSourceById(albumId);
    return Boolean(result);
  }, [mutate, refreshSourceById]);

  const deleteAlbum = useCallback(async (albumId: string) => {
    const result = await mutate(`/api/albums/${encodeURIComponent(albumId)}`, 'DELETE', {});
    if (result && activeSourceRef.current?.album.id === albumId) closeAlbum();
    return Boolean(result);
  }, [closeAlbum, mutate]);

  const addMembers = useCallback(async (albumId: string, paths: readonly string[]) => {
    if (paths.length === 0) return false;
    const result = await mutate(`/api/albums/${encodeURIComponent(albumId)}/members`, 'POST', { paths: [...paths] });
    if (result && activeSourceRef.current?.album.id === albumId) await refreshSourceById(albumId);
    return Boolean(result);
  }, [mutate, refreshSourceById]);

  const removeMembers = useCallback(async (
    albumId: string,
    options: { memberIds?: readonly string[]; paths?: readonly string[] },
  ) => {
    const result = await mutate(`/api/albums/${encodeURIComponent(albumId)}/members`, 'DELETE', {
      ...(options.memberIds?.length ? { memberIds: [...options.memberIds] } : {}),
      ...(options.paths?.length ? { paths: [...options.paths] } : {}),
    });
    if (result && activeSourceRef.current?.album.id === albumId) await refreshSourceById(albumId);
    return Boolean(result);
  }, [mutate, refreshSourceById]);

  const cleanupPaths = useCallback(async (paths: readonly string[]) => {
    const uniquePaths = [...new Set(paths.filter(Boolean))];
    if (uniquePaths.length === 0) return false;
    // Cleanup is a commutative, path-scoped reconciliation operation. Omitting
    // expectedRevision lets the locked store apply it to the latest document
    // instead of turning an unrelated Browser/WPF edit into a false conflict.
    const result = await mutate(
      '/api/albums/members/cleanup',
      'POST',
      { paths: uniquePaths },
      { includeExpectedRevision: false },
    );
    const albumId = activeSourceRef.current?.album.id;
    if (albumId) await refreshSourceById(albumId);
    if (!result) {
      setError('Album cleanup is still pending. Missing membership was preserved; retry Remove missing from Album.');
    }
    return Boolean(result);
  }, [mutate, refreshSourceById]);

  const recycleSource = useCallback(async (imagePath: string, options: DeleteImageOptions = {}) => {
    const source = activeSourceRef.current;
    const member = source?.members.find((candidate) => candidate.imagePath === imagePath);
    if (!source || !member || member.availability === 'missing') return false;
    if ((favorites[imagePath] ?? 0) > 0 && options.favoriteConfirmed !== true) return false;
    const modalWasOpen = selectedIndex !== null;
    const sourceIndex = source.images.findIndex((image) => image.id === imagePath);
    const nextImagePath = source.images[sourceIndex + 1]?.id ?? source.images[sourceIndex - 1]?.id ?? null;

    let recycled = false;
    if (member.availability === 'current') {
      recycled = await deleteImage(imagePath, options);
    } else {
      try {
        const response = await fetch(
          `/api/delete?path=${encodeURIComponent(imagePath)}&indexToken=${encodeURIComponent(source.sourceToken)}`,
          { method: 'DELETE' },
        );
        recycled = response.ok;
        if (!response.ok) {
          setError(response.status === 410
            ? 'The Album source session expired. It has been refreshed; retry the Recycle action.'
            : 'The source image could not be moved to Recycle Bin.');
        }
      } catch {
        setError('Could not reach the Recycle service.');
      }
    }
    if (!recycled) {
      if (member.availability === 'outside') await refreshSourceById(source.album.id);
      return false;
    }

    clearSelection();
    const cleanupSucceeded = await cleanupPaths([imagePath]);
    const refreshed = activeSourceRef.current?.album.id === source.album.id
      ? activeSourceRef.current
      : await refreshSourceById(source.album.id);
    if (!cleanupSucceeded) {
      // refreshSourceById clears an earlier error on success, so publish the
      // recoverable partial-success state only after the source is refreshed.
      setError('The image was recycled, but Album cleanup is pending. The missing member was preserved; use Remove missing from Album to retry.');
    }
    if (modalWasOpen) {
      const nextIndex = nextImagePath && refreshed
        ? refreshed.images.findIndex((image) => image.id === nextImagePath)
        : -1;
      setSelectedIndex(nextIndex >= 0 ? nextIndex : null);
      setModalImageIds(nextIndex >= 0 && refreshed ? refreshed.images.map((image) => image.id) : []);
    }
    return true;
  }, [cleanupPaths, clearSelection, deleteImage, favorites, refreshSourceById, selectedIndex, setModalImageIds, setSelectedIndex]);

  const openPicker = useCallback((paths?: readonly string[]) => {
    const targets = [...new Set((paths ?? selectedIds).filter(Boolean))];
    if (targets.length === 0) return;
    setPickerTargetPaths(targets);
    setPickerOpen(true);
    setLoading(true);
    void refreshAlbums().finally(() => setLoading(false));
  }, [refreshAlbums, selectedIds]);

  const value = useMemo<AlbumContextValue>(() => ({
    document,
    albums: document?.albums ?? [],
    activeSource,
    loading,
    error,
    libraryOpen,
    pickerOpen,
    pickerTargetPaths,
    setLibraryOpen: changeLibraryOpen,
    setPickerOpen,
    openPicker,
    refreshAlbums,
    refreshActiveSource,
    openAlbum,
    closeAlbum,
    createAlbum,
    updateAlbum,
    deleteAlbum,
    addMembers,
    removeMembers,
    cleanupPaths,
    recycleSource,
  }), [
    activeSource, addMembers, changeLibraryOpen, closeAlbum, createAlbum, deleteAlbum, document, error, libraryOpen,
    loading, openAlbum, openPicker, pickerOpen, pickerTargetPaths, recycleSource, refreshActiveSource,
    cleanupPaths, refreshAlbums, removeMembers, updateAlbum,
  ]);

  return <AlbumContext.Provider value={value}>{children}</AlbumContext.Provider>;
}

export function useAlbumStore() {
  const context = useContext(AlbumContext);
  if (!context) throw new Error('useAlbumStore must be inside AlbumProvider');
  return context;
}

const inactiveAlbumContext: Pick<AlbumContextValue,
  'activeSource' | 'refreshActiveSource' | 'recycleSource' | 'openPicker' | 'document' | 'albums'
  | 'setLibraryOpen' | 'openAlbum' | 'closeAlbum'> = {
  activeSource: null,
  document: null,
  albums: [],
  refreshActiveSource: async () => null,
  recycleSource: async () => false,
  openPicker: () => {},
  setLibraryOpen: () => {},
  openAlbum: async () => false,
  closeAlbum: () => {},
};

export function useOptionalAlbumStore() {
  const context = useContext(AlbumContext);
  return context ?? inactiveAlbumContext;
}

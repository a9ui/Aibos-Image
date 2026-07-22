import { randomUUID } from 'crypto';
import { promises as fs } from 'fs';
import path from 'path';

import { withRecoverableDirectoryWriteLock } from './fileWriteLock';
import { isSupportedImagePath } from './imageFormats';

export const ALBUMS_VERSION = 1;
export const MAX_ALBUM_NAME_LENGTH = 120;
export const MAX_ALBUM_MUTATION_PATHS = 10_000;
export const MAX_ALBUM_PATH_LENGTH = 32_768;

export interface AlbumMember extends Record<string, unknown> {
  id: string;
  imagePath: string;
  addedAtUtc: string;
}

export interface AlbumRecord extends Record<string, unknown> {
  id: string;
  name: string;
  pinned: boolean;
  coverMemberId: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  revision: number;
  members: AlbumMember[];
}

export interface AlbumsDocument extends Record<string, unknown> {
  version: typeof ALBUMS_VERSION;
  revision: number;
  updatedAtUtc: string;
  albums: AlbumRecord[];
  recentAlbumIds: string[];
}

export type AlbumsReadResult =
  | { ok: true; document: AlbumsDocument; exists: boolean; malformed: false; futureVersion: false }
  | { ok: false; document: null; exists: true; malformed: boolean; futureVersion: boolean; error: string };

interface MutationBase {
  expectedRevision?: number;
}

export type AlbumMutation =
  | (MutationBase & { action: 'create'; name: string; albumId?: string })
  | (MutationBase & {
    action: 'update';
    albumId: string;
    name?: string;
    pinned?: boolean;
    coverMemberId?: string | null;
  })
  | (MutationBase & { action: 'delete'; albumId: string })
  | (MutationBase & { action: 'add'; albumId: string; paths: string[] })
  | (MutationBase & { action: 'remove'; albumId: string; memberIds?: string[]; paths?: string[] })
  | (MutationBase & { action: 'recent'; albumId: string })
  | (MutationBase & { action: 'cleanupPaths'; paths: string[] });

export interface AlbumMutationResult {
  ok: boolean;
  document: AlbumsDocument | null;
  album?: AlbumRecord;
  changed: boolean;
  conflict: boolean;
  notFound: boolean;
  malformed: boolean;
  futureVersion: boolean;
  error?: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function isRevision(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) >= 0;
}

function isBoundedToken(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 256;
}

function isUtcStamp(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && Number.isFinite(Date.parse(value));
}

export function normalizeAlbumName(value: string) {
  return value.trim();
}

export function canonicalAlbumPath(imagePath: string) {
  const resolved = path.resolve(imagePath);
  return process.platform === 'win32' ? resolved.toLocaleLowerCase('en-US') : resolved;
}

function emptyDocument(): AlbumsDocument {
  return {
    version: ALBUMS_VERSION,
    revision: 0,
    updatedAtUtc: new Date(0).toISOString(),
    albums: [],
    recentAlbumIds: [],
  };
}

function normalizeLegacyEmptyDocument(parsed: Record<string, unknown>) {
  if (Object.hasOwn(parsed, 'version')) return parsed;
  const hasVersionOneOnlyFields = Object.hasOwn(parsed, 'revision')
    || Object.hasOwn(parsed, 'updatedAtUtc')
    || Object.hasOwn(parsed, 'recentAlbumIds');
  if (hasVersionOneOnlyFields || !Array.isArray(parsed.albums) || parsed.albums.length !== 0) return null;
  return {
    ...parsed,
    version: ALBUMS_VERSION,
    revision: 0,
    updatedAtUtc: new Date(0).toISOString(),
    recentAlbumIds: [],
  };
}

function validateDocument(parsed: unknown): { ok: true; document: AlbumsDocument } | { ok: false; futureVersion: boolean; error: string } {
  if (!isObject(parsed)) return { ok: false, futureVersion: false, error: 'albums.json root must be an object.' };
  const candidate = normalizeLegacyEmptyDocument(parsed);
  if (!candidate) {
    return { ok: false, futureVersion: false, error: 'albums.json legacy state is not an unambiguous empty document.' };
  }
  if (typeof candidate.version === 'number' && Number.isInteger(candidate.version) && candidate.version > ALBUMS_VERSION) {
    return { ok: false, futureVersion: true, error: `albums.json version ${candidate.version} is newer than supported version ${ALBUMS_VERSION}.` };
  }
  if (candidate.version !== ALBUMS_VERSION
    || !isRevision(candidate.revision)
    || !isUtcStamp(candidate.updatedAtUtc)
    || !Array.isArray(candidate.albums)
    || !Array.isArray(candidate.recentAlbumIds)
    || !candidate.recentAlbumIds.every(isBoundedToken)) {
    return { ok: false, futureVersion: false, error: 'albums.json does not match the supported version 1 root schema.' };
  }

  const albumIds = new Set<string>();
  const memberIds = new Set<string>();
  const albums: AlbumRecord[] = [];
  for (const value of candidate.albums) {
    if (!isObject(value)
      || !isBoundedToken(value.id)
      || typeof value.name !== 'string'
      || normalizeAlbumName(value.name).length === 0
      || normalizeAlbumName(value.name).length > MAX_ALBUM_NAME_LENGTH
      || typeof value.pinned !== 'boolean'
      || !(value.coverMemberId === null || isBoundedToken(value.coverMemberId))
      || !isUtcStamp(value.createdAtUtc)
      || !isUtcStamp(value.updatedAtUtc)
      || !isRevision(value.revision)
      || !Array.isArray(value.members)
      || albumIds.has(value.id)) {
      return { ok: false, futureVersion: false, error: 'albums.json contains an invalid or duplicate Album.' };
    }
    albumIds.add(value.id);
    const canonicalPaths = new Set<string>();
    const members: AlbumMember[] = [];
    for (const memberValue of value.members) {
      if (!isObject(memberValue)
        || !isBoundedToken(memberValue.id)
        || typeof memberValue.imagePath !== 'string'
        || memberValue.imagePath.length === 0
        || memberValue.imagePath.length > MAX_ALBUM_PATH_LENGTH
        || !path.isAbsolute(memberValue.imagePath)
        || !isSupportedImagePath(memberValue.imagePath)
        || !isUtcStamp(memberValue.addedAtUtc)
        || memberIds.has(memberValue.id)) {
        return { ok: false, futureVersion: false, error: 'albums.json contains an invalid or duplicate member.' };
      }
      const identity = canonicalAlbumPath(memberValue.imagePath);
      if (canonicalPaths.has(identity)) {
        return { ok: false, futureVersion: false, error: 'albums.json contains duplicate member paths.' };
      }
      canonicalPaths.add(identity);
      memberIds.add(memberValue.id);
      members.push({ ...memberValue, id: memberValue.id, imagePath: path.resolve(memberValue.imagePath), addedAtUtc: memberValue.addedAtUtc });
    }
    if (value.coverMemberId !== null && !members.some((member) => member.id === value.coverMemberId)) {
      return { ok: false, futureVersion: false, error: 'albums.json contains a cover that is not an Album member.' };
    }
    albums.push({
      ...value,
      id: value.id,
      name: normalizeAlbumName(value.name),
      pinned: value.pinned,
      coverMemberId: value.coverMemberId,
      createdAtUtc: value.createdAtUtc,
      updatedAtUtc: value.updatedAtUtc,
      revision: value.revision,
      members,
    });
  }
  if (new Set(candidate.recentAlbumIds).size !== candidate.recentAlbumIds.length
    || candidate.recentAlbumIds.some((id) => !albumIds.has(id))) {
    return { ok: false, futureVersion: false, error: 'albums.json contains invalid recent Album ids.' };
  }
  return {
    ok: true,
    document: {
      ...candidate,
      version: ALBUMS_VERSION,
      revision: candidate.revision,
      updatedAtUtc: candidate.updatedAtUtc,
      albums,
      recentAlbumIds: [...candidate.recentAlbumIds],
    },
  };
}

export async function readAlbums(target: string): Promise<AlbumsReadResult> {
  let raw: string;
  try {
    raw = await fs.readFile(target, 'utf8');
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code === 'ENOENT') {
      return { ok: true, document: emptyDocument(), exists: false, malformed: false, futureVersion: false };
    }
    return { ok: false, document: null, exists: true, malformed: true, futureVersion: false, error: error instanceof Error ? error.message : String(error) };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw) as unknown;
  } catch (error) {
    return { ok: false, document: null, exists: true, malformed: true, futureVersion: false, error: error instanceof Error ? error.message : String(error) };
  }
  const validated = validateDocument(parsed);
  return validated.ok
    ? { ok: true, document: validated.document, exists: true, malformed: false, futureVersion: false }
    : { ok: false, document: null, exists: true, malformed: !validated.futureVersion, futureVersion: validated.futureVersion, error: validated.error };
}

function touchRecent(current: readonly string[], albumId: string) {
  return [albumId, ...current.filter((id) => id !== albumId)].slice(0, 30);
}

function validateMutation(mutation: AlbumMutation) {
  if (mutation.expectedRevision !== undefined && !isRevision(mutation.expectedRevision)) return 'expectedRevision must be a non-negative safe integer.';
  if ('albumId' in mutation && !isBoundedToken(mutation.albumId)) return 'albumId must be a non-empty bounded string.';
  if (mutation.action === 'create' || (mutation.action === 'update' && mutation.name !== undefined)) {
    if (typeof mutation.name !== 'string') return `name must contain 1-${MAX_ALBUM_NAME_LENGTH} characters.`;
    const name = normalizeAlbumName(mutation.name);
    if (!name || name.length > MAX_ALBUM_NAME_LENGTH) return `name must contain 1-${MAX_ALBUM_NAME_LENGTH} characters.`;
  }
  if (mutation.action === 'update' && mutation.pinned !== undefined && typeof mutation.pinned !== 'boolean') return 'pinned must be a boolean.';
  if (mutation.action === 'add' || mutation.action === 'cleanupPaths') {
    if (!Array.isArray(mutation.paths) || mutation.paths.length === 0 || mutation.paths.length > MAX_ALBUM_MUTATION_PATHS) return 'paths must be a non-empty bounded array.';
  }
  if (mutation.action === 'remove'
    && ((mutation.memberIds !== undefined && !Array.isArray(mutation.memberIds))
      || (mutation.paths !== undefined && !Array.isArray(mutation.paths)))) return 'memberIds and paths must be arrays.';
  if (mutation.action === 'remove' && (!mutation.memberIds?.length && !mutation.paths?.length)) return 'remove must include memberIds or paths.';
  if (mutation.action === 'remove'
    && ((mutation.memberIds?.length ?? 0) > MAX_ALBUM_MUTATION_PATHS
      || (mutation.paths?.length ?? 0) > MAX_ALBUM_MUTATION_PATHS)) return 'remove memberIds and paths must be bounded arrays.';
  const paths = 'paths' in mutation && Array.isArray(mutation.paths) ? mutation.paths : [];
  if (paths.some((value) => typeof value !== 'string' || !value || value.length > MAX_ALBUM_PATH_LENGTH || !path.isAbsolute(value) || !isSupportedImagePath(value))) return 'paths must contain bounded absolute supported image paths.';
  if (mutation.action === 'remove' && mutation.memberIds && mutation.memberIds.some((id) => !isBoundedToken(id))) return 'memberIds must contain bounded ids.';
  if (mutation.action === 'update' && mutation.coverMemberId !== undefined && mutation.coverMemberId !== null && !isBoundedToken(mutation.coverMemberId)) return 'coverMemberId must be null or a bounded id.';
  return null;
}

function applyMutation(document: AlbumsDocument, mutation: AlbumMutation, now: string): { changed: boolean; document: AlbumsDocument; album?: AlbumRecord; notFound: boolean; error?: string } {
  const albums = [...document.albums];
  if (mutation.action === 'create') {
    const id = mutation.albumId ?? randomUUID();
    if (!isBoundedToken(id) || albums.some((album) => album.id === id)) return { changed: false, document, notFound: false, error: 'Album id already exists or is invalid.' };
    const album: AlbumRecord = { id, name: normalizeAlbumName(mutation.name), pinned: false, coverMemberId: null, createdAtUtc: now, updatedAtUtc: now, revision: 1, members: [] };
    return { changed: true, document: { ...document, albums: [...albums, album], recentAlbumIds: touchRecent(document.recentAlbumIds, id) }, album, notFound: false };
  }
  if (mutation.action === 'cleanupPaths') {
    const identities = new Set(mutation.paths.map(canonicalAlbumPath));
    let changed = false;
    const cleaned = albums.map((album) => {
      const members = album.members.filter((member) => !identities.has(canonicalAlbumPath(member.imagePath)));
      if (members.length === album.members.length) return album;
      changed = true;
      return { ...album, members, coverMemberId: members.some((member) => member.id === album.coverMemberId) ? album.coverMemberId : null, updatedAtUtc: now, revision: album.revision + 1 };
    });
    return { changed, document: changed ? { ...document, albums: cleaned } : document, notFound: false };
  }

  const index = albums.findIndex((album) => album.id === mutation.albumId);
  if (index < 0) return { changed: false, document, notFound: true };
  const current = albums[index];
  if (mutation.action === 'delete') {
    return { changed: true, document: { ...document, albums: albums.filter((_, albumIndex) => albumIndex !== index), recentAlbumIds: document.recentAlbumIds.filter((id) => id !== current.id) }, notFound: false };
  }
  if (mutation.action === 'recent') {
    const recentAlbumIds = touchRecent(document.recentAlbumIds, current.id);
    const changed = recentAlbumIds.some((id, recentIndex) => id !== document.recentAlbumIds[recentIndex]) || recentAlbumIds.length !== document.recentAlbumIds.length;
    return { changed, document: changed ? { ...document, recentAlbumIds } : document, album: current, notFound: false };
  }

  let next = current;
  if (mutation.action === 'update') {
    const name = mutation.name === undefined ? current.name : normalizeAlbumName(mutation.name);
    const pinned = mutation.pinned ?? current.pinned;
    const coverMemberId = mutation.coverMemberId === undefined ? current.coverMemberId : mutation.coverMemberId;
    if (coverMemberId !== null && !current.members.some((member) => member.id === coverMemberId)) return { changed: false, document, album: current, notFound: false, error: 'Cover must reference a member of the Album.' };
    if (name !== current.name || pinned !== current.pinned || coverMemberId !== current.coverMemberId) next = { ...current, name, pinned, coverMemberId };
  } else if (mutation.action === 'add') {
    const identities = new Set(current.members.map((member) => canonicalAlbumPath(member.imagePath)));
    const members = [...current.members];
    for (const imagePath of mutation.paths) {
      const absolutePath = path.resolve(imagePath);
      const identity = canonicalAlbumPath(absolutePath);
      if (identities.has(identity)) continue;
      identities.add(identity);
      members.push({ id: randomUUID(), imagePath: absolutePath, addedAtUtc: now });
    }
    if (members.length !== current.members.length) next = { ...current, members };
  } else if (mutation.action === 'remove') {
    const ids = new Set(mutation.memberIds ?? []);
    const identities = new Set((mutation.paths ?? []).map(canonicalAlbumPath));
    const members = current.members.filter((member) => !ids.has(member.id) && !identities.has(canonicalAlbumPath(member.imagePath)));
    if (members.length !== current.members.length) next = { ...current, members, coverMemberId: members.some((member) => member.id === current.coverMemberId) ? current.coverMemberId : null };
  }
  const changed = next !== current;
  if (changed) next = { ...next, updatedAtUtc: now, revision: current.revision + 1 };
  albums[index] = next;
  return { changed, document: changed ? { ...document, albums } : document, album: next, notFound: false };
}

async function publishAlbums(target: string, document: AlbumsDocument) {
  const directory = path.dirname(target);
  const temp = path.join(directory, `albums-${process.pid}-${Date.now()}-${randomUUID()}.tmp`);
  await fs.mkdir(directory, { recursive: true });
  try {
    const handle = await fs.open(temp, 'wx');
    try {
      await handle.writeFile(`${JSON.stringify(document, null, 2)}\n`, 'utf8');
      await handle.sync();
    } finally {
      await handle.close();
    }
    for (let attempt = 0; ; attempt += 1) {
      try {
        await fs.rename(temp, target);
        break;
      } catch (error) {
        const code = (error as NodeJS.ErrnoException)?.code;
        if (attempt >= 4 || (code !== 'EBUSY' && code !== 'EPERM' && code !== 'EACCES')) throw error;
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
    }
  } finally {
    await fs.unlink(temp).catch(() => {});
  }
}

export async function mutateAlbums(target: string, mutation: AlbumMutation): Promise<AlbumMutationResult> {
  const validationError = validateMutation(mutation);
  if (validationError) return { ok: false, document: null, changed: false, conflict: false, notFound: false, malformed: false, futureVersion: false, error: validationError };
  return withRecoverableDirectoryWriteLock(target, async () => {
    const current = await readAlbums(target);
    if (!current.ok) return { ok: false, document: null, changed: false, conflict: false, notFound: false, malformed: current.malformed, futureVersion: current.futureVersion, error: 'Shared Album state is malformed or from a newer version; refusing to overwrite it.' };
    if (mutation.expectedRevision !== undefined && mutation.expectedRevision !== current.document.revision) {
      return { ok: false, document: current.document, changed: false, conflict: true, notFound: false, malformed: false, futureVersion: false, error: `Album revision conflict: expected ${mutation.expectedRevision}, current ${current.document.revision}.` };
    }
    const now = new Date().toISOString();
    const applied = applyMutation(current.document, mutation, now);
    if (applied.error) return { ok: false, document: current.document, album: applied.album, changed: false, conflict: false, notFound: false, malformed: false, futureVersion: false, error: applied.error };
    if (applied.notFound) return { ok: false, document: current.document, changed: false, conflict: false, notFound: true, malformed: false, futureVersion: false, error: 'Album not found.' };
    const document = applied.changed ? { ...applied.document, revision: current.document.revision + 1, updatedAtUtc: now } : current.document;
    if (applied.changed) await publishAlbums(target, document);
    const album = applied.album ? document.albums.find((candidate) => candidate.id === applied.album!.id) : undefined;
    return { ok: true, document, album, changed: applied.changed, conflict: false, notFound: false, malformed: false, futureVersion: false };
  });
}

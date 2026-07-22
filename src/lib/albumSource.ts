import { promises as fs } from 'fs';
import type { Stats } from 'fs';
import path from 'path';

import type { AlbumsDocument } from './albums';
import { canonicalAlbumPath } from './albums';
import type { AlbumSourceMember, AlbumSourceSnapshot } from './albumSourceTypes';
import { isSupportedImagePath } from './imageFormats';
import { createIndexSession, getIndex, hasIndexSession } from './indexer';
import type { ImageFile } from './types';

const STAT_CONCURRENCY = 32;

function withIndexToken(url: string, indexToken: string) {
  if (/[?&]indexToken=/.test(url)) return url;
  return `${url}${url.includes('?') ? '&' : '?'}indexToken=${encodeURIComponent(indexToken)}`;
}

function makeImage(imagePath: string, stat: Stats, current?: ImageFile): ImageFile {
  const absolutePath = path.resolve(imagePath);
  const encodedPath = encodeURIComponent(absolutePath);
  const version = String(stat.mtimeMs);
  return {
    id: absolutePath,
    filename: path.basename(absolutePath),
    absolutePath,
    fileUrl: `/api/image?path=${encodedPath}&thumb=true&v=${encodeURIComponent(version)}`,
    displayUrl: `/api/image?path=${encodedPath}&display=true&v=${encodeURIComponent(version)}`,
    fullUrl: `/api/image?path=${encodedPath}&v=${encodeURIComponent(version)}`,
    metadata: current?.metadata ?? null,
    createdAt: current?.createdAt ?? (stat.birthtimeMs || stat.mtimeMs),
    mtime: stat.mtimeMs,
  };
}

export async function buildAlbumSource(
  document: AlbumsDocument,
  albumId: string,
  catalogIndexToken?: string,
): Promise<AlbumSourceSnapshot | null> {
  const album = document.albums.find((candidate) => candidate.id === albumId);
  if (!album) return null;
  const targetAlbum = album;

  const catalogExpired = Boolean(catalogIndexToken && !hasIndexSession(catalogIndexToken));
  const currentImages = !catalogIndexToken || catalogExpired ? [] : getIndex(catalogIndexToken);
  const currentByIdentity = new Map(
    currentImages.map((image) => [canonicalAlbumPath(image.id), image]),
  );
  const members = Array<AlbumSourceMember>(targetAlbum.members.length);
  let cursor = 0;

  async function worker() {
    while (true) {
      const index = cursor;
      cursor += 1;
      if (index >= targetAlbum.members.length) return;
      const member = targetAlbum.members[index];
      if (!member) return;
      const current = currentByIdentity.get(canonicalAlbumPath(member.imagePath));
      const stat = await fs.stat(member.imagePath).catch(() => null);
      if (!stat?.isFile()) {
        members[index] = {
          memberId: member.id,
          imagePath: member.imagePath,
          availability: 'missing',
          image: null,
          reason: 'Source file is missing.',
        };
        continue;
      }
      if (!isSupportedImagePath(member.imagePath)) {
        members[index] = {
          memberId: member.id,
          imagePath: member.imagePath,
          availability: 'missing',
          image: null,
          reason: 'Source type is no longer supported.',
        };
        continue;
      }
      members[index] = {
        memberId: member.id,
        imagePath: member.imagePath,
        availability: current ? 'current' : 'outside',
        image: makeImage(member.imagePath, stat, current),
      };
    }
  }

  await Promise.all(Array.from(
    { length: Math.min(STAT_CONCURRENCY, Math.max(1, targetAlbum.members.length)) },
    () => worker(),
  ));
  const availableImages = members.flatMap((member) => member.image ? [member.image] : []);
  const sourceKey = JSON.stringify({
    kind: 'album-v1',
    albumId: targetAlbum.id,
    albumRevision: targetAlbum.revision,
    members: targetAlbum.members.map((member) => [member.id, canonicalAlbumPath(member.imagePath)]),
  });
  const sourceToken = createIndexSession(availableImages, sourceKey);
  const tokenizedMembers = members.map((member) => member.image ? {
    ...member,
    image: {
      ...member.image,
      fileUrl: withIndexToken(member.image.fileUrl, sourceToken),
      displayUrl: withIndexToken(member.image.displayUrl, sourceToken),
      fullUrl: withIndexToken(member.image.fullUrl, sourceToken),
    },
  } : member);
  return {
    album: targetAlbum,
    documentRevision: document.revision,
    sourceToken,
    catalogExpired,
    members: tokenizedMembers,
    images: tokenizedMembers.flatMap((member) => member.image ? [member.image] : []),
  };
}

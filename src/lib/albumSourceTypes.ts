import type { AlbumRecord } from './albums';
import type { ImageFile } from './types';

export type AlbumMemberAvailability = 'current' | 'outside' | 'missing';

export interface AlbumSourceMember {
  memberId: string;
  imagePath: string;
  availability: AlbumMemberAvailability;
  image: ImageFile | null;
  reason?: string;
}

export interface AlbumSourceSnapshot {
  album: AlbumRecord;
  documentRevision: number;
  sourceToken: string;
  catalogExpired: boolean;
  members: AlbumSourceMember[];
  images: ImageFile[];
}

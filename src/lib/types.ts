// Metadata extracted from PNG tEXt chunk.
export interface SDMetadata {
  raw?: string;
  prompt: string;
  negativePrompt: string;
  settings: Record<string, string>;
}

// Single image file representation.
export interface ImageFile {
  id: string;              // normalised absolute path
  filename: string;
  absolutePath: string;
  fileUrl: string;         // /api/image?path=...&thumb=true
  displayUrl: string;      // /api/image?path=...&display=true
  fullUrl: string;         // /api/image?path=...
  metadata: SDMetadata | null;
  createdAt: number;       // file creation timestamp (ms)
  mtime: number;           // file modification timestamp (ms)
  isFavorite?: boolean;    // injected by frontend
}

// Cache schema persisted as JSON.
export interface CacheEntry {
  mtime: number;
  size?: number;
  createdAt?: number;
  metadata: SDMetadata | null;
}

export interface CacheData {
  version: number;
  dirPath: string;
  files: Record<string, CacheEntry>; // key = absolute path
  lastScan: string;                  // ISO timestamp
}

// Search API response.
export interface SearchResponse {
  results: ImageFile[];
  total: number;
  page: number;
  totalPages: number;
}

// Scan SSE event payloads.
export interface ScanProgress {
  type: 'progress' | 'complete' | 'error';
  processed: number;
  total: number;
  newFiles: number;
  stage?: 'preparing' | 'scanning' | 'complete';
  message?: string;
}

// Key bindings.
export interface KeyBindings {
  nextImage: string;
  prevImage: string;
  toggleFavorite: string;
  decreaseFavorite: string;
  deleteImage: string;
  closeModal: string;
  flipHorizontal: string;
  enhanceImage: string;
  toggleFilmstrip: string;
  addToAlbum: string;
  zoomIn: string;
  zoomOut: string;
  zoomReset: string;
}

export const DEFAULT_KEY_BINDINGS: KeyBindings = {
  nextImage: 'ArrowRight',
  prevImage: 'ArrowLeft',
  toggleFavorite: 'f',
  decreaseFavorite: 'u',
  deleteImage: 'Delete',
  closeModal: 'Escape',
  flipHorizontal: 'h',
  enhanceImage: 'a',
  toggleFilmstrip: 't',
  addToAlbum: 'b',
  zoomIn: '=',
  zoomOut: '-',
  zoomReset: '0',
};

export interface ThumbnailStatusBorderPreference {
  enabled: boolean;
  color: string;
}

export const THUMBNAIL_STATUS_BORDER_RAINBOW = 'rainbow' as const;

export interface ThumbnailStatusBorderSettings {
  favorite: ThumbnailStatusBorderPreference;
  enhanced: ThumbnailStatusBorderPreference;
}

export type ThumbnailStatusBorderSettingsPatch = Partial<ThumbnailStatusBorderSettings>;

export const DEFAULT_THUMBNAIL_STATUS_BORDERS: ThumbnailStatusBorderSettings = {
  favorite: { enabled: true, color: '#facc15' },
  enhanced: { enabled: true, color: '#38bdf8' },
};

// App settings.
export interface AppSettings {
  keyBindings: KeyBindings;
  confirmBeforeDelete?: boolean;
  thumbnailStatusBorders?: ThumbnailStatusBorderSettings;
}

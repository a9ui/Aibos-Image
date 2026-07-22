import path from 'path';

export const SUPPORTED_IMAGE_EXTENSIONS = ['png', 'jpg', 'jpeg', 'webp', 'avif', 'gif'] as const;
export const IMAGE_GLOB_EXTENSIONS = `{${SUPPORTED_IMAGE_EXTENSIONS.join(',')}}`;

const CONTENT_TYPES: Record<string, string> = {
  '.avif': 'image/avif',
  '.gif': 'image/gif',
  '.jpeg': 'image/jpeg',
  '.jpg': 'image/jpeg',
  '.png': 'image/png',
  '.webp': 'image/webp',
};

export function isSupportedImagePath(filePath: string) {
  return path.extname(filePath).toLowerCase() in CONTENT_TYPES;
}

export function getImageContentType(filePath: string) {
  return CONTENT_TYPES[path.extname(filePath).toLowerCase()] ?? 'application/octet-stream';
}

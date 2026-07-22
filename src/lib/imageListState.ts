import type { ImageFile } from './types';

export function buildImageIndexById(images: Array<ImageFile | null>) {
  const indexById = new Map<string, number>();
  for (let index = 0; index < images.length; index += 1) {
    const image = images[index];
    if (image) indexById.set(image.id, index);
  }
  return indexById;
}

export function removeImageSlot(
  images: Array<ImageFile | null>,
  imageId: string
): Array<ImageFile | null> {
  const index = images.findIndex((image) => image?.id === imageId);
  if (index < 0) return images;

  const next = [...images];
  next[index] = null;

  const laterPlaceholderIndex = next.findIndex((image, candidateIndex) => (
    candidateIndex > index && image === null
  ));
  if (laterPlaceholderIndex >= 0) {
    next.splice(laterPlaceholderIndex, 1);
    return next;
  }

  next.splice(index, 1);
  return next;
}

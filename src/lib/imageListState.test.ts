import { describe, expect, it } from 'vitest';
import type { ImageFile } from './types';
import { buildImageIndexById, removeImageSlot } from './imageListState';

function image(id: string): ImageFile {
  return {
    id,
    filename: `${id}.png`,
    absolutePath: id,
    fileUrl: `/api/image?path=${id}&thumb=true`,
    displayUrl: `/api/image?path=${id}&display=true`,
    fullUrl: `/api/image?path=${id}`,
    metadata: null,
    createdAt: 1,
    mtime: 1,
  };
}

describe('removeImageSlot', () => {
  it('removes only the deleted image and preserves unloaded placeholders', () => {
    const results = [image('a'), null, image('b'), null, image('c')];

    expect(removeImageSlot(results, 'b')).toEqual([
      image('a'),
      null,
      null,
      image('c'),
    ]);
  });

  it('keeps loaded items in place when a later unloaded placeholder can absorb the deletion', () => {
    const results = [image('a'), image('b'), image('c'), null, null];

    expect(removeImageSlot(results, 'b')).toEqual([
      image('a'),
      null,
      image('c'),
      null,
    ]);
  });

  it('returns the original array when the image is not loaded', () => {
    const results = [image('a'), null, image('c')];

    expect(removeImageSlot(results, 'b')).toBe(results);
  });
});

describe('buildImageIndexById', () => {
  it('indexes only loaded sparse search results', () => {
    const indexById = buildImageIndexById([
      image('a'),
      null,
      image('b'),
      null,
      image('c'),
    ]);

    expect(indexById.get('a')).toBe(0);
    expect(indexById.get('b')).toBe(2);
    expect(indexById.get('c')).toBe(4);
    expect(indexById.has('missing')).toBe(false);
  });

  it('keeps the latest loaded slot for duplicate ids', () => {
    const indexById = buildImageIndexById([image('a'), image('a')]);

    expect(indexById.get('a')).toBe(1);
  });
});

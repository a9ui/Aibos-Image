import { describe, expect, it } from 'vitest';

import { formatFileSizeMb } from './displayedAsset';

describe('formatFileSizeMb', () => {
  it.each([
    [0, '0.00MB'],
    [16 * 1024, '0.02MB'],
    [1024 * 1024 + 512 * 1024, '1.50MB'],
  ])('formats %i bytes with two decimals and no space', (bytes, expected) => {
    expect(formatFileSizeMb(bytes)).toBe(expected);
  });

  it.each([null, undefined, Number.NaN, -1])('returns a placeholder for missing or invalid bytes', (bytes) => {
    expect(formatFileSizeMb(bytes)).toBe('--');
  });
});

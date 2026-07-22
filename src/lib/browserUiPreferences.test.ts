import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SHOW_UNSEEN_MARKERS,
  isUnseenMarkerVisible,
  matchesFavoriteLevel,
  normalizeFavoriteFilterLevels,
  readFavoriteFilterLevelsPreference,
  readStoredFavoriteFilterLevels,
  toggleFavoriteFilterLevel,
} from './browserUiPreferences';

describe('favorite level filters', () => {
  it('normalizes persisted levels into unique ascending values', () => {
    expect(normalizeFavoriteFilterLevels([5, 2, 2, 8, '3'])).toEqual([2, 5]);
    expect(readStoredFavoriteFilterLevels('[4,1,4]')).toEqual([1, 4]);
    expect(readStoredFavoriteFilterLevels('3')).toEqual([3]);
    expect(readStoredFavoriteFilterLevels('invalid')).toEqual([]);
  });

  it('preserves an explicit All selection instead of reviving a legacy threshold', () => {
    expect(readFavoriteFilterLevelsPreference('[]', '3')).toEqual([]);
    expect(readFavoriteFilterLevelsPreference(null, '3')).toEqual([3]);
  });

  it('toggles levels independently without threshold semantics', () => {
    expect(toggleFavoriteFilterLevel([2, 4], 1)).toEqual([1, 2, 4]);
    expect(toggleFavoriteFilterLevel([1, 2, 4], 2)).toEqual([1, 4]);
  });

  it('treats no selected levels as All and selected levels as exact matches', () => {
    expect(matchesFavoriteLevel(0, [])).toBe(false);
    expect(matchesFavoriteLevel(3, [])).toBe(true);
    expect(matchesFavoriteLevel(1, [1, 4])).toBe(true);
    expect(matchesFavoriteLevel(3, [1, 4])).toBe(false);
    expect(matchesFavoriteLevel(4, [1, 4])).toBe(true);
  });
});

describe('unseen marker visibility', () => {
  it('defaults off and adds the marker only when enabled for an unseen image', () => {
    expect(DEFAULT_SHOW_UNSEEN_MARKERS).toBe(false);
    expect(isUnseenMarkerVisible(false, false)).toBe(false);
    expect(isUnseenMarkerVisible(true, true)).toBe(false);
    expect(isUnseenMarkerVisible(true, false)).toBe(true);
  });
});

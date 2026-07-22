import { describe, expect, it } from 'vitest';
import { getFavoriteDeleteProtection, shouldConfirmSourceDelete } from './favoriteDeleteProtection';

describe('favorite source delete protection', () => {
  it('requires confirmation when any unique target has a positive favorite level', () => {
    const protection = getFavoriteDeleteProtection(
      ['plain.png', 'favorite.png', 'favorite.png'],
      { 'plain.png': 0, 'favorite.png': 3 }
    );

    expect(protection).toEqual({ favoriteCount: 1, requiresConfirmation: true });
    expect(shouldConfirmSourceDelete(false, protection)).toBe(true);
  });

  it('keeps the ordinary preference for a selection without favorites', () => {
    const protection = getFavoriteDeleteProtection(['plain.png'], { 'plain.png': 0 });

    expect(shouldConfirmSourceDelete(false, protection)).toBe(false);
    expect(shouldConfirmSourceDelete(true, protection)).toBe(true);
  });
});

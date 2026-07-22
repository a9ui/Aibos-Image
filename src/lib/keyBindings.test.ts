import { describe, expect, it } from 'vitest';

import {
  getKeyBindingConflicts,
  isUnboundKeyBinding,
  normalizeKeyBinding,
  UNBOUND_KEY_BINDING,
} from './keyBindings';
import { DEFAULT_KEY_BINDINGS } from './types';

describe('key binding conflict detection', () => {
  it('normalizes printable keys case-insensitively while preserving Space', () => {
    expect(normalizeKeyBinding(' F ')).toBe('f');
    expect(normalizeKeyBinding(' ')).toBe('space');
    expect(normalizeKeyBinding(` ${UNBOUND_KEY_BINDING.toUpperCase()} `)).toBe('');
    expect(isUnboundKeyBinding('unbound')).toBe(true);
  });

  it('identifies every action using the same normalized key', () => {
    const bindings = { ...DEFAULT_KEY_BINDINGS, nextImage: 'F' };

    expect(getKeyBindingConflicts(bindings)).toEqual([
      { normalizedKey: 'f', actions: ['nextImage', 'toggleFavorite'] },
    ]);
  });

  it('does not report protected unbound actions as colliding with each other', () => {
    expect(getKeyBindingConflicts({
      ...DEFAULT_KEY_BINDINGS,
      toggleFilmstrip: UNBOUND_KEY_BINDING,
      addToAlbum: UNBOUND_KEY_BINDING,
    })).toEqual([]);
  });
});

import type { KeyBindings } from './types';

// Shared with the WPF settings contract. This value is deliberately not a
// KeyboardEvent.key value: it keeps a migrated action disabled until the user
// assigns a real key, rather than silently colliding with a future action.
export const UNBOUND_KEY_BINDING = 'Unbound';

export interface KeyBindingConflict {
  normalizedKey: string;
  actions: Array<keyof KeyBindings>;
}

/**
 * `KeyboardEvent.key` is case-sensitive for printable keys. Bindings are not:
 * F and f would otherwise dispatch the same viewer action.
 */
export function normalizeKeyBinding(key: string): string {
  const trimmed = key.trim();
  if (trimmed.toLocaleLowerCase() === UNBOUND_KEY_BINDING.toLocaleLowerCase()) return '';
  if (trimmed) return trimmed.toLocaleLowerCase();
  return key === ' ' ? 'space' : '';
}

export function isUnboundKeyBinding(key: string): boolean {
  return key.trim().toLocaleLowerCase() === UNBOUND_KEY_BINDING.toLocaleLowerCase();
}

export function getKeyBindingConflicts(
  bindings: Partial<KeyBindings>,
): KeyBindingConflict[] {
  const byKey = new Map<string, Array<keyof KeyBindings>>();
  for (const [rawAction, value] of Object.entries(bindings) as Array<[keyof KeyBindings, unknown]>) {
    if (typeof value !== 'string') continue;
    const normalizedKey = normalizeKeyBinding(value);
    if (!normalizedKey) continue;
    const actions = byKey.get(normalizedKey) ?? [];
    actions.push(rawAction);
    byKey.set(normalizedKey, actions);
  }

  return Array.from(byKey.entries())
    .filter(([, actions]) => actions.length > 1)
    .map(([normalizedKey, actions]) => ({ normalizedKey, actions }));
}

export function hasKeyBindingConflicts(bindings: Partial<KeyBindings>): boolean {
  return getKeyBindingConflicts(bindings).length > 0;
}

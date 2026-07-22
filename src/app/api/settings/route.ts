import { NextResponse } from 'next/server';
import { promises as fs } from 'fs';
import path from 'path';

import { withFileWriteLock } from '@/lib/fileWriteLock';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { hasKeyBindingConflicts, normalizeKeyBinding, UNBOUND_KEY_BINDING } from '@/lib/keyBindings';
import { resolveSharedCachePath } from '@/lib/sharedProjectRoot';
import type { AppSettings, KeyBindings } from '@/lib/types';
import { DEFAULT_KEY_BINDINGS } from '@/lib/types';
import { isValidThumbnailStatusBordersDocument, normalizeThumbnailStatusBorders } from '@/lib/thumbnailStatusBorders';

export const dynamic = 'force-dynamic';

type SettingsDocument = Record<string, unknown>;

const KEY_BINDING_NAMES = Object.keys(DEFAULT_KEY_BINDINGS) as Array<keyof KeyBindings>;
const KEY_BINDING_NAME_SET = new Set<string>(KEY_BINDING_NAMES);
const MAX_KEY_BINDING_LENGTH = 64;
const SAFE_MIGRATION_KEYS = ['b', 'g', 'v', 'y', 'j', 'k', 'l', 'n', 'm', 'q', 'w', 'x', 'c', 'z', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12'] as const;
const FILMSTRIP_MIGRATION_KEYS = [DEFAULT_KEY_BINDINGS.toggleFilmstrip, ...SAFE_MIGRATION_KEYS] as const;
const ADD_TO_ALBUM_MIGRATION_KEYS = [DEFAULT_KEY_BINDINGS.addToAlbum, ...SAFE_MIGRATION_KEYS.filter((key) => key !== DEFAULT_KEY_BINDINGS.addToAlbum)] as const;

function settingsPath() {
  return resolveSharedCachePath('settings.json', process.env.PVU_SETTINGS_PATH);
}

function isObject(value: unknown): value is SettingsDocument {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function validateKeyBindings(value: unknown): value is Partial<KeyBindings> & SettingsDocument {
  if (!isObject(value)) return false;
  for (const name of KEY_BINDING_NAMES) {
    if (!Object.hasOwn(value, name)) continue;
    const key = value[name];
    if (typeof key !== 'string' || key.length === 0 || key.length > MAX_KEY_BINDING_LENGTH) {
      return false;
    }
  }
  return true;
}

function validateStoredDocument(value: unknown): value is SettingsDocument {
  if (!isObject(value)) return false;
  if (Object.hasOwn(value, 'confirmBeforeDelete') && typeof value.confirmBeforeDelete !== 'boolean') {
    return false;
  }
  if (Object.hasOwn(value, 'keyBindings') && !validateKeyBindings(value.keyBindings)) {
    return false;
  }
  if (Object.hasOwn(value, 'thumbnailStatusBorders') && !isValidThumbnailStatusBordersDocument(value.thumbnailStatusBorders)) {
    return false;
  }
  return true;
}

function addUnknownStringBindingKeys(usedKeys: Set<string>, storedBindings: SettingsDocument): void {
  for (const [name, value] of Object.entries(storedBindings)) {
    if (KEY_BINDING_NAME_SET.has(name) || typeof value !== 'string') continue;
    const normalized = normalizeKeyBinding(value);
    if (normalized) usedKeys.add(normalized);
  }
}

function hasEffectiveKeyBindingConflicts(document: SettingsDocument): boolean {
  const effectiveBindings = publicSettings(document).keyBindings;
  if (hasKeyBindingConflicts(effectiveBindings)) return true;

  const knownKeys = new Set(Object.values(effectiveBindings).map(normalizeKeyBinding));
  const storedBindings = isObject(document.keyBindings) ? document.keyBindings : {};
  for (const [name, value] of Object.entries(storedBindings)) {
    if (!KEY_BINDING_NAME_SET.has(name) && typeof value === 'string') {
      const normalized = normalizeKeyBinding(value);
      if (normalized && knownKeys.has(normalized)) return true;
    }
  }
  return false;
}

function publicSettings(document: SettingsDocument): AppSettings {
  const storedBindings = validateKeyBindings(document.keyBindings) ? document.keyBindings : {};
  const keyBindings = Object.fromEntries(
    KEY_BINDING_NAMES.map((name) => [name, typeof storedBindings[name] === 'string' ? storedBindings[name] : DEFAULT_KEY_BINDINGS[name]]),
  ) as unknown as KeyBindings;

  // `toggleFilmstrip` was added after the original settings schema. Preserve a
  // user's existing T assignment instead of silently making two modal actions
  // fire from the same key. The fallback is only for an old document that has
  // no explicit filmstrip binding; an explicitly saved collision remains
  // visible to the normal Settings conflict repair flow.
  if (typeof storedBindings.toggleFilmstrip !== 'string') {
    const usedKeys = new Set(
      KEY_BINDING_NAMES.filter((name) => name !== 'toggleFilmstrip' && (name !== 'addToAlbum' || typeof storedBindings.addToAlbum === 'string')).map((name) => normalizeKeyBinding(keyBindings[name])),
    );
    addUnknownStringBindingKeys(usedKeys, storedBindings);
    keyBindings.toggleFilmstrip = FILMSTRIP_MIGRATION_KEYS.find((candidate) => !usedKeys.has(normalizeKeyBinding(candidate))) ?? UNBOUND_KEY_BINDING;
  }

  // Album v1 historically suggested B, but a migrated filmstrip may already
  // own it. Allocate the first free candidate without rewriting an explicit
  // user binding or introducing a silent collision.
  if (typeof storedBindings.addToAlbum !== 'string') {
    const usedKeys = new Set(KEY_BINDING_NAMES.filter((name) => name !== 'addToAlbum').map((name) => normalizeKeyBinding(keyBindings[name])));
    addUnknownStringBindingKeys(usedKeys, storedBindings);
    keyBindings.addToAlbum = ADD_TO_ALBUM_MIGRATION_KEYS.find((candidate) => !usedKeys.has(normalizeKeyBinding(candidate))) ?? UNBOUND_KEY_BINDING;
  }

  return {
    keyBindings,
    confirmBeforeDelete: typeof document.confirmBeforeDelete === 'boolean' ? document.confirmBeforeDelete : true,
    thumbnailStatusBorders: normalizeThumbnailStatusBorders(document.thumbnailStatusBorders),
  };
}

async function readSettings(): Promise<
  | {
      ok: true;
      document: SettingsDocument;
      settings: AppSettings;
      malformed: false;
    }
  | {
      ok: false;
      document: SettingsDocument;
      settings: AppSettings;
      malformed: true;
      error: string;
    }
> {
  const target = settingsPath();
  try {
    const raw = await fs.readFile(target, 'utf8');
    const parsed: unknown = JSON.parse(raw);
    if (!validateStoredDocument(parsed)) {
      return {
        ok: false,
        document: {},
        settings: publicSettings({}),
        malformed: true,
        error: 'settings.json does not match the supported schema.',
      };
    }
    return {
      ok: true,
      document: parsed,
      settings: publicSettings(parsed),
      malformed: false,
    };
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code === 'ENOENT') {
      return {
        ok: true,
        document: {},
        settings: publicSettings({}),
        malformed: false,
      };
    }
    return {
      ok: false,
      document: {},
      settings: publicSettings({}),
      malformed: true,
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

function validateIncomingDocument(value: unknown): { ok: true; update: SettingsDocument } | { ok: false; error: string } {
  if (!isObject(value)) {
    return { ok: false, error: 'Request body must be an object.' };
  }
  if (Object.hasOwn(value, 'confirmBeforeDelete') && typeof value.confirmBeforeDelete !== 'boolean') {
    return { ok: false, error: 'confirmBeforeDelete must be a boolean.' };
  }
  if (Object.hasOwn(value, 'keyBindings') && !validateKeyBindings(value.keyBindings)) {
    return {
      ok: false,
      error: 'keyBindings must contain only bounded string bindings.',
    };
  }
  if (Object.hasOwn(value, 'thumbnailStatusBorders')) {
    const borders = value.thumbnailStatusBorders;
    const hasPreferenceUpdate =
      isObject(borders) &&
      (['favorite', 'enhanced'] as const).some((status) => {
        const preference = borders[status];
        return isObject(preference) && (Object.hasOwn(preference, 'enabled') || Object.hasOwn(preference, 'color'));
      });
    if (!isValidThumbnailStatusBordersDocument(borders) || !hasPreferenceUpdate) {
      return {
        ok: false,
        error: 'thumbnailStatusBorders must update favorite or enhanced with a boolean enabled value, a six-digit hex color, or the enhanced rainbow preset.',
      };
    }
  }
  if (!Object.hasOwn(value, 'confirmBeforeDelete') && !Object.hasOwn(value, 'keyBindings') && !Object.hasOwn(value, 'thumbnailStatusBorders')) {
    return {
      ok: false,
      error: 'Request body must include a supported setting.',
    };
  }
  return { ok: true, update: value };
}

function mergeThumbnailStatusBorders(currentValue: unknown, incomingValue: unknown): SettingsDocument {
  const current = isObject(currentValue) ? currentValue : {};
  const incoming = isObject(incomingValue) ? incomingValue : {};
  const merged: SettingsDocument = { ...current, ...incoming };

  for (const status of ['favorite', 'enhanced'] as const) {
    const currentPreference = isObject(current[status]) ? current[status] : {};
    const incomingPreference = isObject(incoming[status]) ? incoming[status] : {};
    if (Object.hasOwn(current, status) || Object.hasOwn(incoming, status)) {
      merged[status] = { ...currentPreference, ...incomingPreference };
    }
  }
  return merged;
}

async function writeSettings(document: SettingsDocument) {
  const target = settingsPath();
  const dir = path.dirname(target);
  const temp = path.join(dir, `settings-${process.pid}-${Date.now()}.tmp`);
  await fs.mkdir(dir, { recursive: true });
  try {
    await fs.writeFile(temp, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
    await fs.rename(temp, target);
  } finally {
    await fs.unlink(temp).catch(() => {});
  }
}

export async function GET() {
  const result = await readSettings();
  return NextResponse.json({
    ...result.settings,
    malformed: result.malformed,
    error: result.ok ? undefined : result.error,
  });
}

export async function PUT(request: Request) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ ok: false, error: 'Request body must be valid JSON.' }, { status: 400 });
  }
  const incoming = validateIncomingDocument(body);
  if (!incoming.ok) {
    return NextResponse.json({ ok: false, error: incoming.error }, { status: 400 });
  }

  try {
    return await withFileWriteLock(settingsPath(), async () => {
      const current = await readSettings();
      if (!current.ok) {
        return NextResponse.json(
          {
            ok: false,
            error: 'Shared settings JSON is malformed; refusing to overwrite it.',
            ...current.settings,
            malformed: true,
          },
          { status: 409 },
        );
      }

      const incomingBindings = isObject(incoming.update.keyBindings) ? incoming.update.keyBindings : {};
      const currentBindings = isObject(current.document.keyBindings) ? current.document.keyBindings : {};
      const hasIncomingStatusBorders = Object.hasOwn(incoming.update, 'thumbnailStatusBorders');
      const updated: SettingsDocument = {
        ...current.document,
        ...incoming.update,
        keyBindings: {
          ...currentBindings,
          ...incomingBindings,
        },
        ...(hasIncomingStatusBorders
          ? {
              thumbnailStatusBorders: mergeThumbnailStatusBorders(current.document.thumbnailStatusBorders, incoming.update.thumbnailStatusBorders),
            }
          : {}),
      };
      if (Object.hasOwn(incoming.update, 'keyBindings') && hasEffectiveKeyBindingConflicts(updated)) {
        return NextResponse.json(
          {
            ok: false,
            error: 'Key bindings must not assign the same key to multiple actions.',
            ...current.settings,
            malformed: false,
          },
          { status: 409 },
        );
      }
      await writeSettings(updated);
      return NextResponse.json({
        ok: true,
        ...publicSettings(updated),
        malformed: false,
      });
    });
  } catch (error) {
    return NextResponse.json(
      {
        ok: false,
        error: error instanceof Error ? error.message : String(error),
        malformed: false,
      },
      { status: 503 },
    );
  }
}

import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { UNBOUND_KEY_BINDING } from '@/lib/keyBindings';
import { DEFAULT_KEY_BINDINGS, DEFAULT_THUMBNAIL_STATUS_BORDERS } from '@/lib/types';
import { GET, PUT } from './route';

let root = '';
let target = '';
const previousOverride = process.env.PVU_SETTINGS_PATH;

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-settings-route-'));
  target = path.join(root, 'settings.json');
  process.env.PVU_SETTINGS_PATH = target;
});

afterEach(async () => {
  if (previousOverride === undefined) delete process.env.PVU_SETTINGS_PATH;
  else process.env.PVU_SETTINGS_PATH = previousOverride;
  await fs.rm(root, { recursive: true, force: true });
});

function putRequest(body: string) {
  return new Request('http://127.0.0.1/api/settings', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body,
  });
}

describe('settings route write safety', () => {
  it('returns explicit defaults without creating a missing file', async () => {
    const response = await GET();
    const body = await response.json();

    expect(body).toMatchObject({
      keyBindings: DEFAULT_KEY_BINDINGS,
      confirmBeforeDelete: true,
      thumbnailStatusBorders: DEFAULT_THUMBNAIL_STATUS_BORDERS,
      malformed: false,
    });
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('persists independent thumbnail border settings while preserving nested future fields', async () => {
    await fs.writeFile(target, JSON.stringify({
      futureSetting: true,
      thumbnailStatusBorders: {
        favorite: { enabled: true, color: '#facc15', futureWidth: 4 },
        enhanced: { enabled: true, color: '#facc15' },
        futureStatus: { enabled: true },
      },
    }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      thumbnailStatusBorders: {
        favorite: { enabled: false, color: '#112233' },
        enhanced: { color: 'rainbow' },
      },
    })));
    const body = await response.json();
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(response.status).toBe(200);
    expect(body).toMatchObject({
      ok: true,
      thumbnailStatusBorders: {
        favorite: { enabled: false, color: '#112233' },
        enhanced: { enabled: true, color: '#38bdf8' },
      },
    });
    expect(stored).toMatchObject({
      futureSetting: true,
      thumbnailStatusBorders: {
        favorite: { enabled: false, color: '#112233', futureWidth: 4 },
        enhanced: { enabled: true, color: 'rainbow' },
        futureStatus: { enabled: true },
      },
    });
  });

  it('merges sequential cross-runtime thumbnail preference saves against the latest disk state', async () => {
    await fs.writeFile(target, JSON.stringify({
      futureSetting: { version: 2 },
      thumbnailStatusBorders: {
        favorite: { enabled: true, color: '#101010', futureWidth: 4 },
        enhanced: { enabled: false, color: '#202020', futureGlow: true },
      },
    }), 'utf8');

    const browserFavoriteResponse = await PUT(putRequest(JSON.stringify({
      thumbnailStatusBorders: {
        favorite: { enabled: false, color: '#303030' },
      },
    })));
    expect(browserFavoriteResponse.status).toBe(200);

    const wpfEnhancedResponse = await PUT(putRequest(JSON.stringify({
      thumbnailStatusBorders: {
        enhanced: { enabled: true, color: 'rainbow' },
      },
    })));
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(wpfEnhancedResponse.status).toBe(200);
    expect(stored).toMatchObject({
      futureSetting: { version: 2 },
      thumbnailStatusBorders: {
        favorite: { enabled: false, color: '#303030', futureWidth: 4 },
        enhanced: { enabled: true, color: 'rainbow', futureGlow: true },
      },
    });
  });

  it('keeps a legacy enhanced hex color solid and defaults only a missing color to cyan without rewriting', async () => {
    const legacy = {
      thumbnailStatusBorders: {
        enhanced: { enabled: true, color: '#AABBCC', futureWidth: 5 },
      },
    };
    await fs.writeFile(target, JSON.stringify(legacy), 'utf8');

    const legacyResponse = await GET();
    expect(await legacyResponse.json()).toMatchObject({
      thumbnailStatusBorders: {
        enhanced: { enabled: true, color: '#aabbcc' },
      },
      malformed: false,
    });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual(legacy);

    const missingColor = {
      thumbnailStatusBorders: {
        enhanced: { enabled: false, futureWidth: 7 },
      },
    };
    await fs.writeFile(target, JSON.stringify(missingColor), 'utf8');

    const defaultResponse = await GET();
    expect(await defaultResponse.json()).toMatchObject({
      thumbnailStatusBorders: {
        enhanced: { enabled: false, color: '#38bdf8' },
      },
      malformed: false,
    });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual(missingColor);
  });

  it('atomically merges a partial update while preserving unknown future fields', async () => {
    await fs.writeFile(target, JSON.stringify({
      futureSetting: { enabled: true },
      confirmBeforeDelete: true,
      keyBindings: { nextImage: 'n', futureAction: 'q' },
    }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      confirmBeforeDelete: false,
      keyBindings: { prevImage: 'p' },
    })));
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      ok: true,
      confirmBeforeDelete: false,
      keyBindings: { nextImage: 'n', prevImage: 'p' },
      malformed: false,
    });
    expect(stored).toMatchObject({
      futureSetting: { enabled: true },
      confirmBeforeDelete: false,
      keyBindings: { nextImage: 'n', prevImage: 'p', futureAction: 'q' },
    });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.tmp'))).toEqual([]);
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('defaults a missing filmstrip binding and preserves a legacy T assignment with a conflict-free fallback', async () => {
    await fs.writeFile(target, JSON.stringify({
      keyBindings: { nextImage: 't' },
    }), 'utf8');

    const response = await GET();
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body).toMatchObject({
      malformed: false,
      keyBindings: {
        nextImage: 't',
        toggleFilmstrip: 'b',
        addToAlbum: 'g',
      },
    });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual({
      keyBindings: { nextImage: 't' },
    });
  });

  it('migrates Add to Album away from an existing legacy B filmstrip binding', async () => {
    await fs.writeFile(target, JSON.stringify({
      keyBindings: { toggleFilmstrip: 'b' },
    }), 'utf8');

    const response = await GET();
    expect(await response.json()).toMatchObject({
      malformed: false,
      keyBindings: {
        toggleFilmstrip: 'b',
        addToAlbum: 'g',
      },
    });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual({
      keyBindings: { toggleFilmstrip: 'b' },
    });
  });

  it('reserves unknown future string bindings while migrating filmstrip and Add to Album', async () => {
    const original = JSON.stringify({
      keyBindings: {
        futureFilmstripAction: ' T ',
        futureAlbumAction: 'b',
        laterAction: 'G',
      },
    });
    await fs.writeFile(target, original, 'utf8');

    const response = await GET();

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      malformed: false,
      keyBindings: {
        toggleFilmstrip: 'v',
        addToAlbum: 'y',
      },
    });
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('keeps exhausted shortcut migrations unbound until Settings repairs them without losing unknown bindings', async () => {
    const migrationKeys = [
      't', 'b', 'g', 'v', 'y', 'j', 'k', 'l', 'n', 'm', 'q', 'w', 'x', 'c', 'z',
      'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12',
    ];
    const futureBindings = Object.fromEntries(
      migrationKeys.map((key, index) => [`futureAction${index}`, key]),
    );
    const original = { keyBindings: futureBindings, futureSetting: { keep: true } };
    await fs.writeFile(target, JSON.stringify(original), 'utf8');

    const response = await GET();
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body).toMatchObject({
      malformed: false,
      keyBindings: {
        toggleFilmstrip: UNBOUND_KEY_BINDING,
        addToAlbum: UNBOUND_KEY_BINDING,
      },
    });
    expect(await fs.readFile(target, 'utf8')).toBe(JSON.stringify(original));

    const repairResponse = await PUT(putRequest(JSON.stringify({
      keyBindings: {
        ...body.keyBindings,
        addToAlbum: 'p',
      },
    })));
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(repairResponse.status).toBe(200);
    expect(stored.futureSetting).toEqual({ keep: true });
    expect(stored.keyBindings).toMatchObject({
      ...futureBindings,
      toggleFilmstrip: UNBOUND_KEY_BINDING,
      addToAlbum: 'p',
    });
  });

  it.each([
    ['invalid JSON', '{'],
    ['array body', JSON.stringify([])],
    ['empty body', JSON.stringify({})],
    ['invalid confirmation', JSON.stringify({ confirmBeforeDelete: 'no' })],
    ['invalid bindings map', JSON.stringify({ keyBindings: [] })],
    ['invalid binding value', JSON.stringify({ keyBindings: { nextImage: 7 } })],
    ['empty binding value', JSON.stringify({ keyBindings: { nextImage: '' } })],
    ['empty thumbnail border patch', JSON.stringify({ thumbnailStatusBorders: {} })],
    ['empty thumbnail border preference', JSON.stringify({ thumbnailStatusBorders: { favorite: {} } })],
    ['invalid favorite border toggle', JSON.stringify({ thumbnailStatusBorders: { favorite: { enabled: 'yes' } } })],
    ['rainbow favorite border color', JSON.stringify({ thumbnailStatusBorders: { favorite: { color: 'rainbow' } } })],
    ['invalid enhanced border color', JSON.stringify({ thumbnailStatusBorders: { enhanced: { color: 'yellow' } } })],
  ])('rejects %s without replacing the existing file', async (_name, body) => {
    const original = '{"confirmBeforeDelete":true}\n';
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(body));

    expect(response.status).toBe(400);
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('reports and preserves an existing malformed shared file', async () => {
    const malformed = '{not-json';
    await fs.writeFile(target, malformed, 'utf8');

    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({
      keyBindings: DEFAULT_KEY_BINDINGS,
      confirmBeforeDelete: true,
      malformed: true,
    });

    const putResponse = await PUT(putRequest(JSON.stringify({ confirmBeforeDelete: false })));
    expect(putResponse.status).toBe(409);
    expect(await fs.readFile(target, 'utf8')).toBe(malformed);
  });

  it('treats invalid stored known fields as malformed instead of silently replacing them', async () => {
    const original = JSON.stringify({ confirmBeforeDelete: 'sometimes' });
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(JSON.stringify({ confirmBeforeDelete: true })));

    expect(response.status).toBe(409);
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('refuses a normalized key collision without replacing existing bindings', async () => {
    const original = JSON.stringify({
      confirmBeforeDelete: true,
      keyBindings: { nextImage: 'ArrowRight', toggleFavorite: 'f' },
    });
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      keyBindings: { nextImage: 'F' },
    })));

    expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({
      ok: false,
      error: 'Key bindings must not assign the same key to multiple actions.',
    });
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('refuses a known binding collision with an unknown future string action', async () => {
    const original = JSON.stringify({
      confirmBeforeDelete: true,
      keyBindings: { futureAction: ' B ' },
    });
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      keyBindings: { addToAlbum: 'b' },
    })));

    expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({
      ok: false,
      error: 'Key bindings must not assign the same key to multiple actions.',
    });
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it('preserves an existing collision until an explicit valid replacement resolves it', async () => {
    const original = JSON.stringify({
      keyBindings: { nextImage: 'f', toggleFavorite: 'F' },
    });
    await fs.writeFile(target, original, 'utf8');

    const unrelated = await PUT(putRequest(JSON.stringify({ confirmBeforeDelete: false })));
    expect(unrelated.status).toBe(200);
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toMatchObject({
      keyBindings: { nextImage: 'f', toggleFavorite: 'F' },
      confirmBeforeDelete: false,
    });

    const replacement = await PUT(putRequest(JSON.stringify({
      keyBindings: { toggleFavorite: 'g' },
    })));
    expect(replacement.status).toBe(200);
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toMatchObject({
      keyBindings: { nextImage: 'f', toggleFavorite: 'g' },
    });
  });

  it('serializes concurrent partial updates without dropping either field', async () => {
    await fs.writeFile(target, JSON.stringify({
      confirmBeforeDelete: true,
      keyBindings: {},
    }), 'utf8');

    const [first, second] = await Promise.all([
      PUT(putRequest(JSON.stringify({ confirmBeforeDelete: false }))),
      PUT(putRequest(JSON.stringify({ keyBindings: { nextImage: 'n' } }))),
    ]);
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(stored).toMatchObject({
      confirmBeforeDelete: false,
      keyBindings: { nextImage: 'n' },
    });
  });
});

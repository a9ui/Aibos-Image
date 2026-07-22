import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { GET, PUT } from './route';

let root = '';
let favoritesPath = '';
const previousOverride = process.env.PVU_FAVORITES_PATH;

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-favorites-route-'));
  favoritesPath = path.join(root, 'favorites.json');
  process.env.PVU_FAVORITES_PATH = favoritesPath;
});

afterEach(async () => {
  if (previousOverride === undefined) delete process.env.PVU_FAVORITES_PATH;
  else process.env.PVU_FAVORITES_PATH = previousOverride;
  await fs.rm(root, { recursive: true, force: true });
});

function putRequest(body: string) {
  return new Request('http://127.0.0.1/api/favorites', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body,
  });
}

describe('favorites route write safety', () => {
  it('normalizes a valid map and writes it through a temporary file', async () => {
    const response = await PUT(putRequest(JSON.stringify({
      favorites: { 'C:\\images\\one.png': 4, 'C:\\images\\off.png': 0, 'C:\\images\\legacy.png': true },
    })));

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      ok: true,
      favorites: { 'C:\\images\\one.png': 4, 'C:\\images\\legacy.png': 1 },
      malformed: false,
    });
    expect(JSON.parse(await fs.readFile(favoritesPath, 'utf8'))).toEqual({
      'C:\\images\\one.png': 4,
      'C:\\images\\legacy.png': 1,
    });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.tmp'))).toEqual([]);
  });

  it.each([
    ['invalid JSON', '{'],
    ['missing favorites field', JSON.stringify({})],
    ['array favorites', JSON.stringify({ favorites: [] })],
    ['whitespace favorite path', JSON.stringify({ favorites: { '   ': 1 } })],
    ['invalid favorite value', JSON.stringify({ favorites: { bad: { level: 5 } } })],
    ['invalid base favorites', JSON.stringify({ favorites: {}, baseFavorites: { bad: [] } })],
  ])('rejects %s without replacing the existing file', async (_name, body) => {
    const original = '{"keep":3}\n';
    await fs.writeFile(favoritesPath, original, 'utf8');

    const response = await PUT(putRequest(body));

    expect(response.status).toBe(400);
    expect(await fs.readFile(favoritesPath, 'utf8')).toBe(original);
  });

  it('refuses to overwrite an existing malformed shared file', async () => {
    const malformed = '{not-json';
    await fs.writeFile(favoritesPath, malformed, 'utf8');

    const response = await PUT(putRequest(JSON.stringify({ favorites: { replacement: 5 } })));

    expect(response.status).toBe(409);
    expect(await fs.readFile(favoritesPath, 'utf8')).toBe(malformed);
    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({ favorites: {}, malformed: true });
  });

  it('treats an invalid field inside an object document as malformed', async () => {
    const malformed = JSON.stringify({ keep: 3, invalid: { level: 5 } });
    await fs.writeFile(favoritesPath, malformed, 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      favorites: { replacement: 5 },
      baseFavorites: {},
    })));

    expect(response.status).toBe(409);
    expect(await fs.readFile(favoritesPath, 'utf8')).toBe(malformed);
  });

  it('protects arbitrary legacy strings exactly like the WPF reader', async () => {
    const malformed = '{"keep":3,"invalid":"not-a-level"}\n';
    await fs.writeFile(favoritesPath, malformed, 'utf8');

    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({ favorites: {}, malformed: true });

    const putResponse = await PUT(putRequest(JSON.stringify({
      favorites: { replacement: 5 },
      baseFavorites: {},
    })));
    expect(putResponse.status).toBe(409);
    expect(await fs.readFile(favoritesPath, 'utf8')).toBe(malformed);
  });

  it('keeps supported integer strings, booleans, and null compatible with WPF', async () => {
    await fs.writeFile(favoritesPath, JSON.stringify({
      stringLevel: ' 4 ',
      highString: '99',
      negativeString: '-2',
      legacyTrue: true,
      legacyFalse: false,
      legacyNull: null,
    }), 'utf8');

    const response = await GET();
    expect(await response.json()).toMatchObject({
      favorites: { stringLevel: 4, highString: 5, legacyTrue: 1 },
      malformed: false,
    });
  });

  it('applies only changes from the client base and preserves an external writer', async () => {
    await fs.writeFile(favoritesPath, JSON.stringify({ existing: 1, external: 4 }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      favorites: { existing: 2 },
      baseFavorites: { existing: 1 },
    })));

    expect(response.status).toBe(200);
    expect(JSON.parse(await fs.readFile(favoritesPath, 'utf8'))).toEqual({
      existing: 2,
      external: 4,
    });
  });

  it('serializes concurrent independent changes without losing either key', async () => {
    await fs.writeFile(favoritesPath, '{}', 'utf8');

    const [first, second] = await Promise.all([
      PUT(putRequest(JSON.stringify({ favorites: { first: 1 }, baseFavorites: {} }))),
      PUT(putRequest(JSON.stringify({ favorites: { second: 5 }, baseFavorites: {} }))),
    ]);

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(JSON.parse(await fs.readFile(favoritesPath, 'utf8'))).toEqual({
      first: 1,
      second: 5,
    });
    await expect(fs.stat(`${favoritesPath}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });
});

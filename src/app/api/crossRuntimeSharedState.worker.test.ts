import path from 'path';

import { expect, it } from 'vitest';

import { PUT as putFavorites } from './favorites/route';
import { PUT as putSeen } from './seen/route';

const iterations = Number.parseInt(process.env.CROSS_RUNTIME_ITERATIONS ?? '', 10);
const keyRoot = process.env.CROSS_RUNTIME_KEY_ROOT ?? '';
const enabled = Number.isInteger(iterations)
  && iterations > 0
  && keyRoot !== ''
  && Boolean(process.env.PVU_FAVORITES_PATH)
  && Boolean(process.env.PVU_SEEN_PATH);

function request(url: string, body: unknown) {
  return new Request(url, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

it.skipIf(!enabled)('writes Browser-owned shared favorite and seen keys without HTTP', async () => {

  for (let index = 0; index < iterations; index += 1) {
    const level = (index % 5) + 1;
    const favoriteKey = path.join(keyRoot, `browser-favorite-${String(index).padStart(2, '0')}.png`);
    const seenKey = path.join(keyRoot, `browser-seen-${String(index).padStart(2, '0')}.png`);

    const favoriteResponse = await putFavorites(request('http://127.0.0.1:3001/api/favorites', {
      favorites: { [favoriteKey]: level },
      baseFavorites: {},
    }));
    expect(favoriteResponse.status).toBe(200);
    expect(await favoriteResponse.json()).toMatchObject({ ok: true, malformed: false });

    const seenResponse = await putSeen(request('http://127.0.0.1:3001/api/seen', {
      seen: { [seenKey]: true },
    }));
    expect(seenResponse.status).toBe(200);
    expect(await seenResponse.json()).toMatchObject({ ok: true, malformed: false });
  }
});

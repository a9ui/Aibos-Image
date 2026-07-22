import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { GET, PUT } from './route';

let root = '';
let target = '';
const previousOverride = process.env.PVU_SEEN_PATH;

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-seen-route-'));
  target = path.join(root, 'seen.json');
  process.env.PVU_SEEN_PATH = target;
});

afterEach(async () => {
  if (previousOverride === undefined) delete process.env.PVU_SEEN_PATH;
  else process.env.PVU_SEEN_PATH = previousOverride;
  await fs.rm(root, { recursive: true, force: true });
});

function putRequest(body: string) {
  return new Request('http://127.0.0.1/api/seen', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body,
  });
}

describe('shared seen route safety', () => {
  it('returns an empty map without creating a file when shared state is absent', async () => {
    const response = await GET();

    expect(await response.json()).toMatchObject({ ok: true, seen: {}, malformed: false });
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('unions incoming seen markers with the latest disk map through an atomic file', async () => {
    await fs.writeFile(target, JSON.stringify({ 'D:\\wpf-seen.png': true }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      seen: { 'D:\\browser-seen.png': true },
    })));

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      ok: true,
      seen: { 'D:\\wpf-seen.png': true, 'D:\\browser-seen.png': true },
    });
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual({
      'D:\\wpf-seen.png': true,
      'D:\\browser-seen.png': true,
    });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.tmp'))).toEqual([]);
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('reads WPF legacy stored markers but writes the canonical true-only map', async () => {
    await fs.writeFile(target, JSON.stringify({
      'D:\\true.png': true,
      'D:\\false.png': false,
      'D:\\one.png': 1,
      'D:\\zero.png': 0,
      'D:\\string-true.png': ' TRUE ',
      'D:\\string-false.png': 'false',
    }), 'utf8');

    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({
      ok: true,
      malformed: false,
      seen: {
        'D:\\true.png': true,
        'D:\\one.png': true,
        'D:\\string-true.png': true,
      },
    });

    const putResponse = await PUT(putRequest(JSON.stringify({ seen: { 'D:\\new.png': true } })));
    expect(putResponse.status).toBe(200);
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual({
      'D:\\true.png': true,
      'D:\\one.png': true,
      'D:\\string-true.png': true,
      'D:\\new.png': true,
    });
  });

  it('serializes concurrent seen clients without losing either true marker', async () => {
    const [first, second] = await Promise.all([
      PUT(putRequest(JSON.stringify({ seen: { 'D:\\browser.png': true } }))),
      PUT(putRequest(JSON.stringify({ seen: { 'D:\\wpf.png': true } }))),
    ]);

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(JSON.parse(await fs.readFile(target, 'utf8'))).toEqual({
      'D:\\browser.png': true,
      'D:\\wpf.png': true,
    });
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('fails closed on a stale legacy lock instead of pathname-reaping it', async () => {
    const lockPath = `${target}.lock`;
    await fs.writeFile(lockPath, 'stale writer', 'utf8');
    const old = new Date(Date.now() - 60_000);
    await fs.utimes(lockPath, old, old);

    const response = await PUT(putRequest(JSON.stringify({ seen: { 'D:\\recovered.png': true } })));

    expect(response.status).toBe(503);
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
    expect(await fs.readFile(lockPath, 'utf8')).toBe('stale writer');
  }, 5_000);

  it.each([
    ['invalid JSON', '{'],
    ['array document', JSON.stringify([])],
    ['future marker shape', JSON.stringify({ 'D:\\future.png': { marker: true } })],
  ])('preserves a malformed or unsupported shared document: %s', async (_name, malformed) => {
    await fs.writeFile(target, malformed, 'utf8');

    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({ ok: false, malformed: true });
    const putResponse = await PUT(putRequest(JSON.stringify({ seen: { 'D:\\new.png': true } })));
    expect(putResponse.status).toBe(409);
    expect(await fs.readFile(target, 'utf8')).toBe(malformed);
  });

  it.each([
    ['missing seen map', JSON.stringify({})],
    ['false marker', JSON.stringify({ seen: { 'D:\\not-seen.png': false } })],
    ['truthy compatibility escape hatch', JSON.stringify({ seen: { 'D:\\not-strict.png': 'true' } })],
  ])('rejects invalid incoming maps without replacing existing state: %s', async (_name, body) => {
    const original = JSON.stringify({ 'D:\\keep.png': true });
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(body));

    expect(response.status).toBe(400);
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });
});

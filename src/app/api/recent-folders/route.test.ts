import { promises as fs } from 'fs';
import os from 'os';
import path from 'path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { GET, PUT } from './route';

let root = '';
let target = '';
const previousOverride = process.env.PVU_RECENT_FOLDERS_PATH;

beforeEach(async () => {
  root = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-recent-route-'));
  target = path.join(root, 'recent-folders.json');
  process.env.PVU_RECENT_FOLDERS_PATH = target;
});

afterEach(async () => {
  if (previousOverride === undefined) delete process.env.PVU_RECENT_FOLDERS_PATH;
  else process.env.PVU_RECENT_FOLDERS_PATH = previousOverride;
  await fs.rm(root, { recursive: true, force: true });
});

function putRequest(body: string) {
  return new Request('http://127.0.0.1/api/recent-folders', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body,
  });
}

describe('recent folders route write safety', () => {
  it('returns an empty normalized document when the shared file is absent', async () => {
    const response = await GET();
    const body = await response.json();

    expect(body).toMatchObject({
      ok: true,
      malformed: false,
      recent: { version: 1, lastFolderSet: [], recentFolderSets: [] },
    });
    await expect(fs.stat(target)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('merges incoming and existing folder sets through an atomic temporary file', async () => {
    await fs.writeFile(target, JSON.stringify({
      version: 1,
      lastFolderSet: ['D:\\existing'],
      recentFolderSets: [['D:\\existing']],
      updatedAtUtc: '2026-07-18T00:00:00.000Z',
      futureFlag: { keep: true },
    }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({
      lastDirSet: 'E:\\new',
      recentDirs: ['E:\\new', 'D:\\existing'],
    })));
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(response.status).toBe(200);
    expect(stored.lastFolderSet).toEqual(['E:\\new']);
    expect(stored.recentFolderSets).toEqual([['E:\\new'], ['D:\\existing']]);
    expect(stored.futureFlag).toEqual({ keep: true });
    expect((await fs.readdir(root)).filter((name) => name.endsWith('.tmp'))).toEqual([]);
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('preserves the current last folder set when a partial update only supplies recentDirs', async () => {
    await fs.writeFile(target, JSON.stringify({
      version: 1,
      lastFolderSet: ['D:\\last'],
      recentFolderSets: [['D:\\last']],
      updatedAtUtc: '2026-07-18T00:00:00.000Z',
    }), 'utf8');

    const response = await PUT(putRequest(JSON.stringify({ recentDirs: ['E:\\new'] })));
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body.recent.lastFolderSet).toEqual(['D:\\last']);
    expect(body.recent.recentFolderSets).toEqual([
      ['D:\\last'],
      ['E:\\new'],
    ]);
  });

  it.each([
    ['invalid JSON', '{'],
    ['array body', JSON.stringify([])],
    ['empty body', JSON.stringify({})],
    ['invalid recentDirs', JSON.stringify({ recentDirs: [7] })],
    ['invalid lastDirSet', JSON.stringify({ lastDirSet: false })],
  ])('rejects %s without replacing the existing file', async (_name, body) => {
    const original = '{"version":1,"lastFolderSet":[],"recentFolderSets":[],"updatedAtUtc":"now"}\n';
    await fs.writeFile(target, original, 'utf8');

    const response = await PUT(putRequest(body));

    expect(response.status).toBe(400);
    expect(await fs.readFile(target, 'utf8')).toBe(original);
  });

  it.each([
    ['invalid JSON', '{not-json'],
    ['invalid version', JSON.stringify({ version: 2 })],
    ['invalid folder set', JSON.stringify({ version: 1, lastFolderSet: [7] })],
  ])('reports and preserves malformed shared state: %s', async (_name, malformed) => {
    await fs.writeFile(target, malformed, 'utf8');

    const getResponse = await GET();
    expect(await getResponse.json()).toMatchObject({ ok: false, malformed: true });
    const putResponse = await PUT(putRequest(JSON.stringify({ lastDirSet: 'E:\\new' })));
    expect(putResponse.status).toBe(409);
    expect(await fs.readFile(target, 'utf8')).toBe(malformed);
  });

  it('serializes concurrent folder-set merges without losing either update', async () => {
    await fs.writeFile(target, JSON.stringify({
      version: 1,
      lastFolderSet: [],
      recentFolderSets: [],
      updatedAtUtc: '2026-07-18T00:00:00.000Z',
    }), 'utf8');

    const [first, second] = await Promise.all([
      PUT(putRequest(JSON.stringify({ recentDirs: ['D:\\one'] }))),
      PUT(putRequest(JSON.stringify({ recentDirs: ['E:\\two'] }))),
    ]);
    const stored = JSON.parse(await fs.readFile(target, 'utf8'));

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(stored.recentFolderSets).toEqual(expect.arrayContaining([
      ['D:\\one'],
      ['E:\\two'],
    ]));
    await expect(fs.stat(`${target}.lock`)).rejects.toMatchObject({ code: 'ENOENT' });
  });
});

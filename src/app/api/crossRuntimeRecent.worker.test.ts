import { promises as fs } from 'fs';
import path from 'path';

import { expect, it } from 'vitest';

import { withFileWriteLock } from '@/lib/fileWriteLock';
import { formatDirSet } from '@/lib/pathSet';
import { buildSharedRecentFolders, normalizeSharedRecentFolders } from '@/lib/recentFolders';

import { PUT as putRecentFolders } from './recent-folders/route';

const iterations = Number.parseInt(process.env.CROSS_RUNTIME_ITERATIONS ?? '', 10);
const keyRoot = process.env.CROSS_RUNTIME_KEY_ROOT ?? '';
const target = process.env.PVU_RECENT_FOLDERS_PATH ?? '';
const enabled = Number.isInteger(iterations)
  && iterations > 0
  && keyRoot !== ''
  && target !== '';

function request(body: unknown) {
  return new Request('http://127.0.0.1:3001/api/recent-folders', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
}

async function independentThirdWriter(marker: string) {
  return withFileWriteLock(target, async () => {
    let document: Record<string, unknown> = {};
    try {
      document = JSON.parse(await fs.readFile(target, 'utf8')) as Record<string, unknown>;
    } catch (error) {
      if ((error as NodeJS.ErrnoException)?.code !== 'ENOENT') throw error;
    }
    const current = normalizeSharedRecentFolders(document);
    const recent = buildSharedRecentFolders({
      lastDirSet: marker,
      recentDirs: [marker, ...current.recentFolderSets.map((set) => formatDirSet(set))],
    });
    const dir = path.dirname(target);
    const temp = path.join(dir, `recent-folders-third-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}.tmp`);
    try {
      await fs.mkdir(dir, { recursive: true });
      await fs.writeFile(temp, `${JSON.stringify({ ...document, ...recent }, null, 2)}\n`, 'utf8');
      await fs.rename(temp, target);
    } finally {
      await fs.unlink(temp).catch(() => {});
    }
  });
}

it.skipIf(!enabled)('writes Browser and independent third-owner recent markers without HTTP', async () => {
  const browserMarker = path.join(keyRoot, 'browser-latest');
  const thirdMarker = path.join(keyRoot, 'third-latest');
  for (let index = 0; index < iterations; index += 1) {
    const [browser] = await Promise.all([
      putRecentFolders(request({ lastDirSet: browserMarker, recentDirs: [browserMarker] })),
      independentThirdWriter(thirdMarker),
    ]);
    expect(browser.status).toBe(200);
    expect(await browser.json()).toMatchObject({ ok: true, malformed: false });
  }
});

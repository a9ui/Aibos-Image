import { expect, test, type Page } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

import { DEFAULT_KEY_BINDINGS } from '../src/lib/types';

const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
  'base64',
);

async function mockUnrelatedSharedState(page: Page) {
  await page.route('**/api/recent-folders', (route) => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ ok: true, malformed: false, recent: { version: 1, lastFolderSet: [], recentFolderSets: [], updatedAtUtc: '' } }),
  }));
  await page.route('**/api/legacy-state', (route) => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ recentDirs: [], lastDirSet: '' }),
  }));
  for (const endpoint of ['favorites', 'seen']) {
    await page.route(`**/api/${endpoint}`, async (route) => {
      const body = route.request().method() === 'PUT' ? await route.request().postDataJSON() : {};
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ ok: true, malformed: false, [endpoint]: body?.[endpoint] ?? {} }),
      });
    });
  }
  await page.route('**/api/settings', (route) => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ ok: true, malformed: false, keyBindings: DEFAULT_KEY_BINDINGS, confirmBeforeDelete: true }),
  }));
  await page.route('**/api/search-history', (route) => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ ok: true, malformed: false, futureVersion: false, entries: [] }),
  }));
  await page.route('**/api/enhance/jobs**', (route) => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ jobs: [] }),
  }));
}

test.describe('Album v1 Browser source contract', () => {
  let fixtureRoot = '';
  let catalog = '';
  let outsidePath = '';
  let missingPath = '';

  test.beforeAll(async () => {
    fixtureRoot = await mkdtemp(join(tmpdir(), 'photoviewer-album-e2e-'));
    catalog = join(fixtureRoot, 'catalog');
    outsidePath = join(fixtureRoot, 'outside.png');
    missingPath = join(fixtureRoot, 'missing.webp');
    await import('node:fs/promises').then(({ mkdir }) => mkdir(catalog, { recursive: true }));
    await writeFile(join(catalog, 'current-a.png'), PNG_1X1);
    await writeFile(join(catalog, 'current-b.png'), PNG_1X1);
    await writeFile(outsidePath, PNG_1X1);
  });

  test.afterAll(async () => {
    const resolved = resolve(fixtureRoot);
    const temp = resolve(tmpdir());
    if (!resolved.startsWith(`${temp}\\photoviewer-album-e2e-`)) {
      throw new Error(`Refusing to remove unexpected fixture path: ${resolved}`);
    }
    await rm(resolved, { recursive: true, force: true });
    const albumStore = process.env.PVU_ALBUMS_PATH;
    if (albumStore) {
      const albumRoot = resolve(dirname(albumStore));
      if (albumRoot.startsWith(`${temp}\\pvu-album-e2e-store-`)) {
        await rm(albumRoot, { recursive: true, force: true });
      }
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(!process.env.PVU_ALBUMS_PATH, 'PVU_ALBUMS_PATH must point to an isolated temp store.');
    await page.setViewportSize({ width: 1440, height: 900 });
    await mockUnrelatedSharedState(page);
  });

  test('creates, adds, opens, navigates, and separates Album removal from source Recycle', async ({ page, request }) => {
    const consoleProblems: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') consoleProblems.push(message.text());
    });

    await page.goto('/');
    await page.getByPlaceholder('Paste one absolute path per line...').fill(catalog);
    await page.getByRole('button', { name: 'Add pasted' }).click();
    await page.getByRole('button', { name: 'Open folder set', exact: true }).click();
    await expect(page.locator('.viewer')).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-image-primary="true"]')).toHaveCount(2, { timeout: 15_000 });

    await page.locator('[data-image-primary="true"]').first().click();
    await page.getByRole('button', { name: 'Add selected images to Album' }).click();
    const picker = page.getByRole('dialog', { name: 'Add to Album' });
    await expect(picker).toBeVisible();
    await picker.getByLabel('New Album name').fill('Parity Album');
    await picker.getByRole('button', { name: 'Create & Add' }).click();
    await expect(picker).toBeHidden();

    const libraryResponse = await request.get('/api/albums');
    const library = await libraryResponse.json();
    const album = library.document.albums[0];
    const added = await request.post(`/api/albums/${album.id}/members`, {
      data: { paths: [outsidePath, missingPath], expectedRevision: library.document.revision },
    });
    expect(added.ok()).toBe(true);

    await page.getByRole('button', { name: 'Open Album library' }).first().click();
    const libraryDialog = page.getByRole('dialog', { name: 'Albums' });
    await expect(libraryDialog).toBeVisible();
    await libraryDialog.getByRole('button', { name: /Parity Album/ }).first().click();
    await expect(libraryDialog).toBeHidden();
    await expect(page.locator('.album-source-banner')).toContainText('1 current');
    await expect(page.locator('.album-source-banner')).toContainText('1 outside catalog');
    await expect(page.locator('.album-source-banner')).toContainText('1 missing');
    await expect(page.locator('[data-image-primary="true"]')).toHaveCount(2);

    await page.locator('[data-image-primary="true"]').first().dblclick();
    await expect(page.getByRole('dialog', { name: 'Image preview' })).toBeVisible();
    await page.keyboard.press('ArrowRight');
    await expect(page.locator('.modal-counter')).toContainText('2 / 2');
    await page.keyboard.press('Escape');

    await page.locator('[data-image-primary="true"]').first().click();
    await page.getByRole('button', { name: 'Remove selected from Album' }).click();
    await expect(page.locator('[data-image-primary="true"]')).toHaveCount(1);
    const outsideStillReadable = await import('node:fs/promises').then(({ stat }) => stat(outsidePath).then(() => true, () => false));
    expect(outsideStillReadable).toBe(true);

    await page.getByRole('main').getByRole('button', { name: 'Return to catalog' }).click();
    await expect(page.locator('[data-image-primary="true"]')).toHaveCount(2);
    expect(consoleProblems).toEqual([]);
  });
});

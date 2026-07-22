import { expect, test, type Page } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { DEFAULT_KEY_BINDINGS } from '../src/lib/types';

const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
  'base64',
);

async function openFixtureFolder(page: Page, fixtureDir: string) {
  await page.goto('/');
  await page.getByPlaceholder('Paste one absolute path per line...').fill(fixtureDir);
  await page.getByRole('button', { name: 'Add pasted' }).click();
  await page.getByRole('button', { name: 'Open folder set', exact: true }).click();
  await expect(page.locator('.viewer')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.viewer-header .header-stats')).toContainText('120', { timeout: 15_000 });
  await expect(page.locator('[data-image-primary="true"]').first()).toBeVisible();
}

test.describe('modal filmstrip runtime contract', () => {
  let fixtureDir = '';

  test.beforeAll(async () => {
    fixtureDir = await mkdtemp(join(tmpdir(), 'photoviewer-modal-filmstrip-e2e-'));
    await Promise.all(Array.from({ length: 120 }, (_, index) => (
      writeFile(join(fixtureDir, `filmstrip-${String(index).padStart(3, '0')}.png`), PNG_1X1)
    )));
  });

  test.beforeEach(async ({ page }) => {
    let searchHistoryEntries = ['cat, dog', 'landscape, night'];
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.route('**/api/recent-folders', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          ok: true,
          malformed: false,
          recent: { version: 1, lastFolderSet: [], recentFolderSets: [], updatedAtUtc: '' },
        }),
      });
    });
    await page.route('**/api/legacy-state', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ recentDirs: [], lastDirSet: '' }),
      });
    });
    await page.route('**/api/favorites', async (route) => {
      const body = route.request().method() === 'PUT'
        ? await route.request().postDataJSON()
        : { favorites: {} };
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ ok: true, malformed: false, favorites: body?.favorites ?? {} }),
      });
    });
    await page.route('**/api/seen', async (route) => {
      const body = route.request().method() === 'PUT'
        ? await route.request().postDataJSON()
        : { seen: {} };
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ ok: true, malformed: false, seen: body?.seen ?? {} }),
      });
    });
    await page.route('**/api/settings', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          ok: true,
          malformed: false,
          keyBindings: DEFAULT_KEY_BINDINGS,
          confirmBeforeDelete: false,
        }),
      });
    });
    await page.route('**/api/enhance/jobs**', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ jobs: [] }),
      });
    });
    await page.route('**/api/search-history', async (route) => {
      const method = route.request().method();
      if (method === 'PUT') {
        const body = await route.request().postDataJSON() as { query?: string };
        if (body.query) {
          searchHistoryEntries = [
            body.query,
            ...searchHistoryEntries.filter((entry) => entry !== body.query),
          ];
        }
      } else if (method === 'DELETE') {
        const body = await route.request().postDataJSON() as { query?: string; clear?: boolean };
        searchHistoryEntries = body.clear
          ? []
          : searchHistoryEntries.filter((entry) => entry !== body.query);
      }
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          ok: true,
          entries: searchHistoryEntries,
          malformed: false,
          futureVersion: false,
        }),
      });
    });
  });

  test.afterAll(async ({ request }) => {
    await expect.poll(async () => {
      const response = await request.get('/api/thumbs/warm');
      if (!response.ok()) return false;
      const payload = await response.json() as {
        warmup?: { running?: boolean; activeThumbJobs?: number; pendingThumbs?: number; queuedThumbJobs?: number };
      };
      const warmup = payload.warmup;
      return Boolean(warmup)
        && warmup?.running === false
        && warmup.activeThumbJobs === 0
        && warmup.pendingThumbs === 0
        && warmup.queuedThumbJobs === 0;
    }, { timeout: 15_000 }).toBe(true);

    const resolvedFixture = resolve(fixtureDir);
    const resolvedTemp = resolve(tmpdir());
    if (!resolvedFixture.startsWith(`${resolvedTemp}\\photoviewer-modal-filmstrip-e2e-`)) {
      throw new Error(`Refusing to remove unexpected fixture path: ${resolvedFixture}`);
    }
    await rm(resolvedFixture, { recursive: true, force: true });
  });

  test('virtualizes thumbnails, navigates directly, and persists hide/show across modal sessions', async ({ page }) => {
    const consoleProblems: string[] = [];
    const imageResponses: Array<{ url: string; status: number }> = [];
    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') consoleProblems.push(message.text());
    });
    page.on('response', (response) => {
      if (new URL(response.url()).pathname === '/api/image') {
        imageResponses.push({ url: response.url(), status: response.status() });
      }
    });

    await openFixtureFolder(page, fixtureDir);
    await expect.poll(() => imageResponses.filter((response) => response.status === 200).length)
      .toBeGreaterThan(0);
    expect(imageResponses.some((response) => response.status === 403)).toBe(false);
    const initialImageRequestCounts = new Map<string, number>();
    for (const response of imageResponses) {
      initialImageRequestCounts.set(response.url, (initialImageRequestCounts.get(response.url) ?? 0) + 1);
    }
    expect(Math.max(...initialImageRequestCounts.values())).toBeLessThanOrEqual(2);

    const searchInput = page.getByRole('combobox', { name: 'Search tags' });
    await searchInput.click();
    const searchHistory = page.getByRole('region', { name: 'Search history' });
    await expect(searchHistory).toBeVisible();
    await searchInput.press('ArrowUp');
    const lastHistoryItem = searchHistory.getByRole('button', { name: 'Use search landscape, night' });
    await expect(lastHistoryItem).toHaveClass(/selected/);
    await expect(page.getByRole('status', { name: 'Search history status' }))
      .toContainText('Search history 2 of 2: landscape, night');
    await searchInput.press('Enter');
    await expect(searchHistory).toHaveCount(0);
    await expect(searchInput).toBeFocused();
    await expect(page.locator('.search-chip')).toHaveText(['landscape', 'night']);

    await page.getByRole('button', { name: 'Clear all search tags' }).click();
    await expect(page.locator('.viewer-header .header-stats')).toContainText('120', { timeout: 15_000 });
    await searchInput.click();
    await expect(searchHistory).toBeVisible();
    const firstHistoryItem = searchHistory.getByRole('button', { name: 'Use search landscape, night' });
    await firstHistoryItem.focus();
    await firstHistoryItem.press('ArrowDown');
    await expect(searchHistory.getByRole('button', { name: 'Use search cat, dog' })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(searchHistory).toHaveCount(0);
    await expect(searchInput).toBeFocused();

    await page.locator('[data-image-primary="true"]').first().dblclick();

    const dialog = page.getByRole('dialog', { name: /Image preview:/ });
    const listbox = page.getByRole('listbox', { name: 'Image filmstrip thumbnails' });
    await expect(dialog).toBeVisible();
    await expect(listbox).toBeVisible();
    const imageAreaBox = await page.locator('.modal-image-area').boundingBox();
    const filmstripBox = await page.locator('.modal-filmstrip-shell').boundingBox();
    const zoomIndicatorBox = await page.locator('.zoom-indicator').boundingBox();
    if (!imageAreaBox || !filmstripBox || !zoomIndicatorBox) throw new Error('Expected modal layout bounds');
    // CSS grid tracks can land on fractional device pixels. A sub-pixel border
    // rounding difference is not an overlay; require no whole-pixel intrusion.
    expect(filmstripBox.y + 1).toBeGreaterThanOrEqual(imageAreaBox.y + imageAreaBox.height);
    expect(zoomIndicatorBox.y).toBeGreaterThan(imageAreaBox.y);
    expect(zoomIndicatorBox.y + zoomIndicatorBox.height).toBeLessThan(filmstripBox.y);

    const renderedOptions = listbox.getByRole('option');
    await expect.poll(() => renderedOptions.count()).toBeGreaterThan(1);
    expect(await renderedOptions.count()).toBeLessThan(40);

    const current = listbox.locator('[role="option"][aria-current="true"]');
    await expect(current).toHaveCount(1);
    await expect(current).toHaveAttribute('aria-selected', 'true');

    await listbox.evaluate((element) => {
      element.scrollLeft = 110 * 72;
      element.dispatchEvent(new Event('scroll'));
    });
    const target = listbox.locator('[role="option"][data-filmstrip-index="110"]');
    await expect(target).toBeVisible({ timeout: 15_000 });
    const targetLabel = await target.getAttribute('aria-label');
    const targetFilename = await target.getAttribute('title');
    expect(targetLabel).toBeTruthy();
    expect(targetFilename).toBeTruthy();
    await target.click();
    await expect(page.locator('.modal-filename')).toHaveText(targetFilename!);
    await expect(page.locator('.modal-counter')).toHaveText('111 / 120');
    await expect(listbox.getByRole('option', { name: targetLabel! })).toHaveAttribute('aria-current', 'true');
    await page.waitForTimeout(260);
    await expect(page.locator('.modal-filename')).toHaveText(targetFilename!);

    await target.focus();
    await page.keyboard.press('ArrowRight');
    await expect(page.locator('.modal-counter')).toHaveText('112 / 120');
    await page.keyboard.press('ArrowLeft');
    await expect(page.locator('.modal-counter')).toHaveText('111 / 120');
    await expect(page.locator('.modal-filename')).toHaveText(targetFilename!);

    const modalImage = page.locator('.modal-full-image');
    const clickModalImageCenter = () => modalImage.evaluate((element) => {
      const area = element.closest('.modal-image-area');
      if (!area) throw new Error('Expected modal image area');
      const rect = area.getBoundingClientRect();
      element.dispatchEvent(new MouseEvent('click', {
        bubbles: true,
        cancelable: true,
        detail: 1,
        clientX: rect.left + rect.width / 2,
        clientY: rect.top + rect.height / 2,
      }));
    });
    await clickModalImageCenter();
    await expect(page.locator('.modal-body')).toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-body')).toHaveClass(/cursor-hidden/);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveCount(0);
    await clickModalImageCenter();
    await expect(page.locator('.modal-body')).not.toHaveClass(/chrome-hidden/);
    await expect(listbox).toBeVisible();

    await dialog.focus();
    await page.keyboard.press('Delete');
    await expect.poll(() => page.locator('.modal-filename').textContent()).not.toBe(targetFilename);
    const afterDelete = await page.locator('.modal-filename').textContent();
    await expect(page.locator('.modal-counter')).toContainText('/ 119');
    await expect(listbox.locator('[role="option"][aria-current="true"]')).toHaveCount(1);
    await expect(listbox.locator('[role="option"][aria-current="true"]')).toHaveAttribute('title', afterDelete!);
    await page.waitForTimeout(320);
    await expect(page.locator('.modal-filename')).toHaveText(afterDelete!);

    const toolbarToggle = page.locator('.modal-topbar').getByRole('button', { name: 'Hide image filmstrip' });
    await expect(toolbarToggle).toHaveAttribute('aria-keyshortcuts', 'T');
    await toolbarToggle.click();
    await expect(listbox).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => (
      JSON.parse(localStorage.getItem('pvu_view') || '{}') as { modalFilmstripOpen?: boolean }
    ).modalFilmstripOpen)).toBe(false);

    await clickModalImageCenter();
    await expect(page.locator('.modal-body')).toHaveClass(/chrome-hidden/);
    await clickModalImageCenter();
    await expect(page.locator('.modal-body')).not.toHaveClass(/chrome-hidden/);
    await expect(listbox).toHaveCount(0);
    await expect(page.locator('.modal-topbar').getByRole('button', { name: 'Show image filmstrip' }))
      .toHaveAttribute('aria-expanded', 'false');

    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await page.locator('[data-image-primary="true"]').first().dblclick();
    await expect(dialog).toBeVisible();
    await expect(listbox).toHaveCount(0);

    const showToggle = page.locator('.modal-topbar').getByRole('button', { name: 'Show image filmstrip' });
    await expect(showToggle).toHaveAttribute('aria-expanded', 'false');
    await dialog.focus();
    await page.keyboard.press('t');
    await expect(listbox).toBeVisible();
    await expect.poll(() => page.evaluate(() => (
      JSON.parse(localStorage.getItem('pvu_view') || '{}') as { modalFilmstripOpen?: boolean }
    ).modalFilmstripOpen)).toBe(true);

    await page.waitForTimeout(3_200);
    await expect(page.locator('.modal-body')).not.toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveClass(/is-layout/);

    await clickModalImageCenter();
    await expect(page.locator('.modal-body')).toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-body')).toHaveClass(/cursor-hidden/);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveCount(0);
    const imageAreaBeforeTransientStrip = await page.locator('.modal-image-area').boundingBox();

    const dialogBox = await dialog.boundingBox();
    if (!dialogBox) throw new Error('Expected visible modal bounds');
    await page.mouse.move(dialogBox.x + dialogBox.width / 2, dialogBox.y + dialogBox.height / 2);
    await expect(page.locator('.modal-body')).not.toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveCount(0);
    await page.waitForTimeout(1_000);
    await expect(page.locator('.modal-body')).toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-body')).toHaveClass(/cursor-hidden/);

    await page.mouse.move(dialogBox.x + dialogBox.width / 2, dialogBox.y + dialogBox.height - 20);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveClass(/is-overlay/);
    await expect(listbox).toBeVisible();
    const imageAreaWithTransientStrip = await page.locator('.modal-image-area').boundingBox();
    expect(imageAreaWithTransientStrip).toEqual(imageAreaBeforeTransientStrip);
    await page.waitForTimeout(1_000);
    await expect(page.locator('.modal-body')).toHaveClass(/chrome-hidden/);
    await expect(page.locator('.modal-body')).not.toHaveClass(/cursor-hidden/);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveClass(/is-overlay/);

    await page.mouse.move(dialogBox.x + dialogBox.width / 2, dialogBox.y + dialogBox.height / 2);
    await expect(page.locator('.modal-filmstrip-shell')).toHaveCount(0);

    expect(consoleProblems).toEqual([]);
  });
});

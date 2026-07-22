import { expect, test, type Page } from '@playwright/test';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
  'base64',
);

type WarmupObservation = {
  priority: string;
  seenAt: number;
};

// Sidebar reflow can land on a fractional CSS pixel at different viewport widths.
// Two CSS pixels is visually stationary while still catching row/column jumps.
const MAX_ANCHOR_DRIFT_PX = 2;

async function openFixtureFolder(page: Page, fixtureDir: string) {
  await page.goto('/');
  await page.evaluate(() => {
    const markViewerReady = () => {
      if (!document.querySelector('.viewer')) return false;
      (window as Window & { __pvViewerFirstSeenAt?: number }).__pvViewerFirstSeenAt = Date.now();
      return true;
    };
    if (markViewerReady()) return;
    const observer = new MutationObserver(() => {
      if (!markViewerReady()) return;
      observer.disconnect();
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });

  await page.getByPlaceholder('Paste one absolute path per line...').fill(fixtureDir);
  await page.getByRole('button', { name: 'Add pasted' }).click();
  await page.getByRole('button', { name: 'Open folder set', exact: true }).click();

  await expect(page.locator('.viewer')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.viewer-header .header-stats')).toContainText('96', { timeout: 15_000 });
  await expect(page.locator('.image-card[role="group"]').first()).toBeVisible();
}

test.describe('viewer grid zoom runtime contract', () => {
  let fixtureDir = '';

  test.beforeEach(async ({ page }) => {
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
  });

  test.beforeAll(async () => {
    fixtureDir = await mkdtemp(join(tmpdir(), 'photoviewer-grid-zoom-e2e-'));
    await Promise.all(Array.from({ length: 96 }, (_, index) => (
      writeFile(join(fixtureDir, `fixture-${String(index).padStart(3, '0')}.png`), PNG_1X1)
    )));
  });

  test.afterAll(async ({ request }) => {
    await expect.poll(async () => {
      const response = await request.get('/api/thumbs/warm');
      if (!response.ok()) return false;
      const payload = await response.json() as {
        warmup?: {
          running?: boolean;
          activeThumbJobs?: number;
          pendingThumbs?: number;
          queuedThumbJobs?: number;
        };
      };
      const warmup = payload.warmup;
      return Boolean(warmup) &&
        warmup?.running === false &&
        warmup.activeThumbJobs === 0 &&
        warmup.pendingThumbs === 0 &&
        warmup.queuedThumbJobs === 0;
    }, { timeout: 15_000 }).toBe(true);

    const resolvedFixture = resolve(fixtureDir);
    const resolvedTemp = resolve(tmpdir());
    if (!resolvedFixture.startsWith(`${resolvedTemp}\\photoviewer-grid-zoom-e2e-`)) {
      throw new Error(`Refusing to remove unexpected fixture path: ${resolvedFixture}`);
    }
    await rm(resolvedFixture, { recursive: true, force: true });
  });

  test('prioritizes visible thumbnails and keeps the selected card plus fixed sidebar anchored through Ctrl+Plus', async ({ page }) => {
    const warmups: WarmupObservation[] = [];
    page.on('request', (request) => {
      if (!request.url().includes('/api/thumbs/warm') || request.method() !== 'POST') return;
      try {
        const body = request.postDataJSON() as { priority?: unknown };
        warmups.push({
          priority: typeof body.priority === 'string' ? body.priority : '',
          seenAt: Date.now(),
        });
      } catch {
        warmups.push({ priority: '', seenAt: Date.now() });
      }
    });

    await openFixtureFolder(page, fixtureDir);

    await expect.poll(() => warmups.some((item) => item.priority === 'visible')).toBe(true);
    await expect.poll(() => warmups.some((item) => item.priority === 'nearby')).toBe(true);

    const firstVisible = warmups.findIndex((item) => item.priority === 'visible');
    const firstNearby = warmups.findIndex((item) => item.priority === 'nearby');
    expect(firstVisible).toBeGreaterThanOrEqual(0);
    expect(firstNearby).toBeGreaterThanOrEqual(0);
    expect(firstVisible).toBeLessThan(firstNearby);

    const viewerFirstSeenAt = await page.evaluate(() => (
      (window as Window & { __pvViewerFirstSeenAt?: number }).__pvViewerFirstSeenAt ?? 0
    ));
    expect(viewerFirstSeenAt).toBeGreaterThan(0);
    expect(warmups[firstVisible].seenAt - viewerFirstSeenAt).toBeLessThan(1_000);

    const visibleThumb = page.locator('.image-card img').first();
    await expect(visibleThumb).toHaveAttribute('loading', 'eager');
    await expect(visibleThumb).toHaveAttribute('fetchpriority', 'high');
    await expect(visibleThumb).toHaveAttribute('src', /[?&]priority=visible(?:&|$)/);

    const viewerMain = page.locator('.viewer-main');
    await viewerMain.evaluate((element) => {
      element.scrollTop = 1_200;
      element.dispatchEvent(new Event('scroll'));
    });
    await expect.poll(() => viewerMain.evaluate((element) => element.scrollTop)).toBeGreaterThan(1_000);

    const visibleCards = page.locator('.image-card[role="group"]');
    await expect.poll(() => visibleCards.count()).toBeGreaterThan(3);
    const selectedCard = visibleCards.nth(2);
    const selectedGridIndex = await selectedCard.getAttribute('data-grid-index');
    expect(selectedGridIndex).not.toBeNull();
    await selectedCard.locator('[data-image-primary="true"]').click();

    const stableSelectedCard = page.locator(`.image-card[data-grid-index="${selectedGridIndex}"]`);
    await expect(stableSelectedCard).toHaveClass(/is-selected/);
    const selectedPrimary = stableSelectedCard.locator('[data-image-primary="true"]');
    await expect(selectedPrimary).toHaveAttribute('aria-pressed', 'true');

    const sidebar = page.locator('.sidebar');
    const slider = page.getByRole('slider', { name: 'Thumbnail size' });
    const before = await page.evaluate(() => {
      const sidebarElement = document.querySelector<HTMLElement>('.sidebar');
      const selectedElement = document.querySelector<HTMLElement>('.image-card.is-selected');
      if (!sidebarElement || !selectedElement) throw new Error('Expected viewer zoom surfaces');
      const sidebarRect = sidebarElement.getBoundingClientRect();
      const selectedRect = selectedElement.getBoundingClientRect();
      const sidebarStyle = getComputedStyle(sidebarElement);
      return {
        innerWidth: window.innerWidth,
        devicePixelRatio: window.devicePixelRatio,
        sidebar: {
          left: sidebarRect.left,
          top: sidebarRect.top,
          width: sidebarRect.width,
          height: sidebarRect.height,
          fontSize: sidebarStyle.fontSize,
        },
        selectedTop: selectedRect.top,
      };
    });
    const previousThumbSize = Number(await slider.inputValue());

    await selectedPrimary.focus();
    await page.keyboard.press('Control+=');
    await expect(slider).toHaveValue(String(previousThumbSize + 20));
    await expect(stableSelectedCard).toHaveClass(/is-selected/);

    await expect.poll(async () => {
      const selectedTop = await stableSelectedCard.evaluate((element) => element.getBoundingClientRect().top);
      return Math.abs(selectedTop - before.selectedTop);
    }).toBeLessThanOrEqual(MAX_ANCHOR_DRIFT_PX);

    const after = await page.evaluate(() => {
      const sidebarElement = document.querySelector<HTMLElement>('.sidebar');
      if (!sidebarElement) throw new Error('Expected fixed sidebar');
      const rect = sidebarElement.getBoundingClientRect();
      const style = getComputedStyle(sidebarElement);
      return {
        innerWidth: window.innerWidth,
        devicePixelRatio: window.devicePixelRatio,
        sidebar: {
          left: rect.left,
          top: rect.top,
          width: rect.width,
          height: rect.height,
          fontSize: style.fontSize,
        },
      };
    });

    expect(after).toEqual({
      innerWidth: before.innerWidth,
      devicePixelRatio: before.devicePixelRatio,
      sidebar: before.sidebar,
    });
    await expect(sidebar).toBeVisible();
  });

  test('keeps the selected card anchored through sidebar collapse and exposes dense-to-one-column endpoints', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await openFixtureFolder(page, fixtureDir);

    const viewerMain = page.locator('.viewer-main');
    await viewerMain.evaluate((element) => {
      element.scrollTop = 1_800;
      element.dispatchEvent(new Event('scroll'));
    });
    await expect.poll(() => viewerMain.evaluate((element) => element.scrollTop)).toBeGreaterThan(1_500);

    const visibleCards = page.locator('.image-card[role="group"]');
    await expect.poll(() => visibleCards.count()).toBeGreaterThan(3);
    const selectedCard = visibleCards.nth(2);
    const selectedGridIndex = await selectedCard.getAttribute('data-grid-index');
    expect(selectedGridIndex).not.toBeNull();
    await selectedCard.locator('[data-image-primary="true"]').click();

    const stableSelectedCard = page.locator(`.image-card[data-grid-index="${selectedGridIndex}"]`);
    const selectedTop = () => stableSelectedCard.evaluate((element) => element.getBoundingClientRect().top);
    const beforeCollapseTop = await selectedTop();
    const sliderBeforeCollapse = page.getByRole('slider', { name: 'Thumbnail size' });
    const previousThumbSize = Number(await sliderBeforeCollapse.inputValue());

    await page.getByTitle('Hide sidebar').click();
    await expect(page.locator('.sidebar')).toHaveCount(0);
    await expect.poll(async () => Math.abs((await selectedTop()) - beforeCollapseTop))
      .toBeLessThanOrEqual(MAX_ANCHOR_DRIFT_PX);

    const collapsedTop = await selectedTop();
    await stableSelectedCard.locator('[data-image-primary="true"]').focus();
    await page.keyboard.press('Control+=');
    await expect.poll(async () => Math.abs((await selectedTop()) - collapsedTop))
      .toBeLessThanOrEqual(MAX_ANCHOR_DRIFT_PX);

    const beforeExpandTop = await selectedTop();
    await page.getByTitle('Show sidebar').click();
    const slider = page.getByRole('slider', { name: 'Thumbnail size' });
    await expect(slider).toBeVisible();
    await expect(slider).toHaveValue(String(Math.min(600, previousThumbSize + 20)));
    await expect.poll(async () => Math.abs((await selectedTop()) - beforeExpandTop))
      .toBeLessThanOrEqual(MAX_ANCHOR_DRIFT_PX);

    await slider.fill('40');
    await expect(slider).toHaveValue('40');
    await expect.poll(async () => stableSelectedCard.evaluate(() => {
      const lefts = Array.from(document.querySelectorAll<HTMLElement>('.image-card[role="group"]'))
        .map((element) => Math.round(element.getBoundingClientRect().left));
      return new Set(lefts).size;
    })).toBeGreaterThan(1);
    const oldMinimumColumns = await stableSelectedCard.evaluate(() => {
      const lefts = Array.from(document.querySelectorAll<HTMLElement>('.image-card[role="group"]'))
        .map((element) => Math.round(element.getBoundingClientRect().left));
      return new Set(lefts).size;
    });

    await slider.fill('20');
    await expect(slider).toHaveValue('20');
    await expect.poll(async () => stableSelectedCard.evaluate((_, previousColumns) => {
      const lefts = Array.from(document.querySelectorAll<HTMLElement>('.image-card[role="group"]'))
        .map((element) => Math.round(element.getBoundingClientRect().left));
      return new Set(lefts).size > Number(previousColumns);
    }, oldMinimumColumns)).toBe(true);

    await slider.fill('600');
    await expect(slider).toHaveValue('600');
    await expect(page.getByText('1 column')).toBeVisible();
    await expect.poll(async () => page.locator('.image-card[role="group"]').evaluateAll((elements) => (
      new Set(elements.map((element) => Math.round(element.getBoundingClientRect().left))).size
    ))).toBe(1);
  });

  test('renders the selected source image in the full-screen modal', async ({ page }) => {
    await openFixtureFolder(page, fixtureDir);

    const primaryImages = page.locator('[data-image-primary="true"]');
    await expect.poll(() => primaryImages.count()).toBeGreaterThan(0);
    const firstPrimary = primaryImages.first();
    await firstPrimary.dblclick();

    await expect(page.getByRole('dialog', { name: /Image preview:/ })).toBeVisible();
    const modalImage = page.locator('.modal-full-image');
    await expect(modalImage).toBeVisible();
    await expect.poll(() => modalImage.evaluate((element) => {
      const image = element as HTMLImageElement;
      const rect = image.getBoundingClientRect();
      return image.complete && image.naturalWidth > 0 && rect.width > 0 && rect.height > 0;
    })).toBe(true);
  });
});

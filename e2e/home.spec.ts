import { test, expect } from '@playwright/test';

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
});

test('landing page exposes the Aibos folder workflow', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Aibos');
  await expect(page.getByRole('button', { name: 'Add folder' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open folder set' })).toBeDisabled();
  await expect(page.getByPlaceholder('Paste one absolute path per line...')).toBeVisible();
});

test('landing page restores the last folder set from local storage', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => {
      const folder = String.raw`C:\PV Fixtures\Pictures`;
    localStorage.setItem('pvu_last_dir_set', folder);
    localStorage.setItem('pvu_recent_dirs', JSON.stringify([folder]));
  });
  await expect(page.evaluate(() => localStorage.getItem('pvu_last_dir_set'))).resolves.toContain('Pictures');
  await page.reload();
  await expect(page.evaluate(() => localStorage.getItem('pvu_last_dir_set'))).resolves.toContain('Pictures');

  const lastFolderButton = page.getByRole('button', { name: /Open last folder set/ });
  await expect(lastFolderButton).toBeVisible();
  await expect(lastFolderButton).toContainText('Pictures');
  await expect(page.getByText('Recent folder sets')).toBeVisible();
});

test('landing Settings exposes safe runtime identity without mobile overflow', async ({ page }) => {
  const fullRevision = '1234567890abcdef1234567890abcdef12345678';
  let runtimeRequests = 0;
  await page.route('**/api/runtime', async (route) => {
    runtimeRequests += 1;
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        product: 'Aibos Image',
        sourceRevision: fullRevision,
        sourceDirty: true,
        buildId: 'build_2026-07-18_mobile-overflow-proof',
        buildCompletedAtUtc: '2026-07-18T01:02:03.000Z',
        serverHost: '127.0.0.1',
        serverPort: 3125,
        serverStartedAtUtc: '2026-07-18T01:03:00.000Z',
        processId: 4321,
        projectRoot: 'C:/Users/private/project',
      }),
    });
  });

  await page.goto('/');
  expect(runtimeRequests).toBe(0);
  const opener = page.getByRole('button', { name: 'Open settings and runtime version' });
  await opener.click();

  await expect(page.getByRole('dialog', { name: 'Settings' })).toBeVisible();
  await expect(page.getByText('1234567890')).toHaveAttribute('title', fullRevision);
  await expect(page.getByText('Dirty')).toBeVisible();
  await expect(page.getByText('127.0.0.1:3125')).toBeVisible();
  expect(runtimeRequests).toBeGreaterThanOrEqual(1);
  const requestsAfterReady = runtimeRequests;
  await page.waitForTimeout(100);
  expect(runtimeRequests).toBe(requestsAfterReady);

  await expect(page.getByRole('button', { name: 'Close settings' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Reload' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Copy diagnostics' })).toBeFocused();

  await page.setViewportSize({ width: 320, height: 720 });
  const hasHorizontalOverflow = await page.locator('.settings-panel').evaluate((panel) => (
    panel.scrollWidth > panel.clientWidth
    || Array.from(panel.querySelectorAll<HTMLElement>('.settings-runtime, .settings-runtime-row, .settings-runtime-row dd'))
      .some((element) => element.scrollWidth > element.clientWidth)
  ));
  expect(hasHorizontalOverflow).toBe(false);

  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Settings' })).toBeHidden();
  await expect(opener).toBeFocused();
});

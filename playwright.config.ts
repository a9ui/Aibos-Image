import { defineConfig, devices } from '@playwright/test';
import { resolvePlaywrightTarget } from './scripts/playwright_config';

const isCI = !!process.env.CI;
const target = resolvePlaywrightTarget(process.env);

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: isCI ? 2 : 0,
  reporter: [['html', { open: 'never' }], ['list']],
  use: {
    baseURL: target.baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure'
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ],
  webServer: target.startServer ? {
    command: `corepack pnpm dev --hostname 127.0.0.1 --port ${target.port}`,
    url: target.baseURL,
    reuseExistingServer: target.reuseExistingServer,
    timeout: 120_000
  } : undefined
});

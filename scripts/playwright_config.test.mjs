import { createRequire } from 'node:module';

import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const {
  DEFAULT_PLAYWRIGHT_PORT,
  parsePlaywrightPort,
  resolvePlaywrightTarget,
} = require('./playwright_config.js');

describe('Playwright target safety', () => {
  it('uses a dedicated loopback port and refuses implicit server reuse', () => {
    expect(DEFAULT_PLAYWRIGHT_PORT).toBe(3125);
    expect(resolvePlaywrightTarget({})).toEqual({
      port: 3125,
      baseURL: 'http://127.0.0.1:3125',
      startServer: true,
      reuseExistingServer: false,
    });
  });

  it('accepts an explicit isolated port without enabling reuse', () => {
    expect(resolvePlaywrightTarget({ PLAYWRIGHT_PORT: '43125' })).toMatchObject({
      port: 43125,
      baseURL: 'http://127.0.0.1:43125',
      startServer: true,
      reuseExistingServer: false,
    });
  });

  it('requires an explicit opt-in before reusing an existing listener', () => {
    expect(resolvePlaywrightTarget({
      PLAYWRIGHT_PORT: '3000',
      PLAYWRIGHT_REUSE_EXISTING_SERVER: '1',
    })).toMatchObject({
      port: 3000,
      reuseExistingServer: true,
    });
  });

  it('uses an explicit base URL without starting a local server', () => {
    expect(resolvePlaywrightTarget({ PLAYWRIGHT_BASE_URL: 'http://127.0.0.1:4555/' })).toEqual({
      port: 3125,
      baseURL: 'http://127.0.0.1:4555/',
      startServer: false,
      reuseExistingServer: false,
    });
  });

  it.each(['0', '-1', '65536', '3.5', 'abc', ' 3125 '])('rejects invalid port %s', (value) => {
    expect(() => parsePlaywrightPort(value)).toThrow('PLAYWRIGHT_PORT');
  });
});

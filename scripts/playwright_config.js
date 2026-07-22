const DEFAULT_PLAYWRIGHT_PORT = 3125;

function parsePlaywrightPort(value) {
  if (value === undefined || value === null || value === '') return DEFAULT_PLAYWRIGHT_PORT;
  if (typeof value !== 'string' || !/^[1-9]\d{0,4}$/.test(value)) {
    throw new Error('PLAYWRIGHT_PORT must be an integer from 1 to 65535.');
  }
  const port = Number(value);
  if (port > 65_535) {
    throw new Error('PLAYWRIGHT_PORT must be an integer from 1 to 65535.');
  }
  return port;
}

function resolvePlaywrightTarget(environment = {}) {
  const port = parsePlaywrightPort(environment.PLAYWRIGHT_PORT);
  const explicitBaseURL = typeof environment.PLAYWRIGHT_BASE_URL === 'string'
    ? environment.PLAYWRIGHT_BASE_URL.trim()
    : '';

  return {
    port,
    baseURL: explicitBaseURL || `http://127.0.0.1:${port}`,
    startServer: explicitBaseURL.length === 0,
    // Reusing an arbitrary listener is opt-in. The default must never attach
    // E2E state to the user's normal PhotoViewer instance on port 3000.
    reuseExistingServer: environment.PLAYWRIGHT_REUSE_EXISTING_SERVER === '1',
  };
}

module.exports = {
  DEFAULT_PLAYWRIGHT_PORT,
  parsePlaywrightPort,
  resolvePlaywrightTarget,
};

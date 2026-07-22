import { afterEach, describe, expect, it } from 'vitest';

import { GET } from './route';

const keys = [
  'PVU_SOURCE_REVISION',
  'PVU_SOURCE_DIRTY',
  'PVU_BUILD_ID',
  'PVU_BUILD_COMPLETED_AT_UTC',
  'PVU_SERVER_HOST',
  'PVU_SERVER_PORT',
  'PVU_SERVER_STARTED_AT_UTC',
] as const;

const original = Object.fromEntries(keys.map((key) => [key, process.env[key]]));

afterEach(() => {
  for (const key of keys) {
    const value = original[key];
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
});

describe('runtime provenance route', () => {
  it('returns launcher-provided build identity without exposing the project path', async () => {
    process.env.PVU_SOURCE_REVISION = 'abc123';
    process.env.PVU_SOURCE_DIRTY = '0';
    process.env.PVU_BUILD_ID = 'build-42';
    process.env.PVU_BUILD_COMPLETED_AT_UTC = '2026-07-18T00:00:00.000Z';
    process.env.PVU_SERVER_HOST = '127.0.0.1';
    process.env.PVU_SERVER_PORT = '3011';
    process.env.PVU_SERVER_STARTED_AT_UTC = '2026-07-18T00:01:00.000Z';

    const response = GET();
    const body = await response.json();

    expect(response.headers.get('cache-control')).toBe('no-store, max-age=0');
    expect(body).toMatchObject({
      product: 'Aibos Image',
      sourceRevision: 'abc123',
      sourceDirty: false,
      buildId: 'build-42',
      buildCompletedAtUtc: '2026-07-18T00:00:00.000Z',
      serverHost: '127.0.0.1',
      serverPort: 3011,
      serverStartedAtUtc: '2026-07-18T00:01:00.000Z',
    });
    expect(body).not.toHaveProperty('projectRoot');
    expect(body.processId).toBeTypeOf('number');
  });

  it('uses explicit nulls when the launcher provenance is unavailable', async () => {
    for (const key of keys) delete process.env[key];

    const body = await GET().json();

    expect(body).toMatchObject({
      sourceRevision: null,
      sourceDirty: null,
      buildId: null,
      buildCompletedAtUtc: null,
      serverHost: null,
      serverPort: null,
      serverStartedAtUtc: null,
    });
  });

  it.each([
    ['1', true],
    ['0', false],
    ['true', null],
    ['', null],
  ])('maps PVU_SOURCE_DIRTY=%j to %j', async (value, expected) => {
    process.env.PVU_SOURCE_DIRTY = value;

    expect(await GET().json()).toMatchObject({ sourceDirty: expected });
  });
});

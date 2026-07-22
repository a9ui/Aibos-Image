import { describe, expect, it } from 'vitest';

import { guardLocalApiRequest } from './localApiGuard';

function request(url = 'http://127.0.0.1:3001/api/settings', headers: Record<string, string> = {}) {
  return new Request(url, { method: 'PUT', headers });
}

async function expectForbidden(candidate: Request) {
  const response = guardLocalApiRequest(candidate);
  expect(response?.status).toBe(403);
  await expect(response?.json()).resolves.toEqual({
    error: 'Forbidden local API request.',
  });
}

describe('local API request guard', () => {
  it.each([
    ['127.0.0.1', 'http://127.0.0.1:3001/api/settings', '127.0.0.1:3001', 'http://127.0.0.1:3001'],
    ['localhost', 'http://localhost:3001/api/settings', 'localhost:3001', 'http://localhost:3001'],
    ['IPv6 loopback', 'http://[::1]:3001/api/settings', '[::1]:3001', 'http://[::1]:3001'],
  ])('allows a same-origin browser request on %s', (_name, url, host, origin) => {
    expect(
      guardLocalApiRequest(
        request(url, {
          host,
          origin,
          'sec-fetch-site': 'same-origin',
          'sec-fetch-mode': 'cors',
        }),
      ),
    ).toBeNull();
  });

  it('allows a direct loopback client with no Origin or Fetch Metadata', () => {
    expect(guardLocalApiRequest(request())).toBeNull();
  });

  it('allows an Origin-less same-origin browser request', () => {
    expect(
      guardLocalApiRequest(
        request(undefined, {
          host: '127.0.0.1:3001',
          'sec-fetch-site': 'same-origin',
          'sec-fetch-mode': 'cors',
        }),
      ),
    ).toBeNull();
  });

  it.each([
    ['foreign hostname', 'evil.example:3001'],
    ['DNS-rebinding hostname', '127.0.0.1.attacker.example:3001'],
    ['userinfo authority confusion', 'evil.example@127.0.0.1:3001'],
  ])('rejects a Host header with %s', async (_name, host) => {
    await expectForbidden(request(undefined, { host }));
  });

  it.each([
    ['foreign Origin', 'https://evil.example'],
    ['loopback hostname mismatch', 'http://localhost:3001'],
    ['loopback port mismatch', 'http://127.0.0.1:3002'],
    ['opaque Origin', 'null'],
  ])('rejects %s', async (_name, origin) => {
    await expectForbidden(
      request(undefined, {
        host: '127.0.0.1:3001',
        origin,
        'sec-fetch-site': 'same-origin',
      }),
    );
  });

  it.each(['cross-site', 'same-site', 'none'])('rejects Sec-Fetch-Site: %s', async (site) => {
    await expectForbidden(
      request(undefined, {
        host: '127.0.0.1:3001',
        'sec-fetch-site': site,
      }),
    );
  });

  it('allows a same-origin no-cors GET image subresource request', () => {
    const candidate = new Request(
      'http://127.0.0.1:3001/api/image?path=test.png',
      {
        method: 'GET',
        headers: {
          host: '127.0.0.1:3001',
          'sec-fetch-site': 'same-origin',
          'sec-fetch-mode': 'no-cors',
        },
      },
    );

    expect(guardLocalApiRequest(candidate)).toBeNull();
  });

  it('rejects a no-cors mutation even when its other headers claim same-origin', async () => {
    await expectForbidden(
      request(undefined, {
        host: '127.0.0.1:3001',
        origin: 'http://127.0.0.1:3001',
        'sec-fetch-site': 'same-origin',
        'sec-fetch-mode': 'no-cors',
      }),
    );
  });

  it('rejects an origin-less no-cors request without same-origin Fetch Metadata', async () => {
    await expectForbidden(new Request('http://127.0.0.1:3001/api/image?path=test.png', {
      method: 'GET',
      headers: {
        host: '127.0.0.1:3001',
        'sec-fetch-mode': 'no-cors',
      },
    }));
  });
});

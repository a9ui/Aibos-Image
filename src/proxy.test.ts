import { NextRequest } from 'next/server';
import { describe, expect, it } from 'vitest';

import { proxy } from './proxy';

describe('local API proxy guard', () => {
  it('continues a same-origin loopback API request', () => {
    const response = proxy(
      new NextRequest('http://127.0.0.1:3001/api/runtime', {
        headers: {
          host: '127.0.0.1:3001',
          origin: 'http://127.0.0.1:3001',
          'sec-fetch-site': 'same-origin',
        },
      }),
    );

    expect(response.status).toBe(200);
    expect(response.headers.get('x-middleware-next')).toBe('1');
  });

  it('stops a DNS-rebinding Host before an API route runs', async () => {
    const response = proxy(
      new NextRequest('http://127.0.0.1:3001/api/runtime', {
        headers: { host: 'photo.attacker.example:3001' },
      }),
    );

    expect(response.status).toBe(403);
    await expect(response.json()).resolves.toEqual({
      error: 'Forbidden local API request.',
    });
  });
});

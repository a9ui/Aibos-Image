import { NextRequest } from 'next/server';
import { afterEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  hasIndexSession: vi.fn(),
  searchIndex: vi.fn(),
}));

vi.mock('@/lib/indexer', () => ({
  hasIndexSession: mocks.hasIndexSession,
  searchIndex: mocks.searchIndex,
}));

import { GET } from './route';

afterEach(() => {
  vi.clearAllMocks();
});

describe('GET /api/search viewer session routing', () => {
  it('returns a recoverable error for an expired explicit session token', async () => {
    mocks.hasIndexSession.mockReturnValue(false);
    const response = await GET(new NextRequest('http://127.0.0.1/api/search?indexToken=idx_expired'));

    expect(response.status).toBe(410);
    await expect(response.json()).resolves.toMatchObject({ error: expect.stringContaining('expired') });
    expect(mocks.searchIndex).not.toHaveBeenCalled();
  });

  it('uses an explicit token while preserving tokenless compatibility', async () => {
    mocks.hasIndexSession.mockReturnValue(true);
    mocks.searchIndex.mockReturnValue({ results: [], total: 0, page: 0, totalPages: 1 });
    const response = await GET(new NextRequest('http://127.0.0.1/api/search?dir=C%3A%2Fset-a&indexToken=idx_set_a'));

    expect(response.status).toBe(200);
    expect(mocks.searchIndex).toHaveBeenCalledWith(
      '', 0, 100, 'newest', undefined, undefined, undefined, 'C:/set-a', undefined, undefined, 'idx_set_a'
    );
  });
});

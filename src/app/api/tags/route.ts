import { NextRequest, NextResponse } from 'next/server';
import { getTags } from '@/lib/indexer';

export const dynamic = 'force-dynamic';

/**
 * GET /api/tags
 *
 * Returns autocomplete tags extracted from the in-memory index.
 */
export async function GET(request: NextRequest) {
  const indexToken = request.nextUrl.searchParams.get('indexToken') || undefined;
  const tags = getTags(2000, indexToken);
  return NextResponse.json({ tags });
}

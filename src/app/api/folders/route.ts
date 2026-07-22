import { NextRequest, NextResponse } from 'next/server';
import { getFolderBuckets } from '@/lib/indexer';

export const dynamic = 'force-dynamic';

/**
 * GET /api/folders?dir=ABSOLUTE_PATH
 *
 * Returns first-level folder buckets under the active root directory.
 */
export async function GET(request: NextRequest) {
  const dir = request.nextUrl.searchParams.get('dir');
  const indexToken = request.nextUrl.searchParams.get('indexToken') || undefined;
  if (!dir) {
    return NextResponse.json({ folders: [] });
  }

  const folders = getFolderBuckets(dir, 200, indexToken);
  return NextResponse.json({ folders });
}

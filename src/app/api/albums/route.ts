import { NextResponse } from 'next/server';

import { readAlbums } from '@/lib/albums';

import { albumsPath, optionalExpectedRevision, readObjectBody, runAlbumMutation } from './routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET() {
  const result = await readAlbums(albumsPath());
  return NextResponse.json(result, { status: result.ok ? 200 : 409 });
}

export async function POST(request: Request) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  return runAlbumMutation({
    action: 'create',
    name: parsed.body.name as string,
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

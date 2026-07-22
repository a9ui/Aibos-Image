import { NextResponse } from 'next/server';

import { mutateAlbums, type AlbumMutation, type AlbumMutationResult } from '@/lib/albums';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { resolveSharedCachePath } from '@/lib/sharedProjectRoot';

export function albumsPath() {
  return resolveSharedCachePath('albums.json', process.env.PVU_ALBUMS_PATH);
}

export async function readObjectBody(request: Request) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return { ok: false as const, response: forbidden };

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return { ok: false as const, response: NextResponse.json({ ok: false, error: 'Request body must be valid JSON.' }, { status: 400 }) };
  }
  if (!body || typeof body !== 'object' || Array.isArray(body)) {
    return { ok: false as const, response: NextResponse.json({ ok: false, error: 'Request body must be an object.' }, { status: 400 }) };
  }
  return { ok: true as const, body: body as Record<string, unknown> };
}

export function mutationResponse(result: AlbumMutationResult) {
  const status = result.ok ? 200
    : result.conflict || result.malformed || result.futureVersion ? 409
      : result.notFound ? 404
        : 400;
  return NextResponse.json(result, { status });
}

export async function runAlbumMutation(mutation: AlbumMutation) {
  try {
    return mutationResponse(await mutateAlbums(albumsPath(), mutation));
  } catch (error) {
    return NextResponse.json({
      ok: false,
      changed: false,
      error: error instanceof Error ? error.message : String(error),
    }, { status: 503 });
  }
}

export function optionalExpectedRevision(value: unknown) {
  return value === undefined ? undefined : value as number;
}

import { NextResponse } from 'next/server';

import { buildAlbumSource } from '@/lib/albumSource';
import { readAlbums } from '@/lib/albums';

import { albumsPath, readObjectBody } from '../../routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const catalogIndexToken = parsed.body.catalogIndexToken;
  const expectedRevision = parsed.body.expectedRevision;
  if (catalogIndexToken !== undefined && typeof catalogIndexToken !== 'string') {
    return NextResponse.json({ ok: false, error: 'catalogIndexToken must be a string.' }, { status: 400 });
  }
  if (expectedRevision !== undefined && (!Number.isSafeInteger(expectedRevision) || (expectedRevision as number) < 0)) {
    return NextResponse.json({ ok: false, error: 'expectedRevision must be a non-negative safe integer.' }, { status: 400 });
  }

  const current = await readAlbums(albumsPath());
  if (!current.ok) {
    return NextResponse.json(current, { status: 409 });
  }
  if (expectedRevision !== undefined && expectedRevision !== current.document.revision) {
    return NextResponse.json({
      ok: false,
      conflict: true,
      documentRevision: current.document.revision,
      error: `Album revision conflict: expected ${expectedRevision}, current ${current.document.revision}.`,
    }, { status: 409 });
  }
  const { id } = await params;
  const source = await buildAlbumSource(
    current.document,
    id,
    typeof catalogIndexToken === 'string' ? catalogIndexToken : undefined,
  );
  if (!source) {
    return NextResponse.json({ ok: false, error: 'Album not found.' }, { status: 404 });
  }
  return NextResponse.json({ ok: true, source });
}

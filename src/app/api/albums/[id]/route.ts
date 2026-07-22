import { optionalExpectedRevision, readObjectBody, runAlbumMutation } from '../routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

type RouteContext = { params: Promise<{ id: string }> };

export async function PATCH(request: Request, { params }: RouteContext) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const { id } = await params;
  return runAlbumMutation({
    action: 'update',
    albumId: id,
    name: parsed.body.name as string | undefined,
    pinned: parsed.body.pinned as boolean | undefined,
    coverMemberId: parsed.body.coverMemberId as string | null | undefined,
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

export async function DELETE(request: Request, { params }: RouteContext) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const { id } = await params;
  return runAlbumMutation({
    action: 'delete',
    albumId: id,
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

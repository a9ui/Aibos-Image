import { optionalExpectedRevision, readObjectBody, runAlbumMutation } from '../../routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

type RouteContext = { params: Promise<{ id: string }> };

export async function POST(request: Request, { params }: RouteContext) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const { id } = await params;
  return runAlbumMutation({
    action: 'add',
    albumId: id,
    paths: parsed.body.paths as string[],
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

export async function DELETE(request: Request, { params }: RouteContext) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const { id } = await params;
  return runAlbumMutation({
    action: 'remove',
    albumId: id,
    memberIds: parsed.body.memberIds as string[] | undefined,
    paths: parsed.body.paths as string[] | undefined,
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

import { optionalExpectedRevision, readObjectBody, runAlbumMutation } from '../../routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  const { id } = await params;
  return runAlbumMutation({
    action: 'recent',
    albumId: id,
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

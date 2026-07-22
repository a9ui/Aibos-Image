import { runAlbumMutation, optionalExpectedRevision, readObjectBody } from '../../routeHelpers';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(request: Request) {
  const parsed = await readObjectBody(request);
  if (!parsed.ok) return parsed.response;
  return runAlbumMutation({
    action: 'cleanupPaths',
    paths: parsed.body.paths as string[],
    expectedRevision: optionalExpectedRevision(parsed.body.expectedRevision),
  });
}

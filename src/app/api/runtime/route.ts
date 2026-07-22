export const dynamic = 'force-dynamic';

function readSourceDirty(): boolean | null {
  if (process.env.PVU_SOURCE_DIRTY === '1') return true;
  if (process.env.PVU_SOURCE_DIRTY === '0') return false;
  return null;
}

export function GET() {
  return Response.json(
    {
      product: 'Aibos Image',
      sourceRevision: process.env.PVU_SOURCE_REVISION || null,
      sourceDirty: readSourceDirty(),
      buildId: process.env.PVU_BUILD_ID || null,
      buildCompletedAtUtc: process.env.PVU_BUILD_COMPLETED_AT_UTC || null,
      serverHost: process.env.PVU_SERVER_HOST || null,
      serverPort: Number(process.env.PVU_SERVER_PORT || 0) || null,
      serverStartedAtUtc: process.env.PVU_SERVER_STARTED_AT_UTC || null,
      processId: process.pid,
    },
    {
      headers: {
        'Cache-Control': 'no-store, max-age=0',
      },
    },
  );
}

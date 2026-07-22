import fs from 'fs';
import { NextResponse } from 'next/server';
import { getEnhancementJobStore } from '@/lib/enhance/jobStore';
import { startEnhancementQueue } from '@/lib/enhance/queue';
import { guardLocalApiRequest } from '@/lib/localApiGuard';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  const { id } = await params;
  const store = getEnhancementJobStore();
  const original = await store.getJob(id);
  if (!original) {
    return NextResponse.json({ error: 'Job not found' }, { status: 404 });
  }
  if (original.status !== 'failed' && original.status !== 'canceled') {
    return NextResponse.json({ error: 'Only failed or canceled jobs can be retried' }, { status: 409 });
  }

  let stat: fs.Stats;
  try {
    stat = await fs.promises.stat(original.sourcePath);
  } catch {
    return NextResponse.json({ error: 'Source image no longer exists' }, { status: 404 });
  }
  if (stat.size !== original.sourceSignature.size || stat.mtimeMs !== original.sourceSignature.mtimeMs) {
    return NextResponse.json(
      {
        error: 'Source image changed since the original job. Create a new enhancement job so current size and backend guards are checked.',
        code: 'SOURCE_CHANGED_RECREATE_JOB',
      },
      { status: 409 }
    );
  }

  const job = await store.createJob({
    sourceId: original.sourceId,
    sourcePath: original.sourcePath,
    sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    preset: original.preset,
    adapterId: original.adapterId,
  });
  startEnhancementQueue();

  return NextResponse.json({ job }, { status: 202 });
}

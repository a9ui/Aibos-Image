import { NextResponse } from 'next/server';
import { requestComfyUiInterrupt } from '@/lib/enhance/adapters/comfyUiClient';
import { requestNcnnVulkanCancel } from '@/lib/enhance/adapters/ncnnProcessRegistry';
import { getEnhancementJobStore } from '@/lib/enhance/jobStore';
import { guardLocalApiRequest } from '@/lib/localApiGuard';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(request: Request, { params }: { params: Promise<{ id: string }> }) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  const { id } = await params;
  const job = await getEnhancementJobStore().requestCancel(id);
  if (!job) {
    return NextResponse.json({ error: 'Job not found' }, { status: 404 });
  }
  let interruptWarning = '';
  if (job.adapterId === 'comfyui' && job.status === 'running') {
    try {
      await requestComfyUiInterrupt(job.externalPromptId);
    } catch (error) {
      interruptWarning = error instanceof Error ? error.message : String(error);
    }
  }
  if (job.adapterId === 'realesrgan-ncnn' && job.status === 'running') {
    const requested = requestNcnnVulkanCancel(job.id, job.runId);
    if (!requested) {
      interruptWarning = 'No active Real-ESRGAN process was found for this job; it may have already finished or stopped.';
    }
  }
  return NextResponse.json({ job, interruptWarning });
}

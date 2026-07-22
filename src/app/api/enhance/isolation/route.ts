import { NextResponse } from 'next/server';
import { getEnhancementIsolationMetrics } from '@/lib/enhance/isolationMetrics';
import { isEnhancementQueueRunning } from '@/lib/enhance/queue';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET() {
  return NextResponse.json({
    metrics: getEnhancementIsolationMetrics(),
    queueRunning: isEnhancementQueueRunning(),
  });
}

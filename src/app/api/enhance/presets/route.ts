import { NextResponse } from 'next/server';
import { ENHANCEMENT_PRESETS } from '@/lib/enhance/types';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET() {
  return NextResponse.json({ presets: ENHANCEMENT_PRESETS });
}

import fs from 'fs';
import { NextRequest, NextResponse } from 'next/server';
import sharp from 'sharp';
import { getIndex } from '@/lib/indexer';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { isKnownEnhancementAdapter } from '@/lib/enhance/adapters';
import { getComfyUiConfigErrorMessage, getComfyUiConfigStatus } from '@/lib/enhance/adapters/comfyUiConfig';
import { getNcnnVulkanAvailability } from '@/lib/enhance/adapters/ncnnConfig';
import { getEnhancementJobStore } from '@/lib/enhance/jobStore';
import { startEnhancementQueue } from '@/lib/enhance/queue';
import { ENHANCEMENT_PRESETS, SHARP_TEST_PRESET } from '@/lib/enhance/types';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

const OUTPUT_FORMATS = new Set(['png', 'webp', 'jpg']);
const KNOWN_PRESET_IDS = new Set([
  ...ENHANCEMENT_PRESETS.map((preset) => preset.id),
  SHARP_TEST_PRESET.id,
]);

function roundMP(width?: number, height?: number) {
  if (!width || !height) return undefined;
  return Math.round((width * height / 1_000_000) * 100) / 100;
}

function nativeNcnnScale(requestedScale: number) {
  if (requestedScale <= 2) return 2;
  if (requestedScale <= 3) return 3;
  return 4;
}

function ncnnModelName(modelFamily: string | undefined) {
  return modelFamily === 'anime' ? 'realesr-animevideov3' : 'realesrgan-x4plus';
}

function warningLevelForNcnn(modelName: string, workMP?: number, finalMP?: number) {
  const work = workMP || 0;
  const final = finalMP || 0;
  if (final > 160) return 'blocked';
  if (modelName === 'realesr-animevideov3') {
    if (work > 120) return 'blocked';
    if (work > 80 || final > 120) return 'confirm';
    if (work > 40 || final > 80) return 'slow';
    return 'none';
  }
  if (work > 60) return 'blocked';
  if (work > 40 || final > 120) return 'confirm';
  if (work > 20 || final > 80) return 'slow';
  return 'none';
}

function isNumberInRange(value: unknown, min: number, max: number) {
  return typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max;
}

function parseJobSettings(body: {
  scale?: unknown;
  denoise?: unknown;
  sharpen?: unknown;
  detail?: unknown;
  smoothness?: unknown;
  colorBrightness?: unknown;
  colorContrast?: unknown;
  colorSaturation?: unknown;
  outputFormat?: unknown;
}) {
  const settings: {
    scale?: number;
    denoise?: number;
    sharpen?: number;
    detail?: number;
    smoothness?: number;
    colorBrightness?: number;
    colorContrast?: number;
    colorSaturation?: number;
    outputFormat?: 'png' | 'webp' | 'jpg';
  } = {};

  if (body.scale !== undefined) {
    if (!isNumberInRange(body.scale, 1, 8)) throw new Error('scale must be a number from 1 to 8');
    settings.scale = body.scale as number;
  }
  if (body.denoise !== undefined) {
    if (!isNumberInRange(body.denoise, 0, 100)) throw new Error('denoise must be a number from 0 to 100');
    settings.denoise = body.denoise as number;
  }
  if (body.sharpen !== undefined) {
    if (!isNumberInRange(body.sharpen, 0, 100)) throw new Error('sharpen must be a number from 0 to 100');
    settings.sharpen = body.sharpen as number;
  }
  if (body.detail !== undefined) {
    if (!isNumberInRange(body.detail, 0, 100)) throw new Error('detail must be a number from 0 to 100');
    settings.detail = body.detail as number;
  }
  if (body.smoothness !== undefined) {
    if (!isNumberInRange(body.smoothness, 0, 100)) throw new Error('smoothness must be a number from 0 to 100');
    settings.smoothness = body.smoothness as number;
  }
  if (body.colorBrightness !== undefined) {
    if (!isNumberInRange(body.colorBrightness, -100, 100)) throw new Error('colorBrightness must be a number from -100 to 100');
    settings.colorBrightness = body.colorBrightness as number;
  }
  if (body.colorContrast !== undefined) {
    if (!isNumberInRange(body.colorContrast, -100, 100)) throw new Error('colorContrast must be a number from -100 to 100');
    settings.colorContrast = body.colorContrast as number;
  }
  if (body.colorSaturation !== undefined) {
    if (!isNumberInRange(body.colorSaturation, -100, 100)) throw new Error('colorSaturation must be a number from -100 to 100');
    settings.colorSaturation = body.colorSaturation as number;
  }
  if (body.outputFormat !== undefined) {
    if (typeof body.outputFormat !== 'string' || !OUTPUT_FORMATS.has(body.outputFormat)) {
      throw new Error('outputFormat must be png, webp, or jpg');
    }
    settings.outputFormat = body.outputFormat as 'png' | 'webp' | 'jpg';
  }

  return settings;
}

export async function GET(request: NextRequest) {
  const sourceId = request.nextUrl.searchParams.get('sourceId');
  const store = getEnhancementJobStore();
  const jobs = await store.listJobs();
  return NextResponse.json({
    jobs: sourceId ? jobs.filter((job) => job.sourceId === sourceId) : jobs,
  });
}

export async function POST(request: NextRequest) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  let body: {
    sourceId?: string;
    presetId?: string;
    adapterId?: string;
    scale?: unknown;
    denoise?: unknown;
    sharpen?: unknown;
    detail?: unknown;
    smoothness?: unknown;
    colorBrightness?: unknown;
    colorContrast?: unknown;
    colorSaturation?: unknown;
    outputFormat?: unknown;
    confirmLargeJob?: unknown;
    indexToken?: unknown;
  } = {};
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ error: 'Invalid JSON body' }, { status: 400 });
  }

  if (!body.sourceId) {
    return NextResponse.json({ error: 'Missing sourceId' }, { status: 400 });
  }
  if (typeof body.sourceId !== 'string') {
    return NextResponse.json({ error: 'sourceId must be a string' }, { status: 400 });
  }
  if (body.indexToken !== undefined && typeof body.indexToken !== 'string') {
    return NextResponse.json({ error: 'indexToken must be a string' }, { status: 400 });
  }
  if (body.presetId !== undefined && typeof body.presetId !== 'string') {
    return NextResponse.json({ error: 'presetId must be a string' }, { status: 400 });
  }
  if (body.presetId !== undefined && !KNOWN_PRESET_IDS.has(body.presetId)) {
    // Reject only at the request boundary. The store and queue retain their
    // fallback so persisted jobs from older versions can still be recovered.
    return NextResponse.json({ error: `Unknown enhancement preset: ${body.presetId}` }, { status: 400 });
  }
  if (body.adapterId !== undefined && typeof body.adapterId !== 'string') {
    return NextResponse.json({ error: 'adapterId must be a string' }, { status: 400 });
  }
  if (body.adapterId && !isKnownEnhancementAdapter(body.adapterId)) {
    return NextResponse.json({ error: `Unknown enhancement adapter: ${body.adapterId}` }, { status: 400 });
  }
  if (body.adapterId === 'comfyui') {
    const comfyStatus = getComfyUiConfigStatus();
    if (!comfyStatus.workflowConfigured || !comfyStatus.modelsConfigured) {
      return NextResponse.json(
        { error: getComfyUiConfigErrorMessage(comfyStatus), comfyui: comfyStatus },
        { status: 400 }
      );
    }
  }

  let settings: ReturnType<typeof parseJobSettings>;
  try {
    settings = parseJobSettings(body);
  } catch (error) {
    return NextResponse.json(
      { error: error instanceof Error ? error.message : String(error) },
      { status: 400 }
    );
  }
  const effectiveAdapterId = body.adapterId || 'realesrgan-ncnn';
  if (effectiveAdapterId === 'realesrgan-ncnn') {
    const availability = getNcnnVulkanAvailability();
    if (!availability.available) {
      return NextResponse.json(
        {
          error: 'Real-ESRGAN fast GPU backend is not installed or required model files are missing.',
          code: 'BACKEND_NOT_AVAILABLE',
          diagnostics: availability,
        },
        { status: 503 }
      );
    }
  }
  if (effectiveAdapterId === 'realesrgan-ncnn' && typeof settings.scale === 'number' && settings.scale > 4) {
    return NextResponse.json(
      { error: 'Real-ESRGAN fast GPU supports real AI upscale up to 4x. Choose 2x, 3x, or 4x.' },
      { status: 400 }
    );
  }

  const source = getIndex(body.indexToken).find((image) => image.id.toLowerCase() === body.sourceId?.toLowerCase());
  if (!source) {
    return NextResponse.json({ error: 'Source image is not in the active index' }, { status: 404 });
  }

  const stat = await fs.promises.stat(source.absolutePath);
  let diagnostics = null;
  if (effectiveAdapterId === 'realesrgan-ncnn') {
    const basePreset = ENHANCEMENT_PRESETS.find((preset) => preset.id === body.presetId) ?? SHARP_TEST_PRESET;
    const sourceMeta = await sharp(source.absolutePath, { failOn: 'none' }).metadata();
    const sourceWidth = sourceMeta.width;
    const sourceHeight = sourceMeta.height;
    const requestedScale = settings.scale ?? basePreset.scale;
    const nativeScale = nativeNcnnScale(requestedScale);
    const workWidth = sourceWidth ? Math.max(1, Math.round(sourceWidth * nativeScale)) : undefined;
    const workHeight = sourceHeight ? Math.max(1, Math.round(sourceHeight * nativeScale)) : undefined;
    const targetWidth = sourceWidth ? Math.max(1, Math.round(sourceWidth * requestedScale)) : undefined;
    const targetHeight = sourceHeight ? Math.max(1, Math.round(sourceHeight * requestedScale)) : undefined;
    const modelName = ncnnModelName(basePreset.modelFamily);
    const workMP = roundMP(workWidth, workHeight);
    const targetMP = roundMP(targetWidth, targetHeight);
    const warningLevel = warningLevelForNcnn(modelName, workMP, targetMP);
    diagnostics = {
      backend: 'realesrgan-ncnn',
      modelName,
      warningLevel,
      sourceWidth,
      sourceHeight,
      sourceMP: roundMP(sourceWidth, sourceHeight),
      requestedScale,
      nativeScale,
      workWidth,
      workHeight,
      workMP,
      targetWidth,
      targetHeight,
      targetMP,
    };
    if (warningLevel === 'blocked') {
      return NextResponse.json(
        {
          error: `This upscale is too large for normal mode (${workMP || '?'}MP AI work, ${targetMP || '?'}MP output). Use a lower scale or crop/smaller source.`,
          code: 'UPSCALE_TOO_LARGE',
          diagnostics,
        },
        { status: 409 }
      );
    }
    if (warningLevel === 'confirm' && body.confirmLargeJob !== true) {
      return NextResponse.json(
        {
          error: `This upscale may take several minutes (${workMP || '?'}MP AI work, ${targetMP || '?'}MP output). Confirm before starting.`,
          code: 'UPSCALE_REQUIRES_CONFIRMATION',
          diagnostics,
        },
        { status: 409 }
      );
    }
  }
  const job = await getEnhancementJobStore().createJob({
    sourceId: source.id,
    sourcePath: source.absolutePath,
    sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    presetId: body.presetId,
    adapterId: effectiveAdapterId,
    ...settings,
  });
  startEnhancementQueue();

  return NextResponse.json({ job, diagnostics }, { status: 202 });
}

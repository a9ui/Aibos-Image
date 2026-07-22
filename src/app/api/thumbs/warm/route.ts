import { NextRequest, NextResponse } from 'next/server';
import path from 'path';
import { getIndex } from '@/lib/indexer';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { parseDirSet } from '@/lib/pathSet';
import { enqueueThumbnails, getThumbnailWarmupState, startThumbnailWarmup } from '@/lib/thumbnailCache';
import { collectLimitedThumbnailWarmupPaths } from '@/lib/thumbnailWarmupPaths';

export const dynamic = 'force-dynamic';

const DIRECT_WARMUP_LIMIT = 500;
const BACKGROUND_WARMUP_LIMIT = 5000;

function normalizeDirPaths(value: unknown) {
  if (typeof value !== 'string' || !value.trim()) return [];
  const paths: string[] = [];
  for (const item of parseDirSet(value)) {
    try {
      paths.push(path.resolve(item));
    } catch {
      // ignore invalid path fragments
    }
  }
  return paths;
}

function isInsideDir(filePath: string, dirPath: string) {
  const rel = path.relative(dirPath, filePath);
  return !rel.startsWith('..') && !path.isAbsolute(rel);
}

function isInsideAnyDir(filePath: string, dirPaths: string[]) {
  return dirPaths.length === 0 || dirPaths.some((dirPath) => isInsideDir(filePath, dirPath));
}

export async function GET() {
  return NextResponse.json({ warmup: getThumbnailWarmupState() });
}

export async function POST(request: NextRequest) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  const body = await request.json().catch(() => ({}));
  const indexToken = typeof (body as { indexToken?: unknown }).indexToken === 'string'
    ? (body as { indexToken: string }).indexToken
    : undefined;
  const dirs = normalizeDirPaths((body as { dir?: unknown }).dir);
  const directPaths = Array.isArray((body as { paths?: unknown }).paths)
    ? (body as { paths: unknown[] }).paths
      .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
      .map((item) => path.resolve(item))
    : [];
  const priorityRaw = String((body as { priority?: unknown }).priority ?? '').toLowerCase();
  const priority = priorityRaw === 'focused' || priorityRaw === 'current' || priorityRaw === 'modal'
    ? -1
    : priorityRaw === 'visible' || priorityRaw === 'high'
      ? 0
      : priorityRaw === 'nearby'
        ? 1
        : 2;
  const limitValue = Number((body as { limit?: unknown }).limit);
  const limit = Number.isFinite(limitValue) && limitValue > 0
    ? Math.min(Math.trunc(limitValue), BACKGROUND_WARMUP_LIMIT)
    : BACKGROUND_WARMUP_LIMIT;

  if (directPaths.length > 0) {
    if (dirs.length === 0) {
      return NextResponse.json(
        { ok: false, error: 'dir is required when warming explicit thumbnail paths' },
        { status: 400 }
      );
    }
    const indexedPaths = indexToken
      ? new Set(getIndex(indexToken).map((image) => path.resolve(image.id).toLowerCase()))
      : null;
    const paths = directPaths.filter((imagePath) => (
      isInsideAnyDir(imagePath, dirs) &&
      (!indexedPaths || indexedPaths.has(imagePath.toLowerCase()))
    ));
    return NextResponse.json({
      ok: true,
      warmup: enqueueThumbnails(paths.slice(0, Math.min(limit, DIRECT_WARMUP_LIMIT)), priority),
    });
  }

  const paths = collectLimitedThumbnailWarmupPaths(
    getIndex(indexToken),
    (imagePath) => isInsideAnyDir(imagePath, dirs),
    limit
  );

  return NextResponse.json({
    ok: true,
    warmup: startThumbnailWarmup(paths, dirs.join('\n') || 'all-indexed-images'),
  });
}

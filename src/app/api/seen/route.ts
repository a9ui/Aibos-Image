import { promises as fs } from 'fs';
import path from 'path';
import { NextResponse } from 'next/server';

import { withFileWriteLock } from '@/lib/fileWriteLock';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { resolveSharedCachePath } from '@/lib/sharedProjectRoot';

const MAX_SEEN_PATH_LENGTH = 32_768;

function seenPath() {
  return resolveSharedCachePath('seen.json', process.env.PVU_SEEN_PATH);
}

function isObjectMap(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function isValidSeenPath(imagePath: string) {
  return Boolean(imagePath.trim()) && imagePath.length <= MAX_SEEN_PATH_LENGTH;
}

function normalizeIncomingSeenMap(value: unknown): Record<string, true> | null {
  if (!isObjectMap(value)) return null;

  const seen: Record<string, true> = {};
  for (const [imagePath, marker] of Object.entries(value)) {
    if (!isValidSeenPath(imagePath) || marker !== true) return null;
    seen[imagePath] = true;
  }
  return seen;
}

function normalizeStoredSeenMap(value: unknown): Record<string, true> | null {
  if (!isObjectMap(value)) return null;

  const seen: Record<string, true> = {};
  for (const [imagePath, marker] of Object.entries(value)) {
    if (!isValidSeenPath(imagePath)) return null;
    let isSeen: boolean;
    if (typeof marker === 'boolean') {
      isSeen = marker;
    } else if (typeof marker === 'number'
      && Number.isInteger(marker)
      && marker >= -2_147_483_648
      && marker <= 2_147_483_647) {
      isSeen = marker !== 0;
    } else if (typeof marker === 'string' && /^(?:true|false)$/i.test(marker.trim())) {
      isSeen = marker.trim().toLowerCase() === 'true';
    } else {
      return null;
    }
    if (isSeen) seen[imagePath] = true;
  }
  return seen;
}

async function readSeen(): Promise<
  { ok: true; seen: Record<string, true>; malformed: false } |
  { ok: false; seen: Record<string, true>; malformed: true; error: string }
> {
  try {
    const raw = await fs.readFile(seenPath(), 'utf8');
    const seen = normalizeStoredSeenMap(JSON.parse(raw));
    if (seen === null) {
      return {
        ok: false,
        seen: {},
        malformed: true,
        error: 'seen.json does not match the supported true-marker map schema.',
      };
    }
    return { ok: true, seen, malformed: false };
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code === 'ENOENT') {
      return { ok: true, seen: {}, malformed: false };
    }
    return {
      ok: false,
      seen: {},
      malformed: true,
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

async function writeSeen(seen: Record<string, true>) {
  const target = seenPath();
  const dir = path.dirname(target);
  const temp = path.join(dir, `seen-${process.pid}-${Date.now()}.tmp`);
  await fs.mkdir(dir, { recursive: true });
  try {
    await fs.writeFile(temp, `${JSON.stringify(seen, null, 2)}\n`, 'utf8');
    await fs.rename(temp, target);
  } finally {
    await fs.unlink(temp).catch(() => {});
  }
}

export async function GET() {
  const result = await readSeen();
  return NextResponse.json({
    ok: result.ok,
    seen: result.seen,
    malformed: result.malformed,
    error: result.ok ? undefined : result.error,
  });
}

export async function PUT(req: Request) {
  const forbidden = guardLocalApiRequest(req);
  if (forbidden) return forbidden;

  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return NextResponse.json({ ok: false, error: 'Request body must be valid JSON.' }, { status: 400 });
  }

  if (!isObjectMap(body) || !Object.hasOwn(body, 'seen')) {
    return NextResponse.json({ ok: false, error: 'Request body must include a seen map.' }, { status: 400 });
  }
  const incoming = normalizeIncomingSeenMap(body.seen);
  if (incoming === null) {
    return NextResponse.json({ ok: false, error: 'seen must be a true-marker path map.' }, { status: 400 });
  }

  try {
    return await withFileWriteLock(seenPath(), async () => {
      const current = await readSeen();
      if (!current.ok) {
        return NextResponse.json({
          ok: false,
          seen: current.seen,
          malformed: true,
          error: 'Shared seen JSON is malformed; refusing to overwrite it.',
        }, { status: 409 });
      }

      // Seen is monotonic: neither Browser nor WPF can clear a true marker by
      // sending an older snapshot. The lock makes read/union/replace atomic.
      const seen = { ...current.seen, ...incoming };
      await writeSeen(seen);
      return NextResponse.json({ ok: true, seen, malformed: false });
    });
  } catch (error) {
    return NextResponse.json({
      ok: false,
      error: error instanceof Error ? error.message : String(error),
      malformed: false,
    }, { status: 503 });
  }
}

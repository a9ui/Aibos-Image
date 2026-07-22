import { NextResponse } from 'next/server';

import {
  isBoundedSearchHistoryQuery,
  mutateSearchHistory,
  readSearchHistory,
} from '@/lib/searchHistory';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { resolveSharedCachePath } from '@/lib/sharedProjectRoot';

function searchHistoryPath() {
  return resolveSharedCachePath('search-history.json', process.env.PVU_SEARCH_HISTORY_PATH);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

async function requestBody(req: Request) {
  try {
    return { ok: true as const, body: await req.json() as unknown };
  } catch {
    return { ok: false as const, error: 'Request body must be valid JSON.' };
  }
}

function validQuery(value: unknown) {
  return typeof value === 'string'
    && isBoundedSearchHistoryQuery(value);
}

function mutationResponse(result: Awaited<ReturnType<typeof mutateSearchHistory>>) {
  return NextResponse.json(result, { status: result.ok ? 200 : 409 });
}

export async function GET() {
  const result = await readSearchHistory(searchHistoryPath());
  return NextResponse.json({
    ok: result.ok,
    entries: result.entries,
    malformed: result.malformed,
    futureVersion: result.futureVersion,
    error: result.ok ? undefined : result.error,
  });
}

export async function PUT(req: Request) {
  const forbidden = guardLocalApiRequest(req);
  if (forbidden) return forbidden;

  const parsed = await requestBody(req);
  if (!parsed.ok) return NextResponse.json({ ok: false, error: parsed.error }, { status: 400 });
  if (!isObject(parsed.body) || !validQuery(parsed.body.query)) {
    return NextResponse.json({ ok: false, error: 'query must be a non-empty bounded string.' }, { status: 400 });
  }

  try {
    return mutationResponse(await mutateSearchHistory(searchHistoryPath(), {
      action: 'commit',
      query: parsed.body.query as string,
    }));
  } catch (error) {
    return NextResponse.json({
      ok: false,
      error: error instanceof Error ? error.message : String(error),
      malformed: false,
      futureVersion: false,
    }, { status: 503 });
  }
}

export async function DELETE(req: Request) {
  const forbidden = guardLocalApiRequest(req);
  if (forbidden) return forbidden;

  const parsed = await requestBody(req);
  if (!parsed.ok) return NextResponse.json({ ok: false, error: parsed.error }, { status: 400 });
  if (!isObject(parsed.body)) {
    return NextResponse.json({ ok: false, error: 'Request body must be an object.' }, { status: 400 });
  }

  const clear = parsed.body.clear === true;
  if (!clear && !validQuery(parsed.body.query)) {
    return NextResponse.json({ ok: false, error: 'DELETE requires clear=true or a non-empty bounded query.' }, { status: 400 });
  }

  try {
    return mutationResponse(await mutateSearchHistory(
      searchHistoryPath(),
      clear
        ? { action: 'clear' }
        : { action: 'delete', query: parsed.body.query as string },
    ));
  } catch (error) {
    return NextResponse.json({
      ok: false,
      error: error instanceof Error ? error.message : String(error),
      malformed: false,
      futureVersion: false,
    }, { status: 503 });
  }
}

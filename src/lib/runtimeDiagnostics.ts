export interface RuntimeDiagnostics {
  product: 'Aibos Image';
  sourceRevision: string | null;
  sourceDirty: boolean | null;
  buildId: string | null;
  buildCompletedAtUtc: string | null;
  serverPort: number | null;
}

export type RuntimeDiagnosticsResult =
  | { ok: true; value: RuntimeDiagnostics }
  | { ok: false };

const SAFE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$/;
const UTC_TIMESTAMP_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/;

function readNullableSafeId(value: unknown): string | null | undefined {
  if (value === null) return null;
  if (typeof value !== 'string' || !SAFE_ID_PATTERN.test(value)) return undefined;
  return value;
}

function readNullableUtcTimestamp(value: unknown): string | null | undefined {
  if (value === null) return null;
  if (
    typeof value !== 'string'
    || value.length > 64
    || !UTC_TIMESTAMP_PATTERN.test(value)
    || !Number.isFinite(Date.parse(value))
  ) {
    return undefined;
  }
  return value;
}

function readNullablePort(value: unknown): number | null | undefined {
  if (value === null) return null;
  if (!Number.isInteger(value) || (value as number) < 1 || (value as number) > 65_535) {
    return undefined;
  }
  return value as number;
}

/**
 * Accept only the bounded, non-path runtime fields that Settings is allowed to
 * render or copy. The API may grow future fields without exposing them here.
 */
export function normalizeRuntimeDiagnostics(value: unknown): RuntimeDiagnosticsResult {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return { ok: false };
  const candidate = value as Record<string, unknown>;
  const sourceRevision = readNullableSafeId(candidate.sourceRevision);
  const buildId = readNullableSafeId(candidate.buildId);
  const buildCompletedAtUtc = readNullableUtcTimestamp(candidate.buildCompletedAtUtc);
  const serverPort = readNullablePort(candidate.serverPort);

  if (
    candidate.product !== 'Aibos Image'
    || (typeof candidate.sourceDirty !== 'boolean' && candidate.sourceDirty !== null)
    || (candidate.serverHost !== '127.0.0.1' && candidate.serverHost !== null)
    || sourceRevision === undefined
    || buildId === undefined
    || buildCompletedAtUtc === undefined
    || serverPort === undefined
  ) {
    return { ok: false };
  }

  return {
    ok: true,
    value: {
      product: 'Aibos Image',
      sourceRevision,
      sourceDirty: candidate.sourceDirty,
      buildId,
      buildCompletedAtUtc,
      serverPort: candidate.serverHost === '127.0.0.1' ? serverPort : null,
    },
  };
}

export function shortRuntimeRevision(revision: string | null): string {
  if (!revision) return 'Unavailable';
  return revision.length > 10 ? revision.slice(0, 10) : revision;
}

export function runtimeSourceStateLabel(sourceDirty: boolean | null): 'Dirty' | 'Clean' | 'Unavailable' {
  if (sourceDirty === true) return 'Dirty';
  if (sourceDirty === false) return 'Clean';
  return 'Unavailable';
}

export function formatRuntimeDiagnosticsCopy(
  runtime: RuntimeDiagnostics,
  browserUserAgent: string,
): string {
  const safeUserAgent = browserUserAgent.replace(/[\r\n]+/g, ' ').slice(0, 512);
  return [
    'Aibos Image runtime diagnostics',
    `Product: ${runtime.product}`,
    `Source revision: ${runtime.sourceRevision ?? 'Unavailable'}`,
    `Source state: ${runtimeSourceStateLabel(runtime.sourceDirty)}`,
    `Build ID: ${runtime.buildId ?? 'Unavailable'}`,
    `Build completed (UTC): ${runtime.buildCompletedAtUtc ?? 'Unavailable'}`,
    `Server: ${runtime.serverPort ? `127.0.0.1:${runtime.serverPort}` : 'Unavailable'}`,
    `Browser: ${safeUserAgent || 'Unavailable'}`,
  ].join('\n');
}

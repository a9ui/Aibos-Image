import { describe, expect, it } from 'vitest';

import {
  formatRuntimeDiagnosticsCopy,
  normalizeRuntimeDiagnostics,
  runtimeSourceStateLabel,
  shortRuntimeRevision,
} from './runtimeDiagnostics';

const fullRevision = '1234567890abcdef1234567890abcdef12345678';

function runtimePayload(overrides: Record<string, unknown> = {}) {
  return {
    product: 'Aibos Image',
    sourceRevision: fullRevision,
    sourceDirty: false,
    buildId: 'build_2026-07-18',
    buildCompletedAtUtc: '2026-07-18T01:02:03.000Z',
    serverHost: '127.0.0.1',
    serverPort: 3011,
    serverStartedAtUtc: '2026-07-18T01:03:00.000Z',
    processId: 4321,
    projectRoot: 'C:/Users/private/project',
    ...overrides,
  };
}

describe('runtime diagnostics safety boundary', () => {
  it('keeps only bounded non-path fields and reports dirty source state', () => {
    const result = normalizeRuntimeDiagnostics(runtimePayload({ sourceDirty: true }));

    expect(result).toEqual({
      ok: true,
      value: {
        product: 'Aibos Image',
        sourceRevision: fullRevision,
        sourceDirty: true,
        buildId: 'build_2026-07-18',
        buildCompletedAtUtc: '2026-07-18T01:02:03.000Z',
        serverPort: 3011,
      },
    });
    if (!result.ok) throw new Error('Expected valid runtime diagnostics.');
    expect(result.value).not.toHaveProperty('projectRoot');
    expect(result.value).not.toHaveProperty('processId');
    expect(shortRuntimeRevision(result.value.sourceRevision)).toBe('1234567890');
  });

  it('accepts launcher-unavailable nulls without inventing a local endpoint', () => {
    expect(normalizeRuntimeDiagnostics(runtimePayload({
      sourceRevision: null,
      sourceDirty: null,
      buildId: null,
      buildCompletedAtUtc: null,
      serverHost: null,
      serverPort: null,
    }))).toEqual({
      ok: true,
      value: {
        product: 'Aibos Image',
        sourceRevision: null,
        sourceDirty: null,
        buildId: null,
        buildCompletedAtUtc: null,
        serverPort: null,
      },
    });
    expect(shortRuntimeRevision(null)).toBe('Unavailable');
    expect(runtimeSourceStateLabel(null)).toBe('Unavailable');
  });

  it.each([
    ['non-object payload', null],
    ['wrong product', runtimePayload({ product: 'OtherViewer' })],
    ['path-shaped revision', runtimePayload({ sourceRevision: 'C:/Users/private/revision' })],
    ['non-boolean dirty state', runtimePayload({ sourceDirty: 'false' })],
    ['non-loopback host', runtimePayload({ serverHost: '0.0.0.0' })],
    ['out-of-range port', runtimePayload({ serverPort: 70_000 })],
    ['invalid timestamp', runtimePayload({ buildCompletedAtUtc: 'yesterday' })],
    ['non-UTC timestamp', runtimePayload({ buildCompletedAtUtc: '2026-07-18 01:02:03' })],
  ])('rejects %s', (_name, payload) => {
    expect(normalizeRuntimeDiagnostics(payload)).toEqual({ ok: false });
  });

  it('copies only approved runtime fields plus the browser user agent', () => {
    const result = normalizeRuntimeDiagnostics(runtimePayload());
    if (!result.ok) throw new Error('Expected valid runtime diagnostics.');

    const text = formatRuntimeDiagnosticsCopy(result.value, 'TestBrowser/1.0\r\nInjected: value');

    expect(text).toContain(`Source revision: ${fullRevision}`);
    expect(text).toContain('Source state: Clean');
    expect(text).toContain('Server: 127.0.0.1:3011');
    expect(text).toContain('Browser: TestBrowser/1.0');
    expect(text).toContain('TestBrowser/1.0 Injected: value');
    expect(text).not.toContain('\nInjected:');
    expect(text).not.toContain('C:/Users/private/project');
    expect(text).not.toContain('4321');
    expect(text).not.toContain('process');
  });

  it('copies an unavailable source state without claiming the source is clean', () => {
    const result = normalizeRuntimeDiagnostics(runtimePayload({ sourceDirty: null }));
    if (!result.ok) throw new Error('Expected valid runtime diagnostics.');

    const text = formatRuntimeDiagnosticsCopy(result.value, 'TestBrowser/1.0');

    expect(text).toContain('Source state: Unavailable');
    expect(text).not.toContain('Source state: Clean');
  });
});

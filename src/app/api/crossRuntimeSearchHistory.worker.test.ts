import { access, mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

import { expect, it } from 'vitest';

import { PUT as putSearchHistory } from './search-history/route';

const iterations = Number.parseInt(process.env.CROSS_RUNTIME_ITERATIONS ?? '', 10);
const target = process.env.PVU_SEARCH_HISTORY_PATH ?? '';
const startGatePath = process.env.CROSS_RUNTIME_START_GATE_PATH ?? '';
const readyPath = process.env.CROSS_RUNTIME_BROWSER_READY_PATH ?? '';
const resultPath = process.env.CROSS_RUNTIME_BROWSER_RESULT_PATH ?? '';
const writeDelayMs = Number.parseInt(process.env.CROSS_RUNTIME_WRITE_DELAY_MS ?? '10', 10);
const enabled = Number.isInteger(iterations)
  && iterations > 0
  && target !== ''
  && startGatePath !== ''
  && readyPath !== ''
  && resultPath !== ''
  && Number.isInteger(writeDelayMs)
  && writeDelayMs >= 0;

function request(query: string) {
  return new Request('http://127.0.0.1:3001/api/search-history', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ query }),
  });
}

async function waitForStartGate(timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await access(startGatePath);
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  throw new Error(`Timed out waiting for cross-runtime start gate: ${startGatePath}`);
}

async function delayBetweenWrites() {
  if (writeDelayMs > 0) {
    await new Promise((resolve) => setTimeout(resolve, writeDelayMs));
  }
}

it.skipIf(!enabled)('writes Browser search history concurrently with the WPF process', async () => {
  let readyAtUnixMs: number | null = null;
  let gateObservedAtUnixMs: number | null = null;
  let writeStartedAtUnixMs: number | null = null;
  let writeCompletedAtUnixMs: number | null = null;
  let writes = 0;
  let unicodeWrites = false;
  let error: string | null = null;

  try {
    await mkdir(dirname(readyPath), { recursive: true });
    readyAtUnixMs = Date.now();
    await writeFile(readyPath, JSON.stringify({ ok: true, runtime: 'browser', readyAtUnixMs }), 'utf8');
    await waitForStartGate();
    gateObservedAtUnixMs = Date.now();
    writeStartedAtUnixMs = Date.now();

    const dottedI = await putSearchHistory(request('cat, i\u0307'));
    expect(dottedI.status).toBe(200);
    await delayBetweenWrites();

    const nonAscii = await putSearchHistory(request('\u043c\u043e\u0441\u043a\u0432\u0430, \u03bf\u03c3'));
    expect(nonAscii.status).toBe(200);
    const trimParity = await putSearchHistory(request('trim, parity'));
    expect(trimParity.status).toBe(200);
    unicodeWrites = true;
    await delayBetweenWrites();

    for (let index = 0; index < iterations; index += 1) {
      const response = await putSearchHistory(request(`browser query ${index.toString().padStart(2, '0')}`));
      expect(response.status).toBe(200);
      expect(await response.json()).toMatchObject({ ok: true, malformed: false, futureVersion: false });
      writes += 1;
      if (index + 1 < iterations) {
        await delayBetweenWrites();
      }
    }
    writeCompletedAtUnixMs = Date.now();
  } catch (caught) {
    error = caught instanceof Error ? caught.message : String(caught);
    throw caught;
  } finally {
    await mkdir(dirname(resultPath), { recursive: true });
    await writeFile(resultPath, JSON.stringify({
      ok: error === null && writes === iterations && unicodeWrites,
      message: error ?? 'Browser shared search history writer completed',
      iterations,
      writes,
      unicodeWrites,
      writeDelayMs,
      readyAtUnixMs,
      gateObservedAtUnixMs,
      writeStartedAtUnixMs,
      writeCompletedAtUnixMs,
      historyPath: target,
    }, null, 2), 'utf8');
  }
});

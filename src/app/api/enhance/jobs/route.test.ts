import fs from "fs";
import os from "os";
import path from "path";
import { NextRequest } from "next/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearIndexSessionsForTests, setIndex } from "@/lib/indexer";
import {
  getEnhancementIsolationMetrics,
  resetEnhancementIsolationMetricsForTests,
} from "@/lib/enhance/isolationMetrics";
import {
  EnhancementJobStore,
  setEnhancementJobStoreForTests,
} from "@/lib/enhance/jobStore";
import { startEnhancementQueue } from "@/lib/enhance/queue";
import { ENHANCEMENT_PRESETS, SHARP_TEST_PRESET } from "@/lib/enhance/types";
import type { ImageFile } from "@/lib/types";
import { POST } from "./route";

vi.mock("@/lib/enhance/queue", () => ({
  startEnhancementQueue: vi.fn(),
}));

function createRequest(body: Record<string, unknown>) {
  return new NextRequest("http://127.0.0.1/api/enhance/jobs", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

describe("POST /api/enhance/jobs preset validation", () => {
  let root: string;
  let store: EnhancementJobStore;
  let source: ImageFile;

  beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), "pvu-enhance-route-"));
    store = new EnhancementJobStore(root);
    setEnhancementJobStoreForTests(store);
    resetEnhancementIsolationMetricsForTests();
    vi.mocked(startEnhancementQueue).mockClear();

    const sourcePath = path.join(root, "source.png");
    fs.writeFileSync(sourcePath, "route-validation-fixture");
    const stat = fs.statSync(sourcePath);
    source = {
      id: sourcePath,
      filename: path.basename(sourcePath),
      absolutePath: sourcePath,
      fileUrl: "/api/image?path=source.png&thumb=true",
      displayUrl: "/api/image?path=source.png&display=true",
      fullUrl: "/api/image?path=source.png",
      metadata: null,
      createdAt: stat.birthtimeMs,
      mtime: stat.mtimeMs,
    };
    setIndex([source]);
  });

  afterEach(() => {
    setIndex([]);
    clearIndexSessionsForTests();
    fs.rmSync(root, { recursive: true, force: true });
  });

  it("accepts a known preset ID", async () => {
    const preset = ENHANCEMENT_PRESETS[1];
    const response = await POST(
      createRequest({
        sourceId: source.id,
        presetId: preset.id,
        adapterId: "sharp-test",
      }),
    );

    expect(response.status).toBe(202);
    const payload = await response.json();
    expect(payload.job).toMatchObject({ presetId: preset.id, preset });
    expect(await store.listJobs()).toHaveLength(1);
    expect(startEnhancementQueue).toHaveBeenCalledOnce();
  });

  it("keeps the existing default when presetId is omitted", async () => {
    const response = await POST(
      createRequest({
        sourceId: source.id,
        adapterId: "sharp-test",
      }),
    );

    expect(response.status).toBe(202);
    const payload = await response.json();
    expect(payload.job).toMatchObject({
      presetId: SHARP_TEST_PRESET.id,
      preset: SHARP_TEST_PRESET,
    });
    expect(await store.listJobs()).toHaveLength(1);
    expect(startEnhancementQueue).toHaveBeenCalledOnce();
  });

  it("rejects an explicit unknown preset without creating or starting work", async () => {
    const response = await POST(
      createRequest({
        sourceId: source.id,
        presetId: "removed-legacy-preset",
        adapterId: "sharp-test",
      }),
    );

    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toEqual({
      error: "Unknown enhancement preset: removed-legacy-preset",
    });
    expect(await store.listJobs()).toEqual([]);
    expect(startEnhancementQueue).not.toHaveBeenCalled();
    expect(getEnhancementIsolationMetrics()).toEqual({
      enhancementEnqueues: 0,
      enhancementWorkerStarts: 0,
    });
  });
});

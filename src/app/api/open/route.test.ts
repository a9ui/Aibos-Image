import os from "os";
import path from "path";
import { NextRequest } from "next/server";
import { describe, expect, it, vi } from "vitest";

import { buildDefaultApplicationLaunch, createOpenHandler, type OpenRouteDependencies } from "./route";

function openRequest(
  filePath: string,
  options: {
    display?: "enhanced";
    jobId?: string;
    method?: string;
    headers?: Record<string, string>;
  } = {},
) {
  const url = new URL("http://127.0.0.1/api/open");
  url.searchParams.set("path", filePath);
  if (options.display) url.searchParams.set("display", options.display);
  if (options.jobId) url.searchParams.set("jobId", options.jobId);
  return new NextRequest(url, {
    method: options.method ?? "POST",
    headers: options.headers,
  });
}

function dependencies(indexedPath: string, managedOutputsRoot = path.join(os.tmpdir(), "enhance", "outputs")): OpenRouteDependencies {
  return {
    platform: process.platform,
    getIndexedPaths: vi.fn(() => [indexedPath]),
    getFileInfo: vi.fn(() => ({
      exists: true,
      isFile: true,
      size: 123,
      mtimeMs: 456,
    })),
    realPath: vi.fn((candidate) => path.resolve(candidate)),
    isSupportedImage: vi.fn(() => true),
    getEnhancementJob: vi.fn(async () => null),
    getManagedOutputsRoot: vi.fn(() => managedOutputsRoot),
    openFile: vi.fn(async () => undefined),
  };
}

describe("open route active-index boundary", () => {
  it.each([
    ["foreign Host", { host: "evil.example:3000" }],
    ["foreign Origin", { host: "127.0.0.1", origin: "https://evil.example" }],
    ["cross-site Fetch Metadata", { host: "127.0.0.1", "sec-fetch-site": "cross-site" }],
    [
      "no-cors Fetch Metadata",
      {
        host: "127.0.0.1",
        "sec-fetch-site": "same-origin",
        "sec-fetch-mode": "no-cors",
      },
    ],
  ])("rejects %s before resolving or launching a file", async (_name, headers) => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const deps = dependencies(indexedPath);

    const response = await createOpenHandler(deps)(openRequest(indexedPath, { headers }));

    expect(response.status).toBe(403);
    expect(deps.getIndexedPaths).not.toHaveBeenCalled();
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it.each([String.raw`C:\PVU\x&calc&.jpg`, String.raw`C:\PVU\%COMSPEC%.png`, String.raw`C:\PVU\a^b!.webp`, String.raw`C:\PVU\(draft) 雪 photo.png`])(
    "passes a Windows filename as one opaque Explorer argument: %s",
    (filePath) => {
      const launch = buildDefaultApplicationLaunch(filePath, "win32");

      expect(launch).toEqual({
        command: "explorer.exe",
        args: [filePath],
        options: { windowsHide: true, shell: false },
      });
      expect(launch.command.toLowerCase()).not.toBe("cmd.exe");
      expect(launch.args).not.toContain("/c");
    },
  );

  it("uses direct default-application launchers on non-Windows platforms", () => {
    expect(buildDefaultApplicationLaunch("/tmp/photo.png", "darwin")).toEqual({
      command: "open",
      args: ["/tmp/photo.png"],
      options: { windowsHide: true, shell: false },
    });
    expect(buildDefaultApplicationLaunch("/tmp/photo.png", "linux")).toEqual({
      command: "xdg-open",
      args: ["/tmp/photo.png"],
      options: { windowsHide: true, shell: false },
    });
  });

  it("rejects a path outside the active index without launching an application", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const requestedPath = path.join(os.tmpdir(), "unindexed.png");
    const deps = dependencies(indexedPath);

    const response = await createOpenHandler(deps)(openRequest(requestedPath));

    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({
      error: "Image is not in the active index",
    });
    expect(deps.getFileInfo).not.toHaveBeenCalled();
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it("opens the exact indexed image after existence and type checks", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const deps = dependencies(indexedPath);

    const response = await createOpenHandler(deps)(openRequest(indexedPath));

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      success: true,
      opened: "source",
      sizeBytes: 123,
    });
    expect(deps.getFileInfo).toHaveBeenCalledWith(path.resolve(indexedPath));
    expect(deps.isSupportedImage).toHaveBeenCalledWith(path.resolve(indexedPath));
    expect(deps.openFile).toHaveBeenCalledWith(path.resolve(indexedPath));
  });

  it("opens the currently displayed managed Enhanced output", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const outputPath = path.join(outputsRoot, "owned", "enhanced.webp");
    const deps = dependencies(indexedPath, outputsRoot);
    vi.mocked(deps.getEnhancementJob).mockResolvedValue({
      id: "job-1",
      sourceId: indexedPath,
      sourcePath: indexedPath,
      sourceSignature: { size: 123, mtimeMs: 456 },
      presetId: "test",
      presetHash: "hash",
      preset: {} as never,
      adapterId: "test",
      status: "succeeded",
      progress: 100,
      outputPath,
      createdAt: "2026-07-20T00:00:00.000Z",
      updatedAt: "2026-07-20T00:00:00.000Z",
    });

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
      }),
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      success: true,
      opened: "enhanced",
      sizeBytes: 123,
    });
    expect(deps.openFile).toHaveBeenCalledWith(path.resolve(outputPath));
  });

  it("resolves displayed Enhanced size without launching an application", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const outputPath = path.join(outputsRoot, "owned", "enhanced.webp");
    const deps = dependencies(indexedPath, outputsRoot);
    vi.mocked(deps.getFileInfo).mockImplementation((candidate) => ({
      exists: true,
      isFile: true,
      size: path.resolve(candidate) === path.resolve(outputPath) ? 1_572_864 : 123,
      mtimeMs: 456,
    }));
    vi.mocked(deps.getEnhancementJob).mockResolvedValue({
      id: "job-1",
      sourceId: indexedPath,
      sourcePath: indexedPath,
      sourceSignature: { size: 123, mtimeMs: 456 },
      presetId: "test",
      presetHash: "hash",
      preset: {} as never,
      adapterId: "test",
      status: "succeeded",
      progress: 100,
      outputPath,
      createdAt: "2026-07-20T00:00:00.000Z",
      updatedAt: "2026-07-20T00:00:00.000Z",
    });

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
        method: "GET",
      }),
    );

    expect(await response.json()).toEqual({
      success: true,
      opened: "enhanced",
      sizeBytes: 1_572_864,
    });
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it.each(["GET", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS"])("keeps %s requests passive while resolving the displayed file size", async (method) => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const deps = dependencies(indexedPath);

    const response = await createOpenHandler(deps)(openRequest(indexedPath, { method }));

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      success: true,
      opened: "source",
      sizeBytes: 123,
    });
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it.each([
    ["missing", path.join(os.tmpdir(), "enhance", "outputs", "owned", "missing.webp"), { size: 123, mtimeMs: 456 }, "enhanced-output-missing"],
    ["stale", path.join(os.tmpdir(), "enhance", "outputs", "owned", "stale.webp"), { size: 999, mtimeMs: 456 }, "enhanced-source-stale"],
    ["outside ownership", path.join(os.tmpdir(), "outside", "enhanced.webp"), { size: 123, mtimeMs: 456 }, "enhanced-output-outside-ownership"],
  ])("falls back to Original when the Enhanced output is %s", async (_name, outputPath, sourceSignature, fallbackCode) => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const deps = dependencies(indexedPath, outputsRoot);
    vi.mocked(deps.getEnhancementJob).mockResolvedValue({
      id: "job-1",
      sourceId: indexedPath,
      sourcePath: indexedPath,
      sourceSignature,
      presetId: "test",
      presetHash: "hash",
      preset: {} as never,
      adapterId: "test",
      status: "succeeded",
      progress: 100,
      outputPath,
      createdAt: "2026-07-20T00:00:00.000Z",
      updatedAt: "2026-07-20T00:00:00.000Z",
    });
    if (_name === "missing") {
      vi.mocked(deps.getFileInfo).mockImplementation((candidate) => ({
        exists: path.resolve(candidate) !== path.resolve(outputPath),
        isFile: path.resolve(candidate) !== path.resolve(outputPath),
        size: 123,
        mtimeMs: 456,
      }));
    }

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
      }),
    );
    const body = await response.json();

    expect(response.status).toBe(200);
    expect(body).toMatchObject({
      success: true,
      opened: "source",
      fallback: { code: fallbackCode },
    });
    expect(deps.openFile).toHaveBeenCalledWith(path.resolve(indexedPath));
  });

  it("falls back to Original when a lexically managed output resolves through a link outside the managed root", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const outputPath = path.join(outputsRoot, "linked", "enhanced.webp");
    const escapedPath = path.join(os.tmpdir(), "outside", "escaped.webp");
    const deps = dependencies(indexedPath, outputsRoot);
    vi.mocked(deps.realPath).mockImplementation((candidate) => (path.resolve(candidate) === path.resolve(outputPath) ? path.resolve(escapedPath) : path.resolve(candidate)));
    vi.mocked(deps.getEnhancementJob).mockResolvedValue({
      id: "job-1",
      sourceId: indexedPath,
      sourcePath: indexedPath,
      sourceSignature: { size: 123, mtimeMs: 456 },
      presetId: "test",
      presetHash: "hash",
      preset: {} as never,
      adapterId: "test",
      status: "succeeded",
      progress: 100,
      outputPath,
      createdAt: "2026-07-20T00:00:00.000Z",
      updatedAt: "2026-07-20T00:00:00.000Z",
    });

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
      }),
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      success: true,
      opened: "source",
      fallback: { code: "enhanced-output-outside-ownership" },
    });
    expect(deps.openFile).toHaveBeenCalledWith(path.resolve(indexedPath));
    expect(deps.openFile).not.toHaveBeenCalledWith(path.resolve(escapedPath));
  });

  it.each([
    ["missing", undefined],
    ["malformed size", { size: Number.NaN, mtimeMs: 456 }],
    ["malformed mtime", { size: 123, mtimeMs: Number.POSITIVE_INFINITY }],
  ])("falls back to Original when the source signature is %s", async (_name, sourceSignature) => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const outputPath = path.join(outputsRoot, "owned", "enhanced.webp");
    const deps = dependencies(indexedPath, outputsRoot);
    vi.mocked(deps.getEnhancementJob).mockResolvedValue({
      id: "job-1",
      sourceId: indexedPath,
      sourcePath: indexedPath,
      sourceSignature,
      presetId: "test",
      presetHash: "hash",
      preset: {} as never,
      adapterId: "test",
      status: "succeeded",
      progress: 100,
      outputPath,
      createdAt: "2026-07-20T00:00:00.000Z",
      updatedAt: "2026-07-20T00:00:00.000Z",
    } as never);

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
        method: "GET",
      }),
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      success: true,
      opened: "source",
      sizeBytes: 123,
      fallback: { code: "enhanced-source-signature-invalid" },
    });
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it.each([
    ["source id mismatch", "enhanced-source-mismatch", false],
    ["job store failure", "enhanced-job-store-unavailable", true],
  ])("falls back to Original on %s", async (_name, fallbackCode, rejectJobStore) => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const outputsRoot = path.join(os.tmpdir(), "enhance", "outputs");
    const outputPath = path.join(outputsRoot, "owned", "enhanced.webp");
    const deps = dependencies(indexedPath, outputsRoot);
    if (rejectJobStore) {
      vi.mocked(deps.getEnhancementJob).mockRejectedValue(new Error("private store detail"));
    } else {
      vi.mocked(deps.getEnhancementJob).mockResolvedValue({
        id: "job-1",
        sourceId: path.join(os.tmpdir(), "different.png"),
        sourcePath: indexedPath,
        sourceSignature: { size: 123, mtimeMs: 456 },
        presetId: "test",
        presetHash: "hash",
        preset: {} as never,
        adapterId: "test",
        status: "succeeded",
        progress: 100,
        outputPath,
        createdAt: "2026-07-20T00:00:00.000Z",
        updatedAt: "2026-07-20T00:00:00.000Z",
      });
    }

    const response = await createOpenHandler(deps)(
      openRequest(indexedPath, {
        display: "enhanced",
        jobId: "job-1",
        method: "GET",
      }),
    );

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      opened: "source",
      sizeBytes: 123,
      fallback: { code: fallbackCode },
    });
    expect(deps.openFile).not.toHaveBeenCalled();
  });

  it("returns a generic recoverable error when shell launch fails", async () => {
    const indexedPath = path.join(os.tmpdir(), "indexed.png");
    const deps = dependencies(indexedPath);
    vi.mocked(deps.openFile).mockRejectedValue(new Error("private shell detail"));

    const response = await createOpenHandler(deps)(openRequest(indexedPath));

    expect(response.status).toBe(500);
    expect(await response.json()).toEqual({
      error: "Open external application failed",
    });
  });
});

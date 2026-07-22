import fs from "fs";
import os from "os";
import path from "path";
import { NextRequest } from "next/server";
import { afterEach, describe, expect, it, vi } from "vitest";

import { createImageHandler } from "./route";

const createdPaths: string[] = [];

function imageRequest(
  filePath: string,
  options: {
    indexToken?: string;
    thumb?: boolean;
    headers?: Record<string, string>;
  } = {},
) {
  const url = new URL("http://127.0.0.1/api/image");
  url.searchParams.set("path", filePath);
  if (options.indexToken) url.searchParams.set("indexToken", options.indexToken);
  if (options.thumb) url.searchParams.set("thumb", "true");
  return new NextRequest(url, { headers: options.headers });
}

afterEach(() => {
  for (const filePath of createdPaths.splice(0)) {
    try {
      fs.rmSync(filePath, { force: true });
    } catch {}
  }
});

describe("image route active-index boundary", () => {
  it("rejects an existing-looking image outside the active index before file access", async () => {
    const requestedPath = path.join(os.tmpdir(), "photoviewer-unindexed.png");
    const handler = createImageHandler({
      platform: process.platform,
      getIndexedPaths: () => [path.join(os.tmpdir(), "other.png")],
      hasIndexSession: () => true,
    });

    const response = await handler(imageRequest(requestedPath));

    expect(response.status).toBe(403);
    expect(await response.text()).toBe("Image is not in the active index");
  });

  it("serves the exact image held by the active index", async () => {
    const indexedPath = path.join(
      os.tmpdir(),
      `photoviewer-indexed-${process.pid}.png`,
    );
    createdPaths.push(indexedPath);
    fs.writeFileSync(indexedPath, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    const handler = createImageHandler({
      platform: process.platform,
      getIndexedPaths: () => [indexedPath],
      hasIndexSession: () => true,
    });

    const response = await handler(imageRequest(indexedPath));

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("image/png");
    expect(Array.from(new Uint8Array(await response.arrayBuffer()))).toEqual([
      0x89, 0x50, 0x4e, 0x47,
    ]);
  });

  it("serves a same-origin no-cors thumbnail request instead of rejecting the img subresource", async () => {
    const indexedPath = path.join(
      os.tmpdir(),
      `photoviewer-indexed-no-cors-${process.pid}.png`,
    );
    createdPaths.push(indexedPath);
    fs.writeFileSync(indexedPath, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    const handler = createImageHandler({
      platform: process.platform,
      getIndexedPaths: () => [indexedPath],
      hasIndexSession: () => true,
    });

    const response = await handler(
      imageRequest(indexedPath, {
        thumb: true,
        headers: {
          host: "127.0.0.1",
          "sec-fetch-site": "same-origin",
          "sec-fetch-mode": "no-cors",
          "sec-fetch-dest": "image",
        },
      }),
    );

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("image/png");
  });

  it("serves indexed images without synchronous filesystem probes", async () => {
    const indexedPath = path.join(
      os.tmpdir(),
      `photoviewer-indexed-async-${process.pid}.png`,
    );
    createdPaths.push(indexedPath);
    fs.writeFileSync(indexedPath, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    const handler = createImageHandler({
      platform: process.platform,
      getIndexedPaths: () => [indexedPath],
      hasIndexSession: () => true,
    });
    const syncProbe = () => {
      throw new Error("synchronous filesystem probe used");
    };
    const existsSyncSpy = vi.spyOn(fs, "existsSync").mockImplementation(syncProbe);
    const statSyncSpy = vi.spyOn(fs, "statSync").mockImplementation(syncProbe);

    try {
      const response = await handler(imageRequest(indexedPath));

      expect(response.status).toBe(200);
      expect(Array.from(new Uint8Array(await response.arrayBuffer()))).toEqual([
        0x89, 0x50, 0x4e, 0x47,
      ]);
      expect(existsSyncSpy).not.toHaveBeenCalled();
      expect(statSyncSpy).not.toHaveBeenCalled();
    } finally {
      existsSyncSpy.mockRestore();
      statSyncSpy.mockRestore();
    }
  });

  it("returns 410 for an explicit viewer session that no longer exists without falling back to the active index", async () => {
    const requestedPath = path.join(os.tmpdir(), "photoviewer-expired-session.png");
    const getIndexedPaths = vi.fn(() => [requestedPath]);
    const handler = createImageHandler({
      platform: process.platform,
      getIndexedPaths,
      hasIndexSession: () => false,
    });

    const response = await handler(imageRequest(requestedPath, {
      indexToken: "idx_expired",
    }));

    expect(response.status).toBe(410);
    expect(response.headers.get("cache-control")).toBe("no-store");
    expect(await response.text()).toContain("viewer session expired");
    expect(getIndexedPaths).not.toHaveBeenCalled();
  });
});

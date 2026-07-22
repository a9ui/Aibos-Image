import path from "path";

function pathApi(platform: NodeJS.Platform) {
  return platform === "win32" ? path.win32 : path.posix;
}

/** Canonical identity used by every route that accepts an indexed image path. */
export function canonicalImagePathKey(
  filePath: string,
  platform: NodeJS.Platform = process.platform,
) {
  const resolved = pathApi(platform).resolve(filePath);
  return platform === "win32" ? resolved.toLowerCase() : resolved;
}

/**
 * Resolve a caller-provided path to the exact spelling held by the active
 * catalog. Returning the catalog path avoids case-only aliases on Windows and
 * ensures local file APIs cannot escape the image set the user explicitly
 * scanned.
 */
export function findActiveIndexedImagePath(
  requestedPath: string,
  indexedPaths: readonly string[],
  platform: NodeJS.Platform = process.platform,
): string | null {
  const requestedKey = canonicalImagePathKey(requestedPath, platform);
  return (
    indexedPaths.find(
      (candidate) =>
        canonicalImagePathKey(candidate, platform) === requestedKey,
    ) ?? null
  );
}

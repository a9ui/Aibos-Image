import path from "path";
import { NextRequest, NextResponse } from "next/server";
import { guardLocalApiRequest } from "@/lib/localApiGuard";
import {
  canonicalImagePathKey,
  findActiveIndexedImagePath,
} from "@/lib/activeImagePath";

export interface DeleteRouteDependencies {
  platform: NodeJS.Platform;
  projectRoot: () => string;
  getIndexedPaths: () => string[];
  exists: (filePath: string) => boolean;
  realPath: (filePath: string) => string;
  isSupportedImagePath: (filePath: string) => boolean;
  getDerivedPaths: (filePath: string) => Promise<string[]>;
  recycleFile: (filePath: string) => Promise<void>;
  removeFromIndex: (filePath: string) => void;
  removeDerivedImages: (filePaths: string[]) => Promise<void>;
}

function pathApi(platform: NodeJS.Platform) {
  return platform === "win32" ? path.win32 : path.posix;
}

/**
 * Windows path identity is case-insensitive. Keep the original indexed path for
 * filesystem/catalog mutations, and use this canonical key only for comparisons.
 */
export function canonicalPathKey(filePath: string, platform: NodeJS.Platform) {
  return canonicalImagePathKey(filePath, platform);
}

export function isPathInsideDirectory(
  parent: string,
  child: string,
  platform: NodeJS.Platform,
) {
  const api = pathApi(platform);
  const relative = api.relative(
    canonicalPathKey(parent, platform),
    canonicalPathKey(child, platform),
  );
  return (
    relative === "" ||
    (relative !== ".." &&
      !relative.startsWith(`..${api.sep}`) &&
      !api.isAbsolute(relative))
  );
}

export function createDeleteHandler(dependencies: DeleteRouteDependencies) {
  return async function deleteImage(request: NextRequest) {
    const forbidden = guardLocalApiRequest(request);
    if (forbidden) return forbidden;

    const requestedPath = request.nextUrl.searchParams.get("path");

    if (!requestedPath) {
      return NextResponse.json({ error: "Missing path" }, { status: 400 });
    }

    const { platform } = dependencies;
    const resolvedRequest = pathApi(platform).resolve(requestedPath);
    const projectRoot = pathApi(platform).resolve(dependencies.projectRoot());

    // Check the lexical path before touching the index or filesystem. A second
    // real-path check below also blocks symlink/junction aliases into the repo.
    if (isPathInsideDirectory(projectRoot, resolvedRequest, platform)) {
      return NextResponse.json(
        { error: "Cannot delete files inside the project directory" },
        { status: 403 },
      );
    }

    const indexedPath = findActiveIndexedImagePath(
      resolvedRequest,
      dependencies.getIndexedPaths(),
      platform,
    );
    if (!indexedPath) {
      return NextResponse.json(
        { error: "Can only delete images from the active index" },
        { status: 403 },
      );
    }

    // Preserve the index's exact spelling so Windows case differences do not
    // leave a stale in-memory or on-disk catalog entry after deletion.
    const targetPath = pathApi(platform).resolve(indexedPath);
    if (!dependencies.exists(targetPath)) {
      return NextResponse.json({ error: "File not found" }, { status: 404 });
    }

    if (!dependencies.isSupportedImagePath(targetPath)) {
      return NextResponse.json(
        { error: "Unsupported image type" },
        { status: 415 },
      );
    }

    try {
      const realProjectRoot = dependencies.realPath(projectRoot);
      const realTargetPath = dependencies.realPath(targetPath);
      if (isPathInsideDirectory(realProjectRoot, realTargetPath, platform)) {
        return NextResponse.json(
          { error: "Cannot delete files inside the project directory" },
          { status: 403 },
        );
      }

      // Resolve cache paths before the source file leaves the filesystem. These
      // lookups are read-only and must succeed before the irreversible action.
      const derivedPaths = await dependencies.getDerivedPaths(targetPath);

      // A junction/symlink can be replaced while the cache paths are being
      // prepared. Re-resolve immediately before invoking the Recycle Bin and
      // require the same canonical target that passed the first guard. This
      // does not make a pathname operation fully race-free, but it prevents a
      // detected reparse-point swap from deleting a different source.
      const finalRealProjectRoot = dependencies.realPath(projectRoot);
      const finalRealTargetPath = dependencies.realPath(targetPath);
      if (
        isPathInsideDirectory(
          finalRealProjectRoot,
          finalRealTargetPath,
          platform,
        )
      ) {
        return NextResponse.json(
          { error: "Cannot delete files inside the project directory" },
          { status: 403 },
        );
      }
      if (
        canonicalPathKey(finalRealTargetPath, platform) !==
        canonicalPathKey(realTargetPath, platform)
      ) {
        return NextResponse.json(
          { error: "Delete target changed before recycle operation" },
          { status: 409 },
        );
      }

      // There is deliberately no hard-delete fallback. Catalog/cache mutation
      // begins only after the Recycle Bin operation reports success.
      await dependencies.recycleFile(targetPath);
      dependencies.removeFromIndex(indexedPath);

      try {
        await dependencies.removeDerivedImages(derivedPaths);
      } catch {
        // Derived cache cleanup is best-effort; visible images can regenerate it.
      }

      return NextResponse.json({ success: true, deletedTo: "recycle-bin" });
    } catch (error) {
      return NextResponse.json({ error: String(error) }, { status: 500 });
    }
  };
}

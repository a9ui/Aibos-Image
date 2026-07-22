import { NextRequest, NextResponse } from "next/server";
import { guardLocalApiRequest } from "@/lib/localApiGuard";
import { execFile } from "child_process";
import path from "path";
import fs from "fs";
import { isSupportedImagePath } from "@/lib/imageFormats";
import { getIndex } from "@/lib/indexer";
import { findActiveIndexedImagePath } from "@/lib/activeImagePath";
import { getEnhancementJobStore } from "@/lib/enhance/jobStore";
import { getEnhanceRoot } from "@/lib/enhance/outputPath";
import type { EnhancementJob } from "@/lib/enhance/types";

export const dynamic = "force-dynamic";

/**
 * POST /api/open?path=ABSOLUTE_PATH
 *
 * Opens a file in the OS default application (e.g. Windows Photo Viewer).
 * Windows deliberately invokes Explorer directly instead of routing a user
 * controlled filename through cmd.exe's command language.
 */
export interface OpenRouteDependencies {
  platform: NodeJS.Platform;
  getIndexedPaths: (indexToken?: string) => string[];
  getFileInfo: (filePath: string) => OpenFileInfo;
  realPath: (filePath: string) => string;
  isSupportedImage: (filePath: string) => boolean;
  getEnhancementJob: (jobId: string) => Promise<EnhancementJob | null>;
  getManagedOutputsRoot: () => string;
  openFile: (filePath: string) => Promise<void>;
}

export interface OpenFileInfo {
  exists: boolean;
  isFile: boolean;
  size: number;
  mtimeMs: number;
}

function getFileInfo(filePath: string): OpenFileInfo {
  try {
    const stat = fs.statSync(filePath);
    return {
      exists: true,
      isFile: stat.isFile(),
      size: stat.size,
      mtimeMs: stat.mtimeMs,
    };
  } catch {
    return { exists: false, isFile: false, size: 0, mtimeMs: 0 };
  }
}

export interface DefaultApplicationLaunch {
  command: string;
  args: string[];
  options: { windowsHide: true; shell: false };
}

export function buildDefaultApplicationLaunch(filePath: string, platform: NodeJS.Platform = process.platform): DefaultApplicationLaunch {
  if (platform === "win32") {
    return {
      command: "explorer.exe",
      args: [filePath],
      options: { windowsHide: true, shell: false },
    };
  }
  return platform === "darwin"
    ? {
        command: "open",
        args: [filePath],
        options: { windowsHide: true, shell: false },
      }
    : {
        command: "xdg-open",
        args: [filePath],
        options: { windowsHide: true, shell: false },
      };
}

function openWithDefaultApplication(filePath: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const launch = buildDefaultApplicationLaunch(filePath);

    execFile(launch.command, launch.args, launch.options, (error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}

const defaultDependencies: OpenRouteDependencies = {
  platform: process.platform,
  getIndexedPaths: (indexToken) => getIndex(indexToken).map((image) => image.absolutePath),
  getFileInfo,
  realPath: (filePath) => fs.realpathSync.native(filePath),
  isSupportedImage: isSupportedImagePath,
  getEnhancementJob: (jobId) => getEnhancementJobStore().getJob(jobId),
  getManagedOutputsRoot: () => path.resolve(getEnhanceRoot(), "outputs"),
  openFile: openWithDefaultApplication,
};

function managedOutputFallback(code: string) {
  return {
    code,
    message: "Displayed Enhanced output is unavailable; using Original instead.",
  };
}

function isPathInsideDirectory(rootPath: string, candidatePath: string, platform: NodeJS.Platform) {
  const pathApi = platform === "win32" ? path.win32 : path.posix;
  const relative = pathApi.relative(pathApi.resolve(rootPath), pathApi.resolve(candidatePath));
  return Boolean(relative) && relative !== ".." && !relative.startsWith(`..${pathApi.sep}`) && !pathApi.isAbsolute(relative);
}

async function resolveDisplayedOpenTarget(request: NextRequest, sourcePath: string, sourceInfo: OpenFileInfo, dependencies: OpenRouteDependencies) {
  if (request.nextUrl.searchParams.get("display") !== "enhanced") {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: null,
    };
  }

  const jobId = request.nextUrl.searchParams.get("jobId");
  let job: EnhancementJob | null = null;
  try {
    job = jobId ? await dependencies.getEnhancementJob(jobId) : null;
  } catch {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-job-store-unavailable"),
    };
  }
  if (!job || job.status !== "succeeded" || typeof job.outputPath !== "string" || !job.outputPath.trim()) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-job-unavailable"),
    };
  }

  if (
    typeof job.sourcePath !== "string" ||
    typeof job.sourceId !== "string" ||
    !job.sourcePath.trim() ||
    !job.sourceId.trim() ||
    !findActiveIndexedImagePath(job.sourcePath, [sourcePath], dependencies.platform) ||
    !findActiveIndexedImagePath(job.sourceId, [sourcePath], dependencies.platform)
  ) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-source-mismatch"),
    };
  }

  const sourceSignature = job.sourceSignature;
  if (!sourceSignature || !Number.isFinite(sourceSignature.size) || sourceSignature.size < 0 || !Number.isFinite(sourceSignature.mtimeMs)) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-source-signature-invalid"),
    };
  }

  if (sourceSignature.size !== sourceInfo.size || Math.abs(sourceSignature.mtimeMs - sourceInfo.mtimeMs) > 1) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-source-stale"),
    };
  }

  const outputPath = path.resolve(job.outputPath);
  const outputsRoot = path.resolve(dependencies.getManagedOutputsRoot());
  if (!isPathInsideDirectory(outputsRoot, outputPath, dependencies.platform)) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-output-outside-ownership"),
    };
  }

  const outputInfo = dependencies.getFileInfo(outputPath);
  if (!outputInfo.exists || !outputInfo.isFile) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-output-missing"),
    };
  }
  if (!dependencies.isSupportedImage(outputPath)) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-output-unsupported"),
    };
  }

  let canonicalOutput: string;
  let canonicalRoot: string;
  try {
    canonicalOutput = dependencies.realPath(outputPath);
    canonicalRoot = dependencies.realPath(outputsRoot);
  } catch {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-output-canonicalization-failed"),
    };
  }
  if (!isPathInsideDirectory(canonicalRoot, canonicalOutput, dependencies.platform)) {
    return {
      targetPath: sourcePath,
      opened: "source" as const,
      fileInfo: sourceInfo,
      fallback: managedOutputFallback("enhanced-output-outside-ownership"),
    };
  }

  return {
    targetPath: canonicalOutput,
    opened: "enhanced" as const,
    fileInfo: outputInfo,
    fallback: null,
  };
}

export function createOpenHandler(dependencies: OpenRouteDependencies = defaultDependencies) {
  return async function openImage(request: NextRequest) {
    const forbidden = guardLocalApiRequest(request);
    if (forbidden) return forbidden;

    const filePath = request.nextUrl.searchParams.get("path");
    const indexToken = request.nextUrl.searchParams.get("indexToken") || undefined;

    if (!filePath) {
      return NextResponse.json({ error: "Missing path" }, { status: 400 });
    }

    const indexedPath = findActiveIndexedImagePath(filePath, dependencies.getIndexedPaths(indexToken), dependencies.platform);
    if (!indexedPath) {
      if (indexToken) {
        return NextResponse.json({ error: "Image is not in this viewer session" }, { status: 404 });
      }
      return NextResponse.json({ error: "Image is not in the active index" }, { status: 403 });
    }

    const resolved = path.resolve(indexedPath);
    const sourceInfo = dependencies.getFileInfo(resolved);

    if (!sourceInfo.exists || !sourceInfo.isFile) {
      return NextResponse.json({ error: "File not found" }, { status: 404 });
    }

    if (!dependencies.isSupportedImage(resolved)) {
      return NextResponse.json({ error: "Unsupported image type" }, { status: 415 });
    }

    try {
      const target = await resolveDisplayedOpenTarget(request, resolved, sourceInfo, dependencies);
      if (request.method === "POST") {
        await dependencies.openFile(target.targetPath);
      }
      const response = NextResponse.json({
        success: true,
        opened: target.opened,
        sizeBytes: target.fileInfo.size,
        ...(target.fallback ? { fallback: target.fallback } : {}),
      });
      response.headers.set("Cache-Control", "no-store");
      return response;
    } catch {
      return NextResponse.json({ error: "Open external application failed" }, { status: 500 });
    }
  };
}

export const POST = createOpenHandler();
export const GET = POST;

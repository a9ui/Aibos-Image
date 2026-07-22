import { NextRequest } from 'next/server';
import fs from 'fs';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { getIndex, removeFromIndex } from '@/lib/indexer';
import { isSupportedImagePath } from '@/lib/imageFormats';
import { getDisplayPath, getThumbnailPath } from '@/lib/thumbnailCache';
import { createDeleteHandler } from './deleteHandler';

export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

const execFileAsync = promisify(execFile);

async function moveFileToRecycleBin(filePath: string) {
  if (process.platform !== 'win32') {
    throw new Error('Recycle Bin delete is only supported on Windows in this app.');
  }

  const encodedPath = Buffer.from(filePath, 'utf16le').toString('base64');
  const command = [
    "$ErrorActionPreference = 'Stop'",
    'Add-Type -AssemblyName Microsoft.VisualBasic',
    `$path = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('${encodedPath}'))`,
    "[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($path, 'OnlyErrorDialogs', 'SendToRecycleBin')",
  ].join('; ');
  const encodedCommand = Buffer.from(command, 'utf16le').toString('base64');

  await execFileAsync('powershell.exe', [
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-EncodedCommand',
    encodedCommand,
  ], {
    windowsHide: true,
    timeout: 30_000,
  });
}

async function removeDerivedImages(paths: string[]) {
  await Promise.all(paths.map(async (target) => {
    try {
      await fs.promises.rm(target, { force: true });
    } catch {
      // Derived cache cleanup is best-effort; visible image paths can regenerate.
    }
  }));
}

/**
 * DELETE /api/delete?path=ABSOLUTE_PATH
 *
 * Sends a local image to the Windows Recycle Bin, removes it from the in-memory
 * index and on-disk index cache, then cleans derived thumbnails/display images.
 */
function createDeleteImageHandler(indexToken?: string) {
  return createDeleteHandler({
    platform: process.platform,
    projectRoot: () => process.cwd(),
    getIndexedPaths: () => getIndex(indexToken).map((image) => image.absolutePath),
    exists: (filePath) => fs.existsSync(filePath),
    realPath: (filePath) => fs.realpathSync.native(filePath),
    isSupportedImagePath,
    getDerivedPaths: (filePath) => Promise.all([
      getThumbnailPath(filePath),
      getDisplayPath(filePath),
    ]),
    recycleFile: moveFileToRecycleBin,
    removeFromIndex: (filePath) => removeFromIndex(filePath, indexToken),
    removeDerivedImages,
  });
}

export async function DELETE(request: NextRequest) {
  const indexToken = request.nextUrl.searchParams.get('indexToken') || undefined;
  return createDeleteImageHandler(indexToken)(request);
}

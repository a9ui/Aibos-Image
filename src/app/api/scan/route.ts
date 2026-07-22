import { NextRequest } from 'next/server';
import { guardLocalApiRequest } from '@/lib/localApiGuard';
import { isScanAbortedError, ScanAbortedError, scanDirectory, setIndex } from '@/lib/indexer';
import { cancelThumbnailWarmup } from '@/lib/thumbnailCache';
import { basenameFromPath, parseDirSet } from '@/lib/pathSet';
import { canonicalScanFolderSet, reserveScanRun } from '@/lib/scanRunCoordinator';
import type { ImageFile } from '@/lib/types';

export const dynamic = 'force-dynamic';

/**
 * GET /api/scan?dir=PATH
 *
 * Incrementally scans a directory for supported local image files, extracts
 * Stable Diffusion PNG metadata when available, and streams progress via SSE.
 */
export async function GET(request: NextRequest) {
  const forbidden = guardLocalApiRequest(request);
  if (forbidden) return forbidden;

  const dir = request.nextUrl.searchParams.get('dir');
  const forceFull = request.nextUrl.searchParams.get('full') === '1';

  const dirs = parseDirSet(dir);

  if (dirs.length === 0) {
    return new Response(JSON.stringify({ error: 'Missing dir parameter' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const releaseScanRun = reserveScanRun(dirs);
  if (!releaseScanRun) {
    const message = 'A scan for this folder set is already running. Please retry when it completes.';
    if (request.headers.get('accept')?.includes('text/event-stream')) {
      const event = JSON.stringify({
        type: 'error',
        processed: 0,
        total: 0,
        newFiles: 0,
        message,
      });
      return new Response(`data: ${event}\n\n`, {
        headers: {
          'Content-Type': 'text/event-stream',
          'Cache-Control': 'no-cache',
          Connection: 'keep-alive',
        },
      });
    }
    return new Response(JSON.stringify({
      error: message,
      retryable: true,
    }), {
      status: 409,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const encoder = new TextEncoder();
  const abortController = new AbortController();
  const abort = () => abortController.abort();
  if (request.signal.aborted) abort();
  else request.signal.addEventListener('abort', abort, { once: true });
  cancelThumbnailWarmup();

  const stream = new ReadableStream({
    async start(controller) {
      const close = () => {
        try {
          controller.close();
        } catch {
          // The browser may have already closed the SSE stream.
        }
      };
      const enqueue = (event: string) => {
        if (abortController.signal.aborted) throw new ScanAbortedError();
        try {
          controller.enqueue(encoder.encode(event));
        } catch {
          abort();
          throw new ScanAbortedError();
        }
      };
      const keepAlive = setInterval(() => {
        try {
          enqueue(': keepalive\n\n');
        } catch {
          clearInterval(keepAlive);
        }
      }, 10000);
      try {
        if (abortController.signal.aborted) throw new ScanAbortedError();
        const seen = new Set<string>();
        const allImages: ImageFile[] = [];
        const failedRoots: string[] = [];
        let completedRoots = 0;
        let cumulativeProcessed = 0;
        let cumulativeNewFiles = 0;

        for (const root of dirs) {
          const rootIndex = completedRoots + 1;
          const rootLabel = basenameFromPath(root) || root;
          let rootLatestNewFiles = 0;
          let images: ImageFile[] = [];
          try {
            if (abortController.signal.aborted) throw new ScanAbortedError();
            images = await scanDirectory(root, (processed, total, newFiles, status) => {
              rootLatestNewFiles = newFiles;
              const currentTotal = Math.max(1, total);
              const currentFraction = Math.max(0, Math.min(1, processed / currentTotal));
              const displayProcessed = dirs.length > 1
                ? Math.min(99, Math.round(((completedRoots + currentFraction) / dirs.length) * 100))
                : cumulativeProcessed + processed;
              const displayTotal = dirs.length > 1
                ? 100
                : Math.max(cumulativeProcessed + total, dirs.length);
              const event = JSON.stringify({
                type: 'progress',
                processed: displayProcessed,
                total: displayTotal,
                newFiles: cumulativeNewFiles + newFiles,
                stage: status?.stage,
                message: dirs.length > 1
                  ? `[${rootIndex}/${dirs.length}] ${rootLabel}: ${status?.message ?? 'Scanning...'}`
                  : status?.message,
              });
              enqueue(`data: ${event}\n\n`);
            }, { forceFull, signal: abortController.signal });
          } catch (err) {
            if (isScanAbortedError(err)) throw err;
            failedRoots.push(`${rootLabel}: ${err instanceof Error ? err.message : String(err)}`);
            const event = JSON.stringify({
              type: 'progress',
              processed: dirs.length > 1 ? Math.min(99, Math.round(((completedRoots + 1) / dirs.length) * 100)) : 0,
              total: dirs.length > 1 ? 100 : 1,
              newFiles: cumulativeNewFiles,
              stage: 'scanning',
              message: `Skipped ${rootLabel}: ${err instanceof Error ? err.message : String(err)}`,
            });
            enqueue(`data: ${event}\n\n`);
          }

          for (const image of images) {
            if (seen.has(image.id)) continue;
            seen.add(image.id);
            allImages.push(image);
          }
          cumulativeProcessed = allImages.length;
          cumulativeNewFiles += rootLatestNewFiles;
          completedRoots += 1;
        }

        if (allImages.length === 0 && failedRoots.length > 0) {
          throw new Error(`Scan failed: ${failedRoots.join('; ')}`);
        }

        if (abortController.signal.aborted) throw new ScanAbortedError();
        // Store in memory for search
        const indexToken = setIndex(allImages, canonicalScanFolderSet(dirs));

        const completeEvent = JSON.stringify({
          type: 'complete',
          processed: allImages.length,
          total: allImages.length,
          newFiles: 0,
          stage: 'complete',
          indexToken,
          message: failedRoots.length > 0
            ? `Scan complete with ${failedRoots.length} skipped folder(s). ${allImages.length} images indexed.`
            : `Scan complete. ${allImages.length} images indexed.`,
        });
        enqueue(`data: ${completeEvent}\n\n`);
        close();
      } catch (err) {
        if (isScanAbortedError(err) || abortController.signal.aborted) {
          close();
          return;
        }
        const errorEvent = JSON.stringify({
          type: 'error',
          processed: 0,
          total: 0,
          newFiles: 0,
          message: String(err),
        });
        try {
          enqueue(`data: ${errorEvent}\n\n`);
        } catch {
          // A disconnected client cannot receive a terminal error event.
        }
        close();
      } finally {
        clearInterval(keepAlive);
        request.signal.removeEventListener('abort', abort);
        releaseScanRun();
      }
    },
    cancel() {
      abort();
    },
  });

  return new Response(stream, {
    headers: {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
    },
  });
}

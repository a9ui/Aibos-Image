import { spawn } from 'child_process';
import fs from 'fs';
import path from 'path';
import sharp from 'sharp';
import type { EnhancementAdapter } from '../types';
import { publishEnhancementOutput } from '../outputPublish';
import {
  registerNcnnVulkanShutdownCleanup,
  terminateNcnnVulkanProcess,
  trackNcnnVulkanProcess,
  untrackNcnnVulkanProcess,
} from './ncnnProcessRegistry';
import { getNcnnVulkanAvailability, NCNN_EXE, NCNN_MODEL_DIR, NCNN_ROOT } from './ncnnConfig';

function wait(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function elapsedMs(started: number) {
  return Math.max(0, Math.round(Date.now() - started));
}

function megapixels(width?: number, height?: number) {
  if (!width || !height) return undefined;
  return Math.round((width * height / 1_000_000) * 100) / 100;
}

function nativeScaleFor(requestedScale: number) {
  if (requestedScale <= 2) return 2;
  if (requestedScale <= 3) return 3;
  return 4;
}

function modelNameFor(modelFamily: string | undefined, nativeScale: number) {
  if (modelFamily === 'anime') return 'realesr-animevideov3';
  return 'realesrgan-x4plus';
}

function outputExtension(format: 'png' | 'webp' | 'jpg') {
  return format === 'jpg' ? 'jpg' : format;
}

function warningLevelFor(modelName: string, workMP?: number, finalMP?: number) {
  if (!workMP && !finalMP) return 'none';
  const work = workMP || 0;
  const final = finalMP || 0;
  if (final > 160) return 'blocked';
  if (modelName === 'realesr-animevideov3') {
    if (work > 120) return 'blocked';
    if (work > 80 || final > 120) return 'confirm';
    if (work > 40 || final > 80) return 'slow';
    return 'none';
  }
  if (modelName === 'realesrgan-x4plus-anime') {
    if (work > 35) return 'blocked';
    if (work > 20 || final > 120) return 'confirm';
    if (work > 10 || final > 80) return 'slow';
    return 'none';
  }
  if (work > 60) return 'blocked';
  if (work > 40 || final > 120) return 'confirm';
  if (work > 20 || final > 80) return 'slow';
  return 'none';
}

async function runNcnn(
  jobId: string,
  runId: string | undefined,
  args: string[],
  cwd: string,
  isCancelRequested: () => Promise<boolean>,
  onNcnnProgress: (percent: number) => void,
  onProcessStart?: (pid: number) => void
) {
  return await new Promise<void>((resolve, reject) => {
    const child = spawn(/*turbopackIgnore: true*/ NCNN_EXE, args, {
      cwd: /*turbopackIgnore: true*/ cwd,
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    let settled = false;
    if (child.pid) {
      trackNcnnVulkanProcess(jobId, runId, child.pid);
      onProcessStart?.(child.pid);
    }

    const cancelTimer = setInterval(() => {
      void isCancelRequested().then((cancelRequested) => {
        if (!cancelRequested || settled) return;
        terminateNcnnVulkanProcess(child.pid);
      }).catch(() => {});
    }, 500);

    const collectProgress = (chunk: Buffer) => {
      const text = String(chunk);
      const matches = text.matchAll(/(\d+(?:\.\d+)?)%/g);
      for (const match of matches) {
        const percent = Number(match[1]);
        if (Number.isFinite(percent)) onNcnnProgress(Math.max(0, Math.min(100, percent)));
      }
    };
    child.stdout.on('data', (chunk) => {
      stdout += String(chunk);
      collectProgress(chunk);
    });
    child.stderr.on('data', (chunk) => {
      stderr += String(chunk);
      collectProgress(chunk);
    });
    child.on('error', (error) => {
      settled = true;
      untrackNcnnVulkanProcess(jobId);
      clearInterval(cancelTimer);
      reject(error);
    });
    child.on('exit', (code, signal) => {
      settled = true;
      untrackNcnnVulkanProcess(jobId);
      clearInterval(cancelTimer);
      if (signal) {
        reject(new Error('Real-ESRGAN ncnn-vulkan job canceled.'));
        return;
      }
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(`Real-ESRGAN ncnn-vulkan failed with exit ${code}: ${(stderr || stdout).slice(-1000)}`));
    });
  });
}

async function normalizeOutput(
  ncnnOutputPath: string,
  finalOutputPath: string,
  tempFinalOutputPath: string,
  outputFormat: 'png' | 'webp' | 'jpg',
  targetWidth?: number,
  targetHeight?: number
) {
  const started = Date.now();
  const notes: string[] = [];
  const meta = await sharp(ncnnOutputPath, { failOn: 'none' }).metadata();

  await fs.promises.mkdir(path.dirname(finalOutputPath), { recursive: true });
  const sizeMatches = !targetWidth || !targetHeight || (meta.width === targetWidth && meta.height === targetHeight);
  const extMatches = path.extname(ncnnOutputPath).slice(1).toLowerCase() === outputExtension(outputFormat);

  if (sizeMatches && extMatches) {
    await fs.promises.copyFile(ncnnOutputPath, tempFinalOutputPath);
    notes.push('copied ncnn output without Sharp re-encode');
    if (!meta.width || !meta.height) {
      throw new Error('Real-ESRGAN final output could not be read.');
    }
    if (targetWidth && targetHeight && (meta.width !== targetWidth || meta.height !== targetHeight)) {
      throw new Error(`Real-ESRGAN final output size mismatch: expected ${targetWidth}x${targetHeight}, got ${meta.width}x${meta.height}.`);
    }
    return {
      postprocessMs: elapsedMs(started),
      outputWidth: meta.width,
      outputHeight: meta.height,
      outputMP: megapixels(meta.width, meta.height),
      notes,
    };
  }

  let pipeline = sharp(ncnnOutputPath, { failOn: 'none' });
  if (!sizeMatches && targetWidth && targetHeight) {
    pipeline = pipeline.resize(targetWidth, targetHeight, {
      fit: 'fill',
      kernel: sharp.kernel.lanczos3,
    });
    notes.push(`resized ncnn output ${meta.width || '?'}x${meta.height || '?'} to requested ${targetWidth}x${targetHeight}`);
  }
  if (outputFormat === 'webp') {
    pipeline = pipeline.webp({ quality: 94 });
  } else if (outputFormat === 'jpg') {
    pipeline = pipeline.jpeg({ quality: 95, mozjpeg: true });
  } else {
    pipeline = pipeline.png();
  }
  const finalInfo = await pipeline.toFile(tempFinalOutputPath);
  if (!finalInfo.width || !finalInfo.height) {
    throw new Error('Real-ESRGAN final output could not be read after postprocess.');
  }
  if (targetWidth && targetHeight && (finalInfo.width !== targetWidth || finalInfo.height !== targetHeight)) {
    throw new Error(`Real-ESRGAN final output size mismatch after postprocess: expected ${targetWidth}x${targetHeight}, got ${finalInfo.width}x${finalInfo.height}.`);
  }
  return {
    postprocessMs: elapsedMs(started),
    outputWidth: finalInfo.width,
    outputHeight: finalInfo.height,
    outputMP: megapixels(finalInfo.width, finalInfo.height),
    notes,
  };
}

export const ncnnVulkanAdapter: EnhancementAdapter = {
  id: 'realesrgan-ncnn',
  label: 'Real-ESRGAN fast GPU',
  async run(job, context) {
    registerNcnnVulkanShutdownCleanup();
    if (!fs.existsSync(/*turbopackIgnore: true*/ NCNN_EXE)) {
      throw new Error(`Real-ESRGAN ncnn-vulkan executable not found: ${NCNN_EXE}`);
    }
    if (context.preset.scale > 4) {
      throw new Error('Real-ESRGAN fast GPU supports real AI upscale up to 4x. Use 2x/3x/4x, or choose another method for experimental larger resizing.');
    }
    const totalStarted = Date.now();
    const sourceMeta = await sharp(job.sourcePath, { failOn: 'none' }).metadata();
    const sourceWidth = sourceMeta.width;
    const sourceHeight = sourceMeta.height;
    const requestedScale = context.preset.scale;
    const nativeScale = nativeScaleFor(requestedScale);
    const workWidth = sourceWidth ? Math.max(1, Math.round(sourceWidth * nativeScale)) : undefined;
    const workHeight = sourceHeight ? Math.max(1, Math.round(sourceHeight * nativeScale)) : undefined;
    const targetWidth = sourceWidth ? Math.max(1, Math.round(sourceWidth * requestedScale)) : undefined;
    const targetHeight = sourceHeight ? Math.max(1, Math.round(sourceHeight * requestedScale)) : undefined;
    const modelName = modelNameFor(context.preset.modelFamily, nativeScale);
    const workMP = megapixels(workWidth, workHeight);
    const targetMP = megapixels(targetWidth, targetHeight);
    const warningLevel = warningLevelFor(modelName, workMP, targetMP);
    const notes = [
      `using ${modelName} through ncnn-vulkan`,
      ...(nativeScale !== requestedScale ? [`native ${nativeScale}x output will be resized to requested ${requestedScale}x`] : []),
      ...(warningLevel !== 'none' ? [`${warningLevel} size warning for ${workMP || '?'}MP AI work and ${targetMP || '?'}MP final output`] : []),
    ];

    await context.updateDiagnostics({
      backend: 'realesrgan-ncnn',
      modelName,
      warningLevel,
      sourceWidth,
      sourceHeight,
      sourceMP: megapixels(sourceWidth, sourceHeight),
      requestedScale,
      nativeScale,
      workWidth,
      workHeight,
      workMP,
      targetWidth,
      targetHeight,
      targetMP,
      notes,
    });
    await context.updateProgress(8);

    const tempRoot = path.join(path.dirname(context.outputPath), `${job.id}__${job.runId || 'run'}__ncnn_tmp`);
    const tempFinalOutput = `${context.outputPath}.${job.runId || job.id}.tmp`;
    await fs.promises.rm(tempRoot, { recursive: true, force: true }).catch(() => {});
    await fs.promises.rm(tempFinalOutput, { force: true }).catch(() => {});
    await fs.promises.mkdir(tempRoot, { recursive: true });
    const tempInput = path.join(tempRoot, 'input.png');
    const tempOutput = path.join(tempRoot, `output.${outputExtension(context.preset.outputFormat)}`);
    const normalizeInputStarted = Date.now();
    await sharp(job.sourcePath, { failOn: 'none' })
      .rotate()
      .png()
      .toFile(tempInput);
    await context.updateDiagnostics({ uploadMs: elapsedMs(normalizeInputStarted) });

    const gpuStarted = Date.now();
    const args = [
      '-i', tempInput,
      '-o', tempOutput,
      '-s', String(nativeScale),
      '-m', NCNN_MODEL_DIR,
      '-n', modelName,
      '-f', outputExtension(context.preset.outputFormat),
      '-v',
      '-j', '1:2:2',
    ];

    try {
      await context.updateProgress(20);
      await runNcnn(job.id, job.runId, args, NCNN_ROOT, context.isCancelRequested, (percent) => {
        void context.updateProgress(20 + percent * 0.62).catch(() => {});
      }, (pid) => {
        void context.updateExternalProcessId?.(pid).catch(() => {});
        void context.updateDiagnostics({ notes: [...notes, `ncnn process ${pid}`] }).catch(() => {});
      });
      const ncnnMs = elapsedMs(gpuStarted);
      await context.updateDiagnostics({ processorMs: ncnnMs, ncnnMs });
      await context.updateProgress(82);
      if (await context.isCancelRequested()) throw new Error('Job canceled');
      const outputDiagnostics = await normalizeOutput(
        tempOutput,
        context.outputPath,
        tempFinalOutput,
        context.preset.outputFormat,
        targetWidth,
        targetHeight
      );
      if (await context.isCancelRequested()) throw new Error('Job canceled');
      const publishMethod = await publishEnhancementOutput(tempFinalOutput, context.outputPath);
      await context.updateDiagnostics({
        ...outputDiagnostics,
        notes: [
          ...notes,
          ...(outputDiagnostics.notes || []),
          ...(publishMethod !== 'rename'
            ? ['Windows file lock required safe copy fallback while publishing output']
            : []),
          ...(publishMethod === 'copy-with-stale-temporary'
            ? ['Enhanced output published; temporary cleanup was deferred because Windows still held the source file']
            : []),
        ],
        totalMs: elapsedMs(totalStarted),
      });
      await context.updateProgress(100);
      return { outputPath: context.outputPath };
    } finally {
      await wait(50);
      await fs.promises.rm(tempRoot, { recursive: true, force: true }).catch(() => {});
      await fs.promises.rm(tempFinalOutput, { force: true }).catch(() => {});
    }
  },
};

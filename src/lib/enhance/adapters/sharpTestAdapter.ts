import fs from 'fs';
import path from 'path';
import sharp from 'sharp';
import type { EnhancementAdapter } from '../types';

function wait(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, value));
}

export const sharpTestAdapter: EnhancementAdapter = {
  id: 'sharp-test',
  label: 'Local detail enhancer',
  async run(job, context) {
    await fs.promises.mkdir(path.dirname(context.outputPath), { recursive: true });

    for (const progress of [10, 25, 40]) {
      if (await context.isCancelRequested()) {
        throw new Error('Job canceled');
      }
      await context.updateProgress(progress);
      await wait(120);
    }

    const metadata = await sharp(job.sourcePath, { failOn: 'none' }).metadata();
    const width = metadata.width ? Math.round(metadata.width * context.preset.scale) : undefined;
    const detail = clamp(context.preset.detail ?? 0, 0, 100);
    const smoothness = clamp(context.preset.smoothness ?? 0, 0, 100);
    const colorBrightness = clamp(context.preset.colorBrightness ?? 0, -100, 100);
    const colorContrast = clamp(context.preset.colorContrast ?? 0, -100, 100);
    const colorSaturation = clamp(context.preset.colorSaturation ?? 0, -100, 100);
    const denoise = clamp(context.preset.denoise, 0, 100);
    const sharpen = clamp(context.preset.sharpen, 0, 100);
    const medianSize = denoise >= 55 ? 5 : denoise >= 25 ? 3 : denoise >= 10 ? 2 : 0;
    const blurSigma = smoothness > 0 ? clamp(smoothness / 180, 0.3, 0.7) : 0;
    const brightnessMultiplier = clamp(1 + colorBrightness / 160, 0.35, 1.65);
    const contrastMultiplier = clamp(1 + colorContrast / 140, 0.35, 1.8);
    const saturationMultiplier = clamp(1 + colorSaturation / 100, 0, 2);
    const sharpenSigma = clamp(0.45 + sharpen / 80 + detail / 150, 0.45, 2.6);
    const sharpenM1 = clamp(0.8 + detail / 55, 0.8, 2.5);
    const sharpenM2 = clamp(1.2 + sharpen / 40 + detail / 45, 1.2, 4.2);

    let pipeline = sharp(job.sourcePath, { failOn: 'none' })
      .resize({
        width,
        withoutEnlargement: false,
        kernel: sharp.kernel.lanczos3,
      });
    if (medianSize > 0) {
      pipeline = pipeline.median(medianSize);
    }
    if (blurSigma > 0) {
      pipeline = pipeline.blur(blurSigma);
    }
    if (colorBrightness !== 0 || colorSaturation !== 0) {
      pipeline = pipeline.modulate({
        brightness: brightnessMultiplier,
        saturation: saturationMultiplier,
      });
    }
    if (colorContrast !== 0) {
      pipeline = pipeline.linear(contrastMultiplier, -(128 * contrastMultiplier) + 128);
    }
    pipeline = pipeline.sharpen({
      sigma: sharpenSigma,
      m1: sharpenM1,
      m2: sharpenM2,
      x1: 2,
      y2: 10 + detail / 6,
      y3: 18 + detail / 3,
    });
    if (context.preset.outputFormat === 'webp') {
      pipeline = pipeline.webp({ quality: 92 });
    } else if (context.preset.outputFormat === 'jpg') {
      pipeline = pipeline.jpeg({ quality: 94, mozjpeg: true });
    } else {
      pipeline = pipeline.png();
    }
    await pipeline.toFile(context.outputPath);

    await context.updateProgress(90);
    if (await context.isCancelRequested()) {
      throw new Error('Job canceled');
    }
    await context.updateProgress(100);

    return { outputPath: context.outputPath };
  },
};

import fs from 'fs';
import path from 'path';

export const NCNN_ROOT = process.env.PVU_REALESRGAN_NCNN_ROOT || 'C:\\AI\\RealESRGAN-ncnn-vulkan';
export const NCNN_EXE = process.env.PVU_REALESRGAN_NCNN_EXE || path.join(/*turbopackIgnore: true*/ NCNN_ROOT, 'realesrgan-ncnn-vulkan.exe');
export const NCNN_MODEL_DIR = process.env.PVU_REALESRGAN_NCNN_MODEL_DIR || path.join(/*turbopackIgnore: true*/ NCNN_ROOT, 'models');

const REQUIRED_MODEL_FILES = [
  'realesr-animevideov3-x2.param',
  'realesr-animevideov3-x2.bin',
  'realesr-animevideov3-x3.param',
  'realesr-animevideov3-x3.bin',
  'realesr-animevideov3-x4.param',
  'realesr-animevideov3-x4.bin',
  'realesrgan-x4plus.param',
  'realesrgan-x4plus.bin',
];

export function getNcnnVulkanAvailability() {
  const requiredPaths = [
    NCNN_EXE,
    NCNN_MODEL_DIR,
    ...REQUIRED_MODEL_FILES.map((file) => path.join(/*turbopackIgnore: true*/ NCNN_MODEL_DIR, file)),
  ];
  const missing = requiredPaths.filter((filePath) => !fs.existsSync(/*turbopackIgnore: true*/ filePath));
  return {
    available: missing.length === 0,
    exePath: NCNN_EXE,
    modelDir: NCNN_MODEL_DIR,
    missing,
  };
}

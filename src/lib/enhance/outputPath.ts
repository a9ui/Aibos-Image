import crypto from 'crypto';
import path from 'path';
import { resolveSharedCachePath } from '../sharedProjectRoot';
import type { EnhancementPreset, SourceSignature } from './types';

export function getEnhanceRoot(root?: string) {
  return root ?? resolveSharedCachePath('enhance', process.env.PVU_ENHANCE_ROOT);
}

export function getJobsFilePath(root?: string) {
  return path.join(getEnhanceRoot(root), 'jobs.json');
}

export function hashPreset(preset: EnhancementPreset) {
  const stable = JSON.stringify({
    id: preset.id,
    modelFamily: preset.modelFamily,
    modelName: preset.modelName,
    scale: preset.scale,
    outputFormat: preset.outputFormat,
    denoise: preset.denoise,
    sharpen: preset.sharpen,
    detail: preset.detail,
    smoothness: preset.smoothness,
    colorBrightness: preset.colorBrightness ?? 0,
    colorContrast: preset.colorContrast ?? 0,
    colorSaturation: preset.colorSaturation ?? 0,
    options: preset.options ?? {},
  });
  return crypto.createHash('sha256').update(stable).digest('hex').slice(0, 12);
}

export function hashSource(sourcePath: string, signature: SourceSignature, presetHash: string, adapterId: string) {
  return crypto
    .createHash('sha256')
    .update(JSON.stringify({ sourcePath, signature, presetHash, adapterId }))
    .digest('hex')
    .slice(0, 16);
}

export function getEnhancementOutputPath(
  sourcePath: string,
  signature: SourceSignature,
  preset: EnhancementPreset,
  adapterId: string,
  root?: string,
  jobId?: string
) {
  const presetHash = hashPreset(preset);
  const sourceHash = hashSource(sourcePath, signature, presetHash, adapterId);
  const parsed = path.parse(sourcePath);
  const safeBase = parsed.name.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').slice(0, 80) || 'image';
  const safeJobId = jobId ? jobId.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').slice(0, 80) : '';
  const filename = `${safeJobId ? `${safeJobId}__` : ''}${safeBase}__${preset.id}__${presetHash}.${preset.outputFormat}`;
  return path.join(getEnhanceRoot(root), 'outputs', sourceHash, filename);
}

export type EnhancementJobStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'canceled' | 'deleted';

export interface SourceSignature {
  size: number;
  mtimeMs: number;
}

export interface EnhancementPreset {
  id: string;
  label: string;
  modelFamily: 'anime' | 'photo' | 'general';
  modelName: string;
  scale: number;
  outputFormat: 'png' | 'webp' | 'jpg';
  denoise: number;
  sharpen: number;
  detail: number;
  smoothness: number;
  colorBrightness: number;
  colorContrast: number;
  colorSaturation: number;
  options?: Record<string, string | number | boolean>;
}

export interface EnhancedImage {
  jobId: string;
  sourceId: string;
  sourcePath: string;
  outputPath: string;
  presetId: string;
  adapterId: string;
  createdAt: string;
}

export interface EnhancementDiagnostics {
  backend?: 'sharp-test' | 'realesrgan-ncnn' | 'comfyui';
  modelName?: string;
  warningLevel?: 'none' | 'slow' | 'confirm' | 'blocked';
  sourceWidth?: number;
  sourceHeight?: number;
  sourceMP?: number;
  requestedScale?: number;
  nativeScale?: number;
  workWidth?: number;
  workHeight?: number;
  workMP?: number;
  targetWidth?: number;
  targetHeight?: number;
  targetMP?: number;
  outputWidth?: number;
  outputHeight?: number;
  outputMP?: number;
  uploadMs?: number;
  queueMs?: number;
  comfyWaitMs?: number;
  processorMs?: number;
  ncnnMs?: number;
  downloadMs?: number;
  postprocessMs?: number;
  totalMs?: number;
  notes?: string[];
}

export interface EnhancementJob {
  id: string;
  sourceId: string;
  sourcePath: string;
  sourceSignature: SourceSignature;
  presetId: string;
  presetHash: string;
  preset: EnhancementPreset;
  adapterId: string;
  status: EnhancementJobStatus;
  progress: number;
  outputPath?: string;
  errorMessage?: string;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  finishedAt?: string;
  cancelRequested?: boolean;
  runId?: string;
  workerInstanceId?: string;
  lastHeartbeatAt?: string;
  externalPromptId?: string;
  externalProcessId?: number;
  diagnostics?: EnhancementDiagnostics;
}

export interface EnhancementJobStoreData {
  version: number;
  jobs: EnhancementJob[];
}

export interface EnhancementAdapterContext {
  preset: EnhancementPreset;
  outputPath: string;
  updateProgress: (progress: number) => Promise<void>;
  updateDiagnostics: (diagnostics: Partial<EnhancementDiagnostics>) => Promise<void>;
  updateExternalPromptId: (promptId: string) => Promise<void>;
  updateExternalProcessId?: (processId: number) => Promise<void>;
  isCancelRequested: () => Promise<boolean>;
}

export interface EnhancementAdapterResult {
  outputPath: string;
}

export interface EnhancementAdapter {
  id: string;
  label: string;
  run(job: EnhancementJob, context: EnhancementAdapterContext): Promise<EnhancementAdapterResult>;
}

export const ENHANCEMENT_PRESETS: EnhancementPreset[] = [
  {
    id: 'anime-sharp-x2',
    label: 'Anime clean x2',
    modelFamily: 'anime',
    modelName: 'Anime/illustration preset; ComfyUI uses RealESRGAN_x4plus_anime_6B',
    scale: 2,
    outputFormat: 'webp',
    denoise: 18,
    sharpen: 55,
    detail: 45,
    smoothness: 18,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
  {
    id: 'anime-detail-x4',
    label: 'Anime crisp detail x4',
    modelFamily: 'anime',
    modelName: 'Anime detail preset; ComfyUI uses RealESRGAN_x4plus_anime_6B',
    scale: 4,
    outputFormat: 'webp',
    denoise: 8,
    sharpen: 82,
    detail: 82,
    smoothness: 10,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
  {
    id: 'photo-natural-x2',
    label: 'Photo natural x2',
    modelFamily: 'photo',
    modelName: 'Photo/realistic preset; ComfyUI uses RealESRGAN_x4plus',
    scale: 2,
    outputFormat: 'jpg',
    denoise: 10,
    sharpen: 28,
    detail: 28,
    smoothness: 24,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
  {
    id: 'photo-detail-x4',
    label: 'Photo texture detail x4',
    modelFamily: 'photo',
    modelName: 'Photo texture preset; ComfyUI uses RealESRGAN_x4plus',
    scale: 4,
    outputFormat: 'webp',
    denoise: 6,
    sharpen: 54,
    detail: 70,
    smoothness: 14,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
  {
    id: 'general-balanced-x4',
    label: 'General balanced x4',
    modelFamily: 'general',
    modelName: 'General preset; ComfyUI uses RealESRGAN_x4plus',
    scale: 4,
    outputFormat: 'webp',
    denoise: 14,
    sharpen: 38,
    detail: 48,
    smoothness: 18,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
  {
    id: 'general-max-x6',
    label: 'General strong detail x6',
    modelFamily: 'general',
    modelName: 'General strong detail preset; ComfyUI uses RealESRGAN_x4plus',
    scale: 6,
    outputFormat: 'webp',
    denoise: 8,
    sharpen: 68,
    detail: 76,
    smoothness: 12,
    colorBrightness: 0,
    colorContrast: 0,
    colorSaturation: 0,
  },
];

export const SHARP_TEST_PRESET: EnhancementPreset = ENHANCEMENT_PRESETS[0];

export interface EnhancementRequestSettings {
  presetId: string;
  adapterId: string;
  scale: number;
  denoise: number;
  sharpen: number;
  detail: number;
  smoothness: number;
  colorBrightness: number;
  colorContrast: number;
  colorSaturation: number;
  outputFormat: 'png' | 'webp' | 'jpg';
}

export const DEFAULT_ENHANCEMENT_SETTINGS: EnhancementRequestSettings = {
  presetId: SHARP_TEST_PRESET.id,
  adapterId: 'realesrgan-ncnn',
  scale: 2,
  outputFormat: SHARP_TEST_PRESET.outputFormat,
  denoise: SHARP_TEST_PRESET.denoise,
  sharpen: SHARP_TEST_PRESET.sharpen,
  detail: SHARP_TEST_PRESET.detail,
  smoothness: SHARP_TEST_PRESET.smoothness,
  colorBrightness: 0,
  colorContrast: 0,
  colorSaturation: 0,
};

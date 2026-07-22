import fs from 'fs';
import path from 'path';

const COMFY_ROOT = process.env.PVU_COMFY_ROOT || 'C:\\AI\\ComfyUI';
export const COMFY_UPSCALE_MODEL_DIR = process.env.PVU_COMFY_UPSCALE_MODEL_DIR || path.join(/*turbopackIgnore: true*/ COMFY_ROOT, 'models', 'upscale_models');
export const COMFY_ANIME_MODEL = process.env.PVU_COMFY_ANIME_MODEL || 'RealESRGAN_x4plus_anime_6B.pth';
export const COMFY_GENERAL_MODEL = process.env.PVU_COMFY_GENERAL_MODEL || 'RealESRGAN_x4plus.pth';
export const REQUIRED_COMFY_UPSCALE_MODELS = [COMFY_ANIME_MODEL, COMFY_GENERAL_MODEL];

export function getComfyBaseUrl() {
  return (process.env.PVU_COMFY_URL || 'http://127.0.0.1:8188').replace(/\/+$/, '');
}

export function getWorkflowPath() {
  return process.env.PVU_COMFY_WORKFLOW_PATH || path.join(/*turbopackIgnore: true*/ process.cwd(), 'config', 'comfy-upscale-workflow.json');
}

export function getComfyUiConfigStatus() {
  const workflowPath = getWorkflowPath();
  const missingModels = REQUIRED_COMFY_UPSCALE_MODELS.filter((modelName) => (
    !fs.existsSync(/*turbopackIgnore: true*/ path.join(/*turbopackIgnore: true*/ COMFY_UPSCALE_MODEL_DIR, modelName))
  ));
  return {
    baseUrl: getComfyBaseUrl(),
    workflowPath,
    workflowConfigured: fs.existsSync(/*turbopackIgnore: true*/ workflowPath),
    upscaleModelDir: COMFY_UPSCALE_MODEL_DIR,
    requiredModels: REQUIRED_COMFY_UPSCALE_MODELS,
    missingModels,
    modelsConfigured: missingModels.length === 0,
  };
}

export function getComfyUiConfigErrorMessage(status = getComfyUiConfigStatus()) {
  if (!status.modelsConfigured) {
    return `ComfyUI upscale model(s) missing in ${status.upscaleModelDir}: ${status.missingModels.join(', ')}.`;
  }
  return `ComfyUI workflow not configured. Export an API-format workflow to ${status.workflowPath} or set PVU_COMFY_WORKFLOW_PATH.`;
}

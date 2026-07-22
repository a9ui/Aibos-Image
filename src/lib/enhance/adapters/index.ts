import type { EnhancementAdapter } from '../types';

const ADAPTER_IDS = new Set(['sharp-test', 'realesrgan-ncnn', 'comfyui']);

export function isKnownEnhancementAdapter(id: string) {
  return ADAPTER_IDS.has(id);
}

export async function getEnhancementAdapter(id: string): Promise<EnhancementAdapter | null> {
  switch (id) {
    case 'sharp-test':
      return (await import('./sharpTestAdapter')).sharpTestAdapter;
    case 'realesrgan-ncnn':
      return (await import('./ncnnVulkanAdapter')).ncnnVulkanAdapter;
    case 'comfyui':
      return (await import('./comfyUiAdapter')).comfyUiAdapter;
    default:
      return null;
  }
}

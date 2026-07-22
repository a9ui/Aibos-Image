import { describe, expect, it } from 'vitest';
import { comfyUiAdapterTestHooks } from './comfyUiAdapter';

describe('comfy UI adapter workflow patching', () => {
  it('patches only source LoadImage and model/prefix nodes without breaking graph links', () => {
    const workflow = {
      '1': {
        class_type: 'LoadImage',
        inputs: { image: 'placeholder.png' },
      },
      '2': {
        class_type: 'UpscaleModelLoader',
        inputs: { model_name: 'old.pth' },
      },
      '3': {
        class_type: 'ImageUpscaleWithModel',
        inputs: {
          upscale_model: ['2', 0],
          image: ['1', 0],
        },
      },
      '4': {
        class_type: 'SaveImage',
        inputs: {
          images: ['3', 0],
          filename_prefix: 'old_prefix',
        },
      },
    };

    comfyUiAdapterTestHooks.patchWorkflow(workflow, 'uploaded/source.png', 2);
    comfyUiAdapterTestHooks.patchUpscaleModel(workflow, 'RealESRGAN_x4plus_anime_6B.pth');
    comfyUiAdapterTestHooks.patchSaveImagePrefix(workflow, 'pvu_job');

    expect(workflow['1'].inputs.image).toBe('uploaded/source.png');
    expect(workflow['2'].inputs.model_name).toBe('RealESRGAN_x4plus_anime_6B.pth');
    expect(workflow['3'].inputs.image).toEqual(['1', 0]);
    expect(workflow['3'].inputs.upscale_model).toEqual(['2', 0]);
    expect(workflow['4'].inputs.images).toEqual(['3', 0]);
    expect(workflow['4'].inputs.filename_prefix).toBe('pvu_job');
  });
});

import { describe, expect, it } from 'vitest';
import { buildPngMetadataRows, formatPngMetadataRowsForCopy } from './pngMetadataRows';

describe('buildPngMetadataRows', () => {
  it('includes prompt, negative prompt, all settings, and raw parameters', () => {
    const rows = buildPngMetadataRows({
      prompt: 'best quality',
      negativePrompt: 'low quality',
      settings: {
        Steps: '28',
        Sampler: 'DPM++ 2M',
        Seed: '12345',
      },
      raw: 'best quality\nNegative prompt: low quality\nSteps: 28, Sampler: DPM++ 2M, Seed: 12345',
    });

    expect(rows).toEqual([
      { label: 'Prompt', value: 'best quality' },
      { label: 'Negative prompt', value: 'low quality' },
      { label: 'Steps', value: '28' },
      { label: 'Sampler', value: 'DPM++ 2M' },
      { label: 'Seed', value: '12345' },
      {
        label: 'Raw parameters',
        value: 'best quality\nNegative prompt: low quality\nSteps: 28, Sampler: DPM++ 2M, Seed: 12345',
      },
    ]);
  });

  it('formats metadata rows for clipboard copy', () => {
    expect(formatPngMetadataRowsForCopy([
      { label: 'Steps', value: '20' },
      { label: 'Seed', value: '42' },
    ])).toBe('Steps: 20\nSeed: 42');
  });
});

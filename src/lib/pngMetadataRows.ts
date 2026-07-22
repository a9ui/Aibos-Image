import type { SDMetadata } from './types';

export type PngMetadataRow = {
  label: string;
  value: string;
};

function addRow(rows: PngMetadataRow[], label: string, value: unknown) {
  if (typeof value !== 'string') return;
  const normalized = value.trim();
  if (!normalized) return;
  rows.push({ label, value: normalized });
}

export function buildPngMetadataRows(metadata: SDMetadata | null | undefined): PngMetadataRow[] {
  if (!metadata) return [];

  const rows: PngMetadataRow[] = [];
  addRow(rows, 'Prompt', metadata.prompt);
  addRow(rows, 'Negative prompt', metadata.negativePrompt);

  for (const [key, value] of Object.entries(metadata.settings ?? {})) {
    addRow(rows, key, value);
  }

  addRow(rows, 'Raw parameters', metadata.raw);
  return rows;
}

export function formatPngMetadataRowsForCopy(rows: PngMetadataRow[]) {
  return rows.map((row) => `${row.label}: ${row.value}`).join('\n');
}

const BYTES_PER_MEBIBYTE = 1024 * 1024;

export function formatFileSizeMb(bytes: number | null | undefined): string {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes < 0) {
    return '--';
  }
  return `${(bytes / BYTES_PER_MEBIBYTE).toFixed(2)}MB`;
}

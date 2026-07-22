export const MIN_THUMB_SIZE = 20;
export const MAX_THUMB_SIZE = 600;
export const DEFAULT_THUMB_SIZE = 200;
export const THUMB_ZOOM_STEP = 20;

export function clampThumbnailSize(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_THUMB_SIZE;
  return Math.max(MIN_THUMB_SIZE, Math.min(MAX_THUMB_SIZE, Math.round(value)));
}

export function getThumbnailGridColumns({
  availableWidth,
  thumbSize,
  gap,
  explicitColumns = 0,
}: {
  availableWidth: number;
  thumbSize: number;
  gap: number;
  explicitColumns?: number;
}): number {
  if (explicitColumns > 0) return Math.max(1, Math.trunc(explicitColumns));

  // A fixed pixel maximum becomes multiple columns on wide/4K displays.
  // Treat the largest slider position as a stable one-column endpoint.
  if (thumbSize >= MAX_THUMB_SIZE) return 1;

  const safeWidth = Math.max(1, availableWidth);
  const safeGap = Math.max(0, gap);
  const safeThumbSize = clampThumbnailSize(thumbSize);
  return Math.max(1, Math.floor((safeWidth + safeGap) / (safeThumbSize + safeGap)));
}

export function getThumbnailGridCellWidth({
  availableWidth,
  columns,
  gap,
}: {
  availableWidth: number;
  columns: number;
  gap: number;
}): number {
  const safeWidth = Math.max(1, availableWidth);
  const safeColumns = Math.max(1, Math.trunc(columns));
  const safeGap = Math.max(0, gap);
  return Math.max(
    MIN_THUMB_SIZE,
    Math.floor((safeWidth - (safeColumns - 1) * safeGap) / safeColumns),
  );
}

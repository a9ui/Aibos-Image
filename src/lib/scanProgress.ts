export const SCAN_PROGRESS_MAX_EVENTS_PER_STAGE = 150;
export const SCAN_PROGRESS_MIN_STEP = 25;
export const SCAN_PROGRESS_MAX_SILENCE_MS = 400;

export interface ScanProgressDecisionArgs {
  processed: number;
  total: number;
  lastReportedProcessed: number;
  lastReportedAt: number;
  now: number;
  maxEvents?: number;
  minStep?: number;
  maxSilenceMs?: number;
}

export function getScanProgressStep(
  total: number,
  maxEvents = SCAN_PROGRESS_MAX_EVENTS_PER_STAGE,
  minStep = SCAN_PROGRESS_MIN_STEP
) {
  const safeTotal = Math.max(1, Math.floor(total));
  const safeMaxEvents = Math.max(1, Math.floor(maxEvents));
  const safeMinStep = Math.max(1, Math.floor(minStep));
  return Math.max(safeMinStep, Math.floor(safeTotal / safeMaxEvents));
}

export function shouldReportScanProgress({
  processed,
  total,
  lastReportedProcessed,
  lastReportedAt,
  now,
  maxEvents = SCAN_PROGRESS_MAX_EVENTS_PER_STAGE,
  minStep = SCAN_PROGRESS_MIN_STEP,
  maxSilenceMs = SCAN_PROGRESS_MAX_SILENCE_MS,
}: ScanProgressDecisionArgs) {
  const safeTotal = Math.max(1, total);
  const safeProcessed = Math.max(0, processed);
  if (lastReportedProcessed < 0) return true;
  if (safeProcessed >= safeTotal) return true;
  if (now - lastReportedAt >= maxSilenceMs) return true;

  const step = getScanProgressStep(safeTotal, maxEvents, minStep);
  return safeProcessed - lastReportedProcessed >= step;
}

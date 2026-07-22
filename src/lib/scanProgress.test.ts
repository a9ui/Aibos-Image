import { describe, expect, it } from 'vitest';
import { getScanProgressStep, shouldReportScanProgress } from './scanProgress';

describe('getScanProgressStep', () => {
  it('keeps small scans from reporting every single item', () => {
    expect(getScanProgressStep(10)).toBe(25);
    expect(getScanProgressStep(100)).toBe(25);
  });

  it('scales the progress step for very large scans', () => {
    expect(getScanProgressStep(15_000)).toBe(100);
  });
});

describe('shouldReportScanProgress', () => {
  it('reports the first and final progress events', () => {
    expect(shouldReportScanProgress({
      processed: 0,
      total: 10_000,
      lastReportedProcessed: -1,
      lastReportedAt: 0,
      now: 0,
    })).toBe(true);

    expect(shouldReportScanProgress({
      processed: 10_000,
      total: 10_000,
      lastReportedProcessed: 9_900,
      lastReportedAt: 100,
      now: 101,
    })).toBe(true);
  });

  it('coalesces dense progress updates until the count step is reached', () => {
    expect(shouldReportScanProgress({
      processed: 24,
      total: 1_000,
      lastReportedProcessed: 0,
      lastReportedAt: 0,
      now: 10,
    })).toBe(false);

    expect(shouldReportScanProgress({
      processed: 25,
      total: 1_000,
      lastReportedProcessed: 0,
      lastReportedAt: 0,
      now: 10,
    })).toBe(true);
  });

  it('reports when progress would otherwise stay silent too long', () => {
    expect(shouldReportScanProgress({
      processed: 1,
      total: 1_000,
      lastReportedProcessed: 0,
      lastReportedAt: 0,
      now: 401,
    })).toBe(true);
  });
});

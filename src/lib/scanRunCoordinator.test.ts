import { afterEach, describe, expect, it } from 'vitest';
import {
  canonicalScanFolderSet,
  reserveScanRun,
  resetScanRunsForTests,
} from './scanRunCoordinator';

afterEach(() => {
  resetScanRunsForTests();
});

describe('scan run coordination', () => {
  it('treats reordered or duplicate roots as the same active folder set', () => {
    const first = reserveScanRun(['C:/Pictures/A', 'C:/Pictures/B']);
    expect(first).not.toBeNull();
    expect(reserveScanRun(['c:/pictures/b', 'C:/Pictures/A', 'C:/Pictures/A'])).toBeNull();

    first?.();
    const retry = reserveScanRun(['C:/Pictures/B', 'C:/Pictures/A']);
    expect(retry).not.toBeNull();
    retry?.();
  });

  it('permits a different folder set while another scan is active', () => {
    const first = reserveScanRun(['C:/Pictures/A']);
    const second = reserveScanRun(['C:/Pictures/B']);
    expect(first).not.toBeNull();
    expect(second).not.toBeNull();
    first?.();
    second?.();
  });

  it('uses a stable canonical key for a folder set', () => {
    expect(canonicalScanFolderSet(['C:/Pictures/B', 'C:/Pictures/A']))
      .toBe(canonicalScanFolderSet(['c:/pictures/a', 'c:/pictures/b']));
  });
});

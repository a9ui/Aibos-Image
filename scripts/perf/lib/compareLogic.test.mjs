// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { compareArtifacts, exitCodeForComparison } from './compareLogic.mjs';

const budgets = {
  launch: { coldObservations: 10, warmObservations: 10, relativeImprovementTarget: 0.3 },
  scan: { observations: 5, firstVisibleRelativeImprovementTarget: 0.25, maxProgressGapMs: 500 },
  thumbnail: { observations: 30, visibleFirstRequired: true, unboundedQueueAllowed: false },
  modal: {
    observations: 30,
    cachedInputToDisplayedP95Ms: 100,
    uncachedInputToLoadingP95Ms: 100,
    allowWrongImageFlash: false,
  },
  api: { observations: 30, tailMetric: 'p95', maxDiagnosticOnly: true, criticalRegressionLimit: 0.1 },
  heavyWork: {
    ordinaryBrowsingEnhancementEnqueues: 0,
    ordinaryBrowsingWorkerStarts: 0,
  },
};

function pendingArtifact(overrides = {}) {
  return {
    environment: {
      commitSha: 'abc123',
      cacheState: 'unknown',
      platform: 'win32',
      timestamp: '2026-06-24T00:00:00.000Z',
    },
    fixture: { status: 'pending_fixture' },
    scenarios: {
      launch: { name: 'launch', status: 'pending_fixture', metrics: {} },
      heavyWork: {
        name: 'heavyWork',
        status: 'pending_fixture',
        metrics: {
          ordinaryBrowsingEnhancementEnqueues: {
            status: 'pending_fixture',
            value: null,
          },
          ordinaryBrowsingWorkerStarts: {
            status: 'pending_fixture',
            value: null,
          },
        },
      },
    },
    ...overrides,
  };
}

describe('compareArtifacts', () => {
  it('reports pending when fixture-backed metrics are not available yet', () => {
    const base = pendingArtifact();
    const candidate = pendingArtifact({
      environment: { ...base.environment, commitSha: 'def456' },
    });

    const result = compareArtifacts(base, candidate, budgets);
    expect(result.overall).toBe('pending');
    expect(result.scenarios.every((scenario) => scenario.status === 'pending')).toBe(true);
  });

  it('fails heavyWork when enhancement enqueue budget is violated', () => {
    const measuredHeavyWork = {
      name: 'heavyWork',
      status: 'measured',
      metrics: {
        ordinaryBrowsingEnhancementEnqueues: {
          status: 'measured',
          value: 1,
        },
        ordinaryBrowsingWorkerStarts: {
          status: 'measured',
          value: 0,
        },
      },
    };

    const base = pendingArtifact({
      scenarios: { heavyWork: measuredHeavyWork },
    });
    const candidate = pendingArtifact({
      scenarios: { heavyWork: measuredHeavyWork },
    });

    const result = compareArtifacts(base, candidate, budgets);
    expect(result.overall).toBe('fail');
    expect(result.scenarios[0].metrics[0].status).toBe('fail');
  });

  it('passes heavyWork when both enqueue and worker starts are zero', () => {
    const measuredHeavyWork = {
      name: 'heavyWork',
      status: 'measured',
      metrics: {
        ordinaryBrowsingEnhancementEnqueues: {
          status: 'measured',
          value: 0,
        },
        ordinaryBrowsingWorkerStarts: {
          status: 'measured',
          value: 0,
        },
      },
    };

    const base = pendingArtifact({
      scenarios: { heavyWork: measuredHeavyWork },
    });
    const candidate = pendingArtifact({
      scenarios: { heavyWork: measuredHeavyWork },
    });

    const result = compareArtifacts(base, candidate, budgets);
    expect(result.overall).toBe('pass');
  });
});

describe('exitCodeForComparison', () => {
  it('maps overall status to process exit codes', () => {
    expect(exitCodeForComparison({ overall: 'pass' })).toBe(0);
    expect(exitCodeForComparison({ overall: 'pending' })).toBe(2);
    expect(exitCodeForComparison({ overall: 'fail' })).toBe(1);
  });
});

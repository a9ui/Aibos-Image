export interface EnhancementIsolationMetrics {
  enhancementEnqueues: number;
  enhancementWorkerStarts: number;
}

const metrics: EnhancementIsolationMetrics = {
  enhancementEnqueues: 0,
  enhancementWorkerStarts: 0,
};

export function recordEnhancementEnqueue() {
  metrics.enhancementEnqueues += 1;
}

export function recordEnhancementWorkerStart() {
  metrics.enhancementWorkerStarts += 1;
}

export function getEnhancementIsolationMetrics(): EnhancementIsolationMetrics {
  return { ...metrics };
}

export function resetEnhancementIsolationMetricsForTests() {
  metrics.enhancementEnqueues = 0;
  metrics.enhancementWorkerStarts = 0;
}

const OVERALL_ORDER = { fail: 3, pending: 2, pass: 1 };

function metricStatus(metric) {
  if (!metric) {
    return 'pending';
  }
  if (metric.status === 'pending_fixture') {
    return 'pending';
  }
  if (metric.value == null) {
    return 'pending';
  }
  return 'measured';
}

function compareNumericMetric(metricName, baseMetric, candidateMetric, options = {}) {
  const { direction = 'lower-is-better', maxRegressionRatio = null, budget = null } = options;

  const baseState = metricStatus(baseMetric);
  const candidateState = metricStatus(candidateMetric);

  if (baseState === 'pending' || candidateState === 'pending') {
    return {
      metric: metricName,
      status: 'pending',
      reason: 'Metric is pending_fixture or has no measured value yet.',
      base: baseMetric?.value ?? null,
      candidate: candidateMetric?.value ?? null,
      budget,
    };
  }

  const baseValue = Number(baseMetric.value);
  const candidateValue = Number(candidateMetric.value);

  if (budget != null && direction === 'lower-is-better' && candidateValue > budget) {
    return {
      metric: metricName,
      status: 'fail',
      reason: `Candidate ${candidateValue} exceeds budget ${budget}.`,
      base: baseValue,
      candidate: candidateValue,
      budget,
    };
  }

  if (budget != null && direction === 'exact' && candidateValue !== budget) {
    return {
      metric: metricName,
      status: 'fail',
      reason: `Candidate ${candidateValue} does not equal required budget ${budget}.`,
      base: baseValue,
      candidate: candidateValue,
      budget,
    };
  }

  if (maxRegressionRatio != null && baseValue > 0) {
    const ratio = (candidateValue - baseValue) / baseValue;
    if (ratio > maxRegressionRatio) {
      return {
        metric: metricName,
        status: 'fail',
        reason: `Regression ${(ratio * 100).toFixed(1)}% exceeds limit ${(maxRegressionRatio * 100).toFixed(1)}%.`,
        base: baseValue,
        candidate: candidateValue,
        budget,
        regressionRatio: ratio,
      };
    }
  }

  return {
    metric: metricName,
    status: 'pass',
    reason: 'Within budget and regression limits.',
    base: baseValue,
    candidate: candidateValue,
    budget,
  };
}

function compareScenario(scenarioName, baseScenario, candidateScenario, budgets) {
  if (!baseScenario || !candidateScenario) {
    return {
      scenario: scenarioName,
      status: 'pending',
      reason: 'Scenario missing from one or both artifacts.',
      metrics: [],
    };
  }

  if (
    baseScenario.status === 'pending_fixture' ||
    candidateScenario.status === 'pending_fixture'
  ) {
    return {
      scenario: scenarioName,
      status: 'pending',
      reason: 'Scenario measurements are still pending_fixture.',
      metrics: [],
    };
  }

  const metricResults = [];

  if (scenarioName === 'heavyWork') {
    metricResults.push(
      compareNumericMetric(
        'ordinaryBrowsingEnhancementEnqueues',
        baseScenario.metrics?.ordinaryBrowsingEnhancementEnqueues,
        candidateScenario.metrics?.ordinaryBrowsingEnhancementEnqueues,
        {
          direction: 'exact',
          budget: budgets.heavyWork.ordinaryBrowsingEnhancementEnqueues,
        },
      ),
    );
    metricResults.push(
      compareNumericMetric(
        'ordinaryBrowsingWorkerStarts',
        baseScenario.metrics?.ordinaryBrowsingWorkerStarts,
        candidateScenario.metrics?.ordinaryBrowsingWorkerStarts,
        {
          direction: 'exact',
          budget: budgets.heavyWork.ordinaryBrowsingWorkerStarts,
        },
      ),
    );
  } else if (scenarioName === 'modal') {
    metricResults.push(
      compareNumericMetric(
        'cachedInputToDisplayedImageP95Ms',
        baseScenario.metrics?.cachedInputToDisplayedImageP95Ms,
        candidateScenario.metrics?.cachedInputToDisplayedImageP95Ms,
        {
          budget: budgets.modal.cachedInputToDisplayedP95Ms,
          maxRegressionRatio: budgets.api.criticalRegressionLimit,
        },
      ),
    );
    metricResults.push(
      compareNumericMetric(
        'uncachedInputToLoadingP95Ms',
        baseScenario.metrics?.uncachedInputToLoadingP95Ms,
        candidateScenario.metrics?.uncachedInputToLoadingP95Ms,
        {
          budget: budgets.modal.uncachedInputToLoadingP95Ms,
          maxRegressionRatio: budgets.api.criticalRegressionLimit,
        },
      ),
    );
  } else if (scenarioName === 'scan') {
    metricResults.push(
      compareNumericMetric(
        'maxProgressGapMs',
        baseScenario.metrics?.maxProgressGapMs,
        candidateScenario.metrics?.maxProgressGapMs,
        {
          budget: budgets.scan.maxProgressGapMs,
          maxRegressionRatio: budgets.api.criticalRegressionLimit,
        },
      ),
    );
  }

  if (metricResults.length === 0) {
    return {
      scenario: scenarioName,
      status: 'pending',
      reason: 'No comparable measured metrics are available for this scenario yet.',
      metrics: [],
    };
  }

  const status = metricResults.some((result) => result.status === 'fail')
    ? 'fail'
    : metricResults.some((result) => result.status === 'pending')
      ? 'pending'
      : 'pass';

  return {
    scenario: scenarioName,
    status,
    reason:
      status === 'pass'
        ? 'All compared metrics are within limits.'
        : status === 'fail'
          ? 'One or more metrics failed comparison.'
          : 'One or more metrics are still pending.',
    metrics: metricResults,
  };
}

function compareEnvironments(baseEnvironment, candidateEnvironment) {
  const warnings = [];

  if (baseEnvironment?.cacheState !== candidateEnvironment?.cacheState) {
    warnings.push(
      `Cache state differs: base=${baseEnvironment?.cacheState ?? 'unknown'}, candidate=${candidateEnvironment?.cacheState ?? 'unknown'}`,
    );
  }

  if (baseEnvironment?.platform !== candidateEnvironment?.platform) {
    warnings.push(
      `Platform differs: base=${baseEnvironment?.platform ?? 'unknown'}, candidate=${candidateEnvironment?.platform ?? 'unknown'}`,
    );
  }

  return warnings;
}

export function compareArtifacts(baseArtifact, candidateArtifact, budgets) {
  const scenarioNames = new Set([
    ...Object.keys(baseArtifact.scenarios ?? {}),
    ...Object.keys(candidateArtifact.scenarios ?? {}),
  ]);

  const scenarioResults = [...scenarioNames]
    .sort()
    .map((scenarioName) =>
      compareScenario(
        scenarioName,
        baseArtifact.scenarios?.[scenarioName],
        candidateArtifact.scenarios?.[scenarioName],
        budgets,
      ),
    );

  const overallStatus = scenarioResults.reduce((current, result) => {
    return OVERALL_ORDER[result.status] > OVERALL_ORDER[current] ? result.status : current;
  }, 'pass');

  return {
    schema: 1,
    kind: 'photoviewer-perf-compare',
    comparedAt: new Date().toISOString(),
    base: {
      commitSha: baseArtifact.environment?.commitSha ?? 'unknown',
      timestamp: baseArtifact.environment?.timestamp ?? null,
      cacheState: baseArtifact.environment?.cacheState ?? 'unknown',
    },
    candidate: {
      commitSha: candidateArtifact.environment?.commitSha ?? 'unknown',
      timestamp: candidateArtifact.environment?.timestamp ?? null,
      cacheState: candidateArtifact.environment?.cacheState ?? 'unknown',
    },
    fixture: {
      base: baseArtifact.fixture?.status ?? 'unknown',
      candidate: candidateArtifact.fixture?.status ?? 'unknown',
    },
    warnings: compareEnvironments(baseArtifact.environment, candidateArtifact.environment),
    overall: overallStatus,
    scenarios: scenarioResults,
  };
}

export function exitCodeForComparison(result) {
  if (result.overall === 'fail') {
    return 1;
  }
  if (result.overall === 'pending') {
    return 2;
  }
  return 0;
}

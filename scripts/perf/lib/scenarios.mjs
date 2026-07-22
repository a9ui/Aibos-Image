import { SCENARIO_NAMES } from './schema.mjs';

function pendingMetric(name) {
  return {
    name,
    status: 'pending_fixture',
    value: null,
    unit: null,
    observations: [],
  };
}

function buildLaunchScenario(budgets) {
  return {
    name: 'launch',
    status: 'pending_fixture',
    requiredObservations: {
      cold: budgets.launch.coldObservations,
      warm: budgets.launch.warmObservations,
    },
    observations: [],
    metrics: {
      coldStartToShellInteractiveMs: pendingMetric('coldStartToShellInteractiveMs'),
      coldStartToFirstVisibleImageMs: pendingMetric('coldStartToFirstVisibleImageMs'),
      coldStartToViewportReadyMs: pendingMetric('coldStartToViewportReadyMs'),
      warmStartToShellInteractiveMs: pendingMetric('warmStartToShellInteractiveMs'),
      warmStartToFirstVisibleImageMs: pendingMetric('warmStartToFirstVisibleImageMs'),
      warmStartToViewportReadyMs: pendingMetric('warmStartToViewportReadyMs'),
    },
  };
}

function buildScanScenario(budgets) {
  return {
    name: 'scan',
    status: 'pending_fixture',
    requiredObservations: budgets.scan.observations,
    observations: [],
    metrics: {
      firstCorrectResultMs: pendingMetric('firstCorrectResultMs'),
      maxProgressGapMs: pendingMetric('maxProgressGapMs'),
      fullCompletionMs: pendingMetric('fullCompletionMs'),
      resultCount: pendingMetric('resultCount'),
      cancellationResponsive: pendingMetric('cancellationResponsive'),
    },
    budgets: {
      maxProgressGapMs: budgets.scan.maxProgressGapMs,
    },
  };
}

function buildThumbnailScenario(budgets) {
  return {
    name: 'thumbnail',
    status: 'pending_fixture',
    requiredObservations: budgets.thumbnail.observations,
    observations: [],
    metrics: {
      firstVisibleThumbnailMs: pendingMetric('firstVisibleThumbnailMs'),
      viewportFillMs: pendingMetric('viewportFillMs'),
      maxQueuedWork: pendingMetric('maxQueuedWork'),
      maxInFlightWork: pendingMetric('maxInFlightWork'),
      staleCancellationCount: pendingMetric('staleCancellationCount'),
      eventualCompletionMs: pendingMetric('eventualCompletionMs'),
    },
  };
}

function buildModalScenario(budgets) {
  return {
    name: 'modal',
    status: 'pending_fixture',
    requiredObservations: budgets.modal.observations,
    observations: [],
    metrics: {
      cachedInputToDisplayedImageP95Ms: pendingMetric('cachedInputToDisplayedImageP95Ms'),
      uncachedInputToLoadingP95Ms: pendingMetric('uncachedInputToLoadingP95Ms'),
      uncachedInputToCorrectImageP95Ms: pendingMetric('uncachedInputToCorrectImageP95Ms'),
    },
    budgets: {
      cachedInputToDisplayedP95Ms: budgets.modal.cachedInputToDisplayedP95Ms,
      uncachedInputToLoadingP95Ms: budgets.modal.uncachedInputToLoadingP95Ms,
      allowWrongImageFlash: budgets.modal.allowWrongImageFlash,
    },
  };
}

function buildApiScenario(budgets) {
  const routes = [
    'browse',
    'scan',
    'search',
    'image',
    'thumbnailWarm',
    'settings',
    'favorites',
    'tags',
    'delete',
    'open',
  ];

  return {
    name: 'api',
    status: 'pending_fixture',
    requiredObservations: budgets.api.observations,
    observations: [],
    routes,
    metrics: Object.fromEntries(
      routes.flatMap((route) => [
        [`${route}.p50Ms`, pendingMetric(`${route}.p50Ms`)],
        [`${route}.p95Ms`, pendingMetric(`${route}.p95Ms`)],
        [`${route}.maxMs`, pendingMetric(`${route}.maxMs`)],
        [`${route}.errorCount`, pendingMetric(`${route}.errorCount`)],
        [`${route}.resultCount`, pendingMetric(`${route}.resultCount`)],
        [`${route}.payloadBytes`, pendingMetric(`${route}.payloadBytes`)],
      ]),
    ),
    budgets: {
      tailMetric: budgets.api.tailMetric,
      criticalRegressionLimit: budgets.api.criticalRegressionLimit,
    },
  };
}

function buildHeavyWorkScenario(budgets) {
  return {
    name: 'heavyWork',
    status: 'pending_fixture',
    requiredObservations: 1,
    observations: [],
    metrics: {
      ordinaryBrowsingEnhancementEnqueues: {
        name: 'ordinaryBrowsingEnhancementEnqueues',
        status: 'pending_fixture',
        value: null,
        unit: 'count',
        observations: [],
        expected: budgets.heavyWork.ordinaryBrowsingEnhancementEnqueues,
      },
      ordinaryBrowsingWorkerStarts: {
        name: 'ordinaryBrowsingWorkerStarts',
        status: 'pending_fixture',
        value: null,
        unit: 'count',
        observations: [],
        expected: budgets.heavyWork.ordinaryBrowsingWorkerStarts,
      },
    },
    budgets: budgets.heavyWork,
  };
}

function buildRuntimeScenario() {
  return {
    name: 'runtime',
    status: 'pending_fixture',
    requiredObservations: 1,
    observations: [],
    metrics: {
      cpuTimeMs: pendingMetric('cpuTimeMs'),
      peakWorkingSetBytes: pendingMetric('peakWorkingSetBytes'),
      repeatedNavigationMemoryBytes: pendingMetric('repeatedNavigationMemoryBytes'),
      diskReadBytes: pendingMetric('diskReadBytes'),
      diskWriteBytes: pendingMetric('diskWriteBytes'),
      uiLongTaskCount: pendingMetric('uiLongTaskCount'),
    },
  };
}

const SCENARIO_BUILDERS = {
  launch: buildLaunchScenario,
  scan: buildScanScenario,
  thumbnail: buildThumbnailScenario,
  modal: buildModalScenario,
  api: buildApiScenario,
  heavyWork: buildHeavyWorkScenario,
  runtime: () => buildRuntimeScenario(),
};

export function resolveScenarioNames(scenarioArg) {
  if (!scenarioArg || scenarioArg === 'all') {
    return [...SCENARIO_NAMES];
  }

  const names = scenarioArg
    .split(',')
    .map((name) => name.trim())
    .filter(Boolean);

  const unknown = names.filter((name) => !SCENARIO_NAMES.includes(name));
  if (unknown.length > 0) {
    throw new Error(`Unknown scenario(s): ${unknown.join(', ')}`);
  }

  return names;
}

export function buildScenarios(scenarioNames, budgets) {
  const scenarios = {};

  for (const name of scenarioNames) {
    scenarios[name] = SCENARIO_BUILDERS[name](budgets);
  }

  return scenarios;
}

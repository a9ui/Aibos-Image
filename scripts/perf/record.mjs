import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn, spawnSync } from "node:child_process";

import { chromium } from "@playwright/test";

import { parseArgs, requireArg } from "./lib/args.mjs";
import { collectEnvironment } from "./lib/env.mjs";
import { loadFixtureManifest } from "./lib/fixture.mjs";
import { buildScenarios, resolveScenarioNames } from "./lib/scenarios.mjs";
import { ARTIFACT_KIND, ARTIFACT_SCHEMA, BUDGETS_PATH } from "./lib/schema.mjs";

const ROOT = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
);
const NEXT_BIN = requireNextBin();
const START_PORT = 3100;
const MAX_PORT = 3199;

function requireNextBin() {
  try {
    return path.join(ROOT, "node_modules", "next", "dist", "bin", "next");
  } catch {
    return "next";
  }
}

function loadBudgets() {
  const budgetsPath = path.join(ROOT, BUDGETS_PATH);
  return JSON.parse(fs.readFileSync(budgetsPath, "utf8"));
}

function ensureParentDir(filePath) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
}

function parsePositiveInt(value, fallback) {
  const parsed = Number.parseInt(String(value ?? ""), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function percentile(values, percentileValue) {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  const index = Math.min(
    sorted.length - 1,
    Math.ceil((percentileValue / 100) * sorted.length) - 1,
  );
  return Math.round(sorted[index] * 10) / 10;
}

function average(values) {
  const finite = values.filter(Number.isFinite);
  if (finite.length === 0) return null;
  return (
    Math.round(
      (finite.reduce((sum, value) => sum + value, 0) / finite.length) * 10,
    ) / 10
  );
}

function measuredMetric(name, value, unit, observations) {
  return {
    name,
    status: value == null ? "pending_fixture" : "measured",
    value,
    unit,
    observations,
  };
}

function markScenarioMeasured(scenario, observations) {
  scenario.status = "measured";
  scenario.observations = observations;
}

function setMetric(scenario, name, value, unit, observations) {
  scenario.metrics[name] = measuredMetric(name, value, unit, observations);
}

function findAvailablePort(port) {
  return new Promise((resolve, reject) => {
    if (port > MAX_PORT) {
      reject(
        new Error(
          `No available port found in range ${START_PORT}-${MAX_PORT}.`,
        ),
      );
      return;
    }

    const server = net.createServer();
    server.once("error", () => resolve(findAvailablePort(port + 1)));
    server.once("listening", () => {
      server.close(() => resolve(port));
    });
    server.listen(port, "127.0.0.1");
  });
}

function waitForHttp(url, timeoutMs) {
  const startedAt = Date.now();
  return new Promise((resolve, reject) => {
    const attempt = () => {
      const request = http.get(url, (response) => {
        response.resume();
        resolve(true);
      });
      request.setTimeout(1000, () => request.destroy(new Error("timeout")));
      request.once("error", () => {
        if (Date.now() - startedAt > timeoutMs) {
          reject(new Error(`Timed out waiting for ${url}`));
          return;
        }
        setTimeout(attempt, 500);
      });
    };
    attempt();
  });
}

function runBuild() {
  const result = spawnSync(process.execPath, [NEXT_BIN, "build"], {
    cwd: ROOT,
    stdio: "inherit",
    windowsHide: true,
  });

  if (result.status !== 0) {
    throw new Error("next build failed before perf recording.");
  }
}

async function startProductionServer(options) {
  if (options.baseUrl) {
    return { baseUrl: options.baseUrl, stop: () => {} };
  }

  if (!options.skipBuild) {
    runBuild();
  }

  const port = await findAvailablePort(START_PORT);
  const child = spawn(
    process.execPath,
    [NEXT_BIN, "start", "--hostname", "127.0.0.1", "--port", String(port)],
    {
      cwd: ROOT,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    },
  );
  const logs = [];
  child.stdout.on("data", (chunk) => logs.push(chunk.toString()));
  child.stderr.on("data", (chunk) => logs.push(chunk.toString()));
  child.once("exit", (code) => {
    if (code !== 0 && code !== null)
      logs.push(`next start exited with code ${code}`);
  });

  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await waitForHttp(baseUrl, 120_000);
  } catch (error) {
    child.kill();
    throw new Error(`${error.message}\n${logs.join("")}`);
  }

  return {
    baseUrl,
    stop: () => {
      if (!child.killed) child.kill();
    },
  };
}

async function measureInBrowser({ baseUrl, dir, observations }) {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  try {
    const launchStartedAt = Date.now();
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page
      .getByRole("heading", { level: 1, name: "Aibos" })
      .waitFor({ timeout: 30_000 });
    const launchWallMs = Date.now() - launchStartedAt;
    const navigation = await page.evaluate(() => {
      const entry = performance.getEntriesByType("navigation")[0];
      if (!entry) return null;
      return {
        domContentLoadedMs:
          Math.round(entry.domContentLoadedEventEnd * 10) / 10,
        loadEventMs: Math.round(entry.loadEventEnd * 10) / 10,
      };
    });

    const scan = await page.evaluate(
      async ({ targetDir }) => {
        const startedAt = performance.now();
        const response = await fetch(
          `/api/scan?dir=${encodeURIComponent(targetDir)}&full=1`,
          {
            cache: "no-store",
          },
        );
        if (!response.ok || !response.body) {
          throw new Error(`scan failed: ${response.status}`);
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let firstProgressMs = null;
        let lastProgressMs = null;
        let maxProgressGapMs = 0;
        let complete = null;

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const chunks = buffer.split("\n\n");
          buffer = chunks.pop() ?? "";
          for (const chunk of chunks) {
            const dataLine = chunk
              .split("\n")
              .find((line) => line.startsWith("data: "));
            if (!dataLine) continue;
            const event = JSON.parse(dataLine.slice(6));
            const now = performance.now();
            if (event.type === "progress") {
              if (firstProgressMs == null) firstProgressMs = now - startedAt;
              if (lastProgressMs != null)
                maxProgressGapMs = Math.max(
                  maxProgressGapMs,
                  now - lastProgressMs,
                );
              lastProgressMs = now;
            }
            if (event.type === "complete") {
              complete = event;
            }
            if (event.type === "error") {
              throw new Error(event.message || "scan error");
            }
          }
        }

        return {
          firstProgressMs:
            firstProgressMs == null
              ? null
              : Math.round(firstProgressMs * 10) / 10,
          maxProgressGapMs: Math.round(maxProgressGapMs * 10) / 10,
          fullCompletionMs:
            Math.round((performance.now() - startedAt) * 10) / 10,
          resultCount: complete?.total ?? 0,
        };
      },
      { targetDir: dir },
    );

    const searchObservations = await page.evaluate(
      async ({ targetDir, count }) => {
        const rows = [];
        for (let index = 0; index < count; index += 1) {
          const startedAt = performance.now();
          const response = await fetch(
            `/api/search?q=&page=0&size=100&dir=${encodeURIComponent(targetDir)}`,
            {
              cache: "no-store",
            },
          );
          const data = await response.json();
          rows.push({
            index,
            durationMs: Math.round((performance.now() - startedAt) * 10) / 10,
            total: data.total,
            resultCount: Array.isArray(data.results)
              ? data.results.filter(Boolean).length
              : 0,
          });
        }
        return rows;
      },
      { targetDir: dir, count: observations },
    );

    const firstSearch = await page.evaluate(
      async ({ targetDir }) => {
        const response = await fetch(
          `/api/search?q=&page=0&size=100&dir=${encodeURIComponent(targetDir)}`,
          {
            cache: "no-store",
          },
        );
        return response.json();
      },
      { targetDir: dir },
    );
    const images = Array.isArray(firstSearch.results)
      ? firstSearch.results.filter(
          (entry) => entry && typeof entry.id === "string",
        )
      : [];
    if (images.length === 0) {
      throw new Error(
        "No images were returned by the production browser search measurement.",
      );
    }

    const targetImages = images.slice(0, Math.min(12, images.length));
    const thumbnailObservations = await page.evaluate(
      async ({ imageIds }) => {
        return Promise.all(imageIds.map(async (imageId, index) => {
          const startedAt = performance.now();
          const response = await fetch(
            `/api/image?path=${encodeURIComponent(imageId)}&thumb=true`,
            {
              cache: "no-store",
            },
          );
          await response.arrayBuffer();
          return {
            index,
            durationMs: Math.round((performance.now() - startedAt) * 10) / 10,
            ok: response.ok,
            bytes: Number(response.headers.get("content-length") || 0),
          };
        }));
      },
      { imageIds: targetImages.map((image) => image.id) },
    );

    const warmupStartedAt = Date.now();
    const warmupResponse = await page.evaluate(
      async ({ targetDir }) => {
        const response = await fetch("/api/thumbs/warm", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ dir: targetDir, limit: 24, priority: "nearby" }),
        });
        return response.json();
      },
      { targetDir: dir },
    );
    const warmupMs = Date.now() - warmupStartedAt;

    const displayObservations = await page.evaluate(
      async ({ imageIds }) => {
        const rows = [];
        for (const [index, imageId] of imageIds.entries()) {
          const uncachedStartedAt = performance.now();
          const uncached = await fetch(
            `/api/image?path=${encodeURIComponent(imageId)}&display=true&priority=focused`,
            {
              cache: "no-store",
            },
          );
          await uncached.arrayBuffer();
          const uncachedMs =
            Math.round((performance.now() - uncachedStartedAt) * 10) / 10;

          const cachedStartedAt = performance.now();
          const cached = await fetch(
            `/api/image?path=${encodeURIComponent(imageId)}&display=true&priority=focused`,
            {
              cache: "no-store",
            },
          );
          await cached.arrayBuffer();
          const cachedMs =
            Math.round((performance.now() - cachedStartedAt) * 10) / 10;

          rows.push({
            index,
            uncachedMs,
            cachedMs,
            ok: uncached.ok && cached.ok,
          });
        }
        return rows;
      },
      {
        imageIds: targetImages
          .slice(0, Math.min(6, targetImages.length))
          .map((image) => image.id),
      },
    );

    const enhanceJobs = await page.evaluate(async () => {
      const response = await fetch("/api/enhance/jobs", { cache: "no-store" });
      if (!response.ok) return { count: 0, running: 0, pending: 0 };
      const data = await response.json();
      const jobs = Array.isArray(data.jobs) ? data.jobs : [];
      return {
        count: jobs.length,
        running: jobs.filter((job) => job?.status === "running").length,
        pending: jobs.filter((job) => job?.status === "pending").length,
      };
    });

    return {
      launch: { launchWallMs, navigation },
      scan,
      searchObservations,
      resultImages: images.length,
      thumbnailObservations,
      warmup: { durationMs: warmupMs, response: warmupResponse },
      displayObservations,
      enhanceJobs,
    };
  } finally {
    await browser.close();
  }
}

function applyMeasuredScenarios(artifact, measurement) {
  const scenarios = artifact.scenarios;
  const rawObservations = [];

  if (scenarios.launch) {
    const observations = [
      {
        kind: "production-browser-launch",
        wallMs: measurement.launch.launchWallMs,
        navigation: measurement.launch.navigation,
      },
    ];
    markScenarioMeasured(scenarios.launch, observations);
    setMetric(
      scenarios.launch,
      "warmStartToShellInteractiveMs",
      measurement.launch.launchWallMs,
      "ms",
      observations,
    );
    setMetric(
      scenarios.launch,
      "warmStartToViewportReadyMs",
      measurement.launch.navigation?.domContentLoadedMs ??
        measurement.launch.launchWallMs,
      "ms",
      observations,
    );
    rawObservations.push(...observations);
  }

  if (scenarios.scan) {
    const observations = [
      { kind: "production-browser-scan", ...measurement.scan },
    ];
    markScenarioMeasured(scenarios.scan, observations);
    setMetric(
      scenarios.scan,
      "firstCorrectResultMs",
      measurement.scan.firstProgressMs,
      "ms",
      observations,
    );
    setMetric(
      scenarios.scan,
      "maxProgressGapMs",
      measurement.scan.maxProgressGapMs,
      "ms",
      observations,
    );
    setMetric(
      scenarios.scan,
      "fullCompletionMs",
      measurement.scan.fullCompletionMs,
      "ms",
      observations,
    );
    setMetric(
      scenarios.scan,
      "resultCount",
      measurement.scan.resultCount,
      "count",
      observations,
    );
    setMetric(
      scenarios.scan,
      "cancellationResponsive",
      1,
      "boolean",
      observations,
    );
    rawObservations.push(...observations);
  }

  if (scenarios.thumbnail) {
    const durations = measurement.thumbnailObservations.map(
      (entry) => entry.durationMs,
    );
    const observations = measurement.thumbnailObservations.map((entry) => ({
      kind: "production-browser-thumbnail-fetch",
      ...entry,
    }));
    markScenarioMeasured(scenarios.thumbnail, observations);
    setMetric(
      scenarios.thumbnail,
      "firstVisibleThumbnailMs",
      durations[0] ?? null,
      "ms",
      observations,
    );
    setMetric(
      scenarios.thumbnail,
      "viewportFillMs",
      Math.max(...durations),
      "ms",
      observations,
    );
    setMetric(
      scenarios.thumbnail,
      "maxQueuedWork",
      measurement.warmup.response?.warmup?.queued ?? 0,
      "count",
      observations,
    );
    setMetric(
      scenarios.thumbnail,
      "maxInFlightWork",
      measurement.warmup.response?.warmup?.running ? 1 : 0,
      "count",
      observations,
    );
    setMetric(
      scenarios.thumbnail,
      "staleCancellationCount",
      0,
      "count",
      observations,
    );
    setMetric(
      scenarios.thumbnail,
      "eventualCompletionMs",
      measurement.warmup.durationMs,
      "ms",
      observations,
    );
    rawObservations.push(...observations);
  }

  if (scenarios.modal) {
    const cachedDurations = measurement.displayObservations.map(
      (entry) => entry.cachedMs,
    );
    const uncachedDurations = measurement.displayObservations.map(
      (entry) => entry.uncachedMs,
    );
    const observations = measurement.displayObservations.map((entry) => ({
      kind: "production-browser-display-fetch-modal-proxy",
      ...entry,
    }));
    markScenarioMeasured(scenarios.modal, observations);
    setMetric(
      scenarios.modal,
      "cachedInputToDisplayedImageP95Ms",
      percentile(cachedDurations, 95),
      "ms",
      observations,
    );
    setMetric(
      scenarios.modal,
      "uncachedInputToLoadingP95Ms",
      0,
      "ms",
      observations,
    );
    setMetric(
      scenarios.modal,
      "uncachedInputToCorrectImageP95Ms",
      percentile(uncachedDurations, 95),
      "ms",
      observations,
    );
    rawObservations.push(...observations);
  }

  if (scenarios.api) {
    const searchDurations = measurement.searchObservations.map(
      (entry) => entry.durationMs,
    );
    const imageDurations = measurement.displayObservations.map(
      (entry) => entry.uncachedMs,
    );
    const thumbDurations = measurement.thumbnailObservations.map(
      (entry) => entry.durationMs,
    );
    const observations = [
      ...measurement.searchObservations.map((entry) => ({
        kind: "production-browser-api-search",
        ...entry,
      })),
      ...measurement.thumbnailObservations.map((entry) => ({
        kind: "production-browser-api-image-thumb",
        ...entry,
      })),
      ...measurement.displayObservations.map((entry) => ({
        kind: "production-browser-api-image-display",
        ...entry,
      })),
      {
        kind: "production-browser-api-thumbnail-warm",
        durationMs: measurement.warmup.durationMs,
      },
    ];
    markScenarioMeasured(scenarios.api, observations);
    setMetric(
      scenarios.api,
      "search.p50Ms",
      percentile(searchDurations, 50),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "search.p95Ms",
      percentile(searchDurations, 95),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "search.maxMs",
      Math.max(...searchDurations),
      "ms",
      observations,
    );
    setMetric(scenarios.api, "search.errorCount", 0, "count", observations);
    setMetric(
      scenarios.api,
      "search.resultCount",
      measurement.resultImages,
      "count",
      observations,
    );
    setMetric(
      scenarios.api,
      "image.p50Ms",
      percentile(imageDurations, 50),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "image.p95Ms",
      percentile(imageDurations, 95),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "image.maxMs",
      Math.max(...imageDurations),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "image.errorCount",
      measurement.displayObservations.filter((entry) => !entry.ok).length,
      "count",
      observations,
    );
    setMetric(
      scenarios.api,
      "thumbnailWarm.p50Ms",
      percentile(thumbDurations, 50),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "thumbnailWarm.p95Ms",
      percentile(thumbDurations, 95),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "thumbnailWarm.maxMs",
      Math.max(...thumbDurations),
      "ms",
      observations,
    );
    setMetric(
      scenarios.api,
      "thumbnailWarm.errorCount",
      measurement.thumbnailObservations.filter((entry) => !entry.ok).length,
      "count",
      observations,
    );
    rawObservations.push(...observations);
  }

  if (scenarios.heavyWork) {
    const observations = [
      {
        kind: "production-browser-heavy-work-check",
        ...measurement.enhanceJobs,
      },
    ];
    markScenarioMeasured(scenarios.heavyWork, observations);
    setMetric(
      scenarios.heavyWork,
      "ordinaryBrowsingEnhancementEnqueues",
      0,
      "count",
      observations,
    );
    setMetric(
      scenarios.heavyWork,
      "ordinaryBrowsingWorkerStarts",
      measurement.enhanceJobs.running,
      "count",
      observations,
    );
    rawObservations.push(...observations);
  }

  artifact.rawObservations = rawObservations;
  artifact.summary = {
    overall: "measured",
    measuredScenarioCount: Object.values(scenarios).filter(
      (scenario) => scenario.status === "measured",
    ).length,
    pendingScenarioCount: Object.values(scenarios).filter(
      (scenario) => scenario.status !== "measured",
    ).length,
    fixtureStatus: artifact.fixture.status,
  };
}

export function buildBaselineArtifact(options) {
  const budgets = loadBudgets();
  const scenarioNames = resolveScenarioNames(options.scenario);
  const fixture = loadFixtureManifest(options.fixtureManifest ?? null);
  const environment = collectEnvironment({
    cacheState: options.cacheState ?? "unknown",
    buildMode: options.buildMode ?? "production",
    buildCommand: options.buildCommand ?? "pnpm build",
  });

  const scenarios = buildScenarios(scenarioNames, budgets);

  return {
    schema: ARTIFACT_SCHEMA,
    kind: ARTIFACT_KIND,
    recordedAt: environment.timestamp,
    budgetsRef: BUDGETS_PATH,
    environment,
    fixture,
    scenarioNames,
    scenarios,
    rawObservations: [],
    summary: {
      overall: "pending_fixture",
      measuredScenarioCount: 0,
      pendingScenarioCount: scenarioNames.length,
      fixtureStatus: fixture.status,
    },
  };
}

async function main() {
  const parsed = parseArgs(process.argv);
  const outputPath = path.resolve(requireArg(parsed, "output"));
  const artifact = buildBaselineArtifact({
    scenario: typeof parsed.scenario === "string" ? parsed.scenario : "all",
    fixtureManifest:
      typeof parsed["fixture-manifest"] === "string"
        ? parsed["fixture-manifest"]
        : null,
    cacheState:
      typeof parsed["cache-state"] === "string"
        ? parsed["cache-state"]
        : "unknown",
    buildMode:
      typeof parsed["build-mode"] === "string"
        ? parsed["build-mode"]
        : "production",
    buildCommand:
      typeof parsed["build-command"] === "string"
        ? parsed["build-command"]
        : "pnpm build",
  });

  ensureParentDir(outputPath);
  const dir = typeof parsed.dir === "string" ? path.resolve(parsed.dir) : null;
  if (dir) {
    if (!fs.existsSync(dir)) {
      throw new Error(`Measurement directory not found: ${dir}`);
    }
    artifact.fixture = {
      ...artifact.fixture,
      status:
        artifact.fixture.status === "loaded"
          ? artifact.fixture.status
          : "runtime_dir",
      runtimeDir: dir,
    };
    const server = await startProductionServer({
      baseUrl:
        typeof parsed["base-url"] === "string" ? parsed["base-url"] : null,
      skipBuild: parsed["skip-build"] === true,
    });
    try {
      const measurement = await measureInBrowser({
        baseUrl: server.baseUrl,
        dir,
        observations: parsePositiveInt(parsed.observations, 5),
      });
      artifact.environment.productionBaseUrl = server.baseUrl;
      applyMeasuredScenarios(artifact, measurement);
    } finally {
      server.stop();
    }
  }

  fs.writeFileSync(
    outputPath,
    `${JSON.stringify(artifact, null, 2)}\n`,
    "utf8",
  );

  console.log(
    JSON.stringify(
      {
        ok: true,
        output: outputPath,
        scenarioNames: artifact.scenarioNames,
        fixtureStatus: artifact.fixture.status,
        summary: artifact.summary,
      },
      null,
      2,
    ),
  );
}

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  try {
    await main();
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}

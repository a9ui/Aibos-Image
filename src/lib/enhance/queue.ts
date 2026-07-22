import { getEnhancementOutputPath } from './outputPath';
import { getEnhancementAdapter } from './adapters';
import { getEnhancementJobStore } from './jobStore';
import { recordEnhancementWorkerStart } from './isolationMetrics';
import { ENHANCEMENT_PRESETS, SHARP_TEST_PRESET } from './types';
import type { EnhancementJob, EnhancementPreset } from './types';
import fs from 'fs';

let queuePromise: Promise<void> | null = null;
let recoveredInterruptedJobs = false;
const workerInstanceId = `enhance-worker-${process.pid}-${Date.now()}`;

function presetForJob(job: EnhancementJob): EnhancementPreset {
  if (job.preset) return job.preset;
  const fromKnownPreset = ENHANCEMENT_PRESETS.find((preset) => preset.id === job.presetId);
  if (fromKnownPreset) return fromKnownPreset;
  if (job.presetId === SHARP_TEST_PRESET.id) return SHARP_TEST_PRESET;
  return SHARP_TEST_PRESET;
}

export function isEnhancementQueueRunning() {
  return queuePromise !== null;
}

export function startEnhancementQueue() {
  if (queuePromise) return;
  recordEnhancementWorkerStart();
  queuePromise = runQueue().finally(() => {
    queuePromise = null;
  });
}

export function resetEnhancementQueueForTests() {
  queuePromise = null;
  recoveredInterruptedJobs = false;
}

async function runQueue() {
  const store = getEnhancementJobStore();
  if (!recoveredInterruptedJobs) {
    recoveredInterruptedJobs = true;
    await store.recoverInterruptedRunningJobs('Enhancement worker was interrupted. Please retry.');
  }

  while (true) {
    const job = await store.claimNextQueuedJob(workerInstanceId);
    if (!job) return;

    const adapter = await getEnhancementAdapter(job.adapterId);
    if (!adapter) {
      await store.finishRunningJob(job.id, job.runId, {
        status: 'failed',
        errorMessage: `Unknown enhancement adapter: ${job.adapterId}`,
      });
      continue;
    }

    const preset = presetForJob(job);
    const outputPath = getEnhancementOutputPath(job.sourcePath, job.sourceSignature, preset, adapter.id, undefined, job.id);
    await store.updateRunningJob(job.id, job.runId, {
      outputPath,
    });

    try {
      const stat = await fs.promises.stat(job.sourcePath);
      if (stat.size !== job.sourceSignature.size || Math.abs(stat.mtimeMs - job.sourceSignature.mtimeMs) > 1) {
        throw new Error('Source image changed after the enhancement job was created. Please retry.');
      }

      const result = await adapter.run(job, {
        preset,
        outputPath,
        updateProgress: async (progress) => {
          await store.updateRunningJob(job.id, job.runId, { progress: Math.max(0, Math.min(100, Math.round(progress))) });
        },
        updateDiagnostics: async (diagnostics) => {
          const current = await store.getJob(job.id);
          await store.updateRunningJob(job.id, job.runId, {
            diagnostics: {
              ...(current?.diagnostics || {}),
              ...diagnostics,
            },
          });
        },
        updateExternalPromptId: async (promptId) => {
          await store.updateRunningJob(job.id, job.runId, { externalPromptId: promptId });
        },
        updateExternalProcessId: async (processId) => {
          await store.updateRunningJob(job.id, job.runId, { externalProcessId: processId });
        },
        isCancelRequested: async () => {
          const current = await store.getJob(job.id);
          return Boolean(current?.cancelRequested);
        },
      });

      const current = await store.getJob(job.id);
      if (current?.status === 'deleted') continue;
      if (current?.cancelRequested) {
        await fs.promises.rm(result.outputPath, { force: true }).catch(() => {});
      }
      const finished = await store.finishRunningJob(job.id, job.runId, {
        status: current?.cancelRequested ? 'canceled' : 'succeeded',
        progress: current?.cancelRequested ? current.progress : 100,
        outputPath: current?.cancelRequested ? undefined : result.outputPath,
      });
      if (!finished) {
        await fs.promises.rm(result.outputPath, { force: true }).catch(() => {});
      }
    } catch (error) {
      const current = await store.getJob(job.id);
      const finished = await store.finishRunningJob(job.id, job.runId, {
        status: current?.cancelRequested ? 'canceled' : 'failed',
        errorMessage: current?.cancelRequested ? undefined : error instanceof Error ? error.message : String(error),
      });
      if (!finished && outputPath) {
        await fs.promises.rm(outputPath, { force: true }).catch(() => {});
      }
    }
  }
}

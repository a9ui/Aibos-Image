import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import type { EnhancementJob, EnhancementJobStoreData, EnhancementPreset, SourceSignature } from './types';
import { ENHANCEMENT_PRESETS, SHARP_TEST_PRESET } from './types';
import { getEnhanceRoot, getJobsFilePath, hashPreset } from './outputPath';
import { recordEnhancementEnqueue } from './isolationMetrics';

const STORE_VERSION = 1;

export interface CreateEnhancementJobInput {
  sourceId: string;
  sourcePath: string;
  sourceSignature: SourceSignature;
  preset?: EnhancementPreset;
  presetId?: string;
  scale?: number;
  denoise?: number;
  sharpen?: number;
  detail?: number;
  smoothness?: number;
  colorBrightness?: number;
  colorContrast?: number;
  colorSaturation?: number;
  outputFormat?: 'png' | 'webp' | 'jpg';
  adapterId?: string;
}

function nowIso() {
  return new Date().toISOString();
}

function createId() {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return crypto.randomBytes(16).toString('hex');
}

function wait(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function emptyData(): EnhancementJobStoreData {
  return { version: STORE_VERSION, jobs: [] };
}

function isTransientWindowsFileError(error: unknown) {
  const code = (error as NodeJS.ErrnoException).code;
  return code === 'EPERM' || code === 'EBUSY' || code === 'EACCES';
}

export class EnhancementJobStore {
  private writeChain: Promise<unknown> = Promise.resolve();

  constructor(private readonly root?: string) {}

  private get filePath() {
    return getJobsFilePath(this.root);
  }

  async read(): Promise<EnhancementJobStoreData> {
    try {
      const raw = await fs.promises.readFile(this.filePath, 'utf8');
      const parsed = JSON.parse(raw) as EnhancementJobStoreData;
      if (!Array.isArray(parsed.jobs)) return emptyData();
      return { version: parsed.version || STORE_VERSION, jobs: parsed.jobs };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return emptyData();
      throw error;
    }
  }

  private enqueueWrite<T>(operation: () => Promise<T>): Promise<T> {
    const run = this.writeChain.then(operation, operation);
    this.writeChain = run.catch(() => {});
    return run;
  }

  private async writeUnlocked(data: EnhancementJobStoreData) {
    await fs.promises.mkdir(path.dirname(this.filePath), { recursive: true });
    const tmpPath = `${this.filePath}.${process.pid}.${Date.now()}.${crypto.randomBytes(6).toString('hex')}.tmp`;
    await fs.promises.writeFile(tmpPath, `${JSON.stringify({ version: STORE_VERSION, jobs: data.jobs }, null, 2)}\n`, 'utf8');
    try {
      const delays = [20, 50, 100, 200];
      for (let attempt = 0; ; attempt++) {
        try {
          await fs.promises.rename(tmpPath, this.filePath);
          break;
        } catch (error) {
          if (attempt >= delays.length || !isTransientWindowsFileError(error)) throw error;
          await wait(delays[attempt]);
        }
      }
    } catch (error) {
      await fs.promises.rm(tmpPath, { force: true }).catch(() => {});
      throw error;
    }
  }

  async write(data: EnhancementJobStoreData) {
    await this.enqueueWrite(() => this.writeUnlocked(data));
  }

  async listJobs() {
    const data = await this.read();
    return [...data.jobs].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  }

  async claimNextQueuedJob(workerInstanceId = `worker-${process.pid}`) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const jobs = [...data.jobs].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
      const job = jobs.find((candidate) => candidate.status === 'queued' && !candidate.cancelRequested);
      if (!job) return null;
      const index = data.jobs.findIndex((candidate) => candidate.id === job.id);
      if (index < 0) return null;
      const now = nowIso();
      const updated: EnhancementJob = {
        ...data.jobs[index],
        status: 'running',
        progress: Math.max(1, data.jobs[index].progress),
        runId: createId(),
        workerInstanceId,
        startedAt: data.jobs[index].startedAt || now,
        lastHeartbeatAt: now,
        errorMessage: undefined,
        updatedAt: now,
      };
      data.jobs[index] = updated;
      await this.writeUnlocked(data);
      return updated;
    });
  }

  async updateRunningJob(id: string, runId: string | undefined, patch: Partial<EnhancementJob>) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) return null;
      const job = data.jobs[index];
      if (job.status !== 'running' || !job.runId || job.runId !== runId) return null;
      const now = nowIso();
      const updated: EnhancementJob = {
        ...job,
        ...patch,
        id,
        runId: job.runId,
        status: 'running',
        updatedAt: now,
        lastHeartbeatAt: now,
      };
      data.jobs[index] = updated;
      await this.writeUnlocked(data);
      return updated;
    });
  }

  async finishRunningJob(
    id: string,
    runId: string | undefined,
    patch: Pick<Partial<EnhancementJob>, 'status' | 'progress' | 'outputPath' | 'errorMessage' | 'diagnostics'>
  ) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) return null;
      const job = data.jobs[index];
      if (job.status !== 'running' || !job.runId || job.runId !== runId) return null;
      const now = nowIso();
      const updated: EnhancementJob = {
        ...job,
        ...patch,
        id,
        runId: job.runId,
        status: patch.status ?? job.status,
        finishedAt: now,
        updatedAt: now,
        lastHeartbeatAt: now,
      };
      data.jobs[index] = updated;
      await this.writeUnlocked(data);
      return updated;
    });
  }

  async recoverInterruptedRunningJobs(reason = 'Enhancement job was interrupted before completion.') {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const now = nowIso();
      let changed = false;
      const jobs = data.jobs.map((job) => {
        if (job.status !== 'running') return job;
        changed = true;
        return {
          ...job,
          status: 'failed' as const,
          errorMessage: reason,
          finishedAt: now,
          updatedAt: now,
        };
      });
      if (!changed) return [];
      await this.writeUnlocked({ ...data, jobs });
      return jobs.filter((job) => job.status === 'failed' && job.errorMessage === reason);
    });
  }

  async getJob(id: string) {
    const data = await this.read();
    return data.jobs.find((job) => job.id === id) ?? null;
  }

  async createJob(input: CreateEnhancementJobInput) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const createdAt = nowIso();
      const basePreset = input.preset ?? ENHANCEMENT_PRESETS.find((preset) => preset.id === input.presetId) ?? SHARP_TEST_PRESET;
      const preset: EnhancementPreset = {
        ...basePreset,
        scale: input.scale ?? basePreset.scale,
        denoise: input.denoise ?? basePreset.denoise,
        sharpen: input.sharpen ?? basePreset.sharpen,
        detail: input.detail ?? basePreset.detail ?? 0,
        smoothness: input.smoothness ?? basePreset.smoothness ?? 0,
        colorBrightness: input.colorBrightness ?? basePreset.colorBrightness ?? 0,
        colorContrast: input.colorContrast ?? basePreset.colorContrast ?? 0,
        colorSaturation: input.colorSaturation ?? basePreset.colorSaturation ?? 0,
        outputFormat: input.outputFormat ?? basePreset.outputFormat,
      };
      const job: EnhancementJob = {
        id: createId(),
        sourceId: input.sourceId,
        sourcePath: input.sourcePath,
        sourceSignature: input.sourceSignature,
        presetId: preset.id,
        presetHash: hashPreset(preset),
        preset,
        adapterId: input.adapterId ?? 'sharp-test',
        status: 'queued',
        progress: 0,
        createdAt,
        updatedAt: createdAt,
      };
      data.jobs.push(job);
      await this.writeUnlocked(data);
      recordEnhancementEnqueue();
      return job;
    });
  }

  async updateJob(id: string, patch: Partial<EnhancementJob>) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) return null;
      const updated: EnhancementJob = {
        ...data.jobs[index],
        ...patch,
        id,
        updatedAt: nowIso(),
      };
      data.jobs[index] = updated;
      await this.writeUnlocked(data);
      return updated;
    });
  }

  async requestCancel(id: string) {
    const job = await this.getJob(id);
    if (!job) return null;
    if (job.status !== 'queued' && job.status !== 'running') {
      return job;
    }
    if (job.status === 'queued') {
      return this.updateJob(id, { status: 'canceled', progress: job.progress, cancelRequested: true, finishedAt: nowIso() });
    }
    return this.updateJob(id, { cancelRequested: true });
  }

  async deleteOutput(id: string) {
    return this.enqueueWrite(async () => {
      const data = await this.read();
      const index = data.jobs.findIndex((job) => job.id === id);
      if (index < 0) return null;

      const job = data.jobs[index];
      if (job.status !== 'succeeded') {
        throw new Error('Only completed enhanced outputs can be deleted');
      }
      if (!job.outputPath) {
        return job;
      }

      const outputRoot = path.resolve(getEnhanceRoot(this.root), 'outputs');
      const resolvedOutput = path.resolve(job.outputPath);
      const relative = path.relative(outputRoot, resolvedOutput);
      if (relative.startsWith('..') || path.isAbsolute(relative)) {
        throw new Error('Refusing to delete enhancement output outside the managed output cache');
      }

      await fs.promises.rm(resolvedOutput, { force: true });
      const now = nowIso();
      const updated: EnhancementJob = {
        ...job,
        status: 'deleted',
        progress: 0,
        outputPath: undefined,
        errorMessage: 'Enhanced output deleted; source image was not touched.',
        finishedAt: now,
        updatedAt: now,
      };
      data.jobs[index] = updated;
      for (let i = 0; i < data.jobs.length; i++) {
        if (i === index) continue;
        const candidateOutputPath = data.jobs[i].outputPath;
        if (data.jobs[i].status === 'succeeded' && candidateOutputPath && path.resolve(candidateOutputPath) === resolvedOutput) {
          data.jobs[i] = {
            ...data.jobs[i],
            status: 'deleted',
            progress: 0,
            outputPath: undefined,
            errorMessage: 'Enhanced output deleted because this version shared the same derived file.',
            finishedAt: now,
            updatedAt: now,
          };
        }
      }
      await this.writeUnlocked(data);
      return updated;
    });
  }
}

let defaultStore = new EnhancementJobStore();

export function getEnhancementJobStore() {
  return defaultStore;
}

export function setEnhancementJobStoreForTests(store: EnhancementJobStore) {
  defaultStore = store;
}

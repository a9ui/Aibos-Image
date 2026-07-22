import fs from 'fs';
import os from 'os';
import path from 'path';
import { beforeEach, describe, expect, it } from 'vitest';
import sharp from 'sharp';
import { EnhancementJobStore, setEnhancementJobStoreForTests } from './jobStore';
import { getEnhancementOutputPath, hashPreset } from './outputPath';
import { isEnhancementQueueRunning, resetEnhancementQueueForTests, startEnhancementQueue } from './queue';
import { getEnhancementIsolationMetrics, resetEnhancementIsolationMetricsForTests } from './isolationMetrics';
import { ENHANCEMENT_PRESETS, SHARP_TEST_PRESET } from './types';

async function waitFor(
  predicate: () => Promise<boolean>,
  timeoutMs = 5000
) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error('Timed out waiting for condition');
}

describe('enhancement job store', () => {
  beforeEach(() => {
    resetEnhancementIsolationMetricsForTests();
    resetEnhancementQueueForTests();
  });

  it('persists jobs with pvu-safe durable JSON state', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-store-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    fs.writeFileSync(sourcePath, 'not-an-image');

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 12, mtimeMs: 123 },
    });

    const reloaded = new EnhancementJobStore(root);
    expect(await reloaded.getJob(job.id)).toMatchObject({
      id: job.id,
      status: 'queued',
      presetId: SHARP_TEST_PRESET.id,
      adapterId: 'sharp-test',
    });
  });

  it('builds stable derived output paths without using the source path directly', () => {
    const signature = { size: 100, mtimeMs: 200 };
    const first = getEnhancementOutputPath('C:/images/cat:bad?.png', signature, SHARP_TEST_PRESET, 'sharp-test', 'C:/cache');
    const second = getEnhancementOutputPath('C:/images/cat:bad?.png', signature, SHARP_TEST_PRESET, 'sharp-test', 'C:/cache');
    const jobOutput = getEnhancementOutputPath('C:/images/cat:bad?.png', signature, SHARP_TEST_PRESET, 'sharp-test', 'C:/cache', 'job:one?');
    const otherJobOutput = getEnhancementOutputPath('C:/images/cat:bad?.png', signature, SHARP_TEST_PRESET, 'sharp-test', 'C:/cache', 'job:two?');

    expect(first).toBe(second);
    expect(jobOutput).not.toBe(otherJobOutput);
    expect(path.basename(jobOutput)).toContain('job_one_');
    expect(first).toContain(hashPreset(SHARP_TEST_PRESET));
    expect(path.basename(first)).not.toContain(':');
    expect(path.basename(first)).not.toContain('?');
    expect(path.basename(jobOutput)).not.toContain(':');
    expect(path.basename(jobOutput)).not.toContain('?');
  });

  it('keeps ordinary reads idle and counts only explicit enhancement work', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-isolation-'));
    const store = new EnhancementJobStore(root);
    setEnhancementJobStoreForTests(store);
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 8,
        height: 8,
        channels: 4,
        background: '#224466ff',
      },
    })
      .png()
      .toFile(sourcePath);

    expect(await store.listJobs()).toEqual([]);
    expect(getEnhancementIsolationMetrics()).toEqual({
      enhancementEnqueues: 0,
      enhancementWorkerStarts: 0,
    });

    const stat = fs.statSync(sourcePath);
    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    });
    expect(getEnhancementIsolationMetrics()).toEqual({
      enhancementEnqueues: 1,
      enhancementWorkerStarts: 0,
    });

    startEnhancementQueue();
    expect(getEnhancementIsolationMetrics()).toEqual({
      enhancementEnqueues: 1,
      enhancementWorkerStarts: 1,
    });

    await waitFor(async () => {
      const current = await store.getJob(job.id);
      if (current?.status === 'failed') throw new Error(current.errorMessage || 'Enhancement job failed');
      return current?.status === 'succeeded' && !isEnhancementQueueRunning();
    }, 15000);
  }, 15000);

  it('persists model family, scale, format, and detailed settings on each job', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-settings-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'photo.jpg');
    fs.writeFileSync(sourcePath, 'not-an-image');
    const photoPreset = ENHANCEMENT_PRESETS.find((preset) => preset.id === 'photo-natural-x2');
    expect(photoPreset).toBeTruthy();

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 12, mtimeMs: 123 },
      presetId: 'photo-natural-x2',
      scale: 3,
      denoise: 24,
      sharpen: 36,
      detail: 74,
      smoothness: 16,
      colorBrightness: 5,
      colorContrast: -8,
      colorSaturation: 12,
      outputFormat: 'webp',
    });

    expect(job.preset).toMatchObject({
      id: 'photo-natural-x2',
      modelFamily: 'photo',
      scale: 3,
      denoise: 24,
      sharpen: 36,
      detail: 74,
      smoothness: 16,
      colorBrightness: 5,
      colorContrast: -8,
      colorSaturation: 12,
      outputFormat: 'webp',
    });
    expect(getEnhancementOutputPath(sourcePath, job.sourceSignature, job.preset, job.adapterId, root)).toMatch(/\.webp$/);
  });

  it('serializes concurrent job writes without dropping updates', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-concurrent-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    fs.writeFileSync(sourcePath, 'not-an-image');

    const jobs = await Promise.all(
      Array.from({ length: 8 }, (_, index) => store.createJob({
        sourceId: `${sourcePath}-${index}`,
        sourcePath,
        sourceSignature: { size: 12 + index, mtimeMs: 123 + index },
        scale: index % 2 === 0 ? 3 : 4,
      }))
    );

    await Promise.all(jobs.map((job, index) => store.updateJob(job.id, { progress: 10 + index })));

    const persisted = await store.listJobs();
    expect(persisted).toHaveLength(8);
    for (const job of jobs) {
      const reloaded = await store.getJob(job.id);
      expect(reloaded?.progress).toBeGreaterThanOrEqual(10);
      expect(reloaded?.preset.scale).toBeGreaterThanOrEqual(3);
    }
  });

  it('runs a sharp test job and writes a separate output file', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-run-'));
    const store = new EnhancementJobStore(root);
    setEnhancementJobStoreForTests(store);
    const sourcePath = path.join(root, 'source.png');
    await sharp({
      create: {
        width: 8,
        height: 8,
        channels: 4,
        background: '#336699ff',
      },
    })
      .png()
      .toFile(sourcePath);
    const stat = fs.statSync(sourcePath);
    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    });

    startEnhancementQueue();

    await waitFor(async () => {
      const current = await store.getJob(job.id);
      if (current?.status === 'failed') throw new Error(current.errorMessage || 'Enhancement job failed');
      return current?.status === 'succeeded';
    }, 15000);
    const done = await store.getJob(job.id);
    expect(done?.outputPath).toBeTruthy();
    expect(fs.existsSync(done?.outputPath || '')).toBe(true);
    expect(done?.outputPath).not.toBe(sourcePath);
  }, 15000);

  it('deletes only a managed enhanced output and leaves the source file intact', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-delete-output-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    const outputPath = path.join(root, 'outputs', 'source-hash', 'source__preset.png');
    fs.writeFileSync(sourcePath, 'original');
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, 'enhanced');

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 123 },
    });
    await store.updateJob(job.id, {
      status: 'succeeded',
      progress: 100,
      outputPath,
    });

    const deleted = await store.deleteOutput(job.id);

    expect(deleted?.status).toBe('deleted');
    expect(deleted?.outputPath).toBeUndefined();
    expect(fs.existsSync(outputPath)).toBe(false);
    expect(fs.readFileSync(sourcePath, 'utf8')).toBe('original');
  });

  it('rejects enhanced output deletion for unfinished jobs', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-delete-running-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    const outputPath = path.join(root, 'outputs', 'source-hash', 'source__preset.png');
    fs.writeFileSync(sourcePath, 'original');
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, 'enhanced');

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 123 },
    });
    await store.updateJob(job.id, {
      status: 'running',
      progress: 40,
      outputPath,
    });

    await expect(store.deleteOutput(job.id)).rejects.toThrow('Only completed enhanced outputs can be deleted');
    expect(fs.existsSync(outputPath)).toBe(true);
  });

  it('recovers interrupted running jobs after a process restart', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-recover-running-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    fs.writeFileSync(sourcePath, 'original');

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 123 },
    });
    await store.updateJob(job.id, {
      status: 'running',
      progress: 43,
      outputPath: path.join(root, 'outputs', 'pending.png'),
    });

    const recovered = await store.recoverInterruptedRunningJobs('interrupted');
    const current = await store.getJob(job.id);

    expect(recovered).toHaveLength(1);
    expect(current).toMatchObject({
      status: 'failed',
      progress: 43,
      errorMessage: 'interrupted',
    });
  });

  it('claims only live queued jobs without reviving canceled jobs', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-claim-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    fs.writeFileSync(sourcePath, 'original');

    const canceled = await store.createJob({
      sourceId: `${sourcePath}-canceled`,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 123 },
    });
    await store.requestCancel(canceled.id);
    const live = await store.createJob({
      sourceId: `${sourcePath}-live`,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 124 },
    });

    const claimed = await store.claimNextQueuedJob('worker-test');

    expect(claimed?.id).toBe(live.id);
    expect(claimed?.status).toBe('running');
    expect(claimed?.runId).toBeTruthy();
    expect(claimed?.workerInstanceId).toBe('worker-test');
    expect((await store.getJob(canceled.id))?.status).toBe('canceled');
  });

  it('applies running updates and completion only for the active run id', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-cas-'));
    const store = new EnhancementJobStore(root);
    const sourcePath = path.join(root, 'source.png');
    fs.writeFileSync(sourcePath, 'original');

    const job = await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: 8, mtimeMs: 123 },
    });
    const claimed = await store.claimNextQueuedJob('worker-cas');
    expect(claimed?.id).toBe(job.id);
    expect(claimed?.runId).toBeTruthy();

    const staleUpdate = await store.updateRunningJob(job.id, 'stale-run', { progress: 90 });
    expect(staleUpdate).toBeNull();
    expect((await store.getJob(job.id))?.progress).not.toBe(90);

    const liveUpdate = await store.updateRunningJob(job.id, claimed?.runId, { progress: 45 });
    expect(liveUpdate?.progress).toBe(45);

    const staleFinish = await store.finishRunningJob(job.id, 'stale-run', {
      status: 'succeeded',
      progress: 100,
      outputPath: path.join(root, 'wrong.webp'),
    });
    expect(staleFinish).toBeNull();
    expect((await store.getJob(job.id))?.status).toBe('running');

    const liveFinish = await store.finishRunningJob(job.id, claimed?.runId, {
      status: 'succeeded',
      progress: 100,
      outputPath: path.join(root, 'right.webp'),
    });
    expect(liveFinish?.status).toBe('succeeded');
    expect((await store.getJob(job.id))?.outputPath).toMatch(/right\.webp$/);
  });
});

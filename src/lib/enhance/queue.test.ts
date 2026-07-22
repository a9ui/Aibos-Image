import fs from 'fs';
import os from 'os';
import path from 'path';
import { beforeEach, describe, expect, it } from 'vitest';
import sharp from 'sharp';
import { EnhancementJobStore, setEnhancementJobStoreForTests } from './jobStore';
import { getEnhancementIsolationMetrics, resetEnhancementIsolationMetricsForTests } from './isolationMetrics';
import {
  isEnhancementQueueRunning,
  resetEnhancementQueueForTests,
  startEnhancementQueue,
} from './queue';

describe('enhancement queue', () => {
  beforeEach(() => {
    resetEnhancementIsolationMetricsForTests();
    resetEnhancementQueueForTests();
  });

  it('starts only one worker loop when startEnhancementQueue is called repeatedly', async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pvu-enhance-queue-single-'));
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
    const stat = fs.statSync(sourcePath);

    await store.createJob({
      sourceId: sourcePath,
      sourcePath,
      sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    });
    await store.createJob({
      sourceId: `${sourcePath}-2`,
      sourcePath,
      sourceSignature: { size: stat.size, mtimeMs: stat.mtimeMs },
    });

    startEnhancementQueue();
    startEnhancementQueue();
    startEnhancementQueue();

    expect(getEnhancementIsolationMetrics()).toEqual({
      enhancementEnqueues: 2,
      enhancementWorkerStarts: 1,
    });
    expect(isEnhancementQueueRunning()).toBe(true);

    const started = Date.now();
    while (isEnhancementQueueRunning() && Date.now() - started < 15000) {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }

    const jobs = await store.listJobs();
    expect(jobs.every((job) => job.status === 'succeeded')).toBe(true);
    expect(getEnhancementIsolationMetrics().enhancementWorkerStarts).toBe(1);
  }, 15000);
});

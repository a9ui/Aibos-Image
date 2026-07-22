import { describe, expect, it } from 'vitest';
import { createThumbnailWarmupBatcher, type ThumbnailWarmupPriority } from './thumbnailWarmupBatcher';

type DispatchCall = {
  paths: string[];
  dirPath: string;
  priority: ThumbnailWarmupPriority;
};

function createManualBatcher() {
  let currentTime = 0;
  const calls: DispatchCall[] = [];
  const batcher = createThumbnailWarmupBatcher({
    delayMs: 25,
    dedupeMs: 100,
    highPriorityDedupeMs: 30,
    now: () => currentTime,
    setTimer: () => 1 as unknown as ReturnType<typeof setTimeout>,
    clearTimer: () => {},
    dispatch: (paths, dirPath, priority) => {
      calls.push({ paths, dirPath, priority });
    },
  });

  return {
    calls,
    batcher,
    advance: (ms: number) => {
      currentTime += ms;
    },
  };
}

describe('thumbnail warmup batcher', () => {
  it('coalesces repeated nearby paths into one dispatch', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['a.png', 'b.png', 'a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(['b.png', 'c.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });

    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png', 'b.png', 'c.png'] },
    ]);
  });

  it('upgrades pending nearby work to visible priority', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['a.png', 'b.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(['b.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });

    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['b.png'] },
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
    ]);
  });

  it('dispatches visible paths before earlier queued nearby overscan', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['before-visible.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(['visible.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });

    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['visible.png'] },
      { dirPath: 'C:/images', priority: 'nearby', paths: ['before-visible.png'] },
    ]);
  });

  it('flushes visible work immediately while keeping nearby work coalesced for the timer', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['old-nearby.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(['current-visible.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });

    batcher.flushHighPriority();
    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['current-visible.png'] },
    ]);
    expect(batcher.getPendingCount()).toBe(1);

    batcher.flush();
    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['current-visible.png'] },
      { dirPath: 'C:/images', priority: 'nearby', paths: ['old-nearby.png'] },
    ]);
  });

  it('lets focused modal work outrank visible thumbnails', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['visible.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.enqueue(['modal.png'], {
      dirPath: 'C:/images',
      contextKey: 'modal',
      priority: 'focused',
    });

    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'focused', paths: ['modal.png'] },
      { dirPath: 'C:/images', priority: 'visible', paths: ['visible.png'] },
    ]);
  });

  it('skips recently sent duplicates but allows them after the dedupe window', () => {
    const { batcher, calls, advance } = createManualBatcher();

    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();

    advance(101);
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
    ]);
  });

  it('allows visible priority to resend a recently sent nearby warmup', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
      { dirPath: 'C:/images', priority: 'visible', paths: ['a.png'] },
    ]);
  });

  it('allows visible priority to resend after the shorter high-priority dedupe window', () => {
    const { batcher, calls, advance } = createManualBatcher();

    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.flush();

    advance(31);
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['a.png'] },
      { dirPath: 'C:/images', priority: 'visible', paths: ['a.png'] },
    ]);
  });

  it('keeps nearby warmups deduped after a recent visible warmup', () => {
    const { batcher, calls, advance } = createManualBatcher();

    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.flush();

    advance(31);
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'visible', paths: ['a.png'] },
    ]);
  });

  it('keeps different directories and contexts independent', () => {
    const { batcher, calls } = createManualBatcher();

    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();
    batcher.enqueue(['a.png'], {
      dirPath: 'D:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(['a.png'], {
      dirPath: 'C:/images',
      contextKey: 'fav-only',
      priority: 'nearby',
    });
    batcher.flush();

    expect(calls).toEqual([
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
      { dirPath: 'D:/images', priority: 'nearby', paths: ['a.png'] },
      { dirPath: 'C:/images', priority: 'nearby', paths: ['a.png'] },
    ]);
  });

  it('reduces a representative scroll plus modal warmup burst', () => {
    const { batcher, calls } = createManualBatcher();
    const nearby = Array.from({ length: 80 }, (_, index) => `image-${index + 1}.png`);
    const visible = Array.from({ length: 24 }, (_, index) => `image-${index + 1}.png`);
    const modal = Array.from({ length: 18 }, (_, index) => `image-${index + 10}.png`);

    batcher.enqueue(nearby, {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.enqueue(visible, {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'visible',
    });
    batcher.enqueue(modal, {
      dirPath: 'C:/images',
      contextKey: 'grid',
      priority: 'nearby',
    });
    batcher.flush();

    const totalSentIds = calls.reduce((count, call) => count + call.paths.length, 0);

    expect(calls).toHaveLength(2);
    expect(totalSentIds).toBe(80);
    expect(calls.find((call) => call.priority === 'visible')?.paths).toEqual(visible);
    expect(calls.find((call) => call.priority === 'nearby')?.paths).toEqual(
      Array.from({ length: 56 }, (_, index) => `image-${index + 25}.png`)
    );
  });
});

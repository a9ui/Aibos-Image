import { describe, expect, it, vi } from 'vitest';
import {
  isTransientEnhancementPublishError,
  publishEnhancementOutput,
  type EnhancementOutputPublishDependencies,
} from './outputPublish';

function fileError(code: string) {
  return Object.assign(new Error(code), { code });
}

function dependencies(overrides: Partial<EnhancementOutputPublishDependencies> = {}) {
  return {
    rename: vi.fn(async () => {}),
    copyFile: vi.fn(async () => {}),
    remove: vi.fn(async () => {}),
    wait: vi.fn(async () => {}),
    ...overrides,
  } satisfies EnhancementOutputPublishDependencies;
}

describe('enhancement output publishing', () => {
  it.each(['EBUSY', 'EPERM', 'EACCES'])('recognizes transient Windows lock %s', (code) => {
    expect(isTransientEnhancementPublishError(fileError(code))).toBe(true);
  });

  it('uses atomic rename when the file is not locked', async () => {
    const deps = dependencies();
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [1],
    })).resolves.toBe('rename');
    expect(deps.rename).toHaveBeenCalledTimes(1);
    expect(deps.copyFile).not.toHaveBeenCalled();
  });

  it('waits and retries temporary Windows locks', async () => {
    const rename = vi.fn()
      .mockRejectedValueOnce(fileError('EBUSY'))
      .mockRejectedValueOnce(fileError('EPERM'))
      .mockResolvedValue(undefined);
    const deps = dependencies({ rename });
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [10, 20, 30],
    })).resolves.toBe('rename');
    expect(rename).toHaveBeenCalledTimes(3);
    expect(deps.wait).toHaveBeenNthCalledWith(1, 10);
    expect(deps.wait).toHaveBeenNthCalledWith(2, 20);
    expect(deps.copyFile).not.toHaveBeenCalled();
  });

  it('falls back to a completed copy when rename remains locked', async () => {
    const deps = dependencies({
      rename: vi.fn(async () => { throw fileError('EBUSY'); }),
    });
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [10, 20],
    })).resolves.toBe('copy');
    expect(deps.rename).toHaveBeenCalledTimes(3);
    expect(deps.copyFile).toHaveBeenCalledWith('output.tmp', 'output.webp');
    expect(deps.remove).toHaveBeenCalledWith('output.tmp');
  });

  it('retries a locked temporary cleanup after the completed copy is published', async () => {
    const remove = vi.fn()
      .mockRejectedValueOnce(fileError('EBUSY'))
      .mockRejectedValueOnce(fileError('EPERM'))
      .mockResolvedValue(undefined);
    const deps = dependencies({
      rename: vi.fn(async () => { throw fileError('EBUSY'); }),
      remove,
    });
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [],
      cleanupRetryDelaysMs: [10, 20],
    })).resolves.toBe('copy');
    expect(deps.copyFile).toHaveBeenCalledWith('output.tmp', 'output.webp');
    expect(remove).toHaveBeenCalledTimes(3);
    expect(deps.wait).toHaveBeenNthCalledWith(1, 10);
    expect(deps.wait).toHaveBeenNthCalledWith(2, 20);
  });

  it('keeps a completed copy successful when Windows holds the temporary indefinitely', async () => {
    const remove = vi.fn(async () => { throw fileError('EBUSY'); });
    const deps = dependencies({
      rename: vi.fn(async () => { throw fileError('EBUSY'); }),
      remove,
    });
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [],
      cleanupRetryDelaysMs: [10, 20],
    })).resolves.toBe('copy-with-stale-temporary');
    expect(deps.copyFile).toHaveBeenCalledTimes(1);
    expect(remove).toHaveBeenCalledTimes(3);
  });

  it('reports a copy failure separately from temporary cleanup', async () => {
    const deps = dependencies({
      rename: vi.fn(async () => { throw fileError('EBUSY'); }),
      copyFile: vi.fn(async () => { throw fileError('EACCES'); }),
    });
    await expect(publishEnhancementOutput('output.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [],
    })).rejects.toThrow(
      'Could not publish the enhanced output: rename remained locked after retries and the copy fallback failed: EACCES',
    );
    expect(deps.remove).not.toHaveBeenCalled();
  });

  it('does not hide a non-locking filesystem error', async () => {
    const deps = dependencies({
      rename: vi.fn(async () => { throw fileError('ENOENT'); }),
    });
    await expect(publishEnhancementOutput('missing.tmp', 'output.webp', {
      dependencies: deps,
      retryDelaysMs: [1, 1],
    })).rejects.toMatchObject({ code: 'ENOENT' });
    expect(deps.wait).not.toHaveBeenCalled();
    expect(deps.copyFile).not.toHaveBeenCalled();
  });
});

export interface BulkRecycleProgress {
  total: number;
  processed: number;
  successCount: number;
  failureCount: number;
}

export interface BulkRecycleResult extends BulkRecycleProgress {
  succeededIds: string[];
  failedIds: string[];
}

export function snapshotBulkRecycleTargets(ids: readonly string[]): string[] {
  return Array.from(new Set(ids.filter(Boolean)));
}

export async function recycleImagesSequentially(
  ids: readonly string[],
  recycleImage: (id: string) => Promise<boolean>,
  onProgress?: (progress: BulkRecycleProgress) => void,
): Promise<BulkRecycleResult> {
  const targets = snapshotBulkRecycleTargets(ids);
  const succeededIds: string[] = [];
  const failedIds: string[] = [];

  for (const id of targets) {
    let succeeded = false;
    try {
      succeeded = await recycleImage(id);
    } catch {
      succeeded = false;
    }
    if (succeeded) succeededIds.push(id);
    else failedIds.push(id);
    onProgress?.({
      total: targets.length,
      processed: succeededIds.length + failedIds.length,
      successCount: succeededIds.length,
      failureCount: failedIds.length,
    });
  }

  return {
    total: targets.length,
    processed: targets.length,
    successCount: succeededIds.length,
    failureCount: failedIds.length,
    succeededIds,
    failedIds,
  };
}

export function formatBulkRecycleProgress(progress: BulkRecycleProgress): string {
  if (progress.processed < progress.total) {
    return `Moving to Recycle Bin: ${progress.processed}/${progress.total} checked, ${progress.successCount} moved, ${progress.failureCount} failed.`;
  }
  if (progress.failureCount === 0) {
    return `Moved ${progress.successCount} image(s) to Recycle Bin.`;
  }
  if (progress.successCount === 0) {
    return `Could not move ${progress.failureCount} image(s) to Recycle Bin. They remain selected for retry.`;
  }
  return `Moved ${progress.successCount}/${progress.total} image(s) to Recycle Bin. ${progress.failureCount} failed and remain selected for retry.`;
}

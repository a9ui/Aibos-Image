import { spawnSync } from 'child_process';

const activeNcnnProcesses = new Map<string, { runId?: string; pid: number }>();
let shutdownCleanupRegistered = false;

export function terminateNcnnVulkanProcess(pid: number | undefined) {
  if (!pid) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill.exe', ['/pid', String(pid), '/t', '/f'], {
      stdio: 'ignore',
      windowsHide: true,
    });
    return;
  }
  try {
    process.kill(pid, 'SIGTERM');
  } catch {
    // Process already exited.
  }
}

export function trackNcnnVulkanProcess(jobId: string, runId: string | undefined, pid: number) {
  activeNcnnProcesses.set(jobId, { runId, pid });
}

export function untrackNcnnVulkanProcess(jobId: string) {
  activeNcnnProcesses.delete(jobId);
}

export function requestNcnnVulkanCancel(jobId: string, runId?: string) {
  const active = activeNcnnProcesses.get(jobId);
  if (!active) return false;
  if (runId && active.runId && active.runId !== runId) return false;
  terminateNcnnVulkanProcess(active.pid);
  return true;
}

export function registerNcnnVulkanShutdownCleanup() {
  if (shutdownCleanupRegistered) return;
  shutdownCleanupRegistered = true;
  process.once('exit', () => {
    for (const active of activeNcnnProcesses.values()) {
      terminateNcnnVulkanProcess(active.pid);
    }
  });
}

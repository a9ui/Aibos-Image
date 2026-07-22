import { getComfyBaseUrl } from './comfyUiConfig';

export async function comfyFetch(pathname: string, init?: RequestInit) {
  const res = await fetch(`${getComfyBaseUrl()}${pathname}`, init);
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`ComfyUI ${pathname} failed: HTTP ${res.status}${text ? ` ${text.slice(0, 500)}` : ''}`);
  }
  return res;
}

async function getQueue() {
  const res = await comfyFetch('/queue');
  return await res.json() as unknown;
}

function queueJsonContainsPromptId(queue: unknown, promptId: string) {
  return JSON.stringify(queue).includes(promptId);
}

export async function requestComfyUiInterrupt(promptId?: string) {
  if (promptId) {
    const queue = await getQueue();
    if (!queueJsonContainsPromptId(queue, promptId)) {
      throw new Error(`ComfyUI prompt ${promptId} is not the active or queued job; refusing global interrupt.`);
    }
  }
  const res = await fetch(`${getComfyBaseUrl()}/interrupt`, { method: 'POST' });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`ComfyUI interrupt failed: HTTP ${res.status}${text ? ` ${text.slice(0, 500)}` : ''}`);
  }
}

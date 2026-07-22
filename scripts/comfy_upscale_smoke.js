const fs = require('fs');
const net = require('net');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const COMFY_ROOT = process.env.PVU_COMFY_ROOT || 'C:\\AI\\ComfyUI';
const COMFY_HOST = process.env.PVU_COMFY_HOST || '127.0.0.1';
const COMFY_PORT = Number(process.env.PVU_COMFY_PORT || 8188);
const COMFY_URL = process.env.PVU_COMFY_URL || `http://${COMFY_HOST}:${COMFY_PORT}`;
const WORKFLOW_PATH = process.env.PVU_COMFY_WORKFLOW_PATH || path.join(ROOT, 'config', 'comfy-upscale-workflow.json');
const TEST_IMAGE = path.join(process.env.TEMP || ROOT, 'pvu-comfy-smoke.png');
const TEST_IMAGE_BASE64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYPgPAAEDAQD6Hg6iAAAAAElFTkSuQmCC';
const MODEL_NAME = process.env.PVU_COMFY_SMOKE_MODEL || 'RealESRGAN_x4plus_anime_6B.pth';

let comfyChild = null;
let ownsComfy = false;

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isPortOpen(port, host) {
  return new Promise((resolve) => {
    const socket = net.createConnection({ port, host });
    socket.once('connect', () => {
      socket.destroy();
      resolve(true);
    });
    socket.once('error', () => {
      socket.destroy();
      resolve(false);
    });
  });
}

function killTree(pid) {
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
    // Already stopped.
  }
}

async function waitForComfy() {
  for (let i = 0; i < 120; i += 1) {
    try {
      const res = await fetch(`${COMFY_URL}/system_stats`);
      if (res.ok) return;
    } catch {
      // Server is still starting.
    }
    await sleep(1000);
  }
  throw new Error('ComfyUI did not become ready within 120s.');
}

async function startComfyIfNeeded() {
  if (await isPortOpen(COMFY_PORT, COMFY_HOST)) {
    console.log(`[smoke] Reusing ComfyUI at ${COMFY_URL}`);
    return;
  }

  const pythonPath = path.join(COMFY_ROOT, 'venv', 'Scripts', 'python.exe');
  if (!fs.existsSync(pythonPath)) {
    throw new Error(`ComfyUI python not found: ${pythonPath}`);
  }
  if (!fs.existsSync(path.join(COMFY_ROOT, 'main.py'))) {
    throw new Error(`ComfyUI main.py not found under ${COMFY_ROOT}`);
  }

  console.log(`[smoke] Starting ComfyUI at ${COMFY_URL}`);
  comfyChild = spawn(pythonPath, ['main.py', '--listen', COMFY_HOST, '--port', String(COMFY_PORT)], {
    cwd: COMFY_ROOT,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  ownsComfy = true;
  comfyChild.stdout.on('data', (data) => process.stdout.write(`[comfy] ${data}`));
  comfyChild.stderr.on('data', (data) => process.stderr.write(`[comfy] ${data}`));
  await waitForComfy();
}

async function assertModelVisible() {
  const res = await fetch(`${COMFY_URL}/object_info/UpscaleModelLoader`);
  if (!res.ok) throw new Error(`object_info failed: HTTP ${res.status}`);
  const data = await res.json();
  const models = data?.UpscaleModelLoader?.input?.required?.model_name?.[0] || [];
  if (!models.includes(MODEL_NAME)) {
    throw new Error(`${MODEL_NAME} is not visible to UpscaleModelLoader. Visible models: ${models.join(', ')}`);
  }
  console.log(`[smoke] Model visible: ${MODEL_NAME}`);
}

async function uploadImage() {
  if (!fs.existsSync(TEST_IMAGE)) {
    fs.writeFileSync(TEST_IMAGE, Buffer.from(TEST_IMAGE_BASE64, 'base64'));
  }
  const bytes = fs.readFileSync(TEST_IMAGE);
  const form = new FormData();
  form.append('image', new Blob([bytes]), path.basename(TEST_IMAGE));
  form.append('overwrite', 'true');
  const res = await fetch(`${COMFY_URL}/upload/image`, {
    method: 'POST',
    body: form,
  });
  if (!res.ok) throw new Error(`upload failed: HTTP ${res.status} ${await res.text()}`);
  return await res.json();
}

async function submitPrompt(uploaded) {
  const workflow = JSON.parse(fs.readFileSync(WORKFLOW_PATH, 'utf8'));
  workflow['1'].inputs.image = uploaded.subfolder ? `${uploaded.subfolder}/${uploaded.name}` : uploaded.name;
  workflow['2'].inputs.model_name = MODEL_NAME;
  const res = await fetch(`${COMFY_URL}/prompt`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt: workflow, client_id: 'pvu-comfy-smoke' }),
  });
  if (!res.ok) throw new Error(`prompt failed: HTTP ${res.status} ${await res.text()}`);
  const data = await res.json();
  if (!data.prompt_id) throw new Error('ComfyUI did not return prompt_id.');
  return data.prompt_id;
}

async function waitForOutput(promptId) {
  for (let i = 0; i < 180; i += 1) {
    const res = await fetch(`${COMFY_URL}/history/${encodeURIComponent(promptId)}`);
    if (res.ok) {
      const history = await res.json();
      const entry = history[promptId];
      for (const output of Object.values(entry?.outputs || {})) {
        const image = output.images?.[0];
        if (image?.filename) return image;
      }
    }
    await sleep(1000);
  }
  throw new Error(`Prompt ${promptId} did not produce an output image.`);
}

async function cleanup() {
  if (ownsComfy && comfyChild && !comfyChild.killed) {
    console.log('[smoke] Stopping managed ComfyUI');
    killTree(comfyChild.pid);
  }
}

async function main() {
  try {
    await startComfyIfNeeded();
    await assertModelVisible();
    const uploaded = await uploadImage();
    const promptId = await submitPrompt(uploaded);
    console.log(`[smoke] prompt_id=${promptId}`);
    const output = await waitForOutput(promptId);
    console.log(`[smoke] output=${JSON.stringify(output)}`);
  } finally {
    await cleanup();
  }
}

main().catch(async (error) => {
  console.error(`[smoke] ${error instanceof Error ? error.message : String(error)}`);
  await cleanup();
  process.exit(1);
});

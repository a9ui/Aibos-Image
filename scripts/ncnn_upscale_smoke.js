const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const ROOT = path.join(__dirname, '..');
const NCNN_ROOT = process.env.PVU_REALESRGAN_NCNN_ROOT || 'C:\\AI\\RealESRGAN-ncnn-vulkan';
const NCNN_EXE = process.env.PVU_REALESRGAN_NCNN_EXE || path.join(NCNN_ROOT, 'realesrgan-ncnn-vulkan.exe');
const MODEL_DIR = process.env.PVU_REALESRGAN_NCNN_MODEL_DIR || path.join(NCNN_ROOT, 'models');
const TMP_ROOT = path.join(ROOT, 'exports', 'ncnn-smoke');

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
    // Already exited.
  }
}

function runNcnn(args, cancelAfterMs = 0) {
  return new Promise((resolve, reject) => {
    const child = spawn(NCNN_EXE, args, {
      cwd: NCNN_ROOT,
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let stderr = '';
    let stdout = '';
    let cancelTimer = null;
    if (cancelAfterMs > 0) {
      cancelTimer = setTimeout(() => killTree(child.pid), cancelAfterMs);
    }
    child.stdout.on('data', (chunk) => { stdout += String(chunk); });
    child.stderr.on('data', (chunk) => { stderr += String(chunk); });
    child.on('error', reject);
    child.on('exit', (code, signal) => {
      if (cancelTimer) clearTimeout(cancelTimer);
      resolve({ code, signal, stdout, stderr });
    });
  });
}

async function main() {
  if (!fs.existsSync(NCNN_EXE)) {
    throw new Error(`ncnn executable not found: ${NCNN_EXE}`);
  }
  fs.rmSync(TMP_ROOT, { recursive: true, force: true });
  fs.mkdirSync(TMP_ROOT, { recursive: true });
  const input = path.join(TMP_ROOT, 'input.png');
  const output = path.join(TMP_ROOT, 'output.webp');
  const cancelOutput = path.join(TMP_ROOT, 'cancel-output.webp');
  await sharp({
    create: {
      width: 128,
      height: 128,
      channels: 4,
      background: '#336699ff',
    },
  }).png().toFile(input);

  const args = [
    '-i', input,
    '-o', output,
    '-s', '2',
    '-m', MODEL_DIR,
    '-n', 'realesr-animevideov3',
    '-f', 'webp',
    '-v',
    '-j', '1:2:2',
  ];
  const result = await runNcnn(args);
  if (result.code !== 0) {
    throw new Error(`ncnn smoke failed with exit ${result.code}: ${(result.stderr || result.stdout).slice(-1000)}`);
  }
  const meta = await sharp(output, { failOn: 'none' }).metadata();
  if (meta.width !== 256 || meta.height !== 256) {
    throw new Error(`unexpected ncnn smoke output size: ${meta.width}x${meta.height}`);
  }

  const cancelResult = await runNcnn([
    '-i', input,
    '-o', cancelOutput,
    '-s', '4',
    '-m', MODEL_DIR,
    '-n', 'realesr-animevideov3',
    '-f', 'webp',
    '-v',
    '-j', '1:2:2',
  ], 10);
  if (cancelResult.code === 0 && fs.existsSync(cancelOutput)) {
    throw new Error('ncnn cancel smoke unexpectedly completed before cancel could be observed');
  }
  fs.rmSync(cancelOutput, { force: true });

  console.log(JSON.stringify({
    ok: true,
    output,
    width: meta.width,
    height: meta.height,
    cancelExit: cancelResult.code,
    cancelSignal: cancelResult.signal,
  }, null, 2));
}

main().catch((error) => {
  console.error(error.message);
  process.exit(1);
});

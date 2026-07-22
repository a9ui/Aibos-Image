/**
 * Production launcher for Photoviewer.
 *
 * 1. Uses an explicit loopback port or finds an open port starting at 3000.
 * 2. Builds the app if `.next/BUILD_ID` is missing or source/config files changed.
 * 3. Starts `next start` and opens the browser when ready.
 */
const net = require('net');
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const {
  DEFAULT_START_PORT,
  dispatchLauncher,
} = require('./prod_launcher_cli');
const { selectManagedProcessRoots } = require('./prod_launcher_processes');

const ROOT = path.join(__dirname, '..');
const BUILD_ID_FILE = path.join(ROOT, '.next', 'BUILD_ID');
const SERVER_HOST = '127.0.0.1';
const OPEN_BROWSER = process.env.PVU_NO_OPEN !== '1';
const COMFY_ROOT = process.env.PVU_COMFY_ROOT || 'C:\\AI\\ComfyUI';
const COMFY_HOST = process.env.PVU_COMFY_HOST || '127.0.0.1';
const COMFY_PORT = Number(process.env.PVU_COMFY_PORT || 8188);
const COMFY_URL = process.env.PVU_COMFY_URL || `http://${COMFY_HOST}:${COMFY_PORT}`;
let serverChild = null;
let comfyChild = null;
let ownsComfy = false;
let cleanedUp = false;

function killProcessTree(pid) {
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

function cleanupServer() {
  if (cleanedUp) return;
  cleanedUp = true;
  if (serverChild && !serverChild.killed) {
    console.log('[Photoviewer] Stopping production server...');
    killProcessTree(serverChild.pid);
  }
  cleanupComfy();
}

function cleanupComfy() {
  if (!ownsComfy || !comfyChild || comfyChild.killed) return;
  console.log('[Photoviewer] Stopping managed ComfyUI...');
  killProcessTree(comfyChild.pid);
  comfyChild = null;
  ownsComfy = false;
}

function cleanupStaleServers() {
  if (process.platform !== 'win32') return;

  const script = [
    'Get-CimInstance Win32_Process |',
    "  Where-Object { $_.Name -eq 'node.exe' } |",
    '  Select-Object ProcessId, ParentProcessId, Name, CommandLine |',
    '  ConvertTo-Json -Compress',
  ].join('\n');

  const result = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    cwd: ROOT,
    encoding: 'utf8',
    windowsHide: true,
  });
  if (result.status !== 0 || !result.stdout.trim()) return;

  let processes;
  try {
    const parsed = JSON.parse(result.stdout.trim());
    processes = Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    console.log('[Photoviewer] Could not inspect leftover server processes safely; skipping cleanup.');
    return;
  }

  const nextBin = require.resolve('next/dist/bin/next', { paths: [ROOT] });
  const staleRoots = selectManagedProcessRoots(processes, {
    root: ROOT,
    nextBin,
    selfPid: process.pid,
  });
  for (const processInfo of staleRoots) {
    console.log(`[Photoviewer] Stopping leftover server process ${processInfo.ProcessId}`);
    killProcessTree(Number(processInfo.ProcessId));
  }
}

function isPortAvailable(port) {
  return new Promise((resolve) => {
    const server = net.createServer();
    server.once('error', () => resolve(false));
    server.once('listening', () => {
      server.close(() => resolve(true));
    });
    // Probe the wildcard socket so a listener on either IPv4 or IPv6 reserves
    // the port. The real Next server is still started on loopback only.
    server.listen(port);
  });
}

function readRuntimeProvenance(port) {
  const buildId = fs.readFileSync(BUILD_ID_FILE, 'utf8').trim();
  const buildCompletedAtUtc = fs.statSync(BUILD_ID_FILE).mtime.toISOString();
  const revisionResult = spawnSync('git', ['rev-parse', 'HEAD'], {
    cwd: ROOT,
    encoding: 'utf8',
    windowsHide: true,
  });
  const sourceRevision = revisionResult.status === 0
    ? revisionResult.stdout.trim()
    : 'unknown';
  const dirtyResult = spawnSync('git', ['status', '--porcelain'], {
    cwd: ROOT,
    encoding: 'utf8',
    windowsHide: true,
  });
  const sourceDirty = dirtyResult.status !== 0 || dirtyResult.stdout.trim().length > 0;

  return {
    product: 'Aibos Image',
    projectRoot: ROOT,
    sourceRevision,
    sourceDirty,
    buildId,
    buildCompletedAtUtc,
    serverHost: SERVER_HOST,
    serverPort: port,
    launcherPid: process.pid,
    serverStartedAtUtc: new Date().toISOString(),
  };
}

function isPortListening(port, host = '127.0.0.1') {
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

async function waitForPort(port, host, timeoutMs) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await isPortListening(port, host)) return true;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return false;
}

function openBrowser(url) {
  console.log(`[Photoviewer] Opening browser: ${url}`);
  try {
    const child = spawn('cmd', ['/c', 'start', '', url], {
      stdio: 'ignore',
      windowsHide: true,
    });
    child.on('error', (err) => {
      console.log(`[Photoviewer] Browser launch failed (${err.message}). Open manually: ${url}`);
    });
    child.unref();
  } catch (err) {
    console.log(`[Photoviewer] Browser launch failed (${err.message}). Open manually: ${url}`);
  }
}

function newestMtimeMs(targetPath) {
  if (!fs.existsSync(targetPath)) return 0;

  const stat = fs.statSync(targetPath);
  if (stat.isFile()) return stat.mtimeMs;
  if (!stat.isDirectory()) return 0;

  let newest = stat.mtimeMs;
  const entries = fs.readdirSync(targetPath, { withFileTypes: true });
  for (const entry of entries) {
    if (
      entry.name === 'node_modules' ||
      entry.name === '.next' ||
      entry.name === '.cache' ||
      entry.name === '.local' ||
      entry.name === 'exports'
    ) {
      continue;
    }
    newest = Math.max(newest, newestMtimeMs(path.join(targetPath, entry.name)));
  }
  return newest;
}

function needsBuild() {
  if (!fs.existsSync(BUILD_ID_FILE)) {
    console.log('[Photoviewer] Build missing; running build.');
    return true;
  }

  const buildMtime = fs.statSync(BUILD_ID_FILE).mtimeMs;
  const watchedPaths = [
    'src',
    'scripts',
    'package.json',
    'pnpm-lock.yaml',
    'next.config.js',
    'next.config.ts',
    'tsconfig.json',
    'postcss.config.js',
  ];

  for (const relPath of watchedPaths) {
    const absPath = path.join(ROOT, relPath);
    const mtime = newestMtimeMs(absPath);
    if (mtime > buildMtime) {
      console.log(`[Photoviewer] Build stale because ${relPath} changed after .next/BUILD_ID.`);
      return true;
    }
  }

  console.log(`[Photoviewer] Build up to date. BUILD_ID mtime: ${new Date(buildMtime).toISOString()}`);
  return false;
}

function runBuild() {
  console.log('[Photoviewer] Running Next build... (first-time setup, please wait ~1 min)');
  const nextBin = require.resolve('next/dist/bin/next', { paths: [ROOT] });
  const result = spawnSync(process.execPath, [nextBin, 'build'], {
    cwd: ROOT,
    stdio: 'inherit',
    windowsHide: true,
  });

  if (result.status !== 0) {
    console.error('[Photoviewer] Build failed. Please check the error above.');
    process.exit(1);
  }
  console.log('[Photoviewer] Build complete.');
}

async function startManagedComfy() {
  if (process.env.PVU_COMFY_AUTOSTART !== '1') {
    console.log('[Photoviewer] ComfyUI autostart disabled by default. Set PVU_COMFY_AUTOSTART=1 for Advanced ComfyUI Workflow.');
    return;
  }
  if (process.env.PVU_COMFY_AUTOSTART === '0') {
    console.log('[Photoviewer] ComfyUI autostart disabled by PVU_COMFY_AUTOSTART=0.');
    return;
  }
  if (!Number.isFinite(COMFY_PORT) || COMFY_PORT <= 0) {
    console.log('[Photoviewer] ComfyUI autostart skipped because PVU_COMFY_PORT is invalid.');
    return;
  }

  if (await isPortListening(COMFY_PORT, COMFY_HOST)) {
    console.log(`[Photoviewer] ComfyUI is already listening at ${COMFY_URL}; reusing it.`);
    return;
  }

  const comfyPython = path.join(COMFY_ROOT, 'venv', 'Scripts', 'python.exe');
  const comfyMain = path.join(COMFY_ROOT, 'main.py');
  if (!fs.existsSync(comfyPython) || !fs.existsSync(comfyMain)) {
    console.log(`[Photoviewer] ComfyUI autostart skipped. Expected ${comfyPython} and ${comfyMain}.`);
    return;
  }

  console.log(`[Photoviewer] Starting managed ComfyUI on ${COMFY_URL} ...`);
  comfyChild = spawn(comfyPython, [
    'main.py',
    '--listen',
    COMFY_HOST,
    '--port',
    String(COMFY_PORT),
  ], {
    cwd: COMFY_ROOT,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
    env: {
      ...process.env,
      PVU_COMFY_URL: COMFY_URL,
    },
  });
  ownsComfy = true;

  comfyChild.stdout.on('data', (data) => process.stdout.write(`[ComfyUI] ${data}`));
  comfyChild.stderr.on('data', (data) => process.stderr.write(`[ComfyUI] ${data}`));
  comfyChild.on('error', (err) => {
    console.log(`[Photoviewer] ComfyUI launch failed (${err.message}). AI upscale will need manual ComfyUI start.`);
    comfyChild = null;
    ownsComfy = false;
  });
  comfyChild.on('close', (code) => {
    if (ownsComfy) {
      console.log(`[Photoviewer] Managed ComfyUI stopped${typeof code === 'number' ? ` with code ${code}` : ''}.`);
    }
    comfyChild = null;
    ownsComfy = false;
  });

  const ready = await waitForPort(COMFY_PORT, COMFY_HOST, 30_000);
  if (ready) {
    console.log(`[Photoviewer] Managed ComfyUI ready at ${COMFY_URL}.`);
  } else {
    console.log('[Photoviewer] ComfyUI did not become ready within 30s; continuing viewer startup.');
  }
}

async function main({ port, explicitPort }) {
  process.env.PVU_COMFY_URL = COMFY_URL;
  if (explicitPort === null && port !== DEFAULT_START_PORT) {
    console.log(`[Photoviewer] Port ${DEFAULT_START_PORT} is busy. Using port ${port}.`);
  } else if (explicitPort !== null) {
    console.log(`[Photoviewer] Using requested loopback port ${port}.`);
  }

  if (needsBuild()) {
    runBuild();
  } else {
    console.log('[Photoviewer] Existing build found. Skipping build step.');
  }

  await startManagedComfy();

  const url = `http://${SERVER_HOST}:${port}`;
  const provenance = readRuntimeProvenance(port);
  console.log(`[Photoviewer] Runtime provenance ${JSON.stringify(provenance)}`);
  console.log(`[Photoviewer] Starting production server on ${url} ...`);

  let browserOpened = false;

  const child = spawn(process.execPath, [
    path.join(__dirname, 'serve_with_parent_watch.js'),
    String(process.pid),
    String(port),
    SERVER_HOST,
  ], {
    cwd: ROOT,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
    env: {
      ...process.env,
      PVU_BUILD_ID: provenance.buildId,
      PVU_BUILD_COMPLETED_AT_UTC: provenance.buildCompletedAtUtc,
      PVU_SOURCE_REVISION: provenance.sourceRevision,
      PVU_SOURCE_DIRTY: provenance.sourceDirty ? '1' : '0',
      PVU_SERVER_HOST: provenance.serverHost,
      PVU_SERVER_PORT: String(provenance.serverPort),
      PVU_SERVER_STARTED_AT_UTC: provenance.serverStartedAtUtc,
    },
  });
  serverChild = child;

  const onData = (data) => {
    process.stdout.write(data);
    if (OPEN_BROWSER && !browserOpened && /ready|started server|localhost/i.test(data.toString())) {
      browserOpened = true;
      openBrowser(url);
    }
  };

  child.stdout.on('data', onData);
  child.stderr.on('data', (data) => {
    process.stderr.write(data);
    if (OPEN_BROWSER && !browserOpened && /ready|started server|localhost/i.test(data.toString())) {
      browserOpened = true;
      openBrowser(url);
    }
  });

  if (OPEN_BROWSER) setTimeout(() => {
    if (!browserOpened) {
      browserOpened = true;
      console.log('[Photoviewer] Server ready signal not detected, opening browser anyway...');
      openBrowser(url);
    }
  }, 8000);

  child.on('error', (err) => {
    console.error(`[Photoviewer] Failed to launch server: ${err.message}`);
    process.exit(1);
  });

  child.on('close', (code) => {
    serverChild = null;
    cleanupComfy();
    cleanedUp = true;
    process.exit(code ?? 0);
  });
}

let lifecycleHandlersInstalled = false;

function installLifecycleHandlers() {
  if (lifecycleHandlersInstalled) return;
  lifecycleHandlersInstalled = true;
  process.once('exit', cleanupServer);
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.once(signal, () => {
      cleanupServer();
      process.exit(signal === 'SIGINT' ? 130 : 0);
    });
  }
  process.once('uncaughtException', (err) => {
    console.error(`[Photoviewer] ${err.message}`);
    cleanupServer();
    process.exit(1);
  });
  process.once('unhandledRejection', (err) => {
    console.error(`[Photoviewer] ${err instanceof Error ? err.message : String(err)}`);
    cleanupServer();
    process.exit(1);
  });
}

async function runCli(argv) {
  const result = await dispatchLauncher(argv, {
    writeUsage: (message) => console.log(message),
    writeError: (message) => console.error(message),
    prepare: ({ explicitPort }) => {
      // Automatic stale-process cleanup belongs only to the historical default
      // launch path. An explicit busy port must fail, never kill or replace it.
      if (explicitPort === null) cleanupStaleServers();
    },
    isPortAvailable,
    start: async (options) => {
      installLifecycleHandlers();
      await main(options);
    },
  });
  if (result.exitCode !== 0) process.exitCode = result.exitCode;
}

runCli(process.argv.slice(2)).catch((err) => {
  console.error(`[Photoviewer] ${err.message}`);
  cleanupServer();
  process.exitCode = 1;
});

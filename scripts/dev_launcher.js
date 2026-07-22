const net = require('net');
const { spawn } = require('child_process');

const isWin = process.platform === 'win32';
const START_PORT = 3000;
const MAX_PORT = 3999;

function findAvailablePort(port) {
  return new Promise((resolve, reject) => {
    if (port > MAX_PORT) {
      reject(new Error('No available port found in range 3000-3999.'));
      return;
    }
    const server = net.createServer();
    server.once('error', () => resolve(findAvailablePort(port + 1)));
    server.once('listening', () => {
      server.close(() => resolve(port));
    });
    server.listen(port, '127.0.0.1');
  });
}

function openBrowser(url) {
  console.log(`[Photoviewer] Opening browser: ${url}`);
  try {
    // shell:true で cmd /c start を呼ぶのが最も安全
    const child = spawn('cmd', ['/c', 'start', '', url], { shell: true });
    child.on('error', (err) => {
      console.log(`[Photoviewer] Browser launch failed (${err.message}). Open manually: ${url}`);
    });
    child.unref();
  } catch (err) {
    console.log(`[Photoviewer] Browser launch failed (${err.message}). Open manually: ${url}`);
  }
}

async function main() {
  const port = await findAvailablePort(START_PORT);
  const url = `http://localhost:${port}`;
  if (port !== START_PORT) {
    console.log(`[Photoviewer] Port ${START_PORT} is busy. Using ${port}.`);
  }

  const args = ['run', 'dev', '--', '--port', String(port)];
  console.log(`[Photoviewer] Launching: npm ${args.join(' ')}`);

  let browserOpened = false;

  const child = spawn('npm', args, {
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: true,
  });

  // stdout を中継しつつ "ready" を検出してブラウザを開く
  child.stdout.on('data', (data) => {
    process.stdout.write(data);
    if (!browserOpened && /ready|started server|localhost/i.test(data.toString())) {
      browserOpened = true;
      openBrowser(url);
    }
  });

  child.stderr.on('data', (data) => {
    process.stderr.write(data);
    // Next.js は起動ログを stderr に出すことがある
    if (!browserOpened && /ready|started server|localhost/i.test(data.toString())) {
      browserOpened = true;
      openBrowser(url);
    }
  });

  // 10秒経っても ready が来なければ強制的にブラウザを開く
  setTimeout(() => {
    if (!browserOpened) {
      browserOpened = true;
      console.log('[Photoviewer] Server ready signal not detected, opening browser anyway...');
      openBrowser(url);
    }
  }, 10000);

  child.on('error', (err) => {
    console.error(`[Photoviewer] Failed to launch dev server: ${err.message}`);
    console.log(`[Photoviewer] Please run manually: npm run dev -- --port ${port}`);
    process.exit(1);
  });

  child.on('close', (code) => process.exit(code ?? 0));
}

main().catch((err) => {
  console.error(`[Photoviewer] ${err.message}`);
  process.exit(1);
});

const DEFAULT_START_PORT = 3000;
const DEFAULT_MAX_PORT = 3999;

const USAGE = [
  'Aibos production launcher',
  '',
  'Usage:',
  '  node scripts/prod_launcher.js',
  '  node scripts/prod_launcher.js --port <1..65535>',
  '  node scripts/prod_launcher.js --port=<1..65535>',
  '  node scripts/prod_launcher.js --help',
  '',
  'Without --port, Aibos uses the first available port from 3000 to 3999.',
  'With --port, Aibos uses only that loopback port and fails if it is unavailable.',
].join('\n');

function parsePortValue(value) {
  if (typeof value !== 'string' || !/^[1-9]\d{0,4}$/.test(value)) {
    throw new Error(`Invalid --port value "${String(value)}". Expected an integer from 1 to 65535.`);
  }
  const port = Number(value);
  if (port > 65_535) {
    throw new Error(`Invalid --port value "${value}". Expected an integer from 1 to 65535.`);
  }
  return port;
}

function parseLauncherArgs(argv) {
  if (!Array.isArray(argv)) throw new TypeError('Launcher arguments must be an array.');
  if (argv.includes('--help') || argv.includes('-h')) {
    return { help: true, explicitPort: null };
  }

  let explicitPort = null;
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    let rawPort = null;
    if (argument === '--port') {
      if (index + 1 >= argv.length) {
        throw new Error('Missing value after --port. Expected an integer from 1 to 65535.');
      }
      rawPort = argv[index + 1];
      index += 1;
    } else if (typeof argument === 'string' && argument.startsWith('--port=')) {
      rawPort = argument.slice('--port='.length);
    } else {
      throw new Error(`Unknown launcher argument "${String(argument)}".`);
    }

    if (explicitPort !== null) {
      throw new Error('The --port option may be specified only once.');
    }
    explicitPort = parsePortValue(rawPort);
  }

  return { help: false, explicitPort };
}

async function selectLauncherPort({
  explicitPort,
  isPortAvailable,
  startPort = DEFAULT_START_PORT,
  maxPort = DEFAULT_MAX_PORT,
}) {
  if (typeof isPortAvailable !== 'function') {
    throw new TypeError('isPortAvailable must be a function.');
  }

  if (explicitPort !== null) {
    if (await isPortAvailable(explicitPort)) return explicitPort;
    throw new Error(
      `Requested port ${explicitPort} is already in use or unavailable. No server was started.`,
    );
  }

  for (let port = startPort; port <= maxPort; port += 1) {
    if (await isPortAvailable(port)) return port;
  }
  throw new Error(`No available port found in range ${startPort}-${maxPort}.`);
}

async function dispatchLauncher(argv, dependencies) {
  const writeUsage = dependencies.writeUsage ?? (() => {});
  const writeError = dependencies.writeError ?? (() => {});
  let options;
  try {
    options = parseLauncherArgs(argv);
  } catch (error) {
    writeError(`[Photoviewer] ${error instanceof Error ? error.message : String(error)}`);
    writeUsage(USAGE);
    return { action: 'error', exitCode: 2 };
  }

  if (options.help) {
    writeUsage(USAGE);
    return { action: 'help', exitCode: 0 };
  }

  let port;
  try {
    await dependencies.prepare?.({ explicitPort: options.explicitPort });
    port = await selectLauncherPort({
      explicitPort: options.explicitPort,
      isPortAvailable: dependencies.isPortAvailable,
    });
  } catch (error) {
    writeError(`[Photoviewer] ${error instanceof Error ? error.message : String(error)}`);
    return { action: 'error', exitCode: 1 };
  }

  await dependencies.start({ port, explicitPort: options.explicitPort });
  return { action: 'started', exitCode: 0, port };
}

module.exports = {
  DEFAULT_MAX_PORT,
  DEFAULT_START_PORT,
  USAGE,
  dispatchLauncher,
  parseLauncherArgs,
  selectLauncherPort,
};

import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);
const {
  USAGE,
  dispatchLauncher,
  parseLauncherArgs,
  selectLauncherPort,
} = require('./prod_launcher_cli.js');

function createDependencies(availability = () => true) {
  return {
    writeUsage: vi.fn(),
    writeError: vi.fn(),
    prepare: vi.fn(),
    isPortAvailable: vi.fn(availability),
    start: vi.fn(),
  };
}

describe('production launcher CLI', () => {
  it('documents help, explicit port forms, and the default port policy', () => {
    expect(USAGE).toContain('--port <1..65535>');
    expect(USAGE).toContain('--port=<1..65535>');
    expect(USAGE).toContain('--help');
    expect(USAGE).toContain('first available port from 3000 to 3999');
    expect(USAGE).toContain('uses only that loopback port');
  });

  it('parses help and both explicit port forms at the valid boundaries', () => {
    expect(parseLauncherArgs([])).toEqual({ help: false, explicitPort: null });
    expect(parseLauncherArgs(['--help'])).toEqual({ help: true, explicitPort: null });
    expect(parseLauncherArgs(['-h'])).toEqual({ help: true, explicitPort: null });
    expect(parseLauncherArgs(['--port', '1'])).toEqual({ help: false, explicitPort: 1 });
    expect(parseLauncherArgs(['--port=65535'])).toEqual({ help: false, explicitPort: 65_535 });
  });

  it.each([
    [['--port'], 'Missing value'],
    [['--port='], 'Invalid --port value'],
    [['--port', '0'], 'Invalid --port value'],
    [['--port=65536'], 'Invalid --port value'],
    [['--port', '12.5'], 'Invalid --port value'],
    [['--port', '3000', '--port=3001'], 'only once'],
    [['--unknown'], 'Unknown launcher argument'],
  ])('rejects invalid arguments %j', (argv, message) => {
    expect(() => parseLauncherArgs(argv)).toThrow(message);
  });

  it.each([['--help'], ['-h']])('prints %s without preparing, probing, or starting', async (helpFlag) => {
    const dependencies = createDependencies();

    await expect(dispatchLauncher([helpFlag], dependencies)).resolves.toEqual({
      action: 'help',
      exitCode: 0,
    });
    expect(dependencies.writeUsage).toHaveBeenCalledOnce();
    expect(dependencies.writeError).not.toHaveBeenCalled();
    expect(dependencies.prepare).not.toHaveBeenCalled();
    expect(dependencies.isPortAvailable).not.toHaveBeenCalled();
    expect(dependencies.start).not.toHaveBeenCalled();
  });

  it('rejects invalid input before every launcher side effect', async () => {
    const dependencies = createDependencies();

    await expect(dispatchLauncher(['--port=70000'], dependencies)).resolves.toEqual({
      action: 'error',
      exitCode: 2,
    });
    expect(dependencies.writeError).toHaveBeenCalledWith(expect.stringContaining('Invalid --port value'));
    expect(dependencies.writeUsage).toHaveBeenCalledOnce();
    expect(dependencies.prepare).not.toHaveBeenCalled();
    expect(dependencies.isPortAvailable).not.toHaveBeenCalled();
    expect(dependencies.start).not.toHaveBeenCalled();
  });

  it('uses only an available explicit port', async () => {
    const dependencies = createDependencies((port) => port === 3125);

    await expect(dispatchLauncher(['--port', '3125'], dependencies)).resolves.toEqual({
      action: 'started',
      exitCode: 0,
      port: 3125,
    });
    expect(dependencies.prepare).toHaveBeenCalledWith({ explicitPort: 3125 });
    expect(dependencies.isPortAvailable).toHaveBeenCalledOnce();
    expect(dependencies.isPortAvailable).toHaveBeenCalledWith(3125);
    expect(dependencies.start).toHaveBeenCalledOnce();
    expect(dependencies.start).toHaveBeenCalledWith({ port: 3125, explicitPort: 3125 });
  });

  it('reports a busy explicit port without probing or starting an alternative', async () => {
    const dependencies = createDependencies(() => false);

    await expect(dispatchLauncher(['--port=3125'], dependencies)).resolves.toEqual({
      action: 'error',
      exitCode: 1,
    });
    expect(dependencies.isPortAvailable).toHaveBeenCalledTimes(1);
    expect(dependencies.isPortAvailable).toHaveBeenCalledWith(3125);
    expect(dependencies.start).not.toHaveBeenCalled();
    expect(dependencies.writeError).toHaveBeenCalledWith(expect.stringContaining('No server was started'));
  });

  it('preserves default first-available selection from port 3000', async () => {
    const probed = [];
    const selected = await selectLauncherPort({
      explicitPort: null,
      isPortAvailable: async (port) => {
        probed.push(port);
        return port === 3002;
      },
    });

    expect(selected).toBe(3002);
    expect(probed).toEqual([3000, 3001, 3002]);
  });

  it('prepares the default launch before probing, then starts the selected port', async () => {
    const order = [];
    const result = await dispatchLauncher([], {
      prepare: async () => { order.push('prepare'); },
      isPortAvailable: async (port) => {
        order.push(`probe:${port}`);
        return port === 3001;
      },
      start: async ({ port }) => { order.push(`start:${port}`); },
    });

    expect(result).toEqual({ action: 'started', exitCode: 0, port: 3001 });
    expect(order).toEqual(['prepare', 'probe:3000', 'probe:3001', 'start:3001']);
  });

  it('statically wires the real launcher through dispatch before cleanup or main', () => {
    const source = readFileSync(resolve(process.cwd(), 'scripts', 'prod_launcher.js'), 'utf8');
    const watchdogSource = readFileSync(resolve(process.cwd(), 'scripts', 'kill_when_parent_exits.js'), 'utf8');

    expect(source).toContain('runCli(process.argv.slice(2))');
    expect(source).not.toMatch(/\nmain\(\)\.catch/);
    expect(source).toContain('if (explicitPort === null) cleanupStaleServers();');
    expect(source.indexOf('dispatchLauncher(argv')).toBeLessThan(source.indexOf('await main(options)'));
    expect(source).toContain("const SERVER_HOST = '127.0.0.1';");
    expect(source).toContain('const provenance = readRuntimeProvenance(port);');
    expect(source).toContain('PVU_SERVER_PORT: String(provenance.serverPort)');
    expect(source).toContain('selectManagedProcessRoots(processes');
    expect(watchdogSource).not.toContain('Get-NetTCPConnection');
    expect(watchdogSource).not.toContain('killPortOwner');
  });
});

import { execFileSync, spawnSync } from 'node:child_process';
import os from 'node:os';

function runCommand(command, args) {
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    windowsHide: true,
  });

  if (result.status !== 0) {
    return null;
  }

  return (result.stdout || '').trim();
}

function readGitCommitSha() {
  try {
    return runCommand('git', ['rev-parse', 'HEAD']) || 'unknown';
  } catch {
    return 'unknown';
  }
}

function readPnpmVersion() {
  return runCommand('pnpm', ['--version']) || 'unknown';
}

function readWindowsVersion() {
  if (process.platform !== 'win32') {
    return null;
  }

  try {
    const output = execFileSync(
      'powershell.exe',
      [
        '-NoProfile',
        '-Command',
        '[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); (Get-CimInstance Win32_OperatingSystem).Caption',
      ],
      { encoding: 'utf8', windowsHide: true },
    );
    const caption = output.trim();
    return caption || `Windows ${os.release()}`;
  } catch {
    return `Windows ${os.release()}`;
  }
}

export function collectEnvironment(options = {}) {
  const {
    cacheState = 'unknown',
    buildMode = 'production',
    buildCommand = 'pnpm build',
  } = options;

  return {
    commitSha: readGitCommitSha(),
    nodeVersion: process.version,
    pnpmVersion: readPnpmVersion(),
    platform: process.platform,
    platformRelease: os.release(),
    arch: process.arch,
    windowsVersion: readWindowsVersion(),
    hostname: os.hostname(),
    cpuModel: os.cpus()[0]?.model ?? 'unknown',
    totalMemoryBytes: os.totalmem(),
    timestamp: new Date().toISOString(),
    buildMode,
    buildCommand,
    cacheState,
  };
}

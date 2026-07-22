const path = require('path');

function normalizeWindowsPath(value) {
  return path.win32.normalize(String(value)).replaceAll('/', '\\').toLowerCase();
}

function splitWindowsCommandLine(commandLine) {
  if (typeof commandLine !== 'string') return [];
  const args = [];
  let index = 0;
  while (index < commandLine.length) {
    while (/\s/.test(commandLine[index] ?? '')) index += 1;
    if (index >= commandLine.length) break;

    let argument = '';
    let quoted = false;
    while (index < commandLine.length) {
      let backslashes = 0;
      while (commandLine[index] === '\\') {
        backslashes += 1;
        index += 1;
      }

      if (commandLine[index] === '"') {
        argument += '\\'.repeat(Math.floor(backslashes / 2));
        if (backslashes % 2 === 1) {
          argument += '"';
        } else {
          quoted = !quoted;
        }
        index += 1;
        continue;
      }

      argument += '\\'.repeat(backslashes);
      if (index >= commandLine.length || (!quoted && /\s/.test(commandLine[index]))) break;
      argument += commandLine[index];
      index += 1;
    }
    args.push(argument);
  }
  return args;
}

function isManagedServerProcess(processInfo, { root, nextBin, selfPid }) {
  const processId = Number(processInfo?.ProcessId);
  if (!Number.isInteger(processId) || processId <= 0 || processId === selfPid) return false;

  const name = String(processInfo?.Name ?? '').toLowerCase();
  if (name !== 'node.exe') return false;
  const commandLine = processInfo?.CommandLine;
  if (typeof commandLine !== 'string' || commandLine.length === 0) return false;
  const args = splitWindowsCommandLine(commandLine);
  if (args.length < 2) return false;
  const entrypoint = normalizeWindowsPath(args[1]);

  const managedScripts = [
    path.join(root, 'scripts', 'prod_launcher.js'),
    path.join(root, 'scripts', 'serve_with_parent_watch.js'),
  ];
  if (managedScripts.some((scriptPath) => entrypoint === normalizeWindowsPath(scriptPath))) {
    return true;
  }

  return entrypoint === normalizeWindowsPath(nextBin) && args[2]?.toLowerCase() === 'start';
}

function selectManagedProcessRoots(processes, options) {
  const managed = processes.filter((processInfo) => isManagedServerProcess(processInfo, options));
  const managedIds = new Set(managed.map((processInfo) => Number(processInfo.ProcessId)));
  return managed.filter((processInfo) => !managedIds.has(Number(processInfo.ParentProcessId)));
}

module.exports = {
  isManagedServerProcess,
  selectManagedProcessRoots,
  splitWindowsCommandLine,
};

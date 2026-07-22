import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const {
  isManagedServerProcess,
  selectManagedProcessRoots,
  splitWindowsCommandLine,
} = require('./prod_launcher_processes.js');

const root = String.raw`C:\PV Fixtures\PhotoViewer`;
const nextBin = String.raw`C:\PV Fixtures\PhotoViewer\node_modules\next\dist\bin\next`;
const options = { root, nextBin, selfPid: 700 };

function processInfo(ProcessId, CommandLine, ParentProcessId = 1, Name = 'node.exe') {
  return { ProcessId, ParentProcessId, Name, CommandLine };
}

describe('production launcher process ownership', () => {
  it('matches exact managed paths regardless of Windows path case or quoting', () => {
    expect(splitWindowsCommandLine(
      String.raw`"C:\Program Files\nodejs\node.exe" "c:\PV FIXTURES\PHOTOVIEWER\scripts\prod_launcher.js"`,
    )).toEqual([
      String.raw`C:\Program Files\nodejs\node.exe`,
      String.raw`c:\PV FIXTURES\PHOTOVIEWER\scripts\prod_launcher.js`,
    ]);
    expect(isManagedServerProcess(processInfo(
      701,
      String.raw`"C:\Program Files\nodejs\node.exe" "c:\PV FIXTURES\PHOTOVIEWER\scripts\prod_launcher.js"`,
    ), options)).toBe(true);
    expect(isManagedServerProcess(processInfo(
      701,
      String.raw`node "C:\PV Fixtures\PhotoViewer\scripts\serve_with_parent_watch.js" 700 3013 127.0.0.1`,
    ), options)).toBe(true);
  });

  it('rejects prefix, suffix, and unrelated argument false positives', () => {
    expect(isManagedServerProcess(processInfo(
      701,
      String.raw`node C:\PV Fixtures\PhotoViewer-old\scripts\prod_launcher.js`,
    ), options)).toBe(false);
    expect(isManagedServerProcess(processInfo(
      702,
      String.raw`node C:\tools\report.js --label=C:\PV Fixtures\PhotoViewer --mode=prod_launcher`,
    ), options)).toBe(false);
    expect(isManagedServerProcess(processInfo(
      703,
      String.raw`node C:\tools\prod_launcher.js --source C:\PV Fixtures\PhotoViewer`,
    ), options)).toBe(false);
    expect(isManagedServerProcess(processInfo(
      704,
      String.raw`node -e "setInterval(() => {}, 1000)" "C:\PV Fixtures\PhotoViewer\scripts\prod_launcher.js"`,
    ), options)).toBe(false);
  });

  it('matches the exact checkout Next start process but not another checkout', () => {
    expect(isManagedServerProcess(processInfo(
      701,
      `node "${nextBin}" start --hostname 127.0.0.1 --port 3013`,
    ), options)).toBe(true);
    expect(isManagedServerProcess(processInfo(
      702,
      String.raw`node C:\PV Fixtures\PhotoViewer-copy\node_modules\next\dist\bin\next start -p 3013`,
    ), options)).toBe(false);
  });

  it('kills only the highest owned process when managed descendants share a tree', () => {
    const launcher = processInfo(
      701,
      String.raw`node "C:\PV Fixtures\PhotoViewer\scripts\prod_launcher.js"`,
    );
    const wrapper = processInfo(
      702,
      String.raw`node "C:\PV Fixtures\PhotoViewer\scripts\serve_with_parent_watch.js"`,
      701,
    );
    const next = processInfo(703, `node "${nextBin}" start`, 702);
    const unrelated = processInfo(704, String.raw`node C:\tools\server.js`, 701);

    expect(selectManagedProcessRoots([launcher, wrapper, next, unrelated], options))
      .toEqual([launcher]);
  });

  it('never identifies the current launcher or non-node executables', () => {
    expect(isManagedServerProcess(processInfo(
      700,
      String.raw`node C:\PV Fixtures\PhotoViewer\scripts\prod_launcher.js`,
    ), options)).toBe(false);
    expect(isManagedServerProcess(processInfo(
      701,
      String.raw`python C:\PV Fixtures\PhotoViewer\scripts\prod_launcher.js`,
      1,
      'python.exe',
    ), options)).toBe(false);
    expect(isManagedServerProcess(processInfo(
      702,
      String.raw`cmd.exe /d /s /c "node C:\PV Fixtures\PhotoViewer\scripts\prod_launcher.js"`,
      1,
      'cmd.exe',
    ), options)).toBe(false);
  });
});

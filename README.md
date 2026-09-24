# Aibos Image

Aibos Image is a local-first native image workspace for Windows. The current
product in this repository is the WPF application.

This is an early public source snapshot, not a hosted service or a release
quality claim.

## Requirements and launch

- Windows
- .NET 10 SDK

```powershell
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
.\start_aibos.bat
```

`start_wpf.bat` remains as a compatibility entry point.
Normal batch startup returns after launching Aibos, so a double-clicked CMD
window closes without closing Aibos. Both the apphost and local dotnet host
start without a console; an existing terminal is left open and may be closed
independently. Build errors and immediate startup errors remain visible.
`PHOTOVIEWER_WPF_DOTNET_RUN=1` is the foreground development exception.
When a rebuild is required, the launcher prefers the local .NET 10 SDK and
uses a one-shot build that does not retain a shared compiler or build server.
Repair builds regenerate outputs without incremental reuse before recording
their hashes, including host configuration files with unchanged timestamps.
An external Enhancement companion must be selected explicitly with
`AIBOS_COMPANION_ROOT` by its trusted dispatcher. The public launcher does not
guess a private companion root from unrelated Git worktrees.

For a desktop shortcut whose Aibos process is independent of the program that
requested the launch, install the per-user Task Scheduler dispatcher:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-aibos-desktop-launcher.ps1
```

Pass `-CompanionRoot <path>` only when selecting a trusted external Enhancement
companion explicitly. The shortcut asks Task Scheduler to start Aibos in the
interactive user session. Add `-AutoStartCompanion` to explicitly enable API
startup together with Aibos; it does not recover or resume generation Jobs.
Without that switch startup remains lazy. Jobs also offers API Start, Restart,
and Stop. Stop/Restart require interruption confirmation and fresh authenticated
server identity. An unverified listener is never stopped. Window close still
leaves reused servers and accepted durable work alone.
Each fresh application start still runs the normal
source-revision and source-content check, rebuilding the local Release target
when the current checkout has changed. Re-running the installer updates both
the task action and the desktop shortcut to the current repository path.
Clicking the shortcut again activates the existing window without rebuilding
or restarting it. A fresh start verifies the apphost, managed assembly, host
configuration, and the required local SQLite dependency assemblies together.
The source check also evaluates MSBuild input paths without building, including
embedded JSON, linked resources, icons and files copied to the output. Changing
their content is detected even when their timestamps are unchanged. Failed or
incomplete input evaluation refuses launch instead of trusting the old output.
A configured Companion that is temporarily
unavailable does not prevent ordinary viewing.
The scheduler accepts subsequent requests even while a previous Companion
remains alive. A per-user/session startup mutex serializes preparation until
the application accepts activation; it is not held for the Companion lifetime.
New installations use a task name derived from the Windows user identity.
To update an older named registration in place, pass its existing `-TaskName`.
The installer rejects another owner's task or an unrelated shortcut and rolls
back task registration if shortcut publication fails.
Task and shortcut updates, including readback and rollback, share one short
per-user configuration transaction across Windows sessions. A competing update
refuses after three seconds without reading or changing the task. The internal
cutover task-closure adapter uses that same transaction. This exclusion ends
when the operation returns; it is not persistent closure across a restart.

An explicit, passive maintenance enrollment inspection is available through
`scripts/start-aibos-desktop.ps1 -EnrollmentEnhancementRoot <existing-enhance-directory>`.
Use `-EnrollmentJobsFileName jobs.sqlite3` when that is the configured Jobs store.
The expected directory must match the application's independently resolved store;
this option does not select or change it. Inspection requires an already current,
recorded Release build, rejects an existing WPF instance and does not activate a
window, start the Companion, rebuild or modify durable state. A completed probe
returns JSON and exit code **10** for a verified handoff with enrollment evidence
unavailable, or **2** for rejection. Earlier launcher failures return **2** with
a diagnostic on standard error. All outcomes leave enrollment and maintenance disabled.
A successful handoff does not prove that older execution or external work ended.
The [Inbox contract](contracts/enhancement-enqueue-inbox-v1.json) defines the
additional evidence needed before maintenance can be enabled.

An explicit cold launch of one recorded Release artifact set is available through
`scripts/start-aibos-desktop.ps1 -PinnedLaunchManifestSha256 <launchManifestSha256>`.
Use the manifest returned by `scripts/check-wpf-launch-target.ps1 -Json` for an
already prepared, current Release build. This route refuses development/rebuild
settings, automatic Companion startup and an existing WPF instance. The application
independently checks the manifest and holds read handles on its eight recorded
artifacts for its lifetime, preventing cooperating file replacement. No build or
ordinary activation fallback occurs. Launcher exit code zero means dispatch only;
it does not certify completed startup, managed launch closure, root enrollment or
external-work settlement.

To configure a desktop task and shortcut for that same cold-launch route, pass
`-PinnedLaunchManifestSha256 <launchManifestSha256>` to the desktop installer.
The shortcut carries the manifest and optional Companion path, bypasses ordinary
window activation, and checks that the enabled task has the expected owner,
launcher and exact arguments before dispatch. Read and dispatch share the short
configuration mutex with cooperating installers and task closure. A changed or
disabled task refuses; no alternative task or normal-launch fallback is used.
Repeated clicks do not activate an existing window in this explicit cold mode.
Reinstalling without this option explicitly restores the normal desktop route.
This optional configuration does not close other entry points, pin launcher
scripts or prove whole-process/dependency lifetime. No installed configuration
changes until the installer is explicitly run.

The internal `scripts/lib/CutoverDesktopGraph.ps1` observer records one such
route: the exact task plan, the existing shortcut and a fixed nine-script
dependency inventory. It holds file read handles during capture, checks native
resolved paths and shortcut fields, and requires the selected scripts to match
the observer's package. Files are bounded to 64 KiB for the shortcut and 1 MiB
per script. Saved-plan replay checks the same inventory and either the original
task definition or its disabled definition. A missing, changed, retargeted or
aliased file refuses verification. This is a bounded observation for the upper
cutover transaction; it neither discovers other entry points nor freezes paths
after return, proves external-work lifetime, requests restart or grants repair.

The read-only `scripts/lib/CutoverBootEvidence.ps1` component captures an exact
System-log anchor and observes a later, operation-bound OS shutdown/startup sequence.
The request comment binds both the operation ID and the resource/launch configuration
digest. It checks the local host and OS profile, comment, shutdown/startup events,
current boot time, and the original log record again after enumeration. Missing,
changed, canceled, unexpected or unsupported evidence refuses observation.
This is one input to an initial managed cutover, not a completed cutover command:
managed launch closure, external-work lifetime and durable enrollment are still
required. The component cannot request a restart, change launch settings or enable
maintenance. Its verifier uses synthetic Windows XML and replaces only local OS
read boundaries; actual target-host cutover acceptance remains separate.
The caller must durably fix the original anchor, binding and requested API/flags
before requesting restart; it cannot reconstruct that intent from this result.
The result observes OS generation change, not normal shutdown completion or proof
that a particular restart API completed exactly as requested.

The internal `MaintenanceCutoverIntentStore` freezes that pre-request intent as
one bounded `intent.json` in an explicitly supplied, existing control directory.
It binds the operation, canonical configuration digest, original OS anchor and
exact restart API/flags/comment. Same-intent replay returns the original bytes;
a different operation, recaptured anchor, changed configuration or unsupported
record refuses replacement. Resume preserves compatible additive fields and
does not create missing storage. Interrupted temporary files remain uncommitted
and are retained. Saving this intent does not establish that named launch entries
are closed or that the selected lifetime profile is supported.

`scripts/invoke-aibos-cutover.ps1` connects the pinned desktop route to that store
through the explicit headless WPF `--maintenance-cutover-intent` entry. All phases
require an existing control directory, operation ID and current recorded Release
manifest. `Prepare` also requires the selected task, shortcut and lifetime-profile
identifier: it observes the route, obtains an independently resolved Jobs/root
binding from WPF, captures the operation-bound OS anchor, and saves the immutable
intent. WPF holds the actual artifact set and root handle during its operation.
Its input is bounded to 128 KiB with a ten-second read deadline. No normal window,
worker or ordinary activation is started by this entry.

`Arm` resumes the saved intent, closes ordinary cooperating intake using the
existing `enqueue-inbox/maintenance.json` presence gate, and disables only its
exact task. The marker binds the operation, configuration and original intent;
its publication uses the shared Jobs lock and held native root/inbox handles.
An immutable `admission.json` in the control directory records the marker hash
and publication-completion time. A marker published before an interrupted receipt
write can be verified and completed without replacement. Once a receipt exists,
a missing or changed marker refuses replay; it is never silently recreated.
Unsupported or another operation's marker is preserved. Compatible additive
fields remain in the original bytes. Neither file is aged out or removed by
startup, timeout, observation or this command.

Repeating Arm does not rewrite those records or issue another task disable.
It refuses after observing a restart request, OS-generation change or lost
original anchor, and rechecks after closure.
`Observe` resumes that same intent, verifies the closed route and reads the bound
OS sequence. It requires the admission receipt to strictly precede the observed
request, and verifies admission again before returning. Neither phase recaptures the anchor or silently re-closes a task
after restart. `-WhatIf` returns before artifact, OS or scheduler access.
These phases do not request a restart or enable enrollment/repair. Persistent
normal-intake closure does not prove complete managed-entry continuity or
supported external-work lifetime; those still need independent evidence. The
cutover marker is not a repair-engine operation: a verified handoff to repair is
still required and this command does not clear the gate. The supplied
lifetime-profile name is recorded, not certified. Existing data is preserved;
the command never creates a missing control directory or replaces an intent.

The internal `scripts/lib/CutoverManagedTask.ps1` adapter prepares a bounded
snapshot of one explicitly named desktop launch task. It checks the current
owner, launcher action and absence of triggers, then binds the full definition
and the expected definition with only `Enabled` changed to false. After the
upper transaction durably saves this plan, its Arm step can disable that task
and read the definition back. An already closed task needs no repeated write;
changed or unreadable state refuses success. After a restart request, the
read-only assertion rejects a reopened task without silently disabling it again.
The explicit command above uses this adapter. It does not stop
existing processes, close other launch paths or prove continuous closure under
concurrent configuration changes. The upper transaction must coordinate managed
configuration changes and establish the complete finite launch cohort.

## Product boundary

- Normal viewing and state changes do not rewrite source images.
- Source deletion is explicit and goes through the Windows Recycle Bin.
- Browsing, search, navigation, state loading, health checks, and Jobs viewing
  do not enqueue AI work or start workers.
- Ordinary viewing is local and does not require Node.js or a web UI.
- The optional Enhancement companion is an authenticated API bound only to
  `127.0.0.1`. It does not load or open a UI. AI recovery and
  processing remain behind an explicit user action.
- LAN, tunnel, reverse-proxy, hosted, and Internet exposure are outside the
  supported boundary.

## Data and compatibility

Versioned durable formats cover Favorites, Seen state, settings, Albums,
Search History, recent folders, and Enhancement Jobs. Presentation-only state
stays local to WPF.

Start at [`contracts/index.json`](contracts/index.json), then read only the
contract or synthetic fixture relevant to a change. Stable cross-cutting
semantics are in [`docs/product-contract.md`](docs/product-contract.md).
Documentation authority, code ownership, state ownership, and critical-flow
routing are indexed in [`docs/index.md`](docs/index.md).
The [storage layout map](docs/architecture/storage-layout.md) distinguishes
executable deployment, durable data, models, and generated maintenance targets.
A directory named `.cache` may contain durable data and must not be removed
based on its name.

`PhotoViewer`, `photoviewer`, and `Browser` still appear in assemblies,
paths, environment variables, and fixtures as compatibility identifiers. They
must not be renamed without a non-destructive migration.

The former Browser/Next.js implementation is preserved in an
[archived repository](https://github.com/a9ui/tools-h000025-photoviewer). It is
historical evidence, not an active product or contract authority.

## Development and verification

Focused verifiers live under `scripts/` and use synthetic data under the
operating-system temporary directory. Choose the verifier for the changed
surface; do not run every verifier for an unrelated documentation edit.

Common entry points include:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-public-surface.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-contract-index.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-shared-state-contracts.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-enhancement-jobs-workspace.ps1
```

Repository instructions for agents are in [`AGENTS.md`](AGENTS.md). Security
and disclosure rules are in [`SECURITY.md`](SECURITY.md).

Use the same product and verification requirements with any coding agent.
Keep personal model profiles, experimental settings, credentials, and external
review destinations in the operator's local configuration, outside this
repository. Optional tools are selected for the current task, not installed as
a prerequisite for every change.

| Changed behavior | Focused verification entry point |
|---|---|
| Launch freshness / Release artifacts | `scripts/verify-wpf-launch-target.ps1` (synthetic artifacts and real .NET repair build, no WPF launch) |
| Passive enrollment handoff / pinned cold launch | `scripts/verify-enrollment-handoff.ps1` (isolated source-linked processes, artifact locks and TEMP launcher fixtures; no production enrollment) |
| Enrollment startup / read deadline / invalid pinned launch | `scripts/verify-wpf-enrollment-startup.ps1 -WpfDll <built-dll>` (actual headless WPF startup; invalid requests refuse before instance or store activation) |
| CMD-independent launch / argument forwarding | `scripts/verify-wpf-launch-detachment.ps1` (bounded synthetic apphost and dotnet children; no real Aibos state) |
| Retained server / desktop relaunch | `scripts/verify-desktop-relaunch.ps1` (unique real scheduled task and synthetic processes; requires a Windows interactive session) |
| Desktop repeat activation | `scripts/verify-desktop-activation.ps1` (synthetic identity against the WPF coordinator) |
| Desktop installation / rollback | `scripts/verify-desktop-launcher-install.ps1` (TEMP shortcuts, mocked scheduler writes, native PowerShell argument round trips including trailing path separators) |
| Initial cutover OS evidence | `scripts/verify-cutover-boot-evidence.ps1` (synthetic System events, no OS changes) |
| Initial cutover task closure | `scripts/verify-cutover-managed-task.ps1` (mocked scheduler reads and writes, definition drift and readback failures) |
| Pinned desktop route observation | `scripts/verify-cutover-desktop-graph.ps1` (real TEMP shortcut/file handles, fixed script inventory, mocked task closure, saved-plan replay) |
| Initial cutover intent | `scripts/verify-cutover-intent.ps1` (includes task closure checks, source-linked TEMP store, three owned process exits, saved-intent/task-adapter/OS-reader interoperability) |
| Cutover phase integration | `scripts/verify-cutover-flow.ps1 -WpfDll <built-dll>` (actual WPF/store with isolated paths and mocked scheduler/OS/provenance boundaries) |
| Desktop startup errors | `scripts/verify-desktop-launch-errors.ps1` (TEMP launchers, observed native error dialogs) |
| Companion launch options / photoreal enqueue | `scripts/verify-wpf-modal-photoreal.ps1` |
| Video retry source / publication pin | `scripts/verify-wpf-enhancement-operation-filter.ps1` |

Read each verifier's parameters and side effects first. The launcher verifiers
retain their synthetic fixtures under TEMP for diagnosis. Run relevant local
checks once per changed input, then use the existing PR workflow for the
aggregate checks. A local checkpoint, a reviewed PR, and a deployed desktop
application are distinct results; report which one was verified.

## Privacy when reporting bugs

Use synthetic files and redact local data. Follow [`SECURITY.md`](SECURITY.md)
for the publication boundary and private vulnerability reporting; never put
sensitive security details in a public issue.

## License status

No license is currently granted. Public source visibility does not grant
permission to use, copy, modify, or redistribute the repository beyond rights
provided by applicable law. A future `LICENSE` file, if added, supersedes this
notice. Third-party dependencies retain their own licenses.

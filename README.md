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
When a rebuild is required, the launcher prefers the local .NET 10 SDK and
uses a one-shot build that does not retain a shared compiler or build server.
An external Enhancement companion must be selected explicitly with
`AIBOS_COMPANION_ROOT` by its trusted dispatcher. The public launcher does not
guess a private companion root from unrelated Git worktrees.

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

## Privacy when reporting bugs

Use synthetic files and redact local data. Follow [`SECURITY.md`](SECURITY.md)
for the publication boundary and private vulnerability reporting; never put
sensitive security details in a public issue.

## License status

No license is currently granted. Public source visibility does not grant
permission to use, copy, modify, or redistribute the repository beyond rights
provided by applicable law. A future `LICENSE` file, if added, supersedes this
notice. Third-party dependencies retain their own licenses.

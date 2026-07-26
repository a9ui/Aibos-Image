# Aibos Image

Aibos Image is a local-first native image workspace for Windows. This
repository is the development authority for the WPF application. The Browser
application is maintained independently in
[`a9ui/tools-h000025-photoviewer`](https://github.com/a9ui/tools-h000025-photoviewer).
Browser UI parity is not a completion gate for this repository.

This repository is an early public source snapshot. It is not a hosted service,
not a release-quality claim, and may contain known product defects that are
being tracked and fixed incrementally.

## Requirements and launch

- Windows;
- .NET 10 SDK.

```powershell
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
.\start_aibos.bat
```

`start_aibos.bat` launches the native WPF application directly. The compatible
`start_wpf.bat` entry point remains available.

## Security boundary

Aibos Image reads user-selected image folders. Normal browsing, metadata
inspection, Favorite, Seen, Album, Search History, and Enhancement state do not
rewrite source images. Source deletion is a separate explicit action and uses
the operating system Recycle Bin.

Ordinary viewing is fully local and does not require a Browser or Node.js
runtime. Optional Enhancement begins only after an explicit user action and may
call the separately installed H25 Browser application over loopback as a local
companion. That companion must remain bound to `127.0.0.1`; LAN, tunnel,
reverse-proxy, hosted, and Internet exposure are outside the product boundary.
If the companion is not already running, pressing an AI Start/Retry action may
start it without opening the Browser UI. Aibos never starts it during browsing,
preview, search, navigation, or passive job inspection, and stops only the
exact companion process tree that the current Aibos process created.

## Cross-repository durable state

The WPF and Browser applications remain independent products but will read and
write one versioned durable-state contract. The shared set is intended to
include:

- `favorites.json`, `seen.json`, `settings.json`, `albums.json`,
  `search-history.json`, and `recent-folders.json`;
- `enhance/jobs.json` and the managed files under `enhance/outputs/**`.

Renderer-local presentation state, including WPF window geometry, panel sizes,
keyboard bindings, current selection, and preview layout, stays local. In
particular, the existing WPF `state.json` is not shared wholesale.

Normal application startup remains reader-only: it never creates a locator,
shared root, durable-data directory, or store. Its only operational write is an
empty lock file under the operating-system temporary directory. A separate
`.NET 10` command-line tool, `Aibos.SharedRootSetup`, can perform the reviewed
one-time creation of the default locator after an inspection-only preflight and
an explicit `--apply --confirm CREATE`. It is create-only, requires the
protocol-global writer lease, and refuses malformed, future, unreadable,
ambiguous, redirected, or conflicting state. It does not copy, merge,
initialize, rewrite, or delete durable state; root migration and locator
replacement remain disabled.

The WPF application fixes the seven durable-store paths from one validated root
for the process lifetime while preserving explicit per-store test overrides.
Shared settings protect unsupported or unreadable documents, fail safe to
delete confirmation enabled, and preserve unknown fields; a supported shared
recent-folder document is authoritative, including an explicit empty folder
set. The exact H25 Browser reader and cross-repository TEMP matrix must remain
green.

## Verification

```powershell
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-shared-root-setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-legacy-asset-ledger.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-parity-foundation.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-modal-interaction.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-ui-language.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-shared-root-locator.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-thumbnail-status-borders.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-shared-recent.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-cross-repo-shared-root-paths.ps1 -LegacyRepo <path> -BrowserCommit <sha>
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-album-library-hardening.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-modal-enhancement-actions.ps1
```

Additional focused WPF verifiers live under `scripts/` and use synthetic
fixtures under the operating-system temporary directory.

## Product contract and compatibility identifiers

WPF behavior and the cross-repository durable-state boundary are defined in
[`docs/product-contract.md`](docs/product-contract.md). Some technical paths,
assemblies, namespaces, cache keys, environment variables, and fixtures still
use `PhotoViewer`, `photoviewer`, or `Browser`. They are compatibility
identifiers and remain stable until a separately tested, non-destructive
migration exists.

## Privacy when reporting bugs

Do not attach personal images, unredacted absolute paths, cache/state files,
logs, database files, environment files, credentials, or private URLs to a
public issue or pull request. Use synthetic files under a temporary directory.

Security reports should use GitHub private vulnerability reporting when it is
available. If the private form is unavailable, do not disclose sensitive
details in a public issue.

## License status

No license is currently granted. The source is publicly visible, but this
repository is not described as open source and no permission to use, copy,
modify, or redistribute is granted beyond rights provided by applicable law.
A future `LICENSE` file, if added, will supersede this notice.

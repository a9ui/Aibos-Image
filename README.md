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

The current public-foundation milestone does not choose a shared root and does
not move, merge, initialize, rewrite, or delete existing state. A read-only
ledger and a versioned, repository-independent locator will be introduced in
separate reviewed changes before either application changes its write target.

## Verification

```powershell
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-modal-interaction.ps1
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

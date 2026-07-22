# Aibos Image

Aibos Image is a local-first image workspace for Windows. Its current public
feature set is an image library and viewer with two supported renderers:

- a Browser renderer built with Next.js;
- a native WPF renderer built for .NET 8 on Windows.

This repository is an early public source snapshot. It is not a hosted service,
not a release-quality claim, and may contain known product defects that are
being tracked and fixed incrementally.

## Security boundary

The Browser runtime must bind to `127.0.0.1`. Aibos Image is not designed for
LAN or Internet exposure, reverse proxies, tunnels, or hosted deployment. The
WPF renderer runs locally and does not require the Browser server for ordinary
viewing.

Aibos Image reads user-selected image folders. Source images are not rewritten
by normal browsing, metadata inspection, Favorite, Seen, Album, or Enhancement
state. Source deletion is a separate explicit action and uses the operating
system Recycle Bin.

As a non-exhaustive safety summary, Optional Enhancement work starts only after
an explicit user action. Ordinary browsing, preview, search, modal navigation,
and state hydration must not enqueue Enhancement jobs or start workers. The
product contract defines the normative shared behavior.

## Start on Windows

Double-click `start_aibos.bat` and choose **Browser** or **WPF**. The two
renderer-specific launchers remain available for direct use, but the selector
is the canonical interactive entry point.

## Browser renderer

Requirements:

- Node.js 20.9 or later;
- Corepack with pnpm 11 or later.

```powershell
corepack pnpm install --frozen-lockfile
corepack pnpm dev
```

The development server listens on `127.0.0.1`. A Windows production launcher
is also available as `start_viewer.bat`.

## WPF renderer

Requirements:

- Windows;
- .NET 8 SDK.

```powershell
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
.\start_wpf.bat
```

## Verification

```powershell
corepack pnpm test:unit
corepack pnpm typecheck
corepack pnpm lint
corepack pnpm build
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --nologo
```

Additional focused WPF and cross-runtime verifiers live under `scripts/`.

## Product contract

Browser and WPF are independent renderers of one product. Shared behavior and
state ownership are defined in [docs/product-contract.md](docs/product-contract.md).

## Compatibility identifiers

Some technical paths, assemblies, namespaces, cache keys, and test fixture
names still use `PhotoViewer` or `photoviewer`. They are compatibility
identifiers, not the public product name. They remain stable until a separate
migration preserves existing installations and user state.

## Privacy when reporting bugs

Do not attach personal images, unredacted absolute paths, cache/state files,
logs, database files, environment files, credentials, or private URLs to a
public issue or pull request. Use synthetic files under a temporary directory
for reproduction.

Security reports should use GitHub's private vulnerability reporting when it is
available. If the private form is unavailable, do not disclose sensitive
details in a public issue.

## License status

No license is currently granted. The source is publicly visible, but this
repository is not described as open source and no permission to use, copy,
modify, or redistribute is granted beyond rights provided by applicable law.
A future `LICENSE` file, if added, will supersede this notice.

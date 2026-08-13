# Aibos Image Agent Guide

This file contains public repository instructions for automated coding agents.
It must not depend on a private workspace, private tracker, personal machine
state, or a particular AI product.

## Read first

Before changing code, read:

1. `README.md`;
2. `SECURITY.md`;
3. `docs/product-contract.md`;
4. `contracts/parity-v1.json` when changing a registered shared-state meaning;
5. `contracts/shared-root-locator-v1.json` when changing shared-root discovery;
6. `contracts/enhancement-enqueue-inbox-v1.json` when changing explicit
   Enhancement registration or delivery;
7. any more specific `AGENTS.md` closer to the files being changed.

`docs/product-contract.md` is normative for this WPF application and for the
cross-repository durable-state boundary. Historical documents, screenshots,
tests, and the behavior of only one application do not override it.

## Repository authority

- Maintain the native WPF application in this repository.
- The active product lane is Aibos WPF plus its optional loopback Enhancement
  companion. The historical Browser/Next.js product is out of scope unless the
  repository owner explicitly reactivates it in a later instruction.
- Do not inspect, start, benchmark, modify, import, or use Browser UI/runtime
  source, Browser caches, Browser screenshots, or Browser tests as product
  evidence. This restriction also applies during performance and cleanup work.
- Shared-state compatibility is contract-only. Use versioned public contracts
  and synthetic fixtures; do not require Browser UI/runtime parity or a Browser
  checkout.
- Treat `Aibos Image` as the public product name and `Aibos` as its compact UI
  label. Legacy `PhotoViewer` assembly, namespace, cache, and persistence names
  are compatibility identifiers; do not rename them without a tested migration.
- Do not introduce or extend the legacy WinForms renderer. It is frozen and is
  not included in this repository.

## Non-negotiable safety boundaries

- Normal viewing and state changes must not rewrite source images.
- Source deletion is explicit and uses the operating system Recycle Bin. Do
  not add a permanent-delete fallback.
- Enhancement starts only from an explicit user action. Browsing, preview,
  search, modal navigation, and state hydration must not enqueue jobs or start
  workers.
- Ordinary WPF viewing must not require a Browser or Node.js runtime. The
  optional dedicated H25 Enhancement API companion is loopback-only, must
  remain bound to `127.0.0.1`, and must not load, inspect, or open the Browser
  UI.
- Treat file paths, process arguments, image metadata, loopback responses, and
  durable-state files as untrusted input. Preserve validation and resource
  bounds.
- Preserve unrelated and unknown fields where the format permits it. Mutations
  must use the latest on-disk state and remain non-destructive on malformed or
  unsupported future versions.
- Never delete or reset user images, caches, settings, Albums, Search History,
  Favorites, Seen state, Enhancement jobs/outputs, or other persistence as a
  repair strategy.

## Shared-state changes

The WPF and Browser applications are independently versioned. Only their
durable-state protocol is shared. Use reader-first rollout:

1. define the versioned contract and synthetic fixtures here;
2. prove both readers against the same exact fixture revision;
3. preserve unknown fields and reject future or malformed state without writes;
4. enable a new writer in one application only after both readers are green;
5. enable the second writer after cross-repository simultaneous-writer tests.

Do not treat a vendored H25 fixture as a second source of truth. It must identify
this canonical repository, path, contract version, and source commit SHA.
Renderer-local presentation state remains local; do not share WPF `state.json`
wholesale.

## Privacy-safe development

- Use synthetic files under a temporary directory for tests and reproduction.
- Never commit personal images, screenshots containing personal data,
  unredacted home-directory paths, email addresses, credentials, cookies,
  private URLs, cache/state files, databases, logs, environment files, scanner
  reports, or generated build output.
- Redact machine-specific paths from test output and public issue text.
- Do not publish a vulnerability, proof of concept, secret, or private path in
  a public issue. Follow `SECURITY.md`.

## Working-tree hygiene

- Inspect `git status` before editing and preserve unrelated user changes.
- Do not use destructive reset or checkout commands to discard work.
- Keep changes reviewable. Do not combine repository-boundary work,
  shared-state migration, framework migration, structural refactoring, and
  visual redesign in one patch.
- Do not commit WPF `bin`/`obj`, caches, reports, or local runtime artifacts.
- Do not change or add a repository license without an explicit owner decision.
  Until a `LICENSE` file exists, do not describe this repository as open source.

## Verification

Build WPF without relying on a previously built executable:

```powershell
$artifacts = Join-Path $env:TEMP ("aibos-wpf-agent-build-" + [guid]::NewGuid().ToString("N"))
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj `
  -c Release `
  --artifacts-path $artifacts `
  --nologo
```

Run the smallest relevant focused verifier from `scripts/`, then the aggregate
GitHub Actions gate once for the candidate SHA. All destructive, malformed,
concurrent-writer, and persistence tests must use isolated TEMP fixtures. Do
not start the real H25 Browser application or touch its user state for a test;
use the bounded fake loopback server in the existing Enhancement verifier.

Report completion in this order: meaning, evidence, remaining risk, then the
relevant Issue or pull request.

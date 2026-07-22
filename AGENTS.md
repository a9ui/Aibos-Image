# Aibos Image Agent Guide

This file contains public repository instructions for automated coding agents.
It must not depend on a private workspace, private issue tracker, personal
machine state, or a particular AI product.

## Read first

Before changing code, read:

1. `README.md`
2. `SECURITY.md`
3. `docs/product-contract.md`
4. `contracts/parity-v1.json` when changing a registered shared behavior
5. any more specific `AGENTS.md` closer to the files being changed

`docs/product-contract.md` is the normative source for product behavior shared
by the Browser and WPF renderers. Historical documents, screenshots, tests, and
the current behavior of only one renderer do not override it.

## Product scope

- Maintain one Aibos Image product with two independent renderers: Browser and
  WPF.
- Treat `Aibos Image` as the public product name and `Aibos` as its compact UI
  label. Legacy `PhotoViewer` assembly, namespace, cache, and persistence names
  are compatibility identifiers; do not rename them without a tested migration.
- Keep ordinary WPF viewing independent of the Browser server.
- Do not introduce, restore, or extend the legacy WinForms renderer; it is not
  included in this public repository.
- Prefer the smallest compatible change over a rewrite or framework migration.
- Preserve existing user workflows unless the product contract and regression
  coverage intentionally change them.

## Non-negotiable safety boundaries

The following is a non-exhaustive operational summary. The exact shared product
semantics remain in `docs/product-contract.md`.

- Keep the Browser runtime bound to `127.0.0.1`. Do not add LAN, tunnel,
  reverse-proxy, hosted, or Internet deployment support.
- Normal viewing and state changes must not rewrite source images.
- Source deletion is explicit and uses the operating system Recycle Bin. Do not
  add a permanent-delete fallback.
- Enhancement starts only from an explicit user action. Browsing, preview,
  search, modal navigation, and state hydration must not enqueue jobs or start
  workers.
- Treat file paths, process arguments, image metadata, network responses, and
  shared-state files as untrusted input. Keep validation and resource bounds in
  place.
- Preserve unrelated and unknown fields in shared state when the format permits
  it. Mutations must use the latest on-disk state and remain non-destructive on
  malformed or unsupported future versions.
- Do not delete or reset user caches, settings, state databases, or source
  images as a repair strategy.

## Browser/WPF parity

For behavior owned by both renderers:

1. state the product-contract decision, or state that the contract is unchanged;
2. inspect both Browser and WPF implementations;
3. update both when the product meaning changes;
4. add shared or equivalent regression coverage for both;
5. record an explicit reason when one renderer is not applicable.

A one-renderer patch is not a complete shared-behavior fix merely because its
local tests pass.

Stable `PV-*` identifiers live in the normative product contract. The shared
vectors in `contracts/parity-v1.json` must be consumed by parity runners that
exercise each renderer's production behavior implementation. Do not copy their
expected results into renderer-owned fixtures or change only one consumer to
make a parity failure green.

Share meanings, schemas, fixtures, action identifiers, and design tokens where
useful. Do not make WPF depend on Node.js, force both renderers through one
runtime, or duplicate renderer-specific UI code merely to make files look
similar.

## Privacy-safe development

- Use synthetic files under a temporary directory for tests and reproduction.
- Never commit personal images, screenshots containing personal data,
  unredacted home-directory paths, email addresses, credentials, cookies,
  private URLs, cache/state files, databases, logs, environment files, scanner
  reports, or generated build output.
- Redact machine-specific paths from test output and issue text.
- Do not publish a vulnerability, proof of concept, secret, or private path in
  a public issue. Follow `SECURITY.md`.

## Working-tree hygiene

- Inspect `git status` before editing and preserve unrelated user changes.
- Do not use destructive reset or checkout commands to discard work.
- Keep changes reviewable and avoid mixing behavior changes, broad file moves,
  dependency migrations, and visual redesign in one patch.
- Do not commit `node_modules`, `.next`, WPF `bin`/`obj`, caches, reports, or
  local runtime artifacts.
- Do not change or add a repository license without an explicit owner decision.
  Until a `LICENSE` file exists, do not describe this repository as open source.

## Verification

Use Corepack for JavaScript commands:

```powershell
corepack pnpm install --frozen-lockfile
corepack pnpm test:unit
corepack pnpm typecheck
corepack pnpm lint
corepack pnpm build
corepack pnpm verify:contracts
```

Build WPF without relying on a previously built executable:

```powershell
$artifacts = Join-Path $env:TEMP ("photoviewer-wpf-agent-build-" + [guid]::NewGuid().ToString("N"))
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj `
  -c Release `
  --artifacts-path $artifacts `
  --nologo
```

Run relevant focused verifiers from `scripts/` when changing shared state,
deletion, external actions, decode/metadata handling, Albums, navigation, or
other covered behavior. Use isolated temporary fixtures and do not overwrite a
running Browser `.next` directory or WPF executable.

On Windows, run the executable cross-runtime vectors when a registered shared
contract or either of its consumers changes:

```powershell
corepack pnpm verify:parity
```

Report the exact commands run, their results, and anything not verified. Do not
call work complete based only on documentation or one renderer's behavior.

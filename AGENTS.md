# Aibos Image agent guide

These are public repository instructions. Do not depend on a private workspace,
tracker, machine state, or a particular AI product.

## Scope

- This repository owns the native WPF application.
- The historical Browser/Next.js implementation is archived. It is not an
  active product, authority, dependency, or completion gate.
- Keep private H25 source and history, user data, caches, queue state,
  screenshots, and runtime evidence out of this public repository and review
  material.
- The public product name is `Aibos Image`; `Aibos` is the compact UI label.
  `PhotoViewer` names are compatibility identifiers and require a tested
  migration before renaming.
- Do not introduce or extend the retired WinForms renderer.

## Read only what applies

Always read the nearest `AGENTS.md`, inspect the current diff, and read the
files and focused tests you will change. Then use this routing table:

| Change | Additional authority to read |
|---|---|
| Public setup or entry points | `README.md` |
| Security, privacy, publication, or trust boundary | `SECURITY.md` and the relevant security contract |
| Product behavior or durable-state meaning | The relevant section of `docs/product-contract.md` |
| Durable-state or Enhancement protocol | The matching entry in `contracts/index.json`, then only its listed contract or fixture |
| Shared-root discovery | `contracts/shared-root-locator-v1.json` |

Do not read the whole product contract or contract directory by default.
Historical packets, screenshots, benchmarks, and live runtime observations are
evidence, not product authority.

## Hard boundaries

- Viewing and ordinary state changes must not rewrite source images.
- Source deletion is explicit and uses the operating-system Recycle Bin. There
  is no permanent-delete fallback.
- Enhancement starts only from an explicit user action. Passive viewing,
  search, navigation, hydration, and health or Jobs reads do not enqueue, wake,
  claim, retry, or start workers.
- Ordinary WPF viewing does not require Node.js or a web UI. The optional
  Enhancement companion is authenticated, API-only, loopback-only on
  `127.0.0.1`, and does not load or open a UI.
- Treat paths, arguments, metadata, loopback data, and durable files as
  untrusted. Preserve validation and resource bounds.
- Preserve compatible unknown fields. Mutate the latest on-disk state and fail
  without writing on malformed or unsupported future state.
- Never delete, reset, or replace user media or persistence as a repair method.

## Working rules

- Check `git status` before editing and preserve unrelated changes.
- Keep repository-boundary, protocol, framework, structural, and visual changes
  in separate patches.
- Use isolated synthetic TEMP fixtures. Do not touch real user state for tests.
- Do not commit `bin`, `obj`, caches, logs, generated output, unsanitized
  reports, private paths, credentials, or personal media.
- Do not add or change a repository license without an owner decision. Until a
  `LICENSE` exists, do not call the repository open source.

## Verification

Run the smallest relevant verifier from `scripts/`. Build from source when WPF
code, project files, or build inputs change:

```powershell
$artifacts = Join-Path $env:TEMP ("aibos-wpf-agent-build-" + [guid]::NewGuid().ToString("N"))
dotnet build .\local-native\PhotoViewer.Wpf\PhotoViewer.Wpf.csproj -c Release --artifacts-path $artifacts --nologo
```

When more than one active reader consumes a protocol, verify the same synthetic
fixture against exact revisions. Run GitHub Actions only for a pushed candidate
or an explicitly requested remote handoff.

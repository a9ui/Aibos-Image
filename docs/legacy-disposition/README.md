# Legacy Disposition Overlay

This directory contains the public-safe M2 disposition overlay for the
immutable M1 legacy asset ledger.

M2 never edits `docs/legacy-ledger/manifest-v3.json` or
`docs/legacy-ledger/summary-v3.json`. Every overlay row binds back to the exact
M1 cutoff and manifest SHA-256, names exactly one M1 asset, and records one
terminal disposition with bounded public evidence.

The terminal vocabulary is:

- `ADOPT_BASELINE`;
- `ADOPT_FOUNDATION`;
- `KEEP_H25`;
- `MERGED_H25`;
- `DEFER_M6`;
- `CLOSE_SUPERSEDED`;
- `HISTORICAL_PROVENANCE`;
- `REJECT`.

`PENDING_M2`, `UNKNOWN`, `NOT_VERIFIED`, and `NEEDS_OWNER` are transient and
are forbidden in the completed overlay.

`target_semantic_unit` keeps the immutable `M1-SU-*` responsibility when the
exact Aibos or H25 baseline already owns the semantic result. A future row may
target `M3-WPF-PARITY` only when bounded WPF recovery is still required; the
checked-in overlay has no such outstanding row.

The overlay is classification evidence only. It does not apply a stash, move
or delete a ref, clean a worktree, copy an untracked file, rewrite history,
change a source image, or mutate user cache or durable state. Private local
names, paths, messages, file contents, and Issue or pull request titles remain
represented only by the opaque M1 asset ID and domain-separated fingerprints.
When a private legacy asset is semantically superseded by the public Aibos
baseline, its row also pins a public repository path and Git blob SHA through
an `AIBOS-BLOB:<sha>@<path>` token. The builder resolves those four bindings
from the exact accepted Aibos tree, and both independent verifiers require the
same per-asset mapping without exposing the legacy path or bytes.

The builder is evidence-bound and fails if either public evidence `main` has
moved or if the expected H25 Issue/pull-request corpus no longer matches:

```powershell
python .\scripts\build_legacy_disposition_overlay.py `
  --gh-path <authenticated-gh>
```

Validate the checked-in overlay independently with both implementations:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-legacy-disposition-overlay.ps1

python .\scripts\verify_legacy_disposition_overlay.py
```

Both implementations must report 507 terminal rows, zero duplicates, zero
orphans, zero missing assets, zero transient rows, and zero private-surface
findings before M2 can close.

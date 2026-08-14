# Legacy Asset Recovery Ledger

Start with `summary-v3.json`. Read `manifest-v3.json` only when investigating a
specific legacy asset; it is never required for ordinary product work.

This directory contains the privacy-safe, item-level M1 inventory for the
legacy H25 repository and its preserved local Git surfaces.

The ledger is evidence only. Every item remains `PENDING_M2`; this directory
does not apply a stash, clean a worktree, delete a ref, change history, recover
code, migrate state, or decide a semantic disposition.

## Public artifact

`manifest-v3.json` contains one row for each observed asset in these categories:

- GitHub branches, Issues, pull requests, and tags;
- local refs and stash entries;
- registered worktrees;
- staged, unstaged, and untracked path layers.

Local paths, ref names, branch names, stash messages, dirty contents, Issue/PR
titles and bodies, user data, and credentials are never serialized. Their exact
capture inputs contribute only domain-separated SHA-256 fingerprints. Public
GitHub numeric identifiers and public commit SHAs may appear directly.

Each row has exactly one `M1-SU-*` owner and one corresponding Aibos Issue
reference. `M1-SU-001` through `M1-SU-007` own their fixed focal Issue/PR
predicates, `M1-SU-008` owns the remaining GitHub Issue/PR set,
`M1-SU-009` owns branches/tags/refs, and `M1-SU-010` owns
stash/worktree/dirty-layer records.

`summary-v3.json` records category and ownership counts and hashes, the exact
accepted Aibos base, the H25 default SHA, before/after source-state hashes, and
the independent recomputation result.

## Capture and verification

The capture is deliberately implemented twice:

1. `scripts/capture-legacy-asset-ledger.ps1`;
2. `scripts/capture_legacy_asset_ledger.py`.

Both implementations independently query GitHub and read local Git with
`GIT_OPTIONAL_LOCKS=0`. Each takes a before and after snapshot. The coordinator
writes public artifacts only when both sources remained stable and the two
implementations produced byte-identical manifests, identity sets, ownership
maps, category maps, and SHA-256 values.

Run a new cutoff only against an explicitly selected, preserved H25 worktree:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-legacy-asset-ledger.ps1 `
  -LegacyRepo <preserved-h25-worktree> `
  -GhPath <authenticated-gh>
```

The checked-in artifact is reproducibly validated without access to private
paths or local worktrees:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-legacy-asset-ledger.ps1
```

Any H25 GitHub, ref, stash, worktree, index, worktree-content, or untracked-file
change invalidates the observation and requires a new cutoff. An unchanged
source must not be recaptured merely to produce another timestamp.

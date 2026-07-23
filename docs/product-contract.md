# Aibos Image Product Contract

This document is the minimum normative contract for the native WPF product and
for durable-state compatibility with the separately maintained H25 Browser
application. WPF implementation details are not product truth unless this
document adopts them.

Stable `PV-*` identifiers name shared-state behavior without moving its meaning
out of this document. `contracts/parity-v1.json` contains the initial executable
vectors. It is test input and evidence mapping, not a second specification.

## Product and repository boundary

- `a9ui/Aibos-Image` is the development authority for the native WPF product.
- `a9ui/tools-h000025-photoviewer` is the independent development authority for
  the Browser product.
- The applications have independent UI, runtime, release, issue, and pull
  request lifecycles. Browser/WPF UI parity is not a completion gate.
- Only versioned durable-state meanings and the optional loopback Enhancement
  protocol cross the repository boundary.
- `Aibos Image` is the public product name and `Aibos` may be used as a compact
  UI label. Legacy `PhotoViewer` names are compatibility identifiers and must
  not be renamed without a non-destructive migration.

## Runtime boundary

- Ordinary WPF viewing is local and remains usable without a Browser or Node.js
  runtime.
- Enhancement may call the separately installed H25 Browser application as an
  optional local companion, only after an explicit user action.
- The companion endpoint is loopback-only and must bind to `127.0.0.1`. LAN,
  tunnel, reverse-proxy, hosted, and Internet exposure are unsupported.
- An unavailable companion must produce a bounded, actionable error and must
  not affect ordinary viewing or mutate source/shared state.

## Source images and destructive actions

- Normal viewing, search, metadata inspection, Favorite, Seen, Album, Search
  History, and Enhancement state do not rewrite source images.
- Removing an image from an Album changes membership only.
- Recycling a source image is a distinct explicit operation.
- Source deletion uses the operating system Recycle Bin. There is no silent
  hard-delete fallback.
- UI and durable state reconcile only after the source operation succeeds.

## Cross-repository durable state

The WPF and Browser applications will resolve one repository-independent shared
data root. Selection of that root must be explicit, versioned, and reversible;
an ambiguous or unsupported root fails closed. A change to repository layout
must not silently create a second state authority.

The shared durable set is:

- `favorites.json`;
- `seen.json`;
- `settings.json`, containing only allowlisted settings with the same product
  meaning in both applications;
- `albums.json`;
- `search-history.json`;
- `recent-folders.json`;
- `enhance/jobs.json` and managed outputs under `enhance/outputs/**`.

Derived thumbnails, indexes, and metadata caches are rebuildable data and do
not receive the same retention semantics as the durable set. Renderer-local
presentation state remains local. WPF window geometry, panels, card width,
keybindings, selection, modal chrome, preview layout, and similar fields in the
existing WPF `state.json` must not be shared wholesale.

Shared writers preserve unrelated and unknown fields when the format permits
it, acquire the contract lock, reread the latest on-disk document, and merge at
the smallest defined semantic unit. Malformed and unsupported future state is
rejected without changing its bytes. A reader must not rewrite state merely by
opening it.

The current public-foundation change does not select or activate a shared root
and does not move, merge, initialize, rewrite, or delete existing user state.
Root discovery and schema rollout require a read-only ledger and separate
reviewed changes.

### `PV-SH-001` — Search History identity

- Browser and WPF normalize comma-separated query tokens with the same explicit
  trim character set: U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680,
  U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, and U+FEFF.
  Empty tokens are removed and remaining tokens are joined with `", "`.
- Search History identity applies NFKC, then invariant lowercase independently
  to each Unicode code point so contextual final-sigma rules do not apply.
  U+0130 is explicitly folded to `i` plus U+0307. Both applications must
  produce the same resulting string; whole-string runtime lowercase behavior
  does not define product identity.

### `PV-SH-002` — Search History document protection

- Search History keeps at most the newest 50 distinct normalized entries.
- Commit, delete, and clear operate on the latest on-disk document under the
  shared lock and preserve unknown root fields.
- Malformed and unsupported future Search History documents are rejected
  without overwriting their bytes.

### `PV-ALB-001` — Album document compatibility

- A missing Album store reads as an empty version 1 document without creating
  a file.
- Only an unambiguous versionless empty Album store may migrate to version 1;
  reading it does not rewrite it.
- Malformed and unsupported future Album documents are rejected
  non-destructively, and compatible unknown fields survive later mutations.

### `PV-ALB-002` — Album operations and revision

- Album mutations read the latest on-disk document under the shared lock and
  increment the document revision only when state changes.
- An Album record's revision increments exactly once when that Album changes.
  A no-op or conflict increments neither document nor Album revision.
- Repeated member addition is idempotent, an optional stale
  `expectedRevision` conflicts without mutation, and path cleanup removes only
  the named memberships.
- Existing surviving member order is preserved, newly added members append in
  request order, and removing the member used as the cover clears
  `coverMemberId`.
- Compatible unknown root, Album, and member fields survive unrelated
  operations in both applications.

## WPF navigation

- The active source owns image order for selection, modal navigation, and the
  Filmstrip.
- Album order is preserved when an Album is the active source.
- Search and Album sources do not overwrite each other's owned collections.
- Presentation geometry and gestures not stated here remain WPF implementation
  details.

## Enhancement

- Enhancement begins only from an explicit user action.
- Ordinary browsing, preview, search, modal navigation, and state hydration do
  not enqueue jobs or start workers.
- Original and managed Enhanced outputs remain distinct; source images are not
  overwritten.
- The H25 Browser companion owns the current local Enhancement API and worker.
  WPF owns its loopback client and must keep the API optional.
- Removing the in-repository Browser backend is not merge-ready until a named
  H25 commit passes an isolated TEMP compatibility test against the exact WPF
  candidate. That test must prove request and response compatibility, one
  absolute Enhancement root for `jobs.json` and `outputs/**`, WPF output
  ownership checks, restart recovery, unchanged source bytes, and zero writes
  to user-owned state or caches.

## Change rule

A WPF-only behavior change requires WPF implementation evidence only. A durable
state or Enhancement protocol change additionally requires:

1. a versioned contract decision in this repository;
2. synthetic fixtures tied to the exact Aibos source commit;
3. WPF evidence at an exact candidate SHA;
4. H25 Browser reader/compatibility evidence at an exact candidate SHA;
5. reader-first rollout before either new writer is enabled.

The repositories may use separate pull requests and merge schedules. A vendored
fixture in H25 must identify this canonical repository, path, contract version,
and source commit SHA; it is never a second source of truth.

## Legacy WinForms

The legacy WinForms renderer is frozen, is not included in this repository, and
must not be restored or extended.

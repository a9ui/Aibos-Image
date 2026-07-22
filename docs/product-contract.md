# Aibos Image Product Contract

This document is the minimum normative product contract shared by the Browser
and WPF renderers. Renderer-specific implementation details are not product
truth unless this document explicitly adopts them.

Stable `PV-*` identifiers name shared behavior without moving its meaning out
of this document. `contracts/parity-v1.json` contains executable vectors for
the initial covered identifiers; it is test input and evidence mapping, not a
second normative specification.

## One product, two renderers

- The public product name is `Aibos Image`; compact UI branding may use
  `Aibos`.
- Legacy `PhotoViewer` assembly, namespace, cache, and persisted-state names
  are compatibility identifiers. They do not define a second product and must
  not be renamed without a non-destructive migration.
- Browser and WPF expose the same product meanings through independent UI and
  runtime implementations.
- WPF is the intended long-term primary renderer. Browser remains supported
  until an explicit retirement milestone proves complete workflow and user-data
  replacement.
- Shared work may use a Browser-first implementation sequence, but the product
  contract is decided first and the change is not complete until both
  applicable renderer gates are green.
- A shared product behavior change must be evaluated for both renderers.
- A renderer-specific exception must identify the affected surface and explain
  why the other renderer is not applicable.
- Shared meaning, state ownership, and safety boundaries take precedence over
  historical renderer documents or screenshots.

## Runtime boundary

- Browser HTTP endpoints are local-only and bind to `127.0.0.1`.
- Public source visibility does not authorize LAN, tunnel, reverse-proxy, or
  Internet exposure.
- WPF ordinary viewing remains usable without a Browser server.

## Source images and destructive actions

- Normal viewing, search, metadata inspection, Favorite, Seen, Album, and
  Enhancement state do not rewrite source images.
- Removing an image from an Album changes membership only.
- Recycling a source image is a distinct explicit operation.
- Source deletion uses the operating system Recycle Bin. There is no silent
  hard-delete fallback.
- UI and shared state reconcile only after the source operation succeeds.

## Shared state

- Favorite, Seen, Search History, Settings, and Albums have shared product
  meanings across Browser and WPF.
- Shared writers preserve unrelated and unknown fields when the format permits
  it and mutate the latest on-disk state rather than replacing a stale full
  document.
- Malformed or unsupported future state is rejected non-destructively.
- Renderer-local presentation preferences remain local unless this contract
  explicitly promotes them to shared product settings.

### `PV-SH-001` — Search History identity

- Browser and WPF normalize comma-separated query tokens with the same explicit
  trim character set: U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680,
  U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, and U+FEFF.
  Empty tokens are removed and remaining tokens are joined with `", "`.
- Search History identity applies NFKC, then invariant lowercase independently
  to each Unicode code point so contextual final-sigma rules do not apply.
  U+0130 is explicitly folded to `i` plus U+0307. Browser and WPF must produce
  the same resulting string; whole-string runtime lowercase behavior does not
  define product identity.

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
- An Album record's own revision increments exactly once when that Album
  changes. A no-op or conflict increments neither the document revision nor
  the Album revision.
- Repeated member addition is idempotent, an optional stale
  `expectedRevision` conflicts without mutation, and path cleanup removes only
  the named memberships.
- Existing surviving member order is preserved, newly added members append in
  request order, and removing the member used as the cover clears
  `coverMemberId`.
- Compatible unknown root, Album, and member fields survive unrelated
  operations in both runtimes.

## Navigation

- The active source owns image order for selection, modal navigation, and its
  Filmstrip.
- Album order is preserved when an Album is the active source.
- Search and Album sources do not overwrite each other's owned collections.
- Presentation geometry and gesture details that are not stated here remain
  implementation details until a shared contract and regression vector adopt
  them.

## Enhancement

- Enhancement begins only from an explicit user action.
- Ordinary browsing, preview, search, modal navigation, and state hydration do
  not enqueue Enhancement jobs or start workers.
- Original and managed Enhanced outputs remain distinct; source images are not
  overwritten.

## Change rule

For a behavior owned by both renderers, a change is complete only when it has:

1. a product-contract decision or an explicit no-contract-change statement;
2. Browser implementation evidence;
3. WPF implementation evidence;
4. shared or equivalent regression coverage;
5. any renderer-specific exception recorded in the change.

## Legacy WinForms

The legacy WinForms renderer is excluded from this repository and is not
supported. It is not part of the Browser/WPF parity contract.

# Aibos Image product contract

This document defines stable, cross-cutting product semantics. Exact wire
fields, file shapes, bounds, and synthetic vectors live in the versioned JSON
files under `contracts/`.

This document does not record rollout status, running processes, local
benchmarks, candidate commits, hardware observations, or temporary activation
flags. Those are revision-bound evidence, not product authority.

## Authority and scope

- This repository owns the native WPF application.
- The former Browser implementation is archived. It is not an active product,
  reader, writer, authority, dependency, or completion gate.
- Private H25 implementation, lineage, user data, and runtime evidence are not
  part of this public contract.
- `Aibos Image` is the public product name and `Aibos` is the compact UI
  label. Existing `PhotoViewer` identifiers remain compatibility names.
- A conflict between this document, an affected JSON contract, and its
  executable fixtures means the change is incomplete. Do not guess or write
  user state until they agree.

Historical documents, screenshots, benchmarks, issue text, and the behavior of
only one reader do not override this contract.

## Safety invariants

- Viewing, metadata inspection, Favorite, Seen, Album, Search History, and
  Enhancement state changes do not rewrite source images.
- Source deletion is a separate explicit action through the operating-system
  Recycle Bin. There is no permanent-delete fallback.
- Album remove and delete actions change Album membership only; they never
  recycle a source file.
- If a source Recycle operation fails, UI and durable state do not reconcile it
  as a successful deletion.
- Enhancement begins only after an explicit user action. Browsing, preview,
  search, navigation, hydration, and passive health or Jobs reads do not
  enqueue, recover, wake, claim, retry, reorder, or start workers.
- Ordinary WPF viewing is local and does not require Node.js or a web UI.
- The optional Enhancement companion is authenticated, bound to
  `127.0.0.1`, and not exposed to a LAN, proxy, tunnel, hosted service, or the
  Internet. The default companion is API-only and does not load or open a UI.
- Paths, metadata, process arguments, loopback messages, and durable files are
  untrusted input. Readers and writers enforce bounds and ownership checks.
- Malformed, unreadable, ambiguous, or unsupported future state fails without a
  destructive write.
- User media, settings, caches, Albums, Search History, Favorites, Seen state,
  Jobs, and outputs are never deleted or reset as a repair strategy.

## Durable-state boundary

The versioned durable set consists of:

- `favorites.json`
- `seen.json`
- `settings.json`
- `albums.json`
- `search-history.json`
- `recent-folders.json`
- `enhance/jobs.sqlite3`, with `enhance/jobs.json` retained for legacy
  compatibility
- `enhance/enqueue-inbox/**`
- the managed output root selected by `enhance/output-root.txt`, with the
  contract fallback under `enhance/outputs/**`

Window geometry, panel layout, current selection, keyboard bindings, local
prompt defaults, Styles, and other renderer presentation state remain local.
WPF `state.json` is not part of the public durable protocol wholesale.

### Discovery and lifetime

- A process resolves one validated shared root and pins all derived store paths
  for its lifetime.
- Normal application startup is reader-only. It does not create a locator,
  shared root, store, or migration.
- `Aibos.SharedRootSetup` is the explicit create-only locator tool. It does
  not copy, merge, migrate, replace, or delete durable data.
- Environment and per-store test overrides remain test and deployment inputs;
  they do not create a second product authority.

The exact locator document, validation, leases, and cases are defined by
`PV-ROOT-001`.

### Safe reads and writes

- Writers read the latest on-disk state while holding the required lease.
- Compatible unknown fields survive unrelated mutations.
- Writes use bounded parsing and an atomic replace strategy appropriate to the
  store.
- Unsupported future versions and malformed documents remain byte-preserved
  unless a separately reviewed migration applies.
- A failed read never becomes permission to publish an empty replacement.
- Renderer-local state never leaks into a shared document.

### Registered shared meanings

- `PV-SET-001`: shared settings preserve unknown fields, protect unsupported
  documents, and default deletion confirmation to enabled when no valid shared
  value is available.
- `PV-REC-001`: a supported recent-folder document is authoritative,
  including an explicitly empty set. Malformed or future state is protected.
- `PV-SH-001`: Search History identity uses the registered normalization and
  comparison vectors rather than platform-default string comparison.
- `PV-SH-002`: Search History mutations are bounded, preserve compatible
  unknown fields, and do not replace protected documents.
- `PV-ALB-001`: Album readers preserve compatible unknown root, Album, and
  member fields.
- `PV-ALB-002`: Album mutations operate on the latest revision, keep stable
  identities and order, and reject conflicting or protected state.

The executable cases for these meanings are routed by
`contracts/index.json`. Read only the matching member.

### Protocol evolution

- Define a versioned public contract and synthetic fixtures before enabling a
  writer for a new meaning.
- When multiple active readers or writers consume a document, prove each reader
  first and test simultaneous writers before enabling them.
- A copied fixture identifies its canonical repository path, contract version,
  and source revision. It is evidence, not a second authority.

## WPF navigation and Favorites

- The active source owns gallery order, modal navigation, and Filmstrip order.
- An open modal pins the displayed source identity until the user navigates or
  closes it. Background selection, filtering, catalog refresh, or Enhancement
  refresh cannot retarget modal actions. If the pinned source disappears, the
  modal closes.
- Album order is preserved while an Album is active. Search and Album sources
  do not overwrite each other's owned collections.
- A modal Favorite targets the version actually displayed. Original uses the
  canonical source path. A validated managed Photoreal or Video output uses its
  exact managed output path. Upscale and I2I retain Original Favorite meaning
  in the current shared schema.
- Favorite writers merge only changed path keys into the latest shared map.
  Temporarily missing media does not erase its Favorite entry.
- Favorite filtering, colors, layout, undo history, and interaction geometry
  are WPF presentation behavior unless they change the path-keyed shared
  meaning above.

## Enhancement

### Operation and source rules

- The version 1 operation envelope accepts `upscale`, `photoreal`, `i2i`,
  and `video`. Only a genuinely absent operation on a legacy row means
  `upscale`.
- A present null, malformed, or unknown operation is reader-only protected. It
  is not coerced, executed, retried, reordered, opened, or deleted through a
  managed-operation path.
- Every job snapshots the effective request needed for deterministic retry.
  Later settings changes do not silently rewrite queued or running jobs.
- A request that uses a managed producer refers to its durable job identity.
  A video request may instead name the exact managed still currently displayed,
  but only through the advertised displayed-source capability and after both
  WPF and companion validate its canonical managed-root ownership, supported
  operation folder, current file identity, decoded bounds, and content hash.
  No other client-supplied managed output path is accepted.
- Original and managed output identities remain distinct. Enhancement never
  overwrites the source.

### Companion trust boundary

- Loopback location is not identity. WPF proves companion ownership before
  sending source identity, prompts, settings, credentials, or job bodies.
- Protected requests and responses are authenticated, encrypted where the
  contract requires it, replay-resistant, and bound to one companion epoch.
- Durable enqueue publishes the bounded inbox item before sending a bodyless
  wake. A post-publication transport failure does not discard the durable item.
- An authenticated queue resume first restores the configured read-only
  MiniMax H3 runtime mounts when that seal is unavailable, then completes the
  companion's one-time recovery before it starts a worker. Queue pause remains
  available during deferred recovery and lets the current job stop at its
  normal boundary.
- Passive reads and startup history access do not recover the queue or start GPU
  work, and do not mount the optional Enhancement runtime.

The exact capability storage, identity proof, tunnel, request, response, and
startup rules are in `contracts/enhancement-companion-auth-v2.json`.

### Jobs, queue, and output

- `enhance/jobs.sqlite3` is the current Jobs authority. WPF uses the registered
  reader surface and does not invent a second writer.
- Jobs loads every running and queued row, but only the most recently updated
  terminal history selected by the WPF-local 100, 500, or 1000 row limit. The
  default is 500. Database totals and operations labelled all are not narrowed
  by this presentation window.
- Queue ordering, pause, claim, retry, cancellation, and queued-setting updates
  share the locks and idempotency rules in `PV-ENHANCE-QUEUE-001`.
- Health is a bounded passive snapshot. Reading it has no queue, worker,
  ComfyUI, or GPU side effect.
- Managed outputs stay below the selected output root and operation folder.
  Lexical and canonical ownership checks apply before open, Favorite, retry,
  deletion, or producer reuse.
- A completed output is finalized below its operation's `YYYY-MM-DD` folder.
  The date comes only from that output file's Windows CreationTime in the
  companion's local timezone. Job, source, and EXIF dates do not substitute.
- Output-root changes do not move existing files. Migration is a separate
  paused-and-drained operation defined by `PV-ENHANCE-OUTPUT-001`.

### Image and video protocols

- I2I is a distinct managed image operation. Its accepted targets, source
  provenance, immutable snapshot, capability gate, and fail-closed vectors are
  defined by `PV-ENHANCE-I2I-001`, `PV-ENHANCE-I2I-002`, and
  `PV-ENHANCE-I2I-003`.
- Unified I2I version 3 snapshots separate overall, expression, outfit,
  background, and pose directives in one durable job. A blank scoped directive
  does not unlock that region. STEP, CFG, resolved outfit-mask expansion, and
  Seed are part of the immutable retry snapshot; opening or applying a named
  native style remains passive until the user explicitly starts the edit.
- Video rows are typed media and are never decoded or mutated as still-image
  versions. Wan-compatible version 1 rows remain readable under
  `PV-ENHANCE-VIDEO-001`.
- MiniMax H3 requests use the additive version 2 contract. Profile, step, and
  canvas selections are separate versioned capabilities; clients require exact
  readiness before durable publication.
- Readiness is obtained from the current authenticated health response. It is
  not inferred from a committed activation record, benchmark, previous
  process, or candidate revision.

## Contract index

Use `contracts/index.json` to select one contract and, only when testing its
vectors, its listed fixture. Verification-only bundles are materialized under
TEMP and are not duplicate checked-in authorities.

## Change rule

- Change stable semantics here and exact protocol details in the affected JSON
  contract and synthetic fixtures in the same reviewable patch.
- Use a new contract version for a breaking meaning or shape. Do not silently
  reinterpret persisted values.
- Keep runtime observations, benchmark results, rollout receipts, and candidate
  SHAs in dated review evidence when they must be retained; never make them
  normative product state.
- Keep private values and private implementation out of public contracts.
- The legacy WinForms renderer remains retired and is not a compatibility
  target.

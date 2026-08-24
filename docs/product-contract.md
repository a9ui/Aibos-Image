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
- A successful retry first commits the replacement child job and its durable
  idempotency receipt. Only then is the failed or canceled source row removed
  from terminal history. Rejected, pending-delivery, ambiguous, or malformed
  retry results retain the source row, and retry never removes source or output
  media.
- A request that uses a managed producer refers to its durable job identity.
  A video request may instead name the exact managed still currently displayed,
  but only through the advertised displayed-source capability and after both
  WPF and companion validate its canonical managed-root ownership, supported
  operation folder, current file identity, decoded bounds, and content hash.
  No other client-supplied managed output path is accepted.
- Video Tools version 2 may instead select the exact current single dropped or
  displayed regular video. During the explicit Start action, WPF captures its
  canonical request path, size, last-write time, and SHA-256 through a
  no-delete/read lease. The companion independently canonicalizes and measures
  the same opened file after the committed inbox item is claimed, requires
  every captured value to match, applies the bounded media probe, and creates a
  separately verified job-owned staging copy before committing a Job. The
  request path is never source authority, and passive view, health, and Jobs
  reads do not open, hash, probe, or stage it.
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
  default is 500. Database totals and terminal-history dismissal labelled all
  are not narrowed by this presentation window. WPF freezes queued ids from its
  complete validated active snapshot; the companion freezes exact ids for
  presentation-bounded terminal history, so rows created after that plan are
  not included. Exact queued cancellation uses a dedicated batch route; it
  never overloads the legacy bodyless cancel-all route. With authoritative
  terminal targets and batch retry advertised, WPF retries the exact filtered
  history in idempotent chunks of at most 1000 ids; without both capabilities,
  retry-all stays disabled whenever history extends beyond the loaded window.
- Queue ordering, pause, claim, retry, cancellation, and queued-setting updates
  share the locks and idempotency rules in `PV-ENHANCE-QUEUE-001`.
  When exact batch reorder is advertised, rapid WPF moves update the complete
  queued presentation immediately and coalesce to the latest full order. The
  companion applies that order in one write only if the queued-id snapshot is
  still exact; a concurrent claim, enqueue, or cancel returns conflict without
  a partial reorder or worker wake.
- Health is a bounded passive snapshot. Reading it has no queue, worker,
  ComfyUI, or GPU side effect.
- Managed outputs stay below the selected output root and operation folder.
  Lexical and canonical ownership checks apply before open, Favorite, retry,
  deletion, or producer reuse.
- A managed image output cannot be deleted while any queued or running video
  row depends on its producer id or names that exact canonical output as its
  persisted source path. This protection remains fail-closed for an active
  video row whose other mutation fields are malformed or reader-only.
- A managed video output cannot be deleted while a queued or running Video
  Tools child, or an exact committed pending durable item, depends on its
  producer id. An imported external-video staging copy is owned only by its
  Video Tools Job and remains pinned through retry and recovery. Aibos never
  deletes, moves, overwrites, repairs, or replaces the external original.
- Durable video create and retry publication reserves the same managed source
  before a Jobs row is visible. Publication and managed-output deletion share
  the Jobs writer lock; DELETE passively scans bounded committed pending,
  processing, and needs-action inbox state while holding that lock. Malformed,
  unsupported-future, over-bound, ambiguous, or unresolved reservation state
  fails closed without deleting output or rewriting Jobs state.
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
- Motion Director is a deterministic, WPF-local planning surface for MiniMax
  H3. It compiles bounded action, camera, and frame-timeline choices into a
  transient prompt candidate. Opening it, changing its controls, or building a
  candidate creates no Job and publishes no durable reservation. Only the
  existing explicit Apply action changes the video prompt; video generation
  still requires its separate explicit enqueue action.
- Video Tools version 1 keeps `operation=video` and selects one succeeded,
  exact managed video by producer Job id. A client path is never source
  authority. Retake accepts a half-open frame selection on an exact 24 fps H3
  v2 source, derives and persists the smallest legal centered H3 window, and
  shows the selected and actual windows separately. The completed Retake is
  the full source clip: only the actual video window is replaced, unchanged
  prefix and suffix frames remain, source frame count, duration, and fps remain
  exact, original source audio is preserved, and generated-window audio is
  discarded.
- Video Tools version 1 is frozen legacy reader evidence. Its production
  writer remains disabled, and a version 1 `retake` request or snapshot is
  never rewritten, upgraded, or executed as a version 2 `edit`.
- Video Finish is a separate spatial 2x super-resolution intent. Faithful and
  detail modes preserve source fps, duration, frame count, and audio, reset
  temporal state at scene cuts, and do not perform frame interpolation. RIFE
  interpolation is not Video Finish.
- Video Tools health is passive and exact. Reader readiness never authorizes a
  writer. Retake and Finish remain disabled until their own pinned runtime,
  workflow input, model/license, bounded GPU, and output media canaries all
  verify. Unknown, malformed, or future Video Tools snapshots remain
  reader-only protected. A queued or running `sourceVideoJobId`, and an exact
  committed pending durable inbox reservation for it, blocks deletion of that
  producer output. The exact wire, bounds, persisted snapshot, delivery, and
  production gate are defined by `PV-ENHANCE-VIDEO-TOOLS-001`.
- Video Tools version 2 keeps `operation=video` but exposes only the semantic
  `edit` and `finish` kinds. Edit is direct source-video-conditioned work, not
  a keyframe-completion contract. Its request contains one Japanese
  instruction, one validated compiled backend prompt and Japanese summary,
  compiler revision and context digest, an exact half-open frame selection,
  STEP, integer strength, and a maximum pixel tier. Seed, backend, context,
  actual mask, affected range, denoise, source snapshot, and delivery are
  server-owned. The UI's skip-review checkbox is transient one-shot policy and
  is not durable generation semantics.
- Opening or hydrating the Edit board, changing the instruction or selection,
  ordinary seeking, toggling skip-review, and reading health or Jobs are
  passive: they do not resolve, open, hash, or probe a source, extract compiler
  previews, invoke the compiler, publish an inbox item, create a Job, or wake
  work. A displayed file first requires the explicit authenticated
  `フレームを読み込む` action. It may bind, canonicalize, hash, and probe the
  source under a no-delete/read lease and decode bounded requested thumbnails.
  Its transient result contains exact frame count, rational fps, duration,
  dimensions, and requested preview identities. Exact frame controls,
  compilation, and Start stay disabled until that result is current. A managed
  source may use its exact persisted producer probe and skip the media re-probe.
  Neither path infers 24 fps or frame count from MediaElement duration or
  playback state.
- `指示を整える` is another explicit authenticated action. It may recapture and
  revalidate the selected source, extract the exact start/middle/end
  preview-frame identities, and invoke the bounded local compiler, but it
  creates no Job, inbox item, wake, staging copy, or output.
- The compiled candidate is transient and its context digest binds the exact
  source identity, half-open selection, ordered preview identities, Japanese
  instruction, compiled prompt and summary, and compiler revision. It becomes
  unusable when any of those inputs changes. With review enabled, compilation
  only displays and announces the result; the user must perform a later
  explicit Start. With skip-review checked, the compiler click supplies one
  transient single-use authorization to compile, perform final validation, and
  then publish. A successful compiler response alone never starts work. Before
  either path publishes, Start recaptures the same source and preview identities
  and requires the same current context digest; drift or mismatch fails without
  durable publication, Job creation, wake, staging, or output.
- A version 2 source is either one succeeded exact managed Videos producer id,
  or the explicitly captured current `displayed-file` selector described
  above. Both resolve to an immutable canonical source signature, SHA-256, and
  bounded probe; imported files additionally resolve to a job-owned staging
  identity. Common input is one regular MP4 with exactly one video stream, at
  most one audio stream, no more than 512 MiB, 300 seconds, 1920 x 1080, and
  18,000 frames. Edit and Finish initially accept exact 24/1, 30/1, or 60/1
  fps. Initial Edit accepts an exact selected interval no longer than 5,000
  milliseconds and 300 source frames. Unsupported input fails capability or
  request validation closed instead of being coerced.
- Explicit Start first atomically publishes the exact captured request through
  `PV-ENHANCE-ENQUEUE-INBOX-001`, then sends the authenticated bodyless wake.
  The Companion performs full source, probe, compiler, staging, and capability
  validation after claiming that committed item and before committing a Job.
  A displayed-file mismatch, missing source, or unsupported probe is an
  authenticated definitive 4xx: no Job, process, retained staging residue, or
  output is created, and the committed envelope moves to `needs-action` after
  later valid items are processed. Delivery may be tried again only against
  the same captured identity. Different bytes require a new explicit Start and
  request id; an old committed item is never rewritten to import them.
- Edit backend selection remains open behind exact receipts. Bernini-R-1.3B is
  the first canary for the current one-Japanese-prompt, mask-free semantic V2V
  request. Wan2.1-VACE-1.3B is the precise-mask candidate but cannot become
  ready until a separate exact spatial-mask or auto-mask plus preview contract
  exists. MiniMax H3 masked Edit remains research-only. H3 FL2VA, Ref2VA, and
  AddGuide retain their distinct generation feature names and are never aliases
  or silent fallbacks for source-video Edit. Qwen-Video-Edit and JoyAI-Video-Edit
  remain future-only, and no standard winner is declared.
- Edit canary zero downloads no model: it checks exact Comfy graph/object input
  schema, the existing artifact inventory without inferring readiness, and a
  synthetic source-frame, PTS, backend-map, pad, crop, and selected-audio
  mapper. Shared UMT5/VAE receipts and an incremental Bernini canary come only
  after that preflight. Every Edit writer remains false until its own model,
  workflow, instruction, timeline, memory, cancellation, and output receipts
  pass.
- Edit persists the exact selected source frames and PTS, then the server-owned
  backend fps, frame map, internal frame count, alignment padding such as
  `4n+1`, delivery crop and source-fps reconstruction, strength mapping, seed,
  canvas, audio packet window, and receipts. Backend 16-fps or alignment needs
  never leak into or rewrite the request selection. Initial output is one new
  non-destructive managed child clip for the selected interval, with no source
  prefix/suffix and no splice into the long source. It reconstructs the exact
  selected source frame count, rational fps, relative PTS, and duration.
  Generated audio is discarded; when source audio exists, only the persisted
  intersecting encoded packet window is remuxed and rebased without re-encoding
  or a sample-exact trim claim. The source is never overwritten.
- Version 2 Finish is a separate AI spatial super-resolution Job. Its public
  modes are `fast`, `standard`, and `quality`, with explicit 2x or 4x scale.
  Each mode has an independent backend, source-bound, scale, delivery, and
  canary capability; no backend or mode silently falls back to another and no
  candidate ID implies a default mode. The faithful candidate is NVIDIA VFX
  VideoSuperRes via `nvidia-vfx 0.1.0.1`, internal VFX SDK `1.2.0.0`, with a
  server-owned `MEDIUM`, `HIGH`, or `ULTRA` setting. It is not claimed
  frame-independent, so scene-cut reset or effect recreation remains a gate.
  SeedVR2 3B is the generative-detail candidate and must explicitly pass source
  fidelity, synthesized-texture, and bounded-VRAM canaries. NanoVSR-1.7M is the
  lightweight native-4x candidate; its reported bidirectional recurrent,
  15-frame disjoint-chunk demonstration requires an exact Aibos overlap/crop
  revision and chunk-seam canary. NanoVSR 2x remains unsupported until a
  separate 4x-to-2x delivery mapping passes.
- Finish output is initially 8-bit SDR, no larger than 3840 x 2160 or
  8,294,400 pixels, with exact source frame count, rational fps, video PTS,
  duration, and encoded audio packets. It performs no interpolation,
  frame-rate conversion, implicit crop, or implicit scale fallback. Every
  candidate's scene-boundary temporal-state behavior remains an exact canary
  requirement rather than an inferred SDK or model property.
- Edit and Finish share only the typed video Job envelope, source pinning,
  idempotency, durable inbox, queue, output-root, and lifecycle infrastructure.
  Their request schemas, capabilities, receipts, backend candidates, planners,
  and output meanings are separate. Readiness or a receipt for either never
  enables, validates, retries, or executes the other.
- `capabilities.videoToolsV2` is an exact passive health shape with reader
  readiness separate from each lane's writer, backend, runtime, ready state,
  and reason code. Finish additionally reports independent fast, standard, and
  quality readiness. Every production writer is currently false. Passive reads
  do not canonicalize, open, hash, probe, or stage source files; mutate Jobs;
  mount models; create processes; enqueue; wake; claim; or retry. Unknown,
  malformed, and future snapshots preserve compatible fields and remain
  reader-only. Retry reuses the exact snapshot, Edit seed, source identities,
  and hashes, then revalidates them. Cancel, delete, cleanup, and publish fail
  closed when ownership is not exact. The version 2 wire, fixture, bounds,
  snapshot, delivery, lifecycle, and production gates are defined by
  `PV-ENHANCE-VIDEO-TOOLS-002`.
- The exact frame-selection UI may later be reused by a separately versioned
  non-AI trim/export operation. Non-AI trim is not an `edit` or `finish` kind in
  Video Tools v2, and its writer, output ownership, frame delivery, and audio
  boundary semantics are separate; Edit readiness authorizes none of them.
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

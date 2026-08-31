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
- Upscale mutation controls require one exact JSON boolean
  `upscaleMutationSafeV1: true` in the compact writer projection. Missing,
  false, null, non-boolean, or duplicate members leave the row visible but
  reader-only across cancel, retry, dismiss, reorder, output deletion, and
  bulk mutation surfaces. The validated gate is part of WPF immutable row
  identity; WPF never passively backfills or rewrites it.
- `upscaleMutationSafeV1` is presentation permission, not execution authority.
  The authenticated Companion still revalidates current durable executability
  for every mutation. Rows written before the gate remain readable without a
  passive migration and can become mutable only through an explicit,
  separately reviewed writer projection update.
- Every job snapshots the effective request needed for deterministic retry.
  Later settings changes do not silently rewrite queued or running jobs.
- The photoreal Engine selector is independent from Style. Missing or unknown
  local selection state uses the established FLUX.2 Klein adapter. An explicit
  selection affects only newly created Jobs, whose `adapterId` remains the
  durable execution snapshot; applying or saving a Style never changes it.
- The exact Krea Anime-to-Real v1 adapter is offered only when the authenticated
  Companion advertises boolean `kreaAnimeToRealV1`. That capability means the
  writer recognizes the durable adapter row; backend and asset readiness stay
  separate. Its engine LoRA is fixed on at strength 1.0, its execution schedule
  is fixed at 8 steps and CFG 1, and generic photoreal Strength is compatibility
  input rather than Krea denoise authority.
- The Krea Anything-to-Real V3 1536 author workflow is an explicit,
  Style-independent option whose missing local state and reset value are OFF.
  It is shown and enabled only after authenticated health contains one exact
  boolean `kreaAnythingToReal1536V1: true` member. Losing that capability does
  not rewrite the saved choice or silently downgrade a requested 1536 Job;
  create and saved retry publication remain blocked until exact support is
  proved again. Passive health discovery never creates, retries, wakes, or
  starts a Job.
- Only exact adapter
  `comfyui-krea2-anything2real-v3-photoreal` may snapshot `maxDimension: 1536`.
  That selection resamples the source, including a smaller source, to a
  16-aligned aspect-preserving canvas whose long edge is exactly 1536 and whose
  pixel area is at most 1536 squared. The existing default and every selection
  through 1280 retain their no-upscale, one-megapixel bound. Anime-to-Real,
  FLUX, unknown adapters, and incomplete durable identities cannot publish or
  retry a 1536 reservation. No 2048 author mode is defined.
- Krea source preparation keeps a minimum 64-pixel work edge without distorting
  the source. A 1280 workflow therefore accepts source aspect ratios through
  20:1, and the 1536 workflow accepts ratios through 24:1. The inclusive aspect
  limit does not override the default workflow's no-upscale rule: when uniform
  no-upscale preparation would leave either edge below 64 pixels, the source
  fails closed rather than being enlarged or squeezed. A source beyond the
  applicable inclusive limit also fails closed before upload or backend start.
- The revision 1 WPF photoreal mutation reader recognizes exactly
  `comfyui-flux2-photoreal`, `comfyui-krea2-anything2real-v3-photoreal`,
  `comfyui-krea2-anime-to-real-edit-v1-photoreal`, and legacy
  `a1111-photoreal`. A missing, duplicate, malformed, unknown, or future
  photoreal `adapterId` remains visible as reader-only state. Cancel, retry,
  dismiss, reorder, rerun, output deletion, current-settings updates, and bulk
  mutations are disabled without sending a mutation request.
- Every durable create, rerun, or retry derives the exact Krea Anime-to-Real v1
  health requirement from its durable adapter identity before inbox
  publication. Unknown, unavailable, timed-out, missing, false, malformed, or
  duplicate capability state fails closed. The matching selector starts
  disabled and becomes available only after the latest authenticated health
  contains one exact boolean `true` member.
- Opening or selecting a native photoreal Style is passive and creates no Job.
  A shipped built-in Style changes only the current positive and blank-positive
  prompt pair; it preserves the current LoRA, strength, CFG, quality,
  resolution, seed, Negative text, and Negative-enabled state. User-saved
  Styles remain separate and may restore the full settings snapshot they own.
- The optional photoreal `Preservation Scan` switch is independent from every
  built-in or user-saved Style. An explicit photoreal Job snapshots the switch.
  When enabled and advertised, the Companion may inspect only source-visible
  eye, gaze, mouth, brow, hand, foot, clothing, pose, crop, camera, and layout
  facts through the bounded `aibos.photoreal-preservation-scan/v1` protocol.
  It does not infer ethnicity, nationality, age, personality, emotion, beauty,
  sexual-content, safety, or censorship labels. Valid observations compile to
  a deterministic positive preservation suffix; they never replace or rewrite
  the source image. An unavailable scanner, timeout, malformed result, or
  unsupported result is discarded and the exact saved prompt plus unchanged
  image reference continues without a generated safety or censorship prompt.
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
- Ordinary WPF launch, passive reads, and startup history access do not start
  Node.js or the Companion, recover the queue, start GPU work, or mount the
  optional Enhancement runtime. A passive reader may use an already-running
  authenticated Companion but does not launch a replacement. Only an explicit
  Enhancement action may start the Companion.
- If WPF owns a Companion but no authenticated non-GET request was constructed,
  closing WPF stops that exact owned process tree. Once an authenticated
  mutation or recovery request can activate durable work, WPF releases its
  process wrapper so accepted queued or running work can continue. WPF never
  signals a listener or process it did not start.

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
- “待機中を現在設定へ” and each queued row's “設定を更新” apply only when
  the exact queued adapter matches the current selected photoreal engine. The
  supported bounded set is FLUX.2 Klein, Krea Anything-to-Real V3, and Krea
  Anime-to-Real V1. An update preserves the job id, adapter identity, source,
  status, and durable `queueOrder`; it neither moves nor wakes the queued row.
  Every engine requires exact `queuedPhotorealSettingsUpdateV1: true`; Krea
  additionally requires exact additive
  `queuedKreaPhotorealSettingsUpdateV1: true`, so an older FLUX-only companion
  keeps the Krea actions disabled without disabling its compatible FLUX action.
  Krea Anything-to-Real may carry 1536 only under its existing exact health
  capability, while the other engines retain their accepted resolution bounds.
  Krea updates keep Engine LoRA fixed on at 1 and `kvCache` on; Anime-to-Real
  additionally fixes denoise 100, 8 steps, and CFG 1, while Anything-to-Real
  derives denoise from bounded Strength and accepts its bounded STEP and CFG.
  A disallowed 1536 update returns conflict and writes nothing.
- The visible global queue is the execution queue. After any running row, the
  companion claims queued rows by durable `queueOrder` using the exact reader
  ordering in `PV-ENHANCE-QUEUE-001`; it does not alternate image and video
  families or maintain a scheduler cursor. The only permitted exception is an
  exact adapter id in the contract's bounded optional non-Krea deferred list;
  that list is empty in schema version 1. The existing physical GPU lease still
  permits only one worker.
- The two recognized Krea photoreal adapters are strict FIFO barriers. If one
  is first among queued rows and its backend is unavailable, that row stays
  byte-equivalent at its durable queue order and no later job, including video,
  starts. The optional bounded passive health field
  `backendAvailability.kreaPhotorealV1.queueHeadBlocked` is true only for that
  exact state. Its absence or malformed value keeps the rest of health and its
  capabilities readable but never infers that the queue head is blocked. This
  intentional idle pump is not `queued-without-pump` and does not expose queue
  recovery. A reader suppresses a conflicting recovery issue only after its
  cached visible queue head also identifies one of the two exact Krea rows;
  an uncorroborated boolean never hides a real pump failure. Readiness is
  reconsidered only at an explicit
  pump, recovery, resume, enqueue, or Companion restart; passive Jobs and
  health reads never monitor assets, start a worker, or wake the queue. The
  explicit gate proves
  the complete sealed Krea inventory, pinned digests, canonical identities, and
  ACL lease before claim. A readiness or security drift detected between that
  gate and owned runtime start restores the exact pre-claim queued row instead
  of settling it failed or changing queue order.
- Exact `deferredBackendSkipV1` is limited to an exact adapter id in the
  contract's bounded recognized optional non-Krea list. Schema version 1 has no
  members. It never skips either Krea adapter or an unknown, future, duplicate,
  or malformed row, and it never changes durable or visible order.
- Health is a bounded passive snapshot. Reading it has no queue, worker,
  ComfyUI, or GPU side effect.
- Health `store.catalogRevision` is the monotonic authority for terminal,
  new-row, and managed-media changes visible in Jobs. Its advance invalidates
  the displayed snapshot by the next poll, while progress-only and heartbeat
  writes may advance `inventoryRevision` without forcing a full SQLite read.
  If catalog revision or counts change during that read, WPF coalesces at most
  one replacement snapshot instead of entering an unbounded reread loop.
  `inventoryRevision` remains mutation-debt evidence; only a legacy health
  writer with no catalog revision may use its advance as a throttled passive
  fallback.
- Explicit durable-Inbox consumption scans at most 256 directory entries, 128
  committed envelopes, and 64 MiB of envelope bytes per poll across pending and
  processing. Each envelope is opened and read through one stable plain-file
  identity, oversized input is quarantined without dispatch, and the Inbox root
  and phase directories must not be links or reparse redirects. Hitting a scan
  bound or observing identity drift fails closed without Jobs mutation.
- The durable `progress` field is the companion-owned percentage of completed
  adapter execution stages. A queued row retains lifecycle value `0` but shows
  only its waiting order and no progress bar. A running row alone shows a
  determinate value from `1` through `99`; `99` means final publication or
  verification is in progress, not an ETA or a remaining-time estimate.
  Succeeded and deleted rows retain lifecycle value `100` but show their
  terminal label without a decorative full bar. WPF clamps presentation to
  these lifecycle bounds without writing queue state, and terminal status
  remains the completion authority.
- If passive health is unavailable because the default authenticated Companion
  is not running, the explicit Connect and Resume control may start the exact
  WPF-owned child, prove identity, perform authenticated recovery, and reread
  current health before sending `paused=false` only when still required. An
  already-running queue gets no duplicate mutation; untrusted, malformed,
  unsupported, ambiguous, or concurrent state fails closed.
- With the Companion unavailable, Jobs may perform one bounded identity-only
  ownership probe and then render the selected local SQLite snapshot read-only.
  Queued or running records without a current valid health signature do not
  arm the one-second poll: manual Refresh and explicit authenticated actions
  remain available, but passive display starts no process and sends no Jobs API
  or queue mutation request.
- Queued or running records discovered during ordinary WPF startup do not by
  themselves enable the three-second catalog-revision timer. That timer starts
  only after this WPF session explicitly activates authenticated durable
  Enhancement work and remains single-flight. `SavedForDelivery` arms a local,
  read-only adoption watch across a pre-recovery zero-active snapshot until the
  first post-arm validated snapshot introduces a previously unobserved valid
  queued or running Job, with both probe-count and elapsed-time bounds.
  Unrelated zero-active revisions and updates to Jobs already active at arm do
  not discharge the watch. The watch sends no API request, recovery, wake, or
  queue mutation. If adoption reveals queued or running work, ordinary active
  watching continues; after terminal or zero-active adoption, or either bound,
  it stops.
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
- MiniMax H3 I2VA prompt candidates pass a pure, pinned conformance profile
  before Apply. Format or reference errors and stale source, input, model,
  style, or mode context disable Apply without repairing the candidate. The
  guide revision and diagnostic evidence are defined by
  `PV-ENHANCE-VIDEO-H3-PROMPT-REWRITE-001`.
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
  explicit `preserve|mute` audio policy, STEP, integer strength, and a required
  maximum pixel tier. Seed, backend, context, actual mask, affected range,
  denoise, source snapshot, and delivery are server-owned. The UI's skip-review
  checkbox is transient one-shot policy and is not durable generation
  semantics.
- Opening or hydrating the Edit board, changing the instruction or selection,
  ordinary seeking, toggling skip-review, and reading health or Jobs are
  passive: they do not resolve, open, hash, or probe a source, extract compiler
  previews, invoke the compiler, publish an inbox item, create a Job, or wake
  work. A displayed file first requires the explicit authenticated
  `フレームを読み込む` action. It may bind, canonicalize, hash, and probe the
  source under a no-delete/read lease and decode bounded requested thumbnails.
  Its transient result contains exact frame count, rational fps, duration,
  dimensions, and exactly three ordered start/middle/end displayable PNG
  thumbnails. Every thumbnail is canonical base64 without a data-URL prefix,
  at most 384 pixels on either edge, 147,456 pixels, and 512 KiB encoded; all
  three are at most 1.5 MiB encoded. The full-resolution source RGB24 digest
  remains the frame identity, while each separately records the encoded PNG
  digest. Exact frame controls,
  compilation, and Start stay disabled until that result is current. A managed
  source may use its exact persisted producer probe and skip the media re-probe.
  Neither path infers 24 fps or frame count from MediaElement duration or
  playback state.
- Probe, preview, and compile share only the exact authenticated Companion
  `POST /api/enhance/video-prompts/v2/edit/compile` route. Requests are at most
  128 KiB. Probe and compile responses remain at most 128 KiB; only explicit
  preview permits a 2,113,536-byte JSON response for base64 expansion. Unknown
  methods and paths fail closed. Health and Jobs reads never dispatch this
  route or start its source/FFmpeg work.
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
- The current disabled Edit health object remains the exact compatible six-field
  shape. A future `state=ready` object is a different exact discriminated
  variant. It must identify Bernini-R-1.3B as the first genuine
  source-video-conditioned semantic V2V backend and bind exact model, workflow,
  compiler, timeline, and delivery revisions. It must also bind one sealed set
  of runtime, model, workflow, compiler, timeline, audio, quality, resource,
  cancel, recovery, and output-validator receipts, plus exact source,
  selection, pixel-tier, process, memory, scratch, output-byte, timeout, cancel,
  and one-execution resource bounds. Image-to-video, two-image retake, FL2VA,
  Ref2VA, AddGuide, per-frame I2I, unknown backends, missing receipts, or
  inferred bounds cannot satisfy this variant.
- Exact ready shape is necessary but not sufficient. Every receipt must resolve,
  hash, and cross-bind the same capability/backend/resource/output revisions,
  current canary result, runner identity, and synthetic fixture. A paired
  activation revision must separately open the production writer. Setting the
  existing writer, backend, runtime, and ready booleans to true, or supplying a
  structurally complete object with synthetic/unresolved receipts, remains not
  ready and cannot publish an inbox item. No live Bernini canary receipt exists
  in this contract revision, so ready advertisement, production writer, and
  quality assertion all remain false.
- Edit persists the exact selected source frames and PTS, then the server-owned
  backend fps, frame map, internal frame count, alignment padding such as
  `4n+1`, delivery crop and source-fps reconstruction, strength mapping, seed,
  canvas, discriminated audio plan, and receipts. Backend 16-fps or alignment
  needs never leak into or rewrite the request selection. Initial output is one
  new non-destructive managed child clip for the selected interval, with no
  source prefix/suffix and no splice into the long source. It reconstructs the
  exact selected source frame count, rational fps, relative PTS, and duration.
  Generated audio is always discarded. `preserve` remuxes and rebases only a
  persisted non-empty encoded source packet range intersecting the selection,
  without re-encoding or a sample-exact trim claim. If no packet intersects, it
  emits no audio stream and stores no packet-range identity. `mute` always emits
  no audio stream and never fabricates a zero-range/hash identity or synthesized
  silence. The source is never overwritten.
- Before success, the child output must pass the exact
  `aibos-video-edit-child-mp4-validator-v1` policy. It is one bounded regular
  MP4 with exactly one H.264 `yuv420p`, 8-bit SDR video stream, zero or one audio
  stream dictated by the persisted audio-delivery variant, and no subtitle,
  data, attachment, unknown, or extra streams. Dimensions, selected frame
  count, rational source fps, rebased presentation timestamps, PTS digest, and
  duration must exactly match the immutable delivery. Generated audio is
  forbidden; preserved encoded packet payloads must match the selected source
  packet identity. Final bytes must fit both the 512 MiB hard ceiling and the
  smaller advertised canaried output bound. The worker validates the same
  closed temporary bytes, persists their receipt/hash/probe/journal identity,
  atomically publishes without overwrite, then reopens and validates the
  published file before marking success.
- A displayed-file staging copy is owned by the logical Job, not by one attempt.
  Exact retry reuses and revalidates that copy and never reopens the external
  path or imports replacement bytes. Cancel preserves it while any retry or
  recovery journal may refer to it. Delete removes it only after terminal Job,
  dependency, retry, journal, publication, and ownership checks all agree;
  ambiguous ownership preserves it and fails closed.
- Each execution attempt has a durable, bounded, identity-bearing journal before
  process launch. It binds Job/attempt, immutable preset hash, backend receipt
  set, source/staging identity, managed dependency closure, scratch ownership,
  temporary output, process ownership, validator result, publication, and
  cleanup state. Retry creates a new attempt and new scratch/output while
  reusing the exact request, seed, source, dependency closure, and compatible
  receipts. Cancel records intent before signalling only its proven process;
  PID alone is insufficient. Startup recovery is an authenticated explicit
  worker action, never a passive health/Jobs-read side effect. Delete and
  cleanup require terminal journal agreement and never repair state by deleting
  source media.
- Managed Edit sources may themselves be outputs of managed Edit Jobs. Under
  the shared Jobs lock, publication walks at most 64 exact producer edges with
  cycle and visited-ID protection, persists the ordered ancestor output closure
  in the durable dependency reservation, and transfers it to the Job without a
  gap. Any ancestor output remains deletion-protected while a descendant inbox,
  active/retry Job, cancel/recovery journal, or publication journal depends on
  it. Missing, malformed, future, cyclic, over-depth, or ambiguous lineage
  fails closed.
- Version 2 Finish is a separate AI spatial super-resolution Job. Its public
  modes are `fast`, `standard`, and `quality`, with explicit 2x or 4x scale.
  Each mode has an independent backend, source-bound, scale, delivery, and
  canary capability; no backend or mode silently falls back to another and no
  candidate ID implies a default mode. The faithful candidate is NVIDIA VFX
  VideoSuperRes via `nvidia-vfx 0.1.0.1`, internal VFX SDK `1.2.0.0`, with a
  server-owned `LOW`, `MEDIUM`, `HIGH`, or `ULTRA` setting. The first future
  activation mapping is fast = one receipt-resolved `LOW` or `MEDIUM`, standard
  = `HIGH`, and quality = `ULTRA`; fast has no inferred default between its two
  values. It is not claimed frame-independent, so scene-cut reset or effect
  recreation remains a gate.
  SeedVR2 3B is the generative-detail candidate and must explicitly pass source
  fidelity, synthesized-texture, and bounded-VRAM canaries. NanoVSR-1.7M is the
  lightweight native-4x candidate; its reported bidirectional recurrent,
  15-frame disjoint-chunk demonstration requires an exact Aibos overlap/crop
  revision and chunk-seam canary. NanoVSR 2x remains unsupported until a
  separate 4x-to-2x delivery mapping passes.
- The current overall Finish health object and each current fast, standard, and
  quality object remain their exact compatible six-field disabled variants. A
  future overall `state=ready` variant is exact and separate from a future
  requested-mode `state=ready` variant. A Finish request is ready only when
  both exact variants occur in the same authenticated health response, their
  backend, setting, overall and mode receipt digests, source and scale bounds,
  scene-cut policy, streaming revision, journal, and output policy cross-bind,
  and the request fits both. A ready standard mode cannot run a disabled quality
  request; a lower setting, 2x, another candidate, or Edit is never a fallback.
  NanoVSR, SeedVR2, FlashVSR, and other candidate families are not part of this
  first activation shape.
- Future faithful Finish readiness requires one source-length-independent,
  bounded decoder/GPU/encoder frame stream and at most one GPU Finish Job. It
  retains only a canaried bounded number of decoded frames and bytes, never the
  complete video in RAM or VRAM. It preserves the complete source frame count,
  rational fps, full video PTS sequence and digest, duration, and persisted
  encoded audio packet identity. It performs no interpolation, frame-rate
  conversion, implicit crop, implicit 4x-to-2x fallback, generated audio, or
  audio-shortness video truncation. Explicit 4x must fit every mode and overall
  dimension, pixel, queue, memory, scratch, and output-byte bound.
- Finish output is initially 8-bit SDR, no larger than 3840 x 2160 or
  8,294,400 pixels, with exact source frame count, rational fps, video PTS,
  duration, and encoded audio packets. It performs no interpolation,
  frame-rate conversion, implicit crop, or implicit scale fallback. Every
  candidate's scene-boundary temporal-state behavior remains an exact canary
  requirement rather than an inferred SDK or model property.
- Before success, Finish writes one attempt-owned temporary regular MP4 and
  validates exactly one H.264 `yuv420p` 8-bit SDR video stream, zero or one
  source-matching audio stream, no extra stream types, full frame/PTS identity,
  requested dimensions, packet-copy identity, and both advertised and 512 MiB
  hard byte ceilings. A separate Finish attempt journal binds Job, attempt,
  mode, scale, backend setting, overall and mode receipt sets, source and
  dependency identities, scratch, process/helper ownership, validator result,
  publication, and cleanup. PID alone is not ownership proof. Retry creates a
  new attempt without changing mode or scale; cancel records intent before
  signalling only proven ownership; passive reads never recover or mount the
  SDK. Published bytes are reopened and matched before success.
- No live Finish canary or quality receipt is asserted in this revision. The
  overall Finish writer, every mode writer, ready advertisement, validator,
  journal writer, and quality assertion all remain false. Defining the future
  discriminated shapes does not activate a production POST path.
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
- The exact frame-selection UI is also reused by separately versioned non-AI
  trim/export version 1. Its wire is the top-level `videoTrim` claim with
  `operation=video`, `mediaKind=video`, and its own `schemaVersion=1`. It is not
  an `edit`, `finish`, or `trim` kind in Video Tools v2; that reader continues
  to reject `kind=trim`. Prompt, compiler, style, STEP, seed, backend, model,
  strength, denoise, and GPU fields are invalid in a Video Trim request.
- Video Trim selects one half-open exact source-frame interval
  `[startFrame,endFrameExclusive)` from either a proven managed producer or an
  explicitly captured displayed file. The source is bounded to 512 MiB,
  300 seconds, 1920 x 1080, 18,000 frames, and exact 24, 30, or 60 fps. The
  selection may use the full bounded source and is not limited to 15 seconds.
  Time labels and thumbnails are presentation only; persisted frame indices and
  checked rational source fps are execution authority.
- Exact frame controls use the separate authenticated transient Video Trim
  source-inspection route only after an explicit user action. `probe` returns
  exact frame count, rational fps and duration, dimensions, rational video time
  base, start timestamp, and the complete source PTS digest. `preview`
  revalidates that exact source identity and one current half-open selection,
  then returns bounded PNGs for three distinct server-verified start, middle,
  and end frames. A selection change makes those previews stale. Probe and
  preview never create or mutate a Job or inbox item, stage a source, wake or
  claim the queue, start a worker, or create output. They are not Video Tools
  Edit prompt compilation, and thumbnails are never frame-selection authority.
- The result is one new non-destructive managed child MP4 containing exactly
  the selected frame count at source rational fps, with zero-origin relative
  PTS and a persisted full PTS digest. Version 1 deliberately re-encodes video
  as H.264 `yuv420p` 8-bit SDR so non-keyframe boundaries remain exact; fast
  keyframe-only stream-copy is not supported. It publishes no source prefix or
  suffix, extra streams, inherited metadata, or source mutation.
- `preserve` selects the same rational interval from source audio and
  re-encodes it as AAC. It does not claim packet bit identity, sample-exact
  boundaries, priming identity, or packet identity. A source without audio
  yields no audio stream. `mute` always yields no audio and never synthesizes
  silence. Audio shortness never truncates the exact selected video frames.
- Video Trim uses a separate single-job CPU-video lane with sealed, bounded
  FFmpeg/FFprobe argv, stderr, process timeout, cancellation, memory, scratch,
  and output limits. It obtains no GPU lease and mounts no model. It reuses only
  the durable inbox, idempotency, source staging/dependencies, Jobs queue,
  attempt-journal framework, and managed output ownership; retry, cancel,
  delete, recovery, publication, frame delivery, audio semantics, readiness,
  and receipts remain its own exact meanings.
- A supported durable Video Trim Job retains one immutable exact `videoTrim`
  snapshot through `queued`, `running`, `succeeded`, `failed`, `canceled`, and
  `deleted`.
  Its source, request, rational frame plan, AAC-or-mute policy, expected exact
  frame delivery, PTS digest, and ownership bindings do not change across
  states. Delivery becomes an available managed child only after a succeeded
  row also carries the reopened validated output identity. Malformed and future
  Job claims preserve compatible unknown fields and remain reader-only with no
  retry, cancel, delete, recovery, execution, or publication action. A
  successful output deletion retains an exact dismissible `deleted` history
  row without output, error, queue, run, or worker identity.
  Known attempt and worker fields are accepted only on a running row; their
  presence on queued or terminal rows is lifecycle drift and makes that row
  reader-only. Compatible unknown fields remain preserved.
  The private execution `sourcePath` is ordinal-exact to the immutable
  execution snapshot. Queue and external process integers use signed Int32
  bounds, while failed error text follows the public technical/control-free
  reader rules without private normalization shortcuts.
  Lifecycle timestamps use the writer's exact four-digit-year, three-digit
  millisecond UTC form. Running IDs are bounded control-free text and running
  diagnostics use the same 32,768-code-unit stable-JSON bound in both readers.
  `sourceId` is bounded control-free text and, for staged displayed sources,
  is ordinal-exact to the immutable original canonical path.
  Recognized JSON numbers are finite and lossless, integers first remain in the
  JavaScript safe exact range, and only mathematically identical stable exponent
  spelling is compatible; rounded high-precision and underflow tokens fail
  closed as reader-only.
- `capabilities.videoTrimV1` is passive and reader-ready, while the current
  runtime, source-inspection route, writer, ready advertisement, live receipts,
  and quality assertion remain false. Isolated synthetic TEMP route fixtures
  are test evidence and never activation. Passive viewing, health, Jobs, search,
  navigation, and hydration never open, hash, probe, stage, enqueue, wake,
  claim, retry, recover, or start a process. A future exact ready variant
  requires the paired public/private
  activation revision plus sealed FFmpeg, FFprobe, runtime, quality, resource,
  cancel, recovery, and output-validator receipts. Unknown, malformed, and
  future claims preserve compatible fields and remain reader-only. The exact
  wire, fixture, bounds, output, lifecycle, and gate are defined by
  `PV-ENHANCE-VIDEO-TRIM-001`.
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

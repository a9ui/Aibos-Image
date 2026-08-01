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
- An explicit AI Start or Retry may start that local companion when it is not
  already available. Passive browsing, preview, navigation, job inspection,
  and state hydration never start it. Before readiness, WPF may stop only the
  exact failed or canceled launch attempt it created. After readiness, the
  companion is an independent durable worker and must not be stopped merely
  because WPF closes.
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
- `enhance/jobs.json`, `enhance/output-root.txt`, and managed outputs below the
  configured parent (with `enhance/outputs/**` retained as the fallback).

The current shared `settings.json` allowlist includes
`confirmBeforeDelete` and `thumbnailStatusBorders`. WPF adopts the shared
delete-confirmation value when present and keeps its local value only as the
migration fallback when the supported shared field is absent. If the shared
document is present but protected, unreadable, malformed, or unsupported, WPF
enables delete confirmation in memory as the fail-safe value without changing
either file. WPF keybindings remain renderer-local and are preserved, not
adopted, when they occur in the shared document.

Shared `settings.json` is readable either as the existing versionless document
or with `version: 1`. A present non-integer or non-1 `version`, malformed JSON,
an empty/whitespace-only file, or an invalid known field is protected and no
writer may change its bytes. Only a genuinely missing file may be initialized
by an explicit owned-setting mutation. Supported writers preserve `version`,
key bindings, unknown root fields, and unknown nested border fields.
Readers and locked writers determine missing state by opening the document;
only `FileNotFound` or `DirectoryNotFound` is missing. Access denial, sharing
contention, and every other I/O failure protect the existing document.

A present, supported `recent-folders.json` is the startup authority for the
last folder set, including an explicit empty `lastFolderSet`. A genuinely
missing, malformed, unreadable, or unsupported-future shared document leaves
its bytes untouched and uses the renderer-local last-folder state only as a
migration/recovery fallback. That fallback does not become a second shared
writer and does not initialize or repair the shared file merely by starting.
Recent readers and writers use the same direct-open missing rule, so an
unreadable existing document can never be replaced as though it were absent.

Derived thumbnails, indexes, and metadata caches are rebuildable data and do
not receive the same retention semantics as the durable set. Renderer-local
presentation state remains local. WPF window geometry, panels, card width,
interface language, keybindings, selection, modal chrome, preview layout, and
similar fields in the existing WPF `state.json` must not be shared wholesale.

Shared writers preserve unrelated and unknown fields when the format permits
it, acquire the contract lock, reread the latest on-disk document, and merge at
the smallest defined semantic unit. Malformed and unsupported future state is
rejected without changing its bytes. A reader must not rewrite state merely by
opening it.

The WPF activation resolves the root once at process startup and routes only
the durable set named above. Normal application startup remains reader-only and
does not create a locator, shared root, durable-data directory, or store. The
only startup write this activation may make is the empty TEMP coordination
directory/file defined by the lease protocol below.
WPF Settings may display that process-fixed data location and open it in the
Windows shell only when the directory already exists. Merely opening Settings,
viewing the location, or pressing the disabled open action never creates a
locator, root, directory, or store. Per-store overrides are reported as having
no single data folder rather than inferring a false shared authority.

The dedicated `.NET 10` `Aibos.SharedRootSetup` process owns the separately
reviewed one-time setup operation. Its default action is inspection only. Apply
requires both `--apply` and the exact `--confirm CREATE` token. The production
surface always targets the default locator; arbitrary locator paths are
available only to a TEMP-bounded smoke fixture.

- The requested root must already exist and resolve to a canonical directory.
- Every present durable JSON store must be readable, unambiguous, and supported;
  missing stores are allowed. Managed Enhancement outputs are inspected without
  modifying or opening them as images.
- Apply holds the protocol-global exclusive writer lease, repeats the
  preflight, content-hashes managed outputs, creates a sibling temporary
  locator with write-through flush, and moves it into place without overwrite.
- A supported existing locator for the same canonical root is idempotent and
  byte-identical. A different, invalid, unavailable, or concurrently appearing
  locator is rejected without replacement.
- Apply compares all store fingerprints plus the managed-output tree before
  and after creation. A mismatch removes only the exact locator payload created
  by that invocation; no durable-state file is repaired or deleted.
- Root migration, store copy/merge, store initialization, locator replacement,
  and automatic setup during either product's startup remain disabled.

### `PV-ROOT-001` — Shared data root locator

The locator protocol is a reader-first compatibility contract. Its default
location is %LOCALAPPDATA%\Aibos Image\shared-root.v1.json. Installers and
isolated tests may select a different locator file with
AIBOS_SHARED_ROOT_LOCATOR_PATH. A set but invalid override fails closed; it
does not fall through to the default locator.

The version 1 locator is UTF-8 JSON no larger than 65,536 bytes:

    {
      "schemaVersion": 1,
      "sharedDataRoot": "<fully-qualified existing directory>"
    }

sharedDataRoot names the data directory that directly contains favorites.json,
seen.json, settings.json, albums.json, search-history.json, and
recent-folders.json, plus enhance/jobs.json and the output-root configuration.
The actual managed output files may live below that configured absolute parent;
without a configuration they remain below enhance/outputs/**. The shared data
root is not a repository root. It may therefore point at an existing legacy
.cache directory without copying or rewriting any durable data.

- Both required fields occur exactly once. Duplicate required fields,
  malformed JSON, unsupported versions, relative roots, unavailable roots, and
  file-valued roots are rejected.
- Readers resolve an existing root to its canonical final filesystem target
  and remove a trailing directory separator except at the volume root. WPF
  fixes that canonical target and all seven store paths for the process
  lifetime, so later redirection of a junction or symbolic-link spelling does
  not redirect active durable state.
- Unknown fields in a supported version are ignored by readers.
- Existing per-store test overrides retain highest precedence. The locator-path
  override is next, followed by the default locator. The legacy repository
  data root is used only when the selected locator file is genuinely absent.
- A reader does not create the locator, root, directories, or stores and does
  not probe writability. WPF fixes the resolved root and all seven store paths
  for the process lifetime. A malformed, future, inaccessible, or invalid
  locator prevents shared-store activation without falling back or changing
  bytes. When a fresh checkout has neither a locator nor an initialized legacy
  data directory, WPF preserves the existing lazy legacy behavior and creates
  no locator, shared root, durable-data directory, or store during startup. The
  empty TEMP lease artifact remains the sole operational exception.
- The locator contains no credentials, network endpoint, dynamic companion
  port, renderer state, index path, thumbnail cache, or migration instruction.

The canonical reader fixture is contracts/shared-root-locator-v1.json. WPF
production routing is enabled only through that reader contract after exact
Aibos and H25 revisions passed the fixture and cross-repository activation
matrix. The reviewed setup tool may create a genuinely missing default locator
only; it never replaces an existing document, so supported unknown locator
fields remain byte-identical. Data migration remains disabled and application
startup never writes the locator.
Activation requires a process-lifetime reader lease. The setup tool requires
the corresponding exclusive writer lease and therefore cannot create the
locator while either compliant application is running.

The v1 lease is an empty operational file under
`%TEMP%\aibos-shared-root-locator-leases-v1`; it is not durable state and is
never deleted at runtime, avoiding last-reader deletion races. Its fixed name
is `locator.lock`, protocol-global within that v1 TEMP directory; no locator
path or user data is encoded in the name. This deliberately conservative scope
also removes cross-runtime Unicode/case-mapping ambiguity: any v1 reader blocks
any v1 locator change, even when separate installations selected differently
spelled locator paths. Readers open or create the file with read access and
`FileShare.Read` and hold that handle until process exit. A locator
creator/replacer opens the same file with read/write access and `FileShare.None`
for the entire same-volume create or atomic-replace operation. A sharing
violation is contention and fails closed; other lease failures are unavailable,
not contention. The lease directory must resolve to the same relative path
below the canonical OS temporary root; a redirected descendant is rejected.
After opening, the lock handle's final path must equal that canonical fixed
path and the file must be empty. The lease may create only this TEMP
coordination directory/file, never the locator, shared root, store directories,
or stores.

### `PV-SET-001` — Shared settings protection

- The document is strict UTF-8, with either no BOM or one leading UTF-8 BOM,
  and is no larger than 1,048,576 bytes including any BOM. UTF-16, UTF-32,
  invalid UTF-8, and oversized documents are protected. Canonical writers emit
  UTF-8 without a BOM and refuse any merged result above the same byte limit.
- Versionless and `version: 1` documents are supported; any other present
  version is protected.
- `confirmBeforeDelete` and each dirty thumbnail-border preference are separate
  owned semantic units. Writers lock, reread, merge only those leaves, validate
  the result, and atomically replace the latest supported document.
- Existing malformed, empty, whitespace-only, future, invalid-encoding,
  oversized, or invalid-known-field documents remain byte-identical and make
  delete confirmation fail safe to enabled in memory. Missing or an absent
  field is distinct and uses the local migration fallback; a missing document
  may initialize only the explicitly changed owned leaf.
- Compatible unknown root/nested fields and renderer-local key bindings survive
  every supported mutation.

### `PV-REC-001` — Recent-folder startup authority

- The document uses the same strict UTF-8, optional single UTF-8 BOM, and
  1,048,576-byte boundary as `PV-SET-001`. Canonical writers emit UTF-8 without
  a BOM and refuse a merged document above that boundary.
- A present supported shared document wins over renderer-local last-folder
  state, including when its `lastFolderSet` is explicitly empty.
- A genuinely missing shared document uses the local migration fallback without
  creating a shared file.
- A malformed, unreadable, invalid-encoding, oversized, or unsupported-future
  shared document uses the local recovery fallback without changing the
  protected bytes.
- Shared recent-folder writers continue to lock, reread, preserve unknown root
  fields, and merge the newest folder set as one semantic unit.

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

## Favorite safety

- Gallery compact/card/list surfaces, the right-preview action row, and the
  bulk relative controls do not expose a Favorite decrement button. Gallery
  users may increment or choose an explicit level 0 through 5. Modal decrement
  and its existing keyboard binding remain available.
- Favorite changes made in the current viewer session are recorded with image
  name, before/after levels, action type, and time. The right preview exposes
  History, Undo, and Redo. `Ctrl+Z` and `Ctrl+Y` apply the same Favorite-only
  undo/redo operations when an editable input or modal is not active.
- Favorite undo/redo never attempts to cancel or reverse Enhancement jobs,
  source recycle operations, image files, Seen state, or Album state.

## Enhancement

- Enhancement begins only from an explicit user action.
- Ordinary browsing, preview, search, modal navigation, and state hydration do
  not enqueue jobs or start workers.
- Original and managed Enhanced outputs remain distinct; source images are not
  overwritten.
- Enhancement operation envelope v1 is an additive extension of the existing
  version 1 job store. `operation` is `upscale`, `photoreal`, or `video`; only
  a genuinely missing value on an older job means `upscale`. A present null,
  malformed, or unknown value is unsupported and fails closed. It must never
  be coerced to `upscale`, executed, retried, reordered, opened, or deleted as
  an image Enhancement.
- The modal exposes separate explicit `AI高画質化` and `AI実写化` actions.
  Photoreal prompt, strength, structure retention, CFG scale, quality steps,
  and work resolution are WPF-local request defaults and do not mutate shared
  Browser settings. All six values are exposed by the application settings
  screen and kept synchronized with the modal popup. The prompt starts with
  the built-in tested default, remains freely editable from both surfaces,
  persists locally as one shared value, and has an explicit prompt Reset.
  Application settings also provide one explicit Reset for all six values.
- A named photoreal Style is WPF-local and snapshots the prompt, strength,
  structure retention, CFG scale, quality steps, and work resolution. Up to 32
  Styles with names of at most 40 characters are persisted in WPF `state.json`.
  Selecting a Style from either the modal popup or application settings applies
  all six values; later manual edits return to the unsaved Custom selection
  without modifying the stored Style. Saving the same name replaces that Style,
  and deleting one leaves the current request values unchanged.
  Quality offers 4, 6, 8, and opt-in `非常に高い（12 step）`; the default remains
  the measured 8-step profile.
  Editing is saved while typing. Each job snapshots the current prompt when it
  is enqueued; already queued or running jobs are not rewritten.
- The built-in photoreal prompt asks the edit model to preserve the source
  identity, expression, mood, occlusions, pose, hand placement, lighting, and
  Japanese/East Asian facial proportions while correcting malformed visible
  hands to five natural fingers. The operation is one model pass: no ADetailer,
  face restoration, hidden upscale, or second generative detail pass is run.
- New photoreal requests use the companion adapter identifier
  `comfyui-flux2-photoreal`; older `a1111-photoreal` jobs remain readable as
  managed historical versions.
- Each valid succeeded output remains an independently selectable version.
  In the modal, one visible dropdown lists Original and every available
  AI高画質化/AI実写化 version; repeated versions are numbered per operation and
  the newest of each operation is identified. `Ctrl+Up` and `Ctrl+Down` retain
  wraparound cycling of the same inventory. Delete removes only the selected
  managed version and never the source or sibling versions.
- Both operations use the same companion `/api/enhance/jobs` endpoint, durable
  ordered queue, and single worker. New jobs append in FIFO order by default.
  They must not create separate GPU queues or run GPU work in parallel. Retry
  and Cancel retain the job operation.
- The gallery exposes independent `AI高画質化済みのみ` and `AI実写化済みのみ`
  filters. Enabling both uses intersection semantics. Cyan `HQ` and violet
  `REAL` thumbnail markers may appear together when both completed operation
  types exist for one source.
- Grid and list right-click menus expose explicit `AI高画質化` and `AI実写化`
  actions for the clicked real source image. Opening a context menu remains
  passive; only choosing either action may start the companion and enqueue
  work.
- `PV-ENHANCE-OUTPUT-001` defines one configurable parent for both operations.
  The parent is selected in the WPF AI実写化 settings section and stored as one
  absolute path in `enhance/output-root.txt`; the fixed flat operation folders
  below it are `Upscaled/` and `Photorealized/`. WPF's dedicated environment
  override and then `PVU_ENHANCE_OUTPUT_ROOT` take precedence and make this
  setting read-only. Without any override or configuration, the legacy
  `enhance/outputs` parent remains the fallback.
- Changing the parent is an explicit atomic settings write. It does not create
  operation folders, move or delete existing outputs, or rewrite recorded job
  paths. A queued job resolves the current parent when it starts processing;
  a running job keeps its already recorded destination. Existing recorded
  absolute output paths remain readable.
- The H25 Browser companion owns the current local Enhancement API and worker.
  WPF owns its loopback client and must keep the API optional.
- Modal and batch Start/Retry first reuse an already-ready loopback companion.
  If none is ready, that same explicit action may launch the separately
  installed H25 companion with Browser opening and ComfyUI autostart disabled.
  A successful ready companion continues the durable ordered queue after WPF
  closes. Reopening WPF passively reads the persisted queue, operation type,
  status, and latest saved integer progress. On companion startup, the worker
  first recovers an interrupted running job as Failed and then immediately
  pumps the remaining queued work without requiring a new enqueue or Retry.
  Queued jobs remain queued across interruption; the interrupted running job
  requires an explicit Retry rather than pretending to resume an in-memory
  model pass.
- The WPF Enhancement Jobs workspace is a virtualized client view over that
  API. Opening it performs a passive jobs read only. It polls once per second
  only while the workspace is visible and at least one job is queued or
  running, and stops polling when hidden or when all jobs are terminal.
  - Jobs may be filtered as All, Queued, Running, Completed, Video, Failed, or
    Canceled. The Video filter includes every video status. Canceled records
    remain durable and visible for audit, Retry, and queue safety.
  Running work is shown first and never reordered. Queued work is inventoried
  in persisted `queueOrder` order with an explicit waiting position. A missing,
  null, invalid, or duplicate order in a legacy reader payload falls back
  deterministically to enqueue time and a stable reader tie-breaker. New jobs
  append by default; queued rows alone may move one place up, one place down,
  or become the next queued job. Reordering never interrupts the running job
  and survives a companion restart. The canonical additive reader fixture is
  `contracts/enhancement-queue-order-v1.json`.
  Stable job-view and thumbnail instances are updated in place so polling does
  not make thumbnails flash. Each row visibly identifies `HQ`/高画質化 or
  `REAL`/実写化.
- Choosing a job thumbnail closes the workspace and opens its validated source
  in the WPF viewer. Open output opens the exact validated managed version in
  that same viewer even when the source is currently hidden by gallery filters
  or belongs to another loaded catalog. This temporary Jobs viewer context does
  not add the source to the durable catalog. Closing either image restores the
  prior gallery selection and returns to Jobs with its filter preserved.
  - Queued, running, and failed rows expose Cancel. Canceling a running job
    interrupts that job and the worker must claim the next queued job without
    requiring another enqueue or Retry. One explicit bulk action cancels queued
    rows only and does not change the running job.
  - Failed and Canceled rows expose Retry, which copies the original job
    snapshot and operation into a newly appended queued job. A completed
    photoreal row exposes `現在設定で再実写化`, which creates a new job from the
    current WPF prompt, strength, structure retention, CFG scale, steps, and
    work resolution rather than silently reusing the old snapshot.
  - Cancel never deletes the source, a managed output, or failure diagnostics.
    Cancel, Retry, re-run, Open output, and Delete output remain explicit user
    actions.
  WPF validates source identity, source signature, and managed-output ownership
  before opening or deleting an output. The workspace does not change the
  `enhance/jobs.json` schema and never starts a worker from ordinary browsing.
- `PV-ENHANCE-HEALTH-001` defines the optional read-only
  `GET /api/enhance/health` companion contract. The Jobs workspace may request
  it alongside its existing passive jobs refresh and display `Healthy`,
  `Working`, `Needs attention`, or `Health unavailable`, active counts, and a
  bounded H25 source-revision prefix. The health request never creates,
  retries, claims, or reorders a job; starts or wakes a worker; polls ComfyUI;
  or launches a missing companion. A missing route, unavailable companion, or
  malformed/future payload leaves the jobs response usable and produces only
  the unavailable health state. Unknown response fields are ignored. An
  unknown issue retains a valid `needs-attention` state with a generic message.
  WPF does not infer a stall from elapsed time alone until a measured threshold
  is adopted. The canonical reader fixture is
  `contracts/enhancement-health-v1.json`.
- Removing the in-repository Browser backend is not merge-ready until a named
  H25 commit passes an isolated TEMP compatibility test against the exact WPF
  candidate. That test must prove request and response compatibility, one
  absolute Enhancement root for `jobs.json` and `outputs/**`, WPF output
  ownership checks, restart recovery, unchanged source bytes, and zero writes
  to user-owned state or caches.

### `PV-ENHANCE-VIDEO-001` — Reader-first managed video operation

`video` is a distinct managed-media operation. It is not an AI-upscaled or
photoreal image version, and an MP4 must never enter an image decode or
image-output deletion path. The modal may expose image and video versions in
one typed display selector, but selecting a video must route only to managed
media playback. The canonical additive fixture is
`contracts/enhancement-video-v1.json`. Video requests may additionally
name `sourceProducerJobId`. When present, it is a durable reference to one
succeeded photoreal job; WPF never sends that job's output path. The companion
revalidates producer ownership and the managed still image, while the video
row keeps the Original catalog identity in `sourceId` and pins the actual
input in `sourcePath`, `sourceSignature`, and `sourceSha256`.

- The gallery context menu offers Original plus every valid, unambiguous
  photoreal version for the selected catalog image. Each photoreal choice is
  bound to its exact producer job id; stale outputs and duplicate producer ids
  are omitted. Upscaled versions are deliberately not video input choices.
  The modal action instead uses the currently displayed photoreal version when
  one is selected, otherwise Original.
- The explicit video button always opens its bounded, scrollable settings
  board. If the selected input version is missing, stale, or ambiguous, the
  board explains the concrete input error and disables enqueue rather than
  appearing unresponsive. Opening the board alone remains passive.
- The modal's single display-version dropdown lists Original, every valid
  upscale and photoreal image version, and every valid managed video version.
  The last selected media kind and exact job version are retained per source
  for later modal navigation in the WPF session. A source with no retained
  selection, or whose retained version is no longer valid, falls back to
  Original without weakening any ownership check.
- Selecting a video starts playback in the modal. A short primary click on the
  video toggles play and pause, completed playback loops from the beginning by
  default, and an upward primary-button swipe enters full screen. Escape exits
  full screen before it closes the modal. These gestures never enqueue or
  mutate a job.
- For a succeeded video row in Jobs, selecting the thumbnail opens that exact
  managed video in the modal and starts playback. `Open output` instead opens
  Explorer at the validated MP4 and selects it. Image-job thumbnail and output
  behavior remains unchanged.
- A video job keeps `mediaKind: "video"` and a media-specific `video` snapshot
  alongside the existing version 1 job envelope. The snapshot records the
  requested duration, Wan generation FPS, and user prompt; the native
  effective frame count, width, height, positive prompt, negative prompt, and
  quality-bound step count;
  the enqueue-time seed; model/preset identities; codec; and bit depth. Retry
  reuses the whole persisted snapshot and seed. Compatible unknown fields are
  preserved.
- `video.delivery` is an optional additive v1 field. A genuinely missing field
  is the legacy shape: managed-video playback metadata continues to use
  `requested.playbackFps` and `effective.frameCount` (normally 16 fps and 97
  frames). A present field is supported only when it is an object with the
  exact current delivery identity and values: backend
  `vs-rife-5.7.0-rife-4.25-v1`, model `4.25`, target 30 fps, duration 4 or 6
  seconds, respectively 120 or 180 frames, `yuv420p`, and no audio. Missing,
  null, mistyped, duplicate, extra, or inconsistent delivery members make the
  row protected. Presence is never coerced to legacy absence. A protected row
  is not canceled, retried, reordered, opened, or deleted through a managed
  video mutation path.
- Normal v1 Wan generation uses Wan2.2 TI2V 5B FP16, nominal 6 seconds, 16
  generation fps, 97 native frames,
  an aspect-preserving 32-pixel-aligned bucket at or below 409,600 pixels,
  20 steps, CFG 5, `uni_pc` with `simple`, shift 8, denoise 1, an int32 seed
  fixed at enqueue, and 8-bit H.264 in MP4. A blank prompt means the built-in
  conservative anime idle-motion instruction. A custom prompt uses the
  preservation preamble plus the user's instruction and is not contradicted by
  blank-only idle or locked-camera wording.
- Quality is an exact preset choice, not a free-form step field. Normal remains
  `wan22-ti2v-5b-normal-v1` at 20 steps and is the persisted default. High is
  the explicit `wan22-ti2v-5b-high-v1` preset at 40 steps. High keeps the same
  FP16 model, pixel budget, native frame count, RIFE delivery, one-worker
  queue, and exclusive GPU lease. A known preset paired with the wrong
  `effective.steps` value is protected as reader-only instead of being
  coerced.
- A named video Style is WPF-local and snapshots the prompt, model, quality
  preset, duration, generation FPS, and maximum pixel budget. Up to 32 Styles
  with names of at most 40 characters are persisted in WPF `state.json`.
  Selecting one from the video board or application settings applies all six
  values; a later manual edit returns to the unsaved Custom selection without
  modifying the stored Style. Saving the same name replaces that Style, and
  deleting one leaves the current request values unchanged. Jobs still
  snapshot the effective values only when explicitly enqueued. Restoring a
  Style that names the 12GB-unverified Hunyuan candidate does not bypass its
  disabled execution state.
- The current delivery stage uses RIFE 4.25 to publish exactly 30 fps and
  duration-times-30 frames: 120 frames for 4 seconds or 180 for 6 seconds.
  Final H.264 output is `yuv420p` and contains no audio. Managed-video labels
  and playback metadata use those delivery values when the field is valid;
  legacy rows continue to show their native values. WPF labels the selectable
  12/16 fps value as generation FPS and separately identifies the final 30 fps
  RIFE 4.25 output.
- WPF completion estimates include both Wan generation and RIFE delivery.
  The measured RTX 4070 SUPER 12GB landscape baseline at 832x480 is
  146.691 seconds plus 11.768 seconds. The earlier portrait baseline at
  480x832 is 202.942 seconds plus 15.318 seconds, with 218.810 seconds observed
  end to end. That first portrait output was rejected for visible replicated
  edge padding, so its timing remains evidence but is no longer the
  conservative ETA upper bound.
  The refined 480x800 portrait output was adopted for the anime M1 smoke at
  158.825 seconds plus 18.070 seconds, 177.458 seconds end to end, and
  11,765 / 12,282 MiB sampled peak VRAM. A later 512x768 photoreal-input run
  measured 274.801 seconds for Wan plus 17.560 seconds for delivery and
  292.702 seconds end to end; it is the current conservative ETA upper
  baseline. WPF scales Wan by native frame count and maximum pixel budget,
  scales delivery by duration and the same maximum pixel budget, and presents
  the landscape-to-portrait result as a range because orientation, content,
  motion, and cold-run effects remain material. Defaults display about 2:38
  to 4:53 for Normal and about 5:05 to 9:28 for High. The 4-second,
  12-generation-fps, 307,200-pixel setting displays about 1:01 to 1:53 for
  Normal and about 1:57 to 3:38 for High. Only the measured Wan component is
  scaled by `steps / 20`; RIFE delivery time is unchanged. Queue wait is
  excluded.
- The prepared input is a separate contain-resized, edge-padded PNG at the
  exact effective bucket. After the ordinary over-budget convergence, bucket
  refinement compares the current bucket with each valid width-minus-32 and
  height-minus-32 neighbor. Its score is `relativeAspectError + 0.25 *
  unusedAreaRatio`; it moves to the lowest strictly improving neighbor and
  repeats until neither neighbor improves the score. The standard 16:9, 3:2,
  4:3, and 1:1 buckets remain 832x480, 768x512, 736x544, and 640x640. The
  975x1614 bird input refines to 480x800, reducing edge-copy padding. The
  source is never cropped, rewritten, or used as a temporary output.
- Final video outputs use the fixed flat `Videos/` folder below the same
  configured Enhancement output parent. The filename includes job, source,
  and preset identities. A core ComfyUI staging file is allowed only as an
  exact adapter-owned transient and must be removed after success, cancel, or
  failure; the final residue audit is zero.
- Wan generation and RIFE delivery remain one video job and share the existing
  durable ordered queue, worker, and exclusive GPU lease with upscale and
  photoreal jobs. Cancel applies to the active stage and leaves no separate
  delivery job. There is no second worker, parallel inference, forced
  high-VRAM mode, or automatic fallback model.
- Reader rollout precedes writer rollout. Aibos first recognizes `video` as
  reader-only media and protects unknown operations. H25 must then pass the
  exact canonical fixture and retain the same fail-closed rule. Only after
  both exact readers are green may H25 emit a video row, followed by the WPF
  enqueue/UI writer. Once a video row exists, rollback may disable video
  enqueue but must not deploy a pre-video reader.
- The reader-only WPF phase may inventory a video row as `VIDEO` in Jobs, but
  it does not retry, cancel, reorder, open, or delete that row until the exact
  H25 writer and managed-video ownership contract are active. Passive reads
  remain read-only and never start the companion or ComfyUI.
- Source readiness and live rollout are separate. The canonical activation
  record is `contracts/enhancement-video-writer-activation-v1.json`: the exact
  H25 writer and guarded WPF mutation client are code-ready, while both live
  runtime flags remain false because the running PhotoViewer API, ComfyUI, and
  Aibos WPF were deliberately not restarted. This state permits merge and
  candidate verification; it does not claim production cutover.
- A 14B or alternate-model HQ profile, 24 fps native generation, and an
  approximately 704p default remain deferred until measured evidence on the
  supported 12GB GPU establishes memory, latency, playback, and anime
  temporal-quality bounds.

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

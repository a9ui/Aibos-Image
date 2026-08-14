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
- WPF may start the separately installed, API-only H25 Enhancement companion
  with the application so persisted Jobs are immediately readable. This startup
  mode exposes the authenticated API only: it does not recover or pump the
  queue, drain the durable enqueue inbox, start ComfyUI, or perform GPU work.
  The companion opens no Browser
  window and does not load the Browser Viewer, Album, Search, thumbnail, or
  Favorite surfaces.
- The API-only guarantees above apply to the default Enhancement companion
  launcher. The explicit `AIBOS_H25_LEGACY_NEXT_COMPANION=1` rollback switch
  selects the unchanged legacy Next runtime instead. That switch is outside
  normal companion mode and is retained only for a controlled rollback; while
  it is enabled, the API-only and no-Viewer-loading guarantees do not apply.
- An explicit AI Start or Retry first sends an authenticated, bodyless queue
  recovery request and may start the API if it is not already available.
  Passive browsing, preview, navigation, job inspection, and state hydration
  never recover or pump the queue. Before readiness, WPF may stop only the
  exact failed or canceled launch attempt it created. After readiness, the
  companion is an independent durable worker and must not be stopped merely
  because WPF closes.
- The companion endpoint is loopback-only and must bind to `127.0.0.1`. LAN,
  tunnel, reverse-proxy, hosted, and Internet exposure are unsupported.
- Loopback reachability or an arbitrary HTTP response is not companion
  ownership. Before health, jobs, output, queue control, or durable enqueue,
  WPF verifies a nonce-HMAC identity response bound to the companion instance,
  process ID, and process start time. Every later request and response is
  encrypted, authenticated, and bound to that exact instance/start epoch.
  Listener replacement after proof exposes only an opaque fixed-route envelope.
  An unknown listener fails closed before WPF sends source identity, prompt,
  settings, credentials, or a job body and before it writes a reservation.
  The exact additive wire contract is
  [`contracts/enhancement-companion-auth-v2.json`](../contracts/enhancement-companion-auth-v2.json).
- An unavailable companion must not affect ordinary viewing or source images.
  An explicit enqueue either publishes a bounded durable reservation or reports
  that nothing was saved; it must never report success for a failed local save.

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
- `enhance/jobs.sqlite3` as the current Enhancement Jobs authority,
  `enhance/jobs.json` as the retained legacy/rollback store,
  `enhance/output-root.txt`, the versioned explicit-enqueue inbox below
  `enhance/enqueue-inbox/**`, and managed outputs below the configured parent
  (with `enhance/outputs/**` retained as the fallback).

Managed outputs use the same final layout for every operation:
`Upscaled/YYYY-MM-DD/`, `Photorealized/YYYY-MM-DD/`,
`Edited/YYYY-MM-DD/`, and `Videos/YYYY-MM-DD/`. The date is derived only from
the completed output file's own Windows CreationTime in the companion's local
timezone. Job creation/start/finish timestamps, source EXIF, source
CreationTime, and source LastWriteTime are not date inputs. An adapter writes a
provisional file outside every date folder; after publication the companion
reads that file's CreationTime, performs a same-volume no-replace atomic
finalize, and writes only the final path to the completed durable job. Missing,
non-finite, implausibly old, or materially future CreationTime fails closed and
is reported without guessing another date.

The one-time existing-output migration is separately gated. It inventories
only the four configured operation roots, plans every destination from each
real file's CreationTime, pauses and drains the queue, completes every
same-volume file move, and only then remaps durable output/source references in
one locked store write. Its bounded plan, pre-write snapshot, receipt, file and
byte totals, reference digest, ambiguous-date report, and zero remaining-move
check are retained locally. It never uses source or job timestamps and never
copies, deletes, or republishes model, source, or output bytes.

`PV-ENHANCE-JOBS-SQLITE-001` defines the current local durable Jobs store.
The loopback companion is its only writer and commits through SQLite WAL. WPF
opens only the reader surface in read-only/query-only mode, requires SQLite
3.50.2 or newer, and never creates, repairs, migrates, pauses, reorders, or
claims work through the database. A present `enhance/jobs.sqlite3` is preferred
unless an explicit jobs-path override is set; otherwise the version-1 JSON
reader remains the compatibility fallback. There is no dual-write mode.
Migration requires a paused, drained queue plus semantic and ordering checks;
the source JSON and verified rollback material remain local. The additive
`catalog_revision` advances only when the WPF-visible managed-media projection
changes, so heartbeat and progress writes do not force a full direct-reader
reload. The canonical public schema surface and synthetic fixture are
`contracts/enhancement-jobs-sqlite-v1.json`.
SQLite may create or update its standard `-wal`/`-shm` coordination sidecars
while serving that read-only connection. Those sidecars are not a second
logical writer or durable-data authority; WPF never writes a table or the main
database file.

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
does not create a locator, shared root, durable-data directory, or logical
store. Startup writes are limited to the empty TEMP coordination directory/file
defined by the lease protocol below and SQLite's standard WAL coordination
sidecars when the current Jobs database requires them.
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
recent-folders.json, plus enhance/jobs.sqlite3 (or the retained legacy
enhance/jobs.json) and the output-root configuration.
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
- Once the enlarged-image modal is open, its source identity is pinned to the
  displayed source path until the user navigates or closes it. A background
  gallery selection, Favorite-filter projection, catalog refresh, or async
  Enhancement refresh must not retarget the modal decode, Favorite mutation,
  version selection, AI action, delete, external-open, or video-source action.
  If that pinned source disappears from the active catalog, the modal closes
  instead of falling through to an unrelated gallery selection.
- Album order is preserved when an Album is the active source.
- Search and Album sources do not overwrite each other's owned collections.
- The WPF gallery may sort newest-first by completed upscale, photoreal, or
  video activity. It uses `completedAt` when present, otherwise `updatedAt`,
  otherwise `createdAt`; legacy rows without `completedAt` are therefore an
  explicit last-update approximation. Images without a usable timestamp sort
  after timestamped images with filename and path as stable tie-breakers.
- WPF exposes one renderer-local, persisted control for the filename/file-size
  caption over large Grid cards. Turning it off removes the full caption
  gradient without hiding Favorite, Enhancement, queue, or other status
  indicators. Compact icon cards already have no caption, and List identity
  text remains visible because it does not overlap the thumbnail.
- Presentation geometry and gestures not stated here remain WPF implementation
  details.
- The left filters/folders sidebar is 300 pixels by default and has a keyboard-
  accessible draggable edge. Wide layout clamps the persisted width to 272–480
  pixels; compact layout keeps its separate fixed rail. Resizing must preserve
  the gallery viewport anchor and must not alter Favorite, folder, or queue
  state.

## Favorite safety

- In the enlarged-image modal, Favorite targets the version actually displayed.
  Original uses the canonical source path as its shared `favorites.json` key.
  A validated managed Photoreal version uses that exact output path as a
  separate key, so each Photoreal output and its Original keep independent
  levels. A validated managed Video version likewise uses that exact MP4 output
  path as a separate key. Upscale and I2I displays retain the Original/source
  Favorite meaning in this contract version.
- The gallery's primary red Favorite badge, normal Favorite filters, and
  `Fav touched` sort retain Original/source semantics even when its thumbnail
  displays a managed Photoreal image. In addition, one blue heart shows the
  highest Favorite level among currently validated managed `photoreal`
  outputs for that Original. Its `実写` internal levels 1 through 5 are
  selectors for the unified rule below. The UI uses five visibly labeled
  neutral controls. The one top-level localized `Unrated only` switch means
  the Original/source Favorite is level 0, including when a managed Photoreal
  or Video version has its own positive Favorite. This is a
  presentation over existing job and path-keyed Favorite state, not a shared
  schema change. Individual version editing remains in the modal.
  Every supported writer must merge only its changed path keys into the latest
  shared map; a normal write must preserve catalog-external managed-output keys.
- WPF groups its local Favorite filters into one `お気に入り` section: Original
  keeps the red heart, managed Photoreal uses the blue heart, and managed Video
  uses the purple heart. These rows are separate views over the same retained
  path-keyed Favorite data; they do not merge the three version meanings. Each
  row exposes neutral `Lv 1` through `Lv 5` controls. The five controls use a stable three-column,
  two-row layout below the category label so the buttons neither overlap nor
  truncate at the minimum sidebar width. Each button has at least a 30-pixel
  hit surface and does not collapse into a tiny color swatch.
  Filter controls do not use category color or intensity to encode levels, and
  their checked and unchecked states must remain visibly distinct. Gallery
  hearts retain their red, blue, and purple category meanings.
  Pressing the checked `お気に入りのみ` control again turns it off. Filter
  projection and removal run asynchronously so the UI remains responsive.
  These selectors do not filter the gallery while `お気に入りのみ` is off.
  While it is on, every selected Original, Photoreal, and Video level is
  combined with OR semantics: matching any enabled category/level keeps the
  Original in the gallery. If no level is selected in any row, the master
  switch means any positive Favorite in any available category. The
  gallery purple heart is the maximum Favorite level among currently validated
  managed Video outputs for that Original. Temporarily missing output paths retain their keys under the
  removal rule below.
- Removing or temporarily losing a managed output does not delete its Favorite
  entry. The entry is invisible while that exact path is unavailable and is
  reused only if the same validated output path becomes available again. This
  matches the existing retention of Favorite history when an Original leaves
  the active catalog.
- Gallery compact/card/list surfaces, the right-preview action row, and the
  bulk relative controls do not expose a Favorite decrement button. Gallery
  users may increment or choose an explicit level 0 through 5. Modal decrement
  and its existing keyboard binding remain available.
- Favorite changes made in the current viewer session are recorded with image
  name, before/after levels, action type, and time. The right preview exposes
  History, Undo, and Redo. `Ctrl+Z` and `Ctrl+Y` apply the same Favorite-only
  undo/redo operations when an editable input or modal is not active.
- WPF stores the latest successful Favorite interaction time per image in
  renderer-local `state.json` for its `Fav touched` sort. Setting level 0 is an
  interaction and remains in this local history. Older images with no recorded
  time sort last. Only the newest 20,000 activity rows are retained so this
  renderer-local presentation history cannot grow without bound. This field is
  not added to shared `favorites.json` and does not change cross-repository
  Favorite meaning.
- Favorite undo/redo never attempts to cancel or reverse Enhancement jobs,
  source recycle operations, image files, Seen state, or Album state.

## Enhancement

- Enhancement begins only from an explicit user action.
- Ordinary browsing, preview, search, modal navigation, and state hydration do
  not enqueue jobs or start workers.
- Original and managed Enhanced outputs remain distinct; source images are not
  overwritten.
- Enhancement operation envelope v1 is an additive extension of the existing
  version 1 job store. `operation` is `upscale`, `photoreal`, `i2i`, or `video`; only
  a genuinely missing value on an older job means `upscale`. A present null,
  malformed, or unknown value is unsupported and fails closed. It must never
  be coerced to `upscale`, executed, retried, reordered, opened, or deleted as
  an image Enhancement.
- The modal exposes separate explicit `AI高画質化` and `AI実写化` actions.
  LoRA enabled, Positive prompt, Positive prompt used when the first field is blank,
  Negative prompt, strength, CFG scale, quality steps, and work resolution are
  WPF-local request defaults and do not mutate shared Browser settings. All
  eight values are exposed by the application settings screen and kept
  synchronized with the modal popup. The three prompt fields remain freely
  editable from both surfaces, persist locally, and each has an explicit Reset.
  Application settings also provide one explicit Reset for all eight values.
  At enqueue time, a nonblank Positive prompt is sent verbatim after trimming;
  otherwise the trimmed blank-field fallback is sent. The trimmed Negative
  prompt is sent through the companion's real negative-conditioning input.
  With the FLUX CFG guider, CFG 1.00 gives Negative zero guidance contribution;
  the WPF labels this dependency and users can select a value above 1.00 when
  they intend to use Negative guidance. No hidden Positive sentence is appended
  from a composition setting.
- WarmBloodAban Anything-to-Real is an optional comparison LoRA. Fresh/reset
  settings default to LoRA OFF with a dormant 40% value; OFF means the companion
  omits the LoRA node and does not require the LoRA asset. A legacy saved Style
  or queued job that predates the boolean retains its historical ON meaning.
- Application settings expose a separate searchable PNG Prompt inheritance
  editor. Each row has Enabled, category, exact A1111 source tag, and editable
  Positive output text, with Add/Delete/Reset. Clicking the Enabled checkbox
  toggles it directly with one click rather than requiring the row to enter edit
  mode first. Production prompt defaults and inheritance rows are supplied by
  an ignored local policy selected with `AIBOS_WPF_PROMPT_POLICY_PATH`, or by
  `config/wpf-prompts.local.json` beside the application/current directory.
  The tracked example documents only the schema and synthetic values. Missing
  or invalid local policy uses bounded public placeholders and an empty default
  table; an explicitly empty persisted table stays empty. A higher local policy
  revision appends only missing rows to older persisted state and preserves
  custom rows and edits. Prompt values and their revision history are not part
  of the public source contract.
  Matching ignores case, treats `_` and an ASCII space as equivalent,
  keeps hyphens distinct, ignores A1111 outer attention brackets, numeric
  weights, and escaping, treats a top-level standalone `BREAK` token as a separator,
  preserves PNG source order, and deduplicates output. A local row may use
  bounded `matchTokens` and `excludeTokens` arrays for generic related-tag
  matching without embedding those private values in source. A complete mapped
  fragment is appended only when the resulting Positive remains within 2,000
  characters. Overflow fragments are skipped at phrase boundaries rather than
  truncating text or failing the explicit enqueue.
  In the enlarged Original PNG view, each Positive prompt chip exposes a
  right-click action that opens this editor at the matching row. A missing tag
  is staged as an enabled `カスタム` row whose initial output is the same
  normalized text; A1111 numeric weight and attention wrappers are removed.
  Existing normalized tags are selected without duplication, and Cancel keeps
  the table unchanged.
  The editor's normal actions and primary save action use explicit contrasting
  dark backgrounds, light text, and visible borders; text/background contrast
  remains at least 4.5:1.
- A named photoreal Style is WPF-local and snapshots LoRA enabled, the three raw
  prompt fields, strength, CFG scale, quality steps, and work resolution. Up to 32
  Styles with names of at most 40 characters are persisted in WPF `state.json`.
  Selecting a Style from either the modal popup or application settings applies
  all eight values; later manual edits return to the unsaved Custom selection
  without modifying the stored Style. Saving the same name replaces that Style,
  and deleting one leaves the current request values unchanged.
  Quality offers 4, 6, 8, and opt-in `非常に高い（12 step）`; the default remains
  the measured 8-step profile.
  Editing is saved while typing. Each job snapshots the resolved Positive and
  current Negative prompt when it is enqueued; ordinary settings edits do not
  rewrite queued or running jobs. The explicit Jobs action described below is
  the only queued-job prompt replacement path.
- Photoreal defaults come from the same ignored local policy and remain freely
  editable in WPF. Their concrete wording is not tracked. The operation is one
  model pass: no face restoration, hidden upscale, or second generative detail
  pass is run.
- New photoreal requests use the companion adapter identifier
  `comfyui-flux2-photoreal`; older `a1111-photoreal` jobs remain readable as
  managed historical versions.
- `PV-ENHANCE-I2I-001` defines explicit local image editing without a manual
  mask. `i2i` is a distinct managed-image operation shown as `AI編集`; it is
  never counted or labeled as photorealization or upscale. The request chooses
  the indexed Original or one exact succeeded photoreal producer by durable
  `sourceProducerJobId`; clients never submit a managed output path. The job
  keeps the Original catalog identity. The first writer slice accepts only
  `target=hair-color`; its immutable preset snapshot records the requested
  hair color, optional bounded detail, effective prompt, numeric generation
  settings, seed, workflow revision, and mask revision. Hairstyle and outfit
  editing remain unsupported until a later measured protocol revision.
- I2I v1 uses the existing local FLUX.2 reference-conditioning runtime with
  the comparison LoRA OFF, one model pass, empty Negative conditioning, and
  CFG 1.0. The companion derives a hair mask with SAM 3.1, protects the face
  core detected with MediaPipe, runs one FLUX.2 reference-edit candidate, and
  deterministically composites only the bounded feathered hair region onto the
  exact selected source. Protected face-core pixels and every pixel outside
  the editable mask come from that source. Missing or ambiguous source, subject,
  hair mask, or face protection fails closed without publishing a full-frame
  fallback. SAM and FLUX are loaded sequentially on the one existing GPU worker;
  no manual mask, face-restoration pass, hidden upscale, second worker, or
  parallel GPU queue is introduced.
- I2I controls are explicit and transient: opening a menu, modal, source list,
  or settings board remains passive. Only the enlarged-image modal opens the
  bounded `AI編集` boards. The modal toolbar and context menu expose one
  `AI編集` entry whose passive submenu selects either the focused hair-color
  editor or the multi-target outfit/expression/background/pose editor; those
  two protocol revisions remain separate internally. The source is the exact
  version currently displayed, using its valid photoreal producer when
  applicable. There is no gallery-list enqueue action or source picker in the
  first slice. The multi-target entry opens the existing board-level target
  selector; it does not claim that a specific target was chosen in the picker.
  Opening either board retires the other board generation before showing the
  new one, and a pending writer action blocks route switching. A retired health
  response cannot reopen or update the former board. Picker selection and board
  opening remain read-only; only the board's explicit queue action may publish.
  I2I outputs and upscaled outputs are not v1 input choices. Retry copies the
  complete saved preset and source provenance; Cancel, queue order, enqueue-next,
  pause/resume, output open, and output delete retain the existing Jobs rules.
  Enqueue is enabled only after the open edit board has observed
  the schema-matched `capabilities.i2i` or `capabilities.i2iV2` object reporting
  the exact contract, target, revisions, and all reader/writer/backend/ready
  flags as ready. After that proof, a transiently
  unavailable second health probe may still save the explicit create
  reservation locally; a positive not-ready response rejects it before save.
  Retry has no board-scoped proof and therefore requires an exact ready health
  response on that action before its reservation is saved. A not-ready POST
  returns 503 without adding a durable row or waking the queue.
  The companion writer is default-off and requires the explicit local
  `PVU_I2I_WRITER_ENABLED=1` process gate in addition to the verified mask
  assets. Removing that gate disables new enqueue while retaining the reader
  and existing managed outputs for rollback.
  The canonical additive fixture is `contracts/enhancement-i2i-v1.json`.
- Each valid succeeded output remains an independently selectable version.
  In the modal, one visible dropdown lists Original and every available
  AI高画質化/AI実写化/AI編集/動画化 version. The inventory is grouped and numbered as
  `Original`, `実写化 n/N`, `高画質化 n/N`, `AI編集 n/N`, and `動画化 n/N`
  independently. This dropdown is the only visible entry for switching between
  Original and generated versions; the modal does not expose a separate
  `差分` button. HQ, 実写化, 動画化, and 実写編集 remain explicit operation buttons.
  `Ctrl+Up` and `Ctrl+Down` retain
  wraparound cycling of the same inventory. Delete removes only the selected
  managed version and never the source or sibling versions.
- Ordinary explicit AI upscale defaults to the installed portable Real-ESRGAN
  ncnn route. An explicitly saved ComfyUI selection remains available and WPF
  never silently falls back from the selected route. The fixed
  `realesrgan-x4plus` photo network is always invoked at native 4x; requests
  for other display scales are resized from that valid native output. Scale
  values must not be passed to the fixed photo network as if it had separate
  2x or 3x weights, because that mismatches ncnn tile/output geometry and can
  produce magnified square blocks. The anime family may use its actual
  scale-specific 2x, 3x, and 4x model files.
- Explicit `AI高画質化` uses the currently displayed managed photoreal version
  when one is selected. WPF sends only its durable `sourceProducerJobId` and
  the photo profile `photo-natural-x2` / `realesrgan-ncnn` / scale 2; it never
  sends a managed output path. The companion revalidates that the producer is
  a succeeded photoreal job for the same Original, runs the adapter against
  that exact output, and keeps the Original `sourceId`, `sourcePath`, and
  signature on the new upscale row so version grouping and Retry remain stable.
  Original or already-upscaled selections retain the ordinary upscale profile.
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
- The enlarged-image menu exposes the same direct and enqueue-next AI actions,
  while retaining display-version, zoom, file, Album, and delete actions in
  separate visual groups. Grid and list menus also group direct AI, queue,
  video, and ordinary file/view actions. Opening any menu remains passive.
- Those menus also expose `次に高画質化` and `次に実写化`. The companion must
  advertise `atomicImageEnqueueNext`; the POST then carries
  `queuePlacement: "next"` and inserts first among waiting jobs under the claim
  lock without preempting the running job. Missing capability never silently
  degrades to tail insertion.
- `PV-ENHANCE-OUTPUT-001` defines one configurable parent for all operations.
  The parent is selected in the WPF AI実写化 settings section and stored as one
  absolute path in `enhance/output-root.txt`; the fixed flat operation folders
  below it are `Upscaled/`, `Photorealized/`, `Edited/`, and `Videos/`. WPF's dedicated environment
  override and then `PVU_ENHANCE_OUTPUT_ROOT` take precedence and make this
  setting read-only. Without any override or configuration, the legacy
  `enhance/outputs` parent remains the fallback.
- Changing the parent is an explicit atomic settings write. It does not create
  operation folders, move or delete existing outputs, or rewrite recorded job
  paths. A queued job resolves the current parent when it starts processing;
  a running job keeps its already recorded destination. Existing recorded
  absolute output paths remain readable.
- The dedicated H25 Enhancement companion owns the local Enhancement API,
  durable inbox consumer, and worker. WPF owns its loopback client and the
  explicit enqueue publisher; ordinary viewing must keep the API optional.
- Aibos application startup may launch the separately installed API-only H25
  companion in deferred-recovery mode so Jobs history is available immediately.
  This mode does not drain the enqueue inbox or start the queue worker. Modal and
  batch Start/Retry first reuse that authenticated loopback companion. If none
  is ready, the same explicit action may launch the separately
  installed API-only H25 companion through the default launcher. It must not
  start or load the Browser
  Viewer, React, Albums, Search, thumbnails, or Favorites, and ComfyUI autostart
  remains disabled.
  After an explicit Start/Retry, WPF sends a bodyless encrypted queue-recovery
  request before reserving new work. That request first recovers an interrupted
  running job as Failed and then pumps the remaining queued work. Recovery is
  idempotent per companion process: concurrent or repeated authenticated
  requests do not reclassify a job that began running after recovery. A successful
  active companion continues the durable ordered queue after WPF closes.
  Reopening WPF starts only the passive API and reads the persisted queue,
  operation type, status, and latest saved integer progress.
  Queued jobs remain queued across interruption; the interrupted running job
  requires an explicit Retry rather than pretending to resume an in-memory
  model pass.
- The WPF Enhancement Jobs workspace is a virtualized client view over that
  API. Opening it performs a passive jobs read only. It polls once per second
  only while the workspace is visible and at least one job is queued or
  running, and stops polling when hidden or when all jobs are terminal. Active
  polling reads compact health first. It reloads the full jobs inventory only
  when counts, current job identity, last claim, or last terminal time changes,
  when the companion process/start/build identity changes, or when compact
  health is unavailable. Current progress is applied directly
  from health without forcing the multi-megabyte inventory read. Mutations
  issued by this WPF reconcile their returned queue immediately; the v1 health
  contract has no general inventory revision, so a same-cardinality queued-only
  mutation issued by another client remains available through explicit Refresh
  or reopening the workspace until a later companion contract adds such a
  revision.
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
  The Jobs header exposes one durable Pause/Resume control when the companion
  advertises `worker.paused`. Pause prevents new claims but lets the current
  job finish; queued rows and their FIFO order survive WPF/companion restarts.
  Resume preserves that order. An older companion without the capability keeps
  the control disabled instead of pretending to pause.
  Stable job-view and thumbnail instances are updated in place so polling does
  not make thumbnails flash. Each row visibly identifies `HQ`/高画質化,
  `REAL`/実写化, `EDIT`/AI編集, or `VIDEO`/動画化.
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
    snapshot and operation into a newly appended queued job. Succeeded, Failed,
    and Canceled photoreal rows also expose `現在設定で再実写化` and
    `現在設定で次に実写化`, which create a separate new job from the
    current WPF resolved Positive prompt, Negative prompt, LoRA enabled,
    strength, CFG scale, steps, and work resolution rather than silently
    reusing the old snapshot. Thus Failed and Canceled photoreal rows retain
    both the original-snapshot Retry and current-settings actions; none of these
    actions mutates the source row. The latter requires the atomic enqueue-next
    capability and requests `queuePlacement: "next"`.
  - A waiting `comfyui-flux2-photoreal` row exposes `現在設定へ更新`.
    This explicit action atomically replaces its resolved Positive and Negative
    prompts, LoRA enabled state, strength, steps, CFG, work resolution, and
    fixed or newly randomized Seed. It preserves job ID, queued status, waiting
    order, source identity, and unknown additive preset fields, and does not
    wake the worker. The update shares the companion's claim lock;
    once claimed/running it fails with conflict and changes nothing. Historical
    `a1111-photoreal` rows never expose this action because that adapter does not
    consume these saved settings. The action also requires the exact
    `capabilities.queuedPhotorealSettingsUpdateV1` health flag; an older companion
    without it keeps Jobs readable but does not expose a button that would 404.
    The Jobs header also exposes one bulk variant for all currently waiting,
    eligible rows. It resolves the current WPF settings and Positive separately
    for each source PNG, then invokes the same per-row replacement contract.
    Resolved Positive means the current nonblank (or empty-state fallback)
    Positive plus outputs from enabled WPF-local Prompt mappings matched
    against that source PNG's `parameters` metadata. Resolved Negative is the
    current WPF Negative; a metadata `Negative prompt` is intentionally not
    inherited.
    Source identity, status, and queue order do not change; ineligible and
    concurrently claimed rows are skipped or reported without rolling back
    successful updates to other rows.
  - Cancel never deletes the source, a managed output, or failure diagnostics.
    Cancel, Retry, re-run, Open output, and Delete output remain explicit user
    actions.
  WPF validates source identity, source signature, and managed-output ownership
  before opening or deleting an output. WPF never writes either Enhancement
  Jobs store directly and never starts a worker from ordinary browsing.
- `PV-ENHANCE-ENQUEUE-INBOX-001` defines durable registration of explicit
  create and Retry actions. WPF performs one bounded health probe. An exact v1
  capability publishes the request before any immediate bodyless wake. A timeout,
  transport error, retryable status, malformed/ambiguous health response, or
  companion without the v1 capability also publishes first and sends no
  ambiguous job POST. Only the exact v1 capability permits an immediate
  encrypted, bodyless inbox wake after publication; the job body is never
  resent over HTTP.
  Feature-gated I2I create actions use this unknown-probe fallback only after
  the open edit board has already observed the exact ready I2I capability.
  I2I Retry requires an exact ready health response for the Retry action and
  never publishes from an unknown capability state.
  - A publish writes one bounded envelope to a same-directory temporary file,
    flushes it through the storage stack, and moves it without overwrite into
    `enhance/enqueue-inbox/v1/pending`. Passive viewing and health reads never
    create this directory. A failed write or move is an explicit no-save error.
  - The API-only companion claims envelopes through `processing`, dispatches
    the fixed create or Retry route with the saved request ID as its idempotency
    key, and deletes the envelope only after a matching durable receipt. A lost
    response, timeout, 408, 425, 429, 5xx, job-store contention, restart, or
    WPF exit retains the reservation and retries it in FIFO order. Definitive
    4xx input failures move to `needs-action` without blocking later valid
    items in the same batch.
  - After a successful publish, one valid request converges to at most one Jobs
    row even when the immediate response is lost or dispatch is repeated. The
    guarantee begins when the durable move succeeds; disk-full, access-denied,
    unsupported-state, or media failure must remain visible as a local save
    failure rather than a false queued state.
  - The logical version-1 Jobs store may contain the additive optional receipt
    ledger. In legacy JSON this is the root array `idempotencyReceipts`; SQLite
    stores the same logical records transactionally. A receipt contains only request ID,
    SHA-256 request fingerprint, original job ID, and original creation time;
    it never stores a source path, prompt, or request body. Terminal history
    dismissal creates the receipt under the same lock and atomic replacement
    before removing the visible row. A matching replay after dismissal returns
    the original job ID without creating another row; a different fingerprint
    for the same request ID conflicts.
  - Receipts have no TTL or eviction and are bounded at 8192 entries. If a new
    receipt is required at capacity, dismissal returns conflict and preserves
    the visible row. Legacy version-1 stores without the array remain valid.
    Unknown root and receipt fields round-trip unchanged; malformed, duplicate,
    or conflicting receipt state fails closed without replacing the store.
  - Rollout installs and starts the inbox-capable companion before installing
    this WPF writer. Rollback restores the prior WPF and companion as a pair;
    already-published envelopes remain intact for a later inbox-capable
    companion and are never converted to an unsafe legacy POST.
  - The versioned wire shape, bounds, fixed routes, hash, capability, and
    synthetic vectors are canonical in
    `contracts/enhancement-enqueue-inbox-v1.json`.
- A completed photoreal PNG stores its effective Positive, Negative, numeric
  settings, seed, model, and LoRA state in its own A1111-compatible
  `parameters` text chunk. No per-image JSON sidecar is created. For existing
  photoreal PNGs made before that writer, WPF may recover the connected final
  Positive, Negative, steps, CFG, sampler, scheduler, seed, generation size,
  model, and LoRA state from a bounded ComfyUI `prompt` graph only when no
  `parameters` chunk exists. The first `parameters` chunk remains authoritative
  even when empty or unsupported; a graph never overrides it. The modal
  metadata sidebar and its Copy actions resolve only the currently displayed
  version: Original reads the original PNG, photoreal reads that output PNG,
  and missing metadata is shown as unavailable rather than replaced with
  current defaults. A displayed video reads its exact stored `video` snapshot
  from the active Enhancement Jobs store, including native/output FPS and frame counts,
  model, steps, CFG, sampler, scheduler, shift, denoise, seed, codec, and RIFE
  delivery. Missing legacy fields remain visibly unknown.
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
- Replacing `next start` as WPF's launch target is not merge-ready until a named
  H25 API-companion commit passes an isolated TEMP compatibility test against
  the exact WPF candidate. That test must prove the existing URL contracts,
  durable inbox recovery and idempotency, one absolute Enhancement root for
  the active Jobs store, the inbox, and `outputs/**`, WPF output ownership checks,
  unchanged source bytes, and zero writes to unrelated user-owned state or
  caches. The independently maintained Browser product remains present and
  unchanged; it is only excluded from the WPF companion launch path.

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
- For historical Wan rows, quality is an exact preset choice, not a free-form
  step field. Normal is `wan22-ti2v-5b-normal-v1` at 20 steps and High is the
  explicit `wan22-ti2v-5b-high-v1` preset at 40 steps. High keeps the same
  FP16 model, pixel budget, native frame count, RIFE delivery, one-worker
  queue, and exclusive GPU lease. A known preset paired with the wrong
  `effective.steps` value is protected as reader-only instead of being
  coerced.
- A named video Style is WPF-local and snapshots the prompt, model, quality
  preset, duration, generation FPS, and maximum pixel budget. Up to 32 Styles
  with names of at most 40 characters are persisted in WPF `state.json`.
  Selecting one from the video board or application settings applies its
  retained prompt and request values, while any legacy Wan/Hunyuan model id is
  migrated to H3 for new generation. A later manual edit returns to the unsaved
  Custom selection without modifying the stored Style. Saving the same name
  replaces that Style, and deleting one leaves the current request values
  unchanged. Jobs still snapshot the effective values only when explicitly
  enqueued.
- The current delivery stage uses RIFE 4.25 to publish exactly 30 fps and
  duration-times-30 frames: 120 frames for 4 seconds or 180 for 6 seconds.
  Final H.264 output is `yuv420p` and contains no audio. Managed-video labels
  and playback metadata use those delivery values when the field is valid;
  legacy rows continue to show their native values. Historical Wan presentation
  labels 12/16 fps as generation FPS and separately identifies the final 30 fps
  RIFE 4.25 output; those controls are not part of the new H3 job surface.
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
- Final video outputs use `Videos/YYYY-MM-DD/` below the configured
  Enhancement output parent, with the date derived only from the final file's
  Windows CreationTime under the common managed-output rule above. The
  filename includes job, source, and preset identities. Existing valid flat
  `Videos/` references remain readable during migration. A core ComfyUI
  staging file is allowed only as an exact adapter-owned transient and must be
  removed after success, cancel, or failure; the final residue audit is zero.
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

### `PV-ENHANCE-VIDEO-002` — MiniMax H3 and prompt conversion

`contracts/enhancement-video-v2.json` is the canonical additive reader and
writer base contract for MiniMax H3. The additive
`contracts/enhancement-video-h3-profiles-v1.json` contract defines the four
measured high-quality duration profiles: 124, 243, 294, or 362 native frames
at 24 fps, delivering 5.167, 10.125, 12.250, or 15.083 seconds. Every profile
retains the source-aspect canvas capped at 414,720 pixels, H.264,
and the contract-defined AAC audio path. Legacy prompt-only v2 rows remain the
124-frame, 20 STEP profile. New requests pin `requested.profileId`. The additive
`contracts/enhancement-video-h3-steps-v1.json` contract allows an integer from
1 through 40 in `requested.steps`; arbitrary duration, frame, FPS, resolution, or invalid STEP
values are rejected. Unknown or inconsistent
snapshots remain protected rather than being coerced to the Wan v1 shape.

- MiniMax H3 is the only model exposed by the new-video UI and the persisted
  default. Wan and Hunyuan model identities remain supported by historical job
  readers, managed-output playback, deletion guards, and Favorite presentation,
  but cannot be selected for a new job. A persisted Wan/Hunyuan new-job choice
  or saved Style is normalized to H3 while retaining its name and prompt; there
  is no silent fallback from an unavailable H3 writer to Wan.

- The visible H3 control exposes only the measured 5 / 10 second profiles and
  a synchronized integer slider/input from 1 through 40 STEP. FPS remains fixed
  at 24. Changing STEP changes only the scheduler iteration count and is presented without an
  unmeasured quality guarantee. The measured 12 / 15 second profiles remain valid
  durable-reader and backend contracts for existing jobs, but are not offered
  for new selection. The 10-second option carries an idle-system warning
  because the RTX 4070 SUPER / 32 GiB measurements used most physical RAM.
  The retained 12.250-second 512x768 measurement completed in 802.452 seconds
  with 11,332 MiB peak VRAM, 1.78 GiB minimum free physical RAM, and 81 C peak
  GPU temperature.

- The video input prompt remains the only authoritative prompt. The explicit
  MiniMax conversion action reads that text and the currently selected source
  image, then asks the local Qwen 4B companion route for an editable H3-English
  candidate. It calls only an already-running loopback compiler and never starts
  the durable companion or a worker. If that compiler is unavailable, the
  action fails closed. It does not enqueue, reorder, wake, pause, or otherwise
  mutate an AI Job or image queue. The sealed llama.cpp compiler is CPU-only:
  `CUDA_VISIBLE_DEVICES=-1`, `--device none`, `--n-gpu-layers 0`, all model/KV/
  projector offload paths disabled, one inference slot, and at most eight CPU
  threads. It therefore does not contend for or acquire the product GPU lease.
- Conversion offers three UI modes: `polish` preserves the written intent,
  `direction` strengthens image-compatible motion and camera direction, and
  `auto` lets the source image guide that direction. Mode guidance stays
  inside the bounded rewrite request; the companion protocol remains the
  exact v1 prompt-rewrite shape. If input plus mode guidance would exceed the
  2,000-character request bound, conversion is rejected explicitly instead of
  silently dropping the selected mode.
- Conversion receives the selected exact H3 frame count. The local Qwen
  planner keeps two contiguous beats for the 5.167-second profile and may use
  three contiguous beats for 10.125, 12.250, and 15.083 seconds, so long clips
  are not produced merely by stretching a five-second two-beat plan.
- A returned candidate is transient and separate from the input. The user may
  edit it, convert again, apply it explicitly to the input, or undo the most
  recent apply. Changing the input, image, model, style, or conversion mode
  makes the old candidate stale. A response already being computed when any of
  those inputs changes is canceled when possible and rejected again against the
  captured context before adoption, even if the compiler returns late. Retiring
  a request also retires its generation immediately: an older completion may
  change neither candidate, status text, pending state, nor Apply/Undo state
  after a newer request begins.
  Conversion never starts video generation.
- Only the existing explicit video-generation action may enqueue the final H3
  request. It uses the same durable ordered AI Jobs queue as other managed
  operations; prompt conversion itself remains outside that queue.

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

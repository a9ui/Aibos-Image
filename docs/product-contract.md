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
  and state hydration never start it. WPF may stop only the exact companion
  process tree that the current WPF process created.
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
recent-folders.json, plus enhance/jobs.json and enhance/outputs/**. It is not a
repository root. It may therefore point at an existing legacy .cache directory
without copying or rewriting any durable data.

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

## Enhancement

- Enhancement begins only from an explicit user action.
- Ordinary browsing, preview, search, modal navigation, and state hydration do
  not enqueue jobs or start workers.
- Original and managed Enhanced outputs remain distinct; source images are not
  overwritten.
- Enhancement operation envelope v1 is an additive extension of the existing
  version 1 job store. `operation` is `upscale` or `photoreal`; a missing value
  on an older job means `upscale`.
- The modal exposes separate explicit `AI高画質化` and `AI実写化` actions.
  Photoreal prompt, strength, structure retention, quality steps, and work
  resolution are WPF-local request defaults and do not mutate shared Browser
  settings. The prompt starts with the built-in tested default, remains freely
  editable, persists locally, and has an explicit Reset action.
- New photoreal requests use the companion adapter identifier
  `comfyui-flux2-photoreal`; older `a1111-photoreal` jobs remain readable as
  managed historical versions.
- Each valid succeeded output remains an independently selectable version.
  In the modal, `Ctrl+Up` and `Ctrl+Down` cycle Original and every available
  AI高画質化/AI実写化 version with wraparound. Delete removes only the selected
  managed version and never the source or sibling versions.
- Both operations use the same companion `/api/enhance/jobs` endpoint, durable
  FIFO queue, and single worker. They must not create separate GPU queues or
  run GPU work in parallel. Retry and Cancel retain the job operation.
- The gallery exposes independent `AI高画質化済みのみ` and `AI実写化済みのみ`
  filters. Enabling both uses intersection semantics. Cyan `HQ` and violet
  `REAL` thumbnail markers may appear together when both completed operation
  types exist for one source.
- Grid and list right-click menus expose explicit `AI高画質化` and `AI実写化`
  actions for the clicked real source image. Opening a context menu remains
  passive; only choosing either action may start the companion and enqueue
  work.
- New managed outputs are flat within the companion-owned
  `enhance/outputs/高画質化/` and `enhance/outputs/実写化/` operation folders.
  Existing recorded nested output paths remain valid and are not migrated
  automatically.
- The H25 Browser companion owns the current local Enhancement API and worker.
  WPF owns its loopback client and must keep the API optional.
- Modal and batch Start/Retry first reuse an already-ready loopback companion.
  If none is ready, that same explicit action may launch the separately
  installed H25 companion with Browser opening and ComfyUI autostart disabled.
  Closing WPF stops only a launcher process tree that WPF itself created.
- The WPF Enhancement Jobs workspace is a virtualized client view over that
  API. Opening it performs a passive jobs read only. It polls once per second
  only while the workspace is visible and at least one job is queued or
  running, and stops polling when hidden or when all jobs are terminal.
- Cancel, Retry, Open output, and Delete output remain explicit user actions.
  WPF validates source identity, source signature, and managed-output ownership
  before opening or deleting an output. The workspace does not change the
  `enhance/jobs.json` schema and never starts a worker from ordinary browsing.
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

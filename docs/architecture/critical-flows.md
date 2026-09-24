# WPF critical flows

These flows show orchestration and stop conditions. Stable semantics remain in
[`product-contract.md`](../product-contract.md); exact messages and fields
remain in the selected contract from [`contracts/index.json`](../../contracts/index.json).

## 1. Ordinary launch and passive viewing

```text
App startup
  -> validate/pin existing roots and local process identity
  -> create MainWindow
  -> read local/shared viewer state
  -> scan/project catalog and render UI
  -> stop
```

The passive branch does not start Node.js, the Companion, a worker, a model, or
GPU work. It does not recover, wake, claim, retry, or enqueue Enhancement work.
Entry points: `App.xaml.cs`, `MainWindow.xaml.cs`, `SharedDataRoot*.cs`.
Tests: public-surface, launch-target, shared-root, scan, catalog, and AI
processing-minimize verifiers.

## 2. Explicit Enhancement enqueue

```text
explicit UI action
  -> capture and validate current source/settings
  -> prove or start the exact authenticated Companion when required
  -> capture immutable wire items and the destination
  -> serialize, wait for the shared lock and commit off the UI thread
     (revalidate context and install source-pin overlays on UI before commit)
  -> acknowledge the saved Inbox envelope and durable-work lifetime
  -> send an authenticated bodyless wake
  -> observe delivery and Jobs through read-only projections
```

Publishing the envelope precedes wake. A transport failure after publication
does not discard the durable intent. Opening an editor, changing selection,
health, hydration, or Jobs display cannot enter this flow. Entry points:
feature partial -> `MainWindow.EnhancementCompanion.cs` ->
`EnhancementEnqueueInboxStore.cs`. Tests: durable-enqueue, selected-batch,
Companion lifetime/auth, and feature-specific start verifiers.

Within one operation, capability validation reuses the authenticated health
response obtained during API preparation. It is not a cache across actions or
process epochs; Stop/Restart invalidation and request authentication still
apply before publication and delivery.

## 3. Passive Jobs display

```text
open or refresh Jobs
  -> read one bounded validated SQLite snapshot
  -> optionally read authenticated health only from an already-running owner
  -> classify rows and revisions
  -> update the presentation window
  -> stop
```

SQLite and the Companion remain the authorities; WPF owns only the projection.
Unavailable health leaves a read-only local snapshot and does not start a
replacement process. Entry point: `MainWindow.EnhancementJobs.cs`. Tests:
offline, SQLite reader/status/count, workspace, operation-filter, paging, and
scroll-performance verifiers.

## 4. Explicit queue control or Job mutation

```text
explicit Connect/Resume/Cancel/Retry/Reorder action
  -> validate current row/capability identity
  -> prove the Companion and authenticate the request
  -> send the exact versioned mutation
  -> reread authoritative state
```

Unknown, malformed, stale, or future rows remain visible but reader-only. No
local optimistic state becomes durable authority. Entry points:
`MainWindow.EnhancementJobs.cs` and `MainWindow.EnhancementCompanion.cs`.
Tests: queue/order, mutation-safety, recovery/connect, retry, and cancellation
verifiers.

Connect establishes the authenticated API and reads status without recovering
or starting the queue. An explicit Resume can proceed after authenticated
identity even when queue health is unavailable: it sends one queue mutation
and lets the Companion own recovery, reservation intake, and unpausing. WPF
does not send a preparatory recovery mutation or infer success from health.
The confirmed mutation response determines whether Resume succeeded.

## 5. Managed output open, reuse, and deletion

```text
Jobs/catalog output reference
  -> validate operation and managed-root ownership
  -> validate current file identity and dependency guards
  -> open/reuse, or send an explicit protected deletion request
```

WPF never substitutes the source path for managed ownership and never repairs
state by deleting media. Source deletion is a separate explicit Recycle Bin
flow. Entry points: `MainWindow.EnhancementJobs.cs`, `MainWindow.Video.cs`, and
feature readers. Tests: managed-output, dependency, delete-correctness, and
source-preservation verifiers.

## 6. Versioned video and operation readers

```text
validated Jobs snapshot
  -> select the exact protocol version and adapter identity
  -> parse with the matching pure reader
  -> project supported state, or keep the row reader-only
```

Missing, duplicate, malformed, unknown, and future members do not gain a
fallback execution meaning. Entry points: matching `MainWindow.*Reader.cs` or
`*Contract.cs`. Tests: the matching contract/reader verifier and smoke runner.

## 7. Local settings and Style save

Local persistence returns its commit result to the caller before the UI reports
success or begins shutdown. A failed Style save retains the current edits and
blocks collection reload from another Style editor. Closing retries only
pending Style writes alongside viewer settings; a failed save leaves the
window usable unless the user explicitly chooses to discard unsaved changes.
Latest-file conflict checks and compatible unknown fields remain with each
existing store owner. Tests: shutdown-state and style-state-forward-compat.

## Flow-change rule

A patch that changes an arrow above is not documentation cleanup. It requires
the affected contract or invariant review, a focused baseline, and a separate
implementation patch.

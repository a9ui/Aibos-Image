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
  -> publish the bounded durable Inbox envelope
  -> send an authenticated bodyless wake
  -> observe delivery and Jobs through read-only projections
```

Publishing the envelope precedes wake. A transport failure after publication
does not discard the durable intent. Opening an editor, changing selection,
health, hydration, or Jobs display cannot enter this flow. Entry points:
feature partial -> `MainWindow.EnhancementCompanion.cs` ->
`EnhancementEnqueueInboxStore.cs`. Tests: durable-enqueue, selected-batch,
Companion lifetime/auth, and feature-specific start verifiers.

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

## Flow-change rule

A patch that changes an arrow above is not documentation cleanup. It requires
the affected contract or invariant review, a focused baseline, and a separate
implementation patch.

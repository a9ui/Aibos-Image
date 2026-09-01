# WPF project map

This map identifies current responsibility boundaries in the native WPF
application. It describes the present code; it does not grant a module new
authority and does not change product behavior.

## Dependency direction

```text
App startup
  -> MainWindow UI and feature partials
       -> focused stores, readers, planners, and platform helpers
       -> authenticated Enhancement bridge for explicit actions only
            -> public versioned contracts at the Companion boundary

Focused verifiers -> the exact implementation and contract seam they exercise
```

Passive UI and readers do not call the explicit-action branch. Private
Companion internals remain outside this public map.

## Module ownership

| Area | Owns and may call | Must not own | Main entry points and test seams |
|---|---|---|---|
| Process startup and command dispatch | WPF application startup, single-instance coordination, source-root activation, and explicit smoke/contract command dispatch. It may create the main window and call isolated runners. | Enhancement queue recovery, worker startup, or GPU/model lifecycle during ordinary launch. | [`App.xaml.cs`](../../local-native/PhotoViewer.Wpf/App.xaml.cs), [`SingleInstanceCoordinator.cs`](../../local-native/PhotoViewer.Wpf/SingleInstanceCoordinator.cs), `App.*Smoke.cs`, [`verify-public-surface.ps1`](../../scripts/verify-public-surface.ps1) |
| Viewer shell, catalog, and modal presentation | Window state, catalog materialization, selection, search/filter/sort projection, thumbnails, preview tabs, modal navigation, and renderer-local interaction state. It may call focused stores and helpers. | Companion durable Jobs authority or source-media rewrites during viewing. | [`MainWindow.xaml`](../../local-native/PhotoViewer.Wpf/MainWindow.xaml), [`MainWindow.xaml.cs`](../../local-native/PhotoViewer.Wpf/MainWindow.xaml.cs), [`MainWindow.SearchTerms.cs`](../../local-native/PhotoViewer.Wpf/MainWindow.SearchTerms.cs), catalog/modal verifiers under [`scripts/`](../../scripts/) |
| Shared-root and viewer persistence | Validated shared-root discovery and leases; bounded readers/writers for Albums, Search History, Favorites activity, Seen/settings/recent state, and local presentation files. | Silent creation during passive startup, destructive repair, or renderer-local fields in shared documents. | `SharedDataRoot*.cs`, [`SharedJsonDocumentReader.cs`](../../local-native/PhotoViewer.Wpf/SharedJsonDocumentReader.cs), [`SharedStoreWriter.cs`](../../local-native/PhotoViewer.Wpf/SharedStoreWriter.cs), [`AlbumStore.cs`](../../local-native/PhotoViewer.Wpf/AlbumStore.cs), [`SearchHistoryStore.cs`](../../local-native/PhotoViewer.Wpf/SearchHistoryStore.cs), shared-state verifiers |
| Enhancement process and enqueue bridge | Exact Companion root/process/authentication proof, protected request construction, process lifetime, durable Inbox publication, and bodyless wake after an explicit action. It may call public protocol helpers and platform process APIs. | Jobs database ownership, queue scheduling, runtime adapter decisions, or passive process startup. | [`MainWindow.EnhancementCompanion.cs`](../../local-native/PhotoViewer.Wpf/MainWindow.EnhancementCompanion.cs), [`EnhancementCompanionAuthStoragePath.cs`](../../local-native/PhotoViewer.Wpf/EnhancementCompanionAuthStoragePath.cs), [`EnhancementEnqueueInboxStore.cs`](../../local-native/PhotoViewer.Wpf/EnhancementEnqueueInboxStore.cs), Companion/inbox verifiers |
| Jobs reader and UI projection | Bounded SQLite reads, health/catalog-revision observation, row classification, presentation-window state, mutation eligibility display, Jobs navigation, and notifications. It may send a mutation only from the matching explicit UI action through the bridge. | A second Jobs writer, passive wake/recovery, or normalization of unknown/future rows. | [`MainWindow.EnhancementJobs.cs`](../../local-native/PhotoViewer.Wpf/MainWindow.EnhancementJobs.cs), [`MainWindow.EnhancementNotifications.cs`](../../local-native/PhotoViewer.Wpf/MainWindow.EnhancementNotifications.cs), `App.Enhancement*Smoke.cs`, `verify-wpf-enhancement-*.ps1` |
| Explicit operation surfaces | User-controlled request capture and presentation for Batch, Photoreal, I2I, Video, Video Tools, Video Trim, Video Edit, Video Finish, and Motion Director. They may validate current UI/source state and publish through the bridge. | Automatic work from selection, hydration, health, Jobs reads, or opening an editor. | Matching `MainWindow.<Feature>.cs` partial, matching `App.<Feature>Smoke.cs`, and matching focused verifier |
| Protocol readers and pure planners | Exact snapshot parsing, mutation-safety classification, frame selection, prompt conformance, and operation planning. | Durable writes or fallback reinterpretation of malformed and future state. | `*Contract.cs`, `*Reader.cs`, [`ExactVideoFrameSelection.cs`](../../local-native/PhotoViewer.Wpf/ExactVideoFrameSelection.cs), [`MotionDirectorPlan.cs`](../../local-native/PhotoViewer.Wpf/MotionDirectorPlan.cs), contract and reader verifiers |

## Refactor candidate register

The candidates below are evidence for a later implementation milestone. This
Atlas patch performs none of them.

| Order | Candidate | Desired owner | Main risk | Existing test seam | Status |
|---|---|---|---|---|---|
| 1 | Separate the read-only Jobs snapshot parser and projection from window event/state handling in `MainWindow.EnhancementJobs.cs`. | A focused immutable reader/projection component consumed by `MainWindow`. | Accidentally turning a passive read into a mutation or changing future-row handling. | Enhancement Jobs offline, SQLite reader/status/count, workspace, operation-filter, paging, and lifecycle smoke verifiers. | Highest-ranked public candidate, but deferred. This public Atlas does not authorize a refactor. |
| 2 | Separate catalog/filter/selection coordination from `MainWindow.xaml.cs`. | One catalog session/coordinator with UI-thread projection kept at the window edge. | Ordering, cancellation, thumbnail continuity, and selection-anchor regressions. | Catalog, scan, search, thumbnail, gallery-order, and rapid-state verifiers. | Deferred. |
| 3 | Separate smoke/contract command dispatch from ordinary `App.xaml.cs` startup. | A command registry used only for explicit test/tool arguments. | Running a test path during ordinary launch or changing exit behavior. | Existing `App.*Smoke.cs` entry points and public-surface verifier. | Deferred. |
| 4 | Group operation-board lifecycle state by feature instead of sharing broad window state. | Feature-scoped presentation models with explicit capture/reset boundaries. | Cross-board source identity and modal cleanup regressions. | Feature smoke runners and focused operation verifiers. | Deferred. |
| 5 | Consolidate repeated video snapshot parsing behind existing exact reader contracts. | Small pure readers per protocol version. | Silently accepting duplicate, malformed, or future members. | Video v2, Video Tools v2, Video Trim, Edit, Finish, and H3 reader verifiers. | Deferred. |

## M1 stop line

This documentation milestone does not extract classes, add interfaces, rename
compatibility identifiers, remove code, change contracts, alter state
machines, or change tests. Any selected refactor starts as a separate M2 patch
with its focused baseline recorded first.

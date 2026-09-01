# WPF state ownership

Cross-feature state has one durable owner. WPF may own a projection without
owning the durable data behind it. Exact meanings and shapes remain in
[`product-contract.md`](../product-contract.md) and
[`contracts/index.json`](../../contracts/index.json).

| State | Durable or lifecycle owner | WPF writers | WPF readers and projections | Authority and verification seam |
|---|---|---|---|---|
| Shared-root identity | One validated root pinned for the process lifetime by `SharedDataRootLocator` and its lease. | Explicit setup/activation tools only; ordinary startup is reader-only. | All shared stores resolve paths from the pinned root. | `PV-ROOT-001`; shared-root locator/setup/lease verifiers. |
| Favorites, Seen, settings, Albums, Search History, and recent folders | The latest supported on-disk document under the shared root. | Focused store/writer classes mutate the latest revision under the required lease and preserve compatible unknown fields. | `MainWindow` and library/search projections. | Matching `PV-SET`, `PV-REC`, `PV-SH`, and `PV-ALB` entry in the contract index; shared-state verifiers. |
| Renderer-local presentation | WPF local persistence for window geometry, panel layout, current selection, shortcuts, Styles, and other presentation-only values. | `MainWindow.LocalPersistence.cs` and focused local store paths. | WPF presentation only. | Durable-state boundary in the product contract; local persistence and shutdown-state tests. |
| Catalog, selection, preview tabs, and modal session | The live `MainWindow` session. This is not shared durable protocol state. | Scan/drop/navigation/filter/selection UI paths. | Gallery, preview, operation boards, and modal presentation. | `MainWindow.xaml.cs` plus focused catalog, scan, drop, modal, and gallery verifiers. |
| Companion capability and process ownership | WPF owns only the exact child it authenticated and started; ownership may be released after a protected mutation can activate durable work. | `MainWindow.EnhancementCompanion.cs` and auth storage helpers. | Explicit action flows; passive readers may use an already-running proven Companion without starting one. | `enhancement-companion-auth-v2`; Companion lifetime, path-boundary, auth, and secure-request smoke tests. |
| Durable enqueue intent | The committed Inbox envelope is the handoff record between WPF and the Companion. | `EnhancementEnqueueInboxStore` publishes a bounded envelope after explicit validation. | Delivery/adoption UI and dependency guards read bounded state; Jobs is not the envelope owner. | `PV-ENHANCE-ENQUEUE-INBOX-001`; durable-enqueue and selected-batch verifiers. |
| Enhancement Jobs rows and revisions | Companion-owned `enhance/jobs.sqlite3`; legacy JSON is compatibility input only. | WPF has no direct Jobs writer. Explicit mutations use the authenticated Companion protocol. | `MainWindow.EnhancementJobs.cs` renders a bounded, validated SQLite/API projection. | `PV-ENHANCE-JOBS-SQLITE-001`; offline, SQLite reader/status/count, workspace, and paging verifiers. |
| Queue order, pause, claim, retry, and cancellation | Companion queue/store implementation under the shared protocol locks and idempotency rules. | Only explicit authenticated actions; WPF never edits queue files or Jobs rows directly. | Jobs queue projection and action eligibility. | `PV-ENHANCE-QUEUE-001`; queue/order/mutation focused verifiers. |
| Managed Enhancement outputs | Companion publication owns final placement below the validated output root. | WPF does not create or replace managed output bytes; explicit deletion goes through the protected owner path. | Gallery/Jobs/modal validate managed-root ownership before open, Favorite, reuse, or deletion. | `PV-ENHANCE-OUTPUT-001` and operation contracts; output, dependency, and reader verifiers. |
| Operation and video snapshots | The durable Job row written for the explicit request. | WPF captures the exact supported request envelope but does not rewrite an existing snapshot during passive display. | Exact versioned readers and UI projections. | Matching I2I/Video/Tools/Trim/H3 contract and focused reader verifier. |

## Change checklist

Before moving state or adding a writer:

1. Identify the single durable owner and every active reader.
2. Select the exact contract entry; do not infer meaning from one reader.
3. Preserve unknown compatible fields and fail without writing on malformed or
   unsupported future state.
4. Prove passive paths stay read-only.
5. Add simultaneous-reader/writer verification when more than one active
   process consumes the state.

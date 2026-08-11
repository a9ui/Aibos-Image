# PR 60 Pro follow-up packet

This packet covers the follow-up commit that contains it on
`codex/aibos-h3-i2v-product`. The first Pro review examined `c602f62` and
returned `MERGEABLE_AFTER_FOCUSED_FIXES` with no P0 finding.

## Product decision

- MiniMax H3 is the only model on the new-video surface. Historical Wan and
  Hunyuan jobs remain readable and their managed outputs remain playable,
  deletable under the existing ownership guards, and Favorite-aware.
- The prompt compiler is a small editor action: it sends the current input and
  selected image to the already-running local Qwen route, returns an editable
  MiniMax-English candidate, and does not start a companion, worker, or queue.
- The enlarged viewer now exposes one passive `AI編集` picker instead of two
  competing `髪色編集` / `AI編集+` toolbar actions. The picker keeps the focused
  hair-color v1 and multi-target v2 protocols separate internally and opens
  neither a companion nor a queue by itself.
- `次に動画化` remains deferred: the current companion contract explicitly
  rejects image-only `queuePlacement` for video jobs, so WPF does not present a
  control that could silently append or misrepresent queue placement.
- The paired private companion implementation `PRIVATE-H25-CREATION-A` now
  applies one output rule to Upscaled, Photorealized, Edited, and Videos: the
  final folder date comes only from the output file's Windows CreationTime.
  Its local migration receipt proves unchanged file/byte totals and zero
  remaining moves. The public candidate carries only the cross-repository
  contract and synthetic behavior; it does not import the private Node/Next.js
  implementation or lineage.
- Security and public-readiness scanning remain a separate review lane. This
  packet contains only synthetic and aggregate evidence.

## First-review adjudication

| Pro finding | Decision | Follow-up |
| --- | --- | --- |
| Prompt conversion could start the durable companion | ADOPT | Removed the explicit-action readiness/launcher path. Direct loopback compiler failure now leaves launch attempts, worker state, and queue mutations at zero. |
| Cold gallery could initially realize only one thumbnail | ADOPT | All missing visible candidates are priority work, and the duplicate-suppression signature now includes visible/realized bounds and candidate counts. |
| Video Favorite prose contradicted the implementation | ADOPT | The normative contract now defines exact MP4 path keys, purple max badge, Lv0 semantics, filter union/intersection, and missing-output retention. |
| Wan remained selectable for new work | ADOPT | New-video selectors and Wan tuning panels are hidden, H3 is the only option/default, legacy persisted choices and Styles normalize to H3, and unavailable H3 never falls back to Wan. |
| One-second Jobs polling repeatedly loaded the full inventory | ADOPT | Polling fetches compact health first and refreshes the full inventory only when counts, current job, last claim, or last terminal time changes, or health is unavailable. Current progress updates in place. The v1 health contract still lacks a general revision for same-cardinality queued-only changes made by another client; explicit Refresh remains the bounded fallback. |
| Direction/Auto could silently lose mode guidance near 2,000 characters | ADOPT | The editor rejects the conversion explicitly before transport when input plus guidance exceeds the bound. |
| Public CI expected an ignored local Photoreal prompt policy | ADOPT | The smoke now forces the bounded public fallback and no longer embeds private-policy wording in tracked source. |
| Aggregate video smoke still exercised the retired Wan writer | ADOPT | Historical Wan read/playback fixtures remain, while its new-job assertions now require the exact H3 preset/backend/prompt-only payload, omit legacy seed state, reject unavailable H3 without a POST, and preserve source-error feedback across health refresh. |
| Modal zoom retention and bounded HQ decode looked sound | KEEP | Existing focused zoom, modal-interaction, and decode-bound evidence remains green. |

## Focused verification

- Release build: zero warnings, zero errors on .NET SDK 10.0.302.
- H3 prompt rewrite: exact v1 route/fixture, editable candidate, Apply/Undo,
  stale-result rejection including deliberately delayed responses after input,
  Style, Model, and source-image changes, response byte bound, mode-overflow
  rejection, unavailable-compiler fail-closed, companion launch attempts `0`, starter
  calls `0`, Jobs POST `0`, all other queue mutation routes `0`. The sealed
  llama.cpp route is explicitly CPU-only (`CUDA_VISIBLE_DEVICES=-1`, device
  `none`, zero GPU layers/offload, one slot) and never creates the GPU lease.
  A separate overlapping-request smoke holds request A, changes the prompt,
  completes request B, and only then releases A; B remains the exact candidate
  and status, A is rejected, and A cannot clear B's pending state.
- Video v2 UI: H3-only default surface, unavailable H3 disabled, exact health
  gate, legacy Wan persisted selection migrated to H3, no fallback.
- Video Favorite: max-level purple badge, Lv0 and union/intersection behavior,
  exact-output modal edit, rollback/retry, persistence, missing-key retention,
  normal and high-contrast visual tokens.
- Thumbnail continuity: transient retry, latest-generation wins, four-worker
  decode bound, resident bound, progressive layout, and full final visible
  coverage all passed.
- Jobs workspace: a health-only poll increased health GET count without
  increasing full-inventory GET count; changing the terminal timestamp with
  identical counts forced one full refresh, closing the same-count terminal/
  enqueue race. Changing the companion process/start/build identity also forced
  one full refresh. Queue ordering and all existing mutation guards remained green.
- Photoreal modal and shared queue: the exact GitHub Actions failure was
  reproduced locally, traced to a non-deterministic private-policy expectation,
  isolated to the public fallback, and then passed in full.
- Aggregate enhancement/video flow: historical Wan managed-output
  read/filter/playback/delete compatibility, H3-only new enqueue, exact H3
  prompt-only payload, ignored legacy seed state, unavailable-writer
  fail-closed behavior, Style migration/persistence, input-error retention,
  reload, and unchanged Jobs store all passed together.
- Existing zoom-anchor, modal-interaction, and bounded-decode smokes remain
  green. The separate hidden-window modal-pan runner has an unresolved cadence
  hang; its exact owned process was stopped, and the product-level modal
  interaction smoke passed. Treat this as a verifier gap, not proof of a UI
  failure.
- Unified AI edit entry: the modal toolbar has one picker with exactly the two
  supported editors, the former second toolbar button stays hidden, the modal
  context menu uses the same grouping, and the external-image interaction
  smoke remained passive with companion calls `0` and all stores unchanged.
  A delayed hair-board health response was retired by opening the multi-target
  board; opening hair again retired multi-target. At every boundary exactly one
  board was visible, the three board opens made three health GETs, and mutation
  calls remained `0`.
- CreationTime output layout: the paired private companion migration planned
  and moved 2,269 existing artifacts, then remapped 1,367 output paths and two
  managed-source references in one locked store write. The before/after audit
  remained 7,640 files and 43,546,828,337 bytes, with zero ambiguous dates and
  zero remaining moves. Three durable rows with no real output file stayed
  reported as `missing-output`; no timestamp was guessed for them. Focused
  companion tests passed 26/26, the companion TypeScript build passed, and
  scoped ESLint passed. The broader legacy typecheck remains contaminated by
  unrelated generated experiment source outside this change.

## Second Pro adjudication

- Compiler latest-wins and CPU-only ownership are design-closed on the supplied
  local smoke evidence. Exact-code approval still waits for the pushed SHA.
- Pro withdrew the proposed 15–30 second full reconciliation after the measured
  24 MiB / roughly 18 second inventory cost. The known v1 limitation is accepted
  for this PR: external same-cardinality queued-only changes remain stale until
  explicit Refresh or reopen. WPF-owned mutations still reconcile immediately;
  a real inventory revision remains a separate backend/SQLite PR.
- The picker has two routes, not five direct target choices. Therefore target
  propagation from picker to `expression`/`background`/`pose` is not applicable;
  those choices remain inside the v2 board. The applicable P1 gate was route
  serialization, which is now implemented and covered above.
- Pro's final local-design verdict closes both the two-route picker conflict and
  Jobs restart invalidation as P1s. External same-cardinality changes remain the
  documented P2 limitation. No further product feature or infrastructure work is
  requested in PR #60 before exact-SHA review; the candidate is feature-frozen.

## Read-only performance evidence

The measurements deliberately separated the desktop control plane, WPF,
loopback companion, and GPU runtime.

| Component | Observed behavior |
| --- | --- |
| Desktop control plane | Roughly 4.2 GiB aggregate working set across the Codex/ChatGPT/browser group during review activity. |
| Aibos landing | Roughly 175–192 MiB working set. |
| Aibos large catalog | Roughly 1.1–1.25 GiB after a six-figure catalog loaded. |
| Jobs before health-first polling | About 2.08 GiB peak WPF working set and 60.8 CPU-seconds during a 34.8-second mixed catalog/Jobs sample. |
| Jobs after health-first polling | About 1.52 GiB peak and 30.77 CPU-seconds during a comparable 35.1-second mixed sample. This is directional rather than a controlled benchmark because catalog work overlapped both samples. |
| Full Jobs vs health | The live full-inventory response was about 24 MiB and took about 18 seconds; compact health was about 3.8 KiB. Local full-store JSON parsing alone was about 1.58 seconds. |
| Loopback companion | Roughly 416 MiB aggregate working set and idle in the short CPU sample. |
| GPU runtime | Roughly 10.4–16.0 GiB aggregate working set depending on active inference; intentionally left running. |

Additional bounded probes separate individual viewer paths from the live GPU
worker:

| Path | Evidence |
| --- | --- |
| Favorites | The live store was 4,306,030 bytes. A read-only host baseline under active inference read the whole UTF-8 file in about 34 ms and materialized its JSON in about 488 ms. This is a PowerShell-host diagnostic, not a claim about the WPF parser. |
| Initial thumbnails | The live large-catalog check filled all 15 initially realized regions without a filter toggle. The synthetic continuity smoke ended with 399/399 visible items covered, four decode workers, and the 256-entry resident bound intact. |
| Zoom | The 243-image endpoint/anchor smoke passed 20 px through the forced one-column 600 px endpoint, kept anchor drift effectively zero across panel/resize/DPI changes, and bounded maximum realized containers at 214 of the 512 limit. |
| HQ image load | The oversized-aspect synthetic decode probe deferred preview decode by 13 ms, bounded modal decode at 1,960,000 pixels, grew working set by 13.2 MiB, kept the dispatcher responsive, preserved fidelity, and rejected an over-budget non-native header before decode. |
| Live CPU contention | During one 100,000-item interaction run, ComfyUI consumed about 77% of total 28-logical-CPU capacity, versus about 4.8% for the Codex/ChatGPT group and 1.7% for the companion. Search/filter p95 still passed at 232/147 ms, but sort p95 was 581.75 ms against 500 ms and the UI heartbeat gap was 76 ms against 50 ms. This contaminated run is retained as contention evidence, not presented as an acceptance pass; clean GitHub Actions remains the aggregate gate. |

No model, source media, generated output, queue entry, or durable product state
was deleted or published while gathering this evidence.

## Requested follow-up review

Re-check the follow-up commit for the four original merge gates and any
regression caused by the health-first Jobs poll or unified AI-edit picker. In
particular, verify that no normal UI or persisted-state path can enqueue Wan,
while historical Wan/Hunyuan reader and managed-output compatibility remains
intact.

## Publication status

- The normal publication command is
  `git push aibos codex/aibos-h3-i2v-product`.
- The current Codex host rejected that command before network execution with
  `approval required by policy, but AskForApproval is set to Never`. This is a
  host-policy gate, not a GitHub authentication failure. No alternate upload or
  history bypass was attempted; the local candidate remains ahead of the last
  published PR revision until an approved push path is available.

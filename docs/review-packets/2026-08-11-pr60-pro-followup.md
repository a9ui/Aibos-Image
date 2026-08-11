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
- Security and public-readiness scanning remain a separate review lane. This
  packet contains only synthetic and aggregate evidence.

## First-review adjudication

| Pro finding | Decision | Follow-up |
| --- | --- | --- |
| Prompt conversion could start the durable companion | ADOPT | Removed the explicit-action readiness/launcher path. Direct loopback compiler failure now leaves launch attempts, worker state, and queue mutations at zero. |
| Cold gallery could initially realize only one thumbnail | ADOPT | All missing visible candidates are priority work, and the duplicate-suppression signature now includes visible/realized bounds and candidate counts. |
| Video Favorite prose contradicted the implementation | ADOPT | The normative contract now defines exact MP4 path keys, purple max badge, Lv0 semantics, filter union/intersection, and missing-output retention. |
| Wan remained selectable for new work | ADOPT | New-video selectors and Wan tuning panels are hidden, H3 is the only option/default, legacy persisted choices and Styles normalize to H3, and unavailable H3 never falls back to Wan. |
| One-second Jobs polling repeatedly loaded the full inventory | ADOPT | Polling fetches compact health first and refreshes the full inventory only when its active-job/count signature changes or health is unavailable. |
| Direction/Auto could silently lose mode guidance near 2,000 characters | ADOPT | The editor rejects the conversion explicitly before transport when input plus guidance exceeds the bound. |
| Public CI expected an ignored local Photoreal prompt policy | ADOPT | The smoke now forces the bounded public fallback and no longer embeds private-policy wording in tracked source. |
| Modal zoom retention and bounded HQ decode looked sound | KEEP | Existing focused zoom, modal-interaction, and decode-bound evidence remains green. |

## Focused verification

- Release build: zero warnings, zero errors on .NET SDK 10.0.302.
- H3 prompt rewrite: exact v1 route/fixture, editable candidate, Apply/Undo,
  stale-result rejection, response byte bound, mode-overflow rejection,
  unavailable-compiler fail-closed, companion launch attempts `0`, starter
  calls `0`, Jobs POST `0`, all other queue mutation routes `0`.
- Video v2 UI: H3-only default surface, unavailable H3 disabled, exact health
  gate, legacy Wan persisted selection migrated to H3, no fallback.
- Video Favorite: max-level purple badge, Lv0 and union/intersection behavior,
  exact-output modal edit, rollback/retry, persistence, missing-key retention,
  normal and high-contrast visual tokens.
- Thumbnail continuity: transient retry, latest-generation wins, four-worker
  decode bound, resident bound, progressive layout, and full final visible
  coverage all passed.
- Jobs workspace: a health-only poll increased health GET count without
  increasing full-inventory GET count; queue ordering and all existing mutation
  guards remained green.
- Photoreal modal and shared queue: the exact GitHub Actions failure was
  reproduced locally, traced to a non-deterministic private-policy expectation,
  isolated to the public fallback, and then passed in full.
- Existing zoom-anchor, modal-interaction, and bounded-decode smokes remain
  green. The separate hidden-window modal-pan runner has an unresolved cadence
  hang; its exact owned process was stopped, and the product-level modal
  interaction smoke passed. Treat this as a verifier gap, not proof of a UI
  failure.

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

No model, source media, generated output, queue entry, or durable product state
was deleted or published while gathering this evidence.

## Requested follow-up review

Re-check the follow-up commit for the four original merge gates and any
regression caused by the health-first Jobs poll. In particular, verify that no
normal UI or persisted-state path can enqueue Wan, while historical Wan/Hunyuan
reader and managed-output compatibility remains intact.

# Aibos Image WPF design QA

## Evidence

- Source visual truth path: `private-evidence://aibos/workbench-split-reference.png`
  - SHA-256: `b47ee334b70174d6640f756574a74709c6401120710807cd7e1388c00483db12`
  - Pixels: 1586 x 992 composite design board.
- Implementation screenshot paths:
  - `private-evidence://aibos/grid-wide.png` — 1600 x 900, SHA-256 `8f16b88aa18f535006a006e66d654df64fa99695364f92a088b5bdae798e610e`
  - `private-evidence://aibos/grid-narrow.png` — 900 x 820, SHA-256 `319b5de25786755dac398ba373b101019f463b180768945592a6f84f3263339c`
  - `private-evidence://aibos/grid-wide-final.png` — 1600 x 900, SHA-256 `7c72967dda774a4927252a43c0ee54860e6fff4f3475bf993c206a45ec1af465`
  - `private-evidence://aibos/grid-narrow-final.png` — 900 x 820, SHA-256 `2cdc3ade67543a4de7e49fe38645d46406596556b70e71e33d790d088b19ead0`
  - `private-evidence://aibos/settings.png` — 900 x 700, SHA-256 `7c12723af1bf1a23ec62996bc2afe861eebcea11459fde797c6412cad6371979`
  - `private-evidence://aibos/modal-wide.png` — 1280 x 820, SHA-256 `cd6490a4df436d46c477b4b32f7f7c91ef76ede0f2b4162c9934bf89cca9d178`
  - `private-evidence://aibos/album-library-populated.png` — 1120 x 760, SHA-256 `25a795f85c075e926e578d0834e571f146d95d7be4b6834c45d9974fd1298715`
  - `private-evidence://aibos/settings-high-contrast.png` — 1280 x 820, SHA-256 `acbf9eb9e820a40218c5e7ab631b518691f91c9ca9fee719f3a0782f5102272d`
  - `private-evidence://aibos/selected-batch-wide.png` — 1280 x 820, SHA-256 `3f0f343c887b9e5874d70f434d4b5095043c234e113fc5a084d7994c770f2abb`
  - `private-evidence://aibos/selected-batch-900px.png` — 900 x 620, SHA-256 `21d5822c9906cf5cef1bac5a1fbc3ad72fe58645ea591cf0bbc283c32fbb7dda`
  - `private-evidence://aibos/selected-batch-comparison.png` — 2591 x 820, SHA-256 `f85bf355e0b243b300bb9c5051149800fec42d01b0bda1047cd1f6bf434397cd`
- Local filesystem paths are deliberately omitted because this report is intended for a public repository and the captures contain private workstation path text.
- WPF captures used 96 DPI at 1.0 density. Pixel dimensions equal the arranged WPF DIP viewport. The composite source board contains several differently sized states, so each implementation capture was compared to its corresponding source region rather than scaled as one full frame.
- State: dark theme; synthetic TEMP image fixture; landing/grid/settings/modal/populated-Album/selected-batch states; wide and 900-DIP adaptive layouts. No user image, cache, history, or shared state was used.

## Full-view comparison evidence

The source board and each rendered implementation capture were opened together in the same comparison input. Wide workbench composition, 900-DIP adaptive composition, Settings, modal-with-filmstrip, the populated Album Library, and the selected-batch review were reviewed against the same token, density, border, and control language. The implementation retains the product's real portrait-filmstrip and complete toolbar semantics where the visual board was illustrative rather than exhaustive.

The selected-batch source comparison is intentionally language-based rather than a pixel-equality claim: the source board does not contain the new overlay. The review preserves the near-black workbench, violet primary action, thin cool-gray separators, compact hierarchy, flat rectangular controls, and low-radius opaque surfaces. At 900 x 620 the heading, configuration summary, virtualized source list, status strip, and persistent actions remain visible without horizontal clipping.

## Focused region comparison evidence

- Settings: compared left navigation, selected state, content column, form density, fixed footer action, borders, and radii at readable scale.
- Modal: compared top toolbar density, image fit, navigation edge zones, bottom filmstrip, footer controls, opaque surfaces, and window controls at readable scale.
- Narrow workbench: compared the 44-DIP navigation rail, gallery region, bottom preview, and persistent header at 900 x 820.
- Selection projection: compared the selected gallery card against the source board in both 1600-DIP wide and 900-DIP adaptive layouts. The final captures show the same persistent accent outline after focus moves to the Preview action surface.
- Album Library: compared its populated library/member columns, selected state, compact actions, status strip, borders, radii, and typography against the selected workbench language. The source board has no standalone Album surface, so the comparison is token- and density-based rather than pixel-fidelity based.
- High Contrast: inspected the Settings surface under the live Windows system palette. This accessibility mode intentionally replaces the dark visual direction rather than claiming pixel fidelity; the evidence confirms opaque system backgrounds, system text, highlight/focus color, readable primary action text, and preserved control hierarchy.
- Selected batch: inspected the full 1280 x 820 capture at original pixels, where its 11–17 px text, four-to-six-pixel radii, one-pixel borders, checkbox, status copy, bounded list, and footer actions remain readable. The 900 x 620 capture separately verifies the responsive boundary.

## Required fidelity surfaces

- Fonts and typography: Segoe UI/system WPF text is retained for native rendering. Sizes, weights, truncation, and hierarchy follow the compact source rhythm; small status text remains readable and modal filenames truncate rather than moving controls off-screen.
- Spacing and layout rhythm: large capsules and decorative spacing were removed. The wide layout uses sidebar/gallery/preview columns; the narrow layout uses rail/gallery/bottom-preview rows. Settings uses a stable 132-DIP navigation column. Modal, Album, and selected-batch surfaces use 3–6 DIP radii and solid borders.
- Colors and visual tokens: background, elevated surface, border, accent, favorite, enhanced, focus, hover, pressed, warning, and danger colors are centralized in WPF resources. Gradients, blur, refraction, parallax, and shadows are absent from the selected direction.
- Image quality and asset fidelity: production images remain decoded by the existing WPF pipeline. QA uses synthetic fixtures to preserve the user-data boundary, so subject matter differs intentionally from the illustrative source board. The selected-batch overlay introduces no new image asset, logo, illustration, decorative mark, custom SVG, or placeholder art.
- Copy and content: labels reflect the implemented Aibos functions. Existing safety, shared-state, enhancement, Album, keyboard, and Recycle Bin semantics are retained even when the visual board used shorter illustrative labels. The batch review explicitly states selected, eligible, skipped, preset, scale, format, request bound, companion and adapter availability, large-job consent, and the no-job-created state before the primary action.
- States and accessibility: review, checking, ready, skipped, submitting, created, failed, stopped, retry, and Jobs handoff states exist. Focus cycles in the batch dialog, the primary action receives initial focus after checking, Escape closes an idle review, and controls expose automation names/help text.

## Comparison history

1. Initial P1: the existing UI used capsule controls, large radii, translucent overlays, and gradient-heavy chrome. Fix: introduced solid lightweight tokens and compact square control primitives. Post-fix evidence: wide and settings captures above.
2. Initial P1: the 900-DIP layout did not match the selected adaptive workbench. Fix: added a 44-DIP navigation rail, moved the same preview instance below the gallery, and preserved wide-layout restoration. Post-fix evidence: `grid-narrow.png`; right-panel and zoom-anchor smokes pass.
3. Initial P2: Settings was one long undifferentiated scroll surface. Fix: added functional General, Display, Thumbnails, Keyboard, and About navigation with a fixed footer. Post-fix evidence: `settings.png`; settings/unseen-state smoke passes without changing Seen or cache data.
4. Initial P2: Album and modal chrome retained large radii, capsule badges, and the default context-menu treatment. Fix: reduced radii, removed the fake Album text mark, compacted modal controls, and added an opaque lightweight context-menu surface. Post-fix evidence: `modal-wide.png`; modal interaction and Album hardening smokes pass.
5. PRO High: gallery selection was represented in the header and Preview but was not durable enough visually after keyboard focus moved away from the gallery. Fix: projected canonical selection state into compact Grid, standard Grid, and List templates independently of transient WPF focus selection. Post-fix evidence: `grid-wide-final.png` and `grid-narrow-final.png`; the Grid → narrow Grid → List → wide Grid smoke preserves the same canonical path, visible marker, and source bytes.
6. Follow-up polish: the Right Preview action row overflowed at its minimum width and the Album Library lacked populated visual evidence. Fix: replaced the fixed action grid with an ordered wrapping surface (240 DIP = two rows, 380 DIP = one row) and added an isolated secondary-window capture using a synthetic populated Album store. Post-fix evidence: right-panel smoke and `album-library-populated.png`.
7. Accessibility pass: the static dark palette did not follow Windows High Contrast and several Right Preview actions depended on tooltip-only names. Fix: routed opaque color tokens through live system colors, restored the standard palette without restarting, preserved user-configured thumbnail-border semantics, and added explicit automation names/focus/tab-order checks. Post-fix evidence: `settings-high-contrast.png`; accessibility, keyboard, modal-focus, Album-focus, and DPI-anchor gates pass.
8. Selected-batch pass: the new state was compared once at 1280 x 820 and 900 x 620 against the selected workbench language. No actionable P0/P1/P2 difference was found, so no visual correction loop was required.

## Findings

No actionable P0, P1, or P2 visual mismatch remains for this implementation batch.

## Open questions

- The source board does not define a standalone Album Library window or selected-batch review. Those states therefore follow the same tokens and density without claiming pixel fidelity to missing source states.
- The source board shows landscape filmstrip samples. The product now uses one compact, landscape, horizontally virtualized filmstrip in the modal, matching that direction without reducing the image canvas.

## Implementation checklist

- [x] Lightweight solid visual tokens and compact control primitives.
- [x] Wide workbench and 900-DIP adaptive layout.
- [x] Functional Settings category navigation.
- [x] Modal, filmstrip, context menu, and Album chrome alignment.
- [x] Persistent canonical selection marker in compact Grid, standard Grid, and List across focus and wide/narrow mode changes.
- [x] Right Preview actions fit at 240–380 DIP without clipping or changing keyboard order.
- [x] Populated Album Library secondary-window visual captured from isolated synthetic state.
- [x] Live Windows High Contrast palette with standard-theme restoration.
- [x] Right Preview automation names, focus visuals, and logical Tab order.
- [x] DPI anchor, editable keyboard, modal focus, and Album focus regression gates.
- [x] Selected-batch review with bounded virtualized list and fixed footer at 900 DIP.
- [x] Review-only no-POST state, large-job consent, keyboard focus cycle, and Jobs handoff.
- [x] .NET 10 Release build with zero warnings and zero errors.
- [x] Focused UI, state-isolation, modal, Album, zoom-anchor, rapid-churn, batch, and real-HTTP gates.

## English/Japanese and adaptive-preview follow-up

- Additional exact captures:
  - `private-evidence://aibos/locale-landing-final.png` — 1280 x 820, SHA-256 `8537abdf8ff78982859b74601c58d47028b5e304a580388eb00d5dee3d8bef0`
  - `private-evidence://aibos/locale-grid-wide-final.png` — 1580 x 920, SHA-256 `b0e1522afa743a9243dec54f6d7a04e1fb98b4b6470e50ff6f108fa38cc1751c`
  - `private-evidence://aibos/locale-settings-ja-final.png` — 1280 x 820, SHA-256 `84f8fcda5fa651d20826c0b8c73a76dfc5848d291a4c1e08f605662db64f7341`
  - `private-evidence://aibos/locale-modal-final.png` — 1280 x 820, SHA-256 `daebc2475e98c242b566b5550a241cd3a3bd4976ba538b89ea56b6a5ab717b17`
  - `private-evidence://aibos/locale-grid-narrow-ja-final.png` — 900 x 820, SHA-256 `21e14f2effbbb4afe24306a59af6de7648aa57342ccb702a0c383ea868b932ea`
  - `private-evidence://aibos/locale-source-comparison-final.png` — 1586 x 2020, SHA-256 `aa8710be666792bb4822f794ed3acf1448d738b606749b903b2c8b753c0a14d3`
- The source and exact candidate contact sheets were opened together in the same comparison input. Each implementation state was also inspected at its native 96-DPI viewport before contact-sheet fitting.
- P2 fixed: the empty Landing previously appeared as an unexplained thin blank surface. It now exposes the selected direction's bounded folder-drop target with explicit non-destructive copy.
- P2 fixed: the 900-DIP Preview previously pushed Favorite, AI, Open, Album, and overflow actions below the visible pane. Its image height is now derived from the bounded adaptive row and uses aspect-preserving `Uniform`; the image and primary actions remain visible.
- P2 fixed: Japanese switching previously left the thumbnail-border save label and idle status in English. Dynamic border status now uses the same live language resources.
- English/Japanese switching is WPF-local presentation state. Captures and the dedicated gate prove that it does not write Browser `settings.json`, source images, or user state outside isolated TEMP fixtures.
- The post-fix pass found no actionable P0, P1, or P2 difference in Landing, wide Grid, Japanese Settings, full-canvas Modal, or the 900-DIP Japanese adaptive state.

## Repair 4 final hands-on request pass

### Exact visual evidence

- Source visual truth: `private-evidence://aibos/workbench-split-reference.png`
  - 1586 x 992 pixels at 96 DPI; SHA-256 `b47ee334b70174d6640f756574a74709c6401120710807cd7e1388c00483db12`.
- Rendered implementation:
  - `private-evidence://aibos/repair4-landing.png` — 900 x 1068, SHA-256 `b9d9aec482aa6c84e0ef1886aa956ef4f346fc0124fe61401b158677793b8f91`
  - `private-evidence://aibos/repair4-grid-wide.png` — 1280 x 820, SHA-256 `b85a6531665aeb6d4e10122928bac93d6dc2f4b89acadd68d92e2258406f7633`
  - `private-evidence://aibos/repair4-settings.png` — 900 x 710, SHA-256 `c56eb86efe74cd13deeb42a4602d5c95072b7f1f9529342ed6efd64bbdd82e`
  - `private-evidence://aibos/repair4-modal.png` — 1500 x 793, SHA-256 `151bd7e8345e40e73eba20e39e7654e029f5692a4ba4e175c5d5951f8726a24d`
  - `private-evidence://aibos/repair4-narrow.png` — 900 x 770, SHA-256 `98910a82ffe84ce06c48d9af859ead477804ae8d6c892e3a241e94c14048f020`
  - `private-evidence://aibos/repair4-source-vs-implementation.png` — normalized 1586 x 1984 full-view comparison, SHA-256 `88a7cb5fc891735dc6e41eca32cdb87daf90314d046f3f12e4673cdea7afe6e5`
  - `private-evidence://aibos/repair4-grid-modal-focused.png` — 1332 x 712 focused comparison, SHA-256 `7392eff15d1c5a2cc14426d5335de51a43856e67b388bfc152b0c1e8147798ba`
- Viewport and density: captures were rendered at the named WPF logical DIP dimensions through a 96-DPI `RenderTargetBitmap`; implementation pixels equal CSS-equivalent logical size at density 1. The composite source board contains five differently sized states, so each implementation state was normalized to its corresponding source region before the combined comparison was made.
- State: dark theme, English UI, `Original` aspect behavior, seven Windows system sample images copied into an isolated TEMP fixture. No user image, cache, shared state, history, favorite, Album, or enhancement record was read or changed.

### Full-view and focused comparison

The normalized full-view comparison was opened at original pixels and reviewed for Landing, wide workbench, Settings, modal, and 900-DIP adaptive composition. The focused comparison placed the source and implementation Grid and Modal regions together at readable scale. The actual product preserves its real Jobs, Albums, enhancement, Preview, and metadata workflows where the source board used illustrative controls.

### Required fidelity surfaces

- Fonts and typography: native Segoe UI rendering, compact hierarchy, line height, truncation, wrapping, and optical weight remain consistent with the selected workbench. Narrow and Japanese layout gates show no clipped persistent labels.
- Spacing and layout rhythm: the 1280-DIP workbench now reaches three columns with 10-DIP card spacing, retains a 340-DIP default Preview, and guarantees a 96-DIP draggable header region. Sidebar, scrollbar, settings action, and modal controls remain contained at compact and short work areas.
- Colors and visual tokens: the near-black background, cool one-pixel separators, violet accent, bright favorite red, restrained cyan enhancement state, and lightweight translucent chrome map to centralized WPF resources. No backdrop blur, refraction, parallax, or continuously animated glass effect was added.
- Image quality and asset fidelity: production decoding remains unchanged. `Stretch.Uniform` intentionally preserves the full source image, so letterboxing may appear where the illustrative board used cropped scenic thumbnails. This is accepted product behavior rather than a placeholder or missing thumbnail. All seven TEMP thumbnails settle with zero final placeholders and zero unrealized visible items.
- Copy and content: the implementation uses real Aibos labels and commands. Grid/List cards do not add prompt text. English and Japanese resources exist for the new accessibility settings without forcing Japanese onto icon-led chrome.
- Icons and controls: modal Favorite, folder, filmstrip, close, navigation, zoom, and fit actions use the existing licensed/native icon language. The Favorite heart is larger and brighter in gallery cards; modal Favorite is icon-only. The app close control and modal exit affordance remain spatially distinct.
- Responsiveness: wide, 900-DIP adaptive, 760 x 480 compact, 960 x 500 short, maximize/restore, and DPI-equivalent work areas remain contained. The narrow layout keeps gallery, bottom Preview, and persistent actions reachable.
- Accessibility and states: live Windows High Contrast, reduced motion, reduced transparency, keyboard focus, automation names, bilingual settings, hidden modal chrome, filmstrip on/off, details open/closed, zoom/pan, favorite-only eviction, loading retry, corrupt-terminal, and empty Landing states are covered.

### Primary interactions tested

- Favorite-only → remove favorite completes without blocking, preserves the expected neighbor selection, and keeps virtualization bounded in a 100,000-item catalog.
- Grid zoom buttons, shortcuts, gallery wheel, scrollbar wheel, selection anchor, sidebar/right-panel resize, DPI change, and List-mode isolation pass.
- Image click hides and shows modal chrome immediately; hidden chrome also hides the cursor and remains hidden across image navigation.
- Filmstrip button turns the single bottom strip off and on without hover reopening it or moving the footer control under the pointer.
- Opening Details preserves the full image canvas, zoom, and pan rather than fitting or shrinking the image.
- Edge navigation remains configurable by percentage while its chevrons are visually subdued; the black surface outside the image exits to the gallery.
- A transient locked thumbnail recovers without reload; corrupt input terminates after four bounded attempts; fixture source hashes remain unchanged.

### Comparison history for this pass

1. P1: modal chrome could only be hidden from the image, and the duplicated hover/pinned filmstrips could reopen or move controls while the pointer was pressing them. Fix: made image-click chrome toggling bidirectional, consolidated to one stable filmstrip presenter, and made its explicit off state authoritative over hover. Post-fix evidence: `repair4-modal.png` and `repair4-grid-modal-focused.png`; the expanded modal interaction gate passes.
2. P2: opening Details scheduled a fit update and changed zoom/pan geometry. Fix: separated details visibility from image fitting. Post-fix evidence: the modal smoke records stable geometry and transform across Details open/close.
3. P2: the initial repair capture still showed a bulky portrait filmstrip and a two-column 1280-DIP grid. Fix: changed the strip to compact landscape cells, reduced grid spacing, and tuned the default Preview width. Post-fix evidence: `repair4-grid-wide.png`, `repair4-modal.png`, and the focused comparison show three columns and the smaller landscape strip.
4. P2: the Landing frame lacked the selected direction's persistent Aibos header identity. Fix: added the same compact icon-and-wordmark treatment used by the workbench without introducing a new final brand asset. Post-fix evidence: `repair4-landing.png`.
5. P2: fixed minimum dimensions and header density could clip Settings, sidebars, scrollbars, buttons, or the drag surface on compact work areas. Fix: derive effective minimums from the active work area at startup, DPI transition, and custom maximize; normalize the initial bounds before first interaction; wrap sidebar content; constrain search; and preserve the drag region. Post-fix evidence: monitor-work-area smoke passes wide, 760 x 480 compact startup, compact/short maximize, Japanese, restore, and offscreen normalization cases.

### Findings

No actionable P0, P1, or P2 mismatch remains in the selected five-state WPF direction or the repaired interactions.

## Follow-up polish

- P3: add optional monochrome category icons to Settings only if a licensed, consistent icon source is adopted; text navigation is currently clearer than placeholder glyphs.
- Completed in the later Gallery Fold savepoint: the user-selected mark now has
  dedicated small optical masters, deterministic PNG exports, and a
  multi-frame Windows icon. The generated wordmark remains excluded because its
  font and provenance are not authoritative.
- P3: the large-image consent label is accurate but slightly mechanical; a later copy-only pass may shorten it to “Allow very large images when warned.”
- P3: the adaptive Preview can become a true two-column thumbnail/details composition if hands-on testing shows that metadata must remain above the fold.
- P3: static surface opacity can be tuned after hands-on testing; blur or shaders are not required for that refinement.

final result: passed

## M3 background-scan handoff and off-CPU attribution

- Exact hosted diagnostic candidate
  `35cdacdd6336b3498a17fd7d65376bc65af13424` failed only one 55 ms Input
  heartbeat during `search-clear-2`; the unchanged budget is 50 ms. The same
  operation had no Gen2 collection, a maximum Dispatcher-owned slice of 3 ms,
  a 6 ms layout flush, and a 2.0927 ms reset-panel unit. Its reset sub-steps
  were 0.2504 ms generator removal, 0.0017 ms deferred-measure cleanup, and
  1.8393 ms visual removal.
- Clear projections consistently spent longer in complete-catalog scans than
  matching projections. The bounded repair retains the `Lowest` worker
  priority and yields the worker quantum every 2,048 items only in
  cancellation-enabled, non-sort linear scans and the Automation name-index
  build. Synchronous compatibility paths, sort comparison, UI capture/apply,
  and all hard limits are unchanged.
- `RemoveInternalChildRange` attribution now also records Dispatcher-thread
  CPU time through Windows `GetThreadTimes`. Per-projection diagnostics include
  Gen0/Gen1/Gen2 collection deltas and `GC.GetTotalPauseDuration()` so an
  elapsed outlier can be separated from code execution and GC suspension.
- The first local handoff run made heartbeat green at 43 ms with
  search/filter/sort P95 151/48/124 ms, Favorite eviction 29 ms, and exact
  pending-broaden behavior. It failed only a 21.0821 ms reset-panel wall-time
  sample whose visual removal accounted for 20.9669 ms.
- With CPU and GC attribution enabled, a later local sample measured
  27.0565 ms visual-removal wall time but 0 ms Dispatcher-thread CPU, zero
  Gen0/Gen1/Gen2 collections, and zero GC pause for that same projection.
  Heartbeat was green at 46 ms and every other contract passed. This proves
  that reset outlier was an off-CPU host scheduling interval, not 27 ms of WPF
  container teardown. The 12 ms reset limit remains unchanged.

final result: pending exact hosted verification

## M3 Gallery Fold brand savepoint

- The user selected Gallery Fold as the WPF brand direction.
- The runtime uses dedicated 20, 24, and 64 px marks plus one embedded Windows
  icon. It does not load brand files from disk or the network at runtime.
- The deterministic brand gate passes with 20 PNG frames and 10 ICO frames at
  16, 20, 24, 32, 40, 48, 64, 96, 128, and 256 px.
- Runtime payload is 103.06 KiB. Branding adds no shader, blur, animation,
  polling, per-frame allocation, shared-state access, or Browser dependency.
- The 16, 20, and 24 px frames use separate optical masters so both negative
  seams remain visible. White and black exports also pass the structural gate.
- PRO review returned CLEAR with Critical/High blocker count 0 for the
  pre-refactor savepoint. The raster pack is accepted until an authoritative
  vector source exists; speculative tracing and the generated wordmark remain
  excluded.
- Trademark, name-clearance, and final asset-provenance approval remain human
  release gates. This engineering savepoint is not legal clearance and does
  not authorize deployment.

## M3 catalog-interaction acceptance decision

### Oracle question

Should the 100,000-item WPF catalog keep every instrumented dispatcher slice at
or below 4 ms as a Critical gate, or should one indivisible realized-container
reset unit have a separate budget while externally observed input latency and
end-to-end correctness remain hard gates?

### Answer summary

PRO returned REVISE and selected the second option. The 4 ms value remains a
diagnostic target. The Critical contract is one realized-container reset unit
at or below 12 ms, external Dispatcher heartbeat gap at or below 50 ms,
Favorite-only removal at or below 100 ms, and exact eviction, neighboring
selection, UI Automation projection, visible coverage, and stale-visual
results.

### Adjudication

- ADOPT: separate the indivisible WPF generator/visual-tree reset budget from
  code-owned diagnostic slices; keep heartbeat and end-to-end correctness as
  hard gates.
- PARTIAL: the recommendation asked for one cold and two warm isolated
  processes. All three were run against the same candidate tree and all hard
  gates passed.
- REJECT: clearing DataContext, image bindings, or templates merely to force
  WPF's indivisible detach below 4 ms. That would add selection, binding,
  recycling, and layout risk without evidence of a user-visible freeze.
- DEFER: High-DPI screen-reader combinations and independent M4 contract audit.
  This decision does not authorize M4 or the structural refactor.

### Evidence

- Cold process: 100,000 catalog items, 9 realized cards, heartbeat maximum
  44 ms, Favorite-only removal 26 ms, no over-budget heartbeat sample.
- Warm process 1: heartbeat maximum 40 ms, Favorite-only removal 32 ms, no
  over-budget heartbeat sample.
- Warm process 2: heartbeat maximum 41 ms, Favorite-only removal 28 ms, no
  over-budget heartbeat sample.
- All three processes evicted the changed Favorite item, selected the same
  expected neighboring logical item, preserved the exact UI Automation
  projection, retained full-extent virtualization, and passed the complete
  interaction gate.
- The previously observed 10 ms reset unit was one realized container, remained
  below the new 12 ms hard budget, and coincided with a 42 ms heartbeat maximum
  and 51 ms exact Favorite-only removal. It is retained as diagnostic evidence,
  not discarded as an exception.

### Durable decision

The M3 gate now reports its budgets in the result payload. A code-owned apply
slice above 4 ms is named and reported as a diagnostic warning. M3 fails when a
single-container reset exceeds 12 ms, a Dispatcher heartbeat exceeds 50 ms,
Favorite-only removal exceeds 100 ms, or eviction/selection/UI Automation
correctness differs. The rollback boundary is the gate classification and
result fields only; catalog scheduling, virtualization, bindings, product
state, source images, Browser behavior, licensing, and deployment are
unchanged.

## M3 hosted-runner catalog gate repair

- The first exact hosted run failed only the large-catalog gate: Dispatcher
  heartbeat 73 ms versus 50 ms and normalized working-set growth 39.225%
  versus 35%. Logical count, virtualization, selection, UI Automation,
  Favorite eviction, search/filter/sort latency, one-container detach, and
  live managed memory all remained within contract.
- Root-cause review found an incomplete measurement boundary. Search, filter,
  sort, and mixed-churn paths were primed before the memory baseline, but the
  first Windows UI Automation client connection and focused projection reset
  were not. Their one-time native WPF/UIA page commitment was therefore
  counted as repeated catalog growth.
- The smoke now primes one bounded focus filter/clear cycle and one external
  UI Automation realization before the memory baseline. Dispatcher heartbeat
  monitoring starts before that accessibility warmup, so cold first-use
  latency remains visible. Only the harness's deliberate stop-the-world Full
  GC is excluded from heartbeat timing; captured samples and maxima are never
  reset.
- No product threshold was relaxed. The 50 ms Dispatcher, 35% normalized
  working-set, 100 ms Favorite eviction, 12 ms realized-container reset, and
  exact UI Automation/selection contracts are unchanged.
- Three isolated .NET 10 Release processes passed at 100,000 items:
  - cold: 9 realized, 45 ms heartbeat, 41 ms Favorite eviction, 7.309%
    normalized working-set growth;
  - warm 1: 9 realized, 38 ms heartbeat, 34 ms Favorite eviction, 9.832%
    normalized working-set growth;
  - warm 2: 9 realized, 46 ms heartbeat, 30 ms Favorite eviction, 7.647%
    normalized working-set growth.
- All three reported zero over-budget heartbeat samples and exact Favorite
  eviction, neighboring selection, external UI Automation projection, and
  100,000-item logical count. The post-gate DPI, modal, Album, accessibility,
  enhancement, batch, and companion-path checks also passed.
- The next exact hosted run kept normalized working-set growth at 11.791% but
  exposed two slower-runner product costs: Favorite eviction at 121 ms and one
  53 ms heartbeat sample. Correctness, selection, UI Automation, and the
  one-container reset were still exact.
- Favorite-filter exclusion now feeds the existing scheduler from the current
  visible projection. In the measured Favorite-only case that reduces the
  immutable input from 100,000 items to 10,000 without bypassing the staged
  Automation projection or atomic publication. Real-product state persistence
  is also debounced after the projection; the smoke suppresses state writes, so
  that improvement is not counted as gate evidence.
- Reset preparation now follows WPF's ordinary viewport-cleanup lifecycle:
  release one tail generator entry, forget its deferred measure, detach its
  visual, then yield. The previous visual-first/all-generators-later order was
  unique to Reset and produced a local 18 ms detach outlier.
- The repaired tree passed three isolated 100,000-item processes with
  heartbeat 48/47/44 ms, Favorite eviction 35/29/34 ms, one-container reset
  1/8/9 ms, and normalized working-set growth 5.388/4.234/10.399%. All exact
  count, neighboring selection, UI Automation, virtualization, and heartbeat
  sample requirements passed. Thumbnail continuity, right-panel selection,
  and accessibility regression gates passed afterward.

final result: passed

## M3 lightweight modal-glass follow-up

- The user reaffirmed `private-evidence://aibos/workbench-split-reference.png`
  (1586 x 992, SHA-256
  `b47ee334b70174d6640f756574a74709c6401120710807cd7e1388c00483db12`)
  as the product direction, then asked for less matte floating chrome.
- Exact candidate captures:
  - `private-evidence://aibos/modal-glass-wide-final.png` — 1280 x 820,
    SHA-256
    `8bb8f9a323e458a3af8c36210103d9550ae6cef12fe888374910bf97c33cbeb0`.
  - `private-evidence://aibos/modal-glass-narrow-final.png` — 900 x 700,
    SHA-256
    `289d141d013c59dd2047fbaed006939760f572c22db47e972a0d79ef84d63d80`.
  - `private-evidence://aibos/modal-glass-before-after.png` — 2560 x 864,
    SHA-256
    `0c553768cd18028d6b56df1a3e1dd8f672e207f29cc01844da6781aaaca18782`.
- Floating modal chrome now uses one static alpha gradient over the image.
  There is still no blur, refraction, shader, parallax, shadow, or continuous
  glass animation. Reduced Transparency composites both gradient colors to
  opaque tokens, and High Contrast replaces them with the Windows system
  palette.
- The modal title is the last fill child in the toolbar, so commands keep their
  stable geometry while the filename truncates. At widths below 1080 DIP,
  duplicated Shortcuts, Delete, Open, and Reveal commands move to the existing
  context-menu path; Actual pixels, Favorite level, Filmstrip, Details,
  Enhancement, and native window controls remain visible.
- The ambiguous `1:1` label is now `100%` with the explicit tooltip “one image
  pixel per screen pixel.” The modal Favorite group displays the current
  numeric level between its decrease/increase hearts.
- Exact gates: .NET 10 Release warning 0/error 0; modal interaction including
  compact-toolbar restore; reduced motion/transparency; Windows High Contrast;
  760 x 480 and 960 x 500 work-area containment; Original-aspect reflow.

final result: passed

## M3 repair 8 exact single-removal candidate

- Repair 6 passed locally but its exact hosted Windows run exposed three
  independent hard-gate overruns: search P95 251.75 ms, one 13 ms
  single-container detach, and two heartbeat samples above 50 ms. All catalog
  count, selection, stale-peer, UI Automation, Favorite, virtualization, and
  memory semantics remained exact.
- A bounded Reset-container recycling experiment was rejected after its changed
  tree still produced a 14 ms detach and 54 ms heartbeat and did not
  consistently lower apply allocation.
- The candidate therefore uses the previously reviewed narrower A+B repair:
  a stable current-projection result that is proved off-thread and revalidated
  on the Dispatcher as exactly one ordered removal publishes one Remove
  notification after staging the matching Automation projection. Every
  broader, reordered, pending, or non-exact result stays on the existing Reset
  path. The non-sort scan and Automation-index cooperative-yield quantum is
  1,024 items; thresholds and sort semantics are unchanged.
- The interaction gate now requires the Favorite-only exercise to observe one
  Remove and zero Reset notifications. The pending-broaden race must continue
  to use Reset and zero Remove notifications.
- The first fresh 100,000-item process passed: search/filter/sort P95
  181/78.45/108.75 ms, Favorite eviction 63 ms with exact single Remove,
  heartbeat 46 ms, maximum detach and apply slice 4 ms, normalized working-set
  growth 10.002%, and all semantic checks exact.
- The second required fresh process held only on a 52 ms heartbeat during the
  cold accessibility warmup. It kept search/filter/sort P95
  188.8/108.45/187.5 ms, Favorite eviction 55 ms, exact single Remove,
  one-millisecond detach/apply slices, and all semantics exact. The candidate
  is not accepted from a later green rerun.
- The diagnostic candidate retains the warmup filter and clear completion
  metrics and records wall time for selection, layout, focus, filter, clear,
  realization-idle, external UI Automation, and final-focus substeps. No
  product threshold, source image, shared state, cache, history, Browser
  behavior, license, or deployment boundary changes.

final result: pending exact hosted attribution

## M3 repair 9 bounded small-projection handoff

- The Repair 8 exact hosted run kept every catalog, selection, UI Automation,
  pending-broaden, virtualization, heartbeat, detach, and memory contract
  green. Its sole failure was Favorite-only eviction at 107 ms against the
  unchanged 100 ms hard limit; the same route had completed in 55-63 ms in the
  two fresh local processes.
- Independent very-high review attributed the narrow-host variance to worker
  handoffs that remained tuned for the 100,000-item projection even after the
  Favorite-only path safely restricted its input to 10,000 items. Cancellation
  checks remain every 64 items, but projections of at most 16,384 items no
  longer call `Thread.Yield`; larger projections retain the 1,024-item
  cooperative-yield quantum.
- Dispatcher publication still proves the current collection is exactly the
  worker result with one ordered item removed. The already validated array is
  then committed with shape checks instead of repeating the same full sequence
  comparison a second time. Automation staging, one Remove / zero Reset
  notification semantics, selection fallback, and pending-broaden Reset
  fallback are unchanged.
- The first fresh 100,000-item local process reduced Favorite-only eviction to
  70 ms and kept search/filter/sort P95 at 207.45/81.5/171.25 ms, maximum apply
  and detach slices at 1 ms, normalized working-set growth at 7.583%, and all
  correctness contracts exact. Two unrelated 61/59 ms heartbeat samples during
  100,000-item search/filter Reset paths had no GC pause and only 1 ms measured
  Dispatcher slices, so this process remains red pending exact hosted
  adjudication; no threshold was relaxed and no green rerun is substituted.

final result: pending exact hosted adjudication

## M3 repair 10 exact-removal presentation turn

- Repair 9 reduced the exact hosted Favorite-only result from 107 ms to
  101 ms, but the unchanged 100 ms hard limit still rejected the candidate.
  Search/filter/sort P95 were 244.45/137.85/194.25 ms, heartbeat was exactly
  50 ms with zero over-budget samples, maximum detach/apply slices were
  2/3 ms, normalized working-set growth was 6.951%, and every semantic contract
  remained exact.
- The Favorite path had already proved one ordered removal, staged its matching
  Automation projection, and safely unwound the originating pointer route.
  It nevertheless paid four more `Dispatcher.Yield` scheduling turns between
  O(1) presentation phases. Repair 10 retains the initial route-unwind yield,
  all generation checks, and every Reset/broader-projection yield, while keeping
  only the proven exact-removal presentation phases in the same Dispatcher
  turn.
- Independent review rejected the first prototype because it also skipped the
  mandatory immediate post-publication yield and split the diagnostic
  Stopwatch at boundaries that did not actually yield. That green result is
  excluded from acceptance evidence.
- The corrected implementation always yields before and immediately after
  publication. It skips only the later three exact-removal presentation
  boundaries, keeps the main apply-slice Stopwatch cumulative across them, and
  emits the exact Favorite projection's apply slice separately.
- The corrected fresh 100,000-item process measured Favorite-only eviction at
  72 ms with a 2 ms uninterrupted apply slice, search/filter/sort P95 at
  205.15/97.4/180 ms, one-millisecond detach, exact single Remove and
  pending-broaden Reset semantics, and 7.615% normalized working-set growth.
  It remained red only on one unrelated 52 ms keyboard-focus-clear heartbeat
  sample with zero GC pause; no rerun or threshold change substitutes for that
  result. Release build remained warning 0/error 0.

final result: pending exact hosted verification

## M3 repair 11 keyboard focus GC attribution

- The exact hosted Repair 10 tree passed every semantic, Favorite, projection,
  memory, and internal-slice contract. Its only failure was a 59 ms Input
  heartbeat during `keyboard-focus-filter-realize`; the same interval recorded
  a 54.769 ms runtime GC pause at projection generation 55.
- Existing projection diagnostics show that later clear operations reuse the
  prepared 100,000-item layout (`ComputeAllocatedBytes` falls to 560 bytes),
  while each WPF Reset publication still allocates roughly 0.8-1.7 MB.
  Expanding the weak layout cache would not remove that publication churn and
  could retain several large-object-heap layouts, so that change is rejected.
- The next changed tree exports the filter and clear
  `SearchFilterCompletion` records used by the keyboard-focus scenario. Grid
  realization and focus restoration are separately labeled and report wall
  time, process allocation, GC pause, and Gen0/1/2 collection deltas. This is
  diagnostic-only: product scheduling, input priority, GC policy, thresholds,
  and user-visible behavior are unchanged.
- The diagnostic run passed incidentally at a 45 ms heartbeat, so it is not
  accepted as the product repair. It isolated the allocation source:
  generation 55 spent only 2,040 bytes in the staged collection Reset and
  roughly 1.12 MB across apply presentation, while subsequent card
  realization allocated 2.76 MB. Focus restoration allocated only 8 KB.

final result: attribution complete; product repair required

## M3 repair 12 bounded reset-container reuse

- WPF clears its built-in recyclable-container queue on a collection Reset.
  The custom gallery panel must therefore recreate card templates after every
  search/filter publication even though only the visible card containers are
  involved.
- Cards view now retains at most 32 homogeneous `ListBoxItem` containers only
  after reset preparation has fully unlinked each container from the WPF
  generator and visual tree. A candidate is reused only when it has no visual
  parent. Normal viewport cleanup and the list view's built-in WPF recycling
  remain unchanged; the bounded pool is cleared when the gallery unloads.
- The first prototype registered containers from
  `ClearContainerForItemOverride`, before the panel detached them. It reduced
  realization allocation by about half and Favorite eviction to 39 ms, but
  one visual detach reached 28 ms and a heartbeat reached 56 ms. That tree is
  rejected.
- The corrected reset-only implementation registers a container after
  `RemoveInternalChildRange`. The exact local .NET 10 Release 100,000-item
  gate passed with 237 container reuses:
  - search/filter/sort P95 193.55/110.7/86.5 ms;
  - Favorite eviction 45 ms with a 2 ms exact apply slice;
  - heartbeat 45 ms with zero over-budget samples;
  - maximum reset-container detach 11 ms against the unchanged 12 ms limit;
  - normalized working-set growth 5.891%;
  - exact count, selection, focus, pending-broaden, stale-peer, UI Automation,
    virtualization, and recycled-container state contracts.
- For keyboard-focus generation 55, apply allocation fell from 1,123,144 to
  738,504 bytes and realization from 2,755,184 to 2,066,224 bytes. Generation
  56 fell from 1,119,360 to 777,408 apply bytes and from 2,689,368 to
  1,558,608 realization bytes. The maximum 11 ms detach is green but has only
  one millisecond of margin against the unchanged 12 ms hard limit, so M3
  remains held until exact same-tree hosted evidence.
- No forced GC, no GC subtraction, no threshold change, and no change to
  user data, Browser behavior, licensing, or deployment is included.

final result: pending independent review and exact hosted verification

## M3 pending-broaden Favorite exclusion audit repair

- Independent final review found one stale-subset race in the Favorite-only
  fast path. If a narrow search was visible, a broader search was pending, and
  the selected Favorite was removed before that broader projection published,
  the exclusion could cancel the broader request and reuse the old narrow
  projection as its source.
- Current-projection reuse is now allowed only when no scheduled, pending, or
  in-flight catalog projection exists. Otherwise the scheduler recomputes from
  the complete logical catalog. Publication, selection fallback, UI Automation,
  and the deferred pointer route are unchanged.
- The 100,000-item gate now reproduces the exact overlap: Favorite-only plus a
  1,000-item narrow search, pending search clear, then immediate Favorite
  exclusion. It requires the pending broaden to be discarded, the replacement
  projection to publish, the target to be evicted, and the complete 9,999-item
  Favorite projection to appear before restoring all 100,000 items.
- The first harness attempt correctly exposed that the preceding stale-peer
  warmup had intentionally marked the synthetic selected tile non-real. The
  regression now explicitly restores that existing smoke precondition before
  mutating Favorite state; no product condition or performance threshold was
  relaxed.
- Three isolated .NET 10 Release processes passed at 100,000 items:
  - cold: 43 ms Favorite eviction, 46 ms heartbeat, 11 ms one-container
    detach, 9.448% normalized working-set growth;
  - warm 1: 36 ms Favorite eviction, 34 ms heartbeat, 9 ms one-container
    detach, 7.369% normalized working-set growth;
  - warm 2: 38 ms Favorite eviction, 43 ms heartbeat, 1 ms one-container
    detach, 7.521% normalized working-set growth.
- All three passed the new pending-broaden regression and the unchanged hard
  limits of 100/50/12/35. Release build remained warning 0/error 0.

final result: passed

## M3 hosted keyboard-focus contention repair

- Exact hosted candidate `980c8a3a3129057da5ffdf47b0cb20fe9622e815`
  passed Release, shared-state, thumbnail, Public surface, CodeQL, and every
  large-catalog correctness contract, including the pending-broaden regression.
  The large gate failed only one 56 ms Input heartbeat during
  `keyboard-focus-filter` against the unchanged 50 ms limit.
- Hosted diagnostics bounded Dispatcher-owned work at 2 ms per apply/detach
  slice while background projection compute consumed 19-39 ms. Independent
  high-reasoning review therefore classified the failure as real CPU scheduler
  contention rather than a blocking UI operation.
- The broad cooperative-yield prototype was rejected: applying yields to the
  sort comparator increased local sort P95 to 1,166 ms against 500 ms. None of
  that prototype is retained.
- The accepted repair changes only the existing background projection worker
  from `BelowNormal` to `Lowest` priority and restores the pooled thread's
  original priority in the existing `finally`. Projection logic, focus
  restoration, heartbeat boundaries, and every threshold are unchanged.
- Three isolated .NET 10 Release processes passed at 100,000 items after the
  priority repair:
  - cold: 73 ms Favorite eviction, 43 ms heartbeat, 2 ms one-container detach,
    7.456% normalized working-set growth;
  - warm 1: 25 ms Favorite eviction, 36 ms heartbeat, 1 ms one-container detach,
    3.727% normalized working-set growth;
  - warm 2: 26 ms Favorite eviction, 39 ms heartbeat, 1 ms one-container detach,
    8.768% normalized working-set growth.
- Search/filter/sort P95 remained within 250/250/500 ms in all three processes,
  and the pending-broaden Favorite regression remained exact.

final result: passed

## M3 reset sub-step attribution

- Exact hosted attempt 2 made Dispatcher heartbeat green at 43 ms but held the
  candidate on one 13 ms single-container reset unit against the unchanged
  12 ms hard limit. All other correctness, latency, memory, and
  pending-broaden contracts passed.
- The reset unit now reports separate elapsed time for
  `ItemContainerGenerator.Remove`, `ForgetDeferredMeasureRange`, and
  `RemoveInternalChildRange`, plus the complete panel unit. Timestamp reads do
  not change the generator-first order, yield boundary, selection, or reset
  behavior.
- The PowerShell gate requires attribution whenever a reset unit was observed
  and includes all three sub-step values in any hard-limit failure.
- The first local .NET 10 Release 100,000-item run passed with 44 ms heartbeat,
  25 ms Favorite eviction, 1 ms hard-gate detach, and 8.445% normalized
  working-set growth. Its maximum panel unit was 1.3352 ms:
  `ItemContainerGenerator.Remove` 0.1925 ms,
  `ForgetDeferredMeasureRange` 0.0013 ms, and
  `RemoveInternalChildRange` 1.1410 ms.

final result: passed

## M3 final heartbeat attribution repair

- Exact hosted candidate `687c4e0a8eff6bcfc58f726ba0b26f46f75a4a2e`
  passed every large-catalog semantic and resource contract but reported one
  60 ms Input heartbeat during the combined stale-peer lifetime exercise.
  Search/filter/sort, pending-broaden Favorite exclusion, selection, UI
  Automation, virtualization, reset slices, and memory stayed within contract.
- The stale-peer exercise is now split into eight labeled substeps. Its filter
  and clear completions retain projection generation, capture/compute/apply,
  allocation, GC-pause, reset, and maximum-slice evidence. The virtualizing
  panel retains the maximum Measure wall sample with operation and layout
  generation; coarse Windows thread-CPU timing is collected only while the
  catalog diagnostic is active.
- The first diagnostic run failed at a different operation:
  `filter-on-1` reached 55 ms with an 11.786 ms Gen0 pause, while its maximum
  apply slice was 1 ms and its panel reset was 1.0957 ms. Independent
  very-high review found that the diagnostic operation setter itself was
  repeatedly searching the visual tree, so that measurement tree was rejected
  rather than treated as product evidence.
- The operation setter is now O(1): it stores the label and updates only an
  already-attached panel. A newly attached panel inherits the stored label.
  Heartbeat samples of at least 30 ms retain their operation, elapsed time,
  projection generation, and interval GC pause. The unchanged raw 50 ms wall
  gate remains authoritative. The GC baseline is restarted with each heartbeat
  measurement window, including after the deliberate memory-baseline
  collection, and operation attribution ignores pre-diagnostic measures.
- The final corrected .NET 10 Release tree passed at 100,000 items:
  - raw heartbeat 50 ms, zero over-budget samples;
  - search/filter/sort P95 192.1/97.95/100.25 ms;
  - Favorite eviction 89 ms against 100 ms;
  - maximum apply slice 1 ms against the 4 ms diagnostic target;
  - maximum single-container reset 1 ms against 12 ms;
  - exact stale-peer unavailability, selection, external UI Automation,
    pending-broaden, virtualization, and 100,000-item count.
- Because the corrected measurement passed without a product allocation
  change, the proposed matched-index pool was not added. Release build,
  shared-root and shared-state contracts, right-panel selection, thumbnail
  continuity, compact/high-DPI work areas, modal interaction, Album Library,
  accessibility, enhancement workspaces, selected batch, companion path
  boundary, Public surface, and the independently recomputed legacy ledger
  all passed afterward. Source fixtures and user state remained untouched.

final result: passed

## M3 repair 6 dispatcher publication boundary

- Exact GitHub repair-5 run kept every catalog semantic, memory, and internal
  slice contract exact but failed Favorite-only eviction at 114 ms and one
  51 ms heartbeat during `search-match-2`. The corresponding UI-owned apply
  slice was 2 ms, panel reset was 2.1387 ms, and interval GC pause was zero.
- The apply pipeline had been yielding at `DispatcherPriority.Background`
  after every 0-1 ms presentation phase. Those repeated resumptions enlarged
  end-to-end wall time without reducing the measured UI-owned slice.
- Repair 6 preserves mandatory input boundaries when entering the Reset
  preparation/publication path and immediately after atomic collection
  publication. The remaining short presentation phases now share one
  cumulative slice and yield only when it reaches 2 ms. Maximum apply-slice
  diagnostics measure that cumulative slice; no threshold or test subject
  changed.
- An intermediate tree that also removed the pre-publication input boundary
  reduced Favorite eviction to 57 ms but moved a 54 ms heartbeat to
  `keyboard-stale-peer-clear`. That tree was rejected. Restoring the explicit
  pre-publication boundary kept the consolidation benefit while servicing
  pending input before WPF Reset/render work begins.
- The corrected changed tree passed the exact .NET 10 Release 100,000-item
  gate: heartbeat 50 ms with zero over-budget samples, Favorite eviction
  52 ms, search/filter/sort P95 199.95/84.8/145.75 ms, maximum cumulative
  apply slice 1 ms, maximum single-container reset 1 ms, and normalized
  working-set growth 6.045%. Pending-broaden, stale-peer, selection, external
  UI Automation, virtualization, and logical-count contracts remained exact.
- Release build remained warning 0/error 0. Source images, shared state, cache,
  history, Browser behavior, licensing, and deployment were unchanged.
- Independent very-high review returned CLEAR for candidate publication and
  exact remote CI. Because the heartbeat landed exactly on its unchanged
  50 ms ceiling, M3 closure still requires green same-SHA hosted evidence.

final result: passed

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

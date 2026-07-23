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
- The source board shows landscape filmstrip samples, while the product contract deliberately keeps a portrait-oriented horizontally virtualized filmstrip. This is an intentional product constraint, not unresolved visual drift.

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

## Follow-up polish

- P3: add optional monochrome category icons to Settings only if a licensed, consistent icon source is adopted; text navigation is currently clearer than placeholder glyphs.
- P3: the large-image consent label is accurate but slightly mechanical; a later copy-only pass may shorten it to “Allow very large images when warned.”
- P3: the adaptive Preview can become a true two-column thumbnail/details composition if hands-on testing shows that metadata must remain above the fold.
- P3: static surface opacity can be tuned after hands-on testing; blur or shaders are not required for that refinement.

final result: passed

# Aibos Image video studio design

This document records the bounded implementation slices for video direction,
source-video editing, and video finishing. The product contract and indexed
machine-readable contracts remain semantic authority. Upstream model and SDK
claims are candidate evidence until Aibos exact workflow and output canaries
pass.

## Evidence connection map

| Product intent | Current product evidence | Classification | Version 2 slice |
|---|---|---|---|
| Compile a useful video instruction instead of asking the user to know model grammar | The WPF video board already owns strict H3 prompt validation, transient rewrite, Apply/Undo, Styles, notifications, and explicit durable enqueue | Available with extension | Compile one Japanese Edit instruction from bounded source preview frames into a validated backend prompt plus Japanese summary; compilation and review remain transient until explicit Start |
| Edit the displayed source video instead of recreating a clip from isolated guide images | Current public video generation is not source-video Edit; current research identifies Bernini semantic V2V, VACE precise-mask work, and H3 masked research as distinct candidate roles | Candidate private runtime | Keep the request backend-independent, run a no-download graph/timeline preflight, and canary Bernini first for the one-prompt mask-free slice; do not call FL2VA, Ref2VA, or AddGuide V2V |
| Select a short edit inside a longer source | Existing version 1 source bounds stop near 15 seconds, while gallery playback and dropped-file display can represent longer media | Missing paired protocol | Accept a bounded five-minute source, persist an exact half-open selection of at most five seconds, and output one non-destructive managed child clip without splicing it into the source |
| Edit a managed output or one explicitly displayed regular file | Existing version 1 accepts only a succeeded managed producer id | Missing paired protocol | Add an exact managed-job/displayed-file selector union. Capture external file identity under a WPF lease, durably publish that exact request, then revalidate and stage it under a Companion lease before Job commit |
| Keep AI video finishing separate from generative editing | Existing RIFE work changes temporal sampling only and is not spatial Video SR | Missing backend | Add a separate Finish Job with semantic quality mode and explicit spatial scale, with no interpolation or implicit chaining after Edit |
| Reuse Jobs and durable enqueue | WPF already has explicit enqueue, durable inbox delivery, exact retry, cancel, deletion, and passive readers | Available with extension | Preserve one immutable source and execution snapshot, reserve managed producers or staged input, and keep health and Jobs reads side-effect-free |

## Frozen Video Tools version 1

`PV-ENHANCE-VIDEO-TOOLS-001` remains byte-for-byte legacy reader evidence.
Its `retake` and `finish` writer lanes remain production-disabled. A version 1
request or row is never rewritten or executed as version 2 `edit` or `finish`.
The dedicated version 2 verifier pins the version 1 file SHA-256 so accidental
reinterpretation or editing fails immediately.

Version 1's managed-producer-only source, short source bound, Retake planner,
and two legacy Finish modes are not defaults for new writers. They remain only
for exact reader compatibility and protected mutation behavior.

## Decisions for Video Tools version 2

- `operation` and `mediaKind` remain `video`. The exact kind is `edit` or
  `finish`; backend names do not appear in the semantic client request.
- A source selector is exactly one of:
  - `managed-video-job` with only a succeeded producer `sourceVideoJobId`;
  - `displayed-file` with the canonical request path, captured size, last-write
    time, and SHA-256 measured by WPF through a no-delete/read lease during the
    explicit Start action.
- WPF validates the bounded captured request, publishes it atomically through
  the existing durable inbox while holding the publication interlock, closes
  its lease, and only then sends the authenticated bodyless wake. After the
  committed item is claimed, the Companion independently opens and
  canonicalizes the displayed file, requires every captured value to match,
  hashes and probes the same bounded opened file, copies it to a newly
  allocated job-owned staging file, and verifies the staged copy before Job
  commit. The client path is request evidence, not retry or execution authority.
- A displayed-file mismatch, missing source, or unsupported probe after inbox
  publication returns an authenticated definitive 4xx. It creates no Job,
  process, retained staging residue, or output, and the envelope moves to
  `needs-action` after later valid items are processed. Delivery may be tried
  again only for the same captured identity. Different bytes require a new
  explicit Start and request id; the old item is never rewritten.
- Opening or hydrating the editor, ordinary seeking, editing the instruction or
  selection, toggling skip-review, and reading health or Jobs are passive. They
  do not resolve, open, hash, or probe a source; extract compiler previews;
  invoke a compiler; create a Job; publish or claim an inbox item; reserve an
  output; wake a worker; or mount a model. Exact start/middle/end compiler-frame
  extraction occurs only under an explicit preview or compiler action, never
  because a modal or context menu opened.
- For a displayed file, `フレームを読み込む` is the explicit authenticated
  preview-probe action. It may bind, canonicalize, hash, and probe the exact
  source under a no-delete/read lease and decode bounded requested thumbnails.
  It returns only a transient exact summary containing frame count, rational
  fps, duration, width, and height plus requested preview identities; it creates
  no Job, inbox item, wake, staging copy, or output. Frame controls, compile,
  and Start remain disabled until that result matches the current source. A
  managed Job may reuse its persisted exact probe. The UI never assumes 24 fps
  or derives frame count from MediaElement duration or playback position.
- Edit accepts one Japanese instruction and one selected half-open frame range.
  `指示を整える` is an explicit authenticated action that may bind,
  canonicalize, hash, and probe the exact selected source under a no-delete/read
  lease, extract exact start/middle/end preview identities, and launch the
  bounded local compiler. It still creates no Job, durable inbox item, wake, or
  output. The transient candidate contains a backend prompt, Japanese summary,
  compiler revision, and context digest binding that exact source, selection,
  ordered preview identities, instruction, and compiled texts.
- A candidate becomes stale when source, selection, instruction, compiler
  revision, or a preview identity changes. With review enabled, successful
  compilation only displays and announces the candidate; a separate explicit
  Start is required. With skip-review checked, the compiler click carries one
  transient single-use authorization to compile, perform final revalidation,
  and then publish. The compiler response alone never starts work. Before
  either path publishes, Start recaptures the same source and preview identities
  and requires the same current context digest; drift fails without publication.
- Edit requires an explicit `preserve` or `mute` audio policy and one of the
  required maximum-pixel tiers. STEP is an integer from 1 through 40. Strength
  is an integer from 10 through 100 and is mapped by the pinned server planner;
  a client cannot send seed, sampler denoise, backend, model context, mask,
  actual affected range, delivery, or source snapshot.
- Initial Edit accepts exact 24/1-, 30/1-, or 60/1-fps source and a selected
  interval no longer than 5,000 milliseconds or 300 source frames. Planning
  persists exact selected frames and PTS, the resolved backend fps and frame
  map, internal frame count, alignment padding such as `4n+1`, delivery crop
  and source-fps reconstruction, strength mapping, seed, model canvas, selected
  audio-policy plan, and workflow/model/runtime/timeline/delivery receipts.
- Backend 16-fps or alignment needs are server-owned and never appear in or
  rewrite the request. The UI shows the exact source selection and resulting
  child-clip duration; internal padding and crop are diagnostics.
- Edit outputs one new non-destructive managed child clip for the selected
  interval. It includes no long-source prefix or suffix and performs no source
  splice or overwrite. The delivery reconstructs the selected frame count,
  rational fps, relative PTS, and duration. Generated audio is discarded.
  `preserve` remuxes only a non-empty intersecting encoded source packet range;
  no intersecting packet produces no audio stream and no range identity.
  `mute` produces no audio stream, synthesized silence, or fabricated zero-range
  identity. Neither policy makes a sample-exact trim claim.
- Finish is a separate Job and is never implicitly chained after Edit. Its
  public mode is `fast`, `standard`, or `quality`; scale is explicit 2x or 4x.
  Each mode independently advertises and persists its resolved backend,
  setting, supported scales, source bounds, delivery mapping, and temporal
  canary receipts. No backend or mode silently falls back to another. Initial
  output is 8-bit SDR and preserves source frame count, rational fps, PTS,
  duration, and encoded audio packets. It does not interpolate, convert frame
  rate, crop, or silently reduce scale.
- Edit and Finish share only the typed video Job, source, durable inbox, queue,
  idempotency, output-root, and lifecycle infrastructure. Their request schema,
  capability, receipts, backend candidates, plan, and delivery are separate;
  readiness or a receipt never crosses the feature boundary.
- Unknown, malformed, mixed-version, and unsupported-future request or row
  shapes are preserved reader-only. Exact retry reuses the Job snapshot, Edit
  seed, source identities, hashes, and staged copy. Cancel, cleanup, delete,
  and publish fail closed when ownership is ambiguous. External originals are
  never changed or deleted.

## Candidate backends and claim boundary

### Edit

The current one-Japanese-prompt request has no spatial mask. Its first semantic
V2V canary is `bernini-r-1.3b-edit-candidate-v1`. The precise-mask role is
`wan-vace-1.3b-edit-candidate-v1`, but it cannot become ready until a separate
exact manual-mask or auto-mask plus preview contract exists. The
`minimax-h3-masked-edit-research-v1` path remains research-only. No candidate is
declared the standard winner.

FL2VA remains image-to-video generation, Ref2VA remains reference-conditioned
generation, and AddGuide remains guided generation. They are not aliases or
silent fallbacks for source-video Edit. Qwen-Video-Edit and JoyAI-Video-Edit are
future-only.

Canary zero downloads no model. It verifies exact Comfy `object_info` and graph
input schema, inventories already present artifacts without inferring
readiness, and runs a synthetic source-frame, PTS, backend map, pad, crop, and
selected-audio mapper. Shared UMT5/VAE receipts and an incremental Bernini
canary follow only after that preflight. Production writer readiness remains
false until exact model, workflow, instruction, timeline, resource,
cancellation, recovery, and child-clip output canaries pass.

### Finish

The public fast/standard/quality mode does not encode a backend name. Exact
capability receipts independently map a mode and scale to one candidate family:

| Semantic role | Candidate | Additional closed gate |
|---|---|---|
| faithful | `nvidia-vfx-vsr-1.2-candidate-v1`, package `nvidia-vfx 0.1.0.1`, internal SDK `1.2.0.0` | Server setting is `MEDIUM`, `HIGH`, or `ULTRA`. Frame independence is not claimed; scene-cut reset or effect recreation must pass. FlashDreams revision `e580e27d408b3cf8bd8a549f990c361b94d3379f` is integration evidence only. |
| generative-detail | `seedvr2-3b-detail-candidate-v1` | The UI mapping must make synthesized-detail behavior explicit. Source fidelity, texture generation, bounded VRAM, cancellation, and scene boundaries require canaries. |
| lightweight-4x | `nanovsr-1.7m-4x-candidate-v1` | Native scale is 4x. Its reported bidirectional recurrent T=15 disjoint-chunk demonstration is not seamless evidence; Aibos must pin overlap/crop and pass chunk-seam canaries. A 2x mapping is unsupported until separately canaried. |

Production readiness and every quality claim remain false until each mode's
exact artifact/package, Windows runner, driver/GPU, memory, cancellation,
timeline/audio, temporal-state, delivery, and visual A/B canaries pass.
Scene-boundary behavior is never inferred from SDK or model descriptions.
Failure keeps that exact mode disabled and does not fall back to another
semantic role, backend, interpolation, or per-frame still Enhancement. A
candidate ID never implies a default mode.

## Bounded source and output policy

Both lanes accept one regular MP4 with exactly one video stream, no more than
one audio stream, at most 512 MiB, 300,000 milliseconds, 1920 x 1080,
2,073,600 source pixels, and 18,000 frames. Edit and Finish initially accept
exact 24/1, 30/1, or 60/1 fps. An Edit selection is additionally limited to
5,000 milliseconds and 300 source frames. Candidate-specific source limits are
advertised by that candidate's exact capability and never inferred from the
public mode or silently substituted by another backend.

Edit model canvases use one of the existing 230,400-, 307,200-, or
414,720-pixel tiers and preserve source aspect without an implicit crop. Finish
scale is exact 2x or 4x. Exact multiplication must stay within 3840 x 2160 and
8,294,400 output pixels; otherwise validation rejects the request rather than
cropping, downscaling, or selecting a different scale.

Media handles, probe output, hashing, staging, scratch, process execution,
publication, and cleanup are all bounded and timed. A managed producer is
reserved when WPF atomically publishes the child inbox item under the shared
Jobs interlock. An external staging copy is created only while the Companion
dispatches a committed item, then remains job-owned and pinned until every
active, durable, retry, cancel, recovery, and publication dependency is gone.

## UI behavior for the first slice

- A single dropped video becomes the current enlarged media item through the
  same user expectation as a single dropped image, without treating it as a
  still image.
- Enlarged video exposes separate `動画編集` and `AI動画高画質化` actions.
  Neither action starts work merely by opening its modal.
- Edit provides precise start and end frame controls, seeks the player to each
  boundary, and exposes explicit bounded start/middle/end preview actions. It
  labels the exact user-selected range and resulting child-clip duration.
  Server-owned backend padding and delivery crop remain diagnostics rather than
  a second editable range.
- Displayed-file frame controls, compile, and Start show a disabled reason until
  the current `フレームを読み込む` result exists; any source-identity drift clears
  that transient result. Managed Jobs may enable the controls from their exact
  persisted producer probe.
- The Japanese instruction is the only authoring field in the main surface.
  Compiled backend text and Japanese summary appear in a review area after the
  bounded local compile step. A notification announces completion. When
  skip-review was checked for that compiler click, its one-shot continuation
  may publish only after all source, preview, compiler, request, and current
  capability checks succeed.
- Finish shows input and exact output dimensions, mode, scale, timing/audio
  preservation, and unsupported-input reason before Start. It does not offer
  frame interpolation as a quality option.

### Application-surface integration checklist

The Edit vertical slice is complete only after the same typed state and actions
are represented across all of these public WPF surfaces. Finish then extends
that proven integration shape with its own separate request, capability,
receipt, plan, and delivery types; a private backend alone is not completion.

- The enlarged-video modal toolbar and modal context menu expose Edit and
  Finish as separate explicit actions, with capability-disabled reasons and no
  work started by opening either menu.
- Jobs can filter each operation, render its durable state and needs-action
  reason, expose only ownership-safe retry/cancel/publish/delete actions, and
  open the managed child-clip or finished output without treating the external
  source as owned output.
- Settings carry bounded defaults and saved styles per feature, including a
  visible Edit audio-policy default. Edit's skip-review choice is transient UI
  policy and never becomes durable generation semantics; compiled prompt
  provenance remains Job data.
- Gallery filtering, sorting, version selection, and source/output ownership
  distinguish an Edit child clip, a Finish output, a managed source, and an
  external displayed file without deleting or rewriting the source.
- Japanese and English labels, keyboard focus/order, accessible names,
  disabled reasons, range announcements, and notification text cover every new
  action and state.
- Modal hydration, gallery/Jobs reads, capability reads, sorting, filtering,
  selection, preview seeking, and context-menu opening remain passive: they do
  not probe media, hash or stage a source, mutate Job/inbox state, launch a
  process, load a model, enqueue, wake, claim, or retry work.

The frame-range controls may later be reused by a separately versioned non-AI
trim/export operation. That operation is not an Edit or Finish kind, and its
writer, output ownership, frame delivery, and audio-boundary contract cannot be
inferred from Video Tools v2 readiness.

## Slice order and acceptance

1. Contract and reader: publish version 2 request, health, source, immutable
   snapshot, lifecycle, fixture, durable-inbox result, and closed-writer rules
   while leaving version 1 unchanged.
2. Source surface: enlarge one dropped video, add the explicit transient
   preview-probe and precise trim preview, capture displayed-file identity only
   on explicit action, never infer frame metadata from MediaElement, and keep
   every passive path free of filesystem and durable side effects.
3. Prompt compiler: bounded preview context, Japanese instruction, compiled
   prompt and Japanese summary, stale-context rejection, notification,
   confirmation, and transient skip-review policy.
4. Durable writer foundation: strict parser, managed dependency or committed
   displayed-file request plus verified staging, immutable snapshots, exact
   retry, needs-action rejection, cancel/delete guards, idempotent inbox
   publication, owned process cancellation, and recovery.
5. Edit canary: run the no-download graph, artifact-inventory, and synthetic
   timeline/audio mapper first; then seal the Bernini semantic one-prompt path.
   VACE precise-mask readiness waits for its separate exact mask-and-preview
   contract, and H3 masked remains research-only. Verify child-clip timeline and
   audio delivery, visual A/B, resource bounds, and paired writer activation.
6. Finish canaries: independently seal the NVIDIA faithful, SeedVR2
   generative-detail, and NanoVSR lightweight-4x candidates. No candidate ID
   implies fast, standard, quality, or a default. Verify exact supported
   scale/source bounds, mode/backend/delivery mapping, chunk and scene-cut
   policy, timing/audio preservation, visual A/B, and per-mode writer
   activation without silent fallback.
7. Verify with synthetic TEMP fixtures against exact public/private revisions,
   focused tests, Release WPF build, real UI smoke on synthetic media, and an
   independent review before paired rollout.

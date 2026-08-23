# Aibos Image video studio design

This document records the first bounded implementation slices for video
direction, video-to-video editing, and video finishing. The product contract
and the machine-readable contracts remain the semantic authority.

## Evidence connection map

| Research hypothesis | Current product evidence | Classification | First slice |
|---|---|---|---|
| Compile a structured motion plan instead of asking an AI to invent the final prompt | The WPF video board already owns the H3 prompt, strict prompt grammar, transient rewrite candidate, Apply/Undo, Styles, and explicit durable enqueue | Available | Add a deterministic, transient Motion Director compiler that targets the existing H3 prompt and never enqueues by itself |
| Treat AI analysis as advice rather than authority | H3 rewrite already pins the source, validates the response, rejects stale context, and leaves the authoritative prompt unchanged until Apply | Available | Keep manual planning authoritative; a later analyzer may only propose bounded catalog values |
| Retake a selected interval instead of presenting all V2V work as one feature | Current video v2 accepts only a still source; managed MP4 files are outputs, not generation inputs | Missing paired protocol | Add a reader-first `operation=video` Video Tools snapshot whose source is a succeeded managed video producer job |
| Use first/last anchors for a bounded Retake | The pinned H3 runtime currently proves first-frame I2V only. Newer upstream node shapes are not proof for the sealed Aibos runtime | Private runtime dependency | Persist the selected and actual legal H3 windows and both anchor frame indices, but keep the writer closed until the exact runtime, workflow, and GPU/output canaries are sealed |
| Use a temporal video restoration model for higher quality | Existing RIFE work changes temporal sampling only and is not spatial video super-resolution | Missing backend | Add a separate Finish snapshot and capability. Preserve timing and audio; never label interpolation or per-frame still upscaling as Video SR |
| Reuse Jobs and durable enqueue | WPF already has explicit enqueue, durable inbox delivery, exact retry, cancel, deletion, and passive readers | Available with extension | Keep `operation=video`, use the managed producer job ID as source authority, and extend dependency protection to video chains |

## Decisions for Video Tools v1

- `operation` remains `video`; a new additive discriminator separates
  `retake` and `finish` from existing Wan and H3 I2V snapshots.
- The client selects a succeeded managed video by producer job ID. It does not
  provide an arbitrary source path.
- The server persists the canonical managed source path, file signature,
  SHA-256, bounded media probe, and producer dependency before a job can run.
- Retake v1 accepts only an exact, succeeded H3 v2 managed output at 24 fps.
  The selected interval is user intent; the actual interval is the smallest
  legal H3 frame count (`124`, `243`, `294`, or `362`) that covers it, centered
  where possible and clamped to the source.
- Finish v1 is spatial, temporally coherent restoration. It preserves frame
  count, frame rate, duration, and at most one audio stream. Its semantic modes
  are `faithful` and `detail`; the exact pinned backend remains server-owned.
- Both features start reader-first. A writer stays closed until its runtime,
  workflow/runner, model and license receipt, GPU canary, output media canary,
  cancellation, cleanup, and recovery contracts all pass.
- Passive health and Jobs reads never reserve, enqueue, wake, claim, retry, or
  start a worker. Only the explicit Start action may publish a durable item.
- Unknown or malformed snapshots are preserved and reader-only. Retrying uses
  the exact persisted snapshot and revalidates the current managed source.
- A managed source video cannot be deleted while a queued/running child or a
  committed durable inbox item depends on it. Deleting an output never deletes
  its source video or original image.

## Bounded source and output policy

Video Tools v1 accepts one regular managed MP4 with one video stream, no more
than one audio stream, at most 512 MiB and 15.1 seconds. Input is limited to
450 frames and a 1920 x 1080 pixel-area ceiling. A Finish plan has a fixed 2x
spatial scale and an 8,294,400-pixel output ceiling. Media probing, hashing,
frame extraction, execution, publication, and cleanup all require explicit
timeouts and scratch quotas.

## Slice order and acceptance

1. Motion Director: deterministic plan, exact frame coverage, warnings,
   transient preview, Apply/Undo, and no HTTP or durable side effect.
2. Video Tools reader: paired contract, exact health gate, managed video source
   selection, selected-versus-actual Retake preview, and Finish mode preview.
3. Companion writer foundations: strict request parser, immutable snapshots,
   source pin/probe, dependency protection, exact retry/cancel/delete behavior,
   and dependency-injected synthetic execution.
4. Runtime enablement: seal and canary a first/last-frame H3 Retake workflow and
   a dedicated temporal Video SR runner. This step must not be inferred from an
   installed file or an upstream feature claim.
5. Verify with synthetic TEMP fixtures, focused WPF/Companion tests, Release
   WPF build, visual smoke, and an independent review before paired rollout.

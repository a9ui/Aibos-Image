# Aibos Gallery Fold brand candidate

This directory contains the user-selected Gallery Fold direction for Aibos
Image. It is a local production candidate, not a trademark or legal clearance.

- The visible direction was selected by the user on 2026-07-24.
- OpenAI ImageGen produced the isolated large, small, and Windows-shell raster
  sources from that selected direction.
- A PRO review retained the direction and required dedicated 16-24 px optical
  masters, wider negative seams, no shell outline at 16/20 px, and a shell
  outline from 32 px upward.
- `scripts/build-wpf-brand-assets.ps1` deterministically derives the PNG and
  multi-frame ICO resources in `Generated/`.
- The WPF runtime embeds only the 20, 24, and 64 px mark PNGs plus the ICO. It
  performs no brand-asset file or network I/O at runtime.
- The existing text label remains the wordmark. No generated contact-sheet
  lettering or unapproved font is embedded.

Do not change the repository license or treat these files as trademark
clearance. Human approval and name/mark clearance remain release gates.

## Savepoint review

PRO reviewed exact implementation commit
`5d45088d9584a39852c6702354fe444aca7b939a` against the selected direction and
returned `CLEAR` with zero Critical/High blockers. The raster pack is accepted
as the pre-refactor, user-touchable brand savepoint because no authoritative
vector or Figma master exists and speculative tracing would be less faithful.

Deferred, non-blocking follow-ups are High Contrast mono-ICO switching,
approved vector or 2x-DPI masters, and human Windows review at 100%, 125%,
150%, and 200% scaling. Trademark, name-clearance, publication provenance, and
commercial-release approval remain open human gates.

The durable engineering decision and local acceptance evidence are recorded in
the repository's design QA document; no authenticated review-session reference
is part of the public source.

# e2e

Playwright-based end-to-end specs live here. The default configuration (`playwright.config.ts`) points to this directory and launches the local development server automatically, so you only need to ensure dependencies are installed.

## Commands

- `corepack pnpm test:e2e` &mdash; run the full Playwright suite once.
- `corepack pnpm exec playwright codegen http://127.0.0.1:3000` &mdash; record new scenarios.

`home.spec.ts` covers the Aibos landing workflow and last folder set restoration. Add narrower specs for viewer interactions when a stable fixture folder is available.

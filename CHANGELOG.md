# Changelog

## Phase 1: Skeleton and CI (2026-10-13)

- `Majorsilence.Recital.slnx` with central package management, Majorsilence.Forms pinned at 26.11.4.
- Libraries `Majorsilence.Recital.Core`, `.Epub`, `.Layout`, `.Storage`, `.Tts` (empty for now) and `.UI` (`MainForm`).
- Avalonia hosts: `Majorsilence.Recital.Desktop`, `.Android`, `.iOS`, `.Browser`.
- `Majorsilence.Recital.UI.Tests`: renders `MainForm` on the Headless backend and pixel-diffs it against a baseline.
- GitHub Actions: build + test on Windows/macOS/Linux, plus Android, iOS and WASM head builds.
- Docs: plan, decisions, framework gaps (GAP-001 iOS package asset, GAP-002 pixel-diff helper, GAP-003 host-dependent Headless renders, GAP-004 `Theme.UIFont` family-name-only), coverage, sync format draft.

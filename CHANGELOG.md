# Changelog

## Phase 6: Annotations (2026-10-17)

- Highlights in five colours, notes on highlights, and bookmarks, anchored by CFI and synced per device
  with tombstones. A deletion is final, even against a stale device's later edit (D-022).
- Reader: hold then drag (or long-press) to select; a highlight bar to colour, note, copy or delete;
  tapping a highlight to change it; a Bookmark toolbar button with a ribbon on bookmarked pages; a
  Notes panel grouped by chapter. Export to Markdown as a file on the desktop, or Copy all anywhere
  (GAP-012). Highlights and notes made on another device appear in the open book when they sync.
- Layout: `LayoutPage.HitTest`, `WordAt`, `TextRects` and `TextLine.Extent`; `PageRenderer` draws marks
  under the text. Epub: `ContentDocument.GetText` for a range.
- Tests: highlights keep their words at another page size (10%, 50% and 90% into three books);
  merge, delete-is-final and Markdown export; the sync simulation now also adds, changes and deletes
  annotations, including stale edits of deleted ones (a newest-wins merge without delete-wins fails it);
  a stale device reconnecting after a deletion; UI tests for drag-select, drag-to-turn, long press,
  notes, delete, bookmarks and export, with a new pixel baseline (`Reader.Annotations`).

## Phase 5: Sync engine and local sync folder (2026-10-17)

- `SyncEngine` (`Majorsilence.Recital.Core.Sync`): a local copy of the sync home in app data, a
  persistent outbox, push then pull rounds, change detection by change feed or by listing and ETag, and
  conflicted-copy tolerance. Rounds run every 30 s, soon after each write, and with exponential backoff
  while offline. A sync home from a newer format is left alone (D-018, docs/SYNC_FORMAT.md).
- The library on the desktop can sync through any folder (Dropbox, Drive, OneDrive, iCloud Drive or a
  network share): Sync > Choose sync folder… in the library, with the sync state in the status bar and
  the library refreshing when another device's changes arrive. An existing local library is uploaded
  the first time; Stop syncing keeps the local copy.
- `LocalFolderStorageProvider`: content-hash ETags for files up to 256 KB (D-019), names no logical
  path can hold are skipped rather than failing a listing, and `requireRoot` treats a missing folder as
  offline instead of creating it.
- Fixed: after a restart, a device with a slow clock could save a position that lost to one it had
  already read past (D-020).
- Tests: `Majorsilence.Recital.Sync.Tests`, the plan's acceptance simulation (three devices, random
  interleavings, offline periods, flaky networks, restarts, ±10 min clock skew, conflicted and stale
  copies, a 2 s file-system clock; 1,000 seeds in CI, D-021) and 34 engine tests; Core 52 (LocalFolder
  ETags), UI 15 (choosing a sync folder picks up another device's book).

## Before Phase 5

- Majorsilence.Forms 26.11.6, with the fixes for GAP-002 to GAP-011. Every Recital workaround is gone:
  - Library import reads picked files as streams (`OpenFileDialog.SelectedFiles`), so Android, iOS and the
    browser can import books the platform gives no path.
  - The reader collapses the split's side panel instead of moving the page out of it; toolbars draw
    item colours and drop-down arrows without a custom renderer.
  - UI tests use `HeadlessRenderer.UseDeterministic`, `UseSynchronizationContext` and `PixelComparison`
    (a mismatched baseline also writes a `*.diff.png`, which CI uploads), and a swipe test covers
    `BookPageView`'s gestures.
- README screenshots, rendered by an opt-in test (`ScreenshotTests`) from Project Gutenberg books.
- Test corpus: Project Gutenberg's Frankenstein (#84), The Adventures of Sherlock Holmes (#1661) and Moby-Dick (#2701),
  in the corpus-wide parse, CFI and layout tests. Gutenberg is now the preferred source for test books.
- Fixed: a contents entry whose heading starts a page with a forced break (Gutenberg breaks before every `h2`)
  opened on the last page before it. `ChapterLayout.PageOf` puts a position between two pages on the next one.
- Majorsilence.Forms pinned at 26.11.5, whose `Majorsilence.Forms.Avalonia` package has the `net10.0-ios`
  asset: the iOS head now builds against the real iOS backend (GAP-001 closed).

## Phase 4: Reader UI, local only (2026-10-14)

- `Majorsilence.Recital.Storage`: `IStorageProvider` (plan 2.4) with `InMemory` and `LocalFolder`
  providers (atomic temp-file writes, ETags, a change feed or a polled listing).
- `Majorsilence.Recital.Core`:
  - Hybrid logical clocks and the sync-folder files (docs/SYNC_FORMAT.md).
  - `LibraryService`: content-addressed import (duplicates found by hash), DRM and non-EPUB refusal,
    cover thumbnails, per-device progress and settings files with HLC merges.
  - Library search and sort; reader preferences and the four themes.
  - `ReadingSession`: page turns across chapters, contents navigation, and a CFI-anchored position that
    survives any resize or typography change (D-016).
- `Majorsilence.Recital.UI`: library (owner-drawn cover grid or details list, search, sort, import from
  the file picker or by drag and drop), `BookPageView` (tap zones, drag, swipe, wheel, keys), the reader
  (contents tree, typography panel, theme menu, status bar), phone and wide layouts, and themes (D-017).
  Progress is saved locally in the sync-folder format under app data.
- Layout: dark themes apply the book's `prefers-color-scheme: dark` rules, images honour
  `filter: invert()`, and CSS properties AngleSharp.Css has no model for are kept for the cascade.
- Epub: character offsets into a chapter (the locator fallback) and resource sizes.
- Tests: Core 48, UI 12 (the plan's acceptance flow: open, ten page turns, theme switch and two resizes
  with the position checked by CFI each time; tap zones; drag and wheel; contents; typography; reopen
  at the saved place; library search, picker import and double-click open; four pixel baselines).
- Framework: GAP-005 to GAP-011, four of them fixed on local Majorsilence.Forms branches.

## Phase 3: Layout engine (2026-10-14)

- `Majorsilence.Recital.Layout`: XHTML + CSS → styled box tree → shaped lines → pages, deterministic on every OS (D-011).
  - Cascade over the user-agent sheet, the book's sheets (with `@import`, `@media`, `@supports`,
    `@font-face`) and inline styles, using AngleSharp.Css for parsing and selector matching. Reader
    overrides cover font size, family, line height and justification (D-012).
  - Text is shaped with HarfBuzz from bundled DejaVu fonts or the book's own fonts, with font fallback.
    Line breaking, justification, indents, soft hyphens, nowrap/pre, small caps, super/subscript and
    list markers are our own (D-010).
  - Pagination honours widows, orphans, keep-with-next headings, `break-inside: avoid`, forced breaks,
    margin truncation at unforced breaks, image scaling to the page, and atomic table rows.
  - Layout ↔ DOM ↔ CFI: every line maps back to DOM positions; `ChapterLayout.PageOf` finds the page
    for a position or a CFI; `TextLine.HitTest` finds the position under a point.
  - `PageRenderer` draws pages with Skia (for the reader view in Phase 4, and for tests).
- Tests (69): cascade, line layout, pagination, a golden box-tree snapshot, golden page-break snapshots
  for *Alice* and the opening of the Standard Ebooks *Pride and Prejudice* at phone, tablet and desktop
  sizes, CFI round trips at 10/50/90% across viewports for all four books, layout invariants on every
  page of every book, and the plan's timing target (D-014).
- Bugs found along the way: AngleSharp compound selectors are enumerable, and splitting them as if they
  were selector lists made `div.x` match every `div`; a segmentation bug split every lowercase letter
  into its own shaping call, which lost kerning and ligatures and cost 0.5 s per book; a GC-movable
  font buffer gave HarfBuzz garbage glyphs.

## Phase 2: EPUB model (2026-10-14)

- `Majorsilence.Recital.Epub`: opens EPUB 2 and 3 from a path or any stream, async throughout. Parses the container, the OPF
  (metadata with EPUB 3 refinements and EPUB 2 attributes, manifest, spine with fallbacks and
  `linear`), the EPUB 3 nav or EPUB 2 NCX table of contents, the cover (four detection strategies),
  embedded fonts (IDPF and Adobe de-obfuscation) and DRM. Problems become `Warnings`, not failures.
- Content documents load as AngleSharp HTML documents with the exact source XML tree (D-008).
- `EpubCfi`: parse, format, compare, points and ranges, id/text/side-bias assertions; generate from
  and resolve to DOM positions (D-009).
- Test corpus `tests/corpus/`: two Standard Ebooks (EPUB 3), two Project Gutenberg (EPUB 3 and EPUB 2).
  CFIs round-trip on all 5,788 paragraphs (23,007 positions). Hand-built edge cases cover missing
  nav, NCX only, no TOC, broken manifests, no container, case and percent-encoding in hrefs, named
  entities, malformed XHTML, windows-1252, metadata refinements, cover variants, obfuscated fonts,
  DRM, stale CFIs and a 20,000-paragraph chapter.
- Framework: none of this phase touches Majorsilence.Forms, so there are no new gaps and no coverage changes.

## Phase 1: Skeleton and CI (2026-10-13)

- `Majorsilence.Recital.slnx` with central package management, Majorsilence.Forms pinned at 26.11.4.
- Libraries `Majorsilence.Recital.Core`, `.Epub`, `.Layout`, `.Storage`, `.Tts` (empty for now) and `.UI` (`MainForm`).
- Avalonia hosts: `Majorsilence.Recital.Desktop`, `.Android`, `.iOS`, `.Browser`.
- `Majorsilence.Recital.UI.Tests`: renders `MainForm` on the Headless backend and pixel-diffs it against a baseline.
- GitHub Actions: build + test on Windows/macOS/Linux, plus Android, iOS and WASM head builds.
- Docs: plan, decisions, framework gaps (GAP-001 iOS package asset, GAP-002 pixel-diff helper, GAP-003 host-dependent Headless renders, GAP-004 `Theme.UIFont` family-name-only), coverage, sync format draft.

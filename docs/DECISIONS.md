# Decisions

One short entry per non-obvious choice. Newest last.

## D-001: Project names are `Majorsilence.Recital.*`; the app is called Recital (2026-10-13)

The plan's `Reader.*` working names become `Majorsilence.Recital.<Area>` (`Majorsilence.Recital.Core`,
`.Epub`, `.Layout`, ...), with tests as `Majorsilence.Recital.<Area>.Tests`. This matches how
Majorsilence.Forms (`Majorsilence.Forms.*`) and Reporting (`Majorsilence.Reporting.*`, `Majorsilence.Pdf.*`)
are named, and keeps every package under the reserved `Majorsilence.*` NuGet ID prefix. Assemblies,
root namespaces and package IDs all use the same name. This settles open question 5: the app is
**Recital**, and the sync folder is `<SyncRoot>/Recital/`.

## D-002: Avalonia is the only interactive host (2026-10-13)

Hosts are the Majorsilence.Forms Avalonia backend on desktop (Windows/macOS/Linux), Android, iOS and
the browser. The plan's `Desktop.Uno` and `Desktop.Gtk4` second-/third-backend heads are dropped.
Framework coverage beyond Avalonia comes from the Headless backend (all UI tests) and the stretch
Terminal host. Per-backend columns in `FRAMEWORK_COVERAGE.md` cover only those backends.

## D-003: Framework fixes go to a Majorsilence.Forms branch, not only a patch note (2026-10-13)

Plan 0.2 step 3 says to write proposed fixes up in `docs/framework-patches/<id>.md`. When the fix is
clear, make it instead in a `git worktree` of Majorsilence.Forms on a new branch, and link that branch
from the gap entry. Recital still consumes the framework only as a pinned NuGet package, never as a
project reference or a vendored copy, so a fix reaches Recital when a framework release ships it and
the pin is bumped.

## D-004: Projects are created in the phase that first needs them (2026-10-13)

Phase 1 creates the core libraries (`Core`, `Epub`, `Layout`, `Storage`, `Tts`, `UI`), the four
Avalonia hosts and `UI.Tests`. Storage providers, TTS engines and the other test projects are added
in their own phases rather than as empty shells now.

## D-005: Warnings as errors (2026-10-13)

`src/` treats warnings as errors in every configuration (plan 0.3). `hosts/` and `tests/` follow the
Majorsilence.Forms rule, errors in Release only, which is what CI builds.

## D-006: Pixel baselines are per OS, with a tight tolerance (2026-10-13)

Headless renders are compared to PNGs under `tests/Majorsilence.Recital.UI.Tests/Baselines/`, one per
OS (`<name>.linux.png`, `.windows.png`, `.macos.png`). Even with the same font file, each OS's Skia
anti-aliases text differently: about 200 pixels on the title bar alone. The comparison allows a
per-channel difference of 8 and up to 0.01% of pixels differing. A looser 0.1% let a one-character
title change (about 380 pixels at 1000x700) pass.

Every UI test starts with `HeadlessTestEnvironment.Use ()`. It forces custom window chrome and
registers the framework's embedded Noto Sans as the UI font (GAP-003, GAP-004), so a render depends on
neither the machine's installed fonts nor, on macOS, the native title bar. With that, a local Ubuntu
26.04 render matches the CI Ubuntu 24.04 baseline.

The Windows and macOS baselines come from CI: a missing or mismatched baseline fails the test and
writes `<name>.<os>.actual.png` beside the test assembly, which CI uploads as an artifact. Review it,
then commit it as the baseline, or regenerate locally on that OS with `RECITAL_UPDATE_BASELINES=1 dotnet test`.
CI runner images are pinned (`ubuntu-24.04`, `windows-2025`, `macos-26`), so an image change shows up as a
deliberate baseline update and never as a surprise failure.

## D-007: A small EPUB parser of our own, not VersOne.Epub (2026-10-14)

Plan 1.2 allows either. VersOne.Epub (Unlicense) parses packages well, but hands content documents
back as strings, so Recital would parse every chapter a second time anyway, and CFIs depend on the tree
that second parse builds. The package side is small (`System.IO.Compression` plus `System.Xml.Linq`),
and owning it buys: forgiving opens that record `EpubWarning`s instead of failing (missing files,
duplicate ids, bad case, no container), font de-obfuscation (IDPF and Adobe), DRM detection, and async
I/O throughout (`ZipArchive.CreateAsync`). About 2,000 lines including comments.

## D-008: Content documents are parsed as XML, then rebuilt as an AngleSharp HTML document (2026-10-14)

CFIs count element and text children, so the tree must be exactly the source XML. AngleSharp's HTML
parser is not: `<a id="x"/>` swallowed the next paragraph in a probe. AngleSharp.Xml gets the
structure right but builds generic XML elements, which AngleSharp.Css (Phase 3) cannot style as HTML. So
`XhtmlLoader` parses with AngleSharp.Xml and replays that tree through an HTML document's DOM API,
which yields real `IHtmlElement`/`ISvgElement` types, keeps `epub:type` and other namespaced
attributes, and keeps the XML tree.

Real books use HTML named entities (`&nbsp;`) that strict XML rejects, so they are rewritten as numeric
references first. A document that is still not well-formed goes through the HTML parser with an
`xhtml-not-well-formed` warning and `ContentDocument.ParsedAsXml = false`. CFIs inside it are
consistent within Recital but may not match other readers'. All four corpus books parse as XML.

## D-009: CFI conventions (2026-10-14)

- Character offsets count UTF-16 code units, the DOM's unit, across a whole text chunk, which can
  span several text nodes split by comments or CDATA. A boundary offset resolves to the end of the
  earlier node.
- When a step's id assertion disagrees with its index, the id wins, for content steps and for the
  itemref step alike. The document changed, and the id is the more stable of the two.
- An offset past the end of its text is clamped, not rejected: a CFI saved against an older copy of
  a book should still land in the right paragraph.
- Itemref steps count the itemref's position in the source spine, including itemrefs Recital skipped
  as broken, so a CFI means the same thing to every reader.
- Equality and ordering ignore assertions; ordering is document order (an element sorts before
  anything inside it). Temporal (`~`) and spatial (`@`) offsets are rejected; EPUB reflowable text
  has no use for them.

## D-010: Shape text with HarfBuzz directly; line breaking and justification are ours (2026-10-14)

Plan 1.2 names Topten.RichTextKit for shaping and line layout. Its `TextAlignment` has no `Justify`, it
has no first-line indent, and its line breaker and shaper are internal. Justified text and
`text-indent` are basic e-reader requirements, so `Majorsilence.Recital.Layout` shapes with HarfBuzzSharp (the
engine RichTextKit uses underneath, already a framework dependency) and does its own:

- line breaking: greedy first-fit over simplified UAX #14 opportunities (spaces, hyphens and dashes
  inside words, soft hyphens with a visible hyphen, zero-width spaces, CJK), with nowrap/pre honoured
  and an emergency break for words longer than the line;
- justification (extra space shared between the spaces of every line but the last);
- the CSS inline box model for line heights (half-leading per inline box, plus the block's strut).

## D-011: Layout is deterministic: bundled fonts, metrics from font tables (2026-10-14)

Golden page-break snapshots must match on Linux, Windows and macOS, so nothing in layout consults
the platform:

- Fonts are the bundled DejaVu Serif/Sans/Sans Mono, plus the book's own `@font-face` fonts. Installed
  fonts are never used, for fallback either. DejaVu's license (Bitstream Vera, with DejaVu changes in
  the public domain) is permissive. OFL fonts such as Literata or Noto would read better, but OFL has
  copyleft-like terms and plan 0.1 says to ask first, so that is an open question for the user.
- Glyph advances, kerning and ligatures come from HarfBuzz reading the font file's own tables (the
  OpenType font functions, not Skia's or FreeType's). Ascent and descent come from the same tables.
  Skia is used only to draw, and to read image headers.
- The font bytes live in unmanaged memory the HarfBuzz blob owns. `Blob.FromStream` left HarfBuzz
  reading memory the GC could move, and shaping returned garbage glyph ids after a collection.

## D-012: The reader's font size scales the book's absolute lengths (2026-10-14)

`LayoutSettings.FontSize` is what CSS `medium` (and `1rem`) means, and every absolute length in book CSS
(px, pt, cm, ...) is multiplied by `FontSize / 16`. Many books pin `body { font-size: 12pt }`, and
without this scaling the reader's size control would do nothing for them. Proportions inside the
book's design are kept.

## D-013: Layout scope and known limitations (2026-10-14)

Supported, per plan 1.3: block and inline flow, margins (with collapsing) and padding, top/bottom
borders (rules), `text-align`, `text-indent` (including hanging), `font-family/size/weight/style`,
small caps (synthesised: lowercase letters as 75%-size capitals), `line-height`, `letter-/word-spacing`,
`text-transform`, `white-space`, `vertical-align: super/sub`, lists with markers, images and SVG-wrapped
images, simple tables, `page-break-*` / `break-*`, widows and orphans, keep-with-next for headings,
embedded fonts and media queries.

Not supported yet, and how each degrades:
- No hyphenation dictionary: justified narrow columns can have loose lines. Soft hyphens in the
  text are honoured.
- Left-to-right text only, with no bidi; RTL books render in logical order.
- Floats are laid out as blocks. Inline images are pulled out as their own block.
- Tables use equal-width columns and ignore colspan; each row stays on one page.
- Inline SVG without a raster `<image>` is not drawn. `vw`/`vh` lengths resolve to 0.
- `@supports` with a positive condition never matches, because books test for features this
  engine lacks.
- `::first-letter`, `::before` and `::after` rules are ignored rather than misapplied to the whole
  element.

## D-014: Layout caching (2026-10-14)

`LayoutEngine` caches parsed content documents (per chapter), parsed stylesheets (per text) and style
resolvers (per set of sheets plus settings), so a book's chapters share one parse of `core.css`. It
also caches styled box trees per (chapter, typography, page size); each tree keeps its shaped
paragraphs. Re-paginating a shaped chapter for a new page size then costs only line breaking and
pagination. Measured on the user's Linux desktop (Release build): the whole Standard Ebooks *Pride
and Prejudice* at 768x1024 (275 pages) takes about 1.2 s cold in a fresh process (parse 0.15 s, styles
0.48 s, shaping and pagination 0.5 s) and about 0.7 s warm. Re-paginating it at a new size takes
45–90 ms. The timing test lays out Gutenberg's *Pride and Prejudice* (438 pages at the test's tablet
settings) in about 0.55 s and fails above 2 s.

## D-015: Core builds on Storage, Epub and Layout (2026-10-14)

Plan 1.1 puts the library service in Core and `IStorageProvider` in Storage. Phase 1 had Storage
referencing Core, which made the library service unable to use a provider. The reference now runs the
other way: Storage has no dependencies, and Core references Storage, Epub and Layout. Core also holds
the reading session (paging, CFI anchoring, percentages), which is business logic and so stays out of
the UI project (plan 0.3); the UI only turns input into calls on it and draws its pages.

## D-016: The reading position is an anchor, not a page number (2026-10-14)

`ReadingSession` keeps a DOM position (the start of the page the reader turned to, or the exact
saved position) and only moves it when the reader navigates. Re-paginating for a new window size, font
size or theme lands on the page containing the anchor without moving it, so resizing back and forth
never drifts forward a page at a time, and the saved CFI is the one the reader last navigated to. The
saved locator also carries the spine index and the character offset into the chapter body, used when
the CFI no longer resolves (plan 1.4). Percentages weight each linear spine item by its byte size.

## D-017: Reader UI structure and theming (2026-10-14)

- One `MainForm` on every host, showing either `LibraryView` or `ReaderView`. Asynchronous work from
  event handlers goes through `UiOperations`, which reports failures in a banner instead of crashing an
  `async void`, and which tests wait on.
- Below 640 logical px the reader uses a phone layout: the toolbar and status bar are hidden until the
  middle of the page is tapped, and then cover the page instead of shrinking it, so showing them never
  re-paginates. The side panel (contents, typography) overlays the page. Wider windows show the side
  panel in a `SplitContainer` beside the page.
- Themes (light, sepia, dark, black) set `BackColor`/`ForeColor` on the form and reach containers and
  labels ambiently (plan 4). Toolbars, inputs, lists and trees are not ambient, as in WinForms, so
  `ThemePainter` sets theirs. Dark themes are a layout input (`LayoutSettings.DarkMode`) because books
  style for `prefers-color-scheme`: Standard Ebooks inverts its black-on-transparent title pages there.
- The typography panel is an in-window panel rather than a modal dialog, so it behaves the same on the
  phone hosts. Margins grow with the window, and wide windows hold the text to about 36 em.
- Progress is saved 1.5 s after the last page turn and when the book closes (including on window close,
  which is held until the write finishes).

## D-018: Sync engine: a local copy, an outbox, and copy-then-merge (2026-10-17)

Each device keeps a full copy of the sync home in app data, and the app reads and writes only that copy
(offline-first, plan 2.4). `SyncEngine` copies files both ways and never merges. Merging stays in
`LibraryService`, which already reads every device's files and takes the highest HLC, so convergence
reduces to every device holding the same files.

- **Outbox:** writes to the device's own files and to write-once files are recorded (persisted, by path
  and a sequence number) *before* the local write. A push removes a path only if no newer write arrived
  while it was copying.
- **Write-once collisions:** two devices importing the same book write different `meta/` files
  (different `addedAt`/`addedByDevice`). The sync home's copy wins: the second device skips its push and
  pulls the winner over its own. Books are content-addressed, so a local copy is never re-downloaded.
- **Change detection:** providers with a change feed use it; others (LocalFolder) are listed in full
  and diffed by ETag against an index of what was last copied. File write times are not compared with
  a cursor, because Dropbox and similar clients keep the original machine's time, which can be ten
  minutes "in the past" on a skewed device.
- **Not deleted, not compacted:** nothing is ever deleted on either side; removing a book from the
  library will need tombstones (later phase). All books are pulled eagerly; fetching books on first
  open is a possible later saving on phones.
- **The sync home's identity** (`local:<folder>`) keys the outbox and index, so a new sync home
  re-uploads the local library ("attach"), and the old one's state is left alone.
- **A missing folder is offline.** `LocalFolderStorageProvider(requireRoot: true)` throws
  `DirectoryNotFoundException` rather than create the folder, so an unmounted drive never gets a fresh
  empty library written into its mount point.
- **Format guard:** a `format.json` with a higher version stops the device from writing to that sync home.

The local sync folder is offered only on the desktop (Windows, macOS, Linux), where Dropbox, Drive,
OneDrive and iCloud have clients that sync such a folder. Phones and the browser get the cloud providers
in Phase 7.

## D-019: LocalFolder ETags hash small files (2026-10-17)

Write time plus size is not a safe ETag for a folder that is polled. File systems stamp writes with a
coarse clock (milliseconds on ext4 before multigrain timestamps, 2 s on FAT/exFAT and some network
shares), and two progress saves of the same size within one tick shared an ETag, so a poller missed the
second save until the next one. Files up to 256 KB (every state, settings and meta file, and covers)
now get a SHA-256 content ETag. Writes hash while copying, and listings cache hashes, trusting a cached
hash only if the file's write time is at least 2 s before the hash was taken (git's "racy entry" rule).
Larger files (books, which are write-once and content-addressed) keep time plus size.

## D-020: A save outranks everything the device has seen for that book (2026-10-17)

`SaveProgressAsync` reads the book's state files (observing their HLCs) before it stamps a new
position. Without this, a device whose clock runs behind could restart (its HLC clock forgetting what
it had observed), save, and lose to its own earlier save or to a position it had already read past.
Settings already worked this way, because saving them merges first.

## D-021: The Phase 5 simulation (2026-10-17)

`Majorsilence.Recital.Sync.Tests` runs three devices over a shared sync home in a real temporary folder
(`LocalFolderStorageProvider`), each with an in-memory local copy and a clock skewed by up to ±10
minutes. A seeded run is 80 random steps: imports from a pool of four books, page turns, preference
changes, sync rounds, going offline, flaky networks (calls fail at random, including writes that land
but report failure), restarts (the local copy and outbox survive), and a simulated sync client that
leaves conflicted copies of files (current or stale) and sometimes puts a stale version back under the
real name. The shared folder's write times are rounded to 2 s, as on FAT. Then all devices come online
and sync three times. The run passes if:

- the third round moves nothing;
- every outbox is empty;
- no device wrote a file other than its own or an absent write-once one;
- every device shows the same library, positions and preferences, and those are the newest of
  everything any device saved (the highest HLC returned by any save, and per setting the highest HLC
  in any device's own settings file).

CI runs 1,000 seeds (about 8 s on a desktop). `RECITAL_SYNC_RUNS` and `RECITAL_SYNC_SEED` change the
count and first seed, and a failure names its seed and prints the last 60 steps. Before relying on it,
each of these deliberate bugs was checked to fail it: a write-once meta file that keeps the local
version (it failed); time-plus-size ETags (168 of 500 runs failed); an outbox that does not survive a
restart (146 of 300); pulling a device's own files (1 of 300). The D-020 fix is not reachable from the
simulation, which always lists the library (and so observes every HLC) before saving, so a unit test
covers it. 20,000 seeds passed locally.

## D-022: Annotations: CFI ranges, merged by id, deletion is final (2026-10-17)

- Highlights, notes and bookmarks are `Annotation` records in each device's state file for the book
  (docs/SYNC_FORMAT.md). A note is a highlight with `note` set, not a separate kind.
- Anchors are a range CFI (a point for bookmarks), plus spine index and character offset as a fallback.
  Highlights are drawn by resolving the CFI on the page being shown, so they land on the same words at
  any page size or typography.
- **Deletion wins over any edit**, not just over older ones. The plan's requirement is that a deleted
  highlight stays deleted after a stale device reconnects. A device that was offline and recoloured the
  highlight after the deletion would otherwise bring it back with a higher HLC. Ids are random GUIDs
  and never reused, so a final deletion costs nothing. `SaveAnnotationAsync` refuses to edit an
  annotation it can see is deleted.
- Tombstones keep only id, kind, times and HLC, so a deleted note's text leaves the sync folder the next
  time the device writes its file. Tombstones are kept forever (compaction is a later phase).
- Every save observes the HLCs of all the book's state files first (as D-020 does for progress), so an
  edit outranks every version the device has seen.

## D-023: Selecting text: hold still, then drag (2026-10-17)

A quick drag (or swipe) still turns the page. Selection starts when the pointer has been held still
for 350 ms and then moves, or with a long press, which selects the word under the finger. Dragging
then extends the selection, and lifting shows the highlight bar: five colours, Note…, Copy, plus Delete
for an existing highlight. Tapping a highlight opens the bar for it. This keeps one gesture set for
mouse and touch, and the page-turn gestures unchanged. The note editor is an in-window panel, like the
typography panel, so it behaves the same on phones.

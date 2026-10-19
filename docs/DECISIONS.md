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

## D-024: Cloud providers are thin HttpClient wrappers with in-process fakes (2026-10-17)

Plan 1.2 prefers hand-written clients to SDKs. Each provider (`Majorsilence.Recital.Storage.WebDav`,
`.Dropbox`, `.OneDrive`, `.GoogleDrive`) is one class over `HttpClient`, with no packages, so the
browser build stays small and trimmable. Each has an in-process fake of its API in
`Majorsilence.Recital.Storage.Tests`, answering the way the real service does: errors, paging in pages
of three so paging code runs, change feeds, case rules. Normal CI runs the whole conformance suite and
the three-device sync simulation (100 seeds per provider) against every fake. WebDAV also runs against
a real Apache mod_dav in CI, and the cloud services run against real accounts when credentials are set
(docs/CLOUD_SETUP.md). Real servers found bugs the fakes did not have, and those bugs were fixed and
the fakes made stricter:
- WebDAV: a missing base folder (409 on MKCOL), and Apache's weak ETags, which never satisfy If-Match.
- OneDrive: `new Uri (base, "root:/...")` parses `root:` as a scheme.

Per-service choices:
- **WebDAV:** no change feed. The engine lists `Recital/` and diffs ETags. Folders are created as
  needed, including the configured base. A weak ETag (`W/`) is compared before a conditional PUT
  instead of being sent as If-Match.
- **Dropbox:** Full Dropbox access, so the sync home is a visible `/Recital` folder and import can
  browse everything. ETag = `rev`, conditional writes use upload's `update` mode, and the feed is
  `list_folder/continue` scoped to `Recital/`. Dropbox paths are case-insensitive, and entries keep
  the case Recital wrote.
- **OneDrive:** path addressing (`root:/a/b:`), ETag = `eTag` with If-Match, and upload sessions above
  4 MB, sent through a plain HttpClient because session URLs refuse a bearer token. The feed is `delta`
  from the drive root, because OneDrive for Business supports delta only there. It starts at
  `token=latest` after a recursive listing and is filtered to `Recital/`. Personal drives' delta items
  have no parent path, so paths are rebuilt from parent ids.
- **Google Drive:** the `drive.file` scope, so Recital sees only its own files and needs no security
  review. Drive allows same-named files in one folder, which two devices racing to create `Recital/`
  would produce. A path resolves to all matches: listings merge same-named folders, a duplicated file
  reads as its oldest copy (first writer, as write-once needs), and writes go to the oldest folder.
  Folder ids are cached for 5 minutes, and dropped at once when the feed shows an unknown folder at a
  known path. ETag = `version`, and If-Match is checked before writing (Drive v3 has no conditional
  write). The feed is `changes.list`, with paths rebuilt from parent ids. Browse is off: `drive.file`
  cannot see the user's other files, so Drive import goes through the OS picker (plan 2.5).
- **iCloud:** not a provider in v1. On macOS and Windows, iCloud Drive is a local folder, so the Local
  folder choice covers it. The iOS ubiquity container needs an entitlement and a device, and is left for
  Phase 10.

## D-025: Secrets live only in the OS secure store, through P/Invoke (2026-10-17)

`ISecretStore` (Storage) has three platform implementations in `Majorsilence.Recital.Platform`, and no
packages:
- **Windows:** Credential Manager generic credentials (`CredWriteW`/`CredReadW`), so not DPAPI blobs
  in files.
- **macOS:** the login keychain, through the SecKeychain generic-password calls. They are deprecated
  but still present, and they take plain buffers rather than CoreFoundation dictionaries.
- **Linux:** the Secret Service through libsecret, loaded at run time from the system. It is LGPL, but
  it is the desktop's own library, dynamically loaded and not shipped. The non-variadic `...v_sync`
  calls are used, because a variadic call cannot be declared portably (arm64).

The browser keeps secrets in memory (plan 0.3), and so do Android and iOS until Phase 10 wires up
Keystore and Keychain. The settings say "memory only" there. A test signs in through fake services and
then checks that no token or password appears in `sync-home.json`, the local library or the sync home.

## D-026: OAuth: PKCE everywhere, a raw-socket loopback on the desktop (2026-10-17)

`OAuthClient` does the authorization-code flow with PKCE (S256) and a `state` check. `OAuthHandler`
adds the bearer token and refreshes it on expiry or a 401. A refresh token the server rotates is
stored again, and a refused refresh becomes `StorageAuthenticationException`. The engine then shows
"Sign in again" instead of retrying for ever.

The desktop broker opens the system browser and reads the redirect on `127.0.0.1`/`localhost` with a
one-shot `TcpListener`. HttpListener on Windows needs a URL reservation (admin) to listen. Google and
Microsoft accept any loopback port. Dropbox matches redirects exactly, so it gets the fixed port 47612.

## D-027: A sync home is a kind plus settings; switching starts a fresh index (2026-10-17)

`sync-home.json` records the kind (`localFolder`, `webDav`, `dropbox`, `oneDrive`, `googleDrive`) and
what that kind needs: the folder, the WebDAV URL and user, or the cloud account's id and name. Files
from Phase 5 with only `folder` still read. The engine's outbox and index are keyed by the sync home
(`dropbox:<account id>`, `webdav:<url>|<user>`). Choosing another sync home, including another account
on the same service, uploads the local library to it afresh. Stopping, or moving to another kind,
forgets the old password or refresh token. Enums in sync files are now camelCase strings
(`"kind": "highlight"`); the numbers early Phase 6 builds wrote still read.

## D-028: Reading aloud: paragraph chunks, a queue kept ahead, two kinds of engine (2026-10-17)

`Majorsilence.Recital.Tts` follows plan 3.1:
- **Planning.** `UtterancePlanner` walks a chapter's DOM into chunks, one per paragraph or heading. A
  paragraph is split at sentence ends only when it is longer than the engine accepts, because neural
  voices plan intonation across a chunk (plan 3.2). Every character keeps its DOM position, for
  highlighting and for saving the place. Footnotes, note references, ruby annotations and hidden
  elements are not read.
- **Prosody (rule-based).** `em`/`i`/`cite` and `strong`/`b` become emphasis, and quoted speech becomes
  a dialogue span (with an optional second voice). Headings get 700 ms of pause on each side, and scene
  breaks (`hr`, `* * *`) 1.2 s before what follows. Sentences are split by rule, and abbreviations
  (Mr., St., e.g.) and initials do not end one.
- **SSML.** `Ssml.Build` makes SSML with breaks, `emphasis`, a `voice` for dialogue, and, for engines
  that report marks, a `mark` named after each word's character index.
- **Two kinds of engine behind one `ISpeechChannel`** (a queue that speaks chunks back to back and
  reports chunk start, word progress and chunk end):
  - Engines that produce audio (`ISpeechSynthesizer`: SAPI, say, later Piper and the cloud engines) go
    through `AudioSpeechChannel`. It synthesizes each enqueued chunk at once, or takes it from
    `AudioCache` (keyed by engine, voice, speed, language and the chunk). It queues the audio on an
    `IAudioOutput` and maps the output's play position to the word. With word timings that position
    is exact; without them it is interpolated by character count.
  - Engines that speak by themselves (speech-dispatcher, later Web Speech and the mobile engines)
    queue chunks with the OS service and turn its mark, begin and end events into the same progress.
- **The player.** `TtsPlayer` keeps three chunks queued beyond the one playing, so synthesis is always
  ahead and there are no gaps. It follows on into the next chapter, starts at the sentence holding the
  page's first position, and skips by sentence. Changing voice or speed restarts the current sentence.
  It has a sleep timer (minutes, or the end of the chapter).
- **The reader.** Speech moves the page itself (`ReadingSession.ShowPositionAsync`), so the place saved
  is the word being read. The sentence and the word are drawn under the text. A page turned by hand
  restarts reading from the new page.

The acceptance numbers are tested against a fake engine with real latency and a real-time output:
- No gap between chunks over 300 ms, while every chunk takes 150 ms to synthesize.
- The highlighted word is never early, and never later than one sentence, with and without word
  timings.

Each word passed since the last 40 ms tick is reported, so a short sentence is never skipped.

## D-029: The OS voices and outputs, without native dependencies (2026-10-17)

- **Linux:** speech-dispatcher, which desktops already run (it drives espeak-ng, RHVoice, Piper and
  others), reached in its SSIP protocol over the session socket. It is a protocol, so nothing is
  linked, which keeps the copyleft libspeechd out. Chunks go as SSML with word marks. The marks come
  back as progress, in order, which was checked against the real service on the development machine.
  Sends are serialised, because two racing sends spoke paragraphs out of order.
- **Windows:** SAPI through `System.Speech` (MIT). It renders SSML to 16-bit PCM and records the word
  marks' audio positions (`BookmarkReached`) as word timings. Output is winmm `waveOut`, with an exact
  device position.
- **macOS:** `say` writes a WAVE file, using its own embedded commands for pauses and emphasis. It gives
  no timings, so progress is interpolated. Output is `afplay` per segment.
- **Linux audio for engines that produce audio** (Piper and the cloud engines in Phase 9): one
  `pw-cat --raw` (or `pacat`, `aplay`) fed through a pipe for the whole session. Its position is the
  clock less a 150 ms buffer, and pause stops the player process.
- **Not yet:** media keys and lock-screen controls (an `IMediaSession` per host: SMTC, MPRIS,
  MPNowPlayingInfoCenter), the Android foreground service, iOS background audio, and engines for
  Android, iOS and the browser. They need the mobile heads running on devices (Phase 10). Linux
  without speech-dispatcher has no voice until Piper (Phase 9).

## D-030: Piper only as the user's own program; cloud voices with the user's own key (2026-10-18)

- **Piper.** The original MIT Piper was archived in 2025. Its successor is GPL-3.0, and every Piper
  phonemizes with espeak-ng (GPL-3.0). Plan 0.1 says to ask before adding anything copyleft, so
  Recital does not ship, download or link Piper. `PiperSynthesizer` starts the `piper` program the user
  installed (from the PATH or a path in voice settings) and talks to it over pipes:
  - One process per voice and speed, because a model takes a second to load.
  - A line of text per chunk; with `--output_dir`, Piper writes a WAVE file per line and prints its path.
  - Voices are the `.onnx` files (with their `.onnx.json`) in a folder, by default `piper-voices` in
    app data. A voice in the book's language is preferred.
  - There are no timings, so progress is interpolated. Each voice model's licence is the user's to
    check.
- **Cloud voices** (`Majorsilence.Recital.Tts.Cloud`, plain HttpClient):
  - **ElevenLabs:** `with-timestamps`, so character alignment gives exact word timings.
  - **Google Cloud:** v1beta1 with SSML marks and `enableTimePointing`, so timings are exact. The key is
    sent in `X-Goog-Api-Key`, never in the URL.
  - **OpenAI:** `gpt-4o-mini-tts` with a narrator instruction, and no timings.
  - **Azure Speech:** REST SSML with a prosody rate and dialogue in a second voice. The REST endpoint
    has no word boundaries, so progress is interpolated.
  - Keys live only in the secure store (`tts:<service>`), and an engine appears only once its key is
    there.
  - The voice settings panel says, before any key is entered, that the text being read is sent to the
    chosen service while listening with it, and only then.
  - Errors never contain the key. A test checks this against a service that fails with 401, 429 and
    500, and also checks the audio cache and its keys.
- **Engine choice** is part of the synced voice preference (`engine:voice`). A device without that
  engine falls back to its first one. Device-specific settings (the Piper paths, the Azure region) are
  in `tts.json` in local app data, never synced.
- **Not done, needing the user:** the plan's side-by-side recordings in `docs/tts-samples/` need real
  keys, or a real Piper and voice. Word-level highlighting is exact for ElevenLabs, Google, SAPI and
  speech-dispatcher, and interpolated for the rest.

## D-031: Recital's own offline voices: Kokoro in ONNX Runtime with a C# misaki (2026-10-18)

The user asked for voices that always work, with no account and nothing to install, keeping cloud
keys optional. They chose Kokoro over a Piper voice and bundling over a download button.

- **Model:** Kokoro-82M v1.0 (Apache-2.0; trained only on permissive or public-domain audio, with two
  small CC BY sets credited in THIRD_PARTY.md), the onnx-community export at a pinned revision.
  `model_uint8f16.onnx` (114 MB). On a Ryzen 5 3600 it makes speech about 4× faster than it plays:
  - the plain 8-bit file (92 MB) is slower than real time on CPU;
  - fp16 (163 MB) is no faster;
  - fp32 (326 MB) is only slightly faster;
  - `model_q8f16.onnx` crashes ONNX Runtime 1.30 (a segfault on its first run).

  The cost is almost all the vocoder, so it grows with the length of the audio, not with a fixed cost
  per run. Running graph branches in parallel (`ORT_PARALLEL`) and more threads gain nothing.

  CPU use: ONNX Runtime's defaults take every core and spin between steps, which used about 4 s of
  CPU for each second of speech. With spinning off and a quarter of the logical cores (2 to 4 threads)
  it uses about 0.9 s, and is as fast or faster: 0.43 s of work per second of speech, against 0.63 s,
  with other work running.
- **Voices:** six of the 28 English ones (af_heart, af_bella, am_michael, am_puck, bf_emma, bm_george),
  0.5 MB each. Heart is the default.
- **Runtime:** Microsoft.ML.OnnxRuntime 1.30.0 (MIT). It has native builds for Windows, macOS, Linux,
  Android and iOS. Not the browser: the browser head uses the browser's own voices (Web Speech API,
  `WebSpeechEngine`).
- **Text to phonemes, with no espeak-ng (GPL):**
  - A C# port of misaki's English rules (Apache-2.0): its gold and silver dictionaries (183k words,
    1.4 MB gzipped, embedded), regular -s/-ed/-ing endings, stress, initialisms, numbers, money and
    years.
  - spaCy's part-of-speech tagger is not ported. Words said differently as noun and verb get their
    default reading, and "a" is always the article.
  - Words not in the dictionaries (most names) go to misaki's own fallback network,
    PeterReid/graphemes_to_phonemes_en_us (Apache-2.0). It is a 750k-weight BART trained on misaki's
    dictionaries; its training script used WikiText only as a list of real plural forms. It runs in
    plain C# (about 3 ms a word, cached per book), reproduces 67% of dictionary words exactly, and is
    close on the rest.
- **Timing:** a chunk (up to 240 characters) is spoken sentence by sentence, so each sentence starts
  at an exact time. Words are placed by their share of the sentence's phonemes. The model reads up to
  510 phonemes at once, so a longer sentence is split at a pause.
- **Starting quickly:** at first, pressing Listen took about 10 seconds: the model loaded only then,
  the whole first chunk (often one long sentence) had to be made before any of it played, and the
  chunks queued behind it could take the model first. Now:
  - The reader loads the model in the background when a book opens (`IPreparable`).
  - `IStreamingSpeechSynthesizer` hands over a chunk in parts, and `AudioSpeechChannel` plays each part
    as it comes. The channel tells the synthesizer how much audio is waiting to play before the next part.
  - With nothing waiting, Kokoro makes a short first part: one clause, about 2.5 s of speech, ending at a
    pause. Each later part is sized to be ready before the audio ahead runs out, using the synthesis
    speed it measures, and grows to whole sentences.
  - Chunks take the model in reading order: when the model frees up, the earliest waiting chunk goes next,
    and a chunk hands over between parts if an earlier one is waiting.

  The first words now come in about a second on the Ryzen 5 3600, even with other work running. The
  short first part is the only place a sentence is split for speed, and only right after Listen or a jump.
  Disposing waits for a run in progress, because a stopped channel does not wait for its synthesis.
- **Packaging:** the model and voices are not in git. `build/Kokoro.targets`:
  - downloads each file once into a per-user cache;
  - checks it against a pinned SHA-256 before use;
  - copies it beside the app as `kokoro/`.

  The desktop head and the TTS tests import it. The app itself never downloads anything.
  `-p:RecitalBundleKokoro=false` opts out. CI caches the files.
- **Order:** `TtsEngines` offers Kokoro first when the files are present, then the OS voices, Piper and
  the cloud services. Its audio is quick to remake, so it is not cached on disk.
- **Not yet:**
  - British voices still use the American dictionary; misaki has a British one too.
  - Only English.
  - Android/iOS packaging (the model has to reach a file path, or be loaded from bytes).

## D-032: Phones and tablets read with the device's own voices (2026-10-19)

The user wants reading aloud on a phone to be fast and to use little battery. Kokoro (D-031) runs a model
with about 82 million weights for every second of speech. On a phone's processor that keeps the chip busy
while listening, so it is not the default there. The system voices run on hardware and software built
for speech, work offline and need no account.

- **Android:** `AndroidSpeechEngine` in the Android head (TextToSpeech).
  - Only installed voices are offered (not `IsNetworkConnectionRequired`, not `notInstalled`), and one
    is always set explicitly, so the book's text never goes to a speech server the user did not choose.
  - Words come from `onRangeStart` (API 26 and later).
  - Android cannot pause, so Pause stops speaking and Resume speaks the chunk again from the last word
    heard. A generation number in each utterance id drops callbacks from speech that has been stopped.
  - Checked on an Android 16 emulator with Google's speech service: an on-device voice
    (`en-us-x-tpf-local`), word highlighting, and pause and resume.
- **iOS:** `AppleSpeechEngine` in the iOS head (AVSpeechSynthesizer).
  - Words come from `willSpeakRangeOfSpeechString`.
  - Pause waits for the end of the word.
  - The voice list includes the Enhanced and Premium voices from Settings, and the best installed voice
    for the book's language is the default.
  - The audio session is Playback / SpokenAudio, and `UIBackgroundModes` includes `audio`, so speech
    goes on with the silent switch on and the screen locked.
  - Built in CI only; it has not run on a device.
- Both are handed to `MainForm.HostTtsEngines`, so they are listed first, as the browser's voices are.
- **Next:** Kokoro on phones as an opt-in, on the Neural Engine (CoreML) or NNAPI where they can run it,
  and cached audio there so listening again costs nothing.

## D-033: Recital's voices on phones: download on opt-in, accelerator first, audio cached (2026-10-19)

The user asked for Kokoro (D-031) on phones and tablets as an opt-in, light on the battery. They chose a
download on request over bundling 115 MB into every mobile app.

- **Download:** `KokoroFiles` lists the pinned files and their SHA-256. A test checks it matches
  `build/Kokoro.targets`, so desktop and phone use the same files.
  - Voice settings shows "Download Recital voices" only where they are missing and audio can play.
  - MainForm asks once more, giving the size and the site (huggingface.co), then downloads into
    `<app data>/kokoro/<revision>`.
  - Each file is written to `.part`, checked, then moved, so a failed or altered file is never kept.
  - This is the only download the app makes by itself, and only after a yes. It sends only the request.
- **Accelerator:**
  - iOS runs CoreML with `ModelFormat = MLProgram` (fp16 on the Neural Engine) and `MLComputeUnits = ALL`.
  - Android stays on the CPU settings from D-031. NNAPI was tried first (with `NNAPI_FLAG_CPU_DISABLED`).
    On the Android 16 emulator, ONNX Runtime reported it took 203 of the model's 2,401 operations, in three
    partitions. The sine source, STFT and transposed convolutions of Kokoro's vocoder are not NNAPI operations.
    So nearly all the work stayed on the CPU, with copies at every partition edge, and NNAPI is deprecated
    since Android 15. A vendor driver might take a few more operations, but not the vocoder.
  - If the provider is missing or rejects the model, the CPU settings from D-031 are used.
    `KokoroSynthesizer.Accelerator` says which one is in use.
  - Checked on the emulator: downloaded from Voice settings, chosen in the Engine menu, and read aloud
    through `AndroidAudioOutput` with word highlighting. Speed and battery are not yet measured on a real
    phone, and CoreML is not yet tried on an iPhone.
- **Audio out:** phones get an `IAudioOutput` from their host through `MainForm.HostAudioOutput`:
  - Android: `AudioTrack` in streaming mode, with the play head as the position;
  - iOS: `AVAudioEngine` and a player node, with buffers played back plus the clock inside the current
    one. The position stands still while paused or out of audio.

  This also lets the cloud voices play on phones.
- **Cache:** on Android and iOS, Kokoro's audio goes into the existing audio cache (512 MB, oldest
  dropped first), so listening again costs no battery. Desktop still makes it again, which is cheap there.
- **Default:** the device voices stay first (D-032). Recital's voices are chosen in the Engine menu.


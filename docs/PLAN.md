# Recital — Build Plan

> Copied from the original plan with two decisions applied (see `docs/DECISIONS.md`): projects are named `Majorsilence.Recital.*` (D-001), and Avalonia is the only interactive host (D-002).

A cross-platform EPUB reader in the spirit of Readest, built on **Majorsilence.Forms**, where **all storage and sync live in the user's own cloud storage** (Google Drive, Dropbox, OneDrive, WebDAV, iCloud Drive, or any synced local folder). There is no central server and no accounts we operate.

The project has two goals, and they carry equal weight:

1. Ship a good reader: save your place everywhere, run on desktop/mobile/web, import from any cloud, and offer text-to-speech that sounds like a person reading.
2. **Exercise Majorsilence.Forms across every backend and platform** and turn every rough edge found into a tracked framework fix.

> **For Claude Code:** work one phase at a time. Each phase lists its deliverables and acceptance criteria, and a phase is done only when its acceptance criteria pass. Read the "Ground rules" section before writing any code, and re-read it at the start of each phase.

---

## 0. Ground rules

### 0.1 Clean-room and licensing
- Readest is **AGPL-3.0**. **Do not read, copy, port or translate Readest source code.** Use only its public docs and feature descriptions for inspiration.
- This project is **MIT**, matching Majorsilence.Forms.
- Every third-party dependency must be MIT, Apache-2.0, BSD, Unlicense or similar. Add each one to `THIRD_PARTY.md` with its license **before** using it. Ask before adding anything copyleft.

### 0.2 Framework dogfooding rules (the important part)
- Before using any Majorsilence.Forms control or API, check `COMPATIBILITY_MATRIX.md` in the Majorsilence.Forms repo. Many members exist but are documented as stored-only, partial or no-op.
- **Do not silently work around framework gaps.** When something is missing, stubbed or behaves differently from WinForms:
  1. Add an entry to `docs/FRAMEWORK_GAPS.md`: the API, the expected vs actual behaviour, the backend(s) affected, a minimal repro (a few lines, ideally runnable on the Headless backend), and how severe it is for this app.
  2. Only then add the smallest possible local workaround, marked `// FRAMEWORK-GAP: <id>`.
  3. If the gap is small and clearly a framework bug, propose the fix as a patch against Majorsilence.Forms in `docs/framework-patches/<id>.md`. Do not vendor or fork the framework into this repo.
- Pin the Majorsilence.Forms package version in `Directory.Packages.props` (central package management). Do not float it.
- Paint code receives a canvas in **logical units**. Owner-draw events (`DrawItem`, `DrawNode`, `CellPainting`) still use **device pixels**. Know which one you're in.

### 0.3 Engineering rules
- Target `net10.0` (plus the platform TFMs the hosts need). Nullable enabled, warnings as errors in `src/`.
- **All business logic lives outside the UI projects**, so it is unit-testable without any backend.
- The UI is tested with the **Headless backend** (offscreen Skia, pixel-diff capable). Every custom control gets at least one Headless render test.
- All I/O is async. No sync-over-async. Nothing blocks the UI thread.
- No telemetry and no network calls except to the storage/TTS providers the user explicitly connects.
- Secrets (OAuth refresh tokens, API keys) are stored only in the platform secure store: Windows DPAPI/Credential Manager, macOS/iOS Keychain, Android Keystore, libsecret on Linux. On WASM, store them only in memory unless the user opts in. **Never write secrets to the sync folder.**

---

## 1. Architecture

### 1.1 Solution layout

```
src/
  Majorsilence.Recital.Core/            Domain model, library service, sync engine, settings. No UI, no platform APIs.
  Majorsilence.Recital.Epub/            EPUB container + OPF/NCX/nav parsing, CFI generation/resolution.
  Majorsilence.Recital.Layout/          XHTML+CSS subset → styled box tree → paginated pages (Skia + RichTextKit). Pure, deterministic.
  Majorsilence.Recital.Storage/         IStorageProvider abstraction + InMemory + LocalFolder providers.
  Majorsilence.Recital.Storage.GoogleDrive/
  Majorsilence.Recital.Storage.Dropbox/
  Majorsilence.Recital.Storage.OneDrive/
  Majorsilence.Recital.Storage.WebDav/
  Majorsilence.Recital.Storage.ICloud/  Apple platforms only (iCloud Drive ubiquity container).
  Majorsilence.Recital.Tts/             ITtsEngine abstraction, utterance planner, prosody annotator, audio queue.
  Majorsilence.Recital.Tts.Platform/    OS voices (per-platform implementations behind one interface).
  Majorsilence.Recital.Tts.Piper/       Local neural TTS (offline).
  Majorsilence.Recital.Tts.Cloud/       Bring-your-own-key engines: Azure Speech, ElevenLabs, OpenAI, Google.
  Majorsilence.Recital.UI/              All Majorsilence.Forms forms and custom controls. Backend-agnostic.
hosts/
  Majorsilence.Recital.Desktop/         Majorsilence.Forms.Avalonia — Windows/macOS/Linux.
  Majorsilence.Recital.Android/         Avalonia Android head.
  Majorsilence.Recital.iOS/             Avalonia iOS head.
  Majorsilence.Recital.Browser/         Avalonia Browser (WASM) head.
  Majorsilence.Recital.Terminal/        Stretch goal: same UI on the Terminal backend.
tests/
  Majorsilence.Recital.Core.Tests/
  Majorsilence.Recital.Epub.Tests/
  Majorsilence.Recital.Layout.Tests/    Golden-file layout tests (box trees + page breaks as JSON snapshots).
  Majorsilence.Recital.Sync.Tests/      Multi-device sync simulations against InMemory + LocalFolder providers.
  Majorsilence.Recital.UI.Tests/        Headless backend render + interaction tests, pixel-diff baselines.
docs/
  FRAMEWORK_GAPS.md
  framework-patches/
  SYNC_FORMAT.md
  DECISIONS.md                 Architecture decision log (one short entry per non-obvious choice).
```

### 1.2 Key dependencies (verify licenses and current versions first)
- **Majorsilence.Forms** + backends (Avalonia, Headless). Avalonia is the only interactive host; see DECISIONS.md D-002.
- **VersOne.Epub** (Unlicense) or an equivalent permissive EPUB parser. If none fits, write a small one; EPUB is just ZIP + XML.
- **AngleSharp** + **AngleSharp.Css** (MIT) for XHTML and CSS parsing.
- **Topten.RichTextKit**: already a Majorsilence.Forms dependency; use it for text shaping and line layout.
- **Piper** voices via ONNX Runtime (MIT). Check each voice model's license individually.
- Official or REST-level clients for Google Drive v3, Dropbox v2, Microsoft Graph and WebDAV. Prefer thin hand-written `HttpClient` wrappers over heavy SDKs where it keeps WASM size and trimming sane.

### 1.3 The big rendering decision: native layout, not a WebView

Render EPUB content with **our own layout engine on Skia** (`Majorsilence.Recital.Layout` plus a custom `BookPageView` control), not a web view. The reasons:

- It works identically on **every** backend, including Headless (testable), Terminal, and mobile/WASM, where native web view support is uncertain.
- We own the text geometry, so TTS sentence/word highlighting, selection, annotations and CFI mapping are exact.
- It is the strongest possible exercise of the framework's custom-control path: painting, hit-testing, gestures, scrolling, focus, accessibility and DPI handling.

Scope it honestly. Support a **reflowable-EPUB CSS subset** first:
- block/inline flow, margins/padding, `text-align`, `text-indent`, `font-family/size/weight/style`, `line-height`, lists, `<img>`/`<svg>` as images, simple tables, `page-break-*` / `break-*`, and embedded fonts.
- Pages are computed per (chapter, viewport size, user typography settings) and cached.

**Optional later:** a "high-fidelity mode" for fixed-layout or CSS-heavy books using the framework's `IWebViewFactory` seam (real WebView2/WKWebView/WebKitGTK on the Avalonia desktop backend) with **foliate-js (MIT)**. This is desktop-only and opt-in per book. Record the decision in `DECISIONS.md`.

### 1.4 Positions: EPUB CFI is canonical
- Page numbers are **not portable**, because every device paginates differently. The canonical locator is an **EPUB CFI**, plus a fallback tuple `(spineIndex, charOffsetInChapter, percentInBook)`.
- `Majorsilence.Recital.Epub` must round-trip: layout position → CFI → layout position on a different viewport size, landing within the same paragraph.

---

## 2. Storage and sync design (user-owned cloud, no server)

### 2.1 Two separate concepts
- **Sync home**: the one provider/folder the user picks to hold their library and reading state. All devices point at the same sync home.
- **Import sources**: any connected provider, the OS file picker, or a URL. Importing **copies** the book into the sync home.

### 2.2 Sync home folder layout (`docs/SYNC_FORMAT.md` must document this, versioned)

```
<SyncRoot>/Recital/
  format.json                         { "formatVersion": 1 }
  books/<bookId>.epub                 bookId = lowercase hex SHA-256 of file bytes
  meta/<bookId>.json                  title, authors, language, dc:identifier, addedAt, addedByDevice
  covers/<bookId>.jpg                 small thumbnail, generated on import
  state/<bookId>/<deviceId>.json      this device's progress + annotations for this book
  devices/<deviceId>.json             device name, platform, lastSeen
  settings/<deviceId>.json            this device's synced preferences
```

### 2.3 The core rule: each device writes only its own files
Dumb file stores (Drive, Dropbox, WebDAV, OS sync clients) have no transactions and create "conflicted copy" files on concurrent writes. We avoid write conflicts entirely:

- A device **only ever writes** `state/*/<itsDeviceId>.json`, `devices/<itsDeviceId>.json` and `settings/<itsDeviceId>.json`.
- `books/`, `meta/` and `covers/` are **write-once**, keyed by content hash, so concurrent imports of the same book are idempotent.
- To read state, a device reads **all** device files for a book and **merges**:
  - **Progress**: take the locator with the highest **Hybrid Logical Clock** (HLC) timestamp.
  - **Annotations** (highlights, notes, bookmarks): union by annotation GUID. Within a GUID the highest HLC wins. Deletes are **tombstones** (`deleted: true`) that are kept, never physically removed during normal operation.
  - **Settings**: per-key last-writer-wins by HLC across device files.
- Treat any `*conflicted copy*` / `(1)` files we find as read-only extra inputs to the merge, then ignore them. Never delete user files.
- Optional later: periodic **compaction**, where a device that has been idle for N days has its state folded into a `state/<bookId>/_compacted.json` written by one elected device. Not in v1.

### 2.4 `IStorageProvider`

```csharp
public interface IStorageProvider
{
    string Id { get; }                       // "gdrive", "dropbox", "onedrive", "webdav", "icloud", "local", "memory"
    StorageCapabilities Capabilities { get; } // ChangeFeed, ETags, Browse, LargeUpload, ...
    Task<IReadOnlyList<StorageEntry>> ListAsync(string folderPath, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<StorageEntry> WriteAsync(string path, Stream content, string? ifMatchEtag, CancellationToken ct);
    Task<StorageEntry?> StatAsync(string path, CancellationToken ct);
    Task<ChangeSet> GetChangesAsync(string? cursor, CancellationToken ct); // fall back to ListAsync polling if unsupported
}
```

- Paths are logical (`Recital/state/abc/dev1.json`). Each provider maps them, e.g. Drive uses folder IDs and caches the path→ID map.
- Use ETags/revisions where they exist. Use change feeds (Drive `changes`, Dropbox `list_folder/continue`, Graph `delta`) where they exist, and fall back to polling.
- **Offline-first:** every write goes to a local outbox first. The sync engine drains the outbox with exponential backoff. The app must be fully usable with no network.

### 2.5 Provider notes (things that will bite)
- **Google Drive:** use the `drive.file` scope. The app sees only files it created plus files the user explicitly picks, which avoids the restricted-scope security review that broad `drive.readonly` needs for a public app. Create a **visible** folder (not `appDataFolder`) so users can see their books. For *importing* arbitrary existing Drive files:
  - Web: Google Picker grants per-file access under `drive.file`.
  - Mobile: the OS picker (Android SAF / iOS Files) already exposes Drive, Dropbox and iCloud, so OS-picker import covers most of this for free.
  - Desktop: OS picker over the Drive-for-desktop folder, or the Picker in a system browser. Record the final approach in `DECISIONS.md`.
- **OAuth:** PKCE everywhere. Desktop uses a loopback redirect, mobile a custom URI scheme, and web a redirect page. No client secrets embedded in WASM.
- **WASM CORS:** Drive, Dropbox and Graph APIs support CORS. WebDAV servers often don't, so detect this and show a clear message.
- **iCloud:** realistically Apple-only, via the iCloud Drive ubiquity container (needs the entitlement). On Windows, users point the **LocalFolder** provider at their iCloud Drive folder. Do not attempt CloudKit Web Services in v1.
- **LocalFolder provider:** any folder, including Dropbox, Drive, OneDrive or iCloud desktop-client folders. This is the **first** real provider to build, because it exercises the whole sync engine with zero OAuth.

---

## 3. Text-to-speech design ("proper emphasis and tone")

### 3.1 Pipeline

```
Layout model ──► Utterance planner ──► Prosody annotator ──► Engine adapter ──► Audio queue ──► Output
                 (paragraph chunks,     (dialogue, emphasis,  (SSML or engine     (prefetch,       (platform
                  sentence + word        questions, headings,  params; returns     cache, gapless)  audio)
                  CFI ranges)            pauses)               audio + timings)
```

### 3.2 Why most readers sound flat, and what we do instead
- **Chunk by paragraph, not sentence.** Neural voices plan intonation across the whole chunk, and one-sentence-at-a-time synthesis is what makes narration choppy. Send paragraph-sized chunks, plus the previous/next text as context where the engine accepts it.
- **Prosody annotator (rule-based, v1):**
  - `<em>`, `<i>` and `<strong>` become emphasis.
  - Quoted dialogue becomes a dialogue segment, with an optional alternate voice for dialogue vs narration.
  - Headings get a longer pause before and after, and scene breaks (`<hr>`, `* * *`) a longer pause.
  - Question/exclamation endings are kept intact so the engine's own intonation applies.
- **Map to SSML** for engines that accept it (Azure, Google, Windows). For engines that don't, map to their native parameters or pass plain text with the punctuation preserved.
- **Optional (later, opt-in, BYO key):** an LLM pass that tags speaker and emotion per dialogue line, used to pick voice/style. It is off by default, the user is told text leaves the device, and the results are cached per book.

### 3.3 Engines (all behind `ITtsEngine`)

| Engine | Offline | Expressiveness | Word timings | Notes |
|---|---|---|---|---|
| Platform voices | yes | low–medium | varies | Windows WinRT/SAPI, macOS/iOS AVSpeechSynthesizer, Android TextToSpeech, Linux speech-dispatcher, WASM Web Speech API. Default. |
| Piper | yes | medium | sentence-level | Local neural; desktop + Android first. Check each voice's license. |
| Azure Speech (BYO key) | no | high (styles via SSML) | yes (word boundary events) | |
| ElevenLabs (BYO key) | no | highest | yes (character alignment) | Best for dialogue and emotion. |
| OpenAI / Google (BYO key) | no | high | varies | |

- Engines report their capabilities (SSML, styles, word timings, max chunk length), and the planner adapts.
- **Highlighting:** use word-level timings when the engine provides them; otherwise interpolate within the sentence by character count.
- **Audio cache:** synthesized chunks are cached locally, keyed by (engine, voice, settings, text hash). The cache is never synced; it's just a cache.
- **Background playback and lock-screen controls** are host-specific: an Android foreground service with MediaSession, an iOS background audio mode with Now Playing, and OS media keys on desktop. Keep these in the host projects behind an `IMediaSession` interface.
- **Audio output:** check whether Majorsilence.Forms provides any audio playback. If not, log it in `FRAMEWORK_GAPS.md` (as a "framework scope" question, not necessarily a bug) and implement an `IAudioOutput` per host.

---

## 4. UI (Majorsilence.Forms)

| Screen | Primary controls | Exercises |
|---|---|---|
| Library | `ListView` (`LargeIcon` and `Details` views), `ImageList` covers, search `TextBox`, `ContextMenuStrip`, `ToolStrip` | Virtual mode for large libraries, owner-draw tiles, sorting, drag-and-drop import |
| Reader | Custom `BookPageView : Control` (paint, hit-test, gestures, selection), `ToolStrip`, `StatusStrip` | Custom painting, DPI scaling, keyboard handling (`ProcessCmdKey`), mouse capture, touch/swipe, focus |
| Contents / Annotations | `SplitContainer` + `TreeView` (TOC) + `ListView` (highlights) | Splitter behaviour, tree events, `NodeMouseClick` |
| TTS player | Custom mini-player strip, `TrackBar` (speed), `ComboBox` (voice), sleep timer | Timers, cross-thread UI updates, `ComboBox` data binding |
| Settings | `TabControl`, `NumericUpDown`, `CheckBox`, `ComboBox`, font preview panel | Data binding, validation, `AutoScaleMode` |
| Storage setup | Wizard-style `Form`, provider list, OAuth status | Dialogs, `ShowDialog`, async + UI thread marshalling |

- **Layout must adapt to phone widths.** Use a single-column reader with an overlay toolbar on narrow viewports, and a `SplitContainer` with a side panel on wide ones. Use `StackPanel` (a Majorsilence extension) where WinForms layout is awkward. Note each such use in `FRAMEWORK_GAPS.md` as "used extension instead of WinForms idiom" so we learn where the WinForms model struggles on mobile.
- **Themes:** light, sepia, dark and black, applied through ambient `BackColor`/`ForeColor` on containers.
- **Accessibility:** expose `BookPageView` text through the accessibility object model. On Windows, verify with `Majorsilence.Forms.WindowsUIAutomation` and a screen reader.

---

## 5. Phases

Each phase ends with: all tests green, `docs/FRAMEWORK_GAPS.md` updated, and a short `CHANGELOG.md` entry.

### Phase 1 — Skeleton and CI
- Create the solution, central package management, `.editorconfig`, MIT `LICENSE`, `THIRD_PARTY.md`, the `docs/` stubs, and a GitHub Actions matrix (Windows/macOS/Linux build + test; Android/iOS/WASM build only).
- `Majorsilence.Recital.Desktop` shows an empty main form via the Avalonia backend. `Majorsilence.Recital.UI.Tests` renders the same form on Headless and pixel-diffs it against a baseline.
- **Accept:** CI is green on all three desktop OSes, and the WASM and Android heads build.

### Phase 2 — EPUB model
- Parse the container, OPF, spine, manifest, NCX and EPUB 3 nav, metadata, cover and embedded fonts.
- Implement CFI: generate a CFI from a DOM position, resolve a CFI back to a DOM position, and compare CFIs.
- Test corpus: a handful of public-domain EPUBs (Standard Ebooks, Project Gutenberg EPUB 3) committed under `tests/corpus/` with their licenses noted, plus hand-made edge cases (missing nav, NCX only, broken manifest entries, huge chapters).
- **Accept:** every corpus book parses, and CFI round-trip tests pass on every paragraph of every corpus book.

### Phase 3 — Layout engine
- Build the styled box tree from XHTML + CSS (user-agent stylesheet + book CSS + user overrides), then line layout via RichTextKit, then pagination with widow/orphan control and image scaling.
- Map every glyph run back to a DOM range, which gives layout ↔ CFI.
- **Accept:**
  - Golden JSON snapshots for page breaks at three viewport sizes.
  - A position at 10% / 50% / 90% maps to a CFI and back to the same paragraph at a different viewport size.
  - Layout of a 300-page novel finishes in under 2 s on desktop (measure it and record the number).

### Phase 4 — Reader UI (local only)
- Library (import from the OS file picker, covers, search, sort), the `BookPageView` reader (tap/click zones, arrow keys, swipe, page-turn animation optional), TOC, typography settings, and themes.
- Progress is saved locally via the same state-file format, using an `InMemory`/`LocalFolder` provider pointed at app data.
- **Accept:**
  - Headless tests cover opening a book, paging forward 10 times, switching theme, and resizing the window, with the position preserved by CFI.
  - Manual smoke test on desktop under Avalonia on Windows, macOS and Linux. Record every per-backend difference in `FRAMEWORK_GAPS.md`.

### Phase 5 — Sync engine + LocalFolder provider
- HLC implementation, merge logic, outbox, change detection by polling, and conflict-copy tolerance.
- **Accept:** `Majorsilence.Recital.Sync.Tests` simulates 3 devices with random interleavings, offline periods, clock skew (±10 min) and duplicate files over a shared `LocalFolder`. Every run must converge to the same state on all devices, using property-based or seeded-random tests with ≥1,000 runs in CI.

### Phase 6 — Annotations
- Highlights (colours), notes, and bookmarks, all CFI-anchored, synced via tombstoned GUIDs. Export to Markdown.
- **Accept:** annotations survive a viewport change and sync across simulated devices, and a deleted highlight stays deleted after a stale device reconnects.

### Phase 7 — Cloud providers
- Order: **WebDAV**, then **Google Drive**, then **Dropbox**, then **OneDrive**, then **iCloud** (Apple only).
- Each provider passes one shared **provider conformance test suite**: list/read/write/stat/ETag/change-feed, run against the real service with test credentials behind an opt-in CI flag, and against a fake in normal CI.
- Import from any connected provider: browse, pick, copy into the sync home.
- **Accept:** the conformance suite passes for each provider, and two real devices (e.g. desktop + Android) sync progress through Google Drive within one polling interval.

### Phase 8 — TTS v1
- Utterance planner, rule-based prosody annotator, platform engines, audio queue with prefetch and cache, sentence highlighting, auto page-turn, and sleep timer.
- Host media integration: desktop media keys, Android foreground service, iOS background audio.
- **Accept:**
  - Plays a full chapter with no gaps longer than 300 ms between chunks on desktop.
  - The highlight never lags the audio by more than one sentence.
  - Playback continues with the screen off on Android.

### Phase 9 — TTS v2 (expressive)
- Piper (offline neural), plus the BYO-key cloud engines (Azure, ElevenLabs, OpenAI, Google).
- Word-level highlighting where timings exist, and optional separate voices for dialogue vs narration.
- **Accept:** a side-by-side recording of the same dialogue-heavy passage on each engine is committed to `docs/tts-samples/`. Each engine's capabilities are documented, and API keys never appear in logs or the sync folder (add a test for this).

### Phase 10 — Platform polish and WASM
- Browser head: storage provider OAuth redirect flow, OPFS/IndexedDB local cache, Web Speech API engine, and installable PWA manifest.
- Mobile: touch gestures, safe areas, share-sheet import, and offline-first behaviour checks.
- **Accept:** the same book and position open on desktop, Android, iOS and browser after a sync. Document the WASM bundle size and startup time.

### Phase 11 (stretch)
- Fixed-layout / high-fidelity mode via `IWebViewFactory` + foliate-js on desktop.
- Terminal host (`Majorsilence.Recital.Terminal`).
- LLM-assisted speaker/emotion tagging (opt-in, BYO key).
- OPDS catalog import.
- Sync-folder compaction.

---

## 6. Framework coverage report

At the end of each phase, regenerate `docs/FRAMEWORK_COVERAGE.md`. It is a table of every Majorsilence.Forms control and API the app uses, showing:
- which backends it was exercised on (Avalonia desktop / Android / iOS / Browser, Headless, Terminal),
- whether it behaved correctly on each,
- and links to the related `FRAMEWORK_GAPS.md` entries.

This report is the main output for the second project goal. Keep it accurate rather than flattering.

---

## 7. Open questions to settle early (record answers in `DECISIONS.md`)

1. Which Avalonia mobile/WASM capabilities does the Majorsilence.Forms Avalonia backend expose today? Specifically: file pickers, clipboard, touch gestures, soft keyboard, and lifecycle (suspend/resume). Verify each on a device before Phase 4 relies on it.
2. Does any backend support native web views on Android/iOS/WASM? This only matters for the Phase 11 high-fidelity mode.
3. Should the Google Drive sync home live in a user-visible folder (current plan) or `appDataFolder`? Weigh visibility against the risk of users editing files by hand.
4. Should book identity be a content hash only, or also match on `dc:identifier` so a re-downloaded copy of the same book keeps its progress? The current plan is content hash as the key, plus a `dc:identifier` match offered as a "same book?" merge prompt.
5. App name. "Recital" is a working title.

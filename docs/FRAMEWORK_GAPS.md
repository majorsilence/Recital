# Majorsilence.Forms gaps

Every place Recital hit a missing, stubbed or WinForms-divergent Majorsilence.Forms behaviour, per plan
section 0.2. Local workarounds are marked `// FRAMEWORK-GAP: <id>` (or the XML-comment equivalent) in
code, so `git grep FRAMEWORK-GAP` finds each one.

Severity: **blocker** (cannot ship the feature), **major** (workaround costs real effort or
fidelity), **minor** (cosmetic or cheap to work around), **scope** (a question of what the framework
should cover, not a bug).

Phase 5 (sync engine, sync folder menu, folder picker) found no new gaps. Phase 6 found GAP-012. Phase 7 (cloud sync UI) found none.

| ID | Area | Backends | Severity | Status |
|---|---|---|---|---|
| GAP-001 | Packaging: no `net10.0-ios` asset in `Majorsilence.Forms.Avalonia` | Avalonia iOS | blocker (iOS) | Fixed in 26.11.5 by [Forms PR #478](https://github.com/majorsilence/Majorsilence.Forms/pull/478); Recital pins 26.11.6 |
| GAP-002 | No pixel-diff/baseline helper for Headless renders | Headless | scope | Fixed in 26.11.6 by [Forms PR #489](https://github.com/majorsilence/Majorsilence.Forms/pull/489); Recital's workaround removed |
| GAP-003 | Headless renders depend on the host OS (chrome policy, system fonts) | Headless | major (for UI tests) | Fixed in 26.11.6 by [Forms PR #488](https://github.com/majorsilence/Majorsilence.Forms/pull/488); Recital's workaround removed |
| GAP-004 | `Theme.UIFont` honours only the typeface's family name, not the typeface | all | major | Fixed in 26.11.6 by [Forms PR #487](https://github.com/majorsilence/Majorsilence.Forms/pull/487); Recital's workaround removed |
| GAP-005 | File dialogs return paths only; mobile and browser pickers give nothing openable | Avalonia Android, iOS, Browser | blocker (mobile/browser import) | Fixed in 26.11.6 by [Forms PR #484](https://github.com/majorsilence/Majorsilence.Forms/pull/484); library import reads picked files as streams |
| GAP-006 | Headless cannot inject touch gestures (swipe, pinch, long press) | Headless | minor | Fixed in 26.11.6 by [Forms PR #485](https://github.com/majorsilence/Majorsilence.Forms/pull/485); `BookPageView` swipe test added |
| GAP-007 | No UI `SynchronizationContext` on the Headless backend | Headless | minor | Fixed in 26.11.6 by [Forms PR #486](https://github.com/majorsilence/Majorsilence.Forms/pull/486); Recital's workaround removed |
| GAP-008 | `ToolStripDropDownButton` text wraps: measured with 6px less arrow room than drawn | all | minor | Fixed in 26.11.6 by [Forms PR #480](https://github.com/majorsilence/Majorsilence.Forms/pull/480); Recital's workaround removed |
| GAP-009 | Toolbar item text ignores `ToolStripItem.ForeColor` and `ToolStrip.ForeColor` | all | major (dark themes) | Fixed in 26.11.6 by [Forms PR #481](https://github.com/majorsilence/Majorsilence.Forms/pull/481); Recital's workaround removed |
| GAP-010 | `SplitContainer.Panel1Collapsed` leaves the splitter bar showing | all | minor | Fixed in 26.11.6 by [Forms PR #482](https://github.com/majorsilence/Majorsilence.Forms/pull/482); Recital's workaround removed |
| GAP-011 | `Application.SetDefaultFont` does not change the typeface controls draw with | all | minor | Fixed in 26.11.6 by [Forms PR #483](https://github.com/majorsilence/Majorsilence.Forms/pull/483); Recital's workaround removed |
| GAP-012 | `SaveFileDialog` returns a path only; mobile and browser save pickers give nothing writable | Avalonia Android, iOS, Browser | major (export on mobile/browser) | Open; Recital offers Copy as Markdown everywhere and file export on the desktop |

---

## GAP-001: `Majorsilence.Forms.Avalonia` on nuget.org has no `net10.0-ios` asset

- **Found:** Phase 1, setting up `hosts/Majorsilence.Recital.iOS`, framework 26.11.4.
- **Expected:** The package ships the same `net10.0-ios` row the source project builds with
  `EnableIOSHead`, as it does for `net10.0-android` and `net10.0-browser`. `Application.RunIOS` and
  `IosAudioBackend` are documented and used by `samples/Gallery.iOS`.
- **Actual:** The 26.11.4 nuspec lists `net8.0`, `net10.0`, `net10.0-android36.0` and
  `net10.0-browser1.0` only. `publish-nuget.yml` and `release.yml` pack `Majorsilence.Forms.Avalonia` on
  Linux in the `PACKABLE_PROJECTS` loop with `-p:EnableAndroidTarget=true`, and the iOS row can only build on
  macOS. On `net10.0-ios`, NuGet falls back to the plain `net10.0` desktop asset (and its
  `Avalonia.Desktop` dependency), so none of the iOS-specific backend code is present.
- **Repro:**
  `curl -s https://api.nuget.org/v3-flatcontainer/majorsilence.forms.avalonia/26.11.4/majorsilence.forms.avalonia.nuspec | grep targetFramework`
- **Severity:** blocker for the iOS head (plan phases 4, 8 and 10). Phase 1 only needs it to build.
- **Workaround:** none was possible. The iOS head built against the fallback asset until Recital moved to 26.11.5.
- **Fix:** [Majorsilence.Forms PR #478](https://github.com/majorsilence/Majorsilence.Forms/pull/478) (branch `fix/avalonia-ios-package`). It packs `Majorsilence.Forms.Avalonia` in the
  macOS job with both mobile workloads, the way `Majorsilence.Forms.Essentials` already is.

## GAP-002: No pixel-diff/baseline helper for Headless renders

- **Found:** Phase 1, `Majorsilence.Recital.UI.Tests`.
- **Expected:** Plan 0.3 calls the Headless backend "pixel-diff capable". `HeadlessRenderer.CapturePng`
  provides the render, but comparing it against a committed baseline, with a tolerance for cross-OS
  anti-aliasing, is left to each app.
- **Actual:** Majorsilence.Forms' own tests assert on pixels ad hoc. There is no shared helper.
- **Severity:** scope. Should every consumer write its own, or should `Majorsilence.Forms.Headless`
  offer `AssertMatchesBaseline`?
- **Workaround:** `tests/Majorsilence.Recital.UI.Tests/PixelBaseline.cs` (about 70 lines, SkiaSharp).
- **Fix:** [Forms PR #489](https://github.com/majorsilence/Majorsilence.Forms/pull/489) adds `Majorsilence.Forms.Headless.PixelComparison` (tolerant compare, diff image).
- **Resolved:** Recital pins 26.11.6; `PixelBaseline` compares with `PixelComparison` and writes its diff image.

## GAP-003: Headless renders depend on the host OS

- **Found:** Phase 1, the first CI run of `Majorsilence.Recital.UI.Tests`, on framework 26.11.4.
- **Expected:** An offscreen test backend whose output depends only on the code under test, so one
  machine can produce a baseline that another machine reproduces.
- **Actual:** Two kinds of host dependence:
  1. *Chrome.* `Form`'s constructor sets `UseSystemDecorations = true` on macOS, so a Headless render
     there has no title bar while Windows and Linux draw the custom one. The CI macOS render of an
     empty `MainForm` is a blank grey rectangle.
  2. *Fonts.* `Theme`'s default UI font is `"Segoe UI Emoji"` on Windows and `"sans-serif"` elsewhere, resolved
     through the machine's font manager. `FontSubstitution.ResolveWhereNoSystemFonts` only uses the embedded
     Noto/Liberation faces when the platform has *no* fonts. The CI Ubuntu 24.04 image and a local
     Ubuntu 26.04 machine rendered the same title 319 pixels apart.
- **Repro:** `HeadlessRenderer.Use (); HeadlessRenderer.CapturePng (new Form { Text = "Recital" }, 1000, 700);`
  on macOS vs Linux, or on two Linux machines with different fonts installed.
- **Severity:** major for pixel tests. Without a workaround, every baseline is tied to one machine image.
- **Workaround:** `tests/Majorsilence.Recital.UI.Tests/HeadlessTestEnvironment.cs` sets
  `MF_FORCE_CUSTOM_CHROME=1`, the switch Majorsilence.Forms' own CI uses. It also registers the Noto Sans faces
  that `Majorsilence.Forms.Drawing.Common` embeds as the UI font (see GAP-004), finding them by manifest
  resource name, which is not a public contract. With both in place, a local Ubuntu 26.04 render matches
  the CI Ubuntu 24.04 baseline. Baselines are still per OS, because each OS's Skia anti-aliases the same
  font differently (about 200 pixels on the title); that is expected, not a gap.
- **Fix:** [Forms PR #488](https://github.com/majorsilence/Majorsilence.Forms/pull/488) adds `HeadlessRenderer.UseDeterministic ()`: custom chrome everywhere and
  the bundled Noto Sans as UI, bold and default font, restored on dispose. It builds on #483 (GAP-011).

- **Phase 4 addition:** controls that draw with `Control.Font` (the tree view, owner-drawn list tiles)
  use `SystemFonts.DefaultFont`, which also resolves to the machine's own sans-serif; `HeadlessTestEnvironment`
  pins it to the embedded Noto Sans with `Application.SetDefaultFont`, and pins the culture to en-US.
- **Resolved:** Recital pins 26.11.6; `HeadlessTestEnvironment` calls `HeadlessRenderer.UseDeterministic ()`.

## GAP-004: `Theme.UIFont` honours only the typeface's family name

- **Found:** Phase 1, reviewing CI renders after the GAP-003 workaround.
- **Expected:** `Theme.UIFont = someTypeface` makes controls draw with that typeface, as the property's
  type (`SKTypeface`) suggests.
- **Actual:** Text layout goes through `CachingFontMapper`, which resolves a face from the style's
  family *name*: first `PrivateFontRegistry`, then the platform font manager. A typeface created with
  `SKTypeface.FromData`/`FromStream` is never used directly. Where its family is not installed, text
  silently renders in the system UI font: Helvetica on the macOS runner, Segoe UI on Windows.
- **Repro:** On Windows or macOS without Noto Sans installed,
  `Theme.UIFont = SKTypeface.FromStream (notoSansStream);` then render a `Form` with a title. The
  title is not Noto Sans.
- **Severity:** major. An app bundling its own UI font (Recital will, for consistent typography across
  devices) has to know to go through `PrivateFontCollection` as well.
- **Workaround:** register the same font bytes with `PrivateFontCollection.AddMemoryFont` before
  assigning `Theme.UIFont`, which is what WinForms code that bundles a font does anyway.
- **Fix:** [Forms PR #487](https://github.com/majorsilence/Majorsilence.Forms/pull/487): the `UIFont`/`UIFontBold` setters register a typeface the font manager
  cannot find by name with `PrivateFontRegistry`, and unregister it when replaced.
- **Resolved:** Recital pins 26.11.6; the `PrivateFontCollection` registration went with GAP-003's workaround.

## GAP-005: File dialogs return paths only

- **Found:** Phase 4, library import, framework 26.11.4.
- **Expected:** a way to read the file the user picked on every backend. WinForms is path-based, but
  `OpenFileDialog.OpenFile ()` exists to hand back a stream, and on Android (content URIs), iOS
  (security-scoped URLs) and the browser (no file system) a stream is the only thing that works.
- **Actual:** `IWindowBackend.ShowOpenFileDialog` returns `string[]`. The Avalonia backend maps each
  `IStorageFile` with `GetFullPath ()`, which is `file.Path.LocalPath`: for a `content://` URI that is
  a path nothing can open, and in the browser there is none. `OpenFileDialog.OpenFile ()` is
  `File.OpenRead (FileName)`.
- **Repro:** on Android, `new OpenFileDialog ().ShowDialogAsync ()`, pick a file from Downloads, then
  `File.OpenRead (dialog.FileName)` throws.
- **Severity:** blocker for importing on Android, iOS and the browser (Phase 10 needs it); none on desktop.
- **Workaround:** none yet; `LibraryView.ImportFilesAsync` is marked. Desktop import works.
- **Fix:** [Forms PR #484](https://github.com/majorsilence/Majorsilence.Forms/pull/484): an optional `IPickedFileBackend` returns `PickedFile`s (name, path when
  there is one, `OpenReadAsync`); the Avalonia hosts and Headless implement it, and `OpenFileDialog` gains
  `SelectedFiles` and `OpenFileAsync`. Once released, `LibraryView.ImportFilesAsync` reads through them.
- **Resolved:** Recital pins 26.11.6; `LibraryView.ImportFilesAsync` takes `PickedFile`s and the picker passes `OpenFileDialog.SelectedFiles`; `ImportsAFileThePickerGivesNoPath` covers a pick with no path.

## GAP-006: Headless cannot inject touch gestures

- **Found:** Phase 4, `BookPageView` swipe handling.
- **Expected:** `HeadlessRenderer` can raise every input the neutral pipeline has, so a control's
  gesture handling can be tested.
- **Actual:** `WindowBase.HandleSwipe`, `HandlePinch`, `HandleLongPress` and `HandleScrollGesture` are
  internal, and `HeadlessRenderer` has mouse, wheel, key and text injection only.
- **Severity:** minor. `BookPageView.OnSwipe` is untested; the mouse-drag path, which is also what a
  touch drag arrives as, is tested.
- **Fix:** [Forms PR #485](https://github.com/majorsilence/Majorsilence.Forms/pull/485) adds `HeadlessRenderer.Swipe/Pinch/LongPress/ScrollGesture`.
- **Resolved:** Recital pins 26.11.6; `ReaderTests.SwipingTurnsPagesOnAPhone`.

## GAP-007: No UI `SynchronizationContext` on the Headless backend

- **Found:** Phase 4, UI tests.
- **Expected:** as on Avalonia (and in WinForms), an `await` in an event handler resumes on the UI thread.
- **Actual:** nothing installs `WindowsFormsSynchronizationContext` on Headless (its `AutoInstall` is
  stored and read by nothing), so continuations ran on the thread pool and touched controls there.
- **Severity:** minor (tests only).
- **Workaround:** `UiTest` installs the framework's own `WindowsFormsSynchronizationContext` and pumps
  `Application.DoEvents`.
- **Fix:** [Forms PR #486](https://github.com/majorsilence/Majorsilence.Forms/pull/486): the headless main loop installs the context (honouring `AutoInstall`),
  and `HeadlessRenderer.UseSynchronizationContext ()` / `PumpUntil` replace this workaround in tests.
- **Resolved:** Recital pins 26.11.6; `UiTest` uses `HeadlessRenderer.UseSynchronizationContext ()`.

## GAP-008: `ToolStripDropDownButton` text wraps onto two lines

- **Found:** Phase 4, the library's "Sort: Recent" and the reader's "Theme" buttons.
- **Actual:** `ToolBarRenderer.GetPreferredItemSize` added 14px for a drop-down item's arrow, but
  `RenderItem` takes 16 + 4 = 20px out of the content box for it, so the text got 6px less than it was
  measured at and wrapped. `ToolStripItem.AutoSize` is stored-only, so an explicit width is not a way out.
- **Workaround:** Recital's toolbar renderer (`ThemePainter`) widens the text rectangle of drop-down items by 6px.
- **Fix:** [Forms PR #480](https://github.com/majorsilence/Majorsilence.Forms/pull/480): both sides use one
  `DropDownArrowGutter`, with a regression test.
- **Resolved:** Recital pins 26.11.6; `ThemePainter`'s toolbar renderer is gone (with GAP-009).

## GAP-009: Toolbar item text ignores `ForeColor`

- **Found:** Phase 4, dark reading themes.
- **Actual:** `ToolStripItem.ForeColor` and `ToolStrip.ForeColor` are documented as "read by the strip
  renderers", but `ToolBarRenderer.RenderItem` takes the text colour from the built-in item style unless
  a `ToolStripRenderer` is installed, so a dark strip drew near-black text.
- **Workaround:** `ThemePainter` installs a `ToolStripProfessionalRenderer` subclass whose
  `OnRenderItemText` passes the item's `ForeColor` through.
- **Fix:** [Forms PR #481](https://github.com/majorsilence/Majorsilence.Forms/pull/481), with tests.
- **Resolved:** Recital pins 26.11.6; `ThemePainter` no longer installs a renderer; item `ForeColor` is enough.

## GAP-010: `SplitContainer.Panel1Collapsed` leaves the splitter showing

- **Found:** Phase 4, the reader with its side panel closed.
- **Actual:** collapsing a panel set only `Panel.Visible`; the 5px `Splitter` stayed docked at the edge,
  so the other panel never got the whole container. WinForms hides the splitter too.
- **Workaround:** `ReaderView` moves the page out of the split and hides the split when no side panel is open.
- **Fix:** [Forms PR #482](https://github.com/majorsilence/Majorsilence.Forms/pull/482), with a test.
- **Resolved:** Recital pins 26.11.6; `ReaderView` keeps the page in the split and collapses `Panel1`.

## GAP-011: `Application.SetDefaultFont` does not reach drawing

- **Found:** Phase 4, CI's Linux render of the contents tree differed from a local one.
- **Actual:** `SetDefaultFont` changes what `Control.Font` reports, but `Control.GetEffectiveFont` — the
  typeface `TreeView`, `ListView` and most renderers draw with — falls back to `SystemFonts.DefaultTypeface`,
  a fixed lookup of the system face that ignores the override.
- **Severity:** minor for Recital (tests only); an app-wide font is not honoured in a real app either.
- **Workaround:** `HeadlessTestEnvironment.PinFont` sets the window style's typeface.
- **Fix:** [Forms PR #483](https://github.com/majorsilence/Majorsilence.Forms/pull/483): `DefaultTypeface`
  returns the override's face when there is one, with a test.
- **Resolved:** Recital pins 26.11.6; `PinFont` is gone; `UseDeterministic` sets the default font.

## GAP-012: `SaveFileDialog` returns a path only

- **Found:** Phase 6, exporting highlights and notes to Markdown, framework 26.11.6.
- **Expected:** a way to write the file the user chose on every backend, as GAP-005's `PickedFile` now
  gives for opening. On Android (content URIs), iOS (security-scoped URLs) and the browser (a download
  or a File System Access handle) a writable stream from the picker is the only thing that works.
- **Actual:** `IWindowBackend.ShowSaveFileDialog` returns `string?`; `SaveFileDialog.OpenFile ()` is
  `File.Create (FileName)`. Avalonia's save picker returns an `IStorageFile` with `OpenWriteAsync`, which
  the backend reduces to a local path.
- **Severity:** major for exporting on phones and in the browser; none on desktop.
- **Workaround:** the Notes panel shows Export… (save as a file) only on the desktop, and Copy all
  (Markdown to the clipboard) everywhere. Marked `FRAMEWORK-GAP: GAP-012` in `ReaderView`.
- **Proposed fix:** mirror GAP-005: an optional `IPickedSaveFileBackend` returning a `PickedSaveFile`
  (name, path when there is one, `OpenWriteAsync`), `SaveFileDialog.SelectedFile` and `SaveFileAsync`,
  and a Headless `SaveFilePickResponse`.


# Majorsilence.Forms coverage

Every Majorsilence.Forms control and API Recital uses, which backends it was exercised on, and whether it
behaved. Regenerated at the end of each phase (plan section 6). Kept accurate, not flattering.

Legend: ✅ works · ⚠️ works with a gap (see link) · ❌ broken · — not exercised yet

Backends: **Desktop** = Avalonia on Windows/macOS/Linux, **Android** / **iOS** / **Browser** = Avalonia
heads, **Headless** = offscreen Skia (UI tests).

| API | Desktop | Android | iOS | Browser | Headless | Gaps |
|---|---|---|---|---|---|---|
| `Form` (Text, ClientSize, Show, Load, FormClosing with Cancel, FormClosed) | ✅ Linux (starts; not checked by eye) | ✅ emulator (Android 16) | — (builds in CI) | ✅ Brave | ✅ | |
| `Application.Run` | ✅ Linux (starts and initialises the library) | | | | | |
| `Application.RunAndroid` | | ✅ emulator | | | | |
| `Application.RunIOS` | | | — (builds in CI; not run) | | | [GAP-001](FRAMEWORK_GAPS.md#gap-001-majorsilenceformsavalonia-on-nugetorg-has-no-net100-ios-asset) (fixed) |
| `Application.RunBrowserAsync` | | | | ✅ Brave (by hand) and headless Brave (starts in about 1.3 s) | | [GAP-014](FRAMEWORK_GAPS.md#gap-014-the-browser-head-draws-nothing-in-headless-firefox-and-blocks-the-main-thread-at-start-up) (headless Firefox only) |
| `AvaloniaPlatformBackend.RaiseBackRequested` / `RegisterAndroidActivity` / `ReportAndroidIntent` | | ✅ emulator (activity registered, intents forwarded; Back not exercised) | | | | |
| `HeadlessRenderer.Use` / `UseDeterministic` / `UseSynchronizationContext` / `CapturePng` / `Click` / `MouseDown/Move/Up` / `MouseWheel` / `KeyDown` / `Swipe` | | | | | ✅ | [GAP-003](FRAMEWORK_GAPS.md#gap-003-headless-renders-depend-on-the-host-os), [GAP-006](FRAMEWORK_GAPS.md#gap-006-headless-cannot-inject-touch-gestures), [GAP-007](FRAMEWORK_GAPS.md#gap-007-no-ui-synchronizationcontext-on-the-headless-backend) (all fixed) |
| `HeadlessRenderer.OpenFileResponse` / `OpenFilePickResponse` / `OpenFolderResponse` | | | | | ✅ | |
| `PixelComparison.Compare` / `IsWithin` / `CreateDiffPng` | | | | | ✅ | [GAP-002](FRAMEWORK_GAPS.md#gap-002-no-pixel-diffbaseline-helper-for-headless-renders) (fixed) |
| Custom `Control` (`BookPageView`): `OnPaint` canvas in logical units, `IsInputKey`, `OnKeyDown`, `OnMouseDown/Up`, `OnMouseWheel`, `OnSwipe`, `Focus`, `AccessibleRole/Description` | — | ✅ emulator (paints, taps turn pages and show the bars) | — | ✅ Brave | ✅ | |
| `Control.BeginInvoke` from a worker thread (sync rounds) | — | — | — | — | ✅ | |
| `UserControl`, `Panel`, `Label`, `Dock`, `SetBounds`, `BringToFront`, re-parenting via `Controls.Add` | — | — | — | — | ✅ | |
| Ambient `BackColor` / `ForeColor` from the form | — | — | — | — | ✅ (inputs and toolbars are not ambient, as in WinForms) | |
| `ToolStrip`, `ToolStripButton` (`Checked`), `ToolStripLabel`, `ToolStripSeparator`, `ToolStripTextBox`, item `ForeColor`, `Alignment = Right` | — | ⚠️ captions wrap at 2.625 on 26.11.6 | — | ✅ Brave | ✅ | [GAP-009](FRAMEWORK_GAPS.md#gap-009-toolbar-item-text-ignores-forecolor) (fixed), [GAP-016](FRAMEWORK_GAPS.md#gap-016-tool-strip-captions-wrap-their-last-letter-at-fractional-scales) (Forms PR #510) |
| `ToolStripDropDownButton` + `ToolStripMenuItem` (`Checked`, `Enabled`), `DefaultDropDownDirection` | — | ❌ on 26.11.6: no drop-down is seen (off screen); ✅ with Forms PRs #509/#510 built locally, ⚠️ a reopened one can be cut short | — | ✅ Brave | ✅ | [GAP-008](FRAMEWORK_GAPS.md#gap-008-toolstripdropdownbutton-text-wraps-onto-two-lines) (fixed), [GAP-015](FRAMEWORK_GAPS.md#gap-015-tool-strip-drop-down-menus-never-appear-on-android) (Forms PR #509), [GAP-017](FRAMEWORK_GAPS.md#gap-017-a-reopened-drop-down-on-android-can-be-only-as-wide-as-its-button) (Forms PR #511) |
| `ToolBar.DefaultItemHoverStyle` (themed hover/checked background) | — | — | — | — | ✅ | |
| `StatusStrip`, `ToolStripStatusLabel` (`Spring`, `TextAlign`) | — | ✅ emulator | — | ✅ Brave | ✅ | |
| `ListView`: `LargeIcon` with `OwnerDraw`/`DrawItem` (device pixels), `TileSize`, `Details` columns, `ItemActivate` (double-click; `Activation = OneClick` on phones), `ContextMenuStrip`, `GetItemRect` | — | ✅ emulator (one tap opens a book) | — | ✅ Brave | ✅ | |
| `ContextMenuStrip` | — | — | — | — | — (built, not opened in a test) | |
| `TreeView`: `Nodes`, `TreeNode.Tag`, `ExpandAll`, `NodeMouseClick`, `SelectedNode`, `EnsureVisible`, `TreeNode.Bounds` | — | — | — | — | ✅ | |
| `SplitContainer` (`FixedPanel`, `SplitterDistance`, `Panel1Collapsed`, `SplitterColor`) | — | — | — | — | ✅ | [GAP-010](FRAMEWORK_GAPS.md#gap-010-splitcontainerpanel1collapsed-leaves-the-splitter-showing) (fixed) |
| `FlowLayoutPanel` (TopDown, no wrap), `ComboBox` (DropDownList), `NumericUpDown`, `CheckBox` | — | — | — | — | ✅ | |
| `OpenFileDialog.ShowDialogAsync` (Multiselect, Filter), `SelectedFiles`, `PickedFile.OpenReadAsync`, `new PickedFile (name, path, open)` | — | ✅ emulator (system picker; files from other apps) | — | ✅ Brave | ✅ | [GAP-005](FRAMEWORK_GAPS.md#gap-005-file-dialogs-return-paths-only) (fixed) |
| `FolderBrowserDialog.ShowDialogAsync` (`Description`, `ShowNewFolderButton`, `SelectedPath`) | — | | | | ✅ | |
| `Control.OnMouseMove` (drag selection), `OnLongPress` | — | — | — | — | ✅ | |
| `SaveFileDialog.ShowDialogAsync` (`Filter`, `DefaultExt`, `FileName`) | — | | | | ✅ (Headless `SaveFileResponse`) | [GAP-012](FRAMEWORK_GAPS.md#gap-012-savefiledialog-returns-a-path-only) |
| `Clipboard.SetTextAsync` | — | — | — | — | — (called, not checked) | |
| `TextBox` (`Multiline`, `AcceptsReturn`, `WordWrap`, `PlaceholderText`), `Button` | — | — | — | — | ✅ | |
| `TreeNode.ToolTipText`, `TreeView.ContextMenuStrip` | — | — | — | — | — (built, not opened in a test) | |
| `TextBox.UseSystemPasswordChar`, `Label.MaximumSize` (wrapping), the on-screen keyboard | — | ✅ emulator (WebDAV form typed through the keyboard) | | | ✅ | |
| `ListView` `Details` with `MultiSelect`, `SelectedIndexChanged` (import browser) | — | — | — | — | ✅ | |
| `ToolStripMenuItem` `Enabled`/`Checked`/`ToolTipText` per state (Sync menu) | — | — | — | — | ✅ | |
| `Control.BeginInvoke` from engine threads (speech progress) | — | — | — | — | ✅ | |
| `Majorsilence.Forms.Media.AudioPlayer` / `SoundPlayer` | not used | | | | | [GAP-013](FRAMEWORK_GAPS.md#gap-013-no-streaming-audio-output-on-the-desktop-backends) |
| Drag-and-drop file import (`AllowDrop`, `DragEnter`, `DragDrop`, `DataFormats.FileDrop`) | — | — | — | — | — | |
| `Majorsilence.Forms.Timer` (progress-save debounce) | — | — | — | — | ✅ (implicitly: saves happen) | |
| `MessageBox.ShowAsync` (OK/Cancel confirmations: links, the voice download) | — | ❌ on 26.11.6: at the top left and off the right edge; ✅ with Forms PR #509 built locally | — | — | ✅ | [GAP-015](FRAMEWORK_GAPS.md#gap-015-tool-strip-drop-down-menus-never-appear-on-android) (same PR) |
| `Screen.FromControl` / `AllScreens` on a single view | | ❌ on 26.11.6: no screen; ✅ with Forms PR #509 | — | — | | |
| `Application.UiScale` / device scaling of measured text | ✅ | ⚠️ 2.625: GAP-016 | — | ✅ at 1 | ✅ | [GAP-016](FRAMEWORK_GAPS.md#gap-016-tool-strip-captions-wrap-their-last-letter-at-fractional-scales) |
| `Graphics` in owner draw: `FillRectangle`, `DrawImage`, `DrawString` with `StringFormat`, `Bitmap (Stream)` | — | — | — | — | ✅ | |

Phase 1 status: every host entry point compiles in CI (Desktop on all three OSes, Android, iOS
simulator, Browser publish: 47 MB wwwroot). Only the Linux desktop host has been run. On-device
checks happen in Phase 4, when there is a UI worth running.

Phases 2 and 3 used no Majorsilence.Forms APIs (EPUB parsing and the layout engine are UI-free by design), so this table is unchanged.

Phase 4: every Headless row above is covered by `Majorsilence.Recital.UI.Tests` (12 tests, four pixel
baselines). On the desktop host the app has been started on Linux (Wayland) and runs, initialising the
library; nobody has yet clicked through it by eye on any OS, so the Desktop column says "—" for the
controls. The Android, iOS and browser heads build but have not been run with the reader UI.

Since 26.11.6 Recital no longer uses `Theme.UIFont`, `PrivateFontCollection`, `Application.SetDefaultFont`,
`MF_FORCE_CUSTOM_CHROME` or a custom `ToolStripProfessionalRenderer`: `HeadlessRenderer.UseDeterministic`
and the framework's own fixes replaced them (GAP-003, GAP-004, GAP-008, GAP-009, GAP-011).

Phase 5: the sync menu (a right-aligned `ToolStripDropDownButton`), `FolderBrowserDialog` (Headless
`OpenFolderResponse` drives it in `SyncTests`) and `Control.BeginInvoke` from the sync loop's thread
are new. The menu is shown only on the desktop, so the folder dialog has no mobile or browser column.
No new gaps.

Phase 6: selection gestures (`OnMouseMove` after a hold, `OnLongPress`), the floating highlight bar
(a `ToolStrip` placed over the page), the note editor (multiline `TextBox`), the Notes panel
(`TreeView`), `SaveFileDialog` for Markdown export and `Clipboard.SetTextAsync`. One new gap: GAP-012,
saves on phones and in the browser.

Phase 7: the Sync menu grew one item per kind of sync home. The WebDAV form (password `TextBox`) and
the import browser (`ListView` with multi-select) are in-window panels; Headless tests drive both. No
new gaps.

Phase 8: the reading-aloud bar (a bottom `ToolStrip` with drop-downs for speed, voice and sleep) and
speech highlighting (marks under the text, from `BeginInvoke` on engine threads). The framework's audio
classes were evaluated and not used: GAP-013.

Phase 9: the Engine menu (a `ToolStripDropDownButton` rebuilt as engines come and go) and the voice
settings panel (password `TextBox`es with placeholders). No new gaps.

Phase 10: the first phase run on Android (an Android 16 emulator, Pixel 7) and in a real browser (Brave,
and headless Brave for logs and timings). iOS still only builds in CI.
- **What works on Android:**
  - reading, page turns and import from the system picker;
  - one tap opening a book;
  - the on-screen keyboard in forms;
  - books opened from other apps through `new PickedFile (name, path, open)`;
  - reading aloud, with the framework's bars and panels around it.
- **What failed on the pinned 26.11.6, and was fixed in Majorsilence.Forms:**
  - every drop-down and dialog was placed in device pixels as if they were layout units, so it was drawn
    off the screen;
  - a single view reported no screen, so dialogs could neither centre nor fit;
  - measured captions lost a pixel at 2.625 and wrapped.

  PRs #509 and #510 fix these. With both built into local packages, the menus, the confirmation dialog and
  the captions were all checked on the emulator. Recital takes them with the next Forms release (it pins
  26.11.6).
- **GAP-017** (a menu rebuilt from its own item's Click came out too narrow) was traced to the same
  framework and fixed there too (PR #511).
- **Not used:** the framework's notifications. Reading aloud in the background uses an Android foreground
  service of Recital's own, since Forms has no service or media session API.

